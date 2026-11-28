using System;
using Glacier.DocTree.Core;
using Glacier.DocTree.Traversal;

namespace Glacier.DocTree.Demo
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine("==========================================");
            Console.WriteLine(" Glacier.DocTree | Semantic Parser Engine");
            Console.WriteLine("==========================================\n");

            // 1. A simulated, somewhat messy Markdown document
            string rawMarkdown = @"
# Glacier Enterprise API

Welcome to the Glacier API. This document outlines the core integration steps.

## Authentication

All requests to the API must be cryptographically signed or use a Bearer token.

### OAuth 2.0

To authenticate via OAuth2, you must include your token in the authorization header.

```json
{ 
  ""Authorization"": ""Bearer eyJhb..."" 
}
```

## Usage Policies

Please adhere to the following usage guidelines.

### Rate Limits
        
Free tier users are limited to 100 requests per minute.
Enterprise users have unlimited access.
If you exceed the limit, you will receive an HTTP 429 status code.

### Acceptable Use

Do not use the API to train competing LLM models.
";

            // 2. Parse the document into a Tree
            Console.WriteLine("[1] Parsing Markdown into Semantic Tree...");
            var parser = new MarkdownTreeParser();
            var root = parser.Parse(rawMarkdown);

            Console.WriteLine("\n[2] Visualizing the Document Structure:");
            PrintTreeVisually(root, 0);

            // 3. Execute Semantic Search
            Console.WriteLine("\n[3] Simulating Agent Query: 'Extract Rate Limits context'");
            var search = new TreeSearch(root);

            string llmBundle = search.GetLlmContextBundle("Rate Limits");

            Console.WriteLine("\n" + llmBundle);

            Console.WriteLine("==========================================");
            Console.WriteLine(" DOCTREE ENGINE READY FOR AGENT DEVKIT");
            Console.WriteLine("==========================================");
        }

        // Iteratively print the tree structure to the console
        static void PrintTreeVisually(DocNode root, int initialIndent)
        {
            var stack = new System.Collections.Generic.Stack<(DocNode Node, int Indent)>();
            stack.Push((root, initialIndent));

            while (stack.Count > 0)
            {
                var (node, indent) = stack.Pop();
                string indentStr = new string(' ', indent * 2);

                string displayContent = node.Content;
                if (node.Type == NodeType.Paragraph || node.Type == NodeType.CodeBlock)
                {
                    // Truncate long paragraphs/code for the visualizer
                    displayContent = displayContent.Replace("\n", " ").Replace("\r", "");
                    if (displayContent.Length > 40)
                        displayContent = displayContent.Substring(0, 37) + "...";
                }

                Console.WriteLine($"{indentStr}└─ [{node.Type}] {displayContent}");

                for (int i = node.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push((node.Children[i], indent + 1));
                }
            }
        }
    }
}