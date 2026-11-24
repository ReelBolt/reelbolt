using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// Task 5: preview cards are structured product-map data. The card's markup is untrusted input:
/// it is reduced to plain text, its script body never surfaces, and only the basenames of relative
/// images are kept — no URL is ever fetched and nothing is executed.
/// </summary>
public sealed class PreviewCardReaderTests
{
    [Fact]
    public void Manifest_cards_become_preview_card_data()
    {
        IReadOnlyList<ProductMapPreviewCard> cards = PreviewCardReader.Read(
            [File("components/core/core.card.html",
                Utf8("<!-- @dsCard group=\"Components\" viewport=\"700x360\" name=\"Core\" subtitle=\"Button\" -->" +
                     "<body><b>Save</b><img src=\"../assets/logo.svg\"><script>alert(1)</script></body>"))],
            ManifestCards(("Core", "Components", "Button", "700x360", "components/core/core.card.html")),
            []);

        cards.Should().ContainSingle();
        cards[0].Name.Should().Be("Core");
        cards[0].Group.Should().Be("Components");
        cards[0].Subtitle.Should().Be("Button");
        cards[0].Viewport.Should().Be("700x360");
        cards[0].Path.Should().Be("components/core/core.card.html");
        cards[0].Text.Should().Be("Save");
        cards[0].Assets.Should().Equal("logo.svg");
        cards[0].RuntimeRendered.Should().BeTrue();
    }

    [Fact]
    public void A_card_script_body_never_reaches_the_text()
    {
        ProductMapPreviewCard card = PreviewCardReader.Read(
            [File("x.card.html", Utf8("<body><script type=\"text/babel\">const x = 'Approved';</script><b>Open</b></body>"))],
            ManifestCards(("X", "G", null, "1x1", "x.card.html")), [])[0];

        card.Text.Should().Be("Open").And.NotContain("Approved");
        card.RuntimeRendered.Should().BeTrue();
    }

    [Fact]
    public void A_card_without_a_script_is_not_runtime_rendered()
    {
        ProductMapPreviewCard card = PreviewCardReader.Read(
            [File("static.card.html", Utf8("<body><b>Quiet</b></body>"))],
            ManifestCards(("Static", "G", null, "1x1", "static.card.html")), [])[0];

        card.RuntimeRendered.Should().BeFalse();
        card.Text.Should().Be("Quiet");
    }

    [Fact]
    public void The_header_overrides_the_manifest_fields()
    {
        ProductMapPreviewCard card = PreviewCardReader.Read(
            [File("x.card.html",
                Utf8("<!-- @dsCard name=\"Header\" group=\"HeaderGroup\" subtitle=\"HeaderSub\" viewport=\"9x9\" -->" +
                     "<b>x</b>"))],
            ManifestCards(("Manifest", "ManifestGroup", "ManifestSub", "1x1", "x.card.html")), [])[0];

        card.Name.Should().Be("Header");
        card.Group.Should().Be("HeaderGroup");
        card.Subtitle.Should().Be("HeaderSub");
        card.Viewport.Should().Be("9x9");
        card.Path.Should().Be("x.card.html");
    }

    [Fact]
    public void Manifest_cards_come_first_then_unmatched_markup_by_path()
    {
        IReadOnlyList<ProductMapPreviewCard> cards = PreviewCardReader.Read(
            [
                File("b.card.html", Utf8("<!-- @dsCard name=\"B\" --><b>B</b>")),
                File("z.card.html", Utf8("<b>Z</b>")),
                File("a.card.html", Utf8("<!-- @dsCard name=\"A\" --><b>A</b>")),
            ],
            ManifestCards(("Zed", "G", null, "1x1", "z.card.html")),
            []);

        cards.Select(card => card.Name).Should().Equal("Zed", "A", "B");
    }

    [Fact]
    public void A_manifest_card_without_markup_keeps_empty_text_and_assets()
    {
        ProductMapPreviewCard card = PreviewCardReader.Read(
            [],
            ManifestCards(("Missing", "G", "S", "1x1", "gone.card.html")), [])[0];

        card.Name.Should().Be("Missing");
        card.Text.Should().BeNull();
        card.Assets.Should().BeEmpty();
        card.RuntimeRendered.Should().BeFalse();
    }

