using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;

namespace ReelBolt.WorkflowEngine.Services.Styles;

/// <summary>The creative role an agent plays, for choosing which style nudges it receives.</summary>
public enum StyleAgentRole
{
    /// <summary>Chooses what to keep: VideoStoryEditor, VideoEditDirector (and the edit room's seats).</summary>
    Editor,
    /// <summary>Plans on-screen graphics: MotionGraphicsPlanner, MotionGraphicsDirector.</summary>
    Graphics,
    /// <summary>Picks background music: MusicSupervisor.</summary>
    Music,
    /// <summary>Picks the colour look: Colorist, ColorGradeDirector.</summary>
    Color,
    /// <summary>Places sound effects: SoundDesigner.</summary>
    Sound,
    /// <summary>Plans generated shots: ShotDirector.</summary>
    Shots,
    /// <summary>Plans the beat-aligned dynamic-edit moves after generation: DynamicsPlanner.</summary>
    Dynamics,
    /// <summary>Writes or translates narration: NarrationWriter, NarrationTranslator.</summary>
    Narration,
    /// <summary>Writes the promo script: ScriptwriterAgent.</summary>
    Scriptwriter,
    /// <summary>Plans the promo's scenes: DirectorAgent.</summary>
    Director,
    /// <summary>Builds the promo composition: AuthorAgent.</summary>
    Author,
    /// <summary>Scores the compiled edit: VideoReviewAgent.</summary>
    Reviewer
}

/// <summary>
/// THE reviewable registry of style nudges: one short instruction per (style choice, agent role).
/// Every sentence an agent can receive because of a style lives in this file and nowhere else, so
/// a reviewer can read the whole surface in one place; the only user-written text that ever reaches
/// a prompt is a style's sanitized notes, which <see cref="StyleNudgeService"/> quotes separately.
///
/// <para>
/// Nudges are deliberately PREFERENCES phrased inside each agent's existing contract: none of them
/// asks for a timestamp, a number of seconds, a coordinate or a field the agent's output schema
/// does not have, and none overrides a hard rule of the agent's own prompt. A role absent from a
/// choice's map simply gets nothing for that choice.
/// </para>
/// </summary>
public static class StyleNudgeRegistry
{
    /// <summary>The role <paramref name="agentType"/> plays, or null for an agent styles do not touch.</summary>
    public static StyleAgentRole? RoleOf(AgentType agentType) => agentType switch
    {
        AgentType.VideoStoryEditor or AgentType.VideoEditDirector => StyleAgentRole.Editor,
        AgentType.MotionGraphicsPlanner or AgentType.MotionGraphicsDirector => StyleAgentRole.Graphics,
        AgentType.MusicSupervisor => StyleAgentRole.Music,
        AgentType.Colorist or AgentType.ColorGradeDirector => StyleAgentRole.Color,
        AgentType.SoundDesigner => StyleAgentRole.Sound,
        AgentType.ShotDirector => StyleAgentRole.Shots,
        AgentType.DynamicsPlanner => StyleAgentRole.Dynamics,
        AgentType.NarrationWriter or AgentType.NarrationTranslator => StyleAgentRole.Narration,
        AgentType.ScriptwriterAgent => StyleAgentRole.Scriptwriter,
        AgentType.DirectorAgent => StyleAgentRole.Director,
        AgentType.AuthorAgent or AgentType.CompositionReviser => StyleAgentRole.Author,
        AgentType.VideoReviewAgent => StyleAgentRole.Reviewer,
        _ => null
    };

    private static IReadOnlyDictionary<StyleAgentRole, string> Map(params (StyleAgentRole Role, string Text)[] entries) =>
        entries.ToDictionary(e => e.Role, e => e.Text);

