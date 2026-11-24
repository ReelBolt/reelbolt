using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Editing.Color;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing.Color;

/// <summary>
/// Independent spec anchors for the camera log profiles.
///
/// Every expected number in this file is transcribed from
/// the camera-profile research spec (section numbers are
/// quoted at each group). Nothing here is derived from the production constants or from
/// CameraProfilesTests. Tolerances are the linear change covered by the precision the spec
/// prints (a 10-bit anchor is printed to the nearest code, code value to the printed
/// decimals), never looser than that resolution.
/// </summary>
public class CameraProfileSpecAnchorTests
{
    private static CameraProfileDefinition Profile(string id)
    {
        CameraProfiles.TryGet(id, out CameraProfileDefinition? profile)
            .Should().BeTrue($"the profile '{id}' must be registered");
        return profile!;
    }

    [Theory]
    [InlineData("appleLog")]
    [InlineData("djiDLog")]
    [InlineData("djiDLogM")]
    [InlineData("sonySLog3")]
    [InlineData("panasonicVLog")]
    [InlineData("canonCLog3")]
    [InlineData("arriLogC3")]
    [InlineData("arriLogC4")]
    [InlineData("fujiFLog")]
    [InlineData("fujiFLog2")]
    [InlineData("nikonNLog")]
    [InlineData("redLog3G10")]
    public void Every_spec_profile_id_resolves(string id)
    {
        Profile(id).Id.Should().Be(id);
    }

    // Sections 1(2), 2(2), 3(2), 4(2), 5(2), 6(2), 7(2), 8(2), 9(2), 10(2), 11(2):
    // printed anchor rows. Codes are the spec's printed code values (10-bit codes are
    // divided by 1023); expected is the scene-linear value printed for that row, in the
    // 0.18-grey convention the surface documents.
    [Theory]
    // Apple Log, section 1(2): 0 -> 0.150476, 0.18 -> 0.488272, 0.90 -> 0.681687,
    // 1.00 -> 0.694553, P=0 -> -0.05641088, P=1.0 -> R=12.0000.
    [InlineData("appleLog", 0.150476, 0.0, 1e-4)]
    [InlineData("appleLog", 0.488272, 0.18, 1e-4)]
    [InlineData("appleLog", 0.681687, 0.90, 1e-4)]
    [InlineData("appleLog", 0.694553, 1.00, 1e-4)]
    [InlineData("appleLog", 0.0, -0.05641088, 1e-6)]
    [InlineData("appleLog", 1.0, 12.0, 0.01)]
    // DJI D-Log, section 2(2): 95 / 408 / 586; decode(1.0) = 41.9994.
    [InlineData("djiDLog", 0.092864, 0.0, 1e-4)]
    [InlineData("djiDLog", 0.398729, 0.18, 5e-4)]
    [InlineData("djiDLog", 0.572923, 0.90, 2e-3)]
    [InlineData("djiDLog", 1.0, 41.9994, 0.05)]
    // Sony S-Log3, section 3(2): 95 / 420 / 598; decode(1.0) = 38.42.
    [InlineData("sonySLog3", 0.092864, 0.0, 1e-4)]
    [InlineData("sonySLog3", 0.410557, 0.18, 5e-4)]
    [InlineData("sonySLog3", 0.584458, 0.90, 2e-3)]
    [InlineData("sonySLog3", 1.0, 38.42, 0.05)]
    // Panasonic V-Log, section 4(2): 128 / 433 / 602; decode(1.0) = 46.09.
    [InlineData("panasonicVLog", 0.125024, 0.0, 1e-4)]
    [InlineData("panasonicVLog", 0.423266, 0.18, 5e-4)]
    [InlineData("panasonicVLog", 0.588172, 0.90, 2e-3)]
    [InlineData("panasonicVLog", 1.0, 46.09, 0.05)]
    // Canon Log 3, section 5(2): 128 / 351 / 577 / 908 / 1020 map to scene-linear
    // x = 0 / 0.20 / 1.0 / 8 / 16 and the 0.18-grey convention is R = 0.9*x;
    // decode(1.0) -> R = 14.67.
    [InlineData("canonCLog3", 0.125122, 0.0, 1e-4)]
    [InlineData("canonCLog3", 0.343402, 0.18, 5e-4)]
    [InlineData("canonCLog3", 0.564516, 0.90, 2e-3)]
    [InlineData("canonCLog3", 0.887195, 7.2, 0.01)]
    [InlineData("canonCLog3", 0.997067, 14.4, 0.02)]
    [InlineData("canonCLog3", 1.0, 14.67, 0.05)]
    // ARRI LogC3 (EI 800), section 6(2): black 0.0928, 18% grey 0.391 (400/1023),
    // 90% white 0.5594 (572.3/1023); decode(1.0) = 55.08.
    [InlineData("arriLogC3", 0.0928, 0.0, 1e-4)]
    [InlineData("arriLogC3", 0.391, 0.18, 1e-3)]
    [InlineData("arriLogC3", 0.5594, 0.90, 2e-3)]
    [InlineData("arriLogC3", 1.0, 55.08, 0.01)]
    // ARRI LogC4, section 7(2): 0.0 -> -0.0181, 0.0929 -> 0.0, 0.2784 -> 0.18,
    // hardware max 1.0 -> 469.80.
    [InlineData("arriLogC4", 0.0, -0.0181, 1e-4)]
    [InlineData("arriLogC4", 0.0929, 0.0, 1e-4)]
    [InlineData("arriLogC4", 0.2784, 0.18, 1e-3)]
    [InlineData("arriLogC4", 1.0, 469.80, 0.5)]
    // Fujifilm F-Log, section 8(2): 95 / 470 / 705; decode(1.0) = 7.28.
    [InlineData("fujiFLog", 0.092864, 0.0, 1e-4)]
    [InlineData("fujiFLog", 0.459335, 0.18, 5e-4)]
    [InlineData("fujiFLog", 0.689541, 0.90, 2e-3)]
    [InlineData("fujiFLog", 1.0, 7.28, 0.01)]
    // Fujifilm F-Log2, section 9(2): 95 / 400 / 570; decode(1.0) = 58.25.
    [InlineData("fujiFLog2", 0.092864, 0.0, 1e-4)]
    [InlineData("fujiFLog2", 0.391007, 0.18, 5e-4)]
    [InlineData("fujiFLog2", 0.557087, 0.90, 0.01)]
    [InlineData("fujiFLog2", 1.0, 58.25, 0.1)]
    // Nikon N-Log, section 10(2): 127.2 / 372.0 / 603.2; decode(code 1023) = 14.78.
    [InlineData("nikonNLog", 0.124340, 0.0, 1e-4)]
    [InlineData("nikonNLog", 0.363636, 0.18, 1e-3)]
    [InlineData("nikonNLog", 0.589638, 0.90, 1e-3)]
    [InlineData("nikonNLog", 1.0, 14.78, 0.05)]
    // RED Log3G10, section 11(2): -0.010000 -> 0.000000, 0.000000 -> 0.091551,
    // 0.180000 -> 0.333333, 1.000000 -> 0.493449, 184.322 -> 1.000000.
    [InlineData("redLog3G10", 0.0, -0.01, 1e-6)]
    [InlineData("redLog3G10", 0.091551, 0.0, 1e-4)]
    [InlineData("redLog3G10", 0.333333, 0.18, 1e-4)]
    [InlineData("redLog3G10", 0.493449, 1.0, 1e-4)]
    [InlineData("redLog3G10", 1.0, 184.322, 0.5)]
    public void Decode_matches_the_printed_spec_anchor(string id, double code, double expectedLinear, double tolerance)
    {
        CameraProfileDefinition profile = Profile(id);

        profile.DecodeToLinear(code).Should().BeApproximately(expectedLinear, tolerance);
    }

