using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Glacier.DocTree.Lexer;
using Xunit;

namespace Glacier.DocTree.Tests;

public class StructuralPatchChunkerTests
{
    [Fact]
    public void StructuralPatch_ExactSizeIs16Bytes()
    {
        Assert.Equal(16, Unsafe.SizeOf<StructuralPatch>());
        Assert.Equal(16, Marshal.SizeOf<StructuralPatch>());
    }

    [Fact]
    public void StructuralPatch_FieldPropertiesAndEquality()
    {
        var patch1 = new StructuralPatch(PatchType.Statement, 0x01, 100, 32, 10, 0xABCDEF01);
        Assert.Equal(PatchType.Statement, patch1.Type);
        Assert.Equal(PatchType.Statement, patch1.PatchType);
        Assert.Equal(0x01, patch1.Flags);
        Assert.Equal(100u, patch1.ByteOffset);
        Assert.Equal(100ul, patch1.Offset);
        Assert.Equal((ushort)32, patch1.ByteLength);
        Assert.Equal((ushort)32, patch1.Length);
        Assert.Equal((ushort)10, patch1.TokenEquivEstimate);
        Assert.Equal(0xABCDEF01u, patch1.HashCode);

        var patch2 = new StructuralPatch(PatchType.Statement, 0x01, 100, 32, 10, 0xABCDEF01);
        Assert.Equal(patch1, patch2);
        Assert.True(patch1 == patch2);
        Assert.False(patch1 != patch2);
        Assert.Equal(patch1.GetHashCode(), patch2.GetHashCode());

        var patch3 = new StructuralPatch(PatchType.Declaration, 0, 100, 32, 10, 0);
        Assert.NotEqual(patch1, patch3);
    }

