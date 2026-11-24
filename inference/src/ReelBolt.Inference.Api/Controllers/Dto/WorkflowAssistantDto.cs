using ReelBolt.Inference.Api.Services.Workflows;

namespace ReelBolt.Inference.Api.Controllers.Dto;

public record ProposeWorkflowRequest(Guid? WorkflowId, string? Name, IReadOnlyList<CreateWorkflowStepRequest> Steps);

public record ApplyWorkflowRequest(Guid? WorkflowId, string? Name, IReadOnlyList<CreateWorkflowStepRequest> Steps, string ConfirmationToken);

/// <param name="AlreadyApplied">
/// Set (to <c>true</c>) only when nothing was written because the workflow already holds exactly the proposed
/// steps — typically because the assistant applied the same proposal earlier in the same turn.
/// </param>
public record WorkflowAssistantApplyResponse(
    Guid WorkflowId,
    string Name,
    int StepCount,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    bool? AlreadyApplied = null);
