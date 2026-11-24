namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// Body of <c>POST /api/v1/projects/{projectId}/image-documents/{documentId}/graphics</c>: ask the graphic
/// designer for one still graphic (docs/editor.md "Designed graphics").
/// </summary>
/// <param name="Description">What to design (1-2000 characters), or — with <paramref name="RevisesLayerId"/> — what to change.</param>
/// <param name="Placement">One of <c>center</c> (default), <c>top</c>, <c>bottom</c>, <c>left</c>, <c>right</c>, <c>topLeft</c>, <c>topRight</c>, <c>bottomLeft</c>, <c>bottomRight</c>.</param>
/// <param name="Width">The graphic's box in pixels (64-4096), with <paramref name="Height"/>; omit both to design on the whole picture.</param>
/// <param name="Height">See <paramref name="Width"/>.</param>
/// <param name="CanvasWidth">The canvas size the editor shows; used only when the document stores none (16-8192).</param>
/// <param name="CanvasHeight">See <paramref name="CanvasWidth"/>.</param>
/// <param name="RevisesLayerId">"Change this graphic": a layer this designer made.</param>
public sealed record CreateImageGraphicRequest(
    string? Description = null,
    string? Placement = null,
    int? Width = null,
    int? Height = null,
    int? CanvasWidth = null,
    int? CanvasHeight = null,
    Guid? RevisesLayerId = null);

/// <summary>A rectangle in canvas pixels.</summary>
public sealed record ImageGraphicRegionDto(double X, double Y, double Width, double Height);

/// <summary>One graphic request and the run that carries it out.</summary>
/// <param name="Status">The run's status: <c>Queued</c>, <c>Running</c>, <c>Passed</c>, <c>Failed</c> or <c>Cancelled</c>.</param>
/// <param name="Target">Where on the picture the graphic is expected to land (the editor's in-place progress region).</param>
/// <param name="LayerId">The layer the graphic became, once the request was settled.</param>
/// <param name="Layer">That layer, in the document's own shape, once settled.</param>
/// <param name="Error">A plain-language reason when the run failed.</param>
public sealed record ImageGraphicJobDto(
    Guid Id,
    Guid DocumentId,
    Guid ExecutionId,
    Guid WorkflowId,
    string Status,
    string Description,
    int FrameWidth,
    int FrameHeight,
    string Placement,
    ImageGraphicRegionDto Target,
    Guid? RevisesLayerId,
    Guid? LayerId,
    EditImageLayerDto? Layer,
    string? Summary,
    string? Error,
    DateTime CreatedAt,
    DateTime? CompletedAt)
{
    /// <summary>The credits the run settled at ("used N credits"); <c>null</c> until it has finished or when nothing was charged.</summary>
    public double? ChargedCredits { get; init; }

    /// <summary>
    /// <c>{ reason: "layerGone", layerId }</c> when the revised layer was deleted before the result landed: the result
    /// is a HIDDEN layer (<c>layerId</c>); show it or delete it. <c>null</c> otherwise (also once the person shows it).
    /// </summary>
    public ImageNeedsReviewDto? NeedsReview { get; init; }
}

/// <summary>
/// What one paid picture action is expected to cost, priced exactly as its preflight prices it. The figures are
/// ABOUT numbers (the settled charge follows what was actually produced). Shared by every picture tool, the
/// cut-out and the graphic designer: <c>Credits</c> is the figure to show ("about N credits").
/// </summary>
/// <remarks>
/// <c>BillingEnabled: false</c> means nothing is charged (all figures 0, <c>Affordable</c> true). A tool that cannot
/// run says why in <c>UnavailableReason</c> (plain language) and <c>Reason</c> (a code: <c>NO_PROVIDER</c>,
/// <c>DISABLED</c>, <c>MODEL_UNPRICED</c>).
/// </remarks>
public sealed record ImageGraphicEstimateDto(
    bool BillingEnabled,
    double LowCredits,
    double HighCredits,
    double RequiredCredits,
    double BalanceCredits,
    bool Affordable)
{
    /// <summary>About how many credits the action would hold or charge (the same figure as <see cref="RequiredCredits"/>).</summary>
    public double Credits => RequiredCredits;

    /// <summary>Plain-language reason the action cannot run, or <c>null</c>.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Machine code for <see cref="UnavailableReason"/> the UI maps to a message, or <c>null</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>A priced estimate of <paramref name="millis"/> millicredits against <paramref name="availableMillis"/>.</summary>
    public static ImageGraphicEstimateDto From(bool billingEnabled, long millis, long? availableMillis)
        => billingEnabled
            ? new(true, millis / 1000.0, millis / 1000.0, millis / 1000.0, (availableMillis ?? 0) / 1000.0,
                availableMillis is null || millis <= availableMillis)
            : new(false, 0, 0, 0, 0, true);
}

/// <summary><c>GET .../graphics</c>: the document's graphic requests, newest first, plus the cost of one more.</summary>
public sealed record ImageGraphicsResponse(
    IReadOnlyList<ImageGraphicJobDto> Graphics,
    ImageGraphicEstimateDto? Estimate,
    bool Available,
    string? UnavailableReason,
    string? UnavailableCode = null,
    bool CanConfigure = false);
