using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The pure app logic: the deriver over every template, the validator matrix, orphans and the cost class.</summary>
public class AppManifestTests
{
    public static IEnumerable<object[]> Templates() =>
        WorkflowTemplateCatalog.GetAll().Select(t => new object[] { t.Key });

    private static AppStepFacts Agent(AgentType type) => new(StepType.Agent, type);

    private static AppStepFacts VideoAnalyzeOpenSlot() => new(StepType.VideoAnalyze, AgentType.VideoTransform, TakesProjectVideoFile: true);

    private static JsonElement J(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static AppManifestInput Brief(string field, string label = "L", string? def = null, string? choices = null) =>
        new("brief", Field: field, Label: label, Default: def is null ? null : J(def),
            Choices: choices is null ? null : JsonSerializer.Deserialize<List<JsonElement>>(choices));

    private static AppManifest With(params AppManifestInput[] inputs) => new(Inputs: inputs);

    private static readonly AppStepFacts[] PromoSteps =
    [
        Agent(AgentType.ScriptwriterAgent), Agent(AgentType.AuthorAgent)
    ];

    [Fact]
    public void There_are_at_least_23_templates()
    {
        WorkflowTemplateCatalog.GetAll().Count.Should().BeGreaterThanOrEqualTo(23);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Every_template_derives_a_deterministic_manifest_within_the_input_cap(string key)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(key)!;
        IReadOnlyList<AppStepFacts> facts = AppStepFacts.FromTemplate(template);

        AppManifest first = AppManifestDeriver.Derive(facts, template.RequiresUserInput);
        AppManifest second = AppManifestDeriver.Derive(facts, template.RequiresUserInput);

        AppManifestJson.Serialize(first).Should().Be(AppManifestJson.Serialize(second));
        first.Inputs!.Count.Should().BeLessThanOrEqualTo(AppManifestVocabulary.MaxInputs);
        // A derived manifest is always valid against its own steps.
        AppManifestValidator.Validate(first, facts).Should().BeEmpty();
        AppCostClass.Derive(facts).Should().NotBeNullOrEmpty();
        AppManifestDeriver.Effective(template.AppManifest, facts, template.RequiresUserInput, template.Name).Title.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void The_pace_is_shown_in_the_main_inputs_of_every_template_with_a_compile(string key)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(key)!;
        IReadOnlyList<AppStepFacts> facts = AppStepFacts.FromTemplate(template);
        if (AppManifestDeriver.Derive(facts, template.RequiresUserInput).Inputs!.All(i => i.Field != BriefFieldConsumers.Pace))
            return;

        AppManifest authored = template.AppManifest ?? WorkflowTemplateAppManifests.ByKey.GetValueOrDefault(key) ?? new AppManifest();
        AppManifest effective = AppManifestDeriver.Effective(authored, facts, template.RequiresUserInput, template.Name);
        effective.Inputs!.Where(i => !AppInputPlacement.IsUnderMore(i)).Should()
            .Contain(i => i.Field == BriefFieldConsumers.Pace, key);

        // The worst case: an own list that already shows the full cap.
        AppManifest full = new(Inputs: Enumerable.Range(0, AppManifestVocabulary.MaxInputs)
            .Select(n => new AppManifestInput(AppManifestVocabulary.KindNotes, Label: "n" + n)).ToList());
        AppManifestDeriver.Effective(full, facts, template.RequiresUserInput, template.Name).Inputs!
            .Where(i => !AppInputPlacement.IsUnderMore(i)).Should().Contain(i => i.Field == BriefFieldConsumers.Pace, key);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void A_curated_template_manifest_is_valid_against_its_steps(string key)
    {
        // The curated pages as authored (the served catalog adds app inputs on top, checked in RecipeAppInputsTests).
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(key)!;
        if (!WorkflowTemplateAppManifests.ByKey.TryGetValue(key, out AppManifest? authored))
            return;
        AppManifestValidator.Validate(authored, AppStepFacts.FromTemplate(template)).Should().BeEmpty();
    }

    private static readonly string[] CuratedKeys =
    [
        "quick-win-promo", "quick-win-promo-ds", "lean-context-promo", "video-derush-edit", "video-derush-edit-room",
        "video-derush-edit-music", "video-derush-edit-voiceover", "video-derush-edit-broll", "green-screen-insert",
        "quick-win-promo-plates", "testimonial-reel"
    ];

    [Fact]
    public void The_ten_most_used_templates_ship_curated_manifests_and_the_rest_derive()
    {
        foreach (string key in CuratedKeys)
            WorkflowTemplateAppManifests.ByKey.Should().ContainKey(key);
        WorkflowTemplateAppManifests.ByKey.Should().NotContainKey("narrated-story");
        // The subtitles lane (#548) ships two more curated manifests beyond the ten most used templates.
        WorkflowTemplateAppManifests.ByKey.Keys.Should()
            .BeEquivalentTo(CuratedKeys.Concat(["video-subtitles", "video-subtitles-translated", "video-sound-effects"]));
    }

    [Fact]
    public void Every_curated_manifest_has_a_title_a_tagline_a_category_and_a_known_style()
    {
        foreach (string key in CuratedKeys)
        {
            AppManifest manifest = WorkflowTemplateAppManifests.ByKey[key];
            manifest.Title.Should().NotBeNullOrWhiteSpace(key);
            manifest.Tagline.Should().NotBeNullOrWhiteSpace(key);
            AppManifestVocabulary.Categories.Should().Contain(manifest.Category!, key);
            manifest.Inputs!.Count.Should().BeInRange(1, AppManifestVocabulary.MaxInputs, key);
        }
    }

    [Fact]
    public void The_footage_templates_expose_a_video_slot_and_the_narrated_one_exposes_a_voice()
    {
        foreach (string key in new[] { "video-derush-edit", "video-derush-edit-room", "video-derush-edit-music", "video-derush-edit-voiceover", "video-derush-edit-broll", "green-screen-insert" })
            WorkflowTemplateAppManifests.ByKey[key].Inputs!.Should().Contain(i => i.Kind == "files" && i.Role == "video", key);
        WorkflowTemplateAppManifests.ByKey["video-derush-edit-voiceover"].Inputs!.Should().Contain(i => i.Field == "voice");
        WorkflowTemplateAppManifests.ByKey["video-derush-edit-room"].Inputs!.Should().NotContain(i => i.Field == "voice");
    }

    [Fact]
    public void Scriptwriter_exposes_language_length_format_voice_style_and_design_but_not_files()
    {
        AppManifest derived = AppManifestDeriver.Derive(PromoSteps, requiresUserInput: false);

        derived.Inputs!.Select(i => i.Field ?? i.Kind).Should().Equal("language", "lengthSec", "format", "voice", "stylePreset", "designSystem");
        derived.Category.Should().Be("promo");
    }

    [Fact]
    public void A_video_analyze_slot_derives_a_video_files_input_and_notes_follow_RequiresUserInput()
    {
        AppManifest derived = AppManifestDeriver.Derive([VideoAnalyzeOpenSlot(), Agent(AgentType.VideoStoryEditor)], requiresUserInput: true);

        derived.Inputs!.Should().Contain(i => i.Kind == "files" && i.Role == "video");
        derived.Inputs!.Last().Kind.Should().Be("notes");
        derived.Category.Should().Be("footage");
    }

    [Fact]
    public void An_overlay_slot_derives_only_when_the_analyze_step_emits_overlay_offers()
    {
        const string offers = """{"version":1,"source":{"kind":"ProjectFile"},"emitOverlayOffers":true}""";
        const string noOffers = """{"version":1,"source":{"kind":"ProjectFile"}}""";

        AppStepFacts accepting = AppStepFacts.From(StepType.VideoAnalyze, AgentType.VideoTransform, offers);
        AppStepFacts refusing = AppStepFacts.From(StepType.VideoAnalyze, AgentType.VideoTransform, noOffers);
        accepting.TakesOverlayFile.Should().BeTrue();
        refusing.TakesOverlayFile.Should().BeFalse();
        BriefFieldConsumers.AcceptsFileRole("overlay", [accepting]).Should().BeTrue();
        BriefFieldConsumers.AcceptsFileRole("overlay", [refusing]).Should().BeFalse();

        AppManifestInput? slot = AppManifestDeriver.Derive([accepting], requiresUserInput: false).Inputs!
            .SingleOrDefault(i => i.Kind == "files" && i.Role == "overlay");
        slot.Should().NotBeNull();
        slot!.Label.Should().Be("A picture or video to show");
        slot.Min.Should().Be(0);
        slot.Max.Should().Be(4);

        AppManifestDeriver.Derive([refusing], requiresUserInput: false).Inputs!
            .Should().NotContain(i => i.Kind == "files" && i.Role == "overlay");
    }

    [Fact]
    public void An_overlay_slot_with_no_step_that_emits_offers_is_a_WL018_orphan()
    {
        AppManifest manifest = With(new AppManifestInput("files", Role: "overlay", Label: "A picture or video to show", Min: 0, Max: 4));

        IReadOnlyList<AppManifestOrphan> orphans = AppManifestValidator.FindOrphans(manifest, [VideoAnalyzeOpenSlot()]);

        orphans.Should().ContainSingle();
        orphans[0].Code.Should().Be("WL018");
        orphans[0].Message.Should().Contain("A picture or video to show");

        AppStepFacts emits = AppStepFacts.From(
            StepType.VideoAnalyze, AgentType.VideoTransform,
            """{"version":1,"source":{"kind":"ProjectFile"},"emitOverlayOffers":true}""");
        AppManifestValidator.Validate(manifest, [emits]).Should().BeEmpty();
    }

    [Fact]
    public void The_base_edit_exposes_the_overlay_slot_and_card_sound_and_the_other_footage_edits_do_not()
    {
        AppManifest baseManifest = WorkflowTemplateAppManifests.ByKey["video-derush-edit"];
        baseManifest.Inputs!.Should().Contain(i => i.Kind == "files" && i.Role == "overlay" && i.Min == 0 && i.Max == 4);
        AppManifestInput cardAudio = baseManifest.Inputs!.Single(i => i.Field == "overlayCardAudio");
        cardAudio.Default!.Value.GetString().Should().Be("agent");
        cardAudio.Choices!.Select(c => c.GetString()).Should().Equal("agent", "keep", "mute");

        foreach (string key in new[] { "video-derush-edit-room", "video-derush-edit-music", "video-derush-edit-voiceover", "video-derush-edit-broll" })
        {
            WorkflowTemplateAppManifests.ByKey[key].Inputs!
                .Should().NotContain(i => i.Kind == "files" && i.Role == "overlay", key);
            WorkflowTemplateAppManifests.ByKey[key].Inputs!
                .Should().NotContain(i => i.Field == "overlayCardAudio", key);
        }
    }

    [Fact]
    public void The_testimonial_recipe_exposes_the_overlay_slot_and_card_sound_with_no_orphans()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.TestimonialReelTemplateKey)!;
        AppManifest manifest = WorkflowTemplateAppManifests.ByKey[template.Key];
        AppStepFacts[] facts = [.. AppStepFacts.FromTemplate(template)];

        // The overlay slot is accepted because the analyze step emits offers (WL018 otherwise).
        facts.Single(f => f.StepType == StepType.VideoAnalyze).TakesOverlayFile.Should().BeTrue();
        manifest.Inputs!.Should().Contain(i => i.Kind == "files" && i.Role == "video" && i.Min == 1);

        AppManifestInput overlay = manifest.Inputs!.Single(i => i.Kind == "files" && i.Role == "overlay");
        overlay.Label.Should().Be("A picture or video to show");
        overlay.Min.Should().Be(0);
        overlay.Max.Should().Be(4);

        AppManifestInput cardAudio = manifest.Inputs!.Single(i => i.Field == BriefFieldConsumers.OverlayCardAudio);
        cardAudio.Kind.Should().Be(AppManifestVocabulary.KindBrief);
        cardAudio.Default!.Value.GetString().Should().Be("agent");
        cardAudio.Choices!.Select(c => c.GetString()).Should().Equal("agent", "keep", "mute");
        AppManifestVocabulary.BriefFields.Should().Contain(BriefFieldConsumers.OverlayCardAudio);

        IReadOnlyList<AppManifestOrphan> orphans = AppManifestValidator.FindOrphans(manifest, facts);
        orphans.Should().BeEmpty();
        AppManifestValidator.Validate(manifest, facts).Should().BeEmpty();

        // The open analyze step derives the same overlay slot when no curated manifest is attached.
        AppManifestDeriver.Derive(facts, requiresUserInput: true).Inputs!
            .Should().Contain(i => i.Kind == "files" && i.Role == "overlay" && i.Min == 0 && i.Max == 4);
    }

