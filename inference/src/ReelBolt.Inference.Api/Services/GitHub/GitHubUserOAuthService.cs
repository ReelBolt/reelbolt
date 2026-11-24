using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// What a started authorization hands back to the controller: the URL to send the browser to, the
/// <c>state</c> that GitHub will echo (already embedded in the URL, returned for logging-free
/// assertions), and the value the browser-binding cookie must carry.
/// </summary>
public sealed record GitHubOAuthStart(string AuthorizeUrl, string State, string Binding);

/// <summary>How an authorization finished. <see cref="Code"/> is a short machine token, never prose.</summary>
public enum GitHubOAuthOutcome
{
    Connected,
    NotConfigured,
    Denied,
    InvalidState,
    ExpiredState,
    StateMismatch,
    ExchangeFailed,
    IdentityUnavailable
}

/// <summary>
/// The result of a callback: what happened, and which project to send the browser back to.
/// <see cref="ProjectId"/> is echoed from our own state row, never from the request.
/// </summary>
public sealed record GitHubOAuthCompletion(GitHubOAuthOutcome Outcome, Guid? ProjectId, string? Login)
{
    public bool Succeeded => Outcome == GitHubOAuthOutcome.Connected;

    /// <summary>The short token the dashboard reads out of the query string to pick a sentence.</summary>
    public string Code => Outcome switch
    {
        GitHubOAuthOutcome.Connected => "connected",
        GitHubOAuthOutcome.NotConfigured => "not_configured",
        GitHubOAuthOutcome.Denied => "denied",
        GitHubOAuthOutcome.ExpiredState => "expired",
        GitHubOAuthOutcome.StateMismatch => "state_mismatch",
        GitHubOAuthOutcome.InvalidState => "invalid_state",
        GitHubOAuthOutcome.IdentityUnavailable => "identity_unavailable",
        _ => "exchange_failed"
    };
}

/// <summary>
/// Connects a person's GitHub account to a workspace, through the GitHub App's user-to-server OAuth
/// flow (docs/github-app.md, "Connecting a GitHub account").
///
/// <para>
/// <b>Why the App's own OAuth flow and not an OAuth App.</b> The rest of this integration authenticates
/// as the App; a user access token minted by that same App is the intersection of the App's permissions
/// and the person's own access, which is exactly the set of repositories ReelBolt may pull. A separate
/// OAuth App would need its own registration and its own, wider scopes, and would be a second credential
/// to reason about. See the class doc on <see cref="GitHubAccountLink"/>.
/// </para>
///
/// <para>
/// <b>The three defences, in order.</b> A <c>state</c> this deployment minted, stored as a hash and
/// spendable exactly once; a second random value that only ever travels as an <c>HttpOnly</c> cookie, so
/// possessing the state is not enough; and PKCE, so an intercepted authorization code is worthless
/// without the verifier that never left our database. The callback performs all three before it exchanges
/// anything, and it identifies the person from OUR state row — never from the callback's query string.
/// That last point is what makes the flow safe without a session: nothing a caller can put in the URL
/// decides whose account is connected.
/// </para>
/// </summary>
public interface IGitHubUserOAuthService
{
    /// <summary>False when the app has no client id, secret or callback URL.</summary>
    bool IsConfigured { get; }

    /// <summary>Begins an authorization for one person in one workspace. Null when not configured.</summary>
    Task<GitHubOAuthStart?> StartAsync(Guid userId, Guid organizationId, Guid? projectId, CancellationToken ct);

    /// <summary>
    /// Finishes an authorization. <paramref name="bindingCookie"/> must be the cookie the start response
    /// set, and <paramref name="state"/> the value GitHub echoed back.
    /// </summary>
    Task<GitHubOAuthCompletion> CompleteAsync(string? state, string? bindingCookie, string? code, string? error, CancellationToken ct);
}

/// <inheritdoc />
public sealed class GitHubUserOAuthService : IGitHubUserOAuthService
{
    /// <summary>
    /// The cookie that carries the browser binding: set when a flow starts, checked and cleared on the
    /// callback. Named once, here, because a set and a clear that disagree on the name silently produce
    /// a flow that can never complete.
    /// </summary>
    public const string BindingCookieName = "reelbolt_github_oauth";

