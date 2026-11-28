using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace Glacier.DocTree.Lexer;

/// <summary>
/// Hardware-accelerated SIMD scanner utilizing Vector512 / Vector256 / Vector128 intrinsics to chunk raw UTF-8 byte streams
/// into bounded structural patches with zero heap allocations.
/// </summary>
public static class SimdDocTreePatchChunker
{
    public const int MinPatchLength = 16;
    public const int MaxPatchLength = 64;

    private interface IPatchSink
    {
        void Emit(ref byte baseRef, int offset, int length, PatchType type);
        int Count { get; }
    }

    private struct CountingPatchSink : IPatchSink
    {
        private int _count;
        public readonly int Count => _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Emit(ref byte baseRef, int offset, int length, PatchType type)
        {
            if (length > 0)
            {
                _count++;
            }
        }
    }

    private ref struct SpanPatchSink : IPatchSink
    {
        private readonly Span<StructuralPatch> _destination;
        private int _count;
        public readonly int Count => _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SpanPatchSink(Span<StructuralPatch> destination)
        {
            _destination = destination;
            _count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Emit(ref byte baseRef, int offset, int length, PatchType type)
        {
            if (length <= 0) return;
            if (_count >= _destination.Length)
            {
                ThrowDestinationOverflow();
            }

            ReadOnlySpan<byte> span = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref baseRef, offset), length);
            uint hash = ComputeFnv1a(span);
            ushort tokenEstimate = (ushort)Math.Max(1, (length + 2) / 3);

            _destination[_count++] = new StructuralPatch(
                type,
                flags: 0,
                (uint)offset,
                (ushort)length,
                tokenEstimate,
                hash);
        }
    }

    /// <summary>
    /// Scans the provided UTF-8 byte span and writes structural patches into the destination span.
    /// Hot path is 100% zero-allocation.
    /// </summary>
    /// <param name="utf8Source">Input UTF-8 source buffer.</param>
    /// <param name="destinationPatches">Caller-provided destination patch span.</param>
    /// <returns>Number of structural patches generated.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int ChunkUtf8(ReadOnlySpan<byte> utf8Source, Span<StructuralPatch> destinationPatches)
    {
        if (utf8Source.IsEmpty) return 0;
        var sink = new SpanPatchSink(destinationPatches);
        return ChunkUtf8Core(utf8Source, ref sink);
    }

    /// <summary>
    /// Counts the exact number of structural patches that <see cref="ChunkUtf8(ReadOnlySpan{byte}, Span{StructuralPatch})"/>
    /// would emit for the given UTF-8 input with strictly zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int CountPatches(ReadOnlySpan<byte> utf8Source)
    {
        if (utf8Source.IsEmpty) return 0;
        var sink = new CountingPatchSink();
        return ChunkUtf8Core(utf8Source, ref sink);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ChunkUtf8Core<TSink>(ReadOnlySpan<byte> utf8Source, ref TSink sink)
        where TSink : IPatchSink, allows ref struct
    {
        ref byte baseRef = ref MemoryMarshal.GetReference(utf8Source);
        int length = utf8Source.Length;
        int currentStart = 0;
        int pendingDelimPos = -1;
        int i = 0;

        // --- Hardware SIMD Vector512 Path (AVX-512) ---
        if (Vector512.IsHardwareAccelerated && length >= Vector512<byte>.Count)
        {
            var vecNl = Vector512.Create((byte)'\n');
            var vecSemi = Vector512.Create((byte)';');
            var vecOpenBrace = Vector512.Create((byte)'{');
            var vecCloseBrace = Vector512.Create((byte)'}');

            int v512Limit = length - Vector512<byte>.Count;
            for (; i <= v512Limit; i += Vector512<byte>.Count)
            {
                var chunk = Vector512.LoadUnsafe(ref baseRef, (nuint)i);
                var matches = Vector512.Equals(chunk, vecNl) |
                              Vector512.Equals(chunk, vecSemi) |
                              Vector512.Equals(chunk, vecOpenBrace) |
                              Vector512.Equals(chunk, vecCloseBrace);

                ulong mask = matches.ExtractMostSignificantBits();
                while (mask != 0)
                {
                    int bitIndex = BitOperations.TrailingZeroCount(mask);
                    int delimiterPos = i + bitIndex;

                    ProcessDelimiter(ref baseRef, delimiterPos, ref currentStart, ref pendingDelimPos, ref sink);

                    mask &= (mask - 1);
                }

                while (i + Vector512<byte>.Count - currentStart >= MaxPatchLength)
                {
                    FlushPendingOrForced(ref baseRef, ref currentStart, ref pendingDelimPos, ref sink);
                }
            }
        }
        // --- Hardware SIMD Vector256 Path (AVX2) ---
        else if (Vector256.IsHardwareAccelerated && length >= Vector256<byte>.Count)
        {
            var vecNl = Vector256.Create((byte)'\n');
            var vecSemi = Vector256.Create((byte)';');
            var vecOpenBrace = Vector256.Create((byte)'{');
            var vecCloseBrace = Vector256.Create((byte)'}');

            int v256Limit = length - Vector256<byte>.Count;
            for (; i <= v256Limit; i += Vector256<byte>.Count)
            {
                var chunk = Vector256.LoadUnsafe(ref baseRef, (nuint)i);
                var matches = Vector256.Equals(chunk, vecNl) |
                              Vector256.Equals(chunk, vecSemi) |
                              Vector256.Equals(chunk, vecOpenBrace) |
                              Vector256.Equals(chunk, vecCloseBrace);

                uint mask = matches.ExtractMostSignificantBits();
                while (mask != 0)
                {
                    int bitIndex = BitOperations.TrailingZeroCount(mask);
                    int delimiterPos = i + bitIndex;

                    ProcessDelimiter(ref baseRef, delimiterPos, ref currentStart, ref pendingDelimPos, ref sink);

                    mask &= (mask - 1);
                }

                while (i + Vector256<byte>.Count - currentStart >= MaxPatchLength)
                {
                    FlushPendingOrForced(ref baseRef, ref currentStart, ref pendingDelimPos, ref sink);
                }
            }
        }
        // --- Hardware SIMD Vector128 Path (SSE2 / NEON) ---
        else if (Vector128.IsHardwareAccelerated && length >= Vector128<byte>.Count)
        {
            var vecNl = Vector128.Create((byte)'\n');
            var vecSemi = Vector128.Create((byte)';');
            var vecOpenBrace = Vector128.Create((byte)'{');
            var vecCloseBrace = Vector128.Create((byte)'}');

            int v128Limit = length - Vector128<byte>.Count;
            for (; i <= v128Limit; i += Vector128<byte>.Count)
            {
                var chunk = Vector128.LoadUnsafe(ref baseRef, (nuint)i);
                var matches = Vector128.Equals(chunk, vecNl) |
                              Vector128.Equals(chunk, vecSemi) |
                              Vector128.Equals(chunk, vecOpenBrace) |
                              Vector128.Equals(chunk, vecCloseBrace);

                uint mask = matches.ExtractMostSignificantBits();
                while (mask != 0)
                {
                    int bitIndex = BitOperations.TrailingZeroCount(mask);
                    int delimiterPos = i + bitIndex;

                    ProcessDelimiter(ref baseRef, delimiterPos, ref currentStart, ref pendingDelimPos, ref sink);

                    mask &= (mask - 1);
                }

                while (i + Vector128<byte>.Count - currentStart >= MaxPatchLength)
                {
                    FlushPendingOrForced(ref baseRef, ref currentStart, ref pendingDelimPos, ref sink);
                }
            }
        }

        // --- Scalar Tail Processing ---
        for (; i < length; i++)
        {
            byte b = Unsafe.Add(ref baseRef, i);
            if (b is (byte)'\n' or (byte)';' or (byte)'{' or (byte)'}')
            {
                ProcessDelimiter(ref baseRef, i, ref currentStart, ref pendingDelimPos, ref sink);
            }
            else if (i - currentStart >= MaxPatchLength)
            {
                FlushPendingOrForced(ref baseRef, ref currentStart, ref pendingDelimPos, ref sink);
            }
        }

        // Final tail flush
        if (currentStart < length)
        {
            if (pendingDelimPos >= currentStart)
            {
                FlushPendingDelimiter(ref baseRef, ref currentStart, ref pendingDelimPos, ref sink);
            }

            int remaining = length - currentStart;
            while (remaining > 0)
            {
                int len = Math.Min(remaining, MaxPatchLength);
                PatchType tailType = ClassifyFast(ref baseRef, currentStart, len, delim: 0);
                sink.Emit(ref baseRef, currentStart, len, tailType);
                currentStart += len;
                remaining -= len;
            }
        }

        return sink.Count;
    }

    /// <summary>
    /// Pure raw SIMD byte-scan kernel measuring hardware bus scanning speed
    /// without structural patch table writes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int ScanDelimitersRawSimd(ReadOnlySpan<byte> utf8Source)
    {
        if (utf8Source.IsEmpty) return 0;

        ref byte baseRef = ref MemoryMarshal.GetReference(utf8Source);
        int length = utf8Source.Length;
        int delimiterCount = 0;
        int i = 0;

        if (Vector512.IsHardwareAccelerated && length >= Vector512<byte>.Count)
        {
            var vecNl = Vector512.Create((byte)'\n');
            var vecSemi = Vector512.Create((byte)';');
            var vecOpen = Vector512.Create((byte)'{');
            var vecClose = Vector512.Create((byte)'}');

            int v512Limit = length - Vector512<byte>.Count;
            for (; i <= v512Limit; i += Vector512<byte>.Count)
            {
                var chunk = Vector512.LoadUnsafe(ref baseRef, (nuint)i);
                var matches = Vector512.Equals(chunk, vecNl) |
                              Vector512.Equals(chunk, vecSemi) |
                              Vector512.Equals(chunk, vecOpen) |
                              Vector512.Equals(chunk, vecClose);
                ulong mask = matches.ExtractMostSignificantBits();
                delimiterCount += BitOperations.PopCount(mask);
            }
        }
        else if (Vector256.IsHardwareAccelerated && length >= Vector256<byte>.Count)
        {
            var vecNl = Vector256.Create((byte)'\n');
            var vecSemi = Vector256.Create((byte)';');
            var vecOpen = Vector256.Create((byte)'{');
            var vecClose = Vector256.Create((byte)'}');

            int v256Limit = length - Vector256<byte>.Count;
            for (; i <= v256Limit; i += Vector256<byte>.Count)
            {
                var chunk = Vector256.LoadUnsafe(ref baseRef, (nuint)i);
                var matches = Vector256.Equals(chunk, vecNl) |
                              Vector256.Equals(chunk, vecSemi) |
                              Vector256.Equals(chunk, vecOpen) |
                              Vector256.Equals(chunk, vecClose);
                uint mask = matches.ExtractMostSignificantBits();
                delimiterCount += BitOperations.PopCount(mask);
            }
        }
        else if (Vector128.IsHardwareAccelerated && length >= Vector128<byte>.Count)
        {
            var vecNl = Vector128.Create((byte)'\n');
            var vecSemi = Vector128.Create((byte)';');
            var vecOpen = Vector128.Create((byte)'{');
            var vecClose = Vector128.Create((byte)'}');

            int v128Limit = length - Vector128<byte>.Count;
            for (; i <= v128Limit; i += Vector128<byte>.Count)
            {
                var chunk = Vector128.LoadUnsafe(ref baseRef, (nuint)i);
                var matches = Vector128.Equals(chunk, vecNl) |
                              Vector128.Equals(chunk, vecSemi) |
                              Vector128.Equals(chunk, vecOpen) |
                              Vector128.Equals(chunk, vecClose);
                uint mask = matches.ExtractMostSignificantBits();
                delimiterCount += BitOperations.PopCount(mask);
            }
        }

        for (; i < length; i++)
        {
            byte b = Unsafe.Add(ref baseRef, i);
            if (b is (byte)'\n' or (byte)';' or (byte)'{' or (byte)'}')
            {
                delimiterCount++;
            }
        }

        return delimiterCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FlushPendingDelimiter<TSink>(
        ref byte baseRef,
        ref int currentStart,
        ref int pendingDelimPos,
        ref TSink sink)
        where TSink : IPatchSink, allows ref struct
    {
        int pendingLen = pendingDelimPos - currentStart + 1;
        byte pendingDelim = Unsafe.Add(ref baseRef, pendingDelimPos);
        PatchType pendingType = ClassifyFast(ref baseRef, currentStart, pendingLen, pendingDelim);
        sink.Emit(ref baseRef, currentStart, pendingLen, pendingType);
        currentStart = pendingDelimPos + 1;
        pendingDelimPos = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FlushPendingOrForced<TSink>(
        ref byte baseRef,
        ref int currentStart,
        ref int pendingDelimPos,
        ref TSink sink)
        where TSink : IPatchSink, allows ref struct
    {
        if (pendingDelimPos >= currentStart)
        {
            FlushPendingDelimiter(ref baseRef, ref currentStart, ref pendingDelimPos, ref sink);
            return;
        }

        EmitForcedPatch(ref baseRef, ref currentStart, MaxPatchLength, ref sink);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessDelimiter<TSink>(
        ref byte baseRef,
        int delimiterPos,
        ref int currentStart,
        ref int pendingDelimPos,
        ref TSink sink)
        where TSink : IPatchSink, allows ref struct
    {
        while (delimiterPos - currentStart >= MaxPatchLength)
        {
            FlushPendingOrForced(ref baseRef, ref currentStart, ref pendingDelimPos, ref sink);
        }

        byte delim = Unsafe.Add(ref baseRef, delimiterPos);
        bool isScope = delim is (byte)'{' or (byte)'}';

        if (isScope)
        {
            if (pendingDelimPos >= currentStart)
            {
                FlushPendingDelimiter(ref baseRef, ref currentStart, ref pendingDelimPos, ref sink);
            }

            if (delimiterPos > currentStart)
            {
                int preLen = delimiterPos - currentStart;
                PatchType preType = ClassifyFast(ref baseRef, currentStart, preLen, delim: 0);
                sink.Emit(ref baseRef, currentStart, preLen, preType);
            }

            PatchType scopeType = delim == (byte)'{' ? PatchType.ScopeOpen : PatchType.ScopeClose;
            sink.Emit(ref baseRef, delimiterPos, 1, scopeType);
            currentStart = delimiterPos + 1;
            pendingDelimPos = -1;
            return;
        }

        int segmentLen = delimiterPos - currentStart + 1;
        if (segmentLen >= MinPatchLength)
        {
            PatchType pType = ClassifyFast(ref baseRef, currentStart, segmentLen, delim);
            sink.Emit(ref baseRef, currentStart, segmentLen, pType);
            currentStart = delimiterPos + 1;
            pendingDelimPos = -1;
        }
        else
        {
            pendingDelimPos = delimiterPos;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitForcedPatch<TSink>(
        ref byte baseRef,
        ref int currentStart,
        int length,
        ref TSink sink)
        where TSink : IPatchSink, allows ref struct
    {
        PatchType pType = ClassifyFast(ref baseRef, currentStart, length, delim: 0);
        sink.Emit(ref baseRef, currentStart, length, pType);
        currentStart += length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PatchType ClassifyFast(ref byte baseRef, int offset, int length, byte delim)
    {
        if (length <= 0) return PatchType.VerbatimText;
        if (delim == (byte)'{') return PatchType.ScopeOpen;
        if (delim == (byte)'}') return PatchType.ScopeClose;

        // Skip leading whitespace to find first significant byte
        int k = 0;
        while (k < length && Unsafe.Add(ref baseRef, offset + k) is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            k++;
        }
        if (k >= length) return PatchType.IndentationCluster;

        byte first = Unsafe.Add(ref baseRef, offset + k);
        if (first == (byte)'#') return PatchType.DocHeader;

        // Quick check for JSON key-value pair quotes
        if (first is (byte)'"' or (byte)'\'')
        {
            return PatchType.KeyValuePair;
        }

        // O(1) keyword hint check
        if (first is (byte)'p' or (byte)'c' or (byte)'s' or (byte)'i' or (byte)'f' or (byte)'d')
        {
            if (StartsWithKeyword(ref baseRef, offset + k, length - k))
            {
                return PatchType.Declaration;
            }
        }

        if (delim is (byte)';' or (byte)'\n')
        {
            return PatchType.Statement;
        }

        return PatchType.VerbatimText;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentifierByte(byte b) =>
        (uint)(b - 'a') <= 'z' - 'a' ||
        (uint)(b - 'A') <= 'Z' - 'A' ||
        (uint)(b - '0') <= '9' - '0' ||
        b == (byte)'_';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWordBoundaryAfter(ref byte startRef, int rem, int kwLen) =>
        rem == kwLen || !IsIdentifierByte(Unsafe.Add(ref startRef, kwLen));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StartsWithKeyword(ref byte baseRef, int offset, int length)
    {
        int k = 0;
        while (k < length && Unsafe.Add(ref baseRef, offset + k) is (byte)' ' or (byte)'\t')
        {
            k++;
        }
        int rem = length - k;
        if (rem < 3) return false;

        ref byte startRef = ref Unsafe.Add(ref baseRef, offset + k);

        if ((rem >= 6 && Matches6(ref startRef, "public") && IsWordBoundaryAfter(ref startRef, rem, 6)) ||
            (rem >= 7 && Matches7(ref startRef, "private") && IsWordBoundaryAfter(ref startRef, rem, 7)) ||
            (rem >= 8 && Matches8(ref startRef, "internal") && IsWordBoundaryAfter(ref startRef, rem, 8)) ||
            (rem >= 5 && Matches5(ref startRef, "class") && IsWordBoundaryAfter(ref startRef, rem, 5)) ||
            (rem >= 6 && Matches6(ref startRef, "struct") && IsWordBoundaryAfter(ref startRef, rem, 6)) ||
            (rem >= 4 && Matches4(ref startRef, "func") && IsWordBoundaryAfter(ref startRef, rem, 4)) ||
            (rem >= 3 && Matches3(ref startRef, "def") && IsWordBoundaryAfter(ref startRef, rem, 3)))
        {
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Matches3(ref byte b, string s) =>
        Unsafe.Add(ref b, 0) == (byte)s[0] &&
        Unsafe.Add(ref b, 1) == (byte)s[1] &&
        Unsafe.Add(ref b, 2) == (byte)s[2];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Matches4(ref byte b, string s) =>
        Unsafe.Add(ref b, 0) == (byte)s[0] &&
        Unsafe.Add(ref b, 1) == (byte)s[1] &&
        Unsafe.Add(ref b, 2) == (byte)s[2] &&
        Unsafe.Add(ref b, 3) == (byte)s[3];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Matches5(ref byte b, string s) =>
        Matches4(ref b, s) && Unsafe.Add(ref b, 4) == (byte)s[4];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Matches6(ref byte b, string s) =>
        Matches5(ref b, s) && Unsafe.Add(ref b, 5) == (byte)s[5];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Matches7(ref byte b, string s) =>
        Matches6(ref b, s) && Unsafe.Add(ref b, 6) == (byte)s[6];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Matches8(ref byte b, string s) =>
        Matches7(ref b, s) && Unsafe.Add(ref b, 7) == (byte)s[7];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ComputeFnv1a(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            uint hash = 2166136261;
            int i = 0;
            int limit = data.Length - 4;
            for (; i <= limit; i += 4)
            {
                uint val = MemoryMarshal.Read<uint>(data.Slice(i));
                hash = (hash ^ val) * 16777619;
            }
            for (; i < data.Length; i++)
            {
                hash = (hash ^ data[i]) * 16777619;
            }
            return hash;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EstimateMaxPatches(int byteLength) => Math.Max(16, byteLength + 4);

    public static StructuralPatch[] ChunkUtf8(ReadOnlySpan<byte> utf8Source)
    {
        int max = EstimateMaxPatches(utf8Source.Length);
        StructuralPatch[] buffer = new StructuralPatch[max];
        int count = ChunkUtf8(utf8Source, buffer);
        if (count == max) return buffer;

        StructuralPatch[] result = new StructuralPatch[count];
        buffer.AsSpan(0, count).CopyTo(result);
        return result;
    }

    public static StructuralPatch[] ChunkText(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return ChunkUtf8(utf8);
    }

    /// <summary>
    /// Benchmarks the throughput of the SIMD chunker in GB/s on the physical CPU.
    /// </summary>
    public static double MeasureThroughputGbPerSecond(ReadOnlySpan<byte> utf8Source, int iterations = 100)
    {
        if (utf8Source.IsEmpty) return 0.0;
        int max = EstimateMaxPatches(utf8Source.Length);
        StructuralPatch[] buffer = new StructuralPatch[max];

        // Warmup
        for (int w = 0; w < 10; w++)
        {
            ChunkUtf8(utf8Source, buffer);
        }

        var sw = Stopwatch.StartNew();
        for (int it = 0; it < iterations; it++)
        {
            ChunkUtf8(utf8Source, buffer);
        }
        sw.Stop();

        double elapsedSec = sw.Elapsed.TotalSeconds;
        double totalBytes = (double)utf8Source.Length * iterations;
        double gb = totalBytes / (1024.0 * 1024.0 * 1024.0);
        return gb / elapsedSec;
    }

    /// <summary>
    /// Benchmarks raw hardware SIMD scanning speed across memory without patch allocation overhead.
    /// </summary>
    public static double MeasureRawSimdScanBandwidthGbPerSecond(ReadOnlySpan<byte> utf8Source, int iterations = 1000)
    {
        if (utf8Source.IsEmpty) return 0.0;

        for (int w = 0; w < 20; w++)
        {
            ScanDelimitersRawSimd(utf8Source);
        }

        var sw = Stopwatch.StartNew();
        for (int it = 0; it < iterations; it++)
        {
            ScanDelimitersRawSimd(utf8Source);
        }
        sw.Stop();

        double elapsedSec = sw.Elapsed.TotalSeconds;
        double totalBytes = (double)utf8Source.Length * iterations;
        double gb = totalBytes / (1024.0 * 1024.0 * 1024.0);
        return gb / elapsedSec;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDestinationOverflow() =>
        throw new ArgumentException("Destination patches span buffer was too small.");
}
