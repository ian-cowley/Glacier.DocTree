# Glacier.DocTree Manual

Glacier.DocTree is a blazing fast, zero-dependency Markdown parser and semantic document tree library for C# .NET 10. It is designed to construct hierarchical document structures, execute precise contextual search, and support low-memory operations via lazy-loading and serialization.

---

## 1. Core Architecture

Unlike flat text parsers or HTML converters, Glacier.DocTree parses Markdown into a hierarchical parent-child structure (`DocNode`).

### A. NodeType Enum
- **Structural Nodes**: `Root`, `Header1` to `Header6`.
- **Content Nodes**: `Paragraph`, `CodeBlock`, `List`.

### B. DocNode Representation
Every node in the tree inherits from `DocNode`:
- `NodeType Type { get; set; }`
- `string Content { get; set; }`
- `Dictionary<string, string> Metadata { get; set; }`
- `DocNode? Parent { get; set; }`
- `List<DocNode> Children { get; set; }`
- `string GetFullText()`: Recursively gathers all text under this node.

---

## 2. Parsing Markdown (`MarkdownTreeParser`)

Use the parser to convert markdown strings directly into a hierarchical tree.

```csharp
using Glacier.DocTree.Core;

string markdown = @"
# Introduction
Welcome to Glacier.

## Quickstart
Install the package via NuGet.
";

var parser = new MarkdownTreeParser();
var root = parser.Parse(markdown);
```

---

## 3. Serialization & Lazy Loading (`TreeSerializer`)

To optimize memory usage when working with large documentation corpuses, you can serialize the tree structures to disk and load them on demand.

### A. TreeSerializer
- **`Serialize(DocNode root, string directoryPath)`**: Traverses the document tree and serializes each node individually to its own JSON file inside `directoryPath`.
- **`DeserializeLazy(string directoryPath)`**: Loads only the root metadata and returns a lazy proxy node.

### B. LazyDocNode
A proxy subclass of `DocNode` that overrides the base properties. Sub-nodes (children and properties) are loaded dynamically on demand only when they are accessed.

#### Example: Out-of-Core Serialization & Semantic Search

```csharp
using Glacier.DocTree.Core;
using Glacier.DocTree.Traversal;

// Parse a document
var parser = new MarkdownTreeParser();
var originalRoot = parser.Parse(largeMarkdownString);

// Serialize tree to disk
TreeSerializer.Serialize(originalRoot, "./doc_cache");

// Load lazily (requires negligible memory)
DocNode lazyRoot = TreeSerializer.DeserializeLazy("./doc_cache");

// Search the lazy tree - nodes are loaded from disk only when traversed!
var search = new TreeSearch(lazyRoot);
var section = search.FindHeader("Usage Limits");

if (section != null)
{
    Console.WriteLine(section.GetFullText());
}
```

---

## 4. Traversal and LLM Context Extraction (`TreeSearch`)

`TreeSearch` provides capabilities to locate relevant documentation segments and prepare formatted text bundles optimized for LLMs:
- **`FindHeader(headerText)`**: Searches the hierarchy to locate a header node.
- **`FindContainingText(keyword)`**: Scans content nodes (Paragraphs, Lists, CodeBlocks) for a specific keyword.
- **`GetSemanticPath(DocNode node)`**: Returns the path from root to the node (e.g. `Root > API > Authentication > OAuth2`).
- **`GetLlmContextBundle(headerText)`**: Produces a structured Markdown string containing the node's full text, path, and boundaries, ideal for inclusion in an LLM system prompt.
