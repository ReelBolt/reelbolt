#nullable enable
using System;
using System.Collections.Generic;
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

/// <summary>
/// Every provider client in the image arm, driven against a MOCKED HTTP layer. Nothing here touches
/// a live vendor: the whole point is that these three APIs are paid and a wrong field name fails at
/// runtime against a real invoice rather than in the suite.
/// </summary>
public class ImageClientTests
{
    /// <summary>A 1x1 PNG, so content sniffing has a real payload to recognise.</summary>
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
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

    private static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpClient Client(StubHandler handler, string baseUrl) =>
        new(handler) { BaseAddress = new Uri(baseUrl) };

    // ================================================================ MiniMax

    [Fact]
    public async Task MiniMax_posts_to_v1_image_generation_with_a_bearer_token_and_the_documented_body()
    {
        StubHandler handler = new(_ => Json("""{"id":"trace-1","base_resp":{"status_code":0},"data":{"image_urls":[]}}"""));
        HttpClient http = Client(handler, "https://api.minimax.io");

        MiniMaxImageClient client = new(http, "mm-key", new HttpClient(new StubHandler(_ => Bytes(PngBytes))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(
            new ImageGenerationRequest("image-01", "a cover", AspectRatio: "16:9", Count: 3, Seed: 7),
            CancellationToken.None));

        HttpRequestMessage sent = handler.Requests[0];
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.AbsolutePath.Should().Be("/v1/image_generation");
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sent.Headers.Authorization.Parameter.Should().Be("mm-key");

        using JsonDocument body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("model").GetString().Should().Be("image-01");
        body.RootElement.GetProperty("prompt").GetString().Should().Be("a cover");
        body.RootElement.GetProperty("aspect_ratio").GetString().Should().Be("16:9");
        body.RootElement.GetProperty("n").GetInt32().Should().Be(3);
        body.RootElement.GetProperty("seed").GetInt64().Should().Be(7);
        // Always url, never base64: the envelope stays small and the bytes are fetched once through
        // the guarded download client.
        body.RootElement.GetProperty("response_format").GetString().Should().Be("url");
        body.RootElement.TryGetProperty("subject_reference", out _).Should().BeFalse();
    }

    [Fact]
    public async Task MiniMax_downloads_every_url_it_returns_and_sniffs_the_content_type()
    {
        StubHandler api = new(_ => Json("""{"id":"t","base_resp":{"status_code":0},"data":{"image_urls":["https://filecdn.minimax.chat/a.png","https://filecdn.minimax.chat/b.png"]}}"""));
        StubHandler downloader = new(_ => Bytes(PngBytes));

        MiniMaxImageClient client = new(
            Client(api, "https://api.minimax.io"), "k", Client(downloader, "https://filecdn.minimax.chat"));

        GeneratedImageSet set = await client.GenerateAsync(
            new ImageGenerationRequest("image-01", "x", Count: 2), CancellationToken.None);

        set.Images.Should().HaveCount(2, "n images means n billed images, and the caller must see all of them");
        set.Images.Should().OnlyContain(i => i.ContentType == "image/png");
        set.VendorRequestId.Should().Be("t");
        downloader.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task MiniMax_reads_a_non_zero_base_resp_status_code_as_a_failure_even_on_HTTP_200()
    {
        StubHandler handler = new(_ => Json("""{"base_resp":{"status_code":1008,"status_msg":"Insufficient account balance"}}"""));
        MiniMaxImageClient client = new(Client(handler, "https://api.minimax.io"), "k");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GenerateAsync(new ImageGenerationRequest("image-01", "x"), CancellationToken.None));

        ex.Message.Should().Contain("1008");
        ex.Message.Should().Contain("insufficient account balance");
    }

    [Fact]
    public async Task MiniMax_refuses_mask_inpainting_instead_of_sending_a_mask_it_would_ignore()
    {
        MiniMaxImageClient client = new(new HttpClient(new StubHandler(_ => Json("{}"))), "k");

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.InpaintAsync(
            new ImageInpaintRequest("image-01", "remove it", "https://example.com/a.png", PngBytes),
            CancellationToken.None));

        ex.Message.Should().Contain("no mask field");
        client.Describe("image-01").SupportsMaskInpaint.Should().BeFalse();
    }

