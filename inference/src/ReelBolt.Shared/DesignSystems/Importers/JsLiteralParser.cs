using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// One value or key the restricted literal reader could not understand, with the dotted path it
/// appeared at. <see cref="Code"/> is one of <c>TAILWIND_DYNAMIC_VALUE</c>, <c>TOO_DEEP</c>,
/// <c>TRUNCATED</c> or <c>PARSE_FAILED</c>.
/// </summary>
public sealed record JsLiteralIssue(string Code, string Path);

/// <summary>The root value read from a source, plus everything skipped on the way.</summary>
public sealed record JsLiteralParseResult(JsonNode? Root, IReadOnlyList<JsLiteralIssue> Issues);

/// <summary>
/// A pure, hand-written, single-pass reader of the restricted JavaScript literal subset a Tailwind
/// v3 config uses. It reads the source as inert text and never executes anything: no module
/// loading, no dynamic code compilation, no process, no reflection, no IO, no regular expressions.
/// Anything outside the subset is omitted and reported through <see cref="JsLiteralIssue"/>; the
/// reader never throws.
/// </summary>
public static class JsLiteralParser
{
    /// <summary>The largest source accepted, in characters.</summary>
    public const int MaxBytes = 262144;

    /// <summary>The deepest JS nesting accepted before the rest is skipped iteratively.</summary>
    public const int MaxDepth = 12;

    /// <summary>The largest number of values and keys read from one source.</summary>
    public const int MaxTokens = 100_000;

    /// <summary>Reads <paramref name="text"/> as an inert JS literal. Never throws on hostile input.</summary>
    public static JsLiteralParseResult Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new JsLiteralParseResult(null, Array.Empty<JsLiteralIssue>());

        if (text.Length > MaxBytes)
            return new JsLiteralParseResult(null, new[] { new JsLiteralIssue("PARSE_FAILED", string.Empty) });

        string stripped = StripComments(text).Trim();
        if (stripped.Length == 0)
            return new JsLiteralParseResult(null, Array.Empty<JsLiteralIssue>());

        stripped = UnwrapAssignment(stripped);

        if (TryUnwrapDefineConfig(stripped, out string inner, out bool dynamicCall))
        {
            if (dynamicCall)
                return new JsLiteralParseResult(null, new[] { new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", string.Empty) });

            stripped = inner;
        }

        stripped = StripTrailingSatisfies(stripped).Trim();
        if (stripped.EndsWith(";", StringComparison.Ordinal))
            stripped = stripped[..^1].TrimEnd();

        if (stripped.Length == 0)
            return new JsLiteralParseResult(null, Array.Empty<JsLiteralIssue>());

        var scanner = new Scanner(stripped);
        JsonNode? root = scanner.Run(out IReadOnlyList<JsLiteralIssue> issues);
        return new JsLiteralParseResult(root, issues);
    }

    /// <summary>Removes <c>//</c> line and <c>/* */</c> block comments without touching string bodies.</summary>
    private static string StripComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        int i = 0;
        char quote = '\0';

        while (i < text.Length)
        {
            char c = text[i];

            if (quote != '\0')
            {
                builder.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    builder.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == quote)
                    quote = '\0';

                i++;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                builder.Append(c);
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i += 2;
                    while (i < text.Length && text[i] != '\n')
                        i++;
                    continue;
                }

                if (text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                        i++;

                    i = Math.Min(i + 2, text.Length);
                    continue;
                }
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>Strips <c>module.exports =</c> / <c>exports.default =</c> / <c>export default</c> prefixes.</summary>
    private static string UnwrapAssignment(string source)
    {
        string[] assignmentPrefixes = ["module.exports.default", "module.exports", "exports.default"];
        foreach (string prefix in assignmentPrefixes)
        {
            if (!source.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            int i = prefix.Length;
            while (i < source.Length && char.IsWhiteSpace(source[i]))
                i++;

            if (i >= source.Length || source[i] != '=')
                continue;

            if (i + 1 < source.Length && (source[i + 1] == '=' || source[i + 1] == '>'))
                continue;

            i++;
            while (i < source.Length && char.IsWhiteSpace(source[i]))
                i++;

            return source[i..];
        }

        const string exportDefault = "export default";
        if (source.StartsWith(exportDefault, StringComparison.Ordinal))
        {
            int i = exportDefault.Length;
            if (i >= source.Length || char.IsWhiteSpace(source[i]) || source[i] is '{' or '[')
            {
                while (i < source.Length && char.IsWhiteSpace(source[i]))
                    i++;

                return source[i..];
            }
        }

        return source;
    }

    /// <summary>Unwraps a single-argument <c>defineConfig(<arg>)</c> / <c>config(<arg>)</c> call.</summary>
    private static bool TryUnwrapDefineConfig(string source, out string inner, out bool dynamicCall)
    {
        inner = source;
        dynamicCall = false;

        string? name = null;
        if (source.StartsWith("defineConfig", StringComparison.Ordinal))
            name = "defineConfig";
        else if (source.StartsWith("config", StringComparison.Ordinal))
            name = "config";

        if (name is null)
            return false;

        int open = name.Length;
        while (open < source.Length && char.IsWhiteSpace(source[open]))
            open++;

        if (open >= source.Length || source[open] != '(')
            return false;

        int close = FindMatchingParen(source, open);
        if (close < 0)
        {
            dynamicCall = true;
            return true;
        }

        string arguments = source[(open + 1)..close].Trim();
        if (arguments.Length == 0 || arguments[0] is not ('{' or '[') || HasTopLevelComma(arguments))
        {
            dynamicCall = true;
            return true;
        }

        inner = arguments;
        return true;
    }

    private static int FindMatchingParen(string source, int open)
    {
        int depth = 0;
        char quote = '\0';

        for (int i = open; i < source.Length; i++)
        {
            char c = source[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == quote)
                    quote = '\0';

                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                continue;
            }

            if (c == '(')
                depth++;
            else if (c == ')' && --depth == 0)
                return i;
        }

        return -1;
    }

    private static bool HasTopLevelComma(string arguments)
    {
        int depth = 0;
        char quote = '\0';

        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == quote)
                    quote = '\0';

                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                continue;
            }

