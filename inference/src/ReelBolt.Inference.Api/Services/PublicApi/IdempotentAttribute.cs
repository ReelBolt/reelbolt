using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Services.ApiKeys;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>
/// What a validated <c>Idempotency-Key</c> means for the run it starts (S-M1): the deterministic
/// <c>WorkflowExecution.CorrelationId</c> a start uses, and the marker the action sets once it has committed
/// its write, so the filter knows an exception must not abandon the key (a replay must not start a second run).
/// </summary>
public static class PublicIdempotency
{
    /// <summary>The correlation id prefix of an idempotent public start (the dispatcher uses <c>apirun:req:</c>).</summary>
    public const string CorrelationPrefix = "apirun:idem:";

    internal const string KeyItem = "ReelBolt.PublicApi.IdempotencyKey";
    internal const string CommittedItem = "ReelBolt.PublicApi.WriteCommitted";

    /// <summary>The validated key of this request, or null when the request carried none.</summary>
    public static string? KeyOf(HttpContext http) =>
        http.Items.TryGetValue(KeyItem, out object? value) ? value as string : null;

    /// <summary>
    /// <c>apirun:idem:{keyId:N}:{sha256hex(key)[..32]}</c>: the same key and API key always name the same run, so a
    /// start that reached the database before its response was lost is found again instead of run twice.
    /// </summary>
    public static string CorrelationId(Guid apiKeyId, string key) =>
        $"{CorrelationPrefix}{apiKeyId:N}:{Hash(key)[..32]}";

    /// <summary>True when <paramref name="correlationId"/> is an idempotent public start's correlation id.</summary>
    public static bool IsIdempotentCorrelation(string? correlationId) =>
        correlationId?.StartsWith(CorrelationPrefix, StringComparison.Ordinal) == true;

    /// <summary>The action committed a write that must not be repeated (called inside the start's save callback).</summary>
    public static void MarkCommitted(HttpContext http) => http.Items[CommittedItem] = true;

    internal static bool IsCommitted(HttpContext http) =>
        http.Items.TryGetValue(CommittedItem, out object? value) && value is true;

    private static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}

