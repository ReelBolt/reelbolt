using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// P4-15 coverage for Penpot: the plain DTCG export path (the documented, tested recommended route,
/// already served by <c>dtcg</c>) and the <c>.penpot</c> archive path (a zip of JSON, v3) read by
/// <see cref="PenpotImporter"/>. The real v3 layout cannot be confirmed offline, so the synthetic
/// fixtures are written from the documented contract only and a BLOCKED note records that (see
/// handover/BLOCKED-penpot.md).
/// </summary>
public sealed class PenpotImportTests
{
    private static readonly BundleImporter Importer = new();

    [Theory]
    [InlineData("app.penpot")]
    [InlineData("theme.penpot.zip")]
    [InlineData("exports/design.penpot")]
    public void The_penpot_name_labels_the_archive(string path)
    {
        BundleImporter.DetectVendor([path], null).Should().Be(BundleVendors.Penpot);
    }

    [Fact]
    public void Plain_dtcg_export_is_read_for_real_through_the_dtcg_route()
    {
        // Penpot's plain "export tokens" output is a DTCG file, possibly with Tokens-Studio-style
        // $themes/sets. It carries no .penpot name: the registered dialect reads it, not Penpot.
        const string export = """
            {
              "$themes": [
                { "id": "light", "name": "Light", "selectedTokenSets": { "core": "enabled" } }
              ],
              "$metadata": { "tokenSetOrder": ["core"] },
              "core": {
                "color": {
                  "brand-primary": { "$type": "color", "$value": { "hex": "#6d28d9", "colorSpace": "srgb" } },
                  "surface": { "$type": "color", "$value": "#ffffff" }
                }
              }
            }
            """;
        ImportFile tokens = File("tokens.json", export);

        BundleImportResult result = Importer.ImportBundle([tokens]);

        result.Vendor.Should().BeNull();
        result.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.brand-primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.surface").Should().NotBeNull();

        result.Import.Sources.Should().Contain(source =>
            source.Path == "tokens.json"
            && source.Importer == "tokens-studio");
    }

    [Fact]
    public void Penpot_color_library_maps_to_color_penpot_paths()
    {
        ImportFile colors = File("colors.json", """
            {
              "colors": [
                { "name": "Brand / Primary", "color": "#6D28D9", "path": "brand/primary" },
                { "name": "Surface", "color": { "hex": "#ffffff" }, "path": "base/surface" },
                { "name": "Accent", "color": "#00ff88" }
              ]
            }
            """);

        DesignImportResult result = new PenpotImporter().Import([colors]);

        result.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.penpot.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.penpot.base.surface").Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.penpot.accent").Should().NotBeNull();

