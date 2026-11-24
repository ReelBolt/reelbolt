using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Views;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Views;

/// <summary>The Logo line names whose logo it is and where it reads, from the persisted keys only.</summary>
public class GraphicsPaletteLogoQualifierTests
{
    private static readonly Guid LogoId = Guid.NewGuid();

    private static string Line(JsonObject logo)
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#6d28d9" },
            new TokenProvenance("manual", null, null, null), 1.0);
        logo["fileRef"] = "project-file:" + LogoId;
        doc["assets"] = new JsonObject { ["logo"] = logo };
        string block = DesignSystemPromptView.RenderDesign(
            doc, DesignView.GraphicsPalette, 1, 2400, new Dictionary<Guid, string> { [LogoId] = "mark.svg" })!;
        return Array.Find(block.Split('\n'), l => l.StartsWith("Logo:", StringComparison.Ordinal))!;
    }

    [Fact]
    public void Product_logo_names_the_tone_it_reads_on()
    {
        Line(new JsonObject { ["subject"] = "product", ["tone"] = "light", ["readableOn"] = new JsonArray("dark") })
            .Should().Be("Logo: available as a project file named \"mark.svg\" (product logo; readable on dark backgrounds only)");
    }

    [Fact]
    public void Publisher_base_logo_points_at_the_product_variant()
    {
        string line = Line(new JsonObject
        {
            ["subject"] = "publisher",
            ["variants"] = new JsonArray(new JsonObject { ["subject"] = "product", ["file"] = "app.svg", ["tone"] = "dark", ["readableOn"] = new JsonArray("light") }),
        });

        line.Should().Contain("company mark, not the product logo").And.Contain("product logo: \"app.svg\", readable on light backgrounds only");
    }

    [Fact]
    public void Legacy_logo_without_keys_keeps_the_old_line()
    {
        Line(new JsonObject()).Should().Be("Logo: available as a project file named \"mark.svg\"");
    }
}
