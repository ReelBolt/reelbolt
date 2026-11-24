using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The follow-up to the dynamic edit: a late DynamicsPlanner that sees the generated clip ids (docs/design/dynamic-edit.md).</summary>
public class DynamicsPlanTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static DynamicsEntryOutput Entry(string effect, string anchor, string other = "", string winner = "") =>
        new() { Effect = effect, AnchorId = anchor, OtherId = other, Winner = winner };

    // ---- the view ----

    private static VideoAnalysisArtifact Artifact(VideoAnalysisMusicBeat? beat, params (string Id, string? Summary)[] shots) =>
        new(1, new VideoAnalysisMedia(30, 30, 1, 1920, 1080),
            shots.Select((s, i) => new VideoAnalysisShot(s.Id, i * 3, i * 3 + 3,
                Caption: s.Summary is null ? null : new VideoShotCaption(s.Id, s.Summary, [], "", "", "", "", "", [], [], "Night", "", "", "", []))).ToList(),
            [], [], [], shots.Select(s => s.Id).Append("t0").ToList(),
            new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Music: beat is null ? null : new VideoAnalysisMusicTrack(System.Guid.NewGuid(), "track.mp3", beat));

    private static JsonArray Manifest() => (JsonArray)JsonNode.Parse("""
        [
          {"clipId":"v1t1","purpose":"cutaway","status":"succeeded","description":"a night street in the rain","storageKey":"projects/p/generated/a.mp4"},
          {"clipId":"v1t2","purpose":"cutaway","status":"succeeded","description":"a night street, second take","storageKey":"projects/p/generated/b.mp4"},
          {"clipId":"v2t1","purpose":"seambridge","status":"succeeded","description":"a bridge","storageKey":"projects/p/generated/c.mp4"},
          {"clipId":"v3t1","purpose":"endcard","status":"failed","description":"an end card","storageKey":null}
        ]
        """)!;

    [Fact]
    public void The_view_lists_offered_shots_the_usable_clips_the_selected_takes_and_the_music_as_words_and_counts()
    {
        var beat = new VideoAnalysisMusicBeat(128, 60.0 / 128, 240.0 / 128, 0, 0, 40, DropSec: 8, OutroSec: null, Confidence: 0.9, Tempo: "fast");
        JsonObject view = DynamicsPlannerViewBuilder.Build(
            Artifact(beat, ("s0", "a parked car"), ("s1", "a man by a window")), Manifest(), ["v1t1"], musicTrackId: null);

        string json = view.ToJsonString();
        // Sample of what the agent receives (the ids and words, never a key):
        // {"sourceShots":[{"id":"s0","words":"a parked car, night"},{"id":"s1","words":"a man by a window, night"}],
        //  "generatedClips":[{"id":"v1t1","purpose":"cutaway","words":"a night street in the rain"}],
        //  "selectedTakes":["v1t1"],"music":{"present":true,"tempo":"fast","bpm":128,"beatsPerBar":4,"dropPresent":true}}
        view["sourceShots"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).Should().Equal("s0", "s1");
        view["sourceShots"]![0]!["words"]!.GetValue<string>().Should().Be("a parked car, night");
        view["generatedClips"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()).Should().Equal("v1t1");
        view["generatedClips"]![0]!["purpose"]!.GetValue<string>().Should().Be("cutaway");
        view["selectedTakes"]!.AsArray().Select(c => c!.GetValue<string>()).Should().Equal("v1t1");
        view["music"]!["bpm"]!.GetValue<int>().Should().Be(128);
        view["music"]!["beatsPerBar"]!.GetValue<int>().Should().Be(4);
        view["music"]!["dropPresent"]!.GetValue<bool>().Should().BeTrue();
        json.Should().NotContain("storageKey").And.NotContain("projects/p").And.NotContain("t0");
    }

    [Fact]
    public void Without_a_take_selection_every_delivered_picture_clip_is_offered_and_a_missing_music_is_said_so()
    {
        JsonObject view = DynamicsPlannerViewBuilder.Build(Artifact(null, ("s0", null)), Manifest(), null, null);

        view["generatedClips"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()).Should().Equal("v1t1", "v1t2");
        view["selectedTakes"].Should().BeNull();
        view["music"]!["present"]!.GetValue<bool>().Should().BeFalse();
        view["sourceShots"]![0]!["words"].Should().BeNull();
        view["notes"]!.AsArray().Should().NotBeEmpty();
        view["notes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(n => n.Contains("only usable as otherId") && n.Contains("anchorId must be a source shot id"));
    }

    [Fact]
    public void The_music_of_the_downstream_compile_config_is_found_when_the_analysis_has_none()
    {
        var track = System.Guid.NewGuid();
        var compile = new VideoCompileStepConfig(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1)
        {
            DynamicsPlan = new ExtractInputRef(ExtractInputSource.Previous),
            MusicTrackProjectFileId = track
        };
        var steps = new List<WorkflowStep>
        {
            new() { StepOrder = 2, StepType = StepType.Agent },
            new() { StepOrder = 3, StepType = StepType.VideoCompile, VideoCompileConfigJson = JsonSerializer.Serialize(compile, Options) }
        };
        DynamicsPlannerViewBuilder.ConfiguredMusicFileId(steps, 2).Should().Be(track);
        DynamicsPlannerViewBuilder.ConfiguredMusicFileId(steps, 3).Should().BeNull("no compile step follows");

        // An analysis without music plus the beat measured for that track: the view says so in words and counts, no key.
        var beat = new VideoAnalysisMusicBeat(140, 0.4286, 1.7143, 0, 0, 30, 6.884, null, 0.9, "Fast");
        JsonObject view = DynamicsPlannerViewBuilder.Build(Artifact(null, ("s0", null)), null, null, null, beat);
        view["music"]!["present"]!.GetValue<bool>().Should().BeTrue();
        view["music"]!["bpm"]!.GetValue<int>().Should().Be(140);
        view["music"]!["dropPresent"]!.GetValue<bool>().Should().BeTrue();
        view.ToJsonString().Should().NotContain(track.ToString());
    }

    [Fact]
    public void A_view_with_nothing_to_alternate_with_still_builds()
    {
        JsonObject view = DynamicsPlannerViewBuilder.Build(null, null, null, null);

        view["sourceShots"]!.AsArray().Should().BeEmpty();
        view["generatedClips"]!.AsArray().Should().BeEmpty();
        view["notes"]!.AsArray().Count.Should().Be(3);
    }

    // ---- the merge ----

    [Fact]
    public void Plan_entries_win_on_an_identical_anchor_and_effect_and_the_editors_other_entries_stay()
    {
        var editor = new List<DynamicsEntryOutput> { Entry("PunchIn", "s0"), Entry("BeatAlternate", "s1", "s2", "A"), Entry("WhipOnBar", "s2") };
        var plan = new List<DynamicsEntryOutput> { Entry("beatalternate", "s1", "v1t1", "B") };

        List<DynamicsEntryOutput> merged = VideoCompileStepExecutor.MergeDynamicsEntries(editor, plan);

        merged.Select(e => $"{e.Effect}:{e.AnchorId}:{e.OtherId}").Should().Equal("beatalternate:s1:v1t1", "PunchIn:s0:", "WhipOnBar:s2:");
    }

    [Fact]
    public void Merging_with_nothing_changes_nothing()
    {
        var editor = new List<DynamicsEntryOutput> { Entry("PunchIn", "s0") };

        VideoCompileStepExecutor.MergeDynamicsEntries(editor, null).Should().Equal(editor);
        VideoCompileStepExecutor.MergeDynamicsEntries(null, [Entry("PunchIn", "s0")]).Should().HaveCount(1);
        VideoCompileStepExecutor.MergeDynamicsEntries(null, null).Should().BeEmpty();
    }

    // ---- the config ----

    [Fact]
    public void A_compile_without_a_dynamics_plan_serializes_and_keys_byte_for_byte_as_before()
    {
        string json = JsonSerializer.Serialize(JsonSerializer.Deserialize<VideoCompileStepConfig>("""{"version":1,"decision":{"from":"Previous"}}""", Options)!, Options);
        json.Should().NotContainEquivalentOf("dynamicsPlan");
        DeterministicStepRevisions.For(StepType.VideoCompile, """{"dynamics":{"pace":"Fast"}}""").Should().NotContain("dynamics-plan");
        DeterministicStepRevisions.For(StepType.VideoCompile, "{}").Should().Be(DeterministicStepRevisions.For(StepType.VideoCompile));
    }

    [Fact]
    public void A_compile_with_a_dynamics_plan_round_trips_and_carries_the_revision_tag_only_then()
    {
        const string config = """{"version":1,"decision":{"from":"Step","stepOrder":2},"dynamicsPlan":{"from":"Step","stepOrder":6}}""";

        VideoCompileStepConfig parsed = JsonSerializer.Deserialize<VideoCompileStepConfig>(config, Options)!;
        parsed.DynamicsPlan.Should().NotBeNull();
        parsed.DynamicsPlan!.StepOrder.Should().Be(6);
        JsonSerializer.Serialize(parsed, Options).Should().Contain("\"dynamicsPlan\"");
        DeterministicStepRevisions.For(StepType.VideoCompile, config).Should().EndWith("+dynamics-plan-1");
        DeterministicStepRevisions.For(StepType.VideoCompile, """{"dynamicsPlan":null}""").Should().NotContain("dynamics-plan");
    }

    // ---- why words get dropped (docs/design/dynamic-edit.md "Why words get dropped"): the model is told as words ----

    [Fact]
    public void Each_shot_carries_a_length_word_against_the_beat_and_the_view_says_what_it_means()
    {
        var beat = new VideoAnalysisMusicBeat(128, 60.0 / 128, 240.0 / 128, 0, 0, 40, DropSec: 8, OutroSec: null, Confidence: 0.9, Tempo: "fast");
        VideoAnalysisArtifact artifact = new(1, new VideoAnalysisMedia(30, 30, 1, 1920, 1080),
            [new VideoAnalysisShot("s0", 0, 1), new VideoAnalysisShot("s1", 0, 3), new VideoAnalysisShot("s2", 0, 8)],
            [], [], [], ["s0", "s1", "s2"], new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Music: new VideoAnalysisMusicTrack(System.Guid.NewGuid(), "track.mp3", beat));

        JsonObject view = DynamicsPlannerViewBuilder.Build(artifact, null, null, musicTrackId: null);

        // 128 bpm: a beat is 0.47 s. 1 s is about two beats (short), 3 s about six (medium), 8 s about seventeen (long).
        view["sourceShots"]!.AsArray().Select(x => x!["length"]!.GetValue<string>()).Should().Equal("short", "medium", "long");
        string notes = string.Join(" ", view["notes"]!.AsArray().Select(n => n!.GetValue<string>()));
        notes.Should().Contain("only a long one takes division Bar").And.Contain("OnDrop applies only to the one shot that holds the music's drop");

        // Without a beat there is no length word and no such note (nothing to measure it against).
        JsonObject silent = DynamicsPlannerViewBuilder.Build(artifact with { Music = null }, null, null, musicTrackId: null);
        silent["sourceShots"]![0]!["length"].Should().BeNull();
        silent["notes"]!.ToJsonString().Should().NotContain("long one takes division Bar");
    }

    // ---- QA-DYN-45: the kept span after the editor's cut and the beat sync's trim, and the shot that holds the drop, in words ----

    private static VideoAnalysisArtifact DropFilm()
    {
        // 120 bpm: a beat is 0.5 s. Bar lines sit at 0.5 + 2k s (a deliberately odd bar phase), the drop at 4.7 s.
        var beat = new VideoAnalysisMusicBeat(120, 0.5, 2.0, 0, 0.5, 40, DropSec: 4.7, OutroSec: null, Confidence: 0.9, Tempo: "medium");
        return new VideoAnalysisArtifact(1, new VideoAnalysisMedia(30, 30, 1, 1920, 1080),
            [new VideoAnalysisShot("s0", 0, 6.25), new VideoAnalysisShot("s1", 6.25, 9.25), new VideoAnalysisShot("s2", 9.25, 12.25)],
            [], [], [], ["s0", "s1", "s2"], new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Music: new VideoAnalysisMusicTrack(System.Guid.NewGuid(), "track.mp3", beat));
    }

    [Fact]
    public void The_length_word_comes_from_the_kept_span_after_the_beat_sync_trim_not_from_the_offered_shot()
    {
        // s0 is offered as 6.25 s (12.5 beats: long). The compile cuts it on the drop (4.7 s, 9.4 beats): medium. s2 is not kept at all.
        IReadOnlyList<(string, string)> keep = [("s0", "s0"), ("s1", "s1")];

        JsonObject view = DynamicsPlannerViewBuilder.Build(DropFilm(), null, null, null, keep: keep, beatSync: VideoBeatSync.Bar);

        var shots = view["sourceShots"]!.AsArray();
        shots[0]!["length"]!.GetValue<string>().Should().Be("medium", "the offered shot is long, the kept span is not");
        shots[1]!["length"]!.GetValue<string>().Should().Be("short", "offered 3 s (six beats), but the beat sync cuts its tail back to the bar line at 6.5 s: 1.8 s, under four beats");
        shots[2]!["length"].Should().BeNull("an unkept shot has no kept length");
        DynamicsPlannerViewBuilder.Build(DropFilm(), null, null, null)["sourceShots"]![0]!["length"]!.GetValue<string>().Should().Be("long", "without a cut the offered shot is all there is");
    }

    [Fact]
    public void Exactly_one_shot_holds_the_drop_and_the_view_says_so_in_words()
    {
        IReadOnlyList<(string, string)> keep = [("s0", "s0"), ("s1", "s1")];

        JsonObject view = DynamicsPlannerViewBuilder.Build(DropFilm(), null, null, null, keep: keep, beatSync: VideoBeatSync.Bar);

        var shots = view["sourceShots"]!.AsArray();
        shots[0]!["drop"].Should().BeNull("the cut lands on the drop; the next shot starts with it");
        shots[1]!["drop"]!.GetValue<string>().Should().Be("holds the drop");
        shots.Count(x => x!["drop"] is not null).Should().Be(1);
        string notes = string.Join(" ", view["notes"]!.AsArray().Select(n => n!.GetValue<string>()));
        notes.Should().Contain("holds the drop");

        // The drop in no kept shot: no flag, and the view says OnDrop has no shot.
        JsonObject none = DynamicsPlannerViewBuilder.Build(DropFilm(), null, null, null, keep: [("s2", "s2")], beatSync: VideoBeatSync.Off);
        none["sourceShots"]!.AsArray().Should().OnlyContain(x => x!["drop"] == null);
        none["notes"]!.ToJsonString().Should().Contain("no kept shot");
        // Without a cut the old sentence stays and nothing is flagged.
        DynamicsPlannerViewBuilder.Build(DropFilm(), null, null, null)["sourceShots"]!.AsArray().Should().OnlyContain(x => x!["drop"] == null);
    }

    [Fact]
    public void The_nudge_does_not_claim_a_length_word_the_story_editors_view_does_not_have()
    {
        string fast = StyleNudgeService.Instance.BuildInstructions(
            AgentType.VideoStoryEditor, new RunStyleSnapshot(null, null, null, new StyleConfig(Dynamics: StyleDynamics.Fast)), null)!;
        fast.Should().NotContain("the view gives each shot's length as a word").And.Contain("the story editor's view does not");
    }

    [Fact]
    public void The_fast_wording_tells_the_model_what_a_shot_must_have_and_where_OnDrop_works_in_words_only()
    {
        foreach (AgentType agent in new[] { AgentType.DynamicsPlanner, AgentType.VideoStoryEditor })
        {
            string fast = StyleNudgeService.Instance.BuildInstructions(
                agent, new RunStyleSnapshot(null, null, null, new StyleConfig(Dynamics: StyleDynamics.Fast)), null)!;
            fast.Should().Contain("only a long shot takes division Bar")
                .And.Contain("OnDrop only works on the one shot that holds the music's drop")
                .And.Contain("same shot as a BeatAlternate or StrobeCut is dropped");

            // Medium leaves BeatAlternate and StrobeCut out, so the wording about them is not in its line.
            StyleNudgeService.Instance.BuildInstructions(
                    agent, new RunStyleSnapshot(null, null, null, new StyleConfig(Dynamics: StyleDynamics.Medium)), null)!
                .Should().NotContain("only a long shot takes division Bar");
        }
    }

    // ---- the agent ----

    [Fact]
    public void The_planner_has_project_read_and_workflow_control_only()
    {
        ToolGroupCatalog.GroupsFor(AgentType.DynamicsPlanner).Should().BeEquivalentTo([ToolGroup.ProjectRead, ToolGroup.WorkflowControl]);
    }

    [Fact]
    public void The_planner_role_gets_the_dynamics_wording_for_every_pace_and_nothing_without_one()
    {
        foreach (StyleDynamics pace in new[] { StyleDynamics.Calm, StyleDynamics.Medium, StyleDynamics.Fast })
        {
            string text = StyleNudgeService.Instance.BuildInstructions(
                AgentType.DynamicsPlanner, new RunStyleSnapshot(null, null, null, new StyleConfig(Dynamics: pace)), null)!;
            text.Should().Contain($"Dynamics: {pace.ToString().ToLowerInvariant()}.").And.Contain("ids in your view");
        }

        StyleNudgeService.Instance.BuildInstructions(
                AgentType.DynamicsPlanner, new RunStyleSnapshot(null, null, null, new StyleConfig(Pacing: StylePacing.Fast)), null)
            .Should().BeNull();
        StyleNudgeService.Instance.BuildInstructions(
                AgentType.DynamicsPlanner,
                new RunStyleSnapshot(null, null, null, new StyleConfig(Dynamics: StyleDynamics.Fast)), null)!
            .Should().Contain("BeatAlternate").And.Contain("generated clip");
    }

    // ---- the templates ----

    [Theory]
    [InlineData("video-derush-edit-broll", 6, 7)]
    [InlineData("video-derush-edit-dailies", 8, 9)]
    public void The_b_roll_templates_plan_dynamics_after_the_clips_and_before_the_compile(string key, int plannerOrder, int compileOrder)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll().Single(t => t.Key == key);

        template.Steps[plannerOrder - 1].AgentType.Should().Be(AgentType.DynamicsPlanner);
        template.Steps[compileOrder - 1].StepType.Should().Be(StepType.VideoCompile);
        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(template.Steps[compileOrder - 1].VideoCompileConfigJson!, Options)!;
        compile.DynamicsPlan!.From.Should().Be(ExtractInputSource.Step);
        compile.DynamicsPlan.StepOrder.Should().Be(plannerOrder);
        compile.GeneratedClips!.StepOrder.Should().Be(4);
        template.Steps[compileOrder].StepType.Should().Be(StepType.ReviewLoop);
    }
}
