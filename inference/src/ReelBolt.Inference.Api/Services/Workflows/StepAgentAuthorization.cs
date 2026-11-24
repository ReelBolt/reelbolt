using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// A8 on a step's OWN agent (<see cref="CreateWorkflowStepRequest.AgentDefinitionId"/>): it must be a
/// platform built-in or one of the project's organization's agents. The one implementation every path
/// that writes workflow steps runs before writing anything (REST create/update's
/// <c>InvalidStepAgentsAsync</c> applies the same rule and message): the assistant/MCP ProposeWorkflow and
/// ApplyWorkflow tools and both branches of the assistant controller's apply (SQFU-S7). Same lookup as
/// REST: the tenant query filter scopes it, and the explicit organization comparison is the second,
/// independent enforcement. A missing row and another organization's row get the same message.
/// </summary>
public static class StepAgentAuthorization
{
    public const string RefusalMessage = "Invalid or unauthorized agent";

    public static async Task<bool> AllUsableAsync(
        InferenceApiDbContext db, Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct)
    {
        List<Guid> ids = [.. steps.Select(s => s.AgentDefinitionId).Distinct()];
        int usable = await db.AgentDefinitions
            .CountAsync(a => ids.Contains(a.Id) && (a.IsBuiltIn || a.OrganizationId == organizationId), ct);
        return usable == ids.Count;
    }

    /// <summary>The synchronous twin, for the assistant's synchronous ProposeWorkflow tool.</summary>
    public static bool AllUsable(InferenceApiDbContext db, Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps)
    {
        List<Guid> ids = [.. steps.Select(s => s.AgentDefinitionId).Distinct()];
        int usable = db.AgentDefinitions
            .Count(a => ids.Contains(a.Id) && (a.IsBuiltIn || a.OrganizationId == organizationId));
        return usable == ids.Count;
    }
}
