using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class VideoCompileOverlayFontColorTests
{
    [Theory]
    [InlineData("#AABBCC", true)]
    [InlineData("white", true)]
    [InlineData("#AABBCC\n", false)]
    public void Overlay_font_colour_refuses_a_trailing_newline(string color, bool valid) =>
        VideoCompileStepExecutor.IsValidOverlayFontColor(color).Should().Be(valid);
}
