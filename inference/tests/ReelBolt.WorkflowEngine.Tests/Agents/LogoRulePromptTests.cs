using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using ReelBolt.WorkflowEngine.Agents.Translation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The logo rule in both copies (fallback and seeded row) of every prompt that places or stages logos, and
/// <c>logo_low_contrast</c> wherever the prompts list what <c>CheckLayout</c> reports.
/// </summary>
public class LogoRulePromptTests
{
    public static IEnumerable<object[]> Agents() =>
    [
        [AgentType.RemotionComponentTranslator, typeof(RemotionComponentTranslatorAgent)],
        [AgentType.AuthorAgent, typeof(AuthorAgentImpl)],
        [AgentType.CompositionReviser, typeof(CompositionReviserAgent)],
    ];

    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    [Theory]
    [MemberData(nameof(Agents))]
    public void Both_copies_carry_the_same_logo_rule(AgentType agent, System.Type type)
    {
        var table = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string fallback = (string)type.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        table[agent].SystemPrompt.Should().Be(fallback);
        string prompt = Flat(fallback);
        prompt.Should().Contain("## Logos");
        prompt.Should().Contain("never from dependency, vendored, framework or node_modules images");
        prompt.Should().Contain("The product logo is the default.");
        prompt.Should().Contain("only when the brief explicitly asks for it, as a secondary \"by <company>\" element, never in place of the product logo");
        prompt.Should().Contain("Choose the variant whose tone contrasts with the actual background");
        prompt.Should().Contain("readableOn from ReadDesignSystem assets or from the brand block StageProjectFile returns");
        prompt.Should().Contain("never from a file name's light or dark");
        prompt.Should().Contain("Never invent a logo wordmark in type.");
        prompt.Should().Contain("fix every logo_low_contrast issue it reports");
    }

    [Theory]
    [InlineData(typeof(AuthorAgentImpl))]
    [InlineData(typeof(CompositionReviserAgent))]
    [InlineData(typeof(MotionGraphicsPlannerAgent))]
    public void CheckLayout_kind_lists_name_logo_low_contrast(System.Type type)
    {
        string fallback = (string)type.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Flat(fallback).Should().Contain("too-small text and every logo_low_contrast");
    }
}
