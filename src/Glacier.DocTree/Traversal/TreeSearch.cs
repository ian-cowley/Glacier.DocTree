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
        /// Finds the first header (H1-H6) that exactly matches or contains the target text.
        /// Perfect for Agent queries like: "Extract the 'Terms of Service' section".
        /// </summary>
        public DocNode? FindHeader(string headerText, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            return FindHeaderRecursive(_root, headerText, comparison);
        }

        private DocNode? FindHeaderRecursive(DocNode current, string target, StringComparison comparison)
        {
            if (current.Type >= NodeType.Header1 && current.Type <= NodeType.Header6)
            {
                if (current.Content.Contains(target, comparison))
                {
                    return current;
                }
            }

            foreach (var child in current.Children)
            {
                var result = FindHeaderRecursive(child, target, comparison);
                if (result != null) return result;
            }

            return null;
        }

        /// <summary>
        /// Scans the entire tree for any node (paragraph, list, code block) containing a specific keyword.
        /// </summary>
        public List<DocNode> FindContainingText(string keyword, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            var results = new List<DocNode>();
            FindContainingTextRecursive(_root, keyword, comparison, results);
            return results;
        }

        private void FindContainingTextRecursive(DocNode current, string keyword, StringComparison comparison, List<DocNode> results)
        {
            if (current.Type == NodeType.Paragraph || current.Type == NodeType.List || current.Type == NodeType.CodeBlock)
            {
                if (current.Content.Contains(keyword, comparison))
                {
                    results.Add(current);
                }
            }

            foreach (var child in current.Children)
            {
                FindContainingTextRecursive(child, keyword, comparison, results);
            }
        }

        /// <summary>
        /// Walks UP the tree from a specific node to build its semantic breadcrumb path.
        /// Example output: "Root > API Documentation > Authentication > OAuth2"
        /// This gives LLMs absolute certainty about the context of the text they are reading.
        /// </summary>
        public static string GetSemanticPath(DocNode target)
        {
            var path = new List<string>();
            var current = target;

            while (current != null)
            {
                // For paragraphs, we just want the header hierarchy above them
                if (current.Type != NodeType.Paragraph && current.Type != NodeType.CodeBlock)
                {
                    path.Add(current.Content);
                }
                current = current.Parent;
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
            sb.AppendLine($"--- SEMANTIC CONTEXT ---");
            sb.AppendLine($"LOCATION: {GetSemanticPath(node)}");
            sb.AppendLine($"--- BEGIN TEXT ---");
            sb.AppendLine(node.GetFullText());
            sb.AppendLine($"--- END TEXT ---");

            return sb.ToString();
        }
    }
}