#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Briefs;

/// <summary>
/// Retry and Resume rebuild the run's brief from its stored copy for the start blockers: every stored field a blocker reads
/// (avatar, video files) must survive, or a run that was started with them is refused for what it already has. Through the
/// real routes, on SQLite (Resume needs the engine's snapshot owner column).
/// </summary>
public sealed class RunBriefRestartLintTests : IDisposable
{
    private const string TalkingHead =
        "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"model\":\"minimax/h3-max/lip-sync/image-to-video\",\"maxSpendCredits\":10,\"allowSourceMediaEgress\":true,\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":1}}";

    private const string OpenVideo = "{\"source\":{\"kind\":\"ProjectFile\"}}";

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;

    public RunBriefRestartLintTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }

        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        using WorkflowEngineDbContext engineDb = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options);
        engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class User(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public bool EmailVerified { get; } = true;
        public string Email { get; } = "user@example.test";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole { get; } = OrganizationRole.Owner;
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

    private WorkflowsController Controller(Guid userId)
    {
        User user = new(userId);
        WorkflowLintGate lint = new(
            new WorkflowLinter(new ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit.FixedFactsSource(UnknownLintFacts.Instance)),
            _db, new ConfigurationBuilder().Build(), NullLogger<WorkflowLintGate>.Instance);
        return new WorkflowsController(
            _db, user, TestTenancy.Access(_db, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(_db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance,
            costEstimator: new WorkflowCostEstimator(
                new SingleContextScopes(_db),
                Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = false }),
                Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
                NullLogger<WorkflowCostEstimator>.Instance),
            lintGate: lint);
    }

    private sealed record Seeded(Guid Owner, Project Project, WorkflowExecution Run);

    /// <summary>A failed run: step 1 (Agent) completed, step 2 (the given type and config) failed; the snapshot is engine-taken.</summary>
    private async Task<Seeded> SeedAsync(StepType secondType, string secondConfig, StoredRunBrief? stored)
    {
        Guid owner = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(owner);
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "AuthorAgent", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "w", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), Name = "w (run snapshot)", ProjectId = project.Id, Kind = WorkflowDefinitionKind.ExecutionSnapshot, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        List<WorkflowStep> steps = [];
        foreach (WorkflowDefinition owning in new[] { workflow, snapshot })
        {
            WorkflowStep one = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = owning.Id, StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent.Id, Label = "Plan" };
            WorkflowStep two = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = owning.Id, StepOrder = 2, StepType = secondType, AgentDefinitionId = agent.Id, Label = "Second" };
            if (secondType == StepType.VideoGenerate)
                two.VideoGenerateConfigJson = secondConfig;
            else
                two.VideoAnalyzeConfigJson = secondConfig;
            owning.Steps.Add(one);
            owning.Steps.Add(two);
            steps.AddRange([one, two]);
        }

        DateTime start = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            SnapshotDefinitionId = snapshot.Id,
            AgentSnapshotJson = "{\"agents\":[]}",
            ProjectId = project.Id,
            OrganizationId = org,
            InitiatedByUserId = owner,
            Status = ExecutionStatus.Failed,
            ErrorMessage = "step 2 failed",
            UserRequest = "make a reel",
            QueuedAt = start.AddMinutes(-1),
            StartedAt = start,
            CompletedAt = start.AddMinutes(5),
            RunBriefJson = stored is null ? null : RunBriefJson.Serialize(stored)
        };
        run.StepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = snapshot.Steps.Single(s => s.StepOrder == 1).Id, Status = StepStatus.Completed, Output = "one", ExecutedAt = start.AddMinutes(1) });
        run.StepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = snapshot.Steps.Single(s => s.StepOrder == 2).Id, Status = StepStatus.Failed, Output = "", ExecutedAt = start.AddMinutes(2) });

        _db.Projects.Add(project);
        _db.AgentDefinitions.Add(agent);
        _db.WorkflowDefinitions.Add(workflow);
        _db.WorkflowDefinitions.Add(snapshot);
        _db.WorkflowExecutions.Add(run);
        await _db.SaveChangesAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_definitions SET snapshot_of_execution_id = {run.Id} WHERE id = {snapshot.Id}");
        _db.ChangeTracker.Clear();
        return new Seeded(owner, project, run);
    }

    private static string? CodeOf(ActionResult<WorkflowExecutionResponse> result) =>
        result.Result is ObjectResult { Value: { } value }
            ? JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null
            : null;

    private async Task<(ActionResult<WorkflowExecutionResponse> Retry, ActionResult<WorkflowExecutionResponse> Resume)> RestartBothAsync(
        StepType type, string config, StoredRunBrief? stored)
    {
        // Each route is tried on its own fresh run: a successful restart changes the row.
        Seeded forRetry = await SeedAsync(type, config, stored);
        ActionResult<WorkflowExecutionResponse> retry = await Controller(forRetry.Owner)
            .RetryExecution(forRetry.Project.Id, forRetry.Run.WorkflowDefinitionId, forRetry.Run.Id, null, CancellationToken.None);
        Seeded forResume = await SeedAsync(type, config, stored);
        ActionResult<WorkflowExecutionResponse> resume = await Controller(forResume.Owner)
            .ResumeExecution(forResume.Project.Id, forResume.Run.WorkflowDefinitionId, forResume.Run.Id, null, CancellationToken.None);
        return (retry, resume);
    }

    [Fact]
    public async Task A_run_started_with_an_avatar_in_its_brief_retries_and_resumes_and_one_without_is_refused_on_both_routes()
    {
        StoredRunBrief withAvatar = new(Avatar: new RunBriefAvatar(Guid.NewGuid()));
        (ActionResult<WorkflowExecutionResponse> retry, ActionResult<WorkflowExecutionResponse> resume) =
            await RestartBothAsync(StepType.VideoGenerate, TalkingHead, withAvatar);
        CodeOf(retry).Should().BeNull("the brief's avatar fills the step, as the engine does");
        CodeOf(resume).Should().BeNull();
        retry.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(200);
        resume.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().BeOneOf(200, 202);

        (retry, resume) = await RestartBothAsync(StepType.VideoGenerate, TalkingHead, new StoredRunBrief(Language: "it"));
        CodeOf(retry).Should().Be("AVATAR_NOT_CHOSEN");
        CodeOf(resume).Should().Be("AVATAR_NOT_CHOSEN");
    }

    [Fact]
    public async Task A_run_started_with_a_video_in_its_brief_retries_and_resumes_and_one_without_is_refused_on_both_routes()
    {
        StoredRunBrief withVideo = new(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Video, [Guid.NewGuid()])]);
        (ActionResult<WorkflowExecutionResponse> retry, ActionResult<WorkflowExecutionResponse> resume) =
            await RestartBothAsync(StepType.VideoAnalyze, OpenVideo, withVideo);
        CodeOf(retry).Should().BeNull();
        CodeOf(resume).Should().BeNull();

        (retry, resume) = await RestartBothAsync(StepType.VideoAnalyze, OpenVideo, new StoredRunBrief(Language: "it"));
        CodeOf(retry).Should().Be("VIDEO_SOURCE_NOT_CHOSEN");
        CodeOf(resume).Should().Be("VIDEO_SOURCE_NOT_CHOSEN");
    }

    [Fact]
    public void Every_stored_brief_field_survives_the_rebuild_for_the_gate()
    {
        Guid avatar = Guid.NewGuid();
        StoredRunBrief stored = new(
            Language: "it", SourceLanguage: "en", LengthSec: 30, DesignSystem: "none", Voice: new RunBriefVoice(ProjectVoiceId: Guid.NewGuid()),
            SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Video, [Guid.NewGuid()])], OverlayCardAudio: "mute",
            Avatar: new RunBriefAvatar(avatar), Inputs: new Dictionary<string, JsonElement> { ["x"] = JsonDocument.Parse("true").RootElement });

        RunBrief rebuilt = WorkflowLintGate.BriefFromStored(RunBriefJson.Serialize(stored))!;

        rebuilt.Language.Should().Be("it");
        rebuilt.SourceLanguage.Should().Be("en");
        rebuilt.LengthSec.Should().Be(30);
        rebuilt.DesignSystem.Should().Be("none");
        rebuilt.Voice.Should().NotBeNull();
        rebuilt.SourceFiles.Should().HaveCount(1);
        rebuilt.OverlayCardAudio.Should().Be("mute");
        rebuilt.Avatar!.AvatarId.Should().Be(avatar);
        rebuilt.Inputs.Should().ContainKey("x");
    }

    // ---- the start blockers judge only what the run will certainly do ----

    private static WorkflowStep Step(int order, StepType type, string? config = null, string? condition = null, string? whenTrue = null, string? whenFalse = null)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = order,
            Label = $"Step {order}",
            StepType = type,
            AgentDefinitionId = Guid.NewGuid(),
            ConditionExpression = condition,
            TrueBranchStepOrder = whenTrue,
            FalseBranchStepOrder = whenFalse
        };
        if (type == StepType.VideoAnalyze)
            step.VideoAnalyzeConfigJson = config;
        if (type == StepType.VideoGenerate)
            step.VideoGenerateConfigJson = config;
        return step;
    }

    private WorkflowLintGate Gate(IWorkflowLintFacts? facts = null, bool enforce = false) => new(
        new WorkflowLinter(new ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit.FixedFactsSource(facts ?? UnknownLintFacts.Instance)),
        _db,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [WorkflowLintGate.EnforceKey] = enforce.ToString() }).Build(),
        NullLogger<WorkflowLintGate>.Instance);

    private static Project AProject() => new() { Id = Guid.NewGuid(), Name = "p", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };

    private static string? CodeOf(ActionResult? refusal) => refusal is null ? null : WorkflowLintGate.CodeOf(refusal);

    [Fact]
    public async Task Video_sources_follow_the_engine_sources_win_over_source_when_set()
    {
        Project project = AProject();
        string both = "{\"source\":{\"kind\":\"ProjectFile\"},\"sources\":[{\"kind\":\"ProjectFile\",\"projectFileId\":\"" + Guid.NewGuid() + "\"}]}";
        (await Gate().CheckAsync(project, Guid.NewGuid(), [Step(1, StepType.VideoAnalyze, both)], null, null, default))
            .Should().BeNull("the engine reads `sources` and ignores the open `source` beside it");
        (await Gate().CheckAsync(project, Guid.NewGuid(), [Step(1, StepType.VideoAnalyze, "{\"source\":{\"kind\":\"ProjectFile\"},\"sources\":[]}")], null, null, default))
            .Should().BeOfType<BadRequestObjectResult>("an empty `sources` falls back to `source`, as the engine does");
    }

    [Fact]
    public async Task A_blocker_refuses_a_straight_workflow_and_a_branch_the_inputs_pick_and_leaves_an_undecided_branch_to_the_engine()
    {
        Project project = AProject();
        Guid wf = Guid.NewGuid();

        (await Gate().CheckAsync(project, wf, [Step(1, StepType.Agent), Step(2, StepType.VideoAnalyze, OpenVideo)], null, null, default))
            .Should().BeOfType<BadRequestObjectResult>("a straight workflow always reaches the step");

        // Step 1 decides, from an app input, between step 2 (a harmless agent) and step 3 (the open video).
        List<WorkflowStep> decided =
        [
            Step(1, StepType.Conditional, condition: "input.go == true", whenTrue: "3", whenFalse: "end"),
            Step(2, StepType.Agent),
            Step(3, StepType.VideoAnalyze, OpenVideo)
        ];
        RunBrief take = new(Inputs: new Dictionary<string, JsonElement> { ["go"] = JsonDocument.Parse("true").RootElement });
        RunBrief skip = new(Inputs: new Dictionary<string, JsonElement> { ["go"] = JsonDocument.Parse("false").RootElement });
        (await Gate().CheckAsync(project, wf, decided, take, null, default)).Should().BeOfType<BadRequestObjectResult>("the inputs send the run there");
        (await Gate().CheckAsync(project, wf, decided, skip, null, default)).Should().BeNull("the inputs send the run elsewhere");

        // The condition reads a step's output: undecided, so the engine decides at run time.
        List<WorkflowStep> undecided =
        [
            Step(1, StepType.Conditional, condition: "$.score > 5", whenTrue: "3", whenFalse: "2"),
            Step(2, StepType.Agent),
            Step(3, StepType.VideoAnalyze, OpenVideo)
        ];
        (await Gate().CheckAsync(project, wf, undecided, null, null, default)).Should().BeNull("the run may never reach that step");
    }

    [Fact]
    public void The_certain_set_is_the_walk_up_to_the_first_undecided_branch()
    {
        List<WorkflowStep> steps =
        [
            Step(1, StepType.Agent),
            Step(2, StepType.Conditional, condition: "$.score > 5", whenTrue: "4", whenFalse: "3"),
            Step(3, StepType.Agent),
            Step(4, StepType.Agent)
        ];
        ReelBolt.Shared.Workflows.Apps.StaticBranchResolver.CertainStepOrders([.. steps.Select(ReelBolt.Shared.Workflows.Apps.AppBindingStep.From)], null)
            .Should().BeEquivalentTo(new[] { 1, 2 });
        ReelBolt.Shared.Workflows.Apps.StaticBranchResolver.CertainStepOrders([.. steps.Where(s => s.StepOrder != 2).Select(ReelBolt.Shared.Workflows.Apps.AppBindingStep.From)], null)
            .Should().BeEquivalentTo(new[] { 1, 3, 4 });
    }

    [Fact]
    public async Task Each_refusal_is_recorded_under_its_own_code()
    {
        Project project = AProject();
        Guid wf = Guid.NewGuid();
        CodeOf(await Gate().CheckAsync(project, wf, [Step(1, StepType.VideoAnalyze, OpenVideo)], null, null, default)).Should().Be("VIDEO_SOURCE_NOT_CHOSEN");
        CodeOf(await Gate().CheckAsync(project, wf, [Step(1, StepType.VideoGenerate, TalkingHead)], null, null, default)).Should().Be("AVATAR_NOT_CHOSEN");

        var facts = new Mock<IWorkflowLintFacts>();
        facts.Setup(f => f.VideoModelSupportedAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        string plain = TalkingHead.Replace(",\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":1}", "");
        CodeOf(await Gate(facts.Object).CheckAsync(project, wf, [Step(1, StepType.VideoGenerate, plain)], null, null, default)).Should().Be("VIDEO_MODEL_UNSUPPORTED");

        WorkflowLintGate.CodeOf(new ConflictObjectResult(new { code = WorkflowLintGate.ErrorsCode })).Should().Be("WORKFLOW_LINT_ERRORS");
        WorkflowLintGate.CodeOf(new ConflictObjectResult(new { message = "no code" })).Should().Be("WORKFLOW_LINT_ERRORS");
    }

    // ---- the real facts: a provider and a model, through the real resolver and client factory ----

    private sealed class Tenant(Guid org) : ReelBolt.Shared.Inference.InferenceTenantAccessorBase
    {
        protected override Guid? AmbientOrganizationId => org;
    }

    private sealed class Passthrough : ReelBolt.Shared.Inference.ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = ciphertext ?? string.Empty;
            return ciphertext != null;
        }
    }

    /// <summary>Serves a row to its own organization only (and platform rows, OrganizationId null, to everyone).</summary>
    private sealed class OrgStore(IReadOnlyList<ReelBolt.Shared.Inference.ModelCapabilityView> rows) : ReelBolt.Shared.Inference.IInferenceProviderStore
    {
        public Task<IReadOnlyList<ReelBolt.Shared.Inference.ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReelBolt.Shared.Inference.ModelCapabilityView>>([.. rows.Where(r => r.OrganizationId is null || r.OrganizationId == organizationId)]);

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlySet<Guid>> LoadUnpricedCapabilityIdsAsync(DateTime at, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    }

    private static ReelBolt.Shared.Inference.ModelCapabilityView FalRow(
        Guid? org, bool isDefault, InferenceProviderCapability capability = InferenceProviderCapability.VideoGeneration, string? model = null) => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            Name = "fal",
            Kind = InferenceProviderKind.FalAi,
            Capability = capability,
            Endpoint = "https://queue.fal.run",
            ModelName = model ?? ReelBolt.Shared.Inference.FalAiVideoGenerationClient.Catalog.DefaultFor(ReelBolt.Shared.Inference.FalVideoJob.ImageToVideo).Id,
            ApiKeyEncrypted = "key",
            IsDefault = isDefault,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private DbWorkflowLintFacts RealFacts(Guid org, params ReelBolt.Shared.Inference.ModelCapabilityView[] rows)
    {
        ServiceCollection services = new();
        services.AddSingleton<ReelBolt.Shared.Inference.IInferenceProviderStore>(new OrgStore(rows));
        ServiceProvider sp = services.BuildServiceProvider();
        ReelBolt.Shared.Inference.InferenceProviderResolver resolver = new(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            new Passthrough(),
            NullLogger<ReelBolt.Shared.Inference.InferenceProviderResolver>.Instance,
            new Tenant(org));
        return new DbWorkflowLintFacts(
            _db, resolver, Mock.Of<ReelBolt.Shared.Tenancy.IOrganizationEntitlements>(), org, Guid.NewGuid(),
            new ReelBolt.Shared.Inference.VideoGenerationClientFactory());
    }

    [Fact]
    public async Task The_real_facts_answer_supported_unsupported_and_unknown_for_a_foreign_org_provider()
    {
        Guid org = Guid.NewGuid();
        ReelBolt.Shared.Inference.ModelCapabilityView mine = FalRow(org, isDefault: true);
        string supported = mine.ModelName;

        (await RealFacts(org, mine).VideoModelSupportedAsync(mine.Id, supported, default)).Should().BeTrue();
        (await RealFacts(org, mine).VideoModelSupportedAsync(mine.Id, "not-a-fal-endpoint/x", default)).Should().BeFalse();

        // A provider of another organization is not visible: the answer is unknown, never a false finding or a leak.
        ReelBolt.Shared.Inference.ModelCapabilityView foreign = FalRow(Guid.NewGuid(), isDefault: false);
        (await RealFacts(org, foreign).VideoModelSupportedAsync(foreign.Id, "not-a-fal-endpoint/x", default)).Should().BeNull();
    }

    [Fact]
    public async Task The_public_api_start_path_refuses_an_unsupported_model_the_same_way()
    {
        // The public API start goes through the same RunStartGate; its only input is the same gate result.
        Project project = AProject();
        var facts = new Mock<IWorkflowLintFacts>();
        facts.Setup(f => f.VideoModelSupportedAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        string plain = TalkingHead.Replace(",\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":1}", "");
        ActionResult? refused = await Gate(facts.Object).CheckAsync(project, Guid.NewGuid(), [Step(1, StepType.VideoGenerate, plain)], null, null, default);
        refused.Should().BeOfType<BadRequestObjectResult>();
        JsonSerializer.Serialize(((ObjectResult)refused!).Value).Should().Contain("VIDEO_MODEL_UNSUPPORTED");
    }

    [Fact]
    public async Task A_talking_head_with_no_lip_sync_model_set_up_is_refused_at_start_and_says_so_once()
    {
        Project project = AProject();
        var brief = new RunBrief(Avatar: new RunBriefAvatar(Guid.NewGuid()));
        var facts = new Mock<IWorkflowLintFacts>();
        facts.Setup(f => f.ProviderAvailableAsync(InferenceProviderCapability.LipSync, null, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        ActionResult? refused = await Gate(facts.Object).CheckAsync(project, Guid.NewGuid(), [Step(1, StepType.VideoGenerate, TalkingHead)], brief, null, default);

        refused.Should().BeOfType<BadRequestObjectResult>();
        CodeOf(refused).Should().Be("LIP_SYNC_MODEL_NOT_SET");
        string json = JsonSerializer.Serialize(((ObjectResult)refused!).Value);
        json.Should().Contain("no lip-sync model is set up");
        json.Should().NotContain("no provider is set as the default", "the generic WL006 sentence is replaced, not repeated");

        // An unknown answer is never a finding; and a plain video step is not asked about lip-sync at all.
        facts.Setup(f => f.ProviderAvailableAsync(InferenceProviderCapability.LipSync, null, It.IsAny<CancellationToken>())).ReturnsAsync((bool?)null);
        (await Gate(facts.Object).CheckAsync(project, Guid.NewGuid(), [Step(1, StepType.VideoGenerate, TalkingHead)], brief, null, default)).Should().BeNull();
        string plain = TalkingHead.Replace(",\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":1}", "");
        facts.Setup(f => f.ProviderAvailableAsync(InferenceProviderCapability.LipSync, null, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        (await Gate(facts.Object).CheckAsync(project, Guid.NewGuid(), [Step(1, StepType.VideoGenerate, plain)], brief, null, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_talking_head_step_with_its_own_provider_has_its_model_judged_by_the_LipSync_resolver()
    {
        Project project = AProject();
        var brief = new RunBrief(Avatar: new RunBriefAvatar(Guid.NewGuid()));
        var facts = new Mock<IWorkflowLintFacts>();
        facts.Setup(f => f.LipSyncStepAcceptedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        string bound = TalkingHead.Replace("\"maxSpendCredits\"", $"\"providerId\":\"{Guid.NewGuid()}\",\"maxSpendCredits\"");

        ActionResult? refused = await Gate(facts.Object).CheckAsync(project, Guid.NewGuid(), [Step(1, StepType.VideoGenerate, bound)], brief, null, default);
        CodeOf(refused).Should().Be("VIDEO_MODEL_UNSUPPORTED");
        facts.Verify(f => f.VideoModelSupportedAsync(It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // The same step judged acceptable by the LipSync resolver passes, bound or not.
        facts.Setup(f => f.LipSyncStepAcceptedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        (await Gate(facts.Object).CheckAsync(project, Guid.NewGuid(), [Step(1, StepType.VideoGenerate, TalkingHead)], brief, null, default)).Should().BeNull();
    }

    [Fact]
    public async Task The_real_facts_resolve_a_lip_sync_step_through_LipSync_rows_only()
    {
        Guid org = Guid.NewGuid();
        const string talkingHead = "minimax/h3-max/lip-sync/image-to-video";
        ReelBolt.Shared.Inference.ModelCapabilityView lipSync = FalRow(org, isDefault: true, InferenceProviderCapability.LipSync, talkingHead);
        ReelBolt.Shared.Inference.ModelCapabilityView video = FalRow(org, isDefault: true);

        string Json(Guid? provider, string model, string resolution) =>
            "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"model\":\"" + model + "\",\"resolution\":\"" + resolution + "\",\"ratio\":\"9:16\",\"durationSeconds\":8,"
            + (provider is { } id ? "\"providerId\":\"" + id + "\"," : "") + "\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":1}}";
        const string kling = "fal-ai/kling-video/ai-avatar/v2/standard";
        ReelBolt.Shared.Inference.ModelCapabilityView klingRow = FalRow(org, isDefault: true, InferenceProviderCapability.LipSync, kling);

        // Bound row and a resolution its model offers: fine. A bound row whose model does not offer the step's resolution: refused.
        (await RealFacts(org, lipSync, video).LipSyncStepAcceptedAsync(Json(lipSync.Id, talkingHead, "768P"), default)).Should().BeTrue();
        (await RealFacts(org, klingRow, video).LipSyncStepAcceptedAsync(Json(klingRow.Id, kling, "768P"), default)).Should().BeFalse();
        // No provider on the step: the default row's own model and its own resolution are what runs, so the template's 768P passes.
        (await RealFacts(org, klingRow, video).LipSyncStepAcceptedAsync(Json(null, talkingHead, "768P"), default)).Should().BeTrue();
        (await RealFacts(org, video).LipSyncStepAcceptedAsync(Json(null, talkingHead, "768P"), default)).Should().BeNull("no LipSync row: nothing resolves, and the VideoGeneration default is never used");
        (await RealFacts(org, video).ProviderAvailableAsync(InferenceProviderCapability.LipSync, null, default)).Should().BeFalse();
        (await RealFacts(org, lipSync, video).ProviderAvailableAsync(InferenceProviderCapability.LipSync, null, default)).Should().BeTrue();
    }
}
