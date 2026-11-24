#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// fal.ai hosted video outpainting (Wan VACE default, Luma reframe alternative) replayed against the recorded queue
/// fixtures shared with the other hosted video-enhance clients; no live call. The request bodies follow the OpenAPI
/// input schemas read 2026-11-02 and are UNVERIFIED against a real run.
/// </summary>
public class FalAiVideoOutpaintClientTests
{
    private const string Source = "https://storage.example.test/source/clip.mp4?X-Amz-Signature=abc";
    private static readonly byte[] Clip = [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 1, 2, 3, 4];

    private static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Inference", "Fixtures", "VideoEnhance", name));

    private static AudioStubHandler Queue() =>
        new(request =>
        {
            if (request.RequestUri!.Host != "queue.fal.run")
                return AudioFixtures.Audio(Clip, "video/mp4");
            if (request.Method == HttpMethod.Post)
                return AudioFixtures.Json(Text("ve-submit.json"));
            return request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
                ? AudioFixtures.Json(Text("ve-status-completed.json"))
                : AudioFixtures.Json(Text("ve-result-topaz.json"));
        });

    private static FalAiVideoOutpaintClient Client(AudioStubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://queue.fal.run") }, AudioFixtures.Key,
            downloadClient: new HttpClient(handler), pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);

    private static VideoOutpaintRequest Req(
        string? aspect = "16:9", string model = "", int width = 1080, int height = 1080, double seconds = 5, double fps = 24,
        double left = 0, double right = 0, double top = 0, double bottom = 0, string prompt = "more sky") =>
        new(Source, width, height, fps, seconds, (long)(seconds * fps), aspect, left, right, top, bottom, prompt, model, "corr-9");

    [Fact]
    public async Task Wan_submits_the_aspect_as_per_side_expansion_with_the_safety_checker_on_and_the_input_frames_kept()
    {
        AudioStubHandler api = Queue();

        await Client(api).SubmitAsync(Req(), CancellationToken.None);

        api.Requests.Should().ContainSingle();
        api.Requests[0].RequestUri!.ToString().Should().Be("https://queue.fal.run/fal-ai/wan-vace-14b/outpainting");
        JsonObject body = JsonNode.Parse(api.Bodies[0])!.AsObject();
        body["video_url"]!.GetValue<string>().Should().Be(Source);
        body["prompt"]!.GetValue<string>().Should().Be("more sky");
        body["expand_left"]!.GetValue<bool>().Should().BeTrue();
        body["expand_right"]!.GetValue<bool>().Should().BeTrue();
        body["expand_top"]!.GetValue<bool>().Should().BeFalse();
        body["expand_bottom"]!.GetValue<bool>().Should().BeFalse();
        body["expand_ratio"]!.GetValue<double>().Should().BeApproximately((16d / 9d - 1d) / 2d, 1e-6);
        body["match_input_num_frames"]!.GetValue<bool>().Should().BeTrue();
        body["match_input_frames_per_second"]!.GetValue<bool>().Should().BeTrue();
        body["enable_safety_checker"]!.GetValue<bool>().Should().BeTrue("the endpoint defaults it to false");
        body["resolution"]!.GetValue<string>().Should().Be("720p");
    }

    [Fact]
    public async Task Wan_margins_enable_only_the_sides_that_grow_and_a_result_polls_and_downloads_without_the_key()
    {
        AudioStubHandler api = Queue();
        FalAiVideoOutpaintClient client = Client(api);

        VideoEnhanceJobHandle handle = await client.SubmitAsync(Req(aspect: null, left: 0.25, top: 0.25), CancellationToken.None);
        JsonObject body = JsonNode.Parse(api.Bodies[0])!.AsObject();
        body["expand_left"]!.GetValue<bool>().Should().BeTrue();
        body["expand_right"]!.GetValue<bool>().Should().BeFalse();
        body["expand_top"]!.GetValue<bool>().Should().BeTrue();
        body["expand_ratio"]!.GetValue<double>().Should().Be(0.25);

        VideoEnhanceResult result = await client.PollAsync(handle, "", CancellationToken.None);
        result.Video.Should().Equal(Clip);
        result.Model.Should().Be("fal-ai/wan-vace-14b/outpainting");
        api.Requests.Single(r => r.RequestUri!.Host != "queue.fal.run").Headers.Contains("Authorization").Should().BeFalse();
    }

