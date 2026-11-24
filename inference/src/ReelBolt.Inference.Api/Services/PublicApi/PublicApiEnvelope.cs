using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>The error codes the public surface shares.</summary>
public static class PublicApiErrorCodes
{
    public const string ApiKeyRequired = "api_key_required";
    public const string RateLimited = "rate_limited";
    public const string InvalidIdempotencyKey = "invalid_idempotency_key";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string IdempotencyInProgress = "idempotency_in_progress";
    public const string IdempotencyResponseUnavailable = "idempotency_response_unavailable";
    public const string NotFound = "not_found";
    public const string InvalidRequest = "invalid_request";
    public const string Forbidden = "forbidden";
    public const string Internal = "internal_error";

    /// <summary>The 401 every non-key principal gets on the public surface (it is key-only).</summary>
    public const string ApiKeyRequiredMessage = "The public API requires an API key (Authorization: Bearer rbk_...).";

    /// <summary>The JSON body's code for a status that carried no machine-readable code.</summary>
    public static string ForStatus(int status) => status switch
    {
        400 => InvalidRequest,
        401 => ApiKeyRequired,
        402 => "payment_required",
        403 => Forbidden,
        404 => NotFound,
        409 => "conflict",
        413 => "payload_too_large",
        422 => "unprocessable",
        429 => RateLimited,
        500 => Internal,
        _ => status >= 500 ? Internal : InvalidRequest
    };

    public static string MessageForStatus(int status) => status switch
    {
        400 => "The request is not valid.",
        401 => ApiKeyRequiredMessage,
        402 => "The plan or credit budget refuses this request.",
        403 => "This API key may not do that.",
        404 => "The resource was not found.",
        409 => "The request conflicts with the current state.",
        413 => "The request is too large.",
        422 => "The request cannot be processed.",
        429 => "Too many requests.",
        _ => status >= 500 ? "An unexpected error occurred." : "The request could not be completed."
    };
}

