using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ReelBolt.Shared.Archives;

/// <summary>
/// The only zip reader the product uses. Everything happens in memory, nothing is ever written to disk,
/// and every hostile shape a customer zip can take is either refused per entry (<see cref="SafeZipRejected"/>)
/// or refuses the whole archive (<see cref="SafeZipResult.Aborted"/>). Pure and deterministic.
///
/// Refusals are reported with the exact reason codes below; an abort always returns an empty
/// <see cref="SafeZipResult.Entries"/> so a caller can never consume partial content.
/// </summary>
public static class SafeZipReader
{
    // Abort reasons (whole archive).
    private const string ArchiveTooLarge = "ARCHIVE_TOO_LARGE";
    private const string ArchiveInvalid = "ARCHIVE_INVALID";
    private const string TooManyEntries = "TOO_MANY_ENTRIES";
    private const string TotalTooLarge = "TOTAL_TOO_LARGE";
    private const string BombSuspected = "BOMB_SUSPECTED";
    private const string TooManyFiles = "TOO_MANY_FILES";

    // Reject reasons (one entry).

    private const string DuplicatePath = "DUPLICATE_PATH";
    private const string SpecialFile = "SPECIAL_FILE";
    private const string EntryTooLarge = "ENTRY_TOO_LARGE";
    private const string RatioExceeded = "RATIO_EXCEEDED";
    private const string EntryUnreadable = "ENTRY_UNREADABLE";
    private const string NestedArchive = "NESTED_ARCHIVE";

