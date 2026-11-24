using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;
using ReelBolt.WorkflowEngine.Tests.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>The three video apps (docs/design/video-events-analysis.md section 7): manifest, lint, binder for every switch and choice, cost.</summary>
public class VideoAppsTests
{
    private static readonly JsonSerializerOptions CompileJson = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static readonly Dictionary<AgentType, Guid> AgentIds = Enum.GetValues<AgentType>().ToDictionary(t => t, _ => Guid.NewGuid());

    public static IEnumerable<object[]> Keys() => VideoAppTemplates.Keys.Select(k => new object[] { k });

    private static WorkflowTemplateDefinition Template(string key) => WorkflowTemplateCatalog.GetAll().Single(t => t.Key == key);

    private static IReadOnlyList<AppBindingStep> Steps(WorkflowTemplateDefinition t) =>
        [.. AppBindingStep.FromTemplate(t).Select((s, i) => s with { AgentDefinitionId = AgentIds[t.Steps[i].AgentType] })];

    private static AppManifest ManifestOf(WorkflowTemplateDefinition t) =>
        WorkflowTemplateAppInputs.ManifestForCreate(t, t.Key, type => AgentIds[type])!;

    private static IEnumerable<AppManifestInput> Typed(AppManifest m) => m.Inputs!.Where(i => i.Kind == AppManifestVocabulary.KindInput);

    private static IEnumerable<JsonElement> ValuesOf(AppManifestInput i) => i.Type == "toggle" ? [J("true"), J("false")] : (i.Options ?? []).Select(o => o.Value);

    private static Dictionary<string, JsonElement> Defaults(AppManifest m)
    {
        Dictionary<string, JsonElement> d = new(StringComparer.Ordinal);
        foreach (AppManifestInput i in Typed(m).Where(x => x.Default is { ValueKind: not JsonValueKind.Null }))
            d[i.Id!] = i.Default!.Value;
        foreach (AppManifestInput i in Typed(m).Where(x => x.Required == true && !d.ContainsKey(x.Id!)))
            d[i.Id!] = J(JsonSerializer.Serialize("A sample, written for the test."));
        return d;
    }

