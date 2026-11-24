namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A user-defined or built-in workflow composed of ordered steps.
/// </summary>
public class WorkflowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid ProjectId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When true, users must provide a free-text request (UserRequest) when executing this workflow.
    /// The request is passed as context to all agents throughout the run.
    /// When false, workflows execute without user input (UserRequest is null).
    /// </summary>
    public bool RequiresUserInput { get; set; } = false;

    /// <summary>
    /// <see cref="WorkflowDefinitionKind.Standard"/> for every authored workflow;
    /// <see cref="WorkflowDefinitionKind.CompositionRevision"/> for the project's hidden system
    /// workflow that carries composition revisions (never listed, edited or executed directly).
    /// </summary>
    public WorkflowDefinitionKind Kind { get; set; } = WorkflowDefinitionKind.Standard;

    /// <summary>
    /// The app manifest (<c>docs/workflow-apps.md</c>): how this workflow is presented as an app. Null for
    /// a workflow that never had one authored; the effective manifest is then DERIVED from the steps and
    /// never persisted. Engine-owned column <c>workflow_definitions.app_manifest_json</c> (jsonb).
    /// </summary>
    public string? AppManifestJson { get; set; }

    /// <summary>
    /// What created the workflow: <see cref="WorkflowCreatedVia.User"/>, <see cref="WorkflowCreatedVia.Assistant"/>,
    /// <see cref="WorkflowCreatedVia.Template"/> or <see cref="WorkflowCreatedVia.Marketplace"/>. Null reads as user (every row that predates the column).
    /// </summary>
    public string? CreatedVia { get; set; }

    /// <summary>
    /// The assistant thread that created the workflow (<see cref="WorkflowCreatedVia.Assistant"/> only), so a
    /// run can be traced to its conversation. No foreign key (the thread belongs to another owner); cleared
    /// when the thread or the account is deleted.
    /// </summary>
    public Guid? CreatedInThreadId { get; set; }

    /// <summary>The template key a <see cref="WorkflowCreatedVia.Template"/> workflow was provisioned from; null otherwise.</summary>
    public string? TemplateKey { get; set; }

    public Project Project { get; set; } = null!;
    public ICollection<WorkflowStep> Steps { get; set; } = new List<WorkflowStep>();
    public ICollection<WorkflowExecution> Executions { get; set; } = new List<WorkflowExecution>();
}

/// <summary>The closed set of <see cref="WorkflowDefinition.CreatedVia"/> values.</summary>
public static class WorkflowCreatedVia
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Template = "template";

    /// <summary>A copy of a recipe from the community marketplace (<c>TemplateKey = "market:&lt;recipeId:N&gt;"</c>).</summary>
    public const string Marketplace = "marketplace";

    public static bool IsValid(string? value) => value is User or Assistant or Template or Marketplace;
}
