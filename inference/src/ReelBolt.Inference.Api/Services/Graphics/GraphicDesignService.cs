using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Graphics;

/// <summary>
/// The request side of designed graphics for the picture editor (docs/editor.md "Designed graphics"):
/// validates a request, prices it, turns an accepted one into an <see cref="EditImageGraphicRequest"/> row
/// plus an execution of the project's hidden <see cref="WorkflowDefinitionKind.GraphicDesign"/> workflow,
/// and SETTLES finished requests — inserts the engine's verified PNG as a layer of the document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorization is the caller's job</b>, exactly as for <c>CompositionRevisionService</c>: every method
/// takes the <see cref="Project"/> the caller already resolved through <c>IProjectAccessService</c>, and
/// every query here is constrained by that project's id and then by the document — the guard for this
/// unfiltered child table (<c>TenantBoundaryInventoryTests</c>). A document in another project is "not
/// found", whoever asks.
/// </para>
/// <para>
/// <b>Why settle on read.</b> The engine renders, verifies and uploads the PNG and writes the result onto the
/// request row; it does not map the layer document at all. The API inserts the layer the next time anyone
/// reads the request (the editor's list poll, the assistant's description of the document), under the
/// document's own layer-stack rules, once per request: <c>ResultLayerId</c> is a concurrency token, so a
/// second concurrent settle matches no row and inserts nothing.
/// </para>
/// </remarks>
public interface IGraphicDesignService
{
    /// <summary>The document's requests (settling finished ones first), the cost of one more, and availability. Null when the document is not in <paramref name="project"/>.</summary>
    Task<ImageGraphicsResponse?> ListAsync(Project project, Guid documentId, Guid? organizationId, CancellationToken ct);

    /// <summary>One request (settled first when finished). Null when it is not a request of this project's document.</summary>
    Task<ImageGraphicJobDto?> GetAsync(Project project, Guid documentId, Guid graphicId, CancellationToken ct);

    /// <summary>Validate and resolve a request WITHOUT writing anything (the assistant's proposal uses it).</summary>
    Task<GraphicDesignPlanResult> PlanAsync(Project project, Guid documentId, CreateImageGraphicRequest body, CancellationToken ct);

    /// <summary>Validate, rate-limit, preflight and queue a request.</summary>
    /// <exception cref="PlanLimitException">When the plan's concurrency cap refuses the run.</exception>
    Task<GraphicDesignRequestResult> RequestAsync(
        Project project, Guid documentId, Guid userId, Guid? organizationId, CreateImageGraphicRequest body,
        ExecutionInitiator initiator, CancellationToken ct);

    /// <summary>
    /// What one graphic is expected to cost for this organization, priced on the hidden workflow of
    /// <paramref name="projectId"/> when it exists (the step the request preflights); null when billing is
    /// off or it cannot be priced.
    /// </summary>
    Task<ImageGraphicEstimateDto?> EstimateAsync(Guid? organizationId, Guid? projectId, CancellationToken ct);

    /// <summary>Insert every finished, unsettled graphic of the document as a layer. Returns how many were settled.</summary>
    Task<int> SettleAsync(Project project, Guid documentId, CancellationToken ct);
}

/// <summary>A validated, resolved request: everything a write needs, nothing written.</summary>
public sealed record GraphicDesignPlan(
    string Description,
    GraphicFrame Frame,
    EditImageLayer? RevisedLayer,
    Guid? BaseVersionId,
    int CanvasWidth,
    int CanvasHeight);

/// <summary>A plan, or a refusal the caller renders verbatim.</summary>
public sealed record GraphicDesignPlanResult(GraphicDesignPlan? Plan, int StatusCode, string? Code, string? Message)
{
    public static GraphicDesignPlanResult Refuse(int status, string? code, string message) => new(null, status, code, message);
}

