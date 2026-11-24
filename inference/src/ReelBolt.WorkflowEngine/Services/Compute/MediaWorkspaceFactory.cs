using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// D11: one request for a media workspace - everything needed both to ask <see cref="IComputeTarget"/>
/// where the work runs and, when the answer is a runner, to open the media session that carries it.
/// </summary>
/// <param name="ExecutionId">The workflow execution; the per-execution session lease is keyed on it.</param>
/// <param name="OrganizationId">The execution's organization, for the entitlement half of the decision.</param>
/// <param name="ProjectId">The project whose object store the session stages from and publishes to.</param>
/// <param name="StepId">The workflow step this attempt belongs to. It is also part of the job idempotency key.</param>
/// <param name="StepOrder">The step's order, for the log lines and the job purpose.</param>
/// <param name="JobType">Which media job type this is: <c>video.compile</c> or <c>video.analyze.extract</c>.</param>
/// <param name="Purpose">The purpose component of the idempotency key. Must be stable across retries of one attempt.</param>
/// <param name="Iteration">The loop iteration. Part of the idempotency key; 0 outside a loop.</param>
/// <param name="RunnerEligible">
/// False for a step that cannot run on a runner at all in v1. The selector then answers Local before
/// it reads anything, which is the mechanism D6 built for exactly this, and the step runs where it
/// always has rather than being turned into a failure.
/// </param>
/// <param name="JobSpec">The job-type-specific <c>assign.spec</c> body, or null. Owned by the caller; this seam never reads it.</param>
/// <param name="RequiredCapabilities">The pins the job declares (see <see cref="RequiredCapsPayload"/>).</param>
public sealed record MediaWorkspaceRequest(
    Guid ExecutionId,
    Guid OrganizationId,
    Guid ProjectId,
    Guid StepId,
    int StepOrder,
    RunnerJobType JobType,
    string Purpose,
    int Iteration = 0,
    bool RunnerEligible = true,
    System.Text.Json.Nodes.JsonNode? JobSpec = null,
    RequiredCapsPayload? RequiredCapabilities = null);

/// <summary>
/// The outcome of opening a media workspace: either a lease, or the coded refusal the step must fail
/// with. A refusal is a VALUE and not an exception on purpose - the executors already have a
/// <c>Failure(context, sw, code, message)</c> path with a real step result, and a thrown exception
/// would be classified (and refunded) as a platform fault instead.
/// </summary>
public sealed record MediaWorkspaceOpenResult(MediaWorkspaceLease? Lease, string? FailureCode, string? Message)
{
    /// <summary>True when the step must fail with <see cref="FailureCode"/> rather than run anywhere.</summary>
    public bool Refused => FailureCode is not null;

    public static MediaWorkspaceOpenResult Opened(MediaWorkspaceLease lease) => new(lease, null, null);

    public static MediaWorkspaceOpenResult Refuse(string code, string message) => new(null, code, message);
}

/// <summary>
/// The media workspace a step attempt runs on, plus what the attempt needs to clean up afterwards.
/// <para>
/// Disposing it closes a REMOTE session (the <c>close</c> op, then the gateway job) and logs the op
/// count the plan asks for. It deliberately does NOT dispose a local scratch directory: the executors
/// own that object and already dispose it in their own <c>finally</c>, and a second disposal here
/// would be a second cleanup of a directory the executor may still be describing in its result.
/// </para>
/// </summary>
public sealed class MediaWorkspaceLease : IAsyncDisposable
{
    private readonly RunnerJobSession? _job;
    private readonly Guid _executionId;
    private readonly int _stepOrder;
    private readonly ILogger _logger;
    private int _disposed;

    internal MediaWorkspaceLease(
        IMediaWorkspace workspace,
        VideoScratchSpace? scratch,
        ComputeDecision decision,
        RunnerJobSession? job,
        Guid executionId,
        int stepOrder,
        ILogger logger)
    {
        Workspace = workspace;
        Scratch = scratch;
        Decision = decision;
        _job = job;
        _executionId = executionId;
        _stepOrder = stepOrder;
        _logger = logger;
    }

