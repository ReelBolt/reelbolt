using ReelBolt.Shared.Editing.Color;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The stages a derivative production pass reports through
/// <see cref="IProgress{MediaDerivativeStage}"/>. The MassTransit event that carries this to the
/// GUI is T-302's job (and the contract is backend-owned) — this seam exists so that half has
/// something to publish without reaching into the production code.
/// </summary>
public enum MediaDerivativeStage
{
    Queued,
    Probing,
    Proxy,
    Filmstrip,
    Peaks,
    KeyframeIndex,
    Done,

    /// <summary>
    /// Declared for the cache-hit path T-302 owns: <see cref="MediaDerivativeService"/> itself
    /// does no caching (it is pure production) and never reports this stage.
    /// </summary>
    Skipped,

    /// <summary>Extracting full-size stills (T-101). Appended.</summary>
    Stills,

    /// <summary>Encoding the stabilised preview proxy. Appended.</summary>
    Stabilize
}

/// <summary>
/// Thrown when a derivative is <b>impossible for this source</b> — the source's own measured
/// properties cannot produce it at all — as opposed to having been attempted and failed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately its own type rather than <see cref="InvalidOperationException"/>.</b> The
/// dispatcher reports a <c>Failed</c> progress stage for every unexpected exception. A source that
/// is simply too short for a requested filmstrip density is not a failure: the pass was never
/// runnable, and reporting it as one made a short clip log a <c>Failed</c> stage on every single
/// request. A caller that catches this type reports a skip; anything else stays a failure.
/// </para>
/// <para>
/// <b>Thrown before any work is done.</b> The decision is taken from values already in hand — the
/// probe result — and before the first ffmpeg invocation, precisely so that "not attempted" is a
/// property a caller can assert on an invocation count.
/// </para>
/// </remarks>
public sealed class MediaDerivativeUnsupportedException(string message) : Exception(message);

/// <summary>
/// One produced local file, with the relative object leaf it belongs at. Uploading is NOT this
/// service's job — T-302 passes <see cref="FileName"/> to
/// <c>IProjectFileWorkspace.UploadArtifactAsync(projectId, LocalPath, FileName, ContentType, ct,
/// category: "agentFiles")</c>, which is also why <see cref="FileName"/> is a plain relative leaf:
/// <c>ProjectFilePath.NormalizeRelativePath</c> rejects <c>..</c> and would reject a full key.
/// </summary>
public sealed record MediaDerivativeArtifact(string LocalPath, string FileName, string ContentType);

/// <summary>The produced derivative: where its files are, what they are, and how to identify them.</summary>
/// <param name="StoragePrefix">
/// <c>projects/{projectId}/agentFiles/media-derivatives/{sourceHash}</c> — the directory every
/// artifact of this derivative belongs under. Join it with <see cref="MediaDerivativeArtifact.FileName"/>
/// to get the eventual storage key; the per-variant leaf carries the params hash, so two parameter
/// sets of one source land in sibling objects and never on the same key.
/// </param>
public sealed record MediaDerivativeResult(
    MediaDerivativeKindSlug Slug,
    int Variant,
    string ParamsJson,
    string CacheKey,
    string StoragePrefix,
    int Width,
    int Height,
    double DurationSec,
    long SizeBytes,
    IReadOnlyList<MediaDerivativeArtifact> Artifacts,
    string? SidecarJson);

