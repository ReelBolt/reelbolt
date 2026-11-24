namespace ReelBolt.Shared.Inference;

/// <summary>The hard bounds of one web search call. Enforced by the clients, whatever a caller passes in.</summary>
public static class WebSearchLimits
{
    /// <summary>Default wall-clock bound of one search call.</summary>
    public const int DefaultTimeoutSeconds = 20;

    /// <summary>Largest timeout a provider row may ask for.</summary>
    public const int MaxTimeoutSeconds = 60;

    /// <summary>Most results one call returns.</summary>
    public const int MaxResults = 10;

    /// <summary>Default cap on the WebSearch calls of one agent step; the worst case a step's credit hold is sized for.</summary>
    public const int DefaultMaxCallsPerStep = 5;

    /// <summary>Most characters of a query the agent tool accepts.</summary>
    public const int MaxQueryChars = 200;

    /// <summary>Most characters of one title.</summary>
    public const int MaxTitleChars = 300;

    /// <summary>Most characters of one snippet.</summary>
    public const int MaxSnippetChars = 600;

    /// <summary>Most characters of the synthesized answer.</summary>
    public const int MaxAnswerChars = 2000;
}

/// <summary>One search request. <paramref name="MaxResults"/> is clamped to 1..10 by the clients.</summary>
public sealed record WebSearchRequest(string Query, int MaxResults);

/// <summary>One result. The URL is always an absolute http/https URL without userinfo.</summary>
public sealed record WebSearchResult(string Title, string Url, string Snippet, string? PublishedDate = null);

/// <summary>
/// The answer of one search. <see cref="SearchUnits"/> is the billable count (queries/actions);
/// <see cref="Restricted"/> is true for Gemini (terms bar caching and forwarding) and then
/// <see cref="SearchSuggestionsHtml"/> must be displayed with the result.
/// </summary>
public sealed record WebSearchResponse(
    string Provider,
    IReadOnlyList<WebSearchResult> Results,
    string? Answer,
    IReadOnlyList<string> Queries,
    int SearchUnits,
    string? SearchSuggestionsHtml,
    bool Restricted,
    long InputTokens = 0,
    long OutputTokens = 0);

/// <summary>Runs a live web search. Implementations throw <see cref="WebSearchException"/> on failure.</summary>
public interface IWebSearchClient
{
    Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken ct);
}

/// <summary>Builds a metered web search client for a resolved provider.</summary>
public interface IWebSearchClientFactory
{
    IWebSearchClient Create(ResolvedWebSearchProvider provider);
}

/// <summary>
/// A web search failure with a stable <see cref="Code"/>. The message never contains the query or any part of
/// a response body.
/// </summary>
public sealed class WebSearchException : Exception
{
    public const string RateLimited = "WEB_SEARCH_RATE_LIMITED";
    public const string Auth = "WEB_SEARCH_AUTH";
    public const string BadResponse = "WEB_SEARCH_BAD_RESPONSE";
    public const string Timeout = "WEB_SEARCH_TIMEOUT";
    public const string Unpriced = "WEB_SEARCH_UNPRICED";
    public const string UnsupportedEndpoint = "WEB_SEARCH_UNSUPPORTED_ENDPOINT";
    public const string Unavailable = "WEB_SEARCH_UNAVAILABLE";

    public string Code { get; }

    public WebSearchException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }
}