    internal static readonly IReadOnlyDictionary<StylePacing, IReadOnlyDictionary<StyleAgentRole, string>> Pacing =
        new Dictionary<StylePacing, IReadOnlyDictionary<StyleAgentRole, string>>
        {
            [StylePacing.Slow] = Map(
                (StyleAgentRole.Editor, "Pacing: slow. Prefer longer, complete shots and let moments breathe; keep fewer, longer spans rather than many short ones."),
                (StyleAgentRole.Graphics, "Pacing: slow. Give each graphic room to be read; plan fewer overlays."),
                (StyleAgentRole.Sound, "Pacing: slow. Use few, subtle cues; silence is part of the style."),
                (StyleAgentRole.Shots, "Pacing: slow. Prefer calm camera words and the longer duration words."),
                (StyleAgentRole.Narration, "Pacing: slow. Write fewer, unhurried lines with room between them."),
                (StyleAgentRole.Scriptwriter, "Pacing: slow. Fewer scenes, each given time to land."),
                (StyleAgentRole.Director, "Pacing: slow. Fewer scenes with longer holds and gentle motion."),
                (StyleAgentRole.Author, "Pacing: slow. Use gentle, unhurried animation timing."),
                (StyleAgentRole.Reviewer, "Requested pacing: slow. Do not penalize long holds that serve the style.")),
            [StylePacing.Medium] = Map(
                (StyleAgentRole.Editor, "Pacing: medium. Keep a natural, conversational rhythm: trim dead air but let sentences finish."),
                (StyleAgentRole.Narration, "Pacing: medium. Write at a natural speaking rhythm."),
                (StyleAgentRole.Scriptwriter, "Pacing: medium. A steady rhythm, neither rushed nor slow."),
                (StyleAgentRole.Director, "Pacing: medium. A steady scene rhythm.")),
            [StylePacing.Fast] = Map(
                (StyleAgentRole.Editor, "Pacing: fast. Prefer short, high-energy spans; cut dead air, hesitations and slow build-ups aggressively, and keep only the strongest moment of each idea."),
                (StyleAgentRole.Graphics, "Pacing: fast. Keep overlay text very short so it reads at a glance."),
                (StyleAgentRole.Sound, "Pacing: fast. Accent cuts with crisp cues where they add energy."),
                (StyleAgentRole.Shots, "Pacing: fast. Prefer dynamic camera words and the shortest duration words."),
                (StyleAgentRole.Narration, "Pacing: fast. Write short, punchy lines; no filler words."),
                (StyleAgentRole.Scriptwriter, "Pacing: fast. Short scenes, one idea each, no warm-up."),
                (StyleAgentRole.Director, "Pacing: fast. Short scenes with energetic motion and quick transitions between them."),
                (StyleAgentRole.Author, "Pacing: fast. Use snappy animation timing; avoid slow fades."),
                (StyleAgentRole.Reviewer, "Requested pacing: fast. Treat lingering, slow passages as a weakness.")),
            [StylePacing.BeatSynced] = Map(
                (StyleAgentRole.Editor, "Pacing: beat-synced. Favour short, rhythmic spans of similar length that can sit on a musical beat; the music drives the edit more than the dialogue."),
                (StyleAgentRole.Music, "Pacing: beat-synced. Prefer a track with a clear, steady beat; the edit is cut to it."),
                (StyleAgentRole.Graphics, "Pacing: beat-synced. Keep overlays minimal and short; they should not fight the rhythm."),
                (StyleAgentRole.Sound, "Pacing: beat-synced. Use few cues; the music carries the rhythm."),
                (StyleAgentRole.Shots, "Pacing: beat-synced. Prefer short duration words and rhythmic camera motion."),
                (StyleAgentRole.Director, "Pacing: beat-synced. Plan scenes of even length that can land on a beat."),
                (StyleAgentRole.Reviewer, "Requested pacing: beat-synced. Judge rhythm and energy over completeness of speech."))
        };

