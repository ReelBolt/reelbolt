using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>C6: trial provisioning on Sqlite, plus a real-Postgres concurrency test gated on <c>REELBOLT_TEST_PG</c>.</summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class CreditAccountProvisionerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public CreditAccountProvisionerTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private static CreditAccountProvisioner Make(Action<DbContextOptionsBuilder<InferenceApiDbContext>> use, bool enabled = true, Dictionary<string, string?>? extra = null)
    {
        ServiceCollection services = new();
        services.AddScoped(_ =>
        {
            DbContextOptionsBuilder<InferenceApiDbContext> b = new();
            use(b);
            return new InferenceApiDbContext(b.Options);
        });
        Dictionary<string, string?> cfg = new() { ["Billing:Enabled"] = enabled ? "true" : "false", ["Billing:FirstVideoGrant:Enabled"] = "false" };
        foreach (KeyValuePair<string, string?> kv in extra ?? [])
        {
            cfg[kv.Key] = kv.Value;
        }

        return new CreditAccountProvisioner(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(cfg).Build(),
            NullLogger<CreditAccountProvisioner>.Instance);
    }

    private CreditAccountProvisioner Sqlite(bool enabled = true, Dictionary<string, string?>? extra = null)
        => Make(b => b.UseSqlite(_connection), enabled, extra);

    private async Task<(Guid User, Guid Org)> Seed(string email, bool verified = true, OrganizationKind kind = OrganizationKind.Personal, OrganizationRole role = OrganizationRole.Owner)
    {
        await using InferenceApiDbContext db = NewDb();
        return await SeedInto(db, email, verified, kind, role);
    }

    private static async Task<(Guid User, Guid Org)> SeedInto(InferenceApiDbContext db, string email, bool verified = true, OrganizationKind kind = OrganizationKind.Personal, OrganizationRole role = OrganizationRole.Owner)
    {
        Guid user = Guid.NewGuid();
        Guid org = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = email, EmailVerifiedAt = verified ? DateTime.UtcNow : null });
        db.Organizations.Add(new Organization { Id = org, Name = "w", Slug = "w-" + org.ToString("N"), Kind = kind });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org, UserId = user, Role = role });
        await db.SaveChangesAsync();
        return (user, org);
    }

    private async Task<long> Balance(Guid org)
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditAccounts.Where(a => a.OrganizationId == org).Select(a => a.BalanceMillis).FirstOrDefaultAsync();
    }

    [Fact]
    public async Task VerifiedOwner_IsGrantedOnce_AndRepeatCallsAreNoOps()
    {
        (Guid user, Guid org) = await Seed("a@example.com");
        CreditAccountProvisioner p = Sqlite();

        (await p.EnsureTrialGrantAsync(user, org)).Should().Be(TrialProvisioningOutcome.Granted);
        (await p.EnsureTrialGrantAsync(user, org)).Should().Be(TrialProvisioningOutcome.AlreadyClaimed);
        (await Balance(org)).Should().Be(100_000);

        await using InferenceApiDbContext db = NewDb();
        db.CreditGrants.Count(g => g.Source == CreditGrantSource.Trial).Should().Be(1);
        db.TrialClaims.Count().Should().Be(1);
    }

    [Fact]
    public async Task SecondOrg_WithTheSameEmail_GetsNothing()
    {
        (Guid u1, Guid o1) = await Seed("same@example.com");
        (Guid u2, Guid o2) = await Seed("SAME@example.com ");
        CreditAccountProvisioner p = Sqlite();

        (await p.EnsureTrialGrantAsync(u1, o1)).Should().Be(TrialProvisioningOutcome.Granted);
        (await p.EnsureTrialGrantAsync(u2, o2)).Should().Be(TrialProvisioningOutcome.AlreadyClaimed);
        (await Balance(o2)).Should().Be(0);
    }

    [Fact]
    public async Task Unverified_NonOwner_NonPersonal_OrNoOrg_GetNothing()
    {
        CreditAccountProvisioner p = Sqlite();
        (Guid u1, Guid o1) = await Seed("unv@example.com", verified: false);
        (Guid u2, Guid o2) = await Seed("member@example.com", role: OrganizationRole.Member);
        (Guid u3, Guid o3) = await Seed("team@example.com", kind: OrganizationKind.Team);

        (await p.EnsureTrialGrantAsync(u1, o1)).Should().Be(TrialProvisioningOutcome.NotEligible);
        (await p.EnsureTrialGrantAsync(u2, o2)).Should().Be(TrialProvisioningOutcome.NotEligible);
        (await p.EnsureTrialGrantAsync(u3, o3)).Should().Be(TrialProvisioningOutcome.NotEligible);
        (await p.EnsureTrialGrantAsync(u1, null)).Should().Be(TrialProvisioningOutcome.NotEligible);
        (await Balance(o1) + await Balance(o2) + await Balance(o3)).Should().Be(0);

        // Verifying later makes the same org eligible: the unverified attempt wrote no claim.
        await using (InferenceApiDbContext db = NewDb())
        {
            await db.ApplicationUsers.Where(u => u.Id == u1).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAt, DateTime.UtcNow));
        }

        (await p.EnsureTrialGrantAsync(u1, o1)).Should().Be(TrialProvisioningOutcome.Granted);
    }

    [Theory]
    [InlineData("John.Doe+promo@Gmail.com", "johndoe@gmail.com")]
    [InlineData("j.o.h.n.d.o.e@googlemail.com", "johndoe@gmail.com")]
    [InlineData("  JohnDoe@GMAIL.COM ", "johndoe@gmail.com")]
    [InlineData("john.doe+x@example.com", "john.doe@example.com")]
    [InlineData("john.doe@outlook.com", "john.doe@outlook.com")]
    public void Normalisation(string input, string expected)
        => CreditAccountProvisioner.NormalizeEmail(input).Should().Be(expected);

    [Fact]
    public void GmailVariants_ShareOneHash_ButOtherDomainsKeepDots()
    {
        CreditAccountProvisioner.HashEmail("J.Doe+a@googlemail.com").Should().Be(CreditAccountProvisioner.HashEmail("jdoe@gmail.com"));
        CreditAccountProvisioner.HashEmail("j.doe@example.com").Should().NotBe(CreditAccountProvisioner.HashEmail("jdoe@example.com"));
    }

    [Fact]
    public async Task GmailAliases_AcrossOrgs_ClaimOnlyOnce()
    {
        (Guid u1, Guid o1) = await Seed("john.doe@gmail.com");
        (Guid u2, Guid o2) = await Seed("johndoe+trial@googlemail.com");
        CreditAccountProvisioner p = Sqlite();

        (await p.EnsureTrialGrantAsync(u1, o1)).Should().Be(TrialProvisioningOutcome.Granted);
        (await p.EnsureTrialGrantAsync(u2, o2)).Should().Be(TrialProvisioningOutcome.AlreadyClaimed);
    }

    [Fact]
    public async Task SelfHost_NeverGrants()
    {
        (Guid user, Guid org) = await Seed("selfhost@example.com");
        CreditAccountProvisioner p = Sqlite(enabled: false);

        (await p.EnsureTrialGrantAsync(user, org)).Should().Be(TrialProvisioningOutcome.Disabled);
        (await Balance(org)).Should().Be(0);
        await using InferenceApiDbContext db = NewDb();
        db.TrialClaims.Any().Should().BeFalse();
    }

    [Fact]
    public async Task ConfigOverridesTrialSize()
    {
        (Guid user, Guid org) = await Seed("cfg@example.com");
        await Sqlite(extra: new() { ["Billing:Trial:Credits"] = "250" }).EnsureTrialGrantAsync(user, org);
        (await Balance(org)).Should().Be(250_000);
    }

    private static Dictionary<string, string?> FirstVideoOn(params (string Key, string? Value)[] more)
    {
        Dictionary<string, string?> d = new() { ["Billing:FirstVideoGrant:Enabled"] = "true" };
        foreach ((string key, string? value) in more)
        {
            d[key] = value;
        }

        return d;
    }

    private async Task<List<CreditGrant>> PromoGrants(Guid org)
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditGrants.Where(g => g.OrganizationId == org && g.Source == CreditGrantSource.Promo).ToListAsync();
    }

    [Fact]
    public async Task FirstVideo_IsGrantedOnce_With750Credits_Expiring30Days_AndReplayIsIdempotent()
    {
        (Guid user, Guid org) = await Seed("fv@example.com");
        CreditAccountProvisioner p = Sqlite(extra: FirstVideoOn());

        (await p.EnsureTrialGrantAsync(user, org)).Should().Be(TrialProvisioningOutcome.Granted);
        (await p.EnsureTrialGrantAsync(user, org)).Should().Be(TrialProvisioningOutcome.AlreadyClaimed);

        (await Balance(org)).Should().Be(100_000 + 750_000);
        List<CreditGrant> promo = await PromoGrants(org);
        promo.Should().ContainSingle();
        promo[0].AmountMillis.Should().Be(750_000);
        promo[0].ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(5));
        await using InferenceApiDbContext db = NewDb();
        db.CreditLedgerEntries.Count(e => e.IdempotencyKey == CreditAccountProvisioner.FirstVideoKey(org) && e.Note == "first-video").Should().Be(1);
    }

    [Fact]
    public async Task FirstVideo_SecondOrgOfTheSamePerson_GetsNone()
    {
        (Guid u1, Guid o1) = await Seed("dup@example.com");
        (Guid u2, Guid o2) = await Seed("DUP@example.com ");
        CreditAccountProvisioner p = Sqlite(extra: FirstVideoOn());

        await p.EnsureTrialGrantAsync(u1, o1);
        await p.EnsureTrialGrantAsync(u2, o2);

        (await PromoGrants(o1)).Should().ContainSingle();
        (await Balance(o2)).Should().Be(0);
    }

    [Fact]
    public async Task FirstVideo_UnverifiedAndBillingOff_GetNone()
    {
        (Guid u1, Guid o1) = await Seed("unv2@example.com", verified: false);
        (Guid u2, Guid o2) = await Seed("off@example.com");

        await Sqlite(extra: FirstVideoOn()).EnsureTrialGrantAsync(u1, o1);
        await Sqlite(enabled: false, extra: FirstVideoOn()).EnsureTrialGrantAsync(u2, o2);

        (await PromoGrants(o1)).Should().BeEmpty();
        (await PromoGrants(o2)).Should().BeEmpty();
    }

    [Fact]
    public async Task FirstVideo_KillSwitch_GrantsOnlyTheTrial()
    {
        (Guid user, Guid org) = await Seed("kill@example.com");
        await Sqlite(extra: FirstVideoOn(("Billing:FirstVideoGrant:Enabled", "false"))).EnsureTrialGrantAsync(user, org);
        (await PromoGrants(org)).Should().BeEmpty();
        (await Balance(org)).Should().Be(100_000);
    }

    [Fact]
    public async Task FirstVideo_ConfigOverridesSizeAndExpiry_AndExistingClaimHolderGetsNothing()
    {
        (Guid user, Guid org) = await Seed("cfg2@example.com");
        // Existing account: already holds a trial claim from before the feature shipped.
        await Sqlite().EnsureTrialGrantAsync(user, org);
        await Sqlite(extra: FirstVideoOn()).EnsureTrialGrantAsync(user, org);
        (await PromoGrants(org)).Should().BeEmpty();

        (Guid u2, Guid o2) = await Seed("cfg3@example.com");
        await Sqlite(extra: FirstVideoOn(("Billing:FirstVideoGrant:Credits", "300"), ("Billing:FirstVideoGrant:ExpiryDays", "7"))).EnsureTrialGrantAsync(u2, o2);
        List<CreditGrant> promo = await PromoGrants(o2);
        promo.Should().ContainSingle();
        promo[0].AmountMillis.Should().Be(300_000);
        promo[0].ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Concurrent_DoubleCall_OnARealPostgres_GrantsOnce()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_c6_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options;
            Guid user;
            Guid org;
            await using (InferenceApiDbContext setup = new(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync();
                (user, org) = await SeedInto(setup, "race@example.com");
            }

            CreditAccountProvisioner p = Make(b => b.UseNpgsql(csb.ConnectionString));
            TrialProvisioningOutcome[] outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => p.EnsureTrialGrantAsync(user, org)));

            outcomes.Count(o => o == TrialProvisioningOutcome.Granted).Should().Be(1);
            outcomes.Should().NotContain(TrialProvisioningOutcome.Failed);

            await using InferenceApiDbContext verify = new(options);
            verify.CreditGrants.Count(g => g.Source == CreditGrantSource.Trial).Should().Be(1);
            verify.CreditAccounts.Single(a => a.OrganizationId == org).BalanceMillis.Should().Be(100_000);
            verify.TrialClaims.Count().Should().Be(1);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
