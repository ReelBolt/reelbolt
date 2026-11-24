using System;
using System.Collections.Generic;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="VideoGenerationPricing"/> must price fail-closed: an input it cannot price
/// throws, and a returned estimate is always strictly positive (a zero or negative estimate
/// would pass every budget cap and credit the daily ledger).
/// </summary>
public class VideoGenerationPricingTests
{
    private static VideoGenerationCapabilities Caps(params (string Res, decimal Price)[] prices)
    {
        var dict = new Dictionary<string, decimal>();
        foreach (var (res, price) in prices)
        {
            dict[res] = price;
        }

        return new VideoGenerationCapabilities(dict, 4, 15, new[] { "16:9" });
    }

    [Fact]
    public void EstimateClipCostUsd_KnownResolution_ReturnsPriceTimesDuration()
    {
        var cost = VideoGenerationPricing.EstimateClipCostUsd(Caps(("768P", 0.08m)), "768P", 6);

        Assert.Equal(0.48m, cost);
    }

    [Theory]
    [InlineData("1080P")]
    [InlineData("768p")]
    [InlineData("")]
    public void EstimateClipCostUsd_UnknownOrMiscasedResolution_Throws(string resolution)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => VideoGenerationPricing.EstimateClipCostUsd(Caps(("768P", 0.08m)), resolution, 6));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void EstimateClipCostUsd_NonPositiveDuration_Throws(int duration)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => VideoGenerationPricing.EstimateClipCostUsd(Caps(("768P", 0.08m)), "768P", duration));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.08)]
    public void EstimateClipCostUsd_NonPositivePriceInTable_Throws(double price)
    {
        Assert.ThrowsAny<InvalidOperationException>(
            () => VideoGenerationPricing.EstimateClipCostUsd(Caps(("768P", (decimal)price)), "768P", 6));
    }

    [Fact]
    public void EstimateClipCostUsd_NullCapabilities_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => VideoGenerationPricing.EstimateClipCostUsd(null!, "768P", 6));
    }
}
