using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// The deterministic last line of the "names, never ids" rule: it watches what the assistant's tools return,
/// remembers which identifier belongs to which name, and cleans the finished reply so a customer never reads a
/// GUID or a confirmation token, whatever the model wrote.
/// </summary>
/// <remarks>
/// <para>One instance per turn. <see cref="Learn"/> is called with every tool result (any shape; non-JSON is
/// tolerated) and <see cref="Sanitize"/> with the reply text before it is persisted or streamed. Both are safe
/// to call from several threads and neither ever throws: a failure means "learn nothing" / "return the reply
/// unchanged".</para>
/// <para>What <see cref="Sanitize"/> does, in order of precedence for one identifier:</para>
/// <list type="bullet">
/// <item>a bracketed, parenthesised or braced GUID (<c>(id: …)</c>, <c>[…]</c>) is removed entirely, with an
/// <c>id:</c> / <c>with id</c> label and the space in front of it;</item>
/// <item>a bare or backticked GUID whose name was learned becomes that name in curly quotes
/// (<c>“Launch Teaser”</c>); when the same name already stands right before it, it is just removed;</item>
/// <item>an unknown bare GUID is removed, and the punctuation, brackets, bullets and spaces it leaves behind are
/// tidied;</item>
/// <item>a remembered confirmation token is removed wherever it appears;</item>
/// <item>a GUID inside the target of a Markdown link (<c>[open it](/app/projects/…)</c>) is left alone: it is
/// not text the reader sees, and removing it would break the link.</item>
/// </list>
/// <para>Nothing else is rewritten, so fenced code blocks keep their structure.</para>
/// </remarks>
public sealed class AssistantReplySanitizer
{
    private const int MaxRememberedNames = 5_000;
    private const int MaxRememberedTokens = 64;
    private const int MaxNameLength = 120;
    private const int MinTokenLength = 8;
    private const int MaxJsonWalkDepth = 16;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const string GuidPattern =
        @"(?<![0-9A-Za-z])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Za-z])";

    private const string LabelPattern =
        @"(?<![0-9A-Za-z])(?:(?:with|having|has)\s+(?:the\s+)?)?(?:ids?|identifiers?|guid|uuid)\s*[:=#]?";

