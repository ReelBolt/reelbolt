namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A durable Remotion project: the source of truth a finished film can be revised from
/// (docs/design/composition-revisions.md). One workspace per LINEAGE — it is created for the run that
/// first rendered the film (<see cref="OriginExecutionId"/>), and every revision of that film, and
/// every revision of a revision, appends a <see cref="CompositionWorkspaceVersion"/> to it.
///
/// <para>
/// Owned by the Inference API (migration <c>AddCompositionWorkspaces</c>); mapped by the WorkflowEngine
/// with <c>ExcludeFromMigrations()</c>, which inserts the rows at capture time — the same
/// cross-service write the API already does into the engine-owned <c>workflow_executions</c>.
/// No tenant query filter: it has no caller-addressable route of its own and every read path reaches
/// it through a project the caller was authorized for (the <c>ProjectVoice</c> rule, asserted in
/// <c>TenantBoundaryInventoryTests</c>). <see cref="OrganizationId"/> is denormalized for metering
/// and retention, never as a guard.
/// </para>
/// </summary>
public class CompositionWorkspace
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>The organization that owns the project, denormalized at creation. Never an authorization input.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The execution whose render started this lineage. Unique: one workspace per origin run.</summary>
    public Guid OriginExecutionId { get; set; }

    /// <summary>The workflow that origin run belonged to (informational; the workflow may since have been deleted).</summary>
    public Guid? OriginWorkflowDefinitionId { get; set; }

    /// <summary>The highest <see cref="CompositionWorkspaceVersion.VersionNumber"/> appended so far (0 when none yet).</summary>
    public int LatestVersionNumber { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One immutable snapshot of a <see cref="CompositionWorkspace"/>: a gzip-compressed tar of the
/// sandbox project (sources, manifest/lockfile, public assets, config — never <c>node_modules</c>,
/// <c>out/</c> or caches) at <see cref="StorageKey"/>, plus what is known about the composition it
/// renders. Append-only: a row is written once and never updated, so the chain of
/// <see cref="ParentVersionId"/> links is a faithful history.
/// </summary>
public class CompositionWorkspaceVersion
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Denormalized so every read can be scoped by project without a join (the guard is the project).</summary>
    public Guid ProjectId { get; set; }

    /// <summary>1-based, unique within the workspace, assigned in append order.</summary>
    public int VersionNumber { get; set; }

    /// <summary>The version this one was made FROM; null for the first version of a lineage.</summary>
    public Guid? ParentVersionId { get; set; }

    /// <summary><c>projects/{projectId}/agentFiles/workspaces/{workspaceId}/{versionId}.tar.gz</c> — see <c>CompositionWorkspaceKeys</c>.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the archive bytes, computed by the engine (never by the sandbox).</summary>
    public string Sha256 { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Regular files in the archive (as the sandbox counted them).</summary>
    public int FileCount { get; set; }

    public CompositionVersionSource Source { get; set; } = CompositionVersionSource.RenderCapture;

    /// <summary>The composition the version renders (the id passed to the render tool), when known.</summary>
    public string? CompositionId { get; set; }
    public double? Fps { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? DurationInFrames { get; set; }

    /// <summary>The <c>CompositionSceneManifest</c> of the render this version produced, as JSON (jsonb). Null when none was written.</summary>
    public string? SceneManifestJson { get; set; }

    /// <summary>The playable render this version produced (an <c>outputFiles</c> key); null for a preview-only snapshot.</summary>
    public string? RenderOutputStorageKey { get; set; }

    /// <summary>The execution that produced this version (the origin run, or a revision run).</summary>
    public Guid? CreatedByExecutionId { get; set; }

    /// <summary>The user who asked for it (the run's initiator); null for engine-internal captures with no initiator.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>The <see cref="CompositionRevision"/> that produced this version; null for an origin capture.</summary>
    public Guid? RevisionId { get; set; }

    /// <summary>The plain-language change request that produced this version; null for the original render.</summary>
    public string? Instruction { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A request to change a finished film: the instruction, its optional targets, the version it starts
/// from, and the execution that carries it out. Written by the Inference API when the request is
/// accepted; the engine reads it (by <see cref="ExecutionId"/>) and links <see cref="ResultVersionId"/>
/// once the revision has produced a version. The run's status lives on the execution row, never here.
/// </summary>
public class CompositionRevision
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>The run the user asked for changes on (an origin run or an earlier revision run).</summary>
    public Guid SourceExecutionId { get; set; }

    /// <summary>
    /// The version to start from. Null only for a run made before workspaces were captured: the engine
    /// then recovers the project from <see cref="SourceExecutionId"/>'s sandbox (while it still exists)
    /// and records that as the first version before revising it.
    /// </summary>
    public Guid? BaseVersionId { get; set; }

    /// <summary>The revision's own execution (unique). Its sandbox is keyed by this id.</summary>
    public Guid ExecutionId { get; set; }

    /// <summary>The project's hidden system workflow (<see cref="WorkflowDefinitionKind.CompositionRevision"/>) the execution runs.</summary>
    public Guid WorkflowDefinitionId { get; set; }

    public string Instruction { get; set; } = string.Empty;

    /// <summary>A serialized <c>CompositionRevisionTargets</c> (jsonb); null = the whole film.</summary>
    public string? TargetsJson { get; set; }

    public CompositionRevisionMode Mode { get; set; } = CompositionRevisionMode.Render;

    public Guid RequestedByUserId { get; set; }

    /// <summary>The version this revision produced; null until (and unless) it does.</summary>
    public Guid? ResultVersionId { get; set; }

    /// <summary>
    /// What the revision does about the editorial decisions that depend on the film
    /// (<see cref="ReelBolt.Shared.Workflows.Replay.CompositionCutPolicy"/>): keep the approved cut, or
    /// re-edit. Null for a revision with nothing to replay (the film is the deliverable) and for every
    /// request made before replays existed. Persisted by name (migration <c>AddCompositionRevisionReplay</c>).
    /// </summary>
    public ReelBolt.Shared.Workflows.Replay.CompositionCutPolicy? CutPolicy { get; set; }

    /// <summary>
    /// The serialized <see cref="ReelBolt.Shared.Workflows.Replay.CompositionReplayPlan"/> (jsonb): which
    /// steps of the base version's run are reused, which one is revised and which run again so the
    /// revision ends with the workflow's real deliverable. Null when nothing depends on the film — the
    /// revision is then the one-step run it always was, on the project's shared hidden workflow.
    /// </summary>
    public string? ReplayPlanJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>How a <see cref="CompositionWorkspaceVersion"/> came to exist. Persisted by name; append-only.</summary>
public enum CompositionVersionSource
{
    /// <summary>Snapshotted right after a successful render (the normal path, original or revision).</summary>
    RenderCapture,

    /// <summary>Recovered from an earlier run's sandbox that predates capture (best effort, within the sandbox TTL).</summary>
    SandboxRecovery,

    /// <summary>Snapshotted after a preview-only revision: edited, checked with stills, never rendered.</summary>
    PreviewSnapshot,

    /// <summary>
    /// The source of a still GRAPHIC designed for a picture document (StepType.DesignGraphic): a
    /// Remotion <c>Still</c> on a transparent frame, whose render is a PNG layer rather than a film.
    /// A lineage holding one of these is a graphic, never a film, so the film revision front-end
    /// refuses it; it is changed from the picture editor ("Change this graphic").
    /// </summary>
    GraphicDesign
}

/// <summary>What a revision delivers. Persisted by name; append-only.</summary>
public enum CompositionRevisionMode
{
    /// <summary>Edit, verify, render once, upload, record a version linked to the render.</summary>
    Render,

    /// <summary>
    /// Edit and verify with preview stills only; record a version with no render. The low-latency
    /// shape the editor-v2 live front-end is meant to drive (docs/design/composition-revisions.md).
    /// </summary>
    PreviewOnly
}

/// <summary>
/// What a <see cref="WorkflowDefinition"/> is for. Persisted as an int (engine migration
/// <c>AddWorkflowDefinitionKind</c>, default <see cref="Standard"/>); append-only.
/// </summary>
public enum WorkflowDefinitionKind
{
    /// <summary>A workflow a user (or a template) authored. Every pre-existing row.</summary>
    Standard = 0,

    /// <summary>
    /// The project's hidden system workflow that carries composition revisions: one
    /// <see cref="StepType.Revise"/> step. Never listed, edited, deleted or executed directly — a
    /// revision is requested through <c>POST .../executions/{id}/revisions</c>.
    /// </summary>
    CompositionRevision = 1,

    /// <summary>
    /// The project's hidden system workflow that designs still graphics for picture documents: one
    /// <see cref="StepType.DesignGraphic"/> step. Hidden exactly like <see cref="CompositionRevision"/>;
    /// a graphic is requested through <c>POST .../image-documents/{id}/graphics</c>.
    /// </summary>
    GraphicDesign = 2,

    /// <summary>
    /// The hidden single-step <c>TimelineRender</c> workflow <c>POST …/edit-timelines/{id}/render</c>
    /// creates (editor v2, P4). Hidden from the authored list like the two above; unlike them a
    /// retry is harmless (a render is deterministic and spends no credits beyond compute), so the
    /// retry endpoint lets it through. See docs/editor.md "Rendering (P4)". Stored as 4: it was
    /// first declared as 3, the value <see cref="CompositionReplay"/> also took, so every render ran as
    /// a replay; migration <c>SplitTimelineRenderWorkflowKind</c> moved the stored rows. Values are
    /// persisted as integers and must stay distinct (<c>WorkflowDefinitionKindTests</c>).
    /// </summary>
    TimelineRender = 4,

    /// <summary>
    /// A per-revision snapshot of the workflow a revised film came from: the same steps at the same
    /// orders, with the film's render step replaced by a <see cref="StepType.Revise"/> step
    /// (docs/design/composition-revisions.md "Replaying what depends on the film"). One per revision
    /// that has downstream work — the steps are what the revision execution actually ran, so a later
    /// edit of the original workflow cannot change (or cascade-delete) its record. Hidden exactly like
    /// <see cref="CompositionRevision"/>.
    /// </summary>
    CompositionReplay = 3,

    /// <summary>
    /// The frozen copy of a <see cref="Standard"/> workflow one run executes, taken by the engine when
    /// the run starts (<c>workflow_executions.snapshot_definition_id</c>): the same steps at the same
    /// orders, so the run's results reference these steps and a later edit or delete of the user's
    /// workflow can neither change the run nor cascade-delete what it produced. One per run, deleted
    /// with it. Hidden from every list, get, edit, delete and execute path (REST, assistant, MCP,
    /// templates); the run itself stays filed under the source workflow.
    /// </summary>
    ExecutionSnapshot = 5,

    /// <summary>
    /// The hidden single-step <see cref="StepType.VideoAnalyze"/> workflow the editor's "object analysis"
    /// queues (find a text-named object in a project video and produce segmentation tracks a clip effect
    /// binds to). Hidden from the authored list like the system kinds above; its runs may be read by id like a
    /// <see cref="TimelineRender"/>, but a retry is refused (the request row carries the intent: ask again
    /// from the editor) and an account purge removes it with the project. Persisted as an integer; append-only.
    /// </summary>
    ObjectAnalysis = 6,

    /// <summary>
    /// The hidden single-step <c>AudioCleanup</c> workflow <c>POST .../media/{mediaRef}/cleanup</c>
    /// creates. Hidden from every list, get, edit, delete and execute path like
    /// <see cref="TimelineRender"/>; its executions stay readable through the status poll. Appended after ObjectAnalysis:
    /// the integer is persisted (<c>WorkflowDefinitionKindTests</c>).
    /// </summary>
    AudioCleanup = 7,

    /// <summary>
    /// The hidden single-step <see cref="StepType.VideoEnhance"/> workflow <c>POST .../media/{mediaRef}/enhance</c> queues.
    /// Hidden from every list, get-as-workflow, edit, delete, execute, retry AND resume path (REST, assistant, MCP); its
    /// executions stay readable through the status poll. Appended after AudioCleanup (7). Persisted as an integer;
    /// append-only (<c>WorkflowDefinitionKindTests</c>).
    /// </summary>
    VideoEnhance = 8,

    /// <summary>
    /// The hidden single-step <see cref="StepType.VideoGenerate"/> workflow the editor's "Generate clip" queues
    /// (<c>POST .../subject-clips</c>, docs/design/avatar-objects.md "Generating a clip with avatars from the editor"): one
    /// inline step that shows up to three of the project's avatars and objects, its output registered as ordinary project
    /// video files. Hidden from every list, get-as-workflow, edit, delete, execute, retry AND resume path (REST, assistant,
    /// MCP) like <see cref="VideoEnhance"/>; its executions stay readable through <c>GET .../subject-clips/{executionId}</c>.
    /// Persisted as an integer, append-only (<c>WorkflowDefinitionKindTests</c>).
    /// </summary>
    SubjectClip = 9,

    /// <summary>
    /// A hidden workflow an avatar build job queues (docs/design/avatar-objects.md "Avatar variants" and "Pulling an
    /// object out of a video"): the free <c>ExtractFrame</c> grabs and the <c>VideoGenerate</c> takes of a variant's
    /// videos, and the frames cut out of a video for a new object avatar. Hidden from every list, get-as-workflow, edit,
    /// delete, execute, retry AND resume path (REST, assistant, MCP) like <see cref="VideoEnhance"/>; the build job reads
    /// its executions. Explicit value 10 (9 is <see cref="SubjectClip"/>); persisted as an integer, append-only
    /// (<c>WorkflowDefinitionKindTests</c>).
    /// </summary>
    AvatarBuild = 10
}
