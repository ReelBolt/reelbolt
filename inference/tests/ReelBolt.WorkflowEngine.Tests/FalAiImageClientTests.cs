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
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The fal.ai image arm, driven against a MOCKED HTTP layer. Nothing here touches the live queue: no
/// credential exists, every endpoint is paid, and a field fal silently ignores would look like a working
/// request until someone compared the image to what they asked for. Each test pins either the exact
/// body sent for one endpoint, or a refusal that must happen BEFORE anything is sent.
/// </summary>
public class FalAiImageClientTests
{
    /// <summary>A real PNG header for a 1280x720 image: enough for the dimension sniffer, not a picture.</summary>
    private static readonly byte[] Png1280X720 =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x02, 0xD0, 0x08, 0x06, 0x00, 0x00, 0x00
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

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static FalAiImageClient Client(
        string resultJson, out StubHandler api, out StubHandler media, string? probeModel = null)
    {
        const string submit =
            "{\"request_id\":\"req-9\",\"status_url\":\"https://queue.fal.run/m/requests/req-9/status\",\"response_url\":\"https://queue.fal.run/m/requests/req-9\"}";

        StubHandler apiHandler = new(request =>
        {
            if (request.Method == HttpMethod.Post) return Json(submit);
            return request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
                ? Json("{\"status\":\"COMPLETED\"}")
                : Json(resultJson);
        });
        StubHandler mediaHandler = new(_ => Bytes(Png1280X720));

        api = apiHandler;
        media = mediaHandler;
        return new FalAiImageClient(
            new HttpClient(apiHandler) { BaseAddress = new Uri("https://queue.fal.run") }, "fal-key",
            new HttpClient(mediaHandler) { BaseAddress = new Uri("https://fal.media") },
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask, probeModel: probeModel);
    }

    private const string OneImage = "{\"images\":[{\"url\":\"https://fal.media/a.png\"}]}";

    private static Dictionary<string, JsonElement> P(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>The bodies of the POSTs only - the poll GETs interleave with empty bodies.</summary>
    private static List<string> Posts(StubHandler api) =>
        [.. api.Requests.Select((r, i) => (r, i)).Where(t => t.r.Method == HttpMethod.Post).Select(t => api.Bodies[t.i])];

    private static JsonElement Body(StubHandler api) => JsonDocument.Parse(Posts(api).Last()).RootElement;

    // ================================================================ the queue contract

    [Fact]
    public async Task Submits_to_the_endpoint_path_with_a_Key_header_and_returns_downloaded_bytes_never_a_url()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out StubHandler media);

