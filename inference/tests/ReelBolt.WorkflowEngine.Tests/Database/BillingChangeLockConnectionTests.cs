#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Database;

/// <summary>
/// The billing change lock is built from the configured connection string, not from
/// <c>DatabaseFacade.GetConnectionString()</c>: with <c>ConfigureDataSource</c> the latter has no
/// <c>Password</c>, so the lock could not authenticate and every seat/plan change answered 500.
/// </summary>
public class BillingChangeLockConnectionTests
{
    [Fact]
    public void The_lock_resolved_from_the_inference_api_container_keeps_the_configured_password()
    {
        using var root = CompositionRootHarness.Build(
            "f1-billing-lock",
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=secretpw"
            });
        using IServiceScope scope = root.Services.CreateScope();

        IBillingChangeLock resolved = scope.ServiceProvider.GetRequiredService<IBillingChangeLock>();

        PostgresBillingChangeLock billingLock = resolved.Should().BeOfType<PostgresBillingChangeLock>().Subject;
        NpgsqlConnectionStringBuilder csb = new(billingLock.ConnectionString);
        csb.Password.Should().Be("secretpw");
        csb.Host.Should().Be("127.0.0.1");
        csb.Username.Should().Be("u");
    }
}

/// <summary>The lock from DI authenticates against a real password-auth server and excludes a second claimer.</summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class BillingChangeLockPostgresTests
{
    [Fact]
    public async Task The_lock_resolved_from_di_claims_and_excludes_a_concurrent_claimer()
    {
        string? baseConnectionString = PostgresTestEnvironment.BaseConnectionString();
        if (baseConnectionString is null)
        {
            return;
        }

        using var root = CompositionRootHarness.Build(
            "f1-billing-lock-pg",
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = PostgresTestEnvironment.ForDatabase(baseConnectionString, "postgres", maxPoolSize: 2)
            });
        using IServiceScope first = root.Services.CreateScope();
        using IServiceScope second = root.Services.CreateScope();
        IBillingChangeLock firstLock = first.ServiceProvider.GetRequiredService<IBillingChangeLock>();
        IBillingChangeLock secondLock = second.ServiceProvider.GetRequiredService<IBillingChangeLock>();
        firstLock.Should().BeOfType<PostgresBillingChangeLock>();
        Guid organizationId = Guid.NewGuid();

        await using (IAsyncDisposable? claim = await firstLock.TryAcquireAsync(organizationId, CancellationToken.None))
        {
            claim.Should().NotBeNull("the lock must authenticate with the configured password");

            IAsyncDisposable? rival = await secondLock.TryAcquireAsync(organizationId, CancellationToken.None);
            rival.Should().BeNull("the first claim is still held");
        }

        await using IAsyncDisposable? after = await secondLock.TryAcquireAsync(organizationId, CancellationToken.None);
        after.Should().NotBeNull("the first claim was released");
    }
}
