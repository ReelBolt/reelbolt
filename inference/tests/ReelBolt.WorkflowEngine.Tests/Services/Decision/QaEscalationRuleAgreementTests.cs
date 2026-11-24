#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Decision;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Services.Decision;

/// <summary>
/// T-505 — does the calibration view's RECOMPUTED escalation rule match each decision site's ACTUAL
/// accept rule?
///
/// <para>
/// The two rules under comparison, verbatim from the source:
/// </para>
/// <list type="number">
/// <item>
/// <b>The view's recomputed rule</b> — <c>DecisionCalibrationService.cs:340</c>, over a
/// <see cref="DecisionAnswer"/> rebuilt from the stored row:
/// <c>Escalated = top &lt; acceptAt || (top - second) &lt; minMargin</c>, where <c>(top, second)</c> is
/// <see cref="DecisionProbability.TopTwo"/> — two <i>scalar</i> probabilities, the top one and its
/// runner-up.
/// </item>
/// <item>
/// <b>The <c>ReviewLoop</c> site's real rule</b> — <c>ReviewLoopStepExecutor.cs:424-431</c>, after the
/// sum at <c>:445-467</c>:
/// <c>Escalate if (top - second) &lt; BimodalMargin || SumProbabilityAtOrAbove(levels >= MinScore) &lt; AcceptAt</c>.
/// The second term is a <b>sum over every rubric level at or above <c>MinScore</c></b> — a different
/// reduction of the same probability map, not a top probability.
/// </item>
/// </list>
///
/// <para>
/// <b>Findings, as executed below.</b>
/// </para>
/// <list type="bullet">
/// <item>
/// The row the ticket prescribes (mass peaking on level <c>6</c>, everything <c>&gt;= 9</c> at zero,
/// <c>top = 0.60</c>) <b>AGREES</b>: both rules escalate it, because at the shipped <c>AcceptAt = 0.85</c>
/// the view's <c>top &lt; acceptAt</c> term already fires on its own. That single example therefore
/// does <b>not</b> demonstrate a divergence.
/// </item>
/// <item>
/// A divergence <b>does</b> exist, and it needs only that the row be confident <i>about a level the
/// reviewer would fail</i>: with <c>top = 0.90</c> on level <c>6</c> (<c>second = 0.06</c>, margin
/// <c>0.84</c>, <c>pAtOrAbove = 0.00</c>) the view computes <c>Escalated = false</c> — it calls the row
/// <b>accepted</b> — while <c>Evaluate</c> returns <see cref="ReviewLoopGateDecision.Escalate"/>.
/// The gate's own generic <c>DecisionGateOutcome.Accepted</c> also says <c>true</c> on that same row,
/// and so does the <c>Escalated</c> column the gate persisted for it. Three of the four reductions
/// agree with each other and all three disagree with the rule the <c>ReviewLoop</c> step actually
/// applies.
/// </item>
/// <item>
/// The divergence is <b>site-specific</b>: <c>Colorist</c>/<c>MusicSupervisor</c> use
/// <c>DecisionGate.BuildOutcome</c>'s top/margin rule (<c>DecisionGate.cs:356</c>, consumed at
/// <c>AgentStepExecutor.cs:551</c>) and the colour-grade room uses <c>outcome.Accepted</c>
/// (<c>RoomStepExecutorBase.cs:1064</c>) — the view's exact reduction — so those series are
/// faithful. <c>Conditional</c> is a third case again: the view's recomputed value there is
/// precisely the gate's own <c>decision.escalated</c> flag (same <c>TopTwo</c> rule,
/// <c>DecisionGate.cs:239</c>), and it says nothing about whether the step took the True branch,
/// which is <c>pYes &gt;= Threshold</c> (<c>ConditionalStepExecutor.cs:260</c>).
/// </item>
/// <item>
/// <b>No workflow outcome can change.</b> <c>ReviewLoopStepExecutor</c> never reads
/// <c>outcome.Accepted</c> — it calls <c>Evaluate</c> (<c>:341</c>), and the divergence is in a
/// read-only aggregation service. What is affected is the number a threshold-tuning operator reads.
/// </item>
/// </list>
/// </summary>
public sealed class QaEscalationRuleAgreementTests
{
    /// <summary>The step-level default the executor itself applies: <c>context.Step.MinScore ?? 9</c>.</summary>
    private const int ShippedMinScore = 9;

