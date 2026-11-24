namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>P6: one media source's annotation, keyed by its opaque <c>mediaRef</c>.</summary>
public sealed record MediaAnnotationResponse(
    string MediaRef,
    IReadOnlyList<string> Keywords,
    string? Note,
    string? ColorLabel,
    bool Favourite,
    bool Flag,
    DateTime UpdatedAt);

/// <summary>
/// Body of <c>PUT …/media/annotations/{mediaRef}</c>. Every field is optional and an omitted one
/// keeps its stored value; <c>keywords: []</c> clears the keywords, <c>note: ""</c> clears the note,
/// <c>colorLabel: null</c> is "no label" only when the key is sent (JsonElement, like the bins' parent).
/// </summary>
public sealed record UpsertMediaAnnotationRequest(
    IReadOnlyList<string>? Keywords,
    string? Note,
    System.Text.Json.JsonElement ColorLabel,
    bool? Favourite,
    bool? Flag)
{
    public bool HasColorLabel => ColorLabel.ValueKind != System.Text.Json.JsonValueKind.Undefined;
}
