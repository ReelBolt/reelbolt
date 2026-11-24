using System.Globalization;
using System.Text;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Styles;

namespace ReelBolt.WorkflowEngine.Services.Briefs;

/// <summary>
/// The typed brief of the execution currently running on this async flow (docs/run-brief.md): the
/// stored fields plus the one language the run uses. <see cref="Language"/> is the brief's language,
/// or, when the brief sets none and a design is pinned, that design's <c>voice.locale</c>; the brief
/// always wins, and a style-carried language yields to both (see <see cref="RunStyleState.From"/>).
/// </summary>
public sealed class RunBriefState
{
    private readonly Dictionary<AgentType, string?> _blocks = new();
    private readonly object _gate = new();

    public RunBriefState(StoredRunBrief brief, string? designLocale = null, string? styleLanguage = null)
    {
        Brief = brief;
        BriefLanguage = RunBriefLanguages.Normalize(brief.Language);
        StyleLanguage = RunBriefLanguages.Normalize(styleLanguage);
        DesignLocale = RunBriefLanguages.Normalize(designLocale);
        // brief > style > design (spec 3.2): the design's locale is only the default when nothing else says.
        Language = BriefLanguage ?? StyleLanguage ?? DesignLocale;
        SourceLanguage = RunBriefLanguages.Normalize(brief.SourceLanguage);
        LengthSec = brief.LengthSec is >= 1 ? brief.LengthSec : null;
        Voice = brief.Voice;
    }

    public StoredRunBrief Brief { get; }

    /// <summary>The validated language the brief itself set, or null.</summary>
    public string? BriefLanguage { get; }

    /// <summary>The validated language the run's style carried, or null.</summary>
    public string? StyleLanguage { get; }

    /// <summary>True when <see cref="Language"/> is a default (from the style or the design), not the person's brief.</summary>
    public bool LanguageIsDefault => BriefLanguage is null && Language is not null;

    /// <summary>The pinned design's validated <c>voice.locale</c>, or null.</summary>
    public string? DesignLocale { get; }

    /// <summary>The language the run uses: the brief's, else the pinned design's locale, else null.</summary>
    public string? Language { get; }

    /// <summary>The validated spoken language of the source footage, or null.</summary>
    public string? SourceLanguage { get; }

    public int? LengthSec { get; }

    public RunBriefVoice? Voice { get; }

    /// <summary>The files the brief named for <paramref name="role"/>, in order.</summary>
    public IReadOnlyList<Guid> FilesFor(RunBriefFileRole role) =>
        Brief.SourceFiles?.Where(f => f.Role == role).SelectMany(f => f.FileIds ?? []).Distinct().ToList() ?? [];

    private RunBriefFacts Facts()
    {
        (string Name, string Label)? language = RunBriefLanguages.Describe(Language);
        (string Name, string Label)? source = RunBriefLanguages.Describe(SourceLanguage);
        return new RunBriefFacts(language?.Name, language?.Label, source?.Name, source?.Label, LengthSec, Voice?.Mode, LanguageIsDefault);
    }

    /// <summary>The <c>[RUN BRIEF]</c> block for <paramref name="agentType"/>, computed once per run; null when nothing applies.</summary>
    public string? BlockFor(AgentType agentType)
    {
        lock (_gate)
        {
            if (!_blocks.TryGetValue(agentType, out string? block))
            {
                block = RunBriefRegistry.BuildBlock(agentType, Facts());
                _blocks[agentType] = block;
            }

            return block;
        }
    }

    /// <summary>
    /// The step-cache component: a canonical string of ONLY the brief fields this step reads, or null
    /// when it reads none that are set (a null component is not hashed, so a run with no brief keeps its key).
    /// </summary>
    public string? CacheKeyComponent(StepType stepType, AgentType? agentType, string? videoAnalyzeConfigJson = null, string? imageGenerateConfigJson = null)
    {
        var parts = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{name}={value.Length}:{value}"));
        }

