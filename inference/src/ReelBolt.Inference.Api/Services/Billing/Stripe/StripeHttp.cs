using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Observability;

namespace ReelBolt.Inference.Api.Services.Billing.Stripe;

/// <summary>
/// An ordered Stripe form body or query string. <see cref="Add"/> flattens nested values to Stripe's bracket keys
/// (<c>a[b][0][c]</c>, scalar lists as <c>x[]</c>); null is skipped, an empty string is kept (<c>discounts=</c> clears),
/// booleans are lower-case.
/// </summary>
internal sealed class StripeForm : IEnumerable<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _pairs = [];

    public StripeForm Add(string key, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case string s:
                _pairs.Add(new(key, s));
                break;
            case bool b:
                _pairs.Add(new(key, b ? "true" : "false"));
                break;
            case IEnumerable<KeyValuePair<string, object?>> map:
                foreach (KeyValuePair<string, object?> kv in map) Add($"{key}[{kv.Key}]", kv.Value);
                break;
            case IEnumerable<KeyValuePair<string, string>> strMap:
                foreach (KeyValuePair<string, string> kv in strMap) Add($"{key}[{kv.Key}]", kv.Value);
                break;
            case IEnumerable list:
                int i = 0;
                foreach (object? item in list)
                {
                    bool scalar = item is null or string or bool or IFormattable;
                    Add(scalar ? $"{key}[]" : $"{key}[{i}]", item);
                    i++;
                }

                break;
            case IFormattable f:
                _pairs.Add(new(key, f.ToString(null, CultureInfo.InvariantCulture)));
                break;
            default:
                _pairs.Add(new(key, value.ToString() ?? string.Empty));
                break;
        }

        return this;
    }

    public bool Has(string key) => _pairs.Any(p => p.Key == key);

    public string ToQueryString()
        => string.Join('&', _pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _pairs.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The Stripe REST wire: bearer auth, the pinned <c>Stripe-Version</c>, form bodies, <c>Idempotency-Key</c> on POST, bounded
/// retry of 429 and <c>lock_timeout</c> (always) and of 5xx and network errors (GET or keyed POST only), and Stripe's typed error mapped to <see cref="BillingProviderException"/>.
/// Neither the key nor a request body is ever written to an exception message or a log line.
/// </summary>
internal sealed class StripeHttp(
    HttpClient http,
    Func<StripeOptions> options,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<double>? jitter = null,
    ILogger? logger = null)
{
    public const int MaxRetries = 3;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

    public Task<JsonElement> GetAsync(string path, StripeForm? query, CancellationToken ct)
        => SendAsync(HttpMethod.Get, path, query, null, null, ct);

    public Task<JsonElement> PostAsync(string path, StripeForm form, string? idempotencyKey, CancellationToken ct)
        => SendAsync(HttpMethod.Post, path, null, form, idempotencyKey, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, StripeForm? query, StripeForm? form, string? idempotencyKey, CancellationToken ct)
    {
        StripeOptions o = options();
        string url = o.BaseUrl.TrimEnd('/') + path;
        if (query is not null && query.Any()) url += "?" + query.ToQueryString();
        bool safeToRepeat = method == HttpMethod.Get || idempotencyKey is not null;

        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = new(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
            request.Headers.TryAddWithoutValidation("Stripe-Version", StripeOptions.ApiVersion);
            if (form is not null)
            {
                request.Content = new FormUrlEncodedContent(form);
                if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }

            HttpResponseMessage response;
            string body;
            try
            {
                response = await http.SendAsync(request, ct);
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                if (safeToRepeat && attempt < MaxRetries)
                {
                    logger?.LogWarning("Stripe {Method} {Path} failed on the wire ({Type}); retry {Attempt}.", method, Path(path).ForLog(), ex.GetType().Name, attempt + 1);
                    await Backoff(attempt, ct);
                    continue;
                }

                throw new HttpRequestException($"Stripe {method} {Path(path)} could not be completed ({ex.GetType().Name}).", ex);
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return ParseOrEmpty(body);
                }

                (string? code, string? type, string? message) = ParseError(body, o.ApiKey);
                // L6: 429 and lock_timeout mean "not processed", retried for every call; a 5xx may have been processed,
                // so it is repeated only when that is safe (a GET, or a POST carrying an Idempotency-Key).
                bool retryable = status == 429 || code == "lock_timeout" || (status >= 500 && safeToRepeat);
                if (retryable && attempt < MaxRetries)
                {
                    logger?.LogWarning("Stripe {Method} {Path} answered {Status} ({Code}); retry {Attempt}.", method, Path(path).ForLog(), status, (code ?? type).ForLog(), attempt + 1);
                    await Backoff(attempt, ct);
                    continue;
                }

                if (retryable || status >= 500)
                {
                    throw new HttpRequestException($"Stripe {method} {Path(path)} failed with status {status} ({code ?? type ?? "unknown"}) after {attempt} retries.", null, response.StatusCode);
                }

                throw new BillingProviderException(code ?? type ?? "stripe_error", status, message ?? $"Stripe answered {status}.");
            }
        }
    }

    private Task Backoff(int attempt, CancellationToken ct)
        => _delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt) * (0.5 + _jitter())), ct);

    /// <summary>The path without ids' query noise, safe to log (ids are not secrets; query strings may carry emails).</summary>
    private static string Path(string path) => path;

    private static JsonElement ParseOrEmpty(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new HttpRequestException("Stripe answered with a body that is not JSON.");
        }
    }

    private static (string? Code, string? Type, string? Message) ParseError(string body, string? apiKey)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement? err = StripeEventParser.Prop(doc.RootElement, "error");
            string? message = StripeEventParser.Str(err, "message");
            if (message is not null && !string.IsNullOrEmpty(apiKey)) message = message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
            return (StripeEventParser.Str(err, "code"), StripeEventParser.Str(err, "type"), message);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }
}
