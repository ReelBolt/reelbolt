using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class PortableRecipeValidatorTests
{
    [Fact]
    public void Only_the_structural_rules_run_never_a_readiness_rule()
    {
        PortableRecipeValidator.StructuralRules.Should().BeEquivalentTo(
            ["WL001", "WL002", "WL003", "WL004", "WL005", "WL014", "WL018", "WL019", "WL021"]);
        PortableRecipeValidator.StructuralRules.Should().NotContain(["WL006", "WL007", "WL008", "WL009", "WL010", "WL012", "WL015"]);
    }

    [Fact]
    public void A_voiceover_without_a_source_fails_the_structural_rule()
    {
        WorkflowStep step = SnapshotFixtures.Step(1, StepType.Voiceover);
        SnapshotFixtures.SetConfig(step, new JsonObject { ["source"] = new JsonObject { ["kind"] = "ScriptScenes" } });
        PortableRecipe recipe = PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), [step], SnapshotFixtures.BuiltIns(), null).Recipe!;

        PortableRecipeValidator.Validate(recipe).Should().NotBeEmpty();
    }

    [Fact]
    public void Open_slots_do_not_fail_validation()
    {
        WorkflowStep analyze = SnapshotFixtures.Step(1, StepType.VideoAnalyze);
        SnapshotFixtures.SetConfig(analyze, new JsonObject
        {
            ["version"] = 1,
            ["source"] = new JsonObject { ["kind"] = "ProjectFile", ["projectFileId"] = System.Guid.NewGuid().ToString() },
            ["transcriptionProviderId"] = System.Guid.NewGuid().ToString()
        });
        PortableRecipe recipe = PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), [analyze], SnapshotFixtures.BuiltIns(), null).Recipe!;

        recipe.Slots.Should().HaveCount(2);
        PortableRecipeValidator.Validate(recipe).Should().BeEmpty();
    }

    [Fact]
    public void An_insert_content_file_slot_is_validated_with_a_placeholder_never_stored()
    {
        WorkflowStep pin = SnapshotFixtures.Step(1, StepType.PinInsert);
        SnapshotFixtures.SetConfig(pin, new JsonObject
        {
            ["analysis"] = new JsonObject { ["from"] = "Previous" },
            ["pins"] = new JsonArray(new JsonObject { ["content"] = new JsonObject { ["from"] = "ProjectFile", ["projectFileId"] = System.Guid.NewGuid().ToString() } })
        });
        PortableRecipeResult built = PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), [pin], SnapshotFixtures.BuiltIns(), null);

        built.Recipe!.Slots.Should().ContainSingle(s => s.Kind == PortableSlotKinds.File && s.Field == "pins[0].content.projectFileId");
        built.CanonicalJson.Should().NotContain("f11e5");
        PortableRecipeValidator.Validate(built.Recipe).Where(e => e.Contains("projectFileId")).Should().BeEmpty();
    }
}
