namespace ReelBolt.WorkflowEngine.Execution.Decision;

/// <summary>
/// Per-site configuration for a <see cref="DecisionGate"/> run.
/// </summary>
/// <param name="Mode">
/// How the gate treats this site. Defaults to <see cref="DecisionGateMode.Off"/> and must stay
/// that way in every shipped configuration.</param>
/// <param name="AcceptAt">
/// The minimum top-question probability for an answer to be acceptable. This is the phase-1
/// default and is a <b>fallback, not a tuned value</b> — real values come from calibration data
/// (decisions.md §3: there is no live calibration data yet, so nothing may claim "calibrated").</param>
/// <param name="MinMargin">
/// The minimum gap between the top and runner-up probability for an answer to be acceptable.
/// Same status as <paramref name="AcceptAt"/>: a phase-1 fallback, never a tuned number.</param>
/// <param name="ProviderId">
/// The specific Decision provider to use, or null to resolve the configured default.</param>
/// <param name="TimeoutSeconds">
/// The gate-level budget for the whole <c>DecideAsync</c> call (provider resolution plus the
/// request), applied as a linked <see cref="System.Threading.CancellationTokenSource"/> deadline.
/// A value of zero or less disables the gate-level deadline and leaves the provider's own
/// <c>HttpClient</c> timeout as the only bound.
///
/// <para>
/// This exists because the provider's configured timeout is up to 300 s and the gate sits on the
/// critical path of a Colorist/MusicSupervisor step: without a gate-level budget a slow or hanging
/// provider delays the real agent. On expiry the gate gives up and the caller escalates to the
/// agent — it never turns a slow provider into a failed step.
/// </para></param>
public sealed record DecisionGateConfig(
    DecisionGateMode Mode = DecisionGateMode.Off,
    double AcceptAt = 0.85, double MinMargin = 0.25, Guid? ProviderId = null,
    int TimeoutSeconds = 5);
