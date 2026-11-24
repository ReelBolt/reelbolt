using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// P4-16 coverage for Sketch: the <c>document.json</c> + <c>meta.json</c> layout label, the
/// <c>sharedSwatches</c> / legacy <c>assets.colorAssets</c> swatch libraries and the
/// <c>layerTextStyles</c> text styles read by <see cref="SketchImporter"/>, and the guarantee that
/// pages, layer trees and <c>meta.json</c> are never read. The real <c>document.json</c> shape
/// cannot be confirmed offline, so the synthetic fixtures are written from the documented contract
/// only and a BLOCKED note records that (see handover/BLOCKED-sketch.md).
/// </summary>
public sealed class SketchImportTests
{
    private static readonly BundleImporter Importer = new();

    [Fact]
    public void Sketch_layout_is_the_document_json_and_meta_json_pair()
    {
        BundleImporter.DetectVendor(["document.json", "meta.json"], null)
            .Should().Be(BundleVendors.Sketch);

        // A pages/ tree is expected in a real archive but is never read, so it is not required.
        BundleImporter.DetectVendor(["document.json", "meta.json", "pages/Page-1.json"], null)
            .Should().Be(BundleVendors.Sketch);

        BundleImporter.DetectVendor(["document.json", "pages/Page-1.json"], null)
            .Should().BeNull();
        BundleImporter.DetectVendor(["document.json"], null)
            .Should().BeNull();
    }

    [Fact]
    public void Shared_swatches_map_to_color_sketch_paths()
    {
        ImportFile document = Document("""
            {
              "sharedSwatches": {
                "objects": [
                  { "name": "Brand/Primary", "color": { "red": 0.427, "green": 0.157, "blue": 0.851, "alpha": 1 } }
                ]
              }
            }
            """);

        DesignImportResult result = new SketchImporter().Import([document]);

        result.Design.Should().NotBeNull();
        JsonObject token = DesignDocument.FindToken(result.Design!, "color.sketch.brand-primary")!;
        token["$type"]!.GetValue<string>().Should().Be("color");
        token["$value"]!["hex"]!.GetValue<string>().Should().Be("#6d28d9");
        token["$value"]!["colorSpace"]!.GetValue<string>().Should().Be("srgb");
        result.Sources.Should().Contain(source =>
            source.Path == "document.json" && source.Importer == SketchImporter.ImporterId);
    }

    [Fact]
    public void A_swatch_value_that_is_the_colour_object_itself_is_read()
    {
        ImportFile document = Document("""
            { "sharedSwatches": { "objects": [
              { "_class": "swatch", "name": "Primary", "value": { "_class": "color", "alpha": 1, "red": 0.427, "green": 0.157, "blue": 0.851 } }
            ] } }
            """);

        DesignImportResult result = new SketchImporter().Import([document]);

        JsonObject token = DesignDocument.FindToken(result.Design!, "color.sketch.primary")!;
        token["$value"]!["hex"]!.GetValue<string>().Should().Be("#6d28d9");
    }

    [Fact]
    public void Legacy_assets_color_assets_are_read_too()
    {
        ImportFile document = Document("""
            {
              "assets": {
                "colorAssets": [
                  { "name": "Accent", "color": { "red": 0, "green": 1, "blue": 0.533, "alpha": 1 } }
                ]
              }
            }
            """);

        DesignImportResult result = new SketchImporter().Import([document]);

        result.Design.Should().NotBeNull();
        JsonObject token = DesignDocument.FindToken(result.Design!, "color.sketch.accent")!;
        token["$value"]!["hex"]!.GetValue<string>().Should().Be("#00ff88");
    }

    [Fact]
    public void Layer_text_styles_map_to_typography_sketch_with_a_family_guess()
    {
        ImportFile document = Document("""
            {
              "layerTextStyles": {
                "objects": [
                  {
                    "name": "Heading/H1",
                    "value": {
                      "textStyle": {
                        "encodedAttributes": {
                          "MSAttributedStringFontAttribute": {
                            "attributes": { "name": "Inter-Bold", "size": 32 }
                          }
                        }
                      }
                    }
                  }
                ]
              }
            }
            """);

        DesignImportResult result = new SketchImporter().Import([document]);

        result.Design.Should().NotBeNull();
        JsonObject token = DesignDocument.FindToken(result.Design!, "typography.sketch.heading-h1")!;
        token["$type"]!.GetValue<string>().Should().Be("typography");
        JsonNode value = token["$value"]!;
        value["fontFamily"]!.AsArray().Select(node => node!.GetValue<string>())
            .Should().Equal("Inter");
        value["fontSize"]!["value"]!.GetValue<double>().Should().Be(32);
        value["fontSize"]!["unit"]!.GetValue<string>().Should().Be("px");
    }

