using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Glacier.DocTree.Lexer;
using Xunit;

namespace Glacier.DocTree.Tests;

public class SimdDocTreePatchChunkerAdversarialTests
{
    [Fact]
    public void StructuralPatch_UnmanagedStruct_Strictly16Bytes()
    {
        Assert.Equal(16, Unsafe.SizeOf<StructuralPatch>());
        Assert.Equal(16, Marshal.SizeOf<StructuralPatch>());
        Assert.Equal(16, Marshal.SizeOf(default(StructuralPatch)));

        // Verify layout packing
        var patch = new StructuralPatch(PatchType.Statement, 0x1234, 0x56789ABC, 0x40, 20, 0xDEADBEEF);
        Assert.Equal((ushort)PatchType.Statement, (ushort)patch.Type);
        Assert.Equal(0x1234, patch.Flags);
        Assert.Equal(0x56789ABCu, patch.ByteOffset);
        Assert.Equal((ushort)0x40, patch.ByteLength);
        Assert.Equal((ushort)20, patch.TokenEquivEstimate);
        Assert.Equal(0xDEADBEEFu, patch.HashCode);
    }

    [Fact]
    public void ChunkUtf8_ZeroLengthInputs_ReturnsZero()
    {
        Span<StructuralPatch> destination = stackalloc StructuralPatch[16];

        int count1 = SimdDocTreePatchChunker.ChunkUtf8(ReadOnlySpan<byte>.Empty, destination);
        Assert.Equal(0, count1);

        byte[] emptyArray = Array.Empty<byte>();
        int count2 = SimdDocTreePatchChunker.ChunkUtf8(emptyArray, destination);
        Assert.Equal(0, count2);

        // Also with zero-length destination span
        int count3 = SimdDocTreePatchChunker.ChunkUtf8(ReadOnlySpan<byte>.Empty, Span<StructuralPatch>.Empty);
        Assert.Equal(0, count3);
    }

