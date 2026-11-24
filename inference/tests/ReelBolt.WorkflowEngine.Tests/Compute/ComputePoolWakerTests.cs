using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The engine side of the pool waker: the selector keeps an empty-pool decision on CloudPool only when
/// <c>Compute:PoolWaker:Enabled</c> is on AND the gateway reports <c>pool.wakerHealthy</c>; the media
/// factory waits a bounded time for the woken node and then runs Local. Everything else is today's
/// behaviour, which the flag-off rows pin.
/// </summary>
public class ComputePoolWakerTests
{
    private static readonly Guid Execution = Guid.Parse("e0000000-0000-4000-8000-000000000001");
    private static readonly Guid Org = Guid.Parse("0a000000-0000-4000-8000-000000000002");
    private static readonly Guid Step = Guid.Parse("5e000000-0000-4000-8000-000000000003");
    private static readonly Guid Project = Guid.Parse("90000000-0000-4000-8000-000000000004");
    private static readonly Guid Job = Guid.Parse("1b000000-0000-4000-8000-000000000005");

    private static ComputeTargetRequest SelectorRequest(RunnerJobType jobType = RunnerJobType.VideoAnalyzeExtract)
        => new(Execution, Org, jobType, Step, "VideoAnalyze", 0, "video.analyze");

