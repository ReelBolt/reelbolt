using System.Globalization;
using System.Text;
using ReelBolt.Shared.Editing;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// R09.6/R10.5 burn-in: the drawtext decoration, colour, size and 3x3 placement of a caption look and
/// of a title position. Everything is derived from closed catalogues (<c>EditTimelineValidator.CaptionLooks</c>,
/// <c>TextPositions</c>); an unknown value falls back to the clean look / default position, so no
/// document text or value ever reaches a filter string except through the <c>textfile=</c> body.
/// A caption with a null look and a null position keeps the pre-R09.6 bytes exactly.
/// </summary>
public static class CaptionBurnIn
{
    /// <summary>Most lines a caption burns in; the rest is dropped (the sidecar still carries it all).</summary>
    public const int MaxLines = 6;

    /// <summary>True when the caption uses the legacy (look and position both unset) placement.</summary>
    public static bool IsLegacy(string? look, string? position) =>
        (look is null || look == "clean") && position is null;

    /// <summary>Sanitised lines of a caption: each line sanitised on its own, blanks dropped.</summary>
    public static IReadOnlyList<string> Lines(string? raw, int maxCharsPerCaption)
    {
        if (string.IsNullOrEmpty(raw)) return [];
        List<string> lines = [];
        foreach (string part in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string clean = OverlayTextSanitizer.Sanitize(part, maxCharsPerCaption);
            if (clean.Length > 0) lines.Add(clean);
            if (lines.Count == MaxLines) break;
        }

        return lines;
    }

    /// <summary>Greedy word wrap of each line to at most <paramref name="maxChars"/> characters.</summary>
    public static IReadOnlyList<string> Wrap(IReadOnlyList<string> lines, int maxChars)
    {
        maxChars = Math.Max(8, maxChars);
        List<string> wrapped = [];
        foreach (string line in lines)
        {
            if (line.Length <= maxChars) { wrapped.Add(line); continue; }
            StringBuilder current = new();
            foreach (string word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.Length > 0 && current.Length + 1 + word.Length > maxChars)
                {
                    wrapped.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }

            if (current.Length > 0) wrapped.Add(current.ToString());
        }

        return wrapped.Count > MaxLines ? wrapped.GetRange(0, MaxLines) : wrapped;
    }

    /// <summary>Font size, colour and decoration of a caption look (new path only).</summary>
    public static (int FontSize, string Color, string Decoration) Look(string? look, int width, int height)
    {
        int shortSide = Math.Min(width, height);
        double basis = shortSide * 0.045;
        int pad = Math.Max(8, shortSide / 54);
        switch (look)
        {
            case "bold":
                return (Size(basis * 1.25, height), "#FFFFFF", $"borderw={Math.Max(3, shortSide / 160)}:bordercolor=black");
            case "boxed":
                return (Size(basis, height), "#FFFFFF", $"box=1:boxcolor=black@0.6:boxborderw={pad}");
            case "outline":
                return (Size(basis, height), "#FFFFFF", $"borderw={Math.Max(3, shortSide / 200)}:bordercolor=black");
            case "spotlight":
                return (Size(basis, height), "#FFE600", $"borderw={Math.Max(2, shortSide / 270)}:bordercolor=black");
            default:
                return (Size(basis, height), "#FFFFFF", "borderw=2:bordercolor=black@0.6");
        }
    }

    /// <summary>
    /// The drawtext <c>text_align</c>/<c>x</c>/<c>y</c> options of a 3x3 position. Bottom and top
    /// rows keep clear of the safe margin (8 percent of the height, 14 percent on a vertical frame).
    /// </summary>
    public static string Placement(string? position, int width, int height, bool multiline)
    {
        bool vertical = width < height;
        string margin = vertical ? "h*0.14" : "h/12";
        string pos = position ?? "bottomCenter";
        char row = pos.StartsWith("top", StringComparison.Ordinal) ? 't'
            : pos.StartsWith("bottom", StringComparison.Ordinal) ? 'b' : 'm';
        char col = pos.EndsWith("Left", StringComparison.Ordinal) ? 'l'
            : pos.EndsWith("Right", StringComparison.Ordinal) ? 'r' : 'c';
        if (!EditTimelineValidator.TextPositions.Contains(pos)) { row = 'b'; col = 'c'; }

        string x = col switch { 'l' => "x=w/12", 'r' => "x=w-text_w-w/12", _ => "x=(w-text_w)/2" };
        string y = row switch
        {
            't' => $"y={margin}",
            'b' => $"y=h-text_h-{margin}",
            _ => "y=(h-text_h)/2",
        };
        string align = multiline ? $":text_align={(col == 'l' ? 'L' : col == 'r' ? 'R' : 'C')}" : string.Empty;
        return $"{x}:{y}{align}";
    }

    /// <summary>Rough characters that fit across 84 percent of the width at <paramref name="fontSize"/>.</summary>
    public static int CharsPerLine(int width, int fontSize) =>
        (int)Math.Floor(width * 0.84 / Math.Max(1.0, fontSize * 0.56));

    private static int Size(double requested, int height) =>
        (int)Math.Round(Math.Clamp(requested, 8, Math.Max(8, height * 0.5)));

    /// <summary>Appends look/position to a recipe hash only when set, so old recipes keep their hash.</summary>
    public static string HashKey(string? look, string? position) =>
        (look is null ? string.Empty : "look:" + look) + (position is null ? string.Empty : "|pos:" + position);

    internal static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
