namespace ReelBolt.Shared.Archives;

/// <summary>The bounded channel that carries image assets out of an import archive.</summary>
public static class ArchiveAssetLimits
{
    public const int MaxEntryBytes = 2 * 1024 * 1024;
    public const int MaxTotalBytes = 8 * 1024 * 1024;
    public const int MaxEntries = 16;
}
