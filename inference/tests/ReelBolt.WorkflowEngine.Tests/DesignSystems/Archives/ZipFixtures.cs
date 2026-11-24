#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;

/// <summary>
/// Builds the archives the safe reader must survive. Two entry points are deliberate:
/// <see cref="Build"/> (deflate, for ordinary fixtures) and <see cref="BuildStored"/> (no
/// compression, so the local header's declared size can be patched without a compressor in the way).
/// <see cref="PatchName"/> is the only way to reach names the BCL refuses to create — the local
/// header and the central directory both carry the name, so replacing every ASCII occurrence fixes
/// both copies while leaving the length fields untouched.
/// </summary>
public static class ZipFixtures
{
    public static byte[] Build(params (string Name, byte[] Data)[] files)
        => BuildWith(CompressionLevel.Optimal, files);

    public static byte[] BuildStored(params (string Name, byte[] Data)[] files)
        => BuildWith(CompressionLevel.NoCompression, files);

    /// <summary>Replaces every ASCII occurrence of <paramref name="from"/> with <paramref name="to"/>.</summary>
    public static byte[] PatchName(byte[] zip, string from, string to)
    {
        byte[] fromBytes = Encoding.ASCII.GetBytes(from);
        byte[] toBytes = Encoding.ASCII.GetBytes(to);
        if (fromBytes.Length != toBytes.Length)
            throw new ArgumentException("from and to must have the same ASCII length", nameof(to));

        byte[] result = (byte[])zip.Clone();
        for (int i = 0; i + fromBytes.Length <= result.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < fromBytes.Length; j++)
            {
                if (result[i + j] != fromBytes[j])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
                continue;

            Array.Copy(toBytes, 0, result, i, toBytes.Length);
            i += fromBytes.Length - 1;
        }

        return result;
    }

    /// <summary>
    /// Overwrites the declared uncompressed size in every central header (+24) and local header (+22).
    /// Little-endian uint32, matching what a zip writer emits on little-endian hosts.
    /// </summary>
    public static byte[] PatchUncompressedSize(byte[] zip, uint size)
    {
        byte[] result = (byte[])zip.Clone();
        byte[] sizeBytes = BitConverter.GetBytes(size);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(sizeBytes);

        for (int i = 0; i + 4 <= result.Length; i++)
        {
            if (result[i] == 0x50 && result[i + 1] == 0x4B && result[i + 2] == 0x01 && result[i + 3] == 0x02)
                Array.Copy(sizeBytes, 0, result, i + 24, 4);
            else if (result[i] == 0x50 && result[i + 1] == 0x4B && result[i + 2] == 0x03 && result[i + 3] == 0x04)
                Array.Copy(sizeBytes, 0, result, i + 22, 4);
        }

        return result;
    }

    private static byte[] BuildWith(CompressionLevel level, (string Name, byte[] Data)[] files)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, level);
                using Stream entryStream = entry.Open();
                entryStream.Write(data, 0, data.Length);
            }
        }

        return stream.ToArray();
    }
}
