using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Glacier.DocTree.Core;

namespace Glacier.DocTree.Binary
{
    public sealed unsafe class GdocReader : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private byte* _basePointer;
        private readonly uint _nodeCount;
        private readonly GdocNodeEntry* _nodeTable;
        private readonly byte* _stringArena;
        private readonly GdocMetadataEntry* _metadataTable;
        private readonly uint _metadataCount;
        private bool _disposed;

        public uint NodeCount => _nodeCount;

        public GdocReader(string gdocFilePath)
        {
            if (!File.Exists(gdocFilePath))
            {
                throw new FileNotFoundException($"The specified .gdoc file was not found: '{gdocFilePath}'");
            }

            _mmf = MemoryMappedFile.CreateFromFile(gdocFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePointer);

            // 1. Validate magic and version
            uint magic = *(uint*)_basePointer;
            if (magic != GdocWriter.GdocMagic)
            {
                throw new InvalidDataException($"Invalid .gdoc file magic identifier: 0x{magic:X8}");
            }

            ushort version = *(ushort*)(_basePointer + 4);
            if (version != GdocWriter.CurrentVersion)
            {
                throw new InvalidDataException($"Unsupported .gdoc file version: {version}");
            }

            // 2. Validate CRC
            uint expectedCrc = *(uint*)(_basePointer + 58);
            long fileLength = new FileInfo(gdocFilePath).Length;
            long payloadLength = fileLength - GdocWriter.HeaderSize;
            if (payloadLength < 0)
            {
                throw new InvalidDataException("File is too short to be a valid .gdoc file.");
            }
            if (payloadLength > 0)
            {
                var payloadSpan = new ReadOnlySpan<byte>(_basePointer + GdocWriter.HeaderSize, (int)payloadLength);
                uint actualCrc = GdocCrc32.Compute(payloadSpan);
                if (actualCrc != expectedCrc)
                {
                    throw new InvalidDataException($"CRC32 mismatch in .gdoc file: expected 0x{expectedCrc:X8}, calculated 0x{actualCrc:X8}");
                }
            }

            _nodeCount = *(uint*)(_basePointer + 6);
            ulong stringArenaOffset = *(ulong*)(_basePointer + 10);
            ulong stringArenaLength = *(ulong*)(_basePointer + 18);
            ulong nodeTableOffset = *(ulong*)(_basePointer + 26);
            ulong metadataTableOffset = *(ulong*)(_basePointer + 42);
            ulong metadataTableLength = *(ulong*)(_basePointer + 50);

            _metadataCount = (uint)(metadataTableLength / 16);
            _nodeTable = (GdocNodeEntry*)(_basePointer + nodeTableOffset);
            _stringArena = _basePointer + stringArenaOffset;
            _metadataTable = (GdocMetadataEntry*)(_basePointer + metadataTableOffset);
        }

        public uint MetadataCount => _metadataCount;

        public List<int> GetChildIndices(int nodeIndex)
        {
            var result = new List<int>();
            ref readonly var node = ref GetNode(nodeIndex);
            int childIdx = node.FirstChildIndex;
            while (childIdx >= 0 && childIdx < (int)_nodeCount)
            {
                result.Add(childIdx);
                ref readonly var child = ref GetNode(childIdx);
                childIdx = child.NextSiblingIndex;
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly GdocNodeEntry GetNode(int nodeIndex)
        {
            if ((uint)nodeIndex >= _nodeCount)
            {
                throw new ArgumentOutOfRangeException(nameof(nodeIndex), $"Node index {nodeIndex} is out of bounds (0..{_nodeCount - 1}).");
            }
            return ref _nodeTable[nodeIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> GetContentUtf8(int nodeIndex)
        {
            ref readonly var node = ref GetNode(nodeIndex);
            if (node.ContentLength == 0) return ReadOnlySpan<byte>.Empty;
            return new ReadOnlySpan<byte>(_stringArena + node.ContentOffset, (int)node.ContentLength);
        }

        public string GetContentString(int nodeIndex)
        {
            var span = GetContentUtf8(nodeIndex);
            return span.IsEmpty ? string.Empty : Encoding.UTF8.GetString(span);
        }

        public Dictionary<string, string> GetMetadata(int nodeIndex)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ref readonly var node = ref GetNode(nodeIndex);
            if (node.MetadataCount == 0 || node.MetadataIndex < 0) return result;

            int metaIdx = node.MetadataIndex;
            while (metaIdx >= 0 && metaIdx < (int)_metadataCount)
            {
                ref readonly var m = ref _metadataTable[metaIdx];
                string key = Encoding.UTF8.GetString(_stringArena + m.KeyOffset, m.KeyLength);
                string val = Encoding.UTF8.GetString(_stringArena + m.ValOffset, m.ValLength);
                result[key] = val;
                metaIdx = m.NextMetadataIndex;
            }

            return result;
        }

        public DocNode ToDocNodeTree()
        {
            if (_nodeCount == 0)
            {
                return new DocNode { Type = NodeType.Root };
            }

            var nodes = new DocNode[_nodeCount];
            for (int i = 0; i < _nodeCount; i++)
            {
                ref readonly var entry = ref GetNode(i);
                nodes[i] = new DocNode
                {
                    Type = (NodeType)entry.NodeType,
                    Content = GetContentString(i),
                    Metadata = GetMetadata(i)
                };
            }

            // Link parent and children
            for (int i = 0; i < _nodeCount; i++)
            {
                ref readonly var entry = ref GetNode(i);
                if (entry.ParentIndex >= 0 && entry.ParentIndex < _nodeCount)
                {
                    nodes[entry.ParentIndex].AddChild(nodes[i]);
                }
            }

            return nodes[0];
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
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
