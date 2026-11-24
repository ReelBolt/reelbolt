using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Execution.Caching;

/// <summary>
/// Decides whether a given <see cref="WorkflowStep"/>'s result is a candidate for
/// cross-execution caching at all. This is a pure, side-effect-free question about the STEP
/// DEFINITION — it says nothing about whether a matching cache entry actually exists (that is
/// <see cref="IStepResultCache.TryGetAsync"/>'s job).
/// </summary>
public interface IStepCachePolicy
{
    /// <summary>
    /// True when <paramref name="step"/> is eligible to be read from and written to the
    /// step-result cache. See <see cref="StepCachePolicy.IsCacheable"/> for the actual decision
    /// table and its rationale.
    /// </summary>
    bool IsCacheable(WorkflowStep step);

    /// <summary>
    /// D12: True when a COMPLETED <paramref name="result"/> may be written to the step-result cache.
    /// Deliberately a second question, asked about the RESULT rather than the step definition:
    /// <see cref="IsCacheable"/> is a pure property of the <see cref="WorkflowStep"/> and is answered
    /// BEFORE the step runs (it decides whether the cache is even consulted), while admissibility
    /// depends on what the run actually did — specifically on which compute target produced the
    /// output, which is only known once it has.
    /// <para>
    /// A result produced by the organization's own runner is inadmissible. This is not a trust
    /// heuristic: a runner is a machine the CUSTOMER controls, its output is verified once, for the
    /// run that produced it, and a
    /// cache hit would replay those bytes into a later run with no executor having verified them for
    /// that run. A <see cref="Shared.Data.Models.ComputeTargetKind.CloudPool"/> result is one of our
    /// own workers: verified too, but trusted, and therefore still admissible — the exclusion is
    /// exactly the OrgRunner case, not "anything that came from a job".
    /// </para>
    /// <para>
    /// The caller must require BOTH: <see cref="IsCacheable"/>(step) to read or write at all, and
    /// <see cref="IsAdmissible"/>(result) to write.
    /// </para>
    /// </summary>
    bool IsAdmissible(StepExecutionResult result);
}
