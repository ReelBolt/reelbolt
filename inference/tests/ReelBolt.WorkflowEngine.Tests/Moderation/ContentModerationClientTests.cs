using System;
using System.Collections.Generic;
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
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// Pins the OpenAI-compatible moderation wire contract with a fake <see cref="HttpMessageHandler"/>:
/// the POST path, one input per request (a plain text string, or a single data-URL image), the
/// response union across the fan-out, the vendor error summary, and the two distinct cancellation
/// classes (timeout = unavailable, caller cancellation = re-thrown). No live call.
/// </summary>
public sealed class ContentModerationClientTests
{
    private const string Endpoint = "https://moderation.example.test/v1";
    private const string Model = "omni-moderation-2024-09-26";

    private static ContentModerationOptions Options() => new()
    {
        Model = Model,
        TimeoutSeconds = 1,
        MaxTextChars = 8000,
        MaxImagesPerRequest = 20
    };

    private static ModerationImageInput Image(byte[]? bytes = null) =>
        new("image/jpeg", bytes ?? [0xFF, 0xD8, 0xFF, 0x01]);

    [Fact]
    public async Task Posts_to_endpoint_moderations_with_the_model_and_a_text_part()
    {
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "sk-test", Options(), http);

        await client.ClassifyAsync(new ModerationClassifyRequest("hello world", [], Model), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Uri.Should().Be(new Uri(Endpoint + "/moderations"));
        handler.Requests[0].Authorization.Should().Be("Bearer sk-test");

        using JsonDocument body = JsonDocument.Parse(handler.Requests[0].Body);
        body.RootElement.GetProperty("model").GetString().Should().Be(Model);
        JsonElement input = body.RootElement.GetProperty("input");
        input.ValueKind.Should().Be(JsonValueKind.String, "the base input shape is a plain string");
        input.GetString().Should().Be("hello world");
    }

