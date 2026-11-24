using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// <c>StepType.ImageGenerate</c> — deterministic, non-LLM, PAID: makes <c>Variations</c> pictures from a prompt
/// (Generate, an ImageGeneration provider) or one picture from a source picture and an instruction (Edit, an ImageEdit
/// provider, which uploads the source to the vendor and so needs the author's egress consent). Every picture is one
/// ledger row in <c>external_generation_jobs</c> through the SAME atomic budget gate VideoGenerate and AudioGenerate use
/// (scope <see cref="VideoGenerationBudgetScope.Image"/>: the step cap <c>MaxSpendCredits</c> only, clip id
/// <c>img-{k}</c>; all pictures or none), is published under <c>outputFiles/{execution}/step-{n}-i{k}.{ext}</c> and
/// registered as an image project file. The credit side is metered by the image client's metering decorator.
/// <para>
/// <b>Order, fail-closed.</b> Config, consent, provider, model, price and the model's own limits are all checked BEFORE the
/// gate, so a refusal writes no ledger row and sends nothing. An identical earlier picture (same project, same resolved
/// row, same request hash, a Succeeded row whose object still exists) is reused and buys nothing.
/// </para>
/// <para>
/// <b>Every exit leaves each row in a defined state.</b> A refusal the vendor answered definitively (a 4xx other than 408,
/// a 429, a failure before the request left the engine) is <c>Failed</c> (released); an outcome the step cannot know (a
/// timeout, a 5xx, a dropped connection, an accepted job that then failed) stays <c>Reserved</c> (charged): inside one
/// attempt it is never re-submitted (<c>RESERVATION_ORPHANED</c>); a later attempt of the run has the gate abandon it once
/// (<c>VideoGeneration:MaxAbandonsPerTake</c>) and buy the picture again, the abandoned row still counting toward the
/// step cap. A picture generated but lost in delivery is <c>Abandoned</c> too (never charged to the tenant); a picture the
/// stop reached before its call is <c>Cancelled</c>.
/// </para>
/// It never throws except to propagate a stop that lands before anything was reserved.
/// </summary>
public sealed class ImageGenerateStepExecutor : IStepExecutor
{
    /// <summary>How long the vendor may read the source picture of an Edit.</summary>
    public static readonly TimeSpan SourceUrlTtl = TimeSpan.FromMinutes(10);

    private const string StatusSucceeded = "succeeded";
    private const string StatusFailed = "failed";
    private const string EgressRefusedCode = "EGRESS_REFUSED";

    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>Codes that blame the vendor of the resolved row: on an organization's own row they are BYO errors.</summary>
    private static readonly HashSet<string> VendorCodes = new(StringComparer.Ordinal)
    {
        "IMAGE_PROVIDER_CONFIG", "IMAGE_VENDOR_RETRYABLE", "IMAGE_VENDOR_REJECTED", "SUBMIT_OUTCOME_UNKNOWN",
    };

    /// <summary>The fixed tenant sentence for a platform row's account refusal.</summary>
    internal const string PlatformAccountSentence =
        "The platform's image provider is temporarily unavailable; nothing was generated and the reservation was released. " +
        "Try again later, and contact support if it persists.";

    private readonly IVideoGenerationBudgetGate _budgetGate;
    private readonly IInferenceProviderResolver _providers;
    private readonly IImageClientFactory _clients;
    private readonly IProjectFileWorkspace _workspace;
    private readonly IDbContextFactory<WorkflowEngineDbContext> _dbContextFactory;
    private readonly ILogger<ImageGenerateStepExecutor> _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IModelPriceService? _prices;
    private readonly IPresignedUrlService? _presignedUrls;
    private readonly ExecutionRunFlags _runFlags;
    private readonly IContentModerationGate? _moderation;
    private readonly IUsageRecorder? _usageRecorder;
    private readonly int _maxAbandonsPerTake;
    private readonly IVideoToolRunner? _videoToolRunner;

    public ImageGenerateStepExecutor(
        IVideoGenerationBudgetGate budgetGate,
        IInferenceProviderResolver providers,
        IImageClientFactory clients,
        IProjectFileWorkspace workspace,
        IDbContextFactory<WorkflowEngineDbContext> dbContextFactory,
        ILogger<ImageGenerateStepExecutor> logger,
        IModelPriceService? prices = null,
        IPresignedUrlService? presignedUrls = null,
        IServiceScopeFactory? scopeFactory = null,
        ExecutionRunFlags? runFlags = null,
        IContentModerationGate? moderation = null,
        IUsageRecorder? usageRecorder = null,
        Microsoft.Extensions.Options.IOptions<VideoGenerationOptions>? generationOptions = null,
        IVideoToolRunner? videoToolRunner = null)
    {
        _videoToolRunner = videoToolRunner;
        _maxAbandonsPerTake = Math.Max(0, generationOptions?.Value.MaxAbandonsPerTake ?? new VideoGenerationOptions().MaxAbandonsPerTake);
        _usageRecorder = usageRecorder;
        _budgetGate = budgetGate;
        _providers = providers;
        _clients = clients;
        _workspace = workspace;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _prices = prices;
        _presignedUrls = presignedUrls;
        _scopeFactory = scopeFactory;
        _runFlags = runFlags ?? new ExecutionRunFlags();
        _moderation = moderation;
    }

    public StepType StepType => StepType.ImageGenerate;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sw = Stopwatch.StartNew();
        CancellationToken ct = context.CancellationToken;
        WorkflowStep step = context.Step;

        try
        {
            // ---- 1. Config (re-validated at run time: a template, an import or the assistant skips the save validator) ----
            if (string.IsNullOrWhiteSpace(step.ImageGenerateConfigJson))
                return Failure(context, sw, "CONFIG_INVALID", "The ImageGenerate step has no ImageGenerateConfigJson configured.");

            ImageGenerateStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<ImageGenerateStepConfig>(step.ImageGenerateConfigJson, ImageGenerateStepConfig.JsonOptions);
            }
            catch (JsonException ex)
            {
                return Failure(context, sw, "CONFIG_INVALID", $"ImageGenerateConfigJson is not valid JSON: {ex.Message}");
            }

            if (config is null)
                return Failure(context, sw, "CONFIG_INVALID", "ImageGenerateConfigJson deserialized to null.");

            List<string> errors = config.Validate();
            if (errors.Count > 0)
                return Failure(context, sw, "CONFIG_INVALID", string.Join("; ", errors));

            bool edit = config.Mode == ImageGenerateMode.Edit;

            // ---- 2. Edit sends the source out: consent first, nothing is read or uploaded without it ----
            if (edit && !config.AllowSourceMediaEgress)
            {
                return Failure(context, sw, EgressRefusedCode,
                    "Editing a picture sends it to a hosted AI service, so it needs your consent. allowSourceMediaEgress is false on " +
                    "this step; turn it on to accept that the picture leaves this host. Nothing was uploaded.");
            }

