using System;
using System.Collections.Generic;
using System.Text;
using Glacier.DocTree.Core;

namespace Glacier.DocTree.Traversal
{
    /// <summary>
    /// Provides high-speed semantic search capabilities over a parsed Document Tree.
    /// </summary>
    public class TreeSearch
    {
        private readonly DocNode _root;

        public TreeSearch(DocNode root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        /// <summary>
        /// Finds the first header (H1-H6) that exactly matches or contains the target text
        /// using iterative pre-order traversal.
        /// </summary>
        public DocNode? FindHeader(string headerText, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            ArgumentNullException.ThrowIfNull(headerText);

            using var stack = new ValueDocNodeStack<DocNode>();
            stack.Push(_root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current.Type >= NodeType.Header1 && current.Type <= NodeType.Header6)
                {
                    if (current.Content.Contains(headerText, comparison))
                    {
                        return current;
                    }
                }

                var children = current.Children;
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }

            return null;
        }

        /// <summary>
        /// Scans the entire tree iteratively for any node (paragraph, list, code block) containing a specific keyword.
        /// </summary>
        public List<DocNode> FindContainingText(string keyword, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            ArgumentNullException.ThrowIfNull(keyword);

            var results = new List<DocNode>();
            using var stack = new ValueDocNodeStack<DocNode>();
            stack.Push(_root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current.Type == NodeType.Paragraph || current.Type == NodeType.List || current.Type == NodeType.CodeBlock)
                {
                    if (current.Content.Contains(keyword, comparison))
                    {
                        results.Add(current);
                    }
                }

                var children = current.Children;
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }

            return results;
        }

        /// <summary>
        /// Walks UP the tree from a specific node to build its semantic breadcrumb path.
        /// Example output: "Root > API Documentation > Authentication > OAuth2"
        /// </summary>
        public static string GetSemanticPath(DocNode target)
        {
            ArgumentNullException.ThrowIfNull(target);

            var path = new List<string>();
            var current = target;
            var hare = target.Parent;

            while (current != null)
            {
                if (ReferenceEquals(current, hare))
                {
                    throw new InvalidOperationException("Cycle detected in DocNode parent chain.");
                }

                // For paragraphs and code blocks, include only the header hierarchy above them
                if (current.Type != NodeType.Paragraph && current.Type != NodeType.CodeBlock)
                {
                    path.Add(current.Content);
                }

                current = current.Parent;
                hare = hare?.Parent?.Parent;
            }

            path.Reverse();
            return string.Join(" > ", path);
        }

        /// <summary>
        /// Retrieves a bundled context frame for an LLM.
        /// Finds a target header, gets the breadcrumb path, and extracts all nested text.
        /// </summary>
        public string GetLlmContextBundle(string headerText)
        {
            var node = FindHeader(headerText);
            if (node == null) return $"[No section found matching '{headerText}']";

            var sb = new StringBuilder();
            sb.AppendLine("--- SEMANTIC CONTEXT ---");
            sb.AppendLine($"LOCATION: {GetSemanticPath(node)}");
            sb.AppendLine("--- BEGIN TEXT ---");
            sb.AppendLine(node.GetFullText());
            sb.AppendLine("--- END TEXT ---");

            return sb.ToString();
        }
    }
}
