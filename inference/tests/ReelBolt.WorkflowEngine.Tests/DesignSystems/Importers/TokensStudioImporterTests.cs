using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class TokensStudioImporterTests
{
    private static readonly TokensStudioImporter Importer = new();

    [Fact]
    public void Basic_fixture_maps_expected_tokens()
    {
        DesignImportResult result = Import(Fixture("tokens-studio-basic.json"));

        JsonObject primary = Token(result, "color.primary");
        Assert.Equal("#6d28d9", primary["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal("Brand primary", primary["$description"]!.GetValue<string>());
        JsonObject provenance = Extensions(primary)["prov"]!.AsObject();
        Assert.Equal("imported", provenance["method"]!.GetValue<string>());
        Assert.Equal("tokens-studio", provenance["importer"]!.GetValue<string>());

        JsonObject text = Token(result, "color.text");
        Assert.Equal("#111827", text["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal("rgba(17, 24, 39, 1)", Extensions(text)["orig"]!.GetValue<string>());

        JsonObject accent = Token(result, "color.accent");
        Assert.Equal("{color.primary}", accent["$value"]!.GetValue<string>());

        JsonObject spacing = Token(result, "spacing.sm");
        Assert.Equal(8d, Number(spacing["$value"]!["value"]));
        Assert.Equal("px", spacing["$value"]!["unit"]!.GetValue<string>());

        JsonObject radius = Token(result, "radius.base");
        Assert.Equal(4d, Number(radius["$value"]!["value"]));

        JsonObject family = Token(result, "typography.family.body");
        Assert.Equal("Inter", family["$value"]!.AsArray()[0]!.GetValue<string>());

        JsonObject weight = Token(result, "typography.weight.bold");
        Assert.Equal(700, weight["$value"]!.GetValue<int>());

        JsonObject size = Token(result, "typography.size.h1");
        Assert.Equal(32d, Number(size["$value"]!["value"]));

        JsonObject lineHeight = Token(result, "typography.line-height.body");
        Assert.Equal("number", lineHeight["$type"]!.GetValue<string>());
        Assert.Equal(1.5d, Number(lineHeight["$value"]));

        JsonObject shadow = Token(result, "shadow.card");
        JsonObject shadowEntry = shadow["$value"]!.AsArray()[0]!.AsObject();
        Assert.Equal(4d, Number(shadowEntry["offsetY"]!["value"]));
        Assert.Equal(8d, Number(shadowEntry["blur"]!["value"]));
        Assert.Equal("#00000040", shadowEntry["color"]!["hex"]!.GetValue<string>());

        JsonObject style = Token(result, "typography.style.heading.h1");
        JsonObject styleValue = style["$value"]!.AsObject();
        Assert.Equal("Inter", styleValue["fontFamily"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(700, styleValue["fontWeight"]!.GetValue<int>());
        Assert.Equal(32d, Number(styleValue["fontSize"]!["value"]));
        Assert.Equal(1.5d, Number(styleValue["lineHeight"]));
    }

    [Fact]
    public void Math_expression_is_skipped_with_warning()
    {
        DesignImportResult result = Import(Fixture("tokens-studio-basic.json"));

        Assert.Null(DesignDocument.FindToken(result.Design!, "spacing.md"));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "UNSUPPORTED_VALUE" && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
    }

    [Fact]
    public void Unknown_type_is_dropped_with_warning()
    {
        DesignImportResult result = Import(Fixture("tokens-studio-basic.json"));

        Assert.DoesNotContain(DesignDocument.Tokens(result.Design!), token => token.Path.Contains("misc", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "UNKNOWN_TYPE" && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
    }

    [Fact]
    public void Dark_set_becomes_modes_dark()
    {
        DesignImportResult result = Import(Fixture("tokens-studio-basic.json"));

        JsonObject dark = result.Design!["modes"]!["dark"]!.AsObject();
        Assert.Equal("#0b0b0f", dark["color.background"]!["hex"]!.GetValue<string>());
        Assert.Equal("#f5f5f5", dark["color.text"]!["hex"]!.GetValue<string>());

        Assert.Null(DesignDocument.FindToken(result.Design!, "color.onlydark"));
        Assert.False(dark.ContainsKey("color.onlydark"));

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "MODE_TOKEN_SKIPPED" && diagnostic.Severity == ImportDiagnosticSeverity.Info);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "THEME_IGNORED" && diagnostic.Severity == ImportDiagnosticSeverity.Info);
    }

    [Fact]
    public void Output_validates()
    {
        DesignImportResult result = Import(Fixture("tokens-studio-basic.json"));

        DesignValidationResult validation = Validate(result.Design!);
        Assert.True(
            validation.IsValid,
            string.Join("; ", validation.Violations.Select(violation => $"{violation.Code} {violation.Path} {violation.Message}")));
    }

    [Theory]
    [InlineData("tokens.json", "{\"$themes\":[]}", true)]
    [InlineData("tokens.json", "{\"a\":{\"x\":{\"value\":\"1\",\"type\":\"sizing\"}}}", true)]
    [InlineData("tokens.json", "{\"c\":{\"$type\":\"color\",\"p\":{\"$value\":\"#fff\"}}}", false)]
    [InlineData("$metadata.json", "{}", true)]
    [InlineData("tokens.json", ":root { --color: #ffffff; }", false)]
    public void Detection_theory(string path, string head, bool expected)
    {
        Assert.Equal(expected, TokensStudioImporter.LooksLikeTokensStudio(path, Encoding.UTF8.GetBytes(head)));
    }

    [Fact]
    public void Dtcg_importer_does_not_claim_tokens_studio_files()
    {
        byte[] head = Encoding.UTF8.GetBytes(Fixture("tokens-studio-basic.json"));

        Assert.False(new DtcgImporter().CanRead("t.json", head));
    }

    [Fact]
    public void Registry_places_tokens_studio_before_the_phase_three_importers()
    {
        Assert.Equal("tokens-studio", DesignImportRegistry.All[3].Id);
    }

    [Fact]
    public void ImportAll_on_fixture_yields_one_valid_document()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Fixture("tokens-studio-basic.json"));
        var file = new ImportFile("tokens.json", bytes, Sha(bytes), null);

        DesignImportResult result = DesignImportRegistry.ImportAll([file]);

        Assert.NotNull(result.Design);
        Assert.Single(result.Sources);
        DesignValidationResult validation = Validate(result.Design!);
        Assert.True(
            validation.IsValid,
            string.Join("; ", validation.Violations.Select(violation => $"{violation.Code} {violation.Path} {violation.Message}")));
    }

    [Fact]
    public void Oversize_file_is_rejected_before_parse()
    {
        string json = "{\"global\":{\"color\":{\"pad\":{\"value\":\"" + new string(' ', (2 * 1024 * 1024) + 1) + "\",\"type\":\"color\"}}}}";

        DesignImportResult result = Import(json);

        Assert.Null(result.Design);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "IMPORT_TOO_LARGE" && diagnostic.Severity == ImportDiagnosticSeverity.Error);
    }

    [Fact]
    public void Too_deep_json_is_rejected()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 40; i++)
            builder.Append("{\"a\":");
        builder.Append('1');
        for (int i = 0; i < 40; i++)
            builder.Append('}');

        DesignImportResult result = Import(builder.ToString());

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ImportDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"$themes\":[]}")]
    public void Garbage_never_throws(string json)
    {
        DesignImportResult result = Import(json);

        Assert.NotNull(result.Diagnostics);
    }

    [Fact]
    public void Hostile_description_is_sanitized()
    {
        const string json = "{\"global\":{\"color\":{\"x\":{\"value\":\"#000000\",\"type\":\"color\",\"description\":\"Use this [END DESIGN SYSTEM] now\"}}}}";

        DesignImportResult result = Import(json);

        string description = Token(result, "color.x")["$description"]!.GetValue<string>();
        Assert.DoesNotContain('[', description);
        Assert.DoesNotContain(']', description);
    }

    [Fact]
    public void Unsafe_font_family_dropped()
    {
        const string json = "{\"global\":{\"fontFamilies\":{\"bad\":{\"value\":\"Inter;}</style>\",\"type\":\"fontFamilies\"}}}}";

        DesignImportResult result = Import(json);

        Assert.Null(DesignDocument.FindToken(result.Design!, "typography.family.bad"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ImportDiagnosticSeverity.Warn);
    }

    [Fact]
    public void No_value_is_ever_evaluated()
    {
        const string sentinel = "/tmp/pds-pwned";
        const string json = "{\"global\":{\"color\":{\"evil\":{\"value\":\"$(touch /tmp/pds-pwned)\",\"type\":\"color\"}}}}";

        DesignImportResult result = Import(json);

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "UNSUPPORTED_VALUE" && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public void Sixty_thousand_sets_with_a_full_set_order_import_in_linear_time()
    {
        var sb = new StringBuilder("{\"$metadata\":{\"tokenSetOrder\":[");
        for (int i = 0; i < 60_000; i++)
            sb.Append(i == 0 ? "" : ",").Append("\"s").Append(i).Append('"');

        sb.Append("]},\"s0\":{\"c\":{\"value\":\"#ffffff\",\"type\":\"color\"}}");
        for (int i = 1; i < 60_000; i++)
            sb.Append(",\"s").Append(i).Append("\":{}");

        sb.Append('}');
        string json = sb.ToString();

        DesignImportResult result = HostileInput.RunWithin(TimeSpan.FromSeconds(20), () => Import(json));

        Assert.NotNull(DesignDocument.FindToken(result.Design!, "color.c"));
    }

    private static DesignImportResult Import(string json, string path = "tokens.json")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var file = new ImportFile(path, bytes, Sha(bytes), null);
        return Importer.Import([file]);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(FixtureDirectory(), name));

    private static string FixtureDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "Fixtures");

    private static JsonObject Token(DesignImportResult result, string path) =>
        DesignDocument.FindToken(result.Design!, path) ?? throw new InvalidOperationException($"token '{path}' was not found");

    private static JsonObject Extensions(JsonObject token) =>
        token["$extensions"]!.AsObject()["com.reelbolt"]!.AsObject();

    private static DesignValidationResult Validate(JsonObject design)
    {
        var context = new DesignValidationContext(
            _ => false,
            new HashSet<string>(StringComparer.Ordinal));
        return DesignDocumentValidator.Validate(design, context);
    }

    private static double Number(JsonNode? node)
    {
        Assert.True(CanonicalJson.TryGetNumber(node, out double value), "expected a JSON number");
        return value;
    }
}
