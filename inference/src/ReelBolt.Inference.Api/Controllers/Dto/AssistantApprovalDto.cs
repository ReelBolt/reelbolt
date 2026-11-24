namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// The user's click on a permission card (<c>POST /api/v1/assistant/approvals/{id}</c>).
/// </summary>
/// <param name="Decision"><c>allow</c> or <c>deny</c>.</param>
/// <param name="AlwaysAllow">With <c>allow</c>: also switch to auto mode, so later actions are not asked about.</param>
public sealed record AssistantApprovalAnswerRequest(string? Decision, bool AlwaysAllow = false);

/// <summary>
/// The caller's assistant preferences. <see cref="ApprovalMode"/> is <c>ask</c> (a permission card before every
/// change — the default) or <c>auto</c> (no cards).
/// </summary>
public sealed record AssistantPreferencesDto(string? ApprovalMode, double? AutoApproveCostLimitCredits = null);
