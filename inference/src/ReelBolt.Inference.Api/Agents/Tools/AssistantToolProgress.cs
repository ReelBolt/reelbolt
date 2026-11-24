using Microsoft.Extensions.AI;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// Plain-language captions for what the assistant is doing while a tool runs, streamed to the chat
/// panel (<c>data-toolStatus</c>) so the "Thinking" line cycles through real steps instead of
/// sitting on one word for a minute. Captions are written for users: no tool names, ids or jargon.
/// </summary>
public static class AssistantToolProgress
{
    internal const string FallbackCaption = "Working on it";

    private static readonly Dictionary<string, string> Captions = new(StringComparer.Ordinal)
    {
        ["ListAgents"] = "Looking at the available agents",
        ["ListStepSchemas"] = "Checking how workflow steps are set up",
        ["GetStepCatalog"] = "Looking up what the workflow steps do",
        ["ListProjectFiles"] = "Looking through your project files",
        ["ValidateWorkflow"] = "Checking the workflow for mistakes",
        ["ProposeWorkflow"] = "Preparing the workflow changes",
        ["ApplyWorkflow"] = "Saving the workflow",
        ["ExecuteWorkflow"] = "Starting the workflow",
        ["ListProjects"] = "Looking at your projects",
        ["GetProject"] = "Opening the project",
        ["ProposeCreateProject"] = "Preparing the new project",
        ["ApplyCreateProject"] = "Creating the project",
        ["ProposeUpdateProject"] = "Preparing the project changes",
        ["ApplyUpdateProject"] = "Saving the project",
        ["SearchSoundEffects"] = "Searching the sound effects",
        ["ProposeAddSoundEffect"] = "Preparing the sound effect",
        ["ApplyAddSoundEffect"] = "Adding the sound effect",
        ["ProposeSavePronunciation"] = "Preparing the pronunciation",
        ["ApplySavePronunciation"] = "Saving the pronunciation",
        ["ProposeDeleteProject"] = "Preparing to delete the project",
        ["ApplyDeleteProject"] = "Deleting the project",
        ["ReadProjectFile"] = "Reading a file",
        ["ProposeWriteProjectFile"] = "Preparing the file",
        ["ApplyWriteProjectFile"] = "Saving the file",
        ["ReadProjectFileLines"] = "Reading part of a file",
        ["GetProjectFileOutline"] = "Looking at the file's outline",
        ["ProposeEditProjectFile"] = "Preparing the file change",
        ["ApplyEditProjectFile"] = "Saving the file change",
        ["ProposeInsertProjectFileLines"] = "Preparing the new lines",
        ["ApplyInsertProjectFileLines"] = "Saving the new lines",
        ["ProposeDeleteProjectFile"] = "Preparing to delete the file",
        ["ApplyDeleteProjectFile"] = "Deleting the file",
        ["ListWorkflows"] = "Looking at your workflows",
        ["GetWorkflow"] = "Reading the workflow's steps",
        ["ListWorkflowExecutions"] = "Checking recent runs",
        ["GetWorkflowExecution"] = "Looking at how the run went",
        ["ProposeStopExecution"] = "Preparing to stop the run",
        ["ApplyStopExecution"] = "Stopping the run",
        ["ProposeDeleteWorkflow"] = "Preparing to delete the workflow",
        ["ApplyDeleteWorkflow"] = "Deleting the workflow",
        ["SearchPlatformDocs"] = "Searching the documentation",
        ["ListWorkflowTemplates"] = "Browsing workflow templates",
        ["GetWorkflowTemplate"] = "Opening a workflow template",
        ["ListInferenceProviders"] = "Checking the AI service connections",
        ["TestInferenceProvider"] = "Testing the connection",
        ["ProposeCreateInferenceProvider"] = "Preparing the new connection",
        ["ApplyCreateInferenceProvider"] = "Adding the connection",
        ["ProposeUpdateInferenceProvider"] = "Preparing the connection changes",
        ["ApplyUpdateInferenceProvider"] = "Saving the connection",
        ["ProposeDeleteInferenceProvider"] = "Preparing to remove the connection",
        ["ApplyDeleteInferenceProvider"] = "Removing the connection",
        ["ListSkills"] = "Looking at the skill catalog",
        ["ProposeAssignAgentSkills"] = "Preparing the skill changes",
        ["ApplyAssignAgentSkills"] = "Saving the agent's skills",
        ["ListAgentsAdmin"] = "Looking at every agent",
        ["GetDecisionCalibration"] = "Reading the calibration report",
        ["ListStyles"] = "Looking at the editing styles",
        ["ProposeSaveStyle"] = "Preparing the style",
        ["ApplySaveStyle"] = "Saving the style",
        ["GetDesignSystem"] = "Looking at the design system",
        ["ListDesignChanges"] = "Checking the design changes",
        ["ProposeSaveDesignSystem"] = "Preparing the design system draft",
        ["ApplySaveDesignSystem"] = "Saving the design system draft",
        ["ProposeDecideDesignChange"] = "Preparing the design change decision",
        ["ApplyDecideDesignChange"] = "Deciding the design change",
        ["ListProjectVersions"] = "Looking at the project's versions",
        ["GetProjectChanges"] = "Comparing versions",
        ["ProposeSaveProjectVersion"] = "Preparing to save a version",
        ["ApplySaveProjectVersion"] = "Saving a version",
        ["ProposeRestoreProjectVersion"] = "Preparing the restore",
        ["ApplyRestoreProjectVersion"] = "Restoring the version",
        ["ListSocialAccounts"] = "Checking the connected accounts",
        ["ListScheduledPosts"] = "Checking the scheduled posts",
        ["GetPostPerformance"] = "Looking at how the posts did",
        ["ProposeSchedulePost"] = "Preparing the post",
        ["ApplySchedulePost"] = "Scheduling the post",
        ["ProposeCancelScheduledPost"] = "Preparing to cancel the post",
        ["ApplyCancelScheduledPost"] = "Cancelling the post",
        ["DescribePictureDocument"] = "Looking at your picture",
        ["ListPictureTools"] = "Checking the AI picture tools",
        ["LookAtPicture"] = "Looking at the picture",
        ["ProposeAddTextLayer"] = "Preparing the text",
        ["ApplyAddTextLayer"] = "Adding the text to your picture",
        ["ProposeEditPictureLayers"] = "Preparing the layer changes",
        ["ApplyEditPictureLayers"] = "Changing the layers",
        ["ProposeDeletePictureLayers"] = "Preparing to delete the layers",
        ["ApplyDeletePictureLayers"] = "Deleting the layers",
        ["ProposeChangePictureCanvas"] = "Preparing the canvas change",
        ["ApplyChangePictureCanvas"] = "Changing the canvas",
        ["ProposeRunPictureTool"] = "Pricing the AI picture tool",
        ["ApplyRunPictureTool"] = "Running the AI picture tool",
        ["ProposeDesignGraphic"] = "Preparing the graphic",
        ["ApplyDesignGraphic"] = "Starting the graphic designer",
        ["ProposeUploadFile"] = "Preparing the upload",
        ["ApplyUploadFile"] = "Adding the uploaded file",
        ["ProposeImportFileFromUrl"] = "Preparing to fetch the file",
        ["ApplyImportFileFromUrl"] = "Downloading the file",
    };

