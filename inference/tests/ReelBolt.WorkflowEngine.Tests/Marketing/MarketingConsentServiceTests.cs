#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>saas-promotions T-004: the consent store's state machine, one event per change.</summary>
public sealed class MarketingConsentServiceTests : IDisposable
{
    private const string V1 = "2026-10-marketing-v1";
    private static readonly MarketingRequestEvidence Evidence = new("203.0.113.7", "TestAgent/1.0");
    private readonly MarketingHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<T> WithService<T>(Func<MarketingConsentService, Task<T>> act)
    {
        await using InferenceApiDbContext db = _h.NewDb();
        return await act(_h.NewService(db));
    }

    private MarketingConsent Row(string email)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsents.AsNoTracking().Single(c => c.Email == email);
    }

    private List<MarketingConsentEvent> Events()
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsentEvents.AsNoTracking().OrderBy(e => e.OccurredAt).ToList();
    }

    // --- Authenticated consent ----------------------------------------------------------------

    [Fact]
    public async Task Opt_in_records_the_version_timestamp_ip_and_one_event()
    {
        Guid user = _h.AddUser("Bob@Example.com", verifiedAt: null);

        MarketingConsentUpdate result = await WithService(s => s.SetAsync(
            user, " Bob@Example.com ", optIn: true, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None));

        result.StaleWording.Should().BeFalse();
        result.View.Status.Should().Be("optedIn");
        result.View.WordingVersion.Should().Be(V1);
        result.View.ConsentedAt.Should().Be(_h.Now);
        result.View.ConfirmedAt.Should().BeNull("the account's email is not verified yet");

        MarketingConsent row = Row("bob@example.com");
        row.Status.Should().Be(MarketingConsentStatus.OptedIn);
        row.UserId.Should().Be(user);
        row.Source.Should().Be(MarketingConsentSource.Settings);
        row.Ip.Should().Be("203.0.113.7");
        row.UserAgent.Should().Be("TestAgent/1.0");
        row.ConsentedAt.Should().Be(_h.Now);

        MarketingConsentEvent ev = Events().Should().ContainSingle().Subject;
        ev.Action.Should().Be(MarketingConsentAction.Granted);
        ev.WordingVersion.Should().Be(V1);
        ev.Ip.Should().Be("203.0.113.7");
        ev.ConsentId.Should().Be(row.Id);
        ev.OccurredAt.Should().Be(_h.Now);
    }

    [Fact]
    public async Task Opt_in_by_a_verified_account_is_confirmed_at_once()
    {
        Guid user = _h.AddUser("v@example.com", verifiedAt: _h.Now.AddDays(-3));

        MarketingConsentUpdate result = await WithService(s => s.SetAsync(
            user, "v@example.com", true, MarketingConsentSource.Checkout, V1, Evidence, CancellationToken.None));

        result.View.ConfirmedAt.Should().Be(_h.Now);
        Events().Should().ContainSingle();
    }

    [Theory]
    [InlineData("2025-01-marketing-v0")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Opt_in_with_a_stale_or_unknown_version_is_refused(string? version)
    {
        Guid user = _h.AddUser("s@example.com", null);

        MarketingConsentUpdate result = await WithService(s => s.SetAsync(
            user, "s@example.com", true, MarketingConsentSource.Settings, version, Evidence, CancellationToken.None));

        result.StaleWording.Should().BeTrue();
        result.CurrentWordingVersion.Should().Be(V1);
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.Should().BeEmpty();
        db.MarketingConsentEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Opt_out_withdraws_keeps_the_row_and_writes_one_event()
    {
        Guid user = _h.AddUser("o@example.com", null);
        await WithService(s => s.SetAsync(user, "o@example.com", true, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None));
        _h.Clock.Advance(TimeSpan.FromHours(1));

        MarketingConsentUpdate result = await WithService(s => s.SetAsync(
            user, "o@example.com", false, MarketingConsentSource.Settings, null, Evidence, CancellationToken.None));

        result.View.Status.Should().Be("withdrawn");
        result.View.WithdrawnAt.Should().Be(_h.Now);
        Row("o@example.com").Status.Should().Be(MarketingConsentStatus.Withdrawn);
        Events().Select(e => e.Action).Should().Equal(MarketingConsentAction.Granted, MarketingConsentAction.Withdrawn);
    }

    [Fact]
    public async Task Repeating_the_same_choice_writes_no_second_event()
    {
        Guid user = _h.AddUser("r@example.com", null);
        await WithService(s => s.SetAsync(user, "r@example.com", true, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None));
        await WithService(s => s.SetAsync(user, "r@example.com", true, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None));
        await WithService(s => s.SetAsync(user, "r@example.com", false, MarketingConsentSource.Settings, null, Evidence, CancellationToken.None));
        await WithService(s => s.SetAsync(user, "r@example.com", false, MarketingConsentSource.Settings, null, Evidence, CancellationToken.None));

        Events().Should().HaveCount(2);
    }

    [Fact]
    public async Task Get_reports_none_without_a_row()
    {
        MarketingConsentView view = await WithService(s => s.GetAsync("nobody@example.com", CancellationToken.None));
        view.Status.Should().Be("none");
        view.WordingVersion.Should().BeNull();
    }

    // --- Anonymous subscribe ------------------------------------------------------------------

    [Fact]
    public async Task Subscribe_creates_a_pending_row_and_mails_a_confirm_link()
    {
        MarketingSubscribeOutcome outcome = await WithService(s => s.SubscribeAsync(
            "New@Example.com", V1, MarketingConsentSource.SiteContact, Evidence, CancellationToken.None));

        outcome.Should().Be(MarketingSubscribeOutcome.Accepted);
        MarketingConsent row = Row("new@example.com");
        row.Status.Should().Be(MarketingConsentStatus.Pending);
        row.RequestedAt.Should().Be(_h.Now);
        row.LastConfirmationSentAt.Should().Be(_h.Now);
        row.Source.Should().Be(MarketingConsentSource.SiteContact);
        Events().Should().ContainSingle().Which.Action.Should().Be(MarketingConsentAction.Requested);

        AccountMail mail = _h.Outbox.Sent.Should().ContainSingle().Subject;
        mail.Template.Should().Be("marketing-confirm");
        mail.To.Should().Be("new@example.com");
        mail.Data.Link.Should().StartWith(MarketingHarness.BaseUrl + "/app/marketing/confirm?token=c1.k1.");
        mail.Data.Link.Should().NotContain("@").And.NotContain("new%40").And.NotContainEquivalentOf("example.com/");
        string token = mail.Data.Link!.Split("token=")[1];
        MarketingTokenResult verified = _h.Tokens.VerifyConfirm(Uri.UnescapeDataString(token), _h.Clock.GetUtcNow());
        verified.Status.Should().Be(MarketingTokenStatus.Valid);
        verified.ConsentId.Should().Be(row.Id);
        mail.Data.ExpiresIn.Should().Be("7 days");
    }

    [Fact]
    public async Task A_second_subscribe_within_the_ten_minute_cooldown_sends_no_second_mail()
    {
        // T-024 (C6) replaced round 2's one mail per 24 h with a 10-minute cooldown and 3 per day.
        await WithService(s => s.SubscribeAsync("twice@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));
        _h.Clock.Advance(TimeSpan.FromMinutes(9));
        await WithService(s => s.SubscribeAsync("twice@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));

        _h.Outbox.Sent.Should().HaveCount(1);

        _h.Clock.Advance(TimeSpan.FromMinutes(16));
        await WithService(s => s.SubscribeAsync("twice@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));
        _h.Outbox.Sent.Should().HaveCount(2);
        Events().Should().ContainSingle("re-sending a confirmation is not a state change");
    }

    [Fact]
    public async Task Subscribe_does_nothing_for_an_opted_in_email()
    {
        Guid user = _h.AddUser("in@example.com", _h.Now);
        await WithService(s => s.SetAsync(user, "in@example.com", true, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None));

        MarketingSubscribeOutcome outcome = await WithService(s => s.SubscribeAsync(
            "in@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));

        outcome.Should().Be(MarketingSubscribeOutcome.Accepted);
        _h.Outbox.Sent.Should().BeEmpty();
        Row("in@example.com").Status.Should().Be(MarketingConsentStatus.OptedIn);
        Events().Should().ContainSingle();
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    [InlineData("a@b@c.com")]
    public async Task Subscribe_rejects_a_malformed_email_without_touching_the_store(string email)
    {
        MarketingSubscribeOutcome outcome = await WithService(s => s.SubscribeAsync(
            email, V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));

        outcome.Should().Be(MarketingSubscribeOutcome.InvalidEmail);
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.Should().BeEmpty();
    }

    [Fact]
    public async Task Subscribe_with_a_stale_version_is_refused()
    {
        MarketingSubscribeOutcome outcome = await WithService(s => s.SubscribeAsync(
            "x@example.com", "old", MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));

        outcome.Should().Be(MarketingSubscribeOutcome.StaleWording);
    }

    [Fact]
    public async Task Without_a_public_base_url_the_subscribe_is_recorded_but_no_mail_is_queued()
    {
        _h.Options.PublicBaseUrl = null;

        MarketingSubscribeOutcome outcome = await WithService(s => s.SubscribeAsync(
            "nourl@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));

        outcome.Should().Be(MarketingSubscribeOutcome.Accepted);
        _h.Outbox.Sent.Should().BeEmpty();
        Row("nourl@example.com").LastConfirmationSentAt.Should().BeNull();
    }

    [Fact]
    public async Task The_public_base_url_falls_back_to_the_cloud_storage_origin()
    {
        _h.Options.PublicBaseUrl = null;
        _h.CloudStorage.PublicBaseUrl = "https://fallback.example.test/";

        await WithService(s => s.SubscribeAsync("fb@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));

        _h.Outbox.Sent.Should().ContainSingle().Which.Data.Link
            .Should().StartWith("https://fallback.example.test/app/marketing/confirm?token=");
    }

    // --- Confirm ------------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_sets_opted_in_and_confirmed_at_with_the_clicks_ip()
    {
        await WithService(s => s.SubscribeAsync("c@example.com", V1, MarketingConsentSource.SiteContact, Evidence, CancellationToken.None));
        MarketingConsent pending = Row("c@example.com");
        string token = _h.Tokens.CreateConfirmToken(pending.Id, _h.Clock.GetUtcNow().AddDays(7));
        _h.Clock.Advance(TimeSpan.FromMinutes(10));

        MarketingTokenStatus status = await WithService(s => s.ConfirmAsync(
            token, new MarketingRequestEvidence("198.51.100.9", "Clicker"), CancellationToken.None));

        status.Should().Be(MarketingTokenStatus.Valid);
        MarketingConsent row = Row("c@example.com");
        row.Status.Should().Be(MarketingConsentStatus.OptedIn);
        row.ConfirmedAt.Should().Be(_h.Now);
        row.ConsentedAt.Should().Be(_h.Now);
        row.Ip.Should().Be("198.51.100.9");
        row.UserAgent.Should().Be("Clicker");
        MarketingConsentEvent confirmed = Events().Last();
        confirmed.Action.Should().Be(MarketingConsentAction.Confirmed);
        confirmed.Ip.Should().Be("198.51.100.9");
    }

    [Fact]
    public async Task Confirm_rejects_invalid_and_expired_tokens()
    {
        await WithService(s => s.SubscribeAsync("e@example.com", V1, MarketingConsentSource.SiteContact, Evidence, CancellationToken.None));
        MarketingConsent pending = Row("e@example.com");
        string expired = _h.Tokens.CreateConfirmToken(pending.Id, _h.Clock.GetUtcNow().AddSeconds(-1));

        (await WithService(s => s.ConfirmAsync(expired, Evidence, CancellationToken.None))).Should().Be(MarketingTokenStatus.Expired);
        (await WithService(s => s.ConfirmAsync("garbage", Evidence, CancellationToken.None))).Should().Be(MarketingTokenStatus.Invalid);
        (await WithService(s => s.ConfirmAsync(
            _h.Tokens.CreateConfirmToken(Guid.NewGuid(), _h.Clock.GetUtcNow().AddDays(1)), Evidence, CancellationToken.None)))
            .Should().Be(MarketingTokenStatus.Invalid, "a deleted or unknown row cannot be confirmed");
        Row("e@example.com").Status.Should().Be(MarketingConsentStatus.Pending);
    }

    [Fact]
    public async Task A_confirmed_opt_in_clears_the_suppression_entry()
    {
        using (InferenceApiDbContext db = _h.NewDb())
        {
            db.MarketingSuppressions.Add(new MarketingSuppression
            {
                Id = Guid.NewGuid(),
                EmailHmac = _h.Suppression.Hash("back@example.com"),
                WithdrawnAt = _h.Now.AddDays(-100),
            });
            db.SaveChanges();
        }

        await WithService(s => s.SubscribeAsync("back@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));
        string token = _h.Tokens.CreateConfirmToken(Row("back@example.com").Id, _h.Clock.GetUtcNow().AddDays(7));
        await WithService(s => s.ConfirmAsync(token, Evidence, CancellationToken.None));

        using InferenceApiDbContext check = _h.NewDb();
        check.MarketingSuppressions.Should().BeEmpty();
    }

    // --- Unsubscribe --------------------------------------------------------------------------

    [Fact]
    public async Task Unsubscribe_withdraws_with_one_event_and_accepts_a_deleted_row()
    {
        Guid user = _h.AddUser("u@example.com", _h.Now);
        await WithService(s => s.SetAsync(user, "u@example.com", true, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None));
        string token = _h.Tokens.CreateUnsubscribeToken(Row("u@example.com").Id);

        (await WithService(s => s.UnsubscribeAsync(token, Evidence, CancellationToken.None))).Should().BeTrue();
        (await WithService(s => s.UnsubscribeAsync(token, Evidence, CancellationToken.None))).Should().BeTrue();

        MarketingConsent row = Row("u@example.com");
        row.Status.Should().Be(MarketingConsentStatus.Withdrawn);
        row.WithdrawnAt.Should().Be(_h.Now);
        Events().Select(e => e.Action).Should().Equal(MarketingConsentAction.Granted, MarketingConsentAction.Withdrawn);
        Events().Last().Source.Should().Be(MarketingConsentSource.Unsubscribe);

        (await WithService(s => s.UnsubscribeAsync(_h.Tokens.CreateUnsubscribeToken(Guid.NewGuid()), Evidence, CancellationToken.None)))
            .Should().BeTrue("a validly signed token for a deleted row still answers 200");
        (await WithService(s => s.UnsubscribeAsync("u1.k1.nope.nope", Evidence, CancellationToken.None))).Should().BeFalse();
    }

    // --- Sweeper ------------------------------------------------------------------------------

    [Fact]
    public async Task The_sweeper_deletes_a_31_day_old_pending_row_and_keeps_a_29_day_old_one()
    {
        await WithService(s => s.SubscribeAsync("old@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));
        _h.Clock.Advance(TimeSpan.FromDays(2));
        await WithService(s => s.SubscribeAsync("young@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));
        Guid user = _h.AddUser("kept@example.com", null);
        await WithService(s => s.SetAsync(user, "kept@example.com", true, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None));
        _h.Clock.Advance(TimeSpan.FromDays(29));

        ServiceCollection services = new();
        services.AddScoped(_ => _h.NewDb());
        using ServiceProvider provider = services.BuildServiceProvider();
        PendingConsentSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(_h.Options),
            NullLogger<PendingConsentSweeper>.Instance);

        int deleted = await sweeper.SweepOnceAsync(_h.Clock.GetUtcNow().UtcDateTime, CancellationToken.None);

        deleted.Should().Be(1);
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.Select(c => c.Email).ToList().Should().BeEquivalentTo("young@example.com", "kept@example.com");
        db.MarketingConsentEvents.Select(e => e.Email).ToList().Should().BeEquivalentTo("young@example.com", "kept@example.com");
    }

    // --- Account deletion ---------------------------------------------------------------------

    [Fact]
    public async Task Account_deletion_leaves_only_the_suppression_entry()
    {
        InMemorySecretStore secrets = new();
        MarketingSuppressionHasher hasher = new(secrets);
        string databaseName = Guid.NewGuid().ToString();
        ServiceCollection services = new();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddSingleton(Mock.Of<IAmazonS3>());
        services.AddSingleton<ITenantContext>(new TenantContext(new HttpContextAccessor()));
        services.AddScoped<IAccountBillingCanceller, AccountBillingCanceller>();
        services.AddSingleton(Mock.Of<IVectorIndexService>());
        services.AddSingleton(Mock.Of<IInferenceProviderResolver>());
        services.AddSingleton(Mock.Of<ISpeechSynthesisClientFactory>());
        services.AddSingleton<IMarketingSuppressionHasher>(hasher);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        using ServiceProvider provider = services.BuildServiceProvider();

        DateTime now = DateTime.UtcNow;
        Guid userId = Guid.NewGuid();
        Guid optedIn = Guid.NewGuid();
        Guid pendingOther = Guid.NewGuid();
        using (IServiceScope scope = provider.CreateScope())
        {
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.ApplicationUsers.Add(new ApplicationUser
            {
                Id = userId,
                Email = "Gone@Example.com",
                DisplayName = "Gone",
                DeletionRequestedAt = now.AddDays(-31),
            });
            db.MarketingConsents.Add(new MarketingConsent
            {
                Id = optedIn,
                Email = "gone@example.com",
                UserId = userId,
                Status = MarketingConsentStatus.OptedIn,
                WordingVersion = V1,
                Source = MarketingConsentSource.Signup,
                ConsentedAt = now.AddDays(-60),
                ConfirmedAt = now.AddDays(-60),
            });
            db.MarketingConsentEvents.AddRange(
                new MarketingConsentEvent { Id = Guid.NewGuid(), ConsentId = optedIn, Email = "gone@example.com", UserId = userId, Action = MarketingConsentAction.Granted, WordingVersion = V1 },
                new MarketingConsentEvent { Id = Guid.NewGuid(), ConsentId = optedIn, Email = "gone@example.com", UserId = userId, Action = MarketingConsentAction.Confirmed, WordingVersion = V1 });
            // Somebody else's row stays.
            db.MarketingConsents.Add(new MarketingConsent
            {
                Id = pendingOther,
                Email = "other@example.com",
                Status = MarketingConsentStatus.Pending,
                WordingVersion = V1,
                Source = MarketingConsentSource.SiteFooter,
            });
            await db.SaveChangesAsync();
        }

        AccountPurgeService purge = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AccountOptions { Purge = new AccountPurgeOptions { Enabled = true, GraceDays = 30 } }),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "b" }).Build(),
            NullLogger<AccountPurgeService>.Instance);

        AccountPurgeUserResult result = await purge.PurgeUserAsync(userId, now, CancellationToken.None);

        result.Purged.Should().BeTrue(string.Join("; ", result.Failures));
        using IServiceScope check = provider.CreateScope();
        InferenceApiDbContext after = check.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        after.MarketingConsents.Select(c => c.Id).ToList().Should().Equal(pendingOther);
        after.MarketingConsentEvents.Should().BeEmpty();
        MarketingSuppression suppression = after.MarketingSuppressions.Should().ContainSingle().Subject;
        suppression.EmailHmac.Should().Be(hasher.Hash("gone@example.com"));
        suppression.EmailHmac.Should().MatchRegex("^[0-9a-f]{64}$");
        suppression.WithdrawnAt.Should().Be(now);
    }

    [Fact]
    public async Task Account_deletion_deletes_a_pending_row_outright()
    {
        using InferenceApiDbContext db = _h.NewDb();
        Guid userId = _h.AddUser("p@example.com", null);
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = Guid.NewGuid(),
            Email = "p@example.com",
            UserId = userId,
            Status = MarketingConsentStatus.Pending,
            WordingVersion = V1,
        });
        await db.SaveChangesAsync();
        ApplicationUser user = db.ApplicationUsers.AsNoTracking().Single(u => u.Id == userId);

        await MarketingConsentErasure.StageAsync(db, user, _h.Suppression, _h.Now, CancellationToken.None);
        await db.SaveChangesAsync();

        db.MarketingConsents.Should().BeEmpty();
        db.MarketingSuppressions.Should().BeEmpty();
    }
}

/// <summary>saas-promotions T-004: the confirmation mail leaves through the relay, off the request path.</summary>
public sealed class MarketingMailOutboxTests
{
    private sealed class RecordingClient : IAccountMailClient
    {
        public TaskCompletionSource<AccountMail> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> SendAsync(AccountMail mail, CancellationToken ct)
        {
            Sent.TrySetResult(mail);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task A_queued_mail_is_sent_through_the_account_mail_client()
    {
        RecordingClient client = new();
        ServiceCollection services = new();
        services.AddSingleton<IAccountMailClient>(client);
        using ServiceProvider provider = services.BuildServiceProvider();
        using MarketingMailOutbox outbox = new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<MarketingMailOutbox>.Instance);
        await outbox.StartAsync(CancellationToken.None);

        AccountMail mail = new("q@example.com", MarketingConsentService.ConfirmTemplate, new AccountMailData { Link = "https://x.test/l" });
        outbox.Enqueue(mail, MarketingMailOrigin.Anonymous);

        (await client.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeSameAs(mail);
        await outbox.StopAsync(CancellationToken.None);
    }
}
