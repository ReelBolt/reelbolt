using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>
/// WL023: a configured picture/video overlay must name a usable project file or an earlier step that
/// can produce one.
/// </summary>
public sealed class MediaOverlayLintTests
{
    private static readonly Guid FileId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static WorkflowStep Compile(int order, string contentJson) =>
        LintKit.Compile(order,
            $"{{\"version\":1,\"decision\":{{\"source\":\"Previous\"}},\"mediaOverlays\":[{{\"content\":{contentJson}}}]}}");

    private static async Task<IReadOnlyList<LintFinding>> LintAsync(
        IEnumerable<WorkflowStep> steps, Action<LintKit.FakeFacts>? configure = null)
    {
        var facts = new LintKit.FakeFacts();
        configure?.Invoke(facts);
        return (await LintKit.LintAsync(steps, facts: facts)).Where(f => f.RuleId == "WL023").ToList();
    }

    [Theory]
    [InlineData(LintMediaVerdict.Missing, "WL023.missingFile")]
    [InlineData(LintMediaVerdict.WrongKind, "WL023.wrongKind")]
    [InlineData(LintMediaVerdict.Quarantined, "WL023.quarantined")]
    public async Task A_project_file_that_is_not_usable_is_reported(LintMediaVerdict verdict, string messageKey)
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(
            [Compile(1, $"{{\"from\":\"ProjectFile\",\"projectFileId\":\"{FileId}\"}}")],
            facts => facts.MediaVerdicts[FileId] = verdict);

