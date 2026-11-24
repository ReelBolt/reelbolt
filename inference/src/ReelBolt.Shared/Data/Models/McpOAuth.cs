namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One OAuth 2.1 client registered against the ReelBolt authorization server
/// (<c>oauth_clients</c>, docs/design/mcp-oauth.md §5).
/// </summary>
/// <remarks>
/// <para>
/// The Inference API owns this table (it owns the schema and runs the EF Core migrations); the Go API
/// is the authorization server and reads/writes it by GORM, exactly as it already does for
/// <c>organizations</c>. Column names and types are therefore an <b>interface</b>: do not rename or
/// retype them without changing both sides.
/// </para>
/// <para>
/// A public client (<c>token_endpoint_auth_method = "none"</c>, PKCE only) has a null
/// <see cref="ClientSecretHash"/>; a confidential client stores only a hash of its secret, never the
/// secret itself. <see cref="RedirectUris"/> is matched <b>exactly</b> by the authorization server —
/// never by prefix or wildcard.
/// </para>
/// </remarks>
public class OAuthClient
{
    public Guid Id { get; set; }

    /// <summary>The public client identifier (not a secret). Unique.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Hash of the client secret; null for a public (PKCE-only) client. Never the secret.</summary>
    public string? ClientSecretHash { get; set; }

    /// <summary>Display name shown on the consent screen.</summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>The registered redirect URIs (jsonb array), matched exactly.</summary>
    public List<string> RedirectUris { get; set; } = new();

    /// <summary>Grant types the client may use (jsonb array), e.g. authorization_code, refresh_token.</summary>
    public List<string> GrantTypes { get; set; } = new();

    /// <summary>Response types the client may use (jsonb array), e.g. code.</summary>
    public List<string> ResponseTypes { get; set; } = new();

    /// <summary><c>none</c> for a public client, <c>client_secret_post</c> for a confidential one.</summary>
    public string TokenEndpointAuthMethod { get; set; } = "none";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last successful client authentication / token request; null until first use.</summary>
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>
/// One issued, single-use OAuth authorization code (<c>oauth_authorization_codes</c>,
/// docs/design/mcp-oauth.md §5).
/// </summary>
/// <remarks>
/// <b>The code is stored hashed</b> — <see cref="CodeHash"/> is a hex SHA-256 of the value sent to the
/// client, and the plaintext never touches the database. A row is consumed by setting
/// <see cref="ConsumedAt"/>, never by being read twice: a lookup is by hash, and a replay finds an
/// already-consumed row and is refused. The PKCE challenge is stored unhashed because it is not a
/// secret (the verifier is).
/// </remarks>
public class OAuthAuthorizationCode
{
    public Guid Id { get; set; }

    /// <summary>Hex SHA-256 of the issued code. Unique; the lookup key.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>The client the code was issued to (a plain column, not an FK — the Go API writes both sides).</summary>
    public string ClientId { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    /// <summary>The workspace the consent was granted in.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The redirect URI the code is bound to; the token request must repeat it exactly.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>The PKCE <c>S256</c> challenge the token exchange must satisfy.</summary>
    public string CodeChallenge { get; set; } = string.Empty;

    /// <summary>Space-separated granted scopes.</summary>
    public string Scope { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Set the moment the code is exchanged. Single use.</summary>
    public DateTime? ConsumedAt { get; set; }
}

/// <summary>
/// One issued OAuth refresh token (<c>oauth_refresh_tokens</c>, docs/design/mcp-oauth.md §5).
/// </summary>
/// <remarks>
/// <b>The token is stored hashed</b> — <see cref="TokenHash"/> is a hex SHA-256 and the plaintext is
/// returned to the client exactly once. Refresh tokens rotate on use: the presented row is revoked and
/// a new row is written with <see cref="RotatedFromId"/> pointing at it. A <em>reused</em> (already
/// rotated) token revokes the whole grant, which is why the chain is recorded.
/// </remarks>
public class OAuthRefreshToken
{
    public Guid Id { get; set; }

    /// <summary>Hex SHA-256 of the issued refresh token. Unique; the lookup key.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>Space-separated granted scopes.</summary>
    public string Scope { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>The row this token replaced, when it was minted by a rotation; null for a first issue.</summary>
    public Guid? RotatedFromId { get; set; }
}

/// <summary>
/// One bridged MCP tool call (<c>mcp_tool_calls</c>, docs/design/mcp-oauth.md §6): usage is reportable
/// per user and abuse is visible.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one row is written per <c>POST /api/v1/assistant/tool-call</c>, <b>whether the call
/// succeeded, was refused, or failed</b>. A read tool's row is a <b>record, not a charge</b>: the
/// table exists for attribution and abuse review, and it deliberately does not feed billing.
/// </para>
/// <para>
/// <b><see cref="Succeeded"/> is transport-level completion, mirroring the response's
/// <c>isError</c>.</b> It is false when the bridge refused the call before executing it (a gated tool
/// with admin tools disabled, an unknown tool, a missing name) and when the tool threw; a tool body's
/// own refusal — "admin privileges required", a plan-gate or rate-limit refusal — is an ordinary
/// result the model reads, so it is recorded as <c>true</c> with no error code.
/// </para>
/// <para>
/// <b>Nothing here is written to <c>usage_events</c>.</b> That ledger records what the platform buys
/// from a provider; a tool call is not a provider purchase, and a zero-cost row there would distort
/// provider-cost reporting. Charging is unchanged: the existing credit preflight and ledger already
/// price workflows and paid picture tools, and the MCP path inherits them.
/// </para>
/// </remarks>
public class McpToolCall
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// The caller's active organization at call time. Recorded as <see cref="Guid.Empty"/> when the
    /// verified token carried no active organization, so a call is never left unmetered.
    /// </summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The requested tool name, exactly as the catalog reported it (empty for a nameless call).</summary>
    public string ToolName { get; set; } = string.Empty;

    public DateTime CalledAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True when the call produced a result the model can act on; false when the call was REFUSED —
    /// by the bridge before the tool ran, or by the tool body's own gate (an authority, plan or
    /// rate-limit refusal) — or when the tool threw.
    /// </summary>
    /// <remarks>
    /// A tool-body refusal still comes back to the client as HTTP 200 with <c>isError: false</c>,
    /// because the model is meant to read it; this column is the operator-facing view, where "somebody
    /// kept calling a tool they have no authority for" must not look like three successful calls.
    /// </remarks>
    public bool Succeeded { get; set; }

    /// <summary>
    /// Short machine-readable reason when <see cref="Succeeded"/> is false: <c>name_required</c>,
    /// <c>unknown_tool</c>, <c>mcp_admin_tools_disabled</c>, <c>tool_execution_failed</c>,
    /// <c>cancelled</c> or <c>tool_refused</c> (the tool ran and declined — see
    /// <c>AssistantToolRefusal</c>). Null on a completed call. The literal tool text is NOT stored here.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>Wall-clock duration of the bridged call, in milliseconds.</summary>
    public int DurationMs { get; set; }
}
