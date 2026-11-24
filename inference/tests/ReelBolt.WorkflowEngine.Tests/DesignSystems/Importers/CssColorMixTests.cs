using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class CssColorMixTests
{
    [Theory]
    [InlineData("color-mix(in srgb, #ff0000 50%, #0000ff 50%)", "#800080")]
    [InlineData("color-mix(in srgb, #ff0000 25%, #0000ff)", "#4000bf")]
    public void A_color_mix_of_two_literals_resolves(string value, string expected)
    {
        Assert.True(CssColorMix.TryResolve(value, new Dictionary<string, string>(), out string? hex));
        Assert.Equal(expected, hex);
    }

    [Fact]
    public void A_color_mix_with_an_alias_resolves()
    {
        var vars = new Dictionary<string, string> { ["surface"] = "#ebedef", ["primary"] = "#7e6fff" };

        Assert.True(CssColorMix.TryResolve("color-mix(in srgb, #7e6fff 4%, var(--surface))", vars, out string? hex));
        Assert.NotNull(hex);
        Assert.Equal("#e7e8f0", hex);
    }

    [Theory]
    [InlineData("color-mix(in oklab, #ff0000, #0000ff)")]
    [InlineData("color-mix(in srgb, rgb(255,0,0), #0000ff)")]
    [InlineData("color-mix(in srgb, var(--missing), #ffffff)")]
    [InlineData("color-mix(in srgb, #ff0000 50%, #0000ff 50%, #00ff00 50%)")]
    [InlineData("color-mix(in srgb, color-mix(in srgb, #ff0000, #00ff00), #0000ff)")]
    public void An_unsupported_color_mix_is_rejected(string value)
    {
        Assert.False(CssColorMix.TryResolve(value, new Dictionary<string, string>(), out _));
    }

    [Fact]
    public void An_unresolvable_color_mix_is_reported()
    {
        DesignImportResult result = Import(":root{--color-x:color-mix(in srgb, var(--missing), #fff)}");

        Assert.Null(DesignDocument.FindToken(result.Design!, "color.x"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == CssThemeImporter.MixUnresolved);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UNSUPPORTED_VALUE");
    }

    [Fact]
    public void A_resolvable_color_mix_emits_a_colour_token()
    {
        DesignImportResult result = Import(
            ":root{--surface:#ebedef;--color-x:color-mix(in srgb, #7e6fff 4%, var(--surface))}");

        JsonObject? token = DesignDocument.FindToken(result.Design!, "color.x");
        Assert.NotNull(token);
        Assert.Equal("color", token!["$type"]!.GetValue<string>());
        Assert.Equal("#e7e8f0", token["$value"]!["hex"]!.GetValue<string>());
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == CssThemeImporter.MixUnresolved);
    }

    private static DesignImportResult Import(string css)
    {
        var file = new ImportFile("tokens.css", Encoding.UTF8.GetBytes(css), string.Empty, null);
        return new CssThemeImporter().Import([file]);
    }
}
