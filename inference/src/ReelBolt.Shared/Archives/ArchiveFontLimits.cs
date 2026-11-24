namespace ReelBolt.Shared.Archives;

/// <summary>
/// The bounded channel that carries font files out of an import archive. Fonts are the only new
/// binary kind: magic-checked, digest-checked and capped, never decoded by the host.
/// </summary>
public static class ArchiveFontLimits
{
    public const int MaxEntryBytes = 4 * 1024 * 1024;
    public const int MaxEntries = 8;
    public const int MaxTotalBytes = 16 * 1024 * 1024;
}
