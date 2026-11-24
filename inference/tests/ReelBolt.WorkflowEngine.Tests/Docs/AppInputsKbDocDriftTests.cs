using System;
using System.Collections.Generic;
using System.Globalization;
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
/// docs/assistant-kb/app-inputs.md (docs/design/app-inputs.md 13.3, lane L8): one "Settings a field can fill" section per
/// step type that has bindable settings, each with a generated block equal to the allowlist's render now, and no section
/// for a step type without any. Regenerate the blocks (and add a missing section) with:
/// UPDATE_APP_INPUTS_DOC=1 dotnet test --filter AppInputsKbDocDriftTests
/// </summary>
public class AppInputsKbDocDriftTests
{
    private const string UpdateVariable = "UPDATE_APP_INPUTS_DOC";
    private const string Heading = "## Settings a field can fill: ";

    private static IEnumerable<StepType> BindableTypes() =>
        Enum.GetValues<StepType>().Where(t => StepCatalog.BindableFieldsOf(t).Count > 0);

    [Fact]
    public void Every_step_type_with_bindable_settings_has_a_generated_block_equal_to_the_render()
    {
        string path = Path.Combine(AssistantKbPaths.KbDir(), "app-inputs.md");
        string text = File.ReadAllText(path).Replace("\r\n", "\n");
        bool update = Environment.GetEnvironmentVariable(UpdateVariable) == "1";
        List<string> problems = [];

        foreach (StepType stepType in BindableTypes())
        {
            string begin = $"<!-- bindable:begin {stepType} -->";
            string end = $"<!-- bindable:end {stepType} -->";
            string expected = "\n" + Render(stepType) + "\n";
            int b = text.IndexOf(begin, StringComparison.Ordinal);
            int e = text.IndexOf(end, StringComparison.Ordinal);
            if (b < 0 || e < b)
            {
                if (update)
                    text = text.TrimEnd('\n') + $"\n\n{Heading}{stepType}\n\n{begin}{expected}{end}\n";
                else
                    problems.Add($"{stepType}: section or markers missing");
                continue;
            }

            if (text[(b + begin.Length)..e] == expected)
                continue;
            if (update)
                text = text[..(b + begin.Length)] + expected + text[e..];
            else
                problems.Add($"{stepType}: committed block differs from the render");
        }

        if (update)
            File.WriteAllText(path, text);
        else
            problems.Should().BeEmpty($"regenerate with {UpdateVariable}=1 (see the file header of app-inputs.md)");
    }

    [Fact]
    public void No_section_names_a_step_type_without_bindable_settings()
    {
        string text = File.ReadAllText(Path.Combine(AssistantKbPaths.KbDir(), "app-inputs.md"));
        List<string> named = [.. Regex.Matches(text, @"^## Settings a field can fill: (\w+)\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];
        named.Should().BeEquivalentTo(BindableTypes().Select(t => t.ToString()));
    }

    /// <summary>The block's text: one bullet per setting a field can fill, from the allowlist itself.</summary>
    internal static string Render(StepType stepType)
    {
        StringBuilder sb = new();
        sb.Append("Generated from the step catalog; do not edit by hand.\n");
        foreach (StepCardBindableField field in StepCatalog.BindableFieldsOf(stepType))
        {
            if (field.Mode == "value" && field.ValueKind == "boolean" && field.Types.SequenceEqual(["toggle"]))
            {
                sb.Append($"\n- `{field.Path}` ({field.Label}): true or false (a switch, or a choice mapping its values).");
                continue;
            }

            List<string> facts = [field.Mode == "placeholder" ? "text, through a {{input.id}} placeholder"
                : field.Path.Contains("[]", StringComparison.Ordinal) ? "a whole value of one item, through an appInputs entry naming the item (for example " + field.Path.Replace("[]", "[0]", StringComparison.Ordinal) + ")"
                : "whole value (appInputs)"];
            facts.Add("type " + string.Join(" or ", field.Types));
            if (field.MaxLength is { } maxLength)
                facts.Add($"at most {maxLength.ToString(CultureInfo.InvariantCulture)} characters");
            if (field.Min is { } min && field.Max is { } max)
                facts.Add($"from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}");
            if (field.Choices is { Count: > 0 } choices)
                facts.Add("one of " + string.Join(", ", choices));
            if (field.Capabilities is { Count: > 0 } capabilities)
                facts.Add("a " + string.Join(" or ", capabilities) + " model");
            if (field.GovernedByBriefField is { } brief)
                facts.Add($"the brief's {brief} field decides it when the page has that field");
            sb.Append($"\n- `{field.Path}` ({field.Label}): {string.Join("; ", facts)}.");
        }

        return sb.ToString();
    }
}
