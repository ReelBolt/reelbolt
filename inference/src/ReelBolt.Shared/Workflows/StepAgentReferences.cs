using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows;

/// <summary>One agent a step's config names besides the step's own <see cref="WorkflowStep.AgentDefinitionId"/>.</summary>
/// <param name="Field">The config field, as an author would recognise it (e.g. <c>directorAgentDefinitionId</c>).</param>
public sealed record StepAgentReference(string Field, Guid AgentDefinitionId);

/// <summary>
/// The one place that knows which step config fields name an agent: a Parallel step's extra agents
/// (<c>ParallelAgentIdsJson</c>) and a room's custom seat and director agents
/// (<see cref="EditRoomSeat.AgentDefinitionId"/>, <see cref="IRoomStepConfig.DirectorAgentDefinitionId"/>)
/// for the Edit, Graphics, ColorGrade and Dailies rooms. A built-in seat or director carries no id and
/// names nothing. The API's save-time check (<c>StepProviderOwnershipValidator</c>) and the engine's run
/// snapshot (<c>ExecutionSnapshotter</c>) both read it, so they cannot disagree about what a step names.
/// An unreadable config names nothing here; the config validators report it.
/// </summary>
public static class StepAgentReferences
{
    /// <summary>The options the room executors read their configs with.</summary>
    private static readonly JsonSerializerOptions RoomConfigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<StepAgentReference> Extract(WorkflowStep step) =>
        Extract(step.StepType, step.ParallelAgentIdsJson, step.EditRoomConfigJson, step.GraphicsRoomConfigJson,
            step.ColorGradeRoomConfigJson, step.DailiesRoomConfigJson);

    public static IReadOnlyList<StepAgentReference> Extract(
        StepType stepType,
        string? parallelAgentIdsJson,
        string? editRoomConfigJson,
        string? graphicsRoomConfigJson,
        string? colorGradeRoomConfigJson,
        string? dailiesRoomConfigJson)
    {
        try
        {
            if (stepType == StepType.Parallel)
            {
                return string.IsNullOrWhiteSpace(parallelAgentIdsJson)
                    ? []
                    : [.. (JsonSerializer.Deserialize<Guid[]>(parallelAgentIdsJson) ?? []).Select(id => new StepAgentReference("parallelAgentIds", id))];
            }

            IRoomStepConfig? room = stepType switch
            {
                StepType.EditRoom when !string.IsNullOrWhiteSpace(editRoomConfigJson)
                    => JsonSerializer.Deserialize<EditRoomStepConfig>(editRoomConfigJson, RoomConfigJson),
                StepType.GraphicsRoom when !string.IsNullOrWhiteSpace(graphicsRoomConfigJson)
                    => JsonSerializer.Deserialize<GraphicsRoomStepConfig>(graphicsRoomConfigJson, RoomConfigJson),
                StepType.ColorGradeRoom when !string.IsNullOrWhiteSpace(colorGradeRoomConfigJson)
                    => JsonSerializer.Deserialize<ColorGradeRoomStepConfig>(colorGradeRoomConfigJson, RoomConfigJson),
                StepType.DailiesRoom when !string.IsNullOrWhiteSpace(dailiesRoomConfigJson)
                    => JsonSerializer.Deserialize<DailiesRoomStepConfig>(dailiesRoomConfigJson, RoomConfigJson),
                _ => null
            };
            if (room is null)
                return [];

            List<StepAgentReference> references = [];
            IReadOnlyList<EditRoomSeat> seats = room.EffectiveSeats;
            for (int i = 0; i < seats.Count; i++)
            {
                if (seats[i].AgentDefinitionId is { } seatAgent)
                    references.Add(new StepAgentReference($"seats[{i}].agentDefinitionId", seatAgent));
            }

            if (room.DirectorAgentDefinitionId is { } director)
                references.Add(new StepAgentReference("directorAgentDefinitionId", director));
            return references;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
