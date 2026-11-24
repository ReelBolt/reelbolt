#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Proposals;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;
using ValidationResult = ReelBolt.Shared.Editing.ValidationResult;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Editor v2, P8 (advanced structure + audio) — the server half: the flattener (compound and
/// multicam clips into plain tracks), the validator's nesting/bus/multicam/channel rules, the mix's
/// buses and processing chain, and the AI-assist proposal compiler and endpoint.
/// </summary>
public class EditorP8BackendTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly AssetRef AssetB = new(AssetRefKind.ProjectFile, Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), null);
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Guid Seq(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    private static Guid Id(string hex) => Guid.Parse($"{hex}{hex}{hex}{hex}{hex}{hex}{hex}{hex}-{hex}{hex}{hex}{hex}-4{hex}{hex}{hex}-8{hex}{hex}{hex}-{hex}{hex}{hex}{hex}{hex}{hex}{hex}{hex}{hex}{hex}{hex}{hex}");

    private static MediaClip Clip(string id, long recordIn, long recordOut, AssetRef? asset = null, long sourceIn = 0, Provenance? provenance = null, Keyframed<double>? gain = null, string channelMap = "stereo") =>
        new(Id(id), $"clip-{id}", recordIn, recordOut, asset ?? AssetA, sourceIn, 1.0, true, provenance, gain, ChannelMap: channelMap);

    private static Track VideoTrack(string id, params Item[] items) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-0000-4000-8000-000000000000"), TrackKind.Video, $"V{id}", null, 64, false, true, false, false, null, items);

    private static Track AudioTrack(string id, Guid? busId, params Item[] items) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-0000-4000-8000-000000000001"), TrackKind.Audio, $"A{id}", null, 48, false, true, false, false, busId, items);

    private static EditTimeline Doc(IReadOnlyList<EditSequence>? sequences = null, IReadOnlyList<AudioBus>? buses = null, params Track[] tracks) =>
        new(Guid.Parse("22222222-2222-4222-8222-222222222222"), ProjectId, "P8 doc", Settings, tracks, [], null, null, null, sequences, buses);

    private static readonly Dictionary<AssetRef, LocalAsset> Assets = new()
    {
        [AssetA] = new LocalAsset("/tmp/a.mp4", "a|100", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
        [AssetB] = new LocalAsset("/tmp/b.mp4", "b|200", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
    };

    private static ResolvedRenderTarget Target() => TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);

    private static string Graph(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-filter_complex") + 1];

    private static EditTimelineValidationContext Context(params AssetRef[] owned) =>
        new(ProjectId, new Dictionary<AssetRef, long>(), owned.Where(a => a.ProjectFileId is not null).ToDictionary(a => a.ProjectFileId!.Value, _ => ProjectId), new Dictionary<Guid, Guid>());

    // ── Flattener ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_cut_title_keeps_its_template_position_and_transform()
    {
        Guid seqId = Id("5");
        ClipTransform transform = new(PositionX: new Keyframed<double>(0.1), ScaleY: new Keyframed<double>(1.5));
        EditSequence seq = new(seqId, "Intro", [
            VideoTrack("a", new TitleClip(Id("2"), "title", 60, 120, "Hi", Template: "card", Position: "topLeft", Transform: transform))]);
        CompoundClip compound = new(Id("c"), "Intro", 100, 160, seqId, SourceIn: 30);

        EditTimeline flat = TimelineFlattener.Flatten(Doc([seq], null, VideoTrack("1", compound)));

        TitleClip title = flat.Tracks.SelectMany(t => t.Items).OfType<TitleClip>().Should().ContainSingle().Subject;
        title.Template.Should().Be("card");
        title.Position.Should().Be("topLeft");
        title.Transform.Should().BeEquivalentTo(transform);
    }

    [Fact]
    public void A_compound_clip_expands_into_derived_tracks_right_after_its_host_shifted_and_clipped()
    {
        // Sequence: V (clip 0..90 sourceIn 10, title 60..120) + A (clip 0..120). Compound plays frames 30..90 of it at record 100..160.
        Guid seqId = Id("5");
        EditSequence seq = new(seqId, "Intro", [
            VideoTrack("a", Clip("1", 0, 90, sourceIn: 10), new TitleClip(Id("2"), "title", 60, 120, "Hi")),
            AudioTrack("b", null, Clip("3", 0, 120))]);
        CompoundClip compound = new(Id("c"), "Intro", 100, 160, seqId, SourceIn: 30);
        EditTimeline doc = Doc([seq], null, VideoTrack("1", Clip("9", 0, 100), compound), VideoTrack("2", Clip("8", 0, 200)));

        EditTimeline flat = TimelineFlattener.Flatten(doc);
        flat.Tracks.Select(t => t.Name).Should().Equal("V1", "Intro / Va", "Intro / Ab", "V2");
        flat.Tracks[0].Items.Should().ContainSingle().Which.Id.Should().Be(Id("9"), "the compound itself is gone from the host track");

        IReadOnlyList<Item> nestedVideo = flat.Tracks[1].Items;
        nestedVideo.Should().HaveCount(2);
        MediaClip clip = nestedVideo[0].Should().BeOfType<MediaClip>().Subject;
        // Clip 0..90 shifted by (100 − 30) = 70 → 70..160, clipped to 100..160: head trim 30, so sourceIn 10 + 30.
        clip.RecordIn.Should().Be(100);
        clip.RecordOut.Should().Be(160);
        clip.SourceIn.Should().Be(40);
        clip.Id.Should().Be(TimelineFlattener.DeriveId(compound.Id, Id("1").ToString("N")));
        TitleClip title = nestedVideo[1].Should().BeOfType<TitleClip>().Subject;
        // Title 60..120 → 130..190, clipped to the compound's end 160.
        title.RecordIn.Should().Be(130);
        title.RecordOut.Should().Be(160);
        flat.Tracks[2].Items.Should().ContainSingle().Which.RecordIn.Should().Be(100);
        flat.Tracks[1].Id.Should().Be(TimelineFlattener.DeriveId(compound.Id, seq.Tracks[0].Id.ToString("N")));
        flat.Sequences.Should().BeEquivalentTo(doc.Sequences, "sequences ride along untouched");

        // Pure: the input document still holds its compound clip.
        doc.Tracks[0].Items.Should().HaveCount(2);
        TimelineFlattener.NeedsFlattening(flat).Should().BeFalse();
        TimelineFlattener.Flatten(flat).Should().BeSameAs(flat);
    }

    [Fact]
    public void Two_compounds_of_one_sequence_get_distinct_ids_and_a_disabled_or_dangling_one_expands_to_nothing()
    {
        Guid seqId = Id("5");
        EditSequence seq = new(seqId, "S", [VideoTrack("a", Clip("1", 0, 30))]);
        CompoundClip first = new(Id("c"), "first", 0, 30, seqId);
        CompoundClip second = new(Id("d"), "second", 30, 60, seqId);
        CompoundClip disabled = new(Id("e"), "off", 60, 90, seqId, Enabled: false);
        CompoundClip dangling = new(Id("f"), "gone", 90, 120, Id("7"));
        EditTimeline flat = TimelineFlattener.Flatten(Doc([seq], null, VideoTrack("1", first, second, disabled, dangling)));

        flat.Tracks.Should().HaveCount(3);
        flat.Tracks[0].Items.Should().BeEmpty();
        flat.Tracks[1].Items.Single().Id.Should().NotBe(flat.Tracks[2].Items.Single().Id);
        flat.Tracks[2].Items.Single().RecordIn.Should().Be(30);
    }

    [Fact]
    public void Nesting_is_followed_to_the_depth_limit_and_a_self_reference_is_dropped_not_looped()
    {
        // root → s1 → s2 → s3 → s4 → s5: the root is level 1, so s3 (level 4) is the deepest content kept and s4's
        // compound is dropped. Each sequence has its compound on one track and a title on another (a track whose
        // only item was the compound is empty after expansion and is dropped).
        Guid[] ids = Enumerable.Range(1, 5).Select(i => Id(i.ToString())).ToArray();
        List<EditSequence> sequences = [];
        for (int i = 0; i < 5; i++)
        {
            Item content = i + 1 < 5 ? new CompoundClip(Id($"{i + 1}"), $"to{i + 2}", 0, 60, ids[i + 1]) : Clip("9", 0, 60);
            sequences.Add(new EditSequence(ids[i], $"s{i + 1}", [VideoTrack($"{i + 1}", content), VideoTrack($"{(char)('a' + i)}", new TitleClip(Guid.NewGuid(), $"t{i + 1}", 0, 60, $"T{i + 1}"))]));
        }

        EditTimeline flat = TimelineFlattener.Flatten(Doc(sequences, null, VideoTrack("a", new CompoundClip(Id("0"), "root", 0, 60, ids[0]))));
        flat.Tracks.Should().HaveCount(4, "the root track plus one title track per kept level");
        flat.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Should().BeEmpty("s5's media clip is beyond the limit");
        // Nested tracks are inserted right after their host track, so the deepest content composites lowest.
        flat.Tracks.SelectMany(t => t.Items).OfType<TitleClip>().Select(t => t.Text).Should().Equal("T3", "T2", "T1");

        EditSequence loop = new(Id("a"), "loop", [VideoTrack("b", new CompoundClip(Id("b"), "self", 0, 30, Id("a")), Clip("1", 30, 60))]);
        EditTimeline looped = TimelineFlattener.Flatten(Doc([loop], null, VideoTrack("c", new CompoundClip(Id("c"), "in", 0, 60, Id("a")))));
        looped.Tracks.Should().HaveCount(2);
        looped.Tracks[1].Items.Should().ContainSingle().Which.RecordIn.Should().Be(30, "the self reference expands to nothing; the clip beside it survives");
    }

    [Fact]
    public void A_multicam_clip_becomes_one_media_clip_per_angle_run_reading_each_angles_offset_source()
    {
        MulticamAngle wide = new(Id("1"), "Wide", AssetA, OffsetFrames: 0);
        MulticamAngle close = new(Id("2"), "Close", AssetB, OffsetFrames: 45);
        MulticamClip multicam = new(Id("3"), "Interview", 100, 400, [wide, close], wide.Id,
            [new MulticamCut(200, close.Id), new MulticamCut(300, wide.Id), new MulticamCut(50, close.Id), new MulticamCut(250, Id("9"))], SourceIn: 10, GainDb: new Keyframed<double>(-3));

        IReadOnlyList<MediaClip> runs = TimelineFlattener.ExpandMulticam(multicam);
        runs.Select(r => (r.RecordIn, r.RecordOut, r.AssetRef, r.SourceIn)).Should().Equal(
            (100L, 200L, AssetA, 10L),
            (200L, 300L, AssetB, 45 + 10 + 100L),
            (300L, 400L, AssetA, 10 + 200L));
        runs.Select(r => r.Name).Should().Equal("Interview · Wide", "Interview · Close", "Interview · Wide");
        runs.Should().OnlyContain(r => r.GainDb != null && r.GainDb.Value == -3);
        runs.Select(r => r.Id).Distinct().Should().HaveCount(3);

        EditTimeline flat = TimelineFlattener.Flatten(Doc(null, null, VideoTrack("1", multicam)));
        flat.Tracks.Should().HaveCount(2, "a multicam clip expands in place, plus its program-audio track (R08.2)");
        RenderPlan plan = SegmentPlanner.Plan(flat, null);
        plan.Regions.Select(r => (r.StartFrame, r.EndFrame)).Should().Equal((0L, 100L), (100L, 200L), (200L, 300L), (300L, 400L));
        plan.Regions[2].Layers.Single().Item.Should().BeOfType<MediaClip>().Which.AssetRef.Should().Be(AssetB);
    }

    [Fact]
    public void Media_sources_cover_root_clips_nested_clips_and_every_angle()
    {
        AssetRef nested = new(AssetRefKind.ProjectFile, Id("d"));
        AssetRef angle = new(AssetRefKind.ProjectFile, Id("e"));
        EditSequence seq = new(Id("5"), "S", [VideoTrack("a", Clip("1", 0, 30, nested))]);
        EditTimeline doc = Doc([seq], null,
            VideoTrack("1", Clip("2", 0, 30), new CompoundClip(Id("c"), "c", 30, 60, seq.Id), new MulticamClip(Id("3"), "m", 60, 90, [new MulticamAngle(Id("4"), "a", angle)], Id("4"))));
        TimelineAssetReferences.MediaSources(doc).Distinct().Should().BeEquivalentTo([AssetA, angle, nested]);
    }

    // ── Validator ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_validator_reports_a_missing_sequence_a_cycle_and_too_deep_nesting_at_the_compound_clip()
    {
        EditTimeline missing = Doc(null, null, VideoTrack("1", new CompoundClip(Id("c"), "c", 0, 30, Id("7"))));
        EditTimelineValidator.Validate(missing, Context(AssetA)).Violations.Should().ContainSingle()
            .Which.Path.Should().Be("tracks[0].items[0].sequenceId");

        EditSequence a = new(Id("a"), "a", [VideoTrack("1", new CompoundClip(Id("1"), "to b", 0, 30, Id("b")))]);
        EditSequence b = new(Id("b"), "b", [VideoTrack("2", new CompoundClip(Id("2"), "to a", 0, 30, Id("a")))]);
        ValidationResult cycle = EditTimelineValidator.Validate(Doc([a, b], null, VideoTrack("3", new CompoundClip(Id("3"), "root", 0, 30, Id("a")))), Context(AssetA));
        cycle.Violations.Should().ContainSingle().Which.Should().Be(("sequences[1].tracks[0].items[0].sequenceId", "sequence " + Id("a") + " contains itself"));

        Guid[] ids = Enumerable.Range(1, 5).Select(i => Id(i.ToString())).ToArray();
        List<EditSequence> chain = [];
        for (int i = 0; i < 5; i++)
        {
            Item content = i + 1 < 5 ? new CompoundClip(Id($"{i + 1}"), "down", 0, 30, ids[i + 1]) : Clip("9", 0, 30);
            chain.Add(new EditSequence(ids[i], $"s{i + 1}", [VideoTrack($"{i + 1}", content)]));
        }

        // The root is level 1, s3 is level 4: s3's compound (into s4) is the one too deep.
        ValidationResult deep = EditTimelineValidator.Validate(Doc(chain, null, VideoTrack("a", new CompoundClip(Id("0"), "root", 0, 30, ids[0]))), Context(AssetA));
        deep.Violations.Should().ContainSingle().Which.Path.Should().Be("sequences[2].tracks[0].items[0].sequenceId");
        deep.Violations.Single().Message.Should().Contain("at most 4 deep");

        // Three nested sequences (four levels with the root) are fine; an invalid item INSIDE a sequence is reported with its sequence path.
        ValidationResult ok = EditTimelineValidator.Validate(Doc(chain.Take(3).Select((s, i) => i == 2 ? new EditSequence(s.Id, s.Name, [VideoTrack("3", Clip("9", 0, 30))]) : s).ToList(), null, VideoTrack("a", new CompoundClip(Id("0"), "root", 0, 30, ids[0]))), Context(AssetA));
        ok.IsValid.Should().BeTrue(string.Join("; ", ok.Violations));
        EditSequence bad = new(Id("a"), "a", [VideoTrack("1", Clip("1", 0, 30, channelMap: "quad"))]);
        EditTimelineValidator.Validate(Doc([bad], null, VideoTrack("2")), Context(AssetA)).Violations.Should().ContainSingle()
            .Which.Path.Should().Be("sequences[0].tracks[0].items[0].channelMap");
    }

    [Fact]
    public void The_validator_checks_angles_cuts_buses_and_processing_ranges_and_tolerates_a_dangling_bus()
    {
        MulticamClip multicam = new(Id("3"), "m", 100, 200, [new MulticamAngle(Id("1"), "a", AssetA), new MulticamAngle(Id("2"), "b", AssetB)], Id("1"),
            [new MulticamCut(150, Id("2")), new MulticamCut(150, Id("1")), new MulticamCut(250, Id("9"))]);
        ValidationResult result = EditTimelineValidator.Validate(Doc(null, null, VideoTrack("1", multicam)), Context(AssetA));
        result.Violations.Select(v => v.Path).Should().BeEquivalentTo(
            "tracks[0].items[0].angles[1].assetRef", // AssetB is not owned
            "tracks[0].items[0].cuts[1].atFrame",     // not increasing
            "tracks[0].items[0].cuts[2].atFrame",     // outside the span
            "tracks[0].items[0].cuts[2].angleId");    // unknown angle
        EditTimelineValidator.Validate(Doc(null, null, VideoTrack("1", new MulticamClip(Id("3"), "m", 0, 30, [], Id("1")))), Context(AssetA))
            .Violations.Select(v => v.Path).Should().BeEquivalentTo("tracks[0].items[0].angles");

        AudioBus a = new(Id("a"), "Dialogue", OutputBusId: Id("b"));
        AudioBus b = new(Id("b"), "Music", OutputBusId: Id("a"), Dynamics: new AudioDynamics(new AudioCompressor(ThresholdDb: 5, Ratio: 30)), Denoise: new AudioDenoise("rnn", 2), Leveler: new AudioLeveler("loud", -50));
        ValidationResult buses = EditTimelineValidator.Validate(Doc(null, [a, b, new AudioBus(Id("c"), "")], AudioTrack("1", Id("f"), Clip("1", 0, 30))), Context(AssetA));
        buses.Violations.Select(v => v.Path).Should().BeEquivalentTo(
            "buses[0].outputBusId", "buses[1].outputBusId",
            "buses[1].dynamics.compressor.thresholdDb", "buses[1].dynamics.compressor.ratio",
            "buses[1].denoise.kind", "buses[1].denoise.strength", "buses[1].leveler.kind", "buses[1].leveler.targetDb",
            "buses[2].name");
        buses.Violations.Should().NotContain(v => v.Path.StartsWith("tracks"), "a track routed to a bus that does not exist is tolerated and feeds the master");

        Track processed = new(Id("1"), TrackKind.Audio, "A", null, 48, false, true, false, false, null, [Clip("1", 0, 30)],
            Dynamics: new AudioDynamics(Gate: new AudioGate(ThresholdDb: -95), Limiter: new AudioLimiter(1), Deesser: new AudioDeesser(1.5)), Leveler: new AudioLeveler("dynamic", -18));
        EditTimelineValidator.Validate(Doc(null, null, processed), Context(AssetA)).Violations.Select(v => v.Path)
            .Should().BeEquivalentTo("tracks[0].dynamics.gate.thresholdDb", "tracks[0].dynamics.limiter.ceilingDb", "tracks[0].dynamics.deesser.intensity");
    }

    // ── Mix ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_document_without_buses_or_processing_mixes_byte_identically_to_P5()
    {
        EditTimeline doc = Doc(null, [], AudioTrack("1", null, Clip("1", 0, 60)), AudioTrack("2", null, Clip("2", 0, 60, AssetB)));
        RenderPlan plan = SegmentPlanner.Plan(doc, null);
        string before = Graph(AudioMixBuilder.BuildMixArgs(plan, Target(), Assets, "/scratch/mix.wav", 1, doc.Tracks));
        string after = Graph(AudioMixBuilder.BuildMixArgs(plan, Target(), Assets, "/scratch/mix.wav", 1, doc.Tracks, doc.Buses));
        after.Should().Be(before);
        before.Should().EndWith("[a0][a1]amix=inputs=2:normalize=0:duration=longest,apad=whole_dur=2,atrim=duration=2,asetpts=PTS-STARTPTS[aout]");
        AudioMixBuilder.HasProcessing(doc.Tracks[0]).Should().BeFalse();
    }

    [Fact]
    public void Tracks_routed_to_a_bus_are_summed_through_its_chain_and_a_child_bus_is_built_before_its_output()
    {
        AudioBus music = new(Id("a"), "Music", new Keyframed<double>(-3));
        AudioBus master = new(Id("b"), "Stems", Pan: 0, Dynamics: new AudioDynamics(Limiter: new AudioLimiter(-1)));
        AudioBus routedMusic = music with { OutputBusId = master.Id };
        EditTimeline doc = Doc(null, [routedMusic, master],
            AudioTrack("1", routedMusic.Id, Clip("1", 0, 60)),
            AudioTrack("2", routedMusic.Id, Clip("2", 0, 60, AssetB)),
            AudioTrack("3", null, Clip("3", 0, 60)));
        string graph = Graph(AudioMixBuilder.BuildMixArgs(SegmentPlanner.Plan(doc, null), Target(), Assets, "/scratch/mix.wav", 1, doc.Tracks, doc.Buses));
        graph.Should().Contain("[a0]anull,apad=whole_dur=2,atrim=duration=2[bus0]");
        graph.Should().Contain("[a2]anull,apad=whole_dur=2,atrim=duration=2[bus2]");
        graph.Should().Contain("[bus0][bus1]amix=inputs=2:normalize=0:duration=longest,volume=-3dB,apad=whole_dur=2,atrim=duration=2[mix0]");
        graph.Should().Contain("[mix0]anull,alimiter=limit=0.891251:level=0,apad=whole_dur=2,atrim=duration=2[mix1]");
        graph.Should().EndWith("[bus2][mix1]amix=inputs=2:normalize=0:duration=longest,atrim=duration=2,asetpts=PTS-STARTPTS[aout]");
    }

    [Fact]
    public void The_processing_chain_and_the_channel_map_compile_to_exact_filters()
    {
        AudioMixBuilder.ProcessingChain(
            new AudioDynamics(new AudioCompressor(), new AudioLimiter(), new AudioGate(), new AudioDeesser()),
            new AudioDenoise(), new AudioLeveler()).Should().Equal(
            "afftdn=nr=21:nf=-50",
            "agate=threshold=0.01:ratio=2:attack=20:release=250",
            "deesser=i=0.5",
            "acompressor=threshold=0.125893:ratio=3:attack=20:release=250:makeup=1",
            "speechnorm=p=0.125893:e=3",
            "alimiter=limit=0.891251:level=0");
        AudioMixBuilder.ProcessingChain(null, null, new AudioLeveler("dynamic", -14)).Should().Equal("dynaudnorm=p=0.199526:f=500:g=31");
        AudioMixBuilder.ProcessingChain(new AudioDynamics(), null, null).Should().BeEmpty();
        AudioMixBuilder.HasChannelProcessing(new AudioDynamics(), null, null).Should().BeFalse();

        Track voice = new(Id("1"), TrackKind.Audio, "A", null, 48, false, true, false, false, null, [Clip("1", 0, 60, channelMap: "left")], Denoise: new AudioDenoise(Strength: 1));
        EditTimeline doc = Doc(null, null, voice);
        AudioMixBuilder.HasProcessing(voice).Should().BeTrue();
        string graph = Graph(AudioMixBuilder.BuildMixArgs(SegmentPlanner.Plan(doc, null), Target(), Assets, "/scratch/mix.wav", 1, doc.Tracks, doc.Buses));
        graph.Should().StartWith("[0:a]atrim=duration=2,asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=stereo,pan=stereo|c0=c0|c1=c0,adelay=0|0,apad=whole_dur=2[a0]");
        graph.Should().Contain("[a0]anull,afftdn=nr=36:nf=-50,apad=whole_dur=2,atrim=duration=2[bus0]");
        AudioMixBuilder.ChannelMapFilter("mono").Should().Be("pan=stereo|c0=0.5*c0+0.5*c1|c1=0.5*c0+0.5*c1,");
        AudioMixBuilder.ChannelMapFilter("swap").Should().Be("pan=stereo|c0=c1|c1=c0,");
        AudioMixBuilder.ChannelMapFilter("stereo").Should().BeEmpty();
    }

    // ── Proposals ─────────────────────────────────────────────────────────────

    private static EditTimeline ProposalDoc() => Doc(null, null,
        VideoTrack("1",
            Clip("1", 0, 90, provenance: new Provenance(ProvenanceOrigin.Generated, Rationale: "hook", AgentName: "VideoStoryEditor")),
            Clip("2", 90, 180),
            Clip("3", 180, 300, AssetB, provenance: new Provenance(ProvenanceOrigin.Generated, EditedByUser: true))),
        AudioTrack("2", null, Clip("4", 0, 300)));

    [Fact]
    public void Log_converted_footage_is_named_only_from_the_clips_own_profile()
    {
        FactShot shot = new(0, 10, "calm", "dim", "flat contrast", null);
        Dictionary<Guid, ReelBolt.Shared.Editing.Proposals.SourceFacts> facts = new()
        {
            [AssetA.ProjectFileId!.Value] = new ReelBolt.Shared.Editing.Proposals.SourceFacts([], [shot], [], "appleLog"),
            [AssetB.ProjectFileId!.Value] = new ReelBolt.Shared.Editing.Proposals.SourceFacts([], [shot], [])
        };
        // A clip with a null profile on a High-hinted source renders unconverted: no "log-converted" line.
        EditProposalView.Build(ProposalDoc(), null, [], 30, facts, 6000).Text.Should().NotContain("log-converted");

        MediaClip own = new(Id("9"), "own", 0, 90, AssetB, 0, 1.0, true, null, InputProfile: "djiDLogM");
        EditTimeline withOwn = Doc(null, null, VideoTrack("1", own));
        string text = EditProposalView.Build(withOwn, null, [], 30, facts, 6000).Text;
        text.Should().Contain("log-converted footage (djiDLogM)");
        text.Should().NotContain("log-converted footage (appleLog)");
    }

    [Fact]
    public void The_view_offers_aliases_with_timecodes_and_tells_the_model_who_made_each_item()
    {
        EditProposalView view = EditProposalView.Build(ProposalDoc(), null, [(Id("e"), "whoosh.wav")], 30);
        view.Text.Should().Contain("i1 | V1 | video clip | clip-1 | 00:00:00:00–00:00:03:00 | 3.0 | VideoStoryEditor | note: hook");
        view.Text.Should().Contain("i2 | V1 | video clip | clip-2 | 00:00:03:00–00:00:06:00 | 3.0 | user");
        view.Text.Should().Contain("i3 | V1 | video clip | clip-3 | 00:00:06:00–00:00:10:00 | 4.0 | user");
        view.Text.Should().Contain("i4 | A2 | audio clip");
        view.Text.Should().Contain("x1 | whoosh.wav");
        view.ItemAliases["i2"].Should().Be(Id("2"));
        view.FileAliases["x1"].Should().Be(Id("e"));
        (view.InFrame, view.OutFrame).Should().Be((0L, 300L));
        EditProposalView.Build(ProposalDoc(), new EditRange(100, 200), [], 30).Text.Should().NotContain("i1 | V1 | video clip | clip-1");
    }

    [Fact]
    public void The_recut_compiler_turns_words_into_ops_rejects_unknown_aliases_and_flags_user_owned_clips()
    {
        EditTimeline doc = ProposalDoc();
        EditProposalView view = EditProposalView.Build(doc, null, [], 30);
        EditProposalOutput output = new()
        {
            Summary = "Tighten the open.",
            Actions =
            [
                new() { Kind = "remove", ItemId = "i1", Reason = "repeats the hook" },
                new() { Kind = "trimHead", ItemId = "i2", Word = "medium", Reason = "dead air" },
                new() { Kind = "trimTail", ItemId = "i3", Word = "large" },
                new() { Kind = "mute", ItemId = "i4" },
                new() { Kind = "remove", ItemId = "i9" },
                new() { Kind = "addTitle", ItemId = "i1", Text = "no" },
                new() { Kind = "trimHead", ItemId = "i1", Word = "large" }
            ]
        };
        int n = 0;
        EditProposal proposal = EditProposalCompiler.Compile(doc, EditProposalAgents.Recut, output, view, 30, () => Seq(++n));

        proposal.Summary.Should().Be("Tighten the open.");
        proposal.Groups.Should().HaveCount(5);
        proposal.Groups[0].Ops.Single().ToJsonString().Should().Be($"{{\"type\":\"extract\",\"itemId\":\"{Id("1")}\"}}");
        proposal.Groups[0].TouchesUserOwned.Should().BeFalse("an agent-made clip");
        proposal.Groups[0].Reason.Should().Be("repeats the hook");
        proposal.Groups[1].Ops.Single().ToJsonString().Should().Be($"{{\"type\":\"trim\",\"itemId\":\"{Id("2")}\",\"edge\":\"in\",\"mode\":\"ripple\",\"deltaFrames\":30}}");
        proposal.Groups[1].TouchesUserOwned.Should().BeTrue("no provenance means the editor placed it");
        proposal.Groups[2].Ops.Single()["deltaFrames"]!.GetValue<long>().Should().Be(-60);
        proposal.Groups[2].TouchesUserOwned.Should().BeTrue("edited by the user");
        proposal.Groups[3].Ops.Single().ToJsonString().Should().Be($"{{\"type\":\"setProperty\",\"itemId\":\"{Id("4")}\",\"key\":\"enabled\",\"value\":false}}");
        proposal.Groups[4].Ops.Single()["deltaFrames"]!.GetValue<long>().Should().Be(60, "a 3 s clip can lose at most 89 frames; 2 s fits");
        proposal.Rejected.Should().HaveCount(2);
        proposal.Rejected[0].Should().Contain("'i9' is not an offered item");
        proposal.Rejected[1].Should().Contain("'addTitle' is not something the recut agent may propose");
    }

    [Fact]
    public void Graphics_grade_sfx_and_review_compile_to_tracks_items_grades_and_markers_with_provenance()
    {
        EditTimeline doc = ProposalDoc();
        EditProposalView view = EditProposalView.Build(doc, null, [(Id("e"), "whoosh.wav")], 30);
        int n = 0;
        Func<Guid> newId = () => Seq(++n);

        EditProposal graphics = EditProposalCompiler.Compile(doc, EditProposalAgents.Graphics, new EditProposalOutput
        {
            Actions =
            [
                new() { Kind = "addTitle", ItemId = "i1", Text = "Meet the team", Word = "lowerThird", Reason = "names the speaker" },
                new() { Kind = "addTitle", ItemId = "i1", Text = "Again", Word = "card" },
                new() { Kind = "addTitle", ItemId = "i2", Text = "", Word = "card" },
                new() { Kind = "addTitle", ItemId = "i3", Text = "Closing", Word = "neon" }
            ]
        }, view, 30, newId);
        graphics.Groups.Should().HaveCount(2);
        graphics.Groups[0].Ops.Should().HaveCount(2, "the first title creates the Graphics track");
        JsonObject addTrack = graphics.Groups[0].Ops[0];
        addTrack["type"]!.GetValue<string>().Should().Be("addTrack");
        addTrack["index"]!.GetValue<int>().Should().Be(2);
        addTrack["track"]!["kind"]!.GetValue<string>().Should().Be("video");
        addTrack["track"]!["name"]!.GetValue<string>().Should().Be("Graphics");
        JsonObject place = graphics.Groups[0].Ops[1];
        place["type"]!.GetValue<string>().Should().Be("placeOnTop");
        place["trackId"]!.GetValue<string>().Should().Be(addTrack["track"]!["id"]!.GetValue<string>());
        JsonNode title = place["item"]!;
        title["type"]!.GetValue<string>().Should().Be("titleClip");
        title["text"]!.GetValue<string>().Should().Be("Meet the team");
        title["template"]!.GetValue<string>().Should().Be("lowerThird");
        title["recordIn"]!.GetValue<long>().Should().Be(0);
        title["recordOut"]!.GetValue<long>().Should().Be(90, "3 s at 30 fps, within the 3 s clip");
        title["provenance"]!["origin"]!.GetValue<string>().Should().Be("generated");
        title["provenance"]!["agentName"]!.GetValue<string>().Should().Be("MotionGraphicsPlanner");
        title["provenance"]!["rationale"]!.GetValue<string>().Should().Be("names the speaker");
        graphics.Groups[1].Ops.Should().HaveCount(1, "the second group only places");
        graphics.Groups[1].Ops[0]["item"]!["template"]!.GetValue<string>().Should().Be("lowerThird", "an unknown template word falls back");
        graphics.Rejected.Should().HaveCount(2);

        EditProposal grade = EditProposalCompiler.Compile(doc, EditProposalAgents.Grade, new EditProposalOutput
        {
            Actions = [new() { Kind = "setLook", ItemId = "all", Word = "Warm", Text = "Subtle Lifted" }, new() { Kind = "setLook", ItemId = "i2", Word = "Neon" }]
        }, view, 30, newId);
        grade.Groups.Should().HaveCount(3, "every video clip in range");
        JsonNode stack = grade.Groups[0].Ops.Single()["value"]!;
        stack["enabled"]!.GetValue<bool>().Should().BeTrue();
        stack["nodes"]![0]!["name"]!.GetValue<string>().Should().Be("Warm (Subtle)");
        stack["nodes"]![0]!["primaries"]!["saturation"]!.GetValue<double>().Should().Be(1.03);
        stack["nodes"]![0]!["primaries"]!["lift"]!["master"]!.GetValue<double>().Should().Be(0.02);
        grade.Rejected.Should().ContainSingle().Which.Should().Contain("'Neon' is not a look");

        EditProposal sfx = EditProposalCompiler.Compile(doc, EditProposalAgents.Sfx, new EditProposalOutput
        {
            Actions = [new() { Kind = "addSfx", ItemId = "i2", Text = "x1", Word = "Subtle" }, new() { Kind = "addSfx", ItemId = "i3", Text = "x7" }]
        }, view, 30, newId);
        sfx.Groups.Should().ContainSingle();
        sfx.Groups[0].Ops[0]["track"]!["kind"]!.GetValue<string>().Should().Be("audio");
        JsonNode clip = sfx.Groups[0].Ops[1]["item"]!;
        clip["type"]!.GetValue<string>().Should().Be("mediaClip");
        clip["assetRef"]!["kind"]!.GetValue<string>().Should().Be("projectFile");
        clip["assetRef"]!["projectFileId"]!.GetValue<string>().Should().Be(Id("e").ToString());
        clip["recordIn"]!.GetValue<long>().Should().Be(90);
        clip["recordOut"]!.GetValue<long>().Should().Be(150);
        clip["gainDb"]!["value"]!.GetValue<double>().Should().Be(-12);
        sfx.Rejected.Should().ContainSingle().Which.Should().Contain("'x7' is not an offered sound file");

        EditProposal review = EditProposalCompiler.Compile(doc, EditProposalAgents.Review, new EditProposalOutput
        {
            Actions = [new() { Kind = "note", ItemId = "i3", Text = "The ending lingers.", Word = "warn" }, new() { Kind = "note", Text = "Overall pace is good.", Word = "info" }]
        }, view, 30, newId);
        review.Groups.Should().HaveCount(2);
        JsonNode marker = review.Groups[0].Ops.Single()["marker"]!;
        marker["frame"]!.GetValue<long>().Should().Be(180);
        marker["color"]!.GetValue<string>().Should().Be("#f59f00");
        marker["name"]!.GetValue<string>().Should().Be("Review: The ending lingers.");
        marker["note"]!.GetValue<string>().Should().Be("The ending lingers.");
        review.Groups[1].Ops.Single()["marker"]!["frame"]!.GetValue<long>().Should().Be(0);
        review.Groups[1].TouchedItemIds.Should().BeEmpty();
    }

    [Fact]
    public void Grade_looks_match_the_clients_look_node_numbers()
    {
        GradeNode warm = GradeLooks.Node(Guid.NewGuid(), "Warm");
        warm.Primaries!.Lift.Should().Be(new GradeWheel(R: 0.1, B: -0.1));
        warm.Primaries.Gamma.Should().Be(new GradeWheel(R: 0.05, B: -0.05));
        warm.Primaries.Saturation.Should().Be(1.06);
        GradeNode subtle = GradeLooks.Node(Guid.NewGuid(), "Warm", "Subtle", "Lifted", "Softened");
        subtle.Primaries!.Lift.Should().Be(new GradeWheel(R: 0.05, B: -0.05, Master: 0.02));
        subtle.Primaries.Gain.Should().Be(new GradeWheel(Master: -0.025));
        subtle.Primaries.Saturation.Should().Be(1.03);
        GradeNode mono = GradeLooks.Node(Guid.NewGuid(), "Mono", "Strong");
        mono.Primaries!.Saturation.Should().Be(0);
        mono.Primaries.Contrast.Should().Be(1.09);
        GradeLooks.Node(Guid.NewGuid(), "Filmic").Primaries!.Gamma.Should().Be(new GradeWheel(Master: -0.03));
    }

    [Fact]
    public void The_model_wrapper_parses_fenced_json_and_degrades_garbage_to_an_empty_output()
    {
        AgentEditProposalModel.Parse("```json\n{\"actions\":[{\"kind\":\"remove\",\"itemId\":\"i1\"}],\"summary\":\"ok\"}\n```").Actions.Should().ContainSingle().Which.Kind.Should().Be("remove");
        AgentEditProposalModel.Parse("not json").Actions.Should().BeEmpty();
        AgentEditProposalModel.Parse("not json").Failed.Should().BeTrue();
        AgentEditProposalModel.Parse(null).Failed.Should().BeTrue();
        AgentEditProposalModel.SystemPrompt(EditProposalAgents.Sfx).Should().Contain("addSfx");
        EditProposalAgents.AgentTypeOf(EditProposalAgents.Grade).Should().Be(AgentType.Colorist);
    }

    // ── Controller ────────────────────────────────────────────────────────────

    private static InferenceApiDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(databaseName).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    private static ICurrentUser Caller(Guid userId)
    {
        Mock<ICurrentUser> currentUser = new();
        currentUser.SetupGet(u => u.UserId).Returns(userId);
        currentUser.SetupGet(u => u.Email).Returns("test@example.com");
        currentUser.SetupGet(u => u.IsAuthenticated).Returns(true);
        currentUser.SetupGet(u => u.IsPlatformAdmin).Returns(false);
        currentUser.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(userId));
        currentUser.SetupGet(u => u.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);
        return currentUser.Object;
    }

    private sealed class FakeModel(EditProposalOutput? output) : IEditProposalModel
    {
        public string? LastView { get; private set; }
        public string? LastAgent { get; private set; }

        public Task<EditProposalOutput?> ProposeAsync(string agent, string view, string? brief, ResolvedInferenceProvider? provider, CancellationToken ct)
        {
            LastAgent = agent;
            LastView = view + (brief is null ? string.Empty : "|" + brief);
            return Task.FromResult(output);
        }
    }

    [Fact]
    public async Task The_proposals_endpoint_runs_the_model_over_the_view_and_answers_400_404_and_503_honestly()
    {
        Guid userId = Guid.NewGuid();
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        db.ProjectFiles.Add(new ProjectFile { Id = Id("e"), ProjectId = project.Id, OriginalFileName = "whoosh.wav", StorageKey = "k", StorageBucket = "b", Category = "userFiles", MimeType = "audio/wav", SizeBytes = 1, UploadedAt = DateTime.UtcNow });
        db.ProjectFiles.Add(new ProjectFile { Id = Id("f"), ProjectId = project.Id, OriginalFileName = "a.mp4", StorageKey = "k2", StorageBucket = "b", Category = "userFiles", MimeType = "video/mp4", SizeBytes = 1, UploadedAt = DateTime.UtcNow });
        EditTimeline document = ProposalDoc() with { ProjectId = project.Id };
        EditTimelineEntity row = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "P8", DocumentJson = JsonSerializer.Serialize(document, Json), Revision = 3, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.EditTimelines.Add(row);
        db.SaveChanges();

        ICurrentUser caller = Caller(userId);
        EditTimelinesController controller = new(db, caller, TestTenancy.Access(db, caller), new Mock<IFileStorageService>().Object, new Mock<IWorkflowExecutionService>().Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        FakeModel model = new(new EditProposalOutput { Summary = "One sound.", Actions = [new() { Kind = "addSfx", ItemId = "i2", Text = "x1", Word = "Normal", Reason = "punctuates the cut" }] });
        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(project.Id, row.Id, new EditProposalRequest("SFX", null, "make it punchy"), model, CancellationToken.None);
        EditProposalResponse response = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditProposalResponse>().Subject;
        response.Agent.Should().Be("sfx");
        response.Summary.Should().Be("One sound.");
        response.Groups.Should().ContainSingle();
        response.Groups[0].Ops.GetArrayLength().Should().Be(2);
        response.Groups[0].Ops[1].GetProperty("item").GetProperty("assetRef").GetProperty("projectFileId").GetString().Should().Be(Id("e").ToString());
        response.Groups[0].TouchesUserOwned.Should().BeFalse();
        response.Groups[0].Reason.Should().Be("punctuates the cut");
        model.LastAgent.Should().Be("sfx");
        model.LastView.Should().Contain("x1 | whoosh.wav").And.NotContain("a.mp4").And.EndWith("|make it punchy");

        (await controller.ProposeEdit(project.Id, row.Id, new EditProposalRequest("director", null, null), model, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.ProposeEdit(project.Id, row.Id, new EditProposalRequest("recut", new EditRange(10, 5), null), model, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.ProposeEdit(project.Id, Guid.NewGuid(), new EditProposalRequest("recut", null, null), model, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();

        ObjectResult unavailable = (await controller.ProposeEdit(project.Id, row.Id, new EditProposalRequest("recut", null, null), new FakeModel(null), CancellationToken.None)).Result.Should().BeOfType<ObjectResult>().Subject;
        unavailable.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        JsonSerializer.Serialize(unavailable.Value).Should().Contain("no_chat_provider");

        // The write path is untouched: the row's revision and document are what they were.
        db.EditTimelines.Single().Revision.Should().Be(3);
        db.EditTimelineOps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_timeline_proposal_is_metered_as_TimelineAssist_for_the_projects_org_project_and_caller()
    {
        Guid userId = Guid.NewGuid();
        using InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        EditTimeline document = ProposalDoc() with { ProjectId = project.Id };
        EditTimelineEntity row = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "P8", DocumentJson = JsonSerializer.Serialize(document, Json), Revision = 3, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.EditTimelines.Add(row);
        db.SaveChanges();

        ICurrentUser caller = Caller(userId);
        EditTimelinesController controller = new(db, caller, TestTenancy.Access(db, caller), new Mock<IFileStorageService>().Object, new Mock<IWorkflowExecutionService>().Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        CapturingSink sink = new();
        UsageRecorder recorder = new(
            new StaticPriceService(), sink, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance,
            metering: Microsoft.Extensions.Options.Options.Create(new MeteringOptions { FailOnUnattributed = true }));
        ResolvedInferenceProvider provider = new(Guid.NewGuid(), "platform", InferenceProviderKind.OpenAICompatible, "http://x", "gpt-test", "", 30);
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeteringChatClient(new UsageReportingChatClient(100, 20, answer: "{\"actions\":[],\"summary\":\"ok\"}"), provider, recorder));
        AgentEditProposalModel model = new(chatClients.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentEditProposalModel>.Instance);

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(project.Id, row.Id, new EditProposalRequest("recut", null, null), model, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        sink.Events.Should().NotBeEmpty();
        sink.Events.Should().OnlyContain(e =>
            e.Source == UsageSource.TimelineAssist
            && e.OrganizationId == project.OrganizationId
            && e.ProjectId == project.Id
            && e.UserId == userId);
        UsageScope.Current.Should().BeNull("the controller closes the scope it opened");
    }

    private (InferenceApiDbContext Db, EditTimelinesController Controller, Guid ProjectId, Guid TimelineId, Guid Org) ChargedProposalSetup(
        Billing.FakeNonWorkflowCharges charges, IAgentChatClientProvider? chatClients = null, EditProposalRateLimiter? limiter = null)
    {
        Guid userId = Guid.NewGuid();
        InferenceApiDbContext db = CreateContext(Guid.NewGuid().ToString());
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = userId, OrganizationId = TestTenancy.OrgOf(userId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        EditTimeline document = ProposalDoc() with { ProjectId = project.Id };
        EditTimelineEntity row = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "P8", DocumentJson = JsonSerializer.Serialize(document, Json), Revision = 3, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.EditTimelines.Add(row);
        db.SaveChanges();

        ICurrentUser caller = Caller(userId);
        EditTimelinesController controller = new(db, caller, TestTenancy.Access(db, caller), new Mock<IFileStorageService>().Object, new Mock<IWorkflowExecutionService>().Object, charges, chatClients, proposalLimiter: limiter);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return (db, controller, project.Id, row.Id, project.OrganizationId);
    }


    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static EditProposalRateLimiter Limiter(int perHour, TimeProvider? clock = null) =>
        new(Microsoft.Extensions.Options.Options.Create(new EditProposalOptions { MaxPerHourPerUserProject = perHour }), clock);

    [Fact]
    public async Task R11_1_the_hourly_cap_answers_429_with_a_plain_message_and_retry_after_and_never_calls_the_model()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) =
            ChargedProposalSetup(charges, limiter: Limiter(2));
        using InferenceApiDbContext ownedDb = db;
        FakeModel model = new(new EditProposalOutput { Summary = "ok", Actions = [] });
        EditProposalRequest body = new("recut", null, null);

        (await controller.ProposeEdit(projectId, timelineId, body, model, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        (await controller.ProposeEdit(projectId, timelineId, body, model, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        FakeModel overCap = new(new EditProposalOutput { Summary = "ok", Actions = [] });
        int holdsBefore = charges.Holds.Count;

        ObjectResult limited = (await controller.ProposeEdit(projectId, timelineId, body, overCap, CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Subject;

        limited.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        JsonElement json = JsonSerializer.SerializeToElement(limited.Value);
        json.GetProperty("code").GetString().Should().Be("PROPOSAL_RATE_LIMITED");
        json.GetProperty("retryAfter").GetInt32().Should().BeGreaterThan(0);
        json.GetProperty("limit").GetInt32().Should().Be(2);
        json.GetProperty("message").GetString().Should().Contain("Try again in").And.NotContain("PROPOSAL");
        controller.Response.Headers.RetryAfter.ToString().Should().NotBeEmpty();
        overCap.LastAgent.Should().BeNull("the model is not called over the cap");
        charges.Holds.Count.Should().Be(holdsBefore, "no hold is taken over the cap");
    }

    [Fact]
    public async Task R11_1_a_refused_402_does_not_use_up_a_cap_slot()
    {
        var charges = new Billing.FakeNonWorkflowCharges { RefuseHolds = true };
        EditProposalRateLimiter limiter = Limiter(1);
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) =
            ChargedProposalSetup(charges, limiter: limiter);
        using InferenceApiDbContext ownedDb = db;
        FakeModel model = new(new EditProposalOutput { Summary = "never", Actions = [] });

        for (int i = 0; i < 3; i++)
        {
            (await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None))
                .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        }

        model.LastAgent.Should().BeNull();
        limiter.Peek(db.Projects.First().OwnerId, projectId).Used.Should().Be(0);
    }

    [Fact]
    public void R11_1_the_cap_window_slides_after_an_hour()
    {
        ManualClock clock = new();
        EditProposalRateLimiter limiter = Limiter(1, clock);
        Guid user = Guid.NewGuid(), project = Guid.NewGuid();

        limiter.TryAcquire(user, project, out _).Should().BeTrue();
        limiter.TryAcquire(user, project, out EditProposalRateState full).Should().BeFalse();
        full.RetryAfterSeconds.Should().BeInRange(3590, 3600);
        clock.Now = clock.Now.AddMinutes(61);
        limiter.TryAcquire(user, project, out _).Should().BeTrue();
        limiter.TryAcquire(Guid.NewGuid(), project, out _).Should().BeTrue("the cap is per user");
    }

    [Fact]
    public async Task R11_1_the_estimate_prices_before_asking_without_calling_the_model_or_the_cap()
    {
        var charges = new Billing.FakeNonWorkflowCharges { AvailableMillis = 2_000 };
        EditProposalRateLimiter limiter = Limiter(5);
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) =
            ChargedProposalSetup(charges, limiter: limiter);
        using InferenceApiDbContext ownedDb = db;

        // billing is "on" only when a cost estimator says so; with none the estimate answers billing off.
        EditProposalEstimateResponse off = (await controller.EstimateProposal(projectId, timelineId, new EditProposalRequest("recut", null, "tighten it"), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditProposalEstimateResponse>().Subject;
        off.BillingEnabled.Should().BeFalse();
        off.Affordable.Should().BeTrue();
        off.Disclosure.BriefChars.Should().Be(10);
        off.Disclosure.Summary.Should().Contain("No video, audio or transcript");
        off.Limit.Should().Be(5);
        charges.Holds.Should().BeEmpty("an estimate never holds credits");
    }

    [Fact]
    public async Task R11_1_the_estimate_with_billing_on_shows_the_hold_the_balance_and_affordability()
    {
        var charges = new Billing.FakeNonWorkflowCharges { AvailableMillis = 2_000 };
        Mock<ReelBolt.Inference.Api.Services.Billing.IWorkflowCostEstimator> estimator = new();
        estimator.SetupGet(e => e.BillingEnabled).Returns(true);
        (InferenceApiDbContext db, EditTimelinesController _, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        ICurrentUser caller = Caller(db.Projects.First().OwnerId);
        EditTimelinesController controller = new(db, caller, TestTenancy.Access(db, caller), new Mock<IFileStorageService>().Object,
            new Mock<IWorkflowExecutionService>().Object, charges, null, costEstimator: estimator.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        EditProposalEstimateResponse r = (await controller.EstimateProposal(projectId, timelineId, new EditProposalRequest("recut", null, null), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditProposalEstimateResponse>().Subject;

        r.BillingEnabled.Should().BeTrue();
        r.Credits.Should().Be(5, "no chat provider resolves, so the hold floor applies");
        r.BalanceCredits.Should().Be(2);
        r.Affordable.Should().BeFalse();
        charges.Holds.Should().BeEmpty();
        charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task R11_1_the_estimate_of_a_foreign_timeline_is_404_and_a_bad_agent_400()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, _, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;

        (await controller.EstimateProposal(projectId, Guid.NewGuid(), new EditProposalRequest("recut", null, null), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
        (await controller.EstimateProposal(Guid.NewGuid(), Guid.NewGuid(), new EditProposalRequest("recut", null, null), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task R11_1_the_response_discloses_the_context_as_counts_and_a_model_label_never_ids()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;

        EditProposalResponse ok = (await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, "short"),
                new FakeModel(new EditProposalOutput { Summary = "ok", Actions = [] }), CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditProposalResponse>().Subject;

        ok.Disclosure.Should().NotBeNull();
        ok.Disclosure!.ItemCount.Should().BeGreaterThan(0);
        ok.Disclosure.BriefChars.Should().Be(5);
        ok.Disclosure.TranscriptSeconds.Should().Be(0);
        ok.Disclosure.Summary.Should().NotMatchRegex("[0-9a-f]{8}-[0-9a-f]{4}-");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    public async Task R11_1_a_failed_answer_that_metered_nothing_releases_the_hold_and_answers_424_without_a_charge(string text)
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;

        ObjectResult failed = (await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null),
                new FakeModel(AgentEditProposalModel.Parse(text)), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Subject;

        failed.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);
        JsonSerializer.SerializeToElement(failed.Value).GetProperty("code").GetString().Should().Be("PROPOSAL_FAILED");
        charges.Released.Should().ContainSingle("a failure before any tokens is refunded");
        charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task R11_1_an_unparseable_answer_that_used_tokens_is_charged_what_it_metered()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        EditProposalRateLimiter limiter = Limiter(5);
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges, limiter: limiter);
        using InferenceApiDbContext ownedDb = db;
        CapturingSink sink = new();
        UsageRecorder recorder = new(new StaticPriceService(), sink, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);
        ResolvedInferenceProvider provider = new(Guid.NewGuid(), "platform", InferenceProviderKind.OpenAICompatible, "http://x", "gpt-test", "", 30);
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeteringChatClient(new UsageReportingChatClient(100, 20, answer: "this is not json"), provider, recorder));
        AgentEditProposalModel model = new(chatClients.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentEditProposalModel>.Instance);

        ObjectResult failed = (await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Subject;

        failed.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        long metered = sink.Events.Sum(e => e.CreditsMillis);
        metered.Should().BeGreaterThan(0);
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(metered);
        charges.Released.Should().BeEmpty("the tokens were consumed, so they are charged");
        JsonSerializer.SerializeToElement(failed.Value).GetProperty("creditsUsed").GetDouble().Should().BeGreaterThan(0);
        limiter.Peek(db.Projects.First().OwnerId, projectId).Used.Should().Be(1, "an unparseable answer costs money, so it uses a slot");
    }

    [Fact]
    public void R11_1_a_valid_empty_answer_is_not_a_failure()
    {
        AgentEditProposalModel.Parse("{\"actions\":[],\"summary\":\"nothing to change\"}").Failed.Should().BeFalse();
    }

    [Fact]
    public async Task A_proposal_the_workspace_cannot_afford_answers_402_and_never_calls_the_model()
    {
        var charges = new Billing.FakeNonWorkflowCharges { RefuseHolds = true, AvailableMillis = 2_000 };
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, Guid org) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        FakeModel model = new(new EditProposalOutput { Summary = "never", Actions = [] });

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None);

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        JsonElement body = JsonSerializer.SerializeToElement(refused.Value);
        body.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        body.GetProperty("required").GetDouble().Should().Be(5, "the default proposal hold is 5 credits");
        body.GetProperty("balance").GetDouble().Should().Be(2);
        model.LastAgent.Should().BeNull("the model is never called");
        charges.Holds.Should().ContainSingle().Which.Should().Be((org, UsageSource.TimelineAssist, 5_000L));
    }

    [Fact]
    public async Task A_proposal_settles_its_hold_at_what_the_model_call_metered()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        CapturingSink sink = new();
        UsageRecorder recorder = new(
            new StaticPriceService(), sink, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);
        ResolvedInferenceProvider provider = new(Guid.NewGuid(), "platform", InferenceProviderKind.OpenAICompatible, "http://x", "gpt-test", "", 30);
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeteringChatClient(new UsageReportingChatClient(100, 20, answer: "{\"actions\":[],\"summary\":\"ok\"}"), provider, recorder));
        AgentEditProposalModel model = new(chatClients.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentEditProposalModel>.Instance);

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        long metered = sink.Events.Sum(e => e.CreditsMillis);
        metered.Should().BeGreaterThan(0);
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(metered);
        charges.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task A_proposal_whose_platform_usage_could_not_be_priced_is_charged_its_hold_never_zero()
    {
        // the model call metered platform tokens no price row prices (recorded at 0 credits, flagged
        // unpriced), so the proposal settles at its hold (the 5 credit default), not at 0.
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        UsageRecorder recorder = new(
            new EmptyPriceService(), new DiscardSink(), Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);
        ResolvedInferenceProvider provider = new(Guid.NewGuid(), "platform", InferenceProviderKind.OpenAICompatible, "http://x", "gpt-unpriced", "", 30);
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeteringChatClient(new UsageReportingChatClient(100, 20, answer: "{\"actions\":[],\"summary\":\"ok\"}"), provider, recorder));
        AgentEditProposalModel model = new(chatClients.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentEditProposalModel>.Instance);

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        (long Held, long Settled) = (charges.Holds.Should().ContainSingle().Subject.EstimateMillis,
            charges.Settled.Should().ContainSingle().Subject.ActualMillis);
        Settled.Should().Be(Held).And.BeGreaterThan(0);
    }

    [Fact]
    public async Task A_proposal_whose_chat_model_is_unpriced_is_409_MODEL_UNPRICED_before_any_hold()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        var refusal = new ModelUnpricedException(InferenceProviderCapability.Chat, "Pricey chat");
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients.Setup(c => c.ResolveProviderAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
        chatClients.Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges, chatClients.Object);
        using InferenceApiDbContext ownedDb = db;
        AgentEditProposalModel model = new(chatClients.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentEditProposalModel>.Instance);

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None);

        ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonElement body = JsonSerializer.SerializeToElement(conflict.Value);
        body.GetProperty("code").GetString().Should().Be("MODEL_UNPRICED");
        body.GetProperty("message").GetString().Should().Be(refusal.Message);
        charges.Holds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_proposal_whose_model_turns_out_unpriced_at_the_call_is_409_and_releases_its_hold()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        var refusal = new ModelUnpricedException(InferenceProviderCapability.Chat, "Pricey chat");
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients.Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        AgentEditProposalModel model = new(chatClients.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentEditProposalModel>.Instance);

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None);

        ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.SerializeToElement(conflict.Value).GetProperty("code").GetString().Should().Be("MODEL_UNPRICED");
        charges.Released.Should().ContainSingle("no model call was made");
        charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_proposal_with_no_chat_provider_releases_its_hold()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), new FakeModel(null), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        charges.Released.Should().ContainSingle();
        charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_proposal_brief_longer_than_2000_characters_answers_400_before_any_hold_or_model_call()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        FakeModel model = new(new EditProposalOutput { Summary = "never", Actions = [] });

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(
            projectId, timelineId, new EditProposalRequest("recut", null, new string('x', 2001)), model, CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.SerializeToElement(bad.Value).GetProperty("message").GetString().Should().Be("The brief can be at most 2000 characters.");
        model.LastAgent.Should().BeNull();
        charges.Holds.Should().BeEmpty();

        (await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, new string('x', 2000)), model, CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>("2000 characters is still allowed");
    }

    [Fact]
    public async Task A_proposal_hold_is_sized_from_the_request_priced_on_the_resolved_chat_model()
    {
        var charges = new Billing.FakeNonWorkflowCharges { EstimateMillis = 9_000 };
        ResolvedInferenceProvider provider = new(Guid.NewGuid(), "platform", InferenceProviderKind.Anthropic, "http://x", "claude-test", "", 30);
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients
            .Setup(c => c.ResolveProviderAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, Guid org) = ChargedProposalSetup(charges, chatClients.Object);
        using InferenceApiDbContext ownedDb = db;
        string brief = new('b', 1_200);

        await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, brief), new FakeModel(new EditProposalOutput { Summary = "ok", Actions = [] }), CancellationToken.None);

        charges.Estimates.Should().HaveCount(2);
        PriceRequest estimate = charges.Estimates[0];
        (estimate.ModelCapabilityId, estimate.Kind, estimate.Capability, estimate.ModelCode, estimate.Unit, estimate.BillingMode)
            .Should().Be((provider.ProviderId, InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat, "claude-test", UsageKind.LlmInputTokens, UsageBillingMode.Platform));
        estimate.Quantity.Should().BeGreaterThan(brief.Length / 4m, "about one token per four characters of the brief plus the serialized view");
        charges.Estimates.Should().OnlyContain(e => e.PromptTokens == (long)estimate.Quantity, "the prompt length picks a gt{N}k tier");

        // T-032 (low a): the output the model may write (its MaxOutputTokens, 2000) is priced at the output price too.
        PriceRequest output = charges.Estimates[1];
        (output.ModelCapabilityId, output.ModelCode, output.Unit, output.BillingMode, output.Quantity)
            .Should().Be((provider.ProviderId, "claude-test", UsageKind.LlmOutputTokens, UsageBillingMode.Platform, (decimal)AgentEditProposalModel.MaxOutputTokens));
        AgentEditProposalModel.MaxOutputTokens.Should().Be(2000);
        charges.Holds.Should().ContainSingle().Which.Should().Be((org, UsageSource.TimelineAssist, 18_000L));
    }

    /// <summary>
    /// QA-4: a proposal whose agent resolves to a chat provider the workspace pays for itself (BYO, or a Runner) costs no
    /// credits, so it takes no hold and is never refused, even at a zero balance (as editor ops already do).
    /// </summary>
    [Fact]
    public async Task A_proposal_on_a_byo_chat_provider_takes_no_hold_and_is_not_refused_at_zero_balance()
    {
        var charges = new Billing.FakeNonWorkflowCharges { RefuseHolds = true, AvailableMillis = 0, EstimateMillis = 9_000 };
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients
            .Setup(c => c.ResolveProviderAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(Guid.NewGuid(), "org llm", InferenceProviderKind.OpenAICompatible, "http://x", "qwen3", "", 30,
                OrganizationId: Guid.NewGuid(), IsPlatformManaged: false));
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges, chatClients.Object);
        using InferenceApiDbContext ownedDb = db;
        FakeModel model = new(new EditProposalOutput { Summary = "ok", Actions = [] });

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, "short"), model, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        model.LastAgent.Should().Be("recut");
        charges.Holds.Should().BeEmpty();
        charges.Settled.Should().BeEmpty();
        charges.Released.Should().BeEmpty();
    }

    /// <summary>
    /// T-026 (finding 5): the chat provider is resolved ONCE and the proposal's call runs on that row, so the hold and
    /// the call cannot disagree. Here the agent's provider would change between the estimate (BYO: no hold) and the call
    /// (Platform): the call still runs on the BYO row the estimate saw, and the Platform row is never called.
    /// </summary>
    [Fact]
    public async Task A_proposal_calls_the_model_on_the_provider_its_hold_was_sized_on()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        ResolvedInferenceProvider byo = new(Guid.NewGuid(), "org llm", InferenceProviderKind.OpenAICompatible, "http://x", "qwen3", "", 30,
            OrganizationId: Guid.NewGuid(), IsPlatformManaged: false);
        ResolvedInferenceProvider platform = new(Guid.NewGuid(), "platform", InferenceProviderKind.OpenAICompatible, "http://x", "gpt-test", "", 30);
        UsageRecorder recorder = new(new StaticPriceService(), new DiscardSink(), Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients
            .SetupSequence(c => c.ResolveProviderAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(byo)
            .ReturnsAsync(platform);
        chatClients
            .Setup(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeteringChatClient(new UsageReportingChatClient(100, 20, answer: "{\"actions\":[],\"summary\":\"ok\"}"), platform, recorder));
        List<ResolvedInferenceProvider> called = [];
        Mock<IChatClientFactory> factory = new();
        factory.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>()))
            .Returns((ResolvedInferenceProvider p) =>
            {
                called.Add(p);
                return new MeteringChatClient(new UsageReportingChatClient(100, 20, answer: "{\"actions\":[],\"summary\":\"ok\"}"), p, recorder);
            });
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges, chatClients.Object);
        using InferenceApiDbContext ownedDb = db;
        AgentEditProposalModel model = new(chatClients.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentEditProposalModel>.Instance, factory.Object);

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        called.Should().ContainSingle().Which.Should().Be(byo, "the call runs on the row the hold was sized on");
        chatClients.Verify(c => c.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        charges.Holds.Should().BeEmpty("a BYO row costs no credits");
        charges.UnheldCharges.Should().BeEmpty("nothing Platform-billed ran");
    }

    /// <summary>
    /// T-026 (finding 5): if a proposal that took no hold still metered Platform usage, it is charged after the fact,
    /// once, under the proposal's request id, never left free.
    /// </summary>
    [Fact]
    public async Task A_proposal_that_took_no_hold_but_metered_platform_usage_is_charged_after_the_call()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        ResolvedInferenceProvider byo = new(Guid.NewGuid(), "org llm", InferenceProviderKind.OpenAICompatible, "http://x", "qwen3", "", 30,
            OrganizationId: Guid.NewGuid(), IsPlatformManaged: false);
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients
            .Setup(c => c.ResolveProviderAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(byo);
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, Guid org) = ChargedProposalSetup(charges, chatClients.Object);
        using InferenceApiDbContext ownedDb = db;
        UsageRecorder recorder = new(new StaticPriceService(), new DiscardSink(), Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);
        PlatformMeteringModel model = new(recorder);

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        charges.Holds.Should().BeEmpty();
        model.Metered.Should().BeGreaterThan(0);
        (Guid Org, UsageSource Source, long ActualMillis, Guid RequestId) charged = charges.UnheldCharges.Should().ContainSingle().Subject;
        (charged.Org, charged.Source, charged.ActualMillis).Should().Be((org, UsageSource.TimelineAssist, model.Metered));
        charged.RequestId.Should().NotBe(Guid.Empty);
    }

    /// <summary>
    /// T-040 (item 4): with no proposal hold floor (TimelineProposalHoldCredits = 0) and no chat provider resolved, the
    /// hold reserves nothing (NO_PLATFORM_COST); a call that still metered Platform usage is charged after the fact,
    /// never settled at 0.
    /// </summary>
    [Fact]
    public async Task A_proposal_whose_hold_reserved_nothing_but_metered_platform_usage_is_charged_after_the_call()
    {
        var charges = new Billing.FakeNonWorkflowCharges { EstimateMillis = 0 };
        charges.Options.NonWorkflow.TimelineProposalHoldCredits = 0;
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, Guid org) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        UsageRecorder recorder = new(new StaticPriceService(), new DiscardSink(), Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);
        PlatformMeteringModel model = new(recorder);

        ActionResult<EditProposalResponse> result = await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        charges.Holds.Should().ContainSingle().Which.EstimateMillis.Should().Be(0);
        model.Metered.Should().BeGreaterThan(0);
        (Guid Org, UsageSource Source, long ActualMillis, Guid RequestId) charged = charges.UnheldCharges.Should().ContainSingle().Subject;
        (charged.Org, charged.Source, charged.ActualMillis).Should().Be((org, UsageSource.TimelineAssist, model.Metered));
    }

    /// <summary>A proposal model that meters a Platform call of its own, whatever provider it was handed.</summary>
    private sealed class PlatformMeteringModel(UsageRecorder recorder) : IEditProposalModel
    {
        public long Metered { get; private set; }

        public async Task<EditProposalOutput?> ProposeAsync(string agent, string view, string? brief, ResolvedInferenceProvider? provider, CancellationToken ct)
        {
            Metered = await recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.LlmInputTokens, 1_000, Guid.NewGuid(), InferenceProviderKind.OpenAICompatible, "gpt-test", UsageBillingMode.Platform), ct);
            return new EditProposalOutput { Summary = "ok", Actions = [] };
        }
    }

    [Fact]
    public async Task A_proposal_hold_never_goes_below_the_configured_floor()
    {
        var charges = new Billing.FakeNonWorkflowCharges { EstimateMillis = 200 };
        Mock<IAgentChatClientProvider> chatClients = new();
        chatClients
            .Setup(c => c.ResolveProviderAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(Guid.NewGuid(), "platform", InferenceProviderKind.Anthropic, "http://x", "claude-test", "", 30));
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, Guid org) = ChargedProposalSetup(charges, chatClients.Object);
        using InferenceApiDbContext ownedDb = db;

        await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, "short"), new FakeModel(new EditProposalOutput { Summary = "ok", Actions = [] }), CancellationToken.None);

        charges.Holds.Should().ContainSingle().Which.Should().Be((org, UsageSource.TimelineAssist, 5_000L));
    }

    // ---- recut-timeout: bounded sync path and background proposal jobs ----

    private sealed class BlockingModel : IEditProposalModel
    {
        private readonly TaskCompletionSource<EditProposalOutput?> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public void Finish(EditProposalOutput? output) => _gate.SetResult(output);

        public async Task<EditProposalOutput?> ProposeAsync(string agent, string view, string? brief, ResolvedInferenceProvider? provider, CancellationToken ct)
        {
            Started.TrySetResult();
            try { return await _gate.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }

    private static EditProposalJobStore Jobs(double timeout = 30, double sync = 30, double abandon = 30, int maxRunning = 3, int maxJobs = 500, int maxStored = 10, TimeProvider? clock = null) =>
        new(Microsoft.Extensions.Options.Options.Create(new EditProposalOptions
        {
            TimeoutSeconds = timeout,
            SyncTimeoutSeconds = sync,
            AbandonAfterSeconds = abandon,
            MaxRunningPerUser = maxRunning,
            MaxJobs = maxJobs,
            MaxStoredPerUser = maxStored
        }), clock);

    private static async Task<ObjectResult> PollUntilDoneAsync(EditTimelinesController controller, Guid projectId, Guid timelineId, Guid jobId)
    {
        for (int i = 0; i < 500; i++)
        {
            ObjectResult r = (await controller.GetProposalJob(projectId, timelineId, jobId, ((EditProposalJobStore)controller.HttpContext.Items["jobs"]!), CancellationToken.None))
                .Should().BeAssignableTo<ObjectResult>().Subject;
            if (r.StatusCode != StatusCodes.Status202Accepted) return r;
            await Task.Delay(10);
        }

        throw new TimeoutException("job never finished");
    }

    private static async Task<Guid> StartJobAsync(EditTimelinesController controller, Guid projectId, Guid timelineId, IEditProposalModel model, EditProposalJobStore jobs)
    {
        controller.HttpContext.Items["jobs"] = jobs;
        ObjectResult accepted = (await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, CancellationToken.None, true, jobs))
            .Result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        JsonElement json = JsonSerializer.SerializeToElement(accepted.Value);
        json.GetProperty("state").GetString().Should().Be("running");
        return json.GetProperty("jobId").GetGuid();
    }

    private static string Code(ObjectResult r) => JsonSerializer.SerializeToElement(r.Value).GetProperty("code").GetString()!;

    [Fact]
    public async Task Background_proposal_answers_202_then_polls_running_then_the_proposal()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs();
        BlockingModel model = new();

        Guid jobId = await StartJobAsync(controller, projectId, timelineId, model, jobs);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ObjectResult running = (await controller.GetProposalJob(projectId, timelineId, jobId, jobs, CancellationToken.None)).Should().BeAssignableTo<ObjectResult>().Subject;
        running.StatusCode.Should().Be(StatusCodes.Status202Accepted);

        model.Finish(new EditProposalOutput { Summary = "done", Actions = [] });
        ObjectResult done = await PollUntilDoneAsync(controller, projectId, timelineId, jobId);

        done.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<EditProposalResponse>().Which.Summary.Should().Be("done");
        (await controller.GetProposalJob(projectId, timelineId, jobId, jobs, CancellationToken.None)).Should().BeOfType<OkObjectResult>("a finished result can be read again");
        charges.Settled.Should().ContainSingle();
    }

    [Fact]
    public async Task Polling_an_unknown_foreign_user_or_foreign_timeline_job_is_one_404()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs();
        BlockingModel model = new();
        Guid jobId = await StartJobAsync(controller, projectId, timelineId, model, jobs);

        (await controller.GetProposalJob(projectId, timelineId, Guid.NewGuid(), jobs, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await controller.GetProposalJob(projectId, Guid.NewGuid(), jobId, jobs, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await jobs.PollAsync(jobId, Guid.NewGuid(), projectId, timelineId)).State.Should().Be(EditProposalPollState.NotFound, "another user's job is unknown");
        (await jobs.PollAsync(jobId, Guid.NewGuid(), Guid.NewGuid(), timelineId)).State.Should().Be(EditProposalPollState.NotFound, "another project's job is unknown");
        model.Finish(null);
    }

    [Fact]
    public async Task Background_timeout_is_424_PROPOSAL_TIMEOUT_with_the_hold_released_and_nothing_charged()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs(timeout: 0.05);
        BlockingModel model = new();

        Guid jobId = await StartJobAsync(controller, projectId, timelineId, model, jobs);
        ObjectResult done = await PollUntilDoneAsync(controller, projectId, timelineId, jobId);

        done.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);
        Code(done).Should().Be("PROPOSAL_TIMEOUT");
        model.Cancelled.Should().BeTrue();
        charges.Released.Should().ContainSingle();
        charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task An_abandoned_background_job_is_cancelled_and_its_hold_released()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs(abandon: 0.05);
        BlockingModel model = new();

        await StartJobAsync(controller, projectId, timelineId, model, jobs);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 500 && charges.Released.Count == 0; i++) await Task.Delay(10);

        model.Cancelled.Should().BeTrue();
        charges.Released.Should().ContainSingle();
        charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task PROPOSAL_FAILED_is_424_on_the_background_path_too()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs();

        Guid jobId = await StartJobAsync(controller, projectId, timelineId, new FakeModel(AgentEditProposalModel.Parse("not json at all")), jobs);
        ObjectResult done = await PollUntilDoneAsync(controller, projectId, timelineId, jobId);

        done.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);
        Code(done).Should().Be("PROPOSAL_FAILED");
    }

    [Fact]
    public async Task The_per_user_running_cap_answers_429_PROPOSALS_BUSY_before_any_hold()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs(maxRunning: 1);
        BlockingModel model = new();
        await StartJobAsync(controller, projectId, timelineId, model, jobs);
        int holds = charges.Holds.Count;

        ObjectResult busy = (await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), new BlockingModel(), CancellationToken.None, true, jobs))
            .Result.Should().BeOfType<ObjectResult>().Subject;

        busy.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        Code(busy).Should().Be("PROPOSALS_BUSY");
        charges.Holds.Count.Should().Be(holds);
        model.Finish(null);
    }

    [Fact]
    public async Task Sync_timeout_is_424_PROPOSAL_TIMEOUT_with_the_hold_released()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs(sync: 0.05);

        ObjectResult r = (await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), new BlockingModel(), CancellationToken.None, false, jobs))
            .Result.Should().BeOfType<ObjectResult>().Subject;

        r.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);
        Code(r).Should().Be("PROPOSAL_TIMEOUT");
        charges.Released.Should().ContainSingle();
        charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sync_request_cancelled_mid_model_call_releases_the_hold_and_charges_nothing()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using CancellationTokenSource cts = new();
        BlockingModel model = new();

        Task<ActionResult<EditProposalResponse>> call = controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), model, cts.Token);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await call.Invoking(c => c).Should().ThrowAsync<OperationCanceledException>();

        charges.Released.Should().ContainSingle();
        charges.Settled.Should().BeEmpty();
    }

    private sealed class ThrowingModel : IEditProposalModel
    {
        public Task<EditProposalOutput?> ProposeAsync(string agent, string view, string? brief, ResolvedInferenceProvider? provider, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task A_finished_background_job_nobody_reads_is_released_not_settled()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs(abandon: 0.1);

        await StartJobAsync(controller, projectId, timelineId, new FakeModel(new EditProposalOutput { Summary = "ok", Actions = [] }), jobs);
        for (int i = 0; i < 500 && charges.Released.Count == 0; i++) await Task.Delay(10);

        charges.Released.Should().ContainSingle();
        charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task The_first_read_settles_once_and_a_second_read_does_not_charge_again()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs();

        Guid jobId = await StartJobAsync(controller, projectId, timelineId, new FakeModel(new EditProposalOutput { Summary = "ok", Actions = [] }), jobs);
        await WaitFinishedAsync(jobs, jobId);
        charges.Settled.Should().BeEmpty("nothing is charged before the answer is read");

        await PollUntilDoneAsync(controller, projectId, timelineId, jobId);
        await controller.GetProposalJob(projectId, timelineId, jobId, jobs, CancellationToken.None);

        charges.Settled.Should().ContainSingle();
        charges.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_concurrent_reads_settle_once()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs();
        BlockingModel model = new();
        Guid jobId = await StartJobAsync(controller, projectId, timelineId, model, jobs);
        model.Finish(new EditProposalOutput { Summary = "ok", Actions = [] });
        await PollUntilDoneAsync(controller, projectId, timelineId, jobId);
        charges.Settled.Clear();

        // Fresh job so both reads race for the first delivery.
        BlockingModel model2 = new();
        Guid job2 = await StartJobAsync(controller, projectId, timelineId, model2, jobs);
        model2.Finish(new EditProposalOutput { Summary = "ok", Actions = [] });
        await WaitFinishedAsync(jobs, job2);
        await Task.WhenAll(
            controller.GetProposalJob(projectId, timelineId, job2, jobs, CancellationToken.None),
            controller.GetProposalJob(projectId, timelineId, job2, jobs, CancellationToken.None));

        charges.Settled.Should().ContainSingle();
    }

    [Fact]
    public async Task A_job_that_throws_before_delivery_is_released_and_stays_not_charged()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs();

        Guid jobId = await StartJobAsync(controller, projectId, timelineId, new ThrowingModel(), jobs);
        ObjectResult done = await PollUntilDoneAsync(controller, projectId, timelineId, jobId);

        done.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);
        charges.Released.Should().ContainSingle();
        charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_reservations_never_exceed_the_per_user_cap_and_a_refusal_gives_the_slot_back()
    {
        using EditProposalJobStore jobs = Jobs(maxRunning: 3);
        Guid user = Guid.NewGuid();
        bool[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => jobs.TryReserve(user))));
        results.Count(r => r).Should().Be(3);

        jobs.CancelReservation(user);
        jobs.TryReserve(user).Should().BeTrue("a cancelled reservation frees its slot");

        var refusing = new Billing.FakeNonWorkflowCharges { RefuseHolds = true };
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(refusing);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs2 = Jobs(maxRunning: 1);
        for (int i = 0; i < 3; i++)
        {
            (await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), new BlockingModel(), CancellationToken.None, true, jobs2))
                .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired, "a refused hold never leaves its slot taken");
        }
    }

    [Fact]
    public async Task A_full_store_answers_429_PROPOSALS_BUSY_before_any_hold()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        (InferenceApiDbContext db, EditTimelinesController controller, Guid projectId, Guid timelineId, _) = ChargedProposalSetup(charges);
        using InferenceApiDbContext ownedDb = db;
        using EditProposalJobStore jobs = Jobs(maxJobs: 1);
        BlockingModel model = new();
        await StartJobAsync(controller, projectId, timelineId, model, jobs);
        int holds = charges.Holds.Count;

        ObjectResult busy = (await controller.ProposeEdit(projectId, timelineId, new EditProposalRequest("recut", null, null), new BlockingModel(), CancellationToken.None, true, jobs))
            .Result.Should().BeOfType<ObjectResult>().Subject;

        busy.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        Code(busy).Should().Be("PROPOSALS_BUSY");
        charges.Holds.Count.Should().Be(holds);
        model.Finish(null);
    }

    private static async Task WaitFinishedAsync(EditProposalJobStore jobs, Guid jobId)
    {
        for (int i = 0; i < 500 && !jobs.IsFinished(jobId); i++) await Task.Delay(10);
        jobs.IsFinished(jobId).Should().BeTrue();
    }

    private static readonly EditProposalOutcome ReadyOutcome = new(StatusCodes.Status200OK, new { ok = true });

    private static Guid StartFake(EditProposalJobStore jobs, Guid user, List<bool> closes, bool finishImmediately = true, Guid? timeline = null)
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (finishImmediately) gate.SetResult();
        return jobs.Start(user, Guid.Empty, timeline ?? Guid.Empty, async _ =>
        {
            await gate.Task;
            return (ReadyOutcome, (Func<bool, Task>?)(deliver => { lock (closes) closes.Add(deliver); return Task.CompletedTask; }));
        });
    }

    private static EditProposalJobStore ManualJobs(ManualClock clock, int maxJobs = 500, int maxStored = 10, int maxRunning = 50) =>
        new(Microsoft.Extensions.Options.Options.Create(new EditProposalOptions
        {
            AbandonAfterSeconds = 45,
            DeliveredRetentionSeconds = 60,
            ResultRetentionSeconds = 600,
            MaxJobs = maxJobs,
            MaxStoredPerUser = maxStored,
            MaxRunningPerUser = maxRunning
        }), clock);

    [Fact]
    public async Task Delivery_racing_a_sweep_never_both_settles_and_releases()
    {
        for (int i = 0; i < 50; i++)
        {
            ManualClock clock = new();
            using EditProposalJobStore jobs = ManualJobs(clock);
            Guid user = Guid.NewGuid();
            List<bool> closes = [];
            Guid id = StartFake(jobs, user, closes);
            await WaitFinishedAsync(jobs, id);
            clock.Now = clock.Now.AddSeconds(46);

            Task<EditProposalPoll> poll = jobs.PollAsync(id, user, Guid.Empty, Guid.Empty);
            Task sweep = Task.Run(jobs.Sweep);
            EditProposalPoll result = await poll;
            await sweep;
            for (int w = 0; w < 500 && closes.Count == 0; w++) await Task.Delay(2); // a sweep's release runs on its own task

            closes.Should().ContainSingle();
            if (result.State == EditProposalPollState.Ready) closes[0].Should().BeTrue("a returned body was settled");
            else
            {
                result.State.Should().Be(EditProposalPollState.Expired);
                result.Outcome.Should().BeNull("a released job never returns its body");
                closes[0].Should().BeFalse();
            }
        }
    }

    [Fact]
    public async Task A_released_job_answers_expired_not_the_body_and_a_delivered_job_is_not_released_later()
    {
        ManualClock clock = new();
        using EditProposalJobStore jobs = ManualJobs(clock);
        Guid user = Guid.NewGuid();
        List<bool> released = [];
        Guid unread = StartFake(jobs, user, released);
        await WaitFinishedAsync(jobs, unread);
        clock.Now = clock.Now.AddSeconds(46);
        jobs.Sweep();
        (await jobs.PollAsync(unread, user, Guid.Empty, Guid.Empty)).Should().BeEquivalentTo(new EditProposalPoll(EditProposalPollState.Expired));
        for (int w = 0; w < 500 && released.Count == 0; w++) await Task.Delay(2);
        released.Should().Equal(false);

        List<bool> delivered = [];
        Guid read = StartFake(jobs, user, delivered);
        await WaitFinishedAsync(jobs, read);
        (await jobs.PollAsync(read, user, Guid.Empty, Guid.Empty)).State.Should().Be(EditProposalPollState.Ready);
        clock.Now = clock.Now.AddSeconds(50);
        jobs.Sweep();
        (await jobs.PollAsync(read, user, Guid.Empty, Guid.Empty)).State.Should().Be(EditProposalPollState.Ready, "a re-read inside the delivered retention still works");
        delivered.Should().Equal(true);
    }

    [Fact]
    public async Task Delivered_results_are_dropped_after_the_shorter_delivered_retention()
    {
        ManualClock clock = new();
        using EditProposalJobStore jobs = ManualJobs(clock);
        Guid user = Guid.NewGuid();
        Guid id = StartFake(jobs, user, []);
        await WaitFinishedAsync(jobs, id);
        (await jobs.PollAsync(id, user, Guid.Empty, Guid.Empty)).State.Should().Be(EditProposalPollState.Ready);

        clock.Now = clock.Now.AddSeconds(61);
        jobs.Sweep();

        (await jobs.PollAsync(id, user, Guid.Empty, Guid.Empty)).State.Should().Be(EditProposalPollState.NotFound);
    }

    [Fact]
    public async Task The_global_cap_counts_running_jobs_only_so_finished_results_cannot_starve_others()
    {
        ManualClock clock = new();
        using EditProposalJobStore jobs = ManualJobs(clock, maxJobs: 1);
        Guid a = Guid.NewGuid();
        Guid id = StartFake(jobs, a, []);
        await WaitFinishedAsync(jobs, id);

        jobs.TryReserve(Guid.NewGuid()).Should().BeTrue("a finished-unread job does not count against the global cap");

        StartFake(jobs, Guid.NewGuid(), [], finishImmediately: false);
        jobs.TryReserve(Guid.NewGuid()).Should().BeFalse("a running job plus the reservation fill it");
    }

    [Fact]
    public async Task The_per_user_stored_cap_evicts_the_oldest_delivered_result_and_refuses_when_nothing_is_evictable()
    {
        ManualClock clock = new();
        using EditProposalJobStore jobs = ManualJobs(clock, maxStored: 2);
        Guid user = Guid.NewGuid();
        Guid first = StartFake(jobs, user, []);
        await WaitFinishedAsync(jobs, first);
        await jobs.PollAsync(first, user, Guid.Empty, Guid.Empty);
        clock.Now = clock.Now.AddSeconds(1);
        Guid second = StartFake(jobs, user, []);
        await WaitFinishedAsync(jobs, second);
        await jobs.PollAsync(second, user, Guid.Empty, Guid.Empty);

        jobs.TryReserve(user).Should().BeTrue("the oldest delivered result makes room");
        (await jobs.PollAsync(first, user, Guid.Empty, Guid.Empty)).State.Should().Be(EditProposalPollState.NotFound);
        (await jobs.PollAsync(second, user, Guid.Empty, Guid.Empty)).State.Should().Be(EditProposalPollState.Ready);

        Guid user2 = Guid.NewGuid();
        StartFake(jobs, user2, [], finishImmediately: false);
        StartFake(jobs, user2, [], finishImmediately: false);
        jobs.TryReserve(user2).Should().BeFalse("running jobs are never evicted");
    }
}
