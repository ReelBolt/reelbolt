using System.Diagnostics.CodeAnalysis;

namespace ReelBolt.Shared.Editing.Color;

/// <summary>
/// The closed set of camera log profiles. Our own implementation of the published curves and gamut
/// matrices (no vendor LUT file is used or redistributed). Constants are copied as printed in the
/// maker documents named in each <see cref="CameraProfileDefinition.SourceNote"/>; every profile is
/// <see cref="CameraProfileStatus.Uncalibrated"/> unless stated. Matrices are row-major linear
/// camera gamut to linear Rec.709 (D65 to D65, no adaptation).
/// </summary>
public static class CameraProfiles
{
    // BT.2020 -> Rec.709 (derived; equals ITU-R BT.2407).
    private static readonly double[] Bt2020 =
    [
        1.660491, -0.587641, -0.072850,
        -0.124550, 1.132900, -0.008349,
        -0.018151, -0.100579, 1.118730,
    ];

    // D-Gamut -> Rec.709, as published by DJI.
    private static readonly double[] DGamut =
    [
        1.6746, -0.5797, -0.0949,
        -0.0981, 1.3340, -0.2359,
        -0.0410, -0.2430, 1.2840,
    ];

    // S-Gamut3.Cine -> Rec.709 (derived). Documented alternate, not a separate id: S-Gamut3 is
    // 1.877915 -0.794169 -0.083746 / -0.176807 1.351000 -0.174193 / -0.026201 -0.148422 1.174623.
    private static readonly double[] SGamut3Cine =
    [
        1.626947, -0.540139, -0.086809,
        -0.178516, 1.417941, -0.239425,
        -0.044436, -0.195920, 1.240356,
    ];

    // V-Gamut -> Rec.709, as published by Panasonic.
    private static readonly double[] VGamut =
    [
        1.806576, -0.695697, -0.110879,
        -0.170090, 1.305955, -0.135865,
        -0.025206, -0.154468, 1.179674,
    ];

    // Canon Cinema Gamut -> Rec.709 (derived; primaries verified through Canon's IDT matrix).
    private static readonly double[] CinemaGamut =
    [
        1.923861, -0.798761, -0.125101,
        -0.204311, 1.495899, -0.291588,
        -0.023685, -0.420127, 1.443812,
    ];

    // ALEXA Wide Gamut (AWG3) -> Rec.709, as published by ARRI.
    private static readonly double[] Awg3 =
    [
        1.617523, -0.537287, -0.080237,
        -0.070573, 1.334613, -0.264040,
        -0.021102, -0.226954, 1.248056,
    ];

    // ARRI Wide Gamut 4 -> Rec.709 (derived from ARRI's published XYZ matrix).
    private static readonly double[] Awg4 =
    [
        1.893123443, -0.780881504, -0.112241939,
        -0.205700358, 1.340257489, -0.134557131,
        -0.012705743, -0.152184876, 1.164890619,
    ];

    // REDWideGamutRGB -> Rec.709, as published by RED.
    private static readonly double[] RedWideGamut =
    [
        1.981880, -0.900388, -0.081540,
        -0.178143, 1.500467, -0.322325,
        -0.101811, -0.535343, 1.637304,
    ];

    // LogC4 constants, evaluated from the equations of the ARRI LogC4 specification (23 Jan 2025).
    private const double C4A = (262144.0 - 16.0) / 117.45;
    private const double C4B = (1023.0 - 95.0) / 1023.0;
    private const double C4C = 95.0 / 1023.0;
    private static readonly double C4S = 7.0 * Math.Log(2.0) * Math.Pow(2.0, 7.0 - 14.0 * C4C / C4B) / (C4A * C4B);
    private static readonly double C4T = (Math.Pow(2.0, 14.0 * (-C4C / C4B) + 6.0) - 64.0) / C4A;

