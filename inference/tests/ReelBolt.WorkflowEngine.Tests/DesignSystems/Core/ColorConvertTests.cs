using System;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems.Color;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

public class ColorConvertTests
{
    [Theory]
    [InlineData("#fff", "#ffffff")]
    [InlineData("#FFF8", "#ffffff88")]
    [InlineData("#6D28D9", "#6d28d9")]
    [InlineData("rgb(255 0 0)", "#ff0000")]
    [InlineData("rgb(255,0,0,0.5)", "#ff000080")]
    [InlineData("rgba(0, 128, 255, 1)", "#0080ff")]
    [InlineData("rgb(100% 0% 0%)", "#ff0000")]
    [InlineData("hsl(0 100% 50%)", "#ff0000")]
    [InlineData("hsl(120 50% 50%)", "#40bf40")]
    [InlineData("hsl(0 100% 50% / 50%)", "#ff000080")]
    [InlineData("hwb(120 20% 20%)", "#33cc33")]
    [InlineData("oklch(1 0 0)", "#ffffff")]
    [InlineData("oklab(1 0 0)", "#ffffff")]
    [InlineData("lab(100 0 0)", "#ffffff")]
    [InlineData("color(display-p3 1 0 0)", "#ff0000")]
    [InlineData("color(srgb 0.5 0.5 0.5)", "#808080")]
    [InlineData("black", "#000000")]
    [InlineData("WHITE", "#ffffff")]
    [InlineData("transparent", "#00000000")]
    public void TryToSrgbHex_ExactValues(string css, string expected)
    {
        ColorConvert.TryToSrgbHex(css, out string hex, out _).Should().BeTrue();
        hex.Should().Be(expected);
    }

    [Theory]
    [InlineData("oklch(0.628 0.2577 29.23)", "#ff0000")]
    [InlineData("oklch(0.55 0.2 290)", "#7552db")]
    [InlineData("oklch(0.985 0 0)", "#fafafa")]
    [InlineData("oklch(0.205 0 0)", "#171717")]
    [InlineData("lab(50 0 0)", "#777777")]
    [InlineData("lch(54.29 106.8 40.85)", "#ff0000")]
    public void TryToSrgbHex_WithinOnePerChannel(string css, string expected)
    {
        ColorConvert.TryToSrgbHex(css, out string hex, out _).Should().BeTrue();
        AssertClose(hex, expected);
    }

    [Fact]
    public void TryToSrgbHex_ClipsOutOfGamutColour()
    {
        ColorConvert.TryToSrgbHex("oklch(0.7 0.4 150)", out string hex, out string? original).Should().BeTrue();

        hex.Should().HaveLength(7);
        hex[0].Should().Be('#');
        AssertClose(hex, "#00d600");
        original.Should().NotBeNull();
    }

    [Fact]
    public void TryToSrgbHex_KeepsOriginalForFunctionForms()
    {
        ColorConvert.TryToSrgbHex("oklch(0.55 0.2 290)", out _, out string? original).Should().BeTrue();
        original.Should().Be("oklch(0.55 0.2 290)");

        ColorConvert.TryToSrgbHex("rgb(1 2 3)", out _, out string? rgbOriginal).Should().BeTrue();
        rgbOriginal.Should().Be("rgb(1 2 3)");
    }

    [Fact]
    public void TryToSrgbHex_HexAndKeywordInputsHaveNoOriginal()
    {
        ColorConvert.TryToSrgbHex("#fff", out _, out string? hexOriginal).Should().BeTrue();
        hexOriginal.Should().BeNull();

        ColorConvert.TryToSrgbHex("black", out _, out string? keywordOriginal).Should().BeTrue();
        keywordOriginal.Should().BeNull();
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("#ff")]
    [InlineData("#gggggg")]
    [InlineData("rgb(1 2)")]
    [InlineData("rgb(1 2 3 4 5)")]
    [InlineData("rgb(1e999 0 0)")]
    [InlineData("color(rec2020 1 0 0)")]
    [InlineData("hsl(")]
    public void TryToSrgbHex_RejectsInvalidInput(string css)
    {
        ColorConvert.TryToSrgbHex(css, out string hex, out string? original).Should().BeFalse();
        hex.Should().BeEmpty();
        original.Should().BeNull();
    }

    [Fact]
    public void TryToSrgbHex_RejectsOverlongInput()
    {
        ColorConvert.TryToSrgbHex(new string('a', 300), out string hex, out string? original).Should().BeFalse();
        hex.Should().BeEmpty();
        original.Should().BeNull();
    }

    private static void AssertClose(string actual, string expected)
    {
        actual.Should().HaveLength(expected.Length);

        for (int i = 1; i < expected.Length; i += 2)
        {
            int actualChannel = Convert.ToInt32(actual.Substring(i, 2), 16);
            int expectedChannel = Convert.ToInt32(expected.Substring(i, 2), 16);
            Math.Abs(actualChannel - expectedChannel).Should().BeLessThanOrEqualTo(1);
        }
    }

    [Theory]
    [InlineData("rebeccapurple", "#663399")]
    [InlineData("AntiqueWhite", "#faebd7")]
    [InlineData("  CORNFLOWERBLUE  ", "#6495ed")]
    [InlineData("grey", "#808080")]
    [InlineData("gray", "#808080")]
    [InlineData("lightgoldenrodyellow", "#fafad2")]
    [InlineData("yellowgreen", "#9acd32")]
    [InlineData("aliceblue", "#f0f8ff")]
    [InlineData("RGBA(0,0,0,0.09)", "#00000017")]
    [InlineData("rgba(0,0,0,0)", "#00000000")]
    [InlineData("rgba(0,0,0,1)", "#000000")]
    [InlineData("rgba(0,0,0,0.5)", "#00000080")]
    [InlineData("rgb(300, -5, 128)", "#ff0080")]
    [InlineData("  #ABCDEF  ", "#abcdef")]
    [InlineData("#AABBCC80", "#aabbcc80")]
    [InlineData("hsla(240, 100%, 50%, 0.5)", "#0000ff80")]
    [InlineData("rgb(255 0 0 / 50%)", "#ff000080")]
    public void TryToSrgbHex_NamedColoursAndNormalisation(string css, string expected)
    {
        ColorConvert.TryToSrgbHex(css, out string hex, out _).Should().BeTrue();
        hex.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notacolor")]
    [InlineData("red blue")]
    [InlineData("linear-gradient(red, blue)")]
    [InlineData("radial-gradient(circle, #fff, #000)")]
    [InlineData("url(https://example.com/a.png)")]
    [InlineData("var(--x)")]
    [InlineData("calc(1px + 2px)")]
    [InlineData("green (Mantine green, the default accent)")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    [InlineData("rgb(1,2)")]
    public void TryToSrgbHex_RejectsWhatIsNotAColour(string css)
    {
        ColorConvert.TryToSrgbHex(css, out string hex, out string? original).Should().BeFalse();
        hex.Should().BeEmpty();
        original.Should().BeNull();
    }

    [Fact]
    public void TryToSrgbHex_KnowsAll148CssNamedColours()
    {
        string[] sample = ["black", "white", "red", "lime", "blue", "navy", "teal", "silver", "maroon", "olive", "orange", "pink", "purple", "tan", "tomato", "turquoise", "wheat", "whitesmoke", "yellow", "gold"];
        foreach (string name in sample)
            ColorConvert.TryToSrgbHex(name, out _, out _).Should().BeTrue(name);
    }
}
