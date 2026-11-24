using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// T-009: the axis-tagged "what this import dropped" report. The fixture is the trimmed,
/// sanitised Claude Design export; every byte is read as data and nothing is executed or fetched.
/// </summary>
public sealed class DesignImportReportTests
{
    [Fact]
    public void The_report_counts_every_axis_and_keeps_the_dropped_reasons()
    {
        ClaudeDesignBundle bundle = Loader.ImportFixture();

        DesignImportReport report = DesignImportReportBuilder.Build(
            "claude-design", "claude-design", bundle.Import,
            assets: bundle.Assets,
            references: bundle.ReferenceDocuments,
            skipped: [new ImportSkippedGroup("DROPPED_SCRIPT", 1)]);

        report.Axes.Should().HaveCount(10);
        report.Axes.Single(a => a.Axis == DesignImportAxis.Tokens).Imported.Should().BeGreaterThan(0);
        report.Axes.Single(a => a.Axis == DesignImportAxis.Assets).Imported.Should().Be(5);
        report.Axes.Single(a => a.Axis == DesignImportAxis.ReferenceDocuments).Imported.Should().BeGreaterThan(0);
        report.Axes.SelectMany(a => a.Reasons).Should().Contain(r => r.Code == "DROPPED_SCRIPT");
    }

    [Fact]
    public void No_dropped_reason_is_silent()
    {
        DesignImportReport report = Reports.NorthwindLike();

        report.Axes.SelectMany(a => a.Reasons).Select(r => r.Code)
            .Should().Contain(["UNSUPPORTED_VALUE", "MIX_UNRESOLVED", "GRADIENT_NOT_REPRESENTABLE",
                               "CLAUDE_DESIGN_FILE_SKIPPED", "DROPPED_SCRIPT"]);
    }

    // H2: an Info diagnostic that is a summary or a success note is not a drop. Before the fix every
    // axis-tagged Info inflated Dropped, so a clean import showed false drops on six axes.
    [Fact]
    public void A_clean_import_counts_no_summary_info_as_a_drop()
    {
        ClaudeDesignBundle bundle = Loader.ImportFixture();

        DesignImportReport report = DesignImportReportBuilder.Build(
            "claude-design", "claude-design", bundle.Import,
            assets: bundle.Assets,
            references: bundle.ReferenceDocuments);

        foreach (DesignImportAxis axis in new[]
                 {
                     DesignImportAxis.Assets, DesignImportAxis.Modes, DesignImportAxis.Components,
                     DesignImportAxis.PreviewCards, DesignImportAxis.ReferenceDocuments,
                 })
        {
            ImportAxisReport row = report.Axes.Single(a => a.Axis == axis);
            row.Dropped.Should().Be(0, $"{axis} reports only summary Info on a clean import");
            row.Reasons.Should().BeEmpty();
        }
    }

    [Fact]
    public void A_summary_info_diagnostic_never_counts_as_a_drop()
    {
        var result = new DesignImportResult(
            DesignDocument.New(), null, [],
            [
                Tagged(ImportDiagnosticSeverity.Info, "CLAUDE_DESIGN_ASSETS", DesignImportAxis.Assets),
                Tagged(ImportDiagnosticSeverity.Info, "SVG_SANITIZED", DesignImportAxis.Assets),
                Tagged(ImportDiagnosticSeverity.Info, "CLAUDE_DESIGN_MODES", DesignImportAxis.Modes),
                Tagged(ImportDiagnosticSeverity.Info, "CLAUDE_DESIGN_COMPONENTS", DesignImportAxis.Components),
                Tagged(ImportDiagnosticSeverity.Info, "PREVIEW_CARDS", DesignImportAxis.PreviewCards),
                Tagged(ImportDiagnosticSeverity.Info, "CLAUDE_DESIGN_REFERENCES", DesignImportAxis.ReferenceDocuments),
                Tagged(ImportDiagnosticSeverity.Info, "FONT_SOURCE_NOT_FETCHED", DesignImportAxis.Fonts),
            ]);

        DesignImportReport report = DesignImportReportBuilder.Build("claude-design", "claude-design", result);

        report.Axes.SelectMany(a => a.Reasons).Should().BeEmpty();
        report.Axes.Should().OnlyContain(a => a.Dropped == 0);
    }

