using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>
/// Removes a vendor-side copy of an avatar's media (<c>project_avatar_vendor_assets</c>, for example a MiniMax
/// <c>mm_file://</c> upload). Best effort: it never throws for a vendor failure, it answers the outcome, and the avatar
/// row is deleted whatever the outcome (a withdrawn consent is never kept alive here). Phase 1 has no vendor uploads, so
/// the registered implementation is <see cref="NotSupportedAvatarVendorAssetDeleter"/>; phase 2 replaces it.
/// </summary>
public interface IAvatarVendorAssetDeleter
{
    Task<AvatarVendorAssetDeleteOutcome> DeleteAsync(ProjectAvatarVendorAsset asset, CancellationToken ct);
}

/// <summary>Phase 1: no vendor accepts an avatar upload yet, so there is nothing a call could delete.</summary>
public sealed class NotSupportedAvatarVendorAssetDeleter : IAvatarVendorAssetDeleter
{
    public Task<AvatarVendorAssetDeleteOutcome> DeleteAsync(ProjectAvatarVendorAsset asset, CancellationToken ct) =>
        Task.FromResult(AvatarVendorAssetDeleteOutcome.NotSupported);
}

/// <summary>The wire words of <see cref="AvatarVendorAssetDeleteOutcome"/> (the delete report and the audit events).</summary>
public static class AvatarVendorAssetOutcomeWords
{
    public static string Of(AvatarVendorAssetDeleteOutcome outcome) => outcome switch
    {
        AvatarVendorAssetDeleteOutcome.Deleted => "deleted",
        AvatarVendorAssetDeleteOutcome.AlreadyGone => "already_gone",
        AvatarVendorAssetDeleteOutcome.VendorRefused => "vendor_refused",
        AvatarVendorAssetDeleteOutcome.VendorError => "vendor_error",
        AvatarVendorAssetDeleteOutcome.NotSupported => "not_supported",
        _ => "vendor_error"
    };
}