    [Theory]
    [InlineData("a")]
    [InlineData(";")]
    [InlineData("\n")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void ChunkUtf8_SingleCharacterInputs_PreservesByteAndOffset(string charStr)
    {
        byte[] input = Encoding.UTF8.GetBytes(charStr);
        Span<StructuralPatch> destination = stackalloc StructuralPatch[16];

        int count = SimdDocTreePatchChunker.ChunkUtf8(input, destination);
        Assert.Equal(1, count);
        Assert.Equal(0u, destination[0].ByteOffset);
        Assert.Equal((ushort)1, destination[0].ByteLength);

        if (charStr == "{") Assert.Equal(PatchType.ScopeOpen, destination[0].Type);
        else if (charStr == "}") Assert.Equal(PatchType.ScopeClose, destination[0].Type);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(1000)]
    public void ChunkUtf8_NoDelimiters_ExactVectorBoundaries_PreservesReconstruction(int length)
    {
        byte[] input = new byte[length];
        Array.Fill(input, (byte)'x'); // All non-delimiter characters

        int maxPatches = SimdDocTreePatchChunker.EstimateMaxPatches(length);
        StructuralPatch[] patches = new StructuralPatch[maxPatches];

        int count = SimdDocTreePatchChunker.ChunkUtf8(input, patches);
        Assert.True(count > 0, $"Expected patches for length {length}");

        // Full reconstruction verification
        int currentOffset = 0;
        for (int i = 0; i < count; i++)
        {
            var p = patches[i];
            Assert.Equal((uint)currentOffset, p.ByteOffset);
            Assert.True(p.ByteLength > 0, $"Patch {i} length was 0");
            currentOffset += p.ByteLength;
        }
        Assert.Equal(length, currentOffset);
    }

    [Fact]
    public void ChunkUtf8_100PercentDelimiters_10kSemicolons()
    {
        const int size = 10_000;
        byte[] input = new byte[size];
        Array.Fill(input, (byte)';');

        int maxPatches = SimdDocTreePatchChunker.EstimateMaxPatches(size);
        StructuralPatch[] patches = new StructuralPatch[maxPatches];

        int count = SimdDocTreePatchChunker.ChunkUtf8(input, patches);
        Assert.True(count > 0);

        // Verify continuity and full coverage
        int currentOffset = 0;
        for (int i = 0; i < count; i++)
        {
            var p = patches[i];
            Assert.Equal((uint)currentOffset, p.ByteOffset);
            currentOffset += p.ByteLength;
        }
        Assert.Equal(size, currentOffset);
    }

    [Fact]
    public void ChunkUtf8_100PercentDelimiters_10kNewlines()
    {
        const int size = 10_000;
        byte[] input = new byte[size];
        Array.Fill(input, (byte)'\n');

        int maxPatches = SimdDocTreePatchChunker.EstimateMaxPatches(size);
        StructuralPatch[] patches = new StructuralPatch[maxPatches];

        int count = SimdDocTreePatchChunker.ChunkUtf8(input, patches);
        Assert.True(count > 0);

        int currentOffset = 0;
        for (int i = 0; i < count; i++)
        {
            var p = patches[i];
            Assert.Equal((uint)currentOffset, p.ByteOffset);
            currentOffset += p.ByteLength;
        }
        Assert.Equal(size, currentOffset);
    }

    [Fact]
    public void ChunkUtf8_100PercentDelimiters_10kBraces()
    {
        const int size = 10_000;
        byte[] input = new byte[size];
        for (int i = 0; i < size; i++)
        {
            input[i] = (byte)(i % 2 == 0 ? '{' : '}');
        }

        int maxPatches = SimdDocTreePatchChunker.EstimateMaxPatches(size);
        StructuralPatch[] patches = new StructuralPatch[maxPatches];

        int count = SimdDocTreePatchChunker.ChunkUtf8(input, patches);
        Assert.Equal(size, count); // Each brace is emitted as a 1-byte scope patch

        int currentOffset = 0;
        for (int i = 0; i < count; i++)
        {
            var p = patches[i];
            Assert.Equal((uint)currentOffset, p.ByteOffset);
            Assert.Equal(1, p.ByteLength);
            Assert.Equal(i % 2 == 0 ? PatchType.ScopeOpen : PatchType.ScopeClose, p.Type);
            currentOffset += p.ByteLength;
        }
        Assert.Equal(size, currentOffset);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(15)]
    [InlineData(31)]
    public void ChunkUtf8_UnalignedTailBuffers_DoesNotFaultOrCorrupt(int sliceOffset)
    {
        byte[] buffer = new byte[2048];
        string text = "public class UnalignedWorker\n{\n    private int _val = 100;\n    public void Run() { _val++; }\n}\n";
        byte[] pattern = Encoding.UTF8.GetBytes(text);
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = pattern[i % pattern.Length];
        }

        int[] testLengths = { 31, 32, 33, 63, 64, 65, 127, 128, 129, 512, 1024 };

        foreach (int testLen in testLengths)
        {
            ReadOnlySpan<byte> unalignedSpan = buffer.AsSpan(sliceOffset, testLen);
            int maxPatches = SimdDocTreePatchChunker.EstimateMaxPatches(testLen);
            StructuralPatch[] patches = new StructuralPatch[maxPatches];

            int count = SimdDocTreePatchChunker.ChunkUtf8(unalignedSpan, patches);
            Assert.True(count > 0, $"Failed for sliceOffset={sliceOffset}, testLen={testLen}");

            int covered = 0;
            for (int i = 0; i < count; i++)
            {
                Assert.Equal((uint)covered, patches[i].ByteOffset);
                covered += patches[i].ByteLength;
            }
            Assert.Equal(testLen, covered);
        }
    }

