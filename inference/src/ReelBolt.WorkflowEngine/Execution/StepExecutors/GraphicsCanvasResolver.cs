using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Styles;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// The output canvas a motion-graphics step designs for, and how each source clip lands on it
/// (docs/design/graphics-see-and-size.md). Read from the compile step that will apply the plan — the
/// first later <see cref="StepType.VideoCompile"/> with graphics on — through the same run format and
/// resolution overrides the compile applies, so the size the agent is told is the size the film is
/// encoded at. Without such a compile the canvas is the largest analysed source and the floor the
/// default.
/// </summary>
public sealed record GraphicsCanvas(
    int Width,
    int Height,
    double MinCapHeightPct,
    VideoOutputFormat Format,
    VideoOutputFit Fit,
    int? CompileStepOrder)
{
    /// <summary>The ledger's view of this canvas.</summary>
    public OverlayCanvas ToOverlayCanvas() => new(Width, Height, MinCapHeightPct);

    /// <summary>Whether a source clip is scaled to cover the canvas (crop) rather than fitted inside it (pad).</summary>
    public bool Cover => Format != VideoOutputFormat.Source && Fit != VideoOutputFit.Pad;
}

/// <inheritdoc cref="GraphicsCanvas"/>
public static class GraphicsCanvasResolver
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The canvas for the motion-graphics step running in <paramref name="context"/>; null when no size can be known.</summary>
    public static GraphicsCanvas? Resolve(StepExecutionContext context, VideoAnalysisArtifact artifact)
    {
        (VideoCompileStepConfig? config, int? compileOrder) = FindConsumingCompile(context);
        if (config is not null)
            config = RunStyleOverrides.ApplyToCompile(config, context.Execution, out _);

        VideoOutputFormat format = config?.OutputFormat ?? VideoOutputFormat.Source;
        (int Width, int Height)? size = VideoCompileStepExecutor.FixedCanvasSize(format, RunStyleOverrides.OutputResolutionOf(context.Execution));
        if (size is null)
        {
            VideoAnalysisMedia? largest = LargestSource(artifact);
            if (largest is null)
                return null;
            size = (largest.Width, largest.Height);
        }

        return new GraphicsCanvas(
            size.Value.Width,
            size.Value.Height,
            config?.ClampedMinOverlayCapHeightPct ?? OverlayTextSizing.DefaultMinCapHeightPct,
            format,
            config?.OutputFit ?? VideoOutputFit.Crop,
            compileOrder);
    }

    /// <summary>The first later compile with graphics (a <c>GraphicsPlan</c> or <c>EnableGraphics</c>), with its order.</summary>
    internal static (VideoCompileStepConfig? Config, int? StepOrder) FindConsumingCompile(StepExecutionContext context)
    {
        foreach (Shared.Data.Models.WorkflowStep step in context.AllSteps
            .Where(s => s.StepType == StepType.VideoCompile && s.StepOrder > context.Step.StepOrder)
            .OrderBy(s => s.StepOrder))
        {
            if (string.IsNullOrWhiteSpace(step.VideoCompileConfigJson))
                continue;
            try
            {
                VideoCompileStepConfig? config = JsonSerializer.Deserialize<VideoCompileStepConfig>(step.VideoCompileConfigJson, ConfigJsonOptions);
                if (config is not null && (config.EnableGraphics || config.GraphicsPlan is not null))
                    return (config, step.StepOrder);
            }
            catch (JsonException)
            {
                // A config the compile itself will reject tells nothing about the canvas.
            }
        }

        return (null, null);
    }

    private static VideoAnalysisMedia? LargestSource(VideoAnalysisArtifact artifact)
    {
        IEnumerable<VideoAnalysisMedia> candidates = artifact.Sources is { Count: > 0 } sources
            ? sources.Select(s => s.Media)
            : [artifact.Media];
        return candidates
            .Where(m => m is not null && m.Width > 0 && m.Height > 0)
            .OrderByDescending(m => (long)m.Width * m.Height)
            .FirstOrDefault();
    }

    /// <summary>The media of source <paramref name="sourceIndex"/> (the artifact's own media for a single-source artifact).</summary>
    internal static VideoAnalysisMedia SourceMedia(VideoAnalysisArtifact artifact, int sourceIndex) =>
        artifact.Sources?.FirstOrDefault(s => s.SourceIndex == sourceIndex)?.Media ?? artifact.Media;

    /// <summary>
    /// <paramref name="placement"/>'s band on the canvas, in canvas fractions, through the same fit
    /// geometry the compile uses (centre crop or pad; a subject-placed crop is approximated by the
    /// centre). Null when the band falls outside the canvas.
    /// </summary>
    internal static VideoAnalysisRect? BandOnCanvas(VideoAnalysisArtifact artifact, VideoAnalysisPlacement placement, GraphicsCanvas canvas)
    {
        VideoAnalysisMedia source = SourceMedia(artifact, placement.SourceIndex);
        var canvasMedia = new VideoAnalysisMedia(0, 0, 1, canvas.Width, canvas.Height);
        if (source.Width == canvas.Width && source.Height == canvas.Height)
            return placement.Rect;

        VideoCompileStepExecutor.SourceCanvasFit fit = VideoCompileStepExecutor.SourceCanvasFit.For(source, canvasMedia, canvas.Cover);
        return VideoCompileStepExecutor.MapRectThroughFit(placement.Rect, fit, canvasMedia);
    }

    /// <summary>
    /// The <c>view.graphicsCanvas</c> object the agents read: the canvas, the floor, what each
    /// <c>textSize</c> word stands for in pixels, and each offered placement's band in canvas pixels.
    /// Numbers are for reading: the plan still carries only words.
    /// </summary>
    internal static JsonObject BuildViewNode(VideoAnalysisArtifact artifact, GraphicsCanvas canvas)
    {
        var sizes = new JsonObject();
        foreach (string word in OverlayTextSizing.TextSizeWords)
        {
            double cap = OverlayTextSizing.EffectiveCapHeightPct(word, canvas.MinCapHeightPct)!.Value;
            sizes[word] = new JsonObject
            {
                ["capPx"] = OverlayTextSizing.CapPx(canvas.Height, cap),
                ["fontPx"] = OverlayTextSizing.FontPxForCap(canvas.Height, cap)
            };
        }

        HashSet<string> offered = new(artifact.OfferedPlacementIds ?? [], StringComparer.Ordinal);
        var bands = new JsonArray();
        foreach (VideoAnalysisPlacement placement in artifact.Placements ?? [])
        {
            if (!offered.Contains(placement.Id) || BandOnCanvas(artifact, placement, canvas) is not { } band)
                continue;
            bands.Add(new JsonObject
            {
                ["id"] = placement.Id,
                ["x"] = (int)Math.Round(band.X * canvas.Width),
                ["y"] = (int)Math.Round(band.Y * canvas.Height),
                ["w"] = (int)Math.Round(band.W * canvas.Width),
                ["h"] = (int)Math.Round(band.H * canvas.Height)
            });
        }

        return new JsonObject
        {
            ["width"] = canvas.Width,
            ["height"] = canvas.Height,
            ["minCapHeightPct"] = canvas.MinCapHeightPct,
            ["minFontPx"] = OverlayTextSizing.FontPxForCap(canvas.Height, canvas.MinCapHeightPct),
            ["textSizes"] = sizes,
            ["bands"] = bands,
            ["note"] =
                "The film is encoded on this canvas. Render every overlay graphic as a transparent composition of exactly " +
                $"{canvas.Width}x{canvas.Height} and set assetCanvas to \"Output\"; place the type inside the chosen placement's band " +
                "(bands are in canvas pixels). No text may be smaller than minFontPx (capitals minCapHeightPct of the height); " +
                "textSizes says what each textSize word means here. These numbers are for reading only: the plan carries words."
        };
    }
}
