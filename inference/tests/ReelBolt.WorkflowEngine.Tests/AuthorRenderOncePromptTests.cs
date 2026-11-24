using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-206: "render once" is enforced by the render tool now, so both copies of the Author prompt (the fallback and
/// the seeded row) drop the old double-check prose and say the tool refuses a second render — in the same words.
/// </summary>
public class AuthorRenderOncePromptTests
{
    [Fact]
    public void Both_copies_point_at_the_tool_rule_instead_of_the_old_prose()
    {
        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string fallback = (string)typeof(AuthorAgentImpl)
            .GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        foreach (string raw in new[] { fallback, builtInAgents[AgentType.AuthorAgent].SystemPrompt })
        {
            string prompt = Regex.Replace(raw, @"\s+", " ");
            prompt.Should().Contain("the render tool refuses a second render of the same composition in this step.");
            prompt.Should().NotContain("never render again just to double-check");
        }
    }
}
