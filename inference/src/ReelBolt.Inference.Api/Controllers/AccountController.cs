using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// C9a: the data subject's own account endpoints. Today that is the export; the deletion *request*
/// lives in the Go API (<c>POST /api/v1/auth/account/delete</c>), because it is an identity operation
/// that must re-verify the password and kill the session, and this service has neither.
/// </summary>
[ApiController]
[Route("api/v1/account")]
[Authorize]
public sealed class AccountController : ControllerBase
{
    /// <summary>How long the short-lived re-issue link is valid. Well under the mailed 7-day one.</summary>
    public static readonly TimeSpan LinkTtl = TimeSpan.FromMinutes(15);

    private readonly AccountExportService _exports;
    private readonly ICurrentUser _currentUser;
    private readonly AccountOptions _options;

    public AccountController(AccountExportService exports, ICurrentUser currentUser, IOptions<AccountOptions> options)
    {
        _exports = exports;
        _currentUser = currentUser;
        _options = options.Value;
    }

    /// <summary>
    /// Queues an export of the caller's own data. One per 24 hours: a second request inside the
    /// window is <c>429</c> with <c>Retry-After</c>, never a silent queue of duplicates.
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> RequestExport(CancellationToken ct)
    {
        if (!_options.Export.Enabled)
            return NotFound();

        if (!_currentUser.IsAuthenticated)
            return Unauthorized();

        // The mail greets the reader by name, and ICurrentUser carries no display name: an empty
        // value renders the mailer's neutral "Hello there," rather than greeting someone by their
        // own email address.
        AccountExportRequestResult result = await _exports.RequestExportAsync(
            _currentUser.UserId, _currentUser.Email, displayName: string.Empty, ct);

        switch (result.Status)
        {
            case AccountExportRequestStatus.Accepted:
                return Accepted(new { status = "accepted" });

            case AccountExportRequestStatus.AlreadyRunning:
                Response.Headers.RetryAfter = "300";
                return StatusCode(StatusCodes.Status409Conflict, new { status = "already_running" });

            case AccountExportRequestStatus.TooSoon:
                int seconds = (int)Math.Ceiling((result.RetryAfter ?? TimeSpan.FromHours(24)).TotalSeconds);
                Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return StatusCode(StatusCodes.Status429TooManyRequests, new { status = "too_soon", retryAfterSeconds = seconds });

            default:
                return NotFound();
        }
    }

    /// <summary>
    /// The state the dashboard card renders: whether a copy is being prepared, the newest copy's date,
    /// size and expiry, and when another request will be accepted. Never a link: the link is minted
    /// only when the person actually clicks Download.
    /// </summary>
    [HttpGet("export/status")]
    public async Task<IActionResult> GetExportStatus(CancellationToken ct)
    {
        if (!_options.Export.Enabled)
            return NotFound();

        if (!_currentUser.IsAuthenticated)
            return Unauthorized();

        AccountExportStatus status = await _exports.GetStatusAsync(_currentUser.UserId, ct);
        return Ok(new
        {
            preparing = status.Preparing,
            latest = status.LatestCreatedAt is null
                ? null
                : new { createdAt = status.LatestCreatedAt, sizeBytes = status.LatestBytes, expiresAt = status.LatestExpiresAt },
            nextRequestAt = status.NextRequestAt
        });
    }

    /// <summary>
    /// A fresh short-lived link to the caller's newest export, or 404 when there is none. This is the
    /// mail's fallback and the self-host path (no SMTP), and it means an export is never a dead end
    /// just because the mail did not go out.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> GetExportLink(CancellationToken ct)
    {
        if (!_options.Export.Enabled)
            return NotFound();

        if (!_currentUser.IsAuthenticated)
            return Unauthorized();

        string? url = await _exports.CreateDownloadLinkAsync(_currentUser.UserId, LinkTtl, ct);
        return url is null
            ? NotFound(new { status = "none" })
            : Ok(new { url, expiresInSeconds = (int)LinkTtl.TotalSeconds });
    }
}