    // The output, run and revision surfaces own their captions beside their tools; merging them here keeps one
    // lookup (and the coverage test) while a surface's wording stays next to its tool.
    static AssistantToolProgress()
    {
        foreach (IReadOnlyDictionary<string, string> own in new[]
                 { AssistantOutputTools.Captions, AssistantOutputTools.LookCaptions, AssistantRunTools.Captions, AssistantRevisionTools.Captions, AssistantChangeRequestTools.Captions,
                   AssistantWorkflowPatchTools.Captions, AssistantPictureDocumentTools.Captions, AssistantGreenScreenTools.Captions, AssistantMediaOverlayTools.Captions, AssistantAudioCleanupTools.Captions, AssistantVoiceTools.Captions, AssistantAvatarTools.Captions, AssistantMarketplaceTools.Captions, AssistantVideoEnhanceTools.Captions, AssistantRecipeTools.Captions, AssistantObjectEffectTools.Captions, AssistantPostTextTools.Captions, AssistantVideoEventsTools.Captions })
        {
            foreach ((string name, string caption) in own)
                Captions[name] = caption;
        }
    }

    /// <summary>The caption for a tool, or a neutral fallback for one added without a caption.</summary>
    public static string CaptionFor(string toolName) =>
        Captions.TryGetValue(toolName, out string? caption) ? caption : FallbackCaption;

    /// <summary>
    /// Shown after a tool returned, while the model reads the result and decides what to do next.
    /// </summary>
    internal const string AfterToolCaption = "Thinking";

    /// <summary>
    /// Shown when the documentation search could not answer in time (or the index is not built yet)
    /// and the assistant carries on without it — the truthful state, instead of a search caption
    /// that never changes.
    /// </summary>
    internal const string DocsUnavailableCaption = "Docs index still building — answering without it";

    /// <summary>
    /// The caption that is true once <paramref name="toolName"/> has returned <paramref name="result"/>.
    /// A documentation search that came back unavailable says so; everything else hands back to
    /// "Thinking".
    /// </summary>
    public static string CaptionAfter(string toolName, object? result)
    {
        if (toolName == "SearchPlatformDocs" && AssistantKnowledgeTools.IsUnavailableSearchResult(result))
            return DocsUnavailableCaption;

        return AfterToolCaption;
    }

    /// <summary>The tool names that have a caption (for the coverage test).</summary>
    internal static IReadOnlyCollection<string> CaptionedTools => Captions.Keys;

    /// <summary>
    /// Wraps a tool so <paramref name="onStarting"/> hears its caption just before it runs. The name,
    /// description and schema the model sees are the inner function's, byte for byte. A failing
    /// progress callback never fails the tool.
    /// </summary>
    public static AIFunction WithProgress(
        AIFunction inner,
        Func<string, CancellationToken, Task> onStarting,
        Func<string, CancellationToken, Task>? onFinished = null) =>
        new ProgressReportingFunction(inner, onStarting, onFinished);

    private sealed class ProgressReportingFunction(
        AIFunction inner,
        Func<string, CancellationToken, Task> onStarting,
        Func<string, CancellationToken, Task>? onFinished)
        : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            await ReportAsync(onStarting, CaptionFor(Name), cancellationToken);

            object? result = await base.InvokeCoreAsync(arguments, cancellationToken);

            if (onFinished is not null)
                await ReportAsync(onFinished, CaptionAfter(Name, result), cancellationToken);

            return result;
        }

        private static async Task ReportAsync(Func<string, CancellationToken, Task> report, string caption, CancellationToken ct)
        {
            try
            {
                await report(caption, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Progress is cosmetic.
            }
        }
    }
}
