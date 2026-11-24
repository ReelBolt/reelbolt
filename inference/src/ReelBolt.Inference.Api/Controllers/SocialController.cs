using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Social publishing accounts (docs/social-publishing.md): the platforms on offer, connecting (OAuth),
/// listing and disconnecting. Accounts belong to the ACTIVE ORGANIZATION: Owner and Admin connect and
/// disconnect, every non-billing member lists. An account of another organization, or one that does not
/// exist, is the same <c>404 account_not_found</c> (the Tenant filter plus an explicit organization check),
/// so this API is never an existence oracle. Nothing here ever returns a token.
/// </summary>
[ApiController]
[Route("api/v1/social")]
[Authorize]
public class SocialController : ControllerBase
{
    /// <summary>Where the browser lands after connecting when the connect request named no valid page.</summary>
    public const string DefaultReturnPath = "/app/publishing";

    private readonly InferenceApiDbContext _db;
    private readonly ISocialPlatformRegistry _platforms;
    private readonly ISocialConnectionService _connections;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<SocialController> _logger;

    public SocialController(
        InferenceApiDbContext db,
        ISocialPlatformRegistry platforms,
        ISocialConnectionService connections,
        ICurrentUser currentUser,
        ILogger<SocialController> logger)
    {
        _db = db;
        _platforms = platforms;
        _connections = connections;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>The platforms this deployment can connect. An unconfigured platform is not listed at all.</summary>
    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<SocialProviderResponse>> Providers() =>
        Ok(_platforms.Configured
            .Select(p => new SocialProviderResponse(SocialPlatforms.Slug(p.Kind), SocialPlatforms.DisplayName(p.Kind)))
            .ToList());

    /// <summary>The active organization's accounts, newest first.</summary>
    [HttpGet("accounts")]
    public async Task<ActionResult<IReadOnlyList<SocialAccountResponse>>> Accounts(CancellationToken ct)
    {
        if (_currentUser.OrganizationId is not { } organizationId || _currentUser.OrgRole is null) return Ok(Array.Empty<SocialAccountResponse>());
        if (_currentUser.OrgRole is OrganizationRole.Billing) return Forbid();

        List<SocialAccount> rows = await _db.SocialAccounts
            .Where(a => a.OrganizationId == organizationId && a.Status != SocialAccountStatus.Revoked)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        return Ok(rows.Select(a => new SocialAccountResponse(
            a.Id, SocialPlatforms.Slug(a.Platform), a.DisplayName, a.AvatarUrl, a.Status, a.LastError, a.CreatedAt)).ToList());
    }

    /// <summary>
    /// Begins connecting an account: answers the URL of the platform's consent screen, which the dashboard
    /// navigates to. Owner/Admin only. <c>503 provider_not_configured</c> without the platform credentials,
    /// <c>503 public_base_url_missing</c> without <c>PUBLIC_BASE_URL</c>.
    /// </summary>
    [HttpPost("{provider}/connect")]
    public async Task<ActionResult<SocialConnectResponse>> Connect(string provider, [FromBody] SocialConnectRequest? request, CancellationToken ct)
    {
        if (!SocialPlatforms.TryParseSlug(provider, out SocialPlatformKind kind)) return NotFound(new { error = "provider_not_found" });
        if (_currentUser.OrganizationId is not { } organizationId || _currentUser.OrgRole is null)
            return NotFound(new { error = "no_workspace", message = "Choose a workspace before connecting an account." });
        if (!OrgAuthority.IsOrganizationAdmin(_currentUser.OrgRole))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden", message = "Only an owner or admin can connect an account." });

        SocialOAuthStart start = await _connections.StartAsync(kind, _currentUser.UserId, organizationId, request?.ReturnTo, ct);
        return start.Error switch
        {
            null => Ok(new SocialConnectResponse(start.AuthorizeUrl!)),
            SocialOAuthStart.PublicBaseUrlMissing => StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = start.Error,
                message = "Connecting accounts is not finished on this server: its public address is not set. Ask your administrator."
            }),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = SocialOAuthStart.ProviderNotConfigured,
                message = $"{SocialPlatforms.DisplayName(kind)} is not set up on this server. Ask your administrator."
            })
        };
    }

    /// <summary>
    /// Where the platform sends the browser back. Anonymous in the routing sense (a top-level navigation from
    /// the vendor, so nginx blanks nothing it needs and the generic <c>/api/v1/</c> location serves it), it
    /// validates its OWN state: a single-use ticket this deployment minted, spendable only by the person who
    /// started it. Every answer is a redirect to a same-origin <c>/app/…</c> path taken from our own ticket,
    /// with <c>?social=connected&amp;provider=…</c> or <c>?social=error&amp;provider=…&amp;reason=…</c>.
    /// </summary>
    [HttpGet("{provider}/oauth/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(
        string provider,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        if (!SocialPlatforms.TryParseSlug(provider, out SocialPlatformKind kind)) return NotFound(new { error = "provider_not_found" });

        Guid? caller = null;
        if (_currentUser.IsAuthenticated)
        {
            try { caller = _currentUser.UserId; }
            catch (InvalidOperationException) { caller = null; }
        }

        SocialOAuthCompletion completion;
        try
        {
            completion = await _connections.CompleteAsync(kind, state, code, error, caller, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The {Platform} account-connection callback failed unexpectedly", kind);
            completion = new SocialOAuthCompletion(SocialOAuthOutcome.ExchangeFailed, null);
        }

        if (!completion.Succeeded)
            _logger.LogInformation("A {Platform} account connection finished as {Outcome}", kind, completion.Code);

        string target = completion.ReturnPath ?? DefaultReturnPath;
        string separator = target.Contains('?') ? "&" : "?";
        string slug = SocialPlatforms.Slug(kind);
        string outcome = completion.Succeeded
            ? $"social=connected&provider={slug}"
            : $"social=error&provider={slug}&reason={Uri.EscapeDataString(completion.Code)}";
        return Redirect(target + separator + outcome);
    }

    /// <summary>Disconnects (Owner/Admin): best-effort revoke at the platform, tokens deleted now, metrics deleted.</summary>
    [HttpDelete("accounts/{id:guid}")]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken ct)
    {
        if (_currentUser.OrganizationId is not { } organizationId || _currentUser.OrgRole is null) return AccountNotFound();
        if (!OrgAuthority.IsOrganizationAdmin(_currentUser.OrgRole))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden", message = "Only an owner or admin can disconnect an account." });

        bool removed = await _connections.DisconnectAsync(id, organizationId, _currentUser.UserId, ct);
        return removed ? NoContent() : AccountNotFound();
    }

    private NotFoundObjectResult AccountNotFound() =>
        NotFound(new { error = "account_not_found", message = "That connected account could not be found." });
}
