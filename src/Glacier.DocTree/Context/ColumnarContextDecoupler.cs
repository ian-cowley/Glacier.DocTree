using System;
using System.Collections.Generic;
using System.Text;
using Glacier.Polaris;
using Glacier.Polaris.Data;

namespace Glacier.DocTree.Context;

/// <summary>
/// Decouples invariant tabular contextual facts (telemetry records, database tables, CSV/JSON logs)
/// from the autoregressive LLM prompt stream into high-performance Apache Arrow-aligned
/// Glacier.Polaris.DataFrame structures resident in host memory.
/// </summary>
public static class ColumnarContextDecoupler
{
    public const int Qwen25_7b_BytesPerToken = 57344;   // 28 layers * 4 KV heads * 128 dim * 2 bytes (FP16)
    public const int Llama3_8b_BytesPerToken = 131072;  // 32 layers * 8 KV heads * 128 dim * 2 bytes (FP16)

    /// <summary>
    /// Creates a symbolic prompt reference tag representing the decoupled tabular context,
    /// replacing thousands of raw text tokens with a single concise pointer.
    /// </summary>
    public static string CreateContextReference(string tableName, int rowCount, int columnCount) =>
        $"[POLARIS_REF: Table={tableName}, Rows={rowCount}, Cols={columnCount}]";

    /// <summary>
    /// Parses and decouples simple structured telemetry records (ID, DeviceId, Metric, Status) into a columnar DataFrame.
    /// </summary>
    public static DataFrame DecoupleTelemetryRecords(IEnumerable<(int Id, int DeviceId, float Metric, int Status)> records)
    {
        var list = new List<(int Id, int DeviceId, float Metric, int Status)>(records);
        int count = list.Count;

        var idSeries = new Int32Series("Id", count);
        var devSeries = new Int32Series("DeviceId", count);
        var metricSeries = new Float32Series("Metric", count);
        var statusSeries = new Int32Series("Status", count);

        Span<int> idSpan = idSeries.Memory.Span;
        Span<int> devSpan = devSeries.Memory.Span;
        Span<float> metricSpan = metricSeries.Memory.Span;
        Span<int> statusSpan = statusSeries.Memory.Span;

        for (int i = 0; i < count; i++)
        {
            var r = list[i];
            idSpan[i] = r.Id;
            devSpan[i] = r.DeviceId;
            metricSpan[i] = r.Metric;
            statusSpan[i] = r.Status;
        }

        return new DataFrame(new ISeries[] { idSeries, devSeries, metricSeries, statusSeries });
    }

    /// <summary>
    /// Directly accesses unmanaged scalar memory span of a numeric column from the DataFrame
    /// for sub-microsecond query execution without populating the neural KV cache.
    /// </summary>
    public static ReadOnlySpan<int> GetInt32ColumnSpan(DataFrame df, string columnName)
    {
        foreach (var col in df.Columns)
        {
            if (string.Equals(col.Name, columnName, StringComparison.OrdinalIgnoreCase) && col is Int32Series s)
            {
                return s.Memory.Span;
            }
        }
        throw new ArgumentException($"Column '{columnName}' not found or is not Int32Series.");
    }

    /// <summary>
    /// Directly accesses unmanaged float scalar memory span of a numeric column from the DataFrame.
    /// </summary>
    public static ReadOnlySpan<float> GetFloat32ColumnSpan(DataFrame df, string columnName)
    {
        foreach (var col in df.Columns)
        {
            if (string.Equals(col.Name, columnName, StringComparison.OrdinalIgnoreCase) && col is Float32Series s)
            {
                return s.Memory.Span;
            }
        }
        throw new ArgumentException($"Column '{columnName}' not found or is not Float32Series.");
    }

    /// <summary>
    /// Calculates the sequence length reduction ratio when tabular text is decoupled into Polaris.
    /// </summary>
    public static double CalculateSequenceLengthReduction(int textBpeTokenCount, int referenceTokenCount = 12)
    {
        if (referenceTokenCount <= 0) referenceTokenCount = 1;
        return (double)textBpeTokenCount / referenceTokenCount;
    }

    /// <summary>
    /// Computes the absolute and percentage KV cache memory savings achieved by keeping the table in Polaris.
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
