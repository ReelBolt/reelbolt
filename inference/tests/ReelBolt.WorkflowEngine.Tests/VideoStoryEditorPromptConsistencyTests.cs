using System.Collections.Generic;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using ReelBolt.WorkflowEngine.Agents.Quality;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="VideoStoryEditorAgent"/>'s in-process fallback system prompt and the built-in
/// <c>AgentDefinition</c> row <see cref="DatabaseSeeder"/> seeds for
/// <see cref="AgentType.VideoStoryEditor"/> must stay verbatim-identical — the fallback exists
/// only for the (should-never-happen) case where the seeded config-driven prompt is absent, and a
/// silent drift between the two would mean that fallback describes a different contract (e.g. a
/// stale no-timestamp rule or missing Phase 1 visual/audio field description) than what agents
/// actually run with day to day. Reflection is used since both are intentionally private —
/// neither should widen its access just to satisfy this test.
///
/// <see cref="MotionGraphicsPlannerAgent"/> (Phase 3) gets the exact same verbatim-consistency
/// check, mirroring the same risk for its own no-timestamp/no-coordinate contract.
/// <see cref="VideoReviewAgentImpl"/> (the video-editing templates' ReviewLoop agent) gets the
/// same check for the same reason — a drift here would mean the fallback describes stale
/// deterministic-evidence field names (e.g. a renamed "sentenceCheck" key) the agent never
/// actually receives day to day.
/// </summary>
public class VideoStoryEditorPromptConsistencyTests
{
    [Fact]
    public void Fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(VideoStoryEditorAgent), AgentType.VideoStoryEditor);
    }

    [Fact]
    public void MotionGraphicsPlanner_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(MotionGraphicsPlannerAgent), AgentType.MotionGraphicsPlanner);
    }

    [Fact]
    public void VideoReviewAgent_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(VideoReviewAgentImpl), AgentType.VideoReviewAgent);
    }

    [Fact]
    public void MusicSupervisor_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(MusicSupervisorAgent), AgentType.MusicSupervisor);
    }

    [Fact]
    public void VideoEditDirector_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(VideoEditDirectorAgent), AgentType.VideoEditDirector);
    }

    [Fact]
    public void MotionGraphicsDirector_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(MotionGraphicsDirectorAgent), AgentType.MotionGraphicsDirector);
    }

    [Fact]
    public void Colorist_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(ColoristAgent), AgentType.Colorist);
    }

    [Fact]
    public void ColorGradeDirector_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(ColorGradeDirectorAgent), AgentType.ColorGradeDirector);
    }

    [Fact]
    public void SoundDesigner_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(SoundDesignerAgent), AgentType.SoundDesigner);
    }

    [Fact]
    public void DynamicsPlanner_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(DynamicsPlannerAgent), AgentType.DynamicsPlanner);
    }

    [Fact]
    public void NarrationWriter_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        // Phase 2 of the voiceover work: the narration plan replaces phase 1's hand-written
        // "Inline" script with a model-authored one, so this pair guards the same contract the
        // eight above do — an anchor-id-only, never-a-number narration plan. A drift here would
        // mean the fallback describes different hard constraints than the seeded row agents
        // actually run with, which is exactly the failure this file exists to catch.
        AssertFallbackMatchesSeeded(typeof(NarrationWriterAgent), AgentType.NarrationWriter);
    }

    [Fact]
    public void PickupPlanner_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(PickupPlannerAgent), AgentType.PickupPlanner);
    }

    [Fact]
    public void NarrationTranslator_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(NarrationTranslatorAgent), AgentType.NarrationTranslator);
    }

    [Fact]
    public void Author_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        AssertFallbackMatchesSeeded(typeof(AuthorAgentImpl), AgentType.AuthorAgent);
    }

    [Fact]
    public void Review_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        // The reviewer's score is the loop's exit gate, so its rubric (anchors, the audio-silence
        // rule, the frame-based visualAccuracy wording) is pinned like the Author's; a drifted seed
        // would score the film against criteria the fallback never states.
        AssertFallbackMatchesSeeded(typeof(ReviewAgentImpl), AgentType.ReviewAgent);
    }

    [Fact]
    public void Director_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        // Phase 3 of the voiceover work (audio-first timing) made the Director's prompt a contract
        // with the Voiceover step's measured durations, so the two copies are now held byte-identical
        // like every other built-in whose prompt carries a hard rule.
        AssertFallbackMatchesSeeded(typeof(DirectorAgentImpl), AgentType.DirectorAgent);
    }

    [Fact]
    public void Scriptwriter_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        // The Scriptwriter's prompt now carries hard on-screen-copy rules (no markdown, no invented
        // personas or metrics, format/length read from the brief), so it is pinned like the others.
        AssertFallbackMatchesSeeded(typeof(ScriptwriterAgentImpl), AgentType.ScriptwriterAgent);
    }

    [Fact]
    public void RemotionComponentTranslator_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        // The translator registers the compositions, so its prompt carries the frame-size-from-the-
        // brief rule and the legible-type rule; pinned for the same reason.
        AssertFallbackMatchesSeeded(typeof(ReelBolt.WorkflowEngine.Agents.Translation.RemotionComponentTranslatorAgent), AgentType.RemotionComponentTranslator);
    }

    [Fact]
    public void VideoStoryEditor_prompt_does_not_promise_an_automatic_shortening_it_only_does_with_a_target()
    {
        // The compile trims span tails only when a target length is set (TargetDurationSec > 0),
        // and no template sets one. The editor must be told to pick parts of a long shot instead.
        string seededPrompt = SeededPrompt(AgentType.VideoStoryEditor);

        seededPrompt.Should().NotContain("the compile shortens the longest kept shots");
        seededPrompt.Should().Contain("keep only some parts of a long shot");
    }

    private static string SeededPrompt(AgentType builtInAgentType)
    {
        FieldInfo? builtInAgentsField = typeof(DatabaseSeeder)
            .GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static);
        builtInAgentsField.Should().NotBeNull("DatabaseSeeder must expose a private BuiltInAgents seed table");

        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            builtInAgentsField!.GetValue(null)!;

        builtInAgents.Should().ContainKey(builtInAgentType);
        return builtInAgents[builtInAgentType].SystemPrompt;
    }

    private static void AssertFallbackMatchesSeeded(System.Type agentType, AgentType builtInAgentType)
    {
        FieldInfo? promptField = agentType.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static);
        promptField.Should().NotBeNull($"{agentType.Name} must expose a private DefaultPrompt constant");
        string fallbackPrompt = (string)promptField!.GetValue(null)!;

        FieldInfo? builtInAgentsField = typeof(DatabaseSeeder)
            .GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static);
        builtInAgentsField.Should().NotBeNull("DatabaseSeeder must expose a private BuiltInAgents seed table");

        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            builtInAgentsField!.GetValue(null)!;

        builtInAgents.Should().ContainKey(builtInAgentType);
        string seededPrompt = builtInAgents[builtInAgentType].SystemPrompt;

        seededPrompt.Should().Be(fallbackPrompt,
            $"the fallback prompt in {agentType.Name}.cs and the seeded built-in agent row in " +
            "DatabaseSeeder.cs must be kept verbatim-identical");
    }
}
