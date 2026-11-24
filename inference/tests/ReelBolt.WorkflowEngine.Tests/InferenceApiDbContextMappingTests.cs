using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using System.Linq;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression coverage for the WS1 InferenceProvider.Capability addition (plan §1.4 / R4).
/// Mirrors WorkflowEngineDbContextMappingTests' approach: a relational provider (Sqlite, no
/// connection ever opened) is used purely to build the model and read its annotations, since
/// GetColumnType()/index metadata is not available on the InMemory provider's runtime model.
/// </summary>
public class InferenceApiDbContextMappingTests
{
    private static InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        return new InferenceApiDbContext(options);
    }

    [Fact]
    public void ModelCapability_Capability_is_mapped_with_string_conversion_and_defaults_to_Chat()
    {
        using InferenceApiDbContext db = CreateContext();

        var entityType = db.Model.FindEntityType(typeof(ModelCapability));
        var property = entityType?.FindProperty(nameof(ModelCapability.Capability));

        property.Should().NotBeNull();
        property!.GetProviderClrType().Should().Be(typeof(string));
        property.GetDefaultValue().Should().Be(InferenceProviderCapability.Chat);
    }

    [Fact]
    public void AgentDefinition_AssignedSkillsJson_is_mapped_as_jsonb()
    {
        using InferenceApiDbContext db = CreateContext();

        var entityType = db.Model.FindEntityType(typeof(AgentDefinition));
        var property = entityType?.FindProperty(nameof(AgentDefinition.AssignedSkillsJson));

        property.Should().NotBeNull();
        property!.GetColumnType().Should().Be("jsonb");
    }

    [Theory]
    [InlineData(nameof(WorkflowStep.EditRoomConfigJson))]
    [InlineData(nameof(WorkflowStep.GraphicsRoomConfigJson))]
    [InlineData(nameof(WorkflowStep.ColorGradeRoomConfigJson))]
    [InlineData(nameof(WorkflowStep.DailiesRoomConfigJson))]
    public void WorkflowStep_room_config_columns_are_mapped_as_jsonb(string propertyName)
    {
        // The exact mistake that broke a live deploy once: a new WorkflowStep config-json column
        // mapped as jsonb on the engine's DbContext but forgotten on this one (or vice versa).
        // This test and its WorkflowEngineDbContextMappingTests twin pin BOTH sides.
        using InferenceApiDbContext db = CreateContext();

        var entityType = db.Model.FindEntityType(typeof(WorkflowStep));
        var property = entityType?.FindProperty(propertyName);

        property.Should().NotBeNull();
        property!.GetColumnType().Should().Be("jsonb");
    }

    [Theory]
    [InlineData(nameof(WorkflowStepResult.ToolCallsJson))]
    [InlineData(nameof(WorkflowStepResult.ReasoningJson))]
    [InlineData(nameof(WorkflowStepResult.ChatTranscriptJson))]
    public void WorkflowStepResult_diagnostics_columns_are_mapped_as_jsonb(string propertyName)
    {
        using InferenceApiDbContext db = CreateContext();

        var entityType = db.Model.FindEntityType(typeof(WorkflowStepResult));
        var property = entityType?.FindProperty(propertyName);

        property.Should().NotBeNull();
        property!.GetColumnType().Should().Be("jsonb");
    }

    [Fact]
    public void WorkflowStep_CacheMode_is_mapped_with_string_conversion()
    {
        // Regression test: found live. WorkflowEngineDbContext had HasConversion<string>() on
        // CacheMode (the column is text: "Default"/"Always"/"Never"); this context didn't, so EF
        // fell back to its default int mapping for the enum and every query through this
        // context's WorkflowsController.List (the endpoint the workflow list page actually calls)
        // threw "operator does not exist: text = integer" for every project — every workflow
        // silently vanished from the UI, though the rows were always there in the database. Same
        // mistake as the jsonb columns above, same fix shape: pin both sides.
        using InferenceApiDbContext db = CreateContext();

        var entityType = db.Model.FindEntityType(typeof(WorkflowStep));
        var property = entityType?.FindProperty(nameof(WorkflowStep.CacheMode));

        property.Should().NotBeNull();
        property!.GetProviderClrType().Should().Be(typeof(string));
    }

    [Fact]
    public void ModelCapability_has_a_unique_default_index_per_organization_and_capability_not_is_default_alone()
    {
        using InferenceApiDbContext db = CreateContext();

        // The one-default rule moved with the columns (docs/design/model-catalog.md §2): the provider
        // account no longer has a capability or a default flag at all.
        db.Model.FindEntityType(typeof(InferenceProvider))!.GetIndexes()
            .Should().NotContain(i => i.GetFilter() == "is_default");

        var entityType = db.Model.FindEntityType(typeof(ModelCapability));
        entityType.Should().NotBeNull();

        var indexes = entityType!.GetIndexes().ToList();

        // R4: the old UNIQUE(is_default) index must be gone entirely, not merely supplemented -
        // otherwise a Transcription default and a Chat default could never coexist.
        indexes.Should().NotContain(
            i => i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == nameof(ModelCapability.IsDefault));

        indexes.Should().ContainSingle(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(ModelCapability.OrganizationId),
                nameof(ModelCapability.Capability)
            }) &&
            i.GetFilter() == "is_default" &&
            i.GetAreNullsDistinct() == false);
    }
}
