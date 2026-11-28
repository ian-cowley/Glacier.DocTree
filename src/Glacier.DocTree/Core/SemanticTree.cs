using System;
using System.Collections.Generic;
using System.Text;
using Glacier.DocTree.Parser;

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
        /// Iteratively gathers all text under this node in pre-order depth-first traversal.
        /// </summary>
        public string GetFullText()
        {
            var sb = new StringBuilder();
            using var stack = new ValueDocNodeStack<DocNode>();
            HashSet<DocNode>? visited = null;
            int iterations = 0;

            stack.Push(this);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (++iterations > 1024)
                {
                    visited ??= new HashSet<DocNode>(ReferenceEqualityComparer.Instance);
                    if (!visited.Add(current))
                    {
                        continue;
                    }
                }

                sb.AppendLine(current.Content);
                var children = current.Children;
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }

            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// Span-tokenized Markdown parser that builds a semantic <see cref="DocNode"/> hierarchy.
    /// </summary>
    public class MarkdownTreeParser
    {
        public DocNode Parse(string markdown)
        {
            if (markdown == null) throw new ArgumentNullException(nameof(markdown));
            return SpanMarkdownParser.Parse(markdown.AsSpan());
        }

        public DocNode Parse(ReadOnlySpan<char> markdown)
        {
            return SpanMarkdownParser.Parse(markdown);
        }
    }
}
