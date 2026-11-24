using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A record of an LLM decision made during workflow execution: the question posed,
/// the available options, the decision made, confidence/probabilities, and associated metadata.
/// Owned by the WorkflowEngine (table <c>decision_observations</c>).
///
/// <para>
/// <b>Deliberately no foreign key to <c>workflow_executions</c> or <c>workflow_step_results</c>.</b>
/// Observations are indexed by denormalized <see cref="ExecutionId"/> and <see cref="StepOrder"/>
/// (the same rationale as <see cref="WorkflowStepCacheEntry"/> — see that entity's doc comment)
/// rather than enforced entity relationships, so observations survive execution or step-result
/// deletion and can be queried/analyzed independently of workflow definition evolution.
/// </para>
/// </summary>
public class DecisionObservation
{
    public Guid Id { get; set; }

    /// <summary>The execution this decision was made during. Not a real FK — see the class doc comment.</summary>
    public Guid ExecutionId { get; set; }

    /// <summary>The project of the execution (denormalized; null on rows written before tenancy or when unknown).</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>The organization of the execution (denormalized; null on rows written before tenancy or when unknown).</summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>The ordinal position of the step within the execution that made this decision.</summary>
    public int StepOrder { get; set; }

    /// <summary>The agent type that made this decision.</summary>
    public AgentType AgentType { get; set; }

    /// <summary>Free-text or structured site/context identifier for where this decision occurred in the workflow.</summary>
    public string Site { get; set; } = "";

    /// <summary>The question/prompt posed to the decision-making agent.</summary>
    public string Question { get; set; } = "";

    /// <summary>SHA-256 hash of the available options offered to the decision-maker, for deduplication.</summary>
    public string OptionsHash { get; set; } = "";

    /// <summary>The kind of decision: Choice, Score, Noul.</summary>
    public DecisionKind Kind { get; set; }

    /// <summary>The answer/choice selected by the decision-maker.</summary>
    public string Answer { get; set; } = "";

    /// <summary>Confidence level reported by the model (0.0-1.0), if available.</summary>
    public double? Confidence { get; set; }

    /// <summary>JSON object encoding the probability distribution across options, if available.</summary>
    public string ProbabilitiesJson { get; set; } = "{}";

    /// <summary>String mode or method identifier for how the decision was made (e.g., "sampling", "greedy", "temperature").</summary>
    public string Mode { get; set; } = "";

    /// <summary>True if this decision was escalated or required human review.</summary>
    public bool Escalated { get; set; }

    /// <summary>JSON object encoding downstream outcome, feedback, or consequences if this decision was later observed or evaluated.</summary>
    public string? DownstreamOutcomeJson { get; set; }

    /// <summary>The inference provider ID that produced this decision, if tracked.</summary>
    public Guid? ProviderId { get; set; }

    /// <summary>The model identifier or version that made this decision.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Prompt tokens the decision model consumed for this observation, taken from
    /// <c>DecisionResult.InputTokens</c> — a value phase 1 read and discarded.
    /// Null when the provider reported no usage, and on historic rows written before this
    /// column existed ("not recorded", not zero).
    /// </summary>
    public int? DecisionInputTokens { get; set; }

    /// <summary>
    /// Completion tokens the decision model produced for this observation, taken from
    /// <c>DecisionResult.OutputTokens</c> — a value phase 1 read and discarded.
    /// Null when the provider reported no usage, and on historic rows written before this
    /// column existed ("not recorded", not zero).
    /// </summary>
    public int? DecisionOutputTokens { get; set; }

    /// <summary>
    /// Tokens the shadowed agent itself spent, when an agent actually ran.
    /// Null on a Gate-accepted step, where no agent ran, and on historic rows.
    /// <para>
    /// This is why the calibration view's "tokens saved" is <b>projected at a supplied
    /// threshold, never measured</b>: Shadow pays for the agent it shadows (so nothing was
    /// saved), and a null means we did not record the counterfactual at all — the projection
    /// supplies the threshold and the reader is told it is a projection.
    /// </para>
    /// </summary>
    public int? AgentTokensUsed { get; set; }

    /// <summary>
    /// The accept metric this row was written under — the basis its escalation was decided on, as
    /// the short name of a <see cref="DecisionAcceptMetric"/> member ("Top", "AtOrAbove",
    /// "Affirmative").
    ///
    /// <para>
    /// <b>A null means <see cref="DecisionAcceptMetric.Top"/>, and that is a fact rather than a
    /// fallback.</b> Every row written before this column existed was written by a gate whose only
    /// rule was the top/margin rule, so reading an absent declaration as <c>Top</c> recovers exactly
    /// the rule that produced the row. A reader that wants to treat an unrecognised value as
    /// "unknown" must check for that separately — the calibration aggregation excludes such a row
    /// from its escalation figure rather than guessing at it.
    /// </para>
    ///
    /// <para>
    /// Why it is persisted at all: the calibration view recomputes a row's escalation at a
    /// <b>supplied</b> threshold, and a recomputation is only exact if it reduces the probabilities
    /// the way the site that wrote the row did. Persisting the declaration is also what stops the
    /// next call site from silently reintroducing the class of bug this column exists to remove.
    /// </para>
    /// </summary>
    public string? AcceptMetric { get; set; }

    /// <summary>
    /// The level cutoff <see cref="AcceptMetric"/> needs, or <b>null for every metric that does not
    /// need one</b> (which is all of them except <see cref="DecisionAcceptMetric.AtOrAbove"/>).
    ///
    /// <para>
    /// It is a step property — the <c>ReviewLoop</c> step's <c>MinScore</c> — and this entity
    /// deliberately has no foreign key to any step (see the class doc comment), so the value is
    /// denormalized here. Without it <c>Σ p(level &gt;= MinScore)</c> cannot be recomputed from the
    /// row at all, which is what makes this column the price of the metric declaration rather than an
    /// optional extra.
    /// </para>
    /// </summary>
    public int? ScoreCutoff { get; set; }

    /// <summary>When this observation was recorded.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
