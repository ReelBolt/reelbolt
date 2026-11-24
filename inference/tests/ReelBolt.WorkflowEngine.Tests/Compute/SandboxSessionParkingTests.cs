using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// The execution's ONE runner-session slot, shared between the Remotion workspace session and the
/// media steps. The headline is the deadlock a live run hit: an Author step held the slot through
/// its workspace session for the whole execution, so the <c>VideoAnalyze</c> step after it waited
/// for a session whose owner had already finished, and failed <c>RUNNER_SESSION_BUSY</c> 15 minutes
/// later. Everything here is composed through the service provider - the same registrations the
/// engine uses - so the wiring between the media factory and the session registry is under test too.
/// </summary>
public class SandboxSessionParkingTests
{
    private static readonly Guid Execution = Guid.Parse("e0000000-0000-4000-8000-0000000000b1");
    private static readonly Guid Project = Guid.Parse("9a000000-0000-4000-8000-0000000000b2");
    private static readonly Guid Org = Guid.Parse("0a000000-0000-4000-8000-0000000000b3");
    private static readonly Guid AnalyzeStep = Guid.Parse("5e000000-0000-4000-8000-0000000000b4");

    private sealed record Harness(
        ServiceProvider Services,
        FakeRunnerGateway Gateway,
        StubHttpMessageHandler GatewayHandler,
        FakeSandboxJournalStore Store,
        FixedComputeTarget Selector,
        StubHttpMessageHandler LocalHandler) : IAsyncDisposable
    {
        public IExecutionSessionGate Gate => Services.GetRequiredService<IExecutionSessionGate>();

        public ISandboxSessionFactory Sessions => Services.GetRequiredService<ISandboxSessionFactory>();

        public ISandboxSessionRegistry Registry => Services.GetRequiredService<ISandboxSessionRegistry>();

        public IMediaWorkspaceFactory Media => Services.GetRequiredService<IMediaWorkspaceFactory>();

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private static Harness Build(
        ComputeMode mode = ComputeMode.Fabric,
        ComputeDecision? decision = null,
        Action<SandboxSessionOptions>? configureSession = null,
        int sessionWaitSeconds = 1)
    {
        FakeSandboxJournalStore store = new();
        FakeRunnerGateway gateway = new(store);
        StubHttpMessageHandler handler = (StubHttpMessageHandler)gateway.BuildHandler();
        FixedComputeTarget selector = new(
            decision ?? ComputeDecision.Runner(RunnerJobTarget.OrgRunner, ComputeDecisionReasons.PreferenceRunner));

        // One second, not the production 900: on the unfixed code the media step waits this long for
        // a slot nobody will release, and the test should say so quickly.
        ComputeOptions compute = ComputeTestFactory.Options(mode: mode, sessionWaitSeconds: sessionWaitSeconds, jobWaitSeconds: 0);
        SandboxSessionOptions sessionOptions = new() { AssignmentWaitSeconds = 5 };
        configureSession?.Invoke(sessionOptions);

        StubHttpMessageHandler local = new(_ => Task.FromResult(ComputeTestFactory.Json("{\"ok\":true}")));

        ServiceCollection services = new();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(compute));
        services.AddSingleton(Options.Create(sessionOptions));
        services.AddSingleton(Options.Create(new RemoteMediaOptions { AssignWaitSeconds = 5 }));
        services.AddSingleton(Options.Create(new VideoEditingOptions { ScratchPath = System.IO.Path.GetTempPath(), AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 }));
        services.AddSingleton<IExecutionSessionGate>(new ExecutionSessionGate(Options.Create(compute)));
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
        services.AddSingleton(sp => ComputeTestFactory.Gateway(handler, compute, sp.GetRequiredService<IExecutionSessionGate>()));
        services.AddSingleton<IComputeTarget>(selector);
        services.AddSingleton<ISandboxExecutionScopeResolver>(new FakeSandboxScopeResolver(Project, Org));
        services.AddSingleton(new SandboxWorkspaceJournal(store));
        services.AddSingleton(new LocalSandboxSessionFactory(new StubHttpClientFactory(local), new ConfigurationBuilder().Build()));
        services.AddSingleton<IPresignedUrlService, FakePresignedUrlService>();
        services.AddSingleton<IRunnerOutputStore, FakeRunnerOutputStore>();
        services.AddSingleton<IOutputVerifier, FakeOutputVerifier>();
        services.AddSingleton<IProjectFileWorkspace, FakeProjectFileWorkspace>();
        services.AddSingleton<IVideoToolRunner, MediaWorkspaceFactoryTests.NoopVideoToolRunner>();
        services.AddSingleton<ComputeSandboxSessionFactory>();
        services.AddSingleton<ISandboxSessionFactory>(sp => sp.GetRequiredService<ComputeSandboxSessionFactory>());
        services.AddSingleton<ISandboxSessionRegistry>(sp => sp.GetRequiredService<ComputeSandboxSessionFactory>());
        services.AddSingleton<IMediaWorkspaceFactory, MediaWorkspaceFactory>();

        return new Harness(services.BuildServiceProvider(), gateway, handler, store, selector, local);
    }

