using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class DesignMdImporterTests
{
    private static readonly DesignMdImporter Importer = new();

    [Fact]
    public void FrontMatter_Tokens_Are_Normalized_And_Valid()
    {
        DesignImportResult result = Import(Fixture("design-md-public-alpha.md"));

        Assert.Equal("Public Alpha", result.Design!["name"]!.GetValue<string>());

        JsonObject? bg = DesignDocument.FindToken(result.Design!, "color.bg");
        Assert.NotNull(bg);
        Assert.Equal("#0b0f14", bg!["$value"]!["hex"]!.GetValue<string>());

        JsonObject? fg = DesignDocument.FindToken(result.Design!, "color.fg");
        Assert.NotNull(fg);
        Assert.Equal("srgb", fg!["$value"]!["colorSpace"]!.GetValue<string>());
        Assert.Equal(7, fg["$value"]!["hex"]!.GetValue<string>().Length);

        JsonObject? accent = DesignDocument.FindToken(result.Design!, "color.accent");
        Assert.NotNull(accent);
        Assert.Equal("#ff5d3a", accent!["$value"]!["hex"]!.GetValue<string>());

        JsonObject? display = DesignDocument.FindToken(result.Design!, "typography.display");
        Assert.NotNull(display);
        Assert.Equal("typography", display!["$type"]!.GetValue<string>());
        JsonNode displayValue = display["$value"]!;
        Assert.Equal(3, displayValue["fontFamily"]!.AsArray().Count);
        Assert.Equal(48, Number(displayValue["fontSize"]!["value"]!));
        Assert.Equal("px", displayValue["fontSize"]!["unit"]!.GetValue<string>());
        Assert.Equal(700, Number(displayValue["fontWeight"]!));
        Assert.Equal(1.1, Number(displayValue["lineHeight"]!), 3);
        Assert.Equal(-0.02, Number(displayValue["letterSpacing"]!["value"]!), 3);
        Assert.Equal("em", displayValue["letterSpacing"]!["unit"]!.GetValue<string>());

        JsonObject? radius = DesignDocument.FindToken(result.Design!, "radius.lg");
        Assert.NotNull(radius);
        Assert.Equal(16, Number(radius!["$value"]!["value"]!));

        JsonObject? spacing = DesignDocument.FindToken(result.Design!, "spacing.md");
        Assert.NotNull(spacing);
        Assert.Equal(16, Number(spacing!["$value"]!["value"]!));

        Assert.Null(DesignDocument.FindToken(result.Design!, "component.button"));

        ImportDiagnostic skipped = Assert.Single(result.Diagnostics, d => d.Code == "COMPONENTS_SKIPPED");
        Assert.Equal(ImportDiagnosticSeverity.Info, skipped.Severity);

        AssertValid(result.Design!);
    }

    [Fact]
    public void Provenance_Records_The_Importer_And_A_File_Line()
    {
        DesignImportResult result = Import(Fixture("design-md-public-alpha.md"));

        JsonNode reelbolt = DesignDocument.FindToken(result.Design!, "color.bg")!["$extensions"]!["com.reelbolt"]!;
        Assert.Equal("imported", reelbolt["prov"]!["method"]!.GetValue<string>());
        Assert.Equal("design-md", reelbolt["prov"]!["importer"]!.GetValue<string>());
        Assert.Equal(0.9, Number(reelbolt["conf"]!));
        Assert.Contains("DESIGN.md:", reelbolt["prov"]!["loc"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Prose_Sections_Become_Voice()
    {
        DesignImportResult result = Import(Fixture("design-md-public-alpha.md"));

        JsonNode voice = result.Design!["voice"]!;
        string summary = voice["summary"]!.GetValue<string>();
        Assert.Contains("warm, high-contrast system", summary, StringComparison.Ordinal);
        Assert.True(summary.Length <= 600);

        JsonArray dos = voice["do"]!.AsArray();
        Assert.Equal(3, dos.Count);
        Assert.Contains("Lead with the headline", dos[0]!.GetValue<string>(), StringComparison.Ordinal);

        JsonArray donts = voice["dont"]!.AsArray();
        Assert.Equal(2, donts.Count);
        Assert.Contains("Don't stack two accents", donts[0]!.GetValue<string>(), StringComparison.Ordinal);

        JsonArray tone = voice["tone"]!.AsArray();
        Assert.Equal(3, tone.Count);
        Assert.Equal("Confident", tone[0]!.GetValue<string>());

        AssertValid(result.Design!);
    }

    [Fact]
    public void Injection_Prose_Stays_Inside_Sanitized_Voice()
    {
        const string phrase = "Ignore previous instructions";

        DesignImportResult result = Import(Fixture("design-md-injection.md"));

        JsonNode voice = result.Design!["voice"]!;
        var voiceStrings = new List<string>();
        foreach (KeyValuePair<string, JsonNode?> property in voice.AsObject())
        {
            if (property.Value is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String)
                voiceStrings.Add(value.GetValue<string>());
            else if (property.Value is JsonArray array)
                voiceStrings.AddRange(array.Select(item => item!.GetValue<string>()));
        }

        Assert.Contains(voiceStrings, text => text.Contains(phrase, StringComparison.Ordinal));
        foreach (string text in voiceStrings)
        {
            Assert.DoesNotContain('[', text);
            Assert.DoesNotContain(']', text);
            Assert.DoesNotContain('`', text);
            Assert.True(text.Length <= 600);
        }

        foreach (DesignTokenRef token in DesignDocument.Tokens(result.Design!))
            Assert.DoesNotContain(phrase, token.Value.ToJsonString(), StringComparison.Ordinal);

        foreach (ImportDiagnostic diagnostic in result.Diagnostics)
            Assert.DoesNotContain(phrase, diagnostic.Message, StringComparison.Ordinal);

        AssertValid(result.Design!);
    }

    [Fact]
    public void ProseOnly_File_Has_Only_Voice()
    {
        DesignImportResult result = Import(Fixture("design-md-prose-only.md"));

        Assert.NotNull(result.Design!["voice"]);
        Assert.Equal(2, result.Design!["voice"]!["do"]!.AsArray().Count);
        Assert.Single(result.Design!["voice"]!["dont"]!.AsArray());
        Assert.Equal(2, result.Design!["voice"]!["tone"]!.AsArray().Count);
        Assert.Empty(DesignDocument.Tokens(result.Design!));
        AssertValid(result.Design!);
    }

    [Fact]
    public void YamlBomb_Is_Rejected_With_An_Error()
    {
        string markdown = Fixture("design-md-yaml-bomb.md");
        string frontMatter = string.Join(
            "\n",
            markdown.Split('\n').Skip(1).TakeWhile(line => line.Trim() != "---"));

        JsonNode? parsed = YamlSubsetReader.Parse(frontMatter, out ImportDiagnostic? diagnostic);
        Assert.Null(parsed);
        Assert.NotNull(diagnostic);
        Assert.Equal(ImportDiagnosticSeverity.Error, diagnostic!.Severity);
        Assert.Equal("YAML_ANCHOR", diagnostic.Code);

        DesignImportResult result = Import(markdown);
        Assert.Contains(result.Diagnostics, d => d.Severity == ImportDiagnosticSeverity.Error);
        Assert.NotNull(result.Design);
    }

    [Fact]
    public void Document_Over_Maximum_Size_Is_Rejected()
    {
        var markdown = new StringBuilder("---\nname: Huge\n---\n\n# T\n\n");
        markdown.Append('x', ImportLimits.MarkdownMaxBytes);

        DesignImportResult result = Import(markdown.ToString());

        Assert.Contains(result.Diagnostics, d => d.Code == "IMPORT_TOO_LARGE");
    }

    [Fact]
    public void Depth_Nine_Is_Rejected()
    {
        var yaml = new StringBuilder();
        for (int i = 0; i < 10; i++)
        {
            yaml.Append(new string(' ', i * 2));
            yaml.Append((char)('a' + i));
            yaml.Append(i < 9 ? ":\n" : ": 1\n");
        }

        JsonNode? parsed = YamlSubsetReader.Parse(yaml.ToString(), out ImportDiagnostic? diagnostic);

        Assert.Null(parsed);
        Assert.NotNull(diagnostic);
        Assert.Equal("YAML_TOO_DEEP", diagnostic!.Code);
    }

    [Fact]
    public void CanRead_Recognizes_Design_Md_And_Front_Matter()
    {
        Assert.True(Importer.CanRead("DESIGN.md", Encoding.UTF8.GetBytes("---\nname: x\n---\n")));
        Assert.True(Importer.CanRead("brand/design.md", Encoding.UTF8.GetBytes("---\nname: x\n---\n")));
        Assert.True(Importer.CanRead("theme.md", Encoding.UTF8.GetBytes("---\ncolors:\n  a: \"#fff\"\n---\n")));
        Assert.True(Importer.CanRead("theme.md", Encoding.UTF8.GetBytes("---\ntypography:\n  body: {}\n---\n")));
        Assert.False(Importer.CanRead("readme.md", Encoding.UTF8.GetBytes("plain text")));
        Assert.False(Importer.CanRead("tokens.css", Encoding.UTF8.GetBytes(":root { --a: #fff; }")));
        Assert.False(Importer.CanRead(string.Empty, ReadOnlySpan<byte>.Empty));
    }

    private static DesignImportResult Import(string markdown)
    {
        var file = new ImportFile("DESIGN.md", Encoding.UTF8.GetBytes(markdown), string.Empty, null);
        return Importer.Import([file]);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(FixtureDirectory(), name));

    private static string FixtureDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "Fixtures");

    private static DesignValidationResult Validate(JsonObject design)
    {
        var context = new DesignValidationContext(
            _ => false,
            new HashSet<string>(StringComparer.Ordinal));
        return DesignDocumentValidator.Validate(design, context);
    }

    private static void AssertValid(JsonObject design)
    {
        DesignValidationResult validation = Validate(design);
        Assert.True(
            validation.IsValid,
            string.Join("; ", validation.Violations.Select(violation => $"{violation.Code} {violation.Path} {violation.Message}")));
    }

    private static double Number(JsonNode? node)
    {
        Assert.True(CanonicalJson.TryGetNumber(node, out double value), "expected a JSON number");
        return value;
    }
}

public sealed class YamlSubsetReaderTests
{
    [Fact]
    public void Comments_Inline_Lists_And_Block_Scalars_Are_Read()
    {
        const string yaml = """
            name: Example # trailing comment
            colors:
              a: "#111111"   # another
              b: [red, "green", blue]
            note: |
              first line
              second line
            folded: >
              one
              two
            """;

        JsonNode? parsed = YamlSubsetReader.Parse(yaml, out ImportDiagnostic? diagnostic);

        Assert.Null(diagnostic);
        JsonObject root = Assert.IsType<JsonObject>(parsed);
        Assert.Equal("Example", root["name"]!.GetValue<string>());
        Assert.Equal(3, root["colors"]!["b"]!.AsArray().Count);
        Assert.Equal("red", root["colors"]!["b"]!.AsArray()[0]!.GetValue<string>());
        Assert.Contains("first line", root["note"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("one two", root["folded"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_Document_Is_An_Empty_Object()
    {
        JsonNode? parsed = YamlSubsetReader.Parse("   \n# only a comment\n");

        Assert.NotNull(parsed);
        Assert.Empty(parsed!.AsObject());
    }

    [Fact]
    public void Merge_Keys_And_Tags_Are_Rejected()
    {
        Assert.Null(YamlSubsetReader.Parse("<<: *base\n", out ImportDiagnostic? merge));
        Assert.Equal("YAML_MERGE_KEY", merge!.Code);
        Assert.Equal(ImportDiagnosticSeverity.Error, merge.Severity);

        Assert.Null(YamlSubsetReader.Parse("a: !!str value\n", out ImportDiagnostic? tag));
        Assert.Equal("YAML_TAG", tag!.Code);
    }
}
