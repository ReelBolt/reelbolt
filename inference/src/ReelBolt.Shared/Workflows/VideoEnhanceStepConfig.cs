using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Shared.Workflows;

/// <summary>Where a <c>VideoEnhance</c> step's work runs. Append-only: persisted by name.</summary>
public enum VideoEnhanceEngine
{
    /// <summary>Free, offline, ffmpeg on this host. The source never leaves the engine.</summary>
    Local,

    /// <summary>A hosted AI model. The source's picture leaves this host with consent; metered per output (or compute) second.</summary>
    Hosted
}

/// <summary>The two local denoise tables. Append-only: persisted by name.</summary>
public enum VideoEnhanceDenoiseStrength
{
    Light,
    Strong
}

/// <summary>
/// JSON configuration for a <c>StepType.VideoEnhance</c> workflow step, deserialised from
/// <c>WorkflowStep.VideoEnhanceConfigJson</c>. Deterministic, non-LLM: one video in, one enhanced video out with the
/// SAME duration and the source's OWN audio remuxed back untouched (the vendor is sent the whole file, but only the
/// picture is used and the audio is never altered). Local operations are free; hosted operations are metered by
/// <see cref="VideoEnhanceCreditPricing"/> and need the <c>video_enhance</c> plan feature and egress consent.
/// </summary>
/// <param name="Op">What to do: <see cref="VideoEnhanceOp.Upscale"/>, <see cref="VideoEnhanceOp.Interpolate"/>, <see cref="VideoEnhanceOp.Denoise"/> or <see cref="VideoEnhanceOp.MotionBlur"/>.</param>
/// <param name="Engine">Local (free) or Hosted (paid AI). Denoise and MotionBlur are local only; Upscale and Interpolate run both ways.</param>
/// <param name="Source">The one video to enhance: a project file, an earlier step's output or a step result. It is enhanced whole; a trim window is refused.</param>
/// <param name="ProviderId">The hosted provider row for a hosted operation; null resolves the capability's default. Not valid with a local operation.</param>
/// <param name="AllowSourceMediaEgress">Consent for the source's file to leave this host (hosted only). False refuses with <c>EGRESS_REFUSED</c> and uploads nothing.</param>
/// <param name="RegisterProjectFile">Also register the result as a project file (default true).</param>
/// <param name="MaxSourceSeconds">Longest source this step may enhance. Can only LOWER the deployment's cap: 120 s hosted, 600 s local.</param>
/// <param name="UpscaleFactor">Upscale multiplier: 2 or 4. Exactly one of this and <paramref name="TargetHeight"/> is set for Upscale.</param>
/// <param name="TargetHeight">Upscale target height: 720, 1080, 1440 or 2160. Exactly one of this and <paramref name="UpscaleFactor"/> is set for Upscale.</param>
/// <param name="FrameMultiplier">Frame multiplier for Interpolate: 2, 4 or 8 locally; 2 or 4 hosted. The output must stay at or below 240 fps.</param>
/// <param name="DenoiseStrength">Denoise strength (<c>Light</c> or <c>Strong</c>); required for Denoise.</param>
/// <param name="KeepGrain">Append light synthetic grain after denoising.</param>
/// <param name="ShutterAngle">Motion-blur shutter angle in degrees, 90..360; null means 180. MotionBlur only.</param>
[StepCard(
    "Enhances one video: upscale, smooth slow motion, denoise, motion blur or extend past the border. The local operations are free; the AI ones use credits and need consent.",
    ["One video from the project or an earlier step"],
    "An enhanced MP4 of the same length with the source's own audio, optionally saved as a project file",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCredits,
    ReadableBy = ["Later steps", "The project's files"],
    PairsWith = ["VideoAnalyze: the enhanced video can be analysed", "VideoCompile: the enhanced video can be the source"])]
