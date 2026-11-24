using System.Text;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

/// <summary>A file a build job saved (or found again): the row, or the quota refusal that stopped it.</summary>
public sealed record AvatarBuildFileOutcome(ProjectFile? File, PlanGateViolation? OverQuota, bool Reused = false);

/// <summary>
/// Saves what a build job makes as ORDINARY project files (docs/design/avatar-objects.md "Avatar variants", stage 3):
/// under <c>userFiles/avatars/{original}/{label}/</c>, marked <c>source_kind = avatar_variant</c>, counted against the
/// workspace's storage like any upload, and handed to the same follow-up every new file gets (the CSAM scan, ingest
/// moderation, indexing), so the attach checks of the avatar create path can pass on them.
/// <para>
/// <b>A file's id is given by the job</b> (<see cref="AvatarBuildParams.FileIdFor"/>), so its storage key is too: a job
/// that runs again finds the row it already wrote (<see cref="FindAsync"/>) or the object it already stored without a
/// row (<see cref="RecoverAsync"/>) and buys nothing a second time.
/// </para>
/// </summary>
public sealed class AvatarBuildFiles
{
    /// <summary>The <c>project_files.source_kind</c> of a picture or video a variant build made. Accepted as an object reference.</summary>
    public const string VariantSourceKind = "avatar_variant";

    /// <summary>
    /// The <c>project_files.source_kind</c> of a frame an extraction cut out of a video (docs/design/avatar-objects.md
    /// "Pulling an object out of a video"). Accepted as an object reference.
    /// </summary>
    public const string ExtractionSourceKind = "avatar_extraction";

    /// <summary>The storage category of every file a build saves.</summary>
    public const string Category = "userFiles";

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly OrganizationStorageLock _storageLock;
    private readonly IProjectFileAddedEffects? _effects;
    private readonly IPlanGateService? _planGate;
    private readonly ILogger<AvatarBuildFiles> _logger;

    public AvatarBuildFiles(
        InferenceApiDbContext db,
        IFileStorageService storage,
        OrganizationStorageLock storageLock,
        ILogger<AvatarBuildFiles> logger,
        IProjectFileAddedEffects? effects = null,
        IPlanGateService? planGate = null)
    {
        _db = db;
        _storage = storage;
        _storageLock = storageLock;
        _logger = logger;
        _effects = effects;
        _planGate = planGate;
    }

    /// <summary><c>avatars/{original}/{label}</c>: where a variant's files go below <c>userFiles/</c>.</summary>
    public static string VariantDirectory(string originalName, string variantLabel) =>
        "avatars/" + Segment(originalName, "avatar") + "/" + Segment(variantLabel, "variant");

    /// <summary><c>objects/{name}</c>: where the frames of an object pulled out of a video go below <c>userFiles/</c>.</summary>
    public static string ObjectDirectory(string name) => "objects/" + Segment(name, "object");

