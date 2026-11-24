using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Pins the Higgsfield price oracle (the async price oracle) — the async, cached pricing path that replaced
/// "every Higgsfield model is unofferable".
/// <para>
/// Entirely offline, and it has to be: there is no Higgsfield credential anywhere and none is to be
/// requested. Every test drives the client through an <see cref="HttpMessageHandler"/> stub, so the
/// vendor boundary is faked and no test can reach the network even by accident — a test that tried
/// would hit a handler that either answers from a literal or throws.
/// </para>
/// <para>
/// The load-bearing claims this file exists to hold:
/// <list type="bullet">
/// <item>a quote costs exactly ONE request per distinct configuration, and a repeat costs none;</item>
/// <item>a failure or a timeout is FAIL-CLOSED — it throws, and it can never be read as a price of
/// zero, because no representation of an unpriced quote exists;</item>
/// <item><see cref="IVideoGenerationClient.Describe"/> is still pure and no-network, so the offline
/// contract it always had is unchanged rather than quietly broken.</item>
/// </list>
/// </para>
/// </summary>
public class HiggsfieldVideoGenerationPriceOracleTests
{
    private const string BaseUrl = "https://api.higgsfield.ai";
    private const string KeyId = "hf-key-id-abc";
    private const string KeySecret = "hf-key-secret-xyz";
    private const string EndpointId = "kling-video/v3.0/std/text-to-video";

    /// <summary>
    /// A controllable clock. Deliberately hand-rolled rather than <c>Microsoft.Extensions.TimeProvider.Testing</c>:
    /// this initiative may not add a NuGet package, and a TTL test that sleeps is a TTL test that
    /// flakes.
    /// </summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Records every request the client sends; the response comes from <c>respond</c>.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Auth, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                body));
            return await respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (HiggsfieldVideoGenerationClient Client, RecordingHandler Handler, FakeTimeProvider Clock) ClientFor(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? priceOracleTimeout = null)
    {
        RecordingHandler handler = new(respond);
        HttpClient http = new(handler) { BaseAddress = new Uri(BaseUrl) };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        return (new HiggsfieldVideoGenerationClient(
            http, KeyId + ":" + KeySecret, probeTimeout: null,
            priceOracleTimeout: priceOracleTimeout ?? TimeSpan.FromSeconds(10),
            timeProvider: clock), handler, clock);
    }

    private static (HiggsfieldVideoGenerationClient Client, RecordingHandler Handler) ClientThatSendsNothing()
    {
        RecordingHandler handler = new((_, _) =>
            throw new InvalidOperationException("No HTTP request was expected, but one was sent."));
        HttpClient http = new(handler) { BaseAddress = new Uri(BaseUrl) };
        return (new HiggsfieldVideoGenerationClient(http, KeyId + ":" + KeySecret), handler);
    }

    private static VideoGenerationPriceQuery Query(
        string model = EndpointId, string resolution = "1080P", string ratio = "16:9", int duration = 5) =>
        new(model, resolution, ratio, duration);

