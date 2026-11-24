using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Workflows.ChangeRequests;

/// <summary>What a step's items are (docs/design/change-requests.md "Capability registry").</summary>
public enum StepItemKind
{
    None,
    Line,
    Shot,
    Take,
    Scene
}

/// <summary>The value type of one override key.</summary>
public enum ChangeOverrideType
{
    Text,
    Number,
    PronunciationList,
    /// <summary>A JSON boolean (<c>true</c> switches the behaviour on).</summary>
    Flag,
    /// <summary>One of <see cref="ChangeOverrideSpec.Values"/>, by name (case-insensitive).</summary>
    Enum
}

/// <summary>One entry of a step type's closed override catalogue.</summary>
/// <param name="Key">The override key.</param>
/// <param name="Type">The JSON value type it takes.</param>
/// <param name="Min">Inclusive lower bound for a number.</param>
/// <param name="Max">Inclusive upper bound for a number.</param>
/// <param name="MaxLength">Longest accepted text (characters).</param>
/// <param name="MaxEntries">Most entries accepted in a list.</param>
/// <param name="Values">The accepted names of an <see cref="ChangeOverrideType.Enum"/>.</param>
/// <param name="MultiLine">A <see cref="ChangeOverrideType.Text"/> that may carry line breaks, or be given as a list of lines (at most <paramref name="MaxEntries"/>).</param>
public sealed record ChangeOverrideSpec(
    string Key,
    ChangeOverrideType Type,
    double? Min = null,
    double? Max = null,
    int? MaxLength = null,
    int? MaxEntries = null,
    IReadOnlyList<string>? Values = null,
    bool MultiLine = false);

/// <summary>One changeable item of a finished step, read from its stored output.</summary>
public sealed record StepItem(string Id, string? Status, double? DurationSec, string? Text);

/// <summary>
/// Whether a step type accepts change requests, and what a request may carry.
/// </summary>
/// <param name="Supported">True when the engine has an executor that applies a request for this type.</param>
/// <param name="NotSupportedReason">Why not; non-empty whenever <paramref name="Supported"/> is false.</param>
/// <param name="ItemKind">What the step's items are.</param>
/// <param name="Overrides">The closed override catalogue.</param>
/// <param name="AllowsPlainRetry">True when a request with nothing in it (re-run the step as is) is meaningful.</param>
/// <param name="Route">Set when the change is made through another route (e.g. <c>composition-revision</c>).</param>
/// <param name="CostNote">A short key for a standing cost warning the form and the approval card show (<c>script</c>: everything after the script is made again).</param>
/// <param name="AcceptsInstructionWithItems">True when an instruction naming at least one item is a request by itself (the model rewrites those items).</param>
public sealed record StepChangeCapability(
    bool Supported,
    string? NotSupportedReason,
    StepItemKind ItemKind,
    IReadOnlyList<ChangeOverrideSpec> Overrides,
    bool AllowsPlainRetry,
    string? Route = null,
    string? CostNote = null,
    bool AcceptsInstructionWithItems = false)
{
    public static StepChangeCapability NotSupported(string reason, string? route = null) =>
        new(false, reason, StepItemKind.None, [], false, route);
}

/// <summary>
/// The one pure registry of which step types accept a change request (docs/design/change-requests.md).
/// Every <see cref="StepType"/> has an explicit arm; the drift guard in the test project fails the build
/// when one is missing, empty-reasoned, or Supported without an executor.
/// </summary>
public static class StepChangeCapabilities
{
    public const string CompositionRevisionRoute = "composition-revision";
    public const int MaxItems = 64;
    public const int MaxInstructionLength = 2000;
    public const int MaxPronunciationEntries = 16;
    public const int MaxTextLength = 1000;
    public const int MaxVoiceIdLength = 128;
    public const int MaxPromptLength = 2000;
    public const int MaxAudioPromptLength = 4000;
    public const int MaxItemTextLength = 200;
    /// <summary>Longest overlay text a thumbnail request may carry (the config's own cap is 400).</summary>
    public const int MaxOverlayTextLength = 400;
    /// <summary>The one item id of a thumbnail step.</summary>
    public const string ThumbnailItemId = "thumbnail";

