using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>
/// The request side (Inference API): lineage resolution from any run in it, availability, the hidden
/// system workflow, and every refusal the endpoint renders.
/// </summary>
public class CompositionRevisionServiceTests
{
    private const string Manifest =
        "{\"Version\":1,\"CompositionId\":\"FinalVideo\",\"Fps\":30,\"Scenes\":[" +
        "{\"Handle\":\"scene-1\",\"Name\":\"Intro\",\"FromFrame\":0,\"DurationInFrames\":90,\"Description\":\"Logo\",\"OnScreenText\":[]}]}";

    private sealed class Fixture : IDisposable
    {
        public InferenceApiDbContext Db { get; }
        public Mock<IPublishEndpoint> Bus { get; } = new();
        public Project Project { get; }
        public Guid User { get; } = Guid.NewGuid();
        public Guid ReviserAgentId { get; } = Guid.NewGuid();

        public Fixture(int maxPerHour = 10, IWorkflowCostEstimator? estimator = null)
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase("revisions-" + Guid.NewGuid().ToString("N")).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = User, OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);
            Db.AgentDefinitions.Add(new AgentDefinition { Id = ReviserAgentId, Name = "CompositionReviser", AgentType = AgentType.CompositionReviser, IsBuiltIn = true });
            Db.SaveChanges();
            Service = new CompositionRevisionService(
                Db,
                new WorkflowExecutionService(Db, Bus.Object, NullLogger<WorkflowExecutionService>.Instance),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CompositionRevisions:MaxPerHourPerUserProject"] = maxPerHour.ToString()
                }).Build(),
                NullLogger<CompositionRevisionService>.Instance,
                estimator);
        }

        public CompositionRevisionService Service { get; }

        /// <summary>A finished run whose Author step rendered a film.</summary>
        public WorkflowExecution FilmRun(ExecutionStatus status = ExecutionStatus.Passed, DateTime? completedAt = null, StepType renderStep = StepType.Agent)
        {
            WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Promo", ProjectId = Project.Id };
            WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = ReviserAgentId, StepOrder = 1, StepType = renderStep };
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
            Db.SaveChanges();
            return run;
        }

        public CompositionWorkspaceVersion Capture(WorkflowExecution run)
        {
            CompositionWorkspace workspace = new() { Id = Guid.NewGuid(), ProjectId = Project.Id, OrganizationId = Project.OrganizationId, OriginExecutionId = run.Id, LatestVersionNumber = 1 };
            CompositionWorkspaceVersion version = new()
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                ProjectId = Project.Id,
                VersionNumber = 1,
                StorageKey = CompositionWorkspaceKeys.ArchiveKey(Project.Id, workspace.Id, Guid.NewGuid()),
                Sha256 = new string('a', 64),
                CreatedByExecutionId = run.Id,
                CompositionId = "FinalVideo",
                Fps = 30,
                SceneManifestJson = Manifest,
                RenderOutputStorageKey = $"projects/{Project.Id}/outputFiles/{run.Id}/promo.mp4"
            };
            Db.CompositionWorkspaces.Add(workspace);
            Db.CompositionWorkspaceVersions.Add(version);
            Db.SaveChanges();
            return version;
        }

        public Task<CompositionRevisionRequestResult> Request(Guid executionId, string? instruction = "Bigger intro title", Guid? fromVersionId = null, CompositionRevisionTargets? targets = null, ExecutionInitiator? initiator = null) =>
            initiator is { } who
                ? Service.RequestAsync(Project, executionId, User, Project.OrganizationId,
                    new CreateCompositionRevisionRequest(instruction, targets, fromVersionId), CancellationToken.None, who)
                : Service.RequestAsync(Project, executionId, User, Project.OrganizationId,
                    new CreateCompositionRevisionRequest(instruction, targets, fromVersionId), CancellationToken.None);

        /// <summary>A later version made by another run in the same workspace.</summary>
        public CompositionWorkspaceVersion LaterVersion(CompositionWorkspaceVersion v1, int number, ExecutionStatus creatorStatus, bool rendered, string? manifest = null)
        {
            WorkflowExecution creator = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                ProjectId = Project.Id,
                OrganizationId = Project.OrganizationId,
                Status = creatorStatus
            };
            CompositionWorkspaceVersion v = new()
            {
                Id = Guid.NewGuid(),
                WorkspaceId = v1.WorkspaceId,
                ProjectId = Project.Id,
                VersionNumber = number,
                StorageKey = v1.StorageKey,
                Sha256 = v1.Sha256,
                CreatedByExecutionId = creator.Id,
                CompositionId = "FinalVideo",
                Fps = 30,
                SceneManifestJson = manifest ?? Manifest,
                RenderOutputStorageKey = rendered ? $"projects/{Project.Id}/outputFiles/{creator.Id}/promo.mp4" : null
            };
            Db.WorkflowExecutions.Add(creator);
            Db.CompositionWorkspaceVersions.Add(v);
            Db.SaveChanges();
            return v;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task A_restore_in_progress_refuses_a_revision_before_anything_is_queued()
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        f.Capture(run);
        int runsBefore = await f.Db.WorkflowExecutions.CountAsync();

        using IDisposable held = RestoreRunGate.HoldForTests(f.Project.Id);
        CompositionRevisionRequestResult result = await f.Request(run.Id);

        result.StatusCode.Should().Be(409);
        result.Code.Should().Be("RESTORE_IN_PROGRESS");
        (await f.Db.WorkflowExecutions.CountAsync()).Should().Be(runsBefore);
        (await f.Db.CompositionRevisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_captured_run_is_revisable_and_its_history_lists_the_version_with_a_playable_render()
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        CompositionWorkspaceVersion v1 = f.Capture(run);

        CompositionRevisionHistoryResponse history = (await f.Service.GetHistoryAsync(f.Project, run.Id, f.Project.OrganizationId, CancellationToken.None))!;

        history.Availability.Should().Be(CompositionRevisionService.AvailabilitySnapshot);
        history.CanRevise.Should().BeTrue();
        history.CurrentVersionId.Should().Be(v1.Id);
        history.Scenes.Should().ContainSingle().Which.Name.Should().Be("Intro");
        CompositionVersionResponse version = history.Versions.Single();
        version.RenderStepResultId.Should().NotBeNull("the UI plays a version through the outputs endpoint by step result");
        version.WorkflowId.Should().Be(run.WorkflowDefinitionId);
        version.SceneCount.Should().Be(1);
    }

    /// <summary>Billing on: every preflight is Allowed with a real, non-zero estimate, evaluated or not as told.</summary>
    private sealed class PreflightStub(bool evaluated) : IWorkflowCostEstimator
    {
        private static readonly WorkflowCostEstimate Estimate = new(true, false, 2_000, 6_000, 6_000, []);
        public int Preflights { get; private set; }
        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(Estimate);

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
        {
            Preflights++;
            return Task.FromResult(new WorkflowPreflightResult(WorkflowPreflightOutcome.Allowed, Estimate, evaluated ? 50_000 : 0, evaluated));
        }

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            Task.FromResult(Estimate);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            PreflightAsync(organizationId, runSteps, ct);
    }

    [Fact]
    public async Task An_evaluated_preflight_shows_the_revision_estimate()
    {
        PreflightStub estimator = new(evaluated: true);
        using Fixture f = new(estimator: estimator);
        WorkflowExecution run = f.FilmRun();
        f.Capture(run);

        CompositionRevisionHistoryResponse history = (await f.Service.GetHistoryAsync(f.Project, run.Id, f.Project.OrganizationId, CancellationToken.None))!;

        history.Replay.Should().Match<RevisionReplayResponse?>(r => r == null || !r.Available, "the revision estimate, not the replay's, is shown");
        RevisionCostEstimateResponse estimate = history.Estimate!;
        estimate.RequiredCredits.Should().Be(6);
        estimate.BalanceCredits.Should().Be(50);
        estimate.Affordable.Should().BeTrue();
    }

    [Fact]
    public async Task A_preflight_that_was_not_evaluated_shows_no_revision_estimate()
    {
        PreflightStub estimator = new(evaluated: false);
        using Fixture f = new(estimator: estimator);
        WorkflowExecution run = f.FilmRun();
        f.Capture(run);

        CompositionRevisionHistoryResponse history = (await f.Service.GetHistoryAsync(f.Project, run.Id, f.Project.OrganizationId, CancellationToken.None))!;

        estimator.Preflights.Should().BePositive("the revision estimate asked for a preflight");
        history.Replay.Should().Match<RevisionReplayResponse?>(r => r == null || !r.Available);
        history.Estimate.Should().BeNull("a balance that was never read is not a balance of 0 / unaffordable");
    }

    [Fact]
    public async Task A_recent_film_run_without_a_snapshot_is_recoverable_and_an_old_one_or_a_non_film_run_is_not()
    {
        using Fixture f = new();
        (await f.Service.GetHistoryAsync(f.Project, f.FilmRun().Id, null, CancellationToken.None))!
            .Availability.Should().Be(CompositionRevisionService.AvailabilityRecoverable);

        CompositionRevisionHistoryResponse old = (await f.Service.GetHistoryAsync(
            f.Project, f.FilmRun(completedAt: DateTime.UtcNow.AddDays(-9)).Id, null, CancellationToken.None))!;
        old.Availability.Should().Be(CompositionRevisionService.AvailabilityUnavailable);
        old.CanRevise.Should().BeFalse();
        old.UnavailableReason.Should().Contain("more than 7 days ago");

        CompositionRevisionHistoryResponse compiled = (await f.Service.GetHistoryAsync(
            f.Project, f.FilmRun(renderStep: StepType.VideoCompile).Id, null, CancellationToken.None))!;
        compiled.Availability.Should().Be(CompositionRevisionService.AvailabilityUnavailable);
        compiled.UnavailableReason.Should().Contain("did not make an animated video");
    }

    [Fact]
    public async Task A_run_in_another_project_is_not_found()
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        Project other = new() { Id = Guid.NewGuid(), Name = "Other", OrganizationId = Guid.NewGuid() };

        (await f.Service.GetHistoryAsync(other, run.Id, null, CancellationToken.None)).Should().BeNull();
        (await f.Service.RequestAsync(other, run.Id, f.User, null, new CreateCompositionRevisionRequest("x"), CancellationToken.None))
            .StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task A_request_creates_the_hidden_workflow_the_request_row_and_a_queued_execution_sharing_its_id()
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        CompositionWorkspaceVersion v1 = f.Capture(run);

        CompositionRevisionRequestResult result = await f.Request(run.Id, targets: new CompositionRevisionTargets { Scenes = ["Intro"] });

        result.StatusCode.Should().Be(201);
        CompositionRevisionResponse created = result.Created!;
        created.BaseVersionId.Should().Be(v1.Id);
        created.BaseVersionNumber.Should().Be(1);
        created.Status.Should().Be("Queued");

        WorkflowDefinition system = await f.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync(w => w.Kind == WorkflowDefinitionKind.CompositionRevision);
        system.Id.Should().Be(created.WorkflowId);
        system.Steps.Should().ContainSingle().Which.StepType.Should().Be(StepType.Revise);
        system.Steps.Single().AgentDefinitionId.Should().Be(f.ReviserAgentId);

        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == created.ExecutionId);
        queued.WorkflowDefinitionId.Should().Be(system.Id);
        queued.UserRequest.Should().Be("Bigger intro title");
        CompositionRevision row = await f.Db.CompositionRevisions.SingleAsync();
        row.ExecutionId.Should().Be(queued.Id);
        row.TargetsJson.Should().Be("{\"scenes\":[\"Intro\"]}");
        f.Bus.Verify(b => b.Publish(It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == queued.Id), It.IsAny<CancellationToken>()), Times.Once);

        // A second request reuses the same system workflow, and the revision run's own history is the same chain.
        (await f.Request(run.Id, "And the outro")).StatusCode.Should().Be(201);
        (await f.Db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.CompositionRevision)).Should().Be(1);
        CompositionRevisionHistoryResponse fromRevisionRun = (await f.Service.GetHistoryAsync(f.Project, created.ExecutionId, null, CancellationToken.None))!;
        fromRevisionRun.WorkspaceId.Should().Be(v1.WorkspaceId);
        fromRevisionRun.Revisions.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_request_on_an_older_run_with_no_version_named_builds_on_the_newest_version()
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        CompositionWorkspaceVersion v1 = f.Capture(run);
        // A later change request made version 2 in the same workspace; the owner still looks at the original run.
        CompositionWorkspaceVersion v2 = f.LaterVersion(v1, 2, ExecutionStatus.Passed, rendered: true);

        CompositionRevisionHistoryResponse history = (await f.Service.GetHistoryAsync(f.Project, run.Id, null, CancellationToken.None))!;
        history.CurrentVersionId.Should().Be(v1.Id);
        history.HeadVersionId.Should().Be(v2.Id);
        history.HeadVersionNumber.Should().Be(2);

        CompositionRevisionResponse implicitBase = (await f.Request(run.Id)).Created!;
        implicitBase.BaseVersionId.Should().Be(v2.Id, "the earlier change must not be dropped");
        implicitBase.BaseVersionNumber.Should().Be(2);

        CompositionRevisionResponse explicitBase = (await f.Request(run.Id, fromVersionId: v1.Id)).Created!;
        explicitBase.BaseVersionId.Should().Be(v1.Id, "an explicit version means exactly that version");
    }

    [Theory]
    [InlineData(ExecutionStatus.Failed, true)]
    [InlineData(ExecutionStatus.Running, true)]
    [InlineData(ExecutionStatus.Passed, false)]
    public async Task The_default_base_skips_a_version_that_is_not_a_finished_render(ExecutionStatus creator, bool rendered)
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        CompositionWorkspaceVersion v1 = f.Capture(run);
        CompositionWorkspaceVersion good = f.LaterVersion(v1, 2, ExecutionStatus.Passed, rendered: true);
        f.LaterVersion(v1, 3, creator, rendered);

        CompositionRevisionHistoryResponse history = (await f.Service.GetHistoryAsync(f.Project, run.Id, null, CancellationToken.None))!;
        history.HeadVersionId.Should().Be(good.Id);
        (await f.Request(run.Id)).Created!.BaseVersionId.Should().Be(good.Id);
    }

    [Fact]
    public async Task The_scenes_described_are_those_of_the_version_the_change_applies_to()
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        CompositionWorkspaceVersion v1 = f.Capture(run);
        string outro = Manifest.Replace("Intro", "Outro");
        CompositionWorkspaceVersion v2 = f.LaterVersion(v1, 2, ExecutionStatus.Passed, rendered: true, outro);

        (await f.Service.GetHistoryAsync(f.Project, run.Id, null, CancellationToken.None))!
            .Scenes.Single().Name.Should().Be("Outro", "no version named means the head");
        (await f.Service.GetHistoryAsync(f.Project, run.Id, null, CancellationToken.None, v1.Id))!
            .Scenes.Single().Name.Should().Be("Intro", "a named version's own scenes");
    }

    [Fact]
    public async Task An_instruction_revision_carries_the_source_runs_brief_language_and_design_opt_out()
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        f.Capture(run);
        run.RunBriefJson = "{\"version\":1,\"language\":\"it\",\"designSystem\":\"none\"}";
        await f.Db.SaveChangesAsync();

        CompositionRevisionRequestResult result = await f.Request(run.Id);

        result.StatusCode.Should().Be(201);
        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == result.Created!.ExecutionId);
        queued.RunBriefJson.Should().Contain("\"language\":\"it\"").And.Contain("\"designSystem\":\"none\"");
        queued.DesignSystemPinJson.Should().BeNull("a run that opted out of the brand is never re-pinned by its revision");
    }

    [Fact]
    public async Task The_hidden_workflow_cannot_be_executed_through_the_ordinary_execute_path()
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        f.Capture(run);
        CompositionRevisionResponse created = (await f.Request(run.Id)).Created!;

        Func<Task> act = () => new WorkflowExecutionService(f.Db, f.Bus.Object, NullLogger<WorkflowExecutionService>.Instance)
            .ExecuteAsync(f.Project.Id, created.WorkflowId, f.User, "x", ExecutionInitiator.Human, CancellationToken.None);

        await act.Should().ThrowAsync<WorkflowNotFoundException>();
    }

    [Fact]
    public async Task A_recoverable_run_gets_a_workspace_and_a_request_with_no_base_version()
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();

        CompositionRevisionRequestResult result = await f.Request(run.Id);

        result.StatusCode.Should().Be(201);
        result.Created!.BaseVersionId.Should().BeNull("the engine recovers version 1 from the run's sandbox first");
        (await f.Db.CompositionWorkspaces.SingleAsync()).OriginExecutionId.Should().Be(run.Id);
    }

    [Theory]
    [InlineData(null, ExecutionInitiator.Human)]
    [InlineData(ExecutionInitiator.Assistant, ExecutionInitiator.Assistant)]
    [InlineData(ExecutionInitiator.Mcp, ExecutionInitiator.Mcp)]
    public async Task The_queued_revision_run_records_who_asked_for_it(ExecutionInitiator? asked, ExecutionInitiator expected)
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        f.Capture(run);

        CompositionRevisionRequestResult result = await f.Request(run.Id, initiator: asked);

        result.StatusCode.Should().Be(201);
        (await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == result.Created!.ExecutionId)).Initiator.Should().Be(expected);
    }

    [Theory]
    [InlineData(-3600, true)]
    [InlineData(3600, false)]
    public async Task A_run_that_first_started_at_or_before_the_projects_marker_is_closed_to_revisions(int firstStartOffset, bool closed)
    {
        using Fixture f = new();
        WorkflowExecution run = f.FilmRun();
        DateTime marker = DateTime.UtcNow.AddDays(-1);
        (await f.Db.Projects.SingleAsync(p => p.Id == f.Project.Id)).ContentQuarantinedAt = marker;
        (await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == run.Id)).FirstStartedAt = marker.AddSeconds(firstStartOffset);
        await f.Db.SaveChangesAsync();
        int runsBefore = await f.Db.WorkflowExecutions.CountAsync();

        CompositionRevisionHistoryResponse? history = await f.Service.GetHistoryAsync(f.Project, run.Id, f.Project.OrganizationId, CancellationToken.None);
        history!.CanRevise.Should().Be(!closed, "the page and the assistant offer exactly what the request gate allows");

        CompositionRevisionRequestResult result = await f.Request(run.Id);

        if (closed)
        {
            result.StatusCode.Should().Be(409);
            result.Code.Should().Be("RUN_CLOSED");
            result.Message.Should().Be("This run can no longer be changed. Start a new run.");
            (await f.Db.WorkflowExecutions.CountAsync()).Should().Be(runsBefore, "nothing was queued");
        }
        else
        {
            result.Code.Should().NotBe("RUN_CLOSED");
        }
    }

    [Fact]
    public async Task Every_refusal_answers_with_its_own_status_and_code()
    {
        using Fixture f = new(maxPerHour: 2);
        WorkflowExecution running = f.FilmRun(ExecutionStatus.Running);
        (await f.Request(running.Id)).Should().Match<CompositionRevisionRequestResult>(r => r.StatusCode == 409 && r.Code == "RUN_NOT_FINISHED");

        WorkflowExecution old = f.FilmRun(completedAt: DateTime.UtcNow.AddDays(-30));
        (await f.Request(old.Id)).Should().Match<CompositionRevisionRequestResult>(r => r.StatusCode == 409 && r.Code == "WORKSPACE_UNAVAILABLE");

        WorkflowExecution run = f.FilmRun();
        f.Capture(run);
        (await f.Request(run.Id, "   ")).StatusCode.Should().Be(400);
        (await f.Request(run.Id, new string('x', 4001))).StatusCode.Should().Be(400);
        (await f.Request(run.Id, targets: new CompositionRevisionTargets { FrameRanges = [new CompositionFrameRange(9, 3)] })).StatusCode.Should().Be(400);
        (await f.Request(run.Id, fromVersionId: Guid.NewGuid())).StatusCode.Should().Be(404);

        (await f.Request(run.Id)).StatusCode.Should().Be(201);
        (await f.Request(run.Id)).StatusCode.Should().Be(201);
        CompositionRevisionRequestResult limited = await f.Request(run.Id);
        limited.StatusCode.Should().Be(429);
        limited.Code.Should().Be("REVISION_RATE_LIMITED");
        limited.Extra!["retryAfterSeconds"].Should().BeOfType<int>().Which.Should().BePositive();
        limited.Extra["used"].Should().Be(2);
        limited.Extra["limit"].Should().Be(2);
        limited.Extra["windowMinutes"].Should().Be(60);
        limited.Message.Should().MatchRegex(@"^Change limit reached — 2 of 2 changes in this project in the last hour; try again in \d+ minutes?\.$",
            "QA-P0-15: the sentence names the limit and the wait, like the run limit does");
    }
}