    /// <summary>
    /// The shipped <c>ReviewLoopStepConfig</c> defaults, read from the record rather than retyped:
    /// <c>AcceptAt = 0.85</c>, <c>BimodalMargin = 0.15</c>. <c>Enabled</c> is false in every shipped
    /// configuration, which is irrelevant here — this test drives the gate directly.
    /// </summary>
    private static readonly ReviewLoopStepConfig Shipped = new();

    /// <summary>
    /// The <c>DecisionGateConfig</c> the <c>ReviewLoop</c> site builds for its own call
    /// (<c>ReviewLoopStepExecutor.cs:299-304</c>): note <c>MinMargin = BimodalMargin</c>, so the
    /// per-row <c>Escalated</c> column the gate writes for this site is scored on the bimodal margin.
    /// </summary>
    private static DecisionGateConfig ReviewLoopGateConfig() =>
        new(Mode: DecisionGateMode.Gate, AcceptAt: Shipped.AcceptAt, MinMargin: Shipped.BimodalMargin);

    /// <summary>The view's own query defaults, as <c>DecisionCalibrationController</c> defaults them.</summary>
    private const double ViewAcceptAt = 0.85;
    private const double ViewMinMargin = 0.25;

    private readonly ITestOutputHelper _output;

    public QaEscalationRuleAgreementTests(ITestOutputHelper output) => _output = output;

    // ---------------------------------------------------------------------
    // 1. The prescribed row: AGREE (at the shipped AcceptAt the top-based term fires anyway).
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ReviewLoop_row_peaking_below_MinScore_AGREES_both_rules_escalate_at_the_shipped_defaults()
    {
        using WorkflowEngineDbContext db = NewDb();

        // The ticket's prescribed row: mass peaks below MinScore — level "6" at 0.60, level "5" at
        // 0.20, every level >= 9 at 0.00. (Levels "7" at 0.15 and "4" at 0.05 carry the remainder so
        // the map is a real distribution summing to 1.00; neither can change the two rules.)
        var probabilities = new Dictionary<string, double>
        {
            ["6"] = 0.60,
            ["5"] = 0.20,
            ["7"] = 0.15,
            ["4"] = 0.05,
        };

        DecisionGateOutcome outcome = await RunGateAsync(
            db, ReviewLoopScoreQuestion(), ScoreAnswer(probabilities, score: 6), ReviewLoopGateConfig(), "ReviewLoop.step");

        // The site's REAL rule, executed through the executor's own evaluator.
        ReviewLoopGateEvaluation evaluation = ReviewLoopStepExecutor.Evaluate(
            outcome, ShippedMinScore, Shipped.AcceptAt, Shipped.BimodalMargin);

        // The view's RECOMPUTED rule, executed through the real service over the row the gate wrote.
        DecisionCalibrationSite site = await ReviewLoopSiteAsync(db);
        DecisionObservation row = await SingleRowAsync(db);

        bool gateEscalates = evaluation.Decision == ReviewLoopGateDecision.Escalate;
        bool viewEscalates = site.EscalationCount == 1;

        _output.WriteLine(
            $"[AGREE] prescribed row: top=0.60 second=0.20 pAtOrAbove(>=9)=0.00 "
            + $"acceptAt={Shipped.AcceptAt} bimodalMargin={Shipped.BimodalMargin} minScore={ShippedMinScore} "
            + $"viewAcceptAt={ViewAcceptAt} viewMinMargin={ViewMinMargin}");
        _output.WriteLine($"[AGREE] gateEscalates={gateEscalates} viewEscalates={viewEscalates}");

        // The numbers both rules were taken on.
        outcome.TopTwoProbabilities[ReviewLoopStepExecutor.GateQuestionName].Top.Should().Be(0.60);
        outcome.TopTwoProbabilities[ReviewLoopStepExecutor.GateQuestionName].Second.Should().Be(0.20);
        evaluation.PAtOrAbove.Should().Be(0.0);

        // Both rules escalate this row: they AGREE at the shipped thresholds.
        gateEscalates.Should().BeTrue();
        viewEscalates.Should().BeTrue();
        site.EscalationRate.Should().Be(1.0);

        // And the column the gate stored for it agrees too — this row is the easy case.
        row.Escalated.Should().BeTrue();
    }

