using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PerfettoMcp.Perfetto;

// Field numbers follow protos/perfetto/trace_processor/trace_processor.proto (RPC API v14).
internal static class TpMessages
{
    private const int CellNull = 1;
    private const int CellVarint = 2;
    private const int CellFloat64 = 3;
    private const int CellString = 4;
    private const int CellBlob = 5;

    public static byte[] EncodeQueryArgs(string sql)
    {
        using MemoryStream ms = new();
        ProtoWriter.WriteString(ms, 1, sql);
        return ms.ToArray();
    }

    public static string? DecodeAppendResultError(ReadOnlySpan<byte> data)
    {
        ProtoReader r = new(data);
        string? error = null;
        while (r.Next(out int field, out int wt))
        {
            if (field == 2 && wt == ProtoReader.LengthDelimited)
            {
                error = r.ReadString();
            }
            else
            {
                r.Skip(wt);
            }
        }
        return error;
    }

    public static TpStatus DecodeStatus(ReadOnlySpan<byte> data)
    {
        ProtoReader r = new(data);
        TpStatus s = new();
        while (r.Next(out int field, out int wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.LengthDelimited:
                    // Raw bytes: older shells echo argv in the ANSI codepage on Windows.
                    s.LoadedTraceName = Encoding.UTF8.GetString(r.ReadBytes());
                    break;
                case 2 when wt == ProtoReader.LengthDelimited:
                    s.Version = r.ReadString();
                    break;
                case 3 when wt == ProtoReader.Varint:
                    s.ApiVersion = (int)r.ReadVarint();
                    break;
                default:
                    r.Skip(wt);
                    break;
            }
        }
        return s;
    }

    // Chunked /query replies are concatenated QueryResult fragments; protobuf merging decodes them as one.
    public static QueryTable DecodeQueryResult(ReadOnlySpan<byte> data, int maxRows)
    {
        QueryTable table = new();
        ProtoReader r = new(data);
        List<object?> pendingRow = new();
        while (r.Next(out int field, out int wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.LengthDelimited:
                    table.Columns.Add(r.ReadString());
                    break;
                case 2 when wt == ProtoReader.LengthDelimited:
                    string err = r.ReadString();
                    if (err.Length > 0)
                    {
                        table.Error = err;
                    }
                    break;
                case 3 when wt == ProtoReader.LengthDelimited:
                    DecodeBatch(r.ReadBytes(), table, pendingRow, maxRows);
                    break;
                case 4 when wt == ProtoReader.Varint:
                    table.StatementCount = (int)r.ReadVarint();
                    break;
                case 7 when wt == ProtoReader.Fixed64:
                    table.ElapsedMs = r.ReadDouble();
                    break;
                default:
                    r.Skip(wt);
                    break;
            }
        }
        return table;
    }

    private static void DecodeBatch(ReadOnlySpan<byte> data, QueryTable table, List<object?> pendingRow, int maxRows)
    {
        List<int> cellTypes = new();
        List<long> varints = new();
        List<double> doubles = new();
        List<byte[]> blobs = new();
        string strings = string.Empty;

        ProtoReader r = new(data);
        while (r.Next(out int field, out int wt))
        {
            switch (field)
            {
                case 1:
                    ReadPackedVarints(ref r, wt, v => cellTypes.Add((int)v));
                    break;
                case 2:
                    ReadPackedVarints(ref r, wt, v => varints.Add((long)v));
                    break;
                case 3 when wt == ProtoReader.LengthDelimited:
                    ProtoReader packed = new(r.ReadBytes());
                    while (!packed.AtEnd)
                    {
                        doubles.Add(packed.ReadDouble());
                    }
                    break;
                case 3 when wt == ProtoReader.Fixed64:
                    doubles.Add(r.ReadDouble());
                    break;
                case 4 when wt == ProtoReader.LengthDelimited:
                    blobs.Add(r.ReadBytes().ToArray());
                    break;
                case 5 when wt == ProtoReader.LengthDelimited:
                    strings = r.ReadString();
                    break;
                default:
                    r.Skip(wt);
                    break;
            }
        }

        int numCols = Math.Max(1, table.Columns.Count);
        int vi = 0, di = 0, bi = 0, si = 0;
        foreach (int type in cellTypes)
        {
            object? value = type switch
            {
                CellNull => null,
                CellVarint => varints[vi++],
                CellFloat64 => doubles[di++],
                CellString => NextString(strings, ref si),
                CellBlob => BlobToString(blobs[bi++]),
                _ => throw new InvalidDataException($"invalid cell type {type}")
            };
            pendingRow.Add(value);
            if (pendingRow.Count == numCols)
            {
                table.TotalRows++;
                if (table.Rows.Count < maxRows)
                {
                    table.Rows.Add(pendingRow.ToArray());
                }
                pendingRow.Clear();
            }
        }
    }

    // string_cells is a single string with every cell NUL-terminated.
    private static string NextString(string pool, ref int index)
    {
        int end = pool.IndexOf('\0', index);
        if (end < 0)
        {
            end = pool.Length;
        }
        string s = pool[index..end];
        index = Math.Min(pool.Length, end + 1);
        return s;
    }

    private static string BlobToString(byte[] blob)
    {
        const int MaxHex = 64;
        string hex = Convert.ToHexString(blob, 0, Math.Min(blob.Length, MaxHex)).ToLowerInvariant();
        return blob.Length > MaxHex ? $"blob:hex:{hex}...({blob.Length} bytes)" : $"blob:hex:{hex}";
    }

    private static void ReadPackedVarints(ref ProtoReader r, int wt, Action<ulong> sink)
    {
        if (wt == ProtoReader.Varint)
        {
            sink(r.ReadVarint());
            return;
        }
        if (wt != ProtoReader.LengthDelimited)
        {
            r.Skip(wt);
            return;
        }
        ProtoReader inner = new(r.ReadBytes());
        while (!inner.AtEnd)
        {
            sink(inner.ReadVarint());
        }
    }
}

internal sealed class TpStatus
{
    public string? LoadedTraceName { get; set; }
    public string? Version { get; set; }
    public int ApiVersion { get; set; }
}

internal sealed class QueryTable
{
    public List<string> Columns { get; } = new();
    public List<object?[]> Rows { get; } = new();
    public long TotalRows { get; set; }
    public string? Error { get; set; }
    public int StatementCount { get; set; }
    public double? ElapsedMs { get; set; }

    public bool Truncated => TotalRows > Rows.Count;

    public object? Cell(int row, string column)
    {
        int idx = Columns.IndexOf(column);
        return idx < 0 ? null : Rows[row][idx];
    }
}