    [Fact]
    public async Task MiniMax_probe_sends_an_unknown_model_and_treats_2013_as_key_accepted()
    {
        StubHandler handler = new(_ => Json("""{"base_resp":{"status_code":2013,"status_msg":"invalid params, model not found"}}"""));
        MiniMaxImageClient client = new(Client(handler, "https://api.minimax.io"), "k");

        ImageProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.IsOk.Should().BeTrue();
        using JsonDocument body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("model").GetString().Should().StartWith("__reelbolt_probe",
            "the probe must ask for a model that cannot exist, so it can never generate and never bill");
    }

    [Fact]
    public async Task MiniMax_probe_reports_a_rejected_key()
    {
        StubHandler handler = new(_ => Json("""{"base_resp":{"status_code":2049,"status_msg":"invalid api key"}}"""));
        MiniMaxImageClient client = new(Client(handler, "https://api.minimax.io"), "k");

        ImageProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.IsOk.Should().BeFalse();
        probe.Reason.Should().Contain("2049");
    }

    [Fact]
    public async Task MiniMax_rejects_a_prompt_over_its_published_cap_rather_than_truncating_it()
    {
        MiniMaxImageClient client = new(new HttpClient(new StubHandler(_ => Json("{}"))), "k");
        string tooLong = new string('a', MiniMaxImageClient.MaxPromptLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GenerateAsync(new ImageGenerationRequest("image-01", tooLong), CancellationToken.None));
    }

    // ================================================================ OpenAI-compatible

    [Fact]
    public async Task OpenAI_compatible_posts_to_images_generations_and_decodes_b64_json()
    {
        string payload = JsonSerializer.Serialize(new
        {
            created = 123,
            data = new[] { new { b64_json = Convert.ToBase64String(PngBytes), revised_prompt = "revised" } }
        });

        StubHandler handler = new(_ => Json(payload));
        HttpClient http = Client(handler, "https://api.openai.com/v1");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "oa-key");

        OpenAICompatibleImageClient client = new(http);
        GeneratedImageSet set = await client.GenerateAsync(
            new ImageGenerationRequest("gpt-image-2", "a cover", Width: 1024, Height: 1536), CancellationToken.None);

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/v1/images/generations");
        using JsonDocument body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("size").GetString().Should().Be("1024x1536");
        // A ratio is NOT translated into a size: the Images API does not take one, and inventing a
        // resolution the caller never asked for would be a silent product decision.
        body.RootElement.TryGetProperty("aspect_ratio", out _).Should().BeFalse();