    internal static readonly IReadOnlyDictionary<StyleStorytelling, IReadOnlyDictionary<StyleAgentRole, string>> Storytelling =
        new Dictionary<StyleStorytelling, IReadOnlyDictionary<StyleAgentRole, string>>
        {
            [StyleStorytelling.Chronological] = Map(
                (StyleAgentRole.Editor, "Story: chronological. Keep material in the order it happened; do not reorder ideas for effect."),
                (StyleAgentRole.Narration, "Story: chronological. Narrate events in the order they happen on screen."),
                (StyleAgentRole.Scriptwriter, "Story: chronological. Tell it in order, beginning to end."),
                (StyleAgentRole.Director, "Story: chronological. Order scenes as a simple timeline.")),
            [StyleStorytelling.HookFirst] = Map(
                (StyleAgentRole.Editor, "Story: hook first. Open on the most striking, attention-grabbing moment of the content itself — never an end card, logo or call to action, which stay at the end; context comes after the hook."),
                (StyleAgentRole.Graphics, "Story: hook first. Avoid an opening title that delays the hook."),
                (StyleAgentRole.Shots, "Story: hook first. A ColdOpen, when you plan one, should be the boldest image."),
                (StyleAgentRole.Narration, "Story: hook first. Open with a bold claim or question, then explain."),
                (StyleAgentRole.Scriptwriter, "Story: hook first. Open on the single most compelling idea, then explain."),
                (StyleAgentRole.Director, "Story: hook first. The first scene is the hook; save logos and context for later."),
                (StyleAgentRole.Reviewer, "Requested story: hook first. A slow or generic opening is a weakness.")),
            [StyleStorytelling.ProblemSolution] = Map(
                (StyleAgentRole.Editor, "Story: problem then solution. Keep the material that states the problem first, then the material that shows the answer, then the result."),
                (StyleAgentRole.Narration, "Story: problem then solution. Name the pain first, then the fix, then the outcome."),
                (StyleAgentRole.Scriptwriter, "Story: problem then solution. Problem, solution, proof, call to action."),
                (StyleAgentRole.Director, "Story: problem then solution. Sequence scenes as problem, solution, outcome.")),
            [StyleStorytelling.Montage] = Map(
                (StyleAgentRole.Editor, "Story: montage. Favour a collage of strong visual moments carried by mood; complete sentences matter less than striking images."),
                (StyleAgentRole.Music, "Story: montage. The music carries the piece; prefer a track with a clear arc."),
                (StyleAgentRole.Narration, "Story: montage. Use little narration, if any; let the pictures and music speak."),
                (StyleAgentRole.Scriptwriter, "Story: montage. Short evocative beats rather than a spoken argument."),
                (StyleAgentRole.Director, "Story: montage. A flowing sequence of visual moments rather than explanation."))
        };

    internal static readonly IReadOnlyDictionary<StyleEditing, IReadOnlyDictionary<StyleAgentRole, string>> Editing =
        new Dictionary<StyleEditing, IReadOnlyDictionary<StyleAgentRole, string>>
        {
            [StyleEditing.JumpCuts] = Map(
                (StyleAgentRole.Editor, "Editing: jump cuts. It is fine to keep several short spans from the same take back to back, removing pauses between them."),
                (StyleAgentRole.Director, "Editing: hard cuts between scenes, no long transitions."),
                (StyleAgentRole.Author, "Editing: hard cuts between scenes; avoid crossfades.")),
            [StyleEditing.SmoothTransitions] = Map(
                (StyleAgentRole.Editor, "Editing: smooth. Prefer spans that begin and end on calm, settled moments so joins feel gentle; avoid choppy runs of tiny spans."),
                (StyleAgentRole.Director, "Editing: smooth transitions between scenes (fades, dissolves)."),
                (StyleAgentRole.Author, "Editing: smooth transitions between scenes; prefer fades and dissolves over hard cuts.")),
            [StyleEditing.Mixed] = Map(
                (StyleAgentRole.Editor, "Editing: mixed. Cut tightly inside a section, and let the material breathe between sections."),
                (StyleAgentRole.Director, "Editing: hard cuts inside a section, gentler transitions between sections."))
        };

    internal static readonly IReadOnlyDictionary<StyleTone, IReadOnlyDictionary<StyleAgentRole, string>> Tone =
        new Dictionary<StyleTone, IReadOnlyDictionary<StyleAgentRole, string>>
        {
            [StyleTone.Calm] = AllCreative("Tone: calm and reassuring."),
            [StyleTone.Upbeat] = AllCreative("Tone: upbeat and friendly."),
            [StyleTone.Energetic] = AllCreative("Tone: energetic and bold."),
            [StyleTone.Serious] = AllCreative("Tone: serious and credible; avoid gimmicks."),
            [StyleTone.Playful] = AllCreative("Tone: playful and light."),
            [StyleTone.Inspirational] = AllCreative("Tone: inspirational and uplifting.")
        };

