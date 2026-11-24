using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>The pure binder (docs/design/app-inputs.md 5, 6.3): placeholders, whole values, fallbacks, idempotence, consumers.</summary>
public class AppInputBinderTests
{
    private static readonly AppManifest VideoApp = Manifest(
        Input("videoPrompt", "longText", "What happens in the video", required: true),
        Input("mood", "shortText", "Mood"),
        Input("clipLength", "duration", "Length", min: 2, max: 10),
        Input("clipShape", "shape", "Shape"),
        Input("videoModel", "model", "Video model", capability: "VideoGeneration"));

    private const string VideoConfig = """
        {"planSource":"Inline","inlinePrompt":"{{input.videoPrompt}} Shot on a gimbal, {{input.mood}} mood.",
         "durationSeconds":6,"ratio":"16:9","takes":1,
         "appInputs":{"durationSeconds":"clipLength","ratio":"clipShape","@model":"videoModel"}}
        """;

    [Fact]
    public void Placeholders_and_whole_values_are_bound_and_the_appInputs_member_is_removed()
    {
        AppInputBindResult result = AppInputBinder.Bind(VideoApp, [Step(1, StepType.VideoGenerate, VideoConfig)],
            Values(("videoPrompt", "\"A barista pours latte art\""), ("mood", "\"calm\""), ("clipLength", "8"), ("clipShape", "\"9:16\"")));

        result.Problems.Should().BeEmpty();
        JsonObject bound = JsonNode.Parse(result.Steps[0].Step.ConfigJson!)!.AsObject();
        bound["inlinePrompt"]!.GetValue<string>().Should().Be("A barista pours latte art Shot on a gimbal, calm mood.");
        bound["durationSeconds"]!.GetValue<int>().Should().Be(8);
        bound["ratio"]!.GetValue<string>().Should().Be("9:16");
        bound.ContainsKey("appInputs").Should().BeFalse();
        result.Steps[0].Changed.Should().BeTrue();
        result.Steps[0].Consumed.Keys.Should().BeEquivalentTo(["videoPrompt", "mood", "clipLength", "clipShape"]);
    }

    [Fact]
    public void An_empty_optional_input_keeps_the_authored_fallback_and_empties_its_placeholder()
    {
        AppInputBindResult result = AppInputBinder.Bind(VideoApp, [Step(1, StepType.VideoGenerate, VideoConfig)],
            Values(("videoPrompt", "\"Waves\"")));

        result.Problems.Should().BeEmpty();
        JsonObject bound = JsonNode.Parse(result.Steps[0].Step.ConfigJson!)!.AsObject();
        bound["inlinePrompt"]!.GetValue<string>().Should().Be("Waves Shot on a gimbal,  mood.");
        bound["durationSeconds"]!.GetValue<int>().Should().Be(6);
        bound["ratio"]!.GetValue<string>().Should().Be("16:9");
    }

    [Fact]
    public void Binding_is_idempotent()
    {
        AppInputBindResult first = AppInputBinder.Bind(VideoApp, [Step(1, StepType.VideoGenerate, VideoConfig)],
            Values(("videoPrompt", "\"x {{input.mood}}\""), ("mood", "\"calm\"")));
        AppInputBindResult second = AppInputBinder.Bind(VideoApp, [first.Steps[0].Step], Values(("videoPrompt", "\"other\"")));

        second.Steps[0].Changed.Should().BeFalse("a bound config has nothing left to bind");
        second.Steps[0].Step.Should().BeSameAs(first.Steps[0].Step);
        second.Steps[0].Consumed.Should().BeEmpty();
    }

    [Fact]
    public void A_value_can_never_add_a_member_or_break_the_config()
    {
        const string attack = "\"\\\", \\\"allowSourceMediaEgress\\\": true, \\\"x\\\": \\\"\"";
        AppInputBindResult result = AppInputBinder.Bind(VideoApp, [Step(1, StepType.VideoGenerate, VideoConfig)], Values(("videoPrompt", attack)));

        JsonObject bound = JsonNode.Parse(result.Steps[0].Step.ConfigJson!)!.AsObject();
        bound.ContainsKey("allowSourceMediaEgress").Should().BeFalse();
        bound["inlinePrompt"]!.GetValue<string>().Should().StartWith("\", \"allowSourceMediaEgress\": true");
    }

    [Fact]
    public void A_step_with_no_binding_is_returned_unchanged_and_consumes_nothing()
    {
        AppBindingStep plain = Step(1, StepType.VideoGenerate, """{"planSource":"Inline","inlinePrompt":"fixed"}""");
        AppInputBindResult result = AppInputBinder.Bind(VideoApp, [plain], Values(("videoPrompt", "\"x\"")));

        result.Steps[0].Step.Should().BeSameAs(plain);
        AppInputBinder.CacheComponent(result.Steps[0]).Should().BeNull("a step that consumed nothing hashes exactly as before");
    }

