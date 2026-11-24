namespace ReelBolt.Inference.Api.Services.Assistant;

/// <param name="Kind">Which <see cref="AssistantActionKind"/> the card is shown as (icon and what to reload).</param>
/// <param name="Risk">The card's default <see cref="AssistantActionRisk"/>; a builder may raise it (overwriting a file).</param>
/// <param name="DoneText">The plain-language outcome shown once the action has run.</param>
/// <param name="FallbackTitle">Used only when a card cannot name the target any better.</param>
public sealed record ApprovalToolInfo(string Kind, string Risk, string DoneText, string FallbackTitle);

/// <summary>
/// Which assistant tools write, and how each is presented when the user is asked. A tool is gated by its
/// NAME — every <c>Apply*</c> tool, plus <c>ExecuteWorkflow</c> — so a new <c>Apply*</c> tool is covered the
/// moment it is registered; <c>AssistantApprovalCatalogTests</c> fails when one has no entry here, so its card
/// is written on purpose instead of falling back to the generic one.
/// </summary>
public static class AssistantApprovalCatalog
{
    public const string ExecuteWorkflowTool = "ExecuteWorkflow";

    private const string ApplyPrefix = "Apply";
    private const string ProposePrefix = "Propose";

    private static readonly IReadOnlyDictionary<string, ApprovalToolInfo> Tools =
        new Dictionary<string, ApprovalToolInfo>(StringComparer.Ordinal)
        {
            ["ApplyStopExecution"] = new(AssistantActionKind.Run, AssistantActionRisk.Destructive, "Stopped.", "Stop a run"),
            ["ApplyWorkflow"] = new(AssistantActionKind.Workflow, AssistantActionRisk.Normal, "Saved.", "Change a workflow"),
            [ExecuteWorkflowTool] = new(AssistantActionKind.Run, AssistantActionRisk.Normal, "Started.", "Run a workflow"),
            ["ApplyCreateProject"] = new(AssistantActionKind.Project, AssistantActionRisk.Normal, "Created.", "Create a project"),
            ["ApplyUpdateProject"] = new(AssistantActionKind.Project, AssistantActionRisk.Normal, "Saved.", "Change a project"),
            ["ApplySavePronunciation"] = new(AssistantActionKind.Project, AssistantActionRisk.Normal, "Saved.", "Save a pronunciation"),
            ["ApplyAddSoundEffect"] = new(AssistantActionKind.Project, AssistantActionRisk.Normal, "Added.", "Add a sound effect"),
            ["ApplyDeleteProject"] = new(AssistantActionKind.Project, AssistantActionRisk.Destructive, "Deleted.", "Delete a project"),
            ["ApplyWriteProjectFile"] = new(AssistantActionKind.File, AssistantActionRisk.Normal, "Saved.", "Save a file"),
            ["ApplyEditProjectFile"] = new(AssistantActionKind.File, AssistantActionRisk.Destructive, "Saved.", "Change part of a file"),
            ["ApplyInsertProjectFileLines"] = new(AssistantActionKind.File, AssistantActionRisk.Destructive, "Saved.", "Add lines to a file"),
            ["ApplyDeleteProjectFile"] = new(AssistantActionKind.File, AssistantActionRisk.Destructive, "Deleted.", "Delete a file"),
            ["ApplyDeleteWorkflow"] = new(AssistantActionKind.Workflow, AssistantActionRisk.Destructive, "Deleted.", "Delete a workflow"),
            ["ApplySaveStyle"] = new(AssistantActionKind.Style, AssistantActionRisk.Normal, "Saved.", "Save an editing style"),
            ["ApplySaveDesignSystem"] = new(AssistantActionKind.Style, AssistantActionRisk.Normal, "Saved.", "Save the design system draft"),
            ["ApplyDecideDesignChange"] = new(AssistantActionKind.Style, AssistantActionRisk.Normal, "Done.", "Decide a design change"),
            ["ApplyAddTextLayer"] = new(AssistantActionKind.Picture, AssistantActionRisk.Normal, "Added.", "Add text to the picture"),
            ["ApplyEditPictureLayers"] = new(AssistantActionKind.Picture, AssistantActionRisk.Normal, "Changed.", "Change the picture's layers"),
            ["ApplyDeletePictureLayers"] = new(AssistantActionKind.Picture, AssistantActionRisk.Destructive, "Deleted.", "Delete picture layers"),
            ["ApplyChangePictureCanvas"] = new(AssistantActionKind.Picture, AssistantActionRisk.Normal, "Changed.", "Change the picture's canvas"),
            ["ApplyRunPictureTool"] = new(AssistantActionKind.Picture, AssistantActionRisk.Paid, "Done.", "Run an AI picture tool"),
            ["ApplyDesignGraphic"] = new(AssistantActionKind.Picture, AssistantActionRisk.Paid, "Started.", "Design a graphic"),
            ["ApplyCreateInferenceProvider"] = new(AssistantActionKind.Connection, AssistantActionRisk.Normal, "Added.", "Add an AI service connection"),
            ["ApplyUpdateInferenceProvider"] = new(AssistantActionKind.Connection, AssistantActionRisk.Normal, "Saved.", "Change an AI service connection"),
            ["ApplyDeleteInferenceProvider"] = new(AssistantActionKind.Connection, AssistantActionRisk.Destructive, "Removed.", "Remove an AI service connection"),
            ["ApplyAssignAgentSkills"] = new(AssistantActionKind.Agent, AssistantActionRisk.Normal, "Saved.", "Change an agent's skills"),
            ["ApplySaveOutputAsFile"] = new(AssistantActionKind.File, AssistantActionRisk.Normal, "Saved to your files.", "Save a render to your files"),
            ["ApplyUploadFile"] = new(AssistantActionKind.File, AssistantActionRisk.Normal, "File added.", "Add an uploaded file"),
            ["ApplyImportFileFromUrl"] = new(AssistantActionKind.File, AssistantActionRisk.Normal, "File imported.", "Import a file from a link"),
            ["ApplyReviseComposition"] = new(AssistantActionKind.Run, AssistantActionRisk.Paid, "Started.", "Change this video"),
            ["ApplyStepChange"] = new(AssistantActionKind.Run, AssistantActionRisk.Paid, "Started.", "Change steps of this run"),
            ["ApplyPatchWorkflow"] = new(AssistantActionKind.Workflow, AssistantActionRisk.Normal, "Saved.", "Change a workflow"),
            ["ApplyCloneWorkflow"] = new(AssistantActionKind.Workflow, AssistantActionRisk.Normal, "Copied.", "Copy a workflow"),
            ["ApplyCreatePictureDocument"] = new(AssistantActionKind.Picture, AssistantActionRisk.Normal, "Opened.", "Open an image in the thumbnail studio"),
            ["ApplyCreateFromTemplate"] = new(AssistantActionKind.Workflow, AssistantActionRisk.Normal, "Workflow ready.", "Create a workflow from a recipe"),
            ["ApplyGreenScreenInsert"] = new(AssistantActionKind.Workflow, AssistantActionRisk.Normal, "Workflow ready.", "Set up a green-screen insert"),
            ["ApplyObjectEffect"] = new(AssistantActionKind.Run, AssistantActionRisk.Paid, "Started.", "Put an effect on an object in a clip"),
            ["ApplyCopyMarketplaceRecipe"] = new(AssistantActionKind.Workflow, AssistantActionRisk.Normal, "Workflow ready.", "Copy a community recipe"),
            ["ApplyCleanAudio"] = new(AssistantActionKind.Run, AssistantActionRisk.Normal, "Started.", "Clean the audio"),
            ["ApplySaveProjectVersion"] = new(AssistantActionKind.Project, AssistantActionRisk.Normal, "Saved.", "Save a project version"),
            ["ApplyRestoreProjectVersion"] = new(AssistantActionKind.Project, AssistantActionRisk.Destructive, "Restored.", "Restore a project version"),
            ["ApplyEnhanceVideo"] = new(AssistantActionKind.Run, AssistantActionRisk.Paid, "Started.", "Enhance a video"),
            ["ApplyAddLibraryVoice"] = new(AssistantActionKind.Project, AssistantActionRisk.Normal, "Added.", "Add a library voice"),
            ["ApplySchedulePost"] = new(AssistantActionKind.Run, AssistantActionRisk.Normal, "Scheduled.", "Schedule a post"),
            ["ApplyCancelScheduledPost"] = new(AssistantActionKind.Run, AssistantActionRisk.Normal, "Cancelled.", "Cancel a scheduled post"),
            ["ApplySetMediaOverlay"] = new(AssistantActionKind.Workflow, AssistantActionRisk.Normal, "Saved.", "Show a picture on the video"),
            // Avatars and objects (docs/design/avatar-objects.md "Avatar tools for the assistant and MCP").
            ["ApplyCreateObjectAvatar"] = new(AssistantActionKind.Project, AssistantActionRisk.Normal, "Made.", "Make an object avatar"),
            ["ApplyExtractObjectFromVideo"] = new(AssistantActionKind.Project, AssistantActionRisk.Paid, "Started.", "Take an object out of a video"),
            ["ApplyCreateAvatarVariant"] = new(AssistantActionKind.Project, AssistantActionRisk.Paid, "Done.", "Make a variant of an object"),
            ["ApplyImportLibraryAvatar"] = new(AssistantActionKind.Project, AssistantActionRisk.Normal, "Imported.", "Import an avatar from the library"),
            ["ApplyGenerateSubjectClip"] = new(AssistantActionKind.Run, AssistantActionRisk.Paid, "Started.", "Make a clip with avatars"),
        };

