using System.Text.Json.Serialization;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// How a <c>StepType.Conditional</c> step decides which branch to take.
///
/// <para>
/// Exhaustively switched over in <c>ConditionalStepExecutor</c> — never tested with a single
/// equality check — so an unrecognised value produced by a bad binding or a cast means
/// <b>today's expression path</b>, never "not <see cref="Expression"/>" and certainly not "call a
/// Decision provider". This is the same fail-closed discipline <c>DecisionGateMode</c> uses.
/// </para>
/// </summary>
public enum ConditionMode
{
    /// <summary>
    /// Today's behaviour: evaluate <c>WorkflowStep.ConditionExpression</c> with NCalc against the
    /// accumulated workflow output. The default — a config-less step and an explicitly
    /// <see cref="Expression"/> step are the same thing.
    /// </summary>
    Expression,

    /// <summary>
    /// Ask a System One <c>Noul</c> (yes/no) question over a <b>bounded</b> view of the prior
    /// step's output, then evaluate <c>ConditionExpression</c> with the decision's own parameters
    /// (<c>decision.p_yes</c>, <c>decision.p_no</c>, <c>decision.confidence</c>,
    /// <c>decision.escalated</c>) in scope. When <c>ConditionExpression</c> is null/blank the step
    /// compares <c>decision.p_yes</c> against <see cref="ConditionalStepConfig.Threshold"/> itself.
    ///
    /// <para>
    /// <b>Never enabled by default</b> — <see cref="Expression"/> is the default everywhere, and no
    /// shipped configuration sets this. With no Decision provider row configured (the normal local
    /// case) the whole arm degrades to <c>false</c>: the False branch, exactly as an unresolvable
    /// expression already does.
    /// </para>
    /// </summary>
    Decision
}

/// <summary>
/// Typed configuration for a <c>StepType.Conditional</c> step, deserialised from
/// <c>WorkflowStep.ConditionalConfigJson</c> (nullable jsonb — null means "no config", which is
/// <see cref="ConditionMode.Expression"/>).
///
/// <para>
/// The name ends in <c>StepConfig</c> on purpose: <c>StepConfigSchemaExporter</c> discovers step
/// configuration records reflectively by that suffix and matches them to the <c>StepType</c> member
/// of the same name (<c>Conditional</c>), so the assistant/MCP <c>ListStepSchemas</c> tool exports
/// this shape with no hand-written registration. Property names serialise camelCase
/// (<c>mode</c>, <c>decisionQuestion</c>, <c>threshold</c>, <c>providerId</c>,
/// <c>timeoutSeconds</c>), matching every other step config record and the web-side schema.
/// </para>
/// </summary>
/// <param name="Version">Schema version of this record. Bump only for a breaking shape change.</param>
/// <param name="Mode">Whether this step decides by expression (the default) or by a decision model.</param>
/// <param name="DecisionQuestion">
/// The <c>Noul</c> question's name in <see cref="ConditionMode.Decision"/>, or null to derive one
/// from <c>ConditionExpression</c> (and from <see cref="Threshold"/> when that is blank too). It
/// identifies the answer in the provider's result and is what lands in the calibration view's
/// <c>question</c> column, so the same site stays groupable across runs.
/// </param>
/// <param name="Threshold">
/// The yes-probability the step compares against when <c>ConditionExpression</c> is null/blank in
/// <see cref="ConditionMode.Decision"/> — the brief's "True branch when p(yes) ≥ Threshold". It is
/// deliberately a <b>step-level</b> knob rather than a reuse of the gate's
/// <c>DecisionGateConfig.AcceptAt</c>: <c>AcceptAt</c> answers "is the model confident enough to be
/// trusted" (and is the separate <c>decision.escalated</c> parameter), whereas this answers "is
/// yes more likely than not for this branch". Ignored in <see cref="ConditionMode.Expression"/>.
/// </param>
/// <param name="ProviderId">
/// The specific Decision provider to use, or null to resolve the configured default. Threaded
/// straight into <c>DecisionGateConfig.ProviderId</c>.
/// </param>
/// <param name="TimeoutSeconds">
/// The gate-level budget for the whole decision call, threaded into
/// <c>DecisionGateConfig.TimeoutSeconds</c>. A timeout is a gate failure and therefore evaluates
/// <c>false</c> — a slow or hanging provider can never block a Conditional step, let alone fail it.
///
/// <para>
/// This is the <b>raw, client-supplied</b> value and is what round-trips through JSON unchanged.
/// What the executor actually passes to the gate is <see cref="ClampedTimeoutSeconds"/>, because a
/// non-positive value here would otherwise mean "no deadline at all" and an enormous one would
/// otherwise make the gate's own <c>CancelAfter</c> throw. See <see cref="DecisionGateLimits"/>.
/// </para>
/// </param>
/// <param name="SourceStepOrder">
/// The earlier step whose output the condition reads, or null (the default) for the previous step's
/// output, which is what every Conditional read before this member existed. It lets a branch placed after
/// other steps still read the step that decided it: the profile template's Conditional after the product
/// analyzers re-checks the Parse step's <c>$.view.fallbackDesign</c>, not the last analyzer's output. A
/// source step that has not run in this execution (skipped, or a later step) evaluates <c>false</c>, the
/// branch an unresolvable identifier already takes. The step's own output is still the previous step's,
/// passed through unchanged. Applies to both modes (it is the state a Decision is asked about too).
/// </param>
[StepCard(
    "Chooses which later steps run by checking an earlier step's output.",
    ["An earlier step whose output decides the branch"],
    "A yes or no branch decision",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.NoCredits,
    ReadableBy = ["Later steps in the branch"],
    PairsWith = ["Agent: the branch runs the steps it guards"])]
public sealed record ConditionalStepConfig(
    int Version = 1,
    ConditionMode Mode = ConditionMode.Expression,
    string? DecisionQuestion = null,
    double Threshold = 0.5,
    Guid? ProviderId = null,
    int TimeoutSeconds = 5,
    int? SourceStepOrder = null)
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

    /// <summary>
    /// True unless the step provably decides by expression alone. <see cref="ConditionMode.Decision"/> asks a
    /// Decision provider (a model); any value outside the declared members is treated as a model call too, so the
    /// run-rate classifier fails closed (the executor would run it as an expression, but the classifier never
    /// assumes that). Adding a mode? Decide here whether it calls a provider.
    /// </summary>
    [JsonIgnore]
    public bool CallsAnyProvider => Mode != ConditionMode.Expression;
}
