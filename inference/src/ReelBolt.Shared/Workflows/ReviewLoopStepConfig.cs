using System.Text.Json.Serialization;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// JSON configuration for a <c>StepType.ReviewLoop</c> workflow step. Deserialized from
/// <c>WorkflowStep.ReviewLoopConfigJson</c> (decision-engine phase 2, D-008).
///
/// <para>
/// The name ends in <c>StepConfig</c> deliberately: <see cref="StepConfigSchemaExporter"/> scans
/// this namespace reflectively for sealed records ending in that suffix and matches them to a
/// <see cref="ReelBolt.Shared.Data.Models.StepType"/> member by name, so <c>ReviewLoopStepConfig</c>
/// is matched to <c>StepType.ReviewLoop</c> with no hand-written registration. Renaming it breaks
/// the assistant/MCP <c>ListStepSchemas</c> surface silently.
/// </para>
///
/// <para>
/// <b>Calibrated early stop.</b> A ReviewLoop step normally runs the reviewer (System Two) on every
/// iteration and loops back when its score is below <c>MinScore</c>. With this config enabled, a
/// System One decision model is asked a <c>Score</c> question over the deterministic review facts the
/// prior step already computed, and when <c>p(level &gt;= MinScore)</c> clears <see cref="AcceptAt"/>
/// the reviewer is skipped entirely and the workflow advances (so the reviewer is never paid for on
/// a run the fast model is sure about). Every other shape — a bimodal distribution, a low
/// <c>p(level &gt;= MinScore)</c>, a missing provider, a timeout, any failure — escalates to the
/// reviewer, i.e. behaves exactly as the step does today.
/// </para>
///
/// <para>
/// <b><see cref="Enabled"/> is false by default and every shipped configuration keeps it that
/// way</b> (plan Global Constraints: nothing in this initiative is on by default, and the local
/// install has no <c>Decision</c> provider row at all). Disabled, absent or unparseable config is
/// byte-identical to the pre-change executor.
/// </para>
/// </summary>
/// <param name="Version">Schema version, so a future shape change can be migrated rather than guessed.</param>
/// <param name="Enabled">
/// Whether the calibrated early stop runs at all. Default <c>false</c>; a disabled gate takes
/// today's exact path (the reviewer runs, <c>ParseReviewScore</c>, the loop-back rule).</param>
/// <param name="AcceptAt">
/// The minimum <c>p(level &gt;= MinScore)</c> for an early stop. This is the phase-1 default and is
/// a <b>fallback, not a tuned value</b>: real values come from calibration data, and this environment
/// can produce none (decisions.md §3), so nothing here may be described as calibrated.</param>
/// <param name="BimodalMargin">
/// The minimum gap between the top two rubric levels' probabilities. A narrower gap means the fast
/// model is torn between two levels ("the edit is a 7 or an 8") — exactly the case a fast model is
/// least trustworthy on — so it escalates to the reviewer rather than early-stopping, even when
/// <c>p(level &gt;= MinScore)</c> itself is high. Checked <b>before</b> <see cref="AcceptAt"/> so a
/// distribution that is both high and bimodal resolves deterministically to escalate.</param>
/// <param name="ProviderId">
/// The specific <c>Decision</c> provider to ask, or null to resolve the configured capability
/// default. A null provider (nothing configured) escalates — the normal local case, never an error.</param>
/// <param name="TimeoutSeconds">
/// The gate-level budget for the whole decision call. The gate sits before the reviewer on the
/// step's critical path, so this — not the provider's own <c>HttpClient</c> timeout — is what
/// bounds how long a slow or hanging provider can delay the step; on expiry the gate gives up and
/// the reviewer runs.
///
/// <para>
/// This is the <b>raw, client-supplied</b> value and is what round-trips through JSON unchanged.
/// What the executor actually passes to the gate is <see cref="ClampedTimeoutSeconds"/>, because a
/// non-positive value here would otherwise mean "no deadline at all" — and here that would delay
/// the reviewer, not just the gate — while an enormous one would otherwise make the gate's own
/// <c>CancelAfter</c> throw. See <see cref="DecisionGateLimits"/>.
/// </para>
/// </param>
/// <param name="VisualCheck">
/// The frame-sampling visual check run before the reviewer each iteration (<see cref="VisualCheckConfig"/>).
/// Independent of <see cref="Enabled"/> (which governs the calibrated early stop only). Null or disabled is
/// byte-identical to a review loop without it. Appended: positional members are append-only.</param>
/// <param name="AllowAdjustments">
/// Lets the reviewer change a closed list of quality settings of earlier steps (for example the compile's title size
/// or the analysis budget) and ask for the changes a customer can request through the change-request API (a narration
/// line, a generated shot or take, a thumbnail, a script scene), on THIS run's own copy of the workflow, never the saved
/// workflow; the loop then re-runs from the earliest changed step, paid steps included, each re-run held and charged
/// normally (docs/design/review-loop-adjustments.md). Default false: off is byte-identical (no settings block in the
/// reviewer's input, any proposal in the answer is ignored). The shipped templates turn it on. Appended last.</param>
[StepCard(
    "Has a reviewer score a step's work and sends it back for another round until it passes or the rounds run out.",
    [],
    "The reviewer's score and notes for each round",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCredits,
    ReadableBy = ["The loop target on the next round", "Any later step"],
    PairsWith = ["Agent: the loop target is the writer being reviewed"],
    OutputContract = "ReviewOutput")]
public sealed record ReviewLoopStepConfig(
    int Version = 1,
    bool Enabled = false,
    double AcceptAt = 0.85,
    double BimodalMargin = 0.15,
    Guid? ProviderId = null,
    int TimeoutSeconds = 5,
    VisualCheckConfig? VisualCheck = null,
    bool AllowAdjustments = false)
{
    /// <summary>
    /// The gate budget actually used at run time: <see cref="TimeoutSeconds"/> resolved through
    /// <see cref="DecisionGateLimits.Clamp"/>, so it is always within the documented bounds and a
    /// deadline is never disabled.
    ///
    /// <para>
    /// A computed property on purpose. <see cref="TimeoutSeconds"/> is a positional parameter of
    /// this record and must keep the value the client sent — clamping inside the constructor (or
    /// in a property initialiser) would silently rewrite the persisted/serialised config. Only the
    /// effective value is clamped.
    /// </para>
    ///
    /// <para>
    /// <see cref="JsonIgnoreAttribute"/> keeps the derived value out of both JSON and the
    /// schema <c>StepConfigSchemaExporter</c> generates for the assistant/MCP
    /// <c>ListStepSchemas</c> tool: it is not an input, so it has no business appearing in the
    /// shape a client is told to send.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public int ClampedTimeoutSeconds => DecisionGateLimits.Clamp(TimeoutSeconds);
}
