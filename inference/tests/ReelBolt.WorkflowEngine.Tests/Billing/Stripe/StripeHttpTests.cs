using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripeKit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

public sealed class StripeHttpTests
{
    private static StripeHttp Http(StripeFake fake, List<TimeSpan>? delays = null)
        => new(new HttpClient(fake), () => Options(), (d, _) =>
        {
            delays?.Add(d);
            return System.Threading.Tasks.Task.CompletedTask;
        }, () => 0.5);

    // ------------------------------------------------------------------ form encoder

    [Fact]
    public void Form_FlattensNestedKeys_ArraysAndScalars()
    {
        StripeForm form = new StripeForm()
            .Add("a", new Dictionary<string, object?> { ["b"] = new[] { new Dictionary<string, object?> { ["c"] = 1 } } })
            .Add("x", new[] { "one", "two" })
            .Add("flag", true)
            .Add("off", false)
            .Add("skipped", null)
            .Add("empty", "")
            .Add("n", 12L)
            .Add("d", 1.5m);

        form.Select(p => $"{p.Key}={p.Value}").Should().Equal("a[b][0][c]=1", "x[]=one", "x[]=two", "flag=true", "off=false", "empty=", "n=12", "d=1.5");
    }

    [Fact]
    public void Form_NestedNullsInsideADictionaryAreSkippedToo()
        => new StripeForm().Add("m", new Dictionary<string, object?> { ["k"] = null, ["v"] = "1" }).Select(p => p.Key).Should().Equal("m[v]");

    // ------------------------------------------------------------------ headers

    [Fact]
    public async Task EveryRequest_CarriesBearerAndTheApiVersion_AndOnlyPostsMayCarryAnIdempotencyKey()
    {
        StripeFake fake = new StripeFake().On("GET", "/v1/x", new { ok = 1 }).On("POST", "/v1/x", new { ok = 1 });
        StripeHttp http = Http(fake);

        await http.GetAsync("/v1/x", new StripeForm().Add("k", "v w"), default);
        await http.PostAsync("/v1/x", new StripeForm().Add("a", "b"), "key-1", default);
        await http.PostAsync("/v1/x", new StripeForm().Add("a", "b"), null, default);

        fake.Calls.Should().OnlyContain(c => c.Authorization == $"Bearer {ApiKey}" && c.Version == "2026-09-30.endive");
        fake.Calls[0].Q("k").Should().Be("v w");
        fake.Calls[0].IdempotencyKey.Should().BeNull();
        fake.Calls[1].IdempotencyKey.Should().Be("key-1");
        fake.Calls[2].IdempotencyKey.Should().BeNull();
        fake.Calls[1].F("a").Should().Be("b");
    }

    // ------------------------------------------------------------------ errors

    [Fact]
    public async Task CardError402_IsATypedRefusal_WithCodeStatusAndDetail()
    {
        StripeFake fake = new StripeFake().Error("POST", "/v1/x", 402, "card_declined", "Your card was declined.", "card_error");

        BillingProviderException ex = (await Http(fake).Invoking(h => h.PostAsync("/v1/x", new StripeForm(), null, default)).Should().ThrowAsync<BillingProviderException>()).Which;

        ex.Code.Should().Be("card_declined");
        ex.Status.Should().Be(402);
        ex.Detail.Should().Be("Your card was declined.");
        fake.Calls.Should().ContainSingle("4xx is never retried");
    }

