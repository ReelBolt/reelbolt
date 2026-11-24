using System.Text.Json;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// The automatic-version triggers of contract section 9: the apply tools an assistant/MCP write
/// captures before, and the one door every trigger calls so a capture can never fail the action it
/// is protecting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Best effort, bounded.</b> Every trigger awaits <see cref="IProjectHistoryService.TryCaptureAutoAsync"/>,
/// which owns a 10 s timeout and swallows its own failures. This class adds one more guarantee: an
/// authority check resolved in the same ambient scope never turns into a failed tool either.
/// </para>
/// <para>
/// <b>Ambient service.</b> <see cref="RunStartGate"/> and the assistant tool interceptor are built
/// without a service dependency at every call site (the controllers instantiate the gate directly,
/// and the provider wraps tools with the interceptor), so the request-scoped service is published on
/// an async-local scope by <see cref="ProjectHistoryAmbientFilter"/>. A caller that does have the
/// service can pass it directly instead; the two are equivalent.
/// </para>
/// </remarks>
public static class ProjectHistoryAutoCapture
{
    private static readonly AsyncLocal<Ambient?> Current = new();

    /// <summary>
    /// The in-app Apply tools whose successful write changes a project's versioned content (contract
    /// section 9): file writes/edits/inserts/deletes/uploads/imports and save-output-as-file, workflow
    /// save/patch/clone/create-from-template, pronunciation save, picture document create, green-screen
    /// insert and media overlay. A name here must exist in
    /// <c>AssistantToolProvider.GetTools</c>; the drift test holds the set complete against the real
    /// registrations, and <see cref="AssistantApplyToolsLeftOut"/> records every Apply tool left out
    /// and why.
    /// </summary>
    public static IReadOnlySet<string> AssistantWriteTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // Files (contract section 9: write/edit/insert/delete/upload/import/save-output-as-file).
        "ApplyWriteProjectFile",
        "ApplyEditProjectFile",
        "ApplyInsertProjectFileLines",
        "ApplyDeleteProjectFile",
        "ApplyUploadFile",
        "ApplyImportFileFromUrl",
        "ApplySaveOutputAsFile",
        "ApplyAddSoundEffect",

        // Workflows (save/patch/clone/create-from-template).
        "ApplyWorkflow",
        "ApplyPatchWorkflow",
        "ApplyCloneWorkflow",
        "ApplyCreateFromTemplate",
        "ApplyCopyMarketplaceRecipe",

        // Pronunciation set.
        "ApplySavePronunciation",

        // Picture document create.
        "ApplyCreatePictureDocument",

        // Green-screen insert and media overlays.
        "ApplyGreenScreenInsert",
        "ApplySetMediaOverlay",

        // Audio cleanup starts a run that registers cleaned files in the project.
        "ApplyCleanAudio",

        // Video enhance starts a run that registers the enhanced video in the project (unless the call opts out).
        "ApplyEnhanceVideo",

