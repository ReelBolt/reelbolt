using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Briefs;

/// <summary>
/// The brief's typed overrides of deterministic step configs (docs/run-brief.md), the analogue of
/// <see cref="Styles.RunStyleOverrides"/>. One call per executor; each returns the SAME instance when
/// nothing applies, so a run without the relevant brief field is untouched. A run field overrides the
/// property it is bound to and nothing else; the property is listed beside each method.
/// </summary>
public static class RunBriefOverrides
{
    /// <summary>
    /// Voiceover: <c>language</c> fills <see cref="VoiceoverStepConfig.Language"/> when the step has none;
    /// <c>voice.projectVoiceId</c> / <c>voice.libraryVoiceId</c> replace <see cref="VoiceoverStepConfig.ProjectVoiceId"/>
    /// (both are project-voice rows, resolved and authorized by the consent guard, never trusted here).
    /// </summary>
    public static VoiceoverStepConfig ApplyToVoiceover(VoiceoverStepConfig config, RunBriefState? brief)
    {
        if (brief is null)
            return config;

        VoiceoverStepConfig result = config;
        if (string.IsNullOrWhiteSpace(config.Language) && brief.Language is { } language)
            result = result with { Language = language };

        Guid? voiceId = VoiceIdOf(brief.Voice);
        if (voiceId is { } id && config.ProjectVoiceId != id)
            result = result with { ProjectVoiceId = id };

        return result;
    }

    /// <summary>The explicit voice the brief chose, or null.</summary>
    public static Guid? VoiceIdOf(RunBriefVoice? voice) => voice?.ProjectVoiceId ?? voice?.LibraryVoiceId;

    /// <summary>True when the brief says the film is not narrated (<c>voice.mode: none</c>).</summary>
    public static bool IsSilent(RunBriefState? brief) => brief?.Voice?.Mode == RunBriefVoiceMode.None;

    /// <summary>VideoCompile: <c>lengthSec</c> fills <see cref="VideoCompileStepConfig.TargetDurationSec"/> when it is 0; the mode stays as authored.</summary>
    public static VideoCompileStepConfig ApplyToCompile(VideoCompileStepConfig config, RunBriefState? brief)
    {
        VideoCompileStepConfig result = brief?.LengthSec is { } length && config.TargetDurationSec == 0
            ? config with { TargetDurationSec = length }
            : config;

        // The file the person chose for "what goes on the screen" becomes the insert content when the step names
        // none of its own (a file of the run's project; the compile re-checks it against the project file list).
        if (brief is not null
            && result.InsertContentProjectFileId is null && result.InsertContentRef is null
            && result.InsertContents is not { Count: > 0 }
            && brief.FilesFor(RunBriefFileRole.InsertContent) is { Count: > 0 } files)
        {
            result = result with { InsertContentProjectFileId = files[0] };
        }

        // The brief's card-sound choice binds every full-frame picture; "agent" leaves the planner's choice alone.
        // The overlay file role itself writes no config: those files feed the analyze step's media offers.
        if (brief?.Brief.OverlayCardAudio is { } cardAudio)
        {
            MediaOverlayCardAudio? choice = cardAudio.Trim().ToLowerInvariant() switch
            {
                RunBrief.OverlayCardAudioKeep => MediaOverlayCardAudio.Keep,
                RunBrief.OverlayCardAudioMute => MediaOverlayCardAudio.Mute,
                _ => null
            };
            if (choice is { } audio && result.MediaOverlayCardAudioDefault != audio)
                result = result with { MediaOverlayCardAudioDefault = audio };
        }

        return result;
    }

    /// <summary>VideoAnalyze: <c>sourceLanguage</c> becomes the transcription language hint (primary subtag) when the step has none.</summary>
    /// <summary>
    /// The brief's avatar (avatars phase 2) on a <c>VideoGenerate</c> config: set only on an inline step that names no
    /// avatar and no first/last frame, so the brief never swaps the person a step was built to show and never mixes
    /// frame and reference mode. It never touches <c>AllowSourceMediaEgress</c>: a step whose permission is off still
    /// fails <c>EGRESS_REFUSED</c>. Mutates and returns the executor's own freshly deserialized instance.
    /// </summary>
    public static VideoGenerateStepConfig ApplyToVideoGenerate(VideoGenerateStepConfig config, RunBriefState? brief)
    {
        if (brief?.Brief.Avatar?.AvatarId is Guid avatarId && avatarId != Guid.Empty
            && !config.HasAvatar && !config.HasFrameInputs && config.PlanSource == VideoGeneratePlanSource.Inline)
        {
            config.AvatarId = avatarId;
        }

        return config;
    }

    public static VideoAnalyzeStepConfig ApplyToAnalyze(VideoAnalyzeStepConfig config, RunBriefState? brief) =>
        brief?.SourceLanguage is { } source && string.IsNullOrWhiteSpace(config.Language)
            && RunBriefLanguages.Primary(source) is { } primary
            ? config with { Language = primary }
            : config;

    /// <summary>True for a ProjectFile source with no pinned file id: the open "no file selected" slot the run fills.</summary>
    public static bool IsOpenVideoSlot(VideoSourceRef source) =>
        source is { Kind: VideoSourceKind.ProjectFile, ProjectFileId: null };

    /// <summary>
    /// VideoAnalyze sources: when the brief carries files for the video role, the step's open ProjectFile source (no pinned
    /// file id) becomes a <see cref="VideoSourceKind.BriefFiles"/> source for that role, so exactly those files are analyzed
    /// (re-checked against the project by <c>BriefFilesSourceResolver</c>). Further open slots are dropped so the files are not
    /// read twice; pinned files and every other kind are untouched. The SAME list is returned when nothing applies.
    /// </summary>
    public static IReadOnlyList<VideoSourceRef> ApplyToAnalyzeSources(IReadOnlyList<VideoSourceRef> sources, RunBriefState? brief)
    {
        if (brief is null || brief.FilesFor(RunBriefFileRole.Video).Count == 0 || !sources.Any(IsOpenVideoSlot))
            return sources;

        var result = new List<VideoSourceRef>(sources.Count);
        bool replaced = false;
        foreach (VideoSourceRef source in sources)
        {
            if (!IsOpenVideoSlot(source))
            {
                result.Add(source);
            }
            else if (!replaced)
            {
                replaced = true;
                result.Add(new VideoSourceRef(VideoSourceKind.BriefFiles, Role: RunBriefFileRole.Video, InSec: source.InSec, OutSec: source.OutSec));
            }
        }

        return result;
    }
}
