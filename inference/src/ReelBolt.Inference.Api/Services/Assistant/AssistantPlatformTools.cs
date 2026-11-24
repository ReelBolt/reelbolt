using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>Whether a call of one assistant tool can be Platform-billed by a model call of its own.</summary>
public enum AssistantToolCostKind
{
    /// <summary>Reads or writes ReelBolt's own data only (or queues work charged elsewhere): no model call of its own.</summary>
    Free,

    /// <summary>Can call a Platform model itself, whatever model the chat runs on: pre-claimed before it runs.</summary>
    PlatformBilled
}

/// <summary>One tool's cost classification.</summary>
/// <param name="Kind">Free or Platform-billed.</param>
/// <param name="Capabilities">The capabilities a Platform-billed tool can call (at least one); empty for a free tool.</param>
/// <param name="Why">One line on why the tool is classified so (what it calls, or what it queues and who charges that).</param>
public sealed record AssistantToolCost(AssistantToolCostKind Kind, IReadOnlyList<InferenceProviderCapability> Capabilities, string Why);

/// <summary>
/// The one cost table of every assistant / MCP tool: each registered tool is classified here as
/// <see cref="AssistantToolCostKind.Free"/> or <see cref="AssistantToolCostKind.PlatformBilled"/>, with the capabilities
/// it can call. A Platform-billed tool is pre-claimed before it runs (<see cref="AssistantTurnGuard"/>: a look on its
/// vision quote for the actual picture, a picture tool at its dearest tier, the others at the unpriced default), and a
/// BYO-chat turn's first one is admitted under the workspace's in-flight cap. <b>Fail closed:</b> a tool name the table
/// does not list is treated exactly like a Platform-billed one (<see cref="IsPlatformBilled"/>), so a tool added or
/// renamed without an entry is never free, and the drift-guard tests (<see cref="Unclassified"/>, <see cref="Stale"/>)
/// fail the build until the table and the registered tool set agree.
/// </summary>
public static class AssistantPlatformTools
{
    private static readonly IReadOnlyList<InferenceProviderCapability> None = [];

    private const string Reads = "Reads ReelBolt's own data; no model call.";
    private const string Proposes = "Proposes a change and mints a confirmation token; writes nothing and calls no model.";
    private const string Writes = "Writes ReelBolt's own data through its own routes; no model call.";
    private const string SavesProvider = "Saves a provider row; may ask the Higgsfield price oracle for a quote, which is not a generation.";

    private static KeyValuePair<string, AssistantToolCost> Free(string name, string why) =>
        new(name, new AssistantToolCost(AssistantToolCostKind.Free, None, why));

    private static KeyValuePair<string, AssistantToolCost> Billed(string name, string why, params InferenceProviderCapability[] capabilities) =>
        new(name, new AssistantToolCost(AssistantToolCostKind.PlatformBilled, capabilities, why));

