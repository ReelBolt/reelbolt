using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers.Admin;

/// <summary>
/// The Brevo contact sync status and the Sync now request, platform admin only. Sync now only records a request; the
/// worker picks it up within about ten seconds. Nothing here carries an address or a name.
/// </summary>
[ApiController]
[Route("api/v1/marketing/admin/contact-sync")]
[Authorize]
public class MarketingContactSyncAdminController(ContactSyncService sync, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet("")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        ContactSyncStatus s = await sync.StatusAsync(ct);
        return Ok(new
        {
            enabled = s.Enabled,
            configured = s.Configured,
            campaignActive = s.CampaignActive,
            lastRunAt = s.LastRunAt,
            lastRunOk = s.LastRunOk,
            lastErrorCode = s.LastErrorCode,
            lastReconcileAt = s.LastReconcileAt,
            counts = new { synced = s.Synced, pendingPush = s.PendingPush, failed = s.Failed },
        });
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        return await sync.RequestRunAsync(ct) switch
        {
            ContactSyncRequestOutcome.Accepted => Accepted(new { requested = true }),
            ContactSyncRequestOutcome.Disabled => Conflict(new { code = "sync_disabled" }),
            _ => StatusCode(StatusCodes.Status429TooManyRequests, new { code = "rate_limited" }),
        };
    }
}
