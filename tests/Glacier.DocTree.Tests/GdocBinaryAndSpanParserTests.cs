using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Glacier.DocTree.Binary;
using Glacier.DocTree.Core;
using Glacier.DocTree.Parser;
using SpanLineEnumerator = Glacier.DocTree.Parser.SpanLineEnumerator;
using Xunit;

namespace Glacier.DocTree.Tests
{
    public class GdocBinaryAndSpanParserTests : IDisposable
    {
        private readonly string _tempDir;

        public GdocBinaryAndSpanParserTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"gdoc_tests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        [Fact]
        public void SpanLineEnumerator_HandlesVariousLineEndings()
        {
            var text = "Line 1\r\nLine 2\nLine 3\rLine 4";
            var enumerator = new SpanLineEnumerator(text.AsSpan());

            var lines = new List<string>();
            while (enumerator.MoveNext())
            {
                lines.Add(enumerator.Current.ToString());
            }

            Assert.Equal(4, lines.Count);
            Assert.Equal("Line 1", lines[0]);
            Assert.Equal("Line 2", lines[1]);
            Assert.Equal("Line 3", lines[2]);
            Assert.Equal("Line 4", lines[3]);
        }

        [Fact]
        public void SpanMarkdownParser_BuildsHierarchicalAst()
        {
            string markdown = @"# Top Level H1
Introduction paragraph text.

## Subsection H2
Detail paragraph 1.
Detail paragraph 2.

### Sub-subsection H3
Deepest detail.

```csharp
var x = 42;
Console.WriteLine(x);
```

# Second Top Level H1
Another chapter.";

            var root = SpanMarkdownParser.Parse(markdown.AsSpan());

            Assert.Equal(NodeType.Root, root.Type);
            Assert.Equal(2, root.Children.Count);

            // First H1
            var h1_1 = root.Children[0];
            Assert.Equal(NodeType.Header1, h1_1.Type);
            Assert.Equal("Top Level H1", h1_1.Content);
            Assert.Equal(2, h1_1.Children.Count); // Paragraph and H2

            var p1 = h1_1.Children[0];
            Assert.Equal(NodeType.Paragraph, p1.Type);
            Assert.Contains("Introduction paragraph text.", p1.Content);

            var h2 = h1_1.Children[1];
            Assert.Equal(NodeType.Header2, h2.Type);
            Assert.Equal("Subsection H2", h2.Content);
            Assert.Equal(2, h2.Children.Count); // Paragraph and H3

            var h3 = h2.Children[1];
            Assert.Equal(NodeType.Header3, h3.Type);
            Assert.Equal("Sub-subsection H3", h3.Content);
            Assert.Equal(2, h3.Children.Count); // Paragraph and CodeBlock

            var codeBlock = h3.Children[1];
            Assert.Equal(NodeType.CodeBlock, codeBlock.Type);
            Assert.Contains("var x = 42;", codeBlock.Content);

            // Second H1
            var h1_2 = root.Children[1];
            Assert.Equal(NodeType.Header1, h1_2.Type);
            Assert.Equal("Second Top Level H1", h1_2.Content);
            Assert.Single(h1_2.Children);
            Assert.Equal("Another chapter.", h1_2.Children[0].Content);
        }

        [Fact]
        public void MarkdownTreeParser_UsesSpanParserEquivalently()
        {
            string markdown = @"# Header
Paragraph line 1
Paragraph line 2

## Sub
Sub paragraph";

            var parser = new MarkdownTreeParser();
            var treeFromString = parser.Parse(markdown);
            var treeFromSpan = parser.Parse(markdown.AsSpan());

            Assert.Equal(treeFromString.GetFullText(), treeFromSpan.GetFullText());
            Assert.Equal(treeFromString.Children.Count, treeFromSpan.Children.Count);
        }

