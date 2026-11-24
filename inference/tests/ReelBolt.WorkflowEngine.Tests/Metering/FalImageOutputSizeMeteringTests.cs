#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Metering;

/// <summary>
/// T-013 (saas-quality-followups): a per-megapixel fal image endpoint (no billing tier) is metered at the size of the
/// picture it DELIVERED, as the <c>{n}MP</c> price variant the seed keys its rows on, so a 640x352 fill bills the 1 MP
/// row and not the 4 MP figure the variant-less row carries. Unknown size keeps today's fail-closed variant-less row.
/// Real <see cref="FalAiImageClient"/> over a stubbed queue, real <see cref="MeteringImageClient"/>, real
/// <see cref="ModelPriceService"/> over the committed <c>model-catalog.seed.json</c> rows: no price is restated here.
/// </summary>
public sealed class FalImageOutputSizeMeteringTests
{
    private static readonly DateTime At = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ the ticket's cases

    [Fact]
    public async Task Fill_of_a_640x352_picture_meters_the_1MP_row()
    {
        Metered run = await RunAsync(InferenceProviderCapability.ImageFill, Png(640, 352),
            c => c.InpaintAsync(new ImageInpaintRequest("", "a red balloon", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png"), default));

        run.Recorded.ModelName.Should().Be("fal-ai/flux-pro/v1/fill");
        run.Recorded.Variant.Should().Be("1MP", "640x352 = 0.23 MP, rounded up to 1 MP");
        (await PriceAsync(run, InferenceProviderCapability.ImageFill)).CostUsd
            .Should().Be(SeedCost("fal-ai/flux-pro/v1/fill", "ImageFill", "1MP")).And.Be(0.05m);
    }

    [Fact]
    public async Task Upscale_with_a_4MP_output_meters_the_4MP_row()
    {
        Metered run = await RunAsync(InferenceProviderCapability.ImageUpscale, Png(2560, 1440),
            c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Upscale, "", "https://files.example.com/a.png"), default),
            singleImage: true);

        run.Recorded.ModelName.Should().Be("fal-ai/seedvr/upscale/image");
        run.Recorded.Variant.Should().Be("4MP", "2560x1440 = 3.69 MP of output, rounded up to 4 MP");
        (await PriceAsync(run, InferenceProviderCapability.ImageUpscale)).CostUsd
            .Should().Be(SeedCost("fal-ai/seedvr/upscale/image", "ImageUpscale", "4MP"));
    }

    [Fact]
    public async Task An_unreadable_size_with_no_output_ceiling_is_still_billed_at_the_largest_row_and_warns()
    {
        // F3: the vendor answered, so the call is billed: never thrown, never 0. With no measured size and no documented
        // ceiling the variant-less row bills, which the seed sets at or above every sized row.
        Metered run = await RunAsync(InferenceProviderCapability.ImageFill, [0x01, 0x02, 0x03],
            c => c.InpaintAsync(new ImageInpaintRequest("", "a red balloon", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png"), default));

        run.Recorded.Variant.Should().BeNull();
        run.Recorded.Quantity.Should().Be(1);
        (await PriceAsync(run, InferenceProviderCapability.ImageFill)).CostUsd
            .Should().Be(SeedRows("fal-ai/flux-pro/v1/fill", "ImageFill").Max(r => r.CostUsdPerUnit));
        run.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("fal-ai/flux-pro/v1/fill"));
    }

    [Theory]
    [InlineData("fal-ai/flux-pro/v1/fill", "ImageFill")]
    [InlineData("fal-ai/flux-pro/v1/erase", "ImageErase")]
    [InlineData("fal-ai/flux-2-pro/outpaint", "ImageExpand")]
    [InlineData("fal-ai/iclight-v2", "ImageRelight")]
    [InlineData("fal-ai/qwen-image-edit-2511", "ImageEdit")]
    [InlineData("fal-ai/seedvr/upscale/image", "ImageUpscale")]
    public void The_variant_less_row_of_every_size_priced_endpoint_is_its_dearest_row(string model, string capability)
    {
        // What an unmeasurable size falls back to must never be below a sized row.
        List<SeedPriceRow> rows = [.. SeedRows(model, capability)];
        rows.Single(r => r.Variant is null).CostUsdPerUnit.Should().Be(rows.Max(r => r.CostUsdPerUnit));
    }

    [Fact]
    public async Task An_unreadable_size_on_an_endpoint_with_a_documented_ceiling_bills_that_ceiling()
    {
        // flux-2-pro/outpaint documents at most 4,194,304 output pixels (2048 x 2048): exactly 4 binary MP, and the
        // input it also bills (never larger than the output, size not stated here) is at most the same 4 MP.
        Metered run = await RunAsync(InferenceProviderCapability.ImageExpand, [0x01, 0x02, 0x03],
            c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint",
                "https://files.example.com/a.png", Params: Params("{\"expand_right\":256}")), default));

        run.Recorded.Variant.Should().Be("4MP", "max(4 MP out, 4 MP in): 2048 x 2048 is exactly 4 binary MP, and the seed's n MP row prices n in and n out");
        (await PriceAsync(run, InferenceProviderCapability.ImageExpand)).CostUsd
            .Should().Be(SeedCost("fal-ai/flux-2-pro/outpaint", "ImageExpand", "4MP"));
        run.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("fal-ai/flux-2-pro/outpaint"));
    }

