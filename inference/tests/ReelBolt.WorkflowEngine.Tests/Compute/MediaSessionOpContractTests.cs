using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// D11's half of the same drift guard D6 wrote for the job routes: the media session op set and the
/// session-ops wire shape are declared in Go, and this is the check that a rename on either side fails
/// here rather than in a deployment that has a gateway. Nothing here reads a C# constant to check
/// another C# constant - every assertion is against the gateway's and the worker library's own source.
/// </summary>
public class MediaSessionOpContractTests
{
    private static string Go(params string[] parts)
        => RepositoryRoot() + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, parts);

    [Fact]
    public void TheMediaOpSet_StillMatchesTheGoProtocolsOwnConstants()
    {
        string constants = File.ReadAllText(Go("sandbox", "pkg", "protocol", "constants.go"));

        // Each op name this engine sends is declared in pkg/protocol with exactly that spelling.
        foreach (string op in MediaSessionOps.All)
        {
            constants.Should().Contain($"= \"{op}\"", $"sandbox/pkg/protocol must still declare the {op} op");
        }

        // And the protocol still calls this list the MEDIA op set, over the same seven members, in the
        // same order. A reordering is not itself a contract break, but a member appearing or vanishing
        // without this engine noticing is exactly what this test is for.
        constants.Should().Contain("func MediaOps() []string");
        constants.Should().Contain(
            "return []string{OpStage, OpFfmpeg, OpFfprobe, OpRead, OpPublish, OpStat, OpClose}");
    }

    [Fact]
    public void TheWorkerLibrary_CanActuallyHandleEveryOpThisEngineSends()
    {
        // The op table above is a vocabulary; this is the dispatch. An op this engine sends that the
        // worker answers unsupported_op would otherwise fail only at runtime, on a customer's machine.
        string media = File.ReadAllText(Go("sandbox", "pkg", "agent", "media", "media.go"));

        media.Should().Contain("func (s *session) Handle(ctx context.Context, op string, args json.RawMessage)");
        foreach (string op in MediaSessionOps.All)
        {
            string goConstant = "protocol.Op" + char.ToUpperInvariant(op[0]) + op[1..];
            media.Should().Contain($"case {goConstant}:", $"the worker's media session must handle the {op} op");
        }

        // The refusal the engine must be able to read as "this op is not available here at all".
        media.Should().Contain("protocol.ErrCodeUnsupportedOp");
    }

    [Fact]
    public void TheSessionOpsRoute_StillMatchesTheGatewaysOwnRegistrationAndBody()
    {
        string internalApi = File.ReadAllText(Go("sandbox", "cmd", "runner-gateway", "internal_api.go"));
        string sessions = File.ReadAllText(Go("sandbox", "cmd", "runner-gateway", "sessions.go"));

        internalApi.Should().Contain("internal.HandleFunc(\"/sessions/{id}/ops\", g.handleSessionOp).Methods(http.MethodPost)");

        // The request body: the four fields RunSessionOpAsync writes, as JSON tags.
        sessions.Should().Contain("struct {");
        foreach (string tag in new[] { "json:\"opId,omitempty\"", "json:\"op\"", "json:\"args,omitempty\"", "json:\"timeoutSec,omitempty\"" })
        {
            sessions.Should().Contain(tag, "the op request body is what RunSessionOpAsync serializes");
        }

        // The response body: every field RunnerSessionOpResult reads. The tag form is asserted, not the
        // bare word, so a rename cannot hide behind the Go field name.
        foreach (string tag in new[]
        {
            "json:\"sessionId\"", "json:\"jobId\"", "json:\"opId\"", "json:\"ok\"",
            "json:\"result,omitempty\"", "json:\"error,omitempty\"", "json:\"durationMs\""
        })
        {
            sessions.Should().Contain(tag);
        }

        // "A failed op is a successful round trip": the worker's own error is passed through on a 200.
        sessions.Should().Contain("if outcome.payload.OK {");

        // The four session-level refusals the client branches on, and the statuses that carry them.
        foreach (string code in new[]
        {
            SessionOpErrorCodes.SessionNotFound,
            SessionOpErrorCodes.SessionLost,
            SessionOpErrorCodes.OpInFlight,
            SessionOpErrorCodes.OpCancelled,
            SessionOpErrorCodes.OpTimeout,
            SessionOpErrorCodes.SessionOwnerUnreachable
        })
        {
            sessions.Should().Contain($"\"{code}\"", "the gateway answers this code and the client reads it");
        }

        sessions.Should().Contain("http.StatusNotFound");
        sessions.Should().Contain("http.StatusGone");
        sessions.Should().Contain("http.StatusConflict");
        sessions.Should().Contain("http.StatusGatewayTimeout");
        sessions.Should().Contain("http.StatusBadGateway");

        // The op timeout the client renders as a retry-safe refusal, and the op id the retry reuses.
        sessions.Should().Contain("\"op_timeout\"");
        sessions.Should().Contain("opID = uuid.NewString()");
    }

    [Fact]
    public void TheProtocolOpCodesTheEngineBranchesOnAreStillDeclared()
    {
        string constants = File.ReadAllText(Go("sandbox", "pkg", "protocol", "constants.go"));

        // The op-level codes the engine maps onto its own failures. They are an OPEN SET on both sides
        // (an unknown one is an unknown case, never a parse error), so only the ones this engine
        // branches on are pinned.
        foreach (string code in new[] { "invalid_path", "argv_not_allowed", "file_too_large", "unsupported_op", "timeout", "cancelled" })
        {
            constants.Should().Contain($"= \"{code}\"");
        }
    }

    [Fact]
    public void TheWorkerValidatesArgvItself_WhichIsWhyTheEngineRefusesAHostPathBeforeSendingOne()
    {
        // The engine's pre-flight check (RemoteMediaWorkspace.ValidateArgv) is a legibility measure,
        // not the bound: the worker MUST reject the same argv on its own. If that ever stops being
        // true, the engine's check becomes load-bearing and this test is the warning.
        string argv = File.ReadAllText(Go("sandbox", "pkg", "agent", "media", "argv.go"));
        string media = File.ReadAllText(Go("sandbox", "pkg", "agent", "media", "media.go"));

        // The validator exists, refuses with the protocol's own code, and is wired into the op.
        argv.Should().Contain("func ValidateArgv(");
        argv.Should().Contain("argv_not_allowed");
        media.Should().Contain("protocol.ErrCodeArgvNotAllowed");
        media.Should().Contain("ValidateArgv(argv, s.dir)");

        // And the two rules the engine mirrors on the way out: an absolute path, and a traversal.
        argv.Should().Contain("any absolute path");
        argv.Should().Contain(".. component");
    }

    private static string RepositoryRoot()
    {
        string dir = Path.GetDirectoryName(typeof(MediaSessionOpContractTests).Assembly.Location)!;
        for (string? candidate = dir; candidate != null; candidate = Path.GetDirectoryName(candidate))
        {
            if (Directory.Exists(Path.Combine(candidate, "sandbox", "pkg", "protocol")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("could not locate the repository root (no sandbox/pkg/protocol above " + dir + ")");
    }
}
