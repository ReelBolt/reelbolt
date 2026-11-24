using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// No plan caps video spend per day (Owner decision): the plan decides WHO may generate
/// (<c>videoGenerationAllowed</c>); only the user's own limit, the step's <c>MaxSpendCredits</c>, the credit
/// balance and the operator ceiling bound how much.
/// </summary>
public sealed class NoPlanDailyVideoCapTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public NoPlanDailyVideoCapTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private static string ShippedCatalog() => File.ReadAllText(PlanCatalogSeeder.DefaultPath);

    private static OrganizationEntitlementSet Shipped(string plan)
        => PlanEntitlements.ToSet(PlanCatalogSeeder.Load(ShippedCatalog()).Single(p => p.Key == plan));

    private static VideoGenerationBudgetGate NewGate(SharedInMemoryDbFactory factory, OrganizationEntitlementSet set)
        => new(
            factory,
            Options.Create(new VideoGenerationOptions { DailyBudgetCredits = 100000m, GlobalDailyBudgetCredits = 100000m }),
            new ConfigOrganizationEntitlements(new TenancyOptions { DefaultEntitlements = set }));

    private static async Task SeedAsync(SharedInMemoryDbFactory factory, Guid projectId, Guid orgId, decimal spentCredits)
    {
        await using var db = factory.CreateDbContext();
        db.Projects.Add(new Project { Id = projectId, Name = "p", OrganizationId = orgId });
        if (spentCredits > 0m)
        {
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                StepResultId = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                OrganizationId = orgId,
                ClipId = "c",
                RequestHash = Guid.NewGuid().ToString(),
                ProviderId = Guid.NewGuid(),
                Status = ExternalGenerationJobStatus.Succeeded,
                CostEstimate = spentCredits,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    private static VideoGenerationReservationRequest Request(Guid projectId, decimal cost)
        => new(projectId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100000m,
            new[] { new VideoGenerationTakeRequest("clip-1", "hash-1", cost) });

    [Fact]
    public async Task Pro_keeps_generating_past_the_old_833_credit_plan_cap()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        await SeedAsync(factory, projectId, Guid.NewGuid(), spentCredits: 833.34m);

        var result = await NewGate(factory, Shipped("pro")).ReserveAsync(Request(projectId, 50m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
    }

    [Theory]
    [InlineData("trial")]
    [InlineData("creator")]
    public async Task Plans_without_video_generation_are_still_refused_and_write_nothing(string plan)
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        await SeedAsync(factory, projectId, Guid.NewGuid(), spentCredits: 0m);

        var result = await NewGate(factory, Shipped(plan)).ReserveAsync(Request(projectId, 1m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("PLAN_NOT_ENTITLED:");
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Any().Should().BeFalse();
    }

    [Fact]
    public void The_shipped_catalog_states_no_daily_video_cap_for_any_plan()
    {
        ShippedCatalog().Should().NotContainEquivalentOf("videoGenerationDailyCredits");
        PlanCatalogSeeder.Load(ShippedCatalog()).Select(p => (p.Key, PlanEntitlements.ToSet(p).VideoGenAllowed))
            .Should().Equal(("trial", false), ("creator", false), ("pro", true), ("team", true), ("enterprise", true));
    }

    [Fact]
    public async Task Upsert_clears_the_old_cap_from_an_existing_plan_row()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            await PlanCatalogSeeder.UpsertAsync(db, PlanCatalogSeeder.Load(ShippedCatalog()));
            Plan pro = db.Plans.Single(p => p.Key == "pro");
            pro.EntitlementsJson = pro.EntitlementsJson.TrimEnd().TrimEnd('}') + ",\"videoGenerationDailyCredits\":833.3333}";
            await db.SaveChangesAsync();
        }

        await using (InferenceApiDbContext db = NewDb())
        {
            await PlanCatalogSeeder.UpsertAsync(db, PlanCatalogSeeder.Load(ShippedCatalog()));
        }

        await using (InferenceApiDbContext db = NewDb())
        {
            Plan pro = db.Plans.Single(p => p.Key == "pro");
            pro.EntitlementsJson.Should().NotContainEquivalentOf("videoGenerationDailyCredits");
            PlanEntitlements.ToSet(pro).VideoGenAllowed.Should().BeTrue();
        }
    }
}
