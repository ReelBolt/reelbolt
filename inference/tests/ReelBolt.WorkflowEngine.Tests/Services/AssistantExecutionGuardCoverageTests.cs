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
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.Shared.Workflows;
using Xunit;
using ReelBolt.Inference.Api.Services.Styles;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// The WIRING from the assistant's <c>ExecuteWorkflow</c> tool to
/// <see cref="IAssistantExecutionGuard"/>, not the guard's own arithmetic
/// (<see cref="AssistantExecutionGuardTests"/> covers that).
///
/// <para>The tool surface grew from the original seven tools to the full platform surface composed
/// by <see cref="AssistantToolProvider"/>; the binding constraint on that refactor is that the
/// assistant-initiated execution rate limit still covers every execution the model can start. So
/// these tests drive the <b>real</b> <c>ExecuteWorkflow</c> <see cref="AIFunction"/> the model would
/// call — obtained from <see cref="AssistantToolProvider.GetTools"/>, never a private method by
/// reflection — against a recording guard stub, and assert the two properties that make the limit
/// real: the tool asks the guard exactly once, for this call's user and this call's project; and
/// <b>nothing executes unless the guard itself ran the insert callback it was handed</b>.</para>
///
/// <para>A refactor that quietly drops the <c>TryAcquireAsync</c> call, or that starts calling the
/// execution service directly and then "asks" the guard after the fact, fails here: the denied case
/// asserts zero executions and zero persisted rows, and the granted case asserts the row exists only
/// because the guard chose to run the callback.</para>
/// </summary>
public class AssistantExecutionGuardCoverageTests
{
    /// <summary>The exact sentence the model reads when the paid rolling-hour limit is hit (10 of 10, 12 minutes left).</summary>
    private const string RateLimitError = "run limit reached — 10 of 10 paid runs in the last hour on this project; try again in 12 minutes";

    private static readonly Guid WorkflowId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private static InferenceApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Project SeedProject(InferenceApiDbContext db, Guid ownerId)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Launch video",
            Description = "seeded",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    /// <summary>
    /// Builds the provider exactly as the assistant transport does — the four platform surfaces and
    /// the guard all wired — so that <see cref="AssistantToolProvider.GetTools"/> returns the real
    /// registered tool set and <c>ExecuteWorkflow</c> is the real delegate the model invokes.
    /// </summary>
    private static AssistantToolProvider CreateProvider(
        InferenceApiDbContext db,
        Guid userId,
        IAssistantExecutionGuard guard,
        IWorkflowExecutionService executions,
        ReelBolt.Inference.Api.Services.Billing.IWorkflowCostEstimator? costEstimator = null)
    {
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
        ICurrentUser currentUser = new FakeCurrentUser(userId);

        // Auto-template provisioning with the starter disabled: the seeded rows are the only rows.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowTemplates:EnableAutoStarter"] = "false" })
            .Build();
        var provisioning = new WorkflowTemplateProvisioningService(
            db, NullLogger<WorkflowTemplateProvisioningService>.Instance, configuration);

        return new AssistantToolProvider(
            db,
            new WorkflowEditDiffService(db),
            executions,
            guard,
            currentUser,
            new AssistantProjectTools(db, tokens, provisioning, currentUser, TestTenancy.Access(db, currentUser)),
            new AssistantFileTools(db, tokens, new FakeFileStorage(), Mock.Of<IPublishEndpoint>(), new RecordingSummarizationQueue(), TestTenancy.Access(db, currentUser)),
            new AssistantWorkflowTools(db, tokens, TestTenancy.Access(db, currentUser)),
            new AssistantAdminTools(db, tokens, currentUser, Mock.Of<ISecretProtector>()),
            new AssistantKnowledgeTools(db, new FakePlatformDocsSearch(), provisioning),
            new AssistantStyleTools(db, tokens, TestTenancy.Access(db, currentUser)),
            AssistantToolTestFactory.DesignSystemTools(db, currentUser, tokens),
            AssistantToolTestFactory.PictureTools(db, currentUser, tokens),
            AssistantToolTestFactory.P0Tools(db, currentUser, tokens).Outputs,
            AssistantToolTestFactory.P0Tools(db, currentUser, tokens).Uploads,
            AssistantToolTestFactory.P0Tools(db, currentUser, tokens).Runs,
            AssistantToolTestFactory.P0Tools(db, currentUser, tokens).Revisions,
            AssistantToolTestFactory.P0Tools(db, currentUser, tokens).ChangeRequests,
            AssistantToolTestFactory.P2Tools(db, currentUser, tokens).Patch,
            AssistantToolTestFactory.P2Tools(db, currentUser, tokens).PictureDocuments,
            AssistantToolTestFactory.P2Tools(db, currentUser, tokens).GreenScreen,
            TestTenancy.Access(db, currentUser),
            null,
            null,
            // B7: the execute preflight the ExecuteWorkflow tool runs. A null estimator here is the
            // same "nothing wired" state a host without billing has, so the preflight refuses nothing.
            new ReelBolt.Inference.Api.Services.Billing.WorkflowExecutionPreflight(db, costEstimator));
    }

