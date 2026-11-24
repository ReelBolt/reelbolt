using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.Snapshots;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Lane L3 (docs/design/app-inputs.md 6.3, 9, 10.1): binding onto the run's snapshot, APP_INPUTS_STALE, the step-cache
/// component, input.* parameters that win over step outputs, the "end" branch target, and holds over the reachable steps.
/// </summary>
public class AppInputEngineTests
{
    private static readonly AppManifest App = Manifest(
        Input("videoPrompt", "longText", "What happens in the video", required: true),
        Input("takes", "number", "Takes", min: 1, max: 4, def: "1"),
        Input("liteMode", "toggle", "Lite mode"));

    private const string Generate =
        """{"planSource":"Inline","inlinePrompt":"{{input.videoPrompt}}","durationSeconds":6,"takes":1,"maxClips":4,"maxSpendCredits":5,"appInputs":{"takes":"takes"}}""";

    private static WorkflowStep Step(int order, StepType type, string? config = null, string? condition = null, string? whenTrue = null, string? whenFalse = null)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = order,
            StepType = type,
            AgentDefinitionId = Guid.NewGuid(),
            ConditionExpression = condition,
            TrueBranchStepOrder = whenTrue,
            FalseBranchStepOrder = whenFalse
        };
        AppBindingStep.SetConfigJson(step, config);
        return step;
    }

    private static string Brief(params (string Id, string Raw)[] values) =>
        RunBriefJson.Serialize(new StoredRunBrief(Inputs: Values(values)));

    // ---------------- binding onto the snapshot ----------------

    [Fact]
    public void The_snapshot_binder_writes_plain_configs_and_leaves_a_workflow_without_inputs_alone()
    {
        WorkflowStep video = Step(1, StepType.VideoGenerate, Generate);
        AppInputSnapshotBinding.TryBindInPlace(App, [video], Values(("videoPrompt", "\"Latte art\""), ("takes", "2")), out var problems)
            .Should().BeTrue();
        problems.Should().BeEmpty();
        video.VideoGenerateConfigJson.Should().Contain("\"inlinePrompt\":\"Latte art\"").And.Contain("\"takes\":2").And.NotContain("appInputs");

        string before = video.VideoGenerateConfigJson!;
        AppInputSnapshotBinding.TryBindInPlace(App, [video], Values(("videoPrompt", "\"Other\"")), out _).Should().BeTrue();
        video.VideoGenerateConfigJson.Should().Be(before, "a bound config has nothing left to bind");

        WorkflowStep plain = Step(1, StepType.VideoGenerate, """{"planSource":"Inline","inlinePrompt":"fixed"}""");
        AppInputSnapshotBinding.TryBindInPlace(null, [plain], null, out _).Should().BeTrue();
        plain.VideoGenerateConfigJson.Should().Be("""{"planSource":"Inline","inlinePrompt":"fixed"}""");
    }

    [Fact]
    public void A_value_that_no_longer_binds_is_reported_and_nothing_is_changed()
    {
        WorkflowStep video = Step(1, StepType.VideoGenerate, Generate);

        AppInputSnapshotBinding.TryBindInPlace(App, [video], Values(("videoPrompt", "\"x\""), ("takes", "9")), out var problems)
            .Should().BeFalse();
        problems.Should().NotBeEmpty();
        video.VideoGenerateConfigJson.Should().Be(Generate);
    }

    private static WorkflowEngineDbContext Db() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase("l3-" + Guid.NewGuid().ToString("N")).Options);

    private static async Task<WorkflowExecution> SeedRunAsync(WorkflowEngineDbContext db, AppManifest? manifest, string? briefJson, params WorkflowStep[] steps)
    {
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "Avatar clip",
            ProjectId = Guid.NewGuid(),
            Kind = WorkflowDefinitionKind.Standard,
            AppManifestJson = manifest is null ? null : AppManifestJson.Serialize(manifest),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        foreach (WorkflowStep step in steps)
        {
            step.WorkflowDefinitionId = workflow.Id;
            workflow.Steps.Add(step);
        }

        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = workflow.ProjectId,
            OrganizationId = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            RunBriefJson = briefJson,
            Status = ExecutionStatus.Running
        };
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return await db.WorkflowExecutions.Include(e => e.WorkflowDefinition).ThenInclude(w => w.Steps).SingleAsync(e => e.Id == execution.Id);
    }

    [Fact]
    public async Task The_snapshot_is_bound_with_the_stored_values_and_carries_the_manifest()
    {
        await using WorkflowEngineDbContext db = Db();
        WorkflowExecution execution = await SeedRunAsync(db, App, Brief(("videoPrompt", "\"Latte art\""), ("takes", "2"), ("liteMode", "false")),
            Step(1, StepType.VideoGenerate, Generate));

        ExecutionRunPlan plan = await ExecutionSnapshotter.PrepareAsync(db, execution, CancellationToken.None);

        plan.Steps.Single().VideoGenerateConfigJson.Should().Contain("Latte art").And.NotContain("{{input.").And.NotContain("appInputs");
        WorkflowDefinition snapshot = await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == execution.SnapshotDefinitionId);
        snapshot.AppManifestJson.Should().NotBeNull();
        (await db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == snapshot.Id)).VideoGenerateConfigJson
            .Should().Contain("Latte art");
        db.WorkflowSteps.AsNoTracking().Single(s => s.WorkflowDefinitionId == execution.WorkflowDefinitionId).VideoGenerateConfigJson
            .Should().Be(Generate, "the workflow itself is never edited");

        plan.AppInputs.Should().NotBeNull();
        plan.AppInputs!.ComponentFor(1).Should().Contain("takes").And.Contain("videoPrompt");
    }

    [Fact]
    public async Task A_stale_value_throws_APP_INPUTS_STALE_and_writes_no_snapshot()
    {
        await using WorkflowEngineDbContext db = Db();
        WorkflowExecution execution = await SeedRunAsync(db, App, Brief(("videoPrompt", "\"x\""), ("takes", "9")),
            Step(1, StepType.VideoGenerate, Generate));

        Func<Task> take = () => ExecutionSnapshotter.PrepareAsync(db, execution, CancellationToken.None);

        AppInputsStaleException stale = (await take.Should().ThrowAsync<AppInputsStaleException>()).Which;
        stale.Code.Should().Be("APP_INPUTS_STALE");
        db.ChangeTracker.Clear();
        (await db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.ExecutionSnapshot)).Should().Be(0);
    }

    [Fact]
    public async Task A_run_without_app_inputs_takes_its_snapshot_exactly_as_before()
    {
        await using WorkflowEngineDbContext db = Db();
        const string plain = """{"planSource":"Inline","inlinePrompt":"fixed","takes":1}""";
        WorkflowExecution execution = await SeedRunAsync(db, null, null, Step(1, StepType.VideoGenerate, plain));

        ExecutionRunPlan plan = await ExecutionSnapshotter.PrepareAsync(db, execution, CancellationToken.None);

        plan.AppInputs.Should().BeNull();
        plan.Steps.Single().VideoGenerateConfigJson.Should().Be(plain);
        (await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == execution.SnapshotDefinitionId)).AppManifestJson.Should().BeNull();
    }

    // ---------------- caching ----------------

    [Fact]
    public void The_cache_component_is_keyed_only_when_set()
    {
        StepCacheKeyInputs plain = new()
        {
            ProjectId = Guid.Empty,
            StepType = StepType.VideoGenerate,
            AgentDefinitionId = Guid.Empty,
            AgentType = AgentType.VideoTransform,
            AgentInputContextMode = default,
            MaxIterations = 0,
            ProjectFileFingerprint = "-",
            VideoGenerateConfigJson = "{}"
        };
        StepCacheKeyBuilder builder = new();

        string baseline = builder.Build(plain);
        builder.Build(plain with { AppInputs = null }).Should().Be(baseline);
        builder.Build(plain with { AppInputs = "5:takes=1:2|" }).Should().NotBe(baseline);
        builder.Build(plain with { AppInputs = "5:takes=1:3|" }).Should().NotBe(builder.Build(plain with { AppInputs = "5:takes=1:2|" }));
    }

    [Fact]
    public void A_step_that_consumed_no_input_has_no_component()
    {
        WorkflowStep video = Step(1, StepType.VideoGenerate, Generate);
        WorkflowStep other = Step(2, StepType.VideoGenerate, """{"planSource":"Inline","inlinePrompt":"fixed"}""");
        WorkflowExecution execution = new() { RunBriefJson = Brief(("videoPrompt", "\"x\""), ("takes", "1")) };

        AppInputRunState state = AppInputSnapshotBinding.StateFor(execution, AppManifestJson.Serialize(App), [video, other], [video, other])!;

        state.ComponentFor(1).Should().NotBeNull();
        state.ComponentFor(2).Should().BeNull();
        AppInputSnapshotBinding.StateFor(new WorkflowExecution(), AppManifestJson.Serialize(App), [video], [video]).Should().BeNull();
    }

    // ---------------- conditions and "end" ----------------

    [Fact]
    public void An_app_input_wins_over_a_step_output_of_the_same_name()
    {
        const string output = """{"input":{"liteMode":true}}""";
        Dictionary<string, object?> inputs = new() { ["input.liteMode"] = false };

        ExpressionEvaluator.EvaluateCore("input.liteMode", output, inputs, null).Should().BeTrue("today the JSON wins");
        ExpressionEvaluator.EvaluateCore("input.liteMode", output, inputs, null, AppInputPlaceholders.ExpressionPrefix).Should().BeFalse();
        ExpressionEvaluator.EvaluateCore("input.liteMode", output, new Dictionary<string, object?>(), null, AppInputPlaceholders.ExpressionPrefix)
            .Should().BeFalse("a step output can never set a reserved input");
    }

    private static StepExecutionContext ConditionalContext(string expression, string whenTrue, string whenFalse)
    {
        WorkflowStep conditional = Step(1, StepType.Conditional, null, expression, whenTrue, whenFalse);
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = conditional,
            AllSteps = [conditional, Step(2, StepType.Agent), Step(3, StepType.Agent)],
            AccumulatedOutput = """{"input":{"liteMode":false}}""",
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "app-inputs",
            CancellationToken = CancellationToken.None
        };
    }

    private static async Task<StepExecutionResult> RunAsync(StepExecutionContext context, AppInputRunState? state)
    {
        using IDisposable scope = AppInputsAmbient.Enter(state);
        return await new ConditionalStepExecutor(NullLogger<ConditionalStepExecutor>.Instance, Mock.Of<IDecisionGate>()).ExecuteAsync(context);
    }

    private static AppInputRunState LiteState(bool lite) =>
        new(Values(("liteMode", lite ? "true" : "false")), new Dictionary<int, string>(), []);

    [Fact]
    public async Task A_condition_reads_the_run_input_not_the_previous_output()
    {
        (await RunAsync(ConditionalContext("input.liteMode", "2", "3"), LiteState(true))).NextStepIndex.Should().Be(1);
        (await RunAsync(ConditionalContext("input.liteMode", "2", "3"), LiteState(false))).NextStepIndex.Should().Be(2);
    }

    [Fact]
    public async Task The_end_target_finishes_the_run()
    {
        StepExecutionResult result = await RunAsync(ConditionalContext("", "end", "3"), null);

        result.NextStepIndex.Should().Be(3, "the index past the last step ends the run loop");
        result.Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public void Branch_reach_and_the_dependency_graph_know_the_end_token()
    {
        List<WorkflowStep> steps =
        [
            Step(1, StepType.Conditional, null, "input.liteMode", "2", "4"),
            Step(2, StepType.Agent),
            Step(3, StepType.Conditional, null, "", "end", "end"),
            Step(4, StepType.Agent)
        ];

        // Step 4 is skipped by the lite branch, which now ends at step 3 instead of falling through to 4.
        WorkflowBranchReach.OnEveryPath(steps).Should().Equal(true, false, false, false);
        WorkflowStepDependencyGraph.Build(steps).DependsOn(4).Keys.Should().Contain(1);
    }

    [Fact]
    public void Holds_plan_over_the_reachable_steps_only()
    {
        List<WorkflowStep> steps =
        [
            Step(1, StepType.Conditional, null, "input.liteMode", "2", "4"),
            Step(2, StepType.VideoGenerate, Generate),
            Step(3, StepType.Conditional, null, "", "end", "end"),
            Step(4, StepType.VideoGenerate, Generate)
        ];
        AppInputRunState state = new(Values(("liteMode", "true")), new Dictionary<int, string>(),
            [.. steps.Select(AppBindingStep.From)]);

        state.Reachable(steps).Select(s => s.StepOrder).Should().Equal(1, 2, 3);
        state.ExpressionParameters["input.liteMode"].Should().Be(true);
    }
}