    [Fact]
    public async Task Sends_an_image_as_a_base64_data_url()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0x02];
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "sk-test", Options(), http);

        await client.ClassifyAsync(
            new ModerationClassifyRequest(null, [new ModerationImageInput("image/jpeg", jpeg)], Model),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.Requests.Single().Body);
        JsonElement part = body.RootElement.GetProperty("input").EnumerateArray().Single();
        part.GetProperty("type").GetString().Should().Be("image_url");
        part.GetProperty("image_url").GetProperty("url").GetString()
            .Should().Be("data:image/jpeg;base64," + Convert.ToBase64String(jpeg));
    }

    [Fact]
    public async Task Omits_the_authorization_header_when_the_key_is_empty()
    {
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, string.Empty, Options(), http);

        await client.ClassifyAsync(new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        handler.Requests.Single().Authorization.Should().BeNull();
    }

    [Fact]
    public async Task Unions_results_flagged_scores_categories_and_input_types()
    {
        const string textResponse = """
            {
              "results": [
                {
                  "flagged": false,
                  "categories": { "violence": false, "sexual": false },
                  "category_scores": { "violence": 0.20, "sexual": 0.40 },
                  "category_applied_input_types": { "violence": ["text"] }
                }
              ]
            }
            """;
        const string imageResponse = """
            {
              "results": [
                {
                  "flagged": true,
                  "categories": { "violence": true, "hate": true },
                  "category_scores": { "violence": 0.70, "hate": 0.10 },
                  "category_applied_input_types": { "violence": ["image"] }
                }
              ]
            }
            """;
        ScriptedHandler handler = new((_, request) =>
            Json(HttpStatusCode.OK, IsTextBody(request.Body) ? textResponse : imageResponse));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        // The transcript and the image are two requests; the union is the same as one combined call.
        ModerationClassification result = await client.ClassifyAsync(
            new ModerationClassifyRequest("text", [Image()], Model), CancellationToken.None);

        result.Flagged.Should().BeTrue("one flagged part flags the request");
        result.Scores["violence"].Should().Be(0.70, "scores keep the maximum, not the last");
        result.Scores["sexual"].Should().Be(0.40);
        result.Scores["hate"].Should().Be(0.10);
        result.FlaggedCategories["violence"].Should().BeTrue();
        result.FlaggedCategories["hate"].Should().BeTrue();
        result.FlaggedCategories["sexual"].Should().BeFalse();
        result.InputTypes.Should().Equal(["text", "image"], "input types keep first-seen order, text first");
    }

    [Fact]
    public async Task Truncates_text_to_the_configured_character_cap()
    {
        ContentModerationOptions options = Options();
        options.MaxTextChars = 5;
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", options, http);

        await client.ClassifyAsync(new ModerationClassifyRequest("0123456789", [], Model), CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.Requests.Single().Body);
        body.RootElement.GetProperty("input").GetString().Should().Be("01234");
    }

    [Fact]
    public async Task Rejects_an_image_over_the_byte_cap_without_calling_the_endpoint()
    {
        ContentModerationOptions options = Options();
        options.MaxImageBytes = 4;
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", options, http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest(null, [Image(new byte[5])], Model), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_non_success_status_is_ContentModerationUnavailableException()
    {
        CapturingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        (await act.Should().ThrowAsync<ContentModerationUnavailableException>())
            .Which.Message.Should().Contain("500");
    }

    [Fact]
    public async Task Invalid_json_is_ContentModerationUnavailableException()
    {
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, "not json at all"));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        await act.Should().ThrowAsync<ContentModerationUnavailableException>();
    }

    [Fact]
    public async Task A_response_without_results_is_ContentModerationUnavailableException()
    {
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, """{"id":"x"}"""));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        await act.Should().ThrowAsync<ContentModerationUnavailableException>();
    }

    [Fact]
    public async Task No_content_is_unavailable_never_an_allow()
    {
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("   ", [], Model), CancellationToken.None);

        await act.Should().ThrowAsync<ContentModerationUnavailableException>();
        handler.Requests.Should().BeEmpty("there is nothing to send");
    }

    [Fact]
    public async Task An_empty_results_array_is_unavailable_never_an_allow()
    {
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, """{"results":[]}"""));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        await act.Should().ThrowAsync<ContentModerationUnavailableException>();
    }

    [Fact]
    public async Task A_fan_out_call_with_no_results_is_unavailable_never_a_partial_allow()
    {
        // One image is classified, the other comes back with no result: the unclassified half must
        // not be hidden behind the union of the rest.
        ScriptedHandler handler = new((_, request) =>
            DataUrlOf(request.Body).EndsWith(Convert.ToBase64String([2]), StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"results":[]}""")
                : Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest(null, [Image([1]), Image([2])], Model), CancellationToken.None);

        await act.Should().ThrowAsync<ContentModerationUnavailableException>();
    }

    [Fact]
    public async Task A_large_error_body_is_bounded_and_redacted_in_the_exception()
    {
        string filler = new('x', 200_000);
        CapturingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent($"Authorization: Bearer sk-" + $"live-secret-1234567890 {filler}")
        });
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string message = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        message.Should().Contain("500");
        message.Length.Should().BeLessThan(400, "the body is never copied into the exception");
        message.Should().NotContain("sk-live-secret", "vendor/attacker payloads are redacted");
    }

    [Fact]
    public async Task The_short_timeout_is_ContentModerationUnavailableException()
    {
        CapturingHandler handler = new(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        (await act.Should().ThrowAsync<ContentModerationUnavailableException>())
            .Which.Message.Should().Contain("timed out");
    }

    [Fact]
    public async Task A_cancelled_caller_is_re_thrown_not_reported_as_unavailable()
    {
        CapturingHandler handler = new(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), cts.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().NotBeOfType<ContentModerationUnavailableException>();
    }

    [Fact]
    public async Task Every_request_carries_exactly_one_input()
    {
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        await client.ClassifyAsync(
            new ModerationClassifyRequest(
                "transcript",
                [Image(new byte[] { 1 }), Image(new byte[] { 2 }), Image(new byte[] { 3 })],
                Model),
            CancellationToken.None);

        handler.Requests.Should().HaveCount(4, "three images and the transcript are four inputs");

        int textCalls = 0;
        int imageCalls = 0;
        foreach (CapturedRequest request in handler.Requests)
        {
            using JsonDocument document = JsonDocument.Parse(request.Body);
            JsonElement input = document.RootElement.GetProperty("input");
            if (input.ValueKind == JsonValueKind.String)
            {
                textCalls++;
                input.GetString().Should().Be("transcript");
            }
            else
            {
                imageCalls++;
                input.ValueKind.Should().Be(JsonValueKind.Array);
                JsonElement part = input.EnumerateArray().Single();
                part.GetProperty("type").GetString().Should().Be("image_url");
                part.TryGetProperty("text", out _).Should().BeFalse("an image call carries no text");
            }
        }

        textCalls.Should().Be(1, "the transcript is one text-only request");
        imageCalls.Should().Be(3, "each image is its own image-only request");
    }

    [Fact]
    public async Task A_multi_frame_video_yields_one_image_per_request_and_is_union_ed()
    {
        byte[][] frames =
        [
            [0xFF, 0xD8, 0xFF, 1],
            [0xFF, 0xD8, 0xFF, 2],
            [0xFF, 0xD8, 0xFF, 3]
        ];
        FfmpegModerationMediaSampler sampler = new(new FrameWritingRunner(frames), Options());

        ModerationMediaSample sample = await sampler.SampleAsync(
            new MemoryStream([1, 2, 3]), "video/mp4", CancellationToken.None);

        sample.Kind.Should().Be(ModerationContentKind.Video);
        // The fake's stream report lists one video stream and no audio, so no WAV is not a failed extraction.
        sample.Failure.Should().BeNull();
        sample.Audio.Should().BeNull("the fake runner writes no WAV");
        sample.Images.Should().HaveCount(3, "the real sampler read the three frames the runner wrote");

        const string first = """
            {"results":[{"flagged":false,"categories":{"violence":false},"category_scores":{"violence":0.2},
             "category_applied_input_types":{"violence":["image"]}}]}
            """;
        const string second = """
            {"results":[{"flagged":true,"categories":{"violence":true},"category_scores":{"violence":0.7},
             "category_applied_input_types":{"violence":["image"]}}]}
            """;
        const string third = """
            {"results":[{"flagged":false,"categories":{"sexual":false},"category_scores":{"sexual":0.6},
             "category_applied_input_types":{"sexual":["image"]}}]}
            """;
        ScriptedHandler handler = new((index, request) =>
        {
            // Reject any body that is not exactly one image input: that is the shape the vendor
            // refuses and the shape this ticket removes.
            DataUrlOf(request.Body);
            return Json(HttpStatusCode.OK, index switch { 0 => first, 1 => second, _ => third });
        });
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        ModerationClassification result = await client.ClassifyAsync(
            new ModerationClassifyRequest(
                null,
                sample.Images.Select(bytes => new ModerationImageInput("image/jpeg", bytes)).ToArray(),
                Model),
            CancellationToken.None);

        handler.Requests.Should().HaveCount(3, "one request per sampled frame, never a mixed request");
        result.Flagged.Should().BeTrue("one flagged frame flags the video");
        result.Scores["violence"].Should().Be(0.7, "scores keep the per-category maximum");
        result.Scores["sexual"].Should().Be(0.6);
        result.FlaggedCategories["violence"].Should().BeTrue();
        result.InputTypes.Should().Equal(["image"]);
    }

    [Fact]
    public async Task A_single_image_and_a_transcript_make_two_requests()
    {
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        await client.ClassifyAsync(
            new ModerationClassifyRequest("transcript", [Image()], Model), CancellationToken.None);

        handler.Requests.Should().HaveCount(2, "one text-only call and one image-only call");
        handler.Requests.Count(request => IsTextBody(request.Body)).Should().Be(1);
        handler.Requests.Count(request => !IsTextBody(request.Body)).Should().Be(1);
    }

    [Fact]
    public async Task A_text_only_request_is_one_string_input()
    {
        CapturingHandler handler = new(_ => Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        await client.ClassifyAsync(
            new ModerationClassifyRequest("hello world", [], Model), CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.Requests.Single().Body);
        JsonElement input = body.RootElement.GetProperty("input");
        input.ValueKind.Should().Be(JsonValueKind.String,
            "plain strings are the base input shape every OpenAI-compatible gateway documents");
        input.GetString().Should().Be("hello world");
    }

    [Fact]
    public async Task A_single_image_rejected_with_400_still_throws()
    {
        ScriptedHandler handler = new((_, _) =>
            Json(HttpStatusCode.BadRequest, """{"error":"bad request"}"""));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest(null, [Image()], Model), CancellationToken.None);

        (await act.Should().ThrowAsync<ContentModerationUnavailableException>())
            .Which.Message.Should().Contain("400");
        handler.Requests.Should().HaveCount(1, "a single image has no smaller shape to retry");
    }

    [Fact]
    public async Task A_failing_image_call_surfaces_the_vendor_failure_and_is_not_retried()
    {
        // Fail-fast (the cancellation of queued calls) is a latency property of
        // Parallel.ForEachAsync and is not deterministically observable from a fake handler: under
        // thread-pool pressure the queue can drain before the failure's continuation runs. What is
        // contractual is pinned here: the real vendor failure surfaces, and the failed input is
        // never re-sent in another shape.
        string failingBytes = Convert.ToBase64String([2]);
        ScriptedHandler handler = new((_, request) =>
            DataUrlOf(request.Body).EndsWith(failingBytes, StringComparison.Ordinal)
                ? Json(HttpStatusCode.BadRequest,
                    """{"error":{"type":"invalid_request_error","code":"invalid_value","param":"input[0]","message":"image rejected"}}""")
                : Json(HttpStatusCode.OK, UnionResponse()));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        ModerationImageInput[] images = Enumerable.Range(1, 6)
            .Select(i => Image(new byte[] { (byte)i }))
            .ToArray();

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest(null, images, Model), CancellationToken.None);

        (await act.Should().ThrowAsync<ContentModerationUnavailableException>())
            .Which.Message.Should().Contain("invalid_value", "the real failure is surfaced");
        handler.Requests
            .Count(request => DataUrlOf(request.Body).EndsWith(failingBytes, StringComparison.Ordinal))
            .Should().Be(1, "the failed input is never retried in another shape");
    }

    [Fact]
    public async Task A_successful_single_image_request_makes_exactly_one_call()
    {
        const string response = """
            {"results":[{"flagged":true,"categories":{"violence":true},
             "category_scores":{"violence":0.8},"category_applied_input_types":{"violence":["image"]}}]}
            """;
        ScriptedHandler handler = new((_, request) =>
        {
            DataUrlOf(request.Body);
            return Json(HttpStatusCode.OK, response);
        });
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        ModerationClassification result = await client.ClassifyAsync(
            new ModerationClassifyRequest(null, [Image(new byte[] { 1 })], Model), CancellationToken.None);

        handler.Requests.Should().HaveCount(1, "one image is one request, not a batch");
        result.Flagged.Should().BeTrue();
        result.Scores["violence"].Should().Be(0.8);
    }

    [Fact]
    public async Task Image_fan_out_keeps_at_most_four_calls_in_flight()
    {
        ScriptedHandler handler = new(async (_, request) =>
        {
            DataUrlOf(request.Body);
            await Task.Delay(25);
            return Json(HttpStatusCode.OK, UnionResponse());
        });
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        const int imageCount = 9;
        ModerationImageInput[] images = Enumerable.Range(0, imageCount)
            .Select(i => Image(new byte[] { (byte)(i + 1) }))
            .ToArray();

        await client.ClassifyAsync(
            new ModerationClassifyRequest(null, images, Model), CancellationToken.None);

        handler.Requests.Should().HaveCount(imageCount, "one request per image");
        handler.MaxInFlight.Should().BeLessThanOrEqualTo(4, "the fan-out bounds calls in flight");
        handler.MaxInFlight.Should().BeGreaterThan(1, "images are classified in parallel, not one by one");
    }

    [Fact]
    public async Task A_400_error_body_surfaces_the_structured_cause_and_omits_the_message()
    {
        const string vendorBody = """
            {
              "error": {
                "message": "The image input could not be processed.",
                "type": "invalid_request_error",
                "param": "input[1]",
                "code": "invalid_value"
              }
            }
            """;
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, vendorBody));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest(null, [Image()], Model), CancellationToken.None);

        string message = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        message.Should().Contain("invalid_request_error");
        message.Should().Contain("invalid_value");
        message.Should().Contain("input[1]", "the param names the offending element");
        message.Should().Contain("message=omitted (");
        message.Should().NotContain("The image input could not be processed.",
            "vendor free text is never echoed");
    }

    [Fact]
    public async Task A_non_json_error_body_is_summarised_without_echoing_it()
    {
        string body = "upstream failure\n  at   gateway\ndata:image/jpeg;base64,AAAA" + new string('y', 1000);
        CapturingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(body)
        });
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string message = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        message.Should().Contain("502", "the status is still surfaced");
        message.Should().Contain("(non-JSON error body)", "a non-JSON body is reported by kind, not echoed");
        message.Should().NotContain("upstream failure", "arbitrary body text never reaches the log");
        message.Should().NotContain("data:image", "a data-URL fragment never reaches the log");
        message.Length.Should().BeLessThan(400, "the whole snippet is bounded");
    }

    [Fact]
    public async Task A_request_echoing_gateway_body_never_leaks_the_request()
    {
        // A compatible gateway that echoes what it was sent, plus a data URL: the only thing that
        // may surface is the marker. No free text, no transcript, no data URL.
        const string body = """
            {
              "input": "the transcript of the video must never reach the log",
              "message": "data:image/jpeg;base64,AAAA+/==",
              "detail": "invalid moderation request"
            }
            """;
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string message = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        message.Should().Contain("message omitted (", "the reflected field is replaced by its length");
        message.Should().NotContain("transcript");
        message.Should().NotContain("data:image");
        message.Should().NotContain("invalid moderation request", "there is no sibling fallback");
    }

    [Fact]
    public async Task An_unknown_json_error_body_is_not_echoed()
    {
        const string body = """{"input":"the transcript must never reach the log","unexpected":true}""";
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string message = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        message.Should().Contain("(unrecognised error body)");
        message.Should().NotContain("transcript");
    }

    [Fact]
    public async Task A_secret_in_a_message_is_omitted_entirely()
    {
        string message = new string('a', 150) + " sk-" + new string('b', 40);
        string body = JsonSerializer.Serialize(new { error = new { type = "invalid_request_error", message } });
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().Contain("message=omitted (");
        summary.Should().NotContain("bbbb", "free text is never echoed, so no fragment can leak");
        summary.Should().NotContain("sk-");
        summary.Length.Should().BeLessThan(400);
    }

    [Fact]
    public async Task A_structured_field_is_redacted_before_it_is_echoed()
    {
        string body = JsonSerializer.Serialize(new
        {
            error = new
            {
                type = "invalid_request_error",
                code = "invalid_value",
                param = "https://alice:hunter2@example.test/v1/moderations"
            }
        });
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().Contain("param=");
        summary.Should().NotContain("hunter2", "structured fields are redacted before they are echoed");
    }

    [Fact]
    public async Task A_message_over_the_cap_is_omitted_not_truncated()
    {
        string message = new string('a', 250);
        string body = JsonSerializer.Serialize(new { message });
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().Contain("message omitted (250 chars)", "long free text is replaced, never cut");
    }

    [Theory]
    [InlineData("message")]
    [InlineData("detail")]
    [InlineData("error_description")]
    public async Task A_reflected_request_in_a_top_level_field_is_omitted(string field)
    {
        string transcript = "Hello? This is the transcript.\n\"quoted\" text";
        string body = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = transcript });
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().Contain("message omitted (", "a reflected request is described, not echoed");
        summary.Should().NotContain("transcript");
        summary.Should().NotContain("Hello");
    }

    [Fact]
    public async Task A_plain_ascii_sentence_is_not_echoed()
    {
        // The shape a character allowlist would have admitted: plain ASCII prose with no quotes,
        // braces, slashes or base64 punctuation. It must still be replaced by the marker.
        string transcript = "hello this is the transcript and it has only allowed characters";
        string body = JsonSerializer.Serialize(new { message = transcript });
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().Contain("message omitted (");
        summary.Should().NotContain("hello this is the transcript");
    }

    [Fact]
    public async Task The_first_message_bearing_field_governs_and_no_sibling_is_used()
    {
        // Stop at the first message-bearing field: an omitted `message` must not fall through to a
        // sibling, which could be the reflected payload.
        string body = JsonSerializer.Serialize(new
        {
            message = "first reflected field",
            detail = "second reflected field"
        });
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().Contain("message omitted (");
        summary.Should().NotContain("first reflected field");
        summary.Should().NotContain("second reflected field", "there is no sibling fallback");
    }

    [Fact]
    public async Task A_reflected_data_url_in_an_error_message_is_omitted()
    {
        string body = JsonSerializer.Serialize(new
        {
            error = new
            {
                type = "invalid_request_error",
                message = "data:image/jpeg;base64,AAAA+/=="
            }
        });
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().Contain("invalid_request_error", "the structured fields always name the cause");
        summary.Should().Contain("message=omitted (");
        summary.Should().NotContain("data:image");
        summary.Should().NotContain("base64");
    }

    [Fact]
    public async Task A_reflected_error_string_is_omitted()
    {
        string body = JsonSerializer.Serialize(new { error = """{"input":"secret transcript"}""" });
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().Contain("message omitted (");
        summary.Should().NotContain("secret transcript");
    }

    [Fact]
    public async Task A_bare_json_string_error_body_is_not_echoed()
    {
        string body = JsonSerializer.Serialize("data:image/jpeg;base64,AAAA");
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().Contain("(unrecognised error body)", "a bare JSON string is not an error object");
        summary.Should().NotContain("data:image");
    }

    [Fact]
    public async Task A_benign_vendor_message_is_also_omitted()
    {
        // The cost of the absolute rule, pinned: even a harmless vendor sentence is replaced by the
        // marker. The structured code names the cause instead.
        const string text = "Invalid image format. Supported formats are PNG, JPEG.";
        string body = JsonSerializer.Serialize(new
        {
            error = new { type = "invalid_request_error", code = "invalid_value", message = text }
        });
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().Contain("invalid_value");
        summary.Should().Contain("message=omitted (");
        summary.Should().NotContain("Invalid image format");
    }

    [Fact]
    public async Task Control_characters_are_stripped_from_structured_fields()
    {
        string body = JsonSerializer.Serialize(new
        {
            error = new
            {
                type = "invalid_request_error\u001b[31m",
                code = "invalid_value\nFORGED LINE"
            }
        });
        CapturingHandler handler = new(_ => Json(HttpStatusCode.BadRequest, body));
        using HttpClient http = new(handler);
        OpenAICompatibleContentModerationClient client = new(Endpoint, "", Options(), http);

        Func<Task> act = () => client.ClassifyAsync(
            new ModerationClassifyRequest("text", [], Model), CancellationToken.None);

        string summary = (await act.Should().ThrowAsync<ContentModerationUnavailableException>()).Which.Message;
        summary.Should().NotContain("\u001b", "an ANSI escape must not colour the log line");
        summary.Should().NotContain("\n", "a newline must not forge a log line");
        summary.Should().Contain("invalid_request_error");
        summary.Should().Contain("invalid_value");
    }

    private static bool IsTextBody(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("input").ValueKind == JsonValueKind.String;
    }

    /// <summary>
    /// Reads the single data URL an image request must carry. Throws if any body is not exactly one
    /// <c>image_url</c> input — the shape every request in the new contract has.
    /// </summary>
    private static string DataUrlOf(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement input = document.RootElement.GetProperty("input");
        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() != 1)
            throw new InvalidOperationException("Every request must carry exactly one input element.");

        JsonElement part = input.EnumerateArray().Single();
        if (part.GetProperty("type").GetString() != "image_url")
            throw new InvalidOperationException("An image call must carry only the image.");
        if (part.TryGetProperty("text", out _))
            throw new InvalidOperationException("An image call must not carry text.");

        return part.GetProperty("image_url").GetProperty("url").GetString()!;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string UnionResponse() => """
        {"results":[{"flagged":false,"categories":{"violence":false},"category_scores":{"violence":0.1},
        "category_applied_input_types":{"violence":["text"]}}]}
        """;

    private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? Authorization, string Body);

    /// <summary>A fake handler: records every request and delegates the response, never a live call.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;

        public CapturingHandler(Func<CancellationToken, HttpResponseMessage> respond)
            : this(ct => Task.FromResult(respond(ct)))
        {
        }

        public CapturingHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        private readonly List<CapturedRequest> _requests = [];
        private readonly object _sync = new();

        /// <summary>
        /// A snapshot: the fan-out calls the handler concurrently, so recording must be locked and
        /// readers must not iterate a list another thread is appending to.
        /// </summary>
        public IReadOnlyList<CapturedRequest> Requests
        {
            get
            {
                lock (_sync)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            CapturedRequest captured = new(
                request.Method,
                request.RequestUri,
                request.Headers.TryGetValues("Authorization", out IEnumerable<string>? values)
                    ? values.Single()
                    : null,
                body);

            lock (_sync)
            {
                _requests.Add(captured);
            }

            return await _respond(cancellationToken);
        }
    }

    /// <summary>
    /// A fake handler whose answer depends on the call index (first call, second call, …) and which
    /// records how many calls were in flight at once, so the fallback's ordering and concurrency
    /// can be asserted without a live endpoint.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, CapturedRequest, Task<HttpResponseMessage>> _respond;
        private readonly object _sync = new();
        private int _inFlight;

        public ScriptedHandler(Func<int, CapturedRequest, HttpResponseMessage> respond)
            : this((index, request) => Task.FromResult(respond(index, request)))
        {
        }

        public ScriptedHandler(Func<int, CapturedRequest, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public List<CapturedRequest> Requests { get; } = [];

        public int MaxInFlight { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            CapturedRequest captured = new(
                request.Method,
                request.RequestUri,
                request.Headers.TryGetValues("Authorization", out IEnumerable<string>? values)
                    ? values.Single()
                    : null,
                body);

            int index;
            lock (_sync)
            {
                Requests.Add(captured);
                index = Requests.Count - 1;
                _inFlight++;
                if (_inFlight > MaxInFlight) MaxInFlight = _inFlight;
            }

            try
            {
                return await _respond(index, captured);
            }
            finally
            {
                lock (_sync)
                {
                    _inFlight--;
                }
            }
        }
    }

    /// <summary>
    /// A fake <see cref="IProcessRunner"/> for the real sampler: writes the requested frames to the
    /// <c>frame_%03d.jpg</c> path ffmpeg would fill and writes no WAV, so the sample has images only.
    /// </summary>
    private sealed class FrameWritingRunner : IProcessRunner
    {
        private readonly byte[][] _frames;

        public FrameWritingRunner(byte[][] frames) => _frames = frames;

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Calls.Add([.. arguments]);

            if (fileName == "ffprobe" && arguments.Any(x => x.StartsWith("packet=", StringComparison.Ordinal)))
            {
                // The packet list: six packets a second apart, so a 6 s video is three frames at one per 2 s.
                return Task.FromResult(new ProcessRunResult(0,
                    string.Join("\n", Enumerable.Range(0, 6).Select(i => $"{i}.000000,{i}.000000,1.000000")),
                    string.Empty, false));
            }

            if (fileName == "ffprobe")
            {
                // The stream report: one real video stream and nothing else, in a recognised container.
                return Task.FromResult(new ProcessRunResult(0,
                    "{\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"h264\",\"disposition\":{\"attached_pic\":0}}],\"format\":{\"format_name\":\"mov,mp4,m4a,3gp,3g2,mj2\",\"duration\":\"10\"}}",
                    string.Empty, false));
            }

            string output = arguments[^1];
            if (output.Contains("%03d", StringComparison.Ordinal))
            {
                for (int i = 0; i < _frames.Length; i++)
                    File.WriteAllBytes(output.Replace("%03d", (i + 1).ToString("000")), _frames[i]);
            }
            else if (Path.GetFileName(output) is { } name && name.StartsWith("frame_", StringComparison.Ordinal)
                     && int.TryParse(name.AsSpan(6, 3), out int index) && index - 1 < _frames.Length)
            {
                // One keyframe per call, written under the name the sampler asked for.
                File.WriteAllBytes(output, _frames[index - 1]);
            }

            // A frame pass reports the time of the frame it wrote, as the metadata filter does: the one asked for.
            int seekAt = arguments.ToList().IndexOf("-ss");
            string frameTime = seekAt >= 0 && arguments.Contains("-copyts")
                ? $"frame:0 pts:0 pts_time:{(double.Parse(arguments[seekAt + 1], System.Globalization.CultureInfo.InvariantCulture) + 0.0002).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}\nrb=1\n"
                : string.Empty;
            return Task.FromResult(new ProcessRunResult(0, frameTime, string.Empty, false));
        }
    }
}