    /// <summary>True for every tool that writes or spends: the user is asked before it runs.</summary>
    public static bool IsGated(string toolName) =>
        string.Equals(toolName, ExecuteWorkflowTool, StringComparison.Ordinal)
        || toolName.StartsWith(ApplyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The gated tools whose card is shown even when the user is in auto mode and whatever the cost limit: a restore
    /// rewrites the project back to an earlier state, and a scheduled or cancelled post speaks in the user's name on a
    /// public channel, so only the user's own click may allow it. The admin writes (an AI service connection holds a key
    /// and an endpoint; an agent's skills steer what it does) and a link import (the server fetches an address the model
    /// chose) are here too: content read during a turn can steer the model, so it must not be able to make these happen
    /// with no human step. A wake turn has nobody watching, so such a card simply expires there. A click on "always
    /// allow" on such a card does not turn auto mode on. Scoped by tool name.
    /// </summary>
    private static readonly HashSet<string> AlwaysAskTools = new(StringComparer.Ordinal)
    {
        "ApplyRestoreProjectVersion",
        "ApplySchedulePost",
        "ApplyCancelScheduledPost",
        "ApplyCreateInferenceProvider",
        "ApplyUpdateInferenceProvider",
        "ApplyDeleteInferenceProvider",
        "ApplyAssignAgentSkills",
        "ApplyImportFileFromUrl",
        // Avatars and objects: making an object is the member's own attestation (the object statement on the card), and an
        // import brings a person's likeness into a project, so only the user's own click may allow them.
        "ApplyCreateObjectAvatar",
        "ApplyExtractObjectFromVideo",
        "ApplyCreateAvatarVariant",
        "ApplyImportLibraryAvatar"
    };

    /// <summary>True when the card must be answered by the user even in auto mode.</summary>
    public static bool AlwaysAsksUser(string toolName) => AlwaysAskTools.Contains(toolName);

    /// <summary>True for the half of a pair that only computes a proposal and writes nothing.</summary>
    public static bool IsProposal(string toolName) => toolName.StartsWith(ProposePrefix, StringComparison.Ordinal);

    /// <summary>How <paramref name="toolName"/> is presented; a generic entry for a gated tool that has none.</summary>
    public static ApprovalToolInfo Describe(string toolName) =>
        Tools.TryGetValue(toolName, out ApprovalToolInfo? info)
            ? info
            : new ApprovalToolInfo(AssistantActionKind.Agent, AssistantActionRisk.Normal, "Done.", "Make a change");

    /// <summary>The gated tool names that have a deliberate entry (for the coverage test).</summary>
    public static IReadOnlyCollection<string> KnownTools => Tools.Keys.ToList();
}
