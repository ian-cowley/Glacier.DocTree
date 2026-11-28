namespace Glacier.DocTree.GDoc;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading;
using Glacier.DocTree.Binary;

public sealed unsafe class GDocReader : IDisposable
{
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;
    private MemoryHandle _memoryHandle;
    private readonly bool _ownsMemoryHandle;
    private byte* _pointer;
    private readonly long _length;
    private readonly bool _ownsPointer;

    private readonly GDocHeader _header;
    private readonly GDocAstNode[]? _legacyNodes;
    private readonly GdocMetadataEntry[]? _legacyMetadata;
    private int _disposed;

    public GDocHeader Header
    {
        get
        {
            ThrowIfDisposed();
            return _header;
        }
    }

    public uint NodeCount
    {
        get
        {
            ThrowIfDisposed();
            return _header.AstNodeCount;
        }
    }

    public uint VectorCount
    {
        get
        {
            ThrowIfDisposed();
            return _header.VectorCount;
        }
    }

    public uint VectorDim
    {
        get
        {
            ThrowIfDisposed();
            return _header.VectorDim;
        }
    }

    public uint MetadataCount
    {
        get
        {
            ThrowIfDisposed();
            return _header.MetadataCount;
        }
    }

    public static GDocReader OpenFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        var fi = new FileInfo(filePath);
        if (!fi.Exists)
        {
            throw new FileNotFoundException($"The specified .gdoc file was not found: '{filePath}'", filePath);
        }

        long fileLength = fi.Length;
        if (fileLength < GDocHeader.HeaderSize)
        {
            throw new InvalidDataException("Invalid .gdoc file: length smaller than header size.");
        }

        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? accessor = null;
        byte* ptr = null;
        bool pointerAcquired = false;

        try
        {
            mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            pointerAcquired = true;
        }
        catch
        {
            if (pointerAcquired && accessor != null)
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
            accessor?.Dispose();
            mmf?.Dispose();
            throw;
        }