    // -----------------------------------------------------------------------------------------
    // The happy path: one bounded, authenticated request, and the vendor's own numbers come back
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task QuoteAsync_posts_to_the_estimate_endpoint_and_returns_the_vendors_own_price()
    {
        var (client, handler, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":42,"usd":1.25}""")));

        VideoGenerationPriceQuote quote = await client.QuoteAsync(Query(duration: 5), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        (HttpMethod Method, string Path, string Auth, string Body) sent = handler.Requests[0];
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Path.Should().Be("/estimate/" + EndpointId);
        sent.Auth.Should().Be($"Key {KeyId}:{KeySecret}", "the estimate is authenticated with the two-part Key scheme");
        sent.Body.Should().Be(
            """{"duration":5,"aspect_ratio":"16:9"}""",
            "the vendor documents /estimate taking the model parameters at the TOP LEVEL, with no "
            + "'input' wrapper (https://docs.higgsfield.ai/docs/concepts/billing-and-retention); the "
            + "submit's envelope is not evidence for the estimate's");

        quote.Source.Should().Be(VideoGenerationPriceSource.VendorEstimate);
        quote.TotalUsd.Should().Be(1.25m, "the vendor's own usd is authoritative and is not re-derived");
        quote.PricePerSecondUsd.Should().Be(0.25m);
        quote.Credits.Should().Be(42);
        quote.DurationSeconds.Should().Be(5);
    }

    // -----------------------------------------------------------------------------------------
    // usd is accepted as a JSON NUMBER or a JSON STRING
    //
    // The vendor's own documented envelope prints both members as strings —
    // {"credits": "1.500", "usd": "0.094"} (https://docs.higgsfield.ai/docs/concepts/billing-and-retention)
    // — so a number-only reader would reject every real answer and leave every Higgsfield model
    // unofferable, making the async price oracle a no-op against the live vendor. Accepting the string does
    // NOT loosen fail-closed: every rejection case is pinned by UnpriceableBodiesData below.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task QuoteAsync_accepts_usd_as_a_json_number()
    {
        var (client, _, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":42,"usd":1.25}""")));

        VideoGenerationPriceQuote quote = await client.QuoteAsync(Query(duration: 5), CancellationToken.None);

        quote.TotalUsd.Should().Be(1.25m);
        quote.PricePerSecondUsd.Should().Be(0.25m);
    }

    [Fact]
    public async Task QuoteAsync_accepts_usd_as_a_json_string_in_the_vendors_documented_envelope()
    {
        // Byte-for-byte the envelope the vendor's billing page prints, both members strings.
        var (client, _, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits": "1.500", "usd": "0.094"}""")));

        VideoGenerationPriceQuote quote = await client.QuoteAsync(Query(duration: 5), CancellationToken.None);

        quote.TotalUsd.Should().Be(0.094m, "the documented string form is the REAL vendor shape, not an edge case");
        quote.PricePerSecondUsd.Should().Be(0.0188m);
        quote.DurationSeconds.Should().Be(5);
    }

    [Fact]
    public async Task QuoteAsync_accepts_a_usd_string_with_surrounding_whitespace()
    {
        var (client, _, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"usd":" 0.094 "}""")));

        VideoGenerationPriceQuote quote = await client.QuoteAsync(Query(duration: 5), CancellationToken.None);

        quote.TotalUsd.Should().Be(0.094m, "a padded string is trimmed, not rejected");
    }

    [Fact]
    public async Task QuoteAsync_reads_a_usd_string_under_the_invariant_culture_only()
    {
        // The failure the invariant culture exists to prevent: under a comma-decimal culture "0.094"
        // could be read as 94, which is a WRONG price rather than a missing one. Invariant parsing
        // makes the string arm culture-independent, so this asserts a stable value regardless of the
        // machine's culture.
        var (client, _, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"usd":"0.094"}""")));

        VideoGenerationPriceQuote quote = await client.QuoteAsync(Query(duration: 1), CancellationToken.None);

        quote.TotalUsd.Should().Be(0.094m, "invariant parsing: the decimal point is a decimal point");
    }

    [Fact]
    public async Task QuoteAsync_accepts_a_usd_string_that_is_an_integer_literal()
    {
        var (client, _, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"usd":"2"}""")));

        VideoGenerationPriceQuote quote = await client.QuoteAsync(Query(duration: 4), CancellationToken.None);

        quote.TotalUsd.Should().Be(2m);
        quote.PricePerSecondUsd.Should().Be(0.5m);
    }

    [Fact]
    public async Task QuoteAsync_treats_a_string_and_a_number_carrying_the_same_price_identically()
    {
        var (numberClient, _, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"usd":0.094}""")));
        var (stringClient, _, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"usd":"0.094"}""")));

        VideoGenerationPriceQuote fromNumber = await numberClient.QuoteAsync(Query(), CancellationToken.None);
        VideoGenerationPriceQuote fromString = await stringClient.QuoteAsync(Query(), CancellationToken.None);

        fromString.TotalUsd.Should().Be(fromNumber.TotalUsd);
        fromString.PricePerSecondUsd.Should().Be(fromNumber.PricePerSecondUsd);
    }

    [Fact]
    public async Task QuoteAsync_never_sends_the_prompt()
    {
        var (client, handler, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":1,"usd":0.5}""")));

        await client.QuoteAsync(Query(), CancellationToken.None);

        handler.Requests[0].Body.Should().NotContain("prompt",
            "the prompt is not part of VideoGenerationPriceQuery and the quote is cached under a "
            + "configuration key: sending it would make every priced clip a cache miss, i.e. a "
            + "per-shot network call on the pre-submit pricing hot path, which the async price oracle "
            + "condition (2) forbids");
    }

    // -----------------------------------------------------------------------------------------
    // The cache: one request per configuration, not one per call
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task QuoteAsync_serves_a_repeat_query_from_cache_without_another_request()
    {
        var (client, handler, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":1,"usd":0.75}""")));

        VideoGenerationPriceQuote first = await client.QuoteAsync(Query(), CancellationToken.None);
        VideoGenerationPriceQuote second = await client.QuoteAsync(Query(), CancellationToken.None);

        handler.Requests.Should().ContainSingle(
            "no uncached per-call pricing on a hot path: pricing a whole plan must not price per take");
        second.Should().Be(first);
    }

    [Fact]
    public async Task QuoteAsync_re_queries_only_once_the_success_ttl_has_elapsed()
    {
        var (client, handler, clock) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":1,"usd":0.75}""")));

        await client.QuoteAsync(Query(), CancellationToken.None);

        clock.Now = clock.Now.Add(HiggsfieldVideoGenerationClient.PriceQuoteTtl).AddSeconds(-1);
        await client.QuoteAsync(Query(), CancellationToken.None);
        handler.Requests.Should().ContainSingle("the quote is still inside its TTL, to the second");

        clock.Now = clock.Now.AddSeconds(2);
        await client.QuoteAsync(Query(), CancellationToken.None);
        handler.Requests.Should().HaveCount(2, "past the TTL the vendor is asked again rather than trusted forever");
    }

    [Fact]
    public async Task QuoteAsync_keeps_distinct_configurations_apart()
    {
        var (client, handler, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":1,"usd":0.75}""")));

        await client.QuoteAsync(Query(duration: 5), CancellationToken.None);
        await client.QuoteAsync(Query(duration: 10), CancellationToken.None);
        await client.QuoteAsync(Query(ratio: "9:16"), CancellationToken.None);

        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Body.Should().Contain("\"duration\":5");
        handler.Requests[1].Body.Should().Contain("\"duration\":10");
        handler.Requests[2].Body.Should().Contain("\"aspect_ratio\":\"9:16\"");
    }

    [Fact]
    public async Task QuoteAsync_cache_key_is_injective_so_a_separator_in_a_field_cannot_collide()
    {
        // The concrete collision a naive "join the fields with |" would produce: these two
        // configurations both flatten to "m|x|y|z|5". Serving one configuration's price for another
        // is the single wrong answer this cache must never give, which is why the key length-prefixes
        // every component.
        var (client, handler, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":1,"usd":0.75}""")));

        await client.QuoteAsync(Query(model: "m", resolution: "x|y", ratio: "z"), CancellationToken.None);
        await client.QuoteAsync(Query(model: "m", resolution: "x", ratio: "y|z"), CancellationToken.None);

        handler.Requests.Should().HaveCount(2, "two different configurations are two different cache keys");
        handler.Requests.Should().OnlyContain(sent => sent.Path == "/estimate/m");
        handler.Requests[0].Body.Should().Contain("\"aspect_ratio\":\"z\"");
        handler.Requests[1].Body.Should().Contain("\"aspect_ratio\":\"y|z\"");
    }

    [Fact]
    public async Task QuoteAsync_cache_is_bounded_and_evicts_rather_than_growing_without_limit()
    {
        // A long-lived singleton client may be asked about many configurations over its lifetime, so
        // the cache has to have a ceiling. 300 distinct configurations against a 256-entry cap must
        // evict: the second pass therefore re-asks the vendor for SOME of them, but certainly not all
        // — the ones still inside the ceiling are served from cache.
        var (client, handler, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":1,"usd":0.75}""")));

        int overCap = HiggsfieldVideoGenerationClient.MaxPriceQuoteCacheEntries + 44;
        for (int i = 0; i < overCap; i++)
        {
            await client.QuoteAsync(Query(duration: i + 1), CancellationToken.None);
        }

        handler.Requests.Should().HaveCount(overCap, "every distinct configuration was priced exactly once");

        for (int i = 0; i < overCap; i++)
        {
            await client.QuoteAsync(Query(duration: i + 1), CancellationToken.None);
        }

        int secondPass = handler.Requests.Count - overCap;
        secondPass.Should().BeGreaterThan(0, "the ceiling forced eviction, so not everything survived");
        secondPass.Should().BeLessThan(overCap, "the survivors were served from cache, not re-priced");
    }

    // -----------------------------------------------------------------------------------------
    // Fail-closed: no failure can ever read as a price of zero
    // -----------------------------------------------------------------------------------------

    public static TheoryData<string, string> UnpriceableBodiesData()
    {
        TheoryData<string, string> data = new()
        {
            { """{"credits":10,"usd":0}""", "a zero price" },
            { """{"credits":10,"usd":-1.5}""", "a negative price" },
            { """{"credits":10,"usd":"0"}""", "a zero price written as a string" },
            { """{"credits":10,"usd":"-1.5"}""", "a negative price written as a string" },
            { """{"credits":10,"usd":"free"}""", "a usd string that is not numeric" },
            { """{"credits":10,"usd":""}""", "an empty usd string" },
            { """{"credits":10,"usd":"   "}""", "a whitespace-only usd string" },
            { """{"credits":10,"usd":"1,25"}""", "a usd string using a comma as the decimal separator" },
            { """{"credits":10,"usd":"$0.09"}""", "a usd string carrying a currency symbol" },
            { """{"credits":10,"usd":"0.09 USD"}""", "a usd string carrying a unit suffix" },
            { """{"credits":10,"usd":"NaN"}""", "a usd string that is not a number" },
            { """{"credits":10}""", "a missing usd" },
            { """{"credits":10,"usd":null}""", "a null usd" },
            { """{"credits":10,"usd":{}}""", "a usd that is an object" },
            { """{"credits":10,"usd":[]}""", "a usd that is an array" },
            { """{"credits":10,"usd":true}""", "a usd that is a boolean" },
            { """[]""", "a root that is not an object" },
            { """not json at all""", "a body that is not JSON" },
            { "", "an empty body" }
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(UnpriceableBodiesData))]
    public async Task QuoteAsync_fails_closed_on_every_unpriceable_body(string body, string because)
    {
        var (client, _, _) = ClientFor((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body)));

        Func<Task> act = () => client.QuoteAsync(Query(), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>(
                $"the oracle must fail closed on {because}, never answer with a price of zero")
            .WithMessage("*unofferable rather than priced at zero*");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task QuoteAsync_fails_closed_on_a_non_success_status(HttpStatusCode status)
    {
        var (client, _, _) = ClientFor((_, _) => Task.FromResult(Json(status, """{"detail":"nope"}""")));

        Func<Task> act = () => client.QuoteAsync(Query(), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task A_vendor_or_network_failure_is_the_unavailable_exception_and_a_config_fault_is_not()
    {
        var (down, _, _) = ClientFor((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}")));
        await ((Func<Task>)(() => down.QuoteAsync(Query(), CancellationToken.None)))
            .Should().ThrowAsync<VideoGenerationPriceUnavailableException>("the vendor did not price it right now");

        var (slow, _, _) = ClientFor(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Json(HttpStatusCode.OK, "{}");
            },
            priceOracleTimeout: TimeSpan.FromMilliseconds(50));
        await ((Func<Task>)(() => slow.QuoteAsync(Query(), CancellationToken.None)))
            .Should().ThrowAsync<VideoGenerationPriceUnavailableException>();

        var (client, handler, _) = ClientFor((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        Func<Task> badId = () => client.QuoteAsync(Query(model: "https://evil.example/x"), CancellationToken.None);
        (await badId.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<VideoGenerationPriceUnavailableException>(
            "an endpoint id that could never be addressed is the step's own fault");
        handler.Requests.Should().BeEmpty("nothing left the process");
    }

    [Fact]
    public async Task QuoteAsync_reports_authentication_rejection_through_the_same_fail_closed_exception()
    {
        // The oracle deliberately does NOT get its own auth-rejection result type: Describe already
        // throws NotSupportedException for "cannot offer this model", an existing call site already
        // catches exactly that, and a second failure vocabulary would be a new catch to forget.
        var (client, _, _) = ClientFor((_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}")));

        Func<Task> act = () => client.QuoteAsync(Query(), CancellationToken.None);

        (await act.Should().ThrowAsync<NotSupportedException>())
            .WithMessage("*HTTP 401*");
    }

    [Fact]
    public async Task QuoteAsync_remembers_a_failure_for_the_failure_ttl_and_then_retries()
    {
        int attempts = 0;
        var (client, handler, clock) = ClientFor((_, _) =>
        {
            attempts++;
            return Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}"));
        });

        Func<Task> quote = () => client.QuoteAsync(Query(), CancellationToken.None);

        await quote.Should().ThrowAsync<NotSupportedException>();
        int afterFirst = attempts;

        await quote.Should().ThrowAsync<NotSupportedException>();
        attempts.Should().Be(afterFirst, "a broken oracle must not be hammered once per take");

        clock.Now = clock.Now.Add(HiggsfieldVideoGenerationClient.PriceQuoteFailureTtl).AddSeconds(1);
        await quote.Should().ThrowAsync<NotSupportedException>();
        attempts.Should().Be(afterFirst + 1, "the failure TTL is short so a recovered vendor is noticed quickly");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task QuoteAsync_fails_closed_when_the_oracle_times_out()
    {
        var (client, handler, _) = ClientFor(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Json(HttpStatusCode.OK, "{}");
            },
            priceOracleTimeout: TimeSpan.FromMilliseconds(50));

        Func<Task> act = () => client.QuoteAsync(Query(), CancellationToken.None);

        (await act.Should().ThrowAsync<NotSupportedException>())
            .WithMessage("*did not respond within*");
        handler.Requests.Should().ContainSingle("one attempt, then give up — a price lookup never hangs a plan");
    }

    [Fact]
    public async Task QuoteAsync_propagates_caller_cancellation_rather_than_pricing_the_configuration()
    {
        var (client, _, _) = ClientFor((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Json(HttpStatusCode.OK, """{"usd":1}"""));
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => client.QuoteAsync(Query(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled execution is not a pricing failure; it must not be converted into a price");
    }

    [Fact]
    public async Task QuoteAsync_rejects_a_model_that_would_move_the_request_off_the_vendors_host()
    {
        // The estimate is path-addressed, so it reuses the submit path's authority-override guard. A
        // model id is step-config-authored, not admin-only, and every request built here carries the
        // two-part credential — so an id shaped like a URL must be refused before anything is sent.
        var (client, handler) = ClientThatSendsNothing();

        Func<Task> act = () => client.QuoteAsync(
            Query(model: "https://evil.example.com/steal"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task QuoteAsync_rejects_a_model_with_a_parent_segment_without_sending_anything()
    {
        var (client, handler) = ClientThatSendsNothing();

        Func<Task> act = () => client.QuoteAsync(
            Query(model: "kling-video/../../admin"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_price_quote_cannot_be_constructed_with_a_non_positive_price()
    {
        // The structural half of "no silent price of zero": there is no way to build the value that
        // would mean "free", so no oracle bug can smuggle one past a budget gate.
        Action zeroTotal = () => new VideoGenerationPriceQuote(
            EndpointId, "1080P", "16:9", 5, 0.25m, totalUsd: 0m, null, VideoGenerationPriceSource.VendorEstimate);
        Action negativeTotal = () => new VideoGenerationPriceQuote(
            EndpointId, "1080P", "16:9", 5, 0.25m, totalUsd: -1m, null, VideoGenerationPriceSource.VendorEstimate);
        Action zeroPerSecond = () => new VideoGenerationPriceQuote(
            EndpointId, "1080P", "16:9", 5, pricePerSecondUsd: 0m, 1.25m, null, VideoGenerationPriceSource.VendorEstimate);

        zeroTotal.Should().Throw<InvalidOperationException>();
        negativeTotal.Should().Throw<InvalidOperationException>();
        zeroPerSecond.Should().Throw<InvalidOperationException>();
    }

    // -----------------------------------------------------------------------------------------
    // The purity contract, restated on Describe
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Describe_stays_offline_even_after_the_oracle_has_priced_the_same_model()
    {
        // The whole point of the async price oracle is that ONE bounded cached call joined the pricing path.
        // It did not join Describe: Describe is still the offline answer, and it still refuses.
        var (client, handler, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":1,"usd":0.75}""")));

        await client.QuoteAsync(Query(), CancellationToken.None);
        int afterQuote = handler.Requests.Count;

        Action act = () => client.Describe(EndpointId);

        act.Should().Throw<NotSupportedException>();
        handler.Requests.Should().HaveCount(afterQuote, "Describe is pure AND no-network, exactly as before");
    }

    [Fact]
    public async Task ProbeAsync_stays_no_cost_and_does_not_disturb_the_price_cache()
    {
        // One stub serving both endpoints, so the assertion is about WHICH endpoint each call reached
        // rather than about two unrelated handlers.
        var (client, handler, clock) = ClientFor((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.StartsWith("/requests/", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"status":"completed"}""")
                : Json(HttpStatusCode.OK, """{"credits":1,"usd":0.75}""")));

        await client.QuoteAsync(Query(), CancellationToken.None);
        VideoGenerationProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Method.Should().Be(HttpMethod.Get);
        handler.Requests[1].Path.Should().StartWith(
            "/requests/", "the no-cost probe is unchanged and never estimates");

        // The probe must not have disturbed the cached quote: the next pricing call is still free.
        clock.Now = clock.Now.AddSeconds(1);
        await client.QuoteAsync(Query(), CancellationToken.None);
        handler.Requests.Should().HaveCount(2);
    }

    // -----------------------------------------------------------------------------------------
    // The provider-agnostic surface a consumer actually calls
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task QuoteClipAsync_routes_to_the_oracle_when_the_client_has_one()
    {
        var (client, handler, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":7,"usd":2.00}""")));

        VideoGenerationPriceQuote quote = await ((IVideoGenerationClient)client).QuoteClipAsync(Query(), CancellationToken.None);

        quote.Source.Should().Be(VideoGenerationPriceSource.VendorEstimate);
        quote.TotalUsd.Should().Be(2.00m);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task QuoteClipAsync_falls_back_to_the_published_table_for_a_client_without_an_oracle()
    {
        // MiniMax, and every Mock<IVideoGenerationClient> in the suite, take this branch: pricing is
        // provider-agnostic to call, and a provider that publishes its prices costs no network.
        RecordingHandler handler = new((_, _) =>
            throw new InvalidOperationException("A published price table must never require a request."));
        IVideoGenerationClient miniMax = new MiniMaxVideoGenerationClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.minimax.io") }, "test-key-never-sent");

        VideoGenerationPriceQuote quote = await miniMax.QuoteClipAsync(
            new VideoGenerationPriceQuery("MiniMax-H3", "768P", "16:9", 6), CancellationToken.None);

        quote.Source.Should().Be(VideoGenerationPriceSource.StaticTable);
        quote.PricePerSecondUsd.Should().Be(0.08m);
        quote.TotalUsd.Should().Be(0.48m, "the static branch goes through VideoGenerationPricing, not a second table");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task QuoteClipAsync_still_fails_closed_for_an_unpriced_resolution()
    {
        IVideoGenerationClient miniMax = new MiniMaxVideoGenerationClient(
            new HttpClient { BaseAddress = new Uri("https://api.minimax.io") }, "test-key-never-sent");

        Func<Task> act = () => miniMax.QuoteClipAsync(
            new VideoGenerationPriceQuery("MiniMax-H3", "4320P", "16:9", 6), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>(
            "an unpriced resolution is rejected, never guessed — the phase-1 rule, preserved");
    }

    [Fact]
    public async Task QuoteClipAsync_uses_the_clients_own_overload_when_it_has_one()
    {
        var (client, handler, _) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"credits":1,"usd":0.60}""")));

        VideoGenerationPriceQuote quote = await client.QuoteClipAsync(Query(duration: 6), CancellationToken.None);

        quote.ToCapabilities().MinDurationSeconds.Should().Be(6);
        handler.Requests.Should().ContainSingle();
    }

    // -----------------------------------------------------------------------------------------
    // ToCapabilities: the bridge that lets existing consumers stay unchanged
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ToCapabilities_pins_the_exact_configuration_that_was_priced()
    {
        var quote = new VideoGenerationPriceQuote(
            EndpointId, "1080P", "9:16", 5, 0.25m, 1.25m, 42, VideoGenerationPriceSource.VendorEstimate);

        VideoGenerationCapabilities capabilities = quote.ToCapabilities();

        capabilities.PricePerSecondByResolution.Should().ContainKey("1080P").WhoseValue.Should().Be(0.25m);
        capabilities.PricePerSecondByResolution.Should().HaveCount(1);
        capabilities.MinDurationSeconds.Should().Be(5);
        capabilities.MaxDurationSeconds.Should().Be(5, "the range is a POINT: only the priced duration is offerable");
        capabilities.AllowedRatios.Should().Equal("9:16");

        // And the shared pricing path round-trips through it unchanged.
        VideoGenerationPricing.EstimateClipCostUsd(capabilities, "1080P", 5).Should().Be(1.25m);
    }

    // -----------------------------------------------------------------------------------------
    // VideoGenerationPricing: one resolution rule, not two
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TryGetPricePerSecond_is_ordinal_and_never_case_folds()
    {
        var capabilities = new VideoGenerationCapabilities(
            new Dictionary<string, decimal>(StringComparer.Ordinal) { ["768P"] = 0.08m },
            MinDurationSeconds: 4,
            MaxDurationSeconds: 15,
            AllowedRatios: new[] { "16:9" });

        VideoGenerationPricing.TryGetPricePerSecond(capabilities, "768P", out decimal exact).Should().BeTrue();
        exact.Should().Be(0.08m);
        VideoGenerationPricing.TryGetPricePerSecond(capabilities, "768p", out _).Should().BeFalse();
        VideoGenerationPricing.TryGetPricePerSecond(capabilities, " 768P", out _).Should().BeFalse();
    }

    [Fact]
    public void QuoteFromStaticTable_matches_EstimateClipCostUsd_exactly()
    {
        IVideoGenerationClient miniMax = new MiniMaxVideoGenerationClient(
            new HttpClient(), "test-key-never-sent");
        VideoGenerationCapabilities capabilities = miniMax.Describe("MiniMax-H3-Max");
        var query = new VideoGenerationPriceQuery("MiniMax-H3-Max", "480P", "4:3", 9);

        VideoGenerationPriceQuote quote = VideoGenerationPriceOracleExtensions.QuoteFromStaticTable(capabilities, query);

        quote.TotalUsd.Should().Be(VideoGenerationPricing.EstimateClipCostUsd(capabilities, "480P", 9));
        quote.Credits.Should().BeNull("only the vendor oracle reports credits; a static table has none to report");
        quote.TotalUsd.ToString(CultureInfo.InvariantCulture).Should().Be("0.45");
    }
}