    internal static readonly IReadOnlyDictionary<StyleCaptions, IReadOnlyDictionary<StyleAgentRole, string>> Captions =
        new Dictionary<StyleCaptions, IReadOnlyDictionary<StyleAgentRole, string>>
        {
            [StyleCaptions.Clean] = Map(
                (StyleAgentRole.Graphics, "Captions: clean subtitles. Keep graphics away from the lower third where captions sit.")),
            [StyleCaptions.Karaoke] = Map(
                (StyleAgentRole.Graphics, "Captions: karaoke-style. Keep graphics away from the lower third where captions sit."),
                (StyleAgentRole.Narration, "Captions highlight each word as it is spoken: prefer clear, distinct words.")),
            [StyleCaptions.Punch] = Map(
                (StyleAgentRole.Graphics, "Captions: one big word at a time in the centre. Keep graphics out of the centre of the frame."),
                (StyleAgentRole.Narration, "Captions show one word at a time: prefer short, strong words.")),
            [StyleCaptions.Bold] = Map(
                (StyleAgentRole.Graphics, "Captions: big bold words a few at a time, around two-thirds of the way down a vertical frame. Keep graphics out of that band."),
                (StyleAgentRole.Narration, "Captions show a few big words at a time: write short, punchy phrases that read well in three-word bursts."))
        };

    internal static readonly IReadOnlyDictionary<StyleColor, IReadOnlyDictionary<StyleAgentRole, string>> Color =
        new Dictionary<StyleColor, IReadOnlyDictionary<StyleAgentRole, string>>
        {
            [StyleColor.Natural] = ColorMap("None", "natural, true-to-life colour"),
            [StyleColor.Warm] = ColorMap("Warm", "a warm colour look"),
            [StyleColor.Cool] = ColorMap("Cool", "a cool colour look"),
            [StyleColor.Filmic] = ColorMap("Filmic", "a filmic colour look"),
            [StyleColor.Vibrant] = ColorMap("Vibrant", "vivid, saturated colour"),
            [StyleColor.Muted] = ColorMap("Muted", "muted, desaturated colour"),
            [StyleColor.Mono] = ColorMap("Mono", "black and white")
        };

    internal static readonly IReadOnlyDictionary<StyleMusicEnergy, IReadOnlyDictionary<StyleAgentRole, string>> Music =
        new Dictionary<StyleMusicEnergy, IReadOnlyDictionary<StyleAgentRole, string>>
        {
            [StyleMusicEnergy.Low] = Map(
                (StyleAgentRole.Music, "Music energy: low. Prefer a calm, understated track and a soft Intensity word; when in doubt, no track."),
                (StyleAgentRole.Shots, "Music energy: low. Prefer calm imagery.")),
            [StyleMusicEnergy.Medium] = Map(
                (StyleAgentRole.Music, "Music energy: medium. Prefer a track with steady momentum and a balanced Intensity word.")),
            [StyleMusicEnergy.High] = Map(
                (StyleAgentRole.Music, "Music energy: high. Prefer an energetic, driving track and a strong Intensity word."),
                (StyleAgentRole.Sound, "Music energy: high. Keep cues punchy so they cut through the music."))
        };

