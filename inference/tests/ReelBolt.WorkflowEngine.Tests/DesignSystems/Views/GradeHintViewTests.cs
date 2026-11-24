using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Views;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Views;

/// <summary>
/// P4-02 golden coverage for the <see cref="DesignView.GradeHint"/> renderer: the exact single line,
/// the warm/cool/neutral classes, the no-forged-fence rule and the empty-palette fallback.
/// </summary>
public class GradeHintViewTests
{
    private static readonly TokenProvenance Prov = new("manual", null, null, null);

    private const string LinePrefix = "Brand colour temperature: ";
    private const string LineSuffix = ". This is a hint, not a rule; choosing no grade is fine.";

    [Fact]
    public void Warm_palette_matches_golden_text()
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#ff0000" }, Prov, 1.0);
        DesignDocument.SetToken(doc, "color.brand.secondary", "color", new JsonObject { ["hex"] = "#ff7f00" }, Prov, 1.0);
        DesignDocument.SetToken(doc, "color.accent", "color", new JsonObject { ["hex"] = "#ff4500" }, Prov, 1.0);

        string? block = DesignSystemPromptView.RenderDesign(doc, DesignView.GradeHint, 1, 2400);

        string expected = string.Join(
            "\n",
            "[DESIGN SYSTEM v1]",
            DesignSystemPromptView.DesignHeader,
            LinePrefix + "warm" + LineSuffix,
            "[END DESIGN SYSTEM]");

        block.Should().Be(expected);
    }

    [Fact]
    public void Cool_palette_renders_cool()
    {
        string? block = DesignSystemPromptView.RenderDesign(CoolDoc(), DesignView.GradeHint, 1, 2400);

        block.Should().NotBeNull();
        block!.Should().Contain(LinePrefix + "cool" + LineSuffix);
    }

    [Fact]
    public void Grey_palette_renders_neutral()
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#808080" }, Prov, 1.0);
        DesignDocument.SetToken(doc, "color.brand.secondary", "color", new JsonObject { ["hex"] = "#9e9e9e" }, Prov, 1.0);
        DesignDocument.SetToken(doc, "color.brand.accent", "color", new JsonObject { ["hex"] = "#bdbdbd" }, Prov, 1.0);

        string? block = DesignSystemPromptView.RenderDesign(doc, DesignView.GradeHint, 1, 2400);

        block.Should().NotBeNull();
        block!.Should().Contain(LinePrefix + "neutral" + LineSuffix);
    }

    [Fact]
    public void The_rendered_line_forges_no_fence()
    {
        string? block = DesignSystemPromptView.RenderDesign(DesignDocument.New("Acme"), DesignView.GradeHint, 1, 2400);

        block.Should().NotBeNull();
        string[] lines = block!.Split('\n');
        lines.Should().HaveCount(4);
        foreach (string line in lines[2..^1])
        {
            line.Should().NotContain("[");
            line.Should().NotContain("]");
        }
    }

    [Fact]
    public void An_empty_document_still_renders_neutral()
    {
        string? block = DesignSystemPromptView.RenderDesign(DesignDocument.New("Empty"), DesignView.GradeHint, 1, 2400);

        block.Should().NotBeNull();
        block!.Should().Contain("neutral");

        string? coolBlock = DesignSystemPromptView.RenderDesign(CoolDoc(), DesignView.GradeHint, 1, 2400);
        coolBlock!.Length.Should().BeLessThanOrEqualTo(2400);
    }

    private static JsonObject CoolDoc()
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#0000ff" }, Prov, 1.0);
        DesignDocument.SetToken(doc, "color.brand.secondary", "color", new JsonObject { ["hex"] = "#008080" }, Prov, 1.0);
        DesignDocument.SetToken(doc, "color.brand.accent", "color", new JsonObject { ["hex"] = "#00bfff" }, Prov, 1.0);
        return doc;
    }
}
