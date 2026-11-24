using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Compositions;

/// <summary>
/// Layer 1 of the composition-revision library (docs/design/composition-revisions.md): where a
/// composition workspace's versions live — rows in <c>composition_workspaces</c> /
/// <c>composition_workspace_versions</c> and archives in the object store. Knows nothing about
/// sandboxes or agents, so the capture/restore layer, the revision runner and any future front-end
/// (the editor-v2 live mode) all share one place that assigns version numbers and verifies digests.
/// </summary>
public interface ICompositionWorkspaceStore
{
    /// <summary>The lineage workspace of <paramref name="originExecutionId"/>, created on first use.</summary>
    Task<CompositionWorkspace> GetOrCreateOriginWorkspaceAsync(
        Guid projectId, Guid organizationId, Guid originExecutionId, Guid? workflowDefinitionId, CancellationToken ct);

    Task<CompositionWorkspace?> FindWorkspaceAsync(Guid workspaceId, CancellationToken ct);

    Task<CompositionWorkspaceVersion?> FindVersionAsync(Guid versionId, CancellationToken ct);

    /// <summary>The highest-numbered version of the workspace, or null when it has none yet.</summary>
    Task<CompositionWorkspaceVersion?> LatestVersionAsync(Guid workspaceId, CancellationToken ct);

    /// <summary>
    /// Upload <paramref name="archive"/> and append it as the workspace's next version. The object is
    /// written BEFORE the row (a row never points at a missing object) under a key that names the
    /// version id, so two concurrent appends can never overwrite each other's archive; the version
    /// NUMBER is arbitrated by the unique (workspace, number) index and retried on a clash.
    /// </summary>
    Task<CompositionWorkspaceVersion> AppendVersionAsync(
        CompositionVersionDraft draft, CompositionArchiveFile archive, CancellationToken ct);

    /// <summary>Download a version's archive to scratch and verify it against the recorded SHA-256.</summary>
    /// <exception cref="CompositionWorkspaceException">With <c>WORKSPACE_ARCHIVE_CORRUPT</c> on a digest mismatch, <c>WORKSPACE_ARCHIVE_MISSING</c> when the object is gone.</exception>
    Task<CompositionArchiveFile> DownloadArchiveAsync(CompositionWorkspaceVersion version, CancellationToken ct);

    /// <summary>The revision request whose execution is <paramref name="executionId"/>, if any.</summary>
    Task<CompositionRevision?> FindRevisionByExecutionAsync(Guid executionId, CancellationToken ct);

    /// <summary>Record that <paramref name="revisionId"/> produced <paramref name="versionId"/>. Idempotent.</summary>
    Task LinkRevisionResultAsync(Guid revisionId, Guid versionId, CancellationToken ct);

    /// <summary>The newest playable render a run produced (an <c>outputFiles</c> key on an agent step), for recovering pre-capture runs.</summary>
    Task<string?> FindLatestRenderKeyAsync(Guid executionId, CancellationToken ct);

    /// <summary>
    /// True when <paramref name="version"/> may hold a copy of a file quarantined since: it was produced by a run that
    /// began at or before the project's quarantine marker (the version's own CreatedAt when no run is linked), or
    /// that cannot be read. A restore of such a version is refused. A store without the project's state refuses
    /// nothing.
    /// </summary>
    Task<bool> PredatesQuarantineAsync(CompositionWorkspaceVersion version, CancellationToken ct) => Task.FromResult(false);

