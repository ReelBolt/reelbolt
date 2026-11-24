using System.Text;
using System.Text.Json;
using NCalc;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// Decides, before a run, the Conditional steps whose expression reads only app inputs (docs/design/app-inputs.md 9.3).
/// Pure; used by the validator (hidden inputs, 4.4), and later by the estimate, the holds and the linter. It evaluates
/// with the engine's <c>ExpressionEvaluator</c> semantics for such an expression: the names <c>input.x</c> and
/// <c>$.input.x</c> become NCalc parameters (never text spliced into the expression), <c>and</c>/<c>or</c> are
/// normalised the same way, the same length and nesting guards apply, and any evaluation error is false. A parity test
/// pins it against the engine evaluator. Values reach NCalc through <see cref="Parameters"/>, the one conversion the
/// engine also uses for <c>input.*</c> (lane L3).
/// </summary>
public static class StaticBranchResolver
{
    /// <summary>The branch-order token that ends the run (docs/design/app-inputs.md 9.4).</summary>
    public const string EndToken = "end";

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) { "and", "or", "not", "true", "false" };

    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "in", "Abs", "Max", "Min", "Round", "Ceiling", "Floor", "Sign", "Truncate"
    };

    /// <summary>The input ids an expression reads (distinct, in order of first appearance).</summary>
    public static IReadOnlyList<string> InputIds(string? expression)
    {
        List<string> ids = [];
        foreach (Token token in Tokens(expression))
        {
            if (token.InputId is { } id && !ids.Contains(id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>True when every identifier is an <c>input.*</c> name, a literal, a keyword or a known function.</summary>
    public static bool IsInputDecided(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;
        foreach (Token token in Tokens(expression))
        {
            if (token.InputId is not null)
                continue;
            if (Keywords.Contains(token.Text) || (token.IsFunction && Functions.Contains(token.Text)))
                continue;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The branch an input-decided expression takes with these values: true, false, or null when the expression reads
    /// anything but inputs or a value it reads is absent. An empty expression is true (the engine's rule).
    /// </summary>
    public static bool? Decide(string? expression, IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;
        if (!IsInputDecided(expression))
            return null;
        IReadOnlyDictionary<string, object?> parameters = Parameters(values);
        foreach (string id in InputIds(expression))
        {
            if (!parameters.ContainsKey(AppInputPlaceholders.ExpressionPrefix + id))
                return null;
        }

        return Evaluate(expression, parameters);
    }

    /// <summary>
    /// The NCalc parameters of the values: <c>input.&lt;id&gt;</c> to a bool, an int (when the number is a whole 32-bit
    /// value), a double or a string, exactly as the engine converts JSON. Nulls and other kinds are left out.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Parameters(IReadOnlyDictionary<string, JsonElement>? values)
    {
        Dictionary<string, object?> map = new(StringComparer.Ordinal);
        if (values is null)
            return map;
        foreach ((string id, JsonElement value) in values)
        {
            object? converted = value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.TryGetInt32(out int i) ? i : value.GetDouble(),
                JsonValueKind.String => value.GetString(),
                _ => null
            };
            if (converted is not null)
                map[AppInputPlaceholders.ExpressionPrefix + id] = converted;
        }

        return map;
    }

    /// <summary>
    /// The step orders that can run with these values: a Conditional whose expression is decided takes only its branch;
    /// any other Conditional may take both; a branch target of <c>"end"</c> ends the run; a ReviewLoop may go back to its
    /// target. Unknown or missing targets fall through to the next step by order (the engine's rule).
    /// </summary>
    public static IReadOnlySet<int> ReachableStepOrders(IReadOnlyList<AppBindingStep> steps, IReadOnlyDictionary<string, JsonElement>? values)
    {
        List<AppBindingStep> ordered = [.. steps.OrderBy(s => s.StepOrder)];
        HashSet<int> reached = [];
        if (ordered.Count == 0)
            return reached;

        int end = ordered.Count;
        bool[] seen = new bool[end + 1];
        Stack<int> pending = new();
        pending.Push(0);
        seen[0] = true;
        while (pending.Count > 0)
        {
            int at = pending.Pop();
            if (at == end)
                continue;
            AppBindingStep step = ordered[at];
            reached.Add(step.StepOrder);
            foreach (int next in Successors(ordered, step, at, values))
            {
                int clamped = Math.Clamp(next, 0, end);
                if (!seen[clamped])
                {
                    seen[clamped] = true;
                    pending.Push(clamped);
                }
            }
        }

        return reached;
    }

    /// <summary>
    /// The step orders a run with these values will CERTAINLY reach: the walk from the first step, following a Conditional
    /// only when its branch is decided (an Expression-mode expression over app inputs with known values) and stopping at
    /// any Conditional it cannot decide, at a branch that ends the run, and at the end. The opposite of
    /// <see cref="ReachableStepOrders"/> (every step that MAY run): a start refusal may rely only on this set.
    /// </summary>
    public static IReadOnlySet<int> CertainStepOrders(IReadOnlyList<AppBindingStep> steps, IReadOnlyDictionary<string, JsonElement>? values)
    {
        List<AppBindingStep> ordered = [.. steps.OrderBy(s => s.StepOrder)];
        HashSet<int> certain = [];
        int at = 0;
        while (at >= 0 && at < ordered.Count && certain.Add(ordered[at].StepOrder))
        {
            AppBindingStep step = ordered[at];
            if (step.StepType != StepType.Conditional)
            {
                at++;
                continue;
            }

            bool? decided = DecidesByModel(step.ConfigJson) ? null : Decide(step.ConditionExpression, values);
            if (decided is null)
                break;
            at = Target(ordered, decided.Value ? step.TrueBranchStepOrder : step.FalseBranchStepOrder, at + 1);
        }

        return certain;
    }

    private static IEnumerable<int> Successors(List<AppBindingStep> ordered, AppBindingStep step, int index, IReadOnlyDictionary<string, JsonElement>? values)
    {
        int fallThrough = index + 1;
        switch (step.StepType)
        {
            case StepType.Conditional:
                bool? decided = DecidesByModel(step.ConfigJson) ? null : Decide(step.ConditionExpression, values);
                if (decided != false)
                    yield return Target(ordered, step.TrueBranchStepOrder, fallThrough);
                if (decided != true)
                    yield return Target(ordered, step.FalseBranchStepOrder, fallThrough);
                break;
            case StepType.ReviewLoop:
                yield return fallThrough;
                if (step.LoopTargetStepOrder is { } loop && ordered.FindIndex(s => s.StepOrder == loop) is var target and >= 0)
                    yield return target;
                break;
            default:
                yield return fallThrough;
                break;
        }
    }

    private static readonly JsonSerializerOptions ModeOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// True when the Conditional is not in Expression mode: the engine then decides by a model probability (or an
    /// unrecognised mode it cannot predict), so no input value fixes the branch. A missing or unreadable config is
    /// Expression mode, the engine's own fallback.
    /// </summary>
    private static bool DecidesByModel(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return false;
        try
        {
            ConditionalStepConfig? config = JsonSerializer.Deserialize<ConditionalStepConfig>(configJson, ModeOptions);
            return config is not null && config.Mode != ConditionMode.Expression;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int Target(List<AppBindingStep> ordered, string? branch, int fallThrough)
    {
        if (string.Equals(branch?.Trim(), EndToken, StringComparison.OrdinalIgnoreCase))
            return ordered.Count;
        if (!int.TryParse(branch, out int order))
            return fallThrough;
        int index = ordered.FindIndex(s => s.StepOrder == order);
        return index < 0 ? fallThrough : index;
    }

    private static bool Evaluate(string expression, IReadOnlyDictionary<string, object?> parameters)
    {
        // The engine's rewrite: $.name first, then whole-word bare names, longest first; then and/or keywords.
        string ncalc = expression;
        List<string> names = [.. parameters.Keys.OrderByDescending(k => k.Length)];
        foreach (string key in names)
            ncalc = ncalc.Replace($"$.{key}", $"[{key}]");
        foreach (string key in names)
        {
            if (!ncalc.Contains($"[{key}]"))
                ncalc = ReplaceWholeWord(ncalc, key, $"[{key}]");
        }

        ncalc = ncalc.Replace(" AND ", " && ").Replace(" OR ", " || ").Replace(" and ", " && ").Replace(" or ", " || ");
        if (ncalc.Length > 2000 || ExceedsMaxParenDepth(ncalc, 20))
            return false;
        try
        {
            Expression e = new(ncalc);
            foreach ((string key, object? value) in parameters)
                e.Parameters[key] = value;
            return e.Evaluate() switch
            {
                bool b => b,
                int i => i != 0,
                long l => l != 0,
                double d => d != 0.0,
                decimal m => m != 0m,
                string s => !string.IsNullOrEmpty(s),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool ExceedsMaxParenDepth(string expression, int maxDepth)
    {
        int depth = 0;
        foreach (char c in expression)
        {
            if (c == '(' && ++depth > maxDepth)
                return true;
            if (c == ')')
                depth--;
        }

        return false;
    }

    private static string ReplaceWholeWord(string source, string word, string replacement)
    {
        int start = 0;
        while (true)
        {
            int idx = source.IndexOf(word, start, StringComparison.Ordinal);
            if (idx < 0)
                break;
            bool prevOk = idx == 0 || !IsIdentChar(source[idx - 1]);
            bool nextOk = idx + word.Length >= source.Length || !IsIdentChar(source[idx + word.Length]);
            if (prevOk && nextOk)
            {
                source = string.Concat(source.AsSpan(0, idx), replacement, source.AsSpan(idx + word.Length));
                start = idx + replacement.Length;
            }
            else
            {
                start = idx + 1;
            }
        }

        return source;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '[' || c == ']';

    private readonly record struct Token(string Text, bool IsFunction, string? InputId);

    /// <summary>The identifiers of an expression (string and number literals skipped), each with its input id if it is one.</summary>
    private static IEnumerable<Token> Tokens(string? expression)
    {
        if (string.IsNullOrEmpty(expression))
            yield break;
        int i = 0;
        while (i < expression.Length)
        {
            char c = expression[i];
            if (c is '\'' or '"')
            {
                int close = expression.IndexOf(c, i + 1);
                i = close < 0 ? expression.Length : close + 1;
                continue;
            }

            if (c == '[')
            {
                int close = expression.IndexOf(']', i + 1);
                string inner = close < 0 ? expression[(i + 1)..] : expression[(i + 1)..close];
                i = close < 0 ? expression.Length : close + 1;
                yield return Classify(inner, isFunction: false);
                continue;
            }

            if (char.IsDigit(c))
            {
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '.'))
                    i++;
                continue;
            }

            if (char.IsLetter(c) || c is '_' or '$')
            {
                StringBuilder sb = new();
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] is '_' or '.' or '$'))
                    sb.Append(expression[i++]);
                int j = i;
                while (j < expression.Length && char.IsWhiteSpace(expression[j]))
                    j++;
                yield return Classify(sb.ToString(), isFunction: j < expression.Length && expression[j] == '(');
                continue;
            }

            i++;
        }
    }

    private static Token Classify(string name, bool isFunction)
    {
        string bare = name.StartsWith("$.", StringComparison.Ordinal) ? name[2..] : name;
        if (!isFunction && bare.StartsWith(AppInputPlaceholders.ExpressionPrefix, StringComparison.Ordinal))
        {
            string id = bare[AppInputPlaceholders.ExpressionPrefix.Length..];
            if (AppInputPlaceholders.IdPattern().IsMatch(id))
                return new Token(name, false, id);
        }

        return new Token(name, isFunction, null);
    }
}
