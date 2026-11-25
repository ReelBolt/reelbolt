using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.StaticFiles;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Observability;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// What one pull did. <see cref="Status"/> is the terminal outcome recorded on the delivery row:
/// <c>pulled</c>, <c>unchanged</c> (the project is already at that commit), <c>stale</c> (an older
/// push arrived after a newer one and was dropped) or <c>failed</c> (the caller records the error).
/// </summary>
public sealed record GitHubPullOutcome(
    string Status,
    string? Commit,
    int Written,
    int Deleted,
    int Unchanged,
    int Skipped);

/// <summary>
/// A pull refused before it changed anything, for a reason the project's owner can act on (too many
/// files chosen, too much data, a repository too large to list). Not a GitHub failure — GitHub answered
/// fine — which is why it is its own type and the API answers it <c>422</c> with <see cref="Code"/>
/// rather than <c>502 github_error</c>. <see cref="Message"/> is plain language and is what the link's
/// <c>LastError</c> records. <see cref="Actual"/>/<see cref="Limit"/> are the two numbers (files or
/// bytes) when the refusal is a limit.
/// </summary>
public sealed class GitHubSyncRefusedException : Exception
{
    public GitHubSyncRefusedException(string code, string message, long? actual = null, long? limit = null)
        : base(message)
    {
        Code = code;
        Actual = actual;
        Limit = limit;
    }

    public string Code { get; }
    public long? Actual { get; }
    public long? Limit { get; }
}

/// <summary>
/// Materializes a linked repository into a ReelBolt project. See <c>docs/github-app.md</c>
/// "What \"pull\" means" for the contract; the properties this implementation is built around are:
///
/// <list type="bullet">
/// <item><b>It reconciles, it does not append.</b> The project's GitHub-sourced files are made to
/// equal the repository tree at one commit: new paths are written, changed paths are rewritten,
/// vanished paths are removed. Running it twice at the same commit writes nothing the second time.</item>
/// <item><b>It touches only its own rows.</b> Every row it writes carries
/// <see cref="ProjectFile.SourceKind"/> = <c>github</c>, and it only ever reads, rewrites or deletes
/// rows with that marker. A user upload or an agent's file is invisible to a pull, whatever it is
/// called.</item>
/// <item><b>It is keyed on content, not on the delivery.</b> Each row remembers the git blob SHA its
/// bytes came from (<see cref="ProjectFile.SourceRevision"/>), so an unchanged file costs no download
/// and no upload. That is what makes a redelivered webhook — or the same commit pushed twice — a
/// no-op rather than duplicated work.</item>
/// <item><b>It fails closed on anything it cannot see in full.</b> A truncated tree, too many files or
/// too much data refuses the pull instead of applying a partial one, because a partial tree looks
/// exactly like "everything else was deleted".</item>
/// <item><b>It mirrors the selection, not the whole repository.</b> Only the files and folders the
/// link's <see cref="ProjectGitHubLink.IncludePathsJson"/> names (all of them when it is null) are
/// pulled, and a pulled file that falls out of the selection is removed like a file deleted in the
/// repository — so narrowing the selection and re-pulling leaves exactly the selection.</item>
/// <item><b>It keeps its progress.</b> Written rows are saved every <see cref="SaveEvery"/> files, so
/// an interrupted pull keeps what it wrote and the next one resumes by blob SHA. Removing vanished files
/// and stamping the commit happen only after a complete pass.</item>
/// <item><b>One unreadable file is skipped, not fatal.</b> A blob GitHub no longer has, or sends in an
/// encoding other than base64, is counted in <c>skipped</c> and logged; a zero-byte file is an empty
/// file. Only GitHub refusing or failing (auth, 5xx, 429, the tree) fails the pull.</item>
/// <item><b>A failure is written on the link.</b> <see cref="ProjectGitHubLink.LastError"/> carries a
/// plain-language reason the dashboard shows as-is; the caller saves it (<see cref="GitHubSyncWorker"/>
/// for "Sync now", a new link and a changed selection; the delivery worker for a push).</item>
/// </list>
/// </summary>
public interface IGitHubRepositoryPullService
{
    /// <summary>Pulls <paramref name="commitSha"/> into the linked project.</summary>
    Task<GitHubPullOutcome> PullAsync(ProjectGitHubLink link, string commitSha, DateTime? pushedAt, string? deliveryId, CancellationToken ct);

