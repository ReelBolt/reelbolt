#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// Security condition 3 (public-api): purging a Team workspace succeeds when an API key's service user created a
/// project and ran it. The schema is built by migrating both contexts on a fresh Postgres (skipped unless
/// <c>REELBOLT_TEST_PG</c> is set), so every real foreign key is in force. Seeded: the key, its service user, a
/// project, a workflow execution, key spend, a run request, a webhook endpoint and a delivery. After the purge none
/// of them remains and no foreign-key error defers the account.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class AccountPurgeApiKeyPostgresTests
{
    private const string ApiHistory = "__EFMigrationsHistory_Api";
    private const string EngineHistory = "__EFMigrationsHistory_Workflow";

    [PostgresFact]
    public async Task Purging_a_team_workspace_removes_the_api_keys_runs_spend_requests_and_webhooks_without_an_fk_error()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_purge_apikey_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            string connection = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
            DbContextOptions<InferenceApiDbContext> apiOptions = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseNpgsql(connection, o => o.MigrationsHistoryTable(ApiHistory)).Options;
            DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseNpgsql(connection, o => o.MigrationsHistoryTable(EngineHistory)).Options;
            await using (InferenceApiDbContext api = new(apiOptions))
                await api.Database.MigrateAsync();
            await using (WorkflowEngineDbContext engine = new(engineOptions))
                await engine.Database.MigrateAsync();

            Guid ownerId = Guid.NewGuid();
            Guid serviceUserId = Guid.NewGuid();
            Guid orgId = Guid.NewGuid();
            Guid projectId = Guid.NewGuid();
            Guid workflowId = Guid.NewGuid();
            Guid keyId = Guid.NewGuid();
            Guid endpointId = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;

            await using (InferenceApiDbContext seed = new(apiOptions))
            {
                seed.ApplicationUsers.Add(new ApplicationUser
                {
                    Id = ownerId,
                    Email = "owner@example.com",
                    DisplayName = "Owner",
                    PasswordHash = "hash",
                    CreatedAt = now.AddDays(-100),
                    EmailVerifiedAt = now.AddDays(-100),
                    DeletionRequestedAt = now.AddDays(-40)
                });
                seed.ApplicationUsers.Add(new ApplicationUser { Id = serviceUserId, Email = "apikey+x@service.invalid", IsServiceAccount = true });
                seed.Organizations.Add(new Organization
                {
                    Id = orgId,
                    Name = "Team",
                    Slug = "team-" + orgId.ToString("N")[..8],
                    Kind = OrganizationKind.Team,
                    CreatedByUserId = ownerId,
                    CreatedAt = now.AddDays(-100),
                    UpdatedAt = now.AddDays(-100)
                });
                seed.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = orgId, UserId = ownerId, Role = OrganizationRole.Owner, CreatedAt = now.AddDays(-100) });
                seed.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = orgId, UserId = serviceUserId, Role = OrganizationRole.Member, CreatedAt = now.AddDays(-90) });
                seed.Projects.Add(new Project { Id = projectId, Name = "p", OwnerId = serviceUserId, OrganizationId = orgId, CreatedAt = now.AddDays(-80), UpdatedAt = now.AddDays(-80) });
                seed.ApiKeys.Add(new ApiKey
                {
                    Id = keyId,
                    OrganizationId = orgId,
                    CreatedByUserId = ownerId,
                    ServiceUserId = serviceUserId,
                    Name = "k",
                    KeyId = "rbk_" + keyId.ToString("N")[..8],
                    SecretHash = "hash",
                    LastFour = "abcd",
                    Scopes = ["runs:write"],
                    CreatedAt = now.AddDays(-90)
                });
                seed.ApiKeySpends.Add(new ApiKeySpend
                {
                    Id = Guid.NewGuid(),
                    ApiKeyId = keyId,
                    OrganizationId = orgId,
                    ReservationEntryId = Guid.NewGuid(),
                    HeldMillis = 1000,
                    CreatedAt = now.AddDays(-70)
                });
                seed.ApiRunRequests.Add(new ApiRunRequest
                {
                    Id = Guid.NewGuid(),
                    ApiKeyId = keyId,
                    OrganizationId = orgId,
                    ProjectId = projectId,
                    WorkflowId = workflowId
                });
                seed.WebhookEndpoints.Add(new WebhookEndpoint
                {
                    Id = endpointId,
                    OrganizationId = orgId,
                    Url = "https://example.com/hook",
                    Events = ["run.completed"],
                    SecretEncrypted = "secret",
                    CreatedByUserId = serviceUserId,
                    CreatedByApiKeyId = keyId
                });
                seed.WebhookDeliveries.Add(new WebhookDelivery
                {
                    Id = "whd_" + Guid.NewGuid().ToString("N"),
                    EndpointId = endpointId,
                    OrganizationId = orgId,
                    ProjectId = projectId,
                    EventType = "run.completed",
                    EventKey = "run-1"
                });
                await seed.SaveChangesAsync();
            }

            await using (WorkflowEngineDbContext engineSeed = new(engineOptions))
            {
                engineSeed.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflowId, Name = "w", ProjectId = projectId, CreatedAt = now, UpdatedAt = now });
                await engineSeed.SaveChangesAsync();
                engineSeed.WorkflowExecutions.Add(new WorkflowExecution
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = workflowId,
                    ProjectId = projectId,
                    OrganizationId = orgId,
                    InitiatedByUserId = serviceUserId,
                    ApiKeyId = keyId,
                    QueuedAt = now.AddDays(-60)
                });
                await engineSeed.SaveChangesAsync();
            }

            ServiceCollection services = new();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(connection));
            Mock<IAmazonS3> s3 = new();
            s3.Setup(s => s.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListObjectsV2Response { S3Objects = [], IsTruncated = false });
            services.AddSingleton(s3.Object);
            services.AddSingleton<ITenantContext>(new TenantContext(new HttpContextAccessor()));
            services.AddScoped<IAccountBillingCanceller, AccountBillingCanceller>();
            Mock<IVectorIndexService> vectors = new();
            vectors.Setup(v => v.DeleteProjectCollectionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
            services.AddSingleton(vectors.Object);
            services.AddSingleton(Mock.Of<IInferenceProviderResolver>());
            services.AddSingleton(Mock.Of<ISpeechSynthesisClientFactory>());
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            await using ServiceProvider provider = services.BuildServiceProvider();

            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MinIO:BucketName"] = "test-bucket",
                ["Tenancy:Mode"] = "Cloud"
            }).Build();
            AccountPurgeService service = new(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AccountOptions { Purge = new AccountPurgeOptions { Enabled = true, GraceDays = 30, PurgeUnverified = true } }),
                configuration,
                NullLogger<AccountPurgeService>.Instance);

            AccountPurgeUserResult result = await service.PurgeUserAsync(ownerId, now, default);

            result.Purged.Should().BeTrue("no foreign key may defer the purge: {0}", string.Join("; ", result.Failures));
            await using InferenceApiDbContext verify = new(apiOptions);
            await using WorkflowEngineDbContext verifyEngine = new(engineOptions);
            (await verifyEngine.WorkflowExecutions.IgnoreQueryFilters().CountAsync(e => e.OrganizationId == orgId)).Should().Be(0);
            (await verify.Projects.IgnoreQueryFilters().CountAsync(p => p.OrganizationId == orgId)).Should().Be(0);
            (await verify.ApiKeys.IgnoreQueryFilters().CountAsync(k => k.OrganizationId == orgId)).Should().Be(0);
            (await verify.ApiKeySpends.CountAsync(s => s.OrganizationId == orgId)).Should().Be(0);
            (await verify.ApiRunRequests.CountAsync(r => r.OrganizationId == orgId)).Should().Be(0);
            (await verify.WebhookEndpoints.IgnoreQueryFilters().CountAsync(w => w.OrganizationId == orgId)).Should().Be(0);
            (await verify.WebhookDeliveries.IgnoreQueryFilters().CountAsync(d => d.OrganizationId == orgId)).Should().Be(0);
            (await verify.ApplicationUsers.IgnoreQueryFilters().CountAsync(u => u.Id == serviceUserId || u.Id == ownerId)).Should().Be(0);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
