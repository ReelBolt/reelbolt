using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Editing.Interchange;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>What the package builder writes.</summary>
/// <param name="InterchangeEntryName">The timeline file's entry name, <c>&lt;timeline-slug&gt;.&lt;ext&gt;</c>.</param>
/// <param name="IncludeMedia">False for a manifest-only package.</param>
/// <param name="NotIncludedReason">Why the media is not in the package (plain sentence), when <see cref="IncludeMedia"/> is false.</param>
public sealed record HandOffPackageInput(
    Guid ProjectId,
    string TimelineName,
    string Format,
    string InterchangeEntryName,
    string InterchangeText,
    IReadOnlyList<HandOffSource> Sources,
    bool IncludeMedia,
    string? NotIncludedReason,
    DateTime GeneratedAtUtc);

/// <summary>
/// R12.2: writes the hand-off ZIP straight to an output stream (no temp file, no whole-zip buffer): the timeline file,
/// each media file once under <c>media/&lt;safe-name&gt;</c> (Store: already compressed), then <c>README.txt</c>, and
/// <c>manifest.json</c> LAST so it reflects what actually happened. A storage object that is missing or unreadable is
/// listed in the manifest and the zip still completes; only a client abort (cancellation) stops it.
/// </summary>
public static class HandOffPackage
{
    public const string ManifestEntry = "manifest.json";
    public const string ReadmeEntry = "README.txt";

    /// <summary>The per-file outcome words used in the manifest.</summary>
    public const string Included = "included";
    public const string Missing = "missing";
    public const string NotIncluded = "notIncluded";
    public const string Incomplete = "incomplete";

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Outcome(HandOffSource Source, string Status, string? Reason, long? SizeBytes);

    public static async Task WriteAsync(Stream output, HandOffPackageInput input, IFileStorageService storage, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(logger);

        // Not in a using block on purpose: when the client aborts we must not try to write a central directory to a dead stream.
        ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true);

        ZipArchiveEntry timeline = zip.CreateEntry(input.InterchangeEntryName, CompressionLevel.Optimal);
        await using (Stream entry = timeline.Open())
        {
            await entry.WriteAsync(Encoding.UTF8.GetBytes(input.InterchangeText), ct).ConfigureAwait(false);
        }

        List<Outcome> outcomes = [];
        byte[] buffer = new byte[81920];
        foreach (HandOffSource source in input.Sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!source.Resolvable)
            {
                outcomes.Add(new Outcome(source, Missing, "This file is not in the project any more.", null));
                continue;
            }

            if (!input.IncludeMedia)
            {
                outcomes.Add(new Outcome(source, NotIncluded, input.NotIncludedReason ?? "The media was not included.", source.SizeBytes));
                continue;
            }

