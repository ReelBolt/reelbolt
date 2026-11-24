using System;
using System.Collections.Generic;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Analysis;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The five analyzer agents' in-code fallback prompts and the built-in rows DatabaseSeeder seeds
/// (the row is what actually runs, and it mirrors the seeder on every startup) must stay
/// verbatim-identical, and must cover .NET/Blazor projects and tolerate a missing single file.
/// </summary>
public class AnalyzerPromptConsistencyTests
{
    public static IEnumerable<object[]> Analyzers() =>
    [
        [AgentType.ComponentInventoryAnalyzer, typeof(ComponentInventoryAnalyzerAgent), ".razor"],
        [AgentType.RouteAndApiAnalyzer, typeof(RouteAndApiAnalyzerAgent), "@page"],
        [AgentType.CodeStructureAnalyzer, typeof(CodeStructureAnalyzerAgent), "_Imports.razor"],
        [AgentType.DependencyAnalyzer, typeof(DependencyAnalyzerAgent), "PackageReference"],
        [AgentType.StyleAndThemeExtractor, typeof(StyleAndThemeExtractorAgent), "MudTheme"],
    ];

    private static string Fallback(Type agent) =>
        (string)agent.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Theory]
    [MemberData(nameof(Analyzers))]
    public void Fallback_prompt_matches_the_seeded_prompt_verbatim(AgentType type, Type agent, string _)
    {
        var builtIn = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        builtIn[type].SystemPrompt.Should().Be(Fallback(agent));
    }

    [Theory]
    [MemberData(nameof(Analyzers))]
    public void Prompt_covers_blazor_and_does_not_treat_one_missing_file_as_fatal(AgentType _, Type agent, string marker)
    {
        string prompt = Fallback(agent);

        prompt.Should().Contain(marker);
        prompt.Should().Contain("exactly as returned by `ListProjectFiles`");
        prompt.Should().Contain("NOT an unrecoverable error");
        prompt.Should().Contain("no readable source files at all");
    }
}
