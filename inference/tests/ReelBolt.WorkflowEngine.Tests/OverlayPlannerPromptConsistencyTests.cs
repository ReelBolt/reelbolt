using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="OverlayPlannerAgent"/>'s in-process fallback system prompt and the built-in
/// <c>AgentDefinition</c> row <see cref="DatabaseSeeder"/> seeds for
/// <see cref="AgentType.OverlayPlanner"/> must stay byte-identical (design
/// <c>docs/design/picture-overlay.md</c> §7.2). The fallback exists only for the
/// (should-never-happen) case where the seeded config-driven prompt is absent; a silent drift
/// would mean the fallback describes different hard constraints (offered ids only, no timestamp
/// or coordinate, card vs inset rules, sound rules) than what agents actually run with.
///
/// Mirrors <see cref="VideoStoryEditorPromptConsistencyTests"/>: both prompts are intentionally
/// private, so reflection reads them without widening either one's access.
/// </summary>
public class OverlayPlannerPromptConsistencyTests
{
    [Fact]
    public void OverlayPlanner_fallback_prompt_matches_the_seeded_built_in_agent_prompt_verbatim()
    {
        FieldInfo? promptField = typeof(OverlayPlannerAgent)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static);
        promptField.Should().NotBeNull("OverlayPlannerAgent must expose a private DefaultPrompt constant");
        string fallbackPrompt = (string)promptField!.GetValue(null)!;

        string seededPrompt = SeededPrompt(AgentType.OverlayPlanner);

        seededPrompt.Should().Be(fallbackPrompt,
            "the fallback prompt in OverlayPlannerAgent.cs and the seeded built-in agent row in "
            + "DatabaseSeeder.cs must be kept verbatim-identical");
    }

    /// <summary>
    /// The prompt advertises the shared block catalogue, so it must carry the generated
    /// [[BLOCKS:BEGIN]]…[[BLOCKS:END]] list exactly once in EACH copy — the same contract
    /// <c>BlockPromptListTests</c> pins to the catalogue generator.
    /// </summary>
    [Fact]
    public void Both_prompt_copies_carry_the_block_list_exactly_once()
    {
        FieldInfo? promptField = typeof(OverlayPlannerAgent)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static);
        string fallbackPrompt = (string)promptField!.GetValue(null)!;
        string seededPrompt = SeededPrompt(AgentType.OverlayPlanner);

        foreach (string prompt in new[] { fallbackPrompt, seededPrompt })
        {
            Regex.Matches(prompt, @"\[\[BLOCKS:BEGIN\]\]").Count.Should().Be(1);
            Regex.Matches(prompt, @"\[\[BLOCKS:END\]\]").Count.Should().Be(1);
        }

        static string Region(string prompt) =>
            Regex.Match(prompt, @"\[\[BLOCKS:BEGIN\]\]\r?\n(?<body>[\s\S]*?)\r?\n\s*\[\[BLOCKS:END\]\]")
                .Groups["body"].Value;

        Region(fallbackPrompt).Should().Be(Region(seededPrompt));
    }

    private static string SeededPrompt(AgentType builtInAgentType)
    {
        FieldInfo? builtInAgentsField = typeof(DatabaseSeeder)
            .GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static);
        builtInAgentsField.Should().NotBeNull("DatabaseSeeder must expose a private BuiltInAgents seed table");

        var builtInAgents = (System.Collections.Generic.Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            builtInAgentsField!.GetValue(null)!;

        builtInAgents.Should().ContainKey(builtInAgentType);
        return builtInAgents[builtInAgentType].SystemPrompt;
    }
}
