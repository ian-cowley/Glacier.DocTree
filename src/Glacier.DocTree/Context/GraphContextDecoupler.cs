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
    /// (<c>LINKS_TO</c>) directly from a <see cref="DocNode"/> AST into a <see cref="GraphStore"/> using iterative traversal.
    /// </summary>
    public static GraphStore ExtractDocumentGraph(DocNode root, GraphStore? targetStore = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var store = targetStore ?? CreateGraphStore();
        var slugToNodeId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        IndexHeadersIterative(root, slugToNodeId);
        PopulateEdgesIterative(root, store, slugToNodeId);
        return store;
    }

    private static void IndexHeadersIterative(DocNode root, Dictionary<string, string> slugToNodeId)
    {
        var slugCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var stack = new ValueDocNodeStack<(DocNode Node, string? ParentKey, int ParentIndex, int Depth, ulong ParentPathHash, string? LastHeaderSlug, string? BoundedSlugPath, int HeaderDepth)>();
        stack.Push((root, null, -1, 0, FnvOffsetBasis64, null, null, 0));

        int counter = 0;
        while (stack.Count > 0)
        {
            var (node, parentKey, parentIndex, depth, parentPathHash, lastHeaderSlug, boundedSlugPath, headerDepth) = stack.Pop();
            int nodeIndex = counter++;
            string nodeId = BuildNodeKey(node, nodeIndex, parentKey, parentIndex, depth, parentPathHash, out ulong nodePathHash);
            string? nextLastHeaderSlug = lastHeaderSlug;
            string? nextBoundedSlugPath = boundedSlugPath;
            int nextHeaderDepth = headerDepth;

            if (node.Type is >= NodeType.Header1 and <= NodeType.Header6 && !string.IsNullOrWhiteSpace(node.Content))
            {
                string trimmed = node.Content.Trim();
                string slug = Slugify(trimmed);
                if (!string.IsNullOrEmpty(slug))
                {
                    slugToNodeId.TryAdd(slug, nodeId);

                    if (!string.IsNullOrEmpty(lastHeaderSlug))
                    {
                        slugToNodeId.TryAdd($"{lastHeaderSlug}/{slug}", nodeId);
                    }

                    if (headerDepth == 0 && string.IsNullOrEmpty(boundedSlugPath))
                    {
                        nextBoundedSlugPath = slug.Length <= 256 ? slug : null;
                    }
                    else if (!string.IsNullOrEmpty(boundedSlugPath) &&
                             headerDepth < 8 &&
                             boundedSlugPath.Length + 1 + slug.Length <= 256)
                    {
                        string pathSlug = $"{boundedSlugPath}/{slug}";
                        slugToNodeId.TryAdd(pathSlug, nodeId);
                        nextBoundedSlugPath = pathSlug;
                    }
                    else
                    {
                        nextBoundedSlugPath = null;
                    }

                    nextLastHeaderSlug = slug;
                    nextHeaderDepth = headerDepth + 1;

                    if (slugCounts.TryGetValue(slug, out int seen))
                    {
                        slugToNodeId.TryAdd($"{slug}-{seen}", nodeId);
                        slugCounts[slug] = seen + 1;
                    }
                    else
                    {
                        slugCounts[slug] = 1;
                    }
                }

                slugToNodeId.TryAdd(trimmed, nodeId);
            }

            var children = node.Children;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((children[i], nodeId, nodeIndex, depth + 1, nodePathHash, nextLastHeaderSlug, nextBoundedSlugPath, nextHeaderDepth));
            }
        }
    }

    private static void PopulateEdgesIterative(
        DocNode root,
        GraphStore store,
        Dictionary<string, string> slugToNodeId)
    {
        using var stack = new ValueDocNodeStack<(DocNode Node, string? ParentKey, int ParentIndex, int Depth, ulong ParentPathHash)>();
        stack.Push((root, null, -1, 0, FnvOffsetBasis64));

        int counter = 0;
        while (stack.Count > 0)
        {
            var (node, parentKey, parentIndex, depth, parentPathHash) = stack.Pop();
            int nodeIndex = counter++;
            string nodeId = BuildNodeKey(node, nodeIndex, parentKey, parentIndex, depth, parentPathHash, out ulong nodePathHash);
            store.AddNode(nodeId);

            if (parentKey != null)
            {
                store.AddEdge(parentKey, nodeId, "CONTAINS", 1.0f);
            }

            if (!string.IsNullOrEmpty(node.Content) && node.Type != NodeType.CodeBlock)
            {
                ExtractInlineLinks(node.Content.AsSpan(), nodeId, store, slugToNodeId);
            }

            var children = node.Children;
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((children[i], nodeId, nodeIndex, depth + 1, nodePathHash));
            }
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
                    string resolved = ResolveLinkTarget(rawTarget, slugToNodeId);
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
                                ? rawSpan[1..].Trim().ToString()
                                : rawSpan.ToString();
                            string resolved = ResolveLinkTarget(target, slugToNodeId);
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

    private static string ResolveLinkTarget(string rawTarget, Dictionary<string, string> slugToNodeId)
    {
        if (slugToNodeId.TryGetValue(rawTarget, out string? mapped))
        {
            return mapped;
        }

        if (rawTarget.IndexOf('/') >= 0)
        {
            string normalizedPath = NormalizePathSlug(rawTarget.AsSpan());
            if (!string.IsNullOrEmpty(normalizedPath) && slugToNodeId.TryGetValue(normalizedPath, out mapped))
            {
                return mapped;
            }
        }
        else
        {
            string normalized = Slugify(rawTarget);
            if (!string.IsNullOrEmpty(normalized) && slugToNodeId.TryGetValue(normalized, out mapped))
            {
                return mapped;
            }
        }

        return rawTarget;
    }

    private static string NormalizePathSlug(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty) return string.Empty;

        var sb = new System.Text.StringBuilder(path.Length);
        while (!path.IsEmpty)
        {
            int slash = path.IndexOf('/');
            ReadOnlySpan<char> segment = slash >= 0 ? path[..slash] : path;
            string segSlug = Slugify(segment.Trim().ToString());
            if (!string.IsNullOrEmpty(segSlug))
            {
                if (sb.Length > 0)
                {
                    sb.Append('/');
                }
                sb.Append(segSlug);
            }

            if (slash < 0)
            {
                break;
            }
            path = path[(slash + 1)..];
        }
        return sb.ToString();
    }

    private const ulong FnvOffsetBasis64 = 14695981039346656037UL;
    private const ulong FnvPrime64 = 1099511628211UL;

    private static ulong AdvancePathHash(ulong hash, string segment)
    {
        hash ^= (ulong)'/';
        hash *= FnvPrime64;
        for (int i = 0; i < segment.Length; i++)
        {
            hash ^= segment[i];
            hash *= FnvPrime64;
        }
        return hash;
    }

    private static string BuildNodeKey(
        DocNode node,
        int index,
        string? parentKey,
        int parentIndex,
        int depth,
        ulong parentPathHash,
        out ulong nodePathHash)
    {
        if (node.Type == NodeType.Root)
        {
            string rootKey = index == 0 ? "Root" : $"Root#{index}";
            nodePathHash = AdvancePathHash(parentPathHash, rootKey);
            return rootKey;
        }

        string localKey = node.Type is >= NodeType.Header1 and <= NodeType.Header6 && !string.IsNullOrWhiteSpace(node.Content)
            ? $"{node.Type}:{node.Content.Trim()}#{index}"
            : $"{node.Type}#{index}";

        nodePathHash = AdvancePathHash(parentPathHash, localKey);

        if (string.IsNullOrEmpty(parentKey) || string.Equals(parentKey, "Root", StringComparison.Ordinal))
        {
            return localKey;
        }

        if (depth <= 4 && parentKey.Length <= 128)
        {
            return $"{parentKey}/{localKey}";
        }

        return $"{parentPathHash:x16}/@{parentIndex}/{localKey}";
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
