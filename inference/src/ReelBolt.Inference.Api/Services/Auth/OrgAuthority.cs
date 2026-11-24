using Microsoft.AspNetCore.Mvc;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Auth;

/// <summary>
/// The organization authority rules the agent and skill surfaces share (A8). Kept in one place so
/// <c>AgentsController</c> and <c>SkillsController</c> cannot drift apart on who may do what.
/// <para>
/// Decision D5 governs every answer here: a caller with no active organization — or asking about a
/// custom agent that belongs to another one — gets <c>404</c>, because existence never leaks. A
/// caller inside the organization whose role is too low gets <c>403</c>.
/// </para>
/// <para>
/// The project-flavoured rules (create/edit/delete a project) live in
/// <see cref="IProjectAccessService"/>; the role row for custom agents happens to match that
/// service's <see cref="ProjectPermission.Edit"/> answer, so callers use it directly instead of a
/// second copy of the same matrix.
/// </para>
/// </summary>
internal static class OrgAuthority
{
    /// <summary>
    /// Whether <paramref name="agent"/> exists for <paramref name="user"/> at all. A built-in agent
    /// is shared by every organization (the A1 check constraint lets a built-in row carry a null
    /// <c>organization_id</c> and no custom row may); a custom agent is visible only inside the
    /// organization that owns it.
    /// </summary>
    public static bool IsVisible(AgentDefinition agent, ICurrentUser user) =>
        IsVisible(agent, user.OrganizationId);

    /// <summary>
    /// The same visibility rule against an organization id the caller already holds. Exists so a
    /// caller that has read the live organization ONCE — as the assistant's gated tools do, where
    /// that read is the first executed statement of the tool body — decides both visibility and
    /// authority from one value rather than re-reading <see cref="ICurrentUser"/> and risking two
    /// answers from two reads.
    /// </summary>
    public static bool IsVisible(AgentDefinition agent, Guid? organizationId) =>
        agent.IsBuiltIn || (agent.OrganizationId is { } orgId && orgId == organizationId);

    /// <summary>
    /// The role rule on its own, with no controller to answer through: Owner and Admin govern an
    /// organization's own resources (its custom agents and their skill assignment, its
    /// bring-your-own providers); Member and Billing do not. Split out of
    /// <see cref="RequireOrgAdmin"/> (A11) so a caller that has no <c>ActionResult</c> to return —
    /// the assistant's JSON tool surface — applies the identical rule instead of restating it.
    /// </summary>
    public static bool IsOrganizationAdmin(OrganizationRole? role) =>
        role is OrganizationRole.Owner or OrganizationRole.Admin;

    /// <summary>
    /// The per-agent inference-provider override and the per-agent skill assignment are Owner/Admin
    /// actions (docs/design/tenancy.md: "Assign skills to org custom agents" is Owner/Admin, and a
    /// custom agent's provider override is that organization's own resource). Returns the failure
    /// result, or null when the caller may proceed.
    /// </summary>
    public static ActionResult? RequireOrgAdmin(ControllerBase controller, ICurrentUser user)
    {
        if (user.OrganizationId is null || user.OrgRole is null) return controller.NotFound();

        return IsOrganizationAdmin(user.OrgRole)
            ? null
            : controller.Forbid();
    }
}
