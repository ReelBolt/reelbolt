using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.S3.Model;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// Oversized videos staged into the sandbox (runs 36df4287 and 9bf15ff8: five 2160x3840 clips playing at
/// once in a 1080x1920 grid, "Could not extract frame from compositor"). Three parts:
/// <list type="number">
/// <item>Staging a video whose long side is over <see cref="StagedVideoProxy.MaxLongSideFor"/> stages a
/// delivery copy made by the ENGINE's ffmpeg (<see cref="StagedVideoProxy"/>; never ffmpeg in the sandbox),
/// under the same file name, cached in object storage by the source object so the second staging is free.</item>
/// <item>Every staged video's facts are kept in <see cref="StagedVideosManifestPath"/>, and a render's
/// <c>&lt;OffthreadVideo&gt;</c> cache is sized from them (<see cref="SizeOffthreadVideoCache"/>) instead of a
/// flat 256 MiB that starved several simultaneous clips.</item>
/// <item>A render that cannot read a video frame names the video(s) and their resolution.</item>
/// </list>
/// Any failure in the copy path stages the original file exactly as before; the copy never fails a staging.
/// </summary>
public partial class ReactRemotionSandboxTools
{
    /// <summary>Workspace file (outside <c>public/</c>, so never served) recording what each staged video is.</summary>
    internal const string StagedVideosManifestPath = ".reelbolt/staged-videos.json";

    /// <summary>Configuration key of the largest <c>&lt;OffthreadVideo&gt;</c> cache a render is sized to.</summary>
    internal const string OffthreadVideoCacheMaxBytesKey = "Compute:Render:OffthreadVideoCacheMaxBytes";

    /// <summary>1 GiB: holds the 2026-11-05 repro (six 1080x1920 copies at concurrency 2) inside an 8 GB sandbox.</summary>
    internal const long DefaultOffthreadVideoCacheMaxBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Decoded frames budgeted per staged video per render tab. Measured on the repro: about 3 per clip
    /// (256 MiB) stalls, about 7 (512 MiB) renders; 12 leaves room for B-frame reordering.
    /// </summary>
    internal const int OffthreadVideoFramesPerVideo = 12;

    /// <summary>Bytes per decoded pixel the budget assumes (the compositor keeps RGB, RGBA with transparency).</summary>
    internal const int OffthreadVideoBytesPerPixel = 4;

    /// <summary>A staged video whose size is not recorded is budgeted as a 1920x1080 frame.</summary>
    internal const long UnknownVideoFrameBytes = 1920L * 1080 * OffthreadVideoBytesPerPixel;

    /// <summary>
    /// An unrecorded video file larger than this is budgeted as a 3840x2160 frame instead
    /// (<see cref="LargeUnknownVideoFrameBytes"/>): a phone's 4K clip passes 32 MiB in about six seconds,
    /// a 1080p H.264 clip at 8-10 Mbit/s only after about half a minute. Conservative on purpose: a larger
    /// cache is still capped by <c>Compute:Render:OffthreadVideoCacheMaxBytes</c>.
    /// </summary>
    internal const long UnrecordedLargeVideoBytes = 32L * 1024 * 1024;

    /// <summary>The frame budget of a large unrecorded video (see <see cref="UnrecordedLargeVideoBytes"/>).</summary>
    internal const long LargeUnknownVideoFrameBytes = 3840L * 2160 * OffthreadVideoBytesPerPixel;

    /// <summary>The frame budget of a video under <c>public/</c>: its recorded size, else guessed from its file size.</summary>
    internal static long FrameBytesFor(StagedVideoFacts? facts, long fileBytes) =>
        facts is { Width: > 0, Height: > 0 }
            ? (long)facts.Width * facts.Height * OffthreadVideoBytesPerPixel
            : fileBytes > UnrecordedLargeVideoBytes ? LargeUnknownVideoFrameBytes : UnknownVideoFrameBytes;

