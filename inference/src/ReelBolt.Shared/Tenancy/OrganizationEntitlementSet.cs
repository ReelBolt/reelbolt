namespace ReelBolt.Shared.Tenancy;

/// <summary>
/// The entitlements of one organization at one moment. Immutable. Numeric limits use
/// <see cref="UnlimitedInt"/> / <see cref="UnlimitedCredits"/> for "no limit" so comparisons need no
/// null handling; a limit of <c>0</c> means "not entitled".
/// </summary>
public sealed record OrganizationEntitlementSet
{
    /// <summary>"No limit" for integer limits.</summary>
    public const int UnlimitedInt = int.MaxValue;

    /// <summary>"No limit" for credit limits.</summary>
    public const decimal UnlimitedCredits = decimal.MaxValue;

    /// <summary>Plan key (catalog id). <c>selfhost</c> when billing is disabled.</summary>
    public string Tier { get; init; } = "selfhost";

    // Tenancy / providers

    /// <summary>May register org-owned (BYO) inference providers. Trial: false.</summary>
    public bool ByoAllowed { get; init; } = true;

    /// <summary>Executions the org may run concurrently (fair-share admission, A12).</summary>
    public int MaxConcurrentExecutions { get; init; } = UnlimitedInt;

    // Billing entitlements (B8a)

    /// <summary>Tallest output the encoder may produce, in pixels of height (720 on Trial).</summary>
    public int MaxOutputHeight { get; init; } = UnlimitedInt;

    /// <summary>Burn the ReelBolt watermark into outputs (Trial).</summary>
    public bool Watermark { get; init; }

    /// <summary>May use the multi-agent room step types (EditRoom, GraphicsRoom, ColorGradeRoom).</summary>
    public bool RoomsAllowed { get; init; } = true;

    /// <summary>May use <c>VideoGenerate</c> at all (independent of any spend limit: no plan caps video spend per day; the user's own limit, the step's <c>MaxSpendCredits</c> and the operator ceiling do).</summary>
    public bool VideoGenAllowed { get; init; } = true;

    /// <summary>
    /// May run TEXT-PROMPTED VIDEO SEGMENTATION — <c>VideoAnalyze</c> targets with
    /// <c>ObjectTrackMethod.Segmentation</c>, and therefore <c>VideoCompile</c>'s <c>RegionEffects</c>
    /// (an effect with no mask has nothing to apply through). Refused by
    /// <see cref="PlanGate.CheckSegmentation"/> as <c>PLAN_NOT_ENTITLED</c> feature
    /// <c>segmentation</c>, before any provider call or credit hold. Defaults to <c>true</c> for the
    /// same reason every other entitlement defaults the way it does: an omitted key means "allowed", so
    /// a catalog written before this flag existed keeps granting it (self-host and Enterprise are the
    /// shipped cases). The shipped per-plan values — Trial <c>false</c>, every paid plan <c>true</c> —
    /// are the plan §6 recommendation and are the maintainers' call to overturn in
    /// <c>plans.catalog.json</c> without a code change.
    /// </summary>
    public bool SegmentationAllowed { get; init; } = true;

    /// May use <c>AudioGenerate</c> (music and sound effects; the product decision), independent of video generation:
    /// <c>audioGenAllowed</c> in the plan catalog, off on Trial. There is deliberately no daily cap: only credits and
    /// the step's own <c>MaxSpendCredits</c> limit it,.
    /// </summary>
    public bool AudioGenAllowed { get; init; } = true;

    /// <summary>May create API keys for the public API (<c>apiAccess</c> in the plan catalog; off on Trial, on when billing is off).</summary>
    public bool ApiAccess { get; init; } = true;

    /// <summary>
    /// May use the HOSTED targets of <c>AudioCleanup</c> (AI voice isolation and music removal): <c>audioCleanupAllowed</c>
    /// in the plan catalog, off on Trial. The local, free targets (hum, wind, hiss, echo, basic voice) never need it.
    /// Refused by <see cref="PlanGate.CheckAudioCleanup"/> as <c>PLAN_NOT_ENTITLED</c> feature <c>audio_cleanup</c>.
    /// An omitted key means "allowed", like every other entitlement.
    /// </summary>
    public bool AudioCleanupAllowed { get; init; } = true;

    /// <summary>
    /// May use the HOSTED operations of <c>VideoEnhance</c> (AI upscaling and AI frame interpolation):
    /// <c>videoEnhanceAllowed</c> in the plan catalog, off on Trial. The local, free operations (denoise, motion blur,
    /// local upscale and local slow motion) never need it. Refused by <see cref="PlanGate.CheckVideoEnhance"/> as
    /// <c>PLAN_NOT_ENTITLED</c> feature <c>video_enhance</c>. An omitted key means "allowed", like every other entitlement.
    /// </summary>
    public bool VideoEnhanceAllowed { get; init; } = true;

