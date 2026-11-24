#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.SocialPublishing;

/// <summary>
/// The publish engine on a real relational schema: the status machine, the conditional claim, leases, the duplicate-publish
/// guard, retry classes, the missed-window rule, the caps and every fire-time re-check refusal. The platform is a fake; no
/// network is touched.
/// </summary>
public class SocialPublishEngineTests
{
    // ------------------------------------------------------------------ happy path

    [Fact]
    public async Task A_due_post_is_claimed_published_and_the_remote_id_is_stored()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();

        SocialClaim? claim = await f.RunAsync(post);

        claim.Should().NotBeNull();
        SocialPost after = await f.ReloadAsync(post.Id);
        after.Status.Should().Be(SocialPostStatus.Published);
        after.RemotePostId.Should().StartWith("remote-");
        after.RemotePostUrl.Should().Be("https://youtube.example/shorts/abc");
        after.PublishedAt.Should().NotBeNull();
        after.LeaseOwner.Should().BeNull();
        after.Attempts.Should().Be(1);
        f.Platform.PublishCalls.Should().Be(1);
        f.Platform.TokensSeen.Should().Equal("access-1");
        f.Platform.BytesStreamed.Should().Be(2048, "the video streams from the object store through the request");
        SocialPublishRequest request = f.Platform.Requests.Single();
        request.Title.Should().Be("A title");
        request.Hashtags.Should().Equal("#one");
        request.Options.Should().Contain("aiGenerated", "true").And.Contain("privacy", "public");
        f.Notices.Should().ContainSingle(n => n.Subject.StartsWith("Published to") && n.User == f.UserId && n.Project == f.ProjectId);
    }

    [Fact]
    public async Task A_post_that_is_not_due_yet_is_left_alone()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost(dueIn: TimeSpan.FromMinutes(10));

        SocialSweepResult sweep = await f.NewEngine().ClaimDueAsync(CancellationToken.None);

        sweep.Claims.Should().BeEmpty();
        (await f.ReloadAsync(post.Id)).Status.Should().Be(SocialPostStatus.Scheduled);
        f.Clock.Advance(TimeSpan.FromMinutes(11));
        (await f.NewEngine().ClaimDueAsync(CancellationToken.None)).Claims.Should().ContainSingle();
    }

    [Fact]
    public async Task Only_scheduled_posts_are_ever_claimed()
    {
        using EngineFixture f = new();
        foreach (string status in new[] { SocialPostStatus.Draft, SocialPostStatus.Cancelled, SocialPostStatus.Published, SocialPostStatus.Failed, SocialPostStatus.Unknown })
        {
            SocialPost post = f.AddPost(status);
            (await f.NewEngine().TryClaimAsync(post.Id, CancellationToken.None)).Should().BeNull(status);
        }

        f.Platform.PublishCalls.Should().Be(0);
    }

    // ------------------------------------------------------------------ double claim and leases

    [Fact]
    public async Task Concurrent_replicas_claim_a_post_exactly_once()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();

        SocialClaim?[] results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { return await f.NewEngine().TryClaimAsync(post.Id, CancellationToken.None); }
                catch (Microsoft.Data.Sqlite.SqliteException) { await Task.Delay(10); } // shared-cache table lock in the test database only
            }

            return null;
        })));

        results.Count(c => c is not null).Should().Be(1, "the conditional UPDATE lets exactly one replica win");
        SocialPost after = await f.ReloadAsync(post.Id);
        after.Status.Should().Be(SocialPostStatus.Publishing);
        after.Attempts.Should().Be(1);
        after.LeaseOwner.Should().Be(results.Single(c => c is not null)!.Owner);
    }

    [Fact]
    public async Task A_stale_snapshot_cannot_double_claim_because_the_update_is_conditional()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        SocialClaim? first = await f.NewEngine().TryClaimAsync(post.Id, CancellationToken.None);
        first.Should().NotBeNull();

        // A replica that read the row before the first claim and now sends its UPDATE.
        await using var stale = f.NewDb();
        int rows = await stale.SocialPosts.Where(p => p.Id == post.Id && p.Status == SocialPostStatus.Scheduled && p.LeaseOwner == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeaseOwner, "intruder"));
        rows.Should().Be(0);
        (await f.ReloadAsync(post.Id)).LeaseOwner.Should().Be(first!.Owner);
    }

    [Fact]
    public async Task A_publish_for_a_lease_that_is_no_longer_ours_does_nothing()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        SocialClaim? claim = await f.NewEngine().TryClaimAsync(post.Id, CancellationToken.None);

        await f.NewEngine().PublishClaimedAsync(new SocialClaim(post.Id, "someone-else"), CancellationToken.None);

        f.Platform.PublishCalls.Should().Be(0);
        (await f.ReloadAsync(post.Id)).LeaseOwner.Should().Be(claim!.Owner);
    }

    [Fact]
    public async Task The_lease_is_renewed_while_the_video_uploads()
    {
        using EngineFixture f = new();
        f.Platform.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SocialPost post = f.AddPost();
        SocialPublishingService engine = f.NewEngine();
        SocialClaim claim = (await engine.TryClaimAsync(post.Id, CancellationToken.None))!;
        DateTime first = (await f.ReloadAsync(post.Id)).LeaseExpiresAt!.Value;

        Task publish = f.NewEngine().PublishClaimedAsync(claim, CancellationToken.None);
        await f.Platform.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Clock.Advance(TimeSpan.FromMinutes(2)); // far beyond the original lease

        DateTime renewed = first;
        for (int i = 0; i < 80 && renewed <= first; i++)
        {
            await Task.Delay(100);
            renewed = (await f.ReloadAsync(post.Id)).LeaseExpiresAt!.Value;
        }

        renewed.Should().BeAfter(first, "the lease is pushed out while the upload runs");
        f.Platform.Gate.SetResult();
        await publish.WaitAsync(TimeSpan.FromSeconds(10));
        (await f.ReloadAsync(post.Id)).Status.Should().Be(SocialPostStatus.Published);
    }

    [Fact]
    public async Task A_crash_mid_publish_becomes_unknown_and_is_never_retried()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        SocialClaim? claim = await f.NewEngine().TryClaimAsync(post.Id, CancellationToken.None);
        claim.Should().NotBeNull(); // ... and the process dies here: nobody publishes, nobody renews.

        f.Clock.Advance(TimeSpan.FromMinutes(3));
        SocialSweepResult sweep = await f.NewEngine().ClaimDueAsync(CancellationToken.None);

        sweep.MarkedUnknown.Should().Be(1);
        sweep.Claims.Should().BeEmpty();
        SocialPost after = await f.ReloadAsync(post.Id);
        after.Status.Should().Be(SocialPostStatus.Unknown);
        after.FailureCode.Should().Be(SocialFailureCodes.OutcomeUnknown);
        after.LeaseOwner.Should().BeNull();
        f.Notices.Should().ContainSingle(n => n.Subject == "Check your post");

        for (int i = 0; i < 3; i++)
        {
            f.Clock.Advance(TimeSpan.FromHours(1));
            (await f.NewEngine().ClaimDueAsync(CancellationToken.None)).Claims.Should().BeEmpty();
        }

        f.Platform.PublishCalls.Should().Be(0);
        (await f.ReloadAsync(post.Id)).Status.Should().Be(SocialPostStatus.Unknown);
    }

    [Fact]
    public async Task A_live_lease_is_not_taken_over_by_the_recovery_pass()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        await f.NewEngine().TryClaimAsync(post.Id, CancellationToken.None);

        SocialSweepResult sweep = await f.NewEngine().ClaimDueAsync(CancellationToken.None);

        sweep.MarkedUnknown.Should().Be(0);
        (await f.ReloadAsync(post.Id)).Status.Should().Be(SocialPostStatus.Publishing);
    }

    // ------------------------------------------------------------------ duplicate-publish guard

    [Fact]
    public async Task A_failure_after_the_platform_may_have_posted_is_unknown_and_never_retried()
    {
        using EngineFixture f = new();
        f.Platform.PublishFailure = new SocialApiException("connection reset after the final request", SocialErrorKind.ServerError, postMayExist: true);
        SocialPost post = f.AddPost();

        await f.RunAsync(post);

        SocialPost after = await f.ReloadAsync(post.Id);
        after.Status.Should().Be(SocialPostStatus.Unknown);
        after.NextAttemptAt.Should().BeNull();
        for (int i = 0; i < 3; i++)
        {
            f.Clock.Advance(TimeSpan.FromHours(2));
            (await f.NewEngine().ClaimDueAsync(CancellationToken.None)).Claims.Should().BeEmpty();
        }

        f.Platform.PublishCalls.Should().Be(1, "a duplicate public post is worse than a missed one");
    }

    [Fact]
    public async Task An_unexpected_exception_after_the_platform_call_is_unknown_not_retryable()
    {
        using EngineFixture f = new();
        f.Platform.PublishFailure = new InvalidOperationException("boom");
        SocialPost post = f.AddPost();

        await f.RunAsync(post);

        (await f.ReloadAsync(post.Id)).Status.Should().Be(SocialPostStatus.Unknown);
        f.Platform.PublishCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_published_post_cannot_be_published_again()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        await f.RunAsync(post);
        (await f.RunAsync(post)).Should().BeNull();
        f.Clock.Advance(TimeSpan.FromDays(1));
        (await f.NewEngine().ClaimDueAsync(CancellationToken.None)).Claims.Should().BeEmpty();
        f.Platform.PublishCalls.Should().Be(1);
    }

    // ------------------------------------------------------------------ failure classes

    [Fact]
    public async Task A_retryable_failure_backs_off_and_stops_after_three_attempts()
    {
        using EngineFixture f = new();
        f.Platform.PublishFailure = new SocialApiException("slow down", SocialErrorKind.RateLimited, retryAfter: null);
        SocialPost post = f.AddPost();

        await f.RunAsync(post);
        SocialPost one = await f.ReloadAsync(post.Id);
        one.Status.Should().Be(SocialPostStatus.Scheduled);
        one.Attempts.Should().Be(1);
        one.FailureCode.Should().Be(SocialFailureCodes.RateLimited);
        one.NextAttemptAt.Should().Be(f.Clock.UtcNow.AddSeconds(60));
        (await f.NewEngine().ClaimDueAsync(CancellationToken.None)).Claims.Should().BeEmpty("still backing off");

        f.Clock.Advance(TimeSpan.FromSeconds(61));
        await f.RunAsync(post);
        SocialPost two = await f.ReloadAsync(post.Id);
        two.Status.Should().Be(SocialPostStatus.Scheduled);
        two.Attempts.Should().Be(2);
        two.NextAttemptAt.Should().Be(f.Clock.UtcNow.AddSeconds(240), "4x the previous wait");

        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await f.RunAsync(post);
        SocialPost three = await f.ReloadAsync(post.Id);
        three.Status.Should().Be(SocialPostStatus.Failed);
        three.FailureCode.Should().Be(SocialFailureCodes.RetriesExhausted);
        three.Attempts.Should().Be(3);
        f.Platform.PublishCalls.Should().Be(3);
        f.Notices.Should().ContainSingle(n => n.Subject.Contains("failed"));
    }

    [Fact]
    public async Task The_platforms_retry_after_wins_when_it_is_longer_than_our_backoff()
    {
        using EngineFixture f = new();
        f.Platform.PublishFailure = new SocialApiException("slow down", SocialErrorKind.RateLimited, retryAfter: TimeSpan.FromMinutes(30));
        SocialPost post = f.AddPost();
        await f.RunAsync(post);
        (await f.ReloadAsync(post.Id)).NextAttemptAt.Should().Be(f.Clock.UtcNow.AddMinutes(30));
    }

    [Fact]
    public async Task A_permanent_refusal_fails_at_once_without_retrying_and_leaks_no_platform_text()
    {
        using EngineFixture f = new();
        f.Platform.PublishFailure = new SocialApiException("raw body with token secret-token-xyz", SocialErrorKind.Forbidden);
        SocialPost post = f.AddPost();

        await f.RunAsync(post);

        SocialPost after = await f.ReloadAsync(post.Id);
        after.Status.Should().Be(SocialPostStatus.Failed);
        after.FailureCode.Should().Be(SocialFailureCodes.PlatformRefused);
        after.FailureMessage.Should().NotContain("secret-token-xyz");
        after.Attempts.Should().Be(1);
        f.Clock.Advance(TimeSpan.FromHours(1));
        (await f.NewEngine().ClaimDueAsync(CancellationToken.None)).Claims.Should().BeEmpty();
        f.Platform.PublishCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_network_error_before_the_post_exists_is_retried()
    {
        using EngineFixture f = new();
        f.Platform.PublishFailure = new System.Net.Http.HttpRequestException("dns");
        SocialPost post = f.AddPost();
        await f.RunAsync(post);
        (await f.ReloadAsync(post.Id)).Status.Should().Be(SocialPostStatus.Scheduled);
    }

    [Fact]
    public async Task A_refused_grant_flags_the_account_notifies_once_and_fails_the_post()
    {
        using EngineFixture f = new();
        f.Platform.PublishFailure = new SocialApiException("401", SocialErrorKind.Unauthorized);
        SocialPost post = f.AddPost();

        await f.RunAsync(post);

        (await f.ReloadAsync(post.Id)).FailureCode.Should().Be(SocialFailureCodes.ReconnectNeeded);
        (await f.NewDb().SocialAccounts.AsNoTracking().SingleAsync(a => a.Id == f.Account.Id)).Status.Should().Be(SocialAccountStatus.ReconnectNeeded);
        f.Notices.Should().ContainSingle(n => n.Subject.StartsWith("Reconnect") && n.Project == null);
    }

    // ------------------------------------------------------------------ missed window

    [Fact]
    public async Task A_post_more_than_an_hour_late_is_held_as_missed_not_posted()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost(dueIn: -TimeSpan.FromMinutes(61));

        SocialSweepResult sweep = await f.NewEngine().ClaimDueAsync(CancellationToken.None);

        sweep.MissedWindow.Should().Be(1);
        sweep.Claims.Should().BeEmpty();
        SocialPost after = await f.ReloadAsync(post.Id);
        after.Status.Should().Be(SocialPostStatus.Failed);
        after.FailureCode.Should().Be(SocialFailureCodes.MissedWindow);
        f.Platform.PublishCalls.Should().Be(0);
        f.Notices.Should().ContainSingle();
    }

    [Fact]
    public async Task A_post_inside_the_window_still_goes_out_and_the_window_is_configurable()
    {
        using EngineFixture f = new();
        SocialPost late = f.AddPost(dueIn: -TimeSpan.FromMinutes(59));
        await f.RunAsync(late);
        (await f.ReloadAsync(late.Id)).Status.Should().Be(SocialPostStatus.Published);

        using EngineFixture strict = new(o => o.MissedWindowMinutes = 5);
        SocialPost tooLate = strict.AddPost(dueIn: -TimeSpan.FromMinutes(10));
        (await strict.NewEngine().ClaimDueAsync(CancellationToken.None)).MissedWindow.Should().Be(1);
        (await strict.ReloadAsync(tooLate.Id)).FailureCode.Should().Be(SocialFailureCodes.MissedWindow);
    }

    // ------------------------------------------------------------------ concurrency caps

    [Fact]
    public async Task One_account_uploads_one_post_at_a_time()
    {
        using EngineFixture f = new();
        SocialPost a = f.AddPost();
        SocialPost b = f.AddPost();

        SocialSweepResult sweep = await f.NewEngine().ClaimDueAsync(CancellationToken.None);

        sweep.Claims.Should().ContainSingle("per-account cap is 1");
        (await f.NewDb().SocialPosts.CountAsync(p => p.Status == SocialPostStatus.Scheduled)).Should().Be(1);
        _ = a; _ = b;
    }

    [Fact]
    public async Task A_platform_is_capped_across_accounts()
    {
        using EngineFixture f = new(o => { o.MaxConcurrentPerPlatform = 2; o.MaxConcurrentPerAccount = 1; });
        foreach (SocialAccount account in new[] { f.Account, f.AddAccount(), f.AddAccount() }) f.AddPost(account: account);

        SocialSweepResult sweep = await f.NewEngine().ClaimDueAsync(CancellationToken.None);

        sweep.Claims.Should().HaveCount(2, "the platform cap is 2");
    }

    // ------------------------------------------------------------------ budgets

    [Fact]
    public async Task The_organizations_monthly_cap_stops_further_posts()
    {
        using EngineFixture f = new(o => o.MonthlyPostCapPerOrganization = 2);
        f.AddLedger(f.OrganizationId, f.Clock.UtcNow, 2);
        SocialPost next = f.AddPost();

        await f.RunAsync(next);

        SocialPost after = await f.ReloadAsync(next.Id);
        after.Status.Should().Be(SocialPostStatus.Failed);
        after.FailureCode.Should().Be(SocialFailureCodes.MonthlyCapReached);
        f.Platform.PublishCalls.Should().Be(0);
        (await f.NewDb().SocialPublishLedger.CountAsync()).Should().Be(2, "a refused post gives its slot straight back");
    }

    [Fact]
    public async Task Deleting_posts_does_not_hand_back_the_monthly_cap()
    {
        using EngineFixture f = new(o => o.MonthlyPostCapPerOrganization = 1);
        SocialPost first = f.AddPost();
        await f.RunAsync(first);
        (await f.ReloadAsync(first.Id)).Status.Should().Be(SocialPostStatus.Published);

        // The project is deleted: its posts cascade away. The slot does not.
        await f.NewDb().SocialPosts.Where(p => p.Id == first.Id).ExecuteDeleteAsync();
        SocialPost next = f.AddPost();
        await f.RunAsync(next);

        (await f.ReloadAsync(next.Id)).FailureCode.Should().Be(SocialFailureCodes.MonthlyCapReached);
    }

    [Fact]
    public async Task A_retry_of_the_same_post_takes_no_second_slot()
    {
        using EngineFixture f = new(o => o.MonthlyPostCapPerOrganization = 1);
        SocialPost post = f.AddPost();
        await f.RunAsync(post);
        await f.NewDb().SocialPosts.Where(p => p.Id == post.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, SocialPostStatus.Scheduled));

        await f.RunAsync(post);

        (await f.NewDb().SocialPublishLedger.CountAsync()).Should().Be(1);
        (await f.ReloadAsync(post.Id)).Status.Should().Be(SocialPostStatus.Published);
    }

    [Fact]
    public async Task Failed_and_cancelled_posts_do_not_use_up_the_monthly_cap()
    {
        using EngineFixture f = new(o => o.MonthlyPostCapPerOrganization = 1);
        f.AddPost(SocialPostStatus.Failed); f.AddPost(SocialPostStatus.Cancelled);
        SocialPost next = f.AddPost();
        await f.RunAsync(next);
        (await f.ReloadAsync(next.Id)).Status.Should().Be(SocialPostStatus.Published);
    }

    [Fact]
    public async Task The_platform_daily_budget_is_shared_by_every_organization()
    {
        using EngineFixture f = new(o => o.DailyBudgetYouTube = 1);
        f.AddLedger(Guid.NewGuid(), f.Clock.UtcNow, 1);
        SocialPost mine = f.AddPost();

        await f.RunAsync(mine);

        SocialPost after = await f.ReloadAsync(mine.Id);
        after.FailureCode.Should().Be(SocialFailureCodes.DailyBudgetReached);
        after.Status.Should().Be(SocialPostStatus.Failed);
        f.Platform.PublishCalls.Should().Be(0);

        f.Clock.Advance(TimeSpan.FromDays(1));
        SocialPost tomorrow = f.AddPost();
        await f.RunAsync(tomorrow);
        (await f.ReloadAsync(tomorrow.Id)).Status.Should().Be(SocialPostStatus.Published);
    }

    [Fact]
    public async Task One_organization_cannot_use_more_than_its_share_of_the_platform_daily_budget()
    {
        using EngineFixture f = new(o => o.DailyBudgetYouTube = 6);
        f.Options.YouTube.MaxPerOrgPerDay = 2;
        f.AddLedger(f.OrganizationId, f.Clock.UtcNow, 2);
        SocialPost mine = f.AddPost();

        await f.RunAsync(mine);

        SocialPost after = await f.ReloadAsync(mine.Id);
        after.FailureCode.Should().Be(SocialFailureCodes.DailyBudgetReached);
        after.FailureMessage.Should().Contain("share", "the message names the organization's own share");
        f.Platform.PublishCalls.Should().Be(0);

        // Another organization still has room under the same global budget.
        Guid other = Guid.NewGuid();
        f.AddLedger(other, f.Clock.UtcNow, 1);
        (await f.NewDb().SocialPublishLedger.CountAsync(l => l.OrganizationId == f.OrganizationId)).Should().Be(2);
    }

    [Fact]
    public async Task The_per_organization_share_does_not_hold_back_the_organizations_other_days()
    {
        using EngineFixture f = new(o => o.DailyBudgetYouTube = 6);
        f.Options.YouTube.MaxPerOrgPerDay = 2;
        f.AddLedger(f.OrganizationId, f.Clock.UtcNow.AddDays(-1), 2);
        SocialPost mine = f.AddPost();
        await f.RunAsync(mine);
        (await f.ReloadAsync(mine.Id)).Status.Should().Be(SocialPostStatus.Published);
    }

    // ------------------------------------------------------------------ fire-time re-check refusals

    private static async Task<SocialPost> RefusedAsync(EngineFixture f, SocialPost post, string code)
    {
        await f.RunAsync(post);
        SocialPost after = await f.ReloadAsync(post.Id);
        after.Status.Should().Be(SocialPostStatus.Failed, after.FailureMessage);
        after.FailureCode.Should().Be(code);
        after.LeaseOwner.Should().BeNull();
        f.Platform.PublishCalls.Should().Be(0, "nothing is uploaded after a refused re-check");
        f.Notices.Should().NotBeEmpty();
        return after;
    }

    [Fact]
    public async Task Refuses_when_the_output_no_longer_exists()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        f.Outputs.Remove(f.OrganizationId, f.ProjectId, SocialOutputKinds.StepResult, f.OutputId);
        await RefusedAsync(f, post, SocialFailureCodes.OutputMissing);
    }

    [Fact]
    public async Task Refuses_when_the_project_was_deleted_after_scheduling()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        await f.NewDb().Projects.Where(p => p.Id == f.ProjectId).ExecuteDeleteAsync();
        await RefusedAsync(f, post, SocialFailureCodes.OutputMissing);
    }

    [Fact]
    public void Project_and_organization_deletion_cascade_to_queued_posts()
    {
        using EngineFixture f = new();
        var foreignKeys = f.Db.Model.FindEntityType(typeof(SocialPost))!.GetForeignKeys().ToList();
        foreignKeys.Should().Contain(k => k.PrincipalEntityType.ClrType == typeof(Project) && k.DeleteBehavior == DeleteBehavior.Cascade);
        foreignKeys.Should().Contain(k => k.PrincipalEntityType.ClrType == typeof(Organization) && k.DeleteBehavior == DeleteBehavior.Cascade);
        foreignKeys.Should().Contain(k => k.PrincipalEntityType.ClrType == typeof(SocialAccount) && k.DeleteBehavior == DeleteBehavior.Cascade);
    }

    [Fact]
    public async Task Refuses_when_the_person_who_scheduled_it_left_the_organization()
    {
        using EngineFixture f = new();
        Guid gone = Guid.NewGuid();
        SocialPost post = f.AddPost(createdBy: gone);
        await RefusedAsync(f, post, SocialFailureCodes.AccessLost);
    }

    [Fact]
    public async Task Refuses_when_the_person_who_scheduled_it_no_longer_has_edit_access()
    {
        using EngineFixture f = new();
        Guid billing = Guid.NewGuid();
        f.Db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = f.OrganizationId, UserId = billing, Role = OrganizationRole.Billing });
        await f.Db.SaveChangesAsync();
        SocialPost post = f.AddPost(createdBy: billing);
        await RefusedAsync(f, post, SocialFailureCodes.AccessLost);
    }

    [Fact]
    public async Task Refuses_when_the_account_belongs_to_another_organization()
    {
        using EngineFixture f = new();
        SocialAccount foreign = f.AddAccount(Guid.NewGuid());
        SocialPost post = f.AddPost(account: foreign);
        await RefusedAsync(f, post, SocialFailureCodes.AccountUnavailable);
    }

    [Fact]
    public async Task Refuses_when_the_account_needs_reconnecting()
    {
        using EngineFixture f = new();
        SocialAccount account = f.AddAccount(status: SocialAccountStatus.ReconnectNeeded);
        await RefusedAsync(f, f.AddPost(account: account), SocialFailureCodes.ReconnectNeeded);
    }

    [Fact]
    public async Task Refuses_when_the_account_was_disconnected()
    {
        using EngineFixture f = new();
        SocialAccount account = f.AddAccount(status: SocialAccountStatus.Revoked);
        await RefusedAsync(f, f.AddPost(account: account), SocialFailureCodes.AccountUnavailable);
    }

    [Fact]
    public async Task Refuses_when_the_plan_no_longer_includes_publishing()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        f.Entitlements.SocialAllowed = false;
        await RefusedAsync(f, post, SocialFailureCodes.PlanNotEntitled);
    }

    [Fact]
    public async Task Refuses_when_the_platform_is_not_configured()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        f.Platform.IsConfigured = false;
        await RefusedAsync(f, post, SocialFailureCodes.PlatformUnavailable);
    }

    [Fact]
    public async Task Refuses_an_output_under_a_csam_output_hold()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        Project project = await f.Db.Projects.SingleAsync();
        project.CsamOutputHoldAt = f.Clock.UtcNow;
        await f.Db.SaveChangesAsync();
        SocialPost after = await RefusedAsync(f, post, SocialFailureCodes.OutputUnavailable);
        after.FailureMessage.Should().Be("This output is not available.", "the wording is neutral on purpose");
    }

    [Fact]
    public async Task Refuses_an_output_that_predates_a_content_quarantine_marker()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        Project project = await f.Db.Projects.SingleAsync();
        project.ContentQuarantinedAt = f.Clock.UtcNow;
        await f.Db.SaveChangesAsync();
        await RefusedAsync(f, post, SocialFailureCodes.OutputUnavailable);
    }

    [Fact]
    public async Task Refuses_a_project_file_flagged_by_the_csam_scan()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        f.SetOutput(f.GoodOutput() with { IsProjectFile = true, FileFlagged = true });
        await RefusedAsync(f, post, SocialFailureCodes.OutputUnavailable);
    }

    [Fact]
    public async Task Moderation_fails_closed_a_block_is_final_and_a_pending_verdict_is_retried()
    {
        using EngineFixture f = new();
        f.Moderation.Verdict = new SocialModerationVerdict(false, "CONTENT_BLOCKED", "This output may violate the content policy.");
        SocialPost blocked = f.AddPost();
        await RefusedAsync(f, blocked, SocialFailureCodes.ModerationBlocked);

        using EngineFixture pending = new();
        pending.Moderation.Verdict = new SocialModerationVerdict(false, PublishModerationOutcome.PendingCode, "Still being checked.");
        SocialPost waiting = pending.AddPost();
        await pending.RunAsync(waiting);
        SocialPost after = await pending.ReloadAsync(waiting.Id);
        after.Status.Should().Be(SocialPostStatus.Scheduled);
        after.FailureCode.Should().Be(SocialFailureCodes.ModerationPending);
        after.NextAttemptAt.Should().NotBeNull();
        pending.Platform.PublishCalls.Should().Be(0);
    }

    [Fact]
    public async Task Refuses_a_video_that_no_longer_fits_the_platform_limits()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        f.SetOutput(f.GoodOutput() with { DurationSeconds = 600 });
        SocialPost after = await RefusedAsync(f, post, SocialFailureCodes.PreflightFailed);
        after.FailureMessage.Should().Contain("600");
    }

    [Fact]
    public async Task Refuses_text_that_is_too_long_for_the_platform()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost(title: new string('t', 101));
        await RefusedAsync(f, post, SocialFailureCodes.PreflightFailed);
    }

    // ------------------------------------------------------------------ shutdown

    [Fact]
    public async Task Shutting_down_before_the_platform_is_called_gives_the_post_back()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        SocialPublishingService engine = f.NewEngine();
        SocialClaim claim = (await engine.TryClaimAsync(post.Id, CancellationToken.None))!;
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await f.NewEngine().PublishClaimedAsync(claim, cts.Token);

        SocialPost after = await f.ReloadAsync(post.Id);
        after.Status.Should().Be(SocialPostStatus.Scheduled);
        after.Attempts.Should().Be(0);
        f.Platform.PublishCalls.Should().Be(0);
    }
}
