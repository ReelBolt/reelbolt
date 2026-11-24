using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// DeepSeek Search API: <c>POST {endpoint}/v1/tools/search</c> with a Bearer key and
/// <c>{query, count, need_content:false}</c>. Billed per non-empty call, so SearchUnits is 1 only when the raw results array has at least
/// one result came back.
/// </summary>
internal sealed class DeepSeekWebSearchClient : WebSearchClientBase
{
    public const string DefaultEndpoint = "https://api.deepseek.com";
    internal const int MaxQueryChars = 128;

    private readonly string _endpoint;
    private readonly string _apiKey;

    public DeepSeekWebSearchClient(ResolvedWebSearchProvider provider, HttpClient http, bool ownsClient)
        : base(http, provider.TimeoutSeconds, ownsClient)
    {
        _apiKey = ResolveKey(provider.ApiKey, "DeepSeek", provider.IsPlatformManaged, "DEEPSEEK_API_KEY");
        _endpoint = (string.IsNullOrWhiteSpace(provider.Endpoint) ? DefaultEndpoint : provider.Endpoint.Trim()).TrimEnd('/');
    }

    protected override string ProviderLabel => "DeepSeek";

    protected override HttpRequestMessage BuildRequest(string query, int maxResults)
    {
        string capped = query.Length > MaxQueryChars ? query[..MaxQueryChars] : query;
        HttpRequestMessage request = new(HttpMethod.Post, _endpoint + "/v1/tools/search")
        {
            Content = JsonBody(new Dictionary<string, object>
            {
                ["query"] = capped,
                ["count"] = Math.Min(maxResults, WebSearchLimits.MaxResults),
                ["need_content"] = false
            })
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        return request;
    }

    protected override WebSearchResponse Parse(string json, int maxResults)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement? results = Get(root, "results") ?? (Get(root, "data") is { } d ? Get(d, "results") : null);
        if (results is null || results.Value.ValueKind != JsonValueKind.Array)
            throw new JsonException("no results array");

        List<WebSearchResult> list = [];
        foreach (JsonElement item in results.Value.EnumerateArray())
        {
            string? url = CleanUrl(Str(item, "url"));
            if (url is null) continue;

            string snippet = Sanitize(Str(item, "snippet"), WebSearchLimits.MaxSnippetChars);
            string summary = Sanitize(Str(item, "summary"), WebSearchLimits.MaxSnippetChars);
            if (snippet.Length == 0) snippet = summary;
            else if (summary.Length > 0 && summary != snippet)
                snippet = Sanitize(snippet + " " + summary, WebSearchLimits.MaxSnippetChars);

            string? date = Sanitize(Str(item, "date") ?? Str(item, "published_date"), 40);
            list.Add(new WebSearchResult(
                Sanitize(Str(item, "title"), WebSearchLimits.MaxTitleChars), url, snippet,
                string.IsNullOrEmpty(date) ? null : date));
            if (list.Count >= maxResults) break;
        }

        return new WebSearchResponse("deepseek", list, null, [], results.Value.GetArrayLength() > 0 ? 1 : 0, null, false);
    }
}
