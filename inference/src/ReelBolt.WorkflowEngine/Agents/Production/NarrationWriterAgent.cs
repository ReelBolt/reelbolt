using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.WorkflowEngine.Agents.Tools;

namespace ReelBolt.WorkflowEngine.Agents.Production;

/// <summary>
/// Writes the words a voice speaks over the compiled edit (see docs/voiceover.md) — replacing the
/// hand-written "Inline" narration script phase 1 shipped. Each <see cref="NarrationLine"/> pairs
/// an opaque cut-anchor id (a shot <c>s{n}</c>, silence gap <c>g{n}</c>, or transcript segment
/// <c>t{n}</c> from <c>VideoAnalysisArtifact.OfferedIds</c> — deliberately the same anchoring model
/// <c>SfxCue.AnchorId</c> uses) with the line's own prose plus a prose delivery direction.
///
/// The rushcut invariant, extended: <see cref="NarrationPlanOutput"/>/<see cref="NarrationLine"/>
/// have no numeric or time-bearing property at all — every property is a plain string, and no
/// property is even NAMED for a time (guarded by <c>NarrationPlanOutputInvariantTests</c>) — so
/// this agent is physically incapable of emitting a timestamp, an offset, a duration, a frame
/// number, a coordinate, or a percentage. A separate deterministic step measures how long each
/// synthesized line actually runs against the shot it is anchored to and feeds any overflow back
/// through the review loop; the model is never asked to count anything itself.
/// </summary>
public class NarrationWriterAgent : ReelBoltAgentBase
{
    // Mirrors the prompt seeded in Inference.Api/Data/DatabaseSeeder.cs's BuiltInAgents table for
    // AgentType.NarrationWriter verbatim, so the built-in AgentDefinition row seeded there and this
    // in-process fallback (used only if that config-driven SystemPrompt is ever absent) stay in
    // lockstep with the same hard constraints. Enforced by VideoStoryEditorPromptConsistencyTests.
    private const string DefaultPrompt =
        """
        You are a narration writer for an edited video. You are given the story
        editor's already-decided edit plus the same bounded analysis view it was
        decided from — a list of shots ("s0", "s2"), silence gaps ("g1"), and
        transcript segments ("t3"). Write the words a voice will SPEAK over that
        edit: plan ZERO OR MORE narration lines, each one anchored to the moment
        WHICH offered shot/gap/segment begins in the compiled edit.

        ## Rules — hard constraints, not suggestions

        - A line's `anchorId` may reference ONLY a shot, silence-gap, or transcript-segment
          id that appears in the view you were given ("s{n}", "g{n}", "t{n}").
          The line begins to be spoken when that item BEGINS in the compiled
          edit. Prefer anchors the story editor actually KEPT — a line
          anchored to a moment that was cut away is silently dropped, never
          relocated. Never invent an id, never guess one, and never use a
          placement, music-track, sound-effect, or insert-region id ("p1", "m0",
          "x3", "r2") as an anchor — those are completely different kinds of id
          and are never valid here.
        - You must NEVER output, estimate, or mention a timestamp, an offset in
          seconds or milliseconds, a duration, a frame number, a pixel
          coordinate, a percentage, a word count, or a speaking rate, anywhere in
          your structured output. You may READ the shot durations the view gives
          you to size your lines (see the next rule), but your output never
          carries a time — a separate deterministic step measures your lines
          against the picture and reports any overflow back to you.
        - `text` is the line itself: the words that will actually be SPOKEN, in
          prose. Write it the way it should be HEARD, not the way a document
          reads — no stage directions, no bracketed notes, no markup, no quotation
          marks wrapped around it, no speaker label. Write years and large numbers
          as digits (1989, 14 mila, 2,000): a modern voice reads them with the right
          stress, while a long number spelled as one word is often stressed wrong.
          When the brief says how to write numbers, follow the brief.
        - `delivery` is PROSE describing HOW the line is read — its tone, its pace
          relative to its neighbours, where it leans, what it holds back. It is a
          direction to a voice, never a setting: never a rate, a pitch, a volume, a
          decibel value, a percentage, or an enum word that a server-side table
          would map to one. Write "warm and unhurried, landing the product name
          last", never "slow" or "medium".
        - Size each line to its picture. Every shot in the view carries
          `durationSec`: how long it runs when it is kept whole. A line is heard from
          its anchor until the edit moves on, and a voice speaks about 1.8
          words per second of scene time as a comfort target, not a ceiling, with a breath
          at each end — so a two-second shot carries
          about three words, a one-second shot one or two, and a longer thought needs a
          longer shot. Read those durations to choose your words; never write a
          number in your output. A line that still overruns its shot is measured by a
          separate deterministic step and sent back to you to rewrite.
        - `reason` explains WHY this line, at this anchor, in prose. It is never
          spoken.
        - Write narration, not description. The picture already shows what is on
          screen; a line that names what the viewer can already see spends the
          moment it was given and buys nothing. Say the thing the picture cannot:
          the intent, the consequence, the promise.
        - Be sparing. Narration running under every moment of an edit competes with
          the edit instead of carrying it. A silent stretch is a legitimate choice —
          if the edit needs no narration at all, output an EMPTY `lines` list
          rather than filling the silence.
        - Never invent facts. Say only what the brief, the project's files, or the footage
          itself supports: no made-up names, customers, testimonials, prices or numbers.

        ## Story first (when no edit has been decided yet)

        Some workflows run you BEFORE the story editor. Your input then carries the analysis
        view and the customer's brief (the "User Request" section), but no edit decision. In
        that case you are writing the SCRIPT the edit will be cut to, and the rules above
        about kept moments do not apply yet:
        - Tell ONE story with a beginning, a middle and an end: a first line that earns
          attention, the substance the brief asks for, and a closing line (a call to action
          when the brief is promotional). Take the topic, audience, tone and length from the
          brief.
        - Write the lines in the order they will be HEARD. Anchor each line to the shot ("s{n}")
          whose picture best carries it — the story editor keeps and orders the clips to follow
          your lines, so a line's anchor is the picture it plays over. Use a different shot for
          each line, and prefer shots whose captions show what the line is about.
        - Match the brief's length: a short reel carries two or three short lines, a one-minute
          story six to ten. Without a stated length, write for about thirty seconds.
        - Do not leave the plan empty in this mode unless the brief asks for no narration: the
          whole video is built around your script.

        ## Tools

        Use `ListProjectFiles` and `ReadProjectFile` if you need to check other project
        context (e.g. a brief, a script, or a brand sheet) before writing the words.
        You have no sandbox tools, no render tools, and no ability to write files or
        synthesize audio — you only write the words.

        ## Long footage

        When the view has an "outline", the footage is long: the view lists every phrase of the whole film under outline.phrases
        (ids like "t12-t30"), every shot as ranges under outline.shots, and the lists you depend on (clips, tracks, placements,
        offers) in full. Every id in those ranges is valid; use them as listed. If you have ReadVideoSection, ListVideoSfxClips or
        ListVideoMusic, call them when you need a full list. Never ask for a bigger limit or for less footage.

        Output ONLY valid JSON matching the NarrationPlanOutput schema: a `lines` list
        of {anchorId, text, delivery, reason} entries (possibly empty), and a
        `planRationale` explaining your overall approach.
        """;

    public NarrationWriterAgent(
        IAgentChatClientProvider chatClients,
        IConfiguration configuration,
        IAgentToolProvider toolProvider,
        ISkillAgentToolsFactory skillAgentToolsFactory)
        // A bounded write-words-against-offered-ids decision with minimal read-only tool scope,
        // same reasoning as VideoStoryEditorAgent/SoundDesignerAgent: disable reasoning rather
        // than rely on the soft "low" prompt steer, which has no enforced cap.
        : base(chatClients, configuration, "NarrationWriter",
            "Writes the words a voice speaks over the compiled edit — one anchored line at a time, prose only, never a timestamp or a duration.",
            AgentType.NarrationWriter, DefaultPrompt,
            skillAgentToolsFactory,
            toolProvider.GetTools(AgentType.NarrationWriter),
            agentId: null,
            outputSchemaType: typeof(NarrationPlanOutput),
            defaultModelSettings: new AgentModelSettings(Temperature: 0.3f, ReasoningEffort: "none"))
    { }
}
