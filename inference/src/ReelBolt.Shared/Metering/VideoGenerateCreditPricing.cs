using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// B6c: what a <see cref="StepType.VideoGenerate"/> step is configured to BUY, in the two units the
/// credit ledger charges it in — how many clips, and how many seconds each. Priced into
/// <see cref="HoldMillis"/> by <see cref="VideoGenerateCreditPricing"/>.
/// </summary>
/// <param name="Clips">The clips the step will order: <c>Takes</c> inline, <c>MaxClips</c> from a plan.</param>
/// <param name="SecondsPerClip">The seconds each of those clips is expected to bill for.</param>
/// <param name="HoldMillis">
/// The millicredits to hold for the whole step, priced through the price row the settlement will use.
/// <c>0</c> means "not priced yet" (see <see cref="VideoGenerateCreditPricing.TryShape"/>).
/// </param>
public readonly record struct VideoGenerateCreditHold(int Clips, int SecondsPerClip, long HoldMillis)
{
    /// <summary>The step's total billable seconds: <see cref="Clips"/> x <see cref="SecondsPerClip"/>.</summary>
    public int Seconds => Clips * SecondsPerClip;
}

/// <summary>
/// B6c: the ONE place a <see cref="StepType.VideoGenerate"/> step's credit hold is sized.
///
/// <para>
/// <b>Why the whole step moved onto credits.</b> The step has always written its measured usage as
/// <see cref="UsageKind.VideoGenSeconds"/> rows (B3c) with non-zero <c>credits_millis</c>, but until
/// B6c the step's credit hold was <c>0</c> — <see cref="StepCreditDefaults"/> answered
/// <see cref="StepCreditDefaults.NoHold"/> for it — and a zero hold short-circuits
/// <c>CreditGate.ReserveAsync</c> into taking NO reservation. A step with no reservation is never
/// settled, so those credit rows were recorded and never charged: a live revenue leak. B7
/// deliberately did not paper over it by holding at estimate time, because the step already charges
/// its USD spend through <c>external_generation_jobs</c> and a credit hold without a matching
/// settlement would double-charge the same take.
/// </para>
///
/// <para>
/// <b>Hold and settle are the same arithmetic, by construction.</b> The hold is
/// <c>expected seconds x the per-second price of this model at the configured resolution</c> — the SAME
/// <see cref="IModelPriceService"/> lookup, for the same <see cref="UsageKind.VideoGenSeconds"/> unit, the same
/// resolved capability and the same resolution variant, that <c>VideoGenerateStepExecutor.RecordVideoGenUsageAsync</c>
/// feeds at settlement. The hold is therefore the measured charge scaled to the number of takes that
/// have not been ordered yet, so a completed step settles to (essentially) its own hold and a reuse
/// or a failure releases the difference.
/// </para>
///
/// <para>
/// <b>Never throws, never invents a price.</b> Every failure — no config, no provider, a model the
/// factory does not know, no price row, a database that is unreachable — answers <c>null</c>, and
/// the caller falls back to <see cref="StepCreditDefaults"/>. That is deliberately the opposite of
/// <see cref="VideoGenerationPricing"/>'s fail-closed USD pricing: the USD estimate drives a
/// spend CAP, where an unknown price must refuse, while this sizes a RESERVATION, where an unknown
/// price must not refuse work the executor could still pay for — the gate stays the enforcement
/// point and fails closed on its own.
/// </para>
/// </summary>
public static class VideoGenerateCreditPricing
{
    /// <summary>
    /// The duration word the plan-sourced estimate is priced at. <c>Short</c>/<c>Medium</c>/<c>Long</c>
    /// is the whole vocabulary a <c>ShotDirector</c> plan's shots use, and <c>Medium</c> is its
    /// midpoint (<see cref="ShotPlanParameterMapper.SnapDurationSeconds"/>), so the hold sits between
    /// a plan of all-short and a plan of all-long shots rather than at either extreme.
    /// </summary>
    public const string PlanDurationWord = "Medium";

    /// <summary>
    /// What <paramref name="configJson"/> is configured to buy, or <c>null</c> when it cannot be
    /// priced structurally (unreadable JSON, a missing/invalid field, or a plan whose per-clip
    /// duration needs a model description this caller did not supply).
    /// </summary>
    public static VideoGenerateCreditHold? TryShape(string? configJson, VideoGenerationCapabilities? capabilities)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        VideoGenerateStepConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(configJson, VideoGenerateStepConfig.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        return config is null ? null : TryShape(config, capabilities);
    }