/// <summary>
/// The one error envelope of the public surface: <c>{ "error": { "code", "message", ...extras } }</c>.
/// Every helper here produces exactly that shape, so a 401, a 402 and a 429 cannot drift apart.
/// </summary>
public static class PublicApiEnvelope
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The <c>Retry-After</c>/<c>RateLimit-Reset</c> horizon of a 429 that has no fixed window in front of it
    ///: a short, honest wait rather than an invented per-minute window.
    /// </summary>
    public const int DefaultRetryAfterSeconds = 30;

    /// <summary>The header that must carry the same horizon as <c>Retry-After</c> (Q-L3).</summary>
    public const string RateLimitResetHeader = "RateLimit-Reset";

    /// <summary>The extras of a refusal body as JSON properties, with <c>code</c>/<c>message</c> last.</summary>
    public static JsonObject Extras(object? extras)
    {
        JsonObject result = [];
        if (extras is null) return result;
        JsonNode? node = extras switch
        {
            JsonObject already => already,
            JsonNode n => n,
            IReadOnlyDictionary<string, object?> dictionary => Serialize(dictionary),
            _ => Serialize(extras)
        };
        if (node is not JsonObject obj) return result;
        foreach ((string name, JsonNode? value) in obj)
            result[name] = value?.DeepClone();
        return result;
    }

    /// <summary>The <c>error</c> inner object: <c>code</c>, <c>message</c>, then the extras.</summary>
    public static JsonObject Body(string code, string message, object? extras = null)
    {
        JsonObject body = Extras(extras);
        body["code"] = code;
        body["message"] = message;
        return body;
    }

    /// <summary>The full envelope.</summary>
    public static JsonObject Envelope(string code, string message, object? extras = null) =>
        new() { ["error"] = Body(code, message, extras) };

    /// <summary>An MVC result carrying the envelope at <paramref name="status"/>.</summary>
    public static ObjectResult Result(int status, string code, string message, object? extras = null) =>
        new(Envelope(code, message, extras)) { StatusCode = status, ContentTypes = { "application/json" } };

    /// <summary>
    /// The seconds a <c>Retry-After</c> header carries for a 429 that resets at <paramref name="resetsAt"/>.
    /// </summary>
    public static int RetryAfterSeconds(DateTimeOffset resetsAt) =>
        Math.Max(1, (int)Math.Ceiling((resetsAt - DateTimeOffset.UtcNow).TotalSeconds));

    /// <summary>
    /// Sets <c>Retry-After</c> and <c>RateLimit-Reset</c> to the SAME horizon (Q-L3): the guard filter writes the
    /// per-minute window earlier, so a 429 that is not the per-key limit must overwrite it here. Returns the
    /// number of seconds actually written.
    /// </summary>
    public static int ApplyRateLimitHeaders(HttpResponse response, int retryAfterSeconds)
    {
        int seconds = Math.Max(1, retryAfterSeconds);
        string text = seconds.ToString(CultureInfo.InvariantCulture);
        response.Headers.RetryAfter = text;
        response.Headers[RateLimitResetHeader] = text;
        return seconds;
    }

    /// <summary>
    /// A 429 in the shared envelope shape: <c>error.resetsAt</c> (ISO UTC), <c>error.retryAfterSeconds</c>,
    /// plus whatever the refusal reports.
    /// </summary>
    public static ObjectResult RateLimitedResult(
        DateTimeOffset resetsAt, string code, string message, object? extras = null)
    {
        int seconds = RetryAfterSeconds(resetsAt);
        JsonObject body = Extras(extras);
        body["retryAfterSeconds"] = seconds;
        body["resetsAt"] = resetsAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return Result(StatusCodes.Status429TooManyRequests, code, message, body);
    }

    /// <summary>
    /// The ONE normalization of every public 429. It guarantees <c>error.resetsAt</c>, keeps the
    /// source's <c>Retry-After</c> when the refusal carried one (else <see cref="DefaultRetryAfterSeconds"/>),
    /// and writes <c>Retry-After</c> and <c>RateLimit-Reset</c> as that same number. Every controller path that
    /// answers 429 goes through here (via <c>PublicError</c>/<c>PublicRefusal</c>) and the guard filter does the
    /// same in <see cref="WriteRateLimitedAsync"/>.
    /// </summary>
    public static ObjectResult RateLimited(HttpResponse response, ObjectResult result)
    {
        if (result.StatusCode != StatusCodes.Status429TooManyRequests) return result;

        JsonObject envelope = result.Value is JsonObject { } root && root["error"] is JsonObject
            ? root
            : new JsonObject { ["error"] = Body(PublicApiErrorCodes.RateLimited, PublicApiErrorCodes.MessageForStatus(StatusCodes.Status429TooManyRequests), result.Value) };
        JsonObject error = (JsonObject)envelope["error"]!;

        int seconds;
        DateTimeOffset resetsAt;
        if (TryReadResetsAt(error["resetsAt"], out DateTimeOffset claimed) && claimed > DateTimeOffset.UtcNow)
        {
            resetsAt = claimed;
            seconds = RetryAfterSeconds(resetsAt);
        }
        else if (ReadPositiveInt(error["retryAfterSeconds"]) is int asked)
        {
            seconds = asked;
            resetsAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
        else
        {
            seconds = DefaultRetryAfterSeconds;
            resetsAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        // Both headers and both body fields come from the same computed horizon, so they cannot disagree.
        seconds = ApplyRateLimitHeaders(response, seconds);
        resetsAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        error["retryAfterSeconds"] = seconds;
        error["resetsAt"] = resetsAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        result.Value = envelope;
        if (!result.ContentTypes.Contains("application/json")) result.ContentTypes.Add("application/json");
        return result;
    }

    private static bool TryReadResetsAt(JsonNode? node, out DateTimeOffset resetsAt)
    {
        resetsAt = default;
        return node is JsonValue value
            && value.GetValueKind() == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out resetsAt);
    }

    private static int? ReadPositiveInt(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return null;
        return value.TryGetValue(out int number) && number > 0 ? number : null;
    }

    /// <summary>Writes the envelope directly (filters run outside MVC's result execution).</summary>
    public static Task WriteAsync(
        HttpContext context, int status, string code, string message, object? extras = null, CancellationToken ct = default)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(Envelope(code, message, extras).ToJsonString(Json), ct);
    }

    /// <summary>
    /// Every 429 the public API emits: <c>Retry-After</c> and <c>RateLimit-Reset</c> seconds to the window end
    /// and a machine-readable <c>error.resetsAt</c> (ISO UTC). Both are required, on every refusal.
    /// </summary>
    public static Task WriteRateLimitedAsync(
        HttpContext context, DateTimeOffset resetsAt, string message, CancellationToken ct = default)
    {
        int seconds = ApplyRateLimitHeaders(context.Response, RetryAfterSeconds(resetsAt));
        return WriteAsync(context, StatusCodes.Status429TooManyRequests, PublicApiErrorCodes.RateLimited, message,
            new Dictionary<string, object?>
            {
                ["retryAfterSeconds"] = seconds,
                ["resetsAt"] = DateTimeOffset.UtcNow.AddSeconds(seconds).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            }, ct);
    }

    private static JsonNode? Serialize(object value) => JsonSerializer.SerializeToNode(value, Json);
}

/// <summary>A refusal result that already knows the wait a client should observe (e.g. the video-source gate).</summary>
public interface IRetryAfterResult
{
    int RetryAfterSeconds { get; }
}

