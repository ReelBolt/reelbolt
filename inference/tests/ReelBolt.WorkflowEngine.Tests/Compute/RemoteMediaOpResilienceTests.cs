using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The 2026-11-17 CloudPool failures: an op the engine abandoned client-side while the worker still ran it,
/// and the next op meeting <c>409 op_in_flight</c>. Each test fails against the code before the fix.
/// </summary>
public class RemoteMediaOpResilienceTests
{
    private sealed class Wire
    {
        public StubHttpMessageHandler Handler = null!;
        public readonly List<string> OpIds = [];
        public readonly List<int> TimeoutSeconds = [];
        public int InFlight;
        public int MaxInFlight;

        public JsonObject Body(int index) => JsonNode.Parse(Handler.Bodies[index])!.AsObject();
    }

    private static (RemoteMediaWorkspace Workspace, Wire Wire) Build(
        Func<Wire, int, CancellationToken, Task<HttpResponseMessage>> answer,
        RemoteMediaOptions? options = null,
        int requestTimeoutSeconds = 30)
    {
        Wire wire = new();
        int call = 0;
        wire.Handler = new StubHttpMessageHandler(async _ =>
        {
            int n = Interlocked.Increment(ref call) - 1;
            JsonObject body = wire.Body(n);
            lock (wire)
            {
                wire.OpIds.Add(body["opId"]!.GetValue<string>());
                wire.TimeoutSeconds.Add(body["timeoutSec"]?.GetValue<int>() ?? 0);
            }

            int now = Interlocked.Increment(ref wire.InFlight);
            wire.MaxInFlight = Math.Max(wire.MaxInFlight, now);
            try
            {
                return await answer(wire, n, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref wire.InFlight);
            }
        });

        ComputeOptions compute = ComputeTestFactory.Options();
        compute.RequestTimeoutSeconds = requestTimeoutSeconds;

        RemoteMediaWorkspace workspace = new(
            MediaSessionTestData.Project,
            MediaSessionTestData.Job,
            MediaSessionTestData.Session,
            RunnerJobTarget.CloudPool,
            ComputeTestFactory.Gateway(wire.Handler, compute),
            new FakeProjectFileWorkspace(),
            new FakePresignedUrlService(),
            new FakeOutputVerifier(),
            new FakeRunnerOutputStore(),
            options ?? new RemoteMediaOptions { OpPollIntervalMilliseconds = 5, OpSettleGraceSeconds = 30 },
            NullLogger.Instance);
        return (workspace, wire);
    }

    private static HttpResponseMessage Stat(bool exists = true)
        => ComputeTestFactory.Json(MediaSessionTestData.OpBody($$"""{"exists":{{(exists ? "true" : "false")}},"bytes":5}"""));

    private static HttpResponseMessage InFlight()
        => ComputeTestFactory.Json(MediaSessionTestData.RefusalBody("op_in_flight"), HttpStatusCode.Conflict);

    [Fact]
    public async Task AnOpWithNoCallerTimeoutStillCarriesAnExplicitDeadlineOnTheWire()
    {
        // Before: timeoutSec was 0 ("the job's own timeout") and the HTTP request got the bare 30 s request
        // timeout, shorter than a large stage.
        (RemoteMediaWorkspace workspace, Wire wire) = Build((_, _, _) => Task.FromResult(Stat()));

        await workspace.StatAsync("clip.mp4", CancellationToken.None);

        wire.TimeoutSeconds.Should().ContainSingle().Which.Should().Be(120);
    }

    [Fact]
    public async Task AnOpWhoseCompletionNeverArrivesIsAskedAgainUnderTheSameOpIdInsteadOfBeingAbandoned()
    {
        // The request hangs (a lost completion, a cut link). The engine's own timeout fires; the op may
        // still be running on the worker, so the answer is asked for again, never a new op.
        (RemoteMediaWorkspace workspace, Wire wire) = Build(
            async (_, n, _) =>
            {
                if (n == 0)
                {
                    await Task.Delay(Timeout.Infinite, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token)
                        .ConfigureAwait(false);
                }

                return Stat();
            },
            new RemoteMediaOptions { OpTimeoutSeconds = 1, SmallOpTimeoutSeconds = 1, OpPollIntervalMilliseconds = 5, OpSettleGraceSeconds = 30 },
            requestTimeoutSeconds: 1);

        MediaWorkspaceStat stat = await workspace.StatAsync("clip.mp4", CancellationToken.None);

        stat.Exists.Should().BeTrue();
        wire.OpIds.Should().HaveCount(2);
        wire.OpIds.Distinct().Should().ContainSingle("the retry reuses the opId, so the worker replays instead of re-executing");
    }

