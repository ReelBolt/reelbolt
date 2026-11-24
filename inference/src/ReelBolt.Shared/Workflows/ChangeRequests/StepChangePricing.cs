using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.ChangeRequests;

/// <summary>
/// The step config a change request's hold and estimate are PRICED from: the configured one with the request's
/// overrides applied where they make an item bigger. The run itself still executes the unchanged config (each take
/// reads its own override), so only the price sees the raised size. Pure; null/unparseable input comes back as is.
/// </summary>
public static class StepChangePricing
{
    /// <summary>
    /// An AudioGenerate config whose <c>durationSec</c> is the largest of its own and every override's <c>durationSec</c>
    /// (any item key, <c>*</c> included): a take re-made longer than configured is held at the longer price. The full-step
    /// hold at that size times the scope fraction always covers the re-made takes (each is at most that long).
    /// </summary>
    public static string? AudioConfigWithOverrides(
        string? configJson, IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? overrides)
    {
        if (string.IsNullOrWhiteSpace(configJson) || overrides is not { Count: > 0 })
            return configJson;
        double longest = 0d;
        foreach (IReadOnlyDictionary<string, JsonElement>? keys in overrides.Values)
            if (keys is not null && keys.TryGetValue("durationSec", out JsonElement d)
                && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out double sec) && double.IsFinite(sec))
                longest = Math.Max(longest, sec);
        if (longest <= 0d)
            return configJson;
        try
        {
            AudioGenerateStepConfig? config = JsonSerializer.Deserialize<AudioGenerateStepConfig>(configJson, AudioGenerateStepConfig.JsonOptions);
            if (config is null || longest <= config.DurationSec)
                return configJson;
            return JsonSerializer.Serialize(config with { DurationSec = longest }, AudioGenerateStepConfig.JsonOptions);
        }
        catch (JsonException)
        {
            return configJson;
        }
    }

    /// <summary>The same for the stored overrides JSON of a change request row (item id to key to value).</summary>
    public static string? AudioConfigWithOverrides(string? configJson, string? overridesJson)
    {
        if (string.IsNullOrWhiteSpace(overridesJson))
            return configJson;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(overridesJson);
            return AudioConfigWithOverrides(configJson,
                parsed?.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, JsonElement>)(p.Value ?? [])));
        }
        catch (JsonException)
        {
            return configJson;
        }
    }
}
