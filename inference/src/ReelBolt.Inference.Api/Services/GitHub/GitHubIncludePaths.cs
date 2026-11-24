using System.Text.Json;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// The part of a linked repository a project mirrors: a list of repository-relative paths, each one a
/// file or a folder (a folder includes everything under it, including files added to it later). An
/// empty list means "the whole repository". See <c>docs/github-app.md</c> "Choosing which files are
/// synced".
///
/// <para>
/// Everything stored on <c>ProjectGitHubLink.IncludePathsJson</c> goes through <see cref="TryNormalize"/>
/// first, so the stored list is always canonical: trimmed, slash-separated, without traversal or
/// control characters, de-duplicated, with any path already covered by a selected folder dropped, and
/// sorted. That is what lets <see cref="Matches"/> be a plain ordinal prefix test, and what makes two
/// selections that mean the same thing store the same bytes.
/// </para>
/// </summary>
public static class GitHubIncludePaths
{
    /// <summary>Most paths one selection may hold. A larger selection should pick the folder instead.</summary>
    public const int MaxPaths = 500;

    /// <summary>Longest single path accepted, in characters.</summary>
    public const int MaxPathLength = 512;

    /// <summary>Error code for a selection that is not acceptable, shared by the link and edit endpoints.</summary>
    public const string InvalidError = "invalid_include_paths";

    /// <summary>
    /// Normalizes a selection. Null or empty (after normalization) yields an empty list, meaning the
    /// whole repository. Returns false — and the caller answers <c>400 invalid_include_paths</c> — when
    /// any path is malformed or there are too many.
    /// </summary>
    public static bool TryNormalize(IEnumerable<string?>? raw, out List<string> normalized, out string? reason)
    {
        normalized = [];
        reason = null;
        if (raw is null) return true;

        List<string> candidates = [];
        foreach (string? value in raw)
        {
            if (candidates.Count >= MaxPaths)
            {
                reason = $"At most {MaxPaths} files and folders can be chosen. Choose their folder instead.";
                return false;
            }

            if (!TryNormalizeOne(value, out string? path, out reason)) return false;
            if (path is null) return SetWhole(out normalized);
            candidates.Add(path);
        }

        // Sorted ordinally, a folder comes before everything inside it, so a path covered by a chosen
        // folder is always met after that folder and dropped. At most MaxPaths entries, so the
        // quadratic check is bounded.
        candidates.Sort(StringComparer.Ordinal);
        foreach (string path in candidates.Distinct(StringComparer.Ordinal))
        {
            if (normalized.Any(kept => IsWithin(path, kept))) continue;
            normalized.Add(path);
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="repositoryPath"/> is selected: no selection at all, or the path equals
    /// one selected path or lies inside one selected folder.
    /// </summary>
    public static bool Matches(IReadOnlyList<string> includePaths, string repositoryPath)
    {
        if (includePaths.Count == 0) return true;
        foreach (string include in includePaths)
        {
            if (IsWithin(repositoryPath, include)) return true;
        }

        return false;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folderOrFile"/> or lies inside it.</summary>
    public static bool IsWithin(string path, string folderOrFile) =>
        string.Equals(path, folderOrFile, StringComparison.Ordinal) ||
        (path.Length > folderOrFile.Length &&
         path[folderOrFile.Length] == '/' &&
         path.StartsWith(folderOrFile, StringComparison.Ordinal));

    /// <summary>The stored column value: null for "the whole repository", a JSON array otherwise.</summary>
    public static string? Serialize(IReadOnlyList<string> includePaths) =>
        includePaths.Count == 0 ? null : JsonSerializer.Serialize(includePaths);

    /// <summary>
    /// Reads the stored column. A value that cannot be read throws rather than being treated as "no
    /// selection", because silently widening a selection to the whole repository is the failure that
    /// would surprise somebody, and a pull that refuses is the safe direction.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        List<string>? values = JsonSerializer.Deserialize<List<string>>(json);
        if (values is null) return [];
        if (!TryNormalize(values, out List<string> normalized, out string? reason))
            throw new InvalidOperationException($"The stored file selection is not valid: {reason}");
        return normalized;
    }

    private static bool SetWhole(out List<string> normalized)
    {
        // A selected root ("/" or "") is the whole repository, whatever else was chosen next to it.
        normalized = [];
        return true;
    }

    /// <summary>One path. <paramref name="path"/> null means the repository root.</summary>
    private static bool TryNormalizeOne(string? raw, out string? path, out string? reason)
    {
        path = null;
        reason = null;

        if (raw is null)
        {
            reason = "A chosen path is empty.";
            return false;
        }

        string value = raw.Trim();
        if (value.Length > MaxPathLength)
        {
            reason = $"A chosen path is longer than {MaxPathLength} characters.";
            return false;
        }

        foreach (char c in value)
        {
            if (char.IsControl(c) || c == '\\')
            {
                reason = "A chosen path contains a character that cannot be part of a repository path.";
                return false;
            }
        }

        value = value.Trim('/');
        if (value.Length == 0) return true; // the root

        string[] segments = value.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                reason = "A chosen path is not a plain path inside the repository.";
                return false;
            }
        }

        path = value;
        return true;
    }
}
