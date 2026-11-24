using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents.Tools;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>T-006: the advisory-only <c>design_system_colors_unused</c> reminder.</summary>
public sealed class DesignSystemColourAdvisoryTests
{
    private static readonly IReadOnlyDictionary<string, string> Palette = new Dictionary<string, string>
    {
        ["1a73e8"] = "color.brand.primary",
        ["fbbc04"] = "color.brand.accent"
    };

    private const string Foreign =
        "const a = '#ff0000'; const b = '#00aa55'; const c = 'rgb(10, 200, 30)'; const d = 'hsl(280, 60%, 50%)';";

    [Fact]
    public void Four_foreign_colours_and_no_design_colour_warns_and_names_palette_colours()
    {
        string? advice = AuthoringAdvisories.DesignSystemColourAdvice("src/Scene.tsx", Foreign, Palette);

        advice.Should().NotBeNull();
        advice.Should().StartWith("design_system_colors_unused");
        advice.Should().Contain("color.brand.primary").And.Contain("#1a73e8");
    }

    [Fact]
    public void A_design_system_colour_in_any_notation_silences_it()
    {
        AuthoringAdvisories.DesignSystemColourAdvice("a.tsx", Foreign + " const e = '#1A73E8';", Palette).Should().BeNull();
        AuthoringAdvisories.DesignSystemColourAdvice("a.css", Foreign + " x { color: rgb(251, 188, 4); }", Palette).Should().BeNull();
    }

    [Fact]
    public void Neutral_and_transparent_colours_do_not_count_and_few_colours_do_not_warn()
    {
        const string neutrals = "'#000' '#fff' '#ffffff' '#111111' 'rgba(0,0,0,0.5)' 'rgba(255, 0, 0, 0)' '#ff000000' '#ff0000' '#00aa55'";
        AuthoringAdvisories.DesignSystemColourAdvice("a.tsx", neutrals, Palette).Should().BeNull();
    }

    [Fact]
    public void No_design_system_palette_means_no_advisory()
    {
        AuthoringAdvisories.DesignSystemColourAdvice("a.tsx", Foreign, new Dictionary<string, string>()).Should().BeNull();
    }

    [Fact]
    public void Non_source_files_are_not_scanned()
    {
        AuthoringAdvisories.DesignSystemColourAdvice("notes.md", Foreign, Palette).Should().BeNull();
    }

    [Fact]
    public void The_warning_is_given_once_per_file_per_run()
    {
        var state = new ReactRemotionSandboxTools.AdvisoryState();

        ReactRemotionSandboxTools.DesignSystemColourWarning(state, "a.tsx", Foreign, Palette).Should().NotBeNull();
        ReactRemotionSandboxTools.DesignSystemColourWarning(state, "a.tsx", Foreign, Palette).Should().BeNull();
        ReactRemotionSandboxTools.DesignSystemColourWarning(state, "b.tsx", Foreign, Palette).Should().NotBeNull();
    }
}
