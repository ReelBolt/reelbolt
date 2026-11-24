namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Indicates who initiated a workflow execution.
/// </summary>
public enum ExecutionInitiator
{
    /// <summary>A human user initiated the execution via API.</summary>
    Human,

    /// <summary>An AI assistant initiated the execution via MCP.</summary>
    Assistant,

    /// <summary>An MCP client initiated the execution.</summary>
    Mcp,

    /// <summary>The public v1 API started the run through an API key (public-api T-007). Appended: the stored
    /// value is the enum's integer, so existing rows are unchanged.</summary>
    Api
}
