using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary><c>NoDailyCap</c>: the owner chooses no daily cap (needs <c>confirmCaps</c>).</summary>
public sealed record ApiKeyCapsDto(long? DailyCredits, long? WeeklyCredits, long? MonthlyCredits, bool? NoDailyCap = null);

public sealed record CreateApiKeyRequest(
    string? Name,
    string[]? Scopes,
    Guid[]? ProjectIds,
    DateTimeOffset? ExpiresAt,
    ApiKeyCapsDto? Caps,
    bool? ConfirmCaps = null);

/// <summary>Name and caps only: scopes, projects and expiry are immutable (make a new key).</summary>
public sealed record UpdateApiKeyRequest(
    string? Name,
    ApiKeyCapsDto? Caps,
    string[]? Scopes = null,
    Guid[]? ProjectIds = null,
    DateTimeOffset? ExpiresAt = null,
    bool? ConfirmCaps = null);

public sealed record ApiKeyCreatorDto(Guid Id, string Name);

public sealed record ApiKeyProjectDto(Guid Id, string? Name);

public sealed record ApiKeyCapsView(decimal? DailyCredits, decimal? WeeklyCredits, decimal? MonthlyCredits);

/// <summary><c>status</c>: capped, waived (owner removed it) or legacy_uncapped (made before the default existed).</summary>
public sealed record ApiKeyDailyCapView(string Status, long DefaultCredits);

public sealed record ApiKeyItemDto(
    Guid Id,
    string Name,
    string KeyPrefix,
    string LastFour,
    ApiKeyCreatorDto CreatedBy,
    string[] Scopes,
    Guid[]? ProjectIds,
    ApiKeyProjectDto[]? Projects,
    ApiKeyCapsView Caps,
    DateTime? ExpiresAt,
    bool NeverExpires,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    string Status,
    DateTime? RevokedAt,
    ApiKeyUsageView? Usage = null,
    ApiKeyDailyCapView? DailyCap = null);

/// <summary>The list response: <c>capsEnforced</c> is false when billing is off (no holds exist, so caps do nothing).</summary>
public sealed record ApiKeyListDto(IReadOnlyList<ApiKeyItemDto> Items, bool CapsEnforced, long DefaultDailyCapCredits = 200);

public sealed record ApiKeyCreatedDto(ApiKeyItemDto Key, string Secret);

