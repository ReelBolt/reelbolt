using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Decision;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-017 — <b>for every decision site, the executor's real accept decision and the calibration
/// aggregation's escalation figure agree over the same persisted row.</b>
///
/// <para>
/// This is the test whose absence let the defect through, and it is deliberately not a comparison of
/// two hand-computed booleans. Every case below runs the site's <b>real</b> executor (or, for the
/// agent-step sites, the real rule that executor consumes) against a <b>real</b>
/// <see cref="DecisionGate"/> writing into an in-memory <see cref="WorkflowEngineDbContext"/>, and
/// then reads the escalation figure out of the <b>real</b> <see cref="DecisionCalibrationService"/>
/// over the row that gate wrote. Both sides therefore see one artefact — the row production actually
/// persists — rather than two reconstructions of it.
/// </para>
///
/// <para>
/// <b>What "the executor's real accept decision" is observed as, per site:</b>
/// </para>
/// <list type="bullet">
/// <item><c>ReviewLoop.Score</c> — whether the reviewer agent actually ran. Escalating is the branch
/// that runs it, so <c>IReelBoltAgent.RunAsync</c> being invoked <i>is</i> the site's accept
/// decision, read off the executor's behaviour rather than off a flag. (The step reports its
/// decision through <c>ReviewLoopStepExecutor.Evaluate</c>, which is driven here too.)</item>
/// <item><c>Conditional.Decision</c> — which branch the step took, with the step's own
/// <c>ConditionExpression</c> set to <c>decision.escalated</c>: the branch is then literally the
/// accept decision the site publishes, so <c>NextStepIndex</c> answers the question end to end.</item>
/// <item><c>Colorist</c>/<c>MusicSupervisor</c> and the colour-grade room — <c>DecisionGateOutcome.Accepted</c>,
/// which is what those call sites consume (the room's convening gate acts on it directly).</item>
/// </list>
///
/// <para>
/// <b>Thresholds.</b> Escalation is recomputed by the view at the thresholds the operator
/// <i>supplies</i>, so an exact parity claim is a claim at the site's own thresholds — which is what
/// every case here supplies. That is the point of the fix: the view still answers "what would this
/// threshold have done?", and it now answers it with the site's rule instead of a universal one.
/// </para>
/// </summary>
public sealed class DecisionCalibrationEscalationParityTests
{
    /// <summary>The step-level default the executor applies: <c>context.Step.MinScore ?? 9</c>.</summary>
    private const int ShippedMinScore = 9;

    /// <summary>The shipped <c>ReviewLoopStepConfig</c>, read from the record rather than retyped.</summary>
    private static readonly ReviewLoopStepConfig ShippedReviewLoop = new();

    /// <summary>The shipped <c>ConditionalStepConfig</c> defaults, likewise.</summary>
    private static readonly ConditionalStepConfig ShippedConditional = new();

    /// <summary>The view's own query defaults, as <c>DecisionCalibrationController</c> defaults them.</summary>
    private const double ViewAcceptAt = 0.85;
    private const double ViewMinMargin = 0.25;

    private readonly ITestOutputHelper _output;

    public DecisionCalibrationEscalationParityTests(ITestOutputHelper output) => _output = output;

    // =====================================================================
    // ReviewLoop.Score — AtOrAbove with the step's MinScore
    // =====================================================================

    /// <summary>
    /// The whole corpus, one row per fresh store, both sides driven over the same persisted row. The
    /// reviewer running and the view counting an escalation are the same event for a
    /// <c>ReviewLoop.Score</c> row.
    /// </summary>
    [Theory]
    [InlineData("6", 0.60, true)]
    [InlineData("6", 0.90, true)]
    [InlineData("9", 0.92, false)]
    [InlineData("9", 0.50, true)]
    public async Task ReviewLoop_parity_the_reviewer_running_and_the_views_escalation_agree(
        string peakLevel, double peak, bool expectEscalated)
    {
        DbContextOptions<WorkflowEngineDbContext> options = NewStore();

        Dictionary<string, double> probabilities = ReviewLoopCorpusRow(peakLevel, peak);

        bool reviewerRan = await RunReviewLoopAsync(options, probabilities, ShippedMinScore);

        DecisionCalibrationSite site = await ReviewLoopSiteAsync(options, ShippedReviewLoop.AcceptAt, ShippedReviewLoop.BimodalMargin);

        _output.WriteLine(
            $"[PARITY] ReviewLoop.Score peak={peakLevel}@{peak} reviewerRan={reviewerRan} "
            + $"viewEscalationCount={site.EscalationCount} viewEscalationRate={site.EscalationRate}");

        site.ObservationCount.Should().Be(1);
        reviewerRan.Should().Be(expectEscalated);
        site.EscalationCount.Should().Be(expectEscalated ? 1 : 0);

        // The claim, stated once and directly: the site's behaviour and the view's number agree.
        reviewerRan.Should().Be(site.EscalationCount == 1);
    }

