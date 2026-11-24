using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems.Views;

namespace ReelBolt.WorkflowEngine.Services.DesignSystems;

/// <summary>
/// The engine's answer to "which design and product views does this agent see?". It deliberately
/// delegates to the Shared <see cref="DesignSystemRoleViews"/> table so there is ONE role map,
/// shared with the preview route.
/// </summary>
public static class DesignSystemRoleRegistry
{
    public static (DesignView? Design, ProductView? Product) ViewsFor(AgentType agentType) =>
        DesignSystemRoleViews.ViewsFor(agentType);
}
