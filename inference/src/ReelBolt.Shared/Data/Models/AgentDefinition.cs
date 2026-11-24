namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Defines an agent's configuration, either built-in or user-created.
/// </summary>
public class AgentDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public AgentType AgentType { get; set; }
    public bool IsBuiltIn { get; set; }
    public Guid? OwnerId { get; set; }

    /// <summary>
    /// Owning organization of a custom agent. Null only for built-ins (shared by every org);
    /// a check constraint enforces <c>is_built_in OR organization_id IS NOT NULL</c>.
    /// </summary>
    public Guid? OrganizationId { get; set; }
    public string? ConfigJson { get; set; }
    public string? OutputSchemaJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Color { get; set; }
    public string? AvailableToolsJson { get; set; }
    public bool GeneratesOutput { get; set; }
    public string? OutputSchemaName { get; set; }

    /// <summary>How much prior workflow context this agent receives.</summary>
    public ContextMode ContextMode { get; set; } = ContextMode.LastStep;

    /// <summary>When ContextMode is LastN, specifies how many prior steps to include.</summary>
    public int? ContextWindowSize { get; set; }

    /// <summary>
    /// Optional per-agent provider override: a MODEL CAPABILITY id (column and name kept from when it named
    /// an <c>inference_providers</c> row; migrated rows kept their ids). Null = use the global default provider.
    /// </summary>
    public Guid? InferenceProviderId { get; set; }

    /// <summary>
    /// Per-agent skill assignment, JSON array of skill names (e.g. ["remotion-markup"]).
    /// ONLY meaningful for a custom (non-built-in) AgentDefinition — a built-in agent's skills
    /// come exclusively from SkillCatalog.DefaultsFor(AgentType) in code, never from this column.
    /// Null on a custom agent means "no skills assigned" (NOT "inherit a default" — there is no
    /// default to inherit for a custom agent, unlike InferenceProviderId's fallback chain).
    /// </summary>
    public string? AssignedSkillsJson { get; set; }

    /// <summary>
    /// How hard this agent's model is asked to think, in the house vocabulary
    /// (<c>ReelBolt.Shared.Inference.AgentReasoningEffort</c>: <c>none</c>, <c>low</c>, <c>medium</c>,
    /// <c>xhigh</c>), persisted by NAME as a string for the same append-only reason the enums are.
    /// <b>Null means "use the code default"</b> — <c>Agents:&lt;name&gt;:ReasoningEffort</c> from
    /// configuration, then the agent class's own <c>AgentModelSettings</c>. The seeder fills a built-in
    /// row's NULL with the shipped default and never overwrites a value an admin has set.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    public ApplicationUser? Owner { get; set; }
    /// <summary>The model capability <see cref="InferenceProviderId"/> names (navigation name kept).</summary>
    public ModelCapability? InferenceProvider { get; set; }
    public ICollection<WorkflowStep> WorkflowSteps { get; set; } = new List<WorkflowStep>();
}
