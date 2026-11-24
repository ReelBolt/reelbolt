using System.Text;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The <see cref="IMediaWorkspace"/> a self-host deployment runs on: one directory on this host,
/// ffmpeg/ffprobe as local child processes, and object storage reached through
/// <see cref="IProjectFileWorkspace"/>. Every method reproduces exactly what the media pipeline did
/// before this seam existed — the only thing that moved is <i>who</i> resolves a path.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "wraps" means here.</b> A local workspace holds the directory <see cref="VideoScratchSpace"/>
/// already created for one execution + step attempt, and applies that type's containment assertion
/// (<see cref="VideoScratchSpace.ValidateScratchScope"/>) to every virtual path — so a caller-supplied
/// name containing <c>..</c>, or an absolute path, is refused with the same
/// <see cref="UnauthorizedAccessException"/> the pipeline has always thrown rather than being
/// silently resolved. ffmpeg and ffprobe go through the same singleton
/// <see cref="IVideoToolRunner"/> (hence the same process-wide concurrency gate and the same
/// timeout/kill behaviour), and object storage goes through the same
/// <see cref="IProjectFileWorkspace"/> instance.
/// </para>
/// <para>
/// <b>Scope.</b> Per execution + step attempt, exactly like the scratch directory it wraps: an
/// execution cancels, retries or finishes and the whole directory goes away with it. It is created
/// per call site rather than registered in DI, because a singleton cannot know which execution it
/// is serving — which is the reason this is a per-call parameter and not a constructor dependency
/// of the media services that used to take <see cref="IVideoToolRunner"/> directly.
/// </para>
/// </remarks>
public sealed class LocalMediaWorkspace : IMediaWorkspace
{
    private readonly IVideoToolRunner _runner;
    private readonly IProjectFileWorkspace? _projectFiles;
    private readonly Guid _projectId;
    private readonly ILogger? _logger;

    /// <summary>The scheduling class of every ffmpeg and ffprobe call of this workspace. Normal unless a factory says otherwise.</summary>
    private readonly VideoToolPriority _priority;

    private LocalMediaWorkspace(
        string rootPath,
        IVideoToolRunner runner,
        IProjectFileWorkspace? projectFiles,
        Guid projectId,
        ILogger? logger,
        VideoToolPriority priority = VideoToolPriority.Normal)
    {
        RootPath = rootPath;
        _runner = runner;
        _projectFiles = projectFiles;
        _projectId = projectId;
        _logger = logger;
        _priority = priority;
    }

    public string RootPath { get; }

