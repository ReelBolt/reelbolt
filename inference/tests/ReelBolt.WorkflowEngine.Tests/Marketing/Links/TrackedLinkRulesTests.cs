#nullable enable
using System;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Marketing.Links;
using ReelBolt.Shared.Data.Models.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing.Links;

/// <summary>The shape rules of a tracked link and the two addresses built from it.</summary>
public class TrackedLinkRulesTests
{
    private static MarketingLink Link(Action<MarketingLink>? change = null)
    {
        MarketingLink link = new()
        {
            Id = Guid.NewGuid(),
            Code = "hn-launch",
            Channel = "hackernews",
            Medium = "community",
            Campaign = "launch-2026-11",
            Placement = "Show HN, first comment",
            DestinationPath = "/for/software-teams",
            Persona = "software",
        };
        change?.Invoke(link);
        return link;
    }

    [Fact]
    public void The_short_path_is_l_slash_code()
    {
        TrackedLinkRules.ShortPath("hn-launch").Should().Be("/l/hn-launch");
    }

    [Theory]
    [InlineData("HN-Launch", "hn-launch")]
    [InlineData("HN-LAUNCH", "hn-launch")]
    [InlineData("hn-launch", "hn-launch")]
    [InlineData("YT-DEMO-2", "yt-demo-2")]
    [InlineData(" HN ", " hn ")] // only the letter case is repaired; this is still not a code
    [InlineData("", "")]
    [InlineData(null, null)]
    public void A_code_is_looked_up_lowered_and_nothing_else_is_repaired(string? typed, string? lookedUp)
    {
        TrackedLinkRules.NormalizeCode(typed).Should().Be(lookedUp);
    }

