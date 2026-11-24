using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Editing;

/// <summary>What a binding check needs to know about one analysis track and its source.</summary>
/// <param name="TrackId">The <c>g{n}</c> id.</param>
/// <param name="SourceAsset">The asset the track's analysis source IS, or null when the artifact does not say.</param>
/// <param name="Trimmed">True when the analysis used only a window of its source (its times are then window-relative).</param>
/// <param name="CoverageEndSec">The end, in SOURCE seconds from 0, of the frames the mask video holds.</param>
/// <param name="DecodeGeometry">The analysis source's recorded decode geometry.</param>
public sealed record SegmentationTrackFacts(string TrackId, AssetRef? SourceAsset, bool Trimmed, double CoverageEndSec, string? DecodeGeometry)
{
    /// <summary>The end, in source seconds from 0, of the frames the track's mask video holds (the binding's coverage).</summary>
    public static double CoverageEndOf(VideoSegmentationTrack track) =>
        track.Fps > 0 && track.FrameCount > 0 ? track.FrameCount / track.Fps : track.EndSec;

    /// <summary>Builds the facts of every track of an artifact, by track id.</summary>
    public static IReadOnlyDictionary<string, SegmentationTrackFacts> FromArtifact(VideoAnalysisArtifact? artifact)
    {
        Dictionary<string, SegmentationTrackFacts> result = new(StringComparer.Ordinal);
        foreach (VideoSegmentationTrack track in artifact?.SegmentationTracks ?? [])
        {
            if (string.IsNullOrEmpty(track.Id))
            {
                continue;
            }

            VideoAnalysisSourceInfo? source = (artifact!.Sources ?? []).FirstOrDefault(s => s.SourceIndex == track.SourceIndex);
            AssetRef? asset = source is null ? null
                : source.ProjectFileId is { } file ? new AssetRef(AssetRefKind.ProjectFile, file, null)
                : source.StepResultId is { } step ? new AssetRef(AssetRefKind.StepResult, null, step)
                : null;
            double coverage = CoverageEndOf(track);
            result[track.Id] = new SegmentationTrackFacts(
                track.Id, asset, source is null || source.TrimInSec is not null || source.TrimOutSec is not null, coverage, source?.DecodeGeometry);
        }

        return result;
    }
}

/// <summary>
/// The ONE rule that binds a segmentation mask to the clip it is cut for, used by the API validator (early 400) and the
/// render (DOCUMENT_INVALID). A frame-size match alone is not a binding: the mask must come from THIS clip's source, on
/// the source's own clock, and cover every source frame the clip plays. Anything else would put the effect on the wrong
/// pixels with no error, so it is refused. Messages are plain words and never carry a storage key.
/// </summary>
public static class SegmentationMaskBinding
{
    /// <param name="clip">The masked clip.</param>
    /// <param name="fps">The timeline frame rate (the unit of <see cref="MediaClip.SourceIn"/>); null or non-positive skips the coverage check.</param>
    /// <param name="facts">The track's facts.</param>
    /// <param name="sourceAltersPixelsOnDecode">The staged source's probe flag (render only); null when unknown (validator).</param>
    /// <returns>The refusal, or null when the mask may be drawn on this clip.</returns>
    public static string? Check(MediaClip clip, double? fps, SegmentationTrackFacts facts, bool? sourceAltersPixelsOnDecode = null)
    {
        if (facts.SourceAsset is null || facts.SourceAsset != clip.AssetRef)
        {
            return $"track {facts.TrackId} was cut from a different source than this clip";
        }

        if (facts.Trimmed)
        {
            return $"track {facts.TrackId} comes from an analysis of only part of its source, so its frames do not line up with the clip; re-run the analysis on the whole source";
        }

        if (sourceAltersPixelsOnDecode == true && !string.Equals(facts.DecodeGeometry, VideoAnalysisSourceInfo.UprightGeometry, StringComparison.Ordinal))
        {
            return $"track {facts.TrackId} was analysed before upright tracking and this source is stored rotated or mirrored, so the mask may be flipped; re-run the analysis";
        }

        if (fps is > 0)
        {
            double rate = clip.PlaybackRate > 0 ? clip.PlaybackRate : 1.0;
            double startSec = clip.SourceIn / fps.Value;
            double endSec = startSec + (clip.Hold ? 1.0 / fps.Value : (clip.RecordOut - clip.RecordIn) * rate / fps.Value);
            if (endSec > facts.CoverageEndSec + (1.0 / fps.Value))
            {
                return $"track {facts.TrackId} ends before the clip's source range does, so part of the clip would have no mask";
            }
        }

        return null;
    }
}
