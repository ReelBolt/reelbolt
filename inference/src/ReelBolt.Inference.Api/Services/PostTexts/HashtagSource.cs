namespace ReelBolt.Inference.Api.Services.PostTexts;

/// <summary>How a suggested tag was arrived at. Only <see cref="Measured"/> may ever be described as measured.</summary>
public enum HashtagKind
{
    ModelSuggested,
    Measured,

    /// <summary>Taken from cited web search results; used only when at least one source is cited.</summary>
    WebSearched
}

/// <summary>A web page the tags were read from: its title and where it is.</summary>
public sealed record HashtagCitation(string Title, string Host, string Url);

/// <summary>What a source is asked: the platform, the language, the topic words from the context and the tags the writer's model proposed.</summary>
public sealed record HashtagSourceRequest(
    string Platform, string Language, string Topic, IReadOnlyList<string> ModelTags, bool SinglePlatform = false);

/// <summary>One source's answer. Its tags still pass <see cref="PostTextValidator"/> (count, length, links) before anyone sees them.</summary>
public sealed record HashtagSuggestion(
    IReadOnlyList<string> Tags, HashtagKind Kind, string Source,
    IReadOnlyList<HashtagCitation>? Sources = null, string? Note = null)
{
    /// <summary>The plain label shown with the tags.</summary>
    public string Label => Kind switch
    {
        HashtagKind.Measured => $"measured by {Source}",
        HashtagKind.WebSearched when Sources is { Count: > 0 } => "found in cited web search results",
        _ => "suggested, not measured trends"
    };
}

/// <summary>The seam for where hashtags come from; register another implementation to replace the default.</summary>
public interface IHashtagSource
{
    Task<HashtagSuggestion> SuggestAsync(HashtagSourceRequest request, CancellationToken ct);
}

/// <summary>The default: the writer's own model-suggested tags. No vendor call, no web search, no claim about trends.</summary>
public sealed class ModelSuggestedHashtagSource : IHashtagSource
{
    public const string SourceName = "model";

    public Task<HashtagSuggestion> SuggestAsync(HashtagSourceRequest request, CancellationToken ct) =>
        Task.FromResult(new HashtagSuggestion(request.ModelTags, HashtagKind.ModelSuggested, SourceName));
}

/// <summary>The basis of the tags in a response (field <c>hashtagBasis</c>).</summary>
public sealed record HashtagBasis(
    string Kind, string Source, string Label, IReadOnlyList<HashtagCitation>? Sources = null, string? Note = null);
