namespace ReelBolt.Shared.Archives;

/// <summary>
/// The "what is not worth unpacking" policy for a customer's code zip. Pure: names in, a reason code or null out.
/// Always-skip names (dependency folders, VCS metadata, OS clutter) never enter the workspace. Build-output
/// folders are skipped only when the archive also holds files OUTSIDE every build-output folder, so a zip that
/// is nothing but a built bundle keeps it.
/// </summary>
public static class ArchiveUnpackExclusions
{
    public const string Dependencies = "EXCLUDED_DEPENDENCIES";
    public const string Vcs = "EXCLUDED_VCS";
    public const string BuildOutput = "EXCLUDED_BUILD_OUTPUT";
    public const string System = "EXCLUDED_SYSTEM";

    /// <summary>All reason codes this class can return.</summary>
    public static readonly IReadOnlyList<string> Reasons = new[] { Dependencies, Vcs, BuildOutput, System };

    private static readonly HashSet<string> DependencyDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components", ".venv", "venv", ".gradle",
    };

    private static readonly HashSet<string> VcsDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
    };

    private static readonly HashSet<string> SystemDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "__MACOSX", "__pycache__", ".idea",
    };

    private static readonly HashSet<string> BuildOutputDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "dist", "build", "out", ".next", ".nuxt", ".output", ".svelte-kit", "coverage", "target", "bin", "obj",
        ".turbo", ".cache", ".parcel-cache",
    };

    /// <summary>The reason a path is always skipped, or null. <paramref name="path"/> is a normalized relative path with '/' separators.</summary>
    public static string? AlwaysSkipReason(string path)
    {
        string[] segments = path.Split('/');
        string file = segments[^1];

        // Directory segments first: a node_modules/.DS_Store file is dependencies, not clutter.
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string segment = segments[i];
            if (DependencyDirs.Contains(segment))
                return Dependencies;
            if (VcsDirs.Contains(segment))
                return Vcs;
        }

        // A bare ".git" file (a submodule pointer) is VCS metadata too.
        if (VcsDirs.Contains(file))
            return Vcs;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (SystemDirs.Contains(segments[i]))
                return System;
        }

        if (file.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
            || file.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("._", StringComparison.Ordinal)
            || file.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase))
        {
            return System;
        }

        return null;
    }

    /// <summary>True when any DIRECTORY segment of <paramref name="path"/> is a build-output folder name.</summary>
    public static bool IsInBuildOutput(string path)
    {
        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (BuildOutputDirs.Contains(segments[i]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the final filter from every candidate path (the names-only first pass). Build-output paths are
    /// excluded only when at least one candidate that is not always-skipped lies outside every build-output folder.
    /// The returned function gives the exclusion reason for a path, or null to keep it.
    /// </summary>
    public static Func<string, string?> BuildFilter(IEnumerable<string> candidatePaths)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);

        bool hasSourceOutsideBuildOutput = false;
        foreach (string path in candidatePaths)
        {
            if (AlwaysSkipReason(path) is null && !IsInBuildOutput(path))
            {
                hasSourceOutsideBuildOutput = true;
                break;
            }
        }

        return path =>
        {
            string? always = AlwaysSkipReason(path);
            if (always is not null)
                return always;

            return hasSourceOutsideBuildOutput && IsInBuildOutput(path) ? BuildOutput : null;
        };
    }
}