    [Fact]
    public void A_pinned_project_file_is_not_a_slot()
    {
        string pinned = """{"version":1,"source":{"kind":"ProjectFile","projectFileId":"11111111-1111-1111-1111-111111111111"}}""";
        string open = """{"version":1,"source":{"kind":"ProjectFile"}}""";

        AppStepFacts.From(StepType.VideoAnalyze, AgentType.VideoTransform, pinned).TakesProjectVideoFile.Should().BeFalse();
        AppStepFacts.From(StepType.VideoAnalyze, AgentType.VideoTransform, open).TakesProjectVideoFile.Should().BeTrue();
        AppStepFacts.From(StepType.VideoAnalyze, AgentType.VideoTransform, null).TakesProjectVideoFile.Should().BeFalse();
    }

    [Fact]
    public void Analyzer_only_workflows_expose_nothing_and_an_explicit_manifest_wins_member_by_member()
    {
        AppStepFacts[] steps = [Agent(AgentType.CodeStructureAnalyzer)];
        AppManifestDeriver.Derive(steps, requiresUserInput: false).Inputs.Should().BeEmpty();

        AppManifest explicitManifest = new(Title: "Mine", Inputs: []);
        AppManifest effective = AppManifestDeriver.Effective(explicitManifest, PromoSteps, true, "Workflow name", "fallback");

        effective.Title.Should().Be("Mine");
        effective.Inputs.Should().BeEmpty();
        effective.Tagline.Should().Be("fallback");
        effective.Category.Should().Be("promo", "the category the author left out is derived");
        AppManifestDeriver.Effective(null, PromoSteps, false, "Workflow name").Title.Should().Be("Workflow name");
    }