    /// <summary>The same shape, from an already-deserialised config.</summary>
    public static VideoGenerateCreditHold? TryShape(
        VideoGenerateStepConfig? config, VideoGenerationCapabilities? capabilities)
    {
        if (config is null)
        {
            return null;
        }

        int clips;
        int secondsPerClip;

        if (config.PlanSource == VideoGeneratePlanSource.Inline)
        {
            // Exact: the inline path builds every one of its clips from the step's own
            // DurationSeconds, so Takes x DurationSeconds IS the step's order.
            clips = config.Takes;
            secondsPerClip = config.DurationSeconds;
        }
        else
        {
            // A plan-sourced step's shots carry their own duration word, which does not exist until
            // the planner step has run. MaxClips is the hard ceiling the executor itself enforces
            // across the whole step (whatever the plan says, no more than MaxClips clips reach a
            // ledger row), and the pinned midpoint of the resolved model's range is the per-clip
            // duration estimate. A plan of all-Long shots settles above the hold — allowed: the
            // reservation carries a 20%/100-credit overdraft headroom and the settle charges the
            // measured seconds regardless — and a plan of all-Short shots releases the difference.
            clips = config.MaxClips;
            if (capabilities is null)
            {
                return null;
            }

            try
            {
                secondsPerClip = ShotPlanParameterMapper.SnapDurationSeconds(capabilities, PlanDurationWord);
            }
            catch (ArgumentException)
            {
                // An inverted or sub-second duration range: this model cannot produce a billable
                // clip, so there is no honest figure to hold.
                return null;
            }
        }

        if (clips < 1 || secondsPerClip < 1)
        {
            return null;
        }

        return new VideoGenerateCreditHold(clips, secondsPerClip, 0);
    }

