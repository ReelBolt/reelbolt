namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// <c>GET /api/v1/activity</c>: the background jobs of the caller's active workspace. The wire shape is the contract in
/// the background-activity design (camelCase JSON).
/// </summary>
public sealed record ActivityFeedDto(IReadOnlyList<ActivityItemDto> Items, DateTime ServerTime, int ActiveCount);

/// <summary>One background job. <see cref="Id"/> is <c>&lt;source&gt;:&lt;guid&gt;</c> (run, analysis) and stable across polls.</summary>
public sealed record ActivityItemDto(
    string Id,
    string Type,
    string State,
    Guid ProjectId,
    string ProjectName,
    string? Title,
    Guid? ExecutionId,
    Guid? WorkflowId,
    ActivitySubjectDto Subject,
    ActivityStepDto? Step,
    double? Progress,
    ActivityCreditsDto Credits,
    ActivityFailureDto? Failure,
    bool StartedByYou,
    bool Cancellable,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? EndedAt);

/// <summary>What the job is about; every member is optional and only the ones that apply are set.</summary>
public sealed record ActivitySubjectDto(
    Guid? TimelineId = null,
    Guid? DocumentId = null,
    string? MediaRef = null,
    Guid? SourceExecutionId = null,
    Guid? LinkId = null,
    string? AnalysisKind = null,
    int? KindNumber = null);

public sealed record ActivityStepDto(int Order, int Count, string? StepType);

/// <summary>Credits in whole credits (not millicredits); both null when billing is off or the figure is unknown.</summary>
public sealed record ActivityCreditsDto(double? HeldCredits, double? ChargedCredits);

public sealed record ActivityFailureDto(string? Code, bool? Refunded);
