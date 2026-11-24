using System.Globalization;
using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video.Enhance;

/// <summary>The picture geometry an enhanced file must end up with (from the request, not the vendor).</summary>
public sealed record RemuxTarget(int Width, int Height, int FpsNum, int FpsDen)
{
    public double Fps => FpsDen > 0 ? FpsNum / (double)FpsDen : 0;

    public static RemuxTarget From(SourceFacts picture) =>
        new(picture.Width, picture.Height, picture.FpsNum, picture.FpsDen);
}

/// <summary>The seam the step executor takes, so a test can fake the remux without ffmpeg.</summary>
public interface IEnhancedAudioRemux
{
    /// <summary>Muxes <paramref name="picturePath"/> with the ORIGINAL audio of <paramref name="sourcePath"/>; see <see cref="EnhancedAudioRemux"/>.</summary>
    Task<LocalEnhanceResult> RemuxAsync(
        string sourcePath,
        string picturePath,
        string outputPath,
        RemuxTarget? target,
        CancellationToken ct);
}

/// <summary>
/// Muxes a processed picture file (picture-only, or picture with vendor audio that is discarded)
/// back with the ORIGINAL source's audio streams. The audio is copied when its codec is
/// MP4-compatible, re-encoded to AAC otherwise, and never taken from the picture. The picture is
/// conformed to CFR yuv420p at the requested size/fps only when it differs, reusing the
/// <see cref="GeneratedClipNormalizer"/> argument style. Same duration rule and failure code as
/// <see cref="LocalVideoEnhancer"/>.
/// </summary>
public sealed class EnhancedAudioRemux : IEnhancedAudioRemux
{
    private readonly IVideoToolRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly LocalEnhanceOptions _options;

    public EnhancedAudioRemux(
        IVideoToolRunner runner, IMediaProbe probe, IOptions<LocalEnhanceOptions> options)
    {
        _runner = runner;
        _probe = probe;
        _options = options.Value;
    }

    public async Task<LocalEnhanceResult> RemuxAsync(
        string sourcePath,
        string picturePath,
        string outputPath,
        RemuxTarget? target,
        CancellationToken ct)
    {
        MediaProbeResult sourceProbe = await ProbeAsync(sourcePath, ct).ConfigureAwait(false);
        // One whitelisted probe supplies every picture fact (size, rate, duration, colour); the
        // picture is never read through the non-whitelisted shared probe.
        SourceFacts picture = (await RequireVideoPictureAsync(picturePath, ct).ConfigureAwait(false)).ToSourceFacts();
        SourceFacts source = SourceFacts.FromProbe(sourceProbe);
        if (source.DurationSec > _options.MaxSourceSeconds)
        {
            throw new EnhanceException(
                EnhanceErrorCodes.SourceTooLong,
                "maxSourceSeconds",
                $"The source is {source.DurationSec:F1}s, longer than the local cap of {_options.MaxSourceSeconds}s.");
        }

        RemuxTarget requested = target ?? RemuxTarget.From(picture);
        string[] args = BuildArgs(sourcePath, picturePath, outputPath, source, picture, requested, sourceProbe.AudioCodec);
        await RunAsync(args, source, sourcePath, ct).ConfigureAwait(false);
        double duration = await VerifyDurationAsync(outputPath, source, ct).ConfigureAwait(false);

        var warnings = new List<string>();
        if (picture.NeedsSdr)
        {
            warnings.Add("hdr_converted_to_sdr");
        }

        string method = EnhanceAudioPolicy.CanCopy(sourceProbe.AudioCodec)
            ? "remux+copy"
            : "remux+aac";
        return new LocalEnhanceResult(
            true,
            method,
            requested.Width,
            requested.Height,
            requested.Fps,
            duration,
            new FileInfo(outputPath).Length,
            warnings);
    }

