namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// What happened when a vendor-side copy of an avatar's media was deleted. <b>APPEND ONLY</b>: persisted by name.
/// The delete report shows these as <c>deleted</c>, <c>already_gone</c>, <c>vendor_refused</c>, <c>vendor_error</c>,
/// <c>not_supported</c>.
/// </summary>
public enum AvatarVendorAssetDeleteOutcome
{
    Deleted,
    AlreadyGone,
    VendorRefused,
    VendorError,
    NotSupported
}

/// <summary>
/// A copy of an avatar's media that a vendor holds under its own id (for example a MiniMax <c>mm_file://{file_id}</c>
/// upload, valid 7 days), recorded so delete and retention can remove it (<c>project_avatar_vendor_assets</c>,
/// API-owned). The shape is fixed in phase 1 so delete and retention code is written once; the table stays empty
/// until phase 2 writes it. A row is never reused after its avatar's consent is revoked.
/// </summary>
public class ProjectAvatarVendorAsset
{
    public Guid Id { get; set; }

    public Guid AvatarId { get; set; }

    /// <summary>The reference the upload carries, when it is one reference (cascade-deleted with it).</summary>
    public Guid? ReferenceId { get; set; }

    /// <summary>
    /// The <c>VideoGeneration</c> model capability (the provider account) the upload belongs to. Null once that
    /// capability is deleted; the asset can then no longer be deleted through us and the outcome is recorded as such.
    /// </summary>
    public Guid? ModelCapabilityId { get; set; }

    /// <summary>The vendor's id. Never shown to the assistant.</summary>
    public string RemoteAssetId { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The vendor's own retention, when documented.</summary>
    public DateTime? VendorExpiresAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public AvatarVendorAssetDeleteOutcome? DeleteOutcome { get; set; }
}
