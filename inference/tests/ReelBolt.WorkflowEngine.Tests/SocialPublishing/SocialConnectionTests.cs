#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.SocialPublishing;

/// <summary>
/// Social account connection (L1 foundation): hashed single-use state, encrypted tokens, inert 503 without
/// credentials, tenancy 404, token never in a response or a log, refresh under a cross-replica guard.
/// </summary>
public class SocialConnectionTests
{
    private static SocialController Controller(SocialFixture f, OrganizationRole? role = OrganizationRole.Owner, Guid? organizationId = null, bool authenticated = true) =>
        new(f.Db, f.Registry, f.Connections, new SocialFakeUser(f.UserId, organizationId ?? f.OrganizationId, role, authenticated), NullLogger<SocialController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static int StatusOf(IActionResult? result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200
    };

    private static string? ErrorOf(IActionResult? result) =>
        result is ObjectResult { Value: { } v } ? v.GetType().GetProperty("error")?.GetValue(v) as string : null;

    private static async Task<(SocialFixture F, string State)> StartedAsync(SocialFixture? fixture = null)
    {
        SocialFixture f = fixture ?? new SocialFixture();
        SocialOAuthStart start = await f.Connections.StartAsync(SocialPlatformKind.YouTube, f.UserId, f.OrganizationId, "/app/publishing?tab=accounts", CancellationToken.None);
        start.Error.Should().BeNull();
        string state = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl!).Query)["state"]!;
        return (f, state);
    }

    [Fact]
    public async Task Start_stores_only_a_hash_of_the_state_and_an_encrypted_verifier()
    {
        (SocialFixture f, string state) = await StartedAsync();

        SocialOAuthState ticket = await f.Db.SocialOAuthStates.SingleAsync();
        ticket.StateHash.Should().NotBe(state).And.HaveLength(64);
        ticket.ProtectedCodeVerifier.Should().StartWith(ReelBolt.WorkflowEngine.Tests.GitHub.FakeSecretProtector.Prefix);
        ticket.ExpiresAt.Should().Be(f.Clock.UtcNow + TimeSpan.FromMinutes(10));
        ticket.ReturnPath.Should().Be("/app/publishing?tab=accounts");
        ticket.OrganizationId.Should().Be(f.OrganizationId);
    }

    [Fact]
    public async Task Inert_without_credentials_connect_answers_503_provider_not_configured_and_nothing_is_stored()
    {
        SocialFixture f = new(configured: false);

        ActionResult<SocialConnectResponse> result = await Controller(f).Connect("youtube", null, CancellationToken.None);

        StatusOf(result.Result).Should().Be(503);
        ErrorOf(result.Result).Should().Be("provider_not_configured");
        (await f.Db.SocialOAuthStates.CountAsync()).Should().Be(0);

        // Hidden: the provider list does not offer it.
        ((ObjectResult)Controller(f).Providers().Result!).Value.Should().BeAssignableTo<System.Collections.IEnumerable>()
            .Which.Cast<object>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_configured_platform_is_listed_and_a_missing_public_base_url_is_503()
    {
        SocialFixture f = new();
        var listed = (System.Collections.Generic.IReadOnlyList<SocialProviderResponse>)((ObjectResult)Controller(f).Providers().Result!).Value!;
        listed.Select(p => p.Provider).Should().Equal("youtube");

        SocialFixture noBase = new(publicBaseUrl: null);
        ActionResult<SocialConnectResponse> result = await Controller(noBase).Connect("youtube", null, CancellationToken.None);
        StatusOf(result.Result).Should().Be(503);
        ErrorOf(result.Result).Should().Be("public_base_url_missing");
    }

    [Fact]
    public async Task Only_owner_or_admin_can_connect_or_disconnect()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync();

        StatusOf((await Controller(f, OrganizationRole.Member).Connect("youtube", null, CancellationToken.None)).Result).Should().Be(403);
        StatusOf(await Controller(f, OrganizationRole.Member).Disconnect(account.Id, CancellationToken.None)).Should().Be(403);
        StatusOf((await Controller(f, OrganizationRole.Admin).Connect("youtube", null, CancellationToken.None)).Result).Should().Be(200);
    }

    [Fact]
    public async Task Callback_connects_with_encrypted_tokens_and_the_state_is_single_use()
    {
        (SocialFixture f, string state) = await StartedAsync();

        SocialOAuthCompletion done = await f.Connections.CompleteAsync(SocialPlatformKind.YouTube, state, "the-code", null, f.UserId, CancellationToken.None);

        done.Succeeded.Should().BeTrue();
        done.ReturnPath.Should().Be("/app/publishing?tab=accounts");
        SocialAccount account = await f.Db.SocialAccounts.SingleAsync();
        account.OrganizationId.Should().Be(f.OrganizationId);
        account.RemoteAccountId.Should().Be("chan-1");
        account.Status.Should().Be(SocialAccountStatus.Active);
        account.ProtectedAccessToken.Should().NotContain("issued-access").And.StartWith(ReelBolt.WorkflowEngine.Tests.GitHub.FakeSecretProtector.Prefix);
        account.ProtectedRefreshToken.Should().NotContain("issued-refresh");
        f.Secrets.TryUnprotect(account.ProtectedAccessToken, out string access).Should().BeTrue();
        access.Should().Be("issued-access");

        // A replay finds no live ticket and exchanges nothing.
        SocialOAuthCompletion replay = await f.Connections.CompleteAsync(SocialPlatformKind.YouTube, state, "other-code", null, f.UserId, CancellationToken.None);
        replay.Outcome.Should().Be(SocialOAuthOutcome.InvalidState);
        f.Platform.CodesExchanged.Should().Equal("the-code");
    }

    [Fact]
    public async Task An_expired_state_is_refused_and_spent()
    {
        (SocialFixture f, string state) = await StartedAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(11));

        SocialOAuthCompletion done = await f.Connections.CompleteAsync(SocialPlatformKind.YouTube, state, "code", null, f.UserId, CancellationToken.None);

        done.Outcome.Should().Be(SocialOAuthOutcome.ExpiredState);
        f.Platform.CodesExchanged.Should().BeEmpty();
        (await f.Db.SocialAccounts.CountAsync()).Should().Be(0);
        (await f.Db.SocialOAuthStates.SingleAsync()).ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_callback_by_somebody_else_exchanges_nothing_and_leaves_the_ticket_alone()
    {
        (SocialFixture f, string state) = await StartedAsync();

        (await f.Connections.CompleteAsync(SocialPlatformKind.YouTube, state, "code", null, Guid.NewGuid(), CancellationToken.None))
            .Outcome.Should().Be(SocialOAuthOutcome.SessionMismatch);
        (await f.Connections.CompleteAsync(SocialPlatformKind.YouTube, state, "code", null, null, CancellationToken.None))
            .Outcome.Should().Be(SocialOAuthOutcome.SessionMismatch);

        f.Platform.CodesExchanged.Should().BeEmpty();
        (await f.Db.SocialOAuthStates.SingleAsync()).ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_callback_redirects_to_our_stored_path_and_never_to_the_query_string()
    {
        (SocialFixture f, string state) = await StartedAsync();
        IActionResult result = await Controller(f).Callback("youtube", "the-code", state, null, CancellationToken.None);
        ((RedirectResult)result).Url.Should().Be("/app/publishing?tab=accounts&social=connected&provider=youtube");

        IActionResult bad = await Controller(f).Callback("youtube", "x", "unknown-state", null, CancellationToken.None);
        ((RedirectResult)bad).Url.Should().Be("/app/publishing?social=error&provider=youtube&reason=invalid_state");
    }

    [Fact]
    public async Task Responses_never_carry_a_token_and_logs_never_mention_one()
    {
        (SocialFixture f, string state) = await StartedAsync();
        await f.Connections.CompleteAsync(SocialPlatformKind.YouTube, state, "the-code", null, f.UserId, CancellationToken.None);
        SocialAccount account = await f.Db.SocialAccounts.SingleAsync();
        account.AccessTokenExpiresAt = f.Clock.UtcNow.AddSeconds(30);
        await f.Db.SaveChangesAsync();
        f.Platform.TokensToRefresh = new SocialTokenSet("rotated-access", "rotated-refresh", f.Clock.UtcNow.AddHours(1), null);
        await f.Connections.GetAccessTokenAsync(account, false, CancellationToken.None);
        await f.Connections.DisconnectAsync(account.Id, f.OrganizationId, f.UserId, CancellationToken.None);

        ActionResult<System.Collections.Generic.IReadOnlyList<SocialAccountResponse>> list = await Controller(f).Accounts(CancellationToken.None);
        string json = JsonSerializer.Serialize(((ObjectResult)list.Result!).Value);
        foreach (string secret in new[] { "issued-access", "issued-refresh", "rotated-access", "rotated-refresh", "the-code", state })
        {
            json.Should().NotContain(secret);
            string.Join("\n", f.Logs.Lines).Should().NotContain(secret);
        }
    }

    [Fact]
    public async Task Another_organizations_account_is_a_404_for_list_and_disconnect()
    {
        SocialFixture f = new();
        Guid otherOrg = TestTenancy.OrgOf(Guid.NewGuid());
        SocialAccount theirs = await f.AddAccountAsync(otherOrg);
        SocialAccount mine = await f.AddAccountAsync();

        StatusOf(await Controller(f).Disconnect(theirs.Id, CancellationToken.None)).Should().Be(404);
        ErrorOf(await Controller(f).Disconnect(Guid.NewGuid(), CancellationToken.None)).Should().Be("account_not_found");
        (await f.Db.SocialAccounts.FindAsync(theirs.Id))!.Status.Should().Be(SocialAccountStatus.Active);

        var ids = ((System.Collections.Generic.IReadOnlyList<SocialAccountResponse>)((ObjectResult)(await Controller(f).Accounts(CancellationToken.None)).Result!).Value!)
            .Select(a => a.Id);
        ids.Should().Equal(mine.Id);

        // The named Tenant filter hides the other organization's rows from a scoped context altogether.
        using IDisposable scope = f.Tenant.BeginOrganizationScope(f.OrganizationId);
        await using var scoped = f.NewContext();
        (await scoped.SocialAccounts.Select(a => a.Id).ToListAsync()).Should().Equal(mine.Id);
    }

    [Fact]
    public async Task Disconnect_revokes_deletes_tokens_and_metrics_and_cancels_unsent_posts()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync();
        SocialPost scheduled = NewPost(f, account, SocialPostStatus.Scheduled);
        SocialPost published = NewPost(f, account, SocialPostStatus.Published);
        f.Db.SocialPosts.AddRange(scheduled, published);
        f.Db.SocialPostMetrics.Add(new SocialPostMetric { Id = Guid.NewGuid(), PostId = published.Id, Views = 10, Source = "test" });
        await f.Db.SaveChangesAsync();

        (await f.Connections.DisconnectAsync(account.Id, f.OrganizationId, f.UserId, CancellationToken.None)).Should().BeTrue();

        f.Platform.Revoked.Should().ContainSingle().Which.AccessToken.Should().Be("access-1");
        SocialAccount after = await f.NewContext().SocialAccounts.SingleAsync();
        after.Status.Should().Be(SocialAccountStatus.Revoked);
        after.ProtectedAccessToken.Should().BeEmpty();
        after.ProtectedRefreshToken.Should().BeNull();
        (await f.NewContext().SocialPostMetrics.CountAsync()).Should().Be(0);
        SocialPost[] posts = await f.NewContext().SocialPosts.ToArrayAsync();
        posts.Single(p => p.Id == scheduled.Id).Status.Should().Be(SocialPostStatus.Cancelled);
        posts.Single(p => p.Id == published.Id).Status.Should().Be(SocialPostStatus.Published);
    }

    [Fact]
    public async Task A_refresh_in_flight_cannot_write_tokens_or_Active_over_a_disconnected_account()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync(expiresIn: TimeSpan.FromSeconds(30));
        f.Platform.RefreshDelay = TimeSpan.FromMilliseconds(300);
        f.Platform.TokensToRefresh = new SocialTokenSet("access-2", "refresh-2", f.Clock.UtcNow.AddHours(1), null);

        var dbB = f.NewContext();
        SocialConnectionService other = f.NewService(dbB);
        Task<string> refresh = Task.Run(() => f.Connections.GetAccessTokenAsync(account, false, CancellationToken.None));
        await Task.Delay(100);

        // The disconnect arrives while the refresh holds the account's guard: it waits for it, then revokes.
        Task<bool> disconnect = other.DisconnectAsync(account.Id, f.OrganizationId, f.UserId, CancellationToken.None);
        await Task.WhenAll(refresh, disconnect);

        SocialAccount stored = await f.NewContext().SocialAccounts.SingleAsync();
        stored.Status.Should().Be(SocialAccountStatus.Revoked);
        stored.ProtectedAccessToken.Should().BeEmpty();
        stored.ProtectedRefreshToken.Should().BeNull();
    }

    [Fact]
    public async Task A_refresh_that_starts_after_the_disconnect_sees_revoked_and_stores_nothing()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync(expiresIn: TimeSpan.FromSeconds(30));
        var staleDb = f.NewContext();
        SocialAccount stale = await staleDb.SocialAccounts.SingleAsync(a => a.Id == account.Id);
        await f.Connections.DisconnectAsync(account.Id, f.OrganizationId, f.UserId, CancellationToken.None);

        Func<Task> act = () => f.NewService(staleDb).GetAccessTokenAsync(stale, false, CancellationToken.None);

        await act.Should().ThrowAsync<SocialApiException>();
        f.Platform.RefreshTokensUsed.Should().BeEmpty();
        (await f.NewContext().SocialAccounts.SingleAsync()).Status.Should().Be(SocialAccountStatus.Revoked);
    }

    [Fact]
    public async Task Deleting_an_organization_revokes_its_accounts_deletes_tokens_and_metrics_and_cancels_queued_posts()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync();
        SocialPost scheduled = NewPost(f, account, SocialPostStatus.Scheduled);
        SocialPost published = NewPost(f, account, SocialPostStatus.Published);
        f.Db.SocialPosts.AddRange(scheduled, published);
        f.Db.SocialPostMetrics.Add(new SocialPostMetric { Id = Guid.NewGuid(), PostId = published.Id, Views = 10, Source = "test" });
        SocialAccount keep = await f.AddAccountAsync(organizationId: TestTenancy.OrgOf(Guid.NewGuid()));
        await f.Db.SaveChangesAsync();

        (await f.Connections.PurgeDeletedOrganizationsAsync(10, CancellationToken.None)).Should().Be(0, "a live organization keeps its accounts");

        (await f.Db.Organizations.FindAsync(f.OrganizationId))!.DeletedAt = DateTime.UtcNow;
        await f.Db.SaveChangesAsync();
        (await f.Connections.PurgeDeletedOrganizationsAsync(10, CancellationToken.None)).Should().Be(1);

        f.Platform.Revoked.Should().ContainSingle().Which.AccessToken.Should().Be("access-1");
        SocialAccount[] accounts = await f.NewContext().SocialAccounts.IgnoreQueryFilters().ToArrayAsync();
        SocialAccount gone = accounts.Single(a => a.Id == account.Id);
        gone.Status.Should().Be(SocialAccountStatus.Revoked);
        gone.ProtectedAccessToken.Should().BeEmpty();
        gone.ProtectedRefreshToken.Should().BeNull();
        accounts.Single(a => a.Id == keep.Id).ProtectedAccessToken.Should().NotBeEmpty();
        (await f.NewContext().SocialPostMetrics.CountAsync()).Should().Be(0);
        SocialPost[] posts = await f.NewContext().SocialPosts.IgnoreQueryFilters().ToArrayAsync();
        posts.Single(p => p.Id == scheduled.Id).Status.Should().Be(SocialPostStatus.Cancelled);
        posts.Single(p => p.Id == published.Id).Status.Should().Be(SocialPostStatus.Published);
        (await f.Connections.PurgeDeletedOrganizationsAsync(10, CancellationToken.None)).Should().Be(0, "nothing is left to do");
    }

    [Fact]
    public async Task Disconnect_deletes_the_tokens_even_when_the_platform_revoke_fails()
    {
        SocialFixture f = new();
        f.Platform.RevokeFails = true;
        SocialAccount account = await f.AddAccountAsync();

        (await f.Connections.DisconnectAsync(account.Id, f.OrganizationId, f.UserId, CancellationToken.None)).Should().BeTrue();

        (await f.NewContext().SocialAccounts.SingleAsync()).ProtectedAccessToken.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fresh_token_is_returned_without_touching_the_platform_or_the_lock()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync(expiresIn: TimeSpan.FromHours(1));

        (await f.Connections.GetAccessTokenAsync(account, false, CancellationToken.None)).Should().Be("access-1");

        f.Platform.RefreshTokensUsed.Should().BeEmpty();
        ((RecordingRefreshLock)f.Lock).Acquisitions.Should().Be(0);
    }

    [Fact]
    public async Task An_expiring_token_is_refreshed_under_the_lock_and_a_rotated_refresh_token_is_stored()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync(expiresIn: TimeSpan.FromSeconds(90));
        f.Platform.TokensToRefresh = new SocialTokenSet("access-2", "refresh-2", f.Clock.UtcNow.AddHours(1), null);

        (await f.Connections.GetAccessTokenAsync(account, false, CancellationToken.None)).Should().Be("access-2");

        f.Platform.RefreshTokensUsed.Should().Equal("refresh-1");
        ((RecordingRefreshLock)f.Lock).Acquisitions.Should().Be(1);
        SocialAccount stored = await f.NewContext().SocialAccounts.SingleAsync();
        f.Secrets.TryUnprotect(stored.ProtectedRefreshToken, out string rotated).Should().BeTrue();
        rotated.Should().Be("refresh-2");
    }

    [Fact]
    public async Task Two_replicas_refreshing_at_once_spend_the_refresh_token_only_once()
    {
        // Two services with their own contexts (two replicas) share the guard; the second one waits, reloads,
        // finds the fresh token the first stored and never calls the platform.
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync(expiresIn: TimeSpan.FromSeconds(30));
        f.Platform.RefreshDelay = TimeSpan.FromMilliseconds(150);
        f.Platform.TokensToRefresh = new SocialTokenSet("access-2", "refresh-2", f.Clock.UtcNow.AddHours(1), null);

        var dbB = f.NewContext();
        SocialConnectionService b = f.NewService(dbB);
        SocialAccount accountB = await dbB.SocialAccounts.SingleAsync(a => a.Id == account.Id);

        string[] tokens = await Task.WhenAll(
            f.Connections.GetAccessTokenAsync(account, false, CancellationToken.None),
            b.GetAccessTokenAsync(accountB, false, CancellationToken.None));

        tokens.Should().OnlyContain(t => t == "access-2");
        f.Platform.RefreshTokensUsed.Should().Equal("refresh-1");
        ((RecordingRefreshLock)f.Lock).Acquisitions.Should().Be(2);
    }

    [Fact]
    public async Task A_rejected_token_is_refreshed_once_and_the_call_retried_once()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync();
        f.Platform.TokensToRefresh = new SocialTokenSet("access-2", null, f.Clock.UtcNow.AddHours(1), null);
        int calls = 0;

        string result = await f.Connections.CallAsync(account, token =>
        {
            calls++;
            if (token == "access-1") throw new SocialApiException("401", SocialErrorKind.Unauthorized);
            return Task.FromResult(token);
        }, CancellationToken.None);

        result.Should().Be("access-2");
        calls.Should().Be(2);
        f.Platform.RefreshTokensUsed.Should().HaveCount(1);

        // A second refusal propagates.
        Func<Task> always = () => f.Connections.CallAsync<string>(account, _ => throw new SocialApiException("401", SocialErrorKind.Unauthorized), CancellationToken.None);
        await always.Should().ThrowAsync<SocialApiException>();
    }

    [Fact]
    public async Task A_refused_refresh_marks_the_account_reconnect_needed()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync(expiresIn: TimeSpan.FromSeconds(10));
        f.Platform.RefreshFailure = new SocialApiException("invalid_grant", SocialErrorKind.Unauthorized);

        Func<Task> act = () => f.Connections.GetAccessTokenAsync(account, false, CancellationToken.None);

        (await act.Should().ThrowAsync<SocialApiException>()).Which.Kind.Should().Be(SocialErrorKind.Unauthorized);
        SocialAccount stored = await f.NewContext().SocialAccounts.SingleAsync();
        stored.Status.Should().Be(SocialAccountStatus.ReconnectNeeded);
        stored.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_repeatedly_failing_refresh_sends_the_reconnect_notice_once_and_a_reconnect_rearms_it()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync(expiresIn: TimeSpan.FromSeconds(10));
        f.Platform.RefreshFailure = new SocialApiException("invalid_grant", SocialErrorKind.Unauthorized);

        // 1st failure: direct refresh. 2nd: the metrics read path (CallAsync). 3rd: the publish path (CallAsync, forced refresh).
        await FluentActions.Awaiting(() => f.Connections.GetAccessTokenAsync(account, false, CancellationToken.None)).Should().ThrowAsync<SocialApiException>();
        await FluentActions.Awaiting(() => f.Connections.CallAsync(account, t => Task.FromResult(t), CancellationToken.None)).Should().ThrowAsync<SocialApiException>();
        await FluentActions.Awaiting(() => f.Connections.GetAccessTokenAsync(account, true, CancellationToken.None)).Should().ThrowAsync<SocialApiException>();

        f.Notices.Should().ContainSingle("only the change into reconnect_needed tells the person").Which.Subject.Should().Contain("Reconnect");

        // A successful reconnect: active again.
        SocialAccount stored = await f.Db.SocialAccounts.SingleAsync();
        stored.Status = SocialAccountStatus.Active;
        stored.AccessTokenExpiresAt = f.Clock.UtcNow + TimeSpan.FromSeconds(10);
        await f.Db.SaveChangesAsync();

        await FluentActions.Awaiting(() => f.Connections.GetAccessTokenAsync(stored, false, CancellationToken.None)).Should().ThrowAsync<SocialApiException>();
        f.Notices.Should().HaveCount(2, "a new failure after a reconnect notifies again");
    }

    [Fact]
    public async Task The_sweeper_pass_purges_a_deleted_organizations_accounts_even_with_no_provider_configured_and_leaves_others()
    {
        SocialFixture f = new(configured: false);
        Guid otherOrg = Guid.NewGuid();
        await f.AddOrganization(otherOrg);
        SocialAccount mine = await f.AddAccountAsync();
        SocialAccount other = await f.AddAccountAsync(otherOrg);
        (await f.Db.Organizations.FindAsync(f.OrganizationId))!.DeletedAt = DateTime.UtcNow;
        await f.Db.SaveChangesAsync();

        ServiceCollection services = new();
        services.AddScoped<ISocialConnectionService>(_ => f.NewService(f.NewContext()));
        services.AddScoped(_ => new Mock<ISocialPublishingService>(MockBehavior.Strict).Object);
        SocialPublishSweeper sweeper = new(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(f.Options),
            NullLogger<SocialPublishSweeper>.Instance);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(0, "nothing is configured, so nothing is published (and the strict mock is never called)");

        InferenceApiDbContext db = f.NewContext();
        SocialAccount purged = await db.SocialAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == mine.Id);
        purged.Status.Should().Be(SocialAccountStatus.Revoked);
        purged.ProtectedAccessToken.Should().BeEmpty();
        purged.ProtectedRefreshToken.Should().BeNull();
        SocialAccount untouched = await db.SocialAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == other.Id);
        untouched.Status.Should().Be(SocialAccountStatus.Active);
        untouched.ProtectedAccessToken.Should().NotBeEmpty();
    }

    private static SocialPost NewPost(SocialFixture f, SocialAccount account, string status) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = account.OrganizationId,
        ProjectId = Guid.NewGuid(),
        OutputKind = "project_file",
        OutputId = Guid.NewGuid(),
        SocialAccountId = account.Id,
        Platform = account.Platform,
        Format = "short",
        Status = status,
        CreatedByUserId = f.UserId
    };

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Billing)]
    [InlineData(null)]
    public async Task A_person_demoted_or_removed_between_start_and_callback_stores_nothing(OrganizationRole? nowRole)
    {
        (SocialFixture f, string state) = await StartedAsync();
        await f.SetRole(f.OrganizationId, nowRole);

        SocialOAuthCompletion done = await f.Connections.CompleteAsync(SocialPlatformKind.YouTube, state, "the-code", null, f.UserId, CancellationToken.None);

        done.Outcome.Should().Be(SocialOAuthOutcome.InvalidState);
        (await f.Db.SocialAccounts.CountAsync()).Should().Be(0);
        f.Platform.CodesExchanged.Should().BeEmpty();
    }

    [Fact]
    public async Task A_person_who_started_in_one_org_and_is_only_admin_elsewhere_cannot_connect_into_the_first()
    {
        (SocialFixture f, string state) = await StartedAsync();
        Guid other = TestTenancy.OrgOf(Guid.NewGuid());
        await f.SetRole(f.OrganizationId, null);
        await f.SetRole(other, OrganizationRole.Owner);

        (await f.Connections.CompleteAsync(SocialPlatformKind.YouTube, state, "c", null, f.UserId, CancellationToken.None))
            .Outcome.Should().Be(SocialOAuthOutcome.InvalidState);
        (await f.Db.SocialAccounts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_demoted_person_cannot_disconnect()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync();
        await f.SetRole(f.OrganizationId, OrganizationRole.Member);

        (await f.Connections.DisconnectAsync(account.Id, f.OrganizationId, f.UserId, CancellationToken.None)).Should().BeFalse();

        f.Platform.Revoked.Should().BeEmpty();
        (await f.NewContext().SocialAccounts.SingleAsync()).Status.Should().Be(SocialAccountStatus.Active);
    }

    [Fact]
    public async Task An_expiring_token_of_a_deleted_organization_is_not_refreshed()
    {
        SocialFixture f = new();
        SocialAccount account = await f.AddAccountAsync(expiresIn: TimeSpan.FromSeconds(10));
        (await f.Db.Organizations.FindAsync(f.OrganizationId))!.DeletedAt = DateTime.UtcNow;
        await f.Db.SaveChangesAsync();

        Func<Task> act = () => f.Connections.GetAccessTokenAsync(account, false, CancellationToken.None);

        await act.Should().ThrowAsync<SocialApiException>();
        f.Platform.RefreshTokensUsed.Should().BeEmpty();
    }
}
