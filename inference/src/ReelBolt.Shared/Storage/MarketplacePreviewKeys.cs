using System.Linq;

namespace ReelBolt.Shared.Storage;

/// <summary>
/// The platform object keys of a marketplace preview: <c>marketplace/{recipeId}/{versionId}/preview.mp4</c> and
/// <c>poster.jpg</c>. Built only from two Guids, never from user input, and outside <c>projects/</c> by construction, so a
/// project key can neither collide with nor be steered into this space.
/// </summary>
public static class MarketplacePreviewKeys
{
    public const string Root = "marketplace/";
    public const string PreviewFileName = "preview.mp4";
    public const string PosterFileName = "poster.jpg";

    public static string RecipePrefix(Guid recipeId) => $"{Root}{recipeId:D}/";

    public static string VersionPrefix(Guid recipeId, Guid versionId) => $"{RecipePrefix(recipeId)}{versionId:D}/";

    public static string Preview(Guid recipeId, Guid versionId) => VersionPrefix(recipeId, versionId) + PreviewFileName;

    public static string Poster(Guid recipeId, Guid versionId) => VersionPrefix(recipeId, versionId) + PosterFileName;

    /// <summary>
    /// True for any key under a <c>quarantine/</c> segment: CSAM evidence, which no code path may ever delete (docs/csam.md, preserve what
    /// exists). Every prefix or object delete must skip these keys.
    /// </summary>
    public static bool IsQuarantine(string? key) =>
        key is not null && key.Contains("/quarantine/", StringComparison.OrdinalIgnoreCase);

    /// <summary>A run nonce is exactly 32 lowercase hex characters (16 random bytes).</summary>
    public static bool IsValidNonce(string? nonce) =>
        nonce is { Length: 32 } && nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string NewNonce() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Prefix of one run's staging objects. The engine writes only here; only the API writes the final keys.</summary>
    public static string StagingPrefix(Guid recipeId, Guid versionId, string nonce) =>
        IsValidNonce(nonce) ? $"{VersionPrefix(recipeId, versionId)}staging/{nonce}/" : throw new ArgumentException("Invalid run nonce.", nameof(nonce));

    public static string StagingPreview(Guid recipeId, Guid versionId, string nonce) => StagingPrefix(recipeId, versionId, nonce) + PreviewFileName;

    public static string StagingPoster(Guid recipeId, Guid versionId, string nonce) => StagingPrefix(recipeId, versionId, nonce) + PosterFileName;

    /// <summary>Everything staged for a version (all runs).</summary>
    public static string StagingRoot(Guid recipeId, Guid versionId) => VersionPrefix(recipeId, versionId) + "staging/";

    /// <summary>Where a preview that matched the CSAM control is kept as evidence (never served, never purged by a prefix delete).</summary>
    public static string QuarantinedPreview(Guid recipeId, Guid versionId) => VersionPrefix(recipeId, versionId) + "quarantine/" + PreviewFileName;

    public static string QuarantinedPoster(Guid recipeId, Guid versionId) => VersionPrefix(recipeId, versionId) + "quarantine/" + PosterFileName;

    /// <summary>True when <paramref name="key"/> is exactly the preview or poster key of this version.</summary>
    public static bool IsVersionObject(Guid recipeId, Guid versionId, string? key) =>
        string.Equals(key, Preview(recipeId, versionId), StringComparison.Ordinal)
        || string.Equals(key, Poster(recipeId, versionId), StringComparison.Ordinal);

    /// <summary>True when <paramref name="storageKey"/> is a project object (<c>projects/{guid}/...</c>) with no traversal.</summary>
    public static bool IsProjectSourceKey(string? storageKey)
    {
        if (string.IsNullOrEmpty(storageKey) || storageKey.Contains("..", StringComparison.Ordinal) || storageKey.Contains('\\'))
            return false;
        const string prefix = "projects/";
        if (!storageKey.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        int slash = storageKey.IndexOf('/', prefix.Length);
        return slash > prefix.Length && slash < storageKey.Length - 1 && Guid.TryParseExact(storageKey[prefix.Length..slash], "D", out _);
    }
}
