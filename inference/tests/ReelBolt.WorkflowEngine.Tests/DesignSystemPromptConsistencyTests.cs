using System.Collections.Generic;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using ReelBolt.WorkflowEngine.Agents.Quality;
using ReelBolt.WorkflowEngine.Agents.Translation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-006: every agent that receives the design-system blocks is told, in both its fallback prompt and its
/// seeded row (kept verbatim-identical), to read the pinned design system and build from it.
/// </summary>
public class DesignSystemPromptConsistencyTests
{
    public static IEnumerable<object[]> Agents() =>
    [
        [AgentType.ScriptwriterAgent, typeof(ScriptwriterAgentImpl), "voice, tone and vocabulary"],
        [AgentType.DirectorAgent, typeof(DirectorAgentImpl), "identity"],
        [AgentType.AnimationStrategyAgent, typeof(AnimationStrategyAgentImpl), "motion section"],
        [AgentType.RemotionComponentTranslator, typeof(RemotionComponentTranslatorAgent), "colour tokens, type scale"],
        [AgentType.AuthorAgent, typeof(AuthorAgentImpl), "colour tokens, type scale"],
        [AgentType.ReviewAgent, typeof(ReviewAgentImpl), "lower score"],
        [AgentType.CompositionReviser, typeof(CompositionReviserAgent), "colour tokens, type scale"]
    ];

    private static string Fallback(System.Type type) =>
        (string)type.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Seeded(AgentType agent)
    {
        var table = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return table[agent].SystemPrompt;
    }

    private static string Flat(string text) =>
        string.Join(' ', text.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [MemberData(nameof(Agents))]
    public void Prompt_copies_match_and_state_the_design_system_contract(AgentType agent, System.Type type, string phrase)
    {
        string fallback = Fallback(type);
        // AnimationStrategy's seeded Tools section already differed from its fallback before T-006; only the
        // design-system paragraph is pinned for it.
        if (agent != AgentType.AnimationStrategyAgent)
            Seeded(agent).Should().Be(fallback);
        foreach (string prompt in new[] { fallback, Seeded(agent) })
            Flat(prompt).Should().Contain("[DESIGN SYSTEM]").And.Contain(phrase).And.Contain("brief wins");
        string flat = Flat(fallback);
        flat.Should().Contain("ReadDesignSystem").And.Contain("[DESIGN SYSTEM]").And.Contain(phrase);
        flat.Should().Contain("brief wins").And.Contain("no design system present");
    }
}
