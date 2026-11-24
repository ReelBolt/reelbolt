#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The ONE wired <see cref="IVideoSegmentationClientFactory"/> (initiative "prompted region effects",
/// T-013): a resolved <c>VideoSegmentation</c> provider produces a real
/// <see cref="FalAiVideoSegmentationClient"/> over a stub HTTP handler, wrapped in
/// <see cref="MeteringVideoSegmentationClient"/>. Offline: no queue, no network, no key beyond a fake
/// string.
/// </summary>
public sealed class FalAiVideoSegmentationFactoryTests
{
    private const int Width = 12;
    private const int Height = 6;
    private const int Frames = 6;
    private const string Model = "fal-ai/sam-3-1/video-rle";

    [Fact]
    public async Task A_segmentation_target_reaches_the_client_and_yields_decodable_masks()
    {
        StubHandler api = new(request => Routed(request, Document()));
        FalAiVideoSegmentationClientFactory factory = Factory(api, recorder: null);

        VideoSegmentationResult result = await factory
            .Get(Provider(), new VideoSegmentationFrameSize(Width, Height, Frames))
            .SegmentVideoAsync(Request(), CancellationToken.None);

        result.Width.Should().Be(Width);
        result.Height.Should().Be(Height);
        result.Frames.Should().HaveCount(Frames);

        // The masks are the point: every frame's run list decodes to the 2-pixel object the stub says
        // burned into the top row, through the ONE parser the analyze path also uses.
        foreach (VideoSegmentationFrame frame in result.Frames)
        {
            VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode(
                frame.Objects.Should().ContainSingle().Subject.Rle, 0, result.Width, result.Height);

            mask.Area.Should().Be(2);
            mask.Pixels[0].Should().Be(Sam31VideoRleDecoder.ObjectValue);
            mask.Pixels[1].Should().Be(Sam31VideoRleDecoder.ObjectValue);
            mask.Pixels[2].Should().Be(0);
        }
    }

    [Fact]
    public async Task A_blank_endpoint_uses_the_queue_default_and_the_key_is_sent_verbatim()
    {
        StubHandler api = new(request => Routed(request, Document()));
        FalAiVideoSegmentationClientFactory factory = Factory(api, recorder: null);

        await factory
            .Get(Provider(endpoint: "  "), new VideoSegmentationFrameSize(Width, Height, Frames))
            .SegmentVideoAsync(Request(), CancellationToken.None);

        HttpRequestMessage submitted = api.Requests[0];
        submitted.Method.Should().Be(HttpMethod.Post);
        submitted.RequestUri!.Host.Should().Be("queue.fal.run", "FalQueueClient.DefaultEndpoint, filled in for a blank row");
        submitted.RequestUri.AbsolutePath.Should().Be("/" + Model);
        submitted.Headers.GetValues("Authorization").Should().ContainSingle().Which
            .Should().Be("Key fal-key", "the stored key verbatim, never a header we compose from parts");
        submitted.Headers.GetValues("X-Fal-Store-IO").Should().ContainSingle().Which
            .Should().Be("0", "no vendor-side retention of the customer's footage");

        string body = api.Bodies[0];
        body.Should().Contain("\"video_url\"");
        body.Should().Contain("\"prompt\":\"speedometer\"", "the prompt travels to the model, sanitized");
        body.Should().Contain("\"return_object_rles\":true");
        body.Should().Contain("\"apply_mask\":false");
        body.Should().Contain("\"boundingbox_zip\":false");
        body.Should().Contain("\"box_prompts\"", "a drawn region seeds the vendor's own box prompt");
    }