    private const int MaxManifestEntries = 200;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// What one staged video is, as staged (the copy's size when a copy was staged). <paramref name="SourceKey"/>
    /// is the object it was staged from (null when it was read back out of a restored workspace) and
    /// <paramref name="Reason"/> why it was or was not copied (<see cref="StagedVideoProxy.Reasons"/>); both are
    /// absent from records written before they existed.
    /// </summary>
    internal sealed record StagedVideoFacts(
        string Path,
        int Width,
        int Height,
        int FpsNum,
        int FpsDen,
        bool Proxied,
        int SourceWidth,
        int SourceHeight,
        long? StagedBytes = null,
        string? SourceKey = null,
        string? Reason = null);

    /// <summary>The facts of one source object, cached next to its copy (<c>{stem}.json</c>).</summary>
    internal sealed record CachedVideoFacts(
        string Recipe,
        bool Proxied,
        string Reason,
        int SourceWidth,
        int SourceHeight,
        int Width,
        int Height,
        int FpsNum,
        int FpsDen,
        long? Bytes);

    private sealed record StagedVideoManifest(Dictionary<string, StagedVideoFacts> Videos);

    /// <summary>Whether a staged project file is a video this tool may make a copy of (by type or name).</summary>
    internal static bool IsStagedVideo(string? mimeType, string destination) =>
        (mimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ?? false)
        || VideoExtensions.Contains(Path.GetExtension(destination));

    /// <summary>
    /// Stages the video object <paramref name="sourceKey"/> at <paramref name="sandboxPath"/>: its cached or new
    /// delivery copy when it is oversized, the object itself otherwise. Errors staging the ORIGINAL propagate as
    /// they always did (the callers map them); everything about the copy is best effort.
    /// <paramref name="originalInPlace"/>: the original is already at <paramref name="sandboxPath"/> (a restored
    /// workspace), so it is staged again only when a copy was tried and failed half way.
    /// </summary>
    private async Task<StagedVideoFacts?> StageVideoAsync(
        ISandboxSession session, WorkflowExecutionContext ctx, string sourceKey, long sourceSize, string sandboxPath,
        bool originalInPlace = false)
    {
        string stageKey = sourceKey;
        StagedVideoFacts? facts = null;
        bool copyStagingFailed = false;
        if (_videoToolRunner is not null)
        {
            try
            {
                (stageKey, facts) = await PrepareStagedVideoAsync(ctx, sourceKey, sourceSize, sandboxPath);
                facts = facts is null ? null : facts with { SourceKey = sourceKey };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not prepare a delivery copy of {SourceKey} for execution {ExecutionId}; staging the original",
                    sourceKey, ctx.ExecutionId);
                stageKey = sourceKey;
                facts = null;
            }
        }

        if (!string.Equals(stageKey, sourceKey, StringComparison.Ordinal))
        {
            try
            {
                await StageObjectAsync(session, ctx.ProjectId, stageKey, sandboxPath, _sourceMaxBytes, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Staging the delivery copy {CopyKey} of {SourceKey} failed for execution {ExecutionId}; staging the original",
                    stageKey, sourceKey, ctx.ExecutionId);
                stageKey = sourceKey;
                copyStagingFailed = true;
                facts = facts is null
                    ? null
                    : facts with
                    {
                        Width = facts.SourceWidth,
                        Height = facts.SourceHeight,
                        Proxied = false,
                        StagedBytes = sourceSize,
                        Reason = "copy_not_staged"
                    };
            }
        }

        if (string.Equals(stageKey, sourceKey, StringComparison.Ordinal) && (!originalInPlace || copyStagingFailed))
            await StageObjectAsync(session, ctx.ProjectId, sourceKey, sandboxPath, _sourceMaxBytes, CancellationToken.None);

