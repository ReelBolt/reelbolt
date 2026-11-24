using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>Settings for <see cref="TransportStreamMimeBackfillService"/>.</summary>
public sealed class TransportStreamMimeBackfillOptions
{
    /// <summary>Configuration section, bound by <c>AddInferenceApiServices</c>.</summary>
    public const string SectionName = "ProjectFiles:TransportStreamBackfill";

    /// <summary>Whether the one pass runs at startup. Default on: it only re-types rows the bytes prove wrong.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds to wait after startup, so the pass does not compete with the API's own boot work.</summary>
    public int StartupDelaySeconds { get; set; } = 30;

    /// <summary>Rows read per batch (keyset paged by id).</summary>
    public int BatchSize { get; set; } = 200;
}

/// <summary>What one pass did.</summary>
public sealed record TransportStreamMimeBackfillResult(int Examined, int Retyped, int Unreadable);

/// <summary>
/// Issue 8 (2026-11-03): repairs project files stored before the upload, sync and import paths read
/// the bytes of a <c>.ts</c>-family file. A row whose MIME is a transport-stream type and whose name is
/// <c>.ts</c>/<c>.mts</c>/<c>.cts</c> has the first <see cref="ProjectFileTextPolicy.SniffBytes"/> of its
/// object read, and is re-typed through <see cref="TransportStreamMimeSniffer"/> (TypeScript becomes
/// <c>text/plain</c>; a real transport stream keeps its type).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not a migration.</b> The answer needs the bytes, which live in object storage; a migration can
/// only see the row. <c>project_files</c> is API-owned, so the API is the right writer.
/// </para>
/// <para>
/// <b>Idempotent and safe to repeat.</b> A re-typed row no longer matches the selection; a real
/// transport stream is re-read on each pass (one ranged-size read of a few KB, no write). Only
/// <see cref="ProjectFile.MimeType"/> changes: summary, indexing and verdict columns are left as they
/// are, so the pass never queues a model call nobody asked for. A quarantined row (CSAM or moderation)
/// is never read. A row whose object cannot be read is left untouched and counted.
/// </para>
/// </remarks>
public sealed class TransportStreamMimeBackfill(
    InferenceApiDbContext db,
    IFileStorageService storage,
    ILogger<TransportStreamMimeBackfill> logger)
{
    /// <summary>The stored types a <c>.ts</c> file was given by the extension maps (lower-case, as stored).</summary>
    internal static readonly string[] CandidateMimeTypes =
        ["video/vnd.dlna.mpeg-tts", "video/mp2t", "video/mp2ts", "video/x-mp2t", "video/avchd-stream", "video/x-m2ts"];

    /// <summary>One pass over every candidate row, in id order, <paramref name="batchSize"/> at a time.</summary>
    public async Task<TransportStreamMimeBackfillResult> RunOnceAsync(int batchSize, CancellationToken ct)
    {
        batchSize = Math.Max(1, batchSize);
        int examined = 0, retyped = 0, unreadable = 0;
        Guid? after = null;

        while (true)
        {
            IQueryable<ProjectFile> query = db.ProjectFiles
                .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
                .Where(f => CandidateMimeTypes.Contains(f.MimeType.ToLower())
                    && (f.OriginalFileName.ToLower().EndsWith(".ts")
                        || f.OriginalFileName.ToLower().EndsWith(".mts")
                        || f.OriginalFileName.ToLower().EndsWith(".cts"))
                    && f.StorageKey != null && f.StorageKey != ""
                    && f.CsamStatus != CsamScanStatus.Quarantined
                    && f.ModerationStatus != FileModerationStatus.Quarantined);
            if (after is { } last)
                query = query.Where(f => f.Id.CompareTo(last) > 0);

            List<ProjectFile> batch = await query.OrderBy(f => f.Id).Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0)
                break;

            foreach (ProjectFile file in batch)
            {
                examined++;
                string? resolved;
                try
                {
                    await using Stream content = await storage.DownloadAsync(file.ProjectId, file.StorageKey, ct);
                    byte[] head = await ProjectFileTextPolicy.ReadHeadAsync(content, ct);
                    resolved = TransportStreamMimeSniffer.Resolve(file.MimeType, file.OriginalFileName, head);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    unreadable++;
                    logger.LogWarning(ex, "Could not read project file {FileId} to check its .ts type; left as is.", file.Id);
                    continue;
                }

                if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, file.MimeType, StringComparison.Ordinal))
                {
                    file.MimeType = resolved;
                    retyped++;
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            after = batch[^1].Id;
        }

        return new TransportStreamMimeBackfillResult(examined, retyped, unreadable);
    }
}

/// <summary>Runs <see cref="TransportStreamMimeBackfill"/> once, shortly after startup.</summary>
public sealed class TransportStreamMimeBackfillService(
    IServiceScopeFactory scopeFactory,
    IOptions<TransportStreamMimeBackfillOptions> options,
    ILogger<TransportStreamMimeBackfillService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TransportStreamMimeBackfillOptions settings = options.Value;
        if (!settings.Enabled)
            return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, settings.StartupDelaySeconds)), stoppingToken);
            using IServiceScope scope = scopeFactory.CreateScope();
            TransportStreamMimeBackfill backfill = scope.ServiceProvider.GetRequiredService<TransportStreamMimeBackfill>();
            TransportStreamMimeBackfillResult result = await backfill.RunOnceAsync(settings.BatchSize, stoppingToken);
            logger.LogInformation(
                ".ts type backfill: examined {Examined} file(s), re-typed {Retyped}, could not read {Unreadable}",
                result.Examined, result.Retyped, result.Unreadable);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the next start runs the pass again.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The .ts type backfill failed; it runs again on the next start");
        }
    }
}
