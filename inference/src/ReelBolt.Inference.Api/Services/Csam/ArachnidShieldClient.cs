using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>The subset of Project Arachnid Shield this control uses.</summary>
public interface IArachnidShieldClient
{
    /// <summary><c>POST /v1/pdq</c> with base64-of-32-bytes hashes.</summary>
    Task<ArachnidScanResult> ScanPdqAsync(string base64Hash, CancellationToken ct);

    /// <summary><c>POST /v1/media</c> with raw bytes and their content type.</summary>
    Task<ArachnidScanResult> ScanMediaAsync(Stream content, string contentType, CancellationToken ct);

    /// <summary><c>GET /v1/version</c>; null when the provider does not answer one.</summary>
    Task<string?> GetDatabaseVersionAsync(CancellationToken ct);
}

/// <summary>
/// Classification and match details. Strings, because the provider's vocabulary is its own and an
/// unrecognised value must not become an exception: only <c>csam</c> and
/// <c>harmful-abusive-material</c> are matches, anything else is not.
/// </summary>
public sealed record ArachnidScanResult(
    string Classification,
    string? MatchType,
    int NearMatchCount,
    string? Sha256Hex);

/// <summary>A 2xx answer whose body is not the documented shape. The matcher fails open on it.</summary>
public sealed class ArachnidProtocolException(string message) : Exception(message);

/// <summary>
/// Project Arachnid Shield over a named <see cref="HttpClient"/>. Relative paths are resolved
/// against the configured base URL, so this is the only host the control can reach. Nothing here
/// reads or logs a response body except the fields the audit needs; no token is ever logged.
/// </summary>
public sealed class ArachnidShieldClient : IArachnidShieldClient
{
    public const string HttpClientName = "ArachnidShield";

    private readonly HttpClient _http;
    private readonly CsamOptions _options;

    public ArachnidShieldClient(HttpClient http, IOptions<CsamOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<ArachnidScanResult> ScanPdqAsync(string base64Hash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Hash);

        string body = JsonSerializer.Serialize(new { hashes = new[] { base64Hash } });
        using HttpRequestMessage request = new(HttpMethod.Post, "v1/pdq")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddAuthorization(request);

        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument document = await ParseAsync(stream, ct);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArachnidProtocolException("The PDQ response was not an object.");

        if (!root.TryGetProperty("scanned_hashes", out JsonElement scanned)
            || scanned.ValueKind != JsonValueKind.Object
            || !scanned.TryGetProperty(base64Hash, out JsonElement entry)
            || entry.ValueKind != JsonValueKind.Object)
        {
            throw new ArachnidProtocolException("The PDQ response did not contain the scanned hash.");
        }

        return ReadScan(entry);
    }

    public async Task<ArachnidScanResult> ScanMediaAsync(Stream content, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        using HttpRequestMessage request = new(HttpMethod.Post, "v1/media");
        StreamContent body = new(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content = body;
        AddAuthorization(request);

        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument document = await ParseAsync(stream, ct);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArachnidProtocolException("The media response was not an object.");

        ArachnidScanResult result = ReadScan(root);
        string? sha256 = root.TryGetProperty("sha256_hex", out JsonElement sha) && sha.ValueKind == JsonValueKind.String
            ? sha.GetString()
            : null;
        return result with { Sha256Hex = sha256 };
    }

    public async Task<string?> GetDatabaseVersionAsync(CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "v1/version");
        AddAuthorization(request);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument document = await ParseAsync(stream, ct);
        return document.RootElement.TryGetProperty("last_updated_at", out JsonElement updated)
               && updated.ValueKind == JsonValueKind.String
            ? updated.GetString()
            : null;
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        // Shield's spec defines no security scheme; the account's username/password are sent as
        // HTTP Basic auth. Never in the URL or query string, and never logged.
        if (string.IsNullOrWhiteSpace(_options.Arachnid.Username)
            || string.IsNullOrWhiteSpace(_options.Arachnid.Password))
        {
            return;
        }

        string header = string.IsNullOrWhiteSpace(_options.Arachnid.AuthHeader) ? "Authorization" : _options.Arachnid.AuthHeader;
        string credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.Arachnid.Username}:{_options.Arachnid.Password}"));
        request.Headers.TryAddWithoutValidation(header, $"Basic {credentials}");
    }

    private static ArachnidScanResult ReadScan(JsonElement entry)
    {
        string? classification = entry.TryGetProperty("classification", out JsonElement c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(classification))
            throw new ArachnidProtocolException("The scan response had no classification.");

        string? matchType = entry.TryGetProperty("match_type", out JsonElement m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        int nearMatches = entry.TryGetProperty("near_match_details", out JsonElement details) && details.ValueKind == JsonValueKind.Array
            ? details.GetArrayLength()
            : 0;
        string? sha256 = entry.TryGetProperty("sha256_hex", out JsonElement sha) && sha.ValueKind == JsonValueKind.String
            ? sha.GetString()
            : null;

        return new ArachnidScanResult(classification, matchType, nearMatches, sha256);
    }

    private static async Task<JsonDocument> ParseAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new ArachnidProtocolException($"The provider answered malformed JSON: {ex.Message}");
        }
    }
}
