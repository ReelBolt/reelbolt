using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class ClaudeDesignLogoSelectionTests
{
    private const string Manifest = """{"namespace":"AcmeDesignSystem","themes":[],"globalCssPaths":[]}""";
    private const string Skill = "---\nname: acme-design\ndescription: Acme design system.\n---\n";

    private static byte[] Png((byte R, byte G, byte B) color)
    {
        var rgba = new byte[8 * 8 * 4];
        for (int i = 0; i < 64; i++)
        {
            rgba[i * 4] = color.R; rgba[(i * 4) + 1] = color.G; rgba[(i * 4) + 2] = color.B; rgba[(i * 4) + 3] = 255;
        }

        return PngAlpha.EncodeRgba(8, 8, rgba);
    }

    private static byte[] Svg(string fill) => Loader.Utf8(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><path " + fill + " d=\"M0 0h10v10H0z\"/></svg>");

    [Fact]
    public void Product_icons_without_the_slug_in_the_name_are_product_logos_with_measured_tone()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("_ds_manifest.json", Loader.Utf8(Manifest)),
            ("SKILL.md", Loader.Utf8(Skill)),
            ("assets/logo-icon.svg", Svg(string.Empty)),
            ("assets/logo-icon-light.svg", Svg("fill=\"#7e6fff\"")));

        bundle.Assets.Should().HaveCount(2);
        bundle.Assets.Should().OnlyContain(a => a.Subject == "product" && a.Variant != "publisher");
        ImportedAsset plain = bundle.Assets.Single(a => a.Name == "logo-icon.svg");
        plain.Tone.Should().Be("dark");
        plain.ReadableOn.Should().Equal("light");
        ImportedAsset violet = bundle.Assets.Single(a => a.Name == "logo-icon-light.svg");
        violet.Tone.Should().Be("mid");
        violet.ReadableOn.Should().Equal("light", "dark");
        violet.Luminance.Should().BeInRange(0.1, 0.4);
        violet.Form.Should().Be("mark");
    }

    [Fact]
    public void A_company_mark_named_after_the_readme_owner_is_the_publisher_and_not_the_base()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("_ds_manifest.json", Loader.Utf8(Manifest)),
            ("SKILL.md", Loader.Utf8(Skill)),
            ("readme.md", Loader.Utf8("# Acme\n\nAcme is an HR portal published by Harbor Group.\n")),
            ("assets/acme-logo.png", Png((0x7e, 0x6f, 0xff))),
            ("assets/aa-harbor-group-logo.png", Png((0x20, 0x60, 0xd0))));

        ImportedAsset publisher = bundle.Assets.Single(a => a.Name == "aa-harbor-group-logo.png");
        publisher.Subject.Should().Be("publisher");
        publisher.Variant.Should().Be("publisher");
        publisher.Tone.Should().NotBeNull();

        ImportedAsset product = bundle.Assets.Single(a => a.Name == "acme-logo.png");
        product.Subject.Should().Be("product");
        product.Variant.Should().Be("default");
    }

    [Fact]
    public void Without_a_readme_owner_the_unnamed_distinctive_file_is_unknown_and_not_the_base()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("_ds_manifest.json", Loader.Utf8(Manifest)),
            ("SKILL.md", Loader.Utf8(Skill)),
            ("assets/aa-zeta-logo.png", Png((0x20, 0x60, 0xd0))),
            ("assets/acme-logo.png", Png((0x7e, 0x6f, 0xff))));

        ImportedAsset unknown = bundle.Assets.Single(a => a.Name == "aa-zeta-logo.png");
        unknown.Subject.Should().Be("unknown");
        unknown.Variant.Should().NotBe("default").And.NotBe("publisher");
        bundle.Assets.Single(a => a.Name == "acme-logo.png").Variant.Should().Be("default");
    }

    [Fact]
    public void A_name_token_that_contradicts_the_measured_tone_is_reported_and_the_measurement_wins()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("_ds_manifest.json", Loader.Utf8(Manifest)),
            ("SKILL.md", Loader.Utf8(Skill)),
            ("assets/acme-logo-light.png", Png((0, 0, 0))));

        bundle.Assets.Single().Tone.Should().Be("dark");
        bundle.Import.Diagnostics.Should().Contain(d => d.Code == "LOGO_NAME_TONE_MISMATCH");
        DesignImportReportBuilder.AxisForCode("LOGO_NAME_TONE_MISMATCH").Should().Be(DesignImportAxis.Assets);
    }

    [Fact]
    public void An_unmeasurable_logo_falls_back_to_the_name_token()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("_ds_manifest.json", Loader.Utf8(Manifest)),
            ("SKILL.md", Loader.Utf8(Skill)),
            ("assets/acme-logo-dark.png", Loader.TinyPng));

        ImportedAsset asset = bundle.Assets.Single();
        asset.Tone.Should().Be("dark");
        asset.Luminance.Should().BeNull();
    }
}
