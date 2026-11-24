using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// One model a <c>model</c> input offers on the app page (docs/design/app-inputs.md 8.2). <see cref="CostIndex"/> is a
/// ratio to the recommended option for a fixed reference unit of the capability, never money; null when it cannot be priced
/// (an organization's own model, or no price). <see cref="UnavailableReason"/> is plain words, set when
/// <see cref="Available"/> is false, with its stable code in <see cref="UnavailableCode"/> (<see cref="AppModelRules.RefusalCode"/>:
/// <c>notInWorkspace</c>, <c>wrongKind</c>, <c>notAllowed</c>, <c>wrongJob</c>; and <c>notInPlan</c>, docs/design/app-inputs.md 28, with
/// the plan feature it needs in <see cref="PlanFeature"/>: <c>video_generation</c>, <c>audio_generation</c>, <c>avatars</c>,
/// <c>resolution</c>) that the run page maps to its own words.
/// </summary>
public sealed record AppInputModelOption(
    Guid Id,
    string Label,
    string? Note,
    decimal? CostIndex,
    bool Recommended,
    bool Available,
    string? UnavailableReason = null,
    string? UnavailableCode = null,
    string? PlanFeature = null);

/// <summary>
/// The options of one model input: the options, in the order the page shows them (recommended first), and the field's own
/// label (and catalogue key, for a recipe's field) so a list of several fields can name each one.
/// </summary>
public sealed record AppInputModelOptionsResponse(
    string InputId, string Capability, string? Job, List<AppInputModelOption> Options, string? Label = null, string? LabelKey = null);

