using System;
using System.Text;
using Glacier.DocTree.Core;

namespace Glacier.DocTree.Parser
{
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
                        FlushParagraph(levels, ref currentBlock, blockContent);
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
                        FlushParagraph(levels, ref currentBlock, blockContent);

                        var headerNode = new DocNode
                        {
                            Type = (NodeType)level,
                            Content = trimmed[level..].Trim().ToString()
                        };

                        // Find closest parent that is a higher level (lower number)
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

                // 3. Empty lines (used to split paragraphs)
                if (trimmed.IsEmpty)
                {
                    FlushParagraph(levels, ref currentBlock, blockContent);
                    continue;
                }

                // 4. Standard Paragraph Text
                if (currentBlock == null)
                {
                    currentBlock = new DocNode { Type = NodeType.Paragraph };
                    GetDeepestActiveHeader(levels).AddChild(currentBlock);
                }
                blockContent.Append(line);
                blockContent.AppendLine();
            }

            FlushParagraph(levels, ref currentBlock, blockContent);
            return root;
        }

        private static DocNode GetDeepestActiveHeader(DocNode?[] levels)
        {
            for (int i = 6; i >= 0; i--)
            {
                if (levels[i] != null) return levels[i]!;
            }
            return levels[0]!;
        }

        private static void FlushParagraph(DocNode?[] levels, ref DocNode? currentBlock, StringBuilder blockContent)
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
