namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Classifies a brand palette as <c>warm</c>, <c>cool</c> or <c>neutral</c> from its colour tokens
/// (SPEC 22). Hand-written, total and pure: it never throws, never touches IO or the network, and a
/// value it does not understand contributes nothing instead of failing.
/// </summary>
public static class BrandTemperature
{
    /// <summary>
    /// The circular, saturation-weighted mean hue of <paramref name="hexes"/> mapped to one of
    /// exactly <c>"warm"</c>, <c>"cool"</c> or <c>"neutral"</c>.
    /// </summary>
    public static string Classify(IReadOnlyList<string> hexes)
    {
        double sumSin = 0;
        double sumCos = 0;
        double totalWeight = 0;

        if (hexes is not null)
        {
            foreach (string hex in hexes)
            {
                if (!TryHsl(hex, out double hue, out double saturation))
                    continue;
                if (saturation < 0.25)
                    continue;

                double radians = hue * Math.PI / 180.0;
                sumSin += saturation * Math.Sin(radians);
                sumCos += saturation * Math.Cos(radians);
                totalWeight += saturation;
            }
        }

        if (totalWeight < 0.3)
            return "neutral";

        double average = Math.Atan2(sumSin, sumCos) * 180.0 / Math.PI;
        average = ((average % 360.0) + 360.0) % 360.0;

        if (average <= 70.0 || average >= 330.0)
            return "warm";
        if (average >= 170.0 && average <= 270.0)
            return "cool";
        return "neutral";
    }

    /// <summary>
    /// Reads <c>#rrggbb</c> (or <c>#rrggbbaa</c>, alpha ignored) and answers its HSL hue in degrees
    /// and saturation in [0,1]. Anything else answers false.
    /// </summary>
    private static bool TryHsl(string? hex, out double hue, out double saturation)
    {
        hue = 0;
        saturation = 0;

        if (hex is null || hex.Length == 0 || hex[0] != '#')
            return false;

        int digits = hex.Length - 1;
        if (digits != 6 && digits != 8)
            return false;

        if (!TryByte(hex, 1, out int r)
            || !TryByte(hex, 3, out int g)
            || !TryByte(hex, 5, out int b))
        {
            return false;
        }

        // An 8-digit value is #rrggbbaa: the alpha byte is validated but never used.
        if (digits == 8 && (!TryByte(hex, 7, out _)))
            return false;

        double red = r / 255.0;
        double green = g / 255.0;
        double blue = b / 255.0;

        double max = Math.Max(red, Math.Max(green, blue));
        double min = Math.Min(red, Math.Min(green, blue));
        double lightness = (max + min) / 2.0;

        if (max == min)
        {
            hue = 0;
            saturation = 0;
            return true;
        }

        double delta = max - min;
        saturation = delta / (1.0 - Math.Abs(2.0 * lightness - 1.0));

        double raw;
        if (max == red)
            raw = 60.0 * (((green - blue) / delta) % 6.0);
        else if (max == green)
            raw = 60.0 * ((blue - red) / delta + 2.0);
        else
            raw = 60.0 * ((red - green) / delta + 4.0);

        hue = ((raw % 360.0) + 360.0) % 360.0;
        return true;
    }

    private static bool TryByte(string hex, int index, out int value)
    {
        value = 0;
        if (index + 1 >= hex.Length)
            return false;

        int high = HexDigit(hex[index]);
        int low = HexDigit(hex[index + 1]);
        if (high < 0 || low < 0)
            return false;

        value = (high << 4) | low;
        return true;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
