using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>Create or replace a saved editing style. <see cref="ProjectId"/> null = offered in every project.</summary>
public record SaveEditingStyleRequest(string Name, string? Description = null, Guid? ProjectId = null, StyleConfig? Config = null);

public record EditingStyleResponse(
    Guid Id,
    string Name,
    string? Description,
    Guid? ProjectId,
    StyleConfig Config,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static EditingStyleResponse From(EditingStyle style) => new(
        style.Id,
        style.Name,
        style.Description,
        style.ProjectId,
        StyleJson.TryParseConfig(style.ConfigJson) ?? new StyleConfig(),
        style.CreatedAt,
        style.UpdatedAt);
}

public record StylePresetResponse(string Key, string Name, string Description, StyleConfig Config, string SuggestedFormat)
{
    public static StylePresetResponse From(StylePreset preset) =>
        new(preset.Key, preset.Name, preset.Description, preset.Config, preset.SuggestedFormat.ToString());
}
