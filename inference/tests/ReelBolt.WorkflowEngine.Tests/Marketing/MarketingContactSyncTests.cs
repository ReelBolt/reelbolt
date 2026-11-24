#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>brevo-contact-sync T-002: the contact sync pass, the nightly reconcile and the admin endpoints. Fake Brevo only.</summary>
public sealed class MarketingContactSyncTests : IDisposable
{
    private readonly MarketingHarness _h = new();
    private readonly FakeBrevo _brevo = new();
    private readonly RecordingLogger<ContactSyncService> _log = new();
    private readonly ContactSyncSchedule _schedule = new();
    private readonly MarketingCampaignOptions _opt = new()
    {
        BrevoCampaignApiKey = "secret-key",
        BrevoApiBase = "https://brevo.example.test/v3",
        BrevoListId = 10,
        BrevoMinCallIntervalMs = 0,
    };
    private readonly MarketingContactSyncOptions _sync = new() { Enabled = true };

    public void Dispose() => _h.Dispose();

    private sealed class FakeUser(bool admin) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "admin@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
        public bool IsPlatformAdmin => admin;
    }

    private ContactSyncService Svc(InferenceApiDbContext db, MarketingContactSyncOptions? sync = null, MarketingCampaignOptions? opt = null)
    {
        MarketingCampaignOptions o = opt ?? _opt;
        var options = Microsoft.Extensions.Options.Options.Create(o);
        BrevoCampaignClient client = new(new HttpClient(_brevo), options, MailLogRef.Ephemeral(), new RecordingLogger<BrevoCampaignClient>());
        MarketingConsentService consents = _h.NewService(db);
        BrevoContactGuard guard = new(db, consents, client, options);
        BrevoErasureOutbox outbox = new(db, consents, guard, client, new TestProtector(), options, new RecordingLogger<BrevoErasureOutbox>(), _h.Clock);
        return new ContactSyncService(
            db, new MarketingNameReconciler(db, _h.Clock), guard, outbox, consents, client, options,
            Microsoft.Extensions.Options.Options.Create(sync ?? _sync), _schedule, _log, _h.Clock);
    }

    private async Task<ContactSyncResult> PassAsync(MarketingContactSyncOptions? sync = null, MarketingCampaignOptions? opt = null, bool onlyIfRequested = false)
    {
        _h.Clock.Advance(TimeSpan.FromSeconds(10));
        await using InferenceApiDbContext db = _h.NewDb();
        return await Svc(db, sync, opt).PassAsync(CancellationToken.None, onlyIfRequested);
    }

    private void Add(string email, string? first = null, string? last = null, MarketingConsentStatus status = MarketingConsentStatus.OptedIn, bool synced = false, bool pushed = false)
    {
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = Guid.NewGuid(),
            Email = email,
            Status = status,
            WordingVersion = "2026-10-marketing-v1",
            Source = MarketingConsentSource.SiteContact,
            ConsentedAt = _h.Now,
            ConfirmedAt = _h.Now,
            RequestedAt = _h.Now,
            UpdatedAt = _h.Now,
            FirstName = first,
            LastName = last,
            NameSource = first is null ? null : MarketingNameSource.Form,
            NameUpdatedAt = first is null ? null : _h.Now,
            BrevoSyncedAt = synced ? _h.Now : null,
            BrevoPushedAt = pushed ? _h.Now : null,
        });
        db.SaveChanges();
    }

    private void Update(string email, Action<MarketingConsent> change)
    {
        using InferenceApiDbContext db = _h.NewDb();
        MarketingConsent row = db.MarketingConsents.Single(c => c.Email == email);
        change(row);
        db.SaveChanges();
    }

    private MarketingConsent Row(string email)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsents.AsNoTracking().Single(c => c.Email == email);
    }

    private List<FakeBrevo.Call> Upserts() => _brevo.Calls.Where(c => c.Method == "POST" && c.Path == "contacts").ToList();

    [Fact]
    public async Task First_push_creates_the_contact_on_the_list_with_names()
    {
        Add("a@example.test", "Maria", "Rossi");

        ContactSyncResult r = await PassAsync();

        r.Pushed.Should().Be(1);
        FakeBrevo.Call call = Upserts().Single();
        using JsonDocument body = JsonDocument.Parse(call.Body!);
        body.RootElement.GetProperty("email").GetString().Should().Be("a@example.test");
        body.RootElement.GetProperty("updateEnabled").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("listIds")[0].GetInt64().Should().Be(10);
        body.RootElement.GetProperty("attributes").GetProperty("FIRSTNAME").GetString().Should().Be("Maria");
        call.Body.Should().NotContain("lacklist");
        _brevo.Lists[10].Should().Contain("a@example.test");
        MarketingConsent row = Row("a@example.test");
        row.BrevoPushedAt.Should().NotBeNull();
        row.BrevoSyncedAt.Should().NotBeNull();
        row.BrevoPushAttempts.Should().Be(0);

        _brevo.Calls.Clear();
        (await PassAsync()).Pushed.Should().Be(0, "a pushed contact is not pushed again");
        Upserts().Should().BeEmpty();
    }

    [Fact]
    public async Task Name_change_repushes_attributes_only_and_a_cleared_name_is_removed()
    {
        Add("a@example.test", "Maria", "Rossi");
        await PassAsync();
        _brevo.Calls.Clear();

        _h.Clock.Advance(TimeSpan.FromSeconds(10));
        Update("a@example.test", c => { c.FirstName = null; c.LastName = "Bianchi"; c.NameUpdatedAt = _h.Now; c.UpdatedAt = _h.Now; });
        (await PassAsync()).Pushed.Should().Be(1);

        using JsonDocument body = JsonDocument.Parse(Upserts().Single().Body!);
        body.RootElement.TryGetProperty("listIds", out _).Should().BeFalse("only a first add sends the list");
        body.RootElement.GetProperty("attributes").GetProperty("FIRSTNAME").GetString().Should().BeEmpty();
        _brevo.Attributes["a@example.test"].Should().Equal(new Dictionary<string, string> { ["LASTNAME"] = "Bianchi" });
    }

    [Fact]
    public async Task First_add_of_a_brevo_blocked_contact_is_withdrawn_locally_and_not_pushed()
    {
        Add("a@example.test", "Maria", "Rossi");
        _brevo.Blocked.Add("a@example.test");
        _brevo.Stats["a@example.test"] = ["user"];

        ContactSyncResult r = await PassAsync();

        r.Pushed.Should().Be(0);
        Upserts().Should().BeEmpty();
        Row("a@example.test").Status.Should().Be(MarketingConsentStatus.Withdrawn);
    }

    [Fact]
    public async Task Withdrawn_and_undeliverable_contacts_are_deleted_at_brevo_and_never_pushed()
    {
        Add("w@example.test", status: MarketingConsentStatus.Withdrawn, synced: true, pushed: true);
        Add("u@example.test", synced: true, pushed: true);
        Update("u@example.test", c => { c.UndeliverableAt = _h.Now; c.UndeliverableReason = "hard_bounce"; });
        _brevo.Lists[10].UnionWith(["w@example.test", "u@example.test"]);

        ContactSyncResult r = await PassAsync();

        r.Deleted.Should().Be(2);
        _brevo.Lists[10].Should().BeEmpty();
        Upserts().Should().BeEmpty();
        Row("w@example.test").BrevoSyncedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_purge_between_the_write_ahead_flag_and_the_push_stages_an_erasure_that_deletes_the_contact()
    {
        Add("a@example.test", "Maria");
        bool purged = false;
        _brevo.Override = call =>
        {
            if (purged || call.Method != "GET") return null;
            purged = true;
            // what the purge does to a row whose sync flag is already set: stage the erasure, delete the row
            using InferenceApiDbContext db = _h.NewDb();
            db.MarketingBrevoErasures.Add(new MarketingBrevoErasure { Id = Guid.NewGuid(), ProtectedEmail = new TestProtector().Protect("a@example.test"), CreatedAt = _h.Now });
            db.MarketingConsents.Where(c => c.Email == "a@example.test").ExecuteDelete();
            db.SaveChanges();
            return null;
        };

        await PassAsync();
        Upserts().Should().BeEmpty("the row is gone when its names are read, so nothing is pushed");
        await PassAsync();

        _brevo.Lists[10].Should().NotContain("a@example.test", "the staged erasure removes the contact");
        using InferenceApiDbContext check = _h.NewDb();
        check.MarketingBrevoErasures.Should().BeEmpty();
        Upserts().Should().BeEmpty("a purged row is never pushed");
        check.MarketingConsents.Should().BeEmpty();
    }

    [Fact]
    public async Task A_suppressed_address_with_an_opted_in_row_is_never_pushed_and_its_brevo_contact_is_deleted()
    {
        Add("sup@example.test", "Maria", synced: true, pushed: true);
        Add("ok@example.test", "Luca");
        using (InferenceApiDbContext db = _h.NewDb())
        {
            db.MarketingSuppressions.Add(new MarketingSuppression { Id = Guid.NewGuid(), EmailHmac = _h.Suppression.Hash("sup@example.test"), WithdrawnAt = _h.Now });
            db.SaveChanges();
        }

        _brevo.Lists[10].Add("sup@example.test");
        Update("sup@example.test", c => c.UpdatedAt = _h.Now.AddSeconds(5)); // dirty again, so a push would be tried

        ContactSyncResult r = await PassAsync();

        r.Pushed.Should().Be(1);
        Upserts().Should().ContainSingle().Which.Body.Should().Contain("ok@example.test");
        _brevo.Lists[10].Should().NotContain("sup@example.test");
        _brevo.Lists[10].Should().Contain("ok@example.test");
        Row("sup@example.test").BrevoSyncedAt.Should().BeNull();

        await using InferenceApiDbContext st = _h.NewDb();
        OkObjectResult ok = (OkObjectResult)await new MarketingContactSyncAdminController(Svc(st), new FakeUser(true)).Status(CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        json.RootElement.GetProperty("counts").GetProperty("pendingPush").GetInt32().Should().Be(0, "a suppressed row is not sendable");
    }

    [Fact]
    public async Task A_purge_that_lands_after_the_upsert_call_deletes_the_contact_again()
    {
        Add("a@example.test", "Maria");
        _brevo.After = call =>
        {
            if (call is not { Method: "POST", Path: "contacts" }) return;
            using InferenceApiDbContext db = _h.NewDb();
            db.MarketingConsents.Where(c => c.Email == "a@example.test").ExecuteDelete();
        };

        ContactSyncResult r = await PassAsync();

        r.Pushed.Should().Be(0);
        _brevo.Calls.Should().Contain(c => c.Method == "DELETE" && c.Path.StartsWith("contacts/a%40example.test", StringComparison.Ordinal));
        _brevo.Lists[10].Should().BeEmpty();
        _brevo.Existing.Should().BeEmpty();
    }

    [Fact]
    public async Task A_withdrawal_that_lands_after_the_upsert_call_deletes_the_contact_and_clears_the_marks()
    {
        Add("a@example.test", "Maria");
        _brevo.After = call =>
        {
            if (call is not { Method: "POST", Path: "contacts" }) return;
            using InferenceApiDbContext db = _h.NewDb();
            db.MarketingConsents.Where(c => c.Email == "a@example.test")
                .ExecuteUpdate(s => s.SetProperty(c => c.Status, MarketingConsentStatus.Withdrawn));
        };

        await PassAsync();

        _brevo.Calls.Should().Contain(c => c.Method == "DELETE" && c.Path.StartsWith("contacts/a%40example.test", StringComparison.Ordinal));
        _brevo.Lists[10].Should().BeEmpty();
        MarketingConsent row = Row("a@example.test");
        row.BrevoSyncedAt.Should().BeNull();
        row.BrevoPushedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_reopt_in_after_a_withdrawal_is_a_guarded_first_add_with_the_list()
    {
        Add("a@example.test", "Maria", synced: true, pushed: true);
        _brevo.Lists[10].Add("a@example.test");
        Update("a@example.test", c => c.Status = MarketingConsentStatus.Withdrawn);
        await PassAsync();
        _brevo.Lists[10].Should().BeEmpty();
        Row("a@example.test").BrevoPushedAt.Should().BeNull("the contact is gone from Brevo, so the push mark goes with the sync mark");

        _h.Clock.Advance(TimeSpan.FromSeconds(10));
        Update("a@example.test", c => { c.Status = MarketingConsentStatus.OptedIn; c.ConfirmedAt = _h.Now; c.UpdatedAt = _h.Now; });
        _brevo.Calls.Clear();
        (await PassAsync()).Pushed.Should().Be(1);

        _brevo.Calls.Should().Contain(c => c.Method == "GET" && c.Path.StartsWith("contacts/a%40example.test", StringComparison.Ordinal), "the guard read the contact first");
        using JsonDocument body = JsonDocument.Parse(Upserts().Single().Body!);
        body.RootElement.GetProperty("listIds")[0].GetInt64().Should().Be(10);
    }

    [Fact]
    public async Task The_erasure_outbox_and_sweep_wait_while_a_sync_pass_holds_its_lease()
    {
        Add("a@example.test", status: MarketingConsentStatus.Withdrawn, synced: true, pushed: true);
        _brevo.Lists[10].Add("a@example.test");
        await using InferenceApiDbContext db = _h.NewDb();
        await Svc(db).EnsureStateAsync(CancellationToken.None);
        db.MarketingContactSyncStates.ExecuteUpdate(s => s.SetProperty(x => x.LastRunAt, (DateTime?)_h.Now).SetProperty(x => x.LastRunOk, (bool?)null));
        var options = Microsoft.Extensions.Options.Options.Create(_opt);
        BrevoCampaignClient client = new(new HttpClient(_brevo), options, MailLogRef.Ephemeral(), new RecordingLogger<BrevoCampaignClient>());
        MarketingConsentService consents = _h.NewService(db);
        BrevoErasureOutbox outbox = new(db, consents, new BrevoContactGuard(db, consents, client, options), client, new TestProtector(), options,
            new RecordingLogger<BrevoErasureOutbox>(), _h.Clock);

        (await outbox.DeleteUnsendableAsync(CancellationToken.None)).Should().Be(0);
        _brevo.Calls.Should().BeEmpty();
        (await outbox.DeleteUnsendableAsync(CancellationToken.None, fromSyncPass: true)).Should().Be(1);
    }

    [Fact]
    public async Task A_pass_whose_lease_was_taken_over_stops_and_leaves_the_new_owners_mark_alone()
    {
        Add("a@example.test", "Maria");
        DateTime takeover = _h.Now.AddMinutes(11);
        bool done = false;
        _brevo.Override = call =>
        {
            if (done) return null;
            done = true;
            // another replica claimed the row after this pass's lease expired
            using InferenceApiDbContext db = _h.NewDb();
            db.MarketingContactSyncStates.ExecuteUpdate(s => s.SetProperty(x => x.LastRunAt, (DateTime?)takeover).SetProperty(x => x.LastRunOk, (bool?)null));
            return null;
        };

        ContactSyncResult r = await PassAsync();

        r.Ran.Should().BeFalse();
        r.SkippedReason.Should().Be("lease_lost");
        using InferenceApiDbContext check = _h.NewDb();
        MarketingContactSyncState state = check.MarketingContactSyncStates.AsNoTracking().Single();
        state.LastRunAt.Should().Be(takeover);
        state.LastRunOk.Should().BeNull("the new owner's in-progress mark survives the first pass's release");
    }

    [Fact]
    public async Task A_second_claimer_can_take_over_once_the_first_lease_has_expired()
    {
        Add("a@example.test");
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await Svc(db).EnsureStateAsync(CancellationToken.None);
            db.MarketingContactSyncStates.ExecuteUpdate(s => s.SetProperty(x => x.LastRunAt, (DateTime?)_h.Now.AddMinutes(-11)).SetProperty(x => x.LastRunOk, (bool?)null));
        }

        (await PassAsync()).Ran.Should().BeTrue();
        using InferenceApiDbContext check = _h.NewDb();
        check.MarketingContactSyncStates.AsNoTracking().Single().LastRunOk.Should().BeTrue();
    }

    private BrevoErasureOutbox NewOutbox(InferenceApiDbContext db)
    {
        var options = Microsoft.Extensions.Options.Options.Create(_opt);
        BrevoCampaignClient client = new(new HttpClient(_brevo), options, MailLogRef.Ephemeral(), new RecordingLogger<BrevoCampaignClient>());
        MarketingConsentService consents = _h.NewService(db);
        return new BrevoErasureOutbox(db, consents, new BrevoContactGuard(db, consents, client, options), client, new TestProtector(), options,
            new RecordingLogger<BrevoErasureOutbox>(), _h.Clock);
    }

    private void StageErasures(int n)
    {
        using InferenceApiDbContext db = _h.NewDb();
        for (int i = 0; i < n; i++)
        {
            db.MarketingBrevoErasures.Add(new MarketingBrevoErasure { Id = Guid.NewGuid(), ProtectedEmail = new TestProtector().Protect($"e{i}@example.test"), CreatedAt = _h.Now.AddSeconds(i) });
        }

        db.SaveChanges();
    }

    [Fact]
    public async Task The_drain_renews_before_every_row_when_run_by_a_pass_and_stops_when_the_lease_is_lost()
    {
        StageErasures(3);
        int renewals = 0;
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            (await NewOutbox(db).DrainWithBackoffAsync(CancellationToken.None, renew: _ => Task.FromResult(++renewals > 0))).Should().Be(0);
        }

        renewals.Should().Be(3);

        StageErasures(3);
        renewals = 0;
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            (await NewOutbox(db).DrainWithBackoffAsync(CancellationToken.None, renew: _ => Task.FromResult(++renewals < 2))).Should().Be(2, "the drain stops once the lease is lost");
        }

        _brevo.Calls.Count(c => c.Method == "DELETE").Should().Be(3 + 1);

        await using (InferenceApiDbContext db = _h.NewDb())
        {
            (await NewOutbox(db).DrainWithBackoffAsync(CancellationToken.None)).Should().Be(0, "the standalone worker call has no callback and drains everything");
        }
    }

    [Fact]
    public async Task The_unsendable_sweep_renews_per_contact_and_stops_when_the_lease_is_lost()
    {
        foreach (string e in new[] { "a", "b", "c" })
        {
            Add($"{e}@example.test", status: MarketingConsentStatus.Withdrawn, synced: true, pushed: true);
            _brevo.Lists[10].Add($"{e}@example.test");
        }

        int renewals = 0;
        await using InferenceApiDbContext db = _h.NewDb();
        (await NewOutbox(db).DeleteUnsendableAsync(CancellationToken.None, renew: _ => Task.FromResult(++renewals < 3))).Should().Be(2);
        renewals.Should().Be(3);
        _brevo.Lists[10].Should().HaveCount(1);
    }

    [Fact]
    public async Task A_row_purged_before_the_write_ahead_is_not_pushed()
    {
        Add("a@example.test");
        _h.Clock.Advance(TimeSpan.FromSeconds(10));
        await using InferenceApiDbContext db = _h.NewDb();
        ContactSyncService svc = Svc(db);
        await db.MarketingConsents.Where(c => c.Email == "a@example.test").ExecuteDeleteAsync();

        (await svc.PassAsync(CancellationToken.None)).Pushed.Should().Be(0);
        _brevo.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(MarketingCampaignStatus.Queued)]
    [InlineData(MarketingCampaignStatus.Syncing)]
    public async Task A_pass_pauses_while_a_campaign_is_queued_or_syncing(MarketingCampaignStatus status)
    {
        Add("a@example.test");
        using (InferenceApiDbContext db = _h.NewDb())
        {
            db.MarketingCampaigns.Add(new MarketingCampaign { Id = Guid.NewGuid(), Subject = "s", Html = "h", Status = status, CreatedBy = Guid.NewGuid(), UpdatedAt = _h.Now.AddSeconds(5), CreatedAt = _h.Now });
            db.SaveChanges();
        }

        ContactSyncResult r = await PassAsync();

        r.Ran.Should().BeFalse();
        r.SkippedReason.Should().Be("campaign_active");
        _brevo.Calls.Should().BeEmpty();
        Row("a@example.test").BrevoSyncedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_push_backs_off_then_recovers_and_other_rows_still_go_out()
    {
        Add("a@example.test");
        Add("b@example.test");
        _brevo.Override = call => call.Method == "POST" && call.Path == "contacts" && call.Body!.Contains("a@example.test")
            ? FakeBrevo.Status(HttpStatusCode.InternalServerError)
            : null;

        ContactSyncResult r = await PassAsync();

        r.Failed.Should().Be(1);
        r.Pushed.Should().Be(1);
        MarketingConsent a = Row("a@example.test");
        a.BrevoPushAttempts.Should().Be(1);
        a.BrevoPushErrorCode.Should().Be("brevo_http_500");
        a.BrevoPushAttemptedAt.Should().NotBeNull();
        a.BrevoPushedAt.Should().BeNull();

        _brevo.Calls.Clear();
        (await PassAsync()).Failed.Should().Be(0);
        Upserts().Should().BeEmpty("inside the backoff");

        _brevo.Override = null;
        _h.Clock.Advance(TimeSpan.FromMinutes(3));
        (await PassAsync()).Pushed.Should().Be(1);
        a = Row("a@example.test");
        a.BrevoPushAttempts.Should().Be(0);
        a.BrevoPushErrorCode.Should().BeNull();
        a.BrevoPushedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Nightly_reconcile_readds_missing_removes_extra_and_classifies_blocked()
    {
        Add("keep@example.test", synced: true, pushed: true);
        Add("missing@example.test", synced: true, pushed: true);
        Add("blocked@example.test", synced: true, pushed: true);
        _brevo.Lists[10].UnionWith(["keep@example.test", "extra@example.test", "blocked@example.test"]);
        _brevo.Blocked.Add("blocked@example.test");
        _brevo.Stats["blocked@example.test"] = ["user"];
        _h.Clock.Advance(TimeSpan.FromHours(18)); // 2026-10-13 03:00 UTC

        ContactSyncResult r = await PassAsync();

        r.Reconciled.Should().BeTrue();
        _brevo.Lists[10].Should().BeEquivalentTo(["keep@example.test"]);
        Row("blocked@example.test").Status.Should().Be(MarketingConsentStatus.Withdrawn);
        Row("missing@example.test").BrevoPushedAt.Should().BeNull("re-added through the guarded first add");
        Row("keep@example.test").BrevoPushedAt.Should().NotBeNull();
        using (InferenceApiDbContext db = _h.NewDb())
        {
            db.MarketingContactSyncStates.AsNoTracking().Single().LastReconcileAt.Should().NotBeNull();
        }

        // the next pass re-adds the missing one through the first-add path
        _brevo.Calls.Clear();
        (await PassAsync()).Pushed.Should().Be(1);
        _brevo.Lists[10].Should().Contain("missing@example.test");
        (await PassAsync()).Reconciled.Should().BeFalse("at most once per 20 hours");
    }

    [Fact]
    public async Task A_failed_list_read_in_the_reconcile_changes_nothing_and_records_the_code()
    {
        Add("keep@example.test", synced: true, pushed: true);
        _brevo.Lists[10].UnionWith(["keep@example.test", "extra@example.test"]);
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("contacts/lists/10/contacts", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.InternalServerError)
            : null;
        _h.Clock.Advance(TimeSpan.FromHours(18));

        await PassAsync();

        _brevo.Lists[10].Should().Contain("extra@example.test");
        _brevo.Writes.Should().BeEmpty();
        using InferenceApiDbContext db = _h.NewDb();
        MarketingContactSyncState state = db.MarketingContactSyncStates.AsNoTracking().Single();
        state.LastRunOk.Should().BeFalse();
        state.LastErrorCode.Should().Be("brevo_http_500");
        state.LastReconcileAt.Should().BeNull();
    }

    [Fact]
    public async Task Disabled_flag_or_blank_key_or_missing_list_means_zero_brevo_calls()
    {
        Add("a@example.test", synced: true);
        Update("a@example.test", c => c.Status = MarketingConsentStatus.Withdrawn);

        (await PassAsync(new MarketingContactSyncOptions { Enabled = false })).Ran.Should().BeFalse();
        (await PassAsync(opt: new MarketingCampaignOptions { BrevoCampaignApiKey = " ", BrevoListId = 10, BrevoMinCallIntervalMs = 0 })).Ran.Should().BeFalse();
        (await PassAsync(opt: new MarketingCampaignOptions { BrevoCampaignApiKey = "k", BrevoMinCallIntervalMs = 0 })).Ran.Should().BeFalse();

        _brevo.Calls.Should().BeEmpty();
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingContactSyncStates.Should().BeEmpty("an inactive pass does not even touch the state row");
    }

    [Fact]
    public async Task Single_flight_a_second_pass_while_one_is_running_does_nothing()
    {
        Add("a@example.test");
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await Svc(db).EnsureStateAsync(CancellationToken.None);
            db.MarketingContactSyncStates.ExecuteUpdate(s => s.SetProperty(x => x.LastRunAt, (DateTime?)_h.Now.AddSeconds(-30)).SetProperty(x => x.LastRunOk, (bool?)null));
        }

        ContactSyncResult r = await PassAsync();

        r.SkippedReason.Should().Be("other_run");
        _brevo.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_now_is_202_then_429_then_409_when_disabled_and_the_worker_clears_the_request()
    {
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingContactSyncAdminController Ctl(InferenceApiDbContext d, bool admin = true, MarketingContactSyncOptions? sync = null)
            => new(Svc(d, sync), new FakeUser(admin));

        (await Ctl(db, admin: false).Run(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await Ctl(db, sync: new MarketingContactSyncOptions { Enabled = false }).Run(CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>().Which.Value!.ToString().Should().Contain("sync_disabled");

        (await Ctl(db).Run(CancellationToken.None)).Should().BeOfType<AcceptedResult>().Which.Value!.ToString().Should().Contain("True");
        ObjectResult limited = (ObjectResult)await Ctl(db).Run(CancellationToken.None);
        limited.StatusCode.Should().Be(429);
        limited.Value!.ToString().Should().Contain("rate_limited");

        db.MarketingContactSyncStates.AsNoTracking().Single().RunRequestedAt.Should().NotBeNull();
        (await PassAsync(onlyIfRequested: true)).Ran.Should().BeTrue();
        using InferenceApiDbContext after = _h.NewDb();
        after.MarketingContactSyncStates.AsNoTracking().Single().RunRequestedAt.Should().BeNull();
        (await PassAsync(onlyIfRequested: true)).SkippedReason.Should().Be("not_requested");
    }

    [Fact]
    public async Task Status_reports_counts_and_the_last_run()
    {
        Add("a@example.test");
        Add("b@example.test");
        _brevo.Override = call => call.Method == "POST" && call.Path == "contacts" && call.Body!.Contains("b@example.test")
            ? FakeBrevo.Status(HttpStatusCode.BadRequest, "{\"code\":\"invalid_parameter\"}")
            : null;
        await PassAsync();

        await using InferenceApiDbContext db = _h.NewDb();
        OkObjectResult ok = (OkObjectResult)await new MarketingContactSyncAdminController(Svc(db), new FakeUser(true)).Status(CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        JsonElement r = json.RootElement;
        r.GetProperty("enabled").GetBoolean().Should().BeTrue();
        r.GetProperty("configured").GetBoolean().Should().BeTrue();
        r.GetProperty("campaignActive").GetBoolean().Should().BeFalse();
        r.GetProperty("lastRunOk").GetBoolean().Should().BeTrue();
        r.GetProperty("lastRunAt").ValueKind.Should().Be(JsonValueKind.String);
        r.GetProperty("counts").GetProperty("synced").GetInt32().Should().Be(1);
        r.GetProperty("counts").GetProperty("pendingPush").GetInt32().Should().Be(1);
        r.GetProperty("counts").GetProperty("failed").GetInt32().Should().Be(1);
        (await new MarketingContactSyncAdminController(Svc(db), new FakeUser(false)).Status(CancellationToken.None)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task No_name_or_address_ever_appears_in_the_captured_logs()
    {
        Add("secret.person@example.test", "Zelda", "Fitzgerald");
        Add("fail.person@example.test", "Quentin", "Blake");
        _brevo.Override = call => call.Method == "POST" && call.Path == "contacts" && call.Body!.Contains("fail.person")
            ? FakeBrevo.Status(HttpStatusCode.InternalServerError)
            : null;

        await PassAsync();

        string all = string.Join("\n", _log.Entries.Select(e => e.Message));
        all.Should().NotBeEmpty();
        all.Should().NotContain("example.test").And.NotContain("Zelda").And.NotContain("Fitzgerald").And.NotContain("Quentin").And.NotContain("Blake");
    }
}
