using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Docs;

/// <summary>
/// Guards for docs/assistant-kb/ (docs/design/workflow-apps.md 7.3): the generated blocks of step-cards.md equal what
/// <see cref="StepCatalog"/> renders now, the recipe sections mirror <see cref="WorkflowTemplateCatalog"/>, and no
/// credit, dollar or price figure appears anywhere in the folder.
/// Regenerate the step-card blocks with: UPDATE_STEP_CARDS_DOC=1 dotnet test --filter StepCardsDocDriftTests
/// </summary>
public class StepCardsDocDriftTests
{
    private const string UpdateVariable = "UPDATE_STEP_CARDS_DOC";

    [Fact]
    public void Every_step_type_section_has_a_generated_block_equal_to_the_catalog_render()
    {
        string path = Path.Combine(AssistantKbPaths.KbDir(), "step-cards.md");
        string text = File.ReadAllText(path);
        bool update = Environment.GetEnvironmentVariable(UpdateVariable) == "1";
        List<string> problems = [];

        foreach (StepType stepType in Enum.GetValues<StepType>())
        {
            string begin = $"<!-- step-card:begin {stepType} -->";
            string end = $"<!-- step-card:end {stepType} -->";
            int b = text.IndexOf(begin, StringComparison.Ordinal);
            int e = text.IndexOf(end, StringComparison.Ordinal);
            if (b < 0 || e < b)
            {
                problems.Add($"{stepType}: markers missing");
                continue;
            }
            string expected = "\n" + Render(StepCatalog.Get(stepType)) + "\n";
            string current = text[(b + begin.Length)..e];
            if (current == expected)
                continue;
            if (update)
                text = text[..(b + begin.Length)] + expected + text[e..];
            else
                problems.Add($"{stepType}: committed block differs from the render");
        }

        if (update)
            File.WriteAllText(path, text);
        else
            problems.Should().BeEmpty($"regenerate with {UpdateVariable}=1 (see the file header of step-cards.md)");
    }

    [Fact]
    public void Each_step_type_has_exactly_one_section_and_no_unknown_card_sections()
    {
        string text = File.ReadAllText(Path.Combine(AssistantKbPaths.KbDir(), "step-cards.md"));
        List<string> headings = Regex.Matches(text, @"^## (\w+)\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        headings.Should().BeEquivalentTo(Enum.GetNames<StepType>());
    }

    /// <summary>The block's text: one bullet per fact line, from the same text retrieval indexes.</summary>
    internal static string Render(StepCard card)
    {
        StringBuilder sb = new();
        sb.Append("Generated from the step catalog; do not edit by hand.\n\n");
        sb.Append(string.Join("\n", StepCatalog.FactText(card).Split('\n').Select(l => "- " + l.TrimEnd())));
        return sb.ToString();
    }
}

public class RecipesDocTests
{
    [Fact]
    public void Recipes_has_one_section_per_template_with_the_real_key_in_the_same_order()
    {
        string text = File.ReadAllText(Path.Combine(AssistantKbPaths.KbDir(), "recipes.md"));
        List<string> keys = Regex.Matches(text, @"^## .*\(`([a-z0-9-]+)`\)\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        // Every recipe of either edition: the app-input edition is today's catalog plus "avatar-clip" (docs/design/app-inputs.md 22).
        keys.Should().Equal(WorkflowTemplateCatalog.GetAll().Select(t => t.Key));
    }
}

public class AssistantKbStructureTests
{
    [Fact]
    public void Every_section_fits_the_indexer_chunk_cap()
    {
        List<string> tooLong = [];
        foreach (string file in Directory.EnumerateFiles(AssistantKbPaths.KbDir(), "*.md"))
        {
            foreach (string section in Regex.Split(File.ReadAllText(file).Replace("\r\n", "\n"), @"\n(?=## )").Skip(1))
            {
                if (section.Length > 2400)
                    tooLong.Add($"{Path.GetFileName(file)}: {section.Split('\n')[0]} ({section.Length})");
            }
        }
        tooLong.Should().BeEmpty("the indexer chunks at ## with a 2400-character cap");
    }

    [Fact]
    public void Recipe_review_loop_positions_match_the_catalog()
    {
        string text = File.ReadAllText(Path.Combine(AssistantKbPaths.KbDir(), "recipes.md"));
        List<string> wrong = [];
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
        {
            string? section = Regex.Split(text, @"\n(?=## )").FirstOrDefault(s => s.Contains($"(`{template.Key}`)"));
            Match m = section is null ? Match.Empty : Regex.Match(section, @"review loop \(step (\d+) targets step (\d+)\)");
            if (!m.Success)
                continue;
            int index = template.Steps.ToList().FindIndex(s => s.StepType == StepType.ReviewLoop) + 1;
            int target = template.Steps[index - 1].LoopTargetStepOrder ?? 0;
            if (m.Groups[1].Value != index.ToString() || m.Groups[2].Value != target.ToString())
                wrong.Add($"{template.Key}: doc says {m.Groups[1].Value}->{m.Groups[2].Value}, catalog {index}->{target}");
        }
        wrong.Should().BeEmpty();
    }
}

public class AssistantKbNoFiguresTests
{
    // A number next to a currency sign or word, next to "credit(s)", or right after cost/price words.
    private static readonly Regex[] Figures =
    [
        new(@"[$€£¥]\s?\d", RegexOptions.Compiled),
        new(@"\d\s?[$€£¥]", RegexOptions.Compiled),
        new(@"\d[\d.,]*\s*(USD|EUR|GBP|dollars?|euros?|cents?|¢)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\d[\d.,]*\s*(credits?|cr)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bcredits?\b[^.\n]{0,12}\d", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(costs?|prices?|priced|pricing|charges?)\b[^.\n]{0,25}\d", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bper (second|minute|clip|run|image|token)\b[^.\n]{0,15}\d", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    ];

    [Fact]
    public void The_assistant_kb_folder_states_no_credit_dollar_or_price_figure()
    {
        string dir = AssistantKbPaths.KbDir();
        List<string> hits = [];
        foreach (string file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Regex rx in Figures)
                {
                    if (rx.IsMatch(lines[i]))
                        hits.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }
        hits.Should().BeEmpty("the docs say how to find a number (the estimate), never state one");
    }

    [Fact]
    public void Figure_patterns_catch_the_obvious_forms()
    {
        foreach (string bad in new[] { "costs about 12 credits", "$4.50 per run", "5 USD", "price is 3 euros", "credits: 40", "about €2" })
            Figures.Any(r => r.IsMatch(bad)).Should().BeTrue(bad);
        foreach (string good in new[] { "It uses credits; read the estimate.", "Step 3 of 5", "about 60 seconds" })
            Figures.Any(r => r.IsMatch(good)).Should().BeFalse(good);
    }

    [Fact]
    public void The_folder_has_the_six_files_the_spec_names()
    {
        string[] expected = ["recipes", "step-cards", "how-steps-connect", "writing-a-brief", "goal-to-recipe", "project-readiness"];
        foreach (string name in expected)
            File.Exists(Path.Combine(AssistantKbPaths.KbDir(), name + ".md")).Should().BeTrue(name);
    }
}

internal static class AssistantKbPaths
{
    public static string KbDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "docs", "assistant-kb")))
            dir = Path.GetDirectoryName(dir);
        return dir is null ? throw new InvalidOperationException("docs/assistant-kb not found above the test binaries") : Path.Combine(dir, "docs", "assistant-kb");
    }
}
