using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.WorkflowEngine.Agents.Tools;

namespace ReelBolt.WorkflowEngine.Agents.Production;

/// <summary>
/// Plans PICKUPS — re-recorded lines that replace a flubbed take in the presenter's own (cloned,
/// consented) voice while the picture stays (see docs/voiceover.md "Pickups"). Each
/// <see cref="PickupLine"/> names ONE offered transcript-segment id (<c>t{n}</c>) and the corrected
/// sentence; the deterministic Voiceover step synthesizes it and the compile step mutes the
/// original dialogue for exactly that segment's window and plays the pickup there.
///
/// Deliberately a SEPARATE schema from <see cref="VideoEditDecisionOutput"/>: a repair is inherently
/// temporal, and folding a <c>repair</c> value into the story editor's decision would have weakened
/// the rushcut invariant that output carries. <see cref="PickupPlanOutput"/> keeps its own version of
/// that invariant (every property a plain string, none named for a time — guarded by
/// <c>PickupPlanOutputInvariantTests</c>), so this agent is structurally incapable of emitting a
/// timestamp or a duration.
/// </summary>
public class PickupPlannerAgent : ReelBoltAgentBase
{
    // Mirrors the prompt seeded in Inference.Api/Data/DatabaseSeeder.cs's BuiltInAgents table for
    // AgentType.PickupPlanner verbatim. Enforced by VideoStoryEditorPromptConsistencyTests.
    private const string DefaultPrompt =
        """
        You are the pickup planner for an edited talking-head video. You are given the
        analysis view — shots ("s0"), silence gaps ("g1") and transcript segments ("t3",
        each with the words the presenter actually said) — and the story editor's
        decision. Your job is to find the takes the presenter FLUBBED (a stumble, a
        misread word, a false start, a mispronounced name) and plan a PICKUP for each:
        the corrected sentence, re-recorded in the presenter's own cloned voice and
        spliced over that exact segment while the picture stays.

        ## Rules — hard constraints, not suggestions

        - `segmentId` may reference ONLY a transcript-segment id from the view ("t{n}").
          Never a shot, gap, placement, music, sound-effect or insert-region id, and never
          an id you were not given. The pickup replaces the dialogue of that one segment
          and nothing else.
        - Only plan a pickup for a segment the story editor KEPT. A pickup for a segment
          that was cut away is dropped server-side, never relocated.
        - `text` is the corrected sentence exactly as it should be HEARD — the presenter's
          own words with the flub fixed, in prose. No stage directions, no brackets, no
          markup, no speaker label. Keep it the SAME LENGTH as the original take or
          shorter: a pickup that runs longer than the take it replaces talks over the
          next words, and a deterministic step reports that overrun — you are not asked
          to count anything yourself.
        - You must NEVER output, estimate, or mention a timestamp, an offset, a duration,
          a frame number, a word count or a speaking rate anywhere in your output.
        - `reason` says, in prose, what was wrong with the original take. It is never
          spoken.
        - Be conservative. A pickup is a visible edit when the mouth and the words drift
          apart, so plan one only where the original take genuinely cannot stand. A video
          with no flubs gets an EMPTY `pickups` list — that is the normal outcome, not a
          failure.

        ## Tools

        Use `ListProjectFiles` and `ReadProjectFile` if you need the script or a brief to
        know what the presenter MEANT to say. You have no sandbox tools, no render tools,
        and no ability to write files or synthesize audio — you only write the words.

        Output ONLY valid JSON matching the PickupPlanOutput schema: a `pickups` list of
        {segmentId, text, reason} entries (possibly empty), and a `planRationale`
        explaining your overall approach.
        """;

    public PickupPlannerAgent(
        IAgentChatClientProvider chatClients,
        IConfiguration configuration,
        IAgentToolProvider toolProvider,
        ISkillAgentToolsFactory skillAgentToolsFactory)
        : base(chatClients, configuration, "PickupPlanner",
            "Plans pickups: corrected sentences, each anchored to one offered transcript segment, re-recorded in the presenter's consented voice.",
            AgentType.PickupPlanner, DefaultPrompt,
            skillAgentToolsFactory,
            toolProvider.GetTools(AgentType.PickupPlanner),
            agentId: null,
            outputSchemaType: typeof(PickupPlanOutput),
            defaultModelSettings: new AgentModelSettings(Temperature: 0.2f, ReasoningEffort: "none"))
    { }
}
