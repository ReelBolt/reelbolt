#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Tests.Pricing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The two things that must hold for the image arm to be safe to ship: the admin boundary and the
/// capability table must agree for EVERY pair, and a billable image must be PRICED rather than recorded
/// at zero. The second is the Fish Audio incident repeated — a capability that ships with no rate card
/// records its usage for free, silently, because pricing fails closed and the recorder writes the row
/// anyway.
/// </summary>
public class ImageMeteringAndCapabilityRulesTests
{
    private static readonly DateTime Effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime At = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // ================================================================ the admin boundary

    [Theory]
    [InlineData(InferenceProviderKind.MiniMax, InferenceProviderCapability.ImageGeneration)]
    [InlineData(InferenceProviderKind.MiniMax, InferenceProviderCapability.ImageEdit)]
    [InlineData(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ImageGeneration)]
    [InlineData(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ImageEdit)]
    [InlineData(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration)]
    [InlineData(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit)]
    public void The_three_vendors_may_serve_both_image_capabilities(
        InferenceProviderKind kind, InferenceProviderCapability capability)
    {
        InferenceProvidersController.IsUnsupportedCombination(kind, capability, out string error).Should().BeFalse(error);
    }

    [Theory]
    [InlineData(InferenceProviderKind.AzureOpenAI)]
    [InlineData(InferenceProviderKind.Anthropic)]
    [InlineData(InferenceProviderKind.Gemini)]
    [InlineData(InferenceProviderKind.DeepSeek)]
    [InlineData(InferenceProviderKind.TypeSafe)]
    [InlineData(InferenceProviderKind.OpenJev)]
    [InlineData(InferenceProviderKind.FishAudio)]
    public void Every_other_kind_is_refused_for_image_generation(InferenceProviderKind kind)
    {
        // The refusal is what matters, not which rule produced it. MiniMax, OpenJev and FishAudio are
        // rejected by their own "this kind serves exactly these capabilities" rule before the image
        // allowlist is even reached, so their message names THEIR capabilities rather than the one
        // being asked for — asserting on the wording here would pin the rule ORDER, which is not the
        // contract. The allowlist's own wording is asserted separately below.
        InferenceProvidersController.IsUnsupportedCombination(
            kind, InferenceProviderCapability.ImageGeneration, out string error).Should().BeTrue();
        error.Should().NotBeNullOrWhiteSpace();

        InferenceProvidersController.IsUnsupportedCombination(
            kind, InferenceProviderCapability.ImageEdit, out string editError).Should().BeTrue();
        editError.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The five kinds that have no "serves exactly these" rule of their own are refused BY the image
    /// allowlist, and that refusal is the one an admin reads, so it has to name the capability.
    /// </summary>
    [Theory]
    [InlineData(InferenceProviderKind.AzureOpenAI)]
    [InlineData(InferenceProviderKind.Anthropic)]
    [InlineData(InferenceProviderKind.Gemini)]
    [InlineData(InferenceProviderKind.DeepSeek)]
    [InlineData(InferenceProviderKind.TypeSafe)]
    public void The_image_allowlist_refusal_names_the_capability_it_refused(InferenceProviderKind kind)
    {
        InferenceProvidersController.IsUnsupportedCombination(
            kind, InferenceProviderCapability.ImageGeneration, out string error).Should().BeTrue();
        error.Should().Contain("ImageGeneration");

        InferenceProvidersController.IsUnsupportedCombination(
            kind, InferenceProviderCapability.ImageEdit, out string editError).Should().BeTrue();
        editError.Should().Contain("ImageEdit");
    }

    /// <summary>
    /// THE REVERSE RULE. Before the image arm landed, a MiniMax row was pinned to VideoGeneration and no
    /// MiniMax IMAGE row could be created at all — even though MiniMax publishes image-01 at the same host
    /// under the same Bearer credential. This asserts the rule is now a THREE-capability rule and gives the
    /// pair that used to be impossible its own case.
    /// </summary>
    [Fact]
    public void MiniMax_serves_exactly_three_capabilities_and_a_MiniMax_image_row_is_creatable()
    {
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.MiniMax, InferenceProviderCapability.ImageGeneration, out _).Should().BeFalse();
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.MiniMax, InferenceProviderCapability.ImageEdit, out _).Should().BeFalse();
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration, out _).Should().BeFalse();

        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.MiniMax, InferenceProviderCapability.Chat, out string error).Should().BeTrue();
        error.Should().Contain("MiniMax");
    }

    /// <summary>
    /// The trap the coverage test caught rather than a reviewer: BlackForestLabs is a new kind, and Chat,
    /// Vision, Transcription and Decision have no per-kind rule of their own because this method falls
    /// through to ALLOW. Without an explicit negative rule a BlackForestLabs CHAT row was accepted.
    /// </summary>
    [Theory]
    [InlineData(InferenceProviderCapability.Chat)]
    [InlineData(InferenceProviderCapability.Vision)]
    [InlineData(InferenceProviderCapability.Transcription)]
    [InlineData(InferenceProviderCapability.Decision)]
    [InlineData(InferenceProviderCapability.Embedding)]
    [InlineData(InferenceProviderCapability.SpeechSynthesis)]
    [InlineData(InferenceProviderCapability.VideoGeneration)]
    public void BlackForestLabs_serves_images_and_nothing_else(InferenceProviderCapability capability)
    {
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.BlackForestLabs, capability, out string error).Should().BeTrue();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Image_generation_and_editing_are_served_by_at_least_one_kind_and_produce_a_billable_unit()
    {
        foreach (InferenceProviderCapability capability in new[]
                 { InferenceProviderCapability.ImageGeneration, InferenceProviderCapability.ImageEdit })
        {
            ProviderCapabilityUsage.ByCapability[capability].Should().NotBeEmpty(
                capability + " is servable but produces no billable unit, so no card could price it");
        }
    }

    // ================================================================ the prices (ported from the pre-catalog rate cards)

    [Fact]
    public async Task A_MiniMax_image_call_is_priced_from_the_published_per_image_rate()
    {
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync(
            [(InferenceProviderKind.MiniMax, InferenceProviderCapability.ImageGeneration, "image-01")]);

        PriceQuote quote = await catalog.PriceAsync(InferenceProviderKind.MiniMax, InferenceProviderCapability.ImageGeneration,
            "image-01", UsageKind.ImageGenImages, 3);

        quote.PriceId.Should().NotBeNull("an unpriced unit records the usage at zero cost, silently");
        quote.CostUsd.Should().Be(3 * 0.0035m);
        quote.CreditsMillis.Should().Be((long)Math.Round(3 * 0.0035m * 1.2m * 1000m / 0.006m, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task A_BlackForestLabs_edit_is_priced_higher_than_its_own_generation()
    {
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync(
        [
            (InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration, "flux-2-pro"),
            (InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit, "flux-2-pro")
        ]);

        PriceQuote generation = await catalog.PriceAsync(InferenceProviderKind.BlackForestLabs,
            InferenceProviderCapability.ImageGeneration, "flux-2-pro", UsageKind.ImageGenImages);
        PriceQuote edit = await catalog.PriceAsync(InferenceProviderKind.BlackForestLabs,
            InferenceProviderCapability.ImageEdit, "flux-2-pro", UsageKind.ImageEditImages);

        generation.CostUsd.Should().Be(0.03m);
        edit.CostUsd.Should().Be(0.045m,
            "BFL publishes $0.03 text-to-image against $0.045 for editing, which is why the two capabilities " +
            "meter different usage kinds rather than one shared image unit");
    }

    [Fact]
    public async Task An_uncatalogued_image_model_still_resolves_to_the_derived_wildcard_rather_than_to_nothing()
    {
        // A model nobody has catalogued: the upgrade ports the old wildcard onto its capability.
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync(
            [(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageGeneration, "flux-9-unknown")]);

        PriceQuote quote = await catalog.PriceAsync(InferenceProviderKind.BlackForestLabs,
            InferenceProviderCapability.ImageGeneration, "flux-9-unknown", UsageKind.ImageGenImages);

        quote.PriceId.Should().NotBeNull();
        quote.CostUsd.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task Every_ported_image_wildcard_cites_the_published_price_it_was_derived_from()
    {
        List<(InferenceProviderKind, InferenceProviderCapability, string)> capabilities = [.. ProviderCapabilityUsage.RequiredCards()
            .Where(p => p.UsageKind is UsageKind.ImageGenImages or UsageKind.ImageEditImages)
            .Select(p => (p.Kind, p.UsageKind == UsageKind.ImageGenImages
                ? InferenceProviderCapability.ImageGeneration
                : InferenceProviderCapability.ImageEdit, "an-uncatalogued-model"))];
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync(capabilities);
        await using ReelBolt.Inference.Api.Data.InferenceApiDbContext db = catalog.NewContext();

        foreach ((InferenceProviderKind kind, InferenceProviderCapability capability, string code) in capabilities)
        {
            Guid id = catalog.CapabilityId(kind, capability, code);
            ModelCapabilityPrice ported = db.ModelCapabilityPrices.Single(p => p.ModelCapabilityId == id);

            ported.CostUsdPerUnit.Should().BeGreaterThan(0m, "a zero wildcard would make every uncatalogued model free");
            ported.Notes.Should().Contain("derived, not invented");
            ported.Notes.Should().Contain("ModelPriceCatalog",
                "the wildcard must name the published figure it was derived from, never a guess");
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

    private sealed class CountingImageClient : IImageClient
    {
        private readonly int _count;
        public CountingImageClient(int count) => _count = count;

        private GeneratedImageSet Set() => new(
            [.. Enumerable.Range(0, _count).Select(_ => new GeneratedImage([1, 2, 3], "image/png"))]);

        public Task<GeneratedImageSet> GenerateAsync(ReelBolt.Shared.Inference.ImageGenerationRequest r, CancellationToken ct) =>
            Task.FromResult(Set());

        public Task<GeneratedImageSet> EditAsync(ImageEditRequest r, CancellationToken ct) => Task.FromResult(Set());

        public Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest r, CancellationToken ct) => Task.FromResult(Set());

        public Task<GeneratedImageSet> TransformAsync(ImageTransformRequest r, CancellationToken ct) => Task.FromResult(Set());

        public Task<ImageProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new ImageProbeResult(true, null));

        public ImageModelCapabilities Describe(string model) => new(model, true, true, 1, 100, "test");
    }

    private static ResolvedImageProvider Provider(InferenceProviderCapability capability, Guid? orgId = null) =>
        new(Guid.NewGuid(), "img", InferenceProviderKind.MiniMax, capability, "https://api.minimax.io",
            "image-01", "k", 60, orgId, orgId is null);

    [Fact]
    public async Task A_batch_meters_one_unit_per_returned_image_not_one_per_call()
    {
        RecordingRecorder recorder = new();
        MeteringImageClient client = new(
            new CountingImageClient(3), Provider(InferenceProviderCapability.ImageGeneration), recorder);

        await client.GenerateAsync(
            new ReelBolt.Shared.Inference.ImageGenerationRequest("image-01", "x", Count: 3), CancellationToken.None);

        recorder.Recorded.Should().ContainSingle();
        recorder.Recorded[0].Kind.Should().Be(UsageKind.ImageGenImages);
        recorder.Recorded[0].Quantity.Should().Be(3,
            "MiniMax bills $0.0035 per image, so three images are three units — one unit per call would " +
            "under-charge a batch by a factor of n");
    }

    [Fact]
    public async Task Editing_and_inpainting_meter_the_edit_kind_rather_than_the_generation_kind()
    {
        RecordingRecorder recorder = new();
        MeteringImageClient client = new(
            new CountingImageClient(1), Provider(InferenceProviderCapability.ImageEdit), recorder);

        await client.EditAsync(new ImageEditRequest("m", "x", "https://example.com/a.png"), CancellationToken.None);
        await client.InpaintAsync(
            new ImageInpaintRequest("m", "x", "https://example.com/a.png", [1, 2, 3]), CancellationToken.None);

        recorder.Recorded.Should().HaveCount(2);
        recorder.Recorded.Should().OnlyContain(r => r.Kind == UsageKind.ImageEditImages);
        recorder.Recorded.Should().OnlyContain(r => r.ModelName == "image-01");
    }

    [Fact]
    public async Task A_probe_is_never_metered()
    {
        RecordingRecorder recorder = new();
        MeteringImageClient client = new(
            new CountingImageClient(1), Provider(InferenceProviderCapability.ImageGeneration), recorder);

        await client.ProbeAsync(CancellationToken.None);

        recorder.Recorded.Should().BeEmpty(
            "the admin connection test is a no-cost request and must never appear in the ledger");
    }

    [Fact]
    public void An_organization_owned_image_row_meters_as_BYO()
    {
        ResolvedImageProvider byo = Provider(InferenceProviderCapability.ImageGeneration, Guid.NewGuid());
        byo.IsPlatformManaged.Should().BeFalse();
        // BillingModeOf is the one place that decides this; the decorator passes OrganizationId through.
        ReelBolt.Shared.Metering.MeteringChatClient.BillingModeOf(byo.OrganizationId)
            .Should().Be(UsageBillingMode.Byo);
    }
}
