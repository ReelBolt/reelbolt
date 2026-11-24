using System.Text.Json;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// The ONE place every project-scoped assistant tool decides which project a call is about.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> An explicit, non-empty <c>projectId</c> argument wins; otherwise the project open on
/// the user's page (<see cref="AssistantToolContext.ProjectId"/>); otherwise the call is refused with a
/// message the model can act on. The page's project is therefore a <em>default</em>, never a limit: the
/// assistant can work on any project of the workspace from any page, including the home page.
/// </para>
/// <para>
/// <b>Authority is unchanged and per call.</b> Whichever id wins is re-resolved through
/// <see cref="IProjectAccessService.Get"/> with the permission the calling tool needs, on this call, and
/// never remembered. A project of another organization, a project the caller's role may not touch and a
/// project that does not exist all produce the <b>same</b> refusal — an explicit id reads
/// <c>project not found</c>, an id that only came from the page reads <c>no project in context</c> — so a
/// guessed id is never confirmed to a stranger (D5).
/// </para>
/// <para>
/// <b>Page defaults are tied to the page's project.</b> The workflow and the picture the user has open
/// (<see cref="AssistantToolContext.WorkflowId"/>, <see cref="AssistantToolContext.ImageDocumentId"/>)
/// belong to the page's project; <see cref="PageWorkflowFor"/> / <see cref="PageDocumentFor"/> hand them
/// out only when the project a call resolved to IS that project, so a workflow open on project A is never
/// silently applied to a call that named project B.
/// </para>
/// </remarks>
internal static class AssistantProjectScope
{
    internal const string NoProjectMessage = "no project in context";
    internal const string ProjectNotFoundMessage = "project not found";

    /// <summary>What a model that has no usable project is told to do next.</summary>
    internal const string NoProjectHint =
        "No project is open on the user's page, so say which one: pass projectId (find it by the project's name with ListProjects).";

    internal const string ProjectNotFoundHint =
        "Look the project up by its name with ListProjects and pass the id it returns.";

    /// <summary>
    /// The sentence every tool that takes <c>projectId</c> carries in its description, kept here so the
    /// wording the model learns is identical on every surface.
    /// </summary>
    internal const string ProjectIdNote =
        "projectId is optional: omit it to use the project open on the user's page; pass it (get it from ListProjects) to work on any other project of the workspace.";

    internal static readonly string NoProjectErrorJson =
        JsonSerializer.Serialize(new { error = NoProjectMessage, hint = NoProjectHint });

    internal static readonly string ProjectNotFoundErrorJson =
        JsonSerializer.Serialize(new { error = ProjectNotFoundMessage, hint = ProjectNotFoundHint });

    /// <summary>True when the caller named a project in the arguments, as opposed to leaving it to the page.</summary>
    internal static bool IsExplicit(Guid? projectId) => projectId is { } id && id != Guid.Empty;

    /// <summary>
    /// The project this call is about — the one named in the arguments, else the one on the user's page —
    /// re-resolved and re-authorized right now for <paramref name="permission"/>. <c>null</c> comes with
    /// the refusal JSON in <paramref name="errorJson"/>, and nothing has been touched.
    /// </summary>
    internal static Project? Resolve(
        IProjectAccessService projectAccess,
        AssistantToolContext context,
        ProjectPermission permission,
        Guid? projectId,
        out string errorJson)
    {
        bool explicitId = IsExplicit(projectId);
        Guid? target = explicitId ? projectId : context.ProjectId;

        if (target is not { } resolved || resolved == Guid.Empty)
        {
            errorJson = NoProjectErrorJson;
            return null;
        }

        errorJson = explicitId ? ProjectNotFoundErrorJson : NoProjectErrorJson;
        return projectAccess.Get(resolved, permission).Project;
    }

    /// <summary>
    /// The refusal as a JSON value, for the tools whose declared return type is <c>object</c> (so the model
    /// receives an object rather than a string holding JSON, exactly as those tools always answered).
    /// </summary>
    internal static JsonElement AsObject(string errorJson)
    {
        using JsonDocument document = JsonDocument.Parse(errorJson);
        return document.RootElement.Clone();
    }

    /// <summary>True when <paramref name="project"/> is the project open on the user's page.</summary>
    internal static bool IsPageProject(AssistantToolContext context, Project project) =>
        context.ProjectId is { } pageProject && pageProject != Guid.Empty && pageProject == project.Id;

    /// <summary>The workflow open on the user's page, but only for a call on the page's own project.</summary>
    internal static Guid? PageWorkflowFor(AssistantToolContext context, Project project) =>
        IsPageProject(context, project) ? context.WorkflowId : null;

    /// <summary>The picture open on the user's page, but only for a call on the page's own project.</summary>
    internal static Guid? PageDocumentFor(AssistantToolContext context, Project project) =>
        IsPageProject(context, project) ? context.ImageDocumentId : null;
}
