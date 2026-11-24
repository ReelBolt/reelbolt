#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The D7a sandbox seam against a REAL container. Nothing here is mocked: the tests drive a running
/// <c>sandbox/cmd/sandbox-executor</c>, which shells out to a real Docker daemon, which starts the
/// real <c>reelbolt-sandbox-runtime</c> image on a real <c>--internal</c> network and runs real
/// <c>npm</c> inside it.
///
/// <para>
/// <b>Why this file exists.</b> Every container in this feature has, until now, been exercised against
/// a fake driver — which is exactly the kind of test that passes while the real system is broken (the
/// reason <c>VoiceoverRealInfrastructureTests</c> exists). The sandbox path is where the engine runs
/// model-authored code, and it is the arm <see cref="LocalSandboxSession"/> — the one every self-host
/// install uses, and the one a fabric deployment falls back to — talks to.
/// </para>
///
/// <para>
/// <b>Gating.</b> Every test is marked <see cref="RealContainerFactAttribute"/>, which sets xUnit's
/// <c>Skip</c> at DISCOVERY time when the environment says there is no executor to drive. So the
/// default <c>dotnet test</c> run reports them as skipped and can never fail because a machine has no
/// Docker daemon.
/// </para>
///
/// <para>
/// <b>Running the gated path.</b> Export <c>REELBOLT_IT_SANDBOX_URL</c> (for example
/// <c>http://localhost:18080</c>) and <c>REELBOLT_IT_SANDBOX_TOKEN</c>, then run
/// <c>dotnet test inference/ReelBolt.sln --filter FullyQualifiedName~SandboxExecutorRealContainerTests</c>.
/// The disposable stack that satisfies both is in this file's git history and in the WP D7b report:
/// a container running <c>reelbolt-sandbox-executor:local</c> with the Docker socket and a host
/// directory bind-mounted at the SAME path it is given as <c>SANDBOX_ROOT</c>. The path has to agree
/// because the executor asks the daemon to bind-mount a workspace directory and the daemon resolves
/// that path on the HOST — otherwise every container starts with an empty workspace.
/// </para>
/// </summary>
public class SandboxExecutorRealContainerTests
{
    /// <summary>
    /// A session whose execution id is FRESH every call. A fixed id would let one test's leftover
    /// workspace — or a container a developer created by hand against the same id — be what the next
    /// test actually reads, and the executor's registry would hand out a container that no longer
    /// exists.
    /// </summary>
    private static LocalSandboxSession Session()
    {
        string url = Environment.GetEnvironmentVariable(SandboxIntegrationEnvironment.BaseUrl)!;
        string token = Environment.GetEnvironmentVariable(SandboxIntegrationEnvironment.ApiToken)!;

        return new LocalSandboxSession(new SimpleHttpClientFactory(), Guid.NewGuid(), url, token);
    }

    [RealContainerFact]
    public async Task A_real_container_starts_and_the_workspace_round_trips()
    {
        LocalSandboxSession session = Session();

        SandboxInfo info = await session.EnsureAsync(CancellationToken.None);
        info.ContainerName.Should().NotBeNullOrWhiteSpace("a real container had to be created");
        info.WorkflowExecutionId.Should().Be(session.ExecutionId.ToString());

        SandboxStatusResponse status = await session.StatusAsync(CancellationToken.None);
        status.Exists.Should().BeTrue();
        status.Ready.Should().BeTrue("the runtime image carries package.json and node_modules");

        const string Source = "export const It = () => null;\n";
        SandboxActionResult written = await session.WriteTextAsync("src/it-check.tsx", Source, CancellationToken.None);
        written.Ok.Should().BeTrue();

        SandboxTextFile read = await session.ReadTextAsync("src/it-check.tsx", CancellationToken.None);
        read.Text.Should().Be(Source, "the bytes had to survive a real container filesystem");

        IReadOnlyList<SandboxFileEntry> entries = await session.ListFilesAsync("src", CancellationToken.None);
        entries.Should().Contain(e => e.Name == "it-check.tsx" && !e.IsDir);

        byte[] bytes = [0x00, 0x01, 0xfe, 0xff];
        await session.WriteBytesAsync("assets/it-check.bin", bytes, CancellationToken.None);
        SandboxTextFile binaryEcho = await session.ReadTextAsync("assets/it-check.bin", CancellationToken.None);
        binaryEcho.Text.Should().Be(
            Encoding.UTF8.GetString(bytes),
            "a binary payload has to survive the base64 arm of writeFile unchanged");

        SandboxActionResult deleted = await session.DeletePathAsync("assets/it-check.bin", CancellationToken.None);
        deleted.Ok.Should().BeTrue();

        SandboxActionResult completed = await session.CompleteAsync(CancellationToken.None);
        completed.Ok.Should().BeTrue("the workspace container had to be destroyed again");
    }

