#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
/// The invariants that keep the fal.ai image catalogue honest: every endpoint has a default per job, is
/// documented in the fact sheet field for field, and is PRICED - because a missing rate card records the
/// call at zero credits, silently, and has already done so once (Fish Audio ASR).
/// </summary>
public class FalAiImageCatalogTests
{
    private static readonly DateTime Effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime At = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FalImageCatalog Catalog => FalAiImageClient.Catalog;

    private static InferenceProviderCapability CapabilityOf(FalImageEndpoint e) =>
        e.Job == FalImageJob.Generate ? InferenceProviderCapability.ImageGeneration : InferenceProviderCapability.ImageEdit;

    private static UsageKind UsageOf(FalImageEndpoint e) =>
        e.Job == FalImageJob.Generate ? UsageKind.ImageGenImages : UsageKind.ImageEditImages;

    /// <summary>Every price key an endpoint can be metered under: its id, or one key per tier value.</summary>
    private static IEnumerable<string> PriceKeys(FalImageEndpoint e)
    {
        if (e.BillingTierField is not { } tier)
        {
            yield return e.Id;
            yield break;
        }

        foreach (string value in e.FieldNamed(tier)!.Values!) yield return e.Id + "#" + value;
    }

    // ================================================================ defaults

    [Theory]
    [InlineData(FalImageJob.Erase, "fal-ai/object-removal/mask")]
    [InlineData(FalImageJob.Fill, "fal-ai/flux-pro/v1/fill")]
    [InlineData(FalImageJob.Expand, "fal-ai/bria/expand")]
    [InlineData(FalImageJob.ReplaceBackground, "fal-ai/bria/background/replace")]
    [InlineData(FalImageJob.Relight, "fal-ai/image-apps-v2/relighting")]
    [InlineData(FalImageJob.Upscale, "fal-ai/seedvr/upscale/image")]
    [InlineData(FalImageJob.Instruct, "google/nano-banana-2.1/edit")]
    [InlineData(FalImageJob.Generate, "ideogram/v4.5")]
    public void Each_job_has_exactly_one_default_and_it_is_the_one_the_owner_chose(FalImageJob job, string expected)
    {
        Catalog.All.Count(e => e.Job == job && e.IsDefault).Should().Be(1);
        Catalog.DefaultFor(job).Id.Should().Be(expected);
        Catalog.AlternativesFor(job).Should().NotContain(e => e.Id == expected);
    }

    [Fact]
    public void Every_job_in_the_enum_has_a_default()
    {
        foreach (FalImageJob job in Enum.GetValues<FalImageJob>())
        {
            Catalog.All.Should().Contain(e => e.Job == job && e.IsDefault, job + " has no default endpoint");
        }
    }

    [Fact]
    public void Every_image_operation_maps_to_a_job_with_a_default()
    {
        foreach (ImageOperation operation in Enum.GetValues<ImageOperation>())
        {
            FalImageJob job = FalImageCatalog.JobFor(operation);
            Catalog.DefaultFor(job).Should().NotBeNull(operation + " has no fal endpoint");
        }
    }

    [Fact]
    public void The_documented_alternatives_are_all_present()
    {
        string[] expected =
        [
            "fal-ai/flux-pro/v1/erase", "ideogram/v4.5/edit", "fal-ai/flux-2-pro/outpaint", "fal-ai/iclight-v2", "bria/fibo-edit/relight",
            "topaz/upscale/image/precision", "topaz/upscale/image/generative",
            "blackforestlabs/flux-3/edit-image", "openai/gpt-image-2.5/sunburst/edit", "openai/gpt-image-2.5/flare/edit",
            "bytedance/seedream/v5/pro/edit", "fal-ai/qwen-image-edit-2511", "bria/fibo-edit/edit",
            "bria/fibo-edit/erase_by_text", "fal-ai/image-editing/youtube-thumbnails",
            "google/nano-banana-2.1", "blackforestlabs/flux-3/text-to-image",
            "bytedance/seedream/v5/pro/text-to-image", "fal-ai/ideogram/v3/generate-transparent"
        ];

        Catalog.All.Select(e => e.Id).Should().Contain(expected);
    }

