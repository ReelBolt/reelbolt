using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>
/// ProposeReviseComposition / ApplyReviseComposition: revisions as assistant and MCP tools, with the REAL
/// revision service, the REAL run guard (revisions count in the paid bucket) and the real token service.
/// </summary>
public class AssistantRevisionToolsTests
{
    private const string Manifest =
        "{\"Version\":1,\"CompositionId\":\"FinalVideo\",\"Fps\":30,\"Scenes\":[" +
        "{\"Handle\":\"scene-1\",\"Name\":\"Intro\",\"FromFrame\":0,\"DurationInFrames\":90,\"Description\":\"Logo\",\"OnScreenText\":[]}]}";

    private sealed class Caller(Guid userId, bool emailVerified = true) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool EmailVerified { get; } = emailVerified;
    }

    private sealed class BrokeEstimator : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(Estimate());

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowPreflightResult(WorkflowPreflightOutcome.InsufficientCredits, Estimate(), 500));

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            Task.FromResult(Estimate());

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            PreflightAsync(organizationId, runSteps, ct);

        private static WorkflowCostEstimate Estimate() => new(true, false, 2_000, 6_000, 6_000, []);
    }

    private sealed class ThrowingEstimator : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            throw new InvalidOperationException("pricing is down");

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            throw new InvalidOperationException("pricing is down");

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            throw new InvalidOperationException("pricing is down");

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            throw new InvalidOperationException("pricing is down");
    }

    private sealed class Rig : IDisposable
    {
        public InferenceApiDbContext Db { get; }
        public Guid User { get; } = Guid.NewGuid();
        public Project Project { get; }
        public Guid ReviserAgentId { get; } = Guid.NewGuid();
        public IConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();
        private readonly AssistantExecutionGuard _guard;
        private readonly CompositionRevisionService _service;

        private readonly IWorkflowCostEstimator? _estimator;

        public Rig(IWorkflowCostEstimator? estimator = null, int revisionsPerHour = 10)
        {
            _estimator = estimator;
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase("revision-tools-" + Guid.NewGuid().ToString("N")).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = User, OrganizationId = TestTenancy.OrgOf(User), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);
            Db.AgentDefinitions.Add(new AgentDefinition { Id = ReviserAgentId, Name = "CompositionReviser", AgentType = AgentType.CompositionReviser, IsBuiltIn = true });
            Db.SaveChanges();
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CompositionRevisions:MaxPerHourPerUserProject"] = revisionsPerHour.ToString()
            }).Build();
            _guard = new AssistantExecutionGuard(Db, config);
            _service = new CompositionRevisionService(
                Db,
                new WorkflowExecutionService(Db, new Mock<IPublishEndpoint>().Object, NullLogger<WorkflowExecutionService>.Instance),
                config,
                NullLogger<CompositionRevisionService>.Instance,
                estimator);
        }

        public WorkflowExecution FilmRun(ExecutionStatus status = ExecutionStatus.Passed, DateTime? completedAt = null, bool capture = true)
        {
            WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Promo", ProjectId = Project.Id };
            WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = ReviserAgentId, StepOrder = 1, StepType = StepType.Agent };
            WorkflowExecution run = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                ProjectId = Project.Id,
                OrganizationId = Project.OrganizationId,
                Status = status,
                StartedAt = DateTime.UtcNow.AddMinutes(-10),
                CompletedAt = completedAt ?? DateTime.UtcNow.AddMinutes(-1)
            };
            Db.WorkflowDefinitions.Add(workflow);
            Db.WorkflowSteps.Add(step);
            Db.WorkflowExecutions.Add(run);
            Db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = run.Id,
                WorkflowStepId = step.Id,
                Status = StepStatus.Completed,
                OutputStorageKey = $"projects/{Project.Id}/outputFiles/{run.Id}/promo.mp4"
            });
            if (capture)
            {
                CompositionWorkspace workspace = new() { Id = Guid.NewGuid(), ProjectId = Project.Id, OrganizationId = Project.OrganizationId, OriginExecutionId = run.Id, LatestVersionNumber = 1 };
                Db.CompositionWorkspaces.Add(workspace);
                Db.CompositionWorkspaceVersions.Add(new CompositionWorkspaceVersion
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    ProjectId = Project.Id,
                    VersionNumber = 1,
                    StorageKey = ReelBolt.Shared.Compositions.CompositionWorkspaceKeys.ArchiveKey(Project.Id, workspace.Id, Guid.NewGuid()),
                    Sha256 = new string('a', 64),
                    CreatedByExecutionId = run.Id,
                    CompositionId = "FinalVideo",
                    Fps = 30,
                    SceneManifestJson = Manifest,
                    RenderOutputStorageKey = $"projects/{Project.Id}/outputFiles/{run.Id}/promo.mp4"
                });
            }

            Db.SaveChanges();
            return run;
        }

        public void SeedEvents(string bucket, int count)
        {
            for (int i = 0; i < count; i++)
                Db.RunRateEvents.Add(new RunRateEvent { Id = Guid.NewGuid(), UserId = User, ProjectId = Project.Id, Bucket = bucket, Source = "execute", CreatedAt = DateTime.UtcNow.AddMinutes(-5 - i) });
            Db.SaveChanges();
        }

        public AssistantRevisionTools Tools(Guid? asUser = null, bool emailVerified = true, ICreditAccountProvisioner? trialCredits = null, ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null)
        {
            var caller = new Caller(asUser ?? User, emailVerified);
            return new AssistantRevisionTools(Db, TestTenancy.Access(Db, caller), _service, _guard, Tokens, caller, trialCredits, _estimator, productEvents);
        }

        public async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args, AssistantRevisionTools? tools = null, ExecutionInitiator initiator = ExecutionInitiator.Assistant)
        {
            tools ??= Tools();
            AIFunction fn = tools.GetTools(new AssistantToolContext(User, Project.Id, Initiator: initiator)).Single(t => t.Name == tool);
            object? raw = await fn.InvokeAsync(new AIFunctionArguments(args));
            string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public async Task<string> ProposeTokenAsync(Guid executionId, string instruction = "Bigger intro title", string[]? scenes = null)
        {
            JsonElement proposal = await CallAsync("ProposeReviseComposition", Args(executionId, instruction, scenes));
            return proposal.GetProperty("confirmationToken").GetString()!;
        }

        public static Dictionary<string, object?> Args(Guid executionId, string instruction, string[]? scenes = null, string? token = null)
        {
            Dictionary<string, object?> args = new() { ["executionId"] = executionId, ["instruction"] = instruction };
            if (scenes is not null) args["scenes"] = scenes;
            if (token is not null) args["confirmationToken"] = token;
            return args;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Propose_returns_scenes_unknown_scenes_estimate_and_a_token_and_writes_nothing()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();

        JsonElement result = await rig.CallAsync("ProposeReviseComposition", Rig.Args(run.Id, "Bigger intro title", ["Intro", "Nowhere"]));

        result.GetProperty("operation").GetString().Should().Be("ReviseComposition");
        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
        result.GetProperty("projectName").GetString().Should().Be("Launch");
        result.GetProperty("workflowName").GetString().Should().Be("Promo");
        result.GetProperty("summary").GetString().Should().Contain("Bigger intro title");
        JsonElement scene = result.GetProperty("availableScenes").EnumerateArray().Single();
        scene.GetProperty("name").GetString().Should().Be("Intro");
        scene.GetProperty("fromFrame").GetInt32().Should().Be(0);
        scene.GetProperty("durationInFrames").GetInt32().Should().Be(90);
        result.GetProperty("unknownScenes").EnumerateArray().Select(e => e.GetString()).Should().Equal("Nowhere");
        result.GetProperty("cutPolicy").GetString().Should().Be("KeepCut");
        rig.Db.CompositionRevisions.Should().BeEmpty();
        rig.Db.RunRateEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Propose_with_billing_off_reports_a_free_affordable_estimate()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();

        JsonElement estimate = (await rig.CallAsync("ProposeReviseComposition", Rig.Args(run.Id, "Bigger intro title"))).GetProperty("estimate");

        estimate.GetProperty("billingEnabled").GetBoolean().Should().BeFalse();
        estimate.GetProperty("required").GetDouble().Should().Be(0);
        estimate.GetProperty("affordable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Propose_on_another_organizations_execution_is_refused_without_a_token()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();

        JsonElement result = await rig.CallAsync("ProposeReviseComposition", Rig.Args(run.Id, "Bigger intro title"), rig.Tools(asUser: Guid.NewGuid()));

        result.TryGetProperty("error", out _).Should().BeTrue();
        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Propose_on_a_run_that_is_still_running_is_refused_with_RUN_NOT_FINISHED()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun(status: ExecutionStatus.Running);

        JsonElement result = await rig.CallAsync("ProposeReviseComposition", Rig.Args(run.Id, "Bigger intro title"));

        result.GetProperty("code").GetString().Should().Be("RUN_NOT_FINISHED");
        result.GetProperty("error").GetString().Should().Contain("finished");
        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Propose_on_a_run_with_no_recoverable_workspace_is_refused_with_WORKSPACE_UNAVAILABLE()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun(completedAt: DateTime.UtcNow.AddDays(-30), capture: false);

        JsonElement result = await rig.CallAsync("ProposeReviseComposition", Rig.Args(run.Id, "Bigger intro title"));

        result.GetProperty("code").GetString().Should().Be("WORKSPACE_UNAVAILABLE");
        result.GetProperty("error").GetString().Should().Contain("7 days");
    }

    [Fact]
    public async Task Propose_rejects_an_empty_or_oversized_instruction_and_too_many_scenes()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();

        (await rig.CallAsync("ProposeReviseComposition", Rig.Args(run.Id, "  "))).TryGetProperty("error", out _).Should().BeTrue();
        (await rig.CallAsync("ProposeReviseComposition", Rig.Args(run.Id, new string('x', 4001)))).TryGetProperty("error", out _).Should().BeTrue();
        string[] fiftyOne = Enumerable.Range(0, 51).Select(i => $"S{i}").ToArray();
        (await rig.CallAsync("ProposeReviseComposition", Rig.Args(run.Id, "ok", fiftyOne))).TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_records_change_requested_only_when_the_revision_was_created()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();
        var log = new ReelBolt.WorkflowEngine.Tests.Services.Telemetry.RecordingProductEventLog();
        var emitter = new ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter(
            log, Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter>.Instance);
        AssistantRevisionTools tools = rig.Tools(productEvents: emitter);

        await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", null, "stale-token"), tools);
        log.Rows.Should().BeEmpty("a refused apply is not a change request");

        string token = await rig.ProposeTokenAsync(run.Id);
        await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", null, token), tools);

        log.Rows.Should().ContainSingle().Which.Event.Should().Be("product.change_requested");
    }

    [Fact]
    public async Task Apply_queues_the_revision_as_an_assistant_run_and_counts_it_in_the_paid_bucket()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();
        string token = await rig.ProposeTokenAsync(run.Id, scenes: ["Intro"]);

        JsonElement result = await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", ["Intro"], token));

        result.TryGetProperty("error", out _).Should().BeFalse(result.GetRawText());
        Guid newRun = result.GetProperty("executionId").GetGuid();
        result.GetProperty("revisionId").GetGuid().Should().NotBeEmpty();
        result.GetProperty("status").GetString().Should().Be("Queued");
        result.GetProperty("rateLimit").GetProperty("bucket").GetString().Should().Be("paid");
        result.GetProperty("rateLimit").GetProperty("limit").GetInt32().Should().Be(10);
        rig.Db.WorkflowExecutions.Single(e => e.Id == newRun).Initiator.Should().Be(ExecutionInitiator.Assistant);
        RunRateEvent ledger = rig.Db.RunRateEvents.Single();
        ledger.Bucket.Should().Be("paid");
        ledger.Source.Should().Be("revision");
        ledger.ExecutionId.Should().Be(newRun);
    }

    [Fact]
    public async Task Apply_runs_the_lazy_trial_grant_for_the_callers_workspace_before_the_credit_check()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();
        string token = await rig.ProposeTokenAsync(run.Id);
        var provisioner = new Mock<ICreditAccountProvisioner>();
        provisioner.Setup(p => p.EnsureTrialGrantAsync(rig.User, TestTenancy.OrgOf(rig.User), It.IsAny<CancellationToken>()))
            .ReturnsAsync(default(TrialProvisioningOutcome));

        await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", null, token), rig.Tools(trialCredits: provisioner.Object));

        provisioner.Verify(p => p.EnsureTrialGrantAsync(rig.User, TestTenancy.OrgOf(rig.User), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Apply_through_the_mcp_queues_the_run_with_the_mcp_initiator()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();
        string token = await rig.ProposeTokenAsync(run.Id);

        JsonElement result = await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", null, token), initiator: ExecutionInitiator.Mcp);

        rig.Db.WorkflowExecutions.Single(e => e.Id == result.GetProperty("executionId").GetGuid()).Initiator.Should().Be(ExecutionInitiator.Mcp);
    }

    [Fact]
    public async Task A_token_is_bound_to_the_instruction_so_a_changed_instruction_queues_nothing()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();
        string token = await rig.ProposeTokenAsync(run.Id, "Bigger intro title");

        JsonElement result = await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Delete the whole film", null, token));

        result.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        rig.Db.CompositionRevisions.Should().BeEmpty();
        rig.Db.RunRateEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_is_bound_to_the_targets_so_changed_scenes_queue_nothing()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();
        string token = await rig.ProposeTokenAsync(run.Id, scenes: ["Intro"]);

        JsonElement result = await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", ["Outro"], token));

        result.GetProperty("error").GetString().Should().Contain("stale confirmationToken");
        rig.Db.CompositionRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_with_an_unverified_email_is_refused_before_anything_runs()
    {
        using Rig rig = new();
        WorkflowExecution run = rig.FilmRun();
        string token = await rig.ProposeTokenAsync(run.Id);

        JsonElement result = await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", null, token), rig.Tools(emailVerified: false));

        result.GetProperty("code").GetString().Should().Be("EMAIL_NOT_VERIFIED");
        rig.Db.CompositionRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_user_whose_free_bucket_is_full_can_still_revise()
    {
        using Rig rig = new();
        rig.SeedEvents("free", 60);
        WorkflowExecution run = rig.FilmRun();
        string token = await rig.ProposeTokenAsync(run.Id);

        JsonElement result = await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", null, token));

        result.TryGetProperty("error", out _).Should().BeFalse(result.GetRawText());
        rig.Db.CompositionRevisions.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_user_whose_paid_bucket_is_full_is_refused_with_RUN_RATE_LIMITED_and_nothing_is_queued()
    {
        using Rig rig = new();
        rig.SeedEvents("paid", 10);
        WorkflowExecution run = rig.FilmRun();
        string token = await rig.ProposeTokenAsync(run.Id);

        JsonElement result = await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", null, token));

        result.GetProperty("code").GetString().Should().Be("RUN_RATE_LIMITED");
        result.GetProperty("bucket").GetString().Should().Be("paid");
        result.GetProperty("used").GetInt32().Should().Be(10);
        result.GetProperty("limit").GetInt32().Should().Be(10);
        result.GetProperty("retryAfterSeconds").GetInt32().Should().BeGreaterThan(0);
        rig.Db.CompositionRevisions.Should().BeEmpty();
        rig.Db.RunRateEvents.Count(e => e.Source == "revision").Should().Be(0);
    }

    [Fact]
    public async Task The_services_own_revision_rate_limit_is_mapped_with_used_limit_and_retry_after()
    {
        using Rig rig = new(revisionsPerHour: 1);
        WorkflowExecution run = rig.FilmRun();
        string token = await rig.ProposeTokenAsync(run.Id);
        (await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", null, token))).TryGetProperty("revisionId", out _).Should().BeTrue();

        JsonElement second = await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", null, token));

        second.GetProperty("code").GetString().Should().Be("REVISION_RATE_LIMITED");
        second.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        second.GetProperty("retryAfterSeconds").GetInt32().Should().BeGreaterThan(0);
        second.GetProperty("limit").GetInt32().Should().Be(1);
        second.GetProperty("used").GetInt32().Should().Be(1);
        rig.Db.RunRateEvents.Count(e => e.Source == "revision").Should().Be(1, "a refused revision consumes nothing");
    }

    [Fact]
    public async Task Insufficient_credits_is_mapped_with_required_and_balance_and_consumes_no_paid_run()
    {
        using Rig rig = new(estimator: new BrokeEstimator());
        WorkflowExecution run = rig.FilmRun();
        string token = await rig.ProposeTokenAsync(run.Id);

        JsonElement result = await rig.CallAsync("ApplyReviseComposition", Rig.Args(run.Id, "Bigger intro title", null, token));

        result.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        result.TryGetProperty("required", out _).Should().BeTrue(result.GetRawText());
        result.TryGetProperty("balance", out _).Should().BeTrue();
        result.GetProperty("missing").GetDouble().Should().Be(5.5, "the hold is 6 credits and the workspace has 0.5");
        result.GetProperty("error").GetString().Should().Be(
            "This run needs up to 6 credits to start; the workspace has 0.5 available, so 5.5 more are needed. "
            + "Credits that aren't used are returned when the run finishes.");
        rig.Db.RunRateEvents.Should().BeEmpty();
        rig.Db.CompositionRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task Propose_with_billing_on_but_no_evaluable_estimate_says_unavailable_never_billing_off()
    {
        using Rig rig = new(estimator: new ThrowingEstimator());
        WorkflowExecution run = rig.FilmRun();

        JsonElement proposal = await rig.CallAsync("ProposeReviseComposition", Rig.Args(run.Id, "Bigger intro title"));

        JsonElement estimate = proposal.GetProperty("estimate");
        estimate.GetProperty("billingEnabled").GetBoolean().Should().BeTrue();
        estimate.GetProperty("estimateUnavailable").GetBoolean().Should().BeTrue();
        estimate.GetProperty("low").ValueKind.Should().Be(JsonValueKind.Null, "no figure is invented when none could be worked out");
        estimate.GetProperty("required").ValueKind.Should().Be(JsonValueKind.Null);
        estimate.GetProperty("affordable").ValueKind.Should().Be(JsonValueKind.Null);
        proposal.GetProperty("summary").GetString().Should().Contain("cost could not be worked out");
    }

    [Fact]
    public async Task Propose_with_billing_on_shows_the_estimate_range_and_whether_it_is_affordable()
    {
        using Rig rig = new(estimator: new BrokeEstimator());
        WorkflowExecution run = rig.FilmRun();

        JsonElement estimate = (await rig.CallAsync("ProposeReviseComposition", Rig.Args(run.Id, "Bigger intro title"))).GetProperty("estimate");

        estimate.GetProperty("billingEnabled").GetBoolean().Should().BeTrue();
        estimate.GetProperty("high").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public void Captions_cover_both_tools_in_plain_words()
    {
        AssistantRevisionTools.Captions.Keys.Should().BeEquivalentTo("ProposeReviseComposition", "ApplyReviseComposition");
        AssistantRevisionTools.Captions.Values.Should().OnlyContain(c => !c.Contains("Revise") && c.Length > 5);
    }
}