    [Fact]
    public void ChunkUtf8_StrictZeroHeapAllocation_HotPathStress()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            "public class ZeroAllocWorker\n{\n    private int _val = 100;\n    public void Run() { _val++; }\n}\n"
        );

        int max = SimdDocTreePatchChunker.EstimateMaxPatches(payload.Length);
        StructuralPatch[] buffer = new StructuralPatch[max];

        // JIT warm-up
        for (int i = 0; i < 50; i++)
        {
            SimdDocTreePatchChunker.ChunkUtf8(payload, buffer);
        }

        // Hot path zero-allocation stress
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();

        for (int iter = 0; iter < 1000; iter++)
        {
            int count = SimdDocTreePatchChunker.ChunkUtf8(payload, buffer);
            Assert.True(count > 0);
        }

        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        long diff = afterAlloc - beforeAlloc;

        Assert.Equal(0, diff); // 0 bytes allocated across 1000 iterations
    }

    [Fact]
    public void ChunkUtf8_DestinationSpanTooSmall_ThrowsArgumentException()
    {
        byte[] payload = Encoding.UTF8.GetBytes("var a = 1;\nvar b = 2;\nvar c = 3;\n");
        StructuralPatch[] tooSmall = new StructuralPatch[1];

        Assert.Throws<ArgumentException>(() =>
        {
            SimdDocTreePatchChunker.ChunkUtf8(payload, tooSmall);
        });
    }

    [Fact]
    public void ChunkUtf8_TwoDelimitersAtVariousDistances_NeverExceedMaxPatchLength()
    {
        for (int firstDelim = 16; firstDelim <= 64; firstDelim++)
        {
            for (int dist = 1; dist <= 100; dist++)
            {
                int secondDelim = firstDelim + dist;
                byte[] input = new byte[secondDelim + 50];
                Array.Fill(input, (byte)'x');
                input[firstDelim] = (byte)';';
                input[secondDelim] = (byte)';';

                int max = SimdDocTreePatchChunker.EstimateMaxPatches(input.Length);
                StructuralPatch[] patches = new StructuralPatch[max];
                int count = SimdDocTreePatchChunker.ChunkUtf8(input, patches);

                int total = 0;
                for (int i = 0; i < count; i++)
                {
                    Assert.True(patches[i].ByteLength <= SimdDocTreePatchChunker.MaxPatchLength,
                        $"Patch {i} of length {patches[i].ByteLength} exceeded MaxPatchLength {SimdDocTreePatchChunker.MaxPatchLength} when firstDelim={firstDelim}, secondDelim={secondDelim}");
                    Assert.Equal((uint)total, patches[i].ByteOffset);
                    total += patches[i].ByteLength;
                }
                Assert.Equal(input.Length, total);
            }
        }
    }

    [Theory]
    [InlineData("src/Glacier.DocTree/Binary/GdocReader.cs")]
    [InlineData("README.md")]
    [InlineData("src/Glacier.DocTree/Chunking/SimdDocTreePatchChunker.cs")]
    [InlineData("src/Glacier.DocTree/Context/GraphContextDecoupler.cs")]
    public void ChunkUtf8_RealFiles_NeverExceedMaxPatchLength(string relativePath)
    {
        string? current = AppContext.BaseDirectory;
        string? resolvedPath = null;
        while (current != null)
        {
            string candidate = System.IO.Path.Combine(current, relativePath.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(candidate))
            {
                resolvedPath = candidate;
                break;
            }
            current = System.IO.Directory.GetParent(current)?.FullName;
        }

        if (resolvedPath == null || !System.IO.File.Exists(resolvedPath))
        {
            byte[] syntheticPayload = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", System.Linq.Enumerable.Repeat("public void Process() { int a = 1; int b = 2; }", 200)));
            var syntheticPatches = SimdDocTreePatchChunker.ChunkUtf8(syntheticPayload);
            Assert.NotEmpty(syntheticPatches);
            return;
        }

        byte[] bytes = System.IO.File.ReadAllBytes(resolvedPath);
        var patches = SimdDocTreePatchChunker.ChunkUtf8(bytes);

        for (int i = 0; i < patches.Length; i++)
        {
            var p = patches[i];
            Assert.True(p.ByteLength <= SimdDocTreePatchChunker.MaxPatchLength,
                $"File {relativePath} patch {i} of length {p.ByteLength} exceeded MaxPatchLength {SimdDocTreePatchChunker.MaxPatchLength}");
        }
    }
}
