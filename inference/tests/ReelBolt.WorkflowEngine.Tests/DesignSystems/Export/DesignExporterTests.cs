using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Export;

public sealed class DesignExporterTests
{
    [Fact]
    public void ToJson_Returns_An_Independent_Clone()
    {
        JsonObject original = Fixture();

        JsonObject clone = DesignExporter.ToJson(original);
        clone["name"] = "Changed";

        Assert.Equal("Acme Product Design System", original["name"]!.GetValue<string>());
        Assert.Equal("Changed", clone["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToDtcg_Moves_Non_Token_Sections_Under_The_Root_Extension()
    {
        JsonObject dtcg = DesignExporter.ToDtcg(Fixture());

        Assert.Null(dtcg["modes"]);
        Assert.Null(dtcg["voice"]);
        Assert.Null(dtcg["assets"]);
        Assert.Null(dtcg["fonts"]);

        JsonNode extension = dtcg["$extensions"]!["com.reelbolt"]!;
        Assert.NotNull(extension["modes"]);
        Assert.NotNull(extension["voice"]);
        Assert.NotNull(extension["assets"]);
        Assert.NotNull(extension["fonts"]);
    }

    [Fact]
    public void ToDtcg_Preserves_Token_Extensions_And_Description()
    {
        JsonObject dtcg = DesignExporter.ToDtcg(Fixture());
        JsonObject token = DesignDocument.FindToken(dtcg, "color.primary")!;

        Assert.Equal("Primary brand colour", token["$description"]!.GetValue<string>());

        JsonNode reelbolt = token["$extensions"]!["com.reelbolt"]!;
        Assert.Equal("static", reelbolt["prov"]!["method"]!.GetValue<string>());
        Assert.Equal("css", reelbolt["prov"]!["importer"]!.GetValue<string>());
        Assert.True(reelbolt["locked"]!.GetValue<bool>());
    }

    [Fact]
    public void ToDtcg_Omits_Source_File_Ids_And_Paths_From_Provenance_But_Keeps_The_Rest()
    {
        JsonObject document = Fixture();
        DesignDocument.SetToken(
            document,
            "color.leaky",
            "color",
            new JsonObject { ["hex"] = "#112233", ["colorSpace"] = "srgb" },
            new TokenProvenance("imported", "css", Guid.NewGuid(), "src/private/theme.css:12"),
            0.9);

        JsonObject dtcg = DesignExporter.ToDtcg(document);

        JsonNode prov = DesignDocument.FindToken(dtcg, "color.leaky")!["$extensions"]!["com.reelbolt"]!["prov"]!;
        Assert.Null(prov["sourceId"]);
        Assert.Null(prov["loc"]);
        Assert.Equal("imported", prov["method"]!.GetValue<string>());
        Assert.Equal("css", prov["importer"]!.GetValue<string>());
        Assert.DoesNotContain("src/private", dtcg.ToJsonString(), StringComparison.Ordinal);
        // The input document is not touched.
        Assert.NotNull(DesignDocument.FindToken(document, "color.leaky")!["$extensions"]!["com.reelbolt"]!["prov"]!["loc"]);
    }

    [Fact]
    public void ToDesignMd_Escapes_Markdown_Links_And_Html_In_Name_And_Voice()
    {
        JsonObject document = Fixture();
        document["name"] = "[Acme](https://evil.example/x) <img src=x onerror=alert(1)>";
        document["voice"] = new JsonObject
        {
            ["summary"] = "Click [here](javascript:alert(1)) <script>x</script> `code`",
            ["do"] = new JsonArray("[a](https://evil.example)", "<b>bold</b>"),
            ["dont"] = new JsonArray("![img](https://evil.example/p.png)"),
            ["tone"] = new JsonArray("<i>x</i>"),
        };

        string markdown = DesignExporter.ToDesignMd(document);
        string body = markdown[(markdown.IndexOf("\n---\n", 4, StringComparison.Ordinal) + 5)..];

        Assert.DoesNotContain("<img", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", body, StringComparison.Ordinal);
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(body, @"(?<!\\)\]\("), "unescaped link target opener"); // no unescaped link target opener
        Assert.DoesNotContain("`", body.Replace("\\`", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("&lt;img", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ToDtcg_Expands_Colours_To_Components_And_Alpha()
    {
        JsonObject dtcg = DesignExporter.ToDtcg(Fixture());

        JsonNode primary = DesignDocument.FindToken(dtcg, "color.primary")!["$value"]!;
        Assert.Equal("srgb", primary["colorSpace"]!.GetValue<string>());
        Assert.Equal(3, primary["components"]!.AsArray().Count);
        Assert.Equal(1.0, primary["alpha"]!.GetValue<double>(), 3);
        Assert.Equal("#2563eb", primary["hex"]!.GetValue<string>());

        JsonNode shadowColour = DesignDocument.FindToken(dtcg, "shadow.sm")!["$value"]!["color"]!;
        Assert.Equal("#00000033", shadowColour["hex"]!.GetValue<string>());
        Assert.Equal(0.2, shadowColour["alpha"]!.GetValue<double>(), 2);
    }

    [Fact]
    public void ToCss_Emits_Root_Variables_And_A_Dark_Block()
    {
        string css = DesignExporter.ToCss(Fixture(), tailwindV4: false);

        Assert.StartsWith(":root {\n", css, StringComparison.Ordinal);
        Assert.Contains("  --color-brand-primary: #1d4ed8;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --color-brand-secondary: #6d28d9;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --typography-family-primary: Inter, sans-serif;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --typography-size-md: 16px;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --spacing-md: 16px;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --radius-md: 8px;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --motion-easing-standard: cubic-bezier(0.2, 0, 0, 1);\n", css, StringComparison.Ordinal);
        Assert.Contains(".dark {\n", css, StringComparison.Ordinal);
        Assert.Contains("  --color-surface: #111827;\n", css, StringComparison.Ordinal);
        Assert.DoesNotContain("--typography-heading-h1", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCss_Tailwind_Uses_The_V4_Namespaces()
    {
        string css = DesignExporter.ToCss(Fixture(), tailwindV4: true);

        Assert.StartsWith("@theme {\n", css, StringComparison.Ordinal);
        Assert.Contains("  --color-brand-primary: #1d4ed8;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --font-primary: Inter, sans-serif;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --text-md: 16px;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --spacing-md: 16px;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --radius-md: 8px;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --shadow-sm: 0px 1px 2px 0px #00000033;\n", css, StringComparison.Ordinal);
        Assert.Contains("  --ease-standard: cubic-bezier(0.2, 0, 0, 1);\n", css, StringComparison.Ordinal);
        Assert.Contains(".dark {\n", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCss_Escapes_Hostile_Values()
    {
        var document = DesignDocument.New("Hostile");
        DesignDocument.SetToken(
            document,
            "typography.family.evil",
            "fontFamily",
            JsonValue.Create("Inter; } </style><script>"),
            new TokenProvenance("manual", null, null, null),
            1.0);
        DesignDocument.SetToken(
            document,
            "color.evil",
            "color",
            new JsonObject { ["hex"] = "#fff;}</style>", ["colorSpace"] = "srgb" },
            new TokenProvenance("manual", null, null, null),
            1.0);

        string css = DesignExporter.ToCss(document, tailwindV4: false);

        Assert.DoesNotContain("</style>", css, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", css, StringComparison.Ordinal);
        Assert.Contains("\\;", css, StringComparison.Ordinal);
        Assert.Contains("\\}", css, StringComparison.Ordinal);
        Assert.Contains("\\<", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ToDesignMd_Emits_Front_Matter_And_Prose()
    {
        string markdown = DesignExporter.ToDesignMd(Fixture());

        Assert.StartsWith("---\n", markdown, StringComparison.Ordinal);
        Assert.Contains("name: Acme Product Design System\n", markdown, StringComparison.Ordinal);
        Assert.Contains("colors:\n", markdown, StringComparison.Ordinal);
        Assert.Contains("  primary: \"#2563eb\"\n", markdown, StringComparison.Ordinal);
        Assert.Contains("typography:\n", markdown, StringComparison.Ordinal);
        Assert.Contains("  heading-h1:\n", markdown, StringComparison.Ordinal);
        Assert.Contains("    fontSize: 40px\n", markdown, StringComparison.Ordinal);
        Assert.Contains("rounded:\n", markdown, StringComparison.Ordinal);
        Assert.Contains("  sm: 4px\n", markdown, StringComparison.Ordinal);
        Assert.Contains("spacing:\n", markdown, StringComparison.Ordinal);
        Assert.Contains("  md: 16px\n", markdown, StringComparison.Ordinal);
        Assert.Contains("## Overview\n", markdown, StringComparison.Ordinal);
        Assert.Contains("## Do's and Don'ts\n", markdown, StringComparison.Ordinal);
        Assert.Contains("### Do's\n", markdown, StringComparison.Ordinal);
        Assert.Contains("### Don'ts\n", markdown, StringComparison.Ordinal);
    }

    private static JsonObject Fixture()
    {
        string path = Path.Combine(FixtureDirectory(), "design-valid.json");
        return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    }

    private static string FixtureDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "Fixtures");
}
