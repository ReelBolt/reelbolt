using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

/// <summary>
/// The marketplace publishability catalog must classify every property of every shareable step config. A new step type, a new
/// config property that is an id or text, or a new config record fails here until someone decides what a public recipe does with it.
/// </summary>
public sealed class MarketplaceCatalogDriftTests
{
    private static readonly StepType[] Refused = [StepType.Revise, StepType.DesignGraphic, StepType.TimelineRender];

    public static TheoryData<StepType> AllStepTypes()
    {
        TheoryData<StepType> data = [];
        foreach (StepType t in Enum.GetValues<StepType>())
            data.Add(t);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllStepTypes))]
    public void Every_step_type_has_an_explicit_verdict(StepType stepType)
    {
        StepPublishability verdict = MarketplaceStepPublishability.For(stepType);

        if (Refused.Contains(stepType))
        {
            verdict.Should().BeOfType<StepPublishability.NotShareable>();
            ((StepPublishability.NotShareable)verdict).Message.Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            verdict.Should().BeOfType<StepPublishability.Shareable>();
        }
    }

    [Fact]
    public void Step_types_covered_are_the_25_known_members()
    {
        Enum.GetValues<StepType>().Should().HaveCount(25, "a new StepType needs a verdict in MarketplaceStepPublishability and a decision about its config");
    }

    [Fact]
    public void Every_property_of_every_shareable_config_is_classified()
    {
        List<string> unclassified = [];
        foreach (Type type in ConfigReflection.Reachable(ShareableConfigTypes()))
        {
            foreach (ConfigProperty p in ConfigReflection.PropertiesOf(type))
            {
                bool classified = p.IsPrimitive || MarketplaceFieldCatalog.Explicit.ContainsKey(p.RuleKey) || MarketplaceFieldCatalog.Descends(p);
                if (!classified)
                    unclassified.Add($"{p.RuleKey} ({p.Info.PropertyType.Name})");
            }
        }

        string.Join(", ", unclassified).Should().BeEmpty("classify each in MarketplaceFieldCatalog as Keep, Text, Slot(kind) or Strip");
    }

    [Fact]
    public void Every_catalog_entry_names_a_real_property()
    {
        HashSet<string> real = [];
        foreach (Type type in ConfigReflection.Reachable(ShareableConfigTypes()))
        {
            foreach (ConfigProperty p in ConfigReflection.PropertiesOf(type))
                real.Add(p.RuleKey);
        }

        MarketplaceFieldCatalog.Explicit.Keys.Where(k => !real.Contains(k)).Should().BeEmpty("a renamed or removed property must not leave a stale rule behind");
    }

    [Fact]
    public void Id_typed_properties_are_never_kept()
    {
        List<string> kept = [];
        foreach (Type type in ConfigReflection.Reachable(ShareableConfigTypes()))
        {
            foreach (ConfigProperty p in ConfigReflection.PropertiesOf(type))
            {
                if (p.ValueType == typeof(Guid) && MarketplaceFieldCatalog.RuleFor(p) is not { Class: FieldClass.Slot or FieldClass.Strip })
                    kept.Add(p.RuleKey);
            }
        }

        kept.Should().BeEmpty("a Guid property is an id: it becomes a slot or is stripped, never kept or treated as text");
    }

    [Fact]
    public void Numeric_properties_named_like_ids_must_be_classified_explicitly()
    {
        Type[] numeric = [typeof(int), typeof(long), typeof(short), typeof(uint), typeof(ulong), typeof(ushort), typeof(byte)];
        List<string> unclassified = [];
        foreach (Type type in ConfigReflection.Reachable(ShareableConfigTypes()))
        {
            foreach (ConfigProperty p in ConfigReflection.PropertiesOf(type))
            {
                bool idNamed = p.Info.Name.EndsWith("Id", StringComparison.Ordinal) || p.Info.Name.EndsWith("Ids", StringComparison.Ordinal);
                if (idNamed && numeric.Contains(p.ValueType) && !MarketplaceFieldCatalog.Explicit.ContainsKey(p.RuleKey))
                    unclassified.Add(p.RuleKey);
            }
        }

        string.Join(", ", unclassified).Should().BeEmpty("a numeric or long property named ...Id/...Ids may be an external id: classify it in MarketplaceFieldCatalog");
    }

    [Fact]
    public void Every_step_config_record_is_either_catalogued_or_refused_on_purpose()
    {
        HashSet<Type> known = [.. ShareableConfigTypes(), .. MarketplaceStepPublishability.RefusedConfigTypes];
        IEnumerable<Type> all = typeof(ConditionalStepConfig).Assembly.GetTypes()
            .Where(t => t.Namespace == "ReelBolt.Shared.Workflows" && t.Name.EndsWith("StepConfig", StringComparison.Ordinal) && !t.IsInterface);

        all.Where(t => !known.Contains(t)).Select(t => t.Name).Should().BeEmpty("a new *StepConfig must be classified or refused in MarketplaceStepPublishability");
    }

    [Fact]
    public void Refused_config_types_belong_to_refused_step_types()
    {
        MarketplaceStepPublishability.RefusedConfigTypes.Should().ContainSingle().Which.Should().Be(typeof(TimelineRenderStepConfig));
        MarketplaceStepPublishability.For(StepType.TimelineRender).Should().BeOfType<StepPublishability.NotShareable>();
    }

    [Fact]
    public void Shareable_plan_lists_the_slots_and_strips_of_a_step()
    {
        StepPublishability.Shareable voiceover = (StepPublishability.Shareable)MarketplaceStepPublishability.For(StepType.Voiceover);
        voiceover.Plan.Should().Contain(e => e.Property == "VoiceoverStepConfig.ProviderId" && e.SlotKind == PortableSlotKinds.Provider);
        voiceover.Plan.Should().Contain(e => e.Property == "VoiceoverLine.VoiceId" && e.SlotKind == PortableSlotKinds.Voice);
    }

    internal static IReadOnlyList<Type> ShareableConfigTypes() =>
        [.. Enum.GetValues<StepType>()
            .Select(MarketplaceStepPublishability.ConfigTypeOf)
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct()];
}