    /// <summary>
    /// <b>DIVERGE.</b> A row the view counts as ACCEPTED — contributing to the "tokens saved"
    /// projection — while the site's real rule escalates it and pays for the reviewer.
    ///
    /// <para>
    /// The engine is only that the model is <i>confident about a level the reviewer would fail</i>:
    /// <c>top = 0.90</c> on level <c>6</c> clears <c>AcceptAt = 0.85</c> and the margin
    /// (<c>0.90 - 0.06 = 0.84</c>) clears <c>MinMargin = 0.25</c>, while
    /// <c>p(level &gt;= 9) = 0.00</c> cannot clear <c>AcceptAt</c> under the site's sum. A
    /// <i>deliberately</i> confident bad score is exactly the shape a fast model produces on the
    /// case it is least reliable about, which is why the site's rule sums instead of taking the top.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReviewLoop_row_confident_below_MinScore_DIVERGES_view_calls_it_accepted_while_the_gate_escalates()
    {
        using WorkflowEngineDbContext db = NewDb();

        var probabilities = new Dictionary<string, double>
        {
            ["6"] = 0.90,
            ["5"] = 0.06,
            ["7"] = 0.04,
        };

        DecisionGateOutcome outcome = await RunGateAsync(
            db, ReviewLoopScoreQuestion(), ScoreAnswer(probabilities, score: 6), ReviewLoopGateConfig(), "ReviewLoop.step",
            agentTokensUsed: 1200);

        ReviewLoopGateEvaluation evaluation = ReviewLoopStepExecutor.Evaluate(
            outcome, ShippedMinScore, Shipped.AcceptAt, Shipped.BimodalMargin);

        DecisionCalibrationSite site = await ReviewLoopSiteAsync(db);
        DecisionObservation row = await SingleRowAsync(db);

        (double top, double second) = outcome.TopTwoProbabilities[ReviewLoopStepExecutor.GateQuestionName];

        bool gateEscalates = evaluation.Decision == ReviewLoopGateDecision.Escalate;
        bool viewEscalates = site.EscalationCount == 1;

        _output.WriteLine(
            $"[DIVERGE] top={top} second={second} pAtOrAbove(>=9)={evaluation.PAtOrAbove} "
            + $"acceptAt={Shipped.AcceptAt} minMargin={ViewMinMargin} viewAcceptAt={ViewAcceptAt} minScore={ShippedMinScore}");
        _output.WriteLine(
            $"[DIVERGE] gateEscalates={gateEscalates} viewEscalates={viewEscalates} "
            + $"gateOutcomeAccepted={outcome.Accepted} storedEscalatedColumn={row.Escalated}");

        // The exact numbers, quoted: top 0.90 >= acceptAt 0.85, margin 0.84 >= minMargin 0.25,
        // but pAtOrAbove 0.00 < acceptAt 0.85.
        top.Should().Be(0.90);
        second.Should().Be(0.06);
        evaluation.PAtOrAbove.Should().Be(0.0);

        // The site's real rule escalates ...
        gateEscalates.Should().BeTrue();

        // ... and the view's recomputed rule does NOT: the site reports zero escalations for a row
        // its own gate escalates, so the site's EscalationRate and the tokens-saved projection are
        // computed over a rule this site does not use.
        viewEscalates.Should().BeFalse();
        site.EscalationCount.Should().Be(0);
        site.EscalationRate.Should().Be(0.0);

        // Three reductions agree with each other and all three disagree with the rule actually
        // applied: the gate's own step-level Accepted, the Escalated column the gate persisted, and
        // the view's recomputation. The workflow is unaffected only because
        // ReviewLoopStepExecutor never reads outcome.Accepted — it calls Evaluate (:341).
        outcome.Accepted.Should().BeTrue();
        row.Escalated.Should().BeFalse();

        // The projection counts this row as a saved reviewer run. The reviewer would have run.
        double tokensSavedForThisRow = 1200 - 40 - 10;
        tokensSavedForThisRow.Should().Be(1150);
    }