    [Theory]
    [InlineData(StepType.VideoGenerate, AppCostClass.UsesCreditsAndAiShots)]
    [InlineData(StepType.Voiceover, AppCostClass.UsesCreditsAndVoice)]
    [InlineData(StepType.EditRoom, AppCostClass.UsesCredits)]
    [InlineData(StepType.VideoCompile, AppCostClass.NoCredits)]
    [InlineData(StepType.Extract, AppCostClass.NoCredits)]
    public void Cost_class_follows_the_step_types(StepType type, string expected)
    {
        AppCostClass.Derive([new AppStepFacts(type)]).Should().Be(expected);
    }

    [Fact]
    public void Cost_class_ranks_generation_over_a_voice_over_credits_and_leaves_deterministic_agents_free()
    {
        AppCostClass.Derive([Agent(AgentType.ScriptwriterAgent), new AppStepFacts(StepType.Voiceover), new AppStepFacts(StepType.VideoGenerate)])
            .Should().Be(AppCostClass.UsesCreditsAndAiShots);
        AppCostClass.Derive([new AppStepFacts(StepType.Agent, AgentType.VideoTransform), new AppStepFacts(StepType.VideoAnalyze)])
            .Should().Be(AppCostClass.NoCredits);
        AppCostClass.Derive([Agent(AgentType.ScriptwriterAgent)]).Should().Be(AppCostClass.UsesCredits);
    }

