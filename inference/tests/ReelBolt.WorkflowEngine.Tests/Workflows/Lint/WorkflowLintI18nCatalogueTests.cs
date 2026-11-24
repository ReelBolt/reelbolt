using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>
/// Drift guard between the linter and the English catalogue (web/messages/en/apps.json): every message key a rule
/// can emit has a <c>lint.*</c> sentence with exactly the ICU arguments the client passes it, and every
/// <c>labelId</c> a fix carries has a <c>lint.fix.*</c> entry. The table is derived from the emission sites
/// (<see cref="ReelBolt.Shared.Workflows.Lint.StepGraphLinter"/> and the Inference API's WorkflowLinter), not from
/// the catalogue; <see cref="WorkflowLintI18nTests"/> pins the wire keys and their token args.
/// </summary>
/// <remarks>
/// It deliberately does not assert the English wording (the sentence belongs to the frontend: pinning it here would
/// make every copy edit a two-repo change) and does not assert the absence of extra catalogue entries (a rule not
/// merged yet, WL022, declares its copy ahead of the rule). WL000 is absent by design: it passes a save validator's
/// own sentence through and always reads in English.
/// </remarks>
public class WorkflowLintI18nCatalogueTests
{
    private const string CatalogueRelativePath = "web/messages/en/apps.json";

    /// <summary>A message key the linter can emit, the catalogue entry under <c>lint</c> and its ICU arguments.</summary>
    /// <param name="Key">The wire <c>messageKey</c> (for example <c>WL001.comesLater</c>).</param>
    /// <param name="Entry">The dotted path under <c>lint</c> (the wire key, except WL006: <c>WL006.message</c>).</param>
    /// <param name="IcuArguments">The argument names the catalogue sentence declares.</param>
    /// <param name="WireArguments">The <c>args</c> tokens the linter sends for that key (documentation; a <c>?</c>
    /// marks a token that is absent when the fact is unknown). They are what the client turns into the phrases the
    /// ICU arguments name (<c>stepOrder</c>/<c>stepLabel</c> → <c>actor</c>, <c>sourceOrder</c> → <c>from</c>, …).</param>
    private sealed record MessageKey(string Key, string Entry, string[] IcuArguments, string[] WireArguments);

    /// <summary>A <c>labelId</c> a rule-built fix carries, its catalogue entry and its ICU arguments.</summary>
    private sealed record FixLabel(string LabelId, string[] IcuArguments, string[] WireArguments);

