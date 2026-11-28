namespace Glacier.DocTree.Tests;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Glacier.DocTree.Binary;
using Glacier.DocTree.Core;
using Glacier.DocTree.GDoc;
using Xunit;

public class GDocTests
{
    [Fact]
    public void GDoc_StructLayouts_MatchUnifiedV2Spec()
    {
        Assert.Equal(64, Unsafe.SizeOf<GDocHeader>());
        Assert.Equal(48, Unsafe.SizeOf<GDocAstNode>());
        Assert.Equal(16, Unsafe.SizeOf<GDocTokenSpan>());
        Assert.Equal(16, Marshal.OffsetOf<GDocAstNode>(nameof(GDocAstNode.NextSiblingId)).ToInt32());
    }

    [Fact]
    public void GDoc_WriteAndReadMemory_Roundtrip()
    {
        var writer = new GDocWriter(vectorDim: 4);

        uint root = writer.AddNode(GDocNodeType.Document, "Document Title");
        writer.AddMetadata(root, "author", "Glacier");

        uint sec1 = writer.AddNode(GDocNodeType.Section, "Introduction");
        writer.LinkChild(root, sec1);

        float[] vec = [0.5f, 0.5f, 0.5f, 0.5f];
        uint vecIdx = writer.AddVector(vec);
        uint p1 = writer.AddNode(GDocNodeType.Paragraph, "This is the first paragraph.", vectorIndex: vecIdx);
        writer.LinkChild(sec1, p1);

        uint code = writer.AddNode(GDocNodeType.CodeBlock, "Console.WriteLine(\"Hello Glacier\");");
        writer.LinkChild(sec1, code);

        byte[] gdocBytes = writer.ToByteArray();
        Assert.NotNull(gdocBytes);
        Assert.True(gdocBytes.Length >= 64);

        using var reader = GDocReader.FromMemory(gdocBytes);
        Assert.Equal(GDocHeader.CurrentVersion, reader.Header.Version);
        Assert.Equal(4u, reader.NodeCount);
        Assert.Equal(1u, reader.VectorCount);
        Assert.Equal(4u, reader.VectorDim);
        Assert.Equal(1u, reader.MetadataCount);

        ref readonly var rootNode = ref reader.GetNode(root);
        Assert.Equal("Document Title", reader.GetText(in rootNode));
        Assert.Equal((ushort)GDocNodeType.Document, rootNode.NodeType);
        Assert.Equal("Glacier", reader.GetMetadata(root)["author"]);

        var secChildren = reader.GetChildren(sec1);
        Assert.Equal(2, secChildren.Count);
        Assert.Equal(p1, secChildren[0]);
        Assert.Equal(code, secChildren[1]);

        ref readonly var p1Node = ref reader.GetNode(p1);
        var readVec = reader.GetVector(in p1Node);
        Assert.Equal(4, readVec.Length);
        Assert.Equal(0.5f, readVec[0]);

        var breadcrumbs = reader.GetBreadcrumbs(code);
        Assert.Equal(3, breadcrumbs.Count);
        Assert.Equal(root, breadcrumbs[0]);
        Assert.Equal(sec1, breadcrumbs[1]);
        Assert.Equal(code, breadcrumbs[2]);
    }

