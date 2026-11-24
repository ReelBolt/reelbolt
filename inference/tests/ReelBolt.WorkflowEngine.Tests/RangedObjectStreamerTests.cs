#nullable enable
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class RangedObjectStreamerTests
{
    private const long Length = 1000;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-10")]
    [InlineData("bytes=0-1,5-9")]
    [InlineData("garbage")]
    public void NoUsableRange_ServesWholeObject(string? header)
    {
        RangedObjectStreamer.ParseRange(header, Length).Kind.Should().Be(RangedObjectStreamer.RangeKind.None);
    }

    [Theory]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=500-", 500, 999)]
    [InlineData("bytes=900-5000", 900, 999)] // end is clamped to the object
    [InlineData("bytes=-100", 900, 999)]     // suffix range
    [InlineData("bytes=-5000", 0, 999)]      // suffix longer than the object
    public void SingleRange_IsResolvedAgainstLength(string header, long start, long end)
    {
        (RangedObjectStreamer.RangeKind kind, long s, long e) = RangedObjectStreamer.ParseRange(header, Length);
        kind.Should().Be(RangedObjectStreamer.RangeKind.Satisfiable);
        s.Should().Be(start);
        e.Should().Be(end);
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=2000-3000")]
    [InlineData("bytes=-0")]
    public void RangeOutsideObject_IsUnsatisfiable(string header)
    {
        RangedObjectStreamer.ParseRange(header, Length).Kind.Should().Be(RangedObjectStreamer.RangeKind.Unsatisfiable);
    }

    [Theory]
    [InlineData("bytes=0-", 0, RangedObjectStreamer.OpenEndedChunkBytes - 1)]
    [InlineData("bytes=5000000-", 5_000_000, 5_000_000 + RangedObjectStreamer.OpenEndedChunkBytes - 1)]
    [InlineData("bytes=20500000-", 20_500_000, 21_061_534)]      // the last chunk ends at the object
    [InlineData("bytes=0-21061534", 0, 21_061_534)]               // an explicit range is not capped
    [InlineData("bytes=-3000000", 18_061_535, 21_061_534)]        // nor is a suffix range
    public void OpenEndedRange_OnALargeObject_IsServedOneChunkAtATime(string header, long start, long end)
    {
        // A 21 MB render, the size whose open-ended responses hung Firefox over HTTP/3.
        (RangedObjectStreamer.RangeKind kind, long s, long e) = RangedObjectStreamer.ParseRange(header, 21_061_535);
        kind.Should().Be(RangedObjectStreamer.RangeKind.Satisfiable);
        s.Should().Be(start);
        e.Should().Be(end);
    }

    [Fact]
    public void EmptyObject_AnyRangeIsUnsatisfiable()
    {
        RangedObjectStreamer.ParseRange("bytes=0-", 0).Kind.Should().Be(RangedObjectStreamer.RangeKind.Unsatisfiable);
    }
}
