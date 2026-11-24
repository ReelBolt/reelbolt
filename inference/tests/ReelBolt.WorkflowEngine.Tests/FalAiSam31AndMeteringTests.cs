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
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// SAM 3.1 through <see cref="FalAiAnalysisClient"/>, the fal arm of <see cref="ImageClientFactory"/>, and
/// the metering of both: one unit per segmentation REQUEST, one per image RETURNED, billed under the
/// endpoint and tier that actually ran.
/// </summary>
public class FalAiSam31AndMeteringTests
{
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x02, 0x00, 0x08, 0x06, 0x00, 0x00, 0x00
    ];

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            return _handler(request);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static FalAiAnalysisClient Client(string resultJson, out StubHandler api, out StubHandler media)
    {
        const string submit =
            "{\"request_id\":\"sam-1\",\"status_url\":\"https://queue.fal.run/m/requests/sam-1/status\",\"response_url\":\"https://queue.fal.run/m/requests/sam-1\"}";
        StubHandler apiHandler = new(request =>
        {
            if (request.Method == HttpMethod.Post) return Json(submit);
            return request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
                ? Json("{\"status\":\"COMPLETED\"}")
                : Json(resultJson);
        });
        StubHandler mediaHandler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) });
        api = apiHandler;
        media = mediaHandler;
        return new FalAiAnalysisClient(
            new HttpClient(apiHandler) { BaseAddress = new Uri("https://queue.fal.run") }, "k",
            new HttpClient(mediaHandler) { BaseAddress = new Uri("https://fal.media") },
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);
    }

    private static JsonElement PostBody(StubHandler api) =>
        JsonDocument.Parse(api.Bodies[api.Requests.FindIndex(r => r.Method == HttpMethod.Post)]).RootElement;

    private const string OneMask =
        "{\"image\":{\"url\":\"https://fal.media/cut.png\"},\"masks\":[{\"url\":\"https://fal.media/m0.png\"}]}";

    private const string ThreeMasks =
        "{\"image\":{\"url\":\"https://fal.media/cut.png\"}," +
        "\"masks\":[{\"url\":\"https://fal.media/m0.png\"},{\"url\":\"https://fal.media/m1.png\"},{\"url\":\"https://fal.media/m2.png\"}]," +
        "\"metadata\":[{\"index\":0,\"score\":0.9,\"box\":[0.5,0.5,0.2,0.4]},{\"index\":1,\"score\":0.7,\"box\":null}]," +
        "\"scores\":[0.9,0.7,0.4],\"boxes\":[[0.5,0.5,0.2,0.4],null,[0.1,0.1,0.1,0.1]]}";

    // ================================================================ request

    [Fact]
    public async Task Sam31_with_a_prompt_sends_it_and_the_png_and_scores_flags()
    {
        FalAiAnalysisClient client = Client(OneMask, out StubHandler api, out _);

        await client.SegmentAsync(new SegmentationRequest(FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png", Prompt: "the person"),
            CancellationToken.None);

        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/fal-ai/sam-3-1/image");
        api.Bodies[0].Should().Be(
            "{\"image_url\":\"https://files.example.com/a.png\",\"prompt\":\"the person\",\"apply_mask\":true,\"output_format\":\"png\"," +
            "\"include_scores\":true,\"include_boxes\":true}");
    }

    [Fact]
    public async Task Sam31_never_sends_the_schema_default_prompt_wheel_and_refuses_nothing_to_segment()
    {
        FalAiAnalysisClient client = Client(OneMask, out StubHandler api, out _);

        await client.SegmentAsync(new SegmentationRequest(FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png",
            PointPrompts: [new PointPrompt(100, 200, 1)]), CancellationToken.None);

        PostBody(api).TryGetProperty("prompt", out _).Should().BeFalse("omitted, never the vendor's 'wheel'");
        api.Bodies[0].Should().NotContain("wheel").And.NotContain("text_prompt");

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png"), CancellationToken.None));
        ex.Message.Should().Contain("wheel");
    }

    [Fact]
    public async Task Sam31_passes_point_and_box_integers_through_verbatim()
    {
        FalAiAnalysisClient client = Client(OneMask, out StubHandler api, out _);

        await client.SegmentAsync(new SegmentationRequest(FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png",
            PointPrompts: [new PointPrompt(640, 360, 1), new PointPrompt(10, 20, 0)],
            BoxPrompts: [new BoxPrompt(5, 6, 700, 800)]), CancellationToken.None);

        JsonElement body = PostBody(api);
        body.GetProperty("point_prompts").GetArrayLength().Should().Be(2);
        body.GetProperty("point_prompts")[0].GetRawText().Should().Be("{\"x\":640,\"y\":360,\"label\":1}");
        body.GetProperty("point_prompts")[1].GetProperty("label").GetInt32().Should().Be(0);
        body.GetProperty("box_prompts")[0].GetRawText().Should().Be("{\"x_min\":5,\"y_min\":6,\"x_max\":700,\"y_max\":800}");
    }

    [Fact]
    public async Task Sam31_validates_labels_and_the_mask_count_before_sending()
    {
        FalAiAnalysisClient client = Client(OneMask, out StubHandler api, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentAsync(new SegmentationRequest(
            FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png", PointPrompts: [new PointPrompt(1, 2, 2)]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentAsync(new SegmentationRequest(
            FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png", Prompt: "x", MaxMasks: 3), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SegmentAsync(new SegmentationRequest(
            FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png", Prompt: "x", ReturnMultipleMasks: true, MaxMasks: 33),
            CancellationToken.None));
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Sam31_forwards_the_multiple_mask_switches_only_when_asked()
    {
        FalAiAnalysisClient client = Client(ThreeMasks, out StubHandler api, out _);

        await client.SegmentAsync(new SegmentationRequest(FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png",
            Prompt: "car", ReturnMultipleMasks: true, MaxMasks: 3), CancellationToken.None);

        PostBody(api).GetProperty("return_multiple_masks").GetBoolean().Should().BeTrue();
        PostBody(api).GetProperty("max_masks").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Another_endpoint_refuses_point_prompts_and_multiple_masks()
    {
        FalAiAnalysisClient client = Client(OneMask, out StubHandler api, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentAsync(new SegmentationRequest(
            FalAiAnalysisClient.EvfSam, "https://files.example.com/a.png", Prompt: "x", PointPrompts: [new PointPrompt(1, 2, 1)]),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentAsync(new SegmentationRequest(
            FalAiAnalysisClient.BirefnetV2, "https://files.example.com/a.png", ReturnMultipleMasks: true), CancellationToken.None));
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_model_means_the_catalogue_default_which_is_sam31()
    {
        FalAiAnalysisClient client = Client(OneMask, out StubHandler api, out _);

        SegmentationResult result = await client.SegmentAsync(
            new SegmentationRequest("", "https://files.example.com/a.png", Prompt: "the person"), CancellationToken.None);

        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/fal-ai/sam-3-1/image");
        result.ResolvedModel.Should().Be("fal-ai/sam-3-1/image");
        FalAiAnalysisClient.DefaultSegmentationModel.Should().Be("fal-ai/sam-3-1/image");
    }

    [Fact]
    public async Task An_explicit_model_with_no_read_schema_is_still_refused()
    {
        FalAiAnalysisClient client = Client(OneMask, out StubHandler api, out _);

        await Assert.ThrowsAsync<NotSupportedException>(() => client.SegmentAsync(
            new SegmentationRequest("fal-ai/sam-4/image", "https://files.example.com/a.png", Prompt: "x"), CancellationToken.None));
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ result

    [Fact]
    public async Task A_single_mask_result_has_the_vendor_image_as_the_bitmap_and_the_mask_as_the_matte()
    {
        FalAiAnalysisClient client = Client(OneMask, out _, out StubHandler media);

        SegmentationResult result = await client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png", Prompt: "x"), CancellationToken.None);

        result.Masks.Should().BeNull("a single-result caller reads Cutout/Matte; AllMasks yields one pair");
        result.AllMasks.Should().ContainSingle();
        result.Cutout.Bytes.Should().Equal(PngBytes);
        result.Matte.Should().NotBeNull();
        result.Cutout.Width.Should().Be(1024, "read from the PNG header: the schema's image carried no size");
        result.VendorRequestId.Should().Be("sam-1");
        media.Requests.Select(r => r.RequestUri!.AbsoluteUri).Should().Equal("https://fal.media/cut.png", "https://fal.media/m0.png");
    }

    [Fact]
    public async Task Multiple_masks_each_carry_a_matte_score_and_normalized_box_and_later_ones_use_the_source_bitmap()
    {
        FalAiAnalysisClient client = Client(ThreeMasks, out _, out StubHandler media);

        SegmentationResult result = await client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.Sam31Image, "https://fal.media/source.png", Prompt: "car",
                ReturnMultipleMasks: true, MaxMasks: 3), CancellationToken.None);

        result.AllMasks.Should().HaveCount(3);
        result.AllMasks.Select(m => m.Score).Should().Equal(0.9, 0.7, 0.4);
        result.AllMasks[0].BoxCxCyWh.Should().Equal(0.5, 0.5, 0.2, 0.4);
        result.AllMasks[1].BoxCxCyWh.Should().BeNull();
        result.AllMasks[2].BoxCxCyWh.Should().Equal(0.1, 0.1, 0.1, 0.1);
        result.AllMasks.Should().OnlyContain(m => m.Matte != null);

        // The first bitmap is the vendor's image; the other two have none, so the source is fetched ONCE.
        media.Requests.Select(r => r.RequestUri!.AbsoluteUri).Should().Equal(
            "https://fal.media/cut.png", "https://fal.media/m0.png", "https://fal.media/m1.png",
            "https://fal.media/source.png", "https://fal.media/m2.png");
    }

    [Fact]
    public async Task Without_the_multiple_flag_only_the_primary_mask_is_read_even_if_the_vendor_sent_more()
    {
        FalAiAnalysisClient client = Client(ThreeMasks, out _, out StubHandler media);

        SegmentationResult result = await client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png", Prompt: "car"), CancellationToken.None);

        result.Masks.Should().BeNull();
        media.Requests.Should().HaveCount(2, "no point paying to download masks nobody asked for");
    }

    [Fact]
    public async Task A_result_with_no_masks_is_a_failure()
    {
        FalAiAnalysisClient client = Client("{\"masks\":[]}", out _, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.Sam31Image, "https://files.example.com/a.png", Prompt: "x"), CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_vendor_image_falls_back_to_the_source_bitmap()
    {
        FalAiAnalysisClient client = Client("{\"masks\":[{\"url\":\"https://fal.media/m0.png\"}]}", out _, out _);

        SegmentationResult result = await client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.Sam31Image, "https://fal.media/source.png", Prompt: "x"),
            CancellationToken.None);

        result.Cutout.VendorUrl.Should().Be("https://fal.media/source.png");
        result.Matte!.VendorUrl.Should().Be("https://fal.media/m0.png");
    }

    [Fact]
    public void Describe_reports_sam31_as_the_only_point_and_multi_mask_family_member_besides_sam3()
    {
        FalAiAnalysisClient client = Client(OneMask, out _, out _);

        ImageAnalysisModelCapabilities sam = client.Describe(FalAiAnalysisClient.Sam31Image);
        sam.TakesTextPrompt.Should().BeTrue();
        sam.TakesBoxPrompts.Should().BeTrue();
        sam.TakesPointPrompts.Should().BeTrue();
        sam.CanReturnMultipleMasks.Should().BeTrue();
        sam.Basis.Should().Contain("wheel").And.Contain("NO stated space");

        client.Describe(FalAiAnalysisClient.EvfSam).TakesPointPrompts.Should().BeFalse();
        client.Describe(FalAiAnalysisClient.Sam3Image).CanReturnMultipleMasks.Should().BeTrue();
    }

    // ================================================================ metering

    private sealed class RecordingRecorder : IUsageRecorder
    {
        public List<UsageRecordRequest> Recorded { get; } = [];

        public ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default)
        {
            Recorded.Add(request);
            return ValueTask.FromResult(0L);
        }
    }

    private sealed class FakeAnalysis(int masks) : IImageAnalysisClient
    {
        public Task<SegmentationResult> SegmentAsync(SegmentationRequest request, CancellationToken ct)
        {
            GeneratedImage image = new([1], "image/png");
            return Task.FromResult(new SegmentationResult(image, image)
            {
                ResolvedModel = FalAiAnalysisClient.Sam31Image,
                Masks = [.. Enumerable.Range(0, masks).Select(_ => new SegmentedMask(image, image))]
            });
        }

        public Task<DepthResult> EstimateDepthAsync(DepthRequest request, CancellationToken ct) =>
            Task.FromResult(new DepthResult(new GeneratedImage([1], "image/png")));

        public Task<ImageAnalysisProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new ImageAnalysisProbeResult(true, null));

        public ImageAnalysisModelCapabilities Describe(string model) => new(model, true, true, true, false, "test");
    }

    [Fact]
    public async Task Sam31_with_many_masks_is_ONE_metered_unit_billed_under_the_endpoint_that_ran()
    {
        RecordingRecorder recorder = new();
        MeteringImageAnalysisClient client = new(new FakeAnalysis(5),
            new ResolvedImageAnalysisProvider(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi,
                InferenceProviderCapability.Segmentation, "", "", "k", 60), recorder);

        await client.SegmentAsync(new SegmentationRequest("", "https://files.example.com/a.png", Prompt: "x", ReturnMultipleMasks: true),
            CancellationToken.None);

        recorder.Recorded.Should().ContainSingle();
        recorder.Recorded[0].Kind.Should().Be(UsageKind.SegmentationImages);
        recorder.Recorded[0].Quantity.Should().Be(1, "fal bills SAM 3.1 $0.01 per request, not per mask");
        recorder.Recorded[0].ModelName.Should().Be("fal-ai/sam-3-1/image", "the row's model is empty; the model that ran is billed");
    }

    private sealed class FakeImages(string resolved, int count) : IImageClient, IBillingModelKeyed
    {
        private GeneratedImageSet Set() => new(
            [.. Enumerable.Range(0, count).Select(_ => new GeneratedImage([1], "image/png"))])
        { ResolvedModel = resolved };

        public Task<GeneratedImageSet> GenerateAsync(ReelBolt.Shared.Inference.ImageGenerationRequest request, CancellationToken ct) => Task.FromResult(Set());
        public Task<GeneratedImageSet> EditAsync(ImageEditRequest request, CancellationToken ct) => Task.FromResult(Set());
        public Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest request, CancellationToken ct) => Task.FromResult(Set());
        public Task<GeneratedImageSet> TransformAsync(ImageTransformRequest request, CancellationToken ct) => Task.FromResult(Set());
        public Task<ImageProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new ImageProbeResult(true, null));
        public ImageModelCapabilities Describe(string model) => new(model, true, true, 1, 100, "test");
        public string BillingModelKey(string model, IReadOnlyDictionary<string, JsonElement>? parameters) =>
            new FalAiImageClient(new HttpClient(), "k").BillingModelKey(model, parameters);
    }

    private static MeteringImageClient Metered(FakeImages inner, InferenceProviderCapability capability, RecordingRecorder recorder) =>
        new(inner, new ResolvedImageProvider(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, capability, "", "",
            "k", 60), recorder);

    [Fact]
    public async Task Each_image_returned_is_one_unit_under_the_resolved_endpoint_and_its_tier()
    {
        RecordingRecorder recorder = new();
        MeteringImageClient client = Metered(new FakeImages("ideogram/v4.5", 3), InferenceProviderCapability.ImageGeneration, recorder);

        await client.GenerateAsync(new ReelBolt.Shared.Inference.ImageGenerationRequest("", "x",
            Params: JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"quality\":\"high\"}")), CancellationToken.None);

        recorder.Recorded.Should().ContainSingle();
        recorder.Recorded[0].Kind.Should().Be(UsageKind.ImageGenImages);
        recorder.Recorded[0].Quantity.Should().Be(3);
        recorder.Recorded[0].ModelName.Should().Be("ideogram/v4.5", "the code#tier billing key splits into the model...");
        recorder.Recorded[0].Variant.Should().Be("high", "...and the price variant (docs/design/model-catalog.md §2.2)");
        recorder.Recorded[0].Capability.Should().Be(InferenceProviderCapability.ImageGeneration);
    }

    [Fact]
    public async Task Edits_inpaints_and_transforms_all_meter_the_edit_kind_under_the_endpoint_that_ran()
    {
        RecordingRecorder recorder = new();
        MeteringImageClient client = Metered(new FakeImages("fal-ai/flux-pro/v1/fill", 1), InferenceProviderCapability.ImageEdit, recorder);

        await client.EditAsync(new ImageEditRequest("", "p", "u"), CancellationToken.None);
        await client.InpaintAsync(new ImageInpaintRequest("", "p", "u", [], MaskUrl: "m"), CancellationToken.None);
        await client.TransformAsync(new ImageTransformRequest(ImageOperation.Upscale, "", "u"), CancellationToken.None);

        recorder.Recorded.Should().HaveCount(3);
        recorder.Recorded.Should().OnlyContain(r => r.Kind == UsageKind.ImageEditImages && r.Quantity == 1
            && r.ModelName == "fal-ai/flux-pro/v1/fill");
    }

    [Fact]
    public async Task A_probe_is_never_metered_and_a_recorded_price_resolves_to_its_tier_not_the_wildcard()
    {
        RecordingRecorder recorder = new();
        MeteringImageClient client = Metered(new FakeImages("fal-ai/object-removal/mask", 1), InferenceProviderCapability.ImageEdit, recorder);

        await client.ProbeAsync(CancellationToken.None);
        recorder.Recorded.Should().BeEmpty();

        await client.InpaintAsync(new ImageInpaintRequest("", "", "u", [], MaskUrl: "m",
            Params: JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"quality\":\"low_quality\"}")), CancellationToken.None);

        UsageRecordRequest recorded = recorder.Recorded[0];
        recorded.ModelName.Should().Be("fal-ai/object-removal/mask");
        recorded.Variant.Should().Be("low_quality", "the tier rides as the price variant, not inside the model name");

        await using ReelBolt.WorkflowEngine.Tests.Pricing.PortedCatalog catalog = await ReelBolt.WorkflowEngine.Tests.Pricing.PortedCatalog
            .CreateAsync([(InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit, "fal-ai/object-removal/mask")]);
        PriceQuote quote = await catalog.PriceAsync(InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit,
            "fal-ai/object-removal/mask", recorded.Kind, recorded.Quantity, recorded.Variant, recorded.ModelName);

        quote.CostUsd.Should().Be(0.006m, "the low tier ran, so the low tier is what is charged");
    }

    // ================================================================ factory

    [Fact]
    public void The_image_factory_builds_a_fal_client_for_a_FalAi_row_and_meters_it()
    {
        ImageClientFactory factory = new(new RecordingRecorder());
        ResolvedImageProvider row = new(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi,
            InferenceProviderCapability.ImageEdit, "", "google/nano-banana-2.1/edit", "k", 60);

        IImageClient client = factory.Get(row);

        client.Should().BeOfType<MeteringImageClient>();
        client.Describe("google/nano-banana-2.1/edit").SupportsInstructionEdit.Should().BeTrue();
        new ImageClientFactory().Get(row).Should().BeOfType<FalAiImageClient>();
    }

    [Fact]
    public void The_factory_still_refuses_kinds_with_no_image_api()
    {
        ResolvedImageProvider row = new(Guid.NewGuid(), "x", InferenceProviderKind.FishAudio,
            InferenceProviderCapability.ImageEdit, "https://e", "m", "k", 60);

        Action build = () => new ImageClientFactory().Get(row);

        build.Should().Throw<NotSupportedException>().WithMessage("*FalAi*");
    }

    [Fact]
    public void The_fal_image_client_shares_the_queue_host_default_with_the_analysis_client()
    {
        FalAiImageClient.DefaultEndpoint.Should().Be(FalAiAnalysisClient.DefaultEndpoint);
    }

    // ================================================================ dimension sniffer

    [Fact]
    public void Image_headers_give_their_pixel_size_and_garbage_gives_none()
    {
        ImagePayload.TryReadDimensions(PngBytes, out int w, out int h).Should().BeTrue();
        (w, h).Should().Be((1024, 512));

        // A baseline JPEG: SOI, an APP0 segment, then SOF0 (height 360, width 640).
        byte[] jpeg =
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x68, 0x02, 0x80, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01
        ];
        ImagePayload.TryReadDimensions(jpeg, out w, out h).Should().BeTrue();
        (w, h).Should().Be((640, 360));

        // Lossless WebP: RIFF....WEBPVP8L, signature 0x2F, then (width-1) and (height-1) in 14 bits each.
        byte[] webp =
        [
            0x52, 0x49, 0x46, 0x46, 0x20, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x4C,
            0x0D, 0x00, 0x00, 0x00, 0x2F, 0xFF, 0x4F, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];
        ImagePayload.TryReadDimensions(webp, out w, out h).Should().BeTrue();
        (w, h).Should().Be((4096, 46));

        ImagePayload.TryReadDimensions(new byte[] { 1, 2, 3 }, out _, out _).Should().BeFalse();
        ImagePayload.TryReadDimensions(new byte[40], out _, out _).Should().BeFalse();
    }
}
