using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The scene structure of a Remotion composition, read DETERMINISTICALLY out of the composition's
/// own source: its <c>&lt;Sequence&gt;</c> list, each sequence's <c>from</c> and
/// <c>durationInFrames</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the "we wrote the code that renders it, so we know its scenes without looking at
/// pixels" arm of the authored-scene design (docs/video-editing.md "Author-declared scenes"). It
/// exists because the author's declaration at render time is a CLAIM — a model re-typing numbers it
/// wrote a moment ago in a different notation — while this is a reading of the code that actually
/// runs. The declaration supplies what the scenes MEAN; this supplies where they ARE.
/// </para>
/// <para>
/// <b>Fail-closed, never a best guess.</b> A wrong split is worse than no split: an editor that
/// cuts on a fabricated boundary lands mid-animation. So <see cref="Read"/> answers
/// <see cref="Result.Resolved"/> only for a composition whose sequence tree it can read
/// <i>completely and unambiguously</i> — a flat list of sequences with literal integer timing —
/// and otherwise returns an explicit unresolved result carrying its reasons, which every caller
/// treats as "no structural split". It never returns a partial list.
/// </para>
/// <para>
/// Deliberately NOT a TSX parser, in the same spirit as <c>SandboxFileOutline</c>: a small regex
/// scanner over source with comments, template literals and quoted strings blanked out. Unlike an
/// outline, a miss here is not cheap, so every ambiguity is a refusal rather than an omission.
/// </para>
/// </remarks>
public static class RemotionSceneStructure
{
    /// <summary>One <c>&lt;Sequence&gt;</c>: where it starts and how long it holds, in frames.</summary>
    public sealed record Span(int FromFrame, int DurationInFrames)
    {
        /// <summary>One past the last frame this sequence occupies.</summary>
        public int EndFrame => FromFrame + DurationInFrames;
    }

    /// <summary>
    /// The outcome of reading a composition's source. <see cref="Reasons"/> is non-empty whenever
    /// <see cref="Resolved"/> is false and names every problem found — never only the first — so an
    /// author can fix the composition in one go.
    /// </summary>
    public sealed record Result(
        bool Resolved,
        IReadOnlyList<Span> Spans,
        int? Fps,
        int? DurationInFrames,
        IReadOnlyList<string> Reasons)
    {
        public static Result Unresolved(params string[] reasons) =>
            new(false, [], null, null, reasons);

        public static Result Unresolved(IReadOnlyList<string> reasons) =>
            new(false, [], null, null, reasons);
    }

    /// <summary>
    /// A composition needs at least two sequences to be worth splitting. One sequence says the film
    /// is a single continuous scene, which is exactly the case where pixel-based shot detection may
    /// still find real cuts this reading cannot see — so a one-sequence composition stays
    /// unresolved and the existing path runs unchanged.
    /// </summary>
    public const int MinScenes = 2;

    private const char Backtick = (char)96;

    private static readonly Regex CompositionTagRegex = new(
        @"<Composition\b[^>]*", RegexOptions.Compiled);

    private static readonly Regex CompositionIdRegex = new(
        """<Composition\b[^>]*?\bid\s*=\s*["']([^"']*)["']""", RegexOptions.Compiled);

    private static readonly Regex FpsLiteralRegex = new(
        @"\bfps\s*=\s*\{\s*(\d+)\s*\}", RegexOptions.Compiled);

    private static readonly Regex DurationLiteralRegex = new(
        @"\bdurationInFrames\s*=\s*\{\s*(\d+)\s*\}", RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        @"<Sequence\b[^>]*?/?>|</Sequence\s*>", RegexOptions.Compiled);

    private static readonly Regex FromLiteralRegex = new(
        @"\bfrom\s*=\s*\{\s*(\d+)\s*\}", RegexOptions.Compiled);

    private static readonly Regex SeriesRegex = new(
        @"<Series\b", RegexOptions.Compiled);

    private const int MaxReasons = 12;
    private const int MaxReasonChars = 160;