    [Fact]
    public void Lowering_does_not_depend_on_the_servers_culture()
    {
        System.Globalization.CultureInfo before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // Turkish lowers "I" to a dotless i under the current culture; a link code must not.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            TrackedLinkRules.NormalizeCode("LINKEDIN-BIO").Should().Be("linkedin-bio");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void The_long_path_is_the_one_of_the_design()
    {
        TrackedLinkRules.LongPath(Link()).Should().Be(
            "/for/software-teams?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&for=software");
    }

    [Theory]
    [InlineData("creator", "/for/creators")]
    [InlineData("business", "/for/small-business")]
    [InlineData("educator", "/for/educators")]
    [InlineData("nonprofit", "/for/nonprofits")]
    [InlineData("software", "/for/software-teams")]
    [InlineData("agency", "/for/agencies")]
    public void Home_with_a_landing_persona_lands_on_the_audience_page(string persona, string landing)
    {
        TrackedLinkRules.LongPath(Link(l => { l.DestinationPath = "/"; l.Persona = persona; })).Should().Be(
            landing + "?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&for=" + persona);
    }

    [Theory]
    [InlineData("other")]
    [InlineData(null)]
    public void Home_without_a_landing_persona_stays_home(string? persona)
    {
        TrackedLinkRules.LongPath(Link(l => { l.DestinationPath = "/"; l.Persona = persona; })).Should().StartWith("/?utm_source=");
    }

    [Fact]
    public void A_non_home_destination_is_not_rewritten_by_persona()
    {
        TrackedLinkRules.LongPath(Link(l => { l.DestinationPath = "/pricing"; l.Persona = "creator"; })).Should().StartWith("/pricing?utm_source=");
    }

    [Fact]
    public void A_link_without_a_persona_carries_no_for_parameter()
    {
        TrackedLinkRules.LongPath(Link(l => l.Persona = null)).Should().Be(
            "/for/software-teams?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch");
    }

    [Fact]
    public void A_plus_in_the_campaign_is_escaped_so_it_is_not_read_back_as_a_space()
    {
        TrackedLinkRules.LongPath(Link(l => l.Campaign = "a+b~c.d")).Should().Contain("&utm_campaign=a%2Bb~c.d&");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/for/software-teams")]
    [InlineData("/pricing")]
    [InlineData("/blog/a-post_v2.html")]
    [InlineData("/app/signup")]
    [InlineData("/login")] // starts with /l but is not a tracked link
    [InlineData("/Login")]
    [InlineData("/la")]
    [InlineData("/apiary")] // starts with /api but is not under it
    [InlineData("/app/api-keys")]
    [InlineData("/docs/api/keys")]
    public void An_own_origin_path_is_a_destination(string path)
    {
        TrackedLinkRules.IsDestinationPath(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("for/creators")] // no leading slash
    [InlineData("//evil.example")] // protocol-relative: another host
    [InlineData("/for//creators")]
    [InlineData("https://evil.example/")]
    [InlineData("/\\evil.example")] // browsers read a backslash as a slash
    [InlineData("/for/creators?x=1")] // no query
    [InlineData("/for/creators#top")] // no fragment
    [InlineData("/for/crea tors")]
    [InlineData("/for/creators\n")]
    [InlineData("/for/%2f%2fevil.example")] // no percent escapes
    [InlineData("/a/../b")]
    [InlineData("/./a")]
    [InlineData("/..")]
    [InlineData("/l")] // a tracked link itself: would loop
    [InlineData("/l/hn-launch")]
    [InlineData("/L")] // the edge matches /l/ in any letter case and the redirect lowers the code
    [InlineData("/L/hn-launch")]
    [InlineData("/L/HN-Launch")]
    [InlineData("/l/")]
    [InlineData("/api")] // the API's own path space: /api/v1/l/<code> is the redirect itself
    [InlineData("/api/v1/l/hn-launch")]
    [InlineData("/API/V1/L/hn-launch")]
    [InlineData("/Api/v1/l")]
    [InlineData("/api/v1/health")] // no API route is a landing page
    [InlineData("/api/")]
    [InlineData("/for/créateurs")]
    public void Anything_else_is_not_a_destination(string? path)
    {
        TrackedLinkRules.IsDestinationPath(path).Should().BeFalse();
    }

    [Fact]
    public void A_destination_is_at_most_two_hundred_characters()
    {
        TrackedLinkRules.IsDestinationPath("/" + new string('a', 199)).Should().BeTrue();
        TrackedLinkRules.IsDestinationPath("/" + new string('a', 200)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Show HN, first comment", true)]
    [InlineData("x", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("line one\nline two", false)]
    [InlineData("tab\there", false)]
    public void A_placement_is_a_short_single_line_note(string? placement, bool expected)
    {
        TrackedLinkRules.IsPlacement(placement).Should().Be(expected);
    }

    [Fact]
    public void A_placement_is_at_most_one_hundred_sixty_characters()
    {
        TrackedLinkRules.IsPlacement(new string('a', 160)).Should().BeTrue();
        TrackedLinkRules.IsPlacement(new string('a', 161)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(0L, "USD", true)]
    [InlineData(12500L, "EUR", true)]
    [InlineData(12500L, null, false)] // an amount needs a currency
    [InlineData(null, "EUR", false)] // and a currency needs an amount
    [InlineData(-1L, "EUR", false)]
    [InlineData(100L, "eur", false)]
    [InlineData(100L, "EURO", false)]
    [InlineData(100L, "E1R", false)]
    [InlineData(100L, "EUR\n", false)]
    public void A_cost_is_both_parts_or_neither(long? minor, string? currency, bool expected)
    {
        TrackedLinkRules.IsCost(minor, currency).Should().Be(expected);
    }

    [Fact]
    public void A_valid_link_has_no_problem()
    {
        TrackedLinkRules.FirstProblem(Link()).Should().BeNull();
        TrackedLinkRules.FirstProblem(Link(l => { l.Persona = null; l.CostMinor = 5000; l.CostCurrency = "USD"; })).Should().BeNull();
    }

    [Fact]
    public void Each_field_outside_its_set_or_shape_is_named_by_its_own_code()
    {
        TrackedLinkRules.FirstProblem(Link(l => l.Code = "HN")).Should().Be("code_invalid");
        TrackedLinkRules.FirstProblem(Link(l => l.Channel = "twitter")).Should().Be("channel_invalid");
        TrackedLinkRules.FirstProblem(Link(l => l.Medium = "cpc")).Should().Be("medium_invalid");
        TrackedLinkRules.FirstProblem(Link(l => l.Campaign = "Launch 2026")).Should().Be("campaign_invalid");
        TrackedLinkRules.FirstProblem(Link(l => l.Placement = " ")).Should().Be("placement_invalid");
        TrackedLinkRules.FirstProblem(Link(l => l.DestinationPath = "//evil.example")).Should().Be("destination_path_invalid");
        TrackedLinkRules.FirstProblem(Link(l => l.Persona = "developer")).Should().Be("persona_invalid");
        TrackedLinkRules.FirstProblem(Link(l => l.CostMinor = 10)).Should().Be("cost_invalid");
    }

    [Fact]
    public void Problems_are_reported_in_field_order()
    {
        TrackedLinkRules.FirstProblem(Link(l => { l.Channel = "twitter"; l.Persona = "developer"; }))
            .Should().Be("channel_invalid");
    }
}