    /// <summary>The defaults, then each switch and choice moved to each of its values alone, then everything on.</summary>
    private static IEnumerable<Dictionary<string, JsonElement>> Variants(AppManifest m)
    {
        Dictionary<string, JsonElement> baseline = Defaults(m);
        yield return baseline;
        foreach (AppManifestInput c in Typed(m).Where(i => i.Type is "toggle" or "choice"))
            foreach (JsonElement v in ValuesOf(c))
                yield return new Dictionary<string, JsonElement>(baseline, StringComparer.Ordinal) { [c.Id!] = v };
        Dictionary<string, JsonElement> all = new(baseline, StringComparer.Ordinal);
        foreach (AppManifestInput c in Typed(m).Where(i => i.Type is "toggle" or "choice" && i.Id != "quickMode" && i.Id != "whatIsIt"))
            all[c.Id!] = c.Type == "toggle" ? J("true") : (c.Options!.Last().Value);
        yield return all;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_app_is_opt_in_and_its_manifest_validates(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        t.AutoCreateOnProject.Should().BeFalse();
        t.Hidden.Should().BeFalse();
        AppManifest manifest = ManifestOf(t);
        AppManifestValidator.Validate(manifest, AppStepFacts.FromTemplate(t), Steps(t)).Should().BeEmpty();
        AppManifestValidator.Sanitize(manifest).Should().BeEquivalentTo(manifest);
        foreach (AppManifestInput input in Typed(manifest))
            input.LabelKey.Should().Be($"recipes.inputs.{input.Id}.label");
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task The_app_lints_with_no_errors_beyond_the_egress_permission(string key)
    {
        IReadOnlyList<LintFinding> findings = await LintKit.LintAsync(LintKit.FromTemplate(Template(key)));
        findings.Where(f => f.Severity == LintSeverity.Error && f.RuleId != "WL015").Should().BeEmpty(key);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_app_passes_the_save_validator_and_wires_steps_explicitly(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        StepConfigSaveValidator.Validate(RecipeInstance.Build(t, [], null, AgentIds).Steps!).Should().BeEmpty();
        HashSet<string> orders = [.. Enumerable.Range(1, t.Steps.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), "end"];
        foreach (WorkflowTemplateStepDefinition c in t.Steps.Where(s => s.StepType == StepType.Conditional))
        {
            orders.Should().Contain(c.TrueBranchStepOrder!);
            if (c.FalseBranchStepOrder is not null)
                orders.Should().Contain(c.FalseBranchStepOrder);
        }

        foreach (WorkflowTemplateStepDefinition r in t.Steps.Where(s => s.StepType == StepType.ReviewLoop))
            r.ReviewLoopConfigJson.Should().Be(WorkflowTemplateCatalog.ShippedReviewLoopConfigJson);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_switch_and_choice_binds_to_valid_configs_and_egress_stays_off(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        AppManifest manifest = ManifestOf(t);
        IReadOnlyList<AppBindingStep> steps = Steps(t);
        foreach (Dictionary<string, JsonElement> raw in Variants(manifest))
        {
            string where = $"{key} {string.Join(", ", raw.Select(p => $"{p.Key}={p.Value.GetRawText()}"))}";
            AppInputCheckResult values = AppInputValueChecker.Check(manifest, steps, raw);
            values.Problems.Should().BeEmpty(where);
            AppInputBindResult bound = AppInputBinder.Bind(manifest, steps, values.Values, "notes");
            bound.Problems.Should().BeEmpty(where);
            IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders(steps, values.Values);
            foreach (AppBoundStep step in bound.Steps)
            {
                string config = (step.Step.ConfigJson ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
                config.Should().NotContain("{{input.", where).And.NotContain("\"allowSourceMediaEgress\":true", where);
                if (reachable.Contains(step.Step.StepOrder) && step.Step.StepType == StepType.VideoCompile)
                    JsonSerializer.Deserialize<VideoCompileStepConfig>(config, CompileJson).Should().NotBeNull(where);
                if (reachable.Contains(step.Step.StepOrder) && step.Step.StepType == StepType.VideoGenerate)
                    JsonSerializer.Deserialize<VideoGenerateStepConfig>(config, VideoGenerateStepConfig.JsonOptions)!.Validate().Should().BeEmpty(where);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Paid_generation_steps_carry_a_spend_cap_and_the_cost_badge_is_derived(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        foreach (WorkflowTemplateStepDefinition s in t.Steps.Where(x => x.StepType == StepType.VideoGenerate))
        {
            VideoGenerateStepConfig c = JsonSerializer.Deserialize<VideoGenerateStepConfig>(s.VideoGenerateConfigJson!, VideoGenerateStepConfig.JsonOptions)!;
            c.MaxSpendCredits.Should().BeGreaterThan(0);
            c.AllowSourceMediaEgress.Should().BeFalse();
        }

        AppCostClass.Derive(AppStepFacts.FromTemplate(t)).Should().NotBeNullOrWhiteSpace();
    }

    private static (WorkflowTemplateDefinition T, AppManifest M, IReadOnlyList<AppBindingStep> S) Edit()
    {
        WorkflowTemplateDefinition t = Template(VideoAppTemplates.EditMyVideoKey);
        return (t, ManifestOf(t), Steps(t));
    }

    private static string CompileConfig(AppInputBindResult bound, IReadOnlyList<AppBindingStep> steps, bool last) =>
        bound.Steps.Where(s => s.Step.StepType == StepType.VideoCompile).Select(s => s.Step.ConfigJson!).ToList() is { } l
            ? (last ? l.Last() : l.First())
            : "";

    [Fact]
    public void Edit_my_video_music_is_off_by_default_and_on_only_when_chosen()
    {
        (WorkflowTemplateDefinition _, AppManifest m, IReadOnlyList<AppBindingStep> steps) = Edit();
        Defaults(m)["music"].GetString().Should().Be("off");

        AppInputCheckResult checkedOff = AppInputValueChecker.Check(m, steps, Defaults(m));
        AppInputBindResult off = AppInputBinder.Bind(m, steps, checkedOff.Values, null);
        CompileConfig(off, steps, last: true).Replace(" ", "").Should().Contain("\"enableMusic\":false");
        StaticBranchResolver.ReachableStepOrders(steps, checkedOff.Values).Should().NotContain(steps.First(s => s.Name == "Pick background music").StepOrder);

        Dictionary<string, JsonElement> chosen = Defaults(m);
        chosen["music"] = J("\"on\"");
        AppInputCheckResult checkedOn = AppInputValueChecker.Check(m, steps, chosen);
        CompileConfig(AppInputBinder.Bind(m, steps, checkedOn.Values, null), steps, last: true).Replace(" ", "").Should().Contain("\"enableMusic\":true");
        StaticBranchResolver.ReachableStepOrders(steps, checkedOn.Values).Should().Contain(steps.First(s => s.Name == "Pick background music").StepOrder);
    }

    [Fact]
    public void Lite_mode_runs_one_plain_compile_and_the_extras_run_only_when_switched_on()
    {
        (WorkflowTemplateDefinition t, AppManifest m, IReadOnlyList<AppBindingStep> steps) = Edit();
        Dictionary<string, JsonElement> lite = Defaults(m);
        lite["quickMode"] = J("true");
        IReadOnlySet<int> liteReach = StaticBranchResolver.ReachableStepOrders(steps, AppInputValueChecker.Check(m, steps, lite).Values);
        liteReach.Count(o => t.Steps[o - 1].StepType == StepType.VideoCompile).Should().Be(1);
        liteReach.Should().NotContain(t.Steps.Select((s, i) => (s, i)).Where(p => p.s.StepType == StepType.ReviewLoop).Select(p => p.i + 1));

        Dictionary<string, JsonElement> plain = Defaults(m);
        plain["music"] = J("\"off\"");
        plain["motion"] = J("\"off\"");
        IReadOnlySet<int> reach = StaticBranchResolver.ReachableStepOrders(steps, AppInputValueChecker.Check(m, steps, plain).Values);
        t.Steps.Select((s, i) => (s, i)).Where(p => reach.Contains(p.i + 1)).Select(p => p.s.AgentType)
            .Should().NotContain([AgentType.SoundDesigner, AgentType.MusicSupervisor, AgentType.MotionDirector, AgentType.MotionGraphicsPlanner, AgentType.ShotDirector, AgentType.NarrationWriter, AgentType.PickupPlanner]);

        // Video events are on by default: the motion step runs, the compile applies it and cuts land on events.
        Defaults(m)["motion"].GetString().Should().Be("subtle");
        Defaults(m)["speedRamps"].GetBoolean().Should().BeTrue();
        Defaults(m)["flashes"].GetBoolean().Should().BeTrue();
        Defaults(m)["cutOnEvents"].GetBoolean().Should().BeTrue();
        AppInputCheckResult c = AppInputValueChecker.Check(m, steps, Defaults(m));
        StaticBranchResolver.ReachableStepOrders(steps, c.Values).Should().Contain(steps.First(s => s.Name == "Plan moves, ramps and flashes on the action").StepOrder);
        CompileConfig(AppInputBinder.Bind(m, steps, c.Values, null), steps, true).Replace(" ", "").Should().Contain("\"enableMotion\":true");
    }

    [Fact]
    public void Old_footage_keys_resolve_as_hidden_aliases_that_open_an_app_with_preset_values()
    {
        VideoAppTemplates.Aliases.Should().HaveCount(18).And.OnlyContain(a => VideoAppTemplates.Keys.Contains(a.App));
        IReadOnlyList<WorkflowTemplateDefinition> listed = WorkflowTemplateCatalog.GetListed();
        foreach (VideoAppTemplates.Alias alias in VideoAppTemplates.Aliases)
        {
            WorkflowTemplateDefinition old = WorkflowTemplateCatalog.GetByKey(alias.Key)!;
            old.Hidden.Should().BeTrue(alias.Key);
            old.SupersededBy.Should().Be(alias.App);
            listed.Select(t => t.Key).Should().NotContain(alias.Key);
            old.Steps.Should().NotBeEmpty("a workflow made from the old key keeps its own steps");

            WorkflowTemplateDefinition app = WorkflowTemplateCatalog.GetByKey(alias.App)!;
            AppManifest created = WorkflowTemplateAppInputs.ManifestForCreate(app, alias.Key, type => AgentIds[type])!;
            foreach ((string id, JsonElement value) in alias.Presets)
                created.Inputs!.Single(i => i.Id == id).Default!.Value.GetRawText().Should().Be(value.GetRawText(), $"{alias.Key} presets {id}");
            AppInputValueChecker.Check(created, Steps(app), Defaults(created)).Problems.Should().BeEmpty(alias.Key);
        }

        listed.Select(t => t.Key).Should().Contain(VideoAppTemplates.Keys);
        listed.Count(t => t.AppManifest?.Category == "footage").Should().Be(4, "the three video apps plus the sound-effects-only recipe");
    }
}
