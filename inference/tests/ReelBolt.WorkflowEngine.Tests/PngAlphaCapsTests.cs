using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>A PNG header is attacker-shaped: no dimension is trusted on its own.</summary>
public class PngAlphaCapsTests
{
    [Theory]
    [InlineData(1, 64_000_000)]   // within the pixel cap, but a 64M-wide row
    [InlineData(64_000_000, 1)]
    [InlineData(20_000, 20_000)]  // over the pixel cap
    [InlineData(0, 10)]
    public void Oversized_or_degenerate_sizes_are_outside_the_caps(long width, long height) =>
        PngAlpha.WithinCaps(width, height).Should().BeFalse();

    [Theory]
    [InlineData(5931, 3954)]      // the staging cut-out
    [InlineData(16_384, 3_900)]
    public void Real_picture_sizes_are_within_the_caps(long width, long height) =>
        PngAlpha.WithinCaps(width, height).Should().BeTrue();
}
