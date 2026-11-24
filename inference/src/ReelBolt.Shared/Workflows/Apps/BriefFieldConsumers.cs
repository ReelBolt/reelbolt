using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// Which run-brief fields a step reads: the consumption matrix of docs/design/workflow-apps.md 3.8 as data, so
/// the app deriver and validator (and later the brief registry and the pre-run linter) share one answer. A
/// field a step "reads" is one it consumes through the run brief block, a typed override or the style snapshot.
/// Static and pure; it grows by adding rows as step types learn to read more of the brief.
/// </summary>
public static class BriefFieldConsumers
{
    public const string Language = "language";
    public const string SourceLanguage = "sourceLanguage";
    public const string LengthSec = "lengthSec";
    public const string Format = "format";
    public const string Resolution = "resolution";
    public const string StylePreset = "stylePreset";
    public const string Tone = "tone";
    public const string DesignSystem = "designSystem";
    public const string Voice = "voice";
    public const string OverlayCardAudio = "overlayCardAudio";
    public const string Pace = "pace";

    /// <summary>The run's avatar (avatars phase 2), read only by a <c>VideoGenerate</c> step.</summary>
    public const string Avatar = "avatar";

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    // As built, the agent rows of the engine's RunBriefRegistry (ReelBolt.WorkflowEngine/Services/Briefs) decide which
    // run-brief fields an agent is told (language, source language, length, voice); BriefFieldConsumersRegistryTests
    // ties this table to it. The style fields (tone, preset, format, resolution, design system) reach agents through
    // the style snapshot and the typed overrides, not the registry, and keep their own rows here.
    private static readonly string[] CutLookFields = [Tone, Format, Resolution, StylePreset];

    private static IReadOnlySet<string> Of(string[] registry, params string[] style) => Set([.. registry, .. style]);

    // Scriptwriter: language, length and voice.
    private static readonly IReadOnlySet<string> Scriptwriter = Of([Language, LengthSec, Voice], Tone, Format, Resolution, StylePreset, DesignSystem);

    // Director and Author: language and length (no voice).
    private static readonly IReadOnlySet<string> Directing = Of([Language, LengthSec], Tone, Format, Resolution, StylePreset, DesignSystem);

    // Scene translation and animation strategy: no registry row; the look only.
    private static readonly IReadOnlySet<string> Scenes = Set(Tone, Format, Resolution, StylePreset, DesignSystem);

    // Narration writer: language, length and voice.
    private static readonly IReadOnlySet<string> Narration = Of([Language, LengthSec, Voice], Tone, StylePreset);

    // Narration translator: language and the source language.
    private static readonly IReadOnlySet<string> Translator = Of([Language, SourceLanguage], Tone, StylePreset);

    // Cutters (story editor, edit director, edit room): on-screen language and length.
    private static readonly IReadOnlySet<string> Cutter = Of([Language, LengthSec], CutLookFields);

    // Shot director: length.
    private static readonly IReadOnlySet<string> Shots = Of([LengthSec], CutLookFields);

    // On-screen text without a length (graphics planner and director, graphic designer, graphics room, composition reviser).
    private static readonly IReadOnlySet<string> OnScreen = Of([Language], CutLookFields);

    // Reviewers: language and length.
    private static readonly IReadOnlySet<string> Reviewer = Of([Language, LengthSec], CutLookFields);

    private static IReadOnlySet<string> Set(params string[] fields) => new HashSet<string>(fields, StringComparer.Ordinal);

    /// <summary>The brief fields one step reads.</summary>
    public static IReadOnlySet<string> FieldsReadBy(AppStepFacts step)
    {
        switch (step.StepType)
        {
            case StepType.EditRoom:
                return Cutter;
            case StepType.GraphicsRoom:
                return OnScreen;
            case StepType.ColorGradeRoom:
            case StepType.DailiesRoom:
                // The colour and dailies rooms read no run-brief field (no RunBriefRegistry row covers their agents).
                return None;
            case StepType.ReviewLoop:
                return Reviewer;
            case StepType.VideoAnalyze:
                return Set(SourceLanguage);
            case StepType.VideoCompile:
                // Brand overrides exist on the compile step (docs/design/workflow-apps.md 3.8), and the
                // compile binds the brief's card-sound choice (docs/design/picture-overlay.md 8.1).
                // The pace is read by every compile: the run's pace turns the dynamic edit on for a re-encoding compile that
                // did not set it itself (RunStyleOverrides), and a stream copy ignores it (docs/design/dynamic-edit.md).
                return Set(LengthSec, Tone, Format, Resolution, StylePreset, DesignSystem, OverlayCardAudio, Pace);
            case StepType.DesignSystem:
                // The design-system gate: the run's choice between the project's design and none.
                return Set(DesignSystem);
            case StepType.Voiceover:
                return Set(Language, Voice);
            case StepType.VideoGenerate:
                return Set(Format, Resolution, StylePreset, Avatar);
            case StepType.AudioGenerate:
            case StepType.ImageGenerate:
                return Set(Format, Resolution, StylePreset);
            case StepType.Agent:
                return step.AgentType switch
                {
                    AgentType.ScriptwriterAgent => Scriptwriter,
                    AgentType.DirectorAgent or AgentType.AuthorAgent => Directing,
                    AgentType.RemotionComponentTranslator or AgentType.AnimationStrategyAgent => Scenes,
                    AgentType.NarrationWriter => Narration,
                    AgentType.NarrationTranslator => Translator,
                    AgentType.VideoStoryEditor or AgentType.VideoEditDirector => Cutter,
                    AgentType.ShotDirector => Shots,
                    AgentType.MotionGraphicsPlanner or AgentType.MotionGraphicsDirector or AgentType.GraphicDesigner
                        or AgentType.CompositionReviser => OnScreen,
                    AgentType.VideoReviewAgent or AgentType.ReviewAgent => Reviewer,
                    _ => None
                };
            default:
                return None;
        }
    }

    /// <summary>The union of the brief fields any of the steps reads.</summary>
    public static IReadOnlySet<string> FieldsReadBy(IEnumerable<AppStepFacts> steps)
    {
        HashSet<string> all = new(StringComparer.Ordinal);
        foreach (AppStepFacts step in steps)
            all.UnionWith(FieldsReadBy(step));
        return all;
    }

    /// <summary>True when some step can take project files of this role from the run.</summary>
    public static bool AcceptsFileRole(string role, IEnumerable<AppStepFacts> steps)
    {
        foreach (AppStepFacts step in steps)
        {
            bool takes = role switch
            {
                "video" => step.StepType == StepType.VideoAnalyze && step.TakesProjectVideoFile,
                "code" => step.StepType == StepType.Agent
                    && step.AgentType is AgentType.CodeStructureAnalyzer or AgentType.DependencyAnalyzer
                        or AgentType.ComponentInventoryAnalyzer or AgentType.RouteAndApiAnalyzer or AgentType.StyleAndThemeExtractor,
                "music" or "sfx" => step.StepType == StepType.VideoCompile
                    || (step.StepType == StepType.Agent && step.AgentType is AgentType.MusicSupervisor or AgentType.SoundDesigner),
                "image" => step.StepType is StepType.PinInsert or StepType.ExtractFrame || step.TakesBriefImageFiles,
                "insertContent" => step.StepType == StepType.VideoCompile,
                "overlay" => step.StepType == StepType.VideoAnalyze && step.TakesOverlayFile,
                "document" => step.StepType == StepType.Agent && step.AgentType == AgentType.FileSummarizerAgent,
                _ => false
            };
            if (takes)
                return true;
        }

        return false;
    }
}
