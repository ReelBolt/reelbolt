using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class TailwindV3ConfigImporterTests
{
    private const string TypicalConfig = """
        module.exports = {
          theme: {
            colors: {
              transparent: 'transparent',
              white: '#ffffff',
              black: '#000000',
              brand: { DEFAULT: '#6d28d9', light: '#7c3aed' }
            },
            extend: {
              colors: { accent: '#f59e0b' },
              fontFamily: { sans: ['Inter', 'system-ui', 'sans-serif'] },
              fontSize: { sm: '0.875rem', base: ['1rem', { lineHeight: '1.5rem' }] },
              spacing: { 4: '1rem' },
              borderRadius: { lg: '0.5rem' },
              boxShadow: { md: '0 4px 6px -1px rgb(0 0 0 / 0.1)' },
              transitionDuration: { 150: '150ms' },
              transitionTimingFunction: { DEFAULT: 'cubic-bezier(0.4, 0, 0.2, 1)' },
              screens: { md: '768px' }
            }
          }
        }
        """;

    [Fact]
    public void Id_is_tailwind_v3()
    {
        Assert.Equal("tailwind-v3", new TailwindV3ConfigImporter().Id);
    }

    [Fact]
    public void CanRead_only_accepts_tailwind_config_paths_without_nul_bytes()
    {
        var importer = new TailwindV3ConfigImporter();

        Assert.True(importer.CanRead("tailwind.config.js", "module.exports"u8));
        Assert.True(importer.CanRead("app/tailwind.config.ts", "export default {}"u8));
        Assert.True(importer.CanRead("app\\tailwind.config.cjs", "module.exports"u8));
        Assert.False(importer.CanRead("app/globals.css", "--x: 1"u8));
        Assert.False(importer.CanRead("tailwind.config.js", new byte[] { 0x00 }));
        Assert.False(importer.CanRead("tailwind.config.js", ReadOnlySpan<byte>.Empty));
        Assert.False(importer.CanRead(string.Empty, "x"u8));
    }

    [Fact]
    public void Typical_config_maps_theme_and_extend()
    {
        DesignImportResult result = Import(TypicalConfig);
        JsonObject design = result.Design!;

        Assert.Equal("#00000000", Hex(design, "color.transparent"));
        Assert.Equal("#ffffff", Hex(design, "color.white"));
        Assert.Equal("#000000", Hex(design, "color.black"));
        Assert.Equal("#6d28d9", Hex(design, "color.brand"));
        Assert.Equal("#7c3aed", Hex(design, "color.brand.light"));
        Assert.Equal("#f59e0b", Hex(design, "color.accent"));

        Assert.Equal("[\"Inter\",\"system-ui\",\"sans-serif\"]", Value(design, "typography.family.sans")!.ToJsonString());
        Assert.Equal("{\"value\":0.875,\"unit\":\"rem\"}", Value(design, "typography.size.sm")!.ToJsonString());
        Assert.Equal("{\"value\":1,\"unit\":\"rem\"}", Value(design, "typography.size.base")!.ToJsonString());
        Assert.Equal("{\"value\":1.5,\"unit\":\"rem\"}", Value(design, "typography.lineHeight.base")!.ToJsonString());
        Assert.Equal("{\"value\":1,\"unit\":\"rem\"}", Value(design, "spacing.4")!.ToJsonString());
        Assert.Equal("{\"value\":0.5,\"unit\":\"rem\"}", Value(design, "radius.lg")!.ToJsonString());
        Assert.Equal("{\"value\":150,\"unit\":\"ms\"}", Value(design, "motion.duration.150")!.ToJsonString());
        Assert.Equal("[0.4,0,0.2,1]", Value(design, "motion.easing.default")!.ToJsonString());

        var shadow = Value(design, "shadow.md") as JsonObject;
        Assert.NotNull(shadow);
        var color = shadow!["color"] as JsonObject;
        Assert.NotNull(color);
        Assert.Matches("^#[0-9a-f]{8}$", color!["hex"]!.GetValue<string>());
        Assert.Equal(0d, shadow["offsetX"]!["value"]!.GetValue<double>());
        Assert.Equal(4d, shadow["offsetY"]!["value"]!.GetValue<double>());
        Assert.Equal(6d, shadow["blur"]!["value"]!.GetValue<double>());

        Assert.Null(DesignDocument.FindToken(design, "screens.md"));
        Assert.False(design.ContainsKey("screens"));
    }

    [Fact]
    public void Hostile_config_never_executes_and_is_reported()
    {
        DesignImportResult result = Import(
            "module.exports = { theme: { extend: { colors: { evil: require('child_process').execSync('id') } } } }");
        JsonObject design = result.Design!;

        Assert.Null(DesignDocument.FindToken(design, "color.evil"));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Severity == ImportDiagnosticSeverity.Warn
                && diagnostic.Code == "TAILWIND_DYNAMIC_VALUE");
    }

    [Fact]
    public void Oversized_config_is_refused_without_tokens()
    {
        var importer = new TailwindV3ConfigImporter();
        byte[] content = Encoding.UTF8.GetBytes(new string(' ', 10 * 1024 * 1024));
        var file = new ImportFile("tailwind.config.js", content, "sha", null);

        DesignImportResult result = importer.Import(new[] { file });

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Severity == ImportDiagnosticSeverity.Error
                && diagnostic.Code == "IMPORT_TOO_LARGE");
        Assert.False(result.Design!.ContainsKey("color"));
        Assert.Null(DesignDocument.FindToken(result.Design, "color.white"));
    }

    [Theory]
    [InlineData("export default { theme: { colors: { white: '#fff' } } }")]
    [InlineData("module.exports = { theme: { colors: { white: '#fff' } } }")]
    [InlineData("/** @type {import('tailwindcss').Config} */ export default { theme: { colors: { white: '#fff' } } } satisfies Config;")]
    [InlineData("export default defineConfig({ theme: { colors: { white: '#fff' } } })")]
    public void Esm_cjs_and_ts_variants_yield_white(string text)
    {
        DesignImportResult result = Import(text);

        Assert.Equal("#ffffff", Hex(result.Design!, "color.white"));
    }

    [Fact]
    public void Sixteen_thousand_keys_that_normalise_identically_import_quickly_with_unique_keys()
    {
        var sb = new StringBuilder("module.exports={theme:{colors:{");
        for (int i = 0; i < 16_000; i++)
            sb.Append("'a").Append((char)(0x0100 + (i / 128))).Append((char)(0x0100 + (i % 128))).Append("':'#fff',");

        sb.Append("}}}");
        string config = sb.ToString();

        DesignImportResult result = HostileInput.RunWithin(TimeSpan.FromSeconds(10), () => Import(config));

        var keys = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (DesignTokenRef token in DesignDocument.Tokens(result.Design!))
            Assert.True(keys.Add(token.Path), "duplicate path " + token.Path);

        Assert.True(keys.Count == 16_000, "count=" + keys.Count + " diag=" + string.Join(";", result.Diagnostics.Select(d => d.Code).Distinct()));
    }

    private static DesignImportResult Import(string content, string path = "tailwind.config.js")
    {
        var importer = new TailwindV3ConfigImporter();
        var file = new ImportFile(path, Encoding.UTF8.GetBytes(content), "sha", null);
        return importer.Import(new[] { file });
    }

    private static JsonNode? Value(JsonObject design, string path) =>
        DesignDocument.FindToken(design, path)?["$value"];

    private static string? Hex(JsonObject design, string path) =>
        (Value(design, path) as JsonObject)?["hex"]?.GetValue<string>();
}
