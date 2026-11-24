#nullable enable
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The fal SAM 3.1 video-rle arm: the RLE decoder, the response mapping, the request schema, the metering
/// decorator and the resolver. Everything is offline — a stub HTTP handler stands in for the queue, and
/// the real responses are read from T-002's replay pack when it is present (and skipped silently when it
/// is not; this branch does not vendor another ticket's fixtures).
/// </summary>
public class FalAiVideoSegmentationTests
{
    private const string Model = FalAiVideoSegmentationClient.Sam31VideoRle;

    // ================================================================ fixtures

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "inference", "ReelBolt.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        return string.Empty;
    }

    /// <summary>
    /// The replay pack, wherever it is: this branch's own TestData once T-002 merges, or the sibling
    /// worktree it is committed in today. Never copied here.
    /// </summary>
    private static string? FixtureDirectory()
    {
        List<string> candidates = [];
        string? configured = Environment.GetEnvironmentVariable("REELBOLT_SAM31_RLE_FIXTURES");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);

        string root = RepoRoot();
        if (root.Length > 0)
        {
            candidates.Add(Path.Combine(root, "inference", "tests", "ReelBolt.WorkflowEngine.Tests",
                "TestData", "prompted-region-effects", "sam31-rle"));
        }

        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>Accepts both the committed <c>R1-tabby/</c> layout and the lead's <c>R1/tabby/</c> spelling.</summary>
    private static string? ResponsePath(string slug)
    {
        string? directory = FixtureDirectory();
        if (directory is null) return null;

        foreach (string candidate in new[] { slug.Replace('/', '-'), slug })
        {
            string path = Path.Combine(directory, candidate.Replace('/', Path.DirectorySeparatorChar), "response.json");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string? DigestsPath()
    {
        string? directory = FixtureDirectory();
        if (directory is null) return null;
        string path = Path.Combine(directory, "decode-digests.json");
        return File.Exists(path) ? path : null;
    }

    // ================================================================ decoder

    [Fact]
    public void Decodes_one_run_into_a_mask_with_its_area_and_box()
    {
        // 4x3 frame, row-major. Indices 5 and 6 are row 1, columns 1 and 2.
        VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode("5 2", objectId: 7, width: 4, height: 3);

        mask.ObjectId.Should().Be(7);
        mask.Area.Should().Be(2);
        mask.Pixels.Count(p => p == Sam31VideoRleDecoder.ObjectValue).Should().Be(2);
        mask.Pixels[5].Should().Be(Sam31VideoRleDecoder.ObjectValue);
        mask.Pixels[6].Should().Be(Sam31VideoRleDecoder.ObjectValue);
        (mask.X0, mask.Y0, mask.X1, mask.Y1).Should().Be((1, 1, 2, 1));
    }

    [Fact]
    public void Allows_a_run_to_cross_a_row_boundary_because_the_real_data_does()
    {
        // The R1/tabby response has 20,517 row-crossing runs on 76 of its 120 frames: a mask that touches
        // the right edge continues at column 0 of the next row in a linear row-major encoding. 4x3,
        // indices 2..6 = row 0 columns 2,3 then row 1 columns 0,1,2.
        VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode("2 5", objectId: 0, width: 4, height: 3);

        mask.Area.Should().Be(5);
        (mask.X0, mask.Y0, mask.X1, mask.Y1).Should().Be((0, 0, 3, 1));
    }

    [Fact]
    public void Refuses_a_run_that_leaves_the_frame_and_names_the_observed_and_expected_size()
    {
        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(
            () => Sam31VideoRleDecoder.Decode("15 40", objectId: 0, width: 4, height: 4));

        ex.Code.Should().Be("SEGMENTATION_BAD_RESPONSE");
        ex.Message.Should().Contain("outside").And.Contain("4x4").And.Contain("55");
    }

    [Theory]
    [InlineData("1 5 3 2", "sorted")]          // second run starts before the first ends
    [InlineData("3 0", "non-positive")]
    [InlineData("1", "length token was expected")]
    [InlineData("abc 2", "not a non-negative")]
    [InlineData("-1 2", "not a non-negative")]
    public void Refuses_every_malformed_run_list(string rle, string expectedFragment)
    {
        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(
            () => Sam31VideoRleDecoder.Decode(rle, objectId: 0, width: 8, height: 8));

        ex.Message.Should().Contain(expectedFragment);
    }

    [Fact]
    public void A_one_based_reading_refuses_a_start_of_zero_rather_than_guessing()
    {
        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(
            () => Sam31VideoRleDecoder.Decode("0 2", objectId: 0, width: 4, height: 4, RleStartBase.One));

        ex.Message.Should().Contain("starts at 0").And.Contain("1-based");
    }

    [Fact]
    public void An_empty_string_is_a_legitimately_empty_frame()
    {
        VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode(string.Empty, objectId: 0, width: 4, height: 4);

        mask.IsEmpty.Should().BeTrue();
        mask.Area.Should().Be(0);
        mask.Pixels.Should().OnlyContain(p => p == 0);
    }

    // ================================================================ the real responses

    [Fact]
    public void R1_tabby_decodes_120_frames_of_one_object_and_keeps_every_frame_non_empty()
    {
        string? path = ResponsePath("R1/tabby");
        if (path is null) return;

        VideoSegmentationResult result = ReadFixture(path, expectedFrames: 120);

        result.Model.Should().Be(Model);
        (result.Width, result.Height).Should().Be((768, 1344));
        result.Frames.Should().HaveCount(120);
        result.InputFrames.Should().Be(120);
        result.CostUsd.Should().BeNull("fal's document reports no cost");

        foreach (VideoSegmentationFrame frame in result.Frames)
        {
            frame.Objects.Should().ContainSingle("the text-prompted run tracked one object on every frame");
            frame.Objects[0].ObjectId.Should().Be(0);
            frame.Objects[0].Rle.Should().NotBeEmpty();
            frame.Boxes.Should().NotBeNull().And.ContainSingle();
            frame.Boxes![0].ObjectId.Should().Be(0);
            frame.Scores.Should().BeNull("the vendor's scores array is null and objects carry no score");
        }
    }

    [Fact]
    public void R1_turn_a_keeps_its_fourteen_occluded_frames_empty()
    {
        string? path = ResponsePath("R1/turn-a");
        if (path is null) return;

        VideoSegmentationResult result = ReadFixture(path, expectedFrames: 158);

        result.Frames.Should().HaveCount(158);
        result.Frames.Count(f => f.Objects.Count == 0).Should().Be(14,
            "the sibling's provenance measured 14 empty frames on this clip, and that is the occlusion case");
        result.Frames.Should().OnlyContain(f => f.Objects.SequenceEqual(f.Objects.OrderBy(o => o.ObjectId)),
            "objects are ordered by object_id");
        result.Frames.Where(f => f.Objects.Count == 0).Should().OnlyContain(f => f.Boxes == null,
            "an empty frame has no boxes at all — null means 'the vendor returned none'");

        result.Frames.SelectMany(f => f.Objects).Select(o => o.ObjectId).Distinct()
            .Should().BeEquivalentTo([0, 1], "this clip's answer carries a second object slot later on");

        using JsonDocument raw = JsonDocument.Parse(File.ReadAllText(path));
        int twoObjectFrames = raw.RootElement.GetProperty("objects").EnumerateArray()
            .Count(frame => frame.GetArrayLength() == 2);
        twoObjectFrames.Should().Be(59,
            "on these frames the top-level rle and boxes are the UNION of both objects, not object 0's copy; " +
            "the reader therefore only requires equality on single-object frames");
        uint unionMatchesNeither = 0;
        int index = 0;
        foreach (JsonElement frame in raw.RootElement.GetProperty("objects").EnumerateArray())
        {
            if (frame.GetArrayLength() == 2)
            {
                string top = raw.RootElement.GetProperty("rle")[index].GetString()!;
                JsonElement[] objects = frame.EnumerateArray().ToArray();
                if (top != objects[0].GetProperty("rle").GetString() && top != objects[1].GetProperty("rle").GetString())
                    unionMatchesNeither++;
            }

            index++;
        }

        unionMatchesNeither.Should().Be(59u,
            "measured, not assumed: the union mask equals neither object's mask on every two-object frame");
    }

    [Fact]
    public void R10_tabby_decodes_120_frames()
    {
        string? path = ResponsePath("R10/tabby");
        if (path is null) return;

        VideoSegmentationResult result = ReadFixture(path, expectedFrames: 120);

        result.Frames.Should().HaveCount(120);
        result.Frames.Should().OnlyContain(f => f.Objects.Count == 1 && f.Objects[0].ObjectId == 0);
    }

    [Fact]
    public void The_decoded_stack_reproduces_the_pinned_sha256_in_the_provenance_pack()
    {
        string? path = ResponsePath("R1/tabby");
        string? digestsPath = DigestsPath();
        if (path is null || digestsPath is null) return;

        using JsonDocument digests = JsonDocument.Parse(File.ReadAllText(digestsPath));
        string expected = digests.RootElement
            .GetProperty("fixtures").GetProperty("R1-tabby").GetProperty("decodedMaskSha256").GetString()!;
        int width = digests.RootElement.GetProperty("width").GetInt32();
        int height = digests.RootElement.GetProperty("height").GetInt32();

        using JsonDocument response = JsonDocument.Parse(File.ReadAllText(path));
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int frames = 0;
        foreach (JsonElement frame in response.RootElement.GetProperty("rle").EnumerateArray())
        {
            VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode(frame.GetString(), 0, width, height);
            hash.AppendData(mask.Pixels);
            frames++;
        }

        frames.Should().Be(120);
        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant().Should().Be(expected,
            "a 0-based decode reproduces the sibling's committed mattes byte for byte; a 1-based one does not, " +
            "and this is the evidence the decoder's base comes from");
    }

    [Fact]
    public void Box_agreement_is_measured_against_the_vendors_own_box_not_assumed()
    {
        string? path = ResponsePath("R1/tabby");
        if (path is null) return;

        using JsonDocument response = JsonDocument.Parse(File.ReadAllText(path));
        int worst = int.MaxValue;
        int checkedFrames = 0;
        foreach (JsonElement objects in response.RootElement.GetProperty("objects").EnumerateArray())
        {
            JsonElement first = objects.EnumerateArray().First();
            VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode(
                first.GetProperty("rle").GetString(), 0, 768, 1344);
            double[] box = first.GetProperty("box").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            double left = (box[0] - box[2] / 2) * 768 - 2, right = (box[0] + box[2] / 2) * 768 + 2;
            double top = (box[1] - box[3] / 2) * 1344 - 2, bottom = (box[1] + box[3] / 2) * 1344 + 2;

            int inside = 0;
            for (int y = 0; y < 1344; y++)
            {
                if (y < top || y > bottom) continue;
                for (int x = 0; x < 768; x++)
                {
                    if (mask.Pixels[y * 768 + x] == 0) continue;
                    if (x >= left && x <= right) inside++;
                }
            }

            double percent = inside * 100.0 / mask.Area;
            if (percent < worst) worst = (int)Math.Floor(percent);
            checkedFrames++;
        }

        checkedFrames.Should().Be(120);
        worst.Should().BeGreaterThanOrEqualTo((int)Sam31VideoRleResponseReader.MinBoxAgreementPercent,
            "the response reader refuses a mask that falls outside the vendor's own box; the real minimum is " +
            "98.8% and the floor must sit below it");
    }

    /// <summary>Reads one fixture with the frame count the clip's own probe measured and the default object cap.</summary>
    private static VideoSegmentationResult ReadFixture(string path, int expectedFrames, int maxObjects = 4)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return Sam31VideoRleResponseReader.Read(document.RootElement, Model, 768, 1344, expectedFrames, maxObjects);
    }

    // ================================================================ request schema

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
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FixedProbe : IVideoSegmentationFrameSizeProbe
    {
        private readonly VideoSegmentationFrameSize _size;
        public int Calls { get; private set; }

        public FixedProbe(int width, int height, int? frameCount = null) =>
            _size = new VideoSegmentationFrameSize(width, height, frameCount);

        public Task<VideoSegmentationFrameSize> ProbeAsync(Uri videoUrl, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_size);
        }
    }

    /// <summary>A one-frame answer whose single object's mask sits inside its box.</summary>
    private const string OneFrame =
        "{\"rle\":[\"0 2\"],\"objects\":[[{\"object_id\":0,\"rle\":\"0 2\"," +
        "\"box\":[0.25,0.25,0.5,0.5]}]],\"metadata\":[{\"index\":0,\"score\":null," +
        "\"box\":[0.25,0.25,0.5,0.5]}],\"boxes\":[[0.25,0.25,0.5,0.5]],\"scores\":null}";

    private static FalAiVideoSegmentationClient Client(
        string resultJson,
        IVideoSegmentationFrameSizeProbe? probe,
        out StubHandler api,
        string pollStatus = "COMPLETED",
        Guid? providerId = null)
    {
        StubHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post) return Json(
                "{\"request_id\":\"req-1\",\"status_url\":\"https://queue.fal.run/m/requests/req-1/status\"," +
                "\"response_url\":\"https://queue.fal.run/m/requests/req-1\"}");
            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                return Json("{\"status\":\"" + pollStatus + "\"}");
            return Json(resultJson);
        });
        api = handler;

        HttpClient http = new(handler) { BaseAddress = new Uri("https://queue.fal.run") };
        return new FalAiVideoSegmentationClient(
            http, "fal-key", probe, providerId,
            pollInterval: TimeSpan.Zero,
            delay: (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task Submits_the_exact_documented_body_with_the_store_io_header()
    {
        FixedProbe probe = new(768, 1344);
        FalAiVideoSegmentationClient client = Client(OneFrame, probe, out StubHandler api, providerId: Guid.Parse("11111111-2222-3333-4444-555555555555"));

        VideoSegmentationResult result = await client.SegmentVideoAsync(
            new VideoSegmentationRequest(
                Model: Model,
                VideoUrl: new Uri("https://files.example.com/clip.mp4?sig=short"),
                Prompt: "  the\n  cat ",
                PointPrompts:
                [
                    new VideoSegmentationPointPrompt(380.0 / 767.0, 880.0 / 1343.0, FrameIndex: 48, ObjectId: 1, Label: "1")
                ],
                BoxPrompts:
                [
                    new VideoSegmentationBoxPrompt(0.25, 0.25, 0.5, 0.5, FrameIndex: 0, ObjectId: 1, Label: "0")
                ],
                MaxObjects: 4,
                DetectionThreshold: 0.5,
                Timeout: TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        api.Requests[0].Method.Should().Be(HttpMethod.Post);
        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/fal-ai/sam-3-1/video-rle");
        api.Requests[0].Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Key fal-key");
        api.Requests[0].Headers.GetValues("X-Fal-Store-IO").Should().ContainSingle().Which
            .Should().Be("0", "no vendor-side retention of customer footage");

        JsonObject body = JsonNode.Parse(api.Bodies[0])!.AsObject();
        body["video_url"]!.GetValue<string>().Should().Be("https://files.example.com/clip.mp4?sig=short");
        body["prompt"]!.GetValue<string>().Should().Be("the cat", "control characters stripped, whitespace collapsed");
        body["max_num_objects"]!.GetValue<int>().Should().Be(4);
        body["detection_threshold"]!.GetValue<double>().Should().Be(0.5);
        body["apply_mask"]!.GetValue<bool>().Should().BeFalse();
        body["return_object_rles"]!.GetValue<bool>().Should().BeTrue();
        body["boundingbox_zip"]!.GetValue<bool>().Should().BeFalse();

        JsonNode point = body["point_prompts"]!.AsArray()[0]!;
        point["object_id"]!.GetValue<int>().Should().Be(1);
        point["x"]!.GetValue<int>().Should().Be(380, "the contract's 0..1 is converted to the vendor's source pixels");
        point["y"]!.GetValue<int>().Should().Be(880);
        point["label"]!.GetValue<int>().Should().Be(1);
        point["frame_index"]!.GetValue<int>().Should().Be(48);

        JsonNode box = body["box_prompts"]!.AsArray()[0]!;
        box["x_min"]!.GetValue<int>().Should().Be(192);
        box["y_min"]!.GetValue<int>().Should().Be(336);
        box["x_max"]!.GetValue<int>().Should().Be(575);
        box["y_max"]!.GetValue<int>().Should().Be(1007);
        box.AsObject().ContainsKey("label").Should().BeFalse("the vendor box schema declares no label");

        body.AsObject().Select(pair => pair.Key).Should().BeEquivalentTo(
        [
            "video_url", "prompt", "point_prompts", "box_prompts", "max_num_objects",
            "detection_threshold", "apply_mask", "return_object_rles", "boundingbox_zip"
        ], "an undocumented field would be silently ignored by the vendor");

        api.Requests[1].RequestUri!.ToString().Should().Be("https://queue.fal.run/m/requests/req-1/status");
        api.Requests[2].RequestUri!.ToString().Should().Be("https://queue.fal.run/m/requests/req-1");

        result.Width.Should().Be(768);
        result.Height.Should().Be(1344);
        result.ProviderFingerprint.Should().Be(FalAiVideoSegmentationClient.Fingerprint(
            Guid.Parse("11111111-2222-3333-4444-555555555555"), Model));
        result.ProviderFingerprint.Should().NotContain("fal-key", "no key material crosses the seam");
        probe.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Clamps_the_vendor_knobs_instead_of_sending_out_of_range_values()
    {
        FalAiVideoSegmentationClient client = Client(OneFrame, new FixedProbe(4, 4), out StubHandler api);

        await client.SegmentVideoAsync(
            new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "cat",
                MaxObjects: 999, DetectionThreshold: -1),
            CancellationToken.None);

        JsonObject body = JsonNode.Parse(api.Bodies[0])!.AsObject();
        body["max_num_objects"]!.GetValue<int>().Should().Be(16);
        body["detection_threshold"]!.GetValue<double>().Should().Be(0.01);
    }

    [Fact]
    public async Task Refuses_a_model_whose_schema_has_not_been_read_before_any_request()
    {
        FalAiVideoSegmentationClient client = Client(OneFrame, new FixedProbe(4, 4), out StubHandler api);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentVideoAsync(
            new VideoSegmentationRequest("fal-ai/sam2/video", new Uri("https://files.example.com/c.mp4"), "cat"),
            CancellationToken.None));

        api.Requests.Should().BeEmpty("an unknown endpoint must cost nothing");
    }

    [Fact]
    public async Task Refuses_a_missing_frame_size_probe_before_any_request()
    {
        FalAiVideoSegmentationClient client = Client(OneFrame, probe: null, out StubHandler api);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SegmentVideoAsync(
                new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "cat"),
                CancellationToken.None));

        ex.Message.Should().Contain("IVideoSegmentationFrameSizeProbe").And.Contain("width");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_an_empty_request_that_would_segment_nothing_for_money()
    {
        FalAiVideoSegmentationClient client = Client(OneFrame, new FixedProbe(4, 4), out StubHandler api);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SegmentVideoAsync(
            new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "   "),
            CancellationToken.None));

        api.Requests.Should().BeEmpty();
    }

    // ================================================================ faults

    [Fact]
    public async Task An_unrecognised_queue_status_fails_loudly()
    {
        FalAiVideoSegmentationClient client = Client(OneFrame, new FixedProbe(4, 4), out _, pollStatus: "PAYLOADED");

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.SegmentVideoAsync(
            new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "cat"),
            CancellationToken.None));

        ex.Message.Should().Contain("PAYLOADED");
    }

    [Fact]
    public async Task A_run_outside_the_frame_fails_with_the_bad_response_code()
    {
        const string bad = "{\"rle\":[\"0 99\"],\"objects\":[[{\"object_id\":0,\"rle\":\"0 99\"}]]}";
        FalAiVideoSegmentationClient client = Client(bad, new FixedProbe(4, 4), out _);

        VideoSegmentationBadResponseException ex = await Assert.ThrowsAsync<VideoSegmentationBadResponseException>(
            () => client.SegmentVideoAsync(
                new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "cat"),
                CancellationToken.None));

        ex.Code.Should().Be("SEGMENTATION_BAD_RESPONSE");
        ex.Message.Should().Contain("outside");
    }

    [Fact]
    public async Task A_mask_that_disagrees_with_the_vendors_box_is_reported_not_refused()
    {
        // One object covering row 0 of a 4x4 frame, with a box down at the bottom-right: nothing of the
        // mask is inside it. The vendor's boxes are approximate metadata and are clipped on an
        // edge-touching object (D-05), so this is a recorded diagnostic — the answer still decodes.
        const string shifted =
            "{\"rle\":[\"0 4\"],\"objects\":[[{\"object_id\":0,\"rle\":\"0 4\"," +
            "\"box\":[0.75,0.8,0.1,0.1]}]]}";
        FalAiVideoSegmentationClient client = Client(shifted, new FixedProbe(4, 4), out _);

        VideoSegmentationResult result = await client.SegmentVideoAsync(
            new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "cat"),
            CancellationToken.None);

        result.Frames.Should().ContainSingle();
        result.Frames[0].Objects.Should().ContainSingle();
        result.Frames[0].Boxes.Should().ContainSingle("the vendor's box is still carried, as metadata");

        VideoSegmentationDiagnostics diagnostics = result.Diagnostics!;
        diagnostics.BoxAgreementMin.Should().Be(0.0);
        diagnostics.BoxAgreementFrame.Should().Be(0);
        diagnostics.BoxAgreementObject.Should().Be(0);
        diagnostics.ObservedMaxObjectSlots.Should().Be(1);
    }

    [Theory]
    [InlineData("{\"rle\":[],\"objects\":[]}", "zero frames")]
    [InlineData("{\"rle\":[\"0 2\"],\"objects\":[]}", "has 1 entries but 'objects' has 0")]
    [InlineData("{\"rle\":[\"0 2\",\"0 2\"],\"objects\":[[{\"object_id\":0,\"rle\":\"0 2\"}]]}", "'rle' has 2 entries")]
    [InlineData("{\"rle\":[\"0 2\"],\"objects\":[[{\"object_id\":0,\"rle\":\"\"}]]}", "empty mask")]
    [InlineData("{\"rle\":[\"0 2\"],\"objects\":[[{\"object_id\":0,\"rle\":\"0 2\",\"box\":[1,2,3]}]]}", "four numbers")]
    [InlineData("{\"rle\":[\"0 2\"],\"objects\":[[{\"object_id\":0,\"rle\":\"1 2\"}]]}", "top-level rle does not match")]
    [InlineData("{\"objects\":[[{\"rle\":\"0 2\"}]]}", "no integer object_id")]
    public async Task Every_malformed_response_shape_is_refused_with_the_bad_response_code(string resultJson, string fragment)
    {
        FalAiVideoSegmentationClient client = Client(resultJson, new FixedProbe(4, 4), out _);

        VideoSegmentationBadResponseException ex = await Assert.ThrowsAsync<VideoSegmentationBadResponseException>(
            () => client.SegmentVideoAsync(
                new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "cat"),
                CancellationToken.None));

        ex.Message.Should().Contain(fragment);
    }

    [Fact]
    public async Task A_frame_count_that_contradicts_the_probe_is_refused()
    {
        FalAiVideoSegmentationClient client = Client(OneFrame, new FixedProbe(4, 4, frameCount: 99), out _);

        VideoSegmentationBadResponseException ex = await Assert.ThrowsAsync<VideoSegmentationBadResponseException>(
            () => client.SegmentVideoAsync(
                new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "cat"),
                CancellationToken.None));

        ex.Message.Should().Contain("1 frame entries").And.Contain("99");
    }

    [Fact]
    public async Task A_result_body_over_the_byte_ceiling_is_refused_before_it_is_parsed()
    {
        // F-2's body cap, wired where it belongs: the segmentation arm's own fetch. The vendor DECLARES a
        // body one byte over the ceiling, so the refusal costs no 64 MiB allocation in a test and proves
        // the constant reaches FalQueueClient rather than only being defined.
        StubHandler handler = new(request =>
        {
            if (request.Method == HttpMethod.Post) return Json(
                "{\"request_id\":\"req-1\",\"status_url\":\"https://queue.fal.run/m/requests/req-1/status\"," +
                "\"response_url\":\"https://queue.fal.run/m/requests/req-1\"}");
            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                return Json("{\"status\":\"COMPLETED\"}");

            HttpResponseMessage oversized = Json("{}");
            oversized.Content.Headers.ContentLength = FalAiVideoSegmentationClient.MaxResultBytes + 1L;
            return oversized;
        });

        HttpClient http = new(handler) { BaseAddress = new Uri("https://queue.fal.run") };
        FalAiVideoSegmentationClient client = new(
            http, "fal-key", new FixedProbe(4, 4), pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SegmentVideoAsync(
            new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "cat"),
            CancellationToken.None));

        ex.Message.Should().Contain(FalAiVideoSegmentationClient.MaxResultBytes + "-byte ceiling")
            .And.Contain("refused unread");
        handler.Requests.Should().HaveCount(3, "submit, one status poll and the refused result fetch — nothing more");
    }

    // ================================================================ prompt handling

    [Theory]
    [InlineData("  the\n  cat  ", "the cat")]
    [InlineData("a\tb\0c", "a bc")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Sanitises_the_prompt_without_sending_control_characters(string? input, string expected)
    {
        FalAiVideoSegmentationClient.SanitizePrompt(input).Should().Be(expected);
    }

    [Fact]
    public void Caps_the_prompt_at_the_house_limit()
    {
        string sanitized = FalAiVideoSegmentationClient.SanitizePrompt(new string('x', 500));

        sanitized.Should().HaveLength(FalAiVideoSegmentationClient.MaxPromptLength);
    }

    // ================================================================ metering

    private sealed class RecordingRecorder : IUsageRecorder
    {
        public List<UsageRecordRequest> Requests { get; } = [];

        public ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(0L);
        }
    }

    private sealed class StubSegmentationClient : IVideoSegmentationClient
    {
        private readonly VideoSegmentationResult? _result;
        private readonly Exception? _failure;

        private StubSegmentationClient(VideoSegmentationResult? result, Exception? failure) =>
            (_result, _failure) = (result, failure);

        public static StubSegmentationClient Returning(VideoSegmentationResult result) => new(result, null);

        public static StubSegmentationClient Throwing(Exception failure) => new(null, failure);

        public Task<VideoSegmentationResult> SegmentVideoAsync(VideoSegmentationRequest request, CancellationToken ct) =>
            _failure is not null ? Task.FromException<VideoSegmentationResult>(_failure) : Task.FromResult(_result!);
    }

    private static VideoSegmentationResult ResultWithFrames(int frames, int? inputFrames = null) =>
        new(Model, 768, 1344,
            Enumerable.Range(0, frames)
                .Select(i => new VideoSegmentationFrame(i, [new VideoSegmentationObjectRle(0, "0 1")]))
                .ToList(),
            ProviderFingerprint: "FalAi|-|" + Model,
            CostUsd: null,
            InputFrames: inputFrames ?? frames);

    [Fact]
    public async Task Meters_the_frames_of_input_rounded_up_to_the_vendors_block()
    {
        RecordingRecorder recorder = new();
        ResolvedImageAnalysisProvider provider = new(
            Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, InferenceProviderCapability.VideoSegmentation,
            "https://queue.fal.run", Model, "fal-key", 300);

        MeteringVideoSegmentationClient client = new(
            StubSegmentationClient.Returning(ResultWithFrames(120)), provider, recorder);

        await client.SegmentVideoAsync(
            new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "cat"),
            CancellationToken.None);

        UsageRecordRequest recorded = recorder.Requests.Should().ContainSingle().Subject;
        recorded.Kind.Should().Be(UsageKind.VideoSegmentationFrames);
        recorded.Quantity.Should().Be(128m, "120 real frames bill as 8 whole 16-frame blocks; the card is per frame");
        recorded.ProviderId.Should().Be(provider.ProviderId);
        recorded.ProviderKind.Should().Be(InferenceProviderKind.FalAi);
        recorded.ModelName.Should().Be(Model, "the model that RAN is the endpoint id, not the row's probe model");
    }

    [Fact]
    public async Task A_failed_call_records_nothing()
    {
        RecordingRecorder recorder = new();
        ResolvedImageAnalysisProvider provider = new(
            Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, InferenceProviderCapability.VideoSegmentation,
            "https://queue.fal.run", Model, "fal-key", 300);

        MeteringVideoSegmentationClient client = new(
            StubSegmentationClient.Throwing(new VideoSegmentationBadResponseException("SEGMENTATION_BAD_RESPONSE: x")),
            provider, recorder);

        await Assert.ThrowsAsync<VideoSegmentationBadResponseException>(() => client.SegmentVideoAsync(
            new VideoSegmentationRequest(Model, new Uri("https://files.example.com/c.mp4"), "cat"),
            CancellationToken.None));

        recorder.Requests.Should().BeEmpty("fal bills nothing for a failed request, so neither may the ledger");
    }
}