    [Fact]
    public void The_older_generations_the_owner_replaced_are_not_defaults_or_catalogued_at_all()
    {
        string[] dropped = ["fal-ai/nano-banana/edit", "fal-ai/nano-banana-pro/edit", "fal-ai/flux-pro/kontext",
            "fal-ai/nano-banana-pro", "fal-ai/ideogram/v3"];
        Catalog.All.Select(e => e.Id).Should().NotContain(dropped);
        Catalog.All.Where(e => e.IsOlderGeneration).Select(e => e.Id).Should().Equal(
            "fal-ai/ideogram/v3/generate-transparent");
    }

    [Fact]
    public void Ids_are_unique_and_an_unlisted_id_is_not_found()
    {
        Catalog.All.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        Catalog.Find("fal-ai/seedvr2/upscale/image").Should().BeNull("fal answers 404 for it: it does not exist");
        Catalog.Find("ideogram/v4.5/generate-transparent").Should().BeNull("fal answers 404 for it: it does not exist");
        Catalog.Find(null).Should().BeNull();
    }

    // ================================================================ internal consistency

    [Fact]
    public void Every_closed_vocabulary_has_its_vendor_default_inside_it_and_png_where_the_client_asks_for_png()
    {
        foreach (FalImageEndpoint endpoint in Catalog.All)
        {
            foreach (FalImageField field in endpoint.Fields.Where(f => f.Values is not null))
            {
                if (field.VendorDefault is not null && field.SendByDefault is null)
                {
                    field.Values.Should().Contain(field.VendorDefault,
                        endpoint.Id + "." + field.Name + ": the vendor's default must be one of the values");
                }
            }

            if (endpoint.OutputFormatField is { } format)
            {
                endpoint.FieldNamed(format)!.Values.Should().Contain(endpoint.OutputFormatValue,
                    endpoint.Id + ": the client asks for " + endpoint.OutputFormatValue + " in " + format);
                endpoint.OutputFormatValue.Should().BeOneOf("png");
            }

            if (endpoint.BillingTierField is { } tier)
            {
                endpoint.FieldNamed(tier).Should().NotBeNull(endpoint.Id + " names a tier field it does not declare");
                endpoint.FieldNamed(tier)!.Values.Should().NotBeNullOrEmpty();
                endpoint.FieldNamed(tier)!.VendorDefault.Should().NotBeNull(
                    endpoint.Id + ": the tier a call runs under when none is given must be known");
            }

            if (endpoint.AspectRatioField is { } aspect) endpoint.FieldNamed(aspect)!.Values.Should().NotBeNullOrEmpty();
            if (endpoint.SizeField is not null) endpoint.SizePresets.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Wired_inputs_make_sense_for_the_job()
    {
        foreach (FalImageEndpoint e in Catalog.All)
        {
            bool takesPicture = e.ImageUrlField is not null || e.ImageUrlsField is not null;
            takesPicture.Should().Be(e.Job != FalImageJob.Generate, e.Id + ": only text-to-image takes no source picture");
            (e.ImageUrlField is not null && e.ImageUrlsField is not null).Should().BeFalse(e.Id);

            if (e.Job is FalImageJob.Erase or FalImageJob.Fill)
            {
                e.MaskUrlField.Should().NotBeNull(e.Id + " serves InpaintAsync, which needs a mask");
                e.MaskPolarity.Should().NotBe(FalMaskPolarity.None);
            }
            else
            {
                e.MaskPolarity.Should().Be(FalMaskPolarity.None, e.Id);
            }

            if (e.PromptRequired) e.PromptField.Should().NotBeNull(e.Id);
        }
    }

    [Fact]
    public void The_ideogram_size_list_is_the_vendors_36_sizes_and_includes_the_youtube_thumbnail()
    {
        FalImageEndpoint ideogram = Catalog.Find("ideogram/v4.5")!;
        ideogram.SupportedSizes.Should().HaveCount(36).And.Contain("1280x720");
        ideogram.SupportedSizes.Should().OnlyHaveUniqueItems();
    }

    // ================================================================ pricing (the part that has bitten before)

    [Fact]
    public void Every_catalogued_endpoint_and_every_tier_of_it_has_a_cited_published_price()
    {
        foreach (FalImageEndpoint endpoint in Catalog.All)
        {
            foreach (string key in PriceKeys(endpoint))
            {
                ModelPrice? entry = ModelPriceCatalog.Priced.SingleOrDefault(m =>
                    m.ProviderKind == InferenceProviderKind.FalAi && m.Model == key);
                entry.Should().NotBeNull(key + " is callable but has no priced catalogue entry, so it would meter at the wildcard");
                entry!.Capability.Should().Be(CapabilityOf(endpoint), key);
                entry.UsdPerUnit.Should().ContainKey(UsageOf(endpoint));
                entry.UsdPerUnit[UsageOf(endpoint)].Should().BeGreaterThan(0m, key + ": no $0 endpoint may be a default");
                entry.Basis.Should().Contain("fal.ai/models/", key + " must cite the page it was read from");
                entry.Basis.Should().Contain("2026-10-10");
            }
        }
    }

    [Fact]
    public void The_price_book_suggests_every_priced_fal_image_key_and_the_price_equals_the_catalogue()
    {
        foreach (FalImageEndpoint endpoint in Catalog.All)
        {
            foreach (string key in PriceKeys(endpoint))
            {
                (string code, string? tier) = MeteringImageClient.SplitBillingKey(key);
                SuggestedPrice price = ReelBolt.Inference.Api.Services.Billing.PriceBook
                    .SuggestFor(InferenceProviderKind.FalAi, CapabilityOf(endpoint), code)
                    .Single(p => p.Unit == UsageOf(endpoint) && p.Variant == tier);
                ModelPrice entry = ModelPriceCatalog.Priced.Single(m => m.ProviderKind == InferenceProviderKind.FalAi && m.Model == key);
                price.CostUsdPerUnit.Should().Be(entry.UsdPerUnit[UsageOf(endpoint)]);
                price.Notes.Should().Contain(PricingDefaults.PlaceholderMarker).And.Contain("published vendor price");
            }
        }
    }

    [Theory]
    [InlineData("ideogram/v4.5#low", UsageKind.ImageGenImages, 0.03)]
    [InlineData("ideogram/v4.5#medium", UsageKind.ImageGenImages, 0.06)]
    [InlineData("ideogram/v4.5#high", UsageKind.ImageGenImages, 0.22)]
    [InlineData("ideogram/v4.5/edit#very_low", UsageKind.ImageEditImages, 0.008)]
    [InlineData("ideogram/v4.5/edit#high", UsageKind.ImageEditImages, 0.22)]
    [InlineData("fal-ai/ideogram/v3/generate-transparent#TURBO", UsageKind.ImageGenImages, 0.03)]
    [InlineData("fal-ai/ideogram/v3/generate-transparent#QUALITY", UsageKind.ImageGenImages, 0.09)]
    [InlineData("fal-ai/object-removal/mask#low_quality", UsageKind.ImageEditImages, 0.006)]
    [InlineData("fal-ai/object-removal/mask#best_quality", UsageKind.ImageEditImages, 0.024)]
    [InlineData("google/nano-banana-2.1/edit#1K", UsageKind.ImageEditImages, 0.044)]
    [InlineData("google/nano-banana-2.1#4K", UsageKind.ImageGenImages, 0.134)]
    [InlineData("openai/gpt-image-2.5/flare/edit#high", UsageKind.ImageEditImages, 0.0396)]
    [InlineData("fal-ai/flux-pro/v1/fill", UsageKind.ImageEditImages, 0.05)]
    [InlineData("fal-ai/flux-pro/v1/erase", UsageKind.ImageEditImages, 0.042)]
    [InlineData("fal-ai/flux-2-pro/outpaint", UsageKind.ImageEditImages, 0.03)]
    [InlineData("fal-ai/bria/expand", UsageKind.ImageEditImages, 0.04)]
    [InlineData("fal-ai/bria/background/replace", UsageKind.ImageEditImages, 0.04)]
    [InlineData("fal-ai/image-apps-v2/relighting", UsageKind.ImageEditImages, 0.04)]
    [InlineData("fal-ai/iclight-v2", UsageKind.ImageEditImages, 0.1)]
    [InlineData("fal-ai/seedvr/upscale/image", UsageKind.ImageEditImages, 0.001)]
    [InlineData("topaz/upscale/image/precision", UsageKind.ImageEditImages, 0.08)]
    [InlineData("topaz/upscale/image/generative", UsageKind.ImageEditImages, 0.08)]
    [InlineData("blackforestlabs/flux-3/edit-image", UsageKind.ImageEditImages, 0.048)]
    [InlineData("blackforestlabs/flux-3/text-to-image", UsageKind.ImageGenImages, 0.048)]
    [InlineData("bytedance/seedream/v5/pro/edit", UsageKind.ImageEditImages, 0.0675)]
    [InlineData("fal-ai/qwen-image-edit-2511", UsageKind.ImageEditImages, 0.03)]
    [InlineData("bria/fibo-edit/relight", UsageKind.ImageEditImages, 0.04)]
    [InlineData("fal-ai/image-editing/youtube-thumbnails", UsageKind.ImageEditImages, 0.04)]
    public async Task A_call_is_priced_at_the_published_figure_not_at_the_wildcard(string key, UsageKind usage, double usd)
    {
        // After the upgrade: the endpoint is a model of the fal account and its tiers are price variants.
        (string code, string? tier) = MeteringImageClient.SplitBillingKey(key);
        InferenceProviderCapability capability = usage == UsageKind.ImageGenImages
            ? InferenceProviderCapability.ImageGeneration
            : InferenceProviderCapability.ImageEdit;
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync([(InferenceProviderKind.FalAi, capability, code)]);

        PriceQuote quote = await catalog.PriceAsync(InferenceProviderKind.FalAi, capability, code, usage, variant: tier);

        quote.PriceId.Should().NotBeNull();
        quote.CostUsd.Should().Be((decimal)usd);
    }

    [Fact]
    public async Task Sam_is_priced_per_request_and_the_two_versions_differ_as_published()
    {
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync(
        [
            (InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/sam-3-1/image"),
            (InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/sam-3/image")
        ]);

        (await catalog.PriceAsync(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation,
            "fal-ai/sam-3-1/image", UsageKind.SegmentationImages)).CostUsd.Should().Be(0.01m);
        (await catalog.PriceAsync(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation,
            "fal-ai/sam-3/image", UsageKind.SegmentationImages)).CostUsd.Should().Be(0.005m);

        ModelPriceCatalog.Priced.Single(m => m.Model == "fal-ai/sam-3-1/image").Basis
            .Should().Contain("per REQUEST").And.Contain("ONE SegmentationImages unit per SegmentAsync call");
    }

    [Fact]
    public void The_five_original_analysis_endpoints_stay_unpriced_with_their_reason()
    {
        foreach (string id in new[] { "fal-ai/evf-sam", "fal-ai/sam2/image", "fal-ai/birefnet/v2",
                     "fal-ai/imageutils/depth", "fal-ai/imageutils/marigold-depth" })
        {
            ModelPriceCatalog.All.Single(m => m.Model == id).IsPriced.Should().BeFalse();
        }
    }

    [Fact]
    public void The_capability_table_and_the_admin_boundary_agree_on_what_fal_serves()
    {
        InferenceProviderCapability[] expected =
        [
            InferenceProviderCapability.Segmentation, InferenceProviderCapability.Depth,
            InferenceProviderCapability.ImageGeneration, InferenceProviderCapability.ImageEdit,
            InferenceProviderCapability.ImageErase, InferenceProviderCapability.ImageFill,
            InferenceProviderCapability.ImageExpand, InferenceProviderCapability.ImageBackgroundReplace,
            InferenceProviderCapability.ImageRelight, InferenceProviderCapability.ImageUpscale,
            // Appended by the image-to-video wave: fal's VIDEO endpoints (FalAiVideoCatalog) ride the
            // same kind, credential and queue contract. Its own invariants live in
            // FalAiVideoGenerationClientTests.
            InferenceProviderCapability.VideoGeneration,
            // Appended by the prompted-region-effects contracts: fal-ai/sam-3-1/video-rle is the one
            // promptable VIDEO segmenter this platform configures, so the capability is fal-only.
            InferenceProviderCapability.VideoSegmentation,
            // Appended by the audio arm (T-107): music and sound effects on the same queue contract.
            InferenceProviderCapability.MusicGeneration, InferenceProviderCapability.SoundEffectGeneration,
            // Appended by the audio-cleanup vendor layer: hosted voice isolation on the same queue contract.
            InferenceProviderCapability.AudioIsolation,
            // Appended by the video-enhance vendor layer: hosted upscaling and frame interpolation on the same queue contract.
            InferenceProviderCapability.VideoUpscale, InferenceProviderCapability.FrameInterpolation, InferenceProviderCapability.VideoOutpaint,
            // Appended by the avatar-lipsync-capability initiative: talking-head lip-sync on the same queue contract.
            InferenceProviderCapability.LipSync
        ];

        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.FalAi].Should().BeEquivalentTo(expected);
        foreach (InferenceProviderCapability capability in ProviderCapabilityUsage.Capabilities)
        {
            InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.FalAi, capability, out _)
                .Should().Be(!expected.Contains(capability), "FalAi/" + capability);
        }
    }

    /// <summary>
    /// A fal model the old seed had no exact card for was priced by the derived wildcard; the upgrade ports exactly that
    /// figure (the dearest catalogued edit price) onto its capability, with the wildcard's own explanation.
    /// </summary>
    [Fact]
    public async Task An_uncatalogued_model_ports_the_derived_wildcard_and_says_so()
    {
        await using PortedCatalog catalog = await PortedCatalog.CreateAsync(
            [(InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit, "fal-ai/not-in-the-catalogue")]);
        Guid capabilityId = catalog.CapabilityId(InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit, "fal-ai/not-in-the-catalogue");
        await using ReelBolt.Inference.Api.Data.InferenceApiDbContext db = catalog.NewContext();

        ModelCapabilityPrice ported = db.ModelCapabilityPrices.Single(p => p.ModelCapabilityId == capabilityId);

        ported.CostUsdPerUnit.Should().Be(
            ModelPriceCatalog.Priced.Where(m => m.UsdPerUnit.ContainsKey(UsageKind.ImageEditImages))
                .Max(m => m.UsdPerUnit[UsageKind.ImageEditImages]));
        ported.Variant.Should().BeNull();
        ported.Notes.Should().Contain("derived, not invented");
    }

    // ================================================================ the fact sheet (field-for-field)

    private static string? FindFactSheet()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "docs", "fal-ai-models.md");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static string SectionOf(string sheet, string id)
    {
        string marker = "### `" + id + "`";
        int start = sheet.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "'" + id + "' must have a section in docs/fal-ai-models.md");
        int end = sheet.IndexOf("\n### ", start + marker.Length, StringComparison.Ordinal);
        int endTop = sheet.IndexOf("\n## ", start + marker.Length, StringComparison.Ordinal);
        if (end < 0 || (endTop >= 0 && endTop < end)) end = endTop;
        return end < 0 ? sheet[start..] : sheet[start..end];
    }

