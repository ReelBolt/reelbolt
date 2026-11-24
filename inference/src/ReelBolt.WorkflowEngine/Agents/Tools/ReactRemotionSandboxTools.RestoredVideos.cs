using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compositions;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// The videos of a RESTORED workspace (<see cref="IRestoredWorkspaceVideos"/>). A workspace captured before
/// staged videos were copied (12d6ecbd4) holds the originals in <c>public/</c> and no record of them; the
/// reviser edits code and seldom stages every clip again, so its render read 2160x3840 originals and stopped
/// (run 9e22258a, frame 93 of <c>public/ad-pour-warm-light.mp4</c>). After every restore, and again before a
/// full render for anything still unrecorded, each MP4-family video under <c>public/</c> with no record (or a
/// record of an oversized original) gets exactly the treatment a fresh staging gives it:
/// <list type="number">
/// <item>Mapped to the object it came from (its recorded <c>sourceKey</c> when that is still one of the
/// project's files, else the ONE project file with the same name and size) and passed through the staging
/// path itself (<see cref="StageVideoAsync"/>), so a cached copy is staged without reading the sandbox.</item>
/// <item>Otherwise read out of the sandbox into engine scratch (bounded), keyed by its SHA-256, and passed
/// through the same probe, recipe, verification and cache (<see cref="ProbeAndCopyAsync"/>); the copy is
/// staged back under the same name.</item>
/// </list>
/// Bounded (<see cref="MaxRestoredVideosPerPass"/> videos, <see cref="MaxRestoredVideoReadBytes"/> read out of
/// the sandbox, <see cref="RestoredVideoPassBudget"/>) and fail-open: any failure leaves that video exactly as
/// restored and logs a Warning. ffmpeg never runs in the sandbox.
/// </summary>
public partial class ReactRemotionSandboxTools : IRestoredWorkspaceVideos
{
    /// <summary>Videos one pass looks at; the rest stay as they are (and are counted as skipped).</summary>
    internal const int MaxRestoredVideosPerPass = 12;

    /// <summary>Bytes one pass may read back out of the sandbox for videos it cannot map to a project object.</summary>
    internal const long MaxRestoredVideoReadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Wall clock one pass may spend; checked between videos (each copy is bounded by its own ffmpeg timeout).</summary>
    internal static readonly TimeSpan RestoredVideoPassBudget = TimeSpan.FromMinutes(10);

    private const int RestoredVideoReadTimeoutSeconds = 300;

    /// <summary>The cache identity of a video known only by its bytes (no project object it maps to).</summary>
    internal const string SandboxContentIdentityPrefix = "sandbox-sha256:";

    async Task<RestoredVideosReport> IRestoredWorkspaceVideos.PrepareRestoredVideosAsync(
        ISandboxSession session, Guid projectId, CancellationToken ct)
    {
        WorkflowExecutionContext ctx = new()
        {
            ExecutionId = session.ExecutionId,
            ProjectId = projectId,
            CorrelationId = "workspace-restore"
        };
        return await PrepareUnrecordedVideosAsync(session, ctx, ct);
    }

    /// <summary>
    /// Whether the video at <paramref name="path"/> needs a look: an MP4-family file with no record, or a
    /// record of an original over <paramref name="maxLongSide"/> whose decision may change (not transparent,
    /// not a container or picture problem). A recorded copy, or a recorded video within size, is left alone.
    /// </summary>
    internal static bool NeedsDeliveryCheck(string path, StagedVideoFacts? record, int maxLongSide)
    {
        if (!StagedVideoProxy.IsProxyableDestination(path))
            return false;
        if (record is null)
            return true;
        if (record.Proxied || StagedVideoProxy.IsFinal(record.Reason))
            return false;
        return Math.Max(record.Width, record.Height) > maxLongSide;
    }