            // ---- 3. Provider (explicit row, else the capability's default row, else none) ----
            ResolvedImageProvider? provider = await ImageGenerateCreditPricing.ResolveProviderAsync(_providers, config, ct);
            if (provider is null)
            {
                string capability = ImageGenerateCreditPricing.CapabilityOf(config.Mode).ToString();
                return Failure(context, sw, "NO_IMAGE_PROVIDER",
                    $"No {capability} provider is configured{(config.ProviderId is null ? string.Empty : " under the step's ProviderId")}. " +
                    $"Add a provider with the {capability} capability, or name one in ProviderId.");
            }

            // ---- 4. Model, price and the model's own limits: all before any ledger row (fail closed) ----
            string model = ImageGenerateCreditPricing.ResolveModel(config.Mode, provider);
            if (model.Length == 0)
                return Failure(context, sw, "CONFIG_INVALID", $"Provider '{provider.Name}' names no model for {config.Mode}.");

            // Unmetered: the usage is recorded below, only for a picture that was delivered (the decorator would record on the
            // provider's answer, before the upload that can still fail).
            IImageClient client = _clients.GetUnmetered(provider);
            ImageModelCapabilities capabilities;
            try
            {
                capabilities = client.Describe(model);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
            {
                return Failure(context, sw, "CONFIG_INVALID", ex.Message);
            }

            if (edit && !capabilities.SupportsInstructionEdit)
                return Failure(context, sw, "CONFIG_INVALID", $"Model '{model}' cannot edit a picture from an instruction.");
            if (!edit && capabilities.SupportsInstructionEdit)
                return Failure(context, sw, "CONFIG_INVALID", $"Model '{model}' edits pictures; it does not generate from a prompt alone.");
            if (config.EffectivePrompt.Length > capabilities.MaxPromptLength
                || config.Items.Any(item => config.PromptFor(item).Length > capabilities.MaxPromptLength))
                return Failure(context, sw, "CONFIG_INVALID", $"The text must be at most {capabilities.MaxPromptLength} characters for '{model}'.");

            ImageUnitPrice price = await PriceAsync(config, provider, model, ct);
            decimal imageCredits = CreditCurrency.CreditsFromUsd(price.CostUsd);
            if (imageCredits <= 0m)
                return Failure(context, sw, "CONFIG_INVALID", $"The price of '{model}' yields a non-positive estimate; it cannot be offered.");

            // ---- 5. The source picture (Edit): resolved inside this project, never a foreign key ----
            // The batch: one entry per item (an ItemList entry, or a brief picture file), each with its own text and source.
            string? sourceKey = null;
            List<BatchItem> batch = [];
            if (config.UsesBriefFiles)
            {
                (List<BatchItem>? briefItems, string? briefCode, string? briefError) = await ResolveBriefItemsAsync(context, config);
                if (briefItems is null)
                    return Failure(context, sw, briefCode!, briefError!);
                batch = briefItems;
            }
            else
            {
                if (edit)
                {
                    (sourceKey, string? sourceCode, string? sourceError) = await ResolveSourceAsync(context, config.Source!);
                    if (sourceKey is null)
                        return Failure(context, sw, sourceCode!, sourceError!);
                }

                if (config.Items.Count > 0)
                    batch = [.. config.Items.Select(item => new BatchItem(item, config.PromptFor(item), sourceKey))];
                else
                    batch = [new BatchItem(null, config.EffectivePrompt, sourceKey)];
            }

            if (batch.Any(b => b.Prompt.Length > capabilities.MaxPromptLength))
                return Failure(context, sw, "CONFIG_INVALID", $"The text must be at most {capabilities.MaxPromptLength} characters for '{model}'.");

            if (edit)
            {
                if (_presignedUrls is null)
                {
                    return Failure(context, sw, "IMAGE_EGRESS_UNAVAILABLE",
                        "This deployment cannot hand a hosted model a link to your picture, so picture editing is unavailable here.");
                }
            }

            // ---- 6. Pictures, reuse, then the one atomic reservation ----
            Guid providerKey = provider.ProviderId ?? Guid.Empty;
            List<ImagePlan> images = [];
            bool batched = config.Items.Count > 0 || config.UsesBriefFiles;
            for (int j = 0; j < batch.Count; j++)
            {
                for (int v = 1; v <= config.Variations; v++)
                {
                    // The index runs over items then variations, so it is stable across attempts (the ledger clip id and the hash carry it).
                    int k = (j * config.Variations) + v;
                    BatchItem item = batch[j];
                    images.Add(new ImagePlan(k, VideoBudgetAccounting.ImageClipId(k), RequestHash(config, provider.Kind, model, k, item.SourceKey, item.Prompt), imageCredits)
                    {
                        ItemIndex = batched ? j + 1 : null,
                        ItemLabel = item.Label,
                        Variation = v,
                        Prompt = item.Prompt,
                        SourceKey = item.SourceKey,
                    });
                }
            }

            if (images.Count > ImageGenerateStepConfig.MaxTotalImages)
            {
                return Failure(context, sw, "CONFIG_INVALID",
                    $"A step may make at most {ImageGenerateStepConfig.MaxTotalImages} pictures in all; this run would make {images.Count}. Choose fewer files or lower Variations.");
            }

            Dictionary<string, ReusedImage> reusable = _runFlags.RegenerateVideoClips(context.Execution.Id)
                ? new(StringComparer.Ordinal)
                : await FindReusableAsync(context.Execution, providerKey, images, ct);

            List<ImagePlan> buy = [.. images.Where(i => !reusable.ContainsKey(i.Hash))];
            Dictionary<string, ImageOutcome> outcomes = new(StringComparer.Ordinal);
            string? promptCheck = null;
            decimal estimated = 0m;

            if (buy.Count > 0)
            {
                // Screen the prompt BEFORE the reservation, so a blocked prompt buys nothing. An outage or the mid band
                // proceeds (generation fails open).
                if (_moderation is not null)
                {
                    // Every distinct text that will be sent (a batch sends one per item).
                    string[] texts = [.. buy.Select(i => i.Prompt).Distinct(StringComparer.Ordinal)];
                    var attribution = new ModerationAttribution(
                        context.Execution.OrganizationId,
                        context.Execution.ProjectId,
                        context.Execution.InitiatedByUserId,
                        context.Execution.Id,
                        context.StepResultId,
                        ModerationSurface.GenerationPrompt,
                        ModerationContentKind.Text);
                    ModerationDecision decision = await _moderation.ModerateAsync(
                        new ModerationInput(texts[0], [], ModerationContentKind.Text), attribution, ct);
                    if (decision.Action == ModerationAction.Block)
                        return Failure(context, sw, "CONTENT_BLOCKED", decision.UserMessage);
                    for (int t = 1; t < texts.Length && decision.Action != ModerationAction.Block; t++)
                    {
                        ModerationDecision more = await _moderation.ModerateAsync(
                            new ModerationInput(texts[t], [], ModerationContentKind.Text), attribution, ct);
                        if (more.Action == ModerationAction.Block)
                            return Failure(context, sw, "CONTENT_BLOCKED", more.UserMessage);
                    }

                    if (!decision.Available)
                    {
                        _logger.LogWarning(
                            "Content moderation was unavailable for the ImageGenerate prompt at step {StepOrder}; proceeding because generation fails open",
                            step.StepOrder);
                    }
                    else if (decision.Action is ModerationAction.Allow or ModerationAction.Review)
                    {
                        promptCheck = decision.Action.ToString();
                    }
                }

                // With no author cap, room for the priced total plus what a later attempt may abandon and buy again (once per picture by
                // default), so an engine retry after an unknown outcome is not refused by the step's own default.
                decimal stepCap = config.MaxSpendCredits is { } cap ? (decimal)cap : buy.Sum(i => i.Credits) * (1 + _maxAbandonsPerTake);
                await context.ReportProgressAsync("Reserving the generation budget");
                VideoGenerationReservation reservation = await _budgetGate.ReserveAsync(
                    new VideoGenerationReservationRequest(
                        context.Execution.ProjectId, context.Execution.Id, context.StepResultId, providerKey, stepCap,
                        [.. buy.Select(i => new VideoGenerationTakeRequest(i.ClipId, i.Hash, i.Credits))],
                        Scope: VideoGenerationBudgetScope.Image),
                    ct);

                if (!reservation.Allowed)
                    return Denied(context, sw, reservation.DenyReason);

                estimated = buy.Sum(i => i.Credits);

                // Terminal writes use CancellationToken.None so a stopped step still records each picture's state.
                await using WorkflowEngineDbContext db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
                for (int i = 0; i < reservation.Takes.Count; i++)
                {
                    ReservedTake reserved = reservation.Takes[i];
                    ImagePlan plan = buy[i];
                    await context.ReportProgressAsync(
                        $"Generating picture {i + 1} of {buy.Count}", (int)(10 + (80.0 * i / reservation.Takes.Count)));
                    outcomes[plan.Hash] = await RunImageAsync(context, config, provider, client, model, price, plan.SourceKey, plan, reserved, db, ct);
                }
            }

            // ---- 7. Register, then report ----
            return await CompleteAsync(context, sw, config, provider, model, images, reusable, outcomes, estimated, promptCheck);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Only reachable before the gate reserved anything (every picture path records its own stop).
            throw;
        }
        catch (ModelUnpricedException unpriced)
        {
            // A platform model with no effective price is a catalog gap with its own code (refunded: PlatformInternal).
            _logger.LogWarning(unpriced, "ImageGenerate step {StepOrder} refused an unpriced model", step.StepOrder);
            return Failure(context, sw, unpriced.Code, unpriced.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ImageGenerate step {StepOrder} failed unexpectedly", step.StepOrder);
            return Failure(context, sw, "IMAGE_GENERATE_FAILED", "The image generation failed unexpectedly. Try the step again; if it keeps failing, contact support.");
        }
    }