    internal static readonly IReadOnlyDictionary<StyleGraphicsDensity, IReadOnlyDictionary<StyleAgentRole, string>> Graphics =
        new Dictionary<StyleGraphicsDensity, IReadOnlyDictionary<StyleAgentRole, string>>
        {
            [StyleGraphicsDensity.None] = Map(
                (StyleAgentRole.Graphics, "Graphics: none requested. An empty overlay plan is the expected answer unless something is essential to understanding."),
                (StyleAgentRole.Director, "Graphics: avoid on-screen text and titles."),
                (StyleAgentRole.Author, "Graphics: avoid on-screen text and titles.")),
            [StyleGraphicsDensity.Minimal] = Map(
                (StyleAgentRole.Graphics, "Graphics: minimal. Plan at most one or two essential overlays."),
                (StyleAgentRole.Director, "Graphics: minimal on-screen text."),
                (StyleAgentRole.Author, "Graphics: minimal on-screen text.")),
            [StyleGraphicsDensity.Moderate] = Map(
                (StyleAgentRole.Graphics, "Graphics: moderate. Use overlays where they genuinely help: names, key points, a closing title.")),
            [StyleGraphicsDensity.Rich] = Map(
                (StyleAgentRole.Graphics, "Graphics: rich. Use overlays generously for key points, labels and callouts, while keeping each one short."),
                (StyleAgentRole.Director, "Graphics: rich, animated on-screen text and callouts."),
                (StyleAgentRole.Author, "Graphics: rich, animated on-screen text and callouts."))
        };

    /// <summary>The Pacing editor line used instead of the slow one when a dynamics pace is set (the pace bound replaces "fewer, longer runs").</summary>
    internal const string SlowEditorPacingWithDynamics =
        "Pacing: slow. Prefer longer, complete shots and let moments breathe.";

    /// <summary>
    /// What the edit room adds for a run that has dynamics, to every seat's charter and to the director's synthesis request
    /// (the room charter says "no effect of any kind"; a run with dynamics overrides that for the catalogue moves). Absent from a
    /// run without dynamics, so its room prompts are byte-identical.
    /// </summary>
    internal const string RoomDynamicsWords =
        "Dynamic edit in this room: for this run the catalogue moves ARE allowed, which overrides the rule above that you cannot request an effect of any kind. " +
        "The catalogue is SplitOnBeat, PunchIn, PunchOut, BeatAlternate, StrobeCut, FlashFrame, SpeedRamp and WhipOnBar, in words and offered ids only, never a time or a number. " +
        "The director's final decision lists the room's agreed moves in its dynamics list as entries {effect, anchorId, otherId, moment, beats, division, winner, intensity, reason}, using words and offered ids. " +
        "Cutting to the beat: the view may carry the music the edit is cut to (music.beat, or a beat on an offered music track), measured by code: bpm and tempo, beatSec and barSec (one beat, one bar of four beats), drop (where the music hits hardest, as a bar and a time) and outro (where it winds down). " +
        "Plan with these facts: choose runs that last about a whole number of bars or beats, let the energy after the drop suit shorter runs, put the strongest moment right after the drop and end around the outro. " +
        "You still only choose ids; the compile puts every cut and move on the beat for you, so never state a time, a length or a number. " +
        "During the discussion each seat proposes at most a few moves, each with its anchor id (and the second id for BeatAlternate or StrobeCut), so the director can settle the list.";

    // Why a catalogue entry is dropped, as facts the model can use: a move needs room in its shot and a grid line inside it, and the drop is
    // one moment of the film. Words only: no number, time or duration is ever asked of the model.
    private const string FitWords =
        "Fit each move to its shot. BeatAlternate needs room for whole cells after its first grid line: a short shot takes none, a medium shot takes division Beat or HalfBeat, and only a long shot takes division Bar (the planner's view gives each kept shot's length as a word and marks the shot that holds the drop; the story editor's view does not, so keep a shot long enough for the move you want on it). " +
        "moment OnDrop only works on the one shot that holds the music's drop, about one shot of the whole film, so StrobeCut and FlashFrame on OnDrop belong on that shot alone; on every other shot use OnBar, OnBeat or OnAccent. " +
        "A PunchIn on the same shot as a BeatAlternate or StrobeCut is dropped, because the alternation owns the rest of its shot: give each shot one move. ";