    private static readonly ComputeDecision OnTheRunner =
        ComputeDecision.Runner(RunnerJobTarget.OrgRunner, ComputeDecisionReasons.PreferenceRunner);

    private const string RemotionJob = "remotion.workspace-session";

    private const string AnalyzeJob = "video.analyze.extract";

    /// <summary>Requests that touched the gateway's job routes or a session's close, i.e. what a park costs.</summary>
    private static int ParkTraffic(Harness harness)
        => harness.GatewayHandler.Requests.Count(r =>
               r.Method == HttpMethod.Delete
               || (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("/jobs/", StringComparison.Ordinal)))
           + harness.Gateway.Ops.Count(o => o.Op == "close");

    private static MediaWorkspaceRequest AnalyzeRequest()
        => new(
            ExecutionId: Execution,
            OrganizationId: Org,
            ProjectId: Project,
            StepId: AnalyzeStep,
            StepOrder: 5,
            JobType: RunnerJobType.VideoAnalyzeExtract,
            Purpose: "video.analyze.extract:5");

    // ------------------------------------------------------------------
    // The deadlock
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_runner_backed_media_step_after_a_sandbox_step_gets_the_execution_slot()
    {
        // The live run: step 3 (the Author) wrote its film through a runner-backed workspace session,
        // step 5 (VideoAnalyze) was sent to the same runner, and nothing happened for minutes - the
        // Author's session still held the execution's slot, and nothing was ever going to release it.
        await using Harness harness = Build();
        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "export const Root = 1;", CancellationToken.None);

        MediaWorkspaceOpenResult opened = await harness.Media.OpenAsync(AnalyzeRequest());

        opened.Refused.Should().BeFalse(
            "the sandbox step is over, so its session must give the slot up: {0} {1}", opened.FailureCode, opened.Message);
        await opened.Lease!.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Invariant 1: one open session per execution, at every instant
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_parked_sessions_job_is_ended_before_the_media_job_is_created()
    {
        await using Harness harness = Build();
        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "root", CancellationToken.None);

        MediaWorkspaceOpenResult opened = await harness.Media.OpenAsync(AnalyzeRequest());
        opened.Refused.Should().BeFalse();
        await using MediaWorkspaceLease lease = opened.Lease!;