    /// <summary>
    /// Pulls whatever the linked branch points at right now. This is the manual "Sync now" path, and
    /// the one that runs right after a link is created or its selection is changed; the webhook path
    /// already knows the commit it was told about. <paramref name="force"/> reconciles even when the
    /// project already mirrors that commit — what a changed selection needs, since the commit is the
    /// same but the set of files is not.
    /// </summary>
    Task<GitHubPullOutcome> PullBranchAsync(ProjectGitHubLink link, bool force, CancellationToken ct);
}

/// <inheritdoc />
public sealed class GitHubRepositoryPullService : IGitHubRepositoryPullService
{
    /// <summary>The marker every pulled row carries. A pull never reads or writes any other row.</summary>
    public const string SourceKind = "github";

    /// <summary>Written files between two saves of a pull's progress.</summary>
    public const int SaveEvery = 25;

    /// <summary>Directories a pull never mirrors: version-control internals and installed dependencies.</summary>
    private static readonly string[] IgnoredDirectories = [".git", "node_modules"];

    /// <summary>True for a path a pull never mirrors (the ignored directories, and anything inside them).</summary>
    internal static bool IsIgnored(string relativePath) =>
        IgnoredDirectories.Any(ignored => GitHubIncludePaths.IsWithin(relativePath, ignored));

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly InferenceApiDbContext _db;
    private readonly IGitHubAppClient _gitHub;
    private readonly IFileStorageService _storage;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubRepositoryPullService> _logger;
    private readonly IPlanGateService? _planGate;
    private readonly DesignDriftQueue? _driftQueue;

    public GitHubRepositoryPullService(
        InferenceApiDbContext db,
        IGitHubAppClient gitHub,
        IFileStorageService storage,
        IOptions<GitHubOptions> options,
        ILogger<GitHubRepositoryPullService> logger,
        IPlanGateService? planGate = null,
        DesignDriftQueue? driftQueue = null)
    {
        _planGate = planGate;
        _db = db;
        _gitHub = gitHub;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
        _driftQueue = driftQueue;
    }

    public async Task<GitHubPullOutcome> PullBranchAsync(ProjectGitHubLink link, bool force, CancellationToken ct)
    {
        (string owner, string name) = SplitFullName(link.RepositoryFullName);
        string branch = link.Branch ?? link.RepositoryDefaultBranch ?? "main";

        string head;
        try
        {
            head = await _gitHub.GetBranchHeadAsync(link.InstallationId, owner, name, branch, ct);
        }
        catch (GitHubApiException ex)
        {
            _logger.LogWarning("Resolving {Repository}@{Branch} failed: {Message}", link.RepositoryFullName.ForLog(), branch.ForLog(), ex.Message.ForLog());
            throw Record(link, ex.StatusCode == System.Net.HttpStatusCode.NotFound
                ? new GitHubApiException(
                    $"The branch \"{branch}\" could not be found in {link.RepositoryFullName}. It may have been "
                    + "renamed or deleted, or the repository may still be empty. Choose another branch.",
                    ex.StatusCode)
                : InPlainWords(link, ex));
        }

        return await PullCoreAsync(link, head, pushedAt: null, deliveryId: null, force, ct);
    }

    public Task<GitHubPullOutcome> PullAsync(
        ProjectGitHubLink link,
        string commitSha,
        DateTime? pushedAt,
        string? deliveryId,
        CancellationToken ct) =>
        PullCoreAsync(link, commitSha, pushedAt, deliveryId, force: false, ct);

    private async Task<GitHubPullOutcome> PullCoreAsync(
        ProjectGitHubLink link,
        string commitSha,
        DateTime? pushedAt,
        string? deliveryId,
        bool force,
        CancellationToken ct)
    {
        try
        {
            return await ReconcileAsync(link, commitSha, pushedAt, deliveryId, force, ct);
        }
        catch (GitHubApiException ex)
        {
            _logger.LogWarning("Pulling {Repository}@{Commit} failed: {Message}", link.RepositoryFullName.ForLog(), commitSha.ForLog(), ex.Message.ForLog());
            throw Record(link, InPlainWords(link, ex));
        }
        catch (GitHubSyncRefusedException ex)
        {
            Record(link, ex);
            throw;
        }
    }

