using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-106: insert content by reference — the config shape, the per-plate choice, and the cache key.</summary>
public class InsertContentRefTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Guid Id1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Id2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static VideoInsertRegionTrack Region(string id = "r0", int sourceIndex = 0) => new(
        Id: id, ShotId: "s0", StartSec: 0, EndSec: 1, Keyframes: [], Confidence: 0.9, MeanAreaRatio: 0.1,
        MeanAspectRatio: 1.7, MotionClass: "Static", ColorName: "green", SourceIndex: sourceIndex);

    private static VideoCompileStepConfig Config() => new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1);

    [Theory]
    [InlineData("{\"from\":\"Step\",\"stepOrder\":3}", "Step")]
    [InlineData("{\"from\":\"StepResult\",\"stepResultId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"}", "StepResult")]
    [InlineData("{\"from\":\"ProjectFile\",\"projectFileId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"}", "ProjectFile")]
    [InlineData("{\"from\":\"step\",\"stepOrder\":1}", "Step")]
    public void The_documented_json_shapes_deserialize_and_validate(string json, string kind)
    {
        InsertContentRef? parsed = JsonSerializer.Deserialize<InsertContentRef>(json, Options);

        parsed!.Kind.Should().Be(kind);
        parsed.Validate().Should().BeNull();
    }

    [Theory]
    [InlineData("{\"from\":\"Bogus\",\"stepOrder\":3}")]
    [InlineData("{\"from\":\"Step\"}")]
    [InlineData("{\"from\":\"Step\",\"stepOrder\":0}")]
    [InlineData("{\"from\":\"Step\",\"stepOrder\":3,\"stepResultId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"}")]
    [InlineData("{\"from\":\"StepResult\"}")]
    [InlineData("{\"from\":\"StepResult\",\"stepResultId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"from\":\"ProjectFile\",\"stepOrder\":3}")]
    public void A_malformed_reference_has_a_validation_error(string json)
    {
        JsonSerializer.Deserialize<InsertContentRef>(json, Options)!.Validate().Should().NotBeNull();
    }

    [Fact]
    public void An_old_config_without_any_reference_deserializes_with_none_and_picks_as_before()
    {
        VideoCompileStepConfig config = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            $"{{\"version\":1,\"decision\":{{\"source\":\"Previous\"}},\"insertContentProjectFileId\":\"{Id1}\"," +
            $"\"insertContents\":[{{\"projectFileId\":\"{Id2}\",\"regionId\":\"r1\"}}]}}", Options)!;

        config.InsertContentRef.Should().BeNull();
        config.InsertContents![0].InsertContentRef.Should().BeNull();
        VideoCompileStepExecutor.PickInsertContent(config, Region("r1")).Should().Be((Id2, (InsertContentFit?)null));
        VideoCompileStepExecutor.PickInsertContent(config, Region("r2")).Should().Be((Id1, (InsertContentFit?)null));
    }

    [Fact]
    public void A_matching_entry_reference_beats_the_config_level_reference_which_beats_the_project_file_id()
    {
        InsertContentRef entryRef = new("Step", StepOrder: 2);
        InsertContentRef configRef = new("StepResult", StepResultId: Id1);
        VideoCompileStepConfig config = Config() with
        {
            InsertContentProjectFileId = Id2,
            InsertContentRef = configRef,
            InsertContents = [new VideoInsertContentAssignment(Guid.Empty, RegionId: "r1", InsertContentRef: entryRef)]
        };

        InsertContentSelector.Pick(config, Region("r1"))!.Value.Ref.Should().Be(entryRef);
        InsertContentSelector.Pick(config, Region("r2"))!.Value.Ref.Should().Be(configRef);
        InsertContentSelector.Pick(config with { InsertContentRef = null }, Region("r2"))!.Value.Ref
            .Should().Be(new InsertContentRef("ProjectFile", ProjectFileId: Id2));
        InsertContentSelector.Pick(Config(), Region("r2")).Should().BeNull();
    }

    [Fact]
    public void The_step_cache_key_varies_with_the_referenced_content()
    {
        static string Key(VideoCompileStepConfig c) => new StepCacheKeyBuilder().Build(new StepCacheKeyInputs
        {
            ProjectId = Id1,
            StepType = StepType.VideoCompile,
            AgentType = AgentType.Custom,
            AgentDefinitionId = Guid.Empty,
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            MaxIterations = 1,
            ProjectFileFingerprint = "fp",
            VideoCompileConfigJson = JsonSerializer.Serialize(c, Options)
        });

        string none = Key(Config());
        string first = Key(Config() with { InsertContentRef = new InsertContentRef("StepResult", StepResultId: Id1) });
        string second = Key(Config() with { InsertContentRef = new InsertContentRef("StepResult", StepResultId: Id2) });

        new[] { none, first, second }.Should().OnlyHaveUniqueItems("a different render is a different key");
    }
}
