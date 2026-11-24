using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Views;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using Xunit;
using StepTypeEnum = ReelBolt.Shared.Data.Models.StepType;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// P4-02 coverage for the ColorGradeRoom cache-key component: the room follows its
/// <see cref="AgentType.ColorGradeDirector"/> role, the component hashes the rendered
/// <see cref="DesignView.GradeHint"/> block, and a product-only run is constant.
/// </summary>
public class GradeHintCacheKeyTests
{
    private static readonly TokenProvenance Prov = new("manual", null, null, null);

    [Fact]
    public void Color_grade_room_component_is_the_rendered_grade_hint_hash()
    {
        JsonObject design = Doc("#ff0000");
        DesignSystemRunState state = State(design);

        string? component = state.CacheKeyComponent(StepTypeEnum.ColorGradeRoom, null);

        string block = DesignSystemPromptView.RenderDesign(design, DesignView.GradeHint, 1, 2400)!;
        component.Should().Be($"d={CanonicalJson.Sha256Hex(block)[..16]}|p=-");
    }

    [Fact]
    public void Changing_the_temperature_class_changes_the_component()
    {
        string? warm = State(Doc("#ff0000")).CacheKeyComponent(StepTypeEnum.ColorGradeRoom, null);
        string? cool = State(Doc("#0000ff")).CacheKeyComponent(StepTypeEnum.ColorGradeRoom, null);

        warm.Should().NotBeNull();
        cool.Should().NotBe(warm);
    }

    [Fact]
    public void An_unrelated_token_does_not_change_the_component()
    {
        string? baseline = State(Doc("#ff0000")).CacheKeyComponent(StepTypeEnum.ColorGradeRoom, null);

        JsonObject withSpacing = Doc("#ff0000");
        DesignDocument.SetToken(
            withSpacing, "spacing.md", "dimension", new JsonObject { ["value"] = 8, ["unit"] = "px" }, Prov, 1.0);

        JsonObject withExtraColor = Doc("#ff0000");
        DesignDocument.SetToken(
            withExtraColor, "color.extra.cta", "color", new JsonObject { ["hex"] = "#123456" }, Prov, 1.0);

        State(withSpacing).CacheKeyComponent(StepTypeEnum.ColorGradeRoom, null).Should().Be(baseline);
        State(withExtraColor).CacheKeyComponent(StepTypeEnum.ColorGradeRoom, null).Should().Be(baseline);
    }

    [Fact]
    public void A_product_only_run_gets_a_constant_non_null_component()
    {
        DesignSystemRunState state = State(null);

        state.CacheKeyComponent(StepTypeEnum.ColorGradeRoom, null).Should().Be("d=-|p=-");
    }

    [Fact]
    public void Colour_grading_roles_have_no_blocks_without_a_design()
    {
        DesignSystemRunState state = State(null);

        foreach (AgentType agentType in new[] { AgentType.Colorist, AgentType.ColorGradeDirector })
        {
            (string? design, string? product) = state.BlocksFor(agentType);
            design.Should().BeNull(agentType.ToString());
            product.Should().BeNull(agentType.ToString());
        }
    }

    [Fact]
    public void Decorate_returns_the_same_instance_without_a_design()
    {
        const string prompt = "grade it";
        DesignSystemRunState state = State(null);

        using (DesignSystemAmbient.Enter(state))
        {
            DesignSystemAmbient.Decorate(prompt, AgentType.Colorist).Should().BeSameAs(prompt);
        }
    }

    private static DesignSystemRunState State(JsonObject? design) =>
        new(new LoadedProfile(design, 1, null, 1), new DesignSystemAmbientOptions());

    private static JsonObject Doc(string primary)
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = primary }, Prov, 1.0);
        return doc;
    }
}