    [Fact]
    public void Region_effects_use_credits_because_hosted_segmentation_is_billed_but_green_screen_does_not()
    {
        ReelBolt.Shared.Workflows.StepCatalog.GetTemplateFacts().Single(f => f.Key == "video-region-effect").CostClass
            .Should().Be(AppCostClass.UsesCredits);
        ReelBolt.Shared.Workflows.StepCatalog.GetTemplateFacts().Single(f => f.Key == "green-screen-insert").CostClass
            .Should().Be(AppCostClass.NoCredits);
        // A drawn region is tracked locally; a target the vision provider must locate is billed.
        AppStepFacts.AnalyzeCallsProvider("{\"version\":1,\"source\":{\"kind\":\"ProjectFile\"},\"transcription\":\"Off\",\"vision\":\"Off\",\"trackObjects\":[{\"label\":\"x\",\"method\":\"Feature\",\"region\":{\"x0\":0.1,\"y0\":0.1,\"x1\":0.3,\"y1\":0.1,\"x2\":0.1,\"y2\":0.3,\"x3\":0.3,\"y3\":0.3}}]}").Should().BeFalse();
        AppStepFacts.AnalyzeCallsProvider("{\"version\":1,\"source\":{\"kind\":\"ProjectFile\"},\"transcription\":\"Off\",\"vision\":\"Off\",\"trackObjects\":[{\"label\":\"x\",\"method\":\"Feature\"}]}").Should().BeTrue();
    }

    [Fact]
    public void Subtitles_use_credits_because_the_required_transcription_is_billed()
    {
        ReelBolt.Shared.Workflows.StepCatalog.GetTemplateFacts().Single(f => f.Key == "video-subtitles").CostClass
            .Should().Be(AppCostClass.UsesCredits);
        ReelBolt.Shared.Workflows.StepCatalog.GetTemplateFacts().Single(f => f.Key == "video-subtitles-translated").CostClass
            .Should().Be(AppCostClass.UsesCredits);
        AppStepFacts.AnalyzeCallsProvider("{\"version\":1,\"source\":{\"kind\":\"ProjectFile\"},\"transcription\":\"Required\"}").Should().BeTrue();
        AppStepFacts.AnalyzeCallsProvider("{\"version\":1,\"source\":{\"kind\":\"ProjectFile\"},\"transcription\":\"Off\",\"vision\":\"Off\"}").Should().BeFalse();
    }

    // ---- validator ----

