using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;

namespace ReelBolt.WorkflowEngine.Services.Storage;

public class ProjectFileWorkspace : IProjectFileWorkspace
{
    private readonly IAmazonS3 _s3Client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _bucketName;
    private readonly ILogger<ProjectFileWorkspace> _logger;
    private readonly ReelBolt.WorkflowEngine.Execution.IWorkflowExecutionContextAccessor? _contextAccessor;

    public ProjectFileWorkspace(
        IAmazonS3 s3Client,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ProjectFileWorkspace> logger,
        ReelBolt.WorkflowEngine.Execution.IWorkflowExecutionContextAccessor? contextAccessor = null)
    {
        _contextAccessor = contextAccessor;
        _s3Client = s3Client;
        _scopeFactory = scopeFactory;
        _bucketName = configuration["MinIO:BucketName"] ?? "reelforge";
        _logger = logger;
    }

    public async Task<IReadOnlyList<ProjectWorkspaceFile>> ListFilesAsync(Guid projectId, CancellationToken ct)
    {
        _logger.LogInformation("Listing project files for project {ProjectId}", projectId);

        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        List<ProjectWorkspaceFile> files = await db.ProjectFiles
            .Where(f => f.ProjectId == projectId && f.CsamStatus != CsamScanStatus.Quarantined && f.ModerationStatus != FileModerationStatus.Quarantined)
            .OrderByDescending(f => f.UploadedAt)
            .Select(f => new ProjectWorkspaceFile(
                f.Id,
                f.ProjectId,
                f.OriginalFileName,
                f.OriginalPath,
                f.Category,
                f.StorageKey,
                f.MimeType,
                f.SizeBytes,
                f.UploadedAt,
                f.AgentSummary,
                f.DirectoryPath))
            .ToListAsync(ct);

        // A platform-made file that may predate the project's markers, or whose markers cannot be read, is absent.
        Func<string, DateTime, DateTime?, bool> withheld = await BuildWithheldAsync(db, projectId, ct);
        Dictionary<Guid, DateTime?> producedAt = (await db.ProjectFiles
                .Where(f => f.ProjectId == projectId && f.OutputProducedAt != null)
                .Select(f => new { f.Id, f.OutputProducedAt })
                .ToListAsync(ct))
            .ToDictionary(f => f.Id, f => f.OutputProducedAt);
        files = files.Where(f => !withheld(f.StorageKey, f.UploadedAt, producedAt.GetValueOrDefault(f.Id))).ToList();

        _logger.LogInformation("Found {FileCount} files for project {ProjectId}", files.Count, projectId);
        return files;
    }