    /// <summary>All profiles, in the canonical order. FROZEN and append-only: the index is the proxy derivative variant (see <see cref="CameraProfileVariants"/>).</summary>
    public static IReadOnlyList<CameraProfileDefinition> All { get; } =
    [
        new("appleLog", "Apple Log", "Apple", CameraProfileStatus.Uncalibrated, "legal", "bt2020nc", AppleLog, Bt2020,
            "IDT.Apple.AppleLog_BT2020.ctl (Apple-supplied, ACES aces-dev 528c78f); Apple Log white paper not read. Range is unconfirmed (ProRes convention assumed)"),
        new("djiDLog", "D-Log", "DJI", CameraProfileStatus.Uncalibrated, "full", "bt709", DjiDLog, DGamut,
            "White Paper on D-Log and D-Gamut of DJI Cinema Color System, Rev. 1.0 (2017-09-29)"),
        // APPROXIMATE stand-in: DJI publishes no D-Log M specification. The D-Log curve and D-Gamut
        // are used as a rough approximation; this is NOT a calibrated D-Log M decoder.
        new("djiDLogM", "D-Log M", "DJI", CameraProfileStatus.Approximate, "full", "bt709", DjiDLog, DGamut,
            "No public D-Log M specification; approximated with the D-Log curve and D-Gamut (White Paper on D-Log and D-Gamut, Rev. 1.0)"),
        new("sonySLog3", "S-Log3", "Sony", CameraProfileStatus.Uncalibrated, "full", "bt709", SonySLog3, SGamut3Cine,
            "Technical Summary for S-Gamut3.Cine/S-Log3 and S-Gamut3/S-Log3 (Sony, 2016); S-Gamut3.Cine default gamut"),
        new("panasonicVLog", "V-Log", "Panasonic", CameraProfileStatus.Uncalibrated, "full", "bt709", PanasonicVLog, VGamut,
            "V-Log/V-Gamut REFERENCE MANUAL Rev. 1.0 (2014-11-28)"),
        new("canonCLog3", "Canon Log 3", "Canon", CameraProfileStatus.Uncalibrated, "full", "bt709", CanonCLog3, CinemaGamut,
            "Description of the Canon Log, Canon Log 2 and Canon Log 3 Gamma Curves (white paper, 2018-11-01); Cinema Gamut; decoded value scaled by 0.9 so grey is 0.18"),
        new("arriLogC3", "LogC3", "ARRI", CameraProfileStatus.Uncalibrated, "legal", "bt709", ArriLogC3, Awg3,
            "ALEXA Log C Curve - Usage in VFX (ARRI, 2017-03-09), EI 800 parameters, ALEXA Wide Gamut"),
        new("arriLogC4", "LogC4", "ARRI", CameraProfileStatus.Uncalibrated, "legal", "bt709", ArriLogC4, Awg4,
            "ARRI LogC4 Logarithmic Color Space Specification (2025-01-23), ARRI Wide Gamut 4"),
        new("fujiFLog", "F-Log", "Fujifilm", CameraProfileStatus.Uncalibrated, "full", "bt2020nc", FujiFLog, Bt2020,
            "F-Log Data Sheet Ver.1.2; F-Gamut = BT.2020 primaries"),
        // F-Log2C files use F-Gamut C instead of BT.2020; that gamut is not a separate id here.
        new("fujiFLog2", "F-Log2", "Fujifilm", CameraProfileStatus.Uncalibrated, "full", "bt2020nc", FujiFLog2, Bt2020,
            "F-Log2 Data Sheet Ver.1.1; F-Gamut = BT.2020 primaries; file range unconfirmed (full assumed like F-Log)"),
        new("nikonNLog", "N-Log", "Nikon", CameraProfileStatus.Uncalibrated, "legal", "bt2020nc", NikonNLog, Bt2020,
            "N-Log Specification Document Version 1.0.0 (2018-09-01); file range unconfirmed"),
        new("redLog3G10", "Log3G10", "RED", CameraProfileStatus.Uncalibrated, "legal", "bt709", RedLog3G10, RedWideGamut,
            "White Paper on REDWideGamutRGB and Log3G10, 915-0187 Rev C (2017-11); file range unconfirmed"),
    ];

