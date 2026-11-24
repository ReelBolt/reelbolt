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
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Lane L9 (docs/design/app-inputs.md 10.2, 25): a finished app run redone with app-input values changed, as a new linked
/// run through the change-request machinery. The app: 1 a script agent (reads the request template), 2 "Lite mode?"
/// (input.liteMode: true to 5, false to 3), 3 the full clip (prompt placeholder), 4 "Finish here" (to end), 5 the lite clip.
/// The source run took the full branch (1-4).
/// </summary>
public class AppInputChangeRequestTests
{
    internal static readonly AppManifest App = Manifest(
        Input("videoPrompt", "longText", "What happens", required: true),
        Input("audience", "shortText", "Who it is for"),
        Input("takes", "number", "Takes", min: 1, max: 4, def: "1"),
        Input("liteMode", "toggle", "Lite mode")) with
    {
        RequestTemplate = "Made for {{input.audience}}."
    };

    private const string FullClip =
        """{"planSource":"Inline","inlinePrompt":"{{input.videoPrompt}}","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":4,"maxSpendCredits":5,"appInputs":{"takes":"takes"}}""";

    private const string LiteClip =
        """{"planSource":"Inline","inlinePrompt":"Lite: {{input.videoPrompt}}","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":2,"maxSpendCredits":5}""";

    internal sealed class Estimator : IWorkflowCostEstimator
    {
        public List<int> LastCharged { get; } = [];

