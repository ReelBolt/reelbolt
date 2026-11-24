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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>Campaign drafts, test send, the send request and the sync/send pipeline, against a fake Brevo. Nothing is sent for real.</summary>
public sealed class MarketingCampaignTests : IDisposable
{
    private const string Dummy = "Test Street 1, 00000 Testville";
    private readonly MarketingHarness _h = new();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly FakeBrevo _brevo = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly RecordingLogger<BrevoCampaignClient> _clientLog = new();
    private readonly MarketingCampaignOptions _opt = new()
    {
        Enabled = true,
        FooterControllerName = "Test Operator",
        FooterAddress = Dummy,
        TestRecipient = "owner@example.test",
        FromAddress = "from@example.test",
        BrevoCampaignApiKey = "secret-key",
        BrevoApiBase = "https://brevo.example.test/v3",
        BrevoListId = 10,
        BrevoTestListId = 11,
        ListSendEnabled = true,
        BrevoMinCallIntervalMs = 0,
        ProcessPollMs = 0,
        ProcessWaitSeconds = 5,
        ImportChunkSize = 2,
        TestSendsPerHour = 100,
    };

    public void Dispose()
    {
        foreach (IDisposable d in _disposables) d.Dispose();
        _h.Dispose();
    }

    private sealed class FakeUser(Guid id, bool admin) : ICurrentUser
    {
        public Guid UserId => id;
        public string Email => "admin@example.test";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
        public bool IsPlatformAdmin => admin;
    }

    private static readonly CampaignRequest Body = new("Hello", "<p>hello there</p>");

    private InferenceApiDbContext Db()
    {
        InferenceApiDbContext db = _h.NewDb();
        _disposables.Add(db);
        return db;
    }

    private BrevoCampaignClient Client() => new(
        new HttpClient(_brevo), Microsoft.Extensions.Options.Options.Create(_opt), MailLogRef.Ephemeral(), _clientLog);

    private MarketingCampaignService Service(InferenceApiDbContext db) => new(
        db, _h.NewService(db), Client(),
        Microsoft.Extensions.Options.Options.Create(_h.Options),
        Microsoft.Extensions.Options.Options.Create(_h.CloudStorage),
        Microsoft.Extensions.Options.Options.Create(_opt),
        new RecordingLogger<MarketingCampaignService>(), _h.Clock, new TestProtector());

    private MarketingCampaignPipeline Pipeline(InferenceApiDbContext db)
    {
        BrevoCampaignClient client = Client();
        MarketingConsentService svc = _h.NewService(db);
        BrevoContactGuard guard = new(db, svc, client, Microsoft.Extensions.Options.Options.Create(_opt));
        BrevoErasureOutbox drain = new(db, svc, guard, client, new TestProtector(),
            Microsoft.Extensions.Options.Options.Create(_opt), new RecordingLogger<BrevoErasureOutbox>(), _h.Clock);
        return new(db, svc, Service(db), client, drain, guard,
            Microsoft.Extensions.Options.Options.Create(_opt),
            new RecordingLogger<MarketingCampaignPipeline>(), _h.Clock, new TestProtector());
    }

    private MarketingCampaignAdminController Controller(InferenceApiDbContext db, bool admin = true)
        => new(Service(db), new FakeUser(_admin, admin));

