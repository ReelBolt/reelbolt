using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;
using ReelBolt.WorkflowEngine.Tests.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// The preset recipes (docs/design/recipe-presets.md): each is listed and opt-in, validates against its own steps, lints, binds
/// for every combination of its switches and choices (hidden fields are not asked for), prices, derives its cost badge, carries
/// a spend cap on every paid step and can never turn the permission to send media on. No paid or live call is made.
/// </summary>
public class RecipePresetsTests
{
    private static readonly Dictionary<AgentType, Guid> AgentIds = Enum.GetValues<AgentType>().ToDictionary(t => t, _ => Guid.NewGuid());

    private static readonly Dictionary<string, string> ExpectedCost = new(StringComparer.Ordinal)
    {
        [WorkflowTemplatePresets.ProductPromoKey] = AppCostClass.UsesCreditsAndVoice,
        [WorkflowTemplatePresets.AvatarExplainerKey] = AppCostClass.UsesCreditsAndAiShots,
        [WorkflowTemplatePresets.MusicOrSoundEffectKey] = AppCostClass.UsesCreditsAndAiShots,
        [WorkflowTemplatePresets.ThumbnailKey] = AppCostClass.NoCredits,
        [WorkflowTemplatePresets.ReadAloudKey] = AppCostClass.UsesCreditsAndVoice,
        [WorkflowTemplatePresets.AiClipKey] = AppCostClass.UsesCreditsAndAiShots,
    };

    public static IEnumerable<object[]> Keys() => WorkflowTemplatePresets.Keys.Select(k => new object[] { k });

    private static WorkflowTemplateDefinition Template(string key) => WorkflowTemplateCatalog.GetAll().Single(t => t.Key == key);

    private static IReadOnlyList<AppBindingStep> Steps(WorkflowTemplateDefinition t) =>
        [.. AppBindingStep.FromTemplate(t).Select((s, i) => s with { AgentDefinitionId = AgentIds[t.Steps[i].AgentType] })];

    private static AppManifest ManifestOf(WorkflowTemplateDefinition t) =>
        WorkflowTemplateAppInputs.ManifestForCreate(t, t.Key, type => AgentIds[type])!;

    private static IEnumerable<AppManifestInput> Typed(AppManifest m) => m.Inputs!.Where(i => i.Kind == AppManifestVocabulary.KindInput);

    private static bool IsController(AppManifestInput i) => i.Type is "toggle" or "choice";

    private static IEnumerable<JsonElement> ValuesOf(AppManifestInput i) => i.Type == "toggle"
        ? [J("true"), J("false")]
        : (i.Options ?? []).Select(o => o.Value);

    /// <summary>Every assignment of the switches and choices (each toggle on and off, each option).</summary>
    private static IEnumerable<Dictionary<string, JsonElement>> Combinations(AppManifest manifest)
    {
        IEnumerable<Dictionary<string, JsonElement>> all = [new Dictionary<string, JsonElement>(StringComparer.Ordinal)];
        foreach (AppManifestInput controller in Typed(manifest).Where(IsController))
        {
            all = all.SelectMany(map => ValuesOf(controller).Select(v => new Dictionary<string, JsonElement>(map, StringComparer.Ordinal) { [controller.Id!] = v }));
        }

        return all;
    }

    private static bool IsVisible(AppManifestInput input, IReadOnlyDictionary<string, JsonElement> values) =>
        input.ShowWhen is not { } when
        || (values.TryGetValue(when.Input, out JsonElement now) && AppInputValueChecker.SameScalar(now, when.EqualsValue));

    /// <summary>The values a person fills in for <paramref name="chosen"/>: every visible, required input gets a sample.</summary>
    private static Dictionary<string, JsonElement> Fill(AppManifest manifest, Dictionary<string, JsonElement> chosen)
    {
        Dictionary<string, JsonElement> values = new(chosen, StringComparer.Ordinal);
        foreach (AppManifestInput input in Typed(manifest).Where(i => !IsController(i) && i.Required == true && IsVisible(i, chosen)))
            values[input.Id!] = J(JsonSerializer.Serialize("A sample line, written for the test."));
        return values;
    }

