using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>
/// Shared plumbing of the platform adapters: send a request, translate every failure into a <see cref="SocialApiException"/>
/// whose message never carries a token or a platform body, and set <see cref="SocialApiException.PostMayExist"/> for any
/// failure after the final publish request was sent (the answer may have been lost while the post was created).
/// </summary>
internal static class SocialHttp
{
    /// <summary>Most bytes of an error body read for classification. The body is never copied into a message or log.</summary>
    private const int MaxErrorBodyChars = 4096;

    /// <summary>
    /// Sends <paramref name="request"/>. A transport failure or a timeout becomes a retryable <see cref="SocialErrorKind.ServerError"/>;
    /// with <paramref name="finalRequest"/> it also says the post may exist. A cancellation by the caller propagates untouched.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpRequestMessage request, string platform, bool finalRequest, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new SocialApiException($"{platform} could not be reached.", SocialErrorKind.ServerError, postMayExist: finalRequest);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SocialApiException($"{platform} did not answer in time.", SocialErrorKind.ServerError, postMayExist: finalRequest);
        }
    }

    /// <summary>Returns the response when it is a success; otherwise disposes it and throws the mapped exception.</summary>
    public static async Task<HttpResponseMessage> EnsureSuccessAsync(
        HttpResponseMessage response, string platform, bool finalRequest, Func<HttpStatusCode, string, SocialErrorKind?>? classify, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            string body = string.Empty;
            if (classify is not null)
            {
                try
                {
                    body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (body.Length > MaxErrorBodyChars) body = body[..MaxErrorBodyChars];
                }
                catch (HttpRequestException)
                {
                    body = string.Empty;
                }
            }

            HttpStatusCode status = response.StatusCode;
            SocialErrorKind kind = classify?.Invoke(status, body) ?? DefaultKind(status);
            // A 5xx after the final request leaves the outcome open. A 4xx is the platform saying it did not take the post.
            bool mayExist = finalRequest && kind == SocialErrorKind.ServerError;
            throw new SocialApiException($"{platform} answered {(int)status}.", kind, status, RetryAfter(response), mayExist);
        }
    }

    public static SocialErrorKind DefaultKind(HttpStatusCode status) => (int)status switch
    {
        401 => SocialErrorKind.Unauthorized,
        404 => SocialErrorKind.NotFound,
        429 => SocialErrorKind.RateLimited,
        >= 500 => SocialErrorKind.ServerError,
        _ => SocialErrorKind.Forbidden
    };

    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>Parses a JSON body, or throws <see cref="SocialErrorKind.InvalidResponse"/> (with <paramref name="postMayExist"/>).</summary>
    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string platform, bool postMayExist, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            throw new SocialApiException($"{platform} sent an unreadable answer.", SocialErrorKind.InvalidResponse, response.StatusCode, postMayExist: postMayExist);
        }
    }

    public static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>A count the platform sends as a JSON number or a numeric string; null when absent or unreadable.</summary>
    public static long? GetCount(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long s)) return s;
        return null;
    }

    /// <summary>Reads the token endpoint answer shared by Google and LinkedIn (<c>expires_in</c> seconds).</summary>
    public static SocialTokenSet ParseTokens(JsonElement root, string platform, TimeProvider clock)
    {
        string? access = GetString(root, "access_token");
        if (string.IsNullOrEmpty(access))
            throw new SocialApiException($"{platform} sent no access token.", SocialErrorKind.InvalidResponse);
        DateTime? expires = GetCount(root, "expires_in") is { } seconds and > 0
            ? clock.GetUtcNow().UtcDateTime.AddSeconds(seconds)
            : null;
        return new SocialTokenSet(access, GetString(root, "refresh_token"), expires, GetString(root, "scope"));
    }

    /// <summary>Token endpoint errors: a refused code or grant is <see cref="SocialErrorKind.Unauthorized"/> so the person reconnects.</summary>
    public static SocialErrorKind? ClassifyTokenEndpoint(HttpStatusCode status, string body) =>
        (int)status is 400 or 401 ? SocialErrorKind.Unauthorized : null;

    public static bool IsHttps(string? url, out Uri uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>
/// Counts platform quota units spent today (UTC) in named buckets. Per process: a deployment with several replicas
/// should treat the limits as per replica. Feeds the daily budget: the engine counts posts, this counts units.
/// </summary>
public sealed class SocialQuotaCounter(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _used = [];
    private DateOnly _day;

    /// <summary>Takes <paramref name="units"/> from <paramref name="bucket"/> if the day's total stays within <paramref name="limit"/>.</summary>
    public bool TryTake(string bucket, int units, int limit)
    {
        lock (_gate)
        {
            Roll();
            int used = _used.GetValueOrDefault(bucket);
            if (limit > 0 && used + units > limit) return false;
            _used[bucket] = used + units;
            return true;
        }
    }

    public int Used(string bucket)
    {
        lock (_gate)
        {
            Roll();
            return _used.GetValueOrDefault(bucket);
        }
    }

    private void Roll()
    {
        DateOnly today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        if (today == _day) return;
        _day = today;
        _used.Clear();
    }
}