    private void Add(string email, MarketingConsentStatus status = MarketingConsentStatus.OptedIn, bool confirmed = true, Guid? userId = null, bool synced = false)
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
            ConfirmedAt = confirmed ? DateTime.UtcNow : null,
            RequestedAt = DateTime.UtcNow,
            BrevoSyncedAt = synced ? DateTime.UtcNow : null,
        });
        db.SaveChanges();
    }

    private void AddThree()
    {
        Add("a@example.test");
        Add("b@example.test");
        Add("c@example.test");
    }

    private async Task<Guid> DraftAsync(CampaignRequest? body = null)
    {
        await using InferenceApiDbContext db = _h.NewDb();
        return (await Service(db).CreateAsync(new CampaignContent((body ?? Body).Subject, (body ?? Body).Html), _admin, CancellationToken.None)).Id;
    }

    /// <summary>A Draft that passed its test send (the fake's recorded calls are cleared afterwards).</summary>
    private async Task<Guid> TestedDraftAsync()
    {
        Guid id = await DraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        await Service(db).TestSendAsync(id, _admin, CancellationToken.None);
        _brevo.Calls.Clear();
        return id;
    }

    /// <summary>A campaign queued for the worker.</summary>
    private async Task<Guid> QueuedAsync(int confirm = 3)
    {
        Guid id = await TestedDraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        await Service(db).RequestSendAsync(id, confirm, _admin, CancellationToken.None);
        return id;
    }

    private MarketingCampaign Load(Guid id)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingCampaigns.AsNoTracking().Single(c => c.Id == id);
    }

    private async Task<MarketingCampaign> RunAsync(Guid id)
    {
        await using InferenceApiDbContext db = _h.NewDb();
        (await Pipeline(db).ClaimAndRunAsync(CancellationToken.None)).Should().BeTrue();
        return Load(id);
    }

    private string[] Listed(long id) => _brevo.Lists[id].Order(StringComparer.Ordinal).ToArray();

    // ---------------------------------------------------------------- drafts, dry run, settings

    [Fact]
    public async Task Drafts_are_created_listed_newest_first_edited_and_deleted_only_while_draft()
    {
        Guid first = await DraftAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        Guid second = await DraftAsync(new CampaignRequest("Second", "<p>second body</p>"));
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingCampaignAdminController c = Controller(db);

        object list = ((OkObjectResult)await c.List(50, CancellationToken.None)).Value!;
        JsonSerializer.Serialize(list).Should().MatchRegex($"{second}.*{first}");

        await Service(db).TestSendAsync(first, _admin, CancellationToken.None);
        Load(first).TestSentAt.Should().NotBeNull();
        await c.Update(first, new CampaignRequest("Edited", "<p>edited body</p>"), CancellationToken.None);
        MarketingCampaign edited = Load(first);
        edited.Subject.Should().Be("Edited");
        edited.TestSentAt.Should().BeNull("an edit clears the test record");
        edited.TestContentHash.Should().BeNull();

        await using InferenceApiDbContext other = _h.NewDb();
        await other.MarketingCampaigns.Where(x => x.Id == second).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MarketingCampaignStatus.Failed));
        (await c.Update(second, Body, CancellationToken.None)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
        (await c.Delete(second, CancellationToken.None)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
        (await c.Delete(first, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await c.Get(first, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await c.Create(new CampaignRequest("a\r\nBcc: x", "<p>body body</p>"), CancellationToken.None)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task DryRun_counts_follow_the_export_rules_mask_samples_and_call_nothing_at_brevo()
    {
        AddThree();
        Add("pending@example.test", confirmed: false);
        Add("out@example.test", MarketingConsentStatus.Withdrawn);
        Add("unverified@example.test", userId: _h.AddUser("unverified@example.test", null));
        Add("verified@example.test", userId: _h.AddUser("verified@example.test", DateTime.UtcNow));
        Add("suppressed@example.test");
        await using (InferenceApiDbContext sdb = _h.NewDb())
        {
            sdb.MarketingSuppressions.Add(new MarketingSuppression { Id = Guid.NewGuid(), EmailHmac = _h.Suppression.Hash("suppressed@example.test"), WithdrawnAt = DateTime.UtcNow });
            sdb.SaveChanges();
        }

        Guid id = await DraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        IActionResult res = await Controller(db).DryRun(id, CancellationToken.None);

        string json = JsonSerializer.Serialize(res.Should().BeOfType<OkObjectResult>().Subject.Value!);
        json.Should().Contain("\"recipientCount\":4").And.Contain("\"suppressedCount\":1").And.Contain("\"footerOk\":true").And.Contain("\"listSendEnabled\":true");
        json.Should().NotContain("a@example.test").And.Contain("a***@e***.test");
        JsonDocument.Parse(json).RootElement.GetProperty("sampleMasked").GetArrayLength().Should().Be(3);
        _brevo.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Footer_fails_closed_when_identity_or_address_is_blank(bool blankName)
    {
        AddThree();
        Guid id = await TestedDraftAsync();
        if (blankName) _opt.FooterControllerName = " "; else _opt.FooterAddress = null;
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingCampaignService svc = Service(db);

        CampaignDryRunResult r = (await svc.DryRunAsync(id, _admin, CancellationToken.None))!;
        r.FooterOk.Should().BeFalse();
        r.Errors.Should().NotBeEmpty();
        Action build = () => svc.BuildHtml(new CampaignContent("s", "<p>some body</p>"));
        build.Should().Throw<CampaignRefusal>();
        (await Controller(db).Send(id, new CampaignSendRequest(3), CancellationToken.None)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        Func<Task> test = () => svc.TestSendAsync(id, _admin, CancellationToken.None);
        await test.Should().ThrowAsync<CampaignRefusal>();
        _brevo.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_test_send_and_list_send_use_the_same_function_byte_for_byte()
    {
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingCampaignService svc = Service(db);
        CampaignContent content = new("s", "<!DOCTYPE html><html><body><div><p>some body {{ params.X }}</p><!-- open");
        string built = svc.BuildHtml(content);
        svc.TryBuildHtml(content, out string? error).Should().Be(built);
        error.Should().BeNull();
        built.Should().StartWith("<!DOCTYPE html>").And.Contain("{{ params.X }}");
        MarketingCampaignService.ContentHash(content).Should().Be(MarketingCampaignService.ContentHash(new CampaignContent("s", content.Html)));
    }

    [Fact]
    public async Task Footer_has_the_brevo_unsubscribe_placeholder_privacy_link_and_identity()
    {
        await using InferenceApiDbContext db = _h.NewDb();
        string html = Service(db).BuildHtml(new CampaignContent("s", "<p>some body</p>"));
        html.Should().Contain("<p>some body</p>").And.Contain("{{ unsubscribe }}").And.Contain("https://app.example.test/legal/privacy")
            .And.Contain("ReelBolt is operated by Test Operator").And.Contain(Dummy).And.Contain("P.IVA IT02862070030");
        _opt.FooterVatNumber = " ";
        Service(db).BuildHtml(new CampaignContent("s", "<p>some body</p>")).Should().NotContain("P.IVA");
    }

    [Fact]
    public async Task Settings_never_expose_the_key()
    {
        await using InferenceApiDbContext db = _h.NewDb();
        string json = JsonSerializer.Serialize(((OkObjectResult)Controller(db).Settings()).Value!);
        json.Should().Contain("\"footerOk\":true").And.Contain("\"listSendEnabled\":true").And.Contain("o***@e***.test");
        json.Should().NotContain("secret-key").And.NotContain("owner@example.test");
    }

    // ---------------------------------------------------------------- test send

    [Fact]
    public async Task TestSend_targets_only_the_configured_recipient_and_never_the_list_or_an_empty_emailTo()
    {
        AddThree();
        _brevo.Lists[10].Add("a@example.test");
        Guid id = await DraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();

        (await Controller(db).TestSend(id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        _brevo.Lists[11].Should().BeEquivalentTo(["owner@example.test"]);
        _brevo.Listed10Untouched();
        FakeBrevo.Call create = _brevo.Calls.Single(c => c.Path == "emailCampaigns");
        using (JsonDocument doc = JsonDocument.Parse(create.Body!))
        {
            doc.RootElement.TryGetProperty("recipients", out _).Should().BeFalse("a test campaign has no recipients");
            doc.RootElement.GetProperty("subject").GetString().Should().Be("[TEST] Hello");
            doc.RootElement.GetProperty("htmlContent").GetString().Should().Contain("{{ unsubscribe }}")
                .And.Be(Service(db).BuildHtml(new CampaignContent(Body.Subject, Body.Html)), "the test send carries the same bytes as the preview");
        }

        FakeBrevo.Call test = _brevo.Calls.Single(c => c.Path.EndsWith("/sendTest", StringComparison.Ordinal));
        JsonDocument.Parse(test.Body!).RootElement.GetProperty("emailTo").EnumerateArray().Select(e => e.GetString()).Should().Equal("owner@example.test");
        _brevo.Calls.Last().Method.Should().Be("DELETE", "the throwaway campaign is deleted");
        _brevo.Sends.Should().BeEmpty();
        Load(id).TestSentAt.Should().NotBeNull();
        Load(id).TestContentHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Client_refuses_an_empty_emailTo_and_never_blocklists_on_import()
    {
        BrevoCampaignClient client = Client();
        Func<Task> empty = () => client.SendTestAsync(5, [], CancellationToken.None);
        (await empty.Should().ThrowAsync<BrevoCampaignException>()).Which.Code.Should().Be("brevo_empty_recipients");
        Func<Task> blank = () => client.SendTestAsync(5, [" "], CancellationToken.None);
        await blank.Should().ThrowAsync<BrevoCampaignException>();
        _brevo.Calls.Should().BeEmpty();

        await client.ImportAsync(10, [new BrevoContactInput("a@example.test")], CancellationToken.None);
        _brevo.Calls.Single().Body.Should().NotContain("emailBlacklist").And.Contain("\"updateExistingContacts\":true").And.Contain("\"disableNotification\":true");
    }

    [Fact]
    public async Task TestSend_refused_without_recipient_key_or_test_list_and_is_rate_limited()
    {
        Guid id = await DraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingCampaignAdminController c = Controller(db);

        _opt.TestRecipient = null;
        (await c.TestSend(id, CancellationToken.None)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        _opt.TestRecipient = "owner@example.test";
        _opt.BrevoCampaignApiKey = " ";
        JsonSerializer.Serialize(((ObjectResult)await c.TestSend(id, CancellationToken.None)).Value).Should().Contain("brevo_key_missing");
        _opt.BrevoCampaignApiKey = "secret-key";
        _opt.BrevoTestListId = 0;
        JsonSerializer.Serialize(((ObjectResult)await c.TestSend(id, CancellationToken.None)).Value).Should().Contain("brevo_test_list_missing");
        _brevo.Calls.Should().BeEmpty();

        _opt.BrevoTestListId = 11;
        _opt.TestSendsPerHour = 2;
        Guid limited = Guid.NewGuid();
        MarketingCampaignAdminController l = new(Service(db), new FakeUser(limited, true));
        await l.TestSend(id, CancellationToken.None);
        await l.TestSend(id, CancellationToken.None);
        (await l.TestSend(id, CancellationToken.None)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
    }

    // ---------------------------------------------------------------- authority and refusal order

    [Fact]
    public async Task Only_a_platform_admin_may_use_any_action_and_disabled_is_404()
    {
        AddThree();
        Guid id = await TestedDraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();

        async Task<IActionResult[]> AllAsync(MarketingCampaignAdminController c) =>
        [
            await c.List(10, CancellationToken.None), c.Settings(), await c.Create(Body, CancellationToken.None),
            await c.Get(id, CancellationToken.None), await c.Update(id, Body, CancellationToken.None), await c.Delete(id, CancellationToken.None),
            await c.DryRun(id, CancellationToken.None), await c.TestSend(id, CancellationToken.None),
            await c.Send(id, new CampaignSendRequest(3), CancellationToken.None), await c.RefreshResults(id, CancellationToken.None),
        ];

        (await AllAsync(Controller(db, admin: false))).Should().AllBeOfType<ForbidResult>();
        _opt.Enabled = false;
        (await AllAsync(Controller(db))).Should().AllBeOfType<NotFoundResult>();
        _brevo.Calls.Should().BeEmpty();
        Load(id).Status.Should().Be(MarketingCampaignStatus.Draft);
    }

    [Fact]
    public async Task Send_refusals_follow_the_documented_order()
    {
        AddThree();
        Guid id = await DraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingCampaignAdminController c = Controller(db);
        async Task<(int Status, string Error)> Try(Guid cid, int confirm)
        {
            ObjectResult r = (ObjectResult)await c.Send(cid, new CampaignSendRequest(confirm), CancellationToken.None);
            return (r.StatusCode!.Value, JsonSerializer.Serialize(r.Value));
        }

        // 1. the flag comes before everything, even before the missing test send
        _opt.ListSendEnabled = false;
        (await Try(id, 3)).Should().Match<(int, string)>(r => r.Item1 == 403 && r.Item2.Contains("list_send_disabled"));
        _opt.ListSendEnabled = true;
        // 3. content errors before the test requirement
        _opt.FooterAddress = null;
        (await Try(id, 3)).Should().Match<(int, string)>(r => r.Item1 == 422 && r.Item2.Contains("footer_address_missing"));
        _opt.FooterAddress = Dummy;
        // 4. no test send for this content
        (await Try(id, 3)).Should().Match<(int, string)>(r => r.Item1 == 422 && r.Item2.Contains("test_send_required"));
        await Service(db).TestSendAsync(id, _admin, CancellationToken.None);
        // 5. wrong confirmed count
        (await Try(id, 2)).Should().Match<(int, string)>(r => r.Item1 == 409 && r.Item2.Contains("recipient_count_changed"));
        // 8. another active campaign
        Guid other = await TestedDraftAsync();
        (await c.Send(other, new CampaignSendRequest(3), CancellationToken.None)).Should().BeOfType<AcceptedResult>();
        (await Try(id, 3)).Should().Match<(int, string)>(r => r.Item1 == 409 && r.Item2.Contains("campaign_active"));
        // 2. not a draft
        (await Try(other, 3)).Should().Match<(int, string)>(r => r.Item1 == 409 && r.Item2.Contains("not_draft"));
        Load(other).Status.Should().Be(MarketingCampaignStatus.Queued);
        Load(other).ConfirmedRecipientCount.Should().Be(3);
        _brevo.Sends.Should().BeEmpty("a request only queues");
    }

    [Fact]
    public async Task Send_with_no_recipients_is_refused_after_the_count_check()
    {
        Guid id = await TestedDraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        ObjectResult r = (ObjectResult)await Controller(db).Send(id, new CampaignSendRequest(0), CancellationToken.None);
        r.StatusCode.Should().Be(422);
        JsonSerializer.Serialize(r.Value).Should().Contain("no_recipients");
    }

    [Fact]
    public async Task A_test_of_other_content_does_not_count()
    {
        AddThree();
        Guid id = await TestedDraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        await db.MarketingCampaigns.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Html, "<p>changed after the test</p>"));
        ObjectResult r = (ObjectResult)await Controller(db).Send(id, new CampaignSendRequest(3), CancellationToken.None);
        JsonSerializer.Serialize(r.Value).Should().Contain("test_send_required");
    }

    // ---------------------------------------------------------------- the pipeline

    [Fact]
    public async Task Pipeline_syncs_the_list_to_exactly_the_sendable_set_and_sends_once()
    {
        AddThree();
        Add("out@example.test", MarketingConsentStatus.Withdrawn, synced: true);
        _brevo.Lists[10].Add("out@example.test");
        _brevo.Lists[10].Add("junk@example.test");
        Guid id = await QueuedAsync();

        MarketingCampaign done = await RunAsync(id);

        done.Status.Should().Be(MarketingCampaignStatus.Sending);
        done.FinalRecipientCount.Should().Be(3);
        done.DispatchStartedAt.Should().NotBeNull();
        done.BrevoCampaignId.Should().NotBeNull();
        Listed(10).Should().Equal("a@example.test", "b@example.test", "c@example.test");
        _brevo.Sends.Should().HaveCount(1);
        _brevo.Calls.Single(c => c.Method == "DELETE" && c.Path.StartsWith("contacts/out", StringComparison.Ordinal)).Should().NotBeNull();
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.AsNoTracking().Single(c => c.Email == "out@example.test").BrevoSyncedAt.Should().BeNull();
        db.MarketingConsents.AsNoTracking().Where(c => c.BrevoSyncedAt != null).Select(c => c.Email).OrderBy(e => e).ToList()
            .Should().Equal("a@example.test", "b@example.test", "c@example.test");
        using JsonDocument created = JsonDocument.Parse(_brevo.Calls.Single(c => c.Path == "emailCampaigns").Body!);
        created.RootElement.GetProperty("recipients").GetProperty("listIds")[0].GetInt64().Should().Be(10);
        created.RootElement.GetProperty("htmlContent").GetString().Should().Contain("{{ unsubscribe }}")
            .And.Be(Service(db).BuildHtml(new CampaignContent(Body.Subject, Body.Html)), "the list send carries the same bytes as the preview");
        _brevo.Calls.Select(c => c.Path).ToList().FindIndex(p => p.EndsWith("/sendNow", StringComparison.Ordinal))
            .Should().BeGreaterThan(_brevo.Calls.FindIndex(c => c.Path == "emailCampaigns"));
    }

    [Fact]
    public async Task Pipeline_import_carries_names_and_marks_the_rows_pushed()
    {
        AddThree();
        using (InferenceApiDbContext seed = _h.NewDb())
        {
            MarketingConsent a = seed.MarketingConsents.Single(c => c.Email == "a@example.test");
            a.FirstName = "Maria";
            a.LastName = "Rossi";
            seed.SaveChanges();
        }

        _brevo.Attributes["c@example.test"] = new Dictionary<string, string> { ["FIRSTNAME"] = "Old", ["LASTNAME"] = "Name" };
        using (InferenceApiDbContext seed = _h.NewDb())
        {
            seed.MarketingConsents.Where(c => c.Email == "b@example.test")
                .ExecuteUpdate(s => s.SetProperty(c => c.BrevoPushAttempts, 3).SetProperty(c => c.BrevoPushErrorCode, "brevo_http_500"));
        }

        Guid id = await QueuedAsync();
        (await RunAsync(id)).Status.Should().Be(MarketingCampaignStatus.Sending);
        using (InferenceApiDbContext chk = _h.NewDb())
        {
            MarketingConsent b = chk.MarketingConsents.AsNoTracking().Single(c => c.Email == "b@example.test");
            b.BrevoPushAttempts.Should().Be(0);
            b.BrevoPushErrorCode.Should().BeNull();
        }

        _brevo.Attributes["a@example.test"].Should().Equal(new Dictionary<string, string> { ["FIRSTNAME"] = "Maria", ["LASTNAME"] = "Rossi" });
        _brevo.Attributes["b@example.test"].Should().BeEmpty("a contact with no name holds no attributes");
        _brevo.Attributes["c@example.test"].Should().BeEmpty("a name cleared locally is removed at Brevo by the import");
        using JsonDocument import = JsonDocument.Parse(_brevo.Calls.First(c => c.Path == "contacts/import").Body!);
        import.RootElement.TryGetProperty("emailBlacklist", out _).Should().BeFalse();
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.AsNoTracking().Where(c => c.BrevoPushedAt != null).Select(c => c.Email).OrderBy(e => e).ToList()
            .Should().Equal("a@example.test", "b@example.test", "c@example.test");
    }

    [Fact]
    public async Task Flag_off_means_no_brevo_call_of_any_kind()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _opt.ListSendEnabled = false;

        MarketingCampaign done = await RunAsync(id);

        done.Status.Should().Be(MarketingCampaignStatus.Failed);
        done.FailureCode.Should().Be("list_send_disabled");
        _brevo.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("import")]
    [InlineData("process_failed")]
    [InlineData("process_timeout")]
    [InlineData("list_contacts")]
    [InlineData("list_count")]
    [InlineData("delete_contact")]
    [InlineData("create_campaign")]
    public async Task Any_failing_step_means_no_sendNow(string failing)
    {
        AddThree();
        Add("gone@example.test", MarketingConsentStatus.Withdrawn, synced: true);
        _brevo.Existing.Add("gone@example.test");
        Guid id = await QueuedAsync();
        _opt.ProcessWaitSeconds = 0;
        switch (failing)
        {
            case "process_failed": _brevo.ProcessStatus = "failed"; break;
            case "process_timeout": _brevo.ProcessStatus = "in_process"; break;
            default:
                _brevo.Override = call => failing switch
                {
                    "remove" when call.Path.EndsWith("/contacts/remove", StringComparison.Ordinal) => FakeBrevo.Status(HttpStatusCode.InternalServerError),
                    "import" when call.Path == "contacts/import" => FakeBrevo.Status(HttpStatusCode.BadRequest),
                    "list_contacts" when call.Path.Contains("/contacts?limit", StringComparison.Ordinal) && _brevo.Calls.Count(c => c.Path.Contains("/contacts?limit", StringComparison.Ordinal)) > 1 => FakeBrevo.Status(HttpStatusCode.InternalServerError),
                    "list_count" when call.Path == "contacts/lists/10" => FakeBrevo.Status(HttpStatusCode.InternalServerError),
                    "delete_contact" when call.Method == "DELETE" && call.Path.StartsWith("contacts/", StringComparison.Ordinal) => FakeBrevo.Status(HttpStatusCode.InternalServerError),
                    "create_campaign" when call.Path == "emailCampaigns" => FakeBrevo.Status(HttpStatusCode.BadRequest),
                    _ => null,
                };
                break;
        }

        MarketingCampaign done = await RunAsync(id);

        done.Status.Should().Be(MarketingCampaignStatus.Failed);
        done.FailureCode.Should().NotBeNullOrEmpty();
        _brevo.Sends.Should().BeEmpty();
        done.DispatchStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_process_that_never_completes_fails_closed_with_a_stable_code()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _opt.ProcessWaitSeconds = 0;
        _brevo.ProcessStatus = "queued";
        (await RunAsync(id)).FailureCode.Should().Be("brevo_process_timeout");
        _brevo.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Membership_extra_missing_or_blocked_stops_the_run()
    {
        AddThree();
        Guid extra = await QueuedAsync();
        _brevo.ExtraOnImport.Add("stranger@example.test");
        (await RunAsync(extra)).FailureCode.Should().Be("list_mismatch");
        _brevo.Sends.Should().BeEmpty();

        _brevo.ExtraOnImport.Clear();
        _brevo.DropOnImport.Add("b@example.test");
        Guid missing = await QueuedAsync();
        (await RunAsync(missing)).FailureCode.Should().Be("list_mismatch");
        _brevo.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blocked_contact_stops_the_run_is_marked_locally_and_is_dropped_next_time()
    {
        AddThree();
        Guid first = await QueuedAsync();
        _brevo.Blocked.Add("b@example.test");

        (await RunAsync(first)).FailureCode.Should().Be("list_blocked_contact");
        _brevo.Sends.Should().BeEmpty();
        using (InferenceApiDbContext db = _h.NewDb())
        {
            MarketingConsent b = db.MarketingConsents.AsNoTracking().Single(c => c.Email == "b@example.test");
            b.Status.Should().Be(MarketingConsentStatus.Withdrawn, "a blocklist with an unknown reason is the most protective reading");
            b.Source.Should().Be(MarketingConsentSource.BrevoBlocklist);
        }

        _brevo.Blocked.Clear();
        Guid second = await QueuedAsync(confirm: 2);
        (await RunAsync(second)).Status.Should().Be(MarketingCampaignStatus.Sending);
        Listed(10).Should().Equal("a@example.test", "c@example.test");
    }

    [Fact]
    public async Task Brevo_unsubscribe_is_recorded_as_a_withdrawal_and_the_person_is_not_mailed()
    {
        AddThree();
        _brevo.Lists[10].Add("c@example.test");
        _brevo.ListUnsubscribed["c@example.test"] = [10];
        _brevo.Stats["c@example.test"] = ["user"];
        Guid id = await TestedDraftAsync();
        await using (InferenceApiDbContext qdb = _h.NewDb())
        {
            await Service(qdb).RequestSendAsync(id, 3, _admin, CancellationToken.None);
        }

        MarketingCampaign done = await RunAsync(id);

        using InferenceApiDbContext db = _h.NewDb();
        MarketingConsent c = db.MarketingConsents.AsNoTracking().Single(x => x.Email == "c@example.test");
        c.Status.Should().Be(MarketingConsentStatus.Withdrawn);
        c.Source.Should().Be(MarketingConsentSource.BrevoUnsubscribe);
        db.MarketingConsentEvents.AsNoTracking().Count(e => e.ConsentId == c.Id && e.Action == MarketingConsentAction.Withdrawn).Should().Be(1);
        done.Status.Should().Be(MarketingCampaignStatus.Sending, "a shrunk set is safe to send");
        done.FinalRecipientCount.Should().Be(2);
        Listed(10).Should().NotContain("c@example.test");
    }

    [Fact]
    public async Task A_set_larger_than_the_confirmed_count_stops_the_run()
    {
        AddThree();
        Guid id = await QueuedAsync();
        Add("late@example.test");
        (await RunAsync(id)).FailureCode.Should().Be("recipient_count_changed");
        _brevo.Writes.Should().BeEmpty("nothing is written to Brevo before the count check");
    }

    [Fact]
    public async Task A_withdrawal_after_the_read_back_stops_the_send()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.After = call =>
        {
            if (call.Path == "contacts/lists/10")
            {
                using InferenceApiDbContext db = _h.NewDb();
                db.MarketingConsents.Where(c => c.Email == "a@example.test").ExecuteUpdate(s => s.SetProperty(c => c.Status, MarketingConsentStatus.Withdrawn));
            }
        };

        MarketingCampaign done = await RunAsync(id);

        done.Status.Should().Be(MarketingCampaignStatus.Failed);
        done.FailureCode.Should().Be("list_changed");
        _brevo.Sends.Should().BeEmpty();
        _brevo.Calls.Should().NotContain(c => c.Path == "emailCampaigns");
    }

    [Fact]
    public async Task SendNow_is_called_at_most_once_and_an_unknown_outcome_is_SendUnknown()
    {
        AddThree();
        foreach (Func<FakeBrevo.Call, HttpResponseMessage?> inject in new Func<FakeBrevo.Call, HttpResponseMessage?>[]
        {
            call => call.Path.EndsWith("/sendNow", StringComparison.Ordinal) ? FakeBrevo.Status(HttpStatusCode.BadGateway) : null,
            call => call.Path.EndsWith("/sendNow", StringComparison.Ordinal) ? throw new HttpRequestException("connection reset for a@example.test") : null,
            call => call.Path.EndsWith("/sendNow", StringComparison.Ordinal) ? throw new TaskCanceledException("timeout") : null,
        })
        {
            Guid id = await QueuedAsync();
            _brevo.Calls.Clear();
            _brevo.Override = inject;

            MarketingCampaign done = await RunAsync(id);

            done.Status.Should().Be(MarketingCampaignStatus.SendUnknown);
            done.DispatchStartedAt.Should().NotBeNull();
            _brevo.Sends.Should().HaveCount(1, "sendNow is never retried");
            // a human checks Brevo: the row is terminal, not queued again
            await using InferenceApiDbContext db = _h.NewDb();
            (await Pipeline(db).ClaimAndRunAsync(CancellationToken.None)).Should().BeFalse();
            await db.MarketingCampaigns.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, MarketingCampaignStatus.Failed));
        }
    }

    [Fact]
    public async Task A_rejected_sendNow_is_Failed_with_brevos_code_and_the_campaign_is_deleted()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.Override = call => call.Path.EndsWith("/sendNow", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.PaymentRequired, "{\"code\":\"not_enough_credits\",\"message\":\"x@leak.test\"}")
            : null;

        MarketingCampaign done = await RunAsync(id);

        done.Status.Should().Be(MarketingCampaignStatus.Failed);
        done.FailureCode.Should().Be("brevo_not_enough_credits");
        _brevo.Sends.Should().HaveCount(1);
        _brevo.Calls.Last().Method.Should().Be("DELETE");
    }

    // ---------------------------------------------------------------- recovery, results

    [Fact]
    public async Task Restart_recovery_fails_stale_syncing_rows_and_marks_started_sends_unknown()
    {
        Guid plain = Guid.NewGuid(), dispatched = Guid.NewGuid(), fresh = Guid.NewGuid();
        await using (InferenceApiDbContext seed = _h.NewDb())
        {
            DateTime old = _h.Now.AddHours(-2);
            MarketingCampaign Row(Guid id, DateTime updated, DateTime? dispatch) => new()
            {
                Id = id,
                Subject = "s",
                Html = "<p>body body</p>",
                Status = MarketingCampaignStatus.Syncing,
                CreatedBy = _admin,
                CreatedAt = old,
                UpdatedAt = updated,
                DispatchStartedAt = dispatch,
            };
            seed.MarketingCampaigns.AddRange(Row(plain, old, null), Row(dispatched, old, old), Row(fresh, _h.Now, null));
            seed.SaveChanges();
        }

        await using InferenceApiDbContext db = _h.NewDb();
        await Pipeline(db).RecoverAsync(CancellationToken.None);

        Load(plain).Status.Should().Be(MarketingCampaignStatus.Failed);
        Load(plain).FailureCode.Should().Be("interrupted");
        Load(dispatched).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
        Load(fresh).Status.Should().Be(MarketingCampaignStatus.Syncing, "inside its lease a replica may still be running it; a later pass recovers it once the lease lapses");
        _brevo.Writes.Should().BeEmpty("recovery only reads the managed list back; it writes nothing at Brevo");
    }

    [Fact]
    public async Task Only_one_row_can_be_claimed_and_a_second_claim_finds_nothing()
    {
        AddThree();
        Guid id = await QueuedAsync();
        await using InferenceApiDbContext a = _h.NewDb();
        await using InferenceApiDbContext b = _h.NewDb();
        await a.MarketingCampaigns.Where(c => c.Id == id && c.Status == MarketingCampaignStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, MarketingCampaignStatus.Syncing));
        (await Pipeline(b).ClaimAndRunAsync(CancellationToken.None)).Should().BeFalse();
        _brevo.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_results_stores_statistics_and_moves_sent_campaigns_to_Sent()
    {
        AddThree();
        Guid id = await QueuedAsync();
        await RunAsync(id);
        await using InferenceApiDbContext db = _h.NewDb();

        IActionResult res = await Controller(db).RefreshResults(id, CancellationToken.None);

        res.Should().BeOfType<OkObjectResult>();
        MarketingCampaign c = Load(id);
        c.Status.Should().Be(MarketingCampaignStatus.Sent);
        c.StatsJson.Should().Contain("\"delivered\":3");
        c.StatsAt.Should().NotBeNull();
        (await Controller(db).RefreshResults(await DraftAsync(), CancellationToken.None)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
    }

    [Fact]
    public async Task Undeliverable_addresses_leave_the_sendable_set_and_the_export()
    {
        AddThree();
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await _h.NewService(db).MarkUndeliverableAsync("b@example.test", "hard_bounce", CancellationToken.None);
        }

        await using InferenceApiDbContext read = _h.NewDb();
        (await _h.NewService(read).SendableRecipientsAsync(CancellationToken.None)).Recipients.Select(r => r.Email)
            .Should().Equal("a@example.test", "c@example.test");
    }

    [Fact]
    public async Task Logs_never_carry_addresses_bodies_or_the_key()
    {
        _brevo.Override = _ => FakeBrevo.Status(HttpStatusCode.BadRequest, "{\"code\":\"bad\",\"message\":\"x@leak.test secret-key BODYTEXT\"}");
        BrevoCampaignException ex = (await ((Func<Task>)(() => Client().DeleteContactAsync("person@leak.test", CancellationToken.None))).Should().ThrowAsync<BrevoCampaignException>()).Which;
        ex.Code.Should().Be("brevo_bad");

        string all = ex.Message + string.Join("|", _clientLog.Entries.Select(e => e.Message));
        all.Should().Contain("rcpt=").And.Contain("status=400");
        all.Should().NotContain("leak.test").And.NotContain("secret-key").And.NotContain("BODYTEXT");

        _opt.BrevoCampaignApiKey = null;
        BrevoCampaignException blank = (await ((Func<Task>)(() => Client().GetListSubscriberCountAsync(10, CancellationToken.None))).Should().ThrowAsync<BrevoCampaignException>()).Which;
        blank.Code.Should().Be("brevo_key_missing");
    }

    [Fact]
    public void The_controller_has_no_action_that_sends_directly()
    {
        string[] actions = typeof(MarketingCampaignAdminController).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name).OrderBy(n => n).ToArray();
        actions.Should().Equal("Create", "Delete", "DryRun", "Duplicate", "Get", "List", "RefreshResults", "Resolve", "Send", "Settings", "TestSend", "Update");
    }
    // ---------------------------------------------------------------- never lose an unsubscribe

    [Theory]
    [InlineData(false, false, false, false, false, "Clear", null)]
    [InlineData(true, false, false, false, false, "Withdrawn", "BrevoBlocklist")]
    [InlineData(true, true, false, false, false, "Withdrawn", "EmailComplaint")]
    [InlineData(true, false, true, false, false, "Withdrawn", "BrevoUnsubscribe")]
    [InlineData(true, false, false, true, false, "Withdrawn", "BrevoUnsubscribe")]
    [InlineData(true, false, false, false, true, "Undeliverable", null)]
    [InlineData(true, true, true, false, true, "Withdrawn", "EmailComplaint")]
    [InlineData(true, false, true, false, true, "Withdrawn", "BrevoUnsubscribe")]
    public void Classifier_arms(bool black, bool complaint, bool user, bool admin, bool hard, string state, string? source)
    {
        BrevoContactInfo info = new(black, [], complaint, user || admin, hard);
        BrevoContactGuard.Decide(info, false, 10, out MarketingConsentSource src).ToString().Should().Be(state);
        if (source is not null) src.ToString().Should().Be(source);
        BrevoContactGuard.Decide(null, false, 10, out _).Should().Be(BrevoContactState.Absent);
        BrevoContactGuard.Decide(new BrevoContactInfo(false, [10], false, false, false), false, 10, out _).Should().Be(BrevoContactState.Withdrawn);
        BrevoContactGuard.Decide(new BrevoContactInfo(false, [11], false, false, false), false, 10, out _).Should().Be(BrevoContactState.Clear);
        BrevoContactGuard.Decide(null, true, 10, out MarketingConsentSource listOnly).Should().Be(BrevoContactState.Withdrawn);
        listOnly.Should().Be(MarketingConsentSource.BrevoBlocklist);
    }

    [Fact]
    public void No_no_op_erasure_drain_exists_and_only_the_outbox_implements_it()
    {
        System.Reflection.Assembly api = typeof(MarketingCampaignPipeline).Assembly;
        api.GetTypes().Where(t => typeof(IBrevoErasureDrain).IsAssignableFrom(t) && !t.IsInterface).Should().Equal(typeof(BrevoErasureOutbox));
        api.GetTypes().Should().NotContain(t => t.Name == "NoBrevoErasures");
    }

    private (string Email, Guid UserId) UnverifiedSynced()
    {
        Guid user = _h.AddUser("x@example.test", null);
        Add("x@example.test", userId: user, synced: true);
        return ("x@example.test", user);
    }

    [Fact]
    public async Task Unsubscribed_unverified_stale_contact_is_withdrawn_before_the_delete_and_never_mailed_after_verification()
    {
        Add("a@example.test");
        Add("b@example.test");
        (string x, Guid user) = UnverifiedSynced();
        _brevo.Blocked.Add(x);
        _brevo.Stats[x] = ["user"];
        string? statusAtDelete = null;
        int eventsAtDelete = -1;
        _brevo.Override = call =>
        {
            if (call.Method == "DELETE" && call.Path.StartsWith("contacts/x%40", StringComparison.Ordinal))
            {
                using InferenceApiDbContext d = _h.NewDb();
                MarketingConsent r = d.MarketingConsents.AsNoTracking().Single(c => c.Email == x);
                statusAtDelete = r.Status.ToString();
                eventsAtDelete = d.MarketingConsentEvents.Count(e => e.ConsentId == r.Id && e.Action == MarketingConsentAction.Withdrawn);
            }

            return null;
        };

        Guid first = await QueuedAsync(confirm: 2);
        (await RunAsync(first)).Status.Should().Be(MarketingCampaignStatus.Sending);
        statusAtDelete.Should().Be("Withdrawn", "the withdrawal is committed before the delete");
        eventsAtDelete.Should().Be(1);
        _brevo.Calls.Should().Contain(c => c.Method == "DELETE" && c.Path.StartsWith("contacts/x%40", StringComparison.Ordinal));

        // verified later, still mailed never
        using (InferenceApiDbContext vdb = _h.NewDb())
        {
            vdb.ApplicationUsers.Where(u => u.Id == user).ExecuteUpdate(s => s.SetProperty(u => u.EmailVerifiedAt, DateTime.UtcNow));
        }

        _brevo.Calls.Clear();
        using (InferenceApiDbContext cdb = _h.NewDb())
        {
            cdb.MarketingCampaigns.Where(c => c.Id == first).ExecuteUpdate(s => s.SetProperty(c => c.Status, MarketingCampaignStatus.Sent));
        }

        Guid second = await QueuedAsync(confirm: 2);
        (await RunAsync(second)).Status.Should().Be(MarketingCampaignStatus.Sending);
        Listed(10).Should().Equal("a@example.test", "b@example.test");
        using InferenceApiDbContext db = _h.NewDb();
        MarketingConsent row = db.MarketingConsents.AsNoTracking().Single(c => c.Email == x);
        row.Status.Should().Be(MarketingConsentStatus.Withdrawn);
        row.Source.Should().Be(MarketingConsentSource.BrevoUnsubscribe);
        row.BrevoSyncedAt.Should().BeNull();
        db.MarketingConsentEvents.Count(e => e.ConsentId == row.Id && e.Action == MarketingConsentAction.Withdrawn).Should().Be(1, "a repeat writes no second event");
    }

    [Fact]
    public async Task Stale_contact_read_failure_blocks_the_delete_and_fails_the_run_with_no_send()
    {
        Add("a@example.test");
        Add("b@example.test");
        (string x, _) = UnverifiedSynced();
        _brevo.Blocked.Add(x);
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("contacts/x%40", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.InternalServerError) : null;
        Guid id = await QueuedAsync(confirm: 2);

        (await RunAsync(id)).Status.Should().Be(MarketingCampaignStatus.Failed);
        _brevo.Sends.Should().BeEmpty();
        _brevo.Calls.Should().NotContain(c => c.Method == "DELETE" && c.Path.Contains("x%40", StringComparison.Ordinal));
        Row(x).BrevoSyncedAt.Should().NotBeNull("the contact stays");
        Row(x).Status.Should().Be(MarketingConsentStatus.OptedIn);
    }

    [Fact]
    public async Task Stale_contact_404_clears_the_sync_mark_without_a_delete()
    {
        Add("a@example.test");
        Add("b@example.test");
        (string x, _) = UnverifiedSynced();
        Guid id = await QueuedAsync(confirm: 2);
        (await RunAsync(id)).Status.Should().Be(MarketingCampaignStatus.Sending);
        Row(x).BrevoSyncedAt.Should().BeNull();
        _brevo.Calls.Should().NotContain(c => c.Method == "DELETE" && c.Path.Contains("x%40", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Classification_read_failure_on_the_managed_list_fails_the_run_with_no_send()
    {
        AddThree();
        _brevo.Lists[10].Add("b@example.test");
        _brevo.Blocked.Add("b@example.test");
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("contacts/b%40", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.BadGateway) : null;
        Guid id = await QueuedAsync();
        (await RunAsync(id)).Status.Should().Be(MarketingCampaignStatus.Failed);
        _brevo.Sends.Should().BeEmpty();
        Row("b@example.test").Status.Should().Be(MarketingConsentStatus.OptedIn);
    }

    [Theory]
    [InlineData("complaint", "EmailComplaint", "Withdrawn")]
    [InlineData("admin", "BrevoUnsubscribe", "Withdrawn")]
    [InlineData("hard", "SiteContact", "OptedIn")]
    public async Task Blocked_list_contact_is_classified_from_its_statistics(string stat, string source, string status)
    {
        AddThree();
        _brevo.Blocked.Add("b@example.test");
        _brevo.Stats["b@example.test"] = [stat];
        Guid id = await QueuedAsync();
        (await RunAsync(id)).FailureCode.Should().Be("list_blocked_contact");
        MarketingConsent b = Row("b@example.test");
        b.Status.ToString().Should().Be(status);
        b.Source.ToString().Should().Be(source);
        if (stat == "hard")
        {
            b.UndeliverableAt.Should().NotBeNull();
            b.UndeliverableReason.Should().Be("hard_bounce");
        }
        else
        {
            b.UndeliverableAt.Should().BeNull();
        }
    }

    [Fact]
    public async Task Repeated_classification_writes_one_event_and_logs_no_address()
    {
        AddThree();
        _brevo.Blocked.Add("b@example.test");
        _brevo.Stats["b@example.test"] = ["user"];
        for (int i = 0; i < 3; i++)
        {
            await using InferenceApiDbContext db = _h.NewDb();
            BrevoCampaignClient c = Client();
            BrevoContactGuard g = new(db, _h.NewService(db), c, Microsoft.Extensions.Options.Options.Create(_opt));
            (await g.ClassifyAsync("b@example.test", true, CancellationToken.None)).Should().Be(BrevoContactState.Withdrawn);
        }

        using InferenceApiDbContext check = _h.NewDb();
        Guid bid = check.MarketingConsents.Single(c => c.Email == "b@example.test").Id;
        check.MarketingConsentEvents.Count(e => e.ConsentId == bid && e.Action == MarketingConsentAction.Withdrawn).Should().Be(1);
        string.Join("|", _clientLog.Entries.Select(e => e.Message)).Should().NotContain("b@example.test");
    }

    [Fact]
    public async Task Pending_erasure_is_drained_by_the_run_and_a_failing_drain_fails_it_with_no_send()
    {
        AddThree();
        await using (InferenceApiDbContext sdb = _h.NewDb())
        {
            sdb.MarketingBrevoErasures.Add(new MarketingBrevoErasure { Id = Guid.NewGuid(), ProtectedEmail = new TestProtector().Protect("gone@example.test"), CreatedAt = DateTime.UtcNow });
            sdb.SaveChanges();
        }

        _brevo.Override = call => call.Method == "DELETE" && call.Path.StartsWith("contacts/gone", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.InternalServerError) : null;
        Guid first = await QueuedAsync();
        (await RunAsync(first)).FailureCode.Should().Be("erasure_pending");
        _brevo.Sends.Should().BeEmpty();

        _brevo.Override = null;
        Guid second = await QueuedAsync();
        (await RunAsync(second)).Status.Should().Be(MarketingCampaignStatus.Sending);
        _brevo.Calls.Should().Contain(c => c.Method == "DELETE" && c.Path.StartsWith("contacts/gone%40example.test", StringComparison.Ordinal));
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingBrevoErasures.Count().Should().Be(0);
    }

    // ---------------------------------------------------------------- security-verdict fixes

    private BrevoContactGuard Guard(InferenceApiDbContext db)
        => new(db, _h.NewService(db), Client(), Microsoft.Extensions.Options.Options.Create(_opt));

    private void SetConfirmed(string email, DateTime confirmedAt)
    {
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.Where(c => c.Email == email).ExecuteUpdate(s => s.SetProperty(c => c.ConfirmedAt, confirmedAt));
    }

    private void DeleteRow(string email)
    {
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.Where(c => c.Email == email).ExecuteDelete();
    }

    private static readonly DateTime BeforeEvent = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterEvent = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_brevo_http_client_registration_drops_the_default_logging_handlers()
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        Microsoft.Extensions.Configuration.IConfiguration config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        InferenceApiServiceCollectionExtensions.AddInferenceApiServices(services, config);
        using ServiceProvider sp = services.BuildServiceProvider();
        // RemoveAllLoggers is proven by behaviour: the built handler chain holds no logging handler (their types are internal, so by name).
        HttpMessageHandler? h = sp.GetRequiredService<System.Net.Http.IHttpMessageHandlerFactory>().CreateHandler(nameof(IBrevoCampaignClient));
        List<string> chain = [];
        for (; h is not null; h = (h as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(h.GetType().Name);
        }

        chain.Should().NotContain(n => n.Contains("Logging"), "the contact URLs carry the address");
    }

    [Fact]
    public async Task An_empty_list_id_binds_as_not_configured_and_does_not_stop_either_worker()
    {
        Microsoft.Extensions.Configuration.IConfiguration config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Marketing:Campaigns:BrevoListId"] = "",
                ["Marketing:Campaigns:BrevoTestListId"] = "",
            }).Build();
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.Configure<MarketingCampaignOptions>(config.GetSection(MarketingCampaignOptions.SectionName));
        using ServiceProvider sp = services.BuildServiceProvider();
        MarketingCampaignOptions bound = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MarketingCampaignOptions>>().Value;
        bound.BrevoListId.Should().Be(0);
        bound.BrevoTestListId.Should().Be(0);
        bound.Enabled.Should().BeFalse();
        bound.ListSendEnabled.Should().BeFalse();

        var opts = Microsoft.Extensions.Options.Options.Create(bound);
        IServiceScopeFactory scopes = sp.GetRequiredService<IServiceScopeFactory>();
        using BrevoErasureWorker erasure = new(scopes, opts, new RecordingLogger<BrevoErasureWorker>());
        using MarketingCampaignWorker campaigns = new(scopes, opts, new RecordingLogger<MarketingCampaignWorker>());
        // The erasure worker no longer ends when campaigns are disabled (it must never strand an erasure); it waits and
        // stops cleanly. The campaign worker still ends quietly.
        await erasure.StartAsync(CancellationToken.None);
        erasure.ExecuteTask!.IsCompleted.Should().BeFalse("the erasure worker keeps running while campaigns are disabled");
        await erasure.StopAsync(CancellationToken.None);
        erasure.ExecuteTask!.IsFaulted.Should().BeFalse();

        await campaigns.StartAsync(CancellationToken.None);
        await campaigns.ExecuteTask!;
        campaigns.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue("campaigns are disabled, so the worker ends quietly");
        await campaigns.StopAsync(CancellationToken.None);

        MarketingCampaignOptions set = new() { BrevoListId = 7 };
        set.BrevoListId.Should().Be(7);
    }

    [Fact]
    public async Task A_row_purged_before_the_write_ahead_flag_fails_the_run_with_nothing_imported()
    {
        AddThree();
        Add("gone@example.test", status: MarketingConsentStatus.Withdrawn, synced: true);
        Guid id = await QueuedAsync();
        _brevo.Override = call =>
        {
            if (call.Method == "GET" && call.Path.StartsWith("contacts/gone%40", StringComparison.Ordinal)) DeleteRow("b@example.test");
            return null;
        };
        (await RunAsync(id)).FailureCode.Should().Be("consent_changed");
        _brevo.Count("contacts/import").Should().Be(0);
        _brevo.Count("contacts/remove").Should().Be(0);
        _brevo.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_purged_between_the_flag_and_the_import_gets_an_erasure_row_with_its_address()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.After = call =>
        {
            if (call.Path == "contacts/import" && call.Body!.Contains("b@example.test", StringComparison.Ordinal)) DeleteRow("b@example.test");
        };
        MarketingCampaign done = await RunAsync(id);
        done.Status.Should().Be(MarketingCampaignStatus.Failed);
        _brevo.Sends.Should().BeEmpty();
        using InferenceApiDbContext db = _h.NewDb();
        MarketingBrevoErasure[] rows = db.MarketingBrevoErasures.AsNoTracking().ToArray();
        rows.Should().ContainSingle();
        new TestProtector().TryUnprotect(rows[0].ProtectedEmail, out string address).Should().BeTrue();
        address.Should().Be("b@example.test");
    }

    [Fact]
    public async Task A_process_timeout_leaves_the_flag_set_so_the_sweep_can_delete_the_contacts()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _opt.ProcessWaitSeconds = 0;
        _brevo.ProcessStatus = "queued";
        (await RunAsync(id)).FailureCode.Should().Be("brevo_process_timeout");
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.AsNoTracking().Select(c => c.BrevoSyncedAt).ToArray().Should().OnlyContain(v => v != null);
    }

    [Fact]
    public async Task An_erasure_that_appears_after_step_one_stops_the_run_before_the_send()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.After = call =>
        {
            if (call.Method == "GET" && call.Path == "contacts/lists/10")
            {
                using InferenceApiDbContext d = _h.NewDb();
                d.MarketingBrevoErasures.Add(new MarketingBrevoErasure { Id = Guid.NewGuid(), ProtectedEmail = new TestProtector().Protect("z@example.test"), CreatedAt = DateTime.UtcNow });
                d.SaveChanges();
            }
        };
        (await RunAsync(id)).FailureCode.Should().Be("erasure_pending");
        _brevo.Sends.Should().BeEmpty();
        _brevo.Count("emailCampaigns").Should().Be(0);
    }

    // ---------------------------------------------------------------- round 2 (T-012)

    private sealed class BeforeCommand(Func<System.Data.Common.DbCommand, Task> hook) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await hook(command);
            return result;
        }
    }

    private sealed class FixedMonitor(MarketingCampaignOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<MarketingCampaignOptions>
    {
        public MarketingCampaignOptions CurrentValue { get; set; } = value;
        public MarketingCampaignOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MarketingCampaignOptions, string?> listener) => null;
    }

    private DateTime? SyncedAt(string email)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsents.AsNoTracking().Where(c => c.Email == email).Select(c => c.BrevoSyncedAt).SingleOrDefault();
    }

    // 1. test recipient

    [Fact]
    public async Task Test_send_flags_an_existing_consent_row_before_the_import()
    {
        Add("owner@example.test");
        Guid id = await DraftAsync();
        DateTime? atImport = null;
        _brevo.Override = call =>
        {
            if (call.Path == "contacts/import") atImport = SyncedAt("owner@example.test");
            return null;
        };
        await using InferenceApiDbContext db = _h.NewDb();
        await Service(db).TestSendAsync(id, _admin, CancellationToken.None);
        atImport.Should().NotBeNull("the write-ahead flag is set before the test recipient is imported");
        _brevo.Count("contacts/import").Should().Be(1);
    }

    [Fact]
    public async Task Test_send_to_an_address_without_a_consent_row_creates_none()
    {
        Guid id = await DraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        await Service(db).TestSendAsync(id, _admin, CancellationToken.None);
        _brevo.Count("contacts/import").Should().Be(1);
        using InferenceApiDbContext check = _h.NewDb();
        check.MarketingConsents.Should().BeEmpty("the operator's own address is configuration, not a consent row");
    }

    // 2. re-read before sendNow

    [Fact]
    public async Task A_withdrawal_landing_after_the_campaign_is_created_stops_the_send()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.After = call =>
        {
            if (call is { Method: "POST", Path: "emailCampaigns" })
            {
                using InferenceApiDbContext d = _h.NewDb();
                d.MarketingConsents.Where(c => c.Email == "c@example.test").ExecuteUpdate(s => s.SetProperty(c => c.Status, MarketingConsentStatus.Withdrawn));
            }
        };
        MarketingCampaign done = await RunAsync(id);
        done.Status.Should().Be(MarketingCampaignStatus.Failed);
        done.FailureCode.Should().Be("list_changed");
        _brevo.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task A_purge_landing_after_the_campaign_is_created_stops_the_send()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.After = call =>
        {
            if (call is { Method: "POST", Path: "emailCampaigns" }) DeleteRow("b@example.test");
        };
        MarketingCampaign done = await RunAsync(id);
        done.FailureCode.Should().Be("list_changed");
        _brevo.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task A_staged_erasure_landing_after_the_campaign_is_created_stops_the_send()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.After = call =>
        {
            if (call is { Method: "POST", Path: "emailCampaigns" })
            {
                using InferenceApiDbContext d = _h.NewDb();
                d.MarketingBrevoErasures.Add(new MarketingBrevoErasure { Id = Guid.NewGuid(), ProtectedEmail = new TestProtector().Protect("z@example.test"), CreatedAt = DateTime.UtcNow });
                d.SaveChanges();
            }
        };
        MarketingCampaign done = await RunAsync(id);
        done.FailureCode.Should().Be("erasure_pending");
        _brevo.Sends.Should().BeEmpty();
    }

    // 3. erasure worker

    private BrevoErasureWorker ErasureWorker(MarketingCampaignOptions o)
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddLogging();
        services.AddScoped(_ => _h.NewDb());
        services.AddScoped(sp => _h.NewService(sp.GetRequiredService<InferenceApiDbContext>()));
        services.AddSingleton<IBrevoCampaignClient>(_ => new BrevoCampaignClient(
            new HttpClient(_brevo), Microsoft.Extensions.Options.Options.Create(o), MailLogRef.Ephemeral(), _clientLog));
        services.AddSingleton<ReelBolt.Shared.Inference.ISecretProtector>(new TestProtector());
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(o));
        services.AddScoped<BrevoContactGuard>();
        services.AddScoped<BrevoErasureOutbox>();
        ServiceProvider sp = services.BuildServiceProvider();
        _disposables.Add(sp);
        return new BrevoErasureWorker(sp.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(o), new RecordingLogger<BrevoErasureWorker>());
    }

    private void StageErasure(string email)
    {
        using InferenceApiDbContext d = _h.NewDb();
        d.MarketingBrevoErasures.Add(new MarketingBrevoErasure { Id = Guid.NewGuid(), ProtectedEmail = new TestProtector().Protect(email), CreatedAt = DateTime.UtcNow });
        d.SaveChanges();
    }

    private int ErasureRows()
    {
        using InferenceApiDbContext d = _h.NewDb();
        return d.MarketingBrevoErasures.Count();
    }

    [Fact]
    public async Task The_erasure_worker_drains_while_campaigns_are_disabled_when_a_key_is_set()
    {
        _opt.Enabled = false;
        StageErasure("gone@example.test");
        await ErasureWorker(_opt).PassAsync(CancellationToken.None);
        ErasureRows().Should().Be(0);
        _brevo.Calls.Count(c => c.Method == "DELETE").Should().Be(1);
    }

    [Fact]
    public async Task The_erasure_worker_is_inert_with_a_blank_key_and_leaves_rows()
    {
        _opt.Enabled = false;
        _opt.BrevoCampaignApiKey = "";
        StageErasure("gone@example.test");
        await ErasureWorker(_opt).PassAsync(CancellationToken.None);
        ErasureRows().Should().Be(1);
        _brevo.Calls.Should().BeEmpty();
    }

    // 4. lease

    [Fact]
    public async Task A_run_that_outlives_its_lease_and_loses_the_row_aborts_and_never_sends()
    {
        AddThree();
        Guid id = await QueuedAsync();
        DateTime? claimedByOther = null;
        _brevo.After = call =>
        {
            if (call.Path == "contacts/import" && claimedByOther is null)
            {
                // the lease expires, recovery fails the row, it is queued again and a second worker claims it
                _h.Clock.Advance(TimeSpan.FromMinutes(40));
                using InferenceApiDbContext d = _h.NewDb();
                Pipeline(d).RecoverAsync(CancellationToken.None).GetAwaiter().GetResult();
                Load(id).FailureCode.Should().Be("interrupted");
                DateTime other = DateTime.SpecifyKind(_h.Now, DateTimeKind.Utc).AddTicks(-(_h.Now.Ticks % 10)).AddMinutes(1);
                claimedByOther = other;
                d.MarketingCampaigns.Where(c => c.Id == id).ExecuteUpdate(s => s
                    .SetProperty(c => c.Status, MarketingCampaignStatus.Syncing).SetProperty(c => c.FailureCode, (string?)null).SetProperty(c => c.UpdatedAt, other));
            }
        };
        await using InferenceApiDbContext db = _h.NewDb();
        (await Pipeline(db).ClaimAndRunAsync(CancellationToken.None)).Should().BeTrue();
        MarketingCampaign row = Load(id);
        row.Status.Should().Be(MarketingCampaignStatus.Syncing, "the first run did not overwrite the run that owns the row now");
        row.FailureCode.Should().BeNull();
        row.UpdatedAt.Should().Be(claimedByOther!.Value);
        _brevo.Sends.Should().BeEmpty();
        _brevo.Count("emailCampaigns").Should().Be(0);
    }

    // 5. 404 only counts with Brevo's own body

    [Theory]
    [InlineData("<html>404 nginx</html>")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("{\"code\":\"something_else\"}")]
    public async Task A_404_without_brevos_not_found_body_is_an_error_for_read_and_delete(string body)
    {
        _brevo.Override = call => call.Path.StartsWith("contacts/", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.NotFound, body) : null;
        BrevoCampaignClient client = Client();
        (await FluentActions.Awaiting(() => client.GetContactAsync("x@example.test", CancellationToken.None)).Should().ThrowAsync<BrevoCampaignException>())
            .Which.Status.Should().Be(404);
        (await FluentActions.Awaiting(() => client.DeleteContactAsync("x@example.test", CancellationToken.None)).Should().ThrowAsync<BrevoCampaignException>())
            .Which.Status.Should().Be(404);
    }

    [Fact]
    public async Task A_404_with_brevos_not_found_body_is_absent_and_erased()
    {
        BrevoCampaignClient client = Client();
        (await client.GetContactAsync("nobody@example.test", CancellationToken.None)).Should().BeNull();
        await client.DeleteContactAsync("nobody@example.test", CancellationToken.None);
    }

    [Fact]
    public async Task A_proxy_404_keeps_the_erasure_row_for_a_retry()
    {
        StageErasure("gone@example.test");
        _brevo.Override = call => call.Method == "DELETE" ? FakeBrevo.Status(HttpStatusCode.NotFound, "<html>nginx</html>") : null;
        await ErasureWorker(_opt).PassAsync(CancellationToken.None);
        ErasureRows().Should().Be(1);
    }

    // 6. test-hash TOCTOU

    [Fact]
    public async Task An_edit_landing_between_the_checks_and_the_queue_update_does_not_queue_the_old_content()
    {
        AddThree();
        Guid id = await TestedDraftAsync();
        bool edited = false;
        BeforeCommand interceptor = new(async cmd =>
        {
            if (!edited && cmd.CommandText.Contains("confirmed_recipient_count", StringComparison.Ordinal))
            {
                edited = true;
                await using InferenceApiDbContext other = _h.NewDb();
                await Service(other).UpdateAsync(id, new CampaignContent("Edited", "<p>edited body</p>"), CancellationToken.None);
            }
        });
        await using InferenceApiDbContext db = _h.NewDb(interceptor);
        CampaignRefusal refusal = (await FluentActions.Awaiting(() => Service(db).RequestSendAsync(id, 3, _admin, CancellationToken.None))
            .Should().ThrowAsync<CampaignRefusal>()).Which;
        refusal.Code.Should().Be("test_send_required");
        Load(id).Status.Should().Be(MarketingCampaignStatus.Draft);
        edited.Should().BeTrue();
    }

    // 7. concurrent send

    [Fact]
    public async Task A_unique_index_violation_on_queueing_is_409_campaign_active()
    {
        AddThree();
        Guid id = await TestedDraftAsync();
        BeforeCommand interceptor = new(cmd => cmd.CommandText.Contains("confirmed_recipient_count", StringComparison.Ordinal)
            ? throw new DbUpdateException("duplicate key value violates unique constraint",
                new Npgsql.PostgresException("duplicate key", "ERROR", "ERROR", "23505", constraintName: MarketingCampaignService.ActiveIndexName))
            : Task.CompletedTask);
        await using InferenceApiDbContext db = _h.NewDb(interceptor);
        MarketingCampaignAdminController c = new(Service(db), new FakeUser(_admin, true));
        ObjectResult result = (ObjectResult)await c.Send(id, new CampaignSendRequest(3), CancellationToken.None);
        result.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(result.Value).Should().Contain("campaign_active");
    }

    [Fact]
    public void A_postgres_unique_violation_is_recognised_even_when_wrapped()
    {
        Npgsql.PostgresException pg = new("duplicate key", "ERROR", "ERROR", "23505", constraintName: MarketingCampaignService.ActiveIndexName);
        MarketingCampaignService.IsActiveIndexViolation(new InvalidOperationException("wrapped", pg)).Should().BeTrue();
        MarketingCampaignService.IsActiveIndexViolation(new InvalidOperationException("other")).Should().BeFalse();
        // another index, no index named, another SQLSTATE, a bare DbUpdateException: none of them means "a campaign is active"
        MarketingCampaignService.IsActiveIndexViolation(new Npgsql.PostgresException("dup", "ERROR", "ERROR", "23505", constraintName: "ix_other")).Should().BeFalse();
        MarketingCampaignService.IsActiveIndexViolation(new Npgsql.PostgresException("dup", "ERROR", "ERROR", "23505")).Should().BeFalse();
        MarketingCampaignService.IsActiveIndexViolation(new Npgsql.PostgresException("dup", "ERROR", "ERROR", "23503", constraintName: MarketingCampaignService.ActiveIndexName)).Should().BeFalse();
        MarketingCampaignService.IsActiveIndexViolation(new DbUpdateException("some other failure")).Should().BeFalse();
    }

    // 8. SendUnknown

    private Guid AddSendUnknown()
    {
        Guid id = Guid.NewGuid();
        using InferenceApiDbContext d = _h.NewDb();
        d.MarketingCampaigns.Add(new MarketingCampaign
        {
            Id = id,
            Subject = "Old",
            Html = "<p>old body</p>",
            Status = MarketingCampaignStatus.SendUnknown,
            FailureCode = "send_unknown",
            CreatedBy = _admin,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            BrevoCampaignId = 500,
            DispatchStartedAt = DateTime.UtcNow,
        });
        d.SaveChanges();
        return id;
    }

    [Fact]
    public async Task An_unresolved_SendUnknown_refuses_a_new_send_with_409_unresolved_send()
    {
        AddThree();
        Guid id = await TestedDraftAsync();
        AddSendUnknown();
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingCampaignAdminController c = Controller(db);
        ObjectResult result = (ObjectResult)await c.Send(id, new CampaignSendRequest(3), CancellationToken.None);
        result.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(result.Value).Should().Contain("unresolved_send");
        Load(id).Status.Should().Be(MarketingCampaignStatus.Draft);
    }

    [Theory]
    [InlineData("sent", MarketingCampaignStatus.Sent, null)]
    [InlineData("in_process", MarketingCampaignStatus.Sending, null)]
    [InlineData("queued", MarketingCampaignStatus.Sending, null)]
    [InlineData("draft", MarketingCampaignStatus.SendUnknown, "send_unknown")]
    [InlineData("suspended", MarketingCampaignStatus.SendUnknown, "send_unknown")]
    [InlineData("archive", MarketingCampaignStatus.SendUnknown, "send_unknown")]
    public async Task Refresh_results_resolves_SendUnknown_from_brevos_status(string brevoStatus, MarketingCampaignStatus expected, string? code)
    {
        Guid id = AddSendUnknown();
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("emailCampaigns/", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.OK, $"{{\"status\":\"{brevoStatus}\"}}") : null;
        await using InferenceApiDbContext db = _h.NewDb();
        await Service(db).RefreshResultsAsync(id, CancellationToken.None);
        MarketingCampaign row = Load(id);
        row.Status.Should().Be(expected);
        row.FailureCode.Should().Be(code);
    }

    [Fact]
    public async Task A_transport_failure_while_resolving_SendUnknown_leaves_it()
    {
        Guid id = AddSendUnknown();
        _brevo.Override = call => call.Method == "GET" ? throw new HttpRequestException("down") : null;
        await using InferenceApiDbContext db = _h.NewDb();
        await FluentActions.Awaiting(() => Service(db).RefreshResultsAsync(id, CancellationToken.None)).Should().ThrowAsync<BrevoCampaignException>();
        Load(id).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
    }

    // 9. ListSendEnabled through the monitor

    [Fact]
    public async Task The_dispatch_recheck_reads_the_options_monitor()
    {
        AddThree();
        Guid id = await QueuedAsync();
        FixedMonitor monitor = new(new MarketingCampaignOptions { ListSendEnabled = true });
        _brevo.After = call =>
        {
            // the source reloads while the run is in flight
            if (call is { Method: "POST", Path: "emailCampaigns" }) monitor.CurrentValue = new MarketingCampaignOptions { ListSendEnabled = false };
        };
        await using InferenceApiDbContext db = _h.NewDb();
        BrevoCampaignClient client = Client();
        MarketingConsentService svc = _h.NewService(db);
        BrevoContactGuard guard = new(db, svc, client, Microsoft.Extensions.Options.Options.Create(_opt));
        BrevoErasureOutbox drain = new(db, svc, guard, client, new TestProtector(), Microsoft.Extensions.Options.Options.Create(_opt), new RecordingLogger<BrevoErasureOutbox>(), _h.Clock);
        MarketingCampaignPipeline pipeline = new(db, svc, Service(db), client, drain, guard, Microsoft.Extensions.Options.Options.Create(_opt),
            new RecordingLogger<MarketingCampaignPipeline>(), _h.Clock, new TestProtector(), monitor);
        (await pipeline.ClaimAndRunAsync(CancellationToken.None)).Should().BeTrue();
        Load(id).FailureCode.Should().Be("list_send_disabled");
        _brevo.Sends.Should().BeEmpty();
    }

    // 10. preview shows the footer

    private static JsonElement Json(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    [Fact]
    public async Task Get_returns_the_exact_html_that_would_go_to_brevo_with_the_footer()
    {
        Guid id = await DraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        JsonElement view = Json(((OkObjectResult)await Controller(db).Get(id, CancellationToken.None)).Value!);
        string expected = Service(db).BuildHtml(new CampaignContent(Body.Subject, Body.Html));
        view.GetProperty("previewHtml").GetString().Should().Be(expected);
        expected.Should().Contain("{{ unsubscribe }}").And.Contain(Dummy);
        view.GetProperty("previewError").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Get_returns_a_null_preview_with_the_error_code_when_the_footer_cannot_render()
    {
        Guid id = await DraftAsync();
        _opt.FooterAddress = null;
        await using InferenceApiDbContext db = _h.NewDb();
        JsonElement view = Json(((OkObjectResult)await Controller(db).Get(id, CancellationToken.None)).Value!);
        view.GetProperty("previewHtml").ValueKind.Should().Be(JsonValueKind.Null);
        view.GetProperty("previewError").GetString().Should().Be("footer_address_missing");
    }

    private async Task<MarketingConsent> StaleDeleteAsync(string email, DateTime confirmedAt)
    {
        Add(email, synced: true);
        SetConfirmed(email, confirmedAt);
        await using InferenceApiDbContext db = _h.NewDb();
        await Guard(db).DeleteStaleAsync(email, CancellationToken.None);
        _brevo.Calls.Should().Contain(c => c.Method == "DELETE");
        return Row(email);
    }

    [Theory]
    [InlineData("user", MarketingConsentSource.BrevoUnsubscribe)]
    [InlineData("admin", MarketingConsentSource.BrevoUnsubscribe)]
    [InlineData("complaint", MarketingConsentSource.EmailComplaint)]
    public async Task A_stale_contact_with_unsubscribe_or_complaint_statistics_and_no_flag_is_withdrawn(string stat, MarketingConsentSource source)
    {
        _brevo.Existing.Add("s@example.test");
        _brevo.Stats["s@example.test"] = [stat];
        MarketingConsent row = await StaleDeleteAsync("s@example.test", BeforeEvent);
        row.Status.Should().Be(MarketingConsentStatus.Withdrawn);
        row.Source.Should().Be(source);
    }

    [Fact]
    public async Task Statistics_older_than_the_latest_confirmation_do_not_withdraw_a_genuine_re_opt_in()
    {
        _brevo.Existing.Add("s@example.test");
        _brevo.Stats["s@example.test"] = ["user", "complaint"];
        (await StaleDeleteAsync("s@example.test", AfterEvent)).Status.Should().Be(MarketingConsentStatus.OptedIn);
    }

    [Fact]
    public async Task Statistics_whose_time_cannot_be_compared_withdraw()
    {
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("contacts/s%40", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.OK,
                "{\"email\":\"s@example.test\",\"emailBlacklisted\":false,\"listUnsubscribed\":[],\"statistics\":{\"unsubscriptions\":{\"userUnsubscription\":[{\"campaignId\":1}],\"adminUnsubscription\":[]}}}")
            : null;
        (await StaleDeleteAsync("s@example.test", AfterEvent)).Status.Should().Be(MarketingConsentStatus.Withdrawn);
    }

    [Fact]
    public async Task A_flagged_contact_is_withdrawn_even_when_its_statistics_predate_the_confirmation()
    {
        _brevo.Blocked.Add("s@example.test");
        _brevo.Stats["s@example.test"] = ["user"];
        (await StaleDeleteAsync("s@example.test", AfterEvent)).Status.Should().Be(MarketingConsentStatus.Withdrawn);
    }

    [Fact]
    public async Task A_malformed_success_on_a_contact_list_or_process_read_throws_and_never_reads_as_clean_or_empty()
    {
        foreach (string body in new[] { "not json", "", "[]", "{}" })
        {
            _brevo.Override = call => call.Method == "GET" ? FakeBrevo.Status(HttpStatusCode.OK, body) : null;
            BrevoCampaignClient client = Client();
            (await FluentActions.Awaiting(() => client.GetContactAsync("a@example.test", CancellationToken.None)).Should()
                .ThrowAsync<BrevoCampaignException>()).Which.Code.Should().Be("brevo_bad_response");
            await FluentActions.Awaiting(() => client.GetListContactsAsync(10, 0, 500, CancellationToken.None)).Should().ThrowAsync<BrevoCampaignException>();
            await FluentActions.Awaiting(() => client.GetProcessStatusAsync(5, CancellationToken.None)).Should().ThrowAsync<BrevoCampaignException>();
            await FluentActions.Awaiting(() => client.GetListSubscriberCountAsync(10, CancellationToken.None)).Should().ThrowAsync<BrevoCampaignException>();
        }
    }

    [Fact]
    public async Task A_syncing_row_left_by_a_restart_inside_the_lease_is_recovered_on_a_later_pass()
    {
        Guid id = Guid.NewGuid();
        await using (InferenceApiDbContext seed = _h.NewDb())
        {
            seed.MarketingCampaigns.Add(new MarketingCampaign
            {
                Id = id,
                Subject = "s",
                Html = "<p>body body</p>",
                Status = MarketingCampaignStatus.Syncing,
                CreatedBy = _admin,
                CreatedAt = _h.Now,
                UpdatedAt = _h.Now.AddMinutes(-10),
            });
            seed.SaveChanges();
        }

        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddScoped(_ => Pipeline(_h.NewDb()));
        using ServiceProvider sp = services.BuildServiceProvider();
        using MarketingCampaignWorker worker = new(
            sp.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(_opt), new RecordingLogger<MarketingCampaignWorker>());
        await worker.RecoverAndRunAsync(CancellationToken.None);
        Load(id).Status.Should().Be(MarketingCampaignStatus.Syncing, "still inside the lease");
        _h.Clock.Advance(TimeSpan.FromMinutes(25));
        await worker.RecoverAndRunAsync(CancellationToken.None);
        Load(id).Status.Should().Be(MarketingCampaignStatus.Failed);
        Load(id).FailureCode.Should().Be("interrupted");
    }

    [Theory]
    [InlineData("<p>hello there</p><!-- never closed", true)]
    [InlineData("<p>hello there</p><style>p{color:red}", true)]
    [InlineData("<p>hello there</p><textarea>", true)]
    [InlineData("<p>hello there</p><frameset><frame src=x></frameset>", false)]
    public async Task Html_that_swallows_the_footer_is_still_accepted_but_one_without_a_body_is_refused(string html, bool accepted)
    {
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingCampaignService svc = Service(db);
        if (accepted) svc.Validate(new CampaignContent("Hello", html)).Should().NotContain("html_no_body");
        else svc.Validate(new CampaignContent("Hello", "<html><frameset><frame src=x></frameset></html>")).Should().Contain("html_no_body");
        if (!accepted)
        {
            (await FluentActions.Awaiting(() => svc.CreateAsync(new CampaignContent("Hello", "<html><frameset><frame src=x></frameset></html>"), _admin, CancellationToken.None)).Should()
                .ThrowAsync<CampaignRefusal>()).Which.Code.Should().Be("html_no_body");
        }
    }

    // ---------------------------------------------------------------- QA fixes (T-013)

    private MarketingCampaignWorker CampaignWorker()
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddScoped(_ => Pipeline(_h.NewDb()));
        ServiceProvider sp = services.BuildServiceProvider();
        _disposables.Add(sp);
        return new MarketingCampaignWorker(
            sp.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(_opt), new RecordingLogger<MarketingCampaignWorker>());
    }

    private Guid SeedRow(MarketingCampaignStatus status, DateTime? updated = null, DateTime? dispatch = null, long? brevoId = null, string subject = "Hello", string html = "<p>hello there</p>")
    {
        Guid id = Guid.NewGuid();
        using InferenceApiDbContext d = _h.NewDb();
        d.MarketingCampaigns.Add(new MarketingCampaign
        {
            Id = id,
            Subject = subject,
            Html = html,
            Status = status,
            CreatedBy = _admin,
            CreatedAt = _h.Now,
            UpdatedAt = updated ?? _h.Now,
            DispatchStartedAt = dispatch,
            BrevoCampaignId = brevoId,
            TestSentAt = _h.Now,
            TestContentHash = "x",
        });
        d.SaveChanges();
        return id;
    }

    // 1. restart during a send, end to end through the worker's recovery entry point

    [Theory]
    [InlineData(true, MarketingCampaignStatus.SendUnknown, "send_unknown")]
    [InlineData(false, MarketingCampaignStatus.Failed, "interrupted")]
    public async Task A_restart_during_a_send_is_settled_one_pass_after_the_lease_lapses_and_never_resends(bool dispatchStarted, MarketingCampaignStatus expected, string code)
    {
        DateTime touched = _h.Now.AddMinutes(-10);
        Guid id = SeedRow(MarketingCampaignStatus.Syncing, touched, dispatchStarted ? touched : null, dispatchStarted ? 500 : null);
        MarketingCampaignWorker worker = CampaignWorker();

        await worker.RecoverAndRunAsync(CancellationToken.None);
        Load(id).Status.Should().Be(MarketingCampaignStatus.Syncing, "a fresh row is inside its lease: a replica may be running it");

        _h.Clock.Advance(TimeSpan.FromMinutes(25));
        await worker.RecoverAndRunAsync(CancellationToken.None);

        Load(id).Status.Should().Be(expected);
        Load(id).FailureCode.Should().Be(code);
        _brevo.Writes.Should().BeEmpty("recovery only reads the managed list back; it never writes at Brevo and never resends");
        _brevo.Calls.Should().OnlyContain(c => c.Method == "GET" && c.Path.StartsWith("contacts/lists/10/contacts", StringComparison.Ordinal));
    }

    // 2. refresh results settles a Sending row; resolve is the human way out of SendUnknown

    [Theory]
    [InlineData("draft", MarketingCampaignStatus.SendUnknown, "send_unknown")]
    [InlineData("suspended", MarketingCampaignStatus.SendUnknown, "send_unknown")]
    [InlineData("cancelled", MarketingCampaignStatus.SendUnknown, "send_unknown")]
    [InlineData("cancelling", MarketingCampaignStatus.SendUnknown, "send_unknown")]
    [InlineData("archive", MarketingCampaignStatus.SendUnknown, "send_unknown")]
    [InlineData("in_process", MarketingCampaignStatus.Sending, null)]
    [InlineData("queued", MarketingCampaignStatus.Sending, null)]
    public async Task Refresh_settles_a_Sending_row_from_the_Brevo_status(string brevoStatus, MarketingCampaignStatus expected, string? code)
    {
        Guid id = SeedRow(MarketingCampaignStatus.Sending, brevoId: 500);
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("emailCampaigns/500", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.OK, "{\"status\":\"" + brevoStatus + "\"}") : null;
        await using InferenceApiDbContext db = _h.NewDb();

        (await Controller(db).RefreshResults(id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        Load(id).Status.Should().Be(expected);
        Load(id).FailureCode.Should().Be(code);
        _brevo.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_moves_a_Sending_row_to_SendUnknown_when_Brevo_no_longer_knows_the_campaign()
    {
        Guid id = SeedRow(MarketingCampaignStatus.Sending, brevoId: 500);
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("emailCampaigns/500", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.NotFound, "{\"code\":\"document_not_found\"}") : null;
        await using InferenceApiDbContext db = _h.NewDb();

        (await Controller(db).RefreshResults(id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        Load(id).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
        // a 404 that is not Brevo's own document stays an error and moves nothing
        _brevo.Override = call => call.Method == "GET" ? FakeBrevo.Status(HttpStatusCode.NotFound, "<html>proxy</html>") : null;
        Guid other = SeedRow(MarketingCampaignStatus.Sending, brevoId: 501);
        (await Controller(db).RefreshResults(other, CancellationToken.None)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
        Load(other).Status.Should().Be(MarketingCampaignStatus.Sending);
    }

    [Theory]
    [InlineData("sent", MarketingCampaignStatus.Sent, null)]
    [InlineData("notSent", MarketingCampaignStatus.Failed, "not_sent")]
    public async Task Resolve_settles_a_SendUnknown_campaign_calls_Brevo_nothing_and_logs_the_admin(string outcome, MarketingCampaignStatus expected, string? code)
    {
        Guid id = SeedRow(MarketingCampaignStatus.SendUnknown, brevoId: 500, dispatch: _h.Now);
        RecordingLogger<MarketingCampaignService> log = new();
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingCampaignService svc = new(db, _h.NewService(db), Client(), Microsoft.Extensions.Options.Options.Create(_h.Options),
            Microsoft.Extensions.Options.Options.Create(_h.CloudStorage), Microsoft.Extensions.Options.Options.Create(_opt), log, _h.Clock);

        IActionResult res = await new MarketingCampaignAdminController(svc, new FakeUser(_admin, true)).Resolve(id, new CampaignResolveRequest(outcome), CancellationToken.None);

        res.Should().BeOfType<OkObjectResult>();
        Load(id).Status.Should().Be(expected);
        Load(id).FailureCode.Should().Be(code);
        _brevo.Calls.Should().BeEmpty();
        log.Entries.Should().Contain(e => e.Message.Contains(_admin.ToString(), StringComparison.Ordinal) && e.Message.Contains(outcome, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MarketingCampaignStatus.Draft)]
    [InlineData(MarketingCampaignStatus.Queued)]
    [InlineData(MarketingCampaignStatus.Syncing)]
    [InlineData(MarketingCampaignStatus.Sending)]
    [InlineData(MarketingCampaignStatus.Sent)]
    [InlineData(MarketingCampaignStatus.Failed)]
    public async Task Resolve_is_refused_from_any_status_but_SendUnknown(MarketingCampaignStatus status)
    {
        Guid id = SeedRow(status);
        await using InferenceApiDbContext db = _h.NewDb();

        ObjectResult res = (ObjectResult)await Controller(db).Resolve(id, new CampaignResolveRequest("sent"), CancellationToken.None);

        res.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(res.Value).Should().Contain("not_send_unknown");
        Load(id).Status.Should().Be(status);
    }

    [Fact]
    public async Task Resolve_needs_a_platform_admin_an_enabled_feature_a_known_id_and_a_known_outcome()
    {
        Guid id = SeedRow(MarketingCampaignStatus.SendUnknown, brevoId: 500);
        await using InferenceApiDbContext db = _h.NewDb();

        (await Controller(db, admin: false).Resolve(id, new CampaignResolveRequest("sent"), CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await Controller(db).Resolve(Guid.NewGuid(), new CampaignResolveRequest("sent"), CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        foreach (string? bad in new string?[] { null, "", "Sent", "resend", "failed" })
        {
            ObjectResult res = (ObjectResult)await Controller(db).Resolve(id, new CampaignResolveRequest(bad), CancellationToken.None);
            res.StatusCode.Should().Be(400);
        }

        Load(id).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
        _opt.Enabled = false;
        (await Controller(db).Resolve(id, new CampaignResolveRequest("sent"), CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        Load(id).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
    }

    // 3. reopen: duplicate

    [Theory]
    [InlineData(MarketingCampaignStatus.Draft)]
    [InlineData(MarketingCampaignStatus.Sent)]
    [InlineData(MarketingCampaignStatus.Failed)]
    public async Task Duplicate_makes_a_new_Draft_with_the_same_subject_and_html_and_no_test_record(MarketingCampaignStatus status)
    {
        Guid id = SeedRow(status, subject: "Original subject", html: "<p>original body text</p>", brevoId: 500);
        Guid other = Guid.NewGuid();
        await using InferenceApiDbContext db = _h.NewDb();

        OkObjectResult res = (OkObjectResult)await new MarketingCampaignAdminController(Service(db), new FakeUser(other, true)).Duplicate(id, CancellationToken.None);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(res.Value));
        Guid copyId = json.RootElement.GetProperty("id").GetGuid();
        copyId.Should().NotBe(id);
        json.RootElement.GetProperty("status").GetString().Should().Be("draft");
        MarketingCampaign copy = Load(copyId);
        copy.Status.Should().Be(MarketingCampaignStatus.Draft);
        copy.Subject.Should().Be("Original subject");
        copy.Html.Should().Be("<p>original body text</p>");
        copy.TestSentAt.Should().BeNull();
        copy.TestContentHash.Should().BeNull();
        copy.BrevoCampaignId.Should().BeNull();
        copy.FailureCode.Should().BeNull();
        copy.CreatedBy.Should().Be(other);
        Load(id).Status.Should().Be(status, "the source is untouched");
        _brevo.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Duplicate_needs_a_platform_admin_an_enabled_feature_and_a_known_id()
    {
        Guid id = SeedRow(MarketingCampaignStatus.Failed);
        await using InferenceApiDbContext db = _h.NewDb();

        (await Controller(db, admin: false).Duplicate(id, CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await Controller(db).Duplicate(Guid.NewGuid(), CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        _opt.Enabled = false;
        (await Controller(db).Duplicate(id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        _h.NewDb().MarketingCampaigns.Count().Should().Be(1);
    }

    // 4. mutation survivors

    [Fact]
    public async Task U5b_a_read_failure_classifying_a_blocked_contact_after_the_import_fails_with_its_own_code_and_moves_nothing()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.Blocked.Add("b@example.test");
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("contacts/b%40", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.BadGateway) : null;

        MarketingCampaign run = await RunAsync(id);

        run.Status.Should().Be(MarketingCampaignStatus.Failed);
        run.FailureCode.Should().Be("brevo_http_502", "the read failure is its own failure, not list_blocked_contact");
        _brevo.Sends.Should().BeEmpty();
        MarketingConsent b = Row("b@example.test");
        b.Status.Should().Be(MarketingConsentStatus.OptedIn, "nothing was withdrawn locally");
        b.UndeliverableAt.Should().BeNull();
        _brevo.Calls.Should().NotContain(c => c.Method == "DELETE");
    }

    [Fact]
    public async Task E9_a_blank_key_makes_zero_Brevo_calls_from_every_erasure_entry_point_and_leaves_the_rows()
    {
        Add("left@example.test", MarketingConsentStatus.Withdrawn, synced: true);
        Add("keep@example.test", synced: true);
        StageErasure("gone@example.test");
        _opt.BrevoCampaignApiKey = "  ";
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingConsentService svc = _h.NewService(db);
        BrevoCampaignClient client = Client();
        BrevoErasureOutbox outbox = new(db, svc, new BrevoContactGuard(db, svc, client, Microsoft.Extensions.Options.Options.Create(_opt)), client, new TestProtector(),
            Microsoft.Extensions.Options.Options.Create(_opt), new RecordingLogger<BrevoErasureOutbox>(), _h.Clock);

        (await outbox.DrainAsync(CancellationToken.None)).Should().Be(1);
        (await outbox.DrainWithBackoffAsync(CancellationToken.None)).Should().Be(1);
        (await outbox.DeleteUnsendableAsync(CancellationToken.None)).Should().Be(0);
        await ErasureWorker(_opt).PassAsync(CancellationToken.None);

        _brevo.Calls.Should().BeEmpty();
        ErasureRows().Should().Be(1);
        Row("left@example.test").BrevoSyncedAt.Should().NotBeNull("the contact is still at Brevo, so its mark stays");
        Row("keep@example.test").BrevoSyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task E9_a_queued_campaign_with_a_blank_key_fails_closed_with_zero_Brevo_calls()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _opt.BrevoCampaignApiKey = "";

        (await RunAsync(id)).FailureCode.Should().Be("brevo_key_missing");

        _brevo.Calls.Should().BeEmpty();
        _brevo.Lists[10].Should().BeEmpty();
    }

    [Fact]
    public async Task U10b_a_genuine_confirmed_reopt_in_clears_the_undeliverable_mark_and_a_repeat_click_does_not_revive_it()
    {
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await _h.NewService(db).SubscribeAsync("back@example.test", "2026-10-marketing-v1", MarketingConsentSource.SiteContact,
                new MarketingRequestEvidence("203.0.113.7", "TestAgent/1.0"), CancellationToken.None);
        }

        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await _h.NewService(db).MarkUndeliverableAsync("back@example.test", "hard_bounce", CancellationToken.None);
        }

        Row("back@example.test").UndeliverableAt.Should().NotBeNull();
        string token = _h.Tokens.CreateConfirmToken(Row("back@example.test").Id, _h.Clock.GetUtcNow().AddDays(7));
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            (await _h.NewService(db).ConfirmAsync(token, new MarketingRequestEvidence("198.51.100.9", "Clicker"), CancellationToken.None))
                .Should().Be(MarketingTokenStatus.Valid);
        }

        MarketingConsent row = Row("back@example.test");
        row.Status.Should().Be(MarketingConsentStatus.OptedIn);
        row.UndeliverableAt.Should().BeNull();
        row.UndeliverableReason.Should().BeNull();
        await using InferenceApiDbContext read = _h.NewDb();
        (await _h.NewService(read).SendableRecipientsAsync(CancellationToken.None)).Recipients.Select(r => r.Email).Should().Equal("back@example.test");

        // a later bounce marks it again, and clicking the same link once more must not revive it
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await _h.NewService(db).MarkUndeliverableAsync("back@example.test", "hard_bounce", CancellationToken.None);
            await _h.NewService(db).ConfirmAsync(token, new MarketingRequestEvidence("198.51.100.9", "Clicker"), CancellationToken.None);
        }

        Row("back@example.test").UndeliverableAt.Should().NotBeNull();
    }

    [Fact]
    public async Task U11b_the_sweeps_catch_log_carries_no_plain_address()
    {
        Add("left@example.test", MarketingConsentStatus.Withdrawn, synced: true);
        _brevo.Existing.Add("left@example.test");
        _brevo.Override = call => call.Path.StartsWith("contacts/left%40", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.InternalServerError, "{\"message\":\"left@example.test exploded\"}") : null;
        RecordingLogger<BrevoErasureOutbox> log = new();
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingConsentService svc = _h.NewService(db);
        BrevoCampaignClient client = Client();
        BrevoErasureOutbox outbox = new(db, svc, new BrevoContactGuard(db, svc, client, Microsoft.Extensions.Options.Options.Create(_opt)), client, new TestProtector(),
            Microsoft.Extensions.Options.Options.Create(_opt), log, _h.Clock);

        (await outbox.DeleteUnsendableAsync(CancellationToken.None)).Should().Be(0);

        log.Entries.Should().NotBeEmpty("the failure is logged");
        string all = string.Join("|", log.Entries.Select(e => e.Message)) + string.Join("|", _clientLog.Entries.Select(e => e.Message));
        all.Should().NotContain("left@example.test").And.NotContain("left%40").And.Contain("brevo_http_500");
        Row("left@example.test").BrevoSyncedAt.Should().NotBeNull("the contact stays and is retried");
    }

    // 5. early config refusal

    [Theory]
    [InlineData(null, 10L, "brevo_key_missing")]
    [InlineData("  ", 10L, "brevo_key_missing")]
    [InlineData("secret-key", 0L, "brevo_list_missing")]
    public async Task A_send_with_missing_Brevo_key_or_list_is_refused_before_anything_is_queued(string? key, long listId, string code)
    {
        AddThree();
        Guid id = await TestedDraftAsync();
        _opt.BrevoCampaignApiKey = key;
        _opt.BrevoListId = listId;
        await using InferenceApiDbContext db = _h.NewDb();

        ObjectResult res = (ObjectResult)await Controller(db).Send(id, new CampaignSendRequest(3), CancellationToken.None);

        res.StatusCode.Should().Be(422);
        JsonSerializer.Serialize(res.Value).Should().Contain(code);
        Load(id).Status.Should().Be(MarketingCampaignStatus.Draft);
        _brevo.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unset_list_id_from_an_empty_setting_reads_as_not_configured_and_refuses_the_send()
    {
        AddThree();
        Guid id = await TestedDraftAsync();
        _opt.BrevoListIdSetting = null;
        await using InferenceApiDbContext db = _h.NewDb();

        _opt.BrevoListId.Should().Be(0);
        (await FluentActions.Awaiting(() => Service(db).RequestSendAsync(id, 3, _admin, CancellationToken.None)).Should().ThrowAsync<CampaignRefusal>())
            .Which.Code.Should().Be("brevo_list_missing");
    }

    // 7. concurrent send: a raw PostgresException out of ExecuteUpdateAsync

    [Fact]
    public async Task A_raw_PostgresException_from_the_queue_update_is_409_campaign_active()
    {
        AddThree();
        Guid id = await TestedDraftAsync();
        BeforeCommand interceptor = new(cmd => cmd.CommandText.Contains("confirmed_recipient_count", StringComparison.Ordinal)
            ? throw new Npgsql.PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505", constraintName: MarketingCampaignService.ActiveIndexName)
            : Task.CompletedTask);
        await using InferenceApiDbContext db = _h.NewDb(interceptor);
        MarketingCampaignAdminController c = new(Service(db), new FakeUser(_admin, true));

        ObjectResult result = (ObjectResult)await c.Send(id, new CampaignSendRequest(3), CancellationToken.None);

        result.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(result.Value).Should().Contain("campaign_active");
        Load(id).Status.Should().Be(MarketingCampaignStatus.Draft);
    }

    // ---------------------------------------------------------------- second round of Security and QA findings (T-018)

    private sealed class RequestRecorder(HttpMessageHandler inner, List<HttpRequestMessage> seen) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen.Add(request);
            return base.SendAsync(request, cancellationToken);
        }
    }

    // 1. no address in an exported span

    [Fact]
    public async Task Every_Brevo_request_is_marked_origin_only_so_no_address_reaches_the_exported_span()
    {
        List<HttpRequestMessage> seen = [];
        BrevoCampaignClient client = new(
            new HttpClient(new RequestRecorder(_brevo, seen)), Microsoft.Extensions.Options.Options.Create(_opt), MailLogRef.Ephemeral(), _clientLog);
        _brevo.Existing.Add("person@example.test");

        await client.GetContactAsync("person@example.test", CancellationToken.None);
        await client.DeleteContactAsync("person@example.test", CancellationToken.None);
        await client.GetListContactsAsync(10, 0, 50, CancellationToken.None);
        await client.GetCampaignAsync(500, CancellationToken.None);

        seen.Should().HaveCount(4);
        seen.Select(r => r.RequestUri!.ToString()).Should().Contain(u => u.Contains("person%40example.test", StringComparison.Ordinal),
            "the path itself carries the address, so the marking is what protects the span");
        ReelBolt.Shared.Observability.SensitiveUrlRedactionProcessor processor = new();
        foreach (HttpRequestMessage request in seen)
        {
            request.Options.TryGetValue(ReelBolt.Shared.Observability.ReelBoltTelemetry.OriginOnlyUrl, out string? origin).Should().BeTrue();
            origin.Should().Be("https://brevo.example.test");

            using System.Diagnostics.Activity span = new System.Diagnostics.Activity("System.Net.Http.HttpRequestOut")
                .SetTag("url.full", request.RequestUri!.ToString())
                .SetTag("url.path", request.RequestUri.AbsolutePath)
                .SetTag("url.query", request.RequestUri.Query);
            ReelBolt.Shared.Observability.SensitiveUrlRedactionProcessor.MarkOriginOnly(span, request);
            processor.OnEnd(span);

            span.GetTagItem("url.full").Should().Be("https://brevo.example.test");
            span.TagObjects.Select(t => t.Value?.ToString() ?? string.Empty).Should().NotContain(v => v.Contains("example.test%", StringComparison.Ordinal)
                || v.Contains("person", StringComparison.Ordinal) || v.Contains("/contacts", StringComparison.Ordinal));
            span.Baggage.Should().BeEmpty();
        }
    }

    // 5. a database failure after a 204 never writes Failed

    [Fact]
    public async Task A_database_failure_recording_the_outcome_after_a_204_leaves_the_row_for_recovery_and_never_writes_Failed()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.Calls.Clear();
        int attempts = 0;
        BeforeCommand failing = new(cmd =>
        {
            if (cmd.CommandText.Contains("final_recipient_count", StringComparison.Ordinal))
            {
                attempts++;
                throw new InvalidOperationException("the database went away");
            }

            return Task.CompletedTask;
        });
        await using (InferenceApiDbContext db = _h.NewDb(failing))
        {
            (await Pipeline(db).ClaimAndRunAsync(CancellationToken.None)).Should().BeTrue();
        }

        MarketingCampaign row = Load(id);
        row.Status.Should().Be(MarketingCampaignStatus.Syncing, "the outcome was never recorded and nothing wrote Failed");
        row.DispatchStartedAt.Should().NotBeNull();
        attempts.Should().Be(3, "a bounded number of tries");
        _brevo.Sends.Should().HaveCount(1);
        await using (InferenceApiDbContext other = _h.NewDb())
        {
            (await other.MarketingCampaigns.Where(c => c.Status == MarketingCampaignStatus.Failed).CountAsync()).Should().Be(0);
        }

        // the lease lapses and recovery settles it as SendUnknown: a human decides, and the send gate stays shut
        _h.Clock.Advance(TimeSpan.FromMinutes(31));
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await Pipeline(db).RecoverAsync(CancellationToken.None);
        }

        Load(id).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
        _brevo.Sends.Should().HaveCount(1, "sendNow is never repeated");
    }

    [Fact]
    public async Task A_transient_database_failure_recording_the_outcome_is_retried_and_ends_Sending()
    {
        AddThree();
        Guid id = await QueuedAsync();
        int attempts = 0;
        BeforeCommand flaky = new(cmd =>
        {
            if (cmd.CommandText.Contains("final_recipient_count", StringComparison.Ordinal) && ++attempts == 1)
            {
                throw new InvalidOperationException("transient");
            }

            return Task.CompletedTask;
        });
        await using InferenceApiDbContext db = _h.NewDb(flaky);
        (await Pipeline(db).ClaimAndRunAsync(CancellationToken.None)).Should().BeTrue();

        Load(id).Status.Should().Be(MarketingCampaignStatus.Sending);
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task An_unexpected_error_after_dispatch_started_does_not_fail_the_row()
    {
        AddThree();
        Guid id = await QueuedAsync();
        // the throw lands on the first statement after sendNow was recorded; FailAsync must find DispatchStartedAt set and refuse
        _brevo.Override = call => call.Path.EndsWith("/sendNow", StringComparison.Ordinal) ? throw new InvalidOperationException("boom") : null;

        MarketingCampaign row = await RunAsync(id);

        row.Status.Should().NotBe(MarketingCampaignStatus.Failed);
        row.DispatchStartedAt.Should().NotBeNull();
    }

    // 7. the sweep clears the flag only if it still has the value it read

    [Fact]
    public async Task The_sweep_does_not_clear_a_flag_a_later_run_set_while_it_was_at_Brevo()
    {
        Add("s@example.test", MarketingConsentStatus.Withdrawn, synced: true);
        _brevo.Existing.Add("s@example.test");
        DateTime newer = DateTime.UtcNow.AddMinutes(5);
        _brevo.After = call =>
        {
            if (call.Method != "DELETE") return;
            using InferenceApiDbContext other = _h.NewDb();
            other.MarketingConsents.Where(c => c.Email == "s@example.test").ExecuteUpdate(u => u.SetProperty(c => c.BrevoSyncedAt, newer));
        };
        await using InferenceApiDbContext db = _h.NewDb();

        await Guard(db).DeleteStaleAsync("s@example.test", CancellationToken.None);

        _brevo.Calls.Should().Contain(c => c.Method == "DELETE");
        SyncedAt("s@example.test").Should().NotBeNull("the newer flag belongs to a later run and survives");
        SyncedAt("s@example.test")!.Value.Should().BeCloseTo(newer, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task The_sweep_still_clears_an_unchanged_flag()
    {
        Add("s@example.test", MarketingConsentStatus.Withdrawn, synced: true);
        _brevo.Existing.Add("s@example.test");
        await using InferenceApiDbContext db = _h.NewDb();

        await Guard(db).DeleteStaleAsync("s@example.test", CancellationToken.None);

        SyncedAt("s@example.test").Should().BeNull();
    }

    // 8. recovery while campaigns are disabled

    [Fact]
    public async Task A_Syncing_row_is_recovered_by_the_erasure_worker_while_campaigns_are_disabled_and_the_sweep_runs()
    {
        _opt.Enabled = false;
        DateTime old = DateTime.UtcNow.AddHours(-2);
        Guid plain = SeedRow(MarketingCampaignStatus.Syncing, updated: old);
        Guid dispatched = SeedRow(MarketingCampaignStatus.Syncing, updated: old, dispatch: old, brevoId: 500);
        Add("left@example.test", MarketingConsentStatus.Withdrawn, synced: true);
        _brevo.Existing.Add("left@example.test");

        await ErasureWorker(_opt).PassAsync(CancellationToken.None);

        Load(plain).Status.Should().Be(MarketingCampaignStatus.Failed);
        Load(plain).FailureCode.Should().Be("interrupted");
        Load(dispatched).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
        _brevo.Calls.Should().Contain(c => c.Method == "DELETE", "the sweep is not paused by the recovered rows");
        SyncedAt("left@example.test").Should().BeNull();
    }

    [Fact]
    public async Task A_fresh_Syncing_row_still_pauses_the_sweep_while_campaigns_are_disabled()
    {
        _opt.Enabled = false;
        Guid running = SeedRow(MarketingCampaignStatus.Syncing, updated: DateTime.UtcNow);
        Add("left@example.test", MarketingConsentStatus.Withdrawn, synced: true);
        _brevo.Existing.Add("left@example.test");

        await ErasureWorker(_opt).PassAsync(CancellationToken.None);

        Load(running).Status.Should().Be(MarketingCampaignStatus.Syncing);
        _brevo.Calls.Should().NotContain(c => c.Method == "DELETE");
    }

    // 9. one atomic send gate; refresh answers a stable non-500

    [Fact]
    public async Task A_SendUnknown_row_appearing_between_the_checks_and_the_queue_update_refuses_the_send_atomically()
    {
        AddThree();
        Guid id = await TestedDraftAsync();
        bool injected = false;
        BeforeCommand race = new(cmd =>
        {
            if (!injected && cmd.CommandText.Contains("confirmed_recipient_count", StringComparison.Ordinal))
            {
                injected = true;
                AddSendUnknown();
            }

            return Task.CompletedTask;
        });
        await using InferenceApiDbContext db = _h.NewDb(race);
        MarketingCampaignAdminController c = new(Service(db), new FakeUser(_admin, true));

        ObjectResult result = (ObjectResult)await c.Send(id, new CampaignSendRequest(3), CancellationToken.None);

        injected.Should().BeTrue();
        result.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(result.Value).Should().Contain("unresolved_send");
        Load(id).Status.Should().Be(MarketingCampaignStatus.Draft, "the same statement that queues also checks for an unresolved send");
    }

    [Fact]
    public async Task Refresh_maps_an_active_index_violation_to_409_campaign_active()
    {
        Guid id = AddSendUnknown();
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("emailCampaigns/", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.OK, "{\"status\":\"in_process\"}") : null;
        BeforeCommand clash = new(cmd => cmd.CommandText.Contains("failure_code", StringComparison.Ordinal)
            ? throw new Npgsql.PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505", constraintName: MarketingCampaignService.ActiveIndexName)
            : Task.CompletedTask);
        await using InferenceApiDbContext db = _h.NewDb(clash);

        ObjectResult result = (ObjectResult)await new MarketingCampaignAdminController(Service(db), new FakeUser(_admin, true)).RefreshResults(id, CancellationToken.None);

        result.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(result.Value).Should().Contain("campaign_active");
        Load(id).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
    }

    [Theory]
    [InlineData("{\"status\":5}")]
    [InlineData("{\"status\":null}")]
    [InlineData("[]")]
    [InlineData("\"sent\"")]
    [InlineData("{\"status\":\"sent\",\"statistics\":7}")]
    public async Task Refresh_answers_a_stable_502_for_a_malformed_Brevo_status(string brevoBody)
    {
        Guid id = SeedRow(MarketingCampaignStatus.Sending, brevoId: 500);
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("emailCampaigns/500", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.OK, brevoBody) : null;
        await using InferenceApiDbContext db = _h.NewDb();

        IActionResult result = await Controller(db).RefreshResults(id, CancellationToken.None);

        if (brevoBody.Contains("\"sent\",", StringComparison.Ordinal))
        {
            // a status that is fine with a statistics value that is not an object is tolerated, never a 500
            result.Should().BeOfType<OkObjectResult>();
        }
        else
        {
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(502);
            JsonSerializer.Serialize(((ObjectResult)result).Value).Should().Contain("brevo_bad_response");
        }
    }

    // ---------------------------------------------------------------- T-032

    private BrevoErasureOutbox Outbox(InferenceApiDbContext db)
    {
        BrevoCampaignClient client = Client();
        MarketingConsentService svc = _h.NewService(db);
        return new(db, svc, new BrevoContactGuard(db, svc, client, Microsoft.Extensions.Options.Options.Create(_opt)), client, new TestProtector(),
            Microsoft.Extensions.Options.Options.Create(_opt), new RecordingLogger<BrevoErasureOutbox>(), _h.Clock);
    }

    private string[] PendingErasures()
    {
        using InferenceApiDbContext d = _h.NewDb();
        return d.MarketingBrevoErasures.AsNoTracking().ToArray()
            .Select(r => new TestProtector().TryUnprotect(r.ProtectedEmail, out string a) ? a : "?").Order(StringComparer.Ordinal).ToArray();
    }

    private void PurgeWithoutStaging(string email) => DeleteRow(email);

    // 1. the condition: a person purged and drained mid-run must not stay at Brevo

    [Fact]
    public async Task A_purge_and_drain_during_a_run_that_then_fails_at_the_import_leaves_the_contact_to_be_erased_after_the_run()
    {
        AddThree();
        Guid id = await QueuedAsync();
        int pendingSeenByDrain = -1;
        _brevo.After = call =>
        {
            if (call.Path.EndsWith("/contacts/remove", StringComparison.Ordinal))
            {
                // the purge: the row is deleted and its erasure staged; then the worker's drain tick runs while Syncing
                DeleteRow("b@example.test");
                StageErasure("b@example.test");
                using InferenceApiDbContext d = _h.NewDb();
                pendingSeenByDrain = Outbox(d).DrainWithBackoffAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            if (call.Path == "contacts/import") _brevo.ProcessStatus = "failed";
        };

        MarketingCampaign done = await RunAsync(id);

        done.Status.Should().Be(MarketingCampaignStatus.Failed);
        done.FailureCode.Should().Be("brevo_process_failed");
        pendingSeenByDrain.Should().Be(1, "the drain waits while a campaign is Syncing and reports the row as pending");
        _brevo.Calls.Where(c => c.Method == "DELETE").Should().BeEmpty("nothing was erased at Brevo during the run");
        _brevo.Lists[10].Should().Contain("b@example.test", "the run's import put it there");
        PendingErasures().Should().Equal("b@example.test");

        // the run has ended: the next drain deletes the contact and the row
        _brevo.ProcessStatus = "completed";
        await using InferenceApiDbContext db = _h.NewDb();
        (await Outbox(db).DrainWithBackoffAsync(CancellationToken.None)).Should().Be(0);
        _brevo.Calls.Count(c => c.Method == "DELETE" && c.Path.StartsWith("contacts/b%40", StringComparison.Ordinal)).Should().Be(1);
        PendingErasures().Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_purged_during_a_run_that_fails_after_the_import_is_re_staged_on_exit()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.After = call =>
        {
            // purged with nothing staged (it read the row before the write-ahead flag); the import then fails
            if (call.Path.EndsWith("/contacts/remove", StringComparison.Ordinal)) PurgeWithoutStaging("b@example.test");
            if (call.Path == "contacts/import") _brevo.ProcessStatus = "failed";
        };

        (await RunAsync(id)).FailureCode.Should().Be("brevo_process_failed");

        PendingErasures().Should().Equal(["b@example.test"], "the exit of every run that imported stages the erasure for a person with no consent row");
    }

    [Fact]
    public async Task A_row_purged_during_a_run_that_ends_in_SendUnknown_is_re_staged_on_exit_once()
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.After = call =>
        {
            if (call.Path == "contacts/import" && call.Body!.Contains("b@example.test", StringComparison.Ordinal)) PurgeWithoutStaging("b@example.test");
        };
        _brevo.Override = call => call.Path.EndsWith("/sendNow", StringComparison.Ordinal) ? FakeBrevo.Status(HttpStatusCode.BadGateway) : null;

        MarketingCampaign done = await RunAsync(id);

        done.Status.Should().NotBe(MarketingCampaignStatus.Sent);
        PendingErasures().Should().Equal(["b@example.test"], "staged once, not once per exit path");
    }

    [Theory]
    [InlineData(false, MarketingCampaignStatus.Failed, "interrupted")]
    [InlineData(true, MarketingCampaignStatus.SendUnknown, "send_unknown")]
    public async Task Recovery_of_a_crashed_run_reads_the_managed_list_back_and_stages_erasures_for_people_with_no_consent_row(
        bool dispatched, MarketingCampaignStatus expected, string code)
    {
        Add("a@example.test");
        _brevo.Lists[10].UnionWith(["a@example.test", "gone@example.test"]); // gone: purged while the crashed run had imported it
        DateTime old = _h.Now.AddMinutes(-40);
        Guid id = SeedRow(MarketingCampaignStatus.Syncing, old, dispatched ? old : null, dispatched ? 500 : null);

        await using InferenceApiDbContext db = _h.NewDb();
        await Pipeline(db).RecoverAsync(CancellationToken.None);

        PendingErasures().Should().Equal("gone@example.test");
        Load(id).Status.Should().Be(expected);
        Load(id).FailureCode.Should().Be(code);
        _brevo.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_read_back_during_recovery_leaves_the_row_Syncing_and_stages_nothing_until_a_later_tick_reads_it()
    {
        Add("a@example.test");
        _brevo.Lists[10].UnionWith(["a@example.test", "gone@example.test"]);
        DateTime old = _h.Now.AddMinutes(-40);
        Guid id = SeedRow(MarketingCampaignStatus.Syncing, old, old, 500);
        _brevo.Override = call => call.Path.Contains("contacts/lists/10/contacts", StringComparison.Ordinal) ? FakeBrevo.Status(HttpStatusCode.BadGateway) : null;

        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await Pipeline(db).RecoverAsync(CancellationToken.None);
        }

        Load(id).Status.Should().Be(MarketingCampaignStatus.Syncing, "a failed read-back does not settle the row");
        PendingErasures().Should().BeEmpty();

        _brevo.Override = null;
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await Pipeline(db).RecoverAsync(CancellationToken.None);
        }

        Load(id).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
        PendingErasures().Should().Equal("gone@example.test");
    }

    [Fact]
    public async Task The_sweeps_recovery_also_reads_back_and_stages_and_defers_on_a_failed_read()
    {
        Add("a@example.test");
        _brevo.Lists[10].UnionWith(["a@example.test", "gone@example.test"]);
        DateTime old = _h.Now.AddMinutes(-40);
        Guid id = SeedRow(MarketingCampaignStatus.Syncing, old, null);
        _brevo.Override = call => call.Path.Contains("contacts/lists/10/contacts", StringComparison.Ordinal) ? throw new HttpRequestException("down") : null;
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await Outbox(db).DeleteUnsendableAsync(CancellationToken.None);
        }

        Load(id).Status.Should().Be(MarketingCampaignStatus.Syncing);
        _brevo.Override = null;
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await Outbox(db).DeleteUnsendableAsync(CancellationToken.None);
        }

        Load(id).Status.Should().Be(MarketingCampaignStatus.Failed);
        PendingErasures().Should().Equal("gone@example.test");
    }

    [Fact]
    public async Task A_stale_Syncing_row_with_a_failing_read_back_does_not_stall_erasures_and_settles_at_the_age_bound()
    {
        StageErasure("z@example.test");
        _brevo.Override = call => call.Path.Contains("contacts/lists/10/contacts", StringComparison.Ordinal) ? FakeBrevo.Status(HttpStatusCode.Forbidden) : null;
        Guid stale = SeedRow(MarketingCampaignStatus.Syncing, _h.Now.AddMinutes(-40), null);
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            (await Outbox(db).DrainWithBackoffAsync(CancellationToken.None)).Should().Be(0, "a stale row no longer defers the drain");
        }

        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await Outbox(db).DeleteUnsendableAsync(CancellationToken.None);
        }

        Load(stale).Status.Should().Be(MarketingCampaignStatus.Syncing, "inside the age bound the row is retried");
        Guid old = SeedRow(MarketingCampaignStatus.Syncing, _h.Now.AddHours(-25), _h.Now.AddHours(-25), 500);
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await Pipeline(db).RecoverAsync(CancellationToken.None);
        }

        Load(old).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
        PendingErasures().Should().BeEmpty();
    }

    [Theory]
    [InlineData(25, 25)]
    [InlineData(26, 0)]
    [InlineData(0, 0)]
    public async Task Recovery_stages_only_when_the_no_consent_addresses_are_within_the_bound(int unknown, int staged)
    {
        Add("a@example.test");
        _brevo.Lists[10].Add("a@example.test");
        for (int i = 0; i < unknown; i++) _brevo.Lists[10].Add($"x{i}@example.test");
        Guid id = SeedRow(MarketingCampaignStatus.Syncing, _h.Now.AddMinutes(-40), null);

        await using InferenceApiDbContext db = _h.NewDb();
        await Pipeline(db).RecoverAsync(CancellationToken.None);

        PendingErasures().Length.Should().Be(staged);
        Load(id).Status.Should().Be(MarketingCampaignStatus.Failed);
        Load(id).FailureCode.Should().Be("interrupted");
    }

    [Fact]
    public async Task The_workers_drain_waits_for_a_Syncing_campaign_but_the_pipelines_own_step_one_does_not()
    {
        StageErasure("z@example.test");
        Guid running = SeedRow(MarketingCampaignStatus.Syncing);
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            (await Outbox(db).DrainWithBackoffAsync(CancellationToken.None)).Should().Be(1);
        }

        _brevo.Calls.Should().BeEmpty();
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            (await Outbox(db).DrainAsync(CancellationToken.None)).Should().Be(0, "step 1 of a run drains before anything is imported");
        }

        _brevo.Calls.Count(c => c.Method == "DELETE").Should().Be(1);
        Load(running).Status.Should().Be(MarketingCampaignStatus.Syncing);
    }

    // 2. sendNow outcomes

    [Theory]
    // definite refusals: Failed, the campaign is deleted
    [InlineData(400, "{\"code\":\"invalid_parameter\",\"message\":\"bad\"}", false)]
    [InlineData(402, "{\"code\":\"not_enough_credits\"}", false)]
    [InlineData(404, "{\"code\":\"document_not_found\"}", false)]
    // the three answers that mean it may already be going out
    [InlineData(400, "{\"code\":\"campaign_sent\"}", true)]
    [InlineData(400, "{\"code\":\"campaign_processing\"}", true)]
    [InlineData(402, "{\"code\":\"campaign_sent\"}", true)]
    [InlineData(404, "{\"code\":\"campaign_processing\"}", true)]
    [InlineData(400, "{\"code\":\"invalid_parameter\",\"message\":\"Request already processed\"}", true)]
    [InlineData(400, "{\"message\":\"request already processed.\"}", true)]
    // other statuses
    [InlineData(408, "{\"code\":\"timeout\"}", true)]
    [InlineData(429, "{\"code\":\"too_many_requests\"}", true)]
    [InlineData(302, "{}", true)]
    [InlineData(301, "", true)]
    [InlineData(401, "{\"code\":\"unauthorized\"}", true)]
    [InlineData(403, "{\"code\":\"forbidden\"}", true)]
    [InlineData(409, "{\"code\":\"conflict\"}", true)]
    [InlineData(422, "{\"code\":\"invalid\"}", true)]
    [InlineData(500, "{\"code\":\"x\"}", true)]
    [InlineData(503, "", true)]
    // an unparsable or non-object body is not proof
    [InlineData(400, "<html>proxy</html>", true)]
    [InlineData(400, "", true)]
    [InlineData(402, "not json", true)]
    [InlineData(404, "<html>nginx</html>", true)]
    [InlineData(400, "[]", true)]
    [InlineData(400, "\"campaign\"", true)]
    public async Task SendNow_outcome_table_only_a_proven_refusal_is_Failed(int status, string body, bool unknown)
    {
        AddThree();
        Guid id = await QueuedAsync();
        _brevo.Override = call => call.Path.EndsWith("/sendNow", StringComparison.Ordinal)
            ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }
            : null;

        MarketingCampaign done = await RunAsync(id);

        _brevo.Sends.Should().HaveCount(1, "sendNow is never retried");
        if (unknown)
        {
            done.Status.Should().Be(MarketingCampaignStatus.SendUnknown);
            done.FailureCode.Should().Be("send_unknown");
            _brevo.Calls.Should().NotContain(c => c.Method == "DELETE", "the campaign may be going out; it is not deleted");
        }
        else
        {
            done.Status.Should().Be(MarketingCampaignStatus.Failed);
            _brevo.Calls.Last().Method.Should().Be("DELETE");
        }
    }

    // 4. list id binding

    [Fact]
    public async Task A_real_numeric_list_id_binds_from_configuration_and_the_pipeline_sends_to_that_list()
    {
        Microsoft.Extensions.Configuration.IConfiguration config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Marketing:Campaigns:BrevoListId"] = "10", ["Marketing:Campaigns:BrevoTestListId"] = "11" }).Build();
        _opt.BrevoListIdSetting = null;
        _opt.BrevoTestListIdSetting = null;
        _opt.BrevoListId.Should().Be(0);
        config.GetSection(MarketingCampaignOptions.SectionName).Bind(_opt);
        _opt.BrevoListId.Should().Be(10);
        _opt.BrevoTestListId.Should().Be(11);

        AddThree();
        Guid id = await QueuedAsync();
        (await RunAsync(id)).Status.Should().Be(MarketingCampaignStatus.Sending);
        Listed(10).Should().Equal("a@example.test", "b@example.test", "c@example.test");
        _brevo.Calls.Where(c => c.Path == "contacts/import").Should().OnlyContain(c => c.Body!.Contains("\"listIds\":[10]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12x")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("99999999999999999999999")]
    public async Task A_malformed_list_id_never_faults_the_host_reads_as_not_configured_and_warns_at_startup(string value)
    {
        Microsoft.Extensions.Configuration.IConfiguration config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Marketing:Campaigns:BrevoListId"] = value, ["Marketing:Campaigns:BrevoTestListId"] = value }).Build();
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.Configure<MarketingCampaignOptions>(config.GetSection(MarketingCampaignOptions.SectionName));
        using ServiceProvider sp = services.BuildServiceProvider();
        MarketingCampaignOptions bound = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MarketingCampaignOptions>>().Value;

        bound.BrevoListId.Should().Be(0);
        bound.BrevoTestListId.Should().Be(0);
        using BrevoErasureWorker erasure = new(sp.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(bound), new RecordingLogger<BrevoErasureWorker>());
        using MarketingCampaignWorker campaigns = new(sp.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(bound), new RecordingLogger<MarketingCampaignWorker>());
        await campaigns.StartAsync(CancellationToken.None);
        await campaigns.ExecuteTask!;
        campaigns.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        await erasure.PassAsync(CancellationToken.None);

        RecordingLogger<MarketingStartupChecks> log = new();
        await new MarketingStartupChecks(
            Microsoft.Extensions.Options.Options.Create(new MarketingOptions { SiteForwardToken = "t" }), MailLogRef.ForKey("k"), log,
            Microsoft.Extensions.Options.Options.Create(bound)).StartAsync(CancellationToken.None);
        log.Entries.Should().ContainSingle(e => e.EventId == MarketingStartupChecks.BrevoListIdInvalid && e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData(" 7 ")]
    public async Task An_unset_zero_or_valid_list_id_does_not_warn(string? value)
    {
        MarketingCampaignOptions o = new() { BrevoListIdSetting = value, BrevoTestListIdSetting = value };
        RecordingLogger<MarketingStartupChecks> log = new();
        await new MarketingStartupChecks(
            Microsoft.Extensions.Options.Options.Create(new MarketingOptions { SiteForwardToken = "t" }), MailLogRef.ForKey("k"), log,
            Microsoft.Extensions.Options.Options.Create(o)).StartAsync(CancellationToken.None);
        log.Entries.Should().NotContain(e => e.EventId == MarketingStartupChecks.BrevoListIdInvalid);
        o.BrevoListId.Should().Be(value?.Trim() == "7" ? 7 : 0);
    }

    // 5. a way out of Sending

    [Fact]
    public async Task Refresh_on_in_review_keeps_Sending_and_resolve_is_refused_until_the_configured_age()
    {
        Guid id = SeedRow(MarketingCampaignStatus.Sending, brevoId: 500);
        _brevo.Override = call => call.Method == "GET" && call.Path.StartsWith("emailCampaigns/500", StringComparison.Ordinal)
            ? FakeBrevo.Status(HttpStatusCode.OK, "{\"status\":\"in_review\"}") : null;
        await using InferenceApiDbContext db = _h.NewDb();
        await Service(db).RefreshResultsAsync(id, CancellationToken.None);
        Load(id).Status.Should().Be(MarketingCampaignStatus.Sending);

        // 23 hours: still too young (default age 24 h)
        _h.Clock.Advance(TimeSpan.FromHours(23));
        ObjectResult young = (ObjectResult)await Controller(db).Resolve(id, new CampaignResolveRequest("notSent"), CancellationToken.None);
        young.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(young.Value).Should().Contain("not_send_unknown");
        Load(id).Status.Should().Be(MarketingCampaignStatus.Sending);

        _h.Clock.Advance(TimeSpan.FromHours(2));
        OkObjectResult ok = (OkObjectResult)await Controller(db).Resolve(id, new CampaignResolveRequest("notSent"), CancellationToken.None);
        ok.Should().NotBeNull();
        Load(id).Status.Should().Be(MarketingCampaignStatus.Failed);
        Load(id).FailureCode.Should().Be("not_sent");
    }

    [Fact]
    public async Task The_age_before_a_Sending_row_can_be_resolved_is_configurable_and_sent_settles_it()
    {
        _opt.SendingResolveAfterHours = 2;
        Guid id = SeedRow(MarketingCampaignStatus.Sending, brevoId: 500);
        await using InferenceApiDbContext db = _h.NewDb();
        _h.Clock.Advance(TimeSpan.FromHours(1));
        ((ObjectResult)await Controller(db).Resolve(id, new CampaignResolveRequest("sent"), CancellationToken.None)).StatusCode.Should().Be(409);
        _h.Clock.Advance(TimeSpan.FromHours(1.5));
        await Controller(db).Resolve(id, new CampaignResolveRequest("sent"), CancellationToken.None);
        Load(id).Status.Should().Be(MarketingCampaignStatus.Sent);
        _brevo.Calls.Should().NotContain(c => c.Method != "GET", "resolve calls Brevo nothing");
    }

    // 6. smaller

    [Theory]
    [InlineData(MarketingCampaignStatus.Queued, "campaign_active")]
    [InlineData(MarketingCampaignStatus.Syncing, "campaign_active")]
    [InlineData(MarketingCampaignStatus.Sending, "campaign_active")]
    [InlineData(MarketingCampaignStatus.SendUnknown, "unresolved_send")]
    public async Task Duplicate_refuses_a_campaign_that_is_active_or_unresolved(MarketingCampaignStatus status, string code)
    {
        Guid id = SeedRow(status, brevoId: 500);
        await using InferenceApiDbContext db = _h.NewDb();
        ObjectResult res = (ObjectResult)await Controller(db).Duplicate(id, CancellationToken.None);
        res.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(res.Value).Should().Contain(code);
        db.MarketingCampaigns.AsNoTracking().Count().Should().Be(1, "no copy was made");
    }

    [Fact]
    public async Task A_resolve_outcome_that_is_not_a_string_or_a_missing_body_answers_400_outcome_invalid()
    {
        Guid id = SeedRow(MarketingCampaignStatus.SendUnknown, brevoId: 500);
        await using InferenceApiDbContext db = _h.NewDb();
        foreach (CampaignResolveRequest? request in new CampaignResolveRequest?[]
        {
            null,
            new((object?)null),
            new(JsonDocument.Parse("5").RootElement.Clone()),
            new(JsonDocument.Parse("{\"a\":1}").RootElement.Clone()),
            new(JsonDocument.Parse("true").RootElement.Clone()),
            new("maybe"),
        })
        {
            ObjectResult res = (ObjectResult)await Controller(db).Resolve(id, request, CancellationToken.None);
            res.StatusCode.Should().Be(400);
            JsonSerializer.Serialize(res.Value).Should().Contain("outcome_invalid");
        }

        Load(id).Status.Should().Be(MarketingCampaignStatus.SendUnknown);
        (await Controller(db).Resolve(id, new CampaignResolveRequest(JsonDocument.Parse("\"sent\"").RootElement.Clone()), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("a JSON string outcome as the model binder delivers it still works");
    }

    [Fact]
    public async Task The_resolve_request_deserializes_a_numeric_outcome_without_a_binding_error()
    {
        CampaignResolveRequest? r = JsonSerializer.Deserialize<CampaignResolveRequest>("{\"outcome\":5}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        r.Should().NotBeNull();
        r!.Outcome.Should().NotBeNull();
        CampaignResolveRequest? s = JsonSerializer.Deserialize<CampaignResolveRequest>("{\"outcome\":\"sent\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        s!.Outcome.Should().BeOfType<JsonElement>();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_zero_row_outcome_write_is_not_logged_as_sending()
    {
        AddThree();
        Guid id = await QueuedAsync();
        RecordingLogger<MarketingCampaignPipeline> log = new();
        await using InferenceApiDbContext db = _h.NewDb();
        BrevoCampaignClient client = Client();
        MarketingConsentService svc = _h.NewService(db);
        BrevoContactGuard guard = new(db, svc, client, Microsoft.Extensions.Options.Options.Create(_opt));
        BrevoErasureOutbox drain = new(db, svc, guard, client, new TestProtector(), Microsoft.Extensions.Options.Options.Create(_opt), new RecordingLogger<BrevoErasureOutbox>(), _h.Clock);
        MarketingCampaignPipeline pipeline = new(db, svc, Service(db), client, drain, guard, Microsoft.Extensions.Options.Options.Create(_opt), log, _h.Clock, new TestProtector());
        _brevo.After = call =>
        {
            // another actor settles the row while sendNow is out: the outcome write matches no row
            if (call.Path.EndsWith("/sendNow", StringComparison.Ordinal))
            {
                using InferenceApiDbContext d = _h.NewDb();
                d.MarketingCampaigns.Where(c => c.Id == id).ExecuteUpdate(s => s.SetProperty(c => c.Status, MarketingCampaignStatus.SendUnknown));
            }
        };

        (await pipeline.ClaimAndRunAsync(CancellationToken.None)).Should().BeTrue();

        log.Entries.Should().NotContain(e => e.Message.Contains("Marketing campaign sending", StringComparison.Ordinal));
        log.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("matched no row", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_send_stages_an_erasure_when_the_recipients_consent_row_was_purged_meanwhile()
    {
        Add("owner@example.test");
        Guid id = await DraftAsync();
        _brevo.Override = call =>
        {
            if (call.Path == "contacts/import") PurgeWithoutStaging("owner@example.test"); // the purge read the row before the flag
            return null;
        };

        await using InferenceApiDbContext db = _h.NewDb();
        await Service(db).TestSendAsync(id, _admin, CancellationToken.None);

        PendingErasures().Should().Equal("owner@example.test");
    }

    [Fact]
    public async Task Test_send_with_no_consent_row_for_the_recipient_stages_nothing()
    {
        Guid id = await DraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        await Service(db).TestSendAsync(id, _admin, CancellationToken.None);
        PendingErasures().Should().BeEmpty();
    }

    [Fact]
    public async Task Test_send_with_a_surviving_consent_row_stages_nothing()
    {
        Add("owner@example.test");
        Guid id = await DraftAsync();
        await using InferenceApiDbContext db = _h.NewDb();
        await Service(db).TestSendAsync(id, _admin, CancellationToken.None);
        PendingErasures().Should().BeEmpty();
    }

    // E9: blank-key gates (mutants: remove the gate in the worker pass, in the drain, in the sweep)

    private sealed class CountingScopes(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;
            return inner.CreateScope();
        }
    }

    [Fact]
    public async Task E9_with_a_blank_key_the_worker_pass_opens_no_scope_and_the_outbox_touches_no_row_and_logs_nothing()
    {
        Add("left@example.test", MarketingConsentStatus.Withdrawn, synced: true);
        StageErasure("gone@example.test");
        _opt.BrevoCampaignApiKey = " ";
        DateTime old = _h.Now.AddMinutes(-40);
        Guid stale = SeedRow(MarketingCampaignStatus.Syncing, old, null);

        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddLogging();
        ServiceProvider sp = services.BuildServiceProvider();
        _disposables.Add(sp);
        CountingScopes scopes = new(sp.GetRequiredService<IServiceScopeFactory>());
        RecordingLogger<BrevoErasureWorker> workerLog = new();
        await new BrevoErasureWorker(scopes, Microsoft.Extensions.Options.Options.Create(_opt), workerLog).PassAsync(CancellationToken.None);
        scopes.Created.Should().Be(0, "a blank key ends the pass before it opens a scope");
        workerLog.Entries.Should().BeEmpty();

        RecordingLogger<BrevoErasureOutbox> log = new();
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingConsentService svc = _h.NewService(db);
        BrevoCampaignClient client = Client();
        BrevoErasureOutbox outbox = new(db, svc, new BrevoContactGuard(db, svc, client, Microsoft.Extensions.Options.Options.Create(_opt)), client, new TestProtector(),
            Microsoft.Extensions.Options.Options.Create(_opt), log, _h.Clock);
        (await outbox.DrainAsync(CancellationToken.None)).Should().Be(1);
        (await outbox.DrainWithBackoffAsync(CancellationToken.None)).Should().Be(1);
        (await outbox.DeleteUnsendableAsync(CancellationToken.None)).Should().Be(0);

        log.Entries.Should().BeEmpty("a blank key is inert: no attempt is made, so nothing fails and nothing is logged");
        await using InferenceApiDbContext check = _h.NewDb();
        MarketingBrevoErasure row = check.MarketingBrevoErasures.AsNoTracking().Single();
        row.Attempts.Should().Be(0);
        row.LastAttemptAt.Should().BeNull();
        Row("left@example.test").BrevoSyncedAt.Should().NotBeNull();
        Load(stale).Status.Should().Be(MarketingCampaignStatus.Syncing, "the sweep's recovery does not run without a key");
        _brevo.Calls.Should().BeEmpty();
    }

    private MarketingConsent Row(string email)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsents.AsNoTracking().Single(c => c.Email == email);
    }
}

internal static class FakeBrevoAssertions
{
    /// <summary>The managed list (10) is never touched by a test send.</summary>
    public static void Listed10Untouched(this FakeBrevo brevo)
        => brevo.Writes.Where(c => c.Path.Contains("lists/10", StringComparison.Ordinal)
            || (c.Path == "contacts/import" && c.Body!.Contains("\"listIds\":[10]", StringComparison.Ordinal))).Should().BeEmpty();

}
