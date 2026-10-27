using System;
using System.IO;
using System.Text;
using PerfettoMcp.Perfetto;
using Xunit;

namespace PerfettoMcp.Tests;

public class ProtoTests
{
    [Fact]
    public void EncodesQueryArgs()
    {
        byte[] bytes = TpMessages.EncodeQueryArgs("SELECT COUNT(*) FROM slice");
        Assert.Equal(0x0a, bytes[0]);
        Assert.Equal(26, bytes[1]);
        Assert.Equal("SELECT COUNT(*) FROM slice", Encoding.UTF8.GetString(bytes, 2, 26));
    }

    [Fact]
    public void DecodesMixedCellsAcrossBatchesInColumnOrder()
    {
        // Two columns (zeta, alpha) — order must be preserved, not sorted.
        MemoryStream ms = new();
        ProtoWriter.WriteString(ms, 1, "zeta");
        ProtoWriter.WriteString(ms, 1, "alpha");
        WriteBatch(ms, cells: [2, 4, 1, 3], varints: [-5], doubles: [1.5], strings: "héllo\0");
        WriteBatch(ms, cells: [4, 2], varints: [long.MaxValue], doubles: [], strings: "中文\0");

        QueryTable t = TpMessages.DecodeQueryResult(ms.ToArray(), maxRows: 10);

        Assert.Equal(["zeta", "alpha"], t.Columns);
        Assert.Equal(3, t.TotalRows);
        Assert.Equal(-5L, t.Rows[0][0]);
        Assert.Equal("héllo", t.Rows[0][1]);
        Assert.Null(t.Rows[1][0]);
        Assert.Equal(1.5, t.Rows[1][1]);
        Assert.Equal("中文", t.Rows[2][0]);
        Assert.Equal(long.MaxValue, t.Rows[2][1]);
    }

    [Fact]
    public void KeepsCountingBeyondMaxRows()
    {
        MemoryStream ms = new();
        ProtoWriter.WriteString(ms, 1, "n");
        WriteBatch(ms, cells: [2, 2, 2], varints: [1, 2, 3], doubles: [], strings: "");
        QueryTable t = TpMessages.DecodeQueryResult(ms.ToArray(), maxRows: 2);
        Assert.Equal(3, t.TotalRows);
        Assert.Equal(2, t.Rows.Count);
        Assert.True(t.Truncated);
    }

    [Fact]
    public void DecodesError()
    {
        MemoryStream ms = new();
        ProtoWriter.WriteString(ms, 2, "no such table: foo");
        QueryTable t = TpMessages.DecodeQueryResult(ms.ToArray(), 10);
        Assert.Equal("no such table: foo", t.Error);
    }

    [Fact]
    public void RejectsTruncatedInput()
    {
        byte[] bad = [0x0a, 0x10, 0x41];
        Assert.Throws<InvalidDataException>(() => TpMessages.DecodeQueryResult(bad, 10));
    }

    private static void WriteBatch(MemoryStream outer, int[] cells, long[] varints, double[] doubles, string strings)
    {
        MemoryStream b = new();
        MemoryStream packed = new();
        foreach (int c in cells)
        {
            ProtoWriter.WriteVarint(packed, (ulong)c);
        }
        WriteLen(b, 1, packed.ToArray());
        if (varints.Length > 0)
        {
            packed = new MemoryStream();
            foreach (long v in varints)
            {
                ProtoWriter.WriteVarint(packed, unchecked((ulong)v));
            }
            WriteLen(b, 2, packed.ToArray());
        }
        if (doubles.Length > 0)
        {
            packed = new MemoryStream();
            foreach (double d in doubles)
            {
                packed.Write(BitConverter.GetBytes(d));
            }
            WriteLen(b, 3, packed.ToArray());
        }
        if (strings.Length > 0)
        {
            ProtoWriter.WriteString(b, 5, strings);
        }
        WriteLen(outer, 3, b.ToArray());
    }

    private static void WriteLen(MemoryStream ms, int field, byte[] payload)
    {
        ProtoWriter.WriteVarint(ms, (ulong)((field << 3) | 2));
        ProtoWriter.WriteVarint(ms, (ulong)payload.Length);
        ms.Write(payload);
    }
}
