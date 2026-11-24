using System.Text.RegularExpressions;
using ReelBolt.Shared.Workflows.Follow;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Asks the compile to follow one tracked subject: a track id from the VideoAnalyze artifact
/// (<c>r{n}</c> local feature track, <c>g{n}</c> segmentation track) plus enum words. The compile
/// turns it into ordinary <see cref="VideoReframe"/> spans; every number comes from the artifact
/// and the follow core.
/// </summary>
public sealed record FollowSubjectRequest(
    string TrackId,
    int? SourceIndex = null,
    double? FromSec = null,
    double? ToSec = null,
    FollowTightness Tightness = FollowTightness.Medium,
    FollowSmoothness Smoothness = FollowSmoothness.Normal,
    FollowOnLost OnLost = FollowOnLost.Hold,
    bool AllowSoftZoom = false)
{
    /// <summary>The most follow requests one compile may carry.</summary>
    public const int MaxRequests = 8;

    // The analysis numbers both families from 0 (r0 is the first feature track, g0 the first segmentation
    // track), so 0 is a valid id; a leading zero on a longer number is not.
    private static readonly Regex TrackIdPattern = new("^[rg](0|[1-9][0-9]{0,3})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="id"/> looks like <c>r{n}</c> or <c>g{n}</c> (n from 0).</summary>
    public static bool IsTrackId(string? id) => id is not null && TrackIdPattern.IsMatch(id);

    /// <summary>Whether two requests' ranges overlap (null FromSec = the clip's start, null ToSec = its end).</summary>
    public static bool RangesOverlap(FollowSubjectRequest a, FollowSubjectRequest b) =>
        (a.FromSec ?? 0) < (b.ToSec ?? double.PositiveInfinity) - 1e-9 && (b.FromSec ?? 0) < (a.ToSec ?? double.PositiveInfinity) - 1e-9;

    /// <summary>
    /// The config-only checks (no artifact needed): count, id shape, range, Reencode, and one owner per
    /// source against hand-written reframes. Null when fine.
    /// </summary>
    public static VideoReframeValidation? Check(VideoCompileStepConfig config)
    {
        if (config.FollowSubject is not { Count: > 0 } follow)
            return null;
        if (follow.Count > MaxRequests)
            return VideoReframeValidation.Error(FollowSubjectCodes.TooMany, $"{follow.Count} followSubject entries; at most {MaxRequests}.");
        for (int i = 0; i < follow.Count; i++)
        {
            FollowSubjectRequest? f = follow[i];
            if (f is null || !IsTrackId(f.TrackId))
                return VideoReframeValidation.Error(FollowSubjectCodes.TrackIdInvalid, $"FollowSubject {i}: TrackId must look like r0 or g1 (a track id from the analysis).");
            if (f.SourceIndex is < 0)
                return VideoReframeValidation.Error(FollowSubjectCodes.RangeInvalid, $"FollowSubject {i}: SourceIndex must not be negative.");
            if (f.FromSec is { } from && (!double.IsFinite(from) || from < 0))
                return VideoReframeValidation.Error(FollowSubjectCodes.RangeInvalid, $"FollowSubject {i}: FromSec must be a non-negative number of seconds.");
            if (f.ToSec is { } to && (!double.IsFinite(to) || to < 0))
                return VideoReframeValidation.Error(FollowSubjectCodes.RangeInvalid, $"FollowSubject {i}: ToSec must be a non-negative number of seconds.");
            if (f.FromSec is { } fromCap && fromCap > SubjectCameraPath.MaxRangeSeconds || f.ToSec is { } toCap && toCap > SubjectCameraPath.MaxRangeSeconds)
                return VideoReframeValidation.Error(FollowSubjectCodes.RangeInvalid, $"FollowSubject {i}: FromSec and ToSec must be at most {SubjectCameraPath.MaxRangeSeconds} seconds.");
            if (f.FromSec is { } a && f.ToSec is { } b && a >= b)
                return VideoReframeValidation.Error(FollowSubjectCodes.RangeInvalid, $"FollowSubject {i}: ToSec must be after FromSec.");
            if (!Enum.IsDefined(f.Tightness) || !Enum.IsDefined(f.Smoothness) || !Enum.IsDefined(f.OnLost))
                return VideoReframeValidation.Error(FollowSubjectCodes.RangeInvalid, $"FollowSubject {i}: Tightness, Smoothness or OnLost is not a known value.");
        }

        for (int i = 0; i < follow.Count; i++)
        {
            for (int j = i + 1; j < follow.Count; j++)
            {
                // Only entries that name the same source can be told to overlap here; one without a SourceIndex
                // is resolved against the artifact later (the expander checks it then).
                if (follow[i].SourceIndex is { } si && follow[j].SourceIndex == si && RangesOverlap(follow[i], follow[j]))
                    return VideoReframeValidation.Error(FollowSubjectCodes.Overlap, $"FollowSubject {i} and {j} follow source {si} over overlapping ranges; give them disjoint FromSec/ToSec.");
            }
        }

        if (config.Mode == VideoCompileMode.StreamCopy)
            return VideoReframeValidation.Error(FollowSubjectCodes.RequiresReencode, "FollowSubject requires Mode=Reencode - following a subject has no stream-copy equivalent.");

        if (config.Reframes is { Count: > 0 } hand)
        {
            HashSet<int> handSources = hand.Where(r => r is not null).Select(r => r.SourceIndex).ToHashSet();
            if (follow.FirstOrDefault(f => f.SourceIndex is { } s && handSources.Contains(s)) is { } clash)
                return VideoReframeValidation.Error(FollowSubjectCodes.ConflictsReframe, $"Source {clash.SourceIndex} has both a FollowSubject entry and a hand-written Reframes entry; one owner per source.");
        }

        return null;
    }
}

/// <summary>Failure codes of <see cref="FollowSubjectRequest"/>. Config ones are the customer's to fix.</summary>
public static class FollowSubjectCodes
{
    public const string TooMany = "FOLLOW_TOO_MANY";
    public const string TrackIdInvalid = "FOLLOW_TRACK_ID_INVALID";
    public const string RangeInvalid = "FOLLOW_RANGE_INVALID";
    public const string RequiresReencode = "FOLLOW_REQUIRES_REENCODE";
    public const string ConflictsReframe = "FOLLOW_CONFLICTS_REFRAME";
    public const string TrackNotFound = "FOLLOW_TRACK_NOT_FOUND";
    public const string TrackEmpty = "FOLLOW_TRACK_EMPTY";
    public const string Overlap = "FOLLOW_OVERLAP";

    /// <summary>Every code above, for the failure classifier.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        TooMany, TrackIdInvalid, RangeInvalid, RequiresReencode, ConflictsReframe, TrackNotFound, TrackEmpty, Overlap
    ];
}
