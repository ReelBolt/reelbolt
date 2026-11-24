using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

public class WorkflowEditDiffServiceTests
{
    private static InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [Fact]
    public void ComputeDiff_WithAddedStepsOnly_ReturnsAllAdditions()
    {
        // Arrange
        using var db = CreateContext();
        var service = new WorkflowEditDiffService(db);

        var workflowId = Guid.NewGuid();
        var current = new List<WorkflowStep>();
        var agentId = Guid.NewGuid();

        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Step 1", "Agent"),
            new(agentId, 2, null, "Step 2", "Agent")
        };

        // Act
        var diff = service.ComputeDiff(workflowId, current, proposed);

        // Assert
        Assert.NotEmpty(diff.Additions);
        Assert.Empty(diff.Removals);
        Assert.Empty(diff.Modifications);
        Assert.Equal(2, diff.Additions.Count);
        Assert.NotEmpty(diff.ConfirmationToken);
    }

    [Fact]
    public void ComputeDiff_WithRemovedStepsOnly_ReturnsAllRemovals()
    {
        // Arrange
        using var db = CreateContext();
        var service = new WorkflowEditDiffService(db);

        var workflowId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var current = new List<WorkflowStep>
        {
            new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                AgentDefinitionId = agentId,
                StepOrder = 1,
                Label = "Step 1",
                StepType = StepType.Agent
            },
            new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                AgentDefinitionId = agentId,
                StepOrder = 2,
                Label = "Step 2",
                StepType = StepType.Agent
            }
        };

        var proposed = new List<CreateWorkflowStepRequest>();

        // Act
        var diff = service.ComputeDiff(workflowId, current, proposed);

        // Assert
        Assert.Empty(diff.Additions);
        Assert.NotEmpty(diff.Removals);
        Assert.Empty(diff.Modifications);
        Assert.Equal(2, diff.Removals.Count);
    }

    [Fact]
    public void ComputeDiff_WithIdenticalSteps_ReturnsEmptyDiffAndSameToken()
    {
        // Arrange
        using var db = CreateContext();
        var service = new WorkflowEditDiffService(db);

        var workflowId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var current = new List<WorkflowStep>
        {
            new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                AgentDefinitionId = agentId,
                StepOrder = 1,
                Label = "Step 1",
                StepType = StepType.Agent
            }
        };

        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Step 1", "Agent")
        };

        // Act
        var diff1 = service.ComputeDiff(workflowId, current, proposed);
        var diff2 = service.ComputeDiff(workflowId, current, proposed);

        // Assert
        Assert.Empty(diff1.Additions);
        Assert.Empty(diff1.Removals);
        Assert.Empty(diff1.Modifications);
        Assert.Equal(diff1.ConfirmationToken, diff2.ConfirmationToken);
    }

    [Fact]
    public void ComputeDiff_WithDifferentCurrent_ReturnsDifferentToken()
    {
        // Arrange
        using var db = CreateContext();
        var service = new WorkflowEditDiffService(db);

        var workflowId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var current1 = new List<WorkflowStep>
        {
            new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                AgentDefinitionId = agentId,
                StepOrder = 1,
                Label = "Step 1",
                StepType = StepType.Agent
            }
        };

        var current2 = new List<WorkflowStep>
        {
            new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                AgentDefinitionId = agentId,
                StepOrder = 1,
                Label = "Step 1 Modified",
                StepType = StepType.Agent
            }
        };

        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Step 1 New", "Agent")
        };

        // Act
        var diff1 = service.ComputeDiff(workflowId, current1, proposed);
        var diff2 = service.ComputeDiff(workflowId, current2, proposed);

        // Assert
        Assert.NotEqual(diff1.ConfirmationToken, diff2.ConfirmationToken);
    }

    [Fact]
    public void ComputeDiff_WithNullWorkflowIdAndEmptyCurrent_ReturnsAllAdditions()
    {
        // Arrange
        using var db = CreateContext();
        var service = new WorkflowEditDiffService(db);

        var current = new List<WorkflowStep>();
        var agentId = Guid.NewGuid();

        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Step 1", "Agent"),
            new(agentId, 2, null, "Step 2", "Agent")
        };

        // Act
        var diff = service.ComputeDiff(null, current, proposed);

        // Assert
        Assert.NotEmpty(diff.Additions);
        Assert.Empty(diff.Removals);
        Assert.Empty(diff.Modifications);
        Assert.Equal(2, diff.Additions.Count);
    }

    [Fact]
    public void ComputeDiff_ReturnsProposedStepsUnmodified()
    {
        // Arrange
        using var db = CreateContext();
        var service = new WorkflowEditDiffService(db);

        var workflowId = Guid.NewGuid();
        var current = new List<WorkflowStep>();
        var agentId = Guid.NewGuid();

        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Step 1", "Agent"),
            new(agentId, 2, null, "Step 2", "Agent")
        };

        // Act
        var diff = service.ComputeDiff(workflowId, current, proposed);

        // Assert
        Assert.NotNull(diff.ProposedSteps);
        Assert.Equal(proposed.Count, diff.ProposedSteps.Count);
        Assert.Equal(proposed, diff.ProposedSteps);
    }

    [Fact]
    public void ComputeDiff_WithEmptyProposed_ReturnsEmptyProposedSteps()
    {
        // Arrange
        using var db = CreateContext();
        var service = new WorkflowEditDiffService(db);

        var workflowId = Guid.NewGuid();
        var current = new List<WorkflowStep>();
        var proposed = new List<CreateWorkflowStepRequest>();

        // Act
        var diff = service.ComputeDiff(workflowId, current, proposed);

        // Assert
        Assert.NotNull(diff.ProposedSteps);
        Assert.Empty(diff.ProposedSteps);
    }
}

