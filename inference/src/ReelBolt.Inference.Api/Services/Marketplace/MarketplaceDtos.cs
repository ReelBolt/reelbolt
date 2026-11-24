namespace ReelBolt.Inference.Api.Services.Marketplace;

public sealed record PublishPreviewRequest(Guid ProjectId, Guid WorkflowId);

public sealed record PublishRecipeRequest(
    Guid ProjectId,
    Guid WorkflowId,
    string? OutputRef,
    string? Name,
    string? Description,
    string? Category,
    string? Language,
    string? AuthorLabel,
    bool ConsentAccepted,
    string? ConsentVersion);

public sealed record RefusalDto(string Code, int? StepOrder, string Message);

public sealed record RemovedDto(int StepOrder, string Field, string What);

public sealed record SlotDto(string Kind, int StepOrder, string Label);

public sealed record StepDto(int Order, string Type, string Label, IReadOnlyList<string> Limits);

public sealed record PromptDto(int StepOrder, string Field, string Text);

public sealed record PublishPreviewResponse(
    bool Publishable,
    IReadOnlyList<RefusalDto> Refusals,
    IReadOnlyList<RemovedDto> Removed,
    IReadOnlyList<SlotDto> Slots,
    IReadOnlyList<StepDto> Steps,
    IReadOnlyList<PromptDto> Prompts,
    string CostClass);

public sealed record PublishedRecipeResponse(Guid Id, string Status, int Version, string PreviewStatus);

public sealed record ConsentResponse(string Version, string Text, string LicenceSummary);

public sealed record MineVersionDto(int N, string PreviewStatus, DateTime CreatedAt);

public sealed record MineRecipeDto(
    Guid Id,
    string Name,
    string Category,
    string CostClass,
    string Status,
    bool HiddenByReports,
    string? ReasonCategory,
    int? CurrentVersion,
    string? PreviewStatus,
    IReadOnlyList<MineVersionDto> Versions,
    int Copies,
    long Runs,
    double? RatingAverage,
    int RatingCount,
    DateTime? ListedAt,
    DateTime CreatedAt,
    int Version,
    int CopyCount,
    long RunCount,
    string Description,
    string? Language,
    [property: System.Text.Json.Serialization.JsonPropertyName("authorLabel")] string Label);

public sealed record MineResponse(IReadOnlyList<MineRecipeDto> Items);

/// <summary>A refused or failed request. A null <see cref="Code"/> is a bare status with no body.</summary>
public sealed record MarketplaceError(int Status, string? Code = null, string? Message = null, IReadOnlyList<RefusalDto>? Refusals = null);

public sealed record MarketplaceResult<T>(T? Value, MarketplaceError? Error)
{
    public static MarketplaceResult<T> Ok(T value) => new(value, null);

    public static MarketplaceResult<T> Fail(MarketplaceError error) => new(default, error);
}
