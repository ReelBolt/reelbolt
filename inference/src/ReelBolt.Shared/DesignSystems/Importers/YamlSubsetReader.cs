using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// A small, hand-written reader for the YAML subset a DESIGN.md front matter uses: indentation-based
/// block maps and lists, scalars, single- and double-quoted strings, <c>[a, b]</c> inline lists,
/// comments and <c>|</c>/<c>&gt;</c> block scalars. It deliberately refuses YAML's footguns — anchors
/// (<c>&amp;</c>), aliases (<c>*</c>), tags (<c>!</c>) and merge keys (<c>&lt;&lt;</c>) — and rejects
/// a document over 512 KiB, a nesting deeper than 8 or more than 5000 nodes. Pure and total: it
/// never throws and answers null with an <see cref="ImportDiagnostic"/> instead.
/// </summary>
public static class YamlSubsetReader
{
    /// <summary>The largest document accepted, in UTF-8 bytes.</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>The deepest block nesting accepted.</summary>
    public const int MaxDepth = 8;

    /// <summary>The largest number of nodes accepted.</summary>
    public const int MaxNodes = 5000;

    /// <summary>The largest block scalar accepted, in UTF-8 bytes.</summary>
    public const int MaxBlockScalarBytes = 8 * 1024;

    /// <summary>Parses <paramref name="text"/>; null when it is not in the subset.</summary>
    public static JsonNode? Parse(string text) => Parse(text, out _);

    /// <summary>Parses <paramref name="text"/>, reporting the first reason it was refused.</summary>
    public static JsonNode? Parse(string text, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        try
        {
            if (string.IsNullOrWhiteSpace(text))
                return new JsonObject();

            if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            {
                diagnostic = Error("YAML_TOO_LARGE", $"document is over the {MaxBytes} byte limit");
                return null;
            }

            var parser = new Parser(text);
            JsonNode? result = parser.Run();
            if (parser.Error is not null)
            {
                diagnostic = parser.Error;
                return null;
            }

            return result;
        }
        catch (Exception exception)
        {
            diagnostic = Error("YAML_INTERNAL", "parser failed: " + exception.GetType().Name);
            return null;
        }
    }

    private static ImportDiagnostic Error(string code, string message) =>
        new(ImportDiagnosticSeverity.Error, code, string.Empty, message);

    private sealed class Parser
    {
        private readonly List<Line> _lines;
        private readonly int _count;
        private int _index;
        private int _nodes;
        private ImportDiagnostic? _error;

        public Parser(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] raw = normalized.Split('\n');
            _lines = new List<Line>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                string line = raw[i];
                int indent = LeadingWhitespace(line);
                _lines.Add(new Line
                {
                    Indent = indent,
                    Text = line[indent..],
                    Raw = line,
                    Number = i + 1,
                });
            }