    [Fact]
    public void A_good_manifest_is_valid()
    {
        AppManifest manifest = new(
            Title: "Explainer", Tagline: "A short film.", Category: "promo", SuggestedStylePreset: "corporate-explainer",
            Inputs:
            [
                Brief("language", "Language", "\"auto\"", "[\"en\",\"it\",\"es-MX\"]"),
                Brief("lengthSec", "Length", "60", "[30,60,90]"),
                Brief("format", "Shape", choices: "[\"Vertical\",\"16:9\"]"),
                Brief("stylePreset", "Style", "\"cinematic\""),
                new AppManifestInput("notes", Label: "Anything else?")
            ],
            Result: new AppManifestResult("video"));

        AppManifestValidator.Validate(manifest, PromoSteps).Should().BeEmpty();
    }

    [Fact]
    public void A_brief_field_no_step_reads_is_refused_naming_the_input()
    {
        AppManifest manifest = With(Brief("voice", "Narration"));

        IReadOnlyList<string> errors = AppManifestValidator.Validate(manifest, [Agent(AgentType.CodeStructureAnalyzer)]);

        errors.Should().ContainSingle().Which.Should().Contain("\"Narration\"").And.Contain("not read by any step");
    }

    [Fact]
    public void A_files_role_with_no_matching_step_is_refused()
    {
        AppManifest manifest = With(new AppManifestInput("files", Role: "video", Label: "Your clips", Min: 1, Max: 20));

        AppManifestValidator.Validate(manifest, PromoSteps).Should().ContainSingle().Which.Should().Contain("\"Your clips\"");
        AppManifestValidator.Validate(manifest, [VideoAnalyzeOpenSlot()]).Should().BeEmpty();
    }

    [Fact]
    public void More_than_eight_inputs_are_refused()
    {
        AppManifestInput[] nine = Enumerable.Range(0, 9).Select(i => new AppManifestInput("notes", Label: "n" + i)).ToArray();

        AppManifestValidator.Validate(With(nine), PromoSteps).Should().Contain(e => e.Contains("at most 8"));
    }

    [Fact]
    public void Duplicate_fields_and_unknown_kinds_and_fields_are_refused()
    {
        AppManifestValidator.Validate(With(Brief("language"), Brief("language")), PromoSteps).Should().Contain(e => e.Contains("exposed twice"));
        AppManifestValidator.Validate(With(new AppManifestInput("widget")), PromoSteps).Should().Contain(e => e.Contains("unknown kind"));
        AppManifestValidator.Validate(With(Brief("styleId")), PromoSteps).Should().Contain(e => e.Contains("does not exist"));
        AppManifestValidator.Validate(With(new AppManifestInput("step", Label: "Takes")), PromoSteps).Should().Contain(e => e.Contains("do not support"));
    }

    [Theory]
    [InlineData("lengthSec", "[4]")]
    [InlineData("lengthSec", "[601]")]
    [InlineData("lengthSec", "[\"60\"]")]
    [InlineData("language", "[\"not a tag\"]")]
    [InlineData("format", "[\"Diagonal\"]")]
    [InlineData("stylePreset", "[\"no-such-preset\"]")]
    [InlineData("designSystem", "[\"maybe\"]")]
    public void Choices_must_be_valid_values_for_the_field(string field, string choices)
    {
        AppManifestValidator.Validate(With(Brief(field, "Pick", choices: choices)), PromoSteps)
            .Should().Contain(e => e.Contains("not a valid"));
    }

    [Fact]
    public void An_invalid_default_and_too_many_choices_are_refused()
    {
        AppManifestValidator.Validate(With(Brief("lengthSec", "Length", def: "9999")), PromoSteps).Should().Contain(e => e.Contains("default"));
        string many = "[" + string.Join(",", Enumerable.Range(5, 13)) + "]";
        AppManifestValidator.Validate(With(Brief("lengthSec", "Length", choices: many)), PromoSteps).Should().Contain(e => e.Contains("between 1 and"));
    }

    [Fact]
    public void An_unknown_suggested_style_a_bad_category_and_a_bad_result_are_refused()
    {
        AppManifest manifest = new(SuggestedStylePreset: "nope", Category: "weird", Result: new AppManifestResult("hologram"));

        IReadOnlyList<string> errors = AppManifestValidator.Validate(manifest, PromoSteps);

        errors.Should().Contain(e => e.Contains("suggested style"));
        errors.Should().Contain(e => e.Contains("category"));
        errors.Should().Contain(e => e.Contains("result kind"));
    }

