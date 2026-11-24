namespace ReelBolt.WorkflowEngine.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

/// <summary>
/// Pins <see cref="TimelineManifestBuilder"/>'s per-track provenance stamping: every
/// agent-authored track carries the REAL author supplied by <see cref="TimelineProvenanceContext"/>
/// (never a label inferred from which fields happen to be populated — issue #123 §1), the
/// cuts-vs-colour-grade precedence rule holds, and the deterministic tracks (inserts, dialogue)
/// stay null no matter what the context says.
///
/// <para>
/// The builder itself stays pure: it is handed the context and does no I/O. Resolving that
/// context from the execution's step graph is <c>VideoCompileStepExecutor</c>'s job and is pinned
/// by <c>VideoCompileStepExecutorTests</c>.
/// </para>
/// </summary>
public class VideoTimelineProvenanceTests
{
    private static readonly Guid DecisionDefinitionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GraphicsDefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MusicDefinitionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SfxDefinitionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid GradeDefinitionId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static readonly Guid DecisionStepResultId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid GraphicsStepResultId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid MusicStepResultId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid SfxStepResultId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    private static readonly Guid GradeStepResultId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");

    [Fact]
    public void Full_context_stamps_each_agent_authored_track_with_its_own_provenance()
    {
        TimelineManifest manifest = BuildManifest(
            decision: Decision("s0", "s1"),
            provenance: FullContext());

        var videoItem = manifest.Tracks.Single(t => t.Type == "video").Items.Single();
        videoItem.AgentName.Should().Be("VideoStoryEditor");
        videoItem.ProviderName.Should().Be("decision-provider");
        videoItem.StepResultId.Should().Be(DecisionStepResultId);

        var graphicsItem = manifest.Tracks.Single(t => t.Type == "graphics").Items.Single();
        graphicsItem.AgentName.Should().Be("MotionGraphicsPlanner");
        graphicsItem.ProviderName.Should().Be("graphics-provider");
        graphicsItem.StepResultId.Should().Be(GraphicsStepResultId);

        var musicItem = manifest.Tracks.Single(t => t.Type == "music").Items.Single();
        musicItem.AgentName.Should().Be("MusicSupervisor");
        musicItem.ProviderName.Should().Be("music-provider");
        musicItem.StepResultId.Should().Be(MusicStepResultId);

        var sfxItems = manifest.Tracks.Single(t => t.Type == "sfx").Items;
        sfxItems.Should().HaveCount(2);
        sfxItems.Should().OnlyContain(i => i.AgentName == "SoundDesigner");
        sfxItems.Should().OnlyContain(i => i.ProviderName == "sfx-provider");
        sfxItems.Should().OnlyContain(i => i.StepResultId == SfxStepResultId);

        // Each track names its OWN author — nothing bleeds across tracks.
        graphicsItem.AgentName.Should().NotBe(videoItem.AgentName);
        musicItem.AgentName.Should().NotBe(videoItem.AgentName);
        sfxItems[0].AgentName.Should().NotBe(videoItem.AgentName);
    }

    [Fact]
    public void Null_context_leaves_every_provenance_field_null()
    {
        TimelineManifest manifest = BuildManifest(decision: Decision("s0", "s1"), provenance: null);

        foreach (TimelineTrack track in manifest.Tracks)
        {
            foreach (TimelineItem item in track.Items)
            {
                item.AgentName.Should().BeNull();
                item.ProviderName.Should().BeNull();
                item.StepResultId.Should().BeNull();
            }
        }

        // And the provenance keys are absent from the serialized manifest entirely, so a
        // pre-provenance consumer sees a byte-identical payload.
        string json = JsonSerializer.Serialize(manifest, SerializerOptions());
        json.Should().NotContain("\"agentName\"");
        json.Should().NotContain("\"providerName\"");
        json.Should().NotContain("\"stepResultId\"");
    }

    [Fact]
    public void ColorGrade_only_context_never_stamps_an_ungraded_item_but_does_stamp_a_graded_one()
    {
        // ONLY the colour-grade slot is populated: the cuts track has no provenance at all, so
        // this is exactly the case the precedence rule has to get right.
        var gradeOnly = new TimelineProvenanceContext(
            ColorGrade: new TimelineItemProvenance(
                GradeDefinitionId, "Colorist", "grade-provider", GradeStepResultId));

        TimelineManifest ungraded = BuildManifest(decision: Decision("s0", "s1"), provenance: gradeOnly);
        var ungradedItem = ungraded.Tracks.Single(t => t.Type == "video").Items.Single();
        ungradedItem.ColorGrade.Should().BeNull("no grade plan was supplied, so this cut is ungraded");
        ungradedItem.AgentName.Should().BeNull();
        ungradedItem.ProviderName.Should().BeNull();
        ungradedItem.StepResultId.Should().BeNull();

        TimelineManifest graded = BuildManifest(
            decision: Decision("s0", "s1"), colorGrade: GradedPlan(), provenance: gradeOnly);
        var gradedItem = graded.Tracks.Single(t => t.Type == "video").Items.Single();
        gradedItem.ColorGrade.Should().NotBeNull();
        gradedItem.AgentName.Should().Be("Colorist");
        gradedItem.ProviderName.Should().Be("grade-provider");
        gradedItem.StepResultId.Should().Be(GradeStepResultId);

        // A "None" look is a declined grade, not a graded item — the grade provenance must not
        // land on it either.
        TimelineManifest declined = BuildManifest(
            decision: Decision("s0", "s1"),
            colorGrade: new ColorGradePlanOutput { Look = "None" },
            provenance: gradeOnly);
        declined.Tracks.Single(t => t.Type == "video").Items.Single().AgentName
            .Should().BeNull("a declined grade authored nothing");
    }