    // Ticket-wide property: GamutToRec709 is linear camera-gamut -> linear Rec.709 with
    // D65 white on both sides, so every row sums to 1 (white is preserved).
    [Theory]
    [InlineData("appleLog")]
    [InlineData("djiDLog")]
    [InlineData("djiDLogM")]
    [InlineData("sonySLog3")]
    [InlineData("panasonicVLog")]
    [InlineData("canonCLog3")]
    [InlineData("arriLogC3")]
    [InlineData("arriLogC4")]
    [InlineData("fujiFLog")]
    [InlineData("fujiFLog2")]
    [InlineData("nikonNLog")]
    [InlineData("redLog3G10")]
    public void Gamut_matrix_rows_sum_to_one(string id)
    {
        double[] m = Profile(id).GamutToRec709;

        // Spec 11(3): RED's published Rec.709 matrix differs from a fresh derivation in the
        // 4th-5th digit (rows sum to 0.999952..1.00015), so RED is held to 2e-4.
        double tol = id == "redLog3G10" ? 2e-4 : 1e-5;

        m.Length.Should().Be(9);
        (m[0] + m[1] + m[2]).Should().BeApproximately(1.0, tol);
        (m[3] + m[4] + m[5]).Should().BeApproximately(1.0, tol);
        (m[6] + m[7] + m[8]).Should().BeApproximately(1.0, tol);
    }

