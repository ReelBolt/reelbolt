#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Editing.ReviewLinks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ReviewLinks;

/// <summary>The token helper (shape, hash, entropy) and the guest text normaliser.</summary>
public class ReviewLinkTokensAndTextTests
{
    [Fact]
    public void A_generated_token_is_43_base64url_characters_of_32_random_bytes()
    {
        string token = ReviewLinkTokens.Generate();

        token.Should().HaveLength(ReviewLinkTokens.TokenLength).And.HaveLength(43);
        ReviewLinkTokens.IsWellFormed(token).Should().BeTrue();
        string padded = token.Replace('-', '+').Replace('_', '/') + "=";
        Convert.FromBase64String(padded).Should().HaveCount(32, "43 base64url characters carry exactly 32 bytes");
    }

    [Fact]
    public void Tokens_do_not_repeat_and_use_the_whole_alphabet_range()
    {
        HashSet<string> seen = [];
        for (int i = 0; i < 2000; i++) seen.Add(ReviewLinkTokens.Generate()).Should().BeTrue("a collision in 2000 draws of 256 bits is not possible");

        string joined = string.Concat(seen);
        joined.Any(char.IsUpper).Should().BeTrue();
        joined.Any(char.IsLower).Should().BeTrue();
        joined.Any(char.IsDigit).Should().BeTrue();
        joined.Should().Contain("-").And.Contain("_");
    }

    [Fact]
    public void The_hash_is_sha256_of_the_token_text_and_never_the_token()
    {
        string token = ReviewLinkTokens.Generate();
        byte[] hash = ReviewLinkTokens.Hash(token);

        hash.Should().HaveCount(32);
        hash.Should().Equal(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(token)));
        hash.Should().NotEqual(Encoding.ASCII.GetBytes(token).Take(32).ToArray());
        ReviewLinkTokens.Hash(token).Should().Equal(hash, "hashing is deterministic: it is the lookup key");
        ReviewLinkTokens.Hash(ReviewLinkTokens.Generate()).Should().NotEqual(hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 42
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 44
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")] // plus
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/")] // slash
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // padding
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.")] // dot (output-link tokens look like this)
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")] // space
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA%")] // percent
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAé")] // non ascii
    public void Anything_that_is_not_43_characters_of_the_alphabet_is_malformed(string? token) =>
        ReviewLinkTokens.IsWellFormed(token).Should().BeFalse();

    [Fact]
    public void A_name_is_nfc_one_line_collapsed_and_stripped_of_invisible_characters()
    {
        // NFD e + combining acute -> one precomposed char; zero-width, bidi override, tab and newlines go.
        ReviewGuestText.NormalizeName("  Ame\u0301lie\u200B  \u202Egnirps\u202C\t\nBell  ").Should().Be("Amélie gnirps Bell");
        ReviewGuestText.NormalizeName("A\u0000B\u0007C\uFEFFD").Should().Be("ABCD");
        ReviewGuestText.NormalizeName("\u200B \u202E \t").Should().BeNull("nothing visible is left");
        ReviewGuestText.NormalizeName(null).Should().BeNull();
        ReviewGuestText.NormalizeName("lone \uD800 surrogate").Should().Be("lone surrogate");
    }

    [Fact]
    public void A_body_keeps_paragraphs_but_not_controls_or_runs_of_blank_lines()
    {
        ReviewGuestText.NormalizeBody("line one\r\nline two\n\n\n\nline three\u2028four").Should().Be("line one\nline two\n\nline three\nfour");
        ReviewGuestText.NormalizeBody("\n\n  \u200Bhello\u202E  \u0001world \n\n").Should().Be("hello world");
        ReviewGuestText.NormalizeBody("  \n \u200B ").Should().BeNull();
    }

    [Fact]
    public void Markup_is_plain_text_and_is_stored_exactly_as_typed_apart_from_normalisation()
    {
        ReviewGuestText.NormalizeBody("<script>alert(1)</script> <img src=x onerror=alert(1)> [x](javascript:alert(1))")
            .Should().Be("<script>alert(1)</script> <img src=x onerror=alert(1)> [x](javascript:alert(1))");
    }

    [Fact]
    public void An_excerpt_is_cut_on_a_character_boundary_and_flattened()
    {
        string text = "first line\nsecond " + new string('x', 200);
        string excerpt = ReviewGuestText.Excerpt(text, 80);
        excerpt.Length.Should().BeLessThanOrEqualTo(81);
        excerpt.Should().NotContain("\n").And.EndWith("…");

        string emoji = new string('a', 79) + "\U0001F600\U0001F600";
        char.IsHighSurrogate(ReviewGuestText.Excerpt(emoji, 80)[^2]).Should().BeFalse("a surrogate pair is never split");
    }
}