    [Fact]
    public void GDoc_FileMemoryMapped_Roundtrip()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"glacier_test_{Guid.NewGuid():N}.gdoc");
        try
        {
            var writer = new GDocWriter(vectorDim: 8);
            writer.AddNode(GDocNodeType.Heading1, "Architecture");
            float[] queryVec = [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
            float[] targetVec = [0.99f, 0.05f, 0f, 0f, 0f, 0f, 0f, 0f];
            float[] otherVec = [0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f];

            uint v0 = writer.AddVector(targetVec);
            uint v1 = writer.AddVector(otherVec);

            uint matchNode = writer.AddNode(GDocNodeType.Paragraph, "Target Content", vectorIndex: v0);
            writer.AddNode(GDocNodeType.Paragraph, "Other Content", vectorIndex: v1);

            writer.WriteToFile(tempFile);

            using var reader = GDocReader.OpenFile(tempFile);
            Assert.Equal(3u, reader.NodeCount);

            var (bestNodeId, score) = reader.FindBestMatch(queryVec);
            Assert.Equal(matchNode, bestNodeId);
            Assert.True(score > 0.95f, $"Expected score > 0.95, got {score}");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void GDocReader_RejectsOutOfBoundsHeaderTablesCycles_AndAllowsLargeText()
    {
        var writer = new GDocWriter(vectorDim: 4);
        writer.AddNode(GDocNodeType.Document, "Root");
        byte[] valid = writer.ToByteArray();

        byte[] badAst = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badAst.AsSpan(20, 4), 999999u);
        Assert.Throws<InvalidDataException>(() => GDocReader.FromMemory(badAst));

        // Corrupt payload CRC32 -> InvalidDataException("CRC32 mismatch...")
        byte[] badCrc = (byte[])valid.Clone();
        badCrc[^1] ^= 0xFF;
        var crcEx = Assert.Throws<InvalidDataException>(() => GDocReader.FromMemory(badCrc));
        Assert.Contains("CRC32 mismatch", crcEx.Message);

        // Cycle detection in sibling chain (fix CRC after mutating NextSiblingId)
        var cycWriter = new GDocWriter(vectorDim: 4);
        uint r = cycWriter.AddNode(GDocNodeType.Document, "Root");
        uint c1 = cycWriter.AddNode(GDocNodeType.Paragraph, "A");
        cycWriter.LinkChild(r, c1);
        byte[] cycBytes = cycWriter.ToByteArray();
        uint astOffset = BinaryPrimitives.ReadUInt32LittleEndian(cycBytes.AsSpan(16, 4));
        int node1NextSibOffset = checked((int)astOffset + Unsafe.SizeOf<GDocAstNode>() + 16);
        BinaryPrimitives.WriteUInt32LittleEndian(cycBytes.AsSpan(node1NextSibOffset, 4), c1);
        uint recomputedCrc = GdocCrc32.Compute(cycBytes.AsSpan(GDocHeader.HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(cycBytes.AsSpan(60, 4), recomputedCrc);

        using var cycReader = GDocReader.FromMemory(cycBytes);
        Assert.Throws<InvalidDataException>(() => cycReader.GetChildren(r));

        // Widened uint TextLength supports >65,535 UTF-8 byte node text cleanly
        string largeText = new string('x', 100_000);
        var largeWriter = new GDocWriter(vectorDim: 4);
        uint largeId = largeWriter.AddNode(GDocNodeType.Paragraph, largeText);
        byte[] largeBytes = largeWriter.ToByteArray();

        using var largeReader = GDocReader.FromMemory(largeBytes);
        ref readonly var largeNode = ref largeReader.GetNode(largeId);
        Assert.Equal(100_000u, largeNode.TextLength);
        Assert.Equal(largeText, largeReader.GetText(in largeNode));
    }

    [Fact]
    public void GDocReader_Disposed_ThrowsObjectDisposedExceptionAcrossAllMembers()
    {
        var writer = new GDocWriter(vectorDim: 4);
        uint vIdx = writer.AddVector([1f, 0f, 0f, 0f]);
        uint tIdx = writer.AddTokenSpan(0, 4, 1);
        uint rootId = writer.AddNode(GDocNodeType.Document, "Root", tokenSpanIndex: tIdx, vectorIndex: vIdx);
        writer.AddMetadata(rootId, "k", "v");

        byte[] bytes = writer.ToByteArray();
        var reader = GDocReader.FromMemory(bytes);
        ref readonly var cachedNode = ref reader.GetNode(rootId);
        var nodeCopy = cachedNode;

        reader.Dispose();
        reader.Dispose(); // Idempotent dispose

        Assert.Throws<ObjectDisposedException>(() => _ = reader.Header);
        Assert.Throws<ObjectDisposedException>(() => _ = reader.NodeCount);
        Assert.Throws<ObjectDisposedException>(() => _ = reader.VectorCount);
        Assert.Throws<ObjectDisposedException>(() => _ = reader.VectorDim);
        Assert.Throws<ObjectDisposedException>(() => _ = reader.MetadataCount);
        Assert.Throws<ObjectDisposedException>(() => _ = reader.GetNode(rootId));
        Assert.Throws<ObjectDisposedException>(() => _ = reader.GetTextUtf8(in nodeCopy));
        Assert.Throws<ObjectDisposedException>(() => _ = reader.GetText(in nodeCopy));
        Assert.Throws<ObjectDisposedException>(() => _ = reader.GetVector(in nodeCopy));
        Assert.Throws<ObjectDisposedException>(() => _ = reader.GetTokenSpan(tIdx));
        Assert.Throws<ObjectDisposedException>(() => _ = reader.GetChildren(rootId));
        Assert.Throws<ObjectDisposedException>(() => _ = reader.GetBreadcrumbs(rootId));
        Assert.Throws<ObjectDisposedException>(() => _ = reader.FindBestMatch([1f, 0f, 0f, 0f]));
        Assert.Throws<ObjectDisposedException>(() => _ = reader.GetMetadata(rootId));

        // Also test empty AST (NodeCount == 0) disposed FindBestMatch
        var emptyWriter = new GDocWriter(vectorDim: 4);
        var emptyReader = GDocReader.FromMemory(emptyWriter.ToByteArray());
        emptyReader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = emptyReader.FindBestMatch([1f, 0f, 0f, 0f]));
    }

    [Fact]
    public void GDocReader_OpenFile_TruncatedOrCorrupt_CleansUpMmfHandles()
    {
        string emptyPath = Path.Combine(Path.GetTempPath(), $"glacier_empty_{Guid.NewGuid():N}.gdoc");
        string shortPath = Path.Combine(Path.GetTempPath(), $"glacier_short_{Guid.NewGuid():N}.gdoc");
        string badMagicPath = Path.Combine(Path.GetTempPath(), $"glacier_badmagic_{Guid.NewGuid():N}.gdoc");

        try
        {
            File.WriteAllBytes(emptyPath, Array.Empty<byte>());
            File.WriteAllBytes(shortPath, new byte[16]);
            File.WriteAllBytes(badMagicPath, new byte[128]);

            Assert.Throws<InvalidDataException>(() => GDocReader.OpenFile(emptyPath));
            Assert.Throws<InvalidDataException>(() => GDocReader.OpenFile(shortPath));
            Assert.Throws<InvalidDataException>(() => GDocReader.OpenFile(badMagicPath));

            // Deleting immediately proves no leaked MemoryMappedFile handles remain open
            File.Delete(emptyPath);
            File.Delete(shortPath);
            File.Delete(badMagicPath);
        }
        finally
        {
            if (File.Exists(emptyPath)) File.Delete(emptyPath);
            if (File.Exists(shortPath)) File.Delete(shortPath);
            if (File.Exists(badMagicPath)) File.Delete(badMagicPath);
        }
    }

    [Fact]
    public void GDocWriter_WriteToStream_HandlesNonZeroPositionAndNonSeekableStreams()
    {
        var writer = new GDocWriter(vectorDim: 4);
        uint r = writer.AddNode(GDocNodeType.Document, "StreamRoot");
        uint c = writer.AddNode(GDocNodeType.Paragraph, "Non-aligned text length 23");
        writer.LinkChild(r, c);
        writer.AddMetadata(c, "key", "value");
        writer.AddTokenSpan(0, 10, 1);
        writer.AddVector([0.1f, 0.2f, 0.3f, 0.4f]);

        byte[] expected = writer.ToByteArray();

        // 1. Non-zero offset stream (17 prefix bytes)
        using var prefixedStream = new MemoryStream();
        prefixedStream.Write(new byte[17]);
        writer.WriteToStream(prefixedStream);
        byte[] actualSlice = prefixedStream.ToArray().AsSpan(17).ToArray();
        Assert.Equal(expected, actualSlice);

        // 2. Non-seekable stream (CanSeek == false)
        using var backing = new MemoryStream();
        using var nonSeekable = new NonSeekableWriteStream(backing);
        writer.WriteToStream(nonSeekable);
        byte[] actualNonSeekable = backing.ToArray();
        Assert.Equal(expected, actualNonSeekable);

        using var reader = GDocReader.FromMemory(actualNonSeekable);
        Assert.Equal(2u, reader.NodeCount);
        Assert.Equal("Non-aligned text length 23", reader.GetText(in reader.GetNode(c)));
        Assert.Equal("value", reader.GetMetadata(c)["key"]);
    }

    [Fact]
    public void GDocReader_And_Extensions_ReadLegacyV1GDocWithSyntheticMetadataBlob()
    {
        // Construct a legacy v1 GDoc payload (0x434F4447, Version=1, 32-byte GDocAstNodeV1, JSON metadata child)
        string rootText = "LegacyV1Root";
        string metaJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["legacy"] = "true" });
        byte[] rootBytes = Encoding.UTF8.GetBytes(rootText);
        byte[] metaBytes = Encoding.UTF8.GetBytes(metaJson);

        uint stringPoolOffset = 64;
        uint stringPoolLen = (uint)(rootBytes.Length + metaBytes.Length);
        uint astOffset = (stringPoolOffset + stringPoolLen + 7u) & ~7u;
        uint totalLen = astOffset + 64u; // 2 nodes * 32B

        byte[] v1Bytes = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(v1Bytes.AsSpan(0, 4), GDocHeader.MagicValue);
        BinaryPrimitives.WriteUInt32LittleEndian(v1Bytes.AsSpan(4, 4), GDocHeader.LegacyVersion1);
        BinaryPrimitives.WriteUInt32LittleEndian(v1Bytes.AsSpan(8, 4), stringPoolOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(v1Bytes.AsSpan(12, 4), stringPoolLen);
        BinaryPrimitives.WriteUInt32LittleEndian(v1Bytes.AsSpan(16, 4), astOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(v1Bytes.AsSpan(20, 4), 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(v1Bytes.AsSpan(24, 4), 32u);

        rootBytes.CopyTo(v1Bytes.AsSpan((int)stringPoolOffset));
        metaBytes.CopyTo(v1Bytes.AsSpan((int)stringPoolOffset + rootBytes.Length));

        // Node 0 (Document)
        Span<byte> n0 = v1Bytes.AsSpan((int)astOffset, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(n0.Slice(0, 4), 0u);
        BinaryPrimitives.WriteUInt16LittleEndian(n0.Slice(4, 2), (ushort)GDocNodeType.Document);
        BinaryPrimitives.WriteUInt16LittleEndian(n0.Slice(6, 2), 0x4000);
        BinaryPrimitives.WriteUInt32LittleEndian(n0.Slice(8, 4), GDocAstNode.NoNode);
        BinaryPrimitives.WriteUInt32LittleEndian(n0.Slice(12, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(n0.Slice(16, 4), GDocAstNode.NoNode);
        BinaryPrimitives.WriteUInt32LittleEndian(n0.Slice(20, 4), 0u);
        BinaryPrimitives.WriteUInt16LittleEndian(n0.Slice(24, 2), (ushort)rootBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(n0.Slice(26, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(n0.Slice(28, 4), GDocAstNode.NoVector);

        // Node 1 (synthetic v1 MetadataBlobFlag child)
        Span<byte> n1 = v1Bytes.AsSpan((int)astOffset + 32, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(n1.Slice(0, 4), 1u);
        BinaryPrimitives.WriteUInt16LittleEndian(n1.Slice(4, 2), (ushort)GDocNodeType.Text);
        BinaryPrimitives.WriteUInt16LittleEndian(n1.Slice(6, 2), 0x8000);
        BinaryPrimitives.WriteUInt32LittleEndian(n1.Slice(8, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(n1.Slice(12, 4), GDocAstNode.NoNode);
        BinaryPrimitives.WriteUInt32LittleEndian(n1.Slice(16, 4), GDocAstNode.NoNode);
        BinaryPrimitives.WriteUInt32LittleEndian(n1.Slice(20, 4), (uint)rootBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(n1.Slice(24, 2), (ushort)metaBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(n1.Slice(26, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(n1.Slice(28, 4), GDocAstNode.NoVector);

        using (var reader = GDocReader.FromMemory(v1Bytes))
        {
            Assert.Equal(GDocHeader.LegacyVersion1, reader.Header.Version);
            Assert.Equal(2u, reader.NodeCount);
            Assert.Equal(rootText, reader.GetText(in reader.GetNode(0)));
        }

        DocNode loaded = GlacierStorageDocExtensions.LoadGdoc(v1Bytes);
        Assert.Equal(rootText, loaded.Content);
        Assert.Equal("true", loaded.Metadata["legacy"]);
        Assert.Empty(loaded.Children);

        // Verify Binary.GdocReader.ToDocNodeTree() matches GlacierStorageDocExtensions.LoadGdoc for legacy v1 0x8000 blobs
        string tempFile = Path.Combine(Path.GetTempPath(), $"glacier_v1_blob_{Guid.NewGuid():N}.gdoc");
        try
        {
            File.WriteAllBytes(tempFile, v1Bytes);
            using var binReader = new GdocReader(tempFile);
            DocNode binLoaded = binReader.ToDocNodeTree();
            Assert.Equal(rootText, binLoaded.Content);
            Assert.Equal("true", binLoaded.Metadata["legacy"]);
            Assert.Empty(binLoaded.Children);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void GDocReader_And_BinaryGdocReader_RejectInvalidOffsetsStridesEmptyCrc_AndCorruptLinks()
    {
        var writer = new GDocWriter(vectorDim: 4);
        uint r = writer.AddNode(GDocNodeType.Document, "Hello");
        uint c = writer.AddNode(GDocNodeType.Paragraph, "World");
        writer.LinkChild(r, c);
        writer.AddTokenSpan(0, 5, 1);
        writer.AddVector([1f, 0f, 0f, 0f]);
        writer.AddMetadata(r, "k", "v");
        byte[] valid = writer.ToByteArray();

        static byte[] WithValidCrc(byte[] buf)
        {
            if (buf.Length > GDocHeader.HeaderSize)
            {
                uint crc = GdocCrc32.Compute(buf.AsSpan(GDocHeader.HeaderSize));
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(60, 4), crc);
            }
            return buf;
        }

        void AssertBothReadersReject(byte[] corrupt)
        {
            Assert.Throws<InvalidDataException>(() => GDocReader.FromMemory(corrupt));
            string path = Path.Combine(Path.GetTempPath(), $"glacier_bad_{Guid.NewGuid():N}.gdoc");
            try
            {
                File.WriteAllBytes(path, corrupt);
                Assert.Throws<InvalidDataException>(() => new GdocReader(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // 1. StringPoolOffset = 65 (unaligned on v2)
        byte[] badStrOffset = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badStrOffset.AsSpan(8, 4), 65u);
        AssertBothReadersReject(WithValidCrc(badStrOffset));

        // 2. AstTableOffset = 0 when AstNodeCount > 0 (header alias attack)
        byte[] badAstOffsetZero = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badAstOffsetZero.AsSpan(16, 4), 0u);
        AssertBothReadersReject(WithValidCrc(badAstOffsetZero));

        // 3. TokenSpanOffset = 16 (< HeaderSize)
        byte[] badSpanOffset = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badSpanOffset.AsSpan(28, 4), 16u);
        AssertBothReadersReject(WithValidCrc(badSpanOffset));

        // 4. VectorOffset = 32 (< HeaderSize)
        byte[] badVecOffset = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badVecOffset.AsSpan(40, 4), 32u);
        AssertBothReadersReject(WithValidCrc(badVecOffset));

        // 5. AstNodeSize = 49 (non-exact stride)
        byte[] badAstStride = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badAstStride.AsSpan(24, 4), 49u);
        AssertBothReadersReject(WithValidCrc(badAstStride));

        // 6. TokenSpanSize = 17 (non-exact stride)
        byte[] badSpanStride = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badSpanStride.AsSpan(36, 4), 17u);
        AssertBothReadersReject(WithValidCrc(badSpanStride));

        // 7. Empty 64B .gdoc with non-zero PayloadCrc32
        byte[] emptyValid = new GDocWriter(vectorDim: 4).ToByteArray();
        Assert.Equal(GDocHeader.HeaderSize, emptyValid.Length);
        using (var emptyReader = GDocReader.FromMemory(emptyValid))
        {
            Assert.Equal(0u, emptyReader.NodeCount);
        }
        byte[] emptyBadCrc = (byte[])emptyValid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(emptyBadCrc.AsSpan(60, 4), 0xEFBEADDEu);
        AssertBothReadersReject(emptyBadCrc);

        // 8. Out-of-bounds internal child / parent link throws InvalidDataException (not ArgumentOutOfRangeException)
        uint astOffset = BinaryPrimitives.ReadUInt32LittleEndian(valid.AsSpan(16, 4));
        byte[] badChildLink = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badChildLink.AsSpan((int)astOffset + 12, 4), 999u); // Node 0 FirstChildId
        WithValidCrc(badChildLink);
        using (var r1 = GDocReader.FromMemory(badChildLink))
        {
            Assert.Throws<InvalidDataException>(() => r1.GetChildren(0));
        }

        byte[] badParentLink = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badParentLink.AsSpan((int)astOffset + Unsafe.SizeOf<GDocAstNode>() + 8, 4), 999u); // Node 1 ParentId
        WithValidCrc(badParentLink);
        using (var r2 = GDocReader.FromMemory(badParentLink))
        {
            Assert.Throws<InvalidDataException>(() => r2.GetBreadcrumbs(1));
        }
    }

    [Fact]
    public void GDocWriter_LinkChild_RejectsDuplicateChildAttachment()
    {
        var writer = new GDocWriter(vectorDim: 4);
        uint a = writer.AddNode(GDocNodeType.Document, "A");
        uint b = writer.AddNode(GDocNodeType.Paragraph, "B");
        uint c = writer.AddNode(GDocNodeType.Section, "C");

        writer.LinkChild(a, b);
        Assert.Throws<InvalidOperationException>(() => writer.LinkChild(a, b));
        Assert.Throws<InvalidOperationException>(() => writer.LinkChild(c, b));
    }

    [Fact]
    public void GlacierStorageDocExtensions_LoadGdoc_RejectsCrossLevelCycle()
    {
        var writer = new GDocWriter(vectorDim: 0);
        uint n0 = writer.AddNode(GDocNodeType.Document, "Root");
        uint n1 = writer.AddNode(GDocNodeType.Paragraph, "Child");
        writer.LinkChild(n0, n1);

        byte[] bytes = writer.ToByteArray();
        uint astOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
        // Point node 1's FirstChildId back to node 0 (cross-level cycle 0 -> 1 -> 0)
        int node1FirstChildOffset = checked((int)astOffset + Unsafe.SizeOf<GDocAstNode>() + 12);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(node1FirstChildOffset, 4), n0);
        uint crc = GdocCrc32.Compute(bytes.AsSpan(GDocHeader.HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60, 4), crc);

        Assert.Throws<InvalidDataException>(() => GlacierStorageDocExtensions.LoadGdoc(bytes));
    }

    [Fact]
    public void BinaryGdocReader_And_GDocReader_ReadLegacyBinaryV1Files()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"glacier_legacy_v1_{Guid.NewGuid():N}.gdoc");
        try
        {
            // Construct a legacy Binary v1 file (0x47444F43 "CODG" LE, Version=1)
            byte[] content = Encoding.UTF8.GetBytes("Legacy Binary Content");
            byte[] key = Encoding.UTF8.GetBytes("origin");
            byte[] val = Encoding.UTF8.GetBytes("v1-format");

            var node = new GdocNodeEntry((byte)NodeType.Header1, 0x01, 0, (uint)content.Length, -1, -1, -1, 0, 1, 0);
            var meta = new GdocMetadataEntry((uint)content.Length, (ushort)key.Length, (uint)(content.Length + key.Length), (ushort)val.Length, -1);

            using var payloadMs = new MemoryStream();
            Span<byte> nodeBytes = stackalloc byte[32];
            MemoryMarshal.Write(nodeBytes, in node);
            payloadMs.Write(nodeBytes);
            payloadMs.Write(content);
            payloadMs.Write(key);
            payloadMs.Write(val);
            Span<byte> metaBytes = stackalloc byte[16];
            MemoryMarshal.Write(metaBytes, in meta);
            payloadMs.Write(metaBytes);

            byte[] payload = payloadMs.ToArray();
            uint crc = GdocCrc32.Compute(payload);

            byte[] header = new byte[64];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), GdocWriter.GdocMagic);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), GdocWriter.CurrentVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(6, 4), 1u);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(10, 8), 64 + 32);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(18, 8), (ulong)(content.Length + key.Length + val.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(26, 8), 64);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(34, 8), 32);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(42, 8), 64 + 32 + (ulong)(content.Length + key.Length + val.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(50, 8), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(58, 4), crc);

            using (var fs = File.Create(tempFile))
            {
                fs.Write(header);
                fs.Write(payload);
            }

            using (var binReader = new GdocReader(tempFile))
            {
                Assert.Equal(1u, binReader.NodeCount);
                Assert.Equal(1u, binReader.MetadataCount);
                Assert.Equal("Legacy Binary Content", binReader.GetContentString(0));
                Assert.Equal("v1-format", binReader.GetMetadata(0)["origin"]);
                var tree = binReader.ToDocNodeTree();
                Assert.Equal(NodeType.Header1, tree.Type);
                Assert.Equal("Legacy Binary Content", tree.Content);
            }

            using (var gdocReader = GDocReader.OpenFile(tempFile))
            {
                Assert.Equal(1u, gdocReader.NodeCount);
                Assert.Equal(1u, gdocReader.MetadataCount);
                Assert.Equal("Legacy Binary Content", gdocReader.GetText(in gdocReader.GetNode(0)));
                Assert.Equal("v1-format", gdocReader.GetMetadata(0)["origin"]);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableWriteStream(Stream inner) => _inner = inner;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    }
}
