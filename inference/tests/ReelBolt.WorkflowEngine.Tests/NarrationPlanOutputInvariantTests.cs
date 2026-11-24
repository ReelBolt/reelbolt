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
/// Guards the narration extension of the rushcut invariant (see
/// <c>VideoEditDecisionOutputInvariantTests</c>/<c>MotionGraphicsPlanOutputInvariantTests</c>/
/// <c>MusicPlanOutputInvariantTests</c>/<c>SfxPlanOutputInvariantTests</c>/
/// <c>GeneratedShotPlanOutputInvariantTests</c>/<c>ColorGradePlanOutputInvariantTests</c>): the
/// narration-writer agent's structured output must be structurally incapable of expressing a
/// timestamp, an offset, a duration, a frame number, a coordinate, or a percentage. The model
/// contributes one opaque offered id per line (an <c>s{n}</c>/<c>g{n}</c>/<c>t{n}</c> cut-anchor
/// id) plus prose, and nothing else. If a future change adds e.g. an <c>OffsetMs</c>/<c>StartSec</c>
/// property here, this test must fail.
///
/// <para>
/// This variant adds a check its siblings do not have: the rushcut invariant bans numeric CLR
/// TYPES, which is necessary but not sufficient for a schema whose whole point is text. A
/// <c>string StartSec</c> is still a timestamp the model can emit, and it would pass every
/// type-based guard in this file's siblings. Every property NAME is therefore also swept for
/// time-shaped words. <c>NarrationLine</c> has no numeric field to begin with — that is the point —
/// so the name sweep is what makes the ban hold as prose shapes evolve.
/// </para>
/// </summary>
public class NarrationPlanOutputInvariantTests
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

    /// <summary>
    /// Time-shaped PascalCase WORDS, not substrings: a property named <c>Duration</c>,
    /// <c>OffsetMs</c> or <c>StartSec</c> is flagged, while <c>Delivery</c>, <c>Text</c>,
    /// <c>Lines</c> and <c>PlanRationale</c> are not. Splitting on the CamelCase boundary rather
    /// than testing raw substrings is what keeps this from either missing <c>Ms</c>-suffixed
    /// names or false-positiving on an innocent word that merely contains one.
    /// </summary>
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
    [InlineData(typeof(NarrationPlanOutput))]
    [InlineData(typeof(NarrationLine))]
    public void No_numeric_or_time_bearing_property_anywhere_in_the_schema(Type type)
    {
        PropertyInfo[] offending = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => BannedPropertyTypes.Contains(p.PropertyType))
            .ToArray();

        offending.Should().BeEmpty(
            $"{type.Name} must never expose a numeric or time-bearing property — the model must " +
            "not be able to emit a timestamp, an offset, a duration, a frame number, a " +
            "coordinate, or a percentage; found: " +
            string.Join(", ", offending.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }

    [Theory]
    [InlineData(typeof(NarrationPlanOutput))]
    [InlineData(typeof(NarrationLine))]
    public void No_property_is_even_named_for_a_time(Type type)
    {
        // The type ban above cannot catch a time carried in a STRING. Pin the names too: a future
        // `string StartSec` / `string OffsetMs` / `string DurationFrames` would otherwise ship
        // green through every check in this file, because its CLR type is legal.
        string[] offending = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(p => PascalCaseWords(p.Name)
                .Where(w => BannedPropertyNameWords.Contains(w, StringComparer.OrdinalIgnoreCase))
                .Select(w => $"{p.PropertyType.Name} {p.Name} (word \"{w}\")"))
            .ToArray();

        offending.Should().BeEmpty(
            $"{type.Name} must never expose a property even NAMED for a time — a string-typed " +
            "StartSec/OffsetMs/Duration is a timestamp the model can emit just as surely as an " +
            "int one, and no type-based guard would catch it; found: " +
            string.Join(", ", offending));
    }

    [Fact]
    public void NarrationLine_every_property_is_a_string()
    {
        PropertyInfo[] nonString = typeof(NarrationLine)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(string))
            .ToArray();

        nonString.Should().BeEmpty(
            "every NarrationLine property must be a plain string — AnchorId is an opaque offered " +
            "id, Text/Delivery/Reason are prose and nothing else; found: " +
            string.Join(", ", nonString.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }

    [Fact]
    public void NarrationPlanOutput_is_one_line_list_and_one_rationale()
    {
        typeof(NarrationPlanOutput)
            .GetProperty(nameof(NarrationPlanOutput.Lines))!.PropertyType
            .Should().Be(typeof(List<NarrationLine>),
                "Lines must stay a List<NarrationLine> — a list of anything else would put a " +
                "non-prose element in the schema without NarrationLine's own guarantees");

        typeof(NarrationPlanOutput)
            .GetProperty(nameof(NarrationPlanOutput.PlanRationale))!.PropertyType
            .Should().Be(typeof(string));
    }

    [Fact]
    public void Shapes_are_pinned_so_they_never_silently_gain_a_property()
    {
        // The narration path resolves exactly AnchorId (against the offered cut-anchor ids) and
        // passes Text/Delivery/Reason through as prose. Pin BOTH property sets so a future
        // numeric, id-bearing or time-NAMED addition fails HERE and forces that resolution to be
        // revisited, rather than silently shipping an unvalidated field — the same reasoning as
        // SfxPlanOutput's/GeneratedShotPlanOutput's own shape pins.
        typeof(NarrationPlanOutput)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["Lines", "PlanRationale"]);

        typeof(NarrationLine)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["AnchorId", "Text", "Delivery", "Reason"]);
    }

    [Fact]
    public void NarrationPlanOutput_round_trips_through_camelCase_JSON()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plan = new NarrationPlanOutput
        {
            Lines =
            [
                new NarrationLine
                {
                    AnchorId = "s2",
                    Text = "Every cut, placed by the edit itself.",
                    Delivery = "Warm and unhurried, landing the product name last.",
                    Reason = "The demo shot is silent; the line carries the claim it cannot."
                }
            ],
            PlanRationale = "One line over the demo, then silence through the closing card."
        };

        string json = JsonSerializer.Serialize(plan, options);
        json.Should().Contain("\"lines\":");
        json.Should().Contain("\"anchorId\":");
        json.Should().Contain("\"delivery\":");
        json.Should().Contain("\"planRationale\":");
        // A time-shaped property would have to appear as a JSON key to be emittable at all; the
        // reflection sweep above is the structural guard, this is the wire-level cross-check.
        json.Should().NotContain("\"start");
        json.Should().NotContain("\"end");
        json.Should().NotContain("\"duration");
        json.Should().NotContain("\"offset");
        json.Should().NotContain("\"frame");
        json.Should().NotContain("\"time");

        NarrationPlanOutput? roundTripped = JsonSerializer.Deserialize<NarrationPlanOutput>(json, options);
        roundTripped.Should().NotBeNull();
        roundTripped!.Lines.Should().HaveCount(1);
        roundTripped.Lines[0].AnchorId.Should().Be("s2");
        roundTripped.Lines[0].Text.Should().Be("Every cut, placed by the edit itself.");
        roundTripped.Lines[0].Delivery.Should().Be("Warm and unhurried, landing the product name last.");
        roundTripped.PlanRationale.Should().Be(plan.PlanRationale);
    }

    [Fact]
    public void Empty_line_list_deserializes_as_a_valid_plan()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        NarrationPlanOutput? plan = JsonSerializer.Deserialize<NarrationPlanOutput>(
            "{\"lines\":[],\"planRationale\":\"The edit carries itself; no narration is needed.\"}", options);

        plan.Should().NotBeNull();
        plan!.Lines.Should().BeEmpty("an empty line list is a fully valid outcome, never an error");
    }

    /// <summary>Splits a PascalCase property name into its words ("OffsetMs" → ["Offset", "Ms"]).</summary>
    private static IEnumerable<string> PascalCaseWords(string propertyName) =>
        Regex.Split(propertyName, "(?<!^)(?=[A-Z])").Where(w => w.Length > 0);
}
