#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Security finding F-2 (security verdict <c>prompted-region-effects</c>): the vendor chooses both the
/// size of its answer and its shape, so neither may be read on trust. The body is read through a byte
/// counter and refused past a documented ceiling. Inside <see cref="Sam31VideoRleResponseReader"/> the
/// frame count is a precondition and the object slots are WORK BOUNDS (T-015: the request's
/// <c>max_num_objects</c> is not one) — all refused BEFORE a single run is decoded, not compared against
/// the result afterwards. Offline: no queue, no key, no live call.
/// </summary>
public sealed class FalAiVideoSegmentationBoundsTests
{
    private const string Model = FalAiVideoSegmentationClient.Sam31VideoRle;

    // ================================================================ the result body's ceiling

    /// <summary>Answers every request with the content the test builds.</summary>
    private sealed class FixedHandler(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
    }

    /// <summary>A body with no <c>Length</c> and no <c>CanSeek</c>, so no Content-Length header is sent.</summary>
    private sealed class UnseekableBody(byte[] bytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(count, bytes.Length - _position);
            if (take <= 0) return 0;
            Array.Copy(bytes, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static FalQueueClient Queue(HttpClient http) =>
        new(http, "fal-key", downloadClient: null,
            pollInterval: TimeSpan.Zero,
            pollTimeout: TimeSpan.FromSeconds(5),
            delay: (_, _) => Task.CompletedTask);

    private const string ResultUrl = "https://queue.fal.run/m/requests/r1";

    [Fact]
    public async Task A_result_body_that_declares_more_than_the_ceiling_is_refused_unread()
    {
        // The vendor states the size, so the refusal costs one header parse and no body read at all.
        using HttpClient http = new(new FixedHandler(() =>
        {
            StringContent declared = new("{}", Encoding.UTF8, "application/json");
            declared.Headers.ContentLength = 4_096;
            return declared;
        }));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Queue(http).TryFetchResultAsync(ResultUrl, Model, CancellationToken.None, maxResultBytes: 1_024));

        ex.Message.Should().Contain("4096-byte").And.Contain("1024-byte ceiling").And.Contain("refused unread");
    }

    [Fact]
    public async Task A_result_body_that_never_states_its_size_is_counted_and_refused_past_the_ceiling()
    {
        // A chunked body (or one whose Content-Length lies) reaches the running counter instead.
        byte[] payload = Encoding.UTF8.GetBytes("{\"objects\":[]" + new string('x', 512) + "}");
        payload.Length.Should().BeGreaterThan(128, "the body really is over the ceiling the test names");

        using StreamContent streamed = new(new UnseekableBody(payload));
        streamed.Headers.ContentLength.Should().BeNull(
            "this test is about the counted read, not the header check, so no Content-Length may be sent");
        using HttpClient http = new(new FixedHandler(() => streamed));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Queue(http).TryFetchResultAsync(ResultUrl, Model, CancellationToken.None, maxResultBytes: 128));

        ex.Message.Should().Contain("exceeds the 128-byte ceiling").And.Contain("refused rather than buffered");
    }

    [Fact]
    public async Task A_result_body_inside_the_ceiling_is_still_read_whole()
    {
        const string document = "{\"objects\":[],\"rle\":[]}";
        using HttpClient http = new(new FixedHandler(() => new StringContent(document, Encoding.UTF8, "application/json")));

        FalResultFetch fetch = await Queue(http).TryFetchResultAsync(
            ResultUrl, Model, CancellationToken.None, maxResultBytes: 4_096);

        fetch.StatusCode.Should().Be(200);
        fetch.Body.Should().Be(document);
        fetch.Output.Should().NotBeNull();
    }

    [Fact]
    public void The_segmentation_clients_ceiling_is_the_documented_sixty_four_mebibytes()
    {
        FalAiVideoSegmentationClient.MaxResultBytes.Should().Be(64 * 1024 * 1024,
            "a legitimate two-object 60 s answer at the fixtures' geometry is ~36 MB; the ceiling is measured, " +
            "not guessed, and the decode work is bounded by the reader's own count preconditions");
    }

    // ================================================================ counts are preconditions

    /// <summary>
    /// Frame 0's mask is deliberately outside the frame: if the count precondition did not run first, the
    /// decoder's own "outside" refusal would be what surfaces. The message proves which check fired.
    /// </summary>
    private const string FrameWithAnInvalidRun =
        "{\"rle\":[\"0 99\"],\"objects\":[[{\"object_id\":0,\"rle\":\"0 99\"}]]}";

