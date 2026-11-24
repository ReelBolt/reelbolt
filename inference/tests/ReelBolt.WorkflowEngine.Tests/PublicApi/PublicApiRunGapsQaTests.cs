using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.PublicApi;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.PublicApi.Runs;
using ReelBolt.Inference.Api.Services.Webhooks;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.ApiKeys;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>
/// QA pass over the two gaps the executor left open for public-api T-018.
/// <para>
/// Run controls go through <see cref="RunsController"/> - the public surface - and its real
/// <c>ActivatorUtilities</c> construction of the dashboard controller (<c>HttpContext.RequestServices</c>), never the
/// dashboard route directly. Retry/resume ownership is the L19a rule: only the key that started the run may restart
/// it; another key of the same organization gets <c>403 run_not_owned_by_api_key</c>, and a run a person started is
/// refused the same code.
/// </para>
/// <para>
/// The webhook consumer's terminal outcome is derived from the execution row when the message cannot distinguish a
/// user stop from a failure: a cancelled row maps to <c>run.cancelled</c>, a real failure to <c>run.failed</c> with
/// its step's failure code, and a redelivered message makes no second delivery.
/// </para>
/// </summary>
public sealed class PublicApiRunGapsQaTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly OrganizationEntitlementSet Pro = new() { Tier = "pro", MaxOutputHeight = 2160 };

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Clock _clock = new();
    private readonly Mock<IPublishEndpoint> _publish = new();
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _service = Guid.NewGuid();
    private readonly Guid _serviceB = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _keyA = Guid.NewGuid();
    private readonly Guid _keyB = Guid.NewGuid();
    private readonly Guid _project = Guid.NewGuid();
    private readonly Guid _otherProject = Guid.NewGuid();
    private readonly Guid _workflow = Guid.NewGuid();
    private readonly PublicApiOptions _options = new() { Enabled = true };

    public PublicApiRunGapsQaTests() => Seed();

    public void Dispose() => _cleanup?.Invoke();

    private string? _pg;
    private Action? _cleanup;

    private DbContextOptions<InferenceApiDbContext> DbOptions() => _pg is not null
        ? new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(_pg).Options
        : new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private InferenceApiDbContext NewDb() => new(DbOptions());

    private void Seed()
    {
        using InferenceApiDbContext db = NewDb();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = _service,
            Email = "svc@example.com",
            DisplayName = "svc",
            EmailVerifiedAt = DateTime.UtcNow,
            IsServiceAccount = true
        });
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = _serviceB,
            Email = "svcb@example.com",
            DisplayName = "svcb",
            EmailVerifiedAt = DateTime.UtcNow,
            IsServiceAccount = true
        });
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = _owner,
            Email = "owner@example.com",
            DisplayName = "owner",
            EmailVerifiedAt = DateTime.UtcNow
        });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = _org, UserId = _service, Role = OrganizationRole.Member });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = _org, UserId = _serviceB, Role = OrganizationRole.Member });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = _org, UserId = _owner, Role = OrganizationRole.Owner });
        db.ApiKeys.Add(NewKey(_keyA, _service));
        db.ApiKeys.Add(NewKey(_keyB, _serviceB));
        foreach (Guid projectId in new[] { _project, _otherProject })
        {
            db.Projects.Add(new Project
            {
                Id = projectId,
                Name = "p",
                OwnerId = _owner,
                OrganizationId = _org,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
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
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = _workflow,
            Name = "w",
            ProjectId = _project,
            Kind = WorkflowDefinitionKind.Standard,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Steps =
            [
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent.Id, Label = "Write" },
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = agent.Id, Label = "Render" }
            ]
        });
        db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = _org, BalanceMillis = 100_000_000 });
        db.SaveChanges();
    }

    private ApiKey NewKey(Guid id, Guid serviceUser) => new()
    {
        Id = id,
        OrganizationId = _org,
        CreatedByUserId = _owner,
        ServiceUserId = serviceUser,
        Name = "k",
        KeyId = id.ToString("N")[..12],
        SecretHash = "h",
        LastFour = "abcd",
        Scopes = ["runs.read", "runs.execute", "outputs.read"]
    };

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:MaxPaidExecutionsPerHour"] = "100" }).Build();

    private static IOrganizationEntitlements Fixed(OrganizationEntitlementSet set)
    {
        Mock<IOrganizationEntitlements> m = new();
        m.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(set));
        return m.Object;
    }

    /// <summary>
    /// The public controller and, on its request services, the dashboard controller it builds with
    /// <c>ActivatorUtilities</c> exactly as the API does.
    /// </summary>
    private RunsController NewPublicController(InferenceApiDbContext db, Guid callerKey)
    {
        ApiKeyTestSupport.FakeUser user = new(_org, OrganizationRole.Member, _service);
        ApiKeyTestSupport.FakeKeyContext keyContext = new(callerKey);
        PlanGateService gate = new(db, Fixed(Pro));
        WorkflowCostEstimator estimator = new(
            new SingleContextScopes(db),
            Options.Create(new BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);
        WorkflowExecutionService execution = new(db, _publish.Object, NullLogger<WorkflowExecutionService>.Instance, gate);
        PublicRunStartService start = new(
            db, user, new ProjectAccessService(db, user, keyContext), execution, new AssistantExecutionGuard(db, Config()),
            gate, estimator);

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddSingleton<ICurrentUser>(user);
        services.AddSingleton<IProjectAccessService>(new ProjectAccessService(db, user, keyContext));
        services.AddSingleton(_publish.Object);
        services.AddSingleton(new WorkflowTemplateProvisioningService(
            db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()));
        services.AddSingleton<IWorkflowExecutionService>(execution);
        services.AddSingleton<IPlanGateService>(gate);
        services.AddSingleton<IWorkflowCostEstimator>(estimator);

        RunsController controller = new(
            db, user, new ProjectAccessService(db, user, keyContext), keyContext, start,
            new AssistantExecutionGuard(db, Config()), Options.Create(_options), _clock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.ControllerContext.HttpContext.RequestServices = services.BuildServiceProvider();
        return controller;
    }

    private sealed class SingleContextScopes(InferenceApiDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedScope(db);

        private sealed class FixedScope(InferenceApiDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FixedProvider(db);

            public void Dispose()
            {
            }
        }

        private sealed class FixedProvider(InferenceApiDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? db : null;
        }
    }

    private Guid SeedRun(
        ExecutionStatus status, Guid? projectId = null, Guid? initiator = null, Guid? apiKey = null,
        DateTime? completedAt = null)
    {
        Guid id = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = id,
            WorkflowDefinitionId = _workflow,
            ProjectId = projectId ?? _project,
            OrganizationId = _org,
            Status = status,
            StartedAt = DateTime.UtcNow,
            CompletedAt = status is ExecutionStatus.Passed or ExecutionStatus.Failed or ExecutionStatus.Cancelled
                ? completedAt ?? DateTime.UtcNow
                : null,
            InitiatedByUserId = initiator ?? _service,
            ApiKeyId = apiKey
        });
        db.SaveChanges();
        return id;
    }

    // ------------------------------------------------------------------ public stop / retry / resume through the public controller

    [Fact]
    public async Task Public_stop_of_a_running_run_returns_202_and_publishes_the_engine_stop()
    {
        Guid run = SeedRun(ExecutionStatus.Running);

        using InferenceApiDbContext db = NewDb();
        IActionResult result = await NewPublicController(db, _keyB).Stop(_project, run, default);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(202);
        _publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionStopRequested>(m => m.ExecutionId == run),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Public_retry_of_a_run_started_by_a_different_key_of_the_same_org_is_403_and_the_starting_key_retries_and_pays()
    {
        Guid run = SeedRun(ExecutionStatus.Failed, initiator: _service, apiKey: _keyA);

        using (InferenceApiDbContext db = NewDb())
        {
            IActionResult refused = await NewPublicController(db, _keyB).Retry(_project, run, null, default);
            ((ObjectResult)refused).StatusCode.Should().Be(403,
                "L19a: another key of the same organization may not retry a run it did not start");
            ErrorCode(refused).Should().Be("run_not_owned_by_api_key");
        }

        using (InferenceApiDbContext read = NewDb())
        {
            WorkflowExecution untouched = read.WorkflowExecutions.Single(e => e.Id == run);
            untouched.Status.Should().Be(ExecutionStatus.Failed);
            untouched.ApiKeyId.Should().Be(_keyA, "a refused retry changes nothing");
        }

        _publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Never);

        using (InferenceApiDbContext db = NewDb())
        {
            IActionResult result = await NewPublicController(db, _keyA).Retry(_project, run, null, default);
            result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(200);
        }

        using (InferenceApiDbContext read = NewDb())
        {
            WorkflowExecution row = read.WorkflowExecutions.Single(e => e.Id == run);
            row.Status.Should().Be(ExecutionStatus.Queued);
            row.ApiKeyId.Should().Be(_keyA, "the key that started the run retries and pays");
            row.PendingResume.Should().BeFalse();
        }

        _publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Public_retry_and_resume_of_a_run_a_dashboard_user_started_are_403_run_not_owned_by_api_key()
    {
        Guid byPerson = SeedRun(ExecutionStatus.Failed, initiator: _owner);

        using InferenceApiDbContext db = NewDb();
        RunsController controller = NewPublicController(db, _keyB);

        IActionResult retry = await controller.Retry(_project, byPerson, null, default);
        ((ObjectResult)retry).StatusCode.Should().Be(403);
        ErrorCode(retry).Should().Be("run_not_owned_by_api_key");

        IActionResult resume = await controller.Resume(_project, byPerson, null, default);
        ((ObjectResult)resume).StatusCode.Should().Be(403);
        ErrorCode(resume).Should().Be("run_not_owned_by_api_key");
    }

    [Fact]
    public async Task Public_stop_of_a_run_a_dashboard_user_started_is_not_an_ownership_refusal()
    {
        Guid byPerson = SeedRun(ExecutionStatus.Running, initiator: _owner);

        using InferenceApiDbContext db = NewDb();
        IActionResult result = await NewPublicController(db, _keyB).Stop(_project, byPerson, default);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(202,
            "stop does not elevate: the engine acts on the row's own initiator");
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("resume")]
    [InlineData("retry")]
    public async Task Each_public_run_action_answers_404_for_a_run_of_another_project(string action)
    {
        Guid otherProjectRun = SeedRun(ExecutionStatus.Failed, projectId: _otherProject);

        using InferenceApiDbContext db = NewDb();
        RunsController controller = NewPublicController(db, _keyB);
        IActionResult result = action switch
        {
            "stop" => await controller.Stop(_project, otherProjectRun, default),
            "resume" => await controller.Resume(_project, otherProjectRun, null, default),
            _ => await controller.Retry(_project, otherProjectRun, null, default)
        };

        ((ObjectResult)result).StatusCode.Should().Be(404,
            "a run named under a project it does not belong to is simply not there");
    }

    [Theory]
    [InlineData(nameof(RunsController.Stop))]
    [InlineData(nameof(RunsController.Resume))]
    [InlineData(nameof(RunsController.Retry))]
    public async Task A_key_missing_runs_execute_is_403_insufficient_scope_on_the_public_run_action(string action)
    {
        MethodInfo method = typeof(RunsController).GetMethod(action)!;
        object[] metadata = [.. method.GetCustomAttributes<RequireScopeAttribute>()];
        metadata.Should().NotBeEmpty($"{action} is a run control and must declare its scope");

        (bool reached, int status, JsonNode body) = await RunPublicScopeGuard(metadata, "runs.read");

        reached.Should().BeFalse();
        status.Should().Be(403);
        body["error"]!["code"]!.GetValue<string>().Should().Be("insufficient_scope");
        body["error"]!["requiredScopes"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("runs.execute");
    }

    // ------------------------------------------------------------------ the Postgres-only resume happy path

    [PostgresFact]
    public async Task Public_resume_of_a_run_started_by_a_different_key_of_the_same_org_is_403_and_the_starting_key_resumes_and_pays()
    {
        await UsePostgresAsync();
        Guid run;
        using (InferenceApiDbContext db = NewDb())
            run = await SeedResumableRunAsync(db, initiator: _service, apiKey: _keyA);

        using (InferenceApiDbContext db = NewDb())
        {
            IActionResult refused = await NewPublicController(db, _keyB).Resume(_project, run, null, default);
            ((ObjectResult)refused).StatusCode.Should().Be(403,
                "L19a: only the key that started the run may resume it");
            ErrorCode(refused).Should().Be("run_not_owned_by_api_key");
        }

        using (InferenceApiDbContext read = NewDb())
        {
            WorkflowExecution untouched = read.WorkflowExecutions.Single(e => e.Id == run);
            untouched.Status.Should().Be(ExecutionStatus.Failed);
            untouched.ApiKeyId.Should().Be(_keyA, "a refused resume changes nothing");
        }

        _publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Never);

        using (InferenceApiDbContext db = NewDb())
        {
            IActionResult result = await NewPublicController(db, _keyA).Resume(_project, run, null, default);
            result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(202);
        }

        using (InferenceApiDbContext read = NewDb())
        {
            WorkflowExecution row = read.WorkflowExecutions.Single(e => e.Id == run);
            row.Status.Should().Be(ExecutionStatus.Queued);
            row.PendingResume.Should().BeTrue("a resume continues from the frontier, not from step 1");
            row.ApiKeyId.Should().Be(_keyA, "the key that started the run resumes and pays");
            row.ResumeCount.Should().Be(1);
        }

        _publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A failed run with an engine-taken snapshot: the only shape Resume accepts. The owner column lives on the
    /// engine model, so it is written with SQL after both models' create scripts have run.
    /// </summary>
    private async Task<Guid> SeedResumableRunAsync(InferenceApiDbContext db, Guid initiator, Guid apiKey)
    {
        Guid runId = Guid.NewGuid();
        Guid snapshotId = Guid.NewGuid();
        Guid agent = await db.AgentDefinitions.Select(a => a.Id).FirstAsync();
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = snapshotId,
            Name = "snap",
            ProjectId = _project,
            Kind = WorkflowDefinitionKind.ExecutionSnapshot,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Steps =
            [
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent, Label = "Write" },
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = agent, Label = "Render" }
            ]
        });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = runId,
            WorkflowDefinitionId = _workflow,
            ProjectId = _project,
            OrganizationId = _org,
            Status = ExecutionStatus.Failed,
            QueuedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            InitiatedByUserId = initiator,
            ApiKeyId = apiKey,
            SnapshotDefinitionId = snapshotId,
            AgentSnapshotJson = "{}"
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE workflow_definitions SET snapshot_of_execution_id = {0} WHERE id = {1}", runId, snapshotId);
        return runId;
    }

    /// <summary>
    /// A throwaway Postgres database with both models' schemas (as in <see cref="PublicRunsTests"/>), plus the
    /// engine-owned snapshot owner column, which the API-first create script would otherwise omit.
    /// </summary>
    private async Task UsePostgresAsync()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_run_gaps_qa_" + Guid.NewGuid().ToString("N")[..12];
        NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
            await create.ExecuteNonQueryAsync();
        _cleanup = () =>
        {
            NpgsqlConnection.ClearAllPools();
            using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            drop.ExecuteNonQuery();
            admin.Dispose();
        };
        string connection = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);

        await using InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options);
        await using WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options);
        for (int pass = 0; pass < 3; pass++)
        {
            await RunScriptAsync(api, api.Database.GenerateCreateScript());
            await RunScriptAsync(engine, engine.Database.GenerateCreateScript());
        }

        await api.Database.ExecuteSqlRawAsync(
            "ALTER TABLE workflow_definitions ADD COLUMN IF NOT EXISTS snapshot_of_execution_id uuid NULL");

        _pg = connection;
        Seed();
    }

    private static async Task RunScriptAsync(DbContext context, string script)
    {
        foreach (string statement in script.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (statement.All(c => c is '-' or '\n' or ' ' or '\r')) continue;
            try
            {
                await context.Database.ExecuteSqlRawAsync(statement.Replace("{", "{{").Replace("}", "}}"));
            }
            catch (Exception ex) when ((ex as PostgresException ?? ex.InnerException as PostgresException) is { SqlState: "42P01" or "42P07" or "42710" or "42701" or "42P16" })
            {
                // not creatable yet, or already made
            }
        }
    }

    // ------------------------------------------------------------------ webhook terminal outcome

    private static string ErrorCode(IActionResult result) =>
        ((JsonObject)PublicApiResults.From((ObjectResult)result).Value!)["error"]!["code"]!.GetValue<string>();

    private static async Task<(bool Reached, int Status, JsonNode Body)> RunPublicScopeGuard(
        object[] metadata, params string[] scopes)
    {
        PublicApiOptions options = new() { Enabled = true };
        ClaimsPrincipal user = new(new ClaimsIdentity(
        [
            new Claim("sub", "u"),
            new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString()),
            new Claim(AuthClaims.Scope, string.Join(' ', scopes))
        ], "ApiKey"));
        DefaultHttpContext http = new() { User = user };
        http.Request.Method = "POST";
        http.Response.Body = new MemoryStream();
        ResourceExecutingContext context = new(
            new ActionContext(http, new RouteData(), new ActionDescriptor { EndpointMetadata = metadata }), [], []);
        PublicApiGuardFilter filter = new(
            new Monitor(options),
            new ApiKeyTestSupport.FakeKeyContext(Guid.NewGuid()),
            new ApiKeyRateLimiter(new NullRateStore(), new Monitor(options), NullLogger<ApiKeyRateLimiter>.Instance),
            new NoAudit());

        bool reached = false;
        await filter.OnResourceExecutionAsync(context, () =>
        {
            reached = true;
            return Task.FromResult(new ResourceExecutedContext(context, []));
        });
        http.Response.Body.Position = 0;
        string body = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (reached, http.Response.StatusCode, JsonNode.Parse(body)!);
    }

    private sealed class Monitor(PublicApiOptions value) : IOptionsMonitor<PublicApiOptions>
    {
        public PublicApiOptions CurrentValue => value;
        public PublicApiOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PublicApiOptions, string?> listener) => null;
    }

    private sealed class NullRateStore : IApiKeyRateCounterStore
    {
        public Task<(int Total, int Writes)> IncrementAsync(Guid key, DateTime window, bool isWrite, CancellationToken ct) =>
            Task.FromResult((0, 0));

        public Task<int> DeleteOlderThanAsync(DateTime window, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class NoAudit : IApiKeyAuditWriter
    {
        public void TryWrite(ApiKeyAuditEntry entry)
        {
        }
    }

    private Guid SeedEndpoint(string type)
    {
        Guid id = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        db.WebhookEndpoints.Add(new WebhookEndpoint
        {
            Id = id,
            OrganizationId = _org,
            Url = "https://hooks.example.com/reelbolt",
            Events = [type],
            Enabled = true,
            SecretEncrypted = "enc",
            CreatedByUserId = _owner,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
        return id;
    }

    private WebhookRunEventsConsumer NewConsumer(InferenceApiDbContext db) =>
        new(db, new WebhookEventWriter(db, null, _clock), Options.Create(_options));

    private static ConsumeContext<T> Context<T>(T message) where T : class
    {
        Mock<ConsumeContext<T>> ctx = new();
        ctx.SetupGet(c => c.Message).Returns(message);
        ctx.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return ctx.Object;
    }

    private List<WebhookDelivery> Deliveries()
    {
        using InferenceApiDbContext db = NewDb();
        return db.WebhookDeliveries.OrderBy(d => d.CreatedAt).ToList();
    }

    [Fact]
    public async Task WorkflowExecutionFailed_for_a_user_stopped_run_makes_run_cancelled_once_even_when_redelivered()
    {
        SeedEndpoint(WebhookEventTypes.RunCancelled);
        DateTime stoppedAt = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        Guid run = SeedRun(ExecutionStatus.Cancelled, completedAt: stoppedAt);

        WorkflowExecutionFailed message = new()
        {
            ExecutionId = run,
            ErrorMessage = "Stopped by the user.",
            FailedAt = stoppedAt.AddMinutes(1)
        };
        using (InferenceApiDbContext db = NewDb()) await NewConsumer(db).Consume(Context(message));
        using (InferenceApiDbContext db = NewDb()) await NewConsumer(db).Consume(Context(message));

        WebhookDelivery delivery = Deliveries().Should().ContainSingle().Subject;
        delivery.EventType.Should().Be(WebhookEventTypes.RunCancelled);
        JsonNode payload = JsonNode.Parse(delivery.PayloadJson)!;
        payload["type"]!.GetValue<string>().Should().Be("run.cancelled");
        payload["data"]!["status"]!.GetValue<string>().Should().Be("cancelled");
        payload["data"]!["failureCode"].Should().BeNull("a stop is not a failure");
    }

    [Fact]
    public async Task WorkflowExecutionFailed_for_a_real_failure_makes_run_failed_with_the_failure_code_once_even_when_redelivered()
    {
        SeedEndpoint(WebhookEventTypes.RunFailed);
        DateTime failedAt = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        Guid run = SeedRun(ExecutionStatus.Failed, completedAt: failedAt.AddMinutes(-1));
        using (InferenceApiDbContext db = NewDb())
        {
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = run,
                WorkflowStepId = Guid.NewGuid(),
                FailureCode = "RENDER_FAILED",
                CreditsChargedMillis = 2500,
                CompletedAt = failedAt
            });
            db.SaveChanges();
        }

        WorkflowExecutionFailed message = new()
        {
            ExecutionId = run,
            ErrorMessage = "boom",
            FailedAt = failedAt
        };
        using (InferenceApiDbContext db = NewDb()) await NewConsumer(db).Consume(Context(message));
        using (InferenceApiDbContext db = NewDb()) await NewConsumer(db).Consume(Context(message));

        WebhookDelivery delivery = Deliveries().Should().ContainSingle().Subject;
        delivery.EventType.Should().Be(WebhookEventTypes.RunFailed);
        JsonNode payload = JsonNode.Parse(delivery.PayloadJson)!;
        payload["type"]!.GetValue<string>().Should().Be("run.failed");
        payload["data"]!["status"]!.GetValue<string>().Should().Be("failed");
        payload["data"]!["failureCode"]!.GetValue<string>().Should().Be("RENDER_FAILED");
    }
}