    /// <summary>
    /// The options every response that touches the binding cookie must agree on, so a set and a clear
    /// always apply to the same cookie. It lives beside the name for that reason.
    ///
    /// <para>
    /// <c>Path</c> is the narrow <c>/api/v1/github</c> subtree rather than <c>/</c>, so the value is
    /// never sent anywhere else. <c>Secure</c> is the argument, and callers pass it from the scheme of
    /// the configured callback URL: the cookie has to survive the round trip through GitHub and back
    /// to exactly that URL, so that URL's scheme is the fact that decides it — and deriving it keeps
    /// local HTTP development working.
    /// </para>
    /// </summary>
    public static CookieOptions BindingCookieOptions(bool secure, TimeSpan? maxAge = null) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/api/v1/github",
        MaxAge = maxAge,
        Secure = secure
    };

    /// <summary>
    /// Only value a GitHub App may ask for. It does not grant permissions — a GitHub App's permissions
    /// come from its registration — it asks GitHub to issue a token that expires and comes with a
    /// refresh token, so a leaked token is worth eight hours rather than forever.
    /// </summary>
    internal const string OfflineAccessScope = "offline_access";

    private readonly InferenceApiDbContext _db;
    private readonly IGitHubAppClient _gitHub;
    private readonly GitHubOptions _options;
    private readonly ISecretProtector _secrets;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubUserOAuthService> _logger;

    public GitHubUserOAuthService(
        InferenceApiDbContext db,
        IGitHubAppClient gitHub,
        IOptions<GitHubOptions> options,
        ISecretProtector secrets,
        ILogger<GitHubUserOAuthService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _gitHub = gitHub;
        _options = options.Value;
        _secrets = secrets;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsConfigured => _options.HasUserOAuthCredentials;

    public async Task<GitHubOAuthStart?> StartAsync(Guid userId, Guid organizationId, Guid? projectId, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        await DiscardSpentStatesAsync(userId, now, ct);

        byte[] stateBytes = RandomNumberGenerator.GetBytes(32);
        byte[] bindingBytes = RandomNumberGenerator.GetBytes(32);
        byte[] verifierBytes = RandomNumberGenerator.GetBytes(32);

        string state = Base64Url(stateBytes);
        string binding = Base64Url(bindingBytes);
        string verifier = Base64Url(verifierBytes);

        _db.GitHubOAuthStates.Add(new GitHubOAuthState
        {
            Id = Guid.NewGuid(),
            StateHash = Hash(state),
            BindingHash = Hash(binding),
            ProtectedCodeVerifier = _secrets.Protect(verifier),
            UserId = userId,
            OrganizationId = organizationId,
            ProjectId = projectId,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(Math.Max(60, _options.OAuthStateTtlSeconds))
        });

        await _db.SaveChangesAsync(ct);

        string authorizeUrl = BuildAuthorizeUrl(state, Challenge(verifier));
        return new GitHubOAuthStart(authorizeUrl, state, binding);
    }

    public async Task<GitHubOAuthCompletion> CompleteAsync(
        string? state,
        string? bindingCookie,
        string? code,
        string? error,
        CancellationToken ct)
    {
        if (!IsConfigured) return new GitHubOAuthCompletion(GitHubOAuthOutcome.NotConfigured, null, null);

        // The person pressed "Cancel" on GitHub's consent screen. Not an attack and not a fault.
        if (!string.IsNullOrWhiteSpace(error))
            return new GitHubOAuthCompletion(GitHubOAuthOutcome.Denied, null, null);

        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(bindingCookie) || string.IsNullOrWhiteSpace(code))
            return new GitHubOAuthCompletion(GitHubOAuthOutcome.InvalidState, null, null);

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        string stateHash = Hash(state);

        GitHubOAuthState? ticket = await _db.GitHubOAuthStates
            .FirstOrDefaultAsync(s => s.StateHash == stateHash && s.ConsumedAt == null, ct);

        if (ticket is null)
        {
            // Unknown, already spent, or a state this deployment never minted. There is nothing to
            // consume here, which is the point: a replayed state finds no row.
            _logger.LogWarning("Refused a GitHub OAuth callback carrying a state that is unknown or already spent");
            return new GitHubOAuthCompletion(GitHubOAuthOutcome.InvalidState, null, null);
        }

        if (ticket.ExpiresAt <= now)
        {
            ticket.ConsumedAt = now;
            await _db.SaveChangesAsync(ct);
            return new GitHubOAuthCompletion(GitHubOAuthOutcome.ExpiredState, ticket.ProjectId, null);
        }

        // The browser binding. A mismatch deliberately does NOT consume the ticket: an attacker who
        // knows the state could otherwise burn the legitimate browser's authorization by sending one
        // request with a wrong cookie, which turns a security check into a denial of service. Guessing
        // the cookie is a 256-bit problem, so leaving the ticket alive costs nothing.
        if (!FixedTimeEquals(ticket.BindingHash, Hash(bindingCookie)))
        {
            _logger.LogWarning("Refused a GitHub OAuth callback whose browser-binding cookie did not match its state");
            return new GitHubOAuthCompletion(GitHubOAuthOutcome.StateMismatch, ticket.ProjectId, null);
        }

        // Marked spent before the exchange, so an ordinary retry a moment later finds no live ticket.
        //
        // This read-then-write is a check-then-act, and two simultaneous callbacks carrying the same
        // state could both observe ConsumedAt == null. The step that makes that harmless is GitHub's
        // own: an authorization code is single-use, so at most one of the two exchanges succeeds and
        // the other comes back as an ExchangeFailed that writes nothing. Closing the window in SQL
        // would mean a conditional UPDATE, which the in-memory provider the tests run on does not
        // support at all — a worse trade than a race whose worst case is one extra 302 to the
        // "could not finish" sentence.
        ticket.ConsumedAt = now;

        if (!_secrets.TryUnprotect(ticket.ProtectedCodeVerifier, out string verifier) || string.IsNullOrWhiteSpace(verifier))
        {
            // Fail closed rather than exchanging without the PKCE verifier: a missing verifier means the
            // key ring cannot read what we wrote, and completing the flow anyway would silently drop a
            // defence instead of reporting a broken deployment.
            await _db.SaveChangesAsync(ct);
            _logger.LogError("Could not decrypt a GitHub OAuth code verifier; the Data Protection key ring may have changed");
            return new GitHubOAuthCompletion(GitHubOAuthOutcome.ExchangeFailed, ticket.ProjectId, null);
        }

        GitHubUserTokens tokens;
        try
        {
            tokens = await _gitHub.ExchangeUserCodeAsync(code, verifier, ct);
        }
        catch (GitHubApiException ex)
        {
            await _db.SaveChangesAsync(ct);
            // ex.Message names GitHub's own error code, never the code, the verifier or a token.
            _logger.LogWarning("GitHub refused an authorization-code exchange ({Reason})", ex.Message);
            return new GitHubOAuthCompletion(GitHubOAuthOutcome.ExchangeFailed, ticket.ProjectId, null);
        }

        GitHubUserIdentity? identity = await _gitHub.GetUserAsync(tokens.AccessToken, ct);
        if (identity is null)
        {
            await _db.SaveChangesAsync(ct);
            return new GitHubOAuthCompletion(GitHubOAuthOutcome.IdentityUnavailable, ticket.ProjectId, null);
        }

        await StoreAsync(ticket.UserId, ticket.OrganizationId, identity, tokens, ct);

        _logger.LogInformation(
            "Connected the GitHub account {Login} for user {UserId} in organization {OrganizationId}",
            identity.Login, ticket.UserId, ticket.OrganizationId);

        return new GitHubOAuthCompletion(GitHubOAuthOutcome.Connected, ticket.ProjectId, identity.Login);
    }

    /// <summary>
    /// Writes the connection, replacing any previous one for the same (person, workspace). The token is
    /// encrypted here and nowhere else, and the only representation that ever reaches a caller is
    /// <see cref="GitHubUserIdentity"/> — the ciphertext never leaves this method's callers.
    /// </summary>
    private async Task StoreAsync(Guid userId, Guid organizationId, GitHubUserIdentity identity, GitHubUserTokens tokens, CancellationToken ct)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        GitHubAccountLink? existing = await _db.GitHubAccountLinks
            .FirstOrDefaultAsync(l => l.UserId == userId && l.OrganizationId == organizationId, ct);

        if (existing is null)
        {
            existing = new GitHubAccountLink { Id = Guid.NewGuid(), UserId = userId, OrganizationId = organizationId };
            _db.GitHubAccountLinks.Add(existing);
        }

        existing.GitHubUserId = identity.Id;
        existing.Login = identity.Login;
        existing.AvatarUrl = identity.AvatarUrl;
        existing.ProtectedAccessToken = _secrets.Protect(tokens.AccessToken);
        existing.ProtectedRefreshToken = tokens.RefreshToken is { Length: > 0 } refresh ? _secrets.Protect(refresh) : null;
        existing.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        existing.RefreshTokenExpiresAt = tokens.RefreshTokenExpiresAt;
        existing.UpdatedAt = now;

        // Reconnecting clears a previous revocation. Leaving RevokedAt set would make the account look
        // unusable the moment it became usable again.
        if (existing.RevokedAt is not null)
        {
            existing.RevokedAt = null;
            existing.RevokedReason = null;
        }
        else if (existing.ConnectedAt == default)
        {
            existing.ConnectedAt = now;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two browser tabs finished the same authorization at once and the unique index caught the
            // second insert. The row that won is as valid as this one; adopt it rather than failing a
            // connection the person just made.
            _db.Entry(existing).State = EntityState.Detached;
            GitHubAccountLink? winner = await _db.GitHubAccountLinks
                .FirstOrDefaultAsync(l => l.UserId == userId && l.OrganizationId == organizationId, ct);
            if (winner is null) throw;

            winner.GitHubUserId = identity.Id;
            winner.Login = identity.Login;
            winner.AvatarUrl = identity.AvatarUrl;
            winner.ProtectedAccessToken = _secrets.Protect(tokens.AccessToken);
            winner.ProtectedRefreshToken = tokens.RefreshToken is { Length: > 0 } wonRefresh ? _secrets.Protect(wonRefresh) : null;
            winner.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
            winner.RefreshTokenExpiresAt = tokens.RefreshTokenExpiresAt;
            winner.UpdatedAt = now;
            winner.RevokedAt = null;
            winner.RevokedReason = null;
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Drops this person's spent and expired tickets. Bounded to one user and done on start rather than
    /// on a timer, so a deployment with no traffic accumulates nothing.
    /// </summary>
    private async Task DiscardSpentStatesAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        List<GitHubOAuthState> stale = await _db.GitHubOAuthStates
            .Where(s => s.UserId == userId && (s.ConsumedAt != null || s.ExpiresAt <= now))
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        _db.GitHubOAuthStates.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
    }

    private string BuildAuthorizeUrl(string state, string challenge)
    {
        List<string> query =
        [
            $"client_id={Uri.EscapeDataString(_options.ClientId!)}",
            $"redirect_uri={Uri.EscapeDataString(_options.OAuthCallbackUrl!)}",
            $"state={Uri.EscapeDataString(state)}",
            $"code_challenge={Uri.EscapeDataString(challenge)}",
            "code_challenge_method=S256",
            $"scope={Uri.EscapeDataString(OfflineAccessScope)}"
        ];

        string separator = _options.OAuthAuthorizeUrl.Contains('?') ? "&" : "?";
        return _options.OAuthAuthorizeUrl + separator + string.Join('&', query);
    }

    /// <summary>Base64url of SHA-256, unpadded — what RFC 7636 §4.2 requires for <c>S256</c>.</summary>
    internal static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedTimeEquals(string left, string right)
    {
        byte[] a = Encoding.ASCII.GetBytes(left);
        byte[] b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
