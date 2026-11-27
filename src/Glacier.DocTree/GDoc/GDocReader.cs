namespace Glacier.DocTree.GDoc;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

public sealed unsafe class GDocReader : IDisposable
{
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;
    private MemoryHandle _memoryHandle;
    private readonly bool _ownsMemoryHandle;
    private readonly byte* _pointer;
    private readonly long _length;
    private readonly bool _ownsPointer;

    private readonly GDocHeader _header;
    private bool _isDisposed;

    public GDocHeader Header => _header;
    public uint NodeCount => _header.AstNodeCount;
    public uint VectorCount => _header.VectorCount;
    public uint VectorDim => _header.VectorDim;

    public static GDocReader OpenFile(string filePath)
    {
        var fi = new FileInfo(filePath);
        var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, fi.Length, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

        return new GDocReader(mmf, accessor, default, ownsMemoryHandle: false, ptr, fi.Length, ownsPointer: true);
    }

    public static GDocReader FromMemory(ReadOnlyMemory<byte> memory)
    {
        var handle = memory.Pin();
        return new GDocReader(null, null, handle, ownsMemoryHandle: true, (byte*)handle.Pointer, memory.Length, ownsPointer: false);
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
            if (_length < 64)
            {
                throw new InvalidDataException("Invalid .gdoc file: length smaller than header size.");
            }

            _header = Unsafe.Read<GDocHeader>(_pointer);
            if (_header.Magic != GDocHeader.MagicValue)
            {
                throw new InvalidDataException($"Invalid .gdoc magic header: 0x{_header.Magic:X8}");
            }

            ulong totalLen = (ulong)_length;
            if (_header.StringPoolOffset > totalLen ||
                (ulong)_header.StringPoolOffset + _header.StringPoolLength > totalLen)
            {
                throw new InvalidDataException("Invalid .gdoc header: string pool extends beyond file length.");
            }

            if (_header.AstNodeCount > 0)
            {
                if (_header.AstNodeSize < (uint)sizeof(GDocAstNode))
                {
                    throw new InvalidDataException("Invalid .gdoc header: AST node stride smaller than GDocAstNode.");
                }
                ulong astEnd = checked((ulong)_header.AstTableOffset + (ulong)_header.AstNodeCount * _header.AstNodeSize);
                if (astEnd > totalLen)
                {
                    throw new InvalidDataException("Invalid .gdoc header: AST table extends beyond file length.");
                }
            }

            if (_header.TokenSpanCount > 0)
            {
                if (_header.TokenSpanSize < (uint)sizeof(GDocTokenSpan))
                {
                    throw new InvalidDataException("Invalid .gdoc header: token span stride smaller than GDocTokenSpan.");
                }
                ulong spanEnd = checked((ulong)_header.TokenSpanOffset + (ulong)_header.TokenSpanCount * _header.TokenSpanSize);
                if (spanEnd > totalLen)
                {
                    throw new InvalidDataException("Invalid .gdoc header: token span table extends beyond file length.");
                }
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
        catch
        {
            Dispose();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly GDocAstNode GetNode(uint id)
    {
        if (id >= _header.AstNodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        byte* nodePtr = _pointer + _header.AstTableOffset + ((ulong)id * _header.AstNodeSize);
        return ref Unsafe.AsRef<GDocAstNode>(nodePtr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetTextUtf8(in GDocAstNode node)
    {
        if (node.TextLength == 0) return ReadOnlySpan<byte>.Empty;
        ulong end = checked((ulong)_header.StringPoolOffset + node.TextOffset + node.TextLength);
        if (end > (ulong)_length)
        {
            throw new InvalidDataException("Invalid .gdoc AST node: text span extends beyond file length.");
        }
        byte* textPtr = _pointer + _header.StringPoolOffset + node.TextOffset;
        return new ReadOnlySpan<byte>(textPtr, node.TextLength);
    }

    public string GetText(in GDocAstNode node)
    {
        var span = GetTextUtf8(in node);
        return span.IsEmpty ? string.Empty : Encoding.UTF8.GetString(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<float> GetVector(in GDocAstNode node)
    {
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
        if (index >= _header.TokenSpanCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        byte* spanPtr = _pointer + _header.TokenSpanOffset + ((ulong)index * _header.TokenSpanSize);
        return ref Unsafe.AsRef<GDocTokenSpan>(spanPtr);
    }

    public List<uint> GetChildren(uint nodeId)
    {
        var result = new List<uint>();
        var visited = new HashSet<uint>();
        ref readonly var parent = ref GetNode(nodeId);
        uint curr = parent.FirstChildId;
        while (curr != GDocAstNode.NoNode)
        {
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
        var breadcrumbs = new List<uint>();
        var visited = new HashSet<uint>();
        uint curr = nodeId;
        while (curr != GDocAstNode.NoNode)
        {
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
        if (!_isDisposed)
        {
            _isDisposed = true;
            if (_ownsMemoryHandle)
            {
                _memoryHandle.Dispose();
            }
            if (_ownsPointer && _accessor != null)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _accessor.Dispose();
                _mmf?.Dispose();
            }
        }
    }
}
