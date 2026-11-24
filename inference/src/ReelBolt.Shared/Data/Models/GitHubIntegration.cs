namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One ReelBolt project linked to one GitHub repository (<c>project_github_links</c>). A push to the
/// linked branch is pulled into the project's file library — see
/// <c>docs/github-app.md</c> "What \"pull\" means".
///
/// <para>
/// <b>Identity.</b> The link is keyed on <see cref="RepositoryId"/> — GitHub's numeric, immutable
/// repository id — and never on <see cref="RepositoryFullName"/>, which changes when a repository is
/// renamed or transferred. A rename therefore keeps working; a repository deleted and recreated under
/// the same name has a different id and is a different repository, which is the point.
/// </para>
///
/// <para>
/// <b>Tenancy.</b> This table carries no EF tenant query filter, deliberately, for the same reason
/// <see cref="ProjectVoice"/> does not: it is a child of <see cref="Project"/>, reached either through
/// the project access check that every <c>/api/v1/projects/{projectId}/github</c> action performs, or
/// by the webhook, which runs under an explicit system scope and resolves the row from a
/// signature-verified repository identity. <see cref="OrganizationId"/> is denormalized for audit and
/// for the installation-ownership check, not as a second boundary.
/// </para>
/// </summary>
public class ProjectGitHubLink
{
    public Guid Id { get; set; }

    /// <summary>The project this repository is pulled into. One repository per project, one project per repository.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>The project's organization at link time. Audit and installation-ownership only.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>GitHub App installation that covers this repository.</summary>
    public long InstallationId { get; set; }

    /// <summary>GitHub's numeric repository id — the stable identity of the repository.</summary>
    public long RepositoryId { get; set; }

    /// <summary><c>owner/name</c> as it was when linked. Display only; never matched on.</summary>
    public string RepositoryFullName { get; set; } = string.Empty;

    /// <summary>The repository's default branch as reported by GitHub.</summary>
    public string? RepositoryDefaultBranch { get; set; }

    /// <summary>
    /// The branch a push must target to be pulled. Null means the repository's default branch.
    /// A push to any other branch is acknowledged and ignored.
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Optional repository-relative directory the pull is confined to (e.g. <c>video</c>). Null pulls
    /// the whole repository.
    /// </summary>
    public string? PathPrefix { get; set; }

    /// <summary>
    /// The files and folders the project mirrors, as a JSON array of repository-relative paths
    /// (<c>include_paths_json</c>, jsonb). A folder includes everything under it, including files added
    /// to it later; a file includes only itself. Null means everything (under <see cref="PathPrefix"/>,
    /// when that is set). Always written canonical by <c>GitHubIncludePaths.TryNormalize</c>, and every
    /// path lies inside <see cref="PathPrefix"/> when both are set — a pulled file keeps its path relative
    /// to the prefix, exactly as it did before a selection existed.
    /// </summary>
    public string? IncludePathsJson { get; set; }

    /// <summary>Commit SHA the project's GitHub-sourced files currently mirror. Null before the first pull.</summary>
    public string? LastPulledCommit { get; set; }

    /// <summary>When the last successful pull finished.</summary>
    public DateTime? LastPulledAt { get; set; }

    /// <summary>Delivery id of the webhook that produced the last successful pull.</summary>
    public string? LastPulledDeliveryId { get; set; }

    /// <summary>Files the project mirrors after the last successful pull (the selection at that commit).</summary>
    public int LastPulledFileCount { get; set; }

    /// <summary>Reason the last pull attempt failed. Null after a success.</summary>
    public string? LastError { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The organization that owns a GitHub App installation (<c>github_app_installations</c>).
///
/// <para>
/// The installation is the trust anchor for a private repository. GitHub tells us the app is installed
/// on a repository, but it cannot tell us <i>which ReelBolt organization</i> installed it — and anyone
/// who can edit a project could otherwise link someone else's private repository and read its contents
/// in their own project. So the first organization to link a repository under an installation claims
/// that installation, and every later link under the same installation from a different organization is
/// answered <b>404</b> — the same non-oracle rule the rest of the tenancy boundary uses, never a 403.
/// </para>
/// </summary>
public class GitHubAppInstallation
{
    public Guid Id { get; set; }

    /// <summary>GitHub's numeric installation id.</summary>
    public long InstallationId { get; set; }

    /// <summary>The organization that claimed this installation.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The GitHub account the app was installed on (user or organization login). Display only.</summary>
    public string AccountLogin { get; set; } = string.Empty;

    /// <summary><c>all</c> or <c>selected</c>, as GitHub reports it.</summary>
    public string? RepositorySelection { get; set; }

    public Guid ClaimedByUserId { get; set; }
    public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One received GitHub webhook delivery (<c>github_webhook_deliveries</c>). Two jobs in one row: the
/// unique <see cref="DeliveryId"/> makes a redelivery a no-op, and a null <see cref="ProcessedAt"/> is
/// the work queue the pull worker drains.
///
/// <para>
/// Platform-level and organization-less on purpose, exactly like <c>BillingWebhookEvent</c>: the row is
/// written before anything is known about the payload's owner, and a repository that is not linked must
/// still be recorded (so a redelivery of it is also a no-op) without inventing a tenant for it.
/// </para>
/// </summary>
public class GitHubWebhookDelivery
{
    public Guid Id { get; set; }

    /// <summary>The <c>X-GitHub-Delivery</c> header — GitHub's idempotency key for one delivery.</summary>
    public string DeliveryId { get; set; } = string.Empty;

    /// <summary>The <c>X-GitHub-Event</c> header (<c>push</c>, <c>ping</c>, …).</summary>
    public string Event { get; set; } = string.Empty;

    /// <summary><c>owner/name</c> from the payload, when it has one.</summary>
    public string? RepositoryFullName { get; set; }

    /// <summary>GitHub's numeric installation id from the payload, when it has one.</summary>
    public long? InstallationId { get; set; }

    /// <summary>The verified raw body, stored verbatim.</summary>
    public string PayloadJson { get; set; } = "{}";

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the delivery reached a terminal outcome. Null means the pull worker still owes it work.</summary>
    public DateTime? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>
    /// Terminal outcome: <c>queued</c>, <c>pulled</c>, <c>duplicate</c>, <c>no_link</c>,
    /// <c>ignored_ref</c>, <c>installation_mismatch</c>, <c>stale</c>, <c>unchanged</c>,
    /// <c>ignored_event</c>, <c>failed</c>, and for a <c>github_app_authorization</c> delivery
    /// <c>authorization_revoked</c> or <c>authorization_unknown</c>.
    /// </summary>

    public string? Outcome { get; set; }

    /// <summary>Failure reason. Never contains a secret: the payload is stored, the token and the webhook secret are not.</summary>
    public string? Error { get; set; }
}
