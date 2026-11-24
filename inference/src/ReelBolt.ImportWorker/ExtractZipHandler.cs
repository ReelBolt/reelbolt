using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.ImportWorker;

/// <summary>
/// The only place uploaded zip bytes are parsed. It reads the body under a hard cap, hands it to the
/// shared <see cref="SafeZipReader"/>, then classifies each accepted entry in ordinal path order.
/// </summary>
public static class ExtractZipHandler
{
    public static async Task<WorkerResult> HandleAsync(
        Stream body,
        long? contentLength,
        WorkerOptions options,
        CancellationToken ct)
    {
        // A declared length over the cap is refused before a single byte is read.
        if (contentLength.HasValue && contentLength.Value > options.MaxBodyBytes)
            return new WorkerResult(413, null);

        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        long total = 0;

        while (true)
        {
            int read = await body.ReadAsync(chunk.AsMemory(0, chunk.Length), ct);
            if (read <= 0)
                break;

            total += read;
            if (total > options.MaxBodyBytes)
                return new WorkerResult(413, null);

            buffer.Write(chunk, 0, read);
        }

        // A body that is not a zip at all is a 415 (ImportWorkerStatus.NotZip), not an "invalid archive".
        if (!HasZipMagic(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
            return new WorkerResult(415, null);

        buffer.Position = 0;

        SafeZipResult zip = SafeZipReader.Read(buffer, new SafeZipLimits(), null, ct);

        List<WorkerDroppedDto> dropped = new(zip.Rejected.Count);
        foreach (SafeZipRejected rejected in zip.Rejected)
            dropped.Add(new WorkerDroppedDto(rejected.Path, rejected.Reason));

        if (zip.Aborted)
        {
            return new WorkerResult(200, new ExtractZipResponse(
                Array.Empty<WorkerFileDto>(),
                BoundDropped(dropped),
                new WorkerStatsDto(0, 0),
                true,
                zip.AbortReason));
        }

        List<WorkerFileDto> files = new(zip.Entries.Count);
        long inlinedTotal = 0;
        int inlinedAssets = 0;
        long inlinedAssetBytes = 0;
        int inlinedFonts = 0;
        long inlinedFontBytes = 0;
        long bytes = 0;

        foreach (SafeZipEntry entry in zip.Entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            (string? kind, string? dropReason) = ZipFileClassifier.Classify(entry.Path, entry.Content);
            if (dropReason is not null)
            {
                dropped.Add(new WorkerDroppedDto(entry.Path, dropReason));

                // Scripts are never inlined, a tailwind config included. Its NAME is still reported (empty text,
                // zero bytes) so the importer can say the file was seen and not read, instead of silently
                // pretending the archive had no Tailwind theme.
                if (dropReason == "DROPPED_SCRIPT" && ZipFileClassifier.IsTailwindConfig(entry.Path))
                {
                    files.Add(new WorkerFileDto(
                        entry.Path,
                        Convert.ToHexString(SHA256.HashData(entry.Content)).ToLowerInvariant(),
                        entry.Content.Length,
                        "text",
                        string.Empty));
                }

                continue;
            }

            // A Sketch document.json too large to inline would otherwise vanish without a trace: the importer
            // never receives it and nothing says why.
            if (kind == "json"
                && entry.Content.Length > options.ZipInlineFileMaxBytes
                && string.Equals(Path.GetFileName(entry.Path), "document.json", StringComparison.OrdinalIgnoreCase))
            {
                dropped.Add(new WorkerDroppedDto(entry.Path, "DOCUMENT_TOO_LARGE"));
            }

            // SVG and markup travel as inert text on the same cumulative text budget, each under its own cap.
            // The binary channel never carries them: no name can turn text into bytes the host will trust.
            string? text = null;
            long? inlineCap = kind switch
            {
                "svg" => ImportLimits.SvgMaxBytes,
                "markup" => ImportLimits.MarkupMaxBytes,
                "json" or "css" or "text" => options.ZipInlineFileMaxBytes,
                _ => null,
            };

            if (inlineCap is { } cap
                && entry.Content.Length <= cap
                && inlinedTotal + entry.Content.Length <= options.ZipInlineTotalMaxBytes)
            {
                text = Encoding.UTF8.GetString(entry.Content);
                inlinedTotal += entry.Content.Length;
            }

            // An SVG or markup entry that did not fit its per-kind cap (or the cumulative text budget)
            // would otherwise cross as a file with no text and no reason: name the drop so the report
            // says why it was not read.
            if (text is null && kind is "svg" or "markup")
            {
                dropped.Add(new WorkerDroppedDto(
                    entry.Path, kind == "svg" ? "SVG_TOO_LARGE" : "MARKUP_TOO_LARGE"));
            }

            // The image channel: an image the classifier accepted by magic, under an `assets` segment (or the export's
            // exact `.thumbnail` preview basename), within the per-entry, count and total caps. An image the channel
            // cannot carry is named in the dropped list so the report says why it was not inlined.
            string? base64 = null;
            if (kind == "image"
                && ZipFileClassifier.MediaType(entry.Path, entry.Content) is not null)
            {
                if (!IsAssetPath(entry.Path))
                {
                    dropped.Add(new WorkerDroppedDto(entry.Path, "IMAGE_NOT_ASSET_PATH"));
                }
                else if (entry.Content.Length > ArchiveAssetLimits.MaxEntryBytes)
                {
                    dropped.Add(new WorkerDroppedDto(entry.Path, "IMAGE_TOO_LARGE"));
                }
                else if (inlinedAssets >= ArchiveAssetLimits.MaxEntries
                    || inlinedAssetBytes + entry.Content.Length > ArchiveAssetLimits.MaxTotalBytes)
                {
                    dropped.Add(new WorkerDroppedDto(entry.Path, "IMAGE_CAP"));
                }
                else
                {
                    base64 = Convert.ToBase64String(entry.Content);
                    inlinedAssets++;
                    inlinedAssetBytes += entry.Content.Length;
                }
            }

            // The font channel: magic-checked bytes under a `fonts` or `assets` segment, bounded by its own caps.
            // The worker never decodes a font; the API re-checks its length and digest. A font the channel cannot
            // carry is dropped by name and never emitted as `kind=font` with no bytes: the API refuses such an
            // answer as `font_shape` and the whole archive would fail with 502.
            else if (kind == "font")
            {
                if (!IsFontPath(entry.Path))
                {
                    dropped.Add(new WorkerDroppedDto(entry.Path, "FONT_NOT_ASSET_PATH"));
                    continue;
                }

                if (entry.Content.Length > ArchiveFontLimits.MaxEntryBytes)
                {
                    dropped.Add(new WorkerDroppedDto(entry.Path, "FONT_TOO_LARGE"));
                    continue;
                }

                if (inlinedFonts >= ArchiveFontLimits.MaxEntries
                    || inlinedFontBytes + entry.Content.Length > ArchiveFontLimits.MaxTotalBytes)
                {
                    dropped.Add(new WorkerDroppedDto(entry.Path, "FONT_CAP"));
                    continue;
                }

                base64 = Convert.ToBase64String(entry.Content);
                inlinedFonts++;
                inlinedFontBytes += entry.Content.Length;
            }

            files.Add(new WorkerFileDto(
                entry.Path,
                Convert.ToHexString(SHA256.HashData(entry.Content)).ToLowerInvariant(),
                entry.Content.Length,
                kind!,
                text,
                base64));

            bytes += entry.Content.Length;
        }

        return new WorkerResult(200, new ExtractZipResponse(
            files,
            BoundDropped(dropped),
            new WorkerStatsDto(files.Count, bytes),
            false,
            null));
    }

    /// <summary>
    /// True when any segment is named <c>assets</c> (case-insensitive) or the exact basename is <c>.thumbnail</c>
    /// (the export's preview WEBP): the places an image is selected from.
    /// </summary>
    private static bool IsAssetPath(string path)
    {
        if (string.Equals(Path.GetFileName(path), ".thumbnail", StringComparison.Ordinal))
            return true;

        foreach (string segment in path.Split('/'))
        {
            if (string.Equals(segment, "assets", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>True when any segment is named <c>fonts</c> or <c>assets</c> (case-insensitive): where font bytes may be inlined from.</summary>
    private static bool IsFontPath(string path)
    {
        foreach (string segment in path.Split('/'))
        {
            if (string.Equals(segment, "fonts", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "assets", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private const int MaxDroppedNames = 200;
    private const int MaxDroppedNameLength = 128;

    /// <summary>
    /// The refused names are attacker-chosen, so the list is bounded before it leaves the worker: at most
    /// <see cref="MaxDroppedNames"/> entries, each name clipped to <see cref="MaxDroppedNameLength"/> characters of
    /// printable ASCII (each other character becomes one <c>?</c>), and one final entry counting the rest. That keeps the
    /// answer inside the API's own validation instead of being refused there as a bad response.
    /// </summary>
    private static List<WorkerDroppedDto> BoundDropped(List<WorkerDroppedDto> dropped)
    {
        int keep = Math.Min(dropped.Count, MaxDroppedNames);
        List<WorkerDroppedDto> bounded = new(keep + 1);
        for (int i = 0; i < keep; i++)
            bounded.Add(dropped[i] with { Path = PrintableClip(dropped[i].Path) });

        if (dropped.Count > keep)
            bounded.Add(new WorkerDroppedDto($"... and {dropped.Count - keep} more", "DROPPED_MORE"));

        return bounded;
    }

    private static string PrintableClip(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "?";

        // By characters (Unicode scalar values), so a surrogate pair is never split: every printable ASCII
        // character stays, every other character becomes exactly one '?'.
        StringBuilder builder = new(Math.Min(name.Length, MaxDroppedNameLength));
        int characters = 0;
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (characters++ == MaxDroppedNameLength)
                break;
            builder.Append(rune.Value is >= ' ' and <= '~' ? (char)rune.Value : '?');
        }

        return builder.ToString();
    }

    /// <summary>True for a local-file header (<c>PK\x03\x04</c>) or an empty archive's end record (<c>PK\x05\x06</c>).</summary>
    private static bool HasZipMagic(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 4
            && bytes[0] == 0x50
            && bytes[1] == 0x4B
            && ((bytes[2] == 0x03 && bytes[3] == 0x04) || (bytes[2] == 0x05 && bytes[3] == 0x06));
}
