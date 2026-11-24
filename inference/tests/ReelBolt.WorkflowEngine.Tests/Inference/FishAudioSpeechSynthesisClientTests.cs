using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// Tests for <see cref="FishAudioSpeechSynthesisClient"/> that verify the correct wire
/// format for Fish Audio's POST /v1/tts endpoint, using a fake <see cref="HttpMessageHandler"/>
/// to intercept requests without hitting a real service.
/// </summary>
public class FishAudioSpeechSynthesisClientTests
{
    [Fact]
    public async Task SynthesizeAsync_SendsRequest_WithDefaultModelHeader()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler(null, GetSampleWavBytes(), HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        // Act
        var result = await client.SynthesizeAsync(request, CancellationToken.None);

        // Assert
        mockHandler.LastRequest!.Should().NotBeNull();
        mockHandler.LastRequest!.Headers.Should().ContainKey("model");
        var modelHeader = mockHandler.LastRequest!.Headers.GetValues("model").FirstOrDefault();
        modelHeader.Should().Be("s2.1-pro");
    }

    [Fact]
    public async Task SynthesizeAsync_SendsRequest_WithAuthorizationHeader()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler(null, GetSampleWavBytes(), HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        // The Authorization header is set on the injected HttpClient (as per ticket spec)
        httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer test-key");
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        // Act
        var result = await client.SynthesizeAsync(request, CancellationToken.None);

        // Assert
        mockHandler.LastRequest!.Headers.Authorization.Should().NotBeNull();
        mockHandler.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
    }

    [Fact]
    public async Task SynthesizeAsync_SendsRequest_WithCorrectJsonBody()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler(null, GetSampleWavBytes(), HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", "voice-123", null);

        // Act
        var result = await client.SynthesizeAsync(request, CancellationToken.None);

        // Assert
        mockHandler.LastRequest!.Should().NotBeNull();
        mockHandler.LastRequestBody.Should().NotBeNull();

        using var doc = JsonDocument.Parse(mockHandler.LastRequestBody);
        var root = doc.RootElement;

        // Verify body structure
        root.GetProperty("text").GetString().Should().Be("Hello world");
        root.GetProperty("reference_id").GetString().Should().Be("voice-123");
        root.GetProperty("format").GetString().Should().Be("wav");
        root.GetProperty("sample_rate").GetInt32().Should().Be(44100);
        root.GetProperty("latency").GetString().Should().Be("normal");

        var prosody = root.GetProperty("prosody");
        prosody.GetProperty("speed").GetDouble().Should().Be(1.0);
        prosody.GetProperty("volume").GetInt32().Should().Be(0);
        prosody.GetProperty("normalize_loudness").GetBoolean().Should().Be(true);
    }

    [Fact]
    public async Task SynthesizeAsync_OmitsReferenceId_WhenVoiceIdIsNull()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler(null, GetSampleWavBytes(), HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        // Act
        var result = await client.SynthesizeAsync(request, CancellationToken.None);

        // Assert
        mockHandler.LastRequestBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(mockHandler.LastRequestBody);
        var root = doc.RootElement;

        root.TryGetProperty("reference_id", out _).Should().BeFalse("reference_id should be omitted when VoiceId is null");
    }

    [Fact]
    public async Task SynthesizeAsync_UsesModelOverride_FromConstructor()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler(null, GetSampleWavBytes(), HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, "custom-model");

        var request = new SpeechRequest("Hello world", null, null);

        // Act
        var result = await client.SynthesizeAsync(request, CancellationToken.None);

