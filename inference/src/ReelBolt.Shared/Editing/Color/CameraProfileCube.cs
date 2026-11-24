using System.Globalization;
using System.Text;

namespace ReelBolt.Shared.Editing.Color;

/// <summary>Writes a camera profile's table as an Adobe/IRIDAS <c>.cube</c> text.</summary>
public static class CameraProfileCube
{
    /// <summary>
    /// Returns the <c>.cube</c> text: <c>LUT_3D_SIZE</c>, domain 0..1, then <c>size^3</c> rows with
    /// invariant-culture "F6" numbers and LF line endings.
    /// </summary>
    public static string Write(string id, int size = 65)
    {
        float[] table = CameraProfileTable.Generate(id, size);
        CameraProfileDefinition p = CameraProfiles.TryGet(id, out CameraProfileDefinition? d) ? d : throw new ArgumentException($"unknown camera profile '{id}'", nameof(id));
        StringBuilder sb = new();
        sb.Append("TITLE \"ReelBolt ").Append(p.DisplayName).Append(" to Rec.709 (neutral)\"\n");
        sb.Append("LUT_3D_SIZE ").Append(size.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("DOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n");
        for (int i = 0; i < table.Length; i += 3)
        {
            sb.Append(((double)table[i]).ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
              .Append(((double)table[i + 1]).ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
              .Append(((double)table[i + 2]).ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
        }

        return sb.ToString();
    }
}