        // Avatars and objects: each registers project files (copied library files, frames cut from a video, a variant's
        // changed pictures, a clip's takes).
        "ApplyImportLibraryAvatar",
        "ApplyExtractObjectFromVideo",
        "ApplyCreateAvatarVariant",
        "ApplyGenerateSubjectClip"
    };

    /// <summary>
    /// Every in-app <c>Apply*</c> tool deliberately left out of <see cref="AssistantWriteTools"/>, with
    /// the reason. The drift test requires this map and the set together to cover every Apply tool the
    /// provider really registers.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssistantApplyToolsLeftOut { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ApplyCancelScheduledPost"] = "a post in the publishing queue, not project content (contract section 1)",
            ["ApplySchedulePost"] = "a post in the publishing queue, not project content (contract section 1)",
            ["ApplyAddLibraryVoice"] = "a voice entry on the project, not a manifest item (contract section 1)",
            ["ApplyCreateObjectAvatar"] = "an avatar entry on the project made from files already in it, not a manifest item (contract section 1)",
            ["ApplyAddTextLayer"] = "picture-document layers are not a manifest item (contract section 1)",
            ["ApplyAssignAgentSkills"] = "workspace-level agent skills, not project content",
            ["ApplyChangePictureCanvas"] = "picture documents are not a manifest item (contract section 1)",
            ["ApplyCreateInferenceProvider"] = "workspace-level provider configuration, not project content",
            ["ApplyCreateProject"] = "no project exists yet to hold a version",
            ["ApplyDecideDesignChange"] = "the design system is captured by CurrentVersionId + digest, not a draft copy",
            ["ApplyDeleteInferenceProvider"] = "workspace-level provider configuration, not project content",
            ["ApplyDeletePictureLayers"] = "picture documents are not a manifest item (contract section 1)",
            ["ApplyDeleteProject"] = "the project and its history rows cascade away with it",
            ["ApplyDeleteWorkflow"] = "section 9 lists workflow save/patch/clone/create-from-template, not delete; the next beforeRun/idle capture records the removal",
            ["ApplyDesignGraphic"] = "queues a picture-document job; picture documents are not a manifest item",
            ["ApplyEditPictureLayers"] = "picture documents are not a manifest item (contract section 1)",
            ["ApplyObjectEffect"] = "picture documents are not a manifest item (contract section 1)",
            ["ApplyReviseComposition"] = "composition revisions are a hidden workflow kind never captured (contract section 1)",
            ["ApplyRunPictureTool"] = "picture-document tool; picture documents are not a manifest item",
            ["ApplySaveDesignSystem"] = "the manifest pins the design system's CurrentVersionId + digest, so a draft write changes nothing it holds",
            ["ApplySaveStyle"] = "saved styles are workspace-level, not project content",
            ["ApplyStepChange"] = "changes steps of a hidden composition-revision workflow, which is never captured",
            ["ApplyStopExecution"] = "runs are never captured (contract section 1)",
            ["ApplyUpdateInferenceProvider"] = "workspace-level provider configuration, not project content",
            ["ApplyUpdateProject"] = "section 9 does not list project metadata edits; the next beforeRun/idle capture records the rename"
        };

    /// <summary>The direct service supplied by a caller that has one; null when only the ambient scope is set.</summary>
    internal static IProjectHistoryService? CurrentHistory => Current.Value?.History;

    /// <summary>
    /// Publishes the request-scoped service (and the authority checker) for the current async flow.
    /// The previous value is restored on dispose, so nested scopes compose.
    /// </summary>
    public static IDisposable Use(
        IProjectHistoryService history,
        IProjectAccessService? projectAccess = null,
        Guid? pageProjectId = null,
        Guid? userId = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        Ambient? previous = Current.Value;
        Current.Value = new Ambient(
            history,
            projectAccess ?? previous?.ProjectAccess,
            pageProjectId ?? previous?.PageProjectId,
            userId ?? previous?.UserId);
        return new Scope(previous);
    }

    /// <summary>
    /// Captures when a project is in scope, through the ambient service or <paramref name="history"/>.
    /// Never throws for a capture failure (the service already swallows those) and never throws for a
    /// failed authority check; the caller's action proceeds untouched.
    /// </summary>
    public static Task TryCaptureAsync(
        IProjectHistoryService? history,
        Guid projectId,
        string cause,
        Guid? userId,
        CancellationToken ct)
    {
        Ambient? ambient = Current.Value;
        return TryCaptureCoreAsync(history ?? ambient?.History, ambient?.ProjectAccess, projectId, cause, userId, ct);
    }

    /// <summary>Ambient-only overload, for callers that hold no service reference.</summary>
    public static Task TryCaptureAsync(Guid projectId, string cause, Guid? userId, CancellationToken ct) =>
        TryCaptureAsync(history: null, projectId, cause, userId, ct);

    private static async Task TryCaptureCoreAsync(
        IProjectHistoryService? history,
        IProjectAccessService? projectAccess,
        Guid projectId,
        string cause,
        Guid? userId,
        CancellationToken ct)
    {
        if (history is null)
            return;

        try
        {
            // A version is only taken on a project the caller may write. The tools re-check this
            // themselves; checking here keeps an unauthorized explicit id from writing history.
            if (projectAccess is not null)
            {
                ProjectAccessResult access = await projectAccess
                    .GetAsync(projectId, ProjectPermission.Edit, ct)
                    .ConfigureAwait(false);
                if (access.Failure is not null)
                    return;
            }

            await history.TryCaptureAutoAsync(projectId, cause, userId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Automatic capture must never fail the action it protects.
        }
    }

    /// <summary>
    /// The project an assistant call names in its arguments, if any: a typed
    /// <see cref="Guid"/>, a <see cref="JsonElement"/> string or a JSON-GUID value. Null when the
    /// argument is absent or unusable (the tool then falls back to the page's project).
    /// </summary>
    public static Guid? ProjectIdFrom(IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.TryGetValue("projectId", out object? raw) ? AsProjectId(raw) : null;
    }

    /// <summary>Parses a bound argument (typed or JSON) into a project id.</summary>
    public static Guid? AsProjectId(object? raw) => raw switch
    {
        Guid id => id == Guid.Empty ? null : id,
        JsonElement { ValueKind: JsonValueKind.String } element
            when Guid.TryParse(element.GetString(), out Guid parsed) => parsed,
        string text when Guid.TryParse(text, out Guid parsed) => parsed == Guid.Empty ? null : parsed,
        _ => null
    };

    private sealed record Ambient(
        IProjectHistoryService History,
        IProjectAccessService? ProjectAccess,
        Guid? PageProjectId,
        Guid? UserId);

    /// <summary>
    /// Captures before an assistant tool runs (the in-app chat interceptor). Chooses the cause from the
    /// tool name: <c>ExecuteWorkflow</c> starts a run (<see cref="ProjectVersionCause.BeforeRun"/>), a name
    /// in <see cref="AssistantWriteTools"/> writes (<see cref="ProjectVersionCause.BeforeAssistant"/>), any
    /// other tool does nothing. The project is the explicit <c>projectId</c> argument, else the page's.
    /// Never throws and never waits past the capture timeout.
    /// </summary>
    public static Task TryCaptureForToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string? cause = CauseFor(toolName);
        Ambient? ambient = Current.Value;
        if (cause is null || ambient is null)
            return Task.CompletedTask;

        Guid? projectId = ProjectIdFrom(arguments) ?? ambient.PageProjectId;
        return projectId is { } id
            ? TryCaptureAsync(null, id, cause, ambient.UserId, ct)
            : Task.CompletedTask;
    }

    /// <summary>The automatic-version cause a tool call triggers, or null when it triggers none.</summary>
    public static string? CauseFor(string toolName) =>
        string.Equals(toolName, "ExecuteWorkflow", StringComparison.Ordinal) ? ProjectVersionCause.BeforeRun
        : AssistantWriteTools.Contains(toolName) ? ProjectVersionCause.BeforeAssistant
        : null;

    private sealed class Scope(Ambient? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Current.Value = previous;
        }
    }
}
