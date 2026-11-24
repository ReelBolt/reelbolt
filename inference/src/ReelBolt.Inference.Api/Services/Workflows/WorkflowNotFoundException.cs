namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Thrown when a workflow or project is not found during execution.
/// </summary>
public sealed class WorkflowNotFoundException : Exception
{
    public WorkflowNotFoundException() : base("Workflow or project not found.") { }

    public WorkflowNotFoundException(string message) : base(message) { }

    public WorkflowNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
