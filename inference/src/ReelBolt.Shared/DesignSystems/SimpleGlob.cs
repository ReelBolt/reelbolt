namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// The tiny, hand-written glob matcher the design-system Parse op uses to select design sources
/// (the six first-party globs plus a step's <c>ExtraGlobs</c>). It is deliberately NOT a regex:
/// <list type="bullet">
/// <item><c>**</c> matches zero or more whole <c>/</c>-separated path segments.</item>
/// <item><c>*</c> matches any run of characters within ONE segment (never <c>/</c>).</item>
/// <item><c>?</c> matches exactly one character (never <c>/</c>).</item>
/// <item>every other character matches literally.</item>
/// </list>
/// Matching is ordinal and case-sensitive. The matcher is total: it never throws on any input
/// (a null pattern or path is simply a non-match) and it performs no IO, which is what lets the
/// same class be reused by Phase 3 drift checks.
/// </summary>
public static class SimpleGlob
{
    /// <summary>True when <paramref name="path"/> matches the slash-separated <paramref name="pattern"/>.</summary>
    public static bool IsMatch(string pattern, string path)
    {
        if (pattern is null || path is null)
            return false;

        string[] patternSegments = Split(pattern);
        string[] pathSegments = Split(path);

        // Memoised so a pathological pattern (many `**`) stays linear rather than exponential.
        var visited = new Dictionary<(int Pattern, int Path), bool>();
        return MatchSegments(patternSegments, 0, pathSegments, 0, visited);
    }

    private static string[] Split(string value) =>
        value.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool MatchSegments(
        string[] pattern,
        int patternIndex,
        string[] path,
        int pathIndex,
        Dictionary<(int Pattern, int Path), bool> visited)
    {
        if (visited.TryGetValue((patternIndex, pathIndex), out bool cached))
            return cached;

        bool result;
        if (patternIndex == pattern.Length)
        {
            result = pathIndex == path.Length;
        }
        else if (pattern[patternIndex] == "**")
        {
            // Zero or more whole segments: try every split point.
            result = false;
            for (int next = pathIndex; next <= path.Length; next++)
            {
                if (MatchSegments(pattern, patternIndex + 1, path, next, visited))
                {
                    result = true;
                    break;
                }
            }
        }
        else if (pathIndex == path.Length)
        {
            result = false;
        }
        else
        {
            result = MatchSegment(pattern[patternIndex], path[pathIndex])
                && MatchSegments(pattern, patternIndex + 1, path, pathIndex + 1, visited);
        }

        visited[(patternIndex, pathIndex)] = result;
        return result;
    }

    /// <summary>Matches one segment, treating <c>*</c> and <c>?</c> as within-segment wildcards.</summary>
    private static bool MatchSegment(string pattern, string text)
    {
        int patternIndex = 0;
        int textIndex = 0;
        int starIndex = -1;
        int starTextIndex = 0;

        while (textIndex < text.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?' || pattern[patternIndex] == text[textIndex]))
            {
                patternIndex++;
                textIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex;
                starTextIndex = textIndex;
                patternIndex++;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                starTextIndex++;
                textIndex = starTextIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            patternIndex++;

        return patternIndex == pattern.Length;
    }
}
