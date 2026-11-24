namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One ReelBolt user's connected GitHub account, in one organization (<c>github_account_links</c>).
///
/// <para>
/// <b>Why this exists at all.</b> The rest of the GitHub integration authenticates as the <i>App</i>:
/// an app JWT signs a request for an installation access token, and that token can read exactly the
/// repositories the App was installed on. That is the right credential for pulling a linked
/// repository, and it is useless for answering "which repositories may this person pick from?" — an
/// installation token has no notion of a person. Listing somebody's repositories needs a
/// <b>user-to-server</b> token, which comes from the GitHub App's OAuth flow and represents the person,
/// not the installation.
/// </para>
///
/// <para>
/// <b>What the token can reach.</b> A GitHub App user access token is the <i>intersection</i> of the
/// app's own permissions and the user's own access — and, because this App asks for exactly
/// <c>Contents: read-only</c> and <c>Metadata: read-only</c> and nothing else, what the token can reach
/// is precisely "read the file contents and metadata of repositories that both the app and the person
/// can already see". It cannot write, cannot read issues, cannot read organization or enterprise
/// settings, and cannot reach a repository the app was not installed on. That intersection is also why
/// the picker can never show a repository the person cannot access: GitHub answers the list.
/// </para>
///
/// <para>
/// <b>Scoping.</b> A row is keyed on <c>(UserId, OrganizationId)</c>, never on the user alone. The
/// token can read the person's private repositories, so which workspace may spend it is a decision
/// rather than an implementation detail: a connection made inside one workspace is invisible to
/// another, and a colleague in the same team cannot borrow it. Connecting the same GitHub account
/// twice, once per workspace, is the intended shape.
/// </para>
///
/// <para>
/// <b>Tenancy.</b> This table carries no EF tenant query filter, for the same reviewed reason
/// <see cref="ProjectGitHubLink"/> does not: the row is never reached by an id the caller chose, it is
/// always looked up by <c>(caller's user id, caller's active organization)</c> from the session's own
/// claims, which is a stricter guard than a filter would be. It deliberately holds no project link, so
/// revoking it can never delete a project's files.
/// </para>
/// </summary>
public class GitHubAccountLink
{
    public Guid Id { get; set; }

    /// <summary>The ReelBolt user who connected the account.</summary>
    public Guid UserId { get; set; }

    /// <summary>The workspace the connection was made in. Part of the key, not decoration.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// GitHub's numeric account id. This is the identity the <c>github_app_authorization</c> webhook
    /// names when the person revokes ReelBolt in their GitHub settings, so revocation works even if
    /// they changed their login.
    /// </summary>
    public long GitHubUserId { get; set; }

    /// <summary>The GitHub login as it was when connected. Display only; never matched on.</summary>
    public string Login { get; set; } = string.Empty;

    /// <summary>Avatar URL GitHub reported. Display only.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// The user access token, <b>encrypted at rest</b> with <c>ISecretProtector</c> (ASP.NET Core Data
    /// Protection, keyed on the shared <c>dpkeys</c> ring). It is never returned by any endpoint, never
    /// logged, and never placed on a DTO — the column name says so, because a plaintext token here
    /// would be a credential that reads the customer's private repositories.
    /// </summary>
    public string ProtectedAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// The refresh token, encrypted the same way, when GitHub issued one. Null means the token does not
    /// expire, which is the case for an app that has not opted in to user-to-server token expiration —
    /// not an error.
    /// </summary>
    public string? ProtectedRefreshToken { get; set; }

    /// <summary>When the access token stops working. Null means it does not expire.</summary>
    public DateTime? AccessTokenExpiresAt { get; set; }

    /// <summary>When the refresh token stops working. Null when there is no refresh token.</summary>
    public DateTime? RefreshTokenExpiresAt { get; set; }

    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the connection stopped being usable, and why. A revoked row is kept rather than deleted so
    /// that "you have never connected GitHub" and "your GitHub authorization was revoked" are different
    /// sentences on screen; the plaintext is overwritten when this is written, so revocation really does
    /// destroy the credential.
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary><c>user_revoked</c>, <c>disconnected</c>, <c>invalid_grant</c> or <c>decryption_failed</c>.</summary>
    public string? RevokedReason { get; set; }

    /// <summary>True when the row still holds a usable connection.</summary>
    public bool IsActive => RevokedAt is null;
}

/// <summary>
/// One in-flight GitHub OAuth authorization (<c>github_oauth_states</c>) — the CSRF defence for the
/// account-connection flow, and a single-use ticket rather than a signed cookie.
///
/// <para>
/// <b>Why a row and not a signed value.</b> A signed <c>state</c> would prove this deployment minted
/// it, but it could be replayed by anyone who saw it; a row can be consumed exactly once. Both halves
/// of the check are stored as SHA-256 hashes, so a leaked database snapshot cannot be turned back into
/// a working <c>?state=…</c> URL.
/// </para>
///
/// <para>
/// <b>Two independent bindings, both required.</b> <see cref="StateHash"/> is the value GitHub echoes
/// back in the query string; <see cref="BindingHash"/> is a second random value that is only ever sent
/// as an <c>HttpOnly</c> cookie to the browser that started the flow. An attacker who learns the state
/// (a shared screen, a referrer leak) still cannot complete the flow without also holding the cookie,
/// which is why the callback demands both.
/// </para>
/// </summary>
public class GitHubOAuthState
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 (hex) of the <c>state</c> value handed to GitHub.</summary>
    public string StateHash { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the value in the browser-binding cookie.</summary>
    public string BindingHash { get; set; } = string.Empty;

    /// <summary>
    /// The PKCE <c>code_verifier</c> for this authorization, encrypted at rest. Single-use and
    /// short-lived by construction, but it is half of what redeems an authorization code, so it is
    /// stored the way every other credential here is.
    /// </summary>
    public string ProtectedCodeVerifier { get; set; } = string.Empty;

    /// <summary>Who started the flow. The callback binds the token to this user, never to a claim in the callback.</summary>
    public Guid UserId { get; set; }

    /// <summary>The workspace the flow was started in. The token is stored against this pair.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The project to send the browser back to, when the flow was started from a project page.</summary>
    public Guid? ProjectId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Ten minutes. Long enough to sign in to GitHub, short enough to be worthless afterwards.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set the instant the flow is completed. Non-null means this ticket is spent.</summary>
    public DateTime? ConsumedAt { get; set; }
}
