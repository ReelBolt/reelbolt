using System.Text;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Briefs;

/// <summary>The brief fields an agent can read through the <c>[RUN BRIEF]</c> block.</summary>
public enum RunBriefField
{
    Language,
    SourceLanguage,
    Length,
    Voice
}

/// <summary>The validated facts a sentence is rendered from. Names come from a closed table, never from the raw brief string.</summary>
public sealed record RunBriefFacts(
    string? LanguageName,
    string? LanguageLabel,
    string? SourceLanguageName,
    string? SourceLanguageLabel,
    int? LengthSec,
    RunBriefVoiceMode? VoiceMode,
    bool LanguageIsDefault = false);

/// <summary>One reviewable row: which agents read which field, and the fixed sentence they get.</summary>
public sealed record RunBriefRow(RunBriefField Field, string Role, IReadOnlyList<AgentType> Agents, Func<RunBriefFacts, string?> Sentence);

/// <summary>
/// The one reviewable table of <c>(field, role) -> sentence</c> for the <c>[RUN BRIEF]</c> block, the
/// analogue of <see cref="Styles.StyleNudgeRegistry"/> (docs/run-brief.md, "What reads the brief").
/// Sentences stay inside each agent's output contract (no timestamps, no fields its schema lacks) and
/// none of them overrides the agent's own rules, output format, allowed ids or tools. An agent with no
/// row for any set field gets no block, so its prompt is left byte-identical.
/// </summary>
public static class RunBriefRegistry
{
    public const string BlockStart = "[RUN BRIEF]";
    public const string BlockEnd = "[END RUN BRIEF]";

    private const string DefaultsPreamble =
        "These are defaults for this run, taken from its style or its brand. Follow them unless your task says otherwise; " +
        "they never change your output format, your allowed ids or your tools.";

    private const string Preamble =
        "The person running this workflow set these requirements. They are settings, not suggestions: they outrank " +
        "the style, the brand's defaults and your own defaults, and they never change your output format, your allowed ids or your tools.";

    private static readonly AgentType[] Writers =
    [
        AgentType.ScriptwriterAgent, AgentType.NarrationWriter, AgentType.DirectorAgent,
        AgentType.AuthorAgent, AgentType.CompositionReviser
    ];

    // Agents that place viewer-facing text (titles, labels, captions) rather than write the script.
    private static readonly AgentType[] OnScreenText =
    [
        AgentType.VideoStoryEditor, AgentType.VideoEditDirector, AgentType.MotionGraphicsPlanner,
        AgentType.MotionGraphicsDirector, AgentType.GraphicDesigner
    ];

    private static readonly AgentType[] Reviewers = [AgentType.VideoReviewAgent, AgentType.ReviewAgent];

    private static readonly AgentType[] Translators = [AgentType.NarrationTranslator];

    private static readonly AgentType[] Cutters = [AgentType.ShotDirector, AgentType.VideoStoryEditor, AgentType.VideoEditDirector];

    private static readonly AgentType[] LengthWriters =
    [
        AgentType.ScriptwriterAgent, AgentType.NarrationWriter, AgentType.DirectorAgent, AgentType.AuthorAgent
    ];

    private static readonly AgentType[] VoiceWriters = [AgentType.ScriptwriterAgent, AgentType.NarrationWriter];

    private static string Lang(RunBriefFacts f) => f.LanguageIsDefault ? "Default language" : "Language";

    /// <summary>The built-in agents seated in a room step, whose registry rows decide what the room's key reads.</summary>
    public static IReadOnlyList<AgentType> RoomAgents(StepType stepType) => stepType switch
    {
        StepType.EditRoom => [AgentType.VideoStoryEditor, AgentType.VideoEditDirector],
        StepType.GraphicsRoom => [AgentType.MotionGraphicsPlanner, AgentType.MotionGraphicsDirector],
        StepType.ColorGradeRoom => [AgentType.Colorist, AgentType.ColorGradeDirector],
        StepType.DailiesRoom => [AgentType.DailiesDirector, AgentType.TakeSelector],
        _ => []
    };

