using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// OpenAI Responses API with the built-in <c>web_search</c> tool: <c>POST https://api.openai.com/v1/responses</c>,
/// <c>tool_choice: required</c>, <c>include: web_search_call.action.sources</c>, <c>store: false</c>. The results are
/// the deduplicated cited and consulted URLs; the answer text is kept (capped) as <c>Answer</c>.
/// </summary>
internal sealed class OpenAIWebSearchClient : WebSearchClientBase
{
    public const string RequiredHost = "api.openai.com";
    internal const int MaxQueryChars = 500;
    private const int MaxCitationSnippet = 300;

    private readonly string _apiKey;
    private readonly string _model;

    public OpenAIWebSearchClient(ResolvedWebSearchProvider provider, HttpClient http, bool ownsClient)
        : base(http, provider.TimeoutSeconds, ownsClient)
    {
        _apiKey = ResolveKey(provider.ApiKey, "OpenAI", provider.IsPlatformManaged);
        _model = provider.ModelName;
    }

    /// <summary>True when the endpoint is empty (default) or an https URL on exactly api.openai.com.</summary>
    internal static bool IsOpenAiEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return true;
        return Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && string.Equals(uri.Host, RequiredHost, StringComparison.OrdinalIgnoreCase);
    }

    protected override string ProviderLabel => "OpenAI";

    protected override HttpRequestMessage BuildRequest(string query, int maxResults)
    {
        string capped = query.Length > MaxQueryChars ? query[..MaxQueryChars] : query;
        HttpRequestMessage request = new(HttpMethod.Post, $"https://{RequiredHost}/v1/responses")
        {
            Content = JsonBody(new Dictionary<string, object>
            {
                ["model"] = _model,
                ["tools"] = new object[] { new Dictionary<string, object> { ["type"] = "web_search" } },
                ["tool_choice"] = "required",
                ["include"] = new[] { "web_search_call.action.sources" },
                ["input"] = SearchInstruction + capped,
                ["store"] = false
            })
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        return request;
    }

    protected override WebSearchResponse Parse(string json, int maxResults)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement? output = Get(root, "output");
        if (output is null || output.Value.ValueKind != JsonValueKind.Array)
            throw new JsonException("no output array");

        List<string> queries = [];
        int actions = 0;
        System.Text.StringBuilder answer = new();
        // url -> (title, snippet); insertion order preserved.
        Dictionary<string, (string Title, string Snippet)> byUrl = new(StringComparer.Ordinal);
        List<string> order = [];
        List<(string Url, string Title)> sources = [];

        foreach (JsonElement item in output.Value.EnumerateArray())
        {
            string? type = Str(item, "type");
            if (type == "web_search_call")
            {
                JsonElement? action = Get(item, "action");
                string? actionType = action is { } a ? Str(a, "type") : null;
                if (actionType is null or "search")
                {
                    actions++;
                    if (action is { } act && Str(act, "query") is { Length: > 0 } q)
                        queries.Add(Sanitize(q, 200));
                }

                if (action is { } ac && Get(ac, "sources") is { ValueKind: JsonValueKind.Array } srcs)
                {
                    foreach (JsonElement s in srcs.EnumerateArray())
                        sources.Add((Str(s, "url") ?? string.Empty, Str(s, "title") ?? string.Empty));
                }
            }
            else if (type == "message" && Get(item, "content") is { ValueKind: JsonValueKind.Array } content)
            {
                foreach (JsonElement part in content.EnumerateArray())
                {
                    string text = Str(part, "text") ?? string.Empty;
                    if (answer.Length > 0 && text.Length > 0) answer.Append('\n');
                    answer.Append(text);

                    if (Get(part, "annotations") is not { ValueKind: JsonValueKind.Array } annotations) continue;
                    foreach (JsonElement ann in annotations.EnumerateArray())
                    {
                        if (Str(ann, "type") != "url_citation") continue;
                        string? url = CleanUrl(Str(ann, "url"));
                        if (url is null || byUrl.ContainsKey(url)) continue;

                        long start = Long(ann, "start_index");
                        long end = Long(ann, "end_index");
                        string slice = start >= 0 && end > start && end <= text.Length
                            ? Sanitize(text.Substring((int)start, (int)(end - start)), MaxCitationSnippet)
                            : string.Empty;
                        byUrl[url] = (Sanitize(Str(ann, "title"), WebSearchLimits.MaxTitleChars), slice);
                        order.Add(url);
                    }
                }
            }
        }

        foreach ((string rawUrl, string title) in sources)
        {
            string? url = CleanUrl(rawUrl);
            if (url is null || byUrl.ContainsKey(url)) continue;
            byUrl[url] = (Sanitize(title, WebSearchLimits.MaxTitleChars), string.Empty);
            order.Add(url);
        }

        List<WebSearchResult> results = order.Take(maxResults)
            .Select(u => new WebSearchResult(byUrl[u].Title, u, byUrl[u].Snippet)).ToList();

        JsonElement? usage = Get(root, "usage");
        long input = usage is { } u1 ? Long(u1, "input_tokens") : 0;
        long outTokens = usage is { } u2 ? Long(u2, "output_tokens") : 0;

        string answerText = Sanitize(answer.ToString(), WebSearchLimits.MaxAnswerChars);
        return new WebSearchResponse(
            "openai", results, answerText.Length == 0 ? null : answerText, queries, actions, null, false,
            input, outTokens);
    }
}