    [Fact]
    public void Text_is_capped_and_markup_control_characters_and_links_are_refused()
    {
        AppManifest manifest = new(
            Title: new string('t', 61),
            Tagline: "see https://evil.example now",
            Inputs: [new AppManifestInput("notes", Label: new string('x', 41))]);
        IReadOnlyList<string> errors = AppManifestValidator.Validate(manifest, PromoSteps);

        errors.Should().Contain(e => e.Contains("title is longer than 60"));
        errors.Should().Contain(e => e.Contains("tagline contains a link"));
        errors.Should().Contain(e => e.Contains("longer than 40"));

        AppManifestValidator.Validate(new AppManifest(Title: "<b>hi</b>"), PromoSteps).Should().Contain(e => e.Contains("markup"));
        AppManifestValidator.Validate(new AppManifest(Title: "a\u0007b"), PromoSteps).Should().Contain(e => e.Contains("control characters"));
    }

    [Fact]
    public void Sanitize_strips_control_characters_and_markup_and_collapses_whitespace()
    {
        AppManifest clean = AppManifestValidator.Sanitize(new AppManifest(
            Title: "  <b>Big</b>\u0007  film\n ", Tagline: "`code`", Inputs: [new AppManifestInput("notes", Label: "a\tb<script>")]));

        clean.Title.Should().Be("bBig/b film");
        clean.Tagline.Should().Be("code");
        clean.Inputs!.Single().Label.Should().Be("a bscript");
        AppManifestValidator.Validate(clean, PromoSteps).Should().BeEmpty();
    }

    [Fact]
    public void Wrong_version_is_refused()
    {
        AppManifestValidator.Validate(new AppManifest(Version: 3), PromoSteps).Should().Contain(e => e.Contains("version"));
    }

    // ---- orphans (WL018) ----

    [Fact]
    public void FindOrphans_reports_each_exposed_input_the_steps_no_longer_honour()
    {
        AppManifest manifest = With(Brief("voice", "Narration"), Brief("language", "Language"),
            new AppManifestInput("files", Role: "video", Label: "Your clips"));
        AppStepFacts[] steps = [Agent(AgentType.ScriptwriterAgent)];

        IReadOnlyList<AppManifestOrphan> orphans = AppManifestValidator.FindOrphans(manifest, steps);

        orphans.Select(o => o.InputLabel).Should().Equal("Your clips");
        orphans.Should().OnlyContain(o => o.Code == "WL018");

        AppManifestValidator.FindOrphans(manifest, [Agent(AgentType.CodeStructureAnalyzer)]).Select(o => o.InputLabel)
            .Should().Equal("Narration", "Language", "Your clips");
        AppManifestValidator.FindOrphans(null, steps).Should().BeEmpty();
    }

    [Fact]
    public void Json_roundtrips_and_refuses_unknown_members()
    {
        AppManifest manifest = WorkflowTemplateAppManifests.ByKey["video-derush-edit"];

        AppManifestJson.TryParse(AppManifestJson.Serialize(manifest))!.Inputs!.Count.Should().Be(manifest.Inputs!.Count);
        AppManifestJson.TryParse("{\"version\":1,\"bogus\":true}").Should().BeNull();
        AppManifestJson.TryParse("not json").Should().BeNull();
        AppManifestJson.Serialize(new AppManifest(Title: "x")).Should().Contain("\"title\":\"x\"").And.NotContain("tagline");
    }

    // ---- hardening (security review) ----

    [Fact]
    public void Members_a_kind_does_not_use_are_refused_on_every_kind()
    {
        AppManifestValidator.Validate(With(new AppManifestInput("notes", Label: "n", Default: J("\"<b>x</b> http://e.com\""))), PromoSteps)
            .Should().Contain(e => e.Contains("cannot have a field, default or choices"));
        AppManifestValidator.Validate(With(new AppManifestInput("notes", Label: "n", Choices: [J("{\"a\":1}")])), PromoSteps)
            .Should().Contain(e => e.Contains("cannot have a field, default or choices"));
        AppManifestValidator.Validate(With(new AppManifestInput("notes", Label: "n", Field: "anything")), PromoSteps)
            .Should().Contain(e => e.Contains("cannot have a field"));
        AppManifestValidator.Validate(With(new AppManifestInput("notes", Label: "n", Role: "video", Min: 1, Max: 2)), PromoSteps)
            .Should().Contain(e => e.Contains("cannot have a role or file limits"));
        AppManifestValidator.Validate(With(new AppManifestInput("files", Role: "video", Label: "c", Default: J("1"))), [VideoAnalyzeOpenSlot()])
            .Should().Contain(e => e.Contains("cannot have a field, default or choices"));
        AppManifestValidator.Validate(With(new AppManifestInput("brief", Field: "language", Label: "l", Role: "video", Min: 1)), PromoSteps)
            .Should().Contain(e => e.Contains("cannot have a role or file limits"));
    }

