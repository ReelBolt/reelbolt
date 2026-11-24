using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The compiled film must play on phones and in messengers, whatever pixel format the filtergraph
/// happened to end in — a render that ended in planar RGB was encoded as H.264 "High 4:4:4
/// Predictive", which WhatsApp refused.
/// </summary>
public class VideoCompileOutputCompatibilityTests
{
    [Fact]
    public void H264_output_is_420_standard_range_with_the_index_up_front()
    {
        var args = new List<string>();
        VideoCompileStepExecutor.AddOutputVideoArgs(args, "libx264", 20, "veryfast");

        args.Should().Equal(
            "-c:v", "libx264", "-crf", "20", "-preset", "veryfast",
            "-pix_fmt", "yuv420p", "-color_range", "tv", "-movflags", "+faststart");
    }

    [Fact]
    public void H265_output_is_tagged_hvc1_so_apple_players_and_whatsapp_accept_it()
    {
        var args = new List<string>();
        VideoCompileStepExecutor.AddOutputVideoArgs(args, "libx265", 24, "fast");

        args.Should().ContainInOrder("-pix_fmt", "yuv420p");
        args.Should().ContainInOrder("-tag:v", "hvc1");
    }
}
