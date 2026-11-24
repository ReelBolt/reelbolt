using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Wave B residual (issue #93 phase 3): the vendor's own model-page cURL documents an
/// <c>Idempotency-Key</c> header on generation submissions, re-fetched 2026-10-04. The client now
/// sends it when, and only when, the caller supplies a token — the <c>VideoGenerate</c> step passes
/// its persisted ledger row id. A request with no token stays byte-identical to the previous wire
/// shape. No credential, no network: a recording <see cref="HttpMessageHandler"/>.
/// </summary>
public class HiggsfieldIdempotencyKeyTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"request_id":"req-1"}""", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static (HiggsfieldVideoGenerationClient Client, RecordingHandler Handler) Client()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.higgsfield.ai/") };
        return (new HiggsfieldVideoGenerationClient(http, "id:secret"), handler);
    }

    private static VideoGenerationRequest Request(string? idempotencyKey) =>
        new("kling-video/v3.0/std/text-to-video", "A lighthouse at dusk", 5, "1080P", "16:9", Images: null, IdempotencyKey: idempotencyKey);

    [Fact]
    public async Task SubmitAsync_sends_the_Idempotency_Key_header_when_the_request_carries_a_token()
    {
        var (client, handler) = Client();
        string key = Guid.NewGuid().ToString("D");

        await client.SubmitAsync(Request(key), CancellationToken.None);

        HttpRequestMessage sent = handler.Requests.Should().ContainSingle().Subject;
        sent.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? values).Should().BeTrue();
        values!.Single().Should().Be(key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SubmitAsync_sends_no_Idempotency_Key_header_without_a_token(string? key)
    {
        var (client, handler) = Client();

        await client.SubmitAsync(Request(key), CancellationToken.None);

        handler.Requests.Single().Headers.Contains("Idempotency-Key").Should().BeFalse(
            "a request with no token must be byte-identical to the pre-header wire shape");
    }
}