    /// <summary>The value of <see cref="StepChangeCapability.CostNote"/> on the script.</summary>
    public const string ScriptCostNote = "script";
    /// <summary>Longest on-screen text (all lines together) one scene override may carry.</summary>
    public const int MaxOnScreenTextLength = 600;
    /// <summary>Most on-screen lines one scene override may carry.</summary>
    public const int MaxOnScreenTextLines = 6;
    /// <summary>The item id of scene <paramref name="n"/> (1-based) when the script did not number it itself.</summary>
    public static string SceneId(int n) => $"sc{n}";

    private const string NotBuilt = "not built yet";
    private const string Routing = "a routing step: its change is a change to what feeds it";
    private const string Bookkeeping = "a bookkeeping step with no output a person would ask to change";

    private static readonly StepChangeCapability Voiceover = new(
        true,
        null,
        StepItemKind.Line,
        [
            new ChangeOverrideSpec("text", ChangeOverrideType.Text, MaxLength: MaxTextLength),
            new ChangeOverrideSpec("voiceId", ChangeOverrideType.Text, MaxLength: MaxVoiceIdLength),
            new ChangeOverrideSpec("speed", ChangeOverrideType.Number, Min: 0.5, Max: 2.0),
            new ChangeOverrideSpec("pronunciation", ChangeOverrideType.PronunciationList, MaxEntries: MaxPronunciationEntries)
        ],
        false);

    private static readonly StepChangeCapability VideoGenerate = new(
        true,
        null,
        StepItemKind.Shot,
        [
            new ChangeOverrideSpec("prompt", ChangeOverrideType.Text, MaxLength: MaxPromptLength),
            new ChangeOverrideSpec("retake", ChangeOverrideType.Flag)
        ],
        false);

    // Audio: the duration range is the catalogue's widest across both kinds; the chosen model's own range is checked at run time.
    private static readonly (double Min, double Max) AudioRange = (
        Math.Min(AudioGenerateStepConfig.CatalogueRange(AudioGenerationKind.Music).Min, AudioGenerateStepConfig.CatalogueRange(AudioGenerationKind.SoundEffect).Min),
        Math.Max(AudioGenerateStepConfig.CatalogueRange(AudioGenerationKind.Music).Max, AudioGenerateStepConfig.CatalogueRange(AudioGenerationKind.SoundEffect).Max));

    private static readonly StepChangeCapability AudioGenerate = new(
        true,
        null,
        StepItemKind.Take,
        [
            new ChangeOverrideSpec("prompt", ChangeOverrideType.Text, MaxLength: MaxAudioPromptLength),
            new ChangeOverrideSpec("durationSec", ChangeOverrideType.Number, Min: AudioRange.Min, Max: AudioRange.Max),
            new ChangeOverrideSpec("retake", ChangeOverrideType.Flag)
        ],
        false);

    private static readonly StepChangeCapability ThumbnailGenerate = new(
        true,
        null,
        StepItemKind.None,
        [
            new ChangeOverrideSpec("overlayText", ChangeOverrideType.Text, MaxLength: MaxOverlayTextLength),
            new ChangeOverrideSpec("atTimeSec", ChangeOverrideType.Number, Min: 0, Max: VideoSourceRef.MaxWindowSec),
            new ChangeOverrideSpec("overlayPosition", ChangeOverrideType.Enum, Values: [.. Enum.GetNames<ThumbnailOverlayPosition>()]),
            new ChangeOverrideSpec("overlayFontColor", ChangeOverrideType.Enum, Values: ["white", "black", "yellow"]),
            new ChangeOverrideSpec("overlayBoxColor", ChangeOverrideType.Enum, Values: ["black@0.45", "black@0.6", "white@0.4", "none"])
        ],
        false);

    private static readonly StepChangeCapability Script = new(
        true,
        null,
        StepItemKind.Scene,
        [
            new ChangeOverrideSpec("voiceover", ChangeOverrideType.Text, MaxLength: MaxTextLength),
            new ChangeOverrideSpec("onScreenText", ChangeOverrideType.Text, MaxLength: MaxOnScreenTextLength, MaxEntries: MaxOnScreenTextLines, MultiLine: true),
            new ChangeOverrideSpec("visualDescription", ChangeOverrideType.Text, MaxLength: MaxTextLength)
        ],
        false,
        CostNote: ScriptCostNote,
        AcceptsInstructionWithItems: true);

