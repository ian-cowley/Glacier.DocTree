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
        bytes[68] = (byte)(bytes[68] + 1); // Alter byte to induce CRC mismatch
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

    [Fact]
    public void IterativeTraversals_DeepTree15000Levels_NoStackOverflow()
    {
        const int depth = 15_000;
        var root = new DocNode { Type = NodeType.Root, Content = "DeepRoot" };
        var current = root;
        for (int i = 1; i <= depth; i++)
        {
            var child = new DocNode
            {
                Type = i == depth ? NodeType.Paragraph : NodeType.Header2,
                Content = i == depth ? "TargetLeafKeyword" : $"Section_{i}"
            };
            current.AddChild(child);
            current = child;
        }

        // 1. DocNode.GetFullText()
        string fullText = root.GetFullText();
        Assert.Contains("TargetLeafKeyword", fullText);

        // 2. TreeSearch (FindHeader, FindContainingText, GetSemanticPath)
        var search = new Glacier.DocTree.Traversal.TreeSearch(root);
        var foundHeader = search.FindHeader($"Section_{depth - 1}");
        Assert.NotNull(foundHeader);
        var leafMatches = search.FindContainingText("TargetLeafKeyword");
        Assert.Single(leafMatches);
        string semPath = Glacier.DocTree.Traversal.TreeSearch.GetSemanticPath(leafMatches[0]);
        Assert.Contains($"Section_{depth - 1}", semPath);

        // 3. Binary GdocWriter / GdocReader
        string gdocPath = GetTempGdocPath();
        GdocWriter.Write(root, gdocPath);
        using (var binReader = new GdocReader(gdocPath))
        {
            Assert.Equal((uint)(depth + 1), binReader.NodeCount);
            var restored = binReader.ToDocNodeTree();
            Assert.Equal(NodeType.Root, restored.Type);
        }

        // 4. GlacierStorageDocExtensions.ToGdocBytes / LoadGdoc
        byte[] gdocBytes = root.ToGdocBytes();
        var loaded = GlacierStorageDocExtensions.LoadGdoc(gdocBytes);
        Assert.Equal("DeepRoot", loaded.Content);

        // 5. GraphContextDecoupler.ExtractDocumentGraph
        var graph = Glacier.DocTree.Context.GraphContextDecoupler.ExtractDocumentGraph(root);
        Assert.Equal(depth + 1, graph.NodeCount);
        Assert.Equal(depth, graph.EdgeCount);

        // 6. TreeSerializer.Serialize (run on a constrained 256 KB stack thread at 3,000 levels to prove non-recursion)
        var deepJsonRoot = new DocNode { Type = NodeType.Root, Content = "JsonDeepRoot" };
        var curJson = deepJsonRoot;
        for (int i = 1; i <= 3_000; i++)
        {
            var c = new DocNode { Type = NodeType.Header2, Content = $"J_{i}" };
            curJson.AddChild(c);
            curJson = c;
        }
        string jsonDir = Path.Combine(Path.GetTempPath(), $"glacier_deep_json_{Guid.NewGuid():N}");
        try
        {
            Exception? threadEx = null;
            var t = new System.Threading.Thread(() =>
            {
                try { TreeSerializer.Serialize(deepJsonRoot, jsonDir); }
                catch (Exception ex) { threadEx = ex; }
            }, maxStackSize: 256 * 1024);
            t.Start();
            t.Join();
            Assert.Null(threadEx);
            var lazy = TreeSerializer.DeserializeLazy(jsonDir);
            Assert.Equal("JsonDeepRoot", lazy.Content);
        }
        finally
        {
            if (Directory.Exists(jsonDir))
            {
                try { Directory.Delete(jsonDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void TreeSearch_GetSemanticPath_CycleThrowsInvalidOperationException()
    {
        var a = new DocNode { Type = NodeType.Header1, Content = "A" };
        var b = new DocNode { Type = NodeType.Header2, Content = "B" };
        a.AddChild(b);
        a.Parent = b; // Introduce parent cycle

        Assert.Throws<InvalidOperationException>(() => Glacier.DocTree.Traversal.TreeSearch.GetSemanticPath(b));
    }
}
