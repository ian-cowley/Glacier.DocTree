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
        Assert.Equal(21832.0 / 12.0, reduction, precision: 6);

        var (bytesSaved, percentSaved) = ColumnarContextDecoupler.CalculateKvCacheSavings(21832, 12, ColumnarContextDecoupler.Qwen25_7b_BytesPerToken);
        long expectedSaved = (21832L - 12L) * ColumnarContextDecoupler.Qwen25_7b_BytesPerToken;
        Assert.Equal(expectedSaved, bytesSaved);
        Assert.Equal((double)expectedSaved / (21832L * ColumnarContextDecoupler.Qwen25_7b_BytesPerToken) * 100.0, percentSaved, precision: 6);
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

    [Fact]
    public void ColumnarContextDecoupler_DecoupleMarkdownTable_ParsesTypedColumns()
    {
        string mdTable = @"
| Id | Service | LatencyMs |
| --- | :---: | ---: |
| 10 | Auth | 12.5 |
| 20 | Search | 4.25 |
";
        using var df = ColumnarContextDecoupler.DecoupleMarkdownTable(mdTable.AsSpan());
        Assert.Equal(2, df.RowCount);
        Assert.Equal(3, df.Columns.Count);
        Assert.Equal(10, ColumnarContextDecoupler.GetInt32ColumnSpan(df, "Id")[0]);
        Assert.Equal(4.25f, ColumnarContextDecoupler.GetFloat32ColumnSpan(df, "LatencyMs")[1], 0.001f);
    }

    [Fact]
    public void GraphContextDecoupler_ExtractDocumentGraph_ExtractsHierarchyAndLinks()
    {
        string md = "# Overview\nSee [Security Rules](#security-rules) and [[AuditLog]].\n\n## Security Rules\nEnforce zero secrets.";
        var root = Glacier.DocTree.Parser.SpanMarkdownParser.Parse(md.AsSpan());
        var store = GraphContextDecoupler.ExtractDocumentGraph(root);

        // Root, Header1:Overview, Paragraph, Header2:Security Rules, Paragraph, AuditLog
        Assert.Equal(6, store.NodeCount);
        Assert.Equal(6, store.EdgeCount);
    }

    [Fact]
    public void GraphContextDecoupler_DuplicateHeaderTitles_DoNotCollideAndPreserveDistinctEdges()
    {
        string md = """
            # Service A
            ## Overview
            See [Service B Overview](#service-b/overview).

            # Service B
            ## Overview
            See [First Overview](#overview).
            """;

        var root = Glacier.DocTree.Parser.SpanMarkdownParser.Parse(md.AsSpan());
        var store = GraphContextDecoupler.ExtractDocumentGraph(root);

        // Nodes: Root(0), Service A(1), Overview A(2), Para A(3), Service B(4), Overview B(5), Para B(6) = 7 distinct nodes!
        Assert.Equal(7, store.NodeCount);
        // Edges: 6 CONTAINS + 2 LINKS_TO = 8 edges
        Assert.Equal(8, store.EdgeCount);

        string tempCsrPath = Path.Combine(Path.GetTempPath(), $"test_dup_headers_{Guid.NewGuid():N}.gcsr");
        try
        {
            GraphContextDecoupler.CompileToCsr(store, tempCsrPath);
            using var csr = GraphContextDecoupler.LoadCsrStore(tempCsrPath, useMemoryMapping: true);

            int rootId = csr.GetExternalToInternalId("Root");
            int svcAId = csr.GetExternalToInternalId("Header1:Service A#1");
            int ovAId = csr.GetExternalToInternalId("Header1:Service A#1/Header2:Overview#2");
            int paraAId = csr.GetExternalToInternalId("Header1:Service A#1/Header2:Overview#2/Paragraph#3");
            int svcBId = csr.GetExternalToInternalId("Header1:Service B#4");
            int ovBId = csr.GetExternalToInternalId("Header1:Service B#4/Header2:Overview#5");
            int paraBId = csr.GetExternalToInternalId("Header1:Service B#4/Header2:Overview#5/Paragraph#6");

            Assert.True(rootId > 0 && svcAId > 0 && ovAId > 0 && paraAId > 0 && svcBId > 0 && ovBId > 0 && paraBId > 0);
            Assert.NotEqual(ovAId, ovBId);

            // Service A contains Overview A (not Overview B); Service B contains Overview B
            Assert.True(GraphContextDecoupler.ContainsNeighbor(csr, svcAId, ovAId));
            Assert.False(GraphContextDecoupler.ContainsNeighbor(csr, svcAId, ovBId));
            Assert.True(GraphContextDecoupler.ContainsNeighbor(csr, svcBId, ovBId));

            // Path-qualified link in Para A points to Service B's Overview; bare #overview in Para B points to Service A's Overview
            Assert.True(GraphContextDecoupler.ContainsNeighbor(csr, paraAId, ovBId));
            Assert.True(GraphContextDecoupler.ContainsNeighbor(csr, paraBId, ovAId));
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
    public void GraphContextDecoupler_PathQualifiedWikiLinksAndAnchors_ResolveWithoutPhantomNodes()
    {
        string md = """
            # Service A
            ## Overview
            See [[Service B/Overview]] and [x](#Service B/Overview).

            # Service B
            ## Overview
            Target.
            """;

        var root = Glacier.DocTree.Parser.SpanMarkdownParser.Parse(md.AsSpan());
        var store = GraphContextDecoupler.ExtractDocumentGraph(root);

        // 7 real AST nodes only — zero phantom node for [[Service B/Overview]]!
        Assert.Equal(7, store.NodeCount);
        Assert.Equal(8, store.EdgeCount);

        string tempCsrPath = Path.Combine(Path.GetTempPath(), $"test_wikilink_path_{Guid.NewGuid():N}.gcsr");
        try
        {
            GraphContextDecoupler.CompileToCsr(store, tempCsrPath);
            using var csr = GraphContextDecoupler.LoadCsrStore(tempCsrPath, useMemoryMapping: true);

            int paraAId = csr.GetExternalToInternalId("Header1:Service A#1/Header2:Overview#2/Paragraph#3");
            int ovBId = csr.GetExternalToInternalId("Header1:Service B#4/Header2:Overview#5");

            Assert.True(paraAId > 0 && ovBId > 0);
            Assert.True(GraphContextDecoupler.ContainsNeighbor(csr, paraAId, ovBId));
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
    public void GraphContextDecoupler_DeepChain25000_IsLinearTimeAndLinearMemory()
    {
        const int depth = 25_000;
        var root = new Glacier.DocTree.Core.DocNode { Type = Glacier.DocTree.Core.NodeType.Root, Content = "Root" };
        var current = root;
        for (int i = 1; i <= depth; i++)
        {
            var child = new Glacier.DocTree.Core.DocNode
            {
                Type = Glacier.DocTree.Core.NodeType.Header2,
                Content = i % 2 == 0 ? "RepeatedTitle" : $"Section_{i}"
            };
            current.AddChild(child);
            current = child;
        }

        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var store = GraphContextDecoupler.ExtractDocumentGraph(root);
        sw.Stop();
        long allocAfter = GC.GetTotalAllocatedBytes(precise: true);

        Assert.Equal(depth + 1, store.NodeCount);
        Assert.Equal(depth, store.EdgeCount);
        Assert.True(sw.Elapsed.TotalSeconds < 2.0, $"Expected < 2.0s at depth={depth}, took {sw.Elapsed.TotalMilliseconds:F1} ms");
        long deltaBytes = allocAfter - allocBefore;
        Assert.True(deltaBytes < 100L * 1024 * 1024, $"Expected < 100 MB allocated, allocated {deltaBytes / (1024.0 * 1024.0):F2} MB");
    }

    [Fact]
    public void GraphContextDecoupler_DeepDuplicateHeaders_DoNotCollide()
    {
        var root = new Glacier.DocTree.Core.DocNode { Type = Glacier.DocTree.Core.NodeType.Root, Content = "Root" };

        Glacier.DocTree.Core.DocNode MakeBranch(string serviceName)
        {
            var top = new Glacier.DocTree.Core.DocNode { Type = Glacier.DocTree.Core.NodeType.Header1, Content = serviceName };
            var cur = top;
            for (int i = 0; i < 6; i++)
            {
                var mid = new Glacier.DocTree.Core.DocNode { Type = Glacier.DocTree.Core.NodeType.Header2, Content = "Layer" };
                cur.AddChild(mid);
                cur = mid;
            }
            cur.AddChild(new Glacier.DocTree.Core.DocNode { Type = Glacier.DocTree.Core.NodeType.Header3, Content = "Overview" });
            return top;
        }

        root.AddChild(MakeBranch("Service A"));
        root.AddChild(MakeBranch("Service B"));

        var store = GraphContextDecoupler.ExtractDocumentGraph(root);
        // 1 Root + 2 * (1 H1 + 6 H2 + 1 H3) = 17 distinct nodes
        Assert.Equal(17, store.NodeCount);
        Assert.Equal(16, store.EdgeCount);
    }
}
