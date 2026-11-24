namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A provider ACCOUNT (docs/design/model-catalog.md §2): kind, endpoint, key, headers, timeout and
/// OAuth mode, platform-managed (null organization) or an organization's own. What it serves lives one
/// and two levels down: its <see cref="Models"/> (vendor model code + display name) and each model's
/// <see cref="ModelCapability"/> rows (capability, default, enabled, last test, price source). Every
/// reference that once held a provider row id now holds a model capability id.
/// </summary>
public class InferenceProvider
{
    public Guid Id { get; set; }

    /// <summary>Owning organization (bring-your-own provider). Null = platform-managed.</summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>Account name, unique per organization (NULL = the platform scope).</summary>
    public string Name { get; set; } = string.Empty;

    public InferenceProviderKind Kind { get; set; }

    /// <summary>Azure resource URL, or OpenAI-compatible base URL.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Data-Protection ciphertext; never leaves the backend.</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>Display-only hint, e.g. the last four characters of the key.</summary>
    public string? ApiKeyLastFour { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The platform compute provider (§5.4): created by the catalog upgrader, never editable or
    /// deletable from the admin API.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// "Dev/OAuth (Personal use only)" — marks this account as talking to Anthropic through a
    /// personal Claude subscription credential (<c>sk-ant-oat…</c>, or the <c>env:</c> sentinel
    /// resolving to <c>ANTHROPIC_AUTH_TOKEN</c>/an <c>ant auth login</c> profile) rather than a
    /// Console API key. Only meaningful when <see cref="Kind"/> is
    /// <see cref="InferenceProviderKind.Anthropic"/> — the controller rejects the combination
    /// otherwise.
    /// <para>
    /// Anthropic's Messages API silently validates OAuth-authenticated requests: for every model
    /// except Haiku, the <c>system</c> field's first block must be EXACTLY "You are Claude Code,
    /// Anthropic's official CLI for Claude." (as its own separate block, not concatenated into a
    /// larger one), or the API returns an unhelpful HTTP 400 <c>invalid_request_error</c> with
    /// message "Error" (undocumented; reproduced live in
    /// https://github.com/anthropics/claude-code/issues/40515). Setting this flag makes
    /// <see cref="ChatClientFactory"/>/<c>AnthropicChatOptionsAdapter</c> — see the "why" of the
    /// flag name — always prepend that exact phrase as its own leading system block, and adds the
    /// <c>anthropic-beta: claude-code-20250219</c>, <c>user-agent</c>, and <c>x-app</c> headers the
    /// issue's working reproduction used, on every request this provider serves. See
    /// docs/anthropic-provider.md.
    /// </para>
    /// </summary>
    public bool PersonalOAuthMode { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>jsonb; some self-hosted gateways need a custom auth header.</summary>
    public string? ExtraHeadersJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<InferenceModel> Models { get; set; } = new List<InferenceModel>();
}
