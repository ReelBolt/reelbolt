using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The rushcut invariant for the dynamics planner (docs/design/dynamic-edit.md): <c>DynamicsPlanOutput</c> carries the same
/// string-only <c>DynamicsEntryOutput</c> the story editor emits, so the agent can name offered ids and catalogue words and
/// can never express a number or a time. If a numeric or time-bearing property is ever added here, this test must fail.
/// </summary>
public class DynamicsPlanOutputInvariantTests
{
    private static readonly Type[] BannedPropertyTypes =
    [
        typeof(int), typeof(int?), typeof(long), typeof(long?), typeof(float), typeof(float?),
        typeof(double), typeof(double?), typeof(decimal), typeof(decimal?),
        typeof(TimeSpan), typeof(TimeSpan?), typeof(DateTime), typeof(DateTime?), typeof(DateTimeOffset), typeof(DateTimeOffset?)
    ];

    [Theory]
    [InlineData(typeof(DynamicsPlanOutput))]
    [InlineData(typeof(DynamicsEntryOutput))]
    public void No_numeric_or_time_bearing_property_anywhere_in_the_schema(Type type)
    {
        PropertyInfo[] offending = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => BannedPropertyTypes.Contains(p.PropertyType)).ToArray();

        offending.Should().BeEmpty("the planner must not be able to emit a number or a time; found: "
            + string.Join(", ", offending.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }

    [Fact]
    public void Every_entry_property_is_a_string()
    {
        typeof(DynamicsEntryOutput).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(string)).Should().BeEmpty("every entry field is a word or an offered id");
    }

    [Fact]
    public void The_plan_is_the_same_entry_the_story_editor_emits_plus_a_rationale()
    {
        typeof(DynamicsPlanOutput).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
            .Should().BeEquivalentTo(["Dynamics", "PlanRationale"]);
        typeof(DynamicsPlanOutput).GetProperty(nameof(DynamicsPlanOutput.Dynamics))!.PropertyType
            .Should().Be(typeof(VideoEditDecisionOutput).GetProperty(nameof(VideoEditDecisionOutput.Dynamics))!.PropertyType);
        typeof(DynamicsEntryOutput).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
            .Should().BeEquivalentTo(["Effect", "AnchorId", "OtherId", "Moment", "Beats", "Division", "Winner", "Intensity", "Reason"]);
    }
}
