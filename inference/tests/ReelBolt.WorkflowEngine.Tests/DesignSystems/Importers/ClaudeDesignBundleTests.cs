using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.ImportWorker;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// P4-12 coverage for the <c>claude-design</c> vendor hint and the tolerant bundle import of a
/// Claude Design export: whatever the registered importers recognise (CSS variables, DTCG JSON,
/// DESIGN.md, tailwind config) is extracted wherever it sits in the archive and merged, while HTML
/// is never parsed. The three fixtures are synthetic, written from the documented structure only;
/// the layout of a real export is unverified (see handover/BLOCKED-claude-design.md).
/// </summary>
public sealed class ClaudeDesignBundleTests
{
    private static readonly BundleImporter Importer = new();

    [Theory]
    [InlineData("claude/tokens.json")]
    [InlineData("claude-design/index.html")]
    [InlineData("design-system/tokens.json")]
    [InlineData("my-design-system/styles/tokens.css")]
    public void A_top_level_claude_or_design_system_name_labels_the_archive(string path)
    {
        BundleImporter.DetectVendor([path], null).Should().Be(BundleVendors.ClaudeDesign);
    }

    [Fact]
    public void Design_md_beside_index_html_labels_the_archive()
    {
        BundleImporter.DetectVendor(["DESIGN.md", "index.html"], null)
            .Should().Be(BundleVendors.ClaudeDesign);
    }

    [Fact]
    public void Fixture_a_readme_design_md_css_and_index_html_yields_tokens_and_the_vendor()
    {
        ImportFile readme = File("README.md", "# Acme design system\n\nA brand for developers.\n");
        ImportFile design = File("DESIGN.md", "---\nname: Acme\ntypography:\n  heading:\n    fontFamily: Inter\n---\n## Overview\nA clean brand for developers.\n");
        ImportFile css = File("styles/tokens.css", ":root { --color-brand-primary: #6d28d9; --radius: 0.625rem; }");
        ImportFile html = File("index.html", "<html><body><h1>Acme</h1></body></html>");

        BundleImportResult result = Importer.ImportBundle([readme, design, css, html]);

        result.Vendor.Should().Be(BundleVendors.ClaudeDesign);
        result.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "typography.heading").Should().NotBeNull();

