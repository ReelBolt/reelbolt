namespace ReelBolt.Shared.Agents;

/// <summary>
/// A coherent, reusable bundle of agent tool/function names. This is the single source of truth
/// for "which tools does an agent type get" — both <c>AgentToolProvider.GetTools</c> (the real,
/// load-bearing WorkflowEngine scoping decision that actually constructs the runnable
/// <c>AIFunction</c>s) and <c>DatabaseSeeder.GetAvailableToolsJson</c> (the Inference API's
/// display-only "Available Tools" metadata) now derive their answer from
/// <see cref="ToolGroupCatalog"/> instead of maintaining two hand-written mappings that can (and,
/// twice, did) drift apart. See <see cref="ToolGroupCatalog"/> for the actual per-<c>AgentType</c>
/// scoping decision and the group→function-name membership.
/// </summary>
public enum ToolGroup
{
    /// <summary>
    /// ListProjectFiles, ReadProjectFile, ReadProjectFileLines, GetProjectFileOutline,
    /// SearchProjectFiles, GetDeterministicContextFiles — read-only project-file context. Granted to
    /// every real (non-deterministic) agent type.
    /// </summary>
    ProjectRead,

    /// <summary>
    /// WriteProjectFile, EditProjectFile — persist a new project file, or a new version of an
    /// existing one by replacing a snippet. Granted only to agents that produce a project-file
    /// artifact of their own (RemotionComponentTranslator, AuthorAgent).
    /// </summary>
    ProjectWrite,

    /// <summary>
    /// GetSandboxStatus, ListSandboxFiles, ReadSandboxFile — read-only sandbox inspection,
    /// reused verbatim by every agent type that ever looks inside the sandbox.
    /// </summary>
    SandboxBrowse,

    /// <summary>
    /// GetSandbox — full sandbox metadata. A step up from <see cref="SandboxBrowse"/>;
    /// Director/Scriptwriter deliberately do NOT get this group (see
    /// <see cref="ToolGroupCatalog.GroupsFor"/>), every other sandbox-touching agent does.
    /// </summary>
    SandboxMetadata,

    /// <summary>
    /// CheckLintAndTypeErrors — an objective TypeScript/lint quality signal.
    /// </summary>
    SandboxLint,

    /// <summary>
    /// EnsureSandbox, WriteSandboxFile, DeleteSandboxPath, InstallNpmPackages,
    /// RunSandboxNpmScript — full sandbox code-authoring access (create/resume the sandbox,
    /// write/delete files, install packages, run build/lint scripts), short of rendering.
    /// </summary>
    SandboxAuthoring,

    /// <summary>
    /// RunSandboxRemotionCommand, RenderVideoAndUploadToStorage, GetCompositionInfo,
    /// RenderPreviewStills (CompleteSandbox is deliberately not granted) — the render step and the
    /// cheap checks that come before it. Granted only to AuthorAgent (the pipeline's FINAL deliverable
    /// video) and MotionGraphicsPlanner (a small, optional overlay ASSET — never the final
    /// video).
    /// </summary>
    SandboxRender,

    /// <summary>
    /// FailWorkflow — abort the workflow with a human-readable reason. Granted to every real
    /// (non-deterministic) agent type.
    /// </summary>
    WorkflowControl,

    /// <summary>
    /// ReadDesignSystem, ReadProductProfile (SPEC §12) — read-only access to the brand design system
    /// and product profile pinned to the running execution. Appended LAST: the enum is append-only
    /// and its members are persisted by name.
    /// </summary>
    DesignSystemRead,

    /// <summary>
    /// GetCompositionInfo, RenderPreviewStills — look at a composition without delivering anything:
    /// list its compositions and render a few scratch preview stills (deleted afterwards, never
    /// uploaded), with what each frame looks like when a Vision provider is configured. Granted only
    /// to ReviewAgent, so the reviewer can see the frames it scores; every agent that may render gets
    /// the same two tools through <see cref="SandboxRender"/> instead. Appended LAST: the enum is
    /// append-only and its members are persisted by name.
    /// </summary>
    SandboxPreview,
    /// <summary>
    /// WebSearch — one live web search through the workspace's configured WebSearch provider; the
    /// results are untrusted third-party text and the URLs are shown, never fetched. Granted to the
    /// four writing/planning agents (ScriptwriterAgent, DirectorAgent, ShotDirector, NarrationWriter)
    /// and to no custom agent. A step whose agent holds it is never served from the step cache.
    /// Appended LAST: the enum is append-only and its members are persisted by name.
    /// </summary>
    WebSearch,

    /// <summary>
    /// ListBlocks, GetBlock, AddBlock — the first-party block library (inference/blocks/): browse a
    /// hash-verified catalog of transitions, titles, captions, charts, maps, effects, backgrounds, devices
    /// and shapes, and copy one into <c>src/blocks/</c> of the run's workspace. A sandbox writer, so a step
    /// holding it is never cached. Appended LAST: the enum is append-only and its members are persisted by name.
    /// </summary>
    SandboxBlocks,

    /// <summary>
    /// ListVideoEvents, DescribeVideoWindow: free, read-only windows onto the detected events of the run's video analysis
    /// (progressive context, docs/video-editing.md "Outline and windows"). Granted to the planning agents that read an
    /// analysis (VideoStoryEditor, SoundDesigner, MotionGraphicsPlanner); they read only this execution's own analysis,
    /// at most six calls a turn. Appended LAST: the enum is append-only and its members are persisted by name.
    /// </summary>
    EventsRead,

    /// <summary>
    /// ReadVideoSection, ReadVideoTranscript, ListVideoSfxClips, ListVideoMusic: free, read-only windows onto the speech
    /// outline of the run's video analysis (docs/video-editing.md "Speech outline"). Granted to the story editor (the edit
    /// director has project read and workflow control only). Appended LAST: the enum is append-only and its members are persisted by name.
    /// </summary>
    FootageRead,

    /// <summary>
    /// ListVideoSfxClips, ListVideoMusic, ReadVideoSection: the footage tools minus the transcript reader, for the agents that
    /// choose clips and tracks (the sound designer). Appended LAST: the enum is append-only and its members are persisted by name.
    /// </summary>
    OfferListRead,

    /// <summary>
    /// LookAtMoment: asks the picture model what one detected moment of the run's video analysis shows, on the three
    /// frames the analysis kept for it (docs/video-editing.md "Looking at one moment"). It spends (one small Vision call),
    /// so it is capped per step and per run and granted only to the three agents that decide on moments (SoundDesigner,
    /// VideoStoryEditor, MotionDirector). Appended LAST: the enum is append-only and its members are persisted by name.
    /// </summary>
    MomentLook,
}
