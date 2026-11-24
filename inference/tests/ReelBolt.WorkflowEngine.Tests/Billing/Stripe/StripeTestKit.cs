// Request/response shapes follow docs.stripe.com 2026-10-26; replace fixtures with redacted sandbox captures after the operator spike.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Billing.Stripe;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>One request the provider sent: method, path, decoded query and form, and the headers that matter.</summary>
internal sealed record StripeCall(
    HttpMethod Method,
    string Path,
    IReadOnlyList<KeyValuePair<string, string>> Query,
    IReadOnlyList<KeyValuePair<string, string>> Form,
    string? Authorization,
    string? Version,
    string? IdempotencyKey)
{
    public string? Q(string key) => Query.Where(p => p.Key == key).Select(p => p.Value).FirstOrDefault();

    public string? F(string key) => Form.Where(p => p.Key == key).Select(p => p.Value).FirstOrDefault();

    public bool HasF(string key) => Form.Any(p => p.Key == key);

    public IReadOnlyList<string> FKeys => Form.Select(p => p.Key).ToList();
}

/// <summary>An in-process Stripe: routes are matched by method and exact path; the last queued response repeats.</summary>
internal sealed class StripeFake : HttpMessageHandler
{
    private readonly List<(string Method, string Path, Queue<(int Status, string Body)> Responses)> _routes = [];

    public List<StripeCall> Calls { get; } = [];

    public StripeFake On(string method, string path, object body, int status = 200)
    {
        (string Method, string Path, Queue<(int, string)> Responses) route = _routes.FirstOrDefault(r => r.Method == method && r.Path == path);
        if (route.Responses is null)
        {
            route = (method, path, new Queue<(int, string)>());
            _routes.Add(route);
        }

        route.Responses.Enqueue((status, body as string ?? JsonSerializer.Serialize(body)));
        return this;
    }

    public StripeFake Error(string method, string path, int status, string code, string message = "nope", string type = "invalid_request_error")
        => On(method, path, new { error = new { type, code, message } }, status);

    public IReadOnlyList<StripeCall> To(string path) => Calls.Where(c => c.Path == path).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Uri uri = request.RequestUri!;
        string? idem = request.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? v) ? v.Single() : null;
        string? version = request.Headers.TryGetValues("Stripe-Version", out IEnumerable<string>? sv) ? sv.Single() : null;
        Calls.Add(new StripeCall(request.Method, uri.AbsolutePath, Decode(uri.Query.TrimStart('?')), Decode(form), request.Headers.Authorization?.ToString(), version, idem));

        (string Method, string Path, Queue<(int Status, string Body)> Responses) route =
            _routes.FirstOrDefault(r => r.Method == request.Method.Method && r.Path == uri.AbsolutePath);
        (int status, string body) = route.Responses is null
            ? (404, """{"error":{"type":"invalid_request_error","code":"resource_missing","message":"No such object (unrouted in test)."}}""")
            : route.Responses.Count > 1 ? route.Responses.Dequeue() : route.Responses.Peek();
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static List<KeyValuePair<string, string>> Decode(string encoded)
        => encoded.Length == 0
            ? []
            : encoded.Split('&').Select(p =>
            {
                int eq = p.IndexOf('=');
                string k = eq < 0 ? p : p[..eq];
                string val = eq < 0 ? "" : p[(eq + 1)..];
                return new KeyValuePair<string, string>(Uri.UnescapeDataString(k.Replace('+', ' ')), Uri.UnescapeDataString(val.Replace('+', ' ')));
            }).ToList();
}

internal static class StripeKit
{
    public const string ApiKey = "rk_test_secretkey_must_never_leak";
    public static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    public sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    public static StripeOptions Options(bool managedPayments = false, string? portalConfig = "bpc_1") => new()
    {
        ApiKey = ApiKey,
        WebhookSecret = StripePayloads.Secret,
        ReturnBaseUrl = "https://app.example.com/",
        ManagedPayments = managedPayments,
        PortalConfigurationId = portalConfig,
        TopUpPriceIds = new() { ["1000"] = "price_topup_1000", ["5000"] = "price_topup_5000" }
    };

    public static StripeBillingProvider Provider(StripeFake fake, StripeOptions? options = null, List<TimeSpan>? delays = null)
        => new(new HttpClient(fake), Microsoft.Extensions.Options.Options.Create(options ?? Options()), new FixedClock(Now), null,
            (d, _) =>
            {
                delays?.Add(d);
                return Task.CompletedTask;
            });

    public static long Unix(DateTime t) => new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds();

    public static object SubscriptionJson(string status = "active", string price = "price_creator", int quantity = 1, string itemId = "si_1", bool cancelAtPeriodEnd = false, string? org = null, object? discounts = null)
        => new
        {
            id = "sub_1",
            customer = "cus_1",
            status,
            metadata = org is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["organization_id"] = org },
            cancel_at_period_end = cancelAtPeriodEnd,
            cancel_at = (long?)null,
            discounts = discounts ?? Array.Empty<object>(),
            items = new { data = new[] { new { id = itemId, price = new { id = price }, quantity, current_period_start = Unix(Now), current_period_end = Unix(Now.AddMonths(1)) } } }
        };
}
