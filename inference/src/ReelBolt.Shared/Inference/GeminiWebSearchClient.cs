using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Gemini Grounding with Google Search through the NATIVE <c>generateContent</c> route
/// (<c>POST {base}/v1beta/models/{model}:generateContent</c>, <c>x-goog-api-key</c>, <c>google_search</c> tool) -
/// never the <c>/v1beta/openai/</c> compatibility route, which does not ground. The result is marked
/// <c>Restricted</c> (Google's terms bar caching and forwarding) and carries the Search Suggestions HTML.
/// </summary>
internal sealed class GeminiWebSearchClient : WebSearchClientBase
{
    public const string DefaultEndpoint = "https://generativelanguage.googleapis.com";
    internal const int MaxQueryChars = 500;
    private const int MaxSuggestionsChars = 20_000;
    private const int MaxChunkSnippet = 300;

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public GeminiWebSearchClient(ResolvedWebSearchProvider provider, HttpClient http, bool ownsClient)
        : base(http, provider.TimeoutSeconds, ownsClient)
    {
        _apiKey = ResolveKey(provider.ApiKey, "Gemini", provider.IsPlatformManaged, "GEMINI_API_KEY", "GOOGLE_API_KEY");
        _baseUrl = NormalizeBase(provider.Endpoint);
        _model = NormalizeModel(provider.ModelName);
    }

    /// <summary>Strips a trailing <c>/v1beta/openai</c> (or <c>/v1beta</c>) so the native route is always used.</summary>
    internal static string NormalizeBase(string? endpoint)
    {
        string e = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim().TrimEnd('/');
        foreach (string suffix in new[] { "/v1beta/openai", "/v1beta", "/openai" })
        {
            if (e.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                e = e[..^suffix.Length];
                break;
            }
        }

        return e.TrimEnd('/');
    }

    /// <summary>Strips a leading <c>models/</c>, which the native route would double.</summary>
    internal static string NormalizeModel(string? model)
    {
        string m = (model ?? string.Empty).Trim();
        return m.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? m["models/".Length..] : m;
    }

    /// <summary>
    /// Gemini 3 and newer, and any name that is not gemini-<major>, bill per search query; 2.5 and older bill per grounded prompt (one unit for a call that ran
    /// any search). Zero when no search ran. Same family split as the catalog's per-model WebSearch rows.
    /// </summary>
    internal static int BillableUnits(string model, int queryCount)
    {
        if (queryCount <= 0) return 0;
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            model, @"^gemini-(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        // Only an explicit 2.5-and-older family is per grounded prompt; an alias or unknown name bills per query.
        bool perPrompt = m.Success && int.TryParse(m.Groups[1].Value, out int major) && major < 3;
        return perPrompt ? 1 : queryCount;
    }

    protected override string ProviderLabel => "Gemini";

    protected override HttpRequestMessage BuildRequest(string query, int maxResults)
    {
        string capped = query.Length > MaxQueryChars ? query[..MaxQueryChars] : query;
        HttpRequestMessage request = new(
            HttpMethod.Post, $"{_baseUrl}/v1beta/models/{Uri.EscapeDataString(_model)}:generateContent")
        {
            Content = JsonBody(new Dictionary<string, object>
            {
                ["contents"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["parts"] = new object[] { new Dictionary<string, object> { ["text"] = SearchInstruction + capped } }
                    }
                },
                ["tools"] = new object[] { new Dictionary<string, object> { ["google_search"] = new Dictionary<string, object>() } }
            })
        };
        request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
        return request;
    }

    protected override WebSearchResponse Parse(string json, int maxResults)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");

        JsonElement? candidates = Get(root, "candidates");
        JsonElement cand = candidates is { ValueKind: JsonValueKind.Array } c && c.GetArrayLength() > 0
            ? c[0]
            : default;

        System.Text.StringBuilder answer = new();
        if (cand.ValueKind == JsonValueKind.Object && Get(cand, "content") is { } content
            && Get(content, "parts") is { ValueKind: JsonValueKind.Array } parts)
        {
            foreach (JsonElement p in parts.EnumerateArray())
            {
                if (Str(p, "text") is { Length: > 0 } t) answer.Append(t);
            }
        }

        List<string> queries = [];
        List<WebSearchResult> results = [];
        string? suggestions = null;

        if (cand.ValueKind == JsonValueKind.Object && Get(cand, "groundingMetadata") is { ValueKind: JsonValueKind.Object } gm)
        {
            if (Get(gm, "webSearchQueries") is { ValueKind: JsonValueKind.Array } qs)
            {
                foreach (JsonElement q in qs.EnumerateArray())
                    if (q.ValueKind == JsonValueKind.String) queries.Add(Sanitize(q.GetString(), 200));
            }

            // chunk index -> first supported text segment.
            Dictionary<int, string> snippets = [];
            if (Get(gm, "groundingSupports") is { ValueKind: JsonValueKind.Array } supports)
            {
                foreach (JsonElement sup in supports.EnumerateArray())
                {
                    string text = Get(sup, "segment") is { } seg ? Str(seg, "text") ?? string.Empty : string.Empty;
                    if (text.Length == 0 || Get(sup, "groundingChunkIndices") is not { ValueKind: JsonValueKind.Array } idx) continue;
                    foreach (JsonElement i in idx.EnumerateArray())
                    {
                        if (i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out int n))
                            snippets.TryAdd(n, Sanitize(text, MaxChunkSnippet));
                    }
                }
            }

            if (Get(gm, "groundingChunks") is { ValueKind: JsonValueKind.Array } chunks)
            {
                HashSet<string> seen = new(StringComparer.Ordinal);
                int index = -1;
                foreach (JsonElement ch in chunks.EnumerateArray())
                {
                    index++;
                    if (Get(ch, "web") is not { } web) continue;
                    string? url = CleanUrl(Str(web, "uri"));
                    if (url is null || !seen.Add(url)) continue;
                    results.Add(new WebSearchResult(
                        Sanitize(Str(web, "title"), WebSearchLimits.MaxTitleChars), url,
                        snippets.GetValueOrDefault(index, string.Empty)));
                    if (results.Count >= maxResults) break;
                }
            }

            if (Get(gm, "searchEntryPoint") is { } sep && Str(sep, "renderedContent") is { Length: > 0 } html)
                suggestions = html.Length > MaxSuggestionsChars ? html[..MaxSuggestionsChars] : html;
        }

        JsonElement? usage = Get(root, "usageMetadata");
        long input = usage is { } u1 ? Long(u1, "promptTokenCount") : 0;
        long outTokens = usage is { } u2 ? Long(u2, "candidatesTokenCount") + Long(u2, "thoughtsTokenCount") : 0;
        if (usage is { } u3) input += Long(u3, "toolUsePromptTokenCount");

        string answerText = Sanitize(answer.ToString(), WebSearchLimits.MaxAnswerChars);
        return new WebSearchResponse(
            "gemini", results, answerText.Length == 0 ? null : answerText, queries, BillableUnits(_model, queries.Count),
            suggestions, true, input, outTokens);
    }
}
