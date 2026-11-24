using System;
using System.Collections.Generic;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.StepOptions;

/// <summary>
/// The two step options survive the assistant's read-modify-write loop: GetWorkflow projects a stored
/// step through <see cref="AssistantStepShapes.FromStep"/>, the model proposes the list back as
/// <see cref="CreateWorkflowStepRequest"/>s, and the diff service builds the step it will store.
/// </summary>
public class ContinueCompositionRoundTripTests
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static WorkflowStep Stored(string? continueJson, bool register) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = Guid.NewGuid(),
        AgentDefinitionId = Guid.NewGuid(),
        StepOrder = 1,
        Label = "Author",
        StepType = StepType.Agent,
        ContinueCompositionJson = continueJson,
        RegisterOutputAsProjectFile = register
    };

    private static InferenceApiDbContext Db() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public void GetWorkflow_shape_names_both_fields_in_camel_case()
    {
        Guid source = Guid.NewGuid();
        string json = JsonSerializer.Serialize(AssistantStepShapes.FromStep(Stored($$"""{"executionId":"{{source}}"}""", true)), CamelCase);

        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("continueCompositionJson").GetString().Should().Contain(source.ToString());
        doc.RootElement.GetProperty("registerOutputAsProjectFile").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Proposing_the_read_shape_back_builds_a_step_with_both_options()
    {
        using InferenceApiDbContext db = Db();
        Guid source = Guid.NewGuid();
        WorkflowStep stored = Stored($$"""{"versionId":"{{source}}"}""", true);
        string read = JsonSerializer.Serialize(AssistantStepShapes.FromStep(stored), CamelCase);

        // ProposeWorkflow binds its steps with the web defaults (case-insensitive camelCase).
        CreateWorkflowStepRequest proposed = JsonSerializer.Deserialize<CreateWorkflowStepRequest>(read, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        WorkflowStep built = new WorkflowEditDiffService(db).BuildStep(stored.WorkflowDefinitionId, proposed);

        built.ContinueCompositionJson.Should().Be(stored.ContinueCompositionJson);
        built.RegisterOutputAsProjectFile.Should().BeTrue();
    }

    [Fact]
    public void An_unchanged_round_trip_is_not_a_modification()
    {
        using InferenceApiDbContext db = Db();
        WorkflowStep stored = Stored("""{"executionId":"6f0e6a43-25a8-4d1c-9a58-3a1d1c3c2f11"}""", true);

        WorkflowEditDiff diff = new WorkflowEditDiffService(db).ComputeDiff(
            stored.WorkflowDefinitionId, [stored], [AssistantStepShapes.FromStep(stored)]);

        diff.ChangesNothing.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, """{"executionId":"6f0e6a43-25a8-4d1c-9a58-3a1d1c3c2f11"}""")]
    public void Changing_only_a_step_option_is_a_modification(bool register, string? continueJson)
    {
        using InferenceApiDbContext db = Db();
        WorkflowStep stored = Stored(null, false);
        CreateWorkflowStepRequest proposed = AssistantStepShapes.FromStep(stored) with
        {
            RegisterOutputAsProjectFile = register,
            ContinueCompositionJson = continueJson
        };

        WorkflowEditDiff diff = new WorkflowEditDiffService(db).ComputeDiff(stored.WorkflowDefinitionId, [stored], [proposed]);

        diff.Modifications.Should().ContainSingle();
    }

    [Fact]
    public void A_request_without_the_options_serializes_exactly_as_before_so_pending_tokens_still_verify()
    {
        CreateWorkflowStepRequest plain = new(Guid.NewGuid(), 1, null, "Author", StepType: "Agent");

        string json = JsonSerializer.Serialize(plain, CamelCase);

        json.Should().NotContain("continueCompositionJson").And.NotContain("registerOutputAsProjectFile");
    }
}
