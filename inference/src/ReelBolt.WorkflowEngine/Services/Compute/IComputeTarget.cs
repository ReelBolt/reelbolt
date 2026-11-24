using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// D6: the decision the selector reaches for one unit of work — where it will run, and why.
/// <para>
/// It is a value, not a handle: nothing is dispatched by producing one. The executor that receives
/// it writes <see cref="Target"/> onto its <c>StepExecutionResult.ComputeTarget</c> (which
/// <c>WorkflowExecutorService</c> copies to <c>StepExecutionContext.ComputeTarget</c> and thence to
/// <c>WorkflowStepResult.ComputeTarget</c> — the D12 plumbing), and only then does it open a session
/// or run in-process.
/// </para>
/// </summary>
/// <param name="Target">Where the work runs.</param>
/// <param name="Reason">A stable, log-friendly token for WHY — see <see cref="ComputeDecisionReasons"/>. Never localised, never shown to a user.</param>
/// <param name="FailureCode">
/// The user-facing, machine-readable code when the decision is "do not run": today only
/// <see cref="ComputeFailureCodes.NoRunnerOnline"/> (the plan's own example) and
/// <see cref="ComputeFailureCodes.ComputeNotAvailable"/>. Null on every runnable decision, including
/// a <see cref="ComputeTargetKind.Local"/> fallback.
/// </param>
/// <param name="Message">Human-readable detail for the failure, or null.</param>
public sealed record ComputeDecision(
    ComputeTargetKind Target,
    string Reason,
    string? FailureCode = null,
    string? Message = null)
{
    /// <summary>True when the step must NOT run — the caller fails the step with <see cref="FailureCode"/> rather than falling back to anything.</summary>
    public bool IsFailure => FailureCode is not null;

    /// <summary>
    /// True when the pool is empty but a healthy waker will start a node: the job is queued and the
    /// caller waits at most <c>Compute:PoolWaker:WaitSeconds</c> for an assignment, then runs Local.
    /// </summary>
    public bool IsPoolWakeAndWait => Target == ComputeTargetKind.CloudPool && Reason == ComputeDecisionReasons.PoolWakeAndWait;

    /// <summary>True when the work is handed to a runner-backed session (either target), i.e. when the per-execution session gate applies.</summary>
    public bool IsRunnerBacked => Target is ComputeTargetKind.OrgRunner or ComputeTargetKind.CloudPool;

    /// <summary>Shorthand for the common in-process answer.</summary>
    public static ComputeDecision Local(string reason) => new(ComputeTargetKind.Local, reason);

    /// <summary>Shorthand for handing the work to a runner target.</summary>
    public static ComputeDecision Runner(RunnerJobTarget target, string reason)
        => new(ComputeTargets.ToKind(target), reason);

    /// <summary>Shorthand for the one refusal the plan names.</summary>
    public static ComputeDecision NoRunnerOnline(string message)
        => new(ComputeTargetKind.Local, ComputeDecisionReasons.NoRunnerOnline, ComputeFailureCodes.NoRunnerOnline, message);

    /// <summary>Shorthand for "the org is entitled to no compute target at all".</summary>
    public static ComputeDecision NotAvailable(string message)
        => new(ComputeTargetKind.Local, ComputeDecisionReasons.NotAvailable, ComputeFailureCodes.ComputeNotAvailable, message);
}

/// <summary>
/// Stable reason tokens for <see cref="ComputeDecision.Reason"/>. They are deliberately strings, not
/// an enum: they end up in a single log line and in a telemetry dimension, and adding one must never
/// be a breaking change for a dashboard.
/// </summary>
public static class ComputeDecisionReasons
{
    /// <summary><c>Compute:Mode</c> is <c>Local</c> — the default, and the only reason that reaches no database and no gateway.</summary>
    public const string LocalMode = "local_mode";

