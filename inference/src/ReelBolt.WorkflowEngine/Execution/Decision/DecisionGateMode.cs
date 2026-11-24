namespace ReelBolt.WorkflowEngine.Execution.Decision;

/// <summary>
/// How a <see cref="DecisionGate"/> treats a decision site.
///
/// <para>
/// Exhaustively switched over in <see cref="DecisionGate.DecideAsync"/> — never tested with a
/// single equality check. An unrecognised value (a bad appsettings binding, a cast) means
/// <b>do nothing</b>, not "not <see cref="Off"/>" and certainly not "make the outbound call":
/// the gate must fail closed, because the value it is switching on decides whether an agent's
/// real output is used.
/// </para>
/// </summary>
public enum DecisionGateMode
{
    /// <summary>The gate does nothing: no provider resolution, no provider call, no observation.</summary>
    Off,

    /// <summary>
    /// Phase 1's behaviour. The decision model runs alongside the agent's real (System Two)
    /// decision purely to observe agreement/confidence; its answer never changes anything.
    /// </summary>
    Shadow,

    /// <summary>
    /// The decision model's answer may be used <i>instead of</i> running the agent, when every
    /// question clears <see cref="DecisionGateConfig.AcceptAt"/> and
    /// <see cref="DecisionGateConfig.MinMargin"/>; otherwise the caller escalates to the agent.
    /// <b>Never enabled by default</b> — every shipped configuration leaves this <see cref="Off"/>.
    /// </summary>
    Gate,
}
