using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Sanitizes LLM-authored voiceover text before it's passed to Fish Audio's speech synthesis API.
/// Strips emotion tags in both square and parenthesis syntaxes, control AND format characters, and
/// URLs.
/// </summary>
public static class VoiceoverTextSanitizer
{
    /// <summary>
    /// Hard upper bound (in UTF-16 code units) on how much of <c>raw</c> this sanitizer will ever
    /// examine. Applied at the very top of <see cref="Sanitize"/>, BEFORE any regex runs.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists.</b> The two bracket patterns are non-greedy and backtracking: on an input
    /// with many unmatched open brackets and no closer, `[.*?]` fails from every start position and
    /// costs O(n²). Measured on this codebase, a 1.28M-character line of `[` cost <b>28.5 s</b> of
    /// CPU — and <see cref="Sanitize"/> is called by <c>VoiceoverStepExecutor</c> with
    /// <c>maxLength: int.MaxValue</c> and truncates only AFTER every regex has run, so that cost was
    /// paid on the engine shared by every tenant, before the executor's own byte guard.
    /// <c>Regex.MatchTimeout</c> alone is not enough: a timeout would turn a slow line into a thrown
    /// <see cref="RegexMatchTimeoutException"/>, i.e. a failed step. The cap is what keeps the
    /// patterns off an unbounded string in the first place.
    ///
    /// <b>Why 4096.</b> No legitimate voiceover line approaches it: a line is one scene's narration,
    /// typically a sentence or three, and 4096 UTF-16 code units is roughly 700 words — several
    /// minutes of continuous speech, for a step whose whole point is that each line is anchored to a
    /// start time on the timeline. It also leaves the quadratic worst case with a large safety
    /// margin: scaling the measurement above, 4096 all-`[` characters costs ≈0.3 ms against the
    /// 100 ms <see cref="RegexMatchTimeout"/> (and it takes ~65k characters to get near that
    /// timeout). Truncation at this cap is silent by design — the alternative is an unbounded string
    /// reaching a super-linear pattern.
    /// </remarks>
    public const int HardMaxRawChars = 4096;

    /// <summary>
    /// Finite match timeout applied to EVERY <see cref="Regex"/> in this class. Never
    /// <see cref="Regex.InfiniteMatchTimeout"/>: a <see cref="Regex"/> constructed without a timeout
    /// silently gets the infinite one back, which is how the super-linear behaviour above went
    /// unnoticed. Enforced by <c>VoiceoverTextSanitizerTests</c>, which reflects over every static
    /// <see cref="Regex"/> field and fails if any of them has an infinite timeout.
    /// </summary>
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Pattern to match non-greedy `[...]` sequences (emotion tags like [whispering]).
    /// </summary>
    /// <remarks>
    /// <see cref="RegexOptions.Singleline"/> is defense in depth, NOT the fix: it makes `.` match
    /// `\n`, so a tag split by a literal newline (`[whis\npering]`) is still matched as one tag. The
    /// actual fix is the order of operations below (control characters are stripped first), but if a
    /// future reorder ever put this pattern back in front, Singleline alone would still stop the
    /// tag from surviving. Non-greedy semantics are unchanged.
    /// </remarks>
    private static readonly Regex SquareBracketPattern = new(
        @"\[.*?\]", RegexOptions.Compiled | RegexOptions.Singleline, RegexMatchTimeout);

    /// <summary>
    /// Pattern to match non-greedy `(...)` sequences (emotion tags like (excited)). Same
    /// <see cref="RegexOptions.Singleline"/> defense in depth as <see cref="SquareBracketPattern"/>.
    /// </summary>
    private static readonly Regex ParenthesisPattern = new(
        @"\(.*?\)", RegexOptions.Compiled | RegexOptions.Singleline, RegexMatchTimeout);

    /// <summary>
    /// Pattern to match URLs starting with http:// or https://.
    /// </summary>
    private static readonly Regex UrlPattern = new(
        @"https?://\S+", RegexOptions.Compiled, RegexMatchTimeout);

