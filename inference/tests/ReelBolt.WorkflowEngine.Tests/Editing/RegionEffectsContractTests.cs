using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>MediaClip.regionEffects: wire round trip, validator refusals, binding, flatten passthrough, asset references.</summary>
public class RegionEffectsContractTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid StepA = Guid.Parse("3c1f0a52-9d4e-4b7a-8e21-6a5b4c3d2e10");
    private static readonly Guid StepB = Guid.Parse("3c1f0a52-9d4e-4b7a-8e21-6a5b4c3d2e11");
    private static readonly AssetRef Asset = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static ShapeMask Mask(Guid? step = null, bool invert = false) => new("segmentation", Invert: invert, AnalysisStepResultId: step ?? StepA, TrackIds: ["g1"], FeatherPx: 2);

    private static RegionClipEffect Effect(string word = "blur", double strength = 0.5, Guid? step = null) => new(word, strength, Mask(step));

    private static MediaClip Clip(IReadOnlyList<RegionClipEffect>? effects, bool stabilize = false) =>
        new(Guid.Parse("11111111-1111-4111-8111-111111111112"), "clip", 0, 30, Asset, 0, 1.0, true, null, null, null, null, false, false, 0, 0, null, null, null, null, stabilize, RegionEffects: effects);

    private static EditTimeline Doc(MediaClip clip) =>
        new(Guid.NewGuid(), ProjectId, "doc", Settings,
            [new Track(Guid.Parse("22222222-0000-4000-8000-000000000000"), TrackKind.Video, "V1", null, 64, false, true, false, false, null, [clip])], [], null, null, null);

    private static EditTimelineValidationContext Context(SegmentationTrackFacts? facts = null)
    {
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> ids = new Dictionary<Guid, IReadOnlySet<string>> { [StepA] = new HashSet<string> { "g1" } };
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, SegmentationTrackFacts>>? byResult = facts is null
            ? null
            : new Dictionary<Guid, IReadOnlyDictionary<string, SegmentationTrackFacts>> { [StepA] = new Dictionary<string, SegmentationTrackFacts> { ["g1"] = facts } };
        return new EditTimelineValidationContext(ProjectId, new Dictionary<AssetRef, long> { [Asset] = 1000 }, new Dictionary<Guid, Guid> { [Asset.ProjectFileId!.Value] = ProjectId }, new Dictionary<Guid, Guid>(), 30, ids, byResult);
    }

    private static IReadOnlyList<string> Paths(MediaClip clip, SegmentationTrackFacts? facts = null) =>
        EditTimelineValidator.Validate(Doc(clip), Context(facts)).Violations.Select(v => v.Path).ToList();

    [Fact]
    public void Round_trips_in_camel_case_and_validates()
    {
        MediaClip clip = Clip([Effect("colorpop", 0.75)]);
        string json = JsonSerializer.Serialize(Doc(clip), EditTimelineJson.Options);
        json.Should().Contain("\"regionEffects\"").And.Contain("\"colorpop\"").And.Contain("\"strength\"");
        MediaClip back = (MediaClip)JsonSerializer.Deserialize<EditTimeline>(json, EditTimelineJson.Options)!.Tracks![0].Items![0];
        back.RegionEffects.Should().ContainSingle().Which.Mask.TrackIds.Should().Equal("g1");
        Paths(back).Should().BeEmpty();
    }

    [Fact]
    public void Refuses_count_word_strength_and_non_segmentation_mask()
    {
        Paths(Clip(Enumerable.Range(0, 5).Select(_ => Effect()).ToList())).Should().Contain("tracks[0].items[0].regionEffects");
        Paths(Clip([Effect("sparkle")])).Should().Contain("tracks[0].items[0].regionEffects[0].effect");
        Paths(Clip([Effect(strength: 1.1)])).Should().Contain("tracks[0].items[0].regionEffects[0].strength");
        Paths(Clip([Effect(strength: double.NaN)])).Should().Contain("tracks[0].items[0].regionEffects[0].strength");
        Paths(Clip([new RegionClipEffect("blur", 0.5, new ShapeMask())])).Should().Contain("tracks[0].items[0].regionEffects[0].mask");
        Paths(Clip([Effect(step: StepB)])).Should().Contain("tracks[0].items[0].regionEffects[0].mask.analysisStepResultId");
    }

    [Fact]
    public void Refuses_stabilize_together_with_region_effects()
    {
        Paths(Clip([Effect()], stabilize: true)).Should().Contain("tracks[0].items[0].stabilize");
        Paths(Clip(null, stabilize: true)).Should().BeEmpty();
    }

    [Fact]
    public void Runs_the_mask_binding_for_each_entry()
    {
        var wrongAsset = new SegmentationTrackFacts("g1", new AssetRef(AssetRefKind.ProjectFile, Guid.NewGuid(), null), false, 10, "upright-1");
        Paths(Clip([Effect()]), wrongAsset).Should().Contain("tracks[0].items[0].regionEffects[0].mask.trackIds");
        var trimmed = new SegmentationTrackFacts("g1", Asset, true, 10, "upright-1");
        Paths(Clip([Effect()]), trimmed).Should().Contain("tracks[0].items[0].regionEffects[0].mask.trackIds");
        Paths(Clip([Effect()]), new SegmentationTrackFacts("g1", Asset, false, 10, "upright-1")).Should().BeEmpty();
    }

    [Fact]
    public void Asset_references_collect_region_effect_masks_with_the_clip_mask()
    {
        MediaClip clip = Clip([Effect(step: StepB), Effect(step: StepA)]);
        TimelineAssetReferences.SegmentationMaskStepResultIds(Doc(clip)).Should().BeEquivalentTo([StepA, StepB]);
    }

    [Fact]
    public void Flatten_keeps_region_effects_on_a_trimmed_clip()
    {
        MediaClip clip = Clip([Effect("outline", 0.2)]);
        EditTimeline flat = TimelineFlattener.Flatten(Doc(clip));
        MediaClip? kept = flat.Tracks![0].Items!.OfType<MediaClip>().FirstOrDefault();
        kept.Should().NotBeNull();
        kept!.RegionEffects.Should().ContainSingle().Which.Effect.Should().Be("outline");
    }
}
