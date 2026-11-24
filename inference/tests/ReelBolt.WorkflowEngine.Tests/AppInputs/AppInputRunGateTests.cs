using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;
using ReelBolt.WorkflowEngine.Tests.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Lane L2 (docs/design/app-inputs.md 6.2, 11.2-11.4): the run gate's app-input check, the splitter that stores the values
/// and composes the request, the switch, and the lint rules WL028 to WL033.
/// </summary>
public class AppInputRunGateTests
{
    private static readonly AppManifest App = Manifest(
        Input("videoPrompt", "longText", "What happens in the video", required: true),
        Input("takes", "number", "Takes", min: 1, max: 4, def: "1"),
        Input("liteMode", "toggle", "Lite mode")) with
    {
        RequestTemplate = "Make a clip about {{input.videoPrompt}}."
    };

    private const string Generate =
        """{"planSource":"Inline","inlinePrompt":"{{input.videoPrompt}}","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":2,"maxSpendCredits":5,"appInputs":{"takes":"takes"}}""";

    private static WorkflowStep VideoStep(int order, string config = Generate) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.VideoGenerate,
        AgentDefinitionId = Guid.NewGuid(),
        VideoGenerateConfigJson = config
    };

    private static WorkflowStep Branch(int order, string expression, string whenTrue, string whenFalse) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.Conditional,
        AgentDefinitionId = Guid.NewGuid(),
        ConditionExpression = expression,
        TrueBranchStepOrder = whenTrue,
        FalseBranchStepOrder = whenFalse
    };

    // ---------------- the pure gate ----------------

    [Fact]
    public void A_workflow_without_app_inputs_is_not_touched()
    {
        AppInputGateResult gate = AppInputValueValidator.Evaluate(null, [VideoStep(1, """{"planSource":"Inline","inlinePrompt":"x"}""")], null, "notes");

        gate.Applies.Should().BeFalse();
        gate.Refused.Should().BeFalse();
        gate.UserRequest.Should().Be("notes");
        gate.BoundSteps.Should().BeNull();
    }

    [Fact]
    public void Values_for_an_app_without_inputs_are_refused()
    {
        AppInputGateResult gate = AppInputValueValidator.Evaluate(null, [VideoStep(1, """{"planSource":"Inline","inlinePrompt":"x"}""")],
            Values(("videoPrompt", "\"x\"")), null);

        gate.Code.Should().Be(AppInputValueProblem.Invalid);
    }

    [Fact]
    public void A_missing_required_input_is_refused_at_the_gate_but_not_by_the_estimate()
    {
        AppInputGateResult run = AppInputValueValidator.Evaluate(App, [VideoStep(1)], null, null);
        run.Code.Should().Be(AppInputValueProblem.Missing);
        run.Message.Should().Contain("What happens in the video");
        run.InputId.Should().Be("videoPrompt");

        AppInputGateResult estimate = AppInputValueValidator.Evaluate(App, [VideoStep(1)], null, null, forEstimate: true);
        estimate.Refused.Should().BeFalse();
        estimate.Applies.Should().BeTrue();
    }

    [Fact]
    public void Values_are_bound_onto_detached_copies_canonical_values_kept_and_the_request_composed()
    {
        WorkflowStep authored = VideoStep(1);
        AppInputGateResult gate = AppInputValueValidator.Evaluate(App, [authored],
            Values(("videoPrompt", "\"  A barista pours latte art  \""), ("takes", "2")), "Keep it warm.");

        gate.Refused.Should().BeFalse(gate.Message);
        gate.Inputs!["videoPrompt"].GetString().Should().Be("A barista pours latte art");
        gate.Inputs["takes"].GetInt32().Should().Be(2);
        gate.Inputs["liteMode"].GetBoolean().Should().BeFalse("a toggle with no value is off");
        gate.UserRequest.Should().Be("Make a clip about \"A barista pours latte art\".\n\nNotes for this run:\nKeep it warm.");

        WorkflowStep bound = gate.BoundSteps!.Single();
        bound.Should().NotBeSameAs(authored);
        bound.Id.Should().Be(authored.Id);
        bound.VideoGenerateConfigJson.Should().Contain("A barista pours latte art").And.NotContain("appInputs").And.NotContain("{{input.");
        authored.VideoGenerateConfigJson.Should().Be(Generate, "the saved step is never edited");
    }

    [Fact]
    public void A_value_that_makes_the_bound_config_invalid_is_refused_naming_the_input()
    {
        // takes 3 is inside the input's own range (1..4) but the step allows at most maxClips (2) takes.
        AppInputGateResult gate = AppInputValueValidator.Evaluate(App, [VideoStep(1)],
            Values(("videoPrompt", "\"x\""), ("takes", "3")), null);

        gate.Code.Should().Be(AppInputValueProblem.Invalid);
        gate.Message.Should().Contain("“Takes”").And.Contain("MaxClips");
    }

    [Fact]
    public void A_save_validator_that_throws_on_the_bound_config_refuses_the_run()
    {
        AppInputGateResult gate = AppInputValueValidator.Evaluate(App, [VideoStep(1)],
            Values(("videoPrompt", "\"x\""), ("takes", "2")), null,
            saveValidate: _ => throw new InvalidOperationException("boom"));

        gate.Refused.Should().BeTrue();
        gate.Code.Should().Be(AppInputValueProblem.Invalid);
    }

    [Fact]
    public void A_model_binding_without_a_resolver_is_refused_as_model_unavailable()
    {
        AppManifest app = Manifest(Input("videoModel", "model", "Video model", capability: "VideoGeneration"));
        string config = """{"planSource":"Inline","inlinePrompt":"x","durationSeconds":6,"takes":1,"maxClips":1,"maxSpendCredits":5,"appInputs":{"@model":"videoModel"}}""";

        AppInputGateResult gate = AppInputValueValidator.Evaluate(app, [VideoStep(1, config)],
            Values(("videoModel", $"\"{Guid.NewGuid()}\"")), null);

        gate.Code.Should().Be(AppInputValueValidator.ModelUnavailableCode);
        gate.Message.Should().Contain("Video model");
    }

    [Fact]
    public void Only_the_branch_the_inputs_choose_is_reachable()
    {
        List<WorkflowStep> steps =
        [
            Branch(1, "input.liteMode", "2", "4"),
            VideoStep(2),
            Branch(3, "", "end", "end"),
            VideoStep(4)
        ];

        AppInputGateResult lite = AppInputValueValidator.Evaluate(App, steps, Values(("videoPrompt", "\"x\""), ("liteMode", "true")), null);
        lite.ReachableStepOrders.Should().BeEquivalentTo([1, 2, 3]);
        lite.ReachableSteps!.Select(s => s.StepOrder).Should().Equal(1, 2, 3);

        AppInputGateResult full = AppInputValueValidator.Evaluate(App, steps, Values(("videoPrompt", "\"x\"")), null);
        full.ReachableStepOrders.Should().BeEquivalentTo([1, 4]);
    }

    // ---------------- the splitter and the switch ----------------

    private static InferenceApiDbContext Db() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase("app-inputs-" + Guid.NewGuid().ToString("N")).Options);

    private static IConfiguration Switch(bool on) => new ConfigurationBuilder().Build();

    private static async Task<(Guid ProjectId, Guid WorkflowId)> SeedAsync(InferenceApiDbContext db, AppManifest? manifest)
    {
        Guid projectId = Guid.NewGuid();
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "App",
            ProjectId = projectId,
            AppManifestJson = manifest is null ? null : AppManifestJson.Serialize(manifest),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        WorkflowStep step = VideoStep(1);
        step.WorkflowDefinitionId = workflow.Id;
        workflow.Steps.Add(step);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
        return (projectId, workflow.Id);
    }

    [Fact]
    public async Task Brief_inputs_are_refused_when_the_caller_starts_no_saved_workflow()
    {
        await using InferenceApiDbContext db = Db();
        (Guid projectId, Guid workflowId) = await SeedAsync(db, App);

        RunBriefResolution result = await RunBriefSplitter.ResolveAsync(
            new RunStyleResolver(db), db, Guid.NewGuid(), projectId, new LegacyRunInputs(null, null, null, null, null, null),
            new RunBrief(Inputs: Values(("videoPrompt", "\"x\""))), CancellationToken.None);

        result.Error.Should().Be(RunBriefSplitter.InputsNotAvailable);
        result.ErrorCode.Should().BeNull();
        JsonSerializer.Serialize(result.ErrorBody()).Should().Be("{\"message\":" + JsonSerializer.Serialize(RunBriefSplitter.InputsNotAvailable) + "}");
    }

    [Fact]
    public async Task Values_are_stored_canonical_and_the_request_is_composed()
    {
        await using InferenceApiDbContext db = Db();
        (Guid projectId, Guid workflowId) = await SeedAsync(db, App);

        RunBriefResolution result = await RunBriefSplitter.ResolveAsync(
            new RunStyleResolver(db), db, Guid.NewGuid(), projectId, new LegacyRunInputs(null, null, null, null, null, null),
            new RunBrief(Notes: "Warm light.", Inputs: Values(("videoPrompt", "\"Latte art\""))), CancellationToken.None,
            new AppInputRunStart(new AppInputValueValidator(db), null, workflowId));

        result.Error.Should().BeNull();
        result.UserRequest.Should().Be("Make a clip about \"Latte art\".\n\nNotes for this run:\nWarm light.");
        result.Choices!.Brief!.Inputs!["videoPrompt"].GetString().Should().Be("Latte art");
        RunBriefJson.Serialize(result.Choices.Brief).Should().Contain("\"inputs\":{");
        result.AppInputs!.BoundSteps.Should().ContainSingle();
    }

    [Fact]
    public async Task A_missing_required_input_is_a_coded_400_body()
    {
        await using InferenceApiDbContext db = Db();
        (Guid projectId, Guid workflowId) = await SeedAsync(db, App);

        RunBriefResolution result = await RunBriefSplitter.ResolveAsync(
            new RunStyleResolver(db), db, Guid.NewGuid(), projectId, new LegacyRunInputs(null, null, null, null, null, "notes only"),
            null, CancellationToken.None,
            new AppInputRunStart(new AppInputValueValidator(db), null, workflowId));

        result.ErrorCode.Should().Be(AppInputValueProblem.Missing);
        JsonSerializer.Serialize(result.ErrorBody()).Should().Contain("\"code\":\"APP_INPUT_MISSING\"");
        // The run page places the sentence next to the field the refusal names (docs/design/app-inputs.md 21).
        JsonSerializer.Serialize(result.ErrorBody()).Should().Contain("\"inputId\":\"videoPrompt\"");
    }

    [Fact]
    public async Task An_app_without_inputs_runs_exactly_as_before()
    {
        await using InferenceApiDbContext db = Db();
        Guid projectId = Guid.NewGuid();
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Plain", ProjectId = projectId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowStep step = VideoStep(1, """{"planSource":"Inline","inlinePrompt":"x"}""");
        step.WorkflowDefinitionId = workflow.Id;
        workflow.Steps.Add(step);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();

        RunBriefResolution result = await RunBriefSplitter.ResolveAsync(
            new RunStyleResolver(db), db, Guid.NewGuid(), projectId, new LegacyRunInputs(null, null, null, null, null, "the notes"),
            null, CancellationToken.None,
            new AppInputRunStart(new AppInputValueValidator(db), null, workflow.Id));

        result.Error.Should().BeNull();
        (result.Choices?.IsEmpty ?? true).Should().BeTrue();
        result.Choices?.Brief.Should().BeNull();
        result.UserRequest.Should().Be("the notes");
        result.AppInputs.Should().BeNull();
        result.PricingOr(RunStartPricing.SavedWorkflow).Should().BeSameAs(RunStartPricing.SavedWorkflow);
    }

    // ---------------- lint WL028 to WL033 ----------------

    private sealed class FixedManifest(AppManifest manifest) : IAppManifestOrphanSource
    {
        public Task<IReadOnlyList<AppManifestOrphan>> FindOrphansAsync(WorkflowLintRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AppManifestOrphan>>([]);

        public Task<AppManifest?> ManifestAsync(WorkflowLintRequest request, CancellationToken ct) => Task.FromResult<AppManifest?>(manifest);
    }

    private sealed class FixedPrice(double credits) : IAppInputSpendPricer
    {
        public Task<double?> CreditsAsync(WorkflowStep step, CancellationToken ct) => Task.FromResult<double?>(credits);
    }

    private static async Task<IReadOnlyList<LintFinding>> LintAsync(
        AppManifest manifest, IEnumerable<WorkflowStep> steps, RunBrief? brief, bool on = true, IAppInputSpendPricer? pricer = null)
    {
        LintKit.FakeFacts facts = new();
        WorkflowLinter linter = new(new LintKit.FixedFactsSource(facts), new FixedManifest(manifest), null, null, pricer);
        WorkflowLintReport report = await linter.LintAsync(
            new WorkflowLintRequest(Guid.NewGuid(), Guid.NewGuid(), [.. steps], brief, null, facts), CancellationToken.None);
        return report.Findings;
    }

    [Fact]
    public async Task WL015_is_asked_only_on_the_branch_the_fields_choose_and_the_permission_is_a_known_field()
    {
        // docs/design/app-inputs.md "Permission to send media as an app field": a switch picks the branch with the step that sends.
        AppManifest page = Manifest(Input("track", "toggle", "Track the plate"));
        WorkflowStep analyze = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.VideoAnalyze,
            AgentDefinitionId = Guid.NewGuid(),
            VideoAnalyzeConfigJson = """{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","trackObjects":[{"label":"the plate","method":"Segmentation"}],"allowSourceMediaEgress":false}"""
        };
        WorkflowStep compile = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            AgentDefinitionId = Guid.NewGuid(),
            VideoCompileConfigJson = """{"mode":"Reencode"}"""
        };
        WorkflowStep[] steps = [Branch(1, "input.track", "2", "3"), analyze, compile];

        (await LintAsync(page, steps, new RunBrief(Inputs: Values(("track", "true"))))).Where(f => f.RuleId == "WL015").Should().ContainSingle();
        (await LintAsync(page, steps, new RunBrief(Inputs: Values(("track", "false"))))).Where(f => f.RuleId == "WL015").Should().BeEmpty();

        IReadOnlyList<LintFinding> granted = await LintAsync(
            page, steps, new RunBrief(Inputs: Values(("track", "true"), (MediaEgressPermission.InputId, "true"))));
        granted.Where(f => f.RuleId == "WL015").Should().BeEmpty();
        granted.Should().NotContain(f => f.Message.Contains("no input named"), "the permission is one of the app's own fields");
    }

    [Fact]
    public async Task WL028_names_a_missing_required_input_with_a_fill_in_fix()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(App, [VideoStep(1)], new RunBrief());

        LintFinding missing = findings.Rule("WL028").Single();
        missing.Severity.Should().Be(LintSeverity.Error);
        missing.Args["input"].Should().Be("What happens in the video");
        missing.Fix!.Kind.Should().Be(LintFixKind.FillInput);
        missing.Fix.LabelId.Should().Be("WL028.fillInput");
    }

    [Fact]
    public async Task WL029_reports_a_value_that_no_longer_fits_its_setting()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(App, [VideoStep(1)],
            new RunBrief(Inputs: Values(("videoPrompt", "\"x\""), ("takes", "3"))));

        LintFinding unfit = findings.Rule("WL029").Single();
        unfit.Args["input"].Should().Be("Takes");
        unfit.Message.Should().Contain("MaxClips");
    }

    [Fact]
    public async Task WL030_is_a_warning_for_a_default_model_and_an_error_for_a_chosen_one()
    {
        Guid model = Guid.NewGuid();
        AppManifest app = Manifest(Input("videoModel", "model", "Video model", capability: "VideoGeneration", def: $"\"{model}\""));
        string config = """{"planSource":"Inline","inlinePrompt":"x","durationSeconds":6,"takes":1,"maxClips":1,"maxSpendCredits":5,"appInputs":{"@model":"videoModel"}}""";

        (await LintAsync(app, [VideoStep(1, config)], null)).Rule("WL030").Single().Severity.Should().Be(LintSeverity.Warning);
        LintFinding chosen = (await LintAsync(app, [VideoStep(1, config)], new RunBrief(Inputs: Values(("videoModel", $"\"{model}\""))))).Rule("WL030").Single();
        chosen.Severity.Should().Be(LintSeverity.Error);
        chosen.MessageKey.Should().Be("WL030");
    }

    [Fact]
    public async Task WL031_says_which_steps_a_switch_leaves_out()
    {
        List<WorkflowStep> steps = [Branch(1, "input.liteMode", "2", "4"), VideoStep(2), Branch(3, "", "end", "end"), VideoStep(4)];

        LintFinding info = (await LintAsync(App, steps, new RunBrief(Inputs: Values(("videoPrompt", "\"x\""), ("liteMode", "true"))))).Rule("WL031").Single();

        info.Severity.Should().Be(LintSeverity.Info);
        info.StepOrder.Should().Be(1);
        info.Args["value"].Should().Be("on");
        info.Args["count"].Should().Be("1");
        info.Args["skipped1"].Should().Be("4");
    }

    [Fact]
    public async Task WL032_notes_an_agent_model_shared_by_several_steps()
    {
        Guid agent = Guid.NewGuid();
        AppManifest app = Manifest(Input("scriptModel", "model", "Script model", capability: "Chat", agent: agent));
        WorkflowStep a = LintKit.Agent(1, AgentType.ScriptwriterAgent);
        WorkflowStep b = LintKit.Agent(2, AgentType.ScriptwriterAgent);
        a.AgentDefinitionId = agent;
        b.AgentDefinitionId = agent;

        LintFinding info = (await LintAsync(app, [a, b], null)).Rule("WL032").Single();
        info.Args["count"].Should().Be("2");
    }

    [Fact]
    public async Task WL033_warns_when_the_dearest_choice_exceeds_the_spend_cap()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(App, [VideoStep(1, Generate.Replace("\"maxClips\":2", "\"maxClips\":4"))], null, pricer: new FixedPrice(12));

        LintFinding over = findings.Rule("WL033").Single();
        over.Severity.Should().Be(LintSeverity.Warning);
        over.Args["credits"].Should().Be("12");
        over.Args["cap"].Should().Be("5");
        (await LintAsync(App, [VideoStep(1)], null, pricer: new FixedPrice(4))).Rule("WL033").Should().BeEmpty();
    }

}
