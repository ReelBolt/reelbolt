namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>Public list card. Carries an author label only; never an author, organization, project or storage identifier.</summary>
public sealed record RecipeCardDto(
    Guid Id,
    string Name,
    string Description,
    string Category,
    string Language,
    string CostClass,
    int StepCount,
    IReadOnlyList<string> StepTypes,
    string AuthorLabel,
    int CopyCount,
    double? RatingAverage,
    int RatingCount,
    double PreviewSeconds,
    DateTime? ListedAt,
    int Version);

public sealed record RecipeInputDto(string Key, string Label, string Kind);

/// <summary>A card plus the full description, steps, prompts, open slots and inputs of the current listed version.</summary>
public sealed record RecipeDetailDto(
    Guid Id,
    string Name,
    string Description,
    string Category,
    string Language,
    string CostClass,
    int StepCount,
    IReadOnlyList<string> StepTypes,
    string AuthorLabel,
    int CopyCount,
    double? RatingAverage,
    int RatingCount,
    double PreviewSeconds,
    DateTime? ListedAt,
    int Version,
    IReadOnlyList<StepDto> Steps,
    IReadOnlyList<PromptDto> Prompts,
    IReadOnlyList<SlotDto> Slots,
    IReadOnlyList<RecipeInputDto> Inputs,
    string CostNote);

public sealed record RecipeListDto(IReadOnlyList<RecipeCardDto> Items, string? NextCursor);

public sealed record FacetEntryDto(string Key, int Count);

public sealed record FacetsDto(
    IReadOnlyList<FacetEntryDto> Categories,
    IReadOnlyList<FacetEntryDto> StepTypes,
    IReadOnlyList<FacetEntryDto> Languages,
    IReadOnlyList<FacetEntryDto> CostClasses);

/// <summary>The listing filters of <c>GET recipes</c>. <see cref="Sort"/> is <c>new</c>, <c>copied</c>, <c>rated</c> or null (relevance with a query, else new).</summary>
public sealed record RecipeListQuery(string? Q, string? Category, string? Cost, string? StepType, string? Language, string? Sort, string? Cursor, int Limit);

/// <summary>What the controller needs to stream a listed recipe's preview or poster.</summary>
public sealed record RecipeMedia(Guid RecipeId, Guid VersionId, string? PreviewKey, string? PosterKey, string? PreviewSha256, string? PosterSha256 = null);
