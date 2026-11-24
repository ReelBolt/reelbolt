using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Brand;

/// <summary>
/// P4-02 coverage for <see cref="BrandTemperature.Classify"/>: the documented warm/cool/neutral cases,
/// the saturation and total-weight rules, and the guarantee that malformed input is skipped rather
/// than thrown on.
/// </summary>
public class BrandTemperatureTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { new[] { "#ff0000", "#ff7f00", "#ff4500" }, "warm" };
        yield return new object[] { new[] { "#ff0000" }, "warm" };
        yield return new object[] { new[] { "#0000ff", "#008080", "#00bfff" }, "cool" };
        yield return new object[] { new[] { "#008080" }, "cool" };
        yield return new object[] { new[] { "#808080", "#9e9e9e", "#bdbdbd" }, "neutral" };
        yield return new object[] { new[] { "#e5e5e5" }, "neutral" };
        yield return new object[] { new[] { "#ff0000", "#0000ff" }, "neutral" };
        yield return new object[] { Array.Empty<string>(), "neutral" };
        yield return new object[] { new[] { "nope", "#12", "#gggggg", "" }, "neutral" };
        yield return new object[] { new[] { "#ff000080" }, "warm" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify_matches_the_expected_temperature(string[] hexes, string expected)
    {
        BrandTemperature.Classify(hexes).Should().Be(expected);
    }

    [Fact]
    public void Classify_never_throws_on_bad_input()
    {
        BrandTemperature.Classify(null!).Should().Be("neutral");
        BrandTemperature.Classify(new[] { null!, "##ff0000", "#ff00000", "#ff0000000" }).Should().Be("neutral");
    }
}