    // Published/printed Rec.709 matrices, transcribed from the spec (sections 2(3),
    // 4(3), 6(3), 7(3), 11(3)). Compared entry by entry within the printed precision.
    public static IEnumerable<object[]> PublishedRec709Matrices()
    {
        yield return new object[]
        {
            "djiDLog", // section 2(3), published D-Gamut -> Rec.709
            new double[]
            {
                1.6746, -0.5797, -0.0949,
                -0.0981, 1.3340, -0.2359,
                -0.0410, -0.2430, 1.2840,
            },
        };
        yield return new object[]
        {
            "panasonicVLog", // section 4(3), published V-Gamut -> Rec.709
            new double[]
            {
                1.806576, -0.695697, -0.110879,
                -0.170090, 1.305955, -0.135865,
                -0.025206, -0.154468, 1.179674,
            },
        };
        yield return new object[]
        {
            "arriLogC3", // section 6(3), published ALEXA Wide Gamut 3 -> Rec.709
            new double[]
            {
                1.617523, -0.537287, -0.080237,
                -0.070573, 1.334613, -0.264040,
                -0.021102, -0.226954, 1.248056,
            },
        };
        yield return new object[]
        {
            "arriLogC4", // section 7(3), ARRI Wide Gamut 4 -> Rec.709
            new double[]
            {
                1.893123443, -0.780881504, -0.112241939,
                -0.205700358, 1.340257489, -0.134557131,
                -0.012705743, -0.152184876, 1.164890619,
            },
        };
        yield return new object[]
        {
            "redLog3G10", // section 11(3), published REDWideGamutRGB -> Rec.709
            new double[]
            {
                1.981880, -0.900388, -0.081540,
                -0.178143, 1.500467, -0.322325,
                -0.101811, -0.535343, 1.637304,
            },
        };
    }

    [Theory]
    [MemberData(nameof(PublishedRec709Matrices))]
    public void Gamut_matrix_matches_the_published_rec709_matrix(string id, double[] published)
    {
        double[] actual = Profile(id).GamutToRec709;

        actual.Length.Should().Be(9);
        for (int i = 0; i < 9; i++)
        {
            actual[i].Should().BeApproximately(published[i], 1e-4, $"matrix entry {i}");
        }
    }

    // Neutral display transform, spec "Neutral display transform": decoding the printed
    // 18% grey code gives a neutral triple (r = g = b) at V = 0.409, and the printed 90%
    // white code gives V ~ 0.906.
    [Theory]
    // Section 1(5): grey 0.4883; white 0.681687.
    [InlineData("appleLog", 0.488272, 0.681687)]
    // Section 2(5): grey 408/1023 (computed 407.9); white 586/1023 (computed 586.1).
    [InlineData("djiDLog", 0.398729, 0.572923)]
    // Section 3(5): grey 420/1023; white 598/1023 (computed 597.9).
    [InlineData("sonySLog3", 0.410557, 0.584458)]
    // Section 4(5): grey 433/1023; white 602/1023 (computed 601.7).
    [InlineData("panasonicVLog", 0.423266, 0.588172)]
    // Section 5(5): grey 351/1023 (computed 351.3, x = 0.20); white 577/1023 (computed 577.5, x = 1.0).
    [InlineData("canonCLog3", 0.343402, 0.564516)]
    // Section 6(5): grey 0.391 (400/1023); white 0.5594 (572.3/1023).
    [InlineData("arriLogC3", 0.391, 0.5594)]
    // Section 7(5): grey 0.2784 (285/1023 full).
    [InlineData("arriLogC4", 0.2784, 0.0)]
    // Section 8(5): grey 470/1023 (computed 469.9); white 705/1023 (computed 705.4).
    [InlineData("fujiFLog", 0.459335, 0.689541)]
    // Section 9(5): grey 400/1023; white 570/1023 (computed 569.9).
    [InlineData("fujiFLog2", 0.391007, 0.557087)]
    // Section 10(5): grey 372/1023; white 603.2/1023.
    [InlineData("nikonNLog", 0.363636, 0.589638)]
    // Section 11(5): grey 1/3; the table prints no 90% white row.
    [InlineData("redLog3G10", 0.333333, 0.0)]
    public void Display_transform_is_neutral_at_the_printed_anchors(string id, double greyCode, double whiteCode)
    {
        CameraProfileDefinition profile = Profile(id);

        (double r, double g, double b) = CameraDisplayTransform.ToRec709(profile, greyCode, greyCode, greyCode);

        double nTol = id == "redLog3G10" ? 1e-4 : 1e-6; // spec 11(3): published RED matrix rows sum to 1 only within ~5e-5
        r.Should().BeApproximately(g, nTol);
        g.Should().BeApproximately(b, nTol);
        r.Should().BeApproximately(0.409, 2e-3);

        if (whiteCode > 0)
        {
            (double wr, double wg, double wb) = CameraDisplayTransform.ToRec709(profile, whiteCode, whiteCode, whiteCode);

            wr.Should().BeApproximately(wg, 1e-6);
            wg.Should().BeApproximately(wb, 1e-6);
            wr.Should().BeApproximately(0.906, 2e-3);
        }
    }

    // Section 2b: D-Log M has no public specification, so the library must flag it
    // Approximate rather than claim a maker-accurate decode.
    [Fact]
    public void DLogM_is_marked_approximate()
    {
        Profile("djiDLogM").Status.Should().Be(CameraProfileStatus.Approximate);
    }
}