    /// <summary>The step type is not runner-eligible in v1 (a <c>VideoAnalyze</c> that asks for object tracking).</summary>
    public const string StepNotEligible = "step_not_eligible";

    /// <summary>The org prefers <c>CloudOnly</c>, or its preference was downgraded to it because the plan withholds runner use.</summary>
    public const string PreferenceCloudOnly = "preference_cloud_only";

    /// <summary>The org prefers a runner and an eligible one is online right now.</summary>
    public const string PreferenceRunner = "preference_runner";

    /// <summary><c>Auto</c> with a paired, online runner: behaves as <c>PreferRunner</c> (ComputePreference's own definition).</summary>
    public const string AutoWithRunner = "auto_with_runner";

    /// <summary><c>Auto</c> with no runner online: behaves as <c>CloudOnly</c> (ComputePreference's own definition).</summary>
    public const string AutoWithoutRunner = "auto_without_runner";

    /// <summary>The org asked for a runner (<c>PreferRunner</c>/<c>Auto</c>) and none was eligible, so the pool takes the work.</summary>
    public const string RunnerUnavailableCloudFallback = "runner_unavailable_cloud_fallback";

    /// <summary>The org asked for a runner only (<c>RunnerOnly</c>) and none was eligible: the step FAILS. Nothing silently falls back to cloud billing.</summary>
    public const string NoRunnerOnline = "no_runner_online";

    /// <summary>The fabric is deployed but unreachable, or the gateway has no internal token configured (503). The step runs locally.</summary>
    public const string FabricUnavailable = "fabric_unavailable";

    /// <summary>The decision was <c>CloudPool</c> but no pool worker is online (and <c>Compute:LocalWhenPoolEmpty</c> is on), so the step runs on the engine's own host instead of queueing a job nobody can take.</summary>
    public const string PoolEmptyLocalFallback = "pool_empty_local_fallback";

    /// <summary>The decision is <c>CloudPool</c>, no pool worker is online, <c>Compute:PoolWaker:Enabled</c> is on and the gateway reports a healthy waker: queue the job and wait a bounded time for a node to wake.</summary>
    public const string PoolWakeAndWait = "pool_wake_and_wait";

    /// <summary>A <see cref="PoolWakeAndWait"/> job was not assigned within <c>Compute:PoolWaker:WaitSeconds</c> (or the gateway refused it); it was cancelled and the step runs on the engine's own host.</summary>
    public const string PoolWakeTimeoutLocalFallback = "pool_wake_timeout_local_fallback";

    /// <summary>The decision was <c>CloudPool</c> but <c>Compute:CloudPoolJobTypes</c> does not name this job type, so the step runs on the engine's own host.</summary>
    public const string PoolJobTypeNotEnabled = "pool_job_type_not_enabled";

    /// <summary>The decision was <c>CloudPool</c> but presigned URLs are signed against an address a pool node cannot reach (not https, or a private address), so the step runs on the engine's own host.</summary>
    public const string PoolStorageUnreachable = "pool_storage_unreachable";

    /// <summary><c>Compute:OrgRunnersEnabled</c> is off and the organization asked for its own runner only: the step stays on the engine's own host, as it does in <c>Local</c> mode.</summary>
    public const string OrgRunnersDisabled = "org_runners_disabled";

    /// <summary>The org may use neither a runner nor the pool.</summary>
    public const string NotAvailable = "not_available";
}

/// <summary>
/// The machine-readable codes a failed compute decision carries. They are surfaced to the user the
/// same way every other engine failure code is (a step's <c>ErrorDetails</c>), so they are SHOUTY and
/// stable.
/// </summary>
public static class ComputeFailureCodes
{
    /// <summary>
    /// The plan's own code, verbatim: "DesktopOnly with no runner online fails the step with the
    /// user-facing error NO_RUNNER_ONLINE; nothing falls back to cloud billing silently."
    /// </summary>
    public const string NoRunnerOnline = "NO_RUNNER_ONLINE";