    /// <summary>
    /// The arm a RENDER takes: a raw byte stream out of the workspace, which would silently truncate a
    /// video if it were served by a base64 JSON round trip instead of a stream.
    ///
    /// <para>
    /// <b>This test needs an executor image built from the CURRENT source.</b>
    /// <c>GET /api/v1/sandboxes/{id}/files/raw</c> arrived with D4a's driver extraction, so a
    /// <c>reelbolt-sandbox-executor:local</c> image older than that answers mux's default
    /// <c>404 page not found</c> while <c>/files/content</c> keeps working. Check before blaming the
    /// engine: <c>docker run --rm --entrypoint sh reelbolt-sandbox-executor:local -c "grep -c
    /// \"files/raw\" /app/sandbox-executor"</c> — anything other than 0 means the image is current.
    /// </para>
    /// </summary>
    [RealContainerFact]
    public async Task A_raw_file_streams_out_of_the_container_without_a_base64_round_trip()
    {
        LocalSandboxSession session = Session();
        await session.EnsureAsync(CancellationToken.None);

        byte[] bytes = [0x00, 0x01, 0xfe, 0xff];
        await session.WriteBytesAsync("assets/it-raw.bin", bytes, CancellationToken.None);

        await using SandboxRawFile raw = await session.OpenRawFileAsync("assets/it-raw.bin", 120, CancellationToken.None);
        raw.ContentLength.Should().Be(bytes.Length);

        using MemoryStream buffer = new();
        await raw.Content.CopyToAsync(buffer, CancellationToken.None);
        buffer.ToArray().Should().Equal(bytes);

        await session.CompleteAsync(CancellationToken.None);
    }

    [RealContainerFact]
    public async Task An_allowlisted_command_really_runs_inside_the_container()
    {
        LocalSandboxSession session = Session();
        await session.EnsureAsync(CancellationToken.None);

        // "npm run compositions" is on the exec allowlist (container.ValidateExec) and needs the real
        // Remotion CLI, the real template and the pre-downloaded headless shell, none of which a fake
        // driver can stand in for.
        SandboxExecResult result = await session.ExecAsync("npm", ["run", "compositions"], 180, CancellationToken.None);

        result.TransportError.Should().BeNull();
        result.Succeeded.Should().BeTrue("a real docker exec of npm run compositions had to succeed; got: " + result.RawBody);
        ((result.Payload.Output ?? string.Empty) + (result.Payload.Error ?? string.Empty)).Trim()
            .Should().NotBeNullOrWhiteSpace();

        await session.CompleteAsync(CancellationToken.None);
    }

    [RealContainerFact]
    public async Task A_caller_written_package_script_is_what_runs_because_the_allowlist_is_not_isolation()
    {
        // The documented security position, exercised against the real thing: "npm run build" executes
        // whatever the caller wrote into package.json, which is why the CONTAINER is the containment.
        // This test proves the first half — the script really is the caller's — and asserts nothing
        // about the second, which is the driver's own test surface.
        LocalSandboxSession session = Session();
        await session.EnsureAsync(CancellationToken.None);

        await session.WriteTextAsync("package.json", CallerScript, CancellationToken.None);

        SandboxExecResult result = await session.ExecAsync("npm", ["run", "typecheck"], 120, CancellationToken.None);

        result.TransportError.Should().BeNull();
        result.Succeeded.Should().BeTrue("the caller's own script had to run; got: " + result.RawBody);
        result.RawBody.Should().Contain("REELBOLT_REAL_CONTAINER_OK");

        await session.CompleteAsync(CancellationToken.None);
    }

    /// <summary>The caller-authored package.json: its typecheck script prints a marker and nothing else.</summary>
    private const string CallerScript =
        "{\"name\":\"it\",\"private\":true,\"type\":\"module\",\"scripts\":{\"typecheck\":\"node -e \\\"console.log('REELBOLT_REAL_CONTAINER_OK')\\\"\"}}";

    /// <summary>
    /// The engine's own <c>IHttpClientFactory</c> usage without a DI container: this test is about the
    /// container the session drives, not about how the client is resolved.
    /// </summary>
    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

/// <summary>
/// The environment contract of <see cref="SandboxExecutorRealContainerTests"/>. Centralised so the
/// skip message and the assertions can never disagree about which variables are required.
/// </summary>
public static class SandboxIntegrationEnvironment
{
    public const string BaseUrl = "REELBOLT_IT_SANDBOX_URL";
    public const string ApiToken = "REELBOLT_IT_SANDBOX_TOKEN";

    public static readonly string[] RequiredVariables = [BaseUrl, ApiToken];

    public static string[] MissingVariables() =>
        RequiredVariables
            .Where(v => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
            .ToArray();
}

/// <summary>
/// Skips at DISCOVERY time when no live sandbox-executor is configured, so the default suite is
/// unaffected and the gated path is one export away.
/// </summary>
public sealed class RealContainerFactAttribute : FactAttribute
{
    public RealContainerFactAttribute()
    {
        string[] missing = SandboxIntegrationEnvironment.MissingVariables();
        if (missing.Length > 0)
        {
            Skip = "Requires a running sandbox-executor driving a REAL Docker daemon (no container is "
                + "started otherwise). Missing environment variable(s): " + string.Join(", ", missing)
                + ". See SandboxExecutorRealContainerTests for the disposable stack to run this against.";
        }
    }
}
