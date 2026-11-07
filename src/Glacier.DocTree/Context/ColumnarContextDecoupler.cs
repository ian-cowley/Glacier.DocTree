using System;
using System.Collections.Generic;
using System.Globalization;
using Glacier.DocTree.Parser;
using Glacier.Polaris;
using Glacier.Polaris.Data;

namespace Glacier.DocTree.Context;

/// <summary>
/// Decouples invariant tabular contextual facts (Markdown tables, telemetry records, structured logs)
/// from the autoregressive LLM prompt stream into Apache Arrow-aligned
/// <see cref="DataFrame"/> structures resident in host memory.
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
    /// Parses a pipe-delimited Markdown table into a typed <see cref="DataFrame"/> (inferring Int32, Float32, or UTF-8 string columns).
    /// </summary>
    public static DataFrame DecoupleMarkdownTable(ReadOnlySpan<char> markdownTable)
    {
        string[]? headers = null;
        var rows = new List<string[]>();
        bool seenSeparator = false;

        var enumerator = new SpanLineEnumerator(markdownTable);
        while (enumerator.MoveNext())
        {
            var line = enumerator.Current.Trim();
            if (line.IsEmpty || line[0] != '|')
            {
                continue;
            }

            var cells = ParsePipeRow(line);
            if (cells.Length == 0)
            {
                continue;
            }

            if (headers == null)
            {
                headers = cells;
                continue;
            }

            if (!seenSeparator && IsSeparatorRow(cells))
            {
                seenSeparator = true;
                continue;
            }

            rows.Add(cells);
        }

        if (headers == null || headers.Length == 0)
        {
            throw new ArgumentException("Input does not contain a valid pipe-delimited Markdown table header.", nameof(markdownTable));
        }

        int colCount = headers.Length;
        int rowCount = rows.Count;
        var columns = new ISeries[colCount];

        for (int c = 0; c < colCount; c++)
        {
            string colName = string.IsNullOrWhiteSpace(headers[c]) ? $"Col_{c}" : headers[c];
            bool allInt = rowCount > 0;
            bool allFloat = rowCount > 0;

            for (int r = 0; r < rowCount; r++)
            {
                string cell = c < rows[r].Length ? rows[r][c] : string.Empty;
                if (allInt && !int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    allInt = false;
                }
                if (allFloat && !float.TryParse(cell, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
                {
                    allFloat = false;
                }
            }

            if (allInt)
            {
                var intSeries = new Int32Series(colName, rowCount);
                Span<int> span = intSeries.Memory.Span;
                for (int r = 0; r < rowCount; r++)
                {
                    span[r] = int.Parse(rows[r][c], NumberStyles.Integer, CultureInfo.InvariantCulture);
                }
                columns[c] = intSeries;
            }
            else if (allFloat)
            {
                var floatSeries = new Float32Series(colName, rowCount);
                Span<float> span = floatSeries.Memory.Span;
                for (int r = 0; r < rowCount; r++)
                {
                    span[r] = float.Parse(rows[r][c], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                }
                columns[c] = floatSeries;
            }
            else
            {
                var values = new string?[rowCount];
                for (int r = 0; r < rowCount; r++)
                {
                    values[r] = c < rows[r].Length ? rows[r][c] : string.Empty;
                }
                columns[c] = Utf8StringSeries.FromStrings(colName, values);
            }
        }

        return new DataFrame(columns);
    }

    private static string[] ParsePipeRow(ReadOnlySpan<char> line)
    {
        if (line.Length > 0 && line[0] == '|') line = line[1..];
        if (line.Length > 0 && line[^1] == '|') line = line[..^1];

        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i <= line.Length; i++)
        {
            if (i == line.Length || line[i] == '|')
            {
                parts.Add(line.Slice(start, i - start).Trim().ToString());
                start = i + 1;
            }
        }
        return parts.ToArray();
    }

    private static bool IsSeparatorRow(string[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = cells[i].AsSpan().Trim();
            if (cell.IsEmpty) return false;
            int dashCount = 0;
            for (int j = 0; j < cell.Length; j++)
            {
                char ch = cell[j];
                if (ch == '-') dashCount++;
                else if (ch != ':' && ch != ' ') return false;
            }
            if (dashCount < 2) return false;
        }
        return true;
    }

    /// <summary>
    /// Decouples structured telemetry records (ID, DeviceId, Metric, Status) into a columnar DataFrame.
    /// </summary>
    public static DataFrame DecoupleTelemetryRecords(IEnumerable<(int Id, int DeviceId, float Metric, int Status)> records)
    {
        ArgumentNullException.ThrowIfNull(records);
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
