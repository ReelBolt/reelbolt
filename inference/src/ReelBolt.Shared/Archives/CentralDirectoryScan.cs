using System.Buffers.Binary;

namespace ReelBolt.Shared.Archives;

/// <summary>One central-directory record, as far as the safe reader needs it to find and bound the data.</summary>
internal readonly record struct CentralRecord(
    ushort Flags,
    ushort Method,
    long CompressedSize,
    long UncompressedSize,
    long LocalHeaderOffset);

/// <summary>
/// A bounded, allocation-light pre-scan of a zip's central directory that runs BEFORE
/// <c>System.IO.Compression.ZipArchive</c> materialises it. It reads the end-of-central-directory record
/// (and the zip64 records), refuses an archive that claims more than the entry cap, and parses at most that
/// many 46-byte headers. It also gives the reader what the BCL hides: where each entry's data starts, so the
/// reader can inflate it itself and see how much output a deflate stream really produces.
/// </summary>
internal static class CentralDirectoryScan
{
    private const uint EocdSignature = 0x06054B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const uint Zip64EocdSignature = 0x06064B50;
    private const uint CentralSignature = 0x02014B50;
    private const int EocdMinLength = 22;
    private const int MaxCommentLength = 65535;

    public enum Outcome
    {
        Ok,
        Invalid,
        TooManyEntries,
    }

    public static Outcome Scan(Stream zip, long archiveLength, int maxEntries, out List<CentralRecord> records)
    {
        records = new List<CentralRecord>();
        try
        {
            return ScanCore(zip, archiveLength, maxEntries, records);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OverflowException or ArgumentException)
        {
            return Outcome.Invalid;
        }
    }

    private static Outcome ScanCore(Stream zip, long archiveLength, int maxEntries, List<CentralRecord> records)
    {
        if (archiveLength < EocdMinLength)
            return Outcome.Invalid;

        int tailLength = (int)Math.Min(archiveLength, EocdMinLength + MaxCommentLength);
        byte[] tail = ReadAt(zip, archiveLength - tailLength, tailLength);

        int eocd = -1;
        for (int i = tailLength - EocdMinLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EocdSignature)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
            return Outcome.Invalid;

        long eocdPosition = archiveLength - tailLength + eocd;
        ulong count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        ulong cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        ulong cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));

        if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
        {
            if (eocdPosition < 20 + 56)
                return Outcome.Invalid;

            byte[] locator = ReadAt(zip, eocdPosition - 20, 20);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
                return Outcome.Invalid;

            ulong zip64At = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (zip64At > (ulong)archiveLength || zip64At + 56 > (ulong)archiveLength)
                return Outcome.Invalid;

            byte[] zip64 = ReadAt(zip, (long)zip64At, 56);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64EocdSignature)
                return Outcome.Invalid;

            count = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
            cdSize = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
            cdOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
        }

        // The cap is applied to the claim before one header is parsed or one entry object exists.
        if (count > (ulong)maxEntries)
            return Outcome.TooManyEntries;

        if (cdOffset > (ulong)archiveLength || cdSize > (ulong)archiveLength - cdOffset)
            return Outcome.Invalid;

        // Headers are read one at a time from the stream: memory is bounded by the (capped) entry count,
        // never by what the directory claims to occupy.
        long position = (long)cdOffset;
        for (ulong i = 0; i < count; i++)
        {
            if (position + 46 > archiveLength)
                return Outcome.Invalid;

            byte[] h = ReadAt(zip, position, 46);
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != CentralSignature)
                return Outcome.Invalid;

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(8));
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(10));
            ulong compressed = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(20));
            ulong uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(24));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(28));
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(30));
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(32));
            ulong localOffset = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(42));

            long total = 46L + nameLength + extraLength + commentLength;
            if (position + total > archiveLength)
                return Outcome.Invalid;

            ReadOnlySpan<byte> extra = extraLength == 0
                ? ReadOnlySpan<byte>.Empty
                : ReadAt(zip, position + 46 + nameLength, extraLength);
            while (extra.Length >= 4)
            {
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(extra);
                int size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
                if (4 + size > extra.Length)
                    break;

                if (tag == 1)
                {
                    ReadOnlySpan<byte> field = extra.Slice(4, size);
                    int at = 0;
                    if (uncompressed == 0xFFFFFFFF && at + 8 <= field.Length)
                    {
                        uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(field[at..]);
                        at += 8;
                    }

                    if (compressed == 0xFFFFFFFF && at + 8 <= field.Length)
                    {
                        compressed = BinaryPrimitives.ReadUInt64LittleEndian(field[at..]);
                        at += 8;
                    }

                    if (localOffset == 0xFFFFFFFF && at + 8 <= field.Length)
                        localOffset = BinaryPrimitives.ReadUInt64LittleEndian(field[at..]);
                }

                extra = extra[(4 + size)..];
            }

            if (uncompressed > long.MaxValue || compressed > long.MaxValue || localOffset > long.MaxValue)
                return Outcome.Invalid;

            records.Add(new CentralRecord(flags, method, (long)compressed, (long)uncompressed, (long)localOffset));
            position += total;
        }

        // The BCL keeps reading headers for as long as they keep coming, whatever the EOCD says. If more
        // headers follow the claimed count, the claim was a lie and the archive is refused unmaterialised.
        if (position + 4 <= archiveLength
            && BinaryPrimitives.ReadUInt32LittleEndian(ReadAt(zip, position, 4)) == CentralSignature)
        {
            return Outcome.TooManyEntries;
        }

        return Outcome.Ok;
    }

    /// <summary>
    /// Returns the absolute offset where an entry's data starts, or -1 when the local header is not a
    /// well-formed local header inside the archive.
    /// </summary>
    public static long DataStart(Stream zip, long archiveLength, long localHeaderOffset)
    {
        if (localHeaderOffset < 0 || archiveLength < 30 || localHeaderOffset > archiveLength - 30)
            return -1;

        byte[] header = ReadAt(zip, localHeaderOffset, 30);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034B50)
            return -1;

        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        // Both operands are bounded by the check above (offset <= length - 30) and 2 * 65535, so this cannot wrap.
        long start = checked(localHeaderOffset + 30 + nameLength + extraLength);
        return start > archiveLength ? -1 : start;
    }

    private static byte[] ReadAt(Stream zip, long offset, int count)
    {
        byte[] buffer = new byte[count];
        zip.Seek(offset, SeekOrigin.Begin);
        zip.ReadExactly(buffer, 0, count);
        return buffer;
    }
}
