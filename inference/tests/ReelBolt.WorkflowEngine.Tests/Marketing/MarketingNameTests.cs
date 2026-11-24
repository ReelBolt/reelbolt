#nullable enable
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>brevo-contact-sync T-001: cleaning and splitting of contact names.</summary>
public sealed class MarketingNameTests
{
    [Theory]
    [InlineData("  Maria   Rosa  ", "Maria Rosa")]
    [InlineData("Li", "Li")]
    [InlineData("José", "José")]
    [InlineData("A\tB", "A B")]
    public void Clean_accepts_and_normalizes(string input, string expected) =>
        MarketingName.Clean(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("‮evil")]
    [InlineData("a​b")]
    [InlineData("a\u0007b")]
    public void Clean_refuses(string? input) => MarketingName.Clean(input).Should().BeNull();

    [Fact]
    public void Clean_refuses_over_100_never_truncates()
    {
        MarketingName.Clean(new string('a', 100)).Should().HaveLength(100);
        MarketingName.Clean(new string('a', 101)).Should().BeNull();
    }

    [Theory]
    [InlineData("Maria Rosa Bianchi", "Maria", "Rosa Bianchi")]
    [InlineData("Li", "Li", null)]
    [InlineData("ada", "ada", null)]
    public void FromDisplayName_splits(string name, string first, string? last)
    {
        (string? f, string? l) = MarketingName.FromDisplayName(name, "someone@example.com");
        f.Should().Be(first);
        l.Should().Be(last);
    }

    [Theory]
    [InlineData("jane.doe", "jane.doe@example.com")]
    [InlineData("Jane.Doe", "jane.doe@example.com")]
    [InlineData("a@b", "x@example.com")]
    [InlineData("", "x@example.com")]
    [InlineData(null, "x@example.com")]
    [InlineData("‮bad", "x@example.com")]
    public void FromDisplayName_refuses_fallback_email_blank_and_invalid(string? name, string email) =>
        MarketingName.FromDisplayName(name, email).Should().Be(((string?)null, (string?)null));

    [Theory]
    [InlineData("Mary-Jane O'Neil")]
    [InlineData("Zo\u00eb")]
    [InlineData("O\u2019Brien")]
    public void CleanFormName_accepts(string input) => MarketingName.CleanFormName(input).Should().Be(input);

    [Theory]
    [InlineData("evil.com")]
    [InlineData("http://x")]
    [InlineData("a@b")]
    [InlineData("J.R.")]
    [InlineData("Bob2")]
    [InlineData("-Bob")]
    [InlineData("'Bob")]
    [InlineData("<b>x</b>")]
    [InlineData("a:b")]
    public void CleanFormName_refuses_urls_digits_and_symbols(string input) => MarketingName.CleanFormName(input).Should().BeNull();

    [Fact]
    public void CleanFormName_caps_at_40()
    {
        MarketingName.CleanFormName(new string('a', 40)).Should().HaveLength(40);
        MarketingName.CleanFormName(new string('a', 41)).Should().BeNull();
    }

    [Theory]
    [InlineData("a\u0301\u0302\u0303\u0304\u0305b")]
    [InlineData("\u0301\u0302")]
    [InlineData("\u0301")]
    public void Clean_and_form_refuse_stacked_or_mark_only_names(string input)
    {
        MarketingName.Clean(input).Should().BeNull();
        MarketingName.CleanFormName(input).Should().BeNull();
    }

    [Theory]
    [InlineData("Zo\u00eb")]
    [InlineData("e\u0301\u0302x")]
    [InlineData("\u0928\u093e\u0930")]
    public void Normal_marks_stay_valid(string input) => MarketingName.CleanFormName(input).Should().NotBeNull();

    [Fact]
    public void Form_refuses_the_full_stop_lookalike_letter() => MarketingName.CleanFormName("evil\uA78Fcom").Should().BeNull();

    [Theory]
    [InlineData("\u041f\u0430\u0432\u0435\u043b")]
    [InlineData("\u0393\u03b9\u03ce\u03c1\u03b3\u03bf\u03c2")]
    [InlineData("\u0645\u062d\u0645\u062f")]
    [InlineData("\u0930\u093e\u0939\u0941\u0932")]
    [InlineData("\ubbfc\uc900")]
    [InlineData("\u674e\u96f7")]
    [InlineData("\u7530\u4e2d\u3055\u3093")]
    [InlineData("Jos\u00e9 Mar\u00eda")]
    public void CleanFormName_accepts_one_script_names(string input) => MarketingName.CleanFormName(input).Should().Be(input);

    [Theory]
    [InlineData("\uA4F8\uA4F8\uA4F8")]
    [InlineData("a\uA4FDb")]
    [InlineData("\uA4F8x")]
    [InlineData("\u1427\u1403")]
    [InlineData("\u02B0ello")]
    [InlineData("Mar\u02BCy")]
    [InlineData("P\u0430ul")]
    [InlineData("\uFF21\uFF22")]
    [InlineData("\U0001D400a")]
    public void CleanFormName_refuses_lookalikes_modifiers_and_mixed_scripts(string input) =>
        MarketingName.CleanFormName(input).Should().BeNull();

    [Theory]
    [InlineData("\u0e2a\u0e21\u0e0a\u0e32\u0e22")]
    [InlineData("\u0e40\u0e2d\u0e01")]
    [InlineData("\u30b8\u30e7\u30fc\u30b8")]
    [InlineData("\u05d1\u05b0\u05b7\u05bc\u05e8")]
    public void CleanFormName_accepts_thai_prolonged_sound_and_voweled_hebrew(string input) =>
        MarketingName.CleanFormName(input).Should().Be(input);

    [Theory]
    [InlineData("a\u3164b")]
    [InlineData("a\u115Fb")]
    [InlineData("a\u1160b")]
    [InlineData("\u3164")]
    [InlineData("e\u0337")]
    [InlineData("e\u0338x")]
    [InlineData("e\u20DD")]
    [InlineData("e\uFE0F")]
    [InlineData("\u0430\u0483")]
    [InlineData("A\u30fc")]
    [InlineData("\u30fc\u30b8")]
    [InlineData("\u05d1\u05b0\u05bc\u05b7\u05b8\u05e8")]
    [InlineData("a\u0332\u0333\u0334b")]
    public void CleanFormName_refuses_fillers_foreign_marks_and_stray_prolonged_sound(string input) =>
        MarketingName.CleanFormName(input).Should().BeNull();
}