    /// <summary>Every registered assistant / MCP tool (ordinal), with its cost classification.</summary>
    public static readonly IReadOnlyDictionary<string, AssistantToolCost> Tools = new Dictionary<string, AssistantToolCost>(
        [
            // The core workflow surface.
            Free("ListAgents", Reads),
            Free("ListStepSchemas", Reads),
            Free("GetStepCatalog", "Reads the step catalog generated from code; no model call."),
            Free("GetProjectReadiness", Reads),
            Free("EstimateWorkflowRun", "Reads the cost estimate (the same one the run card shows); no model call and no hold."),
            Free("ProposeCreateFromTemplate", Proposes),
            Free("ApplyCreateFromTemplate", "Creates a workflow from a built-in recipe through its own route; no model call."),
            Free("ListProjectFiles", Reads),
            Free("ValidateWorkflow", "Validates a workflow definition locally; no model call."),
            Free("ProposeWorkflow", Proposes),
            Free("ApplyWorkflow", Writes),
            Free("ExecuteWorkflow", "Queues a run; the run is charged by the workflow credit gate, not the turn."),

            // The read surface.
            Free("ListProjects", Reads),
            Free("GetProject", Reads),
            Free("ListWorkflows", Reads),
            Free("ReadProjectFile", "Reads a stored project file; no model call."),
            Free("ReadProjectFileLines", "Reads a line range of a stored project file; no model call."),
            Free("GetProjectFileOutline", "Outlines a stored project file; no model call."),

            // Project, file and workflow mutations.
            Free("ProposeCreateProject", Proposes),
            Free("ApplyCreateProject", Writes),
            Free("ProposeUpdateProject", Proposes),
            Free("ApplyUpdateProject", Writes),
            Free("ProposeDeleteProject", Proposes),
            Free("ApplyDeleteProject", Writes),
            Free("ProposeSavePronunciation", Proposes),
            Free("ApplySavePronunciation", Writes),
            Free("SearchSoundEffects", "Searches the built-in sound effects library (platform content); no model call, no vendor call, no credits."),
            Free("ProposeAddSoundEffect", Proposes),
            Free("ApplyAddSoundEffect", "Copies a built-in library sound into the project as an ordinary audio file; no model call, no credits, not counted against storage."),
            Free("ProposeWriteProjectFile", Proposes),
            Free("ApplyWriteProjectFile", "Stores the file; its background summary and indexing are queued work, free by policy."),
            Free("ProposeEditProjectFile", Proposes),
            Free("ApplyEditProjectFile", "Saves a targeted edit of a project file in place; its background summary and indexing are queued work, free by policy."),
            Free("ProposeInsertProjectFileLines", Proposes),
            Free("ApplyInsertProjectFileLines", "Saves inserted lines of a project file in place; its background summary and indexing are queued work, free by policy."),
            Free("ProposeDeleteProjectFile", Proposes),
            Free("ApplyDeleteProjectFile", Writes),
            Free("ProposeDeleteWorkflow", Proposes),
            Free("ApplyDeleteWorkflow", Writes),
            Free("ProposeStopExecution", Proposes),
            Free("ApplyStopExecution", "Stops a run: no new work is started. Credits already used stay charged."),

            // The admin surface.
            Free("ListAgentsAdmin", Reads),
            Free("ListInferenceProviders", Reads),
            Billed("TestInferenceProvider", "Sends a probe call to the provider under test (chat, vision, decision, embedding, transcription or speech).",
                InferenceProviderCapability.Chat, InferenceProviderCapability.Vision, InferenceProviderCapability.Decision,
                InferenceProviderCapability.Embedding, InferenceProviderCapability.Transcription, InferenceProviderCapability.SpeechSynthesis),
            Free("ProposeCreateInferenceProvider", Proposes),
            Free("ApplyCreateInferenceProvider", SavesProvider),
            Free("ProposeUpdateInferenceProvider", Proposes),
            Free("ApplyUpdateInferenceProvider", SavesProvider),
            Free("ProposeDeleteInferenceProvider", Proposes),
            Free("ApplyDeleteInferenceProvider", Writes),
            Free("ListSkills", Reads),
            Free("ProposeAssignAgentSkills", Proposes),
            Free("ApplyAssignAgentSkills", Writes),
            Free("GetDecisionCalibration", Reads),

            // Knowledge and read-back.
            Billed("SearchPlatformDocs", "Embeds the query to search the documentation.", InferenceProviderCapability.Embedding),
            Free("ListWorkflowTemplates", Reads),
            Free("GetWorkflowTemplate", Reads),
            Free("GetWorkflow", Reads),
            Free("SearchMarketplaceRecipes", Reads),
            Free("GetMarketplaceRecipe", Reads),
            Free("ProposeCopyMarketplaceRecipe", Proposes),
            Free("ApplyCopyMarketplaceRecipe", "Copies a community recipe into the project as an ordinary workflow; starts no run and calls no model."),
            Free("ListWorkflowExecutions", Reads),
            Free("GetWorkflowExecution", Reads),

            // Editing styles.
            Free("ListStyles", Reads),
            Free("ProposeSaveStyle", Proposes),
            Free("ApplySaveStyle", Writes),

            // The picture editor.
            Free("DescribePictureDocument", Reads),
            Free("ListPictureTools", "Lists the picture tools and reads their prices; no model call."),
            Billed("LookAtPicture", "Sends a layer's pixels and a question to the vision model.", InferenceProviderCapability.Vision),
            Free("ProposeAddTextLayer", Proposes),
            Free("ApplyAddTextLayer", Writes),
            Free("ProposeEditPictureLayers", Proposes),
            Free("ApplyEditPictureLayers", Writes),
            Free("ProposeDeletePictureLayers", Proposes),
            Free("ApplyDeletePictureLayers", Writes),
            Free("ProposeChangePictureCanvas", Proposes),
            Free("ApplyChangePictureCanvas", Writes),
            Free("ProposeRunPictureTool", "Quotes the tool from its price row and mints a token; no model call."),
            Billed("ApplyRunPictureTool", "Runs one AI picture tool on its image vendor (fal and the others) or the segmenter.",
                InferenceProviderCapability.ImageErase, InferenceProviderCapability.ImageFill, InferenceProviderCapability.ImageExpand,
                InferenceProviderCapability.ImageBackgroundReplace, InferenceProviderCapability.ImageRelight,
                InferenceProviderCapability.ImageUpscale, InferenceProviderCapability.ImageEdit,
                InferenceProviderCapability.ImageGeneration, InferenceProviderCapability.Segmentation),
            Free("ProposeDesignGraphic", "Plans the graphic's frame and reads the workflow estimate; no model call."),
            Billed("ApplyDesignGraphic", "Queues the Picture graphics workflow (charged by its credit gate); kept billed and claimed at the default on purpose.",
                InferenceProviderCapability.Chat, InferenceProviderCapability.Vision),

            // The design system and product profile.
            Free("GetDesignSystem", "Reads the project's design system and product profile versions; no model call."),
            Free("ListDesignChanges", "Reads the pending design-system change proposals; no model call."),
            Free("ProposeSaveDesignSystem", "Validates the brand arguments locally and mints a token; writes nothing and calls no model."),
            Free("ApplySaveDesignSystem", "Writes a design-system draft through the design-system service; no model call."),
            Free("ProposeDecideDesignChange", Proposes),
            Free("ApplyDecideDesignChange", "Approves or rejects a stored change proposal through the design-system service; no model call."),

            // Project history (versions).
            Free("ListProjectVersions", Reads),
            Free("GetProjectChanges", "Compares stored version manifests by name; no model call."),
            Free("ProposeSaveProjectVersion", Proposes),
            Free("ApplySaveProjectVersion", "Captures a named version through the history service; no model call."),
            Free("ProposeRestoreProjectVersion", Proposes),
            Free("ApplyRestoreProjectVersion", "Restores a version through the history restore service; no model call."),

            // Outputs, uploads, run waiting and video revisions.
            Free("GetOutput", "Describes a render or file and mints a download link; asks for frame sheets to be made in the background, no model call."),
            Free("GetMusicBeats", "Reads the cached beat grid, or queues the free deterministic beat analysis; no model call."),
            Billed("LookAtOutput", "Sends full-size stills of a render or image and a question to the vision model.", InferenceProviderCapability.Vision),
            Free("ProposeSaveOutputAsFile", Proposes),
            Free("ApplySaveOutputAsFile", "Copies a render inside storage into the project's files; its background summary is queued work, free by policy."),
            Free("ProposeUploadFile", "Checks the quota and presigns an upload address; writes nothing and calls no model."),
            Free("ApplyUploadFile", "Records a file whose bytes were uploaded; its background summary is queued work, free by policy."),
            Free("ProposeImportFileFromUrl", Proposes),
            Free("ApplyImportFileFromUrl", "Downloads one public file into the project's files; its background summary is queued work, free by policy."),
            Free("GetExecutionStatus", Reads),
            // Writes one watch row and spends nothing; it is not a project change, so no approval card.
            Free("WatchExecution", "Records a request to be woken when a run finishes; no model call, spends no credits."),
            Free("GetCreditBalance", Reads),
            Free("ProposeReviseComposition", "Reads the run's scenes and the revision estimate and mints a token; writes nothing and calls no model."),
            Free("ApplyReviseComposition", "Queues a revision run; the run is charged by the workflow credit gate, not the turn."),
            // Change requests on a step of a finished run.
            Free("GetChangeOptions", "Reads which steps of a finished run can be changed, their items and settings; changes nothing."),
            Free("ProposeStepChange", "Validates and prices a step change and mints a token; writes nothing and calls no model."),
            Free("ApplyStepChange", "Queues the change run; the run is charged by the workflow credit gate, not the turn."),

            // Workflow patches and clones, picture documents and the green-screen insert.
            Free("ProposePatchWorkflow", Proposes),
            Free("ApplyPatchWorkflow", "Saves the patched steps through the workflow diff path; no model call."),
            Free("ProposeCloneWorkflow", Proposes),
            Free("ApplyCloneWorkflow", "Copies a workflow's steps into a new workflow; no model call."),
            Free("ProposeCreatePictureDocument", Proposes),
            Free("ApplyCreatePictureDocument", "Creates a picture document from a stored image through the picture-document commands; no model call."),
            Free("ProposeGreenScreenInsert", Proposes),
            Free("ApplyGreenScreenInsert", "Saves a workflow from the green-screen template; starts no run and calls no model."),
            Free("ProposeCleanAudio", Proposes),
            Free("ApplyCleanAudio", "Starts a local, free audio cleanup through the same service as the editor route; a hosted AI target is refused and nothing runs."),
            // A project's narration voices: reads and one library lookup, no model call and no generation.
            Free("ListProjectVoices", "Reads the project's voice rows; no model call."),
            Free("SearchVoiceLibrary", "Lists one page of the speech provider's public voice library (a free vendor listing, cached ten minutes); no model call and no audio."),
            Free("ProposeAddLibraryVoice", Proposes),
            Free("ApplyAddLibraryVoice", "Writes one Library voice row after a free vendor lookup; no consent, no model call, no generation."),
            // A project's avatars: read-only (consent is only ever given in the app's Avatars tab).
            Free("ListProjectAvatars", "Reads the project's avatar and reference rows; no model call, no media."),
            // Avatars and objects (docs/design/avatar-objects.md): the library list, the object, variant, import and clip pairs.
            Free("ListAvatarLibrary", "Reads the workspace avatar library's rows; no model call, no media."),
            Free("ProposeCreateObjectAvatar", "Resolves the files by path and mints a token; writes nothing and calls no model."),
            Free("ApplyCreateObjectAvatar", "Creates the object avatar through the one create path; its person check is metered like indexing, free to the customer."),
            Free("ProposeExtractObjectFromVideo", "Resolves the video and reads the build service's estimate; mints a token, writes nothing and calls no model."),
            Billed("ApplyExtractObjectFromVideo", "Queues the extraction job, which shows the frames to the segmentation model when one is set up (held and settled by the build service).",
                InferenceProviderCapability.Segmentation),
            Free("ProposeCreateAvatarVariant", "Resolves the original and reads the build service's estimate; mints a token, writes nothing and calls no model."),
            Billed("ApplyCreateAvatarVariant", "From files: creates the variant through the one create path. By the picture model: queues the build job, which changes the pictures (and remakes the videos) on platform models (held and settled by the build service).",
                InferenceProviderCapability.ImageEdit, InferenceProviderCapability.VideoGeneration),
            Free("ProposeImportLibraryAvatar", Proposes),
            Free("ApplyImportLibraryAvatar", "Copies a library item's objects into the project as a linked import; no model call, no credits."),
            Free("ProposeGenerateSubjectClip", "Resolves the avatars by name and reads the clip service's estimate; mints a token, writes nothing and calls no model."),
            Billed("ApplyGenerateSubjectClip", "Starts the hidden clip run, which calls the video model (and the picture model for a still) on the platform (charged by its workflow credit gate).",
                InferenceProviderCapability.VideoGeneration, InferenceProviderCapability.ImageEdit),
            // Progressive context: read-only windows onto the events a video analysis stored.
            Free("ListVideoEvents", "Reads the events stored by a video analysis; no model call, no media."),
            Free("DescribeVideoWindow", "Reads the events and speech between two stored events; no model call, no media."),
            // Social publishing: reads of the org's own accounts, queue and counts; a schedule or cancel is a pair, never over MCP.
            Free("ListSocialAccounts", "Reads the workspace's connected social accounts by name and status; no token is read out, no model call."),
            Free("ListScheduledPosts", "Reads the workspace's queued and past posts by account, project and video name; no model call."),
            Free("GetPostPerformance", "Reads the stored counts of published posts; no platform call from the tool and no model call."),
            Free("ProposeSchedulePost", "Runs the app's preflight and plan check and mints a token; writes nothing, posts nothing and calls no model."),
            Free("ApplySchedulePost", "Queues one post through the app's create route; the publisher sends it later. No model call."),
            Free("ProposeCancelScheduledPost", Proposes),
            Free("ApplyCancelScheduledPost", "Cancels a post that has not started through the app's cancel route; no model call."),
            Free("ProposeSetMediaOverlay", Proposes),
            Free("ApplySetMediaOverlay", "Appends a picture or video to a compile step through the normal save path; starts no run and calls no model."),
            // Object effects on an editor timeline.
            Free("ProposeObjectEffect", "Resolves the clips by name and reads the analysis estimate; mints a token, writes nothing and calls no model."),
            Billed("WritePostText", "Writes the post text on the chat model (the app write route holds and settles its own credits).",
                InferenceProviderCapability.Chat),
            Billed("ApplyObjectEffect", "Queues the hidden object-analysis run, which calls the segmentation model (charged by its workflow credit gate).",
                InferenceProviderCapability.Segmentation),
            // Video enhancement: the local operations are free; a hosted upscale or interpolation calls a platform video model.
            Free("ProposeEnhanceVideo", "Resolves the source and reads the enhancement estimate; mints a token, writes nothing and calls no model."),
            Billed("ApplyEnhanceVideo", "Starts the hidden video-enhance run; a hosted upscale or interpolation calls a platform video model (charged by its workflow credit gate). Local operations call none.",
                InferenceProviderCapability.VideoUpscale, InferenceProviderCapability.FrameInterpolation)
        ],
        StringComparer.Ordinal);