        // Assert
        var modelHeader = mockHandler.LastRequest!.Headers.GetValues("model").FirstOrDefault();
        modelHeader.Should().Be("custom-model");
    }

    [Fact]
    public async Task SynthesizeAsync_UsesModelOverride_FromRequest()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler(null, GetSampleWavBytes(), HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, "constructor-model");

        var request = new SpeechRequest("Hello world", null, "request-model");

        // Act
        var result = await client.SynthesizeAsync(request, CancellationToken.None);

        // Assert
        var modelHeader = mockHandler.LastRequest!.Headers.GetValues("model").FirstOrDefault();
        modelHeader.Should().Be("request-model");
    }

    [Fact]
    public async Task SynthesizeAsync_Returns200_BuffersWavAndReturnsStream()
    {
        // Arrange
        var wavBytes = GetSampleWavBytes();
        var mockHandler = new MockHttpMessageHandler(null, wavBytes, HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        // Act
        var result = await client.SynthesizeAsync(request, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.ContentType.Should().Be("audio/wav");
        result.Wav.Should().NotBeNull();
        result.Wav.Position.Should().Be(0);
        result.Wav.CanRead.Should().BeTrue();

        // Verify content matches
        var readBytes = new byte[wavBytes.Length];
        var bytesRead = await result.Wav.ReadAsync(readBytes, 0, readBytes.Length);
        bytesRead.Should().Be(wavBytes.Length);
    }

    [Fact]
    public async Task SynthesizeAsync_Throws_On402_WithMessageFromResponse()
    {
        // Arrange
        var errorBody = JsonSerializer.Serialize(new
        {
            message = "Insufficient credits"
        });
        var mockHandler = new MockHttpMessageHandler(errorBody, null, HttpStatusCode.PaymentRequired);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SynthesizeAsync(request, CancellationToken.None));

        ex.Message.Should().Contain("Insufficient credits");
        ex.Message.Should().Contain("402");
    }

    [Fact]
    public async Task SynthesizeAsync_Throws_OnNonSuccess_WithReasonField()
    {
        // Arrange
        var errorBody = JsonSerializer.Serialize(new
        {
            reason = "Invalid text format"
        });
        var mockHandler = new MockHttpMessageHandler(errorBody, null, HttpStatusCode.BadRequest);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SynthesizeAsync(request, CancellationToken.None));

        ex.Message.Should().Contain("Invalid text format");
        ex.Message.Should().Contain("400");
    }

    [Fact]
    public async Task SynthesizeAsync_Throws_OnNonSuccess_WithRawBody()
    {
        // Arrange
        var rawBody = "Something went wrong";
        var mockHandler = new MockHttpMessageHandler(rawBody, null, HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SynthesizeAsync(request, CancellationToken.None));

        ex.Message.Should().Contain("Something went wrong");
        ex.Message.Should().Contain("500");
    }

    [Fact]
    public async Task SynthesizeAsync_Throws_WhenTheAudioResponseExceedsTheCap()
    {
        // Arrange — a response that declares, and really carries, more than the ceiling.
        var oversized = new byte[FishAudioSpeechSynthesisClient.MaxAudioResponseBytes + 1];
        var mockHandler = new MockHttpMessageHandler(null, oversized, HttpStatusCode.OK);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        // Act & Assert — the message names the cap; the payload is not carried into it.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SynthesizeAsync(request, CancellationToken.None));

        ex.Message.Should().Contain(FishAudioSpeechSynthesisClient.MaxAudioResponseBytes.ToString());
        ex.Message.Should().Contain("(32 MiB)");
    }

    [Fact]
    public async Task SynthesizeAsync_Throws_WhenAChunkedAudioResponseExceedsTheCap()
    {
        // A hostile endpoint need not be honest about Content-Length — it can omit it entirely and
        // just keep writing. This content is non-seekable (so no length is declared) and produces
        // its bytes lazily, which is the shape the read loop's own ceiling exists to catch.
        const int size = FishAudioSpeechSynthesisClient.MaxAudioResponseBytes + 1024;
        var mockHandler = new MockHttpMessageHandler(
            responseContent: new StreamContent(new EndlessZeroStream(size)));
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SynthesizeAsync(request, CancellationToken.None));

        ex.Message.Should().Contain(FishAudioSpeechSynthesisClient.MaxAudioResponseBytes.ToString());
    }

    /// <summary>
    /// CONDITION A (security verdict <c>voiceover-104</c>, condition 1 — "make the read caps actually
    /// bound what the process reads").
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other cap test in this file hands the handler a <see cref="ByteArrayContent"/> or a
    /// <see cref="StreamContent"/>, i.e. a body that is already a materialized buffer. Neither shape
    /// can observe the defect this case exists for, because the defect is not in the reading loop —
    /// it is in WHO reads first.
    /// </para>
    /// <para>
    /// <c>SynthesizeAsync</c> used to call <c>_httpClient.SendAsync(httpRequest, ct)</c> with the
    /// default <see cref="HttpCompletionOption.ResponseContentRead"/>, which makes
    /// <see cref="HttpClient"/> drain the ENTIRE response into its own buffer — up to its default
    /// 2 GiB <c>MaxResponseContentBufferSize</c> — BEFORE <c>SynthesizeAsync</c> regains control.
    /// The 32 MiB and 4 KiB caps then ran against an allocation that had already happened, so they
    /// bounded nothing, and the exception text claiming the body "was discarded without being
    /// buffered" was simply false.
    /// </para>
    /// <para>
    /// The only way to see that is to watch the buffering itself. <see cref="CountingLazyZeroContent"/>
    /// records whether HttpClient ever serialized it (that call IS the buffering) and how many bytes
    /// it ever handed out, and it declares no length so the declared-length gate cannot mask the
    /// byte-level behaviour.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SynthesizeAsync_RequestsTheResponseStreamed_SoHttpClientNeverBuffersItFirst()
    {
        // Arrange — an oversized body that exists only as a lazily generated stream: nothing here
        // holds MaxAudioResponseBytes in memory, and the content has no declared Content-Length, so
        // the only thing that can bound the read is the read loop itself.
        //
        // The size is chosen to DISCRIMINATE, not merely to exceed. Assertion (b) below allows the
        // cap plus ONE chunk (the loop reads a chunk, then checks the total), so a body that is only
        // slightly larger than the cap is drained in full by a buffering client and still lands
        // inside that allowance — the assertion then passes on the very tree it exists to indict,
        // which is exactly what a cap+64 KiB body did. This body is the cap plus one chunk plus
        // 4 MiB: a buffering reader produces cap + one chunk + 4 MiB and misses the allowance by a
        // full 4 MiB, while a streaming reader still stops within one chunk of the cap. The
        // separation is a margin wide, not a rounding.
        const int oneChunk = 81920;
        const int discriminatingHeadroomBytes = 4 * 1024 * 1024;
        var content = new CountingLazyZeroContent(
            FishAudioSpeechSynthesisClient.MaxAudioResponseBytes + oneChunk + discriminatingHeadroomBytes);

        var mockHandler = new MockHttpMessageHandler(responseContent: content);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        // Act
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SynthesizeAsync(request, CancellationToken.None));

        // The oversize is still refused, and for the intended reason: the cap, not a parse error.
        ex.Message.Should().Contain(FishAudioSpeechSynthesisClient.MaxAudioResponseBytes.ToString());
        ex.Message.Should().Contain("(32 MiB)");

        // (a) THE DISCRIMINATING ASSERTION. SerializeToStreamAsync is the call HttpClient makes when
        // it buffers under ResponseContentRead. On the pre-fix source it is called and the whole
        // oversized body is drained into HttpClient's buffer before either cap runs; on the fixed
        // source, which sends with ResponseHeadersRead, it is never called at all.
        content.WasBuffered.Should().BeFalse(
            "SynthesizeAsync must send with HttpCompletionOption.ResponseHeadersRead — under the "
            + "default ResponseContentRead, HttpClient drains the entire body (ceiling ~2 GiB) "
            + "before the 32 MiB / 4 KiB caps can fire, so those caps bound nothing and the "
            + "exception text claiming the body was never buffered is false");

        // (b) …and the reading really did stop at the cap: what the process read is bounded by the
        // cap plus at most one chunk (the loop reads a chunk, then checks the total). The body above
        // is sized so that a buffering reader misses this by 4 MiB rather than by nothing.
        content.BytesProduced.Should().BeLessThanOrEqualTo(
            FishAudioSpeechSynthesisClient.MaxAudioResponseBytes + oneChunk,
            "the read must stop within one chunk of the cap rather than draining the body");
    }

    [Fact]
    public async Task SynthesizeAsync_BoundsAHugeErrorBody()
    {
        // Arrange — 1 MB of error body. Only a bounded prefix of it may reach the exception.
        var hugeErrorBody = new string('x', 1024 * 1024);
        var mockHandler = new MockHttpMessageHandler(hugeErrorBody, null, HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.fish.audio")
        };
        var client = new FishAudioSpeechSynthesisClient(httpClient, null);

        var request = new SpeechRequest("Hello world", null, null);

        // Act & Assert — status code survives, the megabyte does not.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SynthesizeAsync(request, CancellationToken.None));

        ex.Message.Should().Contain("500");
        ex.Message.Length.Should().BeLessThan(400);
    }

    internal static byte[] GetSampleWavBytes()
    {
        // Minimal WAV file header (44 bytes) + minimal audio data
        // This is a valid (but silent) WAV file
        byte[] header = new byte[]
        {
            0x52, 0x49, 0x46, 0x46,  // "RIFF"
            0x24, 0x00, 0x00, 0x00,  // File size - 8 (36 bytes)
            0x57, 0x41, 0x56, 0x45,  // "WAVE"
            0x66, 0x6d, 0x74, 0x20,  // "fmt "
            0x10, 0x00, 0x00, 0x00,  // Subchunk1Size (16)
            0x01, 0x00,              // AudioFormat (1 = PCM)
            0x01, 0x00,              // NumChannels (1)
            0x80, 0xBB, 0x00, 0x00,  // SampleRate (48000)
            0x00, 0xEE, 0x02, 0x00,  // ByteRate (48000 * 1 * 2)
            0x02, 0x00,              // BlockAlign (1 * 2)
            0x10, 0x00,              // BitsPerSample (16)
            0x64, 0x61, 0x74, 0x61,  // "data"
            0x00, 0x00, 0x00, 0x00   // Subchunk2Size (0, no audio data)
        };
        return header;
    }

    internal sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string? _responseBodyString;
        private readonly byte[]? _responseBodyBytes;
        private readonly HttpStatusCode _statusCode;
        private readonly HttpContent? _responseContent;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public MockHttpMessageHandler(
            string? responseBodyString = null,
            byte[]? responseBodyBytes = null,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            HttpContent? responseContent = null)
        {
            _responseBodyString = responseBodyString;
            _responseBodyBytes = responseBodyBytes;
            _statusCode = statusCode;
            _responseContent = responseContent;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            HttpResponseMessage response;
            if (_responseContent != null)
            {
                response = new HttpResponseMessage(_statusCode) { Content = _responseContent };
            }
            else if (_responseBodyBytes != null)
            {
                response = new HttpResponseMessage(_statusCode)
                {
                    Content = new ByteArrayContent(_responseBodyBytes)
                    {
                        Headers = { { "Content-Type", "audio/wav" } }
                    }
                };
            }
            else
            {
                response = new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_responseBodyString ?? string.Empty, System.Text.Encoding.UTF8)
                };
            }

            return await Task.FromResult(response);
        }
    }

    /// <summary>
    /// A read-only, non-seekable stream that yields <paramref name="length"/> zero bytes without
    /// ever holding them in memory — the shape of a response that declares no length and simply
    /// keeps sending.
    /// </summary>
    private sealed class EndlessZeroStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int toWrite = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, toWrite);
            _remaining -= toWrite;
            return toWrite;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return ValueTask.FromResult(0);
            int toWrite = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..toWrite].Clear();
            _remaining -= toWrite;
            return ValueTask.FromResult(toWrite);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A response body of a known size that is generated lazily and counts every byte it ever hands
    /// out, plus a flag recording whether it was ever serialized by an outbound writer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="WasBuffered"/> is set by <see cref="SerializeToStreamAsync(Stream, TransportContext?, CancellationToken)"/>,
    /// which is the call <see cref="HttpClient"/> makes when it buffers a response under the default
    /// <see cref="HttpCompletionOption.ResponseContentRead"/>. It is NOT set by
    /// <see cref="CreateContentReadStreamAsync(CancellationToken)"/>: in
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> mode the client never serializes the
    /// content, it just hands the stream to the caller, so the flag distinguishes the two modes
    /// from the content's own side.
    /// </para>
    /// <para>
    /// <see cref="TryComputeLength"/> returns <see langword="false"/>, so this content declares no
    /// length and streams chunked. That is deliberate: it removes the declared-length gate from the
    /// picture, so what this fixture measures is what the READER did, not what a header said.
    /// </para>
    /// <para>
    /// The body is never materialized: <see cref="LazyZeroByteStream"/> synthesizes zeros into the
    /// caller's own buffer one read at a time, so a bug that drains the body costs the process the
    /// read, not a copy of the payload held by the fixture.
    /// </para>
    /// </remarks>
    private sealed class CountingLazyZeroContent : HttpContent
    {
        private readonly long _length;
        private long _bytesProduced;

        public CountingLazyZeroContent(long length) => _length = length;

        /// <summary>Total bytes this content has handed to any reader, across both entry points.</summary>
        public long BytesProduced => Interlocked.Read(ref _bytesProduced);

        /// <summary>
        /// True when an outbound writer serialized this content — i.e. when it was buffered.
        /// </summary>
        public bool WasBuffered { get; private set; }

        internal long Length => _length;

        internal void CountProduced(long bytes) => Interlocked.Add(ref _bytesProduced, bytes);

        protected override bool TryComputeLength(out long length)
        {
            // Chunked — no declared length for a reader to reject the body on.
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            // THE BUFFERING SIGNAL. HttpClient reaches this only when it is copying the body into
            // its own buffer (ResponseContentRead); under ResponseHeadersRead it never runs.
            WasBuffered = true;

            await using var source = new LazyZeroByteStream(this);
            await source.CopyToAsync(stream, 81920, cancellationToken);
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => CreateContentReadStreamAsync(CancellationToken.None);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new LazyZeroByteStream(this));
    }

    /// <summary>
    /// A read-only, non-seekable stream over <paramref name="length"/> zero bytes, generated one
    /// read at a time. Each read is reported to its owner so the owner can prove how much of the
    /// body was ever produced.
    /// </summary>
    private sealed class LazyZeroByteStream : Stream
    {
        private readonly CountingLazyZeroContent _owner;
        private long _remaining;

        public LazyZeroByteStream(CountingLazyZeroContent owner)
        {
            _owner = owner;
            _remaining = owner.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Produce(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Produce(buffer.Span));

        private int Produce(Span<byte> destination)
        {
            int produced = (int)Math.Min(destination.Length, _remaining);
            if (produced <= 0)
                return 0;

            destination[..produced].Clear();
            _remaining -= produced;
            _owner.CountProduced(produced);
            return produced;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
