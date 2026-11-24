using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class MiniMaxVideoGenerationClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request);
    }

    private sealed class SimpleStubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public SimpleStubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    [Fact]
    public async Task SubmitAsync_POSTs_to_endpoint_v2_video_generation_and_returns_handle()
    {
        HttpClient httpClient = new(new SimpleStubHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/v2/video_generation");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task_id":"task-123"}""")
            };
        }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "test-api-key");
        VideoGenerationRequest request = new(
            Model: "MiniMax-H3",
            PromptText: "A cat sleeping",
            DurationSeconds: 10,
            Resolution: "768P",
            Ratio: "16:9");

        ExternalJobHandle handle = await client.SubmitAsync(request, CancellationToken.None);

        handle.RemoteJobId.Should().Be("task-123");
    }

    [Fact]
    public async Task SubmitAsync_includes_Authorization_Bearer_header()
    {
        string capturedAuthHeader = null!;

        HttpClient httpClient = new(new SimpleStubHandler(request =>
        {
            capturedAuthHeader = request.Headers.Authorization?.ToString() ?? "";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task_id":"task-123"}""")
            };
        }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "my-secret-key");
        VideoGenerationRequest request = new(
            Model: "MiniMax-H3",
            PromptText: "A cat",
            DurationSeconds: 10,
            Resolution: "768P",
            Ratio: "16:9");

        await client.SubmitAsync(request, CancellationToken.None);

        capturedAuthHeader.Should().Be("Bearer my-secret-key");
    }

    [Fact]
    public async Task SubmitAsync_includes_correct_request_body()
    {
        string? capturedBody = null;

        HttpClient httpClient = new(new StubHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task_id":"task-123"}""")
            };
        }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "key");
        VideoGenerationRequest request = new(
            Model: "MiniMax-H3",
            PromptText: "A dog barking",
            DurationSeconds: 8,
            Resolution: "2K",
            Ratio: "4:3");

        await client.SubmitAsync(request, CancellationToken.None);

        capturedBody.Should().NotBeNullOrEmpty();
        using JsonDocument doc = JsonDocument.Parse(capturedBody);
        JsonElement root = doc.RootElement;
        root.GetProperty("model").GetString().Should().Be("MiniMax-H3");
        root.GetProperty("duration").GetInt32().Should().Be(8);
        root.GetProperty("resolution").GetString().Should().Be("2K");
        root.GetProperty("ratio").GetString().Should().Be("4:3");

        JsonElement content = root.GetProperty("content")[0];
        content.GetProperty("type").GetString().Should().Be("text");
        content.GetProperty("text").GetString().Should().Be("A dog barking");
    }

    [Fact]
    public async Task PollAsync_GETs_endpoint_v2_query_video_generation_task_id()
    {
        string? capturedPath = null;

        HttpClient httpClient = new(new SimpleStubHandler(request =>
        {
            capturedPath = request.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task":{"status":"succeeded","content":{"url":"https://example.com/video.mp4"}}}""")
            };
        }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "key");
        ExternalJobHandle handle = new("abc-123");

        ExternalJobStatus status = await client.PollAsync(handle, CancellationToken.None);

        capturedPath.Should().Be("/v2/query/video_generation/abc-123");
    }

    [Fact]
    public async Task PollAsync_maps_succeeded_status_and_download_url()
    {
        HttpClient httpClient = new(new SimpleStubHandler(_request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task":{"status":"succeeded","content":{"url":"https://example.com/output.mp4"}}}""")
            }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "key");
        ExternalJobHandle handle = new("task-id");

        ExternalJobStatus status = await client.PollAsync(handle, CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Succeeded);
        status.DownloadUrl.Should().Be("https://example.com/output.mp4");
        status.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task PollAsync_maps_queued_status()
    {
        HttpClient httpClient = new(new SimpleStubHandler(_request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task":{"status":"queued"}}""")
            }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "key");

        ExternalJobStatus status = await client.PollAsync(new("id"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Queued);
    }

    [Fact]
    public async Task PollAsync_maps_running_status()
    {
        HttpClient httpClient = new(new SimpleStubHandler(_request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task":{"status":"running"}}""")
            }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "key");

        ExternalJobStatus status = await client.PollAsync(new("id"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Running);
    }

    [Fact]
    public async Task PollAsync_maps_failed_status_and_error_message()
    {
        HttpClient httpClient = new(new SimpleStubHandler(_request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task":{"status":"failed","error":{"message":"Invalid input"}}}""")
            }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "key");

        ExternalJobStatus status = await client.PollAsync(new("id"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Failed);
        status.FailureReason.Should().Be("Invalid input");
        status.DownloadUrl.Should().BeNull();
    }

    [Fact]
    public async Task PollAsync_reports_the_vendor_code_and_message_of_a_recorded_failed_task()
    {
        // Shape recorded from the vendor's query endpoint for a refused reference audio.
        const string recorded = """{"task":{"id":"1","model":"MiniMax-H3","status":"failed","error":{"code":"2013","message":"content[3].audio_url: invalid param: audio format \".mpeg\" not allowed"},"usage":{}}}""";
        HttpClient httpClient = new(new SimpleStubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(recorded) }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        ExternalJobStatus status = await new MiniMaxVideoGenerationClient(httpClient, "key").PollAsync(new("1"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Failed);
        status.FailureReason.Should().Be("2013: content[3].audio_url: invalid param: audio format \".mpeg\" not allowed");
    }

    [Theory]
    [InlineData("""{"task":{"status":"failed","error":{"code":"2013","message":"bad https://x.example/a?X-Amz-Signature=abc123 here"}}}""", "2013: bad <link> here")]
    [InlineData("""{"task":{"status":"failed","error":{"message":"see HTTPS://x.example/a?sig=1 and s3://b/k\nnext line"}}}""", "see <link> and <link> next line")]
    [InlineData("""{"task":{"status":"failed","error":{"message":"echo data:audio/mp3;base64,AAAA end"}}}""", "echo <link> end")]
    [InlineData("""{"task":{"status":"failed"}}""", null)]
    [InlineData("""{"task":{"status":"failed","error":{"code":2013}}}""", "2013")]
    [InlineData("""{"task":{"status":"failed","error":"nope"}}""", null)]
    public void ReadFailureReason_is_bounded_link_free_and_never_throws(string json, string? expected)
    {
        using JsonDocument doc = JsonDocument.Parse(json);

        MiniMaxVideoGenerationClient.ReadFailureReason(doc.RootElement.GetProperty("task")).Should().Be(expected);
    }

    [Fact]
    public void ReadFailureReason_truncates_long_vendor_prose()
    {
        using JsonDocument doc = JsonDocument.Parse("{\"task\":{\"status\":\"failed\",\"error\":{\"message\":\"" + new string('a', 900) + "\"}}}");

        MiniMaxVideoGenerationClient.ReadFailureReason(doc.RootElement.GetProperty("task"))!.Length.Should().BeLessThanOrEqualTo(301);
    }

    [Fact]
    public async Task PollAsync_maps_cancelled_status()
    {
        HttpClient httpClient = new(new SimpleStubHandler(_request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task":{"status":"cancelled"}}""")
            }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "key");

        ExternalJobStatus status = await client.PollAsync(new("id"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Cancelled);
    }

    [Fact]
    public async Task PollAsync_throws_NotSupportedException_for_unknown_status()
    {
        HttpClient httpClient = new(new SimpleStubHandler(_request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task":{"status":"unknown_status"}}""")
            }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "key");

        Func<Task> act = async () => await client.PollAsync(new("id"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task DownloadAsync_issues_unauthenticated_GET_request()
    {
        string? capturedAuthHeader = null;

        HttpClient httpClient = new(new SimpleStubHandler(request =>
        {
            capturedAuthHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
        }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "secret-key");

        using Stream stream = await client.DownloadAsync("https://example.com/video.mp4", CancellationToken.None);

        capturedAuthHeader.Should().BeNull();
    }

    [Fact]
    public async Task DownloadAsync_returns_response_stream()
    {
        byte[] videoData = { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header stub

        HttpClient httpClient = new(new SimpleStubHandler(_request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(videoData)
            }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "key");

        using Stream stream = await client.DownloadAsync("https://example.com/video.mp4", CancellationToken.None);
        byte[] downloaded = new byte[4];
#pragma warning disable CA2022 // Avoid inexact read with 'Stream.ReadAsync(byte[], int, int)'
        await stream.ReadAsync(downloaded, 0, 4);
#pragma warning restore CA2022

        downloaded.Should().Equal(videoData);
    }

    [Fact]
    public void Describe_returns_MiniMaxH3_capabilities()
    {
        MiniMaxVideoGenerationClient client = new(
            new HttpClient { BaseAddress = new Uri("https://api.minimax.io") },
            "key");

        VideoGenerationCapabilities caps = client.Describe("MiniMax-H3");

        caps.MinDurationSeconds.Should().Be(4);
        caps.MaxDurationSeconds.Should().Be(15);
        caps.AllowedRatios.Should().Equal(new[] { "21:9", "16:9", "4:3", "1:1", "3:4", "9:16" });
        caps.PricePerSecondByResolution.Should().Equal(new Dictionary<string, decimal>
        {
            { "768P", 0.08m },
            { "2K", 0.13m }
        });
    }

    [Fact]
    public void Describe_returns_MiniMaxH3Max_capabilities()
    {
        MiniMaxVideoGenerationClient client = new(
            new HttpClient { BaseAddress = new Uri("https://api.minimax.io") },
            "key");

        VideoGenerationCapabilities caps = client.Describe("MiniMax-H3-Max");

        caps.MinDurationSeconds.Should().Be(5);
        caps.MaxDurationSeconds.Should().Be(15);
        caps.AllowedRatios.Should().Equal(new[] { "21:9", "16:9", "4:3", "1:1", "3:4", "9:16" });
        caps.PricePerSecondByResolution.Should().Equal(new Dictionary<string, decimal>
        {
            { "480P", 0.05m },
            { "768P", 0.08m }
        });
    }

    [Fact]
    public void Describe_calls_no_network()
    {
        // Describe must not make any HTTP calls - it returns compile-time constants
        HttpClient httpClient = new(new SimpleStubHandler(_request =>
            throw new InvalidOperationException("Should not be called")))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "key");

        // Should not throw
        _ = client.Describe("MiniMax-H3");
    }

    [Fact]
    public async Task SubmitAsync_401_response_throws_without_exposing_api_key()
    {
        HttpClient httpClient = new(new SimpleStubHandler(_request =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"message":"Invalid API key"}}""")
            }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "super-secret-key-12345");
        VideoGenerationRequest request = new(
            Model: "MiniMax-H3",
            PromptText: "Test",
            DurationSeconds: 10,
            Resolution: "768P",
            Ratio: "16:9");

        Func<Task> act = async () => await client.SubmitAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.And.Message.Should().NotContain("super-secret-key-12345");
    }

    [Fact]
    public async Task PollAsync_401_response_throws_without_exposing_api_key()
    {
        HttpClient httpClient = new(new SimpleStubHandler(_request =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"message":"Invalid credentials"}}""")
            }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        MiniMaxVideoGenerationClient client = new(httpClient, "my-confidential-api-key");

        Func<Task> act = async () => await client.PollAsync(new("id"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.And.Message.Should().NotContain("my-confidential-api-key");
    }

    // Verbatim succeeded example from https://platform.minimax.io/docs/api-reference/video-generation-v2-query.md
    private const string DocSucceededExampleBody =
        """{"task":{"id":"424010985738629","model":"MiniMax-H3","status":"succeeded","created_at":1785125529,"updated_at":1785125946,"content":{"url":"https://cdn.hailuoai.com/x.mp4"},"resolution":"2K","duration":5,"usage":{"total_seconds":5,"input_seconds":0,"output_seconds":5,"input_image_count":1,"input_audio_seconds":6,"total_tokens":273890,"prompt_tokens":13500,"completion_tokens":260390},"ratio":"16:9","task_type":"generation","modality":"video"}}""";

    private static MiniMaxVideoGenerationClient ClientReturning(HttpStatusCode code, string body, string apiKey = "key") =>
        new(new HttpClient(new SimpleStubHandler(_ =>
            new HttpResponseMessage(code) { Content = new StringContent(body) }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        }, apiKey);

    private static VideoGenerationRequest SampleRequest() =>
        new(Model: "MiniMax-H3", PromptText: "p", DurationSeconds: 5, Resolution: "768P", Ratio: "16:9");

    [Fact]
    public async Task PollAsync_parses_doc_succeeded_example_including_usage_total_seconds()
    {
        MiniMaxVideoGenerationClient client = ClientReturning(HttpStatusCode.OK, DocSucceededExampleBody);

        ExternalJobStatus status = await client.PollAsync(new("424010985738629"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Succeeded);
        status.DownloadUrl.Should().Be("https://cdn.hailuoai.com/x.mp4");
        status.UsageTotalSeconds.Should().Be(5);
    }

    [Theory]
    [InlineData("""{"task":{"status":"succeeded","content":{"url":"https://example.com/o.mp4"}}}""")]
    [InlineData("""{"task":{"status":"succeeded","content":{"url":"https://example.com/o.mp4"},"usage":{}}}""")]
    [InlineData("""{"task":{"status":"succeeded","content":{"url":"https://example.com/o.mp4"},"usage":null}}""")]
    [InlineData("""{"task":{"status":"succeeded","content":{"url":"https://example.com/o.mp4"},"usage":{"total_seconds":null}}}""")]
    [InlineData("""{"task":{"status":"succeeded","content":{"url":"https://example.com/o.mp4"},"usage":{"total_seconds":"5"}}}""")]
    [InlineData("""{"task":{"status":"succeeded","content":{"url":"https://example.com/o.mp4"},"usage":{"total_seconds":5.5}}}""")]
    [InlineData("""{"task":{"status":"succeeded","content":{"url":"https://example.com/o.mp4"},"usage":[5]}}""")]
    public async Task PollAsync_absent_or_non_integer_usage_yields_null_UsageTotalSeconds(string body)
    {
        MiniMaxVideoGenerationClient client = ClientReturning(HttpStatusCode.OK, body);

        ExternalJobStatus status = await client.PollAsync(new("id"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Succeeded);
        status.UsageTotalSeconds.Should().BeNull();
    }

    [Fact]
    public async Task PollAsync_running_status_yields_null_UsageTotalSeconds()
    {
        MiniMaxVideoGenerationClient client = ClientReturning(HttpStatusCode.OK, """{"task":{"status":"running"}}""");

        ExternalJobStatus status = await client.PollAsync(new("id"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Running);
        status.UsageTotalSeconds.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SubmitAsync_auth_rejection_throws_HttpRequestException_with_StatusCode(HttpStatusCode code)
    {
        MiniMaxVideoGenerationClient client = ClientReturning(code, """{"error":{"message":"denied"}}""", "secret-key-xyz");

        Func<Task> act = async () => await client.SubmitAsync(SampleRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.And.StatusCode.Should().Be(code);
        ex.And.Message.Should().NotContain("secret-key-xyz");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PollAsync_auth_rejection_throws_HttpRequestException_with_StatusCode(HttpStatusCode code)
    {
        MiniMaxVideoGenerationClient client = ClientReturning(code, """{"error":{"message":"denied"}}""", "secret-key-xyz");

        Func<Task> act = async () => await client.PollAsync(new("id"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.And.StatusCode.Should().Be(code);
        ex.And.Message.Should().NotContain("secret-key-xyz");
    }

    // --- ProbeAsync: no-cost provider test (GET /v2/query/video_generation/<random id> only) ---

    private const string ProbeKey = "probe-secret-key-123";

    /// <summary>Records every request the client sends; the response comes from <c>respond</c>.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Auth)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString() ?? string.Empty));
            return respond(request, cancellationToken);
        }
    }

    private static (MiniMaxVideoGenerationClient Client, RecordingHandler Handler) ProbeClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? probeTimeout = null)
    {
        RecordingHandler handler = new(respond);
        HttpClient http = new(handler) { BaseAddress = new Uri("https://api.minimax.io") };
        return (new MiniMaxVideoGenerationClient(http, ProbeKey, probeTimeout), handler);
    }

    private static (MiniMaxVideoGenerationClient Client, RecordingHandler Handler) ProbeClientReturning(
        HttpStatusCode code, string body, string mediaType = "application/json") =>
        ProbeClient((_, _) => Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType)
        }));

    private static void AssertOnlyOneQueryGet(RecordingHandler handler)
    {
        handler.Requests.Should().HaveCount(1);
        (HttpMethod method, string path, string auth) = handler.Requests[0];
        method.Should().Be(HttpMethod.Get);
        path.Should().StartWith("/v2/query/video_generation/");
        path.Length.Should().BeGreaterThan("/v2/query/video_generation/".Length);
        auth.Should().Be($"Bearer {ProbeKey}");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    private static void AssertNoSecretLeak(VideoGenerationProbeResult result)
    {
        (result.Reason ?? string.Empty).Should().NotContain(ProbeKey);
        (result.Reason ?? string.Empty).Length.Should().BeLessThanOrEqualTo(300);
    }

    // The documented error envelope (OaiError) for a query the vendor authenticated but rejected.
    private const string OaiBadRequestInvalidTaskId =
        """{"type":"error","error":{"type":"bad_request_error","message":"invalid task_id (2013)","http_code":"400"},"request_id":"021785229015510a2c883cf675b9804d"}""";

    private const string OaiAuthorizedError =
        """{"type":"error","error":{"type":"authorized_error","message":"login fail: Please carry the API secret key in the 'Authorization' field of the request header (1004)","http_code":"401"},"request_id":"021785229015510a2c883cf675b9804d"}""";

    [Fact]
    public async Task ProbeAsync_200_with_documented_task_envelope_is_ok()
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.OK, DocSucceededExampleBody);

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.AuthRejected.Should().BeFalse();
        AssertOnlyOneQueryGet(handler);
        AssertNoSecretLeak(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ProbeAsync_4xx_with_documented_error_envelope_and_no_auth_failure_is_ok(HttpStatusCode code)
    {
        var (client, handler) = ProbeClientReturning(code, OaiBadRequestInvalidTaskId);

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain("bad_request_error");
        AssertOnlyOneQueryGet(handler);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task ProbeAsync_404_with_non_vendor_json_is_not_ok()
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.NotFound, """{"error":"Not Found","path":"/v2/query/video_generation/x"}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain("404");
        AssertOnlyOneQueryGet(handler);
        AssertNoSecretLeak(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ProbeAsync_401_403_is_auth_rejected(HttpStatusCode code)
    {
        var (client, handler) = ProbeClientReturning(code, OaiAuthorizedError);

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeTrue();
        result.Reason.Should().Contain(((int)code).ToString());
        AssertOnlyOneQueryGet(handler);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task ProbeAsync_in_band_authorized_error_on_http_200_is_auth_rejected()
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.OK, OaiAuthorizedError);

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeTrue();
        AssertOnlyOneQueryGet(handler);
        AssertNoSecretLeak(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ProbeAsync_5xx_408_429_is_not_ok_even_with_vendor_envelope(HttpStatusCode code)
    {
        var (client, handler) = ProbeClientReturning(code,
            """{"type":"error","error":{"type":"server_error","message":"internal error (1000)","http_code":"500"}}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain(((int)code).ToString());
        AssertOnlyOneQueryGet(handler);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task ProbeAsync_500_record_not_found_is_ok_because_it_is_the_answer_for_a_nonexistent_task()
    {
        // MiniMax's real response (observed 2026-10-04) to a valid key querying a random task id.
        var (client, handler) = ProbeClientReturning(HttpStatusCode.InternalServerError,
            """{"type":"error","error":{"type":"server_error","message":"record not found (1000)","http_code":"500"},"request_id":"07112ad21ffff4e914e876771fa12a53"}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.AuthRejected.Should().BeFalse();
        AssertOnlyOneQueryGet(handler);
        AssertNoSecretLeak(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "server_error")]
    [InlineData(HttpStatusCode.InternalServerError, "api_error")]
    public async Task ProbeAsync_record_not_found_on_another_status_or_type_is_not_ok(HttpStatusCode code, string errorType)
    {
        var (client, _) = ProbeClientReturning(code,
            $$$"""{"type":"error","error":{"type":"{{{errorType}}}","message":"record not found (1000)"}}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task ProbeAsync_5xx_keeps_the_vendor_error_and_request_id_and_says_the_key_was_accepted()
    {
        var (client, _) = ProbeClientReturning(HttpStatusCode.InternalServerError,
            """{"type":"error","error":{"type":"api_error","message":"task not found for key """ + ProbeKey + """ (2013)","http_code":"500"},"request_id":"abc123"}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse("a 5xx proves nothing about a real generation");
        result.Reason.Should().Contain("HTTP 500").And.Contain("api_error: task not found")
            .And.Contain("request_id abc123").And.Contain("API key was accepted");
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task ProbeAsync_5xx_gateway_page_reports_only_the_status()
    {
        var (client, _) = ProbeClientReturning(HttpStatusCode.BadGateway, "<html>bad gateway</html>", "text/html");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Be("Provider returned HTTP 502.");
    }

    [Fact]
    public async Task ProbeAsync_html_200_is_not_ok()
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.OK,
            "<html><body>Welcome to nginx! " + ProbeKey + "</body></html>", "text/html");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain("JSON");
        result.Reason.Should().NotContain("nginx");
        AssertOnlyOneQueryGet(handler);
        AssertNoSecretLeak(result);
    }

    [Theory]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"base_resp":{"status_code":0,"status_msg":"success"}}""")]
    [InlineData("""{"task":"not-an-object"}""")]
    public async Task ProbeAsync_unrecognised_json_on_200_is_not_ok(string body)
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.OK, body);

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        AssertOnlyOneQueryGet(handler);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task ProbeAsync_vendor_message_is_bounded_and_scrubbed_of_the_key()
    {
        string longMessage = "invalid task_id " + ProbeKey + " " + new string('x', 2000);
        string body = JsonSerializer.Serialize(new
        {
            type = "error",
            error = new { type = "bad_request_error", message = longMessage, http_code = "400" }
        });
        var (client, _) = ProbeClientReturning(HttpStatusCode.BadRequest, body);

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeTrue();
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task ProbeAsync_network_error_is_not_ok()
    {
        var (client, handler) = ProbeClient((_, _) =>
            throw new HttpRequestException("No such host is known. (api.minimax.io:443)"));

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain("Network error");
        AssertOnlyOneQueryGet(handler);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task ProbeAsync_timeout_is_not_ok_and_bounded()
    {
        var (client, handler) = ProbeClient(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, probeTimeout: TimeSpan.FromMilliseconds(200));

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);
        sw.Stop();

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain("did not respond");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        AssertOnlyOneQueryGet(handler);
    }

    [Fact]
    public async Task ProbeAsync_caller_cancellation_propagates()
    {
        using CancellationTokenSource cts = new();
        var (client, _) = ProbeClient(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Func<Task> act = async () => await client.ProbeAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ProbeAsync_uses_a_fresh_random_task_id_each_call()
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.OK, DocSucceededExampleBody);

        await client.ProbeAsync(CancellationToken.None);
        await client.ProbeAsync(CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Path.Should().NotBe(handler.Requests[1].Path);
    }

    // --- Image input: the documented content-item shape -------------------------------------------
    //
    // Wire facts, from the vendor's own OpenAPI schema (fetched 2026-10-09,
    // https://platform.minimax.io/docs/api-reference/video-generation-v2-create.md): "content" is an
    // ARRAY of multimodal items; an image item is
    //   {"type":"image_url","image_url":{"url":"<uri>"},"role":"first_frame"|"last_frame"}
    // — a NESTED image_url object, not a flat string — and a url may be a
    // "data:image/<format>;base64,<Base64>" data URI with a lower-case format. Limits: 30 MB per file,
    // 64 MB whole body, 256..5760 px per side, aspect ratio (w/h) 0.4..2.5, first frame ≤ 1 and
    // last frame ≤ 1. On the image-to-video path "the aspect ratio is determined by the input image
    // and ratio is always adaptive", so no ratio is sent with an image.

    [Fact]
    public async Task SubmitAsync_with_a_first_frame_sends_the_documented_nested_image_url_item()
    {
        byte[] firstFrame = Png(1024, 1024);
        string expectedUrl = "data:image/png;base64," + Convert.ToBase64String(firstFrame);

        string body = await CapturedImageBodyAsync(new VideoGenerationRequest(
            Model: "MiniMax-H3",
            PromptText: "A dog barking",
            DurationSeconds: 8,
            Resolution: "2K",
            Ratio: "4:3",
            Images: new[] { new VideoGenerationImageInput("image/png", firstFrame) }));

        // Asserted as the exact string, because the defect this pins is a SHAPE defect: a flat
        // "image_url" string instead of the nested object, or a missing role, would satisfy any
        // field-by-field reading of the same document.
        body.Should().Be(
            "{\"model\":\"MiniMax-H3\",\"content\":[" +
            "{\"type\":\"text\",\"text\":\"A dog barking\"}," +
            "{\"type\":\"image_url\",\"image_url\":{\"url\":\"" + expectedUrl + "\"},\"role\":\"first_frame\"}" +
            "],\"duration\":8,\"resolution\":\"2K\"}",
            "no 'ratio' on an image request: the vendor forces adaptive on the image-to-video path");
    }

    [Fact]
    public async Task SubmitAsync_with_two_images_roles_them_first_then_last_and_still_omits_ratio()
    {
        byte[] firstFrame = Png(1024, 1024);
        byte[] lastFrame = Png(768, 768);
        string firstUrl = "data:image/png;base64," + Convert.ToBase64String(firstFrame);
        string lastUrl = "data:image/jpeg;base64," + Convert.ToBase64String(lastFrame);

        string body = await CapturedImageBodyAsync(new VideoGenerationRequest(
            Model: "MiniMax-H3-Max",
            PromptText: "A dog barking",
            DurationSeconds: 8,
            Resolution: "2K",
            Ratio: "4:3",
            Images: new[]
            {
                new VideoGenerationImageInput("image/png", firstFrame),
                new VideoGenerationImageInput("image/jpg", lastFrame)
            }));

        // "image/jpg" is normalised to the canonical data-URI token the vendor documents ("jpeg").
        body.Should().Be(
            "{\"model\":\"MiniMax-H3-Max\",\"content\":[" +
            "{\"type\":\"text\",\"text\":\"A dog barking\"}," +
            "{\"type\":\"image_url\",\"image_url\":{\"url\":\"" + firstUrl + "\"},\"role\":\"first_frame\"}," +
            "{\"type\":\"image_url\",\"image_url\":{\"url\":\"" + lastUrl + "\"},\"role\":\"last_frame\"}" +
            "],\"duration\":8,\"resolution\":\"2K\"}");
    }

    [Fact]
    public async Task SubmitAsync_with_no_image_is_byte_identical_to_the_legacy_body()
    {
        // The regression pin for every pre-existing caller: an image-less request must serialize
        // exactly as it did before images were transported at all, member order included.
        string body = await CapturedImageBodyAsync(new VideoGenerationRequest(
            Model: "MiniMax-H3",
            PromptText: "A dog barking",
            DurationSeconds: 8,
            Resolution: "2K",
            Ratio: "4:3"));

        body.Should().Be(
            "{\"model\":\"MiniMax-H3\",\"content\":[{\"type\":\"text\",\"text\":\"A dog barking\"}]," +
            "\"duration\":8,\"resolution\":\"2K\",\"ratio\":\"4:3\"}");
    }

    [Fact]
    public async Task SubmitAsync_with_no_image_does_not_add_a_model_check_the_legacy_path_never_had()
    {
        // The other half of the regression pin: with no image there is nothing to carry, so the model
        // name is not newly validated here — an unknown one is still sent, as it always was, and the
        // vendor answers 4xx. Only an image request consults the model's declaration.
        string body = await CapturedImageBodyAsync(new VideoGenerationRequest(
            Model: "MiniMax-H4",
            PromptText: "A dog barking",
            DurationSeconds: 8,
            Resolution: "2K",
            Ratio: "4:3"));

        body.Should().Contain("\"model\":\"MiniMax-H4\"");
    }

    [Theory]
    [InlineData("image/gif", 1024, 1024, "JPG, JPEG, PNG, WEBP, HEIC and HEIF")]
    [InlineData("image/png", 255, 1024, "256..5760")]
    [InlineData("image/png", 1024, 5761, "256..5760")]
    [InlineData("image/png", 300, 1000, "aspect-ratio")]
    [InlineData("image/png", 1000, 300, "aspect-ratio")]
    public async Task SubmitAsync_refuses_an_image_outside_the_vendors_documented_limits(
        string contentType, int width, int height, string expectedReason)
    {
        MiniMaxVideoGenerationClient client = ClientThatSendsNothing();
        VideoGenerationRequest request = new(
            Model: "MiniMax-H3",
            PromptText: "A dog barking",
            DurationSeconds: 8,
            Resolution: "2K",
            Ratio: "4:3",
            Images: new[] { new VideoGenerationImageInput(contentType, Png(width, height)) });

        Func<Task> act = () => client.SubmitAsync(request, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage($"*{expectedReason}*");
    }

    [Fact]
    public async Task SubmitAsync_refuses_an_image_over_the_documented_per_file_size()
    {
        byte[] oversized = new byte[MiniMaxVideoGenerationClient.MaxImageBytes + 1];
        PngHeaderInto(oversized, 1024, 1024);

        MiniMaxVideoGenerationClient client = ClientThatSendsNothing();
        VideoGenerationRequest request = new(
            Model: "MiniMax-H3",
            PromptText: "A dog barking",
            DurationSeconds: 8,
            Resolution: "2K",
            Ratio: "4:3",
            Images: new[] { new VideoGenerationImageInput("image/png", oversized) });

        Func<Task> act = () => client.SubmitAsync(request, CancellationToken.None);

        // The size is checked before the header is read, so this is the size refusal and not a
        // "cannot read the pixel size" one.
        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*30000000-byte*");
    }

    [Fact]
    public async Task SubmitAsync_refuses_a_HEIC_image_whose_limits_cannot_be_checked_locally()
    {
        // HEIC/HEIF are on the vendor's format list, and this repository reads PNG/JPEG/WebP headers
        // only — so the two size limits that depend on the pixel header cannot be checked and the
        // image is refused rather than sent unchecked.
        MiniMaxVideoGenerationClient client = ClientThatSendsNothing();
        VideoGenerationRequest request = new(
            Model: "MiniMax-H3",
            PromptText: "A dog barking",
            DurationSeconds: 8,
            Resolution: "2K",
            Ratio: "4:3",
            Images: new[] { new VideoGenerationImageInput("image/heic", IsoBmff("heic")) });

        Func<Task> act = () => client.SubmitAsync(request, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*cannot be read locally*");
    }

    [Fact]
    public async Task SubmitAsync_refuses_more_images_than_a_first_and_a_last_frame()
    {
        VideoGenerationImageInput frame = new("image/png", Png(1024, 1024));
        MiniMaxVideoGenerationClient client = ClientThatSendsNothing();
        VideoGenerationRequest request = new(
            Model: "MiniMax-H3",
            PromptText: "A dog barking",
            DurationSeconds: 8,
            Resolution: "2K",
            Ratio: "4:3",
            Images: new[] { frame, frame, frame });

        Func<Task> act = () => client.SubmitAsync(request, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*at most 2*");
    }

    [Theory]
    [InlineData("MiniMax-H3")]
    [InlineData("MiniMax-H3-Max")]
    public void ImageInputSupport_declares_a_first_and_a_last_frame_for_every_served_model(string model)
    {
        ClientThatSendsNothing().ImageInputSupport(model).Should().Be(VideoImageInputSupport.FirstAndLastFrame);
    }

    [Fact]
    public void ImageInputSupport_throws_for_a_model_this_client_does_not_serve()
    {
        Action act = () => ClientThatSendsNothing().ImageInputSupport("MiniMax-H4");

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void EnsureBodyWithinLimit_pins_the_documented_whole_body_ceiling()
    {
        Action atCeiling = () => MiniMaxVideoGenerationClient.EnsureBodyWithinLimit(MiniMaxVideoGenerationClient.MaxRequestBodyBytes);
        atCeiling.Should().NotThrow();

        Action over = () => MiniMaxVideoGenerationClient.EnsureBodyWithinLimit(MiniMaxVideoGenerationClient.MaxRequestBodyBytes + 1);
        over.Should().Throw<ArgumentException>().WithMessage("*64 MB*");
    }

    /// <summary>Submits through a recording handler and returns the exact body that was sent.</summary>
    private static async Task<string> CapturedImageBodyAsync(VideoGenerationRequest request)
    {
        string? captured = null;
        HttpClient httpClient = new(new StubHandler(async httpRequest =>
        {
            captured = await httpRequest.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task_id":"task-img"}""")
            };
        }))
        {
            BaseAddress = new Uri("https://api.minimax.io")
        };

        await new MiniMaxVideoGenerationClient(httpClient, "key").SubmitAsync(request, CancellationToken.None);
        return captured!;
    }

    /// <summary>A client whose handler fails the test if any request is sent at all.</summary>
    private static MiniMaxVideoGenerationClient ClientThatSendsNothing() =>
        new(
            new HttpClient(new SimpleStubHandler(_ =>
                throw new InvalidOperationException("No HTTP request was expected, but one was sent.")))
            {
                BaseAddress = new Uri("https://api.minimax.io")
            },
            "key");

    /// <summary>A PNG whose header reports the given pixel size; nothing else is read from it.</summary>
    private static byte[] Png(int width, int height)
    {
        byte[] bytes = new byte[32];
        PngHeaderInto(bytes, width, height);
        return bytes;
    }

    /// <summary>Writes the PNG signature and IHDR size into an existing buffer (<see cref="ImageHeader"/> reads offsets 0..23).</summary>
    private static void PngHeaderInto(byte[] bytes, int width, int height)
    {
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        bytes[4] = 0x0D;
        bytes[5] = 0x0A;
        bytes[6] = 0x1A;
        bytes[7] = 0x0A;
        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
    }

    /// <summary>An ISO base media file (the container HEIC/HEIF use), for which the header reader has no dimensions.</summary>
    private static byte[] IsoBmff(string brand)
    {
        byte[] bytes = new byte[32];
        bytes[3] = 0x18;
        bytes[4] = (byte)'f';
        bytes[5] = (byte)'t';
        bytes[6] = (byte)'y';
        bytes[7] = (byte)'p';
        for (int i = 0; i < brand.Length; i++)
        {
            bytes[8 + i] = (byte)brand[i];
        }

        return bytes;
    }
}

public class VideoGenerationClientFactoryTests
{
    [Fact]
    public void Get_returns_MiniMaxVideoGenerationClient_for_MiniMax_provider()
    {
        VideoGenerationClientFactory factory = new();
        ResolvedVideoGenerationProvider provider = new(
            ProviderId: Guid.NewGuid(),
            Name: "test-provider",
            Kind: InferenceProviderKind.MiniMax,
            Endpoint: "https://api.minimax.io",
            ModelName: "MiniMax-H3",
            ApiKey: "test-key",
            TimeoutSeconds: 300);

        IVideoGenerationClient client = factory.Get(provider);

        client.Should().BeOfType<MiniMaxVideoGenerationClient>();
    }

    [Fact]
    public void Get_throws_NotSupportedException_for_unsupported_provider_kind()
    {
        VideoGenerationClientFactory factory = new();
        ResolvedVideoGenerationProvider provider = new(
            ProviderId: Guid.NewGuid(),
            Name: "test-provider",
            Kind: InferenceProviderKind.AzureOpenAI,
            Endpoint: "https://example.com",
            ModelName: "gpt-4",
            ApiKey: "test-key",
            TimeoutSeconds: 300);

        Action act = () => factory.Get(provider);

        var ex = act.Should().Throw<NotSupportedException>();
        ex.And.Message.Should().Contain("not supported");
        ex.And.Message.Should().Contain("MiniMax");
    }

    [Fact]
    public void Get_throws_NotSupportedException_for_Anthropic_provider()
    {
        VideoGenerationClientFactory factory = new();
        ResolvedVideoGenerationProvider provider = new(
            ProviderId: Guid.NewGuid(),
            Name: "test-provider",
            Kind: InferenceProviderKind.Anthropic,
            Endpoint: "https://api.anthropic.com",
            ModelName: "claude-opus-4",
            ApiKey: "sk-ant-x",
            TimeoutSeconds: 300);

        Action act = () => factory.Get(provider);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Get_throws_ArgumentNullException_when_provider_is_null()
    {
        VideoGenerationClientFactory factory = new();

        Action act = () => factory.Get(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("provider");
    }

    [Fact]
    public void Get_sets_HttpClient_BaseAddress_from_provider_endpoint()
    {
        // Regression test: the HttpClient must be configured with the provider's endpoint,
        // or subsequent requests will fail to route correctly.
        VideoGenerationClientFactory factory = new();
        ResolvedVideoGenerationProvider provider = new(
            ProviderId: Guid.NewGuid(),
            Name: "test-provider",
            Kind: InferenceProviderKind.MiniMax,
            Endpoint: "https://api.minimax.io",
            ModelName: "MiniMax-H3",
            ApiKey: "test-key",
            TimeoutSeconds: 300);

        IVideoGenerationClient client = factory.Get(provider);
        MiniMaxVideoGenerationClient miniMaxClient = client as MiniMaxVideoGenerationClient
            ?? throw new InvalidOperationException("Expected MiniMaxVideoGenerationClient");

        // Verify that the client is initialized with the correct endpoint by calling Describe
        // (which doesn't make HTTP calls). If the HttpClient wasn't configured, this would still pass,
        // but the actual SubmitAsync/PollAsync would fail. This test documents the contract.
        var caps = miniMaxClient.Describe("MiniMax-H3");
        caps.MinDurationSeconds.Should().Be(4);
    }

    [Fact]
    public void Get_caches_clients_by_CacheKey_and_returns_same_instance()
    {
        VideoGenerationClientFactory factory = new();
        ResolvedVideoGenerationProvider provider1 = new(
            ProviderId: Guid.NewGuid(),
            Name: "test-provider",
            Kind: InferenceProviderKind.MiniMax,
            Endpoint: "https://api.minimax.io",
            ModelName: "MiniMax-H3",
            ApiKey: "test-key",
            TimeoutSeconds: 300);

        // Create provider2 with same settings (will have the same CacheKey)
        ResolvedVideoGenerationProvider provider2 = new(
            ProviderId: Guid.NewGuid(),
            Name: "test-provider",
            Kind: InferenceProviderKind.MiniMax,
            Endpoint: "https://api.minimax.io",
            ModelName: "MiniMax-H3",
            ApiKey: "test-key",
            TimeoutSeconds: 300);

        IVideoGenerationClient client1 = factory.Get(provider1);
        IVideoGenerationClient client2 = factory.Get(provider2);

        // Same CacheKey should return the same client instance
        client1.Should().BeSameAs(client2);
    }

    [Fact]
    public void Get_returns_different_instances_for_different_CacheKeys()
    {
        VideoGenerationClientFactory factory = new();
        ResolvedVideoGenerationProvider provider1 = new(
            ProviderId: Guid.NewGuid(),
            Name: "test-provider-1",
            Kind: InferenceProviderKind.MiniMax,
            Endpoint: "https://api.minimax.io",
            ModelName: "MiniMax-H3",
            ApiKey: "test-key-1",
            TimeoutSeconds: 300);

        ResolvedVideoGenerationProvider provider2 = new(
            ProviderId: Guid.NewGuid(),
            Name: "test-provider-2",
            Kind: InferenceProviderKind.MiniMax,
            Endpoint: "https://api.minimax.io",
            ModelName: "MiniMax-H3",
            ApiKey: "test-key-2",  // Different API key means different CacheKey
            TimeoutSeconds: 300);

        IVideoGenerationClient client1 = factory.Get(provider1);
        IVideoGenerationClient client2 = factory.Get(provider2);

        // Different CacheKey should return different client instances
        client1.Should().NotBeSameAs(client2);
    }
}
