#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// The webhook signature check (docs/github-app.md "Security"). Everything here is about the one
/// property that matters: an unsigned or wrongly-signed delivery must never be accepted, because a
/// webhook that trusts its caller is an unauthenticated write primitive into a customer's project.
/// </summary>
public class GitHubWebhookSignatureTests
{
    private static GitHubWebhookVerifier Verifier(string? secret = GitHubTestData.WebhookSecret) =>
        new(Options.Create(new GitHubOptions
        {
            AppId = "123456",
            PrivateKeyPem = "unused-in-tests",
            WebhookSecret = secret
        }));

    private static string Sign(string body, string secret = GitHubTestData.WebhookSecret)
    {
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    [Fact]
    public void A_signature_github_would_send_verifies()
    {
        const string body = """{"zen":"Keep it logically awesome."}""";

        Verifier().TryVerify(body, Sign(body)).Should().BeTrue();
    }

    [Fact]
    public void An_unsigned_delivery_is_rejected()
    {
        const string body = """{"ref":"refs/heads/main"}""";

        GitHubWebhookVerifier verifier = Verifier();

        verifier.TryVerify(body, signatureHeader: null).Should().BeFalse();
        verifier.TryVerify(body, string.Empty).Should().BeFalse();
        verifier.TryVerify(body, "   ").Should().BeFalse();
    }

    [Fact]
    public void A_body_that_changed_after_signing_is_rejected()
    {
        const string original = """{"after":"aaaa"}""";
        const string tampered = """{"after":"bbbb"}""";

        Verifier().TryVerify(tampered, Sign(original)).Should().BeFalse(
            "the signature covers the exact bytes of the body, which is why the endpoint reads the raw body");
    }

    [Fact]
    public void A_signature_from_another_secret_is_rejected()
    {
        const string body = """{"after":"aaaa"}""";

        Verifier().TryVerify(body, Sign(body, "the-wrong-secret")).Should().BeFalse();
    }

    [Theory]
    [InlineData("sha256=")]
    [InlineData("sha256=abc")]
    [InlineData("sha256=" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]  // 63
    [InlineData("sha256=" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")] // 65
    [InlineData("sha256=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("sha1=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void A_malformed_signature_header_is_rejected(string header)
    {
        const string body = """{"after":"aaaa"}""";

        Verifier().TryVerify(body, header).Should().BeFalse();
    }

    [Fact]
    public void Nothing_verifies_when_the_deployment_has_no_webhook_secret()
    {
        const string body = """{"after":"aaaa"}""";
        GitHubWebhookVerifier verifier = Verifier(secret: null);

        verifier.IsConfigured.Should().BeFalse(
            "an endpoint that cannot verify must report that it cannot verify, so the caller can answer 503 "
            + "instead of accepting anything");

        // Even a signature that would be correct for some secret must not be accepted, because there is
        // no secret to check it against. This is the fail-closed direction.
        verifier.TryVerify(body, Sign(body)).Should().BeFalse();
    }

    [Fact]
    public void Upper_case_hex_is_accepted_because_it_is_the_same_digest()
    {
        const string body = """{"after":"aaaa"}""";

        Verifier().TryVerify(body, Sign(body).ToUpperInvariant().Replace("SHA256=", "sha256=", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    /// <summary>
    /// The comparison is constant time, and the source says so. A byte-wise <c>==</c> over the digest
    /// leaks the correct HMAC one byte at a time to an attacker who can time the endpoint, which is a
    /// forgeable-signature bug rather than a style preference — so this is asserted against the source,
    /// not just observed behaviourally (timing is not measurable in a unit test).
    /// </summary>
    [Fact]
    public void The_digest_comparison_is_constant_time()
    {
        string source = ReadServiceSource("GitHubWebhookVerifier.cs");

        source.Should().Contain("CryptographicOperations.FixedTimeEquals");
        source.Should().NotContain("SequenceEqual");
    }

    private static string ReadServiceSource(string fileName)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ReelBolt.Inference.Api")))
            dir = Path.GetDirectoryName(dir);

        dir.Should().NotBeNull("the test must locate the Inference API's source tree");
        string path = Path.Combine(dir!, "src", "ReelBolt.Inference.Api", "Services", "GitHub", fileName);
        File.Exists(path).Should().BeTrue($"expected the source at {path}");
        return File.ReadAllText(path);
    }
}
