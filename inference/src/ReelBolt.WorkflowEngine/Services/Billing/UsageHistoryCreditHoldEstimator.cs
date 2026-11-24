using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Styles;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Briefs;

namespace ReelBolt.WorkflowEngine.Services.Billing;

/// <summary>
/// Sizes the per-step credit hold from the usage the ledger already records (B7). One call per step,
/// before the reservation is taken.
/// </summary>
public interface ICreditHoldEstimator
{
    /// <summary>
    /// The millicredits to hold for one step. Never throws and never returns a negative number; an
    /// estimator that cannot read history answers with B0's default for the step type, so the
    /// caller always has a bounded, non-zero figure for a chargeable step.
    /// </summary>
    ValueTask<long> HoldMillisAsync(
        Guid organizationId, StepType stepType, Guid? agentDefinitionId, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="HoldMillisAsync(Guid, StepType, Guid?, CancellationToken)"/> for a run that chose
    /// <paramref name="outputResolution"/> (short side; null = Auto). The default ignores the choice.
    /// </summary>
    ValueTask<long> HoldMillisAsync(
        Guid organizationId, StepType stepType, Guid? agentDefinitionId, int? outputResolution, CancellationToken ct = default) =>
        HoldMillisAsync(organizationId, stepType, agentDefinitionId, ct);

    /// <summary>
    /// As <see cref="HoldMillisAsync(Guid, StepType, Guid?, int?, CancellationToken)"/>, also told the
    /// step's <paramref name="agentType"/> so an Agent step that renders the output (granted
    /// <c>SandboxRender</c>) can be scaled like a render (<see cref="ResolutionHoldScaling"/>). The
    /// default ignores the agent type.
    /// </summary>
    ValueTask<long> HoldMillisAsync(
        Guid organizationId, StepType stepType, Guid? agentDefinitionId, AgentType? agentType, int? outputResolution, CancellationToken ct = default) =>
        HoldMillisAsync(organizationId, stepType, agentDefinitionId, outputResolution, ct);

    /// <summary>
    /// the hold for <paramref name="step"/> as one step of <paramref name="runSteps"/>, from the
    /// run-level plan the Inference API's <c>RequiredMillis</c> is the sum of
    /// (<see cref="RunCreditHolds"/>): a history step keeps its p90, a cold charged step holds the larger
    /// of its previous default and its share of the low edge of the cold steps' usual range, a
    /// not-charged step 0.
    /// A VideoGenerate step is the caller's: consult <see cref="TryVideoGenerateHoldMillisAsync"/>
    /// first. Never throws (except on cancellation) and never answers 0 for a charged step; the
    /// default implementation is the per-step <see cref="HoldMillisAsync(Guid, StepType, Guid?, CancellationToken)"/>.
    /// This is the plan for an Auto run; the resolution-aware overload plans at the run's resolution.
    /// </summary>
    ValueTask<long> RunHoldMillisAsync(
        Guid organizationId, IReadOnlyList<WorkflowStep> runSteps, WorkflowStep step, CancellationToken ct = default)
        => HoldMillisAsync(organizationId, step.StepType, step.AgentDefinitionId, ct);

    /// <summary>
    /// <see cref="RunHoldMillisAsync(Guid, IReadOnlyList{WorkflowStep}, WorkflowStep, CancellationToken)"/>
    /// for a run that chose <paramref name="outputResolution"/> (short side; null = Auto). The history-backed
    /// implementation (<see cref="UsageHistoryCreditHoldEstimator"/>) answers the FINAL hold of the shared
    /// resolution-aware plan (<see cref="RunCreditHolds"/>, the one the Inference API sums), so the two cannot
    /// disagree. This default, for an estimator without history, scales the plain plan's hold when the step
    /// renders the output (<see cref="ResolutionHoldScaling.RendersOutput"/> with the step's loaded agent type):
    /// times <see cref="OutputResolution.PixelFactor"/> above 1, never shrunk.
    /// </summary>
    async ValueTask<long> RunHoldMillisAsync(
        Guid organizationId, IReadOnlyList<WorkflowStep> runSteps, WorkflowStep step, int? outputResolution, CancellationToken ct = default)
    {
        long hold = await RunHoldMillisAsync(organizationId, runSteps, step, ct).ConfigureAwait(false);
        return ResolutionHoldScaling.ScaleHold(
            hold, ResolutionHoldScaling.RendersOutput(step.StepType, step.AgentDefinition?.AgentType), outputResolution);
    }

    /// <summary>
    /// B6c: the hold for a <see cref="StepType.VideoGenerate"/> step, sized from its OWN settings —
    /// the clips it will order, at the per-second rate card price the settlement will use
    /// (<see cref="VideoGenerateCreditPricing"/>). <c>null</c> when the step cannot be priced that
    /// way (no config, no provider row, no rate card), in which case the caller must fall back to
    /// <see cref="HoldMillisAsync"/> rather than hold nothing: a VideoGenerate step with a zero hold
    /// is never settled, which is the leak B6c closes.
    /// </summary>
    ValueTask<long?> TryVideoGenerateHoldMillisAsync(
        string? videoGenerateConfigJson, CancellationToken ct = default);

    /// <summary>
    /// T-108: the hold for a <see cref="StepType.AudioGenerate"/> step, sized from its OWN settings — takes x the
    /// billed units of the chosen music/SFX model, at the price row its settlement uses
    /// (<see cref="AudioGenerateCreditPricing"/>). <c>null</c> when it cannot be priced that way; the caller then falls
    /// back to <see cref="HoldMillisAsync(Guid, StepType, Guid?, CancellationToken)"/>, never to a zero hold. The
    /// default answers null, so an estimator without provider and price context always takes the fallback.
    /// </summary>
    ValueTask<long?> TryAudioGenerateHoldMillisAsync(
        string? audioGenerateConfigJson, CancellationToken ct = default) => ValueTask.FromResult<long?>(null);

    /// <summary>
    /// The hold for a <see cref="StepType.ImageGenerate"/> step, sized from its OWN settings — pictures x the chosen model's
    /// price-book row (<see cref="ImageGenerateCreditPricing"/>). <c>null</c> when it cannot be priced that way; the caller
    /// then falls back to the cold-start row, never to a zero hold. The default answers null.
    /// </summary>
    ValueTask<long?> TryImageGenerateHoldMillisAsync(
        string? imageGenerateConfigJson, CancellationToken ct = default) => ValueTask.FromResult<long?>(null);

    /// <summary>
    /// The hold for an <see cref="StepType.AudioCleanup"/> step, sized from its OWN config: <c>0</c> for a local target (free),
    /// the price-book hold of the reference length for a hosted one (<see cref="AudioCleanupCreditPricing"/>). <c>null</c> when it
    /// cannot be priced (no provider or price): the caller then falls back to the cold-start row, never to a zero hold. The
    /// default answers null.
    /// </summary>
    ValueTask<long?> TryAudioCleanupHoldMillisAsync(
        string? audioCleanupConfigJson, CancellationToken ct = default) => ValueTask.FromResult<long?>(null);

    /// <summary>
    /// The hold for a <see cref="StepType.VideoEnhance"/> step, sized from its OWN config: <c>0</c> for a local operation (free),
    /// the price-book hold of the config's length bound for a hosted one (<see cref="VideoEnhanceStepPricing"/>). <c>null</c> when it
    /// cannot be priced: the caller then falls back to the cold-start row, never to a zero hold. The default answers null.
    /// </summary>
    ValueTask<long?> TryVideoEnhanceHoldMillisAsync(
        string? videoEnhanceConfigJson, CancellationToken ct = default) => ValueTask.FromResult<long?>(null);
}

/// <summary>
/// <see cref="ICreditHoldEstimator"/> over <c>usage_events</c>. Registered <b>scoped</b>, which is one
/// instance per execution (<see cref="Execution.WorkflowExecutorService"/> is scoped too), so the
/// organization's sample set is loaded <b>once per run</b> rather than once per step and reused for
/// every step of that run. The run's hold plan (<see cref="RunHoldMillisAsync"/>) is likewise computed
/// once per run, through the same <see cref="RunCreditHolds"/> the Inference API sums into
/// <c>RequiredMillis</c>: agent types from <c>agent_definitions</c> (platform rows and the execution
/// organization's own only), cold profiles priced by <see cref="ProfilePriceTable"/> with
/// <paramref name="videoProviders"/> (the engine's provider resolver, chat included) and
/// <paramref name="prices"/>.
///
/// <para>
/// The load is lazy and failure-tolerant by construction: a query that throws (the engine's
/// <c>usage_events</c> mapping is read-only, and a deployment that has not run the B2 migration yet
/// will fail here) is logged and degrades to <see cref="StepCreditDefaults"/>. Billing must never be
/// able to fail a step that the gate itself would have allowed — the gate remains the enforcement
/// point, and it fails closed on its own.
/// </para>
/// </summary>
public sealed class UsageHistoryCreditHoldEstimator(
    IDbContextFactory<WorkflowEngineDbContext> factory,
    IOptions<StepCreditEstimateOptions> options,
    ILogger<UsageHistoryCreditHoldEstimator> logger,
    TimeProvider? time = null,
    IInferenceProviderResolver? videoProviders = null,
    IVideoGenerationClientFactory? videoClients = null,
    IModelPriceService? prices = null,
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null) : ICreditHoldEstimator
{
    private readonly StepCreditEstimateOptions _options = options.Value;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _planGate = new(1, 1);
    private Task<StepCreditEstimator>? _estimator;
    private (Guid Organization, string Fingerprint, Task<RunHoldPlan> Plan)? _plan;

    /// <summary>
    /// B6c's VideoGenerate arm. Optional like everything else here, and a null triple simply means
    /// the step falls back to its B0 default row — never to a zero hold.
    /// </summary>
    public async ValueTask<long?> TryVideoGenerateHoldMillisAsync(
        string? videoGenerateConfigJson, CancellationToken ct = default)
    {
        if (videoProviders is null || videoClients is null || prices is null)
        {
            return null;
        }

        try
        {
            return await VideoGenerateCreditPricing
                .TryHoldMillisAsync(videoGenerateConfigJson, videoProviders, videoClients, prices, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Same posture as the history load below: an estimator that cannot answer must not fail
            // a step the gate would have allowed. The caller's fallback is the B0 default row.
            logger.LogWarning(ex,
                "Pricing a VideoGenerate step's credit hold from its own settings failed; falling back to B0's default");
            return null;
        }
    }

    /// <summary>T-108's AudioGenerate arm: the same posture as the VideoGenerate one (a failure is a null, never a 0).</summary>
    public async ValueTask<long?> TryAudioGenerateHoldMillisAsync(
        string? audioGenerateConfigJson, CancellationToken ct = default)
    {
        if (videoProviders is null || prices is null)
        {
            return null;
        }

        try
        {
            return await AudioGenerateCreditPricing
                .TryHoldMillisAsync(audioGenerateConfigJson, videoProviders, prices, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Pricing an AudioGenerate step's credit hold from its own settings failed; falling back to B0's default");
            return null;
        }
    }

    /// <summary>The ImageGenerate arm: the same posture as the AudioGenerate one (a failure is a null, never a 0).</summary>
    public async ValueTask<long?> TryImageGenerateHoldMillisAsync(
        string? imageGenerateConfigJson, CancellationToken ct = default)
    {
        if (videoProviders is null || prices is null)
        {
            return null;
        }

        try
        {
            return await ImageGenerateCreditPricing
                .TryHoldMillisAsync(imageGenerateConfigJson, videoProviders, prices, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Pricing an ImageGenerate step's credit hold from its own settings failed; falling back to the cold-start row");
            return null;
        }
    }

    /// <summary>The AudioCleanup arm: a local target holds nothing, a hosted one the price-book hold (a failure is a null, never a 0).</summary>
    public async ValueTask<long?> TryAudioCleanupHoldMillisAsync(
        string? audioCleanupConfigJson, CancellationToken ct = default)
    {
        if (videoProviders is null || prices is null)
        {
            // No provider or price context: a local target is still free, anything else takes the cold-start row.
            return StepCreditDefaults.IsLocalAudioCleanup(audioCleanupConfigJson) ? 0L : null;
        }

        try
        {
            return await AudioCleanupCreditPricing
                .TryStepHoldMillisAsync(audioCleanupConfigJson, videoProviders, prices, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Pricing an AudioCleanup step's credit hold from its own settings failed; falling back to the cold-start row");
            return null;
        }
    }

    /// <summary>The VideoEnhance arm: a local operation holds nothing, a hosted one the price-book hold (a failure is a null, never a 0).</summary>
    public async ValueTask<long?> TryVideoEnhanceHoldMillisAsync(
        string? videoEnhanceConfigJson, CancellationToken ct = default)
    {
        if (videoProviders is null || prices is null)
        {
            // No provider or price context: a local operation is still free, anything else takes the cold-start row.
            return StepCreditDefaults.IsLocalVideoEnhance(videoEnhanceConfigJson) ? 0L : null;
        }

        try
        {
            return await VideoEnhanceStepPricing
                .TryStepHoldMillisAsync(videoEnhanceConfigJson, videoProviders, prices, ct, VideoEnhanceHostedSettings.From(configuration))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Pricing a VideoEnhance step's credit hold from its own settings failed; falling back to the cold-start row");
            return null;
        }
    }

    public ValueTask<long> HoldMillisAsync(
        Guid organizationId, StepType stepType, Guid? agentDefinitionId, CancellationToken ct = default) =>
        HoldMillisAsync(organizationId, stepType, agentDefinitionId, outputResolution: null, ct);

    public ValueTask<long> HoldMillisAsync(
        Guid organizationId, StepType stepType, Guid? agentDefinitionId, int? outputResolution, CancellationToken ct = default) =>
        HoldMillisAsync(organizationId, stepType, agentDefinitionId, agentType: null, outputResolution, ct);

    /// <summary>
    /// Decision 3: a step that renders the output — a <see cref="StepType.VideoCompile"/> encode, a
    /// <see cref="StepType.TimelineRender"/> delivery encode, a <see cref="StepType.Revise"/> re-render, or an Agent step granted <c>SandboxRender</c> (the
    /// Author) — scales with its output pixels, so its hold is multiplied by
    /// <see cref="OutputResolution.PixelFactor"/> when that is above 1 (4K holds four times the 1080p
    /// figure) and never shrunk for a smaller choice. QA-15: such a step's hold reads its Auto/1080p
    /// history only, and its own history at exactly that resolution may raise it, never lower it
    /// (<see cref="ResolutionHoldScaling.HoldAt"/>). The rule is <see cref="ResolutionHoldScaling"/>, the
    /// one the API's preflight (<c>WorkflowCostEstimator</c>) applies, so the two never disagree about a run.
    /// </summary>
    public async ValueTask<long> HoldMillisAsync(
        Guid organizationId, StepType stepType, Guid? agentDefinitionId, AgentType? agentType, int? outputResolution, CancellationToken ct = default)
    {
        // A step type that is deliberately not charged is answered without touching the database at
        // all. VideoGenerate used to be one of these; B6c made it chargeable and gave it a config-aware
        // arm of its own (TryVideoGenerateHoldMillisAsync, which the executor consults first). Reaching
        // this method with StepType.VideoGenerate therefore means that arm could not price the step, and
        // the answer here is its B0 default row -- non-zero, so the hold still happens.
        if (StepCreditDefaults.IsNotCharged(stepType) || organizationId == Guid.Empty)
        {
            return 0;
        }

        StepCreditEstimator estimator = await LoadAsync(organizationId, ct).ConfigureAwait(false);

        // The cold-start default at the live markup (T-032), read the way the run plan reads it; a render step reads
        // only its reference (Auto/1080p) samples and is then scaled to the run's resolution (QA-15).
        decimal markup = (await ProfilePriceTable.BuildAsync([], videoProviders, prices, logger, ct).ConfigureAwait(false)).Markup;
        bool renders = ResolutionHoldScaling.RendersOutput(stepType, agentType);
        long hold = Math.Max(0, estimator.Estimate(stepType, agentDefinitionId, null, markup, referenceOnly: renders).HoldMillis);

        return ResolutionHoldScaling.HoldAt(
            hold, renders, outputResolution, renders ? estimator.NativeHoldMillis(stepType, agentDefinitionId, outputResolution) : null);
    }

    public ValueTask<long> RunHoldMillisAsync(
        Guid organizationId, IReadOnlyList<WorkflowStep> runSteps, WorkflowStep step, CancellationToken ct = default)
        => RunHoldMillisAsync(organizationId, runSteps, step, outputResolution: null, ct);

    /// <summary>
    /// The step's FINAL hold in the run plan at <paramref name="outputResolution"/> (<see cref="RunCreditHolds"/>):
    /// the plan already applies the resolution, so nothing is scaled here.
    /// </summary>
    public async ValueTask<long> RunHoldMillisAsync(
        Guid organizationId, IReadOnlyList<WorkflowStep> runSteps, WorkflowStep step, int? outputResolution, CancellationToken ct = default)
    {
        if (StepCreditDefaults.IsNotCharged(step.StepType) || organizationId == Guid.Empty)
        {
            return 0;
        }

        // App inputs (docs/design/app-inputs.md 8.5, 9.3): the plan covers only the steps the inputs' branches can reach, so a
        // branch that cannot run takes no share of the cold-start holds. A run without app inputs plans over every step.
        if (ReelBolt.WorkflowEngine.Execution.Snapshots.AppInputsAmbient.Current is { } appInputs)
        {
            runSteps = appInputs.Reachable(runSteps);
        }

        int index = IndexOf(runSteps, step);
        if (index >= 0)
        {
            try
            {
                RunHoldPlan plan = await PlanAsync(organizationId, runSteps, outputResolution, ct).ConfigureAwait(false);
                long hold = plan.HoldsMillis[index];
                if (hold > 0)
                {
                    return hold;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Planning the credit holds of a run for organization {OrganizationId} failed; step {StepOrder} holds its per-step default",
                    organizationId, step.StepOrder);
            }
        }

        // Not in the run, no plan, or a zero for a charged step (history that measured 0): the per-step
        // hold at the run's resolution — the p90, else B0's default low, by the same resolution rule.
        return await HoldMillisAsync(
            organizationId, step.StepType, step.AgentDefinitionId, step.AgentDefinition?.AgentType, outputResolution, ct).ConfigureAwait(false);
    }

    private static int IndexOf(IReadOnlyList<WorkflowStep> runSteps, WorkflowStep step)
    {
        for (int i = 0; i < runSteps.Count; i++)
        {
            if (ReferenceEquals(runSteps[i], step) || (step.Id != Guid.Empty && runSteps[i].Id == step.Id))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// What a step's hold depends on beyond its type: a review loop's config (T-102: <c>visualCheck</c> adds one
    /// vision call per iteration). Empty for every other step, so their fingerprints are unchanged. The hash is
    /// only compared inside this process, so the randomised string hash is fine.
    /// </summary>
    private static string ConfigDigest(WorkflowStep step) =>
        step.StepType == StepType.ReviewLoop && !string.IsNullOrEmpty(step.ReviewLoopConfigJson)
            ? step.ReviewLoopConfigJson.GetHashCode(StringComparison.Ordinal).ToString("x", System.Globalization.CultureInfo.InvariantCulture)
            : step.StepType == StepType.VideoAnalyze && !string.IsNullOrEmpty(step.VideoAnalyzeConfigJson)
                ? step.VideoAnalyzeConfigJson.GetHashCode(StringComparison.Ordinal).ToString("x", System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;

    private async Task<RunHoldPlan> PlanAsync(Guid organizationId, IReadOnlyList<WorkflowStep> runSteps, int? outputResolution, CancellationToken ct)
    {
        string briefFiles = RunBriefAmbient.Current is { } ambient
            ? string.Join(';', Enum.GetValues<RunBriefFileRole>().Select(r => string.Join(',', ambient.FilesFor(r))))
            : string.Empty;
        string fingerprint = $"{outputResolution?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto"}#{briefFiles}#"
            + string.Join('|', runSteps.Select(s => $"{s.Id:N}:{s.StepOrder}:{(int)s.StepType}:{s.AgentDefinitionId:N}:{ConfigDigest(s)}"));
        await _planGate.WaitAsync(ct).ConfigureAwait(false);
        Task<RunHoldPlan> plan;
        try
        {
            if (_plan is not { } cached || cached.Organization != organizationId || cached.Fingerprint != fingerprint
                || cached.Plan.IsFaulted || cached.Plan.IsCanceled)
            {
                _plan = (organizationId, fingerprint, PlanCoreAsync(organizationId, [.. runSteps], outputResolution, ct));
            }

            plan = _plan.Value.Plan;
        }
        finally
        {
            _planGate.Release();
        }

        return await plan.ConfigureAwait(false);
    }

    private async Task<RunHoldPlan> PlanCoreAsync(Guid organizationId, IReadOnlyList<WorkflowStep> runSteps, int? outputResolution, CancellationToken ct)
    {
        StepCreditEstimator history = await LoadAsync(organizationId, ct).ConfigureAwait(false);
        IReadOnlyDictionary<Guid, AgentType> agentTypes = await LoadAgentTypesAsync(organizationId, runSteps, ct).ConfigureAwait(false);

        // The same two passes as the Inference API's estimate: a dry run names the quantities to price,
        // the shared table builder prices them (seed constants per key on any failure), and the shared
        // planner decides the holds. VideoGenerate is the executor's (declared price first), so no
        // declared prices are passed here, and its answer matches HoldMillisAsync's.
        IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>> footage = await LoadSourceFootageAsync(organizationId, runSteps, ct).ConfigureAwait(false);
        var recorder = new RecordingProfilePricer();
        RunCreditHolds.Plan(runSteps, history, null, agentTypes, recorder, outputResolution, footage);
        IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync(
            recorder.Requests, videoProviders, prices, logger, ct).ConfigureAwait(false);

        RunHoldPlan plan = RunCreditHolds.Plan(runSteps, history, null, agentTypes, pricer, outputResolution, footage);
        if (plan.UsedFallback)
        {
            logger.LogWarning(
                "The cold-start hold plan for organization {OrganizationId} could not be computed; cold steps hold B0's default low",
                organizationId);
        }

        return plan;
    }

    /// <summary>
    /// The agent type of every agent the run names: the steps' loaded navigations, then
    /// <c>agent_definitions</c> for the rest — platform rows (no organization) and the execution
    /// organization's own only, never another tenant's. Best effort: an unreadable table leaves those
    /// agents unknown (priced as the default profile), never a throw.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, AgentType>> LoadAgentTypesAsync(
        Guid organizationId, IReadOnlyList<WorkflowStep> runSteps, CancellationToken ct)
    {
        Dictionary<Guid, AgentType> agentTypes = ColdStartStepPricing.KnownAgentTypes(runSteps);
        List<Guid> missing = [.. runSteps.SelectMany(ColdStartStepPricing.BranchAgentIds)
            .Where(a => a != Guid.Empty && !agentTypes.ContainsKey(a))
            .Distinct()];
        if (missing.Count == 0)
        {
            return agentTypes;
        }

        try
        {
            await using WorkflowEngineDbContext db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            Guid? org = organizationId;
            var rows = await db.AgentDefinitions.AsNoTracking()
                .Where(a => missing.Contains(a.Id) && (a.OrganizationId == null || a.OrganizationId == org))
                .Select(a => new { a.Id, a.AgentType })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                agentTypes[row.Id] = row.AgentType;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Reading agent types for the hold plan of organization {OrganizationId} failed; those agents are priced as unknown",
                organizationId);
        }

        return agentTypes;
    }

    private async Task<StepCreditEstimator> LoadAsync(Guid organizationId, CancellationToken ct)
    {
        Task<StepCreditEstimator>? existing = _estimator;
        if (existing is not null)
        {
            return await existing.ConfigureAwait(false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_estimator is null)
            {
                _estimator = LoadCoreAsync(organizationId, ct);
            }

            return await _estimator.ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The known footage of the run's VideoAnalyze sources (<see cref="SourceFootageCredits"/>); empty on any failure, so a step holds today's figure.</summary>
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>>> LoadSourceFootageAsync(
        Guid organizationId, IReadOnlyList<WorkflowStep> runSteps, CancellationToken ct)
    {
        try
        {
            await using WorkflowEngineDbContext db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await SourceFootageCredits.ResolveAsync(db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, runSteps, organizationId, ct, RunBriefAmbient.Current is { } b ? b.FilesFor : null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading the source footage of the run for its credit holds failed; those steps hold their per-step default");
            return new Dictionary<int, IReadOnlyList<SourceFootage>>();
        }
    }

    private async Task<StepCreditEstimator> LoadCoreAsync(Guid organizationId, CancellationToken ct)
    {
        try
        {
            await using WorkflowEngineDbContext db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            DateTime since = _time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, _options.WindowDays));
            IReadOnlyList<StepCreditSample> samples = await StepCreditHistoryQuery.LoadAsync(
                db.UsageEvents, db.WorkflowStepResults, db.WorkflowSteps, db.WorkflowExecutions,
                organizationId, since, _options.MaxSamples, ct).ConfigureAwait(false);

            if (samples.Count == 0)
            {
                logger.LogInformation(
                    "No usage history for organization {OrganizationId}; step credit holds fall back to the B0 defaults",
                    organizationId);
            }

            return new StepCreditEstimator(samples, _options.MinSamples);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Reading the usage history of organization {OrganizationId} failed; step credit holds fall back to the B0 defaults",
                organizationId);
            return new StepCreditEstimator([], _options.MinSamples);
        }
    }
}