    [Fact]
    public async Task Every_bought_call_is_metered_in_16_frame_blocks()
    {
        RecordingUsageRecorder recorder = new();
        StubHandler api = new(request => Routed(request, Document()));
        FalAiVideoSegmentationClientFactory factory = Factory(api, recorder);

        await factory
            .Get(Provider(), new VideoSegmentationFrameSize(Width, Height, Frames))
            .SegmentVideoAsync(Request(), CancellationToken.None);

        UsageRecordRequest recorded = recorder.Records.Should().ContainSingle().Subject;
        recorded.Kind.Should().Be(UsageKind.VideoSegmentationFrames);
        recorded.Quantity.Should().Be(VideoSegmentationBilling.BillableFrames(Frames),
            "6 frames bill one 16-frame block, exactly as fal rounds them");
        recorded.ProviderKind.Should().Be(InferenceProviderKind.FalAi);
        recorded.ModelName.Should().Be(Model);
        recorded.ProviderId.Should().Be(Provider().ProviderId);
    }

    [Fact]
    public void A_kind_that_cannot_serve_video_segmentation_is_refused()
    {
        FalAiVideoSegmentationClientFactory factory = Factory(new StubHandler(request => Routed(request, Document())), null);

        Action get = () => factory.Get(
            Provider() with { Kind = InferenceProviderKind.AzureOpenAI },
            new VideoSegmentationFrameSize(Width, Height, Frames));

        get.Should().Throw<NotSupportedException>().Which.Message.Should().Contain("Only FalAi");
    }

    [Fact]
    public void A_zero_sized_frame_geometry_is_refused_rather_than_cached()
    {
        FalAiVideoSegmentationClientFactory factory = Factory(new StubHandler(request => Routed(request, Document())), null);

        Action get = () => factory.Get(Provider(), new VideoSegmentationFrameSize(0, Height, Frames));

        get.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_source_swapped_under_the_same_provider_and_size_is_measured_by_its_own_probe()
    {
        // Production run 0b464e84: a step's source was swapped for a shorter clip of the same frame size.
        // The factory cached one client per (provider, width x height) with the FIRST source's probe baked
        // in, so the new clip's 108-frame answer was checked against the old clip's 192 frames and refused
        // (SEGMENTATION_BAD_RESPONSE). Each Get must carry the caller's own measurement.
        StubHandler api = new(request => Routed(request, Document()));
        FalAiVideoSegmentationClientFactory factory = Factory(api, recorder: null);

        // The earlier source: same grid, many more frames (its client is built, and would have been cached).
        _ = factory.Get(Provider(), new VideoSegmentationFrameSize(Width, Height, 192));

        VideoSegmentationResult result = await factory
            .Get(Provider(), new VideoSegmentationFrameSize(Width, Height, Frames))
            .SegmentVideoAsync(Request(), CancellationToken.None);

        result.Frames.Should().HaveCount(Frames, "the vendor's answer is checked against THIS clip's probe");
    }

    [Fact]
    public async Task The_frame_count_of_the_probe_is_never_taken_from_an_earlier_get()
    {
        StubHandler api = new(request => Routed(request, Document()));
        FalAiVideoSegmentationClientFactory factory = Factory(api, recorder: null);

        _ = factory.Get(Provider(), new VideoSegmentationFrameSize(Width, Height, Frames));
        Func<Task> longer = () => factory
            .Get(Provider(), new VideoSegmentationFrameSize(Width, Height, 192))
            .SegmentVideoAsync(Request(), CancellationToken.None);

        // 6 entries against a 192-frame probe is outside the tolerance: proves the second Get's probe is used.
        await longer.Should().ThrowAsync<VideoSegmentationBadResponseException>();
    }

    [Fact]
    public async Task A_segmentation_target_through_the_tracking_service_produces_a_stored_mask()
    {
        // The end-to-end seam the feature runs on: SegmentationTrackingService -> this factory -> the
        // real fal client -> a stub queue -> decoded masks -> an encoded mask video in the store.
        RecordingUsageRecorder recorder = new();
        StubHandler api = new(request => Routed(request, Document()));
        FalAiVideoSegmentationClientFactory factory = Factory(api, recorder);

        SegmentationTrackingTests.Harness harness = new(
            new SegmentationTrackingTests.CountingClient(), clients: factory);

        SegmentationSourceOutcome outcome = await harness.RunAsync(new VideoAnalyzeStepConfig(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput),
            TrackObjects: [new ObjectTrackTarget("speedometer", Method: ObjectTrackMethod.Segmentation)])
        {
            AllowSourceMediaEgress = true,
            MaxSegmentationSeconds = 60
        });

        outcome.FailureCode.Should().BeNull(outcome.FailureMessage);
        VideoSegmentationTrack track = outcome.Tracks.Should().ContainSingle().Subject;
        track.Prompt.Should().Be("speedometer");
        track.FrameCount.Should().Be(Frames);
        track.Coverage.Should().BeGreaterThan(0, "the stub's object covers two pixels on every frame");
        track.MaskStorageKey.Should().NotBeNullOrWhiteSpace("the mask is written under the project's prefix");
        track.MaskContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
        outcome.ProviderCalls.Should().Be(1);

        api.Requests.Should().Contain(r => r.Method == HttpMethod.Post);
        recorder.Records.Should().ContainSingle().Which.Kind.Should().Be(UsageKind.VideoSegmentationFrames);
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    private static FalAiVideoSegmentationClientFactory Factory(HttpMessageHandler handler, IUsageRecorder? recorder) =>
        new(recorder, NullLogger<FalAiVideoSegmentationClientFactory>.Instance, handler);

    private static ResolvedImageAnalysisProvider Provider(string endpoint = "https://queue.fal.run") => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "fal",
        InferenceProviderKind.FalAi,
        InferenceProviderCapability.VideoSegmentation,
        endpoint,
        Model,
        "fal-key",
        900);