/// <summary>
/// Dashboard management of the organization's API keys. Session principal only (a scoped principal never reaches
/// it: the scope guard refuses every scoped token on routes without <c>RequireScope</c>). Org Owner/Admin.
/// </summary>
[ApiController]
[Route("api/v1/org/api-keys")]
[Authorize]
public sealed class OrgApiKeysController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IApiKeyService keys,
    IProjectAccessService projectAccess,
    IOrganizationEntitlements entitlements,
    IOptions<PublicApiOptions> options,
    TimeProvider? time = null,
    IApiKeyUsageReader? usageReader = null,
    IPublishEndpoint? publish = null) : ControllerBase
{
    private const int ListCap = 200;
    private const int MaxProjects = 50;
    private const long MaxCapCredits = 10_000_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private PublicApiOptions Options => options.Value;

    [HttpGet]
    public async Task<ActionResult> List(CancellationToken ct)
    {
        if (Gate(out Guid orgId) is { } denied) return denied;

        List<ApiKey> rows = await db.ApiKeys.AsNoTracking()
            .Where(k => k.OrganizationId == orgId)
            .OrderByDescending(k => k.CreatedAt).ThenBy(k => k.Id)
            .Take(ListCap)
            .ToListAsync(ct);
        return Ok(new ApiKeyListDto(await ToItemsAsync(orgId, rows, ct), usageReader?.CapsEnforced ?? false, Options.DefaultDailyCapCredits));
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] CreateApiKeyRequest? request, CancellationToken ct)
    {
        if (Gate(out Guid orgId) is { } denied) return denied;

        // A plan with no API access (Trial) may hold read-only keys; the guard refuses its writes.
        if (request is null) return Error(400, "invalid_request", "A request body is required.");
        DateTime now = _time.GetUtcNow().UtcDateTime;

        if (!TryName(request.Name, out string name)) return Error(400, "invalid_name", "name must be 1 to 80 characters without control characters.");

        string[] scopes = request.Scopes ?? [];
        if (scopes.Length == 0) return Error(400, "invalid_scope", "At least one scope is required.");
        string? unknown = scopes.FirstOrDefault(s => !ApiScopes.IsKnown(s));
        if (unknown is not null) return Error(400, "invalid_scope", $"Unknown scope '{unknown}'.");

        // Optional: null = the key never expires. A date must be 1 h .. MaxLifetimeDays ahead.
        DateTime? expires = request.ExpiresAt?.UtcDateTime;
        if (expires is { } e)
        {
            if (e <= now.AddHours(1)) return Error(400, "invalid_expiry", "expiresAt must be more than one hour from now.");
            // Q-9: a browser clock a little ahead must not fail. The value is stored exactly as sent.
            if (e > now.AddDays(Options.MaxLifetimeDays).AddMinutes(10))
                return Error(400, "invalid_expiry", $"expiresAt must be within {Options.MaxLifetimeDays} days.");
        }

        if (!TryCaps(request.Caps, out long? daily, out long? weekly, out long? monthly, out string? capError))
            return Error(400, "invalid_caps", capError!);

        // The daily cap defaults to PublicApi:DefaultDailyCapCredits; raising or removing it must be confirmed.
        bool noDailyCap = request.Caps?.NoDailyCap == true;
        if (noDailyCap && daily is not null) return Error(400, "invalid_caps", "Send either dailyCredits or noDailyCap, not both.");
        if ((noDailyCap || daily is { } d0 && d0 > Options.DefaultDailyCapCredits) && request.ConfirmCaps != true) return ConfirmRequired();
        DateTime? waivedAt = noDailyCap ? now : null;
        if (!noDailyCap) daily ??= Options.DefaultDailyCapCredits;

        Guid[]? projectIds = null;
        if (request.ProjectIds is { } requested)
        {
            projectIds = requested.Distinct().ToArray();
            if (projectIds.Length == 0) return Error(400, "invalid_project", "projectIds must not be empty; omit it for all projects.");
            if (projectIds.Length > MaxProjects) return Error(400, "invalid_project", $"At most {MaxProjects} projects.");
            foreach (Guid projectId in projectIds)
            {
                ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
                if (!access.IsFound)
                    return Error(400, "invalid_project", $"Project {projectId} was not found or you cannot view it.");
            }
        }

        int active = await db.ApiKeys.CountAsync(
            k => k.OrganizationId == orgId && k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now), ct);
        if (active >= Options.MaxKeysPerOrg)
            return Error(409, "api_key_limit", $"An organization can have at most {Options.MaxKeysPerOrg} active API keys.");

        CreatedApiKey created = await keys.CreateAsync(new CreateApiKeyCommand(
            orgId, currentUser.UserId, name, scopes, projectIds, expires,
            ToMillis(daily), ToMillis(weekly), ToMillis(monthly), waivedAt), ct);

        ApiKeyItemDto item = (await ToItemsAsync(orgId, [created.Key], ct))[0];
        return StatusCode(StatusCodes.Status201Created, new ApiKeyCreatedDto(item, created.Token));
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult> Update(Guid id, [FromBody] UpdateApiKeyRequest? request, CancellationToken ct)
    {
        if (Gate(out Guid orgId) is { } denied) return denied;
        if (request is null) return Error(400, "invalid_request", "A request body is required.");
        if (request.Scopes is not null || request.ProjectIds is not null || request.ExpiresAt is not null)
            return Error(400, "immutable_field", "Scopes, projects and expiry cannot be changed; create a new key.");

        ApiKey? key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.OrganizationId == orgId, ct);
        if (key is null) return NotFound();

        if (request.Name is not null)
        {
            if (!TryName(request.Name, out string name)) return Error(400, "invalid_name", "name must be 1 to 80 characters without control characters.");
            key.Name = name;
            // The service user shows the key name in member history.
            ApplicationUser? service = await db.ApplicationUsers.FirstOrDefaultAsync(u => u.Id == key.ServiceUserId && u.IsServiceAccount, ct);
            if (service is not null) service.DisplayName = name;
        }

        if (request.Caps is not null)
        {
            if (!TryCaps(request.Caps, out long? daily, out long? weekly, out long? monthly, out string? capError))
                return Error(400, "invalid_caps", capError!);
            bool noDailyCap = request.Caps.NoDailyCap == true;
            if (noDailyCap && daily is not null) return Error(400, "invalid_caps", "Send either dailyCredits or noDailyCap, not both.");
            // Confirm only a RAISE (above the default and above the key's current cap) or a REMOVAL (the key
            // has a cap, or is legacy). Same value, a lower value, or re-saving a waived key needs none.
            long ceiling = Math.Max(Options.DefaultDailyCapCredits, (key.DailyCapMillis ?? 0) / 1000);
            bool raises = daily is { } requested && requested > ceiling;
            bool removes = noDailyCap && (key.DailyCapMillis is not null || key.DailyCapWaivedAt is null);
            if ((raises || removes) && request.ConfirmCaps != true) return ConfirmRequired();

            // Daily absent (and no noDailyCap) = unchanged; weekly and monthly keep the overwrite semantics.
            if (noDailyCap)
            {
                key.DailyCapMillis = null;
                key.DailyCapWaivedAt ??= _time.GetUtcNow().UtcDateTime;
            }
            else if (daily is { } newDaily)
            {
                key.DailyCapMillis = ToMillis(newDaily);
                key.DailyCapWaivedAt = null;
            }

            key.WeeklyCapMillis = ToMillis(weekly);
            key.MonthlyCapMillis = ToMillis(monthly);
        }

        await db.SaveChangesAsync(ct);
        return Ok((await ToItemsAsync(orgId, [key], ct))[0]);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (Gate(out Guid orgId) is { } denied) return denied;

        ApiKey? key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.OrganizationId == orgId, ct);
        if (key is null) return NotFound();

        // Idempotent: the first revoke stamps the key; a repeat re-applies the (already-idempotent) side
        // effects below without moving RevokedAt.
        if (key.RevokedAt is null)
        {
            key.RevokedAt = _time.GetUtcNow().UtcDateTime;
            key.RevokedByUserId = currentUser.UserId;
        }

        // S-M4: webhook endpoints this key registered die with it (T-024's enqueue and worker skip the
        // disabled ones).
        List<WebhookEndpoint> endpoints = await db.WebhookEndpoints
            .Where(e => e.OrganizationId == orgId && e.CreatedByApiKeyId == id)
            .ToListAsync(ct);
        foreach (WebhookEndpoint endpoint in endpoints)
        {
            endpoint.Enabled = false;
            endpoint.DisabledReason = "api_key_revoked";
        }

        // S-M3b: a queued run request of the key never starts.
        List<ApiRunRequest> queued = await db.ApiRunRequests
            .Where(r => r.OrganizationId == orgId && r.ApiKeyId == id && r.Status == ApiRunRequest.Queued)
            .ToListAsync(ct);
        DateTime now = _time.GetUtcNow().UtcDateTime;
        foreach (ApiRunRequest request in queued)
        {
            request.Status = ApiRunRequest.Cancelled;
            request.UpdatedAt = now;
        }

        // S-M3b: stop what the key already started, through the SAME stop event the dashboard's stop
        // button publishes. The plain-words reason makes the engine say "API key revoked" rather than
        // blaming the person who happened to be signed in.
        List<Guid> inFlight = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.OrganizationId == orgId && e.ApiKeyId == id
                && (e.Status == ExecutionStatus.Queued || e.Status == ExecutionStatus.Running))
            .Select(e => e.Id)
            .ToListAsync(ct);

        // One unit: the revoke, the disabled endpoints and the cancelled run requests commit together.
        await db.SaveChangesAsync(ct);

        if (publish is not null)
        {
            foreach (Guid executionId in inFlight)
                await publish.Publish(new WorkflowExecutionStopRequested
                {
                    ExecutionId = executionId,
                    RequestedByUserId = currentUser.UserId,
                    Reason = WorkflowExecutionStopRequested.ApiKeyRevokedReason
                }, ct);
        }

        return NoContent();
    }

    /// <summary>
    /// The key's audit trail, newest first. The cursor is the last row's numeric id (plain: the query always
    /// re-applies the organization and key filter, so a forged cursor reveals nothing).
    /// </summary>
    [HttpGet("{id:guid}/audit")]
    public async Task<ActionResult> Audit(Guid id, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
    {
        if (Gate(out Guid orgId) is { } denied) return denied;
        if (!await db.ApiKeys.AsNoTracking().AnyAsync(k => k.Id == id && k.OrganizationId == orgId, ct)) return NotFound();

        int take = Math.Clamp(limit ?? 50, 1, 200);
        long? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!long.TryParse(cursor, out long parsed)) return Error(400, "invalid_cursor", "The cursor is not valid.");
            after = parsed;
        }

        IQueryable<ApiKeyAuditEvent> query = db.ApiKeyAuditEvents.AsNoTracking()
            .Where(e => e.ApiKeyId == id && e.OrganizationId == orgId);
        if (after is { } a) query = query.Where(e => e.Id < a);
        List<ApiKeyAuditEvent> rows = await query.OrderByDescending(e => e.Id).Take(take + 1).ToListAsync(ct);
        bool more = rows.Count > take;
        if (more) rows.RemoveAt(take);
        return Ok(new
        {
            data = rows.Select(e => new { e.Id, e.OccurredAt, e.Method, e.Route, e.StatusCode, e.DurationMs, e.RequestId }),
            nextCursor = more ? rows[^1].Id.ToString() : null,
        });
    }

    /// <summary>Flag off or no organization: 404. Member/Billing: 403.</summary>
    private ActionResult? Gate(out Guid orgId)
    {
        orgId = default;
        if (!Options.Enabled) return NotFound();
        if (currentUser.OrganizationId is not { } org || currentUser.OrgRole is null) return NotFound();
        if (!OrgAuthority.IsOrganizationAdmin(currentUser.OrgRole)) return Forbid();
        orgId = org;
        return null;
    }

    private ObjectResult Error(int status, string code, string message) =>
        StatusCode(status, new { code, message });

    private ObjectResult ConfirmRequired() => Error(400, "cap_confirmation_required",
        $"New keys can spend {Options.DefaultDailyCapCredits} credits a day by default. Removing the daily limit, or setting it higher than {Options.DefaultDailyCapCredits}, lets this key spend more without a stop. Send confirmCaps: true to confirm.");

    private static long? ToMillis(long? credits) => credits is { } c ? c * 1000 : null;

    private static bool TryName(string? raw, out string name)
    {
        // S-L5: the shared project-name rule also refuses zero-width and bidi control characters.
        name = ProjectOrganizationRules.NormalizeName(raw, 80) ?? string.Empty;
        return name.Length > 0;
    }

    private static bool TryCaps(ApiKeyCapsDto? caps, out long? daily, out long? weekly, out long? monthly, out string? error)
    {
        daily = caps?.DailyCredits;
        weekly = caps?.WeeklyCredits;
        monthly = caps?.MonthlyCredits;
        error = null;
        foreach ((string label, long? value) in new[] { ("dailyCredits", daily), ("weeklyCredits", weekly), ("monthlyCredits", monthly) })
        {
            if (value is { } v && (v < 1 || v > MaxCapCredits))
            {
                error = $"{label} must be between 1 and {MaxCapCredits}.";
                return false;
            }
        }

        return true;
    }

    private async Task<List<ApiKeyItemDto>> ToItemsAsync(Guid orgId, IReadOnlyList<ApiKey> rows, CancellationToken ct)
    {
        Guid[] creatorIds = rows.Select(r => r.CreatedByUserId).Distinct().ToArray();
        Guid[] serviceIds = rows.Select(r => r.ServiceUserId).Distinct().ToArray();
        Dictionary<Guid, string> creatorNames = await db.ApplicationUsers.AsNoTracking()
            .Where(u => creatorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        HashSet<Guid> members = (await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.OrganizationId == orgId && serviceIds.Contains(m.UserId))
            .Select(m => m.UserId)
            .ToListAsync(ct)).ToHashSet();
        Guid[] projectIds = rows.Where(r => r.ProjectIds is not null).SelectMany(r => r.ProjectIds!).Distinct().ToArray();
        Dictionary<Guid, string> projectNames = projectIds.Length == 0
            ? []
            : await db.Projects.AsNoTracking()
                .Where(p => p.OrganizationId == orgId && projectIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        DateTime now = _time.GetUtcNow().UtcDateTime;
        Dictionary<Guid, ApiKeyUsageView>? usage = usageReader is null ? null : await usageReader.ReadAsync(rows, now, ct);
        return rows.Select(k =>
        {
            string status = k.RevokedAt is not null ? "revoked"
                : k.ExpiresAt is { } exp && DateTime.SpecifyKind(exp, DateTimeKind.Utc) <= now ? "expired"
                : !members.Contains(k.ServiceUserId) ? "inactive"
                : "active";
            return new ApiKeyItemDto(
                k.Id,
                k.Name,
                ApiKeyToken.Prefix + k.KeyId,
                k.LastFour,
                new ApiKeyCreatorDto(k.CreatedByUserId, creatorNames.GetValueOrDefault(k.CreatedByUserId, string.Empty)),
                k.Scopes,
                k.ProjectIds,
                k.ProjectIds?.Select(p => new ApiKeyProjectDto(p, projectNames.GetValueOrDefault(p))).ToArray(),
                new ApiKeyCapsView(Credits(k.DailyCapMillis), Credits(k.WeeklyCapMillis), Credits(k.MonthlyCapMillis)),
                k.ExpiresAt,
                k.ExpiresAt is null,
                k.CreatedAt,
                k.LastUsedAt,
                status,
                k.RevokedAt,
                usage?.GetValueOrDefault(k.Id),
                new ApiKeyDailyCapView(
                    k.DailyCapMillis is not null ? "capped" : k.DailyCapWaivedAt is not null ? "waived" : "legacy_uncapped",
                    Options.DefaultDailyCapCredits));
        }).ToList();
    }

    private static decimal? Credits(long? millis) => millis is { } m ? m / 1000m : null;
}