    private const string AnalysisAgent = "an analysis step: change what feeds it";
    private const string PlanningAgent = "a planning decision: change the script or footage it reads";
    private const string FilmAgent = "the film is changed through a composition revision";

    /// <summary>
    /// The capability of an <see cref="StepType.Agent"/> step by its agent type. Every <see cref="AgentType"/> has an
    /// explicit arm (no default), so a new agent type fails the drift guard until it is decided here. Only the built-in
    /// scriptwriter is supported; the agents that author or revise the film route to a composition revision.
    /// </summary>
    public static StepChangeCapability ForAgent(AgentType agent) => agent switch
    {
        AgentType.ScriptwriterAgent => Script,
        AgentType.AuthorAgent => StepChangeCapability.NotSupported(FilmAgent, CompositionRevisionRoute),
        AgentType.CompositionReviser => StepChangeCapability.NotSupported(FilmAgent, CompositionRevisionRoute),
        AgentType.RemotionComponentTranslator => StepChangeCapability.NotSupported(FilmAgent, CompositionRevisionRoute),
        AgentType.GraphicDesigner => StepChangeCapability.NotSupported(FilmAgent, CompositionRevisionRoute),
        AgentType.CodeStructureAnalyzer => StepChangeCapability.NotSupported(AnalysisAgent),
        AgentType.DependencyAnalyzer => StepChangeCapability.NotSupported(AnalysisAgent),
        AgentType.ComponentInventoryAnalyzer => StepChangeCapability.NotSupported(AnalysisAgent),
        AgentType.RouteAndApiAnalyzer => StepChangeCapability.NotSupported(AnalysisAgent),
        AgentType.StyleAndThemeExtractor => StepChangeCapability.NotSupported(AnalysisAgent),
        AgentType.FileSummarizerAgent => StepChangeCapability.NotSupported(AnalysisAgent),
        AgentType.AnimationStrategyAgent => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.DirectorAgent => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.ReviewAgent => StepChangeCapability.NotSupported("a review verdict: change what it reviews"),
        AgentType.VideoReviewAgent => StepChangeCapability.NotSupported("a review verdict: change what it reviews"),
        AgentType.Custom => StepChangeCapability.NotSupported("a custom agent: its output has no known items to change"),
        AgentType.ExtractTransform => StepChangeCapability.NotSupported(Bookkeeping),
        AgentType.VideoTransform => StepChangeCapability.NotSupported("a deterministic video step: change what feeds it"),
        AgentType.VideoStoryEditor => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.MotionGraphicsPlanner => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.MusicSupervisor => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.VideoEditDirector => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.MotionGraphicsDirector => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.Colorist => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.ColorGradeDirector => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.SoundDesigner => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.Assistant => StepChangeCapability.NotSupported("the conversational assistant is not a workflow output"),
        AgentType.ShotDirector => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.NarrationWriter => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.PickupPlanner => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.NarrationTranslator => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.TakeSelector => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.DailiesDirector => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.OverlayPlanner => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.MotionDirector => StepChangeCapability.NotSupported(PlanningAgent),
        AgentType.DynamicsPlanner => StepChangeCapability.NotSupported(PlanningAgent)
        // No default arm on purpose: an agent type added without a deliberate entry here throws, and the drift guard fails.
    };

    /// <summary>
    /// The capability of a step of <paramref name="type"/> hosting <paramref name="agent"/>: the agent type's own for an
    /// <see cref="StepType.Agent"/> step whose agent is known, <see cref="For(StepType)"/> otherwise.
    /// </summary>
    public static StepChangeCapability For(StepType type, AgentType? agent) =>
        type == StepType.Agent && agent is { } a ? ForAgent(a) : For(type);

