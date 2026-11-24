using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video.Events;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// R09.1: runs the analysis kinds on ONE local file. Silence, shots and motion are deterministic ffmpeg (no model call, no
/// credits); the transcript calls the default Transcription provider. Every failure becomes an outcome with a plain
/// sentence, never an exception (except a real cancellation), so one failing kind never costs the others.
/// </summary>
public sealed class MediaAnalysisService(
    IMediaProbe probe,
    ISilenceDetector silence,
    IShotDetector shots,
    IVideoToolRunner runner,
    IAudioExtractor audio,
    IInferenceProviderResolver providers,
    ITranscriptionClientFactory transcription,
    IOptions<VideoEditingOptions> options,
    ILogger<MediaAnalysisService> logger,
    IVideoEventDetector? events = null)
{
    /// <summary>The detector thresholds the VideoAnalyze defaults use (silence -34 dB / 350 ms, scene 0.30).</summary>
    public const double SilenceThresholdDb = -34.0;
    public const double MinSilenceSeconds = 0.35;
    public const double SceneThreshold = 0.30;
    public const long MaxAsrChunkBytes = 5_000_000;
    private const int AsrMaxAttempts = 2;

    private readonly VideoEditingOptions _options = options.Value;

    /// <summary>Plain, user-facing sentences. Never an exception message, a path or a provider name.</summary>
    public const string CouldNotRead = "We couldn't read this file. It may be damaged or not a video or audio file.";
    public const string NoAudio = "This file has no audio track.";
    public const string NoVideo = "This file has no picture, so there are no shots or motion to find.";
    public const string NoProvider = "Transcription isn't set up for this workspace yet.";
    public const string TranscriptionFailed = "The transcription service couldn't process this file. You were not charged.";
    public const string NoBeat = "No steady beat was found in this file.";
    public const string Generic = "The analysis didn't finish. Try again in a moment.";

    /// <summary>Only the first 20 minutes of a music file are tracked: a full song's grid is far more than enough.</summary>
    public const double MaxBeatSeconds = 1200;

    public async Task<IReadOnlyList<MediaAnalysisKindOutcome>> AnalyseAsync(
        string localPath, IReadOnlyList<string> kinds, string? language, double? heldSeconds, Action<string, int>? progress, CancellationToken ct)
    {
        MediaProbeResult? source;
        try
        {
            source = await probe.ProbeAsync(localPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not probe a file for analysis");
            return kinds.Select(k => Fail(k, CouldNotRead)).ToList();
        }

        using IDisposable timeoutScope = SourceTimeoutPolicy.Begin(
            source.DurationSec, source.Width, source.Height, SourceTimeoutPolicy.SafeLength(localPath),
            fps: SourceTimeoutPolicy.FpsOf(source.FpsNum, source.FpsDen));
        bool hasVideo = source.Width > 0 && source.Height > 0 && !string.IsNullOrWhiteSpace(source.VideoCodec);
        bool hasAudio = !string.IsNullOrWhiteSpace(source.AudioCodec);
        IReadOnlyList<(double StartSec, double EndSec)>? silenceSpans = null;

        List<MediaAnalysisKindOutcome> outcomes = [];
        foreach (string kind in kinds)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(kind, 0);
            MediaAnalysisKindOutcome outcome;
            try
            {
                switch (kind)
                {
                    case MediaAnalysisKinds.Silence when !hasAudio:
                    case MediaAnalysisKinds.Transcript when !hasAudio:
                    case MediaAnalysisKinds.Beats when !hasAudio:
                        outcome = Fail(kind, NoAudio);
                        break;
                    case MediaAnalysisKinds.Shots when !hasVideo:
                    case MediaAnalysisKinds.Motion when !hasVideo:
                    case MediaAnalysisKinds.Events when !hasVideo:
                        outcome = Fail(kind, NoVideo);
                        break;
                    case MediaAnalysisKinds.Silence:
                        silenceSpans = await silence.DetectAsync(localPath, SilenceThresholdDb, MinSilenceSeconds, source.DurationSec, ct).ConfigureAwait(false);
                        outcome = SilenceOutcome(silenceSpans, source.DurationSec);
                        break;
                    case MediaAnalysisKinds.Shots:
                        outcome = ShotsOutcome(await shots.DetectShotsAsync(localPath, SceneThreshold, source.DurationSec, ct).ConfigureAwait(false));
                        break;
                    case MediaAnalysisKinds.Motion:
                        outcome = await MotionAsync(localPath, ct).ConfigureAwait(false);
                        break;
                    case MediaAnalysisKinds.Events:
                        outcome = await EventsAsync(localPath, source, ct).ConfigureAwait(false);
                        break;
                    case MediaAnalysisKinds.Beats:
                        outcome = await BeatsAsync(localPath, source.DurationSec, ct).ConfigureAwait(false);
                        break;
                    case MediaAnalysisKinds.Transcript:
                        silenceSpans ??= await SilenceForChunkingAsync(localPath, source.DurationSec, ct).ConfigureAwait(false);
                        outcome = await TranscriptAsync(localPath, source.DurationSec, heldSeconds, silenceSpans, language, ct).ConfigureAwait(false);
                        break;
                    default:
                        outcome = Fail(kind, Generic);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Analysis kind {Kind} failed", kind);
                outcome = Fail(kind, Generic);
            }

            outcomes.Add(outcome);
            progress?.Invoke(kind, 100);
        }

        return outcomes;
    }

    private static MediaAnalysisKindOutcome Fail(string kind, string reason) =>
        new(kind, false, null, null, reason, null, false);

    private static MediaAnalysisKindOutcome SilenceOutcome(IReadOnlyList<(double StartSec, double EndSec)> spans, double durationSec)
    {
        bool truncated = spans.Count > MediaAnalysisJson.MaxItems;
        List<AnalysisRange> ranges = spans.Take(MediaAnalysisJson.MaxItems).Select(s => new AnalysisRange(Round(s.StartSec), Round(s.EndSec))).ToList();
        double total = Round(spans.Sum(s => Math.Max(0, s.EndSec - s.StartSec)));
        return new(MediaAnalysisKinds.Silence, true,
            MediaAnalysisJson.Serialize(new AnalysisSilenceResult(durationSec > 0 ? Round(durationSec) : null, ranges, truncated)),
            MediaAnalysisJson.Serialize(new { rangeCount = spans.Count, totalSeconds = total }), null, null, false);
    }

    private static MediaAnalysisKindOutcome ShotsOutcome(IReadOnlyList<(double StartSec, double EndSec)> spans)
    {
        bool truncated = spans.Count > MediaAnalysisJson.MaxItems;
        List<AnalysisRange> list = spans.Take(MediaAnalysisJson.MaxItems).Select(s => new AnalysisRange(Round(s.StartSec), Round(s.EndSec))).ToList();
        return new(MediaAnalysisKinds.Shots, true,
            MediaAnalysisJson.Serialize(new AnalysisShotsResult(list, truncated)),
            MediaAnalysisJson.Serialize(new { shotCount = spans.Count }), null, null, false);
    }

    private async Task<MediaAnalysisKindOutcome> MotionAsync(string localPath, CancellationToken ct)
    {
        VideoToolResult result = await runner.RunFfmpegAsync(
            FfmpegArgvBuilder.BuildMotionArgs(localPath, _options.FfmpegThreads),
            SourceTimeoutPolicy.Analyze(_options), ct).ConfigureAwait(false);
        if (!result.Succeeded) return Fail(MediaAnalysisKinds.Motion, Generic);

        IReadOnlyList<AnalysisMotionWindow> windows = MotionSignalParser.BuildWindows(MotionSignalParser.ParseSamples(result.StdOut));
        bool truncated = windows.Count > MediaAnalysisJson.MaxItems;
        double still = windows.Where(w => w.MotionClass == "still").Sum(w => w.EndSec - w.StartSec);
        return new(MediaAnalysisKinds.Motion, true,
            MediaAnalysisJson.Serialize(new AnalysisMotionResult(windows.Take(MediaAnalysisJson.MaxItems).ToList(), truncated)),
            MediaAnalysisJson.Serialize(new { windowCount = windows.Count, stillSeconds = Round(still) }), null, null, false);
    }

    private async Task<MediaAnalysisKindOutcome> EventsAsync(string localPath, MediaProbeResult source, CancellationToken ct)
    {
        if (events is null) return Fail(MediaAnalysisKinds.Events, Generic);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.ForLocalFile(localPath, runner, logger);
        string virtualPath = LocalMediaWorkspace.VirtualPathOf(localPath, workspace.RootPath);
        VideoEventSet set = await events.DetectAsync(workspace, virtualPath, source, ct: ct).ConfigureAwait(false);

        bool truncated = set.Events.Count > MediaAnalysisJson.MaxItems;
        double fps = set.Fps > 0 ? set.Fps : 30.0;
        List<AnalysisEvent> items = set.Events.Take(MediaAnalysisJson.MaxItems).Select((e, i) => new AnalysisEvent(
            $"e{i}", VideoEventOffers.TypeWord(e.Type), e.Frame, Math.Round(e.Frame / fps, 3), Math.Round(e.Strength, 3),
            e.Region is { } r ? new AnalysisEventRegion(Math.Round(r.X, 4), Math.Round(r.Y, 4), Math.Round(r.W, 4), Math.Round(r.H, 4)) : null,
            Math.Round(e.Confidence, 3),
            e.EndFrame, e.Direction, e.Speed, VideoEventKinds.Parse(e.Kind))).ToList();
        Dictionary<string, int> byType = set.Events.GroupBy(e => VideoEventOffers.TypeWord(e.Type)).ToDictionary(g => g.Key, g => g.Count());
        // The version rides in the result and in its summary: a row made by another version is served as outdated and is
        // replaced by the next run. A set the detector marked incomplete is stored with "degraded": true in both and is
        // treated the same way, so it is shown but never kept as the answer for this file.
        Dictionary<string, object> summary = new()
        {
            ["eventCount"] = set.Events.Count,
            ["byType"] = byType,
            ["suppressed"] = set.Suppressed,
            ["version"] = MediaAnalysisJson.EventsVersion,
        };
        if (set.Degraded) summary["degraded"] = true;
        return new(MediaAnalysisKinds.Events, true,
            MediaAnalysisJson.Serialize(new AnalysisEventsResult(Math.Round(fps, 3), set.FrameCount, items, set.Suppressed, truncated, MediaAnalysisJson.EventsVersion, set.Degraded)),
            MediaAnalysisJson.Serialize(summary), null, null, false);
    }

    private async Task<MediaAnalysisKindOutcome> BeatsAsync(string localPath, double fullDurationSec, CancellationToken ct)
    {
        bool durationCut = fullDurationSec > MaxBeatSeconds;
        string wav = Path.Combine(Path.GetDirectoryName(localPath)!, "analysis-beats.wav");
        // Always decode through the capped range: the probed length is a header claim and is never trusted as a
        // bound, so a file that reports a short length but runs for hours still decodes at most MaxBeatSeconds.
        await audio.ExtractWavRangeAsync(localPath, wav, 0, MaxBeatSeconds, ct).ConfigureAwait(false);

        float[]? samples = WavRmsSampler.ReadMonoSamples(await File.ReadAllBytesAsync(wav, ct).ConfigureAwait(false), out int sampleRate);
        MusicBeatTrack? track = samples is null ? null : MusicBeatTracker.Track(samples, sampleRate);
        if (track is null) return Fail(MediaAnalysisKinds.Beats, NoBeat);

        // Truncated when the probe says the file is longer than the cap, or when the decoded audio itself reached the cap.
        bool truncated = durationCut || track.DurationSec >= MaxBeatSeconds - 1 || track.BeatTimesSec.Count > MediaAnalysisJson.MaxItems;
        List<double> beatTimes = track.BeatTimesSec.Take(MediaAnalysisJson.MaxItems).Select(Round).ToList();
        double analysedSec = Round(track.DurationSec);
        double durationSec = fullDurationSec > 0 ? Round(fullDurationSec) : analysedSec;
        AnalysisBeatsResult result = new(
            Bpm: Round2(track.Bpm),
            BeatSec: Round(track.BeatSec),
            BeatPhaseSec: Round(track.BeatPhaseSec),
            BarPhaseSec: Round(track.BarPhaseSec),
            BeatsPerBar: track.BeatsPerBar,
            BeatTimesSec: beatTimes,
            FirstDownbeatIndex: track.FirstDownbeatIndex,
            DurationSec: durationSec,
            AnalysedSec: analysedSec,
            DropSec: track.DropSec is { } drop ? Round(drop) : null,
            OutroSec: track.OutroSec is { } outro ? Round(outro) : null,
            Confidence: Round(track.Confidence),
            Steady: track.Steady,
            TempoWord: track.TempoWord,
            HalfTempoScore: Round(track.HalfTempoScore),
            DoubleTempoScore: Round(track.DoubleTempoScore),
            Truncated: truncated);
        return new(MediaAnalysisKinds.Beats, true,
            MediaAnalysisJson.Serialize(result),
            MediaAnalysisJson.Serialize(new { bpm = Round2(track.Bpm), confidence = Round(track.Confidence), beatCount = track.BeatTimesSec.Count, tempoWord = track.TempoWord }),
            null, null, false);
    }

    private async Task<IReadOnlyList<(double StartSec, double EndSec)>> SilenceForChunkingAsync(string localPath, double durationSec, CancellationToken ct)
    {
        try
        {
            return await silence.DetectAsync(localPath, SilenceThresholdDb, MinSilenceSeconds, durationSec, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Silence for ASR chunking failed; chunks will be cut at fixed points");
            return [];
        }
    }

    private async Task<MediaAnalysisKindOutcome> TranscriptAsync(
        string localPath, double fullDurationSec, double? heldSeconds, IReadOnlyList<(double StartSec, double EndSec)> silenceSpans, string? language, CancellationToken ct)
    {
        // The charge was held for heldSeconds of audio: never transcribe (and so never bill) more than that.
        bool trimmed = heldSeconds is { } held && held > 0 && fullDurationSec > held + 1;
        double durationSec = trimmed ? heldSeconds!.Value : fullDurationSec;
        ResolvedTranscriptionProvider? provider;
        try
        {
            provider = await providers.ResolveTranscriptionAsync(null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transcription provider resolution failed");
            provider = null;
        }

        if (provider is null) return Fail(MediaAnalysisKinds.Transcript, NoProvider);

        string wav = Path.Combine(Path.GetDirectoryName(localPath)!, "analysis-audio.wav");
        try
        {
            // Always bound by the held seconds (-t), whatever the probed duration says: a header that
            // under-reports must not get extra audio transcribed unbilled.
            if (heldSeconds is { } cap && cap > 0)
                await audio.ExtractWavRangeAsync(localPath, wav, 0, cap, ct).ConfigureAwait(false);
            else
                await audio.ExtractWavAsync(localPath, wav, ct).ConfigureAwait(false);
            long wavBytes = new FileInfo(wav).Length;
            IReadOnlyList<(double StartSec, double EndSec)> chunks =
                TranscriptChunkPlanner.PlanChunks(durationSec, wavBytes, silenceSpans, MaxAsrChunkBytes);

            ITranscriptionClient client = transcription.Get(provider);
            List<TranscriptSegment> segments = [];
            List<TranscriptWord> words = [];
            if (chunks.Count <= 1)
            {
                TranscriptResult one = await TranscribeWithRetryAsync(client, wav, language, ct).ConfigureAwait(false);
                segments.AddRange(one.Segments);
                words.AddRange(one.Words);
            }
            else
            {
                int index = 0;
                foreach ((double startSec, double endSec) in chunks)
                {
                    string chunkPath = Path.Combine(Path.GetDirectoryName(localPath)!, $"analysis-chunk-{index++}.wav");
                    await audio.ExtractWavRangeAsync(wav, chunkPath, startSec, endSec, ct).ConfigureAwait(false);
                    TranscriptResult part = await TranscribeWithRetryAsync(client, chunkPath, language, ct).ConfigureAwait(false);
                    segments.AddRange(part.Segments.Select(s => s with { StartSec = s.StartSec + startSec, EndSec = s.EndSec + startSec }));
                    words.AddRange(part.Words.Select(w => w with { StartSec = w.StartSec + startSec, EndSec = w.EndSec + startSec }));
                }
            }

            return TranscriptOutcome(segments, words, language, durationSec);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transcription failed");
            return Fail(MediaAnalysisKinds.Transcript, TranscriptionFailed);
        }
    }

    private static async Task<TranscriptResult> TranscribeWithRetryAsync(ITranscriptionClient client, string wavPath, string? language, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= AsrMaxAttempts; attempt++)
        {
            try
            {
                // A fresh stream per attempt: the clients dispose what they are handed.
                await using FileStream stream = new(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return await client.TranscribeAsync(stream, "audio.wav", language, wordTimestamps: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < AsrMaxAttempts)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("Transcription failed.");
    }

    /// <summary>
    /// Measured words come straight from the recogniser. When it returned segments only, each segment's words are spread
    /// over the segment in proportion to their length and flagged <c>estimated</c> (the editor shows them as approximate).
    /// </summary>
    public static MediaAnalysisKindOutcome TranscriptOutcome(
        IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<TranscriptWord> measured, string? language, double durationSec)
    {
        List<AnalysisWord> all = [];
        if (measured.Count > 0)
        {
            all.AddRange(measured.Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .Select(w => new AnalysisWord(w.Text.Trim(), Round(w.StartSec), Round(Math.Max(w.EndSec, w.StartSec)), false)));
        }
        else
        {
            foreach (TranscriptSegment segment in segments)
            {
                string[] parts = (segment.Text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                int totalChars = parts.Sum(p => p.Length);
                if (parts.Length == 0 || totalChars == 0) continue;
                double span = Math.Max(0, segment.EndSec - segment.StartSec);
                double cursor = segment.StartSec;
                foreach (string part in parts)
                {
                    double end = cursor + span * part.Length / totalChars;
                    all.Add(new AnalysisWord(part, Round(cursor), Round(end), true));
                    cursor = end;
                }
            }
        }

        bool truncated = all.Count > MediaAnalysisJson.MaxWords;
        int estimated = all.Count(w => w.Estimated);
        return new(MediaAnalysisKinds.Transcript, true,
            MediaAnalysisJson.Serialize(new AnalysisTranscriptResult(language, all.Take(MediaAnalysisJson.MaxWords).ToList(), truncated)),
            MediaAnalysisJson.Serialize(new { wordCount = all.Count, estimatedWordCount = estimated, measuredWordCount = all.Count - estimated, language }),
            null, durationSec, false);
    }

    private static double Round(double value) => Math.Round(value, 3);

    private static double Round2(double value) => Math.Round(value, 2);
}
