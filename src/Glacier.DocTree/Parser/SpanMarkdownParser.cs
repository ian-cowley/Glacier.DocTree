using System;
using System.Text;
using Glacier.DocTree.Core;

namespace Glacier.DocTree.Parser
{
    /// <summary>
    /// Span-tokenized Markdown parser that constructs a hierarchical <see cref="DocNode"/> AST
    /// covering headings (H1–H6), fenced code blocks, unordered/ordered lists, and paragraphs.
    /// </summary>
    public static class SpanMarkdownParser
    {
        public static DocNode Parse(ReadOnlySpan<char> markdown)
        {
            var root = new DocNode { Type = NodeType.Root, Content = "Document Root" };

            // levels[0] = Root, levels[1] = H1, ..., levels[6] = H6
            var levels = new DocNode?[7];
            levels[0] = root;

            DocNode? currentBlock = null;
            bool inCodeBlock = false;
            var blockContent = new StringBuilder();

            var enumerator = new SpanLineEnumerator(markdown);
            while (enumerator.MoveNext())
            {
                var line = enumerator.Current;
                var trimmed = line.TrimStart();

                // 1. Code block handling
                if (trimmed.StartsWith("```"))
                {
                    if (inCodeBlock)
                    {
                        blockContent.Append(line);
                        blockContent.AppendLine();
                        currentBlock!.Content = blockContent.ToString().TrimEnd();
                        inCodeBlock = false;
                        currentBlock = null;
                        blockContent.Clear();
                    }
                    else
                    {
                        FlushBlock(ref currentBlock, blockContent);
                        inCodeBlock = true;
                        currentBlock = new DocNode { Type = NodeType.CodeBlock };
                        GetDeepestActiveHeader(levels).AddChild(currentBlock);
                        blockContent.Clear();
                        blockContent.Append(line);
                        blockContent.AppendLine();
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    blockContent.Append(line);
                    blockContent.AppendLine();
                    continue;
                }

                // 2. Header handling (#)
                if (trimmed.StartsWith("#"))
                {
                    int level = 0;
                    while (level < trimmed.Length && trimmed[level] == '#')
                    {
                        level++;
                    }

                    if (level > 0 && level <= 6 && (trimmed.Length == level || trimmed[level] == ' '))
                    {
                        FlushBlock(ref currentBlock, blockContent);

                        var headerNode = new DocNode
                        {
                            Type = (NodeType)level,
                            Content = trimmed[level..].Trim().ToString()
                        };

                        int parentLevel = level - 1;
                        while (parentLevel >= 0 && levels[parentLevel] == null)
                        {
                            parentLevel--;
                        }

                        levels[parentLevel]!.AddChild(headerNode);
                        levels[level] = headerNode;

                        for (int j = level + 1; j <= 6; j++)
                        {
                            levels[j] = null;
                        }

                        continue;
                    }
                }

                // 3. Empty lines (used to split blocks)
                if (trimmed.IsEmpty)
                {
                    FlushBlock(ref currentBlock, blockContent);
                    continue;
                }

                // 4. Unordered / Ordered List items (- , * , + , 1. )
                if (IsListItem(trimmed))
                {
                    if (currentBlock != null && currentBlock.Type != NodeType.List)
                    {
                        FlushBlock(ref currentBlock, blockContent);
                    }
                    if (currentBlock == null)
                    {
                        currentBlock = new DocNode { Type = NodeType.List };
                        GetDeepestActiveHeader(levels).AddChild(currentBlock);
                    }
                    blockContent.Append(line);
                    blockContent.AppendLine();
                    continue;
                }

                // 5. Standard Paragraph Text (or indented continuation of a list item)
                if (currentBlock != null && currentBlock.Type == NodeType.List)
                {
                    if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                    {
                        blockContent.Append(line);
                        blockContent.AppendLine();
                        continue;
                    }
                    FlushBlock(ref currentBlock, blockContent);
                }

                if (currentBlock == null)
                {
                    currentBlock = new DocNode { Type = NodeType.Paragraph };
                    GetDeepestActiveHeader(levels).AddChild(currentBlock);
                }
                blockContent.Append(line);
                blockContent.AppendLine();
            }

            FlushBlock(ref currentBlock, blockContent);
            return root;
        }

        private static bool IsListItem(ReadOnlySpan<char> trimmed)
        {
            if (trimmed.Length >= 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
            {
                return true;
            }

            int idx = 0;
            while (idx < trimmed.Length && char.IsAsciiDigit(trimmed[idx]))
            {
                idx++;
            }

            return idx > 0 && idx + 1 < trimmed.Length && (trimmed[idx] == '.' || trimmed[idx] == ')') && trimmed[idx + 1] == ' ';
        }

        private static DocNode GetDeepestActiveHeader(DocNode?[] levels)
        {
            for (int i = 6; i >= 0; i--)
            {
                if (levels[i] != null) return levels[i]!;
            }
            return levels[0]!;
        }

        private static void FlushBlock(ref DocNode? currentBlock, StringBuilder blockContent)
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
