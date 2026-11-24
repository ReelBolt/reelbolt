using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Billing;

/// <summary>
/// A refused plan gate, machine-readable for the UI: <c>{ code, feature, message, limit, current }</c>.
/// The HTTP status is carried alongside (402 not entitled, 429 concurrency, 413 storage).
/// </summary>
public sealed record PlanGateViolation(string Code, string Feature, string Message, int StatusCode, long? Limit = null, long? Current = null, long? Incoming = null, string? Plan = null, string? UnlockPlan = null, string? UnlockPlanName = null)
{
    /// <summary>
    /// The JSON body returned to clients. <c>unlockPlan</c> / <c>unlockPlanName</c> (the lowest plan in the catalogue that
    /// includes the feature) are sent only on a refusal that has one, so every other body keeps its shape.
    /// </summary>
    public object ToBody() => UnlockPlan is null
        ? (object)new { code = Code, feature = Feature, message = Message, limit = Limit, current = Current, incoming = Incoming, plan = Plan }
        : new { code = Code, feature = Feature, message = Message, limit = Limit, current = Current, incoming = Incoming, plan = Plan, unlockPlan = UnlockPlan, unlockPlanName = UnlockPlanName };

    /// <summary>One-line form for logs and step error messages.</summary>
    public override string ToString() => $"{Code}: feature={Feature}; {Message}";
}

/// <summary>Pure plan-gate rules shared by the Inference API (save/execute/upload) and the engine (runtime re-check).</summary>
public static class PlanGate
{
    /// <summary>Code: the plan does not include the feature (HTTP 402).</summary>
    public const string NotEntitledCode = "PLAN_NOT_ENTITLED";

    /// <summary>Code: the org is at its concurrent-execution cap (HTTP 429).</summary>
    public const string ConcurrencyCode = "CONCURRENCY_LIMIT_REACHED";

    /// <summary>Code: an upload would exceed the plan's storage quota (HTTP 413).</summary>
    public const string StorageCode = "STORAGE_QUOTA_EXCEEDED";

    /// <summary>Feature key for the three room step types.</summary>
    public const string RoomsFeature = "rooms";

    /// <summary>Feature key for <see cref="StepType.VideoGenerate"/>.</summary>
    public const string VideoGenerationFeature = "video_generation";

    /// <summary>Feature key for <see cref="StepType.AudioGenerate"/> (Product decision: separate from video generation).</summary>
    public const string AudioGenerationFeature = "audio_generation";

    /// <summary>Feature key for the hosted targets of <see cref="StepType.AudioCleanup"/> (config-driven, like segmentation: not a step-type gate).</summary>
    public const string AudioCleanupFeature = "audio_cleanup";

    /// <summary>Feature key for the hosted operations of <see cref="StepType.VideoEnhance"/> (config-driven: local operations are free on every plan).</summary>
    public const string VideoEnhanceFeature = "video_enhance";

    /// <summary>
    /// Feature key for creating an avatar of a person or changing its references (docs/design/avatars.md "Billing and
    /// plan gating", AV-2: Pro and up). Not a step-type gate: generating with an avatar also needs <see cref="VideoGenerationFeature"/>.
    /// </summary>
    public const string AvatarsFeature = "avatars";

    /// <summary>
    /// Feature key for creating an OBJECT avatar or changing its references, and for using one in a picture
    /// (docs/design/avatar-objects.md, decision AO-4: Creator and up). Independent of <see cref="AvatarsFeature"/>;
    /// generating a video with an object also needs <see cref="VideoGenerationFeature"/>.
    /// </summary>
    public const string AvatarObjectsFeature = "avatar_objects";

    /// <summary>Feature key for publishing or scheduling finished videos to connected social accounts (Creator and up).</summary>
    public const string SocialPublishingFeature = "social_publishing";

    /// <summary>Feature key for a run's chosen output resolution (<see cref="OutputResolution"/>).</summary>
    public const string ResolutionFeature = "resolution";

    /// <summary>
    /// Feature key for text-prompted video segmentation (<c>ObjectTrackMethod.Segmentation</c> and the
    /// compile-side <c>RegionEffects</c> that depend on it). See
    /// <see cref="CheckSegmentation"/>.
    /// </summary>
    public const string SegmentationFeature = "segmentation";

    /// <summary>Feature key for creating API keys (<see cref="OrganizationEntitlementSet.ApiAccess"/>).</summary>
    public const string ApiFeature = "api";

    /// <summary>Feature key for starting runs, uploading files and changing things through the public API (<see cref="OrganizationEntitlementSet.ApiAccess"/>).</summary>
    public const string ApiWriteFeature = "api_write";

    /// <summary>Code: the plan may read through the API but not write (HTTP 402).</summary>
    public const string ApiPlanReadOnlyCode = "API_PLAN_READ_ONLY";

    /// <summary>The plan feature a step type needs, or null when every plan may use it.</summary>
    public static string? FeatureFor(StepType stepType) => stepType switch
    {
        StepType.EditRoom or StepType.GraphicsRoom or StepType.ColorGradeRoom => RoomsFeature,
        StepType.VideoGenerate => VideoGenerationFeature,
        // music/SFX generation has its own entitlement, never the video one.
        StepType.AudioGenerate => AudioGenerationFeature,
        _ => null
    };