    [Fact]
    public void A_bundle_of_document_meta_and_pages_labels_sketch_and_reads_only_document_json()
    {
        ImportFile document = Document("""
            { "sharedSwatches": { "objects": [ { "name": "Primary", "color": { "red": 0.427, "green": 0.157, "blue": 0.851 } } ] } }
            """);
        ImportFile meta = File("meta.json", """
            { "sharedSwatches": { "objects": [ { "name": "Meta/Decoy", "color": { "red": 1, "green": 0, "blue": 0 } } ] } }
            """);
        ImportFile page = File("pages/Page-1.json", """
            { "name": "Page 1", "layers": [ { "name": "Page/Decoy", "color": { "red": 0, "green": 0, "blue": 1 } } ] }
            """);

        BundleImportResult result = Importer.ImportBundle([document, meta, page]);

        result.Vendor.Should().Be(BundleVendors.Sketch);
        result.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.sketch.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.sketch.meta-decoy").Should().BeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.sketch.page-decoy").Should().BeNull();
        result.Import.Sources.Where(source => source.Importer == SketchImporter.ImporterId)
            .Select(source => source.Path)
            .Should().Equal("document.json");
    }

    [Fact]
    public void Meta_json_is_never_read_even_when_malformed()
    {
        ImportFile document = Document("""
            { "sharedSwatches": { "objects": [ { "name": "Primary", "color": { "red": 0.427, "green": 0.157, "blue": 0.851 } } ] } }
            """);
        ImportFile meta = File("meta.json", """{ this is not even valid json""");

        BundleImportResult result = Importer.ImportBundle([document, meta]);

        result.Vendor.Should().Be(BundleVendors.Sketch);
        result.Import.Design.Should().NotBeNull();
        result.Import.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Path == "meta.json" && diagnostic.Code == SketchImporter.InvalidJsonDiagnostic);
    }

    [Fact]
    public void A_page_layer_tree_is_never_read()
    {
        ImportFile document = Document("""
            { "sharedSwatches": { "objects": [ { "name": "Primary", "color": { "red": 0.427, "green": 0.157, "blue": 0.851 } } ] } }
            """);
        ImportFile page = File("pages/Page-1.json", """
            {
              "name": "Page 1",
              "layers": [
                { "name": "Brand/Redirect", "color": { "red": 0.9, "green": 0.1, "blue": 0.1, "alpha": 1 } }
              ]
            }
            """);
        ImportFile meta = File("meta.json", "{}");

        BundleImportResult result = Importer.ImportBundle([document, page, meta]);

        result.Vendor.Should().Be(BundleVendors.Sketch);
        DesignDocument.FindToken(result.Import.Design!, "color.sketch.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.sketch.brand-redirect").Should().BeNull();
    }

    [Fact]
    public void A_huge_pages_file_is_dropped_and_document_json_still_imports()
    {
        // The import worker inlines only text files up to ZipInlineFileMaxBytes (2 MiB), so an
        // oversized page blob never reaches the bundle at all. This is the defence in depth on the
        // importer side: a page file is never read, whatever its size.
        const int cap = ImportLimits.JsonMaxBytes;
        string hugePage = "{\"name\":\"Page 1\",\"data\":[\"" + new string('x', cap + 1024) + "\"]}";
        Encoding.UTF8.GetByteCount(hugePage).Should().BeGreaterThan(cap);

        ImportFile document = Document("""
            { "sharedSwatches": { "objects": [ { "name": "Primary", "color": { "red": 0.427, "green": 0.157, "blue": 0.851 } } ] } }
            """);
        ImportFile meta = File("meta.json", "{}");
        ImportFile page = File("pages/Page-1.json", hugePage);

        BundleImportResult result = Importer.ImportBundle([document, meta, page]);

        result.Vendor.Should().Be(BundleVendors.Sketch);
        result.Import.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Import.Design!, "color.sketch.primary").Should().NotBeNull();
        result.Import.Sources.Where(source => source.Importer == SketchImporter.ImporterId)
            .Select(source => source.Path)
            .Should().Equal("document.json");
    }

    [Fact]
    public void A_document_with_no_library_reports_no_library_and_nothing_recognized()
    {
        ImportFile document = Document("""{ "pages": [ { "name": "Page 1", "layers": [] } ] }""");
        ImportFile meta = File("meta.json", "{}");

        BundleImportResult result = Importer.ImportBundle([document, meta]);

        result.Vendor.Should().Be(BundleVendors.Sketch);
        result.Import.Design.Should().BeNull();
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == SketchImporter.NoLibraryDiagnostic);
        result.Import.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == BundleImporter.NothingRecognized
            && diagnostic.Severity == ImportDiagnosticSeverity.Error);
    }

    [Fact]
    public void The_modern_value_fill_color_shape_reads_tolerantly()
    {
        ImportFile document = Document("""
            {
              "sharedSwatches": {
                "objects": [
                  { "name": "Modern/Fill", "value": { "fills": [ { "color": { "red": 0, "green": 0, "blue": 0, "alpha": 1 } } ] } },
                  { "name": "Modern/Single", "value": { "fill": { "color": { "red": 1, "green": 1, "blue": 1, "alpha": 1 } } } }
                ]
              }
            }
            """);

        DesignImportResult result = new SketchImporter().Import([document]);

        result.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.sketch.modern-fill")!["$value"]!["hex"]!
            .GetValue<string>().Should().Be("#000000");
        DesignDocument.FindToken(result.Design!, "color.sketch.modern-single")!["$value"]!["hex"]!
            .GetValue<string>().Should().Be("#ffffff");
    }

    [Fact]
    public void Sketch_can_read_claims_document_json_only()
    {
        var importer = new SketchImporter();
        byte[] head = Encoding.UTF8.GetBytes("{}");

        importer.CanRead("document.json", head).Should().BeTrue();
        importer.CanRead("assets/document.json", head).Should().BeTrue();
        importer.CanRead("meta.json", head).Should().BeFalse();
        importer.CanRead("pages/Page-1.json", head).Should().BeFalse();
        importer.CanRead("document.json.bak", head).Should().BeFalse();
    }

    [Theory]
    [InlineData("Inter-Bold", "Inter")]
    [InlineData("Inter-BoldItalic", "Inter")]
    [InlineData("HelveticaNeue-CondensedBold", "HelveticaNeue")]
    [InlineData("ArialMT", "ArialMT")]
    [InlineData("SourceSansPro-Regular", "SourceSansPro")]
    public void The_family_guess_strips_the_postscript_style_suffix(string postScriptName, string expected)
    {
        SketchImporter.GuessFamily(postScriptName).Should().Be(expected);
    }

    [Fact]
    public void The_synthetic_document_fixture_reads_both_libraries()
    {
        ImportFile document = Fixture("sketch/document.json");

        DesignImportResult result = new SketchImporter().Import([document]);

        result.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.sketch.brand-primary")!["$value"]!["hex"]!
            .GetValue<string>().Should().Be("#6d28d9");
        DesignDocument.FindToken(result.Design!, "color.sketch.surface")!["$value"]!["hex"]!
            .GetValue<string>().Should().Be("#ffffff");
        DesignDocument.FindToken(result.Design!, "color.sketch.legacy-accent")!["$value"]!["hex"]!
            .GetValue<string>().Should().Be("#00ff88");
        DesignDocument.FindToken(result.Design!, "typography.sketch.heading-h1")!["$value"]!["fontFamily"]!
            .AsArray().Select(node => node!.GetValue<string>()).Should().Equal("Inter");
        DesignDocument.FindToken(result.Design!, "typography.sketch.body")!["$value"]!["fontSize"]!["value"]!
            .GetValue<double>().Should().Be(16);

        // The fixture's layer-tree decoy is inside document.json too, and is still never read.
        DesignDocument.FindToken(result.Design!, "color.sketch.decoy-layer").Should().BeNull();
    }

    [Fact]
    public void Inert_data_only_never_serializes_script_or_markup()
    {
        ImportFile document = Document("""
            {
              "sharedSwatches": {
                "objects": [
                  { "name": "Evil", "color": { "red": 0, "green": 0, "blue": 0 }, "script": "require('child_process').execSync('touch /tmp/pwned')" }
                ]
              },
              "layerTextStyles": { "objects": [] }
            }
            """);

        DesignImportResult result = new SketchImporter().Import([document]);

        string serialized = System.Text.Json.JsonSerializer.Serialize(result);
        serialized.Should().NotContain("child_process");
        serialized.Should().NotContain("execSync");
    }

    private static ImportFile Document(string content) => File("document.json", content);

    private static ImportFile File(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content), string.Empty, null);

    private static ImportFile Fixture(string name)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(Path.Combine(FixtureDirectory(), name));
        return new ImportFile("document.json", bytes, string.Empty, null);
    }

    private static string FixtureDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "Fixtures");
}
