using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

public class ShapeMaskJsonTests
{
    [Fact]
    public void Shape_mask_without_kind_reads_as_rect_and_writes_the_legacy_shape()
    {
        ShapeMask mask = JsonSerializer.Deserialize<ShapeMask>("""{"x":0.25,"feather":0.1}""", EditTimelineJson.Options)!;

        mask.Kind.Should().Be("rect");
        mask.IsSegmentationMask().Should().BeFalse();
        JsonSerializer.Serialize(mask, EditTimelineJson.Options)
            .Should().Be("""{"kind":"rect","x":0.25,"y":0.5,"w":0.5,"h":0.5,"feather":0.1,"invert":false}""");
    }

    [Fact]
    public void Segmentation_mask_round_trips_with_only_its_own_fields()
    {
        const string json = """{"kind":"segmentation","analysisStepResultId":"3c1f0a52-9d4e-4b7a-8e21-6a5b4c3d2e10","trackIds":["g1","g2"],"invert":true,"featherPx":4}""";

        ShapeMask mask = JsonSerializer.Deserialize<ShapeMask>(json, EditTimelineJson.Options)!;

        mask.IsSegmentationMask().Should().BeTrue();
        mask.TrackIds.Should().Equal("g1", "g2");
        JsonSerializer.Serialize(mask, EditTimelineJson.Options).Should().Be(json);
    }
}
