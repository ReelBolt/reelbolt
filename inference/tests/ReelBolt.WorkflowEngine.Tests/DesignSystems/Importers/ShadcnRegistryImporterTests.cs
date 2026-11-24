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

public sealed class ShadcnRegistryImporterTests
{
    private static readonly ShadcnRegistryImporter Importer = new();

    [Fact]
    public void Theme_fixture_maps_colors_and_radius()
    {
        DesignImportResult result = Import(Fixture("shadcn-theme.json"));

        Assert.NotNull(result.Design);

        JsonObject background = Token(result, "color.background");
        Assert.Equal("#ffffff", background["$value"]!["hex"]!.GetValue<string>());

        JsonObject primary = Token(result, "color.primary");
        Assert.Matches("^#[0-9a-f]{6}$", primary["$value"]!["hex"]!.GetValue<string>());

        JsonObject? text = DesignDocument.FindToken(result.Design!, "color.text");
        JsonObject? foreground = DesignDocument.FindToken(result.Design!, "color.foreground");
        Assert.NotNull(text ?? foreground);
        Assert.Matches("^#[0-9a-f]{6}$", (text ?? foreground!)["$value"]!["hex"]!.GetValue<string>());

        JsonObject radius = Token(result, "radius.base");
        Assert.Equal(0.625, radius["$value"]!["value"]!.GetValue<double>());
        Assert.Equal("rem", radius["$value"]!["unit"]!.GetValue<string>());

        DesignTokenRef family = DesignDocument.Tokens(result.Design!).First(token => token.Type == "fontFamily");
        Assert.Equal("Inter", family.Value.AsArray()[0]!.GetValue<string>());

        JsonObject dark = result.Design!["modes"]!["dark"]!.AsObject();
        Assert.Matches("^#[0-9a-f]{6}$", dark["color.background"]!["hex"]!.GetValue<string>());
    }

    [Fact]
    public void All_tokens_carry_shadcn_importer_provenance()
    {
        DesignImportResult result = Import(Fixture("shadcn-theme.json"));

        Assert.NotNull(result.Design);
        int tokens = 0;
        foreach (DesignTokenRef token in DesignDocument.Tokens(result.Design!))
        {
            tokens++;
            JsonObject provenance = token.Token["$extensions"]!["com.reelbolt"]!["prov"]!.AsObject();
            Assert.Equal("imported", provenance["method"]!.GetValue<string>());
            Assert.Equal("shadcn-registry", provenance["importer"]!.GetValue<string>());
        }

        Assert.True(tokens > 0);
    }

    [Fact]
    public void Component_source_is_never_stored()
    {
        DesignImportResult result = Import(Fixture("shadcn-theme.json"));

        string serialized = result.Design!.ToJsonString();
        Assert.DoesNotContain("export const X", serialized);
        Assert.DoesNotContain("registry:component", serialized);

        foreach (ImportDiagnostic diagnostic in result.Diagnostics)
        {
            Assert.DoesNotContain("export const X", diagnostic.Message);
            Assert.DoesNotContain("registry:component", diagnostic.Message);
        }

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "DROPPED_CODE" && diagnostic.Severity == ImportDiagnosticSeverity.Info);
    }

    [Fact]
    public void Hostile_var_values_skipped()
    {
        const string json = "{\"$schema\":\"https://ui.shadcn.com/schema/registry-item.json\",\"type\":\"registry:theme\",\"cssVars\":{\"light\":{\"evil\":\"red; } body { background: url(http://evil) \",\"Bad Name\":\"#ffffff\"}}}";

        DesignImportResult result = Import(json);

        Assert.Null(DesignDocument.FindToken(result.Design!, "color.evil"));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "UNSUPPORTED_VALUE" && diagnostic.Message.Contains("url(http://evil)", StringComparison.Ordinal));

        ImportDiagnostic[] warnings = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == "UNSUPPORTED_VALUE" && diagnostic.Severity == ImportDiagnosticSeverity.Warn)
            .ToArray();
        Assert.True(warnings.Length >= 1);
        Assert.All(warnings, diagnostic => Assert.DoesNotContain("url(http://evil)", diagnostic.Message));
        Assert.Contains(warnings, diagnostic => diagnostic.Message.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    public void Registry_collection_with_items_array_is_handled()
    {
        const string json = "{\"$schema\":\"https://ui.shadcn.com/schema/registry.json\",\"items\":[{\"type\":\"registry:theme\",\"cssVars\":{\"light\":{\"background\":\"#101010\"}}},{\"type\":\"registry:component\",\"name\":\"x\",\"files\":[{\"path\":\"x.tsx\",\"content\":\"export default function Evil() {}\"}]}]}";

        DesignImportResult result = Import(json, "registry.json");

        Assert.NotNull(result.Design);
        Assert.Equal("#101010", Token(result, "color.background")["$value"]!["hex"]!.GetValue<string>());
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "DROPPED_CODE");
        Assert.DoesNotContain("Evil", result.Design!.ToJsonString());
    }

    [Fact]
    public void Detection_theory()
    {
        byte[] fixture = Encoding.UTF8.GetBytes(Fixture("shadcn-theme.json"));
        byte[] dtcg = Encoding.UTF8.GetBytes("{\"color\":{\"$type\":\"color\",\"$value\":\"#ffffff\"}}");
        byte[] random = Encoding.UTF8.GetBytes("{\"a\":1}");

        Assert.True(ShadcnRegistryImporter.LooksLikeRegistryItem("registry-item.json", fixture));
        Assert.True(ShadcnRegistryImporter.LooksLikeRegistryItem("registry.json", fixture));
        Assert.False(ShadcnRegistryImporter.LooksLikeRegistryItem("tokens.json", dtcg));
        Assert.False(ShadcnRegistryImporter.LooksLikeRegistryItem("x.json", random));
        Assert.False(ShadcnRegistryImporter.LooksLikeRegistryItem("x.txt", fixture));
    }

    [Fact]
    public void Dtcg_does_not_claim_registry_files()
    {
        byte[] head = Encoding.UTF8.GetBytes(Fixture("shadcn-theme.json"));

        Assert.False(new DtcgImporter().CanRead("registry-item.json", head));
    }

    [Fact]
    public void Oversize_rejected_before_parse()
    {
        string json = "{\"$schema\":\"https://ui.shadcn.com/schema/registry-item.json\",\"type\":\"registry:theme\",\"cssVars\":{\"light\":{\"background\":\""
            + new string(' ', (2 * 1024 * 1024) + 1)
            + "\"}}}";

        DesignImportResult result = Import(json);

        Assert.Null(result.Design);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "IMPORT_TOO_LARGE" && diagnostic.Severity == ImportDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"cssVars\":5}")]
    [InlineData("{\"cssVars\":{\"light\":[1]}}")]
    public void Garbage_never_throws(string json)
    {
        DesignImportResult result = Import(json);

        Assert.NotNull(result.Diagnostics);
    }

    private static DesignImportResult Import(string json, string path = "registry-item.json")
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
}