/// <summary>
/// <c>Idempotency-Key</c> support. Resource controllers mark a POST with <c>[Idempotent]</c>:
/// the first request claims the key and its FINAL (2xx except the transient 408/409/429) response is stored for 24 h;
/// a repeat with the same body replays it (<c>Idempotent-Replayed: true</c>), a different body is
/// <c>422 idempotency_key_reused</c>, an in-flight one is <c>409 idempotency_in_progress</c>. A transient answer
/// (408, 409, 429) abandons the record so the client's retry (after <c>Retry-After</c>) runs again. A 5xx or an
/// exception abandons it too, UNLESS the action had already committed its write: then the record is left Done with no
/// body, so the replay answers <c>409 idempotency_response_unavailable</c> and never starts a second run. No header
/// means no-op. The record is scoped by the API key id, so no key can read another's.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute : Attribute, IAsyncResourceFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeaderName = "Idempotent-Replayed";
    /// <summary>
    /// JSON members (case-insensitive) of the response that must never be stored. The client still gets the real
    /// body; the stored copy has each one set to null plus <c>&lt;name&gt;Redacted: true</c>, and a replay answers
    /// that copy. Used for one-time secrets.
    /// </summary>
    public string[] RedactFields { get; init; } = [];

    /// <summary>The body to store: <paramref name="body"/> with <see cref="RedactFields"/> blanked.</summary>
    internal byte[] ForStorage(byte[] body)
    {
        if (RedactFields.Length == 0 || body.Length == 0) return body;
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(body) is not System.Text.Json.Nodes.JsonObject obj) return body;
            foreach (string field in RedactFields)
            {
                string? actual = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase));
                if (actual is null) continue;
                obj[actual] = null;
                obj[actual + "Redacted"] = true;
            }

            return Encoding.UTF8.GetBytes(obj.ToJsonString());
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON we understand: store nothing rather than something unredacted.
            return [];
        }
    }

    // Outer guard (order 0) runs first; idempotency runs inside it, after authentication and the key check.
    public int Order => 100;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        HttpContext http = context.HttpContext;
        IServiceProvider services = http.RequestServices;
        IApiKeyContext keyContext = services.GetRequiredService<IApiKeyContext>();
        if (!keyContext.IsApiKey || keyContext.KeyId is not Guid apiKeyId)
        {
            await next();
            return;
        }

        IReadOnlyList<string> values = http.Request.Headers[HeaderName];
        if (values.Count == 0)
        {
            await next();
            return;
        }

        string key = values.Count == 1 ? values[0] : string.Empty;
        if (!IsValid(key))
        {
            await PublicApiEnvelope.WriteAsync(http, StatusCodes.Status400BadRequest,
                PublicApiErrorCodes.InvalidIdempotencyKey,
                $"`{HeaderName}` must be 1 to 200 printable ASCII characters.");
            return;
        }

        string hash = await RequestHashAsync(http, context.RouteData, http.RequestAborted);
        // The action reads this to derive the run's deterministic correlation id (S-M1b).
        http.Items[PublicIdempotency.KeyItem] = key;
        IIdempotencyStore store = services.GetRequiredService<IIdempotencyStore>();
        DateTime now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        IdempotencyBegin begin = await store.BeginAsync(apiKeyId, key, hash, now, http.RequestAborted);
        switch (begin.Status)
        {
            case IdempotencyBeginStatus.Reused:
                await PublicApiEnvelope.WriteAsync(http, StatusCodes.Status422UnprocessableEntity,
                    PublicApiErrorCodes.IdempotencyKeyReused,
                    "This Idempotency-Key was already used with a different request body.");
                return;
            case IdempotencyBeginStatus.InProgress:
                await PublicApiEnvelope.WriteAsync(http, StatusCodes.Status409Conflict,
                    PublicApiErrorCodes.IdempotencyInProgress,
                    "A request with this Idempotency-Key is still in progress.");
                return;
            case IdempotencyBeginStatus.ResponseUnavailable:
                await PublicApiEnvelope.WriteAsync(http, StatusCodes.Status409Conflict,
                    PublicApiErrorCodes.IdempotencyResponseUnavailable,
                    "The stored response for this Idempotency-Key was too large to replay; use a new key.");
                return;
            case IdempotencyBeginStatus.Replay:
                http.Response.StatusCode = begin.ResponseStatus;
                http.Response.Headers[ReplayedHeaderName] = "true";
                if (!string.IsNullOrEmpty(begin.ResponseBody))
                {
                    http.Response.ContentType = "application/json";
                    await http.Response.WriteAsync(begin.ResponseBody, http.RequestAborted);
                }

                return;
        }

        Stream original = http.Response.Body;
        using MemoryStream capture = new();
        http.Response.Body = capture;
        try
        {
            ResourceExecutedContext executed;
            try
            {
                executed = await next();
            }
            catch
            {
                // A 5xx or an exception must not poison the key: the client retries the same request. Unless the action
                // already committed its write: then the record stays Done with no body so the replay answers 409 and
                // cannot start a second run (S-M1).
                await FailAsync(store, apiKeyId, key, http, now);
                throw;
            }

            int status = http.Response.StatusCode;
            if (executed.Exception is not null || status >= 500)
                await FailAsync(store, apiKeyId, key, http, now);
            else if (IsTransient(status))
                // 408/409/429 are answers the client retries (a stored 429 would block the retry for 24 h, Q-1).
                await store.AbandonAsync(apiKeyId, key, CancellationToken.None);
            else
                await store.CompleteAsync(apiKeyId, key, status, ForStorage(capture.ToArray()), now, CancellationToken.None);
        }
        finally
        {
            http.Response.Body = original;
            if (capture.Length > 0)
            {
                capture.Position = 0;
                await capture.CopyToAsync(original, http.RequestAborted);
            }
        }
    }

    private static bool IsTransient(int status) =>
        status is StatusCodes.Status408RequestTimeout
            or StatusCodes.Status409Conflict
            or StatusCodes.Status429TooManyRequests;

    /// <summary>
    /// A 5xx/exception: abandon the key so a retry runs, unless the action committed its write; then Done with no
    /// body, so a replay answers <c>409 idempotency_response_unavailable</c> instead of starting the run again.
    /// </summary>
    private static Task FailAsync(IIdempotencyStore store, Guid apiKeyId, string key, HttpContext http, DateTime now) =>
        PublicIdempotency.IsCommitted(http)
            ? store.CompleteWithoutBodyAsync(apiKeyId, key, StatusCodes.Status500InternalServerError, now, CancellationToken.None)
            : store.AbandonAsync(apiKeyId, key, CancellationToken.None);

    private static bool IsValid(string key) =>
        key.Length is >= 1 and <= 200 && key.All(c => c is >= (char)0x20 and <= (char)0x7E);

    /// <summary>SHA-256 hex of method + route template + sorted route values + the raw request body.</summary>
    private static async Task<string> RequestHashAsync(
        HttpContext http, RouteData routeData, CancellationToken ct)
    {
        http.Request.EnableBuffering();
        http.Request.Body.Position = 0;
        using MemoryStream body = new();
        await http.Request.Body.CopyToAsync(body, ct);
        http.Request.Body.Position = 0;

        string template = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? string.Empty;
        string routeValues = string.Join('&', routeData.Values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + Convert.ToString(pair.Value, CultureInfo.InvariantCulture)));

        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(http.Request.Method));
        sha.AppendData([0]);
        sha.AppendData(Encoding.UTF8.GetBytes(template));
        sha.AppendData([0]);
        sha.AppendData(Encoding.UTF8.GetBytes(routeValues));
        sha.AppendData([0]);
        sha.AppendData(body.GetBuffer().AsSpan(0, (int)body.Length));
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
