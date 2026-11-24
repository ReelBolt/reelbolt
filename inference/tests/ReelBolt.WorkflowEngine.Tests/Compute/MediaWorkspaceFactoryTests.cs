using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// D11's wiring: a media step's workspace comes from the D6 compute decision and from nowhere else.
/// The rule this file exists to pin is that with <c>Compute:Mode=Local</c> - the default, and every
/// self-host install - nothing about the media pipeline changes at all, and that with the fabric on
/// a refusal is a CODED VALUE the executor turns into a failed step rather than a silent local run.
/// </summary>
public class MediaWorkspaceFactoryTests
{
    private static readonly Guid Execution = Guid.Parse("e0000000-0000-4000-8000-000000000001");
    private static readonly Guid Org = Guid.Parse("0a000000-0000-4000-8000-000000000002");
    private static readonly Guid Step = Guid.Parse("5e000000-0000-4000-8000-000000000003");
    private static readonly Guid Project = Guid.Parse("90000000-0000-4000-8000-000000000004");
    private static readonly Guid Job = Guid.Parse("1b000000-0000-4000-8000-000000000005");

    private static MediaWorkspaceRequest Request(bool runnerEligible = true)
        => new(
            ExecutionId: Execution,
            OrganizationId: Org,
            ProjectId: Project,
            StepId: Step,
            StepOrder: 3,
            JobType: RunnerJobType.VideoCompile,
            Purpose: "video.compile:3",
            RunnerEligible: runnerEligible);

    /// <summary>An <see cref="IComputeTarget"/> that answers with a decision the test states.</summary>
    private sealed class FixedComputeTarget(ComputeDecision decision) : IComputeTarget
    {
        public int Calls { get; private set; }

        public ValueTask<ComputeDecision> ResolveAsync(ComputeTargetRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class RecordingLogger : ILogger<MediaWorkspaceFactory>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class Harness : IDisposable
    {
        public Harness(string scratchPath) { Root = scratchPath; Directory.CreateDirectory(Root); }

        public string Root { get; }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static (MediaWorkspaceFactory Factory, StubHttpMessageHandler Handler, RecordingLogger Logger) Build(
        ComputeDecision decision,
        string scratchPath,
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? gateway = null,
        MediaWorkspaceFactoryOptions? factoryOptions = null)
    {
        MediaWorkspaceFactoryOptions options = factoryOptions ?? new MediaWorkspaceFactoryOptions();
        StubHttpMessageHandler handler = new(gateway ?? (_ => Task.FromResult(
            ComputeTestFactory.Json(ComputeTestFactory.JobBody(Job, sessionId: Guid.NewGuid(), status: "Running")))));

        ComputeOptions compute = ComputeTestFactory.Options(
            mode: ComputeMode.Fabric,
            sessionWaitSeconds: options.SessionWaitSeconds,
            jobWaitSeconds: options.JobWaitSeconds);

        ExecutionSessionGate gate = new(Options.Create(compute));
        if (options.HoldSessionSlot)
        {
            gate.AcquireAsync(Execution, null).GetAwaiter().GetResult();
        }

        RecordingLogger logger = new();
        MediaWorkspaceFactory factory = new(
            new FixedComputeTarget(decision),
            ComputeTestFactory.Gateway(handler, compute, gate),
            new NoopVideoToolRunner(),
            new FakeProjectFileWorkspace(),
            new FakePresignedUrlService(),
            new FakeOutputVerifier(),
            new FakeRunnerOutputStore(),
            Options.Create(new VideoEditingOptions { ScratchPath = scratchPath, AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 }),
            Options.Create(new RemoteMediaOptions { AssignWaitSeconds = options.AssignWaitSeconds }),
            logger);

        return (factory, handler, logger);
    }

    private sealed class MediaWorkspaceFactoryOptions
    {
        public int SessionWaitSeconds { get; set; } = 900;

        public int JobWaitSeconds { get; set; } = 30;

        public int AssignWaitSeconds { get; set; } = 300;

        public bool HoldSessionSlot { get; set; }
    }

    internal sealed class NoopVideoToolRunner : IVideoToolRunner
    {
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct, onStdOutLine);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfprobeAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
            => Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
            => workspace.RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
            Action<string> onStdOutLine)
            => workspace.RunFfmpegAsync(args, timeout, ct, onStdOutLine);
    }

    // --- the Local answer --------------------------------------------------