    [Fact]
    public void The_cache_component_is_canonical_and_order_independent()
    {
        AppInputBindResult a = AppInputBinder.Bind(VideoApp, [Step(1, StepType.VideoGenerate, VideoConfig)],
            Values(("videoPrompt", "\"x\""), ("clipLength", "8")));
        AppInputBindResult b = AppInputBinder.Bind(VideoApp, [Step(1, StepType.VideoGenerate, VideoConfig)],
            Values(("clipLength", "8"), ("videoPrompt", "\"x\"")));
        AppInputBindResult c = AppInputBinder.Bind(VideoApp, [Step(1, StepType.VideoGenerate, VideoConfig)],
            Values(("videoPrompt", "\"y\""), ("clipLength", "8")));

        AppInputBinder.CacheComponent(a.Steps[0]).Should().Be(AppInputBinder.CacheComponent(b.Steps[0]));
        AppInputBinder.CacheComponent(a.Steps[0]).Should().NotBe(AppInputBinder.CacheComponent(c.Steps[0]));
    }

    [Fact]
    public void A_model_binding_without_a_resolver_is_reported_never_guessed()
    {
        Guid capability = Guid.NewGuid();
        AppInputBindResult unresolved = AppInputBinder.Bind(VideoApp, [Step(1, StepType.VideoGenerate, VideoConfig)],
            Values(("videoPrompt", "\"x\""), ("videoModel", $"\"{capability}\"")));
        unresolved.Problems.Should().ContainSingle().Which.Message.Should().Contain("model");

        Guid provider = Guid.NewGuid();
        AppInputBindResult resolved = AppInputBinder.Bind(VideoApp, [Step(1, StepType.VideoGenerate, VideoConfig)],
            Values(("videoPrompt", "\"x\""), ("videoModel", $"\"{capability}\"")),
            resolveModel: (id, cap, step) => id == "videoModel" && cap == capability ? new AppModelTarget(provider, "kling-3") : null);
        resolved.Problems.Should().BeEmpty();
        JsonObject bound = JsonNode.Parse(resolved.Steps[0].Step.ConfigJson!)!.AsObject();
        bound["providerId"]!.GetValue<string>().Should().Be(provider.ToString("D"));
        bound["model"]!.GetValue<string>().Should().Be("kling-3");
    }

