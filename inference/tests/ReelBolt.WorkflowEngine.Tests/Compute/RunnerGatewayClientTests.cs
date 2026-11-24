using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The engine side of the D5b internal API. Two things are being pinned here: the WIRE CONTRACT
/// (routes, JSON field names, idempotency framing, the 503's meaning) and the rule that creating a
/// job and taking the execution's one session slot are the same call.
/// </summary>
public class RunnerGatewayClientTests
{
    private static readonly Guid Execution = Guid.Parse("e0000000-0000-4000-8000-000000000001");
    private static readonly Guid Org = Guid.Parse("0a000000-0000-4000-8000-000000000002");
    private static readonly Guid Step = Guid.Parse("5e000000-0000-4000-8000-000000000003");

    private static RunnerJobRequest Request(
        RunnerJobTarget target = RunnerJobTarget.CloudPool,
        string purpose = "video.compile",
        int iteration = 0)
        => new(Execution, Org, RunnerJobType.VideoCompile, target, StepId: Step, Iteration: iteration, Purpose: purpose);

    // --- Idempotency -------------------------------------------------------

    [Fact]
    public void IdempotencyKey_MatchesTheGatewayGoldenVector_ByteForByte()
    {
        // The SAME vector as TestIdempotencyKeyIsTheSpecifiedDigest in
        // sandbox/cmd/runner-gateway/queue_unit_test.go. The gateway computes this digest itself when
        // a caller supplies none, so the two implementations agreeing on the framing is what makes a
        // retry after a timeout return the SAME job instead of creating a second one.
        RunnerGatewayClient.BuildIdempotencyKey("exec-1", "step-1", 2, "video.compile")
            .Should().Be("b53f9706565bd638c0c6b030b2b1fa5e998ecf6366e9d602007684304ca60e2c");
    }

    [Fact]
    public void IdempotencyKey_DiffersForEveryComponent()
    {
        string baseline = RunnerGatewayClient.BuildIdempotencyKey("exec-1", "step-1", 2, "video.compile");

        RunnerGatewayClient.BuildIdempotencyKey("exec-2", "step-1", 2, "video.compile").Should().NotBe(baseline);
        RunnerGatewayClient.BuildIdempotencyKey("exec-1", "step-2", 2, "video.compile").Should().NotBe(baseline);
        RunnerGatewayClient.BuildIdempotencyKey("exec-1", "step-1", 3, "video.compile").Should().NotBe(baseline);
        RunnerGatewayClient.BuildIdempotencyKey("exec-1", "step-1", 2, "video.analyze.extract").Should().NotBe(baseline);
    }

    [Fact]
    public void IdempotencyKey_FormatsAnExecutionScopedJobWithAnEmptyStepComponent()
    {
        // An execution-scoped session (remotion.workspace-session) has no step, and the gateway
        // receives the empty string for that component, so the framing must be the empty string too.
        RunnerGatewayClient.BuildIdempotencyKey(Execution, null, 0, "remotion.workspace-session")
            .Should().Be(RunnerGatewayClient.BuildIdempotencyKey(Execution.ToString("D"), string.Empty, 0, "remotion.workspace-session"));
    }

    // --- Creating a job ----------------------------------------------------

    [Fact]
    public async Task OpenJob_PostsTheJobAndTakesTheExecutionsSessionSlot()
    {
        Guid jobId = Guid.NewGuid();
        JsonObject? sent = null;
        StubHttpMessageHandler handler = new(async request =>
        {
            sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            return ComputeTestFactory.Json(ComputeTestFactory.JobBody(jobId, target: "OrgRunner"), HttpStatusCode.Created);
        });

        ComputeOptions options = ComputeTestFactory.Options();
        ExecutionSessionGate gate = new(Options.Create(options));
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options, gate);

        await using RunnerJobSession session = await client.OpenJobAsync(
            Request(RunnerJobTarget.OrgRunner) with { Spec = JsonNode.Parse("""{"executionId":"x","stepId":"y","mode":"Reencode"}""") });

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/internal/v1/jobs");
        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("test-internal-token");

        sent!["orgId"]!.GetValue<string>().Should().Be(Org.ToString("D"));
        sent["executionId"]!.GetValue<string>().Should().Be(Execution.ToString("D"));
        sent["stepId"]!.GetValue<string>().Should().Be(Step.ToString("D"));
        sent["jobType"]!.GetValue<string>().Should().Be("video.compile");
        sent["target"]!.GetValue<string>().Should().Be("OrgRunner");
        sent["purpose"]!.GetValue<string>().Should().Be("video.compile");
        sent["idempotencyKey"]!.GetValue<string>()
            .Should().Be(RunnerGatewayClient.BuildIdempotencyKey(Execution, Step, 0, "video.compile"));
        sent["spec"]!["mode"]!.GetValue<string>().Should().Be("Reencode");

