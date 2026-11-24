using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Guards the generated-video extension of the rushcut invariant (see
/// <c>VideoEditDecisionOutputInvariantTests</c>/<c>MotionGraphicsPlanOutputInvariantTests</c>/
/// <c>MusicPlanOutputInvariantTests</c>/<c>SfxPlanOutputInvariantTests</c>/
/// <c>ColorGradePlanOutputInvariantTests</c>): the shot-director agent's structured output must be
/// structurally incapable of expressing a timestamp, a frame index, a duration in seconds, a
/// resolution, a pixel coordinate, or a price. The model contributes only opaque offered ids (a
/// cut-anchor id for placement, optional "s{n}" keyframes) plus enum WORDS
/// (Purpose/Camera/Duration) that the deterministic <c>VideoGenerate</c>/<c>VideoCompile</c> steps
/// alone resolve to provider parameters, plus one sanitized free-prose prompt. If a future change
/// adds e.g. a numeric DurationSeconds/FrameCount property here, this test must fail.
/// </summary>
public class GeneratedShotPlanOutputInvariantTests
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

    [Theory]
    [InlineData(typeof(GeneratedShotPlanOutput))]
    [InlineData(typeof(ShotPlan))]
    public void No_numeric_or_time_bearing_property_anywhere_in_the_schema(Type type)
    {
        PropertyInfo[] offending = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => BannedPropertyTypes.Contains(p.PropertyType))
            .ToArray();

        offending.Should().BeEmpty(
            $"{type.Name} must never expose a numeric or time-bearing property — the model must " +
            "not be able to emit a timestamp, a frame index, a duration in seconds, a resolution, " +
            "or a price; found: " +
            string.Join(", ", offending.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }

    [Fact]
    public void ShotPlan_every_property_is_a_string()
    {
        // Note: a nullable-reference `string?` still reports PropertyType == typeof(string), so
        // the optional FirstFrame/LastFrame keyframes and the optional Subjects offer ids pass this
        // check for the right reason — the property is still a string, never a number or a list.
        PropertyInfo[] nonString = typeof(ShotPlan)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(string))
            .ToArray();

        nonString.Should().BeEmpty(
            "every ShotPlan property must be a plain string — Anchor/FirstFrame/LastFrame are " +
            "opaque offered ids, Purpose/Camera/Duration are enum WORDS resolved entirely " +
            "server-side, and Prompt/Reason are prose; found: " +
            string.Join(", ", nonString.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }

    [Fact]
    public void Shapes_are_pinned_so_they_never_silently_gain_a_property()
    {
        // The plan-sourced VideoGenerate path iterates Shots and resolves exactly Anchor (against
        // the offered cut-anchor ids) and, when the egress gate allows it, FirstFrame/LastFrame
        // (against the offered shot ids). Pin BOTH property sets so a future numeric or id-bearing
        // addition fails HERE and forces that resolution (and its egress validation) to be
        // revisited, rather than silently shipping an unvalidated field — the same reasoning as
        // ColorGradePlanOutput's/SfxPlanOutput's own shape pins.
        typeof(GeneratedShotPlanOutput)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["Shots", "PlanRationale"]);

        typeof(ShotPlan)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo(
            [
                "Purpose",
                "Anchor",
                "FirstFrame",
                "LastFrame",
                "Camera",
                "Duration",
                "Prompt",
                "Reason",
                // docs/design/avatar-objects.md "Avatars and objects in planned shots": opaque subject offer ids
                // ("a1b2c3d4"), resolved and validated by the deterministic VideoGenerate step.
                "Subjects"
            ]);
    }

    [Fact]
    public void GeneratedShotPlanOutput_round_trips_through_camelCase_JSON()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plan = new GeneratedShotPlanOutput
        {
            Shots =
            [
                new ShotPlan
                {
                    Purpose = "Cutaway",
                    Anchor = "s3",
                    FirstFrame = "s3",
                    LastFrame = null,
                    Camera = "PushIn",
                    Duration = "Short",
                    Prompt = "A slow push across a sunlit desk with a laptop and a coffee cup.",
                    Reason = "The narration runs long over a static shot; a cutaway keeps it moving."
                }
            ],
            PlanRationale = "One cutaway covers the longest static stretch; nothing else needs a generated shot."
        };

        string json = JsonSerializer.Serialize(plan, options);
        json.Should().Contain("\"shots\":");
        json.Should().Contain("\"firstFrame\":");
        json.Should().Contain("\"lastFrame\":");
        json.Should().Contain("\"planRationale\":");

        GeneratedShotPlanOutput? roundTripped = JsonSerializer.Deserialize<GeneratedShotPlanOutput>(json, options);
        roundTripped.Should().NotBeNull();
        roundTripped!.Shots.Should().HaveCount(1);
        roundTripped.Shots[0].Purpose.Should().Be("Cutaway");
        roundTripped.Shots[0].Anchor.Should().Be("s3");
        roundTripped.Shots[0].FirstFrame.Should().Be("s3");
        roundTripped.Shots[0].LastFrame.Should().BeNull();
        roundTripped.Shots[0].Camera.Should().Be("PushIn");
        roundTripped.Shots[0].Duration.Should().Be("Short");
        roundTripped.PlanRationale.Should().Be(plan.PlanRationale);
    }

    [Fact]
    public void Subjects_are_one_string_of_offer_ids_and_absent_means_none()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        GeneratedShotPlanOutput? plan = JsonSerializer.Deserialize<GeneratedShotPlanOutput>(
            "{\"shots\":[{\"purpose\":\"Cutaway\",\"anchor\":\"s1\",\"camera\":\"Static\",\"duration\":\"Short\"," +
            "\"prompt\":\"The presenter lifts the cup.\",\"reason\":\"r\",\"subjects\":\"a1b2c3d4, a5e6f7a8\"}," +
            "{\"purpose\":\"Cutaway\",\"anchor\":\"s2\",\"camera\":\"Static\",\"duration\":\"Short\",\"prompt\":\"p\",\"reason\":\"r\"}]," +
            "\"planRationale\":\"x\"}", options);

        plan!.Shots[0].Subjects.Should().Be("a1b2c3d4, a5e6f7a8");
        plan.Shots[1].Subjects.Should().BeNull("a plan made before subjects existed shows none");
        typeof(ShotPlan).GetProperty(nameof(ShotPlan.Subjects))!.PropertyType.Should().Be(typeof(string),
            "the planner names subjects as one string of offer ids, never a list, a number or an avatar id");
    }

    [Fact]
    public void Empty_shots_list_deserializes_as_a_valid_plan()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        GeneratedShotPlanOutput? plan = JsonSerializer.Deserialize<GeneratedShotPlanOutput>(
            "{\"shots\":[],\"planRationale\":\"The source cut is complete; no generated shot is needed.\"}", options);

        plan.Should().NotBeNull();
        plan!.Shots.Should().BeEmpty("an empty shot list is a fully valid outcome, never an error");
    }
}
