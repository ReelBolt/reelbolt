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
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Avatars phase 2, lane B1: the per-model reference declaration (<see cref="IVideoReferenceInputSupport"/>), the
/// selection rule, the input-material price, and MiniMax's reference mode on the wire. No live call: the HTTP
/// handler is a stub that captures the body.
/// </summary>
public class VideoReferenceInputTests
{
    [Theory]
    [InlineData("MiniMax-H3")]
    [InlineData("MiniMax-H3-Max")]
    public void MiniMax_declares_the_documented_reference_envelope(string model)
    {
        VideoReferenceInputSupport support = Client(out _).ReferenceInputSupport(model);

        support.MaxImages.Should().Be(9);
        support.MaxVideos.Should().Be(3);
        support.MaxAudio.Should().Be(3);
        support.MaxFiles.Should().Be(12);
        support.MaxVideoTotalMs.Should().Be(15_000);
        support.MaxAudioTotalMs.Should().Be(15_000);
        support.ExclusiveWithFrames.Should().BeTrue();
        // Must-measure #2: not known to drive speech, so a driving track is refused until measured.
        support.AudioDrivesSpeech.Should().BeFalse();
    }

    [Fact]
    public void An_unknown_model_throws_like_Describe()
    {
        Action act = () => Client(out _).ReferenceInputSupport("nope");
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void A_client_that_declares_nothing_declares_None()
    {
        IVideoGenerationClient silent = new SilentClient();
        silent.DeclaredReferenceInputSupport("x").Should().Be(VideoReferenceInputSupport.None);
        VideoReferenceInput.Refusal(VideoReferenceInputSupport.None, "x", false, false).Should().Contain("cannot show an avatar");
    }

    [Fact]
    public void Refusal_names_frames_and_driving_audio()
    {
        VideoReferenceInputSupport s = MiniMaxVideoGenerationClient.ReferenceSupport;
        VideoReferenceInput.Refusal(s, "MiniMax-H3", hasFrames: false, wantsDrivingAudio: false).Should().BeNull();
        VideoReferenceInput.Refusal(s, "MiniMax-H3", hasFrames: true, wantsDrivingAudio: false).Should().Contain("first or last frame");
        VideoReferenceInput.Refusal(s, "MiniMax-H3", hasFrames: false, wantsDrivingAudio: true).Should().Contain("driving audio");
    }

    [Fact]
    public void Select_takes_pictures_first_then_audio_then_video_inside_every_limit()
    {
        var support = new VideoReferenceInputSupport(2, 1, 2, 5, 10_000, 15_000, false, true);
        var candidates = new List<VideoGenerationReference>
        {
            Video(8_000),                        // 0 chosen (video)
            Image(), Image(), Image(),          // 1,2 chosen; 3 kind_limit
            Audio(5_000),                        // 4 chosen
            Video(3_000),                        // 5 kind_limit (one video)
            Audio(null),                         // 6 unknown length
        };

        VideoReferenceSelection selection = VideoReferenceInput.Select(support, candidates, 64_000_000);

        selection.Chosen.Should().Equal(1, 2, 4, 0);
        selection.Skipped.Should().Equal((3, VideoReferenceInput.SkipKindLimit), (5, VideoReferenceInput.SkipKindLimit), (6, VideoReferenceInput.SkipUnknownLength));
    }

    [Fact]
    public void Select_respects_the_total_length_the_file_limit_and_the_body_ceiling()
    {
        var support = new VideoReferenceInputSupport(9, 3, 3, 3, 10_000, 15_000, false, true);
        var candidates = new List<VideoGenerationReference> { Image(), Video(6_000), Video(6_000), Image(), Image() };

        VideoReferenceSelection selection = VideoReferenceInput.Select(support, candidates, 64_000_000);
        selection.Chosen.Should().Equal(0, 3, 4);
        selection.Skipped.Should().Contain((1, VideoReferenceInput.SkipFileLimit));

        var roomy = support with { MaxFiles = 12 };
        VideoReferenceSelection lengths = VideoReferenceInput.Select(roomy, candidates, 64_000_000);
        lengths.Skipped.Should().Equal((2, VideoReferenceInput.SkipTotalLength));

        var big = new List<VideoGenerationReference> { Image(), new(AvatarReferenceKind.Video, AvatarReferenceRole.Motion, "video/mp4", new byte[3_000_000], 5_000) };
        VideoReferenceSelection body = VideoReferenceInput.Select(roomy, big, maxBodyBytes: 1_000_000);
        body.Chosen.Should().Equal(0);
        body.Skipped.Should().Equal((1, VideoReferenceInput.SkipBodySize));
    }

    [Fact]
    public void Input_material_follows_the_published_pricing_page()
    {
        var five = Enumerable.Repeat(new VideoReferenceMaterial(AvatarReferenceKind.Image, null), 5).ToList();
        var seven = Enumerable.Repeat(new VideoReferenceMaterial(AvatarReferenceKind.Image, null), 7).ToList();
        MiniMaxVideoGenerationClient client = Client(out _);

        client.InputMaterialCostUsd("MiniMax-H3", "768P", five).Should().Be(0m);
        client.InputMaterialCostUsd("MiniMax-H3", "768P", seven).Should().Be(0.08m);                    // 2 x $0.04
        client.InputMaterialCostUsd("MiniMax-H3-Max", "768P", five).Should().Be(3 * 0.074m);           // first 2 free on Max
        client.InputMaterialCostUsd("MiniMax-H3", "768P",
            [new(AvatarReferenceKind.Video, 6_500), new(AvatarReferenceKind.Audio, 9_000)]).Should().Be(7 * 0.08m); // 7 s at the output rate, audio free
        client.InputMaterialCostUsd("MiniMax-H3", "2K", [new(AvatarReferenceKind.Video, 4_000)]).Should().Be(4 * 0.13m);
        client.InputMaterialCostUsd("MiniMax-H3-Max", "480P", [new(AvatarReferenceKind.Video, 2_000)]).Should().Be(2 * 0.0553m);
        MiniMaxVideoGenerationClient.MaxInputMaterialCostUsd("MiniMax-H3", "768P").Should().Be(3 * 0.04m + 15 * 0.08m);
    }

    private static readonly byte[] WavBytes = [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WAVEfmt "u8, .. new byte[40]];
    private static readonly byte[] Mp3WithTag = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0, .. new byte[40]];
    private static readonly byte[] Mp3Frame = [0xFF, 0xFB, 0x90, 0x00, .. new byte[40]];

    [Theory]
    // Declared label vs. bytes: the label sent is the one the BYTES earn, never audio/mpeg (read by the vendor as ".mpeg").
    [InlineData("audio/mpeg", "mp3tag", "data:audio/mp3;base64,")]
    [InlineData("audio/mpeg", "mp3frame", "data:audio/mp3;base64,")]
    [InlineData("audio/mp3", "mp3tag", "data:audio/mp3;base64,")]
    [InlineData("audio/wav", "wav", "data:audio/wav;base64,")]
    [InlineData("audio/x-wav", "wav", "data:audio/wav;base64,")]
    [InlineData("audio/wave", "wav", "data:audio/wav;base64,")]
    [InlineData("audio/mpeg", "wav", "data:audio/wav;base64,")]
    [InlineData("audio/wav", "mp3tag", "data:audio/mp3;base64,")]
    public async Task A_reference_audio_goes_out_under_a_label_the_vendor_lists(string contentType, string bytesKind, string expectedPrefix)
    {
        byte[] bytes = bytesKind switch { "wav" => WavBytes, "mp3tag" => Mp3WithTag, _ => Mp3Frame };
        MiniMaxVideoGenerationClient client = Client(out Func<string> body);
        var audio = new VideoGenerationReference(AvatarReferenceKind.Audio, AvatarReferenceRole.Speech, contentType, bytes, 4_000);
        var request = new VideoGenerationRequest("MiniMax-H3", "Anna waves", 6, "768P", "9:16", References: [Image(), audio]);

        await client.SubmitAsync(request, CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(body());
        doc.RootElement.GetProperty("content")[2].GetProperty("audio_url").GetProperty("url").GetString().Should().StartWith(expectedPrefix);
    }

    [Theory]
    [InlineData("audio/mpeg")]
    [InlineData("audio/wav")]
    [InlineData("audio/x-wav")]
    public async Task A_recording_that_is_neither_wav_nor_mp3_by_its_bytes_is_refused_before_any_request(string declared)
    {
        byte[] m4a = [0, 0, 0, 0x20, .. "ftypM4A "u8, .. new byte[40]];
        byte[] ogg = [.. "OggS"u8, .. new byte[40]];
        byte[] flac = [.. "fLaC"u8, .. new byte[40]];
        byte[] webm = [0x1A, 0x45, 0xDF, 0xA3, .. new byte[40]];
        foreach (byte[] bytes in new[] { m4a, ogg, flac, webm })
        {
            MiniMaxVideoGenerationClient client = Client(out Func<string> body);
            var audio = new VideoGenerationReference(AvatarReferenceKind.Audio, AvatarReferenceRole.Speech, declared, bytes, 4_000);
            var request = new VideoGenerationRequest("MiniMax-H3", "Anna waves", 6, "768P", "9:16", References: [Image(), audio]);

            Func<Task> act = () => client.SubmitAsync(request, CancellationToken.None);

            (await act.Should().ThrowAsync<ReferenceAudioFormatException>()).Which.Message.Should().Contain("WAV or MP3");
        }
    }

    [Fact]
    public void Sniff_names_wav_and_mp3_and_nothing_else()
    {
        ReferenceAudioFormat.Sniff(WavBytes).Should().Be("audio/wav");
        ReferenceAudioFormat.Sniff(Mp3WithTag).Should().Be("audio/mp3");
        ReferenceAudioFormat.Sniff(Mp3Frame).Should().Be("audio/mp3");
        ReferenceAudioFormat.Sniff([0xFF, 0xF1, 0x50, 0x80]).Should().BeNull("an ADTS/AAC frame has layer bits 00");
        ReferenceAudioFormat.Sniff([]).Should().BeNull();
        ReferenceAudioFormat.Sniff(new byte[64]).Should().BeNull();
    }

    [Fact]
    public async Task Reference_mode_sends_typed_reference_items_and_the_ratio_and_never_a_frame()
    {
        MiniMaxVideoGenerationClient client = Client(out Func<string> body);
        var request = new VideoGenerationRequest("MiniMax-H3", "Anna waves", 6, "768P", "9:16",
            References: [Image(), Audio(4_000), Video(5_000)]);

        await client.SubmitAsync(request, CancellationToken.None);

        using JsonDocument doc = JsonDocument.Parse(body());
        JsonElement content = doc.RootElement.GetProperty("content");
        content.GetArrayLength().Should().Be(4);
        content[1].GetProperty("type").GetString().Should().Be("image_url");
        content[1].GetProperty("role").GetString().Should().Be("reference_image");
        content[1].GetProperty("image_url").GetProperty("url").GetString().Should().StartWith("data:image/png;base64,");
        content[2].GetProperty("type").GetString().Should().Be("audio_url");
        content[2].GetProperty("role").GetString().Should().Be("reference_audio");
        content[2].GetProperty("audio_url").GetProperty("url").GetString().Should().StartWith("data:audio/wav;base64,");
        content[3].GetProperty("type").GetString().Should().Be("video_url");
        content[3].GetProperty("role").GetString().Should().Be("reference_video");
        content[3].GetProperty("video_url").GetProperty("url").GetString().Should().StartWith("data:video/mp4;base64,");
        doc.RootElement.GetProperty("ratio").GetString().Should().Be("9:16");
    }

    [Fact]
    public async Task A_request_without_references_is_byte_identical()
    {
        MiniMaxVideoGenerationClient client = Client(out Func<string> body);
        await client.SubmitAsync(new VideoGenerationRequest("MiniMax-H3", "x", 6, "768P", "16:9"), CancellationToken.None);
        body().Should().Be("{\"model\":\"MiniMax-H3\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}],\"duration\":6,\"resolution\":\"768P\",\"ratio\":\"16:9\"}");
    }

    [Fact]
    public async Task References_with_a_frame_are_refused_before_anything_is_sent()
    {
        bool sent = false;
        MiniMaxVideoGenerationClient client = Client(out _, () => sent = true);
        var request = new VideoGenerationRequest("MiniMax-H3", "x", 6, "768P", "16:9",
            Images: [new VideoGenerationImageInput("image/png", PngBytes(1024, 1024))],
            References: [Image()]);

        Func<Task> act = () => client.SubmitAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*first or last frame*");
        sent.Should().BeFalse();
    }

    [Theory]
    [InlineData(1_500)]
    [InlineData(15_001)]
    [InlineData(null)]
    public async Task A_reference_video_outside_2_to_15_seconds_is_refused(int? ms)
    {
        MiniMaxVideoGenerationClient client = Client(out _);
        var request = new VideoGenerationRequest("MiniMax-H3", "x", 6, "768P", "16:9", References: [Image(), Video(ms)]);
        Func<Task> act = () => client.SubmitAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Too_many_reference_pictures_are_refused()
    {
        MiniMaxVideoGenerationClient client = Client(out _);
        var request = new VideoGenerationRequest("MiniMax-H3", "x", 6, "768P", "16:9",
            References: [.. Enumerable.Range(0, 10).Select(_ => Image())]);
        Func<Task> act = () => client.SubmitAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*at most 9*");
    }

    [Fact]
    public async Task A_client_without_reference_mode_refuses_references_instead_of_dropping_them()
    {
        var comfy = new ComfyUiVideoGenerationClient(new HttpClient { BaseAddress = new Uri("http://comfy:8188") }, string.Empty);
        Func<Task> act = () => comfy.SubmitAsync(
            new VideoGenerationRequest("wan2.2-ti2v-5b-t2v", "x", 5, "720P", "16:9", References: [Image()]), CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static MiniMaxVideoGenerationClient Client(out Func<string> body, Action? onSend = null)
    {
        string captured = string.Empty;
        body = () => captured;
        var http = new HttpClient(new CapturingHandler(async request =>
        {
            onSend?.Invoke();
            captured = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"task_id":"t1"}""") };
        }))
        { BaseAddress = new Uri("https://api.minimax.io") };
        return new MiniMaxVideoGenerationClient(http, "key");
    }

    private static VideoGenerationReference Image() =>
        new(AvatarReferenceKind.Image, AvatarReferenceRole.Face, "image/png", PngBytes(1024, 1024));

    private static VideoGenerationReference Video(int? ms) =>
        new(AvatarReferenceKind.Video, AvatarReferenceRole.Motion, "video/mp4", new byte[64], ms);

    private static VideoGenerationReference Audio(int? ms) =>
        new(AvatarReferenceKind.Audio, AvatarReferenceRole.Speech, "audio/wav", WavBytes, ms);

    private static byte[] PngBytes(int width, int height)
    {
        byte[] b = new byte[32];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        sig.CopyTo(b, 0);
        b[16] = (byte)(width >> 24); b[17] = (byte)(width >> 16); b[18] = (byte)(width >> 8); b[19] = (byte)width;
        b[20] = (byte)(height >> 24); b[21] = (byte)(height >> 16); b[22] = (byte)(height >> 8); b[23] = (byte)height;
        return b;
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class SilentClient : IVideoGenerationClient
    {
        public Task<ExternalJobHandle> SubmitAsync(VideoGenerationRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<ExternalJobStatus> PollAsync(ExternalJobHandle handle, CancellationToken ct) => throw new NotImplementedException();
        public Task<VideoGenerationProbeResult> ProbeAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<System.IO.Stream> DownloadAsync(string downloadUrl, CancellationToken ct) => throw new NotImplementedException();
        public VideoGenerationCapabilities Describe(string model) => throw new NotImplementedException();
    }
}