        return new GDocReader(mmf, accessor, default, ownsMemoryHandle: false, ptr, fileLength, ownsPointer: true);
    }

    public static GDocReader FromMemory(ReadOnlyMemory<byte> memory)
    {
        var handle = memory.Pin();
        try
        {
            return new GDocReader(null, null, handle, ownsMemoryHandle: true, (byte*)handle.Pointer, memory.Length, ownsPointer: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private GDocReader(
        MemoryMappedFile? mmf,
        MemoryMappedViewAccessor? accessor,
        MemoryHandle memoryHandle,
        bool ownsMemoryHandle,
        byte* pointer,
        long length,
        bool ownsPointer)
    {
        _mmf = mmf;
        _accessor = accessor;
        _memoryHandle = memoryHandle;
        _ownsMemoryHandle = ownsMemoryHandle;
        _pointer = pointer;
        _length = length;
        _ownsPointer = ownsPointer;

        try
        {
            if (_length < GDocHeader.HeaderSize || _pointer == null)
            {
                throw new InvalidDataException("Invalid .gdoc file: length smaller than header size.");
            }

            uint magic = *(uint*)_pointer;
            if (magic == GdocWriter.GdocMagic)
            {
                LoadLegacyBinaryV1(out _header, out _legacyNodes, out _legacyMetadata);
                return;
            }

            _header = Unsafe.Read<GDocHeader>(_pointer);
            if (_header.Magic != GDocHeader.MagicValue)
            {
                throw new InvalidDataException($"Invalid .gdoc magic header: 0x{_header.Magic:X8}");
            }

            ulong totalLen = (ulong)_length;
            if (_header.Version == GDocHeader.CurrentVersion)
            {
                if (_length > GDocHeader.HeaderSize)
                {
                    uint actualCrc = GdocCrc32.ComputeLarge(_pointer + GDocHeader.HeaderSize, _length - GDocHeader.HeaderSize);
                    if (actualCrc != _header.PayloadCrc32)
                    {
                        throw new InvalidDataException($"CRC32 mismatch in .gdoc file: expected 0x{_header.PayloadCrc32:X8}, calculated 0x{actualCrc:X8}");
                    }
                }
                else if (_header.PayloadCrc32 != 0)
                {
                    throw new InvalidDataException($"CRC32 mismatch in .gdoc file: expected 0x{_header.PayloadCrc32:X8}, calculated 0x00000000");
                }

                ValidateCommonBounds(totalLen, (uint)sizeof(GDocAstNode), isV2: true);

                if (_header.MetadataTableOffset != 0)
                {
                    if (_header.MetadataTableOffset < GDocHeader.HeaderSize)
                    {
                        throw new InvalidDataException("Invalid .gdoc header: metadata table overlaps header.");
                    }
                    if ((_header.MetadataTableOffset & 7u) != 0)
                    {
                        throw new InvalidDataException("Invalid .gdoc header: metadata table offset must be 8-byte aligned.");
                    }
                    if (_header.MetadataTableOffset > totalLen)
                    {
                        throw new InvalidDataException("Invalid .gdoc header: metadata table extends beyond file length.");
                    }
                }

                if (_header.MetadataCount > 0)
                {
                    if (_header.MetadataTableOffset < GDocHeader.HeaderSize)
                    {
                        throw new InvalidDataException("Invalid .gdoc header: metadata table overlaps header.");
                    }
                    ulong metaEnd = checked((ulong)_header.MetadataTableOffset + (ulong)_header.MetadataCount * (ulong)sizeof(GdocMetadataEntry));
                    if (metaEnd > totalLen)
                    {
                        throw new InvalidDataException("Invalid .gdoc header: metadata table extends beyond file length.");
                    }
                }
            }
            else if (_header.Version == GDocHeader.LegacyVersion1)
            {
                ValidateCommonBounds(totalLen, (uint)sizeof(GDocAstNodeV1), isV2: false);

                _header.MetadataTableOffset = 0;
                _header.MetadataCount = 0;
                _header.PayloadCrc32 = 0;

                if (_header.AstNodeCount > 0)
                {
                    var nodes = new GDocAstNode[_header.AstNodeCount];
                    for (uint i = 0; i < _header.AstNodeCount; i++)
                    {
                        ref readonly var v1 = ref Unsafe.AsRef<GDocAstNodeV1>(_pointer + _header.AstTableOffset + ((ulong)i * _header.AstNodeSize));
                        nodes[i] = new GDocAstNode
                        {
                            Id = v1.Id,
                            NodeType = v1.NodeType,
                            Flags = v1.Flags,
                            ParentId = v1.ParentId,
                            FirstChildId = v1.FirstChildId,
                            NextSiblingId = v1.NextSiblingId,
                            TextOffset = v1.TextOffset,
                            TextLength = v1.TextLength,
                            VectorIndex = v1.VectorIndex,
                            TokenSpanIndex = v1.TokenSpanIndex,
                            MetadataIndex = GDocAstNode.NoMetadata,
                            MetadataCount = 0,
                            CoreNodeType = 0,
                            Reserved = 0,
                            Reserved2 = 0
                        };
                    }
                    _legacyNodes = nodes;
                }
            }
            else
            {
                throw new InvalidDataException($"Unsupported .gdoc file version: {_header.Version}");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void ValidateCommonBounds(ulong totalLen, uint expectedNodeSize, bool isV2)
    {
        if ((_header.StringPoolLength > 0 || _header.StringPoolOffset != 0) &&
            _header.StringPoolOffset < GDocHeader.HeaderSize)
        {
            throw new InvalidDataException("Invalid .gdoc header: string pool overlaps header.");
        }
        if (isV2 && (_header.StringPoolOffset & 7u) != 0)
        {
            throw new InvalidDataException("Invalid .gdoc header: string pool offset must be 8-byte aligned.");
        }
        if (_header.StringPoolOffset > totalLen ||
            (ulong)_header.StringPoolOffset + _header.StringPoolLength > totalLen)
        {
            throw new InvalidDataException("Invalid .gdoc header: string pool extends beyond file length.");
        }

        if ((_header.AstNodeCount > 0 || _header.AstTableOffset != 0) &&
            _header.AstTableOffset < GDocHeader.HeaderSize)
        {
            throw new InvalidDataException("Invalid .gdoc header: AST table overlaps header.");
        }
        if (isV2 && (_header.AstTableOffset & 7u) != 0)
        {
            throw new InvalidDataException("Invalid .gdoc header: AST table offset must be 8-byte aligned.");
        }
        if (_header.AstTableOffset > totalLen)
        {
            throw new InvalidDataException("Invalid .gdoc header: AST table extends beyond file length.");
        }
        if (_header.AstNodeCount > 0)
        {
            if (_header.AstNodeSize != expectedNodeSize)
            {
                throw new InvalidDataException($"Invalid .gdoc header: AST node stride {_header.AstNodeSize} does not match expected {expectedNodeSize}.");
            }
            ulong astEnd = checked((ulong)_header.AstTableOffset + (ulong)_header.AstNodeCount * _header.AstNodeSize);
            if (astEnd > totalLen)
            {
                throw new InvalidDataException("Invalid .gdoc header: AST table extends beyond file length.");
            }
        }

        if ((_header.TokenSpanCount > 0 || _header.TokenSpanOffset != 0) &&
            _header.TokenSpanOffset < GDocHeader.HeaderSize)
        {
            throw new InvalidDataException("Invalid .gdoc header: token span table overlaps header.");
        }
        if (isV2 && (_header.TokenSpanOffset & 7u) != 0)
        {
            throw new InvalidDataException("Invalid .gdoc header: token span table offset must be 8-byte aligned.");
        }
        if (_header.TokenSpanOffset > totalLen)
        {
            throw new InvalidDataException("Invalid .gdoc header: token span table extends beyond file length.");
        }
        if (_header.TokenSpanCount > 0)
        {
            if (_header.TokenSpanSize != GDocTokenSpan.SpanSize)
            {
                throw new InvalidDataException($"Invalid .gdoc header: token span stride {_header.TokenSpanSize} does not match expected {GDocTokenSpan.SpanSize}.");
            }
            ulong spanEnd = checked((ulong)_header.TokenSpanOffset + (ulong)_header.TokenSpanCount * _header.TokenSpanSize);
            if (spanEnd > totalLen)
            {
                throw new InvalidDataException("Invalid .gdoc header: token span table extends beyond file length.");
            }
        }

        if ((_header.VectorCount > 0 || _header.VectorOffset != 0) &&
            _header.VectorOffset < GDocHeader.HeaderSize)
        {
            throw new InvalidDataException("Invalid .gdoc header: vector table overlaps header.");
        }
        if (isV2 && (_header.VectorOffset & 7u) != 0)
        {
            throw new InvalidDataException("Invalid .gdoc header: vector table offset must be 8-byte aligned.");
        }
        if (_header.VectorOffset > totalLen)
        {
            throw new InvalidDataException("Invalid .gdoc header: vector table extends beyond file length.");
        }
        if (_header.VectorCount > 0)
        {
            if (_header.VectorDim == 0)
            {
                throw new InvalidDataException("Invalid .gdoc header: vector dimension must be positive when VectorCount > 0.");
            }
            ulong vecEnd = checked((ulong)_header.VectorOffset + (ulong)_header.VectorCount * _header.VectorDim * sizeof(float));
            if (vecEnd > totalLen)
            {
                throw new InvalidDataException("Invalid .gdoc header: vector table extends beyond file length.");
            }
        }
    }

    private void LoadLegacyBinaryV1(
        out GDocHeader normalizedHeader,
        out GDocAstNode[]? nodes,
        out GdocMetadataEntry[]? metadata)
    {
        ushort version = *(ushort*)(_pointer + 4);
        if (version != GdocWriter.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported .gdoc file version: {version}");
        }

        long payloadLength = _length - GdocWriter.HeaderSize;
        uint expectedCrc = *(uint*)(_pointer + 58);
        if (payloadLength > 0)
        {
            uint actualCrc = GdocCrc32.ComputeLarge(_pointer + GdocWriter.HeaderSize, payloadLength);
            if (actualCrc != expectedCrc)
            {
                throw new InvalidDataException($"CRC32 mismatch in .gdoc file: expected 0x{expectedCrc:X8}, calculated 0x{actualCrc:X8}");
            }
        }
        else if (expectedCrc != 0)
        {
            throw new InvalidDataException($"CRC32 mismatch in .gdoc file: expected 0x{expectedCrc:X8}, calculated 0x00000000");
        }

        ulong uFileLength = (ulong)_length;
        uint nodeCount = *(uint*)(_pointer + 6);
        ulong stringArenaOffset = *(ulong*)(_pointer + 10);
        ulong stringArenaLength = *(ulong*)(_pointer + 18);
        ulong nodeTableOffset = *(ulong*)(_pointer + 26);
        ulong nodeTableLength = *(ulong*)(_pointer + 34);
        ulong metadataTableOffset = *(ulong*)(_pointer + 42);
        ulong metadataTableLength = *(ulong*)(_pointer + 50);

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
            stringArenaLength > uFileLength - stringArenaOffset ||
            stringArenaOffset > uint.MaxValue ||
            stringArenaLength > uint.MaxValue)
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

        uint metaCount = (uint)(metadataTableLength / 16UL);
        normalizedHeader = new GDocHeader
        {
            Magic = GDocHeader.MagicValue,
            Version = GDocHeader.LegacyVersion1,
            StringPoolOffset = (uint)stringArenaOffset,
            StringPoolLength = (uint)stringArenaLength,
            AstTableOffset = (uint)Math.Min(nodeTableOffset, uint.MaxValue),
            AstNodeCount = nodeCount,
            AstNodeSize = (uint)GDocAstNode.NodeSize,
            TokenSpanOffset = 0,
            TokenSpanCount = 0,
            TokenSpanSize = 16,
            VectorOffset = 0,
            VectorCount = 0,
            VectorDim = 0,
            MetadataTableOffset = (uint)Math.Min(metadataTableOffset, uint.MaxValue),
            MetadataCount = metaCount,
            PayloadCrc32 = expectedCrc
        };

        if (nodeCount > 0)
        {
            nodes = new GDocAstNode[nodeCount];
            var rawNodes = (GdocNodeEntry*)(_pointer + nodeTableOffset);
            for (uint i = 0; i < nodeCount; i++)
            {
                ref readonly var entry = ref rawNodes[i];
                var coreType = (Core.NodeType)entry.NodeType;
                nodes[i] = new GDocAstNode
                {
                    Id = i,
                    NodeType = (ushort)GlacierStorageDocExtensions.ToGDocType(coreType),
                    Flags = (ushort)(0x4000 | (ushort)coreType),
                    ParentId = entry.ParentIndex >= 0 ? (uint)entry.ParentIndex : GDocAstNode.NoNode,
                    FirstChildId = entry.FirstChildIndex >= 0 ? (uint)entry.FirstChildIndex : GDocAstNode.NoNode,
                    NextSiblingId = entry.NextSiblingIndex >= 0 ? (uint)entry.NextSiblingIndex : GDocAstNode.NoNode,
                    TextOffset = entry.ContentOffset,
                    TextLength = entry.ContentLength,
                    VectorIndex = GDocAstNode.NoVector,
                    TokenSpanIndex = 0,
                    MetadataIndex = entry.MetadataIndex,
                    MetadataCount = entry.MetadataCount,
                    CoreNodeType = entry.NodeType
                };
            }
        }
        else
        {
            nodes = null;
        }

        if (metaCount > 0)
        {
            metadata = new GdocMetadataEntry[metaCount];
            var rawMeta = (GdocMetadataEntry*)(_pointer + metadataTableOffset);
            for (uint i = 0; i < metaCount; i++)
            {
                metadata[i] = rawMeta[i];
            }
        }
        else
        {
            metadata = null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly GDocAstNode GetNode(uint id)
    {
        ThrowIfDisposed();
        if (id >= _header.AstNodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        if (_legacyNodes != null)
        {
            return ref _legacyNodes[id];
        }

        byte* nodePtr = _pointer + _header.AstTableOffset + ((ulong)id * _header.AstNodeSize);
        return ref Unsafe.AsRef<GDocAstNode>(nodePtr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetTextUtf8(in GDocAstNode node)
    {
        ThrowIfDisposed();
        if (node.TextLength == 0) return ReadOnlySpan<byte>.Empty;
        ulong localEnd = (ulong)node.TextOffset + node.TextLength;
        ulong fileEnd = checked((ulong)_header.StringPoolOffset + localEnd);
        if (localEnd > _header.StringPoolLength || fileEnd > (ulong)_length || node.TextLength > int.MaxValue)
        {
            throw new InvalidDataException("Invalid .gdoc AST node: text span extends beyond string pool bounds.");
        }
        byte* textPtr = _pointer + _header.StringPoolOffset + node.TextOffset;
        return new ReadOnlySpan<byte>(textPtr, (int)node.TextLength);
    }

    public string GetText(in GDocAstNode node)
    {
        ThrowIfDisposed();
        var span = GetTextUtf8(in node);
        return span.IsEmpty ? string.Empty : Encoding.UTF8.GetString(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<float> GetVector(in GDocAstNode node)
    {
        ThrowIfDisposed();
        if (node.VectorIndex == GDocAstNode.NoVector || node.VectorIndex >= _header.VectorCount)
        {
            return ReadOnlySpan<float>.Empty;
        }

        ulong vecByteOffset = checked((ulong)node.VectorIndex * _header.VectorDim * sizeof(float));
        ulong vecEnd = checked((ulong)_header.VectorOffset + vecByteOffset + ((ulong)_header.VectorDim * sizeof(float)));
        if (vecEnd > (ulong)_length || _header.VectorDim > int.MaxValue)
        {
            throw new InvalidDataException("Invalid .gdoc AST node: vector extends beyond file length.");
        }

        byte* vecPtr = _pointer + _header.VectorOffset + vecByteOffset;
        return new ReadOnlySpan<float>(vecPtr, (int)_header.VectorDim);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly GDocTokenSpan GetTokenSpan(uint index)
    {
        ThrowIfDisposed();
        if (index >= _header.TokenSpanCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        byte* spanPtr = _pointer + _header.TokenSpanOffset + ((ulong)index * _header.TokenSpanSize);
        return ref Unsafe.AsRef<GDocTokenSpan>(spanPtr);
    }

    public Dictionary<string, string> GetMetadata(uint nodeId)
    {
        ThrowIfDisposed();
        ref readonly var node = ref GetNode(nodeId);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node.MetadataCount == 0 || node.MetadataIndex < 0 || _header.MetadataCount == 0)
        {
            return result;
        }

        int metaIdx = node.MetadataIndex;
        int remaining = (int)_header.MetadataCount;
        var metaTable = _legacyMetadata == null && _header.MetadataTableOffset >= GDocHeader.HeaderSize
            ? (GdocMetadataEntry*)(_pointer + _header.MetadataTableOffset)
            : null;

        while (metaIdx >= 0 && (uint)metaIdx < _header.MetadataCount && remaining-- > 0)
        {
            ref readonly GdocMetadataEntry entry = ref (_legacyMetadata != null
                ? ref _legacyMetadata[metaIdx]
                : ref metaTable![metaIdx]);

            if ((ulong)entry.KeyOffset + entry.KeyLength > _header.StringPoolLength ||
                (ulong)entry.ValOffset + entry.ValLength > _header.StringPoolLength)
            {
                throw new InvalidDataException($"Out-of-bounds metadata entry {metaIdx} on node {nodeId}.");
            }

            byte* poolPtr = _pointer + _header.StringPoolOffset;
            string key = entry.KeyLength == 0 ? string.Empty : Encoding.UTF8.GetString(poolPtr + entry.KeyOffset, entry.KeyLength);
            string val = entry.ValLength == 0 ? string.Empty : Encoding.UTF8.GetString(poolPtr + entry.ValOffset, entry.ValLength);
            result[key] = val;
            metaIdx = entry.NextMetadataIndex;
        }

        return result;
    }

    public List<uint> GetChildren(uint nodeId)
    {
        ThrowIfDisposed();
        var result = new List<uint>();
        var visited = new HashSet<uint>();
        ref readonly var parent = ref GetNode(nodeId);
        uint curr = parent.FirstChildId;
        while (curr != GDocAstNode.NoNode)
        {
            if (curr >= _header.AstNodeCount)
            {
                throw new InvalidDataException($"Invalid .gdoc AST child node index {curr} (node count {_header.AstNodeCount}).");
            }
            if (!visited.Add(curr))
            {
                throw new InvalidDataException("Cycle detected in .gdoc AST sibling chain.");
            }
            result.Add(curr);
            ref readonly var child = ref GetNode(curr);
            curr = child.NextSiblingId;
        }
        return result;
    }

    public List<uint> GetBreadcrumbs(uint nodeId)
    {
        ThrowIfDisposed();
        if (nodeId >= _header.AstNodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId));
        }
        var breadcrumbs = new List<uint>();
        var visited = new HashSet<uint>();
        uint curr = nodeId;
        while (curr != GDocAstNode.NoNode)
        {
            if (curr >= _header.AstNodeCount)
            {
                throw new InvalidDataException($"Invalid .gdoc AST parent node index {curr} (node count {_header.AstNodeCount}).");
            }
            if (!visited.Add(curr))
            {
                throw new InvalidDataException("Cycle detected in .gdoc AST parent chain.");
            }
            breadcrumbs.Add(curr);
            ref readonly var node = ref GetNode(curr);
            curr = node.ParentId;
        }
        breadcrumbs.Reverse();
        return breadcrumbs;
    }

    public (uint NodeId, float Score) FindBestMatch(ReadOnlySpan<float> query)
    {
        ThrowIfDisposed();
        if (query.Length != (int)_header.VectorDim)
        {
            throw new ArgumentException("Query vector dimension mismatch.", nameof(query));
        }

        uint bestNodeId = GDocAstNode.NoNode;
        float bestScore = float.NegativeInfinity;

        for (uint i = 0; i < _header.AstNodeCount; i++)
        {
            ref readonly var node = ref GetNode(i);
            if (node.VectorIndex != GDocAstNode.NoVector)
            {
                var vec = GetVector(in node);
                float sim = CosineSimilarity(query, vec);
                if (sim > bestScore)
                {
                    bestScore = sim;
                    bestNodeId = node.Id;
                }
            }
        }

        return (bestNodeId, bestScore);
    }

    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vector dimensions must match.");

        ref float ptrA = ref MemoryMarshal.GetReference(a);
        ref float ptrB = ref MemoryMarshal.GetReference(b);
        int len = a.Length;
        int i = 0;

        float dot = 0f;
        float normA = 0f;
        float normB = 0f;

        if (Vector512.IsHardwareAccelerated && len >= Vector512<float>.Count)
        {
            var vDot = Vector512<float>.Zero;
            var vNormA = Vector512<float>.Zero;
            var vNormB = Vector512<float>.Zero;

            int step = Vector512<float>.Count;
            while (i <= len - step)
            {
                var va = Vector512.LoadUnsafe(ref ptrA, (nuint)i);
                var vb = Vector512.LoadUnsafe(ref ptrB, (nuint)i);
                vDot += va * vb;
                vNormA += va * va;
                vNormB += vb * vb;
                i += step;
            }

            dot += Vector512.Sum(vDot);
            normA += Vector512.Sum(vNormA);
            normB += Vector512.Sum(vNormB);
        }
        else if (Vector256.IsHardwareAccelerated && len >= Vector256<float>.Count)
        {
            var vDot = Vector256<float>.Zero;
            var vNormA = Vector256<float>.Zero;
            var vNormB = Vector256<float>.Zero;

            int step = Vector256<float>.Count;
            while (i <= len - step)
            {
                var va = Vector256.LoadUnsafe(ref ptrA, (nuint)i);
                var vb = Vector256.LoadUnsafe(ref ptrB, (nuint)i);
                vDot += va * vb;
                vNormA += va * va;
                vNormB += vb * vb;
                i += step;
            }

            dot += Vector256.Sum(vDot);
            normA += Vector256.Sum(vNormA);
            normB += Vector256.Sum(vNormB);
        }

        while (i < len)
        {
            float va = Unsafe.Add(ref ptrA, i);
            float vb = Unsafe.Add(ref ptrB, i);
            dot += va * vb;
            normA += va * va;
            normB += vb * vb;
            i++;
        }

        float denom = (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
        return denom > 0f ? dot / denom : 0f;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_ownsPointer && _accessor != null)
            {
                if (_pointer != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    _pointer = null;
                }
                _accessor.Dispose();
                _mmf?.Dispose();
            }

            if (_ownsMemoryHandle)
            {
                _memoryHandle.Dispose();
                _pointer = null;
            }
        }
    }
}
