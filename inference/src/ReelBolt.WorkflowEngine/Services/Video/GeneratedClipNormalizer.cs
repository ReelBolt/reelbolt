using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video;

public enum GeneratedClipNormalizationFailureReason
{
    FfprobeValidationFailed,
    NoVideoStream,
    EncodingFailed
}

public sealed class GeneratedClipNormalizationException : Exception
{
    public GeneratedClipNormalizationFailureReason Reason { get; }

    public GeneratedClipNormalizationException(GeneratedClipNormalizationFailureReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }
}

/// <summary>
/// Normalizes downloaded generated clips by ffprobe validation and re-encoding to canonical format.
/// Ensures the output matches VideoCompileStepExecutor's EncodeReencodeAsync defaults exactly:
/// libx264 codec, CRF 20, veryfast preset, yuv420p pixel format, and constant frame rate.
/// </summary>
/// <summary>
/// Conform pass target (issue #93 phase 5): what frame rate and height a delivered clip is brought
/// UP to before it reaches the compile step, so an open-model 16/24 fps 480p clip behaves like any
/// other program source. <c>0</c> on either axis means "leave that axis alone". The pass only ever
/// raises: a clip already at or above a target gets no extra filter term, so the normalization is
/// byte-identical to the pre-phase-5 one — see <see cref="GeneratedClipNormalizer.BuildVideoFilter"/>.
/// </summary>
/// <param name="Fps">Target frame rate, or 0.</param>
/// <param name="Height">Target output height in pixels, or 0.</param>
public sealed record GeneratedClipConformTarget(int Fps, int Height)
{
    /// <summary>No conform at all — the pre-phase-5 normalization.</summary>
    public static readonly GeneratedClipConformTarget None = new(0, 0);

    /// <summary>Builds a target from the step config's two knobs; both 0 (the default) yields <see cref="None"/>.</summary>
    public static GeneratedClipConformTarget? FromConfig(int fps, int height) =>
        fps <= 0 && height <= 0 ? null : new GeneratedClipConformTarget(Math.Max(0, fps), Math.Max(0, height));

    /// <summary>Whether any axis asks for a change.</summary>
    public bool IsNone => Fps <= 0 && Height <= 0;
}

/// <summary>
/// How a generated clip that shows a real person (an avatar, docs/design/avatars.md "AI Act disclosure", AV-9) is
/// marked when it is normalized. The machine-readable marking (container metadata) is ALWAYS written for such a clip;
/// <see cref="VisibleLabel"/> adds a burned-in "AI-generated" label (the avatar's <c>Visible</c> disclosure, the
/// default). A null disclosure keeps the normalizer's argv byte-identical for every clip without an avatar.
/// <para>
/// C2PA (AV-9 option B) is NOT written here: see <c>IGeneratedClipProvenanceSigner</c> for the seam and the gap.
/// </para>
/// </summary>
/// <param name="VisibleLabel">Burn a small "AI-generated" label into the picture.</param>
public sealed record GeneratedClipDisclosure(bool VisibleLabel)
{
    /// <summary>The container comment every marked clip of a PERSON carries (MP4 <c>comment</c> / <c>©cmt</c>).</summary>
    public const string Comment = "AI-generated video (ReelBolt). It shows a person's likeness used with their recorded consent.";

    /// <summary>The container comment of a clip that shows only objects: no person, so no likeness and no consent to name.</summary>
    public const string NeutralComment = "AI-generated video (ReelBolt).";

    /// <summary>
    /// The marking of a clip that shows only OBJECT avatars (docs/design/avatar-objects.md "Marking"): the same
    /// machine-readable keys (<c>ai_generated=true</c>, <c>ai_generator=ReelBolt</c>), the neutral comment and no burned
    /// label.
    /// </summary>
    public static GeneratedClipDisclosure Neutral { get; } = new(VisibleLabel: false) { ShowsPerson = false };

    /// <summary>Whether the clip shows a person's likeness (the default). False only for <see cref="Neutral"/>.</summary>
    public bool ShowsPerson { get; init; } = true;

    /// <summary>The container comment this clip is given: <see cref="Comment"/> for a person, <see cref="NeutralComment"/> for objects only.</summary>
    public string CommentText => ShowsPerson ? Comment : NeutralComment;

    /// <summary>The words burned into the picture when <see cref="VisibleLabel"/> is set (first-party, never user text).</summary>
    public const string LabelText = "AI-generated";
}

public sealed class GeneratedClipNormalizer
{
    private readonly IVideoToolRunner _runner;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<GeneratedClipNormalizer> _logger;

