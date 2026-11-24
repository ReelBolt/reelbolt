namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Configuration for project-file vector indexing (section <c>FileIndexing</c>).
/// </summary>
public sealed class FileIndexingOptions
{
    public const string SectionName = "FileIndexing";

    /// <summary>
    /// The largest file, in bytes, the indexer will read into memory to chunk and embed. A file
    /// larger than this (by stored size, or when the stored size lies) is marked Failed with
    /// <c>too_large</c> without downloading it.
    /// </summary>
    public int MaxTextBytes { get; set; } = 4194304;
}