    /// <summary>
    /// Argv builder, exposed for byte-pinned tests. Input 0 is the original source (audio only),
    /// input 1 is the processed picture (video only). The picture audio, if any, is never mapped.
    /// </summary>
    public static string[] BuildArgs(
        string sourcePath,
        string picturePath,
        string outputPath,
        SourceFacts source,
        SourceFacts picture,
        RemuxTarget target,
        string? sourceAudioCodec)
    {
        string pictureFps = $"{picture.FpsNum}/{picture.FpsDen}";
        string targetFps = $"{target.FpsNum}/{target.FpsDen}";
        bool conform =
            picture.Width != target.Width
            || picture.Height != target.Height
            || picture.FpsNum != target.FpsNum
            || picture.FpsDen != target.FpsDen
            || target.FpsNum <= 0
            || target.FpsDen <= 0;

        // The stated rule: a picture of the target's own shape is scaled to the target height (the width follows, kept even). A picture of a
        // DIFFERENT shape (a vendor that grew the frame by another amount than the request) is scaled to fit inside the target size and
        // padded with black, centred, so the delivered file is exactly the size that was priced and plan-checked, never the vendor's.
        bool sameShape = picture.Height > 0 && target.Height > 0
            && Math.Abs(picture.Width / (double)picture.Height - target.Width / (double)target.Height) < 0.01;
        // The width the plain "-2:H" scale would give; when it is not exactly the target width (a picture a hair off the canvas shape,
        // 1596x720 for a 1600x720 canvas) the file is still delivered at exactly the canvas size, with the sub-percent stretch.
        double scaledWidth = picture.Height > 0 ? Math.Round(picture.Width * (double)target.Height / picture.Height / 2d, MidpointRounding.AwayFromZero) * 2d : 0d;
        bool plainScaleIsExact = sameShape && (int)scaledWidth == target.Width;
        string scale = !conform
            ? string.Empty
            : plainScaleIsExact
                ? $",scale=-2:{target.Height}:flags=lanczos"
                : sameShape
                    ? $",scale={target.Width}:{target.Height}:flags=lanczos"
                    : $",scale={target.Width}:{target.Height}:force_original_aspect_ratio=decrease:flags=lanczos,pad={target.Width}:{target.Height}:(ow-iw)/2:(oh-ih)/2:black";
        string fps = conform ? targetFps : pictureFps;
        string toneMap = TimelineRender.HdrToneMap.KindOf(picture.ColorTransfer) is { } kind
            ? TimelineRender.HdrToneMap.Filter(kind) + ","
            : string.Empty;
        string videoFilter = $"{toneMap}fps={fps}{scale},format=yuv420p";

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-v", "error"
        };
        args.AddRange(EnhanceInputGuards.InputOptions());
        args.AddRange(["-i", sourcePath]);
        args.AddRange(EnhanceInputGuards.InputOptions());
        args.AddRange(["-i", picturePath, "-map", "1:v:0"]);
        args.AddRange(EnhanceAudioPolicy.BuildArgs(source.HasAudio, sourceAudioCodec));
        args.AddRange(["-vf", videoFilter]);
        args.AddRange(["-c:v", "libx264", "-crf", "20", "-preset", "veryfast"]);
        args.AddRange([
            "-t", source.DurationSec.ToString("F6", CultureInfo.InvariantCulture),
            // Bound by frames as well as by time: the output can never carry more frames than the checked length at its own rate.
            "-frames:v", Math.Max(1, (long)Math.Ceiling(source.DurationSec * target.Fps) + 2).ToString(CultureInfo.InvariantCulture),
            "-movflags", "+faststart",
            "-f", "mp4",
            outputPath
        ]);
        return args.ToArray();
    }

    /// <summary>
    /// The picture (later a vendor-returned file) is untrusted: probe it with the whitelisted probe
    /// argv and refuse before any remux runs when the probe fails or there is no video stream.
    /// </summary>
    private async Task<StreamFacts> RequireVideoPictureAsync(string picturePath, CancellationToken ct)
    {
        StreamFacts? facts = await EnhanceInputGuards.ProbeStreamAsync(
            _runner, picturePath, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (facts is null || !facts.HasVideo)
        {
            throw new EnhanceException(
                EnhanceErrorCodes.PictureInvalid,
                "picture",
                "The processed picture could not be probed or has no video stream.");
        }

        return facts;
    }

    private async Task RunAsync(
        IReadOnlyList<string> args, SourceFacts source, string sourcePath, CancellationToken ct)
    {
        using (SourceTimeoutPolicy.Begin(
            source.DurationSec,
            source.Width,
            source.Height,
            SourceTimeoutPolicy.SafeLength(sourcePath),
            _options.MaxSourceSeconds,
            source.Fps))
        {
            TimeSpan timeout = TimeSpan.FromSeconds(SourceTimeoutPolicy.Seconds(_options.TimeoutSeconds));
            VideoToolResult result = await _runner.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new EnhanceException(
                    EnhanceErrorCodes.FfmpegFailed,
                    "ffmpeg",
                    $"ffmpeg enhancement remux failed (exitCode={result.ExitCode}, timedOut={result.TimedOut}).");
            }
        }
    }

    private async Task<double> VerifyDurationAsync(string outputPath, SourceFacts source, CancellationToken ct)
    {
        double duration = await ProbeDurationAsync(outputPath, ct).ConfigureAwait(false);
        if (EnhanceDuration.WithinTolerance(duration, source))
        {
            return duration;
        }

        string fixedPath = outputPath + ".fix.mp4";
        try
        {
            VideoToolResult repair = await _runner.RunFfmpegAsync(
                LocalVideoEnhancer.BuildFixArgs(outputPath, fixedPath, source, duration),
                TimeSpan.FromSeconds(_options.TimeoutSeconds),
                ct).ConfigureAwait(false);
            if (repair.Succeeded && File.Exists(fixedPath))
            {
                File.Move(fixedPath, outputPath, overwrite: true);
                duration = await ProbeDurationAsync(outputPath, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(fixedPath))
            {
                File.Delete(fixedPath);
            }
        }

        if (!EnhanceDuration.WithinTolerance(duration, source))
        {
            throw new EnhanceException(
                EnhanceErrorCodes.DurationMismatch,
                "output",
                $"Remuxed duration {duration:F3}s differs from the source {source.DurationSec:F3}s "
                + $"by more than {EnhanceDuration.ToleranceSec(source):F3}s.");
        }

        return duration;
    }

    private async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        try
        {
            // The whitelisted probe (both per-input whitelists): a re-probe is never less restricted than the first one.
            return await EnhanceInputGuards.ProbeWhitelistedAsync(_runner, path, "source", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not EnhanceException)
        {
            throw new EnhanceException(EnhanceErrorCodes.ProbeFailed, "source", "ffprobe failed on an input file.");
        }
    }

    private async Task<double> ProbeDurationAsync(string path, CancellationToken ct)
    {
        try
        {
            // Our own output is probed with the whitelisted probe too: no Enhance probe runs without both whitelists. An output whose
            // length is 0, negative, NaN or unreadable is a failed probe, never "within tolerance".
            MediaProbeResult probe = await EnhanceInputGuards.ProbeWhitelistedAsync(_runner, path, "output", ct).ConfigureAwait(false);
            if (!double.IsFinite(probe.DurationSec) || probe.DurationSec <= 0)
            {
                throw new InvalidOperationException("the output's length is not a positive number");
            }

            return probe.DurationSec;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EnhanceException(EnhanceErrorCodes.ProbeFailed, "output", "ffprobe failed on the output file.");
        }
    }
}
