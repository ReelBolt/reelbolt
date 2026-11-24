using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// The settings a person needs to shape a b-roll film are app inputs (initiative user-decides-max-clips, T-002): the most
/// clips, takes per shot, the video model, cutting on the beat, muting the clips' sound. The run gate prices and refuses on
/// the bound values, and a music bed the compile would refuse after the paid shots is refused up front.
/// </summary>
public class AppInputBRollInputsTests
{
    private static readonly AppManifest App = Manifest(
        Input("mostClips", "number", "Most clips", min: 1, max: 8, def: "2"),
        Input("takes", "number", "Takes per shot", min: 1, max: 4, def: "1"),
        Input("beat", "choice", "Cut on the beat", def: "\"Off\"", options: [Option("\"Off\"", "Off"), Option("\"Beat\"", "On the beat"), Option("\"Bar\"", "On the bar")]),
        Input("mute", "toggle", "Mute the clips' own sound"),
        Input("music", "toggle", "Background music"));

    private const string Generate =
        """{"planSource":"Inline","inlinePrompt":"x","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":1,"maxSpendCredits":5,"appInputs":{"maxClips":"mostClips","takes":"takes"}}""";

    private const string Compile =
        """{"mode":"Reencode","audioCodec":"aac","appInputs":{"beatSync":"beat","muteSourceAudio":"mute","enableMusic":"music"}}""";

