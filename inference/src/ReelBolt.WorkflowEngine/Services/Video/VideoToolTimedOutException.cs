namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// An analysis pass was killed at its deadline. <see cref="HeavySource"/> says the source was above the
/// analysis-copy threshold, which the step reports as a platform-class SOURCE_TOO_DEMANDING instead of an
/// unexplained failure.
/// </summary>
public sealed class VideoToolTimedOutException(string message, bool heavySource) : InvalidOperationException(message)
{
    public bool HeavySource { get; } = heavySource;

    /// <summary>The exception for a failed pass: the timeout type when the deadline killed it, else a plain failure.</summary>
    public static InvalidOperationException For(string message, bool timedOut, int analysisCopyMinDurationSec) =>
        timedOut
            ? new VideoToolTimedOutException(message, SourceTimeoutPolicy.IsHeavy(analysisCopyMinDurationSec))
            : new InvalidOperationException(message);
}