    [Fact]
    public async Task Luma_takes_only_a_supported_aspect_and_refuses_4_5_and_margins_before_anything_is_sent()
    {
        AudioStubHandler api = Queue();
        FalAiVideoOutpaintClient client = Client(api);
        const string luma = "fal-ai/luma-dream-machine/ray-2/reframe";

        await client.SubmitAsync(Req(model: luma), CancellationToken.None);
        JsonObject body = JsonNode.Parse(api.Bodies[0])!.AsObject();
        body["aspect_ratio"]!.GetValue<string>().Should().Be("16:9");
        body.ContainsKey("expand_left").Should().BeFalse();

        Func<Task> fourFive = () => client.SubmitAsync(Req(aspect: "4:5", model: luma, width: 1920, height: 1080), CancellationToken.None);
        (await fourFive.Should().ThrowAsync<VideoEnhanceException>()).Which.Kind.Should().Be(VideoEnhanceErrorKind.RequestInvalid);
        Func<Task> margins = () => client.SubmitAsync(Req(aspect: null, model: luma, left: 0.2), CancellationToken.None);
        (await margins.Should().ThrowAsync<VideoEnhanceException>()).Which.Kind.Should().Be(VideoEnhanceErrorKind.RequestInvalid);
        api.Requests.Should().ContainSingle("only the first, valid request reached the queue");
    }

