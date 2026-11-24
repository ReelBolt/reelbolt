using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Agents;

/// <summary>
/// Single source of truth for agent tool scoping: which <see cref="ToolGroup"/>s a function name
/// belongs to, and which groups a built-in <see cref="AgentType"/> is granted.
///
/// This class only knows NAMES and GROUPING — it has no dependency on the tool implementations
/// (which live in the WorkflowEngine, where the actual <c>AIFunctionFactory.Create(...)</c> calls
/// happen against injected tool-provider instances). Two very different consumers read from this
/// one place:
/// - <c>AgentToolProvider.GetTools</c> (WorkflowEngine) — the real, load-bearing decision that
///   constructs the runnable <c>AIFunction[]</c> an agent actually executes with.
/// - <c>DatabaseSeeder.GetAvailableToolsJson</c> (Inference API) — display-only metadata shown in
///   the UI's "Available Tools" card on an agent's detail page.
///
/// These two consumers hand-maintained separate mappings before this class existed, and that
/// duplication drifted twice in practice (see the git history / prior comments this class's
/// content was moved from): once <c>FailWorkflow</c> was silently missing from the display
/// metadata entirely, and once <c>MotionGraphicsPlanner</c> was widened in the real tool provider
/// without the corresponding display-metadata update. Both call sites must now derive their
/// answer from <see cref="GroupsFor"/> + <see cref="FunctionNamesFor"/> instead of maintaining
/// their own list, so the two can never again disagree about what a given <see cref="AgentType"/>
/// is granted.
/// </summary>
public static class ToolGroupCatalog
{
    /// <summary>
    /// The tool/function names belonging to a <see cref="ToolGroup"/>. Names must match the
    /// actual C# method names on the WorkflowEngine tool-provider classes
    /// (<c>ProjectFileAgentTools</c>, <c>ReactRemotionSandboxTools</c>,
    /// <c>WorkflowControlAgentTools</c>) exactly, since
    /// <c>AIFunctionFactory.Create</c> names each constructed <c>AIFunction</c> after the method
    /// it wraps.
    /// </summary>
    public static IReadOnlyList<string> FunctionNamesFor(ToolGroup group) => group switch
    {
        // ReadProjectFileLines/GetProjectFileOutline are the project-file twins of
        // ReadSandboxFileLines/GetSandboxFileOutline: read-only, so they ride with ReadProjectFile
        // and every agent that can read a project file can read part of one.
        ToolGroup.ProjectRead =>
        [
            "ListProjectFiles", "ReadProjectFile", "ReadProjectFileLines", "GetProjectFileOutline",
            "SearchProjectFiles", "GetDeterministicContextFiles"
        ],
        // EditProjectFile mutates (it saves a new version of an existing file), so it sits with
        // WriteProjectFile, not with the read tools — the same split the sandbox draws between
        // SandboxBrowse and SandboxAuthoring.
        ToolGroup.ProjectWrite =>
        [
            "WriteProjectFile", "EditProjectFile"
        ],
        // ReadSandboxFileLines/GetSandboxFileOutline are read-only locator/inspection tools —
        // grouped with the other Browse tools since they never mutate the sandbox, unlike
        // EditSandboxFile/ApplySandboxFileEdits below (SandboxAuthoring), which do.
        ToolGroup.SandboxBrowse =>
        [
            "GetSandboxStatus", "ListSandboxFiles", "ReadSandboxFile", "ReadSandboxFileLines", "GetSandboxFileOutline"
        ],
        ToolGroup.SandboxMetadata =>
        [
            "GetSandbox"
        ],
        ToolGroup.SandboxLint =>
        [
            "CheckLintAndTypeErrors"
        ],
        // EditSandboxFile/ApplySandboxFileEdits mutate an existing file (a targeted alternative to
        // WriteSandboxFile), so they belong in Authoring alongside it, not in the read-only Browse
        // group above.
        ToolGroup.SandboxAuthoring =>
        [
            "EnsureSandbox", "WriteSandboxFile", "StageVoiceoverAudio", "StageGeneratedClips", "StageProjectFile", "StageFont", "EditSandboxFile",
            "ApplySandboxFileEdits", "InsertSandboxFileLines",
            "DeleteSandboxPath", "InstallNpmPackages", "RunSandboxNpmScript"
        ],
        // CompleteSandbox is deliberately NOT granted. The sandbox is keyed by EXECUTION, not by
        // step, so an agent tearing it down destroys the workspace every later step in the same
        // execution still depends on — and an agent cannot know whether it is the last one. It is
        // also wrong even for a final step: the agent still has to emit its structured answer
        // after the call, and the framework may touch the sandbox again on the way there.
        // Observed live: MotionGraphicsPlanner rendered both of its overlay assets, uploaded them,
        // called CompleteSandbox because its own prompt told it to, then failed its step on the
        // next sandbox call with a bare "sandbox not found" 404. Reclamation belongs to the
        // sandbox service's idle janitor (SANDBOX_TTL, default 1h), which already owns it.
        //
        // GetCompositionInfo and RenderPreviewStills run Remotion (`compositions`, `still`) in the
        // sandbox without uploading anything — they exist so the full render, which takes minutes,
        // happens once. They belong with the render tools: they execute the composition, so an agent
        // that may not render must not get them either. CheckLayout (the layout lint) is the same kind
        // of pre-render check and rides with them; the reviewer's SandboxPreview does not carry it.
        // DeclareRenderEvents (T-110) writes the event manifest the render uploads next to the film, so
        // it belongs to whoever may render.
        ToolGroup.SandboxRender =>
        [
            "RunSandboxRemotionCommand", "RenderVideoAndUploadToStorage", "GetCompositionInfo", "RenderPreviewStills",
            "CheckLayout", "DeclareRenderEvents"
        ],
        // The reviewer exception to "an agent that may not render must not get them": the ReviewAgent
        // gets the two preview tools ALONE, so it can see the frames it scores (read-only stills,
        // deleted afterwards, no upload). No RunSandboxRemotionCommand, no RenderVideoAndUploadToStorage:
        // it never renders a deliverable, so nothing keyed on SandboxRender (step caching, the
        // resolution-scaled credit hold, the replay graph's sandbox-writer and video-producer rules)
        // changes for it. Every agent that may render keeps these tools through SandboxRender.
        ToolGroup.SandboxPreview =>
        [
            "GetCompositionInfo", "RenderPreviewStills"
        ],
        ToolGroup.WorkflowControl =>
        [
            "FailWorkflow"
        ],
        // ReadDesignSystem/ReadProductProfile (SPEC §12): read-only access to the run's pinned brand
        // design system and product map. They read the per-run ambient state, never a database row
        // and never a caller-supplied id, so granting the group grants no write or spend surface.
        ToolGroup.DesignSystemRead =>
        [
            "ReadDesignSystem", "ReadProductProfile"
        ],
        // WebSearch (agent-web-search): one live, metered, capped search call whose results come back
        // fenced as untrusted text. It spends (the provider's per-search price), which is why the grant
        // is the four writing/planning agents below and nothing else, and why a step holding it is never
        // cached (StepCachePolicy).
        // Block library (inference/blocks/): browse the vetted, hash-verified catalog and copy a block into
        // src/blocks/ of the run's workspace. AddBlock writes only under that fixed prefix, so it is a sandbox
        // writer: a step holding this group is never cached and the replay graph treats it as one.
        ToolGroup.SandboxBlocks =>
        [
            "ListBlocks", "GetBlock", "AddBlock"
        ],
        ToolGroup.WebSearch =>
        [
            "WebSearch"
        ],
        // ListVideoEvents/DescribeVideoWindow (progressive context): read-only, free, scoped to the run's own analysis.
        ToolGroup.EventsRead =>
        [
            "ListVideoEvents", "DescribeVideoWindow"
        ],
        ToolGroup.FootageRead =>
        [
            "ReadVideoSection", "ReadVideoTranscript", "ListVideoSfxClips", "ListVideoMusic"
        ],
        ToolGroup.OfferListRead =>
        [
            "ListVideoSfxClips", "ListVideoMusic", "ReadVideoSection"
        ],
        // LookAtMoment (on-request look): one metered Vision call on three stored frames of one event of the run's own
        // analysis. It writes nothing; it spends, so it is capped (MomentLookLimits) and its worst case joins the hold.
        ToolGroup.MomentLook =>
        [
            "LookAtMoment"
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown ToolGroup.")
    };

    /// <summary>
    /// The scoping decision itself — which <see cref="ToolGroup"/>s a built-in
    /// <see cref="AgentType"/> is granted. This is the carefully-reasoned security scoping that
    /// used to live as comments directly in <c>AgentToolProvider.GetTools</c>'s switch
    /// expression (WorkflowEngine) — moved here verbatim, not summarized, since it is the actual
    /// rationale for each agent's access level and both consumers need to keep seeing it.
    /// </summary>
    public static IReadOnlyList<ToolGroup> GroupsFor(AgentType agentType) => agentType switch
    {
        // ──────────────────────────────────────────────────────────────────
        // ExtractTransform / VideoTransform: deterministic, non-LLM placeholder agents — never
        // registered as an IReelBoltAgent and never actually invoked with tools
        // (ExtractStepExecutor / VideoAnalyzeStepExecutor / VideoCompileStepExecutor run
        // deterministic C# code directly, with no IChatClient/IAgentRegistry dependency).
        // Explicitly empty rather than falling through to the default read-only arm below, so
        // both AgentToolProvider (runtime) and DatabaseSeeder (display metadata) agree there is
        // nothing to grant for an AgentType that is never actually run as an agent.
        // ──────────────────────────────────────────────────────────────────

        AgentType.ExtractTransform or AgentType.VideoTransform => [],

        // ──────────────────────────────────────────────────────────────────
        // Analysis agents: project read-only access only.
        // No sandbox access — these agents only inspect source files.
        // ──────────────────────────────────────────────────────────────────

        AgentType.CodeStructureAnalyzer or
        AgentType.DependencyAnalyzer or
        AgentType.ComponentInventoryAnalyzer or
        AgentType.RouteAndApiAnalyzer or
        AgentType.StyleAndThemeExtractor =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // Translation agents: project read/write + sandbox code authoring.
        // Can install packages and verify correctness, but do NOT render.
        //
        // Remotion knowledge-base access (UseSkill/ReadSkillResource) is NOT a ToolGroup — it is
        // resolved per agent-run from SkillCatalog.DefaultsFor(AgentType) instead, since it is a
        // named, on-demand-loadable skill rather than a fixed tool surface. See
        // ReelBolt.Shared/Skills/SkillCatalog.cs for which of these agents get which skills.
        // ──────────────────────────────────────────────────────────────────

        AgentType.RemotionComponentTranslator =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.ProjectWrite,
            ToolGroup.SandboxBrowse,
            ToolGroup.SandboxMetadata,
            ToolGroup.SandboxLint,
            ToolGroup.SandboxAuthoring,
            ToolGroup.WorkflowControl,
            ToolGroup.DesignSystemRead
        ],

        AgentType.AnimationStrategyAgent =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.SandboxBrowse,
            ToolGroup.SandboxMetadata,
            ToolGroup.WorkflowControl,
            ToolGroup.DesignSystemRead
        ],

