namespace ReelBolt.Shared.Editing.Color;

/// <summary>
/// Builds the 3D table (camera code to Rec.709) that the renderer feeds to <c>lut3d</c>.
/// Determinism: all maths is double precision with the same expressions everywhere (decode, matrix,
/// shoulder, OETF through <see cref="CameraDisplayTransform"/>) and a single rounding to float at
/// the end. Math.Pow, Log10 and Tanh come from the platform C runtime, so cross-machine equality
/// rests on that runtime; the pinned SHA-256 per profile in the tests is what detects drift.
/// </summary>
public static class CameraProfileTable
{
    /// <summary>Bumped on any change to the table formula (and so to a pinned SHA); folded into step cache keys that use a profile.</summary>
    public const string TableVersion = "camera-table-v1";

    /// <summary>
    /// Generates <c>size^3 * 3</c> floats, red fastest (cube order). The input domain 0..1 is the
    /// profile's normalised code value on every axis.
    /// </summary>
    public static float[] Generate(string id, int size = 65)
    {
        if (!CameraProfiles.TryGet(id, out CameraProfileDefinition? p))
            throw new ArgumentException($"unknown camera profile '{id}'", nameof(id));
        if (size < 2 || size > 65) throw new ArgumentOutOfRangeException(nameof(size), "size must be in [2, 65]");

        float[] table = new float[size * size * size * 3];
        double step = 1.0 / (size - 1);
        int i = 0;
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)
                {
                    (double outR, double outG, double outB) = CameraDisplayTransform.ToRec709(p, r * step, g * step, b * step);
                    table[i++] = (float)outR;
                    table[i++] = (float)outG;
                    table[i++] = (float)outB;
                }
            }
        }

        return table;
    }
}
