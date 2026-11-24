using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>
/// <c>SocialPublishing:*</c> (compose: <c>SocialPublishing__YouTube__ClientId</c> …). Everything is inert
/// until configured: a platform without a client id and secret is hidden and its connect endpoint answers
/// <c>503 provider_not_configured</c>. No credential is required to build or test.
/// </summary>
public sealed class SocialPublishingOptions
{
    public const string SectionName = "SocialPublishing";

    /// <summary>Global switch. Off hides every platform regardless of credentials.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The public origin of this deployment (<c>PUBLIC_BASE_URL</c>). The OAuth redirect URI registered with
    /// each platform is <c>{PublicBaseUrl}/api/v1/social/{slug}/oauth/callback</c> — see <see cref="RedirectUriFor"/>.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    public YouTubeSocialOptions YouTube { get; set; } = new();

    public LinkedInSocialOptions LinkedIn { get; set; } = new();

    /// <summary>Per-request timeout of the platforms' HTTP clients (video uploads included).</summary>
    public int HttpTimeoutSeconds { get; set; } = 300;

    /// <summary>The publish engine's knobs. Everything has a safe default.</summary>
    public SocialPublishEngineOptions Engine { get; set; } = new();

    public OAuthClientOptions For(SocialPlatformKind kind) => kind switch
    {
        SocialPlatformKind.YouTube => YouTube,
        SocialPlatformKind.LinkedIn => LinkedIn,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>One organization's share of a platform's daily budget (0 = no per-organization limit).</summary>
    public int MaxPerOrgPerDayFor(SocialPlatformKind kind) => kind switch
    {
        SocialPlatformKind.YouTube => YouTube.MaxPerOrgPerDay,
        _ => 0
    };

    public bool IsConfigured(SocialPlatformKind kind) => Enabled && For(kind).IsConfigured;

    /// <summary>Null when <see cref="PublicBaseUrl"/> is not an absolute http(s) URL; connecting is then refused with 503.</summary>
    public Uri? RedirectUriFor(SocialPlatformKind kind)
    {
        if (!Uri.TryCreate(PublicBaseUrl?.Trim().TrimEnd('/'), UriKind.Absolute, out Uri? origin)) return null;
        if (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp) return null;
        return new Uri($"{origin.GetLeftPart(UriPartial.Authority)}/api/v1/social/{SocialPlatforms.Slug(kind)}/oauth/callback");
    }
}

/// <summary>A Google Cloud "Web application" OAuth client with the YouTube Data API enabled.</summary>
public sealed class YouTubeSocialOptions : OAuthClientOptions
{
    public string AuthorizeUrl { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    public string TokenUrl { get; set; } = "https://oauth2.googleapis.com/token";

    public string RevokeUrl { get; set; } = "https://oauth2.googleapis.com/revoke";

    public string ApiBaseUrl { get; set; } = "https://www.googleapis.com";

    /// <summary>The Data API daily quota of the Google project (default 10,000 units). An upload costs 1,600.</summary>
    public int QuotaUnitsPerDay { get; set; } = 10000;

    /// <summary>Units per day set aside for reading statistics (1 each). Uploads may use <c>QuotaUnitsPerDay - this</c>: 6 a day by default.</summary>
    public int MetricsQuotaUnitsPerDay { get; set; } = 400;

    /// <summary>
    /// One organization's share of the platform's daily upload budget (<c>Engine:DailyBudgetYouTube</c>), so a single
    /// workspace cannot use up the shared quota for everyone. Default 2 of the default 6. 0 = no per-organization limit.
    /// </summary>
    public int MaxPerOrgPerDay { get; set; } = 2;
}

/// <summary>A LinkedIn developer app with "Share on LinkedIn" (<c>w_member_social</c>).</summary>
public sealed class LinkedInSocialOptions : OAuthClientOptions
{
    public string AuthorizeUrl { get; set; } = "https://www.linkedin.com/oauth/v2/authorization";

    public string TokenUrl { get; set; } = "https://www.linkedin.com/oauth/v2/accessToken";

    public string RevokeUrl { get; set; } = "https://www.linkedin.com/oauth/v2/revoke";

    public string ApiBaseUrl { get; set; } = "https://api.linkedin.com";

    /// <summary>The <c>LinkedIn-Version</c> header (YYYYMM). LinkedIn retires versions after about a year: verify before each release.</summary>
    public string ApiVersion { get; set; } = "202511";
}

/// <summary><c>SocialPublishing:Engine:*</c>: how the sweeper claims, retries and caps posts.</summary>
public sealed class SocialPublishEngineOptions
{
    /// <summary>Seconds between sweeps. 0 or less turns the sweeper off (posts stay scheduled).</summary>
    public int SweepIntervalSeconds { get; set; } = 30;

    /// <summary>How long a claim lives without renewal. Renewed every third of this while uploading.</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>A post more than this many minutes late (the service was down) fails as <c>missed_window</c> instead of posting at a surprising time.</summary>
    public int MissedWindowMinutes { get; set; } = 60;

    /// <summary>Automatic attempts for a retryable failure (rate limit, network).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Backoff before attempt n+1 is <c>BackoffSeconds * 4^(n-1)</c> (or the platform's Retry-After when larger).</summary>
    public int BackoffSeconds { get; set; } = 60;

    /// <summary>Posts uploading at once per platform, across all organizations.</summary>
    public int MaxConcurrentPerPlatform { get; set; } = 2;

    /// <summary>Posts uploading at once per connected account.</summary>
    public int MaxConcurrentPerAccount { get; set; } = 1;

    /// <summary>Most posts one organization may send (published, publishing or unknown) in a calendar month (UTC). 0 = no cap.</summary>
    public int MonthlyPostCapPerOrganization { get; set; } = 300;

    /// <summary>
    /// Most posts all organizations together may send to one platform per UTC day: our shared app quota. YouTube's
    /// default quota allows about 6 uploads a day (1,600 of 10,000 units each) until an extension is granted. 0 = no cap.
    /// </summary>
    public int DailyBudgetYouTube { get; set; } = 6;

    public int DailyBudgetLinkedIn { get; set; } = 150;

    public int DailyBudgetFor(SocialPlatformKind kind) => kind switch
    {
        SocialPlatformKind.YouTube => DailyBudgetYouTube,
        SocialPlatformKind.LinkedIn => DailyBudgetLinkedIn,
        _ => 0
    };

    /// <summary>Seconds between metrics sweeps. 0 or less turns the collector off.</summary>
    public int MetricsSweepIntervalSeconds { get; set; } = 900;

    /// <summary>Most posts one metrics sweep reads.</summary>
    public int MetricsBatchSize { get; set; } = 50;

    /// <summary>Most due posts one sweep claims.</summary>
    public int ClaimBatchSize { get; set; } = 10;
}
