using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// P4-11 coverage for <see cref="BundleImporter"/>: the vendor label read from an archive's layout,
/// the run of every registered importer over the allowed text files, the merge, the provenance
/// source per file, and the refusal of scripts/markup and of a <c>package.json</c>'s non-dependency
/// fields.
/// </summary>
public sealed class BundleImporterTests
{
    private static readonly BundleImporter Importer = new();

    [Fact]
    public void CanRead_any_bundle_and_Import_matches_ImportBundle()
    {
        ImportFile css = File("app.css", ":root { --color-brand-primary: #6d28d9; }");

        Importer.CanRead("anything.zip", new byte[] { 0x50, 0x4B, 0x03, 0x04 }).Should().BeTrue();

        DesignImportResult viaImport = Importer.Import([css]);
        BundleImportResult viaBundle = Importer.ImportBundle([css]);

        DesignDocument.FindToken(viaImport.Design!, "color.brand.primary").Should().NotBeNull();
        viaImport.Design!.ToJsonString().Should().Be(viaBundle.Import.Design!.ToJsonString());
        viaBundle.Vendor.Should().BeNull();
    }

    [Fact]
    public void Runs_every_registered_importer_and_merges_their_documents()
    {
        ImportFile css = File("app/globals.css", ":root { --color-brand-primary: #6d28d9; --radius: 0.625rem; }");
        ImportFile dtcg = File("tokens.json", "{ \"color\": { \"background\": { \"$type\": \"color\", \"$value\": { \"hex\": \"#0b0b0f\", \"colorSpace\": \"srgb\" } } } }");
        ImportFile md = File("DESIGN.md", "---\nname: Acme\ntypography:\n  heading:\n    fontFamily: Inter\n---\n## Overview\nA clean brand for developers.\n");

        BundleImportResult result = Importer.ImportBundle([css, dtcg, md]);

        result.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.background").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "typography.heading").Should().NotBeNull();

        result.Import.Sources.Select(source => source.Path)
            .Should().BeEquivalentTo(new[] { "app/globals.css", "tokens.json", "DESIGN.md" });
        result.Import.Sources.Select(source => source.Importer)
            .Should().OnlyContain(importer => importer == "css-theme" || importer == "dtcg" || importer == "design-md");
    }

    [Theory]
    [InlineData(".lovable/design-system.json", null, BundleVendors.Lovable)]
    [InlineData("v0.json", null, BundleVendors.V0)]
    [InlineData("SKILL.md", null, BundleVendors.V0)]
    [InlineData("claude/tokens.json", null, BundleVendors.ClaudeDesign)]
    [InlineData("design-system/index.html", null, BundleVendors.ClaudeDesign)]
    [InlineData("DESIGN.md", null, BundleVendors.ClaudeDesign)]
    [InlineData("app.penpot", null, BundleVendors.Penpot)]
    [InlineData("theme.penpot.zip.json", null, BundleVendors.Penpot)]
    [InlineData("tokens.json", null, null)]
    public void Vendor_label_comes_from_the_layout(string path, string? packageJson, string? expected)
    {
        List<string> paths = path == "DESIGN.md" ? ["DESIGN.md", "index.html"] : [path];
        BundleImporter.DetectVendor(paths, packageJson).Should().Be(expected);
    }

    [Fact]
    public void Sketch_layout_needs_document_meta_and_pages()
    {
        BundleImporter.DetectVendor(["document.json", "meta.json", "pages/Page-1.json"], null)
            .Should().Be(BundleVendors.Sketch);

        BundleImporter.DetectVendor(["document.json", "pages/Page-1.json"], null)
            .Should().BeNull();
    }

    [Fact]
    public void Claude_layout_can_be_signalled_by_a_dependency_name()
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal) { "@anthropic-ai/claude-code" };

        BundleImporter.DetectVendor(["tokens.json"], null, dependencies)
            .Should().Be(BundleVendors.ClaudeDesign);
    }

    [Fact]
    public void Package_json_dependencies_are_read_but_scripts_are_never()
    {
        const string packageJson = """
            {
              "name": "acme-design",
              "scripts": { "postinstall": "node -e \"require('child_process').execSync('touch /tmp/pwned')\"" },
              "dependencies": { "react": "19.0.0", "@acme/tokens": "1.2.3" },
              "devDependencies": { "typescript": "5.9.0" }
            }
            """;
        ImportFile css = File("app.css", ":root { --color-brand-primary: #6d28d9; }");
        ImportFile package = File("package.json", packageJson);

        BundleImportResult result = Importer.ImportBundle([css, package]);

        result.Dependencies.Should().BeEquivalentTo(new[] { "react", "@acme/tokens", "typescript" });
        result.Dependencies.Should().NotContain("postinstall");
        result.Import.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == BundleImporter.PackageDiagnostic);
        result.Import.Sources.Should().Contain(source => source.Path == "package.json" && source.Importer == BundleImporter.ImporterId);

        string serialized = JsonSerializer.Serialize(result.Import);
        serialized.Should().NotContain("child_process");
        serialized.Should().NotContain("execSync");
        serialized.Should().NotContain("postinstall");
    }

    [Fact]
    public void Scripts_are_refused_and_markup_is_admitted_but_reported_not_read()
    {
        const string evil = "const cp = require('child_process'); cp.execSync('touch /tmp/pwned');";
        ImportFile script = File("evil.js", evil);
        ImportFile markup = File("x.html", "<html><body>hi</body></html>");
        ImportFile css = File("app.css", ":root { --color-brand-primary: #6d28d9; }");

        BundleImportResult result = Importer.ImportBundle([script, markup, css]);

        result.Import.Design.Should().NotBeNull();
        result.Import.Sources.Select(source => source.Path)
            .Should().BeEquivalentTo(new[] { "app.css", "x.html" });
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == BundleImporter.DroppedDiagnostic && diagnostic.Path == "evil.js");
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == BundleImporter.MarkupNotReadDiagnostic && diagnostic.Path == "x.html");
        result.Import.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Code == BundleImporter.DroppedDiagnostic && diagnostic.Path == "x.html");

        string serialized = JsonSerializer.Serialize(result.Import);
        serialized.Should().NotContain("child_process");
        serialized.Should().NotContain("execSync");
        serialized.Should().NotContain("<html>");
    }

    [Fact]
    public void A_bundle_with_no_recognized_text_yields_nothing_recognized()
    {
        ImportFile notes = File("notes.txt", "just some words no importer reads");

        BundleImportResult result = Importer.ImportBundle([notes]);

        result.Import.Design.Should().BeNull();
        result.Import.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == BundleImporter.NothingRecognized);
    }

    [Fact]
    public void A_path_traversal_name_is_refused_before_any_importer_sees_it()
    {
        ImportFile traversal = File("../tokens.json", "{ \"color\": { \"x\": { \"$type\": \"color\", \"$value\": { \"hex\": \"#000000\", \"colorSpace\": \"srgb\" } } } }");

        BundleImportResult result = Importer.ImportBundle([traversal]);

        result.Import.Design.Should().BeNull();
        result.Import.Sources.Should().BeEmpty();
    }

    [Fact]
    public void Empty_or_null_input_yields_an_empty_result()
    {
        foreach (IReadOnlyList<ImportFile>? files in new IReadOnlyList<ImportFile>?[] { null, Array.Empty<ImportFile>() })
        {
            BundleImportResult result = Importer.ImportBundle(files);

            result.Import.Design.Should().BeNull();
            result.Import.Sources.Should().BeEmpty();
            result.Vendor.Should().BeNull();
        }
    }

    private static ImportFile File(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content), string.Empty, null);
}