        switch (stepType)
        {
            case StepType.Agent when agentType is { } agent:
                foreach (RunBriefField field in RunBriefRegistry.FieldsRead(agent))
                    AddField(field, Add);
                // The pace reaches the editors through their style nudge, so it keys their output (only when set).
                if (agent is AgentType.VideoStoryEditor or AgentType.VideoEditDirector)
                    Add("pace", Brief.Pace);
                break;
            case StepType.EditRoom:
            case StepType.GraphicsRoom:
            case StepType.ColorGradeRoom:
            case StepType.DailiesRoom:
                // Only what the room's built-in participants read per the registry (a grade or dailies room reads none).
                foreach (RunBriefField field in RunBriefRegistry.RoomAgents(stepType).SelectMany(RunBriefRegistry.FieldsRead).Distinct())
                    AddField(field, Add);
                if (stepType == StepType.EditRoom)
                    Add("pace", Brief.Pace);
                break;
            case StepType.Voiceover:
                AddField(RunBriefField.Language, Add);
                Add("voice", VoiceKey());
                break;
            case StepType.VideoCompile:
                AddField(RunBriefField.Length, Add);
                // A binding card-sound preference changes what the compile renders; "agent" leaves
                // the planner's per-card choice alone, so it is not keyed (a run without the field
                // keeps its exact key).
                if (Brief.OverlayCardAudio is { } cardAudio
                    && !string.Equals(cardAudio, "agent", StringComparison.OrdinalIgnoreCase))
                {
                    Add("overlayCardAudio", cardAudio);
                }

                // The pace picks the dynamic edit's busyness; keyed only when set.
                if (Brief.Pace is { Length: > 0 } pace)
                    Add("pace", pace);

                if (FilesFor(RunBriefFileRole.InsertContent) is { Count: > 0 } insertFiles)
                    Add("files." + RunBriefFileRole.InsertContent, string.Join(",", insertFiles.Select(i => i.ToString("N"))));
                break;
            case StepType.VideoGenerate:
                // The run's avatar (only a step that names none and has no frame reads it; the avatar's own consent
                // and reference digests join the key separately, as AvatarConsent).
                Add("avatar", Brief.Avatar?.AvatarId?.ToString("N"));
                break;
            case StepType.ImageGenerate:
                // An Edit with Source=BriefFiles edits the run's picture files: a different chosen set is a different key.
                if (ImageBriefFileRole(imageGenerateConfigJson) is { } imageRole && FilesFor(imageRole) is { Count: > 0 } imageFiles)
                    Add("files." + imageRole, string.Join(",", imageFiles.Select(i => i.ToString("N"))));
                break;
            case StepType.VideoAnalyze:
                AddField(RunBriefField.SourceLanguage, Add);
                foreach (RunBriefFileRole role in BriefFileRolesOf(videoAnalyzeConfigJson))
                {
                    IReadOnlyList<Guid> ids = FilesFor(role);
                    if (ids.Count > 0)
                        Add("files." + role, string.Join(",", ids.Select(i => i.ToString("N"))));
                }

                // Pictures/videos over the film: a flag-on analysis offers the brief's overlay
                // files, so a different named set must produce a different cache key.
                if (UsesOverlayOffers(videoAnalyzeConfigJson)
                    && FilesFor(RunBriefFileRole.Overlay) is { Count: > 0 } overlayFiles)
                {
                    Add("files." + RunBriefFileRole.Overlay, string.Join(",", overlayFiles.Select(i => i.ToString("N"))));
                }

                break;
        }

