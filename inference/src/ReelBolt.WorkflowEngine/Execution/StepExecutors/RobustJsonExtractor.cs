namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Shared helper for extracting a balanced JSON object out of a model's raw completion text.
/// Hoisted out of <see cref="VideoCompileStepExecutor"/> (where it was originally added, and
/// remains used, for the story-editor decision JSON) so <see cref="ReviewLoopStepExecutor"/> can
/// apply the exact same hardening to <c>AgentType.VideoReviewAgent</c>'s output — that agent has
/// tools bound (see <c>AgentToolProvider</c>) just like the story editor, so it is exposed to the
/// identical failure mode: a reasoning-capable model (observed live on this stack) emitting valid
/// JSON followed by trailing prose, a markdown fence, or leaked tool-call-closing tokens once it
/// has any tools bound at all.
/// </summary>
internal static class RobustJsonExtractor
{
    /// <summary>
    /// Finds the first <c>{</c> in <paramref name="raw"/> and returns the substring through its
    /// matching balanced <c>}</c> (brace depth tracked with string-literal/escape awareness, so a
    /// <c>{</c>/<c>}</c> inside a quoted JSON string value never miscounts), discarding anything
    /// before or after. Returns null if no balanced object is found. Pure string scanning — no
    /// dependency on the JSON actually being well-formed beyond bracket balance, since the real
    /// validation happens wherever the caller deserializes/parses the extracted substring next.
    /// </summary>
    public static string? ExtractJsonObject(string raw)
    {
        int start = raw.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return raw.Substring(start, i - start + 1);
            }
        }

        return null; // unbalanced — never seen depth return to 0
    }

    /// <summary>
    /// Like <see cref="ExtractJsonObject"/> but returns the LAST top-level balanced <c>{...}</c>
    /// object in <paramref name="raw"/> instead of the first. Needed for a caller whose model
    /// reliably reasons extensively BEFORE emitting its real structured answer — observed live
    /// from <c>AgentType.VideoStoryEditor</c>: its chain-of-thought used small, complete, informal
    /// brace-pair shorthand (<c>"{s0,s0} and {s1,s1}"</c>, mid-reasoning notation for candidate
    /// spans) thousands of characters before the real final decision JSON at the very end of the
    /// response. <see cref="ExtractJsonObject"/>'s first-match strategy latches onto that
    /// incidental early object instead of the real answer; scanning for the last one that actually
    /// closes (tracking depth exactly like <see cref="ExtractJsonObject"/>, forward in one pass —
    /// scanning backward would need unreliable backward escape-sequence lookahead) finds the real
    /// answer regardless of how much brace-heavy reasoning precedes it. Returns null if no balanced
    /// object is found anywhere in <paramref name="raw"/>.
    /// </summary>
    public static string? ExtractLastJsonObject(string raw)
    {
        string? found = ScanForLastJsonObject(raw);
        if (found != null) return found;

        // Doubled-opening-brace repair, generalizing <see cref="CollapseDuplicateLeadingBrace"/>
        // from its original single caller to every structured-output payload. Same backend defect
        // that helper was written for -- this stack's vLLM server, under guided decoding
        // (`response_format: json_schema`), emits
        //     {{\n  "keep": ["s0", "s2"], ... }
        // with one spurious leading `{` and only one matching close, so the object sits
        // permanently one level deep, depth never returns to 0, and the scan above reports
        // "no JSON object". Reproduced directly against the endpoint while diagnosing a
        // VideoCompile step that failed with exactly that message.
        //
        // That helper's doc comment is careful to call itself narrow, because collapsing braces
        // unconditionally would corrupt a schema whose root legitimately opens with a nested
        // object -- and unlike VideoShotCaption, the payloads reaching this method (e.g.
        // VideoEditDecisionOutput, whose Keep is a list of objects) do have nesting. Running the
        // repair ONLY on the already-failed path is what makes it safe here: any response that
        // produces a balanced object never reaches this line, so a genuine nested structure is
        // returned by the scan above and never rewritten.
        string collapsed = CollapseDuplicateLeadingBrace(raw);
        // Reference equality: the helper returns the input unchanged when there is nothing to fix,
        // so this also avoids a pointless second full scan.
        return ReferenceEquals(collapsed, raw) ? null : ScanForLastJsonObject(collapsed);
    }

    private static string? ScanForLastJsonObject(string raw)
    {
        int depth = 0;
        bool inString = false;
        bool escape = false;
        int start = -1;
        string? lastComplete = null;

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}')
            {
                if (depth == 0) continue; // unmatched close outside any object — ignore
                depth--;
                if (depth == 0 && start >= 0)
                {
                    lastComplete = raw.Substring(start, i - start + 1);
                    start = -1;
                }
            }
        }

        return lastComplete;
    }

    /// <summary>
    /// Best-effort repair for near-miss JSON that uses single quotes as string delimiters —
    /// observed live from a local/open vision-language model that didn't honor a JSON-schema
    /// response format and instead emitted Python-dict-style output, e.g.
    /// <c>{'summary': 'a frame...'}</c>. Only ever meant to be tried AFTER a strict
    /// <see cref="System.Text.Json.JsonSerializer"/> parse of the raw text has already failed —
    /// never applied to output that already parses, since it is a lossy best-effort transform, not
    /// a general JSON5/relaxed parser.
    ///
    /// <para>
    /// Walks the text once, treating BOTH <c>'</c> and <c>"</c> as valid string delimiters
    /// (whichever opens a given string) and re-emitting every string canonicalized to
    /// double-quoted JSON: a bare <c>"</c> found inside a single-quoted string is escaped (it must
    /// be, now that <c>"</c> is the outer delimiter), an escaped delimiter (<c>\'</c> inside a
    /// single-quoted string, or <c>\"</c> inside a double-quoted one) becomes a bare character
    /// inside the new double-quoted string except where that character is itself <c>"</c> (which
    /// must stay escaped), and every other backslash escape (<c>\\</c>, <c>\n</c>, <c>\uXXXX</c>,
    /// ...) is copied through verbatim since it's already valid JSON escape syntax. Structural
    /// characters outside any string (braces, brackets, colons, commas, numbers, literals,
    /// whitespace) are copied through unchanged.
    /// </para>
    /// </summary>
    public static string? NormalizeQuotedStrings(string raw)
    {
        System.Text.StringBuilder sb = new(raw.Length + 16);
        int i = 0;
        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '\'' || c == '"')
            {
                char quote = c;
                i++;
                sb.Append('"');
                while (true)
                {
                    if (i >= raw.Length) return null; // unterminated string — give up

                    char sc = raw[i];
                    if (sc == '\\' && i + 1 < raw.Length)
                    {
                        char next = raw[i + 1];
                        if (next == quote)
                        {
                            // The escaped delimiter itself — becomes a bare character in the
                            // new double-quoted string, except a literal double-quote must stay
                            // escaped (it's the new delimiter).
                            sb.Append(next == '"' ? "\\\"" : next.ToString());
                        }
                        else
                        {
                            // Any other valid JSON escape (\\, \n, \t, \uXXXX, ...) — copy the
                            // backslash AND the character through verbatim.
                            sb.Append(sc).Append(next);
                        }
                        i += 2;
                        continue;
                    }

                    if (sc == quote)
                    {
                        sb.Append('"');
                        i++;
                        break;
                    }

                    if (sc == '"')
                    {
                        // A bare double-quote inside a single-quoted string must be escaped now
                        // that the canonicalized string is itself double-quoted.
                        sb.Append("\\\"");
                        i++;
                        continue;
                    }

                    sb.Append(sc);
                    i++;
                }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Best-effort repair for a model response that opens with a DOUBLED brace —
    /// <c>{{"summary": ...}</c> instead of <c>{"summary": ...}</c> — but only ever emits one
    /// matching close, so <see cref="ExtractJsonObject"/>'s balanced-brace scan (which correctly
    /// treats the second <c>{</c> as opening a real nested object) never sees depth return to 0 and
    /// gives up. Observed live and repeatedly from a vLLM-served vision model: a stray extra
    /// opening brace, always at the very start, always exactly one extra, never anywhere else in
    /// the response and never doubled at the close. Safe to collapse unconditionally for THIS
    /// schema specifically — <c>VideoShotCaption</c> (the only caller) has no nested-object-valued
    /// property, so its root JSON object structurally never needs more than one leading <c>{</c>;
    /// a real, intentional nested object would need this repair to leave it alone, which is why
    /// this is a narrow fix for one caller's schema, not a general "collapse repeated braces"
    /// utility.
    /// </summary>
    public static string CollapseDuplicateLeadingBrace(string raw)
    {
        int i = 0;
        while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;

        int braceRunEnd = i;
        while (braceRunEnd < raw.Length && raw[braceRunEnd] == '{') braceRunEnd++;

        int extraBraces = braceRunEnd - i - 1; // 1 real opening brace is always kept
        if (extraBraces <= 0) return raw;

        return raw[..i] + "{" + raw[braceRunEnd..];
    }

    /// <summary>
    /// The repaired JSON text of the verdict object chosen by <see cref="SelectScoredObject"/>, or
    /// null when no candidate is accepted or an expected verdict key is unaccounted for. The returned
    /// string always parses with <see cref="System.Text.Json.JsonDocument"/>.
    /// </summary>
    public static string? ExtractScoredJsonObject(string raw, IReadOnlyList<string> scoreKeys) =>
        SelectScoredObject(raw, scoreKeys)?.Json;

    /// <summary>
    /// The verdict object chosen by <see cref="ScanVerdicts"/>: its repaired text, its score,
    /// how many candidates were accepted, and whether those candidates disagreed.
    /// </summary>
    internal sealed record ScoredJsonObject(string Json, double Score, int AcceptedCount, bool ScoresDiffer);

    /// <summary>
    /// The outcome of <see cref="ScanVerdicts"/>: the chosen verdict (null when no candidate is
    /// accepted) and how many occurrences of the expected verdict keys lie outside every accepted
    /// candidate. Any such occurrence fails the review closed.
    /// </summary>
    internal sealed record VerdictScan(ScoredJsonObject? Verdict, int UnaccountedKeyCount);

    /// <summary>
    /// The verdict <see cref="ScanVerdicts"/> chose, or null when no candidate is accepted or any
    /// expected verdict key is unaccounted for.
    /// </summary>
    internal static ScoredJsonObject? SelectScoredObject(string raw, IReadOnlyList<string> scoreKeys)
    {
        VerdictScan scan = ScanVerdicts(raw, scoreKeys);
        return scan.UnaccountedKeyCount == 0 ? scan.Verdict : null;
    }

    /// <summary>
    /// The one scan both the review score and the revision brief read. One left-to-right pass tracks
    /// whether it is inside a fenced block, and every <c>{</c> outside a fence starts a candidate. A
    /// candidate's structure is tracked string- and comment-aware: braces and backtick fences inside a
    /// JSON string or a <c>//</c> or <c>/* */</c> comment are not structure, and a fence line met outside
    /// a string ends the candidate. A candidate is accepted when its repaired ROOT object (one
    /// <see cref="RepairFrom"/> pass) parses and carries one of <paramref name="scoreKeys"/> whose value
    /// is an integer literal in [0, 10]; duplicate root keys take the minimum, and a non-numeric,
    /// fractional, exponent or out-of-range occurrence rejects the candidate. A candidate that does not
    /// parse is skipped whole: the scan resumes after its end (or at the fence line that cut it), never
    /// inside it.
    /// <para>
    /// Fail closed on key accounting (see <see cref="CountUnaccountedKeys"/>): a key whose normalised
    /// spelling is "overallScore" must be the exact spelling inside an accepted candidate, and a key
    /// whose normalised spelling is "score" must lie inside one, for the agent types that read it. Each
    /// offending occurrence is counted in <see cref="VerdictScan.UnaccountedKeyCount"/>.
    /// </para>
    /// <para>
    /// Several accepted candidates: the LOWEST score wins, so an echoed or injected object can never
    /// raise the verdict above the real one. Equal scores: the first in order.
    /// </para>
    /// </summary>
    internal static VerdictScan ScanVerdicts(string raw, IReadOnlyList<string> scoreKeys)
    {
        List<AcceptedSpan> accepted = new();
        bool inFence = false;
        int pos = 0;
        while (pos < raw.Length)
        {
            if (IsLineStart(raw, pos))
            {
                int tick = FenceTickAt(raw, pos);
                if (tick >= 0)
                {
                    if (inFence)
                    {
                        inFence = false;
                        pos = tick + 3;
                    }
                    else
                    {
                        System.Text.RegularExpressions.Match opener = FenceOpener.Match(raw, pos);
                        if (opener.Success)
                        {
                            inFence = true;
                            pos += opener.Length;
                        }
                        else
                        {
                            pos = tick + 3;
                        }
                    }

                    continue;
                }
            }

            if (raw[pos] != '{')
            {
                pos++;
                continue;
            }

            // The repair pass is the candidate: it stops at the fence line that ends the candidate, so
            // ScanCandidateEnd (a second walk to the balancing brace) runs only when the repair fails.
            string? repaired = RepairFrom(raw, pos, raw.Length, stopAtFence: true, out int end, out _);
            if (repaired is not null)
            {
                if (TryReadVerdict(repaired, scoreKeys, out double score))
                    accepted.Add(new AcceptedSpan(repaired, score, pos, end));
                pos = end + 1;
            }
            else
            {
                int balancedEnd = ScanCandidateEnd(raw, pos, out int cut);
                pos = balancedEnd >= 0 ? balancedEnd + 1 : cut >= 0 ? cut : raw.Length;
            }
        }

        int unaccounted = CountUnaccountedKeys(raw, scoreKeys, accepted);
        if (accepted.Count == 0)
            return new VerdictScan(null, unaccounted);

        (string Json, double Score) best = (accepted[0].Json, accepted[0].Score);
        bool scoresDiffer = false;
        foreach (AcceptedSpan candidate in accepted)
        {
            if (candidate.Score != best.Score)
                scoresDiffer = true;
            if (candidate.Score < best.Score)
                best = (candidate.Json, candidate.Score);
        }

        return new VerdictScan(new ScoredJsonObject(best.Json, best.Score, accepted.Count, scoresDiffer), unaccounted);
    }

    /// <summary>An accepted candidate and where it sits in the raw text (inclusive end index).</summary>
    private readonly record struct AcceptedSpan(string Json, double Score, int Start, int End);

    /// <summary>
    /// Repairs the structural slips a model makes when it writes a long nested object by hand, then
    /// returns the repaired object starting at the first <c>{</c> in <paramref name="raw"/>. Seen
    /// live from the promo reviewer (staging, promo-2 iteration 1): every <c>criteria</c> entry
    /// ended <c>"feedback": "...",</c> and went straight into the next <c>{</c>, with the last one
    /// closed by a <c>}</c> after a trailing comma. Three repairs, all driven by the parser state and
    /// all string-literal aware:
    /// <list type="bullet">
    /// <item>a <c>{</c> where an object expects a key closes that object, and becomes the next
    /// element of the enclosing array (only inside an array; anywhere else gives up);</item>
    /// <item>a <c>]</c> closes any objects still open inside its array;</item>
    /// <item>a comma directly before <c>}</c> or <c>]</c> is dropped.</item>
    /// </list>
    /// Well-formed JSON comes out unchanged. Never invents a close the text does not imply:
    /// truncated output (the text ends with containers still open) returns null, like
    /// <see cref="ExtractLastJsonObject"/>. The caller still parses the result.
    /// </summary>
    public static string? RepairUnclosedObjects(string raw)
    {
        int start = raw.IndexOf('{');
        return start < 0 ? null : RepairFrom(raw, start, raw.Length, stopAtFence: false, out _, out _);
    }

    /// <summary>A fence opener at a line start: indentation, the backticks, an optional language tag, then the line break.</summary>
    private static readonly System.Text.RegularExpressions.Regex FenceOpener = new(
        @"\G[ \t]*```[A-Za-z0-9_-]*[ \t]*\r?\n",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The verdict key every reviewer type polices by its normalised spelling (SEC-12, SEC-13).</summary>
    private const string OverallScoreKey = "overallScore";

    /// <summary>The verdict key whose normalised spelling is policed outside accepted candidates, when the agent type reads it.</summary>
    private const string ScoreKey = "score";

    /// <summary>A run of key characters: letters, marks, format characters, '_', '-', or a <c>\uXXXX</c> escape.</summary>
    private static readonly System.Text.RegularExpressions.Regex KeyRun = new(
        @"(?:[\p{L}\p{N}\p{M}\p{Cf}_\-]|\\u[0-9A-Fa-f]{4})+",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A <c>\uXXXX</c> escape inside a key run, decoded before the key is normalised.</summary>
    private static readonly System.Text.RegularExpressions.Regex UnicodeEscape = new(
        @"\\u([0-9A-Fa-f]{4})",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Lookalike letters of the Latin letters in the two key names ("overallScore", "score"), mapped to
    /// those Latin letters after case folding. Cyrillic, Greek and the dotless and click letters only.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<char, char> Lookalikes = new()
    {
        // Cyrillic
        ['а'] = 'a',
        ['е'] = 'e',
        ['о'] = 'o',
        ['р'] = 'p',
        ['с'] = 'c',
        ['у'] = 'y',
        ['х'] = 'x',
        ['і'] = 'i',
        ['ј'] = 'j',
        ['ѕ'] = 's',
        ['ԁ'] = 'd',
        ['ӏ'] = 'l',
        ['һ'] = 'h',
        ['ԛ'] = 'q',
        ['ԝ'] = 'w',
        // Greek
        ['α'] = 'a',
        ['ο'] = 'o',
        ['ν'] = 'v',
        ['ε'] = 'e',
        ['ι'] = 'i',
        ['ρ'] = 'p',
        ['κ'] = 'k',
        ['τ'] = 't',
        ['υ'] = 'u',
        // Dotless i and the dental click
        ['ı'] = 'i',
        ['ǀ'] = 'l',
    };

    private static bool IsLineStart(string raw, int pos) => pos == 0 || raw[pos - 1] == '\n';

    /// <summary>The index of the first backtick of a fence line that starts at <paramref name="pos"/> (after optional spaces or tabs), or -1.</summary>
    private static int FenceTickAt(string raw, int pos)
    {
        int i = pos;
        while (i < raw.Length && (raw[i] == ' ' || raw[i] == '\t')) i++;
        return i + 2 < raw.Length && raw[i] == '`' && raw[i + 1] == '`' && raw[i + 2] == '`' ? i : -1;
    }

    /// <summary>
    /// The line start of the fence line whose first backtick is at <paramref name="tick"/> (only spaces
    /// or tabs before it on its line), or -1 when the backticks are not at a line start.
    /// </summary>
    private static int FenceLineStartBefore(string raw, int tick)
    {
        if (tick + 2 >= raw.Length || raw[tick + 1] != '`' || raw[tick + 2] != '`') return -1;

        int p = tick;
        while (p > 0 && (raw[p - 1] == ' ' || raw[p - 1] == '\t')) p--;
        return p == 0 || raw[p - 1] == '\n' ? p : -1;
    }

    /// <summary>
    /// The index of the <c>}</c> that balances the <c>{</c> at <paramref name="start"/>, tracked
    /// string-, escape- and comment-aware, or -1 when it never balances. A fence line met outside a
    /// string or comment before it balances returns -1 with <paramref name="cut"/> set to that line's
    /// start, so a candidate never runs on past the fence that ends it.
    /// </summary>
    private static int ScanCandidateEnd(string raw, int start, out int cut)
    {
        cut = -1;
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
            {
                int newline = raw.IndexOf('\n', i);
                if (newline < 0) return -1;
                i = newline;
                continue;
            }

            if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '*')
            {
                int close = raw.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) return -1;
                i = close + 1;
                continue;
            }

            if (c == '`')
            {
                int fenceLine = FenceLineStartBefore(raw, i);
                if (fenceLine >= 0)
                {
                    cut = fenceLine;
                    return -1;
                }
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }

        return -1;
    }

    /// <summary>
    /// Reads the verdict a repaired root object carries: the first of <paramref name="scoreKeys"/> the
    /// root has decides it. Every occurrence of that key must be a finite JSON number in [0, 10]; the
    /// lowest is the score. False when the text is not an object with such a key, or any occurrence of
    /// it is not a valid score.
    /// </summary>
    private static bool TryReadVerdict(string json, IReadOnlyList<string> scoreKeys, out double score)
    {
        score = 0;

        // A score key is read by its exact decoded name, so a candidate that can carry one spells "score"
        // (in any case) or escapes a character with a backslash. Any other candidate is rejected here, which
        // skips a parse that would throw for the degenerate candidates a hostile reviewer can produce.
        if (json.IndexOf('\\') < 0 && json.IndexOf("score", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            foreach (string key in scoreKeys)
            {
                bool found = false;
                double lowest = 0;
                foreach (System.Text.Json.JsonProperty property in root.EnumerateObject())
                {
                    if (!string.Equals(property.Name, key, StringComparison.Ordinal)) continue;
                    if (!IsVerdictNumber(property.Value, out double value)) return false;
                    lowest = found ? Math.Min(lowest, value) : value;
                    found = true;
                }

                if (found)
                {
                    score = lowest;
                    return true;
                }
            }

            return false;
        }
        // A lone surrogate in a property name or string makes JsonElement throw InvalidOperationException
        // (not JsonException) on read; it is unreadable the same way (SEC-18).
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// A verdict is an integer literal in [0, 10]: the raw token must match <c>^-?[0-9]+$</c>, so a
    /// fraction or an exponent is out of schema and rejects the candidate (SEC-10). The token is read
    /// as written, not as a double, so no rounding can move it onto a pass mark.
    /// </summary>
    private static bool IsVerdictNumber(System.Text.Json.JsonElement value, out double number)
    {
        number = 0;
        if (value.ValueKind != System.Text.Json.JsonValueKind.Number || !value.TryGetDouble(out double parsed))
            return false;
        if (!IsIntegerLiteral(value.GetRawText()))
            return false;
        if (!double.IsFinite(parsed) || parsed < 0 || parsed > 10)
            return false;

        number = parsed;
        return true;
    }

    private static bool IsIntegerLiteral(string token)
    {
        int i = token.Length > 0 && token[0] == '-' ? 1 : 0;
        if (i == token.Length) return false;
        for (; i < token.Length; i++)
        {
            if (token[i] < '0' || token[i] > '9') return false;
        }

        return true;
    }

    /// <summary>
    /// Counts the verdict-key occurrences that fail the review closed (SEC-12, SEC-13). A key is a run of
    /// key characters between two quote marks, or a bare run right after <c>{</c> or <c>,</c> and before
    /// <c>:</c>. Its spelling is <see cref="NormalisedKey"/>, with any <c>\uXXXX</c> escape decoded first.
    /// <list type="bullet">
    /// <item>A key normalising to "overallScore" counts unless it is the exact spelling inside an accepted
    /// candidate. Every reviewer type: a non-canonical spelling anywhere fails, and so does the exact one
    /// outside every accepted candidate.</item>
    /// <item>For an agent type that reads "score", a key normalising to "score" counts when it lies outside
    /// every accepted candidate. Nested keys inside an accepted candidate are fine.</item>
    /// </list>
    /// <paramref name="accepted"/> is in text order, so each occurrence is located by binary search.
    /// </summary>
    private static int CountUnaccountedKeys(string raw, IReadOnlyList<string> keys, IReadOnlyList<AcceptedSpan> accepted)
    {
        bool readsScore = System.Linq.Enumerable.Contains(keys, ScoreKey);
        int unaccounted = 0;
        foreach (System.Text.RegularExpressions.Match run in KeyRun.Matches(raw))
        {
            if (!IsKeyPosition(raw, run.Index, run.Length))
                continue;

            string spelled = UnicodeEscape.Replace(run.Value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            string normalised = NormalisedKey(spelled);
            bool inside = IsInsideAccepted(accepted, run.Index, run.Length);
            if (normalised == "overallscore")
            {
                if (!inside || spelled != OverallScoreKey)
                    unaccounted++;
            }
            else if (readsScore && normalised == "score" && !inside)
            {
                unaccounted++;
            }
        }

        return unaccounted;
    }

    /// <summary>
    /// True when the run is a key: quoted (a quote mark on each side, either quote character), or bare
    /// (the nearest non-space character before it is <c>{</c> or <c>,</c>, and the nearest after is <c>:</c>).
    /// </summary>
    private static bool IsKeyPosition(string raw, int index, int length)
    {
        int after = index + length;
        if (index > 0 && IsQuote(raw[index - 1]) && after < raw.Length && IsQuote(raw[after]))
            return true;

        int before = index - 1;
        while (before >= 0 && char.IsWhiteSpace(raw[before])) before--;
        if (before < 0 || (raw[before] != '{' && raw[before] != ','))
            return false;

        int next = after;
        while (next < raw.Length && char.IsWhiteSpace(raw[next])) next++;
        return next < raw.Length && raw[next] == ':';
    }

    private static bool IsQuote(char c) => c == '"' || c == '\'';

    /// <summary>
    /// The spelling a key is compared by (SEC-12, SEC-13): NFKC, format characters removed (zero-width
    /// joiners and spaces included), '_' and '-' removed, case folded (invariant), then the lookalikes in
    /// <see cref="Lookalikes"/> mapped to Latin. So "overallScore", "OverallScore", "overall_score" and
    /// "overall-score" are all "overallscore". Text NFKC rejects (an invalid UTF-16 sequence) is returned
    /// as written, so it matches only its own spelling.
    /// </summary>
    internal static string NormalisedKey(string key)
    {
        string nfkc;
        try
        {
            nfkc = key.Normalize(System.Text.NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return key;
        }

        System.Text.StringBuilder kept = new(nfkc.Length);
        foreach (char ch in nfkc)
        {
            if (ch == '_' || ch == '-') continue;
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format) continue;
            kept.Append(ch);
        }

        string folded = kept.ToString().ToLowerInvariant();
        System.Text.StringBuilder mapped = new(folded.Length);
        foreach (char ch in folded)
            mapped.Append(Lookalikes.TryGetValue(ch, out char latin) ? latin : ch);
        return mapped.ToString();
    }

    private static bool IsInsideAccepted(IReadOnlyList<AcceptedSpan> accepted, int index, int length)
    {
        int low = 0;
        int high = accepted.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (accepted[mid].Start <= index)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found >= 0 && index + length - 1 <= accepted[found].End;
    }

    /// <summary>
    /// The scan behind <see cref="RepairUnclosedObjects"/> and <see cref="ScanVerdicts"/>, from the
    /// <c>{</c> at <paramref name="start"/> and never past <paramref name="limit"/>. Strips
    /// <c>//</c> and <c>/* */</c> comments outside strings, closes objects left open at the end of an
    /// array element or array, and drops trailing commas. <paramref name="end"/> is the index of the
    /// character that closed the root object (or one implied before it); -1 when it returns null.
    /// With <paramref name="stopAtFence"/>, a fence line met outside a string or comment ends the
    /// candidate: the result is null and <paramref name="cut"/> is that line's start (otherwise -1).
    /// This is the same cut <see cref="ScanCandidateEnd"/> makes, found in the same pass.
    /// </summary>
    private static string? RepairFrom(string text, int start, int limit, bool stopAtFence, out int end, out int cut)
    {
        end = -1;
        cut = -1;
        System.Text.StringBuilder sb = new();
        Stack<char> open = new();
        bool expectKey = false; // the innermost open object is waiting for a key (after `{` or `,`)
        bool inString = false;
        bool escape = false;

        for (int i = start; i < limit; i++)
        {
            char c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '/' && i + 1 < limit && text[i + 1] == '/')
            {
                int newline = text.IndexOf('\n', i, limit - i);
                if (newline < 0) return null;
                sb.Append(' ');
                i = newline;
                continue;
            }

            if (c == '/' && i + 1 < limit && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, limit - i - 2, StringComparison.Ordinal);
                if (close < 0) return null;
                sb.Append(' ');
                i = close + 1;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                sb.Append(c);
                continue;
            }

            if (stopAtFence && c == '`')
            {
                int fenceLine = FenceLineStartBefore(text, i);
                if (fenceLine >= 0)
                {
                    cut = fenceLine;
                    return null;
                }
            }

            bool inObject = open.Count > 0 && open.Peek() == '{';
            switch (c)
            {
                case '"':
                    inString = true;
                    expectKey = false;
                    sb.Append(c);
                    break;
                case '{':
                    if (inObject && expectKey)
                    {
                        // An object left open: close it, and this `{` is the next array element.
                        DropTrailingComma(sb);
                        sb.Append('}');
                        open.Pop();
                        if (open.Count == 0 || open.Peek() != '[') return null;
                        sb.Append(',');
                    }

                    open.Push('{');
                    expectKey = true;
                    sb.Append(c);
                    break;
                case '}':
                    if (!inObject) return null;
                    DropTrailingComma(sb);
                    open.Pop();
                    sb.Append(c);
                    expectKey = false;
                    if (open.Count == 0)
                    {
                        end = i;
                        return sb.ToString();
                    }

                    break;
                case '[':
                    open.Push('[');
                    expectKey = false;
                    sb.Append(c);
                    break;
                case ']':
                    DropTrailingComma(sb);
                    while (open.Count > 0 && open.Peek() == '{')
                    {
                        // Objects still open inside this array end with it.
                        open.Pop();
                        sb.Append('}');
                    }

                    if (open.Count == 0) return null;
                    open.Pop();
                    sb.Append(c);
                    expectKey = false;
                    break;
                case ',':
                    expectKey = inObject;
                    sb.Append(c);
                    break;
                default:
                    expectKey = false;
                    sb.Append(c);
                    break;
            }
        }

        return null; // ran out of text with containers still open: truncated, not repairable
    }

    private static void DropTrailingComma(System.Text.StringBuilder sb)
    {
        int j = sb.Length - 1;
        while (j >= 0 && char.IsWhiteSpace(sb[j])) j--;
        if (j >= 0 && sb[j] == ',') sb.Remove(j, 1);
    }
}
