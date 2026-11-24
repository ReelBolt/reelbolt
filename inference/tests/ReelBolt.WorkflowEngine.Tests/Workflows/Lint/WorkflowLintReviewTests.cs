using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>Rule refinements from the QA and security review of the linter.</summary>
public class WorkflowLintReviewTests
{
    private const string KeepWhole = "\"version\":1,\"keepWholeSources\":true";

    [Theory]
    [InlineData(StepType.DailiesRoom)]
    [InlineData(StepType.VideoGenerate)]
    [InlineData(StepType.EditRoom)]
    [InlineData(StepType.GraphicsRoom)]
    [InlineData(StepType.ColorGradeRoom)]
    [InlineData(StepType.Agent)]
    public async Task WL003_accepts_a_loop_over_every_step_type_that_makes_something_new(StepType producing)
    {
        WorkflowStep target = producing == StepType.Agent ? Agent(1, AgentType.AuthorAgent) : Of(1, producing, producing == StepType.VideoGenerate ? """{"planSource":"Inline"}""" : null);
        (await LintAsync([target, ReviewLoopAt(2, 1)])).Rule("WL003").Should().BeEmpty();
    }

    [Fact]
    public async Task WL003_a_deterministic_target_is_fine_when_a_model_step_follows_it_in_the_loop()
    {
        (await LintAsync([Of(1, ReelBolt.Shared.Data.Models.StepType.VideoAnalyze, """{"version":1}"""), Agent(2, AgentType.VideoStoryEditor), ReviewLoopAt(3, 1)]))
            .Rule("WL003").Should().BeEmpty();
    }

    [Fact]
    public async Task WL012_a_Require_step_for_the_product_map_only_does_not_need_a_design()
    {
        FakeFacts none = new() { DesignApproved = false };
        (await LintAsync([Of(1, StepType.DesignSystem, """{"op":"Require","require":{"requireDesign":false,"requireProductMap":true}}""")], facts: none))
            .Rule("WL012").Should().BeEmpty();
        (await LintAsync([Of(1, StepType.DesignSystem, """{"op":"Require","require":{"requireDesign":true}}""")], facts: none))
            .Rule("WL012").Should().ContainSingle();
    }

