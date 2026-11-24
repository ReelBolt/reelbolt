using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>R08.1 link groups (validator) and R08.2 multicam program audio (flatten + render argv).</summary>
public class EditorLinkedAvMulticamAudioTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly AssetRef AssetB = new(AssetRefKind.ProjectFile, Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), null);
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);
    private static readonly Guid AngleA = Guid.Parse("c0000000-0000-4000-8000-000000000001");
    private static readonly Guid AngleB = Guid.Parse("c0000000-0000-4000-8000-000000000002");

    private static readonly Dictionary<AssetRef, LocalAsset> Assets = new()
    {
        [AssetA] = new LocalAsset("/tmp/a.mp4", "a|100", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
        [AssetB] = new LocalAsset("/tmp/b.mp4", "b|200", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
    };

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    private static MediaClip Clip(int n, long from, long to, AssetRef asset, string? link = null) =>
        new(Id(n), $"c{n}", from, to, asset, LinkId: link);

    private static Track TrackOf(int n, TrackKind kind, params Item[] items) =>
        new(Id(100 + n), kind, $"T{n}", null, 64, false, true, false, false, null, items);

    private static EditTimeline Doc(params Track[] tracks) =>
        new(Guid.Parse("22222222-2222-4222-8222-222222222222"), ProjectId, "doc", Settings, tracks, [], null, null, null, null, null);

    private static EditTimelineValidationContext Context() =>
        new(ProjectId, new Dictionary<AssetRef, long>(), new Dictionary<Guid, Guid> { [AssetA.ProjectFileId!.Value] = ProjectId, [AssetB.ProjectFileId!.Value] = ProjectId }, new Dictionary<Guid, Guid>());

    private static IReadOnlyList<string> Paths(EditTimeline doc) => EditTimelineValidator.Validate(doc, Context()).Violations.Select(v => v.Path).ToList();

    private static MulticamClip Multicam(string? audioSource) =>
        new(Id(1), "Interview", 0, 100, [new MulticamAngle(AngleA, "Wide", AssetA, 0), new MulticamAngle(AngleB, "Close", AssetB, 15)], AngleA,
            [new MulticamCut(50, AngleB)], SourceIn: 10, AudioSource: audioSource);

    [Fact]
    public void A_link_group_across_tracks_sharing_one_source_is_accepted_and_a_group_of_one_is_tolerated()
    {
        Paths(Doc(TrackOf(1, TrackKind.Video, Clip(1, 0, 100, AssetA, "grp-1")), TrackOf(2, TrackKind.Audio, Clip(2, 0, 100, AssetA, "grp-1")))).Should().BeEmpty();
        Paths(Doc(TrackOf(1, TrackKind.Video, Clip(1, 0, 100, AssetA, "alone")))).Should().BeEmpty();
    }

    [Fact]
    public void Link_rules_refuse_same_track_members_other_media_and_bad_ids()
    {
        Paths(Doc(TrackOf(1, TrackKind.Video, Clip(1, 0, 50, AssetA, "g"), Clip(2, 50, 100, AssetA, "g")))).Should().Equal("tracks[0].items[1].linkId");
        Paths(Doc(TrackOf(1, TrackKind.Video, Clip(1, 0, 100, AssetA, "g")), TrackOf(2, TrackKind.Audio, Clip(2, 0, 100, AssetB, "g")))).Should().Equal("tracks[1].items[0].linkId");
        foreach (string bad in new[] { "", "has space", new string('a', 65) })
        {
            Paths(Doc(TrackOf(1, TrackKind.Video, Clip(1, 0, 100, AssetA, bad)))).Should().Equal("tracks[0].items[0].linkId");
        }
    }

    [Fact]
    public void Multicam_audio_source_validates_program_none_and_own_angles_only()
    {
        foreach (string? ok in new[] { null, "program", "none", AngleB.ToString() })
        {
            Paths(Doc(TrackOf(1, TrackKind.Video, Multicam(ok)))).Should().BeEmpty(ok);
        }

        foreach (string bad in new[] { "loud", Id(9).ToString() })
        {
            Paths(Doc(TrackOf(1, TrackKind.Video, Multicam(bad)))).Should().Equal("tracks[0].items[0].audioSource");
        }
    }

    [Fact]
    public void Flatten_emits_a_program_audio_track_from_the_chosen_angle_for_the_whole_span()
    {
        Track host = TrackOf(1, TrackKind.Video, Multicam(null));
        foreach ((string? source, AssetRef expected, long offset) in new (string?, AssetRef, long)[] { (null, AssetA, 10), ("program", AssetA, 10), (AngleB.ToString(), AssetB, 25) })
        {
            EditTimeline flat = TimelineFlattener.Flatten(Doc(host with { Items = [Multicam(source)] }));
            flat.Tracks.Should().HaveCount(2);
            Track audio = flat.Tracks[1];
            audio.Kind.Should().Be(TrackKind.Audio);
            MediaClip clip = audio.Items.Should().ContainSingle().Which.Should().BeOfType<MediaClip>().Subject;
            (clip.AssetRef, clip.SourceIn, clip.RecordIn, clip.RecordOut).Should().Be((expected, offset, 0L, 100L));
        }

        TimelineFlattener.Flatten(Doc(host with { Items = [Multicam("none")] })).Tracks.Should().HaveCount(1);
        EditTimeline twice = TimelineFlattener.Flatten(Doc(host with { Items = [Multicam(AngleB.ToString())] }));
        TimelineFlattener.Flatten(twice).Should().BeSameAs(twice);
    }

    [Fact]
    public void The_render_mixes_the_chosen_angles_audio_and_nothing_for_none()
    {
        static IReadOnlyList<string> Args(string? source)
        {
            EditTimeline flat = TimelineFlattener.Flatten(Doc(TrackOf(1, TrackKind.Video, Multicam(source))));
            ResolvedRenderTarget target = TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);
            return AudioMixBuilder.BuildMixArgs(SegmentPlanner.Plan(flat, null), target, Assets, "/scratch/mix.wav", 1, flat.Tracks);
        }

        IReadOnlyList<string> program = Args(null);
        program.Should().Contain("/tmp/a.mp4").And.NotContain("/tmp/b.mp4");
        program.Should().Contain("-filter_complex");

        IReadOnlyList<string> closeUp = Args(AngleB.ToString());
        closeUp.Should().Contain("/tmp/b.mp4").And.NotContain("/tmp/a.mp4");
        string graph = closeUp[closeUp.ToList().IndexOf("-filter_complex") + 1];
        graph.Should().Contain("atrim").And.Contain("adelay");

        Args("none").Should().NotContain("/tmp/a.mp4").And.NotContain("/tmp/b.mp4");
    }
}