    /// <summary>First violation among <paramref name="stepTypes"/> under <paramref name="set"/>, else null.</summary>
    public static PlanGateViolation? CheckSteps(IEnumerable<StepType> stepTypes, OrganizationEntitlementSet set)
    {
        foreach (StepType type in stepTypes)
        {
            PlanGateViolation? v = CheckStep(type, set);
            if (v is not null) return v;
        }

        return null;
    }

    /// <summary>Violation for one step type under <paramref name="set"/>, else null.</summary>
    public static PlanGateViolation? CheckStep(StepType stepType, OrganizationEntitlementSet set)
    {
        string? feature = FeatureFor(stepType);
        bool allowed = feature switch
        {
            RoomsFeature => set.RoomsAllowed,
            VideoGenerationFeature => set.VideoGenAllowed,
            AudioGenerationFeature => set.AudioGenAllowed,
            _ => true
        };
        string what = feature switch
        {
            RoomsFeature => "multi-agent rooms",
            AudioGenerationFeature => "audio generation",
            _ => "video generation"
        };
        return allowed
            ? null
            : new PlanGateViolation(NotEntitledCode, feature!,
                $"The {set.Tier} plan does not include {what} ({stepType} steps). Upgrade your plan to use it.",
                402).Unlocking(set);
    }

    /// <summary>
    /// 402 when a run asks for an output resolution (short side, see <see cref="OutputResolution"/>)
    /// above the plan's <see cref="OrganizationEntitlementSet.MaxOutputHeight"/>. No choice (Auto) always
    /// passes: Auto renders within the plan by construction.
    /// </summary>
    public static PlanGateViolation? CheckResolution(int? chosen, OrganizationEntitlementSet set)
    {
        if (chosen is not { } choice || choice <= set.MaxOutputHeight) return null;

        int limit = set.MaxOutputHeight;
        return new PlanGateViolation(NotEntitledCode, ResolutionFeature,
            $"Your plan renders up to {OutputResolution.DisplayName(limit)}. Choose {OutputResolution.DisplayName(limit)} or lower, or upgrade your plan to render at {OutputResolution.DisplayName(choice)}.",
            402, limit, choice).Unlocking(set, choice);
    }

    /// <summary>
    /// 402 when the org's plan does not include text-prompted video segmentation
    /// (<see cref="OrganizationEntitlementSet.SegmentationAllowed"/>). Checked BEFORE any egress
    /// consent, presign, provider call or credit hold — a plan that cannot use the feature must not
    /// cause the source clip to leave the host, and must not reserve credits for work it will not do.
    /// The message names the plan and how to upgrade, like every other gate here.
    /// </summary>
    public static PlanGateViolation? CheckSegmentation(OrganizationEntitlementSet set)
    {
        if (set.SegmentationAllowed) return null;

        return new PlanGateViolation(NotEntitledCode, SegmentationFeature,
            $"The {set.Tier} plan does not include text-prompted object segmentation (an object followed through the video, so an effect can be applied to just that part of the picture). Upgrade your plan to use it.",
            402).Unlocking(set);
    }

    /// <summary>402 when the org's plan does not include the public API (creating an API key).</summary>
    public static PlanGateViolation? CheckApiAccess(OrganizationEntitlementSet set)
    {
        if (set.ApiAccess) return null;

        return new PlanGateViolation(NotEntitledCode, ApiFeature,
            $"The {set.Tier} plan does not include API access. Upgrade your plan to create API keys.",
            402).Unlocking(set);
    }

    /// <summary>
    /// 402 <see cref="ApiPlanReadOnlyCode"/> when the org's plan has no API access and the request would change something
    /// (run an app, upload, webhooks). Reads stay open on every plan.
    /// </summary>
    public static PlanGateViolation? CheckApiWrite(OrganizationEntitlementSet set)
    {
        if (set.ApiAccess) return null;

        return new PlanGateViolation(ApiPlanReadOnlyCode, ApiWriteFeature,
            "Your Trial plan can read through the API but not run apps or upload files. Upgrade to run or upload.",
            402).Unlocking(set);
    }

    /// <summary>
    /// 402 when the org's plan does not include hosted audio cleanup
    /// (<see cref="OrganizationEntitlementSet.AudioCleanupAllowed"/>). Checked BEFORE the egress consent, any upload,
    /// provider call or credit hold. Only the hosted targets ask; the local targets are free on every plan.
    /// </summary>
    public static PlanGateViolation? CheckAudioCleanup(OrganizationEntitlementSet set)
    {
        if (set.AudioCleanupAllowed) return null;

        return new PlanGateViolation(NotEntitledCode, AudioCleanupFeature,
            $"The {set.Tier} plan does not include AI audio cleanup (voice isolation and music removal). The free targets (hum, wind, hiss, echo, basic voice) still work; upgrade your plan to use the AI ones.",
            402).Unlocking(set);
    }