    private static WorkflowStep Step(int order, StepType type, string config) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = type,
        AgentDefinitionId = Guid.NewGuid(),
        VideoGenerateConfigJson = type == StepType.VideoGenerate ? config : null,
        VideoCompileConfigJson = type == StepType.VideoCompile ? config : null
    };

    [Fact]
    public void Most_clips_beat_mute_and_music_are_bound_onto_the_run_snapshot()
    {
        AppInputGateResult gate = AppInputValueValidator.Evaluate(App,
            [Step(1, StepType.VideoGenerate, Generate), Step(2, StepType.VideoCompile, Compile)],
            Values(("mostClips", "6"), ("takes", "2"), ("beat", "\"Bar\""), ("mute", "true"), ("music", "true")), null);

        gate.Refused.Should().BeFalse(gate.Message);
        using JsonDocument generate = JsonDocument.Parse(gate.BoundSteps![0].VideoGenerateConfigJson!);
        generate.RootElement.GetProperty("maxClips").GetInt32().Should().Be(6, "the estimate and the hold read the bound ceiling");
        generate.RootElement.GetProperty("takes").GetInt32().Should().Be(2);
        using JsonDocument compile = JsonDocument.Parse(gate.BoundSteps[1].VideoCompileConfigJson!);
        compile.RootElement.GetProperty("beatSync").GetString().Should().Be("Bar");
        compile.RootElement.GetProperty("muteSourceAudio").GetBoolean().Should().BeTrue();
        compile.RootElement.GetProperty("enableMusic").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Fewer_clips_than_takes_is_refused_before_any_spend_naming_the_input()
    {
        AppInputGateResult gate = AppInputValueValidator.Evaluate(App, [Step(1, StepType.VideoGenerate, Generate)],
            Values(("mostClips", "1"), ("takes", "3")), null);

        gate.Code.Should().Be(AppInputValueProblem.Invalid);
        gate.Message.Should().Contain("MaxClips");
    }

    [Fact]
    public void A_music_switch_on_a_stream_copy_compile_is_refused_at_the_gate_not_after_the_paid_shots()
    {
        string copy = """{"mode":"StreamCopy","allowKeyframeSnapping":true,"appInputs":{"enableMusic":"music"}}""";

        AppInputGateResult on = AppInputValueValidator.Evaluate(App, [Step(1, StepType.VideoGenerate, Generate), Step(2, StepType.VideoCompile, copy)],
            Values(("music", "true")), null);
        AppInputGateResult off = AppInputValueValidator.Evaluate(App, [Step(1, StepType.VideoGenerate, Generate), Step(2, StepType.VideoCompile, copy)],
            Values(("music", "false")), null);

        on.Code.Should().Be(AppInputValueProblem.Invalid);
        on.Message.Should().Contain("MUSIC_REQUIRES_REENCODE").And.Contain("Background music");
        off.Refused.Should().BeFalse(off.Message, "no music asked, nothing to refuse");
    }

    [Theory]
    [InlineData("""{"enableMusic":true,"mode":"StreamCopy"}""", "MUSIC_REQUIRES_REENCODE")]
    [InlineData("""{"enableMusic":true,"audioCodec":"copy"}""", "MUSIC_REQUIRES_AUDIO_REENCODE")]
    [InlineData("""{"enableMusic":false,"mode":"StreamCopy"}""", null)]
    [InlineData("""{"enableMusic":true,"mode":"Reencode","audioCodec":"aac"}""", null)]
    public void Saving_a_compile_whose_music_cannot_be_mixed_is_refused(string json, string? code)
    {
        string? refusal = StepConfigSaveValidator.MusicNeedsReencode(JsonDocument.Parse(json).RootElement);

        if (code is null) refusal.Should().BeNull();
        else refusal.Should().StartWith(code);
    }

    [Fact]
    public void The_new_settings_and_the_spend_cap_are_on_the_allowlist_and_file_ids_and_egress_are_not()
    {
        string[] generate = StepCatalog.BindableFieldsOf(StepType.VideoGenerate).Select(f => f.Path).ToArray();
        string[] compile = StepCatalog.BindableFieldsOf(StepType.VideoCompile).Select(f => f.Path).ToArray();

        generate.Should().Contain("maxClips").And.Contain("takes").And.Contain("@model")
            .And.Contain("maxSpendCredits").And.NotContain("allowSourceMediaEgress");
        compile.Should().Contain("beatSync").And.Contain("muteSourceAudio").And.Contain("enableMusic")
            .And.NotContain("musicTrackProjectFileId");
    }

    [Fact]
    public void No_curated_recipe_hides_a_spend_cap_input()
    {
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll().Where(t => t.AppManifest?.Inputs is not null))
        {
            foreach (AppManifestInput input in template.AppManifest!.Inputs!.Where(i => i.Id is "mostCredits" or "bRollCredits"))
            {
                AppInputPlacement.IsUnderMore(input).Should().BeFalse($"{template.Key}: {input.Id} sets a credits cap");
                input.ShowWhen.Should().BeNull();
                input.Default.Should().NotBeNull();
            }
        }
    }

    [Fact]
    public void The_b_roll_recipes_offer_the_most_shots_and_the_dailies_recipe_offers_takes_and_the_model()
    {
        IReadOnlyList<WorkflowTemplateDefinition> all = WorkflowTemplateCatalog.GetAll();
        WorkflowTemplateDefinition broll = all.Single(t => t.Key == "video-derush-edit-broll");
        WorkflowTemplateDefinition dailies = all.Single(t => t.Key == "video-derush-edit-dailies");

        string[] brollIds = broll.AppManifest!.Inputs!.Where(i => i.Id is not null).Select(i => i.Id!).ToArray();
        string[] dailiesIds = dailies.AppManifest!.Inputs!.Where(i => i.Id is not null).Select(i => i.Id!).ToArray();
        brollIds.Should().Contain(["mostShots", "videoModel"]);
        dailiesIds.Should().Contain(["mostShots", "takesPerShot", "videoModel"]);
        broll.Steps.Single(s => s.StepType == StepType.VideoGenerate).VideoGenerateConfigJson.Should().Contain("\"maxClips\":\"mostShots\"");
        dailies.Steps.Single(s => s.StepType == StepType.VideoGenerate).VideoGenerateConfigJson.Should().NotContain("\"maxClips\":\"mostShots\"",
            "a shot is several clips there, so the number goes to the planner only");
        broll.AppManifest.RequestTemplate.Should().Contain("{{input.mostShots}}");
    }

    // ---------------- the credits cap ----------------

    private static readonly AppStepFacts[] Facts = [new(StepType.VideoGenerate)];

    private static IReadOnlyList<string> CapErrors(AppManifestInput input) =>
        AppManifestValidator.Validate(AppManifestValidator.Sanitize(Manifest(input)), Facts,
            [new AppBindingStep(1, StepType.VideoGenerate, Generate.Replace("\"maxClips\":\"mostClips\",\"takes\":\"takes\"", "\"maxSpendCredits\":\"cap\""), null, null, null, null, null, "g")]);

    [Fact]
    public void A_visible_labelled_number_with_a_default_and_a_maximum_may_set_the_spend_cap()
    {
        CapErrors(Input("cap", "number", "Most credits to spend", min: 1, max: 500, def: "200")).Should().BeEmpty();
    }

    [Fact]
    public void A_spend_cap_input_that_is_conditional_unlabelled_unbounded_or_without_a_default_is_refused()
    {
        CapErrors(Input("cap", "number", "Most credits to spend", min: 1, max: 500, def: "200",
            showWhen: new AppInputShowWhen("other", J("true")))).Should().Contain(e => e.Contains("always be shown"));
        // The vocabulary has exactly two ways to hide or collapse an input (unknown members are refused outright): more, showWhen.
        CapErrors(Input("cap", "number", "Most credits to spend", min: 1, max: 500, def: "200", more: true))
            .Should().Contain(e => e.Contains("always be shown"), "a cap under More options is collapsed, not shown");
        CapErrors(Input("cap", "number", "Most credits to spend", min: 1, max: 500, def: "200", more: false)).Should().BeEmpty();
        AppInputPlacement.IsUnderMore(Input("cap", "number", "Most credits to spend", min: 1, max: 500, def: "200")).Should().BeFalse("a number input is shown unless the author says otherwise");
        typeof(AppManifestInput).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            ["Kind", "Field", "Role", "Label", "Default", "Choices", "Min", "Max", "Id", "Type", "Help", "Example", "Required", "MaxLength", "Step", "Unit",
                "Options", "More", "ShowWhen", "Capability", "Job", "Allowed", "Recommended", "Agent", "OnLabel", "OffLabel", "LabelKey", "HelpKey", "ExampleKey", "Permission"],
            "a new manifest member that could hide an input must be reviewed against the spend-cap rule");
        CapErrors(Input("cap", "number", "Most credits to spend", min: 1, max: 500)).Should().Contain(e => e.Contains("visible default"));
        CapErrors(Input("cap", "number", "Most credits to spend", min: 1, def: "200")).Should().Contain(e => e.Contains("needs a maximum"));
        CapErrors(Input("cap", "number", "Most credits to spend", min: 1, max: 999999, def: "200")).Should().Contain(e => e.Contains("at most 100000"));
        CapErrors(Input("cap", "number", " ", min: 1, max: 500, def: "200")).Should().Contain(e => e.Contains("needs a label"));
        CapErrors(Input("cap", "choice", "Most credits to spend", def: "5",
            options: [Option("5", "Five"), Option("999999", "Lots")])).Should().NotBeEmpty();
    }

    [Fact]
    public void The_spend_cap_value_is_bound_onto_the_run_and_a_value_above_the_maximum_is_refused()
    {
        AppManifest app = Manifest(Input("cap", "number", "Most credits to spend", min: 1, max: 500, def: "200"));
        string config = Generate.Replace("\"maxClips\":\"mostClips\",\"takes\":\"takes\"", "\"maxSpendCredits\":\"cap\"");

        AppInputGateResult ok = AppInputValueValidator.Evaluate(app, [Step(1, StepType.VideoGenerate, config)], Values(("cap", "40")), null);
        ok.Refused.Should().BeFalse(ok.Message);
        using JsonDocument bound = JsonDocument.Parse(ok.BoundSteps![0].VideoGenerateConfigJson!);
        bound.RootElement.GetProperty("maxSpendCredits").GetDouble().Should().Be(40, "the estimate, the hold and the 402 read the bound cap");

        AppInputGateResult over = AppInputValueValidator.Evaluate(app, [Step(1, StepType.VideoGenerate, config)], Values(("cap", "501")), null);
        over.Refused.Should().BeTrue();
    }

    private sealed class FixedPricer(double credits) : IAppInputSpendPricer
    {
        public Task<double?> CreditsAsync(WorkflowStep step, CancellationToken ct) => Task.FromResult<double?>(credits);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(1500, false)]
    public async Task A_cap_below_the_price_of_the_bound_step_is_refused_up_front_naming_the_input(double cap, bool refused)
    {
        AppManifest app = Manifest(Input("cap", "number", "Most credits to spend", min: 1, max: 5000, def: "1500"));
        string config = Generate.Replace("\"maxClips\":\"mostClips\",\"takes\":\"takes\"", "\"maxSpendCredits\":\"cap\"");
        List<WorkflowStep> steps = [Step(1, StepType.VideoGenerate, config)];
        AppInputGateResult gate = AppInputValueValidator.Evaluate(app, steps, Values(("cap", cap.ToString(System.Globalization.CultureInfo.InvariantCulture))), null);
        gate.Refused.Should().BeFalse(gate.Message);

        AppInputGateResult checkedGate = await AppInputValueValidator.RefuseCapBelowPriceAsync(app, steps, gate, new FixedPricer(1000), default);

        checkedGate.Refused.Should().Be(refused);
        if (refused)
        {
            checkedGate.Code.Should().Be(AppInputValueValidator.SpendCapTooLowCode);
            checkedGate.InputId.Should().Be("cap");
            checkedGate.Message.Should().Contain("Most credits to spend").And.Contain("1000");
        }
    }

    [Fact]
    public async Task A_fixed_step_cap_below_the_price_of_the_bound_clips_is_refused_naming_the_clips_input()
    {
        AppManifest app = Manifest(Input("mostClips", "number", "Most clips", min: 1, max: 8, def: "2"), Input("takes", "number", "Takes per shot", min: 1, max: 2, def: "1"));
        List<WorkflowStep> steps = [Step(1, StepType.VideoGenerate, Generate)];
        AppInputGateResult gate = AppInputValueValidator.Evaluate(app, steps, Values(("mostClips", "6"), ("takes", "1")), null);
        gate.Refused.Should().BeFalse(gate.Message);

        AppInputGateResult checkedGate = await AppInputValueValidator.RefuseCapBelowPriceAsync(app, steps, gate, new FixedPricer(1000), default);

        checkedGate.Code.Should().Be(AppInputValueValidator.SpendCapTooLowCode);
        checkedGate.InputId.Should().Be("mostClips");
        checkedGate.Message.Should().Contain("spending limit is 5 credits");
    }

    [Theory]
    [InlineData("""{"planSource":"Inline","inlinePrompt":"x","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":1,"maxSpendCredits":5,"AppInputs":{"maxSpendCredits":"cap"}}""")]
    [InlineData("""{"planSource":"Inline","inlinePrompt":"x","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":1,"maxSpendCredits":5,"APPINPUTS":{"maxSpendCredits":"cap"}}""")]
    public async Task The_low_cap_check_finds_the_appInputs_member_like_the_binder_in_any_letter_case(string config)
    {
        AppManifest app = Manifest(Input("cap", "number", "Most credits to spend", min: 1, max: 5000, def: "1500"));
        List<WorkflowStep> steps = [Step(1, StepType.VideoGenerate, config)];
        AppInputGateResult gate = AppInputValueValidator.Evaluate(app, steps, Values(("cap", "500")), null);
        gate.Refused.Should().BeFalse(gate.Message);

        AppInputGateResult checkedGate = await AppInputValueValidator.RefuseCapBelowPriceAsync(app, steps, gate, new FixedPricer(1000), default);

        checkedGate.Code.Should().Be(AppInputValueValidator.SpendCapTooLowCode);
        checkedGate.InputId.Should().Be("cap");
    }

    [Fact]
    public async Task An_object_binding_without_values_is_no_binding_for_the_binder_and_so_none_for_the_low_cap_check()
    {
        // AppValueBinding.TryRead refuses {"input": id} without "values" (the plain form is just the id), so the binder
        // never applies it; the check reads through the same reader and cannot disagree.
        string config = """{"planSource":"Inline","inlinePrompt":"x","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":1,"maxSpendCredits":5,"appInputs":{"maxSpendCredits":{"input":"cap"}}}""";
        AppValueBinding.TryRead(System.Text.Json.Nodes.JsonNode.Parse("""{"input":"cap"}"""), out _, out string? error).Should().BeFalse(error);
        AppManifest app = Manifest(Input("cap", "number", "Most credits to spend", min: 1, max: 5000, def: "1500"));
        List<WorkflowStep> steps = [Step(1, StepType.VideoGenerate, config)];
        AppInputGateResult gate = AppInputValueValidator.Evaluate(app, steps, Values(("cap", "500")), null);

        (await AppInputValueValidator.RefuseCapBelowPriceAsync(app, steps, gate, new FixedPricer(1000), default)).Refused
            .Should().Be(gate.Refused);
    }

    [Fact]
    public void The_dailies_recipe_range_fits_its_own_ceiling_and_cap()
    {
        WorkflowTemplateDefinition dailies = WorkflowTemplateCatalog.GetAll().Single(t => t.Key == "video-derush-edit-dailies");
        AppManifestInput takes = dailies.AppManifest!.Inputs!.Single(i => i.Id == "takesPerShot");
        AppManifestInput shots = dailies.AppManifest.Inputs!.Single(i => i.Id == "mostShots");
        using JsonDocument config = JsonDocument.Parse(dailies.Steps.Single(st => st.StepType == StepType.VideoGenerate).VideoGenerateConfigJson!);
        (shots.Max! * takes.Max!).Should().BeLessThanOrEqualTo(config.RootElement.GetProperty("maxClips").GetInt32(),
            "the most shots at the most takes must fit the step's clip ceiling, and the default cap is sized to that ceiling");
    }

    private static string ExamplePath()
    {
        System.IO.DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "docs", "examples", "reel-c3-app-manifest.json")))
            dir = dir.Parent;
        return System.IO.Path.Combine(dir!.FullName, "docs", "examples", "reel-c3-app-manifest.json");
    }

    [Fact]
    public void The_reel_c3_example_manifest_validates_and_its_default_cap_covers_its_own_range()
    {
        AppManifest manifest = AppManifestJson.TryParse(System.IO.File.ReadAllText(ExamplePath()))!;
        manifest.Should().NotBeNull();
        AppStepFacts[] facts = [new(StepType.VideoAnalyze, TakesProjectVideoFile: true), new(StepType.VideoGenerate), new(StepType.VideoCompile)];
        AppBindingStep[] steps =
        [
            new(1, StepType.VideoGenerate,
                """{"planSource":"Inline","inlinePrompt":"x","takes":1,"maxClips":4,"maxSpendCredits":833,"appInputs":{"maxClips":"mostClips","maxSpendCredits":"mostCredits","takes":"takes","@model":"videoModel"}}""",
                null, null, null, null, null, "g"),
            new(2, StepType.VideoCompile, Compile, null, null, null, null, null, "c")
        ];

        AppManifestValidator.Validate(AppManifestValidator.Sanitize(manifest), facts, steps).Should().BeEmpty();

        AppManifestInput clips = manifest.Inputs!.Single(i => i.Id == "mostClips");
        AppManifestInput takes = manifest.Inputs!.Single(i => i.Id == "takes");
        AppManifestInput cap = manifest.Inputs!.Single(i => i.Id == "mostCredits");
        // About 208.4 credits a clip (the 4-clip recipe cap is 833.33): the default must pay for the top of the range.
        (clips.Max! * takes.Max! * 208.34).Should().BeLessThanOrEqualTo(cap.Default!.Value.GetDouble());
    }
}
