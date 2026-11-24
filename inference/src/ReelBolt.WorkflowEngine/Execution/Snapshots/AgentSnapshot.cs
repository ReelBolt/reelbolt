using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Execution.Snapshots;

/// <summary>
/// One agent as a run saw it when it started: the <see cref="AgentDefinition"/> fields that change what
/// the agent does. Display-only fields (description, colour, owner, created-at) are not recorded; they
/// come from the live row, or are defaults when the row is gone.
/// </summary>
public sealed record AgentSnapshotEntry
{
    public required Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public AgentType AgentType { get; init; }
    public bool IsBuiltIn { get; init; }
    public Guid? OrganizationId { get; init; }
    public string SystemPrompt { get; init; } = string.Empty;
    public string? ConfigJson { get; init; }
    public string? OutputSchemaJson { get; init; }
    public string? OutputSchemaName { get; init; }
    public string? AvailableToolsJson { get; init; }
    public bool GeneratesOutput { get; init; }
    public ContextMode ContextMode { get; init; }
    public int? ContextWindowSize { get; init; }
    public Guid? InferenceProviderId { get; init; }
    public string? AssignedSkillsJson { get; init; }
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// The Chat model an app's <c>model</c> input chose for this agent in this run (docs/design/app-inputs.md 8.4), re-resolved
    /// at the snapshot; null when none (and then not written, so every snapshot without a choice serializes as before). It
    /// wins over every other source for this run only (the org's per-agent choice, the definition, the default); nothing is
    /// written to the agent row.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AppModelProviderId { get; init; }

    internal static AgentSnapshotEntry From(AgentDefinition a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        AgentType = a.AgentType,
        IsBuiltIn = a.IsBuiltIn,
        OrganizationId = a.OrganizationId,
        SystemPrompt = a.SystemPrompt,
        ConfigJson = a.ConfigJson,
        OutputSchemaJson = a.OutputSchemaJson,
        OutputSchemaName = a.OutputSchemaName,
        AvailableToolsJson = a.AvailableToolsJson,
        GeneratesOutput = a.GeneratesOutput,
        ContextMode = a.ContextMode,
        ContextWindowSize = a.ContextWindowSize,
        InferenceProviderId = a.InferenceProviderId,
        AssignedSkillsJson = a.AssignedSkillsJson,
        ReasoningEffort = a.ReasoningEffort
    };
}

/// <summary>
/// The agents a run's steps use, frozen at run start (<c>workflow_executions.agent_snapshot_json</c>).
/// During the run every step's <see cref="AgentDefinition"/> is the live row with these values laid over
/// it (<see cref="Overlay"/>, a detached copy that is never saved), and the chat-client seam answers the
/// agent's provider and reasoning effort from here (<see cref="AgentSnapshotAmbient"/>), so an agent
/// edited mid-run does not reach the run's later steps.
/// </summary>
public sealed class AgentSnapshot
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Dictionary<Guid, AgentSnapshotEntry> _byId;

    private AgentSnapshot(IEnumerable<AgentSnapshotEntry> entries)
    {
        _byId = [];
        foreach (AgentSnapshotEntry entry in entries)
            _byId[entry.Id] = entry;
    }

    public IReadOnlyCollection<AgentSnapshotEntry> Agents => _byId.Values;

    public static AgentSnapshot Capture(IEnumerable<AgentDefinition> agents) =>
        new(agents.Select(AgentSnapshotEntry.From));

    /// <summary>The recorded agent, or null when the run never used <paramref name="agentDefinitionId"/>.</summary>
    public AgentSnapshotEntry? Find(Guid? agentDefinitionId) =>
        agentDefinitionId is { } id && _byId.TryGetValue(id, out AgentSnapshotEntry? entry) ? entry : null;

    /// <summary>
    /// This snapshot with the app-chosen Chat models laid on the agents it records (agent id to capability id); an agent the
    /// snapshot does not record is left out (no step of the run uses it). Empty or null: this snapshot unchanged.
    /// </summary>
    public AgentSnapshot WithAppModels(IReadOnlyDictionary<Guid, Guid>? chosen)
    {
        if (chosen is not { Count: > 0 })
            return this;
        return new AgentSnapshot(_byId.Values.Select(e => chosen.TryGetValue(e.Id, out Guid model) ? e with { AppModelProviderId = model } : e));
    }

    /// <summary>The app-chosen Chat models this snapshot records (agent id to capability id), or null when none.</summary>
    public IReadOnlyDictionary<Guid, Guid>? AppModels()
    {
        Dictionary<Guid, Guid> chosen = _byId.Values
            .Where(e => e.AppModelProviderId is not null)
            .ToDictionary(e => e.Id, e => e.AppModelProviderId!.Value);
        return chosen.Count == 0 ? null : chosen;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new Document([.. _byId.Values.OrderBy(a => a.Id)]), Json);

    /// <summary>Null for a null, blank or unreadable value: the run then uses the live rows, as before snapshots.</summary>
    public static AgentSnapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            Document? document = JsonSerializer.Deserialize<Document>(json, Json);
            return document?.Agents is { } agents ? new AgentSnapshot(agents) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The agent a step runs with: a detached copy of <paramref name="row"/> with the recorded behaviour
    /// fields laid over it; rebuilt from the snapshot alone when the row is gone; <paramref name="row"/>
    /// itself when the snapshot never recorded the agent (it falls back as it always did).
    /// </summary>
    public AgentDefinition? Overlay(AgentDefinition? row, Guid agentDefinitionId)
    {
        if (Find(agentDefinitionId) is not { } frozen)
            return row;

        return new AgentDefinition
        {
            Id = agentDefinitionId,
            Description = row?.Description ?? string.Empty,
            OwnerId = row?.OwnerId,
            CreatedAt = row?.CreatedAt ?? DateTime.UtcNow,
            Color = row?.Color,
            Name = frozen.Name,
            AgentType = frozen.AgentType,
            IsBuiltIn = frozen.IsBuiltIn,
            OrganizationId = frozen.OrganizationId,
            SystemPrompt = frozen.SystemPrompt,
            ConfigJson = frozen.ConfigJson,
            OutputSchemaJson = frozen.OutputSchemaJson,
            OutputSchemaName = frozen.OutputSchemaName,
            AvailableToolsJson = frozen.AvailableToolsJson,
            GeneratesOutput = frozen.GeneratesOutput,
            ContextMode = frozen.ContextMode,
            ContextWindowSize = frozen.ContextWindowSize,
            InferenceProviderId = frozen.InferenceProviderId,
            AssignedSkillsJson = frozen.AssignedSkillsJson,
            ReasoningEffort = frozen.ReasoningEffort
        };
    }

    private sealed record Document(List<AgentSnapshotEntry> Agents);
}

/// <summary>
/// The running execution's <see cref="AgentSnapshot"/>, carried to every agent of the run through an
/// async-local scope opened by <see cref="WorkflowExecutorService.ExecuteAsync"/> (like the run's style
/// and design-system ambients). Null outside a run and for a run without a snapshot.
/// </summary>
public static class AgentSnapshotAmbient
{
    private static readonly AsyncLocal<AgentSnapshot?> CurrentSnapshot = new();

    public static AgentSnapshot? Current => CurrentSnapshot.Value;

    public static IDisposable Enter(AgentSnapshot? snapshot)
    {
        AgentSnapshot? previous = CurrentSnapshot.Value;
        CurrentSnapshot.Value = snapshot;
        return new Restore(previous);
    }

    private sealed class Restore(AgentSnapshot? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CurrentSnapshot.Value = previous;
        }
    }
}
