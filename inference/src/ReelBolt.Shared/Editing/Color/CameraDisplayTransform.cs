namespace ReelBolt.Shared.Editing.Color;

/// <summary>
/// The neutral display transform from a camera log profile to Rec.709: decode, gamut matrix,
/// optional exposure, per-channel tanh shoulder, Rec.709 OETF. Our own design (not a maker look):
/// contrast-neutral, so it looks flatter than a maker's creative LUT by intent.
/// </summary>
public static class CameraDisplayTransform
{
    /// <summary>Highlight shoulder knee; the curve is the identity below it.</summary>
    public const double Knee = 0.45;

    /// <summary>
    /// Converts a normalised camera code triple (0..1, clamped) to a Rec.709 OETF-encoded full-swing
    /// signal (0..1). Range scaling to legal levels is the renderer's job.
    /// </summary>
    public static (double R, double G, double B) ToRec709(CameraProfileDefinition p, double r, double g, double b, double exposureEv = 0)
    {
        ArgumentNullException.ThrowIfNull(p);
        double lr = p.DecodeToLinear(Math.Clamp(r, 0, 1));
        double lg = p.DecodeToLinear(Math.Clamp(g, 0, 1));
        double lb = p.DecodeToLinear(Math.Clamp(b, 0, 1));
        double[] m = p.GamutToRec709;
        double gain = exposureEv == 0 ? 1.0 : Math.Pow(2.0, exposureEv);
        double outR = Math.Max(0, m[0] * lr + m[1] * lg + m[2] * lb) * gain;
        double outG = Math.Max(0, m[3] * lr + m[4] * lg + m[5] * lb) * gain;
        double outB = Math.Max(0, m[6] * lr + m[7] * lg + m[8] * lb) * gain;
        return (Oetf(Shoulder(outR)), Oetf(Shoulder(outG)), Oetf(Shoulder(outB)));
    }

    private static double Shoulder(double x) =>
        x <= Knee ? x : Knee + (1 - Knee) * Math.Tanh((x - Knee) / (1 - Knee));

    private static double Oetf(double y) =>
        y < 0.018 ? 4.5 * y : 1.099 * Math.Pow(y, 0.45) - 0.099;
}
