using ReelBolt.Shared.Billing;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// The storage quota as a background sync applies it (GitHub pull, cloud-drive sync): every new or
/// changed file is checked through <see cref="IPlanGateService.CheckUploadAsync"/> with its NET growth
/// before it is written; a file that does not fit is skipped and counted, and the sync carries on with
/// the rest (deletions and unchanged files still apply). It ends with <see cref="Message"/> as the link's
/// <c>LastError</c>.
/// <para>
/// A sync saves its rows in batches, so the database total lags what the sync has already written. The
/// growth written since the last save is carried here and added to every check, which is what keeps a
/// batch of 25 files from each passing against the same, stale total.
/// </para>
/// </summary>
public sealed class SyncStorageQuota(IPlanGateService? planGate, Guid organizationId)
{
    private long _unsavedGrowth;
    private long? _usedAtRefusal;
    private long? _limit;

    /// <summary>How many files were not copied because they did not fit.</summary>
    public int Refused { get; private set; }

    /// <summary>
    /// True when <paramref name="netGrowth"/> more bytes fit. A file that does not grow storage always
    /// fits. False counts the file as refused.
    /// </summary>
    public async Task<bool> FitsAsync(long netGrowth, CancellationToken ct)
    {
        if (planGate is null || netGrowth <= 0) return true;

        PlanGateViolation? violation = await planGate.CheckUploadAsync(organizationId, _unsavedGrowth + netGrowth, ct);
        if (violation is null) return true;

        Refused++;
        _usedAtRefusal = (violation.Current ?? 0) + _unsavedGrowth;
        _limit = violation.Limit;
        return false;
    }

    /// <summary>Records that a file changed storage by <paramref name="netGrowth"/> bytes (not saved yet).</summary>
    public void Wrote(long netGrowth) => _unsavedGrowth += netGrowth;

    /// <summary>The rows written so far are saved: the database total now includes them.</summary>
    public void Saved() => _unsavedGrowth = 0;

    /// <summary>The sentence for the link's <c>LastError</c>, or null when every file fit.</summary>
    public string? Message => Refused == 0 ? null : StorageFullMessage(_usedAtRefusal ?? 0, _limit, Refused);

    /// <summary>"Storage is full (X of Y used) — N files were not copied. Free space or upgrade, then sync again."</summary>
    public static string StorageFullMessage(long usedBytes, long? limitBytes, int notCopied)
    {
        string usage = limitBytes is { } limit
            ? $"{StorageUsageService.FormatBytes(usedBytes)} of {StorageUsageService.FormatBytes(limit)} used"
            : $"{StorageUsageService.FormatBytes(usedBytes)} used";
        string files = notCopied == 1 ? "1 file was not copied" : $"{notCopied} files were not copied";
        return $"Storage is full ({usage}) — {files}. Free space or upgrade, then sync again.";
    }
}
