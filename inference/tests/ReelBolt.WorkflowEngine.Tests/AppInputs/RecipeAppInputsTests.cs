using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;
using ReelBolt.WorkflowEngine.Tests.Docs;
using ReelBolt.WorkflowEngine.Tests.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// The app-input edition of the built-in recipes (docs/design/app-inputs.md 12.2, 9.5 and 22, lane L7): with the switch off
/// the catalog is today's; with it on every curated manifest validates against its own steps, binds its defaults cleanly,
/// lints like the rest of the catalog, names catalogue keys that exist in English and Italian, and the two avatar templates
/// are hidden behind the merged "Avatar clip".
/// </summary>
public class RecipeAppInputsTests
{
    private static IReadOnlyList<WorkflowTemplateDefinition> Off => WorkflowTemplateCatalog.BaseTemplates;

    private static IReadOnlyList<WorkflowTemplateDefinition> On => WorkflowTemplateCatalog.GetAll();

    /// <summary>One stable agent id per agent type, standing in for the built-in rows provisioning would pick.</summary>
    private static readonly Dictionary<AgentType, Guid> AgentIds = Enum.GetValues<AgentType>().ToDictionary(t => t, _ => Guid.NewGuid());

    public static IEnumerable<object[]> WithInputs() =>
        On.Where(t => !t.Hidden && t.AppManifest?.Inputs?.Any(i => i.Kind == AppManifestVocabulary.KindInput) == true)
            .Select(t => new object[] { t.Key });

    public static IEnumerable<object[]> Listed() => WorkflowTemplateCatalog.GetListed().Select(t => new object[] { t.Key });

    private static WorkflowTemplateDefinition Template(string key) => On.Single(t => t.Key == key);

    private static IReadOnlyList<AppBindingStep> Steps(WorkflowTemplateDefinition t) =>
        [.. AppBindingStep.FromTemplate(t).Select((s, i) => s with { AgentDefinitionId = AgentIds[t.Steps[i].AgentType] })];

    private static AppManifest ManifestOf(WorkflowTemplateDefinition t, string? requestedKey = null) =>
        WorkflowTemplateAppInputs.ManifestForCreate(t, requestedKey ?? t.Key, type => AgentIds[type])!;

    [Fact]
    public void The_edition_keeps_every_key_adds_avatar_clip_after_the_budget_template_and_the_presets_last()
    {
        List<string> expected = [.. Off.Select(t => t.Key)];
        expected.Insert(expected.IndexOf(WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey) + 1, WorkflowTemplateCatalog.AvatarClipTemplateKey);
        expected.AddRange(WorkflowTemplatePresets.Keys);
        expected.AddRange(VideoAppTemplates.Keys);
        On.Select(t => t.Key).Should().Equal(expected);
    }

    [Fact]
    public void The_old_avatar_templates_are_hidden_unchanged_and_superseded_by_avatar_clip()
    {
        foreach (string key in new[] { WorkflowTemplateCatalog.SpokespersonClipTemplateKey, WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey })
        {
            WorkflowTemplateDefinition hidden = Template(key);
            WorkflowTemplateDefinition before = Off.Single(t => t.Key == key);
            hidden.Hidden.Should().BeTrue();
            hidden.SupersededBy.Should().Be(WorkflowTemplateCatalog.AvatarClipTemplateKey);
            hidden.Steps.Should().Equal(before.Steps);
            hidden.Version.Should().Be(before.Version);
            WorkflowTemplateCatalog.GetListed().Should().NotContain(t => t.Key == key);
            WorkflowTemplateCatalog.GetByKey(key).Should().BeSameAs(hidden, "a workflow made from it still finds its template");
        }

        WorkflowTemplateCatalog.GetAutoCreateTemplates().Select(t => t.Key).Should().Equal(WorkflowTemplateCatalog.StarterTemplateKey);
    }

    [Fact]
    public void The_version_moves_exactly_where_the_steps_changed()
    {
        foreach (WorkflowTemplateDefinition before in Off)
        {
            WorkflowTemplateDefinition after = Template(before.Key);
            bool changed = !after.Steps.SequenceEqual(before.Steps);
            after.Version.Should().Be(changed ? before.Version + 1 : before.Version, before.Key);
        }
    }