    /// <summary>
    /// May create avatars (a consented likeness, docs/design/avatars.md): <c>avatarsAllowed</c> in the plan catalog,
    /// off on Trial (AV-2). Refused by <see cref="PlanGate.CheckAvatars"/> as <c>PLAN_NOT_ENTITLED</c> feature
    /// <c>avatars</c>. An omitted key means "allowed", like every other entitlement.
    /// </summary>
    public bool AvatarsAllowed { get; init; } = true;

    /// <summary>
    /// May create OBJECT avatars (a product, a vehicle, a place: docs/design/avatar-objects.md, decision AO-4):
    /// <c>avatarObjectsAllowed</c> in the plan catalog, off on Trial, on for Creator and above. Independent of
    /// <see cref="AvatarsAllowed"/> (people). Refused by <see cref="PlanGate.CheckAvatarObjects"/> as
    /// <c>PLAN_NOT_ENTITLED</c> feature <c>avatar_objects</c>. An omitted key means "allowed", like every other entitlement.
    /// </summary>
    public bool AvatarObjectsAllowed { get; init; } = true;

    /// <summary>
    /// May connect social accounts' posts and publish or schedule finished videos to them (the social-publishing
    /// initiative): <c>socialPublishingAllowed</c> in the plan catalog, off on Trial, on for Creator and above.
    /// Refused by <see cref="PlanGate.CheckSocialPublishing"/> as <c>PLAN_NOT_ENTITLED</c> feature <c>social_publishing</c>,
    /// again when a scheduled post fires. An omitted key means "allowed", like every other entitlement.
    /// </summary>
    public bool SocialPublishingAllowed { get; init; } = true;

    /// <summary>Queue priority class: higher is admitted first. 0 = standard.</summary>
    public int Priority { get; init; }

    /// <summary>Storage quota in gigabytes.</summary>
    public int StorageGb { get; init; } = UnlimitedInt;

    /// <summary>
    /// Days generated storage (output files, step artifacts) is kept before the per-plan
    /// retention job deletes it (F10); the D19 default is <c>30</c>. "Keep forever" is
    /// expressible as <see cref="UnlimitedInt"/>. Unlike the other numeric limits, a value of
    /// <c>0</c> here is not "not entitled" but "delete immediately", which the retention job
    /// guards against; self-host leaves the job disabled regardless of this value.
    /// <para>
    /// The same window bounds the engine-owned step cache: <c>StepCacheRetentionSweeper</c> retires
    /// a <c>workflow_step_cache_entries</c> row once that row's own age exceeds it, because the
    /// table is not reachable from the Inference API's sweep.
    /// </para>
    /// </summary>
    public int RetentionDays { get; init; } = 30;

    // Compute (D6)

    /// <summary>May dispatch jobs to a paired desktop runner. The org's choice is <see cref="ComputePreference"/>.</summary>
    public bool RunnerAllowed { get; init; } = true;

    // Assistant allowance (decision 9)

    /// <summary>
    /// Monthly credits of assistant chat and MCP use included in the plan before they are charged
    /// (<c>assistantMonthlyCredits</c>). Null (key omitted) = nothing included (0) while billing is on, unless
    /// <see cref="AssistantMonthlyCreditsPerSeat"/> is set or <see cref="AssistantUnlimited"/> is true: a missing
    /// value never reads as unlimited.
    /// </summary>
    public int? AssistantMonthlyCredits { get; init; }

    /// <summary>
    /// The plan includes unlimited assistant chat and MCP use (<c>assistantUnlimited</c>; Enterprise). The ONLY source
    /// of an unlimited allowance while billing is on; false by default, including for <see cref="Unlimited"/> (the
    /// self-host/config default), so a missing plan row or entitlement can never grant it.
    /// </summary>
    public bool AssistantUnlimited { get; init; }

    /// <summary>
    /// Per-seat variant of <see cref="AssistantMonthlyCredits"/> (<c>assistantMonthlyCreditsPerSeat</c>),
    /// multiplied by the subscription's seats; wins over the flat value when set. Null = not per seat.
    /// </summary>
    public int? AssistantMonthlyCreditsPerSeat { get; init; }

    /// <summary>
    /// The purchasable plans, lowest first, the set was read with: what lets a refusal name the plan that unlocks its feature
    /// (<see cref="Billing.PlanUnlocks"/>). Display only, never part of a plan's own JSON; null when the reader has no
    /// catalogue (billing off, tests).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<Billing.UpgradePlanOption>? UpgradePlans { get; init; }

    /// <summary>
    /// Everything allowed, nothing limited: self-host behaviour and the config default. The assistant allowance is the
    /// exception: <see cref="AssistantUnlimited"/> stays false, so this set read while billing is on includes 0 (billing
    /// off never reads the allowance at all).
    /// </summary>
    public static OrganizationEntitlementSet Unlimited { get; } = new();
}