        // index.html drove the vendor hint; it is inert markup no vendor reader consumed.
        result.Import.Sources.Select(source => source.Path).Should().Contain("index.html");
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == BundleImporter.MarkupNotReadDiagnostic && diagnostic.Path == "index.html");
        JsonSerializer.Serialize(result.Import).Should().NotContain("<html>");
    }

    [Fact]
    public void Fixture_b_design_system_dir_with_dtcg_and_component_html_yields_dtcg_tokens()
    {
        ImportFile tokens = File(
            "design-system/tokens.json",
            "{ \"color\": { \"background\": { \"$type\": \"color\", \"$value\": { \"hex\": \"#0b0b0f\", \"colorSpace\": \"srgb\" } } } }");
        ImportFile component = File("components/button.html", "<button class=\"btn\">Go</button>");

        BundleImportResult result = Importer.ImportBundle([tokens, component]);

        result.Vendor.Should().Be(BundleVendors.ClaudeDesign);
        DesignDocument.FindToken(result.Import.Design!, "color.background").Should().NotBeNull();
        result.Import.Sources.Select(source => source.Path).Should().Contain("design-system/tokens.json");
        // Markup is admitted as inert text but no vendor reader consumes it.
        result.Import.Sources.Select(source => source.Path).Should().Contain("components/button.html");
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == BundleImporter.MarkupNotReadDiagnostic && diagnostic.Path == "components/button.html");
        JsonSerializer.Serialize(result.Import).Should().NotContain("<button");
    }

    [Fact]
    // Rewritten (QA-6): this used to assert the tailwind config was parsed and merged. In production the worker
    // drops every script, so a zip's tailwind.config.* never reached the parser and the test only passed because it
    // hand-built the ImportFile list. The bundle now says so (see BundleWorkerEndToEndTests for the real pipeline).
    public void Fixture_c_src_tsx_shadcn_css_and_tailwind_config_yields_css_tokens_and_a_not_read_notice()
    {
        ImportFile tsx = File(
            "src/components/Button.tsx",
            "export const Button = () => <button className=\"bg-brand-primary\" />;");
        ImportFile css = File("src/index.css", ":root { --color-brand-primary: #6d28d9; --radius: 0.625rem; }");
        ImportFile tailwind = File(
            "tailwind.config.js",
            "module.exports = { theme: { extend: { colors: { accent: '#ff0000' }, spacing: { gutter: '1rem' } } } };");

        BundleImportResult result = Importer.ImportBundle([tsx, css, tailwind]);

        result.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.accent").Should().BeNull();
        DesignDocument.FindToken(result.Import.Design!, "spacing.gutter").Should().BeNull();

        result.Import.Sources.Select(source => source.Path).Should().NotContain("tailwind.config.js");
        result.Import.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == BundleImporter.TailwindConfigNotReadDiagnostic && diagnostic.Path == "tailwind.config.js");
        result.Import.Sources.Select(source => source.Path).Should().NotContain("src/components/Button.tsx");
    }

    [Fact]
    public void Script_shaped_names_including_tailwind_config_are_refused()
    {
        ImportFile script = File(
            "src/setup.js",
            "const cp = require('child_process'); cp.execSync('touch /tmp/pwned');");

        BundleImporter.IsAllowedTextFile("src/setup.js").Should().BeFalse();
        BundleImporter.IsAllowedTextFile("tailwind.config.js").Should().BeFalse();

        BundleImportResult result = Importer.ImportBundle([script]);

        result.Import.Sources.Should().BeEmpty();
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == BundleImporter.DroppedDiagnostic && diagnostic.Path == "src/setup.js");
    }

    [Fact]
    public void An_archive_of_only_images_is_nothing_recognized()
    {
        ImportFile png = new(
            "logo.png",
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D },
            string.Empty,
            null);

        BundleImportResult result = Importer.ImportBundle([png]);

        result.Import.Design.Should().BeNull();
        result.Import.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == BundleImporter.NothingRecognized);
        result.Vendor.Should().BeNull();
    }

    // ------------------------------------------------------------------ T-005: dispatch

    [Fact]
    public void A_real_claude_design_layout_is_labelled_claude_design_not_v0()
    {
        BundleImporter.DetectVendor(
                ["_ds_manifest.json", "SKILL.md", "tokens/colors.css", "assets/logo.png"], null)
            .Should().Be(BundleVendors.ClaudeDesign);
    }

    [Fact]
    public async Task The_fixture_through_the_real_worker_pipeline_uses_the_complete_reader()
    {
        BundleImportResult bundle = await ImportFixtureZipAsync();

        bundle.Vendor.Should().Be(BundleVendors.ClaudeDesign);
        bundle.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(bundle.Import.Design!, "color.background").Should().NotBeNull();
        DesignDocument.FindToken(bundle.Import.Design!, "spacing.type.h3.size").Should().BeNull(
            "the complete reader owns the archive; the generic CSS reader must not also run");
        bundle.Import.ProductMap.Should().NotBeNull();
        bundle.Import.ProductMap!.Components.Should().HaveCount(26);
        bundle.Assets.Should().HaveCount(5);
        bundle.Import.Diagnostics.Should().Contain(d => d.Code == ClaudeDesignImporter.ManifestDiagnostic);
    }

    [Fact]
    public void A_broken_manifest_falls_back_to_the_tolerant_path()
    {
        ImportFile manifest = File("_ds_manifest.json", "{ not json");
        ImportFile css = File("tokens/colors.css", ":root{--color-brand-primary:#7e6fff}");

        BundleImportResult bundle = Importer.ImportBundle([manifest, css], []);

        bundle.Vendor.Should().Be(BundleVendors.ClaudeDesign);
        DesignDocument.FindToken(bundle.Import.Design!, "color.brand.primary").Should().NotBeNull();
        bundle.Import.Diagnostics.Should().Contain(d => d.Code == ClaudeDesignImporter.ManifestUnreadableDiagnostic);
        bundle.Assets.Should().BeEmpty();
    }

    [Theory]
    [InlineData("components/core/Button.d.ts", true)]
    [InlineData("components/core/Button.ts", false)]
    [InlineData("components/core/Button.jsx", false)]
    [InlineData("components/core/Button.html", true)]
    [InlineData("components/core/Button.htm", true)]
    [InlineData("components/core/Button.xhtml", true)]
    // T-003: an SVG is now text data the Claude reader sanitizes; the other dialects report it
    // as BUNDLE_MARKUP_NOT_READ rather than refuse the entry.
    [InlineData("assets/logo.svg", true)]
    public void Declaration_files_and_svg_are_text_while_other_scripts_and_markup_are_refused(
        string path, bool expected)
    {
        BundleImporter.IsAllowedTextFile(path).Should().Be(expected);
    }

    [Fact]
    public void An_svg_only_asset_list_still_imports_the_design_and_reports_the_assets()
    {
        byte[] svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");

        BundleImportResult bundle = Importer.ImportBundle(
            Loader.Files(),
            [new ImportedAsset("", null, "logo.svg", "assets/logo.svg", "image/svg+xml",
                ClaudeDesignImporterTests.Sha(svg), svg)]);

        bundle.Assets.Should().BeEmpty();
        bundle.Import.Design.Should().NotBeNull();
        bundle.Import.Diagnostics.Should().Contain(d => d.Code == ClaudeDesignImporter.AssetsDiagnostic);
    }

    /// <summary>The fixture as a real zip, through <see cref="ExtractZipHandler"/> and the controller's mapping.</summary>
    private static async Task<BundleImportResult> ImportFixtureZipAsync()
    {
        var entries = new List<(string Name, byte[] Data)>();
        foreach (ImportFile file in Loader.Files())
            entries.Add((file.Path, file.Content.ToArray()));
        foreach (ImportedAsset asset in Loader.Assets())
            entries.Add((asset.Path, asset.Content.ToArray()));

        byte[] zip = Zip([.. entries]);

        WorkerResult result = await ExtractZipHandler.HandleAsync(
            new MemoryStream(zip), zip.Length, new WorkerOptions("t"), CancellationToken.None);
        result.Status.Should().Be(200);
        ExtractZipResponse worker = (ExtractZipResponse)result.Body!;

        List<ImportFile> files = new();
        List<ImportedAsset> assets = new();
        foreach (WorkerFileDto entry in worker.Files)
        {
            if (entry.Text is not null && entry.Kind is "json" or "css" or "text")
                files.Add(new ImportFile(entry.Path, Encoding.UTF8.GetBytes(entry.Text), entry.Sha256, null));

            if (entry.Base64 is not null)
            {
                byte[] bytes = Convert.FromBase64String(entry.Base64);
                assets.Add(new ImportedAsset(
                    string.Empty, null, Path.GetFileName(entry.Path), entry.Path, "image/png", entry.Sha256, bytes));
            }
        }

        return new BundleImporter().ImportBundle(files, assets);
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using Stream entryStream = entry.Open();
                entryStream.Write(data, 0, data.Length);
            }
        }

        return stream.ToArray();
    }

    private static ImportFile File(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content), string.Empty, null);
}