/// <summary>The outcome of a graphic request: created, or a refusal the controller renders verbatim.</summary>
public sealed record GraphicDesignRequestResult(
    ImageGraphicJobDto? Created,
    int StatusCode,
    string? Code,
    string? Message,
    IReadOnlyDictionary<string, object?>? Extra = null)
{
    public static GraphicDesignRequestResult Ok(ImageGraphicJobDto created) => new(created, 201, null, null);

    public static GraphicDesignRequestResult Refuse(int status, string? code, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        new(null, status, code, message, extra);
}

/// <summary>Request-side knobs (<c>GraphicDesign:*</c>).</summary>
public sealed class GraphicDesignOptions
{
    public const string SectionName = "GraphicDesign";

    /// <summary>Graphic requests one user may make per project in a rolling hour.</summary>
    public int MaxPerHourPerUserProject { get; set; } = 20;
}

/// <inheritdoc />
public sealed class GraphicDesignService : IGraphicDesignService
{
    /// <summary>The name of the hidden per-project system workflow.</summary>
    public const string SystemWorkflowName = "Picture graphics";

    public const string NotRevisableCode = "GRAPHIC_NOT_REVISABLE";
    public const string UnavailableCode = "GRAPHIC_UNAVAILABLE";
    public const string RateLimitedCode = "GRAPHIC_RATE_LIMITED";
    public const string AgentUnavailableCode = "GRAPHIC_AGENT_UNAVAILABLE";

    /// <summary>The operation name a designed layer's provenance carries.</summary>
    public const string ProvenanceOperation = "designGraphic";

    private readonly InferenceApiDbContext _db;
    private readonly WorkflowExecutionService _executions;
    private readonly IFileStorageService _storage;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly IInferenceProviderResolver? _providers;
    private readonly GraphicDesignOptions _options;
    private readonly ILogger<GraphicDesignService> _logger;

    public GraphicDesignService(
        InferenceApiDbContext db,
        WorkflowExecutionService executions,
        IFileStorageService storage,
        IConfiguration configuration,
        ILogger<GraphicDesignService> logger,
        IWorkflowCostEstimator? costEstimator = null,
        IInferenceProviderResolver? providers = null)
    {
        _providers = providers;
        _db = db;
        _executions = executions;
        _storage = storage;
        _costEstimator = costEstimator;
        _options = configuration.GetSection(GraphicDesignOptions.SectionName).Get<GraphicDesignOptions>() ?? new GraphicDesignOptions();
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    public async Task<ImageGraphicsResponse?> ListAsync(Project project, Guid documentId, Guid? organizationId, CancellationToken ct)
    {
        if (!await DocumentExistsAsync(project.Id, documentId, ct))
            return null;

        await SettleAsync(project, documentId, ct);

        List<EditImageGraphicRequest> rows = await _db.EditImageGraphicRequests.AsNoTracking()
            .Where(r => r.ProjectId == project.Id && r.DocumentId == documentId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        IReadOnlyList<ImageGraphicJobDto> jobs = await MapAsync(project.Id, documentId, rows, ct);
        bool seeded = await DesignerSeededAsync(ct);
        bool chat = !seeded || await ChatProviderReadyAsync(ct);
        bool available = seeded && chat;
        // Billing off is an answer ("nothing is charged"), not a missing estimate.
        ImageGraphicEstimateDto? estimate = available ? await EstimateAsync(organizationId, project.Id, ct) : null;
        if (available && estimate is null && (_costEstimator is null || !_costEstimator.BillingEnabled))
            estimate = ImageGraphicEstimateDto.From(false, 0, null);
        return new ImageGraphicsResponse(
            jobs,
            estimate,
            available,
            available ? null : seeded ? NoChatProviderMessage : "Designing graphics is not available on this installation yet.",
            available ? null : seeded ? "NO_PROVIDER" : "DISABLED");
    }

    public async Task<ImageGraphicJobDto?> GetAsync(Project project, Guid documentId, Guid graphicId, CancellationToken ct)
    {
        EditImageGraphicRequest? row = await _db.EditImageGraphicRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == graphicId && r.ProjectId == project.Id && r.DocumentId == documentId, ct);
        if (row is null || !await DocumentExistsAsync(project.Id, documentId, ct))
            return null;

        if (row.ResultStorageKey is not null && row.ResultLayerId is null)
        {
            await SettleAsync(project, documentId, ct);
            row = await _db.EditImageGraphicRequests.AsNoTracking().FirstAsync(r => r.Id == graphicId, ct);
        }

        return (await MapAsync(project.Id, documentId, [row], ct))[0];
    }

    // ------------------------------------------------------------------
    // Requesting
    // ------------------------------------------------------------------

    public async Task<GraphicDesignPlanResult> PlanAsync(Project project, Guid documentId, CreateImageGraphicRequest body, CancellationToken ct)
    {
        string description = (body.Description ?? string.Empty).Trim();
        List<string> errors = [];
        if (description.Length == 0)
            errors.Add(body.RevisesLayerId is null ? "Describe the graphic you want." : "Say what should change in the graphic.");
        if (description.Length > GraphicDesignRules.MaxDescriptionLength)
            errors.Add($"The description can be at most {GraphicDesignRules.MaxDescriptionLength} characters.");

        string? placement = GraphicDesignRules.NormalisePlacement(body.Placement);
        if (placement is null)
            errors.Add($"placement must be one of: {string.Join(", ", GraphicDesignRules.Placements)}.");

        if ((body.Width is null) != (body.Height is null))
            errors.Add("width and height must be sent together.");
        else if (body.Width is { } w && body.Height is { } h
                 && (w is < GraphicDesignRules.MinSide or > GraphicDesignRules.MaxSide || h is < GraphicDesignRules.MinSide or > GraphicDesignRules.MaxSide))
            errors.Add($"width and height must each be between {GraphicDesignRules.MinSide} and {GraphicDesignRules.MaxSide} pixels.");

        if ((body.CanvasWidth is null) != (body.CanvasHeight is null))
            errors.Add("canvasWidth and canvasHeight must be sent together.");
        else if (body.CanvasWidth is { } cw && body.CanvasHeight is { } ch
                 && (cw is < EditImageDocumentSupport.MinCanvasSide or > EditImageDocumentSupport.MaxCanvasSide
                     || ch is < EditImageDocumentSupport.MinCanvasSide or > EditImageDocumentSupport.MaxCanvasSide))
            errors.Add($"canvasWidth and canvasHeight must each be between {EditImageDocumentSupport.MinCanvasSide} and {EditImageDocumentSupport.MaxCanvasSide}.");

        if (body.RevisesLayerId is not null && body.Width is not null)
            errors.Add("A graphic being changed keeps its own size; do not send width and height with revisesLayerId.");

        if (errors.Count > 0)
            return GraphicDesignPlanResult.Refuse(400, null, string.Join(" ", errors));

        EditImageDocument? document = await EditImageDocumentSupport.LoadDocumentAsync(_db, project.Id, documentId, ct, tracked: false);
        if (document is null)
            return GraphicDesignPlanResult.Refuse(404, null, "Picture not found.");

        (int canvasWidth, int canvasHeight) = await PictureSizes.CanvasAsync(_storage, project.Id, document, body.CanvasWidth, body.CanvasHeight, ct);

        if (body.RevisesLayerId is { } layerId)
        {
            EditImageLayer? layer = document.Layers.FirstOrDefault(l => l.Id == layerId);
            if (layer is null)
                return GraphicDesignPlanResult.Refuse(404, null, "That layer is not in this picture.");

            DesignedProvenance? provenance = DesignedProvenance.Read(layer.ProvenanceJson);
            if (provenance?.WorkspaceVersionId is not { } versionId)
            {
                return GraphicDesignPlanResult.Refuse(409, NotRevisableCode,
                    $"\"{layer.Label}\" was not made by the graphic designer (or its design was not saved), so it cannot be changed this way. Design a new graphic instead.");
            }

            CompositionWorkspaceVersion? version = await _db.CompositionWorkspaceVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == versionId && v.ProjectId == project.Id, ct);
            if (version is null || version.Source != CompositionVersionSource.GraphicDesign
                || version.Width is not { } vw || version.Height is not { } vh)
            {
                return GraphicDesignPlanResult.Refuse(409, NotRevisableCode,
                    $"The saved design of \"{layer.Label}\" is no longer available, so it cannot be changed. Design a new graphic instead.");
            }

            EditImageLayerTransformDto t = EditImageDocumentSupport.ReadTransform(layer.TransformJson);
            double scale = t.Scale;
            double frameX = t.X - (provenance.CropX * scale);
            double frameY = t.Y - (provenance.CropY * scale);
            GraphicRegion target = new(t.X, t.Y, (layer.Width ?? vw) * scale, (layer.Height ?? vh) * scale);
            GraphicFrame frame = new(vw, vh, frameX, frameY, scale, target, Sized: false, placement!);
            return new GraphicDesignPlanResult(
                new GraphicDesignPlan(description, frame, layer, versionId, canvasWidth, canvasHeight), 200, null, null);
        }

        GraphicFrame resolved = GraphicDesignRules.ResolveFrame(canvasWidth, canvasHeight, body.Width, body.Height, placement!);
        return new GraphicDesignPlanResult(
            new GraphicDesignPlan(description, resolved, null, null, canvasWidth, canvasHeight), 200, null, null);
    }

    public async Task<GraphicDesignRequestResult> RequestAsync(
        Project project, Guid documentId, Guid userId, Guid? organizationId, CreateImageGraphicRequest body,
        ExecutionInitiator initiator, CancellationToken ct)
    {
        GraphicDesignPlanResult planned = await PlanAsync(project, documentId, body, ct);
        if (planned.Plan is not { } plan)
            return GraphicDesignRequestResult.Refuse(planned.StatusCode, planned.Code, planned.Message ?? "The graphic could not be requested.");

        // A restore of this project is rewriting it: no run starts until it is done.
        if (await RestoreRunGate.IsHeldAsync(_db, project.Id, ct))
            return GraphicDesignRequestResult.Refuse(409, ReelBolt.Inference.Api.Services.History.ProjectHistoryRestoreCodes.RestoreInProgress, RestoreRunGate.Message);

        // Per user + project, rolling hour — counted from the request rows, so the limit holds across replicas.
        DateTime since = DateTime.UtcNow.AddHours(-1);
        List<DateTime> recent = await _db.EditImageGraphicRequests.AsNoTracking()
            .Where(r => r.ProjectId == project.Id && r.RequestedByUserId == userId && r.CreatedAt >= since)
            .Select(r => r.CreatedAt)
            .ToListAsync(ct);
        int limit = Math.Max(1, _options.MaxPerHourPerUserProject);
        if (recent.Count >= limit)
        {
            int retryAfter = Math.Max(1, (int)Math.Ceiling((recent.Min().AddHours(1) - DateTime.UtcNow).TotalSeconds));
            return GraphicDesignRequestResult.Refuse(
                429, RateLimitedCode,
                $"Graphic limit reached — {recent.Count} of {limit} graphics in this project in the last hour; try again in {RunRateRefusal.Wait(retryAfter)}.",
                new Dictionary<string, object?>
                {
                    ["used"] = recent.Count,
                    ["limit"] = limit,
                    ["windowMinutes"] = 60,
                    ["retryAfterSeconds"] = Math.Max(1, retryAfter)
                });
        }

        if (!await ChatProviderReadyAsync(ct))
            return GraphicDesignRequestResult.Refuse(503, AgentUnavailableCode, NoChatProviderMessage);

        WorkflowDefinition? workflow = await EnsureSystemWorkflowAsync(project.Id, ct);
        if (workflow is null)
        {
            return GraphicDesignRequestResult.Refuse(
                503, AgentUnavailableCode, "Designing graphics is not available on this installation yet. Please try again later.");
        }

        // The same B7 preflight as every other execute path, against the step the run will hold for.
        WorkflowExecutionPreflightResult preflight = await new WorkflowExecutionPreflight(_db, _costEstimator)
            .CheckAsync(organizationId, project.Id, workflow.Id, ct);
        if (!preflight.Allowed && !preflight.WorkflowNotFound)
        {
            return GraphicDesignRequestResult.Refuse(
                402, "INSUFFICIENT_CREDITS",
                CreditShortfallMessage.Build(preflight),
                new Dictionary<string, object?>
                {
                    ["required"] = preflight.Required,
                    ["balance"] = preflight.Balance,
                    ["missing"] = CreditShortfallMessage.MissingCredits(preflight),
                    ["low"] = preflight.Low,
                    ["high"] = preflight.High,
                    ["hasHistory"] = preflight.HasHistory
                });
        }

        Guid executionId = Guid.NewGuid();
        GraphicFrame frame = plan.Frame;
        EditImageGraphicRequest row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            DocumentId = documentId,
            ExecutionId = executionId,
            WorkflowDefinitionId = workflow.Id,
            Description = plan.Description,
            Placement = frame.Placement,
            FrameWidth = frame.Width,
            FrameHeight = frame.Height,
            Sized = frame.Sized,
            FrameX = frame.X,
            FrameY = frame.Y,
            FrameScale = frame.Scale,
            TargetX = frame.Target.X,
            TargetY = frame.Target.Y,
            TargetWidth = frame.Target.Width,
            TargetHeight = frame.Target.Height,
            RevisesLayerId = plan.RevisedLayer?.Id,
            BaseVersionId = plan.BaseVersionId,
            RequestedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        // The request row lands BEFORE the run is queued, so the engine can never consume an execution
        // whose request it cannot find; a refused queue removes it again.
        _db.EditImageGraphicRequests.Add(row);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _executions.QueueAsync(
                project.Id, workflow.Id, userId, plan.Description, initiator, ct,
                regenerateVideoClips: false, runChoices: null, WorkflowDefinitionKind.GraphicDesign, executionId);
        }
        catch
        {
            _db.EditImageGraphicRequests.Remove(row);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        _logger.LogInformation(
            "Graphic {GraphicId} queued as execution {ExecutionId} for document {DocumentId} ({FrameWidth}x{FrameHeight}, revises {RevisesLayerId})",
            row.Id, executionId, documentId, frame.Width, frame.Height, row.RevisesLayerId);

        return GraphicDesignRequestResult.Ok((await MapAsync(project.Id, documentId, [row], ct))[0]);
    }

    /// <remarks>
    /// Priced like the request's preflight prices it: the project's hidden workflow, when it exists, through
    /// <see cref="WorkflowExecutionPreflight.PriceAsync"/> (the call <c>RequestAsync</c>'s check makes); else
    /// the step that workflow WILL have (<see cref="DesignStep"/>: the GraphicDesigner agent, so the
    /// designer's own history and cold price apply). Never creates the workflow.
    /// </remarks>
    public async Task<ImageGraphicEstimateDto?> EstimateAsync(Guid? organizationId, Guid? projectId, CancellationToken ct)
    {
        if (_costEstimator is null || !_costEstimator.BillingEnabled)
            return null;
        try
        {
            WorkflowPreflightResult? preflight = null;
            if (projectId is { } project
                && await _db.WorkflowDefinitions.AsNoTracking()
                    .Where(w => w.ProjectId == project && w.Kind == WorkflowDefinitionKind.GraphicDesign)
                    .Select(w => (Guid?)w.Id)
                    .FirstOrDefaultAsync(ct) is { } workflowId)
            {
                preflight = await new WorkflowExecutionPreflight(_db, _costEstimator)
                    .PriceAsync(organizationId, project, workflowId, null, ct);
            }

            if (preflight is null)
            {
                Guid designerId = await DesignerAgentIdAsync(ct) ?? Guid.Empty;
                preflight = await _costEstimator.PreflightAsync(organizationId, [DesignStep(Guid.Empty, designerId)], ct);
            }

            WorkflowCostEstimate cost = preflight.Estimate;
            // Not evaluated: no balance to compare (and perhaps no estimate), so no figures to show; the
            // approval card then says the cost could not be worked out.
            if (!cost.BillingEnabled || !preflight.Evaluated)
                return null;
            return new ImageGraphicEstimateDto(
                true,
                cost.LowMillis / 1000.0,
                cost.HighMillis / 1000.0,
                cost.RequiredMillis / 1000.0,
                preflight.BalanceMillis / 1000.0,
                !preflight.Refused);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not estimate a graphic for organization {OrganizationId}", organizationId);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Settling
    // ------------------------------------------------------------------

    public async Task<int> SettleAsync(Project project, Guid documentId, CancellationToken ct)
    {
        List<EditImageGraphicRequest> pending = await _db.EditImageGraphicRequests.AsNoTracking()
            .Where(r => r.ProjectId == project.Id && r.DocumentId == documentId
                        && r.ResultStorageKey != null && r.ResultLayerId == null)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        if (pending.Count == 0)
            return 0;

        List<Guid> executionIds = [.. pending.Select(r => r.ExecutionId)];
        Dictionary<Guid, ExecutionStatus> statuses = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => executionIds.Contains(e.Id) && e.ProjectId == project.Id)
            .ToDictionaryAsync(e => e.Id, e => e.Status, ct);

        int settled = 0;
        foreach (EditImageGraphicRequest candidate in pending)
        {
            // Only a run that finished: the engine records the result as its last act, and a run that
            // then failed (its credit settle, say) is not a delivered graphic.
            if (!statuses.TryGetValue(candidate.ExecutionId, out ExecutionStatus status) || status != ExecutionStatus.Passed)
                continue;

            // The key is the engine's, and the engine is not trusted with naming another document's
            // object: anything but this document's own graphic key is never made a layer.
            if (!GraphicDesignRules.IsGraphicKey(candidate.ResultStorageKey, project.Id, documentId))
            {
                _logger.LogWarning(
                    "Graphic {GraphicId} recorded a result key outside its document's prefix; not settled", candidate.Id);
                continue;
            }

            if (await SettleOneAsync(project, documentId, candidate.Id, ct))
                settled++;
        }

        return settled;
    }

    private async Task<bool> SettleOneAsync(Project project, Guid documentId, Guid requestId, CancellationToken ct)
    {
        EditImageGraphicRequest? row = await _db.EditImageGraphicRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ProjectId == project.Id && r.DocumentId == documentId, ct);
        EditImageDocument? document = await EditImageDocumentSupport.LoadDocumentAsync(_db, project.Id, documentId, ct);
        if (row is null || document is null || row.ResultLayerId is not null || row.ResultStorageKey is null)
            return false;

        // A graphic is judged by its run's first start and the stamp its result was recorded with, never by the time
        // it is settled: a result recorded before the project's output marker does not become a layer now.
        var run = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == row.ExecutionId && e.ProjectId == project.Id)
            .Select(e => new { e.StartedAt, e.FirstStartedAt })
            .FirstOrDefaultAsync(ct);
        ProjectOutputHoldState hold = await ProjectOutputHold.ReadAsync(_db, project.Id, ct);
        if (hold.IsActive
            && (run is null || hold.HoldsOutput(row.ResultRecordedAt, ProjectOutputHold.RunStart(run.FirstStartedAt, run.StartedAt))))
        {
            _logger.LogWarning("Graphic {GraphicId} was not settled into a layer", row.Id);
            return false;
        }

        DateTime now = DateTime.UtcNow;
        EditImageLayer? replaced = row.RevisesLayerId is { } replacedId ? document.Layers.FirstOrDefault(l => l.Id == replacedId) : null;
        int width = row.ResultWidth ?? row.FrameWidth;
        int height = row.ResultHeight ?? row.FrameHeight;
        int cropX = row.ResultOffsetX ?? 0;
        int cropY = row.ResultOffsetY ?? 0;

        EditImageLayerTransformDto transform;
        if (replaced is not null)
        {
            // Where the person has moved the graphic since is kept: the new crop is placed relative to
            // the old one through the old layer's CURRENT transform.
            EditImageLayerTransformDto old = EditImageDocumentSupport.ReadTransform(replaced.TransformJson);
            DesignedProvenance? before = DesignedProvenance.Read(replaced.ProvenanceJson);
            double dx = (cropX - (before?.CropX ?? cropX)) * old.Scale;
            double dy = (cropY - (before?.CropY ?? cropY)) * old.Scale;
            transform = old with { X = old.X + dx, Y = old.Y + dy };
        }
        else
        {
            transform = new EditImageLayerTransformDto(
                X: row.FrameX + (cropX * row.FrameScale),
                Y: row.FrameY + (cropY * row.FrameScale),
                Scale: row.FrameScale);
        }

        string label = "Graphic: " + (row.Description.Length > 40 ? row.Description[..40].TrimEnd() + "..." : row.Description);
        EditImageLayer layer = new()
        {
            Id = Guid.NewGuid(),
            DocumentId = document.Id,
            Role = replaced?.Role ?? EditImageLayerRoles.Overlay,
            Kind = EditImageLayerKind.Generated,
            Label = replaced is not null && !replaced.Label.StartsWith("Graphic: ", StringComparison.Ordinal) ? replaced.Label : label,
            StorageKey = row.ResultStorageKey,
            StorageBucket = _storage.BucketName,
            MimeType = "image/png",
            Width = width,
            Height = height,
            // The layer being revised was deleted meanwhile: the paid result is kept, hidden, for the person to decide.
            Visible = !(row.RevisesLayerId is not null && replaced is null),
            Opacity = replaced?.Opacity ?? 1,
            BlendMode = replaced?.BlendMode ?? EditImageBlendModes.Normal,
            TransformJson = EditImageDocumentSupport.WriteTransform(transform),
            ProvenanceJson = JsonSerializer.Serialize(new
            {
                operation = ProvenanceOperation,
                providerKind = (string?)null,
                capability = InferenceProviderCapability.Chat.ToString(),
                model = row.ResultModelName,
                prompt = row.Description,
                vendorRequestId = (string?)null,
                parentLayerId = row.RevisesLayerId,
                graphicId = row.Id,
                executionId = row.ExecutionId,
                workspaceId = row.ResultWorkspaceId,
                workspaceVersionId = row.ResultVersionId,
                versionNumber = row.ResultVersionNumber,
                compositionId = row.ResultCompositionId,
                summary = row.ResultSummary,
                frameWidth = row.FrameWidth,
                frameHeight = row.FrameHeight,
                cropX,
                cropY
            }, EditImageDocumentSupport.JsonWithNulls),
            CreatedAt = now
        };

        EditImageDocumentSupport.InsertLayer(_db, document, layer, aboveLayerId: replaced?.Id);
        if (replaced is not null)
            replaced.Visible = false;

        row.ResultLayerId = layer.Id;
        row.SettledAt = now;
        document.Revision += 1;
        document.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Graphic {GraphicId} settled as layer {LayerId} of document {DocumentId}", row.Id, layer.Id, documentId);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another reader settled it first (or the document moved on under us): theirs stands, and
            // nothing this attempt staged may leak into a later save on the same context.
            _db.Entry(layer).State = EntityState.Detached;
            _db.Entry(row).State = EntityState.Detached;
            foreach (EditImageLayer existing in document.Layers.ToList())
                _db.Entry(existing).State = EntityState.Detached;
            _db.Entry(document).State = EntityState.Detached;
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private Task<bool> DocumentExistsAsync(Guid projectId, Guid documentId, CancellationToken ct) =>
        _db.EditImageDocuments.AnyAsync(d => d.Id == documentId && d.ProjectId == projectId, ct);

    /// <summary>
    /// What the user is told when no chat AI provider is set up: the designer is an AI agent and has nothing to run on,
    /// so a design would only queue and then fail.
    /// </summary>
    public const string NoChatProviderMessage =
        "Designing a graphic needs a chat AI provider, and this workspace has none set up yet. Ask an administrator to add one under AI providers.";

    /// <summary>
    /// Whether the designer agent has a chat model to run on: a configured provider (the org's choice or the default
    /// Chat capability) or the legacy endpoint-and-key settings. Unknown (no resolver wired, or the lookup failed) reads
    /// as ready: this only decides whether to warn up front, never whether a run is allowed to fail.
    /// </summary>
    private async Task<bool> ChatProviderReadyAsync(CancellationToken ct)
    {
        if (_providers is null) return true;
        try
        {
            ResolvedInferenceProvider resolved = await _providers.ResolveAsync(AgentType.GraphicDesigner, null, ct);
            return resolved.ProviderId is not null
                || (!string.IsNullOrWhiteSpace(resolved.Endpoint) && !string.IsNullOrWhiteSpace(resolved.ApiKey));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell whether a chat provider is set up for the graphic designer");
            return true;
        }
    }

    private Task<bool> DesignerSeededAsync(CancellationToken ct) =>
        _db.AgentDefinitions.AnyAsync(a => a.IsBuiltIn && a.AgentType == AgentType.GraphicDesigner, ct);

    /// <summary>
    /// The project's hidden graphics workflow: one <see cref="StepType.DesignGraphic"/> step driven by the
    /// built-in GraphicDesigner agent. Created on first use; null when the built-in agent row has not been
    /// seeded (an engine/API version skew), which the caller reports as a 503.
    /// </summary>
    private async Task<WorkflowDefinition?> EnsureSystemWorkflowAsync(Guid projectId, CancellationToken ct)
    {
        WorkflowDefinition? existing = await _db.WorkflowDefinitions
            .FirstOrDefaultAsync(w => w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.GraphicDesign, ct);
        if (existing is not null)
            return existing;

        Guid? agentId = await DesignerAgentIdAsync(ct);
        if (agentId is null)
            return null;

        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = SystemWorkflowName,
            ProjectId = projectId,
            Kind = WorkflowDefinitionKind.GraphicDesign,
            RequiresUserInput = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        workflow.Steps.Add(DesignStep(workflow.Id, agentId.Value));
        _db.WorkflowDefinitions.Add(workflow);
        await _db.SaveChangesAsync(ct);
        return workflow;
    }

    /// <summary>
    /// The hidden workflow's one step — shared by <see cref="EnsureSystemWorkflowAsync"/> (which stores it)
    /// and <see cref="EstimateAsync"/> (which prices it before the workflow exists), so the estimate prices
    /// the step the request's preflight will.
    /// </summary>
    private static WorkflowStep DesignStep(Guid workflowId, Guid designerAgentId) => new()
    {
        Id = workflowId == Guid.Empty ? Guid.Empty : Guid.NewGuid(),
        WorkflowDefinitionId = workflowId,
        AgentDefinitionId = designerAgentId,
        StepOrder = 1,
        StepType = StepType.DesignGraphic,
        Label = "Design the graphic",
        MaxIterations = 1
    };

    private Task<Guid?> DesignerAgentIdAsync(CancellationToken ct) =>
        _db.AgentDefinitions.AsNoTracking()
            .Where(a => a.IsBuiltIn && a.AgentType == AgentType.GraphicDesigner)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);

    private async Task<IReadOnlyList<ImageGraphicJobDto>> MapAsync(
        Guid projectId, Guid documentId, IReadOnlyList<EditImageGraphicRequest> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        List<Guid> executionIds = [.. rows.Select(r => r.ExecutionId)];
        var executions = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => executionIds.Contains(e.Id) && e.ProjectId == projectId)
            .Select(e => new { e.Id, e.Status, e.CompletedAt, e.ErrorMessage })
            .ToDictionaryAsync(e => e.Id, ct);

        // What each finished run was charged: the sum of its metered usage (billing off records none).
        Dictionary<Guid, long> chargedMillis = await _db.UsageEvents.AsNoTracking()
            .Where(u => u.ExecutionId != null && executionIds.Contains(u.ExecutionId.Value) && u.CreditsMillis > 0)
            .GroupBy(u => u.ExecutionId!.Value)
            .Select(g => new { Id = g.Key, Millis = g.Sum(u => u.CreditsMillis) })
            .ToDictionaryAsync(g => g.Id, g => g.Millis, ct);

        List<Guid> layerIds = [.. rows.Where(r => r.ResultLayerId is not null).Select(r => r.ResultLayerId!.Value)];
        Dictionary<Guid, EditImageLayer> layers = layerIds.Count == 0
            ? []
            : await _db.EditImageLayers.AsNoTracking()
                .Where(l => layerIds.Contains(l.Id) && l.DocumentId == documentId)
                .ToDictionaryAsync(l => l.Id, ct);

        // A designed layer revised from a layer that has since gone, still hidden: it needs the person's call.
        List<Guid> revisedIds = [.. rows.Where(r => r.RevisesLayerId is not null && r.ResultLayerId is not null).Select(r => r.RevisesLayerId!.Value)];
        HashSet<Guid> revisedAlive = revisedIds.Count == 0
            ? []
            : [.. await _db.EditImageLayers.AsNoTracking()
                .Where(l => revisedIds.Contains(l.Id) && l.DocumentId == documentId)
                .Select(l => l.Id)
                .ToListAsync(ct)];

        return [.. rows.Select(r =>
        {
            executions.TryGetValue(r.ExecutionId, out var e);
            ExecutionStatus status = e?.Status ?? ExecutionStatus.Queued;
            EditImageLayer? layer = r.ResultLayerId is { } id && layers.TryGetValue(id, out EditImageLayer? found) ? found : null;
            return new ImageGraphicJobDto(
                r.Id,
                r.DocumentId,
                r.ExecutionId,
                r.WorkflowDefinitionId,
                status.ToString(),
                r.Description,
                r.FrameWidth,
                r.FrameHeight,
                r.Placement,
                new ImageGraphicRegionDto(r.TargetX, r.TargetY, r.TargetWidth, r.TargetHeight),
                r.RevisesLayerId,
                layer?.Id,
                layer is null ? null : EditImageDocumentSupport.ToDto(layer, documentId, projectId),
                r.ResultSummary,
                status is ExecutionStatus.Failed or ExecutionStatus.Cancelled
                    ? e?.ErrorMessage ?? (status == ExecutionStatus.Cancelled ? "The graphic was stopped before it was finished." : "The graphic could not be designed.")
                    : null,
                r.CreatedAt,
                e?.CompletedAt)
            {
                ChargedCredits = status is ExecutionStatus.Passed or ExecutionStatus.Failed
                    && chargedMillis.TryGetValue(r.ExecutionId, out long millis) ? millis / 1000.0 : null,
                NeedsReview = layer is { Visible: false } && r.RevisesLayerId is { } revised && !revisedAlive.Contains(revised)
                    ? ImageNeedsReviewDto.ForLayerGone(layer.Id) : null
            };
        })];
    }
}

/// <summary>The parts of a designed layer's provenance the request and settle paths read back.</summary>
internal sealed record DesignedProvenance(Guid? WorkspaceVersionId, int CropX, int CropY, string? Prompt, string? Summary)
{
    public static DesignedProvenance? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("operation", out JsonElement op)
                || op.ValueKind != JsonValueKind.String
                || op.GetString() != GraphicDesignService.ProvenanceOperation)
            {
                return null;
            }

            Guid? version = root.TryGetProperty("workspaceVersionId", out JsonElement v) && v.ValueKind == JsonValueKind.String
                            && Guid.TryParse(v.GetString(), out Guid parsed)
                ? parsed
                : null;
            int cropX = root.TryGetProperty("cropX", out JsonElement x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out int xi) ? xi : 0;
            int cropY = root.TryGetProperty("cropY", out JsonElement y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out int yi) ? yi : 0;
            string? prompt = root.TryGetProperty("prompt", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            string? summary = root.TryGetProperty("summary", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return new DesignedProvenance(version, cropX, cropY, prompt, summary);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