    // ---------------------------------------------------------------------
    // 2. Site-specificity: the Colorist/MusicSupervisor site's real rule IS top/margin → AGREE.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The same two-rule comparison at the <c>Colorist</c>/<c>MusicSupervisor</c> site, where the real
    /// rule <i>is</i> the top/margin rule — <c>DecisionGate.BuildOutcome</c> (<c>DecisionGate.cs:356</c>),
    /// consumed as step-level all-or-nothing at <c>AgentStepExecutor.cs:551</c>. Both directions are
    /// checked: a confident row (both accept) and a split row (both escalate). This is what makes the
    /// <c>ReviewLoop</c> finding site-specific rather than a blanket claim about the view.
    /// </summary>
    [Fact]
    public async Task Colorist_choice_rows_AGREE_both_directions_because_this_site_really_does_use_top_and_margin()
    {
        // One row per database so the view's recomputed verdict is readable per row: the service
        // aggregates by (Site, Question), so two rows in one store would only give a count.
        using WorkflowEngineDbContext confidentDb = NewDb();
        using WorkflowEngineDbContext splitDb = NewDb();

        DecisionGateConfig config = new(Mode: DecisionGateMode.Gate); // 0.85 / 0.25 — this site's own defaults

        DecisionGateOutcome confident = await RunGateAsync(
            confidentDb,
            ColoristLookQuestion(),
            ChoiceAnswer("Look", "None", new Dictionary<string, double> { ["None"] = 0.90, ["Warm"] = 0.10 }),
            config,
            "Colorist.step");

        DecisionGateOutcome split = await RunGateAsync(
            splitDb,
            ColoristLookQuestion(),
            ChoiceAnswer("Look", "None", new Dictionary<string, double> { ["None"] = 0.60, ["Warm"] = 0.40 }),
            config,
            "Colorist.step");

        DecisionCalibrationSite confidentSite = await SiteAsync(confidentDb, "Colorist.Look", "Look");
        DecisionCalibrationSite splitSite = await SiteAsync(splitDb, "Colorist.Look", "Look");

        _output.WriteLine(
            $"[AGREE] Colorist.Look confident row: top=0.90 margin=0.80 gateAccepted={confident.Accepted} "
            + $"viewEscalated={confidentSite.EscalationCount == 1}");
        _output.WriteLine(
            $"[AGREE] Colorist.Look split row: top=0.60 margin=0.20 gateAccepted={split.Accepted} "
            + $"viewEscalated={splitSite.EscalationCount == 1}");

        // Confident row: top 0.90 >= 0.85 and margin 0.80 >= 0.25 → the gate accepts it, and the
        // view's recomputed rule does not escalate it. AGREE.
        confident.Accepted.Should().BeTrue();
        confidentSite.ObservationCount.Should().Be(1);
        confidentSite.EscalationCount.Should().Be(0);
        confidentSite.EscalationRate.Should().Be(0.0);

        // Split row: margin 0.20 < 0.25 → the gate escalates it, and the view does too. AGREE.
        split.Accepted.Should().BeFalse();
        splitSite.ObservationCount.Should().Be(1);
        splitSite.EscalationCount.Should().Be(1);
        splitSite.EscalationRate.Should().Be(1.0);
    }

