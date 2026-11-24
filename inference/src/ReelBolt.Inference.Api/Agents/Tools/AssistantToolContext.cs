using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// The per-call context one batch of assistant tools runs under: WHO is being served and WHAT page
/// they are on. Built once per request from the verified request body and handed to
/// <see cref="AssistantToolProvider.GetTools(AssistantToolContext)"/>, so a tool reads its project
/// from its own invocation instead of from a field captured when the provider was constructed.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately carries <b>no authority</b>. There is no <c>IsAdmin</c>, no role, no
/// permission set and no frozen copy of the caller's identity here — an admin claim that travelled
/// with the conversation is exactly the privilege-escalation diagonal the assistant must not have.
/// Authority is read from the request-scoped <see cref="ReelBolt.Shared.Auth.ICurrentUser"/> inside
/// each gated tool body, on the call that is executing.
/// </para>
/// <para>
/// Likewise, the ids below are the caller's <em>page</em> context, not a grant: every tool that uses
/// <see cref="ProjectId"/> re-resolves the project from the database and re-checks it
/// through <c>IProjectAccessService</c> (the caller's active organization and role) before acting on
/// it, on every call.
/// </para>
/// </remarks>
/// <param name="UserId">
/// The acting user, as the transport verified it (the JWT <c>sub</c> claim). This is the identity
/// every access check is made on behalf of — never a user id taken from the model's arguments.
/// </param>
/// <param name="ProjectId">
/// The project the caller currently has open, or <c>null</c> when they are on a page with no project.
/// <c>null</c> is a normal, supported state: it is <b>not</b> a reason to throw, and it is not
/// widened into "any project" — project-scoped tools answer with an actionable JSON error instead.
/// </param>
/// <param name="WorkflowId">
/// The workflow the caller currently has open, or <c>null</c>. Meaningful only alongside a
/// <paramref name="ProjectId"/> the caller owns; the tools re-check it belongs to that project.
/// </param>
/// <param name="ImageDocumentId">
/// The picture (thumbnail) document open in the picture editor, or <c>null</c> on every other page.
/// Meaningful only alongside <paramref name="ProjectId"/>; the picture tools re-resolve it inside that
/// project on every call, and answer "no picture open" without it.
/// </param>
/// <param name="Initiator">
/// What caused THIS call — the assistant chat path (the default) or an external MCP client. It is
/// <b>provenance, not authority</b>: it only labels the executions a tool starts
/// (<c>workflow_executions.initiator</c>), and it never widens or narrows what the caller may do.
/// Authority stays in <c>ICurrentUser</c>, read inside each gated tool body on the call that runs.
/// </param>
/// <param name="ChatApproved">
/// True only for the assistant chat entry point, where the user's approval (card or auto mode) is the acceptance of a
/// profile-build estimate. It is never inferred from <paramref name="Initiator"/>, so a new construction
/// that omits it is not silently fully accepting.
/// </param>
/// <param name="ThreadId">
/// The assistant thread the call runs in (chat only; null over MCP and REST twins). A workflow the assistant creates
/// records it as <c>created_in_thread_id</c>, so a run can be traced to the conversation that made it.
/// </param>
/// <param name="FromWake">
/// True for a turn the platform started itself when a watched run finished (no browser attached). It changes no authority
/// and no approval behaviour: it only lets audit logging and the caps tell an unattended turn from a typed one.
/// </param>
public sealed record AssistantToolContext(
    Guid UserId,
    Guid? ProjectId = null,
    Guid? WorkflowId = null,
    Guid? ImageDocumentId = null,
    ExecutionInitiator Initiator = ExecutionInitiator.Assistant,
    bool ChatApproved = false,
    Guid? ThreadId = null,
    bool FromWake = false);
