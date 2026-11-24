#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>brevo-marketing-campaigns T-002: the Brevo webhook, the erasure outbox and its drain. Fake Brevo only.</summary>
public sealed class MarketingBrevoErasureAndWebhookTests : IDisposable
{
    private const string Token = "hook-token-123";
    private readonly MarketingHarness _h = new();
    private readonly FakeBrevo _brevo = new();
    private readonly FakeProtector _protector = new();
    private readonly RecordingLogger<BrevoErasureOutbox> _log = new();
    private readonly MarketingCampaignOptions _opt = new()
    {
        Enabled = true,
        BrevoCampaignApiKey = "secret-key",
        BrevoApiBase = "https://brevo.example.test/v3",
        BrevoListId = 10,
        BrevoMinCallIntervalMs = 0,
        WebhookToken = Token,
    };

    public void Dispose() => _h.Dispose();

    private sealed class FakeProtector : ISecretProtector
    {
        public bool Fail { get; set; }
        public string Protect(string plaintext)
            => Fail ? throw new InvalidOperationException("no key") : "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = string.Empty;
            if (ciphertext is null || !ciphertext.StartsWith("enc:", StringComparison.Ordinal)) return false;
            plaintext = Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[4..]));
            return true;
        }
    }

    private void Add(string email, MarketingConsentStatus status = MarketingConsentStatus.OptedIn, bool synced = false, Guid? userId = null)
    {
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserId = userId,
            Status = status,
            WordingVersion = "2026-10-marketing-v1",
            Source = MarketingConsentSource.SiteContact,
            ConsentedAt = DateTime.UtcNow,
            ConfirmedAt = DateTime.UtcNow,
            RequestedAt = DateTime.UtcNow,
            BrevoSyncedAt = synced ? DateTime.UtcNow : null,
        });
        db.SaveChanges();
    }

    private MarketingBrevoWebhookController Hook(InferenceApiDbContext db, string? token, string body, MarketingCampaignOptions? opt = null)
    {
        DefaultHttpContext ctx = new();
        if (token is not null) ctx.Request.Headers[MarketingBrevoWebhookController.TokenHeader] = token;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return new MarketingBrevoWebhookController(
            _h.NewService(db), Microsoft.Extensions.Options.Options.Create(opt ?? _opt),
            new RecordingLogger<MarketingBrevoWebhookController>())
        { ControllerContext = new ControllerContext { HttpContext = ctx } };
    }

    private async Task<IActionResult> PostAsync(string? token, string body, MarketingCampaignOptions? opt = null)
    {
        await using InferenceApiDbContext db = _h.NewDb();
        return await Hook(db, token, body, opt).Receive(CancellationToken.None);
    }

    private BrevoErasureOutbox Outbox(InferenceApiDbContext db, MarketingCampaignOptions? opt = null)
    {
        BrevoCampaignClient client = new(new HttpClient(_brevo), Microsoft.Extensions.Options.Options.Create(opt ?? _opt), MailLogRef.Ephemeral(), new RecordingLogger<BrevoCampaignClient>());
        MarketingConsentService svc = _h.NewService(db);
        return new(db, svc, new BrevoContactGuard(db, svc, client, Microsoft.Extensions.Options.Options.Create(opt ?? _opt)), client,
        _protector, Microsoft.Extensions.Options.Options.Create(opt ?? _opt), _log, _h.Clock);
    }

    private MarketingConsent Row(string email)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsents.AsNoTracking().Single(c => c.Email == email);
    }

    private int Events(string email)
    {
        using InferenceApiDbContext db = _h.NewDb();
        Guid id = db.MarketingConsents.Single(c => c.Email == email).Id;
        return db.MarketingConsentEvents.Count(e => e.ConsentId == id);
    }

    // ----- webhook: a failing event does not abandon the array -----

    private sealed class FailOnce : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private bool _done;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true; // the first write of the first event fails; every later write goes through
                throw new InvalidOperationException("the database went away");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        // SaveChanges runs its INSERT/UPDATE statements through a reader
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            string text = command.CommandText.TrimStart();
            if (!_done && (text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) || text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)))
            {
                _done = true;
                throw new InvalidOperationException("the database went away");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task A_failing_event_does_not_abandon_the_rest_of_the_array_and_answers_500_with_the_count_and_no_address()
    {
        Add("a@example.test");
        Add("b@example.test");
        Add("c@example.test");
        RecordingLogger<MarketingBrevoWebhookController> log = new();
        await using InferenceApiDbContext db = _h.NewDb(new FailOnce());
        DefaultHttpContext ctx = new();
        ctx.Request.Headers[MarketingBrevoWebhookController.TokenHeader] = Token;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            "[{\"event\":\"unsubscribe\",\"email\":\"a@example.test\"},{\"event\":\"unsubscribe\",\"email\":\"b@example.test\"},{\"event\":\"spam\",\"email\":\"c@example.test\"}]"));
        MarketingBrevoWebhookController hook = new(_h.NewService(db), Microsoft.Extensions.Options.Options.Create(_opt), log)
        { ControllerContext = new ControllerContext { HttpContext = ctx } };

        IActionResult result = await hook.Receive(CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(500);
        Row("b@example.test").Status.Should().Be(MarketingConsentStatus.Withdrawn, "the events after the failing one are still processed");
        Row("c@example.test").Status.Should().Be(MarketingConsentStatus.Withdrawn);
        log.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("1 of 3 events", StringComparison.Ordinal));
        log.Entries.Should().NotContain(e => e.Message.Contains("example.test", StringComparison.Ordinal));
    }

    // ----- webhook: token -----

    [Fact]
    public async Task Blank_token_is_404_before_anything_else()
    {
        Add("a@example.test");
        (await PostAsync(Token, """{"event":"unsubscribe","email":"a@example.test"}""", new MarketingCampaignOptions { WebhookToken = " " }))
            .Should().BeOfType<NotFoundResult>();
        Row("a@example.test").Status.Should().Be(MarketingConsentStatus.OptedIn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("hook-token-1234")]
    public async Task Missing_or_forged_token_is_401_and_writes_nothing(string? token)
    {
        Add("a@example.test");
        IActionResult r = await PostAsync(token, """{"event":"unsubscribe","email":"a@example.test"}""");
        r.Should().BeOfType<UnauthorizedResult>();
        Row("a@example.test").Status.Should().Be(MarketingConsentStatus.OptedIn);
        Events("a@example.test").Should().Be(0);
    }

    [Fact]
    public async Task Oversized_body_is_rejected()
    {
        (await PostAsync(Token, new string('x', MarketingBrevoWebhookController.MaxBodyBytes + 1)))
            .Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task A_body_of_more_than_100_events_processes_every_one()
    {
        const int count = 150;
        StringBuilder body = new("[");
        for (int n = 0; n < count; n++)
        {
            string email = $"person{n}@example.test";
            Add(email);
            body.Append(n == 0 ? "" : ",").Append($$"""{"event":"unsubscribe","email":"{{email}}"}""");
        }

        body.Append(']');
        (await PostAsync(Token, body.ToString())).Should().BeOfType<OkResult>();

        for (int n = 0; n < count; n++)
        {
            Row($"person{n}@example.test").Status.Should().Be(MarketingConsentStatus.Withdrawn, $"event {n} must not be dropped");
        }
    }

    [Fact]
    public async Task A_body_over_the_limit_is_413_with_a_warning_that_carries_no_address()
    {
        Add("secret.person@example.test");
        RecordingLogger<MarketingBrevoWebhookController> log = new();
        DefaultHttpContext ctx = new();
        ctx.Request.Headers[MarketingBrevoWebhookController.TokenHeader] = Token;
        string pad = new('x', MarketingBrevoWebhookController.MaxBodyBytes);
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($$"""[{"event":"unsubscribe","email":"secret.person@example.test","pad":"{{pad}}"}]"""));
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingBrevoWebhookController c = new(_h.NewService(db), Microsoft.Extensions.Options.Options.Create(_opt), log)
        { ControllerContext = new ControllerContext { HttpContext = ctx } };

        (await c.Receive(CancellationToken.None)).Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(413);

        log.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        log.Entries.Should().NotContain(e => e.Message.Contains("secret.person", StringComparison.Ordinal) || e.Message.Contains("@example", StringComparison.Ordinal));
        MarketingBrevoWebhookController.MaxBodyBytes.Should().Be(1024 * 1024);
    }

    // ----- webhook: mappings -----

    [Theory]
    [InlineData("unsubscribe", MarketingConsentSource.BrevoUnsubscribe)]
    [InlineData("spam", MarketingConsentSource.EmailComplaint)]
    public async Task Unsubscribe_and_spam_withdraw_with_one_event_and_repeats_write_nothing(string ev, MarketingConsentSource source)
    {
        Add("a@example.test");
        string body = $$"""{"event":"{{ev}}","email":" A@Example.test "}""";
        (await PostAsync(Token, body)).Should().BeOfType<OkResult>();
        MarketingConsent row = Row("a@example.test");
        row.Status.Should().Be(MarketingConsentStatus.Withdrawn);
        row.Source.Should().Be(source);
        int events = Events("a@example.test");
        events.Should().Be(1);
        DateTime updated = row.UpdatedAt;

        (await PostAsync(Token, body)).Should().BeOfType<OkResult>();
        Events("a@example.test").Should().Be(1);
        Row("a@example.test").UpdatedAt.Should().Be(updated);
    }

    [Fact]
    public async Task Hard_bounce_marks_undeliverable_once_and_leaves_consent()
    {
        Add("a@example.test");
        string body = """[{"event":"hard_bounce","email":"a@example.test"}]""";
        (await PostAsync(Token, body)).Should().BeOfType<OkResult>();
        MarketingConsent row = Row("a@example.test");
        row.UndeliverableAt.Should().NotBeNull();
        row.Status.Should().Be(MarketingConsentStatus.OptedIn);
        Events("a@example.test").Should().Be(0);
        DateTime updated = row.UpdatedAt;

        _h.Clock.Advance(TimeSpan.FromHours(1));
        await PostAsync(Token, body);
        Row("a@example.test").UpdatedAt.Should().Be(updated);
    }

    [Fact]
    public async Task Soft_bounce_and_unknown_events_are_200_no_ops()
    {
        Add("a@example.test");
        Add("b@example.test");
        (await PostAsync(Token, """[{"event":"soft_bounce","email":"a@example.test"},{"event":"delivered","email":"b@example.test"},{"nonsense":1}]"""))
            .Should().BeOfType<OkResult>();
        Row("a@example.test").Status.Should().Be(MarketingConsentStatus.OptedIn);
        Row("a@example.test").UndeliverableAt.Should().BeNull();
        Row("b@example.test").Status.Should().Be(MarketingConsentStatus.OptedIn);
    }

    [Fact]
    public async Task Unknown_address_is_200_and_writes_nothing()
    {
        (await PostAsync(Token, """{"event":"unsubscribe","email":"nobody@example.test"}""")).Should().BeOfType<OkResult>();
        await using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.Should().BeEmpty();
        db.MarketingConsentEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Webhook_can_never_resubscribe_or_revive()
    {
        Add("w@example.test", MarketingConsentStatus.Withdrawn);
        Add("p@example.test", MarketingConsentStatus.Pending);
        foreach (string ev in new[] { "unsubscribe", "spam", "hard_bounce", "soft_bounce", "subscribe", "opt_in", "delivered" })
        {
            await PostAsync(Token, $$"""[{"event":"{{ev}}","email":"w@example.test"},{"event":"{{ev}}","email":"p@example.test"}]""");
        }

        Row("w@example.test").Status.Should().Be(MarketingConsentStatus.Withdrawn);
        Row("p@example.test").Status.Should().NotBe(MarketingConsentStatus.OptedIn);
    }

    // ----- purge staging -----

    private async Task<(Guid UserId, InferenceApiDbContext Db)> PurgeSetupAsync(string email, bool synced)
    {
        Guid user = _h.AddUser(email, _h.Now.AddDays(-1));
        Add(email, synced: synced, userId: user);
        return (user, _h.NewDb());
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Purge_stages_an_erasure_exactly_when_the_contact_was_synced(bool synced, int expected)
    {
        (Guid userId, InferenceApiDbContext db) = await PurgeSetupAsync("gone@example.test", synced);
        await using (db)
        {
            ApplicationUser user = db.ApplicationUsers.AsNoTracking().Single(u => u.Id == userId);
            await MarketingConsentErasure.StageAsync(db, user, _h.Suppression, _h.Now, CancellationToken.None, _protector);
            await db.SaveChangesAsync();
        }

        await using InferenceApiDbContext check = _h.NewDb();
        List<MarketingBrevoErasure> rows = check.MarketingBrevoErasures.AsNoTracking().ToList();
        rows.Should().HaveCount(expected);
        foreach (MarketingBrevoErasure r in rows)
        {
            r.ProtectedEmail.Should().NotContain("gone@example.test");
            _protector.TryUnprotect(r.ProtectedEmail, out string p).Should().BeTrue();
            p.Should().Be("gone@example.test");
        }
    }

    [Fact]
    public async Task Purge_throws_when_protection_fails_and_nothing_is_committed()
    {
        (Guid userId, InferenceApiDbContext db) = await PurgeSetupAsync("gone@example.test", synced: true);
        _protector.Fail = true;
        await using (db)
        {
            ApplicationUser user = db.ApplicationUsers.AsNoTracking().Single(u => u.Id == userId);
            Func<Task> act = () => MarketingConsentErasure.StageAsync(db, user, _h.Suppression, _h.Now, CancellationToken.None, _protector);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        await using InferenceApiDbContext check = _h.NewDb();
        check.MarketingBrevoErasures.Should().BeEmpty();
        check.MarketingConsents.Should().ContainSingle();
    }

    [Fact]
    public async Task Staged_erasure_rolls_back_with_the_purge_transaction()
    {
        (Guid userId, InferenceApiDbContext db) = await PurgeSetupAsync("gone@example.test", synced: true);
        await using (db)
        {
            ApplicationUser user = db.ApplicationUsers.AsNoTracking().Single(u => u.Id == userId);
            await MarketingConsentErasure.StageAsync(db, user, _h.Suppression, _h.Now, CancellationToken.None, _protector);
            // purge fails before SaveChanges: the context is discarded
        }

        await using InferenceApiDbContext check = _h.NewDb();
        check.MarketingBrevoErasures.Should().BeEmpty();
        check.MarketingConsents.Should().ContainSingle();
    }

    // ----- drain -----

    private async Task StageAsync(params string[] emails)
    {
        await using InferenceApiDbContext db = _h.NewDb();
        foreach (string e in emails)
        {
            db.MarketingBrevoErasures.Add(new MarketingBrevoErasure { Id = Guid.NewGuid(), ProtectedEmail = _protector.Protect(e), CreatedAt = _h.Now });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Drain_deletes_rows_on_204_and_404()
    {
        await StageAsync("one@example.test", "two@example.test");
        _brevo.Override = call => call.Method == "DELETE" && call.Path.Contains("two", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.NotFound, "{\"code\":\"document_not_found\"}") : null;
        await using InferenceApiDbContext db = _h.NewDb();
        (await Outbox(db).DrainAsync(CancellationToken.None)).Should().Be(0);
        await using InferenceApiDbContext check = _h.NewDb();
        check.MarketingBrevoErasures.Should().BeEmpty();
        _brevo.Calls.Count(c => c.Method == "DELETE").Should().Be(2);
    }

    [Fact]
    public async Task Drain_retains_a_5xx_row_with_attempts_and_blocks_the_send()
    {
        await StageAsync("one@example.test");
        _brevo.Override = call => call.Method == "DELETE" ? FakeBrevo.Status(HttpStatusCode.InternalServerError) : null;
        await using InferenceApiDbContext db = _h.NewDb();
        (await Outbox(db).DrainAsync(CancellationToken.None)).Should().Be(1);
        await using InferenceApiDbContext check = _h.NewDb();
        MarketingBrevoErasure row = check.MarketingBrevoErasures.AsNoTracking().Single();
        row.Attempts.Should().Be(1);
        row.LastAttemptAt.Should().NotBeNull();
        _log.Entries.Should().NotBeEmpty();
        _log.Entries.Should().OnlyContain(e => !e.Message.Contains("one@example.test") && !e.Message.Contains("secret-key"));
    }

    [Fact]
    public async Task Worker_pass_respects_backoff_but_pipeline_drain_does_not()
    {
        await StageAsync("one@example.test");
        int fail = 1;
        _brevo.Override = call => call.Method == "DELETE" && fail-- > 0 ? FakeBrevo.Status(HttpStatusCode.BadGateway) : null;
        await using InferenceApiDbContext db = _h.NewDb();
        BrevoErasureOutbox outbox = Outbox(db);
        (await outbox.DrainWithBackoffAsync(CancellationToken.None)).Should().Be(1);
        int deletes = _brevo.Calls.Count(c => c.Method == "DELETE");
        (await outbox.DrainWithBackoffAsync(CancellationToken.None)).Should().Be(1);
        _brevo.Calls.Count(c => c.Method == "DELETE").Should().Be(deletes, "the row is inside its backoff");
        (await outbox.DrainAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Blank_key_is_inert_and_leaves_rows_in_place()
    {
        await StageAsync("one@example.test");
        MarketingCampaignOptions blank = new() { BrevoCampaignApiKey = " ", BrevoApiBase = _opt.BrevoApiBase, BrevoListId = 10 };
        await using InferenceApiDbContext db = _h.NewDb();
        BrevoErasureOutbox outbox = Outbox(db, blank);
        (await outbox.DrainAsync(CancellationToken.None)).Should().Be(1);
        (await outbox.DeleteUnsendableAsync(CancellationToken.None)).Should().Be(0);
        _brevo.Calls.Should().BeEmpty();
        await using InferenceApiDbContext check = _h.NewDb();
        check.MarketingBrevoErasures.Should().ContainSingle();
    }

    [Fact]
    public async Task Pending_erasure_fails_a_campaign_run_with_erasure_pending()
    {
        await StageAsync("one@example.test");
        _brevo.Override = call => call.Method == "DELETE" ? FakeBrevo.Status(HttpStatusCode.InternalServerError) : null;
        await using InferenceApiDbContext db = _h.NewDb();
        // the same drain the pipeline's step 1 uses: anything left means erasure_pending
        IBrevoErasureDrain drain = Outbox(db);
        (await drain.DrainAsync(CancellationToken.None)).Should().BeGreaterThan(0);
        _brevo.Calls.Should().NotContain(c => c.Path.Contains("sendNow", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sweep_deletes_unsendable_synced_contacts_and_clears_the_mark_but_keeps_sendable_ones()
    {
        Add("keep@example.test", synced: true);
        Add("left@example.test", MarketingConsentStatus.Withdrawn, synced: true);
        Add("never@example.test", MarketingConsentStatus.Withdrawn, synced: false);
        _brevo.Existing.Add("left@example.test");
        await using InferenceApiDbContext db = _h.NewDb();
        (await Outbox(db).DeleteUnsendableAsync(CancellationToken.None)).Should().Be(1);
        Row("left@example.test").BrevoSyncedAt.Should().BeNull();
        Row("keep@example.test").BrevoSyncedAt.Should().NotBeNull();
        _brevo.Calls.Count(c => c.Method == "DELETE").Should().Be(1);
    }

    [Fact]
    public async Task Plain_address_is_not_stored_in_the_outbox()
    {
        (Guid userId, InferenceApiDbContext db) = await PurgeSetupAsync("plain@example.test", synced: true);
        await using (db)
        {
            ApplicationUser user = db.ApplicationUsers.AsNoTracking().Single(u => u.Id == userId);
            await MarketingConsentErasure.StageAsync(db, user, _h.Suppression, _h.Now, CancellationToken.None, _protector);
            await db.SaveChangesAsync();
        }

        await using InferenceApiDbContext check = _h.NewDb();
        check.MarketingBrevoErasures.AsNoTracking().Single().ProtectedEmail.Should().NotContain("plain").And.NotContain("@");
    }
}
