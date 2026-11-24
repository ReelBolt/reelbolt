using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class SceneAndTargetModeConfigTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Fact]
    public void Both_new_modes_default_to_null_and_old_payloads_still_deserialize()
    {
        VideoAnalyzeStepConfig analyze = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>("{\"version\":1}", Web)!;
        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>("{\"version\":1}", Web)!;

        analyze.SceneDetection.Should().BeNull();
        compile.TargetDurationMode.Should().BeNull();
    }

    [Fact]
    public void Modes_are_read_by_name()
    {
        JsonSerializer.Deserialize<VideoAnalyzeStepConfig>("{\"version\":1,\"sceneDetection\":\"Sensitive\"}", Web)!
            .SceneDetection.Should().Be(SceneDetectionMode.Sensitive);
        JsonSerializer.Deserialize<VideoCompileStepConfig>("{\"version\":1,\"targetDurationSec\":30,\"targetDurationMode\":\"WholeShots\"}", Web)!
            .TargetDurationMode.Should().Be(TargetDurationMode.WholeShots);
    }
}
