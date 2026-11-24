using ReelBolt.Shared.Editing.Color;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// T-011: a clip shot in a camera log profile is converted to Rec.709 at the very start of its leg,
/// before trim/crop/transform/stabilise/keyer/grade, so every look builds on a correct picture.
/// </summary>
/// <remarks>
/// <para>
/// <b>Range and matrix come from the profile, never from the file tags.</b> Cameras and edit software
/// tag log footage inconsistently (untagged, BT.601, BT.2020), and letting ffmpeg's implicit YUV to RGB
/// conversion read those tags shifts the picture by tens of code values (spike Q2). The profile's
/// published conventions are the truth: <c>ExpectedRange</c> ("full"/"legal") and
/// <c>ExpectedYuvMatrix</c> ("bt709"/"bt2020nc") select the explicit decode prefix.
/// </para>
/// <para>
/// <b>Precision.</b> The table is fed to <c>lut3d</c> as <c>gbrpf32le</c>, never <c>rgb24</c>
/// (spike Q3: rgb24 adds about 3 code values of mean error), and interpolated tetrahedrally.
/// The output is Rec.709 limited-range <c>yuv420p</c> with explicit tags, the same contract
/// <see cref="HdrToneMap"/> ends on (spike Q5).
/// </para>
/// <para>
/// <b>Trust boundary.</b> The only document value here is the profile id, which the validator
/// restricts to <see cref="CameraProfiles.Ids"/>; the cube path is the renderer's own scratch path.
/// </para>
/// </remarks>
public static class CameraProfileStage
{
    /// <summary>Folded into a region's recipe hash only for regions that have a profile clip.</summary>
    public const string RecipeVersion = CameraProfileVariants.RecipeVersion;

    public const string Interpolation = "tetrahedral";

    /// <summary>The warning code recorded when an HDR clip also names a camera profile (the tone-map wins).</summary>
    public const string IgnoredHdrWarning = "camera_profile_ignored_hdr";

    public const string IgnoredHdrMessage =
        "HDR footage that also has a camera log profile is converted as HDR; the camera profile was ignored, so the picture may look different from what the profile predicts.";

    /// <summary>
    /// The complete filter stage for one profile: explicit range/matrix decode, the Rec.709 table as a
    /// 3D LUT, then the Rec.709 limited-range output with explicit tags. <paramref name="useZscale"/> is
    /// false on a host whose ffmpeg has no <c>zscale</c>; the swscale fallback is self-consistent but
    /// slightly less accurate against an exact encode (spike Q2).
    /// </summary>
    public static string Filter(string profileId, string cubePath, bool useZscale = true)
    {
        CameraProfileDefinition profile = Lookup(profileId);
        string range = profile.ExpectedRange == "full" ? "pc" : profile.ExpectedRange == "legal" ? "tv" : throw new ArgumentException($"camera profile '{profileId}' has an unknown expected range '{profile.ExpectedRange}'", nameof(profileId));
        string matrix = profile.ExpectedYuvMatrix switch
        {
            "bt709" => "bt709",
            "bt2020nc" => "bt2020",
            _ => throw new ArgumentException($"camera profile '{profileId}' has an unknown expected matrix '{profile.ExpectedYuvMatrix}'", nameof(profileId))
        };
        string prefix = useZscale
            ? $"zscale=rin={range}:min={(matrix == "bt2020" ? "2020_ncl" : "709")}:tin=709:pin=709:t=709:p=709,format=gbrpf32le"
            : $"scale=in_range={range}:in_color_matrix={matrix}:flags=accurate_rnd+full_chroma_int,format=gbrpf32le";
        return $"{prefix},lut3d=file='{EscapePath(cubePath)}':interp={Interpolation}," +
               "scale=out_range=tv:out_color_matrix=bt709:flags=accurate_rnd+full_chroma_int,format=yuv420p," +
               "setparams=range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709";
    }

    /// <summary>
    /// The recipe-hash token: the profile and the decode range/matrix it selects. Shared with the proxy
    /// derivative's spec, so one definition describes both the timeline stage and the baked proxy.
    /// </summary>
    public static string RecipeToken(string profileId) => CameraProfileVariants.RecipeToken(profileId);

    /// <summary>The filter, comma-terminated, to prepend to a clip's leg; "" when the clip names no profile.</summary>
    public static string Prefix(string? profileId, string cubePath, bool useZscale = true) =>
        string.IsNullOrEmpty(profileId) ? string.Empty : Filter(profileId, cubePath, useZscale) + ",";

    private static CameraProfileDefinition Lookup(string profileId) =>
        CameraProfiles.TryGet(profileId, out CameraProfileDefinition? profile)
            ? profile
            : throw new ArgumentException($"unknown camera profile '{profileId}'", nameof(profileId));

    /// <summary>
    /// Escapes a path used inside a quoted ffmpeg filter argument. Same handling as <c>GradeFilterBuilder</c>'s LUT path
    /// (forward slashes, quoted apostrophe, escaped colon), plus the comma that separates filters in the graph: inside
    /// <c>'...'</c> ffmpeg still splits on an unescaped comma. Existing server-chosen paths carry no comma or backslash,
    /// so their output is unchanged.
    /// </summary>
    private static string EscapePath(string path) => path.Replace("\\", "/").Replace("'", @"'\''").Replace(":", @"\:").Replace(",", @"\,");
}

/// <summary>Whether this host's ffmpeg has <c>zscale</c> for the explicit log decode. Injected so tests can fake a bare build.</summary>
public interface ICameraProfileCapability
{
    Task<bool> IsAvailableAsync(IVideoToolRunner runner, CancellationToken ct);
}

/// <summary>Probes <c>ffmpeg -filters</c> once per process and remembers whether <c>zscale</c> is present.</summary>
public sealed class FfmpegCameraProfileCapability : ICameraProfileCapability
{
    public static readonly FfmpegCameraProfileCapability Shared = new();

    private readonly object _gate = new();
    private Task<bool>? _cached;

    public Task<bool> IsAvailableAsync(IVideoToolRunner runner, CancellationToken ct) =>
        CachedAsync(() => ProbeAsync(() => runner.RunFfmpegAsync(["-hide_banner", "-filters"], TimeSpan.FromSeconds(30), ct)), ct);

    /// <summary>Same answer, asked through a media workspace (the executors that have no tool runner).</summary>
    public Task<bool> IsAvailableAsync(IMediaWorkspace workspace, CancellationToken ct) =>
        CachedAsync(() => ProbeAsync(() => workspace.RunFfmpegAsync(["-hide_banner", "-filters"], TimeSpan.FromSeconds(30), ct)), ct);

    /// <summary>
    /// Remembers a real answer only. A probe that was cancelled is evicted so one cancel cannot turn
    /// every later call in the process into a throw.
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

    private async Task<bool> ProbeAsync(Func<Task<VideoToolResult>> run)
    {
        try
        {
            VideoToolResult result = await run().ConfigureAwait(false);
            return result.Succeeded && ContainsZscale(result.StdOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>True when <c>zscale</c> is listed as a filter.</summary>
    public static bool ContainsZscale(string filtersOutput)
    {
        foreach (string line in filtersOutput.Split('\n'))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == "zscale") return true;
        }

        return false;
    }
}