    /// <summary>
    /// QA's named case, verbatim: <c>top = 0.90, second = 0.06, pAtOrAbove = 0.00, acceptAt = 0.85</c>
    /// on a <c>ReviewLoop.Score</c> row. The executor's <c>Evaluate</c> returns
    /// <see cref="ReviewLoopGateDecision.Escalate"/> <b>and</b> the aggregation counts that row as
    /// escalated — where the universal top/margin rule counted it accepted, which is the defect this
    /// ticket exists to close.
    /// </summary>
    [Fact]
    public async Task Qa_named_case_the_executor_escalates_and_the_aggregation_counts_it_as_escalated()
    {
        DbContextOptions<WorkflowEngineDbContext> options = NewStore();

        var probabilities = new Dictionary<string, double> { ["6"] = 0.90, ["5"] = 0.06, ["7"] = 0.04 };

        (DecisionsResult run, DecisionGateOutcome outcome) = await RunReviewLoopCapturingAsync(
            options, probabilities, ShippedMinScore);

        ReviewLoopGateEvaluation evaluation = ReviewLoopStepExecutor.Evaluate(
            outcome, ShippedMinScore, ShippedReviewLoop.AcceptAt, ShippedReviewLoop.BimodalMargin);

        DecisionCalibrationSite site = await ReviewLoopSiteAsync(
            options, ShippedReviewLoop.AcceptAt, ShippedReviewLoop.BimodalMargin);

        (double top, double second) = outcome.TopTwoProbabilities[ReviewLoopStepExecutor.GateQuestionName];

        _output.WriteLine(
            $"[QA CASE] top={top} second={second} pAtOrAbove(>=9)={evaluation.PAtOrAbove} "
            + $"acceptAt={ShippedReviewLoop.AcceptAt} bimodalMargin={ShippedReviewLoop.BimodalMargin}");

        top.Should().Be(0.90);
        second.Should().Be(0.06);
        evaluation.PAtOrAbove.Should().Be(0.0);

        // The site escalates ...
        evaluation.Decision.Should().Be(ReviewLoopGateDecision.Escalate);
        run.ReviewerRan.Should().BeTrue();

        // ... and now so does the aggregation, over the row that same run persisted.
        site.EscalationCount.Should().Be(1);
        site.EscalationRate.Should().Be(1.0);

        // The stored basis the recomputation was made from, on the row itself.
        DecisionObservation row = await SingleRowAsync(options);
        row.AcceptMetric.Should().Be(nameof(DecisionAcceptMetric.AtOrAbove));
        row.ScoreCutoff.Should().Be(ShippedMinScore);
        row.Escalated.Should().BeTrue("the gate's own stored flag is now computed with the site's rule");
    }

