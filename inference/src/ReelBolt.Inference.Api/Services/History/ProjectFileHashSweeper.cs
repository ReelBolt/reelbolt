using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Fills <c>project_files.content_sha256</c> in the background (contract section 3), oldest first:
/// HEAD (etag A), stream and hash, HEAD (etag B); the hash is saved only when A equals B, so a file
/// overwritten mid-read is skipped rather than recorded under the wrong bytes. Never inside a request.
/// </summary>
public sealed class ProjectFileHashSweeper : BackgroundService
{
    private const int MaxRememberedFailures = 5_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<ProjectHistoryOptions> _options;
    private readonly ILogger<ProjectFileHashSweeper> _logger;

    // A row whose object is missing or unreadable would otherwise be the "oldest unhashed" row forever
    // and starve the rest; remember it for the life of the process.
    private readonly HashSet<Guid> _failed = [];

    public ProjectFileHashSweeper(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<ProjectHistoryOptions> options,
        ILogger<ProjectFileHashSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>One sweep, exposed for tests. Returns how many hashes were saved.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        ProjectHistoryOptions options = _options.CurrentValue;
        int batch = Math.Max(1, options.HashSweepBatchSize);

        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        IFileStorageService storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();

        Guid[] skip = _failed.ToArray();
        List<ProjectFile> rows = await db.ProjectFiles
            .Where(f => (f.Category == "userFiles" || f.Category == "agentFiles")
                        && f.ContentSha256 == null
                        && f.StorageKey != ""
                        && f.ModerationStatus != FileModerationStatus.Quarantined
                        && f.CsamStatus != CsamScanStatus.Quarantined
                        && !skip.Contains(f.Id))
            .OrderBy(f => f.UploadedAt)
            .ThenBy(f => f.Id)
            .Take(batch)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        int saved = 0;
        foreach (ProjectFile row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string? before = await storage.GetObjectETagAsync(row.ProjectId, row.StorageKey, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(before))
                {
                    Remember(row.Id);
                    continue;
                }

                string hash;
                await using (Stream content = await storage.DownloadAsync(row.ProjectId, row.StorageKey, ct).ConfigureAwait(false))
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct).ConfigureAwait(false));

                string? after = await storage.GetObjectETagAsync(row.ProjectId, row.StorageKey, ct).ConfigureAwait(false);
                if (!string.Equals(before, after, StringComparison.Ordinal))
                    continue;   // changed while hashing: try again next sweep

                // Conditional write (QA finding 7): save only while the row's hash is still null and
                // the two HEADs agree, so a concurrent overwrite/move that cleared the hash between
                // our read and our save is never overwritten with a hash for the replaced bytes.
                if (db.Database.IsNpgsql())
                {
                    int updated = await db.ProjectFiles
                        .Where(f => f.Id == row.Id
                                    && f.StorageKey == row.StorageKey
                                    && f.ContentSha256 == null
                                    && f.ContentSha256ETag == null)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(f => f.ContentSha256, hash)
                            .SetProperty(f => f.ContentSha256ETag, before), ct)
                        .ConfigureAwait(false);

                    // ExecuteUpdate bypasses the tracker; drop the stale instance so no later
                    // SaveChanges in this scope writes it unconditionally.
                    db.Entry(row).State = EntityState.Detached;
                    if (updated == 1)
                        saved++;
                    continue;
                }

                // InMemory has no ExecuteUpdate and no concurrency: reload and re-check the
                // condition, then save only when it still holds.
                await db.Entry(row).ReloadAsync(ct).ConfigureAwait(false);
                if (row.ContentSha256 is not null || row.ContentSha256ETag is not null)
                    continue;

                row.ContentSha256 = hash;
                row.ContentSha256ETag = before;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                saved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Hashing file {FileId} failed; it is skipped until the next restart", row.Id);
                db.Entry(row).State = EntityState.Detached;
                Remember(row.Id);
            }
        }

        return saved;
    }

    private void Remember(Guid id)
    {
        if (_failed.Count >= MaxRememberedFailures)
            _failed.Clear();
        _failed.Add(id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ProjectHistoryOptions options = _options.CurrentValue;
                if (options.HashSweepEnabled)
                {
                    try
                    {
                        await SweepOnceAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "The project file hash sweep failed; the next one covers the same rows");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.HashSweepIntervalSeconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
