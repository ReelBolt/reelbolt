#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.SocialPublishing;

public class SocialMetricsScheduleTests
{
    private static readonly DateTime Published = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NothingIsDue_BeforeTheFirstHour_AndTheFirstReadingIsAtOneHour()
    {
        Assert.False(SocialMetricsSchedule.IsDue(Published, null, Published.AddMinutes(59)));
        Assert.Equal(Published.AddHours(1), SocialMetricsSchedule.NextReadingAt(Published, null, Published.AddMinutes(59)));
        Assert.True(SocialMetricsSchedule.IsDue(Published, null, Published.AddHours(1)));
    }

    [Fact]
    public void AfterAReading_TheNextOneWaitsForTheNextSlot()
    {
        DateTime last = Published.AddHours(1);
        Assert.False(SocialMetricsSchedule.IsDue(Published, last, Published.AddHours(5)));
        Assert.Equal(Published.AddHours(6), SocialMetricsSchedule.NextReadingAt(Published, last, Published.AddHours(5)));
        Assert.True(SocialMetricsSchedule.IsDue(Published, last, Published.AddHours(6)));
    }

    [Fact]
    public void ALongOutage_CatchesUpWithOneReading_NotSeven()
    {
        DateTime now = Published.AddDays(10);
        Assert.True(SocialMetricsSchedule.IsDue(Published, null, now));
        Assert.False(SocialMetricsSchedule.IsDue(Published, now, now.AddMinutes(1)));
        Assert.Equal(Published.AddDays(14), SocialMetricsSchedule.NextReadingAt(Published, now, now.AddMinutes(1)));
    }

    [Fact]
    public void StopsAfterThirtyDays_OnceTheFinalReadingIsTaken_OrAfterTheGrace()
    {
        Assert.Null(SocialMetricsSchedule.NextReadingAt(Published, Published.AddDays(30).AddHours(1), Published.AddDays(30).AddHours(2)));
        Assert.True(SocialMetricsSchedule.IsDue(Published, Published.AddDays(14), Published.AddDays(31)));
        Assert.Null(SocialMetricsSchedule.NextReadingAt(Published, Published.AddDays(14), Published.AddDays(33)));
        Assert.False(SocialMetricsSchedule.IsDue(Published, null, Published.AddDays(40)));
    }
}

public class SocialMetricsCollectorTests
{
    private static SocialMetricsSnapshot Numbers(string _) => new(100, 10, 3, null, null, "fake.stats");

    private static (EngineFixture Fx, SocialMetricsCollector Collector, SocialPost Post) Setup(
        Action<SocialPublishEngineOptions>? configure = null, Func<string, SocialMetricsSnapshot>? reader = null, TimeSpan? age = null)
    {
        EngineFixture fx = new(configure);
        fx.Options.YouTube.ClientId = "c";
        fx.Options.YouTube.ClientSecret = "s";
        fx.Platform.MetricsReader = reader ?? Numbers;
        SocialPost post = fx.AddPost(SocialPostStatus.Published);
        post.RemotePostId = "remote-1";
        post.PublishedAt = fx.Clock.UtcNow - (age ?? TimeSpan.FromHours(2));
        fx.Db.SaveChanges();
        return (fx, NewCollector(fx), post);
    }

    private static SocialMetricsCollector NewCollector(EngineFixture fx)
    {
        InferenceApiDbContextHolder db = new(fx.NewDb());
        SocialConnectionService connections = new(
            db.Db, fx.Registry, Options.Create(fx.Options), fx.Secrets, new SocialRefreshLock(), NullLogger<SocialConnectionService>.Instance, fx.Clock, fx.Notifier.Object);
        return new SocialMetricsCollector(db.Db, fx.Registry, connections, Options.Create(fx.Options), NullLogger<SocialMetricsCollector>.Instance, fx.Clock);
    }

    private sealed record InferenceApiDbContextHolder(ReelBolt.Inference.Api.Data.InferenceApiDbContext Db);

    private static async Task<SocialPostMetric[]> ReadingsAsync(EngineFixture fx, Guid postId) =>
        await fx.NewDb().SocialPostMetrics.AsNoTracking().Where(m => m.PostId == postId).OrderBy(m => m.CapturedAt).ToArrayAsync();

