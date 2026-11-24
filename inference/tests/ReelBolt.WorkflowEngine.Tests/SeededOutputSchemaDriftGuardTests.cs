using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.WorkflowEngine;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Production;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>DatabaseSeeder.GetOutputSchemaJson</c> is a hand-written JSON-schema literal per agent type,
/// while the schema actually ENFORCED at runtime comes from the CLR type
/// (<c>ReelBoltAgentBase</c> → <c>ChatResponseFormat.ForJsonSchema&lt;T&gt;()</c>). Nothing tied
/// the two together, so a property added to an output schema type simply never appeared in the
/// documentation every agent is shown.
///
/// <para>
/// That is not hypothetical. Phase 5 added <c>MotionGraphicsPlanOutput.Inserts</c>;
/// <c>GenerateMotionGraphicsPlanSchema</c> was not updated. The seeded schema described a plan with
/// exactly <c>overlays</c> and <c>planRationale</c>, so the graphics room's director rendered both
/// tracked-insert assets, described them in its <c>planRationale</c> prose, and emitted an empty
/// <c>inserts</c> list — leaving two raw green rectangles in a finished film with every step
/// reporting success.
/// </para>
///
/// <para>
/// These tests compare the seeded literal's TOP-LEVEL property names against the CLR type's, for
/// every agent type that declares a schema. Names only: the hand-written descriptions are the
/// point of these literals and are deliberately richer than anything reflection could produce.
/// </para>
///
/// <para>
/// The second half of the same contract is that the agent the literal documents is reachable at
/// all, so one test here also resolves every schema-backed agent type through the real service
/// registrations — a seeded schema for an agent nothing registers documents a type that can never
/// run.
/// </para>
/// </summary>
public class SeededOutputSchemaDriftGuardTests
{
    /// <summary>
    /// The agent types whose seeded schema documents a known CLR output type. Deliberately not
    /// every seeded schema: several analysis agents' literals describe a shape with no CLR type in
    /// <c>OutputSchemas.cs</c> at all, which this guard has nothing to compare against.
    /// </summary>
    public static TheoryData<AgentType, Type> SchemaBackedAgentTypes => new()
    {
        { AgentType.VideoStoryEditor, typeof(VideoEditDecisionOutput) },
        { AgentType.VideoEditDirector, typeof(VideoEditDecisionOutput) },
        { AgentType.MotionGraphicsPlanner, typeof(MotionGraphicsPlanOutput) },
        { AgentType.MotionGraphicsDirector, typeof(MotionGraphicsPlanOutput) },
        { AgentType.VideoReviewAgent, typeof(VideoReviewOutput) },
        { AgentType.MusicSupervisor, typeof(MusicPlanOutput) },
        { AgentType.SoundDesigner, typeof(SfxPlanOutput) },
        { AgentType.ShotDirector, typeof(GeneratedShotPlanOutput) },
        { AgentType.NarrationWriter, typeof(NarrationPlanOutput) },
        { AgentType.PickupPlanner, typeof(PickupPlanOutput) },
        { AgentType.NarrationTranslator, typeof(NarrationPlanOutput) },
        { AgentType.Colorist, typeof(ColorGradePlanOutput) },
        { AgentType.ColorGradeDirector, typeof(ColorGradePlanOutput) },
        { AgentType.CompositionReviser, typeof(CompositionRevisionOutput) },
        { AgentType.GraphicDesigner, typeof(GraphicDesignOutput) },
        { AgentType.TakeSelector, typeof(TakeSelectionOutput) },
        { AgentType.DailiesDirector, typeof(TakeSelectionOutput) },
        { AgentType.OverlayPlanner, typeof(MediaOverlayPlanOutput) },
        { AgentType.MotionDirector, typeof(MotionPlanOutput) },
        { AgentType.DynamicsPlanner, typeof(DynamicsPlanOutput) }
    };

    [Theory]
    [MemberData(nameof(SchemaBackedAgentTypes))]
    public void The_seeded_schema_describes_exactly_the_properties_its_CLR_output_type_has(
        AgentType agentType, Type outputSchemaType)
    {
        string json = SeededSchemaJson(agentType);
        using JsonDocument doc = JsonDocument.Parse(json);

        List<string> seeded = doc.RootElement
            .GetProperty("properties")
            .EnumerateObject()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        List<string> clr = outputSchemaType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        seeded.Should().BeEquivalentTo(clr,
            $"the seeded schema for {agentType} is the documentation every agent of that type is " +
            $"shown, and it must describe the same properties {outputSchemaType.Name} actually " +
            "accepts — a property present in the CLR type but missing here is a field the model " +
            "is told does not exist, and will not emit");
    }

