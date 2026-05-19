using System;
using System.IO;
using Xunit;
using Glacier.DocTree.Core;
using Glacier.DocTree.Traversal;

namespace Glacier.DocTree.Tests
{
    public class TreeSerializationTests : IDisposable
    {
        private readonly string _tempDir;

        public TreeSerializationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"doctree_tests_{Guid.NewGuid():N}");
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
        public void TestEndToEndSerializationAndLazyLoading()
        {
            // 1. Arrange: Sample Markdown Document
            string markdown = @"
# Section A

This is section A content.

## Subsection A1

More details on A1.

```json
{
  ""debug"": true
}
```

# Section B

Some other content.
";

            var parser = new MarkdownTreeParser();
            var originalRoot = parser.Parse(markdown);

            // 2. Act: Serialize the original tree
            TreeSerializer.Serialize(originalRoot, _tempDir);

            // Verify files exist in the temp directory
            var jsonFiles = Directory.GetFiles(_tempDir, "*.json");
            Assert.NotEmpty(jsonFiles);

            // 3. Act: Deserialize lazily
            var lazyRoot = TreeSerializer.DeserializeLazy(_tempDir);

            // Assert: Verify it's a LazyDocNode
            Assert.IsType<LazyDocNode>(lazyRoot);
            var lazyNode = (LazyDocNode)lazyRoot;
            Assert.Equal("root", lazyNode.NodeId);

            // 4. Assert: Traversal/Compare content
            // Verify that calling GetFullText triggers recursive lazy-loading and produces the same text
            string originalText = originalRoot.GetFullText();
            string lazyText = lazyRoot.GetFullText();

            Assert.Equal(originalText, lazyText);

            // Verify structure matches
            Assert.Equal(originalRoot.Children.Count, lazyRoot.Children.Count);
            Assert.Equal(originalRoot.Children[0].Content, lazyRoot.Children[0].Content);
            Assert.Equal(originalRoot.Children[0].Children.Count, lazyRoot.Children[0].Children.Count);

            // 5. Assert: Semantic Search works on Lazy Tree
            var search = new TreeSearch(lazyRoot);
            var headerNode = search.FindHeader("Subsection A1");
            Assert.NotNull(headerNode);
            Assert.Equal("Subsection A1", headerNode.Content);
            Assert.Equal(NodeType.Header2, headerNode.Type);

            // Get context bundle
            string bundle = search.GetLlmContextBundle("Subsection A1");
            Assert.Contains("More details on A1", bundle);
            Assert.Contains("Section A > Subsection A1", bundle);
        }
    }
}
