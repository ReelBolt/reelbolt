using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// D6: the selector. It answers "where does this unit of work run?" from four inputs — the deployment
/// mode, the organization's preference, the plan entitlement and whether an eligible runner is online
/// right now — and it answers with a <see cref="ComputeDecision"/> the caller records on its step
/// result.
/// <para>
/// <b>The decision table.</b> With <c>Compute:Mode=Local</c> (the default) it answers
/// <see cref="ComputeTargetKind.Local"/> immediately, before any database read and before any gateway
/// call, which is what makes the fabric invisible to a self-host install. With <c>Fabric</c>:
/// </para>
/// <list type="table">
/// <item><description>A step that is not runner-eligible in v1 runs where it always has (Local). It does not fail: the plan's one example is a <c>VideoAnalyze</c> with <c>TrackObjects</c>, which is platform-only.</description></item>
/// <item><description><c>CloudOnly</c> (and any preference downgraded to it because the plan withholds runner use) → <c>CloudPool</c>.</description></item>
/// <item><description><c>RunnerOnly</c> → <c>OrgRunner</c> when an eligible desktop runner is online, otherwise <b>the step fails with <c>NO_RUNNER_ONLINE</c></b>. Nothing falls back to cloud billing silently, which is the whole point of the preference.</description></item>
/// <item><description><c>PreferRunner</c> and <c>Auto</c> with a runner online → <c>OrgRunner</c>.</description></item>
/// <item><description><c>PreferRunner</c> with no runner online → <c>CloudPool</c>; <c>Auto</c> with no runner online → <c>CloudPool</c> too, which is <see cref="ComputePreference.Auto"/>'s own definition ("behaves as CloudOnly until a runner is paired").</description></item>
/// <item><description>Whenever the answer above is <c>CloudPool</c> but <b>no pool worker is online</b> (and <c>Compute:LocalWhenPoolEmpty</c> is on, the default) → the step runs <b>locally</b>, on the engine's own host. Nothing enrolls pool workers in a deployment that has not set them up, and a <c>CloudPool</c> job with no worker is never taken: it fails the step after <c>SessionWaitSeconds</c>. <c>RunnerOnly</c> is untouched. Exception: with <c>Compute:PoolWaker:Enabled</c> on and the gateway reporting <c>pool.wakerHealthy</c>, the decision stays <c>CloudPool</c> with reason <c>pool_wake_and_wait</c> and the caller waits a bounded time for the waker's node (<see cref="ComputeDecision.IsPoolWakeAndWait"/>).</description></item>
/// <item><description>The fabric is deployed but unreachable, or its internal API is closed (503) → the step runs <b>locally</b>, except under <c>RunnerOnly</c>, which still reports <c>NO_RUNNER_ONLINE</c>. A gateway we cannot reach cannot take a CloudPool job either, so Local is the only answer that actually runs the work — and switching the mode on is what a deployment that wants the fabric to be mandatory asks for.</description></item>
/// </list>
/// </summary>
public sealed class ComputeTargetSelector : IComputeTarget
{
    private readonly ComputeOptions _options;
    private readonly IComputeEntitlements _entitlements;
    private readonly RunnerGatewayClient _gateway;
    private readonly ILogger<ComputeTargetSelector> _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _presenceTtl;
    private readonly ConcurrentDictionary<(Guid OrganizationId, string JobType), PresenceEntry> _presence = new();

    public ComputeTargetSelector(
        IOptions<ComputeOptions> options,
        IComputeEntitlements entitlements,
        RunnerGatewayClient gateway,
        ILogger<ComputeTargetSelector> logger,
        TimeProvider time)
    {
        _options = options.Value;
        _entitlements = entitlements;
        _gateway = gateway;
        _logger = logger;
        _time = time;
        _presenceTtl = TimeSpan.FromSeconds(Math.Max(0, _options.RunnerQueryCacheSeconds));
    }

    /// <summary>The deployment mode this selector was built for. Exposed so callers can skip the fabric entirely without a decision object.</summary>
    public ComputeMode Mode => _options.Mode;

