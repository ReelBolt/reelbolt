using System.Globalization;
using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.Shared.History;

/// <summary>
/// Builds the content identity a project-history file item records. The identity deliberately
/// carries how sure we are that it names the bytes: a verified hash, an ETag-derived identity read
/// at capture, or a "unverified" marker for a file whose HEAD was skipped or failed.
/// </summary>
public static class ContentId
{
    /// <summary>The column length (<c>project_version_items.content_id</c>).</summary>
    public const int MaxLength = 80;

    public const string Sha256Prefix = "sha256:";
    public const string EtagPrefix = "etag:";
    public const string UnverifiedPrefix = "unverified:";

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> when the row knows a 64-hex content hash AND the ETag that hash was
    /// taken at; otherwise null (the caller falls back to a HEAD or to an unverified marker).
    /// </summary>
    public static string? FromKnownHash(string? contentSha256, string? contentSha256ETag)
    {
        if (!IsSha256Hex(contentSha256) || string.IsNullOrEmpty(contentSha256ETag))
            return null;

        return Sha256Prefix + contentSha256!.ToLowerInvariant();
    }

    /// <summary>
    /// <c>etag:&lt;hex&gt;</c> where the hex is SHA-256 of <c>"{etag}|{sizeBytes}"</c>, computed at
    /// capture from a HEAD. The store's ETag is opaque, so this is an identity, not a content hash.
    /// </summary>
    public static string FromEtag(string etag, long sizeBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(etag);
        string material = string.Create(
            CultureInfo.InvariantCulture,
            $"{etag}|{sizeBytes}");
        return EtagPrefix + CanonicalJson.Sha256Hex(material);
    }

    /// <summary>
    /// The marker for a file that could not be verified (past the HEAD cap, or the HEAD failed):
    /// restorable only while the live file still exists and is unchanged.
    /// </summary>
    public static string Unverified(Guid fileId) =>
        UnverifiedPrefix + fileId.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>True when the identity was computed from a stored content hash.</summary>
    public static bool IsSha256(string? contentId) =>
        contentId is not null && contentId.StartsWith(Sha256Prefix, StringComparison.Ordinal);

    /// <summary>True when the identity was derived from a HEAD ETag + size.</summary>
    public static bool IsEtag(string? contentId) =>
        contentId is not null && contentId.StartsWith(EtagPrefix, StringComparison.Ordinal);

    /// <summary>True when the item could not be verified at capture.</summary>
    public static bool IsUnverified(string? contentId) =>
        contentId is not null && contentId.StartsWith(UnverifiedPrefix, StringComparison.Ordinal);

    /// <summary>True when the string is exactly 64 lowercase-or-uppercase hex characters.</summary>
    public static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64)
            return false;

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        return true;
    }
}