    [Theory]
    [InlineData("a\u202Eb")]
    [InlineData("a\u200Bb")]
    [InlineData("a\u2066b")]
    [InlineData("a\u2028b")]
    [InlineData("a\U000E0041b")]
    [InlineData("a\U000E007Fb")]
    public void Unicode_format_and_separator_characters_are_refused_and_stripped(string title)
    {
        AppManifestValidator.Validate(new AppManifest(Title: title), PromoSteps).Should().Contain(e => e.Contains("control characters"));
        AppManifestValidator.Sanitize(new AppManifest(Title: title)).Title.Should().Be("ab");
    }

    [Theory]
    [InlineData("go to evil.com now")]
    [InlineData("evil.example/login")]
    [InlineData("see Evil.IO")]
    public void Bare_domains_are_links(string tagline)
    {
        AppManifestValidator.Validate(new AppManifest(Tagline: tagline), PromoSteps).Should().Contain(e => e.Contains("link"));
    }

    [Fact]
    public void Messages_clip_an_unlabelled_inputs_kind_field_and_role()
    {
        string huge = new('k', 500);
        IReadOnlyList<string> errors = AppManifestValidator.Validate(With(new AppManifestInput(huge)), PromoSteps);

        errors.Should().NotBeEmpty();
        errors.Should().OnlyContain(e => e.Length < 200);
    }

    [Fact]
    public void The_title_fallback_is_sanitized_and_capped()
    {
        string name = "<b>" + new string('n', 100) + "</b>\u202E";

        string? title = AppManifestDeriver.Effective(null, PromoSteps, false, name).Title;

        title!.Length.Should().BeLessThanOrEqualTo(AppManifestVocabulary.MaxTitle);
        title.Should().NotContain("<").And.NotContain("\u202E");
    }

    [Fact]
    public void The_design_system_gate_and_compile_brand_overrides_read_designSystem()
    {
        BriefFieldConsumers.FieldsReadBy(new AppStepFacts(StepType.DesignSystem)).Should().Contain("designSystem");
        BriefFieldConsumers.FieldsReadBy(new AppStepFacts(StepType.VideoCompile)).Should().Contain("designSystem");
        AppManifestValidator.Validate(With(Brief("designSystem", "Design")), [new AppStepFacts(StepType.DesignSystem)]).Should().BeEmpty();
    }

    [Theory]
    [InlineData(AgentType.NarrationWriter, "voice")]
    [InlineData(AgentType.VideoStoryEditor, "tone")]
    [InlineData(AgentType.RemotionComponentTranslator, "designSystem")]
    [InlineData(AgentType.CodeStructureAnalyzer, null)]
    public void Agent_rows_of_the_matrix(AgentType agent, string? field)
    {
        IReadOnlySet<string> read = BriefFieldConsumers.FieldsReadBy(Agent(agent));
        if (field is null)
            read.Should().BeEmpty();
        else
            read.Should().Contain(field);
    }

    [Theory]
    [InlineData(StepType.EditRoom, "lengthSec", true)]
    [InlineData(StepType.ReviewLoop, "language", true)]
    [InlineData(StepType.Voiceover, "voice", true)]
    [InlineData(StepType.Voiceover, "format", false)]
    [InlineData(StepType.VideoGenerate, "format", true)]
    [InlineData(StepType.VideoAnalyze, "sourceLanguage", true)]
    [InlineData(StepType.Extract, "language", false)]
    public void Step_type_rows_of_the_matrix(StepType type, string field, bool reads)
    {
        BriefFieldConsumers.FieldsReadBy(new AppStepFacts(type)).Contains(field).Should().Be(reads);
    }

    [Fact]
    public void Every_curated_file_slot_states_a_max_within_the_cap_and_the_green_screen_promises_no_second_file()
    {
        foreach ((string key, AppManifest manifest) in WorkflowTemplateAppManifests.ByKey)
        {
            foreach (AppManifestInput input in manifest.Inputs!.Where(i => i.Kind == AppManifestVocabulary.KindFiles))
                input.Max.Should().BeInRange(1, AppManifestVocabulary.MaxFilesPerSlot, key);
        }

        AppManifest green = WorkflowTemplateAppManifests.ByKey[WorkflowTemplateCatalog.GreenScreenInsertTemplateKey];
        green.Inputs.Where(i => i.Kind == AppManifestVocabulary.KindFiles).Select(i => i.Role).Should().Equal("video", "insertContent");
        green.Tagline.Should().NotContain("Places a recording").And.NotContain("workflow editor");
        WorkflowTemplateCatalog.GetAll().Single(t => t.Key == WorkflowTemplateCatalog.GreenScreenInsertTemplateKey).RequiresUserInput.Should().BeFalse();
        green.Inputs.Should().NotContain(i => i.Kind == AppManifestVocabulary.KindNotes);
    }

