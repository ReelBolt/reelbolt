using System.Net;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>A token exchange or refresh answer. The refresh token is null when the platform issues none.</summary>
public sealed record SocialTokenSet(string AccessToken, string? RefreshToken, DateTime? AccessTokenExpiresAt, string? Scope);

/// <summary>The account a fresh access token belongs to. <see cref="AccountId"/> is the platform's stable id, never the email.</summary>
public sealed record SocialAccountInfo(string AccountId, string? DisplayName, string? AvatarUrl);

/// <summary>What to publish. The video is opened lazily so it streams from the object store, never to disk.</summary>
public sealed record SocialPublishRequest(
    string? Title,
    string? Body,
    IReadOnlyList<string> Hashtags,
    long ContentLength,
    string ContentType,
    Func<CancellationToken, Task<Stream>> OpenVideoAsync,
    IReadOnlyDictionary<string, string> Options);

/// <summary>The published post's identity on the platform.</summary>
public sealed record SocialPublishResult(string RemotePostId, string? RemotePostUrl);

/// <summary>One reading of a post's numbers. A null count means the platform does not provide it.</summary>
public sealed record SocialMetricsSnapshot(long? Views, long? Likes, long? Comments, long? Shares, long? Saves, string Source);

/// <summary>
/// One social platform behind a uniform face (docs/social-publishing.md): connect URL and scopes, code
/// exchange, refresh, revoke, publish, read metrics. A concrete adapter is stateless and talks to the
/// platform over an injectable <see cref="HttpClient"/>, so tests replay recorded fixtures. An aggregator
/// would be one more adapter; nothing else changes.
/// </summary>
public interface ISocialPlatform
{
    SocialPlatformKind Kind { get; }

    /// <summary>True when this deployment holds the platform's client id and secret and publishing is enabled.</summary>
    bool IsConfigured { get; }

    /// <summary>The scopes the connect URL requests.</summary>
    IReadOnlyList<string> Scopes { get; }

    Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri);

    Task<SocialTokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct);

    Task<SocialTokenSet> RefreshAsync(string refreshToken, CancellationToken ct);

    Task<SocialAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct);

    /// <summary>Revokes the grant at the platform. Callers treat a failure as best effort.</summary>
    Task RevokeAsync(SocialTokenSet tokens, CancellationToken ct);

    Task<SocialPublishResult> PublishAsync(string accessToken, SocialPublishRequest request, CancellationToken ct);

    Task<SocialMetricsSnapshot> ReadMetricsAsync(string accessToken, string remotePostId, CancellationToken ct);
}

public enum SocialErrorKind
{
    /// <summary>The access token is no longer accepted: refresh once, or the person must reconnect.</summary>
    Unauthorized,
    Forbidden,
    NotFound,
    RateLimited,
    ServerError,
    NotConfigured,
    InvalidResponse
}

/// <summary>A platform call failed. The message never carries a token or a raw platform body.</summary>
public sealed class SocialApiException : Exception
{
    public SocialApiException(string message, SocialErrorKind kind, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null, bool postMayExist = false)
        : base(message)
    {
        PostMayExist = postMayExist;
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public SocialErrorKind Kind { get; }

    /// <summary>
    /// True when the failure came after the platform may already have created the post (the last request was sent and
    /// its answer lost). The engine then records the outcome as <c>unknown</c> and never retries on its own. An adapter
    /// leaves it false only for failures that happened before the post could exist.
    /// </summary>
    public bool PostMayExist { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>The registered <see cref="ISocialPlatform"/> implementations, by kind.</summary>
public interface ISocialPlatformRegistry
{
    ISocialPlatform? Get(SocialPlatformKind kind);

    /// <summary>The registered platforms whose credentials are configured, in <see cref="SocialPlatforms.All"/> order.</summary>
    IReadOnlyList<ISocialPlatform> Configured { get; }
}

/// <inheritdoc />
public sealed class SocialPlatformRegistry : ISocialPlatformRegistry
{
    private readonly Dictionary<SocialPlatformKind, ISocialPlatform> _byKind = [];

    public SocialPlatformRegistry(IEnumerable<ISocialPlatform> platforms)
    {
        foreach (ISocialPlatform platform in platforms) _byKind.TryAdd(platform.Kind, platform);
    }

    public ISocialPlatform? Get(SocialPlatformKind kind) => _byKind.TryGetValue(kind, out ISocialPlatform? p) ? p : null;

    public IReadOnlyList<ISocialPlatform> Configured =>
        SocialPlatforms.All
            .Select(Get)
            .Where(p => p is { IsConfigured: true })
            .Select(p => p!)
            .ToList();
}