    [Fact]
    public async Task ADuePost_GetsAReading_AndIsNotReadAgainUntilTheNextSlot()
    {
        (EngineFixture fx, SocialMetricsCollector collector, SocialPost post) = Setup();
        using (fx)
        {
            Assert.Equal(1, await collector.CollectOnceAsync(CancellationToken.None));
            Assert.Equal(0, await NewCollector(fx).CollectOnceAsync(CancellationToken.None));

            SocialPostMetric[] rows = await ReadingsAsync(fx, post.Id);
            Assert.Single(rows);
            Assert.Equal(100, rows[0].Views);
            Assert.Equal(10, rows[0].Likes);
            Assert.Null(rows[0].Shares); // not provided: null, never 0
            Assert.Equal("fake.stats", rows[0].Source);
            Assert.Equal("remote-1", fx.Platform.MetricsCalls.Single().RemoteId);

            fx.Clock.Advance(TimeSpan.FromHours(5)); // 7 h after publishing: the 6 h slot is due
            Assert.Equal(1, await NewCollector(fx).CollectOnceAsync(CancellationToken.None));
            Assert.Equal(2, (await ReadingsAsync(fx, post.Id)).Length);
        }
    }

    [Fact]
    public async Task NotYetOneHour_AndNotPublished_AreLeftAlone()
    {
        (EngineFixture fx, SocialMetricsCollector collector, _) = Setup(age: TimeSpan.FromMinutes(30));
        using (fx)
        {
            SocialPost failed = fx.AddPost(SocialPostStatus.Failed);
            failed.RemotePostId = "x";
            failed.PublishedAt = fx.Clock.UtcNow - TimeSpan.FromDays(2);
            fx.Db.SaveChanges();

            Assert.Equal(0, await collector.CollectOnceAsync(CancellationToken.None));
            Assert.Empty(fx.Platform.MetricsCalls);
        }
    }

    [Fact]
    public async Task APlatformThatGivesNothing_StoresNulls_NotZeros()
    {
        (EngineFixture fx, SocialMetricsCollector collector, SocialPost post) = Setup(reader: _ => new SocialMetricsSnapshot(null, null, null, null, null, "linkedin.unavailable"));
        using (fx)
        {
            await collector.CollectOnceAsync(CancellationToken.None);

            SocialPostMetric row = Assert.Single(await ReadingsAsync(fx, post.Id));
            Assert.Null(row.Views);
            Assert.Null(row.Likes);
            Assert.Null(row.Comments);
        }
    }

