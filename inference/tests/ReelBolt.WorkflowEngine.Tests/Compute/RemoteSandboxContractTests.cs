using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The drift guard D7b owes its Go counterpart, in the same spirit as D6's
/// <c>RunnerGatewayContractTests</c>: the engine's side of the session-op surface is a set of string
/// literals and JSON keys, and nothing in either language's type system connects them to
/// <c>sandbox/pkg/agent/remotion/remotion.go</c>, <c>sandbox/pkg/protocol/constants.go</c> and
/// <c>sandbox/cmd/runner-gateway/sessions.go</c>. So the test reads the Go SOURCE and fails when
/// either side moves.
///
/// <para>
/// It is deliberately BIDIRECTIONAL: it checks that the runner still implements every op this client
/// sends, and that this client's own source still spells each op and each argument exactly as the Go
/// structs' JSON tags say. A one-way check would let a rename on the C# side pass silently, which is
/// precisely the failure that costs a deployment.
/// </para>
/// </summary>
public class RemoteSandboxContractTests
{
    /// <summary>Every op this client can send, spelled as the protocol spells it.</summary>
    private static readonly string[] Ops =
        ["writeFile", "readFile", "listFiles", "deletePath", "exec", "status", "hydrate", "close"];

    /// <summary>
    /// The keys this client builds or digs out of a <c>JsonObject</c> BY NAME, each paired with the Go
    /// file whose struct carries it as a JSON tag. These are the ones a rename can break silently.
    /// </summary>
    private static readonly (string GoFile, string[] Keys)[] ObjectMemberKeys =
    [
        ("sandbox/pkg/agent/remotion/remotion.go",
        [
            // writeFileArgs / readFileArgs / listFilesArgs / deletePathArgs / execArgs
            "path", "content", "encoding", "maxBytes", "depth", "command", "args", "timeoutSec",
            // hydrateArgs + hydrateEntry
            "manifest", "sha256", "url",
            // readFile / exec / status RESULTS (listFiles builds {"entries": …} rather than a struct,
            // and its ENTRIES are agent.Entry — both asserted separately below)
            "bytes", "exitCode", "stdout", "stderr", "workspaceReady"
        ]),
        ("sandbox/pkg/agent/rootfs.go",
        [
            // agent.Entry, the listFiles entry the client unpacks
            "path", "kind", "bytes"
        ])
    ];

    /// <summary>
    /// The keys bound by DESERIALIZATION into a DTO instead of by name, so the link to the Go tag is
    /// the serializer's camelCase policy rather than a string literal. The guard asserts that link
    /// directly: <c>WorkspaceReady</c> is what becomes <c>workspaceReady</c> only because the options
    /// are <c>JsonSerializerDefaults.Web</c>, and that is a one-line change away from being false.
    /// </summary>
    private static readonly (string GoFile, string Tag, string Member)[] BoundKeys =
    [
        ("sandbox/cmd/runner-gateway/sessions.go", "opId", "OpId"),
        ("sandbox/cmd/runner-gateway/sessions.go", "op", "Op"),
        ("sandbox/cmd/runner-gateway/sessions.go", "ok", "Ok"),
        ("sandbox/cmd/runner-gateway/sessions.go", "result", "Result"),
        ("sandbox/cmd/runner-gateway/sessions.go", "error", "Error"),
        ("sandbox/cmd/runner-gateway/sessions.go", "sessionId", "SessionId"),
        ("sandbox/cmd/runner-gateway/sessions.go", "jobId", "JobId"),
        ("sandbox/cmd/runner-gateway/sessions.go", "servedBy", "ServedBy"),
        ("sandbox/cmd/runner-gateway/sessions.go", "durationMs", "DurationMs"),
        ("sandbox/pkg/protocol/messages.go", "code", "Code"),
        ("sandbox/pkg/protocol/messages.go", "message", "Message"),
        ("sandbox/pkg/protocol/messages.go", "retryable", "Retryable")
    ];