/// <summary>
/// Produces the editor's four media derivatives — proxy, filmstrip sprite, waveform peaks and
/// keyframe index — from an <b>already-downloaded</b> local source.
/// </summary>
/// <remarks>
/// <para>
/// <b>Downloading and uploading are deliberately not this type's job.</b> The source arrives as a
/// local path (T-302 downloads it) and the artifacts leave as local paths (T-302 uploads them).
/// That keeps the whole pipeline runnable — and testable against a real ffmpeg — with no object
/// store in the loop, which is exactly what makes the argv and frame-shape assertions below real
/// encodes rather than mocks.
/// </para>
/// <para>
/// <b>Every invocation is scheduled at <see cref="VideoToolPriority.Low"/> and the gate is
/// released between them.</b> A derivative makes several ffmpeg/ffprobe calls in a row (sheet pass,
/// inventory pass, sheet probe) and must yield the concurrency slot between each one — holding it
/// across the whole run would make the priority rule decorative, since a compile queued a moment
/// after the derivative started would still wait for all of it.
/// </para>
/// <para>
/// <b>Trust boundary.</b> A caller supplies a slug and a variant. Every number that reaches argv is
/// either a first-party constant in <see cref="MediaDerivativeSpec"/> or a value this service read
/// from the source's own ffprobe result. Nothing here accepts a filter name, a filter string, a
/// resolution or a timestamp from outside.
/// </para>
/// </remarks>
public sealed class MediaDerivativeService(
    IVideoToolRunner runner,
    IMediaProbe probe,
    IOptions<VideoEditingOptions> options,
    ILogger<MediaDerivativeService> logger)
{
    private readonly IVideoToolRunner _runner = runner;
    private readonly IMediaProbe _probe = probe;
    private readonly VideoEditingOptions _options = options.Value;
    private readonly ILogger<MediaDerivativeService> _logger = logger;

    /// <summary>
    /// The subdirectory of <see cref="VideoEditingOptions.ScratchPath"/> derivatives are produced
    /// in. Distinct from the per-execution scratch the analysis and compile paths use, because
    /// these files OUTLIVE the call — T-302 uploads them afterwards and owns their cleanup.
    /// </summary>
    public const string ScratchSubdirectory = "media-derivatives";

    /// <summary>
    /// The encoding every JSON artifact is written in: UTF-8 WITHOUT a byte-order mark.
    /// <see cref="Encoding.UTF8"/> would prepend <c>EF BB BF</c>, which the API streams verbatim and
    /// which a browser's <c>JSON.parse</c> over the raw bytes rejects (d2 N11).
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The shortest source, in seconds, that can yield a filmstrip at
    /// <paramref name="densitySec"/>: <b>half</b> the density interval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured on this host, not assumed</b> (ffmpeg 8.0.1, one <c>fps=1/&lt;N&gt;</c> +
    /// <c>showinfo</c> pass per row, decoded-thumbnail count): <c>fps=1/5</c> emits its first
    /// thumbnail at a 2.5 s source and none at 2.4 s; <c>fps=1/30</c> emits its first at 15.0 s and
    /// none at 14.9 s. The decoded count is <c>round(durationSec / densitySec)</c> — a 29 s source
    /// yields ONE thumbnail at density 30, so it is not <c>floor</c> either. The threshold is
    /// therefore <b>not</b> "as long as the density", which is the arithmetic the routing note for
    /// this defect assumed (~30 s); the real cutoff is half of it.
    /// </para>
    /// <para>
    /// The rule belongs to ffmpeg's <c>fps</c> filter, which places each output frame on the input
    /// frame nearest its own output timestamp and drops an output slot that has no candidate within
    /// half an output interval.
    /// </para>
    /// </remarks>
    public static double MinimumSourceSecondsForFilmstripDensity(int densitySec) => densitySec / 2.0;

    /// <summary>
    /// Whether a source of the given <b>measured</b> duration can produce a filmstrip at
    /// <paramref name="densitySec"/> at all. False means the pass would emit zero frames — it must
    /// be skipped rather than attempted.
    /// </summary>
    /// <remarks>
    /// <b>A non-positive duration means "not measured", not "zero",</b> and counts as supported.
    /// <see cref="MediaProbeResult.DurationSec"/> is 0 when ffprobe reported no duration at all,
    /// and refusing on an absent measurement would be a guess — so the behaviour for such a source
    /// is exactly what it was before this predicate existed: attempt it.
    /// </remarks>
    public static bool SupportsFilmstripDensity(double durationSec, int densitySec) =>
        densitySec <= 0
        || durationSec <= 0
        || durationSec >= MinimumSourceSecondsForFilmstripDensity(densitySec);

    /// <summary>
    /// Probes <paramref name="localSourcePath"/> and builds the spec for
    /// <paramref name="slug"/>/<paramref name="variant"/> that describes THIS source. The proxy's
    /// target height, GOP length and audio expectation are functions of the source's own
    /// measurements, so they cannot be known before it is probed; the other three kinds are fully
    /// static and ignore the probe entirely. T-302 calls this, then passes the result to
    /// <see cref="ProduceAsync"/>.
    /// </summary>
    public async Task<MediaDerivativeSpec> ResolveSpecAsync(
        MediaDerivativeKindSlug slug, int variant, string localSourcePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localSourcePath);

        if (slug != MediaDerivativeKindSlug.Proxy)
            return BuildStaticSpec(slug, variant);

        MediaProbeResult source = await ProbeSourceAsync(localSourcePath, ct).ConfigureAwait(false);
        return MediaDerivativeSpec.ForProxy(
            source.Height, source.FpsNum, source.FpsDen, HasAudio(source), _options.FfmpegThreads, source.Width,
            ProfileVariantOf(variant, source));
    }

    /// <summary>
    /// Produces one derivative. Pure production — no cache read, no cache write, no upload.
    /// Deterministic: two runs with the same inputs and the same spec return the same
    /// <see cref="MediaDerivativeResult.CacheKey"/> and the same
    /// <see cref="MediaDerivativeResult.ParamsJson"/>.
    /// </summary>
    public async Task<MediaDerivativeResult> ProduceAsync(
        Guid projectId,
        string sourceStorageKey,
        string sourceEtag,
        MediaDerivativeSpec spec,
        string localSourcePath,
        IProgress<MediaDerivativeStage>? progress,
        CancellationToken ct,
        string? finishedProxyPath = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        // A finished proxy of THIS source (already SDR, timing kept) is read by the filmstrip and stills passes
        // instead of decoding the big original again. A missing file means: read the source, as before.
        string? proxyForReads = finishedProxyPath is not null && File.Exists(finishedProxyPath) ? finishedProxyPath : null;
        ArgumentException.ThrowIfNullOrWhiteSpace(localSourcePath);

        progress?.Report(MediaDerivativeStage.Queued);
        progress?.Report(MediaDerivativeStage.Probing);

        MediaProbeResult source = await ProbeSourceAsync(localSourcePath, ct).ConfigureAwait(false);

        // A proxy spec is a function of this exact source and these exact options. A mismatch means
        // the caller resolved the spec against a different file (or different options), which would
        // produce bytes that do not match their own cache key — the one failure this whole design
        // exists to prevent. Loud beats silently re-keying.
        if (spec.Slug == MediaDerivativeKindSlug.Proxy)
        {
            MediaDerivativeSpec expected = MediaDerivativeSpec.ForProxy(
                source.Height, source.FpsNum, source.FpsDen, HasAudio(source), _options.FfmpegThreads, source.Width,
                ProfileVariantOf(spec.Variant, source));

            if (!string.Equals(expected.ParamsJson, spec.ParamsJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Proxy derivative spec does not describe this source. Resolve it with " +
                    $"ResolveSpecAsync against '{localSourcePath}' before producing. " +
                    $"Expected {expected.ParamsJson} but got {spec.ParamsJson}.");
            }
        }

        string paramsHash = MediaDerivativeCacheKey.ComputeParamsHash(spec);
        string paramsHashShort = paramsHash[..16];
        string cacheKey = MediaDerivativeCacheKey.ComputeCacheKey(sourceStorageKey, sourceEtag, spec);
        string storagePrefix = MediaDerivativeCacheKey.ComputeStoragePrefix(projectId, sourceStorageKey, sourceEtag);
        string workDirectory = CreateWorkDirectory(projectId, sourceStorageKey, sourceEtag);

        _logger.LogDebug(
            "Producing {Slug} derivative variant {Variant} for {Source} in {Directory}",
            spec.Slug, spec.Variant, sourceStorageKey, workDirectory);

        MediaDerivativeResult result = spec.Slug switch
        {
            MediaDerivativeKindSlug.Proxy => await ProduceProxyAsync(
                spec, paramsHashShort, cacheKey, storagePrefix, localSourcePath, source, workDirectory, progress, ct)
                .ConfigureAwait(false),

            MediaDerivativeKindSlug.Filmstrip => await ProduceFilmstripAsync(
                spec, paramsHashShort, cacheKey, storagePrefix, localSourcePath, source, workDirectory, progress, ct, proxyForReads)
                .ConfigureAwait(false),

            MediaDerivativeKindSlug.WaveformPeaks => await ProducePeaksAsync(
                spec, paramsHashShort, cacheKey, storagePrefix, localSourcePath, source, workDirectory, progress, ct)
                .ConfigureAwait(false),

            MediaDerivativeKindSlug.KeyframeIndex => await ProduceKeyframeIndexAsync(
                spec, paramsHashShort, cacheKey, storagePrefix, localSourcePath, source, workDirectory, progress, ct)
                .ConfigureAwait(false),

            MediaDerivativeKindSlug.Stills => await ProduceStillsAsync(
                spec, paramsHashShort, cacheKey, storagePrefix, localSourcePath, source, workDirectory, progress, ct, proxyForReads)
                .ConfigureAwait(false),

            MediaDerivativeKindSlug.StabilizedProxy => await ProduceStabilizedProxyAsync(
                spec, paramsHashShort, cacheKey, storagePrefix, localSourcePath, source, workDirectory, progress, ct)
                .ConfigureAwait(false),

            _ => throw new NotSupportedException($"Unsupported derivative slug '{spec.Slug}'.")
        };

        progress?.Report(MediaDerivativeStage.Done);
        return result;
    }

    /// <summary>The refusal code for a converted proxy asked of an HDR source (HDR keeps its own tone-map).</summary>
    public const string CameraProfileHdrRefusal = "camera_profile_not_applicable_hdr";

    /// <summary>
    /// The proxy variant to build the spec with: 0 stays plain. An unknown variant, or a converted proxy of an HDR
    /// source, is a refusal, never a quiet plain proxy.
    /// </summary>
    private static int ProfileVariantOf(int variant, MediaProbeResult source)
    {
        if (variant == 0) return 0;
        if (!CameraProfileVariants.TryProfileOf(variant, out _))
            throw new InvalidOperationException($"Unknown proxy variant {variant}.");
        if (HdrToneMap.KindOf(source) is not null)
            throw new InvalidOperationException(CameraProfileHdrRefusal);
        return variant;
    }

    private Task<bool>? _zscaleAvailable;

    private Task<bool> ZscaleAvailableAsync(CancellationToken ct) =>
        _zscaleAvailable is { IsFaulted: false, IsCanceled: false } cached ? cached : _zscaleAvailable = ProbeZscaleAsync(ct);

    private async Task<bool> ProbeZscaleAsync(CancellationToken ct)
    {
        VideoToolResult result = await _runner.RunFfmpegAsync(["-hide_banner", "-filters"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return result.Succeeded && result.StdOut.Split('\n').Any(line =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts && parts[1] == "zscale");
    }

    /// <summary>The tone-map kind for an HDR source when this host's ffmpeg can tone-map; null otherwise (argv unchanged).</summary>
    private async Task<string?> HdrKindAsync(MediaProbeResult source, CancellationToken ct) =>
        HdrToneMap.KindOf(source) is { } kind && await FfmpegHdrToneMapCapability.Shared.IsAvailableAsync(_runner, ct).ConfigureAwait(false)
            ? kind
            : null;

    // -----------------------------------------------------------------------------------------
    // Proxy
    // -----------------------------------------------------------------------------------------

    private async Task<MediaDerivativeResult> ProduceProxyAsync(
        MediaDerivativeSpec spec,
        string paramsHashShort,
        string cacheKey,
        string storagePrefix,
        string localSourcePath,
        MediaProbeResult source,
        string workDirectory,
        IProgress<MediaDerivativeStage>? progress,
        CancellationToken ct)
    {
        progress?.Report(MediaDerivativeStage.Proxy);

        bool hasAudio = HasAudio(source);
        bool portrait = MediaDerivativeSpec.IsPortraitProxy(source.Width, source.Height);
        int targetHeight = MediaDerivativeSpec.ProxyTargetSide(source.Width, source.Height);
        int gop = MediaDerivativeSpec.ComputeProxyGopFrames(source.FpsNum, source.FpsDen);

        string outputPath = Path.Combine(workDirectory, "proxy.mp4");
        string? profileId = CameraProfileVariants.TryProfileOf(ProfileVariantOf(spec.Variant, source), out string? profile) ? profile : null;
        string? hdrKind = profileId is null ? await HdrKindAsync(source, ct).ConfigureAwait(false) : null;
        string? cubePath = null;
        bool zscale = true;
        if (profileId is not null)
        {
            // The table is generated here from the closed catalogue and written to the job's own scratch
            // directory: ffmpeg only ever reads this file, never a user's bytes.
            cubePath = Path.Combine(workDirectory, "camera-profile.cube");
            await File.WriteAllTextAsync(cubePath, CameraProfileCube.Write(profileId), ct).ConfigureAwait(false);
            zscale = await ZscaleAvailableAsync(ct).ConfigureAwait(false);
        }

        string[] args = ProxyArgvBuilder.Build(
            localSourcePath, outputPath, targetHeight, gop, hasAudio, DerivativeThreads(), portrait, hdrKind,
            profileId, cubePath, zscale);

        await RunFfmpegAsync(args, "proxy encode", ct).ConfigureAwait(false);

        MediaProbeResult produced = await _probe.ProbeAsync(outputPath, ct).ConfigureAwait(false);

        var artifacts = new List<MediaDerivativeArtifact>
        {
            new(outputPath, $"proxy-{paramsHashShort}.mp4", "video/mp4")
        };

        return new MediaDerivativeResult(
            spec.Slug,
            spec.Variant,
            spec.ParamsJson,
            cacheKey,
            storagePrefix,
            produced.Width,
            produced.Height,
            source.DurationSec,
            TotalBytes(artifacts),
            artifacts,
            SidecarJson: null);
    }

    // -----------------------------------------------------------------------------------------
    // Stabilised preview proxy
    // -----------------------------------------------------------------------------------------

    private async Task<MediaDerivativeResult> ProduceStabilizedProxyAsync(
        MediaDerivativeSpec spec,
        string paramsHashShort,
        string cacheKey,
        string storagePrefix,
        string localSourcePath,
        MediaProbeResult source,
        string workDirectory,
        IProgress<MediaDerivativeStage>? progress,
        CancellationToken ct)
    {
        progress?.Report(MediaDerivativeStage.Stabilize);

        bool portrait = MediaDerivativeSpec.IsPortraitProxy(source.Width, source.Height);
        int targetHeight = MediaDerivativeSpec.ProxyTargetSide(source.Width, source.Height);
        int gop = MediaDerivativeSpec.ComputeProxyGopFrames(source.FpsNum, source.FpsDen);
        string? hdrKind = await HdrKindAsync(source, ct).ConfigureAwait(false);

        string trfPath = Path.Combine(workDirectory, "stabilize.trf");
        string outputPath = Path.Combine(workDirectory, "stabilized-proxy.mp4");

        await RunFfmpegAsync(
            StabilizedProxyArgvBuilder.BuildDetect(localSourcePath, trfPath, targetHeight, portrait, DerivativeThreads()),
            "stabilise analysis", ct).ConfigureAwait(false);

        await RunFfmpegAsync(
            StabilizedProxyArgvBuilder.BuildEncode(localSourcePath, trfPath, outputPath, targetHeight, gop, DerivativeThreads(), portrait, hdrKind),
            "stabilised proxy encode", ct).ConfigureAwait(false);

        MediaProbeResult produced = await _probe.ProbeAsync(outputPath, ct).ConfigureAwait(false);

        var artifacts = new List<MediaDerivativeArtifact>
        {
            new(outputPath, $"stabilized-proxy-{paramsHashShort}.mp4", "video/mp4")
        };

        return new MediaDerivativeResult(
            spec.Slug,
            spec.Variant,
            spec.ParamsJson,
            cacheKey,
            storagePrefix,
            produced.Width,
            produced.Height,
            source.DurationSec,
            TotalBytes(artifacts),
            artifacts,
            SidecarJson: null);
    }

    // -----------------------------------------------------------------------------------------
    // Filmstrip
    // -----------------------------------------------------------------------------------------

    private async Task<MediaDerivativeResult> ProduceFilmstripAsync(
        MediaDerivativeSpec spec,
        string paramsHashShort,
        string cacheKey,
        string storagePrefix,
        string localSourcePath,
        MediaProbeResult source,
        string workDirectory,
        IProgress<MediaDerivativeStage>? progress,
        CancellationToken ct,
        string? proxyPath = null)
    {
        int densitySec = MediaDerivativeSpec.FilmstripDensitySeconds[spec.Variant];

        // Decided from the source's OWN measurement, BEFORE any ffmpeg call: below half the density
        // interval fps=1/<density> emits no frame at all, so the sheet pass would write nothing, the
        // inventory pass would decode nothing, and this service would throw on every request for the
        // same source — forever, since no row is ever written to stop it being asked again. Measured
        // boundary in MinimumSourceSecondsForFilmstripDensity.
        if (!SupportsFilmstripDensity(source.DurationSec, densitySec))
        {
            double minimumSec = MinimumSourceSecondsForFilmstripDensity(densitySec);

            _logger.LogInformation(
                "Skipping filmstrip density {DensitySec}s for '{SourcePath}': the source measures " +
                "{DurationSec:0.###}s, and fps=1/{DensitySec} emits no frame below {MinimumSec:0.###}s. " +
                "No ffmpeg pass was run for this density.",
                densitySec,
                localSourcePath,
                source.DurationSec,
                densitySec,
                minimumSec);

            throw new MediaDerivativeUnsupportedException(
                $"Filmstrip density {densitySec}s is unsupported by the source's own duration: " +
                $"fps=1/{densitySec} emits no frame for a {source.DurationSec:0.###}s source and needs at " +
                $"least {minimumSec:0.###}s. No ffmpeg pass was run.");
        }

        progress?.Report(MediaDerivativeStage.Filmstrip);

        // ONE ffmpeg run serves every density the source supports (sheets AND inventories): the
        // three variants of a source are produced back to back, and each used to decode the whole
        // file twice. The first variant to arrive runs the shared pass and parks the sheets and
        // thumbnail counts of all densities in memory; the others take their own from it.
        string inputPath = proxyPath ?? localSourcePath;
        int[] supportedVariants = Enumerable.Range(0, MediaDerivativeSpec.FilmstripDensitySeconds.Length)
            .Where(v => SupportsFilmstripDensity(source.DurationSec, MediaDerivativeSpec.FilmstripDensitySeconds[v]))
            .ToArray();
        string memoKey = PassMemoKey(inputPath, proxyPath is not null ? "filmstrip-proxy" : "filmstrip-source");

        FilmstripBatch batch = await _filmstripBatches.GetAsync(
            memoKey,
            spec.Variant,
            supportedVariants,
            async token => await RunFilmstripBatchAsync(
                inputPath,
                supportedVariants.Select(v => MediaDerivativeSpec.FilmstripDensitySeconds[v]).ToArray(),
                proxyPath is not null ? null : await HdrKindAsync(source, token).ConfigureAwait(false),
                keyframesOnly: proxyPath is not null,
                workDirectory,
                token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        FilmstripDensityResult measuredDensity = batch.ByDensity[densitySec];

        // Ground truth for the sheet count is what ffmpeg actually WROTE, not arithmetic.
        if (measuredDensity.Sheets.Count == 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg filmstrip pass produced no sheets in '{workDirectory}' for '{localSourcePath}'.");
        }

        for (int i = 0; i < measuredDensity.Sheets.Count; i++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(workDirectory, $"sheet-{(i + 1).ToString("000", CultureInfo.InvariantCulture)}.jpg"),
                measuredDensity.Sheets[i],
                ct).ConfigureAwait(false);
        }

        List<string> sheetPaths = EnumerateSheets(workDirectory);

        // The decoded thumbnail count - the number the `fps=1/<N>` filter actually emitted - comes
        // from the showinfo inventory branch of the SAME pass, so it describes exactly the frames
        // that went into the sheets. See FilmstripArgvBuilder.BuildMultiDensity.
        int totalThumbs = measuredDensity.ThumbCount;
        if (totalThumbs == 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg filmstrip inventory reported no decoded thumbnails for '{localSourcePath}' " +
                $"(density {densitySec}s); refusing to emit a " +
                $"filmstrip index that would mis-map every hover position.");
        }

        IReadOnlyList<int> perSheetCounts = DistributeThumbs(totalThumbs, sheetPaths.Count);

        // The tile filter pads the final partial sheet to the FULL 10x10 area with black, so every
        // sheet is cols*thumbWidth by rows*thumbHeight regardless of how many thumbnails it holds.
        // That is what lets the thumb box be measured from any single sheet.
        MediaProbeResult firstSheet = await _probe.ProbeAsync(sheetPaths[0], ct).ConfigureAwait(false);
        int thumbWidth = Math.Max(1, firstSheet.Width / FilmstripIndex.Cols);
        int thumbHeight = Math.Max(1, firstSheet.Height / FilmstripIndex.Rows);

        var measured = new List<FilmstripSheetFrames>(sheetPaths.Count);
        for (int i = 0; i < sheetPaths.Count; i++)
            measured.Add(new FilmstripSheetFrames(FilmstripIndex.SheetLeaf(paramsHashShort, i + 1), perSheetCounts[i]));

        IReadOnlyList<FilmstripSheet> sheets = FilmstripIndex.Plan(densitySec, measured);
        string indexJson = FilmstripIndex.Build(densitySec, thumbWidth, thumbHeight, sheets);

        var artifacts = new List<MediaDerivativeArtifact>(sheetPaths.Count + 1);
        for (int i = 0; i < sheetPaths.Count; i++)
            artifacts.Add(new MediaDerivativeArtifact(sheetPaths[i], sheets[i].StorageLeaf, "image/jpeg"));

        string indexPath = Path.Combine(workDirectory, FilmstripIndex.IndexLeaf(paramsHashShort));
        await File.WriteAllTextAsync(indexPath, indexJson, Utf8NoBom, ct).ConfigureAwait(false);
        artifacts.Add(new MediaDerivativeArtifact(indexPath, FilmstripIndex.IndexLeaf(paramsHashShort), "application/json"));

        _logger.LogInformation(
            "Filmstrip derivative: density {DensitySec}s, {ThumbCount} thumbnails across {SheetCount} sheets " +
            "({ThumbWidth}x{ThumbHeight} thumbs)",
            densitySec, totalThumbs, sheetPaths.Count, thumbWidth, thumbHeight);

        return new MediaDerivativeResult(
            spec.Slug,
            spec.Variant,
            spec.ParamsJson,
            cacheKey,
            storagePrefix,
            thumbWidth,
            thumbHeight,
            source.DurationSec,
            TotalBytes(artifacts),
            artifacts,
            indexJson);
    }

    // -----------------------------------------------------------------------------------------
    // Waveform peaks
    // -----------------------------------------------------------------------------------------

    private async Task<MediaDerivativeResult> ProducePeaksAsync(
        MediaDerivativeSpec spec,
        string paramsHashShort,
        string cacheKey,
        string storagePrefix,
        string localSourcePath,
        MediaProbeResult source,
        string workDirectory,
        IProgress<MediaDerivativeStage>? progress,
        CancellationToken ct)
    {
        // A source with no audio stream has no peaks to draw. Decoding it anyway is what made every
        // silent video log three failed peaks runs (ffmpeg: "Output file does not contain any
        // stream") on every upload and every editor open (RD-10). Unsupported, not failed: nothing
        // was attempted, and the dispatcher reports it as a skip.
        if (!HasAudio(source))
        {
            throw new MediaDerivativeUnsupportedException(
                "The source has no audio stream, so it has no waveform peaks.");
        }

        progress?.Report(MediaDerivativeStage.Peaks);

        // ONE decode serves all three LOD variants of a source: each variant's artifact set is
        // every LOD plus the sidecar, derived in-process from the same PCM. The first variant
        // decodes and parks the serialized bytes; the others reuse them (byte-identical to a
        // fresh decode - the reduction is a pure function of the PCM).
        PeaksBatch batch = await _peaksBatches.GetAsync(
            PassMemoKey(localSourcePath, "peaks"),
            spec.Variant,
            Enumerable.Range(0, MediaDerivativeSpec.PeaksLodMs.Length),
            token => DecodePeaksAsync(localSourcePath, workDirectory, token),
            ct).ConfigureAwait(false);

        var artifacts = new List<MediaDerivativeArtifact>(batch.Lods.Count + 1);
        foreach ((int lodMs, byte[] bytes) in batch.Lods)
        {
            string leaf = PcmPeakSampler.LodLeaf(paramsHashShort, lodMs);
            string lodPath = Path.Combine(workDirectory, leaf);
            await File.WriteAllBytesAsync(lodPath, bytes, ct).ConfigureAwait(false);
            artifacts.Add(new MediaDerivativeArtifact(lodPath, leaf, "application/octet-stream"));
        }

        string indexJson = batch.SidecarJson;
        string indexPath = Path.Combine(workDirectory, PcmPeakSampler.IndexLeaf(paramsHashShort));
        await File.WriteAllTextAsync(indexPath, indexJson, Utf8NoBom, ct).ConfigureAwait(false);
        artifacts.Add(new MediaDerivativeArtifact(indexPath, PcmPeakSampler.IndexLeaf(paramsHashShort), "application/json"));

        return new MediaDerivativeResult(
            spec.Slug,
            spec.Variant,
            spec.ParamsJson,
            cacheKey,
            storagePrefix,
            0,
            0,
            source.DurationSec,
            TotalBytes(artifacts),
            artifacts,
            indexJson);
    }

    private sealed record FilmstripDensityResult(IReadOnlyList<byte[]> Sheets, int ThumbCount);

    private sealed record FilmstripBatch(IReadOnlyDictionary<int, FilmstripDensityResult> ByDensity);

    private sealed record PeaksBatch(IReadOnlyList<(int LodMs, byte[] Bytes)> Lods, string SidecarJson);

    private readonly SourcePassMemo<FilmstripBatch> _filmstripBatches = new(capacity: 2);
    private readonly SourcePassMemo<PeaksBatch> _peaksBatches = new(capacity: 4);

    /// <summary>Identifies one input file's content for a shared pass: path, size and write time.</summary>
    private static string PassMemoKey(string path, string pass)
    {
        string full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        long length = info.Exists ? info.Length : -1;
        long ticks = info.Exists ? info.LastWriteTimeUtc.Ticks : -1;
        return string.Create(CultureInfo.InvariantCulture, $"{pass}|{full}|{length}|{ticks}");
    }

    /// <summary>
    /// Runs the one shared filmstrip pass for every density and returns each density's sheets (in
    /// memory) and measured thumbnail count. Runs in a scratch subdirectory of the first caller's
    /// work directory, deleted before returning, so nothing is left behind for another variant.
    /// </summary>
    private async Task<FilmstripBatch> RunFilmstripBatchAsync(
        string inputPath,
        int[] densities,
        string? hdrKind,
        bool keyframesOnly,
        string workDirectory,
        CancellationToken ct)
    {
        string scratch = Path.Combine(workDirectory, "filmstrip-pass");
        Directory.CreateDirectory(scratch);
        try
        {
            var outputs = new List<(int DensitySec, string OutputPatternPath)>(densities.Length);
            foreach (int density in densities)
            {
                string dir = Path.Combine(scratch, $"d{density.ToString(CultureInfo.InvariantCulture)}");
                Directory.CreateDirectory(dir);
                outputs.Add((density, Path.Combine(dir, FilmstripArgvBuilder.SheetPattern)));
            }

            VideoToolResult result = await _runner.RunFfmpegAsync(
                FilmstripArgvBuilder.BuildMultiDensity(inputPath, outputs, hdrKind, keyframesOnly),
                Timeout,
                ct,
                VideoToolPriority.Low).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "ffmpeg filmstrip pass failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                    result.ExitCode, result.TimedOut, Tail(result.StdErr));

                throw new InvalidOperationException(
                    $"ffmpeg filmstrip pass failed (exitCode={result.ExitCode}, timedOut={result.TimedOut}). {Tail(result.StdErr)}");
            }

            string[] errLines = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var byDensity = new Dictionary<int, FilmstripDensityResult>(densities.Length);
            foreach (int density in densities)
            {
                string dir = Path.Combine(scratch, $"d{density.ToString(CultureInfo.InvariantCulture)}");
                string prefix = FilmstripArgvBuilder.InventoryLinePrefix(density);
                string densityLog = string.Join('\n', errLines.Where(l => l.Contains(prefix, StringComparison.Ordinal)));
                int thumbs = ShowinfoOutputParser.ParsePtsTimes(densityLog).Count;

                var sheets = new List<byte[]>();
                foreach (string sheet in EnumerateSheets(dir))
                    sheets.Add(await File.ReadAllBytesAsync(sheet, ct).ConfigureAwait(false));

                byDensity[density] = new FilmstripDensityResult(sheets, thumbs);
            }

            return new FilmstripBatch(byDensity);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not delete the filmstrip pass scratch directory {Directory}", scratch);
            }
        }
    }

    /// <summary>
    /// Shares ONE expensive pass over a source between the sibling variants that need it. Keyed by
    /// the input file's identity; an entry is dropped once every expected variant has taken its
    /// share, when its pass fails, and (oldest first) beyond <c>capacity</c> entries, so abandoned
    /// work cannot pile up in memory.
    /// </summary>
    private sealed class SourcePassMemo<T>(int capacity) where T : class
    {
        private sealed class Entry(Lazy<Task<T>> task, IEnumerable<int> expected, long sequence)
        {
            public Lazy<Task<T>> Task { get; } = task;
            public HashSet<int> Pending { get; } = [.. expected];
            public long Sequence { get; } = sequence;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private long _sequence;

        public async Task<T> GetAsync(
            string key,
            int variant,
            IEnumerable<int> expectedVariants,
            Func<CancellationToken, Task<T>> factory,
            CancellationToken ct)
        {
            while (true)
            {
                Entry entry;
                lock (_gate)
                {
                    if (!_entries.TryGetValue(key, out Entry? existing))
                    {
                        existing = new Entry(
                            new Lazy<Task<T>>(() => factory(ct), LazyThreadSafetyMode.ExecutionAndPublication),
                            expectedVariants,
                            ++_sequence);
                        _entries[key] = existing;

                        while (_entries.Count > capacity)
                        {
                            string oldest = _entries.Where(kv => !ReferenceEquals(kv.Value, existing))
                                .OrderBy(kv => kv.Value.Sequence).First().Key;
                            _entries.Remove(oldest);
                        }
                    }

                    entry = existing;
                }

                try
                {
                    T value = await entry.Task.Value.WaitAsync(ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        entry.Pending.Remove(variant);
                        if (entry.Pending.Count == 0) Drop(key, entry);
                    }

                    return value;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // The pass belonged to another caller whose token was cancelled; run it again for us.
                    lock (_gate) Drop(key, entry);
                }
                catch
                {
                    lock (_gate) Drop(key, entry);
                    throw;
                }
            }
        }

        private void Drop(string key, Entry entry)
        {
            if (_entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry))
                _entries.Remove(key);
        }
    }

    private async Task<PeaksBatch> DecodePeaksAsync(string localSourcePath, string workDirectory, CancellationToken ct)
    {
        string rawPath = Path.Combine(workDirectory, "peaks.raw");

        // Decoded to a RAW FILE on purpose. VideoToolResult.StdOut is captured as TEXT LINES by the
        // runner (it is a StreamReader over the redirected pipe), so piping s16le PCM through it
        // would mangle every byte that is not valid text - 0x0A and 0x0D in particular would be
        // split into "lines" and any other high byte re-encoded. Do not "optimize" this back into
        // a pipe.
        await RunFfmpegAsync(
            BuildPeaksDecodeArgs(localSourcePath, rawPath),
            "waveform peaks decode",
            ct).ConfigureAwait(false);

        byte[] rawPcm = await File.ReadAllBytesAsync(rawPath, ct).ConfigureAwait(false);
        PcmPeakResult peaks = PcmPeakSampler.Sample(rawPcm, MediaDerivativeSpec.PeaksSampleRate, MediaDerivativeSpec.PeaksChannels);
        if (peaks.Lods.Count == 0)
        {
            throw new InvalidOperationException(
                $"Decoded PCM for '{localSourcePath}' was empty or malformed " +
                $"({rawPcm.Length} bytes); no peaks could be sampled.");
        }

        return new PeaksBatch(
            peaks.Lods.Select(l => (l.LodMs, PcmPeakSampler.SerializeLod(l))).ToArray(),
            PcmPeakSampler.BuildSidecarJson(peaks));
    }

    /// <summary>
    /// The peaks decode argv. Lives here rather than in a dedicated builder because
    /// <see cref="PcmPeakSampler"/> is deliberately ffmpeg-free (it parses bytes already on disk),
    /// and this is the only invocation the peaks path makes.
    /// <see cref="MediaDerivativeSpec.PeaksChannels"/>/<see cref="MediaDerivativeSpec.PeaksSampleRate"/>
    /// are fixed constants, so the byte layout one spec describes is independent of the source's
    /// own channel layout.
    /// </summary>
    internal static string[] BuildPeaksDecodeArgs(string inputPath, string outputRawPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRawPath);

        return
        [
            "-y",
            "-nostdin",
            "-hide_banner",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", inputPath,
            "-vn",
            "-ac", FfmpegArgvFormat.Number(MediaDerivativeSpec.PeaksChannels),
            "-ar", FfmpegArgvFormat.Number(MediaDerivativeSpec.PeaksSampleRate),
            "-sample_fmt", "s16",
            "-f", "s16le",
            outputRawPath
        ];
    }

    // -----------------------------------------------------------------------------------------
    // Keyframe index
    // -----------------------------------------------------------------------------------------

    private async Task<MediaDerivativeResult> ProduceKeyframeIndexAsync(
        MediaDerivativeSpec spec,
        string paramsHashShort,
        string cacheKey,
        string storagePrefix,
        string localSourcePath,
        MediaProbeResult source,
        string workDirectory,
        IProgress<MediaDerivativeStage>? progress,
        CancellationToken ct)
    {
        progress?.Report(MediaDerivativeStage.KeyframeIndex);

        VideoToolResult probeResult = await _runner.RunFfprobeAsync(
            KeyframeIndexArgvBuilder.BuildProbeArgs(localSourcePath),
            Timeout,
            ct,
            VideoToolPriority.Low).ConfigureAwait(false);

        if (!probeResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"ffprobe keyframe index failed for '{localSourcePath}' " +
                $"(exitCode={probeResult.ExitCode}, timedOut={probeResult.TimedOut}). {Tail(probeResult.StdErr)}");
        }

        IReadOnlyList<double> times = KeyframeIndexArgvBuilder.ParseKeyframeTimes(probeResult.StdOut);
        double gopHintSec = KeyframeIndexArgvBuilder.ComputeGopHintSec(times);
        string indexJson = KeyframeIndexArgvBuilder.BuildIndexJson(times, gopHintSec);

        string indexPath = Path.Combine(workDirectory, KeyframeIndexArgvBuilder.IndexLeaf(paramsHashShort));
        await File.WriteAllTextAsync(indexPath, indexJson, Utf8NoBom, ct).ConfigureAwait(false);

        var artifacts = new List<MediaDerivativeArtifact>
        {
            new(indexPath, KeyframeIndexArgvBuilder.IndexLeaf(paramsHashShort), "application/json")
        };

        return new MediaDerivativeResult(
            spec.Slug,
            spec.Variant,
            spec.ParamsJson,
            cacheKey,
            storagePrefix,
            0,
            0,
            source.DurationSec,
            TotalBytes(artifacts),
            artifacts,
            indexJson);
    }

    // -----------------------------------------------------------------------------------------
    // Stills
    // -----------------------------------------------------------------------------------------

    private async Task<MediaDerivativeResult> ProduceStillsAsync(
        MediaDerivativeSpec spec,
        string paramsHashShort,
        string cacheKey,
        string storagePrefix,
        string localSourcePath,
        MediaProbeResult source,
        string workDirectory,
        IProgress<MediaDerivativeStage>? progress,
        CancellationToken ct,
        string? proxyPath = null)
    {
        progress?.Report(MediaDerivativeStage.Stills);

        // The times come from the spec (server-normalized whole milliseconds) clamped to the source's
        // OWN measured duration; nothing else reaches argv.
        IReadOnlyList<int> times = StillsArgvBuilder.PlanTimesMs(source.DurationSec, MediaDerivativeSpec.StillTimesMsOf(spec));

        var stills = new List<(int Index, int AtMs, string Leaf)>(times.Count);
        var artifacts = new List<MediaDerivativeArtifact>(times.Count + 1);
        string? stillsHdrKind = proxyPath is not null ? null : await HdrKindAsync(source, ct).ConfigureAwait(false);
        foreach (int atMs in times)
        {
            int index = stills.Count;
            string leaf = StillsArgvBuilder.StillLeaf(paramsHashShort, index);
            string path = Path.Combine(workDirectory, leaf);

            VideoToolResult result = await _runner.RunFfmpegAsync(
                StillsArgvBuilder.Build(proxyPath ?? localSourcePath, path, atMs, stillsHdrKind), Timeout, ct, VideoToolPriority.Low).ConfigureAwait(false);

            // A seek that lands past the last decodable frame writes nothing; that one still is skipped
            // rather than failing a set whose other frames are fine.
            if (!result.Succeeded || !File.Exists(path) || new FileInfo(path).Length == 0)
            {
                _logger.LogInformation(
                    "Still at {AtMs} ms produced no frame for '{SourcePath}' (exitCode={ExitCode}); skipped",
                    atMs, localSourcePath, result.ExitCode);
                continue;
            }

            stills.Add((index, atMs, leaf));
            artifacts.Add(new MediaDerivativeArtifact(path, leaf, "image/jpeg"));
        }

        if (stills.Count == 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg produced no still for '{localSourcePath}' at any of {times.Count} times.");
        }

        MediaProbeResult first = await _probe.ProbeAsync(artifacts[0].LocalPath, ct).ConfigureAwait(false);

        string indexJson = StillsArgvBuilder.BuildIndexJson(stills);
        string indexLeaf = StillsArgvBuilder.IndexLeaf(paramsHashShort);
        string indexPath = Path.Combine(workDirectory, indexLeaf);
        await File.WriteAllTextAsync(indexPath, indexJson, Encoding.UTF8, ct).ConfigureAwait(false);
        artifacts.Add(new MediaDerivativeArtifact(indexPath, indexLeaf, "application/json"));

        return new MediaDerivativeResult(
            spec.Slug,
            spec.Variant,
            spec.ParamsJson,
            cacheKey,
            storagePrefix,
            first.Width,
            first.Height,
            source.DurationSec,
            TotalBytes(artifacts),
            artifacts,
            indexJson);
    }

    // -----------------------------------------------------------------------------------------

    private static MediaDerivativeSpec BuildStaticSpec(MediaDerivativeKindSlug slug, int variant) => slug switch
    {
        MediaDerivativeKindSlug.Filmstrip => MediaDerivativeSpec.ForFilmstrip(variant),
        MediaDerivativeKindSlug.WaveformPeaks => MediaDerivativeSpec.ForWaveformPeaks(variant),
        MediaDerivativeKindSlug.KeyframeIndex => MediaDerivativeSpec.ForKeyframeIndex(),
        _ => throw new NotSupportedException(
            $"'{slug}' is not a static derivative kind — resolve it against a source.")
    };

    /// <summary>
    /// A derivative's source carries audio only if ffprobe reported an audio codec. The proxy's
    /// AAC arguments are dropped when it does not — never emitted against a stream that is not
    /// there.
    /// </summary>
    private static bool HasAudio(MediaProbeResult source) => !string.IsNullOrWhiteSpace(source.AudioCodec);

    private static List<string> EnumerateSheets(string workDirectory)
    {
        var sheets = new List<string>(Directory.EnumerateFiles(workDirectory, "sheet-*.jpg"));
        sheets.Sort(StringComparer.Ordinal);
        return sheets;
    }

    /// <summary>
    /// Spreads <paramref name="totalThumbs"/> measured thumbnails over
    /// <paramref name="sheetCount"/> measured sheets: every sheet but the last is full (the tile
    /// filter emits on each completed 10×10 block) and the remainder lands on the last one. A
    /// distribution that does not add up means the two measurements disagree, which would make the
    /// index wrong — so it throws instead of rounding.
    /// </summary>
    internal static IReadOnlyList<int> DistributeThumbs(int totalThumbs, int sheetCount)
    {
        if (sheetCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sheetCount), sheetCount, "A filmstrip must have at least one sheet.");

        var counts = new int[sheetCount];
        int remaining = totalThumbs;

        for (int i = 0; i < sheetCount; i++)
        {
            counts[i] = Math.Min(FilmstripIndex.ThumbsPerSheet, Math.Max(0, remaining));
            remaining -= counts[i];
        }

        if (remaining > 0 || counts[^1] <= 0)
        {
            throw new InvalidOperationException(
                $"Filmstrip sheet/thumbnail measurements disagree: {totalThumbs} decoded thumbnails do not " +
                $"distribute over {sheetCount} sheets of at most {FilmstripIndex.ThumbsPerSheet} each.");
        }

        return counts;
    }

    private string CreateWorkDirectory(Guid projectId, string sourceStorageKey, string sourceEtag)
    {
        string root = Path.GetFullPath(_options.ScratchPath);
        string sourceHash = MediaDerivativeCacheKey.ComputeSourceHash(sourceStorageKey, sourceEtag);
        string directory = Path.GetFullPath(Path.Combine(
            root, ScratchSubdirectory, projectId.ToString("D"), $"{sourceHash}-{Guid.NewGuid():N}"));

        // Same containment assertion the rest of the feature uses: nothing this service writes may
        // land outside the scratch root, whatever a caller passed as a storage key.
        VideoScratchSpace.ValidateScratchScope(root, directory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Probes the untrusted source with the derivative demuxer whitelist (security MI-2). Not through
    /// <see cref="IMediaProbe"/>: its argv has no <c>-format_whitelist</c>, because it also probes
    /// trusted engine output (this service's own proxy and sheets still go through it).
    /// </summary>
    private async Task<MediaProbeResult> ProbeSourceAsync(string localSourcePath, CancellationToken ct)
    {
        VideoToolResult result = await _runner.RunFfprobeAsync(
            MediaDerivativeInputFormats.BuildSourceProbeArgs(localSourcePath),
            SourceTimeoutPolicy.Analyze(_options),
            ct,
            VideoToolPriority.Low).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"ffprobe failed for the derivative source '{localSourcePath}' " +
                $"(exitCode={result.ExitCode}, timedOut={result.TimedOut}). {Tail(result.StdErr)}");
        }

        return FfprobeOutputParser.Parse(result.StdOut);
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        VideoToolResult result = await _runner.RunFfmpegAsync(args, Timeout, ct, VideoToolPriority.Low)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg {What} failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                what, result.ExitCode, result.TimedOut, Tail(result.StdErr));

            throw new InvalidOperationException(
                $"ffmpeg {what} failed (exitCode={result.ExitCode}, timedOut={result.TimedOut}). {Tail(result.StdErr)}");
        }
    }

    /// <summary>Half the threads (at least one) on a source above the analysis-proxy threshold: it runs beside interactive work.</summary>
    private int DerivativeThreads() =>
        SourceTimeoutPolicy.IsHeavy(_options.AnalysisProxyMinDurationSeconds)
            ? Math.Max(1, _options.FfmpegThreads / 2)
            : _options.FfmpegThreads;

    private TimeSpan Timeout => SourceTimeoutPolicy.Analyze(_options);

    private static long TotalBytes(IReadOnlyList<MediaDerivativeArtifact> artifacts)
    {
        long total = 0;
        foreach (MediaDerivativeArtifact artifact in artifacts)
        {
            FileInfo info = new(artifact.LocalPath);
            if (info.Exists)
                total += info.Length;
        }

        return total;
    }

    private static string Tail(string value, int max = 2000) =>
        value.Length <= max ? value : value[^max..];

    /// <summary>
    /// Counts the <c>frame=</c> value ffmpeg reports in its own final progress line — the number of
    /// TILE outputs, i.e. sheets. Used only as a cross-check against the sheet files actually
    /// written; the file count is authoritative, since it is the thing T-302 will upload.
    /// </summary>
    internal static int? ParseReportedSheetCount(string ffmpegStdErr)
    {
        MatchCollection matches = Regex.Matches(ffmpegStdErr, @"frame=\s*(\d+)");
        if (matches.Count == 0)
            return null;

        string last = matches[^1].Groups[1].Value;
        return int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }
}