    /// <summary>
    /// Pattern to match Unicode control (Cc) AND format (Cf) characters.
    /// </summary>
    /// <remarks>
    /// Cc alone was the pre-ticket coverage. Cf is a separate category — invisible by definition —
    /// and every one of U+200B (zero width space), U+202E (right-to-left override), U+FEFF
    /// (zero width no-break space / BOM), U+00AD (soft hyphen), U+2066 (left-to-right isolate) and
    /// U+2069 (pop directional isolate) used to pass through verbatim into the TTS request. Cc
    /// coverage (U+0000..U+001F, U+007F) is retained.
    /// </remarks>
    private static readonly Regex ControlAndFormatCharPattern = new(
        @"[\p{Cc}\p{Cf}]", RegexOptions.Compiled, RegexMatchTimeout);

    /// <summary>
    /// Pattern to match two or more consecutive whitespace characters.
    /// </summary>
    private static readonly Regex MultipleSpacePattern = new(
        @"\s+", RegexOptions.Compiled, RegexMatchTimeout);

    /// <summary>
    /// Sanitizes voiceover text by capping the raw input, stripping control/format characters,
    /// emotion tags (both syntaxes) and URLs, collapsing multiple spaces, and truncating to
    /// maxLength.
    ///
    /// Order of operations:
    /// 0. Cap the raw input at <see cref="HardMaxRawChars"/> — BEFORE any regex runs
    /// 1. Strip Unicode Cc control AND Cf format characters
    /// 2. Strip [...] emotion tags (non-greedy, newline-tolerant)
    /// 3. Strip (...) emotion tags (non-greedy, newline-tolerant)
    /// 4. Strip https?://\S+ URLs
    /// 5. Collapse 2+ spaces to one
    /// 6. Trim leading/trailing whitespace
    /// 7. Truncate to maxLength UTF-16 chars
    ///
    /// <para>
    /// <b>Steps 1-3 are ordered deliberately.</b> Control characters must be stripped BEFORE the
    /// bracket patterns. When the bracket patterns ran first, `[whis\npering]` was untouched by
    /// them (`.` does not match `\n`), and step 1 then simply DELETED the newline — reconstituting
    /// the literal tag `[whispering]` in the string handed to Fish Audio. Stripping the newline
    /// first yields `[whispering]`, which the bracket patterns then remove as normal.
    /// </para>
    /// </summary>
    public static string Sanitize(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        // Step 0: hard-cap the raw input BEFORE any regex sees it, so the quadratically-behaved
        // bracket patterns below can never be handed an unbounded string. Grapheme-safe, so the cap
        // cannot split a surrogate pair.
        string boundedRaw = TruncateByTextElements(raw, HardMaxRawChars);

        // Step 1: Strip Cc control and Cf format characters
        // NFC first: a model that emits decomposed accents (e + U+0300) must reach Fish as the
        // precomposed letter. NFC input is returned unchanged, so existing content keys hold.
        string normalized = boundedRaw.Normalize(NormalizationForm.FormC);
        string noControlChars = ControlAndFormatCharPattern.Replace(normalized, string.Empty);

        // Step 2: Strip square bracket emotion tags
        string noSquareTags = SquareBracketPattern.Replace(noControlChars, string.Empty);

        // Step 3: Strip parenthesis emotion tags
        string noParenTags = ParenthesisPattern.Replace(noSquareTags, string.Empty);

        // Step 4: Strip URLs
        string noUrls = UrlPattern.Replace(noParenTags, string.Empty);

        // Step 5: Collapse multiple spaces to single spaces
        string collapsed = MultipleSpacePattern.Replace(noUrls, " ");

        // Step 6: Trim
        string trimmed = collapsed.Trim();

        if (trimmed.Length == 0)
            return string.Empty;

        // Step 7: Truncate to maxLength (by UTF-16 chars, like OverlayTextSanitizer)
        string truncated = TruncateByTextElements(trimmed, Math.Max(0, maxLength));
        string result = truncated.Trim();
        return result;
    }

    /// <summary>
    /// Truncates by Unicode text element (grapheme cluster, via <see cref="StringInfo"/>) rather
    /// than raw UTF-16 code unit, so a surrogate pair or a base-character-plus-combining-mark
    /// sequence is never split mid-character. Matches the pattern in OverlayTextSanitizer.
    /// </summary>
    private static string TruncateByTextElements(string value, int maxChars)
    {
        if (maxChars <= 0)
            return string.Empty;

        if (value.Length <= maxChars)
            return value;

        StringBuilder sb = new(maxChars);
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            string element = (string)enumerator.Current;
            if (sb.Length + element.Length > maxChars)
                break;

            sb.Append(element);
        }

        return sb.ToString();
    }
}