    /// <summary>
    /// The pass itself (see the type's summary). Returns what it did; never throws except for
    /// <paramref name="ct"/>.
    /// </summary>
    internal async Task<RestoredVideosReport> PrepareUnrecordedVideosAsync(
        ISandboxSession session, WorkflowExecutionContext ctx, CancellationToken ct)
    {
        if (_videoToolRunner is null)
            return RestoredVideosReport.None;

        int copied = 0, kept = 0, failed = 0, skipped = 0, examined = 0;
        try
        {
            IReadOnlyList<(string Path, long Size)> videos = await ListStagedVideosAsync(session);
            if (videos.Count == 0)
                return RestoredVideosReport.None;

            Dictionary<string, StagedVideoFacts> records = await ReadStagedVideoManifestAsync(session);
            int maxLongSide = StagedVideoProxy.MaxLongSideFor(_stagedVideoMaxLongSide, await ResolveChosenResolutionAsync(ctx));
            List<(string Path, long Size)> candidates = [.. videos.Where(v =>
                NeedsDeliveryCheck(v.Path, records.GetValueOrDefault(v.Path), maxLongSide))];
            if (candidates.Count == 0)
                return RestoredVideosReport.None;

            IReadOnlyList<ProjectWorkspaceFile>? projectFiles = null;
            Stopwatch clock = Stopwatch.StartNew();
            long readBytes = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                (string path, long size) = candidates[i];
                if (i >= MaxRestoredVideosPerPass || clock.Elapsed > RestoredVideoPassBudget)
                {
                    skipped = candidates.Count - i;
                    _logger.LogWarning(
                        "Restored-video pass for execution {ExecutionId} stopped after {Done} of {Total} video(s) ({Elapsed} ms); " +
                        "the rest stay as restored",
                        ctx.ExecutionId, i, candidates.Count, clock.ElapsedMilliseconds);
                    break;
                }

                examined++;
                try
                {
                    projectFiles ??= await ListProjectFilesForMappingAsync(ctx);
                    string? sourceKey = MapRestoredVideo(projectFiles, records.GetValueOrDefault(path), path, size, ctx.ProjectId);
                    StagedVideoFacts? facts;
                    if (sourceKey is not null)
                    {
                        facts = await StageVideoAsync(session, ctx, sourceKey, size, path, originalInPlace: true);
                    }
                    else if (size <= 0 || size > _sourceMaxBytes || readBytes + size > MaxRestoredVideoReadBytes)
                    {
                        _logger.LogWarning(
                            "Restored video {Path} ({Size} bytes) of execution {ExecutionId} maps to no project file and is over the " +
                            "read bound; it stays as restored",
                            path, size, ctx.ExecutionId);
                        skipped++;
                        continue;
                    }
                    else
                    {
                        readBytes += size;
                        facts = await ReproxyFromSandboxAsync(session, ctx, path, maxLongSide);
                    }

                    if (facts is null)
                        failed++;
                    else if (facts.Proxied)
                        copied++;
                    else
                        kept++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failed++;
                    _logger.LogWarning(ex,
                        "Could not prepare restored video {Path} for execution {ExecutionId}; it stays as restored",
                        path, ctx.ExecutionId);
                }
            }

            _logger.LogInformation(
                "Restored-video pass for execution {ExecutionId}: {Examined} examined, {Copied} replaced by a delivery copy, " +
                "{Kept} kept, {Failed} failed, {Skipped} skipped, in {Elapsed} ms",
                ctx.ExecutionId, examined, copied, kept, failed, skipped, clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "The restored-video pass for execution {ExecutionId} could not run; the videos stay as they are", ctx.ExecutionId);
        }

