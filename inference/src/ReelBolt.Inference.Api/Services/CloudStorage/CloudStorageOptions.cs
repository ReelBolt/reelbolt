using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.CloudStorage;

/// <summary>
/// <c>CloudStorage:*</c> (compose: <c>CloudStorage__GoogleDrive__ClientId</c> …). Everything is inert
/// until configured: a provider without a client id and secret is hidden from the dashboard and its
/// endpoints answer <c>503 provider_not_configured</c>. No credential is required to build or test.
/// </summary>
public sealed class CloudStorageOptions
{
    public const string SectionName = "CloudStorage";

    /// <summary>
    /// The public origin of this deployment (<c>PUBLIC_BASE_URL</c>, e.g. <c>https://reelbolt.example.com</c>).
    /// The OAuth redirect URI registered with each vendor is
    /// <c>{PublicBaseUrl}/api/v1/cloud-storage/{slug}/oauth/callback</c> — see <see cref="RedirectUriFor"/>.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    public GoogleDriveOptions GoogleDrive { get; set; } = new();

    public MicrosoftGraphOptions OneDrive { get; set; } = new();

    public DropboxOptions Dropbox { get; set; } = new();

    /// <summary>Same limits and meaning as the GitHub pull's, applied to the selected files.</summary>
    public int MaxFilesPerSync { get; set; } = 2000;

    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;

    public long MaxTotalBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>How often the background sweep re-syncs every link. 0 disables the sweep (manual "Sync now" only).</summary>
    public int SweepIntervalMinutes { get; set; } = 30;

    /// <summary>Per-request timeout of the providers' HTTP clients (downloads of up to MaxFileBytes included).</summary>
    public int HttpTimeoutSeconds { get; set; } = 300;

    public OAuthClientOptions For(CloudStorageProviderKind kind) => kind switch
    {
        CloudStorageProviderKind.GoogleDrive => GoogleDrive,
        CloudStorageProviderKind.OneDrive => OneDrive,
        CloudStorageProviderKind.Dropbox => Dropbox,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>Null when <see cref="PublicBaseUrl"/> is not an absolute http(s) URL — connecting is then refused with 503.</summary>
    public Uri? RedirectUriFor(CloudStorageProviderKind kind)
    {
        if (!Uri.TryCreate(PublicBaseUrl?.Trim().TrimEnd('/'), UriKind.Absolute, out Uri? origin)) return null;
        if (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp) return null;
        return new Uri($"{origin.GetLeftPart(UriPartial.Authority)}/api/v1/cloud-storage/{CloudStorageProviders.Slug(kind)}/oauth/callback");
    }
}

public class OAuthClientOptions
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>A Google Cloud "Web application" OAuth client with the Drive API enabled.</summary>
public sealed class GoogleDriveOptions : OAuthClientOptions
{
    public string AuthorizeUrl { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    public string TokenUrl { get; set; } = "https://oauth2.googleapis.com/token";

    public string RevokeUrl { get; set; } = "https://oauth2.googleapis.com/revoke";

    public string ApiBaseUrl { get; set; } = "https://www.googleapis.com";
}

/// <summary>A Microsoft Entra app registration (multi-tenant + personal accounts unless <see cref="Tenant"/> narrows it).</summary>
public sealed class MicrosoftGraphOptions : OAuthClientOptions
{
    /// <summary><c>common</c> (work, school and personal accounts), <c>organizations</c>, <c>consumers</c>, or a tenant id.</summary>
    public string Tenant { get; set; } = "common";

    public string AuthorityBaseUrl { get; set; } = "https://login.microsoftonline.com";

    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
}

/// <summary>A Dropbox app (scoped access, "Full Dropbox"). <see cref="OAuthClientOptions.ClientId"/> is the app key.</summary>
public sealed class DropboxOptions : OAuthClientOptions
{
    public string AuthorizeUrl { get; set; } = "https://www.dropbox.com/oauth2/authorize";

    public string ApiBaseUrl { get; set; } = "https://api.dropboxapi.com";

    public string ContentBaseUrl { get; set; } = "https://content.dropboxapi.com";
}
