using ReelBolt.Shared.Editing;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// R10.7: phone HDR footage (HLG, PQ) is converted to SDR BT.709 BEFORE the grade, so a graded iPhone
/// clip neither clips nor looks washed out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Detection is by tag only.</b> <c>arib-std-b67</c> is HLG and <c>smpte2084</c> is PQ. A log
/// transfer (<c>smpte428</c>, <c>log</c>, <c>log_sqrt</c>) is recorded in the probe but never tone-mapped:
/// there is no standard curve for "camera log", and guessing one from an untagged or loosely tagged stream
/// would damage footage that was fine.
/// </para>
/// <para>
/// <b>Operator.</b> <c>mobius</c> with <c>desat=0</c>: linear up to a knee, then a smooth roll-off, so
/// shadows and mid-tones stay where the SDR grade expects them and only the highlights are compressed.
/// PQ keeps <c>param=0.5</c>; HLG uses <c>param=0.2</c>. Measured on a synthetic 10-bit HLG BT.2020 fixture
/// (full-code-value ramp plus flat patches) through ffmpeg 8.0.1 and 8.1.3, 8-bit Y: with 0.5 the 75-100%
/// signal band sat at 211-232 (22 distinct levels, sunny areas too bright and flat); with 0.2 it is
/// 195-229 (35 levels), while 38% signal (18% grey) stays 109 and 55% moves only 152->150. Reference white
/// (75%) therefore sits at 195 rather than near white, which is what leaves the sun room to separate.
/// <c>peak=</c> is not a lever: with no override ffmpeg reads 10 (2030 nits relative to the 203-nit unit),
/// the same as <c>peak=10</c>; an explicit <c>peak=4.93</c> (the true 1000-nit HLG peak) sends 100% to 235
/// and above-nominal signal to 241, outside the legal 16-235 range.
/// Both kinds use <c>npl=203</c>: the ITU BT.2408 reference white (203 nits) is the luminance that maps to
/// linear 1.0. For PQ that is literal (reference white is 58% signal). For HLG, <c>zscale</c> undoes the HLG
/// OETF and the OOTF at a 203-nit display, so 75% HLG signal (reference white) lands at linear 1.0 and 38%
/// (18% grey) at about 46% of full scale. The earlier npl=100 + hable put both a full stop too dark.
/// Both numbers are fixed here; nothing the user or a document supplies reaches the filter.
/// </remarks>
public static class HdrToneMap
{
    public const string Operator = "mobius";

    /// <summary>Folded into a region's recipe hash only for regions that tone-map, so SDR region caches stay valid.</summary>
    public const string RecipeVersion = "hdr-tonemap-v3";

    public const string Hlg = "hlg";
    public const string Pq = "pq";

    /// <summary>The warning code recorded when the host's ffmpeg cannot tone-map.</summary>
    public const string UnavailableWarning = "hdr_tonemap_unavailable";

    public const string UnavailableMessage =
        "This server cannot convert HDR phone footage to standard colours, so HDR clips were used as they are and may look washed out or too bright.";

    /// <summary><c>"hlg"</c>, <c>"pq"</c> or null (SDR, log-tagged, untagged).</summary>
    public static string? KindOf(string? colorTransfer) => colorTransfer?.Trim().ToLowerInvariant() switch
    {
        "arib-std-b67" => Hlg,
        "smpte2084" => Pq,
        _ => null
    };

    public static string? KindOf(MediaProbeResult? probe) => KindOf(probe?.ColorTransfer);

    /// <summary>The per-source filter chain for a kind: linear light, to BT.709 primaries, tone-map, back to BT.709 limited-range 8-bit.</summary>
    public static string Filter(string kind)
    {
        // HLG knee 0.2 (measured on ffmpeg 8.0.1 and 8.1.3, synthetic 10-bit HLG BT.2020): with 0.5 the
        // 65..100% signal band sat 10-16 8-bit levels too bright and 75..100% had 22 distinct levels; with
        // 0.2 it has 35, while 38% (18% grey) is unchanged and 55% moves 152->150. peak= is not a lever (the
        // default already equals peak=10; peak=4.93 pushes above-nominal signal to 241, outside 16-235).
        // PQ keeps 0.5.
        string param = kind == Pq ? "0.5" : "0.2";
        return $"zscale=t=linear:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap={Operator}:param={param}:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";
    }

    /// <summary>
    /// The tone-map kind to apply to a source, or null: SDR/log/untagged, or this host's ffmpeg cannot tone-map
    /// (then the source is used as it is, as in the timeline renderer, never a failure).
    /// </summary>
    public static async Task<string?> ResolveKindAsync(MediaProbeResult? probe, IMediaWorkspace workspace, CancellationToken ct)
    {
        if (KindOf(probe) is not { } kind) return null;
        return await FfmpegHdrToneMapCapability.Shared.IsAvailableAsync(workspace, ct).ConfigureAwait(false) ? kind : null;
    }

