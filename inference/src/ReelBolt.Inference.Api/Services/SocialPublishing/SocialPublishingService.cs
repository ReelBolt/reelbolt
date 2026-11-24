using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>Why a post is not sent right now. <see cref="Retryable"/> refusals go back to the queue with a backoff; the rest end the post.</summary>
public sealed record SocialRefusal(string Code, string Message, bool Retryable = false, TimeSpan? RetryAfter = null);

/// <summary>A post this process now holds the lease on.</summary>
public sealed record SocialClaim(Guid PostId, string Owner);

/// <summary>What one sweep did, for logs and tests.</summary>
public sealed record SocialSweepResult(IReadOnlyList<SocialClaim> Claims, int MarkedUnknown, int MissedWindow);

/// <summary>Stable codes in <see cref="SocialPost.FailureCode"/>. The wording shown to people lives next to each use.</summary>
public static class SocialFailureCodes
{
    public const string MissedWindow = "missed_window";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string OutputMissing = "output_missing";
    public const string OutputUnavailable = "output_unavailable";
    public const string AccessLost = "access_lost";
    public const string AccountUnavailable = "account_unavailable";
    public const string ReconnectNeeded = "reconnect_needed";
    public const string PlanNotEntitled = "plan_not_entitled";
    public const string PlatformUnavailable = "platform_unavailable";
    public const string PreflightFailed = "preflight_failed";
    public const string MonthlyCapReached = "monthly_cap_reached";
    public const string DailyBudgetReached = "daily_budget_reached";
    public const string ModerationBlocked = "moderation_blocked";
    public const string ModerationPending = "moderation_pending";
    public const string PlatformRefused = "platform_refused";
    public const string RetriesExhausted = "retries_exhausted";
    public const string RateLimited = "rate_limited";
    public const string PlatformError = "platform_error";
    public const string Cancelled = "cancelled";
}

public interface ISocialPublishingService
{
    /// <summary>
    /// One pass: marks expired leases <c>unknown</c>, fails posts that are too late, and claims the due ones within the
    /// per-platform and per-account caps. The caller publishes each claim with <see cref="PublishClaimedAsync"/>.
    /// </summary>
    Task<SocialSweepResult> ClaimDueAsync(CancellationToken ct);

    /// <summary>Claims one specific post if it is due and still scheduled (conditional UPDATE; exactly one caller wins).</summary>
    Task<SocialClaim?> TryClaimAsync(Guid postId, CancellationToken ct);

    /// <summary>Re-checks, uploads, publishes and records the outcome of a claimed post. Never throws for a platform failure.</summary>
    Task PublishClaimedAsync(SocialClaim claim, CancellationToken ct);
}

/// <summary>
/// The publish engine (docs/social-publishing.md). Multi-replica safe: a due post is claimed by a conditional UPDATE to
/// <c>publishing</c> with a lease that is renewed while the video uploads; two replicas can never both send it. At fire
/// time everything is checked again (the project and output still exist, the posting person still has edit access and is
/// still a member, the account still belongs to the organization and is active, the plan still includes publishing, no
/// output hold or quarantine, moderation fails closed, the platform limits). The remote id is stored the moment the
/// platform answers. A crash mid-publish leaves an expired lease, which becomes <c>unknown</c> and is NEVER retried by
/// the engine: a duplicate public post is worse than a missed one.
/// </summary>
public sealed class SocialPublishingService : ISocialPublishingService
{
    private readonly InferenceApiDbContext _db;
    private readonly DbContextOptions<InferenceApiDbContext> _dbOptions;
    private readonly ISocialPlatformRegistry _platforms;
    private readonly ISocialConnectionService _connections;
    private readonly ISocialOutputResolver _outputs;
    private readonly IFileStorageService _storage;
    private readonly ISocialPostModeration _moderation;
    private readonly IOrganizationEntitlements _entitlements;
    private readonly IUserActivityNotifier _notifier;
    private readonly SocialPublishEngineOptions _options;
    private readonly SocialPublishingOptions _publishing;
    private readonly ILogger<SocialPublishingService> _logger;
    private readonly TimeProvider _clock;