    [Fact]
    public void A_green_screen_manifest_stored_with_one_slot_gets_the_content_slot_at_read_time_and_nothing_else_changes()
    {
        AppManifest stored = new(Title: "My edit", Inputs:
        [
            new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "video", Label: "Mine", Min: 1, Max: 1),
            new AppManifestInput(AppManifestVocabulary.KindNotes, Label: "Notes")
        ]);

        AppManifest upgraded = AppManifestDeriver.UpgradeStored(stored, WorkflowTemplateCatalog.GreenScreenInsertTemplateKey)!;

        upgraded.Title.Should().Be("My edit");
        upgraded.Inputs!.Select(i => i.Role ?? i.Kind).Should().Equal("video", "insertContent", "notes");
        AppManifestDeriver.UpgradeStored(upgraded, WorkflowTemplateCatalog.GreenScreenInsertTemplateKey).Should().BeSameAs(upgraded);
        AppManifestDeriver.UpgradeStored(stored, "video-derush-edit").Should().BeSameAs(stored);
    }

    [Theory]
    [InlineData(WorkflowTemplateCatalog.SpokespersonClipTemplateKey)]
    [InlineData(WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey)]
    public void The_avatar_templates_offer_an_avatar_input_on_the_run_page(string key)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(key)!;
        IReadOnlyList<AppStepFacts> facts = AppStepFacts.FromTemplate(template);
        facts.Should().Contain(f => f.StepType == StepType.VideoGenerate && f.TakesBriefAvatar);

        AppManifest effective = AppManifestDeriver.Effective(template.AppManifest, facts, template.RequiresUserInput, template.Name);
        AppManifestInput avatar = effective.Inputs!.Single(i => i.Field == BriefFieldConsumers.Avatar);
        avatar.Kind.Should().Be(AppManifestVocabulary.KindBrief);
        avatar.Label.Should().Be("Avatar");
        avatar.Default.Should().BeNull();
        avatar.Choices.Should().BeNull();
        AppManifestValidator.Validate(effective, facts).Should().BeEmpty();
    }

    [Fact]
    public void Only_an_inline_generate_step_that_names_no_avatar_or_frame_takes_the_brief_avatar()
    {
        AppStepFacts.AcceptsBriefAvatar("""{"planSource":"Inline","inlinePrompt":"x"}""").Should().BeTrue();
        AppStepFacts.AcceptsBriefAvatar("""{"planSource":"StepRef","plan":{"from":"Previous"}}""").Should().BeFalse();
        AppStepFacts.AcceptsBriefAvatar("""{"planSource":"Inline","avatarId":"11111111-1111-1111-1111-111111111111"}""").Should().BeFalse();
        AppStepFacts.AcceptsBriefAvatar("""{"planSource":"Inline","firstFrame":{"from":"Previous"}}""").Should().BeFalse();
        AppStepFacts.AcceptsBriefAvatar(null).Should().BeFalse();
        AppStepFacts.AcceptsBriefAvatar("not json").Should().BeFalse();
        AppStepFacts.From(StepType.VideoAnalyze, null, null, """{"planSource":"Inline"}""").TakesBriefAvatar.Should().BeFalse();

        AppStepFacts plan = AppStepFacts.From(StepType.VideoGenerate, null, null, """{"planSource":"StepRef"}""");
        AppManifestDeriver.Derive([plan], requiresUserInput: false).Inputs!
            .Should().NotContain(i => i.Field == BriefFieldConsumers.Avatar, "a b-roll step ignores the brief's avatar");
    }

    [Fact]
    public void An_avatar_input_takes_no_default_or_choices_and_is_an_orphan_without_a_generate_step()
    {
        AppManifestVocabulary.BriefFields.Should().Contain(BriefFieldConsumers.Avatar);
        AppStepFacts generate = new(StepType.VideoGenerate, TakesBriefAvatar: true);

        AppManifestValidator.Validate(With(Brief("avatar", "Avatar")), [generate]).Should().BeEmpty();
        AppManifestValidator.Validate(With(Brief("avatar", "Avatar", def: "\"11111111-1111-1111-1111-111111111111\"")), [generate])
            .Should().ContainSingle(e => e.Contains("default"));
        AppManifestValidator.Validate(With(Brief("avatar", "Avatar", choices: "[\"a\"]")), [generate])
            .Should().ContainSingle(e => e.Contains("choice"));
        AppManifestValidator.FindOrphans(With(Brief("avatar", "Avatar")), PromoSteps).Should().ContainSingle();
    }
}