    /// <summary>
    /// The impact, quantified: QA's four-row corpus through the executor, then the aggregation over
    /// the rows those runs persisted. The view reports <b>0.75</b> — the rate the step's own rule
    /// produces — where the universal top/margin rule reported <b>0.50</b>. Both numbers are computed
    /// below from the same four rows, the second by asking the shared rule for the
    /// <see cref="DecisionAcceptMetric.Top"/> reading the old aggregation would have taken.
    /// </summary>
    [Fact]
    public async Task ReviewLoop_four_row_corpus_escalation_rate_is_075_not_the_universal_rules_050()
    {
        DbContextOptions<WorkflowEngineDbContext> options = NewStore();

        Dictionary<string, double>[] corpus =
        [
            new() { ["6"] = 0.60, ["5"] = 0.20, ["7"] = 0.15, ["4"] = 0.05 },
            new() { ["6"] = 0.90, ["5"] = 0.06, ["7"] = 0.04 },
            new() { ["9"] = 0.92, ["8"] = 0.05, ["10"] = 0.03 },
            new() { ["9"] = 0.50, ["8"] = 0.45, ["10"] = 0.05 },
        ];

        int escalations = 0;
        foreach (Dictionary<string, double> probabilities in corpus)
        {
            if (await RunReviewLoopAsync(options, probabilities, ShippedMinScore))
                escalations++;
        }

        DecisionCalibrationSite site = await ReviewLoopSiteAsync(
            options, ShippedReviewLoop.AcceptAt, ShippedReviewLoop.BimodalMargin);

        // The rule the view applied before this ticket: the same rows, read as Top.
        int universalEscalations = corpus.Count(p => DecisionAcceptRule.Escalates(
            DecisionAcceptMetric.Top, null, DecisionKind.Score, p, null, ShippedReviewLoop.AcceptAt, ShippedReviewLoop.BimodalMargin));

        _output.WriteLine(
            $"[CORPUS] rows=4 reviewerRuns={escalations} siteRate={site.EscalationRate} "
            + $"universalRate={universalEscalations / 4.0}");

        site.ObservationCount.Should().Be(4);
        escalations.Should().Be(3);
        site.EscalationCount.Should().Be(3);
        site.EscalationRate.Should().Be(0.75);

        // And the number it replaced, over the identical four rows.
        universalEscalations.Should().Be(2, "the universal rule accepted the confident-but-low row the site rejects");

        // The view's own shipped query defaults still produce the same corrected figure for this
        // corpus: no row's verdict here turns on the 0.15-vs-0.25 margin difference.
        DecisionCalibrationSite atViewDefaults = await ReviewLoopSiteAsync(options, ViewAcceptAt, ViewMinMargin);
        atViewDefaults.EscalationRate.Should().Be(0.75);
    }

    // =====================================================================
    // Conditional.Decision — Affirmative
    // =====================================================================

    /// <summary>
    /// The <c>Conditional</c> site, end to end. The step's expression is <c>decision.escalated</c>,
    /// so the branch it takes IS the accept decision the site publishes; the view must report the same
    /// verdict over the row the same run wrote.
    /// </summary>
    [Theory]
    [InlineData(1.0, false)]
    [InlineData(0.9, false)]
    [InlineData(0.10, true)]
    [InlineData(0.0, true)]
    public async Task Conditional_parity_the_branch_the_step_takes_and_the_views_escalation_agree(
        double pYes, bool expectEscalated)
    {
        DbContextOptions<WorkflowEngineDbContext> options = NewStore();

        StepExecutionResult result = await RunConditionalAsync(options, pYes);

        DecisionCalibrationSite site = await SiteAsync(options, ConditionalStepExecutor.DecisionSite, QuestionName, ViewAcceptAt, ViewMinMargin);

        _output.WriteLine(
            $"[PARITY] Conditional p_yes={pYes} branch=escalated:{expectEscalated} NextStepIndex={result.NextStepIndex} "
            + $"viewEscalationCount={site.EscalationCount}");

        // The step's own accept decision, read off the branch it took.
        bool escalated = result.NextStepIndex == 1;
        escalated.Should().Be(expectEscalated);
        site.EscalationCount.Should().Be(escalated ? 1 : 0);
        escalated.Should().Be(site.EscalationCount == 1);
    }

    /// <summary>
    /// The <c>Affirmative</c> reading, stated as the defect it closes: with <c>p_yes = 0.10</c> the
    /// row's <b>top</b> is <c>0.90</c>, so a reader applying the universal rule counts it accepted
    /// while the step itself rejects the condition. The row is counted as escalated now.
    /// </summary>
    [Fact]
    public async Task An_affirmative_row_with_p_yes_010_is_counted_as_escalated_even_though_its_top_is_090()
    {
        DbContextOptions<WorkflowEngineDbContext> options = NewStore();

        await RunConditionalAsync(options, pYes: 0.10);

        DecisionCalibrationSite site = await SiteAsync(options, ConditionalStepExecutor.DecisionSite, QuestionName, ViewAcceptAt, ViewMinMargin);
        DecisionObservation row = await SingleRowAsync(options);

        var answer = new DecisionAnswer(QuestionName, DecisionKind.Noul, null, null, 0.10, null, new Dictionary<string, double>());

        _output.WriteLine(
            $"[AFFIRMATIVE] storeMetric={row.AcceptMetric} storeCutoff={row.ScoreCutoff} "
            + $"top={DecisionProbability.TopTwo(answer).Top} viewEscalationCount={site.EscalationCount}");

        row.AcceptMetric.Should().Be(nameof(DecisionAcceptMetric.Affirmative));
        row.ScoreCutoff.Should().BeNull("the affirmative comparison needs no cutoff");

        // The number whose misreading caused the defect.
        DecisionProbability.TopTwo(answer).Top.Should().Be(0.90);

        // The universal rule would have called this row accepted ...
        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Top, null, DecisionKind.Noul, new Dictionary<string, double>(), 0.10, ViewAcceptAt, ViewMinMargin)
            .Should().BeFalse();

