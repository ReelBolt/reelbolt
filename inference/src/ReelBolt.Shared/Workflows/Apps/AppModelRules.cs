using System.Text.Json;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// One model a <c>model</c> input may resolve to: a model capability the run's organization can select (the platform's,
/// fully priced, or the organization's own while its plan allows bring-your-own; the same rows as
/// <c>GET /api/v1/inference-providers/selectable</c>). <see cref="Id"/> is the capability id every <c>*ProviderId</c>
/// names; <see cref="ModelCode"/> the model the step sends; <see cref="Capability"/> and <see cref="Kind"/> are enum names.
/// </summary>
public sealed record AppModelCandidate(
    Guid Id,
    string Capability,
    string Kind,
    string ModelCode,
    string Name,
    bool IsPlatform,
    bool IsDefault = false);

/// <summary>
/// The pure rules of a <c>model</c> input (docs/design/app-inputs.md 8.1, 8.3), shared by the API's run gate (lane L2's
/// seam <c>IAppInputModelResolverSource</c>), its options endpoint and the engine's snapshot. A value is never trusted:
/// it resolves only to a candidate (selectable for the run's organization) of the capability the step uses, inside the
/// author's <c>allowed</c> list, able to do the job the step's own config asks for. Anything else is refused, never
/// replaced by a guess.
/// </summary>
public static class AppModelRules
{
    /// <summary>The job of a VideoGenerate step that shows an avatar with driving speech (still-then-animate).</summary>
    public const string TalkingHead = "talkingHead";

    /// <summary>The job of a VideoGenerate step that shows an avatar from its reference material.</summary>
    public const string Reference = "reference";

    /// <summary>The job of a VideoGenerate step with a first or last frame.</summary>
    public const string ImageToVideo = "imageToVideo";

    /// <summary>The job of every other VideoGenerate step.</summary>
    public const string TextToVideo = "textToVideo";

    /// <summary>The job of a talking head's still model (<c>@stillModel</c>, an <c>ImageEdit</c> model, docs/design/app-inputs.md 27).</summary>
    public const string AvatarStill = "avatarStill";

    /// <summary>
    /// The capability a model binding at <paramref name="path"/> on <paramref name="step"/> chooses from: <c>ImageEdit</c> for
    /// <c>@stillModel</c>, else <see cref="CapabilityFor(AppBindingStep)"/>.
    /// </summary>
    public static string? CapabilityFor(AppBindingStep step, string? path) =>
        path == AppBindableCatalog.StillModelKey && step.StepType == StepType.VideoGenerate
            ? nameof(InferenceProviderCapability.ImageEdit)
            : path == AppBindableCatalog.VisionModelKey && step.StepType == StepType.VideoAnalyze
                ? nameof(InferenceProviderCapability.Vision)
                : CapabilityFor(step);

    /// <summary>The job a model binding at <paramref name="path"/> asks for: <see cref="AvatarStill"/> for <c>@stillModel</c>, else <see cref="JobFor(AppBindingStep)"/>.</summary>
    public static string? JobFor(AppBindingStep step, string? path) =>
        path == AppBindableCatalog.StillModelKey && step.StepType == StepType.VideoGenerate ? AvatarStill : JobFor(step);

