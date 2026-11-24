#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Data.Models.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// saas-promotions T-001: the constraints the promotions and marketing-consent schema itself
/// enforces, and the shipped Privacy version the same change bumps.
/// </summary>
public sealed class PromotionSchemaTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public PromotionSchemaTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private static Promotion NewPromotion(string? code) => new()
    {
        Id = Guid.NewGuid(),
        Kind = code is null ? PromotionKind.PackBonus : PromotionKind.CreditCode,
        Name = "promo",
        Code = code,
        Status = PromotionStatus.Active,
        CreatedByUserId = Guid.NewGuid(),
    };

    [Fact]
    public async Task Promotion_code_is_unique_when_present()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            // Two automatic promotions (no code) coexist: the uniqueness is only over real codes.
            db.Promotions.AddRange(NewPromotion(null), NewPromotion(null), NewPromotion("WELCOME10"));
            await db.SaveChangesAsync();
        }

        await using (InferenceApiDbContext db = NewDb())
        {
            db.Promotions.Add(NewPromotion("WELCOME10"));
            Func<Task> duplicate = () => db.SaveChangesAsync();
            await duplicate.Should().ThrowAsync<DbUpdateException>();
        }

        await using (InferenceApiDbContext db = NewDb())
        {
            (await db.Promotions.CountAsync()).Should().Be(3);
        }
    }

    [Fact]
    public async Task MarketingConsent_email_is_unique()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            db.MarketingConsents.Add(new MarketingConsent
            {
                Id = Guid.NewGuid(),
                Email = "ada@example.com",
                Status = MarketingConsentStatus.OptedIn,
                WordingVersion = "2026-10-marketing-v1",
                Source = MarketingConsentSource.Signup,
            });
            await db.SaveChangesAsync();
        }

        await using (InferenceApiDbContext db = NewDb())
        {
            db.MarketingConsents.Add(new MarketingConsent
            {
                Id = Guid.NewGuid(),
                Email = "ada@example.com",
                Status = MarketingConsentStatus.Pending,
                WordingVersion = "2026-10-marketing-v1",
                Source = MarketingConsentSource.SiteContact,
            });
            Func<Task> duplicate = () => db.SaveChangesAsync();
            await duplicate.Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Seeder_default_legal_versions_are_terms_and_privacy_2026_11_17()
    {
        await using InferenceApiDbContext db = NewDb();
        IConfiguration empty = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        await LegalDocumentSeeder.SeedAsync(db, empty, NullLogger.Instance);

        // AvatarLibraryLegalVersions moved both to 2026-11-17 (Terms = re-acceptance: objects, pictures, workspace library).
        (await db.LegalDocumentVersions.SingleAsync(d => d.Document == LegalDocument.Privacy)).Version
            .Should().Be("2026-11-17");
        (await db.LegalDocumentVersions.SingleAsync(d => d.Document == LegalDocument.Terms)).Version
            .Should().Be("2026-11-17");
    }
}
