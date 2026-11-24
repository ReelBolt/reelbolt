using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>
/// Whether a sound effect anchored to a shot (not to a detected moment) would land on someone speaking. Pure.
/// A cue anchored to an event id is never judged here: the event is the reason for the sound.
/// </summary>
public static class SfxSpeechGuard
{
    /// <summary>A source whose transcript covers at least this share of it counts as speech footage.</summary>
    public const double SpeechSourceRatio = 0.4;

    /// <summary>The share of a source's length its transcript tokens cover (0 when unknown).</summary>
    public static double SpeechRatio(VideoAnalysisArtifact artifact, int sourceIndex, double sourceDurationSec)
    {
        if (sourceDurationSec <= 0)
            return 0;
        double covered = 0;
        double end = double.MinValue;
        foreach (VideoAnalysisSegment s in artifact.Segments.Where(s => s.SourceIndex == sourceIndex).OrderBy(s => s.StartSec))
        {
            double start = Math.Max(s.StartSec, end);
            if (s.EndSec > start)
                covered += s.EndSec - start;
            end = Math.Max(end, s.EndSec);
        }

        return Math.Min(1.0, covered / sourceDurationSec);
    }

    /// <summary>True when the source time falls inside a transcript token of that source.</summary>
    public static bool InsideSpeech(VideoAnalysisArtifact artifact, int sourceIndex, double sec) =>
        artifact.Segments.Any(s => s.SourceIndex == sourceIndex && sec >= s.StartSec && sec < s.EndSec);

    /// <summary>True when a shot-anchored cue at this source moment should be dropped as <c>sfx_over_speech</c>.</summary>
    public static bool ShouldDrop(VideoAnalysisArtifact artifact, int sourceIndex, double anchorSec, double sourceDurationSec) =>
        InsideSpeech(artifact, sourceIndex, anchorSec) ||
        SpeechRatio(artifact, sourceIndex, sourceDurationSec) >= SpeechSourceRatio;
}
