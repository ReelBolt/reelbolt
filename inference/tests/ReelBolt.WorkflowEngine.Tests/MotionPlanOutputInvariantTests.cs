using System;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The rushcut invariant for the motion director (docs/design/video-events-analysis.md section 5): its output is
/// structurally incapable of expressing a time, a zoom factor, a speed multiplier or a coordinate. Every member is a string
/// (an offered event id or an enum word, or prose), so a numeric property added later fails here.
/// </summary>
public class MotionPlanOutputInvariantTests
{
    [Fact]
    public void Every_cue_property_is_a_string()
    {
        PropertyInfo[] nonString = typeof(MotionCue).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(string)).ToArray();

        nonString.Should().BeEmpty("a MotionCue is ids and words only; found: " + string.Join(", ", nonString.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }

    [Fact]
    public void The_plan_has_only_cues_and_a_rationale()
    {
        typeof(MotionPlanOutput).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
            .Should().BeEquivalentTo(["Cues", "PlanRationale"]);
        typeof(MotionPlanOutput).GetProperty(nameof(MotionPlanOutput.Cues))!.PropertyType.Should().Be(typeof(System.Collections.Generic.List<MotionCue>));
    }

    [Fact]
    public void Cue_shape_is_pinned_so_it_never_silently_gains_a_property()
    {
        typeof(MotionCue).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
            .Should().BeEquivalentTo(["EventId", "Action", "Intensity", "Length", "Colour", "Kind", "Text", "Reason"]);
    }
}
