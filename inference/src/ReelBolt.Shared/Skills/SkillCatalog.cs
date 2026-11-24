using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Skills;

/// <summary>
/// Code-defined source of truth for WHICH skills exist and WHICH built-in agents get them by
/// default. See <c>docs/video-editing.md</c> / the "Remotion Knowledge Base" replacement design
/// for the full rationale — this deliberately mirrors Microsoft Agent Framework's own Agent
/// Skills <c>SKILL.md</c> + YAML-frontmatter format (and the pattern any Claude Code-style
/// environment uses for its own skills), built natively rather than by adopting
/// <c>Microsoft.SemanticKernel</c> or MAF's <c>AgentSkillsProvider</c> class.
/// </summary>
public static partial class SkillCatalog
{
    /// <summary>
    /// Upstream repository for the three design-taste skills below (MIT licensed; each vendored
    /// directory carries its LICENSE) — its own repo and its own pin, recorded in
    /// <c>inference/skills/UPSTREAM.md</c> next to the Remotion one.
    /// </summary>
    private const string TasteRepoUrl = "https://github.com/Leonxlnx/taste-skill";

    /// <summary>Pinned commit SHA for <see cref="TasteRepoUrl"/> — see <c>inference/skills/UPSTREAM.md</c>.</summary>
    private const string TasteCommit = "717446e07a78d4e6d7918b1fde1a263378eebac9";

    /// <summary>Builds the provenance link for a taste skill, shaped like the Remotion one in <c>SkillCatalog.RemotionProvenance.cs</c>.</summary>
    private static string TasteSourceUrl(string relativePath) =>
        $"{TasteRepoUrl}/tree/{TasteCommit}/skills/{relativePath}";

    /// <summary>
    /// Five Remotion skills plus three design-taste skills — deliberately narrow: only what agents
    /// doing automated, headless Remotion codegen for a promotional-video pipeline need (no
    /// SaaS-platform building, interactive Studio UI, version-upgrade guides or docs indexing). The taste repository ships thirteen skills; only the core <c>taste-skill</c> and
    /// two style recipes apply to video (the rest are web-page, mobile, image-generation or
    /// output-format skills). See inference/skills/UPSTREAM.md for exactly what was vendored and why.
    /// </summary>
    public static IReadOnlyList<SkillDescriptor> All { get; } =
    [
        new SkillDescriptor(
            Name: "remotion-create",
            DisplayName: "Remotion Create",
            Description: "Project and composition scaffolding, Tailwind setup, video layout basics for a new Remotion component.",
            Category: SkillCategory.Remotion,
            RelativePath: "remotion-create",
            SourceUrl: RemotionSourceUrl("remotion-create"),
            SourceCommit: RemotionSourceCommit),

        new SkillDescriptor(
            Name: "remotion-markup",
            DisplayName: "Remotion Markup",
            Description: "Core Remotion React markup patterns: timing, sequencing, transitions, audio, 3D, text, animation effects.",
            Category: SkillCategory.Remotion,
            RelativePath: "remotion-markup",
            SourceUrl: RemotionSourceUrl("remotion-markup"),
            SourceCommit: RemotionSourceCommit),

        new SkillDescriptor(
            Name: "remotion-render",
            DisplayName: "Remotion Render",
            Description: "Render configuration and output, including transparent/alpha-channel video output.",
            Category: SkillCategory.Remotion,
            RelativePath: "remotion-render",
            SourceUrl: RemotionSourceUrl("remotion-render"),
            SourceCommit: RemotionSourceCommit),

        new SkillDescriptor(
            Name: "remotion-captions",
            DisplayName: "Remotion Captions",
            Description: "Rendering captions and subtitles in a Remotion composition.",
            Category: SkillCategory.Remotion,
            RelativePath: "remotion-captions",
            SourceUrl: RemotionSourceUrl("remotion-captions"),
            SourceCommit: RemotionSourceCommit),

        new SkillDescriptor(
            Name: "remotion-multimedia",
            DisplayName: "Remotion Multimedia",
            Description: "Probing and working with media duration/dimensions/metadata in a Remotion composition.",
            Category: SkillCategory.Remotion,
            RelativePath: "remotion-multimedia",
            SourceUrl: RemotionSourceUrl("remotion-multimedia"),
            SourceCommit: RemotionSourceCommit),

        // The three entries below are written for web pages upstream. Each directory therefore
        // carries a ReelBolt-authored REELBOLT-PREFACE.md (see SkillPreface) that UseSkill puts in
        // front of the unmodified upstream text; the Description below is ours too, because it is
        // the advertisement an agent sees before it decides to load anything.
        new SkillDescriptor(
            Name: "taste-skill",
            DisplayName: "Design Taste",
            Description: "Design taste for video: how to pick fonts, colour, shape, on-screen copy and motion so a composition does not look like a generic AI template. Long; it opens with a note on which parts apply to video. Load it once, when you are deciding or writing the look.",
            Category: SkillCategory.DesignTaste,
            RelativePath: "taste-skill",
            SourceUrl: TasteSourceUrl("taste-skill"),
            SourceCommit: TasteCommit),

        new SkillDescriptor(
            Name: "minimalist-skill",
            DisplayName: "Minimalist Style",
            Description: "A concrete look to apply only when the brief asks for a minimalist, calm or editorial style: warm monochrome palette, sans body with serif headings, quiet motion.",
            Category: SkillCategory.DesignTaste,
            RelativePath: "minimalist-skill",
            SourceUrl: TasteSourceUrl("minimalist-skill"),
            SourceCommit: TasteCommit),

        new SkillDescriptor(
            Name: "brutalist-skill",
            DisplayName: "Brutalist Style",
            Description: "A concrete look to apply only when the brief asks for a brutalist, Swiss, industrial or terminal style: huge tight uppercase type, a rigid visible grid, one hazard-red accent.",
            Category: SkillCategory.DesignTaste,
            RelativePath: "brutalist-skill",
            SourceUrl: TasteSourceUrl("brutalist-skill"),
            SourceCommit: TasteCommit),

        // First-party (ReelBolt category, no upstream): written in this repository, so no SourceUrl/SourceCommit.
        new SkillDescriptor(
            Name: "reelbolt-blocks",
            DisplayName: "Block Library",
            Description: "Ready-made, tested Remotion blocks in nine categories (transitions, titles, captions, charts, maps, effects, backgrounds, devices, shapes). Before building one from scratch, call ListBlocks to find it and AddBlock to copy it into src/blocks/.",
            Category: SkillCategory.ReelBolt,
            RelativePath: "reelbolt-blocks"),
    ];

