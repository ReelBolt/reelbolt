#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.PublicApi.Runs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>
/// The run-queue dispatcher's <c>FOR UPDATE SKIP LOCKED</c> claim on a real Postgres (skipped unless
/// <c>REELBOLT_TEST_PG</c> is set): two dispatchers claiming at once start every due request exactly once,
/// and a key's requests start oldest first.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class ApiRunQueueDispatcherPostgresTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A thread-safe record of the start hook: exactly-once means each request id appears once.</summary>
    private sealed class Recorder : IApiRunRequestEvents
    {
        public ConcurrentQueue<(Guid Request, Guid Execution)> Started { get; } = new();

        public Task StartedAsync(Guid requestId, Guid apiKeyId, Guid organizationId, Guid projectId, Guid executionId, CancellationToken ct)
        {
            Started.Enqueue((requestId, executionId));
            return Task.CompletedTask;
        }

        public Task RefusedAsync(Guid requestId, Guid apiKeyId, Guid organizationId, Guid projectId, string code, string message, CancellationToken ct)
            => Task.CompletedTask;
    }

    private static readonly OrganizationEntitlementSet Pro = new() { Tier = "pro", MaxOutputHeight = 2160 };

    private readonly Clock _clock = new();
    private readonly Recorder _events = new();
    private readonly Mock<IPublishEndpoint> _publish = new();
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _serviceUser = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _project = Guid.NewGuid();
    private readonly Guid _workflow = Guid.NewGuid();
    private readonly Guid[] _keys = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
    private readonly PublicApiOptions _options = new() { Enabled = true };
    private const int PerKey = 2;

    [PostgresFact]
    public async Task Two_concurrent_dispatchers_start_each_due_request_exactly_once_in_per_key_fifo_order()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_run_dispatcher_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            // The real schema: both contexts migrated, so workflow_executions (engine-owned) exists.
            string connection = PostgresTestEnvironment.ForDatabase(baseConnection, dbName, maxPoolSize: 8);
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseNpgsql(connection).Options;
            await using (InferenceApiDbContext api = new(options))
            {
                await api.Database.MigrateAsync();
            }

            await using (WorkflowEngineDbContext engine = new(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options))
            {
                await engine.Database.MigrateAsync();
            }

            List<(Guid Key, Guid Request)> seeded = await SeedAsync(options);

            // Two separate providers = two dispatchers with their own connections, as two replicas.
            using ServiceProvider first = Provider(options);
            using ServiceProvider second = Provider(options);
            ApiRunQueueDispatcher dispatcherA = Dispatcher(first);
            ApiRunQueueDispatcher dispatcherB = Dispatcher(second);

            int started = 0;
            for (int round = 0; round < 10; round++)
            {
                int[] counts = await Task.WhenAll(
                    dispatcherA.TickAsync(default),
                    dispatcherB.TickAsync(default));
                started += counts.Sum();

                if (await RemainingAsync(options) == 0) break;
            }

            // Exactly once: every start hook fired once, and the dispatchers reported the same number of starts.
            started.Should().Be(seeded.Count, "no request is started twice");
            List<(Guid Request, Guid Execution)> events = [.. _events.Started];
            events.Should().HaveCount(seeded.Count);
            events.Select(e => e.Request).Should().OnlyHaveUniqueItems("each request starts exactly once");
            events.Select(e => e.Request).Should().BeEquivalentTo(seeded.Select(s => s.Request));

            await using InferenceApiDbContext db = new(options);
            List<ApiRunRequest> rows = await db.ApiRunRequests.AsNoTracking().ToListAsync();
            rows.Should().OnlyContain(r => r.Status == ApiRunRequest.Started && r.ExecutionId != null);
            List<WorkflowExecution> executions = await db.WorkflowExecutions.AsNoTracking().ToListAsync();
            executions.Should().HaveCount(seeded.Count, "one execution per request, none duplicated");
            executions.Select(e => e.CorrelationId).Should().OnlyHaveUniqueItems();
            foreach (ApiRunRequest row in rows)
            {
                executions.Should().Contain(e => e.Id == row.ExecutionId
                    && e.CorrelationId == ApiRunRequest.CorrelationFor(row.Id));
            }

            // Per key, the oldest request starts before its younger one.
            foreach (Guid key in _keys)
            {
                Guid[] order = [.. seeded.Where(s => s.Key == key).Select(s => s.Request)];
                int[] positions = [.. order.Select(id => events.FindIndex(e => e.Request == id))];
                positions.Should().BeInAscendingOrder($"key {key} starts its requests FIFO");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private ServiceProvider Provider(DbContextOptions<InferenceApiDbContext> options)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:MaxPaidExecutionsPerHour"] = "100" })
            .Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped(sp => new InferenceApiDbContext(options, sp.GetRequiredService<ITenantContext>()));
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IApiKeyContext, HttpApiKeyContext>();
        services.AddScoped<IProjectAccessService, ProjectAccessService>();
        services.AddSingleton<MembershipValidator>();
        services.AddSingleton(_publish.Object);
        services.AddScoped<IPlanGateService>(sp => new PlanGateService(sp.GetRequiredService<InferenceApiDbContext>(), Fixed(Pro)));
        services.AddScoped<IWorkflowCostEstimator>(sp => new WorkflowCostEstimator(
            sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance));
        services.AddScoped<IWorkflowExecutionService>(sp => new WorkflowExecutionService(
            sp.GetRequiredService<InferenceApiDbContext>(), _publish.Object, NullLogger<WorkflowExecutionService>.Instance,
            sp.GetRequiredService<IPlanGateService>()));
        services.AddScoped<IAssistantExecutionGuard>(sp => new AssistantExecutionGuard(
            sp.GetRequiredService<InferenceApiDbContext>(), config));
        services.AddScoped<PublicRunStartService>();
        services.AddSingleton<IApiRunRequestEvents>(_events);
        return services.BuildServiceProvider();
    }

    private ApiRunQueueDispatcher Dispatcher(ServiceProvider sp)
    {
        _options.RunQueue.StaleStartingMinutes = 5;
        return new ApiRunQueueDispatcher(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(_options),
            NullLogger<ApiRunQueueDispatcher>.Instance, _clock);
    }

    private static IOrganizationEntitlements Fixed(OrganizationEntitlementSet set)
    {
        Mock<IOrganizationEntitlements> m = new();
        m.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(set));
        return m.Object;
    }

    private async Task<int> RemainingAsync(DbContextOptions<InferenceApiDbContext> options)
    {
        await using InferenceApiDbContext db = new(options);
        return await db.ApiRunRequests.CountAsync(r => r.Status == ApiRunRequest.Queued || r.Status == ApiRunRequest.Starting);
    }

    private async Task<List<(Guid Key, Guid Request)>> SeedAsync(DbContextOptions<InferenceApiDbContext> options)
    {
        await using InferenceApiDbContext db = new(options);
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o-" + _org.ToString("N"), Kind = OrganizationKind.Team });
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = _serviceUser,
            Email = $"svc-{_serviceUser:N}@example.com",
            DisplayName = "svc",
            EmailVerifiedAt = DateTime.UtcNow,
            IsServiceAccount = true
        });
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = _owner,
            Email = $"owner-{_owner:N}@example.com",
            DisplayName = "owner",
            EmailVerifiedAt = DateTime.UtcNow
        });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = _org, UserId = _serviceUser, Role = OrganizationRole.Member });
        // api_keys.service_user_id is unique: every key drives its own service account (a real key's shape).
        Guid[] keyServiceUsers = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        for (int i = 0; i < keyServiceUsers.Length; i++)
        {
            db.ApplicationUsers.Add(new ApplicationUser
            {
                Id = keyServiceUsers[i],
                Email = $"svc-{keyServiceUsers[i]:N}@example.com",
                DisplayName = "svc",
                EmailVerifiedAt = DateTime.UtcNow,
                IsServiceAccount = true
            });
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = _org, UserId = keyServiceUsers[i], Role = OrganizationRole.Member });
        }

        for (int i = 0; i < _keys.Length; i++)
        {
            Guid key = _keys[i];
            db.ApiKeys.Add(new ApiKey
            {
                Id = key,
                OrganizationId = _org,
                CreatedByUserId = _serviceUser,
                ServiceUserId = keyServiceUsers[i],
                Name = "k",
                KeyId = key.ToString("N")[..12],
                SecretHash = "h",
                LastFour = "abcd",
                Scopes = ["runs.read", "runs.execute", "outputs.read"]
            });
        }

        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "A",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        db.AgentDefinitions.Add(agent);
        db.Projects.Add(new Project
        {
            Id = _project,
            Name = "p",
            OwnerId = _owner,
            OrganizationId = _org,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = _workflow,
            Name = "w",
            ProjectId = _project,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Steps =
            [
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent.Id, Label = "Write" },
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = agent.Id, Label = "Render" }
            ]
        });
        db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = _org, BalanceMillis = 100_000_000 });

        DateTime due = _clock.Now.UtcDateTime;
        List<(Guid Key, Guid Request)> seeded = [];
        foreach (Guid key in _keys)
        {
            for (int i = 0; i < PerKey; i++)
            {
                ApiRunRequest row = new()
                {
                    Id = Guid.NewGuid(),
                    ApiKeyId = key,
                    OrganizationId = _org,
                    ProjectId = _project,
                    WorkflowId = _workflow,
                    RequestJson = "{\"userRequest\":\"queued\"}",
                    Status = ApiRunRequest.Queued,
                    NotBefore = due.AddSeconds(-120),
                    CreatedAt = due.AddSeconds(-90 + i * 30),
                    UpdatedAt = due.AddSeconds(-90 + i * 30)
                };
                db.ApiRunRequests.Add(row);
                seeded.Add((key, row.Id));
            }
        }

        await db.SaveChangesAsync();
        return seeded;
    }
}