/// <summary>
/// The single adapter from an existing service refusal to the public envelope. Resource controllers call the
/// services exactly as the dashboard does (<c>RunStartGate</c>, <c>PlanGateResults</c>, upload/quota gates …)
/// and pass the <see cref="IActionResult"/> here; the refusal's <c>code</c>/<c>message</c> and any other
/// property (<c>window</c>, <c>limit</c>, <c>required</c>, <c>resetsAt</c>, …) move into <c>error</c>.
/// </summary>
public static class PublicApiResults
{
    /// <summary>One adapter for <see cref="PlanLimitException"/>, which carries <see cref="PlanGateViolation"/>.</summary>
    public static ObjectResult From(PlanLimitException exception) => From(exception.Violation.ToResult());

    /// <summary>Maps a plan-gate refusal (already the shared <c>{code, feature, message, …}</c> body).</summary>
    public static ObjectResult From(PlanGateViolation violation) => From(violation.ToResult());

    /// <summary>Re-shapes any refusal result into the envelope, keeping its code, message and extras.</summary>
    public static ObjectResult From(IActionResult result)
    {
        // Already the public envelope (a controller built it with PublicError): keep it as it is, code included.
        if (result is ObjectResult { Value: System.Text.Json.Nodes.JsonObject { } built } finished && built["error"] is System.Text.Json.Nodes.JsonObject)
            return finished;

        int status;
        object? value = null;
        switch (result)
        {
            case ObjectResult objectResult:
                status = objectResult.StatusCode ?? StatusCodes.Status200OK;
                value = objectResult.Value;
                break;
            case StatusCodeResult statusCode:
                status = statusCode.StatusCode;
                break;
            case ForbidResult:
                return PublicApiEnvelope.Result(StatusCodes.Status403Forbidden,
                    PublicApiErrorCodes.Forbidden, PublicApiErrorCodes.MessageForStatus(StatusCodes.Status403Forbidden));
            default:
                status = StatusCodes.Status500InternalServerError;
                break;
        }

        JsonObject extras = PublicApiEnvelope.Extras(value);
        string? code = extras["code"]?.GetValue<string>();
        string? message = extras["message"]?.GetValue<string>();
        extras.Remove("code");
        extras.Remove("message");

        // Q-M2: a source result that knows its own Retry-After (VideoSourceGate's TOO_DEMANDING_LIMIT) would
        // otherwise lose it: ObjectResult headers only exist at execution time. Carry it in the body so the one
        // 429 helper keeps it; a source with no horizon gets the default there.
        if (status == StatusCodes.Status429TooManyRequests
            && result is IRetryAfterResult { RetryAfterSeconds: > 0 } source
            && !extras.ContainsKey("retryAfterSeconds"))
            extras["retryAfterSeconds"] = source.RetryAfterSeconds;

        return PublicApiEnvelope.Result(status,
            code ?? PublicApiErrorCodes.ForStatus(status),
            message ?? PublicApiErrorCodes.MessageForStatus(status),
            extras.Count == 0 ? null : extras);
    }
}

/// <summary>
/// <c>?limit=&amp;cursor=</c> paging for the public lists: <c>{ data, nextCursor }</c>, default 50, max 200.
/// The cursor is base64url of <c>{ createdAt, id }</c> and is deliberately NOT signed: every list query
/// re-applies the tenant and key restrictions, so a forged cursor can only page within rows the caller is
/// already allowed to read.
/// </summary>
public static class PublicApiPagination
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static int NormalizeLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    public static string EncodeCursor(DateTime createdAt, Guid id)
    {
        string raw = createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "|" + id.ToString("D");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecodeCursor(string? cursor, out DateTime createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor)) return false;

        string padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            string raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            string[] parts = raw.Split('|');
            return parts.Length == 2
                && DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out createdAt)
                && Guid.TryParse(parts[1], out id);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>A page of the public API's list endpoints.</summary>
public sealed record PublicApiPage<T>(IReadOnlyList<T> Data, string? NextCursor);

/// <summary>
/// The OpenAPI schema of every non-2xx body: <c>{ "error": { "code", "message", ...extras } }</c>. The real bodies
/// are built by <see cref="PublicApiEnvelope"/> and may carry extra members (limit, resetsAt, …); this type exists so
/// the document can reference one error shape instead of leaving those responses untyped.
/// </summary>
public sealed record PublicApiErrorEnvelope(PublicApiErrorBody Error);

/// <summary>
/// The inner <c>error</c> object of <see cref="PublicApiErrorEnvelope"/>: <c>code</c> and <c>message</c> on every
/// error, plus the members only some errors carry. Every extra is optional, so a strict generated client accepts
/// each documented body (402 caps, 403 scopes, 429 limits, 400 details).
/// </summary>
public sealed record PublicApiErrorBody(
    string Code,
    string Message,
    DateTimeOffset? ResetsAt = null,
    int? RetryAfterSeconds = null,
    string? Window = null,
    int? WindowMinutes = null,
    int? Limit = null,
    int? Used = null,
    string? Bucket = null,
    bool? Busy = null,
    decimal? CapCredits = null,
    decimal? UsedCredits = null,
    decimal? RequiredCredits = null,
    string[]? RequiredScopes = null,
    Dictionary<string, string[]>? Details = null);