    // ---------------------------------------------------------------------
    // 3. The Conditional site: the view's value IS decision.escalated, and says nothing about the branch.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The <c>Conditional</c> site. Its real decision is <c>pYes &gt;= Threshold</c>
    /// (<c>ConditionalStepExecutor.cs:260</c>), a <b>different</b> rule from the gate's accept rule —
    /// <c>AcceptAt</c>/<c>MinMargin</c> answer only the separate <c>decision.escalated</c> parameter
    /// (<c>:255</c>, <c>:273</c>), which is the gate's top/margin rule verbatim.
    ///
    /// <para>
    /// So the view's recomputed value for a <c>Conditional.Decision</c> row is <b>exactly</b> the
    /// gate's trust flag on that row — faithful, but not the branch. Both rows below are
    /// <i>not</i> escalated while the step takes the True branch on one and the False branch on the
    /// other, which is what makes the site's escalation rate useless as a proxy for "how often this
    /// conditional fired". The <c>Noul</c> values 1.0 and 0.0 are chosen so the persisted
    /// <c>Answer</c> round-trips exactly under any culture.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Conditional_noul_rows_the_recomputed_rule_is_decision_escalated_not_the_Threshold_branch()
    {
        using WorkflowEngineDbContext db = NewDb();

        DecisionGateConfig config = new(Mode: DecisionGateMode.Gate); // AcceptAt/MinMargin defaults — NOT Threshold

        DecisionGateOutcome yes = await RunGateAsync(
            db, ConditionalQuestion(), NoulAnswer(1.0), config, "Conditional.Decision");
        DecisionGateOutcome no = await RunGateAsync(
            db, ConditionalQuestion(), NoulAnswer(0.0), config, "Conditional.Decision");

        DecisionCalibrationSite site = await SiteAsync(db, "Conditional.Decision", "Conditional.Threshold");

        double threshold = new ConditionalStepConfig().Threshold; // 0.5

        // The step's REAL rule, applied to the answers the provider returned.
        bool yesBranch = 1.0 >= threshold;
        bool noBranch = 0.0 >= threshold;

        // The gate's own trust flag, which is what the view recomputes.
        bool yesEscalated = !yes.Accepted;
        bool noEscalated = !no.Accepted;

        _output.WriteLine(
            $"[INFO] Conditional pYes=1.0: branch(pYes>=threshold {threshold})={yesBranch} "
            + $"decision.escalated={yesEscalated}");
        _output.WriteLine(
            $"[INFO] Conditional pYes=0.0: branch(pYes>=threshold {threshold})={noBranch} "
            + $"decision.escalated={noEscalated}");
        _output.WriteLine(
            $"[INFO] view escalation rate (recomputed)={site.EscalationRate} vs True-branch rate=0.5");

        // Noul(1.0) → the two-point set {1.0, 0.0}: top 1.0, margin 1.0 → trusted, not escalated.
        yesEscalated.Should().BeFalse();
        yesBranch.Should().BeTrue();

        // Noul(0.0) → DecisionProbability.TopTwo reads it as {0.0, 1.0}, i.e. (top 1.0, second 0.0):
        // the documented zero-information quirk. Still not escalated — while the step takes the
        // FALSE branch. A row the view reports as "accepted" here means "the gate trusted the
        // answer", never "the step took the True branch".
        noEscalated.Should().BeFalse();
        noBranch.Should().BeFalse();

        // The view recomputes the gate's trust flag exactly: 2 rows, 0 escalations, at the view's
        // thresholds AND at the gate's (both 0.85/0.25 for this site).
        site.ObservationCount.Should().Be(2);
        site.EscalationCount.Should().Be(0);
        site.EscalationRate.Should().Be(0.0);
    }

