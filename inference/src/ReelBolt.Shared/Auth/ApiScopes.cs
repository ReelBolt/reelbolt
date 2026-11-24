namespace ReelBolt.Shared.Auth;

/// <summary>The scopes an API key may carry. Same dot style as the MCP scopes; the two sets never overlap.</summary>
public static class ApiScopes
{
    public const string ProjectsRead = "projects.read";
    public const string ProjectsWrite = "projects.write";
    public const string FilesRead = "files.read";
    public const string FilesWrite = "files.write";
    public const string WorkflowsRead = "workflows.read";
    public const string WorkflowsWrite = "workflows.write";
    public const string RunsRead = "runs.read";
    public const string RunsExecute = "runs.execute";
    public const string OutputsRead = "outputs.read";
    public const string WebhooksManage = "webhooks.manage";

    /// <summary>
    /// Lets a key allow sending the customer's media to AI services for a run it starts
    /// (<c>inputs.allowMediaEgress = true</c>; docs/public-api.md "Allowing media to be sent"). No endpoint requires it:
    /// the run start checks it only when the run's own request sets that value. Off unless an Owner/Admin ticks it.
    /// </summary>
    public const string MediaEgress = "media.egress";

    public static readonly IReadOnlyList<string> All =
    [
        ProjectsRead, ProjectsWrite, FilesRead, FilesWrite, WorkflowsRead, WorkflowsWrite,
        RunsRead, RunsExecute, OutputsRead, WebhooksManage, MediaEgress
    ];

    public static bool IsKnown(string? scope) => scope is not null && All.Contains(scope, StringComparer.Ordinal);
}
