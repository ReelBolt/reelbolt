using System.ClientModel.Primitives;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// The one place a client factory decides which HTTP transport a provider row gets. An ORG-OWNED row's endpoint
/// passed only a write-time check (one DNS lookup), so every client built for it dials through
/// <see cref="GeneratedClipUrlGuard.CreateUserUrlHandler"/>: the address actually connected to must be public
/// (closes DNS rebinding), no redirect is followed and no ambient proxy is used. A platform-managed row keeps the
/// transport it always had, including the private addresses an operator may register for self-hosted kinds.
/// </summary>
public static class ProviderRowTransport
{
    /// <summary>A row a tenant configured, as opposed to a platform row.</summary>
    public static bool IsOrgOwned(Guid? organizationId, bool isPlatformManaged) =>
        organizationId is not null || !isPlatformManaged;

    /// <summary>
    /// The transport differs by ownership, so ownership is part of a factory's cache key: an org row must never be
    /// served a platform row's client that happens to share endpoint, model and key.
    /// </summary>
    public static string CacheKey(string cacheKey, bool orgOwned) => cacheKey + (orgOwned ? "|org" : "|platform");

    /// <summary>A bare client: public-only and redirect-free for an org row, the framework default for a platform row.</summary>
    public static HttpClient CreateHttpClient(bool orgOwned) =>
        orgOwned ? new HttpClient(GeneratedClipUrlGuard.CreateUserUrlHandler()) : new HttpClient();

    /// <summary>A handler: public-only and redirect-free for an org row, the framework default for a platform row.</summary>
    public static HttpMessageHandler CreateHandler(bool orgOwned) =>
        orgOwned ? GeneratedClipUrlGuard.CreateUserUrlHandler() : new HttpClientHandler();

    /// <summary>A handler that follows no redirect on any row, and is public-only for an org row.</summary>
    public static SocketsHttpHandler CreateNoRedirectHandler(bool orgOwned) =>
        orgOwned ? GeneratedClipUrlGuard.CreateUserUrlHandler() : new SocketsHttpHandler { AllowAutoRedirect = false };

    /// <summary>
    /// A public-only client for a vendor SDK to send through. The timeout is infinite because the SDK enforces its
    /// own (the 100-second HttpClient default would cut a long streamed completion off).
    /// </summary>
    public static HttpClient CreateSdkHttpClient() =>
        new(GeneratedClipUrlGuard.CreateUserUrlHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The System.ClientModel transport for an org row; null (the SDK default) for a platform row.</summary>
    public static PipelineTransport? CreateSdkTransport(bool orgOwned) =>
        orgOwned ? new HttpClientPipelineTransport(CreateSdkHttpClient()) : null;
}