    /// <summary>Invokes one registered tool BY NAME, exactly as the model's tool call would.</summary>
    private static async Task<string> InvokeToolAsync(
        AssistantToolProvider provider,
        AssistantToolContext context,
        string toolName,
        params (string Key, object? Value)[] arguments)
    {
        AIFunction tool = provider.GetTools(context).Single(t => t.Name == toolName);

        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in arguments)
            args[key] = value;

        object? raw = await tool.InvokeAsync(args);

        return raw switch
        {
            null => string.Empty,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()!,
            JsonElement element => element.GetRawText(),
            _ => raw.ToString()!
        };
    }

    private static string ErrorOf(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    /// <summary>
    /// A recording <see cref="IAssistantExecutionGuard"/> stub. On grant it runs the callback it was
    /// handed, which is the only thing that may cause an execution; on deny it returns without ever
    /// touching it — so "the guard owns the insert" is observable rather than assumed.
    /// </summary>
    private sealed class RecordingExecutionGuard(bool grant) : IAssistantExecutionGuard
    {
        public List<(Guid UserId, Guid ProjectId)> Calls { get; } = new();

        public int CallbackInvocations { get; private set; }

        public List<(RunRateBucket Bucket, string Source)> Acquisitions { get; } = new();

        public async Task<RunRateDecision> TryAcquireAsync(
            Guid userId,
            Guid projectId,
            RunRateBucket bucket,
            string source,
            Func<Task<Guid?>> insertCallback,
            CancellationToken ct)
        {
            Calls.Add((userId, projectId));
            Acquisitions.Add((bucket, source));

            if (!grant)
                return new RunRateDecision(false, RunRateBucket.Paid, 10, 10, 3600, 720);

            CallbackInvocations++;
            await insertCallback();
            return new RunRateDecision(true, bucket, 1, 10, 3600, 0);
        }

        /// <summary>Runs inside the classify-under-lock overload, before it classifies (a concurrent edit landing under the lock).</summary>
        public Func<Task>? BeforeClassify { get; init; }

        public List<RunRateBucket> ClassifiedUnderGuard { get; } = new();

        public async Task<RunRateDecision> TryAcquireAsync(
            Guid userId,
            Guid projectId,
            Func<Task<RunRateBucket>> classify,
            string source,
            Func<Task<Guid?>> insertCallback,
            CancellationToken ct)
        {
            if (BeforeClassify is { } before)
                await before();
            RunRateBucket bucket = await classify();
            ClassifiedUnderGuard.Add(bucket);
            return await TryAcquireAsync(userId, projectId, bucket, source, insertCallback, ct);
        }
    }

    /// <summary>
    /// A recording <see cref="IWorkflowExecutionService"/> stub. It is the only thing in this test
    /// that can insert a <see cref="WorkflowExecution"/> row, and it inserts exactly one per call.
    /// </summary>
    private sealed class RecordingExecutionService(InferenceApiDbContext db) : IWorkflowExecutionService
    {

        // Only system-workflow owners (the editor's render endpoint) queue by kind; no execute path
        // under test here does.
        public Task<WorkflowExecutionResponse> QueueAsync(
            Guid projectId,
            Guid workflowId,
            Guid initiatedByUserId,
            string? userRequest,
            ExecutionInitiator initiator,
            CancellationToken ct,
            bool regenerateVideoClips,
            RunChoices? runChoices,
            WorkflowDefinitionKind kind,
            Guid? executionId,
            Func<CancellationToken, Task>? beforePublish = null)
            => throw new NotSupportedException("The execute paths under test never queue by workflow kind.");
        public List<(Guid ProjectId, Guid WorkflowId, Guid InitiatedByUserId, string? UserRequest, ExecutionInitiator Initiator)> Calls { get; } = new();

        /// <summary>The style/format choices the most recent call carried.</summary>
        public RunChoices? LastRunChoices { get; private set; }

        public async Task<WorkflowExecutionResponse> ExecuteAsync(
            Guid projectId,
            Guid workflowId,
            Guid initiatedByUserId,
            string? userRequest,
            ExecutionInitiator initiator,
            CancellationToken ct,
            bool regenerateVideoClips = false,
            RunChoices? runChoices = null)
        {
            LastRunChoices = runChoices;
            Calls.Add((projectId, workflowId, initiatedByUserId, userRequest, initiator));

            var execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                ProjectId = projectId,
                InitiatedByUserId = initiatedByUserId,
                Initiator = initiator,
                Status = ExecutionStatus.Queued,
                StartedAt = DateTime.UtcNow,
                UserRequest = userRequest
            };
            db.WorkflowExecutions.Add(execution);
            await db.SaveChangesAsync(ct);

            return new WorkflowExecutionResponse(
                execution.Id,
                workflowId,
                execution.Status.ToString(),
                execution.StartedAt,
                null,
                0,
                null,
                execution.CorrelationId,
                null,
                new List<StepResultResponse>(),
                new List<ReviewScoreResponse>(),
                userRequest);
        }
    }

    // ---------------------------------------------------------------------
    // T-012: the workflow is classified INSIDE the guard, from the steps as they are under the lock
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ExecuteWorkflow_classifies_the_steps_as_they_are_inside_the_guard()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId);
        db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = WorkflowId, StepOrder = 1, StepType = StepType.Extract });
        db.SaveChanges();
        var guard = new RecordingExecutionGuard(grant: true)
        {
            // Free when the tool was called; a paid step lands before the guard classifies.
            BeforeClassify = async () =>
            {
                db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = WorkflowId, StepOrder = 2, StepType = StepType.Agent });
                await db.SaveChangesAsync();
            }
        };
        AssistantToolProvider provider = CreateProvider(db, userId, guard, new RecordingExecutionService(db));

        string text = await InvokeToolAsync(
            provider,
            new AssistantToolContext(userId, project.Id),
            "ExecuteWorkflow",
            ("workflowId", WorkflowId),
            ("userRequest", null),
            ("regenerateVideoClips", false));

        guard.ClassifiedUnderGuard.Should().Equal(RunRateBucket.Paid);
        guard.Acquisitions.Should().ContainSingle().Which.Bucket.Should().Be(RunRateBucket.Paid);
        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("rateLimit").GetProperty("bucket").GetString().Should().Be("paid");
    }

    // ---------------------------------------------------------------------
    // Acceptance: the guard is consulted exactly once, with THIS call's user and project
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ExecuteWorkflow_acquires_a_guard_slot_for_the_context_user_and_the_context_project()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId);
        var guard = new RecordingExecutionGuard(grant: true);
        var executions = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateProvider(db, userId, guard, executions);

        // The user request travels with the call and must reach the execution service unchanged.
        string text = await InvokeToolAsync(
            provider,
            new AssistantToolContext(userId, project.Id),
            "ExecuteWorkflow",
            ("workflowId", WorkflowId),
            ("userRequest", "cut it to thirty seconds"),
            ("regenerateVideoClips", null));

        // Exactly one slot attempt, for the caller the context names and the project the context
        // carries — never a user id or project id taken from the model's own arguments.
        guard.Calls.Should().ContainSingle("the tool must consult the guard once per execution");
        guard.Calls[0].UserId.Should().Be(userId);
        guard.Calls[0].ProjectId.Should().Be(project.Id);

        // The guard ran the callback it was handed...
        guard.CallbackInvocations.Should().Be(1);

        // ...which is what produced the execution, once, with the assistant initiator recorded.
        executions.Calls.Should().ContainSingle();
        executions.Calls[0].ProjectId.Should().Be(project.Id);
        executions.Calls[0].WorkflowId.Should().Be(WorkflowId);
        executions.Calls[0].InitiatedByUserId.Should().Be(userId);
        executions.Calls[0].UserRequest.Should().Be("cut it to thirty seconds");
        executions.Calls[0].Initiator.Should().Be(ExecutionInitiator.Assistant);

        WorkflowExecution persisted = db.WorkflowExecutions.Should().ContainSingle().Subject;
        persisted.ProjectId.Should().Be(project.Id);
        persisted.WorkflowDefinitionId.Should().Be(WorkflowId);
        persisted.InitiatedByUserId.Should().Be(userId);
        persisted.Initiator.Should().Be(ExecutionInitiator.Assistant);
        persisted.UserRequest.Should().Be("cut it to thirty seconds");

        // The tool answers with a compact receipt for the execution it created: ids and names, the
        // rate-limit position, and NOT the request the model just sent.
        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("executionId").GetGuid().Should().Be(persisted.Id);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Queued");
        doc.RootElement.GetProperty("projectName").GetString().Should().Be("Launch video");
        doc.RootElement.GetProperty("rateLimit").GetProperty("bucket").GetString().Should().Be("paid", "a workflow with no steps cannot be proven free (T-012)");
        doc.RootElement.GetProperty("rateLimit").GetProperty("used").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("rateLimit").GetProperty("limit").GetInt32().Should().Be(10);
        text.Should().NotContain("thirty seconds", "ExecuteWorkflow no longer echoes its input");
        foreach (string echoed in new[] { "userRequest", "UserRequest", "resultJson", "ResultJson", "stepResults", "StepResults", "reviewScores", "ReviewScores" })
            doc.RootElement.TryGetProperty(echoed, out _).Should().BeFalse($"{echoed} is not part of the receipt");
    }

    // ---------------------------------------------------------------------
    // Acceptance: a denied acquire executes nothing and answers with a JSON error
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ExecuteWorkflow_returns_a_json_rate_limit_error_and_executes_nothing_when_the_guard_denies()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId);
        var guard = new RecordingExecutionGuard(grant: false);
        var executions = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateProvider(db, userId, guard, executions);

        string text = await InvokeToolAsync(
            provider,
            new AssistantToolContext(userId, project.Id),
            "ExecuteWorkflow",
            ("workflowId", WorkflowId),
            ("userRequest", "cut it to thirty seconds"),
            ("regenerateVideoClips", null));

        // The guard was asked, for these ids, and refused.
        guard.Calls.Should().ContainSingle();
        guard.Calls[0].UserId.Should().Be(userId);
        guard.Calls[0].ProjectId.Should().Be(project.Id);
        guard.CallbackInvocations.Should().Be(0, "a denied acquire must not run the insert callback");

        // A JSON error the model can read and act on, never an exception and never a bare string.
        ErrorOf(text).Should().Be(RateLimitError);
        using (JsonDocument refusal = JsonDocument.Parse(text))
        {
            refusal.RootElement.GetProperty("code").GetString().Should().Be("RUN_RATE_LIMITED");
            refusal.RootElement.GetProperty("bucket").GetString().Should().Be("paid");
            refusal.RootElement.GetProperty("used").GetInt32().Should().Be(10);
            refusal.RootElement.GetProperty("limit").GetInt32().Should().Be(10);
            refusal.RootElement.GetProperty("windowMinutes").GetInt32().Should().Be(60);
            refusal.RootElement.GetProperty("retryAfterSeconds").GetInt32().Should().Be(720);
        }

        // Nothing executed and nothing was persisted: the rate limit is not advisory.
        executions.Calls.Should().BeEmpty("a denied acquire must not reach the execution service");
        db.WorkflowExecutions.Should().BeEmpty("a denied acquire must leave no execution row behind");
    }

    // ---------------------------------------------------------------------
    // Editing styles: a run's style and format reach the execution service, resolved server-side
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ExecuteWorkflow_with_a_preset_and_a_format_hands_the_resolved_choices_to_the_execution()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId);
        var guard = new RecordingExecutionGuard(grant: true);
        var executions = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateProvider(db, userId, guard, executions);

        await InvokeToolAsync(
            provider,
            new AssistantToolContext(userId, project.Id),
            "ExecuteWorkflow",
            ("workflowId", WorkflowId),
            ("userRequest", null),
            ("regenerateVideoClips", null),
            ("stylePresetKey", "fast-social-reel"),
            ("outputFormat", "9:16"));

        executions.Calls.Should().ContainSingle();
        executions.LastRunChoices.Should().NotBeNull();
        executions.LastRunChoices!.Style!.PresetKey.Should().Be("fast-social-reel");
        executions.LastRunChoices.OutputFormat.Should().Be(ReelBolt.Shared.Styles.RunOutputFormat.Vertical);
    }

    [Fact]
    public async Task ExecuteWorkflow_with_an_unknown_format_or_a_stranger_s_style_executes_nothing()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        Project project = SeedProject(db, userId);
        var stranger = new EditingStyle
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            Name = "Theirs",
            ConfigJson = "{\"pacing\":\"Fast\"}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.EditingStyles.Add(stranger);
        db.SaveChanges();

        var guard = new RecordingExecutionGuard(grant: true);
        var executions = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateProvider(db, userId, guard, executions);
        var context = new AssistantToolContext(userId, project.Id);

        string badFormat = await InvokeToolAsync(provider, context, "ExecuteWorkflow",
            ("workflowId", WorkflowId), ("userRequest", null), ("regenerateVideoClips", null), ("outputFormat", "21:9"));
        string foreignStyle = await InvokeToolAsync(provider, context, "ExecuteWorkflow",
            ("workflowId", WorkflowId), ("userRequest", null), ("regenerateVideoClips", null), ("styleId", stranger.Id));

        ErrorOf(badFormat).Should().Contain("Unknown output format");
        ErrorOf(foreignStyle).Should().Be("Style not found.");
        guard.Calls.Should().BeEmpty("a bad choice is reported before a rate-limit slot is spent");
        executions.Calls.Should().BeEmpty();
    }
}
