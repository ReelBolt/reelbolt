using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents.Rooms;

namespace ReelBolt.WorkflowEngine.Agents.EditRoom;

/// <summary>
/// The edit room's binding of the shared, room-generic <see cref="RoomGroupChatManager"/>
/// scheduler/terminator: all it contributes is the edit room's offered-id vocabulary — the exact
/// three id-kinds <c>VideoStoryEditor</c>/<c>VideoEditDirector</c> ever choose among (shots
/// <c>s</c>, silence gaps <c>g</c>, transcript segments <c>t</c>) — never placement (<c>p</c>) or
/// music (<c>m</c>) ids, which a Keep span can never reference in the first place. Every piece of
/// behavior (round-robin scheduling, sentinel/convergence termination, turn observation, the
/// ceiling) lives unchanged in the base class, shared verbatim with
/// <see cref="GraphicsRoomGroupChatManager"/>. See docs/video-editing.md "The edit room".
/// </summary>
// Not sealed: EditRoomGroupChatManagerTests uses a minimal test-only subclass
// (TestableManager) to call ShouldTerminateAsync directly at iteration 0, the one case that
// doesn't need a full engine-driven run.
public class EditRoomGroupChatManager : RoomGroupChatManager
{
    private static readonly Regex OfferedIdMentionPattern = new(@"\b([sgt]\d{1,4})\b", RegexOptions.Compiled);

    /// <summary>Extracts edit-room offered-id-vocabulary mentions from free-form turn text — shared by convergence tracking and by the executor's chat-turn event reporting.</summary>
    public static IReadOnlyList<string> ExtractOfferedIdMentions(string? text, IReadOnlySet<string> offeredIds) =>
        ExtractIdMentions(text, offeredIds, OfferedIdMentionPattern);

    /// <param name="allParticipants">Seats in the same order as <see cref="EditRoomStepConfig.EffectiveSeats"/>, each participant registered twice and the director last (see the base constructor\'s remarks).</param>
    /// <param name="config">The step's resolved configuration.</param>
    /// <param name="offeredIds">The offered shot/silence/segment id vocabulary extracted from the bounded view, used for convergence detection only.</param>
    /// <param name="directorSeatName">The <see cref="AIAgent.Name"/> the director's <see cref="RoomSeatAgent"/> wrapper reports — used to recognise the director's own turns in history.</param>
    /// <param name="decisionClient">The advisory decision client for <see cref="RoomDecisionMode.Questions"/>/<see cref="RoomDecisionMode.Adaptive"/>, or null when no Decision provider is configured (the default). See the base constructor's <c>decisionClient</c> parameter.</param>
    /// <param name="logger">Logger for the decision path's soft-failure warnings; null falls back to a no-op logger.</param>
    public EditRoomGroupChatManager(
        IReadOnlyList<AIAgent> allParticipants,
        EditRoomStepConfig config,
        IReadOnlySet<string> offeredIds,
        string directorSeatName,
        IDecisionClient? decisionClient = null,
        ILogger? logger = null)
        : base(allParticipants, config, offeredIds, directorSeatName, OfferedIdMentionPattern, decisionClient, logger)
    {
    }
}
