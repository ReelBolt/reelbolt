using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// Every sandbox tool that makes Remotion load the composition in a browser (list compositions, render
/// stills, render the video, the raw <c>render</c>/<c>still</c>/<c>compositions</c> commands and the
/// <c>render</c>/<c>compositions</c> npm scripts) runs <see cref="NetworkFontPreflight"/> first: a project
/// that loads a font from the network is refused with <c>NETWORK_FONT</c> before any command is sent
/// to the sandbox, instead of failing minutes into a render with a network error.
/// </summary>
public class ReactRemotionSandboxToolsNetworkFontTests
{
    private const string NetworkFontSource = "import {loadFont} from \"@remotion/google-fonts/Inter\";\nconst {fontFamily} = loadFont();\n";

    private const string Table = """

        The following compositions are available:

        Main                   30      1920x1080      90 (3.00 sec)
        """;

    public static TheoryData<string> GuardedCalls() =>
    [
        "GetCompositionInfo",
        "RenderPreviewStills",
        "RenderVideoAndUploadToStorage",
        "remotion render",
        "remotion still",
        "remotion compositions",
        "npm render",
        "npm compositions",
    ];

    [Theory]
    [MemberData(nameof(GuardedCalls))]
    public async Task A_project_that_loads_a_network_font_is_refused_before_any_sandbox_command(string call)
    {
        FontSession session = new();
        session.Files["src/root.tsx"] = "export const Root = () => null;";
        session.Files["src/scenes/Title.tsx"] = NetworkFontSource;
        (ReactRemotionSandboxTools tool, Mock<IAmazonS3> s3) = Build(session);

        using JsonDocument result = JsonDocument.Parse(await Invoke(tool, call));

        result.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        string error = result.RootElement.GetProperty("error").GetString()!;
        error.Should().StartWith("NETWORK_FONT: src/scenes/Title.tsx:1 loads a font from the network");
        error.Should().Contain("The sandbox has no internet. Use an installed font by CSS font-family name: Inter, Montserrat");
        session.Execs.Should().BeEmpty("the refusal comes before anything is sent to the sandbox");
        s3.Verify(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(GuardedCalls))]
    public async Task A_project_with_local_fonts_only_runs_the_command(string call)
    {
        FontSession session = new();
        session.Files["src/root.tsx"] = "export const Root = () => null;";
        session.Files["src/scenes/Title.tsx"] =
            "// Fonts are local: fonts.googleapis.com is not reachable.\nexport const T = () => <h1 style={{fontFamily: 'Inter'}}>Hi</h1>;\n";
        (ReactRemotionSandboxTools tool, _) = Build(session);

        string json = await Invoke(tool, call);

        json.Should().NotContain("NETWORK_FONT");
        session.Execs.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_command_that_does_not_load_the_composition_is_not_checked()
    {
        FontSession session = new();
        session.Files["src/scenes/Title.tsx"] = NetworkFontSource;
        (ReactRemotionSandboxTools tool, _) = Build(session);

        string json = await tool.RunSandboxNpmScript("typecheck");

        json.Should().NotContain("NETWORK_FONT");
        session.Execs.Should().ContainSingle();
    }

    private static Task<string> Invoke(ReactRemotionSandboxTools tool, string call) => call switch
    {
        "GetCompositionInfo" => tool.GetCompositionInfo(),
        "RenderPreviewStills" => tool.RenderPreviewStills("Main"),
        "RenderVideoAndUploadToStorage" => tool.RenderVideoAndUploadToStorage("Main", "promo.mp4"),
        "remotion render" => tool.RunSandboxRemotionCommand("render", ["Main", "out/x.mp4"]),
        "remotion still" => tool.RunSandboxRemotionCommand("still", ["Main", "out/x.png"]),
        "remotion compositions" => tool.RunSandboxRemotionCommand("compositions"),
        "npm render" => tool.RunSandboxNpmScript("render"),
        "npm compositions" => tool.RunSandboxNpmScript("compositions"),
        _ => throw new ArgumentOutOfRangeException(nameof(call), call, null),
    };

    private static (ReactRemotionSandboxTools Tool, Mock<IAmazonS3> S3) Build(FontSession session)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" })
            .Build();

        WorkflowExecutionContextAccessor accessor = new();
        accessor.BeginScope(session.ExecutionId, Guid.NewGuid(), "correlation");

        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse());

