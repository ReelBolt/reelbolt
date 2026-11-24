using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

public sealed record PublicApiKeyView(
    Guid Id,
    string Name,
    string Prefix,
    string[] Scopes,
    Guid[]? ProjectIds,
    DateTime? ExpiresAt,
    DateTime CreatedAt);

public sealed record PublicApiOrganizationView(Guid Id, string Name);

/// <summary>The key's own identity, never the creator's.</summary>
public sealed record PublicApiActingAsView(string Type, string Name);

public sealed record PublicApiLimitsView(int RequestsPerMinute, int WritesPerMinute);

/// <summary>
/// TODO(public-api T-004): the cap windows' usage. T-004 owns the spend reader and fills this; the field is
/// typed and null until that ticket lands on the branch.
/// </summary>
public sealed record PublicApiBudgetView(
    long? DailyCapMillis,
    long DailyUsedMillis,
    long? WeeklyCapMillis,
    long WeeklyUsedMillis,
    long? MonthlyCapMillis,
    long MonthlyUsedMillis);

public sealed record PublicApiMeView(
    PublicApiKeyView Key,
    PublicApiOrganizationView Organization,
    PublicApiActingAsView ActingAs,
    PublicApiLimitsView Limits,
    PublicApiBudgetView? Budget);

/// <summary><c>GET /api/public/v1/me</c>: the key, its organization, its scopes and limits. Any key may call it.</summary>
public sealed class MeController(
    InferenceApiDbContext db,
    IApiKeyContext keyContext,
    IOptions<PublicApiOptions> options) : PublicApiControllerBase
{
    [AnyApiKeyScope]
    [HttpGet("me")]
    [ProducesResponseType(typeof(PublicApiMeView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(CancellationToken ct)
    {
        if (keyContext.KeyId is not Guid keyId || keyContext.OrganizationId is not Guid organizationId)
            return PublicError(StatusCodes.Status401Unauthorized, PublicApiErrorCodes.ApiKeyRequired,
                PublicApiErrorCodes.ApiKeyRequiredMessage);

        ApiKey? key = await db.ApiKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == keyId && k.OrganizationId == organizationId, ct);
        if (key is null)
            return PublicError(StatusCodes.Status404NotFound, PublicApiErrorCodes.NotFound, "This API key no longer exists.");

        Organization? organization = await db.Organizations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == organizationId, ct);
        if (organization is null)
            return PublicError(StatusCodes.Status404NotFound, PublicApiErrorCodes.NotFound, "This organization no longer exists.");

        PublicApiOptions.RateLimitOptions limits = options.Value.RateLimit;
        return Ok(new PublicApiMeView(
            new PublicApiKeyView(
                key.Id,
                key.Name,
                ApiKeyToken.Prefix + key.KeyId,
                key.Scopes,
                key.ProjectIds,
                key.ExpiresAt,
                key.CreatedAt),
            new PublicApiOrganizationView(organization.Id, organization.Name),
            new PublicApiActingAsView("apiKey", key.Name),
            new PublicApiLimitsView(limits.RequestsPerMinute, limits.WritesPerMinute),
            Budget: null));
    }
}
