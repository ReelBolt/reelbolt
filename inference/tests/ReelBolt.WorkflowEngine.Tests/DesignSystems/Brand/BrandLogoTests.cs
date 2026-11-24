using System;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems.Brand;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Brand;

public sealed class BrandLogoTests
{
    private static byte[] Png(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var p = pixel(x, y);
                int i = ((y * w) + x) * 4;
                rgba[i] = p.R; rgba[i + 1] = p.G; rgba[i + 2] = p.B; rgba[i + 3] = p.A;
            }
        }

        return PngAlpha.EncodeRgba(w, h, rgba);
    }

    private static LogoTone? Svg(string svg) => BrandLogoProfiler.Profile(Encoding.UTF8.GetBytes(svg), "image/svg+xml");

    private static string Wrap(string inner, string attrs = "") =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\" {attrs}>{inner}</svg>";

    [Fact]
    public void Png_black_on_transparent_is_dark()
    {
        var png = Png(8, 8, (x, y) => x < 4 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)0));
        var t = BrandLogoProfiler.Profile(png, "image/png")!;
        t.Tone.Should().Be("dark");
        t.ReadableOnLight.Should().BeTrue();
        t.ReadableOnDark.Should().BeFalse();
        t.Source.Should().Be("pixels");
    }

    [Fact]
    public void Png_white_is_light_and_violet_is_mid()
    {
        BrandLogoProfiler.Profile(Png(4, 4, (_, _) => (255, 255, 255, 255)), "image/png")!.Tone.Should().Be("light");
        var v = BrandLogoProfiler.Profile(Png(4, 4, (_, _) => (0x7e, 0x6f, 0xff, 255)), "image/png")!;
        v.Tone.Should().Be("mid");
        v.ReadableOnLight.Should().BeTrue();
        v.ReadableOnDark.Should().BeTrue();
    }

    [Fact]
    public void Png_unreadable_or_fully_transparent_is_null()
    {
        BrandLogoProfiler.Profile(Png(4, 4, (_, _) => (10, 10, 10, 0)), "image/png").Should().BeNull();
        BrandLogoProfiler.Profile(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, "image/png").Should().BeNull();
        var png = Png(4, 4, (_, _) => (0, 0, 0, 255));
        png[28] = 1; // interlace method byte of IHDR
        BrandLogoProfiler.Profile(png, "image/png").Should().BeNull();
        BrandLogoProfiler.Profile(png[..30], "image/png").Should().BeNull();
    }

    [Fact]
    public void Other_mime_types_are_not_decoded()
    {
        BrandLogoProfiler.Profile(new byte[] { 0xFF, 0xD8, 0xFF }, "image/jpeg").Should().BeNull();
        BrandLogoProfiler.Profile(new byte[] { 1 }, "image/webp").Should().BeNull();
    }

    [Fact]
    public void Svg_without_fill_is_default_black()
    {
        var t = Svg(Wrap("<path d=\"M0 0L10 10\"/><circle r=\"2\"/>"))!;
        t.Tone.Should().Be("dark");
        t.ReadableOnLight.Should().BeTrue();
        t.ReadableOnDark.Should().BeFalse();
        t.Source.Should().Be("svg-default-black");
    }

    [Fact]
    public void Svg_violet_is_mid_white_is_light()
    {
        var v = Svg(Wrap("<path d=\"M0 0L10 10\" fill=\"#7e6fff\"/>"))!;
        v.Tone.Should().Be("mid");
        v.Source.Should().Be("svg-fill");
        Svg(Wrap("<path d=\"M0 0L10 10\" fill=\"#fff\"/>"))!.Tone.Should().Be("light");
    }

    [Fact]
    public void Svg_inherited_style_and_named_fills()
    {
        Svg(Wrap("<g fill=\"white\"><path d=\"M0 0L10 10\"/></g>"))!.Tone.Should().Be("light");
        Svg(Wrap("<path d=\"M0 0L10 10\" style=\"stroke:red;fill:rgb(255,255,255)\"/>"))!.Tone.Should().Be("light");
        Svg(Wrap("<rect width=\"4\" height=\"4\" fill=\"none\"/><rect width=\"4\" height=\"4\" fill=\"#ffffff\"/>"))!.Tone.Should().Be("light");
    }

    [Fact]
    public void Svg_mixed_fills_are_weighted_by_path_length()
    {
        string longD = "M0 0 " + string.Concat(Enumerable.Repeat("L1 1 ", 200));
        var t = Svg(Wrap($"<path d=\"{longD}\" fill=\"#ffffff\"/><path d=\"M0 0L1 1\" fill=\"#000000\"/>"))!;
        t.Luminance.Should().BeGreaterThan(0.9);
        t.Tone.Should().Be("light");
    }

    [Fact]
    public void Svg_unknown_colours_give_null()
    {
        Svg(Wrap("<path d=\"M0 0L1 1\" fill=\"currentColor\"/>")).Should().BeNull();
        Svg(Wrap("<path d=\"M0 0L1 1\" fill=\"url(#g)\"/>")).Should().BeNull();
        Svg(Wrap("<path d=\"M0 0L1 1\" fill=\"none\"/>")).Should().BeNull();
        Svg(Wrap("<g/>")).Should().BeNull();
    }

    [Fact]
    public void Hostile_or_garbage_input_never_throws()
    {
        string xxe = "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaaa\"><!ENTITY b \"&a;&a;&a;\">]><svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"&b;\" fill=\"#fff\"/></svg>";
        Svg(xxe).Should().BeNull();
        Svg("not xml <<<").Should().BeNull();
        BrandLogoProfiler.Profile(new byte[600 * 1024], "image/svg+xml").Should().BeNull();
        BrandLogoProfiler.Profile(ReadOnlySpan<byte>.Empty, "image/svg+xml").Should().BeNull();
        BrandLogoProfiler.Profile(new byte[3], null!).Should().BeNull();
    }

    [Fact]
    public void Wcag_basics()
    {
        WcagContrast.Ratio(1, 0).Should().BeApproximately(21, 0.001);
        WcagContrast.RelativeLuminance(255, 255, 255).Should().BeApproximately(1, 0.001);
        WcagContrast.ReadableThreshold.Should().Be(3.0);
    }

    [Theory]
    [InlineData("logo-icon.svg", "acmehr", "product", "mark", null)]
    [InlineData("logo-icon-light.svg", "acmehr", "product", "mark", "light")]
    [InlineData("acmehr-logo.png", "acmehr", "product", "mark", null)]
    [InlineData("AcmeHrWordmark-dark.svg", "acmehr", "product", "wordmark", "dark")]
    [InlineData("horizontal-lockup.svg", "acmehr", "product", "lockup", null)]
    [InlineData("company-logo.png", "acmehr", "publisher", "mark", null)]
    [InlineData("apple-touch-icon-180x180.png", "acmehr", "product", "mark", null)]
    [InlineData("other-thing-logo.png", "acmehr", "unknown", "mark", null)]
    public void Classifier_table(string file, string slug, string subject, string form, string? hint)
    {
        var r = BrandLogoClassifier.Classify(file, slug, []);
        r.Subject.Should().Be(subject);
        r.Form.Should().Be(form);
        r.NameToneHint.Should().Be(hint);
    }

    [Fact]
    public void Classifier_publisher_name_regression()
    {
        BrandLogoClassifier.Classify("contoso-group-logo.png", "acmehr", ["Contoso Group"]).Subject.Should().Be("publisher");
        BrandLogoClassifier.Classify("contoso-group-logo.png", "acmehr", []).Subject.Should().NotBe("product");
    }

    [Fact]
    public void Classifier_never_throws()
    {
        BrandLogoClassifier.Classify(null, null, null).Should().NotBeNull();
        BrandLogoClassifier.Classify("", "", []).Subject.Should().Be("product");
    }

    [Fact]
    public void Publisher_names_from_prose()
    {
        BrandLogoNames.PublisherNamesFrom("Acme-HR is published by Contoso Group Srl in Italy.").Should().Contain("Contoso Group");
        BrandLogoNames.PublisherNamesFrom("A product by Acme Inc.").Should().Contain("Acme");
        BrandLogoNames.PublisherNamesFrom("© 2026 Foo Bar").Should().Contain("Foo Bar");
        BrandLogoNames.PublisherNamesFrom("from Initech GmbH, and by Acme LLC, by Zed Ltd, by Q Corp").Count.Should().BeLessThanOrEqualTo(3);
        BrandLogoNames.PublisherNamesFrom(null).Should().BeEmpty();
        BrandLogoNames.PublisherNamesFrom("nothing here").Should().BeEmpty();
    }

    // ---- QA fixes (T-006)

    [Fact]
    public void Classifier_publisher_sharing_the_product_name_is_product()
    {
        BrandLogoClassifier.Classify("acme-logo.png", "acme", ["Acme"]).Subject.Should().Be("product");
        BrandLogoClassifier.Classify("acme-logo.png", "acme", ["Acme Labs"]).Subject.Should().Be("product");
        BrandLogoClassifier.Classify("acme-logo.png", "acmelabs", ["Acme"]).Subject.Should().NotBe("publisher");
        BrandLogoClassifier.Classify("acme-logo.png", "acme", BrandLogoNames.PublisherNamesFrom("© Acme")).Subject.Should().Be("product");
        BrandLogoClassifier.Classify("acme-logo.png", "acme", BrandLogoNames.PublisherNamesFrom("by Acme Inc")).Subject.Should().Be("product");
    }

    [Fact]
    public void Classifier_checks_the_product_slug_before_generic_publisher_tokens()
    {
        BrandLogoClassifier.Classify("acme-studio-logo.png", "acme-studio", []).Subject.Should().Be("product");
        BrandLogoClassifier.Classify("studio-logo.png", "acme", []).Subject.Should().Be("publisher");
    }

    [Fact]
    public void Plate_opaque_white_with_blue_mark_measures_the_ink()
    {
        var png = Png(16, 16, (x, y) => x is >= 5 and < 11 && y is >= 5 and < 11
            ? ((byte)0, (byte)60, (byte)220, (byte)255)
            : ((byte)255, (byte)255, (byte)255, (byte)255));
        var t = BrandLogoProfiler.Profile(png, "image/png")!;
        t.ReadableOnLight.Should().BeTrue();
        t.Tone.Should().NotBe("light");
    }

    [Fact]
    public void Plate_opaque_dark_with_violet_mark_measures_the_ink()
    {
        var png = Png(16, 16, (x, y) => x is >= 5 and < 11 && y is >= 5 and < 11
            ? ((byte)0x7e, (byte)0x6f, (byte)0xff, (byte)255)
            : ((byte)10, (byte)10, (byte)20, (byte)255));
        var t = BrandLogoProfiler.Profile(png, "image/png")!;
        t.Tone.Should().Be("mid");
        t.ReadableOnDark.Should().BeTrue();
    }

    [Fact]
    public void Plate_uniform_or_disagreeing_corners_keep_the_whole_image()
    {
        BrandLogoProfiler.Profile(Png(8, 8, (_, _) => (255, 255, 255, 255)), "image/png")!.Tone.Should().Be("light");
        var split = Png(8, 8, (x, _) => x < 4 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        var t = BrandLogoProfiler.Profile(split, "image/png")!;
        t.Luminance.Should().BeApproximately(0.5, 0.45);
        t.Luminance.Should().BeGreaterThan(0.3);
    }

    [Fact]
    public void Logo_profile_pixel_cap_is_four_million()
    {
        PngAlpha.LogoProfileMaxPixels.Should().Be(4_000_000);
        var big = PngAlpha.EncodeRgba(2100, 2000, new byte[2100 * 2000 * 4]);
        PngAlpha.MeanLogoLinearColor(big).Should().BeNull();
    }

    [Theory]
    [InlineData("acme-hr-design", "acmehr")]
    [InlineData("Acme-HR Design System", "acmehr")]
    [InlineData("Acme Brand", "acme")]
    [InlineData("design system", "")]
    [InlineData(null, "")]
    public void Product_slug_from_design_name(string? name, string slug) =>
        BrandLogoNames.ProductSlugFrom(name).Should().Be(slug);

    [Fact]
    public void Publisher_names_need_a_word_boundary_and_localised_forms_need_a_suffix()
    {
        BrandLogoNames.PublisherNamesFrom("Standby Foo Inc").Should().BeEmpty();
        BrandLogoNames.PublisherNamesFrom("Prodotto da Contoso Group Srl").Should().Contain("Contoso Group");
        BrandLogoNames.PublisherNamesFrom("Sviluppato da Rossi SpA.").Should().Contain("Rossi");
        BrandLogoNames.PublisherNamesFrom("Un prodotto di Bianchi S.r.l.").Should().Contain("Bianchi");
        BrandLogoNames.PublisherNamesFrom("Producto de Perez SA").Should().Contain("Perez");
        BrandLogoNames.PublisherNamesFrom("Produit par Dupont SA").Should().Contain("Dupont");
        BrandLogoNames.PublisherNamesFrom("Prodotto da Mario Rossi").Should().BeEmpty();
        BrandLogoNames.PublisherNamesFrom("Made by Acme Inc.").Should().Contain("Acme");
    }

    [Fact]
    public void Svg_ignores_defs_clip_paths_and_faint_elements()
    {
        var onlyDefsAndWhite = Wrap("<defs><path d=\"M0 0L5 5\" fill=\"#000\"/></defs><clipPath><rect fill=\"#000\"/></clipPath><path d=\"M0 0L9 9\" fill=\"#fff\"/>");
        Svg(onlyDefsAndWhite)!.Tone.Should().Be("light");
        var faint = Wrap("<path d=\"M0 0L9 9\" fill=\"#000\" fill-opacity=\"0.2\"/><path d=\"M0 0L9 9\" fill=\"#fff\" opacity=\"0.3\"/><path d=\"M0 0L9 9\" fill=\"#7e6fff\"/>");
        Svg(faint)!.Tone.Should().Be("mid");
        Svg(Wrap("<defs><path d=\"M0 0L5 5\"/></defs>")).Should().BeNull();
    }

    [Fact]
    public void Svg_resolves_simple_style_block_rules()
    {
        var byClass = Wrap("<style>.a{fill:#ffffff}</style><path class=\"a\" d=\"M0 0L9 9\"/>");
        Svg(byClass)!.Tone.Should().Be("light");
        var byTag = Wrap("<style>path{fill:#7e6fff;}</style><path d=\"M0 0L9 9\"/>");
        Svg(byTag)!.Tone.Should().Be("mid");
        Svg(Wrap("<style>.a{fill:")).Should().BeNull();
    }

    [Fact]
    public void Svg_style_block_with_many_selectors_and_declarations_stays_cheap()
    {
        string selectors = string.Join(",", Enumerable.Range(0, 2_500).Select(i => ".c" + i));
        // Under the 32 KB per-block cut, so the rule is parsed: 2,500 selectors x ~1,500 junk declarations.
        string decls = "fill:#ffffff;" + string.Join(";", Enumerable.Range(0, 1_500).Select(i => "p" + i + ":1"));
        var hostile = Wrap("<style>" + selectors + "{" + decls + "}</style><path class=\"c0\" d=\"M0 0L9 9\"/>");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Svg(hostile);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        watch.Stop();

        (selectors.Length + decls.Length).Should().BeLessThan(32_000);
        Svg(hostile)!.Tone.Should().Be("light");
        watch.ElapsedMilliseconds.Should().BeLessThan(2_000);
        allocated.Should().BeLessThan(32L * 1024 * 1024);
    }
}