    /// <summary>
    /// Every key the linter can emit: each rule id, plus each variant a branch produces. WL001, WL002 and WL004
    /// append the <c>Problem</c> code (WL003 accepts any earlier step, so it has no <c>wrongKind</c>); WL003 also has
    /// <c>noImprovement</c>; the rest use their rule id alone or one of their named variants.
    /// </summary>
    private static readonly MessageKey[] MessageKeys =
    [
        new("WL001.isNotSet", "WL001.isNotSet", ["actor", "from", "problem"], ["stepOrder", "stepLabel?", "problem"]),
        new("WL001.notInWorkflow", "WL001.notInWorkflow", ["actor", "from", "problem"], ["stepOrder", "stepLabel?", "sourceOrder", "problem"]),
        new("WL001.comesAfter", "WL001.comesAfter", ["actor", "from", "problem"], ["stepOrder", "stepLabel?", "sourceOrder", "sourceLabel?", "problem"]),
        new("WL001.wrongKind", "WL001.wrongKind", ["actor", "from", "problem"], ["stepOrder", "stepLabel?", "sourceOrder", "sourceLabel?", "problem", "expectedKind"]),
        new("WL002.doesNotNameTheStep", "WL002.doesNotNameTheStep", ["actor", "problem"], ["stepOrder", "stepLabel?", "voiceKind", "problem"]),
        new("WL002.notInWorkflow", "WL002.notInWorkflow", ["actor", "problem"], ["stepOrder", "stepLabel?", "voiceKind", "problem", "targetOrder"]),
        new("WL002.comesAfter", "WL002.comesAfter", ["actor", "problem"], ["stepOrder", "stepLabel?", "voiceKind", "problem", "targetOrder", "targetLabel?"]),
        new("WL002.wrongKind", "WL002.wrongKind", ["actor", "problem"], ["stepOrder", "stepLabel?", "voiceKind", "problem", "targetOrder", "targetLabel?", "expectedKind"]),
        new("WL003.isNotSet", "WL003.isNotSet", ["at", "to", "problem"], ["stepOrder", "stepLabel?", "problem"]),
        new("WL003.notInWorkflow", "WL003.notInWorkflow", ["at", "to", "problem"], ["stepOrder", "stepLabel?", "problem", "targetOrder"]),
        new("WL003.comesAfter", "WL003.comesAfter", ["at", "to", "problem"], ["stepOrder", "stepLabel?", "problem", "targetOrder", "targetLabel?"]),
        new("WL003.noImprovement", "WL003.noImprovement", ["at", "over"], ["stepOrder", "stepLabel?", "targetOrder"]),
        new("WL004.isNotSet", "WL004.isNotSet", ["actor", "input", "from", "problem"], ["stepOrder", "stepLabel?", "input", "problem"]),
        new("WL004.notInWorkflow", "WL004.notInWorkflow", ["actor", "input", "from", "problem"], ["stepOrder", "stepLabel?", "input", "problem", "targetOrder"]),
        new("WL004.comesAfter", "WL004.comesAfter", ["actor", "input", "from", "problem"], ["stepOrder", "stepLabel?", "input", "problem", "targetOrder", "targetLabel?"]),
        new("WL004.wrongKind", "WL004.wrongKind", ["actor", "input", "from", "problem"], ["stepOrder", "stepLabel?", "input", "problem", "targetOrder", "targetLabel?", "expectedKind"]),
        new("WL004.firstStep", "WL004.firstStep", ["actor", "input", "from", "problem"], ["stepOrder", "stepLabel?", "input", "problem"]),
        new("WL004.previousWrongKind", "WL004.previousWrongKind", ["actor", "input", "from", "problem"], ["stepOrder", "stepLabel?", "input", "problem", "targetOrder", "targetLabel?", "expectedKind"]),
        new("WL005", "WL005", ["actor", "features"], ["stepOrder", "stepLabel?", "featureCount", "feature1…featureN"]),
        new("WL006", "WL006.message", ["actor", "capability", "problem"], ["stepOrder", "stepLabel?", "capability", "problem"]),
        new("WL006.providerOwnership", "WL006.providerOwnership", ["detail"], ["stepOrder?", "stepLabel?", "detail"]),
        new("WL007.stepFeature", "WL007.stepFeature", ["feature", "step"], ["feature", "stepOrder", "stepLabel?"]),
        new("WL007.resolution", "WL007.resolution", ["resolution"], ["resolution"]),
        new("WL008", "WL008", ["voice", "reason"], ["voice", "reason"]),
        new("WL009", "WL009", ["language", "languages"], ["language", "languageCount", "language1…languageN"]),
        new("WL010.missingMedia", "WL010.missingMedia", ["actor", "role"], ["stepOrder", "stepLabel?", "role"]),
        new("WL010.archiveOnly", "WL010.archiveOnly", ["actor"], ["stepOrder", "stepLabel?"]),
        new("WL010.unpacking", "WL010.unpacking", ["actor"], ["stepOrder", "stepLabel?"]),
        new("WL010.missingFile", "WL010.missingFile", ["stepOrder", "slot"], ["stepOrder", "stepLabel?", "slot"]),
        new("WL011", "WL011", ["field"], ["field"]),
        new("WL012.step", "WL012.step", ["step"], ["stepOrder", "stepLabel?"]),
        new("WL012.brief", "WL012.brief", [], []),
        new("WL013", "WL013", ["actor", "loop"], ["stepOrder", "stepLabel?", "loopOrder"]),
        new("WL014", "WL014", ["actor", "target"], ["stepOrder", "stepLabel?", "targetOrder"]),
        new("WL015.tracksAnObjectByName", "WL015.tracksAnObjectByName", ["actor"], ["stepOrder", "stepLabel?", "inputId"]),
        new("WL015.startsFromFrame", "WL015.startsFromFrame", ["actor"], ["stepOrder", "stepLabel?", "inputId"]),
        new("WL015.showsAnAvatar", "WL015.showsAnAvatar", ["actor"], ["stepOrder", "stepLabel?", "inputId"]),
        new("WL027", "WL027", ["actor", "avatar", "reason"], ["stepOrder", "stepLabel?", "avatar", "reason"]),
        new("WL016", "WL016", [], []),
        new("WL017", "WL017", ["length", "available"], ["length", "available"]),
        new("WL018.unreadField", "WL018.unreadField", ["input"], ["input", "inputIndex"]),
        new("WL018.unusedRole", "WL018.unusedRole", ["input", "role"], ["input", "inputIndex", "role"]),
        new("WL019", "WL019", ["actor"], ["stepOrder", "stepLabel?"]),
        new("WL020", "WL020", ["reviewOrder", "later", "lastOrder"], ["reviewOrder", "reviewLabel?", "lastOrder", "laterCount", "later1Kind", "later1Order", "…laterNKind", "laterNOrder"]),
        new("WL021", "WL021", ["actor", "over"], ["stepOrder", "stepLabel?", "sourceOrder", "sourceLabel?"]),
        new("WL024", "WL024", ["actor", "start", "end"], ["stepOrder", "stepLabel?", "start", "end"]),
        new("WL025", "WL025", ["actor", "seconds"], ["stepOrder", "stepLabel?", "seconds"]),
        new("WL028", "WL028", ["input"], ["input", "inputId?"]),
        new("WL029", "WL029", ["input"], ["input", "inputId?"]),
        new("WL030", "WL030.message", ["input"], ["input", "inputId?"]),
        new("WL030.recommended", "WL030.recommended", ["input"], ["input", "inputId?"]),
        new("WL031", "WL031", ["input", "value", "count"], ["input", "inputId", "value", "count", "skippedCount", "skipped1…skippedN"]),
        new("WL032", "WL032", ["input", "count"], ["input", "inputId", "count"]),
        new("WL033", "WL033", ["input", "actor", "credits", "cap"], ["input", "stepOrder", "stepLabel?", "credits", "cap"]),
        new("WL034.useSibling", "WL034.useSibling", ["actor", "model", "sibling"], ["stepOrder", "stepLabel?", "model", "sibling"]),
        new("WL034.noSibling", "WL034.noSibling", ["actor", "model"], ["stepOrder", "stepLabel?", "model"])
    ];