    [Fact]
    public async Task RateLimited_StopsThatPlatformForThePass_AndStoresNothing()
    {
        (EngineFixture fx, SocialMetricsCollector collector, SocialPost post) = Setup();
        using (fx)
        {
            SocialPost second = fx.AddPost(SocialPostStatus.Published);
            second.RemotePostId = "remote-2";
            second.PublishedAt = fx.Clock.UtcNow - TimeSpan.FromHours(3);
            fx.Db.SaveChanges();
            fx.Platform.MetricsFailure = new SocialApiException("slow down", SocialErrorKind.RateLimited, retryAfter: TimeSpan.FromMinutes(5));

            Assert.Equal(0, await collector.CollectOnceAsync(CancellationToken.None));

            Assert.Single(fx.Platform.MetricsCalls); // the second post was not even tried
            Assert.Empty(await ReadingsAsync(fx, post.Id));

            fx.Platform.MetricsFailure = null; // next pass, limit over: both are read
            Assert.Equal(2, await NewCollector(fx).CollectOnceAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task APostThePlatformNoLongerKnows_IsRecordedAsNotAvailable_SoTheScheduleMovesOn()
    {
        (EngineFixture fx, SocialMetricsCollector collector, SocialPost post) = Setup();
        using (fx)
        {
            fx.Platform.MetricsFailure = new SocialApiException("gone", SocialErrorKind.NotFound);

            Assert.Equal(1, await collector.CollectOnceAsync(CancellationToken.None));

            SocialPostMetric row = Assert.Single(await ReadingsAsync(fx, post.Id));
            Assert.Null(row.Views);
            Assert.EndsWith(".unavailable", row.Source);
            Assert.Equal(0, await NewCollector(fx).CollectOnceAsync(CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(SocialAccountStatus.Revoked)]
    [InlineData(SocialAccountStatus.ReconnectNeeded)]
    public async Task AnAccountThatIsNotActive_IsNeverActedFor(string status)
    {
        (EngineFixture fx, SocialMetricsCollector collector, SocialPost post) = Setup();
        using (fx)
        {
            SocialAccount account = await fx.Db.SocialAccounts.SingleAsync(a => a.Id == post.SocialAccountId);
            account.Status = status;
            await fx.Db.SaveChangesAsync();

            Assert.Equal(0, await collector.CollectOnceAsync(CancellationToken.None));

            Assert.Empty(fx.Platform.MetricsCalls);
            Assert.Empty(await ReadingsAsync(fx, post.Id));
        }
    }

    [Fact]
    public async Task ARefusedRefreshDuringTheMetricsRead_FlagsTheAccountAndTellsThePersonOnce()
    {
        (EngineFixture fx, SocialMetricsCollector collector, SocialPost post) = Setup();
        using (fx)
        {
            SocialAccount account = await fx.Db.SocialAccounts.SingleAsync(a => a.Id == post.SocialAccountId);
            account.AccessTokenExpiresAt = fx.Clock.UtcNow.AddSeconds(-5);
            await fx.Db.SaveChangesAsync();
            fx.Platform.RefreshFailure = new SocialApiException("invalid_grant", SocialErrorKind.Unauthorized);

            Assert.Equal(0, await collector.CollectOnceAsync(CancellationToken.None));
            fx.Clock.Advance(TimeSpan.FromHours(5));
            Assert.Equal(0, await NewCollector(fx).CollectOnceAsync(CancellationToken.None));

            Assert.Equal(SocialAccountStatus.ReconnectNeeded, (await fx.NewDb().SocialAccounts.AsNoTracking().SingleAsync(a => a.Id == account.Id)).Status);
            (string Subject, Guid User) notice = Assert.Single(fx.Notices.Select(n => (n.Subject, n.User)));
            Assert.StartsWith("Reconnect", notice.Subject);
            Assert.Equal(account.ConnectedByUserId, notice.User);
        }
    }

    [Fact]
    public async Task ADeletedOrganization_IsNeverActedFor()
    {
        (EngineFixture fx, SocialMetricsCollector collector, SocialPost post) = Setup();
        using (fx)
        {
            Organization org = await fx.Db.Organizations.SingleAsync(o => o.Id == post.OrganizationId);
            org.DeletedAt = fx.Clock.UtcNow;
            await fx.Db.SaveChangesAsync();

            Assert.Equal(0, await collector.CollectOnceAsync(CancellationToken.None));

            Assert.Empty(fx.Platform.MetricsCalls);
            Assert.Empty(await ReadingsAsync(fx, post.Id));
        }
    }

    [Fact]
    public async Task AnAccountDisconnectedWhileTheReadWasInFlight_StoresNothing()
    {
        (EngineFixture fx, SocialMetricsCollector collector, SocialPost post) = Setup();
        using (fx)
        {
            fx.Platform.MetricsReader = _ =>
            {
                using var other = fx.NewDb();
                SocialAccount a = other.SocialAccounts.Single(x => x.Id == post.SocialAccountId);
                a.Status = SocialAccountStatus.Revoked;
                other.SaveChanges();
                return Numbers(_);
            };

            Assert.Equal(0, await collector.CollectOnceAsync(CancellationToken.None));
            Assert.Empty(await ReadingsAsync(fx, post.Id));
        }
    }

    [Fact]
    public async Task AfterThirtyDays_NothingIsRead()
    {
        (EngineFixture fx, SocialMetricsCollector collector, _) = Setup(age: TimeSpan.FromDays(40));
        using (fx)
        {
            Assert.Equal(0, await collector.CollectOnceAsync(CancellationToken.None));
            Assert.Empty(fx.Platform.MetricsCalls);
        }
    }

    [Fact]
    public async Task APlatformWithoutCredentials_IsNotCalled()
    {
        (EngineFixture fx, SocialMetricsCollector collector, _) = Setup();
        using (fx)
        {
            fx.Platform.IsConfigured = false;
            Assert.Equal(0, await collector.CollectOnceAsync(CancellationToken.None));
            Assert.Empty(fx.Platform.MetricsCalls);
        }
    }

    // ------------------------------------------------------------------ routes

    private static SocialMetricsController Controller(EngineFixture fx, Guid? organizationId = null, OrganizationRole? role = OrganizationRole.Owner) =>
        new(fx.NewDb(), new SocialFakeUser(fx.UserId, organizationId ?? fx.OrganizationId, role), fx.Clock);

    [Fact]
    public async Task PostMetrics_ReturnsReadingsOldestFirst_WithNullsPreserved_AndTheNextReadingTime()
    {
        (EngineFixture fx, SocialMetricsCollector collector, SocialPost post) = Setup(reader: _ => new SocialMetricsSnapshot(50, null, 2, null, null, "fake.stats"));
        using (fx)
        {
            await collector.CollectOnceAsync(CancellationToken.None);

            ActionResult<SocialPostMetricsResponse> answer = await Controller(fx).PostMetrics(post.Id, CancellationToken.None);

            SocialPostMetricsResponse body = Assert.IsType<OkObjectResult>(answer.Result).Value as SocialPostMetricsResponse ?? throw new Xunit.Sdk.XunitException("no body");
            Assert.Equal("youtube", body.Platform);
            SocialMetricReadingResponse reading = Assert.Single(body.Readings);
            Assert.Equal(50, reading.Views);
            Assert.Null(reading.Likes);
            Assert.Equal(2, reading.Comments);
            Assert.Equal(post.PublishedAt!.Value.AddHours(6), body.NextReadingAt);
        }
    }

    [Fact]
    public async Task PostMetrics_OfAnotherOrganization_IsTheSame404AsAMissingPost()
    {
        (EngineFixture fx, _, SocialPost post) = Setup();
        using (fx)
        {
            ActionResult<SocialPostMetricsResponse> other = await Controller(fx, organizationId: Guid.NewGuid()).PostMetrics(post.Id, CancellationToken.None);
            ActionResult<SocialPostMetricsResponse> missing = await Controller(fx).PostMetrics(Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(other.Result);
            Assert.IsType<NotFoundObjectResult>(missing.Result);
        }
    }

    [Fact]
    public async Task Summary_SumsTheNewestReadingOfEachPost_AndATotalNobodyProvidesIsNull()
    {
        (EngineFixture fx, SocialMetricsCollector collector, SocialPost post) = Setup();
        using (fx)
        {
            SocialPost second = fx.AddPost(SocialPostStatus.Published);
            second.RemotePostId = "remote-2";
            second.PublishedAt = fx.Clock.UtcNow - TimeSpan.FromHours(3);
            fx.Db.SaveChanges();
            await collector.CollectOnceAsync(CancellationToken.None);

            ActionResult<SocialMetricsSummaryResponse> answer = await Controller(fx).Summary(30, CancellationToken.None);

            SocialMetricsSummaryResponse body = (SocialMetricsSummaryResponse)Assert.IsType<OkObjectResult>(answer.Result).Value!;
            Assert.Equal(2, body.PublishedPosts);
            Assert.Equal(200, body.Totals.Views);
            Assert.Equal(20, body.Totals.Likes);
            Assert.Null(body.Totals.Shares);
            Assert.Null(body.Totals.Saves);
            Assert.Equal("youtube", Assert.Single(body.Platforms).Platform);
            Assert.Equal(2, body.Posts.Count);
        }
    }

    [Fact]
    public async Task Summary_IsForbiddenForBilling_AndEmptyWithoutAWorkspace()
    {
        (EngineFixture fx, _, _) = Setup();
        using (fx)
        {
            Assert.IsType<ForbidResult>((await Controller(fx, role: OrganizationRole.Billing).Summary(30, CancellationToken.None)).Result);
            SocialMetricsController none = new(fx.NewDb(), new SocialFakeUser(fx.UserId, null, null), fx.Clock);
            SocialMetricsSummaryResponse empty = (SocialMetricsSummaryResponse)Assert.IsType<OkObjectResult>((await none.Summary(30, CancellationToken.None)).Result).Value!;
            Assert.Equal(0, empty.PublishedPosts);
        }
    }
}

public class SocialLinkedInPrivacyPreflightTests
{
    private static SocialPreflightResult Check(SocialPlatformKind kind, string? privacy, params string[] tags) =>
        SocialPreflight.Check(new SocialPreflightInput(kind, "T", "B", tags, 1_000_000, 30, 1080, 1920, privacy));

    [Theory]
    [InlineData("unlisted")]
    [InlineData("private")]
    public void LinkedIn_RejectsPrivacyValuesItDoesNotHave_WithAPlainMessage(string privacy)
    {
        SocialPreflightResult result = Check(SocialPlatformKind.LinkedIn, privacy);
        Assert.False(result.Ok);
        SocialPreflightIssue issue = Assert.Single(result.Issues, i => i.Code == "privacy_unsupported");
        Assert.Contains("LinkedIn has no", issue.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("public")]
    [InlineData("connections")]
    public void LinkedIn_AcceptsMissingPublicAndConnections(string? privacy) =>
        Assert.True(Check(SocialPlatformKind.LinkedIn, privacy).Ok);

    [Fact]
    public void YouTube_StillTakesUnlistedAndPrivate()
    {
        Assert.True(Check(SocialPlatformKind.YouTube, "unlisted").Ok);
        Assert.True(Check(SocialPlatformKind.YouTube, "private").Ok);
    }

    [Fact]
    public void Hashtags_AreReducedToLettersDigitsAndUnderscore()
    {
        Assert.Equal("#ok_1", SocialPlatformLimits.NormalizeHashtag(" #ok_1 "));
        Assert.Equal("#xurnlipersoni9", SocialPlatformLimits.NormalizeHashtag("#x]{urn:li:person:i9}"));
        Assert.Equal(string.Empty, SocialPlatformLimits.NormalizeHashtag("#@(*)"));
        Assert.Equal("B\n\n#a", SocialPlatformLimits.ComposeBody("B", ["a(b", "@@"]).Replace("#ab", "#a"));
    }
}
