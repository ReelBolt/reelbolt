using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>
/// One golden failing workflow per rule WL001..WL020 and its passing counterpart (docs/workflow-linter.md,
/// docs/design/workflow-apps.md section 6.3).
/// </summary>
public class WorkflowLinterRuleTests
{
    private const string KeepWhole = "\"version\":1,\"keepWholeSources\":true";

    private static void Fires(IReadOnlyList<LintFinding> findings, string rule, int? step = null)
    {
        IEnumerable<LintFinding> hits = findings.Rule(rule);
        hits.Should().NotBeEmpty($"{rule} should fire");
        hits.Should().OnlyContain(f => f.Severity == StepCompatibilityRules.Get(rule).Severity || rule == "WL012");
        if (step is { } order)
            hits.Should().Contain(f => f.StepOrder == order);
        hits.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Message));
    }

    private static void Quiet(IReadOnlyList<LintFinding> findings, string rule) =>
        findings.Rule(rule).Should().BeEmpty($"{rule} should not fire on the passing counterpart");

    // ---------------- table ----------------

    [Fact]
    public void The_rule_table_has_every_rule_once_with_the_spec_severities()
    {
        StepCompatibilityRules.All.Select(r => r.Id).Should().Equal(Enumerable.Range(1, 34).Select(i => $"WL{i:000}"));
        Dictionary<string, LintSeverity> expected = new()
        {
            ["WL001"] = LintSeverity.Error,
            ["WL002"] = LintSeverity.Error,
            ["WL003"] = LintSeverity.Error,
            ["WL004"] = LintSeverity.Error,
            ["WL005"] = LintSeverity.Error,
            ["WL006"] = LintSeverity.Error,
            ["WL007"] = LintSeverity.Error,
            ["WL008"] = LintSeverity.Error,
            ["WL009"] = LintSeverity.Warning,
            ["WL010"] = LintSeverity.Error,
            ["WL011"] = LintSeverity.Info,
            ["WL012"] = LintSeverity.Error,
            ["WL013"] = LintSeverity.Warning,
            ["WL014"] = LintSeverity.Error,
            ["WL015"] = LintSeverity.Error,
            ["WL016"] = LintSeverity.Warning,
            ["WL017"] = LintSeverity.Warning,
            ["WL018"] = LintSeverity.Error,
            ["WL019"] = LintSeverity.Error,
            ["WL020"] = LintSeverity.Warning,
            ["WL021"] = LintSeverity.Error,
            ["WL022"] = LintSeverity.Warning,
            ["WL023"] = LintSeverity.Error,
            ["WL024"] = LintSeverity.Error,
            ["WL025"] = LintSeverity.Warning,
            ["WL026"] = LintSeverity.Warning,
            ["WL027"] = LintSeverity.Error,
            ["WL028"] = LintSeverity.Error,
            ["WL029"] = LintSeverity.Error,
            ["WL030"] = LintSeverity.Error,
            ["WL031"] = LintSeverity.Info,
            ["WL032"] = LintSeverity.Info,
            ["WL033"] = LintSeverity.Warning,
            ["WL034"] = LintSeverity.Error
        };
        foreach (StepCompatibilityRule rule in StepCompatibilityRules.All)
        {
            rule.Severity.Should().Be(expected[rule.Id], rule.Id);
            rule.Name.Should().NotBeNullOrWhiteSpace();
            rule.Description.Should().NotBeNullOrWhiteSpace();
            rule.MessageTemplate.Should().NotBeNullOrWhiteSpace();
            rule.Needs.Should().NotBeNull();
        }
    }

    // ---------------- WL001 ----------------

    [Fact]
    public async Task WL001_a_voiceover_source_that_is_missing_later_or_the_wrong_kind_fires()
    {
        Fires(await LintAsync([Agent(1, AgentType.ScriptwriterAgent), Voiceover(2, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":5}}""")]), "WL001", 2);
        Fires(await LintAsync([Voiceover(1, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":2}}"""), Agent(2, AgentType.ScriptwriterAgent)]), "WL001", 1);
        Fires(await LintAsync([Agent(1, AgentType.DirectorAgent), Voiceover(2, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":1}}""")]), "WL001", 2);
        Fires(await LintAsync([Agent(1, AgentType.ScriptwriterAgent), Voiceover(2, """{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":1}}""")]), "WL001", 2);
        Fires(await LintAsync([Agent(1, AgentType.NarrationWriter), Voiceover(2, """{"source":{"kind":"PickupPlan"}}""")]), "WL001", 2);
    }

    [Fact]
    public async Task WL001_passes_for_each_source_kind_pointing_at_the_right_earlier_step()
    {
        Quiet(await LintAsync([Agent(1, AgentType.ScriptwriterAgent), Voiceover(2, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":1}}""")]), "WL001");
        Quiet(await LintAsync([Agent(1, AgentType.NarrationWriter), Voiceover(2, """{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":1}}""")]), "WL001");
        Quiet(await LintAsync([Agent(1, AgentType.NarrationTranslator), Voiceover(2, """{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":1}}""")]), "WL001");
        Quiet(await LintAsync([Agent(1, AgentType.PickupPlanner), Voiceover(2, """{"source":{"kind":"PickupPlan","pickupPlannerStepOrder":1}}""")]), "WL001");
        Quiet(await LintAsync([Voiceover(1, """{"source":{"kind":"Inline","lines":[{"text":"hi","startSec":0}]}}""")]), "WL001");
    }

    // ---------------- WL002 ----------------

    [Fact]
    public async Task WL002_a_compile_that_mixes_in_a_voiceover_nobody_made_fires()
    {
        Fires(await LintAsync([Agent(1, AgentType.AuthorAgent), Compile(2, $"{{{KeepWhole},\"enableVoiceover\":true,\"voiceoverStepOrder\":1}}")]), "WL002", 2);
        Fires(await LintAsync([Compile(1, $"{{{KeepWhole},\"enableVoiceover\":true}}")]), "WL002", 1);
        Fires(await LintAsync([Agent(1, AgentType.AuthorAgent), Compile(2, $"{{{KeepWhole},\"enablePickups\":true,\"pickupVoiceoverStepOrder\":4}}")]), "WL002", 2);
    }

    [Fact]
    public async Task WL002_passes_when_the_named_voiceover_step_is_earlier()
    {
        Quiet(await LintAsync([
            Voiceover(1, """{"source":{"kind":"Inline","lines":[{"text":"hi","startSec":0}]}}"""),
            Compile(2, $"{{{KeepWhole},\"enableVoiceover\":true,\"voiceoverStepOrder\":1}}")]), "WL002");
        Quiet(await LintAsync([Compile(1, $"{{{KeepWhole}}}")]), "WL002");
    }

    // ---------------- WL003 ----------------

    [Fact]
    public async Task WL003_a_review_loop_with_no_forward_or_deterministic_target_fires()
    {
        Fires(await LintAsync([Agent(1, AgentType.AuthorAgent), ReviewLoopAt(2, null)]), "WL003", 2);
        Fires(await LintAsync([Agent(1, AgentType.AuthorAgent), ReviewLoopAt(2, 9)]), "WL003", 2);
        Fires(await LintAsync([Agent(1, AgentType.AuthorAgent), ReviewLoopAt(2, 2)]), "WL003", 2);
        // A span of only fixed steps still runs, so it is a warning rather than an error.
        (await LintAsync([Compile(1, $"{{{KeepWhole}}}"), ReviewLoopAt(2, 1)])).Rule("WL003").Should().ContainSingle().Which.Severity.Should().Be(LintSeverity.Warning);
    }

    [Fact]
    public async Task WL003_passes_when_the_target_is_an_earlier_model_step()
    {
        Quiet(await LintAsync([Agent(1, AgentType.AuthorAgent), ReviewLoopAt(2, 1)]), "WL003");
    }

    // ---------------- WL004 ----------------

    [Fact]
    public async Task WL004_compile_inputs_that_are_missing_forward_or_of_the_wrong_kind_fire()
    {
        Fires(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1}"""),
            Compile(2, """{"version":1,"decision":{"from":"Step","stepOrder":9},"analysisStepOrder":1}""")]), "WL004", 2);
        Fires(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1}"""), Voiceover(2, """{"source":{"kind":"Inline"}}"""),
            Compile(3, """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1}""")]), "WL004", 3);
        Fires(await LintAsync([Agent(1, AgentType.VideoStoryEditor), Compile(2, """{"version":1,"decision":{"from":"Step","stepOrder":1},"analysisStepOrder":1}""")]), "WL004", 2);
        Fires(await LintAsync([Compile(1, """{"version":1,"decision":{"from":"Step","stepOrder":3},"analysisStepOrder":2}"""), Of(2, StepType.VideoAnalyze, """{"version":1}"""), Agent(3, AgentType.VideoStoryEditor)]), "WL004", 1);
    }

    [Fact]
    public async Task WL004_passes_for_a_real_earlier_analysis_and_decision_and_for_keep_whole_sources()
    {
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1}"""), Agent(2, AgentType.VideoStoryEditor),
            Compile(3, """{"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1}""")]), "WL004");
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1}"""),
            Compile(2, """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"keepWholeSources":true}""")]), "WL004");
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1}"""), Agent(2, AgentType.VideoStoryEditor),
            Compile(3, """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1}""")]), "WL004");
    }

    // ---------------- WL005 ----------------

    [Fact]
    public async Task WL005_stream_copy_with_anything_that_needs_a_re_encode_fires()
    {
        foreach (string feature in new[] { "enableMusic", "enableVoiceover", "enableGraphics", "enableSfx", "enableColorGrade", "enableCaptions" })
        {
            Fires(await LintAsync([Compile(1, $"{{{KeepWhole},\"mode\":\"StreamCopy\",\"{feature}\":true}}")]), "WL005", 1);
        }

        Fires(await LintAsync([Compile(1, $"{{{KeepWhole},\"mode\":1,\"regionEffects\":[{{\"target\":\"x\",\"effect\":\"Blur\"}}]}}")]), "WL005", 1);
    }

    [Fact]
    public async Task WL005_passes_for_re_encode_and_for_a_plain_stream_copy()
    {
        Quiet(await LintAsync([Compile(1, $"{{{KeepWhole},\"mode\":\"Reencode\",\"enableMusic\":true}}")]), "WL005");
        Quiet(await LintAsync([Compile(1, $"{{{KeepWhole},\"mode\":\"StreamCopy\",\"allowKeyframeSnapping\":true}}")]), "WL005");
    }

    // ---------------- WL006 ----------------

    [Fact]
    public async Task WL006_a_step_whose_capability_has_no_provider_fires()
    {
        FakeFacts none = new();
        none.MissingProviders.UnionWith([InferenceProviderCapability.SpeechSynthesis, InferenceProviderCapability.VideoGeneration,
            InferenceProviderCapability.MusicGeneration, InferenceProviderCapability.Transcription, InferenceProviderCapability.Vision]);
        Fires(await LintAsync([Voiceover(1, """{"source":{"kind":"Inline"}}""")], facts: none), "WL006", 1);
        Fires(await LintAsync([Of(1, StepType.VideoGenerate, """{"planSource":"Inline"}""")], facts: none), "WL006", 1);
        Fires(await LintAsync([Of(1, StepType.AudioGenerate, """{"kind":"Music"}""")], facts: none), "WL006", 1);
        Fires(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"transcription":"Required"}""")], facts: none), "WL006", 1);
        Fires(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"vision":"Required"}""")], facts: none), "WL006", 1);
    }

    [Fact]
    public async Task WL006_passes_with_a_provider_and_when_transcription_is_only_optional()
    {
        Quiet(await LintAsync([Voiceover(1, """{"source":{"kind":"Inline"}}""")]), "WL006");
        FakeFacts none = new();
        none.MissingProviders.Add(InferenceProviderCapability.Transcription);
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"transcription":"Optional"}""")], facts: none), "WL006");
    }

    // ---------------- WL007 ----------------

    [Fact]
    public async Task WL007_a_feature_or_resolution_the_plan_lacks_fires()
    {
        FakeFacts trial = new() { UnentitledAboveShortSide = 1080 };
        trial.UnentitledSteps.Add(StepType.VideoGenerate);
        Fires(await LintAsync([Of(1, StepType.VideoGenerate, """{"planSource":"Inline"}""")], facts: trial), "WL007", 1);
        IReadOnlyList<LintFinding> resolution = await LintAsync([Agent(1, AgentType.AuthorAgent)], new RunBrief(Resolution: "4K"), trial);
        Fires(resolution, "WL007");
        resolution.Rule("WL007").Single().StepOrder.Should().BeNull();
    }

    [Fact]
    public async Task WL007_passes_inside_the_plan()
    {
        FakeFacts trial = new() { UnentitledAboveShortSide = 1080 };
        trial.UnentitledSteps.Add(StepType.VideoGenerate);
        Quiet(await LintAsync([Agent(1, AgentType.AuthorAgent)], new RunBrief(Resolution: "1080p"), trial), "WL007");
        Quiet(await LintAsync([Of(1, StepType.VideoGenerate, """{"planSource":"Inline"}""")]), "WL007");
    }

    // ---------------- WL008 / WL009 ----------------

    [Fact]
    public async Task WL008_a_voice_without_live_consent_or_outside_the_project_fires()
    {
        Guid voice = Guid.NewGuid();
        Guid foreign = Guid.NewGuid();
        FakeFacts facts = new();
        facts.Voices[voice] = new LintVoiceFact(true, false, "consent_missing", "Anna", null);
        facts.Voices[foreign] = new LintVoiceFact(false, false, "not_found", null, null);
        string Config(Guid id) => $$"""{"source":{"kind":"Inline"},"projectVoiceId":"{{id}}"}""";
        Fires(await LintAsync([Voiceover(1, Config(voice))], facts: facts), "WL008", 1);
        Fires(await LintAsync([Voiceover(1, Config(foreign))], facts: facts), "WL008", 1);
        // Named only in the brief, the voice is the run's default: a warning, and only when a Voiceover step will speak it.
        (await LintAsync([Voiceover(1, """{"source":{"kind":"Inline"}}""")],
            new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated, ProjectVoiceId: voice)), facts))
            .Rule("WL008").Should().ContainSingle().Which.Severity.Should().Be(LintSeverity.Warning);
        Quiet(await LintAsync([Agent(1, AgentType.AuthorAgent)],
            new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated, ProjectVoiceId: voice)), facts), "WL008");
    }

    [Fact]
    public async Task WL008_passes_for_a_selectable_voice()
    {
        Guid voice = Guid.NewGuid();
        FakeFacts facts = new();
        facts.Voices[voice] = new LintVoiceFact(true, true, null, "Anna", null);
        Quiet(await LintAsync([Voiceover(1, $$"""{"source":{"kind":"Inline"},"projectVoiceId":"{{voice}}"}""")], facts: facts), "WL008");
    }

    [Fact]
    public async Task WL009_a_language_the_voice_does_not_speak_warns_and_unknown_support_is_silent()
    {
        Guid voice = Guid.NewGuid();
        FakeFacts facts = new();
        facts.Voices[voice] = new LintVoiceFact(true, true, null, "Anna", ["en", "de"]);
        RunBrief italian = new(Language: "it", Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated, ProjectVoiceId: voice));
        Fires(await LintAsync([Voiceover(1, """{"source":{"kind":"Inline"}}""")], italian, facts), "WL009");
        Quiet(await LintAsync([Voiceover(1, """{"source":{"kind":"Inline"}}""")],
            italian with { Language = "en-GB" }, facts), "WL009");
        facts.Voices[voice] = new LintVoiceFact(true, true, null, "Anna", null);
        Quiet(await LintAsync([Voiceover(1, """{"source":{"kind":"Inline"}}""")], italian, facts), "WL009");
    }

    // ---------------- WL010 ----------------

    [Fact]
    public async Task WL010_a_step_whose_media_the_project_does_not_have_fires()
    {
        FakeFacts empty = new() { FileCounts = Enum.GetValues<RunBriefFileRole>().ToDictionary(r => r, _ => 0) };
        Fires(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"source":{"kind":"ProjectFile"}}""")], facts: empty), "WL010", 1);
        Fires(await LintAsync([Agent(1, AgentType.CodeStructureAnalyzer)], facts: empty), "WL010", 1);
        // The engine makes the film without music or sound effects it cannot find: warnings, not errors.
        (await LintAsync([Agent(1, AgentType.MusicSupervisor)], facts: empty)).Rule("WL010").Should().ContainSingle().Which.Severity.Should().Be(LintSeverity.Warning);
        (await LintAsync([Agent(1, AgentType.SoundDesigner)], facts: empty)).Rule("WL010").Should().ContainSingle().Which.Severity.Should().Be(LintSeverity.Warning);
        Guid gone = Guid.NewGuid();
        FakeFacts missingFile = new();
        missingFile.MissingFiles.Add(gone);
        Fires(await LintAsync([Of(1, StepType.VideoAnalyze, $$$"""{"version":1,"source":{"kind":"ProjectFile","projectFileId":"{{{gone}}}"}}""")], facts: missingFile), "WL010", 1);
    }

    [Fact]
    public async Task WL010_a_zip_that_is_still_unpacking_says_so_instead_of_cannot_read()
    {
        Dictionary<RunBriefFileRole, int> none = Enum.GetValues<RunBriefFileRole>().ToDictionary(r => r, _ => 0);
        LintFinding unpacking = (await LintAsync([Agent(1, AgentType.CodeStructureAnalyzer)],
            facts: new FakeFacts { FileCounts = none, ArchiveFiles = 1, UnpackingArchives = 1 })).Rule("WL010").Should().ContainSingle().Subject;
        unpacking.Severity.Should().Be(LintSeverity.Error);
        unpacking.MessageKey.Should().Be("WL010.unpacking");
        // Once code exists nothing fires at all, and a finished unpack is the archive-only message again.
        (await LintAsync([Agent(1, AgentType.CodeStructureAnalyzer)], facts: new FakeFacts { ArchiveFiles = 1, UnpackingArchives = 1 }))
            .Rule("WL010").Should().BeEmpty();
        (await LintAsync([Agent(1, AgentType.CodeStructureAnalyzer)], facts: new FakeFacts { FileCounts = none, ArchiveFiles = 1 }))
            .Rule("WL010").Should().ContainSingle().Which.MessageKey.Should().Be("WL010.archiveOnly");
    }

    [Fact]
    public async Task WL010_a_project_with_only_archives_says_it_cannot_read_the_zip()
    {
        Dictionary<RunBriefFileRole, int> none = Enum.GetValues<RunBriefFileRole>().ToDictionary(r => r, _ => 0);
        LintFinding archives = (await LintAsync([Agent(1, AgentType.CodeStructureAnalyzer)],
            facts: new FakeFacts { FileCounts = none, ArchiveFiles = 1 })).Rule("WL010").Should().ContainSingle().Subject;
        archives.Severity.Should().Be(LintSeverity.Error);
        archives.MessageKey.Should().Be("WL010.archiveOnly");
        // No archives: the plain "none in this project" message.
        (await LintAsync([Agent(1, AgentType.CodeStructureAnalyzer)], facts: new FakeFacts { FileCounts = none }))
            .Rule("WL010").Should().ContainSingle().Which.MessageKey.Should().Be("WL010.missingMedia");
    }

    [Fact]
    public async Task WL010_passes_with_files_a_brief_that_names_them_or_unknown_counts()
    {
        FakeFacts empty = new() { FileCounts = Enum.GetValues<RunBriefFileRole>().ToDictionary(r => r, _ => 0) };
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"source":{"kind":"ProjectFile"}}""")]), "WL010");
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"source":{"kind":"ProjectFile"}}""")],
            new RunBrief(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Video, [Guid.NewGuid()])]), empty), "WL010");
        Quiet(await LintAsync([Agent(1, AgentType.CodeStructureAnalyzer)], facts: new FakeFacts { FileCounts = null }), "WL010");
    }

    // ---------------- WL011 / WL016 ----------------

    [Fact]
    public async Task WL011_a_brief_field_no_step_reads_is_an_info()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(
            [Of(1, StepType.VideoAnalyze, """{"version":1}"""), Compile(2, $"{{{KeepWhole}}}")], new RunBrief(Language: "it"));
        Fires(findings, "WL011");
        findings.Rule("WL011").Single().Message.Should().Contain("language");
    }

    [Fact]
    public async Task WL011_passes_when_a_step_reads_the_field()
    {
        Quiet(await LintAsync([Agent(1, AgentType.ScriptwriterAgent)], new RunBrief(Language: "it", LengthSec: 30)), "WL011");
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1}"""), Compile(2, $"{{{KeepWhole}}}")], new RunBrief(SourceLanguage: "en", LengthSec: 40)), "WL011");
    }

    [Fact]
    public async Task WL016_a_narrated_brief_without_a_voice_step_warns_with_the_closed_patch()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(
            [Agent(1, AgentType.ScriptwriterAgent)], new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated)));
        Fires(findings, "WL016");
        LintFinding silent = findings.Rule("WL016").Single();
        silent.Severity.Should().Be(LintSeverity.Warning);
        silent.Fix.Should().NotBeNull();
        silent.Fix!.PatchId.Should().Be("addVoiceover");
        silent.Fix.Kind.Should().Be(LintFixKind.AddStep);
    }

    [Fact]
    public async Task WL016_passes_with_a_voiceover_step_or_a_silent_brief()
    {
        RunBrief narrated = new(Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated));
        Quiet(await LintAsync([Voiceover(1, """{"source":{"kind":"Inline"}}""")], narrated), "WL016");
        Quiet(await LintAsync([Agent(1, AgentType.ScriptwriterAgent)], new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.None))), "WL016");
    }

    // ---------------- WL012 ----------------

    [Fact]
    public async Task WL012_a_design_system_gate_or_brief_with_nothing_approved_fires()
    {
        FakeFacts none = new() { DesignApproved = false };
        Fires(await LintAsync([Of(1, StepType.DesignSystem, """{"op":"Require"}""")], facts: none), "WL012", 1);
        IReadOnlyList<LintFinding> briefOnly = await LintAsync([Agent(1, AgentType.AuthorAgent)], new RunBrief(DesignSystem: "project"), none);
        briefOnly.Rule("WL012").Should().ContainSingle().Which.Severity.Should().Be(LintSeverity.Warning,
            "a brief alone does not fail a run, so that variant only warns");
    }

    [Fact]
    public async Task WL012_passes_with_an_approved_system_or_when_the_brief_opts_out()
    {
        Quiet(await LintAsync([Of(1, StepType.DesignSystem, """{"op":"Require"}""")]), "WL012");
        Quiet(await LintAsync([Agent(1, AgentType.AuthorAgent)], new RunBrief(DesignSystem: "none"), new FakeFacts { DesignApproved = false }), "WL012");
        Quiet(await LintAsync([Of(1, StepType.DesignSystem, """{"op":"Parse"}""")], facts: new FakeFacts { DesignApproved = false }), "WL012");
    }

    // ---------------- WL013 ----------------

    [Fact]
    public async Task WL013_a_paid_step_inside_the_review_loop_span_warns()
    {
        Fires(await LintAsync([Agent(1, AgentType.AuthorAgent), Of(2, StepType.VideoGenerate, """{"planSource":"Inline"}"""), ReviewLoopAt(3, 1)]), "WL013", 2);
        Fires(await LintAsync([Agent(1, AgentType.NarrationWriter), Voiceover(2, """{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":1}}"""), ReviewLoopAt(3, 1)]), "WL013", 2);
    }

    [Fact]
    public async Task WL013_passes_when_the_paid_step_is_before_the_loop_target()
    {
        Quiet(await LintAsync([Of(1, StepType.VideoGenerate, """{"planSource":"Inline"}"""), Agent(2, AgentType.AuthorAgent), ReviewLoopAt(3, 2)]), "WL013");
    }

    // ---------------- WL014 ----------------

    [Fact]
    public async Task WL014_any_step_order_reference_to_a_later_step_fires()
    {
        Fires(await LintAsync([Of(1, StepType.ThumbnailGenerate, """{"analysisStepOrder":3}"""), Agent(2, AgentType.AuthorAgent), Of(3, StepType.VideoAnalyze, """{"version":1}""")]), "WL014", 1);
        Fires(await LintAsync([Of(1, StepType.ThumbnailGenerate, """{"analysisStepOrder":1}""")]), "WL014", 1);
    }

    [Fact]
    public async Task WL014_passes_for_earlier_references_and_does_not_repeat_a_specific_rule()
    {
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1}"""), Of(2, StepType.ThumbnailGenerate, """{"analysisStepOrder":1}""")]), "WL014");
        // A forward voiceover source is WL001's finding; WL014 stays quiet about the same reference.
        IReadOnlyList<LintFinding> findings = await LintAsync([Voiceover(1, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":2}}"""), Agent(2, AgentType.ScriptwriterAgent)]);
        Quiet(findings, "WL014");
        Fires(findings, "WL001", 1);
    }

    // ---------------- WL015 ----------------

    [Fact]
    public async Task WL015_an_image_edit_without_the_egress_switch_fires_and_with_it_or_for_generate_is_quiet()
    {
        Fires(await LintAsync([Of(1, StepType.ImageGenerate, """{"mode":"Edit","instruction":"warmer","source":{"kind":"ProjectFile","projectFileId":"11111111-1111-1111-1111-111111111111"}}""")]), "WL015", 1);
        Quiet(await LintAsync([Of(1, StepType.ImageGenerate, """{"mode":"Edit","instruction":"warmer","allowSourceMediaEgress":true}""")]), "WL015");
        Quiet(await LintAsync([Of(1, StepType.ImageGenerate, """{"mode":"Generate","prompt":"a fox"}""")]), "WL015");
    }

    [Fact]
    public async Task WL015_sending_footage_to_a_vendor_without_the_switch_fires()
    {
        Fires(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"trackObjects":[{"label":"plate","method":"Segmentation"}],"allowSourceMediaEgress":false}""")]), "WL015", 1);
        Fires(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"trackObjects":[{"label":"plate","method":1}]}""")]), "WL015", 1);
        Fires(await LintAsync([Of(1, StepType.VideoGenerate, """{"planSource":"Inline","firstFrame":{"from":"Step","stepOrder":1}}""")]), "WL015", 1);
    }

    [Fact]
    public async Task WL015_passes_with_the_switch_on_or_no_egress()
    {
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"trackObjects":[{"label":"plate","method":"Segmentation"}],"allowSourceMediaEgress":true}""")]), "WL015");
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"trackObjects":[{"label":"plate","method":"Feature"}]}""")]), "WL015");
        Quiet(await LintAsync([Of(1, StepType.VideoGenerate, """{"planSource":"Inline"}""")]), "WL015");
    }

    // ---------------- WL017 ----------------

    [Fact]
    public async Task WL017_a_length_longer_than_the_footage_warns_and_unknown_footage_is_silent()
    {
        List<WorkflowStep> steps = [Of(1, StepType.VideoAnalyze, """{"version":1}""")];
        Fires(await LintAsync(steps, new RunBrief(LengthSec: 120), new FakeFacts { SourceSeconds = 40 }), "WL017");
        Quiet(await LintAsync(steps, new RunBrief(LengthSec: 30), new FakeFacts { SourceSeconds = 40 }), "WL017");
        Quiet(await LintAsync(steps, new RunBrief(LengthSec: 120), new FakeFacts { SourceSeconds = null }), "WL017");
    }

    // ---------------- WL018 ----------------

    private sealed class Orphans(params ReelBolt.Shared.Workflows.Apps.AppManifestOrphan[] found) : IAppManifestOrphanSource
    {
        public Task<IReadOnlyList<ReelBolt.Shared.Workflows.Apps.AppManifestOrphan>> FindOrphansAsync(WorkflowLintRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReelBolt.Shared.Workflows.Apps.AppManifestOrphan>>(found);
    }

    [Fact]
    public async Task WL018_an_orphan_from_the_manifest_seam_fires_and_none_passes()
    {
        Fires(await LintAsync([Agent(1, AgentType.AuthorAgent)], orphans: new Orphans(new ReelBolt.Shared.Workflows.Apps.AppManifestOrphan("WL018", 0, "Voice", "The control \"Voice\" reads nothing."))), "WL018");
        Quiet(await LintAsync([Agent(1, AgentType.AuthorAgent)], orphans: new Orphans()), "WL018");
        Quiet(await LintAsync([Agent(1, AgentType.AuthorAgent)]), "WL018");
    }

    // ---------------- WL021 ----------------

    private const string AnalyzeStepOutput6 = """{"version":1,"source":{"kind":"StepOutput","stepOrder":%S%}}""";

    private static string Narrated(string compileExtra = "") =>
        $"{{{KeepWhole},\"enableVoiceover\":true,\"voiceoverStepOrder\":2,\"analysisStepOrder\":3{compileExtra}}}";

    private static WorkflowStep AnalyzeOf(int order, int source) =>
        Of(order, StepType.VideoAnalyze, AnalyzeStepOutput6.Replace("%S%", source.ToString()));

    private static readonly string VoiceoverInline = """{"source":{"kind":"Inline","lines":[{"text":"hi","startSec":0}]}}""";

    [Fact]
    public async Task WL021_fires_over_a_revise_film_with_the_ceo_shape()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync([Of(1, StepType.Revise), Voiceover(2, VoiceoverInline), AnalyzeOf(3, 1),
            Compile(4, Narrated(",\"voiceoverMode\":\"OverMusic\""))]);
        Fires(findings, "WL021", 4);
        LintFinding finding = findings.Rule("WL021").Single();
        finding.Severity.Should().Be(LintSeverity.Error);
        finding.Fix!.Kind.Should().Be(LintFixKind.ChangeSetting);
        finding.Fix.Label.Should().Contain("muteSourceAudio");
        finding.Message.Should().Contain("heard twice").And.Contain("muteSourceAudio");
    }

    [Fact]
    public async Task WL021_fires_for_duck_dialogue_and_for_each_narrated_film_kind()
    {
        Fires(await LintAsync([Of(1, StepType.Revise), Voiceover(2, VoiceoverInline), AnalyzeOf(3, 1),
            Compile(4, Narrated(",\"voiceoverMode\":\"DuckDialogue\""))]), "WL021", 4);

        WorkflowStep continued = Agent(1, AgentType.AuthorAgent);
        continued.ContinueCompositionJson = """{"executionId":"00000000-0000-0000-0000-000000000001"}""";
        Fires(await LintAsync([continued, Voiceover(2, VoiceoverInline), AnalyzeOf(3, 1), Compile(4, Narrated())]), "WL021", 4);

        Fires(await LintAsync([Voiceover(1, VoiceoverInline), Agent(2, AgentType.AuthorAgent), AnalyzeOf(3, 2),
            Compile(4, Narrated().Replace("voiceoverStepOrder\":2", "voiceoverStepOrder\":1"))]), "WL021", 4);
        Fires(await LintAsync([Voiceover(1, VoiceoverInline), Of(2, StepType.TimelineRender), AnalyzeOf(3, 2),
            Compile(4, Narrated().Replace("voiceoverStepOrder\":2", "voiceoverStepOrder\":1"))]), "WL021", 4);

        Fires(await LintAsync([Voiceover(1, VoiceoverInline), Compile(2, $"{{{KeepWhole},\"enableVoiceover\":true,\"voiceoverStepOrder\":1}}"),
            AnalyzeOf(3, 2), Compile(4, Narrated().Replace("voiceoverStepOrder\":2", "voiceoverStepOrder\":1"))]), "WL021", 4);
    }

    [Fact]
    public async Task WL021_fires_for_a_previous_step_output_source()
    {
        Fires(await LintAsync([Of(1, StepType.Revise), Voiceover(2, VoiceoverInline),
            Of(3, StepType.VideoAnalyze, """{"version":1,"source":{"kind":"PreviousStepOutput"}}"""), Compile(4, Narrated())]), "WL021", 4);
    }

    [Fact]
    public async Task WL021_stays_quiet_when_the_voiceover_is_not_doubled_or_the_source_is_not_known_narrated()
    {
        Quiet(await LintAsync([Of(1, StepType.Revise), Voiceover(2, VoiceoverInline), AnalyzeOf(3, 1),
            Compile(4, Narrated(",\"voiceoverMode\":\"ReplaceDialogue\""))]), "WL021");
        Quiet(await LintAsync([Of(1, StepType.Revise), Voiceover(2, VoiceoverInline), AnalyzeOf(3, 1),
            Compile(4, Narrated(",\"muteSourceAudio\":true"))]), "WL021");
        Quiet(await LintAsync([Of(1, StepType.Revise), Voiceover(2, VoiceoverInline), AnalyzeOf(3, 1),
            Compile(4, Narrated().Replace("enableVoiceover\":true", "enableVoiceover\":false"))]), "WL021");
        Quiet(await LintAsync([Voiceover(1, VoiceoverInline),
            Of(2, StepType.VideoAnalyze, """{"version":1,"source":{"kind":"ProjectFile"}}"""),
            Compile(3, Narrated().Replace("analysisStepOrder\":3", "analysisStepOrder\":2"))]), "WL021");
        // An Agent render with no earlier Voiceover and no continued composition is left to the runtime guard.
        Quiet(await LintAsync([Agent(1, AgentType.AuthorAgent), Voiceover(2, VoiceoverInline), AnalyzeOf(3, 1), Compile(4, Narrated())]), "WL021");
    }

    [Fact]
    public async Task WL021_reads_unreadable_configs_without_throwing()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync([Of(1, StepType.Revise), Voiceover(2, VoiceoverInline),
            Of(3, StepType.VideoAnalyze, "{not json"), Compile(4, Narrated()),
            Compile(5, "[1,2"), Compile(6, """{"enableVoiceover":"yes","voiceoverMode":{},"analysisStepOrder":"x"}""")]);
        findings.Rule("WL021").Should().BeEmpty();
    }

    // ---------------- WL019 ----------------

    [Fact]
    public async Task WL019_captions_with_nothing_to_burn_fire()
    {
        Fires(await LintAsync([Compile(1, $"{{{KeepWhole},\"enableCaptions\":true}}")]), "WL019", 1);
    }

    [Fact]
    public async Task WL019_passes_with_an_applied_voiceover_or_a_transcript_source()
    {
        Quiet(await LintAsync([Voiceover(1, """{"source":{"kind":"Inline"}}"""),
            Compile(2, $"{{{KeepWhole},\"enableCaptions\":true,\"enableVoiceover\":true,\"voiceoverStepOrder\":1}}")]), "WL019");
        Quiet(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"transcription":"Required"}"""),
            Compile(2, $"{{{KeepWhole},\"enableCaptions\":true,\"captionSource\":\"Transcript\",\"analysisStepOrder\":1}}")]), "WL019");
    }

    [Fact]
    public async Task WL019_fires_when_the_transcript_source_has_no_transcribing_analysis()
    {
        Fires(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"transcription":"Off"}"""),
            Compile(2, $"{{{KeepWhole},\"enableCaptions\":true,\"captionSource\":\"Transcript\",\"analysisStepOrder\":1}}")]), "WL019", 2);
        Fires(await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"transcription":"Required"}"""),
            Compile(2, $"{{{KeepWhole},\"enableCaptions\":true,\"captionSource\":\"Transcript\"}}")]), "WL019", 2);
    }

    [Fact]
    public async Task WL019_judges_the_transcript_source_even_when_a_voiceover_is_on()
    {
        Fires(await LintAsync([Voiceover(1, """{"source":{"kind":"Inline"}}"""),
            Of(2, StepType.VideoAnalyze, """{"version":1,"transcription":"Off"}"""),
            Compile(3, $"{{{KeepWhole},\"enableCaptions\":true,\"enableVoiceover\":true,\"voiceoverStepOrder\":1,\"captionSource\":\"Transcript\",\"analysisStepOrder\":2}}")]), "WL019", 3);
    }

    [Fact]
    public async Task The_transcript_translation_step_must_be_a_translator()
    {
        WorkflowStep[] ok = [Of(1, StepType.VideoAnalyze, """{"version":1,"transcription":"Required"}"""),
            Agent(2, AgentType.NarrationTranslator),
            Compile(3, $"{{{KeepWhole},\"enableCaptions\":true,\"captionSource\":\"Transcript\",\"analysisStepOrder\":1,\"captionTranslationStepOrder\":2}}")];
        Quiet(await LintAsync(ok), "WL004");

        WorkflowStep[] wrong = [Of(1, StepType.VideoAnalyze, """{"version":1,"transcription":"Required"}"""),
            Agent(2, AgentType.NarrationWriter),
            Compile(3, $"{{{KeepWhole},\"enableCaptions\":true,\"captionSource\":\"Transcript\",\"analysisStepOrder\":1,\"captionTranslationStepOrder\":2}}")];
        Fires(await LintAsync(wrong), "WL004", 3);
    }

    // ---------------- WL020 ----------------

    [Fact]
    public async Task WL020_a_review_before_the_narration_and_final_cut_fires_with_step_numbers()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync([
            Agent(6, AgentType.AuthorAgent), ReviewLoopAt(7, 6),
            Voiceover(8, """{"source":{"kind":"Inline","lines":[{"text":"hi","startSec":0}]}}"""),
            Compile(10, $"{{{KeepWhole}}}")]);
        Fires(findings, "WL020", 7);
        findings.Rule("WL020").Should().ContainSingle().Which.Message.Should().Be(
            "The review at step 7 runs before the narration (step 8) and the final cut (step 10), so it checks a silent picture. Move the review after step 10.");
    }

    [Fact]
    public async Task WL020_passes_when_the_review_comes_last()
    {
        Quiet(await LintAsync([
            Agent(1, AgentType.AuthorAgent),
            Voiceover(2, """{"source":{"kind":"Inline","lines":[{"text":"hi","startSec":0}]}}"""),
            Compile(3, $"{{{KeepWhole}}}"), ReviewLoopAt(4, 1)]), "WL020");
        Quiet(await LintAsync([Agent(1, AgentType.AuthorAgent), ReviewLoopAt(2, 1)]), "WL020");
    }

    [Fact]
    public async Task WL020_does_not_fire_for_a_script_review_ahead_of_the_voice()
    {
        Quiet(await LintAsync([
            Agent(1, AgentType.ScriptwriterAgent), ReviewLoopAt(2, 1),
            Voiceover(3, """{"source":{"kind":"Inline","lines":[{"text":"hi","startSec":0}]}}"""),
            Compile(4, $"{{{KeepWhole}}}")]), "WL020");
    }

    [Fact]
    public async Task WL020_still_fires_for_a_review_aimed_at_a_room_step_that_sits_before_the_final_cut()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync([
            Of(1, StepType.EditRoom, null), ReviewLoopAt(2, 1), Compile(3, $"{{{KeepWhole}}}")]);
        Fires(findings, "WL020", 2);
    }

    [Fact]
    public async Task WL020_suggests_no_reference_retarget_because_the_fix_is_a_move()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync([
            Agent(1, AgentType.AuthorAgent), ReviewLoopAt(2, 1), Compile(3, $"{{{KeepWhole}}}")]);
        findings.Rule("WL020").Should().ContainSingle().Which.Fix!.Kind.Should().Be(LintFixKind.None);
    }

    // ---------------- WL026 ----------------

    [Fact]
    public async Task WL026_a_voiceover_that_types_its_lines_inline_ignores_the_script_step()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync([
            Agent(1, AgentType.ScriptwriterAgent), Voiceover(2, VoiceoverInline)]);

        Fires(findings, "WL026", 2);
        LintFinding finding = findings.Rule("WL026").Single();
        finding.Message.Should().Be(
            "The narration at step 2 is typed inline even though step 1 writes the lines, so that script is never spoken. Point the voiceover at the script.");
        finding.MessageKey.Should().Be("WL026");
        finding.Args.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["stepOrder"] = "2",
            ["scriptOrder"] = "1",
            ["scriptKind"] = "scriptwriter"
        });
        finding.Fix.Should().NotBeNull();
        finding.Fix!.Kind.Should().Be(LintFixKind.ChangeSetting);
        finding.Fix.LabelId.Should().Be("WL026.useScript");
        finding.Fix.Label.Should().Be("Point the voiceover at the script");
    }

    [Fact]
    public async Task WL026_fires_for_a_narration_writer_or_translator_writing_the_lines()
    {
        Fires(await LintAsync([Agent(1, AgentType.NarrationWriter), Voiceover(2, VoiceoverInline)]), "WL026", 2);
        Fires(await LintAsync([Agent(1, AgentType.NarrationTranslator), Voiceover(2, VoiceoverInline)]), "WL026", 2);
    }

    [Fact]
    public async Task WL026_passes_when_the_voiceover_reads_the_script_or_no_writer_came_before()
    {
        Quiet(await LintAsync([Agent(1, AgentType.ScriptwriterAgent),
            Voiceover(2, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":1}}""")]), "WL026");
        Quiet(await LintAsync([Agent(1, AgentType.NarrationWriter),
            Voiceover(2, """{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":1}}""")]), "WL026");
        Quiet(await LintAsync([Agent(1, AgentType.PickupPlanner),
            Voiceover(2, """{"source":{"kind":"PickupPlan","pickupPlannerStepOrder":1}}""")]), "WL026");
        Quiet(await LintAsync([Voiceover(1, VoiceoverInline), Agent(2, AgentType.ScriptwriterAgent)]), "WL026");
        Quiet(await LintAsync([Voiceover(1, VoiceoverInline)]), "WL026");
    }

    // ---------------- shape ----------------

    [Fact]
    public async Task Findings_are_ordered_errors_first_and_carry_plain_messages_without_ids()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(
            [Agent(1, AgentType.ScriptwriterAgent), ReviewLoopAt(2, 9)],
            new RunBrief(Language: "it", SourceLanguage: "en", Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated)));
        findings.Select(f => f.Severity).Should().BeInAscendingOrder();
        findings.Should().OnlyContain(f => !f.Message.Contains("Guid") && !System.Text.RegularExpressions.Regex.IsMatch(f.Message, "[0-9a-f]{8}-[0-9a-f]{4}-"));
    }

    [Fact]
    public async Task An_unreadable_config_never_throws()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync([Voiceover(1, "{not json"), Compile(2, "[]"), Of(3, StepType.VideoAnalyze, "null")]);
        findings.Rule("WL001").Should().BeEmpty();
    }
}