        /// <summary>The chosen agent models in effect at the last preflight (docs/design/app-inputs.md 29).</summary>
        public IReadOnlyDictionary<Guid, Guid>? LastAgentModels { get; private set; }

        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowCostEstimate(true, false, steps.Count * 1_000, steps.Count * 3_000, steps.Count * 3_000, []));

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowPreflightResult(WorkflowPreflightOutcome.Allowed,
                new WorkflowCostEstimate(true, false, steps.Count * 1_000, steps.Count * 3_000, steps.Count * 3_000, []), 50_000));

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            EstimateAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
        {
            LastCharged.Clear();
            LastCharged.AddRange(chargedStepOrders);
            LastAgentModels = ReelBolt.Shared.Metering.AppAgentModelAmbient.Current;
            return PreflightAsync(organizationId, [.. runSteps.Where(s => chargedStepOrders.Contains(s.StepOrder))], ct);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        public InferenceApiDbContext Db { get; }
        public Mock<IPublishEndpoint> Bus { get; } = new();
        public Project Project { get; }
        public Guid User { get; } = Guid.NewGuid();
        public StepChangeRequestService Service { get; }
        public WorkflowExecution Run { get; }
        public WorkflowDefinition Workflow { get; }
        public Estimator Costs { get; } = new();

        public Fixture(bool enabled = true, Action<List<WorkflowStep>>? editAfterRun = null, AppManifest? page = null,
            Dictionary<string, JsonElement>? runValues = null, IServiceProvider? services = null, string fullClip = null,
            string mediaEgressBy = null)
        {
            AppManifest app = page ?? App;
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase("app-input-change-" + Guid.NewGuid().ToString("N")).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = User, OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);

            AgentDefinition writer = new() { Id = Guid.NewGuid(), Name = "Scriptwriter", AgentType = AgentType.ScriptwriterAgent, IsBuiltIn = true };
            AgentDefinition transform = new() { Id = Guid.NewGuid(), Name = "Video", AgentType = AgentType.VideoTransform, IsBuiltIn = true };
            Db.AgentDefinitions.AddRange(writer, transform);

            Workflow = new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Avatar clip",
                ProjectId = Project.Id,
                AppManifestJson = AppManifestJson.Serialize(app)
            };
            Db.WorkflowDefinitions.Add(Workflow);
            List<WorkflowStep> authored =
            [
                new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Workflow.Id, StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = writer.Id, Label = "Write the script" },
                new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Workflow.Id, StepOrder = 2, StepType = StepType.Conditional, AgentDefinitionId = transform.Id, Label = "Lite mode?", ConditionExpression = "input.liteMode", TrueBranchStepOrder = "5", FalseBranchStepOrder = "3" },
                new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Workflow.Id, StepOrder = 3, StepType = StepType.VideoGenerate, AgentDefinitionId = transform.Id, Label = "Make the full clip", VideoGenerateConfigJson = fullClip ?? FullClip },
                new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Workflow.Id, StepOrder = 4, StepType = StepType.Conditional, AgentDefinitionId = transform.Id, Label = "Finish here", TrueBranchStepOrder = "end", FalseBranchStepOrder = "end" },
                new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Workflow.Id, StepOrder = 5, StepType = StepType.VideoGenerate, AgentDefinitionId = transform.Id, Label = "Make the lite clip", VideoGenerateConfigJson = LiteClip },
            ];
            foreach (WorkflowStep s in authored)
                s.AgentDefinition = s.AgentDefinitionId == writer.Id ? writer : transform;

            // The run's own snapshot: the authored steps bound with its values, the page copied (as the engine takes it).
            Dictionary<string, JsonElement> values = runValues ?? Values(("videoPrompt", "\"A barista pours latte art\""), ("audience", "\"Baristas\""));
            AppInputGateResult gate = AppInputValueValidator.Evaluate(app, authored, values, "Keep it warm.",
                mediaEgress: mediaEgressBy is null ? null : new MediaEgressGate(false, mediaEgressBy));
            gate.Refused.Should().BeFalse(gate.Message);
            WorkflowDefinition snapshot = new()
            {
                Id = Guid.NewGuid(),
                Name = "Avatar clip",
                ProjectId = Project.Id,
                Kind = WorkflowDefinitionKind.ExecutionSnapshot,
                AppManifestJson = Workflow.AppManifestJson
            };
            Db.WorkflowDefinitions.Add(snapshot);
            List<WorkflowStep> ran = [.. gate.BoundSteps!.Select(s =>
            {
                WorkflowStep copy = WorkflowStepCloner.Clone(s, snapshot.Id);
                copy.AgentDefinition = null!;
                return copy;
            })];
            foreach (WorkflowStep s in authored)
                s.AgentDefinition = null!;
            editAfterRun?.Invoke(authored);
            Db.WorkflowSteps.AddRange(authored);
            Db.WorkflowSteps.AddRange(ran);

            Run = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Workflow.Id,
                SnapshotDefinitionId = snapshot.Id,
                ProjectId = Project.Id,
                OrganizationId = Project.OrganizationId,
                Status = ExecutionStatus.Passed,
                UserRequest = gate.UserRequest,
                RunBriefJson = RunBriefJson.Serialize(new StoredRunBrief(Inputs: gate.Inputs, AllowMediaEgressBy: gate.MediaEgressBy)),
                StartedAt = DateTime.UtcNow.AddHours(-1),
                CompletedAt = DateTime.UtcNow
            };
            Db.WorkflowExecutions.Add(Run);
            Db.WorkflowStepResults.AddRange(ran.Where(s => s.StepOrder <= 4).Select(s => new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = Run.Id,
                WorkflowStepId = s.Id,
                Status = StepStatus.Completed,
                ExecutedAt = DateTime.UtcNow.AddMinutes(-60 + s.StepOrder),
                Output = "{}"
            }));
            Db.SaveChanges();
            Db.ChangeTracker.Clear();

            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CompositionRevisions:MaxPerHourPerUserProject"] = "10",
            }).Build();
            WorkflowExecutionService executions = new(Db, Bus.Object, NullLogger<WorkflowExecutionService>.Instance);
            Service = new StepChangeRequestService(Db, executions, config, NullLogger<StepChangeRequestService>.Instance, Costs,
                services: services ?? new ServiceCollection().BuildServiceProvider());
        }

        public static CreateStepChangeRequest Inputs(params (string Id, string Raw)[] values) =>
            new(null, null, null, null, null, Inputs: Values(values));

        public Task<StepChangeRequestResult> Request(CreateStepChangeRequest body) =>
            Service.RequestAsync(Project, Run.Id, User, Project.OrganizationId, body, CancellationToken.None);

        public Task<StepChangeRequestResult> Estimate(CreateStepChangeRequest body) =>
            Service.EstimateAsync(Project, Run.Id, Project.OrganizationId, body, CancellationToken.None);

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task A_changed_prompt_re_makes_only_the_clip_that_uses_it_as_a_new_linked_run()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Request(Fixture.Inputs(("videoPrompt", "\"A barista draws a heart\"")));

        r.StatusCode.Should().Be(201, r.Message);
        StepChangeRequest row = await f.Db.StepChangeRequests.SingleAsync();
        row.TargetStepOrder.Should().Be(3);
        row.SourceExecutionId.Should().Be(f.Run.Id);
        row.ScopeFraction.Should().Be(1d);
        row.Instruction.Should().Contain("“What happens”").And.Contain("A barista draws a heart");
        AppInputChangeRows.Values(row.OverridesJson)!["videoPrompt"].GetString().Should().Be("A barista draws a heart");

        CompositionReplayPlan plan = CompositionReplayPlan.TryParse(row.ReplayPlanJson)!;
        plan.For(1)!.Disposition.Should().Be(ReplayStepDisposition.Reuse);
        plan.For(2)!.Disposition.Should().Be(ReplayStepDisposition.Reuse);
        plan.For(3)!.Disposition.Should().Be(ReplayStepDisposition.Revise);
        plan.For(4)!.Disposition.Should().Be(ReplayStepDisposition.Run, "a branch is re-evaluated");
        plan.For(5)!.Disposition.Should().Be(ReplayStepDisposition.Skip, "the lite clip is off the run's path");
        f.Costs.LastCharged.Should().NotContain(5).And.Contain(3);

        WorkflowDefinition snapshot = await f.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync(w => w.Id == row.WorkflowDefinitionId);
        snapshot.Kind.Should().Be(WorkflowDefinitionKind.CompositionReplay);
        snapshot.AppManifestJson.Should().NotBeNullOrEmpty("the replay carries the page, for its conditions and agent models");
        WorkflowStep clip = snapshot.Steps.Single(s => s.StepOrder == 3);
        clip.VideoGenerateConfigJson.Should().Contain("A barista draws a heart").And.NotContain("{{input.");
        clip.CacheMode.Should().Be(StepCacheMode.Never);
        snapshot.Steps.Single(s => s.StepOrder == 5).VideoGenerateConfigJson.Should().Contain("Lite: A barista pours latte art");

        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == row.ExecutionId);
        RunBriefJson.TryParse(queued.RunBriefJson)!.Inputs!["videoPrompt"].GetString().Should().Be("A barista draws a heart");
        queued.UserRequest.Should().Be(f.Run.UserRequest, "the prompt is not in the request template");

        StepChangeRequestResponse created = r.Created!;
        created.InputChanges.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            InputId = "videoPrompt",
            Label = "What happens",
            Before = "A barista pours latte art",
            After = "A barista draws a heart"
        });
        created.Targets!.Single().Kind.Should().Be("inputs");
    }

    [Fact]
    public async Task The_list_says_which_fields_changed_before_and_after_in_words()
    {
        using Fixture f = new();
        (await f.Request(Fixture.Inputs(("videoPrompt", "\"A barista draws a heart\"")))).StatusCode.Should().Be(201);

        StepChangeRequestListResponse list = (await f.Service.ListAsync(f.Project, f.Run.Id, CancellationToken.None))!;

        StepChangeRequestResponse listed = list.Requests.Single();
        listed.Targets!.Single().Kind.Should().Be("inputs");
        listed.InputChanges.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            InputId = "videoPrompt",
            Label = "What happens",
            Before = "A barista pours latte art",
            After = "A barista draws a heart"
        });
    }

    [Fact]
    public async Task A_field_in_the_request_template_re_makes_the_agent_and_recomposes_the_request_keeping_the_notes()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Request(Fixture.Inputs(("audience", "\"Tourists\"")));

        r.StatusCode.Should().Be(201, r.Message);
        StepChangeRequest row = await f.Db.StepChangeRequests.SingleAsync();
        row.TargetStepOrder.Should().Be(1);
        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == row.ExecutionId);
        queued.UserRequest.Should().Be("Made for \"Tourists\".\n\nNotes for this run:\nKeep it warm.");
    }

    [Fact]
    public async Task A_value_that_picks_another_branch_is_refused()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Estimate(Fixture.Inputs(("liteMode", "true")));

        r.StatusCode.Should().Be(409);
        r.Code.Should().Be(AppInputChangeCodes.ChangesBranch);
        r.Extra!["inputId"].Should().Be("liteMode");
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Nothing_changed_and_a_field_nothing_on_the_path_uses_are_refused()
    {
        using Fixture f = new();

        StepChangeRequestResult same = await f.Estimate(Fixture.Inputs(("audience", "\"Baristas\"")));
        same.StatusCode.Should().Be(400);
        same.Code.Should().Be(AppInputChangeCodes.Unchanged);
    }

    [Fact]
    public async Task Values_go_through_the_run_gate_checks()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Estimate(Fixture.Inputs(("takes", "9")));
        r.StatusCode.Should().Be(400);
        r.Code.Should().Be(AppInputValueProblem.Invalid);
        r.Extra!["inputId"].Should().Be("takes");

        StepChangeRequestResult missing = await f.Estimate(new CreateStepChangeRequest(null, null, null, null, null,
            Inputs: new Dictionary<string, JsonElement> { ["videoPrompt"] = J("null") }));
        missing.Code.Should().Be(AppInputValueProblem.Missing);
    }

    [Fact]
    public async Task An_app_edited_since_the_run_is_stale()
    {
        using Fixture f = new(editAfterRun: steps =>
            steps.Single(s => s.StepOrder == 3).VideoGenerateConfigJson = FullClip.Replace("{{input.videoPrompt}}", "{{input.videoPrompt}} at dawn", StringComparison.Ordinal));

        StepChangeRequestResult r = await f.Estimate(Fixture.Inputs(("videoPrompt", "\"Another\"")));

        r.StatusCode.Should().Be(409);
        r.Code.Should().Be(AppInputChangeCodes.Stale);
    }

    [Fact]
    public async Task The_permission_to_send_media_is_never_changed_by_a_change_request()
    {
        // docs/design/app-inputs.md "Permission to send media as an app field" (4): it belongs to the run it was given to.
        using Fixture f = new();

        StepChangeRequestResult r = await f.Estimate(Fixture.Inputs((MediaEgressPermission.InputId, "true")));

        r.StatusCode.Should().Be(400);
        r.Code.Should().Be(AppInputValueProblem.Invalid);
        r.Message.Should().Be(MediaEgressPermission.NotChangeableMessage);
        r.Extra!["inputId"].Should().Be(MediaEgressPermission.InputId);
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_change_request_carries_the_runs_permission_over_with_who_gave_it()
    {
        string sendsAFrame = FullClip.Replace("\"appInputs\"", $"\"firstFrameProjectFileId\":\"{Guid.NewGuid()}\",\"allowSourceMediaEgress\":false,\"appInputs\"", StringComparison.Ordinal);
        using Fixture f = new(fullClip: sendsAFrame, mediaEgressBy: MediaEgressPermission.ByApp,
            runValues: Values(("videoPrompt", "\"A barista pours latte art\""), (MediaEgressPermission.InputId, "true")));

        StepChangeRequestResult r = await f.Request(Fixture.Inputs(("videoPrompt", "\"A barista draws a heart\"")));

        r.StatusCode.Should().Be(201, r.Message);
        StepChangeRequest row = await f.Db.StepChangeRequests.SingleAsync();
        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == row.ExecutionId);
        StoredRunBrief brief = RunBriefJson.TryParse(queued.RunBriefJson)!;
        MediaEgressPermission.IsRequested(brief.Inputs).Should().BeTrue();
        brief.AllowMediaEgressBy.Should().Be(MediaEgressPermission.ByApp, "the engine honours the value only beside its grantor");
        WorkflowDefinition snapshot = await f.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync(w => w.Id == row.WorkflowDefinitionId);
        snapshot.Steps.Single(s => s.StepOrder == 3).VideoGenerateConfigJson.Should().Contain("\"allowSourceMediaEgress\":true");
        (await f.Db.WorkflowSteps.SingleAsync(s => s.WorkflowDefinitionId == f.Workflow.Id && s.StepOrder == 3))
            .VideoGenerateConfigJson.Should().Contain("\"allowSourceMediaEgress\":false", "the saved step is never written");
    }

    [Fact]
    public async Task Inputs_with_step_targets_are_refused()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Estimate(new CreateStepChangeRequest(3, null, "x", null, null, Inputs: Values(("videoPrompt", "\"B\""))));

        r.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Capabilities_list_the_fields_with_what_they_re_make_and_which_pick_a_branch()
    {
        using Fixture f = new();

        StepChangeAppInputsResponse caps = (await f.Service.GetCapabilitiesAsync(f.Project, f.Run.Id, CancellationToken.None))!.AppInputs!;

        caps.WorkflowId.Should().Be(f.Workflow.Id);
        StepChangeAppInputFieldResponse prompt = caps.Fields.Single(x => x.InputId == "videoPrompt");
        prompt.Supported.Should().BeTrue();
        prompt.UsedBy.Should().Equal("Make the full clip");
        prompt.ValueText.Should().Be("A barista pours latte art");

        StepChangeAppInputFieldResponse lite = caps.Fields.Single(x => x.InputId == "liteMode");
        lite.Supported.Should().BeFalse();
        lite.ReasonCode.Should().Be(AppInputChangeCodes.ChangesBranch);
        lite.DecidesBranch.Should().BeTrue();
        lite.BranchChangingValues!.Select(v => v.GetRawText()).Should().Equal("true");

        caps.Fields.Single(x => x.InputId == "audience").UsedBy.Should().Equal("Write the script");
    }
}