    /// <inheritdoc />
    public async ValueTask<ComputeDecision> ResolveAsync(ComputeTargetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The default, and the whole of the self-host contract: no database, no gateway, no change.
        if (_options.Mode != ComputeMode.Fabric)
        {
            return ComputeDecision.Local(ComputeDecisionReasons.LocalMode);
        }

        if (!request.RunnerEligible)
        {
            return ComputeDecision.Local(ComputeDecisionReasons.StepNotEligible);
        }

        ComputeEntitlements entitlements = await _entitlements.GetAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        ComputePreference preference = entitlements.EffectivePreference;

        if (!entitlements.CloudPoolAllowed && !entitlements.RunnerAllowed)
        {
            return ComputeDecision.NotAvailable(
                $"organization {request.OrganizationId} may use neither a paired runner nor the compute pool");
        }

        if (!_options.OrgRunnersEnabled && preference == ComputePreference.RunnerOnly)
        {
            // Pool-only fabric: this organization's own-machine-only preference cannot be honoured and must
            // not be turned into a pool job, so the step runs where it runs with Compute:Mode=Local.
            return ComputeDecision.Local(ComputeDecisionReasons.OrgRunnersDisabled);
        }

        // Consult the gateway when a runner is a candidate, or when we must know whether the pool has a
        // worker before sending anything to it. A CloudOnly org with LocalWhenPoolEmpty off never needs
        // to know what is online, and asking would put a network call on the path of every step (the
        // answer is cached for Compute:RunnerQueryCacheSeconds, so this is one call per window).
        RunnerProbe probe = preference == ComputePreference.CloudOnly && !_options.LocalWhenPoolEmpty
            ? default
            : await ProbeRunnerAsync(request, cancellationToken).ConfigureAwait(false);

        if (probe.FabricUnavailable)
        {
            // There is no usable gateway, so there is no runner AND no pool to send the work to. The
            // only decision that actually runs the work is Local — except under RunnerOnly, where the
            // user asked for their own machine or nothing and gets the coded failure instead.
            if (preference == ComputePreference.RunnerOnly)
            {
                return ComputeDecision.NoRunnerOnline(
                    "This workflow is set to run on your own computer, but the compute service is not reachable. Start the ReelBolt desktop app, or change the workspace's compute preference to allow the cloud pool.");
            }

            _logger.LogInformation(
                "compute: execution {ExecutionId} step {StepType} runs locally because the compute fabric is unavailable",
                request.ExecutionId, request.StepType ?? "-");
            return ComputeDecision.Local(ComputeDecisionReasons.FabricUnavailable);
        }

        bool runnerOnline = probe.Online && _options.OrgRunnersEnabled;

        ComputeDecision decision = preference switch
        {
            ComputePreference.CloudOnly =>
                entitlements.CloudPoolAllowed
                    ? ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.PreferenceCloudOnly)
                    : ComputeDecision.NotAvailable($"organization {request.OrganizationId} may not use the compute pool"),

            ComputePreference.RunnerOnly =>
                runnerOnline
                    ? ComputeDecision.Runner(RunnerJobTarget.OrgRunner, ComputeDecisionReasons.PreferenceRunner)
                    : ComputeDecision.NoRunnerOnline(
                        "This workflow is set to run on your own computer, but no paired runner is online. Start the ReelBolt desktop app, or change the workspace's compute preference to allow the cloud pool."),

            ComputePreference.PreferRunner =>
                runnerOnline
                    ? ComputeDecision.Runner(RunnerJobTarget.OrgRunner, ComputeDecisionReasons.PreferenceRunner)
                    : entitlements.CloudPoolAllowed
                        ? ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.RunnerUnavailableCloudFallback)
                        : ComputeDecision.NoRunnerOnline("No paired runner is online and this workspace may not use the compute pool."),