        // ──────────────────────────────────────────────────────────────────
        // Production planning agents: project + sandbox read-only.
        // Director and Scriptwriter produce creative artefacts from data
        // already in the project/sandbox; they never write or render.
        //
        // Deliberately no SandboxMetadata (GetSandbox) here — unlike every other sandbox-touching
        // agent type in this catalog, Director/Scriptwriter only ever need to browse sandbox
        // files, not read full sandbox metadata.
        // ──────────────────────────────────────────────────────────────────

        AgentType.DirectorAgent or AgentType.ScriptwriterAgent =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.SandboxBrowse,
            ToolGroup.WorkflowControl,
            ToolGroup.DesignSystemRead,
            // May look a fact up on the web (untrusted, fenced, capped, metered); see ToolGroup.WebSearch.
            ToolGroup.WebSearch
        ],

        // ──────────────────────────────────────────────────────────────────
        // Author: full sandbox pipeline including render and upload.
        // This is the only agent producing the pipeline's FINAL deliverable video; the only
        // other agent granted SandboxRender (RenderVideoAndUploadToStorage) is
        // MotionGraphicsPlanner below, which uses it for a small, optional overlay ASSET (never
        // the final video) as part of the separate video-editing pipeline (see
        // docs/video-editing.md "Motion graphics (Phase 3)").
        // ──────────────────────────────────────────────────────────────────

        AgentType.AuthorAgent =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.ProjectWrite,
            ToolGroup.SandboxBrowse,
            ToolGroup.SandboxMetadata,
            ToolGroup.SandboxLint,
            ToolGroup.SandboxAuthoring,
            ToolGroup.SandboxBlocks,
            ToolGroup.SandboxRender,
            ToolGroup.WorkflowControl,
            ToolGroup.DesignSystemRead
        ],

        // ──────────────────────────────────────────────────────────────────
        // Review agent: read-only access + lint/type checking + preview stills.
        // Inspects existing artefacts and surfaces quality issues. SandboxPreview (not
        // SandboxRender) lets it look at the Author's frames: read-only stills, no upload.
        // ──────────────────────────────────────────────────────────────────

        AgentType.ReviewAgent =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.SandboxBrowse,
            ToolGroup.SandboxMetadata,
            ToolGroup.SandboxLint,
            ToolGroup.WorkflowControl,
            ToolGroup.DesignSystemRead,
            ToolGroup.SandboxPreview
        ],

        // ──────────────────────────────────────────────────────────────────
        // VideoStoryEditor: read-only project context + FailWorkflow only. It decides
        // which offered ids to keep; it never produces or touches media directly.
        // Explicitly NO sandbox tools, no ProjectWrite, no render tool — unlike the
        // default/unknown case below, this is spelled out on purpose so a future widening
        // of the default case does not silently hand this agent write/render access.
        // ──────────────────────────────────────────────────────────────────

        AgentType.VideoStoryEditor =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.EventsRead,
            ToolGroup.FootageRead,
            // May ask the picture model what one moment shows (metered, capped; see ToolGroup.MomentLook).
            ToolGroup.MomentLook,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // VideoEditDirector: read-only project context + FailWorkflow only (no events or footage
        // read tools, unlike VideoStoryEditor above). Used both as a StepType.EditRoom group-chat participant (via a
        // raw AIAgent the executor builds directly from these same tools) and for the standalone
        // structured-output synthesis call — neither role produces or touches media.
        // ──────────────────────────────────────────────────────────────────

        AgentType.VideoEditDirector =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.FootageRead,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // MusicSupervisor: read-only project context + FailWorkflow only, identical scope to
        // VideoStoryEditor/VideoReviewAgent above. It only picks among offered "m{n}" track
        // ids and enum-word settings — it never produces or touches media directly (no render,
        // no sandbox — unlike MotionGraphicsPlanner, there is no rendered-asset escape hatch
        // here). Explicitly spelled out rather than left to the default arm below, same
        // reasoning as VideoStoryEditor's own comment.
        // ──────────────────────────────────────────────────────────────────

        AgentType.MusicSupervisor =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.OfferListRead,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // VideoReviewAgent: read-only project context + FailWorkflow only, identical scope
        // to VideoStoryEditor above. Its review evidence (sentenceCheck / overlay coverage)
        // is already present in the pipeline history it is given as input — it never needs
        // sandbox/Remotion-skill tools since there is no code to inspect for a video edit.
        // ──────────────────────────────────────────────────────────────────

        AgentType.VideoReviewAgent =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // MotionGraphicsPlanner (Phase 3): the same full sandbox+Remotion+render pipeline as
        // AuthorAgent immediately above it, minus ProjectWrite (WriteProjectFile) — this agent
        // renders a small transparent-background overlay asset (a lower-third, title card,
        // callout), never a whole project artifact, so there is nothing for it to persist as a
        // project file. It still decides overlay placement/content anchored only to offered
        // placement ids (never a timestamp or pixel coordinate — see MotionGraphicsPlanOutput's
        // reflection-tested invariant); the render tools let it OPTIONALLY back that decision
        // with an actual designed/animated graphic instead of only plain drawtext, via
        // MotionGraphicsOverlay.RenderedAssetStorageKey. Previously this case was deliberately
        // minimal (read-only + FailWorkflow only, identical to VideoStoryEditor) — widened
        // here now that the agent can genuinely produce and render Remotion components; a
        // future change should not silently narrow this back down without updating this
        // comment (both AgentToolProvider's runtime grant and DatabaseSeeder's display metadata
        // now derive from this single arm, so they cannot drift independently of each other).
        // ──────────────────────────────────────────────────────────────────

        AgentType.MotionGraphicsPlanner =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.EventsRead,
            ToolGroup.FootageRead,
            ToolGroup.SandboxBrowse,
            ToolGroup.SandboxMetadata,
            ToolGroup.SandboxLint,
            ToolGroup.SandboxAuthoring,
            ToolGroup.SandboxBlocks,
            ToolGroup.SandboxRender,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // MotionGraphicsDirector: the StepType.GraphicsRoom moderator/synthesizer — granted the
        // SAME full sandbox+Remotion+render set as MotionGraphicsPlanner directly above (minus
        // ProjectWrite, for the same reason), NOT VideoEditDirector's minimal read-only scope.
        // Deliberate, not a copy-paste of either neighbor: the graphics room's synthesis call is
        // where the room's plan can optionally be backed by a real rendered transparent overlay
        // asset (MotionGraphicsOverlay.RenderedAssetStorageKey) — capability parity with the solo
        // planner it replaces, which would otherwise silently regress to plain-drawtext-only
        // whenever a workflow swaps the solo step for the room. The room-PARTICIPANT turns (short
        // free-form prose, ~220 tokens) never get this scope: GraphicsRoomStepExecutor restricts
        // every in-room agent (seats AND the director's room instance) to the ProjectRead +
        // WorkflowControl subset of its grant, so sandbox tools are reachable only from the one
        // standalone structured synthesis call — the same place the solo planner uses them. The
        // prompt-injection tradeoff documented on MotionGraphicsPlanner (media-derived view text
        // reaching a code-executing agent, bounded by the sandbox's containment) applies here
        // identically and is accepted for the same reasons.
        // ──────────────────────────────────────────────────────────────────

        AgentType.MotionGraphicsDirector =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.EventsRead,
            ToolGroup.FootageRead,
            ToolGroup.SandboxBrowse,
            ToolGroup.SandboxMetadata,
            ToolGroup.SandboxLint,
            ToolGroup.SandboxAuthoring,
            ToolGroup.SandboxBlocks,
            ToolGroup.SandboxRender,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // Colorist / ColorGradeDirector: read-only project context + FailWorkflow only,
        // identical scope to VideoStoryEditor/MusicSupervisor above. Both only emit enum WORDS
        // (a named look plus strength/shadow/highlight words — see ColorGradePlanOutput's
        // reflection-tested all-strings invariant) that VideoCompileStepExecutor alone resolves
        // to concrete ffmpeg filter parameters; nothing about a grade ever needs rendering, so —
        // unlike MotionGraphicsPlanner/MotionGraphicsDirector — there is deliberately NO sandbox
        // or render grant here, for either role (room-participant turns OR the director's
        // standalone synthesis call). Explicitly spelled out rather than left to the default arm
        // below, same reasoning as VideoStoryEditor's own comment.
        // ──────────────────────────────────────────────────────────────────

        AgentType.Colorist or AgentType.ColorGradeDirector =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // SoundDesigner: read-only project context + FailWorkflow only, identical scope to
        // VideoStoryEditor/MusicSupervisor above. It only pairs offered "x{n}" clip ids with
        // offered cut-anchor ids plus enum-word Timing/Volume settings — it never produces or
        // touches media directly (no render, no sandbox — unlike MotionGraphicsPlanner, there is
        // no rendered-asset escape hatch here: every cue plays an EXISTING uploaded clip).
        // Explicitly spelled out rather than left to the default arm below, same reasoning as
        // VideoStoryEditor's own comment.
        // ──────────────────────────────────────────────────────────────────

        AgentType.SoundDesigner =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.EventsRead,
            ToolGroup.OfferListRead,
            // May ask the picture model what one moment shows (metered, capped; see ToolGroup.MomentLook).
            ToolGroup.MomentLook,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // ShotDirector: read-only project context + FailWorkflow only, identical scope to
        // VideoStoryEditor/MusicSupervisor/SoundDesigner above. It only plans generated shots —
        // offered cut-anchor ids plus optional offered "s{n}" keyframes and enum-word
        // purpose/camera/duration choices — and never produces or touches media directly.
        // Explicitly NO sandbox tools, NO ProjectWrite and NO render tool, and nothing here can
        // reach a provider: the deterministic StepType.VideoGenerate step does the generating and
        // is the only component that can spend money, which is exactly why this agent must be
        // structurally incapable of either (asserted, not asserted-by-eye — see
        // ToolScopingDriftGuardTests). Spelled out rather than left to the default arm below, same
        // reasoning as VideoStoryEditor's own comment: a future widening of the default case must
        // not silently hand this agent write, render, or spend access.
        // ──────────────────────────────────────────────────────────────────

        AgentType.ShotDirector =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl,
            // The one addition to the read-only grant: a capped, metered web search whose results are
            // fenced untrusted text (ToolGroup.WebSearch). Still no write, render or media tool.
            ToolGroup.WebSearch
        ],

        // ──────────────────────────────────────────────────────────────────
        // NarrationWriter: read-only project context + FailWorkflow only, identical scope to
        // VideoStoryEditor/MusicSupervisor/SoundDesigner/ShotDirector above. It only writes the
        // WORDS a voice speaks — an offered cut-anchor id plus the line's own prose — and never
        // produces or touches media: the deterministic StepType.Voiceover step synthesizes the
        // audio, and the deterministic narration fit solver measures the result. There is no
        // rendered-asset escape hatch here (nothing about a narration line needs rendering) and
        // nothing here can reach a synthesis provider or spend money. Explicitly spelled out
        // rather than left to the default arm below, same reasoning as VideoStoryEditor's own
        // comment: a future widening of the default case must not silently hand this agent write,
        // render, or spend access.
        // ──────────────────────────────────────────────────────────────────

        AgentType.NarrationWriter =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl,
            // The one addition to the read-only grant: a capped, metered web search whose results are
            // fenced untrusted text (ToolGroup.WebSearch). Still no write, render or media tool.
            ToolGroup.WebSearch
        ],

        // PickupPlanner / NarrationTranslator (docs/voiceover.md "Pickups" / "Dubs"): the same
        // words-only, read-only grant as NarrationWriter, for the same reasons — spelled out so a
        // widening of the default arm can never hand them write, render or spend access.
        AgentType.PickupPlanner =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl
        ],

        AgentType.NarrationTranslator =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // CompositionReviser (docs/design/composition-revisions.md): the Author's sandbox+Remotion+
        // render pipeline MINUS ProjectWrite. It edits an already-restored film in place and renders
        // it once; it has no business persisting project files (the revision's record is the
        // composition workspace version the render tool captures). The same media-free input as the
        // Author, plus the owner's free-text request, which the runner quotes as data — the sandbox's
        // containment is what bounds it, as it bounds the Author.
        // ──────────────────────────────────────────────────────────────────

        AgentType.CompositionReviser =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.SandboxBrowse,
            ToolGroup.SandboxMetadata,
            ToolGroup.SandboxLint,
            ToolGroup.SandboxAuthoring,
            ToolGroup.SandboxBlocks,
            ToolGroup.SandboxRender,
            ToolGroup.WorkflowControl,
            ToolGroup.DesignSystemRead
        ],

        // ──────────────────────────────────────────────────────────────────
        // GraphicDesigner (docs/editor.md "Designed graphics"): writes ONE transparent Remotion Still
        // in the execution's sandbox and checks it with GetCompositionInfo/RenderPreviewStills — the
        // CompositionReviser's grant, for the same reasons: the Author's sandbox+Remotion pipeline
        // minus ProjectWrite (a graphic is a layer, never a project file). ProjectRead stays so it can
        // read a brand guide or palette the project holds. SandboxRender is granted because the two
        // preview tools live in it ("an agent that may not render must not get them either"); its
        // RenderVideoAndUploadToStorage is NOT how a graphic is delivered — the GraphicDesignRunner
        // renders, verifies and uploads the PNG itself and ignores anything the agent uploaded, and
        // the prompt tells it so. The user's description reaches a code-executing agent exactly as an
        // owner's revision request does: quoted as data in the brief, bounded by the sandbox's
        // containment (read-only rootfs, no egress, no Docker socket).
        // ──────────────────────────────────────────────────────────────────

        AgentType.GraphicDesigner =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.SandboxBrowse,
            ToolGroup.SandboxMetadata,
            ToolGroup.SandboxLint,
            ToolGroup.SandboxAuthoring,
            ToolGroup.SandboxBlocks,
            ToolGroup.SandboxRender,
            ToolGroup.WorkflowControl
        ],

        // TakeSelector / DailiesDirector (issue #93 phase 4): read-only project context +
        // FailWorkflow only, identical scope to VideoStoryEditor/VideoEditDirector. Both only
        // choose among offered "v{n}t{k}" take ids from a measured analysis view — never produce
        // media, never render, never spend. Spelled out rather than left to the default arm below,
        // for the same reason every other video agent is.
        // ──────────────────────────────────────────────────────────────────

        AgentType.TakeSelector or AgentType.DailiesDirector =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // OverlayPlanner (design docs/design/picture-overlay.md §7.1): read-only project context
        // + FailWorkflow only. It picks offered "m{n}" media ids and "o{n}" overlay-window ids
        // and enum words; it authors nothing, so — unlike MotionGraphicsPlanner, which renders
        // its own overlay asset — it gets NO sandbox, NO render and NO write tool. Spelled out
        // rather than left to the default arm below, for the same reason every other video
        // agent is.
        // ──────────────────────────────────────────────────────────────────

        AgentType.OverlayPlanner =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // MotionDirector (design docs/design/video-events-analysis.md section 5): read-only project
        // context + FailWorkflow only. It picks offered "e{n}" event ids and enum words; it authors
        // nothing, so it gets NO sandbox, NO render and NO write tool, same as SoundDesigner. It may ask the
        // picture model what one moment shows (metered, capped; see ToolGroup.MomentLook).
        // ──────────────────────────────────────────────────────────────────

        AgentType.MotionDirector =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.MomentLook,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // DynamicsPlanner (docs/design/dynamic-edit.md): read-only project context + FailWorkflow only. It picks offered shot
        // and generated-clip ids and enum words from a view it is handed; it authors nothing, so it gets NO sandbox, NO render,
        // NO write tool and no picture look.
        // ──────────────────────────────────────────────────────────────────

        AgentType.DynamicsPlanner =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl
        ],

        // ──────────────────────────────────────────────────────────────────
        // Custom / unknown / FileSummarizerAgent (Inference-API-only — FileSummarizerAgent is
        // never actually routed through this resolver at runtime, but the switch's default arm
        // covers it defensively rather than throwing): minimal project read access only.
        // ──────────────────────────────────────────────────────────────────

        _ =>
        [
            ToolGroup.ProjectRead,
            ToolGroup.WorkflowControl
        ]
    };
}
