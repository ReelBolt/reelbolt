#nullable enable
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The <see cref="InferenceProviderCapability.VideoSegmentation"/> capability, its fal-only admin
/// allowlist and the vendor's 16-frame price arithmetic. Offline: no queue, no key.
/// </summary>
public class VideoSegmentationCapabilityAndPricingTests
{
    private static readonly DateTime Effective = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FalAi_is_the_only_kind_the_admin_boundary_accepts_for_video_segmentation()
    {
        foreach (InferenceProviderKind kind in Enum.GetValues<InferenceProviderKind>())
        {
            bool refused = InferenceProvidersController.IsUnsupportedCombination(
                kind, InferenceProviderCapability.VideoSegmentation, out string error);

            if (kind == InferenceProviderKind.FalAi)
            {
                refused.Should().BeFalse("{0} hosts fal-ai/sam-3-1/video-rle", kind);
            }
            else
            {
                refused.Should().BeTrue("{0} has neither a video-url queue nor a read schema for this endpoint", kind);
                error.Should().NotBeNullOrWhiteSpace("a refusal must explain itself");
            }
        }

        // Kinds with their own negative rule answer first; the capability-level positive allowlist is
        // what a kind with no rule of its own reaches, and its message must name the endpoint.
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.AzureOpenAI, InferenceProviderCapability.VideoSegmentation, out string allowlistError)
            .Should().BeTrue();
        allowlistError.Should().Contain("VIDEO segmentation");
        allowlistError.Should().Contain("sam-3-1/video-rle");
        allowlistError.Should().Contain("'FalAi'");
    }

    [Fact]
    public void The_capability_table_matches_the_admin_boundary()
    {
        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.FalAi]
            .Should().Contain(InferenceProviderCapability.VideoSegmentation);

        foreach ((InferenceProviderKind kind, InferenceProviderCapability[] capabilities) in ProviderCapabilityUsage.ServableByKind)
        {
            bool servable = capabilities.Contains(InferenceProviderCapability.VideoSegmentation);
            bool refused = InferenceProvidersController.IsUnsupportedCombination(
                kind, InferenceProviderCapability.VideoSegmentation, out _);
            servable.Should().Be(!refused, "{0} must agree in both tables", kind);
        }
    }

    [Fact]
    public void Video_segmentation_meters_frames_of_input_not_outputs()
    {
        ProviderCapabilityUsage.ByCapability[InferenceProviderCapability.VideoSegmentation]
            .Should().Equal(UsageKind.VideoSegmentationFrames);

        ProviderCapabilityUsage.RequiredCards()
            .Should().Contain((InferenceProviderKind.FalAi, UsageKind.VideoSegmentationFrames),
                "the coverage contract must demand a card or every request records as free");
    }

    [Fact]
    public void The_catalog_prices_the_endpoint_per_frame_with_the_vendor_block_divided_in()
    {
        ModelPrice price = ModelPriceCatalog.All.Single(m =>
            m.ProviderKind == InferenceProviderKind.FalAi
            && m.Capability == InferenceProviderCapability.VideoSegmentation
            && m.Model == ModelPriceCatalog.FalVideoSegmentationModel);

        price.Capability.Should().Be(InferenceProviderCapability.VideoSegmentation);
        price.UsdPerUnit.Should().ContainKey(UsageKind.VideoSegmentationFrames);
        price.UsdPerUnit[UsageKind.VideoSegmentationFrames].Should().Be(0.01m / 16m,
            "the card is the vendor's $0.01 per 16 frames divided by its block size");
        price.Basis.Should().Contain("$0.01 per 16 frames of video input");
        price.Basis.Should().Contain("2026-10-18");
        price.Basis.Should().Contain("sam-3-1/video-rle");
    }

    [Theory]
    [InlineData(120, 8, 128, 0.08)]
    [InlineData(1, 1, 16, 0.01)]
    [InlineData(16, 1, 16, 0.01)]
    [InlineData(17, 2, 32, 0.02)]
    [InlineData(0, 0, 0, 0.0)]
    public void Rounds_up_to_whole_sixteen_frame_blocks(
        int frames, int expectedUnits, int expectedBillable, double expectedUsd)
    {
        VideoSegmentationBilling.UnitsFor(frames).Should().Be(expectedUnits);
        VideoSegmentationBilling.BillableFrames(frames).Should().Be(expectedBillable);
        VideoSegmentationBilling.UsdFor(frames).Should().Be((decimal)expectedUsd);
    }

    [Fact]
    public void The_catalogs_per_frame_unit_is_what_both_the_bill_and_the_credit_hold_use()
    {
        // One price source: the model price catalog row. The bill (VideoSegmentationBilling) and the
        // credit hold (SegmentationCreditPricing) must both read that row, never a rate card of their own.
        SegmentationCreditPricing.UsdPerFrameUnit.Should().Be(0.01m,
            "the credit hold prices the vendor's 16-frame block as its unit");

        ModelPrice price = ModelPriceCatalog.All.Single(m =>
            m.ProviderKind == InferenceProviderKind.FalAi
            && m.Capability == InferenceProviderCapability.VideoSegmentation
            && m.Model == ModelPriceCatalog.FalVideoSegmentationModel);
        price.UsdPerUnit[UsageKind.VideoSegmentationFrames].Should().Be(0.01m / 16m,
            "the bill prices one frame, i.e. the vendor's 16-frame block divided in");

        // 120 frames = 8 whole blocks = $0.08, the same arithmetic the hold prices before the call.
        VideoSegmentationBilling.UnitsFor(120).Should().Be(8);
        VideoSegmentationBilling.UsdFor(120).Should().Be(0.08m);
        SegmentationCreditPricing.FrameUnitMillis.Should().BeGreaterThan(0,
            "the hold must reserve something for a priced capability");
    }
}
