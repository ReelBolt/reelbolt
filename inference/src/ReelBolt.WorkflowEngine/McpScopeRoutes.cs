using ReelBolt.Shared.Auth;

namespace ReelBolt.WorkflowEngine;

/// <summary>The only WorkflowEngine routes an MCP OAuth token (scope claim) may reach. Drift-tested against DecisionCalibrationController.</summary>
public static class McpScopeRoutes
{
    public const string DecisionCalibration = "/api/v1/workflow-engine/decision-calibration";
    public const string AdminScope = "assistant.admin";

    public static readonly McpAllowedRoute[] Allowed = [new("GET", DecisionCalibration, AdminScope)];
}
