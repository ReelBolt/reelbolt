namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>One assistant turn the user chose to attach to a report.</summary>
public sealed record FeedbackTurnDto(string? Role, string? Content);

/// <summary>
/// The context a report carries. Every field is optional and is assembled <b>client-side</b> from what
/// the user chose to attach; the server never fills one in from the request or from its own state.
/// </summary>
public sealed record FeedbackContextDto(
    string? Page,
    Guid? ProjectId,
    Guid? ExecutionId,
    IReadOnlyList<FeedbackTurnDto>? AssistantTurns,
    System.Text.Json.JsonElement? Diagnostics = null);

/// <summary>The body of <c>POST /api/v1/feedback</c>.</summary>
public sealed record FeedbackRequest(string? Kind, string? Message, FeedbackContextDto? Context);

/// <summary>What a successful submission returns.</summary>
public sealed record FeedbackCreatedResponse(Guid Id, DateTime CreatedAt);

/// <summary>One stored report as a platform operator reads it.</summary>
public sealed record FeedbackReportDto(
    Guid Id, Guid UserId, string Kind, string Message, string ContextJson, DateTime CreatedAt);
