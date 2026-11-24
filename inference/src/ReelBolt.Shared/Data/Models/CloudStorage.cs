namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A file-sharing platform a project can mirror a folder from (docs/cloud-drive-sync.md).
/// Persisted as an int: append-only, never renumber.
/// </summary>
public enum CloudStorageProviderKind
{
    /// <summary>Google Drive: My Drive and shared drives.</summary>
    GoogleDrive = 0,

    /// <summary>Microsoft OneDrive (personal and business) and SharePoint document libraries, through Microsoft Graph.</summary>
    OneDrive = 1,

    /// <summary>Dropbox: the personal space and, for a team member, the team space.</summary>
    Dropbox = 2
}

/// <summary>
/// The fixed strings a provider kind is known by. The slug is what appears in URLs
/// (<c>/api/v1/cloud-storage/{slug}/…</c>, the OAuth redirect URI registered with the vendor) and in
/// the dashboard; the source kind is what <c>project_files.source_kind</c> carries for a file a sync
/// wrote. Neither may ever change once shipped: a registered redirect URI and every stored row depend on it.
/// </summary>
public static class CloudStorageProviders
{
    public static readonly IReadOnlyList<CloudStorageProviderKind> All =
        [CloudStorageProviderKind.GoogleDrive, CloudStorageProviderKind.OneDrive, CloudStorageProviderKind.Dropbox];

    public static string Slug(CloudStorageProviderKind kind) => kind switch
    {
        CloudStorageProviderKind.GoogleDrive => "google-drive",
        CloudStorageProviderKind.OneDrive => "onedrive",
        CloudStorageProviderKind.Dropbox => "dropbox",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static bool TryParseSlug(string? slug, out CloudStorageProviderKind kind)
    {
        foreach (CloudStorageProviderKind candidate in All)
        {
            if (string.Equals(Slug(candidate), slug, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>The <c>project_files.source_kind</c> of a file a sync from this provider wrote.</summary>
    public static string SourceKind(CloudStorageProviderKind kind) => kind switch
    {
        CloudStorageProviderKind.GoogleDrive => "google_drive",
        CloudStorageProviderKind.OneDrive => "onedrive",
        CloudStorageProviderKind.Dropbox => "dropbox",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static string DisplayName(CloudStorageProviderKind kind) => kind switch
    {
        CloudStorageProviderKind.GoogleDrive => "Google Drive",
        CloudStorageProviderKind.OneDrive => "OneDrive / SharePoint",
        CloudStorageProviderKind.Dropbox => "Dropbox",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

/// <summary>
/// One person's connected account on a file-sharing platform, in one workspace
/// (<c>cloud_storage_connections</c>). Mirrors <see cref="GitHubAccountLink"/>: owner-scoped (every
/// query filters on <see cref="UserId"/> AND <see cref="OrganizationId"/>, and anything else is a 404),
/// tokens encrypted at rest with <c>ISecretProtector</c>, never logged, never returned by the API.
/// A person may connect more than one account of the same provider; unique on
/// (user, organization, provider, account id).
/// </summary>
public class CloudStorageConnection
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid OrganizationId { get; set; }

    public CloudStorageProviderKind Provider { get; set; }

    /// <summary>The provider's stable account id (Google permission id, Graph user id, Dropbox account id). Never the email.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Display only.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>Display only.</summary>
    public string? AccountDisplayName { get; set; }

    public string ProtectedAccessToken { get; set; } = string.Empty;

    public string? ProtectedRefreshToken { get; set; }

    public DateTime? AccessTokenExpiresAt { get; set; }

    /// <summary>Scopes the provider actually granted, space separated, as reported at token exchange.</summary>
    public string? GrantedScopes { get; set; }

    /// <summary>
    /// Plain-language reason the connection stopped working (refresh refused, access revoked at the
    /// provider). Null while it works. Shown in the dashboard as "reconnect".
    /// </summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One in-flight OAuth authorization (<c>cloud_storage_oauth_states</c>). Mirrors
/// <c>GitHubOAuthState</c>: the state is stored as a SHA-256 hash, the PKCE verifier encrypted, the row
/// lives 10 minutes and is consumed exactly once, and it is bound to the user and workspace that started it.
/// </summary>
public class CloudStorageOAuthState
{
    public Guid Id { get; set; }

    /// <summary>Hex SHA-256 of the state value sent to the provider. Unique.</summary>
    public string StateHash { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public Guid OrganizationId { get; set; }

    public CloudStorageProviderKind Provider { get; set; }

    public string ProtectedCodeVerifier { get; set; } = string.Empty;

    /// <summary>A same-origin <c>/app/…</c> path to send the browser back to. Validated before it is stored.</summary>
    public string? ReturnPath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }
}

/// <summary>
/// One project mirroring one folder of one file-sharing platform (<c>project_cloud_links</c>). At most
/// one link per (project, provider), so <see cref="CloudStorageProviders.SourceKind"/> alone says which
/// link a synced file belongs to. A child of <see cref="Project"/>: no tenant query filter, reached only
/// through the project access check, exactly like <see cref="ProjectGitHubLink"/>.
///
/// <para>
/// A sync is one-way, provider to project: files in the folder (and in the selection) are created,
/// rewritten in place when their revision changes, and deleted when they leave the folder or the
/// selection. A file a person uploaded or an agent wrote, or one another source wrote, is never touched.
/// </para>
/// </summary>
public class ProjectCloudLink
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The project's organization at link time. Audit only.</summary>
    public Guid OrganizationId { get; set; }

    public CloudStorageProviderKind Provider { get; set; }

    /// <summary>
    /// The connection whose tokens read the folder. Null once that connection is deleted: the link
    /// stays (so do its files) and a sync fails with "link it again".
    /// </summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>
    /// The drive the folder lives in: a Google shared drive id, a Graph drive id (OneDrive or a
    /// SharePoint library), a Dropbox namespace id. Null means the account's own default space.
    /// </summary>
    public string? DriveId { get; set; }

    /// <summary>The provider's stable id of the linked folder (survives a rename). Null means the drive root.</summary>
    public string? FolderId { get; set; }

    /// <summary>Human-readable path of the folder when it was linked. Display only; never matched on.</summary>
    public string FolderDisplayPath { get; set; } = string.Empty;

    /// <summary>
    /// The files and folders mirrored, as a JSON array of folder-relative paths, exactly the format and
    /// rules of <see cref="ProjectGitHubLink.IncludePathsJson"/> (validated with <c>GitHubIncludePaths</c>).
    /// Null means everything in the folder.
    /// </summary>
    public string? IncludePathsJson { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    /// <summary>Files the project mirrors after the last successful sync.</summary>
    public int LastSyncedFileCount { get; set; }

    /// <summary>Plain-language reason the last sync failed. Null after a success.</summary>
    public string? LastError { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
