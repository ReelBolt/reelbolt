#nullable enable
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Security finding F-3 (security verdict <c>prompted-region-effects</c>): the credit hold prices
/// <see cref="SegmentationCreditPricing.AssumedMaxFps"/> = 30 fps, while the vendor bills the clip's REAL
/// frame count. A 60 or 120 fps source therefore cost two to four times the reservation and settled the
/// organization negative after the fact. The analyze step now refuses such a source before any provider
/// call with <c>SEGMENTATION_FRAME_BUDGET_EXCEEDED</c>, a <see cref="FailureClass.UserInput"/> failure.
/// Offline: the vendor is a counting fake and no network is touched.
/// </summary>
public sealed class SegmentationFrameBudgetTests
{
    private static VideoAnalyzeStepConfig Config(int maxSeconds) => new VideoAnalyzeStepConfig(
        Version: 1,
        Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput),
        TrackObjects: [new ObjectTrackTarget("speedometer", Method: ObjectTrackMethod.Segmentation)])
    with
    {
        AllowSourceMediaEgress = true,
        MaxSegmentationSeconds = maxSeconds
    };

    [Theory]
    [InlineData(24, 1_440)]
    [InlineData(30, 1_800)]
    public async Task A_24_or_30_fps_clip_at_the_sixty_second_cap_runs(double fps, int expectedFrames)
    {
        // The rates the hold was priced for: both are inside the 1,808 frames a 60 s hold buys.
        var provider = new SegmentationTrackingTests.CountingClient();
        SegmentationTrackingTests.Harness harness = new(provider);

        SegmentationSourceOutcome outcome = await harness.RunAsync(Config(60), durationSec: 60, fps: fps);

        outcome.FailureCode.Should().BeNull("the hold covers the whole clip at a frame rate it priced");
        provider.Calls.Should().Be(1);
        outcome.Tracks.Single().FrameCount.Should().Be(expectedFrames);
    }

    [Fact]
    public async Task A_60_fps_clip_at_the_sixty_second_cap_is_refused_before_any_provider_call()
    {
        // The step's DEFAULT cap — 60 s — at 60 fps is 3,600 frames against a hold that bought 1,808.
        var provider = new SegmentationTrackingTests.CountingClient();
        SegmentationTrackingTests.Harness harness = new(provider);

        SegmentationSourceOutcome outcome = await harness.RunAsync(Config(60), durationSec: 60, fps: 60);

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.FrameBudgetExceeded);
        outcome.FailureClass.Should().Be(FailureClass.UserInput);
        outcome.FailureMessage.Should().Contain("3600 frames")
            .And.Contain("60 fps")
            .And.Contain("1808 frames")
            .And.Contain("60s cap")
            .And.Contain("30 fps", "the message names the rate the hold priced")
            .And.Contain("Trim the clip or use a lower frame rate")
            .And.Contain("not truncated");

        provider.Calls.Should().Be(0, "the refusal must precede the paid call");
        harness.Presign.Calls.Should().Be(0, "and precede minting a URL to the customer's footage");
    }

    [Fact]
    public async Task A_clip_exactly_at_the_frame_budget_passes()
    {
        // A 16 s cap buys ceil(16 x 30 / 16) = 30 frame-units = 480 frames. 16 s at 30 fps is exactly 480.
        SegmentationTrackingTests.Harness harness = new(new SegmentationTrackingTests.CountingClient());

        SegmentationSourceOutcome outcome = await harness.RunAsync(Config(16), durationSec: 16, fps: 30);

        outcome.FailureCode.Should().BeNull("the boundary is inside the budget, not over it");
        outcome.Tracks.Single().FrameCount.Should().Be(480);
    }

    [Fact]
    public async Task A_frame_rate_above_the_priced_rate_is_refused_even_at_the_cap()
    {
        // 16 s at 30.5 fps = 488 frames: the duration is exactly the cap, so only the FRAME budget can
        // catch it — which is the whole point of pricing the two quantities as one.
        SegmentationTrackingTests.Harness harness = new(new SegmentationTrackingTests.CountingClient());

        SegmentationSourceOutcome outcome = await harness.RunAsync(Config(16), durationSec: 16, fps: 30.5);

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.FrameBudgetExceeded);
        outcome.FailureMessage.Should().Contain("488 frames").And.Contain("30.5 fps").And.Contain("480 frames");
        harness.Presign.Calls.Should().Be(0);
    }

    [Fact]
    public void The_priced_frame_budget_is_the_vendors_own_arithmetic()
    {
        // FrameUnitsFor rounds UP to whole 16-frame blocks, so the budget in frames is that product.
        SegmentationCreditPricing.PricedFramesFor(60).Should().Be(1_808);
        SegmentationCreditPricing.PricedFramesFor(16).Should().Be(480);
        SegmentationCreditPricing.PricedFramesFor(600).Should().Be(18_000);

        // ...and it is the same number the hold charges for, at every frame rate the two can disagree on.
        foreach (int seconds in new[] { 1, 16, 60, 600 })
        {
            SegmentationCreditPricing.PricedFramesFor(seconds).Should().Be(
                SegmentationCreditPricing.FrameUnitsFor(seconds) * SegmentationCreditPricing.VendorFrameUnit,
                $"the frame budget and the hold must price {seconds} s identically");
        }
    }

    [Fact]
    public void The_frame_budget_refusal_is_the_authors_to_fix()
    {
        FailureClassifier.ForCode(SegmentationFailureCodes.FrameBudgetExceeded)
            .Should().Be(FailureClass.UserInput,
                "nothing was bought and the fix (shorten the clip, lower its frame rate, raise the cap) is the author's");

        CreditGate.IsRefundable(FailureClass.UserInput).Should().BeFalse(
            "UserInput keeps the consumption, exactly like SEGMENTATION_SOURCE_TOO_LONG");
    }
}
