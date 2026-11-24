#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// The purge race on a real PostgreSQL (brevo-marketing-campaigns, Security medium). The purge reads the consent row
/// <c>FOR UPDATE</c>, so the campaign pipeline's write-ahead flag update either waits for the purge and then finds the row
/// gone (<c>consent_changed</c>), or the purge sees the flag and stages the erasure. The Sqlite and InMemory providers
/// take no row lock, so only a Postgres run can show the interleaving.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class MarketingPurgeLockPostgresTests : IClassFixture<PostgresTestDatabase>
{
    private readonly PostgresTestDatabase _pg;

    public MarketingPurgeLockPostgresTests(PostgresTestDatabase pg) => _pg = pg;

    private async Task<string> AddConsentAsync(bool flagged)
    {
        string email = "purge-" + Guid.NewGuid().ToString("N")[..10] + "@example.test";
        await using InferenceApiDbContext db = _pg.NewDb();
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = Guid.NewGuid(),
            Email = email,
            Status = MarketingConsentStatus.OptedIn,
            WordingVersion = "2026-10-marketing-v1",
            Source = MarketingConsentSource.SiteContact,
            ConsentedAt = DateTime.UtcNow,
            ConfirmedAt = DateTime.UtcNow,
            RequestedAt = DateTime.UtcNow,
            BrevoSyncedAt = flagged ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();
        return email;
    }

    private static ApplicationUser UserWith(string email) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        DisplayName = "U",
        EmailVerifiedAt = DateTime.UtcNow,
    };

    [PostgresFact]
    public async Task The_write_ahead_flag_update_waits_for_a_purge_that_has_read_the_row_and_then_finds_it_gone()
    {
        string email = await AddConsentAsync(flagged: false);
        MarketingSuppressionHasher hasher = new(new InMemorySecretStore());

        await using InferenceApiDbContext purge = _pg.NewDb();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await purge.Database.BeginTransactionAsync();
        await MarketingConsentErasure.StageAsync(purge, UserWith(email), hasher, DateTime.UtcNow, CancellationToken.None, new TestProtector());

        // step 5 of the pipeline, on its own connection, while the purge holds its read
        Task<int> flag = Task.Run(async () =>
        {
            await using InferenceApiDbContext pipeline = _pg.NewDb();
            return await pipeline.MarketingConsents
                .Where(c => c.Email == email && c.Status == MarketingConsentStatus.OptedIn)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.BrevoSyncedAt, DateTime.UtcNow));
        });

        (await Task.WhenAny(flag, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(flag, "the purge's FOR UPDATE read holds the row");

        await purge.SaveChangesAsync();
        await tx.CommitAsync();

        (await flag.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(0, "the row is gone, so the flagged count no longer matches and the run fails consent_changed");
        await using InferenceApiDbContext check = _pg.NewDb();
        (await check.MarketingConsents.AnyAsync(c => c.Email == email)).Should().BeFalse();
        (await check.MarketingBrevoErasures.CountAsync(e => e.ProtectedEmail == new TestProtector().Protect(email))).Should().Be(0, "the flag was never set, so there is nothing at Brevo to erase");
    }

    [PostgresFact]
    public async Task A_purge_that_reads_a_flagged_row_stages_the_erasure_for_it()
    {
        string email = await AddConsentAsync(flagged: true);
        MarketingSuppressionHasher hasher = new(new InMemorySecretStore());

        await using InferenceApiDbContext purge = _pg.NewDb();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await purge.Database.BeginTransactionAsync();
        await MarketingConsentErasure.StageAsync(purge, UserWith(email), hasher, DateTime.UtcNow, CancellationToken.None, new TestProtector());
        await purge.SaveChangesAsync();
        await tx.CommitAsync();

        await using InferenceApiDbContext check = _pg.NewDb();
        (await check.MarketingConsents.AnyAsync(c => c.Email == email)).Should().BeFalse();
        (await check.MarketingBrevoErasures.CountAsync(e => e.ProtectedEmail == new TestProtector().Protect(email))).Should().Be(1);
    }
}
