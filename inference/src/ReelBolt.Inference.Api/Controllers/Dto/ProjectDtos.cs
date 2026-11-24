using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// <c>POST /api/v1/projects</c>. <see cref="StarterTemplateKey"/> is optional (docs/design/new-user-funnel.md
/// section 6): the key of a listed recipe the project starts with INSTEAD of the automatic code-promo
/// starter. A key that is not a recipe this deployment lists is a 400 and nothing is created.
/// </summary>
public record CreateProjectRequest(
    string Name, string? Description, IReadOnlyList<Guid>? TagIds = null, Guid? FolderId = null, string? StarterTemplateKey = null);
/// <summary>
/// <see cref="AnalyzeOnUpload"/> is the project's "analyse videos when they are uploaded" choice: null (or left out)
/// keeps the stored value, so a client that only renames a project never changes it.
/// </summary>
public record UpdateProjectRequest(string Name, string? Description, bool? AnalyzeOnUpload = null);

/// <summary>
/// Moves a project into another organization (A10). The caller must be Owner/Admin in BOTH — the
/// project's current organization (checked against the active membership) and
/// <see cref="OrganizationId"/>, which is looked up in <c>organization_memberships</c> because the
/// token only ever carries the ACTIVE organization's role.
/// </summary>
public record TransferProjectRequest(Guid OrganizationId);

/// <summary>The project's new organization after a successful transfer.</summary>
public record TransferProjectResponse(Guid Id, Guid OrganizationId);

public record ProjectResponse(
    Guid Id, string Name, string? Description, string Status,
    DateTime CreatedAt, DateTime UpdatedAt,
    Guid? FolderId = null, IReadOnlyList<ProjectTagRef>? Tags = null,
    // The project's choice to analyse each new video when it is uploaded (off by default).
    bool AnalyzeOnUpload = false);

public record ProjectTagRef(Guid Id, string Name, string Color);

public record ProjectTagRequest(string? Name, string? Color);
public record ProjectTagResponse(Guid Id, string Name, string Color, int ProjectCount, DateTime CreatedAt);
public record ProjectFolderRequest(string? Name);
public record ProjectFolderResponse(Guid Id, string Name, int ProjectCount, DateTime CreatedAt);
public record ProjectOrganizationRequest(IReadOnlyList<Guid>? TagIds, Guid? FolderId);
public record OrganizeProjectsRequest(
    IReadOnlyList<Guid>? ProjectIds, IReadOnlyList<Guid>? AddTagIds, IReadOnlyList<Guid>? RemoveTagIds,
    Guid? FolderId = null, bool ClearFolder = false);

public record ProjectFileResponse(
    Guid Id,
    string OriginalFileName,
    string? OriginalPath,
    string? DirectoryPath,
    string Category,
    string StorageKey,
    string? StorageFileName,
    string MimeType,
    long SizeBytes,
    string? AgentSummary,
    string SummaryStatus,
    string IndexingStatus,
    DateTime? IndexedAt,
    string? IndexingError,
    DateTime UploadedAt,
    string ModerationStatus);

/// <summary>
/// <c>POST …/files/upload-url</c>: the browser asks for permission and a place to PUT. The size and
/// content type are the CALLER's claims — nothing has been uploaded yet — so they are checked
/// against the plan quota and the size ceiling as claims, and re-checked against the object's real
/// byte size at <c>upload-complete</c>.
/// </summary>
public record CreateDirectUploadRequest(
    string FileName,
    long SizeBytes,
    string? ContentType = null,
    string? RelativePath = null);

/// <summary>
/// The permission slip: <see cref="UploadToken"/> is the server's MAC over every field below, so
/// finalisation can prove the key was one this server issued to this caller for this project.
/// <see cref="Mode"/> is <c>singlePut</c> (one presigned PUT to <see cref="Url"/>, a bearer capability
/// returned to the client and never logged; <see cref="ContentType"/> is the value the PUT must send,
/// exactly, because it is a signed header) or <c>multipart</c> (<see cref="Url"/> is null; ask
/// <c>upload-parts</c> for part URLs, PUT <see cref="PartCount"/> parts of <see cref="PartSizeBytes"/>
/// bytes — the last one shorter — read each part's <c>ETag</c>, and send them to <c>upload-complete</c>).
/// The client echoes every field back on <c>upload-parts</c>, <c>upload-complete</c> and <c>upload-abort</c>.
/// </summary>
public record DirectUploadTicket(
    string UploadToken,
    string Key,
    Guid FileId,
    string? Url,
    long ExpiresAtUnix,
    string ContentType,
    long MaxBytes,
    string Mode = "singlePut",
    string? UploadId = null,
    long PartSizeBytes = 0,
    int PartCount = 0,
    string? FileName = null,
    string? RelativePath = null,
    long SizeBytes = 0);