    [Theory]
    [InlineData(404, "resource_missing")]
    [InlineData(409, "idempotency_error")]
    [InlineData(400, "parameter_invalid_empty")]
    public async Task OtherClientErrors_AreNeverRetried(int status, string code)
    {
        StripeFake fake = new StripeFake().Error("GET", "/v1/x", status, code);

        BillingProviderException ex = (await Http(fake).Invoking(h => h.GetAsync("/v1/x", null, default)).Should().ThrowAsync<BillingProviderException>()).Which;

        ex.Status.Should().Be(status);
        ex.Code.Should().Be(code);
        fake.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task ErrorWithoutACode_FallsBackToTheType_AndANonJsonBodyToAGenericCode()
    {
        StripeFake typed = new StripeFake().On("GET", "/v1/x", new { error = new { type = "invalid_request_error", message = "m" } }, 400);
        (await Http(typed).Invoking(h => h.GetAsync("/v1/x", null, default)).Should().ThrowAsync<BillingProviderException>()).Which.Code.Should().Be("invalid_request_error");

        StripeFake html = new StripeFake().On("GET", "/v1/x", "<html>bad</html>", 400);
        (await Http(html).Invoking(h => h.GetAsync("/v1/x", null, default)).Should().ThrowAsync<BillingProviderException>()).Which.Code.Should().Be("stripe_error");
    }

    [Fact]
    public async Task TheApiKeyNeverAppearsInAnExceptionMessage_EvenWhenStripeEchoesIt()
    {
        StripeFake fake = new StripeFake().Error("GET", "/v1/x", 401, "api_key_invalid", $"Invalid API Key provided: {ApiKey}");

        BillingProviderException ex = (await Http(fake).Invoking(h => h.GetAsync("/v1/x", null, default)).Should().ThrowAsync<BillingProviderException>()).Which;

        ex.Message.Should().NotContain(ApiKey);
        ex.Detail.Should().NotContain(ApiKey).And.Contain("[redacted]");
    }

    // ------------------------------------------------------------------ retries

    [Fact]
    public async Task RateLimit429_IsRetriedWithJitteredBackoff_ThenSucceeds()
    {
        List<TimeSpan> delays = [];
        StripeFake fake = new StripeFake()
            .Error("GET", "/v1/x", 429, "rate_limit")
            .Error("GET", "/v1/x", 429, "rate_limit")
            .On("GET", "/v1/x", new { id = "ok" });

        JsonElement r = await Http(fake, delays).GetAsync("/v1/x", null, default);

        r.GetProperty("id").GetString().Should().Be("ok");
        fake.Calls.Should().HaveCount(3);
        delays.Should().Equal(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task LockTimeout_IsRetriedEvenOnA4xxStatus()
    {
        StripeFake fake = new StripeFake().Error("POST", "/v1/x", 409, "lock_timeout").On("POST", "/v1/x", new { id = "ok" });

        JsonElement r = await Http(fake).PostAsync("/v1/x", new StripeForm(), "k", default);

        r.GetProperty("id").GetString().Should().Be("ok");
        fake.Calls.Should().HaveCount(2);
        fake.Calls[1].IdempotencyKey.Should().Be("k", "the retry reuses the key");
    }

    [Fact]
    public async Task Server500_IsRetriedThreeTimes_ThenAnHttpRequestException_WithoutTheKey()
    {
        StripeFake fake = new StripeFake().Error("GET", "/v1/x", 500, "api_error", "boom", "api_error");

        HttpRequestException ex = (await Http(fake).Invoking(h => h.GetAsync("/v1/x", null, default)).Should().ThrowAsync<HttpRequestException>()).Which;

        ex.Should().NotBeOfType<BillingProviderException>();
        ex.Message.Should().NotContain(ApiKey);
        fake.Calls.Should().HaveCount(4, "one attempt plus three retries");
    }

    [Fact]
    public async Task Server5xx_OnAPostWithoutAnIdempotencyKey_IsNotRetried_Because_ItMayHaveBeenProcessed()
    {
        StripeFake fake = new StripeFake().Error("POST", "/v1/x", 503, "api_error").On("POST", "/v1/x", new { id = "ok" });

        await Http(fake).Invoking(h => h.PostAsync("/v1/x", new StripeForm(), null, default)).Should().ThrowAsync<HttpRequestException>();

        fake.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task RateLimitAndLockTimeout_AreRetriedOnAPostWithoutAKey_BecauseNothingWasProcessed()
    {
        StripeFake rate = new StripeFake().Error("POST", "/v1/x", 429, "rate_limit").On("POST", "/v1/x", new { id = "ok" });
        await Http(rate).PostAsync("/v1/x", new StripeForm(), null, default);
        rate.Calls.Should().HaveCount(2);

        StripeFake lockFake = new StripeFake().Error("POST", "/v1/x", 409, "lock_timeout").On("POST", "/v1/x", new { id = "ok" });
        await Http(lockFake).PostAsync("/v1/x", new StripeForm(), null, default);
        lockFake.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Server5xx_OnAKeyedPost_IsRetried()
    {
        StripeFake fake = new StripeFake().Error("POST", "/v1/x", 503, "api_error").On("POST", "/v1/x", new { id = "ok" });

        await Http(fake).PostAsync("/v1/x", new StripeForm(), "k", default);

        fake.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ARetryRebuildsTheRequest_SoTheBodyIsStillSent()
    {
        StripeFake fake = new StripeFake().Error("POST", "/v1/x", 503, "api_error").On("POST", "/v1/x", new { id = "ok" });

        await Http(fake).PostAsync("/v1/x", new StripeForm().Add("a", "b"), "k", default);

        fake.Calls.Should().HaveCount(2).And.OnlyContain(c => c.F("a") == "b");
    }

    [Fact]
    public async Task NetworkFailure_OnAPostWithoutAnIdempotencyKey_IsNotRetried()
    {
        int sends = 0;
        HttpClient client = new(new FailingHandler(() => sends++));
        StripeHttp http = new(client, () => Options(), (_, _) => System.Threading.Tasks.Task.CompletedTask);

        await http.Invoking(h => h.PostAsync("/v1/x", new StripeForm(), null, default)).Should().ThrowAsync<HttpRequestException>();
        sends.Should().Be(1);

        sends = 0;
        await http.Invoking(h => h.PostAsync("/v1/x", new StripeForm(), "k", default)).Should().ThrowAsync<HttpRequestException>();
        sends.Should().Be(4, "with a key a network failure is safe to repeat");
    }

    private sealed class FailingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            onSend();
            throw new HttpRequestException("connection reset");
        }
    }
}
