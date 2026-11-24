using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Guards the picture-overlay extension of the rushcut invariant (design
/// <c>docs/design/picture-overlay.md</c> §5.2 and §12): the OverlayPlanner agent's structured
/// output must be structurally incapable of expressing a timestamp, a duration, a frame number or
/// a pixel coordinate. The model contributes only an opaque media id and an opaque overlay-window
/// id drawn from sets it was actually offered, plus enum-word choices (form/anchor/size/border/
/// sound) that <c>VideoCompileStepExecutor</c> alone resolves to concrete geometry, timing and
/// filters.
///
/// <see cref="MediaOverlayChoice.OpacityPercent"/> is the one documented exemption: a bounded
/// 0..100 render constant with no temporal or spatial meaning, which the compile clamps
/// regardless. If a future change adds e.g. an X/Y rect, a start timestamp or a duration here,
/// this test must fail.
/// </summary>
public class MediaOverlayPlanOutputInvariantTests
{
    private static readonly Type[] BannedPropertyTypes =
    [
        typeof(long), typeof(long?),
        typeof(float), typeof(float?),
        typeof(double), typeof(double?),
        typeof(decimal), typeof(decimal?),
        typeof(TimeSpan), typeof(TimeSpan?),
        typeof(DateTime), typeof(DateTime?),
        typeof(DateTimeOffset), typeof(DateTimeOffset?),
        typeof(VideoAnalysisRect)
    ];

    [Fact]
    public void MediaOverlayPlanOutput_has_no_numeric_time_or_coordinate_property()
    {
        AssertNoBannedProperties(typeof(MediaOverlayPlanOutput));
    }

    [Fact]
    public void MediaOverlayChoice_has_no_numeric_time_or_coordinate_property()
    {
        AssertNoBannedProperties(typeof(MediaOverlayChoice));
    }

    /// <summary>
    /// Stronger than the banned-type check: every property is a plain string EXCEPT exactly one
    /// int, <see cref="MediaOverlayChoice.OpacityPercent"/>. Any other numeric type — including
    /// another int, however small — is a regression on the invariant's single documented
    /// exemption, and any property whose type name looks coordinate-bearing is rejected too.
    /// </summary>
    [Fact]
    public void MediaOverlayChoice_carries_only_strings_and_the_one_documented_opacity_int()
    {
        PropertyInfo[] properties = typeof(MediaOverlayChoice)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        properties.Should().NotBeEmpty();

        PropertyInfo[] numeric = properties
            .Where(p => IsNumeric(p.PropertyType))
            .ToArray();

        numeric.Select(p => p.Name).Should().BeEquivalentTo(
            [nameof(MediaOverlayChoice.OpacityPercent)],
            "OpacityPercent is the single documented numeric exemption; no other property may be numeric");

        typeof(MediaOverlayChoice).GetProperty(nameof(MediaOverlayChoice.OpacityPercent))!
            .PropertyType.Should().Be(typeof(int), "the one exemption is a bounded whole percentage");

        properties
            .Where(p => p.Name != nameof(MediaOverlayChoice.OpacityPercent))
            .Should().OnlyContain(p => p.PropertyType == typeof(string),
                "every other member must be an enum WORD carried as a plain string");
    }

    [Fact]
    public void MediaOverlayPlanOutput_Overlays_defaults_to_an_empty_list()
    {
        new MediaOverlayPlanOutput().Overlays.Should().BeEmpty(
            "empty is a valid plan and a plan produced before a later additive field must not deserialize to null");
    }

    [Fact]
    public void MediaOverlayPlanOutput_round_trips_camelCase_JSON()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var plan = new MediaOverlayPlanOutput
        {
            PlanRationale = "the review belongs on the opening claim",
            Overlays =
            {
                new MediaOverlayChoice
                {
                    MediaId = "m0",
                    WindowId = "o1",
                    Form = "Card",
                    Anchor = "BottomRight",
                    Size = "Medium",
                    OpacityPercent = 100,
                    Border = "Light",
                    Audio = "Muted",
                    AudioLevel = "Balanced",
                    CardAudio = "Keep",
                    Reason = "shows the five-star review the speaker quotes"
                }
            }
        };

        string json = JsonSerializer.Serialize(plan, options);
        json.Should().Contain("\"overlays\":");
        json.Should().Contain("\"planRationale\":");
        json.Should().Contain("\"mediaId\":");
        json.Should().Contain("\"windowId\":");
        json.Should().Contain("\"opacityPercent\":");
        json.Should().Contain("\"cardAudio\":");

        MediaOverlayPlanOutput? roundTripped = JsonSerializer.Deserialize<MediaOverlayPlanOutput>(json, options);
        roundTripped.Should().NotBeNull();
        roundTripped!.PlanRationale.Should().Be(plan.PlanRationale);
        roundTripped.Overlays.Should().HaveCount(1);
        roundTripped.Overlays[0].Should().BeEquivalentTo(plan.Overlays[0]);
        roundTripped.Overlays[0].OpacityPercent.Should().Be(100);
    }

    private static bool IsNumeric(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(int) || underlying == typeof(long)
            || underlying == typeof(short) || underlying == typeof(byte)
            || underlying == typeof(float) || underlying == typeof(double)
            || underlying == typeof(decimal);
    }

    private static void AssertNoBannedProperties(Type type)
    {
        PropertyInfo[] offending = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => BannedPropertyTypes.Contains(p.PropertyType)
                || p.PropertyType.Name.Contains("Rect", StringComparison.OrdinalIgnoreCase)
                || p.PropertyType.Name.Contains("Coordinate", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        offending.Should().BeEmpty(
            $"{type.Name} must never expose a numeric, time-bearing or coordinate-bearing property — "
            + "the model must not be able to emit a timestamp or a coordinate; found: "
            + string.Join(", ", offending.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }
}
