using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Read-only endpoint for workflow step configuration schemas.
/// Provides global metadata about available step types and their configuration requirements.
/// </summary>
[ApiController]
[Route("api/v1/workflow-step-schemas")]
[Authorize]
public class WorkflowStepSchemasController : ControllerBase
{
    private readonly StepConfigSchemaExporter _exporter;

    public WorkflowStepSchemasController(StepConfigSchemaExporter exporter)
    {
        _exporter = exporter;
    }

    /// <summary>
    /// Lists schemas for all workflow step types.
    /// Returns one entry per <see cref="StepType"/> enum value.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<WorkflowStepSchemaDto>>> List(CancellationToken ct)
    {
        var schemas = _exporter.GetAllSchemas();

        var result = Enum.GetValues<StepType>()
            .Select(stepType =>
            {
                string stepTypeName = stepType.ToString();
                var schemaResult = schemas[stepTypeName];

                return new WorkflowStepSchemaDto(
                    stepTypeName,
                    schemaResult.HasConfig,
                    schemaResult.ConfigTypeName,
                    schemaResult.Schema);
            })
            .ToList();

        return Ok(result);
    }

    /// <summary>
    /// Gets the schema for a specific workflow step type.
    /// Returns 404 if the step type name is not recognized.
    /// </summary>
    [HttpGet("{stepType}")]
    public async Task<ActionResult<WorkflowStepSchemaDto>> Get(string stepType, CancellationToken ct)
    {
        // Try to parse the step type name
        if (!Enum.TryParse<StepType>(stepType, ignoreCase: false, out var parsedStepType))
        {
            return NotFound();
        }

        var schemaResult = _exporter.GetSchema(parsedStepType);

        var dto = new WorkflowStepSchemaDto(
            stepType,
            schemaResult.HasConfig,
            schemaResult.ConfigTypeName,
            schemaResult.Schema);

        return Ok(dto);
    }
}
