using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Compositions.ReplayShapes;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>The change-request API service (docs/design/change-requests.md): sources, validation, plan, snapshot, price, rate limit, tenancy.</summary>
public class StepChangeRequestServiceTests
{
    private const string VoiceoverOutput = """{"view":{"lines":[{"id":"vo0","status":"ok","durationSec":2.5,"text":"Hello"},{"id":"vo1","status":"ok","durationSec":3,"text":"World"}]}}""";

    private sealed class Estimator(bool refuse = false) : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(Estimate(steps));

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowPreflightResult(
                refuse ? WorkflowPreflightOutcome.InsufficientCredits : WorkflowPreflightOutcome.Allowed, Estimate(steps), 50_000));

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            EstimateAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            PreflightAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);

        /// <summary>The per-step scales the last scaled preflight was asked for (null = the plain overload was used).</summary>
        public IReadOnlyDictionary<int, double>? LastScales { get; private set; }

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, int? outputResolution, IReadOnlyDictionary<int, double>? stepScales, CancellationToken ct = default)
        {
            LastScales = stepScales;
            return PreflightChargedAsync(organizationId, runSteps, chargedStepOrders, ct);
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
        public StepChangeRequestService Service { get; }
        public List<WorkflowStep> Steps { get; }
        public WorkflowDefinition Workflow { get; }
        public WorkflowDefinition? Snapshot { get; }
        public WorkflowExecution Run { get; }
        public Dictionary<int, WorkflowStepResult> Results { get; }

        public Fixture(IWorkflowCostEstimator? estimator = null, ExecutionStatus status = ExecutionStatus.Passed, bool ownSnapshot = false, bool deleteWorkflow = false, int maxPerHour = 10, IServiceProvider? services = null)
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase("change-svc-" + Guid.NewGuid().ToString("N")).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = User, OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);

            Steps =
            [
                Step(1, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.FullWorkflow),
                Step(2, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.FullWorkflow),
                Step(3, AgentType.VideoTransform, StepType.Voiceover, voiceover: """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":2}}"""),
                Step(4, AgentType.VideoTransform, StepType.VideoAnalyze, analyze: """{"version":1,"source":{"kind":"StepOutput","stepOrder":1}}"""),
                Step(5, AgentType.VideoTransform, StepType.VideoCompile, compile: """{"version":1,"decision":{"from":"Step","stepOrder":4},"analysisStepOrder":4,"enableVoiceover":true,"voiceoverStepOrder":3}"""),
                Step(6, AgentType.VideoReviewAgent, StepType.ReviewLoop, mode: AgentInputContextMode.FullWorkflow, loopTarget: 2)
            ];
            Db.AgentDefinitions.AddRange(Steps.Select(s => s.AgentDefinition));

            Workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Voiceover film", ProjectId = Project.Id };
            Guid stepsDefinition = Workflow.Id;
            if (ownSnapshot)
            {
                Snapshot = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "snap", ProjectId = Project.Id, Kind = WorkflowDefinitionKind.ExecutionSnapshot };
                stepsDefinition = Snapshot.Id;
                Db.WorkflowDefinitions.Add(Snapshot);
            }
            foreach (WorkflowStep step in Steps)
                step.WorkflowDefinitionId = stepsDefinition;
            if (!deleteWorkflow)
                Db.WorkflowDefinitions.Add(Workflow);
            Db.WorkflowSteps.AddRange(Steps);

            Run = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Workflow.Id,
                SnapshotDefinitionId = Snapshot?.Id,
                ProjectId = Project.Id,
                OrganizationId = Project.OrganizationId,
                Status = status,
                UserRequest = "A reel",
                StartedAt = DateTime.UtcNow.AddHours(-1),
                CompletedAt = DateTime.UtcNow
            };
            Db.WorkflowExecutions.Add(Run);
            Results = Steps.ToDictionary(s => s.StepOrder, s => new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = Run.Id,
                WorkflowStepId = s.Id,
                Status = StepStatus.Completed,
                ExecutedAt = DateTime.UtcNow.AddMinutes(-60 + s.StepOrder),
                Output = "{}",
                OutputJson = s.StepType == StepType.Voiceover ? VoiceoverOutput : null
            });
            Db.WorkflowStepResults.AddRange(Results.Values);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();

            Service = new StepChangeRequestService(
                Db,
                new WorkflowExecutionService(Db, Bus.Object, NullLogger<WorkflowExecutionService>.Instance),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CompositionRevisions:MaxPerHourPerUserProject"] = maxPerHour.ToString()
                }).Build(),
                NullLogger<StepChangeRequestService>.Instance,
                estimator,
                services: services);
        }

        public static CreateStepChangeRequest Voice(string? text = "New line") => new(
            3, null, "Say it differently", ["vo0"],
            new() { ["vo0"] = new() { ["text"] = JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone() } });

        public Task<StepChangeRequestResult> Request(CreateStepChangeRequest? body = null, Guid? executionId = null) =>
            Service.RequestAsync(Project, executionId ?? Run.Id, User, Project.OrganizationId, body ?? Voice(), CancellationToken.None);

        public void Dispose() => Db.Dispose();
    }

    // ------------------------------------------------------------------ a marked project

    [Theory]
    [InlineData(-3600, true)]    // first started before the marker: closed
    [InlineData(60, true)]       // within the clock margin
    [InlineData(3600, false)]
    public async Task A_run_that_first_started_at_or_before_the_projects_marker_is_closed_to_estimates_and_requests(int firstStartOffset, bool closed)
    {
        using Fixture f = new();
        DateTime marker = DateTime.UtcNow.AddDays(-1);
        (await f.Db.Projects.SingleAsync(p => p.Id == f.Project.Id)).ContentQuarantinedAt = marker;
        (await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == f.Run.Id)).FirstStartedAt = marker.AddSeconds(firstStartOffset);
        await f.Db.SaveChangesAsync();

        StepChangeRequestResult estimate = await f.Service.EstimateAsync(
            f.Project, f.Run.Id, f.Project.OrganizationId, Fixture.Voice(), CancellationToken.None);
        StepChangeRequestResult request = await f.Request();

        if (closed)
        {
            foreach (StepChangeRequestResult refused in new[] { estimate, request })
            {
                refused.StatusCode.Should().Be(409);
                refused.Code.Should().Be("RUN_CLOSED");
                refused.Message.Should().Be("This run can no longer be changed. Start a new run.");
            }

            (await f.Db.WorkflowExecutions.CountAsync()).Should().Be(1, "no run was queued");
        }
        else
        {
            estimate.StatusCode.Should().NotBe(409);
            request.StatusCode.Should().Be(201);
        }
    }

    [Fact]
    public async Task A_restore_in_progress_refuses_a_change_request()
    {
        using Fixture f = new();
        int runsBefore = await f.Db.WorkflowExecutions.CountAsync();

        using IDisposable held = RestoreRunGate.HoldForTests(f.Project.Id);
        StepChangeRequestResult result = await f.Request();

        result.StatusCode.Should().Be(409);
        result.Code.Should().Be("RESTORE_IN_PROGRESS");
        (await f.Db.WorkflowExecutions.CountAsync()).Should().Be(runsBefore);
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_run_with_no_first_start_is_closed_once_the_project_is_marked()
    {
        using Fixture f = new();
        (await f.Db.Projects.SingleAsync(p => p.Id == f.Project.Id)).ContentQuarantinedAt = DateTime.UtcNow.AddDays(-1);
        await f.Db.SaveChangesAsync();

        (await f.Request()).Code.Should().Be("RUN_CLOSED");
    }

    // ------------------------------------------------------------------ sources

    [Fact]
    public async Task The_runs_own_snapshot_steps_are_preferred_over_the_workflow()
    {
        using Fixture f = new(ownSnapshot: true, deleteWorkflow: true);

        StepChangeSourceResolution r = await StepChangeRequestSources.ResolveAsync(f.Db, f.Project.Id, f.Run.Id, new StepChangeTarget(3, null), CancellationToken.None);

        r.Found.Should().BeTrue("a deleted user workflow never breaks a finished run");
        r.Source!.Steps.Select(s => s.WorkflowDefinitionId).Distinct().Should().Equal(f.Snapshot!.Id);
        r.Target!.StepResultId.Should().Be(f.Results[3].Id);
    }

    [Fact]
    public async Task A_target_can_be_named_by_result_id_and_a_mismatch_is_refused()
    {
        using Fixture f = new();

        (await StepChangeRequestSources.ResolveAsync(f.Db, f.Project.Id, f.Run.Id, new StepChangeTarget(null, f.Results[3].Id), CancellationToken.None))
            .Target!.StepOrder.Should().Be(3);
        (await StepChangeRequestSources.ResolveAsync(f.Db, f.Project.Id, f.Run.Id, new StepChangeTarget(2, f.Results[3].Id), CancellationToken.None))
            .StatusCode.Should().Be(400);
        (await StepChangeRequestSources.ResolveAsync(f.Db, f.Project.Id, f.Run.Id, new StepChangeTarget(null, Guid.NewGuid()), CancellationToken.None))
            .StatusCode.Should().Be(404);
        (await StepChangeRequestSources.ResolveAsync(f.Db, f.Project.Id, f.Run.Id, new StepChangeTarget(99, null), CancellationToken.None))
            .StatusCode.Should().Be(404);
        (await StepChangeRequestSources.ResolveAsync(f.Db, f.Project.Id, f.Run.Id, new StepChangeTarget(null, null), CancellationToken.None))
            .StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task An_unfinished_run_is_409_RUN_NOT_FINISHED()
    {
        using Fixture f = new(status: ExecutionStatus.Running);

        StepChangeRequestResult result = await f.Request();

        result.StatusCode.Should().Be(409);
        result.Code.Should().Be("RUN_NOT_FINISHED");
    }

    [Fact]
    public async Task A_skipped_or_missing_target_result_is_409()
    {
        using Fixture f = new();
        (await f.Db.WorkflowStepResults.SingleAsync(r => r.Id == f.Results[3].Id)).Status = StepStatus.Skipped;
        await f.Db.SaveChangesAsync();

        (await f.Request()).StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task A_foreign_project_or_organization_execution_is_404_everywhere()
    {
        using Fixture f = new();
        Project other = new() { Id = Guid.NewGuid(), Name = "O", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

        (await f.Service.RequestAsync(other, f.Run.Id, f.User, other.OrganizationId, Fixture.Voice(), CancellationToken.None)).StatusCode.Should().Be(404);
        (await f.Service.EstimateAsync(other, f.Run.Id, other.OrganizationId, Fixture.Voice(), CancellationToken.None)).StatusCode.Should().Be(404);
        (await f.Service.GetCapabilitiesAsync(other, f.Run.Id, CancellationToken.None)).Should().BeNull();
        (await f.Service.ListAsync(other, f.Run.Id, CancellationToken.None)).Should().BeNull();
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------ scope fraction

    [Fact]
    public async Task A_longer_text_on_one_of_two_lines_stores_its_cost_share_and_prices_the_target_step_by_it()
    {
        Estimator estimator = new();
        using Fixture f = new(estimator);

        StepChangeRequestResult result = await f.Request();

        result.StatusCode.Should().Be(201);
        (await f.Db.StepChangeRequests.SingleAsync()).ScopeFraction.Should().Be(0.8, "'New line' (8 chars) replaces 'Hello' (5) next to 'World' (5): 8 of 10");
        estimator.LastScales.Should().NotBeNull().And.ContainKey(3).WhoseValue.Should().Be(0.8, "the API estimate scales by the same stored fraction");
    }

    [Fact]
    public async Task A_pronunciation_saved_after_the_run_adds_the_untouched_line_that_holds_the_term_to_the_request_and_its_price()
    {
        Estimator estimator = new();
        using Fixture f = new(estimator);
        f.Db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Term = "World",
            Spelling = "Wurld"
        });
        await f.Db.SaveChangesAsync();

        StepChangeRequestResult result = await f.Request();

        result.StatusCode.Should().Be(201);
        StepChangeRequest row = await f.Db.StepChangeRequests.SingleAsync();
        JsonSerializer.Deserialize<string[]>(row.ScopeJson!).Should().BeEquivalentTo("vo0", "vo1");
        row.ScopeFraction.Should().Be(1d, "both lines are made again, so the hold covers the whole step (without the glossary it is 0.8)");
        estimator.LastScales.Should().NotBeNull().And.ContainKey(3).WhoseValue.Should().Be(1d);
    }

    [Fact]
    public async Task A_pronunciation_that_matches_no_line_leaves_the_request_as_asked()
    {
        using Fixture f = new(new Estimator());
        f.Db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Term = "Elsewhere",
            Spelling = "Elsewhère"
        });
        await f.Db.SaveChangesAsync();

        await f.Request();

        StepChangeRequest row = await f.Db.StepChangeRequests.SingleAsync();
        JsonSerializer.Deserialize<string[]>(row.ScopeJson!).Should().BeEquivalentTo("vo0");
        row.ScopeFraction.Should().Be(0.8);
    }

    [Fact]
    public async Task A_star_override_or_an_unreadable_output_stores_the_whole_step()
    {
        using Fixture all = new(new Estimator());
        await all.Request(new CreateStepChangeRequest(
            3, null, "Slower", null, new() { ["*"] = new() { ["speed"] = JsonDocument.Parse("0.9").RootElement.Clone() } }));
        (await all.Db.StepChangeRequests.SingleAsync()).ScopeFraction.Should().Be(1d);

        using Fixture unknown = new(new Estimator());
        (await unknown.Db.WorkflowStepResults.SingleAsync(r => r.Id == unknown.Results[3].Id)).OutputJson = null;
        await unknown.Db.SaveChangesAsync();
        // Without readable lines the item ids cannot be validated, so the request is refused; the estimate path still answers 1.
        double fraction = ReelBolt.Shared.Workflows.ChangeRequests.StepChangeScope.Compute(
            StepType.Voiceover, new ReelBolt.Shared.Workflows.ChangeRequests.ChangeRequestPayload("x", ["vo0"], null), null);
        fraction.Should().Be(1d);
    }

    [Fact]
    public async Task A_request_whose_run_later_failed_still_counts_against_the_hourly_bucket()
    {
        using Fixture f = new(new Estimator(), maxPerHour: 2);
        for (int i = 0; i < 2; i++)
        {
            Guid failedRun = Guid.NewGuid();
            f.Db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = failedRun,
                WorkflowDefinitionId = f.Workflow.Id,
                ProjectId = f.Project.Id,
                Status = ExecutionStatus.Failed,
                ErrorMessage = "SCRIPT_REWRITE_INVALID",
                UserRequest = "x",
                StartedAt = DateTime.UtcNow
            });
            f.Db.StepChangeRequests.Add(new StepChangeRequest
            {
                Id = Guid.NewGuid(),
                ProjectId = f.Project.Id,
                OrganizationId = f.Project.OrganizationId,
                SourceExecutionId = f.Run.Id,
                TargetStepOrder = 1,
                StepType = "Agent",
                ExecutionId = failedRun,
                WorkflowDefinitionId = f.Workflow.Id,
                RequestedByUserId = f.User,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2)
            });
        }
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        StepChangeRequestResult r = await f.Request();

        r.StatusCode.Should().Be(429, "failed (refunded) change requests keep counting");
        r.Code.Should().Be("REVISION_RATE_LIMITED");
    }

    // ------------------------------------------------------------------ telemetry

    private sealed class CounterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public List<(long Value, string StepType, string Outcome)> Seen { get; } = [];

        public CounterCapture()
        {
            _listener.InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == ChangeRequestDiagnostics.MeterName && i.Name == "reelbolt.inference.change_requests") l.EnableMeasurementEvents(i);
            };
            _listener.SetMeasurementEventCallback<long>((_, v, tags, _) =>
            {
                string st = "", oc = "";
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    if (tag.Key == "step_type") st = (string)tag.Value!;
                    else if (tag.Key == "outcome") oc = (string)tag.Value!;
                    else throw new InvalidOperationException("unexpected tag " + tag.Key);
                }

                lock (Seen) Seen.Add((v, st, oc));
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task A_created_request_counts_created_with_the_step_type()
    {
        using Fixture f = new(new Estimator());
        using CounterCapture cap = new();
        StepChangeRequestResult r = await f.Request();

        r.StatusCode.Should().Be(201);
        cap.Seen.Should().Contain(m => m.Outcome == "created" && m.StepType == "Voiceover" && m.Value == 1);
    }

    [Fact]
    public async Task A_rate_limited_request_counts_refused_rate_limit()
    {
        using Fixture f = new(new Estimator(), maxPerHour: 1);
        await f.Request();
        f.Db.ChangeTracker.Clear();
        using CounterCapture cap = new();
        StepChangeRequestResult r = await f.Request();

        r.StatusCode.Should().Be(429);
        cap.Seen.Should().Contain(m => m.Outcome == "refused_rate_limit" && m.Value == 1);
    }

    // ------------------------------------------------------------------ changing a change

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task SeedPriorAsync(Fixture f, string overridesJson)
    {
        f.Db.StepChangeRequests.Add(new StepChangeRequest
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OrganizationId = f.Project.OrganizationId,
            SourceExecutionId = Guid.NewGuid(),
            TargetStepOrder = 3,
            StepType = "Voiceover",
            ExecutionId = f.Run.Id,
            WorkflowDefinitionId = f.Workflow.Id,
            OverridesJson = overridesJson,
            RequestedByUserId = f.User,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
    }

    private static async Task<JsonElement> StoredOverridesAsync(Fixture f) =>
        J((await f.Db.StepChangeRequests.SingleAsync(r => r.ExecutionId != f.Run.Id)).OverridesJson!);

    [Fact]
    public async Task A_retake_only_request_weighs_the_text_carried_from_the_earlier_request()
    {
        using Fixture f = new(new Estimator());
        await SeedPriorAsync(f, """{"vo0":{"text":"Eight by"}}""");

        StepChangeRequestResult r = await f.Request(new CreateStepChangeRequest(3, null, "", ["vo0"], new() { ["vo0"] = new() { ["speed"] = J("1.1") } }));

        r.StatusCode.Should().Be(201);
        (await f.Db.StepChangeRequests.SingleAsync(x => x.ExecutionId != f.Run.Id)).ScopeFraction
            .Should().Be(0.8, "'Eight by' (8 bytes) replaces 'Hello' (5) next to 'World' (5), carried from the earlier request");
    }

    [Fact]
    public async Task A_speed_only_request_on_a_changed_line_keeps_the_earlier_text()
    {
        using Fixture f = new(new Estimator());
        await SeedPriorAsync(f, """{"vo1":{"text":"Earlier words"}}""");

        StepChangeRequestResult r = await f.Request(new CreateStepChangeRequest(3, null, "", ["vo1"], new() { ["vo1"] = new() { ["speed"] = J("0.9") } }));

        r.StatusCode.Should().Be(201);
        JsonElement stored = await StoredOverridesAsync(f);
        stored.GetProperty("vo1").GetProperty("text").GetString().Should().Be("Earlier words");
        stored.GetProperty("vo1").GetProperty("speed").GetDouble().Should().Be(0.9);
    }

    // QA pin (PR #619 verification, uncommitted): CR1 changed vo1's text (run R1); CR2 on R1 changed ANOTHER step
    // (run R2 = f.Run, Voiceover reused from R1). A speed-only CR3 on R2's vo1 re-makes vo1 from the snapshot's
    // unchanged config, so it must still carry CR1's text ("A speed-only request on a line whose text was changed
    // keeps the text"). The one-hop lookup only reads R2's own row, which targets another step.
    [Fact]
    public async Task Qa_a_speed_only_request_keeps_the_earlier_text_across_an_intervening_change_of_another_step()
    {
        using Fixture f = new(new Estimator());
        Guid r1 = Guid.NewGuid();
        f.Db.StepChangeRequests.Add(new StepChangeRequest
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OrganizationId = f.Project.OrganizationId,
            SourceExecutionId = Guid.NewGuid(),
            TargetStepOrder = 3,
            StepType = "Voiceover",
            ExecutionId = r1,
            WorkflowDefinitionId = f.Workflow.Id,
            OverridesJson = """{"vo1":{"text":"Earlier words"}}""",
            RequestedByUserId = f.User,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        f.Db.StepChangeRequests.Add(new StepChangeRequest
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OrganizationId = f.Project.OrganizationId,
            SourceExecutionId = r1,
            TargetStepOrder = 1,
            StepType = "ThumbnailGenerate",
            ExecutionId = f.Run.Id,
            WorkflowDefinitionId = f.Workflow.Id,
            OverridesJson = """{"*":{"overlayText":"Launch"}}""",
            RequestedByUserId = f.User,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        StepChangeRequestResult r = await f.Request(new CreateStepChangeRequest(3, null, "", ["vo1"], new() { ["vo1"] = new() { ["speed"] = J("0.9") } }));

        r.StatusCode.Should().Be(201);
        JsonElement stored = J((await f.Db.StepChangeRequests.SingleAsync(x => x.ExecutionId != f.Run.Id && x.ExecutionId != r1)).OverridesJson!);
        stored.GetProperty("vo1").TryGetProperty("text", out JsonElement text).Should().BeTrue("CR1's wording must survive CR3");
        text.GetString().Should().Be("Earlier words");
    }

    [Fact]
    public async Task The_new_request_wins_a_key_and_items_outside_its_scope_gain_nothing_nor_change_the_fraction()
    {
        using Fixture f = new(new Estimator());
        await SeedPriorAsync(f, """{"vo0":{"text":"Old zero","speed":1.5},"vo1":{"text":"Old one"}}""");

        await f.Request(new CreateStepChangeRequest(3, null, "", null, new() { ["vo0"] = new() { ["text"] = J("\"Fresh\"") } }));

        JsonElement stored = await StoredOverridesAsync(f);
        stored.GetProperty("vo0").GetProperty("text").GetString().Should().Be("Fresh");
        stored.GetProperty("vo0").GetProperty("speed").GetDouble().Should().Be(1.5);
        stored.TryGetProperty("vo1", out _).Should().BeFalse("vo1 is not in the new request's scope");
        using Fixture alone = new(new Estimator());
        await alone.Request(new CreateStepChangeRequest(3, null, "", null, new() { ["vo0"] = new() { ["text"] = J("\"Fresh\"") } }));
        (await f.Db.StepChangeRequests.SingleAsync(r => r.ExecutionId != f.Run.Id)).ScopeFraction
            .Should().Be((await alone.Db.StepChangeRequests.SingleAsync()).ScopeFraction, "the price follows the new request's named items only");
    }

    [Fact]
    public async Task A_star_request_carries_the_earlier_overrides_of_each_item_it_touched_and_the_estimate_prices_the_same()
    {
        Estimator estimator = new();
        using Fixture f = new(estimator);
        await SeedPriorAsync(f, """{"vo0":{"text":"Old zero"},"*":{"speed":1.2}}""");
        CreateStepChangeRequest body = new(3, null, "", null, new() { ["*"] = new() { ["voiceId"] = J("\"x\"") } });

        StepChangeRequestResult estimated = await f.Service.EstimateAsync(f.Project, f.Run.Id, f.Project.OrganizationId, body, CancellationToken.None);
        IReadOnlyDictionary<int, double>? estimateScales = estimator.LastScales;
        StepChangeRequestResult r = await f.Request(body);

        estimated.StatusCode.Should().Be(200);
        estimator.LastScales.Should().BeEquivalentTo(estimateScales);
        r.StatusCode.Should().Be(201);
        JsonElement stored = await StoredOverridesAsync(f);
        stored.GetProperty("vo0").GetProperty("text").GetString().Should().Be("Old zero");
        stored.GetProperty("*").GetProperty("speed").GetDouble().Should().Be(1.2);
        stored.TryGetProperty("vo1", out _).Should().BeFalse();
    }

    [Fact]
    public void Video_audio_and_thumbnail_keys_are_carried_the_same_way_and_the_item_limit_drops_carried_keys_not_the_request()
    {
        HashSet<string> keys = ["prompt", "durationSec", "text", "retake"];
        ChangeRequestPayload video = ChangeRequestCarryForward.Apply(
            new(null, ["c1"], new Dictionary<string, IReadOnlyDictionary<string, JsonElement>> { ["c1"] = new Dictionary<string, JsonElement> { ["retake"] = J("true") } }),
            """{"c1":{"prompt":"earlier"},"c2":{"prompt":"other"}}""", keys);
        video.Overrides!["c1"].Keys.Should().BeEquivalentTo("retake", "prompt");
        video.Overrides.Should().NotContainKey("c2");

        ChangeRequestPayload audio = ChangeRequestCarryForward.Apply(
            new(null, null, new Dictionary<string, IReadOnlyDictionary<string, JsonElement>> { ["a1"] = new Dictionary<string, JsonElement> { ["durationSec"] = J("8") } }),
            """{"a1":{"prompt":"p"}}""", keys);
        audio.Overrides!["a1"].Keys.Should().BeEquivalentTo("durationSec", "prompt");

        ChangeRequestPayload thumb = ChangeRequestCarryForward.Apply(
            new(null, ["t1"], new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>()),
            """{"t1":{"text":"Title"},"t1x":{"bogus":"1"}}""", keys);
        thumb.Overrides!["t1"]["text"].GetString().Should().Be("Title");

        Dictionary<string, IReadOnlyDictionary<string, JsonElement>> many = [];
        for (int i = 0; i < StepChangeCapabilities.MaxItems; i++)
            many[$"i{i}"] = new Dictionary<string, JsonElement> { ["prompt"] = J("\"n\"") };
        ChangeRequestPayload full = ChangeRequestCarryForward.Apply(new(null, ["extra"], many), """{"extra":{"prompt":"old"}}""", keys);
        full.Overrides!.Count.Should().Be(StepChangeCapabilities.MaxItems);
        full.Overrides.Should().NotContainKey("extra");
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public async Task Validator_errors_surface_as_400_with_paths_and_write_nothing()
    {
        using Fixture f = new();
        CreateStepChangeRequest bad = new(3, null, null, ["nope"], new() { ["vo0"] = new() { ["colour"] = JsonDocument.Parse("\"red\"").RootElement.Clone() } });

        StepChangeRequestResult result = await f.Request(bad);

        result.StatusCode.Should().Be(400);
        result.Extra.Should().ContainKey("errors");
        string errors = JsonSerializer.Serialize(result.Extra!["errors"]);
        errors.Should().Contain("itemIds[0]").And.Contain("overrides.vo0.colour");
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.CompositionReplay)).Should().Be(0);
    }

    [Fact]
    public async Task An_unsupported_step_is_400_with_the_reason()
    {
        using Fixture f = new();

        StepChangeRequestResult result = await f.Request(new CreateStepChangeRequest(5, null, null, null, null));

        result.StatusCode.Should().Be(400);
        JsonSerializer.Serialize(result.Extra!["errors"]).Should().Contain("stepType");
    }

    // ------------------------------------------------------------------ plan, snapshot, queue

    [Fact]
    public async Task An_accepted_request_plans_snapshots_and_queues_its_own_run()
    {
        using Fixture f = new();

        StepChangeRequestResult result = await f.Request();

        result.StatusCode.Should().Be(201);
        StepChangeRequestResponse created = result.Created!;
        created.Status.Should().Be("Queued");
        created.ItemIds.Should().Equal("vo0");
        created.Replay!.Steps.Select(s => (s.StepOrder, s.Disposition)).Should().Equal(
            (1, "Reuse"), (2, "Reuse"), (3, "Revise"), (4, "Reuse"), (5, "Run"), (6, "Run"));
        created.Plan.Should().Be(new StepChangePlanSummaryResponse(3, 3, 0));
        created.SourceWorkflowDefinitionId.Should().Be(f.Run.WorkflowDefinitionId, "the source run opens under its own workflow, not the hidden snapshot");
        created.SourceWorkflowDefinitionId.Should().NotBe(created.WorkflowDefinitionId);

        StepChangeRequest row = await f.Db.StepChangeRequests.SingleAsync();
        row.SourceExecutionId.Should().Be(f.Run.Id);
        row.TargetStepOrder.Should().Be(3);
        row.TargetStepResultId.Should().Be(f.Results[3].Id);
        row.StepType.Should().Be("Voiceover");
        row.ExecutionId.Should().Be(created.ExecutionId);
        row.OverridesJson.Should().Contain("New line");
        CompositionReplayPlan.TryParse(row.ReplayPlanJson)!.TargetStepType.Should().Be("Voiceover");

        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == created.ExecutionId);
        queued.WorkflowDefinitionId.Should().Be(row.WorkflowDefinitionId);
        queued.UserRequest.Should().Be("A reel");

        List<WorkflowStep> clones = await f.Db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == row.WorkflowDefinitionId).OrderBy(s => s.StepOrder).ToListAsync();
        clones.Select(s => s.StepOrder).Should().Equal(1, 2, 3, 4, 5, 6);
        clones.Single(s => s.StepOrder == 3).CacheMode.Should().Be(StepCacheMode.Never);
        clones.Where(s => s.StepOrder != 3).Select(s => s.CacheMode).Should().OnlyContain(m => m == null, "every other step is cloned untouched");
        (await f.Db.WorkflowDefinitions.SingleAsync(w => w.Id == row.WorkflowDefinitionId)).Kind.Should().Be(WorkflowDefinitionKind.CompositionReplay);
    }

    [Fact]
    public async Task A_refused_queue_removes_the_row_and_the_snapshot()
    {
        using Fixture f = new();
        f.Bus.Setup(b => b.Publish(It.IsAny<object>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("bus down"));
        f.Bus.Setup(b => b.Publish(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("bus down"));

        Func<Task> act = () => f.Request();

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.CompositionReplay)).Should().Be(0);
    }

    // ------------------------------------------------------------------ price

    [Fact]
    public async Task A_refused_preflight_is_402_with_the_revision_body_and_queues_nothing()
    {
        using Fixture f = new(new Estimator(refuse: true));

        StepChangeRequestResult result = await f.Request();

        result.StatusCode.Should().Be(402);
        result.Code.Should().Be("INSUFFICIENT_CREDITS");
        result.Extra!.Keys.Should().Contain(["required", "balance", "missing", "low", "high", "hasHistory"]);
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_estimate_is_returned_with_the_request_and_prices_revise_plus_run_steps()
    {
        using Fixture f = new(new Estimator());

        StepChangeRequestResponse created = (await f.Request()).Created!;

        created.Estimate.Should().NotBeNull();
        created.Estimate!.Affordable.Should().BeTrue();
        created.Estimate.RequiredCredits.Should().BeGreaterThan(0);
        created.Estimate.HighCredits.Should().BeGreaterThanOrEqualTo(created.Estimate.LowCredits);
    }

    [Fact]
    public async Task The_estimate_endpoint_writes_nothing()
    {
        using Fixture f = new(new Estimator());

        StepChangeRequestResult result = await f.Service.EstimateAsync(f.Project, f.Run.Id, f.Project.OrganizationId, Fixture.Voice(), CancellationToken.None);

        result.StatusCode.Should().Be(200);
        result.Preview!.Estimate.Should().NotBeNull();
        result.Preview.Replay.Steps.Should().HaveCount(6);
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.CompositionReplay)).Should().Be(0);
        (await f.Db.WorkflowExecutions.CountAsync()).Should().Be(1);
        (await f.Db.WorkflowSteps.CountAsync()).Should().Be(6);
    }

    [Fact]
    public async Task The_estimate_validates_like_the_request()
    {
        using Fixture f = new(new Estimator());

        (await f.Service.EstimateAsync(f.Project, f.Run.Id, f.Project.OrganizationId, new CreateStepChangeRequest(3, null, null, ["nope"], null), CancellationToken.None))
            .StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task The_estimate_prices_exactly_the_revise_and_run_steps_and_the_402_requires_the_same_figure()
    {
        // Fake estimator: 1 credit low / 3 high / 3 required per priced step. Plan: 3 Revise, 5 Run, 6 Run;
        // 1, 2, 4 Reuse. So exactly three steps are priced: low 3, high 9, required 9.
        using Fixture f = new(new Estimator());
        StepChangeEstimateResponse estimate =
            (await f.Service.EstimateAsync(f.Project, f.Run.Id, f.Project.OrganizationId, Fixture.Voice(), CancellationToken.None)).Preview!.Estimate!;

        estimate.LowCredits.Should().Be(3);
        estimate.HighCredits.Should().Be(9);
        estimate.RequiredCredits.Should().Be(9);

        using Fixture refused = new(new Estimator(refuse: true));
        StepChangeRequestResult result = await refused.Request();
        result.StatusCode.Should().Be(402);
        result.Extra!["required"].Should().Be(estimate.RequiredCredits, "the 402 holds the same figure the estimate showed");
    }

    private static IServiceProvider SpeechServices(long millisPerByte)
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "tts", InferenceProviderKind.FishAudio, "", "s1", "k", 60, AccountId: Guid.NewGuid()));
        Mock<IModelPriceService> prices = new();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)r.Quantity * millisPerByte, 0m, Guid.NewGuid()));
        return new ServiceCollection().AddSingleton(resolver.Object).AddSingleton(prices.Object).BuildServiceProvider();
    }

    [Fact]
    public async Task The_estimate_carries_the_exact_price_of_the_redone_line()
    {
        using Fixture f = new(new Estimator(), services: SpeechServices(1_000));
        StepChangeEstimateResponse estimate =
            (await f.Service.EstimateAsync(f.Project, f.Run.Id, f.Project.OrganizationId, Fixture.Voice(), CancellationToken.None)).Preview!.Estimate!;

        estimate.TargetExactCredits.Should().Be(8, "the new line is 8 UTF-8 bytes at 1 credit per byte");
        estimate.TargetExactNote.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_exact_price_never_exceeds_the_range_top()
    {
        using Fixture f = new(new Estimator(), services: SpeechServices(5_000));
        StepChangeEstimateResponse estimate =
            (await f.Service.EstimateAsync(f.Project, f.Run.Id, f.Project.OrganizationId, Fixture.Voice(), CancellationToken.None)).Preview!.Estimate!;

        estimate.TargetExactCredits.Should().Be(estimate.HighCredits);
    }

    [Fact]
    public async Task Without_prices_the_exact_price_is_null()
    {
        using Fixture none = new(new Estimator());
        (await none.Service.EstimateAsync(none.Project, none.Run.Id, none.Project.OrganizationId, Fixture.Voice(), CancellationToken.None))
            .Preview!.Estimate!.TargetExactCredits.Should().BeNull();
    }

    // ------------------------------------------------------------------ rate limit

    [Fact]
    public async Task Revisions_and_change_requests_share_one_hourly_bucket()
    {
        using Fixture f = new(maxPerHour: 2);
        f.Db.CompositionRevisions.Add(new CompositionRevision
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OrganizationId = f.Project.OrganizationId,
            WorkspaceId = Guid.NewGuid(),
            SourceExecutionId = f.Run.Id,
            ExecutionId = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            Instruction = "x",
            RequestedByUserId = f.User,
            CreatedAt = DateTime.UtcNow
        });
        await f.Db.SaveChangesAsync();

        (await f.Request()).StatusCode.Should().Be(201);
        StepChangeRequestResult limited = await f.Request();

        limited.StatusCode.Should().Be(429);
        limited.Code.Should().Be("REVISION_RATE_LIMITED");
        limited.Extra!["used"].Should().Be(2);
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_change_request_counts_against_the_revision_limit_too()
    {
        using Fixture f = new(maxPerHour: 1);
        (await f.Request()).StatusCode.Should().Be(201);

        List<DateTime> recent = await RevisionRateWindow.RecentAsync(f.Db, f.Project.Id, f.User, CancellationToken.None);

        RevisionRateWindow.Refusal(recent, 1).Should().NotBeNull();
        (await RevisionRateWindow.RecentAsync(f.Db, f.Project.Id, Guid.NewGuid(), CancellationToken.None)).Should().BeEmpty();
    }

    // ------------------------------------------------------------------ capabilities and list

    [Fact]
    public async Task Capabilities_list_voiceover_supported_with_items_and_agent_not_supported_with_a_reason()
    {
        using Fixture f = new();

        StepChangeCapabilitiesResponse caps = (await f.Service.GetCapabilitiesAsync(f.Project, f.Run.Id, CancellationToken.None))!;

        caps.RunFinished.Should().BeTrue();
        StepChangeStepCapabilityResponse voice = caps.Steps.Single(s => s.StepOrder == 3);
        voice.Supported.Should().BeTrue();
        voice.StepResultId.Should().Be(f.Results[3].Id);
        voice.ItemKind.Should().Be("Line");
        voice.Items.Select(i => i.Id).Should().Equal("vo0", "vo1");
        voice.Overrides.Select(o => o.Key).Should().Contain(["text", "voiceId", "speed", "pronunciation"]);
        voice.Route.Should().BeNull();
        voice.Overrides.Select(o => o.Type).Should().Equal("Text", "Text", "Number", "PronunciationList");
        voice.Overrides.Should().OnlyContain(o => o.Values == null, "only an Enum key carries its accepted names");

        // Step 1 is a ScriptwriterAgent step: supported by scene (its fixture output has no scenes), flagged as the costliest change.
        StepChangeStepCapabilityResponse agent = caps.Steps.Single(s => s.StepOrder == 1);
        agent.Supported.Should().BeTrue();
        agent.ItemKind.Should().Be("Scene");
        agent.CostNote.Should().Be("script");
        agent.Items.Should().BeEmpty();
        agent.Overrides.Select(o => o.Key).Should().Equal("voiceover", "onScreenText", "visualDescription");
        voice.CostNote.Should().BeNull();
    }

    [Fact]
    public void The_override_response_carries_the_enum_names_and_the_flag_and_enum_type_names()
    {
        // The mapping the capabilities route applies to every catalogue entry (StepChangeRequestService.GetCapabilitiesAsync).
        StepChangeCapability thumb = StepChangeCapabilities.For(StepType.ThumbnailGenerate);
        StepChangeOverrideResponse[] mapped = [.. thumb.Overrides.Select(o => new StepChangeOverrideResponse(
            o.Key, o.Type.ToString(), o.Min, o.Max, o.MaxLength, o.MaxEntries, o.Values is null ? null : [.. o.Values]))];

        StepChangeOverrideResponse position = mapped.Single(o => o.Key == "overlayPosition");
        position.Type.Should().Be("Enum");
        position.Values.Should().Contain(["None", "LowerThird", "UpperThird", "Center"]);
        mapped.Single(o => o.Key == "overlayFontColor").Values.Should().Equal("white", "black", "yellow");
        mapped.Single(o => o.Key == "atTimeSec").Values.Should().BeNull();
        StepChangeCapabilities.For(StepType.VideoGenerate).Overrides.Single(o => o.Key == "retake").Type.ToString().Should().Be("Flag");
    }

    [Fact]
    public async Task A_script_step_lists_its_scenes_and_a_request_is_validated_against_them()
    {
        using Fixture f = new();
        WorkflowStepResult script = await f.Db.WorkflowStepResults.SingleAsync(r => r.Id == f.Results[1].Id);
        script.OutputJson = """{"title":"T","durationInSeconds":20,"scenes":[{"sceneId":"sc1","startTime":0,"duration":10,"voiceover":"One.","onScreenText":[],"visualDescription":"a"},{"sceneId":"sc2","startTime":10,"duration":10,"voiceover":"Two.","onScreenText":[],"visualDescription":"b"}],"narrative":"n"}""";
        await f.Db.SaveChangesAsync();

        StepChangeStepCapabilityResponse step = (await f.Service.GetCapabilitiesAsync(f.Project, f.Run.Id, CancellationToken.None))!.Steps.Single(s => s.StepOrder == 1);

        step.Items.Select(i => i.Id).Should().Equal("sc1", "sc2");
        step.ItemKind.Should().Be("Scene");
        step.CostNote.Should().Be("script");
        StepChangeRequestResult unknownScene = await f.Request(new CreateStepChangeRequest(1, null, "calmer", ["sc9"], null));
        unknownScene.Code.Should().NotBe("USE_COMPOSITION_REVISION");
        unknownScene.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task The_film_step_reports_the_composition_revision_route()
    {
        using Fixture f = new();
        WorkflowStepResult film = await f.Db.WorkflowStepResults.SingleAsync(r => r.Id == f.Results[1].Id);
        film.OutputStorageKey = $"projects/{f.Project.Id}/outputFiles/{f.Run.Id}/film.mp4";
        await f.Db.SaveChangesAsync();

        StepChangeStepCapabilityResponse step = (await f.Service.GetCapabilitiesAsync(f.Project, f.Run.Id, CancellationToken.None))!.Steps.Single(s => s.StepOrder == 1);

        step.Route.Should().Be("composition-revision");
        step.Supported.Should().BeTrue();
        (await f.Request(new CreateStepChangeRequest(1, null, null, null, null))).Code.Should().Be("USE_COMPOSITION_REVISION");
    }

    [Fact]
    public async Task The_list_shows_requests_with_the_runs_own_status_and_cost()
    {
        using Fixture f = new(new Estimator());
        StepChangeRequestResponse created = (await f.Request()).Created!;
        WorkflowExecution own = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == created.ExecutionId);
        own.Status = ExecutionStatus.Passed;
        f.Db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = own.Id,
            WorkflowStepId = f.Steps[2].Id,
            Status = StepStatus.Completed,
            Output = "{}",
            CreditsChargedMillis = 1500
        });
        await f.Db.SaveChangesAsync();

        StepChangeRequestListResponse list = (await f.Service.ListAsync(f.Project, f.Run.Id, CancellationToken.None))!;
        StepChangeRequestListResponse own2 = (await f.Service.ListAsync(f.Project, created.ExecutionId, CancellationToken.None))!;

        StepChangeRequestResponse item = list.Requests.Should().ContainSingle().Subject;
        item.Status.Should().Be("Passed");
        item.CostCredits.Should().Be(1.5);
        item.Plan.Should().Be(new StepChangePlanSummaryResponse(3, 3, 0));
        item.SourceWorkflowDefinitionId.Should().Be(f.Run.WorkflowDefinitionId);
        own2.Requests.Single().SourceWorkflowDefinitionId.Should().Be(f.Run.WorkflowDefinitionId);
        own2.Requests.Should().ContainSingle("a request is also listed from its own run");
    }

    // ------------------------------------------------------------------ controller

    [Fact]
    public void The_controller_is_authorized_and_routes_the_four_actions()
    {
        typeof(StepChangeRequestsController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        typeof(StepChangeRequestsController).GetCustomAttribute<RouteAttribute>()!.Template
            .Should().Be("api/v1/projects/{projectId:guid}/executions/{executionId:guid}");
        typeof(StepChangeRequestsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().SingleOrDefault()?.Template)
            .Should().BeEquivalentTo("change-capabilities", "change-requests", "change-requests/estimate", "change-requests");
    }

    [Fact]
    public async Task The_controller_resolves_the_project_first_404_across_orgs_and_403_for_view_only()
    {
        Mock<IProjectAccessService> access = new();
        access.Setup(a => a.GetAsync(It.IsAny<Guid>(), ProjectPermission.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectAccessResult(ProjectAccessOutcome.Found, new Project()));
        access.Setup(a => a.GetAsync(It.IsAny<Guid>(), ProjectPermission.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectAccessResult(ProjectAccessOutcome.Forbidden, null));
        Mock<ReelBolt.Shared.Auth.ICurrentUser> user = new();
        user.SetupGet(u => u.EmailVerified).Returns(true);
        Mock<IStepChangeRequestService> service = new(MockBehavior.Strict);
        StepChangeRequestsController c = new(access.Object, service.Object, user.Object);

        (await c.Create(Guid.NewGuid(), Guid.NewGuid(), Fixture.Voice(), CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await c.Estimate(Guid.NewGuid(), Guid.NewGuid(), Fixture.Voice(), CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();

        access.Setup(a => a.GetAsync(It.IsAny<Guid>(), ProjectPermission.View, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectAccessResult(ProjectAccessOutcome.NotFound, null));
        (await c.Capabilities(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await c.List(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        service.VerifyNoOtherCalls();
    }
}
