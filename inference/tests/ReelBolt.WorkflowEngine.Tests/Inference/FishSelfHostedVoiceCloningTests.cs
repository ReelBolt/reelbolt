#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// Voice cloning against a self-hosted fish-speech server (docs/voiceover.md "Cloning on a
/// self-hosted fish-speech server"): the endpoint is probed once for the server's reference
/// library, a clone is one WAV plus its transcript uploaded to <c>/v1/references/add</c> under a
/// generated id, and deletion is <c>DELETE /v1/references/delete</c>. The hosted API is unchanged.
/// </summary>
[Xunit.Collection("VendorDeleteEnv")]
public class FishSelfHostedVoiceCloningTests
{
    private sealed record Seen(HttpMethod Method, string Path, string? ContentType, string Body);

    /// <summary>Routes by method + path; records every request with its body read as text.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new();
        public int ListFailuresRemaining { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            Requests.Add(new Seen(request.Method, path, request.Content?.Headers.ContentType?.MediaType, body));
            if (path == "/v1/references/list" && ListFailuresRemaining-- > 0)
                throw new HttpRequestException("connection refused");
            return Routes.TryGetValue(request.Method.Method + " " + path, out var route)
                ? route()
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\":\"Not Found\"}") };
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static RoutingHandler SelfHostedServer()
    {
        var handler = new RoutingHandler();
        handler.Routes["GET /v1/references/list"] = () => Json(HttpStatusCode.OK, "{\"success\":true,\"reference_ids\":[],\"message\":\"Found 0 reference voices\"}");
        handler.Routes["POST /v1/references/add"] = () => Json(HttpStatusCode.OK, "{\"success\":true,\"message\":\"added\",\"reference_id\":\"x\"}");
        handler.Routes["DELETE /v1/references/delete"] = () => Json(HttpStatusCode.OK, "{\"success\":true,\"message\":\"deleted\",\"reference_id\":\"x\"}");
        return handler;
    }

    private static FishAudioSpeechSynthesisClient Client(RoutingHandler handler, string endpoint = "http://host.docker.internal:8180/") =>
        new(new HttpClient(handler) { BaseAddress = new Uri(endpoint) }, "s2-pro");

    private static byte[] Wav()
    {
        var bytes = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        return bytes;
    }

    private static VoiceCloneRequest Request(string? transcript = "Hello there.", byte[]? audio = null, int count = 1) =>
        new("Presenter", Enumerable.Range(0, count).Select(_ => new VoiceReferenceAudio(audio ?? Wav(), "take.wav", "audio/wav", transcript)).ToList());

    [Fact]
    public async Task The_hosted_api_is_recognized_by_its_host_without_a_request()
    {
        var handler = new RoutingHandler();
        handler.Routes["POST /model"] = () => Json(HttpStatusCode.Created, "{\"_id\":\"m1\",\"state\":\"trained\"}");
        FishAudioSpeechSynthesisClient client = Client(handler, "https://api.fish.audio");

        (await client.GetRequirementsAsync(CancellationToken.None)).Should().Be(VoiceCloningRequirements.Hosted);
        CreatedVoice created = await client.CreateVoiceAsync(Request(transcript: null), CancellationToken.None);

        created.RemoteVoiceId.Should().Be("m1");
        handler.Requests.Select(r => r.Path).Should().Equal("/model");
    }

    [Fact]
    public async Task A_self_hosted_server_is_detected_once_and_reports_its_requirements()
    {
        RoutingHandler handler = SelfHostedServer();
        FishAudioSpeechSynthesisClient client = Client(handler);

        (await client.GetRequirementsAsync(CancellationToken.None)).Should().Be(VoiceCloningRequirements.SelfHostedFishSpeech);
        (await client.GetRequirementsAsync(CancellationToken.None)).Should().Be(VoiceCloningRequirements.SelfHostedFishSpeech);

        handler.Requests.Should().ContainSingle().Which.Path.Should().Be("/v1/references/list");
    }

    [Fact]
    public async Task Clone_uploads_one_wav_and_its_transcript_under_a_generated_id()
    {
        RoutingHandler handler = SelfHostedServer();
        FishAudioSpeechSynthesisClient client = Client(handler);

        CreatedVoice created = await client.CreateVoiceAsync(Request("Hello there."), CancellationToken.None);

        created.RemoteVoiceId.Should().StartWith(FishAudioSpeechSynthesisClient.SelfHostedReferenceIdPrefix);
        created.RemoteVoiceId.Should().MatchRegex("^[a-zA-Z0-9\\-_ ]+$").And.HaveLength(41);
        Seen add = handler.Requests.Single(r => r.Path == "/v1/references/add");
        add.Method.Should().Be(HttpMethod.Post);
        add.ContentType.Should().Be("multipart/form-data");
        add.Body.Should().Contain("name=id").And.Contain(created.RemoteVoiceId)
            .And.Contain("name=text").And.Contain("Hello there.")
            .And.Contain("name=audio").And.Contain("filename=reference.wav");
        handler.Requests.Should().NotContain(r => r.Path == "/model");
    }

    [Fact]
    public async Task Two_clones_never_share_an_id()
    {
        RoutingHandler handler = SelfHostedServer();
        FishAudioSpeechSynthesisClient client = Client(handler);

        CreatedVoice a = await client.CreateVoiceAsync(Request(), CancellationToken.None);
        CreatedVoice b = await client.CreateVoiceAsync(Request(), CancellationToken.None);

        a.RemoteVoiceId.Should().NotBe(b.RemoteVoiceId);
    }

    [Fact]
    public async Task Clone_refuses_what_the_server_cannot_store_without_uploading()
    {
        RoutingHandler handler = SelfHostedServer();
        FishAudioSpeechSynthesisClient client = Client(handler);

        await FluentActions.Awaiting(() => client.CreateVoiceAsync(Request(transcript: " "), CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => client.CreateVoiceAsync(Request(audio: Encoding.ASCII.GetBytes("ID3 not a wav")), CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => client.CreateVoiceAsync(Request(count: 2), CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();

        handler.Requests.Should().NotContain(r => r.Path == "/v1/references/add");
    }

    [Fact]
    public async Task A_server_refusal_surfaces_its_own_message()
    {
        RoutingHandler handler = SelfHostedServer();
        handler.Routes["POST /v1/references/add"] = () =>
            Json(HttpStatusCode.Conflict, "{\"success\":false,\"message\":\"Reference ID 'x' already exists\",\"reference_id\":\"x\"}");
        FishAudioSpeechSynthesisClient client = Client(handler);

        var thrown = await FluentActions.Awaiting(() => client.CreateVoiceAsync(Request(), CancellationToken.None))
            .Should().ThrowAsync<HttpRequestException>();
        thrown.Which.Message.Should().Contain("already exists").And.Contain("409");
    }

    [Fact]
    public async Task Delete_sends_the_reference_id_as_json_and_treats_404_as_already_gone()
    {
        RoutingHandler handler = SelfHostedServer();
        FishAudioSpeechSynthesisClient client = Client(handler);

        (await client.DeleteVoiceAsync("reelbolt-abc", CancellationToken.None)).Should().BeTrue();
        Seen delete = handler.Requests.Single(r => r.Path == "/v1/references/delete");
        delete.Method.Should().Be(HttpMethod.Delete);
        delete.ContentType.Should().Be("application/json");
        JsonDocument.Parse(delete.Body).RootElement.GetProperty("reference_id").GetString().Should().Be("reelbolt-abc");

        handler.Routes["DELETE /v1/references/delete"] = () => Json(HttpStatusCode.NotFound, "{\"success\":false,\"message\":\"not found\",\"reference_id\":\"x\"}");
        (await client.DeleteVoiceAsync("reelbolt-gone", CancellationToken.None)).Should().BeTrue();

        handler.Routes["DELETE /v1/references/delete"] = () => Json(HttpStatusCode.InternalServerError, "{\"success\":false,\"message\":\"File system error occurred\",\"reference_id\":\"x\"}");
        (await client.DeleteVoiceAsync("reelbolt-stuck", CancellationToken.None)).Should().BeFalse();
        handler.Requests.Should().NotContain(r => r.Path.StartsWith("/model"));
    }

    [Fact]
    public async Task A_non_fish_speech_answer_to_the_probe_means_the_hosted_model_api()
    {
        var handler = new RoutingHandler(); // /v1/references/list → 404, e.g. a proxy in front of api.fish.audio
        handler.Routes["POST /model"] = () => Json(HttpStatusCode.Created, "{\"_id\":\"m2\"}");
        FishAudioSpeechSynthesisClient client = Client(handler, "https://fish-proxy.example.com/");

        CreatedVoice created = await client.CreateVoiceAsync(Request(transcript: null), CancellationToken.None);

        created.RemoteVoiceId.Should().Be("m2");
        handler.Requests.Select(r => r.Path).Should().Equal("/v1/references/list", "/model");
    }

    [Fact]
    public async Task A_probe_that_could_not_connect_is_not_remembered()
    {
        RoutingHandler handler = SelfHostedServer();
        handler.ListFailuresRemaining = 1;
        FishAudioSpeechSynthesisClient client = Client(handler);

        await FluentActions.Awaiting(() => client.GetRequirementsAsync(CancellationToken.None)).Should().ThrowAsync<HttpRequestException>();
        (await client.GetRequirementsAsync(CancellationToken.None)).Should().Be(VoiceCloningRequirements.SelfHostedFishSpeech);
    }

    [Fact]
    public async Task Synthesis_names_the_reference_id_on_the_self_hosted_server_too()
    {
        var handler = SelfHostedServer();
        handler.Routes["POST /v1/tts"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wav()) };
        FishAudioSpeechSynthesisClient client = Client(handler);

        await client.SynthesizeAsync(new SpeechRequest("Line one.", "reelbolt-abc", null), CancellationToken.None);

        JsonDocument.Parse(handler.Requests.Single(r => r.Path == "/v1/tts").Body)
            .RootElement.GetProperty("reference_id").GetString().Should().Be("reelbolt-abc");
    }
}
