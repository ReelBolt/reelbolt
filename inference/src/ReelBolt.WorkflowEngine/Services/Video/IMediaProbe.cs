namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Probed characteristics of a media file. Fps is carried as an exact rational — the numerator
/// and denominator ffprobe reports in <c>r_frame_rate</c> (e.g. <c>"30000/1001"</c>) — rather
/// than collapsed to a <c>double</c>, so downstream frame-exact arithmetic (plan §4.3) never
/// accumulates floating-point drift (R9).
///
/// <para>
/// <see cref="Container"/>, <see cref="AudioChannels"/> and <see cref="BitrateBps"/> are
/// <b>trailing optional parameters</b> so every existing positional construction — src and tests —
/// still compiles unchanged. Each is null when ffprobe did not report it (a missing field, a
/// non-numeric value or ffprobe's literal <c>"N/A"</c>), and null means <b>not known</b>, never
/// zero: a 0-channel or 0-bit/s reading is not a fact worth carrying, and a consumer that rendered
/// one as a value would be stating something ffprobe never said.
/// </para>
/// </summary>
public sealed record MediaProbeResult(
    double DurationSec,
    int FpsNum,
    int FpsDen,
    int Width,
    int Height,
    string? VideoCodec,
    string? AudioCodec,
    int? AudioSampleRate,
    string? PixFmt = null,
    string? AlphaModeTag = null,
    string? Container = null,
    int? AudioChannels = null,
    long? BitrateBps = null,
    // R10.7: the first video stream's colour tags, verbatim from ffprobe (null when untagged/unreported).
    string? ColorTransfer = null,
    string? ColorPrimaries = null,
    string? ColorSpace = null,
    // #776: ffmpeg's autorotate changes this stream's decoded pixels (any rotation angle or a mirror), whether or not the size swaps.
    bool AltersPixelsOnDecode = false,
    // log-footage-color P2: verbatim ffprobe color_range ("tv"/"pc"), bit depth (bits_per_raw_sample or pix_fmt), and the
    // allowlisted, sanitised maker/model/software tags. All null when unreported.
    string? ColorRange = null,
    int? BitDepth = null,
    string? Make = null,
    string? Model = null,
    string? Software = null);

public interface IMediaProbe
{
    /// <summary>
    /// Probes the session file at <paramref name="virtualPath"/> through
    /// <paramref name="workspace"/>. This is the shape a remote workspace (D11) needs: the bytes
    /// come from the workspace, and nothing here assumes they are on this host.
    /// </summary>
    Task<MediaProbeResult> ProbeAsync(IMediaWorkspace workspace, string virtualPath, CancellationToken ct);

    /// <summary>
    /// Compatibility overload for a caller that holds an absolute local path rather than a media
    /// workspace — VideoAnalyze, its last caller, now goes through <see cref="IMediaWorkspace"/>.
    /// The concrete implementation roots a
    /// <see cref="LocalMediaWorkspace"/> at the file's own directory and probes it by name, which
    /// resolves to the very same path — so behaviour and argv are unchanged.
    /// </summary>
    Task<MediaProbeResult> ProbeAsync(string localFilePath, CancellationToken ct);
}