    private static readonly byte[][] NestedArchiveSignatures =
    {
        new byte[] { 0x50, 0x4B, 0x03, 0x04 },
        new byte[] { 0x50, 0x4B, 0x05, 0x06 },
        new byte[] { 0x1F, 0x8B },
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },
        new byte[] { 0x52, 0x61, 0x72, 0x21 },
        new byte[] { 0x42, 0x5A, 0x68 },
        new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 },
    };

    /// <summary>
    /// Reads <paramref name="zip"/> fully in memory under <paramref name="limits"/>. Never seeks a
    /// caller's stream, never writes, never spawns a process. <paramref name="includePath"/> is asked
    /// about each valid, non-special, non-duplicate entry's normalized path; returning false skips it
    /// silently (it was still counted against <see cref="SafeZipLimits.MaxEntries"/>).
    /// </summary>
    public static SafeZipResult Read(
        Stream zip,
        SafeZipLimits limits,
        Func<string, bool>? includePath = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(limits);

        List<SafeZipRejected> rejected = new();

        if (!zip.CanSeek)
            return Abort(ArchiveInvalid, rejected);

        long archiveLength;
        try
        {
            archiveLength = zip.Length;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Abort(ArchiveInvalid, rejected);
        }

        if (archiveLength > limits.MaxArchiveBytes)
            return Abort(ArchiveTooLarge, rejected);

        // The entry cap is applied to the central directory's own claim, and at most that many headers are
        // parsed, BEFORE the BCL builds one ZipArchiveEntry for a hostile archive.
        CentralDirectoryScan.Outcome scan = CentralDirectoryScan.Scan(zip, archiveLength, limits.MaxEntries, out List<CentralRecord> records);
        if (scan == CentralDirectoryScan.Outcome.TooManyEntries)
            return Abort(TooManyEntries, rejected);
        if (scan != CentralDirectoryScan.Outcome.Ok)
            return Abort(ArchiveInvalid, rejected);

        ZipArchive archive;
        try
        {
            zip.Seek(0, SeekOrigin.Begin);
            archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Abort(ArchiveInvalid, rejected);
        }

        try
        {
            IReadOnlyList<ZipArchiveEntry> archiveEntries;
            try
            {
                archiveEntries = archive.Entries;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Abort(ArchiveInvalid, rejected);
            }

            // Two independent parses of the same directory must agree; a disagreement is a crafted archive.
            if (archiveEntries.Count > limits.MaxEntries)
                return Abort(TooManyEntries, rejected);
            if (archiveEntries.Count != records.Count)
                return Abort(ArchiveInvalid, rejected);

            List<SafeZipEntry> entries = new();
            HashSet<string> seenPaths = new(StringComparer.Ordinal); // keys from ArchivePathRules.TryGetDedupeKey

            // Every byte actually inflated or copied, accepted or not, so rejected entries are not free.
            long inflated = 0;
            long normalizedChars = 0;

            for (int index = 0; index < archiveEntries.Count; index++)
            {
                ct.ThrowIfCancellationRequested();

                ZipArchiveEntry entry = archiveEntries[index];
                CentralRecord record = records[index];
                if (entry.Length != record.UncompressedSize || entry.CompressedLength != record.CompressedSize)
                    return Abort(ArchiveInvalid, rejected, inflated);

                string rawName = entry.FullName;

                // Directories carry no content and are not part of the result.
                if (rawName.EndsWith('/') && entry.Length == 0)
                    continue;

                string? pathReason = ArchivePathRules.Validate(rawName, limits.MaxDepth);
                if (pathReason is not null)
                {
                    rejected.Add(new SafeZipRejected(rawName, pathReason));
                    continue;
                }

                // Names are NFC-normalised and compared ignoring case (see ArchivePathRules). Without
                // normalisation data a non-ASCII name cannot be de-duplicated safely, so it is refused.
                // Normalisation cost is bounded per archive as well as per name (marks are capped per segment).
                if (!ArchivePathRules.IsAscii(rawName))
                {
                    normalizedChars += rawName.Length;
                    if (normalizedChars > ArchivePathRules.MaxNormalizedCharactersPerArchive)
                    {
                        rejected.Add(new SafeZipRejected(rawName, ArchivePathRules.PathInvalid));
                        continue;
                    }
                }

                if (!ArchivePathRules.TryNormalize(rawName, out string normalizedPath))
                {
                    rejected.Add(new SafeZipRejected(
                        rawName,
                        ArchivePathRules.NormalizationAvailable ? EntryUnreadable : ArchivePathRules.NormalizationUnavailable));
                    continue;
                }

                // Composition can in principle produce characters the raw check did not see; judge the final name.
                string? finalReason = ArchivePathRules.Validate(normalizedPath, limits.MaxDepth);
                if (finalReason is not null)
                {
                    rejected.Add(new SafeZipRejected(rawName, finalReason));
                    continue;
                }

                if (!ArchivePathRules.TryGetDedupeKey(normalizedPath, out string dedupeKey))
                {
                    rejected.Add(new SafeZipRejected(rawName, EntryUnreadable));
                    continue;
                }

                if (!seenPaths.Add(dedupeKey))
                {
                    rejected.Add(new SafeZipRejected(normalizedPath, DuplicatePath));
                    continue;
                }

                uint fileType = ((uint)entry.ExternalAttributes >> 16) & 0xF000;
                if (fileType is 0xA000 or 0x1000 or 0x2000 or 0x6000 or 0xC000)
                {
                    rejected.Add(new SafeZipRejected(normalizedPath, SpecialFile));
                    continue;
                }

                if (includePath is not null && !includePath(normalizedPath))
                    continue;

                long declaredLength = entry.Length;
                if (declaredLength > limits.MaxEntryBytes)
                {
                    rejected.Add(new SafeZipRejected(normalizedPath, EntryTooLarge));
                    continue;
                }

                // Cheap early refusal on the declared numbers. The central directory is attacker-controlled, so
                // this is NOT the defence: the real ratio is enforced while inflating (see ReadEntry).
                long compressedLength = entry.CompressedLength;
                if ((compressedLength == 0 && declaredLength > 0)
                    || declaredLength / Math.Max(1, compressedLength) > limits.MaxRatio)
                {
                    rejected.Add(new SafeZipRejected(normalizedPath, RatioExceeded));
                    continue;
                }

                if (inflated + declaredLength > limits.MaxTotalBytes)
                    return Abort(TotalTooLarge, rejected, inflated);

                if (entry.IsEncrypted)
                {
                    rejected.Add(new SafeZipRejected(normalizedPath, EntryUnreadable, Encrypted: true));
                    continue;
                }

                EntryRead read = ReadEntry(zip, archiveLength, record, declaredLength, limits, ct);
                inflated += read.Produced;

                // Declared sizes lie; the bytes we actually read are the truth.
                if (read.Status == ReadStatus.Bomb)
                    return Abort(BombSuspected, rejected, inflated);

                if (inflated > limits.MaxTotalBytes)
                    return Abort(TotalTooLarge, rejected, inflated);

                if (read.Status != ReadStatus.Ok)
                {
                    rejected.Add(new SafeZipRejected(normalizedPath, read.Status switch
                    {
                        ReadStatus.TooLarge => EntryTooLarge,
                        ReadStatus.Ratio => RatioExceeded,
                        _ => EntryUnreadable,
                    }));
                    continue;
                }

                byte[] content = read.Content!;
                if (HasNestedArchiveMagic(content))
                {
                    rejected.Add(new SafeZipRejected(normalizedPath, NestedArchive));
                    continue;
                }

                if (limits.MaxIncludedEntries is { } maxIncluded && entries.Count >= maxIncluded)
                    return Abort(TooManyFiles, rejected, inflated);

                entries.Add(new SafeZipEntry(normalizedPath, content));
            }

            return new SafeZipResult(entries, rejected, false, null) { InflatedBytes = inflated };
        }
        finally
        {
            archive.Dispose();
        }
    }

    private static SafeZipResult Abort(string reason, List<SafeZipRejected> rejected, long inflated = 0)
        => new(Array.Empty<SafeZipEntry>(), rejected, true, reason) { InflatedBytes = inflated };

    private enum ReadStatus
    {
        Ok,
        Unreadable,
        TooLarge,
        Ratio,
        Bomb,
    }

    private readonly record struct EntryRead(ReadStatus Status, byte[]? Content, long Produced);

    /// <summary>
    /// Inflates (or copies) one entry itself instead of trusting <c>ZipArchiveEntry.Open</c>, because the BCL
    /// silently truncates a deflate stream at the declared length: an entry that lies about its size would
    /// land short instead of being refused. Output is read up to the declared length, then probed for
    /// surplus up to <see cref="SafeZipLimits.MaxEntryBytes"/>, and the real compression ratio is enforced on
    /// the bytes the inflater actually consumed, never on the forgeable central-directory CompressedLength.
    /// </summary>
    private static EntryRead ReadEntry(
        Stream zip,
        long archiveLength,
        CentralRecord record,
        long declared,
        SafeZipLimits limits,
        CancellationToken ct)
    {
        if (record.Method is not (0 or 8))
            return new EntryRead(ReadStatus.Unreadable, null, 0);

        long dataStart;
        try
        {
            dataStart = CentralDirectoryScan.DataStart(zip, archiveLength, record.LocalHeaderOffset);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or OverflowException)
        {
            dataStart = -1;
        }

        if (dataStart < 0)
            return new EntryRead(ReadStatus.Unreadable, null, 0);

        long produced = 0;
        try
        {
            using RawSlice raw = new(zip, dataStart, Math.Min(record.CompressedSize, archiveLength - dataStart));
            using Stream source = record.Method == 8
                ? new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true)
                : raw;

            bool RatioBroken() => record.Method == 8 && produced > (long)limits.MaxRatio * (raw.Consumed + 1);

            // Grow with what is really produced (never allocate the declared size up front): an entry that
            // claims 8 MiB and delivers 10 bytes must not cost 8 MiB.
            byte[] content = new byte[Math.Min(declared, ChunkSize)];
            while (produced < declared)
            {
                ct.ThrowIfCancellationRequested();
                if (produced == content.Length)
                    Array.Resize(ref content, (int)Math.Min(declared, Math.Max(1L, content.Length) * 2));

                int read = source.Read(content, (int)produced, (int)Math.Min(content.Length - produced, ChunkSize));
                if (read <= 0)
                    return new EntryRead(ReadStatus.Unreadable, null, produced);

                produced += read;
                if (RatioBroken())
                    return new EntryRead(ReadStatus.Ratio, null, produced);
            }

            // Surplus probe: anything past the declared length is a lie, up to the per-entry cap.
            long surplus = 0;
            long surplusCap = limits.MaxEntryBytes + 1 - declared;
            byte[] scratch = new byte[ChunkSize];
            while (surplus < surplusCap)
            {
                ct.ThrowIfCancellationRequested();
                int read = source.Read(scratch, 0, (int)Math.Min(ChunkSize, surplusCap - surplus));
                if (read <= 0)
                    break;

                surplus += read;
                produced += read;

                // Past the declared length the entry is already lying; a fast-expanding lie is judged as a lie
                // (below), not as a bare ratio breach.
                if (RatioBroken())
                    break;
            }

            if (surplus == 0)
                return new EntryRead(ReadStatus.Ok, content, produced);

            long actual = declared + surplus;
            if (actual > limits.MaxEntryBytes)
                return new EntryRead(ReadStatus.TooLarge, null, produced);

            return new EntryRead(actual > 2 * declared ? ReadStatus.Bomb : ReadStatus.Unreadable, null, produced);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return new EntryRead(ReadStatus.Unreadable, null, produced);
        }
    }

    private const int ChunkSize = 81920;

    /// <summary>A read-only window over the archive that counts how many bytes the inflater pulled from it.</summary>
    private sealed class RawSlice : Stream
    {
        private readonly Stream _zip;
        private readonly long _end;
        private long _position;

        public RawSlice(Stream zip, long start, long length)
        {
            _zip = zip;
            _position = start;
            _end = start + Math.Max(0, length);
        }

        public long Consumed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            long remaining = _end - _position;
            if (remaining <= 0 || buffer.IsEmpty)
                return 0;

            _zip.Seek(_position, SeekOrigin.Begin);
            int read = _zip.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            _position += read;
            Consumed += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static bool HasNestedArchiveMagic(byte[] content)
    {
        foreach (byte[] signature in NestedArchiveSignatures)
        {
            if (content.Length < signature.Length)
                continue;

            bool match = true;
            for (int i = 0; i < signature.Length; i++)
            {
                if (content[i] != signature[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }
}