    [Fact]
    public async Task ALocalDecisionReturnsTheSameWorkspaceTheExecutorUsedToBuildItself()
    {
        // Compute:Mode=Local is the default and answers Local before the database or the gateway, so
        // this is the path every self-host install takes: the very same LocalMediaWorkspace, over the
        // very same scratch directory, and not one byte of HTTP traffic.
        using Harness harness = new(Path.Combine(Path.GetTempPath(), "d11-local-" + Guid.NewGuid().ToString("N")));
        (MediaWorkspaceFactory factory, StubHttpMessageHandler handler, _) = Build(
            ComputeDecision.Local(ComputeDecisionReasons.LocalMode), harness.Root);

        MediaWorkspaceOpenResult opened = await factory.OpenAsync(Request());

        opened.Refused.Should().BeFalse();
        await using MediaWorkspaceLease lease = opened.Lease!;
        lease.Workspace.Should().BeOfType<LocalMediaWorkspace>();
        lease.Scratch.Should().NotBeNull("a local workspace still owns the scratch directory the executor cleans up");
        lease.Decision.Target.Should().Be(ComputeTargetKind.Local);
        lease.Decision.Reason.Should().Be(ComputeDecisionReasons.LocalMode);
        lease.OpCount.Should().Be(0);
        handler.Requests.Should().BeEmpty("nothing about a local run may touch the gateway");
    }

    [Fact]
    public async Task AStepThatIsNotRunnerEligibleSTILLGetsAUsableLocalWorkspaceAndNotAFailure()
    {
        // D6's own rule: a step that cannot run on a runner in v1 "is neither sent to a runner nor
        // turned into a failure: it simply runs where it always has".
        using Harness harness = new(Path.Combine(Path.GetTempPath(), "d11-noteligible-" + Guid.NewGuid().ToString("N")));
        (MediaWorkspaceFactory factory, _, _) = Build(
            ComputeDecision.Local(ComputeDecisionReasons.StepNotEligible), harness.Root);

        MediaWorkspaceOpenResult opened = await factory.OpenAsync(Request(runnerEligible: false));

        opened.Refused.Should().BeFalse();
        await using MediaWorkspaceLease lease = opened.Lease!;
        lease.Workspace.Should().BeOfType<LocalMediaWorkspace>();
    }

    // --- the runner answer -------------------------------------------------

    [Fact]
    public async Task ARunnerDecisionOpensAMediaSessionForTheStepAndRunsOnIt()
    {
        using Harness harness = new(Path.Combine(Path.GetTempPath(), "d11-remote-" + Guid.NewGuid().ToString("N")));
        Guid session = Guid.NewGuid();
        JsonObject? posted = null;

        (MediaWorkspaceFactory factory, StubHttpMessageHandler handler, RecordingLogger logger) = Build(
            ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.PreferenceCloudOnly),
            harness.Root,
            async request =>
            {
                if (request.RequestUri!.AbsolutePath == "/internal/v1/jobs")
                {
                    posted = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                    return ComputeTestFactory.Json(ComputeTestFactory.JobBody(Job, sessionId: session, status: "Running"));
                }

                return ComputeTestFactory.Json(MediaSessionTestData.OpBody("""{"exists":true,"bytes":7}"""));
            });

        MediaWorkspaceOpenResult opened = await factory.OpenAsync(Request());

        opened.Refused.Should().BeFalse();
        await using MediaWorkspaceLease lease = opened.Lease!;
        lease.Workspace.Should().BeOfType<RemoteMediaWorkspace>();
        lease.Scratch.Should().BeNull("a remote session has no directory on this host, and inventing one would be the bug");
        lease.SessionId.Should().Be(session);
        lease.Decision.Target.Should().Be(ComputeTargetKind.CloudPool);

        posted!["jobType"]!.GetValue<string>().Should().Be("video.compile");
        posted["target"]!.GetValue<string>().Should().Be("CloudPool");
        posted["stepId"]!.GetValue<string>().Should().Be(Step.ToString("D"));
        posted["purpose"]!.GetValue<string>().Should().Be("video.compile:3");
        posted["idempotencyKey"]!.GetValue<string>()
            .Should().Be(RunnerGatewayClient.BuildIdempotencyKey(Execution, Step, 0, "video.compile:3"));

