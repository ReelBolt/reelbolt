using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The workspace's GitHub account: whether one is connected, connecting it, and disconnecting it
/// (docs/github-app.md, "Connecting a GitHub account").
///
/// <para>
/// <b>Why this exists next to the App.</b> Everything else in this integration authenticates as the App
/// and can therefore only see repositories the App was installed on. Choosing <i>which</i> repository to
/// link needs the person's own view of GitHub, which only a user-to-server token provides.
/// </para>
///
/// <para>
/// <b>What a caller can influence.</b> Three things, and none of them decide whose account is
/// connected: the <c>projectId</c> to return the browser to, the <c>state</c> GitHub echoed, and a
/// cookie this deployment set. The person and the workspace come from the session's own claims on the
/// way in, are written into a single-use ticket, and are read back out of that ticket on the way back —
/// so the callback cannot be aimed at somebody else even by someone holding a valid code.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/github/account")]
[Authorize]
public class GitHubAccountController : ControllerBase
{
    private readonly IGitHubAccountService _accounts;
    private readonly IGitHubUserOAuthService _oauth;
    private readonly ICurrentUser _currentUser;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubAccountController> _logger;

    public GitHubAccountController(
        IGitHubAccountService accounts,
        IGitHubUserOAuthService oauth,
        ICurrentUser currentUser,
        IOptions<GitHubOptions> options,
        ILogger<GitHubAccountController> logger)
    {
        _accounts = accounts;
        _oauth = oauth;
        _currentUser = currentUser;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The connection's state. Never carries a token — see <see cref="GitHubAccountResponse"/>.</summary>
    [HttpGet]
    public async Task<ActionResult<GitHubAccountResponse>> Get(CancellationToken ct)
    {
        if (_currentUser.OrganizationId is not { } organizationId)
            return NotFound(new { error = "no_workspace" });

        GitHubAccountView view = await _accounts.GetViewAsync(_currentUser.UserId, organizationId, ct);
        return Ok(ToResponse(view));
    }

    /// <summary>
    /// Begins the OAuth flow and answers with the URL to send the browser to. It is a POST that returns
    /// JSON rather than a redirect, because the dashboard calls it with <c>fetch</c> and a cross-site
    /// redirect cannot be followed usefully from an XHR.
    /// </summary>
    [HttpPost("connect")]
    public async Task<ActionResult<StartGitHubConnectResponse>> Connect(
        [FromBody] StartGitHubConnectRequest? request,
        CancellationToken ct)
    {
        if (_currentUser.OrganizationId is not { } organizationId)
            return NotFound(new { error = "no_workspace" });

        GitHubOAuthStart? start = await _oauth.StartAsync(_currentUser.UserId, organizationId, request?.ProjectId, ct);
        if (start is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "github_oauth_not_configured" });

        Response.Cookies.Append(
            GitHubUserOAuthService.BindingCookieName,
            start.Binding,
            GitHubUserOAuthService.BindingCookieOptions(CookieIsSecure(), maxAge: TimeSpan.FromMinutes(20)));

        return Ok(new StartGitHubConnectResponse(start.AuthorizeUrl));
    }

    /// <summary>
    /// Forgets the connection and destroys the stored credential. The projects linked through it keep
    /// their files, and a repository that was already linked keeps receiving pushes — those use the
    /// App's installation token, which this never touched.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> Disconnect(CancellationToken ct)
    {
        if (_currentUser.OrganizationId is not { } organizationId)
            return NotFound(new { error = "no_workspace" });

        bool removed = await _accounts.DisconnectAsync(_currentUser.UserId, organizationId, ct);
        ClearBindingCookie();

        return removed ? NoContent() : NotFound(new { error = "not_connected" });
    }

    private bool CookieIsSecure() =>
        _options.OAuthCallbackUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ?? true;

    private void ClearBindingCookie() =>
        Response.Cookies.Delete(GitHubUserOAuthService.BindingCookieName, GitHubUserOAuthService.BindingCookieOptions(CookieIsSecure()));

    private static GitHubAccountResponse ToResponse(GitHubAccountView view) => new(
        view.Connected,
        view.Login,
        view.AvatarUrl,
        view.CanConnect,
        view.RevokedReason,
        view.ConnectedAt,
        view.InstallUrl);
}
