using System.Net;
using System.Net.Sockets;
using System.Text;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>The outcome of one Figma variables fetch. Structural, so the route maps it without strings.</summary>
public enum FigmaFetchStatus
{
    /// <summary>The body was read entirely inside the 2 MiB cap.</summary>
    Ok,

    /// <summary>Figma answered 403 — variables need an Enterprise plan.</summary>
    Forbidden,

    /// <summary>Figma answered 404 — the file key does not exist or is not visible to the token.</summary>
    NotFound,

    /// <summary>Figma answered a 3xx. Redirects are NEVER followed.</summary>
    Redirected,

    /// <summary>The body exceeded the 2 MiB cap, or announced that it would.</summary>
    TooLarge,

    /// <summary>The request never completed (DNS, connect, TLS, timeout, cancellation of the socket).</summary>
    TransportFailed,

    /// <summary>Figma answered a non-2xx status other than the ones above.</summary>
    UpstreamError,
}

/// <summary>One fetch result: the status, the body when readable and the facts a caller may log safely.</summary>
public sealed record FigmaFetchResult(FigmaFetchStatus Status, string? Body, int StatusCode, string? Location);

/// <summary>
/// The SSRF-safe one-shot client for Figma's variables REST endpoint. The base address is fixed to
/// <c>https://api.figma.com</c> — a caller can only supply a file key, never a host — and the host
/// allowlist is checked before every call.
///
/// <para>
/// <see cref="GeneratedClipUrlGuard.CreateDownloadClient"/> turns automatic redirects ON
/// (<c>MaxAutomaticRedirections = 3</c>), and this contract forbids following any 3xx, so the same
/// IP-level SSRF guard is rebuilt here with <c>AllowAutoRedirect = false</c>: the connect callback
/// resolves the host and refuses any non-public address, covering DNS rebinding and every hop.
/// <see cref="GeneratedClipUrlGuard.EnsureAllowed"/> still runs over the final URL.
/// </para>
///
/// <para>
/// The access token is written to the <c>X-Figma-Token</c> request header and nowhere else: this type
/// keeps no field for it, logs nothing, throws nothing that carries it and never places it in the
/// returned body. <see cref="FigmaFetchResult.Location"/> carries only a redirect target.
/// </para>
/// </summary>
public sealed class FigmaVariablesClient : IDisposable
{
    /// <summary>The only host this client may dial.</summary>
    public const string AllowedHost = "api.figma.com";

    /// <summary>
    /// The hard response cap, read bounded so a hostile or endless body cannot OOM the API. It is the
    /// importer's own input limit: a larger body could only be refused by the importer afterwards.
    /// </summary>
    public const int MaxResponseBytes = ImportLimits.JsonMaxBytes;

    /// <summary>The request timeout the contract fixes at 20 seconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private static readonly Uri BaseAddress = new("https://api.figma.com/");

    private readonly HttpClient _http;
    private readonly TimeSpan _deadline;

    /// <summary>Creates the production client: fixed base address, no redirects, the SSRF connect guard.</summary>
    public FigmaVariablesClient() : this(CreateHandler(), DefaultTimeout)
    {
    }

    /// <summary>Creates a client over a caller-supplied handler (the tests' fake transport).</summary>
    public FigmaVariablesClient(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _deadline = timeout ?? DefaultTimeout;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = BaseAddress,
            Timeout = _deadline,
        };
    }

    /// <summary>
    /// Fetches <c>GET https://api.figma.com/v1/files/{fileKey}/variables/local</c>. Never throws for a
    /// remote or transport failure; the caller cancels through <paramref name="ct"/>.
    /// </summary>
    public async Task<FigmaFetchResult> FetchVariablesAsync(string fileKey, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fileKey))
            return new FigmaFetchResult(FigmaFetchStatus.TransportFailed, null, 0, null);

        Uri uri;
        try
        {
            uri = new Uri(BaseAddress, "v1/files/" + Uri.EscapeDataString(fileKey) + "/variables/local");
            GeneratedClipUrlGuard.EnsureAllowed(uri.ToString());
        }
        catch (Exception)
        {
            return new FigmaFetchResult(FigmaFetchStatus.TransportFailed, null, 0, null);
        }

        if (!IsAllowedHost(uri))
            return new FigmaFetchResult(FigmaFetchStatus.TransportFailed, null, 0, null);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-Figma-Token", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new FigmaFetchResult(FigmaFetchStatus.TransportFailed, null, 0, null);
        }

        using (response)
        {
            int status = (int)response.StatusCode;

            // No redirect is ever followed: a 3xx is terminal and reported as such.
            if (status is >= 300 and < 400)
                return new FigmaFetchResult(FigmaFetchStatus.Redirected, null, status, SafeLocation(response));

            if (status == StatusCodes.Status403Forbidden)
                return new FigmaFetchResult(FigmaFetchStatus.Forbidden, null, status, null);

            if (status == StatusCodes.Status404NotFound)
                return new FigmaFetchResult(FigmaFetchStatus.NotFound, null, status, null);

            if (!response.IsSuccessStatusCode)
                return new FigmaFetchResult(FigmaFetchStatus.UpstreamError, null, status, null);

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                return new FigmaFetchResult(FigmaFetchStatus.TooLarge, null, status, null);

            // HttpClient.Timeout stops at the headers (ResponseHeadersRead): the body gets its own deadline.
            using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bodyDeadline.CancelAfter(_deadline);

            string? body;
            bool tooLarge;
            try
            {
                (body, tooLarge) = await ReadBoundedAsync(response.Content, bodyDeadline.Token)
                    .WaitAsync(bodyDeadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new FigmaFetchResult(FigmaFetchStatus.TransportFailed, null, 0, null);
            }
            catch (IOException)
            {
                return new FigmaFetchResult(FigmaFetchStatus.TransportFailed, null, 0, null);
            }

            return tooLarge
                ? new FigmaFetchResult(FigmaFetchStatus.TooLarge, null, status, null)
                : new FigmaFetchResult(FigmaFetchStatus.Ok, body, status, null);
        }
    }

    /// <summary>True when <paramref name="uri"/> names exactly <see cref="AllowedHost"/> over HTTPS with no credentials.</summary>
    public static bool IsAllowedHost(Uri uri) =>
        uri is not null
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();

    private static string? SafeLocation(HttpResponseMessage response)
    {
        Uri? location = response.Headers.Location;
        if (location is null)
            return null;

        // Report only the absolute form of the target; a relative Location is resolved against the
        // fixed base address and never dialled.
        return location.IsAbsoluteUri ? location.ToString() : BaseAddress + location.ToString();
    }

    private static async Task<(string? Body, bool TooLarge)> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        long total = 0;

        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
            if (total > MaxResponseBytes)
                return (null, true);

            buffer.Write(chunk, 0, read);
        }

        return (Encoding.UTF8.GetString(buffer.ToArray()), false);
    }

    /// <summary>
    /// The same SSRF connect guard <see cref="GeneratedClipUrlGuard.CreateDownloadClient"/> installs,
    /// built directly because that helper forces automatic redirects on.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectCallback = async (context, ct) =>
        {
            string host = context.DnsEndPoint.Host;
            IPAddress[] candidates = IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

            IPAddress[] addresses = GeneratedClipUrlGuard.RequirePublic(candidates);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
