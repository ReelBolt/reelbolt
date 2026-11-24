using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.WorkflowEngine.Services.Fonts;

/// <summary>
/// The only code in the engine that talks to Google Fonts. Controls (docs/design/sandbox-customization.md section 3):
/// a FIXED host and the catalog's pinned commit (the URL is built here from catalog values, never from agent
/// text); HTTPS only; redirects are refused (a 3xx is an error, and the registered handler does not follow them);
/// a 10 MB cap enforced on the declared length and again while streaming; the SHA-256 must equal the catalog's
/// before the bytes are returned or cached; a hash-keyed shared cache so a font is downloaded once; a deadline.
/// </summary>
public sealed class GoogleFontFetcher : IGoogleFontFetcher
{
    /// <summary>The named/typed HTTP client registration key.</summary>
    public const string HttpClientName = "GoogleFonts";

    /// <summary>The one host the fetcher will contact.</summary>
    public const string Host = "raw.githubusercontent.com";

    /// <summary>The largest file accepted, in bytes.</summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly GoogleFontsOptions _options;
    private readonly IFontBlobCache? _cache;

    /// <summary>Creates the fetcher over a client that must NOT follow redirects (this class also refuses 3xx itself).</summary>
    public GoogleFontFetcher(HttpClient http, IOptions<GoogleFontsOptions> options, IFontBlobCache? cache = null)
    {
        _http = http;
        _options = options.Value;
        _cache = cache;
    }

    /// <inheritdoc />
    public bool Enabled => _options.Enabled;

    /// <summary>The URL of a catalogued file at the pinned commit. Throws if the catalog values would leave the fixed host.</summary>
    internal static Uri BuildUri(GoogleFontFamily family, GoogleFontFile file)
    {
        string path = string.Join('/', family.Directory.Split('/').Select(Uri.EscapeDataString)) + "/" + Uri.EscapeDataString(file.File);
        var uri = new Uri($"https://{Host}/google/fonts/{GoogleFontCatalog.Commit}/{path}");
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, Host, StringComparison.Ordinal) || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("The font URL left the allowed host.");
        return uri;
    }

    /// <inheritdoc />
    public async Task<GoogleFontFetchResult> FetchAsync(GoogleFontFamily family, GoogleFontFile file, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return GoogleFontFetchResult.Fail("Google Fonts fetching is disabled.");
        if (file.Size <= 0 || file.Size > MaxBytes)
            return GoogleFontFetchResult.Fail($"{file.File} is larger than the {MaxBytes / (1024 * 1024)} MB font limit.");

        try
        {
            if (_cache is not null)
            {
                byte[]? cached = await _cache.GetAsync(file.Sha256, cancellationToken);
                if (cached is not null && cached.Length <= MaxBytes && HashOf(cached) == file.Sha256)
                    return GoogleFontFetchResult.Success(cached);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cache that cannot answer is a cache miss.
        }

        Uri uri = BuildUri(family, file);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 300)));

        byte[] bytes;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            if ((int)response.StatusCode is >= 300 and < 400)
                return GoogleFontFetchResult.Fail("Google Fonts answered with a redirect, which is refused.");
            if (response.StatusCode != HttpStatusCode.OK)
                return GoogleFontFetchResult.Fail($"Google Fonts answered {(int)response.StatusCode} for {file.File}.");
            if (response.RequestMessage?.RequestUri is { } final && !string.Equals(final.Host, Host, StringComparison.Ordinal))
                return GoogleFontFetchResult.Fail("The response did not come from the allowed host.");
            if (response.Content.Headers.ContentLength is long declared && declared > MaxBytes)
                return GoogleFontFetchResult.Fail($"{file.File} is larger than the {MaxBytes / (1024 * 1024)} MB font limit.");

            await using Stream body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return GoogleFontFetchResult.Fail($"{file.File} is larger than the {MaxBytes / (1024 * 1024)} MB font limit.");
                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GoogleFontFetchResult.Fail("Google Fonts did not answer in time.");
        }
        catch (HttpRequestException)
        {
            return GoogleFontFetchResult.Fail("Google Fonts could not be reached.");
        }

        if (HashOf(bytes) != file.Sha256)
            return GoogleFontFetchResult.Fail($"{file.File} did not match its pinned SHA-256 and was discarded.");

        try
        {
            if (_cache is not null)
                await _cache.PutAsync(file.Sha256, bytes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Caching is an optimisation.
        }

        return GoogleFontFetchResult.Success(bytes);
    }

    private static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