    /// <summary>The capability of <paramref name="type"/>. Every member has an explicit arm (there is no fallback, so an undeclared step type fails the drift guard).</summary>
    public static StepChangeCapability For(StepType type) => type switch
    {
        StepType.Voiceover => Voiceover,
        StepType.Revise => StepChangeCapability.NotSupported(
            "the film is changed through a composition revision", CompositionRevisionRoute),
        StepType.VideoGenerate => VideoGenerate,
        StepType.AudioGenerate => AudioGenerate,
        StepType.ThumbnailGenerate => ThumbnailGenerate,
        StepType.Agent => StepChangeCapability.NotSupported(NotBuilt),
        StepType.Conditional => StepChangeCapability.NotSupported(Routing),
        StepType.ForEach => StepChangeCapability.NotSupported(Routing),
        StepType.Parallel => StepChangeCapability.NotSupported(Routing),
        StepType.ReviewLoop => StepChangeCapability.NotSupported(Routing),
        StepType.Extract => StepChangeCapability.NotSupported(Bookkeeping),
        StepType.ExtractFrame => StepChangeCapability.NotSupported(Bookkeeping),
        StepType.PinInsert => StepChangeCapability.NotSupported(Bookkeeping),
        StepType.AudioCleanup => StepChangeCapability.NotSupported(Bookkeeping),
        StepType.VideoEnhance => StepChangeCapability.NotSupported(Bookkeeping),
        StepType.ImageGenerate => StepChangeCapability.NotSupported("picture changes are not offered as a step change request yet: edit the step's prompt or settings and run it again"),
        StepType.DesignSystem => StepChangeCapability.NotSupported(Bookkeeping),
        StepType.VideoAnalyze => StepChangeCapability.NotSupported("a deterministic analysis step: change what feeds it"),
        StepType.VideoCompile => StepChangeCapability.NotSupported("a deterministic compile step: change the decisions or audio it reads"),
        StepType.EditRoom => StepChangeCapability.NotSupported(NotBuilt),
        StepType.GraphicsRoom => StepChangeCapability.NotSupported(NotBuilt),
        StepType.ColorGradeRoom => StepChangeCapability.NotSupported(NotBuilt),
        StepType.DailiesRoom => StepChangeCapability.NotSupported(NotBuilt),
        StepType.DesignGraphic => StepChangeCapability.NotSupported(NotBuilt),
        StepType.TimelineRender => StepChangeCapability.NotSupported("the timeline is changed in the editor")
        // No default arm on purpose: a step type added without a deliberate entry here throws, and the drift guard fails.
    };

    /// <summary>
    /// The changeable items of a finished step, from its stored output. Pure and lenient: unparseable or
    /// unexpected output yields an empty list, never an exception.
    /// </summary>
    public static IReadOnlyList<StepItem> ListItems(StepType type, string? outputJson) => ListItems(type, null, outputJson);

    /// <summary>
    /// <see cref="ListItems(StepType, string?)"/> that also knows the step's agent: the scenes of a scriptwriter
    /// step (<c>scenes[]</c> of its output; id = the scene's own <c>sceneId</c>, else <c>sc{n}</c>; text = the voiceover, shortened).
    /// </summary>
    public static IReadOnlyList<StepItem> ListItems(StepType type, AgentType? agent, string? outputJson)
    {
        if (type == StepType.Agent)
        {
            return agent == AgentType.ScriptwriterAgent
                ? [.. ScriptScenes.Read(outputJson).Select(sc => new StepItem(sc.Id, null, sc.DurationSec, Shorten(sc.Voiceover)))]
                : [];
        }

        if (string.IsNullOrWhiteSpace(outputJson) || type is not (StepType.Voiceover or StepType.VideoGenerate or StepType.AudioGenerate or StepType.ThumbnailGenerate))
            return [];
        if (type == StepType.VideoGenerate)
            return ListClipItems(outputJson);
        try
        {
            if (type == StepType.AudioGenerate)
                return ListAudioTakes(outputJson);
            if (type == StepType.ThumbnailGenerate)
                return ListThumbnail(outputJson);
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("view", out JsonElement view)
                || view.ValueKind != JsonValueKind.Object
                || !view.TryGetProperty("lines", out JsonElement lines)
                || lines.ValueKind != JsonValueKind.Array)
                return [];

            List<StepItem> items = [];
            foreach (JsonElement line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object
                    || !line.TryGetProperty("id", out JsonElement id)
                    || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(id.GetString()))
                    continue;
                items.Add(new StepItem(id.GetString()!, ReadString(line, "status"), ReadNumber(line, "durationSec"), ReadString(line, "text")));
            }
            return items;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The shots (and, when a shot has several takes, each take) of a finished VideoGenerate step, from its
    /// output <c>clips[]</c>. A clip id is <c>v{n}</c> for a one-take shot and <c>v{n}t{k}</c> otherwise; the shot
    /// item <c>v{n}</c> is derived for a multi-take shot (status of the best take). Item text is the prompt used.
    /// </summary>
    private static IReadOnlyList<StepItem> ListClipItems(string outputJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("clips", out JsonElement clips)
                || clips.ValueKind != JsonValueKind.Array)
                return [];

