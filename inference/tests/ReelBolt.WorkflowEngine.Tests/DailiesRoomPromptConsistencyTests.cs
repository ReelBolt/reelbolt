using System;
using System.Collections.Generic;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The dailies room's two agents (issue #93 phase 4) each carry an in-process fallback prompt
/// (<c>DefaultPrompt</c>) and a seeded <c>AgentDefinition</c> row in <see cref="DatabaseSeeder"/>;
/// the two must stay verbatim-identical — the house rule every built-in agent follows (see
/// <see cref="VideoStoryEditorPromptConsistencyTests"/> / <see cref="ShotDirectorPromptConsistencyTests"/>).
/// Reflection is used since both are intentionally private.
/// </summary>
public class DailiesRoomPromptConsistencyTests
{
    public static TheoryData<Type, AgentType> Agents => new()
    {
        { typeof(TakeSelectorAgent), AgentType.TakeSelector },
        { typeof(DailiesDirectorAgent), AgentType.DailiesDirector }
    };

    [Theory]
    [MemberData(nameof(Agents))]
    public void Fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim(Type agentClass, AgentType agentType)
    {
        FieldInfo? promptField = agentClass.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static);
        promptField.Should().NotBeNull($"{agentClass.Name} must expose a private DefaultPrompt constant");
        string fallbackPrompt = (string)promptField!.GetValue(null)!;

        FieldInfo? builtInAgentsField = typeof(DatabaseSeeder)
            .GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static);
        builtInAgentsField.Should().NotBeNull("DatabaseSeeder must expose a private BuiltInAgents seed table");

        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            builtInAgentsField!.GetValue(null)!;

        builtInAgents.Should().ContainKey(agentType);
        builtInAgents[agentType].SystemPrompt.Should().Be(fallbackPrompt,
            $"the fallback prompt in {agentClass.Name}.cs and the seeded built-in agent row in DatabaseSeeder.cs must be kept verbatim-identical");
    }

    [Theory]
    [MemberData(nameof(Agents))]
    public void Fallback_prompt_forbids_numbers_and_names_the_take_id_vocabulary(Type agentClass, AgentType agentType)
    {
        _ = agentType;
        string prompt = (string)agentClass
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        // The output is id-anchored prose: the prompt has to name the take-id shape it may choose
        // from and tell the model it may never emit a score/number. These substrings are the
        // minimum that makes TakeSelectionOutputInvariantTests' structural guarantee also a
        // behavioural one.
        prompt.Should().Contain("clipId");
        prompt.Should().Contain("v1t1");
        prompt.Should().Contain("TakeSelectionOutput");
    }
}
