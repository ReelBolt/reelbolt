using System.Text.Json.Nodes;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The <c>outputResolution</c> node every render reports when the run chose a resolution:
/// <c>{"requested":"2160p","width":3840,"height":2160,"honoured":true}</c>, plus <c>"reason"</c> when
/// the size produced does not reach the request. Absent when the run asked for Auto.
/// </summary>
public static class OutputResolutionReport
{
    /// <summary>
    /// Null when <paramref name="requestedLabel"/> is Auto/blank/unknown. <c>honoured</c> is whether the
    /// short side of <paramref name="width"/> x <paramref name="height"/> equals the request;
    /// <paramref name="reason"/> is written only when it does not.
    /// </summary>
    public static JsonObject? Build(string? requestedLabel, int width, int height, string? reason)
    {
        if (!OutputResolution.TryParse(requestedLabel, out int? requested) || requested is not { } want)
            return null;

        bool honoured = Math.Min(width, height) == want;
        var node = new JsonObject
        {
            ["requested"] = OutputResolution.Label(want),
            ["width"] = width,
            ["height"] = height,
            ["honoured"] = honoured
        };
        if (!honoured && !string.IsNullOrWhiteSpace(reason))
            node["reason"] = reason;
        return node;
    }

    /// <summary>
    /// A compile's report: the canvas it composed at, scaled down to <paramref name="encodedCap"/>
    /// (the plan cap's short side, when the output policy applied it) as the file really is.
    /// </summary>
    public static JsonObject? ForCompile(int? requestedShortSide, int canvasWidth, int canvasHeight, int? encodedCap)
    {
        int width = canvasWidth, height = canvasHeight;
        string? reason = null;
        if (encodedCap is { } cap && cap > 0 && Math.Min(width, height) > cap)
        {
            double scale = cap / (double)Math.Min(width, height);
            width = Even(width * scale);
            height = Even(height * scale);
            reason = $"your plan renders up to {OutputResolution.Label(cap)}";
        }

        return ForShortSide(requestedShortSide, width, height, reason);
    }

    private static int Even(double value) => (int)Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2;

    /// <summary>
    /// A timeline render's report. It never upscales past the timeline's own size, so a request above
    /// the produced short side is not honoured (and says why; the plan cap is the other cause).
    /// </summary>
    public static JsonObject? ForTimelineRender(int? requestedShortSide, int width, int height, bool planCapApplied) =>
        ForShortSide(requestedShortSide, width, height, planCapApplied
            ? "your plan's output cap lowered the render"
            : "timeline renders never upscale above the timeline's own size");

    /// <summary>
    /// A Remotion render's report: the composition scaled by <c>chosen / short side</c> (at most 16x, the
    /// same scale <c>ApplyOutputResolutionArgs</c> passes), then held to the plan's output cap by the
    /// post-pass. Null when the composition's size is unknown (the scale is then not applied either).
    /// </summary>
    public static JsonObject? ForRemotion(int? requestedShortSide, int compositionWidth, int compositionHeight, int planMaxHeight)
    {
        if (requestedShortSide is not { } chosen || compositionWidth <= 0 || compositionHeight <= 0)
            return null;

        double scale = Math.Min(16.0, chosen / (double)Math.Min(compositionWidth, compositionHeight));
        int width = (int)Math.Round(compositionWidth * scale), height = (int)Math.Round(compositionHeight * scale);
        JsonObject? node = ForCompile(chosen, width, height, planMaxHeight);
        if (node is { } n && n["honoured"]!.GetValue<bool>() == false && n["reason"] is null)
            n["reason"] = "the composition is too small to scale up to the requested size (16x limit)";
        return node;
    }

    /// <summary>
    /// A stream-copied compile's report: the file keeps the source's own pixels, so it carries the real
    /// (probed) source size and is not honoured unless the source already has the requested short side.
    /// </summary>
    public static JsonObject? ForStreamCopy(int? requestedShortSide, int sourceWidth, int sourceHeight) =>
        ForShortSide(requestedShortSide, sourceWidth, sourceHeight, "a stream copy keeps the source's own size");

    /// <summary>The report for a run's chosen short side (null = Auto = no node).</summary>
    public static JsonObject? ForShortSide(int? requestedShortSide, int width, int height, string? reason) =>
        requestedShortSide is { } side ? Build(OutputResolution.Label(side), width, height, reason) : null;
}

/// <summary>
/// The canvas of a <see cref="VideoOutputFormat.Source"/> compile once the run chose a resolution:
/// the canonical clip's aspect with the SHORT side at the choice (both sides even). An explicit
/// choice may upscale. No choice (or an unknown source size) leaves the canvas to the source.
/// </summary>
public static class SourceResolutionCanvas
{
    public static (int Width, int Height)? For(int sourceWidth, int sourceHeight, int? shortSide)
    {
        if (shortSide is not { } side || sourceWidth <= 0 || sourceHeight <= 0)
            return null;

        double scale = side / (double)Math.Min(sourceWidth, sourceHeight);
        return (Even(sourceWidth * scale), Even(sourceHeight * scale));
    }

    /// <summary>
    /// The compile's fixed canvas: a platform format's own (scaled to the choice), else — for Source
    /// with a choice — the canonical clip scaled to the choice, else null (source-sized).
    /// </summary>
    public static (int Width, int Height)? CanvasFor(VideoOutputFormat format, int? shortSide, VideoAnalysisMedia canonical) =>
        VideoCompileStepExecutor.FixedCanvasSize(format, shortSide)
        ?? (format == VideoOutputFormat.Source ? For(canonical.Width, canonical.Height, shortSide) : null);

    private static int Even(double value) => (int)Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2;
}
