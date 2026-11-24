using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One caption typeface: the family name libass selects, the bundled file that must exist for it, and a conservative average glyph width used to keep a cue inside the title-safe width.</summary>
/// <param name="Font">The config member this face answers.</param>
/// <param name="Family">The family name written into the ASS style — what libass matches against the fonts it can see.</param>
/// <param name="FileName">The file in <c>VideoEditing:CaptionFontsDir</c> that carries this face; <c>null</c> for the image's system DejaVu Sans, which fontconfig resolves.</param>
/// <param name="Bold">Whether the style asks for bold. False for single-weight display faces, so libass never synthesizes a smeared fake bold over them.</param>
/// <param name="AvgCharWidthEm">Average advance of a mixed-case character, in ems (font size units), rounded UP so a width estimate errs on the side of fitting.</param>
public sealed record CaptionFontFace(CaptionFont Font, string Family, string? FileName, bool Bold, double AvgCharWidthEm);

/// <summary>
/// The closed set of caption fonts (see docs/voiceover.md "Captions"). Every face but DejaVu Sans
/// is a file vendored in <c>inference/fonts/captions</c> and copied into the image; a face whose
/// file is missing at run time resolves to DejaVu Sans with a reason, never to whatever fontconfig
/// happens to substitute for an unknown family.
/// </summary>
public static class CaptionFontCatalog
{
    public const string FontNotInstalledReason = "font_not_installed";

    private static readonly CaptionFontFace Inter = new(CaptionFont.Inter, "Inter", "Inter-Bold.ttf", true, 0.62);
    private static readonly CaptionFontFace Montserrat = new(CaptionFont.Montserrat, "Montserrat", "Montserrat-Bold.ttf", true, 0.68);
    private static readonly CaptionFontFace Poppins = new(CaptionFont.Poppins, "Poppins", "Poppins-Bold.ttf", true, 0.68);
    private static readonly CaptionFontFace BebasNeue = new(CaptionFont.BebasNeue, "Bebas Neue", "BebasNeue-Regular.ttf", false, 0.42);
    private static readonly CaptionFontFace Anton = new(CaptionFont.Anton, "Anton", "Anton-Regular.ttf", false, 0.50);
    private static readonly CaptionFontFace DejaVuSans = new(CaptionFont.DejaVuSans, "DejaVu Sans", null, true, 0.70);

    /// <summary>The font a style uses when <see cref="CaptionFont.Auto"/> is configured.</summary>
    public static CaptionFont ForStyle(CaptionStyle style) => style switch
    {
        CaptionStyle.Karaoke => CaptionFont.Montserrat,
        CaptionStyle.Punch => CaptionFont.Anton,
        CaptionStyle.Bold => CaptionFont.Poppins,
        _ => CaptionFont.Inter
    };

    /// <summary>The face for a concrete (non-Auto) font. An unrecognised value is DejaVu Sans, the one face that is always present.</summary>
    public static CaptionFontFace Get(CaptionFont font) => font switch
    {
        CaptionFont.Inter => Inter,
        CaptionFont.Montserrat => Montserrat,
        CaptionFont.Poppins => Poppins,
        CaptionFont.BebasNeue => BebasNeue,
        CaptionFont.Anton => Anton,
        _ => DejaVuSans
    };

    /// <summary>
    /// Resolves the configured font for a style against the fonts actually installed in
    /// <paramref name="fontsDir"/>. Returns DejaVu Sans plus <see cref="FontNotInstalledReason"/>
    /// when the face's file is absent (or no fonts directory exists at all).
    /// </summary>
    public static (CaptionFontFace Face, string? FallbackReason) Resolve(CaptionFont requested, CaptionStyle style, string? fontsDir)
    {
        CaptionFont concrete = requested == CaptionFont.Auto ? ForStyle(style) : requested;
        CaptionFontFace face = Get(concrete);
        if (face.FileName is null)
            return (face, null);

        bool installed = !string.IsNullOrWhiteSpace(fontsDir) && File.Exists(Path.Combine(fontsDir, face.FileName));
        return installed ? (face, null) : (DejaVuSans, FontNotInstalledReason);
    }
}

