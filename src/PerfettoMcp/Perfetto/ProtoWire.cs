using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace PerfettoMcp.Perfetto;

// Minimal protobuf wire format for the few trace_processor RPC messages (no codegen).
internal ref struct ProtoReader
{
    public const int Varint = 0;
    public const int Fixed64 = 1;
    public const int LengthDelimited = 2;
    public const int Fixed32 = 5;

    private readonly ReadOnlySpan<byte> buf;
    private int pos;

    public ProtoReader(ReadOnlySpan<byte> buf)
    {
        this.buf = buf;
        pos = 0;
    }

    public readonly bool AtEnd => pos >= buf.Length;

    public bool Next(out int field, out int wireType)
    {
        if (pos >= buf.Length)
        {
            field = 0;
            wireType = 0;
            return false;
        }
        ulong tag = ReadVarint();
        field = (int)(tag >> 3);
        wireType = (int)(tag & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (pos >= buf.Length)
            {
                throw new InvalidDataException("truncated varint");
            }
            byte b = buf[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }
        throw new InvalidDataException("varint too long");
    }

    public double ReadDouble()
    {
        EnsureAvailable(8);
        double v = BinaryPrimitives.ReadDoubleLittleEndian(buf.Slice(pos, 8));
        pos += 8;
        return v;
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        int len = checked((int)ReadVarint());
        EnsureAvailable(len);
        ReadOnlySpan<byte> s = buf.Slice(pos, len);
        pos += len;
        return s;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case Varint:
                ReadVarint();
                break;
            case Fixed64:
                EnsureAvailable(8);
                pos += 8;
                break;
            case LengthDelimited:
                ReadBytes();
                break;
            case Fixed32:
                EnsureAvailable(4);
                pos += 4;
                break;
            default:
                throw new InvalidDataException($"unsupported wire type {wireType}");
        }
    }

    private void EnsureAvailable(int n)
    {
        if (n < 0 || pos + n > buf.Length)
        {
            throw new InvalidDataException("truncated message");
        }
    }
}

internal static class ProtoWriter
{
    public static void WriteString(MemoryStream ms, int field, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(ms, (ulong)((field << 3) | ProtoReader.LengthDelimited));
        WriteVarint(ms, (ulong)bytes.Length);
        ms.Write(bytes);
    }

    public static void WriteVarint(MemoryStream ms, ulong v)
    {
        while (v >= 0x80)
        {
            ms.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        ms.WriteByte((byte)v);
    }
}
