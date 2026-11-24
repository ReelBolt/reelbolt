using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Agents.Rooms;

/// <summary>
/// The dailies room's binding of the shared, room-generic <see cref="RoomGroupChatManager"/>
/// scheduler/terminator (issue #93 phase 4): all it contributes is the room's offered-id
/// vocabulary — the generated-TAKE namespace (<c>v{n}</c> and <c>v{n}t{k}</c>), nothing else.
/// Deliberately narrower than the edit room's <c>[sgt]</c> pattern: a judge mentioning the real
/// shot a take will cut against ("it matches s2's warm look") must never count toward
/// convergence — only the takes themselves are the decision. Everything else (scheduling,
/// sentinel, convergence, ceiling) is the base class, unchanged. See docs/video-generation.md
/// "Dailies: choosing between takes".
/// </summary>
public class DailiesRoomGroupChatManager : RoomGroupChatManager
{
    private static readonly Regex OfferedIdMentionPattern = new(@"\b(v\d{1,4}(?:t\d{1,2})?)\b", RegexOptions.Compiled);

    /// <summary>Extracts dailies-room offered-take-id mentions from free-form turn text — shared by convergence tracking and by the executor's chat-turn event reporting.</summary>
    public static IReadOnlyList<string> ExtractOfferedIdMentions(string? text, IReadOnlySet<string> offeredIds) =>
        ExtractIdMentions(text, offeredIds, OfferedIdMentionPattern);

    /// <param name="allParticipants">Seats in the same order as <see cref="DailiesRoomStepConfig.EffectiveSeats"/>, each participant registered twice and the director last (see the base constructor's remarks).</param>
    /// <param name="config">The step's resolved configuration.</param>
    /// <param name="offeredIds">The offered take-id vocabulary extracted from the bounded view's <c>view.sources[].clipId</c>, used for convergence detection and decision filtering.</param>
    /// <param name="directorSeatName">The <see cref="AIAgent.Name"/> the director's <see cref="RoomSeatAgent"/> wrapper reports.</param>
    /// <param name="decisionClient">The advisory decision client for <see cref="RoomDecisionMode.Questions"/>/<see cref="RoomDecisionMode.Adaptive"/>, or null when no Decision provider is configured (the default).</param>
    /// <param name="logger">Logger for the decision path's soft-failure warnings; null falls back to a no-op logger.</param>
    public DailiesRoomGroupChatManager(
        IReadOnlyList<AIAgent> allParticipants,
        DailiesRoomStepConfig config,
        IReadOnlySet<string> offeredIds,
        string directorSeatName,
        IDecisionClient? decisionClient = null,
        ILogger? logger = null)
        : base(allParticipants, config, offeredIds, directorSeatName, OfferedIdMentionPattern, decisionClient, logger)
    {
    }
}
