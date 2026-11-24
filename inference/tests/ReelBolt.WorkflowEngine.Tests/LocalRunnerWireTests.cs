using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The LocalRunner relay on a real socket: the factory-built chat client talks to an in-process loopback fake that
/// behaves like the Go runner gateway's LLM relay (status codes, <c>{error:{code,message}}</c> bodies, SSE passthrough,
/// an aborted stream on a mid-response failure). Also pins no-fallback, token hygiene, org ownership and zero metering.
/// </summary>
public class LocalRunnerWireTests
{
    private const string Token = "wire-secret-token-9f3a";
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private const string Completion =
        """{"id":"c1","object":"chat.completion","created":1,"model":"llama3.2:3b","choices":[{"index":0,"message":{"role":"assistant","content":"hello from the laptop"},"finish_reason":"stop"}]}""";

    // ------------------------------------------------------------------ fake gateway

    private sealed record Seen(string Method, string Path, string? Authorization, string Body);

    private sealed class FakeGateway : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<HttpListenerContext, Task> _respond;
        public ConcurrentQueue<Seen> Requests { get; } = new();
        public string BaseUrl { get; }

        public FakeGateway(Func<HttpListenerContext, Task> respond)
        {
            _respond = respond;
            int port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
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
                    try { await _respond(ctx); }
                    catch { /* aborted by design */ }
                });
            }
        }

        public void Dispose() { try { _listener.Close(); } catch { /* already closed */ } }
    }

    private static int FreePort()
    {
        TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task Reply(HttpListenerContext ctx, int status, string body, string type = "application/json")
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static Task GatewayError(HttpListenerContext ctx, int status, string code)
    {
        // The real gateway marks every refusal of its own with this header.
        ctx.Response.Headers[LocalRunnerChatClient.RelayErrorHeader] = code;
        return Reply(ctx, status, JsonSerializer.Serialize(new { error = new { code, message = "relay says " + code } }));
    }

    private static ResolvedInferenceProvider Row(Guid? org, Guid device) => new(
        Guid.NewGuid(), "my laptop", InferenceProviderKind.LocalRunner, device.ToString(), "llama3.2:3b", string.Empty, 10,
        OrganizationId: org, IsPlatformManaged: false);

    private static LocalRunnerGatewayOptions Options(string url) => new() { GatewayBaseUrl = url, InternalToken = Token };

    private static IChatClient Client(FakeGateway gw, Guid org, Guid device, ILogger<ChatClientFactory>? logger = null) =>
        new ChatClientFactory(null, logger, Microsoft.Extensions.Options.Options.Create(Options(gw.BaseUrl))).Get(Row(org, device));

    private static async Task<List<ChatResponseUpdate>> Drain(IChatClient client)
    {
        List<ChatResponseUpdate> all = [];
        await foreach (ChatResponseUpdate u in client.GetStreamingResponseAsync("hi"))
            all.Add(u);
        return all;
    }

    // ------------------------------------------------------------------ 1 + 2: happy paths

    [Fact]
    public async Task A_buffered_completion_hits_the_device_route_with_the_bearer_token_and_the_model()
    {
        using FakeGateway gw = new(ctx => Reply(ctx, 200, Completion));
        Guid device = Guid.NewGuid();

        ChatResponse response = await Client(gw, OrgA, device).GetResponseAsync("hi");

        response.Text.Should().Be("hello from the laptop");
        Seen seen = gw.Requests.Should().ContainSingle().Subject;
        seen.Method.Should().Be("POST");
        seen.Path.Should().Be($"/internal/v1/llm/orgs/{OrgA}/devices/{device}/v1/chat/completions");
        seen.Authorization.Should().Be("Bearer " + Token);
        using JsonDocument doc = JsonDocument.Parse(seen.Body);
        doc.RootElement.GetProperty("model").GetString().Should().Be("llama3.2:3b");
        doc.RootElement.GetProperty("messages")[0].GetProperty("content").ToString().Should().Contain("hi");
    }

    [Fact]
    public async Task Org_and_device_come_from_the_provider_row_for_each_row()
    {
        using FakeGateway gw = new(ctx => Reply(ctx, 200, Completion));
        Guid d1 = Guid.NewGuid(), d2 = Guid.NewGuid();
        ChatClientFactory factory = new(null, null, Microsoft.Extensions.Options.Options.Create(Options(gw.BaseUrl)));

        await factory.Get(Row(OrgA, d1)).GetResponseAsync("x");
        await factory.Get(Row(OrgB, d2)).GetResponseAsync("x");

        gw.Requests.Select(r => r.Path).Should().BeEquivalentTo(new[]
        {
            $"/internal/v1/llm/orgs/{OrgA}/devices/{d1}/v1/chat/completions",
            $"/internal/v1/llm/orgs/{OrgB}/devices/{d2}/v1/chat/completions"
        });
    }

    [Fact]
    public async Task Streaming_yields_the_sse_chunks_in_order()
    {
        using FakeGateway gw = new(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            foreach (string word in new[] { "one ", "two ", "three" })
            {
                string chunk = "data: " + JsonSerializer.Serialize(new
                {
                    id = "c1",
                    @object = "chat.completion.chunk",
                    created = 1,
                    model = "llama3.2:3b",
                    choices = new[] { new { index = 0, delta = new { role = "assistant", content = word }, finish_reason = (string?)null } }
                }) + "\n\n";
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(chunk));
                await ctx.Response.OutputStream.FlushAsync();
                await Task.Delay(20);
            }
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
            ctx.Response.Close();
        });

        List<ChatResponseUpdate> updates = await Drain(Client(gw, OrgA, Guid.NewGuid()));

        string.Concat(updates.Select(u => u.Text)).Should().Be("one two three");
        updates.Where(u => !string.IsNullOrEmpty(u.Text)).Select(u => u.Text).Should().Equal("one ", "two ", "three");
        gw.Requests.Single().Body.Should().Contain("\"stream\":true");
    }

    // ------------------------------------------------------------------ 3: failures

    public static IEnumerable<object[]> Failures() =>
    [
        [503, "runner_offline"],
        [409, "local_llm_disabled"],
        [404, "device_not_found"],
        [429, "busy"],
        [502, "local_server_unreachable"],
        [504, "timeout"],
        [503, "internal_api_disabled"]
    ];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_gateway_refusal_is_a_typed_unavailable_error_on_the_buffered_path(int status, string code)
    {
        using FakeGateway gw = new(ctx => GatewayError(ctx, status, code));
        IChatClient client = Client(gw, OrgA, Guid.NewGuid());

        Func<Task> act = () => client.GetResponseAsync("hi");

        LocalRunnerUnavailableException ex = (await act.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which;
        AssertTyped(ex, status, code);
        gw.Requests.Should().HaveCount(1, "a relayed call is never retried");
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_gateway_refusal_is_a_typed_unavailable_error_on_the_streaming_path(int status, string code)
    {
        using FakeGateway gw = new(ctx => GatewayError(ctx, status, code));
        IChatClient client = Client(gw, OrgA, Guid.NewGuid());

        Func<Task> act = () => Drain(client);

        LocalRunnerUnavailableException ex = (await act.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which;
        AssertTyped(ex, status, code);
        gw.Requests.Should().HaveCount(1);
    }

    private static void AssertTyped(LocalRunnerUnavailableException ex, int status, string code)
    {
        ex.Code.Should().Be(LocalRunnerUnavailableException.FailureCode);
        ex.GatewayCode.Should().Be(code);
        ((int?)ex.Status).Should().Be(status);
        ex.Message.Should().NotBeNullOrWhiteSpace().And.NotContain("{").And.NotContain("HTTP 5").And.NotContain(Token);
    }

    [Fact]
    public async Task A_server_body_claiming_a_gateway_code_without_the_header_is_the_servers_answer()
    {
        using FakeGateway gw = new(ctx => Reply(ctx, 504, "{\"error\":{\"code\":\"timeout\",\"message\":\"the model said so\"}}"));
        IChatClient client = Client(gw, OrgA, Guid.NewGuid());

        LocalRunnerUnavailableException ex =
            (await ((Func<Task>)(() => client.GetResponseAsync("hi"))).Should().ThrowAsync<LocalRunnerUnavailableException>()).Which;

        ex.GatewayCode.Should().Be("local_server_error");
        ex.Message.Should().Contain("the model said so").And.NotContain("did not answer in time");
    }

    [Fact]
    public async Task An_error_from_the_users_own_model_server_is_reported_as_the_servers_answer()
    {
        // The relay passes the model server's answer through verbatim (no gateway header), so a 404 for a model that
        // is not installed must not read as "your desktop app is gone".
        using FakeGateway gw = new(ctx => Reply(ctx, 404, "{\"error\":{\"message\":\"model 'nope' not found, try pulling it first\"}}"));
        IChatClient client = Client(gw, OrgA, Guid.NewGuid());

        LocalRunnerUnavailableException ex =
            (await ((Func<Task>)(() => client.GetResponseAsync("hi"))).Should().ThrowAsync<LocalRunnerUnavailableException>()).Which;

        ex.GatewayCode.Should().Be("local_server_error");
        ex.Message.Should().Contain("model 'nope' not found").And.Contain("HTTP 404").And.NotContain("no longer available");
    }

    [Fact]
    public async Task A_model_server_cannot_pose_as_a_gateway_refusal_through_its_body()
    {
        using FakeGateway gw = new(ctx => Reply(ctx, 409, "{\"error\":{\"code\":\"anything\",\"message\":\"line one\\u0007line two\"}}"));
        IChatClient client = Client(gw, OrgA, Guid.NewGuid());

        LocalRunnerUnavailableException ex =
            (await ((Func<Task>)(() => client.GetResponseAsync("hi"))).Should().ThrowAsync<LocalRunnerUnavailableException>()).Which;

        ex.GatewayCode.Should().Be("local_server_error");
        ex.Message.Should().NotContain("switched off").And.NotContain("\u0007");
    }

    [Fact]
    public async Task A_refused_connection_is_a_typed_unavailable_error_on_both_paths()
    {
        string dead = $"http://127.0.0.1:{FreePort()}/";
        IChatClient client = new ChatClientFactory(null, null, Microsoft.Extensions.Options.Options.Create(Options(dead)))
            .Get(Row(OrgA, Guid.NewGuid()));

        Func<Task> buffered = () => client.GetResponseAsync("hi");
        Func<Task> streaming = () => Drain(client);

        (await buffered.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which.Message.Should().NotContain(Token);
        (await streaming.Should().ThrowAsync<LocalRunnerUnavailableException>()).Which.Message.Should().NotContain(Token);
    }

    [Fact]
    public async Task A_stream_aborted_mid_body_surfaces_as_a_failure_not_a_clean_completion()
    {
        // A raw TCP server: 200 + one SSE chunk, then the socket is dropped with no terminating chunk (what a
        // proxy does when the upstream dies mid-response). HttpListener.Abort cannot be trusted to do that.
        TcpListener tcp = new(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using TcpClient conn = await tcp.AcceptTcpClientAsync();
                NetworkStream ns = conn.GetStream();
                byte[] buf = new byte[65536];
                int total = 0, n;
                string text = "";
                while (!(text.Contains("\r\n\r\n") && text.Contains("}")) && (n = await ns.ReadAsync(buf.AsMemory(total))) > 0)
                {
                    total += n;
                    text = Encoding.UTF8.GetString(buf, 0, total);
                }
                string payload = "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"par\"},\"finish_reason\":null}]}\n\n";
                string head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n"
                    + payload.Length.ToString("x") + "\r\n" + payload + "\r\n";
                await ns.WriteAsync(Encoding.UTF8.GetBytes(head));
                await ns.FlushAsync();
                await Task.Delay(100);
                conn.Client.LingerState = new LingerOption(true, 0);
            }
        });
        string baseUrl = $"http://127.0.0.1:{port}/";
        // Control: the fake really breaks the transport (a raw read must fail), so a clean pass below is the client's doing.
        using (HttpClient raw = new())
        {
            Func<Task> rawRead = async () =>
            {
                using HttpResponseMessage r = await raw.PostAsync(baseUrl + "x", new StringContent("{}"), CancellationToken.None);
                await r.Content.ReadAsStringAsync();
            };
            await rawRead.Should().ThrowAsync<Exception>("the fake aborts the connection mid-body");
        }

        IChatClient client = new ChatClientFactory(null, null, Microsoft.Extensions.Options.Options.Create(Options(baseUrl)))
            .Get(Row(OrgA, Guid.NewGuid()));
        List<ChatResponseUpdate> got = [];

        Exception? failure = null;
        try
        {
            await foreach (ChatResponseUpdate u in client.GetStreamingResponseAsync("hi"))
                got.Add(u);
        }
        catch (Exception e)
        {
            failure = e;
        }

        got.Select(u => u.Text).Should().Contain("par", "the part that arrived before the abort is delivered");
        failure.Should().NotBeNull("an aborted body must never look like a finished answer");
        failure.Should().BeAssignableTo<Exception>().Which.Message.Should().NotContain(Token);
    }

    // ------------------------------------------------------------------ 4: no fallback

    private sealed class GuardHandler(string allowedAuthority) : DelegatingHandler(new SocketsHttpHandler())
    {
        public ConcurrentBag<string> Authorities { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorities.Add(request.RequestUri!.Authority);
            if (request.RequestUri.Authority != allowedAuthority)
                throw new Xunit.Sdk.XunitException($"Foreign host contacted: {request.RequestUri.Authority}");
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task With_the_gateway_down_no_other_host_is_ever_contacted()
    {
        int port = FreePort();
        string url = $"http://127.0.0.1:{port}/";
        using FakeGateway foreign = new(ctx => Reply(ctx, 200, Completion)); // a live "other" provider that must stay untouched
        GuardHandler guard = new($"127.0.0.1:{port}");
        IChatClient client = ChatClientFactory.BuildLocalRunner(Row(OrgA, Guid.NewGuid()), Options(url), guard);

        Func<Task> buffered = () => client.GetResponseAsync("hi");
        Func<Task> streaming = () => Drain(client);

        await buffered.Should().ThrowAsync<LocalRunnerUnavailableException>();
        await streaming.Should().ThrowAsync<LocalRunnerUnavailableException>();
        guard.Authorities.Distinct().Should().Equal($"127.0.0.1:{port}");
        foreign.Requests.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ 5: token hygiene

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Enqueue(formatter(state, exception));
            if (exception is not null) Lines.Enqueue(exception.ToString());
        }
    }

    [Fact]
    public async Task The_gateway_token_never_appears_in_exceptions_or_logs()
    {
        CaptureLogger<ChatClientFactory> log = new();
        List<string> texts = [];
        foreach ((int status, string code) in new[] { (503, "runner_offline"), (429, "busy"), (504, "timeout") })
        {
            using FakeGateway gw = new(ctx => GatewayError(ctx, status, code));
            IChatClient client = Client(gw, OrgA, Guid.NewGuid(), log);
            try { await client.GetResponseAsync("hi"); }
            catch (LocalRunnerUnavailableException e) { texts.Add(e.Message); texts.Add(e.GatewayCode ?? ""); }
            try { await Drain(client); }
            catch (LocalRunnerUnavailableException e) { texts.Add(e.Message); }
        }

        string dead = $"http://127.0.0.1:{FreePort()}/";
        try
        {
            await new ChatClientFactory(null, log, Microsoft.Extensions.Options.Options.Create(Options(dead)))
                .Get(Row(OrgA, Guid.NewGuid())).GetResponseAsync("hi");
        }
        catch (LocalRunnerUnavailableException e) { texts.Add(e.Message); }

        texts.Should().NotBeEmpty().And.OnlyContain(t => !t.Contains(Token));
        log.Lines.Should().OnlyContain(l => !l.Contains(Token));
    }

    // ------------------------------------------------------------------ 6: org ownership

    private static ModelCapabilityView LocalRow(Guid? org, bool isDefault = false, InferenceProviderCapability cap = InferenceProviderCapability.Chat) => new()
    {
        Id = Guid.NewGuid(),
        Name = "laptop-" + Guid.NewGuid().ToString("N")[..6],
        Kind = InferenceProviderKind.LocalRunner,
        Capability = cap,
        OrganizationId = org,
        Endpoint = Guid.NewGuid().ToString(),
        ModelName = "llama3.2:3b",
        IsEnabled = true,
        IsDefault = isDefault,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static InferenceApiDbContext NewDb() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase("local-runner-wire-" + Guid.NewGuid().ToString("N")).Options);

    private static List<CreateWorkflowStepRequest> StepNaming(Guid providerId) =>
    [
        new(Guid.NewGuid(), 1, null, "Analyze", StepType: nameof(StepType.VideoAnalyze),
            VideoAnalyzeConfigJson: $$"""{"version":1,"source":{"kind":"ProjectFile"},"transcriptionProviderId":"{{providerId}}"}""")
    ];

    [Fact]
    public async Task A_step_may_name_its_own_orgs_local_runner_row_but_not_another_orgs()
    {
        using InferenceApiDbContext db = NewDb();
        ModelCapabilityView mine = LocalRow(OrgA), theirs = LocalRow(OrgB);
        db.AddFlat(mine, theirs);
        await db.SaveChangesAsync();
        StepProviderOwnershipValidator validator = new(db);

        (await validator.ValidateAsync(OrgA, Guid.NewGuid(), StepNaming(mine.Id), CancellationToken.None)).Should().BeEmpty();
        IReadOnlyList<string> refused = await validator.ValidateAsync(OrgA, Guid.NewGuid(), StepNaming(theirs.Id), CancellationToken.None);
        refused.Should().ContainSingle().Which.Should().Contain("is not available to this organization");
        validator.Validate(OrgA, Guid.NewGuid(), StepNaming(theirs.Id)).Should().ContainSingle();
        validator.Validate(OrgA, Guid.NewGuid(), StepNaming(mine.Id)).Should().BeEmpty();
    }

    private sealed class MutableTenant : InferenceTenantAccessorBase
    {
        public Guid? Org { get; set; }
        protected override Guid? AmbientOrganizationId => Org;
    }

    private sealed class PassthroughSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public bool TryUnprotect(string? ciphertext, out string plaintext) { plaintext = ciphertext ?? string.Empty; return true; }
    }

    private static (InferenceProviderResolver Resolver, MutableTenant Tenant) Resolver(InferenceApiDbContext db)
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new InferenceApiProviderStore(db));
        MutableTenant tenant = new();
        return (new InferenceProviderResolver(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(), new PassthroughSecrets(), NullLogger<InferenceProviderResolver>.Instance, tenant), tenant);
    }

    [Fact]
    public async Task The_resolver_never_serves_another_orgs_local_runner_row_and_an_agent_override_falls_to_the_org_default()
    {
        using InferenceApiDbContext db = NewDb();
        ModelCapabilityView platformDefault = LocalRow(null, true);
        platformDefault.Kind = InferenceProviderKind.OpenAICompatible;
        platformDefault.Endpoint = "https://provider.example/v1";
        ModelCapabilityView orgADefault = LocalRow(OrgA, true);
        ModelCapabilityView orgBLaptop = LocalRow(OrgB);
        db.AddFlat(platformDefault, orgADefault, orgBLaptop);
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "custom",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.Custom,
            IsBuiltIn = false,
            OwnerId = Guid.NewGuid(),
            OrganizationId = OrgA,
            InferenceProviderId = orgBLaptop.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();
        (InferenceProviderResolver resolver, MutableTenant tenant) = Resolver(db);
        tenant.Org = OrgA;

        (await resolver.ResolveChatAsync(orgBLaptop.Id, CancellationToken.None)).Should().BeNull("a foreign pin reads as missing");
        ResolvedInferenceProvider resolved = await resolver.ResolveAsync(AgentType.Custom, agent.Id, CancellationToken.None);

        resolved.ProviderId.Should().Be(orgADefault.Id).And.NotBe(orgBLaptop.Id);
        resolved.OrganizationId.Should().Be(OrgA);
        resolved.Kind.Should().Be(InferenceProviderKind.LocalRunner);
    }

    // ------------------------------------------------------------------ 7: metering

    [Fact]
    public async Task A_local_runner_call_through_the_metering_client_records_zero_credits_and_no_ledger_reservation()
    {
        using FakeGateway gw = new(ctx => Reply(ctx, 200, Completion.Replace("\"finish_reason\":\"stop\"}]", "\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":500,\"completion_tokens\":100,\"total_tokens\":600}")));
        CapturingSink sink = new();
        UsageRecorder recorder = new(new EmptyPriceService(), sink, NullLogger<UsageRecorder>.Instance);
        Guid device = Guid.NewGuid();
        IChatClient client = new ChatClientFactory(recorder, null, Microsoft.Extensions.Options.Options.Create(Options(gw.BaseUrl)))
            .Get(Row(OrgA, device));

        using (UsageScope.Begin(OrgA, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), UsageSource.Workflow))
            await client.GetResponseAsync("hi");
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        sink.Events.Should().NotBeEmpty().And.OnlyContain(e =>
            e.CreditsMillis == 0 && e.CostUsd == 0m && e.BillingMode == UsageBillingMode.Byo && !e.Unpriced
            && e.ProviderKind == InferenceProviderKind.LocalRunner);
        gw.Requests.Should().ContainSingle("the only traffic is the relay call itself; nothing reserved or priced remotely");
    }
}