    [Fact]
    public void Every_catalogued_endpoint_is_documented_in_the_fact_sheet_field_for_field()
    {
        string? path = FindFactSheet();
        path.Should().NotBeNull("docs/fal-ai-models.md is the transcription source and must be findable from the test run");
        string sheet = File.ReadAllText(path!);

        foreach (FalImageEndpoint e in Catalog.All)
        {
            string section = SectionOf(sheet, e.Id);

            string[] wired = new[]
            {
                e.ImageUrlField, e.ImageUrlsField, e.MaskUrlField, e.PromptField, e.NegativePromptField,
                e.SeedField, e.CountField, e.SizeField, e.OutputFormatField,
                e.CanvasSizeField, e.OriginalImageSizeField, e.OriginalImageLocationField
            }.Where(n => n is not null).Select(n => n!).ToArray();
            foreach (string name in wired)
            {
                section.Should().Contain("| `" + name + "` |", e.Id + ": wired field '" + name + "' is not in its schema table");
            }

            foreach (FalImageField field in e.Fields)
            {
                string rowName = field.WrapIn is null ? field.Name : field.Name + "." + field.WrapIn;
                string? row = section.Split('\n').FirstOrDefault(l => l.StartsWith("| `" + rowName + "` |", StringComparison.Ordinal));
                row.Should().NotBeNull(e.Id + ": field '" + rowName + "' is not in the fact sheet's schema table");

                if (field.Values is null || row!.Contains("(same ", StringComparison.Ordinal)) continue;
                foreach (string value in field.Values)
                {
                    row.Should().Contain("\"" + value + "\"",
                        e.Id + "." + field.Name + ": '" + value + "' is not in the vendor's enum as transcribed");
                }
            }
        }
    }