        set.Images.Should().ContainSingle();
        set.Images[0].ContentType.Should().Be("image/png");
        set.Images[0].RevisedPrompt.Should().Be("revised");
    }

    [Fact]
    public async Task OpenAI_compatible_edits_send_the_documented_images_array_not_multipart()
    {
        StubHandler handler = new(_ => Json("""{"created":1,"data":[{"b64_json":"%B64%"}]}""".Replace("%B64%", Convert.ToBase64String(PngBytes))));
        OpenAICompatibleImageClient client = new(Client(handler, "https://api.openai.com/v1"));

        await client.EditAsync(
            new ImageEditRequest("gpt-image-2", "make it teal", "https://files.example.com/src.png"),
            CancellationToken.None);

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/v1/images/edits");
        using JsonDocument body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("images")[0].GetProperty("image_url").GetString()
            .Should().Be("https://files.example.com/src.png");
    }

    [Fact]
    public async Task OpenAI_compatible_inpaint_sends_the_mask_as_a_data_url()
    {
        StubHandler handler = new(_ => Json("""{"created":1,"data":[{"b64_json":"%B64%"}]}""".Replace("%B64%", Convert.ToBase64String(PngBytes))));
        OpenAICompatibleImageClient client = new(Client(handler, "https://api.openai.com/v1"));

        await client.InpaintAsync(
            new ImageInpaintRequest("gpt-image-2", "remove it", "https://files.example.com/src.png", PngBytes),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("mask").GetProperty("image_url").GetString()
            .Should().StartWith("data:image/png;base64,");
    }

    [Fact]
    public async Task OpenAI_compatible_rejects_a_knowingly_empty_image_list_as_a_failure()
    {
        StubHandler handler = new(_ => Json("""{"created":1,"data":[]}"""));
        OpenAICompatibleImageClient client = new(Client(handler, "https://api.openai.com/v1"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GenerateAsync(new ImageGenerationRequest("m", "x"), CancellationToken.None));

        ex.Message.Should().Contain("may still have been billed");
    }

    [Fact]
    public async Task OpenAI_compatible_probe_reads_a_model_error_as_key_accepted_and_an_auth_error_as_not()
    {
        OpenAICompatibleImageClient accepted = new(Client(
            new StubHandler(_ => Json("""{"error":{"message":"The model `x` does not exist"}}""", HttpStatusCode.NotFound)),
            "https://api.openai.com/v1"));
        (await accepted.ProbeAsync(CancellationToken.None)).IsOk.Should().BeTrue();

        // A 400 that names the credential must NOT be read as "the model was wrong, so the key is
        // fine" — some gateways answer 400 rather than 401 for a missing key.
        OpenAICompatibleImageClient rejected = new(Client(
            new StubHandler(_ => Json("""{"error":{"message":"Incorrect API key provided"}}""", HttpStatusCode.BadRequest)),
            "https://api.openai.com/v1"));
        (await rejected.ProbeAsync(CancellationToken.None)).IsOk.Should().BeFalse();
    }

    // ================================================================ Black Forest Labs

    private static BlackForestLabsImageClient Bfl(StubHandler handler, StubHandler? downloader = null) =>
        new(
            Client(handler, "https://api.bfl.ai"),
            "bfl-key",
            downloader is null ? null : Client(downloader, "https://delivery.bfl.ai"),
            pollInterval: TimeSpan.Zero,
            delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task BlackForestLabs_submits_to_the_model_path_with_x_key_and_polls_the_returned_url()
    {
        StubHandler api = new(request => request.Method == HttpMethod.Post
            ? Json("""{"id":"req-1","polling_url":"https://api.bfl.ai/v1/get_result?id=req-1"}""")
            : Json("""{"status":"Ready","result":{"sample":"https://delivery.bfl.ai/x.png"}}"""));
        StubHandler downloader = new(_ => Bytes(PngBytes));

        GeneratedImageSet set = await Bfl(api, downloader).GenerateAsync(
            new ImageGenerationRequest("flux-2-pro", "a cover", Width: 1024, Height: 1024), CancellationToken.None);

        api.Requests[0].Method.Should().Be(HttpMethod.Post);
        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/v1/flux-2-pro",
            "BFL selects the model by URL path, not by a body field");
        api.Requests[0].Headers.GetValues("x-key").Should().ContainSingle().Which.Should().Be("bfl-key");
        api.Requests[0].Headers.Authorization.Should().BeNull("BFL uses x-key, never a bearer token");

        api.Requests[1].Method.Should().Be(HttpMethod.Get);
        api.Requests[1].RequestUri!.ToString().Should().Be("https://api.bfl.ai/v1/get_result?id=req-1",
            "the polling_url the vendor returned is the one that must be polled");

        // The signed URL expires in 10 minutes and is not CORS-enabled, so the client must have
        // already fetched the bytes rather than handing the link on.
        set.Images.Should().ContainSingle().Which.Bytes.Should().Equal(PngBytes);
        downloader.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task BlackForestLabs_surfaces_a_failed_job_rather_than_returning_an_empty_set()
    {
        StubHandler api = new(request => request.Method == HttpMethod.Post
            ? Json("""{"id":"req-2","polling_url":"https://api.bfl.ai/v1/get_result?id=req-2"}""")
            : Json("""{"status":"Content Moderated","result":{}}"""));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Bfl(api).GenerateAsync(new ImageGenerationRequest("flux-2-pro", "x"), CancellationToken.None));

        ex.Message.Should().Contain("Content Moderated");
    }

    [Fact]
    public async Task BlackForestLabs_fails_loudly_on_an_unrecognised_status_instead_of_polling_forever()
    {
        StubHandler api = new(request => request.Method == HttpMethod.Post
            ? Json("""{"id":"req-3","polling_url":"https://api.bfl.ai/v1/get_result?id=req-3"}""")
            : Json("""{"status":"Something New"}"""));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => Bfl(api).GenerateAsync(new ImageGenerationRequest("flux-2-pro", "x"), CancellationToken.None));
    }

    [Fact]
    public void BlackForestLabs_reports_mask_inpainting_only_for_the_fill_model()
    {
        BlackForestLabsImageClient client = new(new HttpClient(), "k");

        client.Describe(BlackForestLabsImageClient.FillModel).SupportsMaskInpaint.Should().BeTrue();
        client.Describe("flux-2-pro").SupportsMaskInpaint.Should().BeFalse();
        client.Describe("flux-2-pro").SupportsInstructionEdit.Should().BeTrue();
    }

    [Fact]
    public async Task BlackForestLabs_refuses_an_instruction_edit_against_the_fill_model()
    {
        BlackForestLabsImageClient client = new(new HttpClient(), "k");

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(() => client.EditAsync(
            new ImageEditRequest(BlackForestLabsImageClient.FillModel, "x", "https://example.com/a.png"),
            CancellationToken.None));

        ex.Message.Should().Contain("InpaintAsync");
    }

    [Fact]
    public async Task BlackForestLabs_inpaint_downloads_the_source_then_sends_image_and_mask_as_base64()
    {
        StubHandler api = new(request => request.Method == HttpMethod.Post
            ? Json("""{"id":"req-4","polling_url":"https://api.bfl.ai/v1/get_result?id=req-4"}""")
            : Json("""{"status":"Ready","result":{"sample":"https://delivery.bfl.ai/y.png"}}"""));
        StubHandler downloader = new(_ => Bytes(PngBytes));

        await Bfl(api, downloader).InpaintAsync(
            new ImageInpaintRequest(BlackForestLabsImageClient.FillModel, "remove the car",
                "https://files.example.com/src.png", PngBytes),
            CancellationToken.None);

        // The source URL is fetched through the download client, not passed to BFL as a URL:
        // flux-pro-1.0-fill takes base64, not a link.
        downloader.Requests.Should().HaveCount(2);
        using JsonDocument body = JsonDocument.Parse(api.Bodies[0]);
        body.RootElement.GetProperty("image").GetString().Should().Be(Convert.ToBase64String(PngBytes));
        body.RootElement.GetProperty("mask").GetString().Should().Be(Convert.ToBase64String(PngBytes));
        body.RootElement.GetProperty("output_format").GetString().Should().Be("png");
    }

    [Fact]
    public async Task BlackForestLabs_probe_asks_for_a_model_that_cannot_exist()
    {
        StubHandler handler = new(_ => Json("""{"detail":"model not found"}""", HttpStatusCode.NotFound));
        BlackForestLabsImageClient client = Bfl(handler);

        ImageProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.IsOk.Should().BeTrue();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().StartWith("/v1/__reelbolt_probe");
        handler.Requests[0].Headers.GetValues("x-key").Should().ContainSingle();
    }
}
