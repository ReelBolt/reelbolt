using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Brand;

/// <summary>
/// P3-07 coverage for <see cref="BrandFontMatcher"/> (SPEC 14.2): an exact, case-insensitive
/// catalog match wins with no warning; otherwise the keyword table picks the nearest bundled face
/// and the substitution is reported as <c>font_substituted:requested-&gt;used</c>; anything else is
/// Inter. The theory is the full keyword table, not a sample.
/// </summary>
public class BrandFontMatcherTests
{
    [Theory]
    // Exact catalog names, any casing — no warning.
    [InlineData("Inter", CaptionFont.Inter, null)]
    [InlineData("inter", CaptionFont.Inter, null)]
    [InlineData("Montserrat", CaptionFont.Montserrat, null)]
    [InlineData("Poppins", CaptionFont.Poppins, null)]
    [InlineData("Bebas Neue", CaptionFont.BebasNeue, null)]
    [InlineData("Anton", CaptionFont.Anton, null)]
    [InlineData("DejaVu Sans", CaptionFont.DejaVuSans, null)]
    // Inter keyword group.
    [InlineData("Inter Tight", CaptionFont.Inter, "font_substituted:Inter Tight->Inter")]
    [InlineData("Roboto", CaptionFont.Inter, "font_substituted:Roboto->Inter")]
    [InlineData("Helvetica Neue", CaptionFont.Inter, "font_substituted:Helvetica Neue->Inter")]
    [InlineData("Arial", CaptionFont.Inter, "font_substituted:Arial->Inter")]
    [InlineData("Segoe UI", CaptionFont.Inter, "font_substituted:Segoe UI->Inter")]
    [InlineData("system-ui", CaptionFont.Inter, "font_substituted:system-ui->Inter")]
    [InlineData("-apple-system", CaptionFont.Inter, "font_substituted:-apple-system->Inter")]
    [InlineData("SF Pro Display", CaptionFont.Inter, "font_substituted:SF Pro Display->Inter")]
    // Montserrat keyword group.
    [InlineData("Gotham", CaptionFont.Montserrat, "font_substituted:Gotham->Montserrat")]
    [InlineData("Proxima Nova", CaptionFont.Montserrat, "font_substituted:Proxima Nova->Montserrat")]
    [InlineData("Raleway", CaptionFont.Montserrat, "font_substituted:Raleway->Montserrat")]
    [InlineData("Josefin Sans", CaptionFont.Montserrat, "font_substituted:Josefin Sans->Montserrat")]
    // Poppins keyword group.
    [InlineData("Nunito", CaptionFont.Poppins, "font_substituted:Nunito->Poppins")]
    [InlineData("Quicksand", CaptionFont.Poppins, "font_substituted:Quicksand->Poppins")]
    [InlineData("DM Sans", CaptionFont.Poppins, "font_substituted:DM Sans->Poppins")]
    [InlineData("Manrope", CaptionFont.Poppins, "font_substituted:Manrope->Poppins")]
    [InlineData("Lexend", CaptionFont.Poppins, "font_substituted:Lexend->Poppins")]
    // Bebas Neue keyword group.
    [InlineData("Oswald", CaptionFont.BebasNeue, "font_substituted:Oswald->Bebas Neue")]
    [InlineData("Impact", CaptionFont.BebasNeue, "font_substituted:Impact->Bebas Neue")]
    [InlineData("League Gothic", CaptionFont.BebasNeue, "font_substituted:League Gothic->Bebas Neue")]
    // Anton keyword group.
    [InlineData("Anton SC", CaptionFont.Anton, "font_substituted:Anton SC->Anton")]
    // Serif / mono / script / unknown all fall back to Inter.
    [InlineData("Georgia", CaptionFont.Inter, "font_substituted:Georgia->Inter")]
    [InlineData("Courier New", CaptionFont.Inter, "font_substituted:Courier New->Inter")]
    [InlineData("Brush Script MT", CaptionFont.Inter, "font_substituted:Brush Script MT->Inter")]
    [InlineData("Comic Sans MS", CaptionFont.Inter, "font_substituted:Comic Sans MS->Inter")]
    [InlineData("sans-serif", CaptionFont.Inter, "font_substituted:sans-serif->Inter")]
    public void NearestCaptionFont_matches_the_spec_table(string family, CaptionFont expected, string? warning)
    {
        (CaptionFont font, string? actual) = BrandFontMatcher.NearestCaptionFont([family]);

        font.Should().Be(expected);
        actual.Should().Be(warning);
    }

    [Fact]
    public void NearestCaptionFont_prefers_an_exact_match_anywhere_in_the_stack()
    {
        (CaptionFont font, string? warning) = BrandFontMatcher.NearestCaptionFont(["Roboto", "Montserrat"]);

        font.Should().Be(CaptionFont.Montserrat);
        warning.Should().BeNull();
    }

    [Fact]
    public void NearestCaptionFont_treats_an_empty_request_as_inter_with_no_warning()
    {
        (CaptionFont font, string? warning) = BrandFontMatcher.NearestCaptionFont([]);

        font.Should().Be(CaptionFont.Inter);
        warning.Should().BeNull();
    }

    [Fact]
    public void NearestCaptionFont_ignores_quotes_around_a_family_name()
    {
        (CaptionFont font, string? warning) = BrandFontMatcher.NearestCaptionFont(["\"Inter\""]);

        font.Should().Be(CaptionFont.Inter);
        warning.Should().BeNull();
    }

    [Fact]
    public void RequestedFamilies_reads_every_role_stack_in_document_order()
    {
        var design = new JsonObject
        {
            ["typography"] = new JsonObject
            {
                ["family"] = new JsonObject
                {
                    ["heading"] = Stack("Satoshi", "Inter"),
                    ["body"] = Stack("Roboto"),
                    ["mono"] = Stack("JetBrains Mono")
                }
            }
        };

        BrandFontMatcher.RequestedFamilies(design).Should().Equal("Satoshi", "Inter", "Roboto", "JetBrains Mono");
    }

    [Fact]
    public void RequestedFamilies_is_empty_without_a_document_or_a_family_token()
    {
        BrandFontMatcher.RequestedFamilies(null).Should().BeEmpty();
        BrandFontMatcher.RequestedFamilies(new JsonObject { ["typography"] = new JsonObject() }).Should().BeEmpty();
    }

    private static JsonObject Stack(params string[] families) =>
        new() { ["$type"] = "fontFamily", ["$value"] = new JsonArray(families.Select(family => (JsonNode)JsonValue.Create(family)!).ToArray()) };
}