        ReactRemotionSandboxTools tool = new(
            Mock.Of<IHttpClientFactory>(),
            accessor,
            Mock.Of<IProjectFileWorkspace>(),
            s3.Object,
            configuration,
            NullLogger<ReactRemotionSandboxTools>.Instance,
            Mock.Of<IServiceScopeFactory>(),
            sandboxSessions: new FixedSessionFactory(session));

        return (tool, s3);
    }

    private sealed class FixedSessionFactory(ISandboxSession session) : ISandboxSessionFactory
    {
        public ISandboxSession Create(Guid executionId) => session;
    }

    /// <summary>Files in a dictionary (directories implied by the paths); every exec succeeds with the composition table.</summary>
    private sealed class FontSession : ISandboxSession
    {
        public Guid ExecutionId { get; } = Guid.NewGuid();
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public List<string[]> Execs { get; } = [];

        public Task<SandboxExecResult> ExecAsync(string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
        {
            Execs.Add([command, .. args]);
            string output = args.Contains("compositions") ? Table : "Rendered 90/90";
            return Task.FromResult(new SandboxExecResult(
                Succeeded: true,
                Payload: new SandboxExecResponse(output, null),
                StatusCode: 200,
                RawBody: JsonSerializer.Serialize(new { output }),
                RawError: string.Empty,
                TransportError: null));
        }

        public Task<IReadOnlyList<SandboxFileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken)
        {
            string prefix = path.TrimEnd('/') + "/";
            var entries = new Dictionary<string, SandboxFileEntry>(StringComparer.Ordinal);
            foreach ((string file, string content) in Files)
            {
                if (!file.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                string rest = file[prefix.Length..];
                int slash = rest.IndexOf('/');
                string name = slash < 0 ? rest : rest[..slash];
                entries[name] = new SandboxFileEntry(name, slash >= 0, slash < 0 ? content.Length : 0, DateTime.UtcNow);
            }

            if (entries.Count == 0 && path != "out/preview")
                throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "Sandbox API request failed (404): not found");
            return Task.FromResult<IReadOnlyList<SandboxFileEntry>>([.. entries.Values]);
        }

        public Task<SandboxTextFile> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            Files.TryGetValue(path, out string? text)
                ? Task.FromResult(new SandboxTextFile(path, text))
                : throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "Sandbox API request failed (404): file not found");

        public Task<SandboxRawFile> OpenRawFileAsync(string path, int commandTimeoutSeconds, CancellationToken cancellationToken)
        {
            byte[] bytes = "a local render"u8.ToArray();
            HttpClient client = new();
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            return Task.FromResult(new SandboxRawFile(client, response, new MemoryStream(bytes), bytes.LongLength));
        }

        public Task<SandboxPublishRoute> ResolvePublishRouteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxPublishRoute(false, null, null));

        public Task<SandboxInfo> EnsureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxInfo("s", ExecutionId.ToString(), "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<SandboxInfo> GetAsync(CancellationToken cancellationToken) => EnsureAsync(cancellationToken);

        public Task<SandboxStatusResponse> StatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxStatusResponse(true, true, true, true, "c", ".", DateTime.UtcNow, DateTime.UtcNow));

        public Task<SandboxActionResult> WriteTextAsync(string path, string content, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxActionResult(true));

        public Task WriteBytesAsync(string path, byte[] content, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SandboxActionResult> DeletePathAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxActionResult(true));

        public Task<SandboxExecResponse> InstallPackagesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxExecResponse("installed", null));

        public Task<SandboxActionResult> CompleteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxActionResult(true));

        public Task<SandboxTransfer> StageFromUrlAsync(SandboxStageRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxTransfer(0, null));

        public Task<SandboxTransfer> PublishToUrlAsync(SandboxPublishRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SandboxTransfer(0, null));
    }
}
