using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers.Admin;

/// <summary>One switch, as an operator sees it.</summary>
public sealed record PlatformSettingResponse(
    string Key,
    bool Paused,
    string? Reason,
    int? PoolSlotCap,
    DateTime UpdatedAt,
    string? UpdatedBy);

/// <summary>The whole switch panel: every stored row plus the two derived flags and the disabled orgs.</summary>
public sealed record PlatformSettingsResponse(
    bool ComputePaused,
    bool PlatformInferencePaused,
    IReadOnlyList<Guid> DisabledOrganizationIds,
    IReadOnlyList<PlatformSettingResponse> Settings);

/// <summary>Set one switch. <c>paused: false</c> records a resume rather than deleting the row, so the audit trail survives.</summary>
public sealed record SetPlatformSettingRequest(bool Paused, string? Reason = null, int? PoolSlotCap = null);

/// <summary>
/// The platform kill switches (F11), read and written by a platform admin.
///
/// <para>
/// <b>Where this lives and why.</b> The rows are in <c>platform_settings</c>, which the Inference API
/// owns and is the only writer of. It is routed under <c>/api/v1/platform/settings</c> rather than
/// <c>/api/v1/admin/*</c> for the same reason rate cards are: nginx sends <c>/api/v1/admin/</c> to the
/// Go API, so a route there would never reach this service.
/// </para>
///
/// <para>
/// <b>Authority is per request and is the platform claim only.</b> An organisation Owner is refused:
/// <c>compute_paused</c> stops every tenant, so no tenant's role may set it. As everywhere else in this
/// service the check is the first statement of each action and is never cached in a field.
/// </para>
///
/// <para>
/// <b>Propagation.</b> Writing a row does not notify anything. The WorkflowEngine's reader and the
/// runner gateway's reader each hold the value for at most ten seconds, which is the whole
/// invalidation story — deliberately, because a switch that needed a message bus to take effect would
/// be a switch that could silently fail to take effect.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/platform/settings")]
[Authorize]
public class PlatformSettingsController(InferenceApiDbContext db, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>The largest per-organisation CloudPool cap an operator may set. A typo, not a policy.</summary>
    private const int MaxPoolSlotCap = 1000;

    [HttpGet]
    public async Task<ActionResult<PlatformSettingsResponse>> List(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        List<PlatformSetting> rows = await db.PlatformSettings.AsNoTracking()
            .OrderBy(s => s.Key)
            .ToListAsync(ct);

        return new PlatformSettingsResponse(
            rows.Any(s => s.Key == PlatformSetting.ComputeKey && s.Paused),
            rows.Any(s => s.Key == PlatformSetting.InferenceKey && s.Paused),
            rows.Where(s => s.Paused && PlatformSetting.TryParseOrganizationKey(s.Key, out _))
                .Select(s => PlatformSetting.TryParseOrganizationKey(s.Key, out Guid id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToList(),
            rows.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Sets <paramref name="key"/> (<c>compute</c>, <c>inference</c> or <c>org:&lt;guid&gt;</c>).
    /// Idempotent: writing the same value twice leaves one row with the newer audit fields.
    /// </summary>
    [HttpPut("{key}")]
    public async Task<ActionResult<PlatformSettingResponse>> Set(
        string key, SetPlatformSettingRequest request, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        if (!TryNormalizeKey(key, out string normalized))
            return BadRequest(new { message = "key must be 'compute', 'inference' or 'org:<organizationId>'." });

        if (request.Reason is { Length: > 500 })
            return BadRequest(new { message = "reason is at most 500 characters." });

        bool perOrganization = PlatformSetting.TryParseOrganizationKey(normalized, out _);
        if (!perOrganization && request.PoolSlotCap is not null)
            return BadRequest(new { message = "poolSlotCap is only meaningful on an org:<organizationId> switch." });

        if (request.PoolSlotCap is { } cap && (cap < 1 || cap > MaxPoolSlotCap))
            return BadRequest(new { message = $"poolSlotCap must be between 1 and {MaxPoolSlotCap}." });

        PlatformSetting? row = await db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == normalized, ct);
        if (row is null)
        {
            row = new PlatformSetting { Key = normalized };
            db.PlatformSettings.Add(row);
        }

        row.Paused = request.Paused;
        row.Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        row.PoolSlotCap = request.PoolSlotCap;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = currentUser.Email;

        await db.SaveChangesAsync(ct);

        return ToResponse(row);
    }

    /// <summary>
    /// Normalises the route key, rejecting anything this build does not own. An organisation id is
    /// canonicalised to lower case because the Go runner gateway reads the table by raw SQL and
    /// compares its keys itself; two rows differing only in case would be one switch that half works.
    /// </summary>
    private static bool TryNormalizeKey(string key, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(key) || !PlatformSetting.IsKnownKey(key))
        {
            return false;
        }

        normalized = PlatformSetting.TryParseOrganizationKey(key, out Guid organizationId)
            ? PlatformSetting.OrganizationKey(organizationId)
            : key;

        return true;
    }

    private static PlatformSettingResponse ToResponse(PlatformSetting row) =>
        new(row.Key, row.Paused, row.Reason, row.PoolSlotCap, row.UpdatedAt, row.UpdatedBy);
}