        if (facts is not null)
            await RecordStagedVideoAsync(session, facts);
        return facts;
    }

    /// <summary>
    /// The key to stage and what it is: the cached facts when this source object was seen before, else the
    /// source downloaded to engine scratch, probed under the demuxer whitelist, and copied when oversized.
    /// </summary>
    private async Task<(string StageKey, StagedVideoFacts? Facts)> PrepareStagedVideoAsync(
        WorkflowExecutionContext ctx, string sourceKey, long sourceSize, string sandboxPath)
    {
        int maxLongSide = StagedVideoProxy.MaxLongSideFor(_stagedVideoMaxLongSide, await ResolveChosenResolutionAsync(ctx));
        string stem = StagedVideoProxy.CacheKeyStem(ctx.ProjectId, sourceKey, sourceSize, await TryGetEtagAsync(sourceKey), maxLongSide);
        string copyKey = stem + ".mp4";

        if (await TryReadCachedFactsForAsync(ctx, sourceKey, stem, sandboxPath, maxLongSide) is { } cached)
        {
            return cached.Proxied
                ? (copyKey, FactsOf(sandboxPath, cached, proxied: true))
                : (sourceKey, FactsOf(sandboxPath, cached, proxied: false) with { StagedBytes = sourceSize });
        }

        string scratch = NewVideoScratch();
        try
        {
            string sourcePath = Path.Combine(scratch, "source");
            using (GetObjectResponse stored = await _s3Client.GetObjectAsync(_bucketName, sourceKey, CancellationToken.None))
            await using (FileStream file = File.Create(sourcePath))
            {
                await stored.ResponseStream.CopyToAsync(file, CancellationToken.None);
            }

            (bool copied, CachedVideoFacts facts) = await ProbeAndCopyAsync(
                ctx, sourceKey, sourcePath, sourceSize, sandboxPath, maxLongSide, stem, scratch);
            return copied
                ? (copyKey, FactsOf(sandboxPath, facts, proxied: true))
                : (sourceKey, FactsOf(sandboxPath, facts, proxied: false));
        }
        finally
        {
            DeleteVideoScratch(scratch);
        }
    }

    /// <summary>
    /// The cached facts under <paramref name="stem"/>, adjusted to <paramref name="sandboxPath"/> (a copy is an
    /// MP4, so a destination of another container takes the original even when a copy exists); null on a miss.
    /// </summary>
    private async Task<CachedVideoFacts?> TryReadCachedFactsForAsync(
        WorkflowExecutionContext ctx, string label, string stem, string sandboxPath, int maxLongSide)
    {
        if (await TryReadCachedFactsAsync(stem + ".json") is not { } cached)
            return null;
        if (cached.Proxied && !StagedVideoProxy.Decide(ProbeOf(cached), sandboxPath, maxLongSide).MakeCopy)
            cached = cached with { Proxied = false };
        _logger.LogInformation(
            "Staged video {SourceKey} for execution {ExecutionId}: cached facts ({Width}x{Height}, copy {Proxied})",
            label, ctx.ExecutionId, cached.SourceWidth, cached.SourceHeight, cached.Proxied);
        return cached;
    }

    /// <summary>
    /// The one copy recipe, shared by staging and by a restored workspace: probes the local
    /// <paramref name="sourcePath"/> under the demuxer whitelist and, when it is oversized, makes the
    /// delivery copy, verifies it (a picture, the same frame rate), uploads it to <c>{stem}.mp4</c> and caches
    /// the facts at <c>{stem}.json</c>. <c>Copied</c> is false for a video kept as it is, and for a failed or
    /// unverified copy (whose facts are not cached, so the next staging tries again).
    /// </summary>
    private async Task<(bool Copied, CachedVideoFacts Facts)> ProbeAndCopyAsync(
        WorkflowExecutionContext ctx, string label, string sourcePath, long sourceSize, string sandboxPath,
        int maxLongSide, string stem, string scratch)
    {
        string copyKey = stem + ".mp4";
        string factsKey = stem + ".json";

        MediaProbeResult probe = await ProbeUntrustedAsync(sourcePath);
        StagedVideoProxy.Decision decision = StagedVideoProxy.Decide(probe, sandboxPath, maxLongSide);
        if (!decision.MakeCopy)
        {
            CachedVideoFacts plain = new(StagedVideoProxy.RecipeVersion, false, decision.Reason,
                probe.Width, probe.Height, probe.Width, probe.Height, probe.FpsNum, probe.FpsDen, sourceSize);
            await TryPutJsonAsync(factsKey, plain);
            return (false, plain);
        }

        string? hdrKind = HdrToneMap.KindOf(probe) is { } kind
            && await FfmpegHdrToneMapCapability.Shared.IsAvailableAsync(_videoToolRunner!, CancellationToken.None)
                ? kind
                : null;
        string copyPath = Path.Combine(scratch, "copy.mp4");
        string[] args = StagedVideoProxy.BuildArgs(
            sourcePath, copyPath, probe, maxLongSide, hdrKind, _videoEditingOptions?.FfmpegThreads ?? 0);
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(60, _videoEditingOptions?.CompileTimeoutSeconds ?? 1800));

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        VideoToolResult run;
        // Making the copy is engine work the agent waits on, like a render: it does not count against
        // the agent's own run budget (its own timeout bounds it).
        using (AgentRunBudget.Current?.Exclude(timeout))
        {
            run = await _videoToolRunner!.RunFfmpegAsync(args, timeout, CancellationToken.None);
        }
        if (!run.Succeeded || !File.Exists(copyPath))
        {
            _logger.LogWarning(
                "The delivery copy of {SourceKey} failed for execution {ExecutionId} (exit {ExitCode}, timedOut={TimedOut}); " +
                "staging the original. ffmpeg said: {Tail}",
                label, ctx.ExecutionId, run.ExitCode, run.TimedOut, Tail(run.StdErr, 10));
            return (false, PlainFacts(probe, sourceSize, "copy_failed"));
        }

        MediaProbeResult copyProbe = await ProbeUntrustedAsync(copyPath);
        if (copyProbe.Width <= 0 || copyProbe.Height <= 0
            || (long)copyProbe.FpsNum * probe.FpsDen != (long)probe.FpsNum * copyProbe.FpsDen)
        {
            _logger.LogWarning(
                "The delivery copy of {SourceKey} for execution {ExecutionId} is {Width}x{Height} at {Num}/{Den} fps, " +
                "not the source's {SourceNum}/{SourceDen}; staging the original",
                label, ctx.ExecutionId, copyProbe.Width, copyProbe.Height, copyProbe.FpsNum, copyProbe.FpsDen,
                probe.FpsNum, probe.FpsDen);
            return (false, PlainFacts(probe, sourceSize, "copy_unverified"));
        }

        long copyBytes = new FileInfo(copyPath).Length;
        await using (FileStream upload = File.OpenRead(copyPath))
        {
            await _s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = copyKey,
                InputStream = upload,
                ContentType = "video/mp4",
                AutoCloseStream = false
            }, CancellationToken.None);
        }

        CachedVideoFacts copied = new(StagedVideoProxy.RecipeVersion, true, decision.Reason,
            probe.Width, probe.Height, copyProbe.Width, copyProbe.Height, copyProbe.FpsNum, copyProbe.FpsDen, copyBytes);
        await TryPutJsonAsync(factsKey, copied);

        _logger.LogInformation(
            "Made a delivery copy of {SourceKey} for execution {ExecutionId}: {SourceWidth}x{SourceHeight} -> {Width}x{Height} " +
            "at {Num}/{Den} fps, {Bytes} bytes, in {Elapsed} ms (HDR tone-map: {Hdr})",
            label, ctx.ExecutionId, probe.Width, probe.Height, copyProbe.Width, copyProbe.Height,
            copyProbe.FpsNum, copyProbe.FpsDen, copyBytes, clock.ElapsedMilliseconds, hdrKind ?? "none");
        return (true, copied);
    }

    private string NewVideoScratch()
    {
        string scratch = Path.Combine(ScratchRoot(), $"reelbolt-staged-video-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        return scratch;
    }

    private void DeleteVideoScratch(string scratch)
    {
        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not clean up the staged-video scratch directory {Directory}", scratch);
        }
    }

    private static CachedVideoFacts PlainFacts(MediaProbeResult probe, long sourceSize, string reason) =>
        new(StagedVideoProxy.RecipeVersion, false, reason,
            probe.Width, probe.Height, probe.Width, probe.Height, probe.FpsNum, probe.FpsDen, sourceSize);

    private static StagedVideoFacts FactsOf(string sandboxPath, CachedVideoFacts facts, bool proxied) =>
        proxied
            ? new StagedVideoFacts(sandboxPath, facts.Width, facts.Height, facts.FpsNum, facts.FpsDen, true,
                facts.SourceWidth, facts.SourceHeight, facts.Bytes, Reason: facts.Reason)
            : new StagedVideoFacts(sandboxPath, facts.SourceWidth, facts.SourceHeight, facts.FpsNum, facts.FpsDen, false,
                facts.SourceWidth, facts.SourceHeight, null, Reason: facts.Reason);

    private static MediaProbeResult ProbeOf(CachedVideoFacts facts) =>
        new(0, facts.FpsNum, facts.FpsDen, facts.SourceWidth, facts.SourceHeight, null, null, null);

    /// <summary>ffprobe under the derivative demuxer whitelist: the source is an untrusted upload.</summary>
    private async Task<MediaProbeResult> ProbeUntrustedAsync(string path)
    {
        VideoToolResult probed = await _videoToolRunner!.RunFfprobeAsync(
            MediaDerivativeInputFormats.BuildSourceProbeArgs(path), TimeSpan.FromSeconds(60), CancellationToken.None);
        if (!probed.Succeeded || string.IsNullOrWhiteSpace(probed.StdOut))
            throw new InvalidOperationException($"ffprobe could not read the staged video (exit {probed.ExitCode}).");
        return FfprobeOutputParser.Parse(probed.StdOut);
    }

    private string ScratchRoot()
    {
        string? configured = _videoEditingOptions?.ScratchPath;
        return !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured) ? configured : Path.GetTempPath();
    }

    private async Task<string?> TryGetEtagAsync(string key)
    {
        try
        {
            GetObjectMetadataResponse? metadata = await _s3Client.GetObjectMetadataAsync(_bucketName, key, CancellationToken.None);
            return metadata?.ETag;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No ETag for {Key}; the staged-video cache keys on its size", key);
            return null;
        }
    }

    private async Task<CachedVideoFacts?> TryReadCachedFactsAsync(string key)
    {
        try
        {
            using GetObjectResponse? response = await _s3Client.GetObjectAsync(_bucketName, key, CancellationToken.None);
            if (response?.ResponseStream is null)
                return null;
            CachedVideoFacts? facts = await JsonSerializer.DeserializeAsync<CachedVideoFacts>(
                response.ResponseStream, ManifestJsonOptions, CancellationToken.None);
            return facts is { Recipe: StagedVideoProxy.RecipeVersion, SourceWidth: > 0, SourceHeight: > 0 } ? facts : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not Amazon.S3.AmazonS3Exception { StatusCode: HttpStatusCode.NotFound })
                _logger.LogDebug(ex, "Staged-video facts {Key} could not be read; probing the source", key);
            return null;
        }
    }

    private async Task TryPutJsonAsync(string key, CachedVideoFacts facts)
    {
        try
        {
            await _s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                ContentBody = JsonSerializer.Serialize(facts, ManifestJsonOptions),
                ContentType = "application/json"
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Staged-video facts {Key} could not be cached", key);
        }
    }

    /// <summary>The <c>video</c> block of a staging result: what was staged and, for a copy, a plain sentence.</summary>
    internal static Dictionary<string, object?> DescribeStagedVideo(StagedVideoFacts facts)
    {
        Dictionary<string, object?> video = new()
        {
            ["width"] = facts.Width,
            ["height"] = facts.Height,
            ["fps"] = FpsText(facts.FpsNum, facts.FpsDen),
            ["proxied"] = facts.Proxied
        };
        if (facts.Proxied)
        {
            video["sourceWidth"] = facts.SourceWidth;
            video["sourceHeight"] = facts.SourceHeight;
            video["stagedSizeBytes"] = facts.StagedBytes;
            video["note"] =
                $"The original is {facts.SourceWidth}x{facts.SourceHeight}, too large to play smoothly in the render, so a " +
                $"{facts.Width}x{facts.Height} copy with the same frames per second was staged under the same name. " +
                "Use the path exactly as returned; nothing in your code changes.";
        }
        return video;
    }

    private static double? FpsText(int num, int den) =>
        num > 0 && den > 0 ? Math.Round((double)num / den, 3) : null;

    // ──────────────────────────────────────────────
    // The staged-video manifest
    // ──────────────────────────────────────────────

    private async Task RecordStagedVideoAsync(ISandboxSession session, StagedVideoFacts facts)
    {
        try
        {
            Dictionary<string, StagedVideoFacts> videos = await ReadStagedVideoManifestAsync(session);
            videos[facts.Path] = facts;
            if (videos.Count > MaxManifestEntries)
            {
                foreach (string stale in videos.Keys.Where(k => k != facts.Path).Take(videos.Count - MaxManifestEntries).ToList())
                    videos.Remove(stale);
            }
            await session.WriteTextAsync(
                StagedVideosManifestPath,
                JsonSerializer.Serialize(new StagedVideoManifest(videos), ManifestJsonOptions),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record staged video {Path} for execution {ExecutionId}", facts.Path, session.ExecutionId);
        }
    }

    /// <summary>The recorded facts by sandbox path; empty when there is no manifest or it cannot be read.</summary>
    private static async Task<Dictionary<string, StagedVideoFacts>> ReadStagedVideoManifestAsync(ISandboxSession session)
    {
        try
        {
            SandboxTextFile file = await session.ReadTextAsync(StagedVideosManifestPath, CancellationToken.None);
            StagedVideoManifest? manifest = string.IsNullOrWhiteSpace(file?.Text)
                ? null
                : JsonSerializer.Deserialize<StagedVideoManifest>(file.Text, ManifestJsonOptions);
            return manifest?.Videos is { } videos
                ? new Dictionary<string, StagedVideoFacts>(videos, StringComparer.Ordinal)
                : new Dictionary<string, StagedVideoFacts>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return new Dictionary<string, StagedVideoFacts>(StringComparer.Ordinal);
        }
    }

    // ──────────────────────────────────────────────
    // Sizing the render's <OffthreadVideo> cache
    // ──────────────────────────────────────────────

    /// <summary>
    /// The cache for a render: enough for <see cref="OffthreadVideoFramesPerVideo"/> decoded frames of every
    /// staged video in every render tab (<paramref name="concurrency"/>), never below <paramref name="floor"/>
    /// (the configured default) and never above <paramref name="ceiling"/> (unless the floor is higher).
    /// </summary>
    internal static long SizeOffthreadVideoCache(IReadOnlyList<long> frameBytes, int concurrency, long floor, long ceiling)
    {
        if (frameBytes.Count == 0)
            return floor;
        // Doubles: the product of byte counts, tabs and frames can pass long.MaxValue, and only matters up to the cap.
        double need = 0;
        foreach (long bytes in frameBytes)
            need += Math.Max(0, bytes);
        need *= Math.Max(1, concurrency) * (double)OffthreadVideoFramesPerVideo;
        long cap = Math.Max(floor, ceiling);
        long sized = need >= cap ? cap : (long)need;
        return Math.Max(floor, sized);
    }

    /// <summary>
    /// The <c>--offthreadvideo-cache-size-in-bytes</c> a render whose args do not set it gets: sized from the
    /// videos under <c>public/</c> and their recorded facts (an unrecorded one by <see cref="FrameBytesFor"/>).
    /// The configured default when nothing is staged or anything here fails.
    /// </summary>
    private async Task<long> OffthreadVideoCacheForRenderAsync(ISandboxSession session, IReadOnlyList<string> args)
    {
        if (HasFlag(args, OffthreadVideoCacheFlag))
            return _offthreadVideoCacheBytes;
        try
        {
            IReadOnlyList<(string Path, long Size)> videos = await ListStagedVideosAsync(session);
            if (videos.Count == 0)
                return _offthreadVideoCacheBytes;

            Dictionary<string, StagedVideoFacts> facts = await ReadStagedVideoManifestAsync(session);
            List<long> frameBytes = [.. videos.Select(v => FrameBytesFor(facts.GetValueOrDefault(v.Path), v.Size))];

            int concurrency = FlagValue(args, ConcurrencyFlag) is { } text
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                    ? parsed
                    : _renderConcurrency;
            long sized = SizeOffthreadVideoCache(frameBytes, concurrency, _offthreadVideoCacheBytes, _offthreadVideoCacheMaxBytes);
            _logger.LogInformation(
                "OffthreadVideo cache for execution {ExecutionId}: {Bytes} bytes for {Count} staged video(s) at concurrency {Concurrency}",
                session.ExecutionId, sized, videos.Count, concurrency);
            return sized;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not size the OffthreadVideo cache for execution {ExecutionId}; using the default", session.ExecutionId);
            return _offthreadVideoCacheBytes;
        }
    }
}
