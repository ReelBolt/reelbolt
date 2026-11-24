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

public sealed class DtcgImporterTests
{
    private static readonly DtcgImporter Importer = new();

    [Fact]
    public void Dtcg_Colour_Object_Normalizes_To_Canonical_Hex()
    {
        DesignImportResult result = Import(Fixture("dtcg-2025.10.json"));

        JsonObject? token = DesignDocument.FindToken(result.Design!, "color.brand.primary");
        Assert.NotNull(token);
        Assert.Equal("color", token!["$type"]!.GetValue<string>());
        Assert.Equal("#1a73e8", token["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal("srgb", token["$value"]!["colorSpace"]!.GetValue<string>());
        AssertValid(result.Design!);
    }

    [Fact]
    public void Dtcg_Dimension_And_String_Colour_Normalize()
    {
        DesignImportResult result = Import(Fixture("dtcg-2025.10.json"));

        JsonObject? spacing = DesignDocument.FindToken(result.Design!, "spacing.md");
        Assert.NotNull(spacing);
        Assert.Equal(16, Number(spacing!["$value"]!["value"]));
        Assert.Equal("px", spacing["$value"]!["unit"]!.GetValue<string>());

        JsonObject? surface = DesignDocument.FindToken(result.Design!, "color.brand.surface");
        Assert.NotNull(surface);
        Assert.Equal("#ffffff", surface!["$value"]!["hex"]!.GetValue<string>());
    }

    [Fact]
    public void Legacy_Style_Dictionary_Dialect_Is_Read()
    {
        DesignImportResult result = Import(Fixture("style-dictionary-legacy.json"));

        JsonObject? accent = DesignDocument.FindToken(result.Design!, "color.accent");
        Assert.NotNull(accent);
        Assert.Equal("#ff5722", accent!["$value"]!["hex"]!.GetValue<string>());

        JsonObject? radius = DesignDocument.FindToken(result.Design!, "radius.card");
        Assert.NotNull(radius);
        Assert.Equal(12, Number(radius!["$value"]!["value"]));
        AssertValid(result.Design!);
    }

    [Fact]
    public void Group_Type_Is_Inherited_By_Children()
    {
        DesignImportResult result = Import(Fixture("nested-groups-type.json"));

        JsonObject? baseToken = DesignDocument.FindToken(result.Design!, "color.surface.base");
        JsonObject? raisedToken = DesignDocument.FindToken(result.Design!, "color.surface.raised");
        Assert.NotNull(baseToken);
        Assert.NotNull(raisedToken);
        Assert.Equal("color", baseToken!["$type"]!.GetValue<string>());
        Assert.Equal("color", raisedToken!["$type"]!.GetValue<string>());
        Assert.Equal("#eeeeee", baseToken["$value"]!["hex"]!.GetValue<string>());
        AssertValid(result.Design!);
    }

    [Fact]
    public void Alias_Chain_Is_Preserved_Verbatim()
    {
        DesignImportResult result = Import(Fixture("alias-chain.json"));

        JsonObject? primary = DesignDocument.FindToken(result.Design!, "color.brand.primary");
        Assert.NotNull(primary);
        Assert.Equal("{color.brand.secondary}", primary!["$value"]!.GetValue<string>());

        DesignValidationResult validation = Validate(result.Design!);
        Assert.DoesNotContain(validation.Violations, violation => violation.Code == "DS_V12");
    }

    [Fact]
    public void Alias_Cycle_Is_Still_Emitted_And_Validator_Flags_It()
    {
        DesignImportResult result = Import(Fixture("alias-cycle.json"));

        Assert.NotNull(DesignDocument.FindToken(result.Design!, "color.a"));
        Assert.NotNull(DesignDocument.FindToken(result.Design!, "color.b"));

        DesignValidationResult validation = Validate(result.Design!);
        Assert.Contains(validation.Violations, violation => violation.Code == "DS_V12");
    }

    [Fact]
    public void Unknown_Type_Is_Dropped_With_Warning()
    {
        DesignImportResult result = Import(Fixture("unknown-type.json"));

        Assert.NotNull(DesignDocument.FindToken(result.Design!, "color.valid"));
        Assert.Null(DesignDocument.FindToken(result.Design!, "color.weird"));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "UNKNOWN_TYPE" && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
    }

    [Fact]
    public void Resolution_Order_Warns_But_Keeps_Other_Tokens()
    {
        const string json = """
            {
              "resolutionOrder": [ { "$ref": "#/color/base" } ],
              "color": {
                "$type": "color",
                "base": { "$value": "#000000" }
              }
            }
            """;

        DesignImportResult result = Import(json);

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "RESOLVER_UNSUPPORTED" && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
        Assert.NotNull(DesignDocument.FindToken(result.Design!, "color.base"));
    }

    [Fact]
    public void Nesting_Depth_40_Is_An_Error_Not_An_Exception()
    {
        DesignImportResult result = Import(Fixture("depth-40.json"));

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "TOO_DEEP" && diagnostic.Severity == ImportDiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "JSON_INVALID");
    }

    [Fact]
    public void File_Over_Two_MiB_Is_Import_Too_Large()
    {
        string huge = "{\"color\":{\"pad\":{\"$value\":\"" + new string('a', 3 * 1024 * 1024) + "\",\"$type\":\"color\"}}}";

        DesignImportResult result = Import(huge);

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "IMPORT_TOO_LARGE" && diagnostic.Severity == ImportDiagnosticSeverity.Error);
    }

