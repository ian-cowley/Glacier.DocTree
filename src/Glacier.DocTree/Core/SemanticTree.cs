using System;
using System.Collections.Generic;
using System.Text;

namespace Glacier.DocTree.Core
{
    public enum NodeType
    {
        Root,
        Header1,
        Header2,
        Header3,
        Header4,
        Header5,
        Header6,
        Paragraph,
        CodeBlock,
        List
    }

    /// <summary>
    /// Represents a single structural node in a document.
    /// </summary>
    public class DocNode
    {
        public virtual NodeType Type { get; set; }
        public virtual string Content { get; set; } = string.Empty;
        public virtual Dictionary<string, string> Metadata { get; set; } = new();

        public virtual DocNode? Parent { get; set; }
        public virtual List<DocNode> Children { get; set; } = new();

        public virtual void AddChild(DocNode child)
        {
            child.Parent = this;
            Children.Add(child);
        }

        /// <summary>
        /// Recursively gathers all text under this node. 
        /// Perfect for feeding a highly specific context bundle to an LLM.
        /// </summary>
        public string GetFullText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Content);
            foreach (var child in Children)
            {
                sb.AppendLine(child.GetFullText());
            }
            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// A blazing fast, zero-dependency Markdown parser that builds a semantic hierarchy 
    /// instead of just flat HTML or dumb chunks.
    /// </summary>
    public class MarkdownTreeParser
    {
        public DocNode Parse(string markdown)
        {
            var root = new DocNode { Type = NodeType.Root, Content = "Document Root" };

            // We keep track of the most recent node at each header level
            // levels[0] = Root, levels[1] = H1, levels[2] = H2, etc.
            var levels = new DocNode[7];
            levels[0] = root;

            var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            DocNode? currentBlock = null;
            bool inCodeBlock = false;
            StringBuilder blockContent = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // 1. Handle Code Blocks
                if (trimmed.StartsWith("```"))
                {
                    if (inCodeBlock)
                    {
                        // Close code block
                        blockContent.AppendLine(line);
                        currentBlock!.Content = blockContent.ToString();
                        inCodeBlock = false;
                        currentBlock = null;
                        blockContent.Clear(); // <-- THE FIX: Clear buffer so subsequent paragraphs are clean!
                    }
                    else
                    {
                        // Open code block
                        FlushParagraph(levels, ref currentBlock, blockContent);
                        inCodeBlock = true;
                        currentBlock = new DocNode { Type = NodeType.CodeBlock };

                        // Attach code block to the deepest active header
                        GetDeepestActiveHeader(levels).AddChild(currentBlock);
                        blockContent.Clear();
                        blockContent.AppendLine(line);
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    blockContent.AppendLine(line);
                    continue;
                }

                // 2. Handle Headers
                if (trimmed.StartsWith("#"))
                {
                    int level = 0;
                    while (level < trimmed.Length && trimmed[level] == '#') level++;

                    if (level > 0 && level <= 6 && (trimmed.Length == level || trimmed[level] == ' '))
                    {
                        FlushParagraph(levels, ref currentBlock, blockContent);

                        var headerNode = new DocNode
                        {
                            Type = (NodeType)level, // Header1 is 1, Header2 is 2, etc.
                            Content = trimmed.Substring(level).Trim()
                        };

                        // A Header3 should be a child of the last Header2 (or H1, or Root)
                        // Find the closest parent that is a HIGHER level (lower number)
                        int parentLevel = level - 1;
                        while (parentLevel >= 0 && levels[parentLevel] == null)
                        {
                            parentLevel--;
                        }

                        levels[parentLevel]!.AddChild(headerNode);

                        // This node is now the active parent for its level
                        levels[level] = headerNode;

                        // Invalidate any deeper headers (a new H2 means the old H3s are no longer active parents)
                        for (int j = level + 1; j <= 6; j++) levels[j] = null;

                        continue;
                    }
                }

                // 3. Handle Empty Lines (used to split paragraphs)
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushParagraph(levels, ref currentBlock, blockContent);
                    continue;
                }

                // 4. Handle Standard Paragraph Text
                if (currentBlock == null)
                {
                    currentBlock = new DocNode { Type = NodeType.Paragraph };
                    GetDeepestActiveHeader(levels).AddChild(currentBlock);
                }
                blockContent.AppendLine(line);
            }

            FlushParagraph(levels, ref currentBlock, blockContent);

            return root;
        }

        private DocNode GetDeepestActiveHeader(DocNode[] levels)
        {
            for (int i = 6; i >= 0; i--)
            {
                if (levels[i] != null) return levels[i]!;
            }
            return levels[0]!;
        }

        private void FlushParagraph(DocNode[] levels, ref DocNode? currentBlock, StringBuilder blockContent)
        {
            if (currentBlock != null && blockContent.Length > 0)
            {
                currentBlock.Content = blockContent.ToString().TrimEnd();
                currentBlock = null;
                blockContent.Clear();
            }
        }
    }
}