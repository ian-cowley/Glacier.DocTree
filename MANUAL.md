# Glacier.DocTree Manual

Glacier.DocTree is a high-performance Markdown parser, structural chunker, and semantic document tree library for C# .NET 10 (built with `Glacier.Polaris`, `Glacier.Graph`, and `System.IO.Hashing`). It constructs hierarchical document structures, executes iterative contextual search, decouples tables/graphs into columnar and CSR memory, and supports lazy-loading JSON and zero-copy memory-mapped `.gdoc` v2 binary persistence.

---

## 1. Core Architecture

Unlike flat text parsers or HTML converters, Glacier.DocTree parses Markdown into a hierarchical parent-child structure (`DocNode`).

### A. NodeType Enum
- **Structural Nodes**: `Root`, `Header1` to `Header6`.
- **Content Nodes**: `Paragraph`, `CodeBlock`, `List`.
*(Pipe-delimited Markdown tables are decoupled via `ColumnarContextDecoupler.DecoupleMarkdownTable`, and inline links are extracted via `GraphContextDecoupler.ExtractDocumentGraph`.)*

### B. DocNode Representation
Every node in the tree inherits from `DocNode`:
- `NodeType Type { get; set; }`
- `string Content { get; set; }`
- `Dictionary<string, string> Metadata { get; set; }`
- `DocNode? Parent { get; set; }`
- `List<DocNode> Children { get; set; }`
- `string GetFullText()`: Iteratively gathers all text under this node.

---

## 2. Parsing Markdown (`MarkdownTreeParser` / `SpanMarkdownParser`)

Use the parser to convert Markdown strings or `ReadOnlySpan<char>` directly into a hierarchical tree.

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

## 3. Serialization & Lazy Loading (`TreeSerializer` & `.gdoc` v2)

To optimize memory usage when working with large documentation corpuses, you can serialize the tree structures to disk and load them on demand or via memory-mapped `.gdoc` v2 binaries.

### A. TreeSerializer
- **`Serialize(DocNode root, string directoryPath)`**: Iteratively traverses the document tree and serializes each node to its own JSON file inside `directoryPath` (atomically replacing `root.json` and cleaning up only stale child JSON files from the previous tree, leaving unrelated `*.json` files intact).
- **`DeserializeLazy(string directoryPath)`**: Loads only the root proxy node (`LazyDocNode`); child subtrees load on demand when accessed.
- **`SerializeBinary(DocNode root, string filePath)` / `OpenBinary(string filePath)` / `DeserializeBinary(string filePath)`**: Writes and memory-maps unified `.gdoc` v2 binary files (`0x434F4447`, 48-byte AST nodes, native `GdocMetadataEntry` metadata table, IEEE 802.3 CRC-32 checksum).

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

`TreeSearch` provides iterative traversals to locate relevant documentation segments and prepare formatted text bundles optimized for LLMs:
- **`FindHeader(headerText)`**: Iteratively searches the hierarchy to locate a header node.
- **`FindContainingText(keyword)`**: Iteratively scans content nodes (Paragraphs, Lists, CodeBlocks) for a specific keyword.
- **`GetSemanticPath(DocNode node)`**: Walks upward (with cycle detection) from `node` to return the breadcrumb path (e.g. `Document Root > API > Authentication > OAuth2`).
- **`GetLlmContextBundle(headerText)`**: Produces a structured bundle containing the node's semantic path and iteratively gathered text.
