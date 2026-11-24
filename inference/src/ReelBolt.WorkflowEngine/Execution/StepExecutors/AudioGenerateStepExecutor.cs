using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Inference;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// <c>StepType.AudioGenerate</c> (T-108) — deterministic, non-LLM, PAID: generates <c>Takes</c> music tracks or sound
/// effects through T-107's <see cref="IAudioGenerationClient"/> (fal or direct ElevenLabs), one ledger row per take in
/// <c>external_generation_jobs</c> through the SAME atomic budget gate VideoGenerate uses (step limit
/// <c>MaxSpendCredits</c>, the project, organization and global daily caps; all takes or none), probes each take's
/// duration, publishes it under <c>outputFiles/{execution}/step-{n}-a{k}.{ext}</c>, meters it
/// (<c>MusicGenMinutes</c>/<c>SoundEffectGenSeconds</c>, idempotent per ledger row) and registers it as an audio project
/// file so a MusicSupervisor (<c>m{n}</c>) or SoundDesigner (<c>x{n}</c>) is offered it.
/// <para>
/// <b>Order, fail-closed.</b> Config, provider, model, price and the model's own ranges are all checked BEFORE the gate,
/// so a refusal writes no ledger row and sends nothing. An identical earlier take (same project, same resolved row,
/// same request hash, a Succeeded row whose object still exists) is reused and buys nothing.
/// </para>
/// <para>
/// <b>Every exit leaves each row in a defined state.</b> A refusal the vendor answered definitively (bad prompt,
/// rejected key, exhausted quota, 429/5xx "nothing was generated") is <c>Failed</c> (released); an outcome the step
/// cannot know (timeout, transport, an accepted job that then failed) stays <c>Reserved</c> (charged); a take billed and
/// then lost in delivery stays <c>Submitted</c> (charged); a take the stop reached before its call is
/// <c>Cancelled</c>. A retry is handed an earlier attempt's <c>Reserved</c>/<c>Submitted</c> row back by the gate and
/// NEVER re-submits it (<c>RESERVATION_ORPHANED</c>): the synchronous vendor call has no job to poll.
/// </para>
/// It never throws except to propagate a stop that lands before anything was reserved.
/// </summary>
public sealed class AudioGenerateStepExecutor : IStepExecutor, IChangeRequestAwareStepExecutor
{
    private const string StatusSucceeded = "succeeded";
    private const string StatusFailed = "failed";

    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Codes that blame the vendor of the resolved row: on an organization's own row they are BYO errors.</summary>
    private static readonly HashSet<string> VendorCodes = new(StringComparer.Ordinal)
    {
        "AUDIO_PROVIDER_CONFIG", "AUDIO_VENDOR_QUOTA", "AUDIO_VENDOR_RETRYABLE", "AUDIO_VENDOR_FAILED", "SUBMIT_OUTCOME_UNKNOWN",
    };

    private readonly IVideoGenerationBudgetGate _budgetGate;
    private readonly IInferenceProviderResolver _providers;
    private readonly IAudioGenerationClientFactory _clients;
    private readonly AudioGenerationDurationProbe _durationProbe;
    private readonly IProjectFileWorkspace _workspace;
    private readonly IDbContextFactory<WorkflowEngineDbContext> _dbContextFactory;
    private readonly ILogger<AudioGenerateStepExecutor> _logger;
    private readonly ExecutionRunFlags _runFlags;
    private readonly IUsageRecorder? _usageRecorder;
    /// <summary>T-005: optional so the unit tests that do not care about moderation construct as before.</summary>
    private readonly IContentModerationGate? _moderation;
    /// <summary>Optional like the moderation gate: an ordinary run (and every older test) never has a change request.</summary>
    private readonly IStepChangeRequestContext? _changeRequests;

    public AudioGenerateStepExecutor(
        IVideoGenerationBudgetGate budgetGate,
        IInferenceProviderResolver providers,
        IAudioGenerationClientFactory clients,
        AudioGenerationDurationProbe durationProbe,
        IProjectFileWorkspace workspace,
        IDbContextFactory<WorkflowEngineDbContext> dbContextFactory,
        ILogger<AudioGenerateStepExecutor> logger,
        ExecutionRunFlags? runFlags = null,
        IUsageRecorder? usageRecorder = null,
        IContentModerationGate? moderation = null,
        IStepChangeRequestContext? changeRequests = null)
    {
        _changeRequests = changeRequests;
        _moderation = moderation;
        _budgetGate = budgetGate;
        _providers = providers;
        _clients = clients;
        _durationProbe = durationProbe;
        _workspace = workspace;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _runFlags = runFlags ?? new ExecutionRunFlags();
        _usageRecorder = usageRecorder;
    }

    public StepType StepType => StepType.AudioGenerate;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sw = Stopwatch.StartNew();
        CancellationToken ct = context.CancellationToken;
        WorkflowStep step = context.Step;

        try
        {
            // ---- 1. Config ----
            if (string.IsNullOrWhiteSpace(step.AudioGenerateConfigJson))
                return Failure(context, sw, "CONFIG_INVALID", "The AudioGenerate step has no AudioGenerateConfigJson configured.");

            AudioGenerateStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<AudioGenerateStepConfig>(step.AudioGenerateConfigJson, AudioGenerateStepConfig.JsonOptions);
            }
            catch (JsonException ex)
            {
                return Failure(context, sw, "CONFIG_INVALID", $"AudioGenerateConfigJson is not valid JSON: {ex.Message}");
            }

            if (config is null)
                return Failure(context, sw, "CONFIG_INVALID", "AudioGenerateConfigJson deserialized to null.");

            // Re-validated at run time: a template, an import or the assistant's apply path skips the save validator.
            List<string> errors = config.Validate();
            if (errors.Count > 0)
                return Failure(context, sw, "CONFIG_INVALID", string.Join("; ", errors));

