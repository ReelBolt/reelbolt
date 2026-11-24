namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>A box in fractions (0..1) of the source frame.</summary>
public sealed record SubjectTrackBoxBody(double X, double Y, double W, double H);

/// <summary>Start following the subject inside <see cref="Box"/> at <see cref="AtSec"/> of the source.</summary>
public sealed record SubjectTrackRequestBody(double AtSec, SubjectTrackBoxBody? Box);

public sealed record SubjectTrackQueuedResponse(Guid ExecutionId, string Status);

/// <summary>One tracked box at source time <c>T</c> (seconds), in source-frame fractions.</summary>
public sealed record SubjectTrackBoxResponse(double T, double X, double Y, double W, double H);

public sealed record SubjectTrackResponse(string TrackId, double StartSec, double EndSec, IReadOnlyList<SubjectTrackBoxResponse> Boxes);

/// <summary>The state of a subject-tracking run: queued | running | succeeded | failed.</summary>
public sealed record SubjectTrackStatusResponse(
    Guid ExecutionId,
    string Status,
    string? FailureCode,
    string? Message,
    Guid? AnalysisStepResultId,
    SubjectTrackResponse? Track);

/// <summary>Which analysed track to follow: a step result of this project and one of its track ids (<c>r{n}</c> or <c>g{n}</c>).</summary>
public sealed record FollowTrackBody(Guid AnalysisStepResultId, string? TrackId);

/// <summary>Compute a follow path for one clip. Enums by name; defaults Medium / Normal / Hold / false.</summary>
public sealed record FollowRequestBody(
    Guid ItemId,
    int? Revision,
    FollowTrackBody? Track,
    string? Tightness,
    string? Smoothness,
    string? OnLost,
    bool? AllowSoftZoom);

/// <summary>A camera key anchored to SOURCE time (seconds), as the clip's transform values.</summary>
public sealed record FollowKeyResponse(double SourceSec, double PositionX, double PositionY, double Scale);

public sealed record FollowAssumedResponse(int CanvasWidth, int CanvasHeight, int SourceWidth, int SourceHeight);

public sealed record FollowLostSpanResponse(double FromSec, double ToSec);

public sealed record FollowResponse(
    int Revision,
    Guid ItemId,
    IReadOnlyList<FollowKeyResponse> Keys,
    FollowAssumedResponse Assumed,
    double ZoomCap,
    bool ZoomCapLimited,
    IReadOnlyList<FollowLostSpanResponse> LostSpans,
    int ClampCount,
    IReadOnlyList<string> Warnings);
