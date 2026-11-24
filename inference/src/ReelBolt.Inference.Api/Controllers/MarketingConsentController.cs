using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>PUT <c>consent</c>: <c>source</c> is <c>settings</c>, <c>checkout</c> or <c>acceptTerms</c>.</summary>
public sealed record MarketingConsentRequest(bool OptIn, string? Source, string? WordingVersion);

/// <summary>POST <c>subscribe</c>: <c>source</c> is <c>siteContact</c> or <c>siteFooter</c>.</summary>
public sealed record MarketingSubscribeRequest(string? Email, string? WordingVersion, string? Source, string? Locale = null, string? FirstName = null);

/// <summary>POST <c>confirm</c> and the JSON form of POST <c>unsubscribe</c>.</summary>
public sealed record MarketingTokenRequest(string? Token);

/// <summary>GET <c>wording</c>.</summary>
public sealed record MarketingWordingResponse(string Version, string Text);

/// <summary>The one-field status answer of subscribe, confirm and unsubscribe.</summary>
public sealed record MarketingStatusResponse(string Status);

/// <summary>
/// Marketing consent (saas-promotions; not billing-gated). Consent is per individual email, never
/// per org: the signed-in endpoints act on <c>currentUser.Email</c>, and answer <c>403
/// EMAIL_NOT_VERIFIED</c> until that address is verified.
///
/// <para><b>Anonymous endpoints.</b> <c>wording</c>, <c>subscribe</c>, <c>confirm</c> and
/// <c>unsubscribe</c> carry no session. <c>subscribe</c> answers <c>202 {status:"checkEmail"}</c>
/// identically for a new, pending, opted-in or withdrawn address, and the mail is queued off the
/// request path, so neither the body nor the relay round trip says whether an address is on the
/// list. <c>unsubscribe</c> is the RFC 8058 one-click target: it takes JSON <c>{token}</c>, or
/// <c>?token=</c> with an <c>application/x-www-form-urlencoded</c> or <c>multipart/form-data</c> body
/// (<c>List-Unsubscribe=One-Click</c>); it sets no cookie, needs no CSRF token and never redirects.
/// GET never unsubscribes.</para>
/// </summary>
[ApiController]
[Route("api/v1/marketing")]
[Authorize]
public class MarketingConsentController(
    MarketingConsentService consents,
    MarketingWording wording,
    ICurrentUser currentUser) : ControllerBase
{
    private static readonly MarketingStatusResponse CheckEmail = new("checkEmail");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly MarketingConsentSource[] SignedInSources =
        [MarketingConsentSource.Settings, MarketingConsentSource.Checkout, MarketingConsentSource.AcceptTerms];

    private static readonly MarketingConsentSource[] AnonymousSources =
        [MarketingConsentSource.SiteContact, MarketingConsentSource.SiteFooter];

    [HttpGet("wording")]
    [AllowAnonymous]
    public ActionResult<MarketingWordingResponse> GetWording() =>
        Ok(new MarketingWordingResponse(wording.Current, wording.CurrentText));

    /// <summary>
    /// Consent is keyed by email, so an account reaches the row under its address only once it has
    /// proven it owns that address: an unverified account could otherwise read or withdraw the
    /// consent of whoever subscribed with it (S-02). Read from the database, not the JWT's
    /// <c>emailVerified</c> claim, which can be stale and counts as verified when missing (N-08).
    /// </summary>
    private async Task<ObjectResult?> UnverifiedEmailAsync(CancellationToken ct) =>
        await consents.IsEmailVerifiedAsync(currentUser.UserId, ct)
            ? null
            : new ObjectResult(new
            {
                code = "EMAIL_NOT_VERIFIED",
                message = "Verify your email address before managing marketing preferences. Check your inbox for the verification link.",
            })
            { StatusCode = StatusCodes.Status403Forbidden };

    [HttpGet("consent")]
    public async Task<IActionResult> GetConsent(CancellationToken ct) =>
        await UnverifiedEmailAsync(ct) ?? (IActionResult)Ok(await consents.GetAsync(currentUser.Email, ct));

    [HttpPut("consent")]
    public async Task<IActionResult> PutConsent([FromBody] MarketingConsentRequest request, CancellationToken ct)
    {
        if (await UnverifiedEmailAsync(ct) is { } unverified)
        {
            return unverified;
        }

        if (MarketingConsentService.ParseSource(request.Source) is not { } source || !SignedInSources.Contains(source))
        {
            return BadRequest(new { error = "invalid_source", message = "source must be settings, checkout or acceptTerms." });
        }

        if (string.IsNullOrWhiteSpace(currentUser.Email))
        {
            return BadRequest(new { error = "no_email" });
        }

        MarketingConsentUpdate result = await consents.SetAsync(
            currentUser.UserId, currentUser.Email, request.OptIn, source, request.WordingVersion,
            consents.EvidenceFrom(HttpContext), ct);
        return result.StaleWording
            ? Conflict(new { error = "stale_wording", currentWordingVersion = result.CurrentWordingVersion })
            : Ok(result.View);
    }

    [HttpPost("subscribe")]
    [AllowAnonymous]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Subscribe([FromBody] MarketingSubscribeRequest request, CancellationToken ct)
    {
        if (MarketingConsentService.ParseSource(request.Source) is not { } source || !AnonymousSources.Contains(source))
        {
            return BadRequest(new { error = "invalid_source", message = "source must be siteContact or siteFooter." });
        }

        MarketingSubscribeOutcome outcome = await consents.SubscribeAsync(
            request.Email, request.WordingVersion, source, consents.EvidenceFrom(HttpContext), ct, request.Locale, request.FirstName);
        return outcome switch
        {
            MarketingSubscribeOutcome.InvalidEmail => BadRequest(new { error = "invalid_email" }),
            MarketingSubscribeOutcome.StaleWording => Conflict(new { error = "stale_wording", currentWordingVersion = wording.Current }),
            _ => StatusCode(StatusCodes.Status202Accepted, CheckEmail),
        };
    }

    [HttpPost("confirm")]
    [AllowAnonymous]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Confirm([FromBody] MarketingTokenRequest request, CancellationToken ct)
    {
        MarketingTokenStatus status = await consents.ConfirmAsync(request.Token, consents.EvidenceFrom(HttpContext), ct);
        return status switch
        {
            MarketingTokenStatus.Valid => Ok(new MarketingStatusResponse("optedIn")),
            MarketingTokenStatus.Expired => StatusCode(StatusCodes.Status410Gone, new { error = "token_expired" }),
            _ => BadRequest(new { error = "invalid_token" }),
        };
    }

    /// <summary>
    /// The body is read by hand rather than bound, so no content type can turn a one-click POST into
    /// a <c>415</c>: a form body (urlencoded or multipart) takes the token from <c>?token=</c> (or a
    /// <c>token</c> field), a JSON body from <c>{token}</c>, anything else from <c>?token=</c>.
    /// </summary>
    [HttpPost("unsubscribe")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Unsubscribe(CancellationToken ct)
    {
        string? token = Request.Query["token"].ToString();
        if (Request.HasFormContentType)
        {
            IFormCollection form = await Request.ReadFormAsync(ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                token = form["token"].ToString();
            }
        }
        else if (Request.HasJsonContentType())
        {
            try
            {
                MarketingTokenRequest? body = await JsonSerializer.DeserializeAsync<MarketingTokenRequest>(Request.Body, Json, ct);
                if (!string.IsNullOrWhiteSpace(body?.Token))
                {
                    token = body.Token;
                }
            }
            catch (JsonException)
            {
                // A malformed body is an invalid token, answered below.
            }
        }

        bool ok = await consents.UnsubscribeAsync(
            string.IsNullOrWhiteSpace(token) ? null : token.Trim(), consents.EvidenceFrom(HttpContext), ct);
        return ok ? Ok(new MarketingStatusResponse("unsubscribed")) : BadRequest(new { error = "invalid_token" });
    }
}
