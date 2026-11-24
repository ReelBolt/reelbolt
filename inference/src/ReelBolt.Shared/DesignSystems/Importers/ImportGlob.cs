namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// A small, hand-written glob matcher for the <c>source</c> patterns of a Style Dictionary config.
/// There is deliberately no backtracking regular-expression engine here: patterns are data from an
/// untrusted file and an expression engine is an easy denial of service.
/// </summary>
public static class ImportGlob
{
    private const int MaxPatternLength = 200;
    private const int MaxPathLength = 1024;

    /// <summary>
    /// True when <paramref name="pattern"/> is short enough and free of the constructs that make a
    /// glob escape its config directory (absolute paths, <c>..</c>, backslashes, <c>node_modules</c>)
    /// or that this matcher does not implement (classes, braces, negation).
    /// </summary>
    public static bool IsSafePattern(string? pattern)
    {
        if (pattern is null)
            return false;

        if (pattern.Length < 1 || pattern.Length > MaxPatternLength)
            return false;

        if (pattern[0] == '/')
            return false;

        foreach (char character in pattern)
        {
            if (character == '\\' || char.IsControl(character) || !IsAllowedCharacter(character))
                return false;
        }

        foreach (string segment in pattern.Split('/'))
        {
            if (segment == ".." || segment == "node_modules")
                return false;
        }

        return true;
    }

    /// <summary>
    /// Case-sensitive glob match. A whole segment of <c>**</c> matches zero or more path segments;
    /// inside a segment <c>*</c> matches zero or more characters (never <c>/</c>) and <c>?</c>
    /// matches exactly one. Iterative two-pointer backtracking: no recursion, no expression engine.
    /// </summary>
    public static bool IsMatch(string? pattern, string? path)
    {
        if (pattern is null || path is null || pattern.Length == 0 || path.Length == 0)
            return false;

        if (path.Length > MaxPathLength)
            return false;

        string[] patternSegments = pattern.Split('/');
        string[] pathSegments = path.Split('/');

        int p = 0;
        int s = 0;
        int star = -1;
        int mark = -1;

        while (s < pathSegments.Length)
        {
            if (p < patternSegments.Length && patternSegments[p] == "**")
            {
                star = p;
                mark = s;
                p++;
                continue;
            }

            if (p < patternSegments.Length && MatchSegment(patternSegments[p], pathSegments[s]))
            {
                p++;
                s++;
                continue;
            }

            if (star >= 0)
            {
                mark++;
                s = mark;
                p = star + 1;
                continue;
            }

            return false;
        }

        while (p < patternSegments.Length && patternSegments[p] == "**")
            p++;

        return p == patternSegments.Length;
    }

    private static bool MatchSegment(string pattern, string text)
    {
        int p = 0;
        int t = 0;
        int star = -1;
        int mark = -1;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
                continue;
            }

            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p;
                mark = t;
                p++;
                continue;
            }

            if (star >= 0)
            {
                mark++;
                t = mark;
                p = star + 1;
                continue;
            }

            return false;
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    private static bool IsAllowedCharacter(char character) =>
        (character >= 'a' && character <= 'z')
        || (character >= 'A' && character <= 'Z')
        || (character >= '0' && character <= '9')
        || character is '_' or '.' or '-' or '/' or '*' or '?' or '@' or '+' or ' ';
}
