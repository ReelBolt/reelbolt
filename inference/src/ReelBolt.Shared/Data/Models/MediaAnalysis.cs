namespace ReelBolt.Shared.Data.Models;

/// <summary>Editor v2 Wave 2 (R09.1): the closed set of analysis kinds. Wire names are lower-case (<see cref="MediaAnalysisKinds"/>).</summary>
public static class MediaAnalysisKinds
{
    public const string Silence = "silence";
    public const string Shots = "shots";
    public const string Motion = "motion";
    public const string Transcript = "transcript";
    public const string Beats = "beats";

    /// <summary>Typed, frame-anchored events (layer-1 detector): deterministic and free.</summary>
    public const string Events = "events";

    public static readonly IReadOnlyList<string> All = [Silence, Shots, Motion, Transcript, Beats, Events];

    /// <summary>Only the transcript calls a paid provider; the rest is deterministic ffmpeg and free.</summary>
    public static bool IsMetered(string kind) => kind == Transcript;
}

/// <summary>The states a kind moves through. Stored as the lower-case word.</summary>
public static class MediaAnalysisStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary>
/// One analysis result cached by FILE DIGEST: the same bytes in the same organization reuse it, in any project.
/// Never shared across organizations. API-owned (migration); the engine reads it to skip work and the API writes it.
/// </summary>
public class MediaAnalysisResult
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>Lower-case hex SHA-256 of the file's bytes.</summary>
    public string Digest { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    /// <summary>The transcript language asked for; empty for every other kind and for "detect".</summary>
    public string Language { get; set; } = string.Empty;

    public string ResultJson { get; set; } = "{}";

    /// <summary>A small JSON summary (counts) so listing never loads <see cref="ResultJson"/>.</summary>
    public string SummaryJson { get; set; } = "{}";

    /// <summary>Seconds of audio the recogniser was billed for (transcript only).</summary>
    public double? MeasuredSeconds { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One requested kind for one media file in one project, and where it stands. Reached only through the project's
/// media routes (guarded at the project), so it carries no tenant filter.
/// </summary>
public class MediaAnalysisRequest
{
    public Guid Id { get; set; }

    /// <summary>Groups the kinds of one POST.</summary>
    public Guid RequestId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>The opaque media token (<c>file:guid</c> or <c>step_result:guid</c>).</summary>
    public string MediaRef { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string State { get; set; } = MediaAnalysisStates.Queued;

    /// <summary>Plain-language reason, set when <see cref="State"/> is failed.</summary>
    public string? Reason { get; set; }

    public string? Digest { get; set; }

    public Guid? ResultId { get; set; }

    /// <summary>The credit hold taken at request time for a metered kind; settled or released when the run finishes.</summary>
    public Guid? HoldReservationId { get; set; }

    public long HoldMillis { get; set; }

    public bool HoldSettled { get; set; }

    public Guid? RequestedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