        return parts.Count == 0 ? null : string.Join("|", parts);
    }

    /// <summary>The role an ImageGenerate Edit reads its pictures from (Source=BriefFiles), or null. An unreadable config reads the Image role, so a chosen file is never missing from the key.</summary>
    private static RunBriefFileRole? ImageBriefFileRole(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return null;
        try
        {
            ImageGenerateStepConfig? config = System.Text.Json.JsonSerializer.Deserialize<ImageGenerateStepConfig>(configJson, ImageGenerateStepConfig.JsonOptions);
            return config is null ? null : config.UsesBriefFiles ? config.Source!.Role ?? RunBriefFileRole.Image : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return RunBriefFileRole.Image;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions ConfigJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// True when a VideoAnalyze config's <c>emitOverlayOffers</c> is true, read through the SAME
    /// typed deserialization the executor uses (so a duplicate key is last-wins, not first-wins).
    /// An unparseable config is treated as offering overlays: the key must never under-include.
    /// </summary>
    private static bool UsesOverlayOffers(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return false;
        try
        {
            VideoAnalyzeStepConfig? config =
                System.Text.Json.JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(configJson, ConfigJsonOptions);
            return config is null || config.EmitOverlayOffers;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// The roles of the <see cref="VideoSourceKind.BriefFiles"/> sources in a VideoAnalyze config, read the way the
    /// executor reads it (enum names AND numbers). A config that does not parse is treated as reading every role,
    /// so a chosen file can never be missing from the key.
    /// </summary>
    private static IEnumerable<RunBriefFileRole> BriefFileRolesOf(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return [];
        try
        {
            VideoAnalyzeStepConfig? config = System.Text.Json.JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(configJson, ConfigJsonOptions);
            if (config is null)
                return [];
            IEnumerable<VideoSourceRef> sources = config.Sources is { Count: > 0 } ? config.Sources : [config.Source];
            // An open ProjectFile slot reads the brief's video files too (RunBriefOverrides.ApplyToAnalyzeSources).
            return sources.Where(x => x is { Kind: VideoSourceKind.BriefFiles, Role: not null }).Select(x => x.Role!.Value)
                .Concat(sources.Any(RunBriefOverrides.IsOpenVideoSlot) ? [RunBriefFileRole.Video] : []).Distinct().ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return Enum.GetValues<RunBriefFileRole>();
        }
    }

    private void AddField(RunBriefField field, Action<string, string?> add)
    {
        switch (field)
        {
            case RunBriefField.Language: add("language", Language); break;
            case RunBriefField.SourceLanguage: add("sourceLanguage", SourceLanguage); break;
            case RunBriefField.Length: add("lengthSec", LengthSec?.ToString(CultureInfo.InvariantCulture)); break;
            case RunBriefField.Voice: add("voice", VoiceKey()); break;
        }
    }

    private string? VoiceKey()
    {
        if (Voice is null)
            return null;
        var sb = new StringBuilder();
        sb.Append(Voice.Mode?.ToString() ?? "-");
        if (Voice.ProjectVoiceId is { } p)
            sb.Append(":p").Append(p.ToString("N"));
        if (Voice.LibraryVoiceId is { } l)
            sb.Append(":l").Append(l.ToString("N"));
        return sb.ToString();
    }

    /// <summary>
    /// The state for <paramref name="execution"/>, or null when it has no brief field and the pinned design
    /// offers no locale. <paramref name="designLocale"/> is the pinned design's <c>voice.locale</c>.
    /// </summary>
    public static RunBriefState? From(WorkflowExecution execution, string? designLocale = null)
    {
        StoredRunBrief? stored = RunBriefJson.TryParse(execution.RunBriefJson);
        if (stored is { IsEmpty: true })
            stored = null;
        string? locale = RunBriefLanguages.Normalize(designLocale);
        string? styleLanguage = StyleJson.TryParseSnapshot(execution.StyleConfigJson)?.Config.Language;
        if (stored is null && locale is null && RunBriefLanguages.Normalize(styleLanguage) is null)
            return null;
        return new RunBriefState(stored ?? new StoredRunBrief(), locale, styleLanguage);
    }
}

/// <summary>
/// Carries the running execution's <see cref="RunBriefState"/> to the agents and deterministic steps it
/// runs, exactly like <see cref="RunStyleAmbient"/>: agents are singletons shared by every concurrent
/// execution, so the brief cannot live on the agent. <see cref="Enter"/> is called once at the top of
/// <c>WorkflowExecutorService.ExecuteAsync</c> and the value flows down every <c>await</c> of that
/// execution, including the background tasks a room or a parallel step starts, and no further.
/// </summary>
public static class RunBriefAmbient
{
    private static readonly AsyncLocal<RunBriefState?> CurrentState = new();

    public static RunBriefState? Current => CurrentState.Value;

    public static IDisposable Enter(RunBriefState? state)
    {
        RunBriefState? previous = CurrentState.Value;
        CurrentState.Value = state;
        return new Restore(previous);
    }

    /// <summary>
    /// <paramref name="systemPrompt"/> plus the run's <c>[RUN BRIEF]</c> block for <paramref name="agentType"/>;
    /// the same string instance when the run has no brief or no sentence applies to this agent.
    /// </summary>
    public static string Decorate(string systemPrompt, AgentType agentType) =>
        CurrentState.Value is { } state
            ? StyleNudgeService.Append(systemPrompt, state.BlockFor(agentType))
            : systemPrompt;

    private sealed class Restore(RunBriefState? previous) : IDisposable
    {
        public void Dispose() => CurrentState.Value = previous;
    }
}
