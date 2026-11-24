using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.ChangeRequests;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Follow-up F1 (docs/design/app-inputs.md 27): mapped bindings (one switch sets several settings, a value per position),
/// nested whole values (<c>regionEffects[0].effect</c>), "speak exactly this" (<c>@spokenText</c>) and the talking head's
/// still model (<c>@stillModel</c>), through the analysis, the validators, the binder, the recipes, the change planner and
/// the still model's reference, job and resolution.
/// </summary>
public class AppInputFollowUpF1Tests
{
    private const string Compile = """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"programFadeOutMs":800,"programAudioFadeOutMs":900,"regionEffects":[{"target":"the plate","effect":"Blur"}]}""";

    private static IReadOnlyList<AppBindingStep> CompileSteps(string appInputs) =>
    [
        Step(1, StepType.VideoAnalyze, """{"version":1,"source":{"kind":"ProjectFile"},"appInputs":{"transcription":{"input":"captions","values":{"true":"Required"}}}}"""),
        Step(2, StepType.VideoCompile, Compile.TrimEnd('}') + ",\"appInputs\":" + appInputs + "}"),
    ];

    private static JsonObject Config(AppInputBindResult bound, int index) => JsonNode.Parse(bound.Steps[index].Step.ConfigJson!)!.AsObject();

    // ------------------------------------------------------------------ mapped bindings

