using System;
using System.IO;
using System.Linq;
using Glacier.DocTree.Context;
using Xunit;

namespace Glacier.DocTree.Tests;

public class ContextDecouplingTests
{
    [Fact]
    public void ColumnarContextDecoupler_DecouplesTelemetryAndProvidesUnmanagedSpans()
    {
        // Generate 500 deterministic telemetry records
        var records = Enumerable.Range(1, 500).Select(i => (
            Id: i,
            DeviceId: 1000 + (i % 8),
            Metric: 50.0f + (i * 0.1f),
            Status: i % 2
        )).ToList();

        using var df = ColumnarContextDecoupler.DecoupleTelemetryRecords(records);

        Assert.Equal(500, df.RowCount);
        Assert.Equal(4, df.Columns.Count);

        // Access unmanaged spans directly
        ReadOnlySpan<int> idSpan = ColumnarContextDecoupler.GetInt32ColumnSpan(df, "Id");
        ReadOnlySpan<int> devSpan = ColumnarContextDecoupler.GetInt32ColumnSpan(df, "DeviceId");
        ReadOnlySpan<float> metricSpan = ColumnarContextDecoupler.GetFloat32ColumnSpan(df, "Metric");
        ReadOnlySpan<int> statusSpan = ColumnarContextDecoupler.GetInt32ColumnSpan(df, "Status");

        Assert.Equal(500, idSpan.Length);
        Assert.Equal(1, idSpan[0]);
        Assert.Equal(500, idSpan[499]);
        Assert.Equal(1001, devSpan[0]);
        Assert.Equal(50.1f, metricSpan[0], 0.001f);
        Assert.Equal(1, statusSpan[0]);

        // Check reference string
        string reference = ColumnarContextDecoupler.CreateContextReference("FleetTelemetry", df.RowCount, df.Columns.Count);
        Assert.Equal("[POLARIS_REF: Table=FleetTelemetry, Rows=500, Cols=4]", reference);

        // Check token savings calculation
        // 500 JSON rows serialized as text is ~21,832 BPE tokens.
        // Symbolic pointer is ~12 BPE tokens.
        double reduction = ColumnarContextDecoupler.CalculateSequenceLengthReduction(21832, 12);
        Assert.True(reduction > 1000.0, $"Reduction was {reduction:F1}x");

        var (bytesSaved, percentSaved) = ColumnarContextDecoupler.CalculateKvCacheSavings(21832, 12, ColumnarContextDecoupler.Qwen25_7b_BytesPerToken);
        Assert.True(bytesSaved > 1_000_000_000, $"Saved {bytesSaved} bytes");
        Assert.True(percentSaved > 99.0);
    }

    [Fact]
    public void GraphContextDecoupler_BuildsCompilesAndQueriesCsrStore()
    {
        string tempCsrPath = Path.Combine(Path.GetTempPath(), $"test_graph_{Guid.NewGuid():N}.gcsr");

        try
        {
            var store = GraphContextDecoupler.CreateGraphStore(nodeCapacity: 100, edgeCapacity: 200);

            // Add 10 nodes and directed edges (AST call hierarchy)
            for (int i = 1; i <= 9; i++)
            {
                store.AddEdge($"Method_{i}", $"Method_{i + 1}", "CALLS", 1.0f);
            }
            store.AddEdge("Method_1", "Method_5", "CALLS", 1.0f);

            Assert.Equal(10, store.NodeCount);
            Assert.Equal(10, store.EdgeCount);

            // Compile to static CSR file
            GraphContextDecoupler.CompileToCsr(store, tempCsrPath);
            Assert.True(File.Exists(tempCsrPath));

            // Load memory-mapped CSR store
            using var csr = GraphContextDecoupler.LoadCsrStore(tempCsrPath, useMemoryMapping: true);
            Assert.Equal(10, csr.NodeCount);
            Assert.Equal(10, csr.EdgeCount);

            int method1Internal = csr.GetExternalToInternalId("Method_1");
            int method2Internal = csr.GetExternalToInternalId("Method_2");
            int method5Internal = csr.GetExternalToInternalId("Method_5");

            Assert.True(method1Internal > 0);
            Assert.True(method2Internal > 0);
            Assert.True(method5Internal > 0);

            // Verify unmanaged neighbor span query
            bool foundNeighbors = GraphContextDecoupler.TryGetNeighborsSpan(csr, method1Internal, out ReadOnlySpan<int> neighbors);
            Assert.True(foundNeighbors);
            Assert.Equal(2, neighbors.Length); // Method_1 calls Method_2 and Method_5

            // Verify ContainsNeighbor
            Assert.True(GraphContextDecoupler.ContainsNeighbor(csr, method1Internal, method2Internal));
            Assert.True(GraphContextDecoupler.ContainsNeighbor(csr, method1Internal, method5Internal));
            Assert.False(GraphContextDecoupler.ContainsNeighbor(csr, method1Internal, method1Internal));

            // Verify reference string
            string refStr = GraphContextDecoupler.CreateContextReference("AstCallGraph", csr.NodeCount, csr.EdgeCount);
            Assert.Equal("[GRAPH_CSR_REF: Graph=AstCallGraph, Nodes=10, Edges=10]", refStr);

            // Verify KV cache savings
            var (bytesSaved, percentSaved) = GraphContextDecoupler.CalculateKvCacheSavings(12000, 12, GraphContextDecoupler.Llama3_8b_BytesPerToken);
            Assert.True(bytesSaved > 1_500_000_000, $"Saved {bytesSaved} bytes");
            Assert.True(percentSaved > 99.0);
        }
        finally
        {
            if (File.Exists(tempCsrPath))
            {
                try { File.Delete(tempCsrPath); } catch { }
            }
        }
    }

    [Fact]
    public void ColumnarContextDecoupler_ThrowsOnMissingColumn()
    {
        var records = new[] { (Id: 1, DeviceId: 10, Metric: 1.0f, Status: 0) };
        using var df = ColumnarContextDecoupler.DecoupleTelemetryRecords(records);

        Assert.Throws<ArgumentException>(() => ColumnarContextDecoupler.GetInt32ColumnSpan(df, "NonExistentColumn"));
        Assert.Throws<ArgumentException>(() => ColumnarContextDecoupler.GetFloat32ColumnSpan(df, "NonExistentColumn"));
    }

    [Fact]
    public void GraphContextDecoupler_HandlesNonExistentNeighborGracefully()
    {
        string tempCsrPath = Path.Combine(Path.GetTempPath(), $"test_empty_{Guid.NewGuid():N}.gcsr");
        try
        {
            var store = GraphContextDecoupler.CreateGraphStore(10, 10);
            store.AddNode("IsolatedNode");
            GraphContextDecoupler.CompileToCsr(store, tempCsrPath);

            using var csr = GraphContextDecoupler.LoadCsrStore(tempCsrPath, useMemoryMapping: true);
            int id = csr.GetExternalToInternalId("IsolatedNode");
            Assert.True(id > 0);

            bool found = GraphContextDecoupler.TryGetNeighborsSpan(csr, id, out ReadOnlySpan<int> neighbors);
            Assert.True(found);
            Assert.Equal(0, neighbors.Length);
            Assert.False(GraphContextDecoupler.ContainsNeighbor(csr, id, 999));
        }
        finally
        {
            if (File.Exists(tempCsrPath))
            {
                try { File.Delete(tempCsrPath); } catch { }
            }
        }
    }
}
