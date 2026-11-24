using System.Linq;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// T-006: font files and <c>@font-face</c> declarations. A font is bytes on the bounded binary
/// channel, magic-checked and digest-checked; a stylesheet declaration is recorded as a name and a
/// licence of <c>unknown</c>, never as a URL the host might fetch.
/// </summary>
public sealed class ClaudeDesignFontTests
{
    /// <summary>The WOFF2 magic alone is enough for the sniffer; the content is opaque here.</summary>
    private static byte[] Woff2(int extraBytes = 32) =>
        [0x77, 0x4F, 0x46, 0x32, .. new byte[extraBytes]];

    [Fact]
    public void A_local_font_file_becomes_a_font_asset()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("fonts/Inter-Regular.woff2", Woff2()));

        bundle.Assets.Should().Contain(a => a.Role == "font" && a.MimeType == "font/woff2");
        bundle.Assets.Single(a => a.Role == "font").Variant.Should().Be("Inter");
    }

    [Fact]
    public void A_font_under_the_assets_segment_is_selected_as_a_font()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("assets/Roboto-BoldItalic.woff2", Woff2()));

        bundle.Assets.Single(a => a.Role == "font").Variant.Should().Be("Roboto");
    }

    [Fact]
    public void A_font_face_url_is_reported_not_fetched()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("tokens/fonts.css", Utf8("@font-face{font-family:\"Inter\";src:url(https://example.test/i.woff2)}" +
                                       ":root{--font-sans:\"Inter\"}")));

        bundle.Import.Design!["fonts"]!["licence"]!["Inter"]!.GetValue<string>().Should().Be("unknown");
        bundle.Import.Diagnostics.Should().Contain(d => d.Code == ClaudeDesignImporter.FontSourceNotFetchedDiagnostic);
        bundle.Import.Design!.ToJsonString().Should().NotContain("example.test");
    }

    [Fact]
    public void The_unfetched_count_covers_src_and_import_declarations_in_one_diagnostic()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("tokens/fonts.css", Utf8(
                "@font-face{font-family:\"Inter\";src:url(https://example.test/i.woff2)}" +
                "@font-face{font-family:\"Mono\";src:local(\"Mono\")}" +
                "@import url(https://example.test/more.css);")));

        ImportDiagnostic diagnostic = bundle.Import.Diagnostics
            .Single(d => d.Code == ClaudeDesignImporter.FontSourceNotFetchedDiagnostic);
        diagnostic.Message.Should().Contain("3");
    }

    [Fact]
    public void A_font_over_the_cap_is_refused()
    {
        byte[] oversized = Woff2(ArchiveFontLimits.MaxEntryBytes);

        ClaudeDesignBundle bundle = new ClaudeDesignImporter().ImportBundle(
            [Manifest()],
            [Asset("fonts/Inter-Regular.woff2", oversized)]);

        bundle.Assets.Should().NotContain(a => a.Role == "font");
        bundle.Import.Diagnostics.Should().Contain(d => d.Code == ClaudeDesignImporter.FontFileRejectedDiagnostic);
    }

    [Fact]
    public void Non_font_bytes_are_refused()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

        ClaudeDesignBundle bundle = new ClaudeDesignImporter().ImportBundle(
            [Manifest()],
            [Asset("fonts/Inter-Regular.woff2", png)]);

        bundle.Assets.Should().NotContain(a => a.Role == "font");
        bundle.Import.Diagnostics.Should().Contain(d => d.Code == ClaudeDesignImporter.FontFileRejectedDiagnostic);
    }

    [Fact]
    public void A_font_with_a_bad_digest_is_refused()
    {
        byte[] bytes = Woff2();
        var tampered = new ImportedAsset(
            "font", null, "Inter-Regular.woff2", "fonts/Inter-Regular.woff2", "font/woff2",
            new string('0', 64), bytes);

        ClaudeDesignBundle bundle = new ClaudeDesignImporter().ImportBundle([Manifest()], [tampered]);

        bundle.Assets.Should().NotContain(a => a.Role == "font");
        bundle.Import.Diagnostics.Should().Contain(d => d.Code == ClaudeDesignImporter.AssetDigestMismatchDiagnostic);
    }

    private static ImportedAsset Asset(string path, byte[] bytes) => new(
        "font", null, System.IO.Path.GetFileName(path), path, "font/woff2",
        ClaudeDesignImporterTests.Sha(bytes), bytes);

    private static ImportFile Manifest() =>
        File("_ds_manifest.json", """{"namespace":"AcmeDesignSystem","themes":[],"globalCssPaths":[]}""");

    private static ImportFile File(string path, string content)
    {
        byte[] bytes = Utf8(content);
        return new ImportFile(path, bytes, ClaudeDesignImporterTests.Sha(bytes), null);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
