using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>
/// The boundary between the two independent ingest controls. CSAM is the stricter, separate track:
/// a file the CSAM matcher holds (already quarantined, or a move that failed and still names the
/// quarantine destination) must never be read by general moderation, and a file whose CSAM verdict
/// is still pending must not be sent to the general moderation API either.
/// </summary>
public static class CsamScanGate
{
    /// <summary>True when the CSAM control holds this file and no other control may read its bytes.</summary>
    public static bool IsHeld(ProjectFile file) =>
        file.CsamStatus == CsamScanStatus.Quarantined || file.CsamQuarantineKey is not null;

    /// <summary>
    /// True when the CSAM verdict is settled and not a quarantine, so ingest moderation may run.
    /// Pending/Scanning waits for the CSAM sweeper; Quarantined never runs.
    /// </summary>
    public static bool AllowsModeration(ProjectFile file) =>
        AllowsModeration(file, csamEnabled: true);

    /// <summary>
    /// As <see cref="AllowsModeration(ProjectFile)"/>, but a DISABLED CSAM control is settled by
    /// definition: there will never be a verdict to wait for, so pending/scanning rows may proceed
    /// to general moderation. A held row is still refused.
    /// </summary>
    public static bool AllowsModeration(ProjectFile file, bool csamEnabled) =>
        !IsHeld(file)
        && (!csamEnabled || file.CsamStatus is not (CsamScanStatus.Pending or CsamScanStatus.Scanning));

    /// <summary>
    /// The SQL/InMemory predicate for a sweep that must exclude both held and unresolved rows.
    /// Kept as a static expression body so the three state checks cannot drift from <see cref="AllowsModeration"/>.
    /// </summary>
    public static bool AllowsModeration(
        CsamScanStatus status, string? quarantineKey) =>
        status is not (CsamScanStatus.Pending or CsamScanStatus.Scanning or CsamScanStatus.Quarantined)
        && quarantineKey is null;

    /// <summary>
    /// True when a derivative (proxy, filmstrip, still, peaks), a preview or any other egress may be made from
    /// this file: the CSAM verdict is settled (Clean, or NotScanned because the control could not answer) and
    /// the file is not held. Pending/Scanning waits for the verdict; the CSAM sweeper requests the derivatives
    /// once it has one. Same rule as <see cref="AllowsModeration(ProjectFile)"/>, named for its second caller.
    /// </summary>
    public static bool AllowsDerivatives(ProjectFile file) => AllowsModeration(file);

    /// <summary>
    /// The file-row form of <see cref="AllowsDerivatives(ProjectFile)"/> for a source named only by id. A missing
    /// row is not this gate's business (a render of a step result has no scan state): it answers true.
    /// </summary>
    public static async Task<bool> AllowsDerivativesAsync(
        DbContext db, Guid projectId, Guid? projectFileId, CancellationToken ct)
    {
        if (projectFileId is not { } id) return true;
        var row = await db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Id == id && f.ProjectId == projectId)
            .Select(f => new { f.CsamStatus, f.CsamQuarantineKey })
            .FirstOrDefaultAsync(ct);
        return row is null || AllowsModeration(row.CsamStatus, row.CsamQuarantineKey);
    }
}
