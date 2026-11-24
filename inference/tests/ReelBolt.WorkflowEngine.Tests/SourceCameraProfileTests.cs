#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>log-footage-color T-013: per-source camera profile for VideoCompile and VideoAnalyze.</summary>
public class SourceCameraProfileTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static MediaProbeResult AppleLogProbe() => new(
        10, 30, 1, 1920, 1080, "prores", "pcm_s16le", 48000, PixFmt: "yuv422p10le", Container: "mov,mp4,m4a,3gp,3g2,mj2",
        ColorTransfer: null, ColorPrimaries: "bt2020", ColorSpace: "bt2020nc", ColorRange: "pc", BitDepth: 10, Make: "Apple", Model: "iPhone 15 Pro");

    private static MediaProbeResult PlainProbe() => new(10, 30, 1, 1920, 1080, "h264", "aac", 48000);

    private static VideoCompileStepConfig Compile(IReadOnlyDictionary<int, string>? profiles = null) =>
        new(1, new ExtractInputRef(ExtractInputSource.Previous), 1) { SourceInputProfiles = profiles };

    [Fact]
    public void Config_validation_accepts_ids_and_auto_and_refuses_unknown_naming_the_field()
    {
        Compile().Validate().Should().BeEmpty();
        Compile(new Dictionary<int, string> { [0] = "auto", [1] = CameraProfiles.Ids[0] }).Validate().Should().BeEmpty();

        List<string> errors = Compile(new Dictionary<int, string> { [0] = "nonsense" }).Validate();
        errors.Should().ContainSingle().Which.Should().Contain("SourceInputProfiles[0]").And.Contain("auto");
        Compile(new Dictionary<int, string> { [-1] = "auto" }).Validate().Should().ContainSingle().Which.Should().Contain("SourceInputProfiles");

        VideoAnalyzeStepConfig analyze = new(1, new VideoSourceRef(VideoSourceKind.ProjectFile)) { SourceInputProfiles = new Dictionary<int, string> { [0] = "bogus" } };
        analyze.ValidateSourceInputProfiles().Should().ContainSingle().Which.Should().Contain("SourceInputProfiles[0]");
        new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.ProjectFile)).ValidateSourceInputProfiles().Should().BeEmpty();
    }

    [Fact]
    public void A_config_without_the_field_deserialises_to_null_and_round_trips_the_map()
    {
        VideoCompileStepConfig old = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            "{\"version\":1,\"decision\":{\"from\":\"Previous\"},\"analysisStepOrder\":1}", Web)!;
        old.SourceInputProfiles.Should().BeNull();

        string json = JsonSerializer.Serialize(Compile(new Dictionary<int, string> { [2] = "auto" }), Web);
        JsonSerializer.Deserialize<VideoCompileStepConfig>(json, Web)!.SourceInputProfiles.Should().Equal(new Dictionary<int, string> { [2] = "auto" });
    }

    [Fact]
    public void Pick_resolves_explicit_and_auto_only_on_a_high_hint()
    {
        SourceCameraProfile.Pick(CameraProfiles.Ids[0], PlainProbe()).Should().Be(new SourceCameraProfile.Choice(CameraProfiles.Ids[0], "explicit"));
        SourceCameraProfile.Pick("auto", AppleLogProbe()).Should().Be(new SourceCameraProfile.Choice("appleLog", "auto"));
        SourceCameraProfile.Pick("auto", PlainProbe()).Should().BeNull("no maker evidence");
        SourceCameraProfile.Pick("nope", PlainProbe()).Should().BeNull();
        SourceCameraProfile.Pick(null, AppleLogProbe()).Should().BeNull();
        SourceCameraProfile.Pick("appleLog", PlainProbe() with { VideoCodec = null, Width = 0, Height = 0 }).Should().BeNull("audio-only source");
    }

    [Fact]
    public void The_kind_carries_the_stage_and_an_hdr_kind_still_goes_to_the_tone_map()
    {
        string kind = SourceCameraProfile.EncodeKind("appleLog", "/tmp/a b.cube", useZscale: true);
        SourceCameraProfile.IsKind(kind).Should().BeTrue();
        SourceCameraProfile.IsKind("hlg").Should().BeFalse();
        SourceCameraProfile.Filter(kind).Should().Be(CameraProfileStage.Filter("appleLog", "/tmp/a b.cube", true));
        SourceCameraProfile.Filter("hlg").Should().Be(HdrToneMap.Filter("hlg"));
        SourceCameraProfile.RecipeTokenOf(kind).Should().Be(CameraProfileStage.RecipeToken("appleLog"));
        SourceCameraProfile.RecipeTokenOf("hlg").Should().BeNull();
        SourceCameraProfile.Filter(SourceCameraProfile.EncodeKind("appleLog", "/c.cube", false)).Should().Contain("scale=in_range=tv");
    }

    [Fact]
    public void The_analysis_copy_argv_is_identical_without_a_profile_and_converts_after_scaling_with_one()
    {
        string[] plain = AnalysisProxy.Build("in.mp4", "out.mkv", 4, null);
        AnalysisProxy.Build("in.mp4", "out.mkv", 4, null, 0, null).Should().Equal(plain);
        string kind = SourceCameraProfile.EncodeKind("appleLog", "/tmp/p.cube", true);
        string[] withProfile = AnalysisProxy.Build("in.mp4", "out.mkv", 4, null, 0, kind);
        string vf = withProfile[withProfile.ToList().IndexOf("-vf") + 1];
        vf.Should().StartWith(plain[plain.ToList().IndexOf("-vf") + 1] + ",").And.EndWith(CameraProfileStage.Filter("appleLog", "/tmp/p.cube", true));
    }

    [Fact]
    public void A_step_without_profiles_records_the_same_resolved_input_and_a_profile_adds_only_its_recipe()
    {
        MethodInfo method = typeof(VideoCompileStepExecutor).GetMethod(
            "BuildResolvedInputDescriptor", BindingFlags.NonPublic | BindingFlags.Static, [typeof(VideoCompileStepConfig), typeof(VideoEditDecisionOutput), typeof(IReadOnlyList<(int, SourceCameraProfile.Choice)>)])!;
        var decision = new VideoEditDecisionOutput();
        string without = (string)method.Invoke(null, [Compile(), decision, null])!;
        without.Should().Be("{\"analysisStepOrder\":1,\"analysisStepResultId\":null,\"decisionFrom\":\"Previous\",\"keepSpanCount\":0}");

        var applied = new List<(int, SourceCameraProfile.Choice)> { (0, new("appleLog", "auto")) };
        string with = (string)method.Invoke(null, [Compile(), decision, applied])!;
        with.Should().Contain("inputProfileRecipes").And.Contain(CameraProfileStage.RecipeToken("appleLog"));

        var builder = new StepCacheKeyBuilder();
        builder.Build(Inputs(without)).Should().Be(builder.Build(Inputs(without)));
        builder.Build(Inputs(without)).Should().NotBe(builder.Build(Inputs(with)));
        StepCacheKeyInputs.SchemaVersion.Should().Be("v13");
    }

    private static StepCacheKeyInputs Inputs(string resolved) => new()
    {
        ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        StepType = ReelBolt.Shared.Data.Models.StepType.VideoCompile,
        AgentType = ReelBolt.Shared.Data.Models.AgentType.Custom,
        AgentDefinitionId = Guid.Empty,
        AgentInputContextMode = default,
        MaxIterations = 1,
        ProjectFileFingerprint = "fp",
        ResolvedInput = resolved
    };
}
