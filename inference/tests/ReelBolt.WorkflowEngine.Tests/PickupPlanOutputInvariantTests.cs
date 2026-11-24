using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="PickupPlanOutput"/> is a SEPARATE schema from <c>VideoEditDecisionOutput</c> precisely
/// so the rushcut invariant on that output stays untouched — and it must carry the same invariant
/// itself (docs/voiceover.md "Pickups"): no numeric or time-bearing CLR type, no property even
/// NAMED for a time, every property a plain string, and the exact property set pinned. Modelled on
/// <see cref="NarrationPlanOutputInvariantTests"/>.
/// </summary>
public class PickupPlanOutputInvariantTests
{
    private static readonly Type[] BannedPropertyTypes =
    [
        typeof(int), typeof(int?),
        typeof(long), typeof(long?),
        typeof(float), typeof(float?),
        typeof(double), typeof(double?),
        typeof(decimal), typeof(decimal?),
        typeof(TimeSpan), typeof(TimeSpan?),
        typeof(DateTime), typeof(DateTime?),
        typeof(DateTimeOffset), typeof(DateTimeOffset?)
    ];

    private static readonly string[] BannedPropertyNameWords =
    [
        "Sec", "Seconds",
        "Ms", "Millis", "Millisecond",
        "Time", "Timestamp",
        "Duration",
        "Frame", "Frames",
        "Offset",
        "Start", "End"
    ];

    [Theory]
    [InlineData(typeof(PickupPlanOutput))]
    [InlineData(typeof(PickupLine))]
    public void No_numeric_or_time_bearing_property_anywhere_in_the_schema(Type type)
    {
        PropertyInfo[] offending = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => BannedPropertyTypes.Contains(p.PropertyType))
            .ToArray();

        offending.Should().BeEmpty(
            $"{type.Name} must never expose a numeric or time-bearing property — a pickup is placed by " +
            "the compile step over the segment's own window, never by a number the model wrote; found: " +
            string.Join(", ", offending.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }

    [Theory]
    [InlineData(typeof(PickupPlanOutput))]
    [InlineData(typeof(PickupLine))]
    public void No_property_is_even_named_for_a_time(Type type)
    {
        string[] offending = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(p => PascalCaseWords(p.Name)
                .Where(w => BannedPropertyNameWords.Contains(w, StringComparer.OrdinalIgnoreCase))
                .Select(w => $"{p.PropertyType.Name} {p.Name} (word \"{w}\")"))
            .ToArray();

        offending.Should().BeEmpty(
            $"{type.Name} must never expose a property even NAMED for a time; found: " + string.Join(", ", offending));
    }

    [Fact]
    public void PickupLine_every_property_is_a_string()
    {
        PropertyInfo[] nonString = typeof(PickupLine)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(string))
            .ToArray();

        nonString.Should().BeEmpty(
            "every PickupLine property must be a plain string — SegmentId is an opaque offered id, " +
            "Text/Reason are prose; found: " + string.Join(", ", nonString.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }

    [Fact]
    public void Shapes_are_pinned_so_they_never_silently_gain_a_property()
    {
        typeof(PickupPlanOutput)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["Pickups", "PlanRationale"]);

        typeof(PickupLine)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["SegmentId", "Text", "Reason"]);
    }

    [Fact]
    public void The_story_editor_output_did_not_grow_a_repair_value()
    {
        // The whole reason this schema exists: the rushcut-invariant output must not have been
        // widened to carry a repair. Its property set is pinned; the one addition since is
        // SingleSourceJustification — a verbatim QUOTE from the brief (a string, never a time or a
        // repair) the interleave guard checks in code (docs/video-editing.md "Using every clip offered").
        // Dynamics — a list of word-and-offered-id entries (DynamicsEntryOutput, strings only; docs/design/dynamic-edit.md).
        typeof(VideoEditDecisionOutput)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["Keep", "EditRationale", "SuggestedTitle", "SingleSourceJustification", "Dynamics"]);
        typeof(VideoEditKeepSpan)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(
                ["FromId", "ToId", "Reason", "Transition", "TransitionSpeed"],
                "the transition pair is two WORDS (a preset and a speed) the compile resolves — no repair, no number");
    }

    [Fact]
    public void PickupPlanOutput_round_trips_through_camelCase_JSON_and_an_empty_list_is_valid()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plan = new PickupPlanOutput
        {
            Pickups = [new PickupLine { SegmentId = "t3", Text = "Deploys go from forty minutes to four.", Reason = "The presenter said fourteen." }],
            PlanRationale = "One flub."
        };

        string json = JsonSerializer.Serialize(plan, options);
        json.Should().Contain("\"pickups\":").And.Contain("\"segmentId\":");
        json.Should().NotContain("\"start").And.NotContain("\"end").And.NotContain("\"duration").And.NotContain("\"offset");

        PickupPlanOutput? back = JsonSerializer.Deserialize<PickupPlanOutput>(json, options);
        back!.Pickups.Should().ContainSingle().Which.SegmentId.Should().Be("t3");

        PickupPlanOutput? empty = JsonSerializer.Deserialize<PickupPlanOutput>("{\"pickups\":[],\"planRationale\":\"clean take\"}", options);
        empty!.Pickups.Should().BeEmpty("a clean take plans no pickups, which is the normal outcome");
    }

    private static IEnumerable<string> PascalCaseWords(string propertyName) =>
        Regex.Split(propertyName, "(?<!^)(?=[A-Z])").Where(w => w.Length > 0);
}
