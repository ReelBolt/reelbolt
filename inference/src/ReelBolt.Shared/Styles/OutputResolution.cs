using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Styles;

/// <summary>
/// The output resolution a run may choose next to its style and aspect ratio, measured on the SHORT
/// side in pixels (the same side the plan cap <see cref="OrganizationEntitlementSet.MaxOutputHeight"/>
/// is measured on). The set is closed: Auto (stored as null), 720p, 1080p and 2160p (4K).
/// <para>
/// Auto keeps today's output exactly: a run that does not ask for a resolution is capped at
/// <see cref="AutoCeiling"/> even on a plan that may render 4K, so raising a plan's cap never makes an
/// unchanged run bigger (or dearer). See docs/editing-styles.md.
/// </para>
/// </summary>
public static class OutputResolution
{
    /// <summary>The tallest short side a run without a choice renders at (on a capped plan).</summary>
    public const int AutoCeiling = 1080;

    /// <summary>The short sides a run may choose, ascending.</summary>
    public static IReadOnlyList<int> Allowed { get; } = [720, 1080, 2160];

    /// <summary>
    /// Parses a caller's choice (case-insensitive, trimmed). Null, blank and <c>Auto</c> parse to null
    /// (no choice); <c>720p</c>/<c>720</c>, <c>1080p</c>/<c>1080</c>, <c>2160p</c>/<c>2160</c>/<c>4K</c>
    /// to their short side. Anything else is refused.
    /// </summary>
    public static bool TryParse(string? value, out int? shortSide)
    {
        shortSide = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                return true;
            case "720p" or "720":
                shortSide = 720;
                return true;
            case "1080p" or "1080":
                shortSide = 1080;
                return true;
            case "2160p" or "2160" or "4k":
                shortSide = 2160;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The longest part of a rejected value <see cref="UnknownMessage"/> echoes back.</summary>
    public const int MaxEchoedLength = 32;

    /// <summary>
    /// The plain-language refusal for a value <see cref="TryParse"/> rejected. The value is caller
    /// input of any length, so only its first <see cref="MaxEchoedLength"/> characters are echoed,
    /// followed by "…" when it was cut.
    /// </summary>
    public static string UnknownMessage(string? value)
    {
        string echoed = value is { Length: > MaxEchoedLength } ? value[..MaxEchoedLength] + "…" : value ?? string.Empty;
        return $"Unknown output resolution '{echoed}'. Use Auto, 720p, 1080p or 2160p.";
    }

    /// <summary>The wire label of a short side: <c>720p</c>, <c>1080p</c>, <c>2160p</c>.</summary>
    public static string Label(int shortSide) => $"{shortSide}p";

    /// <summary>The label shown to a person: <c>4K (2160p)</c> for 2160, otherwise <see cref="Label"/>.</summary>
    public static string DisplayName(int shortSide) => shortSide == 2160 ? "4K (2160p)" : Label(shortSide);

    /// <summary>
    /// The tallest short side a run may render at under a plan cap of <paramref name="planMaxOutputHeight"/>.
    /// Without a choice: unlimited on an unlimited plan, otherwise the plan cap but never above
    /// <see cref="AutoCeiling"/> (today's output). With a choice: the choice, clamped to the plan (the
    /// API refuses a choice above the plan before queueing; the clamp covers a downgrade after it).
    /// </summary>
    public static int EffectiveMaxHeight(int planMaxOutputHeight, int? chosen)
    {
        if (chosen is { } choice)
            return Math.Min(choice, planMaxOutputHeight);

        return planMaxOutputHeight == OrganizationEntitlementSet.UnlimitedInt
            ? OrganizationEntitlementSet.UnlimitedInt
            : Math.Min(planMaxOutputHeight, AutoCeiling);
    }

    /// <summary>
    /// How many 1080p frames' worth of pixels one frame at <paramref name="chosen"/> is:
    /// <c>(chosen / 1080)^2</c> (720 ≈ 0.444, 1080 = 1, 2160 = 4); 1.0 without a choice.
    /// </summary>
    public static double PixelFactor(int? chosen)
    {
        if (chosen is not { } choice)
            return 1.0;

        double ratio = choice / 1080.0;
        return ratio * ratio;
    }

    /// <summary>
    /// A 1080-class canvas (<paramref name="w1080"/> x <paramref name="h1080"/>) scaled so its short
    /// side becomes <paramref name="shortSide"/>: both sides multiplied by <c>shortSide / 1080</c> and
    /// rounded to the nearest even number (encoders need even sizes).
    /// </summary>
    public static (int W, int H) Scale1080Class(int w1080, int h1080, int shortSide)
    {
        double factor = shortSide / 1080.0;
        return (Even(w1080 * factor), Even(h1080 * factor));
    }

    private static int Even(double value) => (int)Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2;
}
