using Microsoft.Extensions.Options;
using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>The seam the analysis step depends on, so its event path can be tested without ffmpeg.</summary>
public interface IVideoEventDetector
{
    Task<VideoEventSet> DetectAsync(
        IMediaWorkspace workspace, string pixelsVirtualPath, MediaProbeResult probe, VideoEventOptions? options = null,
        string? audioVirtualPath = null, int pixelsFpsNum = 0, int pixelsFpsDen = 0, CancellationToken ct = default);
}

/// <summary>Knobs of one event-detection run. The defaults are what the engine uses; none of them is per clip.</summary>
public sealed record VideoEventOptions
{
    /// <summary>Cap on the events returned; the strongest are kept and the rest are counted in <c>Suppressed</c>.</summary>
    public int MaxEvents { get; init; } = VideoEventMerger.DefaultMaxEvents;
    public bool DetectSpeech { get; init; } = true;
    public bool DetectBeats { get; init; } = true;
    /// <summary>Same defaults as the VideoAnalyze silence gaps, so speech edges agree with the silences the analyzer already reports.</summary>
    public double SilenceThresholdDb { get; init; } = -34.0;
    public int MinSilenceMs { get; init; } = 350;
}

/// <summary>
/// Layer-1 event detection: typed, frame-anchored events from pixels (one gray 160x90 pass at the source frame rate,
/// capped at 60 fps) and from audio (speech edges from the silence gaps, beats from spectral flux). Deterministic,
/// no model and no paid call.
/// </summary>
public sealed class VideoEventDetector : IVideoEventDetector
{
    private readonly FrameSignalSampler _sampler;
    private readonly ISilenceDetector _silence;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<VideoEventDetector> _logger;