public sealed record VideoEnhanceStepConfig(
    [property: Description("What to do: Upscale, Interpolate, Denoise, MotionBlur or Extend (Extend is hosted only).")]
    VideoEnhanceOp Op,
    [property: Description("Local (free, offline) or Hosted (paid AI, needs consent). Denoise and MotionBlur are local only.")]
    VideoEnhanceEngine Engine,
    [property: Description("The one video to enhance. A project file, an earlier step's output or a step result of this project.")]
    VideoSourceRef Source,
    [property: Description("Hosted provider id. Null uses the capability's default; not valid with a local operation.")]
    Guid? ProviderId = null,
    [property: Description("Consent for the source's file to be uploaded to a hosted model (presigned GET, 10 minutes). false refuses a hosted operation with EGRESS_REFUSED and uploads nothing. Local operations never upload.")]
    bool AllowSourceMediaEgress = false,
    [property: Description("Also register the enhanced video as a project file (default true).")]
    bool RegisterProjectFile = true,
    [property: Description("Longest source for this step in seconds; can only lower the deployment's cap (120 hosted, 600 local). Null uses the cap.")]
    int? MaxSourceSeconds = null,
    [property: Description("Upscale multiplier: 2 or 4. Set exactly one of upscaleFactor and targetHeight.")]
    int? UpscaleFactor = null,
    [property: Description("Upscale target height: 720, 1080, 1440 or 2160. Set exactly one of upscaleFactor and targetHeight.")]
    int? TargetHeight = null,
    [property: Description("Frame multiplier for Interpolate: 2, 4 or 8 (hosted: 2 or 4). The output must stay at or below 240 fps.")]
    int? FrameMultiplier = null,
    [property: Description("Denoise strength: Light or Strong (Denoise only).")]
    VideoEnhanceDenoiseStrength? DenoiseStrength = null,
    [property: Description("Keep a light synthetic grain after denoising (Denoise only).")]
    bool KeepGrain = false,
    [property: Description("Motion-blur shutter angle in degrees, 90..360; null means 180 (MotionBlur only).")]
    int? ShutterAngle = null,
    [property: Description("Hosted only: the source's width in pixels as the route measured it, so the credit hold prices the real source. Set with sourceHeight and sourceFps, all three or none.")]
    int? SourceWidth = null,
    [property: Description("Hosted only: the source's height in pixels as the route measured it (see sourceWidth).")]
    int? SourceHeight = null,
    [property: Description("Hosted only: the source's frame rate as the route measured it (see sourceWidth).")]
    double? SourceFps = null,
    [property: Description("Extend only: the aspect to extend the picture to: 16:9, 9:16, 1:1 or 4:5. Set this OR sides + one amount (the four margins), never both.")]
    string? TargetAspect = null,
    [property: Description("Extend only: fraction of the width to add on the left, 0..0.5. Every side that grows must carry the SAME amount; leave the others at 0.")]
    double? MarginLeft = null,
    [property: Description("Extend only: fraction of the width to add on the right, 0..0.5. Every side that grows must carry the SAME amount; leave the others at 0.")]
    double? MarginRight = null,
    [property: Description("Extend only: fraction of the height to add on the top, 0..0.5. Every side that grows must carry the SAME amount; leave the others at 0.")]
    double? MarginTop = null,
    [property: Description("Extend only: fraction of the height to add on the bottom, 0..0.5. Every side that grows must carry the SAME amount; leave the others at 0.")]
    double? MarginBottom = null,
    [property: Description("Extend only: what the new edges should show, up to 500 characters. Blank uses a neutral default.")]
    string? Prompt = null)
{
    /// <summary>The longest Extend prompt in characters.</summary>
    public const int MaxPromptLength = 500;

    /// <summary>The aspects Extend offers (a closed list; 4:5 is reached by margins on the default model).</summary>
    public static readonly string[] ExtendAspects = ["16:9", "9:16", "1:1", "4:5"];

    /// <summary>The neutral prompt used when an Extend step names none (the model requires one).</summary>
    public const string DefaultExtendPrompt = "Continue the scene naturally beyond the original frame, matching its look, lighting and motion.";

    /// <summary>True when any of the four margins is set.</summary>
    [JsonIgnore]
    public bool HasMargins => MarginLeft is not null || MarginRight is not null || MarginTop is not null || MarginBottom is not null;

    /// <summary>The longest side a source hint may carry (a sanity bound, well above 8K).</summary>
    public const int MaxSourceSideHint = 16384;

    /// <summary>The highest frame rate a source hint may carry.</summary>
    public const double MaxSourceFpsHint = 240;

    /// <summary>True when the route measured the source and wrote its size and rate (all three hints present).</summary>
    [JsonIgnore]
    public bool HasSourceHints => SourceWidth is > 0 && SourceHeight is > 0 && SourceFps is > 0;

    /// <summary>The absolute ceiling on <see cref="MaxSourceSeconds"/> a config may carry (the local cap, 10 minutes).</summary>
    public const int MaxSourceSecondsLimit = 600;

    /// <summary>The hosted request cap in seconds; a hosted step can only lower it.</summary>
    public const int HostedMaxSourceSeconds = VideoEnhanceCreditPricing.DefaultMaxSourceSeconds;

    /// <summary>One serialiser for run time and save time (Web defaults, enums as strings).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>True for <see cref="VideoEnhanceEngine.Hosted"/>: the picture leaves the host and credits are spent.</summary>
    [JsonIgnore]
    public bool IsHosted => Engine == VideoEnhanceEngine.Hosted;

    /// <summary>The same rule the credit estimate, the run-rate bucket and the linter use: only a hosted operation calls a provider.</summary>
    [JsonIgnore]
    public bool CallsAnyProvider => IsHosted;

    /// <summary>The capability a hosted operation uses, or null for a local one.</summary>
    [JsonIgnore]
    public Data.Models.InferenceProviderCapability? HostedCapability => Op switch
    {
        VideoEnhanceOp.Upscale => Data.Models.InferenceProviderCapability.VideoUpscale,
        VideoEnhanceOp.Interpolate => Data.Models.InferenceProviderCapability.FrameInterpolation,
        VideoEnhanceOp.Extend => Data.Models.InferenceProviderCapability.VideoOutpaint,
        _ => null
    };

    /// <summary>The prompt an Extend sends: the step's own trimmed text, or the neutral default when blank.</summary>
    public string EffectiveExtendPrompt() => string.IsNullOrWhiteSpace(Prompt) ? DefaultExtendPrompt : Prompt.Trim();

    /// <summary>The Extend canvas for a source of this size, or null when the config asks for one the platform cannot offer.</summary>
    public Inference.VideoOutpaintCanvas? ExtendCanvas(int sourceWidth, int sourceHeight) =>
        !string.IsNullOrWhiteSpace(TargetAspect)
            ? Inference.VideoOutpaintGeometry.ForAspect(sourceWidth, sourceHeight, TargetAspect)
            : Inference.VideoOutpaintGeometry.ForMargins(sourceWidth, sourceHeight, MarginLeft ?? 0, MarginRight ?? 0, MarginTop ?? 0, MarginBottom ?? 0);

    /// <summary>The effective source cap in seconds: the step's own setting, never above the deployment's cap.</summary>
    public int EffectiveMaxSourceSeconds(int deploymentCap)
    {
        int cap = deploymentCap > 0 ? deploymentCap : HostedMaxSourceSeconds;
        return MaxSourceSeconds is { } own ? Math.Min(own, cap) : cap;
    }

    /// <summary>The local engine's request, or null for a hosted operation.</summary>
    public VideoEnhanceLocalRequest? ToLocalRequest() => IsHosted
        ? null
        : new VideoEnhanceLocalRequest(
            Op,
            UpscaleFactor,
            TargetHeight,
            FrameMultiplier,
            DenoiseStrength,
            KeepGrain,
            ShutterAngle);

    /// <summary>Every reason this config may not be saved or run, in field order; empty means valid. Never throws.</summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (!Enum.IsDefined(Op))
            errors.Add($"Op must be Upscale, Interpolate, Denoise, MotionBlur or Extend (found {Op}).");
        if (!Enum.IsDefined(Engine))
            errors.Add($"Engine must be Local or Hosted (found {Engine}).");

        if (Source is null)
        {
            errors.Add("Source is required.");
        }
        else
        {
            if (Source.Kind is VideoSourceKind.GeneratedClips or VideoSourceKind.BriefFiles)
                errors.Add($"Source={Source.Kind} is not supported; VideoEnhance takes exactly one existing video (a project file, a step output or a step result).");
            if (Source.Validate() is { } sourceError)
                errors.Add(sourceError);
            if (Source.InSec is not null || Source.OutSec is not null)
                errors.Add("Source.InSec/OutSec are not supported by VideoEnhance; it enhances the whole source.");
        }

        if (MaxSourceSeconds is { } seconds && (seconds < 1 || seconds > MaxSourceSecondsLimit))
            errors.Add($"MaxSourceSeconds must be between 1 and {MaxSourceSecondsLimit} (found {seconds}).");
        else if (IsHosted && MaxSourceSeconds is { } hosted && hosted > HostedMaxSourceSeconds)
            errors.Add($"MaxSourceSeconds on a hosted operation must be between 1 and {HostedMaxSourceSeconds} (found {hosted}).");

        if (IsHosted && Op is VideoEnhanceOp.Denoise or VideoEnhanceOp.MotionBlur)
            errors.Add($"{Op} is a local operation; set engine=Local (the AI path offers Upscale, Interpolate and Extend).");

        if (ProviderId is { } providerId && providerId == Guid.Empty)
            errors.Add("ProviderId must be a provider id or omitted.");
        else if (ProviderId is not null && !IsHosted)
            errors.Add("ProviderId is only valid with engine=Hosted.");

        if (AllowSourceMediaEgress && !IsHosted)
            errors.Add("AllowSourceMediaEgress is only valid with engine=Hosted; a local operation never uploads.");

        int hints = (SourceWidth is null ? 0 : 1) + (SourceHeight is null ? 0 : 1) + (SourceFps is null ? 0 : 1);
        if (hints is not (0 or 3))
            errors.Add("SourceWidth, SourceHeight and SourceFps are set together or not at all.");
        else if (hints == 3)
        {
            if (!IsHosted)
                errors.Add("SourceWidth, SourceHeight and SourceFps are only valid with engine=Hosted.");
            if (SourceWidth is < 1 or > MaxSourceSideHint || SourceHeight is < 1 or > MaxSourceSideHint)
                errors.Add($"SourceWidth and SourceHeight must be between 1 and {MaxSourceSideHint}.");
            if (!(SourceFps > 0) || SourceFps > MaxSourceFpsHint || double.IsNaN(SourceFps!.Value))
                errors.Add($"SourceFps must be greater than 0 and at most {MaxSourceFpsHint}.");
        }

        if (Op == VideoEnhanceOp.Extend && !IsHosted)
            errors.Add("Extend is a hosted operation; set engine=Hosted.");

        if (Op != VideoEnhanceOp.Extend)
        {
            RejectFields(errors, Op.ToString().ToLowerInvariant(),
                (TargetAspect, nameof(TargetAspect)), (MarginLeft, nameof(MarginLeft)), (MarginRight, nameof(MarginRight)),
                (MarginTop, nameof(MarginTop)), (MarginBottom, nameof(MarginBottom)), (Prompt, nameof(Prompt)));
        }

        switch (Op)
        {
            case VideoEnhanceOp.Extend:
                ValidateExtend(errors);
                break;
            case VideoEnhanceOp.Upscale:
                ValidateUpscale(errors);
                break;
            case VideoEnhanceOp.Interpolate:
                ValidateInterpolate(errors);
                break;
            case VideoEnhanceOp.Denoise:
                if (DenoiseStrength is null)
                    errors.Add("Denoise needs denoiseStrength Light or Strong.");
                RejectFields(errors, "denoise",
                    (UpscaleFactor, nameof(UpscaleFactor)), (TargetHeight, nameof(TargetHeight)),
                    (FrameMultiplier, nameof(FrameMultiplier)), (ShutterAngle, nameof(ShutterAngle)));
                break;
            case VideoEnhanceOp.MotionBlur:
                if (ShutterAngle is { } angle && (angle < 90 || angle > 360))
                    errors.Add($"ShutterAngle must be between 90 and 360 degrees (found {angle}).");
                RejectFields(errors, "motion blur",
                    (UpscaleFactor, nameof(UpscaleFactor)), (TargetHeight, nameof(TargetHeight)),
                    (FrameMultiplier, nameof(FrameMultiplier)), (DenoiseStrength, nameof(DenoiseStrength)));
                break;
        }

        return errors;
    }

    private void ValidateExtend(List<string> errors)
    {
        bool aspect = !string.IsNullOrWhiteSpace(TargetAspect);
        if (aspect == HasMargins)
            errors.Add("Extend needs exactly one of targetAspect (16:9, 9:16, 1:1, 4:5) and the margins (marginLeft, marginRight, marginTop, marginBottom).");
        else if (aspect && !ExtendAspects.Contains(TargetAspect!.Trim()))
            errors.Add($"TargetAspect must be 16:9, 9:16, 1:1 or 4:5 (found {TargetAspect}).");

        foreach ((double? value, string name) in new (double?, string)[]
                 { (MarginLeft, nameof(MarginLeft)), (MarginRight, nameof(MarginRight)), (MarginTop, nameof(MarginTop)), (MarginBottom, nameof(MarginBottom)) })
        {
            if (value is { } m && (double.IsNaN(m) || m < 0 || m > 0.5))
                errors.Add($"{name} must be between 0 and 0.5 (found {m}).");
        }

        if (HasMargins && (MarginLeft ?? 0) + (MarginRight ?? 0) + (MarginTop ?? 0) + (MarginBottom ?? 0) <= 0)
            errors.Add("Extend margins must add something on at least one side.");

        if (HasMargins && Inference.VideoOutpaintGeometry.AreUneven(MarginLeft ?? 0, MarginRight ?? 0, MarginTop ?? 0, MarginBottom ?? 0))
            errors.Add("Extend margins must be one amount on the sides you pick: set the same value on every side that grows and leave the others at 0.");

        if (Prompt is { } prompt && prompt.Length > MaxPromptLength)
            errors.Add($"Prompt must be at most {MaxPromptLength} characters (found {prompt.Length}).");

        RejectFields(errors, "extend",
            (UpscaleFactor, nameof(UpscaleFactor)), (TargetHeight, nameof(TargetHeight)), (FrameMultiplier, nameof(FrameMultiplier)),
            (DenoiseStrength, nameof(DenoiseStrength)), (ShutterAngle, nameof(ShutterAngle)));
    }

    private void ValidateUpscale(List<string> errors)
    {
        if (UpscaleFactor is not null == TargetHeight is not null)
        {
            errors.Add("Upscale needs exactly one of upscaleFactor (2 or 4) and targetHeight (720, 1080, 1440, 2160).");
        }
        else if (UpscaleFactor is { } factor && factor is not (2 or 4))
        {
            errors.Add($"UpscaleFactor must be 2 or 4 (found {factor}).");
        }
        else if (TargetHeight is { } height && height is not (720 or 1080 or 1440 or 2160))
        {
            errors.Add($"TargetHeight must be 720, 1080, 1440 or 2160 (found {height}).");
        }

        RejectFields(errors, "upscale",
            (FrameMultiplier, nameof(FrameMultiplier)), (DenoiseStrength, nameof(DenoiseStrength)), (ShutterAngle, nameof(ShutterAngle)));
    }

    private void ValidateInterpolate(List<string> errors)
    {
        if (FrameMultiplier is not { } multiplier || multiplier is not (2 or 4 or 8))
            errors.Add("Interpolate needs frameMultiplier 2, 4 or 8.");
        else if (IsHosted && multiplier is not (2 or 4))
            errors.Add("A hosted Interpolate supports frameMultiplier 2 or 4; 8x is local only.");

        RejectFields(errors, "interpolate",
            (UpscaleFactor, nameof(UpscaleFactor)), (TargetHeight, nameof(TargetHeight)),
            (DenoiseStrength, nameof(DenoiseStrength)), (ShutterAngle, nameof(ShutterAngle)));
    }

    private static void RejectFields(List<string> errors, string op, params (object? Value, string Name)[] fields)
    {
        foreach ((object? value, string name) in fields)
        {
            if (value is not null)
                errors.Add($"{name} is not valid with op={op}.");
        }
    }
}

/// <summary>The local engine's request shape, kept free of the WorkflowEngine project's types.</summary>
public sealed record VideoEnhanceLocalRequest(
    VideoEnhanceOp Op,
    int? UpscaleFactor,
    int? TargetHeight,
    int? FrameMultiplier,
    VideoEnhanceDenoiseStrength? DenoiseStrength,
    bool KeepGrain,
    int? ShutterAngle);