    public async Task<string> ReadFileAsync(Guid projectId, string fileReference, CancellationToken ct)
    {
        _logger.LogInformation(
            "Reading project file for project {ProjectId} using reference {FileReference}",
            projectId,
            fileReference);

        ProjectFile file = await ResolveFileAsync(projectId, fileReference, ct);
        ValidateProjectScope(projectId, file.StorageKey);

        // Declaration gate: refuse an unambiguous binary family before a single byte is fetched. This
        // only ever refuses — the sniff below still decides every file it does not refuse — and it is
        // deliberately NOT an admission rule: an allowlisted extension admits nothing on its own.
        if (ProjectFileTextPolicy.IsKnownBinary(file.MimeType, file.OriginalFileName))
            throw BinaryFileException(file);

        GetObjectResponse response = await _s3Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucketName,
            Key = file.StorageKey
        }, ct);

        await using Stream stream = response.ResponseStream;

        // Sniff-first: the bytes decide, not the extension. The shared extension/MIME allowlist
        // (IsLikelyTextFile) is a weak signal in both directions — a text file whose extension nobody
        // listed (an .scss, a .csproj, an unknown source file) is still read, and a genuine binary
        // wearing an allowlisted extension (an .mp3 renamed .md) is still refused. Without the sniff,
        // a binary was decoded as UTF-8 text and returned whole — observed in production as a single
        // MP3 producing ~2.9M "chars" of mojibake that blew the model's context and 400'd the whole
        // request.
        byte[] head = await ProjectFileTextPolicy.ReadHeadAsync(stream, ct);
        if (!ProjectFileTextPolicy.LooksLikeText(head))
            throw BinaryFileException(file);

        // Decode in the encoding the sniff picked (UTF-8, UTF-16 or Windows-1252) rather than forcing
        // the whole file through UTF-8, which would mojibake a legacy source, and bound the read: a
        // file past MaxReadBytes is refused rather than silently truncated (EditProjectFile reads
        // through this path and must never edit a partial file).
        (string content, bool truncated) = await ProjectFileTextPolicy.ReadTextAsync(
            head, stream, ProjectFileTextPolicy.MaxReadBytes, ct);
        if (truncated)
        {
            throw new InvalidOperationException(
                $"File '{file.OriginalFileName}' is larger than the {ProjectFileTextPolicy.MaxReadBytes} " +
                "byte text read limit and cannot be read in full. It is refused rather than truncated " +
                "so a partial file is never returned, summarised or edited.");
        }

        // Whole-body validation: the sniff only saw the head, so a text prefix on a binary body would
        // otherwise be returned whole. This is the same implausibility test the sniff uses, over the
        // whole decoded string.
        if (!ProjectFileTextPolicy.LooksLikeDecodedText(content))
            throw BinaryFileException(file);

        _logger.LogInformation(
            "Read project file {FileId} ({StorageKey}) for project {ProjectId}",
            file.Id,
            file.StorageKey,
            projectId);

        return content;
    }

    private static InvalidOperationException BinaryFileException(ProjectFile file) =>
        new($"File '{file.OriginalFileName}' ({file.MimeType ?? "unknown type"}, {file.SizeBytes:N0} bytes) " +
            "is a binary file and cannot be read as text. Use its file name and any metadata you were " +
            "already given instead of reading its content.");

    public async Task<ProjectWorkspaceFile> WriteTextFileAsync(
        Guid projectId,
        string fileName,
        string content,
        string contentType,
        CancellationToken ct,
        string category = "agentFiles",
        string? originalPath = null)
    {
        _logger.LogInformation(
            "Writing project text file for project {ProjectId}: fileName={FileName}, category={Category}",
            projectId,
            fileName,
            category);

        Guid fileId = Guid.NewGuid();

        if (category != "userFiles" && category != "agentFiles" && category != "outputFiles")
            category = "agentFiles";

        string normalizedOriginalPath = ProjectFilePath.NormalizeRelativePath(
            string.IsNullOrWhiteSpace(originalPath) ? fileName : originalPath);
        string normalizedFileName = ProjectFilePath.GetFileName(normalizedOriginalPath);
        string? directoryPath = ProjectFilePath.GetDirectoryPath(normalizedOriginalPath);
        string storageFileName = ProjectFilePath.BuildStorageFileName(fileId, normalizedFileName);

        string storagePrefix = $"projects/{projectId}/{category}/";
        string storageKey = string.IsNullOrWhiteSpace(directoryPath)
            ? $"{storagePrefix}{storageFileName}"
            : $"{storagePrefix}{directoryPath}/{storageFileName}";

        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await using MemoryStream stream = new(bytes);
        PutObjectRequest request = new()
        {
            BucketName = _bucketName,
            Key = storageKey,
            InputStream = stream,
            ContentType = contentType
        };
        request.Metadata["project-id"] = projectId.ToString();
        request.Metadata["project-file-id"] = fileId.ToString();
        request.Metadata["generated-by"] = "workflow-engine";
        request.Metadata["category"] = category;
        request.Metadata["original-path"] = normalizedOriginalPath;
        request.Metadata["original-file-name"] = normalizedFileName;
        request.Metadata["storage-file-name"] = storageFileName;

        await _s3Client.PutObjectAsync(request, ct);

        string metadataJson = JsonSerializer.Serialize(request.Metadata.Keys
            .ToDictionary(k => k, k => request.Metadata[k]));

        ProjectFile projectFile = new()
        {
            Id = fileId,
            ProjectId = projectId,
            OriginalFileName = normalizedFileName,
            OriginalPath = normalizedOriginalPath,
            DirectoryPath = directoryPath,
            Category = category,
            StorageKey = storageKey,
            StorageFileName = storageFileName,
            StorageBucket = _bucketName,
            StoragePrefix = storagePrefix,
            StorageMetadataJson = metadataJson,
            MimeType = contentType,
            SizeBytes = bytes.LongLength,
            SummaryStatus = SummaryStatus.Pending,
            UploadedAt = DateTime.UtcNow
        };

        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        projectFile.OutputProducedAt = await OutputProducedAtAsync(db, projectId, category, storageKey, ct);
        db.ProjectFiles.Add(projectFile);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Saved project file metadata and object for project {ProjectId}: fileId={FileId}, storageKey={StorageKey}",
            projectId,
            projectFile.Id,
            projectFile.StorageKey);

        return new ProjectWorkspaceFile(
            projectFile.Id,
            projectFile.ProjectId,
            projectFile.OriginalFileName,
            projectFile.OriginalPath,
            projectFile.Category,
            projectFile.StorageKey,
            projectFile.MimeType,
            projectFile.SizeBytes,
            projectFile.UploadedAt,
            projectFile.AgentSummary);
    }

    public Task DownloadStorageKeyToFileAsync(
        Guid projectId, string storageKey, string destinationPath, CancellationToken ct) =>
        DownloadBoundedAsync(projectId, storageKey, destinationPath, maxBytes: null, ct);

    public Task DownloadStorageKeyToFileAsync(
        Guid projectId, string storageKey, string destinationPath, long maxBytes, CancellationToken ct) =>
        DownloadBoundedAsync(projectId, storageKey, destinationPath, maxBytes, ct);

    private async Task DownloadBoundedAsync(
        Guid projectId, string storageKey, string destinationPath, long? maxBytes, CancellationToken ct)
    {
        ValidateProjectScope(projectId, storageKey);

        _logger.LogInformation(
            "Downloading storage key {StorageKey} to {DestinationPath} for project {ProjectId}",
            storageKey, destinationPath, projectId);

        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        using GetObjectResponse response = await _s3Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucketName,
            Key = storageKey
        }, ct);

        // S-67: the object's own length is known before the body, so an object already over the caller's
        // bound is refused with nothing read at all.
        if (maxBytes is { } declaredLimit && (response.ContentLength > declaredLimit || response.ContentLength < 0))
            throw new StorageObjectTooLargeException(declaredLimit, response.ContentLength < 0 ? null : response.ContentLength);

        await using Stream responseStream = response.ResponseStream;

        // Stream straight to disk — never buffer the object in a MemoryStream/byte[] (R21), which
        // matters here because this method exists specifically to bring large source video files
        // from S3 into local scratch space for ffmpeg to operate on. Any failure (the cap, a dropped
        // connection, a cancellation) removes the partial copy, so a half-written file never looks
        // like a staged one.
        try
        {
            await using FileStream fileStream = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            if (maxBytes is not { } limit)
            {
                await responseStream.CopyToAsync(fileStream, ct);
            }
            else
            {
                // ...and when the caller states a bound the copy stops at it, so a response whose length lied
                // (or no length at all) can still never write more than the bound to disk.
                byte[] buffer = new byte[81_920];
                long written = 0;
                int read;
                while ((read = await responseStream.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (written > limit) throw new StorageObjectTooLargeException(limit, observedBytes: written);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
        }
        catch
        {
            TryDeletePartial(destinationPath);
            throw;
        }

        _logger.LogInformation(
            "Downloaded storage key {StorageKey} to {DestinationPath} for project {ProjectId}",
            storageKey, destinationPath, projectId);
    }


    private void TryDeletePartial(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not remove a partial download at {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not remove a partial download at {Path}", path);
        }
    }

    public async Task<ProjectWorkspaceFile> UploadBinaryFileAsync(
        Guid projectId,
        string localFilePath,
        string fileName,
        string contentType,
        SummaryStatus summaryStatus,
        FileIndexingStatus indexingStatus,
        CancellationToken ct,
        string category = "outputFiles",
        string? originalPath = null)
    {
        _logger.LogInformation(
            "Uploading binary project file for project {ProjectId}: fileName={FileName}, category={Category}",
            projectId, fileName, category);

        if (category != "userFiles" && category != "agentFiles" && category != "outputFiles")
            category = "outputFiles";

        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException($"Local file '{localFilePath}' does not exist.", localFilePath);
        }

        Guid fileId = Guid.NewGuid();
        string normalizedOriginalPath = ProjectFilePath.NormalizeRelativePath(
            string.IsNullOrWhiteSpace(originalPath) ? fileName : originalPath);
        string normalizedFileName = ProjectFilePath.GetFileName(normalizedOriginalPath);
        string? directoryPath = ProjectFilePath.GetDirectoryPath(normalizedOriginalPath);
        string storageFileName = ProjectFilePath.BuildStorageFileName(fileId, normalizedFileName);
        string storageKey = ProjectFilePath.BuildStorageKey(projectId, category, directoryPath, storageFileName);
        string storagePrefix = ProjectFilePath.BuildStoragePrefix(projectId, category);

        ValidateProjectScope(projectId, storageKey);

        Dictionary<string, string> metadata = new()
        {
            ["project-id"] = projectId.ToString(),
            ["project-file-id"] = fileId.ToString(),
            ["generated-by"] = "workflow-engine",
            ["category"] = category,
            ["original-path"] = normalizedOriginalPath,
            ["original-file-name"] = normalizedFileName,
            ["storage-file-name"] = storageFileName
        };

        long sizeBytes = new FileInfo(localFilePath).Length;

        // TransferUtility streams from the FileStream in multipart chunks — the whole file is
        // never materialized as a single in-memory buffer (R21), which matters here because this
        // path is exactly how a compiled edited video (potentially hundreds of MB) reaches S3.
        TransferUtility transferUtility = new(_s3Client);
        await using (FileStream fileStream = new(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            TransferUtilityUploadRequest uploadRequest = new()
            {
                BucketName = _bucketName,
                Key = storageKey,
                InputStream = fileStream,
                ContentType = contentType,
                AutoCloseStream = false
            };
            foreach (KeyValuePair<string, string> kv in metadata)
                uploadRequest.Metadata[kv.Key] = kv.Value;

            await transferUtility.UploadAsync(uploadRequest, ct);
        }

        string metadataJson = JsonSerializer.Serialize(metadata);

        ProjectFile projectFile = new()
        {
            Id = fileId,
            ProjectId = projectId,
            OriginalFileName = normalizedFileName,
            OriginalPath = normalizedOriginalPath,
            DirectoryPath = directoryPath,
            Category = category,
            StorageKey = storageKey,
            StorageFileName = storageFileName,
            StorageBucket = _bucketName,
            StoragePrefix = storagePrefix,
            StorageMetadataJson = metadataJson,
            MimeType = contentType,
            SizeBytes = sizeBytes,
            // Caller-supplied, never the Pending/NotIndexed defaults: a binary media file uploaded
            // through this path must never be picked up by the text summarizer or vector chunker
            // (plan §1.5 — same trap R22 fixes on the general upload controller, independently
            // avoided here since this path never goes through that controller).
            SummaryStatus = summaryStatus,
            IndexingStatus = indexingStatus,
            UploadedAt = DateTime.UtcNow
        };

        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        projectFile.OutputProducedAt = await OutputProducedAtAsync(db, projectId, category, storageKey, ct);
        db.ProjectFiles.Add(projectFile);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Saved binary project file metadata and object for project {ProjectId}: fileId={FileId}, storageKey={StorageKey}",
            projectId, projectFile.Id, projectFile.StorageKey);

        return new ProjectWorkspaceFile(
            projectFile.Id,
            projectFile.ProjectId,
            projectFile.OriginalFileName,
            projectFile.OriginalPath,
            projectFile.Category,
            projectFile.StorageKey,
            projectFile.MimeType,
            projectFile.SizeBytes,
            projectFile.UploadedAt,
            projectFile.AgentSummary);
    }

    /// <summary>
    /// True for a row that holds an engine output (a render under outputFiles, a generated take): such a row records when
    /// its output was produced (<see cref="ProjectFile.OutputProducedAt"/>), written here once and never again.
    /// </summary>
    /// <summary>
    /// What a new output row records: the earlier of the current run's first start and now, so a run that began before a
    /// quarantine marker cannot register output that looks newer than it. The run is the ambient execution context's. An
    /// output row made with no run, or by a run whose first start is unknown, is held with the epoch (never null, which
    /// means "not an output"); that only matters once the project has a marker. Null for a row that is not an output.
    /// </summary>
    private async Task<DateTime?> OutputProducedAtAsync(
        WorkflowEngineDbContext db, Guid projectId, string category, string storageKey, CancellationToken ct)
    {
        if (!IsOutputRow(category, storageKey))
            return null;

        DateTime now = DateTime.UtcNow;
        if (_contextAccessor?.Current?.ExecutionId is not { } executionId)
            return DateTime.UnixEpoch;

        DateTime? first = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId && e.ProjectId == projectId)
            .Select(e => e.FirstStartedAt)
            .FirstOrDefaultAsync(ct);
        return first is null ? DateTime.UnixEpoch : first.Value < now ? first.Value : now;
    }

    private static bool IsOutputRow(string category, string storageKey) =>
        category == ContentQuarantineRule.OutputCategory
        || storageKey.Contains(ContentQuarantineRule.GeneratedKeyFragment, StringComparison.Ordinal);

    public async Task<ProjectWorkspaceFile> RegisterStoredObjectAsync(
        Guid projectId,
        string storageKey,
        string fileName,
        string contentType,
        long sizeBytes,
        SummaryStatus summaryStatus,
        FileIndexingStatus indexingStatus,
        CancellationToken ct,
        string category = "outputFiles")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ValidateProjectScope(projectId, storageKey);

        if (category != "userFiles" && category != "agentFiles" && category != "outputFiles")
            category = "outputFiles";

        // The size is the object store's, read here with the engine's own client. The caller's sizeBytes
        // comes from a media-session stat, which on a remote session is the runner's own claim, and
        // storage usage sums SizeBytes: an under-reported size would let a file past the storage limit.
        // A HEAD that fails throws (fail closed) rather than registering an unknown size.
        GetObjectMetadataResponse head = await _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = _bucketName,
            Key = storageKey
        }, ct);
        if (head.ContentLength != sizeBytes)
        {
            _logger.LogWarning(
                "Registering {StorageKey}: caller stated {ClaimedBytes} bytes, the object store holds {StoredBytes}; recording the stored size",
                storageKey, sizeBytes, head.ContentLength);
        }

        sizeBytes = head.ContentLength;

        Guid fileId = Guid.NewGuid();
        string normalizedFileName = ProjectFilePath.GetFileName(ProjectFilePath.NormalizeRelativePath(fileName));
        string storageFileName = storageKey.Split('/')[^1];

        // Metadata mirrors the upload path's own, so an object registered here is indistinguishable
        // from one uploaded through UploadBinaryFileAsync — the same bucket policy and lifecycle
        // rules apply to both, and the retention sweep reaches both the same way.
        Dictionary<string, string> metadata = new()
        {
            ["project-id"] = projectId.ToString(),
            ["project-file-id"] = fileId.ToString(),
            ["generated-by"] = "workflow-engine",
            ["category"] = category,
            ["original-path"] = normalizedFileName,
            ["original-file-name"] = normalizedFileName,
            ["storage-file-name"] = storageFileName,
            ["registered-without-upload"] = "true"
        };

        ProjectFile projectFile = new()
        {
            Id = fileId,
            ProjectId = projectId,
            OriginalFileName = normalizedFileName,
            OriginalPath = normalizedFileName,
            DirectoryPath = null,
            Category = category,
            StorageKey = storageKey,
            StorageFileName = storageFileName,
            StorageBucket = _bucketName,
            StoragePrefix = ProjectFilePath.BuildStoragePrefix(projectId, category),
            StorageMetadataJson = JsonSerializer.Serialize(metadata),
            MimeType = contentType,
            SizeBytes = sizeBytes,
            SummaryStatus = summaryStatus,
            IndexingStatus = indexingStatus,
            UploadedAt = DateTime.UtcNow
        };

        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        projectFile.OutputProducedAt = await OutputProducedAtAsync(db, projectId, category, storageKey, ct);
        db.ProjectFiles.Add(projectFile);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Registered already-stored object as a project file for project {ProjectId}: fileId={FileId}, storageKey={StorageKey}",
            projectId, projectFile.Id, projectFile.StorageKey);

        return new ProjectWorkspaceFile(
            projectFile.Id,
            projectFile.ProjectId,
            projectFile.OriginalFileName,
            projectFile.OriginalPath,
            projectFile.Category,
            projectFile.StorageKey,
            projectFile.MimeType,
            projectFile.SizeBytes,
            projectFile.UploadedAt,
            projectFile.AgentSummary);
    }

    public async Task<string> UploadArtifactAsync(
        Guid projectId,
        string localFilePath,
        string fileName,
        string contentType,
        CancellationToken ct,
        string category = "agentFiles")
    {
        _logger.LogInformation(
            "Uploading bare artifact for project {ProjectId}: fileName={FileName}, category={Category}",
            projectId, fileName, category);

        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException($"Local file '{localFilePath}' does not exist.", localFilePath);
        }

        // Unlike WriteTextFileAsync/UploadBinaryFileAsync, the storage file name is NOT
        // guid-prefixed: this is a bare artifact with no ProjectFile row to dedupe against, so the
        // caller's own key layout (e.g. "video-analysis/{executionId}/step-1-analysis.json", per
        // plan §1.5) is preserved verbatim under the category prefix. NormalizeRelativePath still
        // rejects ".." traversal in a caller-supplied name.
        string normalizedFileName = ProjectFilePath.NormalizeRelativePath(fileName);
        string storageKey = ProjectFilePath.BuildStorageKey(
            projectId, category, directoryPath: null, storageFileName: normalizedFileName);

        ValidateProjectScope(projectId, storageKey);

        TransferUtility transferUtility = new(_s3Client);
        await using (FileStream fileStream = new(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            TransferUtilityUploadRequest uploadRequest = new()
            {
                BucketName = _bucketName,
                Key = storageKey,
                InputStream = fileStream,
                ContentType = contentType,
                AutoCloseStream = false
            };
            uploadRequest.Metadata["project-id"] = projectId.ToString();
            uploadRequest.Metadata["generated-by"] = "workflow-engine";
            uploadRequest.Metadata["category"] = category;

            await transferUtility.UploadAsync(uploadRequest, ct);
        }

        _logger.LogInformation(
            "Uploaded bare artifact for project {ProjectId}: storageKey={StorageKey}", projectId, storageKey);

        return storageKey;
    }

    public async Task DeleteArtifactAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        ValidateDeleteScope(projectId, storageKey);

        await _s3Client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key = storageKey
        }, ct);
        _logger.LogInformation("Deleted bare artifact for project {ProjectId}: storageKey={StorageKey}", projectId, storageKey);
    }

    public async Task<bool> ArtifactExistsAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        ValidateProjectScope(projectId, storageKey);

        try
        {
            // HEAD, not GET: the caller only needs an existence answer, and an artifact can be a
            // full-length WAV — downloading it here purely to learn it is there would defeat the
            // point of checking before synthesizing.
            await _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = storageKey
            }, ct);

            return true;
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.NotFound ||
            string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
        {
            // Genuinely absent — the caller should produce it. Exactly these two shapes count:
            // S3 and MinIO/Garage-compatible stores report a missing key as either a 404 or the
            // NoSuchKey error code, and sometimes only one of the two. Everything ELSE (403,
            // 5xx, throttling, a DNS/TLS failure) is rethrown rather than reported as "missing",
            // because silently answering false would make the caller redo — and re-bill — work
            // that is sitting in the bucket already.
            _logger.LogDebug(
                "Artifact {StorageKey} not found for project {ProjectId} ({ErrorCode})",
                storageKey, projectId, ex.ErrorCode);

            return false;
        }
    }

    public async Task<string?> GetObjectVersionAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        ValidateProjectScope(projectId, storageKey);

        try
        {
            GetObjectMetadataResponse head = await _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = storageKey
            }, ct);

            string? version = head.ETag?.Trim().Trim('"');
            return string.IsNullOrEmpty(version) ? null : version;
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.NotFound ||
            string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>
    /// Decides, for one project, whether a platform-made file (an output or a picture export) is left out, by the
    /// project's output marker (<see cref="ProjectOutputHold"/>) with the clock margin. An unreadable marker leaves
    /// every such file out. Uploaded files are never judged here.
    /// </summary>
    internal static async Task<Func<string, DateTime, DateTime?, bool>> BuildWithheldAsync(
        WorkflowEngineDbContext db, Guid projectId, CancellationToken ct)
    {
        ProjectOutputHoldState hold = await ProjectOutputHold.ReadAsync(db, projectId, ct);
        if (!hold.IsActive)
            return (_, _, _) => false;

        return (key, uploadedAt, producedAt) => ProjectOutputHold.IsPlatformMade(projectId, key, producedAt)
            && hold.HoldsFile(ProjectOutputHold.FileStamp(producedAt, uploadedAt));
    }

    private async Task<ProjectFile> ResolveFileAsync(Guid projectId, string fileReference, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        IQueryable<ProjectFile> query = db.ProjectFiles
            .Where(f => f.ProjectId == projectId && f.CsamStatus != CsamScanStatus.Quarantined && f.ModerationStatus != FileModerationStatus.Quarantined);

        Func<string, DateTime, DateTime?, bool> withheld = await BuildWithheldAsync(db, projectId, ct);
        ProjectFile? Allowed(ProjectFile? f) => f is not null && withheld(f.StorageKey, f.UploadedAt, f.OutputProducedAt) ? null : f;

        if (Guid.TryParse(fileReference, out Guid fileId))
        {
            ProjectFile? byId = Allowed(await query.FirstOrDefaultAsync(f => f.Id == fileId, ct));
            if (byId != null) return byId;
        }

        ProjectFile? byKey = Allowed(await query.FirstOrDefaultAsync(f => f.StorageKey == fileReference, ct));
        if (byKey != null) return byKey;

        ProjectFile? byName = Allowed(await query
            .Where(f => f.OriginalFileName == fileReference || f.OriginalPath == fileReference)
            .OrderByDescending(f => f.UploadedAt)
            .FirstOrDefaultAsync(ct));

        return byName ?? throw new KeyNotFoundException($"Project file '{fileReference}' not found in project scope.");
    }

    /// <summary>
    /// Deletion is narrower than reading: only <c>projects/{projectId}/agentFiles/voiceover/</c> (the
    /// voiceover line cache), a non-empty name below that prefix, and nothing that could escape it: no
    /// <c>..</c> segment, backslash, percent-encoding, control character or empty segment.
    /// </summary>
    internal static void ValidateDeleteScope(Guid projectId, string? storageKey)
    {
        string prefix = $"projects/{projectId}/agentFiles/voiceover/";
        // The two other scopes: the temporary source upload of a hosted AudioCleanup step and that of a hosted
        // VideoEnhance step (each deleted in its finally).
        string cleanupPrefix = $"projects/{projectId}/agentFiles/audio-cleanup/";
        string enhancePrefix = $"projects/{projectId}/agentFiles/video-enhance/";
        if (storageKey is not null && storageKey.StartsWith(cleanupPrefix, StringComparison.Ordinal))
        {
            prefix = cleanupPrefix;
        }
        else if (storageKey is not null && storageKey.StartsWith(enhancePrefix, StringComparison.Ordinal))
        {
            prefix = enhancePrefix;
        }

        // Avatars phase 3: a still uploaded while its avatar was being deleted or changed is removed by the step that
        // made it (the storage sweep's orphan pass is the second line). Only a still key of THIS project qualifies.
        if (storageKey is not null && AvatarStillStorage.AvatarOf(projectId, storageKey) is { } stillAvatar)
        {
            prefix = AvatarStillStorage.ObjectPrefix(projectId, stillAvatar);
        }

        if (string.IsNullOrWhiteSpace(storageKey)
            || !storageKey.StartsWith(prefix, StringComparison.Ordinal)
            || storageKey.Length == prefix.Length
            || storageKey.Contains('\\') || storageKey.Contains('%') || storageKey.Any(char.IsControl)
            || storageKey.Split('/').Any(seg => seg.Length == 0 || seg == ".." || seg == "."))
        {
            throw new UnauthorizedAccessException("Requested file is not in the project's deletable storage scope.");
        }
    }

    private static void ValidateProjectScope(Guid projectId, string storageKey)
    {
        if (!storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Requested file is not in the project's storage scope.");
    }

    // There is deliberately no extension/MIME allowlist here. ReadFileAsync decides from the bytes
    // (ProjectFileTextPolicy.LooksLikeText), which is strictly stronger: it admits a text file whose
    // extension nobody listed AND refuses a binary wearing a listed one. The shared allowlist still
    // governs the surfaces that must decide before any object is fetched — the dashboard controller,
    // the assistant file tools and the brief resolver — and the upload MIME map.
}
