namespace Glacier.DocTree.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using Glacier.DocTree.Binary;
using Glacier.DocTree.Core;
using Xunit;

public class AdversarialStressTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string GetTempGdocPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"glacier_doctree_stress_{Guid.NewGuid():N}.gdoc");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    [Fact]
    public void GdocWriterAndReader_DeeplyNestedAst_RoundTripFidelityPreserved()
    {
        const int depth = 150;
        var root = new DocNode
        {
            Type = NodeType.Root,
            Content = "Root Document",
            Metadata = new Dictionary<string, string> { { "title", "Deep AST" } }
        };

        var current = root;
        var nodeTypes = new[] { NodeType.Header1, NodeType.Header2, NodeType.Paragraph, NodeType.CodeBlock, NodeType.List };

        for (int d = 1; d <= depth; d++)
        {
            var child = new DocNode
            {
                Type = nodeTypes[d % nodeTypes.Length],
                Content = $"Level {d}: Content with Unicode \u26A1 \U0001F680 Line1\nLine2",
                Metadata = new Dictionary<string, string>
                {
                    { "depth", d.ToString() },
                    { "tag", $"tag_{d}" }
                }
            };
            current.AddChild(child);
            current = child;
        }

        string filePath = GetTempGdocPath();
        GdocWriter.Write(root, filePath);
        Assert.True(File.Exists(filePath));

        using var reader = new GdocReader(filePath);
        Assert.Equal((uint)(depth + 1), reader.NodeCount);

        var restoredRoot = reader.ToDocNodeTree();
        Assert.NotNull(restoredRoot);
        Assert.Equal(NodeType.Root, restoredRoot.Type);
        Assert.Equal("Root Document", restoredRoot.Content);
        Assert.Equal("Deep AST", restoredRoot.Metadata["title"]);

        // Walk and verify each level
        var verifyCurrent = restoredRoot;
        for (int d = 1; d <= depth; d++)
        {
            Assert.Single(verifyCurrent.Children);
            var child = verifyCurrent.Children[0];
            Assert.Equal(nodeTypes[d % nodeTypes.Length], child.Type);
            Assert.Equal($"Level {d}: Content with Unicode \u26A1 \U0001F680 Line1\nLine2", child.Content);
            Assert.Equal(d.ToString(), child.Metadata["depth"]);
            Assert.Equal($"tag_{d}", child.Metadata["tag"]);
            verifyCurrent = child;
        }
    }

    [Fact]
    public void GdocReader_CorruptedPayload_ThrowsInvalidDataException()
    {
        var root = new DocNode { Type = NodeType.Root, Content = "Valid Root" };
        root.AddChild(new DocNode { Type = NodeType.Paragraph, Content = "Some payload data" });

        string filePath = GetTempGdocPath();
        GdocWriter.Write(root, filePath);

        // Corrupt a byte in the payload (past the 64-byte header)
        byte[] bytes = File.ReadAllBytes(filePath);
        Assert.True(bytes.Length > 70);
        bytes[68] ^= 0xFF; // Flip bits
        File.WriteAllBytes(filePath, bytes);

        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            using var reader = new GdocReader(filePath);
        });

        Assert.Contains("CRC32 mismatch", ex.Message);
    }

    [Fact]
    public void GdocWriterAndReader_WideFanOutTree_RoundTripFidelityPreserved()
    {
        const int childCount = 500;
        var root = new DocNode { Type = NodeType.Root, Content = "FanOut Root" };

        for (int i = 0; i < childCount; i++)
        {
            var child = new DocNode
            {
                Type = NodeType.Header2,
                Content = $"Section {i}",
                Metadata = new Dictionary<string, string> { { "index", i.ToString() } }
            };
            child.AddChild(new DocNode { Type = NodeType.Paragraph, Content = $"Paragraph for {i}" });
            root.AddChild(child);
        }

        string filePath = GetTempGdocPath();
        GdocWriter.Write(root, filePath);

        using var reader = new GdocReader(filePath);
        Assert.Equal((uint)(1 + childCount * 2), reader.NodeCount);

        var restored = reader.ToDocNodeTree();
        Assert.Equal(childCount, restored.Children.Count);
        for (int i = 0; i < childCount; i++)
        {
            var sec = restored.Children[i];
            Assert.Equal(NodeType.Header2, sec.Type);
            Assert.Equal($"Section {i}", sec.Content);
            Assert.Equal(i.ToString(), sec.Metadata["index"]);
            Assert.Single(sec.Children);
            Assert.Equal($"Paragraph for {i}", sec.Children[0].Content);
        }
    }
}