    private static string CamelCase(string pascal) => char.ToLowerInvariant(pascal[0]) + pascal[1..];

    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new FileInfo(ThisFile()).Directory;
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sandbox", "pkg", "protocol", "constants.go")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test runs inside the repository, under inference/tests/");
        return dir!.FullName;
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") => sourceFile;

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// The client's own source, so an op name or an argument key that is renamed in C# without being
    /// renamed on the Go side fails here rather than at a customer's first render.
    /// </summary>
    private static string SessionSource()
        => Read("inference/src/ReelBolt.WorkflowEngine/Services/Compute/RemoteSandboxSession.cs");

    [Fact]
    public void Every_op_this_client_sends_is_a_protocol_constant()
    {
        string constants = Read("sandbox/pkg/protocol/constants.go");

        foreach (string op in Ops)
        {
            constants.Should().Contain($"= \"{op}\"", because: $"the protocol must still define the {op} op");
        }
    }

    [Fact]
    public void Every_op_this_client_sends_is_handled_by_the_workspace_executor()
    {
        string remotion = Read("sandbox/pkg/agent/remotion/remotion.go");
        string constants = Read("sandbox/pkg/protocol/constants.go");

        foreach (string op in Ops)
        {
            // The constant's Go name, so the case label can be checked without hard-coding both sides
            // independently: "writeFile" -> OpWriteFile.
            string constantName = "Op" + char.ToUpperInvariant(op[0]) + op[1..];
            constants.Should().Contain($"{constantName} ", because: $"the protocol must name the {op} op as {constantName}");

            remotion.Should().Contain(
                $"case protocol.{constantName}:",
                because: $"the workspace executor must dispatch the {op} op");
        }
    }

    [Fact]
    public void This_client_still_spells_every_op_the_way_the_protocol_does()
    {
        string source = SessionSource();

        foreach (string op in Ops)
        {
            source.Should().Contain($"\"{op}\"", because: $"the client must send the {op} op by that name");
        }
    }

    [Fact]
    public void Every_argument_and_result_key_this_client_names_is_a_JSON_tag_on_the_Go_side()
    {
        string source = SessionSource();

        foreach ((string goFile, string[] keys) in ObjectMemberKeys)
        {
            string go = Read(goFile);

            foreach (string key in keys)
            {
                go.Should().Contain($"json:\"{key}", because: $"{goFile} must carry the {key} field");
                source.Should().Contain($"\"{key}\"", because: $"the client must name the {key} field");
            }
        }
    }

    [Fact]
    public void The_listFiles_result_is_the_entries_array_the_client_unpacks()
    {
        // listFiles is the one result built as a literal map rather than a struct, so it is not covered
        // by the JSON-tag scan above and would drift silently without this.
        Read("sandbox/pkg/agent/remotion/remotion.go").Should().Contain("map[string]any{\"entries\": entries}");
        SessionSource().Should().Contain("[\"entries\"]");
    }

    [Fact]
    public void Every_key_bound_by_deserialization_maps_to_its_Go_tag_through_the_serializer_policy()
    {
        string source = SessionSource();
        source.Should().Contain(
            "JsonSerializerDefaults.Web",
            because: "camelCase binding is the ONLY thing connecting WorkspaceReady to workspaceReady");

        foreach ((string goFile, string tag, string member) in BoundKeys)
        {
            Read(goFile).Should().Contain($"json:\"{tag}", because: $"{goFile} must still send the {tag} field");
            CamelCase(member).Should().Be(tag, because: $"JsonSerializerDefaults.Web turns {member} into {tag}");
            source.Should().Contain(member, because: $"the client must still declare the {member} member");
        }
    }

    [Fact]
    public void The_session_op_route_and_its_request_shape_are_the_ones_the_gateway_serves()
    {
        string internalApi = Read("sandbox/cmd/runner-gateway/internal_api.go");
        internalApi.Should().Contain("\"/sessions/{id}/ops\"");

        SessionSource().Should().Contain("internal/v1/sessions/");
    }

    [Fact]
    public void The_failure_statuses_this_client_classifies_are_the_ones_the_gateway_answers()
    {
        // The difference between these codes is what the engine's recovery policy is built on, so each
        // one is pinned to the Go line that produces it rather than to a comment.
        string sessions = Read("sandbox/cmd/runner-gateway/sessions.go");

        sessions.Should().Contain("http.StatusGone", because: "410 means session_lost, and it is the ONE code that opens a new session");
        sessions.Should().Contain("\"session_lost\"");
        sessions.Should().Contain("\"session_not_found\"");
        sessions.Should().Contain("http.StatusNotFound");
        sessions.Should().Contain("\"op_in_flight\"");
        sessions.Should().Contain("http.StatusConflict");
        sessions.Should().Contain("\"op_cancelled\"");
        sessions.Should().Contain("\"op_timeout\"");
        sessions.Should().Contain("http.StatusGatewayTimeout");
        sessions.Should().Contain("\"session_owner_unreachable\"");
        sessions.Should().Contain("http.StatusBadGateway");
        sessions.Should().Contain("http.StatusOK", because: "a failed op is a 200 with ok:false; only the transport failing is an HTTP error");

        string source = SessionSource();
        // The codes this client names outright. op_in_flight and op_cancelled are NOT among them: both
        // arrive as a 409 and are classified by status, which is asserted instead — a code spelled out
        // but never compared would be dead weight rather than a guard.
        foreach (string code in new[] { "session_lost", "session_not_found", "op_timeout", "session_owner_unreachable", "op_expired", "not_found" })
        {
            source.Should().Contain($"\"{code}\"", because: $"the client must classify the {code} answer");
        }

        source.Should().Contain("HttpStatusCode.Conflict", because: "a 409 is op_in_flight or op_cancelled and is terminal for the call");
        source.Should().Contain("\"conflict\"");
    }

    [Fact]
    public void An_op_id_is_minted_when_the_engine_does_not_supply_one_and_is_reused_on_a_retry()
    {
        // The protocol's rule for a repeated opId is what makes a retry against the SAME session safe;
        // minting a fresh one on every attempt would silently double-execute a render.
        string sessions = Read("sandbox/cmd/runner-gateway/sessions.go");
        sessions.Should().Contain("opID = uuid.NewString()", because: "the gateway mints an opId only when the engine supplies none");

        string source = SessionSource();
        source.Should().Contain("MaxAttempts", because: "the retry policy is the client's, and it is bounded");
        source.Should().Contain("OpId", because: "the client supplies an opId rather than letting the gateway mint one");
    }

    [Fact]
    public void The_hydrate_manifest_is_the_shape_the_workspace_executor_consumes()
    {
        string remotion = Read("sandbox/pkg/agent/remotion/remotion.go");

        remotion.Should().Contain("Manifest []hydrateEntry", because: "hydrate takes a manifest array");
        remotion.Should().Contain("\"restored\"");
        remotion.Should().Contain("\"skipped\"");

        // The arguments the engine must send for each entry.
        remotion.Should().Contain("json:\"sha256\"", because: "a hydrate entry lets the runner SKIP a file it already has with that digest");
        remotion.Should().Contain("json:\"url\"");
    }

    [Fact]
    public void The_op_result_cache_is_bounded_and_op_expired_means_session_loss()
    {
        string constants = Read("sandbox/pkg/protocol/constants.go");
        constants.Should().Contain("OpResultCacheSize");
        constants.Should().Contain("ErrCodeOpExpired");

        // The engine's own reading of that code.
        SessionSource().Should().Contain("op_expired");
    }
}
