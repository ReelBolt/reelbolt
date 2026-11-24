using System.Text;
using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>The production factory: one OpenAI-compatible rerank client per resolved provider.</summary>
public sealed class RerankClientFactory : IRerankClientFactory
{
    public IRerankClient Create(ResolvedRerankProvider provider, int timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // One handler per provider, never a shared static client. A bring-your-own (org-owned) row is user-typed
        // input: it connects only to public addresses (the check runs on the address actually connected to, which
        // closes DNS rebinding) and follows no redirect. An operator-configured platform row (a self-hosted TEI on
        // the private network) keeps the normal handler, still without redirects, proxy or cookies.
        SocketsHttpHandler handler = provider.IsPlatformManaged
            ? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }
            : GeneratedClipUrlGuard.CreateUserUrlHandler();
        HttpClient http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new OpenAICompatibleRerankClient(provider.Endpoint, provider.ApiKey, provider.ModelName, timeoutSeconds, http, ownsClient: true);
    }
}

/// <summary>
/// The raw rerank client: plain <c>HttpClient</c> + <c>System.Text.Json</c>, <c>POST {base}/rerank</c> with
/// <c>{query, texts[], top_n}</c> (plus <c>model</c> when one is configured, which hosted rerankers require and
/// text-embeddings-inference ignores). Accepts both response shapes in the wild: the bare
/// <c>[{index, score}]</c> array of Hugging Face text-embeddings-inference and the
/// <c>{results:[{index, relevance_score}]}</c> object of Cohere/Jina/Voyage-style hosts. The one exception is the
/// exact host <c>api.cloudflare.com</c> (Workers AI native): <c>POST .../ai/run/{model}</c> with
/// <c>{query, contexts[{text}], top_k}</c>, scored as <c>{id, score}</c>.
/// </summary>
/// <remarks>
/// Fail-soft by contract (see <see cref="IRerankClient"/>). Nothing from the vendor body is ever logged or put in
/// an exception: the answer is a score list or <c>null</c>. A blank key sends no <c>Authorization</c> header, like
/// every other OpenAICompatible client. The caller supplies the <see cref="HttpClient"/> (there is no shared
/// fallback): <see cref="RerankClientFactory"/> builds one per provider, public-only for org-owned rows.
/// </remarks>
public sealed class OpenAICompatibleRerankClient : IRerankClient, IDisposable
{
    /// <summary>Cap on the response body read; 20 scores are a few hundred bytes.</summary>
    private const int MaxResponseBytes = 64 * 1024;

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private readonly bool _ownsClient;
    private readonly RerankStyle _style;

    /// <summary>True once a 2xx response body was received, whether or not it parsed (the vendor was reached).</summary>
    public bool ReachedVendor { get; private set; }