    // The dynamic edit's whole prompt wording. It lives here, not in the base story editor / director prompts, so a run
    // without dynamics prompts byte for byte as before (BasePromptsUnchangedTests).
    private const string DynamicsWords =
        "This run has a dynamic edit: this run's pace overrides \"prefer fewer, longer runs over many short ones\", so cut as busily as the pace below allows. " +
        "You may add a dynamics list to your output: beat-aligned moves the compile applies on top of your cut. Each entry is {effect, anchorId, otherId, moment, beats, division, winner, intensity, reason}. " +
        "Every field is a word or an id you were offered, never a number, a time or a duration. " +
        "effect is one of SplitOnBeat, PunchIn, PunchOut, BeatAlternate, StrobeCut, FlashFrame, SpeedRamp or WhipOnBar. " +
        "anchorId is an offered id of the span the move belongs to; otherId (BeatAlternate and StrobeCut only) is a second offered id, preferably from a different source. " +
        "moment is OnBeat, OnBar, OnDrop or OnAccent (the compile finds the exact beat; you never say when). " +
        "beats (Two, Four, Eight, Sixteen), division (Beat, HalfBeat, Bar) and winner (A or B) apply to BeatAlternate only. intensity is Subtle, Normal or Strong. " +
        "The pace limits how many moves the compile keeps and which effects it allows, and an entry with an unknown word or id is dropped, so list only moves that serve the content. The minimum gap between moves runs from the start of one (its anchor moment) to the start of the next, not from the end of a move that spans several beats.";

    // The dynamics planner's wording: the same catalogue and the same pace bound as the editor's, without the editor's "you may add a
    // list to your cut" framing, because the planner's whole output is the list. Lives here so a run without dynamics prompts as before.
    private const string PlannerWords =
        "This run has a dynamic edit: plan its beat-aligned moves from the ids in your view. " +
        "anchorId is always a source shot id from sourceShots; a generated clip id is only ever the otherId of a BeatAlternate or StrobeCut (the compile drops a generated clip id used as an anchor). " +
        "The pace limits how many moves the compile keeps and which effects it allows, and an entry with an unknown word or id is dropped, so list only moves that serve the content. The minimum gap between moves runs from the start of one (its anchor moment) to the start of the next, not from the end of a move that spans several beats. ";

    internal static readonly IReadOnlyDictionary<StyleDynamics, IReadOnlyDictionary<StyleAgentRole, string>> Dynamics =
        new Dictionary<StyleDynamics, IReadOnlyDictionary<StyleAgentRole, string>>
        {
            [StyleDynamics.Calm] = Map(
                (StyleAgentRole.Editor, "Dynamics: calm. " + DynamicsWords + " Keep to few, subtle moments: PunchIn and WhipOnBar only, at Subtle intensity."),
                (StyleAgentRole.Dynamics, "Dynamics: calm. " + PlannerWords + "Keep to few, subtle moments: PunchIn and WhipOnBar only, at Subtle intensity."),
                (StyleAgentRole.Graphics, "Dynamics: calm. The edit adds only a few subtle moves; keep overlays still and uncluttered."),
                (StyleAgentRole.Sound, "Dynamics: calm. Keep cues sparse and soft; the edit adds only a few subtle moves."),
                (StyleAgentRole.Music, "Dynamics: calm. Prefer a track whose beat is steady and unobtrusive.")),
            [StyleDynamics.Medium] = Map(
                (StyleAgentRole.Editor, "Dynamics: medium. " + DynamicsWords + " Use a moderate number of moments: PunchIn, WhipOnBar, SplitOnBeat, PunchOut and FlashFrame, at Normal intensity at most; leave BeatAlternate, StrobeCut and SpeedRamp out."),
                (StyleAgentRole.Dynamics, "Dynamics: medium. " + PlannerWords + "Use a moderate number of moments: PunchIn, WhipOnBar, SplitOnBeat, PunchOut and FlashFrame, at Normal intensity at most; leave BeatAlternate, StrobeCut and SpeedRamp out."),
                (StyleAgentRole.Graphics, "Dynamics: medium. The edit moves on the beat; keep overlays short so they do not fight the punch-ins."),
                (StyleAgentRole.Sound, "Dynamics: medium. Land crisp cues on the punch-ins and flashes."),
                (StyleAgentRole.Music, "Dynamics: medium. Prefer a track with a clear beat and audible accents the edit can land on.")),
            [StyleDynamics.Fast] = Map(
                (StyleAgentRole.Editor, "Dynamics: fast. " + DynamicsWords + " Use many strong moments, any effect in the catalogue, including BeatAlternate between two different sources and StrobeCut on a drop. " + FitWords),
                (StyleAgentRole.Dynamics, "Dynamics: fast. " + PlannerWords + "Use many strong moments, any effect in the catalogue, including BeatAlternate between a static source shot and a generated clip and StrobeCut on a drop. " + FitWords),
                (StyleAgentRole.Graphics, "Dynamics: fast. The edit moves on every accent; keep overlays very short and out of the way."),
                (StyleAgentRole.Sound, "Dynamics: fast. Accent the alternations, flashes and speed ramps with punchy cues."),
                (StyleAgentRole.Music, "Dynamics: fast. Prefer a driving track with strong, clear accents."))
        };

