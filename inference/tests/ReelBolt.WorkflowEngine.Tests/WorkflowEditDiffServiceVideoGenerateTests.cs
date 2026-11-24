using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The assistant's propose/apply path (<see cref="WorkflowEditDiffService"/>) must carry a
/// VideoGenerate step's config: a proposal that changes only <c>VideoGenerateConfigJson</c> is a
/// real modification, and applying it (BuildStep) must land the new config on the entity.
/// </summary>
public class WorkflowEditDiffServiceVideoGenerateTests
{
    private const string OldConfig = """{"inlinePrompt":"a fox","durationSeconds":6,"maxSpendCredits":1}""";
    private const string NewConfig = """{"inlinePrompt":"a fox","durationSeconds":10,"maxSpendCredits":1}""";

    private static InferenceApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static WorkflowStep CurrentStep(Guid workflowId, Guid agentId, string config) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = workflowId,
        AgentDefinitionId = agentId,
        StepOrder = 1,
        Label = "Generate",
        StepType = StepType.VideoGenerate,
        VideoGenerateConfigJson = config
    };

    [Fact]
    public void ComputeDiff_reports_a_change_to_only_VideoGenerateConfigJson_as_a_modification()
    {
        using InferenceApiDbContext db = CreateContext();
        WorkflowEditDiffService service = new(db);
        Guid workflowId = Guid.NewGuid();
        Guid agentId = Guid.NewGuid();

        var current = new List<WorkflowStep> { CurrentStep(workflowId, agentId, OldConfig) };
        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Generate", nameof(StepType.VideoGenerate), VideoGenerateConfigJson: NewConfig)
        };

        WorkflowEditDiff diff = service.ComputeDiff(workflowId, current, proposed);

        diff.Modifications.Should().ContainSingle().Which.StepOrder.Should().Be(1);
        diff.Additions.Should().BeEmpty();
        diff.Removals.Should().BeEmpty();
    }

    [Fact]
    public void ComputeDiff_treats_semantically_identical_VideoGenerateConfigJson_as_unchanged()
    {
        using InferenceApiDbContext db = CreateContext();
        WorkflowEditDiffService service = new(db);
        Guid workflowId = Guid.NewGuid();
        Guid agentId = Guid.NewGuid();

        // Postgres jsonb normalizes whitespace, so the stored form may differ textually.
        var current = new List<WorkflowStep> { CurrentStep(workflowId, agentId, OldConfig) };
        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Generate", nameof(StepType.VideoGenerate),
                VideoGenerateConfigJson: """{ "inlinePrompt": "a fox", "durationSeconds": 6, "maxSpendCredits": 1 }""")
        };

        WorkflowEditDiff diff = service.ComputeDiff(workflowId, current, proposed);

        diff.Modifications.Should().BeEmpty();
    }

    [Fact]
    public void BuildStep_applies_VideoGenerateConfigJson()
    {
        using InferenceApiDbContext db = CreateContext();
        WorkflowEditDiffService service = new(db);
        Guid workflowId = Guid.NewGuid();

        CreateWorkflowStepRequest req = new(Guid.NewGuid(), 1, null, "Generate", nameof(StepType.VideoGenerate),
            VideoGenerateConfigJson: NewConfig);

        WorkflowStep step = service.BuildStep(workflowId, req);

        step.StepType.Should().Be(StepType.VideoGenerate);
        JsonNode.DeepEquals(JsonNode.Parse(step.VideoGenerateConfigJson), JsonNode.Parse(NewConfig)).Should().BeTrue();
    }
}