        session.JobId.Should().Be(jobId);
        gate.IsHeld(Execution).Should().BeTrue("the slot is held for the whole life of the job");
    }

    [Fact]
    public async Task OpenJob_ReleasesTheSlotWhenCreationFails()
    {
        // A wedged execution would be worse than a failed step: if creating the job throws, the slot
        // must come straight back.
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json("""{"error":"bad_request"}""", HttpStatusCode.BadRequest)));
        ComputeOptions options = ComputeTestFactory.Options();
        ExecutionSessionGate gate = new(Options.Create(options));
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options, gate);

        Func<Task> act = () => client.OpenJobAsync(Request());

        await act.Should().ThrowAsync<RunnerGatewayRequestException>();
        gate.HeldExecutionCount.Should().Be(0);
        gate.IsHeld(Execution).Should().BeFalse();
    }

    [Fact]
    public async Task OpenJob_503_MeansTheFabricIsNotDeployed()
    {
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(
            """{"error":"internal_api_disabled","message":"RUNNER_INTERNAL_TOKEN is not configured, so the internal API is closed"}""",
            HttpStatusCode.ServiceUnavailable)));
        ComputeOptions options = ComputeTestFactory.Options();
        ExecutionSessionGate gate = new(Options.Create(options));
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options, gate);

        Func<Task> act = () => client.OpenJobAsync(Request());

        ComputeFabricUnavailableException ex = (await act.Should().ThrowAsync<ComputeFabricUnavailableException>()).Subject.Single();
        ex.StatusCode.Should().Be(503);
        gate.HeldExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task AnUnreachableGateway_AlsoReadsAsTheFabricBeingUnavailable()
    {
        StubHttpMessageHandler handler = new(_ => throw new HttpRequestException("connection refused"));
        ComputeOptions options = ComputeTestFactory.Options();
        ExecutionSessionGate gate = new(Options.Create(options));
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler, options, gate);

        Func<Task> act = () => client.OpenJobAsync(Request());

        // "There is no usable gateway here" is one answer, whatever the transport said: DNS,
        // connection refused, TLS and a timeout all read the same as D5b's 503.
        await act.Should().ThrowAsync<ComputeFabricUnavailableException>();
        gate.HeldExecutionCount.Should().Be(0);
    }

    // --- Reading and cancelling --------------------------------------------

    [Fact]
    public async Task WaitForJob_LongPollsAndReportsATimeoutAsData()
    {
        Guid jobId = Guid.NewGuid();
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(
            ComputeTestFactory.JobBody(jobId, status: "Running", created: false, timedOut: true))));
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler);

        RunnerJobView? view = await client.WaitForJobAsync(jobId);

        view.Should().NotBeNull();
        view!.Status.Should().Be(RunnerJobStatus.Running);
        view.TimedOut.Should().BeTrue("a long-poll that ran out of window is not a failure; the caller re-polls");
        view.IsTerminal.Should().BeFalse();
        handler.Requests[0].RequestUri!.Query.Should().Be("?wait=30s");
    }

    [Fact]
    public async Task GetJob_MapsEveryTerminalState_AndAnUnknownOneIsNotTerminal()
    {
        foreach ((string wire, RunnerJobStatus expected) in new[]
        {
            ("Queued", RunnerJobStatus.Queued),
            ("Assigned", RunnerJobStatus.Assigned),
            ("Running", RunnerJobStatus.Running),
            ("Succeeded", RunnerJobStatus.Succeeded),
            ("Failed", RunnerJobStatus.Failed),
            ("Cancelled", RunnerJobStatus.Cancelled),
            ("Expired", RunnerJobStatus.Expired)
        })
        {
            Guid jobId = Guid.NewGuid();
            StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(ComputeTestFactory.JobBody(jobId, status: wire))));
            RunnerJobView? view = await ComputeTestFactory.Gateway(handler).GetJobAsync(jobId);

            view!.Status.Should().Be(expected);
            view.IsTerminal.Should().Be(expected is RunnerJobStatus.Succeeded or RunnerJobStatus.Failed or RunnerJobStatus.Cancelled or RunnerJobStatus.Expired);
        }

        Guid unknown = Guid.NewGuid();
        StubHttpMessageHandler unknownHandler = new(_ => Task.FromResult(ComputeTestFactory.Json(ComputeTestFactory.JobBody(unknown, status: "SomethingNew"))));
        RunnerJobView? unknownView = await ComputeTestFactory.Gateway(unknownHandler).GetJobAsync(unknown);

        unknownView!.Status.Should().BeNull("an unrecognised status word is an unknown case, not a parse error");
        unknownView.IsTerminal.Should().BeFalse("a job we cannot read is never assumed to have ended");
    }

    [Fact]
    public async Task GetJob_ReturnsNullForAJobTheGatewayDoesNotKnow()
    {
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json("""{"error":"job_not_found"}""", HttpStatusCode.NotFound)));

        (await ComputeTestFactory.Gateway(handler).GetJobAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task CancelJob_DeletesTheJobAndReturnsItsNewState()
    {
        Guid jobId = Guid.NewGuid();
        StubHttpMessageHandler handler = new(request => Task.FromResult(request.Method == HttpMethod.Delete
            ? ComputeTestFactory.Json(ComputeTestFactory.JobBody(jobId, status: "Cancelled", created: false), HttpStatusCode.Accepted)
            : ComputeTestFactory.Json(ComputeTestFactory.JobBody(jobId))));
        RunnerGatewayClient client = ComputeTestFactory.Gateway(handler);

        RunnerJobView? view = await client.CancelJobAsync(jobId);

        handler.Requests[0].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be($"/internal/v1/jobs/{jobId:D}");
        view!.Status.Should().Be(RunnerJobStatus.Cancelled);
    }

    [Fact]
    public async Task SessionLostIsCarriedForAnExpiredJob()
    {
        Guid jobId = Guid.NewGuid();
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(
            ComputeTestFactory.JobBody(jobId, status: "Expired", sessionLost: true, created: false))));

        RunnerJobView? view = await ComputeTestFactory.Gateway(handler).GetJobAsync(jobId);

        view!.SessionLost.Should().BeTrue();
        view.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task ARefusedRequest_IsARealErrorWithItsStatus()
    {
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized)));

        Func<Task> act = () => ComputeTestFactory.Gateway(handler).OpenJobAsync(Request());

        RunnerGatewayRequestException ex = (await act.Should().ThrowAsync<RunnerGatewayRequestException>()).Subject.Single();
        ex.StatusCode.Should().Be(401);
        ex.Message.Should().Contain("401");
    }

    // --- Runners ------------------------------------------------------------

    [Fact]
    public async Task ListRunners_BuildsTheOrgAndCapabilityQuery_AndParsesTheFleet()
    {
        Guid runnerId = Guid.NewGuid();
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(JsonSerializer.Serialize(new
        {
            online = 1,
            runners = new[]
            {
                new
                {
                    runnerId,
                    kind = "Desktop",
                    orgId = Org,
                    name = "studio-mac",
                    freeSlots = 2,
                    running = 1,
                    draining = false,
                    connectedAt = DateTimeOffset.UnixEpoch,
                    jobTypes = new[] { "video.compile" },
                    container = "docker",
                    agentVersion = "1.2.3"
                }
            }
        }))));

        RunnerFleet fleet = await ComputeTestFactory.Gateway(handler).ListRunnersAsync(Org, ["video.compile"]);

        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be($"/internal/v1/runners?org={Org:D}&caps=video.compile");
        fleet.Online.Should().Be(1);
        fleet.Runners.Should().ContainSingle();
        fleet.Runners[0].RunnerId.Should().Be(runnerId);
        fleet.Runners[0].Kind.Should().Be(RunnerKind.Desktop);
        fleet.Runners[0].OrganizationId.Should().Be(Org);
        fleet.Runners[0].JobTypes.Should().Contain("video.compile");
        fleet.HasEligible(RunnerKind.Desktop, ["video.compile"]).Should().BeTrue();
        fleet.HasEligible(RunnerKind.Desktop, ["video.analyze.extract"]).Should().BeFalse();
        fleet.HasEligible(RunnerKind.CloudPool, ["video.compile"]).Should().BeFalse();
    }

    [Fact]
    public async Task ListRunners_WithoutAnOrganization_AsksForTheWholeFleet()
    {
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json("""{"online":0,"runners":[]}""")));

        RunnerFleet fleet = await ComputeTestFactory.Gateway(handler).ListRunnersAsync(null);

        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/internal/v1/runners");
        fleet.Online.Should().Be(0);
        fleet.Runners.Should().BeEmpty();
    }

    // --- Contract drift -----------------------------------------------------

    /// <summary>
    /// The engine's copy of the gateway's wire contract, checked against the gateway's own source.
    /// D5b's four routes and the JSON field names this client reads and writes are declared in
    /// <c>sandbox/cmd/runner-gateway/internal_api.go</c> and <c>queue.go</c>; a rename on either side
    /// has to fail here rather than at runtime in a deployment that has a gateway.
    /// </summary>
    [Fact]
    public void TheClientsWireContract_StillMatchesTheGatewaysSource()
    {
        string internalApi = File.ReadAllText(Path.Combine(RepositoryRoot(), "sandbox", "cmd", "runner-gateway", "internal_api.go"));
        string queue = File.ReadAllText(Path.Combine(RepositoryRoot(), "sandbox", "cmd", "runner-gateway", "queue.go"));

        // The four routes the client calls, exactly as registerInternalRoutes declares them.
        internalApi.Should().Contain("router.PathPrefix(\"/internal/v1\")");
        foreach (string route in new[] { "\"/jobs", "\"/jobs/{id}", "\"/runners" })
        {
            internalApi.Should().Contain(route);
        }

        internalApi.Should().Contain("http.MethodPost").And.Contain("http.MethodGet").And.Contain("http.MethodDelete");

        // The 503 the client treats as "the fabric is not deployed".
        internalApi.Should().Contain("http.StatusServiceUnavailable");
        internalApi.Should().Contain("\"internal_api_disabled\"");

        // The token rides in an Authorization bearer header on both sides.
        internalApi.Should().Contain("const internalTokenHeader = \"Authorization\"");
        internalApi.Should().Contain("strings.TrimPrefix(r.Header.Get(internalTokenHeader), \"Bearer \")");

        // Every field name this client reads. The tag form is asserted, not the bare word, so a
        // rename cannot hide behind the field's Go name.
        foreach (string field in new[]
        {
            "jobId", "sessionId", "status", "jobType", "target", "attempt", "maxAttempts",
            "leaseEpoch", "created", "result", "errorClass", "sessionLost", "timedOut"
        })
        {
            internalApi.Should().Contain($"json:\"{field}", "the gateway's jobView must still carry {field}");
        }

        // Every field name this client writes.
        foreach (string field in new[]
        {
            "idempotencyKey", "orgId", "executionId", "stepId", "jobType", "target",
            "priority", "maxAttempts", "iteration", "purpose", "requiredCaps", "spec"
        })
        {
            internalApi.Should().Contain($"json:\"{field}", "createJobRequest must still accept {field}");
        }

        foreach (string field in new[] { "container", "runtimeImageDigest", "fontsetVersion", "ffmpegMajor", "minOutputBytes" })
        {
            queue.Should().Contain($"json:\"{field}", "RequiredCaps must still carry {field}");
        }

        // The three v1 job types, checked against the Go protocol package rather than against this
        // client's own constants.
        string constants = File.ReadAllText(Path.Combine(RepositoryRoot(), "sandbox", "pkg", "protocol", "constants.go"));
        foreach (string jobType in ComputeJobTypes.All)
        {
            constants.Should().Contain($"= \"{jobType}\"", "sandbox/pkg/protocol must still declare {jobType}");
        }

        ComputeJobTypes.All.Should().HaveCount(3);

        // The gateway's status and target vocabularies.
        foreach (string status in new[] { "Queued", "Assigned", "Running", "Succeeded", "Failed", "Cancelled", "Expired" })
        {
            queue.Should().Contain($"\"{status}\"");
        }

        queue.Should().Contain("targetCloudPool = \"CloudPool\"").And.Contain("targetOrgRunner = \"OrgRunner\"");

        // The idempotency framing the golden vector above depends on.
        queue.Should().Contain("fmt.Sprintf(\"%s|%s|%d|%s\", executionID, stepID, iteration, purpose)");
    }

    private static string RepositoryRoot()
    {
        string dir = Path.GetDirectoryName(typeof(RunnerGatewayClientTests).Assembly.Location)!;
        for (string? candidate = dir; candidate != null; candidate = Path.GetDirectoryName(candidate))
        {
            if (File.Exists(Path.Combine(candidate, "inference", "ReelBolt.sln")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("could not locate the repository root from " + dir);
    }
}