    [Fact]
    public void One_switch_sets_several_settings_on_two_steps_and_off_leaves_the_authored_values()
    {
        AppManifest manifest = Manifest(Input("captions", "toggle", "Captions", def: "false"));
        IReadOnlyList<AppBindingStep> steps = CompileSteps("""{"enableCaptions":"captions","captionSource":{"input":"captions","values":{"true":"Transcript"}}}""");
        AppBindingAnalysis.Analyze(manifest, steps).Problems.Should().BeEmpty();
        AppManifestValidator.Validate(manifest, [], steps).Should().BeEmpty();

        AppInputBindResult on = AppInputBinder.Bind(manifest, steps, Values(("captions", "true")));
        on.Problems.Should().BeEmpty();
        Config(on, 0)["transcription"]!.GetValue<string>().Should().Be("Required");
        Config(on, 1)["enableCaptions"]!.GetValue<bool>().Should().BeTrue();
        Config(on, 1)["captionSource"]!.GetValue<string>().Should().Be("Transcript");
        JsonSerializer.Deserialize<VideoCompileStepConfig>(on.Steps[1].Step.ConfigJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!.Validate().Should().BeEmpty();

        AppInputBindResult off = AppInputBinder.Bind(manifest, steps, Values(("captions", "false")));
        off.Problems.Should().BeEmpty();
        Config(off, 0).ContainsKey("transcription").Should().BeFalse("a value not listed keeps the authored setting");
        Config(off, 1)["enableCaptions"]!.GetValue<bool>().Should().BeFalse();
        Config(off, 1).ContainsKey("captionSource").Should().BeFalse();
    }

    [Fact]
    public void A_switch_writes_the_value_listed_for_each_position()
    {
        AppManifest manifest = Manifest(Input("ending", "toggle", "Ending fade", def: "true"));
        IReadOnlyList<AppBindingStep> steps = CompileSteps(
            """{"programFadeOutMs":{"input":"ending","values":{"true":800,"false":0}},"programAudioFadeOutMs":{"input":"ending","values":{"true":900,"false":0}}}""");
        AppManifestValidator.Validate(manifest with { Inputs = [.. manifest.Inputs!, Input("captions", "toggle", "C")] }, [], steps).Should().BeEmpty();

        JsonObject on = Config(AppInputBinder.Bind(manifest, steps, Values(("ending", "true"))), 1);
        on["programFadeOutMs"]!.GetValue<long>().Should().Be(800, "on reproduces what the recipe ships exactly");
        on["programAudioFadeOutMs"]!.GetValue<long>().Should().Be(900);
        JsonObject off = Config(AppInputBinder.Bind(manifest, steps, Values(("ending", "false"))), 1);
        off["programFadeOutMs"]!.GetValue<long>().Should().Be(0);
        off["programAudioFadeOutMs"]!.GetValue<long>().Should().Be(0);
    }

    [Fact]
    public void A_choice_lists_a_value_per_option_and_an_unlisted_option_keeps_the_authored_one()
    {
        AppManifest manifest = Manifest(Input("tempo", "choice", "Pace", options: [Option("\"calm\"", "Calm"), Option("\"quick\"", "Quick"), Option("\"keep\"", "As is")]));
        IReadOnlyList<AppBindingStep> steps = CompileSteps("""{"programFadeOutMs":{"input":"tempo","values":{"calm":2000,"quick":300}}}""");
        AppManifestValidator.Validate(manifest with { Inputs = [.. manifest.Inputs!, Input("captions", "toggle", "C")] }, [], steps).Should().BeEmpty();

        Config(AppInputBinder.Bind(manifest, steps, Values(("tempo", "\"calm\""))), 1)["programFadeOutMs"]!.GetValue<long>().Should().Be(2000);
        Config(AppInputBinder.Bind(manifest, steps, Values(("tempo", "\"keep\""))), 1)["programFadeOutMs"]!.GetValue<int>().Should().Be(800);
    }

    [Fact]
    public void A_mapped_binding_is_refused_when_it_cannot_be_honoured()
    {
        // A listed value the setting cannot take: refused at save, naming the setting.
        AppBindingAnalysis.Analyze(null, CompileSteps("""{"programFadeOutMs":{"input":"ending","values":{"true":99999}}}"""))
            .Problems.Should().ContainSingle(p => p.Message.Contains("lists a value the setting cannot take", StringComparison.Ordinal));
        AppBindingAnalysis.Analyze(null, CompileSteps("""{"enableCaptions":{"input":"captions","values":{"true":"yes"}}}"""))
            .Problems.Should().ContainSingle(p => p.Message.Contains("takes true or false", StringComparison.Ordinal));
        // A malformed entry.
        AppBindingAnalysis.Analyze(null, CompileSteps("""{"enableCaptions":{"input":"captions"}}"""))
            .Problems.Should().ContainSingle(p => p.Message.Contains("\"values\"", StringComparison.Ordinal));
        AppBindingAnalysis.Analyze(null, CompileSteps("""{"enableCaptions":{"input":"captions","values":{"true":true},"extra":1}}"""))
            .Problems.Should().ContainSingle(p => p.Message.Contains("unknown member", StringComparison.Ordinal));

        // The input side: only a switch or a choice lists values, and only values it can take.
        IReadOnlyList<AppBindingStep> steps = CompileSteps("""{"programFadeOutMs":{"input":"ending","values":{"yes":800}}}""");
        AppManifest toggle = Manifest(Input("ending", "toggle", "Ending"), Input("captions", "toggle", "C"));
        AppManifestValidator.Validate(toggle, [], steps).Should().Contain(e => e.Contains("a switch is \"true\" or \"false\"", StringComparison.Ordinal));
        AppManifest text = Manifest(Input("ending", "shortText", "Ending"), Input("captions", "toggle", "C"));
        AppManifestValidator.Validate(text, [], steps).Should().Contain(e => e.Contains("only a switch or a choice can list", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mapped_binding_is_part_of_the_consumer_map_the_change_planner_reads()
    {
        AppManifest manifest = Manifest(Input("captions", "toggle", "Captions", def: "false"));
        IReadOnlyList<AppBindingStep> steps = CompileSteps("""{"enableCaptions":"captions","captionSource":{"input":"captions","values":{"true":"Transcript"}}}""");
        AppBindingAnalysis.Analyze(manifest, steps).ConsumingSteps("captions", steps).Should().BeEquivalentTo([1, 2]);

        AppInputChangeDecision d = AppInputChangePlanner.Decide(manifest, steps, Values(("captions", "false")), Values(("captions", "true")));
        d.Refused.Should().BeFalse();
        d.Seeds.Should().BeEquivalentTo([1, 2]);
    }

    // ------------------------------------------------------------------ nested whole values

    [Fact]
    public void A_choice_sets_one_member_of_an_item_and_the_item_must_exist()
    {
        AppManifest manifest = Manifest(Input("hideWith", "choice", "How to hide it", def: "\"Blur\"",
            options: [Option("\"Blur\"", "Blur"), Option("\"Pixelate\"", "Pixelate")]), Input("captions", "toggle", "C"));
        IReadOnlyList<AppBindingStep> steps = CompileSteps("""{"regionEffects[0].effect":"hideWith"}""");
        AppBindingMap map = AppBindingAnalysis.Analyze(manifest, steps);
        map.Problems.Should().BeEmpty();
        map.UsesOf("hideWith").Should().ContainSingle().Which.Where.Should().Be("regionEffects[0].effect");
        AppManifestValidator.Validate(manifest, [], steps).Should().BeEmpty();

        JsonObject bound = Config(AppInputBinder.Bind(manifest, steps, Values(("hideWith", "\"Pixelate\""))), 1);
        bound["regionEffects"]![0]!["effect"]!.GetValue<string>().Should().Be("Pixelate");
        bound["regionEffects"]![0]!["target"]!.GetValue<string>().Should().Be("the plate");

        AppBindingAnalysis.Analyze(manifest, CompileSteps("""{"regionEffects[3].effect":"hideWith"}""")).Problems
            .Should().ContainSingle(p => p.Message.Contains("not in the step's settings", StringComparison.Ordinal));
        AppBindingAnalysis.Analyze(manifest, CompileSteps("""{"regionEffects[].effect":"hideWith"}""")).Problems
            .Should().ContainSingle(p => p.Message.Contains("must name the item", StringComparison.Ordinal));
        AppManifest wrongOption = manifest with { Inputs = [Input("hideWith", "choice", "H", options: [Option("\"Melt\"", "Melt")]), Input("captions", "toggle", "C")] };
        AppManifestValidator.Validate(wrongOption, [], steps).Should().Contain(e => e.Contains("\"Melt\"", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ speak exactly this

    private const string InlineVoice = """{"source":{"kind":"Inline","lines":[{"text":"Hello!","startSec":0}]},"appInputs":{"@spokenText":"words"}}""";

    [Fact]
    public void The_words_become_one_spoken_line_per_paragraph_from_zero_in_order()
    {
        AppManifest manifest = Manifest(Input("words", "longText", "What they say", required: true));
        IReadOnlyList<AppBindingStep> steps = [Step(1, StepType.Voiceover, InlineVoice)];
        AppManifestValidator.Validate(manifest, [], steps).Should().BeEmpty();

        AppInputCheckResult check = AppInputValueChecker.Check(manifest, steps, Values(("words", "\"One.\\n\\n  Two, still\\nthe second line.\\n\\n\\nThree.\"")));
        check.Problems.Should().BeEmpty();
        AppInputBindResult bound = AppInputBinder.Bind(manifest, steps, check.Values);
        bound.Problems.Should().BeEmpty();
        VoiceoverStepConfig config = VoiceoverStepConfig.Parse(bound.Steps[0].Step.ConfigJson!)!;
        config.Source.Sequential.Should().BeTrue();
        config.Source.Lines!.Select(l => l.Text).Should().Equal("One.", "Two, still the second line.", "Three.");
        config.Source.Lines!.Should().OnlyContain(l => l.StartSec == 0);
        bound.Steps[0].Consumed.Should().ContainKey("words");
    }

    [Fact]
    public void Too_many_paragraphs_are_refused_naming_the_field_and_a_step_that_reads_a_writer_cannot_take_the_words()
    {
        AppManifest manifest = Manifest(Input("words", "longText", "What they say"));
        IReadOnlyList<AppBindingStep> steps = [Step(1, StepType.Voiceover, InlineVoice)];
        string many = JsonSerializer.Serialize(string.Join("\n\n", Enumerable.Range(1, VoiceoverSpokenText.MaxLines + 1).Select(i => $"Line {i}.")));
        AppInputValueProblem problem = AppInputValueChecker.Check(manifest, steps, Values(("words", many))).Problems.Should().ContainSingle().Subject;
        problem.Code.Should().Be(AppInputValueProblem.Invalid);
        problem.InputId.Should().Be("words");
        problem.Message.Should().StartWith("“What they say” has 41 paragraphs");

        IReadOnlyList<AppBindingStep> fromScript = [Step(1, StepType.Voiceover, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":1},"appInputs":{"@spokenText":"words"}}""")];
        AppBindingAnalysis.Analyze(manifest, fromScript).Problems.Should().ContainSingle(p => p.Message.Contains("source must be Inline", StringComparison.Ordinal));
        AppManifestValidator.Validate(Manifest(Input("words", "number", "N")), [], steps).Should().Contain(e => e.Contains("takes shortText or longText", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_optional_text_keeps_the_authored_lines()
    {
        AppManifest manifest = Manifest(Input("words", "longText", "What they say"));
        IReadOnlyList<AppBindingStep> steps = [Step(1, StepType.Voiceover, InlineVoice)];
        AppInputBindResult bound = AppInputBinder.Bind(manifest, steps, Values());
        VoiceoverStepConfig.Parse(bound.Steps[0].Step.ConfigJson!)!.Source.Lines!.Single().Text.Should().Be("Hello!");
    }

    // ------------------------------------------------------------------ the still model

    private const string TalkingHead = """{"planSource":"Inline","inlinePrompt":"a studio","durationSeconds":15,"ratio":"9:16","takes":1,"maxClips":1,"maxSpendCredits":250,"avatarAudio":{"from":"Step","stepOrder":1},"appInputs":{"@stillModel":"picture"}}""";

    [Fact]
    public void The_still_model_binds_to_still_provider_id_through_the_image_edit_rules()
    {
        Guid nano = Guid.NewGuid();
        Guid single = Guid.NewGuid();
        AppModelCandidate[] candidates =
        [
            new(nano, "ImageEdit", nameof(InferenceProviderKind.FalAi), FalImageCatalog.Instance.DefaultFor(FalImageJob.Instruct).Id, "Default edit", true),
            new(single, "ImageEdit", nameof(InferenceProviderKind.OpenAICompatible), "gpt-image", "Other edit", true),
        ];
        AppManifest manifest = Manifest(Input("picture", "model", "Picture model", capability: "ImageEdit"));
        IReadOnlyList<AppBindingStep> steps = [Step(1, StepType.Voiceover, InlineVoice.Replace("@spokenText\":\"words", "@model\":\"none")), Step(2, StepType.VideoGenerate, TalkingHead)];
        AppModelRules.JobFor(steps[1]).Should().Be(AppModelRules.TalkingHead, "driving speech makes a talking head even when the brief names the avatar");
        AppModelRules.JobFor(steps[1], AppBindableCatalog.StillModelKey).Should().Be(AppModelRules.AvatarStill);
        AppModelRules.CapabilityFor(steps[1], AppBindableCatalog.StillModelKey).Should().Be("ImageEdit");

        AppModelResolver resolver = AppModelRules.Resolver(manifest, candidates);
        AppInputBindResult bound = AppInputBinder.Bind(manifest, [steps[1]], Values(("picture", $"\"{nano}\"")), resolveModel: resolver);
        bound.Problems.Should().BeEmpty();
        JsonObject config = Config(bound, 0);
        config["stillProviderId"]!.GetValue<string>().Should().Be(nano.ToString("D"));
        config.ContainsKey("providerId").Should().BeFalse("the still model never replaces the video model");

        AppInputBinder.Bind(manifest, [steps[1]], Values(("picture", $"\"{single}\"")), resolveModel: resolver).Problems
            .Should().ContainSingle("a model that cannot make a still from several pictures is refused");
        AppModelRules.RefusalCode(manifest.Inputs![0], single, "ImageEdit", AppModelRules.AvatarStill, candidates, out _).Should().Be(AppModelRules.WrongJob);
    }

    [Fact]
    public void A_picture_model_input_binds_a_video_analysis_to_its_vision_model_and_nothing_else()
    {
        Guid vision = Guid.NewGuid();
        Guid chat = Guid.NewGuid();
        AppModelCandidate[] candidates =
        [
            new(vision, "Vision", nameof(InferenceProviderKind.OpenAICompatible), "small-vision", "Small vision", true),
            new(chat, "Chat", nameof(InferenceProviderKind.OpenAICompatible), "chat", "Chat", true),
        ];
        AppManifest manifest = Manifest(
            Input("lookModel", "model", "Model that looks at the picture", capability: "Vision"),
            Input("mostLooks", "number", "Most looks at the picture", min: 1, max: 40),
            Input("footageSounds", "toggle", "Sounds on filmed footage too", def: "false"));
        const string analyze = """{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","vision":"Off","analyzeEvents":true,"offerSfxClips":true,"describeEvents":true,"appInputs":{"@visionModel":"lookModel","maxDescribedEvents":"mostLooks","sfxOnFootage":"footageSounds"}}""";
        IReadOnlyList<AppBindingStep> steps = [Step(1, StepType.VideoAnalyze, analyze)];

        AppBindableCatalog.Find(StepType.VideoAnalyze, AppBindableCatalog.VisionModelKey)!.Capabilities.Should().Equal("Vision");
        AppModelRules.CapabilityFor(steps[0], AppBindableCatalog.VisionModelKey).Should().Be("Vision");
        AppBindingAnalysis.Analyze(manifest, steps).Problems.Should().BeEmpty();
        AppManifestValidator.Validate(manifest, [], steps).Should().BeEmpty();

        AppModelResolver resolver = AppModelRules.Resolver(manifest, candidates);
        AppInputBindResult bound = AppInputBinder.Bind(
            manifest, steps, Values(("lookModel", $"\"{vision}\""), ("mostLooks", "9"), ("footageSounds", "true")), resolveModel: resolver);
        bound.Problems.Should().BeEmpty();
        JsonObject config = Config(bound, 0);
        config["visionProviderId"]!.GetValue<string>().Should().Be(vision.ToString("D"));
        config.ContainsKey("providerId").Should().BeFalse("a video analysis has no model of its own to overwrite");
        config.ContainsKey("model").Should().BeFalse();
        config["maxDescribedEvents"]!.GetValue<int>().Should().Be(9);
        config["sfxOnFootage"]!.GetValue<bool>().Should().BeTrue();

        // left alone: the number stays unset (the video's length decides) and the switch keeps its default
        JsonObject untouched = Config(AppInputBinder.Bind(manifest, steps, Values(("lookModel", $"\"{vision}\"")), resolveModel: resolver), 0);
        untouched.ContainsKey("maxDescribedEvents").Should().BeFalse();

        AppInputBinder.Bind(manifest, steps, Values(("lookModel", $"\"{chat}\"")), resolveModel: resolver).Problems
            .Should().ContainSingle("a chat model is not a picture model");
    }

    [Fact]
    public void The_still_model_is_only_for_a_talking_head_and_only_an_image_edit_model()
    {
        AppManifest manifest = Manifest(Input("picture", "model", "Picture model", capability: "ImageEdit"));
        string plain = """{"planSource":"Inline","inlinePrompt":"x","appInputs":{"@stillModel":"picture"}}""";
        AppBindingAnalysis.Analyze(manifest, [Step(1, StepType.VideoGenerate, plain)]).Problems
            .Should().ContainSingle(p => p.Message.Contains("talking head", StringComparison.Ordinal));
        AppManifestValidator.Validate(Manifest(Input("picture", "model", "P", capability: "VideoGeneration")), [], [Step(1, StepType.VideoGenerate, TalkingHead)])
            .Should().Contain(e => e.Contains("needs ImageEdit", StringComparison.Ordinal));
    }

    [Fact]
    public void A_still_model_is_a_provider_reference_of_the_step_and_its_config_checks_it()
    {
        Guid still = Guid.NewGuid();
        string json = TalkingHead.Replace("\"appInputs\":{\"@stillModel\":\"picture\"}", $"\"stillProviderId\":\"{still}\"");
        StepProviderReferences.Extract(StepType.VideoGenerate, null, null, null, json, null, null, null, null, null, null)
            .Should().ContainSingle(r => r.Capability == InferenceProviderCapability.ImageEdit && r.ProviderId == still && r.Field == "stillProviderId");

        VideoGenerateStepConfig config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(json, VideoGenerateStepConfig.JsonOptions)!;
        config.Validate().Should().NotContain(e => e.Contains("StillProviderId", StringComparison.Ordinal));
        config.AvatarAudio = null;
        config.Validate().Should().Contain(e => e.Contains("StillProviderId applies only to a talking-head step", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ encode-once and the recipes

    [Fact]
    public void Captions_may_ride_the_final_encode_of_an_encode_once_edit_but_never_the_plan()
    {
        VideoCompileStepConfig Final(CompileEncodeStage stage) => new(
            Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), EnableCaptions: true, CaptionSource: CaptionSource.Transcript, AnalysisStepOrder: 1, EncodeStage: stage);
        Final(CompileEncodeStage.Final).Validate().Should().NotContain(e => e.Contains("captions", StringComparison.Ordinal));
        Final(CompileEncodeStage.Plan).Validate().Should().Contain(e => e.Contains("captions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("video-derush-edit")]
    [InlineData("video-derush-edit-graphics")]
    [InlineData(WorkflowTemplateCatalog.TestimonialReelTemplateKey)]
    public void The_edit_recipes_caption_the_footage_only_when_asked_and_keep_their_ending_fade(string key)
    {
        WorkflowTemplateDefinition t = WorkflowTemplateCatalog.GetAll().Single(x => x.Key == key);
        AppManifest manifest = t.AppManifest!;
        IReadOnlyList<AppBindingStep> steps = AppBindingStep.FromTemplate(t);
        manifest.Inputs!.Single(i => i.Id == "footageCaptions").Default!.Value.ValueKind.Should().Be(JsonValueKind.False);
        AppInputPlacement.IsUnderMore(manifest.Inputs!.Single(i => i.Id == "ending")).Should().BeTrue();

        AppInputBindResult defaults = AppInputBinder.Bind(manifest, steps, AppInputValueChecker.Check(manifest, steps, null).Values);
        defaults.Problems.Should().BeEmpty();
        WorkflowTemplateDefinition before = WorkflowTemplateCatalog.GetAll().Single(x => x.Key == key);
        IReadOnlyList<AppBindingStep> original = AppBindingStep.FromTemplate(before);
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].StepType != StepType.VideoCompile)
                continue;
            JsonObject bound = Config(defaults, i);
            JsonObject authored = JsonNode.Parse(original[i].ConfigJson!)!.AsObject();
            bound["programFadeOutMs"]!.GetValue<long>().Should().Be(authored["programFadeOutMs"]!.GetValue<int>(), "the default ending is today's 800 ms");
            bound["programAudioFadeOutMs"]!.GetValue<long>().Should().Be(authored["programAudioFadeOutMs"]!.GetValue<int>());
            bound["enableCaptions"]?.GetValue<bool>().Should().BeFalse();
        }

        AppInputBindResult captioned = AppInputBinder.Bind(manifest, steps,
            AppInputValueChecker.Check(manifest, steps, Values(("footageCaptions", "true"), ("ending", "false"))).Values);
        captioned.Problems.Should().BeEmpty();
        int last = steps.ToList().FindLastIndex(s => s.StepType == StepType.VideoCompile);
        Config(captioned, last)["captionSource"]!.GetValue<string>().Should().Be("Transcript");
        Config(captioned, 0)["transcription"]!.GetValue<string>().Should().Be("Required");
        Config(captioned, last)["programFadeOutMs"]!.GetValue<long>().Should().Be(0);
        Dictionary<AgentType, Guid> agents = Enum.GetValues<AgentType>().ToDictionary(x => x, _ => Guid.NewGuid());
        List<CreateWorkflowStepRequest> requests = [.. RecipeInstance.Build(t, [], null, agents).Steps!.Select((r, i) => captioned.Steps[i].Step.StepType switch
        {
            StepType.VideoAnalyze => r with { VideoAnalyzeConfigJson = captioned.Steps[i].Step.ConfigJson },
            StepType.VideoCompile => r with { VideoCompileConfigJson = captioned.Steps[i].Step.ConfigJson },
            _ => r
        })];
        IReadOnlyList<string> errors = StepConfigSaveValidator.Validate(requests);
        errors.Should().BeEmpty("captions on the final encode and the transcript ask are a valid edit");
    }

    [Fact]
    public void The_region_effect_recipe_offers_how_to_hide_the_object()
    {
        WorkflowTemplateDefinition t = WorkflowTemplateCatalog.GetAll().Single(x => x.Key == WorkflowTemplateCatalog.RegionEffectTemplateKey);
        IReadOnlyList<AppBindingStep> steps = AppBindingStep.FromTemplate(t);
        AppInputCheckResult check = AppInputValueChecker.Check(t.AppManifest!, steps, Values(("whatToHide", "\"the plate\""), ("hideWith", "\"Pixelate\"")));
        check.Problems.Should().BeEmpty();
        AppInputBindResult bound = AppInputBinder.Bind(t.AppManifest!, steps, check.Values);
        bound.Problems.Should().BeEmpty();
        int compile = steps.ToList().FindIndex(s => s.StepType == StepType.VideoCompile);
        Config(bound, compile)["regionEffects"]![0]!["effect"]!.GetValue<string>().Should().Be("Pixelate");
        Config(bound, compile)["regionEffects"]![0]!["target"]!.GetValue<string>().Should().Be("the plate");
    }
}
