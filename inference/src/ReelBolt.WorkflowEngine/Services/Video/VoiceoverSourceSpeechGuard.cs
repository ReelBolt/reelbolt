using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>The decision of <see cref="VoiceoverSourceSpeechGuard.Evaluate"/>.</summary>
/// <param name="Case"><c>narratedFilm</c> (source sound dropped), <c>speechUnderLines</c> (warning only) or null (not applied).</param>
public sealed record VoiceoverSourceSpeechGuardResult(
    string? Case, double? SpeechCoverage, double Threshold, string Reason)
{
    public bool MuteSource => Case == VoiceoverSourceSpeechGuard.NarratedFilmCase;
    public bool Applied => Case is not null;

    public string? WarningCode => Case switch
    {
        VoiceoverSourceSpeechGuard.NarratedFilmCase => "voiceover_source_narration_muted",
        VoiceoverSourceSpeechGuard.SpeechUnderLinesCase => "voiceover_source_speech_under_lines",
        _ => null
    };

    public JsonObject ToNode() => new()
    {
        ["applied"] = Applied,
        ["case"] = Case,
        ["speechCoverage"] = SpeechCoverage is { } c ? Math.Round(c, 3) : null,
        ["threshold"] = Threshold,
        ["reason"] = Reason
    };
}

/// <summary>
/// Stops a voiceover laid over a source that already speaks (VoiceoverMode OverMusic, source sound
/// kept). A rendered film that is mostly speech has its own sound dropped (as MuteSourceAudio);
/// any other source with speech under the lines only raises a warning. Pure; the speech facts are
/// the analysis artifact's transcript segments, else the per-shot audio descriptors minus the
/// detected silences. Thresholds are uncalibrated (see docs/voiceover.md).
/// </summary>
public static class VoiceoverSourceSpeechGuard
{
    public const string NarratedFilmCase = "narratedFilm";
    public const string SpeechUnderLinesCase = "speechUnderLines";

    /// <summary>A shot counts as speech when its measured speech ratio is at least this.</summary>
    internal const double ShotSpeechRatioFloor = 0.5;

    public static VoiceoverSourceSpeechGuardResult Evaluate(
        VideoCompileStepConfig config,
        VideoAnalysisArtifact artifact,
        IReadOnlyList<int> usedSourceIndices,
        OutputTimeline timeline,
        IReadOnlyList<(double Start, double End)> lineWindows,
        VoiceoverGuardOptions options)
    {
        double narratedThreshold = Math.Clamp(options.NarratedFilmSpeechCoverage, 0, 1);
        double underLinesThreshold = Math.Clamp(options.SpeechUnderLinesCoverage, 0, 1);
        VoiceoverSourceSpeechGuardResult None(string reason, double? coverage = null) => new(null, coverage, narratedThreshold, reason);

        if (!config.EnableVoiceover || config.VoiceoverMode != VoiceoverMode.OverMusic || config.MuteSourceAudio || lineWindows.Count == 0)
            return None("not_applicable");
        if (artifact.Sources is not { Count: > 0 } sources || usedSourceIndices.Count == 0 || timeline.TotalSec <= 0)
            return None("speech_facts_unreadable");

        var speechOut = new List<(double Start, double End)>();
        bool allFilms = true;
        foreach (int idx in usedSourceIndices)
        {
            VideoAnalysisSourceInfo? info = sources.FirstOrDefault(s => s.SourceIndex == idx);
            if (info is null)
                return None("speech_facts_unreadable");
            if (info.ProjectFileId is not null || info.ClipId is not null)
                allFilms = false;

            List<(double Start, double End)>? speech = SourceSpeech(artifact, idx);
            if (speech is null)
                return None("speech_facts_unreadable");
            foreach ((double s, double e) in speech)
                speechOut.AddRange(timeline.MapWindowToOutputAll(s, e, idx));
        }

        List<(double Start, double End)> speechUnion = Union(speechOut, timeline.TotalSec);
        double programCoverage = Length(speechUnion) / timeline.TotalSec;

        if (allFilms && programCoverage >= narratedThreshold)
            return new(NarratedFilmCase, programCoverage, narratedThreshold, "source_is_narrated_film");

        List<(double Start, double End)> lines = Union(lineWindows, double.MaxValue);
        double lineTime = Length(lines);
        if (lineTime > 0)
        {
            double under = Length(Intersect(speechUnion, lines));
            double underCoverage = under / lineTime;
            if (underCoverage >= underLinesThreshold)
                return new(SpeechUnderLinesCase, underCoverage, underLinesThreshold, "source_speech_under_lines");
        }

        return None("below_threshold", programCoverage);
    }

    private static List<(double Start, double End)>? SourceSpeech(VideoAnalysisArtifact artifact, int idx)
    {
        var result = new List<(double Start, double End)>();
        var segs = artifact.Segments?.Where(s => s.SourceIndex == idx && s.EndSec > s.StartSec).ToList() ?? [];
        if (segs.Count > 0)
        {
            result.AddRange(segs.Select(s => (s.StartSec, s.EndSec)));
            return result;
        }

        var shots = artifact.Shots?.Where(s => s.SourceIndex == idx).ToList() ?? [];
        if (shots.Count == 0 || shots.Any(s => s.Audio is null))
            return null;
        var silences = artifact.SilenceSpans?.Where(s => s.SourceIndex == idx).ToList() ?? [];
        foreach (VideoAnalysisShot shot in shots)
        {
            if (shot.Audio!.SpeechRatio < ShotSpeechRatioFloor)
                continue;
            double cursor = shot.StartSec;
            foreach (VideoAnalysisSilenceSpan sil in silences.OrderBy(s => s.StartSec))
            {
                double ss = Math.Max(sil.StartSec, shot.StartSec), se = Math.Min(sil.EndSec, shot.EndSec);
                if (se <= ss)
                    continue;
                if (ss > cursor)
                    result.Add((cursor, ss));
                cursor = Math.Max(cursor, se);
            }
            if (shot.EndSec > cursor)
                result.Add((cursor, shot.EndSec));
        }
        return result;
    }

    private static List<(double Start, double End)> Union(IEnumerable<(double Start, double End)> input, double max)
    {
        var merged = new List<(double Start, double End)>();
        foreach ((double s, double e) in input.Select(w => (Math.Max(0, w.Start), Math.Min(max, w.End))).Where(w => w.Item2 > w.Item1).OrderBy(w => w.Item1))
        {
            if (merged.Count > 0 && s <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
            else
                merged.Add((s, e));
        }
        return merged;
    }

    private static List<(double Start, double End)> Intersect(List<(double Start, double End)> a, List<(double Start, double End)> b)
    {
        var r = new List<(double Start, double End)>();
        foreach (var x in a)
            foreach (var y in b)
            {
                double s = Math.Max(x.Start, y.Start), e = Math.Min(x.End, y.End);
                if (e > s)
                    r.Add((s, e));
            }
        return r;
    }

    private static double Length(List<(double Start, double End)> w) => w.Sum(x => x.End - x.Start);
}
