using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The production <see cref="IComputeEntitlements"/>: the plan half comes from
/// <see cref="IOrganizationEntitlements"/> (B8a's stack) and the preference half from a read of
/// <c>organizations.compute_preference</c>, the column A1 added. Joining two genuinely different
/// sources is worth a test of its own — especially the fail-open default, which is the value an
/// organization the engine cannot find must produce.
/// </summary>
public class DbComputeEntitlementsTests
{
    [Theory]
    [InlineData(ComputePreference.Auto)]
    [InlineData(ComputePreference.CloudOnly)]
    [InlineData(ComputePreference.PreferRunner)]
    [InlineData(ComputePreference.RunnerOnly)]
    public async Task ReadsTheOrganizationsOwnPreference(ComputePreference preference)
    {
        using Harness harness = new(OrganizationEntitlementSet.Unlimited);
        Guid organization = await harness.SeedAsync(preference);

        ComputeEntitlements result = await harness.Entitlements.GetAsync(organization);

        result.Preference.Should().Be(preference);
        result.RunnerAllowed.Should().BeTrue();
        result.CloudPoolAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task AnOrganizationTheEngineCannotFind_ReadsAsAuto_NotAsSomethingItWasNeverAskedFor()
    {
        using Harness harness = new(OrganizationEntitlementSet.Unlimited);

        ComputeEntitlements result = await harness.Entitlements.GetAsync(Guid.NewGuid());

        result.Preference.Should().Be(ComputePreference.Auto, "Auto is the column's own default, and it never routes work a user did not ask for");
        result.EffectivePreference.Should().Be(ComputePreference.Auto);
    }

    [Fact]
    public async Task APreTenancyExecution_ReadsAsAuto()
    {
        using Harness harness = new(OrganizationEntitlementSet.Unlimited);

        ComputeEntitlements result = await harness.Entitlements.GetAsync(Guid.Empty);

        result.Preference.Should().Be(ComputePreference.Auto);
    }

    [Fact]
    public async Task ThePlanEntitlementWithholdsRunnerUse_WithoutErasingWhatTheOrgAskedFor()
    {
        using Harness harness = new(new OrganizationEntitlementSet { RunnerAllowed = false });
        Guid organization = await harness.SeedAsync(ComputePreference.RunnerOnly);

        ComputeEntitlements result = await harness.Entitlements.GetAsync(organization);

        result.RunnerAllowed.Should().BeFalse();
        result.Preference.Should().Be(ComputePreference.RunnerOnly, "the stored preference is reported as stored");
        result.EffectivePreference.Should().Be(ComputePreference.CloudOnly, "but a plan that forbids runners downgrades it, so the selector can never route to one");
    }

    /// <summary>A scope-per-read harness over one in-memory database, with the plan stubbed out.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Harness(OrganizationEntitlementSet plan)
        {
            ServiceCollection services = new();
            // One store for the whole harness: the options lambda runs once per SCOPE, so generating
            // the name inside it would give every read its own empty database.
            string database = Guid.NewGuid().ToString();
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(database));
            _provider = services.BuildServiceProvider();
            Entitlements = new DbComputeEntitlements(new FixedPlan(plan), _provider.GetRequiredService<IServiceScopeFactory>());
        }

        public DbComputeEntitlements Entitlements { get; }

        public async Task<Guid> SeedAsync(ComputePreference preference)
        {
            Guid id = Guid.NewGuid();
            using (IServiceScope scope = _provider.CreateScope())
            {
                WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
                db.Organizations.Add(new Organization
                {
                    Id = id,
                    Name = "org",
                    Slug = id.ToString("N"),
                    Kind = OrganizationKind.Team,
                    ComputePreference = preference
                });
                await db.SaveChangesAsync();
            }

            return id;
        }

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>A stand-in for the plan-backed entitlement set, so this test needs no billing stack.</summary>
    private sealed class FixedPlan(OrganizationEntitlementSet set) : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(set);
    }
}