    public OpenAICompatibleRerankClient(
        string endpoint,
        string apiKey,
        string? model,
        int timeoutSeconds,
        HttpClient httpClient,
        bool ownsClient = false)
    {
        _ownsClient = ownsClient;
        _endpoint = (endpoint ?? string.Empty).TrimEnd('/');
        _apiKey = apiKey ?? string.Empty;
        _model = model ?? string.Empty;
        _timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : RerankLimits.DefaultTimeoutSeconds);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _style = RerankStyles.For(_endpoint);
    }

    public async Task<IReadOnlyList<RerankScore>?> RerankAsync(
        string query, IReadOnlyList<string> texts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);

        string cappedQuery = Truncate((query ?? string.Empty).Trim(), RerankLimits.MaxQueryChars);
        string[] sent = [.. texts.Take(RerankLimits.MaxCandidates)
            .Select(t => Truncate(t ?? string.Empty, RerankLimits.MaxCandidateChars))];
        if (cappedQuery.Length == 0 || sent.Length == 0) return null;

        // TEI and self-hosted servers take "texts"; hosted Cohere/Jina/Voyage take "documents" (Voyage names
        // the count "top_k"). The style is derived from the endpoint host, never from tenant input.
        Uri? requestUri;
        Dictionary<string, object> body = new() { ["query"] = cappedQuery };
        if (_style == RerankStyle.WorkersAi)
        {
            // Cloudflare Workers AI: the model is in the URL, never the body; contexts[].text must be non-empty,
            // so an empty candidate is sent as a single space (indexes never shift).
            requestUri = WorkersAiUri(_endpoint, _model);
            if (requestUri is null) return null;
            body["contexts"] = sent.Select(t => new Dictionary<string, string> { ["text"] = t.Length == 0 ? " " : t }).ToArray();
            body["top_k"] = sent.Length;
        }
        else
        {
            requestUri = null;
            body[_style == RerankStyle.Tei ? "texts" : "documents"] = sent;
            body[_style == RerankStyle.Voyage ? "top_k" : "top_n"] = sent.Length;
            if (_model.Length > 0) body["model"] = _model;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, requestUri ?? new Uri(_endpoint + "/rerank"))
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            if (_apiKey.Length > 0)
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string json = await BoundedBody.ReadAsync(response.Content, MaxResponseBytes, timeout.Token)
                .ConfigureAwait(false);
            ReachedVendor = true;
            return Parse(json, sent.Length, _style == RerankStyle.WorkersAi);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (UriFormatException)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Sockets.SocketException or IOException)
        {
            // A refused (non-public) address or a dropped connection: fail soft, never into the caller.
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }

    /// <summary>
    /// Parses either response shape into exactly <paramref name="expected"/> scores, best first, or <c>null</c> when
    /// the body is malformed, an index is out of range or repeated, a score is not a finite number, or the scores do
    /// not cover every candidate (fewer scores than candidates is a fall back, never a silent drop).
    /// </summary>
    internal static IReadOnlyList<RerankScore>? Parse(string json, int expected) => Parse(json, expected, false);

    private static readonly System.Text.RegularExpressions.Regex WorkersAiAccount =
        new(@"^[0-9A-Za-z]+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly System.Text.RegularExpressions.Regex WorkersAiModel =
        new(@"^@cf/[a-z0-9][a-z0-9._-]*/[a-z0-9][a-z0-9._-]*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// <c>.../client/v4/accounts/{acct}/ai/v1</c> becomes <c>.../ai/run/{model}</c>, or null when the path is not
    /// exactly that shape, the account is empty, or the model is not a strict <c>@cf/vendor/name</c> code.
    /// </summary>
    internal static Uri? WorkersAiUri(string endpoint, string model)
    {
        if (!Uri.TryCreate((endpoint ?? string.Empty).TrimEnd('/'), UriKind.Absolute, out Uri? uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        string[] seg = uri.AbsolutePath.Split('/');
        // "", client, v4, accounts, <acct>, ai, v1
        if (seg.Length != 7 || seg[0].Length != 0 || seg[1] != "client" || seg[2] != "v4" || seg[3] != "accounts"
            || !WorkersAiAccount.IsMatch(seg[4]) || seg[5] != "ai" || seg[6] != "v1") return null;
        if (!WorkersAiModel.IsMatch(model ?? string.Empty)) return null;
        return new Uri($"https://{uri.Authority}/client/v4/accounts/{seg[4]}/ai/run/{model}");
    }

    internal static IReadOnlyList<RerankScore>? Parse(string json, int expected, bool workersAi)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement array = doc.RootElement;
            if (workersAi)
            {
                // REST envelope {result:{response:[...]},success} or the bare {response:[...]} output.
                JsonElement root = array;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (root.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.False) return null;
                JsonElement holder = root;
                if (root.TryGetProperty("result", out JsonElement result))
                {
                    if (result.ValueKind != JsonValueKind.Object) return null;
                    holder = result;
                }

                if (!holder.TryGetProperty("response", out array)) return null;
            }
            else if (array.ValueKind == JsonValueKind.Object)
            {
                JsonElement root = array;
                if (!root.TryGetProperty("results", out array) && !root.TryGetProperty("data", out array)) return null;
            }

            if (array.ValueKind != JsonValueKind.Array) return null;

            List<RerankScore> scores = new(expected);
            HashSet<int> seen = [];
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                if (!item.TryGetProperty(workersAi ? "id" : "index", out JsonElement index) || index.ValueKind != JsonValueKind.Number
                    || !index.TryGetInt32(out int i) || i < 0 || i >= expected || !seen.Add(i))
                    return null;

                JsonElement score = default;
                bool found = (item.TryGetProperty("score", out score) || item.TryGetProperty("relevance_score", out score))
                             && score.ValueKind == JsonValueKind.Number;
                if (!found || !score.TryGetDouble(out double s) || double.IsNaN(s) || double.IsInfinity(s)) return null;

                scores.Add(new RerankScore(i, s));
            }

            if (scores.Count != expected) return null;

            // Best first; ties keep the caller's order so the result is deterministic.
            return [.. scores.OrderByDescending(x => x.Score).ThenBy(x => x.Index)];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}

/// <summary>The request-body dialect of a rerank endpoint.</summary>
internal enum RerankStyle
{
    /// <summary>Hugging Face text-embeddings-inference and self-hosted servers: <c>texts</c>, <c>top_n</c>.</summary>
    Tei,

    /// <summary>Cohere and Jina: <c>documents</c>, <c>top_n</c>.</summary>
    Documents,

    /// <summary>Voyage: <c>documents</c>, <c>top_k</c>.</summary>
    Voyage,

    /// <summary>Cloudflare Workers AI native: <c>POST .../ai/run/{model}</c> with <c>query</c>, <c>contexts[{text}]</c>, <c>top_k</c>; scores are <c>{id, score}</c>.</summary>
    WorkersAi
}

internal static class RerankStyles
{
    /// <summary>Chosen from a small allowlist of known hosted hosts; everything else is the TEI shape.</summary>
    public static RerankStyle For(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)) return RerankStyle.Tei;
        string host = uri.Host.ToLowerInvariant();
        if (host is "api.cloudflare.com") return RerankStyle.WorkersAi;
        if (host is "api.voyageai.com") return RerankStyle.Voyage;
        if (host is "api.cohere.com" or "api.cohere.ai" or "api.jina.ai") return RerankStyle.Documents;
        return RerankStyle.Tei;
    }
}
