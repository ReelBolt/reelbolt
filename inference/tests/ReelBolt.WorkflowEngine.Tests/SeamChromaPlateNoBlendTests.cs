using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

using ResolvedSpan = VideoCompileStepExecutor.ResolvedSpan;

/// <summary>
/// Regression test: found live in the finished alpha launch film, at the cut from the portal
/// hero-render shot into the desktop-monitor shot that carries the tracked insert.
///
/// A chroma-plate shot's raw content is a saturated green rectangle, and what makes it
/// presentable is the insert composited over it. An insert is only ever composited over frames
/// belonging wholly to its own clip — <c>OutputTimeline.ClipAwayTransitionOverlaps</c> trims its
/// window off any blended seam, correctly, since the warp and the chroma matte are both solved
/// against that clip and mean nothing on a frame that is half another shot.
///
/// Both behaviours are individually right and together produced a visible defect: an overlapping
/// treatment blended INTO the plate shot across exactly the frames where the insert was
/// deliberately absent. Measured on the shipped file, the monitor's screen ramped from
/// RGB (5,25,49) to (0,230,0) over about half a second and then snapped to (16,15,16) — a flash
/// of raw green screen with the previous shot ghosted over it, at a shot change.
///
/// The fix keeps overlapping treatments away from those seams entirely, so a plate shot always
/// starts on a hard cut with its insert composited from its first frame.
/// </summary>
public class SeamChromaPlateNoBlendTests
{
    private static VideoCompileStepConfig Config(VideoTransitionPolicy policy = VideoTransitionPolicy.Auto) => new(
        Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1,
        TransitionPolicy: policy, MaxTransitionRatioPct: 100);

    /// <summary>Facts that select an OVERLAPPING treatment (source change => R5 dissolve).</summary>
    private static SeamFacts DissolveFacts(int seamIndex = 0) =>
        new(seamIndex, SameSource: false, null, "s0", "s1", false, null, null, LookJump: true,
            false, false, false, null, null, null, null, null, null, false);

    private static List<ResolvedSpan> TwoSpans() =>
    [
        new(0, 10, 0, 10, 0, 0),
        new(0, 10, 0, 10, 0, 0)
    ];

    [Fact]
    public void Without_plate_information_an_overlapping_treatment_is_still_chosen()
    {
        // Establishes the baseline the fix must not disturb.
        IReadOnlyList<SeamPlan> plan = SeamTransitionPlanner.Plan(
            [DissolveFacts()], TwoSpans(), Config(), xfadeAvailable: true, out _);

        plan[0].OverlapSec.Should().BeGreaterThan(0, "this seam would otherwise blend");
    }

    [Theory]
    [InlineData(0)] // the plate is the OUTGOING span
    [InlineData(1)] // the plate is the INCOMING span — the launch film's own case
    public void A_seam_touching_a_plate_span_never_blends(int plateSpanIndex)
    {
        IReadOnlyList<SeamPlan> plan = SeamTransitionPlanner.Plan(
            [DissolveFacts()], TwoSpans(), Config(), xfadeAvailable: true, out _,
            new HashSet<int> { plateSpanIndex });

        plan[0].OverlapSec.Should().Be(0, "blending exposes the un-composited plate");
        plan[0].Treatment.Should().Be(SeamTreatment.AudioOnly);
        plan[0].XfadeTransition.Should().BeEmpty();
        plan[0].Rule.Should().Be("R0:chromaPlateNoBlend");
    }

    [Fact]
    public void A_seam_away_from_the_plate_keeps_its_blend()
    {
        // Three spans, plate on the last one: seam 0 (spans 0-1) is untouched, seam 1 (spans 1-2)
        // is demoted. A plate must not cost the whole programme its transitions.
        List<ResolvedSpan> spans = [new(0, 10, 0, 10, 0, 0), new(0, 10, 0, 10, 0, 0), new(0, 10, 0, 10, 0, 0)];

        IReadOnlyList<SeamPlan> plan = SeamTransitionPlanner.Plan(
            [DissolveFacts(0), DissolveFacts(1)], spans, Config(), xfadeAvailable: true, out _,
            new HashSet<int> { 2 });

        plan[0].OverlapSec.Should().BeGreaterThan(0, "this seam is nowhere near the plate");
        plan[1].OverlapSec.Should().Be(0);
    }

    [Fact]
    public void An_already_non_overlapping_seam_is_left_exactly_as_it_was()
    {
        SeamFacts audioOnly = new(0, true, null, "s0", "s1", SameShot: true, null, null, false,
            CutOutStill: true, CutInStill: true, false, null, null, null, null, null, null, false);

        IReadOnlyList<SeamPlan> untouched = SeamTransitionPlanner.Plan(
            [audioOnly], TwoSpans(), Config(), xfadeAvailable: true, out _);
        IReadOnlyList<SeamPlan> withPlate = SeamTransitionPlanner.Plan(
            [audioOnly], TwoSpans(), Config(), xfadeAvailable: true, out _, new HashSet<int> { 1 });

        withPlate[0].Should().Be(untouched[0], "a seam that never blended needs no demotion, rule included");
    }

    // ---- span/region mapping ----

    private static VideoInsertRegionTrack Region(int sourceIndex, double startSec, double endSec) =>
        new("r0", "s0", startSec, endSec, [], 0.9, 0.2, 1.8, "Static", "green", sourceIndex);

    private static VideoAnalysisArtifact ArtifactWith(params VideoInsertRegionTrack[] regions) =>
        new(Version: 2,
            Media: new VideoAnalysisMedia(10, 30, 1, 1920, 1080),
            Shots: [], SilenceSpans: [], Segments: [], Words: [], OfferedIds: [],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            InsertRegions: regions);

    [Fact]
    public void A_span_is_plate_bearing_only_when_the_region_is_on_its_own_source_and_overlaps_it()
    {
        List<ResolvedSpan> spans =
        [
            new(0, 5, 0, 5, 0, 0) { SourceIndex = 0 },
            new(0, 5, 0, 5, 0, 0) { SourceIndex = 9 }
        ];

        IReadOnlySet<int> plates = VideoCompileStepExecutor.BuildPlateBearingSpanIndices(
            ArtifactWith(Region(sourceIndex: 9, startSec: 0, endSec: 5)), spans);

        plates.Should().BeEquivalentTo([1], "only the span from source 9 carries the plate");
    }

    [Fact]
    public void A_region_on_the_same_source_but_a_cut_away_window_is_not_counted()
    {
        List<ResolvedSpan> spans = [new(0, 5, 0, 5, 0, 0) { SourceIndex = 9 }];

        IReadOnlySet<int> plates = VideoCompileStepExecutor.BuildPlateBearingSpanIndices(
            ArtifactWith(Region(sourceIndex: 9, startSec: 20, endSec: 25)), spans);

        plates.Should().BeEmpty("the plate is in a part of the clip this cut never uses");
    }

    [Fact]
    public void No_regions_means_no_plate_spans_and_therefore_no_behaviour_change()
    {
        List<ResolvedSpan> spans = [new(0, 5, 0, 5, 0, 0) { SourceIndex = 0 }];

        VideoCompileStepExecutor.BuildPlateBearingSpanIndices(ArtifactWith(), spans).Should().BeEmpty();
    }
}