        return new RestoredVideosReport(examined, copied, kept, failed, skipped);
    }

    private async Task<IReadOnlyList<ProjectWorkspaceFile>> ListProjectFilesForMappingAsync(WorkflowExecutionContext ctx)
    {
        try
        {
            return await _projectFileWorkspace.ListFilesAsync(ctx.ProjectId, CancellationToken.None) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Project files of {ProjectId} could not be listed; restored videos are read from the sandbox", ctx.ProjectId);
            return [];
        }
    }

    /// <summary>
    /// The project object a restored video came from, or null. Only an object that is one of the project's
    /// listed files is ever used (so a record edited in the workspace cannot name a hidden or foreign object):
    /// the recorded <c>sourceKey</c> when it is such a file, else the ONE file with the same name and size.
    /// </summary>
    internal static string? MapRestoredVideo(
        IReadOnlyList<ProjectWorkspaceFile> files, StagedVideoFacts? record, string path, long size, Guid projectId)
    {
        string prefix = $"projects/{projectId}/";
        List<ProjectWorkspaceFile> own = [.. files.Where(f => f.StorageKey.StartsWith(prefix, StringComparison.Ordinal))];

        if (record?.SourceKey is { Length: > 0 } recorded
            && own.Any(f => string.Equals(f.StorageKey, recorded, StringComparison.Ordinal)))
            return recorded;

        string name = Path.GetFileName(path);
        List<string> matches = [.. own
            .Where(f => f.SizeBytes == size && size > 0 && string.Equals(f.OriginalFileName, name, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.StorageKey)
            .Distinct(StringComparer.Ordinal)];
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// A restored video with no project object behind it: read out of the sandbox (bounded by
    /// <see cref="_sourceMaxBytes"/>), keyed by its SHA-256, probed and copied with the staging recipe and
    /// cache, the copy staged back under the same name, and the facts recorded. Null when nothing was recorded.
    /// </summary>
    private async Task<StagedVideoFacts?> ReproxyFromSandboxAsync(
        ISandboxSession session, WorkflowExecutionContext ctx, string path, int maxLongSide)
    {
        string scratch = NewVideoScratch();
        try
        {
            string sourcePath = Path.Combine(scratch, "source");
            long bytes;
            string hash;
            await using (SandboxRawFile raw = await session.OpenRawFileAsync(path, RestoredVideoReadTimeoutSeconds, CancellationToken.None))
            await using (FileStream file = File.Create(sourcePath))
            {
                (bytes, hash) = await CopyBoundedWithHashAsync(raw.Content, file, _sourceMaxBytes);
            }

            string stem = StagedVideoProxy.CacheKeyStem(ctx.ProjectId, SandboxContentIdentityPrefix + hash, bytes, null, maxLongSide);
            bool copied;
            CachedVideoFacts facts;
            if (await TryReadCachedFactsForAsync(ctx, path, stem, path, maxLongSide) is { } cached)
                (copied, facts) = (cached.Proxied, cached);
            else
                (copied, facts) = await ProbeAndCopyAsync(ctx, path, sourcePath, bytes, path, maxLongSide, stem, scratch);

            StagedVideoFacts staged = FactsOf(path, facts, proxied: false) with { StagedBytes = bytes };
            if (copied)
            {
                try
                {
                    await StageObjectAsync(session, ctx.ProjectId, stem + ".mp4", path, _sourceMaxBytes, CancellationToken.None);
                    staged = FactsOf(path, facts, proxied: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Staging the delivery copy of restored video {Path} failed for execution {ExecutionId}; keeping the original",
                        path, ctx.ExecutionId);
                    await TryPutBackOriginalAsync(session, path, sourcePath, bytes);
                    staged = staged with { Reason = "copy_not_staged" };
                }
            }

            await RecordStagedVideoAsync(session, staged);
            return staged;
        }
        finally
        {
            DeleteVideoScratch(scratch);
        }
    }

    /// <summary>After a failed copy staging, writes the original back when the file is no longer its own size. Best effort.</summary>
    private async Task TryPutBackOriginalAsync(ISandboxSession session, string path, string sourcePath, long bytes)
    {
        try
        {
            string directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? StaticFileDirectory.TrimEnd('/');
            IReadOnlyList<SandboxFileEntry> entries;
            try
            {
                entries = await session.ListFilesAsync(directory, CancellationToken.None);
            }
            catch (SandboxRequestFailedException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                entries = [];
            }

            string name = Path.GetFileName(path);
            if (entries.Any(e => !e.IsDir && e.Name == name && e.Size == bytes))
                return;
            await session.WriteBytesAsync(path, await File.ReadAllBytesAsync(sourcePath), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not put the original of restored video {Path} back", path);
        }
    }

    /// <summary>Copies at most <paramref name="maxBytes"/> (else throws) and returns the count and the lowercase SHA-256.</summary>
    internal static async Task<(long Bytes, string Sha256)> CopyBoundedWithHashAsync(Stream source, Stream destination, long maxBytes)
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, CancellationToken.None)) > 0)
        {
            total += read;
            if (maxBytes > 0 && total > maxBytes)
                throw new InvalidOperationException($"The restored video is over the {maxBytes}-byte read bound.");
            sha.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
        }
        return (total, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }
}
