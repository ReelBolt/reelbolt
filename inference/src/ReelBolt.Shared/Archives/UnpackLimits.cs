namespace ReelBolt.Shared.Archives;

/// <summary>
/// The caps of the "unpack a customer code zip" profile, shared by the import worker (which enforces them) and
/// the API client (which re-checks the worker's answer against them).
/// </summary>
public static class UnpackLimits
{
    public const long MaxArchiveBytes = 64L * 1024 * 1024;
    public const int MaxEntries = 20_000;
    public const int MaxIncludedEntries = 2_000;
    public const long MaxTotalBytes = 128L * 1024 * 1024;
    public const long MaxEntryBytes = 32L * 1024 * 1024;
    public const int MaxRatio = 100;
    public const int MaxDepth = 16;

    /// <summary>The wire manifest may not exceed this many bytes.</summary>
    public const int MaxManifestBytes = 1024 * 1024;

    /// <summary>At most this many skipped-path samples travel in a manifest.</summary>
    public const int MaxSkippedSamples = 50;

    /// <summary>The response media type and its four-byte magic.</summary>
    public const string ContentType = "application/vnd.reelbolt.unpack.v1";
    public static ReadOnlySpan<byte> Magic => "RBU1"u8;

    /// <summary>Worker-side abort reason when the manifest itself would be too big (never a partial answer).</summary>
    public const string ManifestTooLarge = "MANIFEST_TOO_LARGE";

    public static SafeZipLimits ToSafeZipLimits() => new(
        MaxEntries, MaxTotalBytes, MaxEntryBytes, MaxRatio, MaxDepth, MaxArchiveBytes, MaxIncludedEntries);
}