    [Fact]
    public void A_document_with_more_frames_than_the_probe_measured_is_refused_before_any_run_is_decoded()
    {
        // Twenty frame entries for a one-frame clip — far outside the 1-frame tolerance (5% of 1, rounded
        // up) — each carrying a run the decoder would reject. The count must still be
        // refused before any decode.
        string document =
            "{\"objects\":[" + string.Join(",", Enumerable.Range(0, 20)
                .Select(_ => "[{\"object_id\":0,\"rle\":\"0 99\"}]")) + "]}";
        using JsonDocument parsed = JsonDocument.Parse(document);

        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(() =>
            Sam31VideoRleResponseReader.Read(parsed.RootElement, Model, 4, 4, expectedFrames: 1, maxObjects: 4));

        ex.Code.Should().Be("SEGMENTATION_BAD_RESPONSE");
        ex.Message.Should().Contain("20 frame entries").And.Contain("measured at 1 frames")
            .And.Contain("tolerance");
        ex.Message.Should().NotContain("falls outside", "the frame count was refused before a single run was decoded");
    }

    [Fact]
    public void A_document_with_a_mismatched_frame_count_is_refused_when_the_probe_measured_more()
    {
        using JsonDocument parsed = JsonDocument.Parse(FrameWithAnInvalidRun);

        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(() =>
            Sam31VideoRleResponseReader.Read(parsed.RootElement, Model, 4, 4, expectedFrames: 99, maxObjects: 4));

        ex.Message.Should().Contain("1 frame entries").And.Contain("measured at 99 frames");
        ex.Message.Should().NotContain("falls outside");
    }

    /// <summary>One frame of <paramref name="slots"/> object entries, each carrying the same run.</summary>
    private static string FrameWithSlots(int slots, string rle = "0 1") =>
        "[" + string.Join(",", Enumerable.Range(0, slots)
            .Select(id => "{\"object_id\":" + id + ",\"rle\":\"" + rle + "\"}")) + "]";

    [Fact]
    public void More_object_slots_than_the_request_asked_for_are_accepted_and_the_count_is_reported()
    {
        // T-015 / F2/FACE: max_num_objects is a REQUEST parameter the vendor does not honour as a cap on
        // the slots it returns — a real answer to a request for 4 carried 6-10 on every frame. Extra slots
        // are ordinary output, and the analyze path selects one object, so they cannot refuse the document.
        using JsonDocument parsed = JsonDocument.Parse("{\"objects\":[" + FrameWithSlots(6) + "]}");

        VideoSegmentationResult result = Sam31VideoRleResponseReader.Read(
            parsed.RootElement, Model, 4, 4, expectedFrames: 1, maxObjects: 4);

        result.Frames.Should().ContainSingle();
        result.Frames[0].Objects.Should().HaveCount(6);
        result.Diagnostics!.ObservedMaxObjectSlots.Should().Be(6);
        result.Diagnostics.TotalObjectSlots.Should().Be(6);
    }

    [Fact]
    public void A_frame_over_the_structural_slot_ceiling_is_refused_before_its_runs_are_decoded()
    {
        // One slot past MaxObjectSlots. Every run is outside a 4x4 frame, so "outside" surfacing would
        // prove the decoder ran before the ceiling was enforced.
        using JsonDocument parsed = JsonDocument.Parse(
            "{\"objects\":[" + FrameWithSlots(Sam31VideoRleResponseReader.MaxObjectSlots + 1, "0 99") + "]}");

        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(() =>
            Sam31VideoRleResponseReader.Read(parsed.RootElement, Model, 4, 4, expectedFrames: 1, maxObjects: 4));

        ex.Code.Should().Be("SEGMENTATION_BAD_RESPONSE");
        ex.Message.Should().Contain("carries 33 object slots").And.Contain("32-slot ceiling");
        ex.Message.Should().NotContain("falls outside", "the ceiling was enforced before this frame was decoded");
    }

    [Fact]
    public void A_frame_exactly_at_the_structural_slot_ceiling_is_accepted()
    {
        // The ceiling is inclusive: 32 slots is the last accepted frame, 33 is refused above.
        using JsonDocument parsed = JsonDocument.Parse(
            "{\"objects\":[" + FrameWithSlots(Sam31VideoRleResponseReader.MaxObjectSlots) + "]}");

        VideoSegmentationResult result = Sam31VideoRleResponseReader.Read(
            parsed.RootElement, Model, 4, 4, expectedFrames: 1, maxObjects: 4);

        result.Frames[0].Objects.Should().HaveCount(Sam31VideoRleResponseReader.MaxObjectSlots);
        result.Diagnostics!.ObservedMaxObjectSlots.Should().Be(Sam31VideoRleResponseReader.MaxObjectSlots);
    }

