using System;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

/// <summary>The product-analytics sanitizer contract: lowercase + trim, then keep-or-drop, never reject.</summary>
public class AttributionSanitizerTests
{
    [Theory]
    [InlineData("Google", "google")]
    [InlineData("  newsletter_2026  ", "newsletter_2026")]
    [InlineData("a.b-c~d+e", "a.b-c~d+e")]
    [InlineData("x", "x")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("-lead", null)]
    [InlineData("has space", null)]
    [InlineData("a@b.com", null)]
    [InlineData("<script>", null)]
    [InlineData("q?x=1", null)]
    public void Utm_keeps_the_closed_shape_and_drops_the_rest(string? raw, string? expected)
        => AttributionSanitizer.Utm(raw).Should().Be(expected);

    [Fact]
    public void Utm_length_boundary_is_64()
    {
        AttributionSanitizer.Utm(new string('a', 64)).Should().HaveLength(64);
        AttributionSanitizer.Utm(new string('a', 65)).Should().BeNull();
    }

    [Theory]
    [InlineData("News.Ycombinator.com", "news.ycombinator.com")]
    [InlineData("google.com", "google.com")]
    [InlineData("google.com:8080", null)]
    [InlineData("google.com/path", null)]
    [InlineData("https://google.com", null)]
    [InlineData("google.com?x=1", null)]
    [InlineData("reelbolt.ai", null)]
    [InlineData("www.reelbolt.ai", null)]
    [InlineData("notreelbolt.ai", "notreelbolt.ai")]
    [InlineData("", null)]
    public void ReferrerHost_is_a_bare_foreign_hostname(string raw, string? expected)
        => AttributionSanitizer.ReferrerHost(raw, ["reelbolt.ai"]).Should().Be(expected);

    [Fact]
    public void ReferrerHost_length_boundary_is_100()
    {
        AttributionSanitizer.ReferrerHost(new string('a', 100)).Should().HaveLength(100);
        AttributionSanitizer.ReferrerHost(new string('a', 101)).Should().BeNull();
    }

    [Theory]
    [InlineData("/pricing", "/pricing")]
    [InlineData("/Blog/Post-1?utm_source=x#frag", "/blog/post-1")]
    [InlineData("/", "/")]
    [InlineData("/a?b", "/a")]
    [InlineData("pricing", null)]
    [InlineData("/a b", null)]
    [InlineData("/a%20b", null)]
    [InlineData("?x=1", null)]
    [InlineData("//evil.com", null)]
    [InlineData("", null)]
    public void LandingPath_strips_query_and_fragment_then_must_match(string raw, string? expected)
        => AttributionSanitizer.LandingPath(raw).Should().Be(expected);

    [Fact]
    public void LandingPath_length_boundary_is_120()
    {
        AttributionSanitizer.LandingPath("/" + new string('a', 119)).Should().HaveLength(120);
        AttributionSanitizer.LandingPath("/" + new string('a', 120)).Should().BeNull();
    }

    // In .NET a "$" also matches just before a final line feed. A line feed that sat in front of the query
    // survived the trim (it was not at the end yet) and then the shape: 121 characters into a varchar(120)
    // column, or a stored control character. Every shape is anchored at the end of the string.
    [Theory]
    [InlineData("/pricing\n?x=1")]
    [InlineData("/pricing\n#frag")]
    [InlineData("/pricing\r\n?x=1")]
    [InlineData("/pricing\t?x=1")]
    [InlineData("/pricing ?x=1")]
    [InlineData("/pri\ncing")]
    [InlineData("/pricing\u0000")]
    [InlineData("/pricing\u0085?x=1")]
    [InlineData("/pricing\u2028?x=1")]
    public void LandingPath_drops_a_path_with_white_space_or_a_control_character_before_the_query(string raw)
        => AttributionSanitizer.LandingPath(raw).Should().BeNull();

    [Fact]
    public void LandingPath_never_returns_more_than_the_column_holds_or_a_line_feed()
    {
        // The widest path the shape allows, then a line feed, then a query: the reported 121-character value.
        AttributionSanitizer.LandingPath("/" + new string('a', 119) + "\n?x=1").Should().BeNull();
        AttributionSanitizer.LandingPath("/" + new string('a', 119) + "?x=1").Should().HaveLength(120);
        // A line feed at the very end is trimmed with the rest of the outer white space, as before.
        AttributionSanitizer.LandingPath("  /pricing\n").Should().Be("/pricing");
    }

    [Theory]
    [InlineData("google\nx")]
    [InlineData("goo\ngle")]
    public void Utm_and_host_drop_a_value_with_a_line_feed_inside(string raw)
    {
        AttributionSanitizer.Utm(raw).Should().BeNull();
        AttributionSanitizer.ReferrerHost(raw).Should().BeNull();
    }

    [Theory]
    [InlineData("home\n")]
    [InlineData("\nhome")]
    [InlineData("ho\nme")]
    [InlineData("home ")]
    [InlineData("Home")]
    public void DetailToken_is_compared_as_sent_and_a_line_feed_anywhere_drops_it(string raw)
        => AttributionSanitizer.DetailToken(raw).Should().BeNull();

    [Fact]
    public void DetailToken_keeps_a_closed_token()
    {
        AttributionSanitizer.DetailToken("blog:a-story-to-tell").Should().Be("blog:a-story-to-tell");
        AttributionSanitizer.DetailToken(new string('a', 64)).Should().HaveLength(64);
        AttributionSanitizer.DetailToken(new string('a', 65)).Should().BeNull();
    }

    [Theory]
    [InlineData("google", "x.com", "google")]
    [InlineData(null, "x.com", "x.com")]
    [InlineData(null, null, "direct")]
    public void SourceLabel_prefers_utm_then_referrer_then_direct(string? utm, string? host, string expected)
        => AttributionSanitizer.SourceLabel(utm, host).Should().Be(expected);

    [Fact]
    public void SourceLabel_never_exceeds_the_column_width()
        => AttributionSanitizer.SourceLabel(null, new string('a', 90)).Should().Be("direct");

    [Theory]
    [InlineData("f47ac10b-58cc-4372-a567-0e02b2c3d479", true)]
    [InlineData("F47AC10B-58CC-4372-B567-0E02B2C3D479", true)]
    [InlineData("f47ac10b-58cc-1372-a567-0e02b2c3d479", false)]
    [InlineData("f47ac10b-58cc-4372-c567-0e02b2c3d479", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("f47ac10b58cc4372a5670e02b2c3d479", false)]
    [InlineData("not-a-guid", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void VisitorId_must_be_a_canonical_v4_uuid(string? raw, bool valid)
        => AttributionSanitizer.VisitorId(raw).HasValue.Should().Be(valid);
}
