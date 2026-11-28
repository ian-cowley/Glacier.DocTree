namespace Glacier.DocTree.GDoc;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Glacier.DocTree.Binary;
using Glacier.DocTree.Core;

public sealed class GDocWriter
{
    private static readonly byte[] ZeroPadBytes = new byte[8];

    private readonly ArrayBufferWriter<byte> _stringPool = new();
    private readonly List<GDocAstNode> _nodes = new();
    private readonly List<uint> _lastChildIds = new();
    private readonly List<bool> _isLinkedChild = new();
    private readonly List<GdocMetadataEntry> _metadata = new();
    private readonly List<int> _lastMetadataIds = new();
    private readonly List<GDocTokenSpan> _tokenSpans = new();
    private readonly List<float[]> _vectors = new();
    private readonly int _vectorDim;

    public GDocWriter(int vectorDim = 768)
    {
        if (vectorDim < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vectorDim), "Vector dimension cannot be negative.");
        }
        _vectorDim = vectorDim;
    }

    public uint AddString(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        uint offset = checked((uint)_stringPool.WrittenCount);
        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        Span<byte> dest = _stringPool.GetSpan(maxBytes);
        int written = Encoding.UTF8.GetBytes(text.AsSpan(), dest);
        checked
        {
            _ = (uint)(_stringPool.WrittenCount + written);
        }
        _stringPool.Advance(written);
        return offset;
    }

    public uint AddVector(ReadOnlySpan<float> vector)
    {
        if (vector.Length != _vectorDim)
        {
            throw new ArgumentException($"Vector dimension must be {_vectorDim}.", nameof(vector));
        }

        uint index = checked((uint)_vectors.Count);
        _vectors.Add(vector.ToArray());
        return index;
    }

    public uint AddTokenSpan(uint startOffset, uint endOffset, ushort tokenType, ushort flags = 0, uint metadata = 0)
    {
        uint index = checked((uint)_tokenSpans.Count);
        _tokenSpans.Add(new GDocTokenSpan
        {
            StartOffset = startOffset,
            EndOffset = endOffset,
            TokenType = tokenType,
            Flags = flags,
            Metadata = metadata
        });
        return index;
    }

    public uint AddNode(
        GDocNodeType nodeType,
        string? text = null,
        uint parentId = GDocAstNode.NoNode,
        uint firstChildId = GDocAstNode.NoNode,
        uint nextSiblingId = GDocAstNode.NoNode,
        uint tokenSpanIndex = 0,
        uint vectorIndex = GDocAstNode.NoVector,
        ushort flags = 0)
    {
        uint id = checked((uint)_nodes.Count);
        uint textOffset = 0;
        uint textLen = 0;

        if (!string.IsNullOrEmpty(text))
        {
            textOffset = checked((uint)_stringPool.WrittenCount);
            int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
            Span<byte> dest = _stringPool.GetSpan(maxBytes);
            int written = Encoding.UTF8.GetBytes(text.AsSpan(), dest);
            checked
            {
                _ = (uint)(_stringPool.WrittenCount + written);
            }
            _stringPool.Advance(written);
            textLen = checked((uint)written);
        }

        _nodes.Add(new GDocAstNode
        {
            Id = id,
            NodeType = (ushort)nodeType,
            Flags = flags,
            ParentId = parentId,
            FirstChildId = firstChildId,
            NextSiblingId = nextSiblingId,
            TextOffset = textOffset,
            TextLength = textLen,
            VectorIndex = vectorIndex,
            TokenSpanIndex = tokenSpanIndex,
            MetadataIndex = GDocAstNode.NoMetadata,
            MetadataCount = 0,
            CoreNodeType = 0,
            Reserved = 0,
            Reserved2 = 0
        });
        _lastChildIds.Add(firstChildId);
        _isLinkedChild.Add(false);
        _lastMetadataIds.Add(-1);

        return id;
    }

    public void SetCoreNodeType(uint nodeId, NodeType coreNodeType)
    {
        if (nodeId >= (uint)_nodes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId));
        }

        var node = _nodes[(int)nodeId];
        node.CoreNodeType = (byte)coreNodeType;
        _nodes[(int)nodeId] = node;
    }

    public void AddMetadata(uint nodeId, string key, string value)
    {
        if (nodeId >= (uint)_nodes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId));
        }
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var node = _nodes[(int)nodeId];
        if (node.MetadataCount == ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), $"Node {nodeId} exceeded maximum metadata entry count ({ushort.MaxValue}).");
        }

        (uint kOffset, ushort kLen) = AppendMetadataString(key, nameof(key));
        (uint vOffset, ushort vLen) = AppendMetadataString(value, nameof(value));

        int newMetaIdx = _metadata.Count;
        int prevMetaIdx = _lastMetadataIds[(int)nodeId];
        if (prevMetaIdx == -1)
        {
            node.MetadataIndex = newMetaIdx;
        }
        else
        {
            var prev = _metadata[prevMetaIdx];
            _metadata[prevMetaIdx] = new GdocMetadataEntry(prev.KeyOffset, prev.KeyLength, prev.ValOffset, prev.ValLength, newMetaIdx);
        }

        _metadata.Add(new GdocMetadataEntry(kOffset, kLen, vOffset, vLen, -1));
        _lastMetadataIds[(int)nodeId] = newMetaIdx;

        node.MetadataCount++;
        _nodes[(int)nodeId] = node;
    }

    public void AddMetadata(uint nodeId, IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        foreach (var kvp in metadata)
        {
            AddMetadata(nodeId, kvp.Key, kvp.Value);
        }
    }

    private (uint Offset, ushort Length) AppendMetadataString(string text, string paramName)
    {
        if (text.Length == 0)
        {
            return (0u, 0);
        }

        uint offset = checked((uint)_stringPool.WrittenCount);
        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        Span<byte> dest = _stringPool.GetSpan(maxBytes);
        int written = Encoding.UTF8.GetBytes(text.AsSpan(), dest);
        if (written > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(paramName, $"Metadata UTF-8 byte length ({written}) exceeds maximum supported length ({ushort.MaxValue}).");
        }
        checked
        {
            _ = (uint)(_stringPool.WrittenCount + written);
        }
        _stringPool.Advance(written);
        return (offset, (ushort)written);
    }

    public void LinkChild(uint parentId, uint childId)
    {
        if (parentId >= (uint)_nodes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(parentId));
        }
        if (childId >= (uint)_nodes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(childId));
        }
        if (parentId == childId)
        {
            throw new ArgumentException("A node cannot be linked as a child of itself.", nameof(childId));
        }

        var child = _nodes[(int)childId];
        if (_isLinkedChild[(int)childId])
        {
            throw new InvalidOperationException($"Node {childId} is already attached as a child.");
        }
        if (child.ParentId != GDocAstNode.NoNode && child.ParentId != parentId)
        {
            throw new InvalidOperationException($"Node {childId} already has parent {child.ParentId}.");
        }
        if (child.NextSiblingId != GDocAstNode.NoNode)
        {
            throw new InvalidOperationException($"Node {childId} already has a sibling link.");
        }

        var parent = _nodes[(int)parentId];
        if (parent.FirstChildId == GDocAstNode.NoNode)
        {
            parent.FirstChildId = childId;
            _nodes[(int)parentId] = parent;
            _lastChildIds[(int)parentId] = childId;
        }
        else
        {
            uint lastChildId = _lastChildIds[(int)parentId];
            if (lastChildId == GDocAstNode.NoNode)
            {
                uint current = parent.FirstChildId;
                int remaining = _nodes.Count;
                while (remaining-- > 0)
                {
                    var currNode = _nodes[(int)current];
                    if (currNode.NextSiblingId == GDocAstNode.NoNode)
                    {
                        lastChildId = current;
                        break;
                    }
                    current = currNode.NextSiblingId;
                }
            }

            var tailNode = _nodes[(int)lastChildId];
            tailNode.NextSiblingId = childId;
            _nodes[(int)lastChildId] = tailNode;
            _lastChildIds[(int)parentId] = childId;
        }

        child.ParentId = parentId;
        _nodes[(int)childId] = child;
        _isLinkedChild[(int)childId] = true;
    }

    public byte[] ToByteArray()
    {
        var layout = ComputeLayout();
        uint totalBytes = layout.TotalBytes;
        using var ms = totalBytes <= int.MaxValue ? new MemoryStream((int)totalBytes) : new MemoryStream();
        WriteToStream(ms);
        return ms.ToArray();
    }

    public void WriteToFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmpPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                WriteToStream(fs);
                fs.Flush(true);
            }

            File.Move(tmpPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }

    public void WriteToStream(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var layout = ComputeLayout();
        uint payloadCrc = ComputePayloadCrc32(in layout);

        var header = new GDocHeader
        {
            Magic = GDocHeader.MagicValue,
            Version = GDocHeader.CurrentVersion,
            StringPoolOffset = layout.StringPoolOffset,
            StringPoolLength = layout.StringPoolLength,
            AstTableOffset = layout.AstTableOffset,
            AstNodeCount = layout.AstNodeCount,
            AstNodeSize = layout.AstNodeSize,
            TokenSpanOffset = layout.TokenSpanOffset,
            TokenSpanCount = layout.TokenSpanCount,
            TokenSpanSize = layout.TokenSpanSize,
            VectorOffset = layout.VectorOffset,
            VectorCount = layout.VectorCount,
            VectorDim = (uint)_vectorDim,
            MetadataTableOffset = layout.MetadataTableOffset,
            MetadataCount = layout.MetadataCount,
            PayloadCrc32 = payloadCrc
        };

        Span<byte> headerBytes = stackalloc byte[GDocHeader.HeaderSize];
        MemoryMarshal.Write(headerBytes, in header);
        destination.Write(headerBytes);

        long bytesWritten = GDocHeader.HeaderSize;

        // 1. String pool
        ReadOnlySpan<byte> stringSpan = _stringPool.WrittenSpan;
        if (!stringSpan.IsEmpty)
        {
            destination.Write(stringSpan);
            bytesWritten += stringSpan.Length;
        }

        // 2. AST table
        WriteZeroPad(destination, ref bytesWritten, layout.AstTableOffset);
        Span<byte> nodeBytes = stackalloc byte[GDocAstNode.NodeSize];
        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            MemoryMarshal.Write(nodeBytes, in node);
            destination.Write(nodeBytes);
            bytesWritten += GDocAstNode.NodeSize;
        }

        // 3. Token spans
        WriteZeroPad(destination, ref bytesWritten, layout.TokenSpanOffset);
        Span<byte> spanBytes = stackalloc byte[16];
        for (int i = 0; i < _tokenSpans.Count; i++)
        {
            var span = _tokenSpans[i];
            MemoryMarshal.Write(spanBytes, in span);
            destination.Write(spanBytes);
            bytesWritten += 16;
        }

        // 4. Vectors
        WriteZeroPad(destination, ref bytesWritten, layout.VectorOffset);
        for (int i = 0; i < _vectors.Count; i++)
        {
            ReadOnlySpan<float> vec = _vectors[i];
            ReadOnlySpan<byte> rawVec = MemoryMarshal.AsBytes(vec);
            destination.Write(rawVec);
            bytesWritten += rawVec.Length;
        }

        // 5. Native metadata table
        if (layout.MetadataCount > 0)
        {
            WriteZeroPad(destination, ref bytesWritten, layout.MetadataTableOffset);
            Span<byte> metaBytes = stackalloc byte[16];
            for (int i = 0; i < _metadata.Count; i++)
            {
                var meta = _metadata[i];
                MemoryMarshal.Write(metaBytes, in meta);
                destination.Write(metaBytes);
                bytesWritten += 16;
            }
        }

        destination.Flush();
    }

    private uint ComputePayloadCrc32(in Layout layout)
    {
        var crc = new Crc32();
        long offset = GDocHeader.HeaderSize;

        ReadOnlySpan<byte> stringSpan = _stringPool.WrittenSpan;
        if (!stringSpan.IsEmpty)
        {
            crc.Append(stringSpan);
            offset += stringSpan.Length;
        }

        AppendZeroPad(crc, ref offset, layout.AstTableOffset);
        Span<byte> nodeBytes = stackalloc byte[GDocAstNode.NodeSize];
        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            MemoryMarshal.Write(nodeBytes, in node);
            crc.Append(nodeBytes);
            offset += GDocAstNode.NodeSize;
        }

        AppendZeroPad(crc, ref offset, layout.TokenSpanOffset);
        Span<byte> spanBytes = stackalloc byte[16];
        for (int i = 0; i < _tokenSpans.Count; i++)
        {
            var span = _tokenSpans[i];
            MemoryMarshal.Write(spanBytes, in span);
            crc.Append(spanBytes);
            offset += 16;
        }

        AppendZeroPad(crc, ref offset, layout.VectorOffset);
        for (int i = 0; i < _vectors.Count; i++)
        {
            ReadOnlySpan<float> vec = _vectors[i];
            ReadOnlySpan<byte> rawVec = MemoryMarshal.AsBytes(vec);
            crc.Append(rawVec);
            offset += rawVec.Length;
        }

        if (layout.MetadataCount > 0)
        {
            AppendZeroPad(crc, ref offset, layout.MetadataTableOffset);
            Span<byte> metaBytes = stackalloc byte[16];
            for (int i = 0; i < _metadata.Count; i++)
            {
                var meta = _metadata[i];
                MemoryMarshal.Write(metaBytes, in meta);
                crc.Append(metaBytes);
                offset += 16;
            }
        }

        return crc.GetCurrentHashAsUInt32();
    }

    private readonly record struct Layout(
        uint StringPoolOffset,
        uint StringPoolLength,
        uint AstTableOffset,
        uint AstNodeCount,
        uint AstNodeSize,
        uint TokenSpanOffset,
        uint TokenSpanCount,
        uint TokenSpanSize,
        uint VectorOffset,
        uint VectorCount,
        uint MetadataTableOffset,
        uint MetadataCount,
        uint TotalBytes);

    private Layout ComputeLayout()
    {
        const uint headerSize = GDocHeader.HeaderSize;
        const uint astNodeSize = GDocAstNode.NodeSize;
        const uint tokenSpanSize = 16u;
        const uint metadataEntrySize = 16u;

        uint stringPoolOffset = headerSize;
        uint stringPoolLength = checked((uint)_stringPool.WrittenCount);

        uint astTableOffset = Align8(checked(stringPoolOffset + stringPoolLength));
        uint astNodeCount = checked((uint)_nodes.Count);

        uint tokenSpanOffset = Align8(checked(astTableOffset + astNodeCount * astNodeSize));
        uint tokenSpanCount = checked((uint)_tokenSpans.Count);

        uint vectorOffset = Align8(checked(tokenSpanOffset + tokenSpanCount * tokenSpanSize));
        uint vectorCount = checked((uint)_vectors.Count);
        uint vectorBytes = checked(vectorCount * (uint)_vectorDim * sizeof(float));

        uint metadataCount = checked((uint)_metadata.Count);
        uint metadataTableOffset = metadataCount == 0 ? 0u : Align8(checked(vectorOffset + vectorBytes));
        uint totalBytes = metadataCount == 0
            ? checked(vectorOffset + vectorBytes)
            : checked(metadataTableOffset + metadataCount * metadataEntrySize);

        return new Layout(
            stringPoolOffset,
            stringPoolLength,
            astTableOffset,
            astNodeCount,
            astNodeSize,
            tokenSpanOffset,
            tokenSpanCount,
            tokenSpanSize,
            vectorOffset,
            vectorCount,
            metadataTableOffset,
            metadataCount,
            totalBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Align8(uint offset) => checked(offset + 7u) & ~7u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteZeroPad(Stream destination, ref long bytesWritten, uint targetOffset)
    {
        if (bytesWritten >= targetOffset) return;
        int pad = checked((int)(targetOffset - bytesWritten));
        while (pad > 0)
        {
            int chunk = Math.Min(pad, ZeroPadBytes.Length);
            destination.Write(ZeroPadBytes.AsSpan(0, chunk));
            bytesWritten += chunk;
            pad -= chunk;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendZeroPad(Crc32 crc, ref long offset, uint targetOffset)
    {
        if (offset >= targetOffset) return;
        int pad = checked((int)(targetOffset - offset));
        while (pad > 0)
        {
            int chunk = Math.Min(pad, ZeroPadBytes.Length);
            crc.Append(ZeroPadBytes.AsSpan(0, chunk));
            offset += chunk;
            pad -= chunk;
        }
    }
}