    [Fact]
    public async Task A409OpInFlightIsWaitedOutNotSurfacedAsAFailure()
    {
        // Before: one retry, then the second 409 escaped as an exception and failed the step.
        (RemoteMediaWorkspace workspace, Wire wire) = Build(
            (_, n, _) => Task.FromResult(n < 3 ? InFlight() : Stat()));

        MediaWorkspaceStat stat = await workspace.StatAsync("clip.mp4", CancellationToken.None);

        stat.Bytes.Should().Be(5);
        wire.OpIds.Should().HaveCount(4);
        wire.OpIds.Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task AnOpThatStaysInFlightEndsAtItsDeadlineWithTheGatewaysRefusal()
    {
        (RemoteMediaWorkspace workspace, Wire wire) = Build(
            (_, _, _) => Task.FromResult(InFlight()),
            new RemoteMediaOptions { OpTimeoutSeconds = 1, SmallOpTimeoutSeconds = 1, OpPollIntervalMilliseconds = 20, OpSettleGraceSeconds = 0 });

        Func<Task> act = () => workspace.StatAsync("clip.mp4", CancellationToken.None);

        (await act.Should().ThrowAsync<RunnerSessionOpException>()).Which.Code.Should().Be("op_in_flight");
        wire.OpIds.Count.Should().BeGreaterThan(2, "it kept asking until the deadline");
        wire.OpIds.Count.Should().BeLessThan(200, "and then stopped");
    }

    [Fact]
    public async Task TwoOpsIssuedAtOnceAreSentOneAfterTheOther()
    {
        // The gateway refuses a second concurrent op; the engine must never send one.
        (RemoteMediaWorkspace workspace, Wire wire) = Build(
            async (_, _, _) =>
            {
                await Task.Delay(60).ConfigureAwait(false);
                return Stat();
            });

        await Task.WhenAll(
            workspace.StatAsync("a.mp4", CancellationToken.None),
            workspace.StatAsync("b.mp4", CancellationToken.None),
            workspace.StatAsync("c.mp4", CancellationToken.None));

        wire.MaxInFlight.Should().Be(1);
        wire.OpIds.Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task ASessionLostIsNeverWaitedFor()
    {
        (RemoteMediaWorkspace workspace, Wire wire) = Build(
            (_, _, _) => Task.FromResult(ComputeTestFactory.Json(MediaSessionTestData.RefusalBody("session_lost"), HttpStatusCode.Gone)));

        Func<Task> act = () => workspace.StatAsync("clip.mp4", CancellationToken.None);

        (await act.Should().ThrowAsync<RunnerSessionOpException>()).Which.SessionLost.Should().BeTrue();
        wire.OpIds.Should().ContainSingle();
    }

    [Fact]
    public async Task ASilentGatewayFailsASmallOpAtTheShortDeadlineNotTheNineHundredSecondOne()
    {
        // 2026-11-18 CloudPool compile hang: the session went silent and the engine waited out the op deadline
        // (900 s) and then the 30 min session cap. A stat has a short deadline of its own.
        (RemoteMediaWorkspace workspace, Wire wire) = Build(
            async (_, _, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                return InFlight();
            },
            new RemoteMediaOptions { SmallOpTimeoutSeconds = 1, OpSettleGraceSeconds = 0, OpPollIntervalMilliseconds = 5 },
            requestTimeoutSeconds: 1);

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        Func<Task> act = () => workspace.StatAsync("clip.mp4", CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        wire.TimeoutSeconds.Should().OnlyContain(t => t == 1);
    }
}