    /// <summary>
    /// A GitHub failure, re-worded for the project's owner. The original message names REST paths and
    /// status codes; it goes to the log (by the caller), and the link gets what the person can do about
    /// it. The status code is kept, so the API's mapping of it is unchanged.
    /// </summary>
    private static GitHubApiException InPlainWords(ProjectGitHubLink link, GitHubApiException ex)
    {
        string repository = link.RepositoryFullName;
        string message = (int)ex.StatusCode switch
        {
            401 or 403 =>
                $"GitHub refused ReelBolt access to {repository}. ReelBolt's access to the repository may have been "
                + "removed on GitHub; give it access again, then sync.",
            404 =>
                $"GitHub could not find {repository}, or a file in it. The repository may have been deleted or renamed, "
                + "or ReelBolt's access to it may have been removed on GitHub.",
            429 =>
                "GitHub is limiting how many requests ReelBolt may make right now. Wait a few minutes, then sync again.",
            503 when ex.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase) =>
                "GitHub syncing is not set up on this server. Ask your administrator to finish the GitHub setup.",
            >= 500 =>
                $"GitHub had a problem answering (error {(int)ex.StatusCode}). This is usually temporary: sync again in a few minutes.",
            _ =>
                $"GitHub did not accept the request (error {(int)ex.StatusCode}). Sync again in a few minutes; if it keeps "
                + "happening, unlink the repository and link it again."
        };