    [Fact]
    public void A_known_loss_info_and_a_warning_still_count_as_drops()
    {
        var result = new DesignImportResult(
            DesignDocument.New(), null, [],
            [
                Tagged(ImportDiagnosticSeverity.Info, "CSS_TOKEN_UNMAPPED", DesignImportAxis.DerivedTokens),
                Tagged(ImportDiagnosticSeverity.Info, "CLAUDE_DESIGN_FILE_SKIPPED", DesignImportAxis.ReferenceDocuments),
                Tagged(ImportDiagnosticSeverity.Warn, "SVG_REFUSED", DesignImportAxis.Assets),
            ]);

        DesignImportReport report = DesignImportReportBuilder.Build("claude-design", "claude-design", result);

        report.Axes.Single(a => a.Axis == DesignImportAxis.DerivedTokens).Dropped.Should().Be(1);
        report.Axes.Single(a => a.Axis == DesignImportAxis.ReferenceDocuments).Dropped.Should().Be(1);
        report.Axes.Single(a => a.Axis == DesignImportAxis.Assets).Dropped.Should().Be(1);
        report.Axes.SelectMany(a => a.Reasons).Select(r => r.Code)
            .Should().Contain(["CSS_TOKEN_UNMAPPED", "CLAUDE_DESIGN_FILE_SKIPPED", "SVG_REFUSED"]);
    }

    [Fact]
    public void Real_drops_in_a_northwind_like_import_are_still_counted()
    {
        DesignImportReport report = Reports.NorthwindLike();

        report.Axes.Single(a => a.Axis == DesignImportAxis.Tokens).Dropped.Should().BeGreaterThan(0);
        report.Axes.Single(a => a.Axis == DesignImportAxis.DerivedTokens).Dropped.Should().BeGreaterThan(0);
        // The worker dropped a script and the reader also names the compiled bundle it could not read:
        // both are unread support material, so both land on the reference-document axis.
        ImportAxisReport references = report.Axes.Single(a => a.Axis == DesignImportAxis.ReferenceDocuments);
        references.Dropped.Should().BeGreaterThan(0);
        references.Reasons.Select(r => r.Code).Should().Contain("DROPPED_SCRIPT");
        report.Axes.SelectMany(a => a.Reasons).Select(r => r.Code).Should().Contain("CLAUDE_DESIGN_FILE_SKIPPED");
    }

    // M3: Imported[DerivedTokens] counts the resolved destinations in the document, never the
    // CSS_TOKEN_UNMAPPED drop diagnostic.
    [Fact]
    public void Derived_tokens_imported_counts_the_resolved_destinations_in_the_document()
    {
        ClaudeDesignBundle bundle = Loader.ImportFixture();
        JsonObject design = bundle.Import.Design!;

        int derived = DesignDocument.Tokens(design).Count(token =>
            token.Path.StartsWith("z.", System.StringComparison.Ordinal)
            || string.Equals(token.Type, "keyword", System.StringComparison.Ordinal)
            || (string.Equals(token.Type, "shadow", System.StringComparison.Ordinal)
                && token.Value is JsonArray array && array.Count == 0));

        derived.Should().BeGreaterThan(0);
        DesignDocument.FindToken(design, "z.drawer").Should().NotBeNull();
        DesignDocument.FindToken(design, "typography.transform.button")!["$type"]!.GetValue<string>()
            .Should().Be("keyword");
        DesignDocument.FindToken(design, "shadow.0")!["$value"]!.AsArray().Should().BeEmpty();

        DesignImportReport report = DesignImportReportBuilder.Build(
            "claude-design", "claude-design", bundle.Import,
            assets: bundle.Assets,
            references: bundle.ReferenceDocuments);

        report.Axes.Single(a => a.Axis == DesignImportAxis.DerivedTokens).Imported.Should().Be(derived);
    }

