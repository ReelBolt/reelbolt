using System.Text.Json;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The step-level "continue from an earlier run's composition" option, stored as
/// <c>WorkflowStep.ContinueCompositionJson</c> (<c>{"executionId":"…"}</c> or <c>{"versionId":"…"}</c>).
/// Only an Agent step whose agent holds <c>SandboxAuthoring</c> may carry it; the engine restores the
/// named composition workspace version into the new run's sandbox before the agent runs. The source
/// must belong to the same project as the run — checked at save time and again at run time.
/// </summary>
public sealed record ContinueCompositionConfig(Guid? ExecutionId = null, Guid? VersionId = null)
{
    /// <summary>Web defaults (camelCase, case-insensitive), the same shape every step config uses.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The failures, empty when exactly one of the two ids is set.</summary>
    public List<string> Validate()
    {
        bool hasExecution = ExecutionId is { } e && e != Guid.Empty;
        bool hasVersion = VersionId is { } v && v != Guid.Empty;
        return hasExecution == hasVersion
            ? ["continueComposition must name exactly one of executionId or versionId."]
            : [];
    }

    /// <summary>Parse and validate <paramref name="json"/>; false with a reason when it is unusable.</summary>
    public static bool TryParse(string? json, out ContinueCompositionConfig? config, out string? error)
    {
        config = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "continueCompositionJson is empty.";
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "continueCompositionJson must be a JSON object.";
                return false;
            }

            config = doc.RootElement.Deserialize<ContinueCompositionConfig>(JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"continueCompositionJson is not valid JSON: {ex.Message}";
            return false;
        }

        List<string> errors = config?.Validate() ?? ["continueCompositionJson must be a JSON object."];
        if (errors.Count > 0)
        {
            config = null;
            error = string.Join(" ", errors);
            return false;
        }

        error = null;
        return true;
    }
}