    [Fact]
    public void Every_schema_backed_agent_type_resolves_through_the_real_service_registrations()
    {
        // The seeded literal is the documentation every agent of that type is shown — which is
        // shown to nobody if nothing registers such an agent. AgentStepExecutor resolves an agent
        // through IAgentRegistry, so a schema-backed type that is seeded and fully green in every
        // other test, yet never registered, resolves to null and fails at runtime with the whole
        // suite passing. Resolved against the REAL registrations Program.cs uses
        // (AddWorkflowEngineServices, the same seam WorkflowEngineCompositionRootTests builds), so
        // this is a guard on the registration itself rather than a restatement of it.
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "workflow-engine",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));
        using IServiceScope scope = root.Services.CreateScope();
        IAgentRegistry registry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();

        foreach (object[] row in SchemaBackedAgentTypes)
        {
            var agentType = (AgentType)row[0];
            var outputSchemaType = (Type)row[1];

            registry.GetByType(agentType).Should().NotBeNull(
                $"{agentType} is seeded with a prompt and a schema documenting " +
                $"{outputSchemaType.Name}, so an agent of that type must also be registered — " +
                "without one, every step of that type resolves to null and cannot run at all");
        }

        // Spelled out for the type this guard was added for, because "an agent" is not enough:
        // the resolution path AgentStepExecutor uses must hand the step the class that declares
        // NarrationPlanOutput, not merely something non-null.
        registry.GetByType(AgentType.NarrationWriter).Should().BeOfType<NarrationWriterAgent>();
    }

    [Fact]
    public void The_motion_graphics_plan_schema_documents_the_tracked_insert_entry_shape()
    {
        // The specific regression: not just that "inserts" exists, but that its item shape names
        // the three strings ScreenInsert actually carries, since renderedAssetStorageKey being
        // REQUIRED (no plain-text fallback) is the part an agent most needs told.
        using JsonDocument doc = JsonDocument.Parse(SeededSchemaJson(AgentType.MotionGraphicsPlanner));

        JsonElement inserts = doc.RootElement.GetProperty("properties").GetProperty("inserts");
        inserts.GetProperty("type").GetString().Should().Be("array");

        List<string> itemProps = inserts.GetProperty("items").GetProperty("properties")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        List<string> clrProps = typeof(ScreenInsert)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        itemProps.Should().BeEquivalentTo(clrProps);
    }

    /// <summary>
    /// <see cref="AgentType.OverlayPlanner"/> (design <c>docs/design/picture-overlay.md</c> §5.2
    /// and §7) is seeded with a hand-written schema like every other agent, so its top-level names
    /// are covered by the <see cref="SchemaBackedAgentTypes"/> theory above. This Fact pins the
    /// entry shape too: every word the model must fill is named, and the one documented numeric
    /// field is a bounded 0..100 integer.
    /// </summary>
    [Fact]
    public void The_media_overlay_plan_schema_documents_the_choice_entry_shape()
    {
        using JsonDocument doc = JsonDocument.Parse(SeededSchemaJson(AgentType.OverlayPlanner));
        JsonElement properties = doc.RootElement.GetProperty("properties");

        JsonElement overlays = properties.GetProperty("overlays");
        overlays.GetProperty("type").GetString().Should().Be("array");
        List<string> itemProps = overlays.GetProperty("items").GetProperty("properties")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        List<string> choiceProps = typeof(MediaOverlayChoice)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        itemProps.Should().BeEquivalentTo(choiceProps);

        JsonElement opacity = overlays.GetProperty("items").GetProperty("properties")
            .GetProperty("opacityPercent");
        opacity.GetProperty("type").GetString().Should().Be("integer");
        opacity.GetProperty("minimum").GetInt32().Should().Be(0);
        opacity.GetProperty("maximum").GetInt32().Should().Be(100);
    }

    private static string SeededSchemaJson(AgentType agentType)
    {
        MethodInfo? method = typeof(DatabaseSeeder)
            .GetMethod("GetOutputSchemaJson", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("DatabaseSeeder must expose a private GetOutputSchemaJson dispatch");

        var json = (string?)method!.Invoke(null, [agentType]);
        json.Should().NotBeNullOrWhiteSpace($"{agentType} must seed an output schema");
        return json!;
    }
}