public class MinimalJsonSchemaValidatorTests
{
    [Fact]
    public void Validate_WithEmptySchema_ReturnsValid()
    {
        // Arrange
        var schema = JsonNode.Parse("{\"type\": \"object\", \"properties\": {}}")!;
        var configJson = "{}";
        var validator = new MinimalJsonSchemaValidator();

        // Act
        var result = validator.Validate(schema, configJson);

        // Assert
        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WithMatchingRequiredProperties_ReturnsValid()
    {
        // Arrange
        var schema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {"type": "string"},
                    "age": {"type": "integer"}
                },
                "required": ["name", "age"]
            }
            """)!;
        var configJson = "{\"name\": \"John\", \"age\": 30}";
        var validator = new MinimalJsonSchemaValidator();

        // Act
        var result = validator.Validate(schema, configJson);

        // Assert
        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WithMissingRequiredProperty_ReturnsInvalid()
    {
        // Arrange
        var schema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {"type": "string"},
                    "age": {"type": "integer"}
                },
                "required": ["name", "age"]
            }
            """)!;
        var configJson = "{\"name\": \"John\"}";
        var validator = new MinimalJsonSchemaValidator();

        // Act
        var result = validator.Validate(schema, configJson);

        // Assert
        Assert.False(result.Valid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("age", result.Errors.First());
    }

    [Fact]
    public void Validate_WithIncorrectPropertyType_ReturnsInvalid()
    {
        // Arrange
        var schema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {"type": "string"},
                    "age": {"type": "integer"}
                },
                "required": ["name", "age"]
            }
            """)!;
        var configJson = "{\"name\": \"John\", \"age\": \"thirty\"}";
        var validator = new MinimalJsonSchemaValidator();

        // Act
        var result = validator.Validate(schema, configJson);

        // Assert
        Assert.False(result.Valid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("age", result.Errors.First());
    }

    [Fact]
    public void Validate_WithInvalidJson_ReturnsInvalid()
    {
        // Arrange
        var schema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {"type": "string"}
                },
                "required": ["name"]
            }
            """)!;
        var configJson = "{ invalid json }";
        var validator = new MinimalJsonSchemaValidator();

        // Act
        var result = validator.Validate(schema, configJson);

        // Assert
        Assert.False(result.Valid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Validate_WithBooleanProperty_ValidatesType()
    {
        // Arrange
        var schema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "enabled": {"type": "boolean"}
                },
                "required": ["enabled"]
            }
            """)!;
        var configJson = "{\"enabled\": true}";
        var validator = new MinimalJsonSchemaValidator();

        // Act
        var result = validator.Validate(schema, configJson);

        // Assert
        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WithNumberProperty_ValidatesType()
    {
        // Arrange
        var schema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "temperature": {"type": "number"}
                },
                "required": ["temperature"]
            }
            """)!;
        var configJson = "{\"temperature\": 0.7}";
        var validator = new MinimalJsonSchemaValidator();

        // Act
        var result = validator.Validate(schema, configJson);

        // Assert
        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WithOptionalProperty_AllowsMissing()
    {
        // Arrange
        var schema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {"type": "string"},
                    "age": {"type": "integer"}
                },
                "required": ["name"]
            }
            """)!;
        var configJson = "{\"name\": \"John\"}";
        var validator = new MinimalJsonSchemaValidator();

        // Act
        var result = validator.Validate(schema, configJson);

        // Assert
        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WithNoPropertiesNode_ReturnsValid()
    {
        // Arrange
        var schema = JsonNode.Parse("""
            {
                "type": "object"
            }
            """)!;
        var configJson = "{}";
        var validator = new MinimalJsonSchemaValidator();

        // Act
        var result = validator.Validate(schema, configJson);

        // Assert
        Assert.True(result.Valid);
    }
}
