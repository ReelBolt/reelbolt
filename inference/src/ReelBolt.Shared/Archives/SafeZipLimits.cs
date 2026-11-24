namespace ReelBolt.Shared.Archives;

/// <summary>
/// Hard limits applied to every archive the product reads. The defaults are the shipped contract:
/// 2000 entries, 64 MiB total, 8 MiB per entry, 100:1 compression ratio, 12 path segments and a
/// 32 MiB archive. Nothing in the reader may exceed them, and a caller that needs different numbers
/// passes its own instance. <c>MaxIncludedEntries</c> (null = unlimited, the default) caps how many entries may be
/// ACCEPTED; one more aborts the archive with <c>TOO_MANY_FILES</c>.
/// </summary>
public sealed record SafeZipLimits(
    int MaxEntries = 2000,
    long MaxTotalBytes = 67108864,
    long MaxEntryBytes = 8388608,
    int MaxRatio = 100,
    int MaxDepth = 12,
    long MaxArchiveBytes = 33554432,
    int? MaxIncludedEntries = null);

/// <summary>One accepted member: its relative path (Unicode, NFC-normalised) and its exact bytes.</summary>
public sealed record SafeZipEntry(string Path, byte[] Content);

/// <summary>One member that was refused, with the exact reason code and the path it was seen under.</summary>
public sealed record SafeZipRejected(string Path, string Reason, bool Encrypted = false);