        // The close op is how the gateway ends a session's job, and the engine read the job back as
        // terminal before it created the next one - not merely after the close op answered, because the
        // gateway finishes the job AFTER it has written the op's answer.
        harness.Gateway.Events.Should().Equal(
            "create:" + RemotionJob,
            "end:" + RemotionJob + ":Succeeded",
            "create:" + AnalyzeJob);
        harness.Gateway.MaxOpenJobs.Should().Be(1, "runner_sessions allows exactly one open session per execution");
        harness.Gateway.Ops.Should().ContainSingle(o => o.Op == "close");
    }

    [Fact]
    public async Task A_close_that_does_not_end_the_job_is_followed_by_a_cancel_and_waits_out_the_grace_window()
    {
        // The runner never acknowledges: the gateway does not finish the job on close, and a cancel
        // only lands once its grace window has passed. The media job must still wait for that.
        await using Harness harness = Build(configureSession: o => o.ParkWaitSeconds = 1);
        harness.Gateway.CloseLeavesJobRunning = true;
        harness.Gateway.CancelGracePolls = 2;

        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "root", CancellationToken.None);

        MediaWorkspaceOpenResult opened = await harness.Media.OpenAsync(AnalyzeRequest());

        opened.Refused.Should().BeFalse("{0} {1}", opened.FailureCode, opened.Message);
        await using MediaWorkspaceLease lease = opened.Lease!;
        harness.GatewayHandler.Requests.Should().Contain(r => r.Method == HttpMethod.Delete);
        harness.Gateway.Events.Should().Equal(
            "create:" + RemotionJob,
            "end:" + RemotionJob + ":Cancelled",
            "create:" + AnalyzeJob);
        harness.Gateway.MaxOpenJobs.Should().Be(1);
    }

    [Fact]
    public async Task A_job_that_never_ends_keeps_the_slot_and_the_media_step_fails_busy_without_opening_a_second_session()
    {
        await using Harness harness = Build(configureSession: o => o.ParkWaitSeconds = 1, sessionWaitSeconds: 60);
        harness.Gateway.JobsNeverEnd = true;

        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "root", CancellationToken.None);

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        MediaWorkspaceOpenResult opened = await harness.Media.OpenAsync(AnalyzeRequest());
        clock.Stop();

        opened.Refused.Should().BeTrue();
        opened.FailureCode.Should().Be(ComputeFailureCodes.SessionBusy);
        harness.Gateway.Events.Should().NotContain(e => e.StartsWith("create:" + AnalyzeJob, StringComparison.Ordinal));
        harness.Gateway.MaxOpenJobs.Should().Be(1);
        harness.Gate.IsHeld(Execution).Should().BeTrue("a job that may still be running keeps the slot it holds");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the refusal comes from the park's own bound, not the 60-second session wait");
    }

    // ------------------------------------------------------------------
    // Invariant 2: no slot, no park
    // ------------------------------------------------------------------

    [Fact]
    public async Task Parking_an_execution_with_no_sandbox_session_makes_no_gateway_call()
    {
        await using Harness harness = Build();

        SandboxParkOutcome outcome = await harness.Registry.ParkExecutionAsync(Execution);

        outcome.Should().Be(SandboxParkOutcome.NothingToPark);
        harness.GatewayHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sandbox_session_that_bound_locally_holds_no_slot_and_is_not_parked()
    {
        await using Harness harness = Build(decision: ComputeDecision.Local(ComputeDecisionReasons.PoolEmptyLocalFallback));
        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "root", CancellationToken.None);
        harness.GatewayHandler.Requests.Should().BeEmpty("the workspace went to the local sandbox-executor");

        harness.Selector.Decision = OnTheRunner;
        MediaWorkspaceOpenResult opened = await harness.Media.OpenAsync(AnalyzeRequest());

        opened.Refused.Should().BeFalse();
        await using MediaWorkspaceLease lease = opened.Lease!;
        ParkTraffic(harness).Should().Be(0);
        harness.Gateway.Events.Should().Equal("create:" + AnalyzeJob);
    }

    [Fact]
    public async Task A_local_media_step_does_not_park_the_runner_session()
    {
        await using Harness harness = Build();
        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "root", CancellationToken.None);
        int before = harness.GatewayHandler.Requests.Count;

        harness.Selector.Decision = ComputeDecision.Local(ComputeDecisionReasons.StepNotEligible);
        MediaWorkspaceOpenResult opened = await harness.Media.OpenAsync(AnalyzeRequest());

        opened.Refused.Should().BeFalse();
        await using MediaWorkspaceLease lease = opened.Lease!;
        lease.Workspace.Should().BeOfType<LocalMediaWorkspace>();
        harness.GatewayHandler.Requests.Should().HaveCount(before, "a local media step needs no slot, so nothing is parked");

        // ... and the session is still the one the Author opened.
        harness.Selector.Decision = OnTheRunner;
        (await sandbox.ReadTextAsync("src/Root.tsx", CancellationToken.None)).Text.Should().Be("root");
        harness.Gateway.JobPurposes.Should().Equal(RemoteSandboxSession.PurposeToken);
    }

    [Fact]
    public async Task Compute_mode_Local_never_parks_and_never_calls_the_gateway()
    {
        await using Harness harness = Build(mode: ComputeMode.Local, decision: ComputeDecision.Local(ComputeDecisionReasons.LocalMode));
        harness.Sessions.Create(Execution).Should().BeOfType<LocalSandboxSession>();

        (await harness.Registry.ParkExecutionAsync(Execution)).Should().Be(SandboxParkOutcome.NothingToPark);
        MediaWorkspaceOpenResult opened = await harness.Media.OpenAsync(AnalyzeRequest());

        opened.Refused.Should().BeFalse();
        await using MediaWorkspaceLease lease = opened.Lease!;
        lease.Workspace.Should().BeOfType<LocalMediaWorkspace>();
        harness.GatewayHandler.Requests.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Invariant 3: re-open and restore
    // ------------------------------------------------------------------

    [Fact]
    public async Task After_the_media_step_the_next_sandbox_op_reopens_and_the_journal_restores_the_workspace()
    {
        // A ReviewLoop that goes back to the Author after the analyze step: the Author's next tool
        // call must find every file it wrote, with the content it wrote, on a NEW session.
        await using Harness harness = Build();
        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "export const Root = () => <Scene />;", CancellationToken.None);
        await sandbox.WriteTextAsync("src/Scene.tsx", "export const Scene = 1;", CancellationToken.None);
        await sandbox.WriteTextAsync("src/Scratch.tsx", "thrown away", CancellationToken.None);
        await sandbox.DeletePathAsync("src/Scratch.tsx", CancellationToken.None);
        await sandbox.WriteTextAsync("src/Scene.tsx", "export const Scene = 2;", CancellationToken.None);
        int selectorCalls = harness.Selector.Calls;

        MediaWorkspaceOpenResult opened = await harness.Media.OpenAsync(AnalyzeRequest());
        opened.Refused.Should().BeFalse();
        await opened.Lease!.DisposeAsync();
        harness.Gate.IsHeld(Execution).Should().BeFalse("the media lease is disposed and the workspace is parked");
        harness.Gateway.Files.Should().BeEmpty("closing a session tears its workspace down on the runner");

        SandboxTextFile scene = await sandbox.ReadTextAsync("src/Scene.tsx", CancellationToken.None);

        scene.Text.Should().Be("export const Scene = 2;");
        harness.Gateway.FilesAsText().Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["src/Root.tsx"] = "export const Root = () => <Scene />;",
            ["src/Scene.tsx"] = "export const Scene = 2;"
        });
        harness.Gateway.Ops.Where(o => o.Op == "hydrate").Should().ContainSingle();
        harness.Gateway.JobPurposes.Should().Equal(
            RemoteSandboxSession.PurposeToken,
            "video.analyze.extract:5",
            RemoteSandboxSession.PurposeToken + "-2");
        harness.Gateway.MaxOpenJobs.Should().Be(1);

        // The re-open asked the selector again, for the workspace job type - the runner may have gone.
        harness.Selector.Calls.Should().BeGreaterThan(selectorCalls + 1);
        harness.Selector.LastRequest!.JobType.Should().Be(RunnerJobType.RemotionWorkspaceSession);
    }

    [Fact]
    public async Task A_reopen_honours_a_selector_that_now_refuses_and_stays_parked_until_it_can_reopen()
    {
        await using Harness harness = Build();
        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "root", CancellationToken.None);
        MediaWorkspaceOpenResult opened = await harness.Media.OpenAsync(AnalyzeRequest());
        await opened.Lease!.DisposeAsync();

        harness.Selector.Decision = ComputeDecision.NoRunnerOnline("no runner is online for this workspace");
        Func<Task> refused = () => sandbox.ReadTextAsync("src/Root.tsx", CancellationToken.None);
        (await refused.Should().ThrowAsync<ComputeTargetRefusedException>()).Which.FailureCode.Should().Be(ComputeFailureCodes.NoRunnerOnline);
        harness.Gateway.Events.Count(e => e == "create:" + RemotionJob).Should().Be(1, "a refusal opens nothing");

        harness.Selector.Decision = OnTheRunner;
        (await sandbox.ReadTextAsync("src/Root.tsx", CancellationToken.None)).Text.Should().Be("root");
    }

    // ------------------------------------------------------------------
    // Invariant 4: never under an op in flight
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_park_waits_for_the_op_in_flight_and_closes_only_after_it()
    {
        await using Harness harness = Build(sessionWaitSeconds: 30);
        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "root", CancellationToken.None);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Gateway.BeforeOp = async op =>
        {
            if (op == "exec")
            {
                entered.TrySetResult();
                await release.Task;
            }
        };

        Task<SandboxExecResult> render = sandbox.ExecAsync("npx", ["remotion", "render"], 60, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task<SandboxParkOutcome> park = harness.Registry.ParkExecutionAsync(Execution);
        await Task.Delay(300);

        park.IsCompleted.Should().BeFalse("the render is still running on the session");
        harness.Gateway.Ops.Should().NotContain(o => o.Op == "close");

        release.SetResult();
        (await render).Succeeded.Should().BeTrue();
        (await park.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(SandboxParkOutcome.Parked);

        List<string> order = harness.Gateway.Ops.Select(o => o.Op).ToList();
        order.IndexOf("close").Should().BeGreaterThan(order.IndexOf("exec"));
    }

    [Fact]
    public async Task A_park_that_cannot_get_past_a_long_op_answers_still_open_and_closes_nothing()
    {
        await using Harness harness = Build(sessionWaitSeconds: 1);
        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "root", CancellationToken.None);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Gateway.BeforeOp = async op =>
        {
            if (op == "exec")
            {
                entered.TrySetResult();
                await release.Task;
            }
        };

        Task<SandboxExecResult> render = sandbox.ExecAsync("npx", ["remotion", "render"], 60, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        SandboxParkOutcome outcome = await harness.Registry.ParkExecutionAsync(Execution);

        outcome.Should().Be(SandboxParkOutcome.StillOpen);
        harness.Gateway.Ops.Should().NotContain(o => o.Op == "close");
        release.SetResult();
        (await render).Succeeded.Should().BeTrue();
        harness.Gate.IsHeld(Execution).Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Invariant 5: journal failures still surface
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_journal_failure_still_surfaces_and_does_not_wedge_a_later_park()
    {
        await using Harness harness = Build();
        ISandboxSession sandbox = harness.Sessions.Create(Execution);
        await sandbox.WriteTextAsync("src/Root.tsx", "root", CancellationToken.None);

        harness.Store.FailNextPut = true;
        Func<Task> write = () => sandbox.WriteTextAsync("src/Scene.tsx", "scene", CancellationToken.None);
        await write.Should().ThrowAsync<SandboxJournalUnavailableException>();

        // The op lock the write held is released on the failure path, or this would wait forever.
        (await harness.Registry.ParkExecutionAsync(Execution).WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().Be(SandboxParkOutcome.Parked);
    }
}
