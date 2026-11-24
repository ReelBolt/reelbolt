using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// What a delivery needs. <see cref="RequiresPull"/> false means the delivery is settled here and now
/// with <see cref="Outcome"/> — there is nothing left to do. True means the worker owes it a pull, and
/// <see cref="Link"/>/<see cref="CommitSha"/> say exactly which project and which commit.
/// </summary>
public sealed record GitHubDeliveryPlan(string Outcome, ProjectGitHubLink? Link, string? CommitSha, DateTime? PushedAt)
{
    public bool RequiresPull => Link is not null && CommitSha is not null;

    public static GitHubDeliveryPlan Settled(string outcome) => new(outcome, null, null, null);
}

/// <summary>
/// Decides what a verified delivery means, and performs the pull. This is the one place that answers
/// "which project does this webhook touch?" — and the answer is always <b>the project the pushed
/// repository is linked to</b>, resolved by GitHub's numeric repository id. Nothing in the payload
/// (not a branch name, not a path, not a commit message) can name a project, so a verified push has no
/// way to reach a project other than its own.
/// </summary>
public interface IGitHubDeliveryProcessor
{
    Task<GitHubDeliveryPlan> PlanAsync(GitHubWebhookDelivery delivery, CancellationToken ct);

    Task<GitHubPullOutcome> PullAsync(GitHubDeliveryPlan plan, GitHubWebhookDelivery delivery, CancellationToken ct);
}

/// <inheritdoc />
public sealed class GitHubDeliveryProcessor : IGitHubDeliveryProcessor
{
    public const string EventPush = "push";

    /// <summary>
    /// Sent when a person revokes ReelBolt in their GitHub settings. A GitHub App cannot unsubscribe
    /// from it, which is the point: it is the only signal GitHub gives that a stored user access token
    /// has stopped working, and the documented response is to stop calling the API for that user.
    /// </summary>
    public const string EventAuthorization = "github_app_authorization";

    private readonly InferenceApiDbContext _db;
    private readonly IGitHubRepositoryPullService _pull;
    private readonly IGitHubAccountService _accounts;
    private readonly ILogger<GitHubDeliveryProcessor> _logger;

    public GitHubDeliveryProcessor(
        InferenceApiDbContext db,
        IGitHubRepositoryPullService pull,
        IGitHubAccountService accounts,
        ILogger<GitHubDeliveryProcessor> logger)
    {
        _db = db;
        _pull = pull;
        _accounts = accounts;
        _logger = logger;
    }

    public async Task<GitHubDeliveryPlan> PlanAsync(GitHubWebhookDelivery delivery, CancellationToken ct)
    {
        if (string.Equals(delivery.Event, EventAuthorization, StringComparison.Ordinal))
            return await PlanAuthorizationAsync(delivery, ct);

        if (!string.Equals(delivery.Event, EventPush, StringComparison.Ordinal))
            return GitHubDeliveryPlan.Settled("ignored_event");

        using JsonDocument document = Parse(delivery.PayloadJson);
        JsonElement root = document.RootElement;

        if (!TryGetInt64(root, "repository", "id", out long repositoryId))
            return GitHubDeliveryPlan.Settled("ignored_event");

        ProjectGitHubLink? link = await _db.ProjectGitHubLinks
            .FirstOrDefaultAsync(l => l.RepositoryId == repositoryId, ct);

        if (link is null) return GitHubDeliveryPlan.Settled("no_link");

        // The delivery must have come from the installation the link was made under. A mismatch is not
        // an error to report to the caller (that would be an oracle); it is simply not our push.
        if (delivery.InstallationId is { } installationId && installationId != link.InstallationId)
        {
            _logger.LogWarning(
                "Delivery for repository {RepositoryId} arrived under installation {InstallationId}, but that repository is linked under {LinkedInstallationId}",
                repositoryId, installationId, link.InstallationId);
            return GitHubDeliveryPlan.Settled("installation_mismatch");
        }

        string expectedRef = "refs/heads/" + (link.Branch ?? link.RepositoryDefaultBranch ?? "main");
        string? pushedRef = GetString(root, "ref");
        if (!string.Equals(pushedRef, expectedRef, StringComparison.Ordinal))
            return GitHubDeliveryPlan.Settled("ignored_ref");

        string? after = GetString(root, "after");
        if (string.IsNullOrWhiteSpace(after) || IsZeroSha(after))
            return GitHubDeliveryPlan.Settled("ignored_ref");

        if (GetBool(root, "deleted"))
            return GitHubDeliveryPlan.Settled("ignored_ref");

        if (string.Equals(link.LastPulledCommit, after, StringComparison.OrdinalIgnoreCase))
            return GitHubDeliveryPlan.Settled("unchanged");

        return new GitHubDeliveryPlan("pulled", link, after, GetHeadCommitTimestamp(root));
    }

