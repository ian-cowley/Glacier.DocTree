using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Glacier.DocTree.Core;

/// <summary>
/// Zero-heap-allocation (for trees up to depth 64) pooled scratch stack for iterative AST traversals.
/// Clears popped and returned reference slots so the GC never retains dead <see cref="DocNode"/> graphs.
/// </summary>
internal ref struct ValueDocNodeStack<T>
{
    private const int InlineCapacity = 64;

    [InlineArray(InlineCapacity)]
    private struct InlineBuffer
    {
        private T _element0;
    }

    private InlineBuffer _inline;
    private T[]? _rented;
    private int _count;

    public readonly int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(T item)
    {
        int idx = _count;
        if (_rented == null)
        {
            if (idx < InlineCapacity)
            {
                _inline[idx] = item;
                _count = idx + 1;
                return;
            }
            Grow();
        }
        else if (idx == _rented.Length)
        {
            Grow();
        }

        _rented![idx] = item;
        _count = idx + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Pop()
    {
        int idx = --_count;
        if (_rented == null)
        {
            T item = _inline[idx];
            _inline[idx] = default!;
            return item;
        }
        else
        {
            T item = _rented[idx];
            _rented[idx] = default!;
            return item;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        int newCap = _rented == null ? 128 : checked(_rented.Length * 2);
        T[] next = ArrayPool<T>.Shared.Rent(newCap);
        if (_rented == null)
        {
            ((ReadOnlySpan<T>)_inline)[.._count].CopyTo(next);
            ((Span<T>)_inline).Clear();
        }
        else
        {
            _rented.AsSpan(0, _count).CopyTo(next);
            ArrayPool<T>.Shared.Return(_rented, clearArray: true);
        }
        _rented = next;
    }

    public void Dispose()
    {
        if (_rented != null)
        {
            ArrayPool<T>.Shared.Return(_rented, clearArray: true);
            _rented = null;
        }
        else if (_count > 0)
        {
            ((Span<T>)_inline)[.._count].Clear();
        }
        _count = 0;
    }
}
