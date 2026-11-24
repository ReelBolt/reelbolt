using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Plan source for video generation: <see cref="Inline"/> (a prompt typed straight into the step
/// config) or <see cref="StepRef"/> (a planner step's <c>GeneratedShotPlanOutput</c>, one generated
/// clip per planned shot).
/// </summary>
/// <remarks>
/// <see cref="StepRef"/> is APPENDED AFTER <see cref="Inline"/> on purpose: the numeric values are
/// what persisted JSON payloads and the frontend's TS mirror key on, so inserting a member would
/// silently re-point every already-stored <c>planSource: 0</c> at a different mode.
/// </remarks>
public enum VideoGeneratePlanSource
{
    /// <summary>A single <see cref="VideoGenerateStepConfig.InlinePrompt"/> generates <c>Takes</c> clips.</summary>
    Inline,

    /// <summary>
    /// A planner step's <c>GeneratedShotPlanOutput</c> (see <see cref="VideoGenerateStepConfig.Plan"/>)
    /// generates one clip per planned shot, still capped by <see cref="VideoGenerateStepConfig.MaxClips"/>.
    /// </summary>
    StepRef,

    /// <summary>
    /// An approved, saved <c>GeneratedShotPlanOutput</c> JSON project file (see
    /// <see cref="VideoGenerateStepConfig.PlanProjectFileId"/>) — a locked shot list. Generates
    /// exactly like <see cref="StepRef"/>, but the plan cannot drift between runs, so a re-run's
    /// prompts are byte-identical and every clip is served from the spend ledger instead of bought
    /// again. Appended last: the enum persists by member NAME.
    /// </summary>
    ProjectFile
}

/// <summary>
/// What a <c>VideoGenerate</c> step does with already-paid clips when it runs again inside the same
/// execution (a ReviewLoop loop-back). Appended-only: members persist by name.
/// </summary>
public enum VideoGenerateLoopBackBehavior
{
    /// <summary>Serve a take whose identical request already succeeded from its stored clip; pay nothing.</summary>
    Reuse,

    /// <summary>Generate fresh takes on every loop-back iteration (costs money each time).</summary>
    Regenerate
}

/// <summary>
/// One subject a <c>VideoGenerate</c> step shows (docs/design/avatar-objects.md "Subjects in a video generation step"):
/// a project avatar of a person or of an object, and optionally the subset of its reference roles to use.
/// </summary>
/// <param name="AvatarId">The project avatar (a person or an object) of the step's own project.</param>
/// <param name="Roles">
/// Optional subset of that avatar's reference ROLES to use. Null uses every role the model accepts, in the avatar's
/// order. Must keep at least one picture role, since a subject is shown from its pictures.
/// </param>
public sealed record VideoGenerateSubject(Guid AvatarId, List<AvatarReferenceRole>? Roles = null);

/// <summary>
/// How a step's subjects reach the model, when the model offers more than one way. Appended-only: members persist by
/// name.
/// </summary>
public enum VideoSubjectMode
{
    /// <summary>The model's own reference mode when it has one, else one composed first frame. Default.</summary>
    Auto,

    /// <summary>Only the model's reference mode (each subject's pictures as reference items); refused on a model without one.</summary>
    References,

    /// <summary>One composed still that shows every subject in the scene of the prompt, sent as the first frame.</summary>
    FirstFrame
}

/// <summary>
/// Whether a plan-sourced step lets the shot planner use the project's avatars and objects. Appended-only: members
/// persist by name.
/// </summary>
public enum VideoPlannedSubjects
{
    /// <summary>The planner may name subjects per shot, whenever the step's own permission to send media is on. Default.</summary>
    Auto,

    /// <summary>No subject is offered to the planner and none is shown.</summary>
    Off
}

/// <summary>
/// Configuration for StepType.VideoGenerate, a video generation step powered by external providers like MiniMax.
/// Defines what prompt or instruction guides the generation, model selection, output resolution, duration, aspect ratio,
/// cost bounds, and execution parameters (polling interval, timeout).
/// </summary>
[StepCard(
    "Generates new video shots from prompts through a video provider.",
    ["A video-generation provider", "A shot plan: written inline, taken from a project file, or from an earlier step",
        "Optional: up to three avatars (people who consented or objects, one person at most) and the permission to send media"],
    "The generated shots, several takes each",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCreditsAndAiShots,
    ReadableBy = ["DailiesRoom", "VideoAnalyze", "VideoCompile"],
    PairsWith = ["DailiesRoom: chooses between the takes", "VideoCompile: places the chosen take"])]
public class VideoGenerateStepConfig
{
    /// <summary>
    /// The one set of options <c>VideoGenerateConfigJson</c> is deserialized with — by the executor at
    /// run time and by the Inference API's save-time validation — so both read a config identically
    /// (Web defaults: camelCase, case-insensitive; enums accepted as strings).
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The plan source: currently only <see cref="VideoGeneratePlanSource.Inline"/> is supported.
    /// Default: <see cref="VideoGeneratePlanSource.Inline"/>
    /// </summary>
    public VideoGeneratePlanSource PlanSource { get; set; } = VideoGeneratePlanSource.Inline;

    /// <summary>
    /// The inline prompt/instructions sent to the video generation model when PlanSource is Inline.
    /// Nullable; can be empty if the agent or upstream step provides the prompt separately.
    /// </summary>
    [AppBindable(AppInputType.ShortText, AppInputType.LongText, Label = "Video prompt", MaxLength = 7000)]
    public string? InlinePrompt { get; set; }

    /// <summary>
    /// Which step's resolved <c>GeneratedShotPlanOutput</c> to generate clips from, when
    /// <see cref="PlanSource"/> is <see cref="VideoGeneratePlanSource.StepRef"/>. Required in that
    /// mode; <see cref="Validate()"/> rejects its absence before any budget work happens.
    /// </summary>
    /// <remarks>
    /// Reuses <see cref="ExtractInputRef"/> verbatim — the house convention documented on
    /// <c>VideoCompileStepConfig</c> — rather than inventing a second step-reference type, so the
    /// frontend's existing input-ref picker and TS mirror already understand it. Only
    /// <c>From = Previous</c> or <c>From = Step</c> are meaningful; the executor resolves them
    /// through <c>StepExecutionContext.StepOutputHistory</c> exactly like every other
    /// ref-consuming step type.
    /// </remarks>
    public ExtractInputRef? Plan { get; set; }

    /// <summary>
    /// The project file holding the approved <c>GeneratedShotPlanOutput</c> JSON, when
    /// <see cref="PlanSource"/> is <see cref="VideoGeneratePlanSource.ProjectFile"/>. Required in
    /// that mode.
    /// </summary>
    public Guid? PlanProjectFileId { get; set; }

    /// <summary>
    /// Which step's <c>VideoAnalyze</c> artifact to resolve a planned shot's <c>firstFrame</c>/
    /// <c>lastFrame</c> ids against. Required whenever ANY planned shot names either, and never
    /// consulted when none does.
    /// </summary>
    /// <remarks>
    /// Only the analysis step's <c>OfferedIds</c> — the ids actually shown to the planner — are
    /// authoritative; a named frame id absent from that set degrades its own clip rather than the
    /// step. Sending such a frame to a third-party provider is a separate decision, owned by
    /// <see cref="AllowSourceMediaEgress"/>; see <c>GeneratedShotEgressPolicy</c>.
    /// </remarks>
    public ExtractInputRef? Analysis { get; set; }

    /// <summary>
    /// The model identifier to use for video generation.
    /// Default: "MiniMax-H3"
    /// </summary>
    public string Model { get; set; } = "MiniMax-H3";

    /// <summary>
    /// The output resolution for the generated video (e.g., "768P", "1080P").
    /// Default: "768P"
    /// </summary>
    [AppBindable(AppInputType.Choice, Label = "Resolution", GovernedByBriefField = BriefFieldConsumers.Resolution)]
    public string Resolution { get; set; } = "768P";

    /// <summary>
    /// The duration in seconds for the generated video.
    /// Default: 6
    /// </summary>
    [AppBindable(AppInputType.Duration, AppInputType.Number, Label = "Clip length", Min = 1, Max = 60)]
    public int DurationSeconds { get; set; } = 6;

    /// <summary>
    /// The aspect ratio for the generated video (e.g., "16:9", "9:16", "1:1").
    /// Default: "16:9"
    /// </summary>
    [AppBindable(AppInputType.Shape, AppInputType.Choice, Label = "Shape", Choices = ["9:16", "1:1", "16:9", "4:5"], GovernedByBriefField = BriefFieldConsumers.Format)]
    public string Ratio { get; set; } = "16:9";

    /// <summary>
    /// The inference provider ID to use for video generation.
    /// Nullable; if null, the default video-generation-capable provider is selected.
    /// </summary>
    public Guid? ProviderId { get; set; }

    /// <summary>Technical sanity bound on <see cref="MaxClips"/>; never a product limit (credits and budget gates are).</summary>
    public const int MaxClipsCeiling = 64;

    /// <summary>Technical sanity bound on <see cref="Takes"/>; never a product limit (credits and budget gates are).</summary>
    public const int MaxTakesCeiling = 64;

    /// <summary>
    /// The number of takes/attempts to generate for this video.
    /// Must be between 1 and <see cref="MaxTakesCeiling"/> inclusive (a technical sanity bound, not a
    /// product limit: the user's credits, <see cref="MaxSpendCredits"/> and the budget gate are the real limits).
    /// Default: 1
    /// </summary>
    [AppBindable(AppInputType.Number, AppInputType.Choice, Label = "Takes", Min = 1, Max = 4)]
    public int Takes { get; set; } = 1;

    /// <summary>
    /// Author-set hard ceiling on the number of clips this step may generate, counted ACROSS the
    /// whole step. In <see cref="VideoGeneratePlanSource.Inline"/> every take is one generated clip
    /// (<c>clip-1..clip-N</c>), so the step generates <see cref="Takes"/> clips and
    /// <see cref="Validate()"/> rejects <c>Takes &gt; MaxClips</c> before any budget reservation.
    /// It counts clips, so TAKES, not shots: four shots at two takes each need 8, and with the ceiling at 4 only
    /// two shots are made.
    /// In <see cref="VideoGeneratePlanSource.StepRef"/> the ceiling is spent in plan order — one
    /// clip per shot, then per take — and every later shot is skipped rather than submitted.
    /// Validated on its own (1..<see cref="MaxClipsCeiling"/> inclusive; the ceiling is a technical
    /// sanity bound, not a product cap: the user's credits, <see cref="MaxSpendCredits"/>, the budget gate and
    /// the up-front credit hold bound the spend), never derived from <see cref="Takes"/>.
    /// Default: 1
    /// </summary>
    [AppBindable(AppInputType.Number, AppInputType.Choice, Label = "Most clips", Min = 1, Max = MaxClipsCeiling, Integer = true)]
    public int MaxClips { get; set; } = 1;

    /// <summary>
    /// The maximum spend allowed for this video generation step, in platform CREDITS.
    /// Must be positive (> 0) to be valid. One credit is <c>$0.006</c> of platform cost
    /// (<see cref="ReelBolt.Shared.Metering.PricingDefaults.CreditUsdValue"/>), and a vendor's own USD
    /// quote is converted into credits by <see cref="ReelBolt.Shared.Metering.CreditCurrency"/> at
    /// the boundary, so dollars never reach this cap or the ledger it gates.
    /// </summary>
    [AppBindable(AppInputType.Number, Label = "Most credits to spend", Min = 1, Max = 100000, SpendCap = true)]
    public decimal MaxSpendCredits { get; set; }

    /// <summary>
    /// Whether to allow source media egress to the external video generation provider.
    /// Default: false (source media is not sent to external providers)
    /// </summary>
    public bool AllowSourceMediaEgress { get; set; } = false;

    /// <summary>Inclusive lower bound of <see cref="PollIntervalSeconds"/>.</summary>
    public const int MinPollIntervalSeconds = 1;

    /// <summary>Inclusive upper bound of <see cref="PollIntervalSeconds"/>.</summary>
    public const int MaxPollIntervalSeconds = 300;

    /// <summary>Inclusive lower bound of <see cref="TimeoutSeconds"/>.</summary>
    public const int MinTimeoutSeconds = 60;

    /// <summary>Inclusive upper bound of <see cref="TimeoutSeconds"/>.</summary>
    public const int MaxTimeoutSeconds = 7200;

    /// <summary>
    /// Polling interval in seconds for checking the status of generation jobs.
    /// Must be between 1 and 300 inclusive, and not exceed <see cref="TimeoutSeconds"/>.
    /// MiniMax recommends polling every 10 seconds, hence the default.
    /// Default: 10
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// How long, in seconds, a submitted take is polled before it ends <c>POLL_TIMEOUT</c> (the clock
    /// starts once the provider slot is held). Must be between 60 and 7200 inclusive, and at least
    /// <see cref="PollIntervalSeconds"/>. A MiniMax generation takes minutes, so anything under 60s
    /// is near-certain spend-then-timeout; two hours is well inside the engine's own run limits.
    /// Default: 1800 (30 minutes)
    /// </summary>
    public int TimeoutSeconds { get; set; } = 1800;

    /// <summary>
    /// Behaviour when this step runs again after a ReviewLoop loop-back. <c>Reuse</c> (default)
    /// serves identical, already-delivered takes from the ledger; <c>Regenerate</c> pays for fresh
    /// takes each iteration. The per-execution "regenerate" choice made at Run/Retry overrides this.
    /// </summary>
    public VideoGenerateLoopBackBehavior LoopBackBehavior { get; set; } = VideoGenerateLoopBackBehavior.Reuse;

    // -- Issue #93 phase 4/5 additions. Every field below is appended and defaulted OFF so a config
    //    written before this wave deserializes to byte-identical behaviour: no look words are added
    //    to any prompt, no camera word is defaulted, the credit cap stays the only paid cap, and no conform
    //    pass runs. --

    /// <summary>
    /// Look matching (phase 4): when <c>true</c> on the <see cref="VideoGeneratePlanSource.StepRef"/>
    /// path, each planned shot's prompt gains a first-party clause describing the MEASURED look of
    /// the real footage at its anchor (colour temperature, tone, saturation words from the
    /// <see cref="Analysis"/> step's Phase-4 descriptors — see <c>LookMatchingPromptWords</c>), so
    /// the generated clip lands close to the program's grade before grading. Deterministic: the
    /// model never produces these words. Requires <see cref="Analysis"/>; no clause is added when
    /// the anchor has no measured shot. Default <c>false</c>.
    /// </summary>
    public bool LookMatching { get; set; } = false;

    /// <summary>
    /// Camera-move continuity (phase 4): when <c>true</c> on the plan path and a planned shot leaves
    /// <c>camera</c> blank, the camera word defaults to the real anchor shot's classified move
    /// (<c>Static</c>/<c>Handheld</c> — the analyzer's <c>Pan</c>/<c>Tilt</c>/<c>Zoom</c> classes
    /// carry no direction and are deliberately NOT mapped). Never overrides a camera word the
    /// planner chose. Requires <see cref="Analysis"/>. Default <c>false</c>.
    /// </summary>
    public bool CameraContinuity { get; set; } = false;

    /// <summary>
    /// GPU-minutes cap for a self-hosted <c>ComfyUI</c> provider (phase 5), the counterpart of
    /// <see cref="MaxSpendCredits"/> for a kind that bills nothing: the step's whole GPU-seconds
    /// estimate (the template's GPU-seconds-per-output-second table × snapped duration × takes) is
    /// reserved through the same atomic budget gate and ledger, in GPU-seconds, against this cap
    /// and the engine's daily GPU-minute budgets. Required (&gt; 0) when the resolved provider is
    /// ComfyUI; ignored for every priced kind. <see cref="Validate()"/> accepts a config whose
    /// <see cref="MaxSpendCredits"/> is 0 only when this is positive, and <see cref="ValidateForKind"/>
    /// rejects the wrong cap for the resolved kind before any reservation.
    /// </summary>
    public decimal MaxGpuMinutes { get; set; }

    /// <summary>
    /// Conform pass (phase 5): target frame rate the delivered clip is frame-interpolated UP to
    /// (ffmpeg <c>minterpolate</c>, motion-compensated) when the provider's clip has a lower rate —
    /// a 16/24 fps open-model clip conformed to a 30/60 fps program. <c>0</c> (default) keeps the
    /// clip's own rate. Never downsamples; a clip already at or above the target is left at its
    /// own rate (no extra filter term, byte-identical normalization). 1..120 when set.
    /// </summary>
    public int ConformFps { get; set; }

    /// <summary>
    /// Conform pass (phase 5): target output height the delivered clip is upscaled to (ffmpeg
    /// <c>scale</c>, Lanczos, width kept to aspect) when the provider's clip is smaller — 480p
    /// open-model output brought to a 1080p program. <c>0</c> (default) keeps the clip's own size.
    /// Never downscales; a clip already at or above the target is untouched. 16..4320 when set.
    /// </summary>
    public int ConformHeight { get; set; }

    /// <summary>
    /// The conform height to apply (T-105). An explicit <paramref name="configured"/> value always wins. When it is
    /// 0 (unset) and the run carries an explicit output resolution (its SHORT side, e.g. 1080), the clip is conformed
    /// up to that: its height for a landscape or square ratio, or the height that makes its width the short side for a
    /// portrait ratio. A run with no explicit resolution (Auto) changes nothing. The conform pass only ever upscales a
    /// clip that is smaller, so this can never push a clip beyond what the run chose.
    /// </summary>
    public static ConformHeightResolution ResolveConformHeight(int configured, int? runShortSide, string? ratio)
    {
        if (configured > 0)
        {
            return new ConformHeightResolution(configured, "config");
        }

        if (runShortSide is not { } shortSide || shortSide <= 0)
        {
            return new ConformHeightResolution(0, "none");
        }

        int height = shortSide;
        string[]? parts = ratio?.Split(':');
        if (parts is { Length: 2 }
            && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h)
            && w > 0 && h > w)
        {
            height = (int)(Math.Round(shortSide * h / w / 2, MidpointRounding.AwayFromZero) * 2);
        }

        return new ConformHeightResolution(Math.Clamp(height, 16, 4320), "run");
    }

    /// <summary>
    /// Inline mode only (T-105): the image sent as the clip's FIRST frame (<c>Images[0]</c>), taken from an
    /// earlier step whose output is an image (an <c>ExtractFrame</c> step) — <c>From=Previous</c> or
    /// <c>From=Step</c> with <see cref="ExtractInputRef.StepOrder"/>, the same reference type
    /// <see cref="Plan"/> uses. Sent only when the resolved provider and model accept it and
    /// <see cref="AllowSourceMediaEgress"/> is true; otherwise the step fails <c>EGRESS_REFUSED</c> /
    /// <c>KEYFRAME_NOT_SUPPORTED</c> before anything is submitted. Pricing is unchanged. Null (default) sends none.
    /// </summary>
    public ExtractInputRef? FirstFrame { get; set; }

    /// <summary>The image sent as the LAST frame (<c>Images[1]</c>); same rules as <see cref="FirstFrame"/>, and requires a first frame.</summary>
    public ExtractInputRef? LastFrame { get; set; }

    /// <summary>A project image file (instead of a step) for the first frame. Mutually exclusive with <see cref="FirstFrame"/>.</summary>
    public Guid? FirstFrameProjectFileId { get; set; }

    /// <summary>A project image file (instead of a step) for the last frame. Mutually exclusive with <see cref="LastFrame"/>.</summary>
    public Guid? LastFrameProjectFileId { get; set; }

    /// <summary>True when any first/last frame image is configured.</summary>
    [JsonIgnore]
    public bool HasFrameInputs =>
        FirstFrame is not null || LastFrame is not null || FirstFrameProjectFileId is not null || LastFrameProjectFileId is not null;

    // -- Avatars phase 2 (docs/design/avatars.md "Engine and generation", AV-10). Appended and defaulted to null so
    //    a config written before them deserializes, validates, hashes and runs byte-identically. --

    /// <summary>
    /// The project avatar (a consented likeness, <c>project_avatars</c>) this step shows. Its reference pictures,
    /// short videos and speech samples are sent to the provider as reference material on every take. Null (default)
    /// keeps every existing path byte-identical. Inline only in this phase, never with a first/last frame, and the
    /// step must also have <see cref="AllowSourceMediaEgress"/> (set only in the app, never by the assistant).
    /// </summary>
    public Guid? AvatarId { get; set; }

    /// <summary>
    /// Optional subset of the avatar's reference ROLES to send (for example only <c>Face</c> pictures). Null sends
    /// every role the model accepts, in the avatar's order. Must keep at least one picture role.
    /// </summary>
    public List<AvatarReferenceRole>? AvatarReferenceRoles { get; set; }

    /// <summary>
    /// The speech that drives the person's mouth (a <c>Voiceover</c> step's output, <c>From=Step</c>/<c>Previous</c>).
    /// Only for a model that declares that audio drives speech (<c>VideoReferenceInputSupport.AudioDrivesSpeech</c>);
    /// MiniMax does not (unmeasured), so a step setting it on MiniMax fails <c>AVATAR_NOT_SUPPORTED_BY_MODEL</c>
    /// before anything is reserved. Reserved for phase 3's talking-head models.
    /// </summary>
    public ExtractInputRef? AvatarAudio { get; set; }

    /// <summary>A project audio file instead of a step for <see cref="AvatarAudio"/>; mutually exclusive with it.</summary>
    public Guid? AvatarAudioProjectFileId { get; set; }

    /// <summary>
    /// The picture-edit model (an <c>ImageEdit</c> model capability id) that makes the still a talking-head model animates
    /// (docs/design/app-inputs.md 27). Resolved like every other capability: this one when set, else the default
    /// <c>ImageEdit</c> capability; it must still be a fal.ai instruction edit that takes several pictures. An app sets it
    /// from a <c>model</c> input bound to <c>@stillModel</c>. Only for a step with <see cref="AvatarAudio"/>. Appended; null
    /// (default) keeps every existing step byte-identical.
    /// </summary>
    public Guid? StillProviderId { get; set; }

    /// <summary>True when this step shows an avatar: the legacy <see cref="AvatarId"/> or at least one of <see cref="Subjects"/>.</summary>
    [JsonIgnore]
    public bool HasAvatar => AvatarId is not null || Subjects is { Count: > 0 };

    /// <summary>True when a driving audio track is configured for the avatar.</summary>
    [JsonIgnore]
    public bool HasAvatarAudio => AvatarAudio is not null || AvatarAudioProjectFileId is not null;

    /// <summary>
    /// The picture roles of a person and of an object (docs/design/avatar-objects.md); a role subset must keep at least
    /// one, since an avatar is shown from its pictures. The kind of the avatar is not known here, so a subset is judged
    /// against both sets and the engine applies it to the avatar it resolves.
    /// </summary>
    private static readonly AvatarReferenceRole[] PictureRoles =
        [AvatarReferenceRole.Face, AvatarReferenceRole.HalfBody, AvatarReferenceRole.FullBody,
            AvatarReferenceRole.Front, AvatarReferenceRole.Angle, AvatarReferenceRole.Detail];

    private void ValidateAvatar(List<string> errors)
    {
        if (AvatarId is null && Subjects is not null)
        {
            // The step names its subjects itself: ValidateSubjects judges them (and the speech of a talking head).
            if (AvatarReferenceRoles is not null)
            {
                errors.Add("AvatarReferenceRoles applies only to a step with an AvatarId: give each of Subjects its own Roles");
            }

            return;
        }

        if (AvatarId is null)
        {
            if (AvatarReferenceRoles is not null)
            {
                errors.Add("AvatarReferenceRoles applies only to a step with an AvatarId");
            }

            // Avatars phase 3: a step may name its speech without naming the avatar, which the run brief then fills
            // (an inline step without a frame, exactly the steps the brief's avatar applies to). The engine refuses
            // such a step when no avatar arrives, so the speech is never silently ignored.
            if (HasAvatarAudio)
            {
                if (PlanSource != VideoGeneratePlanSource.Inline || HasFrameInputs)
                {
                    errors.Add("AvatarAudio applies only to an inline step without a first/last frame (the step, or the run brief, names the avatar)");
                }

                ValidateAvatarAudio(errors);
            }

            return;
        }

        if (AvatarId == Guid.Empty)
        {
            errors.Add("AvatarId must be an avatar id");
        }

        if (PlanSource != VideoGeneratePlanSource.Inline)
        {
            errors.Add("AvatarId applies to PlanSource=Inline only: one avatar per step, shown in every take");
        }

        if (HasFrameInputs)
        {
            errors.Add("AvatarId cannot be combined with FirstFrame/LastFrame: a model's reference mode (the avatar) and its frame mode are mutually exclusive");
        }

        if (AvatarReferenceRoles is { } roles)
        {
            if (roles.Count == 0 || !roles.Any(r => PictureRoles.Contains(r)))
            {
                errors.Add("AvatarReferenceRoles must include at least one picture role (Face, HalfBody or FullBody for a person; Front, Angle or Detail for an object)");
            }

            if (roles.Any(r => !Enum.IsDefined(r)))
            {
                errors.Add("AvatarReferenceRoles names an unknown role");
            }
        }

        ValidateAvatarAudio(errors);
    }

    private void ValidateAvatarAudio(List<string> errors)
    {
        if (AvatarAudio is not null && AvatarAudioProjectFileId is not null)
        {
            errors.Add("AvatarAudio names both a step and a project file: choose one");
        }

        if (AvatarAudioProjectFileId == Guid.Empty)
        {
            errors.Add("AvatarAudioProjectFileId must be a project file id");
        }

        ValidateInputRef(AvatarAudio, nameof(AvatarAudio), errors);
    }

    /// <summary>The still model (docs/design/app-inputs.md 27): a real id, and only on a talking-head step.</summary>
    private void ValidateStillProvider(List<string> errors)
    {
        if (StillProviderId == Guid.Empty)
        {
            errors.Add("StillProviderId must be a model id");
        }
        else if (StillProviderId is not null && !HasAvatarAudio)
        {
            errors.Add("StillProviderId applies only to a talking-head step (one with AvatarAudio)");
        }
    }

    /// <summary>
    /// Validates the configuration and returns a list of validation errors.
    /// Validation rules:
    /// - MaxSpendCredits must be positive (> 0); it is a CREDIT cap, not a dollar one
    /// - Takes must be between 1 and MaxTakesCeiling (64) inclusive
    /// - MaxClips must be between 1 and MaxClipsCeiling (64) inclusive
    /// - Takes must not exceed MaxClips (each take is one clip; MaxClips is the hard ceiling)
    /// - DurationSeconds must be positive (> 0)
    /// - PollIntervalSeconds must be between 1 and 300 inclusive
    /// - TimeoutSeconds must be between 60 and 7200 inclusive
    /// - TimeoutSeconds must be at least PollIntervalSeconds (checked only when both are in range)
    /// - PlanSource = StepRef requires a Plan reference
    /// - Plan/Analysis, when set, must be sourced From = Previous or From = Step, and From = Step
    ///   requires a StepOrder
    /// The poll/timeout rules are spend-relevant: this runs before any budget reservation, and a
    /// config that times out before (or without) its first poll would submit a paid take and then
    /// end it POLL_TIMEOUT. The 60s floor exists because a MiniMax generation takes minutes, the 7200s
    /// ceiling because no legitimate single clip needs longer than two hours.
    /// The Plan/Analysis rules are structural, not spend-relevant: they are what makes a
    /// plan-sourced step fail closed at save/execute time instead of discovering, mid-run, that it
    /// has no plan to walk or no analysis artifact to resolve keyframes against.
    /// </summary>
    /// <returns>A list of validation error messages. Empty if all validations pass.</returns>
    public List<string> Validate()
    {
        var errors = new List<string>();

        // Exactly one cap is meaningful per provider kind — credits for a priced vendor, GPU-minutes
        // for a self-hosted ComfyUI — and the kind is only known at execution time, so this
        // kind-blind check requires that at least one of them is positive and leaves "the right one
        // for THIS kind" to ValidateForKind(). The message is unchanged for the common case (both
        // zero) so every existing caller and test keeps seeing it verbatim.
        if (MaxSpendCredits <= 0 && MaxGpuMinutes <= 0)
        {
            errors.Add("MaxSpendCredits must be positive (greater than 0)");
        }

        if (MaxGpuMinutes < 0)
        {
            errors.Add("MaxGpuMinutes must not be negative");
        }

        if (ConformFps < 0 || ConformFps > 120)
        {
            errors.Add($"ConformFps must be 0 (off) or between 1 and 120 (got {ConformFps})");
        }

        if (ConformHeight < 0 || (ConformHeight > 0 && (ConformHeight < 16 || ConformHeight > 4320)))
        {
            errors.Add($"ConformHeight must be 0 (off) or between 16 and 4320 (got {ConformHeight})");
        }

        if (Takes < 1 || Takes > MaxTakesCeiling)
        {
            errors.Add($"Takes must be between 1 and {MaxTakesCeiling} inclusive");
        }

        var maxClipsInRange = MaxClips >= 1 && MaxClips <= MaxClipsCeiling;
        if (!maxClipsInRange)
        {
            errors.Add($"MaxClips must be between 1 and {MaxClipsCeiling} inclusive");
        }
        else if (Takes > MaxClips)
        {
            errors.Add($"Takes ({Takes}) must not exceed MaxClips ({MaxClips}): each take generates one clip");
        }

        if (DurationSeconds <= 0)
        {
            errors.Add("DurationSeconds must be positive (greater than 0)");
        }

        var pollInRange = PollIntervalSeconds >= MinPollIntervalSeconds && PollIntervalSeconds <= MaxPollIntervalSeconds;
        if (!pollInRange)
        {
            errors.Add($"PollIntervalSeconds must be between {MinPollIntervalSeconds} and {MaxPollIntervalSeconds} inclusive (got {PollIntervalSeconds})");
        }

        var timeoutInRange = TimeoutSeconds >= MinTimeoutSeconds && TimeoutSeconds <= MaxTimeoutSeconds;
        if (!timeoutInRange)
        {
            errors.Add($"TimeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} inclusive (got {TimeoutSeconds})");
        }

        if (pollInRange && timeoutInRange && TimeoutSeconds < PollIntervalSeconds)
        {
            errors.Add($"TimeoutSeconds ({TimeoutSeconds}) must be at least PollIntervalSeconds ({PollIntervalSeconds}): the step would time out before its first poll");
        }

        // Appended AFTER every phase-1 rule so an Inline config (the default, and every config
        // written before this wave) produces the exact error list it always did.
        if (PlanSource == VideoGeneratePlanSource.StepRef && Plan is null)
        {
            errors.Add("Plan must reference the planner step's GeneratedShotPlanOutput when PlanSource is StepRef (From=Step with StepOrder, or From=Previous)");
        }

        if (PlanSource == VideoGeneratePlanSource.ProjectFile && (PlanProjectFileId is null || PlanProjectFileId == Guid.Empty))
        {
            errors.Add("PlanProjectFileId must name the saved shot-plan project file when PlanSource is ProjectFile");
        }

        ValidateInputRef(Plan, nameof(Plan), errors);
        ValidateInputRef(Analysis, nameof(Analysis), errors);

        // T-105 first/last frame images. Appended after every earlier rule so a config that names none
        // produces the exact error list it always did.
        if (HasFrameInputs)
        {
            if (PlanSource != VideoGeneratePlanSource.Inline)
            {
                errors.Add("FirstFrame/LastFrame apply to PlanSource=Inline only: a plan names its own keyframes per shot");
            }

            ValidateFrameInput(FirstFrame, FirstFrameProjectFileId, nameof(FirstFrame), errors);
            ValidateFrameInput(LastFrame, LastFrameProjectFileId, nameof(LastFrame), errors);

            bool hasFirst = FirstFrame is not null || FirstFrameProjectFileId is not null;
            bool hasLast = LastFrame is not null || LastFrameProjectFileId is not null;
            if (hasLast && !hasFirst)
            {
                errors.Add("LastFrame requires a FirstFrame: the request's images are first frame, then last frame");
            }
        }

        if ((LookMatching || CameraContinuity) && PlanSource == VideoGeneratePlanSource.StepRef && Analysis is null)
        {
            errors.Add("Analysis must reference the VideoAnalyze step when LookMatching or CameraContinuity is enabled: both read that step's measured per-shot descriptors");
        }

        // Avatars phase 2, appended last: a config without an avatar produces the exact error list it always did.
        ValidateAvatar(errors);
        ValidateStillProvider(errors);

        // Several subjects per clip, appended last: a config that names none of the members below produces the exact
        // error list it always did.
        ValidateSubjects(errors);

        return errors;
    }

    /// <summary>
    /// The kind-aware half of validation, run by the executor once the provider row has resolved:
    /// a self-hosted <see cref="ReelBolt.Shared.Data.Models.InferenceProviderKind.ComfyUI"/>
    /// provider bills nothing, so it needs <see cref="MaxGpuMinutes"/> &gt; 0 and
    /// <see cref="MaxSpendCredits"/> is ignored; every priced kind needs <see cref="MaxSpendCredits"/> &gt; 0
    /// (a CREDIT cap, converted from the vendor's USD quote at the boundary) and
    /// <see cref="MaxGpuMinutes"/> is ignored. Pairing the wrong cap with a kind is rejected
    /// rather than silently treated as "no cap". Pure; no I/O.
    /// </summary>
    public List<string> ValidateForKind(ReelBolt.Shared.Data.Models.InferenceProviderKind kind)
    {
        var errors = new List<string>();

        if (kind == ReelBolt.Shared.Data.Models.InferenceProviderKind.ComfyUI)
        {
            if (MaxGpuMinutes <= 0)
            {
                errors.Add("MaxGpuMinutes must be positive (greater than 0) for a ComfyUI provider: it bills nothing, so its budget is GPU time, not credits");
            }
        }
        else if (MaxSpendCredits <= 0)
        {
            errors.Add($"MaxSpendCredits must be positive (greater than 0) for a {kind} provider: MaxGpuMinutes applies to ComfyUI only");
        }

        return errors;
    }

    private static void ValidateFrameInput(ExtractInputRef? reference, Guid? projectFileId, string field, List<string> errors)
    {
        if (reference is not null && projectFileId is not null)
        {
            errors.Add($"{field} names both a step and a project file: choose one");
            return;
        }

        if (projectFileId == Guid.Empty)
        {
            errors.Add($"{field}ProjectFileId must be a project file id");
        }

        ValidateInputRef(reference, field, errors);
    }

    /// <summary>
    /// Adds the structural errors for one optional <see cref="ExtractInputRef"/>, naming the field
    /// so the author knows which of the two to fix. A null reference is valid here — whether it is
    /// REQUIRED is <see cref="Validate()"/>'s (Plan) or the executor's (Analysis, only when a
    /// planned shot actually names a keyframe) decision, not this helper's.
    /// </summary>
    private static void ValidateInputRef(ExtractInputRef? reference, string field, List<string> errors)
    {
        if (reference is null)
        {
            return;
        }

        if (reference.From is not (ExtractInputSource.Previous or ExtractInputSource.Step))
        {
            errors.Add($"{field} must be sourced From=Previous or From=Step (got {reference.From})");
            return;
        }

        if (reference.From == ExtractInputSource.Step && reference.StepOrder is null)
        {
            errors.Add($"{field} with From=Step requires StepOrder");
        }
    }

    /// <summary>
    /// Validates the configuration against what the selected model actually offers, in addition to
    /// <see cref="Validate()"/>.
    /// <para>
    /// <b>Where the capabilities come from.</b> They are the resolved provider's <b>pricing basis</b>
    /// (<c>VideoGenerationPricingBasis.ResolveAsync</c>), not a bare
    /// <c>IVideoGenerationClient.Describe(Model)</c> call: for a provider that publishes a price
    /// table that basis is the published table, and for a provider that publishes none it is the
    /// vendor's own quote for the exact configuration this step will submit, re-expressed as
    /// capabilities by the oracle. A provider that can be priced by neither stays UNOFFERABLE and
    /// the reason is surfaced, exactly as fail-closed behaviour requires.
    /// </para>
    /// <para>
    /// Must pass before any budget reservation is made: <see cref="DurationSeconds"/> must lie
    /// within the model's Min..Max, and <see cref="Resolution"/>/<see cref="Ratio"/> must be
    /// offered verbatim. Comparison is ordinal, with no case folding and no trimming — a value
    /// the price table does not key exactly cannot be priced, so it is rejected, never guessed.
    /// </para>
    /// <para>
    /// Deliberately adds NO rule for <see cref="VideoGeneratePlanSource.StepRef"/>:
    /// <see cref="DurationSeconds"/> stays the Inline path's per-clip duration and the plan path
    /// snaps each shot's own duration word to the resolved model's range, so it is in range by
    /// construction. Checking <see cref="DurationSeconds"/> here therefore cannot reject a plan
    /// whose shots are all legal.
    /// </para>
    /// </summary>
    /// <param name="capabilities">The selected model's pricing basis's capabilities.</param>
    /// <returns>A list of validation error messages. Empty if all validations pass.</returns>
    public List<string> Validate(VideoGenerationCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var errors = Validate();

        if (DurationSeconds < capabilities.MinDurationSeconds || DurationSeconds > capabilities.MaxDurationSeconds)
        {
            // A basis whose Min and Max coincide — an oracle quote, whose duration range is a point
            // at the duration it actually priced — has no range to be "between", so the message
            // states the one legal value. Every other rule, message and order is unchanged.
            errors.Add(
                capabilities.MinDurationSeconds == capabilities.MaxDurationSeconds
                    ? $"DurationSeconds must be exactly {capabilities.MinDurationSeconds} for model {Model}"
                    : $"DurationSeconds must be between {capabilities.MinDurationSeconds} and {capabilities.MaxDurationSeconds} inclusive for model {Model}");
        }

        if (Resolution is null
            || !capabilities.PricePerSecondByResolution.Keys.Any(r => string.Equals(r, Resolution, StringComparison.Ordinal)))
        {
            errors.Add(
                $"Resolution '{Resolution}' is not offered by model {Model} (allowed: {string.Join(", ", capabilities.PricePerSecondByResolution.Keys)})");
        }

        if (Ratio is null
            || !capabilities.AllowedRatios.Any(r => string.Equals(r, Ratio, StringComparison.Ordinal)))
        {
            errors.Add(
                $"Ratio '{Ratio}' is not offered by model {Model} (allowed: {string.Join(", ", capabilities.AllowedRatios)})");
        }

        return errors;
    }

    // -- Several subjects per clip (docs/design/avatar-objects.md "Subjects in a video generation step"). Appended; a
    //    config that sets none of them deserializes, validates, hashes, SERIALIZES and runs byte-identically (each is
    //    written only when it is not its default). --

    /// <summary>The most subjects one clip shows.</summary>
    public const int MaxSubjects = 3;

    /// <summary>The most ids <see cref="PlannedSubjectIds"/> may list (the avatars a project can hold: 20 people and 20 objects).</summary>
    public const int MaxPlannedSubjectIds = 40;

    /// <summary>
    /// The project avatars this step shows in every take: 1 to <see cref="MaxSubjects"/>, distinct, people and objects,
    /// at most one of them a person (the engine reads the kinds when it resolves them). Inline only, never with a
    /// first/last frame, and never together with the legacy <see cref="AvatarId"/>, which stays and means "one subject".
    /// The step must also have <see cref="AllowSourceMediaEgress"/> (set only in the app). Null (default) keeps every
    /// existing path byte-identical.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VideoGenerateSubject>? Subjects { get; set; }

    /// <summary>
    /// How the subjects reach the model when it offers more than one way: <see cref="VideoSubjectMode.Auto"/> (default)
    /// uses the model's reference mode when it has one and one composed first frame otherwise;
    /// <see cref="VideoSubjectMode.References"/> and <see cref="VideoSubjectMode.FirstFrame"/> insist on one. A talking
    /// head (<see cref="AvatarAudio"/> on a model that animates a still) is not affected.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public VideoSubjectMode SubjectMode { get; set; } = VideoSubjectMode.Auto;

    /// <summary>
    /// Plan sources only: whether the shot planner may use the project's avatars and objects.
    /// <see cref="VideoPlannedSubjects.Auto"/> (default) offers them whenever <see cref="AllowSourceMediaEgress"/> is on;
    /// with it off, or with no usable subject in the project, nothing is offered and the run is unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public VideoPlannedSubjects PlannedSubjects { get; set; } = VideoPlannedSubjects.Auto;

    /// <summary>
    /// Plan sources only: the avatars the planner may use. Null (default) = every usable subject of the project;
    /// otherwise this allowlist (1 to <see cref="MaxPlannedSubjectIds"/> distinct ids).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Guid>? PlannedSubjectIds { get; set; }

    /// <summary>
    /// The subjects this step shows, whichever way it names them: <see cref="Subjects"/>, or the one legacy
    /// <see cref="AvatarId"/> with its <see cref="AvatarReferenceRoles"/>. Empty for a step that shows none.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<VideoGenerateSubject> EffectiveSubjects =>
        Subjects is { Count: > 0 } subjects ? subjects
        : AvatarId is Guid legacy ? [new VideoGenerateSubject(legacy, AvatarReferenceRoles)]
        : [];

    /// <summary>
    /// True when the step names its subjects through <see cref="Subjects"/> (the newer shape, which records
    /// <c>meta.subjects</c>); false for a step that shows none or names the one legacy <see cref="AvatarId"/>.
    /// </summary>
    [JsonIgnore]
    public bool NamesSubjects => Subjects is { Count: > 0 };

    private void ValidateSubjects(List<string> errors)
    {
        if (!Enum.IsDefined(SubjectMode))
        {
            errors.Add("SubjectMode must be Auto, References or FirstFrame");
        }

        if (!Enum.IsDefined(PlannedSubjects))
        {
            errors.Add("PlannedSubjects must be Auto or Off");
        }

        bool planSourced = PlanSource != VideoGeneratePlanSource.Inline;
        if (PlannedSubjects == VideoPlannedSubjects.Off && !planSourced)
        {
            errors.Add("PlannedSubjects applies to a plan-sourced step only (PlanSource=StepRef or ProjectFile): an inline step names its own Subjects");
        }

        if (PlannedSubjectIds is { } allowed)
        {
            if (!planSourced)
            {
                errors.Add("PlannedSubjectIds applies to a plan-sourced step only (PlanSource=StepRef or ProjectFile): an inline step names its own Subjects");
            }

            if (PlannedSubjects == VideoPlannedSubjects.Off)
            {
                errors.Add("PlannedSubjectIds cannot be combined with PlannedSubjects=Off: Off offers no subject at all");
            }

            if (allowed.Count == 0 || allowed.Count > MaxPlannedSubjectIds)
            {
                errors.Add($"PlannedSubjectIds must list between 1 and {MaxPlannedSubjectIds} avatars (leave it out to allow every usable one, or set PlannedSubjects=Off to allow none)");
            }

            if (allowed.Any(id => id == Guid.Empty))
            {
                errors.Add("PlannedSubjectIds must hold avatar ids");
            }

            if (allowed.Distinct().Count() != allowed.Count)
            {
                errors.Add("PlannedSubjectIds names the same avatar twice");
            }
        }

        if (Subjects is not { } subjects)
        {
            return;
        }

        if (AvatarId is not null)
        {
            errors.Add("Subjects cannot be combined with AvatarId: name every subject in Subjects (AvatarId alone means one subject)");
        }

        if (subjects.Count == 0 || subjects.Count > MaxSubjects)
        {
            errors.Add($"Subjects must name between 1 and {MaxSubjects} avatars");
        }

        if (subjects.Any(s => s is null || s.AvatarId == Guid.Empty))
        {
            errors.Add("Every entry of Subjects must carry an avatar id");
        }

        if (subjects.Where(s => s is not null).Select(s => s.AvatarId).Distinct().Count() != subjects.Count(s => s is not null))
        {
            errors.Add("Subjects names the same avatar twice");
        }

        if (planSourced)
        {
            errors.Add("Subjects applies to PlanSource=Inline only: a plan-sourced step lets the planner choose them (PlannedSubjects)");
        }

        if (HasFrameInputs)
        {
            errors.Add("Subjects cannot be combined with FirstFrame/LastFrame: the subjects are shown through the model's reference mode or through one composed first frame");
        }

        foreach (VideoGenerateSubject subject in subjects.Where(s => s is { Roles: not null }))
        {
            List<AvatarReferenceRole> roles = subject.Roles!;
            if (roles.Count == 0 || !roles.Any(r => PictureRoles.Contains(r)))
            {
                errors.Add("A subject's Roles must include at least one picture role (Face, HalfBody or FullBody for a person; Front, Angle or Detail for an object)");
            }

            if (roles.Any(r => !Enum.IsDefined(r)))
            {
                errors.Add("A subject's Roles names an unknown role");
            }
        }

        if (HasAvatarAudio)
        {
            if (subjects.Count != 1)
            {
                errors.Add("AvatarAudio (a talking head) applies only to a step with exactly one subject, a person");
            }

            ValidateAvatarAudio(errors);
        }
    }
}

/// <summary>The conform height a VideoGenerate step applies and where it came from: <c>config</c>, <c>run</c> or <c>none</c>.</summary>
public readonly record struct ConformHeightResolution(int Height, string Source);