    /// <summary>
    /// The ordinary construction: the workspace IS this step attempt's scratch directory, so a
    /// virtual path is scratch-relative and <c>MaterializeAsync("grid.rgb")</c> resolves to exactly
    /// the path <see cref="VideoScratchSpace.GetPath"/> returns for the same name.
    /// </summary>
    public static LocalMediaWorkspace Create(
        VideoScratchSpace scratch,
        IVideoToolRunner runner,
        IProjectFileWorkspace? projectFiles = null,
        Guid projectId = default,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scratch);
        return new LocalMediaWorkspace(scratch.DirectoryPath, runner, projectFiles, projectId, logger);
    }

    /// <summary>Same, for a caller that has not created a <see cref="VideoScratchSpace"/> of its own.</summary>
    public static LocalMediaWorkspace Create(
        VideoEditingOptions options,
        Guid executionId,
        Guid stepId,
        IVideoToolRunner runner,
        IProjectFileWorkspace? projectFiles = null,
        Guid projectId = default,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(VideoScratchSpace.Create(options, executionId, stepId, logger), runner, projectFiles, projectId, logger);
    }

    /// <summary>
    /// A workspace rooted at <paramref name="rootPath"/> itself rather than at the scratch layout
    /// <c>{ScratchPath}/{executionId}/{stepId}</c>. Used only by the local-path compatibility
    /// overloads (<see cref="ForLocalFile"/>); the scratch-rooted factories above are the shape the
    /// pipeline is meant to grow into.
    /// </summary>
    public static LocalMediaWorkspace CreateAt(string rootPath, IVideoToolRunner runner, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Workspace root cannot be empty.", nameof(rootPath));

        return new LocalMediaWorkspace(Path.GetFullPath(rootPath), runner, projectFiles: null, projectId: default, logger);
    }

    /// <summary>
    /// <see cref="CreateAt(string, IVideoToolRunner, ILogger?)"/> whose ffmpeg and ffprobe calls all run at
    /// <paramref name="priority"/>. Background work that reuses the analysis passes (the pass cache warm-up of a
    /// derivative run) uses it with <see cref="VideoToolPriority.Low"/>, so those passes take the derivative lane
    /// and never the lane a workflow waits on.
    /// </summary>
    public static LocalMediaWorkspace CreateAt(string rootPath, IVideoToolRunner runner, VideoToolPriority priority, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Workspace root cannot be empty.", nameof(rootPath));

        return new LocalMediaWorkspace(Path.GetFullPath(rootPath), runner, projectFiles: null, projectId: default, logger, priority);
    }

    /// <summary>
    /// A workspace rooted at the directory holding <paramref name="localFilePath"/>, with that file
    /// itself addressed as the virtual path <see cref="VirtualPathOf"/> returns for it.
    /// <para>
    /// This exists so a media service that still takes an absolute local path can run on this seam
    /// without changing behaviour (the executors that drove those overloads — VideoCompile, then
    /// VideoAnalyze — now hold a workspace, so it serves direct-construction callers): materializing
    /// the returned virtual path yields the very same absolute path, so the argv is byte-identical.
    /// It carries no object-store access — <see cref="StageAsync"/> and <see cref="PublishAsync"/>
    /// throw, because a caller that hands over a bare local path has no project scope to stage into.
    /// </para>
    /// </summary>
    public static LocalMediaWorkspace ForLocalFile(string localFilePath, IVideoToolRunner runner, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(localFilePath))
            throw new ArgumentException("File path cannot be empty.", nameof(localFilePath));

        string fullPath = Path.GetFullPath(localFilePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException($"Cannot determine a workspace root for '{localFilePath}'.", nameof(localFilePath));

        return new LocalMediaWorkspace(directory, runner, projectFiles: null, projectId: default, logger);
    }

    /// <summary>
    /// The virtual path naming <paramref name="localFilePath"/> inside <paramref name="rootPath"/>.
    /// Throws rather than guessing when the file is not inside that root — a file name alone would
    /// silently address a <i>different</i> file of the same name in the workspace, which is exactly
    /// the class of bug the containment assertion exists to prevent.
    /// </summary>
    public static string VirtualPathOf(string localFilePath, string rootPath)
    {
        string fullPath = Path.GetFullPath(localFilePath);
        string root = Path.GetFullPath(rootPath);
        string relative = Path.GetRelativePath(root, fullPath);

        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative == ".")
        {
            throw new UnauthorizedAccessException($"Path '{fullPath}' is not inside the media workspace '{root}'.");
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public async Task StageAsync(string storageKey, string virtualPath, CancellationToken ct)
    {
        IProjectFileWorkspace projectFiles = RequireProjectScope(nameof(StageAsync));
        string destination = await MaterializeAsync(virtualPath, ct).ConfigureAwait(false);

        // Downloads straight to the destination (streamed multipart, never buffered): the same call
        // the executors make today, with the destination resolved through the workspace instead of
        // built with scratch.GetPath.
        await projectFiles.DownloadStorageKeyToFileAsync(_projectId, storageKey, destination, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StageAsync(string storageKey, string virtualPath, long maxBytes, CancellationToken ct)
    {
        IProjectFileWorkspace projectFiles = RequireProjectScope(nameof(StageAsync));
        string destination = await MaterializeAsync(virtualPath, ct).ConfigureAwait(false);
        await projectFiles.DownloadStorageKeyToFileAsync(_projectId, storageKey, destination, maxBytes, ct).ConfigureAwait(false);
    }

    // A Normal workspace calls the runner's plain overloads, exactly as before a priority existed here.
    public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
        _priority == VideoToolPriority.Normal
            ? _runner.RunFfmpegAsync(argv, timeout, ct)
            : _runner.RunFfmpegAsync(argv, timeout, ct, _priority);

    public Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
        _priority == VideoToolPriority.Normal
            ? _runner.RunFfmpegAsync(argv, timeout, ct, onStdOutLine)
            : _runner.RunFfmpegAsync(argv, timeout, ct, onStdOutLine, _priority);

    public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
        _priority == VideoToolPriority.Normal
            ? _runner.RunFfprobeAsync(argv, timeout, ct)
            : _runner.RunFfprobeAsync(argv, timeout, ct, _priority);

    public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string resolved = Resolve(virtualPath);
        string? directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        return Task.FromResult(resolved);
    }

    public async Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        // The same "create the parent directory, then write" shape MaterializeAsync already had, so a
        // caller that used to do Materialize + File.WriteAllText keeps writing to the same path.
        string resolved = Resolve(virtualPath);
        string? directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllBytesAsync(resolved, content, ct).ConfigureAwait(false);
    }

    public Task WriteTextAsync(string virtualPath, string content, Encoding encoding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        // Byte-for-byte what File.WriteAllTextAsync(path, content, encoding) writes: the encoding's
        // preamble (empty for UTF-8-no-BOM, EF BB BF for Encoding.UTF8), then the encoded text.
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(content);
        byte[] bytes = preamble.Length == 0 ? body : [.. preamble, .. body];
        return WriteBytesAsync(virtualPath, bytes, ct);
    }

    public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) =>
        ReadAsync(virtualPath, maxBytes: int.MaxValue, ct);

    public async Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct)
    {
        string resolved = Resolve(virtualPath);

        // Length is checked before anything is allocated: a remote workspace asks for at most 4 MiB
        // inline, and a local one must fail the same way rather than quietly hand back a short
        // buffer that a frame-count computation would read as a shorter video.
        FileInfo info = new(resolved);
        if (!info.Exists)
            throw new FileNotFoundException($"Media workspace file '{virtualPath}' does not exist.", resolved);
        if (info.Length > maxBytes)
            throw new MediaWorkspaceFileTooLargeException(virtualPath, info.Length, maxBytes);

        byte[] bytes = new byte[info.Length];
        await using FileStream stream = new(
            resolved, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous);
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string resolved = Resolve(virtualPath);
        FileInfo info = new(resolved);
        return Task.FromResult(info.Exists
            ? new MediaWorkspaceStat(Exists: true, Bytes: info.Length)
            : new MediaWorkspaceStat(Exists: false, Bytes: 0));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The local arm of the document path: the file is on this host, so it is uploaded from its
    /// resolved path exactly as the call sites did before this seam existed.
    /// </remarks>
    public async Task<string> UploadArtifactAsync(
        string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
    {
        IProjectFileWorkspace projectFiles = RequireProjectScope(nameof(UploadArtifactAsync));
        string source = Resolve(virtualPath);
        (string category, string fileName) = DecomposeStorageKey(finalStorageKey);

        return await projectFiles
            .UploadArtifactAsync(_projectId, source, fileName, contentType, ct, category)
            .ConfigureAwait(false);
    }

    public async Task<string> PublishAsync(
        string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
    {
        string actual = await UploadArtifactAsync(virtualPath, finalStorageKey, contentType, ct).ConfigureAwait(false);

        // The media publish is the strict one: the caller registers finalStorageKey as where its
        // generated program lives, so a publish that landed somewhere else is a failure rather than a
        // redirect to be followed.
        if (!string.Equals(actual, finalStorageKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Publish of '{virtualPath}' landed at '{actual}' instead of '{finalStorageKey}'.");
        }

        _logger?.LogDebug("Published media workspace file {VirtualPath} to {StorageKey}", virtualPath, actual);
        return actual;
    }

    /// <summary>
    /// Splits <c>projects/{projectId}/{category}/{file}</c> into the two arguments
    /// <see cref="IProjectFileWorkspace.UploadArtifactAsync"/> takes, refusing anything outside this
    /// workspace's project scope. "<c>projects/{projectId}/{category}/...</c>" is the only key layout
    /// that type can produce, so a key that does not match it would land somewhere the caller did not
    /// ask for.
    /// </summary>
    private (string Category, string FileName) DecomposeStorageKey(string finalStorageKey)
    {
        string prefix = $"projects/{_projectId}/";
        if (!finalStorageKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"Storage key '{finalStorageKey}' is not in the workspace's project scope '{prefix}'.");
        }

        string relative = finalStorageKey[prefix.Length..];
        int separator = relative.IndexOf('/');
        if (separator <= 0 || separator == relative.Length - 1)
        {
            throw new ArgumentException(
                $"Storage key '{finalStorageKey}' must name a category and a file below it.", nameof(finalStorageKey));
        }

        return (relative[..separator], relative[(separator + 1)..]);
    }

    private IProjectFileWorkspace RequireProjectScope(string operation) =>
        _projectFiles ?? throw new NotSupportedException(
            $"{operation} needs object storage, and this workspace has no project scope. Create it with " +
            $"'{nameof(Create)}(scratch, runner, projectFiles, projectId)' rather than '{nameof(ForLocalFile)}'.");

    /// <summary>
    /// The single place a virtual path becomes a filesystem path. It refuses an empty name and any
    /// rooted path up front (a rooted path would otherwise be combined away and only then caught by
    /// the containment check, which makes the reason illegible), then applies the scratch
    /// containment rule every other scratch path in this feature already goes through.
    /// </summary>
    private string Resolve(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
            throw new ArgumentException("Virtual path cannot be empty.", nameof(virtualPath));

        if (Path.IsPathRooted(virtualPath))
        {
            throw new ArgumentException(
                $"Virtual path '{virtualPath}' must be session-relative, not absolute.", nameof(virtualPath));
        }

        string candidate = Path.GetFullPath(Path.Combine(RootPath, virtualPath));
        VideoScratchSpace.ValidateScratchScope(RootPath, candidate);
        return candidate;
    }
}