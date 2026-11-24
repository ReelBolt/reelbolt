using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Normalizes a raw key segment into the design document's path alphabet
/// (<c>^[a-z0-9][a-z0-9_-]{0,47}$</c>) and makes a duplicate dot path unique. Pure and total:
/// every method answers a string rather than throwing.
/// </summary>
public static class DesignKeys
{
    private const int MaxKeyLength = 48;

    private static readonly ConditionalWeakTable<ISet<string>, Dictionary<string, int>> NextSuffix = new();

    /// <summary>
    /// Lowercases each character, turns anything outside <c>a-z0-9_-</c> into <c>-</c>, trims
    /// leading <c>-</c>/<c>_</c>, maps an empty result to <c>x</c> and caps the length at 48.
    /// </summary>
    public static string Slug(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "x";

        var builder = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            char lower = char.ToLowerInvariant(c);
            builder.Append(IsKeyCharacter(lower) ? lower : '-');
        }

        int start = 0;
        while (start < builder.Length && (builder[start] == '-' || builder[start] == '_'))
            start++;

        string normalized = builder.ToString(start, builder.Length - start);
        if (normalized.Length == 0)
            return "x";

        return normalized.Length > MaxKeyLength ? normalized[..MaxKeyLength] : normalized;
    }

    /// <summary>
    /// Returns <paramref name="path"/> when free, otherwise the first <c>path-2</c>, <c>path-3</c>…
    /// not already in <paramref name="used"/>, adding the answer to <paramref name="used"/>.
    /// </summary>
    public static string Unique(ISet<string> used, string path)
    {
        if (used.Add(path))
            return path;

        // Remember where the last probe for this path ended so n duplicates cost O(n), not O(n^2).
        Dictionary<string, int> next = NextSuffix.GetOrCreateValue(used);
        next.TryGetValue(path, out int suffix);
        if (suffix < 2)
            suffix = 2;

        while (true)
        {
            string candidate = path + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
            if (used.Add(candidate))
            {
                next[path] = suffix;
                return candidate;
            }
        }
    }

    /// <summary>Longest attacker-controlled string echoed into a diagnostic.</summary>
    public const int MaxEchoLength = 64;

    /// <summary>
    /// Bounds an attacker-controlled string before it is echoed into a diagnostic message or path:
    /// <c>null</c> becomes <c>(none)</c> and anything longer than <see cref="MaxEchoLength"/> is cut with an ellipsis.
    /// </summary>
    public static string Clip(string? value)
    {
        if (value is null)
            return "(none)";

        if (value.Length <= MaxEchoLength)
            return value;

        int cut = MaxEchoLength - 3;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
            cut--;

        return value[..cut] + "...";
    }

    private static bool IsKeyCharacter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '_' or '-';
}
