using System.Text.Json.Nodes;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// Describes the schema for a workflow step type.
/// </summary>
public record WorkflowStepSchemaDto(
    string StepTypeName,
    bool HasConfig,
    string? ConfigTypeName,
    JsonNode? Schema);
