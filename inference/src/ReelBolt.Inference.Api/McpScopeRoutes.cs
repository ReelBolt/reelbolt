using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api;

/// <summary>The only Inference API routes an MCP OAuth token (scope claim) may reach. Drift-tested against AssistantToolBridgeController.</summary>
public static class McpScopeRoutes
{
    public const string ToolCatalog = "/api/v1/assistant/tool-catalog";
    public const string ToolCall = "/api/v1/assistant/tool-call";
    public const string ToolsScope = "assistant.tools";
    public const string AdminScope = "assistant.admin";

    /// <summary>Run-watching scope: reaches only the tool-call route, and the bridge then allows only GetExecutionStatus.</summary>
    public const string RunsWatchScope = "runs.watch";

    public static readonly McpAllowedRoute[] Allowed =
    [
        new("GET", ToolCatalog, ToolsScope, AdminScope),
        new("POST", ToolCall, ToolsScope, AdminScope),
        new("POST", ToolCall, RunsWatchScope),
        // The admin calibration read, proxied to the WorkflowEngine (WorkflowEngineProxyController).
        new("GET", Services.Assistant.DecisionCalibrationClient.CalibrationPath, AdminScope)
    ];
}