    /// <summary>Every row, in the order the sentences appear inside a block.</summary>
    public static IReadOnlyList<RunBriefRow> Rows { get; } =
    [
        new(RunBriefField.Language, "Writer", Writers, f => f.LanguageLabel is null ? null :
            $"{Lang(f)}: {f.LanguageLabel}. Write every word the viewer reads or hears in {f.LanguageName}: narration, on-screen text, captions. " +
            "Keep brand names and product terms in their original form."),
        new(RunBriefField.Language, "OnScreenText", OnScreenText, f => f.LanguageLabel is null ? null :
            $"{Lang(f)}: {f.LanguageLabel}. Any text you place on screen (titles, labels, captions) is in {f.LanguageName}. " +
            "Keep brand names and product terms in their original form."),
        new(RunBriefField.Language, "Translator", Translators, f => f.LanguageLabel is null ? null :
            $"Target language: {f.LanguageLabel}. Translate the narration into {f.LanguageName}. " +
            "Keep brand names and product terms in their original form."),
        new(RunBriefField.SourceLanguage, "Translator", Translators, f => f.SourceLanguageLabel is null ? null :
            $"Source language: {f.SourceLanguageLabel}. The narration you receive was written in {f.SourceLanguageName}."),
        new(RunBriefField.Language, "Reviewer", Reviewers, f => f.LanguageLabel is null ? null :
            $"{Lang(f)}: the film must be in {f.LanguageLabel}. Report narration, on-screen text or captions in another language as an issue " +
            "(brand names and product terms excepted)."),
        new(RunBriefField.Length, "Writer", LengthWriters, f => f.LengthSec is not { } n ? null :
            $"Length: about {n} seconds. Plan scene count and word budget for it, at a comfortable pace of about 1.8 words per second of scene time."),
        new(RunBriefField.Length, "Cutter", Cutters, f => f.LengthSec is not { } n ? null :
            $"Length: about {n} seconds. Plan the cut to land near it; it is a target, not a guarantee."),
        new(RunBriefField.Length, "Reviewer", Reviewers, f => f.LengthSec is not { } n ? null :
            $"Length: the target is about {n} seconds. Report a film more than 15% longer or shorter than that as an issue. The platform also measures the file itself and caps the score."),
        new(RunBriefField.Voice, "Writer", VoiceWriters, f => f.VoiceMode switch
        {
            RunBriefVoiceMode.Narrated => "Voice: narrated. Write for speaking aloud.",
            RunBriefVoiceMode.None => "Voice: none. The film is not narrated, so write short on-screen text instead of spoken lines.",
            _ => null
        })
    ];

    /// <summary>The fields <paramref name="agentType"/> reads (empty for an agent the brief does not touch).</summary>
    public static IReadOnlyList<RunBriefField> FieldsRead(AgentType agentType) =>
        Rows.Where(r => r.Agents.Contains(agentType)).Select(r => r.Field).Distinct().ToList();

    /// <summary>The rendered block for <paramref name="agentType"/>, or null when no row has a sentence for the set facts.</summary>
    public static string? BuildBlock(AgentType agentType, RunBriefFacts facts)
    {
        var lines = new List<string>();
        bool onlyDefaults = true;
        foreach (RunBriefRow row in Rows)
        {
            if (!row.Agents.Contains(agentType))
                continue;
            if (row.Sentence(facts) is { } sentence)
            {
                lines.Add(sentence);
                onlyDefaults &= row.Field == RunBriefField.Language && facts.LanguageIsDefault;
            }
        }

        if (lines.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append(BlockStart).Append('\n').Append(onlyDefaults ? DefaultsPreamble : Preamble).Append('\n');
        foreach (string line in lines)
            sb.Append("- ").Append(line).Append('\n');
        sb.Append(BlockEnd);
        return sb.ToString();
    }

    /// <summary>
    /// The agent-side consumption matrix as a markdown table, generated from <see cref="Rows"/>.
    /// <c>docs/run-brief.md</c> carries a copy between two markers and a test pins the two together.
    /// </summary>
    public static string MatrixMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("| Agent | language | sourceLanguage | lengthSec | voice |\n");
        sb.Append("|---|---|---|---|---|\n");
        foreach (AgentType agent in Rows.SelectMany(r => r.Agents).Distinct().OrderBy(a => a.ToString(), StringComparer.Ordinal))
        {
            sb.Append("| `").Append(agent).Append("` |");
            foreach (RunBriefField field in Enum.GetValues<RunBriefField>())
            {
                string roles = string.Join(", ", Rows.Where(r => r.Field == field && r.Agents.Contains(agent)).Select(r => r.Role));
                sb.Append(' ').Append(roles.Length == 0 ? "-" : roles).Append(" |");
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
