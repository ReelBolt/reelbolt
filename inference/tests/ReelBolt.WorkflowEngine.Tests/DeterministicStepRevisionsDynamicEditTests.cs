using System;
using AwesomeAssertions;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Styles;
using ReelBolt.WorkflowEngine.Services.Styles;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>QA-DYN-42: an identical re-run of a compile whose output changed must miss the cache; a config that uses none of the changed behaviour keeps its key.</summary>
public class DeterministicStepRevisionsDynamicEditTests
{
    private static string Compile(string? json) => DeterministicStepRevisions.For(StepType.VideoCompile, json);
    private static string Analyze(string? json) => DeterministicStepRevisions.For(StepType.VideoAnalyze, json);
    private static readonly string Plain = DeterministicStepRevisions.For(StepType.VideoCompile);

    [Theory]
    [InlineData(null)]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"beatSync\":\"Off\",\"enableGeneratedClips\":false,\"dynamics\":null}")]
    [InlineData("{\"dynamics\":{\"enabled\":false}}")]
    public void A_compile_that_uses_none_of_the_changed_behaviour_keeps_its_key(string? json)
    {
        Compile(json).Should().Be(Plain);
    }

    [Fact]
    public void Generated_clips_add_the_film_timeline_suffix_only_with_the_flag()
    {
        Compile("{\"enableGeneratedClips\":true}").Should().Be(Plain + "+cutaway-clip-start-1+generated-film-timeline-1+cutaway-min-length-1");
        Compile("{\"enableGeneratedClips\":false}").Should().NotContain("generated-film-timeline-1");
        Compile("{\"beatSync\":\"Beat\"}").Should().NotContain("generated-film-timeline-1");
    }

    [Fact]
    public void Dynamics_adds_dynamics_2_and_the_beat_grid_read_suffix()
    {
        string tag = Compile("{\"dynamics\":{\"enabled\":true}}");
        tag.Should().Be(Plain + "+dynamics-1+dynamics-2+beat-grid-read-1");
        Compile("{\"dynamics\":{\"enabled\":false}}").Should().NotContain("dynamics-2").And.NotContain("beat-grid-read-1");
    }

    [Fact]
    public void The_film_time_grid_suffix_is_only_for_a_compile_with_a_cold_open_that_cuts_to_the_beat_or_runs_dynamics()
    {
        // QA-DYN-54: with generated clips the grid the cuts are placed on is read at the body's time, so the cuts change for the same config.
        Compile("{\"enableGeneratedClips\":true,\"beatSync\":\"Beat\"}").Should().EndWith("+beat-grid-film-time-1");
        Compile("{\"enableGeneratedClips\":true,\"dynamics\":{\"enabled\":true}}").Should().Contain("+beat-grid-film-time-1");
        Compile("{\"enableGeneratedClips\":true}").Should().NotContain("beat-grid-film-time-1", "no beat sync and no dynamics: nothing reads the grid");
        Compile("{\"beatSync\":\"Beat\"}").Should().NotContain("beat-grid-film-time-1", "no generated clips, no cold open");
        Compile("{\"dynamics\":{\"enabled\":true}}").Should().NotContain("beat-grid-film-time-1");
    }

    [Theory]
    [InlineData("{\"beatSync\":\"Beat\"}")]
    [InlineData("{\"beatSync\":2}")]
    public void Beat_sync_adds_the_beat_grid_read_suffix_without_dynamics(string json)
    {
        Compile(json).Should().Be(Plain + "+beat-sync-target-1+beat-grid-read-1");
        Compile(json).Should().NotContain("dynamics-2");
    }

    [Fact]
    public void The_music_beat_analysis_adds_the_beat_read_suffix_only_with_the_flag()
    {
        string plain = DeterministicStepRevisions.For(StepType.VideoAnalyze);
        Analyze("{\"version\":1}").Should().Be(plain);
        Analyze("{\"analyzeMusicBeats\":false}").Should().Be(plain);
        Analyze("{\"analyzeMusicBeats\":true}").Should().Be(plain + "+music-beat-read-1");
    }

    // QA-DYN-53: the RUN's style or brief pace turns Dynamics on at run time (RunStyleOverrides.ApplyToCompile), so the key has to see it.
    private static WorkflowExecution Run(StyleDynamics? styleDynamics, string? briefPace = null) => new()
    {
        StyleConfigJson = styleDynamics is null ? null : StyleJson.Serialize(new RunStyleSnapshot("s", null, null, new StyleConfig(Dynamics: styleDynamics))),
        RunBriefJson = briefPace is null ? null : RunBriefJson.Serialize(new StoredRunBrief(Pace: briefPace))
    };

    private static StepCacheKeyInputs CompileKey(string? config, WorkflowExecution run) => new()
    {
        StepType = StepType.VideoCompile,
        ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        AgentType = AgentType.Custom,
        AgentDefinitionId = Guid.Empty,
        AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
        ProjectFileFingerprint = "fp",
        MaxIterations = 1,
        VideoCompileConfigJson = config,
        RunStyle = RunStyleOverrides.CacheKeyComponent(run, []),
        RunDynamics = RunStyleOverrides.DynamicsCacheKeyComponent(run)
    };

    [Theory]
    [InlineData("Calm", null)]
    [InlineData("Fast", null)]
    [InlineData(null, "fast")]
    public void A_compile_without_dynamics_whose_run_turns_it_on_gets_the_dynamics_suffix(string? style, string? pace)
    {
        WorkflowExecution run = Run(style is null ? null : Enum.Parse<StyleDynamics>(style), pace);
        string component = RunStyleOverrides.DynamicsCacheKeyComponent(run)!;
        DeterministicStepRevisions.For(StepType.VideoCompile, "{\"version\":1}", component)
            .Should().Be(Plain + "+dynamics-1+dynamics-2+beat-grid-read-1");
        // the same config run plain keeps the old key, and a style or pace is a different key from no style or pace
        var builder = new StepCacheKeyBuilder();
        builder.Build(CompileKey("{\"version\":1}", run)).Should().NotBe(builder.Build(CompileKey("{\"version\":1}", Run(null))));
    }

    [Fact]
    public void A_run_with_no_style_or_pace_keeps_the_old_key_byte_identical()
    {
        var builder = new StepCacheKeyBuilder();
        StepCacheKeyInputs bare = CompileKey("{\"version\":1}", Run(null)) with { RunStyle = null, RunDynamics = null };
        builder.Build(CompileKey("{\"version\":1}", Run(null))).Should().Be(builder.Build(bare));
        RunStyleOverrides.CacheKeyComponent(Run(null), []).Should().BeNull();
        DeterministicStepRevisions.For(StepType.VideoCompile, "{\"version\":1}", null).Should().Be(Plain);
    }

    [Fact]
    public void A_compile_that_sets_its_own_dynamics_is_decided_by_its_own_config_not_the_run()
    {
        string component = RunStyleOverrides.DynamicsCacheKeyComponent(Run(StyleDynamics.Fast))!;
        DeterministicStepRevisions.For(StepType.VideoCompile, "{\"dynamics\":{\"enabled\":false}}", component).Should().Be(Plain);
        DeterministicStepRevisions.For(StepType.VideoCompile, "{\"dynamics\":{\"enabled\":true}}", component).Should().Be(Plain + "+dynamics-1+dynamics-2+beat-grid-read-1");
    }

    // QA-DYN-61: the run's dynamics key a VideoCompile only; a pace-only change must not re-run a paid or analysis step.
    private static StepCacheKeyInputs KeyFor(StepType type, WorkflowExecution run, string? runDynamics) =>
        CompileKey(null, run) with { StepType = type, RunDynamics = runDynamics };

    [Theory]
    [InlineData(StepType.VideoAnalyze)]
    [InlineData(StepType.VideoGenerate)]
    [InlineData(StepType.Voiceover)]
    public void A_pace_only_change_leaves_every_non_compile_key_identical(StepType type)
    {
        var builder = new StepCacheKeyBuilder();
        WorkflowExecution calm = Run(null, "calm"), fast = Run(null, "fast");
        // the executor sets RunDynamics for a compile only
        builder.Build(KeyFor(type, calm, null)).Should().Be(builder.Build(KeyFor(type, fast, null)));
        RunStyleOverrides.CacheKeyComponent(calm, []).Should().Be(RunStyleOverrides.CacheKeyComponent(fast, []));
        builder.Build(KeyFor(StepType.VideoCompile, calm, RunStyleOverrides.DynamicsCacheKeyComponent(calm)))
            .Should().NotBe(builder.Build(KeyFor(StepType.VideoCompile, fast, RunStyleOverrides.DynamicsCacheKeyComponent(fast))));
    }

    [Fact]
    public void The_dynamics_component_is_the_value_itself_and_null_without_dynamics()
    {
        RunStyleOverrides.DynamicsCacheKeyComponent(Run(null)).Should().BeNull();
        RunStyleOverrides.DynamicsCacheKeyComponent(Run(null, "fast")).Should().Be("Fast");
        // a style Notes text that mentions the marker never turns dynamics on
        string notes = StyleJson.Serialize(new RunStyleSnapshot("s", null, null, new StyleConfig(Notes: "use |dynamics=Fast here")));
        RunStyleOverrides.DynamicsCacheKeyComponent(new WorkflowExecution { StyleConfigJson = notes }).Should().BeNull();
        DeterministicStepRevisions.For(StepType.VideoCompile, "{\"version\":1}", null).Should().Be(Plain);
    }

    // QA-DYN-62: cutaway_too_short skips a short window; the picture of a generated-clips film changes without Dynamics.
    [Fact]
    public void Generated_clips_add_the_cutaway_min_length_suffix_only_with_the_flag()
    {
        Compile("{\"enableGeneratedClips\":true}").Should().Contain("+cutaway-min-length-1");
        Compile("{\"enableGeneratedClips\":false}").Should().NotContain("cutaway-min-length-1");
        Compile("{\"version\":1}").Should().Be(Plain);
    }
}
