using System.Text.Json;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// Overlay legibility (docs/design/graphics-see-and-size.md): while a motion-graphics step runs, its
/// <see cref="OverlayLegibilityLedger"/> is published, and every successful render of a composition
/// with the output canvas's shape is measured by the same lint <c>CheckLayout</c> runs, at the font
/// size whose capitals reach the floor on that canvas. The result is recorded per storage key (the
/// executor writes it into the step output and the compile enforces it) and handed back to the agent
/// in the render result. It never refuses or fails a render: a composition of another shape (a screen
/// insert, a legacy band asset) is recorded as not measured.
/// </summary>
public partial class ReactRemotionSandboxTools
{
    /// <summary>Frames the overlay legibility lint samples (first, middle, last).</summary>
    internal const int OverlayLegibilitySampleCount = 3;

    /// <summary>
    /// Measures the composition just rendered to <paramref name="storageKey"/> against the published
    /// ledger's canvas and floor, records the outcome and returns it; null when no motion-graphics
    /// ledger is configured. Best effort: any failure records an unmeasured outcome.
    /// </summary>
    internal async Task<OverlayLegibilityRecord?> MeasureOverlayLegibilityAsync(
        ISandboxSession session, OverlayLegibilityLedger? ledger, string compositionId, CompositionInfo? composition, string storageKey)
    {
        if (ledger?.Canvas is not { } canvas)
            return null;

        OverlayLegibilityRecord record;
        if (composition is null || composition.Width <= 0 || composition.Height <= 0)
        {
            record = Unmeasured(storageKey, compositionId, 0, 0, canvas, OverlayLegibilityRecord.LintUnavailable);
        }
        else if (!canvas.HasShapeOf(composition.Width, composition.Height))
        {
            record = Unmeasured(storageKey, compositionId, composition.Width, composition.Height, canvas, OverlayLegibilityRecord.NotCanvasSized);
        }
        else
        {
            try
            {
                (int[]? frames, _) = ChooseLayoutFrames(composition, null, OverlayLegibilitySampleCount);
                double outputScale = Math.Round(canvas.Height / (double)composition.Height, 7);
                double minOnScreenPx = canvas.MinFontPx;
                double shortSide = Math.Min(canvas.Width, canvas.Height);
                double threshold = shortSide > 0 ? Math.Round(minOnScreenPx * ReferenceShortSide / shortSide, 2) : minOnScreenPx;
                string lintJson = await RunLayoutLintAsync(session, composition, frames ?? [0], threshold, minOnScreenPx, outputScale);
                record = FromLintResult(lintJson, storageKey, compositionId, composition.Width, composition.Height, canvas);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Overlay legibility lint of {CompositionId} failed; recording it as not measured", compositionId);
                record = Unmeasured(storageKey, compositionId, composition.Width, composition.Height, canvas, OverlayLegibilityRecord.LintUnavailable);
            }
        }

        ledger.Record(record);
        _logger.LogInformation(
            "Overlay legibility of {StorageKey} ({CompositionId}): measured={Measured}, passed={Passed}, smallest={SmallestFontPx}px, floor={MinFontPx}px",
            storageKey, compositionId, record.Measured, record.Passed, record.SmallestFontPx, record.MinFontPx);
        return record;
    }

    /// <summary>
    /// The legibility outcome a <c>CheckLayout</c>-shaped result describes: measured when at least one
    /// frame was measured, passed when no text is <c>too_small</c>, the smallest offending on-screen
    /// size when one is. Pure, so the accept/reject rule is testable without a sandbox.
    /// </summary>
    internal static OverlayLegibilityRecord FromLintResult(
        string lintJson, string storageKey, string compositionId, int compositionWidth, int compositionHeight, OverlayCanvas canvas)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(lintJson);
            JsonElement root = document.RootElement;
            string? measurement = StringOf(root, "measurement");
            if (measurement is not ("measured" or "partial"))
                return Unmeasured(storageKey, compositionId, compositionWidth, compositionHeight, canvas, OverlayLegibilityRecord.LintUnavailable);

            int tooSmall = root.TryGetProperty("counts", out JsonElement counts) && counts.ValueKind == JsonValueKind.Object
                ? IntOf(counts, "too_small") ?? 0
                : 0;
            double? smallest = null;
            if (root.TryGetProperty("issues", out JsonElement issues) && issues.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement issue in issues.EnumerateArray())
                {
                    if (issue.ValueKind != JsonValueKind.Object || StringOf(issue, "kind") != "too_small")
                        continue;
                    tooSmall = Math.Max(tooSmall, 1);
                    if (NumberOf(issue, "onScreenPx") is { } px && (smallest is null || px < smallest))
                        smallest = px;
                }
            }

            return new OverlayLegibilityRecord(
                storageKey, compositionId, compositionWidth, compositionHeight, canvas.Width, canvas.Height,
                canvas.MinCapHeightPct, canvas.MinFontPx, Measured: true, Passed: tooSmall == 0, SmallestFontPx: smallest);
        }
        catch (JsonException)
        {
            return Unmeasured(storageKey, compositionId, compositionWidth, compositionHeight, canvas, OverlayLegibilityRecord.LintUnavailable);
        }
    }

    private static OverlayLegibilityRecord Unmeasured(
        string storageKey, string compositionId, int width, int height, OverlayCanvas canvas, string reason) =>
        new(storageKey, compositionId, width, height, canvas.Width, canvas.Height, canvas.MinCapHeightPct, canvas.MinFontPx,
            Measured: false, Passed: false, Reason: reason);
}