    /// <summary>
    /// The capability a <c>@model</c> binding on <paramref name="step"/> chooses from (by
    /// <see cref="InferenceProviderCapability"/> name), or null for a step type that binds none: VideoGenerate takes a
    /// <c>VideoGeneration</c> model (a talking head, one with avatar speech, a <c>LipSync</c> model), AudioGenerate the capability of its kind, Voiceover a <c>SpeechSynthesis</c> model.
    /// </summary>
    public static string? CapabilityFor(AppBindingStep step)
    {
        switch (step.StepType)
        {
            case StepType.VideoGenerate:
                // A talking head is lip-synced on a LipSync model, never a VideoGeneration one.
                return JobFor(step) == TalkingHead
                    ? nameof(InferenceProviderCapability.LipSync)
                    : nameof(InferenceProviderCapability.VideoGeneration);
            case StepType.Voiceover:
                return nameof(InferenceProviderCapability.SpeechSynthesis);
            case StepType.AudioGenerate:
                AudioGenerateStepConfig? audio = Read<AudioGenerateStepConfig>(step.ConfigJson, AudioGenerateStepConfig.JsonOptions);
                return audio is not null && Enum.IsDefined(audio.Kind) ? AudioModelCatalog.CapabilityOf(audio.Kind).ToString() : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The job a VideoGenerate step's own config asks of its model (8.1): an avatar with driving speech is
    /// <see cref="TalkingHead"/>, an avatar without it <see cref="Reference"/>, a first/last frame
    /// <see cref="ImageToVideo"/>, anything else <see cref="TextToVideo"/>. Null for every other step type.
    /// </summary>
    public static string? JobFor(AppBindingStep step)
    {
        if (step.StepType != StepType.VideoGenerate)
            return null;
        VideoGenerateStepConfig? config = Read<VideoGenerateStepConfig>(step.ConfigJson, VideoGenerateStepConfig.JsonOptions);
        if (config is null)
            return TextToVideo;
        // Driving speech is set only with an avatar (named by the step or, in an app, by the run brief's avatar).
        if (config.HasAvatar || config.HasAvatarAudio)
            return config.HasAvatarAudio ? TalkingHead : Reference;
        return config.HasFrameInputs ? ImageToVideo : TextToVideo;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> can do <paramref name="job"/>. Only what the catalogue states is used: a fal.ai
    /// endpoint declares its job (<see cref="FalVideoCatalog"/>), so a talking-head endpoint does only talking heads and a
    /// text-to-video endpoint takes no frame; a talking head needs a fal.ai talking-head endpoint. Other kinds and jobs are
    /// left to the executor's own model checks (which refuse before anything is reserved), as for an authored model.
    /// </summary>
    public static bool ServesJob(AppModelCandidate candidate, string? job)
    {
        // A still is one fal.ai instruction edit over several pictures of the avatar (AvatarStillMaker): an endpoint the
        // catalogue declares as an instruction edit that takes an image array, or the fal.ai default when no model is named.
        if (job == AvatarStill)
        {
            if (candidate.Capability != nameof(InferenceProviderCapability.ImageEdit) || candidate.Kind != nameof(InferenceProviderKind.FalAi))
                return false;
            FalImageEndpoint? endpoint = string.IsNullOrWhiteSpace(candidate.ModelCode)
                ? FalImageCatalog.Instance.DefaultFor(FalImageJob.Instruct)
                : FalImageCatalog.Instance.Find(candidate.ModelCode.Trim());
            return endpoint is { Job: FalImageJob.Instruct, ImageUrlsField: not null };
        }

        if (job is null)
            return true;

        // A lip-sync model is a fal.ai talking-head endpoint and does nothing else; a video-generation row no longer serves a talking head.
        if (candidate.Capability == nameof(InferenceProviderCapability.LipSync))
            return job == TalkingHead && candidate.Kind == nameof(InferenceProviderKind.FalAi)
                && FalVideoCatalog.Instance.Find(candidate.ModelCode) is { Job: FalVideoJob.TalkingHead };
        if (candidate.Capability != nameof(InferenceProviderCapability.VideoGeneration))
            return true;
        if (job == TalkingHead)
            return false;

        FalVideoEndpoint? fal = candidate.Kind == nameof(InferenceProviderKind.FalAi) ? FalVideoCatalog.Instance.Find(candidate.ModelCode) : null;
        if (fal is null)
            return true;
        return job switch
        {
            // An avatar or an object needs a model that takes pictures: its own reference route, or an image-to-video
            // route (one composed first frame). A text-only route is refused by the executor with its sibling's name.
            Reference => fal.Job is FalVideoJob.ReferenceToVideo or FalVideoJob.ImageToVideo,
            ImageToVideo => fal.Job == FalVideoJob.ImageToVideo || fal.FirstFrameField is not null,
            _ => fal.Job == FalVideoJob.TextToVideo || (fal.Job == FalVideoJob.ImageToVideo && !fal.FirstFrameRequired)
        };
    }

    /// <summary>
    /// Why <paramref name="candidateId"/> cannot be used by <paramref name="input"/> for a step of <paramref name="capability"/>
    /// doing <paramref name="job"/>, or null when it can (and <paramref name="found"/> is the candidate).
    /// </summary>
    public static string? Refusal(
        AppManifestInput input, Guid candidateId, string capability, string? job, IReadOnlyList<AppModelCandidate> candidates,
        out AppModelCandidate? found) =>
        RefusalCode(input, candidateId, capability, job, candidates, out found) is { } code ? RefusalSentence(code) : null;

    /// <summary>Stable reason codes of <see cref="RefusalCode"/>; the run page maps each to its own catalogue text.</summary>
    public const string NotInWorkspace = "notInWorkspace";

    /// <inheritdoc cref="NotInWorkspace"/>
    public const string WrongKind = "wrongKind";

    /// <inheritdoc cref="NotInWorkspace"/>
    public const string NotAllowed = "notAllowed";

    /// <inheritdoc cref="NotInWorkspace"/>
    public const string WrongJob = "wrongJob";

    /// <summary>The same rules as <see cref="Refusal"/>, answered as a stable code (null when the model can be used).</summary>
    public static string? RefusalCode(
        AppManifestInput input, Guid candidateId, string capability, string? job, IReadOnlyList<AppModelCandidate> candidates,
        out AppModelCandidate? found)
    {
        found = candidates.FirstOrDefault(c => c.Id == candidateId);
        if (found is null)
            return NotInWorkspace;
        // A manifest saved before the LipSync capability names VideoGeneration for its talking-head step: read it as LipSync.
        if (found.Capability != capability
            || (input.Capability is { } wanted && wanted != capability
                && !(wanted == nameof(InferenceProviderCapability.VideoGeneration) && capability == nameof(InferenceProviderCapability.LipSync))))
            return WrongKind;
        if (input.Allowed is { Count: > 0 } allowed && !allowed.Contains(candidateId))
            return NotAllowed;
        if (!ServesJob(found, job))
            return WrongJob;
        return null;
    }

    /// <summary>The <see cref="RefusalCode"/>-style code of a model the organization's plan does not allow (<see cref="PlanFeature"/>).</summary>
    public const string NotInPlan = "notInPlan";

    /// <summary>
    /// The plan feature (<see cref="PlanGate"/> keys) a <c>model</c> input of <paramref name="capability"/> doing <paramref name="job"/>
    /// needs and <paramref name="plan"/> does not include, or null when the plan allows it (docs/design/app-inputs.md 28). The same
    /// for every model of the input, so the options list marks them all and the run gate refuses the value: a video model needs
    /// <c>video_generation</c>, an avatar's clip or still also <c>avatars</c>, a music or sound-effect model
    /// <c>audio_generation</c>; a clip whose short side (<paramref name="clipShortSide"/>, from the step's own resolution) is above
    /// the plan's <c>maxOutputHeight</c> needs <c>resolution</c>. Every other capability is free of plan features.
    /// </summary>
    public static string? PlanFeature(string capability, string? job, OrganizationEntitlementSet plan, int? clipShortSide = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        bool avatar = job is TalkingHead or Reference or AvatarStill;
        switch (capability)
        {
            case nameof(InferenceProviderCapability.VideoGeneration):
            case nameof(InferenceProviderCapability.LipSync):
            case nameof(InferenceProviderCapability.ImageEdit) when job == AvatarStill:
                if (!plan.VideoGenAllowed)
                    return PlanGate.VideoGenerationFeature;
                if (avatar && !plan.AvatarsAllowed)
                    return PlanGate.AvatarsFeature;
                return capability is nameof(InferenceProviderCapability.VideoGeneration) or nameof(InferenceProviderCapability.LipSync)
                    && clipShortSide is { } side && side > plan.MaxOutputHeight
                        ? PlanGate.ResolutionFeature
                        : null;
            case nameof(InferenceProviderCapability.MusicGeneration):
            case nameof(InferenceProviderCapability.SoundEffectGeneration):
                return plan.AudioGenAllowed ? null : PlanGate.AudioGenerationFeature;
            default:
                return null;
        }
    }

    /// <summary>The short side of a VideoGenerate step's clip (<c>"768P"</c> is 768), or null when the step names none it can read.</summary>
    public static int? ClipShortSide(AppBindingStep step)
    {
        if (step.StepType != StepType.VideoGenerate)
            return null;
        string? resolution = Read<VideoGenerateStepConfig>(step.ConfigJson, VideoGenerateStepConfig.JsonOptions)?.Resolution?.Trim();
        if (string.IsNullOrEmpty(resolution))
            return null;
        int end = 0;
        while (end < resolution.Length && char.IsAsciiDigit(resolution[end]))
            end++;
        return end > 0 && int.TryParse(resolution[..end], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int side)
            ? side
            : null;
    }

    /// <summary>The plain English sentence of a plan <paramref name="feature"/> a model needs.</summary>
    public static string PlanSentence(string feature) => feature switch
    {
        PlanGate.VideoGenerationFeature => "Your plan does not include video generation",
        PlanGate.AudioGenerationFeature => "Your plan does not include music and sound effects",
        PlanGate.AvatarsFeature => "Your plan does not include avatars",
        PlanGate.ResolutionFeature => "Your plan does not render at this size",
        _ => "Your plan does not include this"
    };

    /// <summary>The plain English sentence of a <see cref="RefusalCode"/> code.</summary>
    public static string RefusalSentence(string code) => code switch
    {
        NotInWorkspace => "Not available to your workspace",
        WrongKind => "Not this kind of model",
        NotAllowed => "Not offered by this app",
        WrongJob => "Cannot make this kind of clip",
        _ => "Cannot be used here"
    };

    /// <summary>
    /// The resolver the binder calls for each <c>@model</c> binding: the chosen capability id becomes the step's
    /// <c>providerId</c> and the candidate's model code its <c>model</c>, or null (refused) when the rules above say no.
    /// </summary>
    public static AppModelResolver Resolver(AppManifest manifest, IReadOnlyList<AppModelCandidate> candidates) =>
        (inputId, capabilityId, step) =>
        {
            AppManifestInput? input = InputOf(manifest, inputId);
            // A picture-edit input on a VideoGenerate step can only be its still model (the validator allows nothing else).
            // A picture-model (Vision) input on a VideoAnalyze step is that step's vision model.
            string? path = input?.Capability == nameof(InferenceProviderCapability.ImageEdit) ? AppBindableCatalog.StillModelKey
                : input?.Capability == nameof(InferenceProviderCapability.Vision) ? AppBindableCatalog.VisionModelKey
                : AppBindableCatalog.ModelKey;
            if (input is null || CapabilityFor(step, path) is not { } capability)
                return null;
            return Refusal(input, capabilityId, capability, JobFor(step, path), candidates, out AppModelCandidate? found) is null
                ? new AppModelTarget(found!.Id, found.ModelCode)
                : null;
        };

    /// <summary>
    /// The Chat model each agent named by an agent <c>model</c> input runs with in this run (8.4): agent definition id to the
    /// chosen capability id, for every such input that has a value. <paramref name="refusedInputId"/> is the first input
    /// whose value cannot be used (then the map is incomplete and the run must be refused).
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> AgentModels(
        AppManifest manifest,
        IReadOnlyDictionary<string, JsonElement> values,
        IReadOnlyList<AppModelCandidate> candidates,
        out string? refusedInputId)
    {
        refusedInputId = null;
        Dictionary<Guid, Guid> chosen = [];
        foreach (AppManifestInput input in AgentInputs(manifest))
        {
            if (!values.TryGetValue(input.Id!, out JsonElement value))
                continue;
            if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out Guid capabilityId)
                || Refusal(input, capabilityId, nameof(InferenceProviderCapability.Chat), null, candidates, out _) is not null)
            {
                refusedInputId ??= input.Id;
                continue;
            }

            chosen[input.Agent!.Value] = capabilityId;
        }

        return chosen;
    }

    /// <summary>The manifest's agent <c>model</c> inputs (kind <c>input</c>, type <c>model</c>, an <c>agent</c>).</summary>
    public static IEnumerable<AppManifestInput> AgentInputs(AppManifest manifest) =>
        (manifest.Inputs ?? []).Where(i => i.Kind == AppManifestVocabulary.KindInput && i.Id is not null && i.Agent is not null
            && AppInputTypes.TryParse(i.Type, out AppInputType type) && type == AppInputType.Model);

    /// <summary>
    /// The model capabilities the run's STEP <c>model</c> inputs (no <c>agent</c>) chose in <paramref name="values"/>
    /// (docs/design/app-inputs.md 28): what a bound step's <c>providerId</c> is when it came from the app, so the holds price that
    /// model (a Voiceover's speech). Null when none has a value. The values are the run's canonical ones, already checked.
    /// </summary>
    public static IReadOnlySet<Guid>? StepModelChoices(AppManifest? manifest, IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (manifest is null || values is not { Count: > 0 })
            return null;
        HashSet<Guid> chosen = [];
        foreach (AppManifestInput input in manifest.Inputs ?? [])
        {
            if (input.Kind != AppManifestVocabulary.KindInput || input.Id is null || input.Agent is not null
                || !AppInputTypes.TryParse(input.Type, out AppInputType type) || type != AppInputType.Model)
                continue;
            if (values.TryGetValue(input.Id, out JsonElement value) && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out Guid capabilityId))
                chosen.Add(capabilityId);
        }

        return chosen.Count > 0 ? chosen : null;
    }

    /// <summary>True when the manifest has an agent <c>model</c> input with a value in <paramref name="values"/>.</summary>
    public static bool HasAgentModelValue(AppManifest? manifest, IReadOnlyDictionary<string, JsonElement>? values) =>
        manifest is not null && values is { Count: > 0 } && AgentInputs(manifest).Any(i => values.ContainsKey(i.Id!));

    private static AppManifestInput? InputOf(AppManifest manifest, string inputId) =>
        (manifest.Inputs ?? []).FirstOrDefault(i => i.Kind == AppManifestVocabulary.KindInput && i.Id == inputId);

    private static T? Read<T>(string? json, JsonSerializerOptions options) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
