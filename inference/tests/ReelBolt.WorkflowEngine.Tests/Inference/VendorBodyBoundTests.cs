#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// S-52: a hostile BYO endpoint answers error and probe requests with a huge body. Every vendor client must stop
/// reading at a fixed cap (64 KiB for error/probe bodies) instead of buffering the whole body into engine memory.
/// </summary>
public class VendorBodyBoundTests
{
    /// <summary>Far over every cap; the test fails if a client pulls more than <see cref="Allowed"/> of it.</summary>
    private const long HugeBody = 32L * 1024 * 1024;

    /// <summary>The 64 KiB cap plus one socket-sized read of slack.</summary>
    private const long Allowed = 64 * 1024 + 81920;

    /// <summary>A body of <see cref="HugeBody"/> bytes of 'x' that counts how much of it anything read.</summary>
    private sealed class CountingStream : Stream
    {
        public long Served { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, HugeBody - Served);
            Array.Fill(buffer, (byte)'x', offset, n);
            Served += n;
            return n;
        }
    }

    private sealed class HugeBodyHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<CountingStream> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CountingStream body = new();
            Bodies.Add(body);
            var content = new StreamContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private static void NothingReadPastTheCap(HugeBodyHandler handler)
    {
        handler.Bodies.Should().NotBeEmpty();
        foreach (CountingStream body in handler.Bodies) body.Served.Should().BeLessThanOrEqualTo(Allowed);
    }

    [Fact]
    public async Task S52_elevenlabs_reads_at_most_the_cap_of_an_error_body()
    {
        HugeBodyHandler api = new(HttpStatusCode.InternalServerError);
        var client = new ElevenLabsAudioGenerationClient(new HttpClient(api) { BaseAddress = new Uri("https://api.elevenlabs.io") }, "test");

        Func<Task> act = () => client.GenerateAsync(
            new AudioGenerationRequest(AudioGenerationKind.Music, "calm piano", 30, Model: "music_v2_5", Instrumental: true),
            CancellationToken.None);

        await act.Should().ThrowAsync<AudioGenerationException>();
        NothingReadPastTheCap(api);
    }

    [Fact]
    public async Task S52_elevenlabs_reads_at_most_the_cap_of_a_probe_body()
    {
        HugeBodyHandler api = new(HttpStatusCode.BadGateway);
        var client = new ElevenLabsAudioGenerationClient(new HttpClient(api) { BaseAddress = new Uri("https://api.elevenlabs.io") }, "test");

        AudioGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        NothingReadPastTheCap(api);
    }

    [Fact]
    public async Task S52_fal_audio_reads_at_most_the_cap_of_a_probe_body()
    {
        HugeBodyHandler api = new(HttpStatusCode.BadGateway);
        var client = new FalAiAudioGenerationClient(new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "test");

        AudioGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        NothingReadPastTheCap(api);
    }

    [Fact]
    public async Task S52_the_fal_queue_reads_at_most_the_cap_of_a_submit_refusal()
    {
        HugeBodyHandler api = new(HttpStatusCode.InternalServerError);
        var queue = new FalQueueClient(new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "test", null, null, null, null);

        Func<Task> act = () => queue.SubmitAsync("fal-ai/elevenlabs/music", new Dictionary<string, object?> { ["prompt"] = "x" }, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        NothingReadPastTheCap(api);
    }

    [Fact]
    public async Task S52_the_fal_queue_reads_at_most_the_cap_of_a_probe_body()
    {
        HugeBodyHandler api = new(HttpStatusCode.BadGateway);
        var queue = new FalQueueClient(new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "test", null, null, null, null);

        FalProbeOutcome outcome = await queue.ProbeAsync("fal-ai/flux/dev", CancellationToken.None);

        outcome.IsOk.Should().BeFalse();
        NothingReadPastTheCap(api);
    }

    [Fact]
    public async Task S52_the_fal_queue_reads_at_most_the_cap_of_a_result_refusal()
    {
        HugeBodyHandler api = new(HttpStatusCode.InternalServerError);
        var queue = new FalQueueClient(new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "test", null, null, null, null);

        FalResultFetch fetch = await queue.TryFetchResultAsync("https://queue.fal.run/fal-ai/x/requests/r", "fal-ai/x", CancellationToken.None);

        fetch.StatusCode.Should().Be(500);
        NothingReadPastTheCap(api);
    }
}
