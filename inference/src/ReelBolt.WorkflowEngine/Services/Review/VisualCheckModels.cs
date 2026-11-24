namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>One sampled frame of a visual check: its 1-based number in the set sent, and where it was taken.</summary>
public sealed record VisualCheckFrameTime(int Frame, double AtSec);

/// <summary>
/// One finding the vision model reported. <see cref="Frame"/> is one of the frame numbers sent and
/// <see cref="AtSec"/> is OUR time for that frame (never the model's); <see cref="Kind"/> and
/// <see cref="Severity"/> are closed tokens; <see cref="Detail"/> is sanitized, capped model prose.
/// </summary>
public sealed record VisualCheckFinding(int Frame, double AtSec, string Kind, string Detail, string Severity);

/// <summary>The playable output a visual check samples: the step that produced it and its storage key.</summary>
public sealed record VisualCheckSource(int StepOrder, string StorageKey);

/// <summary>
/// What one iteration's visual check concluded. <see cref="StatusChecked"/> carries the frames sampled
/// and the findings (possibly none); <see cref="StatusUnavailable"/> carries a fixed, engine-written reason
/// and never caps. <see cref="Capped"/> is true exactly when a checked outcome has a <c>high</c> finding.
/// </summary>
public sealed record VisualCheckOutcome(
    string Status,
    int? SourceStepOrder,
    IReadOnlyList<VisualCheckFrameTime> Frames,
    IReadOnlyList<VisualCheckFinding> Findings,
    int FailScoreCap,
    string? Reason)
{
    public const string StatusChecked = "checked";
    public const string StatusUnavailable = "unavailable";
    public const string SeverityHigh = "high";

    public static VisualCheckOutcome Checked(
        int sourceStepOrder, IReadOnlyList<VisualCheckFrameTime> frames, IReadOnlyList<VisualCheckFinding> findings, int failScoreCap) =>
        new(StatusChecked, sourceStepOrder, frames, findings, failScoreCap, null);

    public static VisualCheckOutcome Unavailable(string reason) =>
        new(StatusUnavailable, null, [], [], 0, reason);

    public bool IsChecked => Status == StatusChecked;

    public bool Capped => IsChecked && Findings.Any(f => f.Severity == SeverityHigh);
}
