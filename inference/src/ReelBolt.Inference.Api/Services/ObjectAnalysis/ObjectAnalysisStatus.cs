using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.ObjectAnalysis;

/// <summary>What the analysis step's own row says, reduced to what the status needs.</summary>
public sealed record ObjectAnalysisStepFacts(Guid StepResultId, StepStatus Status, string? FailureCode, string? OutputErrorCode);

/// <summary>The derived state of one request: queued | running | succeeded | failed.</summary>
public sealed record ObjectAnalysisDerived(
    string Status,
    string? FailureCode,
    string? Message,
    Guid? StepResultId,
    IReadOnlyList<string>? TrackIds,
    double? CoverageEndSec)
{
    public bool Terminal => Status is ObjectAnalysisStatus.Succeeded or ObjectAnalysisStatus.Failed;
}

/// <summary>Pure derivation of an object-analysis request's status from its run, step result and artifact (nothing is pushed).</summary>
public static class ObjectAnalysisStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>The mask holds at most this many tracks.</summary>
    public const int MaxTrackIds = 8;

    public static ObjectAnalysisDerived Derive(
        ExecutionStatus? execution,
        string? executionError,
        ObjectAnalysisStepFacts? step,
        IReadOnlyList<VideoSegmentationTrack>? tracks,
        bool resultHeld = false,
        bool readFailed = false)
    {
        switch (execution)
        {
            case null or ExecutionStatus.Queued:
                return new(Queued, null, null, null, null, null);
            case ExecutionStatus.Running:
                return new(Running, null, null, null, null, null);
            case ExecutionStatus.Cancelled:
                return Fail("RUN_CANCELLED", step?.StepResultId);
        }

        if (step is { Status: StepStatus.Failed } || execution == ExecutionStatus.Failed)
        {
            string? code = step?.FailureCode ?? step?.OutputErrorCode;
            return Fail(code, step?.StepResultId);
        }

        // A moderation-held result is not served: its own code, never "object not found", and never reused.
        if (resultHeld) return Fail(ObjectAnalysisMessages.ResultUnavailable, step?.StepResultId);

        // A transient artifact read error is not a verdict: stay running so the next read retries, and write nothing terminal.
        if (readFailed && step is not null) return new(Running, null, null, null, null, null);

        List<VideoSegmentationTrack> usable = (tracks ?? [])
            .Where(IsUsable)
            .Take(MaxTrackIds)
            .ToList();
        if (step is null || usable.Count == 0)
            return Fail(ObjectAnalysisMessages.ObjectNotFound, step?.StepResultId);

        return new(Succeeded, null, null, step.StepResultId,
            usable.Select(t => t.Id).ToList(),
            usable.Max(SegmentationTrackFacts.CoverageEndOf));
    }

    /// <summary>A track is usable when it has an id, finite times and the object appears on at least one frame (the engine's Empty verdict is "missing from every frame").</summary>
    public static bool IsUsable(VideoSegmentationTrack t) =>
        !string.IsNullOrEmpty(t.Id)
        && double.IsFinite(t.StartSec) && double.IsFinite(t.EndSec)
        && t.FrameCount > 0
        && (t.Facts?.EmptyFrames ?? 0) < t.FrameCount;

    private static ObjectAnalysisDerived Fail(string? code, Guid? stepResultId) =>
        new(Failed, code, ObjectAnalysisMessages.For(code), stepResultId, null, null);
}
