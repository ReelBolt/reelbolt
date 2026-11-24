using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Tests.Pricing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>
/// The request side of a replaying revision (Inference API): the plan and the per-revision snapshot
/// workflow, the brief and style carried over, the preflight over exactly what will hold credits, and a
/// version linked to its run's FINAL video rather than only the animation render.
/// </summary>
public class CompositionRevisionReplayServiceTests
{
    private const string Brief = "A two-minute vertical reel";

    /// <summary>
    /// Records the steps it was asked to price; refuses when told to. The charged preflights whose 0-based
    /// call index is in <paramref name="notEvaluatedCharged"/> answer Allowed with their real estimate but
    /// Evaluated:false (the balance could not be read).
    /// </summary>
    private sealed class RecordingEstimator(bool refuse = false, params int[] notEvaluatedCharged) : IWorkflowCostEstimator
    {
        private int _chargedCalls;
        public List<IReadOnlyList<WorkflowStep>> Preflights { get; } = [];
        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(Estimate(steps));

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
        {
            Preflights.Add(steps);
            return Task.FromResult(new WorkflowPreflightResult(
                refuse ? WorkflowPreflightOutcome.InsufficientCredits : WorkflowPreflightOutcome.Allowed, Estimate(steps), 50_000));
        }

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            EstimateAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);