            Stream? content = null;
            try
            {
                content = await storage.DownloadAsync(input.ProjectId, source.StorageKey!, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Hand-off: a media object could not be opened; listing it as missing");
                outcomes.Add(new Outcome(source, Missing, "This file could not be read from storage.", source.SizeBytes));
                continue;
            }

            long written = 0;
            try
            {
                await using (content.ConfigureAwait(false))
                {
                    ZipArchiveEntry media = zip.CreateEntry(HandOffNaming.EntryName(source.SafeName), CompressionLevel.NoCompression);
                    await using Stream entry = media.Open();
                    int read;
                    while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await entry.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        written += read;
                    }
                }

                outcomes.Add(new Outcome(source, Included, null, written));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The entry is already started and a streamed zip cannot take it back: say so in the manifest.
                logger.LogWarning(ex, "Hand-off: a media object failed part way through; listing it as incomplete");
                outcomes.Add(new Outcome(source, Incomplete, "This file could not be read completely, so the copy in the package is cut short. Use the original.", source.SizeBytes));
            }
        }

        ZipArchiveEntry readme = zip.CreateEntry(ReadmeEntry, CompressionLevel.Optimal);
        await using (Stream entry = readme.Open())
        {
            await entry.WriteAsync(Encoding.UTF8.GetBytes(Readme(input, outcomes)), ct).ConfigureAwait(false);
        }

        ZipArchiveEntry manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
        await using (Stream entry = manifest.Open())
        {
            await entry.WriteAsync(Encoding.UTF8.GetBytes(Manifest(input, outcomes)), ct).ConfigureAwait(false);
        }

        zip.Dispose();
    }

    private static string Manifest(HandOffPackageInput input, List<Outcome> outcomes)
    {
        var files = outcomes.Select(o => new
        {
            name = o.Source.DisplayName,
            path = HandOffNaming.EntryName(o.Source.SafeName),
            status = o.Status,
            inPackage = o.Status is Included or Incomplete,
            reason = o.Reason,
            sizeBytes = o.SizeBytes,
            sha256 = o.Source.Sha256,
            durationSec = o.Source.DurationSec,
            usedBy = o.Source.UsedBy
        });
        return JsonSerializer.Serialize(new
        {
            timeline = input.TimelineName,
            format = input.Format,
            generatedAt = input.GeneratedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            mediaIncluded = input.IncludeMedia,
            mediaNotIncludedReason = input.IncludeMedia ? null : input.NotIncludedReason,
            files
        }, ManifestJson);
    }

    private static string Readme(HandOffPackageInput input, List<Outcome> outcomes)
    {
        int included = outcomes.Count(o => o.Status == Included);
        int notThere = outcomes.Count(o => o.Status is Missing or Incomplete);
        StringBuilder sb = new();
        sb.Append("ReelBolt hand-off package for \"").Append(input.TimelineName).Append("\"\r\n\r\n");
        sb.Append("What is in this folder\r\n");
        sb.Append("- ").Append(input.InterchangeEntryName).Append(" is the cut, as a ").Append(input.Format.ToUpperInvariant()).Append(" file.\r\n");
        sb.Append("- The media folder holds the video, audio and pictures the cut uses, under their own file names.\r\n");
        sb.Append("- manifest.json lists every file the cut uses, its size, its length and which clips use it.\r\n\r\n");
        if (!input.IncludeMedia)
        {
            sb.Append("The media files are not in this package. ").Append(input.NotIncludedReason ?? string.Empty).Append("\r\n");
            sb.Append("The cut still points at media/<file name> for each one, so put the originals in a folder called media next to the cut and relink.\r\n\r\n");
        }
        else
        {
            sb.Append(included).Append(included == 1 ? " media file is" : " media files are").Append(" in this package.");
            if (notThere > 0) sb.Append(' ').Append(notThere).Append(notThere == 1 ? " could not be included; manifest.json says which and why." : " could not be included; manifest.json says which ones and why.");
            sb.Append("\r\n\r\n");
        }

        sb.Append("How to open it\r\n");
        sb.Append("Unzip the whole package first and keep the cut and the media folder side by side.\r\n\r\n");
        sb.Append("Premiere Pro: choose File, Import and pick the cut. If it asks for media, choose Locate and pick the file with the same name in the media folder. Tick Repeat for the other files so it finds the rest by itself.\r\n\r\n");
        sb.Append("DaVinci Resolve: choose File, Import, Timeline and pick the cut. If clips show as offline, right-click them in the media pool and choose Relink Selected Clips, then pick the media folder.\r\n\r\n");
        sb.Append("Final Cut Pro: choose File, Import, XML and pick the cut (use the FCPXML format for Final Cut). If a clip is missing, choose File, Relink Files and pick the media folder.\r\n\r\n");
        sb.Append("Every clip is matched by its file name, so please do not rename the files.\r\n");
        return sb.ToString();
    }
}

/// <summary>
/// Writes a streamed hand-off package to the response: security headers first, then the body through
/// <see cref="HandOffPackage"/>. Holds the per-user stream lease until the response is finished.
/// </summary>
public sealed class HandOffStreamResult(string fileName, Func<Stream, CancellationToken, Task> write, IDisposable? lease = null) : IActionResult
{
    public string FileName { get; } = fileName;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        HttpContext http = context.HttpContext;
        try
        {
            SafeContentPolicy.ApplySecurityHeaders(http.Response);
            http.Response.ContentType = "application/zip";
            http.Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(false, FileName);
            http.Response.Headers["Cache-Control"] = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // ZipArchive writes synchronously to its output; this request streams a large body, so allow it for this request only.
            if (http.Features.Get<IHttpBodyControlFeature>() is { } bodyControl) bodyControl.AllowSynchronousIO = true;
            await write(http.Response.Body, http.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The client went away: storage reads stopped promptly and there is nobody to answer.
        }
        finally
        {
            lease?.Dispose();
        }
    }
}