    private static readonly IReadOnlyDictionary<string, SkillDescriptor> ByName =
        All.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static SkillDescriptor? Find(string name) =>
        !string.IsNullOrWhiteSpace(name) && ByName.TryGetValue(name, out SkillDescriptor? descriptor)
            ? descriptor
            : null;

    // Every set below is a plain array of the skill Names above — kept as string[] rather than
    // SkillDescriptor[] so this stays a dependency-free source of truth (no SkillDescriptor
    // construction here), consumed via SkillCatalog.Find by callers.
    private static readonly string[] AllFive =
        ["remotion-create", "remotion-markup", "remotion-render", "remotion-captions", "remotion-multimedia"];
    private static readonly string[] CreateMarkupAndTaste = ["remotion-create", "remotion-markup", "taste-skill"];
    private static readonly string[] MarkupAndTaste = ["remotion-markup", "taste-skill"];
    private static readonly string[] MarkupRenderAndTaste = ["remotion-markup", "remotion-render", "taste-skill"];
    private static readonly string[] TasteAndStyles = ["taste-skill", "minimalist-skill", "brutalist-skill"];
    private static readonly string[] AllFiveTasteAndStyles =
    [
        "remotion-create", "remotion-markup", "remotion-render", "remotion-captions", "remotion-multimedia",
        "taste-skill", "minimalist-skill", "brutalist-skill"
    ];
    private static readonly string[] AllFiveTasteStylesAndBlocks = [.. AllFiveTasteAndStyles, "reelbolt-blocks"];
    private static readonly string[] MarkupRenderTasteAndBlocks = [.. MarkupRenderAndTaste, "reelbolt-blocks"];
    private static readonly string[] None = [];

