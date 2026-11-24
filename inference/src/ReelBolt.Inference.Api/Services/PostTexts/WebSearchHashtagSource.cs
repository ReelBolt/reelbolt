using System.Text.RegularExpressions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.PostTexts;

/// <summary>
/// Opt-in hashtag source that reads tags out of live web search results. One search per request (the first call decides;
/// later platforms reuse the answer). The result text is never given to a model: tags are pulled out by code, validated
/// like any other, and the pages they came from are cited. Nothing is called "trending" here. Any failure, a restricted
/// provider (Gemini grounding needs a display we do not have), no provider, or no tag found in the results falls back to
/// the model's own tags with a note, and never fails the request.
/// </summary>
public sealed class WebSearchHashtagSource : IHashtagSource
{
    public const string SourceName = "web search";
    public const int MaxTags = 10;
    public const int MaxCitations = 5;
    public const int ResultsAsked = 8;

    private static readonly Regex TagPattern = new(
        @"(?<![\p{L}\p{N}_&/])#([\p{L}][\p{L}\p{N}_]{1,39})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly IInferenceProviderResolver _providers;
    private readonly IWebSearchClientFactory _clients;
    private readonly ILogger<WebSearchHashtagSource> _logger;
    private readonly Dictionary<string, WebFinding> _byQuery = [];

    public WebSearchHashtagSource(
        IInferenceProviderResolver providers, IWebSearchClientFactory clients, ILogger<WebSearchHashtagSource> logger)
    {
        _providers = providers;
        _clients = clients;
        _logger = logger;
    }

    private sealed record WebFinding(IReadOnlyList<(string Tag, int Count, int Result)> Tags, IReadOnlyList<HashtagCitation> Results, string? Note);

    /// <summary>The search phrase: the topic words (links stripped) and the platform when only one is asked for.</summary>
    public static string BuildQuery(string topic, string platformLabel)
    {
        string words = PostTextValidator.StripUnsafe(topic).Replace('\n', ' ');
        words = Regex.Replace(words, @"[^\p{L}\p{N} '\-]", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        words = Regex.Replace(words, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
        string query = $"hashtags for {platformLabel} video about {words}".Trim();
        return query.Length <= WebSearchLimits.MaxQueryChars ? query : query[..WebSearchLimits.MaxQueryChars].TrimEnd();
    }

    /// <summary>The tags written as #word in some results, most repeated first, with the result each first appeared in.</summary>
    public static IReadOnlyList<(string Tag, int Count, int Result)> ExtractTags(IReadOnlyList<WebSearchResult> results)
    {
        var counts = new Dictionary<string, (int Count, int Result, string Shown)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < results.Count; i++)
        {
            foreach (Match m in TagPattern.Matches(results[i].Title + " " + results[i].Snippet))
            {
                string? tag = PostTextValidator.NormalizeHashtag(m.Groups[1].Value);
                if (tag is null) continue;
                counts[tag] = counts.TryGetValue(tag, out var c) ? (c.Count + 1, c.Result, c.Shown) : (1, i, tag);
            }
        }

        return counts.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Value.Result)
            .Take(MaxTags).Select(kv => (kv.Value.Shown, kv.Value.Count, kv.Value.Result)).ToList();
    }

    public async Task<HashtagSuggestion> SuggestAsync(HashtagSourceRequest request, CancellationToken ct)
    {
        // The query names the platform only when the whole request is for one; otherwise the answer is shared.
        string label = PostTextPlatformLimits.Find(request.Platform)?.Label ?? request.Platform;
        string query = BuildQuery(request.Topic, request.SinglePlatform ? label : "social");
        if (!_byQuery.TryGetValue(query, out WebFinding? finding))
        {
            finding = await SearchAsync(query, ct);
            _byQuery[query] = finding;
        }

        HashtagSuggestion Fallback(string note) =>
            new(request.ModelTags, HashtagKind.ModelSuggested, ModelSuggestedHashtagSource.SourceName, null, note);

        if (finding.Note is not null) return Fallback(finding.Note);
        if (finding.Tags.Count == 0) return Fallback("No hashtags were found in the search results, so these are the writer's own suggestions.");

        var cited = finding.Tags.Select(t => t.Result).Distinct().Order().Take(MaxCitations)
            .Select(i => finding.Results[i]).ToList();
        PostTextPlatform? platform = PostTextPlatformLimits.Find(request.Platform);
        int want = Math.Max(1, platform?.SuggestedHashtags ?? 3);
        var tags = finding.Tags.Select(t => t.Tag).Take(want).ToList();
        string? note = null;
        foreach (string model in request.ModelTags)
        {
            if (tags.Count >= want) break;
            if (PostTextValidator.NormalizeHashtag(model) is { } m && !tags.Contains(m, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(m);
                note = "Some tags are the writer's own suggestions; the cited pages support the others.";
            }
        }

        return new HashtagSuggestion(tags, HashtagKind.WebSearched, SourceName, cited, note);
    }

    private async Task<WebFinding> SearchAsync(string query, CancellationToken ct)
    {
        const string noSearch = "Web search is not available right now, so these are the writer's own suggestions.";
        try
        {
            ResolvedWebSearchProvider? provider = await _providers.ResolveWebSearchAsync(null, ct);
            if (provider is null) return new WebFinding([], [], noSearch);
            if (provider.Kind == InferenceProviderKind.Gemini)
                return new WebFinding([], [], "The web search provider's terms need a display that is not available yet, so these are the writer's own suggestions.");

            IWebSearchClient client = _clients.Create(provider);
            WebSearchResponse response;
            try
            {
                response = await client.SearchAsync(new WebSearchRequest(query, ResultsAsked), ct);
            }
            finally
            {
                (client as IDisposable)?.Dispose();
            }

            if (response.Restricted)
                return new WebFinding([], [], "The web search provider's terms need a display that is not available yet, so these are the writer's own suggestions.");

            // Only http and https pages can be cited (or read for tags); javascript:, data:, file: and the rest are dropped.
            IReadOnlyList<WebSearchResult> usable = response.Results.Where(r => IsWebUrl(r.Url)).ToList();
            var results = usable.Select(r => new HashtagCitation(
                PostTextValidator.StripUnsafe(r.Title).Replace('\n', ' ') is { Length: > 0 } t ? (t.Length > 120 ? t[..120] : t) : HostOf(r.Url),
                HostOf(r.Url), r.Url)).ToList();
            return new WebFinding(ExtractTags(usable), results, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ModelUnpricedException)
        {
            return new WebFinding([], [], noSearch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Hashtag web search failed ({ErrorType}, {Code})", ex.GetType().Name, (ex as WebSearchException)?.Code);
            return new WebFinding([], [], noSearch);
        }
    }

    /// <summary>True for an absolute http or https URL with a host.</summary>
    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(u.Host);

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? u.Host : string.Empty;
}
