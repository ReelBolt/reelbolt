using System.Globalization;
using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video.Enhance;

public sealed record LocalEnhanceResult(
    bool Applied,
    string Method,
    int OutWidth,
    int OutHeight,
    double OutFps,
    double DurationSec,
    long SizeBytes,
    IReadOnlyList<string> Warnings);

public interface ILocalVideoEnhancer
{
    Task<LocalEnhanceResult> EnhanceAsync(
        string sourcePath,
        string outputPath,
        LocalEnhanceRequest request,
        IProgress<double>? progress,
        CancellationToken ct);
}

/// <summary>
/// Free, offline video enhancement: one probed source in, one enhanced MP4 out. Everything runs
/// through the local <see cref="IVideoToolRunner"/>; the source never leaves the engine.
/// </summary>
/// <remarks>
/// Scheduling: the ffmpeg run is announced to <see cref="SourceTimeoutPolicy"/> for the whole
/// source, so a big/long input lands on the runner's single heavy lane and its deadline scales with
/// the source; a short phone clip stays on the normal lane.
/// </remarks>
public sealed class LocalVideoEnhancer : ILocalVideoEnhancer
{
    /// <summary>Encode table, matching GeneratedClipNormalizer's canonical libx264 settings.</summary>
    public const int Crf = 20;

    public const string Preset = "veryfast";

    private readonly IVideoToolRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly LocalEnhanceOptions _options;

    public LocalVideoEnhancer(
        IVideoToolRunner runner, IMediaProbe probe, IOptions<LocalEnhanceOptions> options)
    {
        _runner = runner;
        _probe = probe;
        _options = options.Value;
    }

    public async Task<LocalEnhanceResult> EnhanceAsync(
        string sourcePath,
        string outputPath,
        LocalEnhanceRequest request,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        MediaProbeResult sourceProbe = await ProbeAsync(sourcePath, ct).ConfigureAwait(false);
        SourceFacts source = SourceFacts.FromProbe(sourceProbe);

        // The cap and the run bounds use the LARGEST of the container and video-stream figures, so a
        // container that under-reports cannot make ffmpeg run longer than what was checked.
        StreamFacts? stream = await EnhanceInputGuards.ProbeStreamAsync(
            _runner, sourcePath, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (stream is null || !stream.HasVideo)
        {
            // Fail closed: with no readable stream figures the container's length cannot be trusted as the bound.
            throw new EnhanceException(EnhanceErrorCodes.ProbeFailed, "source", "The source's video stream could not be measured.");
        }

        double checkedDuration = Math.Max(source.DurationSec, stream.DurationSec ?? 0);
        long checkedFrames = Math.Max(
            source.FrameCount,
            Math.Max(stream.FrameCount ?? 0, (long)Math.Ceiling(checkedDuration * source.Fps)));
        source = source with { DurationSec = checkedDuration, FrameCount = (int)Math.Min(int.MaxValue, checkedFrames) };
        if (source.DurationSec > _options.MaxSourceSeconds)
        {
            throw new EnhanceException(
                EnhanceErrorCodes.SourceTooLong,
                "maxSourceSeconds",
                $"The source is {source.DurationSec:F1}s, longer than the local cap of {_options.MaxSourceSeconds}s.");
        }

        EnhanceBuildResult built = EnhanceFilterBuilder.Build(request, source);
        if (!built.Succeeded)
        {
            throw new EnhanceException(built.Refusal!.Code, built.Refusal.Field, built.Refusal.Message);
        }

        EnhanceFilterPlan plan = built.Plan!;
        string[] args = BuildArgs(sourcePath, outputPath, plan, source, sourceProbe.AudioCodec);
        await RunAsync(args, source, sourcePath, progress, ct).ConfigureAwait(false);

        double outDuration = await VerifyDurationAsync(outputPath, source, ct).ConfigureAwait(false);
        return new LocalEnhanceResult(
            true,
            plan.Method,
            plan.OutWidth,
            plan.OutHeight,
            plan.OutFps,
            outDuration,
            new FileInfo(outputPath).Length,
            plan.Warnings);
    }

    /// <summary>
    /// Argv builder, exposed for byte-pinned tests. One <c>-i</c> (the source), one video map, the
    /// builder's <c>-vf</c>, libx264 CRF/preset from the table, and the source audio copied or
    /// re-encoded by <see cref="EnhanceAudioPolicy"/>. Output is MP4 with faststart.
    /// </summary>
    public static string[] BuildArgs(
        string sourcePath,
        string outputPath,
        EnhanceFilterPlan plan,
        SourceFacts source,
        string? audioCodec)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-progress", "pipe:1", "-nostats"
        };
        args.AddRange(EnhanceInputGuards.InputOptions());
        args.AddRange(["-i", sourcePath, "-map", "0:v:0"]);
        args.AddRange(EnhanceAudioPolicy.BuildArgs(source.HasAudio, audioCodec));
        args.AddRange(["-vf", plan.VideoFilter]);
        args.AddRange(["-c:v", "libx264", "-crf", Crf.ToString(CultureInfo.InvariantCulture), "-preset", Preset]);
        if (TimelineRender.HdrToneMap.KindOf(source.ColorTransfer) is not null && source.NeedsSdr)
        {
            args.AddRange(TimelineRender.HdrToneMap.Bt709OutputTags);
        }