    /// <summary>
    /// One alternative per way a GUID is written: wrapped in brackets, introduced by an <c>id</c> label, or on its
    /// own (optionally in backticks). Spaces and tabs in front of it are part of the match so removing it never
    /// leaves a double space.
    /// </summary>
    private static readonly Regex GuidRegex = new(
        @"(?<pre>[ \t]*)(?:"
        + @"(?<open>[(\[{])[ \t]*(?:" + LabelPattern + @"[ \t]*)?`?(?<g1>" + GuidPattern + @")`?[ \t]*(?<close>[)\]}])"
        + @"|(?<label>" + LabelPattern + @")[ \t]*`?(?<g2>" + GuidPattern + @")`?"
        + @"|`?(?<g3>" + GuidPattern + @")`?"
        + @")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex MarkdownLinkTarget = new(
        @"\]\((?<target>[^)\s]*)\)",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex TokenInText = new(
        "\"confirmationToken\"\\s*:\\s*\"(?<t>[^\"]{" + MinTokenLength + ",})\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex GuidInName = new(GuidPattern, RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>Property names that name the object they sit in, in priority order.</summary>
    private static readonly string[] GenericNameProperties =
        ["name", "filename", "originalfilename", "displayname", "title", "label"];

    private readonly ConcurrentDictionary<Guid, string> _names = new();
    private readonly ConcurrentDictionary<string, byte> _tokens = new(StringComparer.Ordinal);

    /// <summary>Number of identifier-to-name pairs remembered so far (for tests and diagnostics).</summary>
    public int KnownNameCount => _names.Count;

    // ---------------------------------------------------------------------
    // Learning
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads one tool result and remembers every identifier that sits beside a name, plus any confirmation
    /// token. Tolerates anything: <c>null</c>, plain text and malformed JSON simply teach nothing.
    /// </summary>
    public void Learn(string? toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return;

        try
        {
            LearnCore(toolResult, 0);
        }
        catch (Exception)
        {
            // Learning is best effort by design; a reply that is not cleaned is still no worse than no guard.
        }
    }

    private void LearnCore(string text, int depth)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });
            Walk(document.RootElement, depth);
        }
        catch (JsonException)
        {
            foreach (Match match in TokenInText.Matches(text))
                RememberToken(match.Groups["t"].Value);
        }
    }

    private void Walk(JsonElement element, int depth)
    {
        if (depth > MaxJsonWalkDepth)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                    Walk(item, depth + 1);
                break;

            case JsonValueKind.Object:
                LearnObject(element);
                foreach (JsonProperty property in element.EnumerateObject())
                    Walk(property.Value, depth + 1);
                break;

            case JsonValueKind.String:
                // A tool that returns its JSON as a string inside JSON: look inside once per level.
                string? inner = element.GetString();
                if (inner is { Length: > 1 } && (inner[0] == '{' || inner[0] == '['))
                    LearnCore(inner, depth + 1);
                break;
        }
    }

    private void LearnObject(JsonElement obj)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var guidProperties = new List<(string Prefix, Guid Value)>();

        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            string? value = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            string lowered = property.Name.ToLowerInvariant();
            strings[lowered] = value;

            if (lowered == "confirmationtoken")
            {
                RememberToken(value);
                continue;
            }

            // "id", "projectId"/"ProjectId" and "project_id": the prefix is what the matching name property
            // starts with ("projectName", "project_name").
            string? prefix = lowered == "id" ? string.Empty
                : lowered.EndsWith("_id", StringComparison.Ordinal) ? lowered[..^2]
                : property.Name.EndsWith("Id", StringComparison.Ordinal) ? lowered[..^2]
                : null;

            if (prefix is not null && Guid.TryParseExact(value.Trim(), "D", out Guid guid))
                guidProperties.Add((prefix, guid));
        }

        foreach ((string prefix, Guid guid) in guidProperties)
        {
            string? name = prefix.Length == 0
                ? FirstPresent(strings, GenericNameProperties)
                : FirstPresent(strings, prefix + "name", prefix + "label", prefix + "title", prefix + "filename")
                  ?? (guidProperties.Count == 1 ? FirstPresent(strings, GenericNameProperties) : null);

            if (name is not null)
                RememberName(guid, name);
        }
    }

    private static string? FirstPresent(Dictionary<string, string> strings, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (strings.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private void RememberName(Guid id, string rawName)
    {
        string? name = CleanName(rawName);
        if (name is null)
            return;

        if (_names.Count >= MaxRememberedNames && !_names.ContainsKey(id))
            return;

        _names[id] = name;
    }

    private void RememberToken(string token)
    {
        string trimmed = token.Trim();
        if (trimmed.Length < MinTokenLength || _tokens.Count >= MaxRememberedTokens)
            return;

        _tokens.TryAdd(trimmed, 0);
    }

    /// <summary>
    /// A name fit to print: single line, no curly quotes (they are the delimiters), bounded, and never an
    /// identifier itself — a name that holds a GUID would put that GUID straight back into the reply.
    /// </summary>
    private static string? CleanName(string rawName)
    {
        var sb = new StringBuilder(Math.Min(rawName.Length, MaxNameLength + 1));
        bool lastWasSpace = true;
        foreach (char c in rawName)
        {
            char mapped = c is '“' or '”' ? '"' : c;
            if (char.IsControl(mapped) || char.IsWhiteSpace(mapped))
            {
                if (!lastWasSpace)
                    sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(mapped);
            lastWasSpace = false;
            if (sb.Length > MaxNameLength + 1)
                break;
        }

        string name = sb.ToString().Trim();
        if (name.Length == 0)
            return null;

        if (name.Length > MaxNameLength)
            name = name[..MaxNameLength].TrimEnd() + "…";

        try
        {
            return GuidInName.IsMatch(name) ? null : name;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Sanitizing
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns <paramref name="reply"/> with every identifier and remembered token dealt with as described on
    /// the type. The very same instance comes back when there is nothing to do. Never throws.
    /// </summary>
    public string Sanitize(string reply)
    {
        if (string.IsNullOrEmpty(reply))
            return reply;

        try
        {
            string current = reply;

            List<Edit> tokenEdits = FindTokenEdits(current);
            if (tokenEdits.Count > 0)
                current = Rewrite(current, tokenEdits);

            List<Edit> guidEdits = FindGuidEdits(current);
            if (guidEdits.Count > 0)
                current = Rewrite(current, guidEdits);

            return current;
        }
        catch (Exception)
        {
            return reply;
        }
    }

    /// <summary>One span to remove (<see cref="Replacement"/> null) or to replace by a name.</summary>
    private readonly record struct Edit(int Start, int End, string? Replacement, string Lead, string? Name);

    private List<Edit> FindTokenEdits(string text)
    {
        var edits = new List<Edit>();
        if (_tokens.IsEmpty)
            return edits;

        foreach (string token in _tokens.Keys.OrderByDescending(t => t.Length))
        {
            int from = 0;
            while (from < text.Length)
            {
                int index = text.IndexOf(token, from, StringComparison.Ordinal);
                if (index < 0)
                    break;

                int start = index;
                int end = index + token.Length;
                if (start > 0 && text[start - 1] == '`' && end < text.Length && text[end] == '`')
                {
                    start--;
                    end++;
                }

                while (start > 0 && (text[start - 1] == ' ' || text[start - 1] == '\t'))
                    start--;

                edits.Add(new Edit(start, end, null, string.Empty, null));
                from = index + token.Length;
            }
        }

        return NonOverlapping(edits);
    }

    private List<Edit> FindGuidEdits(string text)
    {
        var edits = new List<Edit>();
        MatchCollection matches = GuidRegex.Matches(text);
        if (matches.Count == 0)
            return edits;

        List<(int Start, int End)> protectedRanges = LinkTargetRanges(text);

        foreach (Match match in matches)
        {
            Group guidGroup = match.Groups["g1"].Success ? match.Groups["g1"]
                : match.Groups["g2"].Success ? match.Groups["g2"]
                : match.Groups["g3"];

            if (protectedRanges.Any(r => guidGroup.Index >= r.Start && guidGroup.Index < r.End))
                continue;

            string lead = match.Groups["pre"].Value;

            // Wrapped in brackets: an annotation, never part of the sentence. Gone entirely.
            if (match.Groups["open"].Success)
            {
                edits.Add(new Edit(match.Index, match.Index + match.Length, null, lead, null));
                continue;
            }

            if (Guid.TryParse(guidGroup.Value, out Guid id) && _names.TryGetValue(id, out string? name))
            {
                edits.Add(new Edit(match.Index, match.Index + match.Length, "“" + name + "”", lead, name));
                continue;
            }

            edits.Add(new Edit(match.Index, match.Index + match.Length, null, lead, null));
        }

        return edits;
    }

    private static List<(int Start, int End)> LinkTargetRanges(string text)
    {
        var ranges = new List<(int, int)>();
        foreach (Match match in MarkdownLinkTarget.Matches(text))
        {
            Group target = match.Groups["target"];
            ranges.Add((target.Index, target.Index + target.Length));
        }

        return ranges;
    }

    private static List<Edit> NonOverlapping(List<Edit> edits)
    {
        var sorted = edits.OrderBy(e => e.Start).ThenByDescending(e => e.End - e.Start).ToList();
        var result = new List<Edit>(sorted.Count);
        int end = -1;
        foreach (Edit edit in sorted)
        {
            if (edit.Start < end)
                continue;
            result.Add(edit);
            end = edit.End;
        }

        return result;
    }

    private static string Rewrite(string input, List<Edit> edits)
    {
        var sb = new StringBuilder(input.Length);
        int last = 0;

        foreach (Edit edit in edits)
        {
            // An earlier removal may have swallowed this edit's leading spaces (the separator after a removed
            // first list item); what is left of it is still to be dealt with.
            if (edit.End <= last)
                continue;

            int start = Math.Max(edit.Start, last);
            sb.Append(input, last, start - last);
            last = edit.End;
            string lead = start == edit.Start ? edit.Lead : string.Empty;

            string? replacement = edit.Replacement;
            if (replacement is not null && edit.Name is not null && EndsWithName(sb, edit.Name))
            {
                // The name already stands right before the identifier: the identifier was an annotation.
                StripTrailingSeparator(sb);
                replacement = null;
            }

            if (replacement is not null)
            {
                sb.Append(lead).Append(replacement);
                continue;
            }

            last = TidyAfterRemoval(input, sb, last);
        }

        sb.Append(input, last, input.Length - last);
        return sb.ToString();
    }

    private static bool EndsWithName(StringBuilder sb, string name)
    {
        int tail = Math.Min(sb.Length, name.Length + 16);
        string before = sb.ToString(sb.Length - tail, tail).TrimEnd(' ', '\t', '*', '_', '"', '\'', '“', '”', '‘', '’', ':', '-', '–', '—', '(', '[', '`');
        return before.Length >= name.Length && before.EndsWith(name, StringComparison.OrdinalIgnoreCase);
    }

    private static void StripTrailingSeparator(StringBuilder sb)
    {
        int length = sb.Length;
        while (length > 0 && (sb[length - 1] == ' ' || sb[length - 1] == '\t'))
            length--;

        if (length > 0 && sb[length - 1] is ':' or '-' or '–' or '—')
        {
            length--;
            while (length > 0 && (sb[length - 1] == ' ' || sb[length - 1] == '\t'))
                length--;
            sb.Length = length;
        }
    }

    /// <summary>
    /// Cleans up around a removed span: drops a bullet or line the removal emptied, an empty bracket pair, an
    /// emphasis pair with nothing left in it, a comma that now dangles, and a doubled space. Returns where the
    /// copy of the input should resume (it may skip separators that belonged to the removed item).
    /// </summary>
    private static int TidyAfterRemoval(string input, StringBuilder sb, int next)
    {
        int length = input.Length;

        int lineStart = 0;
        for (int i = sb.Length - 1; i >= 0; i--)
        {
            if (sb[i] == '\n')
            {
                lineStart = i + 1;
                break;
            }
        }

        int lineEnd = input.IndexOf('\n', next);
        if (lineEnd < 0)
            lineEnd = length;

        string before = sb.ToString(lineStart, sb.Length - lineStart);
        string after = input.Substring(next, lineEnd - next);

        if (IsBulletOnly(before) && IsPunctuationOnly(after))
        {
            sb.Length = lineStart;
            if (lineEnd < length)
            {
                int resume = lineEnd + 1;

                // The removed line stood between two blank lines: keep one blank line, not two.
                if (resume < length && input[resume] == '\n' && EndsWithBlankLine(sb))
                    resume++;
                return resume;
            }

            if (lineStart > 0 && sb[lineStart - 1] == '\n')
                sb.Length = lineStart - 1;
            return length;
        }

        int p = next;
        while (p < lineEnd && (input[p] == ' ' || input[p] == '\t'))
            p++;
        char following = p < lineEnd ? input[p] : '\0';
        char previous = LastNonSpace(sb, lineStart);

        if ((IsOpener(previous) || previous == ':' || IsBulletOnly(before)) && (following == ',' || following == ';'))
        {
            // The first item of a list was removed: "(first, second)" -> "(second)", "- first, second" ->
            // "- second", "Runs: first, second" -> "Runs: second". Only an opening bracket or the start of a
            // line wants the spaces after the comma gone; elsewhere they separate the next item.
            bool dropSpaces = IsOpener(previous) || before.Trim().Length == 0;
            p++;
            if (dropSpaces)
            {
                while (p < lineEnd && (input[p] == ' ' || input[p] == '\t'))
                    p++;
            }

            TrimTrailingSpaces(sb, lineStart);
            return p;
        }

        if (IsOpener(previous) && IsMatchingCloser(previous, following))
        {
            TrimTrailingSpaces(sb, lineStart);
            sb.Length--;
            return p + 1;
        }

        if (previous == ':' && following != '\0' && ".,;!?".IndexOf(following) >= 0)
        {
            // "It failed: <id>." -> "It failed." (a colon at the end of a line stays: it introduces a list)
            TrimTrailingSpaces(sb, lineStart);
            sb.Length--;
            return next;
        }

        if ((previous == ',' || previous == ';') && (following == '\0' || ".,;:!?)]}".IndexOf(following) >= 0))
        {
            TrimTrailingSpaces(sb, lineStart);
            sb.Length--;
            TrimTrailingSpaces(sb, lineStart);
            return next;
        }

        foreach (string marker in new[] { "**", "__", "~~", "*", "_" })
        {
            if (sb.Length >= marker.Length
                && sb.ToString(sb.Length - marker.Length, marker.Length) == marker
                && string.CompareOrdinal(input, next, marker, 0, marker.Length) == 0)
            {
                sb.Length -= marker.Length;
                next += marker.Length;
                break;
            }
        }

        if (next < length && (input[next] == ' ' || input[next] == '\t'))
        {
            if (sb.Length == 0 || sb[^1] == '\n')
            {
                while (next < length && (input[next] == ' ' || input[next] == '\t'))
                    next++;
            }
            else if (sb[^1] == ' ')
            {
                next++;
            }
        }

        return next;
    }

    private static bool EndsWithBlankLine(StringBuilder sb) =>
        sb.Length == 0 || (sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '\n');

    private static char LastNonSpace(StringBuilder sb, int lineStart)
    {
        for (int i = sb.Length - 1; i >= lineStart; i--)
        {
            if (sb[i] != ' ' && sb[i] != '\t')
                return sb[i];
        }

        return '\0';
    }

    private static void TrimTrailingSpaces(StringBuilder sb, int lineStart)
    {
        while (sb.Length > lineStart && (sb[^1] == ' ' || sb[^1] == '\t'))
            sb.Length--;
    }

    private static bool IsOpener(char c) => c is '(' or '[' or '{';

    private static bool IsMatchingCloser(char opener, char c) =>
        (opener == '(' && c == ')') || (opener == '[' && c == ']') || (opener == '{' && c == '}');

    private static bool IsBulletOnly(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return true;
        if (trimmed is "-" or "*" or "+" or "•")
            return true;

        // "1." / "12)" ordered-list markers
        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
            digits++;
        return digits > 0 && digits == trimmed.Length - 1 && trimmed[^1] is '.' or ')';
    }

    private static bool IsPunctuationOnly(string text)
    {
        foreach (char c in text)
        {
            if (c is not (' ' or '\t' or ',' or '.' or ';' or ':'))
                return false;
        }

        return true;
    }
}
