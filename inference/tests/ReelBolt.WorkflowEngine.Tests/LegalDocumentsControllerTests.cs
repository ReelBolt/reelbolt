#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The versions of the legal documents this deployment serves (F-7). This table is the whole reason
/// a recorded acceptance means anything: <c>legal_acceptances.version</c> used to be whatever string
/// the caller sent — signup and <c>/auth/accept-terms</c> only checked non-empty and at most 32
/// characters — so an operator could not answer "which version did this user accept, and when" from
/// the database at all. What is contractual here:
/// <list type="bullet">
/// <item>a missing row is a 503, never an empty version, because the Go API must refuse to record
/// rather than invent;</item>
/// <item>publishing is the platform claim only, and it stamps <c>effective_at</c>/<c>updated_by</c>
/// because that instant is what an old acceptance is dated against;</item>
/// <item>publishing the version already current moves nothing.</item>
/// </list>
/// </summary>
public class LegalDocumentsControllerTests
{
    private sealed class User(bool isAdmin) : ICurrentUser
    {
        public Guid UserId => Guid.NewGuid();
        public string Email => "operator@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => isAdmin;
        public Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static LegalDocumentsController Controller(InferenceApiDbContext db, bool isAdmin)
        => new(db, new User(isAdmin));

    private static InferenceApiDbContext Seeded()
    {
        InferenceApiDbContext db = NewDb();
        DateTime now = DateTime.UtcNow;
        db.LegalDocumentVersions.AddRange(
            new LegalDocumentVersion { Document = LegalDocument.Terms, Version = "2026-10", EffectiveAt = now, UpdatedAt = now, UpdatedBy = "config" },
            new LegalDocumentVersion { Document = LegalDocument.Privacy, Version = "2026-10", EffectiveAt = now, UpdatedAt = now, UpdatedBy = "config" });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task The_served_versions_are_readable_without_a_session()
    {
        // The proxy gate runs before anything is authenticated, so this read cannot require a
        // caller's session; the versions are public — they are printed on the signup page.
        await using InferenceApiDbContext db = Seeded();

        LegalDocumentsResponse view = (await Controller(db, isAdmin: false).Get(CancellationToken.None)).Value!;

        view.TermsVersion.Should().Be("2026-10");
        view.PrivacyVersion.Should().Be("2026-10");
        view.Documents.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_document_with_no_row_is_503_rather_than_an_empty_version()
    {
        // "The server cannot say what it serves" must not read as "the server serves nothing": the
        // Go API refuses to record an acceptance in that state, and an empty string here would be a
        // version it could compare against.
        await using InferenceApiDbContext db = NewDb();
        db.LegalDocumentVersions.Add(new LegalDocumentVersion
        {
            Document = LegalDocument.Terms,
            Version = "2026-10",
            EffectiveAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        ActionResult<LegalDocumentsResponse> result = await Controller(db, isAdmin: true).Get(CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task A_non_admin_cannot_publish_and_writes_nothing()
    {
        await using InferenceApiDbContext db = Seeded();

        ActionResult<LegalDocumentResponse> result = await Controller(db, isAdmin: false)
            .Publish("Terms", new PublishLegalDocumentRequest("2026-11"), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        (await db.LegalDocumentVersions.SingleAsync(d => d.Document == LegalDocument.Terms)).Version.Should().Be("2026-10");
    }

    [Fact]
    public async Task Publishing_stamps_the_version_the_effective_time_and_the_actor()
    {
        await using InferenceApiDbContext db = Seeded();
        DateTime before = DateTime.UtcNow;

        LegalDocumentResponse published = (await Controller(db, isAdmin: true)
            .Publish("terms", new PublishLegalDocumentRequest(" 2026-11 "), CancellationToken.None)).Value!;

        published.Version.Should().Be("2026-11");
        published.UpdatedBy.Should().Be("operator@example.com");

        // The audit trail: this instant is the lower bound of the new version's validity and the
        // upper bound of the previous one's, which is what makes "when did this user accept X"
        // answerable from the database.
        LegalDocumentVersion row = await db.LegalDocumentVersions.SingleAsync(d => d.Document == LegalDocument.Terms);
        row.Version.Should().Be("2026-11");
        row.UpdatedBy.Should().Be("operator@example.com");
        row.EffectiveAt.Should().BeOnOrAfter(before);
        row.EffectiveAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Republishing_the_current_version_moves_nothing()
    {
        // A retried or scripted publication must not slide effective_at: that instant is the
        // boundary the audit trail is read against.
        await using InferenceApiDbContext db = Seeded();
        LegalDocumentVersion seeded = await db.LegalDocumentVersions.SingleAsync(d => d.Document == LegalDocument.Terms);
        DateTime effective = seeded.EffectiveAt;

        await Controller(db, isAdmin: true).Publish("Terms", new PublishLegalDocumentRequest("2026-10"), CancellationToken.None);

        LegalDocumentVersion row = await db.LegalDocumentVersions.SingleAsync(d => d.Document == LegalDocument.Terms);
        row.EffectiveAt.Should().Be(effective);
        row.UpdatedBy.Should().Be("config");
    }

    [Fact]
    public async Task An_unknown_document_or_a_blank_version_is_refused()
    {
        await using InferenceApiDbContext db = Seeded();
        LegalDocumentsController controller = Controller(db, isAdmin: true);

        (await controller.Publish("Cookies", new PublishLegalDocumentRequest("2026-11"), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.Publish("Terms", new PublishLegalDocumentRequest("   "), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.Publish("Terms", new PublishLegalDocumentRequest(new string('v', 51)), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();

        (await db.LegalDocumentVersions.SingleAsync(d => d.Document == LegalDocument.Terms)).Version.Should().Be("2026-10");
    }

    [Fact]
    public async Task A_document_this_deployment_never_seeded_cannot_be_published_over()
    {
        // Publishing is not an upsert: a deployment with no row for a document is a deployment whose
        // seeding did not run, and inventing the row here would hide that.
        await using InferenceApiDbContext db = NewDb();

        (await Controller(db, isAdmin: true).Publish("Terms", new PublishLegalDocumentRequest("2026-11"), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundObjectResult>();
        db.LegalDocumentVersions.Should().BeEmpty();
    }

    private static IConfiguration Config(string? terms, string? privacy)
    {
        Dictionary<string, string?> values = new();
        if (terms is not null) values[LegalDocumentSeeder.TermsVersionKey] = terms;
        if (privacy is not null) values[LegalDocumentSeeder.PrivacyVersionKey] = privacy;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public async Task The_seeder_records_a_version_for_every_document()
    {
        await using InferenceApiDbContext db = NewDb();

        await LegalDocumentSeeder.SeedAsync(db, Config("2026-11", "2026-12"), NullLogger.Instance);

        List<LegalDocumentVersion> rows = await db.LegalDocumentVersions.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(d => d.Document == LegalDocument.Terms).Version.Should().Be("2026-11");
        rows.Single(d => d.Document == LegalDocument.Privacy).Version.Should().Be("2026-12");
        rows.Should().OnlyContain(d => d.UpdatedBy == LegalDocumentSeeder.SeedActor);
    }

    [Fact]
    public async Task The_seeder_defaults_to_the_version_this_product_last_shipped()
    {
        // An install upgrading into F-7 has acceptances of "2026-10" already recorded by the web
        // default of the day; seeding anything else would re-prompt every existing account on the
        // upgrade itself.
        await using InferenceApiDbContext db = NewDb();

        await LegalDocumentSeeder.SeedAsync(db, Config(null, null), NullLogger.Instance);

        (await db.LegalDocumentVersions.SingleAsync(d => d.Document == LegalDocument.Terms)).Version
            .Should().Be(LegalDocumentSeeder.DefaultTermsVersion);
    }

    [Fact]
    public async Task The_seeder_never_overwrites_a_published_version()
    {
        // A restart must not roll a publication back: configuration decides what a deployment
        // STARTS with, and the row is the single source of truth afterwards.
        await using InferenceApiDbContext db = Seeded();

        await LegalDocumentSeeder.SeedAsync(db, Config("2027-01", "2027-01"), NullLogger.Instance);

        List<LegalDocumentVersion> rows = await db.LegalDocumentVersions.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(d => d.Version == "2026-10");
    }
}
