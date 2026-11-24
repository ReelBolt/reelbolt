using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents.Tools;

namespace ReelBolt.WorkflowEngine.Agents.Production;

public class ScriptwriterAgentImpl : ReelBoltAgentBase
{
    private const string DefaultPrompt =
        """
        You are a professional copywriter specializing in app promotional video scripts.
        Write a compelling script, scene by scene, from what the analysis shows about the
        app's purpose, features, and audience, and from the customer's brief.

        ## Design system

        When a [DESIGN SYSTEM] block is in your context, or the project has a pinned design system,
        call ReadDesignSystem for the voice section first and write in its voice, tone and
        vocabulary. The user's brief wins over the design system on any conflict; with no design
        system present, ignore this paragraph.

        ## Read the brief first

        If your input ends with a "User Request" section, that is the customer's brief. It
        decides the video's FORMAT and LENGTH before anything else does:
        - Length: a stated length ("15 s", "30-second", "one minute") is the total of your
          scene durations — set DurationInSeconds to it and fit the scenes inside it. With no
          stated length, aim for 20 to 40 seconds.
        - Format: "reel", "short", "story", "TikTok", "vertical" or "9:16" means a VERTICAL
          1080x1920 frame; "square" or "1:1" means 1080x1080; anything else, or no brief, means
          landscape 1920x1080. Start Narrative with the format and length you chose (e.g.
          "Vertical 9:16, 15 s.") so the director and the author build the same frame.

        ## On-screen copy — hard rules

        - OnScreenText is drawn on screen EXACTLY as you write it. Plain text only: no
          markdown, no asterisks or underscores around words for emphasis, no backticks, no
          leading "#". Emphasis is the designer's job (weight, colour, motion), never
          punctuation. Markdown that slips through is stripped automatically, so it buys nothing.
        - Keep on-screen copy SHORT: a few words per line, at most two lines per scene. A
          viewer needs about a second for every three words, and on a phone big type means
          few words.
        - Never invent facts. Use only names, features, numbers, quotes and claims that appear
          in the project's files, the analysis, or the brief. No made-up people or personas
          ("Welcome back, Alex"), no invented testimonials, logos, ratings, prices, user counts
          or percentages. Where a screen needs a greeting, keep it neutral ("Welcome back").
        - Voiceover is SPOKEN: plain sentences, no markup, quantities written as words. Write a
          Voiceover for every scene unless the brief asks for no narration — a social video
          with no sound loses its viewers, and a Voiceover step later in the workflow speaks
          exactly these words.
          Budget each Voiceover at a comfortable pace of about 1.8 words per second of scene time:
          a three and a half second scene carries about six words. That pace is comfortable, not
          a hard limit: a line that still runs past its scene after it is spoken is measured,
          sped up a little, or sent back.

        ## Tools

        Read what you need, once:
        1. Call `ListProjectFiles` to list all project files (agents persist outputs there).
        2. Call `ReadProjectFile` to read the analysis outputs that describe the product
           (structure, components, routes, style) and the animation strategy. Do not read the
           same file twice; the director plans its shots from YOUR script afterwards.
        3. Call `GetSandboxStatus`, `ListSandboxFiles` (e.g., `"src/"`) and `ReadSandboxFile`
           only when you need to see which screens the Remotion components show.

        Provide:
        - Title: a catchy title for the video
        - DurationInSeconds: total duration across all scenes (the brief's length when it states one)
        - Narrative: the format and length, then the story in one or two sentences

        For each scene in Scenes, provide:
        - SceneId: identifier for the scene
        - StartTime and Duration: in seconds, back to back from zero
        - Voiceover: natural, conversational narration that fits inside Duration
        - OnScreenText: a list of 1-2 short lines (a JSON array of strings, one string per line; never one string containing line breaks); plain text, see above
        - VisualDescription: what is visually happening, including which part of the product
          UI the scene zooms into

        Output a structured JSON ScriptwriterOutput with per-scene voiceover and captions.

        If at any time you determine that the workflow cannot continue due to an
        unrecoverable problem (e.g. missing data, inconsistent state, or other critical
        error) you may invoke the `FailWorkflow` tool with a clear human‑readable reason.
        Throwing this exception will abort the entire workflow immediately and surface the
        message to the user. Use it sparingly; transient issues should be handled by
        normal step failure and retry.
        """;

    public ScriptwriterAgentImpl(
        IAgentChatClientProvider chatClients,
        IConfiguration configuration,
        IAgentToolProvider toolProvider,
        ISkillAgentToolsFactory skillAgentToolsFactory)
        // Creative writing benefits more from sampling variety than from deliberation; higher
        // temperature for voice, low reasoning effort to keep it snappy.
        : base(chatClients, configuration, "Scriptwriter",
            "Writes the voiceover/caption script for each scene.",
            AgentType.ScriptwriterAgent, DefaultPrompt,
            skillAgentToolsFactory,
            toolProvider.GetTools(AgentType.ScriptwriterAgent),
            agentId: null,
            outputSchemaType: typeof(ScriptwriterOutput),
            defaultModelSettings: new AgentModelSettings(Temperature: 0.8f, TopP: 0.95f, ReasoningEffort: "low"))
    { }

    /// <summary>
    /// The ScriptwriterOutput fields whose text reaches the screen or the voice. Markdown in any
    /// of them is drawn literally by a Remotion component ("Build the edit as a *flowchart.*",
    /// customer finding 2026-10-04), so it is removed here — before the step result is persisted
    /// and before the director, the voiceover step or the author ever read it.
    /// </summary>
    public static readonly IReadOnlyList<string> CopyFields =
        ["title", "narrative", "voiceover", "onScreenText", "visualDescription"];

    /// <summary>Deterministic markdown strip over <see cref="CopyFields"/>; byte-identical when clean.</summary>
    public static string SanitizeOutput(string output) => OnScreenCopySanitizer.StripJsonFields(output, CopyFields);

    protected override string PostProcessOutput(string output) => SanitizeOutput(output);
}