    /// <summary>
    /// <see cref="ResolveKindAsync"/> for a file the caller has only a path for: one cheap header-only ffprobe. A probe that fails answers null (the step then behaves exactly as before; it never fails on this).
    /// </summary>
    public static async Task<string?> ResolveKindForFileAsync(IMediaWorkspace workspace, string inputPath, CancellationToken ct)
    {
        string? kind = null;
        try
        {
            VideoToolResult probed = await workspace.RunFfprobeAsync(FfmpegArgvBuilder.BuildProbeArgs(inputPath), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (probed.Succeeded && !string.IsNullOrWhiteSpace(probed.StdOut))
                kind = await ResolveKindAsync(FfprobeOutputParser.Parse(probed.StdOut), workspace, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return kind;
    }

    /// <summary>
    /// ANALYSIS ONLY, not delivery colour. A cheap, non-float conversion of HLG/PQ to BT.709 limited range for the
    /// shot/grid copy: it clips highlights where <see cref="Filter"/> rolls them off, but costs far less (the float
    /// tone-map was the most expensive part of the copy even at 540p). Never use it for anything a user sees.
    /// </summary>
    public static string AnalysisFilter(string kind) =>
        $"zscale=tin={(kind == Pq ? "smpte2084" : "arib-std-b67")}:t=bt709:min=bt2020nc:m=bt709:pin=bt2020:p=bt709:r=tv,format=yuv420p";

    /// <summary>The filter, comma-terminated, to prepend to a chain; "" when <paramref name="kind"/> is null (SDR argv unchanged).</summary>
    public static string Prefix(string? kind) => kind is null ? "" : Filter(kind) + ",";

    /// <summary>The filter, comma-prefixed, to APPEND after a scale so the tone-map runs on the small frame; "" when <paramref name="kind"/> is null.</summary>
    public static string Suffix(string? kind) => kind is null ? "" : "," + Filter(kind);

    /// <summary>Explicit BT.709 output tags, added only to an output that was tone-mapped.</summary>
    public static readonly string[] Bt709OutputTags = ["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"];

    /// <summary>The recipe-hash token for a tone-mapped source.</summary>
    public static string RecipeToken(string kind) => $"{RecipeVersion}:{kind}:{Operator}";

    /// <summary>Distinct sources a render tone-maps: asset to kind.</summary>
    public static IReadOnlyDictionary<AssetRef, string> Plan(IReadOnlyDictionary<AssetRef, LocalAsset> assets)
    {
        Dictionary<AssetRef, string> plan = [];
        foreach ((AssetRef reference, LocalAsset asset) in assets)
        {
            if (KindOf(asset.Probe) is { } kind) plan[reference] = kind;
        }

        return plan;
    }
}

/// <summary>Whether this host's ffmpeg has the filters the tone-map needs. Injected so tests can fake a bare build.</summary>
public interface IHdrToneMapCapability
{
    Task<bool> IsAvailableAsync(IVideoToolRunner runner, CancellationToken ct);
}

/// <summary>Probes <c>ffmpeg -filters</c> once per process and remembers the answer.</summary>
public sealed class FfmpegHdrToneMapCapability : IHdrToneMapCapability
{
    public static readonly FfmpegHdrToneMapCapability Shared = new();

    private readonly object _gate = new();
    private Task<bool>? _cached;

    public Task<bool> IsAvailableAsync(IVideoToolRunner runner, CancellationToken ct) =>
        CachedAsync(() => ProbeAsync(runner, ct), ct);

    /// <summary>Same answer, asked through a media workspace (the executors that have no tool runner).</summary>
    public Task<bool> IsAvailableAsync(IMediaWorkspace workspace, CancellationToken ct) =>
        CachedAsync(() => ProbeAsync(() => workspace.RunFfmpegAsync(["-hide_banner", "-filters"], TimeSpan.FromSeconds(30), ct)), ct);

    /// <summary>
    /// Remembers a real answer only. A probe that was cancelled (its starter's token) is evicted, and a waiter that was
    /// not itself cancelled asks again, so one cancel can never turn every later call in the process into a throw.
    /// </summary>
    private async Task<bool> CachedAsync(Func<Task<bool>> start, CancellationToken ct)
    {
        while (true)
        {
            Task<bool> task;
            lock (_gate)
            {
                task = _cached ??= start();
            }

            try
            {
                return await task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_cached, task) && task.IsCompleted && !task.IsCompletedSuccessfully)
                    {
                        _cached = null;
                    }
                }

                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private Task<bool> ProbeAsync(IVideoToolRunner runner, CancellationToken ct) =>
        ProbeAsync(() => runner.RunFfmpegAsync(["-hide_banner", "-filters"], TimeSpan.FromSeconds(30), ct));

    private async Task<bool> ProbeAsync(Func<Task<VideoToolResult>> run)
    {
        try
        {
            VideoToolResult result = await run().ConfigureAwait(false);
            return result.Succeeded && ContainsFilters(result.StdOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>True when both <c>zscale</c> and <c>tonemap</c> are listed as filters.</summary>
    public static bool ContainsFilters(string filtersOutput)
    {
        bool zscale = false, tonemap = false;
        foreach (string line in filtersOutput.Split('\n'))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[1] == "zscale") zscale = true;
            else if (parts[1] == "tonemap") tonemap = true;
        }

        return zscale && tonemap;
    }
}
