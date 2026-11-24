using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Styles;

/// <summary>The StyleDynamics axis, the dynamic-edit preset and the brief's pace (docs/design/dynamic-edit.md).</summary>
public class DynamicsStyleTests
{
    private static VideoCompileStepConfig Compile(VideoCompileMode mode = VideoCompileMode.Reencode) =>
        new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1, Mode: mode);

    private static WorkflowExecution Execution(StyleConfig? style = null, string? pace = null) => new()
    {
        Id = Guid.NewGuid(),
        StyleConfigJson = style is null ? null : StyleJson.Serialize(new RunStyleSnapshot("s", null, null, style)),
        RunBriefJson = pace is null ? null : RunBriefJson.Serialize(new StoredRunBrief(Pace: pace))
    };

    [Fact]
    public async Task The_dynamic_edit_preset_resolves_through_the_run_style_resolver()
    {
        using InferenceApiDbContext db = new(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        RunChoicesResolution result = await new RunStyleResolver(db).ResolveAsync(
            Guid.NewGuid(), Guid.NewGuid(), null, "dynamic-edit", null, null, null, default);

        result.Error.Should().BeNull();
        result.Choices!.Style!.PresetKey.Should().Be("dynamic-edit");
        result.Choices.Style.Config.Dynamics.Should().Be(StyleDynamics.Fast, "the proof film was Fast");
        result.Choices.Style.Config.Pacing.Should().Be(StylePacing.Fast);
        result.Choices.Style.Config.Editing.Should().Be(StyleEditing.Mixed);
        result.Choices.Style.Config.Graphics.Should().Be(StyleGraphicsDensity.Moderate);
        result.Choices.Style.Config.Music.Should().Be(StyleMusicEnergy.Medium);
        result.Choices.Style.Config.Tone.Should().Be(StyleTone.Energetic);
        StylePresetCatalog.Find("dynamic-edit")!.SuggestedFormat.Should().Be(RunOutputFormat.Vertical);
        StylePresetCatalog.All.Last().Key.Should().Be("dynamic-edit", "preset keys are append-only");
    }

    [Fact]
    public void The_axis_round_trips_by_name_and_is_omitted_when_null()
    {
        string json = StyleJson.Serialize(new StyleConfig(Dynamics: StyleDynamics.Fast));

        json.Should().Be("{\"dynamics\":\"Fast\"}");
        StyleJson.TryParseConfig(json)!.Dynamics.Should().Be(StyleDynamics.Fast);
        StyleJson.Serialize(new StyleConfig(Pacing: StylePacing.Fast)).Should().Be("{\"pacing\":\"Fast\"}");
        new StyleConfig(Dynamics: StyleDynamics.Calm).IsEmpty.Should().BeFalse();
        new StyleConfig().IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void An_unstyled_run_leaves_the_compile_untouched()
    {
        VideoCompileStepConfig config = Compile();

        RunStyleOverrides.ApplyToCompile(config, Execution(), out var applied).Should().BeSameAs(config);
        applied.Should().BeEmpty();
    }

    [Fact]
    public void A_style_with_dynamics_enables_dynamics_and_the_editor_transition_policy()
    {
        VideoCompileStepConfig result = RunStyleOverrides.ApplyToCompile(
            Compile(), Execution(new StyleConfig(Dynamics: StyleDynamics.Fast)), out var applied);

        result.Dynamics.Should().Be(new VideoDynamicsConfig(Enabled: true, Pace: DynamicPace.Fast, KeepSceneBoundaries: true));
        result.TransitionPolicy.Should().Be(VideoTransitionPolicy.Editor);
        applied.Should().Contain("dynamics=Fast");
    }

    [Fact]
    public void The_brief_pace_overrides_the_style_dynamics()
    {
        VideoCompileStepConfig result = RunStyleOverrides.ApplyToCompile(
            Compile(), Execution(new StyleConfig(Dynamics: StyleDynamics.Fast), pace: "calm"), out _);

        result.Dynamics!.Pace.Should().Be(DynamicPace.Calm);
        RunStyleOverrides.EffectiveDynamics(Execution(new StyleConfig(Dynamics: StyleDynamics.Fast), "calm")).Should().Be(StyleDynamics.Calm);
    }

    [Fact]
    public void A_brief_pace_alone_enables_dynamics_without_a_style()
    {
        RunStyleOverrides.ApplyToCompile(Compile(), Execution(pace: "medium"), out _).Dynamics!.Pace.Should().Be(DynamicPace.Medium);
    }

    [Fact]
    public void A_compile_that_set_dynamics_itself_or_cannot_re_encode_is_left_alone()
    {
        VideoCompileStepConfig own = Compile() with { Dynamics = new VideoDynamicsConfig(Pace: DynamicPace.Calm) };
        RunStyleOverrides.ApplyToCompile(own, Execution(new StyleConfig(Dynamics: StyleDynamics.Fast)), out _).Dynamics!.Pace.Should().Be(DynamicPace.Calm);

        VideoCompileStepConfig copy = Compile(VideoCompileMode.StreamCopy);
        RunStyleOverrides.ApplyToCompile(copy, Execution(new StyleConfig(Dynamics: StyleDynamics.Fast)), out _).Dynamics.Should().BeNull();
    }

    [Fact]
    public void The_brief_pace_reaches_the_editor_nudge_and_replaces_fewer_longer_runs()
    {
        WorkflowExecution execution = Execution(new StyleConfig(Pacing: StylePacing.Slow), "fast");
        RunStyleState state = RunStyleState.From(execution, StyleNudgeService.Instance)!;

        string editor = state.InstructionsFor(AgentType.VideoStoryEditor)!;
        editor.Should().Contain("Dynamics: fast.").And.Contain("BeatAlternate");
        editor.Should().NotContain("fewer, longer spans");
        editor.Should().Contain("overrides \"prefer fewer, longer runs over many short ones\"")
            .And.Contain("SplitOnBeat").And.Contain("never a number, a time or a duration");
        // A run without dynamics never sees the dynamic edit's wording.
        StyleNudgeService.Instance.BuildInstructions(AgentType.VideoStoryEditor,
                new RunStyleSnapshot(null, null, null, new StyleConfig(Pacing: StylePacing.Fast)), null)
            .Should().NotContain("dynamics").And.NotContain("BeatAlternate");
        StyleNudgeService.Instance.BuildInstructions(AgentType.VideoStoryEditor, new RunStyleSnapshot(null, null, null, new StyleConfig(Pacing: StylePacing.Slow)), null)
            .Should().Contain("fewer, longer spans");
    }

    [Fact]
    public void Dynamics_says_a_line_to_graphics_sound_and_music_agents()
    {
        var style = new RunStyleSnapshot(null, null, null, new StyleConfig(Dynamics: StyleDynamics.Medium));
        foreach (AgentType type in new[] { AgentType.MotionGraphicsPlanner, AgentType.SoundDesigner, AgentType.MusicSupervisor })
            StyleNudgeService.Instance.BuildInstructions(type, style, null).Should().Contain("Dynamics: medium.");
    }

    [Fact]
    public void The_pace_choice_validates_and_normalizes()
    {
        RunBriefValidator.NormalizePace(" Fast ").Should().Be("fast");
        RunBriefValidator.NormalizePace("warp").Should().BeNull();
        RunBriefValidator.NormalizePace(null).Should().BeNull();
        JsonSerializer.Serialize(new RunBrief(), RunBriefJson.Options).Should().Be("{}");
        new StoredRunBrief(Pace: "fast").IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void A_brief_with_no_pace_keeps_its_cache_component_and_a_pace_changes_it()
    {
        RunBriefState plain = new(new StoredRunBrief(LengthSec: 20));
        RunBriefState paced = new(new StoredRunBrief(LengthSec: 20, Pace: "fast"));

        plain.CacheKeyComponent(StepType.VideoCompile, null).Should().Be("lengthSec=2:20");
        paced.CacheKeyComponent(StepType.VideoCompile, null).Should().Be("lengthSec=2:20|pace=4:fast");
        paced.CacheKeyComponent(StepType.Agent, AgentType.VideoStoryEditor).Should().Contain("pace=4:fast");
        paced.CacheKeyComponent(StepType.Agent, AgentType.ColorGradeDirector).Should().BeNull();
    }

    [Fact]
    public void The_pace_input_shows_in_every_app_with_a_compile_and_keeps_its_slot()
    {
        AppStepFacts[] plain = [new(StepType.VideoAnalyze), new(StepType.VideoCompile)];
        AppStepFacts[] none = [new(StepType.VideoAnalyze), new(StepType.ThumbnailGenerate)];

        AppManifestDeriver.Derive(plain, false).Inputs!.Select(i => i.Field).Should().Contain("pace");
        AppManifestDeriver.Derive(none, false).Inputs!.Select(i => i.Field).Should().NotContain("pace");
        BriefFieldConsumers.FieldsReadBy(new AppStepFacts(StepType.VideoCompile)).Should().Contain("pace");

        // Over the shown cap the last other input goes, never the pace.
        AppStepFacts[] crowded = [new(StepType.VideoAnalyze), new(StepType.VideoCompile), new(StepType.Voiceover)];
        AppManifest derived = AppManifestDeriver.Derive(crowded, true);
        derived.Inputs!.Count.Should().BeLessThanOrEqualTo(AppManifestVocabulary.MaxInputs);
        derived.Inputs!.Select(i => i.Field).Should().Contain("pace");
    }

    [Fact]
    public void An_authors_own_input_list_still_gets_the_pace_when_a_compile_reads_it()
    {
        AppStepFacts[] steps = [new(StepType.VideoAnalyze), new(StepType.VideoCompile)];
        var own = new AppManifest(Inputs: [new AppManifestInput(AppManifestVocabulary.KindBrief, Field: "lengthSec", Label: "Length")]);

        AppManifestDeriver.Effective(own, steps, false, "App").Inputs!.Select(i => i.Field).Should().Contain("pace");
        AppManifestDeriver.Effective(own, [new AppStepFacts(StepType.ThumbnailGenerate)], false, "App").Inputs!
            .Select(i => i.Field).Should().NotContain("pace");

        var withPace = new AppManifest(Inputs: [new AppManifestInput(AppManifestVocabulary.KindBrief, Field: "pace", Label: "Speed")]);
        AppManifestDeriver.Effective(withPace, steps, false, "App").Inputs!.Count(i => i.Field == "pace").Should().Be(1);
    }
}
