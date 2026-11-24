#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Avatars phase 3, lane C1: the fal talking-head routes (a still plus a speech track), their declaration, the price
/// cards, and the "do not keep a person's media" header on avatar requests. Driven against a mocked HTTP layer only:
/// every route is paid, and no live call is made.
/// </summary>
public class TalkingHeadClientTests
{
    private const string H3LipSync = "minimax/h3-max/lip-sync/image-to-video";
    private const string KlingAvatar = "fal-ai/kling-video/ai-avatar/v2/standard";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] Wav = [0x52, 0x49, 0x46, 0x46];

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

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static StubHandler Queue() => new(request => request.Method == HttpMethod.Post
        ? Json("{\"request_id\":\"r-1\",\"status_url\":\"https://queue.fal.run/x/requests/r-1/status\"," +
               "\"response_url\":\"https://queue.fal.run/x/requests/r-1\"}")
        : request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
            ? Json("{\"status\":\"COMPLETED\"}")
            : Json("{\"images\":[{\"url\":\"https://fal.media/a.png\"}]}"));

    private static FalAiVideoGenerationClient VideoClient(StubHandler api) =>
        new(new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "fal-key",
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);

    private static VideoGenerationReference Speech(int ms = 8_000, string type = "audio/wav") =>
        new(AvatarReferenceKind.Audio, AvatarReferenceRole.Speech, type, Wav, ms);

    private static VideoGenerationRequest TalkingHead(
        string model = H3LipSync, string prompt = "", int duration = 9, string resolution = "768P", string ratio = "9:16",
        IReadOnlyList<VideoGenerationReference>? references = null, bool still = true) =>
        new(model, prompt, duration, resolution, ratio,
            still ? [new VideoGenerationImageInput("image/png", Png)] : null,
            References: references ?? [Speech()])
        { NoVendorRetention = true };

    private static HttpRequestMessage OnlyPost(StubHandler api) => api.Requests.Single(r => r.Method == HttpMethod.Post);

    private static JsonElement PostBody(StubHandler api) =>
        JsonDocument.Parse(api.Bodies[api.Requests.FindIndex(r => r.Method == HttpMethod.Post)]).RootElement;

    // ================================================================ the declaration

    [Fact]
    public void A_talking_head_route_animates_a_still_driven_by_one_speech_track()
    {
        var client = VideoClient(Queue());

        VideoReferenceInputSupport h3 = client.ReferenceInputSupport(H3LipSync);
        h3.AnimatesStill.Should().BeTrue();
        h3.AudioDrivesSpeech.Should().BeTrue();
        h3.AcceptsAvatar.Should().BeTrue();
        (h3.MaxImages, h3.MaxVideos, h3.MaxAudio, h3.MaxFiles).Should().Be((0, 0, 1, 1));
        (h3.MinAudioMs, h3.MaxAudioTotalMs).Should().Be((5_000, 15_000));
        h3.TakesPrompt.Should().BeFalse("the H3 lip-sync schema has no prompt field");
        client.ReferenceInputSupport(KlingAvatar).TakesPrompt.Should().BeTrue();

        client.ReferenceInputSupport("fal-ai/vidu/q4/image-to-video").Should().Be(VideoReferenceInputSupport.None);
        client.InputMaterialCostUsd(H3LipSync, "768P", [new VideoReferenceMaterial(AvatarReferenceKind.Audio, 8_000)])
            .Should().Be(0m, "both routes bill only the delivered seconds");
        FalAiVideoGenerationClient.Catalog.DefaultFor(FalVideoJob.TalkingHead).Id.Should().Be(H3LipSync);
    }

    [Fact]
    public void A_still_model_refuses_a_frame_of_the_step_and_a_step_with_nothing_to_say()
    {
        VideoReferenceInputSupport support = VideoClient(Queue()).ReferenceInputSupport(H3LipSync);

        VideoReferenceInput.Refusal(support, H3LipSync, hasFrames: true, wantsDrivingAudio: true)
            .Should().Contain("first or last frame");
        VideoReferenceInput.Refusal(support, H3LipSync, hasFrames: false, wantsDrivingAudio: false)
            .Should().Contain("speech track");
        VideoReferenceInput.Refusal(support, H3LipSync, hasFrames: false, wantsDrivingAudio: true).Should().BeNull();
    }

    // ================================================================ the bodies

    [Fact]
    public async Task H3_lip_sync_sends_the_still_and_the_speech_as_data_uris_and_nothing_it_does_not_declare()
    {
        StubHandler api = Queue();

        await VideoClient(api).SubmitAsync(TalkingHead(), CancellationToken.None);

        JsonElement body = PostBody(api);
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["image_url", "audio_url", "resolution"]);
        body.GetProperty("image_url").GetString().Should().Be("data:image/png;base64," + Convert.ToBase64String(Png));
        body.GetProperty("audio_url").GetString().Should().Be("data:audio/wav;base64," + Convert.ToBase64String(Wav));
        body.GetProperty("resolution").GetString().Should().Be("768P");
        OnlyPost(api).RequestUri!.AbsolutePath.Should().Be("/" + H3LipSync);
    }

    [Fact]
    public async Task Kling_avatar_sends_the_motion_prompt_and_no_resolution()
    {
        StubHandler api = Queue();

        await VideoClient(api).SubmitAsync(
            TalkingHead(KlingAvatar, prompt: "smiles and nods", resolution: FalVideoCatalog.NativeToken), CancellationToken.None);

        JsonElement body = PostBody(api);
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["image_url", "audio_url", "prompt"]);
        body.GetProperty("prompt").GetString().Should().Be("smiles and nods");
    }

    [Fact]
    public async Task An_avatar_request_asks_the_vendor_not_to_keep_the_inputs_and_an_ordinary_one_does_not()
    {
        StubHandler avatar = Queue();
        await VideoClient(avatar).SubmitAsync(TalkingHead(), CancellationToken.None);
        OnlyPost(avatar).Headers.GetValues("X-Fal-Store-IO").Should().ContainSingle().Which.Should().Be("0");

        StubHandler ordinary = Queue();
        await VideoClient(ordinary).SubmitAsync(
            new VideoGenerationRequest("fal-ai/vidu/q4/image-to-video", "a slow pan", 5, "720p", FalVideoCatalog.NativeToken,
                [new VideoGenerationImageInput("image/png", Png)]),
            CancellationToken.None);
        OnlyPost(ordinary).Headers.Contains("X-Fal-Store-IO").Should().BeFalse("an ordinary request is byte-identical");
        ordinary.Requests.Where(r => r.Method == HttpMethod.Get).Should().AllSatisfy(r =>
            r.Headers.Contains("X-Fal-Store-IO").Should().BeFalse());
    }

    public static TheoryData<string, VideoGenerationRequest> Refused => new()
    {
        { "no speech track", TalkingHead(references: []) },
        { "two speech tracks", TalkingHead(references: [Speech(), Speech()]) },
        { "a picture reference", TalkingHead(references: [new VideoGenerationReference(AvatarReferenceKind.Image, AvatarReferenceRole.Face, "image/png", Png)]) },
        { "speech under the 5 s floor", TalkingHead(references: [Speech(4_900)]) },
        { "speech over the 15 s ceiling", TalkingHead(duration: 15, references: [Speech(15_001)]) },
        { "speech of unknown length", TalkingHead(references: [new VideoGenerationReference(AvatarReferenceKind.Audio, AvatarReferenceRole.Speech, "audio/wav", Wav)]) },
        { "speech longer than the priced take", TalkingHead(duration: 7, references: [Speech(7_400)]) },
        { "not audio", TalkingHead(references: [Speech(type: "video/mp4")]) },
        { "no still", TalkingHead(still: false) },
        { "a ratio no still is made at", TalkingHead(ratio: "21:9") },
        { "a prompt the route has no field for", TalkingHead(prompt: "wave") },
        { "a resolution the route does not list", TalkingHead(resolution: "720p") },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_talking_head_request_the_route_cannot_honour_is_refused_before_anything_is_sent(
        string why, VideoGenerationRequest request)
    {
        StubHandler api = Queue();

        Func<Task> act = () => VideoClient(api).SubmitAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>(why);
        api.Requests.Should().BeEmpty(why);
    }

    [Fact]
    public async Task Every_other_video_route_still_refuses_a_persons_reference_material()
    {
        StubHandler api = Queue();

        Func<Task> act = () => VideoClient(api).SubmitAsync(
            new VideoGenerationRequest("fal-ai/vidu/q4/image-to-video", "a slow pan", 5, "720p", FalVideoCatalog.NativeToken,
                [new VideoGenerationImageInput("image/png", Png)], References: [Speech()]),
            CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ prices

    [Theory]
    [InlineData(H3LipSync, "768P", 0.08)]
    [InlineData(KlingAvatar, FalVideoCatalog.NativeToken, 0.0562)]
    public void The_price_card_is_the_published_figure_at_the_routes_default_tier(string id, string tier, double usd)
    {
        FalAiVideoGenerationClient.Catalog.Find(id)!.PricePerSecondByResolution[tier].Should().Be((decimal)usd);
        ModelPriceCatalog.Priced.Single(m => m.ProviderKind == InferenceProviderKind.FalAi && m.Model == id)
            .UsdPerUnit[UsageKind.VideoGenSeconds].Should().Be((decimal)usd);
        VideoClient(Queue()).Describe(id).MaxDurationSeconds.Should().Be(15, "above 15 s H3 adds a multiplier the card cannot express");
    }

    // ================================================================ the template

    [Fact]
    public void The_budget_template_writes_speaks_in_the_avatars_voice_and_animates_a_still_with_the_permission_off()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll()
            .Single(t => t.Key == WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey);
        template.Name.Should().Be("Talking avatar (budget)");
        template.AutoCreateOnProject.Should().BeFalse();
        template.Steps.Select(s => s.StepType).Should().Equal(StepType.Agent, StepType.Voiceover, StepType.VideoGenerate);
        template.Steps[0].AgentType.Should().Be(AgentType.ScriptwriterAgent);

        VoiceoverStepConfig voice = VoiceoverStepConfig.Parse(template.Steps[1].VoiceoverConfigJson!)!;
        voice.VoiceFromAvatar.Should().BeTrue();
        voice.Source.Kind.Should().Be(VoiceoverSourceKind.ScriptScenes);

        VideoGenerateStepConfig clip = JsonSerializer.Deserialize<VideoGenerateStepConfig>(
            template.Steps[2].VideoGenerateConfigJson!, VideoGenerateStepConfig.JsonOptions)!;
        clip.AllowSourceMediaEgress.Should().BeFalse("only the customer turns it on, in the app");
        clip.AvatarId.Should().BeNull("the avatar comes from the run brief");
        clip.AvatarAudio.Should().Be(new ExtractInputRef(ExtractInputSource.Step, 2));
        clip.Model.Should().Be(H3LipSync);
        clip.Validate().Should().BeEmpty();
        clip.Validate(VideoClient(Queue()).Describe(clip.Model)).Should().BeEmpty();
        VideoClient(Queue()).ReferenceInputSupport(clip.Model).AnimatesStill.Should().BeTrue();
        // 15 s at 768P is $1.20; the cap must leave room for one full take.
        clip.MaxSpendCredits.Should().BeGreaterThanOrEqualTo(CreditCurrency.CreditsFromUsd(15 * 0.08m));
    }

    // ================================================================ the still's edit

    [Fact]
    public async Task An_edit_of_a_persons_pictures_asks_the_vendor_not_to_keep_them_and_an_ordinary_edit_does_not()
    {
        static (FalAiImageClient Client, StubHandler Api) ImageClient()
        {
            StubHandler api = Queue();
            StubHandler media = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
            return (new FalAiImageClient(new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "fal-key",
                new HttpClient(media), pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask), api);
        }

        (FalAiImageClient avatarClient, StubHandler avatarApi) = ImageClient();
        await avatarClient.EditAsync(
            new ImageEditRequest("google/nano-banana-2.1/edit", "a still", "data:image/png;base64,AA==",
                AspectRatio: "9:16", ReferenceImageUrls: ["data:image/png;base64,AQ=="])
            { NoVendorRetention = true },
            CancellationToken.None);
        OnlyPost(avatarApi).Headers.GetValues("X-Fal-Store-IO").Should().ContainSingle().Which.Should().Be("0");

        (FalAiImageClient plainClient, StubHandler plainApi) = ImageClient();
        await plainClient.EditAsync(
            new ImageEditRequest("google/nano-banana-2.1/edit", "make it night", "https://s/a.png"), CancellationToken.None);
        OnlyPost(plainApi).Headers.Contains("X-Fal-Store-IO").Should().BeFalse();
    }
}
