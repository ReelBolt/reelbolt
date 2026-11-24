using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Save-time refusal of reframe configs (<see cref="StepConfigSaveValidator"/>, the 400 body's
/// <c>errors</c>): the same <c>VideoReframe.CheckConfig</c> the compile re-runs before building
/// its graph, so an oversized or malformed reframe list never reaches a stored workflow.
/// </summary>
public sealed class StepConfigSaveValidatorReframeTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static CreateWorkflowStepRequest CompileStep(JsonObject config) =>
        JsonSerializer.Deserialize<CreateWorkflowStepRequest>(new JsonObject
        {
            ["stepOrder"] = 3,
            ["label"] = "Compile",
            ["stepType"] = nameof(StepType.VideoCompile),
            ["videoCompileConfigJson"] = config.ToJsonString()
        }.ToJsonString(), WebJson)!;

    private static JsonObject Config(JsonArray? reframes = null, double? safeArea = null)
    {
        var config = new JsonObject
        {
            ["version"] = 1,
            ["decision"] = new JsonObject { ["from"] = "Previous" },
            ["analysisStepOrder"] = 1
        };
        if (reframes is not null)
            config["reframes"] = reframes;
        if (safeArea is not null)
            config["subjectSafeArea"] = safeArea;
        return config;
    }

    private static JsonArray Reframes(int count) =>
        new(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
        {
            ["sourceIndex"] = 0,
            ["fromSec"] = i,
            ["toSec"] = i + 1,
            ["keyframes"] = new JsonArray(new JsonObject { ["atSec"] = 0, ["zoom"] = 2, ["easing"] = "EaseOut" })
        }).ToArray());

    [Fact]
    public void A_compile_with_no_reframes_saves_exactly_as_before()
    {
        StepConfigSaveValidator.ValidateStep(CompileStep(Config())).Should().BeEmpty();
    }

    /// <summary>QA-P1-19 item 1: the range lived only on the run-time <c>Validate()</c>, so a stored config
    /// could carry an impossible music gain and fail at run time instead of at save.</summary>
    [Theory]
    [InlineData(-40.0, true)]
    [InlineData(6.0, true)]
    [InlineData(-41.5, false)]
    [InlineData(9.0, false)]
    public void A_music_gain_outside_the_range_is_refused_at_save(double gain, bool saves)
    {
        var config = new JsonObject
        {
            ["version"] = 1,
            ["decision"] = new JsonObject { ["from"] = "Previous" },
            ["analysisStepOrder"] = 1,
            ["musicGainDb"] = gain
        };

        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(CompileStep(config));

        if (saves)
            errors.Should().BeEmpty();
        else
            errors.Should().ContainSingle().Which.Should().Contain("MusicGainDb must be between");
    }

    [Fact]
    public void Sixteen_reframes_of_a_clip_save_and_seventeen_are_a_400()
    {
        StepConfigSaveValidator.ValidateStep(CompileStep(Config(Reframes(16)))).Should().BeEmpty();

        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(CompileStep(Config(Reframes(17))));

        errors.Should().ContainSingle().Which.Should().StartWith("Step 3 (VideoCompile): REFRAME_TOO_MANY_REFRAMES:");
        StepConfigSaveValidator.FormatMessage(errors).Should().Be(errors.Single());
    }

    [Fact]
    public void An_out_of_range_zoom_and_safe_area_are_refused_with_their_codes()
    {
        JsonArray bad = Reframes(1);
        bad[0]!["keyframes"]![0]!["zoom"] = 9;
        StepConfigSaveValidator.ValidateStep(CompileStep(Config(bad))).Single()
            .Should().Contain("REFRAME_ZOOM_OUT_OF_RANGE");
        StepConfigSaveValidator.ValidateStep(CompileStep(Config(safeArea: 0.3))).Single()
            .Should().Contain("SUBJECT_SAFE_AREA_OUT_OF_RANGE");
    }

    [Fact]
    public void A_reframes_list_that_is_not_a_reframe_list_is_refused()
    {
        JsonObject config = Config();
        config["reframes"] = "lots";
        StepConfigSaveValidator.ValidateStep(CompileStep(config)).Single()
            .Should().StartWith("Step 3 (VideoCompile): VideoCompileConfigJson is not a valid compile config:");
    }

    [Theory]
    [InlineData("""{"enableCaptions":true,"captionSource":"Transcript"}""", true)]
    [InlineData("""{"enableCaptions":true,"captionSource":"Transcript","captionTranslationStepOrder":2}""", true)]
    [InlineData("""{"enableCaptions":true,"captionSource":"Voiceover"}""", true)]
    [InlineData("""{"captionSource":"Transcript"}""", false)]
    [InlineData("""{"enableCaptions":true,"captionTranslationStepOrder":2}""", false)]
    [InlineData("""{"enableCaptions":true,"captionSource":"Transcript","captionTranslationStepOrder":0}""", false)]
    public void The_transcript_caption_source_is_checked_at_save(string members, bool saves)
    {
        JsonObject config = Config();
        foreach (var (key, value) in JsonNode.Parse(members)!.AsObject())
            config[key] = value!.DeepClone();

        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(CompileStep(config));

        if (saves)
            errors.Should().BeEmpty();
        else
            errors.Should().ContainSingle().Which.Should().Contain("Caption");
    }

    [Fact]
    public void Source_input_profiles_are_checked_at_save_for_compile_and_analyze_naming_the_field()
    {
        JsonObject bad = Config();
        bad["sourceInputProfiles"] = new JsonObject { ["0"] = "nonsense" };
        StepConfigSaveValidator.ValidateStep(CompileStep(bad)).Should().ContainSingle().Which.Should().Contain("SourceInputProfiles[0]");

        JsonObject good = Config();
        good["sourceInputProfiles"] = new JsonObject { ["0"] = "auto", ["1"] = "appleLog" };
        StepConfigSaveValidator.ValidateStep(CompileStep(good)).Should().BeEmpty();

        CreateWorkflowStepRequest Analyze(string profile) => JsonSerializer.Deserialize<CreateWorkflowStepRequest>(new JsonObject
        {
            ["stepOrder"] = 1,
            ["label"] = "Analyze",
            ["stepType"] = nameof(StepType.VideoAnalyze),
            ["videoAnalyzeConfigJson"] = new JsonObject
            {
                ["version"] = 1,
                ["source"] = new JsonObject { ["kind"] = "ProjectFile" },
                ["sourceInputProfiles"] = new JsonObject { ["0"] = profile }
            }.ToJsonString()
        }.ToJsonString(), WebJson)!;
        StepConfigSaveValidator.ValidateStep(Analyze("bogus")).Should().ContainSingle().Which.Should().Contain("SourceInputProfiles[0]");
        StepConfigSaveValidator.ValidateStep(Analyze("auto")).Should().BeEmpty();
    }
}