            _ =>
                runnerOnline
                    ? ComputeDecision.Runner(RunnerJobTarget.OrgRunner, ComputeDecisionReasons.AutoWithRunner)
                    : entitlements.CloudPoolAllowed
                        ? ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.AutoWithoutRunner)
                        : ComputeDecision.NoRunnerOnline("No paired runner is online and this workspace may not use the compute pool.")
        };

        if (decision.Target == ComputeTargetKind.CloudPool)
        {
            ComputeDecision? narrowed = NarrowPoolDecision(request);
            if (narrowed is not null)
            {
                return narrowed;
            }
        }

        // A pool job nobody can take is queued, waited on, and failed. When the pool has no worker the
        // engine's own host is the only machine that can run the step, so run it there.
        if (decision.Target == ComputeTargetKind.CloudPool && _options.LocalWhenPoolEmpty && !probe.PoolOnline)
        {
            // With the waker on and healthy (and not capped: the gateway folds that into the flag) the
            // empty pool is the signal to start a node, so keep the job on CloudPool; the caller waits a
            // bounded time and falls back to Local itself (PoolWakeTimeoutLocalFallback).
            // Media jobs only: a Remotion workspace session has no Local fallback after the wait.
            if (_options.PoolWaker.Enabled && probe.WakerHealthy && request.JobType != RunnerJobType.RemotionWorkspaceSession)
            {
                _logger.LogInformation(
                    "compute: execution {ExecutionId} step {StepType} ({JobType}) queues on CloudPool: no worker is online but the pool waker is healthy",
                    request.ExecutionId, request.StepType ?? "-", ComputeJobTypes.ToWire(request.JobType));
                return ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.PoolWakeAndWait);
            }

            _logger.LogInformation(
                "compute: execution {ExecutionId} step {StepType} ({JobType}) runs locally: the decision was {Reason} but no CloudPool worker is online",
                request.ExecutionId, request.StepType ?? "-", ComputeJobTypes.ToWire(request.JobType), decision.Reason);
            return ComputeDecision.Local(ComputeDecisionReasons.PoolEmptyLocalFallback);
        }

        if (decision.Target != ComputeTargetKind.Local)
        {
            _logger.LogInformation(
                "compute: execution {ExecutionId} step {StepType} ({JobType}) -> {Target} ({Reason})",
                request.ExecutionId, request.StepType ?? "-", ComputeJobTypes.ToWire(request.JobType), decision.Target, decision.Reason);
        }

        return decision;
    }

    private int _storageWarned;

    /// <summary>Local when this job may not go to the pool (job type not opened, or storage a node cannot reach); null when it may.</summary>
    private ComputeDecision? NarrowPoolDecision(ComputeTargetRequest request)
    {
        string wire = ComputeJobTypes.ToWire(request.JobType);
        string[] allowed = _options.CloudPoolJobTypeList();
        if (allowed.Length > 0 && !allowed.Any(t => string.Equals(t, wire, StringComparison.OrdinalIgnoreCase)))
        {
            return ComputeDecision.Local(ComputeDecisionReasons.PoolJobTypeNotEnabled);
        }

        if (_options.RequirePublicStorageForPool
            && _options.PoolStorageEndpoint is { } endpoint
            && !PoolStorageReachability.IsPublicHttps(endpoint))
        {
            if (Interlocked.Exchange(ref _storageWarned, 1) == 0)
            {
                _logger.LogWarning(
                    "compute: pool jobs run locally because the object store address presigned URLs are signed against is not a public https address; set MinIO:PublicEndpoint (see your operator documentation)");
            }

            return ComputeDecision.Local(ComputeDecisionReasons.PoolStorageUnreachable);
        }

        return null;
    }

    /// <summary>
    /// Whether the organization has an ELIGIBLE desktop runner online, through the gateway's
    /// <c>GET /internal/v1/runners?org=&amp;caps=</c>, cached for <c>Compute:RunnerQueryCacheSeconds</c>.
    /// <para>
    /// This checks the runner's KIND itself and does not rely on the query filter: a CloudPool worker
    /// has <c>OrgID = null</c>, and the gateway's filter only excludes entries whose org is non-empty
    /// and different, so a pool worker is returned by an org-scoped query. Counting it as "the org has
    /// a runner" would send the org's work to our own pool while reporting it as local compute.
    /// </para>
    /// <para>
    /// The two failure modes are kept apart, because they call for different answers. A gateway that
    /// is not there at all (<see cref="ComputeFabricUnavailableException"/>: unreachable, or 503
    /// because it has no internal token) also cannot take a CloudPool job, so the caller degrades to
    /// Local. A gateway that REFUSED the query (<see cref="RunnerGatewayRequestException"/>: a wrong
    /// token, a malformed query) read as "no runner online" instead — the conservative answer, since
    /// an unconfirmed runner is never routed to. Either way nothing is cached as a fact for longer
    /// than the cache window, and neither fails a user's workflow.
    /// </para>
    /// </summary>
    private async Task<RunnerProbe> ProbeRunnerAsync(ComputeTargetRequest request, CancellationToken cancellationToken)
    {
        (Guid OrganizationId, string JobType) key = (request.OrganizationId, ComputeJobTypes.ToWire(request.JobType));
        long now = _time.GetUtcNow().UtcTicks;
        if (_presence.TryGetValue(key, out PresenceEntry? cached) && cached.ExpiresAtTicks > now)
        {
            return new RunnerProbe(cached.Online, cached.PoolOnline, cached.FabricUnavailable, cached.WakerHealthy);
        }

        bool online = false;
        bool poolOnline = false;
        bool fabricUnavailable = false;
        bool wakerHealthy = false;
        try
        {
            RunnerFleet fleet = await _gateway
                .ListRunnersAsync(request.OrganizationId == Guid.Empty ? null : request.OrganizationId, [key.JobType], cancellationToken)
                .ConfigureAwait(false);
            online = fleet.HasEligible(RunnerKind.Desktop, [key.JobType]);
            poolOnline = fleet.HasEligible(RunnerKind.CloudPool, [key.JobType]);
            wakerHealthy = fleet.WakerHealthy;
        }
        catch (ComputeFabricUnavailableException ex)
        {
            fabricUnavailable = true;
            _logger.LogInformation(
                "compute: the runner gateway is unavailable ({Message}); execution {ExecutionId} stays local for this step",
                ex.Message, request.ExecutionId);
        }
        catch (RunnerGatewayRequestException ex)
        {
            // A refusal is a misconfiguration (a wrong token, a malformed query), but it says nothing
            // about whether a runner exists, and it must not fail a user's workflow: the conservative
            // reading is "no runner confirmed".
            _logger.LogWarning(
                ex,
                "compute: the runner gateway refused the runner query for organization {OrganizationId}; treating it as no runner online",
                request.OrganizationId);
        }

        _presence[key] = new PresenceEntry(online, poolOnline, fabricUnavailable, wakerHealthy, now + _presenceTtl.Ticks);
        return new RunnerProbe(online, poolOnline, fabricUnavailable, wakerHealthy);
    }

    /// <summary>Whether an eligible desktop runner and an eligible pool worker are online, whether the pool waker is healthy, and whether the answer came from a gateway that is not there at all.</summary>
    private readonly record struct RunnerProbe(bool Online, bool PoolOnline, bool FabricUnavailable, bool WakerHealthy = false);

    private sealed record PresenceEntry(bool Online, bool PoolOnline, bool FabricUnavailable, bool WakerHealthy, long ExpiresAtTicks);
}