    [Fact]
    public void Cuts_provenance_wins_over_color_grade_on_a_graded_item()
    {
        TimelineManifest manifest = BuildManifest(
            decision: Decision("s0", "s1"), colorGrade: GradedPlan(), provenance: FullContext());

        var item = manifest.Tracks.Single(t => t.Type == "video").Items.Single();
        item.ColorGrade.Should().NotBeNull();
        item.AgentName.Should().Be("VideoStoryEditor", "the CUT is the item's primary author");
        item.ProviderName.Should().Be("decision-provider");
        item.StepResultId.Should().Be(DecisionStepResultId);

        // The two provenances are never merged into one item.
        item.AgentName.Should().NotBe("Colorist");
    }

    [Fact]
    public void Insert_and_dialogue_items_stay_null_even_with_a_full_context()
    {
        TimelineManifest manifest = BuildManifest(
            decision: Decision("s0", "s1"),
            segments: new[] { ("t0", 1.0, 3.0) },
            inserts: new[] { "r0" },
            provenance: FullContext());

        manifest.Tracks.Should().Contain(t => t.Type == "inserts");
        manifest.Tracks.Should().Contain(t => t.Type == "dialogue");

        foreach (TimelineItem item in manifest.Tracks.Single(t => t.Type == "inserts").Items)
        {
            item.AgentName.Should().BeNull();
            item.ProviderName.Should().BeNull();
            item.StepResultId.Should().BeNull();
        }

        foreach (TimelineItem item in manifest.Tracks.Single(t => t.Type == "dialogue").Items)
        {
            item.AgentName.Should().BeNull();
            item.ProviderName.Should().BeNull();
            item.StepResultId.Should().BeNull();
        }
    }

    [Fact]
    public void A_partially_resolved_provenance_keeps_the_facts_it_has()
    {
        // StepResultId and ProviderName are independent facts: their absence must not erase the
        // author's name (see TimelineItemProvenance).
        var partial = new TimelineProvenanceContext(
            Decision: new TimelineItemProvenance(DecisionDefinitionId, "VideoStoryEditor", null, null));

        var item = BuildManifest(decision: Decision("s0", "s1"), provenance: partial)
            .Tracks.Single(t => t.Type == "video").Items.Single();

        item.AgentName.Should().Be("VideoStoryEditor");
        item.ProviderName.Should().BeNull();
        item.StepResultId.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // B8b — the trial output policy is the one non-track slot
    // ------------------------------------------------------------------

    [Fact]
    public void Output_policy_reaches_the_manifest_output_metadata()
    {
        // The output policy is not an agent-authored track, so it never lands on an ITEM: it is
        // stamped once onto the manifest's own output metadata.
        var policy = new TimelineOutputPolicy(
            Tier: "trial",
            MaxHeight: 720,
            HeightCapApplied: true,
            OutputHeight: 720,
            WatermarkEntitled: true,
            WatermarkApplied: false,
            WatermarkSkippedReason: "watermark_asset_not_configured");

        TimelineManifest manifest = BuildManifest(
            decision: Decision("s0", "s1"),
            provenance: new TimelineProvenanceContext(OutputPolicy: policy));

        manifest.OutputVideo.OutputPolicy.Should().Be(policy);
        manifest.Tracks.SelectMany(t => t.Items).Should().OnlyContain(i => i.AgentName == null,
            "the policy is not an author and must not be stamped on any item");
    }

    [Fact]
    public void Null_output_policy_is_absent_from_the_serialized_manifest()
    {
        // A run with no plan (no organization) must serialize byte-identically to the pre-B8b
        // payload — so the key is absent, not null.
        TimelineManifest manifest = BuildManifest(decision: Decision("s0", "s1"), provenance: null);

        manifest.OutputVideo.OutputPolicy.Should().BeNull();
        JsonSerializer.Serialize(manifest, SerializerOptions()).Should().NotContain("outputPolicy");
    }

    // ------------------------------------------------------------------
    // Fixture helpers
    // ------------------------------------------------------------------

    private static TimelineProvenanceContext FullContext() => new(
        Decision: new TimelineItemProvenance(DecisionDefinitionId, "VideoStoryEditor", "decision-provider", DecisionStepResultId),
        Graphics: new TimelineItemProvenance(GraphicsDefinitionId, "MotionGraphicsPlanner", "graphics-provider", GraphicsStepResultId),
        Music: new TimelineItemProvenance(MusicDefinitionId, "MusicSupervisor", "music-provider", MusicStepResultId),
        Sfx: new TimelineItemProvenance(SfxDefinitionId, "SoundDesigner", "sfx-provider", SfxStepResultId),
        ColorGrade: new TimelineItemProvenance(GradeDefinitionId, "Colorist", "grade-provider", GradeStepResultId));

    private static ColorGradePlanOutput GradedPlan() => new()
    {
        Look = "Filmic",
        Strength = "Subtle",
        ShadowTone = "Neutral",
        HighlightTone = "Neutral",
        Reason = "test"
    };

    private static VideoEditDecisionOutput Decision(string fromId, string toId) => new()
    {
        Keep = new List<VideoEditKeepSpan>
        {
            new() { FromId = fromId, ToId = toId, Reason = "keep" }
        },
        EditRationale = "test"
    };

    private static TimelineManifest BuildManifest(
        VideoEditDecisionOutput? decision,
        TimelineProvenanceContext? provenance = null,
        ColorGradePlanOutput? colorGrade = null,
        (string Id, double Start, double End)[]? segments = null,
        string[]? inserts = null)
    {
        segments ??= Array.Empty<(string, double, double)>();

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(
                RequestedStart: 0.0,
                RequestedEnd: 10.0,
                SnappedStart: 0.0,
                SnappedEnd: 10.0,
                StartFrame: 0,
                EndFrame: 0,
                SourceIndex: 0,
                OriginKeepFromIds: new[] { "s0" })
        };

        var artifact = new VideoAnalysisArtifact(
            Version: 1,
            Media: new VideoAnalysisMedia(60.0, 30, 1, 1920, 1080),
            Shots: Array.Empty<VideoAnalysisShot>(),
            SilenceSpans: Array.Empty<VideoAnalysisSilenceSpan>(),
            Segments: segments.Select(s => new VideoAnalysisSegment(s.Id, null, s.Start, s.End, "text")).ToList(),
            Words: Array.Empty<VideoAnalysisWord>(),
            OfferedIds: Array.Empty<string>(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false));

        var seams = VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count);
        OutputTimeline timeline = OutputTimeline.Build(spans, seams);

