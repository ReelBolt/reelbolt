using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// The server-written <c>overlayLegibility</c> list a motion-graphics step adds to its output
/// (docs/design/graphics-see-and-size.md): one record per rendered overlay asset of the plan, from the
/// step's <see cref="OverlayLegibilityLedger"/>. The compile reads it to enforce the floor. A
/// model-supplied key of the same name is always replaced, so only the engine's measurement reaches
/// the compile.
/// </summary>
public static class OverlayLegibilityFindings
{
    /// <summary>The output key.</summary>
    public const string Key = "overlayLegibility";

    /// <summary>Reason for an asset key the render tool never measured in this step.</summary>
    public const string NotMeasuredInThisStep = "not_rendered_in_this_step";

    private static readonly JsonSerializerOptions PlanJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The records for <paramref name="plan"/>'s asset overlays; null when the ledger was never configured.</summary>
    public static JsonArray? BuildFor(MotionGraphicsPlanOutput plan, OverlayLegibilityLedger ledger)
    {
        if (ledger.Canvas is not { } canvas)
            return null;

        var list = new JsonArray();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (MotionGraphicsOverlay overlay in plan.Overlays ?? [])
        {
            string key = overlay.RenderedAssetStorageKey;
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                continue;

            OverlayLegibilityRecord record = ledger.Find(key) ?? new OverlayLegibilityRecord(
                key, string.Empty, 0, 0, canvas.Width, canvas.Height, canvas.MinCapHeightPct, canvas.MinFontPx,
                Measured: false, Passed: false, Reason: NotMeasuredInThisStep);
            list.Add(record.ToJson());
        }

        return list;
    }

    /// <summary>
    /// <paramref name="output"/> (a planner's raw answer) with the ledger's records set under
    /// <see cref="Key"/>. Unchanged when the ledger was never configured or the answer holds no
    /// readable plan object; otherwise the answer is reduced to that plan object (the compile reads only
    /// it anyway).
    /// </summary>
    public static string Merge(string output, OverlayLegibilityLedger ledger)
    {
        if (ledger.Canvas is null || string.IsNullOrWhiteSpace(output))
            return output;

        string? json = RobustJsonExtractor.ExtractLastJsonObject(output);
        if (json is null)
            return output;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return output;
            MotionGraphicsPlanOutput? plan = root.Deserialize<MotionGraphicsPlanOutput>(PlanJsonOptions);
            if (plan is null)
                return output;

            root.Remove(Key);
            if (BuildFor(plan, ledger) is { } records)
                root[Key] = records;
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return output;
        }
    }

    /// <summary>
    /// Retry guidance naming every asset overlay of <paramref name="plan"/> measured below the floor, or null
    /// when none is. Used by the graphics room's one policy retry.
    /// </summary>
    public static string? BelowFloorFeedback(MotionGraphicsPlanOutput plan, OverlayLegibilityLedger ledger)
    {
        if (ledger.Canvas is not { } canvas)
            return null;

        List<string> failing = [];
        foreach (MotionGraphicsOverlay overlay in plan.Overlays ?? [])
        {
            if (ledger.Find(overlay.RenderedAssetStorageKey) is { Measured: true, Passed: false } record)
            {
                string smallest = record.SmallestFontPx is { } px
                    ? $" (smallest text {px.ToString("0.#", CultureInfo.InvariantCulture)}px)"
                    : string.Empty;
                failing.Add($"{overlay.PlacementId}{smallest}");
            }
        }

        if (failing.Count == 0)
            return null;

        return $"The rendered graphic(s) for {string.Join(", ", failing)} have text below the legibility floor: every line must be at least " +
               $"{canvas.MinFontPx}px on the {canvas.Width}x{canvas.Height} canvas (capitals {OverlayTextSizing.Format(canvas.MinCapHeightPct)}% of the height). " +
               "Make the type bigger in that composition, render it again with force: true and a forceReason, and use the new storage key. " +
               "Keep each overlay's `text` set to its main words: a graphic left below the floor is replaced by that text at compile.";
    }
}
