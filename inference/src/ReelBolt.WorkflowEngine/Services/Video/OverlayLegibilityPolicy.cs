using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>What the compile does with one rendered overlay asset, given its legibility record.</summary>
public enum OverlayLegibilityAction
{
    /// <summary>No record (a plan made before measurement existed): composited exactly as before.</summary>
    Keep,
    /// <summary>Measured at or above the compile's floor: composited.</summary>
    Passed,
    /// <summary>Recorded but not measured (another shape, lint unavailable): composited, flagged.</summary>
    Unmeasured,
    /// <summary>Passed a LOWER floor than the compile's: composited, flagged.</summary>
    UnverifiedAtFloor,
    /// <summary>Measured below the floor and the entry has text: drawn as plain text at least the floor in size.</summary>
    TextFallback,
    /// <summary>Measured below the floor and the entry has no text: dropped.</summary>
    Dropped
}

/// <summary>
/// The compile's legibility rule for rendered overlay assets (docs/design/graphics-see-and-size.md). Pure:
/// the inputs are the engine-written <c>overlayLegibility</c> records on the plan, the compile's own floor
/// and whether the entry carries fallback text.
/// </summary>
public static class OverlayLegibilityPolicy
{
    /// <summary>Drop reason recorded for an asset measured below the floor with no fallback text.</summary>
    public const string BelowFloorDropReason = "asset_text_below_floor";

    public static OverlayLegibilityAction Decide(OverlayLegibilityRecord? record, double compileFloorCapPct, bool hasFallbackText)
    {
        if (record is null)
            return OverlayLegibilityAction.Keep;
        if (!record.Measured)
            return OverlayLegibilityAction.Unmeasured;
        if (!record.Passed)
            return hasFallbackText ? OverlayLegibilityAction.TextFallback : OverlayLegibilityAction.Dropped;
        return record.FloorCapPct + 1e-6 < compileFloorCapPct
            ? OverlayLegibilityAction.UnverifiedAtFloor
            : OverlayLegibilityAction.Passed;
    }

    /// <summary>The records of a plan's <c>overlayLegibility</c> list, by storage key; empty when there is none or it is unreadable.</summary>
    public static IReadOnlyDictionary<string, OverlayLegibilityRecord> Read(string? planJson)
    {
        Dictionary<string, OverlayLegibilityRecord> byKey = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(planJson))
            return byKey;

        try
        {
            if (JsonNode.Parse(planJson) is JsonObject root && root[OverlayLegibilityFindings.Key] is JsonArray list)
            {
                foreach (JsonNode? item in list)
                {
                    if (OverlayLegibilityRecord.FromJson(item) is { } record)
                        byKey[record.StorageKey] = record;
                }
            }
        }
        catch (JsonException)
        {
            // An unreadable list is no list: every asset is composited as before.
        }

        return byKey;
    }

    /// <summary>The word recorded in <c>graphics.legibility[].action</c>.</summary>
    public static string Word(OverlayLegibilityAction action) => action switch
    {
        OverlayLegibilityAction.Passed => "passed",
        OverlayLegibilityAction.Unmeasured => "unmeasured",
        OverlayLegibilityAction.UnverifiedAtFloor => "unverified_at_floor",
        OverlayLegibilityAction.TextFallback => "text_fallback",
        OverlayLegibilityAction.Dropped => "dropped",
        _ => "kept"
    };
}
