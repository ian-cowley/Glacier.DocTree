using System;
using System.Collections.Generic;
using Glacier.DocTree.Core;
using Glacier.Graph.Storage;

namespace Glacier.DocTree.Context;

/// <summary>
/// Decouples invariant relational context (document AST section hierarchies, Markdown links, call graphs)
/// into zero-lock memory-mapped Compressed Sparse Row (CSR) binary stores resident in host RAM.
/// </summary>
public static class GraphContextDecoupler
{
    public const int Qwen25_7b_BytesPerToken = 57344;
    public const int Llama3_8b_BytesPerToken = 131072;

    /// <summary>
    /// Creates a symbolic prompt reference tag representing the decoupled graph,
    /// replacing thousands of relationship description tokens with a concise pointer.
    /// </summary>
    public static string CreateContextReference(string graphName, int nodeCount, int edgeCount) =>
        $"[GRAPH_CSR_REF: Graph={graphName}, Nodes={nodeCount}, Edges={edgeCount}]";

    /// <summary>
    /// Initializes an in-memory Forward Star graph store.
    /// </summary>
    public static GraphStore CreateGraphStore(int nodeCapacity = 1000, int edgeCapacity = 5000) =>
        new GraphStore(nodeCapacity, edgeCapacity);

    /// <summary>
    /// Extracts structural hierarchy edges (<c>CONTAINS</c>) and inline Markdown/wikilink cross-references
    /// (<c>LINKS_TO</c>) directly from a <see cref="DocNode"/> AST into a <see cref="GraphStore"/>.
    /// </summary>
    public static GraphStore ExtractDocumentGraph(DocNode root, GraphStore? targetStore = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var store = targetStore ?? CreateGraphStore();
        var slugToNodeId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int counter = 0;

        IndexHeadersRecursive(root, slugToNodeId, ref counter);
        counter = 0;
        PopulateEdgesRecursive(root, parentId: null, store, slugToNodeId, ref counter);
        return store;
    }

    private static void IndexHeadersRecursive(DocNode node, Dictionary<string, string> slugToNodeId, ref int counter)
    {
        string nodeId = BuildNodeKey(node, counter++);
        if (node.Type is >= NodeType.Header1 and <= NodeType.Header6 && !string.IsNullOrWhiteSpace(node.Content))
        {
            string slug = Slugify(node.Content);
            if (!string.IsNullOrEmpty(slug))
            {
                slugToNodeId.TryAdd(slug, nodeId);
            }
            slugToNodeId.TryAdd(node.Content.Trim(), nodeId);
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            IndexHeadersRecursive(node.Children[i], slugToNodeId, ref counter);
        }
    }

    private static void PopulateEdgesRecursive(
        DocNode node,
        string? parentId,
        GraphStore store,
        Dictionary<string, string> slugToNodeId,
        ref int counter)
    {
        string nodeId = BuildNodeKey(node, counter++);
        store.AddNode(nodeId);

        if (parentId != null)
        {
            store.AddEdge(parentId, nodeId, "CONTAINS", 1.0f);
        }

        if (!string.IsNullOrEmpty(node.Content) && node.Type != NodeType.CodeBlock)
        {
            ExtractInlineLinks(node.Content.AsSpan(), nodeId, store, slugToNodeId);
        }

        string nextSectionOwner = node.Type is NodeType.Root or (>= NodeType.Header1 and <= NodeType.Header6)
            ? nodeId
            : (parentId ?? nodeId);

        for (int i = 0; i < node.Children.Count; i++)
        {
            PopulateEdgesRecursive(node.Children[i], nodeId, store, slugToNodeId, ref counter);
            _ = nextSectionOwner;
        }
    }

