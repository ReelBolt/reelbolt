using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="ShotKeyframeResolver"/>: the pure half of the frame path. An opaque offered id becomes
/// one stored object and one moment — or a stated refusal — and nothing here touches ffmpeg, the object
/// store or a provider.
/// </summary>
/// <remarks>
/// The rules this file holds down, in the order they matter:
/// <list type="number">
/// <item>only an id that was actually OFFERED resolves — the offer, not the artifact, is the
/// authorization;</item>
/// <item>only a SHOT has a frame range, so a silence gap or a transcript segment that happens to be
/// offered as an anchor is not a frame;</item>
/// <item>a last frame is the shot's last CONTAINED frame, one frame-length before its exclusive
/// end;</item>
/// <item>the pair is all-or-nothing, so a clip never opens on one frame and closes on nothing.</item>
/// </list>
/// </remarks>
public class ShotKeyframeResolverTests
{
    private const string SourceKey = "projects/p/userFiles/e/source.mp4";

    /// <summary>A still photo's rendered clip — what a <c>VideoAnalyze</c> still source really is.</summary>
    private const string StillKey = "projects/p/agentFiles/video-analysis/e/step-1-src0-still.mp4";

    [Fact]
    public void An_offered_shot_id_resolves_to_its_sources_object_and_the_shots_start()
    {
        VideoAnalysisArtifact artifact = Artifact(SourceKey, [new VideoAnalysisShot("s1", 4, 9)]);

        ShotKeyframeResolution resolution = ShotKeyframeResolver.Resolve(artifact, Offer("s1"), "s1", null);

        resolution.Resolved.Should().BeTrue();
        resolution.Reason.Should().BeNull();
        resolution.First.Should().Be(new ShotFrameTarget("s1", SourceKey, 4));
        resolution.Last.Should().BeNull();
    }

    [Fact]
    public void An_id_that_was_never_offered_resolves_to_nothing_and_says_so()
    {
        // The artifact DOES hold s1 at [4,9): the refusal is about the offer, not about the artifact,
        // which is the "not merely in the artifact — in the set actually shown to the model" rule.
        VideoAnalysisArtifact artifact = Artifact(SourceKey, [new VideoAnalysisShot("s1", 4, 9)]);

        ShotKeyframeResolution resolution = ShotKeyframeResolver.Resolve(artifact, Offer("s2"), "s1", null);

        resolution.Resolved.Should().BeFalse();
        resolution.Requested.Should().BeTrue();
        resolution.Reason.Should().Be(ShotKeyframeReasons.Unresolved);
        resolution.First.Should().BeNull();
    }

    [Fact]
    public void An_id_the_artifact_cannot_place_is_unresolved_even_when_it_was_offered()
    {
        // "s2" was offered but the artifact has no shot with that id in it (a truncated or legacy
        // artifact). Guessing a moment for it would put an unplanned frame on a paid request.
        VideoAnalysisArtifact artifact = Artifact(SourceKey, [new VideoAnalysisShot("s1", 4, 9)]);

        ShotKeyframeResolver.Resolve(artifact, Offer("s2"), "s2", null).Reason
            .Should().Be(ShotKeyframeReasons.Unresolved);
    }

    [Fact]
    public void An_offered_id_that_is_not_a_shot_is_not_a_frame()
    {
        // A silence gap is a perfectly good ANCHOR and a nonsense frame: it has no shot range, so it
        // resolves to nothing rather than to the shot that happens to contain it.
        VideoAnalysisArtifact artifact = Artifact(SourceKey, [new VideoAnalysisShot("s1", 4, 9)]) with
        {
            SilenceSpans = [new VideoAnalysisSilenceSpan("g1", 4.5, 5.0)]
        };

        ShotKeyframeResolver.Resolve(artifact, Offer("s1", "g1"), "g1", null).Reason
            .Should().Be(ShotKeyframeReasons.Unresolved);
    }

    [Fact]
    public void A_last_frame_is_the_shots_last_contained_frame_not_its_exclusive_end()
    {
        // EndSec is exclusive and is where the NEXT shot begins, so seeking there would hand the
        // provider the following shot's first frame. At 30 fps the last contained frame is one
        // thirtieth of a second earlier.
        VideoAnalysisArtifact artifact = Artifact(SourceKey, [new VideoAnalysisShot("s1", 4, 9)]);

        ShotKeyframeResolution resolution = ShotKeyframeResolver.Resolve(artifact, Offer("s1"), null, "s1");

        resolution.Last!.AtSec.Should().BeApproximately(9 - (1.0 / 30.0), 1e-9);
        resolution.First.Should().BeNull();
    }

    [Fact]
    public void The_frame_length_comes_from_the_sources_own_rational_rate()
    {
        // 30000/1001 is the one rate where a rounded double would drift: the last frame must be one
        // EXACT frame earlier, not one 1/30th.
        VideoAnalysisArtifact artifact = Artifact(
            SourceKey, [new VideoAnalysisShot("s1", 0, 10)], new VideoAnalysisMedia(10, 30000, 1001, 1920, 1080));

        ShotKeyframeResolution resolution = ShotKeyframeResolver.Resolve(artifact, Offer("s1"), null, "s1");

        resolution.Last!.AtSec.Should().BeApproximately(10 - (1001.0 / 30000.0), 1e-9);
    }

    [Fact]
    public void A_shot_shorter_than_one_frame_clamps_to_its_start()
    {
        ShotKeyframeResolver.LastFrameSec(startSec: 7, endSec: 7.001, media: null).Should().Be(7,
            "seeking before a shot's start would cut a frame that is not in it");
    }