    // ------------------------------------------------------------------ pricing and source

    private async Task<ImageUnitPrice> PriceAsync(ImageGenerateStepConfig config, ResolvedImageProvider provider, string model, CancellationToken ct)
    {
        if (_prices is null)
            throw new ModelUnpricedException(provider.Capability, model);
        try
        {
            return await ImageGenerateCreditPricing.PriceOneAsync(_prices, config, provider, model, ct);
        }
        catch (UnpricedUsageException)
        {
            throw new ModelUnpricedException(provider.Capability, model);
        }
    }

    /// <summary>Resolves the one source picture to a storage key inside this project. Every refusal is a coded, user-input failure.</summary>
    private async Task<(string? Key, string? Code, string? Error)> ResolveSourceAsync(StepExecutionContext context, VideoSourceRef source)
    {
        Guid projectId = context.Execution.ProjectId;
        string? key;
        switch (source.Kind)
        {
            case VideoSourceKind.PreviousStepOutput:
                key = context.StepOutputHistory.LastOrDefault(h => !string.IsNullOrWhiteSpace(h.OutputStorageKey))?.OutputStorageKey;
                if (key is null)
                    return (null, "SOURCE_UNRESOLVED", "Source=PreviousStepOutput but no prior step in this execution produced an output file.");
                break;

            case VideoSourceKind.StepOutput:
                key = context.StepOutputHistory.LastOrDefault(h => h.StepOrder == source.StepOrder)?.OutputStorageKey;
                if (string.IsNullOrWhiteSpace(key))
                    return (null, "SOURCE_UNRESOLVED", $"Step {source.StepOrder} did not produce an output file.");
                break;

            case VideoSourceKind.ProjectFile:
                {
                    // ListFilesAsync never returns a quarantined file (CSAM or moderation), so a held source is "not found".
                    IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(projectId, context.CancellationToken);
                    ProjectWorkspaceFile? file = files.FirstOrDefault(f => f.Id == source.ProjectFileId);
                    if (file is null)
                        return (null, "SOURCE_NOT_FOUND", $"ProjectFile '{source.ProjectFileId}' was not found in this project.");
                    if (!file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        return (null, "SOURCE_NOT_IMAGE", $"ProjectFile '{file.OriginalFileName}' is not a picture ({file.MimeType}).");
                    key = file.StorageKey;
                    break;
                }

            case VideoSourceKind.StepResult:
                {
                    StepResultSourceResolution resolved = await StepResultSourceResolver.ResolveAsync(
                        _scopeFactory, projectId, source, allowImages: true, context.CancellationToken);
                    if (resolved.StorageKey is null)
                        return (null, resolved.Code, resolved.Message);
                    key = resolved.StorageKey;
                    break;
                }

            default:
                return (null, "SOURCE_UNRESOLVED", $"Source kind '{source.Kind}' is not supported by ImageGenerate.");
        }

        if (!UnderProject(projectId, key))
            return (null, "SOURCE_NOT_FOUND", "The source picture is not a file of this project.");
        if (!ImageExtensions.Contains(Path.GetExtension(key)))
            return (null, "SOURCE_NOT_IMAGE", "The source is not a picture (png, jpg or webp).");
        return (key, null, null);
    }

    /// <summary>True when <paramref name="key"/> lies under this project and has no traversal.</summary>
    private static bool UnderProject(Guid projectId, string? key) =>
        !string.IsNullOrEmpty(key)
        && key.StartsWith($"projects/{projectId:D}/", StringComparison.Ordinal)
        && !key.Contains("..", StringComparison.Ordinal);

    // ------------------------------------------------------------------ one picture

    /// <summary>One picture to make: its stable index, ledger clip id, request hash, price and (batch) the item it belongs to.</summary>
    private sealed record ImagePlan(int Index, string ClipId, string Hash, decimal Credits)
    {
        /// <summary>1-based item number; null when the step is not a batch.</summary>
        public int? ItemIndex { get; init; }

        public string? ItemLabel { get; init; }

        public int Variation { get; init; } = 1;

        public string Prompt { get; init; } = string.Empty;

        public string? SourceKey { get; init; }
    }

    /// <summary>One item of a batch: its label (null for a plain step), the text sent for it and, for Edit, its source picture.</summary>
    private sealed record BatchItem(string? Label, string Prompt, string? SourceKey);

    /// <summary>
    /// Edit with Source=BriefFiles: the run's picture files, re-checked against the project, one item each (labelled by the
    /// file name). A coded refusal when none were chosen, one is not a picture, or there are more than the step may make.
    /// </summary>
    private async Task<(List<BatchItem>? Items, string? Code, string? Error)> ResolveBriefItemsAsync(StepExecutionContext context, ImageGenerateStepConfig config)
    {
        BriefFilesResolution expanded = await BriefFilesSourceResolver.ResolveAsync(
            _workspace, context.Execution.ProjectId, config.Source!, RunBriefAmbient.Current, context.CancellationToken);
        if (expanded.Failed)
            return (null, expanded.Code, expanded.Message);
        if (expanded.Sources.Count > config.BriefFileCap)
        {
            return (null, "TOO_MANY_ITEMS",
                $"{expanded.Sources.Count} picture files were chosen, but this step makes at most {ImageGenerateStepConfig.MaxTotalImages} pictures " +
                $"({config.BriefFileCap} files x {config.Variations} per file). Choose fewer files.");
        }

        IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(context.Execution.ProjectId, context.CancellationToken);
        List<BatchItem> items = [];
        foreach (VideoSourceRef source in expanded.Sources)
        {
            ProjectWorkspaceFile? file = files.FirstOrDefault(f => f.Id == source.ProjectFileId);
            if (file is null)
                return (null, "SOURCE_NOT_FOUND", $"ProjectFile '{source.ProjectFileId}' was not found in this project.");
            if (!file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return (null, "SOURCE_NOT_IMAGE", $"ProjectFile '{file.OriginalFileName}' is not a picture ({file.MimeType}).");
            if (!UnderProject(context.Execution.ProjectId, file.StorageKey) || !ImageExtensions.Contains(Path.GetExtension(file.StorageKey)))
                return (null, "SOURCE_NOT_IMAGE", $"ProjectFile '{file.OriginalFileName}' is not a picture (png, jpg or webp).");

            string label = Path.GetFileNameWithoutExtension(file.OriginalFileName);
            items.Add(new BatchItem(label.Length == 0 ? file.OriginalFileName : label, config.PromptFor(label), file.StorageKey));
        }

        return (items, null, null);
    }

    private sealed record ReusedImage(Guid JobId, string StorageKey);

    private sealed record ImageOutcome(
        string? Code, string? Detail, string? StorageKey, string? MimeType, long SizeBytes, int? Width, int? Height, ExternalGenerationJob? Row)
    {
        /// <summary>True when the engine resized the delivered picture to the exact output size.</summary>
        public bool Resized { get; init; }

        /// <summary>The tier sent to the vendor (null when none).</summary>
        public string? Tier { get; init; }

        /// <summary>Step warnings of this picture (<c>image_upscaled</c>, <c>resize_failed</c>).</summary>
        public List<(string Code, string Text)> Warnings { get; init; } = [];

        public bool Delivered => StorageKey is not null && Code is null;

        public static ImageOutcome Failed(string code, string detail, ExternalGenerationJob? row) =>
            new(code, detail, null, null, 0, null, null, row);
    }

    private async Task<ImageOutcome> RunImageAsync(
        StepExecutionContext context, ImageGenerateStepConfig config, ResolvedImageProvider provider, IImageClient client,
        string model, ImageUnitPrice price, string? sourceKey, ImagePlan plan, ReservedTake reserved, WorkflowEngineDbContext db, CancellationToken ct)
    {
        ExternalGenerationJob? job = null;
        bool invoked = false;
        try
        {
            job = await db.ExternalGenerationJobs.FirstOrDefaultAsync(j => j.Id == reserved.JobId, CancellationToken.None)
                  ?? throw new InvalidOperationException($"Reserved ledger row {reserved.JobId} for {plan.ClipId} was not found.");

            if (reserved.Existing)
            {
                // An earlier attempt reserved this picture and may have been billed for it; re-submitting could pay twice.
                // The row is left exactly as it is.
                return ImageOutcome.Failed("RESERVATION_ORPHANED",
                    $"A prior attempt of this step reserved this picture (ledger row {job.Id}, CreatedAt {job.CreatedAt:O} UTC" +
                    (job.RemoteJobId is null ? string.Empty : $", vendor id {job.RemoteJobId}") + ") and its outcome is " +
                    "unknown. It was not re-submitted, to avoid double billing; it stays charged against the budget until " +
                    "the UTC day rolls over. Check the provider console for a generation near that time.", job);
            }

            if (ct.IsCancellationRequested)
            {
                await SaveAsync(db, job, ExternalGenerationJobStatus.Cancelled);
                return ImageOutcome.Failed("NOT_SUBMITTED", "The step was stopped before this picture was sent; its reservation was released.", job);
            }

            GeneratedImageSet generated;
            try
            {
                invoked = true;
                long? pixels = ImageGenerateCreditPricing.ExpectedPixels(config, provider.Kind, model);
                ImageOutputPlan shape = ImageOutputPlanner.PlanFor(config, provider.Kind, model);
                Dictionary<string, JsonElement>? tierParams = shape.Tier is null
                    ? null
                    : new() { ["resolution"] = JsonSerializer.SerializeToElement(shape.Tier) };
                if (config.Mode == ImageGenerateMode.Edit)
                {
                    string url = await _presignedUrls!.CreateGetAsync(context.Execution.ProjectId, sourceKey!, SourceUrlTtl);
                    generated = await client.EditAsync(
                        new ImageEditRequest(model, plan.Prompt, url, shape.AspectRatio, Params: tierParams) { ExpectedOutputPixels = pixels }, ct);
                }
                else
                {
                    generated = await client.GenerateAsync(
                        new ImageGenerationRequest(model, plan.Prompt, shape.AspectRatio, shape.RequestWidth, shape.RequestHeight, 1, Params: tierParams)
                        {
                            ExpectedOutputPixels = pixels
                        }, ct);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // The client's own schema check refused the request: nothing was sent.
                await SaveAsync(db, job, ExternalGenerationJobStatus.Failed);
                return ImageOutcome.Failed("IMAGE_REQUEST_INVALID", Bound(ex.Message, 600), job);
            }
            catch (HttpRequestException ex) when (IsDefiniteRejection(ex))
            {
                return await VendorRefusalAsync(db, job, ex, provider, plan.Index);
            }
            catch (Exception ex)
            {
                // A timeout, a 5xx, a transport failure mid-answer, an accepted job that then failed, or a stop during the
                // call: the vendor may have generated (and billed) the picture, so the row stays Reserved and charged. The
                // exception text (hosts, addresses, inner transport detail) is logged, never written to the tenant-visible step.
                _logger.LogWarning(ex, "ImageGenerate picture {Index} (row {JobId}): the vendor call's outcome is unknown", plan.Index, job.Id);
                await SaveAsync(db, job, null);
                return ImageOutcome.Failed("SUBMIT_OUTCOME_UNKNOWN",
                    "The generation's outcome is unknown (the call timed out or failed); the reservation stays charged.", job);
            }

            // Billed from here: record it before anything else can fail, and deliver even if the step is being stopped.
            job.RemoteJobId = generated.VendorRequestId is { } vendorId ? Bound(vendorId, 200) : null;
            await SaveAsync(db, job, ExternalGenerationJobStatus.Submitted);
            return await DeliverAsync(context, config, provider, model, price, plan, generated, job, db);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ImageGenerate picture {Index} of execution {ExecutionId} failed unexpectedly", plan.Index, context.Execution.Id);
            if (job is null)
            {
                return ImageOutcome.Failed("IMAGE_GENERATE_FAILED",
                    $"The picture's ledger row could not be loaded ({ex.GetType().Name}); it stays as the gate wrote it.", null);
            }

            try
            {
                if (job.Status is ExternalGenerationJobStatus.Reserved && !invoked && !reserved.Existing)
                    await SaveAsync(db, job, ExternalGenerationJobStatus.Cancelled);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Could not persist the ledger state of row {JobId}", job.Id);
            }

            return ImageOutcome.Failed("IMAGE_GENERATE_FAILED", "The picture failed unexpectedly; its ledger state was recorded.", job);
        }
    }

    /// <summary>A 4xx other than 408 means the vendor refused the request: nothing was generated. Also a failure before the request left.</summary>
    private static bool IsDefiniteRejection(HttpRequestException ex)
    {
        if (ex.StatusCode is { } status)
            return (int)status is >= 400 and < 500 && (int)status != 408;

        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is NonPublicAddressException)
                return true;
        }

        return ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError;
    }

    /// <summary>Maps a definite vendor refusal onto the row: released, and a platform row's account state never reaches the tenant.</summary>
    private async Task<ImageOutcome> VendorRefusalAsync(
        WorkflowEngineDbContext db, ExternalGenerationJob job, HttpRequestException ex, ResolvedImageProvider provider, int index)
    {
        await SaveAsync(db, job, ExternalGenerationJobStatus.Failed);
        int? status = ex.StatusCode is { } s ? (int)s : null;

        if (status is 401 or 402 or 403)
        {
            _logger.LogError(ex, "ImageGenerate picture {Index} (row {JobId}): the image provider refused the account (HTTP {Status})", index, job.Id, status);
            return ImageOutcome.Failed("IMAGE_PROVIDER_CONFIG",
                provider.OrganizationId is null ? PlatformAccountSentence : $"The provider refused the account (HTTP {status}). Nothing was generated; the reservation was released.", job);
        }

        if (status == 429 || status is null)
        {
            return ImageOutcome.Failed("IMAGE_VENDOR_RETRYABLE",
                "The provider could not take the request right now (busy or unreachable). Nothing was generated and the reservation was released; try again.", job);
        }

        _logger.LogWarning(ex, "ImageGenerate picture {Index} (row {JobId}): the provider rejected the request (HTTP {Status})", index, job.Id, status);
        return ImageOutcome.Failed("IMAGE_VENDOR_REJECTED",
            provider.OrganizationId is null
                ? "The provider rejected the request (for example a prompt or picture it cannot accept). Nothing was generated and the reservation was released."
                : Bound(ex.Message, 600), job);
    }

    private async Task<ImageOutcome> DeliverAsync(
        StepExecutionContext context, ImageGenerateStepConfig config, ResolvedImageProvider provider, string model, ImageUnitPrice price,
        ImagePlan plan, GeneratedImageSet generated, ExternalGenerationJob job, WorkflowEngineDbContext db)
    {
        CancellationToken none = CancellationToken.None;
        GeneratedImage? image = generated.Images.FirstOrDefault(i => i.Bytes.Length > 0);
        if (image is null)
        {
            // The vendor answered with nothing to keep: released, like a refusal.
            await SaveAsync(db, job, ExternalGenerationJobStatus.Failed);
            return ImageOutcome.Failed("IMAGE_EMPTY", "The provider returned no picture; nothing was charged.", job);
        }

        ImageOutputPlan outputPlan = ImageOutputPlanner.PlanFor(config, provider.Kind, model);
        (byte[] bytes, string extension, int? deliveredWidth, int? deliveredHeight, bool resized, List<(string Code, string Text)> warnings) =
            await FitToTargetAsync(outputPlan, image, plan.Index);
        string mime = MimeFor(extension);
        string localPath = Path.Combine(Path.GetTempPath(), $"reelbolt-image-{Guid.NewGuid():N}{extension}");
        string storageKey;
        try
        {
            await File.WriteAllBytesAsync(localPath, bytes, none);
            storageKey = await _workspace.UploadArtifactAsync(
                context.Execution.ProjectId, localPath,
                $"{context.Execution.Id:D}/step-{context.Step.StepOrder}-i{plan.Index}{extension}",
                mime, none, "outputFiles");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upload of picture {Index} (row {JobId}) failed", plan.Index, job.Id);
            // The vendor answered, so the platform may have paid for it: the row is Abandoned, which keeps counting toward
            // the step cap (MaxSpendCredits) like VideoGenerate's abandoned take, so repeated delivery failures cannot spend
            // past it. The tenant is never charged: usage is recorded only after delivery.
            await SaveAsync(db, job, ExternalGenerationJobStatus.Abandoned);
            return ImageOutcome.Failed("DELIVERY_FAILED", "The picture was generated but could not be stored; it was not charged.", job);
        }
        finally
        {
            TryDelete(localPath);
        }

        job.StorageKey = storageKey;
        job.ActualCost = plan.Credits;
        await SaveAsync(db, job, ExternalGenerationJobStatus.Succeeded);
        // The tenant is charged only for a picture it received. Idempotent per ledger row, so a replay never double-charges.
        await RecordUsageAsync(config, provider, model, price, image, job);
        return new ImageOutcome(null, null, storageKey, mime, bytes.LongLength, deliveredWidth, deliveredHeight, job)
        {
            Resized = resized,
            Tier = outputPlan.Tier,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Brings a delivered picture to the exact output size (scale to cover, centre crop, PNG). Never loses a paid picture:
    /// any failure delivers the original with a <c>resize_failed</c> warning. A picture smaller than the target is still
    /// scaled, with an <c>image_upscaled</c> warning.
    /// </summary>
    private async Task<(byte[] Bytes, string Extension, int? Width, int? Height, bool Resized, List<(string Code, string Text)> Warnings)> FitToTargetAsync(
        ImageOutputPlan outputPlan, GeneratedImage image, int index)
    {
        string extension = ExtensionFor(image.ContentType);
        List<(string Code, string Text)> warnings = [];
        int? width = image.Width;
        int? height = image.Height;
        if (!outputPlan.HasTarget)
            return (image.Bytes, extension, width, height, false, warnings);

        int tw = outputPlan.TargetWidth!.Value;
        int th = outputPlan.TargetHeight!.Value;
        (int W, int H)? actual = ImageDimensions.TryRead(image.Bytes);
        if (actual is null && image.Width is { } iw && image.Height is { } ih)
            actual = (iw, ih);
        if (actual is { } a && a.W == tw && a.H == th && extension == ".png")
            return (image.Bytes, extension, tw, th, false, warnings);

        string input = Path.Combine(Path.GetTempPath(), $"reelbolt-image-in-{Guid.NewGuid():N}{extension}");
        string output = Path.Combine(Path.GetTempPath(), $"reelbolt-image-out-{Guid.NewGuid():N}.png");
        try
        {
            if (_videoToolRunner is null)
                throw new InvalidOperationException("No ffmpeg runner is available for the resize.");
            await File.WriteAllBytesAsync(input, image.Bytes, CancellationToken.None);
            string[] args =
            [
                "-y", "-hide_banner", "-loglevel", "error", .. UserMediaInputFormats.InputOptions(), "-i", input,
                "-vf", $"scale={tw}:{th}:force_original_aspect_ratio=increase:flags=lanczos,crop={tw}:{th}",
                "-frames:v", "1", output,
            ];
            VideoToolResult result = await _videoToolRunner.RunFfmpegAsync(args, TimeSpan.FromSeconds(60), CancellationToken.None);
            if (!result.Succeeded || !File.Exists(output))
                throw new InvalidOperationException($"ffmpeg failed (exit {result.ExitCode}): {Bound(result.StdErr, 300)}");
            byte[] resizedBytes = await File.ReadAllBytesAsync(output, CancellationToken.None);
            (int W, int H)? check = ImageDimensions.TryRead(resizedBytes);
            if (check is not { } c || c.W != tw || c.H != th)
                throw new InvalidOperationException($"the resized picture is {(check is { } k ? $"{k.W}x{k.H}" : "unreadable")}, not {tw}x{th}");
            if (actual is { } before && (before.W < tw || before.H < th))
                warnings.Add(("image_upscaled", $"upscaled from {before.W}x{before.H} to {tw}x{th}"));
            return (resizedBytes, ".png", tw, th, true, warnings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resizing picture {Index} to {Width}x{Height} failed; delivering the original", index, tw, th);
            warnings.Add(("resize_failed", $"could not resize to {tw}x{th}; the original {(actual is { } o ? $"{o.W}x{o.H} " : string.Empty)}picture was delivered"));
            return (image.Bytes, extension, width, height, false, warnings);
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
    }

    /// <summary>
    /// The credit-side record of a delivered picture: one unit of the mode's usage kind at the size the picture really had
    /// (size-priced models; never above the model's billed ceiling), keyed <c>imagegen:{row}</c>. The ledger row stays the
    /// spend-side record.
    /// </summary>
    private async Task RecordUsageAsync(
        ImageGenerateStepConfig config, ResolvedImageProvider provider, string model, ImageUnitPrice price, GeneratedImage image, ExternalGenerationJob job)
    {
        if (_usageRecorder is null) return;

        try
        {
            (string billedModel, string? tier) = MeteringImageClient.SplitBillingKey(price.BillingKey);
            string? variant = tier ?? price.Variant;
            if (tier is null && OutputSizePricing.PricesByOutputMegapixels(provider.Kind, model, config.Mode == ImageGenerateMode.Edit, 0))
            {
                long? expected = ImageGenerateCreditPricing.ExpectedPixels(config, provider.Kind, model);
                long? measured = OutputSizePricing.MeasuredPixels([image]) ?? expected;
                if (measured is { } px)
                {
                    if (OutputSizePricing.BilledOutputCeiling(provider.Kind, model, expected) is { } ceiling && px > ceiling)
                        px = ceiling;
                    variant = OutputSizePricing.MegapixelVariant(px);
                }
            }

            await _usageRecorder.RecordAsync(new UsageRecordRequest(
                ImageGenerateCreditPricing.UnitOf(config.Mode), 1m, provider.ProviderId, provider.Kind, billedModel,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                IdempotencyKey: $"imagegen:{job.Id:N}",
                Capability: ImageGenerateCreditPricing.CapabilityOf(config.Mode),
                Variant: variant, AccountId: provider.AccountId), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metering image generation row {JobId} failed", job.Id);
        }
    }

    // ------------------------------------------------------------------ completion

    private async Task<StepExecutionResult> CompleteAsync(
        StepExecutionContext context, Stopwatch sw, ImageGenerateStepConfig config, ResolvedImageProvider provider, string model,
        List<ImagePlan> images, Dictionary<string, ReusedImage> reusable, Dictionary<string, ImageOutcome> outcomes,
        decimal estimated, string? promptCheck)
    {
        Dictionary<string, ProjectWorkspaceFile>? filesByKey = null;
        string exec8 = context.Execution.Id.ToString("N")[..8];
        JsonArray outputImages = [];
        JsonArray manifestImages = [];
        List<(string Code, string Detail)> failures = [];
        string? firstKey = null;
        decimal totalCost = 0m;

        foreach (ImagePlan plan in images)
        {
            bool reused = reusable.TryGetValue(plan.Hash, out ReusedImage? hit);
            ImageOutcome? outcome = reused ? null : outcomes[plan.Hash];
            string? key = reused ? hit!.StorageKey : outcome!.Delivered ? outcome.StorageKey : null;
            string? extension = key is null ? null : Path.GetExtension(key);
            string? fileName = key is null ? null : plan.ItemIndex is { } itemNo
                ? $"{config.EffectivePrefix}-{exec8}-{itemNo:D2}-{Slug(plan.ItemLabel)}{(config.Variations > 1 ? $"-v{plan.Variation}" : string.Empty)}{extension}"
                : $"{config.EffectivePrefix}-{exec8}-{plan.Index}{extension}";
            Guid? projectFileId = null;

            if (key is not null && config.RegisterProjectFile)
            {
                filesByKey ??= await FilesByKeyAsync(context.Execution.ProjectId);
                if (filesByKey.TryGetValue(key, out ProjectWorkspaceFile? existing))
                {
                    projectFileId = existing.Id;
                    fileName = existing.OriginalFileName;
                }
                else if (!reused)
                {
                    projectFileId = await RegisterAsync(context, key, fileName!, outcome!.MimeType!, outcome.SizeBytes);
                }
            }

            (string basis, decimal cost) = reused ? ("released", 0m) : CostLabel(outcome!.Row);
            totalCost += cost;
            firstKey ??= key;
            if (key is null)
                failures.Add((outcome!.Code!, outcome.Detail ?? string.Empty));

            var entry = new JsonObject
            {
                ["image"] = plan.Index,
                ["status"] = key is null ? StatusFailed : StatusSucceeded,
                ["fileName"] = fileName,
                ["projectFileId"] = projectFileId?.ToString(),
                ["width"] = outcome?.Width,
                ["height"] = outcome?.Height,
                ["cost"] = cost,
                ["costBasis"] = basis,
                ["reused"] = reused,
                ["failureReason"] = key is null ? outcome!.Code : null,
                ["detail"] = key is null ? outcome!.Detail : null,
            };
            if (outcome is { Delivered: true } && config.OutputWidth is { } reqW && config.OutputHeight is { } reqH)
                entry["requested"] = new JsonObject { ["width"] = reqW, ["height"] = reqH };
            if (outcome is { Delivered: true } && (outcome.Tier is not null || config.OutputWidth is not null))
            {
                entry["tier"] = outcome.Tier;
                entry["resized"] = outcome.Resized;
            }

            if (outcome is { Warnings.Count: > 0 })
            {
                entry["warnings"] = new JsonArray([.. outcome.Warnings.Select(w =>
                    (JsonNode?)new JsonObject { ["code"] = w.Code, ["message"] = $"Picture {plan.Index}: {w.Text}" })]);
            }

            if (plan.ItemIndex is { } itemIndex)
            {
                // A batch's gallery groups by item: the label the person gave (or the file's name) and its place in the list.
                entry["item"] = plan.ItemLabel;
                entry["itemIndex"] = itemIndex;
                entry["variation"] = plan.Variation;
            }

            outputImages.Add(entry);

            JsonObject manifestEntry = (JsonObject)entry.DeepClone();
            manifestEntry["storageKey"] = key;
            manifestEntry["ledgerRowId"] = (reused ? hit!.JobId : outcome!.Row?.Id)?.ToString();
            if (!reused && key is not null && promptCheck is not null)
                manifestEntry["promptCheck"] = promptCheck;
            manifestImages.Add(manifestEntry);
        }

        var meta = new JsonObject
        {
            ["costUnit"] = ExternalGenerationJob.CreditsUnit,
            ["estimatedCost"] = estimated,
            ["totalCost"] = totalCost,
            ["imagesGenerated"] = outcomes.Values.Count(o => o.Delivered),
            ["imagesReused"] = reusable.Count,
            ["imagesFailed"] = failures.Count,
        };

        if (images.Any(i => i.ItemIndex is not null))
            meta["items"] = new JsonArray([.. images.Where(i => i.Variation == 1).Select(i => (JsonNode?)JsonValue.Create(i.ItemLabel))]);

        string? manifestKey = await UploadManifestAsync(context, config, provider, model, manifestImages, meta);
        bool allFailed = failures.Count == images.Count;
        var output = new JsonObject
        {
            ["status"] = allFailed ? "failed" : "completed",
            ["mode"] = config.Mode.ToString(),
            ["model"] = model,
            ["images"] = outputImages,
            ["meta"] = meta,
        };

        JsonArray stepWarnings = [.. outputImages.OfType<JsonObject>()
            .Where(e => e["warnings"] is JsonArray)
            .SelectMany(e => ((JsonArray)e["warnings"]!).Select(w => w?.DeepClone()))];
        if (stepWarnings.Count > 0)
            output["warnings"] = stepWarnings;

        if (!allFailed)
        {
            return new StepExecutionResult
            {
                Output = output.ToJsonString(OutputJsonOptions),
                NextStepIndex = context.CurrentStepIndex + 1,
                Status = StepStatus.Completed,
                DurationMs = (int)sw.ElapsedMilliseconds,
                OutputStorageKey = firstKey,
                ArtifactStorageKey = manifestKey,
            };
        }

        // Every picture failed: one code when they agree, else ALL_IMAGES_FAILED; the class follows the code, and a vendor
        // failure on the organization's own row is a BYO error.
        string code = failures.Select(f => f.Code).Distinct().Count() == 1 ? failures[0].Code : "ALL_IMAGES_FAILED";
        output["error"] = new JsonObject { ["code"] = code, ["message"] = failures[0].Detail };
        return new StepExecutionResult
        {
            Output = output.ToJsonString(OutputJsonOptions),
            NextStepIndex = context.CurrentStepIndex + 1,
            Status = StepStatus.Failed,
            ErrorDetails = $"{code}: {string.Join(" | ", failures.Select(f => f.Detail).Distinct())}",
            DurationMs = (int)sw.ElapsedMilliseconds,
            ArtifactStorageKey = manifestKey,
            FailureCode = code,
            FailureClass = ClassFor(code, failures.Select(f => f.Code), provider),
        };
    }

    private static FailureClass ClassFor(string code, IEnumerable<string> pictureCodes, ResolvedImageProvider provider)
    {
        FailureClass mapped = FailureClassifier.ForCode(code);
        bool byo = MeteringChatClient.BillingModeOf(provider.OrganizationId) == UsageBillingMode.Byo;
        // A mix of vendor failures (ALL_IMAGES_FAILED) is still the organization's own provider's.
        return byo && pictureCodes.All(VendorCodes.Contains) ? FailureClass.ByoProviderError : mapped;
    }

    private static (string Basis, decimal Cost) CostLabel(ExternalGenerationJob? row) => row switch
    {
        null => ("released", 0m),
        { Status: ExternalGenerationJobStatus.Failed or ExternalGenerationJobStatus.Cancelled or ExternalGenerationJobStatus.Abandoned } => ("released", 0m),
        { ActualCost: { } actual } => ("actual", actual),
        _ => ("estimate", row.CostEstimate),
    };

    private async Task<Dictionary<string, ProjectWorkspaceFile>> FilesByKeyAsync(Guid projectId)
    {
        try
        {
            IReadOnlyList<ProjectWorkspaceFile> files = await _workspace.ListFilesAsync(projectId, CancellationToken.None);
            return files.GroupBy(f => f.StorageKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list project {ProjectId}'s files; generated pictures are registered as new files", projectId);
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>Best-effort like ExtractFrame's: the object is stored and the manifest names it, so a failure costs discoverability only.</summary>
    private async Task<Guid?> RegisterAsync(StepExecutionContext context, string storageKey, string fileName, string mimeType, long sizeBytes)
    {
        try
        {
            ProjectWorkspaceFile file = await _workspace.RegisterStoredObjectAsync(
                context.Execution.ProjectId, storageKey, fileName, mimeType, sizeBytes,
                SummaryStatus.Done, FileIndexingStatus.NotIndexed, CancellationToken.None, category: "outputFiles");
            return file.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ImageGenerate step {StepOrder}: could not register '{FileName}' as a project file", context.Step.StepOrder, fileName);
            return null;
        }
    }

    private async Task<string?> UploadManifestAsync(
        StepExecutionContext context, ImageGenerateStepConfig config, ResolvedImageProvider provider, string model, JsonArray images, JsonObject meta)
    {
        var manifest = new JsonObject
        {
            ["version"] = 1,
            ["mode"] = config.Mode.ToString(),
            ["providerId"] = provider.ProviderId?.ToString(),
            ["model"] = model,
            ["images"] = images,
            ["meta"] = meta.DeepClone(),
        };

        string localPath = Path.Combine(Path.GetTempPath(), $"reelbolt-image-manifest-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(localPath, manifest.ToJsonString(OutputJsonOptions), CancellationToken.None);
            return await _workspace.UploadArtifactAsync(
                context.Execution.ProjectId, localPath,
                $"image-generate/{context.Execution.Id:D}/step-{context.Step.StepOrder}-images.json",
                "application/json", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the image manifest for execution {ExecutionId}; the pictures are unaffected", context.Execution.Id);
            return null;
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    // ------------------------------------------------------------------ reuse

    /// <summary>
    /// Per request hash, an earlier delivered picture for the identical request: same project and RESOLVED provider row,
    /// <c>Succeeded</c>, object still present. This execution's rows win, then the most recent. Any failure means "not
    /// reusable": the gate, not this lookup, bounds a fresh purchase.
    /// </summary>
    private async Task<Dictionary<string, ReusedImage>> FindReusableAsync(
        WorkflowExecution execution, Guid providerId, IReadOnlyList<ImagePlan> images, CancellationToken ct)
    {
        var result = new Dictionary<string, ReusedImage>(StringComparer.Ordinal);
        try
        {
            List<string> hashes = [.. images.Select(i => i.Hash)];
            await using WorkflowEngineDbContext db = await _dbContextFactory.CreateDbContextAsync(ct);
            // A picture's hash does not cover its input (an Edit's source content), so a project with a quarantined file
            // (CSAM or moderation) reuses only pictures of runs that began after its marker. A failed read throws and
            // lands in the catch below: no reuse.
            DateTime? marker = await ProjectQuarantineState.ReadMarkerAsync(db, execution.ProjectId, ct);
            DateTime? madeAfter = marker + ProjectQuarantineState.ClockSkewMargin;
            List<ExternalGenerationJob> rows = await db.ExternalGenerationJobs.AsNoTracking()
                .Where(j => (madeAfter == null
                        || (j.CreatedAt > madeAfter
                            && db.WorkflowExecutions.Any(e => e.Id == j.ExecutionId && e.FirstStartedAt > madeAfter)))
                    && j.ProjectId == execution.ProjectId && j.ProviderId == providerId && hashes.Contains(j.RequestHash)
                    && j.Status == ExternalGenerationJobStatus.Succeeded && j.StorageKey != null)
                .ToListAsync(ct);

            foreach (IGrouping<string, ExternalGenerationJob> group in rows.GroupBy(r => r.RequestHash))
            {
                foreach (ExternalGenerationJob row in group.OrderByDescending(r => r.ExecutionId == execution.Id).ThenByDescending(r => r.UpdatedAt))
                {
                    if (await _workspace.ArtifactExistsAsync(execution.ProjectId, row.StorageKey!, ct))
                    {
                        result[group.Key] = new ReusedImage(row.Id, row.StorageKey!);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not look up reusable pictures for execution {ExecutionId}; generating fresh", execution.Id);
            result.Clear();
        }

        return result;
    }

    /// <summary>
    /// SHA-256 over every field that changes what the vendor generates, plus the picture index and (Edit) the source's
    /// storage key; domain-separated from the video and audio hashes by its first element and length-safe because the
    /// fields are a JSON array.
    /// </summary>
    internal static string RequestHash(ImageGenerateStepConfig config, InferenceProviderKind providerKind, string model, int index, string? sourceKey) =>
        RequestHash(config, providerKind, model, index, sourceKey, config.EffectivePrompt);

    /// <summary>The same hash for one item of a batch: <paramref name="prompt"/> is that item's text.</summary>
    internal static string RequestHash(ImageGenerateStepConfig config, InferenceProviderKind providerKind, string model, int index, string? sourceKey, string prompt)
    {
        List<object?> fields =
        [
            "image-generate/v1", providerKind.ToString(), model, config.Mode.ToString(), prompt, sourceKey,
            config.AspectRatio, config.Width?.ToString(CultureInfo.InvariantCulture), config.Height?.ToString(CultureInfo.InvariantCulture), index,
        ];
        // The output-size fields join only when set, so every earlier step keeps its hash (and its reusable pictures).
        if (config.ResolutionTier is not null || config.OutputWidth is not null || config.OutputHeight is not null)
            fields.Add(new[] { config.ResolutionTier, config.OutputWidth?.ToString(CultureInfo.InvariantCulture), config.OutputHeight?.ToString(CultureInfo.InvariantCulture) });
        string material = JsonSerializer.Serialize(fields);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    // ------------------------------------------------------------------ helpers

    private static StepExecutionResult Denied(StepExecutionContext context, Stopwatch sw, string? denyReason)
    {
        string reason = denyReason ?? VideoBudgetMessages.Code + ":";
        string code = reason.StartsWith("CONFIG_INVALID:", StringComparison.Ordinal) ? "CONFIG_INVALID"
            : reason.StartsWith("PLAN_NOT_ENTITLED:", StringComparison.Ordinal) ? "PLAN_NOT_ENTITLED"
            : VideoBudgetMessages.Code;
        string text = reason.StartsWith(code + ":", StringComparison.Ordinal) ? reason[(code.Length + 1)..].Trim() : reason.Trim();
        if (text.Length == 0)
            text = code == VideoBudgetMessages.Code ? VideoBudgetMessages.GenericSentence : $"Image generation was refused ({code}).";
        if (code == VideoBudgetMessages.Code)
        {
            text = "The pictures would cost more than this step's spend limit allows (maxSpendCredits, or by default the priced total plus one retry), "
                + "counting any picture an earlier attempt of this step may already have paid for. Raise maxSpendCredits on the step and run it again.";
        }

        return Failure(context, sw, code, text);
    }

    private static StepExecutionResult Failure(StepExecutionContext context, Stopwatch sw, string code, string message)
    {
        var output = new JsonObject
        {
            ["status"] = "failed",
            ["images"] = new JsonArray(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        return new StepExecutionResult
        {
            Output = output.ToJsonString(OutputJsonOptions),
            NextStepIndex = context.CurrentStepIndex + 1,
            Status = StepStatus.Failed,
            ErrorDetails = $"{code}: {message}",
            DurationMs = (int)sw.ElapsedMilliseconds,
            FailureCode = code,
            FailureClass = FailureClassifier.ForCode(code),
        };
    }

    private static async Task SaveAsync(WorkflowEngineDbContext db, ExternalGenerationJob job, ExternalGenerationJobStatus? status)
    {
        if (status is { } s) job.Status = s;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string ExtensionFor(string? contentType) => (contentType ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".png",
    };

    private static string MimeFor(string extension) => extension switch
    {
        ".jpg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    /// <summary>A file-name-safe fragment of an item label: letters and digits, lower case, at most 30 characters; "item" when none remain.</summary>
    internal static string Slug(string? label)
    {
        StringBuilder sb = new();
        bool dash = false;
        foreach (char c in (label ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                sb.Append(char.ToLowerInvariant(c));
                dash = false;
            }
            else if (!dash && sb.Length > 0)
            {
                sb.Append('-');
                dash = true;
            }
        }

        string slug = sb.ToString().Trim('-');
        if (slug.Length > 30)
            slug = slug[..30].Trim('-');
        return slug.Length == 0 ? "item" : slug;
    }

    private static string Bound(string? value, int max = 300) =>
        value is null ? string.Empty : value.Length <= max ? value : value[..max] + "…";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // best-effort scratch cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // as above
        }
    }
}
