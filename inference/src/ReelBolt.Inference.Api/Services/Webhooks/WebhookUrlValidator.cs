using System.Net;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Webhooks;

/// <summary>Resolves a host to its addresses (a seam so tests need no DNS).</summary>
public interface IWebhookHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsWebhookHostResolver : IWebhookHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>
/// The create/update check of an endpoint URL: at most 2048 characters, https, no credentials, port 443 (the
/// default) or 8443 only, and a public host (<see cref="GeneratedClipUrlGuard.EnsureAllowed"/>) that resolves to
/// public addresses only. The delivery handler
/// (<see cref="GeneratedClipUrlGuard.CreateUserUrlHandler"/>) refuses every non-public address when it connects,
/// which is what actually protects the internal network. The check at create time is a courtesy (an early, clear
/// 400); the connect-time check is the guard.
/// </summary>
public sealed class WebhookUrlValidator(IWebhookHostResolver resolver)
{
    public const int MaxLength = 2048;
    public const string NotPublicCode = "webhook_url_not_public";

    /// <summary>
    /// The one answer for a host that does not resolve AND for one that resolves to a non-public address: telling
    /// the two apart would let a caller probe which internal names exist.
    /// </summary>
    public const string HostNotPublicMessage = "The URL host must resolve to public addresses only.";

    /// <summary>443 is https's default, so <c>https://host/x</c> and <c>https://host:443/x</c> both pass.</summary>
    public static bool IsAllowedPort(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Port is 443 or 8443;

    /// <summary>Null when the URL is acceptable, otherwise a short reason.</summary>
    public async Task<string?> ValidateAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength) return $"The URL must be 1 to {MaxLength} characters.";

        try
        {
            GeneratedClipUrlGuard.EnsureAllowed(url);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Replace("Download URL", "The URL", StringComparison.Ordinal);
        }

        if (!IsAllowedPort(url)) return "The URL must use port 443 or 8443.";

        string host = new Uri(url).Host.Trim('[', ']');
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver.ResolveAsync(host, ct);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
            {
                return HostNotPublicMessage;
            }
        }

        if (addresses.Length == 0) return HostNotPublicMessage;
        try
        {
            GeneratedClipUrlGuard.RequirePublic(addresses);
        }
        catch (NonPublicAddressException)
        {
            return HostNotPublicMessage;
        }

        return null;
    }
}