/// <summary>
/// A caption colour from the allowlisted config vocabulary: <c>white</c>, <c>black</c>,
/// <c>yellow</c> or <c>#RRGGBB</c>, each optionally followed by <c>@opacity</c> (0..1, at most
/// three decimals) — e.g. <c>black@0.6</c>. Anything else does not parse, so no config string can
/// reach the ASS file or the drawtext filter string except through this type.
/// </summary>
public readonly record struct CaptionColor(byte R, byte G, byte B, double Opacity)
{
    private static readonly Regex Pattern = new(
        @"^(?<base>white|black|yellow|#[0-9A-Fa-f]{6})(?:@(?<a>0(?:\.[0-9]{1,3})?|1(?:\.0{1,3})?))?\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryParse(string? value, out CaptionColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        Match m = Pattern.Match(value);
        if (!m.Success)
            return false;

        string b = m.Groups["base"].Value.ToLowerInvariant();
        (byte r, byte g, byte bl) = b switch
        {
            "white" => ((byte)255, (byte)255, (byte)255),
            "black" => ((byte)0, (byte)0, (byte)0),
            "yellow" => ((byte)255, (byte)255, (byte)0),
            _ => (
                byte.Parse(b.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(b.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(b.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
        };
        double opacity = m.Groups["a"].Success
            ? double.Parse(m.Groups["a"].Value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 1.0;
        color = new CaptionColor(r, g, bl, Math.Clamp(opacity, 0, 1));
        return true;
    }

    /// <summary>The ASS colour literal <c>&amp;HAABBGGRR</c> — alpha 00 is opaque, FF transparent.</summary>
    public string ToAss()
    {
        int alpha = (int)Math.Round((1 - Opacity) * 255, MidpointRounding.AwayFromZero);
        return $"&H{alpha:X2}{B:X2}{G:X2}{R:X2}";
    }

    public static readonly CaptionColor OpaqueBlack = new(0, 0, 0, 1);
    public static readonly CaptionColor HalfBlack = new(0, 0, 0, 0.5);
}

/// <summary>Where and how large captions are drawn on a given canvas — every number derived in C# from the canvas size, the style and the two config knobs.</summary>
public sealed record CaptionLayout(
    int PlayResX,
    int PlayResY,
    int FontSizePx,
    bool AutoSize,
    CaptionPosition Position,
    int Alignment,
    int MarginL,
    int MarginR,
    int MarginV,
    bool Portrait,
    /// <summary>The title-safe top margin, used by cues moved to the top of a bottom-placed layout.</summary>
    int TopMarginV = 0)
{
    /// <summary>The width a caption line may occupy: the canvas less both side margins.</summary>
    public int SafeWidth => Math.Max(16, PlayResX - MarginL - MarginR);

    /// <summary>
    /// <c>CaptionFontSizePct</c> values meaning "automatic": 0, and 4 — the default every config
    /// saved before the automatic size existed carries, which rendered ~4%-of-height captions that
    /// were too small to read on a phone.
    /// </summary>
    public static bool IsAutoSize(int fontSizePct) => fontSizePct is 0 or 4;

    /// <summary>
    /// Resolves size, alignment and title-safe margins. Portrait means taller than wide. Automatic
    /// size is a percentage of the canvas's SHORT side, per style and orientation (see the table in
    /// docs/voiceover.md "Captions"); an explicit size keeps its historical meaning, a percentage of
    /// the frame height (clamped 2..12, Punch doubled within the clamp).
    /// </summary>
    public static CaptionLayout Resolve(int width, int height, CaptionStyle style, int fontSizePct, CaptionPosition position)
    {
        int w = Math.Max(16, width);
        int h = Math.Max(16, height);
        bool portrait = h > w;
        bool auto = IsAutoSize(fontSizePct);

        int fontSize;
        if (auto)
        {
            double pct = (style, portrait) switch
            {
                (CaptionStyle.Punch, true) => 13.0,
                (CaptionStyle.Punch, false) => 11.0,
                (CaptionStyle.Bold, true) => 14.0,
                (CaptionStyle.Bold, false) => 10.5,
                (CaptionStyle.Karaoke, true) => 8.0,
                (CaptionStyle.Karaoke, false) => 6.5,
                (_, true) => 6.5,
                _ => 5.5
            };
            int shortSide = Math.Min(w, h);
            fontSize = Math.Max(12, (int)Math.Round(shortSide * pct / 100.0, MidpointRounding.AwayFromZero));
        }
        else
        {
            fontSize = CaptionFilterBuilder.ComputeFontSize(h, fontSizePct, style);
        }

        CaptionPosition effective = position switch
        {
            CaptionPosition.Bottom or CaptionPosition.Center or CaptionPosition.Top => position,
            _ => style == CaptionStyle.Punch ? CaptionPosition.Center : CaptionPosition.Bottom
        };

        // Title-safe margins. A portrait canvas is what a phone shows full screen, where the
        // platform's own caption, buttons and progress bar cover roughly the bottom fifth and its
        // header the top tenth, so captions keep clear of both.
        int side = (int)Math.Round(w * 0.08, MidpointRounding.AwayFromZero);
        // Bold sits higher on a phone: Reels/TikTok draw the caption, handle and buttons over the
        // bottom quarter, so a social caption reads best around two-thirds of the way down.
        double bottomPct = style == CaptionStyle.Bold ? (portrait ? 0.28 : 0.10) : portrait ? 0.20 : 0.08;
        int bottom = (int)Math.Round(h * bottomPct, MidpointRounding.AwayFromZero);
        int top = (int)Math.Round(h * (portrait ? 0.12 : 0.08), MidpointRounding.AwayFromZero);

        (int alignment, int marginV) = effective switch
        {
            CaptionPosition.Top => (8, top),
            CaptionPosition.Center => (5, 0),
            _ => (2, bottom)
        };

        return new CaptionLayout(w, h, fontSize, auto, effective, alignment, side, side, marginV, portrait, top);
    }
}

/// <summary>
/// The caption-side text allowlist for the libass path. The overlay sanitizer's set plus the
/// punctuation narration actually contains — commas, apostrophes, quotes, colons, semicolons and
/// an ellipsis — which that sanitizer strips ("don't" became "dont"). Safe here because the text
/// reaches libass only inside an ASS file, never the filter string, and every override-syntax
/// character is neutralized again by <see cref="AssSubtitleBuilder.EscapeText"/>.
/// </summary>
public static class CaptionTextSanitizer
{
    private static readonly Regex Disallowed = new(
        @"[^\p{L}\p{N}\p{M} .,!?:;'""’‘“”…()\-—/&#@+]", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Sanitize(string? raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string normalized = raw.Normalize(NormalizationForm.FormC);
        string spaced = Whitespace.Replace(normalized, " ").Trim();
        string allowed = Disallowed.Replace(spaced, string.Empty);
        string collapsed = Whitespace.Replace(allowed, " ").Trim();
        if (collapsed.Length == 0)
            return string.Empty;

        return ReelBolt.Shared.Editing.OverlayTextSanitizer.TruncateByTextElements(collapsed, Math.Max(0, maxChars)).Trim();
    }
}

/// <summary>
/// Writes the ASS subtitle document libass burns (see docs/voiceover.md "Captions"). Pure — no
/// I/O. Every number in it is computed here from already-resolved cues (output-timeline seconds
/// the C# cue builder produced, never a model-supplied value) and a <see cref="CaptionLayout"/>;
/// the only free text is each cue's spoken words, which go through <see cref="EscapeText"/> so they
/// can never open an override block, force a line break or start a new event line.
/// </summary>
public static class AssSubtitleBuilder
{
    public const string StyleName = "Caption";

    /// <summary>The scratch file name of the generated script — the single source of truth shared with the executor's writer.</summary>
    public const string ScriptFileName = "captions.ass";

    /// <summary>Most lines a Subtitle/Karaoke cue may wrap to before its size is reduced; Punch is always one line.</summary>
    internal const int MaxLinesPerCue = 2;

    /// <summary>The smallest a per-cue size reduction goes, as a fraction of the style size — a word longer than this still fits is left to wrap rather than shrink to nothing.</summary>
    internal const double MinShrink = 0.55;

    public static string Build(
        IReadOnlyList<ResolvedCaptionCue> cues,
        CaptionStyle style,
        CaptionLayout layout,
        CaptionFontFace face,
        CaptionColor fontColor,
        CaptionColor? boxColor,
        CaptionColor highlightColor)
    {
        int size = layout.FontSizePx;
        bool box = style is not (CaptionStyle.Punch or CaptionStyle.Bold) && boxColor is not null;

        // Karaoke: words not yet spoken are the SecondaryColour, spoken ones flip to the
        // PrimaryColour as their \k segment begins — so Primary is the highlight.
        CaptionColor primary = style == CaptionStyle.Karaoke ? highlightColor : fontColor;
        CaptionColor secondary = fontColor;

        int borderStyle;
        int outline;
        int shadow;
        CaptionColor outlineColor;
        CaptionColor backColor;
        if (box)
        {
            // BorderStyle 3 = opaque box; libass fills it with the OutlineColour and pads the
            // text by Outline pixels.
            borderStyle = 3;
            outline = Math.Max(4, Round(size * 0.22));
            shadow = 0;
            outlineColor = boxColor!.Value;
            backColor = boxColor!.Value;
        }
        else
        {
            borderStyle = 1;
            outline = style is CaptionStyle.Punch or CaptionStyle.Bold ? Math.Max(3, Round(size * 0.09)) : Math.Max(2, Round(size * 0.07));
            shadow = style is CaptionStyle.Punch or CaptionStyle.Bold ? Math.Max(2, Round(size * 0.05)) : Math.Max(1, Round(size * 0.04));
            outlineColor = CaptionColor.OpaqueBlack;
            backColor = CaptionColor.HalfBlack;
        }

        StringBuilder sb = new();
        sb.Append("[Script Info]\n");
        sb.Append("; Burned captions generated by ReelBolt (docs/voiceover.md \"Captions\")\n");
        sb.Append("ScriptType: v4.00+\n");
        sb.Append(Invariant($"PlayResX: {layout.PlayResX}\n"));
        sb.Append(Invariant($"PlayResY: {layout.PlayResY}\n"));
        sb.Append("WrapStyle: 0\n");
        sb.Append("ScaledBorderAndShadow: yes\n");
        sb.Append("YCbCr Matrix: None\n");
        sb.Append('\n');
        sb.Append("[V4+ Styles]\n");
        sb.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        sb.Append(Invariant($"Style: {StyleName},{face.Family},{size},{primary.ToAss()},{secondary.ToAss()},{outlineColor.ToAss()},{backColor.ToAss()},"))
            .Append(Invariant($"{(face.Bold ? -1 : 0)},0,0,0,100,100,0,0,{borderStyle},{outline},{shadow},{layout.Alignment},"))
            .Append(Invariant($"{layout.MarginL},{layout.MarginR},{layout.MarginV},1\n"));
        sb.Append('\n');
        sb.Append("[Events]\n");
        sb.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");

        int maxLines = style == CaptionStyle.Punch ? 1 : MaxLinesPerCue;
        // Outline/box padding eats into the line on both sides.
        double safeWidth = Math.Max(16, layout.SafeWidth - 2 * outline);

        foreach (CaptionEvent ev in ToEvents(cues, style))
        {
            int? fitted = FitSize(ev.PlainText, size, face, safeWidth, maxLines);
            string prefix = fitted is int fs ? Invariant($"{{\\fs{fs}}}") : string.Empty;
            // A cue moved off a bottom layout: top-centre alignment and the top title-safe margin,
            // as event overrides so every other cue keeps the style exactly as before.
            bool moveUp = ev.AtTop && layout.Position == CaptionPosition.Bottom;
            int marginV = moveUp ? layout.TopMarginV : 0;
            if (moveUp)
                prefix = "{\\an8}" + prefix;
            sb.Append(Invariant(
                $"Dialogue: 0,{FormatTime(ev.StartSec)},{FormatTime(Math.Max(ev.EndSec, ev.StartSec + 0.01))},{StyleName},,0,0,{marginV},,{prefix}{ev.Text}\n"));
        }

        return sb.ToString();
    }

    /// <summary>One ASS event: an escaped text (with karaoke tags for Karaoke) and its output-timeline window.</summary>
    internal sealed record CaptionEvent(double StartSec, double EndSec, string Text, string PlainText, bool AtTop = false);

    /// <summary>
    /// Subtitle and Punch cues map 1:1 onto events. Karaoke cues come from the cue builder one per
    /// WORD (the drawtext renderer draws a growing highlighted prefix); here each run of word cues
    /// sharing a chunk is folded back into ONE event whose words carry <c>\k</c> durations equal to
    /// the gaps between their measured starts, so libass flips each word to the highlight colour
    /// exactly when it is spoken.
    /// </summary>
    internal static IEnumerable<CaptionEvent> ToEvents(IReadOnlyList<ResolvedCaptionCue> cues, CaptionStyle style)
    {
        if (style != CaptionStyle.Karaoke)
        {
            foreach (ResolvedCaptionCue cue in cues)
                yield return new CaptionEvent(cue.OutputStartSec, cue.OutputEndSec, EscapeText(cue.Text), cue.Text, cue.AtTop);
            yield break;
        }

        List<ResolvedCaptionCue> group = [];
        foreach (ResolvedCaptionCue cue in cues)
        {
            if (group.Count > 0)
            {
                ResolvedCaptionCue last = group[^1];
                bool sameChunk = cue.LineId == last.LineId &&
                    string.Equals(cue.Text, last.Text, StringComparison.Ordinal) &&
                    PrefixCount(cue) > PrefixCount(last);
                if (!sameChunk)
                {
                    yield return KaraokeEvent(group);
                    group = [];
                }
            }

            group.Add(cue);
        }

        if (group.Count > 0)
            yield return KaraokeEvent(group);
    }

    private static CaptionEvent KaraokeEvent(List<ResolvedCaptionCue> group)
    {
        string[] tokens = CaptionCueBuilder.Tokenize(group[0].Text);
        double start = group[0].OutputStartSec;
        double end = Math.Max(group[^1].OutputEndSec, start + 0.01);
        long totalCs = Centis(end) - Centis(start);

        // Word i begins at the start of the cue whose highlighted prefix is i+1 words long. A word
        // with no cue (the cue cap truncated the chunk) begins at the event's end.
        var wordStartCs = new long[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            ResolvedCaptionCue? cue = group.FirstOrDefault(c => PrefixCount(c) == i + 1);
            long t = cue is null ? totalCs : Centis(cue.OutputStartSec) - Centis(start);
            t = Math.Clamp(t, 0, totalCs);
            wordStartCs[i] = i > 0 ? Math.Max(t, wordStartCs[i - 1]) : 0;
        }

        StringBuilder text = new();
        for (int i = 0; i < tokens.Length; i++)
        {
            long next = i + 1 < tokens.Length ? wordStartCs[i + 1] : totalCs;
            long k = Math.Max(0, next - wordStartCs[i]);
            text.Append(Invariant($"{{\\k{k}}}"));
            text.Append(EscapeText(tokens[i]));
            if (i + 1 < tokens.Length)
                text.Append(' ');
        }

        return new CaptionEvent(start, end, text.ToString(), group[0].Text, group[0].AtTop);
    }

    private static int PrefixCount(ResolvedCaptionCue cue) =>
        string.IsNullOrEmpty(cue.HighlightPrefix) ? 0 : CaptionCueBuilder.Tokenize(cue.HighlightPrefix).Length;

    /// <summary>
    /// A smaller size for one event when its estimated width would need more than
    /// <paramref name="maxLines"/> lines inside the safe width; <c>null</c> when it fits at the
    /// style size. Punch's single long word is the case this exists for — libass wraps only at
    /// spaces, so an unbreakable word would otherwise run off both edges of a 9:16 frame.
    /// </summary>
    internal static int? FitSize(string plainText, int size, CaptionFontFace face, double safeWidth, int maxLines)
    {
        double estimated = plainText.Length * face.AvgCharWidthEm * size;
        double budget = safeWidth * maxLines * (maxLines > 1 ? 0.9 : 1.0);
        if (estimated <= budget || estimated <= 0)
            return null;

        int fitted = (int)Math.Floor(size * budget / estimated);
        return Math.Max(Math.Max(12, (int)Math.Ceiling(size * MinShrink)), fitted);
    }

    /// <summary>
    /// Makes spoken text inert as ASS. libass reads <c>{</c> as the start of an override block and
    /// <c>\N</c>/<c>\n</c>/<c>\h</c> as a break or hard space anywhere in event text, and a raw line
    /// break would end the event line and let the rest be parsed as a new script line. So: braces
    /// become parentheses, a word joiner (U+2060, rendered as nothing) follows every backslash so
    /// no escape sequence can form, and every control character becomes a space.
    /// </summary>
    public static string EscapeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        StringBuilder sb = new(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '{':
                    sb.Append('(');
                    break;
                case '}':
                    sb.Append(')');
                    break;
                case '\\':
                    sb.Append('\\').Append('\u2060');
                    break;
                default:
                    if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
                        sb.Append(' ');
                    else
                        sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>ASS time, <c>H:MM:SS.cc</c>, rounded to the nearest centisecond.</summary>
    public static string FormatTime(double seconds)
    {
        long cs = Centis(seconds);
        long h = cs / 360000;
        long m = cs / 6000 % 60;
        long s = cs / 100 % 60;
        long c = cs % 100;
        return Invariant($"{h}:{m:00}:{s:00}.{c:00}");
    }

    private static long Centis(double seconds) =>
        (long)Math.Round(Math.Max(0, double.IsFinite(seconds) ? seconds : 0) * 100, MidpointRounding.AwayFromZero);

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