    private static async Task<ComputeDecision> Decide(
        bool flag, bool? wakerHealthy, bool poolOnline, ComputePreference preference, bool poolInResponse = true, RunnerJobType jobType = RunnerJobType.VideoAnalyzeExtract)
    {
        List<(string Kind, Guid? OrgId, string[] JobTypes)> runners = [];
        if (poolOnline)
        {
            runners.Add(("CloudPool", null, ComputeTestFactory.AllJobTypes));
        }

        string body = wakerHealthy is { } healthy && poolInResponse
            ? ComputeTestFactory.RunnersBodyWithPool(healthy, [.. runners])
            : ComputeTestFactory.RunnersBody([.. runners]);
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(body)));
        ComputeOptions options = ComputeTestFactory.Options();
        options.PoolWaker.Enabled = flag;
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(new ComputeEntitlements(preference, true, true)),
            ComputeTestFactory.Gateway(handler, options),
            options);
        return await selector.ResolveAsync(SelectorRequest(jobType));
    }

    [Theory]
    [InlineData(ComputePreference.CloudOnly)]
    [InlineData(ComputePreference.PreferRunner)]
    [InlineData(ComputePreference.Auto)]
    public async Task EmptyPool_FlagOnAndWakerHealthy_KeepsCloudPoolAsWakeAndWait(ComputePreference preference)
    {
        ComputeDecision d = await Decide(flag: true, wakerHealthy: true, poolOnline: false, preference);

        d.Target.Should().Be(ComputeTargetKind.CloudPool);
        d.Reason.Should().Be(ComputeDecisionReasons.PoolWakeAndWait);
        d.IsPoolWakeAndWait.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, true, false)]   // flag off, waker healthy, pool empty
    [InlineData(false, false, false)]  // flag off, waker unhealthy, pool empty
    [InlineData(true, false, false)]   // flag on, waker unhealthy/capped, pool empty
    public async Task EmptyPool_FlagOffOrWakerUnhealthy_IsTodaysLocalFallback(bool flag, bool wakerHealthy, bool poolOnline)
    {
        foreach (ComputePreference preference in new[] { ComputePreference.CloudOnly, ComputePreference.PreferRunner, ComputePreference.Auto })
        {
            ComputeDecision d = await Decide(flag, wakerHealthy, poolOnline, preference);

            d.Should().Be(ComputeDecision.Local(ComputeDecisionReasons.PoolEmptyLocalFallback));
            d.IsPoolWakeAndWait.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task PoolOnline_IgnoresTheWaker_AndKeepsTheOrdinaryReason(bool flag, bool wakerHealthy)
    {
        ComputeDecision d = await Decide(flag, wakerHealthy, poolOnline: true, ComputePreference.CloudOnly);

        d.Should().Be(ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.PreferenceCloudOnly));
    }

    [Fact]
    public async Task AnOldGatewayWithoutAPoolBlock_ReadsAsWakerUnhealthy_EvenWithTheFlagOn()
    {
        ComputeDecision d = await Decide(flag: true, wakerHealthy: true, poolOnline: false, ComputePreference.Auto, poolInResponse: false);

        d.Should().Be(ComputeDecision.Local(ComputeDecisionReasons.PoolEmptyLocalFallback));
    }

    [Fact]
    public void TheFlagDefaultsAreOffAnd240Seconds()
    {
        new ComputeOptions().PoolWaker.Enabled.Should().BeFalse();
        new ComputeOptions().PoolWaker.WaitSeconds.Should().Be(240);
    }

    [Fact]
    public async Task WakerHealthyLivesInsideThePresenceCache()
    {
        string body = ComputeTestFactory.RunnersBodyWithPool(true);
        StubHttpMessageHandler handler = new(_ => Task.FromResult(ComputeTestFactory.Json(body)));
        ComputeOptions options = ComputeTestFactory.Options();
        options.PoolWaker.Enabled = true;
        ComputeTargetSelector selector = ComputeTestFactory.Selector(
            new FixedComputeEntitlements(ComputeEntitlements.Unlimited),
            ComputeTestFactory.Gateway(handler, options),
            options,
            new TestTimeProvider(DateTimeOffset.UnixEpoch));

        ComputeDecision first = await selector.ResolveAsync(SelectorRequest());
        ComputeDecision second = await selector.ResolveAsync(SelectorRequest());

        first.IsPoolWakeAndWait.Should().BeTrue();
        second.IsPoolWakeAndWait.Should().BeTrue();
        handler.Requests.Count.Should().Be(1);
    }

    // --- the wait, with fake time and a fake gateway -----------------------

    private static async Task<(MediaWorkspaceOpenResult Result, List<(HttpMethod Method, string Path)> Calls)> OpenWake(
        Func<TestTimeProvider, HttpRequestMessage, HttpResponseMessage> gateway,
        int waitSeconds = 240,
        int assignWaitSeconds = 300)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "pw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            TestTimeProvider time = new(DateTimeOffset.UnixEpoch);
            List<(HttpMethod Method, string Path)> calls = [];
            StubHttpMessageHandler handler = new(request =>
            {
                calls.Add((request.Method, request.RequestUri!.AbsolutePath));
                return Task.FromResult(gateway(time, request));
            });
            ComputeOptions compute = ComputeTestFactory.Options(jobWaitSeconds: 0);
            compute.PoolWaker.Enabled = true;
            compute.PoolWaker.WaitSeconds = waitSeconds;
            MediaWorkspaceFactory factory = new(
                new Fixed(ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.PoolWakeAndWait)),
                ComputeTestFactory.Gateway(handler, compute, new ExecutionSessionGate(Options.Create(compute))),
                new MediaWorkspaceFactoryTests.NoopVideoToolRunner(),
                new FakeProjectFileWorkspace(),
                new FakePresignedUrlService(),
                new FakeOutputVerifier(),
                new FakeRunnerOutputStore(),
                Options.Create(new VideoEditingOptions { ScratchPath = scratch, AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 }),
                Options.Create(new RemoteMediaOptions { AssignWaitSeconds = assignWaitSeconds }),
                NullLogger<MediaWorkspaceFactory>.Instance,
                sandboxSessions: null,
                computeOptions: Options.Create(compute),
                time: time);

            MediaWorkspaceOpenResult result = await factory.OpenAsync(new MediaWorkspaceRequest(
                Execution, Org, Project, Step, 3, RunnerJobType.VideoAnalyzeExtract, "video.analyze:3"));
            if (result.Lease is { } lease)
            {
                await lease.DisposeAsync();
            }

            return (result, calls);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class Fixed(ComputeDecision decision) : IComputeTarget
    {
        public ValueTask<ComputeDecision> ResolveAsync(ComputeTargetRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(decision);
    }

    [Fact]
    public async Task NoWorkerWithinWaitSeconds_CancelsTheQueuedJob_AndRunsLocalWithTheTimeoutReason()
    {
        // Each poll of the queued job moves the fake clock 30 s; 240 s allowed => gives up on the 8th.
        (MediaWorkspaceOpenResult result, List<(HttpMethod Method, string Path)> calls) = await OpenWake((time, request) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                time.Advance(TimeSpan.FromSeconds(30));
            }

            return ComputeTestFactory.Json(ComputeTestFactory.JobBody(
                Job, status: request.Method == HttpMethod.Delete ? "Cancelled" : "Queued", jobType: "video.analyze.extract"));
        });

        result.Refused.Should().BeFalse("a wake timeout never becomes a user-facing failure");
        result.Lease!.Workspace.Should().BeOfType<LocalMediaWorkspace>();
        result.Lease.Decision.Should().Be(ComputeDecision.Local(ComputeDecisionReasons.PoolWakeTimeoutLocalFallback));
        calls.Should().Contain(c => c.Method == HttpMethod.Delete);
        calls.FindAll(c => c.Method == HttpMethod.Get).Count.Should().BeLessThanOrEqualTo(9);
    }

    [Fact]
    public async Task WaitIsBoundedByWaitSeconds_NotTheLongerAssignWindow()
    {
        int gets = 0;
        (MediaWorkspaceOpenResult result, _) = await OpenWake(
            (time, request) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    gets++;
                    time.Advance(TimeSpan.FromSeconds(60));
                }

                return ComputeTestFactory.Json(ComputeTestFactory.JobBody(Job, status: request.Method == HttpMethod.Delete ? "Cancelled" : "Queued"));
            },
            waitSeconds: 60,
            assignWaitSeconds: 3000);

        result.Lease!.Decision.Reason.Should().Be(ComputeDecisionReasons.PoolWakeTimeoutLocalFallback);
        gets.Should().BeLessThanOrEqualTo(2, "a 60 s wait must not poll for 3000 s");
    }

    [Fact]
    public async Task AWorkerThatTakesTheJobInTime_RunsOnCloudPool_AndNothingIsCancelled()
    {
        int gets = 0;
        (MediaWorkspaceOpenResult result, List<(HttpMethod Method, string Path)> calls) = await OpenWake((time, request) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                gets++;
                time.Advance(TimeSpan.FromSeconds(100));
                return ComputeTestFactory.Json(ComputeTestFactory.JobBody(
                    Job, sessionId: gets >= 2 ? Guid.NewGuid() : null, status: gets >= 2 ? "Running" : "Queued"));
            }

            return ComputeTestFactory.Json(ComputeTestFactory.JobBody(Job, status: "Queued"));
        });

        result.Refused.Should().BeFalse();
        result.Lease!.Workspace.Should().BeOfType<RemoteMediaWorkspace>();
        result.Lease.Decision.Reason.Should().Be(ComputeDecisionReasons.PoolWakeAndWait);
        calls.Should().NotContain(c => c.Method == HttpMethod.Delete && c.Path.EndsWith(Job.ToString("D")));
    }

    [Fact]
    public async Task AJobAlreadyRunningOnAWorker_IsNeverAbandonedByTheWakeTimeout()
    {
        // Assigned on the very first read although the clock is already past the wake window.
        (MediaWorkspaceOpenResult result, _) = await OpenWake(
            (time, request) =>
            {
                time.Advance(TimeSpan.FromSeconds(600));
                return ComputeTestFactory.Json(ComputeTestFactory.JobBody(Job, sessionId: Guid.NewGuid(), status: "Running"));
            },
            waitSeconds: 60);

        result.Lease!.Workspace.Should().BeOfType<RemoteMediaWorkspace>();
    }

    [Fact]
    public async Task ARemotionWorkspaceSession_NeverWakesAndWaits_ItKeepsTodaysLocalFallback()
    {
        ComputeDecision d = await Decide(true, true, false, ComputePreference.Auto, jobType: RunnerJobType.RemotionWorkspaceSession);

        d.IsPoolWakeAndWait.Should().BeFalse();
        d.Target.Should().Be(ComputeTargetKind.Local);
    }

    [Fact]
    public async Task AJobAWorkerHoldsPastTheWindow_GetsAGraceForItsSessionId_NotACancel()
    {
        int gets = 0;
        (MediaWorkspaceOpenResult result, List<(HttpMethod Method, string Path)> calls) = await OpenWake(
            (time, request) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    gets++;
                    time.Advance(TimeSpan.FromSeconds(100));
                }

                return ComputeTestFactory.Json(ComputeTestFactory.JobBody(
                    Job, sessionId: gets >= 2 ? Guid.NewGuid() : null, status: "Assigned", jobType: "video.analyze.extract"));
            },
            waitSeconds: 60);

        result.Lease!.Workspace.Should().BeOfType<RemoteMediaWorkspace>();
        calls.Should().NotContain(c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task TheGatewayRefusingTheWakeJob_DegradesToLocal()
    {
        (MediaWorkspaceOpenResult result, _) = await OpenWake((_, _) =>
            ComputeTestFactory.Json("""{"error":"bad_request"}""", HttpStatusCode.BadRequest));

        result.Refused.Should().BeFalse();
        result.Lease!.Decision.Reason.Should().Be(ComputeDecisionReasons.PoolWakeTimeoutLocalFallback);
    }
}