    internal static readonly IReadOnlyDictionary<RunOutputFormat, IReadOnlyDictionary<StyleAgentRole, string>> Format =
        new Dictionary<RunOutputFormat, IReadOnlyDictionary<StyleAgentRole, string>>
        {
            [RunOutputFormat.Vertical] = FormatMap("vertical 9:16 (portrait)", "portrait"),
            [RunOutputFormat.Square] = FormatMap("square 1:1", "square or portrait"),
            [RunOutputFormat.Landscape] = FormatMap("landscape 16:9", "landscape"),
            [RunOutputFormat.Portrait] = FormatMap("portrait 4:5", "portrait")
        };

    /// <summary>
    /// The one output-resolution line, for the roles that build a composition (the Author and the
    /// composition reviser); null for every other role. The composition keeps its usual 1080-class
    /// size and the renderer scales it, so the line never asks for a different width or height.
    /// </summary>
    public static string? OutputResolution(int shortSide, StyleAgentRole role) => role == StyleAgentRole.Author
        ? $"Output resolution: {ReelBolt.Shared.Styles.OutputResolution.DisplayName(shortSide)}. Author the composition at its usual 1080-class size; " +
          $"the renderer scales it to {ReelBolt.Shared.Styles.OutputResolution.Label(shortSide)}."
        : null;

    private static IReadOnlyDictionary<StyleAgentRole, string> AllCreative(string text) => Map(
        (StyleAgentRole.Editor, text),
        (StyleAgentRole.Graphics, text),
        (StyleAgentRole.Music, text),
        (StyleAgentRole.Color, text),
        (StyleAgentRole.Sound, text),
        (StyleAgentRole.Shots, text),
        (StyleAgentRole.Narration, text),
        (StyleAgentRole.Scriptwriter, text),
        (StyleAgentRole.Director, text),
        (StyleAgentRole.Author, text));

    private static IReadOnlyDictionary<StyleAgentRole, string> ColorMap(string lookWord, string description) => Map(
        (StyleAgentRole.Color, $"Colour: {description}. Prefer the Look word \"{lookWord}\" unless the footage clearly cannot take it."),
        (StyleAgentRole.Shots, $"Colour: {description}; describe generated shots to match it."),
        (StyleAgentRole.Author, $"Colour: {description}."));

    private static IReadOnlyDictionary<StyleAgentRole, string> FormatMap(string shape, string matching) => Map(
        (StyleAgentRole.Editor, $"Target format: {shape}. Prefer {matching} source clips (see the view's \"orientation\" block); use a clip of another orientation only when it carries material nothing else does, because it will be cropped or letterboxed."),
        (StyleAgentRole.Graphics, $"Target format: {shape}. Keep overlays inside the frame's safe centre so they survive the final crop."),
        (StyleAgentRole.Shots, $"Target format: {shape}. Prefer anchors in {matching} clips and compose generated shots for this shape."),
        (StyleAgentRole.Director, $"Target format: {shape}. Compose scenes for this shape."),
        (StyleAgentRole.Author, $"Target format: {shape}. Lay the composition out for this shape."),
        (StyleAgentRole.Reviewer, $"Target format: {shape}. Material that had to be heavily cropped or letterboxed to fit is a weakness."));
}
