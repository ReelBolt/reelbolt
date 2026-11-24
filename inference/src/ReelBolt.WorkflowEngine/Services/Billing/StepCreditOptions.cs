using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.WorkflowEngine.Services.Billing;

/// <summary>
/// Bound from <c>Billing</c> (the same section as <see cref="CreditGateOptions"/>, <c>Billing__*</c>
/// env vars). Owns the STEP-SIDE side of the reservation: how many credits the executor holds before
/// a step runs.
///
/// <para>
/// <b>The hold now comes from B7's estimate</b> (<see cref="ICreditHoldEstimator"/>): the p90 of the
/// organization's own recorded usage for the step's type and agent, and B0's per-step-type defaults
/// (<see cref="StepCreditDefaults"/>) when it has no history yet. What used to be here — a single flat
/// <c>Billing:StepReserveMillis</c> figure (20 credits) applied to every step type — is gone: it
/// priced a Conditional step and a 4M-token AuthorAgent step identically. The only figure still owned
/// by configuration is <see cref="StepReserveMillisOverride"/>, which is unset by default and exists so
/// an operator (or a test) can pin every step to one number.
/// </para>
///
/// <para>
/// Every step type the estimator can price holds a non-zero amount, so the reservation path stays
/// live: a zero estimate short-circuits the gate to "no reservation", and a step with no reservation
/// is never settled. Only <see cref="StepType.Conditional"/> and <see cref="StepType.Extract"/> hold
/// nothing at all. <see cref="StepType.VideoGenerate"/> has held a real figure since B6c, which
/// prices it from the step's own settings first (clips x seconds at the resolved model's per-second
/// rate) and falls back to its B0 row here.
/// </para>
/// </summary>
public sealed class StepCreditOptions
{
    public const string SectionName = "Billing";

    /// <summary>
    /// When set, every step holds exactly this many millicredits instead of its estimate. Null (the
    /// default) means "use the estimate"; this is an escape hatch, not the normal path.
    /// </summary>
    public long? StepReserveMillisOverride { get; set; }

    /// <summary>
    /// The millicredits to hold for <paramref name="stepType"/> when no history-based estimator is
    /// available (one is not registered, or it could not reach the database). B0's per-step-type p50,
    /// and 0 for the two step types that are deliberately not charged.
    /// </summary>
    public long DefaultReserveMillisFor(StepType stepType)
        => StepReserveMillisOverride ?? StepCreditDefaults.MillisFor(stepType).Low;
}
