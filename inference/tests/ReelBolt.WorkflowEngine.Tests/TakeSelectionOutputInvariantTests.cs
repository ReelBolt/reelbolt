using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Guards the dailies extension of the rushcut invariant (issue #93 phase 4; see
/// <c>VideoEditDecisionOutputInvariantTests</c> and its siblings): the take-selector / dailies
/// director's structured output must be structurally incapable of expressing a score, a frame
/// count, a timestamp, a duration or a storage key. The model contributes only opaque offered take
/// ids (<c>v{n}</c>/<c>v{n}t{k}</c>) plus prose; <c>VideoCompileStepExecutor</c>'s
/// <c>TakeSelection</c> resolution alone turns them into "which manifest clip is placed". If a
/// future change adds e.g. a numeric <c>Score</c> or <c>Rank</c> property here, this test must fail.
/// </summary>
public class TakeSelectionOutputInvariantTests
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
    [InlineData(typeof(TakeSelectionOutput))]
    [InlineData(typeof(TakeSelection))]
    public void No_numeric_or_time_bearing_property_anywhere_in_the_schema(Type type)
    {
        PropertyInfo[] offending = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => BannedPropertyTypes.Contains(p.PropertyType))
            .ToArray();

        offending.Should().BeEmpty(
            $"{type.Name} must never expose a numeric or time-bearing property — the model must " +
            "not be able to emit a score, a rank, a frame index or a timestamp; found: " +
            string.Join(", ", offending.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }

    [Fact]
    public void TakeSelection_every_property_is_a_string()
    {
        PropertyInfo[] nonString = typeof(TakeSelection)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(string))
            .ToArray();

        nonString.Should().BeEmpty(
            "every TakeSelection property is a plain string (an offered id or prose); found: " +
            string.Join(", ", nonString.Select(p => $"{p.PropertyType.Name} {p.Name}")));
    }

    [Fact]
    public void TakeSelection_has_exactly_the_pinned_property_set()
    {
        string[] names = typeof(TakeSelection)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        names.Should().Equal("Reason", "TakeId");
    }

    [Fact]
    public void TakeSelectionOutput_has_exactly_the_pinned_property_set()
    {
        string[] names = typeof(TakeSelectionOutput)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        names.Should().Equal("PlanRationale", "Selected");
    }
}
