using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Production;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The block list embedded in the scene-writing agents' prompts is generated from
/// inference/blocks/catalog.json by blocks/scripts/build-catalog.mjs (between the [[BLOCKS:BEGIN]] /
/// [[BLOCKS:END]] marker lines). This pins both prompt copies to a regeneration of it, so the list cannot drift.
/// </summary>
public class BlockPromptListTests
{
    private static readonly string[] Categories =
        ["transitions", "titles", "captions", "charts", "maps", "effects", "backgrounds", "devices", "shapes"];

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "sandbox", "template", "package-catalog.json")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    // Mirrors blurb() in build-catalog.mjs.
    private static string Blurb(string description)
    {
        string t = Regex.Replace(description, @"^Use (for|to) ", "");
        t = Regex.Split(t, @"\.(\s|$)")[0].Trim();
        if (t.Length > 110)
        {
            string head = t[..110];
            int semi = head.LastIndexOf("; ", StringComparison.Ordinal);
            int clause = semi >= 40 ? semi : head.LastIndexOf(", ", StringComparison.Ordinal);
            t = clause >= 40 ? head[..clause] : Regex.Replace(head, @"\s+\S*$", "");
            t = Regex.Replace(t, @"(\s+(and|or|to|for|the|a|an|with|of|in|on|by|as|at))+$", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"[\s,;:(-]+$", "");
        }
        return t;
    }

    private static (List<string> Lines, List<string> Ids) Expected()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "inference", "blocks", "catalog.json")));
        var blocks = doc.RootElement.GetProperty("blocks").EnumerateArray()
            .Select(b => (Id: b.GetProperty("id").GetString()!, Cat: b.GetProperty("category").GetString()!, Desc: b.GetProperty("description").GetString()!))
            .ToList();
        var lines = new List<string> { "Blocks you can add with AddBlock (id: what it does):" };
        foreach (string cat in Categories)
        {
            var inCat = blocks.Where(b => b.Cat == cat).OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
            if (inCat.Count == 0) continue;
            lines.Add(cat + ":");
            lines.AddRange(inCat.Select(b => $"- {b.Id}: {Blurb(b.Desc)}"));
        }
        return (lines, blocks.Select(b => b.Id).ToList());
    }

    public static IEnumerable<object[]> Agents() =>
    [
        [typeof(AuthorAgentImpl), AgentType.AuthorAgent],
        [typeof(CompositionReviserAgent), AgentType.CompositionReviser],
        [typeof(MotionGraphicsPlannerAgent), AgentType.MotionGraphicsPlanner],
        [typeof(MotionGraphicsDirectorAgent), AgentType.MotionGraphicsDirector],
        [typeof(GraphicDesignerAgent), AgentType.GraphicDesigner],
        [typeof(OverlayPlannerAgent), AgentType.OverlayPlanner],
    ];

    [Theory]
    [MemberData(nameof(Agents))]
    public void Both_prompt_copies_carry_the_generated_block_list_exactly_once(Type agent, AgentType type)
    {
        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string fallback = (string)agent.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var (expected, ids) = Expected();

        var regions = new List<string>();
        foreach (string prompt in new[] { fallback, builtInAgents[type].SystemPrompt })
        {
            Regex.Matches(prompt, @"\[\[BLOCKS:BEGIN\]\]").Count.Should().Be(1);
            Regex.Matches(prompt, @"\[\[BLOCKS:END\]\]").Count.Should().Be(1);
            Match m = Regex.Match(prompt, @"\[\[BLOCKS:BEGIN\]\]\r?\n(?<body>[\s\S]*?)\r?\n\s*\[\[BLOCKS:END\]\]");
            m.Success.Should().BeTrue();
            string[] lines = m.Groups["body"].Value.Split('\n').Select(l => l.Trim()).ToArray();
            lines.Should().Equal(expected, "the prompt list must match build-catalog.mjs; run `node inference/blocks/scripts/build-catalog.mjs`");
            string region = string.Join("\n", lines);
            region.Length.Should().BeLessThan(7000, "a growing catalogue must be noticed here");
            regions.Add(region);

            foreach (string id in ids)
                lines.Count(l => l.StartsWith("- " + id + ": ", StringComparison.Ordinal)).Should().Be(1, id);
            lines.Should().Contain(l => l.StartsWith("- gsap-kinetic-title: "));
        }
        regions[0].Should().Be(regions[1]);
    }
}
