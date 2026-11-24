using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="ComfyUiVideoGenerationClient"/> against a recording <see cref="HttpMessageHandler"/>:
/// no server, no GPU, no credential. Pins the wire facts the client was built from (ComfyUI's
/// <c>POST /prompt</c>, <c>GET /history/{id}</c>, <c>GET /view</c>, <c>GET /system_stats</c>,
/// <c>GET /object_info/{class}</c>) and the two things that make this provider safe to expose to a
/// workflow: only a vetted template name is ever submitted, and a download goes only to the URL this
/// client itself built.
/// </summary>
public class ComfyUiVideoGenerationClientTests
{
    private const string BaseUrl = "http://comfyui:8188/";
    private const string T2V = "wan2.2-ti2v-5b-t2v";
    private const string I2V = "wan2.2-ti2v-5b-i2v";

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string PathAndQuery, string? Auth, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body));
            return await respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (ComfyUiVideoGenerationClient Client, RecordingHandler Handler) ClientFor(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, string apiKey = "")
    {
        var handler = new RecordingHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        return (new ComfyUiVideoGenerationClient(http, apiKey, seedSource: () => 7), handler);
    }

    private static VideoGenerationRequest Request(
        string model = T2V,
        string? idempotencyKey = null,
        IReadOnlyList<VideoGenerationImageInput>? images = null) =>
        new(model, "A lighthouse at dusk", 2, "480P", "16:9", Images: images, IdempotencyKey: idempotencyKey);

    [Fact]
    public void Constructor_refuses_a_client_without_a_base_address()
    {
        Action act = () => new ComfyUiVideoGenerationClient(new HttpClient(), "");
        act.Should().Throw<ArgumentException>("a self-hosted server has no vendor default URL");
    }

    [Theory]
    [InlineData("ltx-2")]
    [InlineData("{\"1\":{\"class_type\":\"UNETLoader\"}}")]
    [InlineData("/wan2.2-ti2v-5b-t2v")]
    public async Task SubmitAsync_rejects_a_non_template_model_before_any_request_leaves(string model)
    {
        var (client, handler) = ClientFor(_ => throw new InvalidOperationException("no request expected"));

        Func<Task> act = () => client.SubmitAsync(Request(model), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
        handler.Requests.Should().BeEmpty("an arbitrary graph or name must never reach the server");
    }

    [Fact]
    public async Task SubmitAsync_POSTs_the_vetted_graph_with_client_id_and_the_idempotency_key_as_prompt_id()
    {
        string promptId = Guid.NewGuid().ToString("D");
        var (client, handler) = ClientFor(_ => Task.FromResult(Json(HttpStatusCode.OK, $$$"""{"prompt_id":"{{{promptId}}}","number":3,"node_errors":{}}""")));

        ExternalJobHandle handle = await client.SubmitAsync(Request(idempotencyKey: promptId), CancellationToken.None);

        handle.RemoteJobId.Should().Be(promptId);
        handler.Requests.Should().ContainSingle();
        (HttpMethod method, string path, string? auth, string body) = handler.Requests[0];
        method.Should().Be(HttpMethod.Post);
        path.Should().Be("/prompt");
        auth.Should().BeNull("a blank key sends no Authorization header");

        JsonObject sent = (JsonObject)JsonNode.Parse(body)!;
        sent["client_id"]!.GetValue<string>().Should().Be(ComfyUiVideoGenerationClient.ClientId);
        sent["prompt_id"]!.GetValue<string>().Should().Be(promptId, "a UUID idempotency token IS the prompt id the server files the history under");
        JsonObject graph = (JsonObject)sent["prompt"]!;
        graph["4"]!["inputs"]!["text"]!.GetValue<string>().Should().Be("A lighthouse at dusk");
        graph["6"]!["inputs"]!["width"]!.GetValue<int>().Should().Be(832);
        graph["6"]!["inputs"]!["length"]!.GetValue<int>().Should().Be(49);
        graph["8"]!["inputs"]!["seed"]!.GetValue<long>().Should().Be(7);
    }

    [Fact]
    public async Task SubmitAsync_sends_a_bearer_token_when_a_key_is_configured()
    {
        var (client, handler) = ClientFor(
            _ => Task.FromResult(Json(HttpStatusCode.OK, """{"prompt_id":"11111111-1111-1111-1111-111111111111"}""")),
            apiKey: "proxy-token");

        await client.SubmitAsync(Request(), CancellationToken.None);

        handler.Requests[0].Auth.Should().Be("Bearer proxy-token");
    }

    [Fact]
    public async Task SubmitAsync_surfaces_a_400_node_error_as_an_exception()
    {
        var (client, _) = ClientFor(_ => Task.FromResult(Json(HttpStatusCode.BadRequest,
            """{"error":{"type":"invalid_prompt","message":"Cannot execute because a node is missing the class_type property."},"node_errors":{}}""")));

        Func<Task> act = () => client.SubmitAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task PollAsync_reports_Running_while_the_history_is_empty()
    {
        var (client, handler) = ClientFor(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        string id = Guid.NewGuid().ToString("D");

        ExternalJobStatus status = await client.PollAsync(new ExternalJobHandle(id), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Running);
        handler.Requests[0].PathAndQuery.Should().Be($"/history/{id}");
    }

    [Fact]
    public async Task PollAsync_reports_Failed_on_an_error_status_with_the_servers_message()
    {
        string id = Guid.NewGuid().ToString("D");
        var (client, _) = ClientFor(_ => Task.FromResult(Json(HttpStatusCode.OK, $$$$"""
            {"{{{{id}}}}":{"outputs":{},"status":{"status_str":"error","completed":false,"messages":[["execution_error",{"exception_message":"CUDA out of memory"}]]}}}
            """)));

        ExternalJobStatus status = await client.PollAsync(new ExternalJobHandle(id), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Failed);
        status.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PollAsync_reports_Succeeded_with_a_view_url_on_this_server_when_a_video_output_exists()
    {
        string id = Guid.NewGuid().ToString("D");
        var (client, _) = ClientFor(_ => Task.FromResult(Json(HttpStatusCode.OK, $$$$"""
            {"{{{{id}}}}":{"outputs":{"9":{"images":[{"filename":"abc_00001_.png","subfolder":"","type":"output"}],"videos":[{"filename":"abc_00001_.mp4","subfolder":"video","type":"output"}]}},"status":{"status_str":"success","completed":true,"messages":[]}}}
            """)));

        ExternalJobStatus status = await client.PollAsync(new ExternalJobHandle(id), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Succeeded);
        status.DownloadUrl.Should().StartWith("http://comfyui:8188/view?");
        status.DownloadUrl.Should().Contain("filename=abc_00001_.mp4").And.Contain("subfolder=video").And.Contain("type=output");
    }

    [Fact]
    public async Task PollAsync_treats_a_completed_prompt_with_only_a_png_as_Failed_not_as_a_clip()
    {
        string id = Guid.NewGuid().ToString("D");
        var (client, _) = ClientFor(_ => Task.FromResult(Json(HttpStatusCode.OK, $$$$"""
            {"{{{{id}}}}":{"outputs":{"9":{"images":[{"filename":"abc_00001_.png","subfolder":"","type":"output"}]}},"status":{"status_str":"success","completed":true}}}
            """)));

        ExternalJobStatus status = await client.PollAsync(new ExternalJobHandle(id), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Failed, "delivering a preview image would fail normalization after GPU time was spent");
    }

    [Fact]
    public async Task ProbeAsync_reads_system_stats_and_object_info_only_and_never_POSTs()
    {
        var (client, handler) = ClientFor(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/system_stats")
                return Task.FromResult(Json(HttpStatusCode.OK, """{"system":{"os":"posix","comfyui_version":"0.3.60","python_version":"3.12"},"devices":[]}"""));
            if (path.StartsWith("/object_info/", StringComparison.Ordinal))
            {
                string cls = Uri.UnescapeDataString(path["/object_info/".Length..]);
                return Task.FromResult(Json(HttpStatusCode.OK, $$$"""{"{{{cls}}}":{"input":{},"output":[]}}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeTrue(result.Reason);
        result.AuthRejected.Should().BeFalse();
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "a probe must never queue a prompt");
        handler.Requests.Select(r => r.PathAndQuery).Should().Contain("/system_stats");
        handler.Requests.Count(r => r.PathAndQuery.StartsWith("/object_info/", StringComparison.Ordinal)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ProbeAsync_is_not_ok_when_a_template_node_class_is_missing()
    {
        var (client, _) = ClientFor(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/system_stats")
                return Task.FromResult(Json(HttpStatusCode.OK, """{"system":{"comfyui_version":"0.3.60"},"devices":[]}"""));
            if (path.EndsWith("/Wan22ImageToVideoLatent", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            string cls = Uri.UnescapeDataString(path["/object_info/".Length..]);
            return Task.FromResult(Json(HttpStatusCode.OK, $$$"""{"{{{cls}}}":{}}"""));
        });

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("Wan22ImageToVideoLatent");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ProbeAsync_reports_AuthRejected_on_401_or_403(HttpStatusCode code)
    {
        var (client, _) = ClientFor(_ => Task.FromResult(new HttpResponseMessage(code)));

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeAsync_is_not_ok_when_the_server_is_not_ComfyUI()
    {
        var (client, _) = ClientFor(_ => Task.FromResult(Json(HttpStatusCode.OK, """{"hello":"world"}""")));

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("system_stats");
    }

    [Theory]
    [InlineData("http://evil.example/view?filename=a.mp4")]
    [InlineData("http://comfyui:9999/view?filename=a.mp4")]
    [InlineData("http://comfyui:8188/history/abc")]
    [InlineData("https://comfyui:8188/view?filename=a.mp4")]
    public async Task DownloadAsync_refuses_any_url_this_client_did_not_build(string url)
    {
        var (client, handler) = ClientFor(_ => throw new InvalidOperationException("no request expected"));

        Func<Task> act = () => client.DownloadAsync(url, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadAsync_streams_a_video_from_the_servers_own_view_endpoint()
    {
        var (client, handler) = ClientFor(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            return Task.FromResult(response);
        });

        await using System.IO.Stream stream = await client.DownloadAsync("http://comfyui:8188/view?filename=a.mp4&subfolder=&type=output", CancellationToken.None);
        using var buffer = new System.IO.MemoryStream();
        await stream.CopyToAsync(buffer);

        buffer.ToArray().Should().Equal(1, 2, 3, 4);
        handler.Requests.Should().ContainSingle().Which.PathAndQuery.Should().StartWith("/view?filename=a.mp4");
    }

    // --- Image input: the i2v template's own image-input API --------------------------------------
    //
    // The image-to-video graph is built from typed slots and fills exactly one image node: the frame
    // is uploaded to the server's own POST /upload/image (multipart: image, overwrite, type,
    // subfolder), which answers {"name","subfolder","type"}, and that name is what
    // LoadImage.inputs.image references (ComfyUI's server.py; the class remarks record the route).
    // The text-to-video graph has no image node at all, and neither template has a last-frame node —
    // Wan 2.2 TI2V-5B's latent node documents a start image only.

    [Fact]
    public async Task SubmitAsync_with_an_image_uploads_it_to_the_image_api_and_wires_the_servers_name_into_the_graph()
    {
        var (client, handler) = ClientFor(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/upload/image")
            {
                return Task.FromResult(Json(HttpStatusCode.OK,
                    """{"name":"keyframe-abc123.png","subfolder":"reelbolt","type":"input"}"""));
            }

            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"prompt_id":"11111111-1111-1111-1111-111111111111","number":1,"node_errors":{}}"""));
        });

        VideoGenerationImageInput frame = new("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
        await client.SubmitAsync(Request(I2V, images: new[] { frame }), CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].PathAndQuery.Should().Be("/upload/image");
        // Multipart, with the documented fields: the bytes under "image", type=input and the
        // client's own subfolder so a shared server keeps its inputs apart.
        handler.Requests[0].Body.Should().Contain("name=image; filename=keyframe-").And.Contain(".png");
        handler.Requests[0].Body.Should().Contain("name=type").And.Contain("input");
        handler.Requests[0].Body.Should().Contain("name=subfolder").And.Contain(ComfyUiVideoGenerationClient.UploadSubfolder);

        handler.Requests[1].PathAndQuery.Should().Be("/prompt");
        JsonObject graph = (JsonObject)JsonNode.Parse(handler.Requests[1].Body)!["prompt"]!;
        // The graph references THE NAME THE SERVER ANSWERED WITH, in the response's subfolder: a
        // locally invented name would be a broken graph, not an upload.
        graph["10"]!["class_type"]!.GetValue<string>().Should().Be("LoadImage");
        graph["10"]!["inputs"]!["image"]!.GetValue<string>().Should().Be("reelbolt/keyframe-abc123.png");
        JsonArray startImage = graph["6"]!["inputs"]!["start_image"]!.AsArray();
        startImage[0]!.GetValue<string>().Should().Be("10");
        startImage[1]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_refuses_an_image_on_the_text_to_video_template_before_any_request()
    {
        var (client, handler) = ClientFor(_ => throw new InvalidOperationException("no request expected"));
        VideoGenerationImageInput frame = new("image/png", new byte[] { 1, 2, 3 });

        Func<Task> act = () => client.SubmitAsync(Request(T2V, images: new[] { frame }), CancellationToken.None);

        // The text-to-video graph carries no image node, so there is nothing the frame could fill: a
        // refusal naming that, rather than an image silently dropped on the floor.
        await act.Should().ThrowAsync<NotSupportedException>();
        handler.Requests.Should().BeEmpty("nothing is uploaded for a template that cannot use an image");
    }

    [Fact]
    public async Task SubmitAsync_refuses_a_last_frame_on_the_image_to_video_template()
    {
        var (client, handler) = ClientFor(_ => throw new InvalidOperationException("no request expected"));
        VideoGenerationImageInput frame = new("image/png", new byte[] { 1, 2, 3 });

        Func<Task> act = () => client.SubmitAsync(Request(I2V, images: new[] { frame, frame }), CancellationToken.None);

        // First frame only: Wan22ImageToVideoLatent documents a start_image, so a second (last-frame)
        // image is refused with that reason rather than dropped or sent to an invented slot.
        (await act.Should().ThrowAsync<NotSupportedException>()).WithMessage("*first frame only*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_refuses_more_images_than_a_first_and_a_last_frame()
    {
        var (client, handler) = ClientFor(_ => throw new InvalidOperationException("no request expected"));
        VideoGenerationImageInput frame = new("image/png", new byte[] { 1, 2, 3 });

        Func<Task> act = () => client.SubmitAsync(Request(I2V, images: new[] { frame, frame, frame }), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*at most 2*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_requires_one_input_image_for_the_image_to_video_template()
    {
        var (client, handler) = ClientFor(_ => throw new InvalidOperationException("no request expected"));

        Func<Task> act = () => client.SubmitAsync(Request(I2V), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*requires one input image*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void ImageInputSupport_declares_what_each_template_graph_carries()
    {
        var (client, empty) = ClientFor(_ => throw new InvalidOperationException("no request expected"));

        client.ImageInputSupport(T2V).Should().Be(VideoImageInputSupport.None);
        client.ImageInputSupport(I2V).Should().Be(VideoImageInputSupport.FirstFrame);
        Action unknown = () => client.ImageInputSupport("ltx-2");
        unknown.Should().Throw<NotSupportedException>();
        empty.Requests.Should().BeEmpty("the declaration is answered from the template enum");
    }

    [Fact]
    public void Describe_throws_for_a_non_template_model_and_prices_a_template_in_GPU_seconds()
    {
        var (client, _) = ClientFor(_ => throw new InvalidOperationException());

        Action unknown = () => client.Describe("ltx-2");
        unknown.Should().Throw<NotSupportedException>();

        client.Describe(T2V).PricePerSecondByResolution["480P"].Should().Be(20m);
    }
}
