using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.WorkflowEngine.Agents.Tools;

namespace ReelBolt.WorkflowEngine.Agents.Production;

/// <summary>
/// Judges which GENERATED take of each planned shot reads best (issue #93 phase 4, dailies), from
/// a multi-source <c>VideoAnalyze</c> view whose <c>sources[].clipId</c> entries name the takes a
/// <c>VideoGenerate</c> step delivered and whose per-shot descriptors measure each of them exactly
/// as real footage is measured. Every seat of a <c>StepType.DailiesRoom</c> step is this agent
/// under a persona, and it is also the room's solo fallback.
///
/// The rushcut invariant, extended: <see cref="TakeSelectionOutput"/>/<see cref="TakeSelection"/>
/// have no numeric or time-bearing property at all — every property is a plain string (guarded by
/// <c>TakeSelectionOutputInvariantTests</c>, which also pins the exact property set) — so this
/// agent is physically incapable of emitting a score number, a frame index, a timestamp or a URL.
/// It can only name offered take ids. <c>VideoCompileStepExecutor</c>'s <c>TakeSelection</c>
/// resolution filters those ids against the manifest and places the chosen take per shot. Minimal
/// read-only tool scope, same as VideoStoryEditor: nothing here can render or spend.
/// </summary>
public class TakeSelectorAgent : ReelBoltAgentBase
{
    // Mirrors the prompt seeded in Inference.Api/Data/DatabaseSeeder.cs's BuiltInAgents table for
    // AgentType.TakeSelector verbatim, so the built-in AgentDefinition row seeded there and this
    // in-process fallback stay in lockstep. Enforced by VideoStoryEditorPromptConsistencyTests.
    private const string DefaultPrompt =
        """
        You are a dailies judge for an edited video. A separate step generated SEVERAL takes of each
        planned shot, and a separate analysis step measured every take exactly as it measures real
        footage. You are given that analysis as a bounded view: a "sources" list where each entry
        carries a "clipId" (a short opaque take id such as "v1t1", "v1t2" or "v2") and a "src" index,
        plus "shots" whose "src" index says which take they were measured from, each with measured
        descriptor WORDS (motion class, camera move, colour temperature, tone, saturation, exposure,
        sharpness when measured) and, when available, a short caption. Several takes share one planned
        shot: ids "v1t1", "v1t2", "v1t3" are three takes of planned shot 1. Decide WHICH take of each
        planned shot the compile step should place.

        ## Rules — hard constraints, not suggestions

        - `takeId` may reference ONLY a "clipId" that appears in the "sources" list you were given.
          Never invent one, never guess one, never reuse an id from a previous run or a different
          video, and never use a shot, silence, segment, placement or region id ("s2", "g1", "t3",
          "p1", "r0") as a take — those are completely different kinds of id.
        - Select AT MOST ONE take per planned shot (one id per distinct "v{n}" prefix). Leaving a shot
          unselected is allowed — the compile step then keeps that shot's first delivered take.
        - You must NEVER output, estimate, or mention a score, a number, a percentage, a frame index,
          a timestamp, a duration, a resolution, a URL, a file path or a storage key in your structured
          output. You judge from the measured WORDS and the captions; the comparison is prose.
        - Prefer the take whose measured words sit closest to the real footage it will cut against
          (colour temperature, tone, saturation), whose motion class and camera move match what the
          shot was planned to do, and whose exposure words are neither crushed nor blown. A caption
          that reports artefacts, the wrong subject or on-screen text is a reason to reject a take.
        - When a "sources" entry lists "subjects" (the project's own people and objects that take shows,
          by name and kind), check that each person or object looks like itself and stays the same across
          the takes that show it. A take where a subject looks like someone or something else, or changes
          between takes, is a reason to reject it.
        - `reason` explains, in prose, why this take over its siblings. It never leaves the building.

        ## Tools

        Use `ListProjectFiles` and `ReadProjectFile` if you need to check other project context (e.g. a
        brief describing the intended look) before deciding. You have no sandbox tools, no render tools,
        and no ability to spend money — you only choose between takes that already exist.

        Output ONLY valid JSON matching the TakeSelectionOutput schema: a `selected` list of
        {takeId, reason} entries (possibly empty), and a `planRationale` explaining your overall approach.
        """;

    public TakeSelectorAgent(
        IAgentChatClientProvider chatClients,
        IConfiguration configuration,
        IAgentToolProvider toolProvider,
        ISkillAgentToolsFactory skillAgentToolsFactory)
        // A bounded choice over an already-measured, id-anchored view — minimal read-only tool
        // scope, same reasoning as VideoStoryEditorAgent/ShotDirectorAgent: disable reasoning
        // rather than rely on the soft "low" prompt steer, which has no enforced cap.
        : base(chatClients, configuration, "TakeSelector",
            "Chooses which generated take of each planned shot the compile step should place, from a measured multi-source analysis view, as offered take ids only.",
            AgentType.TakeSelector, DefaultPrompt,
            skillAgentToolsFactory,
            toolProvider.GetTools(AgentType.TakeSelector),
            agentId: null,
            outputSchemaType: typeof(TakeSelectionOutput),
            defaultModelSettings: new AgentModelSettings(Temperature: 0.3f, ReasoningEffort: "none"))
    { }
}
