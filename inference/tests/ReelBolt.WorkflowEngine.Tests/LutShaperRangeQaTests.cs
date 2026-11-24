using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Editing.Color;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// QA pin (lut-1d-support): plan.md "Shaper + 3D" maps shaper outputs s to (s-c)/(d-c) clamped 0..1. The web parser
/// must agree (web/lib/editing/color/__tests__/lut-shaper-range.test.ts pins the same file and numbers).
/// </summary>
public sealed class LutShaperRangeQaTests
{
    [Fact]
    public void Shaper_outputs_above_one_map_onto_a_wide_LUT_3D_INPUT_RANGE()
    {
        string identity = string.Join("", Enumerable.Range(0, 8).Select(i => $"{i & 1} {(i >> 1) & 1} {(i >> 2) & 1}\n"));
        CubeLut lut = CubeLut.Parse("LUT_1D_SIZE 2\nLUT_3D_SIZE 2\nLUT_3D_INPUT_RANGE 0 2\n0 0 0\n2 2 2\n" + identity);
        lut.Kind.Should().Be(CubeLutKind.ShaperThreeD);
        lut.Sample(1, 1, 1).R.Should().BeApproximately(1f, 1e-5f);
        lut.Sample(0.5f, 0.5f, 0.5f).R.Should().BeApproximately(0.5f, 1e-5f);
    }
}
