using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class DeterministicStepRevisionsSceneDetectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"sceneDetection\":null}")]
    [InlineData("{\"version\":1,\"sceneDetection\":\"Standard\"}")]
    [InlineData("not json")]
    public void VideoAnalyze_without_Sensitive_keeps_the_plain_tag(string? configJson)
    {
        DeterministicStepRevisions.For(StepType.VideoAnalyze, configJson)
            .Should().Be(DeterministicStepRevisions.For(StepType.VideoAnalyze));
    }

    [Theory]
    [InlineData("{\"version\":1,\"sceneDetection\":\"Sensitive\"}")]
    [InlineData("{\"SceneDetection\":\"sensitive\"}")]
    [InlineData("{\"sceneDetection\":1}")]
    public void VideoAnalyze_with_Sensitive_gets_its_own_tag(string configJson)
    {
        DeterministicStepRevisions.For(StepType.VideoAnalyze, configJson)
            .Should().StartWith(DeterministicStepRevisions.For(StepType.VideoAnalyze)).And.Contain("sensitive-scenes");
    }

    [Theory]
    [InlineData("{\"sceneDetection\":\"Soft\"}")]
    [InlineData("{\"SceneDetection\":\"soft\"}")]
    [InlineData("{\"sceneDetection\":2}")]
    public void VideoAnalyze_with_Soft_gets_its_own_tag_and_no_other_config_does(string configJson)
    {
        DeterministicStepRevisions.For(StepType.VideoAnalyze, configJson)
            .Should().StartWith(DeterministicStepRevisions.For(StepType.VideoAnalyze)).And.EndWith("+soft-scenes-1");
        DeterministicStepRevisions.For(StepType.VideoAnalyze, "{\"sceneDetection\":\"Sensitive\"}")
            .Should().NotContain("soft-scenes");
    }

    [Fact]
    public void Other_step_types_ignore_the_config()
    {
        DeterministicStepRevisions.For(StepType.VideoCompile, "{\"sceneDetection\":\"Sensitive\"}")
            .Should().Be(DeterministicStepRevisions.For(StepType.VideoCompile));
    }
}