        JsonObject token = DesignDocument.FindToken(result.Design!, "color.penpot.brand.primary")!;
        token["$type"]!.GetValue<string>().Should().Be("color");
        token["$value"]!["hex"]!.GetValue<string>().Should().Be("#6d28d9");
        result.Sources.Should().Contain(source =>
            source.Path == "colors.json" && source.Importer == PenpotImporter.ImporterId);
    }

    [Fact]
    public void A_group_in_path_and_a_leaf_in_name_make_one_nested_token_path()
    {
        ImportFile colors = File("colors.json", """
            { "colors": [
              { "name": "Primary", "path": "Brand", "color": "#6D28D9" },
              { "name": "Surface", "path": "Brand", "color": "#0B0B0F" }
            ] }
            """);

        DesignImportResult result = new PenpotImporter().Import([colors]);

        DesignDocument.FindToken(result.Design!, "color.penpot.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.penpot.brand.surface").Should().NotBeNull();
    }

    [Fact]
    public void Id_keyed_library_maps_are_read_like_arrays()
    {
        ImportFile file = File("files/f.json", """
            { "data": { "colors": { "c1": { "name": "Accent", "color": "#00ff88" } } } }
            """);

        DesignImportResult result = new PenpotImporter().Import([file]);

        DesignDocument.FindToken(result.Design!, "color.penpot.accent").Should().NotBeNull();
    }

    [Fact]
    public void Penpot_typography_library_maps_to_typography_penpot_paths()
    {
        ImportFile typography = File("typography.json", """
            {
              "typography": [
                {
                  "name": "Heading / H1",
                  "font-family": "Inter, system-ui",
                  "font-size": "32px",
                  "font-weight": "700",
                  "line-height": "1.2",
                  "letter-spacing": "0.5px"
                }
              ]
            }
            """);

        DesignImportResult result = new PenpotImporter().Import([typography]);

        result.Design.Should().NotBeNull();
        JsonObject token = DesignDocument.FindToken(result.Design!, "typography.penpot.heading.h1")!;
        token["$type"]!.GetValue<string>().Should().Be("typography");
        JsonNode value = token["$value"]!;
        value["fontFamily"]!.AsArray().Select(node => node!.GetValue<string>())
            .Should().Equal("Inter", "system-ui");
        value["fontSize"]!["value"]!.GetValue<double>().Should().Be(32);
        value["fontWeight"]!.GetValue<int>().Should().Be(700);
        result.Sources.Should().Contain(source =>
            source.Path == "typography.json" && source.Importer == PenpotImporter.ImporterId);
    }

    [Fact]
    public void Kebab_and_camel_keys_both_read_from_a_tokens_lib_object()
    {
        ImportFile camel = File("camel.json", """
            {
              "tokensLib": {
                "colors": [ { "name": "camelColor", "color": "#112233" } ],
                "typography": [
                  {
                    "name": "camelBody",
                    "fontFamily": "Source Sans 3",
                    "fontSize": "16px",
                    "fontWeight": 400,
                    "lineHeight": "1.5"
                  }
                ]
              }
            }
            """);

        DesignImportResult result = new PenpotImporter().Import([camel]);

        result.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.penpot.camelcolor").Should().NotBeNull();
        JsonObject typography = DesignDocument.FindToken(result.Design!, "typography.penpot.camelbody")!;
        typography["$value"]!["fontFamily"]!.AsArray().Select(node => node!.GetValue<string>())
            .Should().Contain("Source Sans 3");
    }

    [Fact]
    public void A_penpot_archive_merges_colour_and_typography_libraries()
    {
        ImportFile marker = File("design.penpot", string.Empty);
        ImportFile colors = File("colors.json", """
            { "colors": [ { "name": "Primary", "color": "#6d28d9", "path": "brand/primary" } ] }
            """);
        ImportFile typography = File("typography.json", """
            { "typography": [ { "name": "Body", "font-family": "Inter", "font-size": "16px" } ] }
            """);

        BundleImportResult result = Importer.ImportBundle([marker, colors, typography]);

        result.Vendor.Should().Be(BundleVendors.Penpot);
        result.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.penpot.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "typography.penpot.body").Should().NotBeNull();
        result.Import.Sources.Select(source => source.Importer)
            .Should().Contain(PenpotImporter.ImporterId);
    }

    [Fact]
    public void An_unreadable_penpot_document_reports_no_library_and_nothing_recognized()
    {
        ImportFile marker = File("design.penpot", string.Empty);
        ImportFile page = File("pages/page-1.json", """{ "name": "Page 1", "objects": { "shape": { "type": "rect" } } }""");

        BundleImportResult result = Importer.ImportBundle([marker, page]);

        result.Vendor.Should().Be(BundleVendors.Penpot);
        result.Import.Design.Should().BeNull();
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == BundleImporter.NothingRecognized
            && diagnostic.Severity == ImportDiagnosticSeverity.Error);
    }

    [Fact]
    public void A_huge_page_file_is_dropped_by_the_importer_cap_and_the_libraries_still_import()
    {
        // The import worker inlines only text files up to ZipInlineFileMaxBytes (2 MiB) and the API
        // builds no ImportFile for a text-less entry, so an oversized page blob never reaches the
        // bundle at all. This is the defence in depth on the importer side: an oversized document is
        // refused by PenpotImporter's own JsonMaxBytes cap with a warning, never parsed or thrown on.
        const int cap = ImportLimits.JsonMaxBytes;
        string hugePage = "{\"name\":\"Page 1\",\"colors\":[\"" + new string('x', cap + 1024) + "\"]}";
        Encoding.UTF8.GetByteCount(hugePage).Should().BeGreaterThan(cap);

        ImportFile marker = File("design.penpot", string.Empty);
        ImportFile colors = File("colors.json", """{ "colors": [ { "name": "Primary", "color": "#6d28d9" } ] }""");
        ImportFile page = File("pages/page-1.json", hugePage);

        BundleImportResult result = Importer.ImportBundle([marker, colors, page]);

        result.Vendor.Should().Be(BundleVendors.Penpot);
        result.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.penpot.primary").Should().NotBeNull();
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == PenpotImporter.InvalidJsonDiagnostic
            && diagnostic.Path == "pages/page-1.json"
            && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
    }

    [Fact]
    public void Penpot_can_read_claims_json_library_documents_only()
    {
        var importer = new PenpotImporter();

        importer.CanRead("colors.json", Encoding.UTF8.GetBytes("""{ "colors": [] }""")).Should().BeTrue();
        importer.CanRead("tokens.json", Encoding.UTF8.GetBytes("""{ "tokensLib": {} }""")).Should().BeTrue();
        importer.CanRead("page.json", Encoding.UTF8.GetBytes("""{ "objects": {} }""")).Should().BeFalse();
        importer.CanRead("styles.json", Encoding.UTF8.GetBytes("""{"colors":[{"name":"x","color":"#000"}]}""")).Should().BeTrue();
        importer.CanRead("index.html", Encoding.UTF8.GetBytes("""{ "colors": [] }""")).Should().BeFalse();
    }

    [Fact]
    public void Inert_data_only_never_serializes_script_or_markup()
    {
        ImportFile marker = File("design.penpot", string.Empty);
        ImportFile page = File("pages/page-1.json", """
            { "name": "Page", "objects": { "evil": { "code": "require('child_process').execSync('touch /tmp/pwned')" } } }
            """);

        BundleImportResult result = Importer.ImportBundle([marker, page]);

        string serialized = System.Text.Json.JsonSerializer.Serialize(result.Import);
        serialized.Should().NotContain("child_process");
        serialized.Should().NotContain("execSync");
    }

    private static ImportFile File(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content), string.Empty, null);
}
