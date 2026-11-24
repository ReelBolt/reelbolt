using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Sk_token_redacted()
    {
        (string text, int count) = SecretRedactor.Redact("key: sk-" + "abcdefghijklmnopqrstuvwxyz123456");

        text.Should().Contain("[redacted]");
        count.Should().Be(1);
        text.Should().NotContain("sk-");
    }

    [Fact]
    public void Ghp_and_github_pat_and_akia_redacted()
    {
        string input = "a ghp_" + "abcdefghijklmnopqrstuvwxyz1234 b github_pat_"
            + new string('a', 50)
            + " c AKIA" + "IOSFODNN7EXAMPLE";

        (string text, int count) = SecretRedactor.Redact(input);

        count.Should().Be(3);
        text.Should().NotContain("ghp_");
        text.Should().NotContain("github_pat_");
        text.Should().NotContain("AKIA");
    }

    [Fact]
    public void Pem_header_redacted()
    {
        (string text, int count) = SecretRedactor.Redact("-----BEGIN RSA " + "PRIVATE KEY-----");

        text.Should().Be("[redacted]");
        count.Should().Be(1);
    }

    [Fact]
    public void Slack_token_redacted()
    {
        (string text, int count) = SecretRedactor.Redact("xoxb-" + "1234567890abcdef");

        text.Should().Be("[redacted]");
        count.Should().Be(1);
    }

    [Fact]
    public void No_match_is_unchanged()
    {
        (string text, int count) = SecretRedactor.Redact("nothing to see here");

        text.Should().Be("nothing to see here");
        count.Should().Be(0);
    }

    [Fact]
    public void Null_and_empty()
    {
        (string nullText, int nullCount) = SecretRedactor.Redact(null!);
        nullText.Should().BeEmpty();
        nullCount.Should().Be(0);

        (string emptyText, int emptyCount) = SecretRedactor.Redact(string.Empty);
        emptyText.Should().BeEmpty();
        emptyCount.Should().Be(0);
    }

    [Fact]
    public void Hostile_input_does_not_throw()
    {
        string big = new string('a', 1_000_000);
        (string bigText, int bigCount) = SecretRedactor.Redact(big);
        bigText.Should().Be(big);
        bigCount.Should().Be(0);

        string fragments = string.Concat(Enumerable.Repeat("-----BEGIN ", 5000));
        (string fragmentText, int fragmentCount) = SecretRedactor.Redact(fragments);
        fragmentText.Should().Be(fragments);
        fragmentCount.Should().Be(0);
    }

    public static TheoryData<string> SecretShapes => new()
    {
        "token eyJhbGciOiJIUzI1NiJ9." + "eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk end",
        "Authorization: Bearer abcdEFGH0123456789abcdEFGH0123456789",
        "postgres://admin:hunter2hunter2@db.internal:5432/app",
        "stripe sk_live_" + "abcdefghijklmnopqrstuvwx",
        "stripe rk_live_" + "abcdefghijklmnopqrstuvwx",
        "maps AIza" + "SyA1234567890abcdefghijklmnopqrstuv",
        "figma fig" + "d_abcdefghijklmnopqrstuvwxyz0123456789",
        "mail SG." + "abcdefghijklmnop1234.abcdefghijklmnop5678",
        "oauth gho_" + "abcdefghijklmnopqrstuvwxyz0123456789",
        "server ghs_" + "abcdefghijklmnopqrstuvwxyz0123456789",
        "sts ASIA" + "IOSFODNN7EXAMPLE",
        "aws_secret" + "_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
    };

    [Theory]
    [MemberData(nameof(SecretShapes))]
    public void Each_secret_shape_is_redacted(string input)
    {
        (string text, int count) = SecretRedactor.Redact(input);

        count.Should().BeGreaterThan(0);
        text.Should().Contain("[redacted]");
        text.Should().NotContain("eyJ");
        text.Should().NotContain("hunter2");
        text.Should().NotContain("abcdEFGH0123456789");
        text.Should().NotContain("sk_live_");
        text.Should().NotContain("rk_live_");
        text.Should().NotContain("AIzaSy");
        text.Should().NotContain("figd_abc");
        text.Should().NotContain("SG.abc");
        text.Should().NotContain("gho_abc");
        text.Should().NotContain("ghs_abc");
        text.Should().NotContain("ASIAIOSF");
        text.Should().NotContain("wJalrXUtnFEMI");
    }

    [Fact]
    public void Pem_block_is_redacted_through_the_end_line()
    {
        string input = "before\n-----BEGIN RSA " + "PRIVATE KEY-----\nMIIEowIBAAKCAQEAxyz\nabcdefghijklmnop\n-----END RSA PRIVATE KEY-----\nafter";

        (string text, int count) = SecretRedactor.Redact(input);

        count.Should().Be(1);
        text.Should().Be("before\n[redacted]\nafter");
    }

    [Fact]
    public void Ordinary_prose_with_secret_like_words_is_left_alone()
    {
        string input = "Use Bearer authentication; keep the secret safe. See https://example.com/docs and user@example.com.";

        SecretRedactor.Redact(input).text.Should().Be(input);
    }
}
