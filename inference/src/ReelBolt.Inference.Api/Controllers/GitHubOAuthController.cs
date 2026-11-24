using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.GitHub;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Where GitHub sends the browser back after a person authorizes ReelBolt (docs/github-app.md,
/// "Connecting a GitHub account").
///
/// <para>
/// <b>Anonymous, and safe to be.</b> This endpoint is reached by a top-level browser navigation from
/// github.com, so it has no bearer token and no session of its own. It does not need one: the person it
/// connects is read out of a single-use ticket this deployment minted, the request is authenticated by
/// the PKCE verifier and the browser-binding cookie, and the authorization code is redeemed
/// server-to-server with the app's client secret. A caller who can reach this URL with a valid
/// <c>state</c> and cookie is the browser that started the flow; one who cannot is refused before
/// anything is exchanged.
/// </para>
///
/// <para>
/// <b>Every answer is a redirect to a fixed, relative path.</b> Nothing from the request becomes part of
/// the <c>Location</c> header — the project id comes from our own ticket and the outcome from an enum —
/// so this endpoint cannot be turned into an open redirect. The outcome travels as a short machine token
/// the dashboard turns into a sentence; GitHub's own error prose is never reflected to the browser.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/github/oauth")]
public class GitHubOAuthController : ControllerBase
{
    private readonly IGitHubUserOAuthService _oauth;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubOAuthController> _logger;

    public GitHubOAuthController(
        IGitHubUserOAuthService oauth,
        IOptions<GitHubOptions> options,
        ILogger<GitHubOAuthController> logger)
    {
        _oauth = oauth;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        string? binding = Request.Cookies[GitHubUserOAuthService.BindingCookieName];

        GitHubOAuthCompletion completion;
        try
        {
            completion = await _oauth.CompleteAsync(state, binding, code, error, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unexpected failure still has to land the person back on a page that can explain
            // itself, rather than on a raw 500 from an API route they never meant to visit.
            _logger.LogError(ex, "The GitHub account-connection callback failed unexpectedly");
            completion = new GitHubOAuthCompletion(GitHubOAuthOutcome.ExchangeFailed, null, null);
        }

        // The binding has served its purpose the instant it is checked, and it is single-flow by
        // design: leaving it behind would let a later callback reuse it.
        Response.Cookies.Delete(
            GitHubUserOAuthService.BindingCookieName,
            GitHubUserOAuthService.BindingCookieOptions(CookieIsSecure()));

        string path = completion.ProjectId is { } projectId
            ? $"/app/projects/{projectId}?tab=repository&github={Uri.EscapeDataString(completion.Code)}"
            : $"/app/settings?github={Uri.EscapeDataString(completion.Code)}";

        if (!completion.Succeeded)
            _logger.LogInformation("A GitHub account connection finished as {Outcome}", completion.Code);

        return Redirect(path);
    }

    private bool CookieIsSecure() =>
        _options.OAuthCallbackUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ?? true;
}
