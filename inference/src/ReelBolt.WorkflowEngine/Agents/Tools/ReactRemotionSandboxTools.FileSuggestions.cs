using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// What <c>StageProjectFile</c> answers when a reference matches no project file. Production run
/// 36df4287: the Author staged made-up names (<c>ad-01.mp4</c>, <c>ad-follow-man-walking.mp4</c>),
/// each got a bare "no match", and the composition kept pointing at files that were never staged.
/// The miss now names the closest real files (by case-insensitive name and path similarity), so the
/// next call can use an exact id or name. The suggestions are only suggestions: nothing is staged
/// on a miss, and <see cref="ResolveProjectFile"/> stays exact.
/// </summary>
public partial class ReactRemotionSandboxTools
{
    /// <summary>How many near files a miss names.</summary>
    internal const int MaxClosestFiles = 5;

    /// <summary>Names and paths are compared over at most this many characters (bounds the edit distance).</summary>
    private const int MaxComparedChars = 160;

    /// <summary>
    /// The newest file whose <c>DirectoryPath/OriginalFileName</c> is exactly <paramref name="reference"/>
    /// (slashes normalised, a leading <c>/</c> or <c>./</c> ignored). Null when the reference names no folder.
    /// </summary>
    internal static ProjectWorkspaceFile? ResolveByDirectoryAndName(IReadOnlyList<ProjectWorkspaceFile> files, string reference)
    {
        string wanted = NormalizeReferencePath(reference);
        if (!wanted.Contains('/', StringComparison.Ordinal))
            return null;

        return files
            .Where(f => DirectoryOf(f) is { } directory
                && string.Equals(directory + "/" + f.OriginalFileName, wanted, StringComparison.Ordinal))
            .OrderByDescending(f => f.UploadedAt)
            .FirstOrDefault();
    }

    /// <summary>The file's folder (no trailing slash), from <c>DirectoryPath</c> or else its original path; null at the root.</summary>
    internal static string? DirectoryOf(ProjectWorkspaceFile file)
    {
        string? directory = file.DirectoryPath;
        if (string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(file.OriginalPath))
        {
            string path = NormalizeReferencePath(file.OriginalPath);
            int slash = path.LastIndexOf('/');
            directory = slash > 0 ? path[..slash] : null;
        }

        if (string.IsNullOrWhiteSpace(directory))
            return null;

        directory = NormalizeReferencePath(directory).TrimEnd('/');
        return directory.Length == 0 ? null : directory;
    }

    private static string NormalizeReferencePath(string value)
    {
        string path = value.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return path.TrimStart('/');
    }

    /// <summary>
    /// Up to <see cref="MaxClosestFiles"/> project files ranked by how close their name or path is to
    /// <paramref name="reference"/>, case-insensitively: the better of the edit-distance similarity
    /// of the names, of the paths, and of the shared name words, with a small lift for the same
    /// extension. Ties go to the newest file. Always the top few, however far, so the model sees real names.
    /// </summary>
    internal static IReadOnlyList<ProjectWorkspaceFile> ClosestProjectFiles(IReadOnlyList<ProjectWorkspaceFile> files, string reference)
    {
        if (files.Count == 0)
            return [];

        string wantedPath = Bound(NormalizeReferencePath(reference).ToLowerInvariant());
        string wantedName = Bound(Path.GetFileName(wantedPath));
        string wantedExtension = Path.GetExtension(wantedName);
        HashSet<string> wantedWords = Words(Path.GetFileNameWithoutExtension(wantedName));

        return files
            .Select(f =>
            {
                string name = Bound(f.OriginalFileName.ToLowerInvariant());
                string path = Bound(NormalizeReferencePath(f.OriginalPath ?? (DirectoryOf(f) is { } d ? d + "/" + f.OriginalFileName : f.OriginalFileName)).ToLowerInvariant());
                double score = Math.Max(
                    Math.Max(Similarity(wantedName, name), Similarity(wantedPath, path)),
                    WordOverlap(wantedWords, Words(Path.GetFileNameWithoutExtension(name))));
                if (wantedExtension.Length > 0 && string.Equals(wantedExtension, Path.GetExtension(name), StringComparison.Ordinal))
                    score += 0.1;
                return (File: f, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.File.UploadedAt)
            .Take(MaxClosestFiles)
            .Select(x => x.File)
            .ToList();
    }

    private static string Bound(string value) => value.Length <= MaxComparedChars ? value : value[..MaxComparedChars];

    /// <summary>1 - edit distance / longer length; 1 for equal strings, 0 when nothing lines up.</summary>
    internal static double Similarity(string a, string b)
    {
        int longer = Math.Max(a.Length, b.Length);
        if (longer == 0)
            return 1;
        return 1.0 - (double)EditDistance(a, b) / longer;
    }

    private static int EditDistance(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static HashSet<string> Words(string value) =>
        value.Split(['-', '_', ' ', '.', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Shared words over the reference's words (how much of what was asked for the file's name holds).</summary>
    private static double WordOverlap(HashSet<string> wanted, HashSet<string> candidate)
    {
        if (wanted.Count == 0 || candidate.Count == 0)
            return 0;
        return (double)wanted.Count(candidate.Contains) / Math.Max(wanted.Count, candidate.Count);
    }
}
