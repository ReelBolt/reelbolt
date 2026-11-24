using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ReelBolt.Shared.Archives;

namespace ReelBolt.ImportWorker;

/// <summary>What <see cref="UnpackZipHandler"/> decided: a status, and the binary answer when it is 200.</summary>
public sealed record UnpackOutcome(int Status, UnpackResponse? Response);

/// <summary>
/// The framed answer: <c>RBU1</c>, u32 big-endian manifest length, UTF-8 JSON manifest, then every file's raw
/// bytes in manifest order. The bytes are streamed straight from the in-memory entries (no copy, no base64).
/// </summary>
public sealed class UnpackResponse
{
    private readonly byte[] _head;
    private readonly IReadOnlyList<SafeZipEntry> _files;

    internal UnpackResponse(byte[] head, IReadOnlyList<SafeZipEntry> files)
    {
        _head = head;
        _files = files;
        long length = head.Length;
        foreach (SafeZipEntry file in files)
            length += file.Content.Length;
        ContentLength = length;
    }

    public long ContentLength { get; }

    public async Task WriteToAsync(Stream output, CancellationToken ct)
    {
        await output.WriteAsync(_head, ct);
        foreach (SafeZipEntry file in _files)
            await output.WriteAsync(file.Content, ct);
    }
}

/// <summary>
/// <c>POST /v1/unpack-zip</c>: unpacks a customer code zip under the unpack limits and answers with a manifest
/// plus the raw bytes of every kept file. Separate from extract-zip, which is design-system specific.
/// </summary>
public static class UnpackZipHandler
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<UnpackOutcome> HandleAsync(Stream body, long? contentLength, CancellationToken ct)
    {
        if (contentLength.HasValue && contentLength.Value > UnpackLimits.MaxArchiveBytes)
            return new UnpackOutcome(413, null);

        byte[]? buffer;
        int length;
        if (contentLength is { } declared)
        {
            // Preallocated from Content-Length: no MemoryStream doubling.
            buffer = new byte[declared];
            int filled = 0;
            while (filled < buffer.Length)
            {
                int read = await body.ReadAsync(buffer.AsMemory(filled), ct);
                if (read <= 0)
                    return new UnpackOutcome(400, null);
                filled += read;
            }

            length = filled;
        }
        else
        {
            using MemoryStream grown = new();
            byte[] chunk = new byte[81920];
            long total = 0;
            while (true)
            {
                int read = await body.ReadAsync(chunk.AsMemory(), ct);
                if (read <= 0)
                    break;
                total += read;
                if (total > UnpackLimits.MaxArchiveBytes)
                    return new UnpackOutcome(413, null);
                grown.Write(chunk, 0, read);
            }

            length = (int)grown.Length;
            buffer = grown.ToArray();
        }

        if (!HasZipMagic(buffer.AsSpan(0, length)))
            return new UnpackOutcome(415, null);

        return new UnpackOutcome(200, Unpack(buffer, length, ct));
    }

    private static UnpackResponse Unpack(byte[] buffer, int length, CancellationToken ct)
    {
        SafeZipLimits limits = UnpackLimits.ToSafeZipLimits();

        // Pass 1, names only: nothing is inflated. It decides whether build output is dropped.
        List<string> names = new();
        SafeZipResult namesPass = SafeZipReader.Read(
            new MemoryStream(buffer, 0, length, writable: false),
            limits,
            name =>
            {
                names.Add(name);
                return false;
            },
            ct);

        if (namesPass.Aborted)
            return Frame(Manifest(Array.Empty<SafeZipEntry>(), namesPass, new SkipTally(), aborted: true, namesPass.AbortReason), Array.Empty<SafeZipEntry>());

        Func<string, string?> exclusion = ArchiveUnpackExclusions.BuildFilter(names);
        SkipTally tally = new();

        // Pass 2: the real read with the final filter.
        SafeZipResult result = SafeZipReader.Read(
            new MemoryStream(buffer, 0, length, writable: false),
            limits,
            name =>
            {
                string? reason = exclusion(name);
                if (reason is null)
                    return true;
                tally.Add(name, reason);
                return false;
            },
            ct);

        if (result.Aborted)
            return Frame(Manifest(Array.Empty<SafeZipEntry>(), result, tally, aborted: true, result.AbortReason), Array.Empty<SafeZipEntry>());

        List<SafeZipEntry> files = result.Entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList();
        byte[] manifest = Manifest(files, result, tally, aborted: false, abortReason: null);
        if (manifest.Length > UnpackLimits.MaxManifestBytes)
        {
            // Never a partial answer: an oversized manifest is an abort with zero files.
            manifest = Manifest(Array.Empty<SafeZipEntry>(), result, tally, aborted: true, UnpackLimits.ManifestTooLarge);
            return Frame(manifest, Array.Empty<SafeZipEntry>());
        }

        return Frame(manifest, files);
    }

    private static UnpackResponse Frame(byte[] manifest, IReadOnlyList<SafeZipEntry> files)
    {
        byte[] head = new byte[8 + manifest.Length];
        UnpackLimits.Magic.CopyTo(head);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4, 4), (uint)manifest.Length);
        manifest.CopyTo(head, 8);
        return new UnpackResponse(head, files);
    }

    private static byte[] Manifest(IReadOnlyList<SafeZipEntry> files, SafeZipResult result, SkipTally tally, bool aborted, string? abortReason)
    {
        SortedDictionary<string, int> counts = new(StringComparer.Ordinal);
        List<object> samples = new();
        foreach ((string path, string reason) in tally.Samples)
        {
            if (samples.Count >= UnpackLimits.MaxSkippedSamples)
                break;
            samples.Add(new { path = Clip(path), reason });
        }

        foreach ((string reason, int count) in tally.Counts)
            counts[reason] = count;

        foreach (SafeZipRejected rejected in result.Rejected)
        {
            // A password-protected entry is named as such so the API can tell the customer to re-zip without a password.
            string reason = rejected.Encrypted ? "ENTRY_ENCRYPTED" : rejected.Reason;
            counts[reason] = counts.GetValueOrDefault(reason) + 1;
            if (samples.Count < UnpackLimits.MaxSkippedSamples)
                samples.Add(new { path = Clip(rejected.Path), reason });
        }

        var payload = new
        {
            files = files.Select(file => new
            {
                path = file.Path,
                size = file.Content.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant(),
            }).ToList(),
            skipped = new { counts, samples },
            aborted,
            abortReason,
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload, Json);
    }

    private static string Clip(string path) => path.Length <= ArchivePathRules.MaxPathLength ? path : path[..ArchivePathRules.MaxPathLength];

    private static bool HasZipMagic(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 4
            && bytes[0] == 0x50
            && bytes[1] == 0x4B
            && ((bytes[2] == 0x03 && bytes[3] == 0x04) || (bytes[2] == 0x05 && bytes[3] == 0x06));

    private sealed class SkipTally
    {
        public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

        public List<(string Path, string Reason)> Samples { get; } = new();

        public void Add(string path, string reason)
        {
            Counts[reason] = Counts.GetValueOrDefault(reason) + 1;
            if (Samples.Count < UnpackLimits.MaxSkippedSamples)
                Samples.Add((path, reason));
        }
    }
}