    public GeneratedClipNormalizer(
        IVideoToolRunner runner,
        IOptions<VideoEditingOptions> options,
        ILogger<GeneratedClipNormalizer> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Normalizes a downloaded clip by validating it with ffprobe and re-encoding to canonical format.
    /// </summary>
    /// <param name="downloadedLocalPath">Local file path of the downloaded clip.</param>
    /// <param name="destinationLocalPath">Local file path for the normalized output.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The destination path on successful normalization.</returns>
    /// <exception cref="GeneratedClipNormalizationException">Thrown when validation or encoding fails with a typed reason.</exception>
    public Task<string> NormalizeAsync(string downloadedLocalPath, string destinationLocalPath, CancellationToken ct) =>
        NormalizeAsync(downloadedLocalPath, destinationLocalPath, ct, conform: null);

    /// <summary>
    /// Validates, then re-encodes to the canonical format — optionally conforming frame rate and
    /// height upward on the way (<paramref name="conform"/>; null or <see cref="GeneratedClipConformTarget.None"/>
    /// is exactly the pre-phase-5 encode).
    /// </summary>
    public Task<string> NormalizeAsync(
        string downloadedLocalPath, string destinationLocalPath, CancellationToken ct, GeneratedClipConformTarget? conform) =>
        NormalizeAsync(downloadedLocalPath, destinationLocalPath, ct, conform, disclosure: null);

    /// <summary>The local-path normalization, marking an avatar clip per <paramref name="disclosure"/> (null = unmarked).</summary>
    public async Task<string> NormalizeAsync(
        string downloadedLocalPath, string destinationLocalPath, CancellationToken ct, GeneratedClipConformTarget? conform,
        GeneratedClipDisclosure? disclosure)
    {
        LocalMediaWorkspace workspace = LocalMediaWorkspace.ForLocalFile(downloadedLocalPath, _runner, _logger);
        await NormalizeAsync(
            workspace,
            Path.GetFileName(downloadedLocalPath),
            LocalMediaWorkspace.VirtualPathOf(destinationLocalPath, workspace.RootPath),
            ct,
            conform,
            disclosure).ConfigureAwait(false);

        // The local-path contract returns the destination path the caller handed in — not the
        // session-relative name the workspace knows it by.
        return destinationLocalPath;
    }

    /// <summary>
    /// Normalizes a clip that lives in the media workspace: <paramref name="inputVirtualPath"/> is
    /// validated with ffprobe and re-encoded to <paramref name="outputVirtualPath"/>. The caller's
    /// return value is the virtual path it asked for, exactly as the local-path overload returned
    /// the destination path it was given.
    /// </summary>
    public async Task<string> NormalizeAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        CancellationToken ct,
        GeneratedClipConformTarget? conform = null,
        GeneratedClipDisclosure? disclosure = null)
    {
        // Step 1: Validate input with ffprobe
        MediaProbeResult probeResult = await ProbeInputAsync(workspace, inputVirtualPath, ct).ConfigureAwait(false);

        // Step 2: Re-encode to canonical format
        await EncodeToCanonicalFormatAsync(workspace, inputVirtualPath, outputVirtualPath, probeResult, conform, disclosure, ct)
            .ConfigureAwait(false);

        return outputVirtualPath;
    }

    private async Task<MediaProbeResult> ProbeInputAsync(
        IMediaWorkspace workspace, string inputVirtualPath, CancellationToken ct)
    {
        string inputPath = await workspace.MaterializeAsync(inputVirtualPath, ct).ConfigureAwait(false);

        // The downloaded bytes are untrusted and the scratch file has no meaningful extension, so
        // ffmpeg would AUTO-DETECT the container: a text playlist (ffconcat/HLS/...) is a demuxer that
        // opens other inputs. Forcing the mov/mp4 demuxer (-f mov) makes anything that is not an
        // ISO-BMFF file fail validation instead of being interpreted.
        string[] args =
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format", "-show_streams",
            "-protocol_whitelist", "file",
            "-f", "mov",
            inputPath
        };
        TimeSpan timeout = SourceTimeoutPolicy.Analyze(_options);