    [Fact]
    public void ChunkUtf8_EmptyInput_ReturnsZero()
    {
        Span<StructuralPatch> destination = stackalloc StructuralPatch[10];
        int count = SimdDocTreePatchChunker.ChunkUtf8(ReadOnlySpan<byte>.Empty, destination);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ChunkUtf8_ZeroAllocationOnHotPath()
    {
        byte[] input = Encoding.UTF8.GetBytes(
            "public class ZeroAllocWorker\n{\n    private int _val = 100;\n    public void Run() { _val++; }\n}\n"
        );

        int max = SimdDocTreePatchChunker.EstimateMaxPatches(input.Length);
        StructuralPatch[] preallocatedBuffer = new StructuralPatch[max];

        // Warm up JIT
        SimdDocTreePatchChunker.ChunkUtf8(input, preallocatedBuffer);

        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        int patchCount = SimdDocTreePatchChunker.ChunkUtf8(input, preallocatedBuffer);
        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();

        long diff = afterAlloc - beforeAlloc;
        Assert.Equal(0, diff); // Strict 0-byte heap allocation on hot path
        Assert.True(patchCount > 0);
    }

    [Fact]
    public void ChunkUtf8_PreservesFullSourceContentWithoutLoss()
    {
        string sourceText = @"public class DataPipeline
{
    private readonly int _batchSize;
    public DataPipeline(int batchSize)
    {
        _batchSize = batchSize;
    }

    public void Process()
    {
        var result = ComputeKernel.Execute();
        Console.WriteLine(result);
    }
}";
        byte[] utf8 = Encoding.UTF8.GetBytes(sourceText);
        var patches = SimdDocTreePatchChunker.ChunkUtf8(utf8);

        Assert.NotEmpty(patches);

        // Verify that concatenating all patch slices reconstructs the entire text byte-for-byte
        int currentOffset = 0;
        foreach (var p in patches)
        {
            Assert.Equal((uint)currentOffset, p.ByteOffset);
            currentOffset += p.ByteLength;
        }
        Assert.Equal(utf8.Length, currentOffset);
    }

    [Fact]
    public void ChunkUtf8_CorrectlyClassifiesDeclarationsAndScopes()
    {
        string csharpCode = "public class OrderProcessor\n{\n    private int count;\n}\n";
        byte[] utf8 = Encoding.UTF8.GetBytes(csharpCode);
        var patches = SimdDocTreePatchChunker.ChunkUtf8(utf8);

        Assert.Contains(patches, p => p.Type == PatchType.Declaration);
        Assert.Contains(patches, p => p.Type == PatchType.ScopeOpen);
        Assert.Contains(patches, p => p.Type == PatchType.ScopeClose);
    }

    [Fact]
    public void ChunkUtf8_CorrectlyClassifiesMarkdownHeaders()
    {
        string markdown = "# Architecture Overview\n\nThis is document body.\n\n## Subsystem Details\n";
        byte[] utf8 = Encoding.UTF8.GetBytes(markdown);
        var patches = SimdDocTreePatchChunker.ChunkUtf8(utf8);

        Assert.Contains(patches, p => p.Type == PatchType.DocHeader);
    }

    [Fact]
    public void ChunkUtf8_CorrectlyClassifiesKeyValuePairs()
    {
        string json = "{\n  \"device_id\": 1024,\n  \"status\": \"active\"\n}\n";
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        var patches = SimdDocTreePatchChunker.ChunkUtf8(utf8);

        Assert.Contains(patches, p => p.Type == PatchType.KeyValuePair);
    }

    [Fact]
    public void ChunkUtf8_BoundedPatchLengths()
    {
        // Generate a 500-byte single statement without delimiters
        byte[] longText = new byte[500];
        Array.Fill(longText, (byte)'x');
        longText[499] = (byte)';';

        var patches = SimdDocTreePatchChunker.ChunkUtf8(longText);
        Assert.NotEmpty(patches);

        foreach (var p in patches)
        {
            Assert.True(p.ByteLength <= SimdDocTreePatchChunker.MaxPatchLength,
                $"Patch length {p.ByteLength} exceeded MaxPatchLength {SimdDocTreePatchChunker.MaxPatchLength}");
        }
    }

    [Fact]
    public void MeasureThroughput_AchievesHighThroughput()
    {
        // 64 KB repetitive payload to measure SIMD throughput
        byte[] largePayload = new byte[64 * 1024];
        byte[] pattern = Encoding.UTF8.GetBytes("var x = ProcessBatch(buffer, 4096);\npublic class Item { int id; }\n");
        for (int i = 0; i < largePayload.Length; i++)
        {
            largePayload[i] = pattern[i % pattern.Length];
        }

        double chunkerGbPerSec = SimdDocTreePatchChunker.MeasureThroughputGbPerSecond(largePayload, iterations: 100);
        Assert.True(chunkerGbPerSec > 0.2, $"Chunker throughput was {chunkerGbPerSec:F2} GB/s");

        double rawScanGbPerSec = SimdDocTreePatchChunker.MeasureRawSimdScanBandwidthGbPerSecond(largePayload, iterations: 1000);
        Assert.True(rawScanGbPerSec > 10.0, $"Raw SIMD scan throughput was {rawScanGbPerSec:F2} GB/s");
    }

    [Fact]
    public void ChunkUtf8_HandlesMultipleStatementsOnSingleLine()
    {
        string text = "int a = 1; int b = 2; int c = 3; int d = 4;\n";
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        var patches = SimdDocTreePatchChunker.ChunkUtf8(utf8);

        Assert.NotEmpty(patches);
        Assert.All(patches, p => Assert.True(p.ByteLength <= SimdDocTreePatchChunker.MaxPatchLength));
    }

    [Fact]
    public void ChunkUtf8_DeterministicFnv1aHashCode()
    {
        string text = "public class DeterministicWorker { void DoWork(); }\n";
        byte[] utf8 = Encoding.UTF8.GetBytes(text);

        var patches1 = SimdDocTreePatchChunker.ChunkUtf8(utf8);
        var patches2 = SimdDocTreePatchChunker.ChunkUtf8(utf8);

        Assert.Equal(patches1.Length, patches2.Length);
        for (int i = 0; i < patches1.Length; i++)
        {
            Assert.Equal(patches1[i].HashCode, patches2[i].HashCode);
            Assert.Equal(patches1[i].ByteOffset, patches2[i].ByteOffset);
            Assert.Equal(patches1[i].ByteLength, patches2[i].ByteLength);
        }
    }

    [Fact]
    public void ChunkText_HelperMatchesChunkUtf8()
    {
        string text = "var model = LoadWeights(path);\nvar tokens = Tokenize(prompt);\n";
        var patchesFromText = SimdDocTreePatchChunker.ChunkText(text);
        var patchesFromUtf8 = SimdDocTreePatchChunker.ChunkUtf8(Encoding.UTF8.GetBytes(text));

        Assert.Equal(patchesFromUtf8.Length, patchesFromText.Length);
        for (int i = 0; i < patchesFromUtf8.Length; i++)
        {
            Assert.Equal(patchesFromUtf8[i], patchesFromText[i]);
        }
    }
}
