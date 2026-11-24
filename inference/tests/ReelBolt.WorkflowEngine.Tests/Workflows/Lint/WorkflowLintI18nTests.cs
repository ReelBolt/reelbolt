using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>
/// The i18n contract of a finding: <c>MessageKey</c> names the sentence (<c>lint.&lt;MessageKey&gt;</c> in the web
/// catalogue) and <c>Args</c> carries tokens only — never a clause. The English <c>Message</c> is unchanged; the rule
/// tests still pin it.
/// </summary>
public class WorkflowLintI18nTests
{
    private const string KeepWhole = "\"version\":1,\"keepWholeSources\":true";

    /// <summary>Arg names whose value is user text (a label or a name) and may legitimately contain spaces.</summary>
    private static readonly HashSet<string> UserTextArgs = new(StringComparer.Ordinal)
    {
        // A user-authored label or name, or a product display name ("4K (2160p)"): text a customer reads, not a
        // clause the linter composed. Everything else must be one bare token.
        "label", "detail", "voice", "input", "resolution", "stepLabel", "targetLabel", "sourceLabel",
        "reviewLabel", "loopLabel", "lastLabel", "avatar"
    };

    private static WorkflowStep Labeled(WorkflowStep step, string label)
    {
        step.Label = label;
        return step;
    }