    // ---------------------------------------------------------------------
    // 4. The impact, quantified on one site.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Four <c>ReviewLoop.Score</c> rows through the same gate, then both aggregations over them:
    /// the site's recomputed escalation rate and the rate the step's own rule produces.
    ///
    /// <list type="bullet">
    /// <item>"6" 0.60 / "5" 0.20 / "7" 0.15 / "4" 0.05 — top 0.60, margin 0.40, pAtOrAbove 0.00 → both escalate.</item>
    /// <item>"6" 0.90 / "5" 0.06 / "7" 0.04 — top 0.90, margin 0.84, pAtOrAbove 0.00 → <b>only the gate escalates</b>.</item>
    /// <item>"9" 0.92 / "8" 0.05 / "10" 0.03 — top 0.92, margin 0.87, pAtOrAbove 0.95 → both accept.</item>
    /// <item>"9" 0.50 / "8" 0.45 / "10" 0.05 — top 0.50, margin 0.05, pAtOrAbove 0.55 → both escalate.</item>
    /// </list>
    ///
    /// <para>
    /// The report's <c>EscalationRate</c> for the site is <b>0.50</b>; the step's real rule escalates
    /// <b>0.75</b> of the same four rows. And the "tokens saved" projection includes row two's
    /// <c>1200 - 40 - 10 = 1150</c> tokens as a saved reviewer run — a run the gate would have paid
    /// for.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReviewLoop_site_escalation_rate_and_tokens_saved_understate_the_gates_own_behaviour_on_the_same_rows()
    {
        using WorkflowEngineDbContext db = NewDb();

        Dictionary<string, double>[] rows =
        [
            new() { ["6"] = 0.60, ["5"] = 0.20, ["7"] = 0.15, ["4"] = 0.05 },
            new() { ["6"] = 0.90, ["5"] = 0.06, ["7"] = 0.04 },
            new() { ["9"] = 0.92, ["8"] = 0.05, ["10"] = 0.03 },
            new() { ["9"] = 0.50, ["8"] = 0.45, ["10"] = 0.05 },
        ];

        var gateEscalations = new List<bool>();

        for (int i = 0; i < rows.Length; i++)
        {
            DecisionGateOutcome outcome = await RunGateAsync(
                db,
                ReviewLoopScoreQuestion(),
                ScoreAnswer(rows[i], TopLevel(rows[i])),
                ReviewLoopGateConfig(),
                "ReviewLoop.step",
                // Only row two (index 1) carries a counterfactual, so the projection is unambiguous.
                agentTokensUsed: i == 1 ? 1200 : null);

            gateEscalations.Add(
                ReviewLoopStepExecutor.Evaluate(outcome, ShippedMinScore, Shipped.AcceptAt, Shipped.BimodalMargin).Decision
                == ReviewLoopGateDecision.Escalate);
        }

        DecisionCalibrationReport report = await GetReportAsync(db, "ReviewLoop.Score");
        DecisionCalibrationSite site = report.Sites.Single(s => s.Site == "ReviewLoop.Score");

        double gateRate = gateEscalations.Count(e => e) / (double)gateEscalations.Count;
        double viewRate = site.EscalationRate;

        _output.WriteLine($"[IMPACT] rows={gateEscalations.Count} gateEscalations={string.Join(",", gateEscalations)}");
        _output.WriteLine($"[IMPACT] site recomputed escalation rate (view)={viewRate} vs gate's own rule={gateRate}");
        _output.WriteLine($"[IMPACT] report Totals.TokensSaved={report.Totals.TokensSaved} TokensSavedIsProjected={report.Totals.TokensSavedIsProjected}");

        site.ObservationCount.Should().Be(4);
        gateEscalations.Should().Equal(true, true, false, true);

        viewRate.Should().Be(0.5);
        gateRate.Should().Be(0.75);

        // Row two is the one the two rules disagree about, and it is inside the projection.
        report.Totals.TokensSavedIsProjected.Should().BeTrue();
        report.Totals.TokensSaved.Should().Be(1150);
    }

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private static WorkflowEngineDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"QaEscalationRuleAgreementTests_{Guid.NewGuid()}")
            .Options);

    /// <summary>
    /// Runs the real <see cref="DecisionGate"/> in Gate mode against a stubbed Decision client, so
    /// the observation row under test is the one production actually writes and the returned
    /// <see cref="DecisionGateOutcome"/> is the one a step executor actually receives — the same
    /// inputs through both rules, no hand-built rows.
    /// </summary>
    private static async Task<DecisionGateOutcome> RunGateAsync(
        WorkflowEngineDbContext db,
        DecisionGateQuestion question,
        DecisionAnswer answer,
        DecisionGateConfig config,
        string requestSite,
        int? agentTokensUsed = null,
        AgentType agentType = AgentType.ColorGradeDirector)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedDecisionProvider(
                ProviderId: Guid.NewGuid(),
                Name: "qa-provider",
                Kind: InferenceProviderKind.OpenAICompatible,
                Endpoint: "http://qa",
                ModelName: "qa-model",
                ApiKey: "qa-key",
                TimeoutSeconds: 300));

        var factory = new Mock<IDecisionClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>()))
            .Returns(new StubDecisionClient(answer));

        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(db);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        var gate = new DecisionGate(
            resolver.Object, factory.Object, scopeFactory.Object, new Mock<ILogger<DecisionGate>>().Object);

        return await gate.DecideAsync(
            new DecisionGateRequest(
                ExecutionId: Guid.NewGuid(),
                StepOrder: 3,
                AgentType: agentType,
                Site: requestSite,
                State: new DecisionState("qa state"),
                Questions: [question],
                Config: config,
                SystemTwoAnswer: null,
                AgentTokensUsed: agentTokensUsed),
            CancellationToken.None);
    }

    /// <summary>The <c>Score</c> question the <c>ReviewLoop</c> early stop poses, rubric and all.</summary>
    private static DecisionGateQuestion ReviewLoopScoreQuestion() =>
        new(
            Site: ReviewLoopStepExecutor.GateSite,
            Name: ReviewLoopStepExecutor.GateQuestionName,
            Kind: DecisionKind.Score,
            Instructions: "QA: score the edit 0..10.",
            Options: ReviewLoopStepExecutor.RubricLevels(),
            SystemTwoAnswer: null);

    private static DecisionGateQuestion ColoristLookQuestion() =>
        new(
            Site: "Colorist.Look",
            Name: "Look",
            Kind: DecisionKind.Choice,
            Instructions: "QA: whole-program colour-grade look.",
            Options: ["None", "Warm", "Cool", "Filmic", "Vibrant", "Muted", "Mono"],
            SystemTwoAnswer: null);

    private static DecisionGateQuestion ConditionalQuestion() =>
        new(
            Site: "Conditional.Decision",
            Name: "Conditional.Threshold",
            Kind: DecisionKind.Noul,
            Instructions: "QA: does the state satisfy the condition?",
            Options: [],
            SystemTwoAnswer: null);

    private static DecisionAnswer ScoreAnswer(IReadOnlyDictionary<string, double> probabilities, int score) =>
        new(
            Name: ReviewLoopStepExecutor.GateQuestionName,
            Kind: DecisionKind.Score,
            Choice: null,
            Score: score,
            Noul: null,
            Confidence: null,
            Probabilities: probabilities);

    private static DecisionAnswer ChoiceAnswer(string name, string choice, IReadOnlyDictionary<string, double> probabilities) =>
        new(Name: name, Kind: DecisionKind.Choice, Choice: choice, Score: null, Noul: null, Confidence: null, Probabilities: probabilities);

    /// <summary>
    /// A <c>Noul</c> answer: its whole distribution is the single <c>Noul</c> number, and
    /// <c>Probabilities</c> is empty by construction — which is exactly why the view's
    /// <see cref="DecisionProbability.TopTwo"/> reads it as the two-point set <c>{p, 1-p}</c>.
    /// </summary>
    private static DecisionAnswer NoulAnswer(double pYes) =>
        new(
            Name: "Conditional.Threshold",
            Kind: DecisionKind.Noul,
            Choice: null,
            Score: null,
            Noul: pYes,
            Confidence: null,
            Probabilities: new Dictionary<string, double>());

    private static Task<DecisionCalibrationReport> GetReportAsync(WorkflowEngineDbContext db, string site) =>
        new DecisionCalibrationService(db).GetCalibrationAsync(ViewAcceptAt, ViewMinMargin, null, site, CancellationToken.None);

    private static async Task<DecisionCalibrationSite> ReviewLoopSiteAsync(WorkflowEngineDbContext db) =>
        (await GetReportAsync(db, "ReviewLoop.Score")).Sites.Single(s => s.Site == "ReviewLoop.Score");

    private static async Task<DecisionCalibrationSite> SiteAsync(WorkflowEngineDbContext db, string site, string question) =>
        (await GetReportAsync(db, site)).Sites.Single(s => s.Site == site && s.Question == question);

    private static Task<DecisionObservation> SingleRowAsync(WorkflowEngineDbContext db) =>
        db.DecisionObservations.AsNoTracking().SingleAsync();

    /// <summary>The rubric level the row's own mass peaks on — what a <c>Score</c> answer would name.</summary>
    private static int TopLevel(IReadOnlyDictionary<string, double> probabilities) =>
        int.Parse(probabilities.OrderByDescending(p => p.Value).First().Key);

    /// <summary>
    /// A provider stub returning one canned answer — the Decision provider is a hosted third party,
    /// and what this ticket compares is what the host does with an answer, not how it is fetched.
    /// </summary>
    private sealed class StubDecisionClient(DecisionAnswer answer) : IDecisionClient
    {
        public Task<DecisionResult> DecideAsync(
            DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct) =>
            Task.FromResult(new DecisionResult(
                Model: "qa-model",
                Answers: new Dictionary<string, DecisionAnswer> { [answer.Name] = answer },
                InputTokens: 40,
                OutputTokens: 10));
    }
}