    /// <summary>The profile ids, in the canonical order.</summary>
    public static IReadOnlyList<string> Ids { get; } = All.Select(p => p.Id).ToArray();

    /// <summary>Looks a profile up by its exact id.</summary>
    public static bool TryGet(string? id, [NotNullWhen(true)] out CameraProfileDefinition? profile)
    {
        profile = id is null ? null : All.FirstOrDefault(p => p.Id == id);
        return profile is not null;
    }

    /// <summary>True when <paramref name="id"/> names a known profile.</summary>
    public static bool IsKnown(string? id) => TryGet(id, out _);

    private static double AppleLog(double p)
    {
        const double r0 = -0.05641088, rt = 0.01, c = 47.28711236, b = 0.00964052, g = 0.08550479, d = 0.69336945;
        double pt = c * (rt - r0) * (rt - r0);
        if (p >= pt) return Math.Pow(2.0, (p - d) / g) - b;
        if (p >= 0) return Math.Sqrt(p / c) + r0;
        return r0;
    }

    private static double DjiDLog(double x) =>
        x <= 0.14 ? (x - 0.0929) / 6.025 : (Math.Pow(10.0, 3.89616 * x - 2.27752) - 0.0108) / 0.9892;

    private static double SonySLog3(double x) =>
        x >= 171.2102946929 / 1023.0
            ? Math.Pow(10.0, (x * 1023.0 - 420.0) / 261.5) * (0.18 + 0.01) - 0.01
            : (x * 1023.0 - 95.0) * 0.01125000 / (171.2102946929 - 95.0);

    private static double PanasonicVLog(double x) =>
        x < 0.181 ? (x - 0.125) / 5.6 : Math.Pow(10.0, (x - 0.598206) / 0.241514) - 0.00873;

    private static double CanonCLog3(double y)
    {
        double v;
        if (y < 0.097465473) v = -(Math.Pow(10.0, (0.12783901 - y) / 0.36726845) - 1) / 14.98325;
        else if (y <= 0.15277891) v = (y - 0.12512219) / 1.9754798;
        else v = (Math.Pow(10.0, (y - 0.12240537) / 0.36726845) - 1) / 14.98325;
        return 0.9 * v; // Canon's x has 1.0 = 90% white; grey 0.18 convention needs 0.9 * x.
    }

    private static double ArriLogC3(double t)
    {
        const double cut = 0.010591, a = 5.555556, b = 0.052272, c = 0.247190, d = 0.385537, e = 5.367655, f = 0.092809;
        return t > e * cut + f ? (Math.Pow(10.0, (t - d) / c) - b) / a : (t - f) / e;
    }

    private static double ArriLogC4(double e)
    {
        return e >= 0 ? (Math.Pow(2.0, 14.0 * (e - C4C) / C4B + 6.0) - 64.0) / C4A : e * C4S + C4T;
    }

    private static double FujiFLog(double x)
    {
        const double a = 0.555556, b = 0.009468, c = 0.344676, d = 0.790453, e = 8.735631, f = 0.092864, cut2 = 0.100537775223865;
        return x >= cut2 ? Math.Pow(10.0, (x - d) / c) / a - b / a : (x - f) / e;
    }

    private static double FujiFLog2(double x)
    {
        const double a = 5.555556, b = 0.064829, c = 0.245281, d = 0.384316, e = 8.799461, f = 0.092864, cut2 = 0.100686685370811;
        return x >= cut2 ? Math.Pow(10.0, (x - d) / c) / a - b / a : (x - f) / e;
    }

    private static double NikonNLog(double x)
    {
        double code = x * 1023.0;
        return code < 452 ? Math.Pow(code / 650.0, 3.0) - 0.0075 : Math.Exp((code - 619.0) / 150.0);
    }

    private static double RedLog3G10(double y)
    {
        const double a = 0.224282, b = 155.975327, c = 0.01, g = 15.1927;
        return y < 0 ? y / g - c : (Math.Pow(10.0, y / a) - 1.0) / b - c;
    }
}
