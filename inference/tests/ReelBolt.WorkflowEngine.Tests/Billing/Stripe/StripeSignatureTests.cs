// Fixtures shaped from docs.stripe.com 2026-10-26, replace with redacted sandbox captures after the operator spike.
using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

public sealed class StripeSignatureTests
{
    private const string Body = """{"id":"evt_1"}""";
    private static readonly DateTimeOffset Now = new(StripePayloads.Now, TimeSpan.Zero);
    private static readonly long T = StripePayloads.Unix(StripePayloads.Now);
    private static readonly IReadOnlyList<string> Secrets = [StripePayloads.Secret];

    private static string Hex(string header) => header[(header.IndexOf("v1=", StringComparison.Ordinal) + 3)..];

    private static void Verify(string? header, string body = Body, IReadOnlyList<string>? secrets = null, int tolerance = 300)
        => StripeSignature.Verify(body, header, secrets ?? Secrets, tolerance, Now);

    [Fact]
    public void Valid_Passes() => FluentActions.Invoking(() => Verify(StripeSignature.Sign(Body, StripePayloads.Secret, T))).Should().NotThrow();

    [Fact]
    public void TwoV1Values_OneValid_Passes()
    {
        string good = StripeSignature.Sign(Body, StripePayloads.Secret, T);
        string header = $"t={T},v1={new string('a', 64)},{good[(good.IndexOf(',') + 1)..]},v0={new string('b', 64)}";
        FluentActions.Invoking(() => Verify(header)).Should().NotThrow();
    }

    [Fact]
    public void RotationOverlap_PreviousSecretStillVerifies()
    {
        string header = StripeSignature.Sign(Body, StripePayloads.PreviousSecret, T);
        FluentActions.Invoking(() => Verify(header, secrets: [StripePayloads.Secret, StripePayloads.PreviousSecret])).Should().NotThrow();
        FluentActions.Invoking(() => Verify(header)).Should().Throw<WebhookVerificationException>();
    }

    [Fact]
    public void TamperedBody_IsRejected()
        => FluentActions.Invoking(() => Verify(StripeSignature.Sign(Body, StripePayloads.Secret, T), """{"id":"evt_2"}"""))
            .Should().Throw<WebhookVerificationException>().WithMessage("Signature mismatch.");

    [Fact]
    public void StaleTimestamp_IsRejected()
        => FluentActions.Invoking(() => Verify(StripeSignature.Sign(Body, StripePayloads.Secret, T - 301)))
            .Should().Throw<WebhookVerificationException>().WithMessage("*tolerance*");

    [Fact]
    public void FutureTimestamp_IsRejected()
        => FluentActions.Invoking(() => Verify(StripeSignature.Sign(Body, StripePayloads.Secret, T + 301)))
            .Should().Throw<WebhookVerificationException>().WithMessage("*tolerance*");

    [Fact]
    public void TimestampAtTheEdgeOfTheTolerance_Passes()
        => FluentActions.Invoking(() => Verify(StripeSignature.Sign(Body, StripePayloads.Secret, T - 300))).Should().NotThrow();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1=abcd")]
    [InlineData("t=notanumber,v1=abcd")]
    [InlineData("t=1")]
    public void MissingOrMalformedHeader_IsRejected(string? header)
        => FluentActions.Invoking(() => Verify(header)).Should().Throw<WebhookVerificationException>();

    [Fact]
    public void OnlyV0_IsRejected()
        => FluentActions.Invoking(() => Verify($"t={T},v0={new string('c', 64)}"))
            .Should().Throw<WebhookVerificationException>().WithMessage("*no v1*");

    [Fact]
    public void NoSecretConfigured_IsRejected()
        => FluentActions.Invoking(() => Verify(StripeSignature.Sign(Body, StripePayloads.Secret, T), secrets: []))
            .Should().Throw<WebhookVerificationException>();

    [Fact]
    public void WrongSecret_IsRejected_AndTheMessageLeaksNothing()
    {
        string header = StripeSignature.Sign(Body, "whsec_attacker", T);
        Exception ex = FluentActions.Invoking(() => Verify(header)).Should().Throw<WebhookVerificationException>().Which;
        ex.Message.Should().NotContain(StripePayloads.Secret).And.NotContain(Hex(header)).And.NotContain("whsec_");
    }
}