            // ---- 1b. Step change request (docs/design/change-requests.md): which takes are re-made, with what ----
            StepChangeRequestScope? changeScope = _changeRequests is null
                ? null
                : await _changeRequests.FindAsync(context.Execution, step, ct);
            AudioChange? change = null;
            if (changeScope is { Malformed: true })
                return Failure(context, sw, "CHANGE_NOT_APPLIED", StepChangeRequestScope.MalformedRefusal, FailureClass.UserInput);
            if (changeScope is not null)
            {
                change = PlanChange(changeScope.Payload, config.Takes);
                if (change is null)
                {
                    return Failure(context, sw, "CHANGE_NOT_APPLIED",
                        "The change request names no take of this step, so nothing was changed. Choose a take (a1, a2, ...) or all takes and ask again.",
                        FailureClass.UserInput);
                }
            }

            // ---- 2. Provider (explicit row, else the kind's default row, else none) ----
            ResolvedAudioGenerationProvider? provider = await AudioGenerateCreditPricing.ResolveProviderAsync(_providers, config, ct);
            if (provider is null)
            {
                string capability = AudioModelCatalog.CapabilityOf(config.Kind).ToString();
                return Failure(context, sw, "NO_AUDIO_PROVIDER",
                    $"No {capability} provider is configured{(config.ProviderId is null ? string.Empty : " under the step's ProviderId")}. " +
                    $"Add a FalAi row with the {capability} capability, or name one in ProviderId.");
            }

            // ---- 3. Model, price and the model's own ranges: all before any ledger row (fail closed) ----
            Priced priced = Price(config, provider);
            if (priced.Refusal is { } refusal)
                return Failure(context, sw, "CONFIG_INVALID", refusal);

            string model = priced.Model;
            AudioEndpoint endpoint = priced.Endpoint!;
            IAudioGenerationClient client = _clients.Get(provider);
            AudioGenerationCapabilities capabilities;
            try
            {
                // The client's own Describe is the price the request is built against (fail closed: it throws for a
                // model it cannot price).
                capabilities = client.Describe(model);
            }
            catch (NotSupportedException ex)
            {
                return Failure(context, sw, "CONFIG_INVALID", ex.Message);
            }

            if (capabilities.Kind != config.Kind)
                return Failure(context, sw, "CONFIG_INVALID", $"Model '{model}' generates {capabilities.Kind}, not {config.Kind}.");

