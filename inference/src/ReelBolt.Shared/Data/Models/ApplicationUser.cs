namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Represents an application user (tenant).
/// </summary>
public class ApplicationUser
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>bcrypt hash; null for OAuth-only accounts (no local password), which can never pass a password check.</summary>
    public string? PasswordHash { get; set; } = string.Empty;
    /// <summary>Platform admin (kept under its historic name; not renamed).</summary>
    public bool IsAdmin { get; set; }
    public bool MustChangePassword { get; set; } = true;

    /// <summary>The workspace the user last worked in; used to pick the active org at login.</summary>
    public Guid? LastActiveOrganizationId { get; set; }

    /// <summary>When the address was confirmed; null while unverified. Existing rows are backfilled to CreatedAt.</summary>
    public DateTime? EmailVerifiedAt { get; set; }

    /// <summary>Session kill switch: the Go auth middleware rejects JWTs issued before this instant.</summary>
    public DateTime? TokensInvalidatedAt { get; set; }

    /// <summary>Set when the user asked for deletion; cleared if they log in within the grace period.</summary>
    public DateTime? DeletionRequestedAt { get; set; }

    /// <summary>Client IP recorded at self-serve signup (abuse review); null for admin-created users.</summary>
    public string? SignupIp { get; set; }

    /// <summary>How the account authenticates: <c>Password</c> (default) or an OAuth provider name.</summary>
    public string AuthProvider { get; set; } = "Password";

    /// <summary>
    /// Per-user opt-in for the administrative assistant tools over MCP (docs/design/mcp-oauth.md §6).
    /// Off by default. When false the MCP bridge neither lists nor executes the gated tools; when true
    /// it does, and each gated tool STILL runs its own per-call authority check — this setting is an
    /// additional condition, never a substitute for platform/organization authority.
    /// </summary>
    public bool McpAdminToolsEnabled { get; set; }

    /// <summary>
    /// Per-user opt-in that lets connected tools (MCP) allow sending this user's media to AI services for a run they
    /// start (<c>inputs.allowMediaEgress = true</c>). Off by default; read live by the tool on every call, never cached.
    /// It gives the ability only: each run must still set the value itself.
    /// </summary>
    public bool McpMediaEgressEnabled { get; set; }

    /// <summary>
    /// Assistant auto mode (docs/design/assistant-approvals.md). Off by default: the in-app assistant
    /// asks with a permission card before every change. When true it skips the card for every action —
    /// deletions and paid runs included — and still reports what it did. Read live on each gated call,
    /// never cached for a turn. Only the in-app chat transport consults it; the MCP bridge has no card.
    /// </summary>
    public bool AssistantAutoApprove { get; set; }

    /// <summary>Saved UI language (BCP 47 tag, e.g. <c>it</c>); null = follow the browser. Validated by the Go API against the locale registry.</summary>
    public string? Locale { get; set; }

    /// <summary>
    /// Hidden service account behind one API key (never a login, never a platform admin, always a plain Member of one
    /// organization). Member lists and user directories must exclude these rows.
    /// </summary>
    public bool IsServiceAccount { get; set; }

    public ICollection<Project> Projects { get; set; } = new List<Project>();
    public ICollection<AgentDefinition> AgentDefinitions { get; set; } = new List<AgentDefinition>();
}
