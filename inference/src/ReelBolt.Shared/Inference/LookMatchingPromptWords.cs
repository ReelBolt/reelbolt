using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Look matching (issue #93 phase 4): turns the MEASURED colour words of a real shot — the
/// Phase-4 <see cref="VideoAnalysisShotVisual"/> descriptors a <c>VideoAnalyze</c> step computed —
/// into a first-party English clause appended to a planned shot's generation prompt, so the
/// generated clip lands close to the program's grade before grading. A pure table: the model never
/// produces these words and never sees this code's output as something to edit — the clause is
/// appended server-side to the planner's prompt before the shared sanitizer runs over the whole.
/// </summary>
/// <remarks>
/// Deliberately conservative. Only the three CLASSIFIED words the analyzer emits
/// (<c>ColorTemperatureClass</c>: Warm/Cool/Neutral; <c>ToneClass</c>: Blown/Crushed/Flat/
/// Contrasty/Normal; <c>SaturationClass</c>: Muted/Vivid/Natural — see <c>FrameGridAnalyzer</c>)
/// are consulted, never the raw <c>Warmth</c>/<c>BlackPoint</c>/<c>SaturationMean</c> numbers:
/// a number in a prompt is exactly what this pipeline refuses to emit, and the classes already
/// carry the editorial meaning. A shot the analyzer could not classify (every class null) yields
/// <c>null</c> — no clause, prompt byte-identical to the unmatched case.
/// </remarks>
public static class LookMatchingPromptWords
{
    private static readonly IReadOnlyDictionary<string, string> TemperaturePhrases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Warm"] = "warm colour temperature with amber highlights",
            ["Cool"] = "cool colour temperature with blue-leaning shadows",
            ["Neutral"] = "neutral colour temperature",
        };

    private static readonly IReadOnlyDictionary<string, string> TonePhrases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contrasty"] = "high contrast with deep blacks",
            ["Flat"] = "soft, low-contrast, log-like tonality",
            ["Crushed"] = "low-key lighting with dense shadows",
            ["Blown"] = "bright, high-key lighting with open highlights",
            ["Normal"] = "balanced exposure",
        };

    private static readonly IReadOnlyDictionary<string, string> SaturationPhrases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Muted"] = "muted, desaturated colours",
            ["Vivid"] = "vivid, saturated colours",
            ["Natural"] = "natural saturation",
        };

    /// <summary>The fixed lead-in every clause starts with — pinned so tests can assert "the clause is present" without restating the table.</summary>
    public const string ClausePrefix = "Match the look of the surrounding footage: ";

    /// <summary>
    /// The clause for one measured shot, or <c>null</c> when the shot carries no classified word.
    /// </summary>
    public static string? Describe(VideoAnalysisShotVisual? visual)
    {
        if (visual is null)
        {
            return null;
        }

        var parts = new List<string>(3);
        if (visual.ColorTemperatureClass is { } temperature && TemperaturePhrases.TryGetValue(temperature.Trim(), out string? t))
        {
            parts.Add(t);
        }

        if (visual.ToneClass is { } tone && TonePhrases.TryGetValue(tone.Trim(), out string? tn))
        {
            parts.Add(tn);
        }

        if (visual.SaturationClass is { } saturation && SaturationPhrases.TryGetValue(saturation.Trim(), out string? sat))
        {
            parts.Add(sat);
        }

        if (parts.Count == 0)
        {
            return null;
        }

        return ClausePrefix + string.Join(", ", parts) + ".";
    }

    /// <summary>
    /// Camera-move continuity: the planner camera word that continues a real shot's classified
    /// move, or <c>null</c> when the analyzer's class carries no direction. The analyzer emits
    /// <c>Static</c>, <c>Handheld</c>, <c>Pan</c>, <c>Tilt</c> and <c>Zoom</c>; only the first
    /// two map onto <see cref="ShotPlanParameterMapper.CameraWords"/> without inventing a direction
    /// (a <c>Pan</c> could be <c>PanLeft</c> or <c>PanRight</c>, and guessing would put a camera
    /// move into the clip nobody measured).
    /// </summary>
    public static string? ContinuityCameraWord(VideoAnalysisShotVisual? visual) =>
        visual?.CameraMove?.Trim() switch
        {
            { } m when m.Equals("Static", StringComparison.OrdinalIgnoreCase) => "Static",
            { } m when m.Equals("Handheld", StringComparison.OrdinalIgnoreCase) => "Handheld",
            _ => null,
        };
}
