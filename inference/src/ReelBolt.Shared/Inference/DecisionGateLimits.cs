namespace ReelBolt.Shared.Inference;

/// <summary>
/// The one definition of the Decision gate's timeout bounds — the minimum, the default and the
/// maximum — consumed by every place that turns a client-supplied <c>TimeoutSeconds</c> into an
/// effective deadline.
///
/// <para>
/// <b>Why a single definition.</b> Before this type, <c>TimeoutSeconds</c> reached the gate
/// unclamped from three independent places (the <c>Conditional</c> step config, the
/// <c>ReviewLoop</c> step config, and the room convening gate), each with its own primitive
/// behaviour, which produced two distinct failure shapes:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Zero or negative silently disabled the deadline.</b> The gate's
/// <c>TimeoutSeconds &gt; 0 ? linkedTokenSource : null</c> branch meant a non-positive value
/// created no cancellation source at all, so the provider's own up-to-300 s <c>HttpClient</c>
/// timeout became the only bound — re-exposing the very defect the gate-level budget exists to
/// fix, for that step, with no warning.
/// </item>
/// <item>
/// <b>A very large value made the gate permanently inert.</b>
/// <see cref="System.Threading.CancellationTokenSource.CancelAfter(TimeSpan)"/> throws
/// <see cref="System.ArgumentOutOfRangeException"/> once the delay exceeds
/// <see cref="int.MaxValue"/> milliseconds (~24.8 days), and that throw happened <i>inside</i> the
/// gate's own <c>try</c>, so the generic soft-failure catch swallowed it and returned "no
/// decision" — a warning per call that named no cause, forever.
/// </item>
/// </list>
///
/// <para>
/// <b>A deadline is therefore unrepresentable as "none".</b> There is no legitimate configuration
/// in which a step waits on a Decision provider indefinitely: the gate sits on the critical path of
/// a real agent, so its whole purpose is to bound how long that provider can delay the step.
/// </para>
/// </summary>
public static class DecisionGateLimits
{
    /// <summary>
    /// The floor, in seconds, for any effective gate deadline. A positive floor — not zero — is
    /// what makes "no deadline" unrepresentable: every clamped value still creates a cancellation
    /// source and a real budget.
    /// </summary>
    public const int MinSeconds = 1;

    /// <summary>
    /// The value a non-positive configured timeout resolves to, in seconds. Chosen to equal
    /// <c>DecisionGateConfig.TimeoutSeconds</c>'s own default so that a config saying <c>0</c> —
    /// "I did not set this" — behaves exactly as an omitted value always has.
    /// </summary>
    public const int DefaultSeconds = 5;

    /// <summary>
    /// The ceiling, in seconds, for any effective gate deadline. Matches the provider's own
    /// documented 300 s <c>HttpClient</c> ceiling: waiting longer than the transport itself will is
    /// pointless, and holding the value at or below it removes the
    /// <see cref="System.Threading.CancellationTokenSource.CancelAfter(TimeSpan)"/> millisecond
    /// overflow entirely.
    /// </summary>
    public const int MaxSeconds = 300;

    /// <summary>
    /// Resolves a client-supplied <c>TimeoutSeconds</c> to the effective gate budget, in seconds.
    ///
    /// <para>
    /// Two rules, and only two:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <b>Non-positive ⇒ <see cref="DefaultSeconds"/>.</b> Deliberately not
    /// <see cref="MinSeconds"/>: a config that says <c>0</c> means "I did not set this", not "give
    /// me the smallest possible deadline". The point of the fallback is to restore the default
    /// behaviour, never to introduce a second, harsher one.
    /// </item>
    /// <item>
    /// <b>Anything above <see cref="MaxSeconds"/> ⇒ <see cref="MaxSeconds"/>.</b> Which includes
    /// <see cref="int.MaxValue"/>, so the clamp is total over <see cref="int"/> and no input can
    /// reach <see cref="System.Threading.CancellationTokenSource.CancelAfter(TimeSpan)"/>'s
    /// overflow.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// A value already inside <c>[<see cref="MinSeconds"/>, <see cref="MaxSeconds"/>]</c> is
    /// returned unchanged, so an in-range configured timeout keeps today's exact behaviour.
    /// </para>
    /// </summary>
    /// <param name="timeoutSeconds">The raw, client-supplied timeout in seconds.</param>
    /// <returns>The effective timeout in seconds, always within the documented bounds.</returns>
    public static int Clamp(int timeoutSeconds)
    {
        if (timeoutSeconds <= 0)
            return DefaultSeconds;

        return timeoutSeconds > MaxSeconds ? MaxSeconds : timeoutSeconds;
    }
}
