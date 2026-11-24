#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The contracts ticket of the "prompted region effects" initiative: the shapes every later ticket
/// codes against, and above all the promise that NONE of them changes the behaviour of a step that
/// does not opt in. A not-configured <c>VideoAnalyze</c>/<c>VideoCompile</c> must still behave exactly
/// as it did — proved here against the exact stored JSON a pre-change workflow carries.
/// </summary>
public class PromptedRegionEffectsContractTests
{
    /// <summary>The product's own options for a step config (<c>VideoAnalyzeStepExecutor.ConfigJsonOptions</c>).</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] SegmentationConfigKeys =
    [
        "segmentationProviderId",
        "allowSourceMediaEgress",
        "maxSegmentationSeconds",
        "segmentationDetectionThreshold",
        "maxSegmentationObjects",
        "segmentationTimeoutSeconds"
    ];

    private static JsonObject Node(object value) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, Json))!.AsObject();

    /// <summary>Rewrites a serialized config into the exact document a workflow stored BEFORE this change.</summary>
    private static string StoredWithoutNewFields(VideoAnalyzeStepConfig config)
    {
        JsonObject node = Node(config);
        foreach (string key in SegmentationConfigKeys) node.Remove(key);
        if (node["trackObjects"] is JsonArray targets)
        {
            foreach (JsonObject target in targets.OfType<JsonObject>()) target.Remove("method");
        }

        return node.ToJsonString();
    }

    // ---- VideoAnalyzeStepConfig: old payloads keep their behaviour ---------------------------------

    [Fact]
    public void An_analyze_config_stored_before_segmentation_deserializes_to_todays_behaviour()
    {
        VideoAnalyzeStepConfig before = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile),
            TrackObjects: [new ObjectTrackTarget("licence plate", 1.5)]);

        string stored = StoredWithoutNewFields(before);
        stored.Should().NotContain("egmentation", "the fixture must look like a payload written before this ticket")
            .And.NotContain("\"method\"");

        VideoAnalyzeStepConfig parsed = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(stored, Json)!;

        parsed.TrackObjects!.Should().ContainSingle();
        parsed.TrackObjects![0].Method.Should().Be(ObjectTrackMethod.Feature,
            "a stored target carrying no method must follow the free feature tracker, exactly as before");
        parsed.SegmentationProviderId.Should().BeNull();
        parsed.AllowSourceMediaEgress.Should().BeFalse("consent is opt-in; an old payload never gave it");
        parsed.MaxSegmentationSeconds.Should().Be(60);
        parsed.SegmentationDetectionThreshold.Should().Be(0.5);
        parsed.MaxSegmentationObjects.Should().Be(4);
        parsed.SegmentationTimeoutSeconds.Should().Be(900);

        // Nothing else moved: re-serializing the parsed config and dropping the new members gives back
        // the stored document byte for byte, which is the strongest form of "no behaviour change" this
        // contract can assert without pretending the product re-serializes configs (it does not).
        JsonObject roundTripped = Node(parsed);
        foreach (string key in SegmentationConfigKeys) roundTripped.Remove(key);
        foreach (JsonObject target in roundTripped["trackObjects"]!.AsArray().OfType<JsonObject>()) target.Remove("method");
        JsonNode.DeepEquals(roundTripped, JsonNode.Parse(stored)).Should().BeTrue(
            roundTripped.ToJsonString() + " should equal " + stored);
    }

    [Fact]
    public void A_segmentation_target_and_its_knobs_round_trip()
    {
        VideoAnalyzeStepConfig config = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile),
            TrackObjects:
            [
                new ObjectTrackTarget(
                    "speedometer",
                    AtSec: 0.5,
                    Region: new VideoInsertSurfaceQuad(0.1, 0.1, 0.4, 0.1, 0.1, 0.3, 0.4, 0.3),
                    Method: ObjectTrackMethod.Segmentation)
            ],
            SegmentationProviderId: Guid.Parse("55555555-5555-5555-5555-555555555555"),
            AllowSourceMediaEgress: true,
            MaxSegmentationSeconds: 120,
            SegmentationDetectionThreshold: 0.2,
            MaxSegmentationObjects: 8,
            SegmentationTimeoutSeconds: 300);

        VideoAnalyzeStepConfig parsed = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(
            JsonSerializer.Serialize(config, Json), Json)!;

        parsed.Should().BeEquivalentTo(config);
        parsed.TrackObjects![0].Method.Should().Be(ObjectTrackMethod.Segmentation);
        parsed.TrackObjects![0].Region!.X1.Should().Be(0.4);
        parsed.AllowSourceMediaEgress.Should().BeTrue();
        parsed.MaxSegmentationSeconds.Should().Be(120);

        // Persisted by NAME, like every other step-config enum.
        JsonSerializer.Serialize(config, Json).Should().Contain("\"method\":\"Segmentation\"");
    }

    [Fact]
    public void Segmentation_limits_are_one_place_and_clamp_as_documented()
    {
        SegmentationLimits.ClampSeconds(0).Should().Be(1);
        SegmentationLimits.ClampSeconds(601).Should().Be(600);
        SegmentationLimits.ClampObjects(0).Should().Be(1);
        SegmentationLimits.ClampObjects(99).Should().Be(16);
        SegmentationLimits.ClampDetectionThreshold(0).Should().Be(0.01);
        SegmentationLimits.ClampDetectionThreshold(double.NaN).Should().Be(0.5);
        SegmentationLimits.ClampProviderTimeout(5).Should().Be(60);
        SegmentationLimits.ClampProviderTimeout(99_999).Should().Be(3600);
    }

    [Fact]
    public void An_explicit_segmentation_provider_is_a_real_provider_reference()
    {
        Guid providerId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        string json = JsonSerializer.Serialize(new VideoAnalyzeStepConfig(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile),
            SegmentationProviderId: providerId), Json);

        IReadOnlyList<StepProviderReference> references =
            StepProviderReferences.Extract(StepType.VideoAnalyze, videoAnalyzeConfigJson: json);

        StepProviderReference reference = references
            .Should().ContainSingle(r => r.Capability == InferenceProviderCapability.VideoSegmentation).Subject;
        reference.ProviderId.Should().Be(providerId);
        reference.Field.Should().Be("segmentationProviderId");

        // No explicit id: no reference at all. The capability DEFAULT is still fingerprinted by the
        // fingerprint's own capability loop, and nothing extra is validated at save time.
        StepProviderReferences.Extract(
                StepType.VideoAnalyze,
                videoAnalyzeConfigJson: "{\"version\":1,\"source\":{\"kind\":\"ProjectFile\"}}")
            .Should().NotContain(r => r.Capability == InferenceProviderCapability.VideoSegmentation);
    }

    // ---- VideoCompileStepConfig: RegionEffects ----------------------------------------------------

    [Fact]
    public void A_compile_config_stored_before_region_effects_deserializes_with_none()
    {
        VideoCompileStepConfig before = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Previous),
            AnalysisStepOrder: 1);

        JsonObject node = Node(before);
        node.Remove("regionEffects");

        VideoCompileStepConfig parsed = JsonSerializer.Deserialize<VideoCompileStepConfig>(node.ToJsonString(), Json)!;

        parsed.RegionEffects.Should().BeNull("no entry means no mask is ever downloaded and no filter chain built");
        RegionEffectRules.Validate(parsed.RegionEffects, parsed.Mode).Should().BeEmpty();
    }

    [Fact]
    public void Region_effects_round_trip_with_their_enum_member_names()
    {
        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Previous),
            AnalysisStepOrder: 1,
            RegionEffects:
            [
                new VideoRegionEffect("g1", RegionEffect.Blur, Strength: 1.4, FeatherPx: 8, Opacity: 0.9),
                new VideoRegionEffect("speedometer", RegionEffect.ColorPop, Invert: true)
            ]);

        string json = JsonSerializer.Serialize(config, Json);
        json.Should().Contain("\"effect\":\"Blur\"").And.Contain("\"effect\":\"ColorPop\"");

        VideoCompileStepConfig parsed = JsonSerializer.Deserialize<VideoCompileStepConfig>(json, Json)!;
        parsed.Should().BeEquivalentTo(config);
        parsed.RegionEffects!.Should().HaveCount(2);
        parsed.RegionEffects![1].Invert.Should().BeTrue();
        parsed.RegionEffects![0].FeatherPx.Should().Be(8);
    }

    // ---- RegionEffectRules: one specific code per rule --------------------------------------------

    [Fact]
    public void Nine_effects_are_refused_not_truncated()
    {
        List<VideoRegionEffect> effects = Enumerable.Range(0, RegionEffectRules.MaxEffects + 1)
            .Select(i => new VideoRegionEffect($"g{i}", RegionEffect.Blur))
            .ToList();

        IReadOnlyList<RegionEffectViolation> violations =
            RegionEffectRules.Validate(effects, VideoCompileMode.Reencode);

        violations.Should().ContainSingle();
        violations[0].Code.Should().Be(RegionEffectRules.LimitExceededCode);
        violations[0].Message.Should().Contain("9 entries").And.Contain("at most 8");
    }

    [Fact]
    public void Each_broken_entry_names_its_own_rule_and_index()
    {
        List<VideoRegionEffect> effects =
        [
            new("   ", RegionEffect.Blur),
            new(new string('x', RegionEffectRules.MaxTargetChars + 1), RegionEffect.Blur),
            new("g3", (RegionEffect)99),
            new("g4", RegionEffect.Pixelate, Strength: 2.5),
            new("g5", RegionEffect.Outline, FeatherPx: 65),
            new("g6", RegionEffect.Highlight, Opacity: -0.1)
        ];

        IReadOnlyList<RegionEffectViolation> violations =
            RegionEffectRules.Validate(effects, VideoCompileMode.Reencode);

        violations.Select(v => v.Code).Should().Equal(
            RegionEffectRules.TargetInvalidCode,
            RegionEffectRules.TargetInvalidCode,
            RegionEffectRules.InvalidCode,
            RegionEffectRules.StrengthOutOfRangeCode,
            RegionEffectRules.FeatherOutOfRangeCode,
            RegionEffectRules.OpacityOutOfRangeCode);

        violations[2].Message.Should().Contain("RegionEffects[2]").And.Contain("ColorPop");
        violations[4].Message.Should().Contain("64");
    }

    [Fact]
    public void Stream_copy_refuses_effects_rather_than_publishing_an_un_effected_picture()
    {
        RegionEffectRules.Validate([new VideoRegionEffect("g1", RegionEffect.Blur)], VideoCompileMode.StreamCopy)
            .Should().ContainSingle()
            .Which.Code.Should().Be(RegionEffectRules.RequiresReencodeCode);

        RegionEffectRules.Validate(
                [new VideoRegionEffect("g1", RegionEffect.Blur)], VideoCompileMode.Reencode)
            .Should().BeEmpty();
        RegionEffectRules.Validate(null, VideoCompileMode.StreamCopy).Should().BeEmpty("no effects is the default");
    }

    // ---- The artifact: its own g{n} collection ----------------------------------------------------

    private static VideoAnalysisArtifact Artifact(VideoSegmentationTrack? track) => new(
        Version: 2,
        Media: new VideoAnalysisMedia(6.0, 24, 1, 960, 540),
        Shots: [],
        SilenceSpans: [],
        Segments: [],
        Words: [],
        OfferedIds: ["s1"],
        Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, TranscriptionApplied: false, TranscriptionDegraded: false)
        {
            SegmentationPrompts = track is null ? null : [track.Prompt]
        },
        SegmentationTracks: track is null ? null : [track]);

    private static VideoSegmentationTrack Track() => new(
        Id: "g1",
        SourceIndex: 0,
        Prompt: "speedometer",
        StartSec: 0.5,
        EndSec: 5.5,
        FrameCount: 120,
        Fps: 24,
        Coverage: 0.06,
        Confidence: 0.93,
        MaskStorageKey: "projects/11111111-1111-1111-1111-111111111111/agentFiles/segmentation/abc.mkv",
        MaskContentHash: new string('a', 64),
        Model: "fal-ai/sam-3-1/video-rle",
        ProviderFingerprint: "55555555-5555-5555-5555-555555555555:FalAi:fal-ai/sam-3-1/video-rle",
        BoxKeyframes: [new VideoSegmentationBoxFrame(0.5, 0.4, 0.4, 0.2, 0.1)],
        Facts: new VideoSegmentationFacts(EmptyFrames: 4, LongestGapFrames: 3, DetectionThreshold: 0.2, Truncated: false));

    [Fact]
    public void An_artifact_stored_before_segmentation_deserializes_with_no_tracks()
    {
        JsonObject node = Node(Artifact(null));
        node.Remove("segmentationTracks");
        node["provenance"]!.AsObject().Remove("segmentationPrompts");

        VideoAnalysisArtifact parsed = JsonSerializer.Deserialize<VideoAnalysisArtifact>(node.ToJsonString(), Json)!;

        parsed.SegmentationTracks.Should().BeNull();
        parsed.Provenance.SegmentationPrompts.Should().BeNull();
        parsed.OfferedIds.Should().Equal("s1");
    }

    [Fact]
    public void A_populated_segmentation_track_round_trips_with_its_mask_key_and_hash()
    {
        VideoAnalysisArtifact artifact = Artifact(Track());

        VideoAnalysisArtifact parsed = JsonSerializer.Deserialize<VideoAnalysisArtifact>(
            JsonSerializer.Serialize(artifact, Json), Json)!;

        parsed.Should().BeEquivalentTo(artifact);
        VideoSegmentationTrack track = parsed.SegmentationTracks!.Should().ContainSingle().Subject;
        track.Id.Should().Be("g1");
        track.MaskStorageKey.Should().StartWith("projects/", "every mask key is project-scoped");
        track.MaskContentHash.Should().Be(new string('a', 64));
        track.Facts!.LongestGapFrames.Should().Be(3);
        parsed.Provenance.SegmentationPrompts.Should().Equal("speedometer");
    }

    // ---- Plan entitlement -------------------------------------------------------------------------

    [Fact]
    public void Segmentation_is_gated_with_a_specific_402_that_names_the_plan()
    {
        OrganizationEntitlementSet trial = new() { Tier = "trial", SegmentationAllowed = false };

        PlanGateViolation violation = PlanGate.CheckSegmentation(trial)!;

        violation.Code.Should().Be(PlanGate.NotEntitledCode);
        violation.Feature.Should().Be(PlanGate.SegmentationFeature);
        violation.Feature.Should().Be("segmentation");
        violation.StatusCode.Should().Be(402);
        violation.Message.Should().Contain("trial").And.Contain("Upgrade");

        PlanGate.CheckSegmentation(OrganizationEntitlementSet.Unlimited).Should().BeNull();
    }

    [Fact]
    public void The_shipped_catalog_grants_segmentation_to_every_paid_plan_but_trial()
    {
        IReadOnlyDictionary<string, ReelBolt.Shared.Data.Models.Billing.Plan> plans =
            PlanCatalogSeeder.Load(File.ReadAllText(PlanCatalogSeeder.DefaultPath)).ToDictionary(p => p.Key);

        PlanEntitlements.ToSet(plans["trial"]).SegmentationAllowed.Should().BeFalse();
        PlanEntitlements.ToSet(plans["creator"]).SegmentationAllowed.Should().BeTrue();
        PlanEntitlements.ToSet(plans["pro"]).SegmentationAllowed.Should().BeTrue();
        PlanEntitlements.ToSet(plans["team"]).SegmentationAllowed.Should().BeTrue();
        PlanEntitlements.ToSet(plans["enterprise"]).SegmentationAllowed.Should().BeTrue(
            "an absent key means allowed, which is how every other entitlement default works");

        // A catalog written before the flag existed keeps granting it — the mechanism ships with a
        // default of "allowed", so the per-plan values live in the catalog, not in code.
        PlanEntitlements.ToSet(new ReelBolt.Shared.Data.Models.Billing.Plan { Key = "pro", EntitlementsJson = "{\"roomsAllowed\":true}" })
            .SegmentationAllowed.Should().BeTrue();
    }

    // ---- Schema exposure: the assistant and the web form read this --------------------------------

    [Fact]
    public void The_exported_schemas_describe_the_new_fields_and_the_effect_enum()
    {
        StepConfigSchemaExporter exporter = new();
        string analyze = exporter.GetSchema(StepType.VideoAnalyze).Schema!.ToJsonString();
        string compile = exporter.GetSchema(StepType.VideoCompile).Schema!.ToJsonString();

        analyze.Should().Contain("trackObjects").And.Contain("method")
            .And.Contain("allowSourceMediaEgress")
            .And.Contain("segmentationProviderId")
            .And.Contain("maxSegmentationSeconds")
            .And.Contain("segmentationDetectionThreshold")
            .And.Contain("maxSegmentationObjects")
            .And.Contain("segmentationTimeoutSeconds")
            .And.Contain("Segmentation");

        // The BOUNDS are what a form and the assistant need, and they reach the schema only because
        // each new member carries [property: Description] (-> StepConfigSchemaExporter's
        // TransformSchemaNode). A plain [Description] would land on the constructor parameter and be
        // dropped, so this asserts the lift actually happened.
        analyze.Should().Contain("Clamped 1..600");
        compile.Should().Contain("Capped at 8 entries");

        compile.Should().Contain("regionEffects")
            .And.Contain("target").And.Contain("invert").And.Contain("featherPx").And.Contain("opacity");
        foreach (string member in Enum.GetNames<RegionEffect>())
        {
            compile.Should().Contain(member, $"the effect enum member {member} must be described for the form");
        }
    }

    // ---- Cache discipline -------------------------------------------------------------------------

    [Fact]
    public void The_bumped_revisions_are_the_two_deterministic_video_types()
    {
        ReelBolt.WorkflowEngine.Execution.Caching.DeterministicStepRevisions
            .For(StepType.VideoAnalyze)
            .Should().Be("2026-10-17.insert-smoothing-content-sub-shots-6+prompted-region-effects-1+mask-gap-check-2+reacquire-slots-3+match-scope-4+mask-scope-key-5+source-identity-6+run-brief-source-1+display-rotation-7+display-upright-9+hdr-tonemap-2+analysis-copy-3+untrusted-view-names-1+events-1+outline-1+view-coverage-1+speech-outline-1+edit-moments-1+tap-seed-1",
                "the mask gap self-check, the slot-merged selection, MatchScope and the scope-segmented mask cache all change the analyze step's output for the same inputs");
        ReelBolt.WorkflowEngine.Execution.Caching.DeterministicStepRevisions
            .For(StepType.VideoCompile).Should().Be("2026-10-19.whole-shots-target-length-27+prompted-region-effects-1+reframe-knots-28+mask-gap-check-2+run-brief-length-1+transcript-captions-1+voiceover-speech-guard-1+display-rotation-8+hdr-tonemap-2+stream-copy-reencode-1+vfr-sync-1+media-overlays-1+untrusted-view-names-1+follow-subject-1+reframe-visible-anchor-1+reframe-owns-safe-area-1+region-mask-span-fit-1+explicit-output-fit-1+region-effect-object-radius-1+follow-keep-inside-1+canvas-overlays-1+region-mask-crop-geometry-1+event-moves-1+flash-glow-1+speed-ramps-1+event-cut-1+event-graphics-1+sfx-events-1+speech-safe-cuts-1+speech-coverage-1+overlay-geometry-1+film-quality-2+take-shot-numbering-1+beat-lock-bounded-1",
                "the launch-film-residuals QA-P1-13 reframe-knot geometry, the mask gap self-check, the voiceover source-speech guard, followSubject expansion and visible-anchored reframes all change this step's bytes, so the tag carries every revision");
        ReelBolt.WorkflowEngine.Execution.Caching.DeterministicStepRevisions
            .For(StepType.Voiceover).Should().NotBe("2026-10-18.prompted-region-effects-1",
                "only the two step types whose output can change are bumped");
    }
}