    /// <summary>
    /// 402 when the org's plan does not include hosted video enhancement
    /// (<see cref="OrganizationEntitlementSet.VideoEnhanceAllowed"/>). Checked BEFORE the egress consent, any upload,
    /// provider call or credit hold. Only the hosted operations ask; local operations are free on every plan.
    /// </summary>
    public static PlanGateViolation? CheckVideoEnhance(OrganizationEntitlementSet set)
    {
        if (set.VideoEnhanceAllowed) return null;

        return new PlanGateViolation(NotEntitledCode, VideoEnhanceFeature,
            $"The {set.Tier} plan does not include AI video enhancement (upscaling and smooth slow motion). The free local operations (upscale, denoise, motion blur and slow motion) still work; upgrade your plan to use the AI ones.",
            402).Unlocking(set);
    }

    /// <summary>
    /// 402 when the org's plan does not include publishing to social platforms
    /// (<see cref="OrganizationEntitlementSet.SocialPublishingAllowed"/>). Checked when a post is created and again when
    /// a scheduled post fires, before anything is uploaded.
    /// </summary>
    public static PlanGateViolation? CheckSocialPublishing(OrganizationEntitlementSet set)
    {
        if (set.SocialPublishingAllowed) return null;

        return new PlanGateViolation(NotEntitledCode, SocialPublishingFeature,
            $"The {set.Tier} plan does not include publishing to social platforms. Upgrade your plan to publish or schedule posts.",
            402).Unlocking(set);
    }

    /// <summary>
    /// 402 when the org's plan does not include avatars (<see cref="OrganizationEntitlementSet.AvatarsAllowed"/>).
    /// Checked on create and on a reference change, before any reference is read; listing, revoking and deleting an
    /// avatar stay free on every plan, so a downgraded workspace can still withdraw consent and clean up.
    /// </summary>
    public static PlanGateViolation? CheckAvatars(OrganizationEntitlementSet set)
    {
        if (set.AvatarsAllowed) return null;

        return new PlanGateViolation(NotEntitledCode, AvatarsFeature,
            $"The {set.Tier} plan does not include avatars. Upgrade your plan to create one.",
            402).Unlocking(set);
    }

    /// <summary>
    /// 402 when the org's plan does not include object avatars (<see cref="OrganizationEntitlementSet.AvatarObjectsAllowed"/>).
    /// Checked when an object is created or its references change, before any reference is read, and when one is used
    /// in a picture; listing and deleting an object stay free on every plan, so a downgraded workspace can clean up.
    /// </summary>
    public static PlanGateViolation? CheckAvatarObjects(OrganizationEntitlementSet set)
    {
        if (set.AvatarObjectsAllowed) return null;

        return new PlanGateViolation(NotEntitledCode, AvatarObjectsFeature,
            $"The {set.Tier} plan does not include object avatars. Upgrade your plan to create one.",
            402);
    }

    /// <summary>429 when <paramref name="running"/> has reached the plan's concurrent-execution cap.</summary>
    public static PlanGateViolation? CheckConcurrency(int running, OrganizationEntitlementSet set)
    {
        int cap = Math.Max(1, set.MaxConcurrentExecutions);
        return running >= cap
            ? new PlanGateViolation(ConcurrencyCode, "concurrent_executions",
                $"The {set.Tier} plan allows {cap} execution(s) at once and {running} are running. Wait for one to finish or upgrade your plan.",
                429, cap, running)
            : null;
    }

    /// <summary>The plan key as a name in a sentence: "trial" is "Trial".</summary>
    private static string PlanLabel(string tier) =>
        string.IsNullOrEmpty(tier) ? tier : char.ToUpperInvariant(tier[0]) + tier[1..];

    /// <summary>Gigabytes to one decimal, rounded up, so a file just over the plan never reads as equal to it.</summary>
    private static string OneDecimalGigabytes(long bytes) =>
        (Math.Ceiling(bytes / (1024d * 1024 * 1024) * 10) / 10).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>413 when storing <paramref name="incomingBytes"/> more would pass the plan's storage quota.</summary>
    public static PlanGateViolation? CheckStorage(long usedBytes, long incomingBytes, OrganizationEntitlementSet set)
    {
        if (set.StorageGb == OrganizationEntitlementSet.UnlimitedInt) return null;
        long quota = (long)set.StorageGb * 1024L * 1024L * 1024L;
        // A file bigger than the whole plan can never fit, however much is deleted: say that, not "delete files".
        if (incomingBytes > quota)
        {
            return new PlanGateViolation(StorageCode, "storage",
                $"This file is {OneDecimalGigabytes(incomingBytes)} GB and your {PlanLabel(set.Tier)} plan stores up to {set.StorageGb} GB. Export a smaller copy or upgrade.",
                413, quota, usedBytes, incomingBytes, set.Tier.ToString());
        }

        return usedBytes + incomingBytes > quota
            ? new PlanGateViolation(StorageCode, "storage",
                $"This upload would exceed the {set.Tier} plan's {set.StorageGb} GB storage quota. Delete files or upgrade your plan.",
                413, quota, usedBytes)
            : null;
    }
}
