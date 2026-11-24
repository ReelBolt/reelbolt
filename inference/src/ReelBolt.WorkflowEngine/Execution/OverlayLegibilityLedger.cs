using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>
/// The output canvas a motion-graphics step designs for (docs/design/graphics-see-and-size.md): the
/// size the consuming compile encodes at and the legibility floor it enforces.
/// </summary>
public sealed record OverlayCanvas(int Width, int Height, double MinCapHeightPct)
{
    /// <summary>The floor as a font size on this canvas (<see cref="OverlayTextSizing.FontPxForCap"/>).</summary>
    public int MinFontPx => OverlayTextSizing.FontPxForCap(Height, MinCapHeightPct);

    /// <summary>Whether a composition of <paramref name="width"/> x <paramref name="height"/> has this canvas's shape (within 2%).</summary>
    public bool HasShapeOf(int width, int height) =>
        width > 0 && height > 0 && Width > 0 && Height > 0 &&
        Math.Abs(width / (double)height - Width / (double)Height) <= 0.02 * (Width / (double)Height);
}

/// <summary>
/// What the render tool measured about one rendered graphic: whether its smallest text reaches the
/// floor once composited on the canvas. Written by the engine only — never by a model.
/// </summary>
public sealed record OverlayLegibilityRecord(
    string StorageKey,
    string CompositionId,
    int CompositionWidth,
    int CompositionHeight,
    int CanvasWidth,
    int CanvasHeight,
    double FloorCapPct,
    int MinFontPx,
    bool Measured,
    bool Passed,
    double? SmallestFontPx = null,
    string? Reason = null)
{
    /// <summary>Unmeasured: the composition is not the canvas's shape (a screen insert or a legacy band asset).</summary>
    public const string NotCanvasSized = "not_canvas_sized";

    /// <summary>Unmeasured: the layout lint could not run or measured no frame.</summary>
    public const string LintUnavailable = "layout_lint_unavailable";

    /// <summary>The smallest measured text's cap height in percent of the canvas height, when known.</summary>
    public double? SmallestCapPct => SmallestFontPx is { } px ? OverlayTextSizing.CapPctForFontPx(px, CanvasHeight) : null;

    /// <summary>The record as the JSON object written into the graphics step's output and the render result.</summary>
    public JsonObject ToJson() => new()
    {
        ["storageKey"] = StorageKey,
        ["compositionId"] = CompositionId,
        ["compositionWidth"] = CompositionWidth,
        ["compositionHeight"] = CompositionHeight,
        ["canvasWidth"] = CanvasWidth,
        ["canvasHeight"] = CanvasHeight,
        ["floorCapPct"] = FloorCapPct,
        ["minFontPx"] = MinFontPx,
        ["measured"] = Measured,
        ["passed"] = Passed,
        ["smallestFontPx"] = SmallestFontPx,
        ["smallestCapPct"] = SmallestCapPct,
        ["reason"] = Reason
    };

    /// <summary>Reads a record written by <see cref="ToJson"/>; null for anything else.</summary>
    public static OverlayLegibilityRecord? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o)
            return null;

        try
        {
            string? key = o["storageKey"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key))
                return null;

            return new OverlayLegibilityRecord(
                key,
                o["compositionId"]?.GetValue<string>() ?? string.Empty,
                o["compositionWidth"]?.GetValue<int>() ?? 0,
                o["compositionHeight"]?.GetValue<int>() ?? 0,
                o["canvasWidth"]?.GetValue<int>() ?? 0,
                o["canvasHeight"]?.GetValue<int>() ?? 0,
                o["floorCapPct"]?.GetValue<double>() ?? 0,
                o["minFontPx"]?.GetValue<int>() ?? 0,
                o["measured"]?.GetValue<bool>() ?? false,
                o["passed"]?.GetValue<bool>() ?? false,
                o["smallestFontPx"]?.GetValue<double?>(),
                o["reason"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The sentence the render result gives the agent about this record.</summary>
    public string Message()
    {
        string floor = OverlayTextSizing.Format(FloorCapPct);
        if (!Measured)
        {
            return Reason == NotCanvasSized
                ? $"Legibility not measured: this composition is {CompositionWidth}x{CompositionHeight}, not the {CanvasWidth}x{CanvasHeight} " +
                  "output canvas. An overlay graphic must be rendered on the full canvas (assetCanvas \"Output\"); a screen insert is fine as it is."
                : "Legibility not measured: the layout lint could not run. Check the type size with RenderPreviewStills.";
        }

        if (Passed)
            return $"Legible: no text is below {MinFontPx}px on the {CanvasWidth}x{CanvasHeight} canvas (capitals at least {floor}% of the height).";

        string smallest = SmallestFontPx is { } px
            ? $"the smallest text is {px.ToString("0.#", CultureInfo.InvariantCulture)}px (capitals about {OverlayTextSizing.Format(SmallestCapPct ?? 0)}% of the height)"
            : "some text is too small";
        return $"Below the legibility floor: {smallest}; every line must be at least {MinFontPx}px on the {CanvasWidth}x{CanvasHeight} canvas " +
               $"(capitals {floor}% of the height). Make the type bigger, then render again with force: true and a forceReason. " +
               "An overlay left below the floor is replaced by its plain text at compile.";
    }
}

/// <summary>
/// The legibility side channel of one motion-graphics step: the canvas and floor the step designs
/// for (set by <c>MotionGraphicsPlacementAnnotator</c>) and what the render tool measured for each
/// rendered graphic. One per step context, published to the render tool around the agent run the
/// same way <see cref="RenderFailureLedger"/> is. Unconfigured (every non-graphics step) it records
/// nothing and the render tool measures nothing.
/// </summary>
public sealed class OverlayLegibilityLedger
{
    private static readonly AsyncLocal<OverlayLegibilityLedger?> Ambient = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, OverlayLegibilityRecord> _byKey = new(StringComparer.Ordinal);
    private OverlayCanvas? _canvas;

    /// <summary>The ledger of the step this async flow belongs to, if any.</summary>
    public static OverlayLegibilityLedger? Current => Ambient.Value;

    /// <summary>The canvas and floor, once configured.</summary>
    public OverlayCanvas? Canvas { get { lock (_gate) return _canvas; } }

    /// <summary>Sets the canvas and floor this step designs for.</summary>
    public void Configure(OverlayCanvas canvas)
    {
        lock (_gate) _canvas = canvas;
    }

    /// <summary>Records (or replaces) the measurement of one rendered graphic.</summary>
    public void Record(OverlayLegibilityRecord record)
    {
        lock (_gate) _byKey[record.StorageKey] = record;
    }

    /// <summary>The measurement recorded for <paramref name="storageKey"/>, if any.</summary>
    public OverlayLegibilityRecord? Find(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return null;
        lock (_gate) return _byKey.TryGetValue(storageKey, out OverlayLegibilityRecord? r) ? r : null;
    }

    /// <summary>Makes this ledger visible to the tool calls made inside the returned scope.</summary>
    public IDisposable Publish()
    {
        OverlayLegibilityLedger? previous = Ambient.Value;
        Ambient.Value = this;
        return new Restore(previous);
    }

    private sealed class Restore(OverlayLegibilityLedger? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
