using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The <c>{{input.id}}</c> placeholder syntax (docs/design/app-inputs.md 5.1). The sequence <c>{{input.</c> is reserved
/// in every config string and in the request template; there is no escape syntax.
/// </summary>
public static partial class AppInputPlaceholders
{
    /// <summary>The reserved opening sequence.</summary>
    public const string Opening = "{{input.";

    /// <summary>The config key of whole-value bindings, an inert template key the binder strips.</summary>
    public const string AppInputsKey = "appInputs";

    /// <summary>The expression namespace conditions read inputs through (<c>input.liteMode</c>).</summary>
    public const string ExpressionPrefix = "input.";

    /// <summary>The id an input may have: a lower-case letter then letters and digits, 32 characters at most.</summary>
    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,31}$")]
    public static partial Regex IdPattern();

    [GeneratedRegex(@"\{\{input\.([a-z][A-Za-z0-9]{0,31})\}\}")]
    private static partial Regex Placeholder();

    /// <summary>True when the text contains the reserved sequence at all.</summary>
    public static bool ContainsReserved(string? text) => text is not null && text.Contains(Opening, StringComparison.Ordinal);

    /// <summary>The input ids the text names, in order of appearance (with repeats).</summary>
    public static IReadOnlyList<string> Ids(string? text) =>
        text is null ? [] : [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value)];

    /// <summary>
    /// True when every <c>{{input.</c> in the text opens a well-formed placeholder (a valid id then <c>}}</c>): a
    /// malformed one is refused at save, never sent as literal text.
    /// </summary>
    public static bool IsWellFormed(string? text)
    {
        if (text is null)
            return true;
        int reserved = CountOccurrences(text, Opening);
        return reserved == Placeholder().Matches(text).Count;
    }

    /// <summary>The text with every placeholder removed (what the author typed around them).</summary>
    public static string Literal(string text) => Placeholder().Replace(text, string.Empty);

    /// <summary>Replaces each placeholder by the value the callback gives for its id.</summary>
    public static string Substitute(string text, Func<string, string> valueOf) =>
        Placeholder().Replace(text, m => valueOf(m.Groups[1].Value));

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        for (int at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