        // Bound the run to what was checked: -t is the checked duration, -frames:v is the checked
        // source frames times the op's frame factor, plus one frame of slack.
        int factor = source.Fps > 0 ? Math.Max(1, (int)Math.Round(plan.OutFps / source.Fps)) : 1;
        long maxFrames = ((long)source.FrameCount * factor) + 1;
        args.AddRange([
            "-t", source.DurationSec.ToString("F6", CultureInfo.InvariantCulture),
            "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture)
        ]);
        args.AddRange(["-movflags", "+faststart", "-f", "mp4", outputPath]);
        return args.ToArray();
    }

    /// <summary>The trim/pad repair argv, exposed for tests. One attempt, then a typed failure.</summary>
    public static string[] BuildFixArgs(
        string inputPath, string outputPath, SourceFacts source, double actualSec) =>
        actualSec > source.DurationSec
            ? EnhanceDuration.BuildTrimArgs(inputPath, outputPath, source.DurationSec)
            : EnhanceDuration.BuildPadArgs(inputPath, outputPath, source.DurationSec, actualSec);

    private async Task RunAsync(
        IReadOnlyList<string> args,
        SourceFacts source,
        string sourcePath,
        IProgress<double>? progress,
        CancellationToken ct)
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
            VideoToolResult result = await _runner.RunFfmpegAsync(
                args,
                timeout,
                ct,
                line => ReportProgress(line, source.DurationSec, progress),
                VideoToolPriority.Normal).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new EnhanceException(
                    EnhanceErrorCodes.FfmpegFailed,
                    "ffmpeg",
                    $"ffmpeg enhancement failed (exitCode={result.ExitCode}, timedOut={result.TimedOut}).");
            }
        }

        progress?.Report(1.0);
    }

    private static void ReportProgress(string line, double durationSec, IProgress<double>? progress)
    {
        if (progress is null || durationSec <= 0 || !line.StartsWith("out_time_us=", StringComparison.Ordinal))
        {
            return;
        }

        if (long.TryParse(line["out_time_us=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long micros)
            && micros > 0)
        {
            progress.Report(Math.Clamp(micros / 1_000_000.0 / durationSec, 0.0, 1.0));
        }
    }

    /// <summary>Probe the output; one trim/pad repair, then <c>ENHANCE_DURATION_MISMATCH</c>.</summary>
    private async Task<double> VerifyDurationAsync(
        string outputPath, SourceFacts source, CancellationToken ct)
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
                BuildFixArgs(outputPath, fixedPath, source, duration),
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
                $"Enhanced duration {duration:F3}s differs from the source {source.DurationSec:F3}s "
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
            throw new EnhanceException(EnhanceErrorCodes.ProbeFailed, "source", "ffprobe failed on the source file.");
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