    private static Dictionary<string, Func<Task<IReadOnlyList<LintFinding>>>> BuildFixtures()
    {
        Dictionary<string, Func<Task<IReadOnlyList<LintFinding>>>> fixtures = new(StringComparer.Ordinal)
        {
            ["WL001.isNotSet"] = () => LintAsync([Voiceover(1, """{"source":{"kind":"ScriptScenes"}}""")]),
            ["WL001.notInWorkflow"] = () => LintAsync([Voiceover(1, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":9}}""")]),
            ["WL001.comesAfter"] = () => LintAsync([
                Voiceover(1, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":2}}"""),
                Agent(2, AgentType.ScriptwriterAgent)]),
            ["WL001.wrongKind"] = () => LintAsync([
                Agent(1, AgentType.DirectorAgent, "Treatment"),
                Labeled(Voiceover(2, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":1}}"""), "Draft narration")]),
            ["WL002.doesNotNameTheStep"] = () => LintAsync([Compile(1, $"{{{KeepWhole},\"enableVoiceover\":true}}")]),
            ["WL002.wrongKind"] = () => LintAsync([
                Agent(1, AgentType.AuthorAgent),
                Compile(2, $"{{{KeepWhole},\"enableVoiceover\":true,\"voiceoverStepOrder\":1}}")]),
            ["WL003.isNotSet"] = () => LintAsync([Agent(1, AgentType.AuthorAgent), ReviewLoopAt(2, null)]),
            ["WL003.comesAfter"] = () => LintAsync([Agent(1, AgentType.AuthorAgent), ReviewLoopAt(2, 2)]),
            ["WL003.noImprovement"] = () => LintAsync([Compile(1, $"{{{KeepWhole}}}"), ReviewLoopAt(2, 1)]),
            ["WL004.notInWorkflow"] = () => LintAsync([
                Compile(1, """{"version":1,"decision":{"from":"Step","stepOrder":9}}""")]),
            ["WL004.firstStep"] = () => LintAsync([Compile(1, """{"version":1,"decision":{"from":"Previous"}}""")]),
            ["WL004.previousWrongKind"] = () => LintAsync([
                Voiceover(1, """{"source":{"kind":"Inline"}}"""),
                Compile(2, """{"version":1,"decision":{"from":"Previous"}}""")]),
            ["WL005"] = () => LintAsync([
                Compile(1, $"{{{KeepWhole},\"mode\":\"StreamCopy\",\"enableMusic\":true,\"allowKeyframeSnapping\":true}}")]),
            ["WL006.noDefault"] = () => LintAsync(
                [Voiceover(1, """{"source":{"kind":"Inline"}}""")],
                facts: Missing(InferenceProviderCapability.SpeechSynthesis)),
            ["WL006.providerUnavailable"] = () => LintAsync(
                [Voiceover(1, $$"""{"source":{"kind":"Inline"},"providerId":"{{Guid.NewGuid()}}"}""")],
                facts: Missing(InferenceProviderCapability.SpeechSynthesis)),
            ["WL007.stepFeature"] = () => LintAsync(
                [Of(1, StepType.VideoGenerate, """{"planSource":"Inline"}""")],
                facts: UnentitledStep(StepType.VideoGenerate)),
            ["WL007.resolution"] = () => LintAsync(
                [Agent(1, AgentType.AuthorAgent)],
                new RunBrief(Resolution: "4K"),
                new FakeFacts { UnentitledAboveShortSide = 1080 }),
            ["WL008"] = () => LintAsync(
                [Voiceover(1, $$"""{"source":{"kind":"Inline"},"projectVoiceId":"{{ConsentMissingVoice}}"}""")],
                facts: ConsentMissing()),
            ["WL009"] = () => LintAsync(
                [Voiceover(1, """{"source":{"kind":"Inline"}}""")],
                new RunBrief(Language: "it", Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated, ProjectVoiceId: GermanVoice)),
                SpeakingGerman()),
            ["WL010.missingMedia"] = () => LintAsync(
                [Agent(1, AgentType.CodeStructureAnalyzer)],
                facts: new FakeFacts { FileCounts = Enum.GetValues<RunBriefFileRole>().ToDictionary(r => r, _ => 0) }),
            ["WL010.archiveOnly"] = () => LintAsync(
                [Agent(1, AgentType.CodeStructureAnalyzer)],
                facts: new FakeFacts
                {
                    FileCounts = Enum.GetValues<RunBriefFileRole>().ToDictionary(r => r, _ => 0),
                    ArchiveFiles = 2
                }),
            ["WL010.unpacking"] = () => LintAsync(
                [Agent(1, AgentType.CodeStructureAnalyzer)],
                facts: new FakeFacts
                {
                    FileCounts = Enum.GetValues<RunBriefFileRole>().ToDictionary(r => r, _ => 0),
                    ArchiveFiles = 1,
                    UnpackingArchives = 1
                }),
            ["WL010.missingFile"] = () => LintAsync(
                [Of(1, StepType.VideoAnalyze, $$$"""{"version":1,"source":{"kind":"ProjectFile","projectFileId":"{{{GoneFile}}}"}}""")],
                facts: MissingFile(GoneFile)),
            ["WL011"] = () => LintAsync(
                [Of(1, StepType.VideoAnalyze, """{"version":1}"""), Compile(2, $"{{{KeepWhole}}}")],
                new RunBrief(Language: "it")),
            ["WL012.step"] = () => LintAsync(
                [Of(1, StepType.DesignSystem, """{"op":"Require"}""")],
                facts: new FakeFacts { DesignApproved = false }),
            ["WL012.brief"] = () => LintAsync(
                [Agent(1, AgentType.AuthorAgent)],
                new RunBrief(DesignSystem: "project"),
                new FakeFacts { DesignApproved = false }),
            ["WL013"] = () => LintAsync([
                Agent(1, AgentType.AuthorAgent),
                Of(2, StepType.VideoGenerate, """{"planSource":"Inline"}"""),
                ReviewLoopAt(3, 1)]),
            ["WL014"] = () => LintAsync([
                Of(1, StepType.ThumbnailGenerate, """{"analysisStepOrder":3}"""),
                Agent(2, AgentType.AuthorAgent),
                Of(3, StepType.VideoAnalyze, """{"version":1}""")]),
            ["WL015.tracksAnObjectByName"] = () => LintAsync([
                Of(1, StepType.VideoAnalyze, """{"version":1,"trackObjects":[{"label":"plate","method":"Segmentation"}]}""")]),
            ["WL015.startsFromFrame"] = () => LintAsync([
                Of(1, StepType.VideoGenerate, """{"planSource":"Inline","firstFrame":{"from":"Step","stepOrder":1}}""")]),
            ["WL015.showsAnAvatar"] = () => LintAsync([
                Of(1, StepType.VideoGenerate, $$"""{"planSource":"Inline","avatarId":"{{Guid.NewGuid()}}"}""")]),
            ["WL027"] = () => LintAsync(
                [Of(1, StepType.VideoGenerate, $$"""{"planSource":"Inline","avatarId":"{{RevokedAvatar}}","allowSourceMediaEgress":true}""")],
                facts: Revoked()),
            ["WL034.useSibling"] = () => LintAsync(
                [Of(1, StepType.VideoGenerate, $$"""{"planSource":"Inline","avatarId":"{{Guid.NewGuid()}}","allowSourceMediaEgress":true,"model":"alibaba/wan-3.0/text-to-video"}""")],
                facts: new FakeFacts { VideoProviderKind = InferenceProviderKind.FalAi }),
            ["WL034.noSibling"] = () => LintAsync(
                [Of(1, StepType.VideoGenerate, $$"""{"planSource":"Inline","avatarId":"{{Guid.NewGuid()}}","allowSourceMediaEgress":true,"model":"kling-video/v9/text-to-video"}""")],
                facts: new FakeFacts { VideoProviderKind = InferenceProviderKind.Higgsfield }),
            ["WL016"] = () => LintAsync(
                [Agent(1, AgentType.ScriptwriterAgent)],
                new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated))),
            ["WL017"] = () => LintAsync(
                [Of(1, StepType.VideoAnalyze, """{"version":1}""")],
                new RunBrief(LengthSec: 120),
                new FakeFacts { SourceSeconds = 40 }),
            ["WL018.unreadField"] = () => LintAsync(
                [Agent(1, AgentType.AuthorAgent)],
                orphans: new Orphans(new AppManifestOrphan("WL018", 0, "Narrator",
                    "The input \"Narrator\" is not read by any step of this workflow, so it would be shown and ignored. Remove it or add a step that uses it."))),
            ["WL018.unusedRole"] = () => LintAsync(
                [Agent(1, AgentType.AuthorAgent)],
                orphans: new Orphans(new AppManifestOrphan("WL018", 1, "Your clips",
                    "The input \"Your clips\" takes video files, but no step of this workflow can use them. Remove it or add a step that takes them."))),
            ["WL019"] = () => LintAsync([Compile(1, $"{{{KeepWhole},\"enableCaptions\":true}}")]),
            ["WL020"] = () => LintAsync([
                Agent(6, AgentType.AuthorAgent),
                ReviewLoopAt(7, 6),
                Voiceover(8, """{"source":{"kind":"Inline","lines":[{"text":"hi","startSec":0}]}}"""),
                Compile(10, $"{{{KeepWhole}}}")]),
            ["WL021"] = () => LintAsync([
                Of(1, StepType.Revise),
                Voiceover(2, """{"source":{"kind":"Inline","lines":[{"text":"hi","startSec":0}]}}"""),
                Of(3, StepType.VideoAnalyze, """{"version":1,"source":{"kind":"StepOutput","stepOrder":1}}"""),
                Compile(4, $"{{{KeepWhole},\"enableVoiceover\":true,\"voiceoverStepOrder\":2,\"analysisStepOrder\":3}}")])
        };

        // Every catalog template is a fixture too: the graph rules fire on real shapes, not only on our minimal ones.
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
            fixtures["template:" + template.Key] = () => LintAsync(FromTemplate(template));

        return fixtures;
    }

    private static readonly Dictionary<string, Func<Task<IReadOnlyList<LintFinding>>>> Fixtures = BuildFixtures();

    private static readonly Guid ConsentMissingVoice = Guid.NewGuid();

    private static readonly Guid GermanVoice = Guid.NewGuid();

    private static readonly Guid GoneFile = Guid.NewGuid();

    private static readonly Guid RevokedAvatar = Guid.NewGuid();

    private static FakeFacts Revoked()
    {
        FakeFacts facts = new();
        facts.Avatars[RevokedAvatar] = new LintAvatarFact(true, false, "consent_revoked", "Anna presenter");
        return facts;
    }

    private static FakeFacts Missing(InferenceProviderCapability capability)
    {
        FakeFacts facts = new();
        facts.MissingProviders.Add(capability);
        return facts;
    }

    private static FakeFacts UnentitledStep(StepType type)
    {
        FakeFacts facts = new();
        facts.UnentitledSteps.Add(type);
        return facts;
    }

    private static FakeFacts ConsentMissing()
    {
        FakeFacts facts = new();
        facts.Voices[ConsentMissingVoice] = new LintVoiceFact(true, false, "consent_missing", "Anna", null);
        return facts;
    }

    private static FakeFacts SpeakingGerman()
    {
        FakeFacts facts = new();
        facts.Voices[GermanVoice] = new LintVoiceFact(true, true, null, "Anna", ["en", "de"]);
        return facts;
    }

    private static FakeFacts MissingFile(Guid id)
    {
        FakeFacts facts = new();
        facts.MissingFiles.Add(id);
        return facts;
    }

    public static IEnumerable<object[]> Scenarios() => Fixtures.Keys.Select(key => new object[] { key });

    public static IEnumerable<object[]> Rules() => Enumerable.Range(1, 21).Select(i => new object[] { $"WL{i:000}" });

    /// <summary>
    /// Every finding the fixtures produce names a sentence and carries tokens only: an arg value with a space is a
    /// clause that slipped back in, unless the arg is a declared user text (a label, a name, a validator detail).
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Every_finding_names_its_sentence_and_carries_only_tokens(string scenario)
    {
        IReadOnlyList<LintFinding> findings = await Fixtures[scenario]();

        foreach (LintFinding finding in findings)
        {
            finding.Message.Should().NotBeNullOrWhiteSpace();
            finding.MessageKey.Should().NotBeNullOrWhiteSpace();
            finding.MessageKey.Should().StartWith(finding.RuleId, $"{scenario}: the key must start with its rule id");
            finding.Args.Should().NotBeNull();
            AssertTokens(scenario, finding.RuleId, finding.Args);

            if (finding.Fix is { } fix)
            {
                fix.Label.Should().NotBeNullOrWhiteSpace();
                fix.LabelId.Should().NotBeNullOrWhiteSpace($"{scenario}: a rule-built fix names its label");
                AssertTokens(scenario, finding.RuleId, fix.Args ?? LintMessage.NoArgs);
            }
        }
    }

    private static void AssertTokens(string scenario, string rule, IReadOnlyDictionary<string, string> args)
    {
        foreach ((string name, string value) in args)
        {
            name.Should().MatchRegex("^[a-z][A-Za-z0-9]*$", $"{scenario}: arg names are stable lowerCamelCase");
            value.Should().NotBeNullOrWhiteSpace($"{scenario}: {rule} arg {name} has a value");
            if (!UserTextArgs.Contains(name))
                value.Should().NotContain(" ", $"{scenario}: {rule} arg {name} looks like a clause, not a token: \"{value}\"");
        }
    }

    /// <summary>Every rule of the table is exercised by the fixtures and emits a key of its own.</summary>
    [Theory]
    [MemberData(nameof(Rules))]
    public async Task Every_rule_emits_a_message_key(string rule)
    {
        List<string> keys = [];
        foreach (Func<Task<IReadOnlyList<LintFinding>>> fixture in Fixtures.Values)
            keys.AddRange((await fixture()).Where(f => f.RuleId == rule).Select(f => f.MessageKey));

        keys.Should().NotBeEmpty($"{rule} must be emitted by a fixture");
        keys.Should().OnlyContain(key => key == rule || key.StartsWith(rule + ".", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WL001_wrong_kind_carries_the_step_and_the_expected_kind_as_tokens()
    {
        LintFinding finding = (await Fixtures["WL001.wrongKind"]()).Rule("WL001").Single();

        finding.MessageKey.Should().Be("WL001.wrongKind");
        finding.Args.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["stepOrder"] = "2",
            ["stepLabel"] = "Draft narration",
            ["sourceOrder"] = "1",
            ["sourceLabel"] = "Treatment",
            ["problem"] = "wrongKind",
            ["expectedKind"] = "scriptwriter"
        });
    }

    [Fact]
    public async Task WL005_lists_each_feature_as_a_code()
    {
        LintFinding finding = (await Fixtures["WL005"]()).Rule("WL005").Single();

        finding.MessageKey.Should().Be("WL005");
        finding.Args.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["stepOrder"] = "1",
            ["featureCount"] = "1",
            ["feature1"] = "music"
        });
    }

    [Fact]
    public async Task WL020_carries_the_review_the_later_steps_and_the_last_step_as_tokens()
    {
        LintFinding finding = (await Fixtures["WL020"]()).Rule("WL020").Single();

        finding.MessageKey.Should().Be("WL020");
        finding.Args.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["reviewOrder"] = "7",
            ["laterCount"] = "2",
            ["later1Kind"] = "narration",
            ["later1Order"] = "8",
            ["later2Kind"] = "finalCut",
            ["later2Order"] = "10",
            ["lastOrder"] = "10"
        });
    }

    [Fact]
    public async Task WL006_carries_the_capability_and_the_problem_as_codes()
    {
        LintFinding finding = (await Fixtures["WL006.noDefault"]()).Rule("WL006").Single();

        finding.MessageKey.Should().Be("WL006");
        finding.Args.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["stepOrder"] = "1",
            ["capability"] = "speechSynthesis",
            ["problem"] = "noDefault"
        });
    }

    // ---------------- WL018, both variants through the real validator ----------------

    private static InferenceApiDbContext NewContext() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static readonly AppManifest BothOrphans = new(
        Inputs:
        [
            new AppManifestInput(AppManifestVocabulary.KindBrief, Field: BriefFieldConsumers.Voice, Label: "Narrator"),
            new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "video", Label: "Your clips")
        ]);

    [Fact]
    public async Task WL018_has_one_key_per_variant_with_the_input_and_role_as_tokens()
    {
        using InferenceApiDbContext db = NewContext();
        WorkflowLinter linter = new(new FixedFactsSource(new FakeFacts()), new AppManifestOrphanSource(db));

        IReadOnlyList<LintFinding> findings = (await linter.LintAsync(
            new WorkflowLintRequest(Guid.NewGuid(), Guid.NewGuid(), [Agent(1, AgentType.CodeStructureAnalyzer)], Manifest: BothOrphans),
            default)).Findings.Rule("WL018").ToList();

        findings.Should().HaveCount(2);
        LintFinding unread = findings.Single(f => f.MessageKey == "WL018.unreadField");
        unread.Args.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["input"] = "Narrator",
            ["inputIndex"] = "0"
        });
        LintFinding role = findings.Single(f => f.MessageKey == "WL018.unusedRole");
        role.Args.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["input"] = "Your clips",
            ["inputIndex"] = "1",
            ["role"] = "video"
        });
    }

    // ---------------- the wire ----------------

    private static LintFinding AllowingEgress() => new(
        "WL015",
        LintSeverity.Error,
        3,
        new LintMessage(
            "The step 3 would send your footage to an outside service, but the permission is off. It would be refused. It tracks an object by name.",
            LintArgs.Of(("stepOrder", "3"), ("stepLabel", "Track the plate"))),
        new LintFix(
            LintFixKind.AllowEgress,
            "Allow the footage to be sent for this step",
            LabelId: "WL015.allowEgress"));

    [Fact]
    public void The_wire_carries_messageKey_args_and_labelId_in_camelCase()
    {
        string json = JsonSerializer.Serialize(
            LintFindingResponse.From(AllowingEgress()), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        root.GetProperty("ruleId").GetString().Should().Be("WL015");
        root.GetProperty("messageKey").GetString().Should().Be("WL015");
        root.GetProperty("args").GetProperty("stepOrder").GetString().Should().Be("3");
        root.GetProperty("args").GetProperty("stepLabel").GetString().Should().Be("Track the plate");
        root.GetProperty("fix").GetProperty("labelId").GetString().Should().Be("WL015.allowEgress");
        root.GetProperty("fix").GetProperty("args").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void ForAssistant_still_drops_the_AllowEgress_fix_while_the_app_keeps_it()
    {
        LintFinding finding = AllowingEgress();

        LintFindingResponse.ForAssistant(finding).Fix.Should().BeNull();
        LintFindingResponse.ForAssistant(finding).MessageKey.Should().Be("WL015");
        LintFindingResponse.From(finding).Fix!.LabelId.Should().Be("WL015.allowEgress");
    }

    /// <summary>The orphan seam, as the real validator feeds it.</summary>
    private sealed class Orphans(params AppManifestOrphan[] found) : IAppManifestOrphanSource
    {
        public Task<IReadOnlyList<AppManifestOrphan>> FindOrphansAsync(WorkflowLintRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AppManifestOrphan>>(found);
    }
}