/// <summary>One uploaded part of a multipart upload: its 1-based number and the <c>ETag</c> storage answered the PUT with.</summary>
public record DirectUploadPartDto(int PartNumber, string ETag);

/// <summary>
/// <c>POST …/files/upload-complete</c>: the browser says it is done. Every field is re-checked
/// against the ticket — <see cref="SizeBytes"/> and <see cref="ContentType"/> are hints used for a
/// consistency check, and the object's real size comes from a HEAD, never from here. A multipart
/// upload also echoes <see cref="Mode"/>, <see cref="UploadId"/>, <see cref="PartSizeBytes"/> and
/// <see cref="PartCount"/>, and lists every part in <see cref="Parts"/>.
/// </summary>
public record CompleteDirectUploadRequest(
    string UploadToken,
    string Key,
    Guid FileId,
    string FileName,
    long SizeBytes,
    long ExpiresAtUnix,
    string? ContentType = null,
    string? RelativePath = null,
    string? Mode = null,
    string? UploadId = null,
    long PartSizeBytes = 0,
    int PartCount = 0,
    IReadOnlyList<DirectUploadPartDto>? Parts = null);

/// <summary><c>POST …/files/upload-parts</c>: the ticket's fields echoed, and the part numbers (at most 100) to sign URLs for.</summary>
public record DirectUploadPartsRequest(
    string UploadToken,
    string Key,
    Guid FileId,
    string FileName,
    long SizeBytes,
    long ExpiresAtUnix,
    string? ContentType,
    string? RelativePath,
    string? Mode,
    string? UploadId,
    long PartSizeBytes,
    int PartCount,
    IReadOnlyList<int> PartNumbers);

/// <summary><c>POST …/files/upload-abort</c>: the ticket's fields echoed.</summary>
public record AbortDirectUploadRequest(
    string UploadToken,
    string Key,
    Guid FileId,
    string FileName,
    long SizeBytes,
    long ExpiresAtUnix,
    string? ContentType = null,
    string? RelativePath = null,
    string? Mode = null,
    string? UploadId = null,
    long PartSizeBytes = 0,
    int PartCount = 0);

public record ProjectFileContentResponse(
    Guid Id,
    string OriginalFileName,
    string? OriginalPath,
    string MimeType,
    string Content,
    DateTime UploadedAt);

public record UpdateProjectFileContentRequest(string Content, string? ContentType = null);

public record MoveProjectFilesRequest(
    List<Guid> FileIds,
    string? TargetDirectoryPath,
    string? TargetCategory = null);

public record FolderPathRequest(string Path);

public record RenameFolderRequest(string SourcePath, string TargetPath);

/// <summary><c>PATCH /api/v1/projects/{projectId}/files/{fileId}</c>: the file's new display name (no folders).</summary>
public record RenameProjectFileRequest(string? FileName);

public record FolderDeleteRequest(string Path, bool Recursive = false);

/// <summary>
/// <c>GET /api/v1/projects/{projectId}/files/{fileId}/picture-usage</c>: how many pictures (image-editor
/// documents) use this file's photo in one of their layers. Each of them keeps its own copy when the file
/// is deleted (decision D10), so the Files delete dialog can say so before the user confirms.
/// </summary>
/// <param name="Pictures">Live pictures with at least one layer showing this file; 0 when none.</param>
public record PictureUsageResponse(int Pictures);

public record SearchProjectFilesRequest(string Query, int Limit = 5);

public record SearchProjectFileChunkResult(
    Guid FileId,
    string FilePath,
    string FileName,
    int ChunkIndex,
    int TotalChunks,
    string Language,
    string Content,
    float Score);

public record SearchProjectFilesResponse(List<SearchProjectFileChunkResult> Results, bool IndexNotReady);

public record ReindexProjectFilesRequest(int MaxFiles = 25, bool IncludeIndexed = false);

public record ReindexProjectFilesResponse(int QueuedFiles, int EligibleFiles, bool HasMore);

