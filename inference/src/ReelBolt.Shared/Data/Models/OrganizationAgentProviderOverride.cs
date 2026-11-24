namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// An organization's inference-provider override for a BUILT-IN agent type. Built-in
/// <c>agent_definitions</c> rows are shared by every org, so their per-agent provider choice
/// cannot live on the row. Primary key: (organization, agent type).
/// </summary>
public class OrganizationAgentProviderOverride
{
    public Guid OrganizationId { get; set; }
    public AgentType AgentType { get; set; }

    /// <summary>
    /// A MODEL CAPABILITY id (name kept from when it named a provider row). Null after the capability is
    /// deleted (SetNull): the org falls back to its default.
    /// </summary>
    public Guid? InferenceProviderId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Organization Organization { get; set; } = null!;
    public ModelCapability? InferenceProvider { get; set; }
}