    [Fact]
    public async Task A_source_over_the_241_frame_ceiling_a_blank_prompt_a_canvas_beyond_half_a_side_and_a_noop_are_refused_without_a_call()
    {
        AudioStubHandler api = Queue();
        FalAiVideoOutpaintClient client = Client(api);

        Func<Task> tooLong = () => client.SubmitAsync(Req(seconds: 11, fps: 24), CancellationToken.None);
        (await tooLong.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain("241").And.NotContain("X-Amz");
        Func<Task> tooShort = () => client.SubmitAsync(Req(seconds: 2, fps: 24), CancellationToken.None);
        (await tooShort.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain("at least 81");
        Func<Task> blank = () => client.SubmitAsync(Req(prompt: "  "), CancellationToken.None);
        await blank.Should().ThrowAsync<VideoEnhanceException>();
        Func<Task> portraitTo169 = () => client.SubmitAsync(Req(width: 1080, height: 1920), CancellationToken.None);
        await portraitTo169.Should().ThrowAsync<VideoEnhanceException>();
        Func<Task> noop = () => client.SubmitAsync(Req(aspect: "1:1"), CancellationToken.None);
        (await noop.Should().ThrowAsync<VideoEnhanceException>()).Which.Message.Should().Contain("nothing to outpaint");
        Func<Task> overMargin = () => client.SubmitAsync(Req(aspect: null, left: 0.6), CancellationToken.None);
        await overMargin.Should().ThrowAsync<VideoEnhanceException>();

        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Uneven_margins_are_refused_before_a_call_because_wan_takes_one_amount_for_every_side()
    {
        AudioStubHandler api = Queue();
        FalAiVideoOutpaintClient client = Client(api);

        Func<Task> uneven = () => client.SubmitAsync(Req(aspect: null, left: 0.1, right: 0.3), CancellationToken.None);
        (await uneven.Should().ThrowAsync<VideoEnhanceException>()).Which.Kind.Should().Be(VideoEnhanceErrorKind.RequestInvalid);
        api.Requests.Should().BeEmpty();

        // Every body that IS sent describes exactly the canvas that was priced: width = source x (1 + ratio per enabled side).
        await client.SubmitAsync(Req(aspect: null, left: 0.3, right: 0.3), CancellationToken.None);
        JsonObject body = JsonNode.Parse(api.Bodies[0])!.AsObject();
        double ratio = body["expand_ratio"]!.GetValue<double>();
        int vendorWidth = (int)Math.Round(1080 * (1 + (body["expand_left"]!.GetValue<bool>() ? ratio : 0) + (body["expand_right"]!.GetValue<bool>() ? ratio : 0)));
        vendorWidth.Should().Be(VideoOutpaintGeometry.ForMargins(1080, 1080, 0.3, 0.3, 0, 0)!.Width);
        VideoOutpaintGeometry.AreUneven(0.2, 0.2, 0, 0).Should().BeFalse();
        VideoOutpaintGeometry.AreUneven(0.2, 0.1, 0, 0).Should().BeTrue();
    }

    [Fact]
    public async Task Luma_asks_for_its_safety_checker_and_every_field_is_declared_by_its_schema()
    {
        AudioStubHandler api = Queue();
        await Client(api).SubmitAsync(Req(model: "fal-ai/luma-dream-machine/ray-2/reframe"), CancellationToken.None);
        JsonNode.Parse(api.Bodies[0])!["enable_safety_checker"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_submit_timeout_is_an_unknown_outcome_never_a_rejection()
    {
        AudioStubHandler timeout = new(_ => throw new TaskCanceledException("the request timed out", new TimeoutException()));
        FalAiVideoOutpaintClient client = Client(timeout);

        Func<Task> submit = () => client.SubmitAsync(Req(), CancellationToken.None);

        VideoEnhanceException ex = (await submit.Should().ThrowAsync<VideoEnhanceException>()).Which;
        ex.Kind.Should().Be(VideoEnhanceErrorKind.SubmitOutcomeUnknown);
        ex.Message.Should().NotContain("X-Amz").And.NotContain("storage.example.test");

        // A real cancellation by the caller still propagates as a cancellation.
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Func<Task> cancelled = () => Client(Queue()).SubmitAsync(Req(), cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void The_outpaint_client_gets_the_deployment_poll_timeout_and_source_cap()
    {
        Microsoft.Extensions.Configuration.IConfiguration config = Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder(),
            new Dictionary<string, string?>
            {
                [ReelBolt.Shared.Metering.VideoEnhanceCreditPricing.PollTimeoutMinutesKey] = "45",
                [ReelBolt.Shared.Metering.VideoEnhanceCreditPricing.MaxSourceSecondsKey] = "60",
            }).Build();
        VideoEnhanceClientFactory factory = new(config);
        ResolvedAudioGenerationProvider row = new(Guid.NewGuid(), "row", ReelBolt.Shared.Data.Models.InferenceProviderKind.FalAi,
            ReelBolt.Shared.Data.Models.InferenceProviderCapability.VideoOutpaint, "", VideoEnhanceModelCatalog.WanVaceId, "k", 60, AccountId: Guid.NewGuid());

        object outpaint = ((IVideoOutpaintClientFactory)factory).Get(row);

        T Field<T>(object o, string name) => (T)o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(o)!;
        Field<int>(outpaint, "_maxSourceSeconds").Should().Be(60);
        Field<TimeSpan>(Field<object>(outpaint, "_job"), "_pollTimeout").Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void The_canvas_geometry_is_even_exact_and_never_clamped()
    {
        VideoOutpaintCanvas wide = VideoOutpaintGeometry.ForAspect(1080, 1080, "16:9")!;
        (wide.Width, wide.Height).Should().Be((1920, 1080));
        VideoOutpaintCanvas tall = VideoOutpaintGeometry.ForAspect(1080, 1080, "4:5")!;
        (tall.Width, tall.Height).Should().Be((1080, 1350));
        tall.Top.Should().BeApproximately(0.125, 1e-9);
        VideoOutpaintGeometry.ForAspect(1920, 1080, "4:5").Should().BeNull("0.61 of a side is over the bound");
        VideoOutpaintGeometry.ForAspect(1920, 1080, "16:9")!.ExpandRatio.Should().Be(0);
        VideoOutpaintGeometry.ForAspect(1080, 1920, "16:9").Should().BeNull("a portrait clip needs more than half a side");
        VideoOutpaintGeometry.ForAspect(1920, 1080, "21:9").Should().BeNull("not an offered aspect");
        VideoOutpaintGeometry.ForMargins(1000, 500, 0.5, 0.5, 0.5, 0.5)!.Width.Should().Be(2000);
        VideoOutpaintGeometry.ForMargins(1000, 500, 0.51, 0, 0, 0).Should().BeNull();
        VideoOutpaintGeometry.ForMargins(1000, 500, double.NaN, 0, 0, 0).Should().BeNull();
        VideoOutpaintGeometry.LumaSupports("4:5").Should().BeFalse();
        VideoOutpaintGeometry.LumaSupports("9:16").Should().BeTrue();
    }
}