    /// <summary>The workspace the step addresses every stage, read, ffmpeg, ffprobe and publish through.</summary>
    public IMediaWorkspace Workspace { get; }

    /// <summary>
    /// The local scratch directory, for a LOCAL workspace only. Null for a remote session, where
    /// there is no directory on this host - which is exactly the condition a caller must not paper
    /// over by creating one.
    /// </summary>
    public VideoScratchSpace? Scratch { get; }

    /// <summary>Where the work was decided to run, and why. The executor records the target on its step result.</summary>
    public ComputeDecision Decision { get; }

    /// <summary>How many ops ran on the runner. Zero for a local workspace; the plan's "op count per compile".</summary>
    public int OpCount => Workspace is RemoteMediaWorkspace remote ? remote.OpCount : 0;

    /// <summary>The gateway's session id for a remote workspace, else null.</summary>
    public Guid? SessionId => Workspace is RemoteMediaWorkspace remote ? remote.SessionId : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Workspace is RemoteMediaWorkspace remote)
        {
            _logger.LogInformation(
                "compute: media session for execution {ExecutionId} step {StepOrder} ran {OpCount} ops on the runner (session {SessionId})",
                _executionId, _stepOrder, remote.OpCount, remote.SessionId);

            await remote.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (_job is not null)
        {
            // Releasing the job is what frees the execution's single runner-session slot (D6's gate).
            await _job.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// D11: the seam that turns a <see cref="MediaWorkspaceRequest"/> into the
/// <see cref="IMediaWorkspace"/> an executor runs on. The executors no longer build a
/// <see cref="LocalMediaWorkspace"/> themselves - that decision is the D6 decision's to make, and this
/// is where it is made.
/// </summary>
public interface IMediaWorkspaceFactory
{
    Task<MediaWorkspaceOpenResult> OpenAsync(MediaWorkspaceRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IMediaWorkspaceFactory"/>
/// <summary>
/// The one implementation. With <c>Compute:Mode=Local</c> - the default, and every self-host install -
/// the selector answers <c>Local</c> before touching the database and this returns exactly the
/// <see cref="LocalMediaWorkspace"/> the executor used to build for itself, over the same scratch
/// directory, so the argv, the paths and the bytes are unchanged.
/// <para>
/// With <c>Fabric</c> the answer comes from <see cref="IComputeTarget"/> and, for a runner-backed one,
/// a <c>video.compile</c>/<c>video.analyze.extract</c> job is created through
/// <see cref="RunnerGatewayClient.OpenJobAsync"/> - which takes the execution's single session slot in
/// the same call, after <see cref="ISandboxSessionRegistry.ParkExecutionAsync"/> has made the execution's
/// workspace session give it up - and the attempt waits for a runner to be assigned before it returns. A session that
/// is never claimed is cancelled and reported as a coded refusal, never silently run locally.
/// </para>
/// </summary>
public sealed class MediaWorkspaceFactory : IMediaWorkspaceFactory
{
    private readonly IComputeTarget _computeTarget;
    private readonly RunnerGatewayClient _gateway;
    private readonly IVideoToolRunner _videoToolRunner;
    private readonly IProjectFileWorkspace _projectFiles;
    private readonly IPresignedUrlService _presignedUrls;
    private readonly IOutputVerifier _outputVerifier;
    private readonly IRunnerOutputStore _outputStore;
    private readonly VideoEditingOptions _videoOptions;
    private readonly RemoteMediaOptions _remoteOptions;
    private readonly ILogger<MediaWorkspaceFactory> _logger;
    private readonly ISandboxSessionRegistry? _sandboxSessions;
    private readonly PoolWakerOptions _poolWaker;
    private readonly TimeProvider _time;

    /// <param name="computeOptions">Source of <c>Compute:PoolWaker:WaitSeconds</c>; optional so existing callers and tests are unchanged (the waker settings then read as defaults).</param>
    /// <param name="time">The clock the assignment wait runs on; <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="sandboxSessions">
    /// The execution's sandbox sessions, parked before a runner-backed media job takes the execution's
    /// one session slot. Optional only so a test can build the factory without one; the engine's
    /// composition root always supplies it.
    /// </param>
    public MediaWorkspaceFactory(
        IComputeTarget computeTarget,
        RunnerGatewayClient gateway,
        IVideoToolRunner videoToolRunner,
        IProjectFileWorkspace projectFiles,
        IPresignedUrlService presignedUrls,
        IOutputVerifier outputVerifier,
        IRunnerOutputStore outputStore,
        IOptions<VideoEditingOptions> videoOptions,
        IOptions<RemoteMediaOptions> remoteOptions,
        ILogger<MediaWorkspaceFactory> logger,
        ISandboxSessionRegistry? sandboxSessions = null,
        IOptions<ComputeOptions>? computeOptions = null,
        TimeProvider? time = null)
    {
        _poolWaker = computeOptions?.Value.PoolWaker ?? new PoolWakerOptions();
        _time = time ?? TimeProvider.System;
        _sandboxSessions = sandboxSessions;
        _computeTarget = computeTarget;
        _gateway = gateway;
        _videoToolRunner = videoToolRunner;
        _projectFiles = projectFiles;
        _presignedUrls = presignedUrls;
        _outputVerifier = outputVerifier;
        _outputStore = outputStore;
        _videoOptions = videoOptions.Value;
        _remoteOptions = remoteOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// The registry a park goes through. Internal so the composition test can assert that the ENGINE's
    /// own container supplies it rather than falling back to the parameter's null: a factory without one
    /// never parks, and a media step would then wait on the workspace session's slot for
    /// <c>Compute:SessionWaitSeconds</c> and fail <c>RUNNER_SESSION_BUSY</c> — the deadlock this exists
    /// to close. A registration that lives only in a test cannot satisfy that assertion.
    /// </summary>
    internal ISandboxSessionRegistry? SandboxSessions => _sandboxSessions;

    public async Task<MediaWorkspaceOpenResult> OpenAsync(
        MediaWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ComputeDecision decision = await _computeTarget
            .ResolveAsync(
                new ComputeTargetRequest(
                    ExecutionId: request.ExecutionId,
                    OrganizationId: request.OrganizationId,
                    JobType: request.JobType,
                    StepId: request.StepId,
                    StepType: request.JobType.ToString(),
                    Iteration: request.Iteration,
                    Purpose: request.Purpose,
                    RunnerEligible: request.RunnerEligible),
                cancellationToken)
            .ConfigureAwait(false);

        if (decision.IsFailure)
        {
            _logger.LogInformation(
                "compute: execution {ExecutionId} step {StepOrder} cannot run: {Code} ({Reason})",
                request.ExecutionId, request.StepOrder, decision.FailureCode, decision.Reason);
            return MediaWorkspaceOpenResult.Refuse(
                decision.FailureCode!, decision.Message ?? "no compute target is available for this step");
        }

        RunnerJobTarget? jobTarget = ComputeTargets.ToJobTarget(decision.Target);
        if (jobTarget is null)
        {
            return OpenLocal(request, decision);
        }

        try
        {
            // The execution has ONE runner-session slot, and a sandbox step earlier in the run (an
            // Author, a motion-graphics agent with render tools) keeps it through its workspace session
            // until the execution ends. Waiting for it here was a deadlock: the analyze step of a live
            // run waited on a session whose step had finished minutes before. Parking ends that
            // session's job and frees the slot; its workspace comes back from the journal on its next op.
            if (_sandboxSessions is not null &&
                await _sandboxSessions.ParkExecutionAsync(request.ExecutionId, cancellationToken).ConfigureAwait(false)
                    == SandboxParkOutcome.StillOpen)
            {
                _logger.LogWarning(
                    "compute: execution {ExecutionId} step {StepOrder} cannot take the runner-session slot: the execution's workspace session could not be parked",
                    request.ExecutionId, request.StepOrder);
                return MediaWorkspaceOpenResult.Refuse(
                    ComputeFailureCodes.SessionBusy,
                    "the execution's workspace session on the runner could not be ended, so this step could not open its own; nothing was run");
            }

            return await OpenRemoteAsync(request, decision, jobTarget.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (ComputeSessionBusyException ex)
        {
            _logger.LogInformation(
                "compute: execution {ExecutionId} step {StepOrder} could not start a runner session: {Message}",
                request.ExecutionId, request.StepOrder, ex.Message);
            return MediaWorkspaceOpenResult.Refuse(ex.FailureCode, ex.Message);
        }
        catch (RunnerGatewayRequestException ex) when (decision.IsPoolWakeAndWait)
        {
            // A wake-and-wait job is an optimisation over running Local: if the gateway refuses it, the
            // user still gets the step, just on this host.
            _logger.LogWarning(
                "compute: the gateway refused the wake-and-wait media job for execution {ExecutionId} step {StepOrder} ({Message}); running locally",
                request.ExecutionId, request.StepOrder, ex.Message);
            return OpenLocal(request, ComputeDecision.Local(ComputeDecisionReasons.PoolWakeTimeoutLocalFallback));
        }
        catch (ComputeFabricUnavailableException ex)
        {
            // The selector already degrades to Local when the gateway is unreachable; this is the
            // narrower race where it answered and then became unreachable. Same rule, same answer: the
            // step runs where it always has, because a gateway that is not there cannot take the job
            // either. (Under RunnerOnly the selector would not have chosen a runner in the first place.)
            _logger.LogWarning(
                "compute: the gateway became unreachable while opening the media session for execution {ExecutionId} step {StepOrder} ({Message}); running locally",
                request.ExecutionId, request.StepOrder, ex.Message);
            return OpenLocal(request, ComputeDecision.Local(ComputeDecisionReasons.FabricUnavailable));
        }
    }

    private MediaWorkspaceOpenResult OpenLocal(MediaWorkspaceRequest request, ComputeDecision decision)
    {
        VideoScratchSpace scratch = VideoScratchSpace.Create(
            _videoOptions, request.ExecutionId, request.StepId, _logger);

        LocalMediaWorkspace workspace = LocalMediaWorkspace.Create(
            scratch, _videoToolRunner, _projectFiles, request.ProjectId, _logger);

        return MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
            workspace, scratch, decision, job: null, request.ExecutionId, request.StepOrder, _logger));
    }

    private async Task<MediaWorkspaceOpenResult> OpenRemoteAsync(
        MediaWorkspaceRequest request,
        ComputeDecision decision,
        RunnerJobTarget target,
        CancellationToken cancellationToken)
    {
        RunnerJobSession job = await _gateway
            .OpenJobAsync(
                new RunnerJobRequest(
                    ExecutionId: request.ExecutionId,
                    OrganizationId: request.OrganizationId,
                    JobType: request.JobType,
                    Target: target,
                    StepId: request.StepId,
                    Iteration: request.Iteration,
                    Purpose: request.Purpose,
                    RequiredCapabilities: request.RequiredCapabilities,
                    Spec: request.JobSpec),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // A wake-and-wait job waits only as long as a node can plausibly take to start. The bound
            // applies to "no runner assigned yet" alone: once a worker holds the job the wait returns.
            int waitSeconds = Math.Max(1, _remoteOptions.AssignWaitSeconds);
            if (decision.IsPoolWakeAndWait)
            {
                waitSeconds = Math.Min(waitSeconds, Math.Max(1, _poolWaker.WaitSeconds));
            }

            if (decision.IsPoolWakeAndWait)
            {
                // The wait below is silent for up to waitSeconds. Say what it is waiting on, so a job the
                // waker does not count as demand (a reused job a vanished worker still holds) is visible.
                RunnerJobView initial = job.Job;
                _logger.LogInformation(
                    "compute: execution {ExecutionId} step {StepOrder} waits up to {Seconds}s for a pool worker on job {JobId} (status {Status}, attempt {Attempt}, {Origin})",
                    request.ExecutionId, request.StepOrder, waitSeconds, job.JobId, initial.Status, initial.Attempt,
                    initial.Created ? "new" : "reused");
                if (!initial.Created && initial.Status is RunnerJobStatus.Assigned or RunnerJobStatus.Running)
                {
                    _logger.LogWarning(
                        "compute: reused job {JobId} is {Status} (attempt {Attempt}) and leased to a worker; it will not start until that worker answers, the gateway requeues or expires it, or the wait ends",
                        job.JobId, initial.Status, initial.Attempt);
                }
            }

            Guid? sessionId = await WaitForAssignmentAsync(job, waitSeconds, cancellationToken).ConfigureAwait(false);
            if (sessionId is null)
            {
                await job.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                if (decision.IsPoolWakeAndWait)
                {
                    // Never a new failure for the user: the node did not come up in time, so the step
                    // runs where it would have run without the waker. Release the session slot first.
                    _logger.LogInformation(
                        "compute: execution {ExecutionId} step {StepOrder} got no pool worker within {Seconds}s of waking the pool; job {JobId} cancelled, running locally",
                        request.ExecutionId, request.StepOrder, waitSeconds, job.JobId);
                    await job.DisposeAsync().ConfigureAwait(false);
                    return OpenLocal(request, ComputeDecision.Local(ComputeDecisionReasons.PoolWakeTimeoutLocalFallback));
                }

                return MediaWorkspaceOpenResult.Refuse(
                    ComputeFailureCodes.JobUnclaimed,
                    "no runner took the media session within the assignment window; nothing was run and nothing was billed");
            }

            RemoteMediaWorkspace workspace = new(
                request.ProjectId,
                job.JobId,
                sessionId.Value,
                target,
                _gateway,
                _projectFiles,
                _presignedUrls,
                _outputVerifier,
                _outputStore,
                _remoteOptions,
                _logger);

            _logger.LogInformation(
                "compute: execution {ExecutionId} step {StepOrder} runs {JobType} on {Target} (job {JobId}, session {SessionId}, {Reason})",
                request.ExecutionId, request.StepOrder, request.JobType, target, job.JobId, sessionId.Value, decision.Reason);

            return MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                workspace, scratch: null, decision, job, request.ExecutionId, request.StepOrder, _logger));
        }
        catch
        {
            if (decision.IsPoolWakeAndWait)
            {
                // The job must not stay queued for a node that wakes later, once this step has given up on it.
                try
                {
                    await job.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cancelEx)
                {
                    _logger.LogWarning(cancelEx, "compute: could not cancel wake-and-wait job {JobId}", job.JobId);
                }
            }

            // Every failure between creating the job and returning a lease must release the
            // execution's session slot, or the next attempt of this execution queues behind a job
            // nobody is going to close.
            await job.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Waits for a runner to be assigned, then for the gateway to report the session id that assignment
    /// creates (<c>runner_sessions.id</c>; ops cannot be sent before it exists). A long-poll that runs
    /// out of window is a normal answer, so the loop re-polls until the budget closes.
    /// </summary>
    private async Task<Guid?> WaitForAssignmentAsync(RunnerJobSession job, int waitSeconds, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _time.GetUtcNow().AddSeconds(waitSeconds);
        bool graceUsed = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (job.SessionId is { } assigned)
            {
                return assigned;
            }

            if (job.Job.IsTerminal || job.Job.SessionLost)
            {
                return null;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                // A worker already holds the job: never abandon it, give the session id a short grace.
                if (!graceUsed && job.Job.Status is RunnerJobStatus.Assigned or RunnerJobStatus.Running)
                {
                    graceUsed = true;
                    deadline = _time.GetUtcNow().AddSeconds(60);
                    continue;
                }

                return null;
            }

            await job.RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}