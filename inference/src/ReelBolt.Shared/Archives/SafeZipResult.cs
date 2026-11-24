namespace ReelBolt.Shared.Archives;

/// <summary>
/// The outcome of one <see cref="SafeZipReader.Read"/> call. When <see cref="Aborted"/> is true the
/// archive was refused as a whole: <see cref="Entries"/> is always empty so a caller can never act on
/// partial content, while <see cref="Rejected"/> keeps every per-entry refusal collected before the abort.
/// </summary>
public sealed record SafeZipResult(
    IReadOnlyList<SafeZipEntry> Entries,
    IReadOnlyList<SafeZipRejected> Rejected,
    bool Aborted,
    string? AbortReason)
{
    /// <summary>
    /// Every byte the reader inflated or copied out of the archive, accepted or not. Rejected entries cost
    /// real work, so they count here and against <see cref="SafeZipLimits.MaxTotalBytes"/>; a bomb refused on
    /// its declared numbers alone reports 0.
    /// </summary>
    public long InflatedBytes { get; init; }
}
