using AwesomeAssertions;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class GradeFilterBuilderHexColorTests
{
    [Fact]
    public void Keyer_uses_a_valid_colour()
    {
        GradeFilterBuilder.Keyer(new ChromaKeyer("#112233")).Should().Contain("color=0x112233");
    }

    [Theory]
    [InlineData("#112233\n")]
    [InlineData("#112233 ")]
    [InlineData("112233")]
    public void Keyer_refuses_a_colour_with_trailing_characters_and_falls_back_to_green(string color)
    {
        GradeFilterBuilder.Keyer(new ChromaKeyer(color)).Should().Contain("color=0x00FF00");
    }
}