        [Fact]
        public void GdocWriterAndReader_RoundtripFullTreeWithMetadata()
        {
            var root = new DocNode { Type = NodeType.Root, Content = "Root Document" };
            root.Metadata["author"] = "Architect";
            root.Metadata["version"] = "2.0";

            var h1 = new DocNode { Type = NodeType.Header1, Content = "Architecture Overview" };
            h1.Metadata["section_id"] = "sec-01";
            root.AddChild(h1);

            var p1 = new DocNode { Type = NodeType.Paragraph, Content = "This is a detailed specification of Glacier." };
            h1.AddChild(p1);

            var code = new DocNode { Type = NodeType.CodeBlock, Content = "public void Test() => Assert.True(true);" };
            code.Metadata["lang"] = "csharp";
            h1.AddChild(code);

            string gdocPath = Path.Combine(_tempDir, "document.gdoc");

            // 1. Write binary
            TreeSerializer.SerializeBinary(root, gdocPath);
            Assert.True(File.Exists(gdocPath));

            // 2. Open binary with zero-copy GdocReader
            using (var reader = TreeSerializer.OpenBinary(gdocPath))
            {
                Assert.Equal(4u, reader.NodeCount);
                Assert.Equal(4u, reader.MetadataCount);

                // Check root (node 0)
                var rootEntry = reader.GetNode(0);
                Assert.Equal(NodeType.Root, (NodeType)rootEntry.NodeType);
                Assert.Equal("Root Document", reader.GetContentString(0));
                var rootBytes = reader.GetContentUtf8(0);
                Assert.Equal("Root Document", Encoding.UTF8.GetString(rootBytes));

                var rootMeta = reader.GetMetadata(0);
                Assert.Equal(2, rootMeta.Count);
                Assert.Equal("Architect", rootMeta["author"]);
                Assert.Equal("2.0", rootMeta["version"]);

                // Check H1 (node 1)
                var h1Entry = reader.GetNode(1);
                Assert.Equal(NodeType.Header1, (NodeType)h1Entry.NodeType);
                Assert.Equal("Architecture Overview", reader.GetContentString(1));
                Assert.Equal(0, h1Entry.ParentIndex);
                var h1Meta = reader.GetMetadata(1);
                Assert.Equal("sec-01", h1Meta["section_id"]);

                // Check H1 children navigation
                var h1Children = reader.GetChildIndices(1);
                Assert.Equal(2, h1Children.Count);
                Assert.Equal(2, h1Children[0]); // p1
                Assert.Equal(3, h1Children[1]); // code

                // Check code node
                var codeEntry = reader.GetNode(3);
                Assert.Equal(NodeType.CodeBlock, (NodeType)codeEntry.NodeType);
                Assert.Equal("public void Test() => Assert.True(true);", reader.GetContentString(3));
                var codeMeta = reader.GetMetadata(3);
                Assert.Equal("csharp", codeMeta["lang"]);

                // 3. Convert back to full DocNode tree and verify equivalence
                var deserializedRoot = reader.ToDocNodeTree();
                Assert.Equal(root.Type, deserializedRoot.Type);
                Assert.Equal(root.Content, deserializedRoot.Content);
                Assert.Equal("Architect", deserializedRoot.Metadata["author"]);
                Assert.Single(deserializedRoot.Children);

                var deserializedH1 = deserializedRoot.Children[0];
                Assert.Equal(h1.Type, deserializedH1.Type);
                Assert.Equal(h1.Content, deserializedH1.Content);
                Assert.Equal(2, deserializedH1.Children.Count);
                Assert.Equal(p1.Content, deserializedH1.Children[0].Content);
                Assert.Equal(code.Content, deserializedH1.Children[1].Content);
                Assert.Equal("csharp", deserializedH1.Children[1].Metadata["lang"]);

                Assert.Equal(root.GetFullText(), deserializedRoot.GetFullText());
            }

            // 4. Test DeserializeBinary convenience method
            var deserializedDirect = TreeSerializer.DeserializeBinary(gdocPath);
            Assert.Equal(root.GetFullText(), deserializedDirect.GetFullText());
        }

        [Fact]
        public void GdocReader_CorruptedMagic_ThrowsInvalidDataException()
        {
            var root = new DocNode { Type = NodeType.Root, Content = "Test" };
            string gdocPath = Path.Combine(_tempDir, "corrupted_magic.gdoc");
            TreeSerializer.SerializeBinary(root, gdocPath);

            // Corrupt the magic header
            var bytes = File.ReadAllBytes(gdocPath);
            bytes[0] = 0x00;
            File.WriteAllBytes(gdocPath, bytes);

            Assert.Throws<InvalidDataException>(() =>
            {
                using var reader = new GdocReader(gdocPath);
            });
        }

        [Fact]
        public void GdocReader_CorruptedCrc_ThrowsInvalidDataException()
        {
            var root = new DocNode { Type = NodeType.Root, Content = "Test Content for CRC validation" };
            string gdocPath = Path.Combine(_tempDir, "corrupted_crc.gdoc");
            TreeSerializer.SerializeBinary(root, gdocPath);

            // Corrupt payload (e.g. in node table or string arena)
            var bytes = File.ReadAllBytes(gdocPath);
            bytes[bytes.Length - 1] ^= 0xFF;
            File.WriteAllBytes(gdocPath, bytes);

            Assert.Throws<InvalidDataException>(() =>
            {
                using var reader = new GdocReader(gdocPath);
            });
        }
    }
}
