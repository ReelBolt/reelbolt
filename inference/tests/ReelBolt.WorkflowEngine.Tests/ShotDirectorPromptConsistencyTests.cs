using System.Collections.Generic;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="ShotDirectorAgent"/>'s in-process fallback system prompt and the built-in
/// <c>AgentDefinition</c> row <see cref="DatabaseSeeder"/> seeds for
/// <see cref="AgentType.ShotDirector"/> must stay verbatim-identical — the fallback exists only
/// for the (should-never-happen) case where the seeded config-driven prompt is absent, and a
/// silent drift between the two would mean the fallback describes a different contract than what
/// agents actually run with day to day. That matters more here than anywhere else in the video
/// pipeline: <c>ShotPlan.Prompt</c> is the one value of this agent's output that leaves the
/// building, so a stale fallback could describe an anchor/keyframe/word contract the deterministic
/// generation step no longer honours while the model happily emits against it.
///
/// <para>
/// Reflection is used since both are intentionally private — neither should widen its access just
/// to satisfy this test. Mirrors <see cref="VideoStoryEditorPromptConsistencyTests"/>, which
/// covers the sibling agents; that file is owned by the media department and is deliberately not
/// extended from here.
/// </para>
/// </summary>
public class ShotDirectorPromptConsistencyTests
{
    [Fact]
    public void Fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        FieldInfo? promptField = typeof(ShotDirectorAgent)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static);
        promptField.Should().NotBeNull("ShotDirectorAgent must expose a private DefaultPrompt constant");
        string fallbackPrompt = (string)promptField!.GetValue(null)!;

        FieldInfo? builtInAgentsField = typeof(DatabaseSeeder)
            .GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static);
        builtInAgentsField.Should().NotBeNull("DatabaseSeeder must expose a private BuiltInAgents seed table");

        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            builtInAgentsField!.GetValue(null)!;

        builtInAgents.Should().ContainKey(AgentType.ShotDirector);
        string seededPrompt = builtInAgents[AgentType.ShotDirector].SystemPrompt;

        seededPrompt.Should().Be(fallbackPrompt,
            "the fallback prompt in ShotDirectorAgent.cs and the seeded built-in agent row in " +
            "DatabaseSeeder.cs must be kept verbatim-identical");
    }

    [Fact]
    public void Fallback_prompt_states_every_enum_vocabulary_verbatim()
    {
        // The whole point of this agent's output is that every non-prose value is a WORD the
        // deterministic VideoGenerate step can resolve. If a prompt edit ever drops a word from
        // one of these vocabularies, the model can no longer be told what is legal to emit —
        // ShotPlanParameterMapper.CameraPhraseFor degrades an unknown camera word to "no clause",
        // which is silent, and SnapDurationSeconds throws on an unknown duration word, which is
        // not. Pin the vocabularies the prompt promises.
        string prompt = (string)typeof(ShotDirectorAgent)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        foreach (string word in new[]
                 {
                     "Cutaway", "SeamBridge", "Extend", "ColdOpen", "EndCard", "ScreenContent",
                     "Short", "Medium", "Long",
                     "Static", "PushIn", "PullOut", "PanLeft", "PanRight",
                     "TiltUp", "TiltDown", "ZoomIn", "ZoomOut", "Handheld", "Orbit"
                 })
        {
            prompt.Should().Contain(word,
                $"the ShotDirector prompt must name the enum word \"{word}\" — a value the " +
                "deterministic generation step is asked to resolve verbatim");
        }
    }
}
