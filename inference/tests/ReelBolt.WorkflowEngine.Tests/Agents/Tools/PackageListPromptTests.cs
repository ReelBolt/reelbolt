using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// Pins every hand-kept copy of the template package list (agent prompts, seeder copies and the remotion-*
/// skill prefaces) to sandbox/template/package-catalog.json.
/// </summary>
public class PackageListPromptTests
{
    private static readonly Regex Run = new(@"(?:`[@\w/.\-]+`[,.]?\s*){10,}", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "sandbox", "template", "package-catalog.json")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string[] CatalogNames()
    {
        using JsonDocument cat = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "sandbox", "template", "package-catalog.json")));
        return cat.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static List<string[]> Lists(string text) =>
        Run.Matches(text)
            .Select(m => Regex.Matches(m.Value, @"`([^`]+)`").Select(x => x.Groups[1].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray())
            .ToList();

    public static IEnumerable<object[]> PromptFiles() =>
    [
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Production/AuthorAgent.cs", 2],
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Production/CompositionReviserAgent.cs", 1],
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Production/MotionGraphicsPlannerAgent.cs", 1],
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Production/MotionGraphicsDirectorAgent.cs", 1],
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Production/GraphicDesignerAgent.cs", 1],
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Translation/RemotionComponentTranslatorAgent.cs", 1],
        ["inference/src/ReelBolt.Inference.Api/Data/DatabaseSeeder.cs", 7],
        ["inference/skills/remotion-create/REELBOLT-PREFACE.md", 1],
        ["inference/skills/remotion-markup/REELBOLT-PREFACE.md", 1],
        ["inference/skills/remotion-render/REELBOLT-PREFACE.md", 1],
        ["inference/skills/remotion-captions/REELBOLT-PREFACE.md", 1],
        ["inference/skills/remotion-multimedia/REELBOLT-PREFACE.md", 1],
    ];

    [Theory]
    [MemberData(nameof(PromptFiles))]
    public void PackageList_EqualsCatalog(string relativePath, int expectedCopies)
    {
        string[] expected = CatalogNames();
        List<string[]> lists = Lists(File.ReadAllText(Path.Combine(RepoRoot(), relativePath)));

        Assert.Equal(expectedCopies, lists.Count);
        foreach (string[] list in lists)
        {
            Assert.Empty(expected.Except(list));
            Assert.Empty(list.Except(expected));
        }
    }

    private const string GuideSentence =
        "For animated GIFs, Lottie, scene transitions and gsap, read the matching guide first (UseSkill remotion-markup: gifs.md, lottie.md, transitions.md; the gsap rules are in the skill's ReelBolt preface).";

    public static IEnumerable<object[]> GuideFiles() =>
    [
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Production/AuthorAgent.cs", 2],
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Production/CompositionReviserAgent.cs", 1],
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Production/MotionGraphicsPlannerAgent.cs", 1],
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Production/MotionGraphicsDirectorAgent.cs", 1],
        ["inference/src/ReelBolt.WorkflowEngine/Agents/Production/GraphicDesignerAgent.cs", 1],
        ["inference/src/ReelBolt.Inference.Api/Data/DatabaseSeeder.cs", 6],
        ["inference/skills/remotion-create/REELBOLT-PREFACE.md", 1],
        ["inference/skills/remotion-markup/REELBOLT-PREFACE.md", 1],
        ["inference/skills/remotion-render/REELBOLT-PREFACE.md", 1],
        ["inference/skills/remotion-captions/REELBOLT-PREFACE.md", 1],
        ["inference/skills/remotion-multimedia/REELBOLT-PREFACE.md", 1],
    ];

    [Theory]
    [MemberData(nameof(GuideFiles))]
    public void GuideSentence_IsPinned(string relativePath, int expectedCopies)
    {
        string text = Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot(), relativePath)), @"\s+", " ");
        Assert.Equal(expectedCopies, Regex.Matches(text, Regex.Escape(GuideSentence)).Count);
    }
}