    // ------------------------------------------------------------------ an implausible vendor size is clamped

    [Fact]
    public async Task An_implausible_size_is_clamped_to_four_times_the_expected_size_and_warns()
    {
        // 20000 x 20000 = 400 MP back from a fill expected at 1 MP: billed at 4 x the expected size, never 400 MP.
        Metered run = await RunAsync(InferenceProviderCapability.ImageFill, Png(20000, 20000),
            c => c.InpaintAsync(new ImageInpaintRequest("", "a red balloon", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png")
            { ExpectedOutputPixels = 1_000_000 }, default));

        run.Recorded.Variant.Should().Be("4MP");
        run.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("clamped"));
    }

    [Fact]
    public async Task An_implausible_size_with_no_expected_size_is_clamped_to_the_endpoints_fallback_ceiling()
    {
        // No documented maximum and no expected size: fill clamps to the 64 MP this server decodes at most.
        Metered run = await RunAsync(InferenceProviderCapability.ImageFill, Png(20000, 20000),
            c => c.InpaintAsync(new ImageInpaintRequest("", "a red balloon", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png"), default));

        run.Recorded.Variant.Should().Be("62MP", "64,000,000 px = 61.04 binary MP, rounded up");
        run.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("clamped"));
    }

    [Fact]
    public async Task An_outpaint_above_its_documented_maximum_is_clamped_to_it()
    {
        Metered run = await RunAsync(InferenceProviderCapability.ImageExpand, Png(4000, 4000),
            c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint",
                "https://files.example.com/a.png", Params: Params("{\"expand_right\":256}")), default));

        run.Recorded.Variant.Should().Be("4MP", "output clamped to 2048 x 2048 = exactly 4 binary MP; its input is at most that");
        run.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("clamped"));
    }

    // ------------------------------------------------------------------ above the largest row: per megapixel

    [Fact]
    public async Task Fill_above_4MP_bills_per_megapixel_at_the_rate_the_4MP_row_implies()
    {
        // 4000x2500 = 10 MP. fal: "$0.05 per megapixel ... rounding up to the nearest megapixel" - no cap stated.
        Metered run = await RunAsync(InferenceProviderCapability.ImageFill, Png(4000, 2500),
            c => c.InpaintAsync(new ImageInpaintRequest("", "a red balloon", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png"), default));

        run.Recorded.Variant.Should().Be("10MP");
        PriceQuote quote = await PriceAsync(run, InferenceProviderCapability.ImageFill);
        quote.CostUsd.Should().Be(SeedCost("fal-ai/flux-pro/v1/fill", "ImageFill", "4MP") * 10 / 4).And.Be(0.50m);
        quote.CostUsd.Should().BeGreaterThan(SeedCost("fal-ai/flux-pro/v1/fill", "ImageFill", null), "never the 4 MP figure");
    }

    [Fact]
    public async Task Erase_above_4MP_bills_at_least_the_published_formula()
    {
        // 10 MP: "$0.03 for the first megapixel generated, then $0.004 for each subsequent megapixel. Each reference
        // megapixel also costs $0.004 set at 3 MP minimum" = 0.03 + 9 x 0.004 + 10 x 0.004 = $0.106.
        Metered run = await RunAsync(InferenceProviderCapability.ImageErase, Png(4000, 2500),
            c => c.InpaintAsync(new ImageInpaintRequest("fal-ai/flux-pro/v1/erase", "", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png"), default));

        (await PriceAsync(run, InferenceProviderCapability.ImageErase)).CostUsd
            .Should().Be(SeedCost("fal-ai/flux-pro/v1/erase", "ImageErase", "4MP") * 10 / 4)
            .And.BeGreaterThanOrEqualTo(0.106m);
    }

    [Fact]
    public async Task Upscale_to_44MP_bills_per_megapixel_of_output()
    {
        // 10080x4320 = 43,545,600 px = 41.53 binary MP -> 42 MP x $0.001 = $0.042 (linear per MP).
        Metered run = await RunAsync(InferenceProviderCapability.ImageUpscale, Png(10080, 4320),
            c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Upscale, "", "https://files.example.com/a.png"), default),
            singleImage: true);

        run.Recorded.Variant.Should().Be("42MP");
        (await PriceAsync(run, InferenceProviderCapability.ImageUpscale)).CostUsd
            .Should().Be(SeedCost("fal-ai/seedvr/upscale/image", "ImageUpscale", "1MP") * 42).And.Be(0.042m);
    }

    [Fact]
    public async Task The_measured_picture_wins_over_a_vendor_stated_size()
    {
        Metered run = await RunAsync(InferenceProviderCapability.ImageFill, Png(2048, 1536),
            c => c.InpaintAsync(new ImageInpaintRequest("", "a red balloon", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png"), default),
            statedWidth: 640, statedHeight: 352);

        run.Recorded.Variant.Should().Be("3MP", "the delivered bytes are 2048 x 1536 = exactly 3 binary MP whatever the response says");
    }

    // ------------------------------------------------------------------ every other per-megapixel fal job

    [Fact]
    public async Task Erase_on_flux_pro_erase_meters_its_measured_size()
    {
        Metered run = await RunAsync(InferenceProviderCapability.ImageErase, Png(1280, 720),
            c => c.InpaintAsync(new ImageInpaintRequest("fal-ai/flux-pro/v1/erase", "", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png"), default));

        run.Recorded.Variant.Should().Be("1MP");
        (await PriceAsync(run, InferenceProviderCapability.ImageErase)).CostUsd
            .Should().Be(SeedCost("fal-ai/flux-pro/v1/erase", "ImageErase", "1MP"));
    }

    [Fact]
    public async Task Expand_on_flux2_outpaint_counts_its_output_and_its_input()
    {
        // No geometry on the request: the input (never larger than the output) is counted at the output's size.
        Metered run = await RunAsync(InferenceProviderCapability.ImageExpand, Png(1024, 768),
            c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint",
                "https://files.example.com/a.png", Params: Params("{\"expand_right\":256}")), default));

        run.Recorded.Variant.Should().Be("2MP", "max(1, 1) MP, at least 2 because the 1 MP row prices no input");
        (await PriceAsync(run, InferenceProviderCapability.ImageExpand)).CostUsd
            .Should().Be(SeedCost("fal-ai/flux-2-pro/outpaint", "ImageExpand", "4MP"));
    }

    /// <summary>fal: "$0.03 for the first megapixel of output, plus $0.015 per extra megapixel of input and output, rounded up".</summary>
    private static decimal OutpaintFormula(long outputPixels, long inputPixels)
    {
        long output = OutputSizePricing.Megapixels(outputPixels);
        long input = OutputSizePricing.Megapixels(inputPixels);
        return 0.03m + 0.015m * (output - 1) + 0.015m * input;
    }

    [Theory]
    [InlineData(1000, 1000, 744, 1000)]  // 1 MP out, 0.74 MP in: $0.045 at the vendor
    [InlineData(1600, 1250, 1200, 1250)] // 2 MP out, 1.5 MP in: $0.075 at the vendor
    public async Task Outpaint_at_1MP_and_2MP_never_bills_below_the_vendor_formula(int width, int height, int inWidth, int inHeight)
    {
        Metered run = await RunAsync(InferenceProviderCapability.ImageExpand, Png(width, height),
            c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint",
                "https://files.example.com/a.png", Expand: new ImageExpandGeometry(width, height, 0, 0, inWidth, inHeight)), default));

        decimal cost = (await PriceAsync(run, InferenceProviderCapability.ImageExpand)).CostUsd;
        cost.Should().BeGreaterThanOrEqualTo(OutpaintFormula((long)width * height, (long)inWidth * inHeight));
        cost.Should().BeGreaterThan(SeedCost("fal-ai/flux-2-pro/outpaint", "ImageExpand", "1MP"), "never the $0.03 output-only floor");
    }

    /// <summary>What the outpaint rule bills for an output and input of these sizes, on the committed seed's rows.</summary>
    private static decimal OutpaintBilled(long outputPixels, long inputPixels)
    {
        Guid id = Guid.NewGuid();
        ModelPriceSnapshot snapshot = SeedPrices.Snapshot(id, InferenceProviderKind.FalAi, "fal-ai/flux-2-pro/outpaint",
            InferenceProviderCapability.ImageExpand, UsageKind.ImageEditImages);
        string variant = OutputSizePricing.InputAndOutputVariant(outputPixels, inputPixels)!;
        return SeedPrices.Price(snapshot, new PriceRequest(id, id, InferenceProviderKind.FalAi, InferenceProviderCapability.ImageExpand,
            "fal-ai/flux-2-pro/outpaint", UsageKind.ImageEditImages, variant, UsageBillingMode.Platform, 1m)).CostUsd;
    }

    [Theory]
    [InlineData(1_000_000, 744_000)]
    [InlineData(2_000_000, 1_500_000)]
    [InlineData(4_000_000, 4_000_000)]
    [InlineData(6_000_000, 5_800_000)]   // 6 MP out, 5.8 MP in
    [InlineData(10_000_000, 9_750_000)]  // 10 MP out, 9.75 MP in
    [InlineData(10_000_000, 10_000_000)]
    public void The_outpaint_rule_never_bills_below_the_vendor_formula(long output, long input)
    {
        // The pure rule, past the 2048 x 2048 clamp a real call would get: max(in, out) MP, at least 2 at <= 1 MP out.
        OutpaintBilled(output, input).Should().BeGreaterThanOrEqualTo(OutpaintFormula(output, input));
    }

    [Fact]
    public void A_10MP_outpaint_bills_at_most_1_1_times_the_vendor_formula()
    {
        // Tightness pin: 10 MP out, 10 MP in costs 0.03 + 9 x 0.015 + 10 x 0.015 = $0.315 at the vendor; the rule bills
        // 10/4 of the 4 MP row (0.135) = $0.3375, 1.071x. (Below 4 MP the rows allow nothing tighter than the 4 MP row.)
        decimal formula = OutpaintFormula(10_000_000, 10_000_000);
        OutpaintBilled(10_000_000, 10_000_000).Should().BeGreaterThanOrEqualTo(formula).And.BeLessThanOrEqualTo(formula * 1.1m);
    }

    [Fact]
    public async Task Relight_on_iclight_meters_its_measured_size()
    {
        Metered run = await RunAsync(InferenceProviderCapability.ImageRelight, Png(1920, 1920),
            c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Relight, "fal-ai/iclight-v2",
                "https://files.example.com/a.png", Prompt: "sunset light"), default));

        run.Recorded.Variant.Should().Be("4MP", "1920x1920 = 3.69 MP");
        (await PriceAsync(run, InferenceProviderCapability.ImageRelight)).CostUsd
            .Should().Be(SeedCost("fal-ai/iclight-v2", "ImageRelight", "4MP"));
    }

    [Fact]
    public async Task Instruct_on_qwen_edit_meters_its_measured_size()
    {
        Metered run = await RunAsync(InferenceProviderCapability.ImageEdit, Png(800, 600),
            c => c.EditAsync(new ImageEditRequest("fal-ai/qwen-image-edit-2511", "make it blue", "https://files.example.com/a.png"), default));

        run.Recorded.Variant.Should().Be("1MP");
        (await PriceAsync(run, InferenceProviderCapability.ImageEdit)).CostUsd
            .Should().Be(SeedCost("fal-ai/qwen-image-edit-2511", "ImageEdit", "1MP"));
    }

    [Fact]
    public async Task A_size_between_seeded_rows_of_a_linear_endpoint_bills_n_times_its_per_MP_rate()
    {
        // 1600x1200 = 1.92 MP -> "2MP"; seedvr is linear ("$0.001 per megapixel"), so 2 x the 1MP row, not the 4MP row.
        Metered run = await RunAsync(InferenceProviderCapability.ImageUpscale, Png(1600, 1200),
            c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Upscale, "", "https://files.example.com/a.png"), default),
            singleImage: true);

        run.Recorded.Variant.Should().Be("2MP");
        (await PriceAsync(run, InferenceProviderCapability.ImageUpscale)).CostUsd
            .Should().Be(SeedCost("fal-ai/seedvr/upscale/image", "ImageUpscale", "1MP") * 2);
    }

    [Theory]
    [InlineData(1000, 1000, "1MP", "0.05")]    // 1,000,000 px  = 0.95 binary MP
    [InlineData(1024, 1024, "1MP", "0.05")]    // 1,048,576 px  = exactly 1 binary MP
    [InlineData(1025, 1024, "2MP", "0.10")]    // 1,049,600 px  = 1.001, rounded up
    [InlineData(1920, 1080, "2MP", "0.10")]    // 2,073,600 px  = 1.98
    [InlineData(2048, 2048, "4MP", "0.20")]    // 4,194,304 px  = exactly 4
    [InlineData(4000, 2500, "10MP", "0.50")]   // 10,000,000 px = 9.54
    [InlineData(4096, 2560, "10MP", "0.50")]   // 10,485,760 px = exactly 10
    [InlineData(4097, 2560, "11MP", "0.55")]   // 10,488,320 px = 10.002
    public async Task A_fill_bills_its_rounded_up_megapixels_at_five_cents_each(int width, int height, string variant, string usd)
    {
        // fal: "$0.05 per megapixel. Images are billed by rounding up to the nearest megapixel." Linear, so n x $0.05,
        // with 1 MP = 1,048,576 pixels (binary megapixels).
        Metered run = await RunAsync(InferenceProviderCapability.ImageFill, Png(width, height),
            c => c.InpaintAsync(new ImageInpaintRequest("", "a red balloon", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png"), default));

        run.Recorded.Variant.Should().Be(variant);
        (await PriceAsync(run, InferenceProviderCapability.ImageFill)).CostUsd
            .Should().Be(decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_non_linear_endpoint_between_rows_still_bills_the_next_row_up()
    {
        // erase is not linear ("$0.03 for the first megapixel ... then $0.004"): 2 MP bills the 4MP row, never 2 x 1MP.
        Metered run = await RunAsync(InferenceProviderCapability.ImageErase, Png(2000, 1000),
            c => c.InpaintAsync(new ImageInpaintRequest("fal-ai/flux-pro/v1/erase", "", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png"), default));

        (await PriceAsync(run, InferenceProviderCapability.ImageErase)).CostUsd
            .Should().Be(SeedCost("fal-ai/flux-pro/v1/erase", "ImageErase", "4MP"));
    }

    [Fact]
    public async Task Qwen_edit_with_sized_reference_pictures_bills_output_plus_each_reference_per_megapixel()
    {
        // F5: 800x600 output = 1 MP, plus each reference's binary megapixels, at qwen's flat per-MP rate.
        Metered run = await RunAsync(InferenceProviderCapability.ImageEdit, Png(800, 600),
            c => c.EditAsync(new ImageEditRequest("fal-ai/qwen-image-edit-2511", "make it blue", "https://files.example.com/a.png",
                ReferenceImageUrls: ["https://files.example.com/b.png", "https://files.example.com/c.png"])
            {
                ReferenceImagePixels = [3_000_000, 2_000_001]
            }, default));

        run.Recorded.Variant.Should().Be("6MP", "1 MP out + 3,000,000 px (2.86 -> 3) + 2,000,001 px (1.91 -> 2), binary MP");
        (await PriceAsync(run, InferenceProviderCapability.ImageEdit)).CostUsd
            .Should().Be(SeedCost("fal-ai/qwen-image-edit-2511", "ImageEdit", "1MP") * 6)
            .And.BeGreaterThan(SeedCost("fal-ai/qwen-image-edit-2511", "ImageEdit", null), "never the flat variant-less figure");
    }

    [Fact]
    public async Task Qwen_edit_with_unsized_reference_pictures_keeps_the_variant_less_row_and_warns()
    {
        Metered run = await RunAsync(InferenceProviderCapability.ImageEdit, Png(800, 600),
            c => c.EditAsync(new ImageEditRequest("fal-ai/qwen-image-edit-2511", "make it blue", "https://files.example.com/a.png",
                ReferenceImageUrls: ["https://files.example.com/b.png"]), default));

        run.Recorded.Variant.Should().BeNull();
        run.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("fal-ai/qwen-image-edit-2511"));
    }

    [Fact]
    public async Task A_tier_priced_endpoint_keeps_its_tier_and_never_gets_a_size()
    {
        Metered run = await RunAsync(InferenceProviderCapability.ImageErase, Png(640, 352),
            c => c.InpaintAsync(new ImageInpaintRequest("", "", "https://files.example.com/a.png", [],
                MaskUrl: "https://files.example.com/m.png"), default));

        run.Recorded.ModelName.Should().Be("fal-ai/object-removal/mask");
        run.Recorded.Variant.Should().Be("best_quality", "the tier is the price; the vendor default tier ran");
    }

    // ------------------------------------------------------------------ BFL FLUX.2 (same meter path)

    [Fact]
    public async Task Bfl_flux2_generation_meters_its_measured_size()
    {
        Metered run = await RunBflAsync("flux-2-pro", InferenceProviderCapability.ImageGeneration, Png(1024, 1024),
            c => c.GenerateAsync(new ReelBolt.Shared.Inference.ImageGenerationRequest("flux-2-pro", "a cover", Width: 1024, Height: 1024), default));

        run.Recorded.ModelName.Should().Be("flux-2-pro");
        run.Recorded.Variant.Should().Be("1MP", "1024x1024 = exactly 1 binary MP");
        (await PriceAsync(run, InferenceProviderCapability.ImageGeneration, InferenceProviderKind.BlackForestLabs, UsageKind.ImageGenImages))
            .CostUsd.Should().Be(SeedCost("flux-2-pro", "ImageGeneration", "1MP", InferenceProviderKind.BlackForestLabs, UsageKind.ImageGenImages));
    }

    [Fact]
    public async Task Bfl_flux2_generation_of_unknown_size_bills_its_documented_2048x2048_ceiling()
    {
        Metered run = await RunBflAsync("flux-2-pro", InferenceProviderCapability.ImageGeneration, [0x01, 0x02, 0x03],
            c => c.GenerateAsync(new ReelBolt.Shared.Inference.ImageGenerationRequest("flux-2-pro", "a cover"), default));

        run.Recorded.Variant.Should().Be("4MP", "\"up to 4MP (e.g., 2048x2048)\": 2048 x 2048 = exactly 4 binary MP");
    }

    [Fact]
    public async Task Bfl_flux2_edit_keeps_the_variant_less_row_because_reference_megapixels_are_billed_too()
    {
        // "+$0.015 per reference MP": the source picture's size is not the output's, so output size alone could under-bill.
        Metered run = await RunBflAsync("flux-2-pro", InferenceProviderCapability.ImageEdit, Png(1024, 1024),
            c => c.EditAsync(new ImageEditRequest("flux-2-pro", "make it blue", "https://files.example.com/a.png"), default));

        run.Recorded.Variant.Should().BeNull();
    }

    [Fact]
    public async Task Bfl_flux2_generation_with_a_reference_picture_keeps_the_variant_less_row()
    {
        Metered run = await RunBflAsync("flux-2-pro", InferenceProviderCapability.ImageGeneration, Png(1024, 1024),
            c => c.GenerateAsync(new ReelBolt.Shared.Inference.ImageGenerationRequest("flux-2-pro", "a cover",
                ReferenceImageUrl: "https://files.example.com/a.png"), default));

        run.Recorded.Variant.Should().BeNull();
    }

    [Fact]
    public async Task Bfl_models_outside_flux2_are_not_size_metered()
    {
        Metered run = await RunBflAsync("flux-pro-1.1", InferenceProviderCapability.ImageGeneration, Png(1024, 1024),
            c => c.GenerateAsync(new ReelBolt.Shared.Inference.ImageGenerationRequest("flux-pro-1.1", "a cover"), default));

        run.Recorded.Variant.Should().BeNull("FLUX1.1 [pro] is priced per image");
    }

    // ------------------------------------------------------------------ harness

    private sealed record Metered(UsageRecordRequest Recorded, ListLogger Logger, Guid CapabilityId);

    private sealed record Attempt(RecordingRecorder Recorder, ListLogger Logger, Guid CapabilityId, Exception? Error);

    private static async Task<Metered> RunAsync(
        InferenceProviderCapability capability,
        byte[] delivered,
        Func<IImageClient, Task<GeneratedImageSet>> call,
        bool singleImage = false,
        int? statedWidth = null,
        int? statedHeight = null) =>
        Single(await TryRunAsync(capability, delivered, call, singleImage, statedWidth, statedHeight));

    private static Metered Single(Attempt attempt)
    {
        attempt.Error.Should().BeNull();
        attempt.Recorder.Recorded.Should().ContainSingle();
        return new Metered(attempt.Recorder.Recorded[0], attempt.Logger, attempt.CapabilityId);
    }

    private static Task<Attempt> TryRunAsync(
        InferenceProviderCapability capability,
        byte[] delivered,
        Func<IImageClient, Task<GeneratedImageSet>> call,
        bool singleImage = false,
        int? statedWidth = null,
        int? statedHeight = null)
    {
        string size = statedWidth is { } w && statedHeight is { } h ? ",\"width\":" + w + ",\"height\":" + h : "";
        string image = "{\"url\":\"https://fal.media/out.png\"" + size + "}";
        string result = singleImage ? "{\"image\":" + image + "}" : "{\"images\":[" + image + "]}";
        const string submit =
            "{\"request_id\":\"r-1\",\"status_url\":\"https://queue.fal.run/m/requests/r-1/status\",\"response_url\":\"https://queue.fal.run/m/requests/r-1\"}";

        StubHandler api = new(request =>
        {
            if (request.Method == HttpMethod.Post) return Json(submit);
            return request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
                ? Json("{\"status\":\"COMPLETED\"}")
                : Json(result);
        });
        StubHandler media = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(delivered) });
        FalAiImageClient fal = new(
            new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "k",
            new HttpClient(media) { BaseAddress = new Uri("https://fal.media") },
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);
        return MeterAsync(fal, InferenceProviderKind.FalAi, "", capability, call);
    }

    private static async Task<Metered> RunBflAsync(
        string model, InferenceProviderCapability capability, byte[] delivered, Func<IImageClient, Task<GeneratedImageSet>> call)
    {
        StubHandler api = new(request => request.Method == HttpMethod.Post
            ? Json("{\"id\":\"req-1\",\"polling_url\":\"https://api.bfl.ai/v1/get_result?id=req-1\"}")
            : Json("{\"status\":\"Ready\",\"result\":{\"sample\":\"https://delivery.bfl.ai/x.png\"}}"));
        StubHandler media = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(delivered) });
        BlackForestLabsImageClient bfl = new(
            new HttpClient(api) { BaseAddress = new Uri("https://api.bfl.ai") }, "k",
            new HttpClient(media) { BaseAddress = new Uri("https://delivery.bfl.ai") },
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);
        return Single(await MeterAsync(bfl, InferenceProviderKind.BlackForestLabs, model, capability, call));
    }

    private static async Task<Attempt> MeterAsync(
        IImageClient inner, InferenceProviderKind kind, string rowModel, InferenceProviderCapability capability,
        Func<IImageClient, Task<GeneratedImageSet>> call)
    {
        Guid capabilityId = Guid.NewGuid();
        RecordingRecorder recorder = new();
        ListLogger logger = new();
        MeteringImageClient metered = new(inner,
            new ResolvedImageProvider(capabilityId, "row", kind, capability, "", rowModel, "k", 60, AccountId: capabilityId),
            recorder, logger);

        Exception? error = null;
        try
        {
            await call(metered);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return new Attempt(recorder, logger, capabilityId, error);
    }

    /// <summary>Prices the recorded unit against the committed seed's rows for its model and capability.</summary>
    private static async Task<PriceQuote> PriceAsync(Metered run, InferenceProviderCapability capability,
        InferenceProviderKind kind = InferenceProviderKind.FalAi, UsageKind unit = UsageKind.ImageEditImages)
    {
        UsageRecordRequest r = run.Recorded;
        List<ModelCapabilityPrice> prices = [.. SeedRows(r.ModelName, capability.ToString(), kind, unit).Select(row => new ModelCapabilityPrice
        {
            Id = Guid.NewGuid(),
            ModelCapabilityId = run.CapabilityId,
            Unit = row.Unit,
            Variant = row.Variant,
            CostUsdPerUnit = row.CostUsdPerUnit,
            EffectiveFrom = row.EffectiveFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            EffectiveTo = row.EffectiveTo?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
        })];
        prices.Should().NotBeEmpty();
        ModelPriceSnapshot snapshot = new(
            [new PricedCapability(run.CapabilityId, run.CapabilityId, null, kind, capability, r.ModelName)],
            prices, []);
        ModelPriceService service = new(new FixedSource(snapshot));

        return await service.PriceAsync(new PriceRequest(r.ProviderId, r.AccountId, r.ProviderKind,
            r.Capability ?? capability, r.ModelName, r.Kind, r.Variant, r.BillingMode, r.Quantity, At));
    }

    private static IEnumerable<SeedPriceRow> SeedRows(string model, string capability,
        InferenceProviderKind kind = InferenceProviderKind.FalAi, UsageKind unit = UsageKind.ImageEditImages) =>
        ModelCatalogSeed.LoadDefault().Rows.Where(r => r.Kind == kind && r.ModelCode == model
            && r.Capability == capability && r.Unit == unit && r.EffectiveTo is null);

    private static decimal SeedCost(string model, string capability, string? variant,
        InferenceProviderKind kind = InferenceProviderKind.FalAi, UsageKind unit = UsageKind.ImageEditImages) =>
        SeedRows(model, capability, kind, unit).Single(r => r.Variant == variant).CostUsdPerUnit;

    private static Dictionary<string, JsonElement> Params(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>A PNG header (signature + IHDR) stating <paramref name="width"/> x <paramref name="height"/>.</summary>
    private static byte[] Png(int width, int height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        0x08, 0x06, 0x00, 0x00, 0x00
    ];

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handler(request));
    }

    private sealed class FixedSource(ModelPriceSnapshot snapshot) : IModelPriceSource
    {
        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class RecordingRecorder : IUsageRecorder
    {
        public List<UsageRecordRequest> Recorded { get; } = [];

        public ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default)
        {
            Recorded.Add(request);
            return ValueTask.FromResult(0L);
        }
    }

    internal sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