    /// <summary>The organization is entitled to no compute target — no runner use, and no pool.</summary>
    public const string ComputeNotAvailable = "COMPUTE_NOT_AVAILABLE";

    /// <summary>
    /// Another unit of the same execution still holds its one runner session (<c>runner_sessions</c>
    /// allows exactly one open session per execution), and the wait for the slot expired.
    /// </summary>
    public const string SessionBusy = "RUNNER_SESSION_BUSY";

    /// <summary>
    /// D11: the media session's job was created and no runner claimed it before the assignment window
    /// closed. The job is cancelled and the step fails rather than quietly running on this host - a
    /// silent local run after asking for a runner would encode twice and bill the wrong party.
    /// </summary>
    public const string JobUnclaimed = "RUNNER_JOB_UNCLAIMED";
}

/// <summary>
/// D6: one request for a target. It carries everything the decision table reads and nothing else —
/// in particular no credentials, no job spec and no presigned URL, because deciding WHERE work runs
/// is a pure function of the execution, the org and the step's own type.
/// </summary>
/// <param name="ExecutionId">The workflow execution the work belongs to. The session gate is keyed on it.</param>
/// <param name="OrganizationId">The execution's organization; <see cref="Guid.Empty"/> for a pre-tenancy row.</param>
/// <param name="JobType">Which protocol job type the work maps to (<c>remotion.workspace-session</c>, <c>video.compile</c>, <c>video.analyze.extract</c>).</param>
/// <param name="StepId">The workflow step, when the work is per-step. Null for an execution-scoped session.</param>
/// <param name="StepType">The step's type as a string, for logs only.</param>
/// <param name="Iteration">The loop iteration this job belongs to; part of the idempotency key.</param>
/// <param name="Purpose">The purpose token; part of the idempotency key.</param>
/// <param name="RunnerEligible">
/// False when the step cannot run on a runner at ALL in v1 — the one case the plan names is a
/// <c>VideoAnalyze</c> configured with <c>TrackObjects</c>, which is platform-only. Such a step is
/// neither sent to a runner nor turned into a failure: it simply runs where it always has.
/// </param>
public sealed record ComputeTargetRequest(
    Guid ExecutionId,
    Guid OrganizationId,
    RunnerJobType JobType,
    Guid? StepId = null,
    string? StepType = null,
    int Iteration = 0,
    string Purpose = "",
    bool RunnerEligible = true);

/// <summary>
/// D6: resolves the compute target for one unit of work. The plan's "IComputeTarget, selector and
/// gateway client": this is the seam the runner-aware executors (D7b's Remotion session, D8's render
/// and staging, D11's media sessions) call instead of deciding for themselves.
/// <para>
/// <b>Contract.</b> With <c>Compute:Mode=Local</c> — the default, and every self-host install —
/// every call answers <see cref="ComputeTargetKind.Local"/> before touching the database or the
/// gateway, so an install that has never heard of the fabric behaves exactly as it did before this
/// work package existed. With <c>Fabric</c> the answer is one of the three targets, or a
/// <see cref="ComputeDecision.IsFailure"/> refusal the caller turns into a failed step.
/// </para>
/// <para>
/// A selector never throws for an infrastructure problem: an unreachable gateway, a 503 from a
/// gateway with no internal token, and a gateway with no eligible runner are all answers, not
/// exceptions, because a step must not fail merely because the fabric is not deployed.
/// </para>
/// </summary>
public interface IComputeTarget
{
    /// <summary>Resolve the target for <paramref name="request"/>.</summary>
    ValueTask<ComputeDecision> ResolveAsync(ComputeTargetRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The two compute-target vocabularies, and the one place they are related.
/// <see cref="ComputeTargetKind"/> is what the engine persists (D12);
/// <see cref="RunnerJobTarget"/> is what the protocol and the gateway route on. They are the same
/// three-way choice minus <c>Local</c>, which has no protocol representation at all.
/// </summary>
public static class ComputeTargets
{
    /// <summary>The protocol target string for a job target (<c>"CloudPool"</c> / <c>"OrgRunner"</c>).</summary>
    public static string ToWire(RunnerJobTarget target) => target switch
    {
        RunnerJobTarget.CloudPool => "CloudPool",
        RunnerJobTarget.OrgRunner => "OrgRunner",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "unknown runner job target")
    };

    /// <summary>The persisted target for a protocol target.</summary>
    public static ComputeTargetKind ToKind(RunnerJobTarget target) => target switch
    {
        RunnerJobTarget.CloudPool => ComputeTargetKind.CloudPool,
        RunnerJobTarget.OrgRunner => ComputeTargetKind.OrgRunner,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "unknown runner job target")
    };

