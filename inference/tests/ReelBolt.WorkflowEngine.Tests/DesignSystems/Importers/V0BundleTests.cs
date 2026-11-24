using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// P4-13 coverage for the <c>v0</c> vendor hint and the tolerant import of a v0 export: the shared
/// <see cref="TolerantTokenWalker"/> maps <c>v0.json</c>, the design-md prose rules read
/// <c>SKILL.md</c> for voice, and <c>app/globals.css</c> / <c>tailwind.config.ts</c> flow through
/// their own importers. The synthetic fixtures are written from the documented contract only; no
/// real v0 export was available (see handover/BLOCKED-v0.md), so the walker is the stand-in.
/// </summary>
public sealed class V0BundleTests
{
    private static readonly BundleImporter Importer = new();

    [Theory]
    [InlineData("v0.json")]
    [InlineData("design/v0.json")]
    [InlineData("SKILL.md")]
    [InlineData("design/SKILL.md")]
    public void The_v0_files_label_the_archive(string path)
    {
        BundleImporter.DetectVendor([path], null).Should().Be(BundleVendors.V0);
    }

    [Fact]
    public void Fixture_v0_json_walker_maps_colors_fonts_and_dimensions()
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
        ImportFile v0 = File("v0.json", json);

        BundleImportResult result = Importer.ImportBundle([v0]);

        result.Vendor.Should().Be(BundleVendors.V0);
        result.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.surface").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "typography.family.sans").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "typography.family.mono").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "radius.sm").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "spacing.4").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.note").Should().BeNull();

        result.Import.Sources.Should().Contain(source =>
            source.Path == "v0.json" && source.Importer == TolerantTokenWalker.ImporterId);
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == TolerantTokenWalker.SkippedDiagnostic
            && diagnostic.Severity == ImportDiagnosticSeverity.Info);
    }

    [Fact]
    public void Fixture_skill_md_yields_voice_through_the_design_md_prose_rules()
    {
        const string markdown = """
            # Acme voice

            ## Overview
            A plain, confident voice for developers.

            ## Do
            - Use short sentences
            - Prefer active verbs

            ## Don't
            - Never shout
            - Avoid jargon
            """;
        ImportFile skill = File("SKILL.md", markdown);

        BundleImportResult result = Importer.ImportBundle([skill]);

        result.Vendor.Should().Be(BundleVendors.V0);
        result.Import.Design.Should().NotBeNull();

        var voice = result.Import.Design!["voice"] as JsonObject;
        voice.Should().NotBeNull();
        voice!["summary"]!.GetValue<string>().Should().Be("A plain, confident voice for developers.");
        voice["do"]!.AsArray().Select(node => node!.GetValue<string>())
            .Should().Contain("Use short sentences");
        voice["dont"]!.AsArray().Select(node => node!.GetValue<string>())
            .Should().Contain("Never shout");

        result.Import.Sources.Should().Contain(source =>
            source.Path == "SKILL.md" && source.Importer == "design-md");
    }

    [Fact]
    public void Fixture_globals_css_shadcn_vars_flow_through_css_theme()
    {
        ImportFile skill = File("SKILL.md", "# Acme\n\n## Do\n- Use the brand colour\n");
        ImportFile css = File("app/globals.css", ":root { --color-brand-primary: #6d28d9; --radius: 0.625rem; }");
        ImportFile package = File("package.json", """{ "dependencies": { "next": "15.0.0" } }""");

        BundleImportResult result = Importer.ImportBundle([skill, css, package]);

        result.Vendor.Should().Be(BundleVendors.V0);
        DesignDocument.FindToken(result.Import.Design!, "color.brand.primary").Should().NotBeNull();
        result.Import.Sources.Should().Contain(source =>
            source.Path == "app/globals.css" && source.Importer == "css-theme");
        result.Dependencies.Should().Contain("next");
    }

    [Fact]
    // Rewritten (QA-6): the config used to be asserted as parsed and merged; the worker never delivers a script,
    // so that only held for a hand-built ImportFile list. It is now reported as seen-and-not-read.
    public void Fixture_tailwind_config_ts_is_reported_as_not_read_and_contributes_no_tokens()
    {
        ImportFile skill = File("SKILL.md", "# Acme\n\n## Do\n- Use the brand colour\n");
        ImportFile tailwind = File(
            "app/tailwind.config.ts",
            "export default { theme: { extend: { colors: { accent: '#ff0000' }, spacing: { gutter: '1rem' } } } };");

        BundleImporter.IsAllowedTextFile("app/tailwind.config.ts").Should().BeFalse();

        BundleImportResult result = Importer.ImportBundle([skill, tailwind]);

        DesignDocument.FindToken(result.Import.Design!, "color.accent").Should().BeNull();
        DesignDocument.FindToken(result.Import.Design!, "spacing.gutter").Should().BeNull();
        result.Import.Sources.Select(source => source.Path).Should().NotContain("app/tailwind.config.ts");
        result.Import.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == BundleImporter.TailwindConfigNotReadDiagnostic && diagnostic.Path == "app/tailwind.config.ts");
    }

    [Fact]
    public void Fixture_evil_config_js_is_dropped_with_a_diagnostic_only()
    {
        const string evil = "const cp = require('child_process'); cp.execSync('touch /tmp/pwned');";
        ImportFile script = File("evil.config.js", evil);
        ImportFile skill = File("SKILL.md", "# Acme\n\n## Do\n- Use the brand colour\n");

        BundleImporter.IsAllowedTextFile("evil.config.js").Should().BeFalse();

        BundleImportResult result = Importer.ImportBundle([script, skill]);

        result.Import.Sources.Select(source => source.Path).Should().NotContain("evil.config.js");
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == BundleImporter.DroppedDiagnostic && diagnostic.Path == "evil.config.js");

        string serialized = JsonSerializer.Serialize(result.Import);
        serialized.Should().NotContain("child_process");
        serialized.Should().NotContain("execSync");
    }

    private static ImportFile File(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content), string.Empty, null);
}