public record AgentDefinitionResponse(
    Guid Id, string Name, string Description, string SystemPrompt,
    string AgentType, bool IsBuiltIn, Guid? OwnerId, string? ConfigJson,
    DateTime CreatedAt, string? Color, string? OutputSchemaJson,
    string[]? AvailableTools, bool GeneratesOutput, string? OutputSchemaName,
    Guid? InferenceProviderId = null, string? InferenceProviderName = null,
    // Raw column value, deserialized. Null/absent for built-in agents always (they have no
    // column value that means anything). For custom agents: null or empty = no skills assigned.
    string[]? AssignedSkills = null,
    // The resolved set: SkillCatalog.DefaultsFor(agentType) for a built-in row, or
    // AssignedSkills (empty if null) for a custom row. Computed server-side so the frontend
    // never has to reimplement the resolution rule.
    string[]? EffectiveSkills = null,
    // The organization that owns a custom agent (A8). Null for a built-in row, which is shared by
    // every organization — the same convention as inference_providers.organization_id.
    Guid? OrganizationId = null,
    // The stored reasoning effort (none/low/medium/xhigh), or null = "use the code default".
    string? ReasoningEffort = null);

public record CreateAgentRequest(string Name, string Description, string SystemPrompt, string? ConfigJson, string? Color);
public record UpdateAgentRequest(string Name, string Description, string SystemPrompt, string? ConfigJson, string? Color);

public record WorkflowDefinitionResponse(
    Guid Id, string Name, DateTime CreatedAt, DateTime UpdatedAt,
    List<WorkflowStepResponse> Steps,
    bool RequiresUserInput = false);

public record WorkflowStepResponse(
    Guid Id, Guid AgentDefinitionId, int StepOrder,
    string? EdgeConditionJson, string? Label,
    string StepType = "Agent",
    string? ConditionExpression = null,
    string? LoopSourceExpression = null,
    int? LoopTargetStepOrder = null,
    int MaxIterations = 3,
    int? MinScore = null,
    string? InputMappingJson = null,
    string? AgentInputContextMode = null,
    string? SelectedPriorStepOrdersJson = null,
    string? TrueBranchStepOrder = null,
    string? FalseBranchStepOrder = null,
    string? ParallelAgentIdsJson = null,
    string? ExtractConfigJson = null,
    string? VideoAnalyzeConfigJson = null,
    string? VideoCompileConfigJson = null,
    string? EditRoomConfigJson = null,
    string? GraphicsRoomConfigJson = null,
    string? ColorGradeRoomConfigJson = null,
    string? ConditionalConfigJson = null,
    string? ReviewLoopConfigJson = null,
    string? CacheMode = null,
    string? VideoGenerateConfigJson = null,
    string? VoiceoverConfigJson = null,
    string? ThumbnailGenerateConfigJson = null,
    string? DailiesRoomConfigJson = null,
    string? TimelineRenderConfigJson = null,
    string? DesignSystemConfigJson = null,
    string? ContinueCompositionJson = null,
    bool RegisterOutputAsProjectFile = false,
    string? ExtractFrameConfigJson = null,
    string? AudioGenerateConfigJson = null,
    string? PinInsertConfigJson = null,
    string? AudioCleanupConfigJson = null,
    string? VideoEnhanceConfigJson = null,
    string? ImageGenerateConfigJson = null);

