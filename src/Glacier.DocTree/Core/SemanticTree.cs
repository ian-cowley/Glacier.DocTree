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
            if (markdown == null) throw new ArgumentNullException(nameof(markdown));
            return SpanMarkdownParser.Parse(markdown.AsSpan());
        }

        public DocNode Parse(ReadOnlySpan<char> markdown)
        {
            return SpanMarkdownParser.Parse(markdown);
        }
    }
}