    [Fact]
    public void A_still_photos_offered_beat_resolves_like_any_other_shot()
    {
        // A still image the project holds is a VideoAnalyze SOURCE: the analyzer renders it into a
        // short clip and declares one scene per beat, so its beats are ordinary "s{n}" ids. The frame
        // path therefore needs nothing still-specific — the still's own rendered clip is what gets
        // staged and cut.
        VideoAnalysisArtifact artifact = Artifact(StillKey, [new VideoAnalysisShot("s1", 0, 1.5)]);

        ShotKeyframeResolution resolution = ShotKeyframeResolver.Resolve(artifact, Offer("s1"), "s1", "s1");

        resolution.First.Should().Be(new ShotFrameTarget("s1", StillKey, 0));
        resolution.Last!.StorageKey.Should().Be(StillKey);
        resolution.Last!.AtSec.Should().BeApproximately(1.5 - (1.0 / 30.0), 1e-9);
    }

    [Fact]
    public void Both_frames_come_back_in_plan_order_when_both_resolve()
    {
        VideoAnalysisArtifact artifact = Artifact(
            SourceKey,
            [new VideoAnalysisShot("s1", 0, 4), new VideoAnalysisShot("s2", 6, 10)]);

        ShotKeyframeResolution resolution = ShotKeyframeResolver.Resolve(artifact, Offer("s1", "s2"), "s1", "s2");

        resolution.First!.ShotId.Should().Be("s1");
        resolution.Last!.ShotId.Should().Be("s2");
    }

    [Fact]
    public void One_unreadable_id_refuses_the_whole_pair()
    {
        // All-or-nothing: a clip that opened on a frame while closing on nothing would be a different
        // shot from the one that was planned.
        VideoAnalysisArtifact artifact = Artifact(SourceKey, [new VideoAnalysisShot("s1", 0, 4)]);

        ShotKeyframeResolution resolution = ShotKeyframeResolver.Resolve(artifact, Offer("s1"), "s1", "s9");

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Be(ShotKeyframeReasons.Unresolved);
        resolution.First.Should().BeNull();
        resolution.Last.Should().BeNull();
    }

    [Fact]
    public void A_shot_whose_source_entry_is_missing_is_unresolved()
    {
        VideoAnalysisArtifact artifact = Artifact(SourceKey, [new VideoAnalysisShot("s1", 0, 4)]) with
        {
            Sources = [],
        };

        ShotKeyframeResolver.Resolve(artifact, Offer("s1"), "s1", null).Reason
            .Should().Be(ShotKeyframeReasons.Unresolved);
    }

    [Fact]
    public void A_shot_whose_source_has_no_storage_key_is_unresolved()
    {
        VideoAnalysisArtifact artifact = Artifact(SourceKey, [new VideoAnalysisShot("s1", 0, 4)]) with
        {
            Sources = [new VideoAnalysisSourceInfo(0, "", new VideoAnalysisMedia(10, 30, 1, 1920, 1080))],
        };

        ShotKeyframeResolver.Resolve(artifact, Offer("s1"), "s1", null).Reason
            .Should().Be(ShotKeyframeReasons.Unresolved);
    }

    [Fact]
    public void A_shot_that_names_no_frame_resolves_to_nothing_at_all()
    {
        VideoAnalysisArtifact artifact = Artifact(SourceKey, [new VideoAnalysisShot("s1", 0, 4)]);

        ShotKeyframeResolver.Resolve(artifact, Offer("s1"), null, null).Should().Be(ShotKeyframeResolution.None);
        ShotKeyframeResolver.Resolve(artifact, Offer("s1"), "   ", null).Should().Be(ShotKeyframeResolution.None,
            "a whitespace-only id is not something a caller can resolve to a real frame");
    }

    [Fact]
    public void An_artifact_that_was_never_loaded_resolves_nothing()
    {
        ShotKeyframeResolver.Resolve(null, null, "s1", null).Reason.Should().Be(ShotKeyframeReasons.Unresolved);
    }

    [Fact]
    public void Ids_are_matched_after_trimming_and_never_case_folded()
    {
        // A plan is JSON a model wrote: a trailing space is noise a human would not see, and the
        // artifact's ids are "s{n}" by construction. Case is NOT folded, because two ids differing
        // only in case are two different ids.
        VideoAnalysisArtifact artifact = Artifact(SourceKey, [new VideoAnalysisShot("s1", 0, 4)]);

        ShotKeyframeResolver.Resolve(artifact, Offer("s1"), " s1 ", null).Resolved.Should().BeTrue();
        ShotKeyframeResolver.Resolve(artifact, Offer("s1"), "S1", null).Resolved.Should().BeFalse();
    }

    private static IReadOnlySet<string> Offer(params string[] ids) => new HashSet<string>(ids, StringComparer.Ordinal);

    private static VideoAnalysisArtifact Artifact(
        string sourceStorageKey, IReadOnlyList<VideoAnalysisShot> shots, VideoAnalysisMedia? media = null) =>
        new(
            Version: 2,
            Media: media ?? new VideoAnalysisMedia(30, 30, 1, 1920, 1080),
            Shots: shots,
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: shots.Select(s => s.Id).ToArray(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Sources: [new VideoAnalysisSourceInfo(0, sourceStorageKey, media ?? new VideoAnalysisMedia(30, 30, 1, 1920, 1080))]);
}
