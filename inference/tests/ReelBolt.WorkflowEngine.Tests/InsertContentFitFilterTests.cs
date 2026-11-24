using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class InsertContentFitFilterTests
{
    [Fact]
    public void Auto_contains_a_landscape_user_video_on_a_portrait_plate()
    {
        // The launch promo (1920x1080) on the phone plate (aspect 0.45): pad to the plate's shape.
        string? f = InsertContentFitFilter.Build(InsertContentFit.Auto, 1920, 1080, 0.45);
        f.Should().Be("pad=1920:4268:(ow-iw)/2:(oh-ih)/2:black");
    }

    [Fact]
    public void Auto_always_stretches_content_authored_for_the_plate()
    {
        InsertContentFitFilter.Build(InsertContentFit.Auto, 1920, 1080, 0.45, authoredForPlate: true).Should().BeNull();
    }

    [Fact]
    public void Auto_stretches_a_user_video_whose_aspect_is_already_close()
    {
        InsertContentFitFilter.Build(InsertContentFit.Auto, 720, 1600, 0.47).Should().BeNull();
    }

    [Fact]
    public void Cover_crops_to_the_plate_aspect()
    {
        InsertContentFitFilter.Build(InsertContentFit.Cover, 1920, 1080, 0.45)
            .Should().Be("crop=486:1080:(iw-ow)/2:(ih-oh)/2");
    }

    [Fact]
    public void Stretch_and_unusable_dimensions_produce_no_prefilter()
    {
        InsertContentFitFilter.Build(InsertContentFit.Stretch, 1920, 1080, 0.45).Should().BeNull();
        InsertContentFitFilter.Build(InsertContentFit.Contain, 0, 1080, 0.45).Should().BeNull();
        InsertContentFitFilter.Build(InsertContentFit.Contain, 1920, 1080, 0).Should().BeNull();
    }

    [Fact]
    public void Overscan_pad_is_centred_and_even()
    {
        InsertContentFitFilter.OverscanPad(0.1).Should().Be("pad=trunc(iw*1.1/2)*2:trunc(ih*1.1/2)*2:(ow-iw)/2:(oh-ih)/2:black");
        InsertContentFitFilter.Append(null, "b").Should().Be("b");
        InsertContentFitFilter.Append("a", "b").Should().Be("a,b");
    }
}