    private static ImportDiagnostic Tagged(
        ImportDiagnosticSeverity severity, string code, DesignImportAxis axis) =>
        new(severity, code, string.Empty, code) { Axis = axis };

    [Theory]
    [InlineData("DROPPED_SCRIPT", DesignImportAxis.ReferenceDocuments)]
    [InlineData("DROPPED_HTML", DesignImportAxis.ReferenceDocuments)]
    [InlineData("MARKUP_TOO_LARGE", DesignImportAxis.ReferenceDocuments)]
    [InlineData("COMPILED_BUNDLE", DesignImportAxis.ReferenceDocuments)]
    [InlineData("NOT_EXECUTED", DesignImportAxis.ReferenceDocuments)]
    [InlineData("SCREENSHOT", DesignImportAxis.ReferenceDocuments)]
    [InlineData("CLIENT_SCRATCH", DesignImportAxis.ReferenceDocuments)]
    [InlineData("DROPPED_IMAGE", DesignImportAxis.Assets)]
    [InlineData("IMAGE_NOT_ASSET_PATH", DesignImportAxis.Assets)]
    [InlineData("IMAGE_CAP", DesignImportAxis.Assets)]
    [InlineData("SVG_TOO_LARGE", DesignImportAxis.Assets)]
    [InlineData("FONT_CAP", DesignImportAxis.Fonts)]
    [InlineData("FONT_NOT_ASSET_PATH", DesignImportAxis.Fonts)]
    [InlineData("DOCUMENT_TOO_LARGE", DesignImportAxis.Tokens)]
    [InlineData("DROPPED_BINARY", DesignImportAxis.Tokens)]
    public void A_skip_reason_is_attributed_to_the_axis_that_lost_the_entry(
        string reason, DesignImportAxis expected) =>
        DesignImportReportBuilder.AxisForReason(reason).Should().Be(expected);

    /// <summary>
    /// A synthetic Northwind-like export: a manifest with three preview cards, a stylesheet with a
    /// colour mix and a gradient, and a compiled bundle the reader reports as skipped. No client
    /// archive is involved; the file names are generic.
    /// </summary>
    internal static class Reports
    {
        public static DesignImportReport NorthwindLike()
        {
            ClaudeDesignBundle bundle = new ClaudeDesignImporter().ImportBundle(
                [
                    File("_ds_manifest.json", """
                        {
                          "namespace": "NorthwindDesignSystem",
                          "components": [ { "name": "Button", "sourcePath": "components/core/Button.jsx" } ],
                          "globalCssPaths": [ "tokens/colors.css" ],
                          "themes": [],
                          "cards": [
                            { "path": "guidelines/colors-brand.html", "group": "Colors", "name": "Brand" },
                            { "path": "components/core/core.card.html", "group": "Components", "name": "Core" },
                            { "path": "components/overview/overview.card.html", "group": "Components", "name": "Overview" }
                          ]
                        }
                        """),
                    File("tokens/colors.css", """
                        :root{--dhr-primary:#7e6fff;
                        --dhr-primary-tint-4:color-mix(in srgb,#7e6fff 4%,var(--dhr-surface));
                        --dhr-gradient-primary:linear-gradient(82deg,#594ae2 0%,#7467ef 100%)}
                        """),
                    File("_ds_bundle.js", "console.log('never executed');"),
                    File("readme.md", "# Northwind Staffing\n\nBrand prose beyond the first paragraph."),
                ],
                []);

            return DesignImportReportBuilder.Build(
                "claude-design", "claude-design", bundle.Import,
                assets: bundle.Assets,
                references: bundle.ReferenceDocuments,
                skipped: [new ImportSkippedGroup("DROPPED_SCRIPT", 1)]);
        }

        private static ImportFile File(string path, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            return new ImportFile(path, bytes, ClaudeDesignImporterTests.Sha(bytes), null);
        }
    }
}