    [Theory]
    [MemberData(nameof(WithInputs))]
    public void Every_curated_manifest_validates_against_its_own_steps(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        AppManifest manifest = ManifestOf(t);
        manifest.Version.Should().Be(AppManifestVocabulary.Version2);
        AppManifestValidator.Validate(manifest, AppStepFacts.FromTemplate(t), Steps(t)).Should().BeEmpty();
        AppManifestValidator.Sanitize(manifest).Should().BeEquivalentTo(manifest, "the curated manifest is already in its stored form");
    }

    [Theory]
    [MemberData(nameof(Listed))]
    public void Every_listed_template_passes_the_save_validator_with_app_inputs_on(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        RecipePatchOutcome built = RecipeInstance.Build(t, [], null, AgentIds);
        StepConfigSaveValidator.Validate(built.Steps!).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(WithInputs))]
    public void The_defaults_bind_to_plain_valid_configs(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        foreach (bool lite in key == WorkflowTemplateCatalog.AvatarClipTemplateKey ? new[] { false, true } : [false])
        {
            AppManifest manifest = ManifestOf(t);
            Dictionary<string, JsonElement> raw = [];
            if (key == WorkflowTemplateCatalog.AvatarClipTemplateKey)
                raw["liteMode"] = J(lite ? "true" : "false");
            foreach (AppManifestInput input in manifest.Inputs!.Where(i => i.Kind == AppManifestVocabulary.KindInput && i.Required == true))
                raw[input.Id!] = J("\"the bike's number plate\"");

            AppInputCheckResult checkedValues = AppInputValueChecker.Check(manifest, Steps(t), raw);
            checkedValues.Problems.Should().BeEmpty($"{key} lite={lite}");

            AppInputBindResult bound = AppInputBinder.Bind(manifest, Steps(t), checkedValues.Values, "notes");
            bound.Problems.Should().BeEmpty($"{key} lite={lite}");
            foreach (AppBoundStep step in bound.Steps)
            {
                (step.Step.ConfigJson ?? string.Empty).Should().NotContain("{{input.").And.NotContain("appInputs");
                if (step.Step.StepType == StepType.VideoGenerate && step.Step.ConfigJson is { } json)
                    JsonSerializer.Deserialize<VideoGenerateStepConfig>(json, VideoGenerateStepConfig.JsonOptions)!.Validate().Should().BeEmpty();
            }
        }
    }

    [Fact]
    public void Avatar_clip_runs_one_branch_per_switch_position_and_prices_only_that_one()
    {
        IReadOnlyList<AppBindingStep> steps = Steps(Template(WorkflowTemplateCatalog.AvatarClipTemplateKey));
        StaticBranchResolver.ReachableStepOrders(steps, Values(("liteMode", "true"))).Should().BeEquivalentTo([1, 2, 3, 4]);
        StaticBranchResolver.ReachableStepOrders(steps, Values(("liteMode", "false"))).Should().BeEquivalentTo([1, 5]);
    }

    [Theory]
    [InlineData(false, "whatTheyDo", "Waves at the camera")]
    [InlineData(true, "whatTheySay", "Welcome to Caffè Lume!")]
    public void Avatar_clip_runs_with_its_prompt_filled_in_either_mode(bool lite, string field, string text)
    {
        // The value typed into the visible field is accepted and reaches the branch that runs: a non-empty prompt for the
        // spokesperson clip, the exact lines for the lite narration; neither mode asks for the other's field.
        WorkflowTemplateDefinition t = Template(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        AppManifest manifest = ManifestOf(t);
        AppInputCheckResult values = AppInputValueChecker.Check(manifest, Steps(t),
            Values(("liteMode", lite ? "true" : "false"), (field, JsonSerializer.Serialize(text))));
        values.Problems.Should().BeEmpty();
        values.Values.Should().ContainKey(field);

        AppInputBindResult bound = AppInputBinder.Bind(manifest, Steps(t), values.Values, null);
        bound.Problems.Should().BeEmpty();
        IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders(Steps(t), values.Values);
        foreach (AppBoundStep step in bound.Steps.Where(s => s.Step.StepType == StepType.VideoGenerate && reachable.Contains(s.Step.StepOrder)))
        {
            VideoGenerateStepConfig config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(step.Step.ConfigJson!, VideoGenerateStepConfig.JsonOptions)!;
            config.InlinePrompt.Should().NotBeNullOrWhiteSpace($"lite={lite}");
            config.Validate().Should().BeEmpty($"lite={lite}");
        }

        // The run page blocks Run on an empty notes box when the workflow requires a description; this app's page has no
        // notes box (its required fields are the brief), so the flag would leave a block nobody can clear.
        manifest.Inputs!.Should().NotContain(i => i.Kind == AppManifestVocabulary.KindNotes);
        t.RequiresUserInput.Should().BeFalse("the page has no notes box, the fields carry the brief");
    }

    [Fact]
    public void Avatar_clip_fills_the_whole_spokesperson_prompt_from_what_they_do()
    {
        WorkflowTemplateDefinition t = Template(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        AppManifest manifest = ManifestOf(t);
        AppInputCheckResult values = AppInputValueChecker.Check(manifest, Steps(t),
            Values(("liteMode", "false"), ("whatTheyDo", "\"Waves at the camera\""), ("clipLength", "10"), ("clipShape", "\"16:9\"")));
        values.Problems.Should().BeEmpty();
        AppBoundStep clip = AppInputBinder.Bind(manifest, Steps(t), values.Values, null).Steps[4];
        using JsonDocument doc = JsonDocument.Parse(clip.Step.ConfigJson!);
        doc.RootElement.GetProperty("inlinePrompt").GetString().Should().Be("Waves at the camera Medium close-up, steady camera.");
        doc.RootElement.GetProperty("durationSeconds").GetInt32().Should().Be(10);
        doc.RootElement.GetProperty("ratio").GetString().Should().Be("16:9");
        doc.RootElement.GetProperty("allowSourceMediaEgress").GetBoolean().Should().BeFalse("the permission stays the customer's");
    }

    [Fact]
    public void The_lite_branch_speaks_what_they_say_exactly_one_line_per_paragraph()
    {
        // F1 (docs/design/app-inputs.md 27): the words go straight into the Voiceover's Inline lines, no writer agent between.
        WorkflowTemplateDefinition t = Template(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        t.Version.Should().Be(2);
        t.Steps.Should().NotContain(s => s.AgentType == AgentType.ScriptwriterAgent);
        AppManifest manifest = ManifestOf(t);
        manifest.RequestTemplate.Should().BeNull("no agent reads the request any more");
        AppRequestComposer.Compose(manifest, Values(("whatTheySay", "\"Hi\"")), null).Should().BeNull();

        AppInputCheckResult values = AppInputValueChecker.Check(manifest, Steps(t),
            Values(("liteMode", "true"), ("whatTheySay", "\"Welcome to Caffè Lume!\\n\\nOur autumn menu starts today.\"")));
        values.Problems.Should().BeEmpty();
        AppBoundStep voice = AppInputBinder.Bind(manifest, Steps(t), values.Values, null).Steps[1];
        VoiceoverStepConfig config = VoiceoverStepConfig.Parse(voice.Step.ConfigJson!)!;
        config.Source.Kind.Should().Be(VoiceoverSourceKind.Inline);
        config.Source.Sequential.Should().BeTrue();
        config.Source.Lines!.Select(l => (l.Text, l.StartSec)).Should().Equal(("Welcome to Caffè Lume!", 0d), ("Our autumn menu starts today.", 0d));
        config.VoiceFromAvatar.Should().BeTrue();
    }

    [Fact]
    public void The_lite_branch_offers_the_picture_model_of_the_still()
    {
        WorkflowTemplateDefinition t = Template(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        AppManifestInput picture = ManifestOf(t).Inputs!.Single(i => i.Id == "pictureModel");
        picture.Capability.Should().Be("ImageEdit");
        AppInputPlacement.IsUnderMore(picture).Should().BeTrue();
        AppBindingAnalysis.Analyze(ManifestOf(t), Steps(t)).UsesOf("pictureModel").Should().ContainSingle()
            .Which.Field!.Path.Should().Be(AppBindableCatalog.StillModelKey);
    }

    [Fact]
    public void The_budget_key_creates_avatar_clip_with_lite_mode_on()
    {
        WorkflowTemplateDefinition t = Template(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        AppManifestInput Lite(string key) => ManifestOf(t, key).Inputs!.Single(i => i.Id == "liteMode");
        Lite(WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey).Default!.Value.ValueKind.Should().Be(JsonValueKind.True);
        Lite(WorkflowTemplateCatalog.SpokespersonClipTemplateKey).Default!.Value.ValueKind.Should().Be(JsonValueKind.False);
        Lite(WorkflowTemplateCatalog.AvatarClipTemplateKey).Default!.Value.ValueKind.Should().Be(JsonValueKind.False);
    }

    [Fact]
    public void Agent_model_inputs_are_given_the_built_in_agent_they_pick_for()
    {
        WorkflowTemplateDefinition t = Template(WorkflowTemplateCatalog.StarterTemplateKey);
        AppManifest manifest = ManifestOf(t);
        manifest.Inputs!.Single(i => i.Id == "scriptModel").Agent.Should().Be(AgentIds[AgentType.ScriptwriterAgent]);
        manifest.Inputs!.Single(i => i.Id == "builderModel").Agent.Should().Be(AgentIds[AgentType.AuthorAgent]);
        t.AppManifest!.Inputs!.Should().OnlyContain(i => i.Agent == null, "the catalog names agent types, never ids");
    }

    [Fact]
    public void An_empty_call_to_action_leaves_the_request_exactly_as_today()
    {
        AppManifest manifest = ManifestOf(Template(WorkflowTemplateCatalog.StarterTemplateKey));
        AppRequestComposer.Compose(manifest, Values(), "A 15 s reel for cafe owners").Should().Be("A 15 s reel for cafe owners");
        AppRequestComposer.Compose(manifest, Values(), null).Should().BeNull();
        AppRequestComposer.Compose(manifest, Values(("callToAction", "\"Try it free\"")), "notes")
            .Should().Be("End the video with this call to action: \"Try it free\"\n\nNotes for this run:\nnotes");
    }

    /// <summary>Same exceptions as the catalog lint: a template never pre-consents to media leaving the host (WL015).</summary>
    [Theory]
    [MemberData(nameof(Listed))]
    public async Task Every_listed_template_lints_with_no_errors_beyond_the_egress_permission(string key)
    {
        IReadOnlyList<LintFinding> findings = await LintKit.LintAsync(LintKit.FromTemplate(Template(key)));
        findings.Where(f => f.Severity == LintSeverity.Error && f.RuleId != "WL015").Should().BeEmpty(key);
    }

    [Fact]
    public void Every_catalogue_key_exists_in_english_and_italian()
    {
        string messages = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(AssistantKbPaths.KbDir()))!, "web", "messages");
        List<string> keys = [];
        foreach (WorkflowTemplateDefinition t in On)
        {
            foreach (AppManifestInput input in t.AppManifest?.Inputs ?? [])
            {
                keys.AddRange(new[] { input.LabelKey, input.HelpKey, input.ExampleKey }.OfType<string>());
                keys.AddRange((input.Options ?? []).Select(o => o.LabelKey).OfType<string>());
            }
        }

        keys.Should().NotBeEmpty();
        foreach (string locale in new[] { "en", "it" })
        {
            using JsonDocument apps = JsonDocument.Parse(File.ReadAllText(Path.Combine(messages, locale, "apps.json")));
            keys.Distinct().Where(k => !Has(apps.RootElement, k)).Should().BeEmpty($"web/messages/{locale}/apps.json must carry every recipe key");
        }

        static bool Has(JsonElement root, string key)
        {
            JsonElement node = root;
            foreach (string part in key.Split('.'))
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node))
                    return false;
            }

            return node.ValueKind == JsonValueKind.String;
        }
    }
}
