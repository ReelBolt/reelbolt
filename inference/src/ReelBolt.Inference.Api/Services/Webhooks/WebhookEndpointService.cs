using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.PublicApi;

namespace ReelBolt.Inference.Api.Services.Webhooks;

/// <summary>Who is managing webhooks: an API key (maybe restricted to projects) or a dashboard session.</summary>
public sealed record WebhookActor(Guid OrganizationId, Guid UserId, Guid? ApiKeyId, IReadOnlyList<Guid>? RestrictedProjectIds);

/// <summary>
/// A refusal; <see cref="RetryAfterSeconds"/> and <see cref="ResetsAt"/> are set only by a 429, so the public
/// surface can send <c>Retry-After</c> and <c>error.resetsAt</c> beside the code.
/// </summary>
public sealed record WebhookFailure(int Status, string Code, string Message, int? RetryAfterSeconds = null, DateTime? ResetsAt = null);

/// <summary>A value or the refusal; the controllers shape the refusal for their own surface.</summary>
public sealed record WebhookResult<T>(T? Value, WebhookFailure? Failure)
    where T : class
{
    public static WebhookResult<T> Ok(T value) => new(value, null);

    public static WebhookResult<T> Fail(int status, string code, string message, int? retryAfterSeconds = null, DateTime? resetsAt = null) =>
        new(null, new WebhookFailure(status, code, message, retryAfterSeconds, resetsAt));
}

public sealed record WebhookEndpointCreate(string? Url, string? Description, string[]? Events, Guid[]? ProjectIds);

/// <summary>Null members are left unchanged; an empty <c>ProjectIds</c> means "all projects" (when the actor may).</summary>
public sealed record WebhookEndpointPatch(string? Url, string? Description, string[]? Events, Guid[]? ProjectIds, bool? Enabled);

public sealed record WebhookEndpointView(
    Guid Id, string Url, string? Description, string[] Events, Guid[]? ProjectIds, bool Enabled, string? DisabledReason,
    DateTime CreatedAt, DateTime? LastSuccessAt, DateTime? FailingSince,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Secret = null);

public sealed record WebhookDeliveryView(
    string Id, string EventType, string Status, int Attempts, DateTime? NextAttemptAt, int? LastStatusCode, string? LastError,
    DateTime CreatedAt, DateTime? DeliveredAt);

public sealed record WebhookDeliveryPage(IReadOnlyList<WebhookDeliveryView> Data, string? NextCursor);

