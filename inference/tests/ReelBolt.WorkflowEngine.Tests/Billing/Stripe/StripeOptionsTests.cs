using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

public sealed class StripeOptionsTests
{
    private static ValidateOptionsResult Validate(StripeOptions o, string provider = "Paddle")
        => new StripeOptionsValidator(Options.Create(new BillingProviderOptions { Provider = provider })).Validate(null, o);

    [Fact]
    public void Unconfigured_Boots_WhenPaddleIsSelected() => Validate(new StripeOptions()).Succeeded.Should().BeTrue();

    [Fact]
    public void StripeSelected_WithoutKey_Fails() => Validate(new StripeOptions(), "stripe").Failed.Should().BeTrue();

    [Theory]
    [InlineData("sk_test_abc", false)]
    [InlineData("rk_test_abc", false)]
    [InlineData("sk_live_abc", true)]
    [InlineData("rk_live_abc", true)]
    public void AcceptedKeyPrefixes_DeriveTheMode(string key, bool live)
    {
        var o = new StripeOptions { ApiKey = key, WebhookSecret = "whsec_x", ReturnBaseUrl = "https://app.example.com" };
        Validate(o).Succeeded.Should().BeTrue();
        o.IsLiveMode.Should().Be(live);
        o.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void PublishableKey_IsRejected_WithoutEchoingIt()
    {
        var o = new StripeOptions { ApiKey = "pk_test_" + "SECRETVALUE", WebhookSecret = "whsec_" + "x" };
        ValidateOptionsResult r = Validate(o);
        r.Failed.Should().BeTrue();
        r.FailureMessage.Should().NotContain("SECRETVALUE").And.NotContain("pk_test_SECRET");
    }

    [Fact]
    public void BadWebhookSecret_IsRejected_WithoutEchoingIt()
    {
        ValidateOptionsResult r = Validate(new StripeOptions { ApiKey = "sk_test_a", WebhookSecret = "nope_SECRETVALUE" });
        r.Failed.Should().BeTrue();
        r.FailureMessage.Should().NotContain("SECRETVALUE");
    }

    [Fact]
    public void KeyWithoutWebhookSecret_IsRejected() => Validate(new StripeOptions { ApiKey = "sk_test_a" }).Failed.Should().BeTrue();

    [Fact]
    public void NonIntegerPackKey_IsRejected()
        => Validate(new StripeOptions { ApiKey = "sk_test_a", WebhookSecret = "whsec_x", TopUpPriceIds = { ["lots"] = "price_1" } }).Failed.Should().BeTrue();

    private static StripeOptions Valid() => new() { ApiKey = "sk_test_a", WebhookSecret = "whsec_x", ReturnBaseUrl = "https://app.example.com" };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("app.example.com")]
    [InlineData("/relative")]
    [InlineData("http://app.example.com")]
    [InlineData("ftp://app.example.com")]
    [InlineData("https://user:pw@app.example.com")]
    public void ReturnBaseUrl_MustBeAbsoluteHttps(string? url)
    {
        StripeOptions o = Valid();
        o.ReturnBaseUrl = url;
        ValidateOptionsResult r = Validate(o);
        r.Failed.Should().BeTrue();
        r.FailureMessage.Should().Contain("ReturnBaseUrl").And.NotContain("sk_test_a").And.NotContain("whsec_x");
    }

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:3000")]
    [InlineData("https://app.example.com/")]
    public void ReturnBaseUrl_AcceptsHttpsAndLocalHttp(string url)
    {
        StripeOptions o = Valid();
        o.ReturnBaseUrl = url;
        Validate(o).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://api.stripe.com.evil.example")]
    [InlineData("https://evil.example/api.stripe.com")]
    [InlineData("https://api.stripe.com@evil.example")]
    [InlineData("https://user@api.stripe.com")]
    [InlineData("http://api.stripe.com")]
    [InlineData("http://localhost:12111")]
    [InlineData("https://api.stripe.com:8443")]
    public void LiveKey_RequiresTheExactStripeApiOrigin(string baseUrl)
    {
        StripeOptions o = Valid();
        o.ApiKey = "sk_live_a";
        o.BaseUrl = baseUrl;
        ValidateOptionsResult r = Validate(o);
        r.Failed.Should().BeTrue();
        r.FailureMessage.Should().Contain("BaseUrl").And.NotContain("sk_live_a");
    }

    [Fact]
    public void BaseUrl_AcceptsOneTrailingSlash_AndNormalisesIt()
    {
        StripeOptions o = Valid();
        o.ApiKey = "sk_live_a";
        o.BaseUrl = "https://api.stripe.com/";
        Validate(o).Succeeded.Should().BeTrue();
        o.BaseUrl.Should().Be("https://api.stripe.com");

        o.BaseUrl = "https://api.stripe.com//";
        Validate(o).Failed.Should().BeTrue("only ONE trailing slash is tolerated");
        o.BaseUrl = "https://api.stripe.com/v1";
        Validate(o).Failed.Should().BeTrue();
    }

    [Fact]
    public void LiveKey_WithTheRealApi_AndTestKey_WithAMock_AreAccepted()
    {
        StripeOptions live = Valid();
        live.ApiKey = "sk_live_a";
        Validate(live).Succeeded.Should().BeTrue();

        StripeOptions test = Valid();
        test.BaseUrl = "http://localhost:12111";
        Validate(test).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(900, true)]
    [InlineData(901, false)]
    [InlineData(0, false)]
    public void ToleranceSeconds_IsBounded(int seconds, bool ok)
    {
        StripeOptions o = Valid();
        o.ToleranceSeconds = seconds;
        ValidateOptionsResult r = Validate(o);
        r.Succeeded.Should().Be(ok);
        if (!ok) r.FailureMessage.Should().Contain("ToleranceSeconds");
    }

    [Fact]
    public void Defaults()
    {
        var o = new StripeOptions();
        o.BaseUrl.Should().Be("https://api.stripe.com");
        o.ToleranceSeconds.Should().Be(300);
        o.MaxSelfServeSeats.Should().Be(200);
        o.ManagedPayments.Should().BeFalse();
        StripeOptions.ApiVersion.Should().Be("2026-09-30.endive");
        o.IsConfigured.Should().BeFalse();
    }
}