        // The session works, and disposing the lease reports the op count the plan asks to log.
        (await lease.Workspace.StatAsync("out.mp4", CancellationToken.None)).Bytes.Should().Be(7);
        int ops = lease.OpCount;
        await lease.DisposeAsync();
        logger.Messages.Should().Contain(m => m.Contains("op count") || (m.Contains("ran") && m.Contains("ops")));
        ops.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ARefusalDecisionBecomesACodedFailureAndNeverALocalRun()
    {
        using Harness harness = new(Path.Combine(Path.GetTempPath(), "d11-refuse-" + Guid.NewGuid().ToString("N")));
        (MediaWorkspaceFactory factory, StubHttpMessageHandler handler, _) = Build(
            ComputeDecision.NoRunnerOnline("no paired runner is online for this organization"), harness.Root);

        MediaWorkspaceOpenResult opened = await factory.OpenAsync(Request());

        opened.Refused.Should().BeTrue();
        opened.FailureCode.Should().Be(ComputeFailureCodes.NoRunnerOnline);
        opened.Message.Should().Contain("no paired runner");
        opened.Lease.Should().BeNull();
        handler.Requests.Should().BeEmpty("a refusal must not open a session and must not fall back to a local run");
    }

    [Fact]
    public async Task ASessionSlotThatCannotBeTakenIsReportedAsRunnerSessionBusy()
    {
        using Harness harness = new(Path.Combine(Path.GetTempPath(), "d11-busy-" + Guid.NewGuid().ToString("N")));
        (MediaWorkspaceFactory factory, _, _) = Build(
            ComputeDecision.Runner(RunnerJobTarget.OrgRunner, ComputeDecisionReasons.PreferenceRunner),
            harness.Root,
            factoryOptions: new MediaWorkspaceFactoryOptions { HoldSessionSlot = true, SessionWaitSeconds = 0 });

        MediaWorkspaceOpenResult opened = await factory.OpenAsync(Request());

        opened.Refused.Should().BeTrue();
        opened.FailureCode.Should().Be(ComputeFailureCodes.SessionBusy);
    }

    [Fact]
    public async Task AJobNoRunnerTakesIsCancelledAndRefusedRatherThanRunLocally()
    {
        // The plan's own line: nothing silently falls back to local. The job exists, nobody claimed it,
        // so it is cancelled and the step fails with a code - not quietly re-encoded on this host.
        using Harness harness = new(Path.Combine(Path.GetTempPath(), "d11-unclaimed-" + Guid.NewGuid().ToString("N")));
        List<(HttpMethod Method, string Path)> calls = [];

        (MediaWorkspaceFactory factory, _, _) = Build(
            ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.AutoWithoutRunner),
            harness.Root,
            request =>
            {
                calls.Add((request.Method, request.RequestUri!.AbsolutePath));
                return Task.FromResult(request.Method == HttpMethod.Delete
                    ? ComputeTestFactory.Json(ComputeTestFactory.JobBody(Job, status: "Cancelled"))
                    : ComputeTestFactory.Json(ComputeTestFactory.JobBody(Job, sessionId: null, status: "Queued")));
            },
            new MediaWorkspaceFactoryOptions { AssignWaitSeconds = 1, JobWaitSeconds = 0 });

        MediaWorkspaceOpenResult opened = await factory.OpenAsync(Request());

        opened.Refused.Should().BeTrue();
        opened.FailureCode.Should().Be(ComputeFailureCodes.JobUnclaimed);
        calls.Should().Contain(c => c.Method == HttpMethod.Delete, "the unclaimed job is cancelled so it cannot run later");
    }

    [Fact]
    public async Task AGatewayThatVanishesWhileOpeningTheSessionDegradesToLocalRatherThanFailingTheStep()
    {
        // The selector already treats an unreachable gateway as "the fabric is not deployed"; this is
        // the narrower race where it answered and then went away. Same rule, same answer.
        using Harness harness = new(Path.Combine(Path.GetTempPath(), "d11-vanish-" + Guid.NewGuid().ToString("N")));
        (MediaWorkspaceFactory factory, _, _) = Build(
            ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.AutoWithoutRunner),
            harness.Root,
            _ => Task.FromResult(ComputeTestFactory.Json("""{"error":"internal_api_disabled"}""", HttpStatusCode.ServiceUnavailable)));

        MediaWorkspaceOpenResult opened = await factory.OpenAsync(Request());

        opened.Refused.Should().BeFalse();
        await using MediaWorkspaceLease lease = opened.Lease!;
        lease.Workspace.Should().BeOfType<LocalMediaWorkspace>();
        lease.Decision.Reason.Should().Be(ComputeDecisionReasons.FabricUnavailable);
    }
}
