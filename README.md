# Glacier.DocTree

[![NuGet Version](https://img.shields.io/nuget/v/Glacier.DocTree.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.DocTree/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.DocTree.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.DocTree/)

**Glacier.DocTree** is a blazing-fast, zero-dependency Markdown parser and semantic chunking engine for .NET 10. Built explicitly for Retrieval-Augmented Generation (RAG), AI search context indexing, and semantic document analysis, it parses flat Markdown text into a nested, structural **Document Object Tree** (`DocNode`).

Unlike dumb sliding-window text splitters, Glacier.DocTree maintains the structural parent-child relationships between headers (`H1` to `H6`), paragraphs, code blocks, and list items. This ensures LLMs are supplied with absolute context, correct headers, and fully formed semantic blocks.

---

## Key Features

*   📂 **Hierarchical Markdown Parser**: Transforms raw Markdown text into a fully structural tree of `DocNode` elements (Root, H1–H6, Paragraphs, CodeBlocks, Lists).
*   🧭 **Semantic Path Generation**: Recursively traces a node upward to generate semantic breadcrumbs (e.g. `Root > API Manual > Security > OAuth2.0`). Bypasses Naive RAG limitations by giving your AI model complete hierarchy context.
*   🧠 **LLM Context Bundles**: Instantly extracts a specific document section and packages it into a beautifully formatted RAG frame including header paths and all recursively nested texts.
*   🔍 **High-Speed Traversals**: Features fast keyword and exact header search traversal tools across parsed trees.
*   ⚡ **Zero-Dependency Engine**: Built from the ground up for high-performance memory footprints with zero heavy external dependency requirements.

---

## Installation

Glacier.DocTree is available as a [NuGet package](https://www.nuget.org/packages/Glacier.DocTree). Install it using the .NET CLI:

```bash
dotnet add package Glacier.DocTree
```

---

## Quick Start

### 1. Parse a Markdown Document

Initialize the `MarkdownTreeParser` to turn your raw Markdown into a hierarchical tree:

```csharp
using Glacier.DocTree.Core;
using Glacier.DocTree.Traversal;

string markdown = @"
# API Documentation
This is the root API description.

## Authentication
To authenticate, you must use an API key.

### Header Validation
Provide the API key in the `X-API-Key` header:
```bash
curl -H 'X-API-Key: secret_key' https://api.example.com/v1/data
```
";

var parser = new MarkdownTreeParser();
DocNode treeRoot = parser.Parse(markdown);
```

### 2. Traverse and Query Sections

Use `TreeSearch` to find specific headings or filter sections:

```csharp
var search = new TreeSearch(treeRoot);

// Find a specific section header
DocNode? authSection = search.FindHeader("Authentication");
if (authSection != null)
{
    Console.WriteLine($"Found Heading: {authSection.Content} (Type: {authSection.Type})");
}
```

### 3. Generate Semantic Breadcrumbs

Instantly track the parent hierarchy path of any node:

```csharp
DocNode? headerValidation = search.FindHeader("Header Validation");
if (headerValidation != null)
{
    string path = TreeSearch.GetSemanticPath(headerValidation);
    Console.WriteLine($"Semantic Path: {path}");
    // Output: API Documentation > Authentication > Header Validation
}
```

### 4. Create RAG Context Bundles

Extract a clean, fully contextualized text bundle to feed directly to an LLM prompt:

```csharp
string contextBundle = search.GetLlmContextBundle("Header Validation");
Console.WriteLine(contextBundle);

/*
Output:
--- SEMANTIC CONTEXT ---
LOCATION: API Documentation > Authentication > Header Validation
--- BEGIN TEXT ---
Header Validation
Provide the API key in the X-API-Key header:

curl -H 'X-API-Key: secret_key' https://api.example.com/v1/data

--- END TEXT ---
*/
```

---

## Architecture Overview

1.  **Semantic Parser Strategy**: Walks text lines sequentially, dynamically tracking open headers using a back-referencing active parent index stack.
2.  **No-Buffer Flushing**: Seamlessly handles inline text transitions and terminates open blocks cleanly when parsing boundary markers (like code block indicators ```` ``` ````).
3.  **Recursive Gathering**: Resolves full descendant text using recursive structural aggregators that eliminate raw text array fragmentation.

---

## Contributing

We welcome community contributions! Please read [CONTRIBUTING.md](CONTRIBUTING.md) for local setups, branch models, and PR checklist details.

## Credits

Developed by **Ian Cowley** and **Antigravity (Google DeepMind)**.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