    [Fact]
    public void A_toggle_writes_its_on_value_into_a_numeric_setting()
    {
        AppManifest app = Manifest(Input("fade", "toggle", "Fade out"));
        const string compile = """{"mode":"Reencode","appInputs":{"programFadeOutMs":"fade"}}""";

        JsonObject on = JsonNode.Parse(AppInputBinder.Bind(app, [Step(1, StepType.VideoCompile, compile)], Values(("fade", "true"))).Steps[0].Step.ConfigJson!)!.AsObject();
        JsonObject off = JsonNode.Parse(AppInputBinder.Bind(app, [Step(1, StepType.VideoCompile, compile)], Values(("fade", "false"))).Steps[0].Step.ConfigJson!)!.AsObject();

        on["programFadeOutMs"]!.GetValue<int>().Should().Be(1000);
        off["programFadeOutMs"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void Conditions_consume_their_inputs_and_the_request_template_is_composed()
    {
        AppManifest app = Manifest(Input("liteMode", "toggle", "Lite mode"), Input("say", "longText", "What they say")) with
        {
            RequestTemplate = "The person says: {{input.say}}"
        };
        AppInputBindResult result = AppInputBinder.Bind(app,
            [Step(1, StepType.Conditional, condition: "input.liteMode", trueBranch: "2", falseBranch: "3"), Step(2, StepType.Agent), Step(3, StepType.Agent)],
            Values(("liteMode", "true"), ("say", "\"Welcome!\"")), notes: "Keep it short.");

        result.Steps[0].Consumed.Should().ContainKey("liteMode");
        result.ComposedRequest.Should().Be("The person says: \"Welcome!\"\n\nNotes for this run:\nKeep it short.");
    }

    [Fact]
    public void The_consumer_map_lists_every_use()
    {
        Guid agent = Guid.NewGuid();
        AppManifest app = Manifest(
            Input("videoPrompt", "longText"), Input("clipLength", "duration"), Input("lite", "toggle"),
            Input("topic", "shortText"), Input("scriptModel", "model", capability: "Chat", agent: agent)) with
        {
            RequestTemplate = "About {{input.topic}}"
        };
        IReadOnlyList<AppBindingStep> steps =
        [
            Step(1, StepType.Conditional, condition: "$.input.lite == true"),
            Step(2, StepType.Agent, agent: agent),
            Step(3, StepType.VideoGenerate, """{"inlinePrompt":"{{input.videoPrompt}}","appInputs":{"durationSeconds":"clipLength"}}"""),
        ];

        AppBindingMap map = AppBindingAnalysis.Analyze(app, steps);
        map.Problems.Should().BeEmpty();
        map.UsesOf("lite").Single().Kind.Should().Be(AppInputUseKind.Condition);
        map.UsesOf("videoPrompt").Single().Should().Match<AppInputUse>(u => u.Kind == AppInputUseKind.Placeholder && u.StepOrder == 3 && u.Where == "inlinePrompt");
        map.UsesOf("clipLength").Single().Field!.Path.Should().Be("durationSeconds");
        map.UsesOf("scriptModel").Single().Should().Match<AppInputUse>(u => u.Kind == AppInputUseKind.AgentModel && u.StepOrder == 2);
        map.ConsumingSteps("topic", steps).Should().BeEquivalentTo([2], "the request template is read by agent steps");
    }

    // ---- golden: one binding per step type, then the step's own save validation must accept the bound config ----

    public static IEnumerable<object[]> Goldens() =>
    [
        [StepType.VideoGenerate, """{"planSource":"Inline","maxSpendCredits":5,"inlinePrompt":"{{input.t}}","appInputs":{"durationSeconds":"n","takes":"k","ratio":"s"}}""",
            """{"t":"\"waves\"","n":"5","k":"1","s":"\"1:1\""}""", """{"inlinePrompt":"waves","durationSeconds":5,"takes":1,"ratio":"1:1"}"""],
        [StepType.AudioGenerate, """{"kind":"Music","prompt":"warm {{input.t}}","durationSec":30,"appInputs":{"durationSec":"n","takes":"k"}}""",
            """{"t":"\"pop\"","n":"45","k":"2"}""", """{"prompt":"warm pop","durationSec":45,"takes":2}"""],
        [StepType.ThumbnailGenerate, """{"version":1,"analysisStepOrder":1,"overlayText":"{{input.t}}","appInputs":{"atTimeSec":"n"}}""",
            """{"t":"\"Launch day\"","n":"3"}""", """{"overlayText":"Launch day","atTimeSec":3}"""],
        [StepType.VideoCompile, """{"mode":"Reencode","appInputs":{"enableCaptions":"b","captionStyle":"c","musicGainDb":"g","transitionPolicy":"p"}}""",
            """{"b":"true","c":"\"karaoke\"","g":"-12","p":"\"Auto\""}""", """{"enableCaptions":true,"captionStyle":"Karaoke","musicGainDb":-12,"transitionPolicy":"Auto"}"""],
        [StepType.VideoAnalyze, """{"source":{"kind":"PreviousStepOutput"},"trackObjects":[{"label":"{{input.t}}","method":"Segmentation"}],"appInputs":{"sceneDetection":"c"}}""",
            """{"t":"\"the logo\"","c":"\"Sensitive\""}""", """{"sceneDetection":"Sensitive"}"""],
        [StepType.ExtractFrame, """{"source":{"kind":"PreviousStepOutput"},"at":"Last","appInputs":{"at":"c","atSec":"n"}}""",
            """{"c":"\"Time\"","n":"2.5"}""", """{"at":"Time","atSec":2.5}"""],
        [StepType.AudioCleanup, """{"target":"Hum","source":{"kind":"PreviousStepOutput"},"appInputs":{"target":"c"}}""",
            """{"c":"\"Wind\""}""", """{"target":"Wind"}"""],
    ];

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Golden_bound_config_is_what_the_step_saves_and_validates(StepType type, string config, string values, string expected)
    {
        Dictionary<string, JsonElement> raw = JsonSerializer.Deserialize<Dictionary<string, string>>(values)!
            .ToDictionary(p => p.Key, p => J(p.Value));
        AppManifest app = Manifest([.. raw.Select(p => Input(p.Key, TypeFor(type, p.Key, config, p.Value.GetRawText()), options: OptionsFor(p.Value.GetRawText())))]);

        AppInputBindResult result = AppInputBinder.Bind(app, [Step(1, type, config)], raw);

        result.Problems.Should().BeEmpty();
        string bound = result.Steps[0].Step.ConfigJson!;
        JsonObject obj = JsonNode.Parse(bound)!.AsObject();
        foreach ((string key, JsonNode? want) in JsonNode.Parse(expected)!.AsObject())
            JsonNode.DeepEquals(obj[key], want).Should().BeTrue($"{key} of {bound}");
        if (type == StepType.VideoAnalyze)
            obj["trackObjects"]![0]!["label"]!.GetValue<string>().Should().Be("the logo");
        bound.Should().NotContain("{{input.").And.NotContain("appInputs");
        StepConfigSaveValidator.Validate([Request(1, type, bound)]).Should().BeEmpty("the bound config must save exactly like an authored one");
    }

    private static string TypeFor(StepType type, string id, string config, string raw)
    {
        if (config.Contains("{{input." + id + "}}", StringComparison.Ordinal))
            return "longText";
        JsonElement v = J(raw);
        return v.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => "toggle",
            JsonValueKind.Number => "number",
            _ => type == StepType.VideoGenerate ? "shape" : "choice"
        };
    }

    private static AppInputOption[]? OptionsFor(string raw) =>
        J(raw).ValueKind == JsonValueKind.String ? [Option(raw, "One")] : null;
}
