using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Compositions.ReplayShapes;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// Several changes in ONE request (docs/change-requests.md "Several changes in one request"): one plan, one snapshot,
/// one preflight, one rate-limit hit, one run, one row per target; the film as one of the targets adds a revision row.
/// Workflow: 1-2 scripts, 3 and 4 voiceovers, 5 the film (Author, renders), 6 a review.
/// </summary>
public class MultiTargetServiceTests
{
    private const string VoiceoverOutput = """{"view":{"lines":[{"id":"vo0","status":"ok","durationSec":2.5,"text":"Hello"},{"id":"vo1","status":"ok","durationSec":3,"text":"World"}]}}""";

    private sealed class CountingEstimator : IWorkflowCostEstimator
    {
        public int Preflights { get; private set; }

        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(Estimate(steps));

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
        {
            Preflights++;
            return Task.FromResult(new WorkflowPreflightResult(WorkflowPreflightOutcome.Allowed, Estimate(steps), 50_000));
        }

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            EstimateAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            PreflightAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);

        public IReadOnlyDictionary<int, double>? LastScales { get; private set; }

        public List<int> LastCharged { get; } = [];

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, int? outputResolution, IReadOnlyDictionary<int, double>? stepScales, CancellationToken ct = default)
        {
            LastScales = stepScales;
            LastCharged.Clear();
            LastCharged.AddRange(chargedStepOrders);
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
        public WorkflowExecution Run { get; }

        public Fixture(CountingEstimator? estimator = null, int maxPerHour = 10, bool withReviser = true)
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase("multi-change-" + Guid.NewGuid().ToString("N")).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = User, OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);

            List<WorkflowStep> steps =
            [
                Step(1, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.FullWorkflow),
                Step(2, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.FullWorkflow),
                Step(3, AgentType.VideoTransform, StepType.Voiceover, voiceover: """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":2}}"""),
                Step(4, AgentType.VideoTransform, StepType.Voiceover, voiceover: """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":2}}"""),
                Step(5, AgentType.AuthorAgent, mode: AgentInputContextMode.FullWorkflow),
                Step(6, AgentType.VideoReviewAgent, StepType.ReviewLoop, mode: AgentInputContextMode.FullWorkflow, loopTarget: 5)
            ];
            Db.AgentDefinitions.AddRange(steps.Select(s => s.AgentDefinition));
            if (withReviser)
                Db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "Reviser", AgentType = AgentType.CompositionReviser, IsBuiltIn = true });

            WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Voiceover film", ProjectId = Project.Id };
            Db.WorkflowDefinitions.Add(workflow);
            foreach (WorkflowStep step in steps)
                step.WorkflowDefinitionId = workflow.Id;
            Db.WorkflowSteps.AddRange(steps);

            Run = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                ProjectId = Project.Id,
                OrganizationId = Project.OrganizationId,
                Status = ExecutionStatus.Passed,
                UserRequest = "A reel",
                StartedAt = DateTime.UtcNow.AddHours(-1),
                CompletedAt = DateTime.UtcNow
            };
            Db.WorkflowExecutions.Add(Run);
            Db.WorkflowStepResults.AddRange(steps.Select(s => new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = Run.Id,
                WorkflowStepId = s.Id,
                Status = StepStatus.Completed,
                ExecutedAt = DateTime.UtcNow.AddMinutes(-60 + s.StepOrder),
                Output = "{}",
                OutputJson = s.StepType == StepType.Voiceover ? VoiceoverOutput : null,
                OutputStorageKey = s.StepOrder == 5 ? $"projects/{Project.Id}/outputFiles/film.mp4" : null
            }));

            CompositionWorkspace workspace = new() { Id = Guid.NewGuid(), ProjectId = Project.Id, OrganizationId = Project.OrganizationId, OriginExecutionId = Run.Id, LatestVersionNumber = 1 };
            Db.CompositionWorkspaces.Add(workspace);
            Db.CompositionWorkspaceVersions.Add(new CompositionWorkspaceVersion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                ProjectId = Project.Id,
                VersionNumber = 1,
                StorageKey = "k",
                CreatedByExecutionId = Run.Id,
                RenderOutputStorageKey = "r"
            });
            Db.SaveChanges();
            Db.ChangeTracker.Clear();

            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CompositionRevisions:MaxPerHourPerUserProject"] = maxPerHour.ToString()
            }).Build();
            WorkflowExecutionService executions = new(Db, Bus.Object, NullLogger<WorkflowExecutionService>.Instance);
            ServiceCollection services = new();
            services.AddSingleton<ICompositionRevisionService>(new CompositionRevisionService(
                Db, executions, config, NullLogger<CompositionRevisionService>.Instance, estimator));
            Service = new StepChangeRequestService(Db, executions, config, NullLogger<StepChangeRequestService>.Instance, estimator, services: services.BuildServiceProvider());
        }

        public static StepChangeTargetRequest Voice(int step, string id, string text) => new(
            step, null, "Say it differently", [id],
            new() { [id] = new() { ["text"] = JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone() } });

        public static StepChangeTargetRequest Film(string instruction = "Make the title bigger") => new(5, null, instruction, null, null);

        public static CreateStepChangeRequest Many(params StepChangeTargetRequest[] targets) => new(null, null, null, null, null, targets);

        public Task<StepChangeRequestResult> Request(CreateStepChangeRequest body) =>
            Service.RequestAsync(Project, Run.Id, User, Project.OrganizationId, body, CancellationToken.None);

        public void Dispose() => Db.Dispose();
    }

    private static IEnumerable<string> Paths(StepChangeRequestResult r) =>
        ((IEnumerable<object>)r.Extra!["errors"]!).Select(e => (string)e.GetType().GetProperty("path")!.GetValue(e)!);

    [Fact]
    public async Task Two_voiceover_targets_make_one_run_two_rows_one_plan_and_one_preflight()
    {
        CountingEstimator estimator = new();
        using Fixture f = new(estimator);

        StepChangeRequestResult r = await f.Request(Fixture.Many(Fixture.Voice(4, "vo1", "B line"), Fixture.Voice(3, "vo0", "A line")));

        r.StatusCode.Should().Be(201);
        List<StepChangeRequest> rows = await f.Db.StepChangeRequests.OrderBy(x => x.TargetStepOrder).ToListAsync();
        rows.Select(x => x.TargetStepOrder).Should().Equal(3, 4);
        rows.Select(x => x.ExecutionId).Distinct().Should().HaveCount(1);
        rows.Select(x => x.ReplayPlanJson).Distinct().Should().HaveCount(1);
        rows[0].ScopeJson.Should().Be("""["vo0"]""");
        rows[1].ScopeJson.Should().Be("""["vo1"]""");
        CompositionReplayPlan plan = CompositionReplayPlan.TryParse(rows[0].ReplayPlanJson)!;
        plan.TargetOrders.Should().Equal(3, 4);
        plan.For(3)!.Disposition.Should().Be(ReplayStepDisposition.Revise);
        plan.For(4)!.Disposition.Should().Be(ReplayStepDisposition.Revise);
        estimator.Preflights.Should().Be(1);
        estimator.LastScales!.Keys.Should().BeEquivalentTo([3, 4]);
        (await f.Db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.CompositionReplay)).Should().Be(1);
        (await f.Db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == rows[0].WorkflowDefinitionId && (s.StepOrder == 3 || s.StepOrder == 4)).ToListAsync())
            .Should().OnlyContain(s => s.CacheMode == StepCacheMode.Never);
        (await f.Db.CompositionRevisions.CountAsync()).Should().Be(0, "no film target, no revision row");

        StepChangeRequestResponse created = r.Created!;
        created.TargetStepOrder.Should().Be(3, "the top-level fields come from the first target (lowest step order)");
        created.Targets!.Select(t => (t.StepOrder, t.Kind)).Should().Equal((3, "step"), (4, "step"));
        created.Targets![1].ItemIds.Should().Equal("vo1");
    }

    [Fact]
    public async Task One_multi_target_request_is_one_rate_limit_hit()
    {
        using Fixture f = new(maxPerHour: 2);

        (await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Voice(4, "vo1", "B")))).StatusCode.Should().Be(201);
        (await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "C")))).StatusCode.Should().Be(201, "the first request counted once, not twice");
        (await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "D")))).StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task A_one_element_targets_array_behaves_as_the_single_body()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A")));

        r.StatusCode.Should().Be(201);
        CompositionReplayPlan.TryParse((await f.Db.StepChangeRequests.SingleAsync()).ReplayPlanJson)!.RevisedStepOrders.Should().BeNull("one target serializes as today");
    }

    [Fact]
    public async Task Validation_reports_each_target_under_its_own_path_and_refuses_bad_shapes()
    {
        using Fixture f = new();
        StepChangeTargetRequest bad = new(4, null, "x", ["vo1"], new() { ["vo1"] = new() { ["nope"] = JsonDocument.Parse("1").RootElement.Clone() } });

        StepChangeRequestResult invalid = await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), bad));
        invalid.StatusCode.Should().Be(400);
        Paths(invalid).Should().NotBeEmpty().And.OnlyContain(p => p.StartsWith("targets[1]."));

        (await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Voice(3, "vo1", "B")))).StatusCode.Should().Be(400, "two targets on the same step");
        (await f.Request(Fixture.Many())).StatusCode.Should().Be(400, "empty");
        (await f.Request(Fixture.Many([.. Enumerable.Repeat(Fixture.Voice(3, "vo0", "A"), 7)]))).StatusCode.Should().Be(400, "more than 6");

        StepChangeRequestResult mixed = await f.Request(new CreateStepChangeRequest(3, null, null, null, null, [Fixture.Voice(4, "vo1", "B")]));
        mixed.StatusCode.Should().Be(400);
        Paths(mixed).Should().Equal("targets");

        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_film_and_a_voiceover_make_one_revision_row_and_two_change_rows_in_one_run()
    {
        CountingEstimator estimator = new();
        using Fixture f = new(estimator);

        StepChangeRequestResult r = await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Film()));

        r.StatusCode.Should().Be(201, r.Message);
        List<StepChangeRequest> rows = await f.Db.StepChangeRequests.OrderBy(x => x.TargetStepOrder).ToListAsync();
        rows.Select(x => x.TargetStepOrder).Should().Equal(3, 5);
        rows[1].Instruction.Should().Be("Make the title bigger");
        CompositionRevision revision = await f.Db.CompositionRevisions.SingleAsync();
        revision.ExecutionId.Should().Be(rows[0].ExecutionId).And.Be(rows[1].ExecutionId);
        revision.WorkflowDefinitionId.Should().Be(rows[0].WorkflowDefinitionId);
        revision.ReplayPlanJson.Should().Be(rows[0].ReplayPlanJson);
        revision.CutPolicy.Should().Be(CompositionCutPolicy.KeepCut);
        revision.Mode.Should().Be(CompositionRevisionMode.Render);
        revision.Instruction.Should().Be("Make the title bigger");

        List<WorkflowStep> snapshot = await f.Db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == revision.WorkflowDefinitionId).OrderBy(s => s.StepOrder).ToListAsync();
        snapshot.Single(s => s.StepOrder == 5).StepType.Should().Be(StepType.Revise);
        snapshot.Single(s => s.StepOrder == 3).CacheMode.Should().Be(StepCacheMode.Never);
        estimator.Preflights.Should().Be(1);
        estimator.LastCharged.Should().BeEquivalentTo([3, 5], "both targets hold; the review only reviews targets and is skipped");
        estimator.LastScales!.Keys.Should().Equal([3], "the film is priced whole");
        r.Created!.Targets!.Select(t => t.Kind).Should().Equal("step", "film");

        StepChangeRequestListResponse list = (await f.Service.ListAsync(f.Project, f.Run.Id, CancellationToken.None))!;
        list.Requests.Should().ContainSingle("one entry per request, not per row");
        list.Requests[0].Targets!.Select(t => (t.StepOrder, t.Kind)).Should().Equal((3, "step"), (5, "film"));
    }

    [Fact]
    public async Task The_film_alone_is_still_409_in_either_shape_and_a_missing_workspace_is_refused()
    {
        using Fixture f = new();
        StepChangeRequestResult single = await f.Request(new CreateStepChangeRequest(5, null, "bigger", null, null));
        single.StatusCode.Should().Be(409);
        single.Code.Should().Be("USE_COMPOSITION_REVISION");
        (await f.Request(Fixture.Many(Fixture.Film()))).Code.Should().Be("USE_COMPOSITION_REVISION");

        using Fixture noReviser = new(withReviser: false);
        StepChangeRequestResult r = await noReviser.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Film()));
        r.StatusCode.Should().Be(503);
        r.Code.Should().Be("REVISION_AGENT_UNAVAILABLE");
        (await noReviser.Db.StepChangeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_film_target_takes_an_instruction_only()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), new StepChangeTargetRequest(5, null, "", ["x"], null)));

        r.StatusCode.Should().Be(400);
        Paths(r).Should().Contain(["targets[1].instruction", "targets[1].itemIds"]);
    }

    [Fact]
    public async Task A_refused_queue_removes_every_row_the_snapshot_and_the_revision()
    {
        using Fixture f = new();
        f.Bus.Setup(b => b.Publish(It.IsAny<object>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("bus down"));
        f.Bus.Setup(b => b.Publish(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("bus down"));

        Func<Task> act = () => f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Film()));

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
        (await f.Db.CompositionRevisions.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.CompositionReplay)).Should().Be(0);
    }

    [Fact]
    public async Task A_foreign_project_gets_404_for_the_multi_request_and_the_estimate_writes_nothing()
    {
        using Fixture f = new();
        Project other = new() { Id = Guid.NewGuid(), Name = "O", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        CreateStepChangeRequest body = Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Voice(4, "vo1", "B"));

        (await f.Service.RequestAsync(other, f.Run.Id, f.User, other.OrganizationId, body, CancellationToken.None)).StatusCode.Should().Be(404);

        StepChangeRequestResult preview = await f.Service.EstimateAsync(f.Project, f.Run.Id, f.Project.OrganizationId, body, CancellationToken.None);
        preview.Preview!.Targets.Should().HaveCount(2);
        preview.Preview.Replay.Steps.Should().NotBeEmpty();
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
        (await f.Db.CompositionRevisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Capabilities_name_the_limit_and_mark_the_film_combinable()
    {
        using Fixture f = new();

        var caps = (await f.Service.GetCapabilitiesAsync(f.Project, f.Run.Id, CancellationToken.None))!;

        caps.MaxTargets.Should().Be(6);
        caps.Steps.Single(s => s.StepOrder == 5).Combinable.Should().BeTrue();
        caps.Steps.Where(s => s.StepOrder != 5).Should().OnlyContain(s => !s.Combinable);
    }

    [Fact]
    public async Task Rate_window_counts_distinct_runs()
    {
        using Fixture f = new();
        Guid run = Guid.NewGuid();
        foreach (int order in new[] { 3, 4 })
        {
            f.Db.StepChangeRequests.Add(new StepChangeRequest
            {
                Id = Guid.NewGuid(),
                ProjectId = f.Project.Id,
                OrganizationId = f.Project.OrganizationId,
                SourceExecutionId = f.Run.Id,
                TargetStepOrder = order,
                StepType = "Voiceover",
                ExecutionId = run,
                WorkflowDefinitionId = Guid.NewGuid(),
                RequestedByUserId = f.User,
                CreatedAt = DateTime.UtcNow
            });
        }

        await f.Db.SaveChangesAsync();

        (await RevisionRateWindow.RecentAsync(f.Db, f.Project.Id, f.User, CancellationToken.None)).Should().HaveCount(1);
    }

    // ---- QA (T-006) gap pins ----

    private static CreateStepChangeRequest Single(StepChangeTargetRequest t) => new(t.StepOrder, t.StepResultId, t.Instruction, t.ItemIds, t.Overrides);

    [Fact]
    public async Task Qa_a_film_target_request_queues_one_run_and_is_one_rate_limit_hit()
    {
        using Fixture f = new(maxPerHour: 2);

        StepChangeRequestResult r = await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Film()));
        r.StatusCode.Should().Be(201, r.Message);
        (await f.Db.WorkflowExecutions.CountAsync(e => e.Id != f.Run.Id)).Should().Be(1, "N targets queue ONE run");
        (await RevisionRateWindow.RecentAsync(f.Db, f.Project.Id, f.User, CancellationToken.None))
            .Should().HaveCount(1, "two change rows and one revision row of the same run are one hit");

        (await f.Request(Single(Fixture.Voice(3, "vo0", "Again")))).StatusCode.Should().Be(201);
        (await f.Request(Single(Fixture.Voice(3, "vo0", "Once more")))).StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task Qa_a_single_target_list_item_carries_a_targets_list_of_one()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Request(Single(Fixture.Voice(3, "vo0", "Hi there")));
        r.StatusCode.Should().Be(201, r.Message);

        StepChangeRequestResponse item = (await f.Service.ListAsync(f.Project, f.Run.Id, CancellationToken.None))!.Requests.Single();
        item.TargetStepOrder.Should().Be(3);
        item.ItemIds.Should().Equal("vo0");
        item.Instruction.Should().Be("Say it differently");
        item.Targets!.Should().ContainSingle();
        item.Targets![0].StepOrder.Should().Be(3);
        item.Targets![0].StepType.Should().Be("Voiceover");
        item.Targets![0].Kind.Should().Be("step");
        item.Targets![0].Instruction.Should().Be("Say it differently");
        item.Targets![0].ItemIds.Should().Equal("vo0");
    }

    [Fact]
    public async Task Qa_a_film_instruction_the_validator_accepts_fits_the_change_row_column()
    {
        // The film target accepts 1..4000 chars (revision rules) and also stores the text on its step_change_requests
        // row, whose column is HasMaxLength(2000). On Postgres a 2001..4000-char film instruction fails the save
        // (22001) instead of answering 400. The in-memory provider does not enforce lengths, so this pins the
        // contract through the model metadata.
        using Fixture f = new();
        int? column = f.Db.Model.FindEntityType(typeof(StepChangeRequest))!.FindProperty(nameof(StepChangeRequest.Instruction))!.GetMaxLength();

        StepChangeRequestResult r = await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Film(new string('a', 3000))));

        if (r.StatusCode == 201)
        {
            StepChangeRequest filmRow = await f.Db.StepChangeRequests.SingleAsync(x => x.TargetStepOrder == 5);
            filmRow.Instruction.Length.Should().BeLessThanOrEqualTo(column ?? int.MaxValue,
                "an accepted film instruction must fit step_change_requests.instruction");
        }
        else
        {
            r.StatusCode.Should().Be(400);
        }
    }

    private const string ScriptOutput = """{"title":"T","durationInSeconds":30,"scenes":[{"sceneId":"sc1","voiceover":"One."},{"sceneId":"sc2","voiceover":"Two."}]}""";

    [Fact]
    public async Task A_voiceover_target_downstream_of_a_script_target_is_re_made_whole_with_its_own_overrides()
    {
        CountingEstimator estimator = new();
        using Fixture f = new(estimator);
        (await f.Db.WorkflowStepResults.SingleAsync(r => r.WorkflowStep.StepOrder == 2)).OutputJson = ScriptOutput;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        StepChangeTargetRequest script = new(2, null, "Shorter", ["sc1"], null);

        StepChangeRequestResult r = await f.Request(Fixture.Many(script, Fixture.Voice(3, "vo0", "Say it softly")));

        r.StatusCode.Should().Be(201, r.Message);
        StepChangeRequest voice = await f.Db.StepChangeRequests.SingleAsync(x => x.TargetStepOrder == 3);
        voice.ScopeJson.Should().Be("""["vo0","vo1"]""", "every line is re-synthesized: vo1 was built from the old script");
        voice.ScopeFraction.Should().Be(1d);
        voice.OverridesJson.Should().Contain("Say it softly").And.Contain("vo0");
        estimator.LastScales![3].Should().Be(1d);
    }

    [Fact]
    public async Task Independent_targets_keep_their_narrow_scope()
    {
        using Fixture f = new();

        (await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Voice(4, "vo1", "B")))).StatusCode.Should().Be(201);

        (await f.Db.StepChangeRequests.SingleAsync(x => x.TargetStepOrder == 3)).ScopeJson.Should().Be("""["vo0"]""");
        (await f.Db.StepChangeRequests.SingleAsync(x => x.TargetStepOrder == 4)).ScopeJson.Should().Be("""["vo1"]""");
    }

    [Fact]
    public async Task A_refused_step_in_targets_names_the_entry()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), new StepChangeTargetRequest(99, null, "x", null, null)));

        r.StatusCode.Should().Be(404);
        Paths(r).Should().Equal("targets[1]");
    }

    [Fact]
    public async Task A_film_workspace_created_for_the_request_is_removed_when_the_queue_refuses()
    {
        using Fixture f = new();
        f.Db.CompositionWorkspaces.RemoveRange(await f.Db.CompositionWorkspaces.ToListAsync());
        f.Db.CompositionWorkspaceVersions.RemoveRange(await f.Db.CompositionWorkspaceVersions.ToListAsync());
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        (await f.Db.WorkflowStepResults.SingleAsync(x => x.WorkflowStep.StepOrder == 5)).OutputStorageKey = $"projects/{f.Project.Id}/outputFiles/film.mp4";
        await f.Db.SaveChangesAsync();
        f.Bus.Setup(b => b.Publish(It.IsAny<object>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("bus down"));
        f.Bus.Setup(b => b.Publish(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("bus down"));

        Func<Task> act = () => f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Film()));

        await act.Should().ThrowAsync<Exception>();
        (await f.Db.CompositionWorkspaces.CountAsync()).Should().Be(0);
        (await f.Db.CompositionRevisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_script_target_downstream_of_another_target_keeps_the_scenes_the_owner_named()
    {
        using Fixture f = new();
        foreach (var r in await f.Db.WorkflowStepResults.Where(x => x.WorkflowStep.StepOrder == 1 || x.WorkflowStep.StepOrder == 2).ToListAsync())
            r.OutputJson = ScriptOutput;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        StepChangeRequestResult r1 = await f.Request(Fixture.Many(
            new StepChangeTargetRequest(1, null, "Shorter", ["sc1"], null),
            new StepChangeTargetRequest(2, null, "Warmer", ["sc2"], null)));

        r1.StatusCode.Should().Be(201, r1.Message);
        (await f.Db.StepChangeRequests.SingleAsync(x => x.TargetStepOrder == 2)).ScopeJson.Should().Be("""["sc2"]""",
            "the owner's instruction still applies only to the scene they named");
    }

    [Fact]
    public async Task A_refused_queue_keeps_the_new_workspace_when_something_else_came_to_reference_it()
    {
        using Fixture f = new();
        f.Db.CompositionWorkspaces.RemoveRange(await f.Db.CompositionWorkspaces.ToListAsync());
        f.Db.CompositionWorkspaceVersions.RemoveRange(await f.Db.CompositionWorkspaceVersions.ToListAsync());
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        void Reference()
        {
            Guid ws = f.Db.CompositionWorkspaces.Single().Id;
            f.Db.CompositionWorkspaceVersions.Add(new CompositionWorkspaceVersion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ws,
                ProjectId = f.Project.Id,
                VersionNumber = 1,
                StorageKey = "k"
            });
            f.Db.SaveChanges();
        }

        f.Bus.Setup(b => b.Publish(It.IsAny<object>(), It.IsAny<CancellationToken>())).Callback(Reference).ThrowsAsync(new InvalidOperationException("bus down"));
        f.Bus.Setup(b => b.Publish(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>())).Callback(Reference).ThrowsAsync(new InvalidOperationException("bus down"));

        Func<Task> act = () => f.Request(Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Film()));

        await act.Should().ThrowAsync<Exception>();
        (await f.Db.CompositionWorkspaces.CountAsync()).Should().Be(1, "another row references it now");
        (await f.Db.CompositionRevisions.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------ which film version the change builds on

    /// <summary>A later revision run of the film (status <paramref name="status"/>) that made version 2 as a child of version 1.</summary>
    private static async Task<CompositionWorkspaceVersion> AddChildVersionAsync(Fixture f, ExecutionStatus status, bool rendered = true)
    {
        CompositionWorkspaceVersion v1 = await f.Db.CompositionWorkspaceVersions.SingleAsync();
        WorkflowExecution revisionRun = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = f.Run.WorkflowDefinitionId,
            ProjectId = f.Project.Id,
            OrganizationId = f.Project.OrganizationId,
            Status = status,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        };
        CompositionWorkspaceVersion v2 = new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = v1.WorkspaceId,
            ProjectId = f.Project.Id,
            VersionNumber = 2,
            ParentVersionId = v1.Id,
            StorageKey = "k2",
            CreatedByExecutionId = revisionRun.Id,
            RenderOutputStorageKey = rendered ? "r2" : null
        };
        f.Db.WorkflowExecutions.Add(revisionRun);
        f.Db.CompositionWorkspaceVersions.Add(v2);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        return v2;
    }

    [Fact]
    public async Task A_film_target_builds_on_the_newest_passed_child_version_like_a_revision_and_says_so()
    {
        // Production run 22f79d02: a change request on the origin run rebuilt the film from the run's own version and
        // lost the fix a later revision had made. It now builds on the newest version, as ReviseComposition does.
        using Fixture f = new();
        CompositionWorkspaceVersion v2 = await AddChildVersionAsync(f, ExecutionStatus.Passed);
        CreateStepChangeRequest body = Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Film());

        StepChangeRequestResult preview = await f.Service.EstimateAsync(f.Project, f.Run.Id, f.Project.OrganizationId, body, CancellationToken.None);
        preview.Preview!.FilmBase.Should().Be(new StepChangeFilmBaseResponse(
            2, 1, true, "Builds on version 2, the latest version of the video (this run made version 1)"));

        StepChangeRequestResult r = await f.Request(body);
        r.StatusCode.Should().Be(201, r.Message);
        (await f.Db.CompositionRevisions.SingleAsync()).BaseVersionId.Should().Be(v2.Id);
        r.Created!.FilmBase!.VersionNumber.Should().Be(2);
        r.Created.FilmBase.NewerThanRun.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExecutionStatus.Failed)]
    public async Task Without_a_newer_passed_rendered_version_the_film_target_builds_on_the_runs_own(ExecutionStatus? childStatus)
    {
        using Fixture f = new();
        if (childStatus is { } status)
            await AddChildVersionAsync(f, status);
        Guid own = (await f.Db.CompositionWorkspaceVersions.SingleAsync(v => v.VersionNumber == 1)).Id;
        CreateStepChangeRequest body = Fixture.Many(Fixture.Voice(3, "vo0", "A"), Fixture.Film());

        StepChangeRequestResult preview = await f.Service.EstimateAsync(f.Project, f.Run.Id, f.Project.OrganizationId, body, CancellationToken.None);
        preview.Preview!.FilmBase.Should().Be(new StepChangeFilmBaseResponse(1, 1, false, "Builds on version 1, the version this run made"));

        (await f.Request(body)).StatusCode.Should().Be(201);
        (await f.Db.CompositionRevisions.SingleAsync()).BaseVersionId.Should().Be(own);
    }

    [Fact]
    public async Task A_request_without_the_film_carries_no_film_base()
    {
        using Fixture f = new();
        await AddChildVersionAsync(f, ExecutionStatus.Passed);

        StepChangeRequestResult preview = await f.Service.EstimateAsync(
            f.Project, f.Run.Id, f.Project.OrganizationId, Fixture.Many(Fixture.Voice(3, "vo0", "A")), CancellationToken.None);

        preview.Preview!.FilmBase.Should().BeNull();
    }
}
