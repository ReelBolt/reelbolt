using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers.Admin;

/// <summary>
/// The platform-admin view of the marketing-consent list (saas-promotions). Under
/// <c>/api/v1/marketing/</c>, not <c>/api/v1/admin/*</c> (which nginx sends to the Go API).
///
/// <para>The CSV is the sendable list as of now. It is streamed and never stored server-side
/// (<c>Cache-Control: no-store</c>); each export is logged and recorded in
/// <c>marketing_consent_exports</c>. The process rule — re-export right before every send, use our
/// <c>List-Unsubscribe</c>, sync suppressions back from the sending tool — is stated in the admin UI.</para>
/// </summary>
[ApiController]
[Route("api/v1/marketing/admin/consents")]
[Authorize]
public class MarketingConsentAdminController(MarketingConsentService consents, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        return Ok(await consents.GetAdminSummaryAsync(ct));
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv(CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();

        MarketingCsvExport? export = await consents.ExportCsvAsync(currentUser.UserId, ct);
        if (export is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "public_base_url_missing",
                message = "Set Marketing:PublicBaseUrl (or PUBLIC_BASE_URL) so the export can carry unsubscribe links.",
            });
        }

        Response.Headers.CacheControl = "no-store";
        return File(export.Content, "text/csv; charset=utf-8", export.FileName);
    }
}