    /// <summary>
    /// The highest input-material charge (USD) one take of <paramref name="model"/> can carry inside the avatar limits
    /// (<see cref="ProjectAvatarLimits"/>), from the client's own declaration; <c>0</c> for a model with no reference
    /// mode (such a step is refused before anything is reserved). Throws <see cref="NotSupportedException"/> for an
    /// unpriced model or resolution, which the caller treats as "cannot size this hold".
    /// </summary>
    /// <param name="subjects">
    /// How many subjects the take may show (docs/design/avatar-objects.md): each brings up to
    /// <see cref="ProjectAvatarLimits.MaxImages"/> pictures, and together they fill the model's own picture count at
    /// most. One (the default) is the figure a single avatar has always been held at.
    /// </param>
    public static decimal MaxInputMaterialUsd(IVideoGenerationClient client, string model, string resolution, int subjects = 1)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client is not IVideoReferenceInputSupport declared)
        {
            return 0m;
        }

        VideoReferenceInputSupport support = declared.ReferenceInputSupport(model);
        if (!support.AcceptsAvatar)
        {
            return 0m;
        }

        List<VideoReferenceMaterial> worst =
        [
            .. Enumerable.Repeat(new VideoReferenceMaterial(AvatarReferenceKind.Image, null),
                Math.Min(ProjectAvatarLimits.MaxImages * Math.Clamp(subjects, 1, VideoGenerateStepConfig.MaxSubjects), support.MaxImages)),
        ];
        if (support.MaxVideos > 0 && support.MaxVideoTotalMs > 0)
        {
            worst.Add(new VideoReferenceMaterial(AvatarReferenceKind.Video, support.MaxVideoTotalMs));
        }

        return declared.InputMaterialCostUsd(model, resolution, worst);
    }

    /// <summary>
    /// Avatars phase 3: the USD held for the ONE still a talking-head step makes from the avatar's pictures before its
    /// takes (made once per step, reused across takes and runs while the avatar is unchanged). The still's model
    /// (<c>google/nano-banana-2.1/edit</c> by default) is token-priced; its page's 1K figure for two input pictures is
    /// $0.044 and the step sends up to three, so the hold takes the 2K card ($0.063) as the conservative figure.
    /// Settlement charges what the image client metered (a reused still meters nothing).
    /// </summary>
    public const decimal AvatarStillHoldUsd = 0.063m;

    /// <summary>
    /// The hold for a talking head's still. With no <c>stillProviderId</c> it is today's figure (the fixed
    /// <see cref="AvatarStillHoldUsd"/> at the video provider's billing mode). With one (an app's "Picture model",
    /// docs/design/app-inputs.md 27) the still is held at the chosen model's own billing mode (a bring-your-own model holds
    /// nothing) and at the larger of its catalogued per-image price and the fixed figure: a hold only ever grows, and
    /// settlement charges what the image client metered.
    /// </summary>
    private static async ValueTask<long> StillHoldMillisAsync(
        VideoGenerateStepConfig config, IInferenceProviderResolver providers, IModelPriceService prices, UsageBillingMode videoMode, CancellationToken ct)
    {
        UsageBillingMode mode = videoMode;
        long? chosen = null;
        if (config.StillProviderId is { } stillId
            && await providers.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, stillId, ct).ConfigureAwait(false) is { } still)
        {
            mode = MeteringChatClient.BillingModeOf(still.OrganizationId);
            try
            {
                // The model and price tier the still call is metered under (docs/design/app-inputs.md 28): a fal.ai row that
                // names no model runs the catalogue's default instruction edit, and a tiered endpoint (nano-banana's
                // `resolution`) is billed at the tier it is sent, so the hold reads that tier's card.
                (string model, string? variant) = StillBillingKey(still.Kind, still.ModelName);
                chosen = (await prices.PriceAsync(new PriceRequest(
                    still.ProviderId, still.AccountId, still.Kind, InferenceProviderCapability.ImageEdit, model,
                    UsageKind.ImageEditImages, variant, mode, 1m), ct).ConfigureAwait(false)).CreditsMillis;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                chosen = null; // token-priced or unpriced: the fixed figure below holds it
            }
        }

        long fixedHold = (await prices.PriceQuotedUsdAsync(AvatarStillHoldUsd, UsageKind.ImageEditImages, mode, ct: ct).ConfigureAwait(false)).CreditsMillis;
        return chosen is { } c ? Math.Max(c, fixedHold) : fixedHold;
    }

    /// <summary>
    /// The model and price variant a talking head's still is metered under on <paramref name="kind"/> (docs/design/app-inputs.md 28):
    /// for fal.ai, the named instruction-edit endpoint (the catalogue's default one when no model is named) and, when the endpoint
    /// bills by a tier, the tier the call sends (the still maker sends none, so the vendor default; the meter's own rule,
    /// <see cref="FalAiImageClient.BillingModelKey"/>). Any other kind: the model as named, no variant.
    /// </summary>
    public static (string Model, string? Variant) StillBillingKey(InferenceProviderKind kind, string? modelName)
    {
        if (kind != InferenceProviderKind.FalAi)
            return (modelName ?? string.Empty, null);
        FalImageEndpoint? endpoint = string.IsNullOrWhiteSpace(modelName)
            ? FalImageCatalog.Instance.DefaultFor(FalImageJob.Instruct)
            : FalImageCatalog.Instance.Find(modelName.Trim());
        if (endpoint is null)
            return (modelName ?? string.Empty, null);
        string? tier = endpoint.BillingTierField is { } field ? endpoint.FieldNamed(field)?.VendorDefault : null;
        return (endpoint.Id, string.IsNullOrEmpty(tier) ? null : tier);
    }

    /// <summary>
    /// True when <paramref name="model"/> shows an avatar by animating a still (phase 3): such a step's hold also covers
    /// the still. False for an unknown model or a client that declares nothing.
    /// </summary>
    public static bool AnimatesStill(IVideoGenerationClient client, string model)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            return client.DeclaredReferenceInputSupport(model).AnimatesStill;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a plan-sourced step's planner may be offered the project's subjects: <c>PlannedSubjects = Auto</c>, the
    /// step's own permission to send media is on, and the project is not known to hold no avatar
    /// (<see cref="ProjectSubjectsPricing"/>). With any of them false nothing is offered and nothing more is held.
    /// </summary>
    public static bool PlannedSubjectsMayApply(VideoGenerateStepConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.PlanSource != VideoGeneratePlanSource.Inline
            && config.PlannedSubjects == VideoPlannedSubjects.Auto
            && config.AllowSourceMediaEgress
            && ProjectSubjectsPricing.Current != false;
    }

    /// <summary>
    /// The path the step's model would carry subjects on (<see cref="VideoSubjectPaths"/>), or null when it can carry
    /// none or is unknown: the hold then adds nothing, and the step itself is refused before anything is reserved.
    /// </summary>
    private static VideoSubjectPath? SubjectPath(IVideoGenerationClient client, InferenceProviderKind kind, VideoGenerateStepConfig config)
    {
        try
        {
            return VideoSubjectPaths.Decide(client, kind, config.Model, config.SubjectMode, config.HasAvatarAudio).Path;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The millicredits to hold for a VideoGenerate step whose config is
    /// <paramref name="configJson"/>, or <c>null</c> when it cannot be priced (the caller falls back
    /// to <see cref="StepCreditDefaults"/>). A BYO provider prices to <c>0</c> — the same answer the
    /// settlement gives it, and a zero hold takes no reservation at all, which is D8's BYO rule.
    /// </summary>
    public static async ValueTask<long?> TryHoldMillisAsync(
        string? configJson,
        IInferenceProviderResolver providers,
        IVideoGenerationClientFactory clients,
        IModelPriceService prices,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(prices);

        try
        {
            VideoGenerateStepConfig? config;
            try
            {
                config = string.IsNullOrWhiteSpace(configJson)
                    ? null
                    : JsonSerializer.Deserialize<VideoGenerateStepConfig>(configJson, VideoGenerateStepConfig.JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }

            if (config is null)
            {
                return null;
            }

            // The run brief's avatar fills a plain inline step that names none, as the executor does before it runs the
            // step, so the hold prices the still and the avatar input material the run will really order.
            if (BriefAvatarPricing.Current is Guid briefAvatar
                && !config.HasAvatar && !config.HasFrameInputs && config.PlanSource == VideoGeneratePlanSource.Inline)
            {
                config.AvatarId = briefAvatar;
            }

            ResolvedVideoGenerationProvider? provider =
                await LipSyncStepResolution.ResolveAsync(providers, config, ct).ConfigureAwait(false);
            if (provider is null)
            {
                return null;
            }

            // A talking head prices on the LipSync capability and the model it really runs (the local copy only).
            LipSyncStepResolution.Apply(config, provider);

            VideoGenerationCapabilities capabilities;
            IVideoGenerationClient client = clients.Get(provider);
            try
            {
                capabilities = client.Describe(config.Model);
            }
            catch (NotSupportedException)
            {
                return null;
            }

            VideoGenerateCreditHold? shape = TryShape(config, capabilities);
            if (shape is not { } hold)
            {
                return null;
            }

            // The model the vendor is asked for and the resolution it renders at are what the usage row is
            // stamped with (VideoGenerateStepExecutor.RecordVideoGenUsageAsync), so they are what is priced here.
            PriceQuote quote = await prices.PriceAsync(new PriceRequest(
                provider.ProviderId,
                provider.AccountId,
                provider.Kind,
                LipSyncStepResolution.CapabilityFor(config),
                string.IsNullOrWhiteSpace(config.Model) ? provider.ModelName : config.Model,
                UsageKind.VideoGenSeconds,
                string.IsNullOrWhiteSpace(config.Resolution) ? null : config.Resolution,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                hold.Seconds), ct).ConfigureAwait(false);

            // Avatars phase 2: a step that shows an avatar also pays the vendor's input material (extra pictures,
            // input video) on every take. Its references are not read here, so the hold takes the most our avatar
            // limits allow on this model (all eight pictures, the model's whole input-video allowance): a hold only
            // ever grows, and settlement charges what was actually sent.
            // Avatars phase 3: a talking-head step also makes one still of the avatar (an image edit) before its takes.
            if (config.HasAvatar && AnimatesStill(client, config.Model))
            {
                long still = await StillHoldMillisAsync(config, providers, prices, MeteringChatClient.BillingModeOf(provider.OrganizationId), ct)
                    .ConfigureAwait(false);
                return quote.CreditsMillis + still;
            }

            // Several subjects per clip (docs/design/avatar-objects.md "Subjects in a video generation step", Money):
            // what the subjects add depends on the path the model's own declarations choose. Reference mode: the input
            // material of every subject, worst case, on every take (one subject = the figure above, unchanged). One
            // composed first frame: ONE still per inline step (made once, reused by its takes). A plan-sourced step
            // whose planner may use the project's subjects holds the same per planned shot, since each shot may show
            // its own subjects in its own scene. A model that can carry none adds nothing: the step is refused first.
            UsageBillingMode mode = MeteringChatClient.BillingModeOf(provider.OrganizationId);
            bool inline = config.PlanSource == VideoGeneratePlanSource.Inline;
            bool planned = !inline && PlannedSubjectsMayApply(config);
            if ((inline && config.HasAvatar) || planned)
            {
                int subjects = inline ? config.EffectiveSubjects.Count : VideoGenerateStepConfig.MaxSubjects;
                switch (SubjectPath(client, provider.Kind, config))
                {
                    case VideoSubjectPath.References:
                        decimal inputUsd = MaxInputMaterialUsd(client, config.Model, config.Resolution, subjects) * hold.Clips;
                        if (inputUsd > 0m)
                        {
                            PriceQuote input = await prices.PriceQuotedUsdAsync(inputUsd, UsageKind.VideoGenSeconds, mode, ct: ct)
                                .ConfigureAwait(false);
                            return quote.CreditsMillis + input.CreditsMillis;
                        }

                        break;

                    case VideoSubjectPath.FirstFrame:
                        long sceneStill = await StillHoldMillisAsync(config, providers, prices, mode, ct).ConfigureAwait(false);
                        return quote.CreditsMillis + (sceneStill * (inline ? 1 : hold.Clips));
                }
            }

            return quote.CreditsMillis;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // UnpricedUsageException, a provider row that will not decrypt, a connection that is
            // down: all of them are "we cannot size this hold", never "this step is free".
            return null;
        }
    }
}

/// <summary>
/// Whether the project a run belongs to holds any avatar (a person or an object), for the code that prices a
/// plan-sourced <c>VideoGenerate</c> step before it runs: only then can the shot planner be offered subjects, so only
/// then does the hold cover them (docs/design/avatar-objects.md "Subjects in a video generation step", Money). Ambient
/// like <see cref="BriefAvatarPricing"/>, so no estimator signature changes. The API and the engine enter the SAME
/// fact (any <c>project_avatars</c> row of the project), which keeps their holds equal. Not entered = unknown, and an
/// unknown project is held as if it had subjects: a hold only ever grows.
/// </summary>
public static class ProjectSubjectsPricing
{
    private static readonly AsyncLocal<bool?> Current_ = new();

    /// <summary>True/false when the caller read the project; null when nobody did (unknown).</summary>
    public static bool? Current => Current_.Value;

    /// <summary>
    /// True when the answer changes what <paramref name="videoGenerateConfigJson"/> holds: a plan-sourced step whose
    /// planner may be offered subjects (<c>PlannedSubjects = Auto</c>, the step's permission to send media on). Every
    /// other step is priced the same whatever the project holds, so a caller reads the project only when this is true.
    /// </summary>
    public static bool Matters(string? videoGenerateConfigJson)
    {
        if (string.IsNullOrWhiteSpace(videoGenerateConfigJson))
            return false;
        try
        {
            return JsonSerializer.Deserialize<VideoGenerateStepConfig>(videoGenerateConfigJson, VideoGenerateStepConfig.JsonOptions)
                is { PlannedSubjects: VideoPlannedSubjects.Auto, AllowSourceMediaEgress: true } config
                && config.PlanSource != VideoGeneratePlanSource.Inline;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>States whether the project holds an avatar, until the result is disposed.</summary>
    public static IDisposable Enter(bool? projectHasAvatars)
    {
        bool? previous = Current_.Value;
        Current_.Value = projectHasAvatars;
        return new Restore(previous);
    }

    private sealed class Restore(bool? previous) : IDisposable
    {
        public void Dispose() => Current_.Value = previous;
    }
}

/// <summary>
/// The avatar the run's brief chose, for the code that prices a run before it starts (the engine's hold reads its own
/// run brief and enters the same value). Ambient so no estimator signature changes; null = no brief avatar.
/// </summary>
public static class BriefAvatarPricing
{
    private static readonly AsyncLocal<Guid?> Current_ = new();

    /// <summary>The brief's avatar id on this async flow, or null.</summary>
    public static Guid? Current => Current_.Value;

    /// <summary>Makes <paramref name="avatarId"/> the brief avatar the hold prices until the result is disposed.</summary>
    public static IDisposable Enter(Guid? avatarId)
    {
        Guid? previous = Current_.Value;
        Current_.Value = avatarId is { } id && id != Guid.Empty ? id : null;
        return new Restore(previous);
    }

    private sealed class Restore(Guid? previous) : IDisposable
    {
        public void Dispose() => Current_.Value = previous;
    }
}
