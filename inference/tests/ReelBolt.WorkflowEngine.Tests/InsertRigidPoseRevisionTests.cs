using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public sealed class InsertRigidPoseRevisionTests
{
    [Fact]
    public void VideoAnalyze_revision_carries_insert_rigid_pose_only_when_insert_regions_are_detected()
    {
        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"detectInsertRegions":true}""")
            .Should().Contain("+insert-rigid-pose-1");
        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"version":1}""")
            .Should().NotContain("+insert-rigid-pose-1");
    }
}