        return TimelineManifestBuilder.Build(
            projectId: Guid.NewGuid(),
            executionId: Guid.NewGuid(),
            artifact: artifact,
            resolvedSpans: spans,
            timeline: timeline,
            outputStorageKey: "projects/p/output.mp4",
            width: 1920,
            height: 1080,
            fps: 30.0,
            overlays: new[] { Overlay("p0") },
            inserts: inserts?.Select(Insert).ToList(),
            music: Music(),
            sfx: new[] { Sfx("x0"), Sfx("x1") },
            colorGrade: colorGrade,
            seamPlans: seams,
            decision: decision,
            provenance: provenance);
    }

    private static ResolvedOverlay Overlay(string placementId) => new(
        PlacementId: placementId,
        Kind: "LowerThird",
        SanitizedText: "Hello",
        SanitizedSubtext: "World",
        DurationMs: 2000,
        Emphasis: "Normal",
        OutputStartSec: 1.0,
        OutputEndSec: 3.0,
        Rect: new VideoAnalysisRect(0.1, 0.7, 0.4, 0.2),
        TextColor: "white");

    private static ResolvedScreenInsert Insert(string regionId) => new(
        RegionId: regionId,
        LocalAssetPath: "/tmp/insert.webm",
        OutputStartSec: 0.0,
        OutputEndSec: 5.0,
        Keyframes: new[] { new InsertQuadFrame(0, 0, 0, 1, 0, 1, 1, 0, 1) },
        FpsNum: 30,
        FpsDen: 1);

    private static ResolvedMusic Music() => new(
        TrackId: "m0",
        ProjectFileId: Guid.NewGuid(),
        TrackName: "Bed",
        LocalPath: "/tmp/music.mp3",
        TrackDurationSec: 30.0,
        OutputDurationSec: 10.0,
        PlayEndSec: 10.0,
        LoopInput: false,
        BedGainLinear: 0.5,
        DuckGainLinear: 0.25,
        RampSec: 0.5,
        FadeInSec: 0.0,
        FadeOutSec: 0.0,
        LiftWindows: Array.Empty<MusicLiftWindow>());

    private static ResolvedSfxCue Sfx(string sfxId) => new(
        SfxId: sfxId,
        AnchorId: "s0",
        ProjectFileId: Guid.NewGuid(),
        ClipName: "Whoosh",
        LocalPath: "/tmp/sfx.wav",
        OutputStartSec: 0.5,
        PlayDurationSec: 0.4,
        GainLinear: 0.5,
        FadeOutSec: 0.05,
        Timing: "OnCut");

    private static JsonSerializerOptions SerializerOptions() => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
