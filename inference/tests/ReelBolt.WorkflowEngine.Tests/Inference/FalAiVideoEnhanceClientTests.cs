#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;
using System.Collections.Generic;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// fal.ai hosted video upscale and frame interpolation replayed against hand-written fixtures (no live call: the fal key
/// does not exist yet). The submit and status documents follow the queue's recorded shape; the result documents follow the
/// OpenAPI output schemas read 2026-11-02 and are UNVERIFIED against a real run.
/// </summary>
public class FalAiVideoEnhanceClientTests
{
    private const string Source = "https://storage.example.test/source/clip.mp4?X-Amz-Signature=abc";
    private const string ResultUrl = "https://v3.fal.media/files/zebra/enhanced.mp4";
    private static readonly byte[] Clip = [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 1, 2, 3, 4];

    private static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Inference", "Fixtures", "VideoEnhance", name));

    private static bool SameJson(string actual, string fixture) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(Text(fixture)));

    private static AudioStubHandler Queue(
        string result = "ve-result-topaz.json", HttpStatusCode resultStatus = HttpStatusCode.OK, string completed = "ve-status-completed.json",
        HttpStatusCode submitStatus = HttpStatusCode.OK, string submitBody = "", bool stayRunning = false,
        Func<HttpRequestMessage, HttpResponseMessage?>? cdn = null)
    {
        Queue<string> statuses = new(["ve-status-in-queue.json", "ve-status-in-progress.json"]);
        return new AudioStubHandler(request =>
        {
            if (request.RequestUri!.Host != "queue.fal.run")
            {
                return cdn?.Invoke(request) ?? AudioFixtures.Audio(Clip, "video/mp4");
            }

            if (request.Method == HttpMethod.Post)
            {
                return submitStatus == HttpStatusCode.OK
                    ? AudioFixtures.Json(Text("ve-submit.json"))
                    : AudioFixtures.Json(submitBody, submitStatus);
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
            {
                return AudioFixtures.Json(stayRunning ? Text("ve-status-in-progress.json")
                    : statuses.Count > 0 ? Text(statuses.Dequeue()) : Text(completed));
            }

            return AudioFixtures.Json(Text(result), resultStatus);
        });
    }

    private static HttpClient Http(AudioStubHandler handler) => new(handler) { BaseAddress = new Uri("https://queue.fal.run") };

    private static FalAiVideoUpscaleClient Upscaler(AudioStubHandler handler, TimeSpan? pollTimeout = null) =>
        new(Http(handler), AudioFixtures.Key, downloadClient: new HttpClient(handler), pollInterval: TimeSpan.Zero,
            pollTimeout: pollTimeout, delay: (_, _) => Task.CompletedTask);

    private static FalAiFrameInterpolationClient Interpolator(AudioStubHandler handler, TimeSpan? pollTimeout = null) =>
        new(Http(handler), AudioFixtures.Key, downloadClient: new HttpClient(handler), pollInterval: TimeSpan.Zero,
            pollTimeout: pollTimeout, delay: (_, _) => Task.CompletedTask);

    private static VideoUpscaleRequest Up(string? source = Source, double factor = 2, string model = "", double seconds = 10,
        int width = 960, int height = 540, double fps = 30) =>
        new(source!, width, height, fps, seconds, (long)(seconds * fps), factor, model, "corr-1");

    private static FrameInterpolationRequest Interp(int multiplier = 2, string model = "", double seconds = 10) =>
        new(Source, 1920, 1080, 30, seconds, (long)(seconds * 30), multiplier, model, "corr-2");

    private static void NoLeak(VideoEnhanceException ex) =>
        ex.Message.Should().NotContain("X-Amz").And.NotContain("storage.example.test").And.NotContain("clip.mp4");

    private static int StatusReads(AudioStubHandler api) =>
        api.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal));

    [Fact]
    public async Task Topaz_happy_path_submits_the_recorded_body_then_polls_and_downloads_without_the_key()
    {
        AudioStubHandler api = Queue();
        FalAiVideoUpscaleClient client = Upscaler(api);

        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);
        api.Requests.Should().ContainSingle("submit is one POST and no polling");
        api.Requests[0].RequestUri!.ToString().Should().Be("https://queue.fal.run/fal-ai/topaz/upscale/video");
        api.Requests[0].Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Key test");
        SameJson(api.Bodies[0], "ve-topaz-request.json").Should().BeTrue(api.Bodies[0]);
        api.Bodies[0].Should().NotContain("target_fps", "Topaz would start interpolating and change frame timing");
        handle.RequestId.Should().Be("ve-req-0001");
        handle.ToString().Should().NotContain("queue.fal.run");

        VideoEnhanceResult result = await client.PollAsync(handle, "", CancellationToken.None);

        StatusReads(api).Should().Be(3, "IN_QUEUE, IN_PROGRESS, COMPLETED");
        api.Requests.Single(r => r.RequestUri!.ToString() == ResultUrl).Headers.Contains("Authorization")
            .Should().BeFalse("the key must never reach a CDN host");
        result.Video.Should().Equal(Clip);
        result.MimeType.Should().Be("video/mp4");
        result.Extension.Should().Be(".mp4");
        result.Model.Should().Be("fal-ai/topaz/upscale/video");
        result.VendorDurationSec.Should().BeNull();
        result.VendorRequestId.Should().Be("ve-req-0001");
        result.RawMetadata.Should().NotContain("http", "no URL is kept in the provenance metadata");
        result.ToString().Should().NotContain("storage.example.test");
    }

    [Fact]
    public async Task Bytedance_sends_scale_ratio_and_the_rounded_source_fps_and_reports_the_vendor_duration()
    {
        AudioStubHandler api = Queue(result: "ve-result-bytedance.json");
        FalAiVideoUpscaleClient client = Upscaler(api);
        const string model = "fal-ai/bytedance-upscaler/upscale/video";

        // 1080p output at 29.97 fps: the rate is rounded to 30 and clamped to 24..120.
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(model: model, factor: 2, width: 1920, height: 1080, fps: 29.97), CancellationToken.None);
        SameJson(api.Bodies[0], "ve-bytedance-request.json").Should().BeTrue(api.Bodies[0]);

        VideoEnhanceResult result = await client.PollAsync(handle, model, CancellationToken.None);
        result.VendorDurationSec.Should().Be(12.5);
        result.Model.Should().Be(model);
    }

    [Theory]
    [InlineData(2, "ve-rife-2x-request.json", "fal-ai/rife/video")]
    [InlineData(4, "ve-rife-4x-request.json", "fal-ai/rife/video")]
    [InlineData(2, "ve-film-2x-request.json", "fal-ai/film/video")]
    public async Task Interpolation_sends_the_in_between_frame_count_scene_detection_and_never_sync_mode(int multiplier, string fixture, string model)
    {
        AudioStubHandler api = Queue(result: "ve-result-rife.json");
        FalAiFrameInterpolationClient client = Interpolator(api);

        VideoEnhanceJobHandle handle = await client.SubmitAsync(Interp(multiplier, model), CancellationToken.None);

        api.Requests[0].RequestUri!.ToString().Should().Be("https://queue.fal.run/" + model);
        SameJson(api.Bodies[0], fixture).Should().BeTrue(api.Bodies[0]);
        api.Bodies[0].Should().NotContain("sync_mode");

        VideoEnhanceResult result = await client.PollAsync(handle, model, CancellationToken.None);
        result.Video.Should().Equal(Clip);
        result.Model.Should().Be(model);
    }

    [Fact]
    public async Task A_job_outlives_one_poll_and_resumes_from_a_saved_handle_in_a_new_client_without_submitting()
    {
        AudioStubHandler first = Queue(stayRunning: true);
        FalAiVideoUpscaleClient client = Upscaler(first, pollTimeout: TimeSpan.Zero);
        VideoEnhanceJobHandle saved = await client.SubmitAsync(Up(), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(saved, "", CancellationToken.None);
        VideoEnhanceException running = (await poll.Should().ThrowAsync<VideoEnhanceException>()).Which;
        running.Kind.Should().Be(VideoEnhanceErrorKind.StillRunning);
        running.Code.Should().Be("ENHANCE_STILL_RUNNING");
        first.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1, "a timeout must never re-submit");

        // An engine restart: a brand new client, the handle read back from the ledger.
        VideoEnhanceJobHandle fromLedger = new(saved.RequestId, saved.StatusUrl, saved.ResponseUrl);
        AudioStubHandler second = Queue();
        VideoEnhanceResult result = await Upscaler(second).ResumeAsync(fromLedger, "fal-ai/topaz/upscale/video", CancellationToken.None);

        second.Requests.Should().NotContain(r => r.Method == HttpMethod.Post, "resume never submits");
        result.Video.Should().Equal(Clip);
    }

    [Fact]
    public async Task A_status_url_outside_the_queue_origin_is_refused_on_resume()
    {
        AudioStubHandler api = Queue();
        VideoEnhanceJobHandle hostile = new("x", "https://evil.example.test/status", "https://evil.example.test/result");

        Func<Task> act = () => Upscaler(api).ResumeAsync(hostile, "", CancellationToken.None);

        (await act.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_VENDOR_FAILED");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_402_at_submit_is_a_quota_failure()
    {
        AudioStubHandler api = Queue(submitStatus: HttpStatusCode.PaymentRequired, submitBody: "{\"detail\":\"Exhausted balance\"}");

        Func<Task> act = () => Upscaler(api).SubmitAsync(Up(), CancellationToken.None);

        (await act.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_VENDOR_QUOTA");
    }

    [Fact]
    public async Task A_429_at_submit_is_retryable_because_nothing_was_queued()
    {
        AudioStubHandler api = Queue(submitStatus: HttpStatusCode.TooManyRequests, submitBody: "{\"detail\":\"slow down\"}");

        Func<Task> act = () => Interpolator(api).SubmitAsync(Interp(), CancellationToken.None);

        VideoEnhanceException ex = (await act.Should().ThrowAsync<VideoEnhanceException>()).Which;
        ex.Code.Should().Be("ENHANCE_VENDOR_RETRYABLE");
        ex.IsRetryable.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task A_5xx_or_408_at_submit_is_outcome_unknown_and_never_retryable(HttpStatusCode status)
    {
        AudioStubHandler api = Queue(submitStatus: status, submitBody: "{\"detail\":\"try later\"}");

        Func<Task> act = () => Interpolator(api).SubmitAsync(Interp(), CancellationToken.None);

        VideoEnhanceException ex = (await act.Should().ThrowAsync<VideoEnhanceException>()).Which;
        ex.Code.Should().Be("ENHANCE_SUBMIT_OUTCOME_UNKNOWN", "the vendor may have queued and will bill the job");
        ex.IsRetryable.Should().BeFalse("an automatic resubmit could be a second charge");
        api.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1);
    }

    [Fact]
    public async Task An_http_timeout_at_submit_is_outcome_unknown_and_a_caller_cancel_still_propagates()
    {
        AudioStubHandler timeout = new(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        Func<Task> act = () => Interpolator(timeout).SubmitAsync(Interp(), CancellationToken.None);
        VideoEnhanceException ex = (await act.Should().ThrowAsync<VideoEnhanceException>()).Which;
        ex.Code.Should().Be("ENHANCE_SUBMIT_OUTCOME_UNKNOWN");
        ex.IsRetryable.Should().BeFalse();

        Func<Task> up = () => Upscaler(timeout).SubmitAsync(Up(), CancellationToken.None);
        (await up.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_SUBMIT_OUTCOME_UNKNOWN");

        using CancellationTokenSource cts = new();
        cts.Cancel();
        AudioStubHandler cancelled = new(_ => throw new TaskCanceledException());
        Func<Task> cancel = () => Upscaler(cancelled).SubmitAsync(Up(), cts.Token);
        await cancel.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_transport_failure_at_submit_is_outcome_unknown()
    {
        AudioStubHandler api = new(_ => throw new HttpRequestException("connection reset"));

        Func<Task> act = () => Upscaler(api).SubmitAsync(Up(), CancellationToken.None);

        (await act.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_SUBMIT_OUTCOME_UNKNOWN");
    }

    private static AudioStubHandler FlakyStatus(int failures, HttpStatusCode failure)
    {
        int seen = 0;
        return new AudioStubHandler(request =>
        {
            if (request.RequestUri!.Host != "queue.fal.run") return AudioFixtures.Audio(Clip, "video/mp4");
            if (request.Method == HttpMethod.Post) return AudioFixtures.Json(Text("ve-submit.json"));
            if (request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
            {
                if (seen++ < failures)
                {
                    if (failure == HttpStatusCode.RequestTimeout) throw new HttpRequestException("reset");
                    return AudioFixtures.Json("{}", failure);
                }

                return AudioFixtures.Json(Text("ve-status-completed.json"));
            }

            return AudioFixtures.Json(Text("ve-result-topaz.json"));
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)] // simulated transport error
    public async Task A_transient_poll_error_is_retried_within_the_deadline_and_the_job_completes(HttpStatusCode failure)
    {
        AudioStubHandler api = FlakyStatus(2, failure);
        FalAiVideoUpscaleClient client = Upscaler(api);
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);

        VideoEnhanceResult result = await client.PollAsync(handle, "", CancellationToken.None);

        result.Video.Should().Equal(Clip);
        StatusReads(api).Should().Be(3);
        api.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1);
    }

    [Fact]
    public async Task A_poll_that_keeps_failing_past_the_deadline_leaves_the_handle_resumable()
    {
        AudioStubHandler api = FlakyStatus(int.MaxValue, HttpStatusCode.BadGateway);
        FalAiVideoUpscaleClient client = Upscaler(api, pollTimeout: TimeSpan.Zero);
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(handle, "", CancellationToken.None);

        (await poll.Should().ThrowAsync<VideoEnhanceException>()).Which.Kind.Should().Be(VideoEnhanceErrorKind.StillRunning);
    }

    [Fact]
    public async Task A_404_on_poll_is_permanent_and_not_retried()
    {
        AudioStubHandler api = FlakyStatus(int.MaxValue, HttpStatusCode.NotFound);
        FalAiVideoUpscaleClient client = Upscaler(api);
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(handle, "", CancellationToken.None);

        (await poll.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_VENDOR_FAILED");
        StatusReads(api).Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/octet-stream")]
    [InlineData("binary/octet-stream")]
    public async Task A_download_without_a_video_content_type_is_refused(string? contentType)
    {
        AudioStubHandler api = Queue(cdn: _ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(Clip) };
            response.Content.Headers.ContentType = contentType is null ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return response;
        });
        FalAiVideoUpscaleClient client = Upscaler(api);
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(handle, "", CancellationToken.None);

        (await poll.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain("content type");
    }

    private sealed class EndlessStream : Stream
    {
        public long Produced { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Produced += count; Array.Fill(buffer, (byte)1, offset, count); return count; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_streamed_download_with_no_content_length_is_cut_off_at_the_cap()
    {
        EndlessStream stream = new();
        AudioStubHandler api = Queue(cdn: _ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            return response;
        });
        const long cap = 64 * 1024;
        FalAiVideoUpscaleClient client = new(Http(api), AudioFixtures.Key, downloadClient: new HttpClient(api),
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask, maxDownloadBytes: cap);
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(handle, "", CancellationToken.None);

        (await poll.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain("limit");
        stream.Produced.Should().BeLessThan(cap * 4, "the read stops at the cap instead of draining an endless body");
    }

    [Fact]
    public async Task A_422_at_submit_is_a_bad_request_and_never_echoes_the_source_url()
    {
        AudioStubHandler api = Queue(submitStatus: HttpStatusCode.UnprocessableEntity,
            submitBody: "{\"detail\":\"cannot fetch " + Source + " now; also storage.example.test/source/clip.mp4?X-Amz-Signature=abc\"}");

        Func<Task> act = () => Upscaler(api).SubmitAsync(Up(), CancellationToken.None);

        VideoEnhanceException ex = (await act.Should().ThrowAsync<VideoEnhanceException>()).Which;
        ex.Code.Should().Be("ENHANCE_REQUEST_INVALID");
        NoLeak(ex);
    }

    [Fact]
    public async Task A_safety_checker_refusal_is_its_own_code_at_submit_and_after_completion()
    {
        AudioStubHandler atSubmit = Queue(submitStatus: HttpStatusCode.UnprocessableEntity,
            submitBody: "{\"detail\":[{\"type\":\"content_policy_violation\",\"msg\":\"flagged\",\"loc\":[\"body\"]}]}");
        Func<Task> act = () => Upscaler(atSubmit).SubmitAsync(Up(), CancellationToken.None);
        (await act.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_VENDOR_REFUSED");

        AudioStubHandler afterRun = Queue(completed: "ve-status-completed-nsfw.json");
        FalAiVideoUpscaleClient client = Upscaler(afterRun);
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);
        Func<Task> poll = () => client.PollAsync(handle, "", CancellationToken.None);
        (await poll.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_VENDOR_REFUSED");
    }

    [Fact]
    public async Task A_job_the_vendor_completed_with_an_error_is_a_vendor_failure_that_is_not_retried()
    {
        AudioStubHandler api = Queue(completed: "ve-status-completed-error.json");
        FalAiVideoUpscaleClient client = Upscaler(api);
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(handle, "", CancellationToken.None);

        VideoEnhanceException ex = (await poll.Should().ThrowAsync<VideoEnhanceException>()).Which;
        ex.Code.Should().Be("ENHANCE_VENDOR_FAILED");
        ex.IsRetryable.Should().BeFalse();
        ex.Message.Should().Contain("unsupported codec");
    }

    [Fact]
    public async Task A_200_result_without_a_video_is_not_a_success()
    {
        AudioStubHandler api = Queue(result: "ve-result-no-video.json");
        FalAiVideoUpscaleClient client = Upscaler(api);
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(handle, "", CancellationToken.None);

        (await poll.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_VENDOR_FAILED");
    }

    [Fact]
    public async Task A_result_url_on_a_private_host_is_refused_and_never_fetched()
    {
        AudioStubHandler api = Queue(result: "ve-result-private-url.json");
        FalAiVideoUpscaleClient client = Upscaler(api);
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);

        Func<Task> poll = () => client.PollAsync(handle, "", CancellationToken.None);

        (await poll.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain("not safe to fetch");
        api.Requests.Should().NotContain(r => r.RequestUri!.Host == "10.0.0.5");
    }

    [Fact]
    public async Task A_download_of_the_wrong_type_or_over_the_cap_is_refused()
    {
        AudioStubHandler html = Queue(cdn: _ => AudioFixtures.Audio(Clip, "text/html"));
        FalAiVideoUpscaleClient client = Upscaler(html);
        VideoEnhanceJobHandle handle = await client.SubmitAsync(Up(), CancellationToken.None);
        Func<Task> wrongType = () => client.PollAsync(handle, "", CancellationToken.None);
        (await wrongType.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain("content type");

        AudioStubHandler big = Queue(cdn: _ =>
        {
            HttpResponseMessage response = AudioFixtures.Audio(Clip, "video/mp4");
            response.Content = new DeclaredLengthContent(FalVideoEnhanceCap + 1);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            return response;
        });
        FalAiVideoUpscaleClient client2 = Upscaler(big);
        VideoEnhanceJobHandle handle2 = await client2.SubmitAsync(Up(), CancellationToken.None);
        Func<Task> over = () => client2.PollAsync(handle2, "", CancellationToken.None);
        (await over.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain("limit");
    }

    private const long FalVideoEnhanceCap = 512L * 1024 * 1024;

    private sealed class DeclaredLengthContent(long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long len)
        {
            len = length;
            return true;
        }
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "not a url", "absolute https URL" },
        { "http://x.test/a.mp4", "absolute https URL" },
        { "https://user:pw@x.test/a.mp4", "absolute https URL" },
        { "", "absolute https URL" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_bad_source_url_is_refused_before_anything_is_sent(string source, string reason)
    {
        AudioStubHandler api = Queue();

        Func<Task> act = () => Upscaler(api).SubmitAsync(Up(source), CancellationToken.None);

        (await act.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain(reason);
        api.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(4.5)]
    [InlineData(double.NaN)]
    public async Task An_out_of_range_upscale_factor_is_refused_before_anything_is_sent(double factor)
    {
        AudioStubHandler api = Queue();

        Func<Task> act = () => Upscaler(api).SubmitAsync(Up(factor: factor), CancellationToken.None);

        (await act.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_REQUEST_INVALID");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Bytedance_refuses_a_720p_output_it_has_no_price_for_and_a_factor_below_its_floor()
    {
        AudioStubHandler api = Queue();
        const string model = "fal-ai/bytedance-upscaler/upscale/video";

        Func<Task> unpriced = () => Upscaler(api).SubmitAsync(Up(model: model, factor: 1.3, width: 640, height: 360), CancellationToken.None);
        (await unpriced.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain("no price");

        Func<Task> low = () => Upscaler(api).SubmitAsync(Up(model: model, factor: 1.05), CancellationToken.None);
        (await low.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_REQUEST_INVALID");
        api.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public async Task Only_a_2x_or_4x_multiplier_is_offered_on_the_hosted_path(int multiplier)
    {
        AudioStubHandler api = Queue();

        Func<Task> act = () => Interpolator(api).SubmitAsync(Interp(multiplier), CancellationToken.None);

        (await act.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_REQUEST_INVALID");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_over_the_120_second_cap_or_with_unmeasured_fields_is_refused()
    {
        AudioStubHandler api = Queue();

        Func<Task> tooLong = () => Upscaler(api).SubmitAsync(Up(seconds: 120.5), CancellationToken.None);
        (await tooLong.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain("120 seconds");
        Func<Task> atCap = () => Upscaler(api).SubmitAsync(Up(seconds: 120), CancellationToken.None);
        await atCap.Should().NotThrowAsync("exactly 120 s is allowed");
        Func<Task> noSize = () => Upscaler(api).SubmitAsync(Up(width: 0), CancellationToken.None);
        await noSize.Should().ThrowAsync<VideoEnhanceException>();
    }

    [Fact]
    public async Task A_model_of_the_other_capability_or_an_unknown_one_is_refused_before_anything_is_sent()
    {
        AudioStubHandler api = Queue();

        Func<Task> rifeAsUpscaler = () => Upscaler(api).SubmitAsync(Up(model: "fal-ai/rife/video"), CancellationToken.None);
        (await rifeAsUpscaler.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_REQUEST_INVALID");
        Func<Task> unknown = () => Interpolator(api).SubmitAsync(Interp(model: "fal-ai/video-upscaler"), CancellationToken.None);
        (await unknown.Should().ThrowAsync<VideoEnhanceException>()).Which.Code.Should().Be("ENHANCE_REQUEST_INVALID");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public void No_catalogued_endpoint_may_send_target_fps_to_topaz_or_sync_mode_anywhere()
    {
        VideoEnhanceModelCatalog catalog = VideoEnhanceModelCatalog.Instance;
        catalog.Find(ReelBolt.Shared.Data.Models.InferenceProviderCapability.VideoUpscale, "fal-ai/topaz/upscale/video")!
            .AllowedFields.Should().NotContain("target_fps");
        catalog.All.Should().OnlyContain(e => !e.AllowedFields.Contains("sync_mode"));
        catalog.All.Should().OnlyContain(e => e.AllowedFields.Contains("video_url"));
    }

    [Fact]
    public void A_blank_key_throws_rather_than_reading_the_environment()
    {
        Action up = () => _ = new FalAiVideoUpscaleClient(new HttpClient(), " ");
        Action interp = () => _ = new FalAiFrameInterpolationClient(new HttpClient(), "");
        up.Should().Throw<ArgumentException>();
        interp.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Describe_answers_the_catalogued_endpoint_and_refuses_an_unknown_one()
    {
        Upscaler(Queue()).Describe("").Id.Should().Be("fal-ai/topaz/upscale/video");
        Interpolator(Queue()).Describe("").Id.Should().Be("fal-ai/rife/video");
        Action act = () => Upscaler(Queue()).Describe("fal-ai/rife/video");
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void The_request_records_never_print_the_presigned_url()
    {
        Up().ToString().Should().NotContain("storage.example.test").And.NotContain("X-Amz");
        Interp().ToString().Should().NotContain("storage.example.test");
    }
}