    [Fact]
    public void A_document_over_the_total_object_slot_work_bound_is_refused_naming_frames_slots_and_the_bound()
    {
        // DECODE WORK is the SUM of slots, because a slot is one run: 7,813 frames of 32 slots is 250,016
        // slots, past the 250,000 bound while every frame is at (not over) the slot ceiling, so only the
        // work bound can refuse it. The runs are all outside a 4x4 frame, so a decoder refusal would
        // surface as "falls outside" had the bound not fired first.
        const int frames = 7_813;
        string frame = FrameWithSlots(Sam31VideoRleResponseReader.MaxObjectSlots, "0 99");
        string document = "{\"objects\":[" + string.Join(",", Enumerable.Repeat(frame, frames)) + "]}";
        using JsonDocument parsed = JsonDocument.Parse(document);

        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(() =>
            Sam31VideoRleResponseReader.Read(parsed.RootElement, Model, 4, 4, expectedFrames: null, maxObjects: 4));

        ex.Code.Should().Be("SEGMENTATION_BAD_RESPONSE");
        ex.Message.Should().Contain(frames + " frames")
            .And.Contain((frames * Sam31VideoRleResponseReader.MaxObjectSlots) + " object slots in total")
            .And.Contain("250000-slot document work bound");
        ex.Message.Should().NotContain("falls outside", "the work bound was enforced before any run was decoded");
    }

    [Fact]
    public void A_long_clip_with_about_one_slot_per_frame_is_accepted_because_work_is_the_sum_not_the_product()
    {
        // The live regression of 2026-10-19: 825 frames carrying 892 slots is 892 decodes and 1650 here,
        // never 825 x 892. The product form refused an ordinary answer before a single mask was decoded.
        const int frames = 825;
        string frame = FrameWithSlots(2, "0 1");
        string document = "{\"objects\":[" + string.Join(",", Enumerable.Repeat(frame, frames)) + "]}";
        using JsonDocument parsed = JsonDocument.Parse(document);

        VideoSegmentationResult result = Sam31VideoRleResponseReader.Read(
            parsed.RootElement, Model, 4, 4, expectedFrames: frames, maxObjects: 4);

        result.Frames.Should().HaveCount(frames);
        result.Diagnostics!.TotalObjectSlots.Should().Be(frames * 2);
        result.Diagnostics.FramesPadded.Should().Be(0);
    }

    [Fact]
    public void The_slot_ceiling_and_the_work_bound_are_the_documented_numbers()
    {
        Sam31VideoRleResponseReader.MaxObjectSlots.Should().Be(32);
        Sam31VideoRleResponseReader.MaxFrameSlotWork.Should().Be(250_000);
    }

    [Fact]
    public void A_reader_with_no_caller_frame_count_falls_back_to_the_absolute_ceiling()
    {
        // The ceiling is the largest frame count any configured cap can buy, so it can never refuse a
        // document this platform's own configuration produced — and the pricing helper agrees by
        // construction rather than by a second literal.
        Sam31VideoRleResponseReader.MaxFrames.Should().Be(
            SegmentationCreditPricing.PricedFramesFor(SegmentationLimits.MaxSeconds));

        using JsonDocument parsed = JsonDocument.Parse("{\"objects\":[[{\"object_id\":0,\"rle\":\"0 1\"}]]}");

        VideoSegmentationResult result = Sam31VideoRleResponseReader.Read(
            parsed.RootElement, Model, 4, 4, expectedFrames: null, maxObjects: 4);

        result.InputFrames.Should().Be(1);
        result.Frames.Should().ContainSingle("a probe that could not state a frame count still decodes a small document");
    }

    [Fact]
    public void A_non_positive_object_cap_is_the_callers_bug_and_is_refused()
    {
        using JsonDocument parsed = JsonDocument.Parse(FrameWithAnInvalidRun);

        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(() =>
            Sam31VideoRleResponseReader.Read(parsed.RootElement, Model, 4, 4, expectedFrames: 1, maxObjects: 0));

        ex.Message.Should().Contain("max_num_objects 0");
    }
}
