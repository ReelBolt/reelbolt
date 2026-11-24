namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Where a step's work actually ran. D6's selector picks it per execution (sessions) or per step
/// (media jobs); D12 records the outcome on the step result.
/// <para>
/// Persisted as an <c>int</c>, deliberately — unlike the other enums on
/// <see cref="WorkflowStepResult"/>, which are stored by name. <c>workflow_step_results</c> is mapped
/// by BOTH EF contexts (the WorkflowEngine owns and writes it, the Inference API maps it read-only),
/// and every name-converted enum in it needs the identical <c>HasConversion&lt;string&gt;()</c> pair on
/// both sides or one context reads a varchar column as an integer. A numeric enum has no such pair to
/// forget, which is why the plan specifies this one as an int. Append-only: the numbers are the
/// stored values.
/// </para>
/// </summary>
public enum ComputeTargetKind
{
    /// <summary>
    /// No compute fabric was in play: the engine ran the step in-process, exactly as every install
    /// does today. This is the value <c>Compute:Mode=Local</c> (the default, and the self-host
    /// behaviour) produces, and the only one that existed before D6.
    /// </summary>
    Local = 0,

    /// <summary>One of our own worker VMs. Trusted — but its output is verified anyway (D12).</summary>
    CloudPool = 1,

    /// <summary>
    /// The organization's own paired machine. Untrusted by construction (the binary is controlled by
    /// its owner and may be modified), so its output is verified before promotion and tagged
    /// <see cref="WorkflowStepResult.FromRunner"/> and excluded from the step-result cache.
    /// </summary>
    OrgRunner = 2
}