    public Task<GitHubPullOutcome> PullAsync(GitHubDeliveryPlan plan, GitHubWebhookDelivery delivery, CancellationToken ct) =>
        _pull.PullAsync(plan.Link!, plan.CommitSha!, plan.PushedAt, delivery.DeliveryId, ct);

    /// <summary>
    /// A person revoked ReelBolt's access to their GitHub account. The link between the payload and the
    /// rows is GitHub's numeric account id in <c>sender</c> — never the login, which they may have
    /// changed, and never anything from a request body. Every connection that account holds is revoked,
    /// in every workspace: the person revoked ReelBolt, not one workspace's view of it, and GitHub has
    /// already stopped honouring the tokens.
    ///
    /// <para>
    /// Note what this does <b>not</b> touch: projects linked to repositories that account owned. Those
    /// are pulled with the app's installation token, which this event does not affect, so pushes keep
    /// arriving. Revoking a <i>person's</i> OAuth grant must not silently detach a team's repository.
    /// </para>
    /// </summary>
    private async Task<GitHubDeliveryPlan> PlanAuthorizationAsync(GitHubWebhookDelivery delivery, CancellationToken ct)
    {
        using JsonDocument document = Parse(delivery.PayloadJson);
        JsonElement root = document.RootElement;

        if (!string.Equals(GetString(root, "action"), "revoked", StringComparison.Ordinal))
            return GitHubDeliveryPlan.Settled("ignored_event");

        if (!TryGetInt64(root, "sender", "id", out long gitHubUserId))
            return GitHubDeliveryPlan.Settled("ignored_event");

        int revoked = await _accounts.RevokeByGitHubUserIdAsync(gitHubUserId, ct);

        _logger.LogInformation(
            "GitHub account {GitHubUserId} revoked ReelBolt's authorization; {Count} connection(s) revoked",
            gitHubUserId, revoked);

        return GitHubDeliveryPlan.Settled(revoked > 0 ? "authorization_revoked" : "authorization_unknown");
    }

    private static JsonDocument Parse(string payloadJson)
    {
        try
        {
            return JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static bool TryGetInt64(JsonElement root, string parent, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(parent, out JsonElement element) &&
               element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out JsonElement inner) &&
               inner.ValueKind == JsonValueKind.Number &&
               inner.TryGetInt64(out value);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.True;

    /// <summary>
    /// When the pushed commit was authored, used to drop a delivery that arrives after a newer one.
    /// GitHub retries deliveries, and a retry can be delivered out of order; the project mirrors one
    /// commit, so the older one must not win. Absent or unparseable is null — "unknown", which the pull
    /// treats as "apply it", since the commit-sha check still makes an exact replay a no-op.
    /// </summary>
    private static DateTime? GetHeadCommitTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("head_commit", out JsonElement head) || head.ValueKind != JsonValueKind.Object)
            return null;

        if (!head.TryGetProperty("timestamp", out JsonElement timestamp) || timestamp.ValueKind != JsonValueKind.String)
            return null;

        return DateTime.TryParse(timestamp.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out DateTime parsed)
            ? parsed
            : null;
    }

    private static bool IsZeroSha(string sha) => sha.All(c => c == '0');
}