    private static void ExtractInlineLinks(
        ReadOnlySpan<char> text,
        string sourceNodeId,
        GraphStore store,
        Dictionary<string, string> slugToNodeId)
    {
        int i = 0;
        while (i < text.Length)
        {
            // [[WikiLink]]
            if (i + 3 < text.Length && text[i] == '[' && text[i + 1] == '[')
            {
                int close = text[(i + 2)..].IndexOf("]]", StringComparison.Ordinal);
                if (close > 0)
                {
                    string rawTarget = text.Slice(i + 2, close).Trim().ToString();
                    string resolved = slugToNodeId.TryGetValue(rawTarget, out var mapped) ? mapped : rawTarget;
                    store.AddEdge(sourceNodeId, resolved, "LINKS_TO", 1.0f);
                    i += close + 4;
                    continue;
                }
            }

            // [Label](Target)
            if (text[i] == '[')
            {
                int closeBracket = text[(i + 1)..].IndexOf(']');
                if (closeBracket >= 0)
                {
                    int openParenIdx = i + 1 + closeBracket + 1;
                    if (openParenIdx < text.Length && text[openParenIdx] == '(')
                    {
                        int closeParen = text[(openParenIdx + 1)..].IndexOf(')');
                        if (closeParen > 0)
                        {
                            var rawSpan = text.Slice(openParenIdx + 1, closeParen).Trim();
                            string target = rawSpan.StartsWith("#")
                                ? rawSpan[1..].ToString()
                                : rawSpan.ToString();
                            string resolved = slugToNodeId.TryGetValue(target, out var mapped) ? mapped : target;
                            store.AddEdge(sourceNodeId, resolved, "LINKS_TO", 1.0f);
                            i = openParenIdx + 1 + closeParen + 1;
                            continue;
                        }
                    }
                }
            }

            i++;
        }
    }

    private static string BuildNodeKey(DocNode node, int index)
    {
        if (node.Type == NodeType.Root) return "Root";
        if (node.Type is >= NodeType.Header1 and <= NodeType.Header6 && !string.IsNullOrWhiteSpace(node.Content))
        {
            return $"{node.Type}:{node.Content.Trim()}";
        }
        return $"{node.Type}:{index}";
    }

    private static string Slugify(string heading)
    {
        Span<char> buf = heading.Length <= 256 ? stackalloc char[heading.Length] : new char[heading.Length];
        int pos = 0;
        for (int i = 0; i < heading.Length; i++)
        {
            char c = char.ToLowerInvariant(heading[i]);
            if (char.IsLetterOrDigit(c))
            {
                buf[pos++] = c;
            }
            else if ((c == ' ' || c == '-' || c == '_') && pos > 0 && buf[pos - 1] != '-')
            {
                buf[pos++] = '-';
            }
        }
        while (pos > 0 && buf[pos - 1] == '-') pos--;
        return new string(buf[..pos]);
    }

    /// <summary>
    /// Compiles the dynamic Forward Star graph into a static Compressed Sparse Row (GCSR) file.
    /// </summary>
    public static void CompileToCsr(GraphStore source, string outFilePath) =>
        CsrGraphCompiler.CompileFromForwardStar(source, outFilePath);

    /// <summary>
    /// Loads a memory-mapped CSR graph store from disk for zero-copy traversals.
    /// </summary>
    public static CsrGraphStore LoadCsrStore(string filePath, int maxMemoryBytes = 256 * 1024, bool useMemoryMapping = true) =>
        new CsrGraphStore(filePath, maxMemoryBytes, useMemoryMapping);

    /// <summary>
    /// Queries the neighbor list of a node directly from unmanaged memory without allocating heap arrays.
    /// </summary>
    public static bool TryGetNeighborsSpan(CsrGraphStore store, int internalNodeId, out ReadOnlySpan<int> neighbors) =>
        store.TryGetNeighbors(internalNodeId, out neighbors);

    /// <summary>
    /// Fast check for neighbor connectivity using SIMD search over CSR row slices.
    /// </summary>
    public static bool ContainsNeighbor(CsrGraphStore store, int sourceId, int targetId) =>
        store.ContainsNeighbor(sourceId, targetId);

    /// <summary>
    /// Computes token reduction achieved by substituting verbose graph text with a CSR reference.
    /// </summary>
    public static double CalculateSequenceLengthReduction(int textBpeTokenCount, int referenceTokenCount = 12)
    {
        if (referenceTokenCount <= 0) referenceTokenCount = 1;
        return (double)textBpeTokenCount / referenceTokenCount;
    }

    /// <summary>
    /// Computes KV cache bytes saved when graph relations bypass the prompt stream.
    /// </summary>
    public static (long BytesSaved, double PercentSaved) CalculateKvCacheSavings(int textBpeTokens, int referenceTokens, int bytesPerToken = Qwen25_7b_BytesPerToken)
    {
        long textFootprint = (long)textBpeTokens * bytesPerToken;
        long refFootprint = (long)referenceTokens * bytesPerToken;
        long saved = Math.Max(0, textFootprint - refFootprint);
        double percent = textFootprint > 0 ? (double)saved / textFootprint * 100.0 : 0.0;
        return (saved, percent);
    }
}