    /// <summary>Every <c>labelId</c> a rule-built fix carries (the fix row of the finding shape).</summary>
    private static readonly FixLabel[] FixLabels =
    [
        new("WL001.retargetSource", ["expected"], ["expectedKind"]),
        new("WL002.retargetVoiceover", [], []),
        new("WL003.retargetTarget", [], []),
        new("WL003.retargetModelStep", [], []),
        new("WL004.retargetInput", ["input"], ["input"]),
        new("WL005.reencode", [], []),
        new("WL006.chooseProvider", [], []),
        new("WL006.chooseAvailableProvider", [], []),
        new("WL007.changePlan", [], []),
        new("WL007.changeResolution", [], []),
        new("WL008.chooseVoice", [], []),
        new("WL009.chooseVoice", [], []),
        new("WL010.upload", ["role"], ["role"]),
        new("WL010.pickFile", [], []),
        new("WL012.approveDesign", [], []),
        new("WL013.startLoopAfter", [], []),
        new("WL014.earlierStep", [], []),
        new("WL015.allowEgress", [], []),
        new("WL015.allowOnAppPage", [], []),
        new("WL016.addVoiceover", [], []),
        new("WL017.shorterFilm", [], []),
        new("WL018.removeReference", [], []),
        new("WL019.voiceOrCaptions", [], []),
        new("WL020.moveReview", [], []),
        new("WL021.muteSourceAudio", [], []),
        new("WL024.changeSpan", [], []),
        new("WL025.shortenCard", [], []),
        new("WL027.chooseAvatar", [], []),
        new("WL028.fillInput", ["input"], ["input"]),
        new("WL029.changeInput", ["input"], ["input"]),
        new("WL030.chooseModel", [], []),
        new("WL033.narrowRange", [], []),
        new("WL034.chooseModel", [], [])
    ];

