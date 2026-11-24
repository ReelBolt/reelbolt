using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Compute;

/// <summary>
/// WP D7a — the seam's OWN contract, pinned independently of the agent tools that now sit on top of
/// it. The tool-level suites (ReactRemotionSandboxToolsTests, SandboxListMissingDirectoryTests, the
/// output-policy end-to-end test) prove the refactor changed nothing they could see; these prove the
/// thing a D7b remote implementation has to reproduce: the exact method, path, escaping, request
/// body and credential of every sandbox call, and the failure contract callers branch on.
///
/// <para>
/// The recorder stores strings rather than the <see cref="HttpRequestMessage"/>, because the
/// convenience overloads (<c>PutAsJsonAsync</c> and friends) dispose the request they build.
/// </para>
/// </summary>
public class LocalSandboxSessionTests
{
    private static readonly Guid ExecutionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private const string BaseUrl = "http://sandbox-executor:8080";
    private const string ApiToken = "test-token";

    private sealed record Call(string Method, string Uri, string? Authorization, string? Body);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? content = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Calls.Add(new Call(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                content));

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class RawHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static LocalSandboxSession Session(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), ExecutionId, BaseUrl, ApiToken);

    [Fact]
    public async Task Ensure_posts_the_execution_id_to_the_configured_executor_with_the_shared_secret()
    {
        RecordingHandler handler = new(
            HttpStatusCode.OK,
            """
            {"id":"s1","workflowExecutionId":"11111111-2222-3333-4444-555555555555",
             "containerName":"reelbolt-sandbox-1","workspacePath":"/workspace",
             "createdAt":"2024-01-01T00:00:00Z","lastActivity":"2024-01-01T00:00:00Z"}
            """);

        SandboxInfo sandbox = await Session(handler).EnsureAsync(CancellationToken.None);

        sandbox.ContainerName.Should().Be("reelbolt-sandbox-1");
        Call call = handler.Calls.Should().ContainSingle().Subject;
        call.Method.Should().Be("POST");
        call.Uri.Should().Be($"{BaseUrl}/api/v1/sandboxes");
        call.Authorization.Should().Be($"Bearer {ApiToken}");
        call.Body.Should().Be($"{{\"workflowExecutionId\":\"{ExecutionId}\"}}");
    }

    [Fact]
    public async Task ListFiles_reads_the_escaped_path_and_returns_the_entries()
    {
        RecordingHandler handler = new(
            HttpStatusCode.OK,
            """[{"name":"root.tsx","isDir":false,"size":12,"modTime":"2024-01-01T00:00:00Z"}]""");

        IReadOnlyList<SandboxFileEntry> entries =
            await Session(handler).ListFilesAsync("src/remotion", CancellationToken.None);

        entries.Should().ContainSingle();
        entries[0].Name.Should().Be("root.tsx");
        entries[0].IsDir.Should().BeFalse();

        Call call = handler.Calls.Should().ContainSingle().Subject;
        call.Method.Should().Be("GET");
        call.Uri.Should().Be(
            $"{BaseUrl}/api/v1/sandboxes/{ExecutionId}/files?path=src%2Fremotion");
    }

    [Fact]
    public async Task ListFiles_answers_a_missing_directory_with_a_typed_failure_carrying_the_status()
    {
        RecordingHandler handler = new(
            HttpStatusCode.NotFound,
            """{"error":"openat public: no such file or directory"}""");

        Exception? thrown = await Record.ExceptionAsync(
            () => Session(handler).ListFilesAsync("public", CancellationToken.None));

        // The message is the one the engine has always surfaced, and the type still IS an
        // InvalidOperationException, which is what a caller (and every existing test) catches.
        SandboxRequestFailedException failure = thrown.Should().BeOfType<SandboxRequestFailedException>().Subject;
        failure.Should().BeAssignableTo<InvalidOperationException>();
        failure.StatusCode.Should().Be(HttpStatusCode.NotFound);
        failure.Message.Should().Be(
            "Sandbox API request failed (404): openat public: no such file or directory");
    }

    [Fact]
    public async Task ReadText_puts_nothing_on_the_wire_and_decodes_the_base64_body()
    {
        RecordingHandler handler = new(
            HttpStatusCode.OK,
            $$"""{"path":"src/root.tsx","contentBase64":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes("export const x = 1;"))}}"}""");

        SandboxTextFile payload = await Session(handler).ReadTextAsync("src/root.tsx", CancellationToken.None);

        payload.Path.Should().Be("src/root.tsx");
        payload.Text.Should().Be("export const x = 1;");

        Call call = handler.Calls.Should().ContainSingle().Subject;
        call.Method.Should().Be("GET");
        call.Uri.Should().Be($"{BaseUrl}/api/v1/sandboxes/{ExecutionId}/files/content?path=src%2Froot.tsx");
        call.Body.Should().BeNull();
    }

    [Fact]
    public async Task WriteText_puts_the_base64_of_the_utf8_text()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, """{"ok":true}""");

        SandboxActionResult result =
            await Session(handler).WriteTextAsync("src/a.tsx", "héllo", CancellationToken.None);

        result.Ok.Should().BeTrue();

        Call call = handler.Calls.Should().ContainSingle().Subject;
        call.Method.Should().Be("PUT");
        call.Uri.Should().Be($"{BaseUrl}/api/v1/sandboxes/{ExecutionId}/files/content?path=src%2Fa.tsx");
        call.Body.Should().Be($"{{\"contentBase64\":\"{Convert.ToBase64String(Encoding.UTF8.GetBytes("héllo"))}\"}}");
    }

    [Fact]
    public async Task WriteBytes_reports_a_rejection_as_the_same_typed_failure()
    {
        RecordingHandler handler = new(HttpStatusCode.InternalServerError, """{"error":"disk full"}""");

        Exception? thrown = await Record.ExceptionAsync(
            () => Session(handler).WriteBytesAsync("voiceover/scene-0.wav", [1, 2, 3], CancellationToken.None));

        SandboxRequestFailedException failure = thrown.Should().BeOfType<SandboxRequestFailedException>().Subject;
        failure.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        failure.Message.Should().Be("Sandbox API request failed (500): disk full");
    }

    [Fact]
    public async Task Exec_returns_the_payload_and_the_status_it_was_read_under()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, """{"output":"built","error":""}""");

        SandboxExecResult result = await Session(handler).ExecAsync(
            "npm", ["run", "build"], 120, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Payload.Output.Should().Be("built");
        result.TransportError.Should().BeNull();

        Call call = handler.Calls.Should().ContainSingle().Subject;
        call.Method.Should().Be("POST");
        call.Uri.Should().Be($"{BaseUrl}/api/v1/sandboxes/{ExecutionId}/exec");
        call.Body.Should().Be("""{"command":"npm","args":["run","build"],"timeoutSeconds":120}""");
    }

    [Fact]
    public async Task Exec_reports_a_rejection_as_a_result_rather_than_an_exception()
    {
        RecordingHandler handler = new(HttpStatusCode.BadRequest, """{"error":"command not allowed"}""");

        SandboxExecResult result = await Session(handler).ExecAsync(
            "sh", ["-c", "rm -rf /"], 0, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().Be(400);
        result.RawError.Should().Be("command not allowed");
        result.TransportError.Should().BeNull();
    }

    [Fact]
    public async Task Exec_reports_a_transport_failure_as_data_for_the_agent_to_retry_on()
    {
        SandboxExecResult result = await Session(new ThrowingHandler()).ExecAsync(
            "npm", ["run", "build"], 120, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.StatusCode.Should().BeNull();
        result.TransportError.Should().NotBeNull();
        result.RawError.Should().Be("connection refused");
    }

    [Fact]
    public async Task OpenRawFile_throws_the_message_the_render_path_has_always_surfaced()
    {
        RecordingHandler handler = new(HttpStatusCode.NotFound, """{"error":"no such file"}""");

        Exception? thrown = await Record.ExceptionAsync(
            () => Session(handler).OpenRawFileAsync("out/promo.mp4", 600, CancellationToken.None));

        SandboxRequestFailedException failure = thrown.Should().BeOfType<SandboxRequestFailedException>().Subject;
        failure.StatusCode.Should().Be(HttpStatusCode.NotFound);
        failure.Message.Should().Be("Sandbox API request failed (404): no such file");

        Call call = handler.Calls.Should().ContainSingle().Subject;
        call.Uri.Should().Be($"{BaseUrl}/api/v1/sandboxes/{ExecutionId}/files/raw?path=out%2Fpromo.mp4");
    }

    [Fact]
    public async Task OpenRawFile_streams_the_bytes_with_the_length_the_object_store_needs()
    {
        byte[] render = [0x00, 0x01, 0x02, 0x03, 0x04];

        await using SandboxRawFile raw = await Session(new RawHandler(render))
            .OpenRawFileAsync("out/promo.mp4", 600, CancellationToken.None);

        raw.ContentLength.Should().Be(render.Length);

        using MemoryStream copy = new();
        await raw.Content.CopyToAsync(copy);
        copy.ToArray().Should().Equal(render);
    }

    [Fact]
    public async Task The_factory_reads_the_address_and_secret_from_configuration()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, """{"ok":true}""");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sandbox:BaseUrl"] = "http://elsewhere:9000",
                ["Sandbox:ApiToken"] = "other-token"
            })
            .Build();

        LocalSandboxSessionFactory factory = new(new StubHttpClientFactory(handler), configuration);
        ISandboxSession session = factory.Create(ExecutionId);

        session.ExecutionId.Should().Be(ExecutionId);

        await session.CompleteAsync(CancellationToken.None);

        Call call = handler.Calls.Should().ContainSingle().Subject;
        call.Uri.Should().Be($"http://elsewhere:9000/api/v1/sandboxes/{ExecutionId}/complete");
        call.Authorization.Should().Be("Bearer other-token");
    }

    [Fact]
    public async Task An_empty_secret_is_sent_as_an_empty_bearer_the_way_the_shipped_default_is()
    {
        // Sandbox:ApiToken defaults to "" rather than failing: the sandbox-executor rejects a request
        // without the header, and that rejection is what a misconfigured deployment should see.
        RecordingHandler handler = new(HttpStatusCode.OK, """{"ok":true}""");
        LocalSandboxSession session = new(
            new StubHttpClientFactory(handler), ExecutionId, BaseUrl, apiToken: string.Empty);

        await session.CompleteAsync(CancellationToken.None);

        handler.Calls.Should().ContainSingle().Subject.Authorization.Should().Be("Bearer");
    }
}
