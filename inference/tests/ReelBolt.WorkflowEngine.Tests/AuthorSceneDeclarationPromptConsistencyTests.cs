using System.Collections.Generic;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="AuthorAgentImpl"/>'s in-process fallback system prompt and the built-in
/// <c>AgentDefinition</c> row <see cref="DatabaseSeeder"/> seeds for <see cref="AgentType.AuthorAgent"/>
/// must stay verbatim-identical, for the reason every other prompt-consistency test in this suite
/// gives: the fallback exists only for the case where the seeded row is absent, and a silent drift
/// would mean the fallback describes a different contract than the one agents run with.
///
/// <para>
/// The scene-declaration contract is pinned here too. The render tool reads the composition's own
/// <c>&lt;Sequence&gt;</c> list and lets it decide where each scene STARTS, reporting any declared
/// scene that does not match it as <c>sceneWarnings</c> — so a prompt that stopped telling the author
/// to match its frames to its own components, or to act on those warnings, would leave the model
/// declaring frames nothing checks.
/// </para>
/// </summary>
public class AuthorSceneDeclarationPromptConsistencyTests
{
    private static string FallbackPrompt()
    {
        FieldInfo? promptField = typeof(AuthorAgentImpl)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static);
        promptField.Should().NotBeNull("AuthorAgentImpl must expose a private DefaultPrompt constant");
        return (string)promptField!.GetValue(null)!;
    }

    private static string SeededPrompt()
    {
        FieldInfo? builtInAgentsField = typeof(DatabaseSeeder)
            .GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static);
        builtInAgentsField.Should().NotBeNull("DatabaseSeeder must expose a private BuiltInAgents seed table");

        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            builtInAgentsField!.GetValue(null)!;

        builtInAgents.Should().ContainKey(AgentType.AuthorAgent);
        return builtInAgents[AgentType.AuthorAgent].SystemPrompt;
    }

    [Fact]
    public void Fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        SeededPrompt().Should().Be(FallbackPrompt(),
            "the fallback prompt in AuthorAgent.cs and the seeded built-in agent row in " +
            "DatabaseSeeder.cs must be kept verbatim-identical");
    }

    [Fact]
    public void Both_copies_state_the_scene_declaration_contract()
    {
        foreach ((string label, string prompt) in new[]
                 {
                     ("AuthorAgent.DefaultPrompt", FallbackPrompt()),
                     ("DatabaseSeeder.BuiltInAgents[AuthorAgent]", SeededPrompt())
                 })
        {
            prompt.Should().Contain("sceneWarnings",
                because: $"{label} must tell the author that the render reports a scene its own <Sequence>s do not place");
            prompt.Should().Contain("<Sequence>",
                because: $"{label} must say that the composition's own sequences decide where a scene starts");
        }
    }
}