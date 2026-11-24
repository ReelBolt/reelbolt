namespace ReelBolt.Inference.Api.Services.History;

/// <summary>The error codes the project-history routes return; the service throws the two domain exceptions.</summary>
public static class ProjectHistoryErrorCodes
{
    /// <summary>413: the project has more live files than one version may hold.</summary>
    public const string ProjectTooLargeToVersion = "PROJECT_TOO_LARGE_TO_VERSION";

    /// <summary>409: the project already holds the maximum number of named versions.</summary>
    public const string NamedVersionLimit = "NAMED_VERSION_LIMIT";

    /// <summary>409: a project output hold protects a platform-made item the action would destroy.</summary>
    public const string OutputHeld = "OUTPUT_HELD";
}

/// <summary>
/// A capture would hold more files than <see cref="ProjectHistoryOptions.MaxFilesPerVersion"/>.
/// The REST layer maps it to <c>413 PROJECT_TOO_LARGE_TO_VERSION</c>.
/// </summary>
public sealed class ProjectHistoryTooLargeException : Exception
{
    public ProjectHistoryTooLargeException(int fileCount, int maxFiles)
        : base($"The project has {fileCount} live files; one version may hold at most {maxFiles}.")
    {
        FileCount = fileCount;
        MaxFiles = maxFiles;
    }

    public string Code => ProjectHistoryErrorCodes.ProjectTooLargeToVersion;
    public int FileCount { get; }
    public int MaxFiles { get; }
}

/// <summary>
/// A named capture would exceed <see cref="ProjectHistoryOptions.MaxNamedVersions"/>. The REST layer
/// maps it to <c>409 NAMED_VERSION_LIMIT</c>.
/// </summary>
public sealed class ProjectHistoryNamedVersionLimitException : Exception
{
    public ProjectHistoryNamedVersionLimitException(int maxNamedVersions)
        : base($"The project already holds the maximum of {maxNamedVersions} named versions.")
    {
        MaxNamedVersions = maxNamedVersions;
    }

    public string Code => ProjectHistoryErrorCodes.NamedVersionLimit;
    public int MaxNamedVersions { get; }
}