        findings.Should().ContainSingle().Which.MessageKey.Should().Be(messageKey);
    }

    [Fact]
    public async Task A_usable_project_file_is_clean()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(
            [Compile(1, $"{{\"from\":\"ProjectFile\",\"projectFileId\":\"{FileId}\"}}")],
            facts => facts.MediaVerdicts[FileId] = LintMediaVerdict.Usable);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_step_reference_to_a_later_step_is_reported()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(
            [Compile(2, "{\"from\":\"Step\",\"stepOrder\":3}"), LintKit.Of(3, StepType.Agent, agent: AgentType.AuthorAgent)]);

        findings.Should().ContainSingle().Which.MessageKey.Should().Be("WL023.badStep");
    }

    [Fact]
    public async Task A_step_reference_to_a_non_playable_step_is_reported()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(
            [LintKit.Of(1, StepType.VideoAnalyze, "{\"version\":1}"),
             Compile(2, "{\"from\":\"Step\",\"stepOrder\":1}")]);

        findings.Should().ContainSingle().Which.MessageKey.Should().Be("WL023.badStep");
    }

    [Fact]
    public async Task An_earlier_render_step_is_clean()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(
            [LintKit.Agent(1, AgentType.AuthorAgent),
             Compile(2, "{\"from\":\"Step\",\"stepOrder\":1}")]);

        findings.Should().BeEmpty();
    }

    // ---------------- WL004: the mediaOverlayPlan reference ----------------

    private static async Task<IReadOnlyList<LintFinding>> RuleAsync(
        string rule, IEnumerable<WorkflowStep> steps, RunBrief? brief = null, Action<LintKit.FakeFacts>? configure = null)
    {
        var facts = new LintKit.FakeFacts();
        configure?.Invoke(facts);
        return (await LintKit.LintAsync(steps, brief, facts)).Where(f => f.RuleId == rule).ToList();
    }

    private static WorkflowStep CompileRaw(int order, string configJson) => LintKit.Compile(order, configJson);

    [Fact]
    public async Task WL004_a_media_overlay_plan_from_the_previous_overlay_planner_is_clean()
    {
        IReadOnlyList<LintFinding> findings = await RuleAsync("WL004",
        [
            LintKit.Agent(1, AgentType.OverlayPlanner),
            CompileRaw(2, """{"version":1,"mediaOverlayPlan":{"from":"Previous"}}""")
        ]);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task WL004_a_media_overlay_plan_that_is_missing_forward_or_wrong_kind_is_reported()
    {
        LintFinding missing = (await RuleAsync("WL004",
            [CompileRaw(1, """{"version":1,"mediaOverlayPlan":{"from":"Step","stepOrder":9}}""")])).Should().ContainSingle().Which;
        missing.MessageKey.Should().Be("WL004.notInWorkflow");
        missing.Args["input"].Should().Be("mediaOverlayPlan");

        (await RuleAsync("WL004",
            [CompileRaw(1, """{"version":1,"mediaOverlayPlan":{"from":"Step","stepOrder":2}}"""),
             LintKit.Agent(2, AgentType.OverlayPlanner)])).Should().ContainSingle()
            .Which.MessageKey.Should().Be("WL004.comesAfter");

        (await RuleAsync("WL004",
            [LintKit.Of(1, StepType.VideoAnalyze, """{"version":1}"""),
             CompileRaw(2, """{"version":1,"mediaOverlayPlan":{"from":"Step","stepOrder":1}}""")])).Should().ContainSingle()
            .Which.MessageKey.Should().Be("WL004.wrongKind");
    }

    // ---------------- WL010: the overlay role ----------------

    private static Dictionary<RunBriefFileRole, int> NoFiles() =>
        Enum.GetValues<RunBriefFileRole>().ToDictionary(r => r, _ => 0);

    [Fact]
    public async Task WL010_an_analyze_step_that_offers_overlays_needs_pictures()
    {
        IReadOnlyList<LintFinding> findings = await RuleAsync("WL010",
            [LintKit.Of(1, StepType.VideoAnalyze, """{"version":1,"emitOverlayOffers":true}""")],
            configure: facts => facts.FileCounts = NoFiles());

        LintFinding finding = findings.Should().ContainSingle().Which;
        finding.MessageKey.Should().Be("WL010.missingMedia");
        finding.Args["role"].Should().Be("overlay");
        finding.Severity.Should().Be(LintSeverity.Error);
    }

    [Fact]
    public async Task WL010_overlay_pictures_in_the_brief_satisfy_the_role()
    {
        RunBrief brief = new(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Overlay, [Guid.NewGuid()])]);

        IReadOnlyList<LintFinding> findings = await RuleAsync("WL010",
            [LintKit.Of(1, StepType.VideoAnalyze, """{"version":1,"emitOverlayOffers":true}""")],
            brief,
            facts => facts.FileCounts = NoFiles());

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task WL010_project_pictures_satisfy_the_overlay_role()
    {
        // The production facts do not carry an "Overlay" count: with no named set, project images/videos are offered.
        Dictionary<RunBriefFileRole, int> counts = new() { [RunBriefFileRole.Image] = 2 };

        IReadOnlyList<LintFinding> findings = await RuleAsync("WL010",
            [LintKit.Of(1, StepType.VideoAnalyze, """{"version":1,"emitOverlayOffers":true}""")],
            configure: facts => facts.FileCounts = counts);

        findings.Should().BeEmpty();
    }

    // ---------------- WL024: a span the film cannot have ----------------

    private static WorkflowStep OverlayCompile(int order, string overlayJson) =>
        CompileRaw(order, $"{{\"version\":1,\"mediaOverlays\":[{overlayJson}]}}");

    [Theory]
    [InlineData("{\"startSec\":5,\"endSec\":3}")]
    [InlineData("{\"startSec\":-1,\"endSec\":5}")]
    [InlineData("{\"startSec\":1,\"endSec\":90000}")]
    public async Task WL024_an_invalid_span_is_an_error(string overlay)
    {
        IReadOnlyList<LintFinding> findings = await RuleAsync("WL024", [OverlayCompile(1, overlay)]);

        findings.Should().ContainSingle().Which.Severity.Should().Be(LintSeverity.Error);
    }

    [Fact]
    public async Task WL024_an_end_past_the_known_film_is_reported()
    {
        IReadOnlyList<LintFinding> findings = await RuleAsync("WL024",
            [OverlayCompile(1, """{"startSec":1,"endSec":20}""")],
            new RunBrief(LengthSec: 10));

        findings.Should().ContainSingle().Which.MessageKey.Should().Be("WL024");
    }

    [Fact]
    public async Task WL024_the_length_tolerance_and_an_unknown_film_length_are_clean()
    {
        (await RuleAsync("WL024",
            [OverlayCompile(1, """{"startSec":1,"endSec":10.4}""")],
            new RunBrief(LengthSec: 10))).Should().BeEmpty();

        (await RuleAsync("WL024",
            [OverlayCompile(1, """{"startSec":1,"endSec":20}""")])).Should().BeEmpty();
    }

    // ---------------- WL025: a card that stays too long ----------------

    [Fact]
    public async Task WL025_a_card_longer_than_thirty_seconds_warns()
    {
        IReadOnlyList<LintFinding> findings = await RuleAsync("WL025",
            [OverlayCompile(1, """{"form":"Card","startSec":0,"endSec":40}""")]);

        findings.Should().ContainSingle().Which.Severity.Should().Be(LintSeverity.Warning);
    }

    [Fact]
    public async Task WL025_a_card_longer_than_a_quarter_of_a_known_film_warns()
    {
        (await RuleAsync("WL025",
            [OverlayCompile(1, """{"form":"Card","startSec":0,"endSec":20}""")],
            new RunBrief(LengthSec: 60))).Should().ContainSingle();

        (await RuleAsync("WL025",
            [OverlayCompile(1, """{"form":"Card","startSec":0,"endSec":20}""")],
            new RunBrief(LengthSec: 100))).Should().BeEmpty();
    }

    [Fact]
    public async Task WL025_a_short_card_an_inset_and_an_unmeasurable_card_are_clean()
    {
        (await RuleAsync("WL025",
            [OverlayCompile(1, """{"form":"PictureInPicture","startSec":0,"endSec":40}""")])).Should().BeEmpty();

        (await RuleAsync("WL025",
            [OverlayCompile(1, """{"form":"Card","startSec":0,"endSec":10}""")])).Should().BeEmpty();

        (await RuleAsync("WL025",
            [OverlayCompile(1, """{"form":"Card","startSec":0}""")])).Should().BeEmpty();
    }

    [Fact]
    public async Task WL025_a_card_to_the_end_of_a_known_film_is_measured()
    {
        IReadOnlyList<LintFinding> findings = await RuleAsync("WL025",
            [OverlayCompile(1, """{"form":"Card","startSec":0}""")],
            new RunBrief(LengthSec: 60));

        findings.Should().ContainSingle().Which.MessageKey.Should().Be("WL025");
    }

    // ---------------- no false positive ----------------

    [Fact]
    public async Task A_graphics_only_workflow_produces_no_overlay_findings()
    {
        IReadOnlyList<LintFinding> findings = await LintKit.LintAsync(
        [
            LintKit.Agent(1, AgentType.MotionGraphicsPlanner),
            LintKit.Compile(2, """{"version":1,"enableGraphics":true,"graphicsPlan":{"from":"Previous"}}""")
        ]);

        findings.Where(f => f.RuleId is "WL023" or "WL024" or "WL025").Should().BeEmpty();
    }
}
