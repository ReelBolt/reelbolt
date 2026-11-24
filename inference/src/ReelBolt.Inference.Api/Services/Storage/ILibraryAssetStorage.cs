using ReelBolt.Shared.Archives;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// The one answer to "how big may a brand-library asset be": twenty-four assets per library, an SVG
/// bounded by the sanitizer's own input cap and a font by the archive font-entry cap. Defined once so
/// the route, the tests and the object-store keys cannot drift apart.
/// </summary>
public static class LibraryAssetLimits
{
    /// <summary>The most assets one library may hold.</summary>
    public const int MaxAssets = 24;

    /// <summary>An SVG is bounded by what <see cref="SvgSanitizer"/> will accept.</summary>
    public const int SvgMaxBytes = SvgSanitizer.MaxInputBytes;

    /// <summary>A font is bounded by the archive importer's per-entry font cap.</summary>
    public const int FontMaxBytes = ArchiveFontLimits.MaxEntryBytes;
}

/// <summary>
/// The object-store seam for library-owned assets: upload returns the storage key the row will carry,
/// read opens the object (null when it is absent) and delete removes it. The implementation owns the
/// <c>design-libraries/{orgId}/{libraryId}/assets/{assetId}/{name}</c> prefix; callers never build it.
/// </summary>
public interface ILibraryAssetStorage
{
    /// <summary>Stores the (already validated) bytes and returns the key to persist on the row.</summary>
    Task<string> UploadAsync(
        Guid organizationId, Guid libraryId, Guid assetId,
        Stream content, string fileName, string contentType, CancellationToken ct);

    /// <summary>Opens the stored object, or answers null when it is absent.</summary>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct);

    /// <summary>Deletes the stored object; deleting an absent key is a no-op.</summary>
    Task DeleteAsync(string storageKey, CancellationToken ct);
}