    [Fact]
    public async Task WL015_keyframes_from_a_project_image_also_count()
    {
        Guid image = Guid.NewGuid();
        (await LintAsync([Of(1, StepType.VideoGenerate, $$"""{"planSource":"Inline","firstFrameProjectFileId":"{{image}}"}""")])).Rule("WL015").Should().ContainSingle();
        (await LintAsync([Of(1, StepType.VideoGenerate, $$"""{"planSource":"Inline","lastFrameProjectFileId":"{{image}}","allowSourceMediaEgress":true}""")])).Rule("WL015").Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"outputFormat\":\"Vertical\"")]
    [InlineData("\"voiceoverMode\":\"DuckDialogue\"")]
    [InlineData("\"censorLabels\":[\"face\"]")]
    [InlineData("\"programFadeInMs\":500")]
    [InlineData("\"programAudioFadeOutMs\":300")]
    public async Task WL005_covers_the_other_stream_copy_conflicts_that_fail_today(string extra)
    {
        (await LintAsync([Compile(1, $"{{{KeepWhole},\"mode\":\"StreamCopy\",\"allowKeyframeSnapping\":true,{extra}}}")])).Rule("WL005").Should().ContainSingle();
    }

    [Fact]
    public async Task WL005_stream_copy_without_keyframe_snapping_fails_today_and_is_caught()
    {
        (await LintAsync([Compile(1, $"{{{KeepWhole},\"mode\":\"StreamCopy\"}}")])).Rule("WL005").Should().ContainSingle();
    }

    [Fact]
    public async Task WL010_missing_music_is_a_warning_and_missing_footage_an_error()
    {
        FakeFacts empty = new() { FileCounts = Enum.GetValues<RunBriefFileRole>().ToDictionary(r => r, _ => 0) };
        Guid track = Guid.NewGuid();
        FakeFacts gone = new();
        gone.MissingFiles.Add(track);
        (await LintAsync([Compile(1, $$"""{{{KeepWhole}},"enableMusic":true,"musicTrackProjectFileId":"{{track}}"}""")], facts: gone))
            .Rule("WL010").Should().ContainSingle().Which.Severity.Should().Be(LintSeverity.Warning);
        (await LintAsync([Of(1, StepType.VideoAnalyze, """{"version":1,"source":{"kind":"ProjectFile"}}""")], facts: empty))
            .Rule("WL010").Should().ContainSingle().Which.Severity.Should().Be(LintSeverity.Error);
    }

    // ------------------------------------------------------------------ bounded lookups (SEC-WAB-LINT-1)

    private sealed class CountingFacts : IWorkflowLintFacts
    {
        public int Warms { get; private set; }

        public int WarmedIds { get; private set; }

        public int Singles { get; private set; }

        public Task WarmFilesAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct)
        {
            Warms++;
            WarmedIds += fileIds.Count;
            return Task.CompletedTask;
        }

        public Task<bool?> ProjectFileExistsAsync(Guid fileId, CancellationToken ct)
        {
            Singles++;
            return Task.FromResult<bool?>(true);
        }

        public Task<bool?> ProviderAvailableAsync(ReelBolt.Shared.Data.Models.InferenceProviderCapability c, Guid? id, CancellationToken ct) => Task.FromResult<bool?>(null);

        public Task<ReelBolt.Shared.Billing.PlanGateViolation?> StepEntitlementAsync(StepType t, CancellationToken ct) => Task.FromResult<ReelBolt.Shared.Billing.PlanGateViolation?>(null);

        public Task<ReelBolt.Shared.Billing.PlanGateViolation?> ResolutionEntitlementAsync(int s, CancellationToken ct) => Task.FromResult<ReelBolt.Shared.Billing.PlanGateViolation?>(null);

        public Task<IReadOnlyDictionary<RunBriefFileRole, int>?> FileCountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<RunBriefFileRole, int>?>(null);

        public Task<bool?> HasApprovedDesignSystemAsync(CancellationToken ct) => Task.FromResult<bool?>(null);

        public Task<LintVoiceFact?> ProjectVoiceAsync(Guid voiceId, CancellationToken ct) => Task.FromResult<LintVoiceFact?>(null);

        public Task<double?> SourceVideoSecondsAsync(CancellationToken ct) => Task.FromResult<double?>(null);
    }

    [Fact]
    public async Task A_step_naming_thousands_of_files_asks_the_facts_once()
    {
        string sources = string.Join(",", Enumerable.Range(0, 3000).Select(_ => $$"""{"kind":"ProjectFile","projectFileId":"{{Guid.NewGuid()}}"}"""));
        CountingFacts facts = new();
        WorkflowLinter linter = new(new FixedFactsSource(facts));

        await linter.LintAsync(new WorkflowLintRequest(
            Guid.NewGuid(), Guid.NewGuid(), [Of(1, StepType.VideoAnalyze, $$"""{"version":1,"sources":[{{sources}}]}""")], Facts: facts), default);

        facts.Warms.Should().Be(1);
        facts.WarmedIds.Should().Be(3000);
    }

    [Fact]
    public void Naming_file_ids_is_deduplicated()
    {
        Guid one = Guid.NewGuid();
        List<WorkflowStep> steps = [Of(1, StepType.VideoAnalyze, $$"""{"version":1,"sources":[{"kind":"ProjectFile","projectFileId":"{{one}}"},{"kind":"ProjectFile","projectFileId":"{{one}}"}]}""")];
        WorkflowLinter.NamedFileIds(steps).Should().ContainSingle().Which.Should().Be(one);
    }

    // ------------------------------------------------------------------ one consumption table, hidden text

    [Fact]
    public void Variation_selector_runs_private_use_and_unassigned_are_hidden_and_a_single_emoji_selector_is_not()
    {
        string run = "a" + string.Concat(Enumerable.Repeat("\U000E0100", 20));
        ReelBolt.Shared.Workflows.Apps.AppManifestValidator.Sanitize(new ReelBolt.Shared.Workflows.Apps.AppManifest(Title: run)).Title.Should().Be("a");
        ReelBolt.Shared.Workflows.Apps.AppManifestValidator.Sanitize(new ReelBolt.Shared.Workflows.Apps.AppManifest(Title: "a\U000F0000b")).Title.Should().Be("ab");
        ReelBolt.Shared.Workflows.Apps.AppManifestValidator.Sanitize(new ReelBolt.Shared.Workflows.Apps.AppManifest(Title: "a️️️b")).Title.Should().Be("a️b");
        ReelBolt.Shared.Workflows.Apps.AppManifestValidator.Validate(new ReelBolt.Shared.Workflows.Apps.AppManifest(Title: run), []).Should().Contain(e => e.Contains("control characters"));
        ReelBolt.Shared.Workflows.Apps.AppManifestValidator.Validate(new ReelBolt.Shared.Workflows.Apps.AppManifest(Title: "ok ️"), []).Should().NotContain(e => e.Contains("control characters"));
    }

    [Fact]
    public void Clipping_a_long_text_never_leaves_half_a_surrogate_pair()
    {
        string title = new string('x', 39) + "\U0001F600" + "tail";
        string clean = ReelBolt.Shared.Workflows.Apps.AppManifestValidator.SanitizeText(title, 40);
        clean.Should().Be(new string('x', 39));
        clean.Any(char.IsSurrogate).Should().BeFalse();
    }
}
