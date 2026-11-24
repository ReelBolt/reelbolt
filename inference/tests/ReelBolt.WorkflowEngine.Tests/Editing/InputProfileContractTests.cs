using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Color;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>
/// P3a: <see cref="MediaClip.InputProfile"/> — the closed camera-profile word on a media clip, its
/// JSON shape, and its survival through flattening. The contract corpus covers the HTTP verdict;
/// this covers the pieces the corpus cannot reach.
/// </summary>
public class InputProfileContractTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly AssetRef Asset = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static EditTimelineValidationContext Context() =>
        new(ProjectId, new Dictionary<AssetRef, long>(),
            new Dictionary<Guid, Guid> { [Asset.ProjectFileId!.Value] = ProjectId },
            new Dictionary<Guid, Guid>());

    private static MediaClip Clip(string? profile) =>
        new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), "clip", 0, 100, Asset, InputProfile: profile);

    [Fact]
    public void Copy_helpers_keep_the_profile()
    {
        // Positional copies of MediaClip silently drop a trailing field they do not know; this pins the one in the object-effect applier.
        MediaClip copy = ReelBolt.Inference.Api.Services.ObjectAnalysis.ObjectEffectApplier.WithRegionEffects(Clip("sonySLog3"), []);

        copy.InputProfile.Should().Be("sonySLog3");
    }

    [Theory]
    [InlineData("appleLog")]
    [InlineData("sonySLog3")]
    [InlineData("arriLogC4")]
    public void Every_known_profile_id_is_accepted(string profile)
    {
        EditTimeline document = Doc(Track(Clip(profile)));

        EditTimelineValidator.Validate(document, Context()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_null_profile_is_accepted()
    {
        EditTimelineValidator.Validate(Doc(Track(Clip(null))), Context()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("notAProfile")]
    [InlineData("")]
    [InlineData("AppleLog")]
    public void An_unknown_empty_or_mis_cased_profile_is_refused_at_its_path(string profile)
    {
        ValidationResult result = EditTimelineValidator.Validate(Doc(Track(Clip(profile))), Context());

        result.IsValid.Should().BeFalse();
        result.Violations.Should().ContainSingle(v => v.Path == "tracks[0].items[0].inputProfile");
    }

    [Fact]
    public void The_catalogue_is_the_twelve_camera_profiles()
    {
        CameraProfiles.Ids.Should().HaveCount(12);
        EditTimelineValidator.Validate(Doc(Track(Clip(CameraProfiles.Ids[^1]))), Context()).IsValid.Should().BeTrue(
            "the validator reads the same catalogue the field documents");
    }

    [Fact]
    public void A_profile_survives_flattening_a_compound_sequence()
    {
        AssetRef nestedAsset = new(AssetRefKind.ProjectFile, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), null);
        Guid sequenceId = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
        EditSequence sequence = new(sequenceId, "Nested", [
            new Track(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"), TrackKind.Video, "V", null, 64, false, true, false, false, null,
                [new MediaClip(Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"), "nested", 0, 90, nestedAsset, 0, 1.0, true, null, InputProfile: "canonCLog3")])
        ]);
        CompoundClip compound = new(Guid.Parse("99999999-9999-4999-8999-999999999999"), "compound", 100, 160, sequenceId, 30);
        EditTimeline document = DocWithSequences([sequence], Track(compound));

        EditTimeline flat = TimelineFlattener.Flatten(document);

        flat.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Select(c => c.InputProfile)
            .Should().ContainSingle().Which.Should().Be("canonCLog3");
    }

    [Fact]
    public void A_null_profile_is_omitted_from_the_JSON_but_a_known_one_is_written()
    {
        string without = JsonSerializer.Serialize<Item>(Clip(null), EditTimelineJson.Options);
        string with = JsonSerializer.Serialize<Item>(Clip("appleLog"), EditTimelineJson.Options);

        without.Should().NotContain("inputProfile");
        with.Should().Contain("\"inputProfile\":\"appleLog\"");
        JsonSerializer.Deserialize<Item>(with, EditTimelineJson.Options).Should().BeOfType<MediaClip>()
            .Which.InputProfile.Should().Be("appleLog");
    }

    [Fact]
    public void A_multicam_angle_carries_its_profile_onto_every_run_it_is_flattened_into()
    {
        AssetRef assetB = new(AssetRefKind.ProjectFile, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccc01"), null);
        Guid angleA = Guid.Parse("c0000000-0000-4000-8000-000000000001");
        Guid angleB = Guid.Parse("c0000000-0000-4000-8000-000000000002");
        MulticamClip multicam = new(
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1"), "multicam", 0, 100,
            [
                new MulticamAngle(angleA, "Cam A", Asset, 0, "appleLog"),
                new MulticamAngle(angleB, "Cam B", assetB, 15, "sonySLog3"),
            ],
            angleA,
            [new MulticamCut(50, angleB)]);

        IReadOnlyList<MediaClip> runs = TimelineFlattener.ExpandMulticam(multicam);

        runs.Should().HaveCount(2);
        runs[0].InputProfile.Should().Be("appleLog");
        runs[1].InputProfile.Should().Be("sonySLog3");
    }

    [Fact]
    public void A_multicam_angle_without_a_profile_yields_runs_without_one()
    {
        Guid angleId = Guid.Parse("c0000000-0000-4000-8000-000000000003");
        MulticamClip multicam = new(
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2"), "multicam", 0, 100,
            [new MulticamAngle(angleId, "Cam A", Asset)],
            angleId);

        TimelineFlattener.ExpandMulticam(multicam).Should().ContainSingle()
            .Which.InputProfile.Should().BeNull();
    }

    [Fact]
    public void A_multicam_angle_profile_is_omitted_from_the_JSON_when_null()
    {
        MulticamAngle without = new(Guid.Parse("c0000000-0000-4000-8000-000000000004"), "Cam A", Asset);
        MulticamAngle with = without with { InputProfile = "appleLog" };

        JsonSerializer.Serialize(without, EditTimelineJson.Options).Should().NotContain("inputProfile");
        JsonSerializer.Serialize(with, EditTimelineJson.Options).Should().Contain("\"inputProfile\":\"appleLog\"");
    }

    private static Track Track(params Item[] items) =>
        new(Guid.Parse("12121212-1212-4212-8212-121212121212"), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items);

    private static EditTimeline Doc(params Track[] tracks) => DocWithSequences(null, tracks);

    private static EditTimeline DocWithSequences(IReadOnlyList<EditSequence>? sequences, params Track[] tracks) =>
        new(Guid.Parse("22222222-2222-4222-8222-222222222222"), ProjectId, "Contract", Settings, tracks, [], null, null, null, sequences, null);
}
