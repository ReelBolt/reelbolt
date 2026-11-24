using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DnsClient;
using DnsClient.Protocol;

namespace ReelBolt.Inference.Api.Services.Billing.FirstVideoAbuse;

/// <summary>Why the first-video grant was refused; the wire values are stored in <c>trial_claims.first_video_denied_reason</c>.</summary>
public static class FirstVideoDeniedReason
{
    public const string DisposableEmail = "disposable_email";
    public const string TorExit = "tor_exit";
    public const string VpnProxyHosting = "vpn_proxy_hosting";
}

/// <summary>Config keys of the first-video abuse checks (<c>Billing:FirstVideoGrant:Abuse:*</c>). The IP-info token is a secret: env only.</summary>
public static class FirstVideoAbuseKeys
{
    public const string Prefix = "Billing:FirstVideoGrant:Abuse:";
    public const string TorListUrl = Prefix + "TorListUrl";
    public const string TorRefreshHours = Prefix + "RefreshHours";
    public const string IpInfoToken = Prefix + "IpInfoToken";
    public const string IpInfoBaseUrl = Prefix + "IpInfoBaseUrl";
    public const string MxCheck = Prefix + "MxCheck";
    public const string DefaultTorListUrl = "https://check.torproject.org/torbulkexportlist";
    public const string DefaultIpInfoBaseUrl = "https://ipinfo.io";
}

/// <summary>Metrics of the first-video checks.</summary>
public static class FirstVideoAbuseDiagnostics
{
    public const string MeterName = "ReelBolt.Billing.FirstVideo";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>An IP-info lookup that failed (timeout, error, bad body); the grant went ahead (fail open). Not counted when no token is set.</summary>
    public static readonly Counter<long> IpInfoFailures = Meter.CreateCounter<long>(
        "reelbolt.billing.first_video_ipinfo_failures", "lookups", "IP-info privacy lookups that failed, so the first-video grant was not checked for VPN/proxy/hosting");
}

/// <summary>The vendored disposable-email domain list (an embedded copy of api/services/disposable_domains.txt).</summary>
public static class DisposableEmailDomains
{
    private static readonly Lazy<HashSet<string>> Set = new(Load);

    private static HashSet<string> Load()
    {
        HashSet<string> set = new(StringComparer.Ordinal);
        using Stream? stream = typeof(DisposableEmailDomains).Assembly.GetManifestResourceStream("FirstVideoAbuse.disposable_domains.txt");
        if (stream is null)
        {
            return set;
        }

        using StreamReader reader = new(stream);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim().ToLowerInvariant();
            if (line.Length > 0 && line[0] != '#')
            {
                set.Add(line);
            }
        }

        return set;
    }

    public static int Count => Set.Value.Count;

    /// <summary>True when the domain, or any parent domain of it, is listed (so <c>x.mailinator.com</c> matches <c>mailinator.com</c>).</summary>
    public static bool IsDisposable(string? email)
    {
        string domain = DomainOf(email);
        while (domain.Length > 0)
        {
            if (Set.Value.Contains(domain))
            {
                return true;
            }

            int dot = domain.IndexOf('.', StringComparison.Ordinal);
            domain = dot < 0 ? string.Empty : domain[(dot + 1)..];
        }

        return false;
    }

    public static string DomainOf(string? email)
    {
        string value = (email ?? string.Empty).Trim();
        int at = value.LastIndexOf('@');
        return at < 0 ? string.Empty : value[(at + 1)..].TrimEnd('.').ToLowerInvariant();
    }
}

/// <summary>Can a domain receive mail? <c>null</c> means unknown (any DNS failure), which is never a reason to refuse.</summary>
public interface IMailDomainResolver
{
    Task<bool?> CanReceiveMailAsync(string domain, CancellationToken ct);
}