    /// <summary>
    /// A name made safe as one folder or file-name part: letters, digits, spaces, dashes and underscores are kept, every
    /// other character becomes a dash, and it is cut to 60 characters. Never empty, never a dot name.
    /// </summary>
    public static string Segment(string? name, string fallback)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? string.Empty).Trim())
        {
            sb.Append(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '-');
            if (sb.Length >= 60) break;
        }

        string cleaned = sb.ToString().Trim(' ', '-');
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    /// <summary>The file extension of a picture's MIME type (<c>jpg</c> for anything unknown).</summary>
    public static string ExtensionOf(string? mimeType) => (mimeType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/webp" => "webp",
        "video/mp4" => "mp4",
        "video/quicktime" => "mov",
        _ => "jpg"
    };

    /// <summary>The MIME type of picture bytes by their first bytes (PNG, WebP, else JPEG).</summary>
    public static string SniffImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        return "image/jpeg";
    }

    /// <summary>The row a job already wrote under <paramref name="fileId"/>, when it did.</summary>
    public Task<ProjectFile?> FindAsync(Guid projectId, Guid fileId, CancellationToken ct) =>
        _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);

    /// <summary>
    /// An object the job stored under <paramref name="fileId"/> whose row was never written (the process stopped in
    /// between): the row is written now, and the file counts as made. Null file and null refusal = there is no such
    /// object, so the picture has to be made.
    /// </summary>
    public async Task<AvatarBuildFileOutcome> RecoverAsync(
        Project project, Guid userId, Guid fileId, string directory, string fileName, string mimeType, CancellationToken ct)
    {
        string? key = DirectUploadService.DeriveKey(project.Id, fileId, fileName, directory + "/" + fileName);
        if (key is null) return new AvatarBuildFileOutcome(null, null);

        StoredObjectMetadata? stored;
        try
        {
            stored = await _storage.GetMetadataAsync(project.Id, key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Could not look for an earlier object of a build file in project {ProjectId}", project.Id);
            return new AvatarBuildFileOutcome(null, null);
        }

        if (stored is null || stored.SizeBytes < 1) return new AvatarBuildFileOutcome(null, null);

        string originalPath = directory + "/" + fileName;
        var row = new ProjectFile
        {
            Id = fileId,
            ProjectId = project.Id,
            OriginalFileName = fileName,
            OriginalPath = originalPath,
            DirectoryPath = ProjectFilePath.GetDirectoryPath(originalPath),
            Category = Category,
            StorageKey = key,
            StorageFileName = ProjectFilePath.BuildStorageFileName(fileId, fileName),
            StorageBucket = _storage.BucketName,
            StoragePrefix = ProjectFilePath.BuildStoragePrefix(project.Id, Category),
            MimeType = mimeType,
            SizeBytes = stored.SizeBytes,
            SourceKind = VariantSourceKind,
            UploadedAt = DateTime.UtcNow
        };
        ApplyStatuses(row);
        return await RecordAsync(project, userId, row, reused: true, ct);
    }

    /// <summary>
    /// Copies an object the job's hidden run made (<paramref name="sourceKey"/>, inside the project) to a new project
    /// file <paramref name="fileId"/> at <c>userFiles/{directory}/{fileName}</c>, inside the store (the bytes never pass
    /// through this process). The row the job already wrote is the answer when there is one; the quota is asked before
    /// the copy and again with the row write. Null file and null refusal = the source object is gone.
    /// </summary>
    public async Task<AvatarBuildFileOutcome> CopyAsync(
        Project project, Guid userId, Guid fileId, string directory, string fileName, string mimeType, string sourceKey, string sourceKind,
        CancellationToken ct)
    {
        if (await FindAsync(project.Id, fileId, ct) is { } existing) return new AvatarBuildFileOutcome(existing, null, Reused: true);
        if (!sourceKey.StartsWith($"projects/{project.Id}/", StringComparison.Ordinal)) return new AvatarBuildFileOutcome(null, null);

        StoredObjectMetadata? source = await _storage.GetMetadataAsync(project.Id, sourceKey, ct);
        if (source is null || source.SizeBytes < 1) return new AvatarBuildFileOutcome(null, null);
        if (await QuotaAsync(project, source.SizeBytes, ct) is { } early) return new AvatarBuildFileOutcome(null, early);

        string originalPath = directory + "/" + fileName;
        string? key = DirectUploadService.DeriveKey(project.Id, fileId, fileName, originalPath);
        if (key is null) return new AvatarBuildFileOutcome(null, null);
        await _storage.CopyAsync(project.Id, sourceKey, key, ct);

        var row = new ProjectFile
        {
            Id = fileId,
            ProjectId = project.Id,
            OriginalFileName = fileName,
            OriginalPath = originalPath,
            DirectoryPath = ProjectFilePath.GetDirectoryPath(originalPath),
            Category = Category,
            StorageKey = key,
            StorageFileName = ProjectFilePath.BuildStorageFileName(fileId, fileName),
            StorageBucket = _storage.BucketName,
            StoragePrefix = ProjectFilePath.BuildStoragePrefix(project.Id, Category),
            MimeType = mimeType,
            SizeBytes = source.SizeBytes,
            SourceKind = sourceKind,
            UploadedAt = DateTime.UtcNow
        };
        ApplyStatuses(row);
        AvatarBuildFileOutcome outcome = await RecordAsync(project, userId, row, reused: false, ct);
        if (outcome.File is null) await DeleteQuietlyAsync(project.Id, key);
        return outcome;
    }

    /// <summary>
    /// Stores <paramref name="bytes"/> as a new project file <paramref name="fileId"/> at
    /// <c>userFiles/{directory}/{fileName}</c>. The quota is asked before the upload and again, with the row write,
    /// under the organization's storage guard; a refusal leaves nothing behind.
    /// </summary>
    public async Task<AvatarBuildFileOutcome> SaveAsync(
        Project project, Guid userId, Guid fileId, string directory, string fileName, string mimeType, byte[] bytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (await QuotaAsync(project, bytes.LongLength, ct) is { } early) return new AvatarBuildFileOutcome(null, early);

        Dictionary<string, string> metadata = new()
        {
            ["project-id"] = project.Id.ToString(),
            ["org-id"] = project.OrganizationId.ToString(),
            ["uploaded-by"] = userId.ToString(),
            ["project-file-id"] = fileId.ToString(),
            ["reelbolt-kind"] = VariantSourceKind
        };

        StoredFileObject stored;
        using (MemoryStream stream = new(bytes, writable: false))
        {
            // No storage lock is held while the bytes stream.
            stored = await _storage.UploadAsync(
                project.Id, fileId, stream, fileName, mimeType, metadata, ct, Category, directory + "/" + fileName);
        }

        var row = new ProjectFile
        {
            Id = fileId,
            ProjectId = project.Id,
            OriginalFileName = stored.OriginalFileName,
            OriginalPath = stored.OriginalPath,
            DirectoryPath = stored.DirectoryPath,
            Category = Category,
            StorageKey = stored.StorageKey,
            StorageFileName = stored.StorageFileName,
            StorageBucket = stored.BucketName,
            StoragePrefix = stored.StoragePrefix,
            StorageMetadataJson = stored.StorageMetadataJson,
            MimeType = mimeType,
            SizeBytes = bytes.LongLength,
            SourceKind = VariantSourceKind,
            UploadedAt = DateTime.UtcNow
        };
        ApplyStatuses(row);
        AvatarBuildFileOutcome outcome = await RecordAsync(project, userId, row, reused: false, ct);
        if (outcome.File is null) await DeleteQuietlyAsync(project.Id, stored.StorageKey);
        return outcome;
    }

    /// <summary>Whether <paramref name="bytes"/> more would pass the plan's storage quota; null = it fits (or no quota applies).</summary>
    public async Task<PlanGateViolation?> QuotaAsync(Project project, long bytes, CancellationToken ct) =>
        _planGate is not null && bytes > 0 ? await _planGate.CheckUploadAsync(project.OrganizationId, bytes, ct) : null;

    /// <summary>A picture is described and indexed like an upload; a video gets its derivative set instead.</summary>
    private static void ApplyStatuses(ProjectFile row)
    {
        bool media = DirectUploadService.IsVideoOrAudio(row.MimeType);
        row.SummaryStatus = media ? SummaryStatus.Done : SummaryStatus.Pending;
        row.IndexingStatus = media ? FileIndexingStatus.NotIndexed : FileIndexingStatus.Pending;
    }

    /// <summary>
    /// The authoritative quota check and the row write as one critical section under the organization's storage guard,
    /// then the follow-up every new file gets. A row another worker wrote first under the same id is the answer.
    /// </summary>
    private async Task<AvatarBuildFileOutcome> RecordAsync(Project project, Guid userId, ProjectFile row, bool reused, CancellationToken ct)
    {
        PlanGateViolation? refused;
        await using (OrganizationStorageGuard guard = await _storageLock.AcquireAsync(_db, project.OrganizationId, ct))
        {
            refused = await QuotaAsync(project, row.SizeBytes, ct);
            if (refused is null)
            {
                try
                {
                    _db.ProjectFiles.Add(row);
                    await _db.SaveChangesAsync(ct);
                    await guard.CommitAsync(ct);
                }
                catch (DbUpdateException)
                {
                    _db.Entry(row).State = EntityState.Detached;
                    // The same job, run twice at once: the other run's row is the file.
                    ProjectFile? existing = await _db.ProjectFiles.AsNoTracking()
                        .FirstOrDefaultAsync(f => f.Id == row.Id && f.ProjectId == project.Id, CancellationToken.None);
                    if (existing is not null) return new AvatarBuildFileOutcome(existing, null, Reused: true);
                    throw;
                }
            }
        }

        if (refused is not null) return new AvatarBuildFileOutcome(null, refused);

        if (_effects is not null)
        {
            try
            {
                await _effects.RunAsync(row, userId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The row is durable; the sweeps of the scan, the moderation and the indexer pick it up.
                _logger.LogWarning(ex, "The follow-up of build file {FileId} in project {ProjectId} failed; the sweeps will redo it", row.Id, project.Id);
            }
        }

        return new AvatarBuildFileOutcome(row, null, reused);
    }

    private async Task DeleteQuietlyAsync(Guid projectId, string key)
    {
        try
        {
            await _storage.DeleteAsync(projectId, key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // An object no row names is removed by the storage sweep's orphan pass.
            _logger.LogWarning("Could not delete an unrecorded build object in project {ProjectId} ({Error})", projectId, ex.GetType().Name);
        }
    }
}