            decimal takeCredits;
            try
            {
                takeCredits = CreditCurrency.CreditsFromUsd(capabilities.EstimateUsd(config.PricedDurationSec(endpoint)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Failure(context, sw, "CONFIG_INVALID", ex.Message);
            }

            if (takeCredits <= 0m)
                return Failure(context, sw, "CONFIG_INVALID", $"The price of '{model}' yields a non-positive estimate; it cannot be offered.");

            // ---- 4. Takes, reuse, then the one atomic reservation ----
            Guid providerKey = provider.ProviderId ?? Guid.Empty;
            List<TakePlan> takes = [];
            for (int k = 1; k <= config.Takes; k++)
            {
                bool redo = change is null || change.Redo.Contains(k);
                AudioGenerateStepConfig takeConfig = config;
                decimal credits = takeCredits;
                if (redo && change is not null && change.Overrides.TryGetValue(k, out TakeOverride? o) && o.ChangesAnything)
                {
                    takeConfig = config with { Prompt = o.Prompt ?? config.Prompt, DurationSec = o.DurationSec ?? config.DurationSec };
                    List<string> takeErrors = takeConfig.Validate();
                    if (takeErrors.Count == 0)
                        takeErrors = takeConfig.ValidateFor(endpoint);
                    if (takeErrors.Count > 0)
                        return Failure(context, sw, "CONFIG_INVALID", $"Take a{k}: {string.Join("; ", takeErrors)}");
                    try
                    {
                        credits = CreditCurrency.CreditsFromUsd(capabilities.EstimateUsd(takeConfig.PricedDurationSec(endpoint)));
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        return Failure(context, sw, "CONFIG_INVALID", ex.Message);
                    }

                    if (credits <= 0m)
                        return Failure(context, sw, "CONFIG_INVALID", $"The price of '{model}' yields a non-positive estimate; it cannot be offered.");
                }

                takes.Add(new TakePlan(k, VideoBudgetAccounting.AudioClipId(config.Kind, k),
                    RequestHash(takeConfig, provider.Kind, model, k,
                        redo && change is not null && change.Retake.Contains(k) && changeScope is not null ? changeScope.RequestId : null),
                    takeConfig, credits, redo));
            }

            // Takes the request did not name are carried over verbatim from the previous manifest: same object, same
            // ledger row, no submit and no new row. A take that cannot be carried (no manifest, a foreign or missing
            // object) refuses the whole request rather than silently buying it again.
            Dictionary<int, JsonObject> carried = [];
            if (takes.Any(t => !t.Redo))
            {
                Dictionary<int, JsonObject>? manifest = await ReadPreviousManifestAsync(
                    context.Execution.ProjectId, changeScope?.ArtifactStorageKey, ct);
                foreach (TakePlan t in takes.Where(t => !t.Redo))
                {
                    if (manifest is null || !manifest.TryGetValue(t.Take, out JsonObject? prior)
                        || !await CanCarryAsync(context.Execution.ProjectId, prior, ct))
                    {
                        return Failure(context, sw, "CHANGE_NOT_APPLIED",
                            $"Take a{t.Take} of the earlier run cannot be kept (its record or file is no longer available), so nothing was changed. Ask for all takes to be re-made.",
                            FailureClass.UserInput);
                    }

                    carried[t.Take] = prior;
                }
            }

            List<TakePlan> redoTakes = [.. takes.Where(t => t.Redo)];
            Dictionary<string, ReusedTake> reusable = _runFlags.RegenerateVideoClips(context.Execution.Id)
                ? new(StringComparer.Ordinal)
                : await FindReusableAsync(context.Execution, providerKey, redoTakes, ct);

            List<TakePlan> buy = [.. redoTakes.Where(t => !reusable.ContainsKey(t.Hash))];
            Dictionary<string, TakeOutcome> outcomes = new(StringComparer.Ordinal);
            Dictionary<string, string> promptChecks = new(StringComparer.Ordinal);
            decimal estimated = 0m;

            if (buy.Count > 0)
            {
                // T-005: screen the prompt BEFORE the atomic reservation, so a blocked prompt buys
                // nothing. An outage or the mid band proceeds (generation fails open).
                if (_moderation is not null)
                {
                    var attribution = new ModerationAttribution(
                        context.Execution.OrganizationId,
                        context.Execution.ProjectId,
                        context.Execution.InitiatedByUserId,
                        context.Execution.Id,
                        context.StepResultId,
                        ModerationSurface.GenerationPrompt,
                        ModerationContentKind.Text);
                    foreach (string prompt in buy.Select(t => t.Config.Prompt).Distinct(StringComparer.Ordinal))
                    {
                        ModerationDecision decision = await _moderation.ModerateAsync(
                            new ModerationInput(prompt, [], ModerationContentKind.Text), attribution, ct);
                        if (decision.Action == ModerationAction.Block)
                        {
                            return Failure(context, sw, "CONTENT_BLOCKED", decision.UserMessage);
                        }

                        if (!decision.Available)
                        {
                            _logger.LogWarning(
                                "Content moderation was unavailable for the AudioGenerate prompt at step {StepOrder}; proceeding because generation fails open",
                                step.StepOrder);
                        }
                        else if (decision.Action is ModerationAction.Allow or ModerationAction.Review)
                        {
                            // The positive record: a real verdict was given. An outage (above) leaves none.
                            promptChecks[prompt] = decision.Action.ToString();
                        }
                    }
                }

                decimal stepCap = config.MaxSpendCredits is { } cap ? (decimal)cap : buy.Sum(t => t.Credits);
                await context.ReportProgressAsync("Reserving the generation budget");
                VideoGenerationReservation reservation = await _budgetGate.ReserveAsync(
                    new VideoGenerationReservationRequest(
                        context.Execution.ProjectId, context.Execution.Id, context.StepResultId, providerKey, stepCap,
                        [.. buy.Select(t => new VideoGenerationTakeRequest(t.ClipId, t.Hash, t.Credits))],
                        Scope: VideoGenerationBudgetScope.Audio),
                    ct);

                if (!reservation.Allowed)
                    return Denied(context, sw, reservation.DenyReason);

                estimated = buy.Sum(t => t.Credits);

                // Terminal writes use CancellationToken.None so a stopped step still records each take's state.
                await using WorkflowEngineDbContext db = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
                for (int i = 0; i < reservation.Takes.Count; i++)
                {
                    ReservedTake reserved = reservation.Takes[i];
                    TakePlan plan = buy[i];
                    await context.ReportProgressAsync($"Generating {Describe(config.Kind)} take {plan.Take} of {config.Takes}",
                        (int)(10 + (80.0 * i / reservation.Takes.Count)));
                    outcomes[plan.Hash] = await RunTakeAsync(
                        context, plan.Config, provider, client, model, capabilities, plan, reserved, db, ct);
                }
            }

            // ---- 5. Register, then report ----
            return await CompleteAsync(context, sw, config, provider, model, takes, reusable, outcomes, estimated, carried, changeScope, promptChecks);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Only reachable before the gate reserved anything (every take path records its own stop).
            throw;
        }
        catch (ModelUnpricedException unpriced)
        {
            // QA-P1-17: a platform audio model with no effective price is a catalog gap with its own code and class,
            // never folded into AUDIO_GENERATE_FAILED. FailureClassifier maps MODEL_UNPRICED to PlatformInternal, so
            // the step is refunded. The message names no internal id.
            _logger.LogWarning(unpriced, "AudioGenerate step {StepOrder} refused an unpriced model", step.StepOrder);
            return Failure(context, sw, unpriced.Code, unpriced.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioGenerate step {StepOrder} failed unexpectedly", step.StepOrder);
            return Failure(context, sw, "AUDIO_GENERATE_FAILED", "The audio generation failed unexpectedly. Try the step again; if it keeps failing, contact support.");
        }
    }

    // ------------------------------------------------------------------ pricing

    private sealed record Priced(string Model, AudioEndpoint? Endpoint, string? Refusal);

    /// <summary>The model, its catalogue entry and the pre-gate checks; <see cref="Priced.Refusal"/> when any fails.</summary>
    private static Priced Price(AudioGenerateStepConfig config, ResolvedAudioGenerationProvider provider)
    {
        string? model = config.ResolveModel(provider.Kind, provider.ModelName);
        if (model is null)
        {
            return new Priced(string.Empty, null,
                $"Provider '{provider.Name}' is of kind {provider.Kind}, which serves no {Describe(config.Kind)}.");
        }

        if (AudioModelCatalog.Instance.ModelProblem(provider.Kind, config.Kind, model) is { } problem)
            return new Priced(model, null, problem);

        AudioEndpoint endpoint = AudioModelCatalog.Instance.Find(provider.Kind, model)!;
        List<string> errors = config.ValidateFor(endpoint);
        return new Priced(model, endpoint, errors.Count > 0 ? string.Join("; ", errors) : null);
    }

    // ------------------------------------------------------------------ one take

    private sealed record TakePlan(int Take, string ClipId, string Hash, AudioGenerateStepConfig Config, decimal Credits, bool Redo = true);

    /// <summary>One take's override from a change request.</summary>
    private sealed record TakeOverride(string? Prompt, double? DurationSec)
    {
        public bool ChangesAnything => Prompt is not null || DurationSec is not null;
    }

    /// <summary>The takes a change request re-makes and what each is re-made with.</summary>
    private sealed record AudioChange(HashSet<int> Redo, Dictionary<int, TakeOverride> Overrides, HashSet<int> Retake);

    /// <summary>
    /// Takes named by <c>itemIds</c> / override keys (<c>a{k}</c>) are re-made; with none named, or with a <c>*</c> override,
    /// the whole step is. Null when the request names no take of this step.
    /// </summary>
    private static AudioChange? PlanChange(ChangeRequestPayload payload, int takeCount)
    {
        HashSet<string> explicitIds = new(StringComparer.Ordinal);
        foreach (string id in payload.ItemIds ?? [])
            explicitIds.Add(id);
        IReadOnlyDictionary<string, JsonElement>? star = null;
        foreach ((string key, IReadOnlyDictionary<string, JsonElement> keys) in payload.Overrides ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>())
        {
            if (key == ChangeRequestValidator.AllItems)
                star = keys;
            else
                explicitIds.Add(key);
        }

        bool whole = explicitIds.Count == 0 || star is not null;
        HashSet<int> redo = [];
        for (int k = 1; k <= takeCount; k++)
        {
            if (whole || explicitIds.Contains(StepChangeCapabilities.AudioTakeId(k)))
                redo.Add(k);
        }

        if (redo.Count == 0)
            return null;

        Dictionary<int, TakeOverride> overrides = [];
        HashSet<int> retake = [];
        foreach (int k in redo)
        {
            string? prompt = null;
            double? duration = null;
            bool again = false;
            void Read(IReadOnlyDictionary<string, JsonElement>? keys)
            {
                if (keys is null)
                    return;
                if (keys.TryGetValue("prompt", out JsonElement p) && p.ValueKind == JsonValueKind.String)
                    prompt = p.GetString();
                if (keys.TryGetValue("durationSec", out JsonElement d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out double v))
                    duration = v;
                if (keys.TryGetValue("retake", out JsonElement r))
                    again = r.ValueKind == JsonValueKind.True;
            }

            Read(star);
            if (payload.Overrides is not null && payload.Overrides.TryGetValue(StepChangeCapabilities.AudioTakeId(k), out IReadOnlyDictionary<string, JsonElement>? own))
                Read(own);
            overrides[k] = new TakeOverride(prompt, duration);
            if (again)
                retake.Add(k);
        }

        return new AudioChange(redo, overrides, retake);
    }

    private const int MaxManifestBytes = 512 * 1024;

    /// <summary>True when <paramref name="key"/> lies under this project's <paramref name="category"/> and has no traversal.</summary>
    private static bool UnderProject(Guid projectId, string? key, string category) =>
        !string.IsNullOrEmpty(key)
        && key.StartsWith($"projects/{projectId:D}/{category}/", StringComparison.Ordinal)
        && !key.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// The previous run's takes manifest keyed by take number; null when it is missing, foreign (outside this project's
    /// <c>agentFiles</c> tree) or unreadable. Read project-scoped and size-bounded.
    /// </summary>
    private async Task<Dictionary<int, JsonObject>?> ReadPreviousManifestAsync(Guid projectId, string? key, CancellationToken ct)
    {
        if (!UnderProject(projectId, key, "agentFiles"))
            return null;
        string localPath = Path.Combine(Path.GetTempPath(), $"reelbolt-audio-prev-{Guid.NewGuid():N}.json");
        try
        {
            await _workspace.DownloadStorageKeyToFileAsync(projectId, key!, localPath, MaxManifestBytes, ct);
            JsonNode? root = JsonNode.Parse(await File.ReadAllTextAsync(localPath, ct));
            if (root?["takes"] is not JsonArray array)
                return null;
            Dictionary<int, JsonObject> result = [];
            foreach (JsonNode? node in array)
            {
                if (node is JsonObject entry
                    && entry["take"] is JsonValue n && n.TryGetValue(out int take)
                    && (entry["storageKey"] is null || entry["storageKey"] is JsonValue { } sk && sk.TryGetValue(out string? _)))
                    result[take] = entry;
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the previous audio takes manifest for project {ProjectId}", projectId);
            return null;
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    /// <summary>A failed take carries as failed; a delivered one only while its object is still in this project's store.</summary>
    private async Task<bool> CanCarryAsync(Guid projectId, JsonObject prior, CancellationToken ct)
    {
        string? key = prior["storageKey"]?.GetValue<string>();
        if (key is null)
            return true;
        return UnderProject(projectId, key, "outputFiles") && await _workspace.ArtifactExistsAsync(projectId, key, ct);
    }

    private sealed record ReusedTake(Guid JobId, string StorageKey);

    private sealed record TakeOutcome(
        string? Code, string? Detail, string? StorageKey, string? MimeType, string? Extension, long SizeBytes, double? DurationSec,
        ExternalGenerationJob? Row)
    {
        public bool Delivered => StorageKey is not null && Code is null;

        public static TakeOutcome Failed(string code, string detail, ExternalGenerationJob? row) =>
            new(code, detail, null, null, null, 0, null, row);
    }

    private async Task<TakeOutcome> RunTakeAsync(
        StepExecutionContext context, AudioGenerateStepConfig config, ResolvedAudioGenerationProvider provider,
        IAudioGenerationClient client, string model, AudioGenerationCapabilities capabilities,
        TakePlan plan, ReservedTake reserved, WorkflowEngineDbContext db, CancellationToken ct)
    {
        ExternalGenerationJob? job = null;
        bool invoked = false;
        try
        {
            job = await db.ExternalGenerationJobs.FirstOrDefaultAsync(j => j.Id == reserved.JobId, CancellationToken.None)
                  ?? throw new InvalidOperationException($"Reserved ledger row {reserved.JobId} for {plan.ClipId} was not found.");

            if (reserved.Existing)
            {
                // An earlier attempt reserved this take and may have been billed for it; the synchronous vendor call
                // left no job to poll, so re-submitting could pay twice. The row is left exactly as it is.
                return TakeOutcome.Failed("RESERVATION_ORPHANED",
                    $"A prior attempt of this step reserved this take (ledger row {job.Id}, CreatedAt {job.CreatedAt:O} UTC" +
                    (job.RemoteJobId is null ? string.Empty : $", vendor id {job.RemoteJobId}") + ") and its outcome is " +
                    "unknown. It was not re-submitted, to avoid double billing; it stays charged against the budget until " +
                    "the UTC day rolls over. Check the provider console for a generation near that time.", job);
            }

            if (ct.IsCancellationRequested)
            {
                await SaveAsync(db, job, ExternalGenerationJobStatus.Cancelled);
                return TakeOutcome.Failed("NOT_SUBMITTED", "The step was stopped before this take was sent; its reservation was released.", job);
            }

            AudioGenerationResult generated;
            try
            {
                invoked = true;
                // QA-P1-17: a client with a submit/poll split (fal) reports the vendor's request id the moment the
                // submit answers; record it on the row then, so a crash before the result download still leaves it.
                generated = await client.GenerateAsync(config.ToRequest(model), ct,
                    vendorId => RecordVendorRequestIdAsync(db, job, vendorId));
            }
            catch (AudioGenerationException ex)
            {
                return await VendorRefusalAsync(db, job, ex, provider, plan.Take);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // The client's own schema check refused the request: nothing was sent.
                await SaveAsync(db, job, ExternalGenerationJobStatus.Failed);
                return TakeOutcome.Failed("AUDIO_REQUEST_INVALID", ex.Message, job);
            }
            catch (Exception ex)
            {
                // A timeout, a transport failure mid-answer or a stop during the call: the vendor may have generated
                // (and billed) the take, so the row stays Reserved and charged. S-59: the exception text (hosts,
                // addresses, inner transport detail) is logged, never written to the tenant-visible step.
                _logger.LogWarning(ex, "AudioGenerate take {Take} (row {JobId}): the vendor call's outcome is unknown", plan.Take, job.Id);
                await SaveAsync(db, job, null);
                return TakeOutcome.Failed("SUBMIT_OUTCOME_UNKNOWN",
                    "The generation's outcome is unknown (the call timed out or the connection failed); the reservation stays charged.", job);
            }

            // Billed from here: record it before anything else can fail, and deliver even if the step is being stopped.
            job.RemoteJobId = generated.VendorRequestId is { } vendorId ? Bound(vendorId, 200) : null;
            await SaveAsync(db, job, ExternalGenerationJobStatus.Submitted);
            return await DeliverAsync(context, config, provider, model, capabilities, plan, generated, job, db);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioGenerate take {Take} of execution {ExecutionId} failed unexpectedly", plan.Take, context.Execution.Id);
            if (job is null)
            {
                return TakeOutcome.Failed("AUDIO_GENERATE_FAILED",
                    $"The take's ledger row could not be loaded ({ex.GetType().Name}); it stays as the gate wrote it.", null);
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

            return TakeOutcome.Failed("AUDIO_GENERATE_FAILED", "The take failed unexpectedly; its ledger state was recorded.", job);
        }
    }

    /// <summary>Maps a classified vendor refusal onto the row: released unless the vendor may have generated.</summary>
    /// <remarks>
    /// S-58, VideoGenerate's <c>IsDefiniteRejection</c> rule: a "retryable" answer is released (and so re-submitted
    /// by the step retry) ONLY when it is definite that nothing was generated — a 429, or a transport failure while
    /// connecting (name resolution, TCP connect, TLS handshake, the public-only connect refusal), i.e. before the
    /// request body left the engine. A 408, a 5xx or a connection dropped after the request was sent may have been
    /// accepted and billed, so the row stays Reserved (<c>SUBMIT_OUTCOME_UNKNOWN</c>) and a retry finds it and
    /// reports <c>RESERVATION_ORPHANED</c> instead of paying twice. Residual: a vendor that 5xx'd without
    /// generating keeps the take charged until the UTC day rolls over.
    /// <para>
    /// S-53: on a PLATFORM row (no owning organization) an account refusal — a rejected key (401/403, a missing
    /// endpoint) or exhausted vendor quota (402) — is the platform's own account state, so the tenant gets one fixed
    /// sentence and the vendor's text is logged. An organization's own row keeps the vendor detail: it is their account.
    /// </para>
    /// </remarks>
    private async Task<TakeOutcome> VendorRefusalAsync(
        WorkflowEngineDbContext db, ExternalGenerationJob job, AudioGenerationException ex, ResolvedAudioGenerationProvider provider, int take)
    {
        if (provider.OrganizationId is null && IsPlatformAccountRefusal(ex))
        {
            _logger.LogError(ex, "AudioGenerate take {Take} (row {JobId}): the platform audio provider refused the account (HTTP {Status})",
                take, job.Id, ex.VendorStatus);
            await SaveAsync(db, job, ExternalGenerationJobStatus.Failed);
            return TakeOutcome.Failed(ex.Code, PlatformAccountSentence, job);
        }

        if (ex.Kind == AudioGenerationErrorKind.Retryable && !IsDefinitelyNotGenerated(ex))
        {
            await SaveAsync(db, job, null);
            string status = ex.VendorStatus is { } code ? $"HTTP {code}" : "the connection failed after the request was sent";
            return TakeOutcome.Failed("SUBMIT_OUTCOME_UNKNOWN",
                $"The generation's outcome is unknown ({status}): the vendor may have generated and billed it, so the " +
                "reservation stays charged and the take is not re-submitted.", job);
        }

        bool mayHaveBilled = ex.Kind == AudioGenerationErrorKind.VendorFailed;
        await SaveAsync(db, job, mayHaveBilled ? null : ExternalGenerationJobStatus.Failed);

        string detail = Bound(ex.Message, 600);
        if (!string.IsNullOrWhiteSpace(ex.Suggestion))
            detail += " The provider suggests: " + Bound(ex.Suggestion, 1200);
        if (mayHaveBilled)
            detail += " The vendor may have billed it, so the reservation stays charged and the take is not retried.";

        return TakeOutcome.Failed(ex.Code, detail, job);
    }

    /// <summary>The fixed tenant sentence for a platform row's account refusal (S-53).</summary>
    internal const string PlatformAccountSentence =
        "The platform's audio provider is temporarily unavailable; nothing was generated and the reservation was released. " +
        "Try again later, and contact support if it persists.";

    private static bool IsPlatformAccountRefusal(AudioGenerationException ex) =>
        ex.Kind is AudioGenerationErrorKind.Configuration or AudioGenerationErrorKind.VendorQuota
        || ex.VendorStatus is 401 or 402 or 403;

    /// <summary>A retryable refusal that provably generated nothing: a 429, or a failure before the request was sent.</summary>
    private static bool IsDefinitelyNotGenerated(AudioGenerationException ex)
    {
        if (ex.VendorStatus is { } status) return status == 429;
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is NonPublicAddressException) return true;
            if (inner is HttpRequestException http)
            {
                return http.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError;
            }
        }

        return false;
    }

    private async Task<TakeOutcome> DeliverAsync(
        StepExecutionContext context, AudioGenerateStepConfig config, ResolvedAudioGenerationProvider provider, string model,
        AudioGenerationCapabilities capabilities, TakePlan plan, AudioGenerationResult generated, ExternalGenerationJob job,
        WorkflowEngineDbContext db)
    {
        CancellationToken none = CancellationToken.None;
        AudioGenerationResult measured;
        try
        {
            measured = await _durationProbe.EnsureDurationAsync(generated, none);
        }
        catch (AudioGenerationException ex)
        {
            _logger.LogWarning(ex, "Probing audio take {Take} (row {JobId}) failed", plan.Take, job.Id);
            return TakeOutcome.Failed("DELIVERY_FAILED",
                "The take was generated but its duration could not be read; it was not charged.", job);
        }

        double seconds = measured.DurationSec!.Value;
        decimal units = capabilities.BilledUnits(seconds);
        job.ActualCost = CreditCurrency.CreditsFromUsd(units * capabilities.UsdPerUnit);
        await SaveAsync(db, job, null);

        string extension = SafeExtension(measured.Extension);
        string storageKey;
        string localPath = Path.Combine(Path.GetTempPath(), $"reelbolt-audio-{Guid.NewGuid():N}{extension}");
        try
        {
            await File.WriteAllBytesAsync(localPath, measured.Bytes, none);
            storageKey = await _workspace.UploadArtifactAsync(
                context.Execution.ProjectId, localPath,
                $"{context.Execution.Id:D}/step-{context.Step.StepOrder}-a{plan.Take}{extension}",
                measured.MimeType, none, "outputFiles");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upload of audio take {Take} (row {JobId}) failed", plan.Take, job.Id);
            return TakeOutcome.Failed("DELIVERY_FAILED",
                "The take was generated but could not be stored; it was not charged.", job);
        }
        finally
        {
            TryDelete(localPath);
        }

        job.StorageKey = storageKey;
        await SaveAsync(db, job, ExternalGenerationJobStatus.Succeeded);
        // S-60: the tenant is charged only for a take it received. The ledger row above keeps the vendor's bill.
        await RecordUsageAsync(config, provider, model, capabilities, units, job);
        return new TakeOutcome(null, null, storageKey, measured.MimeType, extension, measured.Bytes.LongLength, seconds, job);
    }

    /// <summary>
    /// The credit-side record of a delivered take: billed units of the PROBED length (what the vendor bills), keyed
    /// <c>audiogen:{row}</c> so a replay never double-charges. The ledger row stays the spend-side record.
    /// </summary>
    private async Task RecordUsageAsync(
        AudioGenerateStepConfig config, ResolvedAudioGenerationProvider provider, string model,
        AudioGenerationCapabilities capabilities, decimal units, ExternalGenerationJob job)
    {
        if (_usageRecorder is null || units <= 0m) return;

        try
        {
            await _usageRecorder.RecordAsync(new UsageRecordRequest(
                capabilities.Unit, units, provider.ProviderId, provider.Kind, model,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                IdempotencyKey: $"audiogen:{job.Id:N}",
                Capability: AudioModelCatalog.CapabilityOf(config.Kind),
                AccountId: provider.AccountId), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metering audio generation row {JobId} failed", job.Id);
        }
    }

    // ------------------------------------------------------------------ completion

    private async Task<StepExecutionResult> CompleteAsync(
        StepExecutionContext context, Stopwatch sw, AudioGenerateStepConfig config, ResolvedAudioGenerationProvider provider,
        string model, List<TakePlan> takes, Dictionary<string, ReusedTake> reusable, Dictionary<string, TakeOutcome> outcomes,
        decimal estimated, Dictionary<int, JsonObject> carried, StepChangeRequestScope? changeScope,
        Dictionary<string, string> promptChecks)
    {
        Dictionary<string, ProjectWorkspaceFile>? filesByKey = null;
        string exec8 = context.Execution.Id.ToString("N")[..8];
        JsonArray outputTakes = [];
        JsonArray manifestTakes = [];
        List<(string Code, string Detail)> failures = [];
        string? firstKey = null;
        decimal totalCost = 0m;

        foreach (TakePlan plan in takes)
        {
            if (carried.TryGetValue(plan.Take, out JsonObject? prior))
            {
                // Verbatim from the earlier run: same file, same ledger row; this run paid nothing for it.
                JsonObject carriedManifest = (JsonObject)prior.DeepClone();
                carriedManifest["cost"] = 0m;
                carriedManifest["costBasis"] = "carried";
                carriedManifest["reused"] = true;
                carriedManifest["carried"] = true;
                JsonObject carriedEntry = (JsonObject)carriedManifest.DeepClone();
                carriedEntry.Remove("storageKey");
                carriedEntry.Remove("ledgerRowId");
                string? carriedKey = prior["storageKey"]?.GetValue<string>();
                firstKey ??= carriedKey;
                if (carriedKey is null)
                    failures.Add((prior["failureReason"]?.GetValue<string>() ?? "CARRIED_TAKE_FAILED", prior["detail"]?.GetValue<string>() ?? string.Empty));
                outputTakes.Add(carriedEntry);
                manifestTakes.Add(carriedManifest);
                continue;
            }

            bool reused = reusable.TryGetValue(plan.Hash, out ReusedTake? hit);
            TakeOutcome? outcome = reused ? null : outcomes[plan.Hash];
            string? key = reused ? hit!.StorageKey : outcome!.Delivered ? outcome.StorageKey : null;
            string? extension = key is null ? null : Path.GetExtension(key);
            string? fileName = key is null ? null : $"{config.EffectivePrefix}-{exec8}-{plan.Take}{extension}";
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
            if (key is null) failures.Add((outcome!.Code!, outcome.Detail ?? string.Empty));

            var entry = new JsonObject
            {
                ["take"] = plan.Take,
                ["status"] = key is null ? StatusFailed : StatusSucceeded,
                ["fileName"] = fileName,
                ["projectFileId"] = projectFileId?.ToString(),
                // A take served by content reuse was not measured this run; the request hash covers the requested
                // length, so that is the reused take's duration (later change requests price from it).
                ["durationSec"] = (outcome?.DurationSec ?? (reused ? plan.Config.DurationSec : null)) is { } d ? Math.Round(d, 3) : null,
                ["cost"] = cost,
                ["costBasis"] = basis,
                ["reused"] = reused,
                ["prompt"] = config.HasPlan ? null : Bound(plan.Config.Prompt, 160),
                ["failureReason"] = key is null ? outcome!.Code : null,
                ["detail"] = key is null ? outcome!.Detail : null,
            };
            outputTakes.Add(entry);

            JsonObject manifestEntry = (JsonObject)entry.DeepClone();
            manifestEntry["storageKey"] = key;
            manifestEntry["ledgerRowId"] = (reused ? hit!.JobId : outcome!.Row?.Id)?.ToString();
            // Kept permanently (the audit rows expire): the verdict the prompt of a take made in THIS run was given,
            // written only when a real Allow or Review came back. A reused or carried take carries none: it counts
            // through the step that made it. Read by the output moderation lane to clear wordless generated audio.
            if (!reused && key is not null && promptChecks.TryGetValue(plan.Config.Prompt, out string? promptCheck))
                manifestEntry["promptCheck"] = promptCheck;
            manifestTakes.Add(manifestEntry);
        }

        var meta = new JsonObject
        {
            ["costUnit"] = ExternalGenerationJob.CreditsUnit,
            ["estimatedCost"] = estimated,
            ["totalCost"] = totalCost,
            ["takesGenerated"] = outcomes.Values.Count(o => o.Delivered),
            ["takesReused"] = reusable.Count,
            ["takesFailed"] = failures.Count,
        };
        if (changeScope is not null)
        {
            meta["takesCarried"] = carried.Count;
            ChangeRequestEngineDiagnostics.Record(nameof(StepType.AudioGenerate), takes.Count(t => t.Redo), carried.Count);
            meta["changeRequest"] = new JsonObject
            {
                ["requestId"] = changeScope.RequestId.ToString("D"),
                ["redoneTakeIds"] = new JsonArray([.. takes.Where(t => t.Redo).Select(t => (JsonNode?)StepChangeCapabilities.AudioTakeId(t.Take))]),
                ["carriedTakeCount"] = carried.Count,
            };
        }

        string? manifestKey = await UploadManifestAsync(context, config, provider, model, manifestTakes, meta);
        bool allFailed = failures.Count == takes.Count;
        var output = new JsonObject
        {
            ["status"] = allFailed ? "failed" : "completed",
            ["kind"] = config.Kind.ToString(),
            ["model"] = model,
            ["takes"] = outputTakes,
            ["meta"] = meta,
        };

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

        // Every take failed: one code when they agree, else ALL_TAKES_FAILED; the class follows the code, and a vendor
        // failure on the organization's own row is a BYO error (D8).
        string code = failures.Select(f => f.Code).Distinct().Count() == 1 ? failures[0].Code : "ALL_TAKES_FAILED";
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
            FailureClass = ClassFor(code, provider),
        };
    }

    private static FailureClass ClassFor(string code, ResolvedAudioGenerationProvider provider)
    {
        FailureClass mapped = FailureClassifier.ForCode(code);
        bool byo = MeteringChatClient.BillingModeOf(provider.OrganizationId) == UsageBillingMode.Byo;
        return byo && VendorCodes.Contains(code) ? FailureClass.ByoProviderError : mapped;
    }

    private static (string Basis, decimal Cost) CostLabel(ExternalGenerationJob? row) => row switch
    {
        null => ("released", 0m),
        { Status: ExternalGenerationJobStatus.Failed or ExternalGenerationJobStatus.Cancelled } => ("released", 0m),
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
            _logger.LogWarning(ex, "Could not list project {ProjectId}'s files; generated takes are registered as new files", projectId);
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
            _logger.LogError(ex, "AudioGenerate step {StepOrder}: could not register '{FileName}' as a project file", context.Step.StepOrder, fileName);
            return null;
        }
    }

    private async Task<string?> UploadManifestAsync(
        StepExecutionContext context, AudioGenerateStepConfig config, ResolvedAudioGenerationProvider provider, string model,
        JsonArray takes, JsonObject meta)
    {
        var manifest = new JsonObject
        {
            ["version"] = 1,
            ["kind"] = config.Kind.ToString(),
            ["providerId"] = provider.ProviderId?.ToString(),
            ["model"] = model,
            ["takes"] = takes,
            ["meta"] = meta.DeepClone(),
        };

        string localPath = Path.Combine(Path.GetTempPath(), $"reelbolt-audio-manifest-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(localPath, manifest.ToJsonString(OutputJsonOptions), CancellationToken.None);
            return await _workspace.UploadArtifactAsync(
                context.Execution.ProjectId, localPath,
                $"audio-generate/{context.Execution.Id:D}/step-{context.Step.StepOrder}-takes.json",
                "application/json", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the audio takes manifest for execution {ExecutionId}; the takes are unaffected", context.Execution.Id);
            return null;
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    // ------------------------------------------------------------------ reuse

    /// <summary>
    /// Per request hash, an earlier delivered take for the identical request: same project and RESOLVED provider row,
    /// <c>Succeeded</c>, object still present. This execution's rows win, then the most recent. Any failure means "not
    /// reusable": the gate, not this lookup, bounds a fresh purchase.
    /// </summary>
    private async Task<Dictionary<string, ReusedTake>> FindReusableAsync(
        WorkflowExecution execution, Guid providerId, IReadOnlyList<TakePlan> takes, CancellationToken ct)
    {
        var result = new Dictionary<string, ReusedTake>(StringComparer.Ordinal);
        try
        {
            List<string> hashes = [.. takes.Select(t => t.Hash)];
            await using WorkflowEngineDbContext db = await _dbContextFactory.CreateDbContextAsync(ct);
            List<ExternalGenerationJob> rows = await db.ExternalGenerationJobs.AsNoTracking()
                .Where(j => j.ProjectId == execution.ProjectId && j.ProviderId == providerId && hashes.Contains(j.RequestHash)
                    && j.Status == ExternalGenerationJobStatus.Succeeded && j.StorageKey != null)
                .ToListAsync(ct);

            foreach (IGrouping<string, ExternalGenerationJob> group in rows.GroupBy(r => r.RequestHash))
            {
                foreach (ExternalGenerationJob row in group.OrderByDescending(r => r.ExecutionId == execution.Id).ThenByDescending(r => r.UpdatedAt))
                {
                    if (await _workspace.ArtifactExistsAsync(execution.ProjectId, row.StorageKey!, ct))
                    {
                        result[group.Key] = new ReusedTake(row.Id, row.StorageKey!);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not look up reusable audio takes for execution {ExecutionId}; generating fresh", execution.Id);
            result.Clear();
        }

        return result;
    }

    /// <summary>
    /// SHA-256 over every field that changes what the vendor generates, plus the take index; domain-separated from
    /// VideoGenerate's hashes by its first element and length-safe because the fields are a JSON array.
    /// </summary>
    internal static string RequestHash(AudioGenerateStepConfig config, InferenceProviderKind providerKind, string model, int take, Guid? retakeSalt = null)
    {
        List<object?> fields =
        [
            "audio-generate/v1", providerKind.ToString(), model, config.Kind.ToString(),
            config.HasPlan ? null : config.Prompt,
            config.HasPlan ? null : config.DurationSec.ToString("R", CultureInfo.InvariantCulture),
            config.Instrumental, config.HasPlan ? config.CompositionPlanJson : null, config.Loop,
            config.PromptInfluence?.ToString("R", CultureInfo.InvariantCulture), AudioOutputFormats.Default, take,
        ];
        // A retake carries its change request's id, so it never matches an earlier row: a fresh ledger row and idempotency key.
        if (retakeSalt is Guid salt)
            fields.Add($"retake:{salt:N}");
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
            text = code == VideoBudgetMessages.Code ? VideoBudgetMessages.GenericSentence : $"Audio generation was refused ({code}).";
        return Failure(context, sw, code, text);
    }

    private static StepExecutionResult Failure(
        StepExecutionContext context, Stopwatch sw, string code, string message, FailureClass? failureClass = null)
    {
        var output = new JsonObject
        {
            ["status"] = "failed",
            ["takes"] = new JsonArray(),
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
            FailureClass = failureClass ?? FailureClassifier.ForCode(code),
        };
    }

    private static async Task SaveAsync(WorkflowEngineDbContext db, ExternalGenerationJob job, ExternalGenerationJobStatus? status)
    {
        if (status is { } s) job.Status = s;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>QA-P1-17: persists the vendor's request id the moment the submit answers, before the poll and the
    /// result download, so a crash in that window still leaves the id on the ledger row.</summary>
    private static async Task RecordVendorRequestIdAsync(WorkflowEngineDbContext db, ExternalGenerationJob job, string vendorId)
    {
        job.RemoteJobId = Bound(vendorId, 200);
        await SaveAsync(db, job, ExternalGenerationJobStatus.Submitted);
    }

    private static string Describe(AudioGenerationKind kind) => kind == AudioGenerationKind.Music ? "music" : "sound effects";

    private static string SafeExtension(string? extension) =>
        extension is { Length: > 1 and <= 8 } e && e[0] == '.' && e[1..].All(char.IsAsciiLetterOrDigit) ? e.ToLowerInvariant() : ".bin";

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
