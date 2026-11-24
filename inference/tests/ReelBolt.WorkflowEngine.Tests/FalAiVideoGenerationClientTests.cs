#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The fal.ai video arm, driven against a MOCKED HTTP layer. Nothing here touches the live queue: no
/// credential exists, every endpoint is paid, and a field fal silently ignores would look like a working
/// request until someone watched the clip. Each test pins either the exact body sent for one endpoint,
/// the queue lifecycle, or a refusal that must happen BEFORE anything is sent.
/// </summary>
public class FalAiVideoGenerationClientTests
{
    /// <summary>Two bytes standing in for a PNG: enough to pin the base64 the vendor receives.</summary>
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47];

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

    private const string OneVideo = "{\"video\":{\"url\":\"https://fal.media/clip.mp4\",\"content_type\":\"video/mp4\"},\"seed\":7}";

    private const string Submit =
        "{\"request_id\":\"req-1\",\"status_url\":\"https://queue.fal.run/fal-ai/vidu/requests/req-1/status\"," +
        "\"response_url\":\"https://queue.fal.run/fal-ai/vidu/requests/req-1\"}";

    /// <summary>
    /// The queue as a stub: a POST answers <see cref="Submit"/>, a status GET walks the status sequence
    /// (the last one repeats), and the result URL answers the result body.
    /// </summary>
    private static StubHandler Queue(
        string result = OneVideo, HttpStatusCode resultStatus = HttpStatusCode.OK,
        string submit = Submit, params string[] statusSequence)
    {
        List<string> remaining = [.. statusSequence];
        return new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post) return Json(submit);
            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
            {
                string status = remaining.Count > 1 ? remaining[0] : remaining[^1];
                if (remaining.Count > 1) remaining.RemoveAt(0);
                return Json("{\"status\":\"" + status + "\",\"request_id\":\"req-1\"}");
            }

            return Json(result, resultStatus);
        });
    }

    private static FalAiVideoGenerationClient Client(StubHandler handler, string? probeModel = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://queue.fal.run") }, "fal-key",
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask, probeModel: probeModel);

    /// <summary>The bodies of the POSTs only - the poll GETs interleave with empty bodies.</summary>
    private static List<string> Posts(StubHandler api) =>
        [.. api.Requests.Select((r, i) => (r, i)).Where(t => t.r.Method == HttpMethod.Post).Select(t => api.Bodies[t.i])];

    private static JsonElement SubmitBody(StubHandler api) => JsonDocument.Parse(Posts(api).Single()).RootElement;

    /// <summary>A request carrying ONE first frame - the shape this wave exists for.</summary>
    private static VideoGenerationRequest Request(
        string model, string prompt = "a slow pan", int duration = 5, string resolution = "720p",
        string ratio = FalVideoCatalog.NativeToken, IReadOnlyList<VideoGenerationImageInput>? images = null) =>
        new(model, prompt, duration, resolution, ratio, images ?? FirstFrame());

    /// <summary>A request with NO image at all (a text-to-video take, or the keyframe-less path).</summary>
    private static VideoGenerationRequest NoFrame(
        string model, string prompt = "a slow pan", int duration = 5, string resolution = "720p",
        string ratio = FalVideoCatalog.NativeToken) =>
        new(model, prompt, duration, resolution, ratio, null);

    private static IReadOnlyList<VideoGenerationImageInput> FirstFrame() =>
        [new VideoGenerationImageInput("image/png", PngHeader)];

    private static string ExpectedDataUri => "data:image/png;base64," + Convert.ToBase64String(PngHeader);

    // ================================================================ the queue contract

    [Fact]
    public async Task Submits_to_the_endpoint_path_with_a_Key_header_as_a_POST()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        await client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        api.Requests.Should().ContainSingle();
        api.Requests[0].Method.Should().Be(HttpMethod.Post);
        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/fal-ai/vidu/q4/image-to-video");
        api.Requests[0].Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Key fal-key");
    }

    [Fact]
    public async Task The_handle_carries_the_request_id_and_the_vendors_own_tracking_urls()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        ExternalJobHandle handle = await client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        // The URLs cannot be rebuilt from the id: fal scopes them to the app, and the endpoint-shaped
        // URLs the vendor's documentation shows answer 405 (measured 2026-10-16).
        handle.RemoteJobId.Should().Be(
            "req-1|https://queue.fal.run/fal-ai/vidu/requests/req-1/status|https://queue.fal.run/fal-ai/vidu/requests/req-1");
    }

    [Fact]
    public async Task Poll_drives_IN_QUEUE_then_IN_PROGRESS_then_COMPLETED_and_fetches_the_video()
    {
        StubHandler api = Queue(statusSequence: ["IN_QUEUE", "IN_PROGRESS", "COMPLETED"]);
        FalAiVideoGenerationClient client = Client(api);
        ExternalJobHandle handle = await client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        (await client.PollAsync(handle, CancellationToken.None)).State.Should().Be(ExternalJobState.Queued);
        (await client.PollAsync(handle, CancellationToken.None)).State.Should().Be(ExternalJobState.Running);

        ExternalJobStatus finished = await client.PollAsync(handle, CancellationToken.None);

        finished.State.Should().Be(ExternalJobState.Succeeded);
        finished.DownloadUrl.Should().Be("https://fal.media/clip.mp4");
        // The status URL the vendor returned, and the result URL it returned - never a rebuilt one.
        api.Requests.Select(r => r.RequestUri!.ToString()).Should().Equal(
            "https://queue.fal.run/fal-ai/vidu/q4/image-to-video",
            "https://queue.fal.run/fal-ai/vidu/requests/req-1/status",
            "https://queue.fal.run/fal-ai/vidu/requests/req-1/status",
            "https://queue.fal.run/fal-ai/vidu/requests/req-1/status",
            "https://queue.fal.run/fal-ai/vidu/requests/req-1");
    }

    [Fact]
    public async Task An_unrecognised_queue_status_fails_loudly_rather_than_being_polled_forever()
    {
        StubHandler api = Queue(statusSequence: "PENDING_REVIEW");
        FalAiVideoGenerationClient client = Client(api);
        ExternalJobHandle handle = await client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(handle, CancellationToken.None);

        (await poll.Should().ThrowAsync<NotSupportedException>())
            .WithMessage("*unrecognised queue status 'PENDING_REVIEW'*");
    }

    [Fact]
    public async Task A_COMPLETED_job_that_carries_an_error_is_a_FAILED_take_not_a_delivery()
    {
        StubHandler api = new(request => request.Method == HttpMethod.Post
            ? Json(Submit)
            : Json("{\"status\":\"COMPLETED\",\"request_id\":\"req-1\",\"error\":\"content policy violation\"}"));
        FalAiVideoGenerationClient client = Client(api);
        ExternalJobHandle handle = await client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        ExternalJobStatus status = await client.PollAsync(handle, CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Failed);
        status.FailureReason.Should().Be("content policy violation");
        // The result was never fetched: there is none.
        api.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_4xx_on_the_result_route_is_a_refusal_and_fails_the_take_instead_of_keeping_it_charged()
    {
        StubHandler api = Queue(result: "{\"detail\":\"duration must be between 3 and 16\"}",
            resultStatus: HttpStatusCode.UnprocessableEntity, statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);
        ExternalJobHandle handle = await client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        ExternalJobStatus status = await client.PollAsync(handle, CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Failed);
        status.FailureReason.Should().Contain("HTTP 422").And.Contain("duration must be between 3 and 16");
    }

    [Fact]
    public async Task A_5xx_on_the_result_route_throws_because_whether_that_job_was_billed_is_unknown()
    {
        StubHandler api = Queue(result: "{\"detail\":\"upstream\"}",
            resultStatus: HttpStatusCode.BadGateway, statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);
        ExternalJobHandle handle = await client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(handle, CancellationToken.None);

        (await poll.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*HTTP 502*");
    }

    [Fact]
    public async Task A_200_that_carries_no_video_is_not_a_success()
    {
        StubHandler api = Queue(result: "{\"seed\":7}", statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);
        ExternalJobHandle handle = await client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(handle, CancellationToken.None);

        (await poll.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*no 'video.url'*");
    }

    [Fact]
    public async Task A_handle_this_client_did_not_write_is_refused_rather_than_polled_at_a_guessed_url()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> poll = () => client.PollAsync(new ExternalJobHandle("req-from-another-kind"), CancellationToken.None);

        (await poll.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*not a fal.ai job handle*");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_definite_submit_rejection_carries_its_status_so_the_reservation_can_be_released()
    {
        // VideoGenerateStepExecutor releases a take's budget reservation only for an HttpRequestException
        // carrying a 4xx (other than 408); anything else is "the vendor may have accepted it" and stays
        // charged. A submit is where that decision is made, so the status has to survive the throw.
        StubHandler api = new(_ => Json("{\"detail\":\"invalid key credentials\"}", HttpStatusCode.Unauthorized));
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        (await submit.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_5xx_on_submit_carries_its_status_too_and_5xx_is_not_a_definite_rejection()
    {
        StubHandler api = new(_ => Json("{\"detail\":\"upstream\"}", HttpStatusCode.BadGateway));
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        (await submit.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // ================================================================ the frames (the point of the wave)

    [Fact]
    public async Task The_first_frame_is_sent_as_the_data_uri_the_vendor_documents_on_ITS_field()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        await client.SubmitAsync(
            Request("fal-ai/vidu/q4/image-to-video", images: FirstFrame()), CancellationToken.None);

        JsonElement body = SubmitBody(api);
        body.GetProperty("image_url").GetString().Should().Be(ExpectedDataUri);
        body.GetProperty("prompt").GetString().Should().Be("a slow pan");
        body.GetProperty("duration").GetInt32().Should().Be(5);
        body.GetProperty("resolution").GetString().Should().Be("720p");
        // The endpoint documents no aspect-ratio field, so nothing is sent for the ratio.
        body.TryGetProperty("aspect_ratio", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_first_last_frame_endpoint_takes_its_OWN_field_names_and_both_frames()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);
        byte[] endBytes = [0xFF, 0xD8];
        VideoGenerationImageInput end = new("image/jpeg", endBytes);

        await client.SubmitAsync(Request("blackforestlabs/flux-3/first-last-frame-to-video", duration: 8,
            resolution: "1080p", ratio: "auto", images: [FirstFrame()[0], end]), CancellationToken.None);

        JsonElement body = SubmitBody(api);
        body.GetProperty("start_image_url").GetString().Should().Be(ExpectedDataUri);
        body.GetProperty("end_image_url").GetString().Should().Be("data:image/jpeg;base64," + Convert.ToBase64String(endBytes));
        body.GetProperty("duration").GetInt32().Should().Be(8);
        body.GetProperty("aspect_ratio").GetString().Should().Be("auto");
        body.TryGetProperty("image_url", out _).Should().BeFalse("this endpoint spells its first frame start_image_url");
    }

    [Fact]
    public async Task A_first_last_frame_endpoint_refuses_a_lone_frame_because_its_schema_requires_both()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(
            Request("blackforestlabs/flux-3/first-last-frame-to-video", duration: 8, resolution: "720p",
                ratio: "auto", images: FirstFrame()), CancellationToken.None);

        (await submit.Should().ThrowAsync<ArgumentException>()).WithMessage("*end-frame image is required*");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_endpoint_that_requires_a_first_frame_refuses_a_text_only_take()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(
            NoFrame("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        (await submit.Should().ThrowAsync<ArgumentException>()).WithMessage("*A first-frame image is required*");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_optional_image_endpoints_accept_a_text_only_take_and_send_no_image_field()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        await client.SubmitAsync(
            NoFrame("minimax/h3-max-turbo/image-to-video", resolution: "768P"), CancellationToken.None);

        JsonElement body = SubmitBody(api);
        body.TryGetProperty("image_url", out _).Should().BeFalse();
        body.TryGetProperty("end_image_url", out _).Should().BeFalse();
        // prompt_expansion_mode is REQUIRED by its schema with its own default: the client sends it.
        body.GetProperty("prompt_expansion_mode").GetString().Should().Be("balanced");
    }

    [Fact]
    public async Task A_text_to_video_route_refuses_an_image_rather_than_dropping_it()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(
            Request("fal-ai/kling-video/v3/turbo/pro/text-to-video", images: FirstFrame()), CancellationToken.None);

        (await submit.Should().ThrowAsync<ArgumentException>()).WithMessage("*text-to-video route and documents no image input*");
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ the duration codec (three vendors, three shapes)

    [Theory]
    // Kling spells a duration as a JSON STRING holding a number.
    [InlineData("fal-ai/kling-video/v3/turbo/pro/image-to-video", 5, "native", "native", "\"5\"", true)]
    // Vidu takes a JSON integer.
    [InlineData("fal-ai/vidu/q4/image-to-video", 5, "720p", "native", "5", false)]
    // veo takes a JSON string holding SECONDS.
    [InlineData("fal-ai/veo3.1/fast/image-to-video", 8, "720p", "auto", "\"8s\"", true)]
    // ... and its text-to-video route offers no "auto" ratio.
    [InlineData("fal-ai/veo3.1", 4, "720p", "16:9", "\"4s\"", true)]
    public async Task The_duration_is_sent_in_the_shape_the_schema_declares(
        string model, int duration, string resolution, string ratio, string expectedJson, bool isString)
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);
        bool textToVideo = FalAiVideoGenerationClient.Catalog.Find(model)!.FirstFrameField is null;

        await client.SubmitAsync(
            textToVideo
                ? NoFrame(model, duration: duration, resolution: resolution, ratio: ratio)
                : Request(model, duration: duration, resolution: resolution, ratio: ratio), CancellationToken.None);

        JsonElement body = SubmitBody(api);
        body.GetProperty("duration").GetRawText().Should().Be(expectedJson);
        body.GetProperty("duration").ValueKind.Should().Be(isString ? JsonValueKind.String : JsonValueKind.Number);
    }

    [Fact]
    public async Task A_duration_the_endpoints_closed_enum_does_not_offer_is_refused()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        // veo offers 4s, 6s and 8s - not 5s. The vendor silently ignores an unknown enum value.
        Func<Task> submit = () => client.SubmitAsync(
            NoFrame("fal-ai/veo3.1", duration: 5, ratio: "16:9"), CancellationToken.None);

        (await submit.Should().ThrowAsync<ArgumentException>()).WithMessage("*only takes these durations: 4s, 6s, 8s*");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_duration_outside_the_schemas_range_is_refused_before_anything_is_sent()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(
            Request("fal-ai/vidu/q4/image-to-video", duration: 17), CancellationToken.None);

        (await submit.Should().ThrowAsync<ArgumentOutOfRangeException>()).WithMessage("*duration from 3 to 16 seconds*");
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ the axes the vendor does not have

    [Fact]
    public async Task A_flat_priced_endpoint_with_no_resolution_field_sends_none_and_demands_the_native_token()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        await client.SubmitAsync(Request("fal-ai/kling-video/v3/turbo/pro/image-to-video",
            resolution: FalVideoCatalog.NativeToken), CancellationToken.None);

        JsonElement body = SubmitBody(api);
        body.TryGetProperty("resolution", out _).Should().BeFalse();
        body.TryGetProperty("aspect_ratio", out _).Should().BeFalse();

        Func<Task> wrong = () => client.SubmitAsync(
            Request("fal-ai/kling-video/v3/turbo/pro/image-to-video", resolution: "1080p"), CancellationToken.None);
        (await wrong.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*documents no resolution field at all*only resolution token it offers is 'native'*");
    }

    [Fact]
    public async Task An_endpoint_with_no_ratio_field_refuses_a_ratio_it_cannot_honour()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(
            Request("fal-ai/vidu/q4/image-to-video", ratio: "16:9"), CancellationToken.None);

        (await submit.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*documents no aspect-ratio field*only ratio token it offers is 'native'*");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resolution_the_schema_does_not_offer_is_refused_rather_than_silently_ignored()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(
            Request("fal-ai/vidu/q4/image-to-video", resolution: "2160p"), CancellationToken.None);

        (await submit.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*not a resolution*fal-ai/vidu/q4/image-to-video*documents: 540p, 720p, 1080p, 2K, 4K*");
    }

    [Fact]
    public async Task A_ratio_the_schema_does_not_offer_is_refused_rather_than_silently_ignored()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(
            NoFrame("fal-ai/kling-video/v3/turbo/pro/text-to-video", ratio: "21:9",
                resolution: FalVideoCatalog.NativeToken), CancellationToken.None);

        (await submit.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*not an aspect ratio*documents: 16:9, 9:16, 1:1*");
    }

    [Fact]
    public async Task An_endpoint_whose_schema_requires_a_prompt_refuses_an_empty_one()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(
            NoFrame("fal-ai/veo3.1", prompt: "   ", duration: 8, ratio: "16:9"), CancellationToken.None);

        (await submit.Should().ThrowAsync<ArgumentException>()).WithMessage("*A prompt is required*");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_model_with_no_schema_is_refused_before_anything_is_sent()
    {
        StubHandler api = Queue(statusSequence: "COMPLETED");
        FalAiVideoGenerationClient client = Client(api);

        Func<Task> submit = () => client.SubmitAsync(Request("fal-ai/not-a-video-endpoint"), CancellationToken.None);

        (await submit.Should().ThrowAsync<NotSupportedException>()).WithMessage("*No schema has been read*");
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ Describe (what the budget gate prices against)

    [Fact]
    public void Every_catalogued_endpoint_describes_a_positive_price_per_resolution_token()
    {
        FalAiVideoGenerationClient client = Client(new StubHandler(_ => Json("{}")));

        foreach (FalVideoEndpoint endpoint in FalAiVideoGenerationClient.Catalog.All)
        {
            VideoGenerationCapabilities capabilities = client.Describe(endpoint.Id);

            capabilities.PricePerSecondByResolution.Keys.Should().BeEquivalentTo(endpoint.Resolutions, endpoint.Id);
            capabilities.PricePerSecondByResolution.Values.Should().OnlyContain(usd => usd > 0m, endpoint.Id);
            capabilities.MinDurationSeconds.Should().Be(endpoint.MinDurationSeconds);
            capabilities.MaxDurationSeconds.Should().Be(endpoint.MaxDurationSeconds);
            capabilities.AllowedRatios.Should().BeEquivalentTo(endpoint.AllowedRatios, endpoint.Id);
        }
    }

    [Fact]
    public void A_flat_priced_endpoint_describes_the_native_token_as_its_one_price_tier()
    {
        FalAiVideoGenerationClient client = Client(new StubHandler(_ => Json("{}")));

        VideoGenerationCapabilities capabilities = client.Describe("fal-ai/kling-video/v3/turbo/pro/image-to-video");

        capabilities.PricePerSecondByResolution.Should().ContainKey(FalVideoCatalog.NativeToken)
            .WhoseValue.Should().Be(0.14m);
        capabilities.PricePerSecondByResolution.Should().ContainSingle();
    }

    [Fact]
    public void A_model_with_no_schema_cannot_be_priced_and_says_so()
    {
        FalAiVideoGenerationClient client = Client(new StubHandler(_ => Json("{}")));

        Action describe = () => client.Describe("MiniMax-H3");

        // The step config's default model name is MiniMax's; on a fal row it must be one of OURS.
        describe.Should().Throw<NotSupportedException>().WithMessage("*not a fal.ai video endpoint*");
    }

    [Fact]
    public void A_blank_model_describes_the_default_image_to_video_endpoint()
    {
        FalAiVideoGenerationClient client = Client(new StubHandler(_ => Json("{}")));

        client.Describe(string.Empty).PricePerSecondByResolution.Keys.Should().BeEquivalentTo(
            FalAiVideoGenerationClient.Catalog.DefaultFor(FalVideoJob.ImageToVideo).Resolutions);
    }

    // ================================================================ image input support (the pre-scan's per-model question)

    [Fact]
    public void ImageInputSupport_reads_each_endpoints_frame_capability_from_the_catalogue()
    {
        FalAiVideoGenerationClient client = Client(new StubHandler(_ => Json("{}")));

        foreach (FalVideoEndpoint endpoint in FalAiVideoGenerationClient.Catalog.All)
        {
            VideoImageInputSupport expected = endpoint.FirstFrameField is null
                ? VideoImageInputSupport.None
                : endpoint.LastFrameField is not null
                    ? VideoImageInputSupport.FirstAndLastFrame
                    : VideoImageInputSupport.FirstFrame;

            client.ImageInputSupport(endpoint.Id).Should().Be(expected, endpoint.Id);
        }
    }

    [Fact]
    public void EnsureAccepts_answers_consistently_with_the_catalogue_for_every_endpoint()
    {
        FalAiVideoGenerationClient client = Client(new StubHandler(_ => Json("{}")));

        foreach (FalVideoEndpoint endpoint in FalAiVideoGenerationClient.Catalog.All)
        {
            Action zero = () => client.EnsureAccepts(endpoint.Id, 0);
            zero.Should().NotThrow(endpoint.Id);

            Action one = () => client.EnsureAccepts(endpoint.Id, 1);
            if (endpoint.FirstFrameField is null)
            {
                one.Should().Throw<NotSupportedException>(endpoint.Id);
            }
            else
            {
                one.Should().NotThrow(endpoint.Id);
            }

            Action two = () => client.EnsureAccepts(endpoint.Id, 2);
            if (endpoint.LastFrameField is null)
            {
                two.Should().Throw<NotSupportedException>(endpoint.Id);
            }
            else
            {
                two.Should().NotThrow(endpoint.Id);
            }
        }
    }

    [Fact]
    public void A_blank_model_declares_the_default_image_to_video_endpoints_support()
    {
        FalAiVideoGenerationClient client = Client(new StubHandler(_ => Json("{}")));

        client.ImageInputSupport(string.Empty).Should().Be(VideoImageInputSupport.FirstFrame);
        Action one = () => client.EnsureAccepts(string.Empty, 1);
        one.Should().NotThrow();
        Action two = () => client.EnsureAccepts(string.Empty, 2);
        two.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void A_model_with_no_schema_declares_no_image_support_and_says_so()
    {
        FalAiVideoGenerationClient client = Client(new StubHandler(_ => Json("{}")));

        Action support = () => client.ImageInputSupport("MiniMax-H3");
        support.Should().Throw<NotSupportedException>().WithMessage("*No schema has been read for the fal.ai video endpoint*");
    }

    // ================================================================ the probe (it must not be able to bill)

    [Fact]
    public async Task The_probe_submits_a_body_that_is_not_JSON_so_no_endpoint_can_dispatch_it()
    {
        StubHandler api = new(_ => Json("{\"detail\":\"validation error\"}", HttpStatusCode.UnprocessableEntity));
        FalAiVideoGenerationClient client = Client(api, probeModel: "fal-ai/kling-video/v3/turbo/pro/text-to-video");

        VideoGenerationProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeTrue();
        api.Requests.Should().ContainSingle();
        string body = Posts(api).Single();
        body.Should().Be(UnparseableProbeBody);
        // THE proof: a body that is not a JSON document cannot be deserialised into any endpoint's input
        // schema, so an endpoint whose required list is EMPTY still cannot generate from it.
        Action parse = () => JsonDocument.Parse(body);
        parse.Should().Throw<JsonException>();
    }

    [Fact]
    public void The_empty_body_probe_would_be_a_REAL_request_on_a_text_to_video_endpoint()
    {
        // Why the video arm cannot reuse the image arm's empty-body probe: these endpoints declare every
        // input optional, so {} is a valid request - a real clip, a real charge. The platform's own body
        // builder still insists on a prompt for them; the SCHEMA does not.
        FalVideoEndpoint kling = FalAiVideoGenerationClient.Catalog.Find("fal-ai/kling-video/v3/turbo/pro/text-to-video")!;
        kling.FirstFrameField.Should().BeNull("its schema declares required: []");
        kling.LastFrameField.Should().BeNull();
        kling.PromptRequired.Should().BeTrue("with no prompt the vendor's own default IS the request");

        // The same body on an endpoint WITH a required input is what the image arm relies on. Both facts
        // together are why one probe body cannot serve both arms.
        FalAiVideoGenerationClient.Catalog.Find("fal-ai/vidu/q4/image-to-video")!
            .FirstFrameRequired.Should().BeTrue();
    }

    [Fact]
    public async Task An_uncatalogued_probe_model_fails_without_a_request()
    {
        StubHandler api = new(_ => Json("{}"));
        FalAiVideoGenerationClient client = Client(api, probeModel: "fal-ai/not-a-video-endpoint");

        VideoGenerationProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeFalse();
        probe.Reason.Should().Contain("not a fal.ai video endpoint");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blank_probe_model_fails_without_a_request()
    {
        StubHandler api = new(_ => Json("{}"));
        FalAiVideoGenerationClient client = Client(api, probeModel: " ");

        VideoGenerationProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeFalse();
        probe.Reason.Should().Contain("No model id is configured");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rejected_key_is_reported_as_an_auth_rejection()
    {
        StubHandler api = new(_ => Json("{\"detail\":\"invalid key credentials\"}", HttpStatusCode.Unauthorized));
        FalAiVideoGenerationClient client = Client(api, probeModel: "fal-ai/vidu/q4/image-to-video");

        VideoGenerationProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeFalse();
        probe.AuthRejected.Should().BeTrue();
        probe.Reason.Should().Contain("rejected the credential");
    }

    [Fact]
    public async Task An_unrouted_model_id_is_reported_as_such()
    {
        StubHandler api = new(_ => Json("{\"detail\":\"Application not found\"}", HttpStatusCode.NotFound));
        FalAiVideoGenerationClient client = Client(api, probeModel: "fal-ai/vidu/q4/image-to-video");

        VideoGenerationProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeFalse();
        probe.AuthRejected.Should().BeFalse();
        probe.Reason.Should().Contain("does not route");
    }

    // ================================================================ resuming after a timeout never re-submits

    [Fact]
    public async Task A_poll_after_a_restart_resumes_the_persisted_job_and_never_submits_a_second_one()
    {
        StubHandler api = Queue(statusSequence: ["IN_PROGRESS", "COMPLETED"]);

        // The first attempt: submit, then the process dies (a fresh client is the next attempt).
        ExternalJobHandle handle = await Client(api).SubmitAsync(
            Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);

        // The second attempt polls the PERSISTED id only - which is what VideoGenerateStepExecutor does
        // when its ledger row already carries a RemoteJobId (at fal's prices a second POST is a second
        // clip, so this is the whole reason the queue split exists).
        FalAiVideoGenerationClient resumed = Client(api);
        (await resumed.PollAsync(new ExternalJobHandle(handle.RemoteJobId), CancellationToken.None)).State
            .Should().Be(ExternalJobState.Running);
        (await resumed.PollAsync(new ExternalJobHandle(handle.RemoteJobId), CancellationToken.None)).State
            .Should().Be(ExternalJobState.Succeeded);

        Posts(api).Should().ContainSingle("resuming polls; it must never submit again");
        api.Requests.Skip(1).Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task A_timeout_on_a_poll_leaves_the_submission_alone()
    {
        // The image arm's single submit-and-poll entry point is deliberately NOT what video uses: the
        // video client has no method that submits and polls itself, so a lost poll budget cannot become
        // a second submission.
        StubHandler api = Queue(statusSequence: "IN_PROGRESS");
        FalAiVideoGenerationClient client = Client(api);

        ExternalJobHandle handle = await client.SubmitAsync(Request("fal-ai/vidu/q4/image-to-video"), CancellationToken.None);
        (await client.PollAsync(handle, CancellationToken.None)).State.Should().Be(ExternalJobState.Running);

        Posts(api).Should().ContainSingle();
    }

    // ================================================================ pricing and the catalogue

    [Fact]
    public void Every_catalogued_endpoint_carries_the_pricing_sentence_it_was_read_from()
    {
        foreach (FalVideoEndpoint endpoint in FalAiVideoGenerationClient.Catalog.All)
        {
            endpoint.SourceUrl.Should().Be("https://fal.ai/models/" + endpoint.Id);
            endpoint.PriceBasis.Should().Contain("fal.ai/models/" + endpoint.Id, endpoint.Id + " must cite its own page");
            endpoint.PriceBasis.Should().Contain(ReadOn(endpoint), endpoint.Id + " must say when it was read");
        }
    }

    /// <summary>
    /// The day an endpoint's schema and price were read: the talking-head routes joined later (avatars phase 3), the
    /// reference-to-video routes later still (several subjects per clip).
    /// </summary>
    private static string ReadOn(FalVideoEndpoint endpoint) => endpoint.Job switch
    {
        FalVideoJob.TalkingHead => "2026-11-06",
        FalVideoJob.ReferenceToVideo => "2026-11-16",
        _ => "2026-10-16",
    };

    private static string DefaultTier(FalVideoEndpoint endpoint) =>
        endpoint.VendorDefaultResolution ?? FalVideoCatalog.NativeToken;

    [Fact]
    public void Every_catalogued_endpoint_has_a_priced_card_at_its_default_resolution()
    {
        foreach (FalVideoEndpoint endpoint in FalAiVideoGenerationClient.Catalog.All)
        {
            ModelPrice? card = ModelPriceCatalog.Priced.SingleOrDefault(m =>
                m.ProviderKind == InferenceProviderKind.FalAi
                && m.Capability == (endpoint.Job == FalVideoJob.TalkingHead ? InferenceProviderCapability.LipSync : InferenceProviderCapability.VideoGeneration)
                && m.Model == endpoint.Id);
            card.Should().NotBeNull(endpoint.Id + " is callable but has no priced entry, so it would meter at the wildcard");
            card!.UsdPerUnit[UsageKind.VideoGenSeconds].Should().Be(
                endpoint.PricePerSecondByResolution[DefaultTier(endpoint)], endpoint.Id);
            card.Basis.Should().Contain("fal.ai/models/" + endpoint.Id, endpoint.Id + " must cite its page");
            card.Basis.Should().Contain(ReadOn(endpoint), endpoint.Id);
        }
    }

    /// <summary>
    /// The price book pre-fills every fal video endpoint with its catalogue price (the default resolution, no variant)
    /// and one variant per resolution of its own static table (docs/design/model-catalog.md §2.2, §4.2 step 3).
    /// </summary>
    [Fact]
    public void The_price_book_prices_every_fal_video_endpoint_and_each_of_its_resolutions()
    {
        foreach (FalVideoEndpoint endpoint in FalAiVideoGenerationClient.Catalog.All)
        {
            IReadOnlyList<SuggestedPrice> prices = ReelBolt.Inference.Api.Services.Billing.PriceBook.SuggestFor(
                InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration, endpoint.Id);
            ModelPrice entry = ModelPriceCatalog.Priced.Single(m =>
                m.ProviderKind == InferenceProviderKind.FalAi && m.Model == endpoint.Id);

            prices.Single(p => p.Variant is null).CostUsdPerUnit.Should().Be(entry.UsdPerUnit[UsageKind.VideoGenSeconds]);
            prices.Where(p => p.Variant is not null).Select(p => (p.Variant!, p.CostUsdPerUnit))
                .Should().BeEquivalentTo(endpoint.PricePerSecondByResolution.Select(r => (r.Key, r.Value)), endpoint.Id);
        }
    }

    [Fact]
    public void The_catalogue_has_a_default_per_direction_and_the_documented_alternatives()
    {
        FalVideoCatalog catalog = FalAiVideoGenerationClient.Catalog;

        catalog.DefaultFor(FalVideoJob.ImageToVideo).Id.Should().Be("fal-ai/vidu/q4/image-to-video");
        catalog.DefaultFor(FalVideoJob.TextToVideo).Id.Should().Be("fal-ai/kling-video/v3/turbo/pro/text-to-video");
        catalog.AlternativesFor(FalVideoJob.ImageToVideo).Should().NotBeEmpty();
        catalog.AlternativesFor(FalVideoJob.TextToVideo).Should().NotBeEmpty();
        catalog.All.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        catalog.All.Where(e => e.Job == FalVideoJob.ImageToVideo).Should().NotBeEmpty("image-to-video is the point of the wave");
    }

    // ================================================================ the capability allowlist

    [Fact]
    public void Fal_may_serve_video_generation_and_the_capability_table_and_the_admin_boundary_agree()
    {
        InferenceProviderCapability[] expected =
        [
            InferenceProviderCapability.Segmentation, InferenceProviderCapability.Depth,
            InferenceProviderCapability.ImageGeneration, InferenceProviderCapability.ImageEdit,
            InferenceProviderCapability.ImageErase, InferenceProviderCapability.ImageFill,
            InferenceProviderCapability.ImageExpand, InferenceProviderCapability.ImageBackgroundReplace,
            InferenceProviderCapability.ImageRelight, InferenceProviderCapability.ImageUpscale,
            InferenceProviderCapability.VideoGeneration,
            // Appended by the prompted-region-effects contracts: text-prompted VIDEO segmentation
            // (fal-ai/sam-3-1/video-rle) rides the same kind, credential and queue contract, and it
            // is fal-only. Its own invariants live in PromptedRegionEffectsContractTests.
            InferenceProviderCapability.VideoSegmentation,
            // Appended by the audio arm (T-107): music and sound effects on the same queue contract.
            InferenceProviderCapability.MusicGeneration, InferenceProviderCapability.SoundEffectGeneration,
            InferenceProviderCapability.AudioIsolation,
            InferenceProviderCapability.VideoUpscale, InferenceProviderCapability.FrameInterpolation, InferenceProviderCapability.VideoOutpaint,
            InferenceProviderCapability.LipSync
        ];

        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.FalAi].Should().BeEquivalentTo(expected);
        foreach (InferenceProviderCapability capability in ProviderCapabilityUsage.Capabilities)
        {
            InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.FalAi, capability, out _)
                .Should().Be(!expected.Contains(capability), "FalAi/" + capability);
        }
    }

    [Fact]
    public void The_video_allowlist_is_explicit_and_did_not_become_a_not_a_chat_kind_catch_all()
    {
        // The exact set, in both directions: a kind that serves video at all, and a kind that does not.
        foreach (InferenceProviderKind kind in ProviderCapabilityUsage.Kinds)
        {
            bool allowed = kind is InferenceProviderKind.MiniMax or InferenceProviderKind.Higgsfield
                or InferenceProviderKind.ComfyUI or InferenceProviderKind.FalAi;
            InferenceProvidersController.IsUnsupportedCombination(
                    kind, InferenceProviderCapability.VideoGeneration, out string error)
                .Should().Be(!allowed, kind.ToString());
            if (!allowed)
            {
                // The dormant direct ElevenLabs kind (decision 7) is refused every capability with its own sentence.
                error.Should().Contain(kind switch
                {
                    InferenceProviderKind.ElevenLabs => "is not offered",
                    InferenceProviderKind.LocalRunner => "can only serve the Chat, Vision and Embedding capabilities",
                    _ => "Only MiniMax, Higgsfield, ComfyUI and FalAi providers"
                });
            }
        }

        // And the negative rule: fal serves video and nothing else chat-shaped.
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.FalAi, InferenceProviderCapability.Chat, out _).Should().BeTrue();
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.FalAi, InferenceProviderCapability.Transcription, out _).Should().BeTrue();
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration, out _).Should().BeFalse();
    }

    [Fact]
    public void A_fal_video_row_must_name_an_endpoint_the_platform_has_a_schema_for()
    {
        InferenceProvidersController.IsUnsupportedModel(
            InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration,
            "fal-ai/vidu/q4/image-to-video", out _).Should().BeFalse();
        InferenceProvidersController.IsUnsupportedModel(
            InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration,
            "", out _).Should().BeFalse("a blank model means the catalogue's default image-to-video endpoint");

        InferenceProvidersController.IsUnsupportedModel(
                InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration,
                "MiniMax-H3", out string error)
            .Should().BeTrue();
        error.Should().Contain("not a fal.ai video endpoint");

        // A video endpoint named on an IMAGE row is refused by the image rule, not by the video one.
        InferenceProvidersController.IsUnsupportedModel(
            InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit,
            "fal-ai/vidu/q4/image-to-video", out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_fal_video_capability_is_priced_after_the_upgrade_even_for_an_endpoint_without_a_card()
    {
        ProviderCapabilityUsage.RequiredCards().Should().Contain(
            (InferenceProviderKind.FalAi, UsageKind.VideoGenSeconds));

        // The old seed's (FalAi, VideoGenSeconds, *) wildcard is what such a capability is ported from.
        await using ReelBolt.WorkflowEngine.Tests.Pricing.PortedCatalog catalog = await ReelBolt.WorkflowEngine.Tests.Pricing.PortedCatalog
            .CreateAsync([(InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration, "fal-ai/some-future-endpoint")]);

        PriceQuote quote = await catalog.PriceAsync(InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration,
            "fal-ai/some-future-endpoint", UsageKind.VideoGenSeconds, 5);

        quote.PriceId.Should().NotBeNull();
        quote.CostUsd.Should().BePositive();
    }

    // ================================================================ the fact sheet, field for field

    private static string UnparseableProbeBody =>
        (string)typeof(FalAiVideoGenerationClient).Assembly
            .GetType("ReelBolt.Shared.Inference.FalQueueClient")!
            .GetField("UnparseableProbeBody")!.GetRawConstantValue()!;

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
    public void Every_catalogued_video_endpoint_is_documented_in_the_fact_sheet_field_for_field()
    {
        string? path = FindFactSheet();
        path.Should().NotBeNull("docs/fal-ai-models.md is the transcription source and must be findable from the test run");
        string sheet = File.ReadAllText(path!);

        foreach (FalVideoEndpoint e in FalAiVideoGenerationClient.Catalog.All)
        {
            string section = SectionOf(sheet, e.Id);
            section.Should().Contain("openapi.json?endpoint_id=" + e.Id, e.Id + " must cite its own schema URL");
            section.Should().Contain("fal.ai/models/" + e.Id, e.Id + " must cite its own model page for the price");

            foreach (string field in new[] { e.FirstFrameField, e.LastFrameField, e.PromptField }
                         .Where(n => n is not null).Select(n => n!))
            {
                section.Should().Contain("| `" + field + "` |", e.Id + ": wired field '" + field + "' is not in its schema table");
            }

            if (e.ResolutionField is { } resolutionField)
            {
                section.Should().Contain("| `" + resolutionField + "` |", e.Id);
                foreach (string token in e.Resolutions)
                {
                    section.Should().Contain("\"" + token + "\"", e.Id + ": resolution '" + token + "' is not transcribed verbatim");
                }
            }
            else
            {
                section.Should().Contain(FalVideoCatalog.NativeToken, e.Id + ": a flat-priced endpoint must say so");
            }

            if (e.RatioField is { } ratioField)
            {
                section.Should().Contain("| `" + ratioField + "` |", e.Id);
            }

            foreach (string name in e.SendByDefault.Keys)
            {
                section.Should().Contain("| `" + name + "` |", e.Id + ": sent-by-default field '" + name + "' is not in its schema table");
            }
        }
    }

    [Fact]
    public void The_fact_sheet_says_a_data_uri_is_the_form_the_platform_sends()
    {
        string sheet = File.ReadAllText(FindFactSheet()!);

        SectionOf(sheet, "fal-ai/vidu/q4/image-to-video").Should().Contain("data URI");
        sheet.Should().Contain("X-Fal-Store-IO", "the retention note belongs in the fact sheet");
    }
}