    /// <summary>
    /// The COMPLETE, non-overridable skill set for a built-in <see cref="AgentType"/>. This is
    /// the ONLY place a built-in agent's skills are determined — there is deliberately no
    /// runtime/DB override for built-in agents. A sibling effort adds a per-Custom-agent DB
    /// override (<c>AgentDefinition.AssignedSkillsJson</c>), but that applies ONLY to Custom
    /// (non-built-in) <c>AgentDefinition</c> rows — a built-in agent's skills come from this
    /// method and the seeded prompt alone, full stop. See
    /// <c>ReelBoltAgentBase.CreateAgentAsync</c> for how the two paths are told apart.
    /// </summary>
    public static IReadOnlyList<string> DefaultsFor(AgentType agentType) => agentType switch
    {
        // Writes TSX component files into the sandbox from scratch — needs scaffolding
        // (composition registration, Tailwind setup) and the core markup patterns it's
        // translating the source app into. It explicitly does not render (AuthorAgent does),
        // so no remotion-render. Also taste-skill: it chooses the type scale, colour and radius of
        // every component it writes, and the preface in that skill puts the app's own brand
        // tokens (which it is translating) ahead of any taste preference.
        AgentType.RemotionComponentTranslator => CreateMarkupAndTaste,

        // Purely about timing/sequencing/transition design over components someone else wrote —
        // the whole job lives inside remotion-markup's timing/sequencing/transitions/animations
        // topics. No scaffolding, no rendering, no captions/multimedia probing. Also taste-skill
        // for its "motion must be motivated" and pacing principles; it writes no styling, so not
        // the two style recipes.
        AgentType.AnimationStrategyAgent => MarkupAndTaste,

        // The final assembler: registers/finalizes compositions (create), composes the whole
        // timeline (markup — sequencing/transitions), is the one agent that actually renders the
        // deliverable mp4 (render), assembles voiceover/caption script content into the manifest
        // (captions), and may need to probe asset duration/dimensions while assembling (multimedia).
        // Gets the full set. Also all three taste skills: it writes the final TSX, so it is the
        // agent that has to APPLY a brutalist or minimalist brief the director named, as well as
        // the type, colour and copy discipline in taste-skill.
        AgentType.AuthorAgent => AllFiveTasteStylesAndBlocks,

        // Revises a finished film in place (docs/design/composition-revisions.md): it edits the same
        // TSX the author wrote, previews and renders it, and is typically asked for "better graphics",
        // "a clearer chart", "bigger titles" — so it needs the author's full Remotion set and the
        // taste skills to apply the film's existing look (or a style the request names).
        AgentType.CompositionReviser => AllFiveTasteStylesAndBlocks,

        // Designs one still graphic for the picture editor (docs/editor.md "Designed graphics"): a
        // title treatment, a badge, a price tag, a callout, drawn as a Remotion Still on a transparent
        // frame. Its whole job is visual design, so it gets all three taste skills (type, colour and
        // copy discipline, plus the minimalist and brutalist recipes a request may name) and the full
        // Remotion set (compositions, markup, the transparent-render recipe, fonts via the multimedia
        // topics), even though the platform — not the agent — renders the final PNG.
        AgentType.GraphicDesigner => AllFiveTasteStylesAndBlocks,

        // Reviews the whole pipeline's Remotion output for correctness against best practices
        // across every one of these domains (compositions/timing, rendering, captions, media
        // probing) before scoring — needs the same breadth as AuthorAgent to verify it.
        AgentType.ReviewAgent => AllFive,

        // Optionally renders a small transparent-background overlay asset (lower-third/title/
        // callout) using core markup patterns plus the transparent-render recipe specifically —
        // no project scaffolding (the sandbox/project already exists), no captions/multimedia.
        // Also taste-skill for the overlay's type, colour and copy; not the style recipes, because
        // the look of the whole video is already set by the time an overlay is planned.
        AgentType.MotionGraphicsPlanner => MarkupRenderTasteAndBlocks,

        // The graphics room's moderator/synthesizer: its standalone synthesis call can render the
        // same small transparent overlay assets the solo planner can (see ToolGroupCatalog's
        // MotionGraphicsDirector rationale), so it needs the identical skill set to do it right
        // (including taste-skill, for the same reason as the planner).
        AgentType.MotionGraphicsDirector => MarkupRenderTasteAndBlocks,

        // The director decides the video's look (VisualTheme: mood, colour grading, motifs), but it
        // writes no code, so it gets the taste skills and none of the Remotion ones. It carries the
        // two style recipes because it is the agent that turns "make it brutalist" in a brief into
        // a concrete direction for the author.
        AgentType.DirectorAgent => TasteAndStyles,

        // Deliberately NOT given taste skills, with the reason for each near miss:
        //  - ScriptwriterAgent: its prompt already carries the video-specific copy rules (short
        //    on-screen lines, no markdown, never invent people or metrics), and the copy half of
        //    the taste skill is landing-page copy; a long web-design document for a low-effort
        //    copywriting step costs more than it can return.
        //  - Colorist / ColorGradeDirector: they emit enum words resolved from a fixed internal
        //    table, so there is no colour, font or shape for taste to choose.
        //  - VideoStoryEditor and the other editing agents (VideoEditDirector, MusicSupervisor,
        //    SoundDesigner, ShotDirector, narration and pickup agents): they decide cuts, sound
        //    and generated shots, not typography or layout.
        //  - ReviewAgent keeps the Remotion set only: adding taste criteria would change what its
        //    score measures, and so how often the review loop re-runs the author.
        // Every other AgentType (analysis agents, ExtractTransform/VideoTransform placeholders,
        // FileSummarizerAgent, Custom) likewise gets no skills by default.
        _ => None
    };
}