    /// <summary>The protocol target for a persisted kind, or null for <see cref="ComputeTargetKind.Local"/> (which is not a runner job).</summary>
    public static RunnerJobTarget? ToJobTarget(ComputeTargetKind kind) => kind switch
    {
        ComputeTargetKind.CloudPool => RunnerJobTarget.CloudPool,
        ComputeTargetKind.OrgRunner => RunnerJobTarget.OrgRunner,
        _ => null
    };

    /// <summary>Parses the gateway's target string, tolerating neither case nor whitespace surprises.</summary>
    public static bool TryParseWire(string? wire, out RunnerJobTarget target)
    {
        switch (wire?.Trim())
        {
            case "CloudPool":
                target = RunnerJobTarget.CloudPool;
                return true;
            case "OrgRunner":
                target = RunnerJobTarget.OrgRunner;
                return true;
            default:
                target = default;
                return false;
        }
    }
}

/// <summary>
/// The wire names of the three v1 job types (docs/runner-protocol.md §"Job types"). They live here,
/// next to the selector that chooses a target for one of them, so the engine has exactly one source
/// for the strings the gateway is sent.
/// <para>
/// <see cref="RunnerJobType"/> already exists as a persisted enum; these are its
/// <c>assign</c>-side spellings, which is why the mapping is a function rather than a second enum.
/// </para>
/// </summary>
public static class ComputeJobTypes
{
    /// <summary>Interactive, long-lived, one per workflow execution. Requires a verified container runtime.</summary>
    public const string RemotionWorkspaceSession = "remotion.workspace-session";

    /// <summary>One VideoCompile step.</summary>
    public const string VideoCompile = "video.compile";

    /// <summary>The extraction phase of one VideoAnalyze step.</summary>
    public const string VideoAnalyzeExtract = "video.analyze.extract";

    /// <summary>The wire name of a persisted job type.</summary>
    public static string ToWire(RunnerJobType jobType) => jobType switch
    {
        RunnerJobType.RemotionWorkspaceSession => RemotionWorkspaceSession,
        RunnerJobType.VideoCompile => VideoCompile,
        RunnerJobType.VideoAnalyzeExtract => VideoAnalyzeExtract,
        _ => throw new ArgumentOutOfRangeException(nameof(jobType), jobType, "unknown runner job type")
    };

    /// <summary>The persisted job type for a wire name, or null when the gateway sent one this build does not know (open sets: an unknown value is an unknown case, never a parse error).</summary>
    public static RunnerJobType? TryParseWire(string? wire) => wire?.Trim() switch
    {
        RemotionWorkspaceSession => RunnerJobType.RemotionWorkspaceSession,
        VideoCompile => RunnerJobType.VideoCompile,
        VideoAnalyzeExtract => RunnerJobType.VideoAnalyzeExtract,
        _ => null
    };

    /// <summary>Every v1 job type, for tests and drift guards.</summary>
    public static IReadOnlyList<string> All { get; } = [RemotionWorkspaceSession, VideoCompile, VideoAnalyzeExtract];
}
