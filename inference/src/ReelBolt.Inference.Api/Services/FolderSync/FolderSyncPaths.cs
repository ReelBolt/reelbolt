using ReelBolt.Inference.Api.Services.GitHub;

namespace ReelBolt.Inference.Api.Services.FolderSync;

/// <summary>
/// The path rules of computer folder sync (docs/design/local-folder-sync-and-storage.md, "Rules"):
/// a path the desktop app sends is relative to the local folder, <c>/</c>-separated, and — like the
/// <see cref="GitHubIncludePaths"/> rules — has no empty, <c>.</c> or <c>..</c> segment, no backslash, no
/// control character, and is at most <see cref="MaxPathLength"/> characters. It is checked, never
/// "repaired": a path that would need trimming or rewriting is refused, so the path a project file is
/// stored under is always exactly <c>&lt;targetFolder&gt;/&lt;path&gt;</c> and matching it later is a plain
/// ordinal comparison.
/// </summary>
public static class FolderSyncPaths
{
    public const int MaxPathLength = 1024;

    public const int MaxTargetFolderLength = 512;

    public const int MaxDisplayPathLength = 512;

    /// <summary>The fallback target folder when the local folder's own name cannot be used.</summary>
    public const string DefaultTargetFolder = "Computer folder";

    /// <summary>True when <paramref name="path"/> is a usable folder-relative file path, exactly as sent.</summary>
    public static bool IsValidRelativePath(string? path) => IsCleanPath(path, MaxPathLength);

    /// <summary>
    /// The canonical target folder for a link: the requested one, else the last segment of the display
    /// path, else <see cref="DefaultTargetFolder"/>. Null (with <paramref name="reason"/>) when a folder was
    /// requested and is not acceptable.
    /// </summary>
    public static string? ResolveTargetFolder(string? requested, string? displayPath, out string? reason)
    {
        reason = null;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (!GitHubIncludePaths.TryNormalize([requested], out List<string> normalized, out reason))
                return null;
            if (normalized.Count != 1)
            {
                reason = "Choose a folder inside the project, not the whole project.";
                return null;
            }

            string candidate = normalized[0];
            if (!IsCleanPath(candidate, MaxTargetFolderLength))
            {
                reason = $"A folder name is at most {MaxTargetFolderLength} characters, and no part of it may start or end with a space.";
                return null;
            }

            return candidate;
        }

        string? fromDisplay = LastSegment(displayPath);
        return fromDisplay is not null && IsCleanPath(fromDisplay, MaxTargetFolderLength) ? fromDisplay : DefaultTargetFolder;
    }

    /// <summary>The project path a link stores a file under.</summary>
    public static string StoredPath(string targetFolder, string relativePath) => targetFolder + "/" + relativePath;

    /// <summary>The folder-relative path of a stored path, or null when it is not under <paramref name="targetFolder"/>.</summary>
    public static string? RelativeTo(string targetFolder, string? storedPath)
    {
        if (storedPath is null) return null;
        string prefix = targetFolder + "/";
        return storedPath.Length > prefix.Length && storedPath.StartsWith(prefix, StringComparison.Ordinal)
            ? storedPath[prefix.Length..]
            : null;
    }

    /// <summary>True when one folder is the other or lies inside it (two links could then collide).</summary>
    public static bool Overlaps(string a, string b) => GitHubIncludePaths.IsWithin(a, b) || GitHubIncludePaths.IsWithin(b, a);

    /// <summary>True for a 64-character hex SHA-256.</summary>
    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsCleanPath(string? path, int maxLength)
    {
        if (string.IsNullOrEmpty(path) || path.Length > maxLength) return false;
        foreach (char c in path)
        {
            if (char.IsControl(c) || c == '\\') return false;
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..") return false;
            // ProjectFilePath trims segments when it stores a file; a segment that would change under
            // trimming could never be matched again, so it is refused here instead.
            if (char.IsWhiteSpace(segment[0]) || char.IsWhiteSpace(segment[^1])) return false;
        }

        return true;
    }

    private static string? LastSegment(string? displayPath)
    {
        if (string.IsNullOrWhiteSpace(displayPath)) return null;
        string trimmed = displayPath.Trim().TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        string name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        // "C:" or a bare drive/root has no usable name.
        return name.Length == 0 || name.EndsWith(':') ? null : name;
    }
}