/// <summary>
/// Direct coverage for <see cref="TolerantTokenWalker"/>: the recognized key names, the conversions
/// and the Info skip for a leaf it cannot place. The walker is shared with the planned Lovable and
/// Penpot importers, so it is exercised without the bundle wrapper too.
/// </summary>
public sealed class TolerantTokenWalkerTests
{
    [Fact]
    public void Walker_finds_recognized_keys_at_any_depth_and_skips_unknown_leaves()
    {
        const string json = """
            {
              "design": {
                "palette": { "ink": "#111111", "note": "hello" },
                "typography": {
                  "body": { "fontFamily": "Inter, sans-serif", "fontSize": "16px" }
                },
                "borderRadius": { "card": "12px" },
                "nested": { "theme": { "spacing": { "2": "0.5rem" } } }
              }
            }
            """;
        var diagnostics = new List<ImportDiagnostic>();

        JsonObject? design = TolerantTokenWalker.WalkFile("v0.json", Encoding.UTF8.GetBytes(json), diagnostics);

        design.Should().NotBeNull();
        DesignDocument.FindToken(design!, "color.ink").Should().NotBeNull();
        DesignDocument.FindToken(design!, "color.note").Should().BeNull();
        DesignDocument.FindToken(design!, "typography.family.body").Should().NotBeNull();
        DesignDocument.FindToken(design!, "radius.card").Should().NotBeNull();
        DesignDocument.FindToken(design!, "spacing.2").Should().NotBeNull();

        // The nested fontSize is a metric, never a font stack: it is skipped with Info.
        DesignDocument.FindToken(design!, "typography.family.body.fontsize").Should().BeNull();
        diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == TolerantTokenWalker.SkippedDiagnostic
            && diagnostic.Severity == ImportDiagnosticSeverity.Info);
    }

    [Fact]
    public void Walker_reads_a_bare_string_container_and_reports_its_source()
    {
        var diagnostics = new List<ImportDiagnostic>();

        JsonObject? design =
            TolerantTokenWalker.WalkFile("v0.json", Encoding.UTF8.GetBytes("""{ "colors": "#6d28d9" }"""), diagnostics);

        design.Should().NotBeNull();
        var token = DesignDocument.FindToken(design!, "color.default");
        token.Should().NotBeNull();
        token!["$type"]!.GetValue<string>().Should().Be("color");
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Walker_returns_null_and_warns_on_invalid_json()
    {
        var diagnostics = new List<ImportDiagnostic>();

        JsonObject? design =
            TolerantTokenWalker.WalkFile("v0.json", Encoding.UTF8.GetBytes("{ not json"), diagnostics);

        design.Should().BeNull();
        diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == TolerantTokenWalker.InvalidJsonDiagnostic
            && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
    }

    [Fact]
    public void Walker_reports_nothing_recognized_for_an_unrelated_document()
    {
        var diagnostics = new List<ImportDiagnostic>();

        JsonObject? design =
            TolerantTokenWalker.WalkFile("v0.json", Encoding.UTF8.GetBytes("""{ "name": "acme" }"""), diagnostics);

        design.Should().BeNull();
        diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == TolerantTokenWalker.NothingRecognizedDiagnostic
            && diagnostic.Severity == ImportDiagnosticSeverity.Info);
    }
}