        GeneratedImageSet set = await client.GenerateAsync(
            new ImageGenerationRequest("ideogram/v4.5", "BIG TEXT"), CancellationToken.None);

        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/ideogram/v4.5");
        api.Requests[0].Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Key fal-key");
        api.Requests[1].RequestUri!.ToString().Should().Be("https://queue.fal.run/m/requests/req-9/status");
        api.Requests[2].RequestUri!.ToString().Should().Be("https://queue.fal.run/m/requests/req-9");
        media.Requests.Should().ContainSingle();
        set.Images.Should().ContainSingle().Which.Bytes.Should().Equal(Png1280X720);
        set.VendorRequestId.Should().Be("req-9");
        set.ResolvedModel.Should().Be("ideogram/v4.5");
    }

    [Fact]
    public async Task A_size_the_vendor_does_not_state_is_read_from_the_image_header()
    {
        FalAiImageClient client = Client(OneImage, out _, out _);

        GeneratedImageSet set = await client.GenerateAsync(
            new ImageGenerationRequest("ideogram/v4.5", "x"), CancellationToken.None);

        set.Images[0].Width.Should().Be(1280);
        set.Images[0].Height.Should().Be(720);
    }

    [Fact]
    public async Task A_vendor_stated_size_wins_over_the_sniffed_one()
    {
        FalAiImageClient client = Client(
            "{\"images\":[{\"url\":\"https://fal.media/a.png\",\"width\":2560,\"height\":1440}]}", out _, out _);

        GeneratedImageSet set = await client.GenerateAsync(
            new ImageGenerationRequest("ideogram/v4.5", "x"), CancellationToken.None);

        set.Images[0].Width.Should().Be(2560);
    }

    [Fact]
    public async Task A_result_with_no_image_is_a_failure_not_a_success()
    {
        FalAiImageClient client = Client("{\"images\":[]}", out _, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(
            new ImageGenerationRequest("ideogram/v4.5", "x"), CancellationToken.None));
    }

    // ================================================================ empty model = the catalogue default

    [Fact]
    public async Task An_empty_model_resolves_to_the_catalogue_default_for_each_operation()
    {
        (Func<FalAiImageClient, Task<GeneratedImageSet>> Call, string Expected)[] cases =
        [
            (c => c.GenerateAsync(new ImageGenerationRequest("", "a cat"), CancellationToken.None), "ideogram/v4.5"),
            (c => c.EditAsync(new ImageEditRequest("", "make it night", "https://s/a.png"), CancellationToken.None),
                "google/nano-banana-2.1/edit"),
            (c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Upscale, "", "https://s/a.png"), CancellationToken.None),
                "fal-ai/seedvr/upscale/image"),
            (c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Relight, "", "https://s/a.png"), CancellationToken.None),
                "fal-ai/image-apps-v2/relighting"),
            (c => c.TransformAsync(new ImageTransformRequest(ImageOperation.ReplaceBackground, "", "https://s/a.png", "a beach"), CancellationToken.None),
                "fal-ai/bria/background/replace"),
            (c => c.TransformAsync(new ImageTransformRequest(ImageOperation.Expand, "", "https://s/a.png",
                    Expand: new ImageExpandGeometry(100, 200, 0, 50, 100, 100)), CancellationToken.None),
                "fal-ai/bria/expand"),
        ];

        foreach ((Func<FalAiImageClient, Task<GeneratedImageSet>> call, string expected) in cases)
        {
            // seedvr and the Topaz upscalers answer { image }, everything else { images: [] }.
            FalAiImageClient client = Client(
                "{\"image\":{\"url\":\"https://fal.media/a.png\"},\"images\":[{\"url\":\"https://fal.media/a.png\"}]}",
                out StubHandler api, out _);
            GeneratedImageSet set = await call(client);
            api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/" + expected);
            set.ResolvedModel.Should().Be(expected);
        }
    }

    [Fact]
    public async Task An_empty_model_on_inpaint_picks_erase_for_the_neutral_prompt_and_fill_for_a_real_one()
    {
        FalAiImageClient erase = Client(OneImage, out StubHandler eraseApi, out _);
        await erase.InpaintAsync(new ImageInpaintRequest("", "background", "https://s/a.png", [], MaskUrl: "https://s/m.png"),
            CancellationToken.None);
        eraseApi.Requests[0].RequestUri!.AbsolutePath.Should().Be("/fal-ai/object-removal/mask");

        FalAiImageClient fill = Client(OneImage, out StubHandler fillApi, out _);
        await fill.InpaintAsync(new ImageInpaintRequest("", "a calm lake", "https://s/a.png", [], MaskUrl: "https://s/m.png"),
            CancellationToken.None);
        fillApi.Requests[0].RequestUri!.AbsolutePath.Should().Be("/fal-ai/flux-pro/v1/fill");
    }

    [Fact]
    public async Task An_explicit_model_with_no_read_schema_is_refused_not_guessed()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await Assert.ThrowsAsync<NotSupportedException>(() => client.GenerateAsync(
            new ImageGenerationRequest("fal-ai/not-read", "x"), CancellationToken.None));
        api.Requests.Should().BeEmpty("nothing may be sent to an endpoint whose schema has not been read");
    }

    [Fact]
    public async Task An_endpoint_for_another_job_is_refused()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.EditAsync(
            new ImageEditRequest("ideogram/v4.5", "x", "https://s/a.png"), CancellationToken.None));
        ex.Message.Should().Contain("Generate");
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ generation

    [Fact]
    public async Task Ideogram_45_sends_the_youtube_size_as_an_object_and_the_prompt_verbatim()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.GenerateAsync(
            new ImageGenerationRequest("ideogram/v4.5", "COME PERMETTERSI UNA M2", Width: 1280, Height: 720, Seed: 7),
            CancellationToken.None);

        Posts(api)[0].Should().Be(
            "{\"prompt\":\"COME PERMETTERSI UNA M2\",\"seed\":7,\"image_size\":{\"width\":1280,\"height\":720}}");
    }

    [Fact]
    public async Task Ideogram_45_refuses_a_size_outside_its_published_list()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateAsync(
            new ImageGenerationRequest("ideogram/v4.5", "x", Width: 1000, Height: 500), CancellationToken.None));
        ex.Message.Should().Contain("1280x720");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Ideogram_45_takes_a_size_preset_but_not_a_ratio_it_does_not_document()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.GenerateAsync(new ImageGenerationRequest("ideogram/v4.5", "x", AspectRatio: "landscape_16_9"),
            CancellationToken.None);
        Body(api).GetProperty("image_size").GetString().Should().Be("landscape_16_9");

        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateAsync(
            new ImageGenerationRequest("ideogram/v4.5", "x", AspectRatio: "16:9"), CancellationToken.None));
    }

    [Fact]
    public async Task Count_is_forwarded_as_num_images_within_the_schemas_range_and_refused_outside_it()
    {
        FalAiImageClient client = Client(
            "{\"images\":[{\"url\":\"https://fal.media/a.png\"},{\"url\":\"https://fal.media/b.png\"},{\"url\":\"https://fal.media/c.png\"}]}",
            out StubHandler api, out StubHandler media);

        GeneratedImageSet set = await client.GenerateAsync(
            new ImageGenerationRequest("ideogram/v4.5", "x", Count: 3), CancellationToken.None);

        Body(api).GetProperty("num_images").GetInt32().Should().Be(3);
        set.Images.Should().HaveCount(3);
        media.Requests.Should().HaveCount(3);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GenerateAsync(
            new ImageGenerationRequest("ideogram/v4.5", "x", Count: 9), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GenerateAsync(
            new ImageGenerationRequest("blackforestlabs/flux-3/text-to-image", "x", Count: 2), CancellationToken.None));
    }

    [Fact]
    public async Task A_text_to_image_route_refuses_a_reference_image()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateAsync(
            new ImageGenerationRequest("ideogram/v4.5", "x", ReferenceImageUrl: "https://s/ref.png"), CancellationToken.None));
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NanoBanana_21_text_to_image_takes_an_aspect_ratio_but_no_pixel_size()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.GenerateAsync(new ImageGenerationRequest("google/nano-banana-2.1", "a red car", AspectRatio: "16:9"),
            CancellationToken.None);
        Posts(api)[0].Should().Be("{\"prompt\":\"a red car\",\"aspect_ratio\":\"16:9\",\"output_format\":\"png\"}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateAsync(
            new ImageGenerationRequest("google/nano-banana-2.1", "a red car", Width: 1280, Height: 720), CancellationToken.None));
    }

    [Fact]
    public async Task The_transparent_element_endpoint_is_the_older_generation_and_validates_its_ratio()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.GenerateAsync(new ImageGenerationRequest(
            "fal-ai/ideogram/v3/generate-transparent", "a sticker", AspectRatio: "1:1",
            Params: P("{\"rendering_speed\":\"TURBO\"}")), CancellationToken.None);

        Posts(api)[0].Should().Be("{\"prompt\":\"a sticker\",\"aspect_ratio\":\"1:1\",\"rendering_speed\":\"TURBO\"}");
        FalAiImageClient.Catalog.Find("fal-ai/ideogram/v3/generate-transparent")!.IsOlderGeneration.Should().BeTrue();

        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateAsync(
            new ImageGenerationRequest("fal-ai/ideogram/v3/generate-transparent", "x", AspectRatio: "7:3"), CancellationToken.None));
    }

    [Fact]
    public async Task Flux_3_forwards_only_the_priced_tier_and_documents_no_seed()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.GenerateAsync(new ImageGenerationRequest("blackforestlabs/flux-3/text-to-image", "x"), CancellationToken.None);
        Posts(api)[0].Should().Be("{\"prompt\":\"x\",\"output_format\":\"png\"}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateAsync(
            new ImageGenerationRequest("blackforestlabs/flux-3/text-to-image", "x", Params: P("{\"resolution\":\"4k\"}")),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateAsync(
            new ImageGenerationRequest("blackforestlabs/flux-3/text-to-image", "x", Seed: 5), CancellationToken.None));
    }

    [Fact]
    public async Task Seedream_pins_the_lower_priced_size_tier()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.GenerateAsync(new ImageGenerationRequest("bytedance/seedream/v5/pro/text-to-image", "x"), CancellationToken.None);
        Posts(api)[0].Should().Be("{\"prompt\":\"x\",\"image_size\":\"auto_1K\",\"output_format\":\"png\"}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateAsync(
            new ImageGenerationRequest("bytedance/seedream/v5/pro/text-to-image", "x", Params: P("{\"image_size\":\"auto_2K\"}")),
            CancellationToken.None));
    }

    // ================================================================ instruction edit

    [Fact]
    public async Task NanoBanana_21_edit_sends_the_source_as_a_one_element_array_and_asks_for_png()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.EditAsync(new ImageEditRequest("google/nano-banana-2.1/edit", "make the sky purple",
            "https://s/a.png", Seed: 3), CancellationToken.None);

        Posts(api)[0].Should().Be(
            "{\"image_urls\":[\"https://s/a.png\"],\"prompt\":\"make the sky purple\",\"seed\":3,\"output_format\":\"png\"}");
    }

    [Fact]
    public async Task An_instruction_edit_with_a_reference_sends_both_pictures_source_first()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.EditAsync(new ImageEditRequest(
            "google/nano-banana-2.1/edit", "put the man from the third picture into the second",
            "https://s/a.png", ReferenceImageUrls: ["https://s/ref1.png", "https://s/ref2.png"]),
            CancellationToken.None);

        // The picture being EDITED stays first and the references keep the caller's order: every
        // documented prompt of these models reads the array positionally ("the second image"), so neither
        // the picture the user is working on nor the order they attached things in may shift.
        Posts(api)[0].Should().Be(
            "{\"image_urls\":[\"https://s/a.png\",\"https://s/ref1.png\",\"https://s/ref2.png\"],\"prompt\":\"put the man from the third picture into the second\",\"output_format\":\"png\"}");
    }

    [Fact]
    public async Task A_reference_is_refused_by_a_one_image_endpoint_which_names_one_that_takes_two()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.EditAsync(
            new ImageEditRequest("bria/fibo-edit/edit", "replace the face", "https://s/a.png",
                ReferenceImageUrls: ["https://s/ref.png"]), CancellationToken.None));

        ex.Message.Should().Contain("takes one image");
        ex.Message.Should().Contain("google/nano-banana-2.1/edit");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_enum_value_is_refused_because_the_vendor_would_silently_ignore_it()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => client.EditAsync(
            new ImageEditRequest("google/nano-banana-2.1/edit", "make it night", "https://s/a.png",
                Params: P("{\"resolution\":\"8K\"}")), CancellationToken.None));
        ex.Message.Should().Contain("SILENTLY IGNORES");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_field_the_schema_does_not_declare_is_refused_not_dropped()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => client.EditAsync(
            new ImageEditRequest("google/nano-banana-2.1/edit", "make it night", "https://s/a.png",
                Params: P("{\"enable_web_search\":true}")), CancellationToken.None));
        ex.Message.Should().Contain("not a field this client forwards");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_param_of_the_wrong_type_or_out_of_range_is_refused()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.EditAsync(
            new ImageEditRequest("fal-ai/qwen-image-edit-2511", "x", "https://s/a.png",
                Params: P("{\"num_inference_steps\":\"many\"}")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.EditAsync(
            new ImageEditRequest("fal-ai/qwen-image-edit-2511", "x", "https://s/a.png",
                Params: P("{\"num_inference_steps\":99}")), CancellationToken.None));
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_aspect_ratio_cannot_be_given_twice()
    {
        FalAiImageClient client = Client(OneImage, out _, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.EditAsync(
            new ImageEditRequest("google/nano-banana-2.1/edit", "make it night", "https://s/a.png", AspectRatio: "16:9",
                Params: P("{\"aspect_ratio\":\"1:1\"}")), CancellationToken.None));
    }

    [Fact]
    public async Task The_youtube_thumbnail_preset_refuses_a_blank_prompt_so_the_vendor_default_is_never_printed_on_it()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => client.EditAsync(
            new ImageEditRequest("fal-ai/image-editing/youtube-thumbnails", " ", "https://s/a.png"), CancellationToken.None));
        ex.Message.Should().Contain("Generate youtube thumbnails");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Gpt_image_25_refuses_auto_quality_because_the_vendor_publishes_no_price_for_it()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.EditAsync(new ImageEditRequest("openai/gpt-image-2.5/sunburst/edit", "x", "https://s/a.png",
            Params: P("{\"quality\":\"medium\"}")), CancellationToken.None);
        Body(api).GetProperty("quality").GetString().Should().Be("medium");

        await Assert.ThrowsAsync<ArgumentException>(() => client.EditAsync(
            new ImageEditRequest("openai/gpt-image-2.5/flare/edit", "x", "https://s/a.png",
                Params: P("{\"quality\":\"auto\"}")), CancellationToken.None));
    }

    [Fact]
    public async Task Fibo_erase_by_text_sends_the_prompt_as_object_name_and_reads_the_single_image()
    {
        FalAiImageClient client = Client("{\"image\":{\"url\":\"https://fal.media/a.png\"},\"vgl\":{}}", out StubHandler api, out _);

        GeneratedImageSet set = await client.EditAsync(
            new ImageEditRequest("bria/fibo-edit/erase_by_text", "the watermark", "https://s/a.png"), CancellationToken.None);

        Posts(api)[0].Should().Be("{\"image_url\":\"https://s/a.png\",\"object_name\":\"the watermark\"}");
        set.Images.Should().ContainSingle();
    }

    // ================================================================ mask inpainting

    [Fact]
    public async Task Erase_sends_the_mask_by_url_drops_the_neutral_prompt_and_asks_nothing_else()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.InpaintAsync(new ImageInpaintRequest("fal-ai/object-removal/mask", "background",
            "https://s/a.png", [], MaskUrl: "https://s/m.png"), CancellationToken.None);

        Posts(api)[0].Should().Be("{\"image_url\":\"https://s/a.png\",\"mask_url\":\"https://s/m.png\"}");
    }

    [Fact]
    public async Task Erase_refuses_a_real_instruction_it_cannot_honour()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.InpaintAsync(new ImageInpaintRequest(
            "fal-ai/object-removal/mask", "replace it with a dog", "https://s/a.png", [], MaskUrl: "https://s/m.png"),
            CancellationToken.None));
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Bytes_without_a_url_throw_a_sentence_that_names_the_fix()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.InpaintAsync(
            new ImageInpaintRequest("fal-ai/object-removal/mask", "", "https://s/a.png", [1, 2, 3]), CancellationToken.None));
        ex.Message.Should().Contain("MaskUrl");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_object_removal_tier_is_validated_and_aliased_as_quality()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.InpaintAsync(new ImageInpaintRequest("fal-ai/object-removal/mask", "", "https://s/a.png", [],
            MaskUrl: "https://s/m.png", Params: P("{\"quality\":\"low_quality\",\"mask_expansion\":20}")), CancellationToken.None);

        Body(api).GetProperty("model").GetString().Should().Be("low_quality");
        Body(api).GetProperty("mask_expansion").GetInt32().Should().Be(20);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.InpaintAsync(
            new ImageInpaintRequest("fal-ai/object-removal/mask", "", "https://s/a.png", [], MaskUrl: "https://s/m.png",
                Params: P("{\"mask_expansion\":51}")), CancellationToken.None));
    }

    [Fact]
    public async Task Fill_sends_the_prompt_and_png_and_assumes_white_acts_because_the_schema_does_not_say()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.InpaintAsync(new ImageInpaintRequest("fal-ai/flux-pro/v1/fill", "a calm lake",
            "https://s/a.png", [], MaskUrl: "https://s/m.png"), CancellationToken.None);

        Posts(api)[0].Should().Be(
            "{\"image_url\":\"https://s/a.png\",\"mask_url\":\"https://s/m.png\",\"prompt\":\"a calm lake\",\"output_format\":\"png\"}");
        FalAiImageClient.Catalog.Find("fal-ai/flux-pro/v1/fill")!.MaskPolarity.Should().Be(FalMaskPolarity.NotStated);
    }

    [Fact]
    public async Task A_fill_prompt_is_required()
    {
        FalAiImageClient client = Client(OneImage, out _, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.InpaintAsync(new ImageInpaintRequest(
            "fal-ai/flux-pro/v1/fill", "", "https://s/a.png", [], MaskUrl: "https://s/m.png"), CancellationToken.None));
    }

    [Fact]
    public async Task Ideogram_edit_states_the_opposite_polarity_and_is_refused_unless_the_mask_is_inverted()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        NotSupportedException refused = await Assert.ThrowsAsync<NotSupportedException>(() => client.InpaintAsync(
            new ImageInpaintRequest("ideogram/v4.5/edit", "a dog", "https://s/a.png", [], MaskUrl: "https://s/m.png"),
            CancellationToken.None));
        refused.Message.Should().Contain("white preserves");
        api.Requests.Should().BeEmpty("the wrong polarity must never be sent silently");

        await client.InpaintAsync(new ImageInpaintRequest("ideogram/v4.5/edit", "a dog", "https://s/a.png", [],
            MaskUrl: "https://s/inverted.png", MaskMeansPreserve: true), CancellationToken.None);
        Posts(api)[0].Should().Be(
            "{\"image_url\":\"https://s/a.png\",\"mask_url\":\"https://s/inverted.png\",\"prompt\":\"a dog\"}");
    }

    [Fact]
    public async Task A_white_acts_endpoint_refuses_an_inverted_mask()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await Assert.ThrowsAsync<NotSupportedException>(() => client.InpaintAsync(new ImageInpaintRequest(
            "fal-ai/flux-pro/v1/erase", "", "https://s/a.png", [], MaskUrl: "https://s/m.png", MaskMeansPreserve: true),
            CancellationToken.None));
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ transforms

    [Fact]
    public async Task Expand_maps_the_editors_margin_names_onto_the_schemas_and_needs_at_least_one()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.TransformAsync(new ImageTransformRequest(ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint",
            "https://s/a.png", Params: P("{\"top\":100,\"right\":0,\"bottom\":50,\"left\":2048}")), CancellationToken.None);

        Posts(api)[0].Should().Be(
            "{\"image_url\":\"https://s/a.png\",\"expand_top\":100,\"expand_right\":0,\"expand_bottom\":50,\"expand_left\":2048,\"output_format\":\"png\"}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint", "https://s/a.png", Params: P("{\"top\":0}")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint", "https://s/a.png", Params: P("{\"top\":2049}")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint", "https://s/a.png",
            Prompt: "more sky", Params: P("{\"top\":10}")), CancellationToken.None));
    }

    [Fact]
    public async Task Bria_expand_is_sent_the_canvas_the_original_size_and_its_position_as_width_height_and_x_y()
    {
        FalAiImageClient client = Client("{\"image\":{\"url\":\"https://fal.media/a.png\"},\"seed\":1}", out StubHandler api, out _);

        GeneratedImageSet set = await client.TransformAsync(new ImageTransformRequest(ImageOperation.Expand, "fal-ai/bria/expand",
            "https://s/a.jpg", Prompt: "a sunset sky", Seed: 5,
            Expand: new ImageExpandGeometry(3749, 6664, 0, 2082, 3749, 2499)), CancellationToken.None);

        Posts(api)[0].Should().Be(
            "{\"image_url\":\"https://s/a.jpg\",\"prompt\":\"a sunset sky\",\"seed\":5," +
            "\"canvas_size\":[3749,6664],\"original_image_size\":[3749,2499],\"original_image_location\":[0,2082]}");
        set.ResolvedModel.Should().Be("fal-ai/bria/expand");
    }

    [Fact]
    public async Task Bria_expand_refuses_a_canvas_of_5000x5000_or_more_and_a_call_without_geometry_before_sending()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Expand, "fal-ai/bria/expand", "https://s/a.png",
            Expand: new ImageExpandGeometry(5000, 5000, 0, 0, 5000, 4000)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Expand, "fal-ai/bria/expand", "https://s/a.png", Params: P("{\"top\":100}")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Expand, "fal-ai/bria/expand", "https://s/a.png", Params: P("{\"aspect_ratio\":\"9:16\"}"),
            Expand: new ImageExpandGeometry(100, 200, 0, 50, 100, 100)), CancellationToken.None));
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_planned_geometry_on_flux_outpaint_becomes_its_margins_and_its_4_megapixel_limits_are_enforced()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.TransformAsync(new ImageTransformRequest(ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint",
            "https://s/copy.png", Params: P("{\"mode\":\"fast\"}"),
            Expand: new ImageExpandGeometry(1186, 2108, 0, 658, 1186, 791)), CancellationToken.None);
        Posts(api)[0].Should().Be(
            "{\"image_url\":\"https://s/copy.png\",\"expand_top\":658,\"expand_bottom\":659,\"mode\":\"fast\",\"output_format\":\"png\"}");

        // the request that failed on staging: the 5931x3954 original with 2048 px top and bottom (48 MP out)
        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint", "https://s/a.jpg",
            Expand: new ImageExpandGeometry(5931, 8050, 0, 2048, 5931, 3954)), CancellationToken.None));
        // an input over 4 MP, even with a small canvas growth
        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint", "https://s/a.jpg",
            Expand: new ImageExpandGeometry(2100, 2000, 100, 0, 2000, 2000)), CancellationToken.None));
        // margins in Params as well as the planned geometry
        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint", "https://s/a.png", Params: P("{\"top\":10}"),
            Expand: new ImageExpandGeometry(100, 200, 0, 50, 100, 100)), CancellationToken.None));
        api.Requests.Where(r => r.Method == HttpMethod.Post).Should().HaveCount(1);
    }

    [Fact]
    public void Describe_says_which_expand_endpoint_takes_a_prompt_and_what_its_limits_are()
    {
        FalAiImageClient client = new(new HttpClient(), "k");

        ImageModelCapabilities bria = client.Describe("fal-ai/bria/expand");
        bria.TakesPrompt.Should().BeTrue();
        bria.Expand!.Geometry.Should().Be("canvas");
        bria.Expand.MaxOutputPixels.Should().Be(25_000_000);
        bria.Expand.MaxInputPixels.Should().BeNull("Bria documents no input limit, and none is invented");
        bria.Expand.MaxMarginPx.Should().BeNull();

        ImageModelCapabilities flux = client.Describe("fal-ai/flux-2-pro/outpaint");
        flux.TakesPrompt.Should().BeFalse();
        flux.Expand!.Geometry.Should().Be("margins");
        flux.Expand.MaxMarginPx.Should().Be(2048);
        flux.Expand.MaxOutputPixels.Should().Be(2048L * 2048);
        flux.Expand.Basis.Should().Contain("help.bfl.ai");

        client.Describe("google/nano-banana-2.1/edit").Expand.Should().BeNull();
    }

    [Fact]
    public async Task The_same_margin_under_both_of_its_names_is_refused()
    {
        FalAiImageClient client = Client(OneImage, out _, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Expand, "fal-ai/flux-2-pro/outpaint", "https://s/a.png",
            Params: P("{\"top\":10,\"expand_top\":20}")), CancellationToken.None));
    }

    [Fact]
    public async Task Background_replace_takes_a_prompt_or_a_reference_image_but_not_both_or_neither()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.TransformAsync(new ImageTransformRequest(ImageOperation.ReplaceBackground,
            "fal-ai/bria/background/replace", "https://s/a.png", "a beach at dusk", "people", Seed: 5), CancellationToken.None);
        Posts(api)[0].Should().Be(
            "{\"image_url\":\"https://s/a.png\",\"prompt\":\"a beach at dusk\",\"negative_prompt\":\"people\",\"seed\":5}");

        await client.TransformAsync(new ImageTransformRequest(ImageOperation.ReplaceBackground,
            "fal-ai/bria/background/replace", "https://s/a.png", Params: P("{\"ref_image_url\":\"https://s/ref.png\"}")),
            CancellationToken.None);
        Posts(api)[1].Should().Be("{\"image_url\":\"https://s/a.png\",\"ref_image_url\":\"https://s/ref.png\"}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.ReplaceBackground, "fal-ai/bria/background/replace", "https://s/a.png"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.ReplaceBackground, "fal-ai/bria/background/replace", "https://s/a.png", "x",
            Params: P("{\"ref_image_url\":\"https://s/ref.png\"}")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.ReplaceBackground, "fal-ai/bria/background/replace", "https://s/a.png", "x", Seed: -1), CancellationToken.None));
    }

    [Fact]
    public async Task Relighting_wraps_the_ratio_validates_the_style_and_exposes_the_style_enum_through_Describe()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.TransformAsync(new ImageTransformRequest(ImageOperation.Relight, "fal-ai/image-apps-v2/relighting",
            "https://s/a.png", Params: P("{\"lighting_style\":\"golden_hour\",\"aspect_ratio\":\"16:9\"}")), CancellationToken.None);
        Posts(api)[0].Should().Be(
            "{\"image_url\":\"https://s/a.png\",\"lighting_style\":\"golden_hour\",\"aspect_ratio\":{\"ratio\":\"16:9\"}}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Relight, "fal-ai/image-apps-v2/relighting", "https://s/a.png",
            Params: P("{\"lighting_style\":\"disco\"}")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Relight, "fal-ai/image-apps-v2/relighting", "https://s/a.png", "warm light"), CancellationToken.None));

        ImageModelCapabilities described = client.Describe("fal-ai/image-apps-v2/relighting");
        described.Options.Should().NotBeNull();
        described.Options!["lighting_style"].Should().HaveCount(18).And.Contain("rim_light");
    }

    [Fact]
    public async Task IcLight_is_the_prompt_driven_relight_alternative()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        await client.TransformAsync(new ImageTransformRequest(ImageOperation.Relight, "fal-ai/iclight-v2",
            "https://s/a.png", "rim light from the left", Params: P("{\"initial_latent\":\"Left\"}")), CancellationToken.None);

        Posts(api)[0].Should().Be(
            "{\"image_url\":\"https://s/a.png\",\"prompt\":\"rim light from the left\",\"initial_latent\":\"Left\",\"output_format\":\"png\"}");
        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Relight, "fal-ai/iclight-v2", "https://s/a.png"), CancellationToken.None));
    }

    [Fact]
    public async Task Fibo_relight_requires_both_of_its_enums()
    {
        FalAiImageClient client = Client("{\"image\":{\"url\":\"https://fal.media/a.png\"}}", out StubHandler api, out _);

        await client.TransformAsync(new ImageTransformRequest(ImageOperation.Relight, "bria/fibo-edit/relight",
            "https://s/a.png", Params: P("{\"light_type\":\"blue hour light\",\"light_direction\":\"top-down\"}")),
            CancellationToken.None);
        Posts(api)[0].Should().Be(
            "{\"image_url\":\"https://s/a.png\",\"light_type\":\"blue hour light\",\"light_direction\":\"top-down\"}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Relight, "bria/fibo-edit/relight", "https://s/a.png",
            Params: P("{\"light_type\":\"midday\"}")), CancellationToken.None));
    }

    [Fact]
    public async Task Upscale_maps_factor_and_reads_the_single_image_output()
    {
        FalAiImageClient client = Client("{\"image\":{\"url\":\"https://fal.media/a.png\"},\"seed\":1}", out StubHandler api, out _);

        GeneratedImageSet set = await client.TransformAsync(new ImageTransformRequest(ImageOperation.Upscale,
            "fal-ai/seedvr/upscale/image", "https://s/a.png", Params: P("{\"factor\":2}")), CancellationToken.None);

        Posts(api)[0].Should().Be("{\"image_url\":\"https://s/a.png\",\"upscale_factor\":2,\"output_format\":\"png\"}");
        set.Images.Should().ContainSingle();
    }

    [Fact]
    public async Task Upscale_modes_must_agree_with_the_fields_given()
    {
        FalAiImageClient client = Client("{\"image\":{\"url\":\"https://fal.media/a.png\"}}", out _, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Upscale, "fal-ai/seedvr/upscale/image", "https://s/a.png",
            Params: P("{\"target_resolution\":\"1440p\"}")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Upscale, "fal-ai/seedvr/upscale/image", "https://s/a.png",
            Params: P("{\"upscale_mode\":\"target\",\"factor\":2}")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Upscale, "fal-ai/seedvr/upscale/image", "https://s/a.png",
            Params: P("{\"factor\":11}")), CancellationToken.None));
    }

    [Fact]
    public async Task Topaz_precision_and_generative_validate_their_model_enums()
    {
        FalAiImageClient client = Client("{\"image\":{\"url\":\"https://fal.media/a.png\"}}", out StubHandler api, out _);

        await client.TransformAsync(new ImageTransformRequest(ImageOperation.Upscale, "topaz/upscale/image/precision",
            "https://s/a.png", Params: P("{\"model\":\"Text Refine\",\"factor\":2}")), CancellationToken.None);
        Posts(api)[0].Should().Be(
            "{\"image_url\":\"https://s/a.png\",\"model\":\"Text Refine\",\"upscale_factor\":2,\"output_format\":\"png\"}");

        await Assert.ThrowsAsync<ArgumentException>(() => client.TransformAsync(new ImageTransformRequest(
            ImageOperation.Upscale, "topaz/upscale/image/generative", "https://s/a.png",
            Params: P("{\"model\":\"Text Refine\"}")), CancellationToken.None));
        await client.TransformAsync(new ImageTransformRequest(ImageOperation.Upscale, "topaz/upscale/image/generative",
            "https://s/a.png", Params: P("{\"model\":\"Wonder 3.5\"}")), CancellationToken.None);
        Body(api).GetProperty("model").GetString().Should().Be("Wonder 3.5");
    }

    [Theory]
    [InlineData("fal-ai/seedvr/upscale/image")]
    [InlineData("topaz/upscale/image/precision")]
    [InlineData("topaz/upscale/image/generative")]
    public async Task Every_picture_upscaler_refuses_a_factor_of_3_in_plain_words_before_sending(string endpoint)
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        ArgumentOutOfRangeException refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.TransformAsync(
            new ImageTransformRequest(ImageOperation.Upscale, endpoint, "https://s/a.png", Params: P("{\"factor\":3}")),
            CancellationToken.None));

        refused.Message.Should().Be("The upscale factor can be at most 2.");
        Posts(api).Should().BeEmpty("a refused factor never reaches the vendor");
    }

    [Theory]
    [InlineData("fal-ai/seedvr/upscale/image")]
    [InlineData("topaz/upscale/image/precision")]
    [InlineData("topaz/upscale/image/generative")]
    public async Task Every_picture_upscaler_accepts_a_factor_of_2(string endpoint)
    {
        FalAiImageClient client = Client("{\"image\":{\"url\":\"https://fal.media/a.png\"},\"seed\":1}", out StubHandler api, out _);

        GeneratedImageSet set = await client.TransformAsync(
            new ImageTransformRequest(ImageOperation.Upscale, endpoint, "https://s/a.png", Params: P("{\"factor\":2}")),
            CancellationToken.None);

        Body(api).GetProperty("upscale_factor").GetDouble().Should().Be(2);
        set.Images.Should().ContainSingle();
    }

    [Fact]
    public async Task A_transform_endpoint_for_another_operation_is_refused()
    {
        FalAiImageClient client = Client(OneImage, out StubHandler api, out _);

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.TransformAsync(
            new ImageTransformRequest(ImageOperation.Upscale, "fal-ai/iclight-v2", "https://s/a.png"), CancellationToken.None));
        ex.Message.Should().Contain("Relight");
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ the probe

    [Fact]
    public async Task The_probe_submits_the_rows_own_catalogued_model_with_an_empty_body()
    {
        StubHandler api = new(_ => Json("{\"detail\":\"image_url is required\"}", HttpStatusCode.UnprocessableEntity));
        FalAiImageClient client = new(
            new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "k",
            probeModel: "google/nano-banana-2.1/edit");

        ImageProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.IsOk.Should().BeTrue();
        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/google/nano-banana-2.1/edit");
        Posts(api)[0].Should().Be("{}", "an empty body cannot satisfy a required field, so the probe cannot generate or bill");
    }

    [Fact]
    public async Task The_probe_fails_a_model_outside_the_catalogue_without_sending_anything()
    {
        StubHandler api = new(_ => Json("{}"));
        FalAiImageClient client = new(
            new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "k", probeModel: "fal-ai/not-read");

        ImageProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.IsOk.Should().BeFalse();
        probe.Reason.Should().Contain("not an endpoint this client has a read schema for");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_probe_reports_a_rejected_key_and_fails_closed_on_a_2xx_without_queue_urls()
    {
        FalAiImageClient rejected = new(
            new HttpClient(new StubHandler(_ => Json("{\"detail\":\"Unauthorized\"}", HttpStatusCode.Unauthorized)))
            { BaseAddress = new Uri("https://queue.fal.run") }, "k", probeModel: "ideogram/v4.5");
        (await rejected.ProbeAsync(CancellationToken.None)).IsOk.Should().BeFalse();

        FalAiImageClient accepted = new(
            new HttpClient(new StubHandler(_ => Json("{\"request_id\":\"x\"}")))
            { BaseAddress = new Uri("https://queue.fal.run") }, "k", probeModel: "ideogram/v4.5");
        ImageProbeResult probe = await accepted.ProbeAsync(CancellationToken.None);
        probe.IsOk.Should().BeFalse();
        probe.Reason.Should().Contain("no status_url/response_url");
    }

    // ================================================================ Describe and the billing key

    [Fact]
    public void Describe_reports_what_each_schema_says_and_claims_nothing_for_an_unread_model()
    {
        FalAiImageClient client = Client(OneImage, out _, out _);

        ImageModelCapabilities edit = client.Describe("google/nano-banana-2.1/edit");
        edit.SupportsInstructionEdit.Should().BeTrue();
        edit.SupportsMaskInpaint.Should().BeFalse();
        edit.MaxPromptLength.Should().Be(50000);

        ImageModelCapabilities fill = client.Describe("fal-ai/flux-pro/v1/fill");
        fill.SupportsMaskInpaint.Should().BeTrue();
        fill.Basis.Should().Contain("NotStated");

        ImageModelCapabilities gen = client.Describe("ideogram/v4.5");
        gen.MaxImagesPerRequest.Should().Be(8);
        gen.Options!["image_size"].Should().Contain("landscape_16_9");

        ImageModelCapabilities unknown = client.Describe("fal-ai/nope");
        unknown.SupportsInstructionEdit.Should().BeFalse();
        unknown.Basis.Should().Contain("unverified");
    }

    [Fact]
    public void The_billing_key_names_the_tier_that_ran_and_defaults_to_the_vendors_default_tier()
    {
        FalAiImageClient client = Client(OneImage, out _, out _);

        client.BillingModelKey("ideogram/v4.5", null).Should().Be("ideogram/v4.5#medium");
        client.BillingModelKey("ideogram/v4.5", P("{\"quality\":\"high\"}")).Should().Be("ideogram/v4.5#high");
        client.BillingModelKey("fal-ai/object-removal/mask", P("{\"quality\":\"low_quality\"}"))
            .Should().Be("fal-ai/object-removal/mask#low_quality", "the editor's alias maps onto the same tier field");
        client.BillingModelKey("fal-ai/object-removal/mask", null).Should().Be("fal-ai/object-removal/mask#best_quality");
        client.BillingModelKey("google/nano-banana-2.1/edit", P("{\"resolution\":\"4K\"}")).Should().Be("google/nano-banana-2.1/edit#4K");
        client.BillingModelKey("fal-ai/flux-pro/v1/fill", null).Should().Be("fal-ai/flux-pro/v1/fill", "no tier, no suffix");
        client.BillingModelKey("something/else", null).Should().Be("something/else", "total: an unknown model is returned as given");
    }
}
