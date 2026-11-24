using System.Net;
using System.Net.Http.Headers;

namespace ReelBolt.Inference.Api.Services.CloudStorage;

/// <summary>
/// The one send path every provider uses. Retries a rate limit (429) or a transient 5xx (500, 502, 503,
/// 504) up to <see cref="MaxAttempts"/> times, honouring <c>Retry-After</c> (capped at
/// <see cref="MaxRetryDelay"/>), then maps whatever is left to a <see cref="CloudStorageApiException"/>.
/// The request is built by a factory because an <see cref="HttpRequestMessage"/> cannot be sent twice.
/// The response body of a failure is NOT put in the exception message (remote text reaches logs); a
/// provider that needs it (Dropbox's error summary, Google's rate-limit reason) passes <c>classify</c>.
/// </summary>
public static class CloudHttp
{
    public const int MaxAttempts = 4;

    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Overridable so tests do not sleep.</summary>
    internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>
    /// Sends and returns a SUCCESSFUL response (the caller disposes it). <paramref name="classify"/> may
    /// inspect a failed response's status and body and return a kind to override the default mapping
    /// (e.g. Google's <c>403 rateLimitExceeded</c> is <see cref="CloudStorageErrorKind.RateLimited"/>, and
    /// is then retried like a 429); return null for the default. <paramref name="label"/> names the call
    /// in logs and messages ("GET /drive/v3/files") and must not contain a secret.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        string label,
        CancellationToken ct,
        Func<HttpStatusCode, string, CloudStorageErrorKind?>? classify = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        bool returnRedirects = false)
    {
        for (int attempt = 1; ; attempt++)
        {
            using HttpRequestMessage request = requestFactory();
            HttpResponseMessage response = await http.SendAsync(request, completion, ct);
            if (response.IsSuccessStatusCode) return response;

            // A client with AllowAutoRedirect=false (Graph's download hop) needs the 3xx itself, to read
            // Location and fetch it WITHOUT the bearer token. Only when the caller asks for it.
            if (returnRedirects && (int)response.StatusCode is >= 300 and < 400) return response;

            HttpStatusCode status = response.StatusCode;
            string body = string.Empty;
            if (classify is not null)
            {
                try { body = await response.Content.ReadAsStringAsync(ct); }
                catch (HttpRequestException) { body = string.Empty; }
            }

            TimeSpan? retryAfter = RetryAfterOf(response.Headers.RetryAfter);
            response.Dispose();

            CloudStorageErrorKind kind = classify?.Invoke(status, body) ?? DefaultKind(status);
            bool retryable = kind == CloudStorageErrorKind.RateLimited
                || (kind == CloudStorageErrorKind.ServerError && status is HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout);

            if (!retryable || attempt >= MaxAttempts)
            {
                throw new CloudStorageApiException(
                    $"{label} answered {(int)status} after {attempt} attempt(s).", kind, status, retryAfter);
            }

            TimeSpan wait = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            if (wait > MaxRetryDelay) wait = MaxRetryDelay;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            await Delay(wait, ct);
        }
    }

    public static CloudStorageErrorKind DefaultKind(HttpStatusCode status) => (int)status switch
    {
        401 => CloudStorageErrorKind.Unauthorized,
        403 => CloudStorageErrorKind.Forbidden,
        404 or 410 => CloudStorageErrorKind.NotFound,
        429 => CloudStorageErrorKind.RateLimited,
        >= 500 => CloudStorageErrorKind.ServerError,
        _ => CloudStorageErrorKind.InvalidResponse
    };

    private static TimeSpan? RetryAfterOf(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>Escapes one URL path/query component. Ids come from the provider or a request; never trust them raw.</summary>
    public static string Esc(string value) => Uri.EscapeDataString(value);
}
