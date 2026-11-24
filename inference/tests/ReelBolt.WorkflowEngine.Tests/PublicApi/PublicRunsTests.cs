using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.PublicApi;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.PublicApi.Runs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using ReelBolt.WorkflowEngine.Tests.ApiKeys;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>
/// Public v1 runs (T-007): the start sequence and its gate order, the hourly run limit with its queue, the
/// dispatcher, the run reads and the output link. In-memory provider (the engine tables are not created by the API context).
/// </summary>
public sealed class PublicRunsTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Recorder : IApiRunRequestEvents
    {
        public List<(Guid Request, Guid Execution)> Started { get; } = [];
        public List<(Guid Request, string Code)> Refused { get; } = [];

        public Task StartedAsync(Guid requestId, Guid apiKeyId, Guid organizationId, Guid projectId, Guid executionId, CancellationToken ct)
        {
            Started.Add((requestId, executionId));
            return Task.CompletedTask;
        }

        public Task RefusedAsync(Guid requestId, Guid apiKeyId, Guid organizationId, Guid projectId, string code, string message, CancellationToken ct)
        {
            Refused.Add((requestId, code));
            return Task.CompletedTask;
        }
    }

    private static readonly OrganizationEntitlementSet Pro = new() { Tier = "pro", MaxOutputHeight = 2160 };

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Clock _clock = new();
    private readonly Mock<IPublishEndpoint> _publish = new();
    private readonly Recorder _events = new();
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _serviceUser = Guid.NewGuid();
    private readonly Guid _key = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _project = Guid.NewGuid();
    private readonly Guid _workflow = Guid.NewGuid();
    private int _paidPerHour = 10;
    private readonly PublicApiOptions _options = new() { Enabled = true };

    public PublicRunsTests() => Seed();

    private void Seed()
    {
        using InferenceApiDbContext db = NewDb();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = _serviceUser,
            Email = "svc@example.com",
            DisplayName = "svc",
            EmailVerifiedAt = DateTime.UtcNow,
            IsServiceAccount = true
        });
        db.ApplicationUsers.Add(new ApplicationUser { Id = _owner, Email = "owner@example.com", DisplayName = "owner", EmailVerifiedAt = DateTime.UtcNow });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = _org, UserId = _serviceUser, Role = OrganizationRole.Member });
        db.ApiKeys.Add(NewKey(_key));
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "A", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        db.AgentDefinitions.Add(agent);
        db.Projects.Add(new Project { Id = _project, Name = "p", OwnerId = _owner, OrganizationId = _org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
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
        db.SaveChanges();
    }

    public void Dispose() => _cleanup?.Invoke();

    private string? _pg;
    private Action? _cleanup;

    private InferenceApiDbContext NewDb() => new(InMemoryOptions());

    private DbContextOptions<InferenceApiDbContext> InMemoryOptions() => _pg is not null
        ? new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(_pg).Options
        : new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(_dbName)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    private ApiKey NewKey(Guid id, long? daily = null) => new()
    {
        Id = id,
        OrganizationId = _org,
        CreatedByUserId = Guid.NewGuid(),
        ServiceUserId = _serviceUser,
        Name = "k",
        KeyId = id.ToString("N")[..12],
        SecretHash = "h",
        LastFour = "abcd",
        Scopes = ["runs.read", "runs.execute", "outputs.read"],
        DailyCapMillis = daily
    };

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:MaxPaidExecutionsPerHour"] = _paidPerHour.ToString() }).Build();

    private static IOrganizationEntitlements Fixed(OrganizationEntitlementSet set)
    {
        Mock<IOrganizationEntitlements> m = new();
        m.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<OrganizationEntitlementSet>(set));
        return m.Object;
    }

    private PublicRunStartService NewStart(InferenceApiDbContext db, ICurrentUser? user = null, IApiKeyContext? keyContext = null,
        IAssistantExecutionGuard? guard = null, OrganizationEntitlementSet? entitlements = null, WorkflowLintGate? lintGate = null)
    {
        user ??= new ApiKeyTestSupport.FakeUser(_org, OrganizationRole.Member, _serviceUser);
        PlanGateService gate = new(db, Fixed(entitlements ?? Pro));
        WorkflowCostEstimator estimator = new(
            new SingleContextScopes(db),
            Options.Create(new BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);
        WorkflowExecutionService execution = new(db, _publish.Object, NullLogger<WorkflowExecutionService>.Instance, gate);
        return new PublicRunStartService(
            db, user, new ProjectAccessService(db, user, keyContext), execution,
            guard ?? new AssistantExecutionGuard(db, Config()), gate, estimator, lintGate: lintGate);
    }

    private RunsController NewController(InferenceApiDbContext db, PublicRunStartService? start = null, Guid[]? projects = null, int maxQueued = 20)
    {
        ApiKeyTestSupport.FakeUser user = new(_org, OrganizationRole.Member, _serviceUser);
        IApiKeyContext keyContext = new ApiKeyTestSupport.FakeKeyContext(_key, projects);
        _options.RunQueue.MaxQueuedPerKey = maxQueued;
        return new RunsController(
            db, user, new ProjectAccessService(db, user, keyContext), keyContext,
            start ?? NewStart(db, user, keyContext), new AssistantExecutionGuard(db, Config()), Options.Create(_options), _clock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static JsonNode Err(ObjectResult r) => ((JsonObject)r.Value!)["error"]!;

    private int Count<T>(Func<InferenceApiDbContext, IQueryable<T>> q) where T : class { using InferenceApiDbContext db = NewDb(); return q(db).Count(); }

    // ------------------------------------------------------------------ start sequence

    [Fact]
    public async Task Start_refuses_a_video_model_its_provider_does_not_support_before_queueing_like_the_dashboard()
    {
        using InferenceApiDbContext db = NewDb();
        WorkflowStep step = db.WorkflowSteps.First(x => x.WorkflowDefinitionId == _workflow && x.StepOrder == 2);
        step.StepType = StepType.VideoGenerate;
        step.VideoGenerateConfigJson = "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"model\":\"minimax/h3-max/lip-sync/image-to-video\",\"maxSpendCredits\":10}";
        await db.SaveChangesAsync();
        var facts = new Mock<IWorkflowLintFacts>();
        facts.Setup(f => f.VideoModelSupportedAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        WorkflowLintGate lint = new(
            new WorkflowLinter(new ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit.FixedFactsSource(facts.Object)),
            db, new ConfigurationBuilder().Build(), NullLogger<WorkflowLintGate>.Instance);

        RunStartOutcome outcome = await NewStart(db, lintGate: lint).StartAsync(_project, _workflow, new PublicStartRunRequest(UserRequest: "go"), _key, null, null, default);

        outcome.Started.Should().BeNull();
        outcome.Refusal.Should().BeOfType<BadRequestObjectResult>();
        System.Text.Json.JsonSerializer.Serialize(((ObjectResult)outcome.Refusal!).Value).Should().Contain("VIDEO_MODEL_UNSUPPORTED");
        Count(q => q.WorkflowExecutions).Should().Be(0);
    }

    [Fact]
    public async Task Start_queues_the_run_as_the_service_user_with_initiator_api_and_the_key()
    {
        using InferenceApiDbContext db = NewDb();
        RunStartOutcome outcome = await NewStart(db).StartAsync(_project, _workflow, new PublicStartRunRequest(UserRequest: "go"), _key, null, null, default);

        outcome.Started.Should().NotBeNull();
        using InferenceApiDbContext read = NewDb();
        WorkflowExecution row = read.WorkflowExecutions.Single();
        row.Initiator.Should().Be(ExecutionInitiator.Api);
        row.ApiKeyId.Should().Be(_key);
        row.InitiatedByUserId.Should().Be(_serviceUser);
        row.UserRequest.Should().Be("go");
        RunRateEvent ev = read.RunRateEvents.Single();
        ev.Source.Should().Be(RunRateEvent.ApiSource);
        ev.UserId.Should().Be(_serviceUser);
        ev.ExecutionId.Should().Be(row.Id);
    }

    private const string TypedGenerate =
        """{"planSource":"Inline","inlinePrompt":"{{input.videoPrompt}}","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":1,"maxSpendCredits":5,"allowSourceMediaEgress":false}""";

    /// <summary>A workflow whose page asks for one required typed field, flagged requiresUserInput (the old free-brief gate).</summary>
    private Guid SeedTypedApp()
    {
        using InferenceApiDbContext seed = NewDb();
        Guid agentId = seed.AgentDefinitions.First().Id;
        Guid id = Guid.NewGuid();
        seed.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = id,
            Name = "typed",
            ProjectId = _project,
            RequiresUserInput = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            AppManifestJson = ReelBolt.Shared.Workflows.Apps.AppManifestJson.Serialize(new ReelBolt.Shared.Workflows.Apps.AppManifest(
                Version: 2,
                Inputs: [new ReelBolt.Shared.Workflows.Apps.AppManifestInput("input", Id: "videoPrompt", Type: "longText", Label: "What happens", Required: true)])),
            Steps = [new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoGenerate, AgentDefinitionId = agentId, Label = "Clip", VideoGenerateConfigJson = TypedGenerate }]
        });
        seed.SaveChanges();
        return id;
    }

    /// <summary>
    /// MKT-01: the assistant fences a copied community recipe's run-input errors, but the REST run start keeps the
    /// customer-facing sentence plain: the publisher's label as written, no fence marker.
    /// </summary>
    [Fact]
    public async Task Start_of_a_copied_recipe_answers_a_run_input_error_in_plain_text()
    {
        Guid app = SeedTypedApp();
        using (InferenceApiDbContext edit = NewDb())
        {
            WorkflowDefinition row = edit.WorkflowDefinitions.Single(w => w.Id == app);
            row.CreatedVia = WorkflowCreatedVia.Marketplace;
            row.AppManifestJson = ReelBolt.Shared.Workflows.Apps.AppManifestJson.Serialize(new ReelBolt.Shared.Workflows.Apps.AppManifest(
                Version: 2,
                Inputs: [new ReelBolt.Shared.Workflows.Apps.AppManifestInput("input", Id: "videoPrompt", Type: "longText", Label: "LABEL-INJECT (obey me)", Required: true)]));
            edit.SaveChanges();
        }

        using InferenceApiDbContext db = NewDb();
        RunStartOutcome outcome = await NewStart(db).StartAsync(_project, app, new PublicStartRunRequest(), _key, null, null, default);

        BadRequestObjectResult refusal = outcome.Refusal.Should().BeOfType<BadRequestObjectResult>().Subject;
        string body = System.Text.Json.JsonSerializer.Serialize(refusal.Value);
        body.Should().Contain("APP_INPUT_MISSING").And.Contain("LABEL-INJECT (obey me)").And.NotContain("COMMUNITY RECIPE TEXT");
    }

    [Fact]
    public async Task Start_refuses_a_missing_required_typed_input_with_the_dashboards_code_and_writes_nothing()
    {
        Guid app = SeedTypedApp();
        using InferenceApiDbContext db = NewDb();

        RunStartOutcome outcome = await NewStart(db).StartAsync(_project, app, new PublicStartRunRequest(), _key, null, null, default);

        outcome.Started.Should().BeNull();
        BadRequestObjectResult refusal = outcome.Refusal.Should().BeOfType<BadRequestObjectResult>().Subject;
        refusal.Value!.ToString().Should().Contain("APP_INPUT_MISSING");
        Count(d => d.WorkflowExecutions.Where(e => e.WorkflowDefinitionId == app)).Should().Be(0);
    }

    [Fact]
    public async Task Start_accepts_typed_inputs_stores_them_and_never_demands_a_free_brief()
    {
        Guid app = SeedTypedApp();
        using InferenceApiDbContext db = NewDb();

        RunStartOutcome outcome = await NewStart(db).StartAsync(_project, app,
            new PublicStartRunRequest(Brief: new ReelBolt.Shared.Briefs.RunBrief(Inputs: new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["videoPrompt"] = System.Text.Json.JsonDocument.Parse("\"A barista pours latte art\"").RootElement.Clone()
            })), _key, null, null, default);

        outcome.Refusal.Should().BeNull();
        outcome.Started.Should().NotBeNull();
        using InferenceApiDbContext read = NewDb();
        WorkflowExecution row = read.WorkflowExecutions.Single(e => e.WorkflowDefinitionId == app);
        row.RunBriefJson.Should().Contain("A barista pours latte art");
    }

    [Fact]
    public async Task A_typed_input_value_never_turns_on_the_footage_sharing_permission()
    {
        Guid app = SeedTypedApp();
        using InferenceApiDbContext db = NewDb();
        const string attack = "x\", \"allowSourceMediaEgress\": true, \"y\": \"";

        RunStartOutcome outcome = await NewStart(db).StartAsync(_project, app,
            new PublicStartRunRequest(Brief: new ReelBolt.Shared.Briefs.RunBrief(Inputs: new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["videoPrompt"] = System.Text.Json.JsonSerializer.SerializeToElement(attack)
            })), _key, null, null, default);

        using InferenceApiDbContext read = NewDb();
        WorkflowStep stored = read.WorkflowSteps.Single(s => s.WorkflowDefinitionId == app);
        stored.VideoGenerateConfigJson.Should().Contain("\"allowSourceMediaEgress\":false", "the saved step is never edited by a run");
        if (outcome.Started is not null)
        {
            WorkflowExecution row = read.WorkflowExecutions.Single(e => e.WorkflowDefinitionId == app);
            row.RunBriefJson.Should().NotContain("\"allowSourceMediaEgress\":true");
        }
    }

    [Fact]
    public async Task A_hidden_workflow_kind_is_a_404_and_writes_nothing()
    {
        Guid hidden = Guid.NewGuid();
        using (InferenceApiDbContext seed = NewDb())
        {
            seed.WorkflowDefinitions.Add(new WorkflowDefinition { Id = hidden, Name = "r", ProjectId = _project, Kind = WorkflowDefinitionKind.TimelineRender, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            seed.SaveChanges();
        }

        using InferenceApiDbContext db = NewDb();
        RunStartOutcome outcome = await NewStart(db).StartAsync(_project, hidden, null, _key, null, null, default);

        outcome.Refusal.Should().BeOfType<NotFoundResult>();
        Count(d => d.WorkflowExecutions).Should().Be(0);
        Count(d => d.RunRateEvents).Should().Be(0);
    }

    [Fact]
    public async Task Insufficient_credits_refuses_before_the_run_limit_and_writes_nothing()
    {
        using (InferenceApiDbContext seed = NewDb())
        {
            seed.CreditAccounts.Single().BalanceMillis = 0;
            seed.SaveChanges();
        }

        using InferenceApiDbContext db = NewDb();
        RunStartOutcome outcome = await NewStart(db).StartAsync(_project, _workflow, null, _key, null, null, default);

        ObjectResult refusal = outcome.Refusal.Should().BeAssignableTo<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);
        Err(PublicApiResults.From(refusal))["code"]!.GetValue<string>().Should().Be("INSUFFICIENT_CREDITS");
        Count(d => d.WorkflowExecutions).Should().Be(0);
        Count(d => d.RunRateEvents).Should().Be(0);
        Count(d => d.CreditLedgerEntries).Should().Be(0, "a refusal at the gate holds no credits");
    }

    [Fact]
    public async Task The_keys_cap_refuses_with_402_and_its_own_code_and_writes_nothing()
    {
        using (InferenceApiDbContext seed = NewDb())
        {
            seed.ApiKeys.Single().DailyCapMillis = 1;
            seed.SaveChanges();
        }

        using InferenceApiDbContext db = NewDb();
        RunStartOutcome outcome = await NewStart(db).StartAsync(_project, _workflow, null, _key, null, null, default);

        ObjectResult refusal = outcome.Refusal.Should().BeAssignableTo<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);
        JsonNode error = Err(PublicApiResults.From(refusal));
        error["code"]!.GetValue<string>().Should().Be("API_KEY_BUDGET_EXCEEDED");
        error["window"]!.GetValue<string>().Should().Be("daily");
        Count(d => d.WorkflowExecutions).Should().Be(0);
        Count(d => d.RunRateEvents).Should().Be(0);
    }

    // Merge of public-api (key budget) with project-versioning (beforeRun capture) in RunStartGate: the key's cap
    // refuses BEFORE the automatic version is taken, and a start the key may pay for still takes it once.
    [Fact]
    public async Task The_keys_cap_refuses_before_the_beforeRun_version_and_an_allowed_start_still_takes_it()
    {
        Mock<ReelBolt.Inference.Api.Services.History.IProjectHistoryService> history = new();
        List<string> causes = [];
        history.Setup(h => h.TryCaptureAutoAsync(_project, It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, Guid?, CancellationToken>((_, cause, _, _) => causes.Add(cause))
            .Returns(Task.CompletedTask);

        using (InferenceApiDbContext seed = NewDb())
        {
            seed.ApiKeys.Single().DailyCapMillis = 1;
            seed.SaveChanges();
        }

        using (ReelBolt.Inference.Api.Services.History.ProjectHistoryAutoCapture.Use(history.Object, null, _project, _serviceUser))
        {
            using InferenceApiDbContext db = NewDb();
            RunStartOutcome refused = await NewStart(db).StartAsync(_project, _workflow, null, _key, null, null, default);
            Err(PublicApiResults.From(refused.Refusal!))["code"]!.GetValue<string>().Should().Be("API_KEY_BUDGET_EXCEEDED");
        }

        causes.Should().BeEmpty("a run refused over the key's budget captures no version");

        using (InferenceApiDbContext seed = NewDb())
        {
            seed.ApiKeys.Single().DailyCapMillis = null;
            seed.SaveChanges();
        }

        using (ReelBolt.Inference.Api.Services.History.ProjectHistoryAutoCapture.Use(history.Object, null, _project, _serviceUser))
        {
            using InferenceApiDbContext db = NewDb();
            (await NewStart(db).StartAsync(_project, _workflow, null, _key, null, null, default)).Started.Should().NotBeNull();
        }

        causes.Should().Equal(ProjectVersionCause.BeforeRun);
    }

    [Fact]
    public async Task A_project_the_key_may_not_see_is_a_404_and_the_billing_role_is_refused()
    {
        using InferenceApiDbContext db = NewDb();
        RunStartOutcome restricted = await NewStart(db, keyContext: new ApiKeyTestSupport.FakeKeyContext(_key, [Guid.NewGuid()]))
            .StartAsync(_project, _workflow, null, _key, null, null, default);
        PublicApiResults.From(restricted.Refusal!).StatusCode.Should().Be(404);

        RunStartOutcome billing = await NewStart(db, new ApiKeyTestSupport.FakeUser(_org, OrganizationRole.Billing, _serviceUser))
            .StartAsync(_project, _workflow, null, _key, null, null, default);
        billing.Refusal.Should().NotBeNull();
        Count(d => d.WorkflowExecutions).Should().Be(0);
    }

    [Fact]
    public async Task A_full_run_limit_is_reported_with_the_wait_and_starts_nothing_more()
    {
        _paidPerHour = 1;
        using InferenceApiDbContext db = NewDb();
        PublicRunStartService start = NewStart(db);
        (await start.StartAsync(_project, _workflow, null, _key, null, null, default)).Started.Should().NotBeNull();

        RunStartOutcome second = await start.StartAsync(_project, _workflow, null, _key, null, null, default);

        second.RateFull.Should().NotBeNull();
        second.RateFull!.RetryAfterSeconds.Should().BeInRange(1, 3600);
        Count(d => d.WorkflowExecutions).Should().Be(1);
        Count(d => d.RunRateEvents).Should().Be(1);
    }

    // ------------------------------------------------------------------ queue (controller)

    [Fact]
    public async Task Over_the_run_limit_the_start_is_queued_with_a_202_after_every_other_gate()
    {
        _paidPerHour = 1;
        using InferenceApiDbContext db = NewDb();
        RunsController controller = NewController(db);
        (await controller.Start(_project, _workflow, null, default)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);

        ObjectResult queued = (ObjectResult)await controller.Start(_project, _workflow, new PublicStartRunRequest(UserRequest: "later"), default);

        queued.StatusCode.Should().Be(202);
        PublicQueuedRunView body = queued.Value.Should().BeOfType<PublicQueuedRunView>().Subject;
        body.Status.Should().Be("queued");
        body.ProjectId.Should().Be(_project);
        body.WorkflowId.Should().Be(_workflow);
        body.EstimatedStartAt.Should().BeAfter(DateTime.UtcNow.AddSeconds(-5));
        using InferenceApiDbContext read = NewDb();
        ApiRunRequest row = read.ApiRunRequests.Single();
        row.Status.Should().Be(ApiRunRequest.Queued);
        row.ApiKeyId.Should().Be(_key);
        row.RequestJson.Should().Contain("later");
        read.WorkflowExecutions.Count().Should().Be(1, "the queued start created no run");
    }

    [Fact]
    public async Task A_refusal_while_the_limit_is_full_is_still_the_gates_answer_not_a_queue_entry()
    {
        _paidPerHour = 1;
        using InferenceApiDbContext db = NewDb();
        RunsController controller = NewController(db);
        await controller.Start(_project, _workflow, null, default);
        using (InferenceApiDbContext seed = NewDb())
        {
            seed.CreditAccounts.Single().BalanceMillis = 0;
            seed.SaveChanges();
        }

        using InferenceApiDbContext db2 = NewDb();
        ObjectResult refused = (ObjectResult)await NewController(db2).Start(_project, _workflow, null, default);

        refused.StatusCode.Should().Be(402);
        Count(d => d.ApiRunRequests).Should().Be(0);
    }

    [Fact]
    public async Task The_queue_is_bounded_per_key_with_429_run_queue_full_and_retry_after()
    {
        _paidPerHour = 1;
        using InferenceApiDbContext db = NewDb();
        RunsController controller = NewController(db, maxQueued: 1);
        await controller.Start(_project, _workflow, null, default);
        ((ObjectResult)await controller.Start(_project, _workflow, null, default)).StatusCode.Should().Be(202);

        ObjectResult full = (ObjectResult)await controller.Start(_project, _workflow, null, default);

        full.StatusCode.Should().Be(429);
        JsonNode error = Err(full);
        error["code"]!.GetValue<string>().Should().Be("run_queue_full");
        error["resetsAt"].Should().NotBeNull();
        PublicApi429Tests.AssertConsistent(controller.Response, full);
        Count(d => d.ApiRunRequests).Should().Be(1);
    }

    [Fact]
    public async Task The_plan_concurrency_cap_429_on_start_carries_the_three_horizon_fields()
    {
        _paidPerHour = 1;
        using InferenceApiDbContext seed = NewDb();
        seed.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = _workflow,
            ProjectId = _project,
            OrganizationId = _org,
            Status = ExecutionStatus.Running,
            QueuedAt = DateTime.UtcNow,
            InitiatedByUserId = _serviceUser,
            Initiator = ExecutionInitiator.Api
        });
        seed.SaveChanges();
        using InferenceApiDbContext db = NewDb();
        OrganizationEntitlementSet capped = new() { Tier = "pro", MaxOutputHeight = 2160, MaxConcurrentExecutions = 1 };
        RunsController controller = NewController(db, NewStart(db, entitlements: capped));

        ObjectResult refused = (ObjectResult)await controller.Start(_project, _workflow, null, default);

        PublicApi429Tests.AssertConsistent(controller.Response, refused);
        Count(d => d.ApiRunRequests).Should().Be(0);
    }

    [Fact]
    public async Task Cancel_works_while_queued_and_then_answers_409()
    {
        _paidPerHour = 1;
        using InferenceApiDbContext db = NewDb();
        RunsController controller = NewController(db);
        await controller.Start(_project, _workflow, null, default);
        PublicQueuedRunView queued = (PublicQueuedRunView)((ObjectResult)await controller.Start(_project, _workflow, null, default)).Value!;

        using InferenceApiDbContext cdb = NewDb();
        RunRequestsController requests = new(cdb, new ProjectAccessService(cdb, new ApiKeyTestSupport.FakeUser(_org, OrganizationRole.Member, _serviceUser)),
            new ApiKeyTestSupport.FakeKeyContext(_key), _clock);
        OkObjectResult ok = (OkObjectResult)await requests.Cancel(_project, queued.Id, default);
        ((PublicRunRequestView)ok.Value!).Status.Should().Be("cancelled");

        ObjectResult again = (ObjectResult)await requests.Cancel(_project, queued.Id, default);
        again.StatusCode.Should().Be(409);
        Err(again)["code"]!.GetValue<string>().Should().Be("run_request_not_cancellable");
    }

    [Fact]
    public async Task Another_key_cannot_read_a_run_request()
    {
        _paidPerHour = 1;
        using InferenceApiDbContext db = NewDb();
        RunsController controller = NewController(db);
        await controller.Start(_project, _workflow, null, default);
        PublicQueuedRunView queued = (PublicQueuedRunView)((ObjectResult)await controller.Start(_project, _workflow, null, default)).Value!;

        using InferenceApiDbContext rdb = NewDb();
        RunRequestsController other = new(rdb, new ProjectAccessService(rdb, new ApiKeyTestSupport.FakeUser(_org, OrganizationRole.Member, _serviceUser)),
            new ApiKeyTestSupport.FakeKeyContext(Guid.NewGuid()), _clock);
        ((ObjectResult)await other.Get(_project, queued.Id, default)).StatusCode.Should().Be(404);
    }

    // ------------------------------------------------------------------ dispatcher

    private sealed class BusyGuard : IAssistantExecutionGuard
    {
        public Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, RunRateBucket bucket, string source,
            Func<Task<Guid?>> insertCallback, CancellationToken ct) =>
            Task.FromResult(new RunRateDecision(false, bucket, 0, 10, 3600, 5, Busy: true));
    }

    private ServiceProvider Provider(OrganizationEntitlementSet? entitlements = null, IAssistantExecutionGuard? guardOverride = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped(sp => new InferenceApiDbContext(InMemoryOptions(), sp.GetRequiredService<ITenantContext>()));
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
            sp.GetRequiredService<InferenceApiDbContext>(), _publish.Object, NullLogger<WorkflowExecutionService>.Instance, sp.GetRequiredService<IPlanGateService>()));
        services.AddScoped<IAssistantExecutionGuard>(sp => guardOverride ?? new AssistantExecutionGuard(sp.GetRequiredService<InferenceApiDbContext>(), Config()));
        services.AddScoped<PublicRunStartService>();
        services.AddSingleton<IApiRunRequestEvents>(_events);
        services.AddSingleton(Fixed(entitlements ?? Pro));
        return services.BuildServiceProvider();
    }

    private ApiRunQueueDispatcher Dispatcher(ServiceProvider sp)
    {
        _options.RunQueue.StaleStartingMinutes = 5;
        return new ApiRunQueueDispatcher(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(_options),
            NullLogger<ApiRunQueueDispatcher>.Instance, _clock);
    }

    private Guid SeedRequest(string status = ApiRunRequest.Queued, DateTime? notBefore = null, DateTime? createdAt = null, DateTime? startingAt = null)
    {
        using InferenceApiDbContext db = NewDb();
        ApiRunRequest row = new()
        {
            Id = Guid.NewGuid(),
            ApiKeyId = _key,
            OrganizationId = _org,
            ProjectId = _project,
            WorkflowId = _workflow,
            RequestJson = "{\"userRequest\":\"queued\"}",
            Status = status,
            NotBefore = notBefore ?? _clock.Now.UtcDateTime.AddMinutes(-1),
            CreatedAt = createdAt ?? _clock.Now.UtcDateTime,
            UpdatedAt = _clock.Now.UtcDateTime,
            StartingAt = startingAt
        };
        db.ApiRunRequests.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    private ApiRunRequest Request(Guid id) { using InferenceApiDbContext db = NewDb(); return db.ApiRunRequests.AsNoTracking().Single(r => r.Id == id); }

    [Fact]
    public async Task The_dispatcher_starts_a_due_request_through_the_same_sequence_and_records_the_run()
    {
        Guid id = SeedRequest();
        using ServiceProvider sp = Provider();

        (await Dispatcher(sp).TickAsync(default)).Should().Be(1);

        ApiRunRequest row = Request(id);
        row.Status.Should().Be(ApiRunRequest.Started);
        row.ExecutionId.Should().NotBeNull();
        using InferenceApiDbContext db = NewDb();
        WorkflowExecution run = db.WorkflowExecutions.Single();
        run.Id.Should().Be(row.ExecutionId!.Value);
        run.CorrelationId.Should().Be(ApiRunRequest.CorrelationFor(id));
        run.Initiator.Should().Be(ExecutionInitiator.Api);
        run.ApiKeyId.Should().Be(_key);
        run.InitiatedByUserId.Should().Be(_serviceUser);
        run.UserRequest.Should().Be("queued");
        db.RunRateEvents.Single().Source.Should().Be("api");
        _events.Started.Should().ContainSingle().Which.Execution.Should().Be(run.Id);
    }

    [Fact]
    public async Task A_request_waits_while_the_window_is_still_full_and_starts_when_it_frees()
    {
        _paidPerHour = 1;
        using (InferenceApiDbContext db = NewDb())
            (await NewStart(db).StartAsync(_project, _workflow, null, _key, null, null, default)).Started.Should().NotBeNull();
        Guid id = SeedRequest();
        using ServiceProvider sp = Provider();
        ApiRunQueueDispatcher dispatcher = Dispatcher(sp);

        (await dispatcher.TickAsync(default)).Should().Be(0);
        ApiRunRequest waiting = Request(id);
        waiting.Status.Should().Be(ApiRunRequest.Queued);
        waiting.NotBefore.Should().BeAfter(_clock.Now.UtcDateTime, "a full window bumps not_before");
        (await dispatcher.TickAsync(default)).Should().Be(0, "not due yet: nothing is retried before not_before");

        _clock.Now = _clock.Now.AddHours(2);
        using (InferenceApiDbContext db = NewDb())
        {
            RunRateEvent old = db.RunRateEvents.Single();
            old.CreatedAt = DateTime.UtcNow.AddHours(-2);
            db.SaveChanges();
        }

        (await dispatcher.TickAsync(default)).Should().Be(1);
        Request(id).Status.Should().Be(ApiRunRequest.Started);
    }

    [Fact]
    public async Task A_busy_run_limit_lock_requeues_the_request_a_few_seconds_out_and_does_not_refuse_it()
    {
        Guid id = SeedRequest();
        using ServiceProvider sp = Provider(guardOverride: new BusyGuard());

        (await Dispatcher(sp).TickAsync(default)).Should().Be(0);

        ApiRunRequest row = Request(id);
        row.Status.Should().Be(ApiRunRequest.Queued);
        row.ErrorCode.Should().BeNull();
        row.StartingAt.Should().BeNull();
        row.NotBefore.Should().BeAfter(_clock.Now.UtcDateTime).And.BeOnOrBefore(_clock.Now.UtcDateTime.AddSeconds(6));
    }

    [Fact]
    public async Task A_busy_run_limit_lock_on_a_direct_start_is_still_an_immediate_429_with_retry_after()
    {
        using InferenceApiDbContext db = NewDb();
        RunsController controller = NewController(db, NewStart(db, guard: new BusyGuard()));

        ObjectResult refused = (ObjectResult)await controller.Start(_project, _workflow, null, default);

        PublicApi429Tests.AssertConsistent(controller.Response, refused);
        Count(d => d.ApiRunRequests).Should().Be(0);
    }

    [Fact]
    public async Task A_refusal_at_start_time_is_recorded_with_the_code_the_synchronous_path_gives()
    {
        Guid id = SeedRequest();
        using (InferenceApiDbContext db = NewDb())
        {
            db.ApiKeys.Single().DailyCapMillis = 1;
            db.SaveChanges();
        }

        using ServiceProvider sp = Provider();
        (await Dispatcher(sp).TickAsync(default)).Should().Be(0);

        ApiRunRequest row = Request(id);
        row.Status.Should().Be(ApiRunRequest.Refused);
        row.ErrorCode.Should().Be("API_KEY_BUDGET_EXCEEDED");
        row.ErrorMessage.Should().NotBeNullOrEmpty();
        _events.Refused.Should().ContainSingle().Which.Code.Should().Be("API_KEY_BUDGET_EXCEEDED");
        Count(d => d.WorkflowExecutions).Should().Be(0);
    }

    [Fact]
    public async Task A_revoked_or_expired_key_or_a_disabled_api_refuses_the_request()
    {
        Guid revoked = SeedRequest();
        using (InferenceApiDbContext db = NewDb())
        {
            db.ApiKeys.Single().RevokedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        using ServiceProvider sp = Provider();
        await Dispatcher(sp).TickAsync(default);
        Request(revoked).ErrorCode.Should().Be("api_key_revoked");

        using (InferenceApiDbContext db = NewDb())
        {
            ApiKey key = db.ApiKeys.Single();
            key.RevokedAt = null;
            key.ExpiresAt = DateTime.UtcNow.AddDays(-1);
            db.SaveChanges();
        }

        Guid expired = SeedRequest();
        await Dispatcher(sp).TickAsync(default);
        Request(expired).ErrorCode.Should().Be("api_key_expired");

        using (InferenceApiDbContext db = NewDb())
        {
            db.ApiKeys.Single().ExpiresAt = null;
            db.SaveChanges();
        }

        _options.Enabled = false;
        Guid off = SeedRequest();
        // A disabled API stops the dispatcher loop itself; the per-request check is the second line.
        await Dispatcher(sp).TickAsync(default);
        Request(off).ErrorCode.Should().Be("api_disabled");
        Count(d => d.WorkflowExecutions).Should().Be(0);
    }

    [Fact]
    public async Task A_claim_that_already_has_its_execution_is_reconciled_without_a_second_start()
    {
        Guid id = SeedRequest(ApiRunRequest.Starting, startingAt: _clock.Now.UtcDateTime.AddMinutes(-10));
        Guid executionId = Guid.NewGuid();
        using (InferenceApiDbContext db = NewDb())
        {
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = _workflow,
                ProjectId = _project,
                OrganizationId = _org,
                Status = ExecutionStatus.Queued,
                QueuedAt = DateTime.UtcNow,
                CorrelationId = ApiRunRequest.CorrelationFor(id),
                InitiatedByUserId = _serviceUser,
                Initiator = ExecutionInitiator.Api
            });
            db.SaveChanges();
        }

        using ServiceProvider sp = Provider();
        (await Dispatcher(sp).TickAsync(default)).Should().Be(0);

        Request(id).Status.Should().Be(ApiRunRequest.Started);
        Request(id).ExecutionId.Should().Be(executionId);
        Count(d => d.WorkflowExecutions).Should().Be(1, "the crashed start had already created the run");
        _events.Started.Should().ContainSingle();
    }

    [Fact]
    public async Task A_stale_claim_with_no_execution_goes_back_in_the_queue_and_starts_once()
    {
        Guid id = SeedRequest(ApiRunRequest.Starting, startingAt: _clock.Now.UtcDateTime.AddMinutes(-10));
        using ServiceProvider sp = Provider();

        (await Dispatcher(sp).TickAsync(default)).Should().Be(1);

        Request(id).Status.Should().Be(ApiRunRequest.Started);
        Count(d => d.WorkflowExecutions).Should().Be(1);
    }

    [Fact]
    public async Task A_fresh_claim_is_left_alone_and_a_key_starts_its_requests_in_order()
    {
        DateTime t = _clock.Now.UtcDateTime;
        Guid first = SeedRequest(createdAt: t.AddSeconds(-30));
        Guid second = SeedRequest(createdAt: t.AddSeconds(-20));
        using ServiceProvider sp = Provider();
        ApiRunQueueDispatcher dispatcher = Dispatcher(sp);

        (await dispatcher.TickAsync(default)).Should().Be(1, "one request per key per pass keeps FIFO");
        Request(first).Status.Should().Be(ApiRunRequest.Started);
        Request(second).Status.Should().Be(ApiRunRequest.Queued);

        (await dispatcher.TickAsync(default)).Should().Be(1);
        Request(second).Status.Should().Be(ApiRunRequest.Started);
    }

    // ------------------------------------------------------------------ reads and outputs

    [Fact]
    public async Task A_run_reads_with_public_status_initiator_credits_failure_and_steps_and_lists_by_status()
    {
        Guid runId = Guid.NewGuid();
        using (InferenceApiDbContext db = NewDb())
        {
            List<WorkflowStep> steps = db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == _workflow).OrderBy(s => s.StepOrder).ToList();
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
                ErrorMessage = "boom",
                Initiator = ExecutionInitiator.Mcp,
                InitiatedByUserId = _serviceUser
            });
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = runId,
                WorkflowStepId = steps[0].Id,
                Status = StepStatus.Failed,
                FailureCode = "SOURCE_TOO_DEMANDING",
                CreditsChargedMillis = 2500,
                CompletedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        using InferenceApiDbContext rdb = NewDb();
        RunsController controller = NewController(rdb);
        PublicRunView view = (PublicRunView)((OkObjectResult)await controller.Get(_project, runId, default)).Value!;
        view.Status.Should().Be("failed");
        view.Initiator.Should().Be("mcp");
        view.Credits!.Charged.Should().Be(2.5);
        view.Failure.Should().Be(new PublicRunFailureView("SOURCE_TOO_DEMANDING", "boom"));
        view.Steps.Should().ContainSingle().Which.Should().Match<PublicRunStepView>(s => s.Order == 1 && s.Name == "Write" && s.Status == "failed");

        PublicApiPage<PublicRunSummaryView> page = (PublicApiPage<PublicRunSummaryView>)((OkObjectResult)await controller.List(_project, null, "failed", null, null, default)).Value!;
        page.Data.Should().ContainSingle().Which.Id.Should().Be(runId);
        ((PublicApiPage<PublicRunSummaryView>)((OkObjectResult)await controller.List(_project, null, "succeeded", null, null, default)).Value!).Data.Should().BeEmpty();
        ((ObjectResult)await controller.List(_project, null, "weird", null, null, default)).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task A_project_restricted_key_does_not_see_a_run_of_another_project()
    {
        using InferenceApiDbContext db = NewDb();
        RunsController controller = NewController(db, projects: [Guid.NewGuid()]);
        ((ObjectResult)await controller.Get(_project, Guid.NewGuid(), default)).StatusCode.Should().Be(404);
        ((ObjectResult)await controller.Start(_project, _workflow, null, default)).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task The_output_link_is_minted_for_an_addressable_output_and_a_missing_one_is_404()
    {
        Guid runId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        Guid stepId;
        using (InferenceApiDbContext db = NewDb())
        {
            stepId = db.WorkflowSteps.First(s => s.WorkflowDefinitionId == _workflow).Id;
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = runId,
                WorkflowDefinitionId = _workflow,
                ProjectId = _project,
                OrganizationId = _org,
                Status = ExecutionStatus.Passed,
                QueuedAt = DateTime.UtcNow,
                InitiatedByUserId = _serviceUser
            });
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = resultId,
                WorkflowExecutionId = runId,
                WorkflowStepId = stepId,
                Status = StepStatus.Completed,
                OutputStorageKey = $"projects/{_project}/outputFiles/{Guid.NewGuid()}.mp4",
                CompletedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        using InferenceApiDbContext rdb = NewDb();
        ApiKeyTestSupport.FakeUser user = new(_org, OrganizationRole.Member, _serviceUser);
        Mock<IOutputDescriber> describer = new();
        describer.Setup(d => d.FindRenderAsync(_project, resultId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid p, Guid r, CancellationToken _) => new OutputRender(r, "k", "out.mp4", "video/mp4", 1234));
        OutputLinkService links = new(ConfirmationTokenService.CreateEphemeral(), new ConfigurationBuilder().Build(), null);
        RunOutputsController controller = new(rdb, user, new ProjectAccessService(rdb, user), describer.Object, links, new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        PublicOutputLinkView link = (PublicOutputLinkView)((OkObjectResult)await controller.Link(_project, resultId, default)).Value!;
        link.Url.Should().NotBeNullOrEmpty().And.NotContain("projects/");
        link.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        ((ObjectResult)await controller.Link(_project, Guid.NewGuid(), default)).StatusCode.Should().Be(404);

        List<PublicOutputView> outputs = (List<PublicOutputView>)((OkObjectResult)await controller.List(_project, runId, default)).Value!;
        outputs.Should().ContainSingle().Which.Should().Match<PublicOutputView>(o => o.Kind == "video" && o.SizeBytes == 1234 && o.StepOrder == 1 && o.FileName == "out.mp4");
    }

    // ------------------------------------------------------------------ retry/resume: the caller pays, the snapshot classifies

    private Guid SeedFailedRun(Guid? apiKeyId, Guid? snapshot = null, Guid? initiator = null)
    {
        Guid id = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = id,
            WorkflowDefinitionId = _workflow,
            ProjectId = _project,
            OrganizationId = _org,
            Status = ExecutionStatus.Failed,
            QueuedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            InitiatedByUserId = initiator ?? _owner,
            ApiKeyId = apiKeyId,
            SnapshotDefinitionId = snapshot
        });
        db.SaveChanges();
        return id;
    }

    private Guid SeedKey(long? daily)
    {
        Guid id = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        db.ApiKeys.Add(NewKey(id, daily));
        db.SaveChanges();
        return id;
    }

    private async Task<IActionResult> Retry(Guid runId, Guid? caller)
    {
        using InferenceApiDbContext db = NewDb();
        ApiKeyTestSupport.FakeUser user = new(_org, OrganizationRole.Member, _serviceUser);
        PlanGateService gate = new(db, Fixed(Pro));
        WorkflowCostEstimator estimator = new(new SingleContextScopes(db), Options.Create(new BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance);
        WorkflowsController dashboard = new(
            db, user, new ProjectAccessService(db, user), _publish.Object,
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(db, _publish.Object, NullLogger<WorkflowExecutionService>.Instance, gate),
            NullLogger<WorkflowsController>.Instance, planGate: gate, costEstimator: estimator)
        {
            ApiKeyOverride = caller,
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (await dashboard.RetryExecution(_project, _workflow, runId, null, default)).Result!;
    }

    private Guid? KeyOf(Guid runId) { using InferenceApiDbContext db = NewDb(); return db.WorkflowExecutions.Single(e => e.Id == runId).ApiKeyId; }

    [Fact]
    public async Task A_dashboard_run_retried_through_a_capped_key_checks_that_keys_cap_and_writes_nothing()
    {
        Guid capped = SeedKey(daily: 1);
        Guid run = SeedFailedRun(apiKeyId: null);

        IActionResult result = await Retry(run, capped);

        ObjectResult refusal = result.Should().BeAssignableTo<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);
        Err(PublicApiResults.From(refusal))["code"]!.GetValue<string>().Should().Be("API_KEY_BUDGET_EXCEEDED");
        KeyOf(run).Should().BeNull("a refused retry changes nothing");
    }

    [Fact]
    public async Task A_run_of_key_A_retried_by_key_B_is_charged_to_B_and_the_same_key_is_unchanged()
    {
        Guid a = SeedKey(null);
        Guid b = SeedKey(null);
        Guid byA = SeedFailedRun(a);

        (await Retry(byA, b)).Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(200);
        KeyOf(byA).Should().Be(b);

        Guid same = SeedFailedRun(a);
        (await Retry(same, a)).Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(200);
        KeyOf(same).Should().Be(a);

        Guid dashboard = SeedFailedRun(a);
        (await Retry(dashboard, null)).Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(200);
        KeyOf(dashboard).Should().Be(a, "the dashboard keeps charging whatever key the row has");
    }

    [Fact]
    public async Task A_restart_is_classified_from_the_snapshot_not_the_live_workflow()
    {
        Guid snapshot = Guid.NewGuid();
        Guid agent;
        using (InferenceApiDbContext db = NewDb())
        {
            agent = db.AgentDefinitions.First().Id;
            db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = snapshot,
                Name = "snap",
                ProjectId = _project,
                Kind = WorkflowDefinitionKind.ExecutionSnapshot,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Steps = [new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent }]
            });
            // The live workflow is edited to free-only steps.
            db.WorkflowSteps.RemoveRange(db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == _workflow && s.StepType == StepType.Agent));
            db.SaveChanges();
        }

        Guid run = SeedFailedRun(null, snapshot);
        using InferenceApiDbContext read = NewDb();

        (await RunsController.ClassifyRestartAsync(read, run, _workflow, _ => Task.FromResult(true), default))
            .Should().Be(ReelBolt.Shared.Workflows.RunRateBucket.Paid);
        (await RunsController.ClassifyRestartAsync(read, run, _workflow, _ => Task.FromResult(false), default))
            .Should().Be(ReelBolt.Shared.Workflows.RunRateBucket.Free, "without an engine snapshot the workflow is what runs");
    }

    [Fact]
    public async Task A_start_is_classified_when_it_actually_starts_and_stamps_the_admitted_bucket()
    {
        using (InferenceApiDbContext db = NewDb())
            (await NewStart(db).StartAsync(_project, _workflow, null, _key, null, null, default)).Started.Should().NotBeNull();
        using (InferenceApiDbContext db = NewDb())
            db.WorkflowExecutions.Single().AdmittedRunRateBucket.Should().Be("paid");

        // Queued while paid; the workflow becomes free-only before the dispatcher starts it.
        Guid id = SeedRequest();
        using (InferenceApiDbContext db = NewDb())
        {
            db.WorkflowSteps.RemoveRange(db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == _workflow && s.StepType == StepType.Agent));
            db.SaveChanges();
        }

        using ServiceProvider sp = Provider();
        (await Dispatcher(sp).TickAsync(default)).Should().Be(1);

        using InferenceApiDbContext read = NewDb();
        Guid runId = Request(id).ExecutionId!.Value;
        read.WorkflowExecutions.Single(e => e.Id == runId).AdmittedRunRateBucket.Should().Be("free");
        read.RunRateEvents.Single(e => e.ExecutionId == runId).Bucket.Should().Be(RunRateEvent.FreeBucket);
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

    // ------------------------------------------------------------------ run-path review fixes (T-022)

    /// <summary>
    /// A throwaway Postgres database with the engine's schema (its model carries the unique partial index on
    /// <c>workflow_executions.correlation_id</c>, which has no migration yet) plus the API-only tables, then the same
    /// seed as the in-memory tests. Dropped on dispose.
    /// </summary>
    private async Task UsePostgresAsync()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_runs_test_" + Guid.NewGuid().ToString("N")[..12];
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

        // The tables are split between two models (the API model owns projects and users, the engine model owns
        // workflow_executions and carries the unique partial index), and neither has the other's tables in its
        // migrations. Run both create scripts, twice, skipping a statement whose table or object is not there yet
        // (a foreign key to the other model's table) or is already there.
        await using InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options);
        await using WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options);
        for (int pass = 0; pass < 3; pass++)
        {
            await RunScriptAsync(api, api.Database.GenerateCreateScript());
            await RunScriptAsync(engine, engine.Database.GenerateCreateScript());
        }

        _pg = connection;
        Seed();
    }

    /// <summary>A controller whose request services can build the dashboard <see cref="WorkflowsController"/> it delegates to.</summary>
    private RunsController NewControllerWithDashboard(InferenceApiDbContext db)
    {
        RunsController controller = NewController(db);
        ApiKeyTestSupport.FakeUser user = new(_org, OrganizationRole.Member, _serviceUser);
        PlanGateService gate = new(db, Fixed(Pro));
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddSingleton<ICurrentUser>(user);
        services.AddSingleton<IProjectAccessService>(new ProjectAccessService(db, user, new ApiKeyTestSupport.FakeKeyContext(_key)));
        services.AddSingleton(_publish.Object);
        services.AddSingleton(new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()));
        services.AddSingleton<IWorkflowExecutionService>(new WorkflowExecutionService(db, _publish.Object, NullLogger<WorkflowExecutionService>.Instance, gate));
        services.AddSingleton<IPlanGateService>(gate);
        services.AddSingleton<IWorkflowCostEstimator>(new WorkflowCostEstimator(new SingleContextScopes(db),
            Options.Create(new BillingOptions { Enabled = true }), Options.Create(new StepCreditEstimateOptions()), NullLogger<WorkflowCostEstimator>.Instance));
        controller.ControllerContext.HttpContext.RequestServices = services.BuildServiceProvider();
        return controller;
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

    private static string CodeOf(IActionResult r) => Err(PublicApiResults.From((ObjectResult)r))["code"]!.GetValue<string>();

    [PostgresFact]
    public async Task Parallel_starts_with_the_same_key_make_exactly_one_execution()
    {
        await UsePostgresAsync();
        _paidPerHour = 100;
        string correlation = PublicIdempotency.CorrelationId(_key, "same-key");

        RunStartOutcome[] outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using InferenceApiDbContext db = NewDb();
            return await NewStart(db).StartAsync(_project, _workflow, null, _key, correlation, null, default);
        }));

        outcomes.Should().OnlyContain(o => o.Started != null);
        outcomes.Select(o => o.Started!.Id).Distinct().Should().ContainSingle("every racer answers the one run");
        using InferenceApiDbContext verify = NewDb();
        verify.WorkflowExecutions.Count(e => e.CorrelationId == correlation).Should().Be(1);
        verify.WorkflowExecutions.Count().Should().Be(1);
    }

    [Fact]
    public async Task A_start_with_an_existing_correlation_id_answers_that_run_and_writes_nothing()
    {
        string correlation = PublicIdempotency.CorrelationId(_key, "again");
        using (InferenceApiDbContext db = NewDb())
            (await NewStart(db).StartAsync(_project, _workflow, null, _key, correlation, null, default)).Started.Should().NotBeNull();

        using InferenceApiDbContext second = NewDb();
        RunStartOutcome again = await NewStart(second).StartAsync(_project, _workflow, null, _key, correlation, null, default);

        again.Started.Should().NotBeNull();
        Count(d => d.WorkflowExecutions).Should().Be(1);
        Count(d => d.RunRateEvents).Should().Be(1, "the replayed start consumes no second run-limit slot");
    }

    [PostgresFact]
    public async Task Two_dispatchers_racing_on_one_queued_row_start_it_exactly_once()
    {
        await UsePostgresAsync();
        Guid id = SeedRequest();
        using ServiceProvider spA = Provider();
        using ServiceProvider spB = Provider();

        int[] started = await Task.WhenAll(Dispatcher(spA).TickAsync(default), Dispatcher(spB).TickAsync(default));

        started.Sum().Should().Be(1);
        Request(id).Status.Should().Be(ApiRunRequest.Started);
        Count(d => d.WorkflowExecutions).Should().Be(1);
    }

    [Fact]
    public async Task A_dispatcher_that_lost_its_claim_to_a_stale_reset_starts_nothing_and_the_new_holder_starts_once()
    {
        DateTime t1 = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime t2 = t1.AddMinutes(10);
        Guid id = SeedRequest(ApiRunRequest.Starting, startingAt: t1);
        using ServiceProvider sp = Provider();
        ApiRunQueueDispatcher dispatcher = Dispatcher(sp);

        // Another dispatcher reset the stale claim and took the row again: the token is now t2.
        using (InferenceApiDbContext db = NewDb())
        {
            db.ApiRunRequests.Single(r => r.Id == id).StartingAt = t2;
            db.SaveChanges();
        }

        (await dispatcher.ProcessAsync(id, t1, default)).Should().BeFalse("the late holder's token no longer matches");
        Count(d => d.WorkflowExecutions).Should().Be(0);

        (await dispatcher.ProcessAsync(id, t2, default)).Should().BeTrue();
        (await dispatcher.ProcessAsync(id, t1, default)).Should().BeFalse("and it is started already");
        Count(d => d.WorkflowExecutions).Should().Be(1);
    }

    [Fact]
    public async Task A_stale_claim_whose_run_already_exists_is_marked_started_not_started_again()
    {
        Guid id = SeedRequest(ApiRunRequest.Starting, startingAt: _clock.Now.UtcDateTime.AddMinutes(-10));
        Guid existing = Guid.NewGuid();
        using (InferenceApiDbContext db = NewDb())
        {
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = existing,
                WorkflowDefinitionId = _workflow,
                ProjectId = _project,
                OrganizationId = _org,
                Status = ExecutionStatus.Queued,
                QueuedAt = DateTime.UtcNow,
                CorrelationId = ApiRunRequest.CorrelationFor(id),
                InitiatedByUserId = _serviceUser,
                ApiKeyId = _key
            });
            db.SaveChanges();
        }

        using ServiceProvider sp = Provider();
        (await Dispatcher(sp).TickAsync(default)).Should().Be(0);

        ApiRunRequest row = Request(id);
        row.Status.Should().Be(ApiRunRequest.Started);
        row.ExecutionId.Should().Be(existing);
        Count(d => d.WorkflowExecutions).Should().Be(1);
    }

    [PostgresFact]
    public async Task The_queue_bound_holds_under_concurrent_starts()
    {
        await UsePostgresAsync();
        _paidPerHour = 1;
        using (InferenceApiDbContext first = NewDb())
            (await NewController(first, maxQueued: 2).Start(_project, _workflow, null, default)).Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(201, "the hourly slot is taken");

        IActionResult[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using InferenceApiDbContext db = NewDb();
            return await NewController(db, maxQueued: 2).Start(_project, _workflow, null, default);
        }));

        int queued = Count(d => d.ApiRunRequests);
        queued.Should().BeInRange(1, 2, "the count and the insert are one critical section per key");
        results.OfType<ObjectResult>().Count(r => r.StatusCode == 202).Should().Be(queued);
    }

    [Fact]
    public async Task Retry_and_resume_of_a_run_a_person_started_are_403_while_stop_and_a_service_run_pass()
    {
        Guid byPerson = SeedFailedRun(null, initiator: _owner);
        Guid byService = SeedFailedRun(null, initiator: _serviceUser);

        using (InferenceApiDbContext db = NewDb())
        {
            RunsController c = NewController(db);
            IActionResult retry = await c.Retry(_project, byPerson, null, default);
            ((ObjectResult)retry).StatusCode.Should().Be(403);
            CodeOf(retry).Should().Be("run_not_owned_by_api_key");
            CodeOf(await c.Resume(_project, byPerson, null, default)).Should().Be("run_not_owned_by_api_key");
        }

        using (InferenceApiDbContext db = NewDb())
        {
            IActionResult stop = await NewControllerWithDashboard(db).Stop(_project, byPerson, default);
            ((ObjectResult)stop).StatusCode.Should().NotBe(403, "stopping does not elevate");
        }

        using (InferenceApiDbContext db = NewDb())
        {
            IActionResult retry = await NewControllerWithDashboard(db).Retry(_project, byService, null, default);
            ((ObjectResult)retry).StatusCode.Should().NotBe(403, "this organization's service account started it");
        }
    }

    [Fact]
    public async Task A_dashboard_retry_of_a_run_whose_key_was_revoked_or_expired_clears_the_key_but_a_live_key_stays()
    {
        Guid revoked = SeedKey(null);
        Guid expired = SeedKey(null);
        Guid live = SeedKey(null);
        using (InferenceApiDbContext db = NewDb())
        {
            db.ApiKeys.Single(k => k.Id == revoked).RevokedAt = DateTime.UtcNow.AddHours(-1);
            db.ApiKeys.Single(k => k.Id == expired).ExpiresAt = DateTime.UtcNow.AddHours(-1);
            db.SaveChanges();
        }

        Guid byRevoked = SeedFailedRun(revoked);
        Guid byExpired = SeedFailedRun(expired);
        Guid byLive = SeedFailedRun(live);
        foreach (Guid run in new[] { byRevoked, byExpired, byLive })
            (await Retry(run, null)).Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(200);

        KeyOf(byRevoked).Should().BeNull("the organization pays, as for any dashboard run");
        KeyOf(byExpired).Should().BeNull();
        KeyOf(byLive).Should().Be(live);
    }

    // ------------------------------------------------------------------ T-028 review fixes

    [Fact]
    public async Task The_dispatcher_refuses_a_request_of_a_soft_deleted_organization_as_inactive()
    {
        // M7/M8: deleted_at set by the Go API; the key and service user still exist until the purge.
        Guid request = SeedRequest();
        using (InferenceApiDbContext db = NewDb())
        {
            db.Organizations.Single(o => o.Id == _org).DeletedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        using ServiceProvider sp = Provider();
        await Dispatcher(sp).TickAsync(default);

        Request(request).ErrorCode.Should().Be("api_key_inactive");
        Count(d => d.WorkflowExecutions).Should().Be(0);
    }

    [Fact]
    public async Task L21_the_dispatcher_refuses_a_start_when_the_plan_no_longer_includes_api_access()
    {
        Guid request = SeedRequest();
        using ServiceProvider sp = Provider(new OrganizationEntitlementSet { Tier = "free", ApiAccess = false });

        await Dispatcher(sp).TickAsync(default);

        Request(request).ErrorCode.Should().Be(PlanGate.ApiPlanReadOnlyCode, "the synchronous path answers the same code");
        Count(d => d.WorkflowExecutions).Should().Be(0);
    }

    private RunsController ControllerOfKey(InferenceApiDbContext db, Guid keyId, Guid serviceUser)
    {
        ApiKeyTestSupport.FakeUser user = new(_org, OrganizationRole.Member, serviceUser);
        IApiKeyContext keyContext = new ApiKeyTestSupport.FakeKeyContext(keyId);
        return new RunsController(
            db, user, new ProjectAccessService(db, user, keyContext), keyContext,
            NewStart(db, user, keyContext), new AssistantExecutionGuard(db, Config()), Options.Create(_options), _clock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task L19a_key_B_cannot_retry_or_resume_a_run_key_A_started_but_may_stop_it()
    {
        Guid keyB = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        using (InferenceApiDbContext db = NewDb())
        {
            db.ApplicationUsers.Add(new ApplicationUser { Id = userB, Email = "svcb@example.com", DisplayName = "b", EmailVerifiedAt = DateTime.UtcNow, IsServiceAccount = true });
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = _org, UserId = userB, Role = OrganizationRole.Member });
            ApiKey b = NewKey(keyB);
            b.ServiceUserId = userB;
            db.ApiKeys.Add(b);
            db.SaveChanges();
        }

        Guid byA = SeedFailedRun(_key, initiator: _serviceUser);

        using (InferenceApiDbContext db = NewDb())
        {
            RunsController c = ControllerOfKey(db, keyB, userB);
            IActionResult retry = await c.Retry(_project, byA, null, default);
            ((ObjectResult)retry).StatusCode.Should().Be(403);
            CodeOf(retry).Should().Be("run_not_owned_by_api_key");
            CodeOf(await c.Resume(_project, byA, null, default)).Should().Be("run_not_owned_by_api_key");
        }

        using (InferenceApiDbContext db = NewDb())
        {
            IActionResult retry = await NewControllerWithDashboard(db).Retry(_project, byA, null, default);
            ((ObjectResult)retry).StatusCode.Should().NotBe(403, "key A started it");
        }
    }

    [Fact]
    public async Task L19b_a_dashboard_retry_or_resume_of_a_run_whose_service_key_is_revoked_is_409_run_key_revoked()
    {
        Guid revoked = SeedKey(null);
        using (InferenceApiDbContext db = NewDb())
        {
            db.ApiKeys.Single(k => k.Id == revoked).RevokedAt = DateTime.UtcNow.AddHours(-1);
            db.SaveChanges();
        }

        Guid run = SeedFailedRun(revoked, initiator: _serviceUser);
        IActionResult result = await Retry(run, null);

        ConflictObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.ToString().Should().Contain("run_key_revoked");
        KeyOf(run).Should().Be(revoked, "the key is not cleared");

        Guid live = SeedKey(null);
        Guid liveRun = SeedFailedRun(live, initiator: _serviceUser);
        (await Retry(liveRun, null)).Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task L20_a_correlation_id_reuses_a_run_only_for_the_same_workflow_and_inside_the_idempotency_window()
    {
        string correlation = PublicIdempotency.CorrelationId(_key, "old");
        Guid oldRun = Guid.NewGuid();
        using (InferenceApiDbContext db = NewDb())
        {
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = oldRun,
                WorkflowDefinitionId = _workflow,
                ProjectId = _project,
                OrganizationId = _org,
                CorrelationId = correlation,
                QueuedAt = DateTime.UtcNow.AddHours(-48),
                InitiatedByUserId = _serviceUser,
                ApiKeyId = _key
            });
            db.SaveChanges();
        }

        using InferenceApiDbContext second = NewDb();
        RunStartOutcome again = await NewStart(second).StartAsync(_project, _workflow, null, _key, correlation, null, default);

        again.Started.Should().NotBeNull();
        again.Started!.Id.Should().NotBe(oldRun, "the window passed, so the same id is a new run");
    }

    private Guid SeedOldRunHoldingCorrelation(string correlation, Guid workflowId)
    {
        Guid id = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = id,
            WorkflowDefinitionId = workflowId,
            ProjectId = _project,
            OrganizationId = _org,
            CorrelationId = correlation,
            QueuedAt = DateTime.UtcNow.AddHours(-48),
            InitiatedByUserId = _serviceUser,
            ApiKeyId = _key
        });
        db.SaveChanges();
        return id;
    }

    [PostgresFact]
    public async Task An_idempotency_key_reused_after_its_window_replays_the_old_run_instead_of_a_500()
    {
        await UsePostgresAsync();
        string correlation = PublicIdempotency.CorrelationId(_key, "stale");
        Guid oldRun = SeedOldRunHoldingCorrelation(correlation, _workflow);

        using InferenceApiDbContext db = NewDb();
        RunStartOutcome again = await NewStart(db).StartAsync(_project, _workflow, null, _key, correlation, null, default);

        again.Started.Should().NotBeNull("the unique index still holds the old id, so the start answers that run");
        again.Started!.Id.Should().Be(oldRun);
        using InferenceApiDbContext verify = NewDb();
        verify.WorkflowExecutions.Count(e => e.CorrelationId == correlation).Should().Be(1);
    }

    [PostgresFact]
    public async Task An_idempotency_key_reused_after_its_window_for_another_workflow_is_422_idempotency_key_reused()
    {
        await UsePostgresAsync();
        string correlation = PublicIdempotency.CorrelationId(_key, "stale-other");
        Guid otherWorkflow = Guid.NewGuid();
        using (InferenceApiDbContext seed = NewDb())
        {
            seed.WorkflowDefinitions.Add(new WorkflowDefinition { Id = otherWorkflow, Name = "other", ProjectId = _project, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            seed.SaveChanges();
        }

        SeedOldRunHoldingCorrelation(correlation, otherWorkflow);

        using InferenceApiDbContext db = NewDb();
        RunStartOutcome outcome = await NewStart(db).StartAsync(_project, _workflow, null, _key, correlation, null, default);

        outcome.Started.Should().BeNull();
        ObjectResult refusal = outcome.Refusal.Should().BeAssignableTo<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(422);
        CodeOf(refusal).Should().Be("idempotency_key_reused");
    }
}