    /// <summary>
    /// True when the run <paramref name="executionId"/> (whose sandbox would be captured) first started at or before the
    /// project's quarantine marker, or that cannot be read. A store without the project's state refuses nothing.
    /// </summary>
    Task<bool> RunPredatesQuarantineAsync(Guid projectId, Guid executionId, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Everything about a version except its number and archive, which the store assigns/records.</summary>
public sealed record CompositionVersionDraft
{
    public required Guid WorkspaceId { get; init; }
    public required Guid ProjectId { get; init; }
    public Guid? ParentVersionId { get; init; }
    public CompositionVersionSource Source { get; init; } = CompositionVersionSource.RenderCapture;
    public string? CompositionId { get; init; }
    public double? Fps { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationInFrames { get; init; }
    public string? SceneManifestJson { get; init; }
    public string? RenderOutputStorageKey { get; init; }
    public Guid? CreatedByExecutionId { get; init; }
    public Guid? CreatedByUserId { get; init; }
    public Guid? RevisionId { get; init; }
    public string? Instruction { get; init; }
    public int FileCount { get; init; }
}

/// <summary>
/// A workspace archive spooled to local scratch, with the digest and length computed while it was
/// written (by the engine — never taken from the sandbox). Disposing deletes the file.
/// </summary>
public sealed class CompositionArchiveFile : IAsyncDisposable
{
    private CompositionArchiveFile(string path, string sha256, long length)
    {
        Path = path;
        Sha256 = sha256;
        Length = length;
    }

    public string Path { get; }

    /// <summary>Lower-case hex SHA-256 of the file.</summary>
    public string Sha256 { get; }

    public long Length { get; }

    /// <summary>Opens the spooled archive for reading (the caller disposes the stream).</summary>
    public Stream OpenRead() => new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    /// <summary>
    /// Copy <paramref name="source"/> to a new scratch file under <paramref name="scratchDirectory"/>,
    /// hashing as it goes, refusing anything over <paramref name="maxBytes"/>.
    /// </summary>
    public static async Task<CompositionArchiveFile> SpoolAsync(
        Stream source, string scratchDirectory, long maxBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(scratchDirectory);
        string path = System.IO.Path.Combine(scratchDirectory, $"workspace-{Guid.NewGuid():N}.tar.gz");
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (maxBytes > 0 && total > maxBytes)
                    {
                        throw new CompositionWorkspaceException(
                            CompositionWorkspaceException.ArchiveTooLarge,
                            $"The workspace archive is larger than the {maxBytes}-byte limit.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            return new CompositionArchiveFile(path, Convert.ToHexStringLower(hash.GetHashAndReset()), total);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        TryDelete(Path);
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Scratch cleanup is best effort: a leftover file is reclaimed with the scratch volume and
            // must never turn a finished capture or restore into a failure.
        }
    }
}

/// <summary>A composition-workspace failure with a SCREAMING_SNAKE code the failure classifier maps.</summary>
public sealed class CompositionWorkspaceException : Exception
{
    public const string ArchiveTooLarge = "WORKSPACE_ARCHIVE_TOO_LARGE";
    public const string ArchiveCorrupt = "WORKSPACE_ARCHIVE_CORRUPT";
    public const string ArchiveMissing = "WORKSPACE_ARCHIVE_MISSING";
    public const string Unavailable = "WORKSPACE_UNAVAILABLE";
    public const string RestoreFailed = "WORKSPACE_RESTORE_FAILED";
    public const string TransferNotSupported = "WORKSPACE_TRANSFER_NOT_SUPPORTED";

    public CompositionWorkspaceException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Scratch location and caps for workspace archives on the engine host.</summary>
public sealed class CompositionWorkspaceOptions
{
    public const string SectionName = "CompositionWorkspaces";

    /// <summary>Master switch for capture after a render (default on). Restore/revision work regardless.</summary>
    public bool CaptureEnabled { get; set; } = true;

    /// <summary>Where archives are spooled. Empty = <c>VideoEditing:ScratchPath</c>/workspaces, else the OS temp dir.</summary>
    public string? ScratchPath { get; set; }

    /// <summary>The largest archive the engine will spool or restore (compressed). Matches the sandbox's own default cap.</summary>
    public long MaxArchiveBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Deadline for one archive transfer to or from the sandbox.</summary>
    public int TransferTimeoutSeconds { get; set; } = 600;
}

/// <summary>EF + S3 implementation of <see cref="ICompositionWorkspaceStore"/>. Singleton-safe: one short-lived context per call.</summary>
public sealed class CompositionWorkspaceStore : ICompositionWorkspaceStore
{
    private const int MaxAppendAttempts = 4;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _scratch;
    private readonly long _maxArchiveBytes;
    private readonly ILogger<CompositionWorkspaceStore> _logger;

    public CompositionWorkspaceStore(
        IServiceScopeFactory scopeFactory,
        IAmazonS3 s3,
        IConfiguration configuration,
        ILogger<CompositionWorkspaceStore> logger)
    {
        _scopeFactory = scopeFactory;
        _s3 = s3;
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
        CompositionWorkspaceOptions options = ReadOptions(configuration);
        _scratch = ResolveScratch(configuration, options);
        _maxArchiveBytes = options.MaxArchiveBytes;
        _logger = logger;
    }

    /// <summary>The bound options (a missing section yields the defaults).</summary>
    public static CompositionWorkspaceOptions ReadOptions(IConfiguration configuration) =>
        configuration.GetSection(CompositionWorkspaceOptions.SectionName).Get<CompositionWorkspaceOptions>()
        ?? new CompositionWorkspaceOptions();

    /// <summary>The scratch directory archives are spooled to.</summary>
    public static string ResolveScratch(IConfiguration configuration, CompositionWorkspaceOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ScratchPath))
            return options.ScratchPath;
        string? video = configuration["VideoEditing:ScratchPath"];
        return Path.Combine(string.IsNullOrWhiteSpace(video) ? Path.GetTempPath() : video, "workspaces");
    }

    public async Task<CompositionWorkspace> GetOrCreateOriginWorkspaceAsync(
        Guid projectId, Guid organizationId, Guid originExecutionId, Guid? workflowDefinitionId, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            CompositionWorkspace? existing = await db.CompositionWorkspaces.AsNoTracking()
                .FirstOrDefaultAsync(w => w.OriginExecutionId == originExecutionId, ct);
            if (existing is not null)
                return existing;

            CompositionWorkspace created = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OrganizationId = organizationId,
                OriginExecutionId = originExecutionId,
                OriginWorkflowDefinitionId = workflowDefinitionId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.CompositionWorkspaces.Add(created);
            try
            {
                await db.SaveChangesAsync(ct);
                return created;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Lost the unique (origin_execution_id) race to a concurrent capture: read theirs.
            }
        }
    }

    public async Task<CompositionWorkspace?> FindWorkspaceAsync(Guid workspaceId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        return await db.CompositionWorkspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
    }

    public async Task<CompositionWorkspaceVersion?> FindVersionAsync(Guid versionId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        return await db.CompositionWorkspaceVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct);
    }

    public async Task<bool> PredatesQuarantineAsync(CompositionWorkspaceVersion version, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        return await CompositionVersionQuarantine.PredatesAsync(db, version, ct);
    }

    public async Task<bool> RunPredatesQuarantineAsync(Guid projectId, Guid executionId, CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            return await CompositionVersionQuarantine.RunPredatesAsync(db, projectId, executionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true; // A state that cannot be read captures nothing.
        }
    }

    public async Task<CompositionWorkspaceVersion?> LatestVersionAsync(Guid workspaceId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        return await db.CompositionWorkspaceVersions.AsNoTracking()
            .Where(v => v.WorkspaceId == workspaceId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<CompositionWorkspaceVersion> AppendVersionAsync(
        CompositionVersionDraft draft, CompositionArchiveFile archive, CancellationToken ct)
    {
        Guid versionId = Guid.NewGuid();
        string key = CompositionWorkspaceKeys.ArchiveKey(draft.ProjectId, draft.WorkspaceId, versionId);

        // Object first: a row must never name an archive that is not there. A row that fails to land
        // after this leaves an unreferenced object under the project's prefix, which storage metering
        // counts and account purge removes with everything else under it.
        await using (Stream upload = archive.OpenRead())
        {
            PutObjectRequest put = new()
            {
                BucketName = _bucket,
                Key = key,
                InputStream = upload,
                ContentType = "application/gzip",
                AutoCloseStream = false
            };
            put.Metadata["category"] = "agentFiles";
            put.Metadata["sha256"] = archive.Sha256;
            await _s3.PutObjectAsync(put, ct).ConfigureAwait(false);
        }

        for (int attempt = 1; ; attempt++)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

            CompositionWorkspace workspace = await db.CompositionWorkspaces
                .FirstOrDefaultAsync(w => w.Id == draft.WorkspaceId, ct)
                ?? throw new CompositionWorkspaceException(
                    CompositionWorkspaceException.Unavailable, $"Composition workspace {draft.WorkspaceId} does not exist.");

            int highest = await db.CompositionWorkspaceVersions
                .Where(v => v.WorkspaceId == draft.WorkspaceId)
                .Select(v => (int?)v.VersionNumber)
                .MaxAsync(ct) ?? 0;
            int number = Math.Max(highest, workspace.LatestVersionNumber) + 1;

            CompositionWorkspaceVersion version = new()
            {
                Id = versionId,
                WorkspaceId = draft.WorkspaceId,
                ProjectId = draft.ProjectId,
                VersionNumber = number,
                ParentVersionId = draft.ParentVersionId,
                StorageKey = key,
                Sha256 = archive.Sha256,
                SizeBytes = archive.Length,
                FileCount = draft.FileCount,
                Source = draft.Source,
                CompositionId = Truncate(draft.CompositionId, 200),
                Fps = draft.Fps,
                Width = draft.Width,
                Height = draft.Height,
                DurationInFrames = draft.DurationInFrames,
                SceneManifestJson = draft.SceneManifestJson,
                RenderOutputStorageKey = draft.RenderOutputStorageKey,
                CreatedByExecutionId = draft.CreatedByExecutionId,
                CreatedByUserId = draft.CreatedByUserId,
                RevisionId = draft.RevisionId,
                Instruction = Truncate(draft.Instruction, 4000),
                CreatedAt = DateTime.UtcNow
            };

            db.CompositionWorkspaceVersions.Add(version);
            workspace.LatestVersionNumber = number;
            workspace.UpdatedAt = DateTime.UtcNow;
            try
            {
                await db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Composition workspace {WorkspaceId} v{Version} recorded ({SizeBytes} bytes, {Source}, parent {ParentVersionId})",
                    draft.WorkspaceId, number, archive.Length, draft.Source, draft.ParentVersionId);
                return version;
            }
            catch (DbUpdateException ex) when (attempt < MaxAppendAttempts)
            {
                // Another append took this number (unique (workspace_id, version_number)); take the next.
                _logger.LogDebug(ex, "Version number {Version} of workspace {WorkspaceId} was taken; retrying", number, draft.WorkspaceId);
            }
        }
    }

    public async Task<CompositionArchiveFile> DownloadArchiveAsync(CompositionWorkspaceVersion version, CancellationToken ct)
    {
        if (!CompositionWorkspaceKeys.BelongsTo(version.StorageKey, version.ProjectId))
        {
            throw new CompositionWorkspaceException(
                CompositionWorkspaceException.ArchiveCorrupt,
                $"Version {version.Id} names an archive outside its project's workspace prefix.");
        }

        GetObjectResponse response;
        try
        {
            response = await _s3.GetObjectAsync(_bucket, version.StorageKey, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            throw new CompositionWorkspaceException(
                CompositionWorkspaceException.ArchiveMissing,
                $"The archive of workspace version {version.VersionNumber} is no longer in storage.", ex);
        }

        using (response)
        {
            CompositionArchiveFile file = await CompositionArchiveFile
                .SpoolAsync(response.ResponseStream, _scratch, _maxArchiveBytes, ct)
                .ConfigureAwait(false);

            if (!string.Equals(file.Sha256, version.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await file.DisposeAsync();
                throw new CompositionWorkspaceException(
                    CompositionWorkspaceException.ArchiveCorrupt,
                    $"The archive of workspace version {version.VersionNumber} does not match its recorded digest.");
            }

            return file;
        }
    }

    public async Task<CompositionRevision?> FindRevisionByExecutionAsync(Guid executionId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        return await db.CompositionRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.ExecutionId == executionId, ct);
    }

    public async Task LinkRevisionResultAsync(Guid revisionId, Guid versionId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        CompositionRevision? revision = await db.CompositionRevisions.FirstOrDefaultAsync(r => r.Id == revisionId, ct);
        if (revision is null || revision.ResultVersionId == versionId)
            return;
        revision.ResultVersionId = versionId;
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> FindLatestRenderKeyAsync(Guid executionId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        return await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == executionId
                        && r.SupersededAt == null
                        && r.OutputStorageKey != null
                        && r.OutputStorageKey.Contains("/outputFiles/")
                        && (r.WorkflowStep.StepType == StepType.Agent || r.WorkflowStep.StepType == StepType.Revise))
            .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
            .Select(r => r.OutputStorageKey)
            .FirstOrDefaultAsync(ct);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
