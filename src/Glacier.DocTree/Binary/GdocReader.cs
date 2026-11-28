using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Glacier.DocTree.Core;
using Glacier.DocTree.GDoc;

namespace Glacier.DocTree.Binary
{
    public sealed unsafe class GdocReader : IDisposable
    {
        private const ushort MetadataBlobFlag = 0x8000;

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private byte* _basePointer;
        private readonly uint _nodeCount;
        private readonly GdocNodeEntry* _nodeTable;
        private readonly GdocNodeEntry[]? _projectedNodes;
        private readonly bool[]? _legacyMetadataBlobNodes;
        private readonly byte* _stringArena;
        private readonly ulong _stringArenaLength;
        private readonly GdocMetadataEntry* _metadataTable;
        private readonly uint _metadataCount;
        private int _disposed;

        public uint NodeCount
        {
            get
            {
                ThrowIfDisposed();
                return _nodeCount;
            }
        }

        public uint MetadataCount
        {
            get
            {
                ThrowIfDisposed();
                return _metadataCount;
            }
        }

        public GdocReader(string gdocFilePath)
        {
            if (string.IsNullOrEmpty(gdocFilePath))
            {
                throw new ArgumentException("File path cannot be null or empty.", nameof(gdocFilePath));
            }
            if (!File.Exists(gdocFilePath))
            {
                throw new FileNotFoundException($"The specified .gdoc file was not found: '{gdocFilePath}'", gdocFilePath);
            }

            long fileLength = new FileInfo(gdocFilePath).Length;
            if (fileLength < GdocWriter.HeaderSize)
            {
                throw new InvalidDataException("File is too short to be a valid .gdoc file.");
            }

            long payloadLength = fileLength - GdocWriter.HeaderSize;
            if (payloadLength > int.MaxValue)
            {
                throw new InvalidDataException($".gdoc payload exceeds maximum supported span length ({payloadLength} bytes).");
            }

            MemoryMappedFile? mmf = null;
            MemoryMappedViewAccessor? accessor = null;
            byte* basePtr = null;
            bool acquired = false;

            try
            {
                mmf = MemoryMappedFile.CreateFromFile(gdocFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                acquired = true;
            }
            catch
            {
                if (acquired && accessor != null)
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                accessor?.Dispose();
                mmf?.Dispose();
                throw;
            }

            _mmf = mmf;
            _accessor = accessor;
            _basePointer = basePtr;

            try
            {
                uint magic = *(uint*)_basePointer;
                if (magic == GdocWriter.GdocMagic)
                {
                    LoadLegacyV1(fileLength, payloadLength, out _nodeCount, out _nodeTable, out _stringArena, out _stringArenaLength, out _metadataTable, out _metadataCount);
                }
                else if (magic == GDocHeader.MagicValue)
                {
                    LoadUnifiedGDoc(fileLength, payloadLength, out _nodeCount, out _projectedNodes, out _legacyMetadataBlobNodes, out _stringArena, out _stringArenaLength, out _metadataTable, out _metadataCount);
                }
                else
                {
                    throw new InvalidDataException($"Invalid .gdoc file magic identifier: 0x{magic:X8}");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void LoadLegacyV1(
            long fileLength,
            long payloadLength,
            out uint nodeCount,
            out GdocNodeEntry* nodeTable,
            out byte* stringArena,
            out ulong stringArenaLength,
            out GdocMetadataEntry* metadataTable,
            out uint metadataCount)
        {
            ushort version = *(ushort*)(_basePointer + 4);
            if (version != GdocWriter.CurrentVersion)
            {
                throw new InvalidDataException($"Unsupported .gdoc file version: {version}");
            }

            uint expectedCrc = *(uint*)(_basePointer + 58);
            if (payloadLength > 0)
            {
                var payloadSpan = new ReadOnlySpan<byte>(_basePointer + GdocWriter.HeaderSize, (int)payloadLength);
                uint actualCrc = GdocCrc32.Compute(payloadSpan);
                if (actualCrc != expectedCrc)
                {
                    throw new InvalidDataException($"CRC32 mismatch in .gdoc file: expected 0x{expectedCrc:X8}, calculated 0x{actualCrc:X8}");
                }
            }
            else if (expectedCrc != 0)
            {
                throw new InvalidDataException($"CRC32 mismatch in .gdoc file: expected 0x{expectedCrc:X8}, calculated 0x00000000");
            }

            ulong uFileLength = (ulong)fileLength;
            nodeCount = *(uint*)(_basePointer + 6);
            ulong stringArenaOffset = *(ulong*)(_basePointer + 10);
            stringArenaLength = *(ulong*)(_basePointer + 18);
            ulong nodeTableOffset = *(ulong*)(_basePointer + 26);
            ulong nodeTableLength = *(ulong*)(_basePointer + 34);
            ulong metadataTableOffset = *(ulong*)(_basePointer + 42);
            ulong metadataTableLength = *(ulong*)(_basePointer + 50);

            ulong expectedNodeTableLength = (ulong)nodeCount * 32UL;
            if (nodeTableLength != expectedNodeTableLength ||
                nodeTableOffset < GdocWriter.HeaderSize ||
                nodeTableOffset > uFileLength ||
                nodeTableLength > uFileLength - nodeTableOffset)
            {
                throw new InvalidDataException("Invalid .gdoc node table bounds in header.");
            }

            if (stringArenaOffset < GdocWriter.HeaderSize ||
                stringArenaOffset > uFileLength ||
                stringArenaLength > uFileLength - stringArenaOffset)
            {
                throw new InvalidDataException("Invalid .gdoc string arena bounds in header.");
            }

            if ((metadataTableLength & 15UL) != 0 ||
                metadataTableOffset < GdocWriter.HeaderSize ||
                metadataTableOffset > uFileLength ||
                metadataTableLength > uFileLength - metadataTableOffset ||
                metadataTableLength / 16UL > uint.MaxValue)
            {
                throw new InvalidDataException("Invalid .gdoc metadata table bounds in header.");
            }

            metadataCount = (uint)(metadataTableLength / 16UL);
            nodeTable = (GdocNodeEntry*)(_basePointer + nodeTableOffset);
            stringArena = _basePointer + stringArenaOffset;
            metadataTable = (GdocMetadataEntry*)(_basePointer + metadataTableOffset);
        }

        private void LoadUnifiedGDoc(
            long fileLength,
            long payloadLength,
            out uint nodeCount,
            out GdocNodeEntry[] projectedNodes,
            out bool[]? legacyMetadataBlobNodes,
            out byte* stringArena,
            out ulong stringArenaLength,
            out GdocMetadataEntry* metadataTable,
            out uint metadataCount)
        {
            GDocHeader header = Unsafe.Read<GDocHeader>(_basePointer);
            ulong totalLen = (ulong)fileLength;
            bool isV2 = header.Version == GDocHeader.CurrentVersion;

            if (isV2)
            {
                if (payloadLength > 0)
                {
                    var payloadSpan = new ReadOnlySpan<byte>(_basePointer + GDocHeader.HeaderSize, (int)payloadLength);
                    uint actualCrc = GdocCrc32.Compute(payloadSpan);
                    if (actualCrc != header.PayloadCrc32)
                    {
                        throw new InvalidDataException($"CRC32 mismatch in .gdoc file: expected 0x{header.PayloadCrc32:X8}, calculated 0x{actualCrc:X8}");
                    }
                }
                else if (header.PayloadCrc32 != 0)
                {
                    throw new InvalidDataException($"CRC32 mismatch in .gdoc file: expected 0x{header.PayloadCrc32:X8}, calculated 0x00000000");
                }
            }
            else if (header.Version != GDocHeader.LegacyVersion1)
            {
                throw new InvalidDataException($"Unsupported .gdoc file version: {header.Version}");
            }

            if ((header.StringPoolLength > 0 || header.StringPoolOffset != 0) &&
                header.StringPoolOffset < GDocHeader.HeaderSize)
            {
                throw new InvalidDataException("Invalid .gdoc string arena bounds in header.");
            }
            if (isV2 && (header.StringPoolOffset & 7u) != 0)
            {
                throw new InvalidDataException("Invalid .gdoc string arena alignment in header.");
            }
            if (header.StringPoolOffset > totalLen ||
                (ulong)header.StringPoolOffset + header.StringPoolLength > totalLen)
            {
                throw new InvalidDataException("Invalid .gdoc string arena bounds in header.");
            }

            uint expectedNodeSize = isV2
                ? (uint)sizeof(GDocAstNode)
                : (uint)sizeof(GDocAstNodeV1);

            if ((header.AstNodeCount > 0 || header.AstTableOffset != 0) &&
                header.AstTableOffset < GDocHeader.HeaderSize)
            {
                throw new InvalidDataException("Invalid .gdoc node table bounds in header.");
            }
            if (isV2 && (header.AstTableOffset & 7u) != 0)
            {
                throw new InvalidDataException("Invalid .gdoc node table alignment in header.");
            }
            if (header.AstTableOffset > totalLen)
            {
                throw new InvalidDataException("Invalid .gdoc node table bounds in header.");
            }
            if (header.AstNodeCount > 0)
            {
                if (header.AstNodeSize != expectedNodeSize)
                {
                    throw new InvalidDataException("Invalid .gdoc node table bounds in header.");
                }
                ulong astEnd = checked((ulong)header.AstTableOffset + (ulong)header.AstNodeCount * header.AstNodeSize);
                if (astEnd > totalLen)
                {
                    throw new InvalidDataException("Invalid .gdoc node table bounds in header.");
                }
            }

            if ((header.TokenSpanCount > 0 || header.TokenSpanOffset != 0) &&
                header.TokenSpanOffset < GDocHeader.HeaderSize)
            {
                throw new InvalidDataException("Invalid .gdoc token span table bounds in header.");
            }
            if (isV2 && (header.TokenSpanOffset & 7u) != 0)
            {
                throw new InvalidDataException("Invalid .gdoc token span table alignment in header.");
            }
            if (header.TokenSpanOffset > totalLen)
            {
                throw new InvalidDataException("Invalid .gdoc token span table bounds in header.");
            }
            if (header.TokenSpanCount > 0)
            {
                if (header.TokenSpanSize != GDocTokenSpan.SpanSize)
                {
                    throw new InvalidDataException("Invalid .gdoc token span table bounds in header.");
                }
                ulong spanEnd = checked((ulong)header.TokenSpanOffset + (ulong)header.TokenSpanCount * header.TokenSpanSize);
                if (spanEnd > totalLen)
                {
                    throw new InvalidDataException("Invalid .gdoc token span table bounds in header.");
                }
            }

            if ((header.VectorCount > 0 || header.VectorOffset != 0) &&
                header.VectorOffset < GDocHeader.HeaderSize)
            {
                throw new InvalidDataException("Invalid .gdoc vector table bounds in header.");
            }
            if (isV2 && (header.VectorOffset & 7u) != 0)
            {
                throw new InvalidDataException("Invalid .gdoc vector table alignment in header.");
            }
            if (header.VectorOffset > totalLen)
            {
                throw new InvalidDataException("Invalid .gdoc vector table bounds in header.");
            }
            if (header.VectorCount > 0)
            {
                if (header.VectorDim == 0)
                {
                    throw new InvalidDataException("Invalid .gdoc vector dimension in header.");
                }
                ulong vecEnd = checked((ulong)header.VectorOffset + (ulong)header.VectorCount * header.VectorDim * sizeof(float));
                if (vecEnd > totalLen)
                {
                    throw new InvalidDataException("Invalid .gdoc vector table bounds in header.");
                }
            }

            metadataCount = isV2 ? header.MetadataCount : 0u;
            if (isV2 && header.MetadataTableOffset != 0)
            {
                if (header.MetadataTableOffset < GDocHeader.HeaderSize)
                {
                    throw new InvalidDataException("Invalid .gdoc metadata table bounds in header.");
                }
                if ((header.MetadataTableOffset & 7u) != 0)
                {
                    throw new InvalidDataException("Invalid .gdoc metadata table alignment in header.");
                }
                if (header.MetadataTableOffset > totalLen)
                {
                    throw new InvalidDataException("Invalid .gdoc metadata table bounds in header.");
                }
            }
            if (metadataCount > 0)
            {
                if (header.MetadataTableOffset < GDocHeader.HeaderSize)
                {
                    throw new InvalidDataException("Invalid .gdoc metadata table bounds in header.");
                }
                ulong metaEnd = checked((ulong)header.MetadataTableOffset + (ulong)metadataCount * 16UL);
                if (metaEnd > totalLen)
                {
                    throw new InvalidDataException("Invalid .gdoc metadata table bounds in header.");
                }
                metadataTable = (GdocMetadataEntry*)(_basePointer + header.MetadataTableOffset);
            }
            else
            {
                metadataTable = null;
            }

            nodeCount = header.AstNodeCount;
            stringArena = _basePointer + header.StringPoolOffset;
            stringArenaLength = header.StringPoolLength;
            projectedNodes = nodeCount == 0 ? Array.Empty<GdocNodeEntry>() : new GdocNodeEntry[nodeCount];
            legacyMetadataBlobNodes = null;

            // Precompute child counts for projected GdocNodeEntry
            Span<int> childCounts = nodeCount <= 1024 ? stackalloc int[(int)nodeCount] : new int[nodeCount];
            childCounts.Clear();

            if (isV2)
            {
                for (uint i = 0; i < nodeCount; i++)
                {
                    ref readonly var ast = ref Unsafe.AsRef<GDocAstNode>(_basePointer + header.AstTableOffset + ((ulong)i * header.AstNodeSize));
                    if (ast.ParentId != GDocAstNode.NoNode && ast.ParentId < nodeCount && ast.ParentId != i)
                    {
                        childCounts[(int)ast.ParentId]++;
                    }
                }

                for (uint i = 0; i < nodeCount; i++)
                {
                    ref readonly var ast = ref Unsafe.AsRef<GDocAstNode>(_basePointer + header.AstTableOffset + ((ulong)i * header.AstNodeSize));
                    NodeType coreType = GlacierStorageDocExtensions.ResolveNodeType(in ast);
                    byte flags = ast.MetadataCount > 0 ? (byte)0x01 : (byte)0;
                    ushort cCount = (ushort)Math.Min(childCounts[(int)i], ushort.MaxValue);
                    int parentIdx = ast.ParentId != GDocAstNode.NoNode && ast.ParentId < nodeCount ? (int)ast.ParentId : -1;
                    int firstChildIdx = ast.FirstChildId != GDocAstNode.NoNode && ast.FirstChildId < nodeCount ? (int)ast.FirstChildId : -1;
                    int nextSibIdx = ast.NextSiblingId != GDocAstNode.NoNode && ast.NextSiblingId < nodeCount ? (int)ast.NextSiblingId : -1;

                    projectedNodes[i] = new GdocNodeEntry(
                        (byte)coreType,
                        flags,
                        ast.TextOffset,
                        ast.TextLength,
                        parentIdx,
                        firstChildIdx,
                        nextSibIdx,
                        cCount,
                        ast.MetadataCount,
                        ast.MetadataIndex);
                }
            }
            else
            {
                for (uint i = 0; i < nodeCount; i++)
                {
                    ref readonly var ast = ref Unsafe.AsRef<GDocAstNodeV1>(_basePointer + header.AstTableOffset + ((ulong)i * header.AstNodeSize));
                    if ((ast.Flags & MetadataBlobFlag) != 0)
                    {
                        legacyMetadataBlobNodes ??= new bool[nodeCount];
                        legacyMetadataBlobNodes[i] = true;
                        continue;
                    }
                    if (ast.ParentId != GDocAstNode.NoNode && ast.ParentId < nodeCount && ast.ParentId != i)
                    {
                        childCounts[(int)ast.ParentId]++;
                    }
                }

                for (uint i = 0; i < nodeCount; i++)
                {
                    ref readonly var ast = ref Unsafe.AsRef<GDocAstNodeV1>(_basePointer + header.AstTableOffset + ((ulong)i * header.AstNodeSize));
                    NodeType coreType = GlacierStorageDocExtensions.FromGDocType((GDocNodeType)ast.NodeType, ast.Flags);
                    ushort cCount = (ushort)Math.Min(childCounts[(int)i], ushort.MaxValue);
                    int parentIdx = ast.ParentId != GDocAstNode.NoNode && ast.ParentId < nodeCount ? (int)ast.ParentId : -1;
                    int firstChildIdx = ast.FirstChildId != GDocAstNode.NoNode && ast.FirstChildId < nodeCount ? (int)ast.FirstChildId : -1;
                    int nextSibIdx = ast.NextSiblingId != GDocAstNode.NoNode && ast.NextSiblingId < nodeCount ? (int)ast.NextSiblingId : -1;

                    projectedNodes[i] = new GdocNodeEntry(
                        (byte)coreType,
                        0,
                        ast.TextOffset,
                        ast.TextLength,
                        parentIdx,
                        firstChildIdx,
                        nextSibIdx,
                        cCount,
                        0,
                        -1);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        public List<int> GetChildIndices(int nodeIndex)
        {
            ThrowIfDisposed();
            var result = new List<int>();
            var visited = new HashSet<int>();
            ref readonly var node = ref GetNode(nodeIndex);
            int childIdx = node.FirstChildIndex;
            while (childIdx != -1)
            {
                if (childIdx < 0 || childIdx >= (int)_nodeCount)
                {
                    throw new InvalidDataException($"Invalid .gdoc child node index {childIdx} (node count {_nodeCount}).");
                }
                if (!visited.Add(childIdx))
                {
                    throw new InvalidDataException("Cycle detected in .gdoc sibling chain.");
                }
                result.Add(childIdx);
                ref readonly var child = ref GetNode(childIdx);
                childIdx = child.NextSiblingIndex;
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly GdocNodeEntry GetNode(int nodeIndex)
        {
            ThrowIfDisposed();
            if ((uint)nodeIndex >= _nodeCount)
            {
                throw new ArgumentOutOfRangeException(nameof(nodeIndex), $"Node index {nodeIndex} is out of bounds (0..{_nodeCount - 1}).");
            }
            if (_projectedNodes != null)
            {
                return ref _projectedNodes[nodeIndex];
            }
            return ref _nodeTable[nodeIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> GetContentUtf8(int nodeIndex)
        {
            ThrowIfDisposed();
            ref readonly var node = ref GetNode(nodeIndex);
            if (node.ContentLength == 0) return ReadOnlySpan<byte>.Empty;
            if ((ulong)node.ContentOffset + (ulong)node.ContentLength > _stringArenaLength || node.ContentLength > int.MaxValue)
            {
                throw new InvalidDataException($"Out-of-bounds content slice on node {nodeIndex}.");
            }
            return new ReadOnlySpan<byte>(_stringArena + node.ContentOffset, (int)node.ContentLength);
        }

        public string GetContentString(int nodeIndex)
        {
            ThrowIfDisposed();
            var span = GetContentUtf8(nodeIndex);
            return span.IsEmpty ? string.Empty : Encoding.UTF8.GetString(span);
        }

        public Dictionary<string, string> GetMetadata(int nodeIndex)
        {
            ThrowIfDisposed();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ref readonly var node = ref GetNode(nodeIndex);
            if (node.MetadataCount == 0 || node.MetadataIndex < 0 || _metadataTable == null) return result;

            int metaIdx = node.MetadataIndex;
            int remaining = (int)_metadataCount;
            while (metaIdx >= 0 && metaIdx < (int)_metadataCount && remaining-- > 0)
            {
                ref readonly var m = ref _metadataTable[metaIdx];
                if ((ulong)m.KeyOffset + (ulong)m.KeyLength > _stringArenaLength ||
                    (ulong)m.ValOffset + (ulong)m.ValLength > _stringArenaLength)
                {
                    throw new InvalidDataException($"Out-of-bounds metadata entry {metaIdx} on node {nodeIndex}.");
                }
                string key = m.KeyLength == 0 ? string.Empty : Encoding.UTF8.GetString(_stringArena + m.KeyOffset, m.KeyLength);
                string val = m.ValLength == 0 ? string.Empty : Encoding.UTF8.GetString(_stringArena + m.ValOffset, m.ValLength);
                result[key] = val;
                metaIdx = m.NextMetadataIndex;
            }

            return result;
        }

        public DocNode ToDocNodeTree()
        {
            ThrowIfDisposed();
            if (_nodeCount == 0)
            {
                return new DocNode { Type = NodeType.Root };
            }

            var nodes = new DocNode?[_nodeCount];
            for (int i = 0; i < _nodeCount; i++)
            {
                if (_legacyMetadataBlobNodes != null && _legacyMetadataBlobNodes[i])
                {
                    continue;
                }

                ref readonly var entry = ref GetNode(i);
                nodes[i] = new DocNode
                {
                    Type = (NodeType)entry.NodeType,
                    Content = GetContentString(i),
                    Metadata = GetMetadata(i)
                };
            }

            for (int i = 0; i < _nodeCount; i++)
            {
                ref readonly var entry = ref GetNode(i);
                if (_legacyMetadataBlobNodes != null && _legacyMetadataBlobNodes[i])
                {
                    if (entry.ParentIndex >= 0 && entry.ParentIndex < (int)_nodeCount && nodes[entry.ParentIndex] is DocNode parentNode)
                    {
                        string blobJson = GetContentString(i);
                        if (!string.IsNullOrEmpty(blobJson))
                        {
                            var decoded = JsonSerializer.Deserialize(blobJson, DocTreeJsonContext.Default.DictionaryStringString);
                            if (decoded != null)
                            {
                                foreach (var kvp in decoded)
                                {
                                    parentNode.Metadata[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                    }
                    continue;
                }

                if (entry.ParentIndex >= 0 && entry.ParentIndex < _nodeCount && entry.ParentIndex != i)
                {
                    if (nodes[entry.ParentIndex] is DocNode parentNode && nodes[i] is DocNode childNode)
                    {
                        parentNode.AddChild(childNode);
                    }
                }
            }

            return nodes[0] ?? new DocNode { Type = NodeType.Root };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (_basePointer != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    _basePointer = null;
                }
                _accessor.Dispose();
                _mmf.Dispose();
            }
        }
    }
}