public record CreateWorkflowRequest(
    string Name, List<CreateWorkflowStepRequest> Steps, bool RequiresUserInput = false,
    // The app manifest (docs/workflow-apps.md). Omitted from the serialized request when unset, so every proposal that
    // does not use it hashes to the same ApplyWorkflow confirmation-token payload it always did.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ReelBolt.Shared.Workflows.Apps.AppManifest? AppManifest = null);

/// <summary>Optional body for the execute endpoint. UserRequest is null when the workflow runs without user input.</summary>
/// <summary>
/// Starts a run. Beyond the brief, a run may carry ONE editing style — a saved style
/// (<see cref="StyleId"/>), a built-in preset (<see cref="StylePresetKey"/>) or a one-off style
/// (<see cref="Style"/>) — and an <see cref="OutputFormat"/> override ("Original", "Vertical",
/// "Square", "Landscape", "Portrait", or a ratio such as "9:16"), and an <see cref="OutputResolution"/>
/// ("Auto", "720p", "1080p" or "2160p"/"4K", short side; above the plan is refused with 402).
/// See docs/editing-styles.md. <see cref="AcceptEstimateCredits"/> is read only for the
/// product-profile-from-code template: the run starts only when it is at least the estimate's
/// high figure, in whole credits; for any other workflow it is ignored. <see cref="BriefFileId"/> names a text
/// file of THIS project (up to 64 KiB) whose content is the run's brief; a <see cref="UserRequest"/> sent with it
/// is appended after the file under a "Notes for this run:" line. Read only when a run starts (a retry reuses the
/// stored text).
/// </summary>
public record ExecuteWorkflowRequest(
    string? UserRequest = null,
    bool RegenerateVideoClips = false,
    Guid? StyleId = null,
    string? StylePresetKey = null,
    StyleConfig? Style = null,
    string? OutputFormat = null,
    string? OutputResolution = null,
    double? AcceptEstimateCredits = null,
    Guid? BriefFileId = null,
    // The typed run brief (docs/run-brief.md). The legacy members above keep working and are read as the same
    // brief fields; sending both a legacy member and the same brief field is a 400. Null = no brief.
    RunBrief? Brief = null);

public record CreateWorkflowStepRequest(
    Guid AgentDefinitionId, int StepOrder, string? EdgeConditionJson, string? Label,
    string? StepType = null,
    string? ConditionExpression = null,
    string? LoopSourceExpression = null,
    int? LoopTargetStepOrder = null,
    int? MaxIterations = null,
    int? MinScore = null,
    string? InputMappingJson = null,
    string? AgentInputContextMode = null,
    string? SelectedPriorStepOrdersJson = null,
    string? TrueBranchStepOrder = null,
    string? FalseBranchStepOrder = null,
    string? ParallelAgentIdsJson = null,
    string? ExtractConfigJson = null,
    string? VideoAnalyzeConfigJson = null,
    string? VideoCompileConfigJson = null,
    string? EditRoomConfigJson = null,
    string? GraphicsRoomConfigJson = null,
    string? ColorGradeRoomConfigJson = null,
    string? ConditionalConfigJson = null,
    string? ReviewLoopConfigJson = null,
    string? CacheMode = null,
    string? VideoGenerateConfigJson = null,
    string? VoiceoverConfigJson = null,
    string? ThumbnailGenerateConfigJson = null,
    string? DailiesRoomConfigJson = null,
    string? TimelineRenderConfigJson = null,
    string? DesignSystemConfigJson = null,
    // Step options. Omitted from the serialized request when unset, so every proposal that does not
    // use them hashes to the same ApplyWorkflow confirmation-token payload it always did.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    string? ContinueCompositionJson = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool RegisterOutputAsProjectFile = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    string? ExtractFrameConfigJson = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    string? AudioGenerateConfigJson = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    string? PinInsertConfigJson = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    string? AudioCleanupConfigJson = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    string? VideoEnhanceConfigJson = null,
    string? ImageGenerateConfigJson = null);

public record UpdateWorkflowRequest(
    string? Name, List<CreateWorkflowStepRequest> Steps, bool? RequiresUserInput = null,
    // The app manifest; null leaves the stored one unchanged.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ReelBolt.Shared.Workflows.Apps.AppManifest? AppManifest = null,
    // True removes the stored manifest, so the app is derived from the steps again. Cannot be combined with AppManifest.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool ClearAppManifest = false);

public record WorkflowTemplateSummaryResponse(
    string Key,
    string Name,
    string Description,
    int Version,
    bool AutoCreateOnProject,
    bool RequiresUserInput,
    int StepCount,
    bool RequiresProfile = false);

public record ApplyWorkflowTemplateRequest(bool? SkipIfExists = null);

public record WorkflowExecutionResponse(
    Guid Id, Guid WorkflowDefinitionId, string Status,
    DateTime? StartedAt, DateTime? CompletedAt, int IterationCount,
    string? ResultJson, string? CorrelationId, string? ErrorMessage,
    List<StepResultResponse> StepResults,
    List<ReviewScoreResponse> ReviewScores,
    string? UserRequest = null,
    string? StyleName = null,
    string? OutputFormat = null,
    DesignSystemPin? DesignSystemPin = null,
    string? OutputResolution = null,
    // The steps this run executed: its engine-taken snapshot's, which StepResults[].WorkflowStepId
    // reference. Set by GET .../executions/{id} only; null for a run without a snapshot, whose results
    // reference the workflow's own steps.
    List<WorkflowStepResponse>? Steps = null,
    // T-004: true iff POST .../executions/{id}/resume would pass its run checks (Failed/Cancelled, an
    // engine-taken snapshot with its agents recorded); credits, plan and a still-live lease are not
    // evaluated. False for a legacy run: only Retry can run it again.
    bool CanResume = false,
    // How many times this run was resumed, manually or by the recovery sweeper.
    int ResumeCount = 0,
    // The name of the workflow the run was started from, also after that workflow was deleted (the run
    // is then filed under its hidden snapshot, which carries the source name). Set by GET .../executions/{id}.
    string? WorkflowName = null,
    // The composed run brief: the stored fields plus the style, tone, format and resolution read back from their
    // columns. Null when the run used none of them.
    RunBrief? Brief = null,
    // Plain-language notes about the design this run was pinned to (it has no tokens, or a newer draft was
    // waiting for review). Advice only, never a block. Set by GET .../executions/{id}; null otherwise.
    IReadOnlyList<string>? DesignSystemWarnings = null,
    // Set when the run Passed but a review loop stopped below its minimum score (status stays Passed). Null otherwise.
    ExecutionAttention? NeedsAttention = null,
    // App inputs (docs/design/app-inputs.md 10.4): the run's app fields in words, labelled by its own page. Left out
    // when the run set none, so a run without app inputs reads exactly as before.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ReelBolt.Inference.Api.Services.Briefs.RunAppInputResponse>? AppInputs = null);

/// <summary>
/// Body of <c>POST .../executions/{id}/resume</c> (optional). A resume keeps the run's own request, style,
/// format and resolution. <see cref="AcceptEstimateCredits"/> is read only for a run of the
/// product-profile-from-code template, exactly as on Execute and Retry: the resume starts only when it
/// is at least the estimate's high figure, in whole credits; for any other workflow it is ignored.
/// </summary>
public record ResumeExecutionRequest(double? AcceptEstimateCredits = null);

public record StepResultResponse(
    Guid Id, Guid WorkflowStepId, string Output, int TokensUsed,
    long DurationMs, DateTime ExecutedAt,
    string? InputJson = null, string? OutputJson = null,
    string? Status = null, string? ErrorDetails = null,
    int? IterationNumber = null, DateTime? CompletedAt = null,
    string? OutputStorageKey = null,
    string? ArtifactStorageKey = null,
    string? ToolCallsJson = null,
    string? ReasoningJson = null,
    string? ChatTranscriptJson = null,
    bool FromCache = false,
    int? CachedTokensSaved = null,
    // E8-UI: where this step actually ran (D6/D12, persisted on
    // workflow_step_results). ComputeTarget is the ComputeTargetKind NAME — "Local"
    // (the engine ran it in-process: the only value a deployment without the
    // compute fabric can produce), "CloudPool" (one of our workers) or "OrgRunner"
    // (the workspace's own paired machine) — and it is what the dashboard renders;
    // FromRunner is the persisted tag, which is DERIVED from the target
    // (target == OrgRunner) so the two can never disagree. RunnerId/RunnerJobId
    // are null unless a runner was involved.
    string? ComputeTarget = null,
    bool FromRunner = false,
    Guid? RunnerId = null,
    Guid? RunnerJobId = null,
    // Set when a composition revision copied this result from the run it changed instead of running
    // the step (docs/design/composition-revisions.md): the id of the result it was copied from.
    Guid? ReusedFromStepResultId = null,
    // T-003 run read model (positional append): what the step cost in whole credits (millicredits / 1000;
    // null while the step has not finished), the machine-readable failure code and the class of who bore
    // it (FailureClass name), and which step of the workflow this result belongs to.
    decimal? CreditsCharged = null,
    string? FailureCode = null,
    string? FailureClass = null,
    int? StepOrder = null,
    string? StepLabel = null,
    string? StepType = null,
    // True on a ?detail=summary read: Output, InputJson, ToolCallsJson, ReasoningJson, ChatTranscriptJson and OutputJson
    // (except a bare warnings object) were left out; the full result is GET .../step-results/{resultId}.
    bool DetailOmitted = false);

public record ReviewScoreResponse(
    Guid Id, int IterationNumber, int Score, string Comments, DateTime CreatedAt);