            _count = _lines.Count;
        }

        public ImportDiagnostic? Error => _error;

        public JsonNode? Run()
        {
            if (!MoveToContent())
                return new JsonObject();

            int indent = _lines[_index].Indent;
            JsonNode? result = ParseBlock(indent, 0);
            if (_error is not null)
                return null;

            if (MoveToContent())
            {
                Reject("YAML_TRAILING", "unexpected content after the document", Current());
                return null;
            }

            return result;
        }

        private JsonNode? ParseBlock(int indent, int depth)
        {
            if (depth > MaxDepth)
            {
                Reject("YAML_TOO_DEEP", $"nesting exceeds the {MaxDepth} level limit", Current());
                return null;
            }

            if (!MoveToContent())
                return null;

            Line line = _lines[_index];
            if (line.Indent < indent)
                return null;

            string content = line.Text.TrimStart();
            return IsListMarker(content)
                ? ParseList(line.Indent, depth)
                : ParseMap(line.Indent, depth);
        }

        private JsonNode? ParseMap(int indent, int depth)
        {
            if (depth > MaxDepth)
            {
                Reject("YAML_TOO_DEEP", $"nesting exceeds the {MaxDepth} level limit", Current());
                return null;
            }

            if (!CountNode())
                return null;

            var map = new JsonObject();

            while (true)
            {
                if (!MoveToContent())
                    break;

                Line line = _lines[_index];
                if (line.Indent < indent)
                    break;

                string content = line.Text.TrimStart();
                if (IsListMarker(content))
                    break;

                if (line.Indent > indent)
                {
                    Reject("YAML_INDENT", "unexpected indentation", line);
                    return null;
                }

                int colon = FindKeyColon(content);
                if (colon < 0)
                {
                    Reject("YAML_KEY", "expected 'key: value'", line);
                    return null;
                }

                string key = content[..colon].Trim();
                if (key.Length == 0)
                {
                    Reject("YAML_KEY", "mapping key is empty", line);
                    return null;
                }

                if (key == "<<")
                {
                    Reject("YAML_MERGE_KEY", "merge keys are not supported", line);
                    return null;
                }

                char first = key[0];
                if (first is '&' or '*' or '!')
                {
                    Reject(KeyCode(first), "anchors, aliases and tags are not supported", line);
                    return null;
                }

                string remaining = content[(colon + 1)..].Trim();
                _index++;

                JsonNode? value;
                if (remaining.Length == 0)
                    value = ParseChildBlock(indent, depth + 1);
                else if (IsBlockScalarHeader(remaining))
                    value = ParseBlockScalar(indent, remaining);
                else
                    value = ParseInlineValue(remaining);

                if (_error is not null)
                    return null;

                map[key] = value;
            }

            return map;
        }

        private JsonNode? ParseList(int indent, int depth)
        {
            if (depth > MaxDepth)
            {
                Reject("YAML_TOO_DEEP", $"nesting exceeds the {MaxDepth} level limit", Current());
                return null;
            }

            if (!CountNode())
                return null;

            var list = new JsonArray();

            while (true)
            {
                if (!MoveToContent())
                    break;

                Line line = _lines[_index];
                if (line.Indent != indent)
                    break;

                string content = line.Text.TrimStart();
                if (!IsListMarker(content))
                    break;

                string rest = content.Length > 1 ? content[2..] : string.Empty;

                if (rest.Length == 0)
                {
                    _index++;
                    JsonNode? child = ParseChildBlock(indent, depth + 1);
                    if (_error is not null)
                        return null;

                    list.Add(child);
                    continue;
                }

                if (FindKeyColon(rest) >= 0)
                {
                    line.Text = rest;
                    line.Indent = indent + 2;
                    JsonNode? mapped = ParseMap(indent + 2, depth + 1);
                    if (_error is not null)
                        return null;

                    list.Add(mapped);
                    continue;
                }

                _index++;
                JsonNode? scalar = ParseInlineValue(rest);
                if (_error is not null)
                    return null;

                list.Add(scalar);
            }

            return list;
        }

        private JsonNode? ParseChildBlock(int parentIndent, int depth)
        {
            if (!MoveToContent())
                return null;

            Line line = _lines[_index];
            if (line.Indent < parentIndent)
                return null;

            if (line.Indent == parentIndent && !IsListMarker(line.Text.TrimStart()))
                return null;

            return ParseBlock(line.Indent, depth);
        }

        private JsonNode? ParseInlineValue(string text)
        {
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
                return null;

            return trimmed[0] == '[' ? ParseInlineList(trimmed) : ParseScalar(trimmed);
        }

        private JsonNode? ParseScalar(string token)
        {
            if (token.Length == 0)
                return null;

            char first = token[0];
            if (first is '&' or '*')
            {
                Reject(first == '&' ? "YAML_ANCHOR" : "YAML_ALIAS", "anchors and aliases are not supported", Current());
                return null;
            }

            if (first == '!')
            {
                Reject("YAML_TAG", "tags are not supported", Current());
                return null;
            }

            if (first is '\'' or '"')
                return ParseQuoted(token, first);

            if (first == '#' && TryReadHexColor(token, out string color))
            {
                if (!CountNode())
                    return null;

                return JsonValue.Create(color);
            }

            string value = StripTrailingComment(token).Trim();
            if (value.Length == 0)
                return null;

            char lead = value[0];
            if (lead is '&' or '*')
            {
                Reject(lead == '&' ? "YAML_ANCHOR" : "YAML_ALIAS", "anchors and aliases are not supported", Current());
                return null;
            }

            if (lead == '!')
            {
                Reject("YAML_TAG", "tags are not supported", Current());
                return null;
            }

            if (!CountNode())
                return null;

            return ScalarValue(value);
        }

        private JsonNode? ParseQuoted(string token, char quote)
        {
            var builder = new StringBuilder(token.Length - 1);

            for (int i = 1; i < token.Length; i++)
            {
                char c = token[i];
                if (c == quote)
                {
                    if (quote == '\'' && i + 1 < token.Length && token[i + 1] == '\'')
                    {
                        builder.Append('\'');
                        i++;
                        continue;
                    }

                    if (!CountNode())
                        return null;

                    return JsonValue.Create(builder.ToString());
                }

                if (quote == '"' && c == '\\' && i + 1 < token.Length)
                {
                    i++;
                    builder.Append(Unescape(token[i]));
                    continue;
                }

                builder.Append(c);
            }

            Reject("YAML_QUOTE", "unterminated quoted string", Current());
            return null;
        }

        private JsonNode? ParseInlineList(string token)
        {
            int close = FindClosingBracket(token);
            if (close < 0)
            {
                Reject("YAML_LIST", "unterminated inline list", Current());
                return null;
            }

            if (!CountNode())
                return null;

            var array = new JsonArray();
            foreach (string part in SplitTopLevel(token[1..close], ','))
            {
                string item = part.Trim();
                if (item.Length == 0)
                    continue;

                JsonNode? value = ParseScalar(item);
                if (_error is not null)
                    return null;

                array.Add(value);
            }

            return array;
        }

        private JsonNode? ParseBlockScalar(int parentIndent, string header)
        {
            if (!CountNode())
                return null;

            bool folded = header[0] == '>';
            char chomp = ' ';
            for (int i = 1; i < header.Length; i++)
            {
                if (header[i] is '-' or '+')
                    chomp = header[i];
            }

            var raw = new List<string>();
            while (_index < _count)
            {
                Line line = _lines[_index];
                if (string.IsNullOrWhiteSpace(line.Raw))
                {
                    raw.Add(string.Empty);
                    _index++;
                    continue;
                }

                if (line.Indent <= parentIndent)
                    break;

                raw.Add(line.Raw);
                _index++;
            }

            int contentIndent = int.MaxValue;
            foreach (string text in raw)
            {
                if (text.Length == 0)
                    continue;

                contentIndent = Math.Min(contentIndent, LeadingWhitespace(text));
            }

            if (contentIndent == int.MaxValue)
                contentIndent = parentIndent + 1;

            var stripped = new List<string>(raw.Count);
            foreach (string text in raw)
            {
                if (text.Length == 0)
                    stripped.Add(string.Empty);
                else
                    stripped.Add(text.Length > contentIndent ? text[contentIndent..] : string.Empty);
            }

            string content = folded ? Fold(stripped) : string.Join("\n", stripped);
            if (Encoding.UTF8.GetByteCount(content) > MaxBlockScalarBytes)
            {
                Reject("YAML_BLOCK_SCALAR_TOO_LARGE", $"block scalar is over the {MaxBlockScalarBytes} byte limit", Current());
                return null;
            }

            content = content.TrimEnd('\n');
            if (chomp != '-' && content.Length > 0)
                content += "\n";

            return JsonValue.Create(content);
        }

        private bool CountNode()
        {
            _nodes++;
            if (_nodes > MaxNodes)
            {
                Reject("YAML_TOO_MANY_NODES", $"document has more than {MaxNodes} nodes", Current());
                return false;
            }

            return true;
        }

        private bool MoveToContent()
        {
            while (_index < _count)
            {
                string trimmed = _lines[_index].Text.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                {
                    _index++;
                    continue;
                }

                return true;
            }

            return false;
        }

        private Line? Current() => _index < _count ? _lines[_index] : null;

        private void Reject(string code, string message, Line? line)
        {
            if (_error is not null)
                return;

            string detail = line is null
                ? message
                : $"{message} (line {line.Number.ToString(CultureInfo.InvariantCulture)})";
            _error = new ImportDiagnostic(ImportDiagnosticSeverity.Error, code, string.Empty, detail);
        }
    }

    private static bool IsListMarker(string content) =>
        content.Length == 1
            ? content[0] == '-'
            : content[0] == '-' && content[1] == ' ';

    private static bool IsBlockScalarHeader(string value)
    {
        if (value.Length == 0 || (value[0] != '|' && value[0] != '>'))
            return false;

        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (c is '-' or '+' || (c >= '0' && c <= '9'))
                continue;

            return false;
        }

        return true;
    }

    private static int FindKeyColon(string content)
    {
        char quote = '\0';
        int brackets = 0;

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (quote == '\'' && i + 1 < content.Length && content[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }

                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    continue;
                case '[':
                    brackets++;
                    continue;
                case ']':
                    if (brackets > 0)
                        brackets--;
                    continue;
                case ':' when brackets == 0 && (i + 1 == content.Length || char.IsWhiteSpace(content[i + 1])):
                    return i;
            }
        }

        return -1;
    }

    private static int FindClosingBracket(string token)
    {
        char quote = '\0';
        int depth = 0;

        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }

            if (c == '[')
            {
                depth++;
                continue;
            }

            if (c == ']')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var builder = new StringBuilder();
        char quote = '\0';
        int brackets = 0;

        foreach (char c in text)
        {
            if (quote != '\0')
            {
                builder.Append(c);
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                builder.Append(c);
                continue;
            }

            if (c == '[')
            {
                brackets++;
                builder.Append(c);
                continue;
            }

            if (c == ']')
            {
                if (brackets > 0)
                    brackets--;
                builder.Append(c);
                continue;
            }

            if (c == separator && brackets == 0)
            {
                parts.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(c);
        }

        parts.Add(builder.ToString());
        return parts;
    }

    private static string StripTrailingComment(string token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            if (token[i] == '#' && (i == 0 || char.IsWhiteSpace(token[i - 1])))
                return token[..i];
        }

        return token;
    }

    private static bool TryReadHexColor(string token, out string color)
    {
        color = string.Empty;

        int end = 0;
        while (end < token.Length && !char.IsWhiteSpace(token[end]) && token[end] != ',')
            end++;

        string candidate = token[..end];
        if (candidate.Length is not (4 or 5 or 7 or 9) || candidate[0] != '#')
            return false;

        for (int i = 1; i < candidate.Length; i++)
        {
            if (!Uri.IsHexDigit(candidate[i]))
                return false;
        }

        color = candidate;
        return true;
    }

    private static JsonNode? ScalarValue(string value)
    {
        if (value is "~" or "null" or "Null" or "NULL")
            return null;
        if (bool.TryParse(value, out bool boolean))
            return JsonValue.Create(boolean);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
            return JsonValue.Create(integer);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
            return JsonValue.Create(number);

        return JsonValue.Create(value);
    }

    private static char Unescape(char c) => c switch
    {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        '0' => '\0',
        _ => c,
    };

    private static string Fold(List<string> lines)
    {
        var builder = new StringBuilder();
        bool previous = false;

        foreach (string line in lines)
        {
            string item = line.Trim();
            if (item.Length == 0)
            {
                builder.Append('\n');
                previous = false;
                continue;
            }

            if (previous)
                builder.Append(' ');

            builder.Append(item);
            previous = true;
        }

        return builder.ToString();
    }

    private static int LeadingWhitespace(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;

        return i;
    }

    private static string KeyCode(char first) => first switch
    {
        '&' => "YAML_ANCHOR",
        '*' => "YAML_ALIAS",
        _ => "YAML_TAG",
    };

    private sealed class Line
    {
        public int Indent { get; set; }

        public string Text { get; set; } = string.Empty;

        public string Raw { get; set; } = string.Empty;

        public int Number { get; set; }
    }
}