        VideoToolResult result = await workspace.RunFfprobeAsync(args, timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffprobe validation failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, result.StdErr);
            throw new GeneratedClipNormalizationException(
                GeneratedClipNormalizationFailureReason.FfprobeValidationFailed,
                $"ffprobe validation failed for '{inputVirtualPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}).");
        }

        MediaProbeResult probeResult = FfprobeOutputParser.Parse(result.StdOut);

        // Ensure the input has a video stream
        if (string.IsNullOrEmpty(probeResult.VideoCodec))
        {
            _logger.LogWarning("Input has no video stream: {InputPath}", inputVirtualPath);
            throw new GeneratedClipNormalizationException(
                GeneratedClipNormalizationFailureReason.NoVideoStream,
                $"Input file '{inputVirtualPath}' has no video stream.");
        }

        return probeResult;
    }

    private async Task EncodeToCanonicalFormatAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        MediaProbeResult probeResult,
        GeneratedClipConformTarget? conform,
        GeneratedClipDisclosure? disclosure,
        CancellationToken ct)
    {
        string inputPath = await workspace.MaterializeAsync(inputVirtualPath, ct).ConfigureAwait(false);
        string outputPath = await workspace.MaterializeAsync(outputVirtualPath, ct).ConfigureAwait(false);

        // Build ffmpeg args matching VideoCompileStepExecutor's EncodeReencodeAsync defaults
        List<string> args = new()
        {
            "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
            "-protocol_whitelist", "file",
            "-f", "mov",
            "-i", inputPath
        };

        // Build video filter to enforce CFR and pixel format
        string videoFilter = BuildVideoFilter(probeResult, conform);
        if (disclosure is { VisibleLabel: true })
        {
            videoFilter += "," + BuildDisclosureLabelFilter(DrawtextFilterBuilder.EngineFontFor(workspace, _options.FontFilePath));
        }
        args.Add("-vf");
        args.Add(videoFilter);

        // Canonical encoding parameters (matching VideoCompileStepExecutor)
        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-crf");
        args.Add("20");
        args.Add("-preset");
        args.Add("veryfast");

        // Audio codec (aac is the default from VideoCompileStepConfig)
        if (!string.IsNullOrEmpty(probeResult.AudioCodec))
        {
            args.Add("-c:a");
            args.Add("aac");
        }
        else
        {
            args.Add("-an"); // No audio
        }

        // The destination is a scratch file (Path.GetTempFileName -> ".tmp"), which ffmpeg cannot map
        // to a muxer: without an explicit -f every real encode fails after the clip has been paid for.
        // Machine-readable marking of an avatar clip (AV-9): first-party constants only, never user text. The extra
        // keys need +use_metadata_tags in the mp4 muxer; a clip without a disclosure keeps the exact argv it had.
        if (disclosure is not null)
        {
            args.Add("-metadata");
            args.Add("comment=" + disclosure.CommentText);
            args.Add("-metadata");
            args.Add("ai_generated=true");
            args.Add("-metadata");
            args.Add("ai_generator=ReelBolt");
        }

        args.Add("-movflags");
        args.Add(disclosure is null ? "+faststart" : "+faststart+use_metadata_tags");
        args.Add("-f");
        args.Add("mp4");
        args.Add(outputPath);

        TimeSpan timeout = SourceTimeoutPolicy.Compile(_options);
        VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg re-encoding failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, result.StdErr);
            throw new GeneratedClipNormalizationException(
                GeneratedClipNormalizationFailureReason.EncodingFailed,
                $"ffmpeg re-encoding failed for '{inputVirtualPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}).");
        }
    }

    /// <summary>
    /// The <c>-vf</c> chain. Without a conform target this is the pre-phase-5 chain verbatim —
    /// <c>fps=&lt;probed&gt;,format=yuv420p</c>. With one, the frame-rate term becomes a
    /// motion-compensated <c>minterpolate</c> ONLY when the target is above the probed rate (an
    /// ffmpeg-only "RIFE pass": no model, no new dependency), and a Lanczos <c>scale</c> is inserted
    /// ONLY when the target height is above the probed height. A clip already at or above both
    /// targets gets exactly the no-conform chain, which is the no-op guarantee.
    /// </summary>
    /// <summary>
    /// The burned-in "AI-generated" label: a small box in the bottom-right corner, sized from the picture height so it
    /// stays legible at any resolution. The text is a first-party constant; the font is the engine's configured font.
    /// </summary>
    internal static string BuildDisclosureLabelFilter(string? fontFilePath) =>
        $"drawtext={DrawtextFilterBuilder.FontFileOption(fontFilePath)}text='{GeneratedClipDisclosure.LabelText}'" +
        ":fontsize=h/30:fontcolor=white@0.9:box=1:boxcolor=black@0.5:boxborderw=8:x=w-tw-24:y=h-th-24";

    internal static string BuildVideoFilter(MediaProbeResult probeResult, GeneratedClipConformTarget? conform = null)
    {
        // Enforce constant frame rate and pixel format
        // fps=X/Y converts VFR to CFR using the probed frame rate
        // format=yuv420p ensures canonical pixel format for libx264
        bool raiseFps = conform is { Fps: > 0 } && ProbedFps(probeResult) is { } probed && conform.Fps > probed + 0.001;
        bool raiseHeight = conform is { Height: > 0 } && probeResult.Height > 0 && conform.Height > probeResult.Height;

        string fps = raiseFps
            // mci (motion-compensated interpolation) with bidirectional motion estimation: the
            // closest ffmpeg gets to a learned interpolator, chosen over plain frame duplication so a
            // 16 fps open-model clip does not stutter at 30/60.
            ? $"minterpolate=fps={conform!.Fps}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1"
            : BuildFpsFilterTerm(probeResult);

        string scale = raiseHeight
            // -2 keeps the aspect and an even width (libx264 needs even dimensions).
            ? $",scale=-2:{conform!.Height}:flags=lanczos"
            : string.Empty;

        return $"{fps}{scale},format=yuv420p";
    }

    private static double? ProbedFps(MediaProbeResult probeResult) =>
        probeResult.FpsNum > 0 && probeResult.FpsDen > 0 ? (double)probeResult.FpsNum / probeResult.FpsDen : 30.0;

    private static string BuildFpsFilterTerm(MediaProbeResult probeResult)
    {
        if (probeResult.FpsNum > 0 && probeResult.FpsDen > 0)
        {
            return $"fps={probeResult.FpsNum}/{probeResult.FpsDen}";
        }

        // Fallback to 30/1 if fps cannot be determined
        return "fps=30/1";
    }
}