    private static VideoSegmentationRequest Request() => new(
        Model: Model,
        VideoUrl: new Uri("https://files.example.com/clip.mp4?sig=short"),
        Prompt: "speedometer",
        BoxPrompts: [new VideoSegmentationBoxPrompt(0.0, 0.0, 0.5, 0.5, FrameIndex: 0, ObjectId: 0, Label: "0")],
        MaxObjects: 4,
        DetectionThreshold: 0.5,
        Timeout: TimeSpan.FromSeconds(30));

    /// <summary>
    /// Six frames of a 12x6 clip, one object per frame: the run <c>"0 2"</c> lights the first two pixels
    /// of row 0, and the vendor's CENTRE-anchored box <c>[cx, cy, w, h]</c> is drawn to contain them (the
    /// response reader refuses a mask that contradicts the vendor's own box).
    /// </summary>
    private static string Document()
    {
        const string rle = "0 2";
        const string box = "[0.0833, 0.0833, 0.1667, 0.1667]";

        List<string> objects = [], mirrors = [], boxes = [];
        for (int i = 0; i < Frames; i++)
        {
            objects.Add("[{\"object_id\":0,\"rle\":\"" + rle + "\",\"box\":" + box + "}]");
            mirrors.Add("\"" + rle + "\"");
            boxes.Add("[" + box + "]");
        }

        return "{\"rle\":[" + string.Join(",", mirrors) +
               "],\"objects\":[" + string.Join(",", objects) +
               "],\"boxes\":[" + string.Join(",", boxes) + "]}";
    }

    private static HttpResponseMessage Routed(HttpRequestMessage request, string document)
    {
        if (request.Method == HttpMethod.Post)
        {
            return Json(
                "{\"request_id\":\"req-1\",\"status_url\":\"https://queue.fal.run/m/requests/req-1/status\"," +
                "\"response_url\":\"https://queue.fal.run/m/requests/req-1\"}");
        }

        if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
        {
            return Json("{\"status\":\"COMPLETED\"}");
        }

        return Json(document);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private sealed class RecordingUsageRecorder : IUsageRecorder
    {
        public List<UsageRecordRequest> Records { get; } = [];

        public ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default)
        {
            Records.Add(request);
            return ValueTask.FromResult(0L);
        }
    }
}