            List<StepItem> items = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            // shot id -> its takes, in order, so the derived shot item can be emitted before them
            List<(string ShotId, List<StepItem> Takes)> groups = [];
            foreach (JsonElement clip in clips.EnumerateArray())
            {
                if (clip.ValueKind != JsonValueKind.Object
                    || !clip.TryGetProperty("clipId", out JsonElement id)
                    || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(id.GetString())
                    || !seen.Add(id.GetString()!))
                    continue;
                string clipId = id.GetString()!;
                StepItem item = new(clipId, ReadString(clip, "status"), ReadNumber(clip, "durationSeconds") ?? ReadNumber(clip, "durationSec"),
                    Shorten(ReadString(clip, "description") ?? ReadString(clip, "prompt")));
                string shotId = ShotIdOf(clipId);
                if (shotId == clipId)
                {
                    groups.Add((shotId, [item]));
                }
                else if (groups.Count > 0 && groups[^1].ShotId == shotId)
                {
                    groups[^1].Takes.Add(item);
                }
                else
                {
                    groups.Add((shotId, [item]));
                }
            }

            foreach ((string shotId, List<StepItem> takes) in groups)
            {
                if (takes.Count == 1 && takes[0].Id == shotId)
                {
                    items.Add(takes[0]);
                    continue;
                }
                StepItem best = takes.FirstOrDefault(t => t.Status == "succeeded") ?? takes[0];
                if (seen.Add(shotId))
                    items.Add(new StepItem(shotId, best.Status, best.DurationSec, best.Text));
                items.AddRange(takes);
            }
            return items;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The shot a clip id belongs to: <c>v2t3</c> -> <c>v2</c>; any other id is its own shot.</summary>
    public static string ShotIdOf(string clipId)
    {
        int t = clipId.IndexOf('t', StringComparison.Ordinal);
        if (t > 1 && clipId[0] == 'v' && clipId.AsSpan(1, t - 1).ToString().All(char.IsAsciiDigit) && t + 1 < clipId.Length
            && clipId.AsSpan(t + 1).ToString().All(char.IsAsciiDigit))
            return clipId[..t];
        return clipId;
    }

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string t = text.Trim();
        return t.Length <= MaxItemTextLength ? t : t[..MaxItemTextLength] + "...";
    }

    private static IReadOnlyList<StepItem> ListAudioTakes(string outputJson)
    {
        using JsonDocument doc = JsonDocument.Parse(outputJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("takes", out JsonElement takes)
            || takes.ValueKind != JsonValueKind.Array)
            return [];
        List<StepItem> items = [];
        foreach (JsonElement take in takes.EnumerateArray())
        {
            if (take.ValueKind != JsonValueKind.Object
                || !take.TryGetProperty("take", out JsonElement n)
                || n.ValueKind != JsonValueKind.Number
                || !n.TryGetInt32(out int k)
                || k < 1)
                continue;
            string? prompt = ReadString(take, "prompt");
            items.Add(new StepItem(AudioTakeId(k), ReadString(take, "status"), ReadNumber(take, "durationSec"),
                prompt is { Length: > 120 } ? prompt[..120] + "..." : prompt));
        }
        return items;
    }

    /// <summary>The item id of audio take <paramref name="take"/>: <c>a{k}</c>.</summary>
    public static string AudioTakeId(int take) => $"a{take}";

    private static IReadOnlyList<StepItem> ListThumbnail(string outputJson)
    {
        using JsonDocument doc = JsonDocument.Parse(outputJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || ReadString(doc.RootElement, "thumbnailStorageKey") is null)
            return [];
        return [new StepItem(ThumbnailItemId, ReadString(doc.RootElement, "status"), null, ReadString(doc.RootElement, "thumbnailFileName"))];
    }

    private static string? ReadString(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? ReadNumber(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d)
            ? d
            : null;
}