    public SocialPublishingService(
        InferenceApiDbContext db,
        DbContextOptions<InferenceApiDbContext> dbOptions,
        ISocialPlatformRegistry platforms,
        ISocialConnectionService connections,
        ISocialOutputResolver outputs,
        IFileStorageService storage,
        ISocialPostModeration moderation,
        IOrganizationEntitlements entitlements,
        IUserActivityNotifier notifier,
        IOptions<SocialPublishingOptions> options,
        ILogger<SocialPublishingService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _dbOptions = dbOptions;
        _platforms = platforms;
        _connections = connections;
        _outputs = outputs;
        _storage = storage;
        _moderation = moderation;
        _entitlements = entitlements;
        _notifier = notifier;
        _publishing = options.Value;
        _options = options.Value.Engine;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private TimeSpan Lease => TimeSpan.FromSeconds(Math.Max(5, _options.LeaseSeconds));

    // ------------------------------------------------------------------ sweep

    public async Task<SocialSweepResult> ClaimDueAsync(CancellationToken ct)
    {
        DateTime now = Now();
        int unknown = await MarkExpiredLeasesUnknownAsync(now, ct);
        int missed = await FailMissedWindowAsync(now, ct);

        List<Guid> due = await _db.SocialPosts.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Status == SocialPostStatus.Scheduled && p.ScheduledAt != null && p.ScheduledAt <= now
                        && (p.NextAttemptAt == null || p.NextAttemptAt <= now))
            .OrderBy(p => p.ScheduledAt)
            .Select(p => p.Id)
            .Take(Math.Max(1, _options.ClaimBatchSize) * 3)
            .ToListAsync(ct);

        List<SocialClaim> claims = [];
        foreach (Guid id in due)
        {
            if (claims.Count >= Math.Max(1, _options.ClaimBatchSize)) break;
            if (await TryClaimAsync(id, ct) is { } claim) claims.Add(claim);
        }

        return new SocialSweepResult(claims, unknown, missed);
    }

    /// <summary>An expired lease means the process died (or hung) after the claim: nobody knows whether it was sent.</summary>
    private async Task<int> MarkExpiredLeasesUnknownAsync(DateTime now, CancellationToken ct)
    {
        List<SocialPost> stale = await _db.SocialPosts.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Status == SocialPostStatus.Publishing && p.LeaseExpiresAt != null && p.LeaseExpiresAt < now)
            .Take(50)
            .ToListAsync(ct);

        int marked = 0;
        foreach (SocialPost post in stale)
        {
            const string message = "We could not confirm whether this post was published. Check the channel before posting it again.";
            int rows = await _db.SocialPosts.IgnoreQueryFilters()
                .Where(p => p.Id == post.Id && p.Status == SocialPostStatus.Publishing && p.LeaseOwner == post.LeaseOwner && p.LeaseExpiresAt < now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, SocialPostStatus.Unknown)
                    .SetProperty(p => p.FailureCode, SocialFailureCodes.OutcomeUnknown)
                    .SetProperty(p => p.FailureMessage, message)
                    .SetProperty(p => p.LeaseOwner, (string?)null)
                    .SetProperty(p => p.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            if (rows == 0) continue;
            marked++;
            await NotifyFailureAsync(post, "Check your post", message, ct);
        }

        return marked;
    }

    /// <summary>A post that never started and is later than the window (the service was down) is held, not sent at a surprising time.</summary>
    private async Task<int> FailMissedWindowAsync(DateTime now, CancellationToken ct)
    {
        DateTime cutoff = now - TimeSpan.FromMinutes(Math.Max(1, _options.MissedWindowMinutes));
        List<SocialPost> late = await _db.SocialPosts.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Status == SocialPostStatus.Scheduled && p.Attempts == 0 && p.ScheduledAt != null && p.ScheduledAt < cutoff)
            .Take(50)
            .ToListAsync(ct);