    /// <summary>The Platform-billed tool names (ordinal), derived from <see cref="Tools"/>.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(
        Tools.Where(t => t.Value.Kind == AssistantToolCostKind.PlatformBilled).Select(t => t.Key), StringComparer.Ordinal);

    /// <summary>The classification of <paramref name="toolName"/>, or null when the table does not list it.</summary>
    public static AssistantToolCost? Classify(string? toolName) =>
        toolName is not null && Tools.TryGetValue(toolName, out AssistantToolCost? cost) ? cost : null;

    /// <summary>
    /// Whether a call of <paramref name="toolName"/> is pre-claimed as Platform-billed: a Platform-billed tool and, failing
    /// closed, any name the table does not classify (null and empty included).
    /// </summary>
    public static bool IsPlatformBilled(string? toolName) => Classify(toolName) is not { Kind: AssistantToolCostKind.Free };

    /// <summary>The registered tool names the table does not classify (the drift guard: each needs an entry).</summary>
    public static IReadOnlyList<string> Unclassified(IEnumerable<string> registered) =>
        [.. registered.Where(name => !Tools.ContainsKey(name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The table entries no registered tool carries (the drift guard: a renamed or removed tool).</summary>
    public static IReadOnlyList<string> Stale(IEnumerable<string> registered)
    {
        HashSet<string> names = new(registered, StringComparer.Ordinal);
        return [.. Tools.Keys.Where(name => !names.Contains(name)).Order(StringComparer.Ordinal)];
    }
}