    [Fact]
    public void Every_message_key_the_linter_can_emit_has_an_entry_in_the_English_catalogue()
    {
        JsonElement lint = LintBlock();

        List<string> missing = [.. MessageKeys
            .Where(key => !TryResolve(lint, key.Entry, out _))
            .Select(key => $"message key \"{key.Key}\": the linter can emit it, but {CatalogueRelativePath} has no "
                + $"lint.{key.Entry} entry (args: {Describe(key.WireArguments)}).")];

        Assert.True(missing.Count == 0,
            "The English catalogue is missing copy the linter can emit:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_English_entry_declares_exactly_the_arguments_the_linter_passes()
    {
        JsonElement lint = LintBlock();
        List<string> problems = [];

        foreach (MessageKey key in MessageKeys)
        {
            // A missing or malformed entry is the other test's finding; do not report it twice.
            if (!TryResolve(lint, key.Entry, out JsonElement entry) || entry.ValueKind != JsonValueKind.String)
                continue;

            SortedSet<string> actual = IcuArguments(entry.GetString()!);
            SortedSet<string> expected = new(key.IcuArguments, StringComparer.Ordinal);
            if (!actual.SetEquals(expected))
            {
                problems.Add($"lint.{key.Entry} (message key \"{key.Key}\"): the linter passes "
                    + $"{Describe(key.WireArguments)}, so the sentence must declare the ICU arguments "
                    + $"{Describe(key.IcuArguments)}; it declares {Describe([.. actual])}.");
            }
        }

        Assert.True(problems.Count == 0,
            "The English catalogue and the linter disagree on their arguments:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void Every_label_id_the_linter_emits_has_an_entry_in_the_English_catalogue()
    {
        JsonElement lint = LintBlock();
        List<string> problems = [];

        foreach (FixLabel fix in FixLabels)
        {
            if (!TryResolve(lint, "fix." + fix.LabelId, out JsonElement entry))
            {
                problems.Add($"fix labelId \"{fix.LabelId}\": the linter can emit it, but {CatalogueRelativePath} has no "
                    + $"lint.fix.{fix.LabelId} entry (args: {Describe(fix.WireArguments)}).");
                continue;
            }

            if (entry.ValueKind != JsonValueKind.String)
            {
                problems.Add($"lint.fix.{fix.LabelId} is not a sentence (it is {entry.ValueKind}).");
                continue;
            }

            SortedSet<string> actual = IcuArguments(entry.GetString()!);
            SortedSet<string> expected = new(fix.IcuArguments, StringComparer.Ordinal);
            if (!actual.SetEquals(expected))
            {
                problems.Add($"lint.fix.{fix.LabelId}: the fix passes {Describe(fix.WireArguments)}, so the button "
                    + $"must declare the ICU arguments {Describe(fix.IcuArguments)}; it declares {Describe([.. actual])}.");
            }
        }

        Assert.True(problems.Count == 0,
            "The English catalogue is missing fix copy the linter can emit:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// The ICU argument names of a sentence: <c>{name}</c> and <c>{name, type, …}</c>, with the one level of nested
    /// braces a plural/select branch uses (<c>{count, plural, one {# step} other {# steps}}</c> declares only
    /// <c>count</c>; <c>{gender, select, male {{name} goes} other {they}}</c> declares <c>gender</c> and <c>name</c>).
    /// No MessageFormat library is needed for that much.
    /// </summary>
    private static SortedSet<string> IcuArguments(string sentence)
    {
        SortedSet<string> names = new(StringComparer.Ordinal);
        CollectArguments(sentence, names);
        return names;
    }

    private static void CollectArguments(string text, ISet<string> names)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{')
            {
                i++;
                continue;
            }

            i = MatchingBrace(text, i, out string inner) + 1;
            ParseArgument(inner, names);
        }
    }

    /// <summary>The index of the <c>}</c> that closes the <c>{</c> at <paramref name="open"/>, and its content.</summary>
    private static int MatchingBrace(string text, int open, out string inner)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && --depth == 0)
            {
                inner = text[(open + 1)..i];
                return i;
            }
        }

        throw new InvalidOperationException($"Unbalanced ICU braces in \"{text}\".");
    }

    private static void ParseArgument(string inner, ISet<string> names)
    {
        string[] parts = SplitTopLevel(inner, ',');
        string head = parts[0].Trim();

        // A branch body ("{name} goes") opens with a brace: it is not an argument, so read the braces inside it.
        if (!IsArgumentName(head))
        {
            CollectArguments(inner, names);
            return;
        }

        names.Add(head);
        if (parts.Length >= 2 && parts[1].Trim() is "plural" or "select" or "selectordinal")
            ParseBranches(string.Join(",", parts[2..]), names);
    }

    /// <summary>Reads <c>&lt;selector&gt; {body} &lt;selector&gt; {body}</c>; the bodies may carry their own arguments.</summary>
    private static void ParseBranches(string text, ISet<string> names)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            while (i < text.Length && text[i] != '{' && !char.IsWhiteSpace(text[i]))
                i++;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            if (i >= text.Length)
                return;

            if (text[i] != '{')
            {
                // Not the branch shape we know: read whatever braces follow rather than losing an argument.
                CollectArguments(text[i..], names);
                return;
            }

            i = MatchingBrace(text, i, out string body) + 1;
            CollectArguments(body, names);
        }
    }

