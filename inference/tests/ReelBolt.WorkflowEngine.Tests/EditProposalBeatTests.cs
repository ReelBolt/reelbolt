#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Proposals;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Cut to the music: the recut model is told in words which sound clip has a beat grid and may ask to end a picture
/// clip on a beat or a bar; the compiler turns those words into frames from the stored grid. Every model call is a fake.
/// </summary>
public class EditProposalBeatTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid FilePic = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid FileMusic = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    private static MediaClip Clip(int n, long inF, long outF, Guid file, long sourceIn = 0, double rate = 1.0, bool reverse = false, bool hold = false) =>
        new(Id(n), $"clip-{n}", inF, outF, new AssetRef(AssetRefKind.ProjectFile, file, null), sourceIn, rate, Reverse: reverse, Hold: hold);

    private static Track VideoTrack(params Item[] items) =>
        new(Id(900), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items);

    private static Track AudioTrack(params Item[] items) =>
        new(Id(901), TrackKind.Audio, "A1", null, 48, false, true, false, false, null, items);

    private static EditTimeline Doc(params Track[] tracks) =>
        new(Id(800), ProjectId, "Beats", Settings, tracks, [], null, null, null, null, null);

    /// <summary>A half-second grid (beat i at i/2 s) for 20 s.</summary>
    private static BeatFacts Grid(int beatsPerBar = 4, int firstDownbeat = 0, double confidence = 0.9, bool steady = true, bool drop = false) =>
        new(Enumerable.Range(0, 41).Select(i => i * 0.5).ToList(), 0.5, beatsPerBar, firstDownbeat, confidence, steady, "fast", drop);

    private static IReadOnlyDictionary<Guid, SourceFacts> Facts(BeatFacts? beats) =>
        new Dictionary<Guid, SourceFacts> { [FileMusic] = new SourceFacts([], [], [], null, beats) };

    /// <summary>i1 = picture clip, i2 = music clip (as i{n} aliases are numbered by track then item).</summary>
    private static EditTimeline PictureOverMusic(long pictureOut, MediaClip music) =>
        Doc(VideoTrack(Clip(1, 0, pictureOut, FilePic)), AudioTrack(music));

    private static EditProposal CompileOne(EditTimeline doc, BeatFacts? beats, string kind, string agent = EditProposalAgents.Recut, string item = "i1")
    {
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, Facts(beats), 6000);
        return EditProposalCompiler.Compile(doc, agent, new EditProposalOutput { Actions = [new() { Kind = kind, ItemId = item, Reason = "on the music" }] }, view, 30, () => Guid.NewGuid());
    }

    private static long Delta(EditProposal p) => p.Groups.Single().Ops.Single()["deltaFrames"]!.GetValue<long>();

    [Fact]
    public void The_loader_turns_a_stored_beat_result_into_beat_facts()
    {
        AnalysisBeatsResult stored = new(128, 0.469, 0, 0, 4, [0.0, 0.5, double.NaN, 1.0], 1, 20, 20, 8.0, null, 0.8, true, "fast", 0, 0, false);
        BeatFacts? facts = ProposalFactsLoader.ToBeatFacts(stored);
        facts.Should().NotBeNull();
        facts!.BeatTimesSec.Should().Equal(0.0, 0.5, 1.0);
        facts.TempoWord.Should().Be("fast");
        facts.HasDrop.Should().BeTrue();
        facts.FirstDownbeatIndex.Should().Be(1);
        ProposalFactsLoader.ToBeatFacts(stored with { BeatTimesSec = [] }).Should().BeNull("a grid with no beats is no beat facts");
        ProposalFactsLoader.ToBeatFacts(stored with { Bpm = 0 }).Should().BeNull();
    }

    [Fact]
    public void The_view_says_the_beat_in_words_only()
    {
        EditTimeline doc = PictureOverMusic(300, Clip(2, 0, 600, FileMusic));
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, Facts(Grid(drop: true)), 6000);
        string line = view.Text.Split('\n').Single(l => l.StartsWith("i2 music"));
        line.Should().Be("i2 music has a steady beat, fast tempo, four beats to a bar, and a drop");
        Regex.IsMatch(line["i2".Length..], "[0-9]").Should().BeFalse("beat facts carry no time, frame, tempo number or count in digits");
        view.Grounding!.Level.Should().Be("namesOnly", "beats are not transcript or shots");
        view.BeatClips.Should().ContainSingle().Which.ItemId.Should().Be(Id(2));
    }

    [Fact]
    public void An_unclear_or_drifting_beat_is_said_so()
    {
        EditTimeline doc = PictureOverMusic(300, Clip(2, 0, 600, FileMusic));
        EditProposalView.Build(doc, null, [], 30, Facts(Grid(confidence: 0.1)), 6000).Text.Should().Contain("i2 music: the beat is unclear");
        string drift = EditProposalView.Build(doc, null, [], 30, Facts(Grid(steady: false, beatsPerBar: 3)), 6000).Text;
        drift.Should().Contain("the tempo drifts").And.Contain("three beats to a bar");
    }

    [Fact]
    public void A_view_without_beats_is_byte_identical()
    {
        EditTimeline doc = PictureOverMusic(300, Clip(2, 0, 600, FileMusic));
        string plain = EditProposalView.Build(doc, null, [], 30).Text;
        EditProposalView withEmptyFacts = EditProposalView.Build(doc, null, [], 30, Facts(null), 6000);
        withEmptyFacts.Text.Should().Be(plain);
        withEmptyFacts.BeatClips.Should().BeNull();
        plain.Should().NotContain("Beat facts");
        // a picture clip's file with a grid never gets a beat line: only sound clips do
        EditTimeline pictureOnly = Doc(VideoTrack(Clip(1, 0, 300, FileMusic)));
        EditProposalView.Build(pictureOnly, null, [], 30, Facts(Grid()), 6000).Text.Should().NotContain("Beat facts");
    }

    [Fact]
    public void The_disclosure_mentions_the_beat_words_when_sent()
    {
        EditTimeline doc = PictureOverMusic(300, Clip(2, 0, 600, FileMusic));
        EditTimelinesController.DescribeProposalContext(EditProposalView.Build(doc, null, [], 30, Facts(Grid()), 6000), null, 30)
            .Summary.Should().Contain("beat of your music");
        EditTimelinesController.DescribeProposalContext(EditProposalView.Build(doc, null, [], 30), null, 30)
            .Summary.Should().NotContain("beat");
    }

    [Fact]
    public void The_recut_prompt_explains_the_two_words_and_keeps_the_no_numbers_rule()
    {
        string prompt = AgentEditProposalModel.SystemPrompt(EditProposalAgents.Recut);
        prompt.Should().Contain("endOnBeat").And.Contain("endOnBar").And.Contain("only when the facts say a sound clip has a beat");
        prompt.Should().Contain("Never write a time, a frame or a number");
        AgentEditProposalModel.SystemPrompt(EditProposalAgents.Graphics).Should().NotContain("endOnBeat");
    }

    [Fact]
    public void EndOnBeat_and_endOnBar_end_on_the_latest_line_inside_the_clip()
    {
        EditTimeline doc = PictureOverMusic(300, Clip(2, 0, 600, FileMusic));
        // beat i sits at frame 15 * i; the last one before frame 300 is 285; bars (every fourth) sit at 60 * k, the last is 240
        Delta(CompileOne(doc, Grid(), "endOnBeat")).Should().Be(-15);
        Delta(CompileOne(doc, Grid(), "endOnBar")).Should().Be(-60);
        EditProposal p = CompileOne(doc, Grid(), "endOnBeat");
        p.Groups.Single().Ops.Single().ToJsonString().Should().Be($"{{\"type\":\"trim\",\"itemId\":\"{Id(1)}\",\"edge\":\"out\",\"mode\":\"ripple\",\"deltaFrames\":-15}}");
        p.Rejected.Should().BeEmpty();
    }

    [Fact]
    public void The_grid_maps_through_source_in_and_playback_rate_like_the_web_mapper()
    {
        // source frames 30..630 play over record 0..300 at rate 2: beat i lands at round-half-up((15 i - 30) / 2)
        MediaClip music = Clip(2, 0, 300, FileMusic, sourceIn: 30, rate: 2.0);
        EditTimeline doc = PictureOverMusic(190, music);
        Delta(CompileOne(doc, Grid(), "endOnBeat")).Should().Be(-2, "beat 27 is at 187.5, which rounds half up to 188");
        Delta(CompileOne(doc, Grid(), "endOnBar")).Should().Be(-25, "bar 24 is at 165");
    }

    [Fact]
    public void A_reversed_music_clip_counts_from_its_source_end()
    {
        MediaClip music = Clip(2, 0, 300, FileMusic, reverse: true);
        EditTimeline doc = PictureOverMusic(200, music);
        // source end is frame 300, so beat i is at 300 - 15 i: 195 is the last line before 200
        Delta(CompileOne(doc, Grid(), "endOnBeat")).Should().Be(-5);
        // downbeats at beats 1, 5, 9, ...: record 285, 225, 165 -> 165
        Delta(CompileOne(doc, Grid(firstDownbeat: 1), "endOnBar")).Should().Be(-35);
    }

    [Fact]
    public void Without_beat_facts_or_a_usable_line_the_action_is_rejected_never_guessed()
    {
        EditTimeline doc = PictureOverMusic(300, Clip(2, 0, 600, FileMusic));
        EditProposal none = CompileOne(doc, null, "endOnBeat");
        none.Groups.Should().BeEmpty();
        none.Rejected.Should().ContainSingle().Which.Should().Contain("no music with a beat is listed");

        EditProposal held = CompileOne(PictureOverMusic(300, Clip(2, 0, 600, FileMusic, hold: true)), Grid(), "endOnBar");
        held.Groups.Should().BeEmpty();
        held.Rejected.Should().ContainSingle().Which.Should().Contain("held");

        EditProposal notUnder = CompileOne(PictureOverMusic(300, Clip(2, 0, 100, FileMusic)), Grid(), "endOnBeat");
        notUnder.Rejected.Should().ContainSingle().Which.Should().Contain("no music with a beat plays under the end");

        EditProposal tooShort = CompileOne(PictureOverMusic(10, Clip(2, 0, 600, FileMusic)), Grid(), "endOnBeat");
        tooShort.Rejected.Should().ContainSingle().Which.Should().Contain("no beat line inside");

        EditProposal onSound = CompileOne(doc, Grid(), "endOnBeat", item: "i2");
        onSound.Rejected.Should().ContainSingle().Which.Should().Contain("not a picture clip");

        EditProposal wrongAgent = CompileOne(doc, Grid(), "endOnBeat", EditProposalAgents.Graphics);
        wrongAgent.Rejected.Should().ContainSingle().Which.Should().Contain("is not something the graphics agent may propose");
    }

    [Fact]
    public void With_several_beat_bearing_music_clips_the_one_under_the_end_is_used()
    {
        // first music 0..150 (grid from frame 0), second 150..600 whose source starts at 0 again: beat i at 150 + 15 i
        MediaClip first = Clip(2, 0, 150, FileMusic);
        MediaClip second = Clip(3, 150, 600, FileMusic);
        EditTimeline doc = Doc(VideoTrack(Clip(1, 0, 400, FilePic)), AudioTrack(first, second));
        // under frame 400 is the second clip: 150 + 15 i < 400 -> i = 16 (390)
        Delta(CompileOne(doc, Grid(), "endOnBeat")).Should().Be(-10);
    }
}
