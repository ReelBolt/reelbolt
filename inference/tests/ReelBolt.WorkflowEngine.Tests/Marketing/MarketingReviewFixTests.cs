#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// saas-promotions T-011: regression tests for the security review of marketing consent
/// (S-02, S-05 outbox part, S-08, S-14, S-20, S-27).
/// </summary>
public sealed class MarketingReviewFixTests : IDisposable
{
    private const string V1 = "2026-10-marketing-v1";
    private static readonly MarketingRequestEvidence Evidence = new("203.0.113.7", "TestAgent/1.0");
    private readonly MarketingHarness _h = new();

    public void Dispose() => _h.Dispose();

    private sealed class FakeUser(Guid id, string email, bool verified) : ICurrentUser
    {
        public Guid UserId => id;
        public string Email => email;
        public bool IsAuthenticated => true;
        public bool EmailVerified => verified;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
        public bool IsPlatformAdmin => false;
    }

    private MarketingConsentController Controller(InferenceApiDbContext db, ICurrentUser user)
    {
        MarketingConsentController controller = new(_h.NewService(db), _h.Wording, user);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

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

    /// <summary>An anonymous subscriber's confirmed row, with no account behind it.</summary>
    private Guid SeedAnonymousOptIn(string email)
    {
        using InferenceApiDbContext db = _h.NewDb();
        Guid id = Guid.NewGuid();
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = id,
            Email = email,
            Status = MarketingConsentStatus.OptedIn,
            WordingVersion = V1,
            Source = MarketingConsentSource.SiteFooter,
            ConsentedAt = _h.Now.AddDays(-5),
            ConfirmedAt = _h.Now.AddDays(-5),
            Ip = "198.51.100.1",
        });
        db.SaveChanges();
        return id;
    }

    // --- S-02: an unverified account cannot reach the consent of the address it claims ---------

    [Fact]
    public async Task S02_an_unverified_account_cannot_read_the_consent_registered_under_its_email()
    {
        SeedAnonymousOptIn("victim@example.com");
        Guid mallory = _h.AddUser("victim@example.com", verifiedAt: null);
        await using InferenceApiDbContext db = _h.NewDb();

        IActionResult result = await Controller(db, new FakeUser(mallory, "victim@example.com", verified: false)).GetConsent(CancellationToken.None);

        ObjectResult refused = result.Should().BeAssignableTo<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        refused.Value.Should().NotBeOfType<MarketingConsentView>();
    }

    [Fact]
    public async Task S02_an_unverified_account_cannot_withdraw_the_consent_registered_under_its_email()
    {
        SeedAnonymousOptIn("victim2@example.com");
        Guid mallory = _h.AddUser("victim2@example.com", verifiedAt: null);
        await using InferenceApiDbContext db = _h.NewDb();

        IActionResult result = await Controller(db, new FakeUser(mallory, "victim2@example.com", verified: false))
            .PutConsent(new MarketingConsentRequest(false, "settings", null), CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        MarketingConsent row = Row("victim2@example.com");
        row.Status.Should().Be(MarketingConsentStatus.OptedIn);
        row.UserId.Should().BeNull("the row is never rebound to an unverified account");
        Events().Should().BeEmpty();
    }

    [Fact]
    public async Task S02_a_verified_account_still_reads_and_changes_its_consent()
    {
        Guid user = _h.AddUser("me@example.com", _h.Now);
        await using InferenceApiDbContext db = _h.NewDb();
        FakeUser me = new(user, "me@example.com", verified: true);

        (await Controller(db, me).PutConsent(new MarketingConsentRequest(true, "settings", V1), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await Controller(_h.NewDb(), me).GetConsent(CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<MarketingConsentView>().Which.Status.Should().Be("optedIn");
    }

    [Fact]
    public async Task S02_purging_an_unverified_account_keeps_another_persons_row_under_the_same_email()
    {
        Guid victimRow = SeedAnonymousOptIn("victim3@example.com");
        Guid mallory = _h.AddUser("victim3@example.com", verifiedAt: null);
        await using InferenceApiDbContext db = _h.NewDb();
        ApplicationUser user = db.ApplicationUsers.AsNoTracking().Single(u => u.Id == mallory);

        await MarketingConsentErasure.StageAsync(db, user, _h.Suppression, _h.Now, CancellationToken.None);
        await db.SaveChangesAsync();

        await using InferenceApiDbContext check = _h.NewDb();
        check.MarketingConsents.Select(c => c.Id).ToList().Should().Equal(victimRow);
        check.MarketingSuppressions.Should().BeEmpty();
    }

    [Fact]
    public async Task S02_purging_a_verified_account_still_erases_the_row_under_its_email()
    {
        SeedAnonymousOptIn("owner@example.com");
        Guid owner = _h.AddUser("owner@example.com", _h.Now.AddDays(-1));
        await using InferenceApiDbContext db = _h.NewDb();
        ApplicationUser user = db.ApplicationUsers.AsNoTracking().Single(u => u.Id == owner);

        await MarketingConsentErasure.StageAsync(db, user, _h.Suppression, _h.Now, CancellationToken.None);
        await db.SaveChangesAsync();

        await using InferenceApiDbContext check = _h.NewDb();
        check.MarketingConsents.Should().BeEmpty();
        check.MarketingSuppressions.Should().ContainSingle();
    }

    // --- S-08: an anonymous subscribe never undoes a withdrawal -------------------------------

    private async Task<Guid> WithdrawnAccountRow(string email)
    {
        Guid user = _h.AddUser(email, null);
        await WithService(s => s.SetAsync(user, email, true, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None));
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await WithService(s => s.SetAsync(user, email, false, MarketingConsentSource.Settings, null, new("192.0.2.50", "Withdrawer"), CancellationToken.None));
        _h.Clock.Advance(TimeSpan.FromHours(1));
        return Row(email).Id;
    }

    [Fact]
    public async Task S08_subscribe_keeps_a_withdrawn_row_withdrawn_with_its_evidence_and_records_the_request()
    {
        await WithdrawnAccountRow("w@example.com");
        MarketingConsent before = Row("w@example.com");

        await WithService(s => s.SubscribeAsync("w@example.com", V1, MarketingConsentSource.SiteFooter, new("198.51.100.66", "Anon"), CancellationToken.None));

        MarketingConsent row = Row("w@example.com");
        row.Status.Should().Be(MarketingConsentStatus.Withdrawn);
        row.WithdrawnAt.Should().Be(before.WithdrawnAt);
        row.Ip.Should().Be("192.0.2.50", "the withdrawal's evidence stays on the row");
        row.UserAgent.Should().Be("Withdrawer");
        row.Source.Should().Be(MarketingConsentSource.Settings);
        row.ResubscribeRequestedAt.Should().Be(_h.Now);
        _h.Outbox.Sent.Should().ContainSingle("the address still gets a confirm link");
        Events().Select(e => e.Action).Should().Equal(
            MarketingConsentAction.Granted, MarketingConsentAction.Withdrawn, MarketingConsentAction.Requested);
    }

    [Fact]
    public async Task S08_the_sweeper_never_deletes_a_withdrawal_after_an_anonymous_subscribe()
    {
        await WithdrawnAccountRow("keep@example.com");
        await WithService(s => s.SubscribeAsync("keep@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));
        _h.Clock.Advance(TimeSpan.FromDays(40));

        ServiceCollection services = new();
        services.AddScoped(_ => _h.NewDb());
        using ServiceProvider provider = services.BuildServiceProvider();
        PendingConsentSweeper sweeper = new(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(_h.Options), NullLogger<PendingConsentSweeper>.Instance);

        (await sweeper.SweepOnceAsync(_h.Now, CancellationToken.None)).Should().Be(0);

        MarketingConsent row = Row("keep@example.com");
        row.Status.Should().Be(MarketingConsentStatus.Withdrawn);
        row.ResubscribeRequestedAt.Should().BeNull("a stale re-subscribe request is cleared, the withdrawal is kept");
        Events().Select(e => e.Action).Should().Contain(MarketingConsentAction.Withdrawn);
    }

    [Fact]
    public async Task S08_the_confirm_link_from_the_resubscribe_opts_the_address_back_in()
    {
        await WithdrawnAccountRow("back@example.com");
        await WithService(s => s.SubscribeAsync("back@example.com", V1, MarketingConsentSource.SiteContact, Evidence, CancellationToken.None));
        string token = Uri.UnescapeDataString(_h.Outbox.Sent.Single().Data.Link!.Split("token=")[1]);
        _h.Clock.Advance(TimeSpan.FromMinutes(5));

        (await WithService(s => s.ConfirmAsync(token, new("198.51.100.9", "Clicker"), CancellationToken.None)))
            .Should().Be(MarketingTokenStatus.Valid);

        MarketingConsent row = Row("back@example.com");
        row.Status.Should().Be(MarketingConsentStatus.OptedIn);
        row.ConfirmedAt.Should().Be(_h.Now);
        row.Source.Should().Be(MarketingConsentSource.SiteContact);
        row.WordingVersion.Should().Be(V1);
        row.ResubscribeRequestedAt.Should().BeNull();
        Events().Select(e => e.Action).Should().Equal(
            MarketingConsentAction.Granted, MarketingConsentAction.Withdrawn, MarketingConsentAction.Requested, MarketingConsentAction.Confirmed);
    }

    [Fact]
    public async Task S08_a_withdrawn_row_without_a_resubscribe_request_cannot_be_confirmed()
    {
        Guid id = await WithdrawnAccountRow("old@example.com");
        string token = _h.Tokens.CreateConfirmToken(id, _h.Clock.GetUtcNow().AddDays(7));

        (await WithService(s => s.ConfirmAsync(token, Evidence, CancellationToken.None))).Should().Be(MarketingTokenStatus.Invalid);
        Row("old@example.com").Status.Should().Be(MarketingConsentStatus.Withdrawn);
    }

    // --- S-14: the 24 h confirmation cap holds under concurrency ------------------------------

    /// <summary>
    /// Just before this request's first write to <c>marketing_consents</c>, a concurrent subscribe for
    /// the same address wins: it stamps <c>last_confirmation_sent_at</c> (and sent its own mail).
    /// </summary>
    private sealed class ConcurrentSenderWins(DateTime sentAt) : DbCommandInterceptor
    {
        private bool _fired;

        private void Race(DbCommand command)
        {
            if (_fired || !command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("marketing_consents", StringComparison.Ordinal))
            {
                return;
            }

            _fired = true;
            using DbCommand racer = command.Connection!.CreateCommand();
            racer.Transaction = command.Transaction;
            racer.CommandText = "UPDATE marketing_consents SET last_confirmation_sent_at = $at";
            DbParameter at = racer.CreateParameter();
            at.ParameterName = "$at";
            at.Value = sentAt;
            racer.Parameters.Add(at);
            racer.ExecuteNonQuery();
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Race(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Race(command);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task S14_a_concurrent_subscribe_that_already_sent_the_mail_suppresses_a_second_one()
    {
        await WithService(s => s.SubscribeAsync("race@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));
        _h.Outbox.Sent.Clear();
        _h.Clock.Advance(TimeSpan.FromHours(25));

        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(_h.NewDb().Database.GetDbConnection())
            .AddInterceptors(new ConcurrentSenderWins(_h.Now))
            .Options;
        await using (InferenceApiDbContext racing = new(options))
        {
            MarketingSubscribeOutcome outcome = await _h.NewService(racing).SubscribeAsync(
                "race@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None);
            outcome.Should().Be(MarketingSubscribeOutcome.Accepted);
        }

        _h.Outbox.Sent.Should().BeEmpty("the other request claimed this window's confirmation mail");
        // No assertion on the row: the racer's UPDATE runs inside this request's transaction here (one
        // Sqlite connection), so the rollback of the refused claim (T-024: a refused send claims
        // nothing) takes it along; on PostgreSQL the other request commits on its own connection.
    }

    // --- S-05: the outbox sheds anonymous mail before account mail ----------------------------

    private sealed class OrderedClient : IAccountMailClient
    {
        public List<AccountMail> Sent { get; } = [];
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Expected { get; set; }

        public Task<bool> SendAsync(AccountMail mail, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(mail);
                if (Sent.Count >= Expected) Done.TrySetResult();
            }

            return Task.FromResult(true);
        }
    }

    private static AccountMail Mail(string to) => new(to, MarketingConsentService.ConfirmTemplate, new AccountMailData { Link = "https://x.test/l" });

    [Fact]
    public async Task S05_a_flood_of_anonymous_mail_never_crowds_out_an_account_mail()
    {
        OrderedClient client = new();
        ServiceCollection services = new();
        services.AddSingleton<IAccountMailClient>(client);
        using ServiceProvider provider = services.BuildServiceProvider();
        using MarketingMailOutbox outbox = new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<MarketingMailOutbox>.Instance);

        int accepted = 0;
        for (int i = 0; i < 5000; i++)
        {
            if (outbox.Enqueue(Mail($"anon{i}@example.com"), MarketingMailOrigin.Anonymous)) accepted++;
        }

        accepted.Should().Be(MarketingMailOutbox.AnonymousCapacity, "anonymous mail is shed once its own lane is full");
        outbox.Enqueue(Mail("member@example.com"), MarketingMailOrigin.Account).Should().BeTrue("account mail has its own lane");

        client.Expected = accepted + 1;
        await outbox.StartAsync(CancellationToken.None);
        await client.Done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await outbox.StopAsync(CancellationToken.None);

        client.Sent[0].To.Should().Be("member@example.com", "account mail leaves first");
    }

    [Fact]
    public async Task S05_subscribe_queues_its_mail_as_anonymous()
    {
        await WithService(s => s.SubscribeAsync("lane@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None));

        _h.Outbox.Origins.Should().Equal(MarketingMailOrigin.Anonymous);
    }

    // --- S-20: mail failures never log an anonymous subscriber's address ----------------------

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private sealed class FailingHandler(bool throwing) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throwing
                ? throw new HttpRequestException("relay down")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("s3cret", false)]
    [InlineData("s3cret", true)]
    public async Task S20_a_failed_marketing_confirm_mail_does_not_log_the_subscribers_address(string token, bool throwing)
    {
        CapturingLogger<GoAccountMailClient> logger = new();
        GoAccountMailClient client = new(
            new HttpClient(new FailingHandler(throwing)),
            Options.Create(new AccountOptions { InternalBaseUrl = "http://go-api:8080", InternalToken = token }),
            logger);

        (await client.SendAsync(Mail("secret.subscriber@example.com"), CancellationToken.None)).Should().BeFalse();

        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().AllSatisfy(l => l.Should().NotContain("secret.subscriber"));
    }

    // --- S-27: CSV formula escaping looks past leading whitespace and quotes -------------------

    [Theory]
    [InlineData(" =HYPERLINK(\"x\")")]
    [InlineData("\"=1+1")]
    [InlineData("'@SUM(A1)")]
    [InlineData("  -2+3")]
    [InlineData("\n+cmd")]
    public void S27_a_formula_behind_leading_whitespace_or_quotes_is_escaped(string value)
    {
        string cell = MarketingCsv.Cell(value);

        string unquoted = cell.StartsWith('"') ? cell[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : cell;
        unquoted.Should().StartWith("'", "a spreadsheet must never see the formula");
        unquoted.Should().Be("'" + value);
    }

    [Fact]
    public void S27_plain_values_are_untouched()
    {
        MarketingCsv.Cell("alice@example.com").Should().Be("alice@example.com");
        MarketingCsv.Cell(" alice").Should().Be(" alice");
    }
}