    [Fact]
    public void Only_relative_image_basenames_are_kept()
    {
        ProductMapPreviewCard card = PreviewCardReader.Read(
            [File("x.card.html", Utf8(
                "<img src=\"../assets/logo.svg\">" +
                "<img src=\"assets/inline/a.png\">" +
                "<img src=\"https://evil.example/remote.png\">" +
                "<img src=\"//evil.example/protocol.png\">" +
                "<img src=\"/root.png\">" +
                "<img src=\"data:image/png;base64,AAAA\">" +
                "<img src=\"javascript:alert(1)\">"))],
            ManifestCards(("X", "G", null, "1x1", "x.card.html")), [])[0];

        card.Assets.Should().Equal("logo.svg", "a.png");
    }

    [Fact]
    public void An_image_inside_a_script_body_is_not_a_card_asset()
    {
        ProductMapPreviewCard card = PreviewCardReader.Read(
            [File("x.card.html", Utf8(
                "<script>const img = '<img src=\"assets/hidden.png\">';</script>" +
                "<img src=\"assets/real.png\">"))],
            ManifestCards(("X", "G", null, "1x1", "x.card.html")), [])[0];

        card.Assets.Should().Equal("real.png");
    }

    [Fact]
    public void Assets_are_capped_at_eight()
    {
        string markup = string.Concat(Enumerable.Range(0, 12)
            .Select(i => $"<img src=\"assets/a{i}.png\">"));

        ProductMapPreviewCard card = PreviewCardReader.Read(
            [File("x.card.html", Utf8(markup))],
            ManifestCards(("X", "G", null, "1x1", "x.card.html")), [])[0];

        card.Assets.Should().HaveCount(8);
    }

    [Fact]
    public void Text_is_capped_at_500_characters()
    {
        ProductMapPreviewCard card = PreviewCardReader.Read(
            [File("x.card.html", Utf8("<body>" + new string('a', 600) + "</body>"))],
            ManifestCards(("X", "G", null, "1x1", "x.card.html")), [])[0];

        card.Text.Should().HaveLength(PreviewCardReader.MaxTextChars);
    }

    [Fact]
    public void Cards_are_capped_at_one_hundred()
    {
        var manifest = new JsonArray();
        var files = new List<ImportFile>();
        for (int i = 0; i < 105; i++)
        {
            manifest.Add(new JsonObject
            {
                ["name"] = "Card " + i,
                ["path"] = $"cards/c{i}.card.html",
            });
            files.Add(File($"cards/c{i}.card.html", Utf8("<b>" + i + "</b>")));
        }

        IReadOnlyList<ProductMapPreviewCard> cards = PreviewCardReader.Read(files, manifest, []);

        cards.Should().HaveCount(PreviewCardReader.MaxCards);
        cards[0].Name.Should().Be("Card 0");
        cards[^1].Name.Should().Be("Card 99");
    }

    [Fact]
    public void Paths_are_normalised_for_matching()
    {
        ProductMapPreviewCard card = PreviewCardReader.Read(
            [File("/components\\core\\core.card.html", Utf8("<b>Save</b>"))],
            ManifestCards(("Core", "G", null, "1x1", "components/core/core.card.html")), [])[0];

        card.Text.Should().Be("Save");
    }

    private static ImportFile File(string path, byte[] content) =>
        new(path, content, Convert.ToHexStringLower(SHA256.HashData(content)), null);

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static JsonArray ManifestCards(
        params (string Name, string? Group, string? Subtitle, string? Viewport, string? Path)[] cards)
    {
        var array = new JsonArray();
        foreach ((string name, string? group, string? subtitle, string? viewport, string? path) in cards)
        {
            var card = new JsonObject { ["name"] = name };
            if (group is not null)
                card["group"] = group;
            if (subtitle is not null)
                card["subtitle"] = subtitle;
            if (viewport is not null)
                card["viewport"] = viewport;
            if (path is not null)
                card["path"] = path;
            array.Add(card);
        }

        return array;
    }
}