/// <summary>
/// The options of one <c>model</c> input (docs/design/app-inputs.md 8.2): the organization's selectable models of the
/// input's capability (<see cref="IAppInputModelResolverSource"/>, the step editor's own list), narrowed to the author's
/// <c>allowed</c> list when set, each marked available or not by the same <see cref="AppModelRules"/> the run gate applies
/// (so a model the page offers is a model the run accepts), with a relative price priced by the existing price paths
/// (<see cref="VideoGenerateCreditPricing"/>, <see cref="AudioGenerateCreditPricing"/>, <see cref="IModelPriceService"/>).
/// </summary>
public sealed class AppInputModelOptionsService(
    InferenceApiDbContext db,
    IAppInputModelResolverSource source,
    IInferenceProviderResolver? providers = null,
    IVideoGenerationClientFactory? videoClients = null,
    IModelPriceService? prices = null,
    IOrganizationEntitlements? entitlements = null)
{
    /// <summary>The reference clip a video option is priced on: one take of 6 seconds at the step's own resolution.</summary>
    public const int ReferenceVideoSeconds = 6;

    /// <summary>The reference length a music or sound-effect option is priced on.</summary>
    public const double ReferenceAudioSeconds = 30;

    /// <summary>The reference text a speech option is priced on (characters, metered as bytes).</summary>
    public const decimal ReferenceSpeechBytes = 1_000m;

    /// <summary>
    /// The options of <paramref name="inputId"/>, or null when the workflow or the input is not there (404). With
    /// <paramref name="ignoreAllowed"/> (the builder's "Allowed models" editor) the author's <c>allowed</c> list neither
    /// narrows nor marks the options: every model of the kind the workspace can select, judged on its job only.
    /// </summary>
    public async Task<AppInputModelOptionsResponse?> OptionsAsync(
        Guid? organizationId, Guid projectId, Guid workflowId, string inputId, CancellationToken ct, bool ignoreAllowed = false)
    {
        WorkflowDefinition? workflow = await db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);
        if (workflow is null || AppManifestJson.TryParse(workflow.AppManifestJson) is not { } manifest)
            return null;
        return await OptionsForAsync(organizationId, projectId, manifest, workflow.Steps, inputId, ct, ignoreAllowed);
    }

    /// <summary>
    /// The options of every <c>model</c> input of a page not saved yet (docs/design/app-inputs.md 28, L8 narrowing): a recipe's
    /// manifest and its dry-built steps, judged and priced exactly as <see cref="OptionsAsync"/> judges a created app.
    /// </summary>
    public async Task<IReadOnlyList<AppInputModelOptionsResponse>> AllOptionsAsync(
        Guid? organizationId, Guid projectId, AppManifest manifest, IReadOnlyList<WorkflowStep> steps, CancellationToken ct)
    {
        List<AppInputModelOptionsResponse> all = [];
        foreach (AppManifestInput input in (manifest.Inputs ?? []).Where(i => i.Kind == AppManifestVocabulary.KindInput && i.Id is not null
                     && AppInputTypes.TryParse(i.Type, out AppInputType type) && type == AppInputType.Model))
        {
            if (await OptionsForAsync(organizationId, projectId, manifest, steps, input.Id!, ct) is { } options)
                all.Add(options);
        }

        return all;
    }

    /// <summary>The options of <paramref name="inputId"/> of <paramref name="manifest"/> over <paramref name="workflowSteps"/>, or null when it is not a model input.</summary>
    public async Task<AppInputModelOptionsResponse?> OptionsForAsync(
        Guid? organizationId, Guid projectId, AppManifest manifest, IEnumerable<WorkflowStep> workflowSteps, string inputId, CancellationToken ct,
        bool ignoreAllowed = false)
    {
        AppManifestInput? input = (manifest.Inputs ?? []).FirstOrDefault(i =>
            i.Kind == AppManifestVocabulary.KindInput && i.Id == inputId
            && AppInputTypes.TryParse(i.Type, out AppInputType type) && type == AppInputType.Model);
        if (input is null)
            return null;
        if (ignoreAllowed)
            input = input with { Allowed = null };

        List<WorkflowStep> steps = [.. workflowSteps.OrderBy(s => s.StepOrder)];
        IReadOnlyList<AppBindingStep> bindingSteps = [.. steps.Select(AppBindingStep.From)];
        AppBindingMap map = AppBindingAnalysis.Analyze(manifest, bindingSteps);
        AppInputUse? modelUse = map.UsesOf(inputId).FirstOrDefault(u => u is { Kind: AppInputUseKind.Value, Field.IsModel: true });
        WorkflowStep? step = modelUse?.StepOrder is { } order ? steps.FirstOrDefault(s => s.StepOrder == order) : null;
        AppBindingStep? bindingStep = step is null ? null : AppBindingStep.From(step);

        string? path = modelUse?.Field?.Path;
        string capability = (bindingStep is null ? null : AppModelRules.CapabilityFor(bindingStep, path)) ?? input.Capability ?? nameof(InferenceProviderCapability.Chat);
        string? job = bindingStep is null ? null : AppModelRules.JobFor(bindingStep, path);

        IReadOnlyList<AppModelCandidate> candidates = await source.CandidatesAsync(organizationId, projectId, ct);
        // docs/design/app-inputs.md 28: a kind of model the plan does not include is marked on every option, as the run gate refuses it.
        string? planFeature = entitlements is not null && organizationId is { } org
            ? AppModelRules.PlanFeature(capability, job, await entitlements.GetAsync(org, ct), bindingStep is null ? null : AppModelRules.ClipShortSide(bindingStep))
            : null;
        List<AppModelCandidate> offered = [.. candidates.Where(c => c.Capability == capability
            && (input.Allowed is not { Count: > 0 } allowed || allowed.Contains(c.Id)))];

        // Only a model that can do the step's job is ever recommended (and so preselected): a default row that cannot
        // (a MiniMax default for a lip-sync step) would otherwise be offered and then fail the run.
        Guid? recommended = await RecommendedAsync(organizationId, input, [.. offered.Where(c => AppModelRules.ServesJob(c, job))], ct);
        Dictionary<Guid, long?> price = [];
        foreach (AppModelCandidate candidate in offered)
            price[candidate.Id] = candidate.IsPlatform ? await PriceAsync(candidate, step, ct) : null;
        // The ratio's base: the recommended option's price, else the cheapest priced option.
        List<long> priced = [.. price.Values.Where(v => v is > 0).Select(v => v!.Value)];
        long? baseline = recommended is { } r && price.GetValueOrDefault(r) is { } rp && rp > 0
            ? rp
            : priced.Count > 0 ? priced.Min() : null;

        List<AppInputModelOption> options = [];
        foreach (AppModelCandidate candidate in offered)
        {
            string? code = AppModelRules.RefusalCode(input, candidate.Id, capability, job, candidates, out _);
            string? reason = code is null ? null : AppModelRules.RefusalSentence(code);
            string? needs = null;
            if (code is null && planFeature is not null)
            {
                code = AppModelRules.NotInPlan;
                reason = AppModelRules.PlanSentence(planFeature);
                needs = planFeature;
            }

            decimal? index = baseline is { } b && price.GetValueOrDefault(candidate.Id) is { } p
                ? Math.Round((decimal)p / b, 2, MidpointRounding.AwayFromZero)
                : null;
            bool isRecommended = candidate.Id == recommended;
            options.Add(new AppInputModelOption(
                candidate.Id, candidate.Name, isRecommended ? "Recommended" : null, index, isRecommended, reason is null, reason, code, needs));
        }

        return new AppInputModelOptionsResponse(
            inputId, capability, job,
            [.. options.OrderByDescending(o => o.Recommended).ThenByDescending(o => o.Available).ThenBy(o => o.Label, StringComparer.Ordinal)],
            input.Label, input.LabelKey);
    }

    /// <summary>
    /// The recommended option (8.2): the author's <c>recommended</c>; for an agent input, the organization's per-agent
    /// choice, else the agent's own model; else the capability's default; else none. Only an offered model is recommended.
    /// </summary>
    private async Task<Guid?> RecommendedAsync(Guid? organizationId, AppManifestInput input, List<AppModelCandidate> offered, CancellationToken ct)
    {
        HashSet<Guid> ids = [.. offered.Select(c => c.Id)];
        if (input.Recommended is { } authored && ids.Contains(authored))
            return authored;

        if (input.Agent is { } agentId)
        {
            var agent = await db.AgentDefinitions.AsNoTracking()
                .Where(a => a.Id == agentId && (a.OrganizationId == null || a.OrganizationId == organizationId))
                .Select(a => new { a.AgentType, a.IsBuiltIn, a.InferenceProviderId })
                .FirstOrDefaultAsync(ct);
            if (agent is not null)
            {
                if (agent.IsBuiltIn && organizationId is { } org)
                {
                    Guid? orgChoice = await db.OrganizationAgentProviderOverrides.AsNoTracking()
                        .Where(o => o.OrganizationId == org && o.AgentType == agent.AgentType)
                        .Select(o => o.InferenceProviderId)
                        .FirstOrDefaultAsync(ct);
                    if (orgChoice is { } choice && ids.Contains(choice))
                        return choice;
                }

                if (agent.InferenceProviderId is { } own && ids.Contains(own))
                    return own;
            }
        }

        return offered.FirstOrDefault(c => c.IsDefault)?.Id;
    }

    /// <summary>What one option costs for the capability's reference unit, in millicredits, or null when it cannot be priced.</summary>
    private async Task<long?> PriceAsync(AppModelCandidate candidate, WorkflowStep? step, CancellationToken ct)
    {
        try
        {
            switch (candidate.Capability)
            {
                case nameof(InferenceProviderCapability.VideoGeneration) or nameof(InferenceProviderCapability.LipSync) when providers is not null && videoClients is not null && prices is not null:
                    return await VideoGenerateCreditPricing.TryHoldMillisAsync(
                        ReferenceConfig(step?.VideoGenerateConfigJson, candidate, ("durationSeconds", ReferenceVideoSeconds), ("takes", 1)),
                        providers, videoClients, prices, ct);
                case nameof(InferenceProviderCapability.MusicGeneration) or nameof(InferenceProviderCapability.SoundEffectGeneration)
                    when providers is not null && prices is not null:
                    string kind = candidate.Capability == nameof(InferenceProviderCapability.MusicGeneration) ? "Music" : "SoundEffect";
                    return await AudioGenerateCreditPricing.TryHoldMillisAsync(
                        ReferenceConfig(step?.AudioGenerateConfigJson ?? $$"""{"kind":"{{kind}}","prompt":"x"}""", candidate,
                            ("durationSec", ReferenceAudioSeconds), ("takes", 1)),
                        providers, prices, ct);
                case nameof(InferenceProviderCapability.ImageEdit) when prices is not null:
                    // A still (one picture) at the model's catalogued per-image price, at the tier the still call is billed
                    // (docs/design/app-inputs.md 28: nano-banana's vendor-default `resolution`), as the step's hold reads it.
                    (string stillModel, string? stillTier) = VideoGenerateCreditPricing.StillBillingKey(
                        Enum.Parse<InferenceProviderKind>(candidate.Kind), candidate.ModelCode);
                    return (await prices.PriceAsync(
                        Request(candidate, InferenceProviderCapability.ImageEdit, UsageKind.ImageEditImages, 1m) with { ModelCode = stillModel, Variant = stillTier },
                        ct)).CreditsMillis;
                case nameof(InferenceProviderCapability.SpeechSynthesis) when prices is not null:
                    return (await prices.PriceAsync(Request(candidate, InferenceProviderCapability.SpeechSynthesis, UsageKind.TtsInputBytes, ReferenceSpeechBytes), ct)).CreditsMillis;
                case nameof(InferenceProviderCapability.Chat) when prices is not null:
                    // The same fixed token mix as the step editor's Chat cost index (1M input, 250k output).
                    long input = (await prices.PriceAsync(Request(candidate, InferenceProviderCapability.Chat, UsageKind.LlmInputTokens, 1_000_000m), ct)).CreditsMillis;
                    long output = (await prices.PriceAsync(Request(candidate, InferenceProviderCapability.Chat, UsageKind.LlmOutputTokens, 250_000m), ct)).CreditsMillis;
                    return input + output;
                case nameof(InferenceProviderCapability.Vision) when prices is not null:
                    // A picture model is billed per token like a chat model: the same fixed mix, at its Vision prices.
                    long seen = (await prices.PriceAsync(Request(candidate, InferenceProviderCapability.Vision, UsageKind.LlmInputTokens, 1_000_000m), ct)).CreditsMillis;
                    long said = (await prices.PriceAsync(Request(candidate, InferenceProviderCapability.Vision, UsageKind.LlmOutputTokens, 250_000m), ct)).CreditsMillis;
                    return seen + said;
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static PriceRequest Request(AppModelCandidate candidate, InferenceProviderCapability capability, UsageKind unit, decimal quantity) =>
        new(candidate.Id, null, Enum.Parse<InferenceProviderKind>(candidate.Kind), capability, candidate.ModelCode, unit, null, UsageBillingMode.Platform, quantity);

    /// <summary>The step's own config (or a minimal one) with the candidate as its model and the reference length and takes.</summary>
    private static string ReferenceConfig(string? configJson, AppModelCandidate candidate, params (string Key, double Value)[] reference)
    {
        JsonObject config = (string.IsNullOrWhiteSpace(configJson) ? null : JsonNode.Parse(configJson) as JsonObject) ?? [];
        foreach (string key in config.Select(p => p.Key).ToList())
        {
            if (key.Equals(AppInputPlaceholders.AppInputsKey, StringComparison.OrdinalIgnoreCase)
                || key.Equals("providerId", StringComparison.OrdinalIgnoreCase) || key.Equals("model", StringComparison.OrdinalIgnoreCase)
                || reference.Any(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                config.Remove(key);
        }

        config["providerId"] = candidate.Id.ToString("D");
        config["model"] = candidate.ModelCode;
        foreach ((string key, double value) in reference)
            config[key] = value == Math.Floor(value) ? JsonValue.Create((int)value) : JsonValue.Create(value);
        return config.ToJsonString();
    }
}
