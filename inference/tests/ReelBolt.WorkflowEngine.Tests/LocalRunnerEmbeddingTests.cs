using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The LocalRunner relay serving the Embedding capability, against an in-process loopback fake of the gateway's
/// <c>/v1/embeddings</c> route: route, bearer, body, failure mapping, the allowlist and zero credits.
/// </summary>
public class LocalRunnerEmbeddingTests
{
    private const string Token = "embed-secret-token-77";
    private static readonly Guid Org = Guid.NewGuid();

    private const string Vectors =
        """{"object":"list","model":"nomic-embed-text","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2,0.3]}],"usage":{"prompt_tokens":4,"total_tokens":4}}""";

    private sealed record Seen(string Method, string Path, string? Authorization, string Body);

    private sealed class FakeGateway : IDisposable
    {
        private readonly HttpListener _listener;
        public ConcurrentQueue<Seen> Requests { get; } = new();
        public string BaseUrl { get; }

        public FakeGateway(Func<HttpListenerContext, Task> respond)
        {
            // A free port picked and released leaves a window another parallel test can take ("Address already in use"), so
            // the port is only a candidate: bind the real listener to it and, when somebody got there first, take a fresh one.
            HttpListener? bound = null;
            string? baseUrl = null;
            for (int attempt = 0; bound is null; attempt++)
            {
                TcpListener l = new(IPAddress.Loopback, 0);
                l.Start();
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                string url = $"http://127.0.0.1:{port}/";
                HttpListener candidate = new();
                candidate.Prefixes.Add(url);
                try
                {
                    candidate.Start();
                    bound = candidate;
                    baseUrl = url;
                }
                catch (HttpListenerException) when (attempt < 25)
                {
                    candidate.Close();
                }
            }

            _listener = bound;
            BaseUrl = baseUrl!;
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using StreamReader reader = new(ctx.Request.InputStream, Encoding.UTF8);
                        string body = await reader.ReadToEndAsync();
                        Requests.Enqueue(new Seen(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath,
                            ctx.Request.Headers["Authorization"], body));
                        try { await respond(ctx); }
                        catch { /* aborted by design */ }
                    });
                }
            });
        }

        public void Dispose() { try { _listener.Close(); } catch { /* already closed */ } }
    }

    private static async Task Reply(HttpListenerContext ctx, int status, string body, string? relayError = null)
    {
        // The real gateway marks every refusal of its own with this header.
        if (relayError is not null) ctx.Response.Headers[LocalRunnerChatClient.RelayErrorHeader] = relayError;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
        ctx.Response.Close();
    }

    private static ResolvedEmbeddingProvider Row(Guid? org, Guid device) => new(
        Guid.NewGuid(), "my laptop", InferenceProviderKind.LocalRunner, device.ToString(), "nomic-embed-text", string.Empty, 10,
        OrganizationId: org, IsPlatformManaged: false);

    private static EmbeddingClientFactory Factory(FakeGateway gw, IUsageRecorder? recorder = null) =>
        new(recorder, null, Microsoft.Extensions.Options.Options.Create(
            new LocalRunnerGatewayOptions { GatewayBaseUrl = gw.BaseUrl, InternalToken = Token }));

    [Fact]
    public async Task An_embedding_call_hits_the_device_embeddings_route_with_bearer_model_and_input()
    {
        using FakeGateway gw = new(ctx => Reply(ctx, 200, Vectors));
        Guid device = Guid.NewGuid();

        GeneratedEmbeddings<Embedding<float>> result =
            await Factory(gw).Get(Row(Org, device)).GenerateAsync(["ping"]);

        result.Should().ContainSingle().Which.Vector.Length.Should().Be(3);
        Seen seen = gw.Requests.Should().ContainSingle().Subject;
        seen.Method.Should().Be("POST");
        seen.Path.Should().Be($"/internal/v1/llm/orgs/{Org}/devices/{device}/v1/embeddings");
        seen.Authorization.Should().Be("Bearer " + Token);
        using JsonDocument body = JsonDocument.Parse(seen.Body);
        body.RootElement.GetProperty("model").GetString().Should().Be("nomic-embed-text");
        body.RootElement.GetProperty("input").ToString().Should().Contain("ping");
    }

    [Theory]
    [InlineData(404, "device_not_found")]
    [InlineData(409, "local_llm_disabled")]
    [InlineData(503, "runner_offline")]
    [InlineData(502, "local_server_unreachable")]
    [InlineData(504, "timeout")]
    [InlineData(429, "busy")]
    public async Task A_gateway_refusal_surfaces_as_the_typed_unavailable_exception(int status, string code)
    {
        using FakeGateway gw = new(ctx => Reply(ctx, status, JsonSerializer.Serialize(new { error = new { code, message = "x" } }), code));

        Func<Task> act = () => Factory(gw).Get(Row(Org, Guid.NewGuid())).GenerateAsync(["ping"]);

        (await act.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which.GatewayCode.Should().Be(code);
        gw.Requests.Should().ContainSingle("a relayed call is never retried");
    }

    [Fact]
    public async Task An_unreachable_gateway_surfaces_as_the_typed_unavailable_exception()
    {
        FakeGateway gw = new(ctx => Reply(ctx, 200, Vectors));
        EmbeddingClientFactory factory = Factory(gw);
        gw.Dispose();

        Func<Task> act = () => factory.Get(Row(Org, Guid.NewGuid())).GenerateAsync(["ping"]);

        await act.Should().ThrowAsync<LocalRunnerUnavailableException>();
    }

    [Fact]
    public void A_row_without_an_organization_is_refused()
    {
        using FakeGateway gw = new(ctx => Reply(ctx, 200, Vectors));
        Action act = () => Factory(gw).Get(Row(null, Guid.NewGuid()));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Embedding_is_admitted_for_local_runner_and_the_generic_refusal_does_not_fire()
    {
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.LocalRunner, InferenceProviderCapability.Embedding, out string error).Should().BeFalse(error);
        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.LocalRunner]
            .Should().Contain(InferenceProviderCapability.Embedding);
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.LocalRunner, InferenceProviderCapability.Transcription, out string refused).Should().BeTrue();
        refused.Should().Contain("Chat, Vision and Embedding");
        InferenceProvidersController.IsUnsupportedCombination(
            InferenceProviderKind.Anthropic, InferenceProviderCapability.Embedding, out string other).Should().BeTrue();
        other.Should().Contain("cannot serve the Embedding capability");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_metered_local_runner_embedding_records_zero_credits_and_nothing_remote()
    {
        using FakeGateway gw = new(ctx => Reply(ctx, 200, Vectors));
        CapturingSink sink = new();
        UsageRecorder recorder = new(new EmptyPriceService(), sink, NullLogger<UsageRecorder>.Instance);

        using (UsageScope.Begin(Org, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), UsageSource.Workflow))
            await Factory(gw, recorder).Get(Row(Org, Guid.NewGuid())).GenerateAsync(["ping"]);
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        sink.Events.Should().OnlyContain(e =>
            e.CreditsMillis == 0 && e.CostUsd == 0m && e.BillingMode == UsageBillingMode.Byo && !e.Unpriced
            && e.ProviderKind == InferenceProviderKind.LocalRunner);
        gw.Requests.Should().ContainSingle();
    }
}
