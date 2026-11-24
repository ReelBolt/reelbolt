using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The shared image-input rule, in isolation: what a model's declaration allows, and — the reason the
/// declaration exists — that a text-only model refuses BEFORE the caller reaches the budget gate,
/// because <see cref="VideoImageInput.EnsureAccepts"/> answers from the declaration and touches
/// nothing.
/// </summary>
public class VideoImageInputSupportTests
{
    [Theory]
    [InlineData(VideoImageInputSupport.None)]
    [InlineData(VideoImageInputSupport.FirstFrame)]
    [InlineData(VideoImageInputSupport.FirstAndLastFrame)]
    public void Validate_accepts_a_request_with_no_image_whatever_the_model_declares(VideoImageInputSupport support)
    {
        Action act = () => VideoImageInput.Validate(support, "some-model", 0);

        act.Should().NotThrow("every model is usable for a request that carries no image");
    }

    [Fact]
    public void Validate_refuses_any_image_on_a_text_only_model()
    {
        Action act = () => VideoImageInput.Validate(VideoImageInputSupport.None, "MiniMax-H3", 1);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*'MiniMax-H3' accepts no input image*")
            .WithMessage("*text-only*");
    }

    [Fact]
    public void Validate_refuses_a_last_frame_on_a_first_frame_only_model()
    {
        Action act = () => VideoImageInput.Validate(VideoImageInputSupport.FirstFrame, "wan2.2-ti2v-5b-i2v", 2);

        act.Should().Throw<NotSupportedException>().WithMessage("*first frame only*");
    }

    [Theory]
    [InlineData(VideoImageInputSupport.FirstFrame, 1)]
    [InlineData(VideoImageInputSupport.FirstAndLastFrame, 1)]
    [InlineData(VideoImageInputSupport.FirstAndLastFrame, 2)]
    public void Validate_accepts_what_the_model_declares(VideoImageInputSupport support, int imageCount)
    {
        Action act = () => VideoImageInput.Validate(support, "some-model", imageCount);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_refuses_more_images_than_a_first_and_a_last_frame()
    {
        Action act = () => VideoImageInput.Validate(VideoImageInputSupport.FirstAndLastFrame, "some-model", 3);

        act.Should().Throw<ArgumentException>().WithMessage("*at most 2*");
    }

    [Fact]
    public void Validate_refuses_a_negative_count()
    {
        Action act = () => VideoImageInput.Validate(VideoImageInputSupport.FirstAndLastFrame, "some-model", -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DeclaredImageInputSupport_is_None_for_a_client_that_declares_nothing()
    {
        IVideoGenerationClient client = new SilentClient();

        client.DeclaredImageInputSupport("whatever").Should().Be(
            VideoImageInputSupport.None,
            "an undeclared client must not be assumed to accept an image");
    }

    [Fact]
    public void DeclaredImageInputSupport_reads_the_clients_own_answer()
    {
        IVideoGenerationClient client = new DeclaringClient(VideoImageInputSupport.FirstFrame);

        client.DeclaredImageInputSupport("whatever").Should().Be(VideoImageInputSupport.FirstFrame);
    }

    [Fact]
    public void EnsureAccepts_refuses_a_text_only_model_without_asking_the_vendor_or_reserving_anything()
    {
        IVideoGenerationClient declaring = new DeclaringClient(VideoImageInputSupport.None);

        Action act = () => declaring.EnsureAccepts("some-model", 1);

        // NotSupportedException specifically, while every network member of the fake throws
        // InvalidOperationException: the refusal therefore happened without a vendor call.
        act.Should().Throw<NotSupportedException>().WithMessage("*accepts no input image*");
    }

    [Fact]
    public void EnsureAccepts_refuses_an_undeclared_client_rather_than_inventing_support()
    {
        IVideoGenerationClient silent = new SilentClient();

        Action act = () => silent.EnsureAccepts("whatever", 1);

        act.Should().Throw<NotSupportedException>("nothing declared is answered as no image support");
    }

    [Fact]
    public void EnsureAccepts_refuses_a_null_client()
    {
        Action act = () => VideoImageInput.EnsureAccepts(null!, "whatever", 1);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_extension_sees_the_declaration_through_the_frozen_interface()
    {
        // A real client, held as IVideoGenerationClient — the type a caller actually has.
        IVideoGenerationClient client = new ComfyUiVideoGenerationClient(
            new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://comfyui:8188") }, string.Empty);

        client.DeclaredImageInputSupport("wan2.2-ti2v-5b-t2v").Should().Be(VideoImageInputSupport.None);

        Action act = () => client.EnsureAccepts("wan2.2-ti2v-5b-i2v", 2);
        act.Should().Throw<NotSupportedException>();
    }

    /// <summary>A client that declares an image-input level.</summary>
    private sealed class DeclaringClient(VideoImageInputSupport support) : IVideoGenerationClient, IVideoImageInputSupport
    {
        public VideoImageInputSupport ImageInputSupport(string model) => support;

        public Task<ExternalJobHandle> SubmitAsync(VideoGenerationRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("no vendor call");

        public Task<ExternalJobStatus> PollAsync(ExternalJobHandle handle, CancellationToken ct) =>
            throw new InvalidOperationException("no vendor call");

        public Task<VideoGenerationProbeResult> ProbeAsync(CancellationToken ct) =>
            throw new InvalidOperationException("no vendor call");

        public Task<Stream> DownloadAsync(string downloadUrl, CancellationToken ct) =>
            throw new InvalidOperationException("no vendor call");

        public VideoGenerationCapabilities Describe(string model) =>
            throw new InvalidOperationException("no vendor call");
    }

    /// <summary>A client from before this declaration existed: it says nothing about images.</summary>
    private sealed class SilentClient : IVideoGenerationClient
    {
        public Task<ExternalJobHandle> SubmitAsync(VideoGenerationRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("no vendor call");

        public Task<ExternalJobStatus> PollAsync(ExternalJobHandle handle, CancellationToken ct) =>
            throw new InvalidOperationException("no vendor call");

        public Task<VideoGenerationProbeResult> ProbeAsync(CancellationToken ct) =>
            throw new InvalidOperationException("no vendor call");

        public Task<Stream> DownloadAsync(string downloadUrl, CancellationToken ct) =>
            throw new InvalidOperationException("no vendor call");

        public VideoGenerationCapabilities Describe(string model) =>
            throw new InvalidOperationException("no vendor call");
    }

    /// <summary>Fails a test that lets the client reach the network.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no request expected");
    }
}