            if (c is '{' or '[' or '(')
            {
                depth++;
                continue;
            }

            if (c is '}' or ']' or ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && c == ',')
                return arguments[(i + 1)..].Trim().Length > 0;
        }

        return false;
    }

    /// <summary>Drops a trailing <c>satisfies Type</c> clause at depth zero.</summary>
    private static string StripTrailingSatisfies(string source)
    {
        int depth = 0;
        int cut = -1;
        char quote = '\0';

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == quote)
                    quote = '\0';

                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                continue;
            }

            if (c is '{' or '[' or '(')
            {
                depth++;
                continue;
            }

            if (c is '}' or ']' or ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && c == 's' && MatchesWord(source, i, "satisfies"))
                cut = i;
        }

        return cut < 0 ? source : source[..cut].TrimEnd();
    }

    private static bool MatchesWord(string source, int index, string word)
    {
        if (index + word.Length > source.Length)
            return false;

        if (string.CompareOrdinal(source, index, word, 0, word.Length) != 0)
            return false;

        if (index > 0 && IsIdentifierPart(source[index - 1]))
            return false;

        int after = index + word.Length;
        return after >= source.Length || !IsIdentifierPart(source[after]);
    }

    private static bool IsIdentifierStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c is '_' or '$';

    private static bool IsIdentifierPart(char c) =>
        IsIdentifierStart(c) || (c >= '0' && c <= '9');

    /// <summary>Recursive-descent reader over one comment-free expression.</summary>
    private sealed class Scanner
    {
        private readonly string _s;
        private readonly List<JsLiteralIssue> _issues = [];
        private int _pos;
        private int _depth;
        private int _tokens;
        private bool _tooDeep;
        private bool _truncated;
        private bool _failed;

        public Scanner(string source)
        {
            _s = source;
        }

        public JsonNode? Run(out IReadOnlyList<JsLiteralIssue> issues)
        {
            SkipWhitespace();
            if (_pos >= _s.Length)
            {
                issues = _issues;
                return null;
            }

            bool parsed = TryParseValue(string.Empty, out JsonNode? value);
            if (parsed)
            {
                SkipWhitespace();
                if (_pos < _s.Length && _s[_pos] == ';')
                {
                    _pos++;
                    SkipWhitespace();
                }

                if (_pos < _s.Length && !_tooDeep && !_truncated)
                    _failed = true;
            }

            if (_failed)
                _issues.Add(new JsLiteralIssue("PARSE_FAILED", string.Empty));

            issues = _issues;
            return _failed ? null : value;
        }

        private bool TryParseValue(string path, out JsonNode? value)
        {
            value = null;
            SkipWhitespace();

            if (_truncated || _tooDeep || _pos >= _s.Length)
            {
                if (_pos >= _s.Length && !_tooDeep && !_truncated)
                    _failed = true;

                return false;
            }

            if (!CountToken())
                return false;

            char c = _s[_pos];
            switch (c)
            {
                case '{':
                    value = ParseObject(path);
                    return value is not null;

                case '[':
                    value = ParseArray(path);
                    return value is not null;

                case '\'':
                case '"':
                    if (!TryParseString(out string literal))
                    {
                        _failed = true;
                        return false;
                    }

                    value = JsonValue.Create(literal);
                    return true;

                case '`':
                    if (!TryParseTemplate(out string template, out bool dynamic))
                    {
                        _failed = true;
                        return false;
                    }

                    if (dynamic)
                    {
                        _issues.Add(new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", path));
                        return false;
                    }

                    value = JsonValue.Create(template);
                    return true;

                default:
                    if (c is '-' or '+' || (c >= '0' && c <= '9') || (c == '.' && _pos + 1 < _s.Length && _s[_pos + 1] >= '0' && _s[_pos + 1] <= '9'))
                        return TryParseNumber(path, out value);

                    if (IsIdentifierStart(c))
                    {
                        string identifier = ReadIdentifier();
                        switch (identifier)
                        {
                            case "true":
                                value = JsonValue.Create(true);
                                return true;
                            case "false":
                                value = JsonValue.Create(false);
                                return true;
                            case "null":
                                return true;
                            default:
                                _issues.Add(new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", path));
                                SkipDynamicRest();
                                return false;
                        }
                    }

                    if (c == '(')
                    {
                        _issues.Add(new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", path));
                        SkipDynamicRest();
                        return false;
                    }

                    _failed = true;
                    return false;
            }
        }

        private JsonNode? ParseObject(string path)
        {
            if (_depth >= MaxDepth)
            {
                ReportTooDeep(path);
                BalancedSkip();
                return null;
            }

            _depth++;
            _pos++;
            var result = new JsonObject();

            while (true)
            {
                SkipWhitespace();
                if (_pos >= _s.Length)
                {
                    if (!_tooDeep && !_truncated)
                        _failed = true;

                    break;
                }

                char c = _s[_pos];
                if (c == '}')
                {
                    _pos++;
                    break;
                }

                if (c == ',')
                {
                    _pos++;
                    continue;
                }

                if (c == '.' && Peek(1) == '.' && Peek(2) == '.')
                {
                    _pos += 3;
                    if (!CountToken())
                        break;

                    _issues.Add(new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", Child(path, "{spread}")));
                    SkipDynamicRest();
                    continue;
                }

                if (!CountToken())
                    break;

                if (!TryParseKey(out string key, out bool dynamicKey))
                {
                    // A bare `{`/`[` where a key belongs (a pathological, brace-only payload) still
                    // counts toward the depth budget, so the cap — not a stack overflow — answers it.
                    if (_pos < _s.Length && _s[_pos] is '{' or '[')
                    {
                        TryParseValue(path, out _);
                        continue;
                    }

                    _failed = true;
                    break;
                }

                string childPath = Child(path, key);
                SkipWhitespace();
                if (_pos >= _s.Length)
                {
                    if (!_tooDeep && !_truncated)
                        _failed = true;

                    break;
                }

                char separator = _s[_pos];

                if (separator == ':')
                {
                    _pos++;
                    if (dynamicKey)
                    {
                        _issues.Add(new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", childPath));
                        SkipDynamicRest();
                        continue;
                    }

                    if (TryParseValue(childPath, out JsonNode? item))
                        result[key] = item;

                    continue;
                }

                if (separator == '(')
                {
                    _issues.Add(new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", childPath));
                    BalancedSkip();
                    SkipWhitespace();
                    if (_pos < _s.Length && _s[_pos] == '{')
                        BalancedSkip();

                    continue;
                }

                if (separator == '=' && Peek(1) != '=' && Peek(1) != '>')
                {
                    _issues.Add(new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", childPath));
                    _pos++;
                    SkipDynamicRest();
                    continue;
                }

                _issues.Add(new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", childPath));
            }

            _depth--;
            return result;
        }

        private JsonNode? ParseArray(string path)
        {
            if (_depth >= MaxDepth)
            {
                ReportTooDeep(path);
                BalancedSkip();
                return null;
            }

            _depth++;
            _pos++;
            var result = new JsonArray();
            int index = 0;

            while (true)
            {
                SkipWhitespace();
                if (_pos >= _s.Length)
                {
                    if (!_tooDeep && !_truncated)
                        _failed = true;

                    break;
                }

                char c = _s[_pos];
                if (c == ']')
                {
                    _pos++;
                    break;
                }

                if (c == ',')
                {
                    _pos++;
                    continue;
                }

                if (c == '.' && Peek(1) == '.' && Peek(2) == '.')
                {
                    _pos += 3;
                    if (!CountToken())
                        break;

                    _issues.Add(new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]"));
                    SkipDynamicRest();
                    index++;
                    continue;
                }

                int before = _pos;
                if (TryParseValue(path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", out JsonNode? item))
                    result.Add(item);
                else if (_pos == before)
                {
                    // A failed element that consumed nothing (stray character, token cap, too-deep latch)
                    // would be retried forever; the parse is already marked failed/truncated, so stop.
                    if (!_tooDeep && !_truncated)
                        _failed = true;

                    break;
                }

                index++;
            }

            _depth--;
            return result;
        }

        private bool TryParseKey(out string key, out bool dynamicKey)
        {
            key = string.Empty;
            dynamicKey = false;
            SkipWhitespace();

            if (_pos >= _s.Length)
                return false;

            char c = _s[_pos];
            if (c == '[')
            {
                dynamicKey = true;
                key = "computed";
                BalancedSkip();
                return true;
            }

            if (c is '\'' or '"')
                return TryParseString(out key);

            if (c == '`')
            {
                if (!TryParseTemplate(out key, out bool dynamic))
                    return false;

                dynamicKey = dynamic;
                return true;
            }

            if (c is '-' or '+' || (c >= '0' && c <= '9') || c == '.')
            {
                int start = _pos;
                if (c is '-' or '+')
                    _pos++;

                while (_pos < _s.Length && ((_s[_pos] >= '0' && _s[_pos] <= '9') || _s[_pos] == '.'))
                    _pos++;

                key = _s[start.._pos];
                return key.Length > 0;
            }

            if (IsIdentifierStart(c))
            {
                key = ReadIdentifier();
                return key.Length > 0;
            }

            return false;
        }

        private bool TryParseString(out string result)
        {
            result = string.Empty;
            char quote = _s[_pos];
            _pos++;
            var builder = new StringBuilder();

            while (_pos < _s.Length)
            {
                char c = _s[_pos++];
                if (c == quote)
                {
                    result = builder.ToString();
                    return true;
                }

                if (c == '\\' && _pos < _s.Length)
                {
                    char escape = _s[_pos++];
                    switch (escape)
                    {
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case '"':
                            builder.Append('"');
                            break;
                        case '\'':
                            builder.Append('\'');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'u':
                            if (_pos + 4 <= _s.Length && TryHex4(_s, _pos, out char unicode))
                            {
                                builder.Append(unicode);
                                _pos += 4;
                            }
                            else
                            {
                                builder.Append('u');
                            }

                            break;
                        default:
                            builder.Append(escape);
                            break;
                    }

                    continue;
                }

                builder.Append(c);
            }

            return false;
        }

        private bool TryParseTemplate(out string result, out bool dynamic)
        {
            result = string.Empty;
            dynamic = false;
            _pos++;
            var builder = new StringBuilder();

            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (c == '\\' && _pos + 1 < _s.Length)
                {
                    builder.Append(c);
                    builder.Append(_s[_pos + 1]);
                    _pos += 2;
                    continue;
                }

                if (c == '`')
                {
                    _pos++;
                    result = builder.ToString();
                    return true;
                }

                if (c == '$' && _pos + 1 < _s.Length && _s[_pos + 1] == '{')
                    dynamic = true;

                builder.Append(c);
                _pos++;
            }

            return false;
        }

        private bool TryParseNumber(string path, out JsonNode? value)
        {
            value = null;
            int start = _pos;
            bool floating = false;

            if (_s[_pos] is '-' or '+')
                _pos++;

            while (_pos < _s.Length && _s[_pos] >= '0' && _s[_pos] <= '9')
                _pos++;

            if (_pos < _s.Length && _s[_pos] == '.')
            {
                floating = true;
                _pos++;
                while (_pos < _s.Length && _s[_pos] >= '0' && _s[_pos] <= '9')
                    _pos++;
            }

            if (_pos < _s.Length && (_s[_pos] == 'e' || _s[_pos] == 'E'))
            {
                floating = true;
                _pos++;
                if (_pos < _s.Length && (_s[_pos] == '+' || _s[_pos] == '-'))
                    _pos++;

                while (_pos < _s.Length && _s[_pos] >= '0' && _s[_pos] <= '9')
                    _pos++;
            }

            string token = _s[start.._pos];
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            {
                _issues.Add(new JsLiteralIssue("TAILWIND_DYNAMIC_VALUE", path));
                return false;
            }

            if (!floating && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                value = JsonValue.Create(integer);
            else
                value = JsonValue.Create(number);

            return true;
        }

        private bool CountToken()
        {
            if (_tokens >= MaxTokens)
            {
                if (!_truncated)
                {
                    _truncated = true;
                    _issues.Add(new JsLiteralIssue("TRUNCATED", string.Empty));
                }

                return false;
            }

            _tokens++;
            return true;
        }

        private void ReportTooDeep(string path)
        {
            if (_tooDeep)
                return;

            _tooDeep = true;
            _issues.Add(new JsLiteralIssue("TOO_DEEP", path));
        }

        private void SkipDynamicRest()
        {
            int depth = 0;
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (c is '\'' or '"')
                {
                    SkipStringBody(c);
                    continue;
                }

                if (c == '`')
                {
                    SkipTemplateBody();
                    continue;
                }

                if (c is '{' or '[' or '(')
                {
                    depth++;
                    _pos++;
                    continue;
                }

                if (c is '}' or ']' or ')')
                {
                    if (depth == 0)
                        return;

                    depth--;
                    _pos++;
                    continue;
                }

                if (c == ',' && depth == 0)
                    return;

                _pos++;
            }
        }

        /// <summary>Consumes one balanced <c>{…}</c>, <c>[…]</c> or <c>(…)</c> starting at the current position.</summary>
        private void BalancedSkip()
        {
            if (_pos >= _s.Length || _s[_pos] is not ('{' or '[' or '('))
                return;

            int depth = 0;
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (c is '\'' or '"')
                {
                    SkipStringBody(c);
                    continue;
                }

                if (c == '`')
                {
                    SkipTemplateBody();
                    continue;
                }

                if (c is '{' or '[' or '(')
                {
                    depth++;
                    _pos++;
                    continue;
                }

                if (c is '}' or ']' or ')')
                {
                    depth--;
                    _pos++;
                    if (depth <= 0)
                        return;

                    continue;
                }

                _pos++;
            }
        }

        private void SkipStringBody(char quote)
        {
            _pos++;
            while (_pos < _s.Length)
            {
                char c = _s[_pos++];
                if (c == '\\')
                {
                    _pos++;
                    continue;
                }

                if (c == quote)
                    return;
            }
        }

        private void SkipTemplateBody()
        {
            _pos++;
            while (_pos < _s.Length)
            {
                char c = _s[_pos++];
                if (c == '\\')
                {
                    _pos++;
                    continue;
                }

                if (c == '`')
                    return;
            }
        }

        private string ReadIdentifier()
        {
            int start = _pos;
            _pos++;
            while (_pos < _s.Length && IsIdentifierPart(_s[_pos]))
                _pos++;

            return _s[start.._pos];
        }

        private void SkipWhitespace()
        {
            while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos]))
                _pos++;
        }

        private char Peek(int offset)
        {
            int index = _pos + offset;
            return index >= 0 && index < _s.Length ? _s[index] : '\0';
        }

        private static string Child(string path, string key) =>
            path.Length == 0 ? key : path + "." + key;

        private static bool TryHex4(string source, int start, out char value)
        {
            value = '\0';
            if (start + 4 > source.Length)
                return false;

            int code = 0;
            for (int i = 0; i < 4; i++)
            {
                int digit = HexValue(source[start + i]);
                if (digit < 0)
                    return false;

                code = (code << 4) | digit;
            }

            value = (char)code;
            return true;
        }

        private static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }
}