/// <summary>
/// The endpoint CRUD both surfaces share (public <c>/api/public/v1/webhooks</c> and the dashboard's
/// <c>/api/v1/org/webhooks</c>): one set of rules and error codes. Every query names the organization; a
/// project-restricted key sees and creates only endpoints scoped to projects inside its own list.
/// </summary>
public sealed class WebhookEndpointService(
    InferenceApiDbContext db,
    WebhookSecretProtector secrets,
    WebhookUrlValidator urls,
    IOptions<PublicApiOptions> options,
    TimeProvider? time = null)
{
    private const int MaxProjectIds = 50;
    private const int MaxDescription = 200;
    private const int MaxLastError = 300;

    /// <summary>At most this many test pings may be queued for one endpoint in a rolling minute.</summary>
    public const int MaxTestsPerMinute = 5;

    /// <summary>The 429 code a test ping gets when one is still pending or the minute's budget is spent.</summary>
    public const string TestRateLimitedCode = "webhook_test_rate_limited";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private WebhookOptions Settings => options.Value.Webhooks;

    public async Task<IReadOnlyList<WebhookEndpointView>> ListAsync(WebhookActor actor, CancellationToken ct)
    {
        List<WebhookEndpoint> rows = await db.WebhookEndpoints.AsNoTracking()
            .Where(e => e.OrganizationId == actor.OrganizationId)
            .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(ct);
        return rows.Where(e => Visible(actor, e)).Select(e => ToView(actor, e)).ToList();
    }

    public async Task<WebhookResult<WebhookEndpointView>> GetAsync(WebhookActor actor, Guid id, CancellationToken ct)
    {
        WebhookEndpoint? row = await FindAsync(actor, id, tracking: false, ct);
        return row is null ? NotFound<WebhookEndpointView>() : WebhookResult<WebhookEndpointView>.Ok(ToView(actor, row));
    }

    public async Task<WebhookResult<WebhookEndpointView>> CreateAsync(WebhookActor actor, WebhookEndpointCreate request, CancellationToken ct)
    {
        string url = request.Url?.Trim() ?? string.Empty;
        if (url.Length == 0) return Invalid<WebhookEndpointView>("url is required.");
        if (CheckDescription(request.Description, out string? description) is { } descriptionError) return Invalid<WebhookEndpointView>(descriptionError);
        if (NormalizeEvents(request.Events) is not { } events) return Invalid<WebhookEndpointView>($"events may only contain: {string.Join(", ", WebhookEventTypes.Subscribable)}.");

        WebhookFailure? failure = await CheckProjectsAsync(actor, request.ProjectIds, ct);
        if (failure is not null) return WebhookResult<WebhookEndpointView>.Fail(failure.Status, failure.Code, failure.Message);
        Guid[]? projectIds = NormalizeProjects(request.ProjectIds);

        int existing = await db.WebhookEndpoints.CountAsync(e => e.OrganizationId == actor.OrganizationId, ct);
        if (existing >= Math.Max(1, Settings.MaxEndpointsPerOrg))
            return WebhookResult<WebhookEndpointView>.Fail(StatusCodes.Status409Conflict, "webhook_limit_reached",
                $"This organization already has the most webhook endpoints it may hold ({Settings.MaxEndpointsPerOrg}). Delete one first.");

        if (await urls.ValidateAsync(url, ct) is { } urlError)
            return WebhookResult<WebhookEndpointView>.Fail(StatusCodes.Status400BadRequest, WebhookUrlValidator.NotPublicCode, urlError);

        string secret = StandardWebhookSigner.GenerateSecret();
        WebhookEndpoint row = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = actor.OrganizationId,
            Url = url,
            Description = description,
            Events = events,
            ProjectIds = projectIds,
            SecretEncrypted = secrets.Protect(secret),
            Enabled = true,
            CreatedByUserId = actor.UserId,
            CreatedByApiKeyId = actor.ApiKeyId,
            CreatedAt = _time.GetUtcNow().UtcDateTime
        };
        db.WebhookEndpoints.Add(row);
        await db.SaveChangesAsync(ct);
        return WebhookResult<WebhookEndpointView>.Ok(ToView(actor, row, secret));
    }

    public async Task<WebhookResult<WebhookEndpointView>> UpdateAsync(
        WebhookActor actor, Guid id, WebhookEndpointPatch patch, CancellationToken ct)
    {
        WebhookEndpoint? row = await FindAsync(actor, id, tracking: true, requireOwnership: true, ct: ct);
        if (row is null) return NotFound<WebhookEndpointView>();

        if (patch.Url is not null)
        {
            string url = patch.Url.Trim();
            if (url.Length == 0) return Invalid<WebhookEndpointView>("url must not be empty.");
            if (!string.Equals(url, row.Url, StringComparison.Ordinal))
            {
                if (await urls.ValidateAsync(url, ct) is { } urlError)
                    return WebhookResult<WebhookEndpointView>.Fail(StatusCodes.Status400BadRequest, WebhookUrlValidator.NotPublicCode, urlError);
                row.Url = url;
            }
        }

        if (patch.Description is not null)
        {
            if (CheckDescription(patch.Description, out string? description) is { } descriptionError) return Invalid<WebhookEndpointView>(descriptionError);
            row.Description = description;
        }

        if (patch.Events is not null)
        {
            if (NormalizeEvents(patch.Events) is not { } events) return Invalid<WebhookEndpointView>($"events may only contain: {string.Join(", ", WebhookEventTypes.Subscribable)}.");
            row.Events = events;
        }

        if (patch.ProjectIds is not null)
        {
            WebhookFailure? failure = await CheckProjectsAsync(actor, patch.ProjectIds, ct);
            if (failure is not null) return WebhookResult<WebhookEndpointView>.Fail(failure.Status, failure.Code, failure.Message);
            row.ProjectIds = NormalizeProjects(patch.ProjectIds);
        }

        if (patch.Enabled is bool enabled)
        {
            row.Enabled = enabled;
            if (enabled)
            {
                // Re-enabling starts a clean 72-hour window.
                row.FailingSince = null;
                row.DisabledReason = null;
            }
            else
            {
                row.DisabledReason = "Disabled by a member.";
            }
        }

        await db.SaveChangesAsync(ct);
        return WebhookResult<WebhookEndpointView>.Ok(ToView(actor, row));
    }

    public async Task<WebhookResult<WebhookEndpointView>> DeleteAsync(WebhookActor actor, Guid id, CancellationToken ct)
    {
        WebhookEndpoint? row = await FindAsync(actor, id, tracking: true, requireOwnership: true, ct: ct);
        if (row is null) return NotFound<WebhookEndpointView>();

        // The deliveries go with it (FK cascade).
        db.WebhookEndpoints.Remove(row);
        await db.SaveChangesAsync(ct);
        return WebhookResult<WebhookEndpointView>.Ok(ToView(actor, row));
    }

    /// <summary>
    /// A new secret; the old one keeps signing alongside it for <c>PreviousSecretHours</c>.
    /// <paramref name="revokePrevious"/> drops the old one immediately instead (S-L8).
    /// </summary>
    public async Task<WebhookResult<WebhookEndpointView>> RotateSecretAsync(
        WebhookActor actor, Guid id, bool revokePrevious, CancellationToken ct)
    {
        WebhookEndpoint? row = await FindAsync(actor, id, tracking: true, requireOwnership: true, ct: ct);
        if (row is null) return NotFound<WebhookEndpointView>();

        string secret = StandardWebhookSigner.GenerateSecret();
        row.SecretPreviousEncrypted = revokePrevious ? null : row.SecretEncrypted;
        row.SecretEncrypted = secrets.Protect(secret);
        row.SecretRotatedAt = _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return WebhookResult<WebhookEndpointView>.Ok(ToView(actor, row, secret));
    }

    /// <summary>
    /// Queues a <c>webhook.test</c> delivery for right now. Refused with 429 while one test ping for the endpoint
    /// is still pending, and once <see cref="MaxTestsPerMinute"/> were queued in the last minute (S-M6).
    /// </summary>
    public async Task<WebhookResult<WebhookDeliveryView>> TestAsync(WebhookActor actor, Guid id, CancellationToken ct)
    {
        WebhookEndpoint? row = await FindAsync(actor, id, tracking: false, requireOwnership: true, ct: ct);
        if (row is null) return NotFound<WebhookDeliveryView>();
        if (!row.Enabled)
            return WebhookResult<WebhookDeliveryView>.Fail(StatusCodes.Status409Conflict, "webhook_disabled", "The endpoint is disabled. Enable it first.");

        DateTime now = _time.GetUtcNow().UtcDateTime;

        DateTime? pendingAt = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.EndpointId == id && d.EventType == WebhookEventTypes.Test && d.Status == WebhookDelivery.Pending)
            .OrderBy(d => d.NextAttemptAt)
            .Select(d => (DateTime?)d.NextAttemptAt)
            .FirstOrDefaultAsync(ct);
        if (pendingAt is { } pending)
            return TestRateLimited(pending > now ? pending : now.AddSeconds(1), now, "A test delivery for this endpoint is still pending.");

        DateTime windowStart = now.AddMinutes(-1);
        List<DateTime> recent = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.EndpointId == id && d.EventType == WebhookEventTypes.Test && d.CreatedAt > windowStart)
            .OrderBy(d => d.CreatedAt)
            .Select(d => d.CreatedAt)
            .ToListAsync(ct);
        if (recent.Count >= MaxTestsPerMinute)
            return TestRateLimited(recent[0].AddMinutes(1), now,
                $"At most {MaxTestsPerMinute} test deliveries may be queued per minute.");

        string body = WebhookEventWriter.BuildBody(WebhookEventTypes.Test, now, new WebhookEventData(Status: "test"));
        WebhookDelivery delivery = WebhookEventWriter.NewDelivery(row, WebhookEventTypes.Test, $"{WebhookEventTypes.Test}:{Guid.NewGuid():N}", body, now);
        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync(ct);
        return WebhookResult<WebhookDeliveryView>.Ok(ToView(delivery));
    }

    public async Task<WebhookResult<WebhookDeliveryPage>> ListDeliveriesAsync(
        WebhookActor actor, Guid id, int? limit, string? cursor, CancellationToken ct)
    {
        WebhookEndpoint? row = await FindAsync(actor, id, tracking: false, requireOwnership: true, ct: ct);
        if (row is null) return NotFound<WebhookDeliveryPage>();

        int take = PublicApiPagination.NormalizeLimit(limit);
        IQueryable<WebhookDelivery> query = db.WebhookDeliveries.AsNoTracking().Where(d => d.EndpointId == id);
        if (actor.RestrictedProjectIds is { } allowed)
            query = query.Where(d => d.ProjectId != null && allowed.Contains(d.ProjectId.Value));
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeCursor(cursor, out DateTime at, out string lastId))
                return Invalid<WebhookDeliveryPage>("The cursor is not valid.");
            query = query.Where(d => d.CreatedAt < at || (d.CreatedAt == at && d.Id.CompareTo(lastId) < 0));
        }

        List<WebhookDelivery> rows = await query
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Take(take + 1).ToListAsync(ct);
        bool more = rows.Count > take;
        if (more) rows.RemoveAt(take);
        return WebhookResult<WebhookDeliveryPage>.Ok(new WebhookDeliveryPage(
            rows.Select(ToView).ToList(), more ? EncodeCursor(rows[^1].CreatedAt, rows[^1].Id) : null));
    }

    /// <summary>Puts a delivery back in the queue under the SAME <c>webhook-id</c> (receivers dedupe on it).</summary>
    public async Task<WebhookResult<WebhookDeliveryView>> RedeliverAsync(WebhookActor actor, Guid id, string deliveryId, CancellationToken ct)
    {
        WebhookEndpoint? endpoint = await FindAsync(actor, id, tracking: false, requireOwnership: true, ct: ct);
        if (endpoint is null) return NotFound<WebhookDeliveryView>();

        WebhookDelivery? delivery = await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.EndpointId == id && d.Id == deliveryId, ct);
        if (delivery is null || !DeliveryVisible(actor, delivery)) return NotFound<WebhookDeliveryView>();
        if (delivery.Status == WebhookDelivery.Pending)
            return WebhookResult<WebhookDeliveryView>.Fail(StatusCodes.Status409Conflict, "delivery_pending", "The delivery is already waiting to be sent.");
        if (!endpoint.Enabled)
            return WebhookResult<WebhookDeliveryView>.Fail(StatusCodes.Status409Conflict, "webhook_disabled", "The endpoint is disabled. Enable it first.");

        delivery.Status = WebhookDelivery.Pending;
        delivery.Attempts = 0;
        delivery.NextAttemptAt = _time.GetUtcNow().UtcDateTime;
        delivery.LastError = null;
        delivery.LastStatusCode = null;
        delivery.DeliveredAt = null;
        await db.SaveChangesAsync(ct);
        return WebhookResult<WebhookDeliveryView>.Ok(ToView(delivery));
    }

    // ------------------------------------------------------------------------------------------ helpers

    private async Task<WebhookEndpoint?> FindAsync(
        WebhookActor actor, Guid id, bool tracking, CancellationToken ct, bool requireOwnership = false)
    {
        IQueryable<WebhookEndpoint> query = db.WebhookEndpoints.Where(e => e.Id == id && e.OrganizationId == actor.OrganizationId);
        if (!tracking) query = query.AsNoTracking();
        WebhookEndpoint? row = await query.FirstOrDefaultAsync(ct);
        if (row is null || !Visible(actor, row)) return null;

        // S-L6: a key acts only on the endpoints it created; another endpoint of the organization is "not found".
        if (requireOwnership && actor.ApiKeyId is Guid keyId && row.CreatedByApiKeyId != keyId) return null;
        return row;
    }

    /// <summary>A restricted key sees only endpoints limited to projects inside its own list.</summary>
    private static bool Visible(WebhookActor actor, WebhookEndpoint endpoint) =>
        actor.RestrictedProjectIds is null
        || (endpoint.ProjectIds is { Length: > 0 } ids && ids.All(actor.RestrictedProjectIds.Contains));

    /// <summary>S-L7: a restricted key sees only deliveries of its own projects; a delivery with none is hidden.</summary>
    private static bool DeliveryVisible(WebhookActor actor, WebhookDelivery delivery) =>
        actor.RestrictedProjectIds is not { } allowed
        || (delivery.ProjectId is Guid project && allowed.Contains(project));

    /// <summary>A 429 refusal carrying the header seconds and the instant the surface reports as <c>resetsAt</c>.</summary>
    private static WebhookResult<WebhookDeliveryView> TestRateLimited(DateTime resetsAt, DateTime now, string message)
    {
        if (resetsAt <= now) resetsAt = now.AddSeconds(1);
        int seconds = Math.Max(1, (int)Math.Ceiling((resetsAt - now).TotalSeconds));
        return WebhookResult<WebhookDeliveryView>.Fail(
            StatusCodes.Status429TooManyRequests, TestRateLimitedCode, message, seconds, resetsAt);
    }

    private async Task<WebhookFailure?> CheckProjectsAsync(WebhookActor actor, Guid[]? requested, CancellationToken ct)
    {
        Guid[] ids = requested?.Distinct().ToArray() ?? [];
        if (ids.Length > MaxProjectIds)
            return new WebhookFailure(StatusCodes.Status400BadRequest, PublicApiErrorCodes.InvalidRequest, $"projectIds may hold at most {MaxProjectIds} projects.");

        if (actor.RestrictedProjectIds is { } allowed)
        {
            if (ids.Length == 0)
                return new WebhookFailure(StatusCodes.Status403Forbidden, PublicApiErrorCodes.Forbidden,
                    "This API key is limited to specific projects, so its webhooks must list projectIds inside that limit.");
            if (ids.Any(p => !allowed.Contains(p)))
                return new WebhookFailure(StatusCodes.Status404NotFound, PublicApiErrorCodes.NotFound, "Project not found.");
        }

        if (ids.Length > 0)
        {
            int found = await db.Projects.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(p => ids.Contains(p.Id) && p.OrganizationId == actor.OrganizationId, ct);
            if (found != ids.Length)
                return new WebhookFailure(StatusCodes.Status404NotFound, PublicApiErrorCodes.NotFound, "Project not found.");
        }

        return null;
    }

    private static Guid[]? NormalizeProjects(Guid[]? requested)
    {
        Guid[] ids = requested?.Distinct().ToArray() ?? [];
        return ids.Length == 0 ? null : ids;
    }

    /// <summary>The distinct, known event types; the default set when none are given; null when one is unknown.</summary>
    private static string[]? NormalizeEvents(string[]? requested)
    {
        if (requested is null || requested.Length == 0) return [.. WebhookEventTypes.Default];
        string[] distinct = requested.Select(e => e?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        return distinct.All(WebhookEventTypes.IsSubscribable) ? distinct : null;
    }

    private static string? CheckDescription(string? raw, out string? description)
    {
        description = raw?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            description = null;
            return null;
        }

        if (description.Length > MaxDescription || description.Any(char.IsControl))
            return $"description must be at most {MaxDescription} characters without control characters.";
        return null;
    }

    private static WebhookResult<T> Invalid<T>(string message) where T : class =>
        WebhookResult<T>.Fail(StatusCodes.Status400BadRequest, PublicApiErrorCodes.InvalidRequest, message);

    private static WebhookResult<T> NotFound<T>() where T : class =>
        WebhookResult<T>.Fail(StatusCodes.Status404NotFound, PublicApiErrorCodes.NotFound, "Webhook not found.");

    /// <summary>
    /// The view both surfaces share. For a key actor the URL loses its query string and fragment (S-L6): an
    /// Admin may have put a token there, and the key must not read it back.
    /// </summary>
    public static WebhookEndpointView ToView(WebhookActor actor, WebhookEndpoint e, string? secret = null) => new(
        e.Id, actor.ApiKeyId is null ? e.Url : WithoutQuery(e.Url), e.Description, e.Events, e.ProjectIds, e.Enabled,
        e.DisabledReason, e.CreatedAt, e.LastSuccessAt, e.FailingSince, secret);

    private static string WithoutQuery(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return uri.GetLeftPart(UriPartial.Path);
        int cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    }

    public static WebhookDeliveryView ToView(WebhookDelivery d) => new(
        d.Id, d.EventType, d.Status, d.Attempts, d.Status == WebhookDelivery.Pending ? d.NextAttemptAt : null,
        d.LastStatusCode, d.LastError, d.CreatedAt, d.DeliveredAt);

    private static string EncodeCursor(DateTime createdAt, string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "|" + id))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeCursor(string cursor, out DateTime createdAt, out string id)
    {
        createdAt = default;
        id = string.Empty;
        string padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            if (parts.Length != 2 || parts[1].Length is 0 or > 64) return false;
            id = parts[1];
            return DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out createdAt);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Truncates and strips control characters from text kept in <c>last_error</c> or a response snippet.</summary>
    public static string Sanitize(string? text, int max = MaxLastError)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        StringBuilder sb = new(Math.Min(text.Length, max));
        foreach (char c in text)
        {
            if (sb.Length >= max) break;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString().Trim();
    }
}
