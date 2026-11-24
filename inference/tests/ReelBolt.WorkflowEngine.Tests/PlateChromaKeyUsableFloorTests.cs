using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression test: found live producing the alpha launch film, by rendering the solved matte for
/// each plate and looking at it.
///
/// The two real plates in that project sit either side of what a trustworthy matte looks like:
/// the desk monitor, shot against a wall, agrees with the tracker at 0.996 and mattes cleanly
/// (bezel, notch and the typing hands all correct); the phone, shot outdoors against green
/// foliage, agrees at 0.760 — its handset keys perfectly but ragged patches of leaf and grass key
/// white alongside it, and inside the insert's overscanned quad those patches took on the
/// insert's own dark content, producing black blobs over the scenery that moved with the leaves.
///
/// The old floor of 0.75 admitted the phone by sixteen thousandths. It is NOT the scorer that was
/// at fault — <see cref="PlateChromaKeySolver.RoiDilation"/> already measures agreement 25% past
/// the plate, wider than the 10% a matted insert overscans by, so the foliage was inside the
/// measured region and 0.760 was an honest report of a quarter-wrong matte.
/// </summary>
public class PlateChromaKeyUsableFloorTests
{
    /// <summary>Agreement measured on the project's real desk-monitor plate.</summary>
    private const double MonitorPlateAgreement = 0.996;

    /// <summary>Agreement measured on the project's real outdoor phone plate.</summary>
    private const double PhonePlateAgreement = 0.760;

    [Fact]
    public void A_clean_plate_still_mattes()
    {
        MonitorPlateAgreement.Should().BeGreaterThanOrEqualTo(PlateChromaKeySolver.MinUsableScore,
            "the monitor plate mattes correctly and must keep its occlusion handling");
    }

    [Fact]
    public void A_quarter_wrong_matte_is_rejected_rather_than_carrying_the_alpha()
    {
        PhonePlateAgreement.Should().BeLessThan(PlateChromaKeySolver.MinUsableScore,
            "a matte that keys real scenery must fall back to the plain quad pin");
    }

    [Fact]
    public void The_floor_sits_clear_of_both_measurements_rather_than_splitting_hairs()
    {
        // The old 0.75 sat sixteen thousandths under the phone's score, so the two outcomes were
        // decided by rounding. Keep real margin on both sides of the floor.
        (PlateChromaKeySolver.MinUsableScore - PhonePlateAgreement).Should().BeGreaterThan(0.05);
        (MonitorPlateAgreement - PlateChromaKeySolver.MinUsableScore).Should().BeGreaterThan(0.05);
    }

    [Fact]
    public void An_unusable_key_reads_as_no_matte_downstream()
    {
        // The compile step branches on Matte being null/unusable to choose the plain quad pin, so
        // "rejected" has to be expressible, not just a low score.
        PlateChromaKey.None.IsUsable.Should().BeFalse();
    }
}
