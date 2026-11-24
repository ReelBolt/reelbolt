namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>Body of <c>objects/estimate</c>: what to find.</summary>
public sealed record ObjectAnalysisEstimateRequest(string? Prompt, string? MatchScope = null, ReelBolt.Shared.Workflows.ObjectSeed? Seed = null);

/// <summary>Body of <c>POST objects</c>: what to find and the effect the request is for (stored as intent only).</summary>
public sealed record ObjectAnalysisRequestBody(
    string? Prompt,
    string? MatchScope,
    string? Effect,
    double? Strength,
    bool? Invert = null,
    int? FeatherPx = null,
    ReelBolt.Shared.Workflows.ObjectSeed? Seed = null);

/// <summary>Answer of <c>POST objects</c>: <c>202</c> for a new or in-flight request, <c>200</c> for a reused analysis.</summary>
public sealed record ObjectAnalysisQueuedResponse(Guid RequestId, Guid ExecutionId, string Status, string? Message = null, Guid? WorkflowId = null);

/// <summary>One request, its status derived from the run. No storage key, mask hash or vendor name.</summary>
public sealed record ObjectAnalysisResponse(
    Guid RequestId,
    Guid ExecutionId,
    string Status,
    string Prompt,
    string? MatchScope,
    string Effect,
    double Strength,
    bool Invert,
    int FeatherPx,
    Guid? AnalysisStepResultId,
    IReadOnlyList<string>? TrackIds,
    double? CoverageEndSec,
    string? FailureCode,
    string? Message,
    DateTime CreatedAt,
    string? ApplyStatus = null,
    string? ApplyNote = null,
    Guid? WorkflowId = null);

/// <summary>One finished result waiting in the editor because the timeline changed. No storage key or URL.</summary>
public sealed record ObjectRequestItem(
    Guid RequestId, Guid ExecutionId, string MediaRef, string[] ClipIds, string Prompt, string Effect, double Strength, bool Invert,
    int FeatherPx, double? CoverageEndSec, Guid? AnalysisStepResultId, IReadOnlyList<string> TrackIds, DateTime CreatedAt, Guid? WorkflowId = null);

public sealed record ObjectRequestsResponse(IReadOnlyList<ObjectRequestItem> Items);

/// <summary><c>action</c> is <c>applied</c> or <c>discarded</c>; <c>revision</c> is the timeline revision the editor applied it at.</summary>
public sealed record ObjectRequestResolveBody(string? Action, int? Revision);