        // ... and the site's own rule, which the view now applies, escalates it.
        site.EscalationCount.Should().Be(1);
        site.EscalationRate.Should().Be(1.0);
    }

    // =====================================================================
    // Colorist / MusicSupervisor / the colour-grade room — Top, unaffected
    // =====================================================================

    /// <summary>
    /// The sites that really do use top/margin — the <c>Colorist</c> and <c>MusicSupervisor</c> agent
    /// steps and the colour-grade room, whose convening gate acts on <c>outcome.Accepted</c>. Driven
    /// both ways: a confident row both sides accept, a split row both sides escalate. Their rows carry
    /// no metric declaration and must keep reading as <see cref="DecisionAcceptMetric.Top"/>.
    /// </summary>
    [Theory]
    [InlineData(0.90, 0.10, false)]
    [InlineData(0.60, 0.40, true)]
    [InlineData(0.80, 0.10, true)]
    public async Task Top_sites_parity_the_gates_own_accepted_flag_and_the_views_escalation_agree(
        double topProbability, double secondProbability, bool expectEscalated)
    {
        DbContextOptions<WorkflowEngineDbContext> options = NewStore();

        DecisionGateOutcome outcome = await RunGateAsync(
            options,
            ColoristLookQuestion(),
            ChoiceAnswer("Look", "None", new Dictionary<string, double> { ["None"] = topProbability, ["Warm"] = secondProbability }),
            new DecisionGateConfig(Mode: DecisionGateMode.Gate),
            "Colorist.step");

        DecisionCalibrationSite site = await SiteAsync(options, "Colorist.Look", "Look", ViewAcceptAt, ViewMinMargin);
        DecisionObservation row = await SingleRowAsync(options);

        _output.WriteLine(
            $"[PARITY] Colorist.Look top={topProbability} second={secondProbability} "
            + $"gateAccepted={outcome.Accepted} viewEscalated={site.EscalationCount == 1} storedMetric={row.AcceptMetric}");

        // What the agent step (and the room's convening gate) consumes.
        outcome.Accepted.Should().Be(!expectEscalated);
        site.EscalationCount.Should().Be(expectEscalated ? 1 : 0);
        (!outcome.Accepted).Should().Be(site.EscalationCount == 1);

        // A site that declared nothing is read, correctly, as Top — and the gate stored no basis for
        // it, so this is also the historic-row path.
        row.AcceptMetric.Should().Be(nameof(DecisionAcceptMetric.Top));
        row.ScoreCutoff.Should().BeNull();
    }

    // =====================================================================
    // Rows the aggregation must count and refuse to guess at
    // =====================================================================

    /// <summary>
    /// A row written before the metric column existed carries <c>null</c>, and null means Top — the
    /// rule that wrote it. Its escalation is therefore still recomputed exactly as phase 1 did it.
    /// </summary>
    [Fact]
    public async Task A_row_with_no_recorded_metric_is_read_as_Top()
    {
        DbContextOptions<WorkflowEngineDbContext> options = NewStore();

        await SeedAsync(options, new DecisionObservation
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            StepOrder = 3,
            AgentType = AgentType.Colorist,
            Site = "Colorist.step",
            Question = "look",
            Kind = DecisionKind.Choice,
            Answer = "None",
            ProbabilitiesJson = """{"None":0.90,"Warm":0.10}""",
            Mode = "Shadow",
            Escalated = false,
            AcceptMetric = null,
            ScoreCutoff = null,
        });

        DecisionCalibrationSite site = await SiteAsync(options, "Colorist.step", "look", ViewAcceptAt, ViewMinMargin);

        _output.WriteLine($"[HISTORIC] count={site.ObservationCount} escalations={site.EscalationCount}");

        site.ObservationCount.Should().Be(1);
        site.EscalationCount.Should().Be(0, "top 0.90 with a 0.80 margin clears both thresholds under Top");
    }

    /// <summary>
    /// <c>AtOrAbove</c> without the cutoff it sums from cannot be recomputed — and a row this service
    /// cannot recompute is <b>counted and excluded</b>, never guessed at and never thrown on. Note the
    /// deliberate asymmetry with <see cref="DecisionAcceptRule.Escalates"/>, which fails <i>closed</i>
    /// for the same inputs because an executor must run its agent rather than skip it.
    /// </summary>
    [Fact]
    public async Task An_AtOrAbove_row_with_a_null_cutoff_is_excluded_rather_than_guessed()
    {
        DbContextOptions<WorkflowEngineDbContext> options = NewStore();

        await SeedAsync(options, ScoreRow(acceptMetric: nameof(DecisionAcceptMetric.AtOrAbove), scoreCutoff: null));

        DecisionCalibrationSite site = await SiteAsync(options, "ReviewLoop.Score", "Score", ViewAcceptAt, ViewMinMargin);

        _output.WriteLine($"[EXCLUDED] AtOrAbove/null-cutoff count={site.ObservationCount} escalations={site.EscalationCount} rate={site.EscalationRate}");

        site.ObservationCount.Should().Be(1, "the row is counted");
        site.EscalationCount.Should().Be(0);
        site.EscalationRate.Should().Be(0.0, "the denominator is empty, not one and not a guess");
    }

    /// <summary>
    /// An unrecognised metric name is data, not a contract violation: it is counted and excluded, and
    /// it must never 500 the endpoint.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_metric_string_is_excluded_rather_than_thrown_on()
    {
        DbContextOptions<WorkflowEngineDbContext> options = NewStore();

        await SeedAsync(options, ScoreRow(acceptMetric: "SomethingFromTheFuture", scoreCutoff: 9));

        DecisionCalibrationSite site = await SiteAsync(options, "ReviewLoop.Score", "Score", ViewAcceptAt, ViewMinMargin);

        _output.WriteLine($"[EXCLUDED] unknown-metric count={site.ObservationCount} escalations={site.EscalationCount} rate={site.EscalationRate}");

        site.ObservationCount.Should().Be(1);
        site.EscalationCount.Should().Be(0);
        site.EscalationRate.Should().Be(0.0);
    }

    /// <summary>
    /// And the excluded row does not leak into the other figures either: it is still counted in
    /// <c>ObservationCount</c> and its own reliability-diagram bucket is untouched, while the
    /// <b>tokens-saved</b> projection — which infers "no agent ran" from acceptance — stays out of it,
    /// because this row has no known acceptance at all.
    /// </summary>
    [Fact]
    public async Task An_excluded_row_still_counts_and_plots_but_contributes_no_tokens_saved()
    {
        DbContextOptions<WorkflowEngineDbContext> options = NewStore();

        DecisionObservation row = ScoreRow(acceptMetric: "SomethingFromTheFuture", scoreCutoff: 9);
        row.AgentTokensUsed = 1200;
        row.DecisionInputTokens = 40;
        row.DecisionOutputTokens = 10;
        await SeedAsync(options, row);

        DecisionCalibrationReport report = await ReportAsync(options, ViewAcceptAt, ViewMinMargin, "ReviewLoop.Score");

        _output.WriteLine(
            $"[EXCLUDED] count={report.Totals.ObservationCount} tokensSaved={report.Totals.TokensSaved} "
            + $"bucketsWithMembers={report.Sites.Single().ReliabilityBuckets.Count(b => b.Count > 0)}");

        report.Totals.ObservationCount.Should().Be(1);
        report.Totals.TokensSaved.Should().Be(0);
        report.Sites.Single().ReliabilityBuckets.Count(b => b.Count > 0).Should().Be(1);
    }

    // =====================================================================
    // Harness — real gate, real store, real aggregation
    // =====================================================================

    private static DbContextOptions<WorkflowEngineDbContext> NewStore() =>
        new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"DecisionCalibrationEscalationParityTests_{Guid.NewGuid()}")
            .Options;

    /// <summary>The question the <c>Conditional</c> arm asks; fixed so the canned answer can name it.</summary>
    private const string QuestionName = "qa.conditional";

    /// <summary>
    /// Runs the real <see cref="DecisionGate"/> in Gate mode over <paramref name="options"/>, against
    /// a stubbed Decision client, so the observation row under test is the one production writes and
    /// the returned outcome is the one a step executor receives.
    /// </summary>
    private static async Task<DecisionGateOutcome> RunGateAsync(
        DbContextOptions<WorkflowEngineDbContext> options,
        DecisionGateQuestion question,
        DecisionAnswer answer,
        DecisionGateConfig config,
        string requestSite)
    {
        DecisionGate gate = CreateGate(options, new StubDecisionClient(new DecisionResult(
            Model: "qa-model",
            Answers: new Dictionary<string, DecisionAnswer> { [answer.Name] = answer },
            InputTokens: 40,
            OutputTokens: 10)));

        return await gate.DecideAsync(
            new DecisionGateRequest(
                ExecutionId: Guid.NewGuid(),
                StepOrder: 3,
                AgentType: AgentType.ColorGradeDirector,
                Site: requestSite,
                State: new DecisionState("qa state"),
                Questions: [question],
                Config: config),
            CancellationToken.None);
    }

    private static DecisionGate CreateGate(DbContextOptions<WorkflowEngineDbContext> options, IDecisionClient client)
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
        factory.Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>())).Returns(client);

        // A fresh context per scope: the gate opens one to write its observations and Attach opens its
        // own, and both must land in the same in-memory store.
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(sf => sf.CreateScope()).Returns(() =>
        {
            var scopeServiceProvider = new Mock<IServiceProvider>();
            scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext)))
                .Returns(() => (object?)new WorkflowEngineDbContext(options));

            var scope = new Mock<IServiceScope>();
            scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);
            return scope.Object;
        });

        return new DecisionGate(
            resolver.Object, factory.Object, scopeFactory.Object, NullLogger<DecisionGate>.Instance);
    }

    /// <summary>
    /// The states a <c>ReviewLoop</c> run walks through: the deterministic <c>VideoCompile</c> facts
    /// <c>BuildReviewFacts</c> projects, so the gate is genuinely asked rather than skipped for having
    /// nothing to ask over.
    /// </summary>
    private const string PriorCompileOutput =
        """
        {"status":"completed","segments":4,"outputDurationSec":12.5,"retainedRatio":0.61,
         "sentenceCheck":{"applicable":true,"endsAtSentenceBoundary":true},
         "graphics":{"applied":true,"appliedOverlayCount":1},
         "music":{"applied":true,"dialogueHeadroom":{"applicable":true,"headroomDb":4.5}}}
        """;

    /// <summary>
    /// Runs the real <see cref="ReviewLoopStepExecutor"/> end to end over a real gate and reports
    /// whether the reviewer agent actually ran — which is this site's accept decision, observed.
    /// </summary>
    private static async Task<bool> RunReviewLoopAsync(
        DbContextOptions<WorkflowEngineDbContext> options,
        IReadOnlyDictionary<string, double> probabilities,
        int minScore) =>
        (await RunReviewLoopCapturingAsync(options, probabilities, minScore)).Run.ReviewerRan;

    /// <summary>
    /// The same run, also handing back the real <see cref="DecisionGateOutcome"/> so a caller can
    /// drive <see cref="ReviewLoopStepExecutor.Evaluate"/> — the function the executor itself decides
    /// with — over the outcome that produced the persisted row.
    /// </summary>
    private static async Task<(DecisionsResult Run, DecisionGateOutcome Outcome)> RunReviewLoopCapturingAsync(
        DbContextOptions<WorkflowEngineDbContext> options,
        IReadOnlyDictionary<string, double> probabilities,
        int minScore)
    {
        var answer = new DecisionAnswer(
            Name: ReviewLoopStepExecutor.GateQuestionName,
            Kind: DecisionKind.Score,
            Choice: null,
            Score: TopLevel(probabilities),
            Noul: null,
            Confidence: probabilities.Values.Max(),
            Probabilities: probabilities);

        var result = new DecisionResult(
            "qa-model",
            new Dictionary<string, DecisionAnswer> { [ReviewLoopStepExecutor.GateQuestionName] = answer },
            InputTokens: 40,
            OutputTokens: 10);

        var captured = new CapturingGate(CreateGate(options, new StubDecisionClient(result)));

        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(AgentType.VideoReviewAgent.ToString());
        agent.Setup(a => a.AgentType).Returns(AgentType.VideoReviewAgent);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult
            {
                Output = """{"score":3,"passesReview":false,"summary":"qa"}""",
                Success = true,
                TokensUsed = 120,
                InputTokens = 90,
                OutputTokens = 30,
            });

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent.Object);

        var executor = new ReviewLoopStepExecutor(
            registry.Object, MockAccessor().Object, captured, NullLogger<ReviewLoopStepExecutor>.Instance);

        await executor.ExecuteAsync(CreateReviewLoopContext(minScore));

        bool reviewerRan = agent.Invocations.Any(i => i.Method.Name == nameof(IReelBoltAgent.RunAsync));

        return (
            new DecisionsResult(reviewerRan),
            captured.LastOutcome ?? throw new InvalidOperationException("the gate was never asked"));
    }

    /// <summary>
    /// Runs the real <see cref="ConditionalStepExecutor"/> over a real gate, with its expression set
    /// to <c>decision.escalated</c> so the branch it lands on <i>is</i> the accept decision this site
    /// publishes.
    /// </summary>
    private static async Task<StepExecutionResult> RunConditionalAsync(
        DbContextOptions<WorkflowEngineDbContext> options, double pYes)
    {
        var answer = new DecisionAnswer(
            QuestionName, DecisionKind.Noul, null, null, pYes, null, new Dictionary<string, double>());

        var result = new DecisionResult(
            "qa-model",
            new Dictionary<string, DecisionAnswer> { [QuestionName] = answer },
            InputTokens: 40,
            OutputTokens: 10);

        var executor = new ConditionalStepExecutor(
            NullLogger<ConditionalStepExecutor>.Instance, CreateGate(options, new StubDecisionClient(result)));

        return await executor.ExecuteAsync(CreateConditionalContext());
    }

    private static Mock<IWorkflowExecutionContextAccessor> MockAccessor()
    {
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);
        return accessor;
    }

    private static StepExecutionContext CreateReviewLoopContext(int? minScore)
    {
        var loopTarget = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.VideoCompile,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.VideoTransform, Name = "VideoTransform" }
        };

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = loopTarget.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.ReviewLoop,
            MinScore = minScore,
            MaxIterations = 3,
            LoopTargetStepOrder = 2,
            ReviewLoopConfigJson = """{"enabled":true}""",
            AgentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                AgentType = AgentType.VideoReviewAgent,
                Name = "VideoReviewAgent"
            }
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [loopTarget, step],
            AccumulatedOutput = PriorCompileOutput,
            StepOutputHistory = [new StepOutputHistoryEntry(2, "VideoCompile", PriorCompileOutput)],
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "qa-parity",
            CancellationToken = CancellationToken.None,
        };
    }

    private static StepExecutionContext CreateConditionalContext()
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Conditional,
            // The site's own published accept decision, used AS the branch condition: whatever the
            // step does next is that decision, with no second implementation of it in this harness.
            ConditionExpression = "decision.escalated",
            ConditionalConfigJson = JsonSerializer.Serialize(new
            {
                version = 1,
                mode = "Decision",
                decisionQuestion = QuestionName,
                threshold = ShippedConditional.Threshold,
                providerId = (Guid?)null,
                // The stubbed client answers at once; the deadline is only a bound, so it is set to the
                // longest the gate accepts and a starved machine cannot expire it before the row is written.
                timeoutSeconds = DecisionGateLimits.MaxSeconds
            }),
            TrueBranchStepOrder = "2",
            FalseBranchStepOrder = "3",
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.ExtractTransform, Name = "ExtractTransform" }
        };

        var trueBranch = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = step.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Agent
        };

        var falseBranch = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = step.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.Agent
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step, trueBranch, falseBranch],
            AccumulatedOutput = """{"score":9}""",
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "qa-parity",
            CancellationToken = CancellationToken.None,
        };
    }

    private static DecisionGateQuestion ColoristLookQuestion() =>
        new(
            Site: "Colorist.Look",
            Name: "Look",
            Kind: DecisionKind.Choice,
            Instructions: "QA: whole-program colour-grade look.",
            Options: ["None", "Warm", "Cool", "Filmic", "Vibrant", "Muted", "Mono"],
            SystemTwoAnswer: null);

    private static DecisionAnswer ChoiceAnswer(string name, string choice, IReadOnlyDictionary<string, double> probabilities) =>
        new(Name: name, Kind: DecisionKind.Choice, Choice: choice, Score: null, Noul: null, Confidence: null, Probabilities: probabilities);

    /// <summary>
    /// One of QA's four corpus rows, keyed by the level its mass peaks on. The runners-up are chosen
    /// so every row carries a real distribution summing to 1.00.
    /// </summary>
    private static Dictionary<string, double> ReviewLoopCorpusRow(string peakLevel, double peak) => (peakLevel, peak) switch
    {
        ("6", 0.60) => new() { ["6"] = 0.60, ["5"] = 0.20, ["7"] = 0.15, ["4"] = 0.05 },
        ("6", 0.90) => new() { ["6"] = 0.90, ["5"] = 0.06, ["7"] = 0.04 },
        ("9", 0.92) => new() { ["9"] = 0.92, ["8"] = 0.05, ["10"] = 0.03 },
        ("9", 0.50) => new() { ["9"] = 0.50, ["8"] = 0.45, ["10"] = 0.05 },
        _ => throw new ArgumentOutOfRangeException(nameof(peakLevel), $"no corpus row peaks at {peakLevel}@{peak}"),
    };

    private static int TopLevel(IReadOnlyDictionary<string, double> probabilities) =>
        int.Parse(probabilities.OrderByDescending(p => p.Value).First().Key);

    /// <summary>A hand-built <c>ReviewLoop.Score</c> row — the shape the gate writes, minus the run.</summary>
    private static DecisionObservation ScoreRow(string? acceptMetric, int? scoreCutoff) =>
        new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            StepOrder = 4,
            AgentType = AgentType.VideoReviewAgent,
            Site = ReviewLoopStepExecutor.GateSite,
            Question = ReviewLoopStepExecutor.GateQuestionName,
            Kind = DecisionKind.Score,
            Answer = "9",
            ProbabilitiesJson = """{"9":0.92,"8":0.05,"10":0.03}""",
            Mode = "Gate",
            Escalated = false,
            AcceptMetric = acceptMetric,
            ScoreCutoff = scoreCutoff,
        };

    private static async Task SeedAsync(DbContextOptions<WorkflowEngineDbContext> options, DecisionObservation row)
    {
        await using var db = new WorkflowEngineDbContext(options);
        db.DecisionObservations.Add(row);
        await db.SaveChangesAsync();
    }

    private static async Task<DecisionObservation> SingleRowAsync(DbContextOptions<WorkflowEngineDbContext> options)
    {
        await using var db = new WorkflowEngineDbContext(options);
        return await db.DecisionObservations.AsNoTracking().SingleAsync();
    }

    private static Task<DecisionCalibrationReport> ReportAsync(
        DbContextOptions<WorkflowEngineDbContext> options, double acceptAt, double minMargin, string? site)
    {
        using var db = new WorkflowEngineDbContext(options);
        return new DecisionCalibrationService(db).GetCalibrationAsync(acceptAt, minMargin, null, site, CancellationToken.None);
    }

    private static async Task<DecisionCalibrationSite> ReviewLoopSiteAsync(
        DbContextOptions<WorkflowEngineDbContext> options, double acceptAt, double minMargin) =>
        await SiteAsync(options, ReviewLoopStepExecutor.GateSite, ReviewLoopStepExecutor.GateQuestionName, acceptAt, minMargin);

    private static async Task<DecisionCalibrationSite> SiteAsync(
        DbContextOptions<WorkflowEngineDbContext> options, string site, string question, double acceptAt, double minMargin)
    {
        DecisionCalibrationReport report = await ReportAsync(options, acceptAt, minMargin, site);
        return report.Sites.Single(s => s.Site == site && s.Question == question);
    }

    /// <summary>What one ReviewLoop run did, in the one term this site's accept decision is observed in.</summary>
    private sealed record DecisionsResult(bool ReviewerRan);

    /// <summary>
    /// Forwards to the real gate while remembering what it returned, so a caller can hold both the
    /// outcome and the row that outcome persisted. Deliberately a pass-through: the row, the mock
    /// provider and the Decision client behind it are the production ones.
    /// </summary>
    private sealed class CapturingGate(IDecisionGate inner) : IDecisionGate
    {
        public DecisionGateOutcome? LastOutcome { get; private set; }

        public Task RunShadowAsync(
            Guid executionId, int stepOrder, AgentType agentType,
            DecisionState state, IReadOnlyList<DecisionGateQuestion> questions,
            DecisionGateConfig config, CancellationToken ct) =>
            inner.RunShadowAsync(executionId, stepOrder, agentType, state, questions, config, ct);

        public async Task<DecisionGateOutcome> DecideAsync(DecisionGateRequest request, CancellationToken ct)
        {
            DecisionGateOutcome outcome = await inner.DecideAsync(request, ct);
            LastOutcome = outcome;
            return outcome;
        }

        public Task AttachSystemTwoAnswersAsync(
            Guid executionId, int stepOrder,
            IReadOnlyDictionary<string, string> answersByQuestion,
            int? agentTokensUsed, CancellationToken ct) =>
            inner.AttachSystemTwoAnswersAsync(executionId, stepOrder, answersByQuestion, agentTokensUsed, ct);
    }

    /// <summary>
    /// A provider stub returning one canned result — the Decision provider is a hosted third party,
    /// and what these tests compare is what the host does with an answer, not how it is fetched.
    /// </summary>
    private sealed class StubDecisionClient(DecisionResult result) : IDecisionClient
    {
        public Task<DecisionResult> DecideAsync(
            DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct) =>
            Task.FromResult(result);
    }
}
