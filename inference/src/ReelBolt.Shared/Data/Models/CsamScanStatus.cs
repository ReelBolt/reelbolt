namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Where a project file stands with the CSAM hash-matching control (Project Arachnid Shield).
/// <para>
/// Stored by name on <c>project_files.csam_status</c>. New rows start <see cref="Pending"/> (the
/// initializer), while the database default is <see cref="NotScanned"/> so rows written before this
/// control existed are never picked up as work by the sweeper.
/// </para>
/// </summary>
public enum CsamScanStatus
{
    /// <summary>No scan has produced a verdict (feature off, missing configuration, provider failure, or a legacy row).</summary>
    NotScanned,

    /// <summary>Written but not yet scanned; the sweeper picks these up.</summary>
    Pending,

    /// <summary>A sweeper has claimed the row (see <see cref="ProjectFile.CsamScanClaimedAt"/>); no other sweeper may take it.</summary>
    Scanning,

    /// <summary>Scanned and no known match.</summary>
    Clean,

    /// <summary>A match (or a provider-side failure to clear one): the bytes are moved under <c>quarantine/</c> and never served.</summary>
    Quarantined
}