        return new GitHubApiException(message, ex.StatusCode);
    }

    /// <summary>
    /// Writes the failure on the link (the caller saves it) and hands the exception back to be thrown.
    /// The link's <c>LastError</c> is what the dashboard shows, so the exceptions this class raises
    /// itself are written for the project's owner rather than for an operator.
    /// </summary>
    private static TException Record<TException>(ProjectGitHubLink link, TException ex)
        where TException : Exception
    {
        string message = ex.Message;
        link.LastError = message.Length > 2000 ? message[..2000] : message;
        link.UpdatedAt = DateTime.UtcNow;
        return ex;
    }

    private async Task<GitHubPullOutcome> ReconcileAsync(
        ProjectGitHubLink link,
        string commitSha,
        DateTime? pushedAt,
        string? deliveryId,
        bool force,
        CancellationToken ct)
    {
        if (!force && string.Equals(link.LastPulledCommit, commitSha, StringComparison.OrdinalIgnoreCase))
            return new GitHubPullOutcome("unchanged", commitSha, 0, 0, 0, 0);

        // Out-of-order deliveries: GitHub retries, and a retry of an older push can arrive after a
        // newer one. The project mirrors one commit, so applying the older one would move it backwards.
        if (pushedAt is { } when && link.LastPulledAt is { } lastPulled && when < lastPulled)
            return new GitHubPullOutcome("stale", commitSha, 0, 0, 0, 0);

        (string owner, string repo) = SplitFullName(link.RepositoryFullName);
        GitHubRepoTree tree = await _gitHub.GetTreeAsync(link.InstallationId, owner, repo, commitSha, ct);

        if (tree.Truncated)
        {
            // Operator detail goes to the log; the link gets the sentence its owner can act on.
            _logger.LogWarning(
                "GitHub truncated the tree of {Repository}@{Commit}; the pull was refused rather than half-applied",
                link.RepositoryFullName.ForLog(), commitSha.ForLog());
            throw new GitHubSyncRefusedException(
                "repository_too_large",
                $"{link.RepositoryFullName} is too large for GitHub to list in one go, so ReelBolt cannot tell which files "
                + "it holds. Nothing was changed and nothing was deleted.");
        }

        IReadOnlyList<string> includePaths;
        try
        {
            includePaths = GitHubIncludePaths.Parse(link.IncludePathsJson);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "The stored file selection of the link of project {ProjectId} is unreadable", link.ProjectId);
            throw new GitHubSyncRefusedException(
                "selection_unreadable",
                "The saved choice of files to sync could not be read, so nothing was changed. Choose the files again.");
        }

        List<(string Path, GitHubTreeEntry Entry)> selected = Select(tree, link.PathPrefix, includePaths);
        int skipped = 0;

        if (selected.Count > _options.MaxFilesPerPull)
        {
            _logger.LogWarning(
                "{Repository}@{Commit}: {Count} selected files is over GitHub:MaxFilesPerPull ({Limit})",
                link.RepositoryFullName.ForLog(), commitSha.ForLog(), selected.Count, _options.MaxFilesPerPull);
            throw new GitHubSyncRefusedException(
                "too_many_files",
                $"Too many files are chosen to sync: {selected.Count}, and the limit is {_options.MaxFilesPerPull}. "
                + "Choose fewer files or folders. Nothing was changed.",
                selected.Count,
                _options.MaxFilesPerPull);
        }

        long declaredBytes = selected.Sum(s => s.Entry.Size);
        if (declaredBytes > _options.MaxTotalBytes)
        {
            _logger.LogWarning(
                "{Repository}@{Commit}: {Bytes} selected bytes is over GitHub:MaxTotalBytes ({Limit})",
                link.RepositoryFullName.ForLog(), commitSha.ForLog(), declaredBytes, _options.MaxTotalBytes);
            throw new GitHubSyncRefusedException(
                "selection_too_large",
                $"The chosen files are too large to sync together: {FormatBytes(declaredBytes)}, and the limit is "
                + $"{FormatBytes(_options.MaxTotalBytes)}. Choose fewer files or folders. Nothing was changed.",
                declaredBytes,
                _options.MaxTotalBytes);
        }

        // EVERY file of the project, not only the pulled ones: a path a person uploaded or an agent
        // wrote is occupied, and the pull must skip it rather than add a second file at the same path
        // or replace what is there.
        List<ProjectFile> existing = await _db.ProjectFiles
            .Where(f => f.ProjectId == link.ProjectId)
            .ToListAsync(ct);

        Dictionary<string, ProjectFile> byPath = existing
            .GroupBy(f => f.OriginalPath ?? f.OriginalFileName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        int written = 0;
        int unchanged = 0;
        HashSet<string> changedPaths = new(StringComparer.Ordinal);

        // The plan's storage quota, file by file on the NET growth (docs/design/local-folder-sync-and-storage.md
        // Part 2): a file that does not fit is skipped and counted, and the pull carries on — deletions
        // and unchanged files still apply — and ends with the plain sentence on the link.
        SyncStorageQuota quota = new(_planGate, link.OrganizationId);

        foreach ((string path, GitHubTreeEntry entry) in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Size > _options.MaxFileBytes)
            {
                skipped++;
                continue;
            }

            byPath.TryGetValue(path, out ProjectFile? current);

            // A quarantined row is evidence: never overwrite it, never reuse it for a write, never
            // delete its object. Report the path as skipped and carry on.
            if (current?.CsamStatus == CsamScanStatus.Quarantined)
            {
                skipped++;
                continue;
            }

            if (current is not null && !string.Equals(current.SourceKind, SourceKind, StringComparison.Ordinal))
            {
                // Occupied by a user upload or an agent write. Never overwritten, never duplicated.
                skipped++;
                continue;
            }

            if (current is not null && string.Equals(current.SourceRevision, entry.Sha, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            // Checked on the size the tree declares, before the blob is fetched: a full workspace does
            // not download what it cannot keep.
            long oldSize = ReelBolt.Shared.Billing.ProjectFileQuota.CountedSize(current);
            if (!await quota.FitsAsync(entry.Size - oldSize, ct))
            {
                skipped++;
                continue;
            }

            byte[] content;
            try
            {
                content = await _gitHub.GetBlobAsync(link.InstallationId, owner, repo, entry.Sha, ct);
            }
            catch (GitHubBlobUnreadableException ex)
            {
                // One file GitHub would not hand over (gone since the tree was read, or not base64) is
                // skipped, not fatal: failing here made one odd file block every other file forever.
                // The row (if any) keeps its old revision, so the next pull asks for it again.
                _logger.LogWarning(
                    "{Repository}@{Commit}: skipped {Path}, GitHub returned no readable content for blob {Blob} ({Reason})",
                    link.RepositoryFullName.ForLog(), commitSha.ForLog(), path.ForLog(), ex.BlobSha.ForLog(), ex.Reason.ForLog());
                skipped++;
                continue;
            }

            if (content.LongLength > _options.MaxFileBytes)
            {
                skipped++;
                continue;
            }

            // An overwrite of a file whose bytes are kept is skipped.
            if (current is not null && await Outputs.OutputGate.IsFileHeldAsync(_db, current, ct))
            {
                skipped++;
                continue;
            }

            ProjectFile row = current ?? NewRow(link.ProjectId, path);
            await WriteAsync(link, row, path, content, entry.Sha, commitSha, ct);
            quota.Wrote(content.LongLength - oldSize);

            if (current is null)
            {
                _db.ProjectFiles.Add(row);
                byPath[path] = row;
            }

            written++;
            changedPaths.Add(path);

            // Progress is kept as it goes: an interrupted pull (a restart, a timeout, GitHub failing
            // half-way) leaves these rows saved, each with the blob SHA it came from, so the next pull
            // skips them as unchanged and resumes where this one stopped — and the stored objects they
            // point at are never orphaned. Deleting vanished files and stamping the commit wait for a
            // complete pass (below), because only a complete pass knows what vanished.
            if (written % SaveEvery == 0)
            {
                await _db.SaveChangesAsync(ct);
                quota.Saved();
            }
        }

        HashSet<string> present = selected.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        List<ProjectFile> vanished = byPath.Values
            .Where(f => string.Equals(f.SourceKind, SourceKind, StringComparison.Ordinal))
            .Where(f => !present.Contains(f.OriginalPath ?? f.OriginalFileName))
            .ToList();

        // A quarantined row that vanished upstream is still evidence: keep it, report it skipped.
        int quarantinedKept = vanished.Count(f => f.CsamStatus == CsamScanStatus.Quarantined);
        skipped += quarantinedKept;
        vanished = vanished.Where(f => f.CsamStatus != CsamScanStatus.Quarantined).ToList();
        // A file whose bytes are kept is not removed by a sync.
        HashSet<Guid> keptIds = await Outputs.OutputGate.HeldFileIdsAsync(_db, link.ProjectId, vanished, ct);
        if (keptIds.Count > 0)
        {
            skipped += keptIds.Count;
            vanished = vanished.Where(f => !keptIds.Contains(f.Id)).ToList();
        }
        // Belt and braces: an avatar never takes a synced file (the attach refuses any owned source_kind), but if a
        // row an avatar lists ever reached here it is kept, never deleted from under the avatar (NO ACTION link).
        HashSet<Guid> avatarIds = await ReelBolt.Shared.Avatars.AvatarFileUse.ReferencedFileIdsAsync(_db, [.. vanished.Select(f => f.Id)], ct);
        if (avatarIds.Count > 0)
        {
            _logger.LogWarning("Repository pull of link {LinkId} kept {Count} file(s) an avatar lists as a reference", link.Id, avatarIds.Count);
            skipped += avatarIds.Count;
            vanished = vanished.Where(f => !avatarIds.Contains(f.Id)).ToList();
        }

        if (vanished.Count > 0)
        {
            List<string> keys = vanished.Select(v => v.StorageKey).Where(k => !string.IsNullOrEmpty(k)).ToList();
            // D10: pictures using a vanished photo keep their own copy before the objects go.
            await PictureSourceCopies.CopyBeforeDeleteAsync(_db, _storage, link.ProjectId, vanished, _logger, ct);
            if (keys.Count > 0) await _storage.DeleteManyAsync(link.ProjectId, keys, ct);
            _db.ProjectFiles.RemoveRange(vanished);
            foreach (ProjectFile removed in vanished)
                changedPaths.Add(removed.OriginalPath ?? removed.OriginalFileName);
        }

        DateTime now = DateTime.UtcNow;
        link.LastPulledCommit = commitSha;
        link.LastPulledAt = now;
        link.LastPulledDeliveryId = deliveryId;
        link.LastPulledFileCount = selected.Count;
        // Null after a complete pass, unless files were left out because storage is full.
        link.LastError = quota.Message;
        link.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        if (written > 0 || vanished.Count > 0)
        {
            // The upsert is committed; ask for a drift check off this request. Never throws into the pull.
            DriftHooks.Notify(_driftQueue, link.ProjectId, DesignDriftTrigger.GitHubPush, changedPaths, _logger, link.OrganizationId);
        }

        _logger.LogInformation(
            "GitHub pull of {Repository}@{Commit} into project {ProjectId}: {Written} written, {Deleted} deleted, {Unchanged} unchanged, {Skipped} skipped",
            link.RepositoryFullName.ForLog(), commitSha.ForLog(), link.ProjectId, written, vanished.Count, unchanged, skipped);

        return new GitHubPullOutcome("pulled", commitSha, written, vanished.Count, unchanged, skipped);
    }

    /// <summary>
    /// The files of a pull: blobs only (a submodule is a commit, not content), inside the optional path
    /// prefix, inside the selection (matched on the full repository path), with the ignored directories
    /// removed. Paths are returned relative to the prefix, so a project linked to <c>video/</c> shows
    /// <c>scene.tsx</c>, not <c>video/scene.tsx</c>. A file left out here is not "skipped" — it was
    /// never asked for — so only a selected file that could not be written counts as skipped.
    /// </summary>
    internal static List<(string Path, GitHubTreeEntry Entry)> Select(
        GitHubRepoTree tree,
        string? pathPrefix,
        IReadOnlyList<string> includePaths)
    {
        string prefix = string.IsNullOrWhiteSpace(pathPrefix) ? string.Empty : pathPrefix.Trim('/') + "/";
        List<(string, GitHubTreeEntry)> selected = [];

        foreach (GitHubTreeEntry entry in tree.Entries)
        {
            if (!string.Equals(entry.Type, "blob", StringComparison.Ordinal)) continue;
            if (!GitHubIncludePaths.Matches(includePaths, entry.Path)) continue;

            string path = entry.Path;
            if (prefix.Length > 0)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                path = path[prefix.Length..];
            }

            if (path.Length == 0 || IsIgnored(path)) continue;

            selected.Add((path, entry));
        }

        return selected;
    }

    /// <summary>A size the way the dashboard writes one: binary units, at most one decimal.</summary>
    internal static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} bytes"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    private static ProjectFile NewRow(Guid projectId, string path) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        OriginalFileName = ProjectFilePath.GetFileName(path),
        OriginalPath = path,
        DirectoryPath = ProjectFilePath.GetDirectoryPath(path),
        Category = "userFiles",
        SourceKind = SourceKind,
        UploadedAt = DateTime.UtcNow
    };

    /// <summary>
    /// Stores one file and fills in the row. Deliberately does <b>not</b> queue the summarizer or the
    /// embedder: a push arrives with nobody watching, and an LLM bill the owner never asked for is not
    /// a side effect of installing an app. See docs/github-app.md "What is deliberately not done".
    /// </summary>
    private async Task WriteAsync(
        ProjectGitHubLink link,
        ProjectFile row,
        string path,
        byte[] content,
        string blobSha,
        string commitSha,
        CancellationToken ct)
    {
        string contentType = ContentTypes.TryGetContentType(path, out string? detected)
            ? detected
            : "application/octet-stream";
        // Issue 8: the extension map calls every .ts a transport stream; a TypeScript source is text.
        contentType = TransportStreamMimeSniffer.Resolve(
            contentType, path, content.AsSpan(0, Math.Min(content.Length, ProjectFileTextPolicy.SniffBytes))) ?? contentType;

        Dictionary<string, string> metadata = new()
        {
            ["project-id"] = link.ProjectId.ToString(),
            ["org-id"] = link.OrganizationId.ToString(),
            ["project-file-id"] = row.Id.ToString(),
            ["github-repository"] = link.RepositoryFullName,
            // The commit being pulled, not the link's previous one (it is stamped only at the end).
            ["github-commit"] = commitSha,
            ["github-blob"] = blobSha
        };

        using MemoryStream stream = new(content, writable: false);
        StoredFileObject stored = await _storage.UploadAsync(
            link.ProjectId,
            row.Id,
            stream,
            ProjectFilePath.GetFileName(path),
            contentType,
            metadata,
            ct,
            category: "userFiles",
            originalPath: path);

        if (!string.IsNullOrEmpty(row.StorageKey) &&
            !string.Equals(row.StorageKey, stored.StorageKey, StringComparison.Ordinal))
        {
            await _storage.DeleteAsync(link.ProjectId, row.StorageKey, ct);
        }

        row.OriginalFileName = stored.OriginalFileName;
        row.OriginalPath = stored.OriginalPath;
        row.DirectoryPath = stored.DirectoryPath;
        row.Category = "userFiles";
        row.StorageKey = stored.StorageKey;
        row.StorageFileName = stored.StorageFileName;
        row.StorageBucket = stored.BucketName;
        row.StoragePrefix = stored.StoragePrefix;
        row.StorageMetadataJson = stored.StorageMetadataJson;
        row.MimeType = contentType;
        row.SizeBytes = content.LongLength;
        row.SourceKind = SourceKind;
        row.SourceRevision = blobSha;
        // A rewritten file's old summary and vectors describe bytes that no longer exist.
        row.AgentSummary = null;
        row.SummaryStatus = SummaryStatus.Done;
        row.IndexingStatus = FileIndexingStatus.NotIndexed;
        row.IndexedAt = null;
        row.IndexingError = null;
        row.UploadedAt = DateTime.UtcNow;
    }

    internal static (string Owner, string Repo) SplitFullName(string fullName)
    {
        int slash = fullName.IndexOf('/');
        if (slash <= 0 || slash == fullName.Length - 1)
            throw new InvalidOperationException($"'{fullName}' is not owner/name.");

        return (fullName[..slash], fullName[(slash + 1)..]);
    }
}