    [Fact]
    public void Source_Digest_Is_Recomputed_From_The_Bytes()
    {
        var file = new ImportFile("tokens.json", Encoding.UTF8.GetBytes("{}"), "deadbeef", null);

        DesignImportResult result = Importer.Import([file]);

        ImportSourceRef source = Assert.Single(result.Sources);
        Assert.Equal(64, source.Sha256.Length);
        Assert.NotEqual("deadbeef", source.Sha256);
        Assert.Equal(source.Sha256.ToLowerInvariant(), source.Sha256);
    }

    [Fact]
    public void Product_Map_Is_Always_Null()
    {
        DesignImportResult result = Import(Fixture("dtcg-2025.10.json"));

        Assert.Null(result.ProductMap);
    }

    [Fact]
    public void CanRead_Accepts_Dtcg_And_Legacy_Json()
    {
        byte[] dtcg = Encoding.UTF8.GetBytes("{\"color\":{\"$type\":\"color\",\"a\":{\"$value\":\"#000\"}}}");
        byte[] legacy = Encoding.UTF8.GetBytes("{\"color\":{\"a\":{\"value\":\"#000\",\"type\":\"color\"}}}");

        Assert.True(Importer.CanRead("tokens.json", dtcg));
        Assert.True(Importer.CanRead("tokens.tokens.json", legacy));
    }

    [Fact]
    public void CanRead_Rejects_Css_And_Plain_Json()
    {
        byte[] plain = Encoding.UTF8.GetBytes("{\"name\":\"not tokens\"}");

        Assert.False(Importer.CanRead("tokens.css", plain));
        Assert.False(Importer.CanRead("tokens.json", plain));
    }

    private static DesignImportResult Import(string json)
    {
        var file = new ImportFile("tokens.tokens.json", Encoding.UTF8.GetBytes(json), string.Empty, null);
        return Importer.Import([file]);
    }

    [Fact]
    public void Import_stops_at_the_1500_token_cap_with_a_token_cap_diagnostic()
    {
        var sb = new StringBuilder("{\"color\":{\"$type\":\"color\"");
        for (int i = 0; i < 4000; i++)
            sb.Append(",\"c").Append(i).Append("\":{\"$value\":\"#ffffff\"}");

        sb.Append("}}");

        DesignImportResult result = HostileInput.RunWithin(TimeSpan.FromSeconds(10), () => Import(sb.ToString()));

        int count = DesignDocument.Tokens(result.Design!).Count();
        Assert.True(count == 1500, "tokens: " + count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TOKEN_CAP");
    }

    [Fact]
    public void Document_over_the_node_cap_is_import_too_large_not_walked()
    {
        // 210k leaf nodes in a flat array of a group: over JsonMaxNodes (200k) yet far below the 2 MiB byte cap.
        var sb = new StringBuilder("{\"color\":{\"a\":{\"$value\":\"#ffffff\",\"$type\":\"color\"},\"junk\":[");
        for (int i = 0; i < 210_000; i++)
            sb.Append(i == 0 ? "1" : ",1");

        sb.Append("]}}");
        Assert.True(sb.Length < ImportLimits.JsonMaxBytes);

        DesignImportResult result = HostileInput.RunWithin(TimeSpan.FromSeconds(10), () => Import(sb.ToString()));

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "IMPORT_TOO_LARGE" && diagnostic.Severity == ImportDiagnosticSeverity.Error);
        Assert.Null(DesignDocument.FindToken(result.Design!, "color.a"));
    }

    // PDS_T008_CHILDREN lets the red run use a small N so the unfixed code does not exhaust memory.
    private static int AmplificationChildren =>
        int.TryParse(Environment.GetEnvironmentVariable("PDS_T008_CHILDREN"), out int n) ? n : 35_000;

    [Fact]
    public void Huge_inherited_type_is_not_echoed_per_token_and_diagnostics_are_capped()
    {
        int children = AmplificationChildren;
        var sb = new StringBuilder(2 * 1024 * 1024);
        sb.Append("{\"seed\":{\"$value\":\"#ffffff\",\"$type\":\"color\"},\"$type\":\"");
        sb.Append('T', 1024 * 1024);
        sb.Append('"');
        for (int i = 0; i < children; i++)
            sb.Append(",\"t").Append(i).Append("\":{\"$value\":1}");

        sb.Append('}');
        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        Assert.True(bytes.Length < ImportLimits.JsonMaxBytes, "fixture must stay under the 2 MiB import limit: " + bytes.Length);
        var file = new ImportFile("tokens.json", bytes, "sha", null);

        long allocated = 0;
        DesignImportResult result = HostileInput.RunWithin(TimeSpan.FromSeconds(5), () =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            DesignImportResult r = DesignImportRegistry.ImportAll(new[] { file });
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return r;
        });

        Assert.True(allocated < 50L * 1024 * 1024, "allocated " + allocated + " bytes");
        Assert.True(result.Diagnostics.Count <= 201, "diagnostics: " + result.Diagnostics.Count);
        Assert.All(result.Diagnostics, diagnostic => Assert.True(diagnostic.Message.Length <= 512, "message length " + diagnostic.Message.Length));
        if (children > 400)
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DIAGNOSTICS_TRUNCATED");
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
