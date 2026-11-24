using System.Linq;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// P3-13 coverage for the pure <see cref="BrandPdfImporter"/>: hex and <c>RGB</c>/<c>R:0 G:0 B:0</c>
/// candidates near a role word become <c>color.*</c> tokens at confidence 0.4 with
/// <c>prov.method=static importer=brand-pdf</c>, unlabelled colours land under <c>color.extra.*</c>,
/// safe font names become <c>typography.family.*</c>, and tone words are never extracted.
/// </summary>
public sealed class BrandPdfImporterTests
{
    private static readonly BrandPdfImporter Importer = new();

    [Fact]
    public void Hex_near_primary_becomes_a_low_confidence_color_token()
    {
        DesignImportResult result = Importer.ImportText("Primary #112233");

        JsonObject design = result.Design!;
        JsonObject token = DesignDocument.FindToken(design, "color.primary")!;

        Assert.Equal("color", token["$type"]!.GetValue<string>());
        Assert.Equal("#112233", token["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal("srgb", token["$value"]!["colorSpace"]!.GetValue<string>());

        JsonObject reelbolt = token["$extensions"]!["com.reelbolt"]!.AsObject();
        Assert.Equal(0.4, reelbolt["conf"]!.GetValue<double>());
        Assert.Equal("static", reelbolt["prov"]!["method"]!.GetValue<string>());
        Assert.Equal("brand-pdf", reelbolt["prov"]!["importer"]!.GetValue<string>());
        Assert.Equal("brand-pdf", Importer.Id);
    }

    [Fact]
    public void Rgb_channels_near_secondary_become_a_color_token()
    {
        DesignImportResult result = Importer.ImportText("Secondary: RGB: 17 34 51");

        JsonObject token = DesignDocument.FindToken(result.Design!, "color.secondary")!;
        Assert.Equal("#112233", token["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal(0.4, token["$extensions"]!["com.reelbolt"]!["conf"]!.GetValue<double>());
    }

    [Fact]
    public void R_colon_channels_near_background_become_a_color_token()
    {
        DesignImportResult result = Importer.ImportText("Background R:0 G:0 B:0");

        JsonObject token = DesignDocument.FindToken(result.Design!, "color.background")!;
        Assert.Equal("#000000", token["$value"]!["hex"]!.GetValue<string>());
    }

    [Fact]
    public void An_unlabelled_hex_lands_under_color_extra()
    {
        DesignImportResult result = Importer.ImportText("Swatch Azure #abcdef");

        bool found = DesignDocument.Tokens(result.Design!)
            .Any(t => t.Path.StartsWith("color.extra.", System.StringComparison.Ordinal)
                && t.Value["hex"]!.GetValue<string>() == "#abcdef");

        Assert.True(found);
    }

    [Fact]
    public void The_nearest_role_word_wins()
    {
        DesignImportResult result = Importer.ImportText("Primary #111111 and Accent #222222");

        Assert.Equal("#111111", DesignDocument.FindToken(result.Design!, "color.primary")!["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal("#222222", DesignDocument.FindToken(result.Design!, "color.accent")!["$value"]!["hex"]!.GetValue<string>());
    }

    [Fact]
    public void Safe_font_names_become_typography_family_tokens()
    {
        DesignImportResult result = Importer.ImportText("Font: Inter\nTypography - Poppins");

        JsonObject inter = DesignDocument.FindToken(result.Design!, "typography.family.inter")!;
        Assert.Equal("fontFamily", inter["$type"]!.GetValue<string>());
        Assert.Equal("Inter", inter["$value"]![0]!.GetValue<string>());
        Assert.Equal(0.4, inter["$extensions"]!["com.reelbolt"]!["conf"]!.GetValue<double>());

        Assert.NotNull(DesignDocument.FindToken(result.Design!, "typography.family.poppins"));
    }

    [Fact]
    public void Font_names_are_emitted_only_when_safe()
    {
        DesignImportResult result = Importer.ImportText("Font:    ");

        Assert.Null(result.Design);
        Assert.Contains(result.Diagnostics, d => d.Code == "NO_TOKENS_RECOGNIZED");
    }

    [Fact]
    public void Tone_words_are_never_extracted()
    {
        DesignImportResult result = Importer.ImportText(
            "Brand tone: modern, bold and playful. Voice: friendly and warm.");

        Assert.Null(result.Design);
        Assert.Contains(result.Diagnostics, d => d.Code == "NO_TOKENS_RECOGNIZED");
    }
}
