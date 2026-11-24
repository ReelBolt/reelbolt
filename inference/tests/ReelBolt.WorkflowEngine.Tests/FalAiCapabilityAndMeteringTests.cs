#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.WorkflowEngine.Tests.Pricing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The two obligations that come with adding a capability: the admin boundary and the capability table
/// must agree for every pair, and every unit the platform can meter must be PRICED. The second is the
/// Fish Audio incident repeated — a capability that ships with no rate card records its usage for free,
/// silently, because pricing fails closed and the recorder writes the row anyway.
/// </summary>
public class FalAiCapabilityAndMeteringTests
{
    private static readonly DateTime Effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime At = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // ================================================================ the admin boundary

    [Theory]
    [InlineData(InferenceProviderCapability.Segmentation)]
    [InlineData(InferenceProviderCapability.Depth)]
    [InlineData(InferenceProviderCapability.ImageGeneration)]
    [InlineData(InferenceProviderCapability.ImageEdit)]
    // Appended by the image-to-video wave: fal's video endpoints (FalAiVideoCatalog) are the same kind,
    // the same credential and the same queue contract. FalAiVideoGenerationClientTests holds the video
    // arm's own invariants.
    [InlineData(InferenceProviderCapability.VideoGeneration)]
    public void FalAi_serves_the_analysis_image_and_video_capabilities(InferenceProviderCapability capability)
    {
        InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.FalAi, capability, out string error)
            .Should().BeFalse(error);
    }

    /// <summary>
    /// The trap the coverage test caught when BlackForestLabs landed, asserted directly so the same
    /// hole cannot open twice: Chat, Vision, Transcription and Decision have NO per-kind rule because
    /// IsUnsupportedCombination falls through to ALLOW, so a new kind without its own negative rule is
    /// accepted for all four.
    /// </summary>
    [Theory]
    [InlineData(InferenceProviderCapability.Chat)]
    [InlineData(InferenceProviderCapability.Vision)]
    [InlineData(InferenceProviderCapability.Transcription)]
    [InlineData(InferenceProviderCapability.Decision)]
    [InlineData(InferenceProviderCapability.Embedding)]
    [InlineData(InferenceProviderCapability.SpeechSynthesis)]
    public void FalAi_serves_only_the_capabilities_it_has_an_endpoint_for(InferenceProviderCapability capability)
    {
        InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.FalAi, capability, out string error)
            .Should().BeTrue();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(InferenceProviderKind.AzureOpenAI)]
    [InlineData(InferenceProviderKind.OpenAICompatible)]
    [InlineData(InferenceProviderKind.Anthropic)]
    [InlineData(InferenceProviderKind.Gemini)]
    [InlineData(InferenceProviderKind.DeepSeek)]
    [InlineData(InferenceProviderKind.TypeSafe)]
    [InlineData(InferenceProviderKind.OpenJev)]
    [InlineData(InferenceProviderKind.FishAudio)]
    [InlineData(InferenceProviderKind.MiniMax)]
    [InlineData(InferenceProviderKind.BlackForestLabs)]
    public void Every_other_kind_is_refused_for_segmentation_and_depth(InferenceProviderKind kind)
    {
        InferenceProvidersController.IsUnsupportedCombination(
            kind, InferenceProviderCapability.Segmentation, out string segmentationError).Should().BeTrue();
        segmentationError.Should().NotBeNullOrWhiteSpace();

        InferenceProvidersController.IsUnsupportedCombination(
            kind, InferenceProviderCapability.Depth, out string depthError).Should().BeTrue();
        depthError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Both_analysis_capabilities_produce_a_billable_unit()
    {
        foreach (InferenceProviderCapability capability in new[]
                 { InferenceProviderCapability.Segmentation, InferenceProviderCapability.Depth })
        {
            ProviderCapabilityUsage.ByCapability[capability].Should().NotBeEmpty(
                capability + " is servable but produces no billable unit, so no card could price it");
        }
    }

    // ================================================================ the prices (ported from the pre-catalog rate cards)

    [Fact]
    public async Task A_segmentation_call_is_priced_rather_than_recorded_at_zero()
    {
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync(
            [(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/evf-sam")]);

        PriceQuote quote = await catalog.PriceAsync(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation,
            "fal-ai/evf-sam", UsageKind.SegmentationImages);

        quote.PriceId.Should().NotBeNull("an unpriced unit records the usage at zero cost, silently");
        quote.CostUsd.Should().BeGreaterThan(0m);
        quote.CreditsMillis.Should().BeGreaterThan(0L);
    }

    [Fact]
    public async Task A_depth_call_is_priced_from_the_same_derived_figure()
    {
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync(
            [(InferenceProviderKind.FalAi, InferenceProviderCapability.Depth, "fal-ai/imageutils/depth")]);

        PriceQuote quote = await catalog.PriceAsync(InferenceProviderKind.FalAi, InferenceProviderCapability.Depth,
            "fal-ai/imageutils/depth", UsageKind.DepthImages);

        quote.PriceId.Should().NotBeNull();
        quote.CostUsd.Should().Be(LegacyRateCardSeed.FalAnalysisUsdPerImage);
    }

    /// <summary>
    /// The derivation, asserted so a future edit cannot quietly turn a cited figure into a remembered one: the ported
    /// price must equal fal's published GPU rate divided by an hour, times the stated seconds, and keep the basis that
    /// names where both came from.
    /// </summary>
    [Fact]
    public async Task The_analysis_price_is_derived_from_the_published_gpu_rate_and_says_so()
    {
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync(
            [(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/evf-sam")]);
        Guid capabilityId = catalog.CapabilityId(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/evf-sam");
        await using InferenceApiDbContext db = catalog.NewContext();

        ModelCapabilityPrice row = db.ModelCapabilityPrices.Single(p => p.ModelCapabilityId == capabilityId);

        row.CostUsdPerUnit.Should().Be(LegacyRateCardSeed.FalGpuListUsdPerHour / 3600m * LegacyRateCardSeed.FalSecondsPerOutputImage);
        row.CreditsMillisPerUnitOverride.Should().BeNull("a seeded card is cost-derived, so it picks up the markup");
        row.Notes.Should().NotBeNull();
        row.Notes!.Should().Contain("derived, not invented");
        row.Notes.Should().Contain("fal.ai/pricing", "the source of the rate must be named");
        row.Notes.Should().Contain("$12.99/h");
    }

    [Fact]
    public void Every_fal_endpoint_is_named_in_the_catalogue_and_says_why_it_is_unpriced()
    {
        string[] endpoints =
        [
            "fal-ai/evf-sam", "fal-ai/sam2/image", "fal-ai/birefnet/v2",
            "fal-ai/imageutils/depth", "fal-ai/imageutils/marigold-depth"
        ];

        foreach (string endpoint in endpoints)
        {
            ModelPrice? entry = ModelPriceCatalog.All.FirstOrDefault(m => m.Model == endpoint);
            entry.Should().NotBeNull(endpoint + " must be named, so the gap is visible rather than silent");
            entry!.IsPriced.Should().BeFalse(
                "fal publishes no per-output price for these endpoints; claiming one would be a guess");
            entry.Basis.Should().Contain("no published price");
        }
    }

    // ================================================================ the metering decorator

    private sealed class RecordingRecorder : IUsageRecorder
    {
        public List<UsageRecordRequest> Recorded { get; } = [];

        public ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default)
        {
            Recorded.Add(request);
            return ValueTask.FromResult(0L);
        }
    }

    private sealed class CountingAnalysisClient : IImageAnalysisClient
    {
        public Task<SegmentationResult> SegmentAsync(SegmentationRequest request, CancellationToken ct) =>
            Task.FromResult(new SegmentationResult(new GeneratedImage([1, 2, 3], "image/png"), null));

        public Task<DepthResult> EstimateDepthAsync(DepthRequest request, CancellationToken ct) =>
            Task.FromResult(new DepthResult(new GeneratedImage([1, 2, 3], "image/png")));

        public Task<ImageAnalysisProbeResult> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ImageAnalysisProbeResult(true, null));

        public ImageAnalysisModelCapabilities Describe(string model) => new(model, true, false, false, true, "test");
    }

    private static ResolvedImageAnalysisProvider Provider(InferenceProviderCapability capability, Guid? orgId = null) =>
        new(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, capability, "https://queue.fal.run",
            "fal-ai/evf-sam", "k", 60, orgId, orgId is null);

    [Fact]
    public async Task Segmentation_and_depth_meter_their_own_usage_kinds()
    {
        RecordingRecorder recorder = new();
        MeteringImageAnalysisClient client = new(
            new CountingAnalysisClient(), Provider(InferenceProviderCapability.Segmentation), recorder);

        await client.SegmentAsync(
            new SegmentationRequest("fal-ai/evf-sam", "https://x.example.com/a.png", Prompt: "the person"),
            CancellationToken.None);

        recorder.Recorded.Should().ContainSingle();
        recorder.Recorded[0].Kind.Should().Be(UsageKind.SegmentationImages);
        recorder.Recorded[0].Quantity.Should().Be(1);
        recorder.Recorded[0].ModelName.Should().Be("fal-ai/evf-sam");
    }

    [Fact]
    public async Task A_depth_call_meters_the_depth_kind()
    {
        RecordingRecorder recorder = new();
        MeteringImageAnalysisClient client = new(
            new CountingAnalysisClient(), Provider(InferenceProviderCapability.Depth), recorder);

        await client.EstimateDepthAsync(
            new DepthRequest("fal-ai/imageutils/depth", "https://x.example.com/a.png"), CancellationToken.None);

        recorder.Recorded.Should().ContainSingle().Which.Kind.Should().Be(UsageKind.DepthImages);
    }

    [Fact]
    public async Task A_probe_is_never_metered()
    {
        RecordingRecorder recorder = new();
        MeteringImageAnalysisClient client = new(
            new CountingAnalysisClient(), Provider(InferenceProviderCapability.Segmentation), recorder);

        await client.ProbeAsync(CancellationToken.None);

        recorder.Recorded.Should().BeEmpty(
            "the admin connection test is a no-cost request and must never appear in the ledger");
    }

    [Fact]
    public void An_organization_owned_analysis_row_meters_as_BYO()
    {
        ResolvedImageAnalysisProvider byo = Provider(InferenceProviderCapability.Segmentation, Guid.NewGuid());
        byo.IsPlatformManaged.Should().BeFalse();
        ReelBolt.Shared.Metering.MeteringChatClient.BillingModeOf(byo.OrganizationId)
            .Should().Be(UsageBillingMode.Byo);
    }
}