    /// <summary>
    /// Read <paramref name="source"/> as a composition's scene structure.
    /// </summary>
    /// <param name="source">
    /// The TSX source of the module that registers the composition (the sandbox's <c>src/root.tsx</c>).
    /// </param>
    /// <param name="compositionId">
    /// The composition the render targets. When given, the file must register exactly that one
    /// composition — a module registering several would mix their sequences into one flat list.
    /// </param>
    public static Result Read(string? source, string? compositionId = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Result.Unresolved("the composition source is empty, so its sequences cannot be read.");

        string code = BlankNonCode(source);

        if (SeriesRegex.IsMatch(code))
        {
            return Result.Unresolved(
                "the composition lays its scenes out with <Series>, whose timing this reader does not model; " +
                "declare the scenes at render time instead.");
        }

        MatchCollection compositionTags = CompositionTagRegex.Matches(code);
        if (compositionTags.Count == 0)
        {
            return Result.Unresolved(
                "the source registers no <Composition>, so there is no composition to read.");
        }

        if (compositionTags.Count > 1)
        {
            return Result.Unresolved(
                $"{compositionTags.Count} <Composition> registrations share this source, so their sequences cannot be told apart; " +
                "register one composition per module, or declare the scenes at render time.");
        }

        string compositionTag = compositionTags[0].Value;
        string? registeredId = CompositionIdRegex.Match(source) is { Success: true } idMatch
            ? idMatch.Groups[1].Value
            : null;

        if (!string.IsNullOrWhiteSpace(compositionId) &&
            !string.Equals(registeredId, compositionId, StringComparison.Ordinal))
        {
            return Result.Unresolved(
                $"this source registers composition '{registeredId ?? "(none)"}', not '{compositionId}'.");
        }

        // Both literals are optional: an fps or a duration held in a constant is simply not read,
        // which narrows what this result can be CHECKED against — it never makes the split wrong.
        int? fps = LiteralInt(FpsLiteralRegex, compositionTag);
        int? durationInFrames = LiteralInt(DurationLiteralRegex, compositionTag);

        List<string> reasons = [];
        List<Span> spans = [];
        int depth = 0;

        foreach (Match tag in TagRegex.Matches(code))
        {
            string text = tag.Value;

            if (text.StartsWith("</", StringComparison.Ordinal))
            {
                if (depth == 0)
                    reasons.Add("a </Sequence> closes a <Sequence> that was never opened.");
                else
                    depth--;

                if (reasons.Count > MaxReasons)
                    break;
                continue;
            }

            if (depth > 0)
            {
                return Result.Unresolved(
                    "a <Sequence> sits inside another <Sequence>, so its timing is relative to its parent and cannot be " +
                    "read as a top-level scene.");
            }

            // The element opens HERE, before its own timing is judged: an opening tag whose timing
            // cannot be read still has a matching close, and losing that pair would report the close
            // as unbalanced and hide the real problem.
            if (!text.EndsWith("/>", StringComparison.Ordinal))
                depth++;

            int? from = LiteralInt(FromLiteralRegex, text);
            int? duration = LiteralInt(DurationLiteralRegex, text);

            if (from is null || duration is null)
            {
                reasons.Add(
                    $"a <Sequence> does not give a literal from={{n}} and durationInFrames={{n}} ({Clip(text)}), " +
                    "so its scene boundary cannot be read from the code.");
                if (reasons.Count > MaxReasons)
                    break;
                continue;
            }

            if (duration.Value <= 0)
            {
                reasons.Add($"a <Sequence> at frame {from.Value} lasts {duration.Value} frames, which is not a scene.");
                if (reasons.Count > MaxReasons)
                    break;
                continue;
            }

            spans.Add(new Span(from.Value, duration.Value));
        }

        if (reasons.Count > 0)
            return Result.Unresolved(reasons);

        if (depth != 0)
            return Result.Unresolved($"{depth} <Sequence> element(s) are never closed.");

        if (spans.Count < MinScenes)
        {
            return Result.Unresolved(
                $"the composition places {spans.Count} top-level <Sequence>(s); {MinScenes} are needed for the code to describe a split.");
        }

        if (spans.Count > CompositionSceneManifest.MaxScenes)
        {
            return Result.Unresolved(
                $"the composition places {spans.Count} sequences, more than the {CompositionSceneManifest.MaxScenes} a scene manifest may carry.");
        }

        // TWO SEQUENCES MAY OVERLAP, and that is a scene list rather than a defect: overlapping is how
        // a composition crossfades one scene into the next, and the sandbox template's own
        // root.tsx does exactly that (0..100 and 90..190). A scene therefore begins where its own
        // <Sequence> places it — which is the same reading CompositionSceneManifest.ToShotSpans
        // already applies — and durationInFrames is a scene's hold, not a claim on the frames before
        // the next one starts.
        //
        // What a flat list may NOT contain is two sequences starting at the SAME frame: that is not a
        // crossfade, and it is the signature of a scene component's own inner <Sequence> (whose
        // "from" is relative to its parent) being read as if it were top-level.
        List<Span> ordered = spans.OrderBy(s => s.FromFrame).ThenBy(s => s.DurationInFrames).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].FromFrame == ordered[i - 1].FromFrame)
            {
                return Result.Unresolved(
                    $"two sequences both start at frame {ordered[i].FromFrame}; a nested or duplicated <Sequence> " +
                    "cannot be told from a top-level one, so the scene boundaries cannot be read from the code.");
            }
        }

        if (durationInFrames is { } compositionFrames && ordered[^1].EndFrame > compositionFrames)
        {
            return Result.Unresolved(
                $"a sequence ends at frame {ordered[^1].EndFrame}, past the composition's own {compositionFrames} frames.");
        }

        return new Result(true, ordered, fps, durationInFrames, []);
    }

    /// <summary>
    /// Blank everything that is not code — comments, template literals and quoted strings — while
    /// keeping the result the same length, so a reported problem still points at the right place.
    /// Quoted strings are blanked even inside JSX, which is why the composition's own
    /// <c>id="…"</c> is read from the original source instead: no sequence timing lives in a string,
    /// and leaving strings in would let the text of a string literal pose as a <c>&lt;Sequence&gt;</c>.
    /// </summary>
    internal static string BlankNonCode(string source)
    {
        char[] buffer = source.ToCharArray();

        for (int i = 0; i < buffer.Length; i++)
        {
            char c = buffer[i];

            if (c == '/' && i + 1 < buffer.Length && buffer[i + 1] == '/')
            {
                int end = i;
                while (end < buffer.Length && buffer[end] != '\n')
                    end++;
                Blank(buffer, i, end);
                i = end - 1;
                continue;
            }

            if (c == '/' && i + 1 < buffer.Length && buffer[i + 1] == '*')
            {
                int end = i + 2;
                while (end + 1 < buffer.Length && !(buffer[end] == '*' && buffer[end + 1] == '/'))
                    end++;
                end = Math.Min(end + 1, buffer.Length - 1);
                Blank(buffer, i, end + 1);
                i = end;
                continue;
            }

            if (c == '"' || c == '\'' || c == Backtick)
            {
                int end = i + 1;
                while (end < buffer.Length)
                {
                    if (buffer[end] == '\\')
                    {
                        end += 2;
                        continue;
                    }

                    if (buffer[end] == c)
                    {
                        end++;
                        break;
                    }

                    // An unterminated single-line string ends at the newline; a template literal may
                    // span lines, so only its own backtick closes it.
                    if (c != Backtick && buffer[end] == '\n')
                        break;

                    end++;
                }

                end = Math.Min(end, buffer.Length);
                Blank(buffer, i, end);
                i = end - 1;
            }
        }

        return new string(buffer);
    }

    private static void Blank(char[] buffer, int start, int end)
    {
        for (int i = Math.Max(0, start); i < Math.Min(end, buffer.Length); i++)
        {
            if (buffer[i] != '\n')
                buffer[i] = ' ';
        }
    }

    private static int? LiteralInt(Regex regex, string text) =>
        regex.Match(text) is { Success: true } match && int.TryParse(match.Groups[1].Value, out int value)
            ? value
            : null;

    private static string Clip(string text) =>
        text.Length <= MaxReasonChars
            ? text.Replace('\n', ' ')
            : text[..MaxReasonChars].Replace('\n', ' ') + "…";
}
