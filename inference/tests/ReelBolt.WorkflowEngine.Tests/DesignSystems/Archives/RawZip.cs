#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;

/// <summary>
/// A byte-level zip writer for hostile shapes the BCL writer refuses to produce: forged central-directory
/// sizes, deflate data whose real output differs from the declared size, a shared compressed blob across
/// thousands of entries, zip64 records and an EOCD whose entry count lies. Everything is little-endian.
/// </summary>
public static class RawZip
{
    public sealed record Entry(
        string Name,
        byte[] Compressed,
        long DeclaredSize,
        ushort Method = 8,
        long? ClaimedCompressedSize = null,
        ushort Flags = 0,
        bool Zip64 = false);

    /// <summary>Deflate-compresses <paramref name="data"/> into a raw (headerless) deflate stream.</summary>
    public static byte[] Deflate(byte[] data)
    {
        using MemoryStream output = new();
        using (DeflateStream deflate = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        return output.ToArray();
    }

    /// <summary><paramref name="length"/> bytes of zeros, optionally starting with a prefix (e.g. zip magic).</summary>
    public static byte[] ZerosWithPrefix(int length, params byte[] prefix)
    {
        byte[] data = new byte[length];
        Array.Copy(prefix, data, prefix.Length);
        return data;
    }

    public static byte[] Build(
        IReadOnlyList<Entry> entries,
        bool zip64Eocd = false,
        int? forgedEocdCount = null)
    {
        using MemoryStream stream = new();
        List<long> offsets = new(entries.Count);

        foreach (Entry e in entries)
        {
            offsets.Add(stream.Position);
            byte[] name = Encoding.UTF8.GetBytes(e.Name);
            Write32(stream, 0x04034B50);
            Write16(stream, 20);
            Write16(stream, e.Flags);
            Write16(stream, e.Method);
            Write16(stream, 0);
            Write16(stream, 0x21);
            Write32(stream, 0);
            Write32(stream, e.Zip64 ? 0xFFFFFFFF : (uint)e.Compressed.Length);
            Write32(stream, e.Zip64 ? 0xFFFFFFFF : (uint)e.DeclaredSize);
            Write16(stream, (ushort)name.Length);
            Write16(stream, (ushort)(e.Zip64 ? 20 : 0));
            stream.Write(name);
            if (e.Zip64)
            {
                Write16(stream, 1);
                Write16(stream, 16);
                Write64(stream, (ulong)e.DeclaredSize);
                Write64(stream, (ulong)e.Compressed.Length);
            }

            stream.Write(e.Compressed);
        }

        long cdStart = stream.Position;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            byte[] name = Encoding.UTF8.GetBytes(e.Name);
            long claimed = e.ClaimedCompressedSize ?? e.Compressed.Length;
            Write32(stream, 0x02014B50);
            Write16(stream, 20);
            Write16(stream, 20);
            Write16(stream, e.Flags);
            Write16(stream, e.Method);
            Write16(stream, 0);
            Write16(stream, 0x21);
            Write32(stream, 0);
            Write32(stream, e.Zip64 ? 0xFFFFFFFF : (uint)claimed);
            Write32(stream, e.Zip64 ? 0xFFFFFFFF : (uint)e.DeclaredSize);
            Write16(stream, (ushort)name.Length);
            Write16(stream, (ushort)(e.Zip64 ? 28 : 0));
            Write16(stream, 0);
            Write16(stream, 0);
            Write16(stream, 0);
            Write32(stream, 0);
            Write32(stream, (uint)offsets[i]);
            stream.Write(name);
            if (e.Zip64)
            {
                Write16(stream, 1);
                Write16(stream, 24);
                Write64(stream, (ulong)e.DeclaredSize);
                Write64(stream, (ulong)claimed);
                Write64(stream, (ulong)offsets[i]);
            }
        }

        long cdEnd = stream.Position;
        long cdSize = cdEnd - cdStart;

        if (zip64Eocd)
        {
            long zip64EocdAt = stream.Position;
            Write32(stream, 0x06064B50);
            Write64(stream, 44);
            Write16(stream, 45);
            Write16(stream, 45);
            Write32(stream, 0);
            Write32(stream, 0);
            Write64(stream, (ulong)entries.Count);
            Write64(stream, (ulong)entries.Count);
            Write64(stream, (ulong)cdSize);
            Write64(stream, (ulong)cdStart);

            Write32(stream, 0x07064B50);
            Write32(stream, 0);
            Write64(stream, (ulong)zip64EocdAt);
            Write32(stream, 1);
        }

        Write32(stream, 0x06054B50);
        Write16(stream, 0);
        Write16(stream, 0);
        ushort count = zip64Eocd ? (ushort)0xFFFF : (ushort)(forgedEocdCount ?? entries.Count);
        Write16(stream, count);
        Write16(stream, count);
        Write32(stream, zip64Eocd ? 0xFFFFFFFF : (uint)cdSize);
        Write32(stream, zip64Eocd ? 0xFFFFFFFF : (uint)cdStart);
        Write16(stream, 0);

        return stream.ToArray();
    }

    private static void Write16(Stream s, ushort v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
    }

    private static void Write32(Stream s, uint v)
    {
        Write16(s, (ushort)v);
        Write16(s, (ushort)(v >> 16));
    }

    private static void Write64(Stream s, ulong v)
    {
        Write32(s, (uint)v);
        Write32(s, (uint)(v >> 32));
    }
}
