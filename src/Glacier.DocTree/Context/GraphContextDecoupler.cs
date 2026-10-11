using System;
using System.IO;
using Glacier.Graph.Storage;

namespace Glacier.DocTree.Context;

/// <summary>
/// Decouples invariant relational context (AST call graphs, symbol hierarchies, ontology dependencies)
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
