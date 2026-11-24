using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The slice of a workflow step the app logic reads: its type, its primary agent's type and whether it takes a
/// project video file chosen at run time. One shape for stored steps, template steps and save requests, so the
/// deriver, the validator and the cost class never see a different step than the one that will run.
/// </summary>
public readonly record struct AppStepFacts(StepType StepType, AgentType? AgentType = null, bool TakesProjectVideoFile = false, bool CallsProvider = false, bool TakesOverlayFile = false, bool TakesBriefAvatar = false, bool IsTalkingHead = false, bool CompileDynamics = false, bool TakesBriefImageFiles = false)
{
    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Builds the facts of one step from its type, agent type, (for VideoAnalyze) its analyze config JSON and (for
    /// VideoGenerate) its generate config JSON and (for VideoCompile) its compile config JSON.
    /// </summary>
    public static AppStepFacts From(StepType stepType, AgentType? agentType, string? videoAnalyzeConfigJson, string? videoGenerateConfigJson = null, string? videoCompileConfigJson = null, string? imageGenerateConfigJson = null) =>
        From(stepType, agentType, videoAnalyzeConfigJson, videoGenerateConfigJson, videoCompileConfigJson, honorSwitch: true, imageGenerateConfigJson);

    private static AppStepFacts From(StepType stepType, AgentType? agentType, string? videoAnalyzeConfigJson, string? videoGenerateConfigJson, string? videoCompileConfigJson, bool honorSwitch, string? imageGenerateConfigJson = null) =>
        new(stepType, agentType, stepType == StepType.VideoAnalyze && OpensProjectVideoSlot(videoAnalyzeConfigJson),
            stepType == StepType.VideoAnalyze && AnalyzeCallsProvider(videoAnalyzeConfigJson),
            stepType == StepType.VideoAnalyze && EmitsOverlayOffers(videoAnalyzeConfigJson),
            stepType == StepType.VideoGenerate && (!honorSwitch || BriefAvatarSwitch()) && AcceptsBriefAvatar(videoGenerateConfigJson),
            stepType == StepType.VideoGenerate && IsTalkingHeadConfig(videoGenerateConfigJson),
            stepType == StepType.VideoCompile && CompileEnablesDynamics(videoCompileConfigJson),
            stepType == StepType.ImageGenerate && ImageEditsBriefFiles(imageGenerateConfigJson));

    /// <summary>True when an ImageGenerate config is an Edit of the run's picture files (Source=BriefFiles).</summary>
    public static bool ImageEditsBriefFiles(string? imageGenerateConfigJson)
    {
        if (string.IsNullOrWhiteSpace(imageGenerateConfigJson))
            return false;
        try
        {
            return JsonSerializer.Deserialize<ImageGenerateStepConfig>(imageGenerateConfigJson, ImageGenerateStepConfig.JsonOptions) is { UsesBriefFiles: true };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True when the generate config carries avatar speech: a talking head, which needs a lip-sync model.</summary>
    public static bool IsTalkingHeadConfig(string? videoGenerateConfigJson)
    {
        if (string.IsNullOrWhiteSpace(videoGenerateConfigJson))
            return false;
        try
        {
            return JsonSerializer.Deserialize<VideoGenerateStepConfig>(videoGenerateConfigJson, ConfigOptions) is { HasAvatarAudio: true };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static Func<bool> _briefAvatarSwitch = static () => true;

    /// <summary>
    /// The deployment's avatar kill switch as the facts see it: while it answers false no step takes the brief's avatar, so
    /// no app view gains an Avatar input. The API installs the live <c>Avatars:Enabled</c> value at startup; the default
    /// (true) keeps the pure derivation unchanged for code that never installs one.
    /// </summary>
    public static void UseBriefAvatarSwitch(Func<bool> enabled) => _briefAvatarSwitch = enabled ?? (static () => true);

    private static bool BriefAvatarSwitch() => _briefAvatarSwitch();

    /// <summary>Whether avatars are on right now, read at call time (never cached by a caller).</summary>
    public static bool AvatarsEnabled => _briefAvatarSwitch();

    private static Func<bool>? _personAvatarSwitch;

    /// <summary>
    /// The people half of the avatar switches (<c>Avatars:Enabled</c>; docs/design/avatar-objects.md, decision AO-4):
    /// a recipe that shows a PERSON is listed and created only while it answers true, even when objects alone are on.
    /// The API installs the live value at startup next to <see cref="UseBriefAvatarSwitch"/>; code that installs none
    /// reads the one switch it has, as before objects existed.
    /// </summary>
    public static void UsePersonAvatarSwitch(Func<bool>? enabled) => _personAvatarSwitch = enabled;

    /// <summary>Whether avatars of people are on right now, read at call time (never cached by a caller).</summary>
    public static bool PersonAvatarsEnabled => (_personAvatarSwitch ?? _briefAvatarSwitch)();

    private static Func<bool>? _objectAvatarSwitch;

    /// <summary>The objects half of the avatar switches (<c>Avatars:Objects:Enabled</c>), installed like <see cref="UsePersonAvatarSwitch"/>.</summary>
    public static void UseObjectAvatarSwitch(Func<bool>? enabled) => _objectAvatarSwitch = enabled;

    /// <summary>
    /// Whether avatars of <paramref name="subjectKind"/> are on right now. A row of a kind whose switch is off is
    /// treated as absent wherever a step or a run brief names it (docs/design/avatar-objects.md "Avatar switches and
    /// plan features").
    /// </summary>
    public static bool AvatarKindEnabled(AvatarSubjectKind subjectKind) =>
        subjectKind == AvatarSubjectKind.Object ? (_objectAvatarSwitch ?? _briefAvatarSwitch)() : PersonAvatarsEnabled;

    /// <summary>
    /// The facts of a template's steps as if avatars were on, whatever the switch says: for building the cached edition,
    /// so what is built never depends on when it was first read. Readers apply the live switch themselves.
    /// </summary>
    public static IReadOnlyList<AppStepFacts> FromTemplateIgnoringSwitch(WorkflowTemplateDefinition template) =>
        template.Steps.Select(s => From(s.StepType, s.AgentType, s.VideoAnalyzeConfigJson, s.VideoGenerateConfigJson, s.VideoCompileConfigJson, honorSwitch: false, s.ImageGenerateConfigJson)).ToList();

    /// <summary>
    /// The facts of one stored step, reading every per-type config the facts look at. Prefer this to calling
    /// <see cref="From(StepType, AgentType?, string?, string?, string?, string?)"/> with a hand-picked subset: a config left out
    /// reads as "this step takes nothing" and the manifest check then refuses an input the step does consume.
    /// </summary>
    public static AppStepFacts FromStep(WorkflowStep s, AgentType? agentType) =>
        From(s.StepType, agentType, s.VideoAnalyzeConfigJson, s.VideoGenerateConfigJson, s.VideoCompileConfigJson, s.ImageGenerateConfigJson);

    /// <summary>The facts of every step of a template, in order.</summary>
    public static IReadOnlyList<AppStepFacts> FromTemplate(WorkflowTemplateDefinition template) =>
        template.Steps.Select(s => From(s.StepType, s.AgentType, s.VideoAnalyzeConfigJson, s.VideoGenerateConfigJson, s.VideoCompileConfigJson, s.ImageGenerateConfigJson)).ToList();

    /// <summary>
    /// True when the run brief's avatar would reach this generate step: an inline step that names no avatar and no
    /// first/last frame (the condition of the engine's <c>RunBriefOverrides.ApplyToVideoGenerate</c>), so the app view
    /// offers an avatar picker only where the choice is honoured.
    /// </summary>
    public static bool AcceptsBriefAvatar(string? videoGenerateConfigJson)
    {
        if (string.IsNullOrWhiteSpace(videoGenerateConfigJson))
            return false;
        try
        {
            VideoGenerateStepConfig? config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(videoGenerateConfigJson, ConfigOptions);
            return config is { PlanSource: VideoGeneratePlanSource.Inline, HasAvatar: false, HasFrameInputs: false };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True when the compile config enables the dynamic edit (<see cref="VideoCompileStepConfig.Dynamics"/> with Enabled).</summary>
    public static bool CompileEnablesDynamics(string? videoCompileConfigJson)
    {
        if (string.IsNullOrWhiteSpace(videoCompileConfigJson))
            return false;
        try
        {
            VideoCompileStepConfig? config = JsonSerializer.Deserialize<VideoCompileStepConfig>(videoCompileConfigJson, ConfigOptions);
            return config?.Dynamics is { Enabled: true };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the analyze config reads a project file the run chooses: a <c>ProjectFile</c> source with no
    /// pinned file id (the open slot the builder shows as "no file selected").
    /// </summary>
    public static bool OpensProjectVideoSlot(string? videoAnalyzeConfigJson)
    {
        if (string.IsNullOrWhiteSpace(videoAnalyzeConfigJson))
            return false;
        try
        {
            VideoAnalyzeStepConfig? config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(videoAnalyzeConfigJson, ConfigOptions);
            if (config is null)
                return false;
            IEnumerable<VideoSourceRef> sources = config.Sources is { Count: > 0 } many ? many : [config.Source];
            return sources.Any(s => s is { Kind: VideoSourceKind.ProjectFile, ProjectFileId: null });
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the analyze config offers pictures and videos for an <c>OverlayPlanner</c> step
    /// (<see cref="VideoAnalyzeStepConfig.EmitOverlayOffers"/>).
    /// </summary>
    public static bool EmitsOverlayOffers(string? videoAnalyzeConfigJson)
    {
        if (string.IsNullOrWhiteSpace(videoAnalyzeConfigJson))
            return false;
        try
        {
            VideoAnalyzeStepConfig? config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(videoAnalyzeConfigJson, ConfigOptions);
            return config?.EmitOverlayOffers == true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the analyze config calls a provider that bills: transcription, vision captioning, subject location,
    /// seeded object tracking, hosted segmentation, the decision pre-pass or the span screener
    /// (<see cref="VideoAnalyzeStepConfig.CallsAnyProvider"/>, the same rule the credit estimate and the run-rate
    /// bucket use). Plain compute-only analysis is not billed in credits.
    /// </summary>
    public static bool AnalyzeCallsProvider(string? videoAnalyzeConfigJson)
    {
        if (string.IsNullOrWhiteSpace(videoAnalyzeConfigJson))
            return false;
        try
        {
            VideoAnalyzeStepConfig? config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(videoAnalyzeConfigJson, ConfigOptions);
            return config?.CallsAnyProvider == true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
