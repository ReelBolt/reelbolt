using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// Metadata describing how an edited timeline item was created or modified.
/// </summary>
/// <remarks>
/// <b>Ids and names are populated by different paths, and neither is derived from the other.</b>
/// The <see cref="Guid"/> fields (<see cref="StepResultId"/>, <see cref="AgentDefinitionId"/>,
/// <see cref="ProviderId"/>) are filled by v2-native creation paths, which hold the entities
/// themselves. The display-name fields (<see cref="AgentName"/>, <see cref="ProviderName"/>) are
/// filled by a v1 seed: a <c>TimelineManifest</c> records only the agent's and provider's NAMES
/// and no ids, and a pure seeder has no database in which to look an id up. A name is therefore
/// copied verbatim when the source carried one and left <c>null</c> when it did not — never
/// synthesized, and never resolved from the other field.
/// </remarks>
public record Provenance(
    ProvenanceOrigin Origin,
    Guid? StepResultId = null,
    Guid? AgentDefinitionId = null,
    Guid? ProviderId = null,
    string? OfferedId = null,
    string? Rationale = null,
    bool EditedByUser = false,
    string? AgentName = null,
    string? ProviderName = null
)
{
    /// <summary>
    /// How this item was created (manual, generated, imported, or analyzed).
    /// </summary>
    public ProvenanceOrigin Origin { get; } = Origin;

    /// <summary>
    /// Optional workflow step result that generated this item.
    /// </summary>
    public Guid? StepResultId { get; } = StepResultId;

    /// <summary>
    /// Optional agent that created or modified this item.
    /// </summary>
    public Guid? AgentDefinitionId { get; } = AgentDefinitionId;

    /// <summary>
    /// Optional inference provider used to generate this item.
    /// </summary>
    public Guid? ProviderId { get; } = ProviderId;

    /// <summary>
    /// Optional offered ID from analysis or decision output.
    /// </summary>
    public string? OfferedId { get; } = OfferedId;

    /// <summary>
    /// Optional explanation of why this item was chosen or created.
    /// </summary>
    public string? Rationale { get; } = Rationale;

    /// <summary>
    /// Whether the user has manually edited or confirmed this item.
    /// </summary>
    public bool EditedByUser { get; } = EditedByUser;

    /// <summary>
    /// Optional display name of the agent that created or modified this item. A v2-native creation
    /// path leaves it null and fills <see cref="AgentDefinitionId"/> instead; a v1 seed fills it
    /// from the manifest's own <c>AgentName</c> and leaves the id null. Never synthesized.
    /// </summary>
    public string? AgentName { get; } = AgentName;

    /// <summary>
    /// Optional display name of the inference provider used to generate this item. Same split as
    /// <see cref="AgentName"/>: a v1 seed fills the name it was given, a v2-native path fills
    /// <see cref="ProviderId"/>.
    /// </summary>
    public string? ProviderName { get; } = ProviderName;
}

/// <summary>
/// Reference to an asset used in editing (a project file or workflow output).
/// </summary>
public record AssetRef(
    AssetRefKind Kind,
    Guid? ProjectFileId = null,
    Guid? StepResultId = null
)
{
    /// <summary>
    /// Type of asset reference (project file or step result).
    /// </summary>
    public AssetRefKind Kind { get; } = Kind;

    /// <summary>
    /// Reference to a project file when Kind is ProjectFile.
    /// </summary>
    public Guid? ProjectFileId { get; } = ProjectFileId;

    /// <summary>
    /// Reference to a workflow step result when Kind is StepResult.
    /// </summary>
    public Guid? StepResultId { get; } = StepResultId;
}