    private static string[] SplitTopLevel(string text, char separator)
    {
        List<string> parts = [];
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
                depth--;
            else if (text[i] == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return [.. parts];
    }

    private static bool IsArgumentName(string token) =>
        token.Length > 0 && char.IsLetter(token[0]) && token.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string Describe(IReadOnlyCollection<string> names) =>
        names.Count == 0 ? "(none)" : string.Join(", ", names);

    private static JsonElement LintBlock()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(CataloguePath()));
        return document.RootElement.GetProperty("lint").Clone();
    }

    private static bool TryResolve(JsonElement lint, string path, out JsonElement entry)
    {
        entry = lint;
        foreach (string part in path.Split('.'))
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(part, out JsonElement child))
            {
                entry = default;
                return false;
            }

            entry = child;
        }

        return true;
    }

    /// <summary>
    /// The English catalogue: the repository's own file, or a copy named by <c>WORKFLOW_LINT_CATALOGUE</c> (the way the
    /// guard is shown to fail without editing the file a translation run is writing).
    /// </summary>
    private static string CataloguePath() =>
        Environment.GetEnvironmentVariable("WORKFLOW_LINT_CATALOGUE") is { Length: > 0 } fixture
            ? fixture
            : Path.Combine(RepoRoot(), CatalogueRelativePath);

    /// <summary>The repository root, found the way <c>WorkflowLintEvalGoalsTests</c> and <c>AssistantKbDocsTests</c> find theirs.</summary>
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, CatalogueRelativePath)))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException($"{CatalogueRelativePath} not found above the test binaries");
    }
}
