using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Glacier.DocTree.Lexer;

/// <summary>
/// A strict 16-byte zero-allocation struct representing a bounded structural patch of text
/// for high-throughput SIMD chunking and decoupled context representation.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
public readonly struct StructuralPatch : IEquatable<StructuralPatch>
{
    /// <summary>Syntactic category of this patch (2 bytes).</summary>
    public readonly PatchType Type;

    /// <summary>Flag bitfield for metadata (e.g. newline-terminated, invariant context) (2 bytes).</summary>
    public readonly ushort Flags;

    /// <summary>Starting byte offset within the underlying UTF-8 source buffer (4 bytes).</summary>
    public readonly uint ByteOffset;

    /// <summary>Span length in bytes (2 bytes, bounded typically within [16, 64]).</summary>
    public readonly ushort ByteLength;

    /// <summary>Estimated equivalent number of standard subword/BPE tokens compressed into this patch (2 bytes).</summary>
    public readonly ushort TokenEquivEstimate;

    /// <summary>Fast 32-bit hash (e.g. XXHash32) of the patch content for O(1) deduplication and caching (4 bytes).</summary>
    public readonly uint HashCode;

    /// <summary>Alias property returning <see cref="ByteOffset"/> as a 64-bit integer.</summary>
    public ulong Offset => ByteOffset;

    /// <summary>Alias property returning <see cref="ByteLength"/>.</summary>
    public ushort Length => ByteLength;

    /// <summary>Alias property returning <see cref="Type"/>.</summary>
    public PatchType PatchType => Type;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StructuralPatch(PatchType type, ushort flags, uint byteOffset, ushort byteLength, ushort tokenEquivEstimate, uint hashCode)
    {
        Type = type;
        Flags = flags;
        ByteOffset = byteOffset;
        ByteLength = byteLength;
        TokenEquivEstimate = tokenEquivEstimate;
        HashCode = hashCode;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StructuralPatch(ulong offset, ushort length, ushort patchType, uint flags)
        : this((PatchType)patchType, (ushort)flags, (uint)offset, length, (ushort)Math.Max(1, length / 3), 0)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(StructuralPatch other) =>
        Type == other.Type &&
        Flags == other.Flags &&
        ByteOffset == other.ByteOffset &&
        ByteLength == other.ByteLength &&
        TokenEquivEstimate == other.TokenEquivEstimate &&
        HashCode == other.HashCode;

    public override bool Equals(object? obj) => obj is StructuralPatch other && Equals(other);

    public override int GetHashCode() => unchecked((int)HashCode != 0 ? (int)HashCode : System.HashCode.Combine((ushort)Type, ByteOffset, ByteLength));

    public static bool operator ==(StructuralPatch left, StructuralPatch right) => left.Equals(right);
    public static bool operator !=(StructuralPatch left, StructuralPatch right) => !left.Equals(right);

    public override string ToString() =>
        $"[StructuralPatch Type={Type}, Offset={ByteOffset}, Len={ByteLength}, Tokens~={TokenEquivEstimate}]";
}
