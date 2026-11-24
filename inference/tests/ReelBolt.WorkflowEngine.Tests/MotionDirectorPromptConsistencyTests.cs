using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// MotionDirector: the in-process fallback prompt and the seeded built-in row stay verbatim-identical, both name every
/// word the compile accepts (the primitives' own enums), and the grant stays read-only.
/// </summary>
public class MotionDirectorPromptConsistencyTests
{
    private static string Fallback() =>
        (string)typeof(MotionDirectorAgent).GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Seeded()
    {
        var agents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        agents.Should().ContainKey(AgentType.MotionDirector);
        return agents[AgentType.MotionDirector].SystemPrompt;
    }

    [Fact]
    public void Fallback_prompt_matches_the_seeded_prompt_verbatim() => Seeded().Should().Be(Fallback());

    [Fact]
    public void Prompt_names_every_word_the_compile_accepts_and_never_offers_a_cut_action()
    {
        string prompt = Fallback();
        IEnumerable<string> words = Enum.GetNames<EventMoveKind>()
            .Concat(Enum.GetNames<SpeedRampKind>())
            .Concat(Enum.GetNames<EventMoveIntensity>())
            .Concat(Enum.GetNames<EventMoveHold>())
            .Concat(Enum.GetNames<FlashColour>())
            .Concat(["Flash", "Graphic", "LowerThird", "Title", "Callout", "Tag"]);
        foreach (string word in words.Distinct())
            prompt.Should().Contain($"\"{word}\"", $"the prompt must name the word {word}");
        prompt.Should().NotContain("CutHere", "cuts are the story editor's job");
        prompt.Should().Contain("never").And.Contain("EMPTY `cues` list");
    }

    [Fact]
    public void Both_story_editor_prompts_allow_a_keep_on_an_event_only_when_the_run_cuts_on_events()
    {
        var agents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string fallback = (string)typeof(VideoStoryEditorAgent).GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        foreach (string prompt in new[] { fallback, agents[AgentType.VideoStoryEditor].SystemPrompt })
            prompt.Should().Contain("Only when the run says it cuts on events may a Keep span begin or end on an offered \"e{n}\" id");
        agents[AgentType.VideoStoryEditor].SystemPrompt.Should().Be(fallback);
    }

    [Fact]
    public void Motion_director_is_read_only_project_context_a_look_at_a_moment_and_fail_workflow()
    {
        // It writes nothing and renders nothing. LookAtMoment is its one tool that spends (capped, see MomentLookLimits).
        ToolGroupCatalog.GroupsFor(AgentType.MotionDirector).Should().BeEquivalentTo(
            [ToolGroup.ProjectRead, ToolGroup.MomentLook, ToolGroup.WorkflowControl]);
    }
}