    public VideoEventDetector(
        FrameSignalSampler sampler, ISilenceDetector silence, IOptions<VideoEditingOptions> options, ILogger<VideoEventDetector> logger)
    {
        _sampler = sampler;
        _silence = silence;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Detects the events of <paramref name="probe"/>'s source. <paramref name="pixelsVirtualPath"/> may be the analysis
    /// proxy of the source (pass its frame rate in <paramref name="pixelsFpsNum"/>/<paramref name="pixelsFpsDen"/>;
    /// 0 means the source rate); frames are always reported on the source's own frame grid. Audio is read from
    /// <paramref name="audioVirtualPath"/>, which defaults to the pixel path.
    /// </summary>
    public async Task<VideoEventSet> DetectAsync(
        IMediaWorkspace workspace,
        string pixelsVirtualPath,
        MediaProbeResult probe,
        VideoEventOptions? options = null,
        string? audioVirtualPath = null,
        int pixelsFpsNum = 0,
        int pixelsFpsDen = 0,
        CancellationToken ct = default)
    {
        options ??= new VideoEventOptions();
        double sourceFps = SourceTimeoutPolicy.FpsOf(probe.FpsNum, probe.FpsDen);
        if (sourceFps <= 0)
            sourceFps = 30.0;
        int num = pixelsFpsNum > 0 && pixelsFpsDen > 0 ? pixelsFpsNum : probe.FpsNum > 0 ? probe.FpsNum : 30;
        int den = pixelsFpsNum > 0 && pixelsFpsDen > 0 ? pixelsFpsDen : probe.FpsDen > 0 ? probe.FpsDen : 1;

        FrameEventAnalyzer analyzer = new();
        (double plannedFps, _) = FrameSignalSampler.PlanRate(SourceTimeoutPolicy.FpsOf(num, den));
        EditMomentAnalyzer moments = new(plannedFps, probe.Width, probe.Height);
        FrameSignalInfo info = await _sampler.SampleAsync(workspace, pixelsVirtualPath, num, den, probe.DurationSec, new TeeConsumer(analyzer, moments), ct).ConfigureAwait(false);
        FrameAnalysis analysis = analyzer.Complete();

        // Analysed index -> source frame: by time, so a decimated (>60 fps) pass and a proxy at a lower rate both land on the source grid.
        double toSource = info.Fps > 0 ? sourceFps / info.Fps : 1.0;
        int sourceFrames = (int)Math.Round(info.FrameCount * toSource);
        List<VideoEvent> momentList = moments.Complete();
        if (moments.DroppedMoments > 0)
            _logger.LogWarning("events_moments_truncated: kept {Kept} frame moments, dropped {Dropped}; the source is unusually busy", momentList.Count, moments.DroppedMoments);
        List<VideoEvent> events = [];
        foreach (VideoEvent e in MomentEvents.Combine(VisualEventClassifier.Classify(analysis), momentList, moments.InFullPicture, plannedFps))
        {
            events.Add(e with
            {
                Frame = (int)Math.Round(e.Frame * toSource),
                EndFrame = e.EndFrame is { } end ? (int)Math.Round(end * toSource) : null
            });
        }

        // A part that fails is left out and the rest is returned, as before. The set then says it is incomplete,
        // so nothing keeps it as the answer for this file.
        bool degraded = info.RateUnknown;
        string audioPath = audioVirtualPath ?? pixelsVirtualPath;
        if (probe.AudioCodec is not null)
        {
            if (options.DetectSpeech)
            {
                IReadOnlyList<VideoEvent>? speech = await SpeechEdgesAsync(workspace, audioPath, probe, options, sourceFps, sourceFrames, ct).ConfigureAwait(false);
                degraded |= speech is null;
                events.AddRange(speech ?? []);
            }

            if (options.DetectBeats)
            {
                IReadOnlyList<VideoEvent>? beats = await BeatsAsync(workspace, audioPath, sourceFps, ct).ConfigureAwait(false);
                degraded |= beats is null;
                events.AddRange(beats ?? []);
            }
        }

        (List<VideoEvent> merged, int suppressed) = VideoEventMerger.Merge(events, sourceFrames, options.MaxEvents);
        return new VideoEventSet(sourceFps, sourceFrames, probe.Width, probe.Height, merged, suppressed) { Degraded = degraded };
    }

    /// <summary>Speech starts where a silence gap ends and ends where one begins; the file's own edges are not events.</summary>
    internal static IEnumerable<VideoEvent> SpeechEdges(
        IReadOnlyList<(double StartSec, double EndSec)> silences, double durationSec, double fps)
    {
        const double edgeSec = 0.0005;
        foreach ((double startSec, double endSec) in silences)
        {
            double gapSec = endSec - startSec;
            // A gap that ends the file or starts it has only one inner edge.
            if (endSec < durationSec - edgeSec && endSec > edgeSec)
                yield return new VideoEvent(VideoEventType.SpeechStart, (int)Math.Round(endSec * fps), null, null, 0.8, VideoEventOrigin.Audio, 0.9);
            if (startSec > edgeSec && startSec < durationSec - edgeSec)
                yield return new VideoEvent(VideoEventType.SpeechEnd, (int)Math.Round(startSec * fps), null, null, Math.Clamp(0.5 + gapSec / 4.0, 0.5, 1.0), VideoEventOrigin.Audio, 0.9);
        }
    }

    /// <summary>The speech edges, or null when the silence pass failed.</summary>
    private async Task<IReadOnlyList<VideoEvent>?> SpeechEdgesAsync(
        IMediaWorkspace workspace, string audioPath, MediaProbeResult probe, VideoEventOptions options, double fps, int frames, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<(double StartSec, double EndSec)> silences = await _silence
                .DetectAsync(workspace, audioPath, options.SilenceThresholdDb, options.MinSilenceMs / 1000.0, probe.DurationSec, ct)
                .ConfigureAwait(false);
            return SpeechEdges(silences, probe.DurationSec, fps).Where(e => e.Frame < frames).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "speech edge detection failed; continuing without speech events");
            return null;
        }
    }

    /// <summary>The beat audio decode: mono s16le at the detector's rate, held to the derivative demuxer whitelist.</summary>
    internal static string[] BuildBeatPcmArgs(string inputPath, string pcmPath, int threads) =>
    [
        "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
        "-protocol_whitelist", "file",
        "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
        "-threads", FfmpegArgvFormat.Number(threads),
        "-i", inputPath,
        "-vn", "-ac", "1", "-ar", FfmpegArgvFormat.Number(SpectralFluxBeatDetector.SampleRate),
        "-f", "s16le",
        pcmPath
    ];

    /// <summary>A beat is only emitted inside music, so confidence starts at 0.5 and rises with the steadiness of the pulse (0.28..1 maps to about 0.6..0.9).</summary>
    internal static double BeatConfidence(double pulse) => Math.Round(0.5 + 0.4 * Math.Clamp(pulse, 0.0, 1.0), 3);

    /// <summary>Byte cap of the remote-session beat PCM read (256 MiB, about 2.3 hours of 16 kHz mono s16le).</summary>
    internal const int MaxRemotePcmBytes = 256 * 1024 * 1024;

    /// <summary>The beats, or null when the audio could not be decoded or read.</summary>
    private async Task<IReadOnlyList<VideoEvent>?> BeatsAsync(IMediaWorkspace workspace, string audioPath, double fps, CancellationToken ct)
    {
        string pcmVirtualPath = $"events-pcm-{Guid.NewGuid():N}.raw";
        string pcmPath = string.Empty;
        try
        {
            string inputPath = await workspace.MaterializeAsync(audioPath, ct).ConfigureAwait(false);
            pcmPath = await workspace.MaterializeAsync(pcmVirtualPath, ct).ConfigureAwait(false);
            string[] args = BuildBeatPcmArgs(inputPath, pcmPath, _options.FfmpegThreads);
            VideoToolResult result = await workspace.RunFfmpegAsync(args, SourceTimeoutPolicy.Analyze(_options), ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning("beat audio decode failed (exitCode={ExitCode}); continuing without beats", result.ExitCode);
                return null;
            }

            Stream stream;
            if (workspace.IsRemoteSession)
            {
                // #1206: pcmPath is the session-relative NAME on a remote session; the PCM comes back through one
                // bounded workspace read. Over the cap is named in the log rather than lost as a generic failure.
                try
                {
                    stream = new MemoryStream(await workspace.ReadAsync(pcmVirtualPath, MaxRemotePcmBytes, ct).ConfigureAwait(false), writable: false);
                }
                catch (MediaWorkspaceFileTooLargeException ex)
                {
                    _logger.LogWarning(ex, "events_beats_skipped: remote session beat PCM is over the {MaxBytes}-byte read cap; continuing without beats", MaxRemotePcmBytes);
                    return null;
                }
            }
            else
            {
                stream = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            }

            await using (stream)
            {
                return SpectralFluxBeatDetector.Detect(stream)
                    .Select(b => new VideoEvent(VideoEventType.Beat, (int)Math.Round(b.TimeSec * fps), null, null, b.Strength, VideoEventOrigin.Audio, BeatConfidence(b.Pulse)))
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "beat detection failed; continuing without beats");
            return null;
        }
        finally
        {
            // The media-session op set has no delete: a remote session's PCM stays in the session directory and goes
            // with it. A host File.Delete on a session NAME would hit the engine's cwd.
            if (pcmPath.Length > 0 && !workspace.IsRemoteSession)
            {
                try
                {
                    File.Delete(pcmPath);
                }
                catch (IOException)
                {
                    // Best effort: the scratch space is removed with the session anyway.
                }
            }
        }
    }
}