    [Fact]
    public void Every_expand_endpoint_states_its_geometry_and_where_its_limits_were_read()
    {
        foreach (FalImageEndpoint e in Catalog.All.Where(e => e.Job == FalImageJob.Expand))
        {
            e.ExpandBy.Should().NotBe(FalExpandGeometry.None, e.Id + " cannot be planned without its geometry kind");
            e.LimitsBasis.Should().NotBeNullOrWhiteSpace(e.Id);
            if (e.ExpandBy == FalExpandGeometry.Margins)
            {
                e.MaxMarginPx.Should().NotBeNull(e.Id + ": a margins endpoint's per-side cap comes from its expand_top field");
            }
            else
            {
                new[] { e.CanvasSizeField, e.OriginalImageSizeField, e.OriginalImageLocationField }.Should().NotContainNulls(e.Id);
            }
        }

        Catalog.All.Where(e => e.Job != FalImageJob.Expand).Should().OnlyContain(e => e.ExpandBy == FalExpandGeometry.None);

        FalImageEndpoint bria = Catalog.Find("fal-ai/bria/expand")!;
        bria.MaxOutputPixels.Should().Be(25_000_000);
        bria.MaxOutputPixelsExclusive.Should().BeTrue("the schema says 'an area of LESS than 5000x5000 pixels'");
        bria.MaxInputPixels.Should().BeNull("no input limit is documented, so none is invented");
        bria.PromptField.Should().Be("prompt");
        bria.FieldNamed("aspect_ratio").Should().BeNull(
            "with aspect_ratio the vendor picks the canvas and the placement, which the editor could not line up");

        FalImageEndpoint flux = Catalog.Find("fal-ai/flux-2-pro/outpaint")!;
        flux.MaxMarginPx.Should().Be(2048);
        flux.MaxOutputPixels.Should().Be(2048L * 2048, "the vendor's own \"4MP (e.g., 2048x2048)\" example");
        flux.MaxInputPixels.Should().Be(4_000_000);
        flux.PromptField.Should().BeNull();
    }

    [Fact]
    public void The_fact_sheet_records_how_bria_expands_array_order_was_resolved()
    {
        string section = SectionOf(File.ReadAllText(FindFactSheet()!), "fal-ai/bria/expand");
        section.Should().Contain("[1200, 674]").And.Contain("docs.bria.ai");
    }

    [Fact]
    public void The_fact_sheet_records_every_added_id_with_its_source()
    {
        string sheet = File.ReadAllText(FindFactSheet()!);
        foreach (string id in new[]
                 {
                     "fal-ai/sam-3-1/image", "google/nano-banana-2.1", "openai/gpt-image-2.5/sunburst/edit",
                     "openai/gpt-image-2.5/flare/edit", "bytedance/seedream/v5/pro/edit", "fal-ai/qwen-image-edit-2511",
                     "topaz/upscale/image/generative", "bria/fibo-edit/relight", "bria/fibo-edit/edit",
                     "bria/fibo-edit/erase_by_text"
                 })
        {
            SectionOf(sheet, id).Should().Contain("openapi.json?endpoint_id=" + id);
        }
    }
}