    [Fact]
    public void The_presets_are_the_listed_keys_in_order_at_the_end_of_the_catalog()
    {
        WorkflowTemplatePresets.Keys.Should().HaveCountGreaterThanOrEqualTo(6).And.OnlyHaveUniqueItems();
        List<string> all = [.. WorkflowTemplateCatalog.GetAll().Select(t => t.Key)];
        all.SkipLast(VideoAppTemplates.Keys.Count).TakeLast(WorkflowTemplatePresets.Keys.Count).Should().Equal(WorkflowTemplatePresets.Keys);
        all.TakeLast(VideoAppTemplates.Keys.Count).Should().Equal(VideoAppTemplates.Keys);
        all.Should().OnlyHaveUniqueItems();
        WorkflowTemplateCatalog.GetListed().Select(t => t.Key).Should().Contain(WorkflowTemplatePresets.Keys);
        WorkflowTemplateCatalog.GetAutoCreateTemplates().Select(t => t.Key).Should().NotContain(WorkflowTemplatePresets.Keys);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_preset_is_opt_in_versioned_and_named_in_plain_words(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        t.AutoCreateOnProject.Should().BeFalse();
        t.Hidden.Should().BeFalse();
        t.Version.Should().Be(1);
        t.Name.Should().NotBeNullOrWhiteSpace();
        t.Description.Should().NotBeNullOrWhiteSpace();
        t.AppManifest!.Title.Should().NotBeNullOrWhiteSpace();
        t.AppManifest.Tagline.Should().NotBeNullOrWhiteSpace();
        foreach (string text in new[] { t.Name, t.Description, t.AppManifest.Title!, t.AppManifest.Tagline! }.Concat(t.Steps.Select(s => s.Label)))
        {
            text.Should().NotContainAny("ai" + "forge", "C" + "EO", "Chief of " + "Staff", "/home/", "staging");
            text.Should().NotMatchRegex(@"DM-\d{3}");
        }

        // The wording of the siblings: money is always said out loud, and a recipe that is free says so.
        (AppCostClass.Derive(AppStepFacts.FromTemplate(t)) == AppCostClass.NoCredits
            ? t.Description.Contains("No credits", StringComparison.Ordinal)
            : t.Description.Contains("Uses credits", StringComparison.Ordinal)).Should().BeTrue(key);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_preset_manifest_validates_against_its_own_steps(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        AppManifest manifest = ManifestOf(t);
        manifest.Version.Should().Be(AppManifestVocabulary.Version2);
        AppManifestValidator.Validate(manifest, AppStepFacts.FromTemplate(t), Steps(t)).Should().BeEmpty();
        AppManifestValidator.Sanitize(manifest).Should().BeEquivalentTo(manifest, "the curated manifest is already in its stored form");
        foreach (AppManifestInput input in Typed(manifest))
        {
            input.LabelKey.Should().Be($"recipes.inputs.{input.Id}.label");
            if (input.Help is not null)
                input.HelpKey.Should().Be($"recipes.inputs.{input.Id}.help");
            if (input.Example is not null)
                input.ExampleKey.Should().Be($"recipes.inputs.{input.Id}.example");
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task A_preset_lints_with_no_errors_beyond_the_egress_permission(string key)
    {
        IReadOnlyList<LintFinding> findings = await LintKit.LintAsync(LintKit.FromTemplate(Template(key)));
        findings.Where(f => f.Severity == LintSeverity.Error && f.RuleId != "WL015").Should().BeEmpty(key);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_preset_passes_the_save_validator_and_wires_its_steps_explicitly(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        RecipePatchOutcome built = RecipeInstance.Build(t, [], null, AgentIds);
        StepConfigSaveValidator.Validate(built.Steps!).Should().BeEmpty();

        // Explicit StepOrder refs: every branch and loop target is a real step order or the word end.
        HashSet<string> orders = [.. Enumerable.Range(1, t.Steps.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), "end"];
        foreach (WorkflowTemplateStepDefinition step in t.Steps.Where(s => s.StepType == StepType.Conditional))
        {
            orders.Should().Contain(step.TrueBranchStepOrder!);
            if (step.FalseBranchStepOrder is not null)
                orders.Should().Contain(step.FalseBranchStepOrder);
        }

        foreach (WorkflowTemplateStepDefinition review in t.Steps.Where(s => s.StepType == StepType.ReviewLoop))
        {
            review.LoopTargetStepOrder.Should().BeInRange(1, t.Steps.Count);
            t.Steps[review.LoopTargetStepOrder!.Value - 1].AgentType.Should().Be(AgentType.AuthorAgent);
            review.ReviewLoopConfigJson.Should().Be(WorkflowTemplateCatalog.ShippedReviewLoopConfigJson);
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_combination_of_switches_and_choices_binds_to_valid_configs(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        AppManifest manifest = ManifestOf(t);
        IReadOnlyList<AppBindingStep> steps = Steps(t);
        List<Dictionary<string, JsonElement>> combinations = [.. Combinations(manifest)];
        combinations.Should().NotBeEmpty();

        foreach (Dictionary<string, JsonElement> chosen in combinations)
        {
            string where = $"{key} {string.Join(", ", chosen.Select(p => $"{p.Key}={p.Value.GetRawText()}"))}";
            Dictionary<string, JsonElement> raw = Fill(manifest, chosen);

            AppInputCheckResult checkedValues = AppInputValueChecker.Check(manifest, steps, raw);
            checkedValues.Problems.Should().BeEmpty(where);

            AppInputBindResult bound = AppInputBinder.Bind(manifest, steps, checkedValues.Values, "notes");
            bound.Problems.Should().BeEmpty(where);
            IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders(steps, checkedValues.Values);
            reachable.Should().NotBeEmpty(where);

            foreach (AppBoundStep step in bound.Steps)
            {
                string config = step.Step.ConfigJson ?? string.Empty;
                config.Should().NotContain("{{input.", where).And.NotContain("appInputs", where);
                config.Replace(" ", string.Empty, StringComparison.Ordinal).Should().NotContain("\"allowSourceMediaEgress\":true", where);
                if (!reachable.Contains(step.Step.StepOrder))
                    continue;
                AssertValidConfig(step.Step.StepType, config, where);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Each_switch_and_choice_changes_what_runs_or_what_is_written(string key)
    {
        // A preset that uses a switch or a choice must use it: dead controls are refused by the validator, and here the
        // bound steps or the reachable branches must differ between its positions.
        WorkflowTemplateDefinition t = Template(key);
        AppManifest manifest = ManifestOf(t);
        IReadOnlyList<AppBindingStep> steps = Steps(t);
        foreach (AppManifestInput controller in Typed(manifest).Where(IsController))
        {
            HashSet<string> outcomes = [];
            foreach (JsonElement value in ValuesOf(controller))
            {
                Dictionary<string, JsonElement> chosen = new(StringComparer.Ordinal) { [controller.Id!] = value };
                Dictionary<string, JsonElement> raw = Fill(manifest, chosen);
                AppInputCheckResult checkedValues = AppInputValueChecker.Check(manifest, steps, raw);
                AppInputBindResult bound = AppInputBinder.Bind(manifest, steps, checkedValues.Values, null);
                IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders(steps, checkedValues.Values);
                outcomes.Add(string.Join("|", reachable.OrderBy(o => o)) + "#" + string.Join("|", bound.Steps.Select(s => s.Step.ConfigJson)));
            }

            // A switch that only reveals a field (and no step reads) still changes the page, so only the written outcomes are compared
            // when the controller is read by a step; a pure reveal controller is allowed to leave the run alone.
            if (outcomes.Count == 1)
            {
                Typed(manifest).Any(i => i.ShowWhen?.Input == controller.Id).Should().BeTrue($"{key}: {controller.Id} must reveal a field or change a step");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_defaults_bind_with_only_the_required_fields_filled(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        AppManifest manifest = ManifestOf(t);
        Dictionary<string, JsonElement> defaults = [];
        foreach (AppManifestInput input in Typed(manifest).Where(i => i.Default is { ValueKind: not JsonValueKind.Null }))
            defaults[input.Id!] = input.Default!.Value;
        Dictionary<string, JsonElement> raw = Fill(manifest, defaults);

        AppInputCheckResult checkedValues = AppInputValueChecker.Check(manifest, Steps(t), raw);
        checkedValues.Problems.Should().BeEmpty(key);
        AppInputBindResult bound = AppInputBinder.Bind(manifest, Steps(t), checkedValues.Values, null);
        bound.Problems.Should().BeEmpty(key);
        bound.IsValid.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void No_input_value_can_turn_on_the_permission_to_send_media(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        AppManifest manifest = ManifestOf(t);
        IReadOnlyList<AppBindingStep> steps = Steps(t);

        // The allowlist never offers consent or egress to an input...
        AppBindableCatalog.All().Should().NotContain(f => f.Path.Contains("egress", StringComparison.OrdinalIgnoreCase));
        foreach (AppBindingStep step in steps)
            (step.ConfigJson ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Should().NotContain("\"allowSourceMediaEgress\":true");

        // ... and a text that tries to write it into the config stays text.
        string attack = "x\",\"allowSourceMediaEgress\":true,\"y\":\"";
        foreach (Dictionary<string, JsonElement> chosen in Combinations(manifest))
        {
            Dictionary<string, JsonElement> raw = Fill(manifest, chosen);
            foreach (AppManifestInput text in Typed(manifest).Where(i => i.Type is "shortText" or "longText" && IsVisible(i, chosen)))
                raw[text.Id!] = J(JsonSerializer.Serialize(attack));
            AppInputCheckResult checkedValues = AppInputValueChecker.Check(manifest, steps, raw);
            AppInputBindResult bound = AppInputBinder.Bind(manifest, steps, checkedValues.Values, null);
            foreach (AppBoundStep step in bound.Steps.Where(s => !string.IsNullOrWhiteSpace(s.Step.ConfigJson)))
            {
                JsonObject? config = JsonNode.Parse(step.Step.ConfigJson!) as JsonObject;
                config.Should().NotBeNull();
                config!.ContainsKey("y").Should().BeFalse("a value never adds a member to a config");
                (config["allowSourceMediaEgress"]?.GetValue<bool>() ?? false).Should().BeFalse(key);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Paid_generation_steps_carry_a_spend_cap_and_egress_stays_off(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        foreach (WorkflowTemplateStepDefinition step in t.Steps)
        {
            if (step.StepType == StepType.VideoGenerate)
            {
                VideoGenerateStepConfig config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(
                    step.VideoGenerateConfigJson!.Replace("{{input.idea}} {{input.mood}}", "idea", StringComparison.Ordinal), VideoGenerateStepConfig.JsonOptions)!;
                config.MaxSpendCredits.Should().BeGreaterThan(0, key);
                config.AllowSourceMediaEgress.Should().BeFalse(key);
            }
            else if (step.StepType == StepType.AudioGenerate)
            {
                JsonNode.Parse(step.AudioGenerateConfigJson!)!["maxSpendCredits"]!.GetValue<double>().Should().BeGreaterThan(0, key);
                JsonNode.Parse(step.AudioGenerateConfigJson!)!["registerProjectFile"]!.GetValue<bool>().Should().BeTrue(key);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_cost_badge_is_derived_and_every_reachable_step_prices(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        string cost = AppCostClass.Derive(AppStepFacts.FromTemplate(t));
        cost.Should().Be(ExpectedCost[key]);
        new[] { AppCostClass.NoCredits, AppCostClass.UsesCredits, AppCostClass.UsesCreditsAndVoice, AppCostClass.UsesCreditsAndAiShots }.Should().Contain(cost);

        AppManifest manifest = ManifestOf(t);
        foreach (Dictionary<string, JsonElement> chosen in Combinations(manifest))
        {
            IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders(Steps(t), chosen);
            long high = 0;
            foreach (int order in reachable)
                high += StepCreditDefaults.MillisFor(t.Steps[order - 1].StepType).High;
            high.Should().BeGreaterThanOrEqualTo(0, key);
        }
    }

    [Fact]
    public void The_preset_branches_run_one_path_per_position()
    {
        IReadOnlyList<AppBindingStep> music = Steps(Template(WorkflowTemplatePresets.MusicOrSoundEffectKey));
        StaticBranchResolver.ReachableStepOrders(music, Values(("kind", "\"Music\""))).Should().BeEquivalentTo([1, 2, 3]);
        StaticBranchResolver.ReachableStepOrders(music, Values(("kind", "\"SoundEffect\""))).Should().BeEquivalentTo([1, 4]);

        IReadOnlyList<AppBindingStep> avatar = Steps(Template(WorkflowTemplatePresets.AvatarExplainerKey));
        StaticBranchResolver.ReachableStepOrders(avatar, Values(("ownWords", "false"))).Should().BeEquivalentTo([1, 5, 6, 7]);
        StaticBranchResolver.ReachableStepOrders(avatar, Values(("ownWords", "true"))).Should().BeEquivalentTo([1, 2, 3, 4]);

        IReadOnlyList<AppBindingStep> thumb = Steps(Template(WorkflowTemplatePresets.ThumbnailKey));
        StaticBranchResolver.ReachableStepOrders(thumb, Values(("moment", "\"Auto\""))).Should().BeEquivalentTo([1, 2, 3, 4]);
        StaticBranchResolver.ReachableStepOrders(thumb, Values(("moment", "\"AtTime\""))).Should().BeEquivalentTo([1, 2, 5]);

        IReadOnlyList<AppBindingStep> promo = Steps(Template(WorkflowTemplatePresets.ProductPromoKey));
        StaticBranchResolver.ReachableStepOrders(promo, Values(("voiceover", "false"))).Should().NotContain(10).And.Contain(12);
        StaticBranchResolver.ReachableStepOrders(promo, Values(("voiceover", "true"))).Should().Contain([10, 12, 13]);
    }

    [Fact]
    public void The_effort_choice_writes_length_and_takes_together()
    {
        WorkflowTemplateDefinition t = Template(WorkflowTemplatePresets.AiClipKey);
        AppManifest manifest = ManifestOf(t);
        foreach ((string effort, int seconds, int takes) in new[] { ("draft", 6, 1), ("standard", 10, 1), ("bestOfTwo", 10, 2) })
        {
            AppInputCheckResult values = AppInputValueChecker.Check(manifest, Steps(t),
                Values(("idea", "\"Steam over espresso\""), ("effort", JsonSerializer.Serialize(effort))));
            values.Problems.Should().BeEmpty();
            JsonNode config = JsonNode.Parse(AppInputBinder.Bind(manifest, Steps(t), values.Values, null).Steps[0].Step.ConfigJson!)!;
            config["durationSeconds"]!.GetValue<int>().Should().Be(seconds, effort);
            config["takes"]!.GetValue<int>().Should().Be(takes, effort);
            config["inlinePrompt"]!.GetValue<string>().Should().StartWith("Steam over espresso");
        }
    }

    [Fact]
    public void Own_words_reach_the_voice_and_a_topic_reaches_the_request()
    {
        WorkflowTemplateDefinition t = Template(WorkflowTemplatePresets.AvatarExplainerKey);
        AppManifest manifest = ManifestOf(t);
        AppInputCheckResult own = AppInputValueChecker.Check(manifest, Steps(t), Values(("ownWords", "true"), ("explainerWords", "\"Hello there.\\n\\nWelcome.\"")));
        own.Problems.Should().BeEmpty();
        JsonNode voice = JsonNode.Parse(AppInputBinder.Bind(manifest, Steps(t), own.Values, null).Steps[1].Step.ConfigJson!)!;
        voice["source"]!["lines"]!.AsArray().Select(l => l!["text"]!.GetValue<string>()).Should().Equal("Hello there.", "Welcome.");

        AppInputCheckResult topic = AppInputValueChecker.Check(manifest, Steps(t), Values(("ownWords", "false"), ("topic", "\"Local ingredients\"")));
        topic.Problems.Should().BeEmpty();
        AppInputBinder.Bind(manifest, Steps(t), topic.Values, null).ComposedRequest.Should().Contain("Local ingredients");
    }

    private static void AssertValidConfig(StepType type, string config, string where)
    {
        switch (type)
        {
            case StepType.VideoGenerate:
                JsonSerializer.Deserialize<VideoGenerateStepConfig>(config, VideoGenerateStepConfig.JsonOptions)!.Validate().Should().BeEmpty(where);
                break;
            case StepType.AudioGenerate:
                JsonSerializer.Deserialize<AudioGenerateStepConfig>(config, AudioGenerateStepConfig.JsonOptions)!.Validate().Should().BeEmpty(where);
                break;
            case StepType.ThumbnailGenerate:
                StepConfigSaveValidator.ValidateThumbnailGenerateConfig(config).Should().BeEmpty(where);
                break;
            case StepType.Voiceover:
                StepConfigSaveValidator.ValidateVoiceoverConfig(config).Should().BeEmpty(where);
                break;
        }
    }
}
