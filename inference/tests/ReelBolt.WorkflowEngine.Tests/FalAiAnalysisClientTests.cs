#nullable enable
using System;
using System.Collections.Generic;
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
/// The fal.ai segmentation and depth arm, driven against a MOCKED HTTP layer. Nothing here touches the
/// live queue: the whole point is that these endpoints are paid, and a field name fal silently ignores
/// would look like a working request until someone compared the output to what they asked for.
/// </summary>
public class FalAiAnalysisClientTests
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

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

    /// <summary>
    /// A queue that answers the submit, the poll and the result, in that order, and fetches the media
    /// through the download client. Mirrors fal's documented lifecycle exactly.
    /// </summary>
    private static FalAiAnalysisClient Client(
        string resultJson,
        out StubHandler api,
        out StubHandler media,
        HttpStatusCode submitStatus = HttpStatusCode.OK,
        string submitBody = "",
        string statusBody = "",
        string pollStatus = "COMPLETED")
    {
        string submit = string.IsNullOrEmpty(submitBody)
            ? "{\"request_id\":\"req-1\",\"status_url\":\"https://queue.fal.run/m/requests/req-1/status\",\"response_url\":\"https://queue.fal.run/m/requests/req-1\"}"
            : submitBody;
        string status = string.IsNullOrEmpty(statusBody) ? "{\"status\":\"" + pollStatus + "\"}" : statusBody;

        int pollCount = 0;
        StubHandler apiHandler = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(submit, submitStatus);
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
            {
                pollCount++;
                return Json(status);
            }

            return Json(resultJson);
        });

        StubHandler mediaHandler = new(_ => Bytes(PngBytes));

        api = apiHandler;
        media = mediaHandler;

        HttpClient apiClient = new(apiHandler) { BaseAddress = new Uri("https://queue.fal.run") };
        HttpClient mediaClient = new(mediaHandler) { BaseAddress = new Uri("https://fal.media") };

        return new FalAiAnalysisClient(
            apiClient, "fal-key", mediaClient,
            pollInterval: TimeSpan.Zero,
            delay: (_, _) => Task.CompletedTask,
            probeModel: FalAiAnalysisClient.EvfSam);
    }

    // ================================================================ the queue contract

    [Fact]
    public async Task Submits_to_the_model_path_with_a_Key_authorization_header_and_polls_the_returned_urls()
    {
        FalAiAnalysisClient client = Client(
            "{\"image\":{\"url\":\"https://fal.media/a.png\",\"width\":10,\"height\":20}}",
            out StubHandler api, out StubHandler media);

        await client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.EvfSam, "https://files.example.com/src.png", Prompt: "the person"),
            CancellationToken.None);

        api.Requests[0].Method.Should().Be(HttpMethod.Post);
        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/fal-ai/evf-sam",
            "fal selects the endpoint by URL path");
        api.Requests[0].Headers.GetValues("Authorization").Should().ContainSingle().Which
            .Should().Be("Key fal-key", "fal authenticates with 'Key <FAL_KEY>', NOT a bearer token");

        api.Requests[1].Method.Should().Be(HttpMethod.Get);
        api.Requests[1].RequestUri!.ToString()
            .Should().Be("https://queue.fal.run/m/requests/req-1/status", "the returned status_url is the one to poll");
        api.Requests[2].RequestUri!.ToString()
            .Should().Be("https://queue.fal.run/m/requests/req-1", "and the returned response_url the one to fetch");

        media.Requests.Should().ContainSingle("the media must be downloaded, not handed on as a link");
    }

    [Fact]
    public async Task Keeps_polling_while_the_job_is_queued_or_running()
    {
        int calls = 0;
        StubHandler api = new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("{\"request_id\":\"r\",\"status_url\":\"https://queue.fal.run/m/requests/r/status\",\"response_url\":\"https://queue.fal.run/m/requests/r\"}");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
            {
                calls++;
                return Json(calls < 3 ? "{\"status\":\"IN_QUEUE\"}" : "{\"status\":\"COMPLETED\"}");
            }

            return Json("{\"image\":{\"url\":\"https://fal.media/a.png\"}}");
        });

        FalAiAnalysisClient client = new(
            new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") },
            "k",
            new HttpClient(new StubHandler(_ => Bytes(PngBytes))) { BaseAddress = new Uri("https://fal.media") },
            pollInterval: TimeSpan.Zero,
            delay: (_, _) => Task.CompletedTask);

        await client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.EvfSam, "https://x.example.com/a.png", Prompt: "x"),
            CancellationToken.None);

        calls.Should().Be(3);
    }

    [Fact]
    public async Task An_unrecognised_queue_status_fails_loudly_instead_of_polling_forever()
    {
        FalAiAnalysisClient client = Client(
            "{}", out _, out _, statusBody: "{\"status\":\"SOMETHING_NEW\"}");

        await Assert.ThrowsAsync<NotSupportedException>(() => client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.EvfSam, "https://x.example.com/a.png", Prompt: "x"),
            CancellationToken.None));
    }

    // ================================================================ per-endpoint schemas

    [Fact]
    public async Task EvfSam_sends_the_text_prompt_its_schema_requires()
    {
        FalAiAnalysisClient client = Client(
            "{\"image\":{\"url\":\"https://fal.media/a.png\"}}", out StubHandler api, out _);

        await client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.EvfSam, "https://x.example.com/a.png",
                Prompt: "the dog", NegativePrompt: "the collar", SemanticParts: true),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(api.Bodies[0]);
        body.RootElement.GetProperty("prompt").GetString().Should().Be("the dog");
        body.RootElement.GetProperty("negative_prompt").GetString().Should().Be("the collar");
        body.RootElement.GetProperty("semantic_type").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("image_url").GetString().Should().Be("https://x.example.com/a.png");
    }

    [Fact]
    public async Task EvfSam_refuses_a_missing_prompt_because_its_schema_marks_it_required()
    {
        FalAiAnalysisClient client = Client("{}", out _, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.EvfSam, "https://x.example.com/a.png"),
            CancellationToken.None));
    }

    [Fact]
    public async Task Sam2_requires_boxes_because_it_is_promptable_and_has_no_text_prompt()
    {
        FalAiAnalysisClient client = Client("{}", out _, out _);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.Sam2Image, "https://x.example.com/a.png"),
            CancellationToken.None));
    }

    [Fact]
    public async Task Sam2_sends_box_prompts_verbatim()
    {
        FalAiAnalysisClient client = Client(
            "{\"image\":{\"url\":\"https://fal.media/a.png\"}}", out StubHandler api, out _);

        await client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.Sam2Image, "https://x.example.com/a.png",
                Boxes: [new NormalizedBox(10, 20, 300, 400)]),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(api.Bodies[0]);
        JsonElement box = body.RootElement.GetProperty("box_prompts")[0];
        box.GetProperty("x_min").GetInt32().Should().Be(10);
        box.GetProperty("y_max").GetInt32().Should().Be(400);
        box.GetProperty("frame_index").GetInt32().Should().Be(0);
        body.RootElement.GetProperty("apply_mask").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("output_format").GetString().Should().Be("png");
    }

    [Fact]
    public async Task Birefnet_returns_the_cutout_and_the_separate_matte()
    {
        FalAiAnalysisClient client = Client(
            "{\"image\":{\"url\":\"https://fal.media/a.png\"},\"mask_image\":{\"url\":\"https://fal.media/m.png\"}}",
            out StubHandler api, out StubHandler media);

        SegmentationResult result = await client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.BirefnetV2, "https://x.example.com/a.png",
                Variant: "Matting"),
            CancellationToken.None);

        result.Cutout.Bytes.Should().Equal(PngBytes);
        result.Matte.Should().NotBeNull("birefnet/v2 is the one segmentation endpoint with a SEPARATE matte output");
        media.Requests.Should().HaveCount(2);

        using JsonDocument body = JsonDocument.Parse(api.Bodies[0]);
        body.RootElement.GetProperty("model").GetString().Should().Be("Matting");
        body.RootElement.GetProperty("output_mask").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("output_format").GetString().Should().Be("png");
    }

    [Fact]
    public async Task Birefnet_refuses_an_undocumented_variant_rather_than_letting_the_vendor_ignore_it()
    {
        FalAiAnalysisClient client = Client("{}", out _, out _);

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.BirefnetV2, "https://x.example.com/a.png",
                Variant: "General Use (Enormous)"),
            CancellationToken.None));

        ex.Message.Should().Contain("General Use (Light)");
    }

    [Fact]
    public async Task A_field_an_endpoint_does_not_document_is_refused_rather_than_dropped()
    {
        FalAiAnalysisClient client = Client("{}", out _, out _);

        // birefnet documents no text prompt.
        await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.BirefnetV2, "https://x.example.com/a.png", Prompt: "the dog"),
            CancellationToken.None));

        // evf-sam documents no separate matte output.
        await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentAsync(
            new SegmentationRequest(FalAiAnalysisClient.EvfSam, "https://x.example.com/a.png",
                Prompt: "the dog", MatteToo: true),
            CancellationToken.None));
    }

    [Fact]
    public async Task An_endpoint_with_no_read_schema_is_refused_not_guessed()
    {
        FalAiAnalysisClient client = Client("{}", out _, out _);

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.SegmentAsync(
            new SegmentationRequest("fal-ai/some-other-segmenter", "https://x.example.com/a.png", Prompt: "x"),
            CancellationToken.None));

        ex.Message.Should().Contain("fal-ai/evf-sam", "the refusal names the endpoints that DO have a verified schema");
    }

    // ================================================================ depth

    [Fact]
    public async Task Depth_sends_only_the_source_image_to_the_plain_endpoint()
    {
        FalAiAnalysisClient client = Client(
            "{\"image\":{\"url\":\"https://fal.media/d.png\"}}", out StubHandler api, out _);

        DepthResult result = await client.EstimateDepthAsync(
            new DepthRequest(FalAiAnalysisClient.Depth, "https://x.example.com/a.png"), CancellationToken.None);

        result.DepthMap.Bytes.Should().Equal(PngBytes);
        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/fal-ai/imageutils/depth");
        using JsonDocument body = JsonDocument.Parse(api.Bodies[0]);
        body.RootElement.EnumerateObject().Should().ContainSingle("only image_url is documented as required");
    }

    [Fact]
    public async Task Marigold_takes_its_documented_quality_knobs_and_the_plain_endpoint_rejects_them()
    {
        FalAiAnalysisClient client = Client(
            "{\"image\":{\"url\":\"https://fal.media/d.png\"}}", out StubHandler api, out _);

        await client.EstimateDepthAsync(
            new DepthRequest(FalAiAnalysisClient.MarigoldDepth, "https://x.example.com/a.png",
                ProcessingResolution: 768, InferenceSteps: 20, EnsembleSize: 5),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(api.Bodies[0]);
        body.RootElement.GetProperty("processing_res").GetInt32().Should().Be(768);
        body.RootElement.GetProperty("num_inference_steps").GetInt32().Should().Be(20);
        body.RootElement.GetProperty("ensemble_size").GetInt32().Should().Be(5);

        await Assert.ThrowsAsync<ArgumentException>(() => client.EstimateDepthAsync(
            new DepthRequest(FalAiAnalysisClient.Depth, "https://x.example.com/a.png", InferenceSteps: 20),
            CancellationToken.None));
    }

    [Fact]
    public void Describe_reports_only_what_each_schema_says()
    {
        FalAiAnalysisClient client = new(new HttpClient(), "k");

        ImageAnalysisModelCapabilities evf = client.Describe(FalAiAnalysisClient.EvfSam);
        evf.TakesTextPrompt.Should().BeTrue();
        evf.TakesBoxPrompts.Should().BeFalse();
        evf.CanReturnMatte.Should().BeFalse("evf-sam documents a single image output");

        ImageAnalysisModelCapabilities birefnet = client.Describe(FalAiAnalysisClient.BirefnetV2);
        birefnet.TakesTextPrompt.Should().BeFalse();
        birefnet.CanReturnMatte.Should().BeTrue();

        ImageAnalysisModelCapabilities unknown = client.Describe("fal-ai/nope");
        unknown.TakesTextPrompt.Should().BeFalse();
        unknown.Basis.Should().Contain("unverified", "an unread schema claims nothing");
    }

    // ================================================================ probe

    [Fact]
    public async Task The_probe_submits_the_rows_own_model_with_an_empty_body()
    {
        StubHandler api = new(_ => Json("{\"detail\":\"image_url is required\"}", HttpStatusCode.UnprocessableEntity));
        FalAiAnalysisClient client = new(
            new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "k",
            probeModel: FalAiAnalysisClient.BirefnetV2);

        ImageAnalysisProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.IsOk.Should().BeTrue();
        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/fal-ai/birefnet/v2");
        api.Bodies[0].Should().Be("{}",
            "an empty body cannot satisfy the schema's required image_url, so the probe cannot generate or bill");
        api.Requests[0].Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Key k");
    }

    [Fact]
    public async Task The_probe_reports_a_model_id_fal_does_not_route()
    {
        StubHandler api = new(_ => Json("{\"detail\":\"Not Found\"}", HttpStatusCode.NotFound));
        FalAiAnalysisClient client = new(
            new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "k", probeModel: "fal-ai/typo");

        ImageAnalysisProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.IsOk.Should().BeFalse();
        probe.Reason.Should().Contain("does not route");
    }

    [Fact]
    public async Task The_probe_reports_a_rejected_key()
    {
        StubHandler api = new(_ => Json("{\"detail\":\"Unauthorized\"}", HttpStatusCode.Unauthorized));
        FalAiAnalysisClient client = new(
            new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "k", probeModel: FalAiAnalysisClient.EvfSam);

        (await client.ProbeAsync(CancellationToken.None)).IsOk.Should().BeFalse();
    }

    [Fact]
    public async Task The_probe_follows_an_accepted_submit_and_passes_on_a_validation_error()
    {
        // Measured: fal's queue accepts an empty body and rejects it only when the job runs.
        StubHandler api = new(req =>
            req.Method == HttpMethod.Post
                ? Json("{\"request_id\":\"x\",\"status_url\":\"https://queue.fal.run/s\",\"response_url\":\"https://queue.fal.run/r\"}")
                : req.RequestUri!.AbsolutePath == "/s"
                    ? Json("{\"status\":\"COMPLETED\"}")
                    : Json("{\"detail\":[{\"loc\":[\"body\",\"image_url\"],\"type\":\"missing\"}]}", (HttpStatusCode)422));
        FalAiAnalysisClient client = new(
            new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "k", probeModel: FalAiAnalysisClient.EvfSam);

        (await client.ProbeAsync(CancellationToken.None)).IsOk.Should().BeTrue();
    }

    [Fact]
    public async Task The_probe_fails_closed_when_an_empty_request_actually_ran()
    {
        StubHandler api = new(req =>
            req.Method == HttpMethod.Post
                ? Json("{\"request_id\":\"x\",\"status_url\":\"https://queue.fal.run/s\",\"response_url\":\"https://queue.fal.run/r\"}")
                : req.RequestUri!.AbsolutePath == "/s"
                    ? Json("{\"status\":\"COMPLETED\"}")
                    : Json("{\"masks\":[]}"));
        FalAiAnalysisClient client = new(
            new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "k", probeModel: FalAiAnalysisClient.EvfSam);

        ImageAnalysisProbeResult probe = await client.ProbeAsync(CancellationToken.None);
        probe.IsOk.Should().BeFalse();
        probe.Reason.Should().Contain("proves nothing");
    }
}
