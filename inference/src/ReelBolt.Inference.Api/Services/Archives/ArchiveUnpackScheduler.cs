using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Archives;

/// <summary>An in-process nudge so the worker starts a new unpack at once; the periodic sweep is the durable path.</summary>
public sealed class ArchiveUnpackSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Nudge() => _channel.Writer.TryWrite(true);

    /// <summary>Waits for a nudge or until <paramref name="timeout"/> passes, whichever is first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Opens a zip against the import worker. The seam the unpack processor is tested through.</summary>
public interface IArchiveUnpackGateway
{
    Task<ArchiveUnpackSession> OpenAsync(Stream zip, long length, CancellationToken ct);
}

public sealed class ArchiveUnpackGateway(ArchiveUnpackClient client) : IArchiveUnpackGateway
{
    public Task<ArchiveUnpackSession> OpenAsync(Stream zip, long length, CancellationToken ct) =>
        client.OpenAsync(zip, length, ct);
}

/// <summary>Records a Pending unpack for a zip row and wakes the worker.</summary>
public interface IArchiveUnpackScheduler
{
    /// <summary>
    /// The post-add hook: when <paramref name="file"/> is a zip, queues an unpack. Never throws (a failure is logged and the
    /// upload stands); a quarantined row is never queued.
    /// </summary>
    Task ScheduleForUploadAsync(ProjectFile file, Guid userId, CancellationToken ct);

    /// <summary>
    /// Queues an unpack of <paramref name="zip"/>; null when one is already Pending or Running for it.
    /// </summary>
    Task<ProjectArchiveUnpack?> EnqueueAsync(ProjectFile zip, Guid organizationId, Guid userId, CancellationToken ct);
}

public sealed class ArchiveUnpackScheduler : IArchiveUnpackScheduler
{
    private readonly InferenceApiDbContext _db;
    private readonly ArchiveUnpackSignal _signal;
    private readonly ILogger<ArchiveUnpackScheduler>? _logger;

    public ArchiveUnpackScheduler(InferenceApiDbContext db, ArchiveUnpackSignal signal, ILogger<ArchiveUnpackScheduler>? logger = null)
    {
        _db = db;
        _signal = signal;
        _logger = logger;
    }

    public async Task ScheduleForUploadAsync(ProjectFile file, Guid userId, CancellationToken ct)
    {
        if (file.CsamStatus == CsamScanStatus.Quarantined || !ArchiveUnpackPaths.IsZipFile(file)) return;

        try
        {
            Guid organizationId = await _db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == file.ProjectId)
                .Select(p => p.OrganizationId)
                .FirstOrDefaultAsync(ct);
            if (organizationId == Guid.Empty) return;

            await EnqueueAsync(file, organizationId, userId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not queue the unpack of uploaded zip {FileId}; the upload stands", file.Id);
        }
    }

    public async Task<ProjectArchiveUnpack?> EnqueueAsync(ProjectFile zip, Guid organizationId, Guid userId, CancellationToken ct)
    {
        bool active = await _db.ProjectArchiveUnpacks.AnyAsync(
            u => u.SourceFileId == zip.Id
                 && (u.Status == ArchiveUnpackRunStatus.Pending || u.Status == ArchiveUnpackRunStatus.Running), ct);
        if (active) return null;

        ProjectArchiveUnpack row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = zip.ProjectId,
            OrganizationId = organizationId,
            SourceFileId = zip.Id,
            ArchiveName = zip.OriginalFileName.Length > 255 ? zip.OriginalFileName[..255] : zip.OriginalFileName,
            ArchiveSizeBytes = zip.SizeBytes,
            Status = ArchiveUnpackRunStatus.Pending,
            RequestedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ProjectArchiveUnpacks.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsActiveUnpackConflict(ex))
        {
            // The partial unique index: another request queued the same zip first.
            _db.Entry(row).State = EntityState.Detached;
            return null;
        }

        _signal.Nudge();
        return row;
    }

    /// <summary>True only for a unique-index violation (Postgres 23505); anything else is a real failure.</summary>
    private static bool IsActiveUnpackConflict(DbUpdateException ex)
    {
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
                return true;
        }

        return false;
    }
}
