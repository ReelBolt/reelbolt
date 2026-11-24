using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// Whether a stored preview may be made for a source object now. A preview is a derivative of the source, so it is
/// never made for a project that is missing or content-quarantined, for an output the CSAM/moderation hold covers, or
/// for a file row that is quarantined. Anything unreadable answers false.
/// </summary>
public static class ImagePreviewGate
{
    public static async Task<bool> MayPreviewAsync(
        InferenceApiDbContext db, ITenantContext? tenant, Guid projectId, string sourceKey, CancellationToken ct)
    {
        try
        {
            using (tenant.System())
            {
                var project = await db.Projects.AsNoTracking()
                    .Where(p => p.Id == projectId)
                    .Select(p => new { p.ContentQuarantinedAt })
                    .FirstOrDefaultAsync(ct);
                if (project is null || project.ContentQuarantinedAt is not null) return false;
                bool quarantinedRow = await db.ProjectFiles.IgnoreQueryFilters().AsNoTracking().AnyAsync(f =>
                    f.ProjectId == projectId && f.StorageKey == sourceKey
                    && (f.CsamStatus == CsamScanStatus.Quarantined || f.ModerationStatus == FileModerationStatus.Quarantined), ct);
                if (quarantinedRow) return false;
                return !await OutputGate.IsKeyHeldAsync(db, projectId, sourceKey, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