        public async Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
        {
            WorkflowPreflightResult result = await PreflightAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);
            return notEvaluatedCharged.Contains(_chargedCalls++) ? result with { Evaluated = false } : result;
        }

        private static WorkflowCostEstimate Estimate(IReadOnlyList<WorkflowStep> steps) =>
            new(true, false, steps.Count * 1_000, steps.Count * 3_000, steps.Count * 3_000, []);
    }

    private sealed class Fixture : IDisposable
    {
        public InferenceApiDbContext Db { get; }
        public Mock<IPublishEndpoint> Bus { get; } = new();
        public Project Project { get; }
        public Guid User { get; } = Guid.NewGuid();
        public Guid ReviserId { get; } = Guid.NewGuid();
        public CompositionRevisionService Service { get; }
        public List<WorkflowStep> Steps { get; } = ReplayShapes.AnimatedFilmEditRoomCut();
        public WorkflowDefinition Workflow { get; }
        public WorkflowExecution Run { get; }
        public Dictionary<int, WorkflowStepResult> Results { get; }
        public CompositionWorkspaceVersion V1 { get; }

        public Fixture(IWorkflowCostEstimator? estimator = null, IPlanGateService? planGate = null)
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase("replay-svc-" + Guid.NewGuid().ToString("N")).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = User, OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);
            Db.AgentDefinitions.Add(new AgentDefinition { Id = ReviserId, Name = "CompositionReviser", AgentType = AgentType.CompositionReviser, IsBuiltIn = true });
            Db.AgentDefinitions.AddRange(Steps.Select(s => s.AgentDefinition));

            Workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Animated film + edit room cut", ProjectId = Project.Id };
            foreach (WorkflowStep step in Steps)
                step.WorkflowDefinitionId = Workflow.Id;
            Db.WorkflowDefinitions.Add(Workflow);
            Db.WorkflowSteps.AddRange(Steps);

            Run = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Workflow.Id,
                ProjectId = Project.Id,
                OrganizationId = Project.OrganizationId,
                Status = ExecutionStatus.Passed,
                UserRequest = Brief,
                StartedAt = DateTime.UtcNow.AddHours(-1),
                CompletedAt = DateTime.UtcNow,
                StyleConfigJson = "{\"name\":\"Bold\",\"config\":{}}",
                OutputFormatOverride = RunOutputFormat.Vertical,
                RunBriefJson = "{\"version\":1,\"language\":\"it\",\"lengthSec\":45}"
            };
            Db.WorkflowExecutions.Add(Run);
            string render = $"projects/{Project.Id}/outputFiles/{Run.Id}/film.mp4";
            Results = Steps.ToDictionary(s => s.StepOrder, s => new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = Run.Id,
                WorkflowStepId = s.Id,
                Status = StepStatus.Completed,
                ExecutedAt = DateTime.UtcNow.AddMinutes(-60 + s.StepOrder),
                Output = "{}",
                OutputStorageKey = s.StepOrder switch
                {
                    4 => render,
                    7 => $"projects/{Project.Id}/outputFiles/final-cut.mp4",
                    _ => null
                }
            });
            Db.WorkflowStepResults.AddRange(Results.Values);

            CompositionWorkspace workspace = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OrganizationId = Project.OrganizationId,
                OriginExecutionId = Run.Id,
                OriginWorkflowDefinitionId = Workflow.Id,
                LatestVersionNumber = 1
            };
            V1 = new CompositionWorkspaceVersion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                ProjectId = Project.Id,
                VersionNumber = 1,
                StorageKey = CompositionWorkspaceKeys.ArchiveKey(Project.Id, workspace.Id, Guid.NewGuid()),
                Sha256 = new string('a', 64),
                CreatedByExecutionId = Run.Id,
                CompositionId = "FinalVideo",
                Fps = 30,
                RenderOutputStorageKey = render
            };
            Db.CompositionWorkspaces.Add(workspace);
            Db.CompositionWorkspaceVersions.Add(V1);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();

            Service = new CompositionRevisionService(
                Db,
                new WorkflowExecutionService(Db, Bus.Object, NullLogger<WorkflowExecutionService>.Instance),
                new ConfigurationBuilder().Build(),
                NullLogger<CompositionRevisionService>.Instance,
                estimator,
                planGate: planGate);
        }

        public Task<CompositionRevisionRequestResult> Request(string? cutPolicy = null) =>
            Service.RequestAsync(Project, Run.Id, User, Project.OrganizationId,
                new CreateCompositionRevisionRequest("More graphics, same timing", CutPolicy: cutPolicy), CancellationToken.None);

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task The_original_version_plays_its_final_cut_and_the_render_is_secondary()
    {
        using Fixture f = new();

        CompositionRevisionHistoryResponse history = (await f.Service.GetHistoryAsync(f.Project, f.Run.Id, null, CancellationToken.None))!;

        CompositionVersionResponse v1 = history.Versions.Single();
        v1.RenderStepResultId.Should().Be(f.Results[4].Id);
        v1.Deliverables.Should().ContainSingle().Which.Should().Be(new RevisionDeliverableResponse(f.Results[7].Id, 7, "7. VideoTransform", true));
        v1.FinalVideoExpected.Should().BeTrue();
        v1.RunStatus.Should().Be("Passed");
    }

    [Fact]
    public async Task The_history_describes_the_replay_under_each_cut_choice()
    {
        using Fixture f = new(new RecordingEstimator());

        RevisionReplayResponse replay = (await f.Service.GetHistoryAsync(f.Project, f.Run.Id, f.Project.OrganizationId, CancellationToken.None))!.Replay!;

        replay.Available.Should().BeTrue();
        replay.HasEditDecisions.Should().BeTrue();
        replay.Steps.Select(s => (s.StepOrder, s.KeepCut, s.ReEdit)).Should().Equal(
            (5, "Run", "Run"), (6, "KeepIfCompatible", "Run"), (7, "Run", "Run"), (8, "Run", "Run"));
        replay.KeepCutEstimate!.LowCredits.Should().BeLessThan(replay.KeepCutEstimate.HighCredits,
            "keeping the cut is cheaper unless the edit has to be re-made");
        replay.ReEditEstimate!.RequiredCredits.Should().Be(replay.KeepCutEstimate.RequiredCredits,
            "a kept decision may still be re-made, so both must be able to hold it");
    }

    [Fact]
    public async Task A_keep_cut_hold_that_was_not_evaluated_shows_no_replay_figures()
    {
        // The hold preflight is Allowed with a real, non-zero estimate, but the balance could not be read.
        RecordingEstimator estimator = new(false, 0);
        using Fixture f = new(estimator);

        CompositionRevisionHistoryResponse history = (await f.Service.GetHistoryAsync(f.Project, f.Run.Id, f.Project.OrganizationId, CancellationToken.None))!;

        estimator.Preflights.Should().NotBeEmpty().And.OnlyContain(p => p.Count > 0, "the stub priced real steps");
        RevisionReplayResponse replay = history.Replay!;
        replay.Available.Should().BeTrue();
        replay.KeepCutEstimate.Should().BeNull("a balance that was never read is not a balance of 0");
        replay.ReEditEstimate.Should().BeNull();
    }

    [Fact]
    public async Task A_re_edit_preflight_that_was_not_evaluated_shows_no_replay_figures_even_when_the_hold_was()
    {
        // The keep-cut hold is evaluated; the full (re-edit) preflight is Allowed with a real estimate but not evaluated.
        RecordingEstimator estimator = new(false, 1);
        using Fixture f = new(estimator);

        CompositionRevisionHistoryResponse history = (await f.Service.GetHistoryAsync(f.Project, f.Run.Id, f.Project.OrganizationId, CancellationToken.None))!;

        estimator.Preflights.Should().HaveCountGreaterThanOrEqualTo(2, "both the hold and the full preflight ran");
        RevisionReplayResponse replay = history.Replay!;
        replay.Available.Should().BeTrue();
        replay.KeepCutEstimate.Should().BeNull();
        replay.ReEditEstimate.Should().BeNull("a balance that was never read is not a balance of 0");
    }

    [Fact]
    public async Task A_replay_carries_the_source_runs_output_resolution()
    {
        using Fixture f = new(new RecordingEstimator());
        (await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == f.Run.Id)).OutputResolution = 2160;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        CompositionRevisionRequestResult result = await f.Request();

        result.StatusCode.Should().Be(201, result.Message);
        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == result.Created!.ExecutionId);
        queued.OutputResolution.Should().Be(2160);
    }

    [Fact]
    public async Task A_request_queues_a_snapshot_of_the_workflow_with_the_film_step_revised_and_the_original_brief()
    {
        RecordingEstimator estimator = new();
        using Fixture f = new(estimator);

        CompositionRevisionRequestResult result = await f.Request();

        result.StatusCode.Should().Be(201, result.Message);
        CompositionRevisionResponse created = result.Created!;
        created.CutPolicy.Should().Be("KeepCut");

        WorkflowDefinition snapshot = await f.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync(w => w.Id == created.WorkflowId);
        snapshot.Kind.Should().Be(WorkflowDefinitionKind.CompositionReplay);
        snapshot.Name.Should().Be("Changes to Animated film + edit room cut");
        snapshot.Steps.OrderBy(s => s.StepOrder).Select(s => s.StepType).Should().Equal(
            StepType.Agent, StepType.Agent, StepType.Agent, StepType.Revise,
            StepType.VideoAnalyze, StepType.EditRoom, StepType.VideoCompile, StepType.ReviewLoop);
        snapshot.Steps.Single(s => s.StepOrder == 4).AgentDefinitionId.Should().Be(f.ReviserId);
        snapshot.Steps.Single(s => s.StepOrder == 7).VideoCompileConfigJson.Should().Be(f.Steps[6].VideoCompileConfigJson);

        CompositionRevision row = await f.Db.CompositionRevisions.SingleAsync();
        row.CutPolicy.Should().Be(CompositionCutPolicy.KeepCut);
        CompositionReplayPlan plan = CompositionReplayPlan.TryParse(row.ReplayPlanJson)!;
        plan.SourceExecutionId.Should().Be(f.Run.Id);
        plan.RevisedStepOrder.Should().Be(4);
        plan.For(1)!.SourceStepResultId.Should().Be(f.Results[1].Id);
        plan.OriginWorkflowDefinitionId.Should().Be(f.Workflow.Id);

        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == created.ExecutionId);
        queued.WorkflowDefinitionId.Should().Be(snapshot.Id);
        queued.UserRequest.Should().Be(Brief, "the replayed steps read the original brief; the change request is on the row");
        queued.OutputFormatOverride.Should().Be(RunOutputFormat.Vertical);
        queued.StyleConfigJson.Should().Contain("Bold");
        queued.RunBriefJson.Should().Contain("\"language\":\"it\"", "a revision of an Italian film stays Italian").And.Contain("45");

        // The preflight priced the revision, the steps that run and the decision that may be re-made — never the reused ones.
        estimator.Preflights.Last().Select(s => s.StepOrder).Should().Equal(4, 5, 6, 7, 8);
    }

    [Fact]
    public async Task A_refused_preflight_queues_nothing()
    {
        using Fixture f = new(new RecordingEstimator(refuse: true));

        CompositionRevisionRequestResult result = await f.Request("ReEdit");

        result.StatusCode.Should().Be(402);
        result.Code.Should().Be("INSUFFICIENT_CREDITS");
        result.Message.Should().StartWith("This run needs up to ").And.Contain(" more are needed. ")
            .And.EndWith("Credits that aren't used are returned when the run finishes.");
        result.Extra.Should().ContainKey("missing");
        (await f.Db.CompositionRevisions.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.CompositionReplay)).Should().Be(0);
    }

    [Fact]
    public async Task A_replay_of_a_2160p_run_after_a_downgrade_is_PLAN_NOT_ENTITLED_before_the_credit_preflight()
    {
        Mock<IOrganizationEntitlements> creator = new();
        creator.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { Tier = "creator", MaxOutputHeight = 1080 }));
        RecordingEstimator broke = new(refuse: true);
        using Fixture f = new(broke, planGate: null);
        PlanGateService gate = new(f.Db, creator.Object);
        (await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == f.Run.Id)).OutputResolution = 2160;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        CompositionRevisionService service = new(
            f.Db,
            new WorkflowExecutionService(f.Db, f.Bus.Object, NullLogger<WorkflowExecutionService>.Instance, gate),
            new ConfigurationBuilder().Build(),
            NullLogger<CompositionRevisionService>.Instance,
            broke,
            planGate: gate);

        Func<Task> act = () => service.RequestAsync(f.Project, f.Run.Id, f.User, f.Project.OrganizationId,
            new CreateCompositionRevisionRequest("More graphics, same timing"), CancellationToken.None);

        (await act.Should().ThrowAsync<PlanLimitException>()).Which.Violation.Code.Should().Be("PLAN_NOT_ENTITLED");
        broke.Preflights.Should().BeEmpty("the plan refuses the resolution before anything is priced");
        (await f.Db.CompositionRevisions.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.CompositionReplay)).Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_cut_policy_is_refused()
    {
        using Fixture f = new();

        (await f.Request("Sometimes")).StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task The_revision_version_links_to_its_runs_new_final_cut_and_reports_the_cut_was_kept()
    {
        using Fixture f = new();
        CompositionRevisionResponse created = (await f.Request()).Created!;
        WorkflowDefinition snapshot = await f.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync(w => w.Id == created.WorkflowId);
        Guid StepId(int order) => snapshot.Steps.Single(s => s.StepOrder == order).Id;

        // The engine ran the replay: steps 1-3 and the decision copied, the film revised, analyse/compile/review run.
        WorkflowExecution replay = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == created.ExecutionId);
        replay.Status = ExecutionStatus.Passed;
        string revisedRender = $"projects/{f.Project.Id}/outputFiles/{replay.Id}/revision.mp4";
        WorkflowStepResult Row(int order, string? key = null, Guid? reusedFrom = null) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = replay.Id,
            WorkflowStepId = StepId(order),
            Status = StepStatus.Completed,
            Output = "{}",
            OutputStorageKey = key,
            ReusedFromStepResultId = reusedFrom,
            ExecutedAt = DateTime.UtcNow.AddSeconds(order)
        };
        WorkflowStepResult revise = Row(4, revisedRender);
        WorkflowStepResult compile = Row(7, $"projects/{f.Project.Id}/outputFiles/final-cut-v2.mp4");
        f.Db.WorkflowStepResults.AddRange(
            Row(1, reusedFrom: f.Results[1].Id), Row(2, reusedFrom: f.Results[2].Id), Row(3, reusedFrom: f.Results[3].Id),
            revise, Row(5), Row(6, reusedFrom: f.Results[6].Id), compile, Row(8));
        CompositionWorkspaceVersion v2Row = new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = f.V1.WorkspaceId,
            ProjectId = f.Project.Id,
            VersionNumber = 2,
            ParentVersionId = f.V1.Id,
            StorageKey = "k",
            Sha256 = new string('b', 64),
            CreatedByExecutionId = replay.Id,
            RevisionId = created.Id,
            RenderOutputStorageKey = revisedRender,
            Instruction = "More graphics, same timing"
        };
        f.Db.CompositionWorkspaceVersions.Add(v2Row);
        (await f.Db.CompositionRevisions.SingleAsync(r => r.Id == created.Id)).ResultVersionId = v2Row.Id; // the runner's link
        await f.Db.SaveChangesAsync();

        CompositionRevisionHistoryResponse history = (await f.Service.GetHistoryAsync(f.Project, replay.Id, null, CancellationToken.None))!;

        CompositionVersionResponse v2 = history.Versions.Single(v => v.VersionNumber == 2);
        v2.RenderStepResultId.Should().Be(revise.Id, "the animation stays available as the secondary video");
        v2.Deliverables.Should().ContainSingle().Which.StepResultId.Should().Be(compile.Id);
        v2.CutPolicy.Should().Be("KeepCut");
        v2.CutKept.Should().BeTrue();
        history.Revisions.Single().CutPolicy.Should().Be("KeepCut");

        // Revising version 2 replays from version 2's own run (the snapshot), not from the original.
        CompositionRevisionRequestResult second = await f.Service.RequestAsync(f.Project, replay.Id, f.User, null,
            new CreateCompositionRevisionRequest("Warmer greys"), CancellationToken.None);
        second.StatusCode.Should().Be(201, second.Message);
        CompositionReplayPlan plan = CompositionReplayPlan.TryParse(
            (await f.Db.CompositionRevisions.SingleAsync(r => r.Id == second.Created!.Id)).ReplayPlanJson)!;
        plan.SourceExecutionId.Should().Be(replay.Id);
        plan.RevisedStepOrder.Should().Be(4);
        plan.OriginWorkflowDefinitionId.Should().Be(f.Workflow.Id);
    }

    [Fact]
    public async Task A_version_made_by_a_one_step_revision_replays_from_the_run_it_revised()
    {
        // 7ad6fbb2 on staging: a revision made before replays existed ran the shared hidden workflow's
        // single Revise step, so its run holds only the render; revising its version must still replay
        // the original pipeline.
        using Fixture f = new();
        WorkflowDefinition hidden = new() { Id = Guid.NewGuid(), Name = "Video revisions", ProjectId = f.Project.Id, Kind = WorkflowDefinitionKind.CompositionRevision };
        WorkflowStep reviseStep = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = hidden.Id, AgentDefinitionId = f.ReviserId, StepOrder = 1, StepType = StepType.Revise };
        WorkflowExecution legacy = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = hidden.Id, ProjectId = f.Project.Id, Status = ExecutionStatus.Passed, UserRequest = "More graphics" };
        string render = $"projects/{f.Project.Id}/outputFiles/{legacy.Id}/revision.mp4";
        f.Db.WorkflowDefinitions.Add(hidden);
        f.Db.WorkflowSteps.Add(reviseStep);
        f.Db.WorkflowExecutions.Add(legacy);
        f.Db.WorkflowStepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = legacy.Id, WorkflowStepId = reviseStep.Id, Status = StepStatus.Completed, OutputStorageKey = render, Output = "{}" });
        f.Db.CompositionRevisions.Add(new CompositionRevision
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            WorkspaceId = f.V1.WorkspaceId,
            SourceExecutionId = f.Run.Id,
            BaseVersionId = f.V1.Id,
            ExecutionId = legacy.Id,
            WorkflowDefinitionId = hidden.Id,
            Instruction = "More graphics",
            RequestedByUserId = f.User
        });
        CompositionWorkspaceVersion v2 = new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = f.V1.WorkspaceId,
            ProjectId = f.Project.Id,
            VersionNumber = 2,
            ParentVersionId = f.V1.Id,
            StorageKey = "k",
            Sha256 = new string('b', 64),
            CreatedByExecutionId = legacy.Id,
            RenderOutputStorageKey = render
        };
        f.Db.CompositionWorkspaceVersions.Add(v2);
        await f.Db.SaveChangesAsync();

        CompositionVersionResponse legacyVersion = (await f.Service.GetHistoryAsync(f.Project, legacy.Id, null, CancellationToken.None))!
            .Versions.Single(v => v.VersionNumber == 2);
        legacyVersion.Deliverables.Should().BeEmpty("a one-step revision never re-made the final cut");
        legacyVersion.FinalVideoExpected.Should().BeFalse();

        CompositionRevisionRequestResult result = await f.Service.RequestAsync(f.Project, legacy.Id, f.User, null,
            new CreateCompositionRevisionRequest("Keep it, and re-make the final cut", FromVersionId: v2.Id), CancellationToken.None);

        result.StatusCode.Should().Be(201, result.Message);
        CompositionRevision row = await f.Db.CompositionRevisions.SingleAsync(r => r.Id == result.Created!.Id);
        row.BaseVersionId.Should().Be(v2.Id, "the change starts from the legacy revision's version");
        CompositionReplayPlan plan = CompositionReplayPlan.TryParse(row.ReplayPlanJson)!;
        plan.SourceExecutionId.Should().Be(f.Run.Id, "seeded from the run that holds the whole pipeline");
        plan.RevisedStepOrder.Should().Be(4, "v2's parent's render is step 4 of that run");
    }

    // ---- QA-7: the revision preflight requires exactly what the engine will hold ----

    [Theory]
    [InlineData("KeepCut", null)]
    [InlineData("ReEdit", null)]
    [InlineData("KeepCut", 2160)]
    [InlineData("ReEdit", 2160)]
    public async Task The_revision_preflight_requires_exactly_the_engine_holds_of_the_charged_steps(string cutPolicy, int? resolution)
    {
        CapturingEstimator estimator = new();
        using Fixture f = new(estimator);
        estimator.Inner = RealEstimator.Over(f.Db);
        await SetSourceResolutionAsync(f, resolution);

        CompositionRevisionRequestResult result = await f.Request(cutPolicy);

        result.StatusCode.Should().Be(201, result.Message);
        (WorkflowDefinition snapshot, CompositionReplayPlan plan) = await SnapshotOf(f, result.Created!.Id);
        long engine = await EngineChargedHoldsAsync(f, snapshot, plan, await QueuedResolutionAsync(f, result.Created!.Id));

        long required = estimator.LastPreflight!.Estimate.RequiredMillis;
        required.Should().BePositive();
        required.Should().Be(engine, $"{cutPolicy} at {resolution}: the 402 must compare against what the engine will reserve for the steps that run");
        estimator.LastResolution.Should().Be(resolution, "the replay is priced at the resolution it renders at");
    }

    [Fact]
    public async Task A_4k_replay_requires_more_than_the_same_replay_at_auto()
    {
        long auto;
        {
            CapturingEstimator estimator = new();
            using Fixture f = new(estimator);
            estimator.Inner = RealEstimator.Over(f.Db);
            (await f.Request("ReEdit")).StatusCode.Should().Be(201);
            auto = estimator.LastPreflight!.Estimate.RequiredMillis;
        }

        CapturingEstimator uhd = new();
        using Fixture g = new(uhd);
        uhd.Inner = RealEstimator.Over(g.Db);
        await SetSourceResolutionAsync(g, 2160);
        (await g.Request("ReEdit")).StatusCode.Should().Be(201);

        uhd.LastPreflight!.Estimate.RequiredMillis.Should().BeGreaterThan(auto, "the Revise re-render and the compile hold four times as much at 4K");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2160)]
    public async Task The_replay_estimate_requires_exactly_the_engine_holds_of_the_charged_steps_under_each_cut_choice(int? resolution)
    {
        CapturingEstimator estimator = new();
        using Fixture f = new(estimator);
        estimator.Inner = RealEstimator.Over(f.Db);
        await SetSourceResolutionAsync(f, resolution);

        RevisionReplayResponse replay = (await f.Service.GetHistoryAsync(f.Project, f.Run.Id, f.Project.OrganizationId, CancellationToken.None))!.Replay!;
        long keep = await EngineChargedHoldsAsync(f, (await f.Request("KeepCut")).Created!.Id);
        long reEdit = await EngineChargedHoldsAsync(f, (await f.Request("ReEdit")).Created!.Id);

        ((long)Math.Round(replay.KeepCutEstimate!.RequiredCredits * 1000)).Should().Be(keep);
        ((long)Math.Round(replay.ReEditEstimate!.RequiredCredits * 1000)).Should().Be(reEdit);
    }

    private static async Task SetSourceResolutionAsync(Fixture f, int? resolution)
    {
        (await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == f.Run.Id)).OutputResolution = resolution;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
    }

    private static async Task<(WorkflowDefinition Snapshot, CompositionReplayPlan Plan)> SnapshotOf(Fixture f, Guid revisionId)
    {
        CompositionRevision row = await f.Db.CompositionRevisions.AsNoTracking().SingleAsync(r => r.Id == revisionId);
        WorkflowDefinition snapshot = await f.Db.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps).SingleAsync(w => w.Id == row.WorkflowDefinitionId);
        return (snapshot, CompositionReplayPlan.TryParse(row.ReplayPlanJson)!);
    }

    private static async Task<long> EngineChargedHoldsAsync(Fixture f, Guid revisionId)
    {
        (WorkflowDefinition snapshot, CompositionReplayPlan plan) = await SnapshotOf(f, revisionId);
        return await EngineChargedHoldsAsync(f, snapshot, plan, await QueuedResolutionAsync(f, revisionId));
    }

    /// <summary>The output resolution the revision's execution was queued with (the source run's).</summary>
    private static async Task<int?> QueuedResolutionAsync(Fixture f, Guid revisionId)
    {
        CompositionRevision row = await f.Db.CompositionRevisions.AsNoTracking().SingleAsync(r => r.Id == revisionId);
        return await f.Db.WorkflowExecutions.AsNoTracking().Where(e => e.Id == row.ExecutionId).Select(e => e.OutputResolution).SingleAsync();
    }

    /// <summary>
    /// What the engine reserves for the steps the replay runs: it plans over every step of the snapshot
    /// (<see cref="WorkflowExecutorService.ResolveStepReserveMillisAsync"/> with the execution's own
    /// steps), and only the charged ones ever reserve.
    /// </summary>
    private static async Task<long> EngineChargedHoldsAsync(Fixture f, WorkflowDefinition snapshot, CompositionReplayPlan plan, int? resolution)
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase("replay-engine-" + Guid.NewGuid().ToString("N")).Options;
        await using (WorkflowEngineDbContext engineDb = new(options))
        {
            foreach (AgentDefinition agent in await f.Db.AgentDefinitions.AsNoTracking().ToListAsync())
                engineDb.AgentDefinitions.Add(new AgentDefinition { Id = agent.Id, Name = agent.Name, AgentType = agent.AgentType });
            await engineDb.SaveChangesAsync();
        }

        var holds = new UsageHistoryCreditHoldEstimator(
            new EngineFactory(options),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<UsageHistoryCreditHoldEstimator>.Instance,
            time: null,
            videoProviders: RealEstimator.Resolver,
            videoClients: null,
            prices: RealEstimator.Rates);
        var executor = new WorkflowExecutorService(
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<IWorkflowEventPublisher>(),
            NullLogger<WorkflowExecutorService>.Instance,
            [],
            new RabbitMqHelper(new ConfigurationBuilder().Build()),
            Options.Create(new WorkflowHardeningOptions()),
            new ExecutionCancellationRegistry(),
            creditOptions: Options.Create(new StepCreditOptions()),
            creditHoldEstimator: holds);
        List<WorkflowStep> steps = [.. snapshot.Steps.OrderBy(s => s.StepOrder)];
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = snapshot.Id,
            ProjectId = snapshot.ProjectId,
            OrganizationId = f.Project.OrganizationId,
            OutputResolution = resolution,
            WorkflowDefinition = snapshot
        };

        long total = 0;
        foreach (WorkflowStep step in CompositionRevisionService.ChargedSteps(snapshot, plan, includeConditional: true))
            total += await executor.ResolveStepReserveMillisAsync(execution, step, CancellationToken.None, steps);
        return total;
    }

    /// <summary>The production estimator over the fixture's API database, the legacy seed prices and Opus on the platform for every agent.</summary>
    private static class RealEstimator
    {
        private static readonly DateTime Effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static readonly IModelPriceService Rates = LegacyCardPrices.Seed();

        public static readonly IInferenceProviderResolver Resolver = BuildResolver();

        public static IWorkflowCostEstimator Over(InferenceApiDbContext db) => new WorkflowCostEstimator(
            new Scopes(db),
            Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);

        private static IInferenceProviderResolver BuildResolver()
        {
            var resolver = new Mock<IInferenceProviderResolver>();
            resolver.Setup(r => r.ResolveAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedInferenceProvider(Guid.NewGuid(), "platform", InferenceProviderKind.Anthropic,
                    "https://x", "claude-opus-5-5", "key", 60));
            resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ResolvedSpeechSynthesisProvider?)null);
            resolver.Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ResolvedTranscriptionProvider?)null);
            resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ResolvedVideoGenerationProvider?)null);
            return resolver.Object;
        }

        private sealed class Scopes(InferenceApiDbContext db) : IServiceScopeFactory, IServiceScope, IServiceProvider
        {
            public IServiceScope CreateScope() => this;

            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(InferenceApiDbContext) ? db
                : serviceType == typeof(IInferenceProviderResolver) ? Resolver
                : serviceType == typeof(IModelPriceService) ? Rates
                : null;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// The real estimator, with every preflight recorded and answered Allowed (the fixture's organization has
    /// no credits), so the request goes through and the required figure it compared can be read back.
    /// </summary>
    private sealed class CapturingEstimator : IWorkflowCostEstimator
    {
        public IWorkflowCostEstimator Inner { get; set; } = null!;
        public WorkflowPreflightResult? LastPreflight { get; private set; }
        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Inner.EstimateAsync(organizationId, steps, ct);

        public async Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Allow(await Inner.PreflightAsync(organizationId, steps, ct));

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            Inner.EstimateChargedAsync(organizationId, runSteps, chargedStepOrders, ct);

        public async Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            Allow(await Inner.PreflightChargedAsync(organizationId, runSteps, chargedStepOrders, ct));

        public int? LastResolution { get; private set; }

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, int? outputResolution, CancellationToken ct = default) =>
            Inner.EstimateAsync(organizationId, steps, outputResolution, ct);

        public async Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, int? outputResolution, CancellationToken ct = default)
        {
            LastResolution = outputResolution;
            return Allow(await Inner.PreflightAsync(organizationId, steps, outputResolution, ct));
        }

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, int? outputResolution, CancellationToken ct = default) =>
            Inner.EstimateChargedAsync(organizationId, runSteps, chargedStepOrders, outputResolution, ct);

        public async Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, int? outputResolution, CancellationToken ct = default)
        {
            LastResolution = outputResolution;
            return Allow(await Inner.PreflightChargedAsync(organizationId, runSteps, chargedStepOrders, outputResolution, ct));
        }

        private WorkflowPreflightResult Allow(WorkflowPreflightResult result)
        {
            LastPreflight = result;
            return result with { Outcome = WorkflowPreflightOutcome.Allowed };
        }
    }

    private sealed class EngineFactory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }
}