/// <summary>MX lookup, then A/AAAA (RFC 5321 implicit MX). Only a definite "no such domain" or "no records" answers false.</summary>
public sealed class DnsMailDomainResolver : IMailDomainResolver
{
    private readonly LookupClient _client = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(2), Retries = 0, ThrowDnsErrors = false, UseCache = true });

    public async Task<bool?> CanReceiveMailAsync(string domain, CancellationToken ct)
    {
        try
        {
            IDnsQueryResponse mx = await _client.QueryAsync(domain, QueryType.MX, cancellationToken: ct).ConfigureAwait(false);
            if (mx.Answers.OfType<MxRecord>().Any())
            {
                return true;
            }

            if (mx.Header.ResponseCode is not (DnsHeaderResponseCode.NoError or DnsHeaderResponseCode.NotExistentDomain))
            {
                return null;
            }

            foreach (QueryType type in new[] { QueryType.A, QueryType.AAAA })
            {
                IDnsQueryResponse address = await _client.QueryAsync(domain, type, cancellationToken: ct).ConfigureAwait(false);
                if (address.Answers.OfType<AddressRecord>().Any())
                {
                    return true;
                }

                if (address.Header.ResponseCode is not (DnsHeaderResponseCode.NoError or DnsHeaderResponseCode.NotExistentDomain))
                {
                    return null;
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>The current Tor exit set (in memory; refreshed by <see cref="TorExitListRefresher"/>).</summary>
public interface ITorExitList
{
    bool Contains(string ip);
}

public sealed class TorExitList : ITorExitList
{
    private volatile HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _set.Count;

    public bool Contains(string ip) => _set.Contains(ip.Trim());

    /// <summary>Replaces the set; an empty list is ignored so a bad download never empties the old one.</summary>
    public void Replace(IEnumerable<string> ips)
    {
        HashSet<string> next = new(StringComparer.OrdinalIgnoreCase);
        foreach (string ip in ips)
        {
            string trimmed = ip.Trim();
            if (trimmed.Length > 0 && trimmed[0] != '#' && IPAddress.TryParse(trimmed, out _))
            {
                next.Add(trimmed);
            }
        }

        if (next.Count > 0)
        {
            _set = next;
        }
    }
}

/// <summary>Downloads the public Tor bulk exit list on a schedule. A failure keeps the old set and never throws.</summary>
public sealed class TorExitListRefresher(TorExitList list, IHttpClientFactory http, IConfiguration configuration, ILogger<TorExitListRefresher> logger) : BackgroundService
{
    public const string HttpClientName = "first-video-tor-list";

    /// <summary>The list is a few hundred KB; anything over this is refused rather than buffered.</summary>
    public const long MaxListBytes = 5 * 1024 * 1024;

    /// <summary>One refresh attempt; true when the list was replaced.</summary>
    public async Task<bool> RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            string url = configuration[FirstVideoAbuseKeys.TorListUrl] is { Length: > 0 } configured ? configured : FirstVideoAbuseKeys.DefaultTorListUrl;
            using HttpClient client = http.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = MaxListBytes;
            string body = await client.GetStringAsync(url, ct).ConfigureAwait(false);
            list.Replace(body.Split('\n'));
            return list.Count > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Tor exit list refresh failed ({Error}); keeping the previous list.", ex.GetType().Name);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Billing:Enabled") || !configuration.GetValue("Billing:FirstVideoGrant:Enabled", true))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            double hours = configuration.GetValue<double?>(FirstVideoAbuseKeys.TorRefreshHours) is double h and > 0 ? h : 6;
            try
            {
                await Task.Delay(TimeSpan.FromHours(hours), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>Is this IP a VPN, proxy, Tor or hosting address? <c>null</c> = unknown (fail open).</summary>
public interface IIpReputationClient
{
    Task<bool?> IsVpnProxyOrHostingAsync(string ip, CancellationToken ct);
}

/// <summary>
/// ipinfo.io privacy detection (<c>GET /{ip}/privacy</c>, bearer token from <c>IPINFO_TOKEN</c>). Refuses on vpn/proxy/tor/hosting,
/// never on <c>relay</c> alone. Cached per IP in memory (bounded); 2 s timeout; no token or any failure is unknown. The IP and token are never logged.
/// </summary>
public sealed class IpInfoPrivacyClient(HttpClient http, IConfiguration configuration, TimeProvider? time = null) : IIpReputationClient
{
    public const string HttpClientName = "first-video-ipinfo";
    public const int MaxCacheEntries = 10_000;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (bool Flagged, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool?> IsVpnProxyOrHostingAsync(string ip, CancellationToken ct)
    {
        string? token = configuration[FirstVideoAbuseKeys.IpInfoToken];
        if (string.IsNullOrWhiteSpace(token) || !IPAddress.TryParse(ip, out IPAddress? parsed))
        {
            return null;
        }

        string key = parsed.ToString();
        DateTime now = _time.GetUtcNow().UtcDateTime;
        if (_cache.TryGetValue(key, out (bool Flagged, DateTime At) hit) && now - hit.At < CacheLifetime)
        {
            return hit.Flagged;
        }

        try
        {
            string baseUrl = (configuration[FirstVideoAbuseKeys.IpInfoBaseUrl] is { Length: > 0 } b ? b : FirstVideoAbuseKeys.DefaultIpInfoBaseUrl).TrimEnd('/');
            using HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/{Uri.EscapeDataString(key)}/privacy");
            // The URL carries the sign-up IP: a trace span of this request keeps only the origin (S-08).
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
            {
                request.Options.Set(ReelBolt.Shared.Observability.ReelBoltTelemetry.OriginOnlyUrl, baseUri.GetLeftPart(UriPartial.Authority));
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using HttpResponseMessage response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            bool flagged = Flag(json.RootElement, "vpn") || Flag(json.RootElement, "proxy") || Flag(json.RootElement, "tor") || Flag(json.RootElement, "hosting");
            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }

            _cache[key] = (flagged, now);
            return flagged;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            FirstVideoAbuseDiagnostics.IpInfoFailures.Add(1);
            return null;
        }
    }

    private static bool Flag(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Decides whether a new account is refused the first-video grant. Never throws; every unknown fails open.</summary>
public interface IFirstVideoEligibility
{
    /// <summary>A <see cref="FirstVideoDeniedReason"/> value, or null when the grant may go ahead.</summary>
    Task<string?> DeniedReasonAsync(string email, string? signupIp, CancellationToken ct);
}

public sealed class FirstVideoEligibility(IMailDomainResolver dns, ITorExitList tor, IIpReputationClient ipReputation, IConfiguration configuration) : IFirstVideoEligibility
{
    public async Task<string?> DeniedReasonAsync(string email, string? signupIp, CancellationToken ct)
    {
        try
        {
            string domain = DisposableEmailDomains.DomainOf(email);
            if (DisposableEmailDomains.IsDisposable(email))
            {
                return FirstVideoDeniedReason.DisposableEmail;
            }

            if (domain.Length > 0 && configuration.GetValue(FirstVideoAbuseKeys.MxCheck, true) && await dns.CanReceiveMailAsync(domain, ct).ConfigureAwait(false) == false)
            {
                return FirstVideoDeniedReason.DisposableEmail;
            }

            // No stored sign-up IP: skip the IP checks (fail open).
            if (string.IsNullOrWhiteSpace(signupIp) || !IPAddress.TryParse(signupIp.Trim(), out IPAddress? ip))
            {
                return null;
            }

            string normalised = ip.ToString();
            if (tor.Contains(normalised))
            {
                return FirstVideoDeniedReason.TorExit;
            }

            return await ipReputation.IsVpnProxyOrHostingAsync(normalised, ct).ConfigureAwait(false) == true ? FirstVideoDeniedReason.VpnProxyHosting : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
