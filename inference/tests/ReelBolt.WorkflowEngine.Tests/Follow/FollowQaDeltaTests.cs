using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Follow;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Follow;

/// <summary>QA delta: span-length rounding when fromSec/toSec carry more than 5 decimals.</summary>
public class FollowQaDeltaTests
{
    [Theory]
    [InlineData(0.0, 10.123456)]
    [InlineData(0.0, 10.123457)]
    [InlineData(0.000004, 7.5)]
    [InlineData(1.234564, 61.234566)]
    [InlineData(0.0, 1.0 / 3)]
    public void SpanLengthRounding_StaysValid(double from, double to)
    {
        var s = Enumerable.Range(0, (int)(to * 30) + 2)
            .Select(i => new SubjectSample(i / 30.0, 0.3 + 0.4 * Math.Abs(Math.Sin(i * 0.01)), 0.4, 0.1, 0.15, true)).ToList();
        var o = new FollowOptions(FollowTightness.Tight, FollowSmoothness.Snappy, FollowOnLost.Hold, true, FollowFraming.Full, 4320, 1080, 100);
        CameraPathResult r = SubjectCameraPath.Compute(s, from, to, o);
        IReadOnlyList<VideoReframe> spans = CameraPathReframes.ToReframes(r, 0, from, to);
        VideoReframeValidation v = VideoReframe.Validate(spans);
        v.IsValid.Should().BeTrue(v.Message);
    }
}
