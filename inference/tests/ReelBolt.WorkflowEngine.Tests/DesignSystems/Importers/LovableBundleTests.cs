using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// P4-14 coverage for the <c>lovable</c> vendor hint: the marker <c>.lovable/design-system.json</c>
/// labels the archive and is walked by the shared <see cref="TolerantTokenWalker"/>, while
/// <c>src/index.css</c> and <c>tailwind.config.ts</c> in the same archive flow through the existing
/// css-theme / tailwind-v3 importers. The synthetic fixtures are written from the documented
/// contract only; no real Lovable export was available, so the walker is the stand-in (see
/// handover/BLOCKED-lovable.md).
/// </summary>
public sealed class LovableBundleTests
{
    private static readonly BundleImporter Importer = new();

    [Theory]
    [InlineData(".lovable/design-system.json")]
    [InlineData("app/.lovable/design-system.json")]
    [InlineData(".lovable\\design-system.json")]
    public void The_lovable_marker_labels_the_archive(string path)
    {
        BundleImporter.DetectVendor([path], null).Should().Be(BundleVendors.Lovable);
    }

    [Fact]
    public void Fixture_design_system_json_walker_maps_colors_fonts_and_dimensions()
    {
        const string json = """
            {
              "theme": {
                "colors": { "primary": "#6d28d9", "surface": { "hex": "#ffffff" }, "note": "not a colour" },
                "fontFamily": { "sans": "Inter, system-ui", "mono": ["JetBrains Mono", "monospace"] },
                "radius": { "sm": "0.25rem" },
                "spacing": { "4": "1rem" }
              }
            }
            """;
        ImportFile marker = File(".lovable/design-system.json", json);

        BundleImportResult result = Importer.ImportBundle([marker]);

        result.Vendor.Should().Be(BundleVendors.Lovable);
        result.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.surface").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "typography.family.sans").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "typography.family.mono").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "radius.sm").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "spacing.4").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.note").Should().BeNull();

        result.Import.Sources.Should().Contain(source =>
            source.Path == ".lovable/design-system.json"
            && source.Importer == TolerantTokenWalker.ImporterId);
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == TolerantTokenWalker.SkippedDiagnostic
            && diagnostic.Severity == ImportDiagnosticSeverity.Info);
    }

    [Fact]
    public void Fixture_src_index_css_shadcn_vars_flow_through_css_theme()
    {
        ImportFile marker = File(".lovable/design-system.json", """{ "colors": { "brand": "#6d28d9" } }""");
        ImportFile css = File(
            "src/index.css",
            ":root { --background: #ffffff; --foreground: #0a0a0a; --primary: #6d28d9; --radius: 0.625rem; }");

        BundleImportResult result = Importer.ImportBundle([marker, css]);

        result.Vendor.Should().Be(BundleVendors.Lovable);
        DesignDocument.FindToken(result.Import.Design!, "color.background").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "radius.base").Should().NotBeNull();
        result.Import.Sources.Should().Contain(source =>
            source.Path == "src/index.css" && source.Importer == "css-theme");
    }

    [Fact]
    // Rewritten (QA-6): the config used to be asserted as parsed and merged; the worker never delivers a script,
    // so that only held for a hand-built ImportFile list. It is now reported as seen-and-not-read.
    public void Fixture_tailwind_config_ts_is_reported_as_not_read_and_contributes_no_tokens()
    {
        ImportFile marker = File(".lovable/design-system.json", """{ "colors": { "brand": "#6d28d9" } }""");
        ImportFile tailwind = File(
            "tailwind.config.ts",
            "export default { theme: { extend: { colors: { accent: '#ff0000' } } } };");

        BundleImporter.IsAllowedTextFile("tailwind.config.ts").Should().BeFalse();

        BundleImportResult result = Importer.ImportBundle([marker, tailwind]);

        DesignDocument.FindToken(result.Import.Design!, "color.accent").Should().BeNull();
        result.Import.Sources.Select(source => source.Path).Should().NotContain("tailwind.config.ts");
        result.Import.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == BundleImporter.TailwindConfigNotReadDiagnostic && diagnostic.Path == "tailwind.config.ts");
    }

    [Fact]
    public void Unknown_marker_yields_nothing_recognized()
    {
        ImportFile marker = File(".lovable/design-system.json", """{ "name": "acme" }""");

        BundleImportResult result = Importer.ImportBundle([marker]);

        result.Vendor.Should().Be(BundleVendors.Lovable);
        result.Import.Design.Should().BeNull();
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == BundleImporter.NothingRecognized
            && diagnostic.Severity == ImportDiagnosticSeverity.Error);
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == TolerantTokenWalker.NothingRecognizedDiagnostic
            && diagnostic.Severity == ImportDiagnosticSeverity.Info);
    }

    [Fact]
    public void Invalid_marker_json_warns_and_yields_nothing_recognized()
    {
        ImportFile marker = File(".lovable/design-system.json", "{ not json");

        BundleImportResult result = Importer.ImportBundle([marker]);

        result.Vendor.Should().Be(BundleVendors.Lovable);
        result.Import.Design.Should().BeNull();
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == TolerantTokenWalker.InvalidJsonDiagnostic
            && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == BundleImporter.NothingRecognized
            && diagnostic.Severity == ImportDiagnosticSeverity.Error);
    }

    private static ImportFile File(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content), string.Empty, null);
}
