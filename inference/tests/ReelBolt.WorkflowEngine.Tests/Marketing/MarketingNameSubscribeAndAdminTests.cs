#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>brevo-contact-sync T-001: the subscribe form name and the admin contact list and name edit.</summary>
public sealed class MarketingNameSubscribeAndAdminTests : IDisposable
{
    private const string V1 = "2026-10-marketing-v1";
    private static readonly MarketingRequestEvidence Evidence = new("203.0.113.7", "TestAgent/1.0");
    private readonly MarketingHarness _h = new();

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

    private async Task<MarketingSubscribeOutcome> SubscribeAsync(string email, string? firstName)
    {
        await using InferenceApiDbContext db = _h.NewDb();
        return await _h.NewService(db).SubscribeAsync(
            email, V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None, null, firstName);
    }

    private MarketingConsent Row(string email)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsents.AsNoTracking().Single(c => c.Email == email);
    }

    private Guid Add(string email, MarketingConsentStatus status, MarketingNameSource? source = null, string? first = null,
        string? last = null)
    {
        using InferenceApiDbContext db = _h.NewDb();
        Guid id = Guid.NewGuid();
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = id,
            Email = email,
            Status = status,
            WordingVersion = V1,
            Source = MarketingConsentSource.SiteFooter,
            ConfirmedAt = status == MarketingConsentStatus.OptedIn ? _h.Now : null,
            FirstName = first,
            LastName = last,
            NameSource = source,
            NameUpdatedAt = source is null ? null : _h.Now.AddDays(-1),
            UpdatedAt = _h.Now.AddDays(-1),
        });
        db.SaveChanges();
        return id;
    }

    // --- subscribe ------------------------------------------------------------------------------

    [Fact]
    public async Task Form_name_is_stored_on_a_new_row()
    {
        await SubscribeAsync("new@example.com", "  Maria  ");
        MarketingConsent row = Row("new@example.com");
        row.FirstName.Should().Be("Maria");
        row.NameSource.Should().Be(MarketingNameSource.Form);
        row.NameUpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Form_name_never_changes_an_existing_row()
    {
        Add("form@example.com", MarketingConsentStatus.Pending, MarketingNameSource.Form, "First");
        Add("none@example.com", MarketingConsentStatus.Pending);
        Add("acct@example.com", MarketingConsentStatus.Pending, MarketingNameSource.Account, "Acc", "Ount");
        Add("o@example.com", MarketingConsentStatus.OptedIn);
        Add("w@example.com", MarketingConsentStatus.Withdrawn);
        Add("a@example.com", MarketingConsentStatus.Pending, MarketingNameSource.Admin, "Admin", "Set");
        Guid userId = _h.AddUser("verified@example.com", _h.Now);
        using (InferenceApiDbContext seed = _h.NewDb())
        {
            seed.MarketingConsents.Add(new MarketingConsent
            {
                Id = Guid.NewGuid(),
                Email = "verified@example.com",
                UserId = userId,
                Status = MarketingConsentStatus.Pending,
                WordingVersion = V1,
                Source = MarketingConsentSource.Signup,
            });
            seed.SaveChanges();
        }

        foreach (string email in new[] { "form@example.com", "none@example.com", "acct@example.com", "o@example.com", "w@example.com", "a@example.com", "verified@example.com" })
        {
            (await SubscribeAsync(email, "Intruder")).Should().Be(MarketingSubscribeOutcome.Accepted);
        }

        Row("form@example.com").FirstName.Should().Be("First");
        Row("none@example.com").FirstName.Should().BeNull();
        Row("none@example.com").NameSource.Should().BeNull();
        MarketingConsent acct = Row("acct@example.com");
        acct.FirstName.Should().Be("Acc");
        acct.NameSource.Should().Be(MarketingNameSource.Account);
        Row("o@example.com").FirstName.Should().BeNull();
        Row("w@example.com").FirstName.Should().BeNull();
        Row("a@example.com").FirstName.Should().Be("Admin");
        Row("verified@example.com").FirstName.Should().BeNull();
    }

    [Fact]
    public async Task Repeat_subscribe_with_another_name_keeps_the_first()
    {
        await SubscribeAsync("rep@example.com", "First");
        await SubscribeAsync("rep@example.com", "Second");
        Row("rep@example.com").FirstName.Should().Be("First");
    }

    [Fact]
    public async Task Invalid_name_is_ignored_and_the_outcome_is_the_same()
    {
        MarketingSubscribeOutcome withName = await SubscribeAsync("x@example.com", "‮evil");
        MarketingSubscribeOutcome withoutName = await SubscribeAsync("y@example.com", null);
        withName.Should().Be(withoutName).And.Be(MarketingSubscribeOutcome.Accepted);
        Row("x@example.com").FirstName.Should().BeNull();
        Row("x@example.com").NameSource.Should().BeNull();
    }

    // --- admin ----------------------------------------------------------------------------------

    private MarketingContactAdminController Controller(InferenceApiDbContext db, bool admin = true)
    {
        MarketingContactAdminController c = new(db, new FakeUser(admin), NullLogger<MarketingContactAdminController>.Instance, _h.Clock);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return c;
    }

    [Fact]
    public async Task List_and_patch_require_a_platform_admin()
    {
        Guid id = Add("a@example.com", MarketingConsentStatus.OptedIn);
        await using InferenceApiDbContext db = _h.NewDb();
        (await Controller(db, admin: false).List(null, null, 50, 0, CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await Controller(db, admin: false).SetName(id, new MarketingNameRequest("A", null), CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task List_filters_by_status_and_query_and_derives_the_brevo_state()
    {
        Add("synced@example.com", MarketingConsentStatus.OptedIn, MarketingNameSource.Admin, "Sam", "Synced");
        Add("pend@example.com", MarketingConsentStatus.OptedIn);
        Add("failed@example.com", MarketingConsentStatus.OptedIn);
        Add("w@example.com", MarketingConsentStatus.Withdrawn);
        using (InferenceApiDbContext seed = _h.NewDb())
        {
            seed.MarketingConsents.Single(c => c.Email == "synced@example.com").BrevoPushedAt = _h.Now;
            seed.MarketingConsents.Single(c => c.Email == "failed@example.com").BrevoPushErrorCode = "brevo_http_500";
            seed.SaveChanges();
        }

        await using InferenceApiDbContext db = _h.NewDb();
        var ok = (OkObjectResult)await Controller(db).List(null, null, 50, 0, CancellationToken.None);
        var all = (MarketingContactListResponse)ok.Value!;
        all.Total.Should().Be(3);
        all.Items.Single(i => i.Email == "synced@example.com").Brevo.State.Should().Be("synced");
        all.Items.Single(i => i.Email == "pend@example.com").Brevo.State.Should().Be("pending");
        MarketingContactDto failed = all.Items.Single(i => i.Email == "failed@example.com");
        failed.Brevo.State.Should().Be("failed");
        failed.Brevo.ErrorCode.Should().Be("brevo_http_500");

        var withdrawn = (MarketingContactListResponse)((OkObjectResult)await Controller(db)
            .List("withdrawn", null, 50, 0, CancellationToken.None)).Value!;
        withdrawn.Items.Single().Brevo.State.Should().Be("notAtBrevo");

        var byName = (MarketingContactListResponse)((OkObjectResult)await Controller(db)
            .List("all", "SYNCED", 50, 0, CancellationToken.None)).Value!;
        byName.Total.Should().Be(1);
        (await Controller(db).List("bogus", null, 50, 0, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Patch_sets_the_admin_name_and_clearing_both_means_no_name()
    {
        Guid id = Add("a@example.com", MarketingConsentStatus.OptedIn);
        await using InferenceApiDbContext db = _h.NewDb();
        var ok = (OkObjectResult)await Controller(db).SetName(id, new MarketingNameRequest(" Ada ", "Lovelace"), CancellationToken.None);
        ((MarketingContactDto)ok.Value!).FirstName.Should().Be("Ada");
        MarketingConsent row = Row("a@example.com");
        row.LastName.Should().Be("Lovelace");
        row.NameSource.Should().Be(MarketingNameSource.Admin);
        row.NameUpdatedAt.Should().Be(_h.Now);

        await using InferenceApiDbContext db2 = _h.NewDb();
        await Controller(db2).SetName(id, new MarketingNameRequest(null, ""), CancellationToken.None);
        row = Row("a@example.com");
        row.FirstName.Should().BeNull();
        row.LastName.Should().BeNull();
        row.NameSource.Should().Be(MarketingNameSource.Admin);
    }

    [Fact]
    public async Task Patch_rejects_an_invalid_name_and_an_unknown_id()
    {
        Guid id = Add("a@example.com", MarketingConsentStatus.OptedIn);
        await using InferenceApiDbContext db = _h.NewDb();
        (await Controller(db).SetName(id, new MarketingNameRequest("‮x", null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        Row("a@example.com").NameSource.Should().BeNull();
        (await Controller(db).SetName(Guid.NewGuid(), new MarketingNameRequest("A", null), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Patch_refuses_a_withdrawn_contact_with_409()
    {
        Guid id = Add("w2@example.com", MarketingConsentStatus.Withdrawn);
        await using InferenceApiDbContext db = _h.NewDb();
        var result = (ConflictObjectResult)await Controller(db).SetName(id, new MarketingNameRequest("Ada", null), CancellationToken.None);
        result.StatusCode.Should().Be(409);
        Row("w2@example.com").FirstName.Should().BeNull();
    }

    [Fact]
    public async Task List_caps_the_query_at_200_characters()
    {
        await using InferenceApiDbContext db = _h.NewDb();
        (await Controller(db).List("all", new string('a', 201), 50, 0, CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await Controller(db).List("all", new string('a', 200), 50, 0, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }
}