        int failed = 0;
        foreach (SocialPost post in late)
        {
            string message = $"This post was due {(post.ScheduledAt ?? now):yyyy-MM-dd HH:mm} UTC but could not be sent in time, so it was not posted. Schedule it again if you still want it.";
            int rows = await _db.SocialPosts.IgnoreQueryFilters()
                .Where(p => p.Id == post.Id && p.Status == SocialPostStatus.Scheduled && p.Attempts == 0)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, SocialPostStatus.Failed)
                    .SetProperty(p => p.FailureCode, SocialFailureCodes.MissedWindow)
                    .SetProperty(p => p.FailureMessage, message)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            if (rows == 0) continue;
            failed++;
            await NotifyFailureAsync(post, $"Your post to {SocialPlatforms.DisplayName(post.Platform)} was not sent", message, ct);
        }

        return failed;
    }

    public async Task<SocialClaim?> TryClaimAsync(Guid postId, CancellationToken ct)
    {
        DateTime now = Now();
        SocialPost? post = await _db.SocialPosts.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.Id == postId, ct);
        if (post is null || post.Status != SocialPostStatus.Scheduled) return null;

        // Caps: how many are uploading right now on this platform and on this account (live leases only).
        if (await ActiveCountAsync(p => p.Platform == post.Platform, now, ct) >= Math.Max(1, _options.MaxConcurrentPerPlatform)) return null;
        if (await ActiveCountAsync(p => p.SocialAccountId == post.SocialAccountId, now, ct) >= Math.Max(1, _options.MaxConcurrentPerAccount)) return null;

        string owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        DateTime lease = now + Lease;
        int rows = await _db.SocialPosts.IgnoreQueryFilters()
            .Where(p => p.Id == postId && p.Status == SocialPostStatus.Scheduled && p.LeaseOwner == null
                        && p.ScheduledAt != null && p.ScheduledAt <= now && (p.NextAttemptAt == null || p.NextAttemptAt <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, SocialPostStatus.Publishing)
                .SetProperty(p => p.LeaseOwner, owner)
                .SetProperty(p => p.LeaseExpiresAt, lease)
                .SetProperty(p => p.Attempts, p => p.Attempts + 1)
                .SetProperty(p => p.NextAttemptAt, (DateTime?)null)
                .SetProperty(p => p.UpdatedAt, now), ct);
        if (rows == 0) return null;

        // Two replicas can pass the cap check together; the one that sees the cap exceeded gives its claim back.
        int platformActive = await ActiveCountAsync(p => p.Platform == post.Platform, now, ct);
        int accountActive = await ActiveCountAsync(p => p.SocialAccountId == post.SocialAccountId, now, ct);
        if (platformActive > Math.Max(1, _options.MaxConcurrentPerPlatform) || accountActive > Math.Max(1, _options.MaxConcurrentPerAccount))
        {
            await _db.SocialPosts.IgnoreQueryFilters()
                .Where(p => p.Id == postId && p.Status == SocialPostStatus.Publishing && p.LeaseOwner == owner)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, SocialPostStatus.Scheduled)
                    .SetProperty(p => p.LeaseOwner, (string?)null)
                    .SetProperty(p => p.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(p => p.Attempts, p => p.Attempts - 1), ct);
            return null;
        }

        return new SocialClaim(postId, owner);
    }

    private Task<int> ActiveCountAsync(System.Linq.Expressions.Expression<Func<SocialPost, bool>> scope, DateTime now, CancellationToken ct) =>
        _db.SocialPosts.IgnoreQueryFilters().AsNoTracking()
            .Where(scope)
            .Where(p => p.Status == SocialPostStatus.Publishing && p.LeaseExpiresAt != null && p.LeaseExpiresAt >= now)
            .CountAsync(ct);

    // ------------------------------------------------------------------ publish one

    public async Task PublishClaimedAsync(SocialClaim claim, CancellationToken ct)
    {
        bool platformCalled = false;
        SocialPost? post = null;
        using CancellationTokenSource leaseLost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task renewer = Task.CompletedTask;
        try
        {
            post = await _db.SocialPosts.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == claim.PostId && p.Status == SocialPostStatus.Publishing && p.LeaseOwner == claim.Owner, ct);
            if (post is null) return; // lost the lease already (expired and recovered, or cancelled by an operator)
            renewer = RenewLeaseAsync(claim, leaseLost, leaseLost.Token);

            (SocialRefusal? refusal, Prepared? prepared) = await PrepareAsync(post, leaseLost.Token);
            if (refusal is not null)
            {
                await ApplyRefusalAsync(post, claim, refusal, ct);
                return;
            }

            SocialPublishRequest request = new(
                post.Title, post.Body, ReadHashtags(post), prepared!.Output.SizeBytes ?? 0, prepared.Output.MimeType,
                innerCt => _storage.DownloadAsync(post.ProjectId, prepared.Output.StorageKey, innerCt),
                ReadOptions(post));

            platformCalled = true;
            SocialPublishResult result = await _connections.CallAsync(
                prepared.Account, token => prepared.Platform.PublishAsync(token, request, leaseLost.Token), leaseLost.Token);

            await RecordPublishedAsync(post, claim, result, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down. Before the platform was called nothing happened: give the post back. After, the lease expires into unknown.
            if (!platformCalled) await ReleaseAsync(claim, CancellationToken.None);
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            _logger.LogWarning("Social post {PostId} lost its lease while publishing; the sweeper decides its outcome", claim.PostId);
        }
        catch (SocialApiException ex) when (post is not null)
        {
            await ApplyPlatformFailureAsync(post, claim, ex, ct);
        }
        catch (HttpRequestException) when (post is not null)
        {
            await ApplyRefusalAsync(post, claim, new SocialRefusal(SocialFailureCodes.PlatformError, "The platform could not be reached.", Retryable: true), ct);
        }
        catch (Exception ex) when (post is not null)
        {
            // Anything else after the platform may have been called is an unknown outcome, never a blind retry.
            _logger.LogError("Social post {PostId} ended with an unexpected {Type}", post.Id, ex.GetType().Name);
            if (platformCalled) await MarkUnknownAsync(post, claim, ct);
            else await ApplyRefusalAsync(post, claim, new SocialRefusal(SocialFailureCodes.PlatformError, "The post could not be prepared.", Retryable: true), ct);
        }
        finally
        {
            await leaseLost.CancelAsync();
            try { await renewer; } catch (OperationCanceledException) { }
        }
    }

    private sealed record Prepared(SocialAccount Account, ISocialPlatform Platform, SocialResolvedOutput Output);

    /// <summary>The fire-time re-check. Order matters: authority and existence first, then the platform, then limits, then moderation.</summary>
    private async Task<(SocialRefusal?, Prepared?)> PrepareAsync(SocialPost post, CancellationToken ct)
    {
        Organization? org = await _db.Organizations.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(o => o.Id == post.OrganizationId, ct);
        if (org is null || org.DeletedAt is not null)
            return (new SocialRefusal(SocialFailureCodes.AccessLost, "This workspace is no longer available."), null);

        Project? project = await _db.Projects.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.Id == post.ProjectId, ct);
        if (project is null || project.OrganizationId != post.OrganizationId)
            return (new SocialRefusal(SocialFailureCodes.OutputMissing, "The project this post was made from no longer exists."), null);

        // The person who scheduled it must still be a member who may edit this project, now, not when they scheduled it.
        OrganizationMembership? member = await _db.OrganizationMemberships.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.OrganizationId == post.OrganizationId && m.UserId == post.CreatedByUserId, ct);
        if (member is null || !ProjectAccessService.Allowed(member.Role, ProjectPermission.Edit, project.OwnerId == post.CreatedByUserId))
            return (new SocialRefusal(SocialFailureCodes.AccessLost, "The person who scheduled this post can no longer edit the project, so it was not posted."), null);

        SocialAccount? account = await _db.SocialAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == post.SocialAccountId, ct);
        if (account is null || account.OrganizationId != post.OrganizationId || account.Platform != post.Platform)
            return (new SocialRefusal(SocialFailureCodes.AccountUnavailable, "The connected account is no longer available."), null);
        if (account.Status == SocialAccountStatus.ReconnectNeeded)
            return (new SocialRefusal(SocialFailureCodes.ReconnectNeeded, $"The {SocialPlatforms.DisplayName(account.Platform)} account needs to be reconnected."), null);
        if (account.Status != SocialAccountStatus.Active)
            return (new SocialRefusal(SocialFailureCodes.AccountUnavailable, "The connected account was disconnected."), null);

        OrganizationEntitlementSet set = await _entitlements.GetAsync(post.OrganizationId, ct);
        if (PlanGate.CheckSocialPublishing(set) is { } violation)
            return (new SocialRefusal(SocialFailureCodes.PlanNotEntitled, violation.Message), null);

        ISocialPlatform? platform = _platforms.Get(post.Platform);
        if (platform is not { IsConfigured: true })
            return (new SocialRefusal(SocialFailureCodes.PlatformUnavailable, $"{SocialPlatforms.DisplayName(post.Platform)} publishing is not available on this server."), null);

        SocialResolvedOutput? output = await _outputs.ResolveAsync(post.OrganizationId, post.ProjectId, post.OutputKind, post.OutputId, ct);
        if (output is null)
            return (new SocialRefusal(SocialFailureCodes.OutputMissing, "The video this post was made from no longer exists."), null);

        if (await IsHeldAsync(project, output, ct))
            return (new SocialRefusal(SocialFailureCodes.OutputUnavailable, OutputHoldResponse.Message), null);

        SocialPreflightResult preflight = SocialPreflight.Check(new SocialPreflightInput(
            post.Platform, post.Title, post.Body, ReadHashtags(post), output.SizeBytes, output.DurationSeconds, output.Width, output.Height,
            ReadOptions(post).GetValueOrDefault("privacy")));
        if (!preflight.Ok)
            return (new SocialRefusal(SocialFailureCodes.PreflightFailed, preflight.Issues.First(i => i.Severity == SocialPreflightSeverity.Error).Message), null);

        SocialModerationVerdict verdict = await _moderation.ScreenAsync(post, output, ct);
        if (!verdict.Allowed)
        {
            return verdict.Code == PublishModerationOutcome.PendingCode
                ? (new SocialRefusal(SocialFailureCodes.ModerationPending, verdict.Message, Retryable: true), null)
                : (new SocialRefusal(SocialFailureCodes.ModerationBlocked, verdict.Message), null);
        }

        // Last, so a post that is refused for any other reason never takes a slot of the shared quota.
        if (await CheckBudgetsAsync(post, ct) is { } budget) return (budget, null);

        return (null, new Prepared(account, platform, output));
    }

    private async Task<bool> IsHeldAsync(Project project, SocialResolvedOutput output, CancellationToken ct)
    {
        ProjectOutputHoldState hold = await ProjectOutputHold.ReadAsync(_db, project.Id, ct);
        if (output.FileFlagged) return true;
        bool held = hold.IsActive && (output.IsProjectFile
            ? hold.HoldsFile(output.ProducedAt)
            : hold.HoldsOutput(output.ProducedAt, output.RunStartedAt));
        if (held) return true;
        return ContentQuarantineRule.Predates(project.ContentQuarantinedAt, output.ProducedAt, output.RunStartedAt);
    }

    /// <summary>
    /// Takes this post's publish slot and enforces the organization's monthly cap, the organization's share of the
    /// platform's daily budget and the platform's global daily budget (our shared app quota). Counted from
    /// <c>social_publish_ledger</c>, not from the posts: deleting a project or a post never hands quota back. One slot per
    /// post (a retry of the same post does not take a second); a refused post gives its fresh slot straight back.
    /// </summary>
    private async Task<SocialRefusal?> CheckBudgetsAsync(SocialPost post, CancellationToken ct)
    {
        DateTime now = Now();
        DateTime dayStart = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc);
        DateTime monthStart = new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        int monthlyCap = _options.MonthlyPostCapPerOrganization;
        int dailyGlobal = _options.DailyBudgetFor(post.Platform);
        int dailyPerOrg = _publishing.MaxPerOrgPerDayFor(post.Platform);
        if (monthlyCap <= 0 && dailyGlobal <= 0 && dailyPerOrg <= 0) return null;

        // Its own context: the slot is committed (or given back) on its own, whatever else this scope has pending.
        await using InferenceApiDbContext db = new(_dbOptions);
        bool tookSlot = false;
        if (!await db.SocialPublishLedger.AnyAsync(l => l.PostId == post.Id, ct))
        {
            db.SocialPublishLedger.Add(new SocialPublishLedgerEntry
            {
                Id = Guid.NewGuid(),
                OrganizationId = post.OrganizationId,
                Platform = post.Platform,
                PostId = post.Id,
                Day = dayStart,
                CreatedAt = now
            });
            try
            {
                await db.SaveChangesAsync(ct);
                tookSlot = true;
            }
            catch (DbUpdateException)
            {
                // Another attempt of this same post took it first: it is already counted.
                db.ChangeTracker.Clear();
            }
        }

        SocialRefusal? refusal = null;
        if (monthlyCap > 0 &&
            await db.SocialPublishLedger.CountAsync(l => l.OrganizationId == post.OrganizationId && l.Day >= monthStart, ct) > monthlyCap)
        {
            refusal = new SocialRefusal(SocialFailureCodes.MonthlyCapReached,
                $"Your workspace reached its limit of {monthlyCap} social posts this month. It resets on the 1st.");
        }
        else if (dailyPerOrg > 0 &&
            await db.SocialPublishLedger.CountAsync(l => l.OrganizationId == post.OrganizationId && l.Platform == post.Platform && l.Day == dayStart, ct) > dailyPerOrg)
        {
            refusal = new SocialRefusal(SocialFailureCodes.DailyBudgetReached,
                $"Your workspace reached its share of {SocialPlatforms.DisplayName(post.Platform)} posting for today ({dailyPerOrg}). Try again tomorrow.");
        }
        else if (dailyGlobal > 0 &&
            await db.SocialPublishLedger.CountAsync(l => l.Platform == post.Platform && l.Day == dayStart, ct) > dailyGlobal)
        {
            refusal = new SocialRefusal(SocialFailureCodes.DailyBudgetReached,
                $"{SocialPlatforms.DisplayName(post.Platform)} posting has reached today's limit for all of ReelBolt. Try again tomorrow.");
        }

        if (refusal is not null && tookSlot)
            await db.SocialPublishLedger.Where(l => l.PostId == post.Id).ExecuteDeleteAsync(CancellationToken.None);
        return refusal;
    }

    // ------------------------------------------------------------------ outcomes

    private async Task RecordPublishedAsync(SocialPost post, SocialClaim claim, SocialPublishResult result, CancellationToken ct)
    {
        DateTime now = Now();
        string remoteId = Truncate(result.RemotePostId, 300)!;
        string? url = Truncate(result.RemotePostUrl, 2000);

        // The platform has answered, so this is true whatever happened to the lease meanwhile: record it even over `unknown`.
        int rows = await _db.SocialPosts.IgnoreQueryFilters()
            .Where(p => p.Id == claim.PostId && (p.Status == SocialPostStatus.Publishing || p.Status == SocialPostStatus.Unknown))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, SocialPostStatus.Published)
                .SetProperty(p => p.RemotePostId, remoteId)
                .SetProperty(p => p.RemotePostUrl, url)
                .SetProperty(p => p.PublishedAt, now)
                .SetProperty(p => p.FailureCode, (string?)null)
                .SetProperty(p => p.FailureMessage, (string?)null)
                .SetProperty(p => p.LeaseOwner, (string?)null)
                .SetProperty(p => p.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(p => p.UpdatedAt, now), CancellationToken.None);
        if (rows == 0)
        {
            _logger.LogError("Social post {PostId} was published as {Remote} but its row could not be updated (state changed)", post.Id, remoteId);
            return;
        }

        await _notifier.SocialPublishingAsync(post.OrganizationId, post.ProjectId, post.CreatedByUserId,
            $"Published to {SocialPlatforms.DisplayName(post.Platform)}", url is null ? "Your post is live." : $"Your post is live: {url}", CancellationToken.None);
    }

    private async Task ApplyPlatformFailureAsync(SocialPost post, SocialClaim claim, SocialApiException ex, CancellationToken ct)
    {
        if (ex.PostMayExist)
        {
            await MarkUnknownAsync(post, claim, ct);
            return;
        }

        string platform = SocialPlatforms.DisplayName(post.Platform);
        SocialRefusal refusal = ex.Kind switch
        {
            SocialErrorKind.Unauthorized => new(SocialFailureCodes.ReconnectNeeded, $"The {platform} account needs to be reconnected."),
            SocialErrorKind.RateLimited => new(SocialFailureCodes.RateLimited, $"{platform} is limiting requests right now.", Retryable: true, RetryAfter: ex.RetryAfter),
            SocialErrorKind.ServerError => new(SocialFailureCodes.PlatformError, $"{platform} had a problem.", Retryable: true, RetryAfter: ex.RetryAfter),
            SocialErrorKind.NotConfigured => new(SocialFailureCodes.PlatformUnavailable, $"{platform} publishing is not available on this server."),
            _ => new(SocialFailureCodes.PlatformRefused, $"{platform} did not accept this post.")
        };

        bool notifyCreator = true;
        if (refusal.Code == SocialFailureCodes.ReconnectNeeded)
        {
            // The account notice already reaches the person who connected it; do not mail them twice.
            Guid? told = await MarkReconnectNeededAsync(post, ct);
            notifyCreator = told != post.CreatedByUserId;
        }

        await ApplyRefusalAsync(post, claim, refusal, ct, notifyCreator);
    }

    private async Task<Guid?> MarkReconnectNeededAsync(SocialPost post, CancellationToken ct)
    {
        SocialAccount? account = await _db.SocialAccounts.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(a => a.Id == post.SocialAccountId, ct);
        if (account is null || account.Status == SocialAccountStatus.Revoked) return null;

        // The connection service may already have flagged it while refreshing; either way this is the first post to notice.
        int flagged = await _db.SocialAccounts.IgnoreQueryFilters()
            .Where(a => a.Id == account.Id && a.Status == SocialAccountStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, SocialAccountStatus.ReconnectNeeded)
                .SetProperty(a => a.LastError, "The platform no longer accepts this connection. Reconnect the account.")
                .SetProperty(a => a.UpdatedAt, Now()), ct);
        // Already flagged (the connection service told the person when its refresh failed): once, not per retry.
        if (flagged == 0) return account.ConnectedByUserId;
        string name = SocialPlatforms.DisplayName(account.Platform);
        await _notifier.SocialPublishingAsync(account.OrganizationId, null, account.ConnectedByUserId,
            $"Reconnect your {name} account", $"{name} stopped accepting the connection. Open Publishing and reconnect it so queued posts can go out.", ct);
        return account.ConnectedByUserId;
    }

    /// <summary>Retryable refusals go back to the queue with a backoff until the attempt limit; everything else ends the post.</summary>
    private async Task ApplyRefusalAsync(SocialPost post, SocialClaim claim, SocialRefusal refusal, CancellationToken ct, bool notifyCreator = true)
    {
        DateTime now = Now();
        if (refusal.Retryable && post.Attempts < Math.Max(1, _options.MaxAttempts))
        {
            TimeSpan backoff = TimeSpan.FromSeconds(Math.Max(1, _options.BackoffSeconds) * Math.Pow(4, Math.Max(0, post.Attempts - 1)));
            if (refusal.RetryAfter is { } asked && asked > backoff) backoff = asked;
            DateTime next = now + backoff;
            await _db.SocialPosts.IgnoreQueryFilters()
                .Where(p => p.Id == claim.PostId && p.Status == SocialPostStatus.Publishing && p.LeaseOwner == claim.Owner)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, SocialPostStatus.Scheduled)
                    .SetProperty(p => p.NextAttemptAt, next)
                    .SetProperty(p => p.FailureCode, refusal.Code)
                    .SetProperty(p => p.FailureMessage, Truncate(refusal.Message, 2000))
                    .SetProperty(p => p.LeaseOwner, (string?)null)
                    .SetProperty(p => p.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(p => p.UpdatedAt, now), CancellationToken.None);
            return;
        }

        string code = refusal.Retryable ? SocialFailureCodes.RetriesExhausted : refusal.Code;
        string message = refusal.Retryable
            ? $"{refusal.Message} It was tried {post.Attempts} times and then stopped."
            : refusal.Message;
        int rows = await _db.SocialPosts.IgnoreQueryFilters()
            .Where(p => p.Id == claim.PostId && p.Status == SocialPostStatus.Publishing && p.LeaseOwner == claim.Owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, SocialPostStatus.Failed)
                .SetProperty(p => p.FailureCode, code)
                .SetProperty(p => p.FailureMessage, Truncate(message, 2000))
                .SetProperty(p => p.LeaseOwner, (string?)null)
                .SetProperty(p => p.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(p => p.UpdatedAt, now), CancellationToken.None);
        if (rows > 0 && notifyCreator)
            await NotifyFailureAsync(post, $"Your post to {SocialPlatforms.DisplayName(post.Platform)} failed", message, CancellationToken.None);
    }

    private async Task MarkUnknownAsync(SocialPost post, SocialClaim claim, CancellationToken ct)
    {
        const string message = "We could not confirm whether this post was published. Check the channel before posting it again.";
        int rows = await _db.SocialPosts.IgnoreQueryFilters()
            .Where(p => p.Id == claim.PostId && p.Status == SocialPostStatus.Publishing && p.LeaseOwner == claim.Owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, SocialPostStatus.Unknown)
                .SetProperty(p => p.FailureCode, SocialFailureCodes.OutcomeUnknown)
                .SetProperty(p => p.FailureMessage, message)
                .SetProperty(p => p.LeaseOwner, (string?)null)
                .SetProperty(p => p.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(p => p.UpdatedAt, Now()), CancellationToken.None);
        if (rows > 0) await NotifyFailureAsync(post, "Check your post", message, CancellationToken.None);
    }

    private Task ReleaseAsync(SocialClaim claim, CancellationToken ct) =>
        _db.SocialPosts.IgnoreQueryFilters()
            .Where(p => p.Id == claim.PostId && p.Status == SocialPostStatus.Publishing && p.LeaseOwner == claim.Owner)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, SocialPostStatus.Scheduled)
                .SetProperty(p => p.LeaseOwner, (string?)null)
                .SetProperty(p => p.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(p => p.Attempts, p => p.Attempts - 1), ct);

    private Task NotifyFailureAsync(SocialPost post, string subject, string details, CancellationToken ct) =>
        _notifier.SocialPublishingAsync(post.OrganizationId, post.ProjectId, post.CreatedByUserId, subject, details, ct);

    // ------------------------------------------------------------------ lease renewal

    /// <summary>
    /// Pushes the lease out every third of its length while the upload runs, on its own context. When a renewal finds the
    /// row no longer ours, the publish is cancelled: someone else owns the post's fate now.
    /// </summary>
    private async Task RenewLeaseAsync(SocialClaim claim, CancellationTokenSource cancel, CancellationToken ct)
    {
        TimeSpan period = TimeSpan.FromTicks(Lease.Ticks / 3);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(period, _clock, ct);
                await using InferenceApiDbContext db = new(_dbOptions);
                DateTime now = Now();
                DateTime until = now + Lease;
                int rows = await db.SocialPosts.IgnoreQueryFilters()
                    .Where(p => p.Id == claim.PostId && p.Status == SocialPostStatus.Publishing && p.LeaseOwner == claim.Owner)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeaseExpiresAt, until), CancellationToken.None);
                if (rows == 0)
                {
                    await cancel.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The publish finished.
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static IReadOnlyList<string> ReadHashtags(SocialPost post)
    {
        if (string.IsNullOrWhiteSpace(post.HashtagsJson)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(post.HashtagsJson) ?? []; }
        catch (JsonException) { return []; }
    }

    internal static IReadOnlyDictionary<string, string> ReadOptions(SocialPost post)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(post.OptionsJson)) return map;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(post.OptionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                map[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? string.Empty,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => p.Value.ToString()
                };
            }
        }
        catch (JsonException)
        {
            // An unreadable options blob means no options.
        }

        return map;
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
