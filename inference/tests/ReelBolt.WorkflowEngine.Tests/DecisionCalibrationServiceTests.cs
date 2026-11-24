using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Aggregation tests for <see cref="DecisionCalibrationService"/> — hand-seeded
/// <c>decision_observations</c> rows over EFCore.InMemory.
///
/// <para>
/// The local install has zero rows and cannot produce one (no <c>Decision</c> provider), so
/// every "happy path" here is really the seeded-row case and the malformed shapes are the
/// normal ones the service must survive.
/// </para>
/// </summary>
public sealed class DecisionCalibrationServiceTests
{
    /// <summary>
    /// A hand-seeded in-memory database. Each call gets its own store so a test that seeds twice
    /// (the same rows at two different thresholds) does not accumulate them into one table.
    /// </summary>
    private static WorkflowEngineDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DecisionObservation Row(
        string site,
        string question = "Look",
        DecisionKind kind = DecisionKind.Choice,
        double? confidence = null,
        string probabilitiesJson = "{\"a\":0.9,\"b\":0.1}",
        string answer = "a",
        string? outcomeJson = "{\"agreed\":true}",
        Guid? providerId = null,
        string? model = null,
        int? agentTokens = null,
        int? decisionInputTokens = null,
        int? decisionOutputTokens = null,
        bool escalated = false,
        DateTime? createdAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            StepOrder = 1,
            AgentType = AgentType.Colorist,
            Site = site,
            Question = question,
            OptionsHash = "hash",
            Kind = kind,
            Answer = answer,
            Confidence = confidence,
            ProbabilitiesJson = probabilitiesJson,
            Mode = "Shadow",
            Escalated = escalated,
            DownstreamOutcomeJson = outcomeJson,
            ProviderId = providerId,
            Model = model,
            AgentTokensUsed = agentTokens,
            DecisionInputTokens = decisionInputTokens,
            DecisionOutputTokens = decisionOutputTokens,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };

    private static async Task<DecisionCalibrationReport> GetAsync(
        WorkflowEngineDbContext db,
        double acceptAt = 0.85,
        double minMargin = 0.25,
        Guid? providerId = null,
        string? site = null)
    {
        var service = new DecisionCalibrationService(db);
        return await service.GetCalibrationAsync(acceptAt, minMargin, providerId, site, CancellationToken.None);
    }

    private static async Task<DecisionCalibrationReport> SeedAndGetAsync(
        IEnumerable<DecisionObservation> rows,
        double acceptAt = 0.85,
        double minMargin = 0.25,
        Guid? providerId = null,
        string? site = null)
    {
        await using WorkflowEngineDbContext db = NewDb();
        db.DecisionObservations.AddRange(rows);
        await db.SaveChangesAsync();
        return await GetAsync(db, acceptAt, minMargin, providerId, site);
    }

    /// <summary>
    /// The two guarantees every reliability diagram owes its reader, site or provider: all ten
    /// buckets are present, and they run lowest-first. Ten buckets of 0.1 starting at 0.
    /// </summary>
    private static void AssertTenAscendingBuckets(IReadOnlyList<ReliabilityBucket> buckets)
    {
        buckets.Should().HaveCount(10);
        buckets.Select(b => b.LowerBound).Should().BeInAscendingOrder();

        for (int i = 0; i < 10; i++)
            buckets[i].LowerBound.Should().BeApproximately(i / 10.0, 1e-12);
    }

    // ------------------------------------------------------------------ empty history

    [Fact]
    public async Task GetCalibrationAsync_WithNoRows_ReturnsAnEmptyReportAndTheSampleSizeWarning()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(Array.Empty<DecisionObservation>());

        report.Sites.Should().BeEmpty();
        report.Providers.Should().BeEmpty();
        report.Totals.ObservationCount.Should().Be(0);
        report.Totals.AgreementRate.Should().Be(0);
        report.Totals.EscalationRate.Should().Be(0);
        report.Totals.TokensSaved.Should().Be(0);
        report.Totals.TokensSavedIsProjected.Should().BeTrue();
        report.SampleSizeWarning.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Totals_CarryTheThresholdsTheReportWasComputedAt()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(
            new[] { Row("Colorist") }, acceptAt: 0.7, minMargin: 0.15);

        report.Totals.AcceptAt.Should().Be(0.7);
        report.Totals.MinMargin.Should().Be(0.15);
    }

    // ------------------------------------------------------------------ sites

    [Fact]
    public async Task Sites_GroupBySiteAndQuestion()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", "Look"),
            Row("Colorist", "Look"),
            Row("Colorist", "Strength"),
            Row("MusicSupervisor", "Track"),
        });

        report.Sites.Should().HaveCount(3);
        report.Sites.Single(s => s.Site == "Colorist" && s.Question == "Look")
            .ObservationCount.Should().Be(2);
        report.Sites.Single(s => s.Site == "Colorist" && s.Question == "Strength")
            .ObservationCount.Should().Be(1);
        report.Sites.Single(s => s.Site == "MusicSupervisor" && s.Question == "Track")
            .ObservationCount.Should().Be(1);
    }

    [Fact]
    public async Task Sites_AreOrderedDeterministicallyBySiteThenQuestion()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("MusicSupervisor", "Track"),
            Row("Colorist", "Strength"),
            Row("Colorist", "Look"),
        });

        report.Sites.Select(s => $"{s.Site}/{s.Question}")
            .Should().ContainInOrder("Colorist/Look", "Colorist/Strength", "MusicSupervisor/Track");
    }

    // ------------------------------------------------------------------ reliability buckets

    [Fact]
    public async Task ReliabilityBuckets_AlwaysContainTenFixedBucketsOfWidthOneTenth()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[] { Row("Colorist", confidence: 0.5) });

        DecisionCalibrationSite site = report.Sites.Single();
        site.ReliabilityBuckets.Should().HaveCount(10);
        site.ReliabilityBuckets.Select(b => b.LowerBound)
            .Should().Equal(0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9);
    }

    [Fact]
    public async Task ReliabilityBuckets_PlaceEachRowByItsConfidence()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: 0.05),
            Row("Colorist", confidence: 0.15),
            Row("Colorist", confidence: 0.95),
        });

        int[] counts = report.Sites.Single().ReliabilityBuckets.Select(b => b.Count).ToArray();
        counts.Should().Equal(1, 1, 0, 0, 0, 0, 0, 0, 0, 1);
    }

    [Fact]
    public async Task ReliabilityBuckets_PlaceExactlyOnePointZeroInTheTopBucket()
    {
        // The upper edge is inclusive: [0.9, 1.0] holds 1.0 rather than falling off the end.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: 1.0),
            Row("Colorist", confidence: 0.9),
        });

        report.Sites.Single().ReliabilityBuckets[9].Count.Should().Be(2);
        report.Sites.Single().ReliabilityBuckets.Sum(b => b.Count).Should().Be(2);
    }

    [Fact]
    public async Task ReliabilityBuckets_CarryTheMeanPredictedConfidenceOfTheirRows()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: 0.91),
            Row("Colorist", confidence: 0.95),
        });

        ReliabilityBucket top = report.Sites.Single().ReliabilityBuckets[9];
        top.PredictedMean.Should().BeApproximately(0.93, 1e-9);
    }

    [Fact]
    public async Task ReliabilityBuckets_ExcludeRowsWithUnparseableProbabilitiesJson()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: 0.5, probabilitiesJson: "{not json"),
            Row("Colorist", confidence: 0.5),
        });

        DecisionCalibrationSite site = report.Sites.Single();
        site.ObservationCount.Should().Be(2);
        site.ReliabilityBuckets.Sum(b => b.Count).Should().Be(1);
        site.ReliabilityBuckets[5].Count.Should().Be(1);
    }

    [Fact]
    public async Task ReliabilityBuckets_ExcludeNoulRowsWhoseProbabilitiesAreEmpty()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", kind: DecisionKind.Noul, answer: "0.9", probabilitiesJson: "{}"),
        });

        DecisionCalibrationSite site = report.Sites.Single();
        site.ObservationCount.Should().Be(1);
        site.ReliabilityBuckets.Sum(b => b.Count).Should().Be(0);
    }

    [Fact]
    public async Task ReliabilityBuckets_ForAnEmptyBucket_EmitADegenerateIntervalNotNaN()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[] { Row("Colorist", confidence: 0.05) });

        ReliabilityBucket empty = report.Sites.Single().ReliabilityBuckets[7];
        empty.Count.Should().Be(0);
        empty.PredictedMean.Should().Be(0);
        empty.ObservedAgreementRate.Should().Be(0);
        empty.ConfidenceIntervalLow.Should().Be(0);
        empty.ConfidenceIntervalHigh.Should().Be(0);
        double.IsNaN(empty.ConfidenceIntervalHigh).Should().BeFalse();
    }

    [Fact]
    public async Task WilsonInterval_OnASmallBucket_IsWideRatherThanCertain()
    {
        // 1 agreement out of 3 observations in the top bucket: a point estimate of 0.333 must
        // not read as certainty — the Wilson interval is [0.0615, 0.7923] at 95%.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: 0.95, outcomeJson: "{\"agreed\":true}"),
            Row("Colorist", confidence: 0.95, outcomeJson: "{\"agreed\":false}"),
            Row("Colorist", confidence: 0.95, outcomeJson: "{\"agreed\":false}"),
        });

        ReliabilityBucket top = report.Sites.Single().ReliabilityBuckets[9];
        top.Count.Should().Be(3);
        top.ObservedAgreementRate.Should().BeApproximately(1.0 / 3.0, 1e-9);
        top.ConfidenceIntervalLow.Should().BeApproximately(0.0615, 1e-3);
        top.ConfidenceIntervalHigh.Should().BeApproximately(0.7923, 1e-3);
        top.ConfidenceIntervalHigh.Should().BeLessThan(1.0);
    }

    [Fact]
    public async Task WilsonInterval_IsNormalisedToTheZeroToOneScale()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: 0.95, outcomeJson: "{\"agreed\":true}"),
            Row("Colorist", confidence: 0.95, outcomeJson: "{\"agreed\":false}"),
        });

        ReliabilityBucket top = report.Sites.Single().ReliabilityBuckets[9];
        top.ObservedAgreementRate.Should().BeApproximately(0.5, 1e-9);
        top.ConfidenceIntervalLow.Should().BeInRange(0.0, 1.0);
        top.ConfidenceIntervalHigh.Should().BeInRange(0.0, 1.0);
        top.ConfidenceIntervalLow.Should().BeLessThan(top.ObservedAgreementRate);
        top.ConfidenceIntervalHigh.Should().BeGreaterThan(top.ObservedAgreementRate);
    }

    // ------------------------------------------------------------------ agreement

    [Fact]
    public async Task AgreementRate_CountsRowsWhoseOutcomeSaysAgreed()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", outcomeJson: "{\"agreed\":true}"),
            Row("Colorist", outcomeJson: "{\"agreed\":true}"),
            Row("Colorist", outcomeJson: "{\"agreed\":false}"),
            Row("Colorist", outcomeJson: "{\"agreed\":false}"),
        });

        DecisionCalibrationSite site = report.Sites.Single();
        site.AgreementCount.Should().Be(2);
        site.AgreementRate.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public async Task AgreementRate_ExcludesRowsWithNoUsableOutcomeFromItsDenominator()
    {
        // Two rows agree, one has no outcome at all and one has one without an "agreed" key:
        // the rate is 2/2, not 2/4.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", outcomeJson: "{\"agreed\":true}"),
            Row("Colorist", outcomeJson: "{\"agreed\":true}"),
            Row("Colorist", outcomeJson: null),
            Row("Colorist", outcomeJson: "{\"something\":\"else\"}"),
        });

        DecisionCalibrationSite site = report.Sites.Single();
        site.ObservationCount.Should().Be(4);
        site.AgreementCount.Should().Be(2);
        site.AgreementRate.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task AgreementRate_WithMalformedOutcomeJson_DoesNotThrowAndIsExcluded()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", outcomeJson: "{broken"),
            Row("Colorist", outcomeJson: "{\"agreed\":true}"),
        });

        report.Sites.Single().AgreementRate.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task AgreementRate_WithNoUsableOutcomesAtAll_IsZeroRatherThanNaN()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", outcomeJson: null),
        });

        report.Sites.Single().AgreementRate.Should().Be(0);
    }

    // ------------------------------------------------------------------ confidence

    [Fact]
    public async Task MeanConfidence_UsesTheStoredConfidenceWherePresent()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: 0.8),
            Row("Colorist", confidence: 0.6),
        });

        report.Sites.Single().MeanConfidence.Should().BeApproximately(0.7, 1e-9);
    }

    [Fact]
    public async Task MeanConfidence_ForNoulRows_DerivesFromTheSharedTopTwoRule()
    {
        // A Noul row has no Confidence by construction; its Answer carries p(yes), and
        // DecisionProbability.TopTwo reads (max(p, 1-p), min(p, 1-p)): 0.9 -> 0.9, 0.2 -> 0.8.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", kind: DecisionKind.Noul, answer: "0.9", probabilitiesJson: "{}", confidence: null),
            Row("Colorist", kind: DecisionKind.Noul, answer: "0.2", probabilitiesJson: "{}", confidence: null),
        });

        report.Sites.Single().MeanConfidence.Should().BeApproximately(0.85, 1e-9);
    }

    [Fact]
    public async Task MeanConfidence_ForChoiceRowsWithoutStoredConfidence_DerivesFromTheDistribution()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: null, probabilitiesJson: "{\"a\":0.6,\"b\":0.4}"),
        });

        report.Sites.Single().MeanConfidence.Should().BeApproximately(0.6, 1e-9);
    }

    // ------------------------------------------------------------------ escalation

    [Fact]
    public async Task EscalationRate_IsRecomputedFromTheSuppliedThresholds()
    {
        // The same rows twice, at two different AcceptAt values: the stored Escalated column is
        // never read, so only the supplied thresholds can move the rate.
        static DecisionObservation[] Rows() =>
        [
            Row("Colorist", confidence: 0.9, probabilitiesJson: "{\"a\":0.9,\"b\":0.1}"),
            Row("Colorist", confidence: 0.9, probabilitiesJson: "{\"a\":0.9,\"b\":0.1}"),
            Row("Colorist", confidence: 0.9, probabilitiesJson: "{\"a\":0.9,\"b\":0.1}"),
            Row("Colorist", confidence: 0.9, probabilitiesJson: "{\"a\":0.9,\"b\":0.1}"),
        ];

        DecisionCalibrationReport lenient = await SeedAndGetAsync(Rows(), acceptAt: 0.85, minMargin: 0.25);
        DecisionCalibrationReport strict = await SeedAndGetAsync(Rows(), acceptAt: 0.95, minMargin: 0.25);

        lenient.Sites.Single().EscalationCount.Should().Be(0);
        lenient.Sites.Single().EscalationRate.Should().Be(0);

        strict.Sites.Single().EscalationCount.Should().Be(4);
        strict.Sites.Single().EscalationRate.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task Escalation_NeverReadsTheStoredEscalatedColumn()
    {
        // A row the writer flagged escalated, at a threshold that accepts it, is NOT an escalation.
        // And a row nobody flagged, at a threshold that rejects it, IS one.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}", escalated: true),
            Row("Colorist", probabilitiesJson: "{\"a\":0.9,\"b\":0.1}", escalated: false),
        }, acceptAt: 0.85, minMargin: 0.25);

        report.Sites.Single().EscalationCount.Should().Be(0);
    }

    [Fact]
    public async Task Escalation_AlsoFiresOnAMarginBelowTheMinimum()
    {
        // top 0.90 and 0.88 both clear AcceptAt 0.85, but each margin is 0.02 — under MinMargin.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", probabilitiesJson: "{\"a\":0.90,\"b\":0.88}"),
            Row("Colorist", probabilitiesJson: "{\"a\":0.88,\"b\":0.86}"),
        }, acceptAt: 0.85, minMargin: 0.25);

        DecisionCalibrationSite site = report.Sites.Single();
        site.EscalationCount.Should().Be(2);
        site.EscalationRate.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task Totals_EscalationRateSpansEverySite()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}"),
            Row("MusicSupervisor", probabilitiesJson: "{\"a\":0.90,\"b\":0.89}"),
        }, acceptAt: 0.85, minMargin: 0.25);

        report.Totals.ObservationCount.Should().Be(2);
        report.Totals.EscalationRate.Should().BeApproximately(0.5, 1e-9);
    }

    // ------------------------------------------------------------------ tokens saved

    [Fact]
    public async Task TokensSaved_SumsTheDeltaOverAcceptedRowsOnly()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            // Accepted (top 0.99, margin 0.98): saves 900 - 100 - 50 = 750.
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}",
                agentTokens: 900, decisionInputTokens: 100, decisionOutputTokens: 50),
            // Escalated on the margin (top 0.90 clears AcceptAt 0.85 but its margin is 0.01):
            // not accepted, contributes 0.
            Row("Colorist", probabilitiesJson: "{\"a\":0.90,\"b\":0.89}",
                agentTokens: 900, decisionInputTokens: 100, decisionOutputTokens: 50),
        }, acceptAt: 0.85, minMargin: 0.25);

        report.Totals.TokensSaved.Should().Be(750);
        report.Totals.TokensSavedIsProjected.Should().BeTrue();
    }

    [Fact]
    public async Task TokensSaved_IsFlooredAtZeroPerRow()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            // The decision cost more than the agent: -150, floored to 0.
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}",
                agentTokens: 50, decisionInputTokens: 100, decisionOutputTokens: 100),
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}",
                agentTokens: 300, decisionInputTokens: 100, decisionOutputTokens: 50),
        });

        report.Totals.TokensSaved.Should().Be(150);
    }

    [Fact]
    public async Task TokensSaved_IsZeroWhenAnyPartOfTheCostBasisWasNotRecorded()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            // No counterfactual recorded: contributes exactly 0.
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}", agentTokens: null),
            // The counterfactual IS recorded but the decision's own cost is not. Coalescing those two
            // nulls to zero would report the agent's whole 500 as saved — an unrecorded basis must
            // contribute nothing, not the maximum. (QA-11: this row used to report 500.)
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}",
                agentTokens: 500, decisionInputTokens: null, decisionOutputTokens: null),
            // One half of the decision cost recorded, the other not: still an incomplete basis.
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}",
                agentTokens: 500, decisionInputTokens: 100, decisionOutputTokens: null),
        });

        report.Totals.TokensSaved.Should().Be(0);
        report.Totals.TokensSavedIsProjected.Should().BeTrue();
    }

    [Fact]
    public async Task TokensSaved_CountsAFullyRecordedBasisAlongsideAnUnrecordedOne()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            // Fully recorded: 900 - 100 - 50 = 750, and it still counts.
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}",
                agentTokens: 900, decisionInputTokens: 100, decisionOutputTokens: 50),
            // Partially recorded: contributes 0 rather than the agent's whole 500.
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}",
                agentTokens: 500, decisionInputTokens: null, decisionOutputTokens: null),
        });

        report.Totals.TokensSaved.Should().Be(750);
    }

    // ------------------------------------------------------------------ sample size

    [Fact]
    public async Task SampleSizeWarning_IsNullAtExactlyThirtyObservations()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(
            Enumerable.Range(0, 30).Select(_ => Row("Colorist")));

        report.Totals.ObservationCount.Should().Be(30);
        report.SampleSizeWarning.Should().BeNull();
    }

    [Fact]
    public async Task SampleSizeWarning_IsNonNullAtTwentyNineObservations()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(
            Enumerable.Range(0, 29).Select(_ => Row("Colorist")));

        report.Totals.ObservationCount.Should().Be(29);
        report.SampleSizeWarning.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SampleSizeWarning_CountsEverySiteNotJustTheLargest()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}"),
            Row("MusicSupervisor", probabilitiesJson: "{\"a\":0.99,\"b\":0.01}"),
        });

        report.Totals.ObservationCount.Should().Be(2);
        report.SampleSizeWarning.Should().NotBeNullOrWhiteSpace();
    }

    // ------------------------------------------------------------------ providers

    [Fact]
    public async Task Providers_GroupByProviderIdAndCarryTheModel()
    {
        var providerA = Guid.NewGuid();
        var providerB = Guid.NewGuid();

        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", providerId: providerA, model: "gpt-4o-mini", outcomeJson: "{\"agreed\":true}"),
            Row("Colorist", providerId: providerA, model: "gpt-4o-mini", outcomeJson: "{\"agreed\":false}"),
            Row("Colorist", providerId: providerB, model: "claude-sonnet", outcomeJson: "{\"agreed\":true}"),
        });

        report.Providers.Should().HaveCount(2);

        DecisionCalibrationProvider a = report.Providers.Single(p => p.ProviderId == providerA);
        a.Model.Should().Be("gpt-4o-mini");
        a.ObservationCount.Should().Be(2);
        a.AgreementRate.Should().BeApproximately(0.5, 1e-9);

        DecisionCalibrationProvider b = report.Providers.Single(p => p.ProviderId == providerB);
        b.Model.Should().Be("claude-sonnet");
        b.ObservationCount.Should().Be(1);
        b.AgreementRate.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task Providers_ExcludeRowsWithNoProviderId()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", providerId: null),
            Row("Colorist", providerId: Guid.NewGuid()),
        });

        report.Providers.Should().HaveCount(1);
    }

    // ------------------------------------------------------------------ provider reliability diagrams

    // The brief's goal 0 asks for a reliability diagram per site AND per provider. The site
    // diagram cannot be filtered down to one: a single site's rows can be split across providers,
    // so folding them back together is not a per-provider figure. These tests pin that the
    // provider entries carry their OWN diagram, recomputed over their own rows.

    [Fact]
    public async Task ProviderBuckets_SpreadAcrossTwoBuckets_CarryTheirOwnCountsAndPredictedMean()
    {
        var provider = Guid.NewGuid();

        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", providerId: provider, confidence: 0.95),
            Row("Colorist", providerId: provider, confidence: 0.85),
            Row("Colorist", providerId: provider, confidence: 0.88),
        });

        DecisionCalibrationProvider entry = report.Providers.Single();
        AssertTenAscendingBuckets(entry.ReliabilityBuckets);

        // 0.95 lands in [0.9, 1.0]; 0.85 and 0.88 both land in [0.8, 0.9].
        ReliabilityBucket high = entry.ReliabilityBuckets[9];
        high.Count.Should().Be(1);
        high.PredictedMean.Should().BeApproximately(0.95, 1e-9);

        ReliabilityBucket mid = entry.ReliabilityBuckets[8];
        mid.Count.Should().Be(2);
        mid.PredictedMean.Should().BeApproximately(0.865, 1e-9);

        // The eight buckets the provider did not touch are present and empty, not absent.
        entry.ReliabilityBuckets.Where((_, i) => i is not 8 and not 9)
            .Should().OnlyContain(b => b.Count == 0);
        entry.ReliabilityBuckets.Sum(b => b.Count).Should().Be(3);
    }

    [Fact]
    public async Task ProviderBuckets_DifferFromEachOtherAndFromTheSite()
    {
        var providerA = Guid.NewGuid();
        var providerB = Guid.NewGuid();

        // Four rows, one site, one question, two providers. Every provider sees only its own half,
        // so its diagram must be its own figure rather than a copy of the site's.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", providerId: providerA, confidence: 0.95),
            Row("Colorist", providerId: providerA, confidence: 0.05),
            Row("Colorist", providerId: providerB, confidence: 0.55),
            Row("Colorist", providerId: providerB, confidence: 0.65),
        });

        int[] siteCounts = report.Sites.Single().ReliabilityBuckets.Select(b => b.Count).ToArray();
        int[] aCounts = report.Providers.Single(p => p.ProviderId == providerA)
            .ReliabilityBuckets.Select(b => b.Count).ToArray();
        int[] bCounts = report.Providers.Single(p => p.ProviderId == providerB)
            .ReliabilityBuckets.Select(b => b.Count).ToArray();

        // A holds only 0.05 (bucket 0) and 0.95 (bucket 9); B holds only 0.55 and 0.65 (5 and 6).
        aCounts.Should().BeEquivalentTo(new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, o => o.WithStrictOrdering());
        bCounts.Should().BeEquivalentTo(new[] { 0, 0, 0, 0, 0, 1, 1, 0, 0, 0 }, o => o.WithStrictOrdering());

        // The site sees all four rows at once — a third, different figure.
        siteCounts.Should().BeEquivalentTo(new[] { 1, 0, 0, 0, 0, 1, 1, 0, 0, 1 }, o => o.WithStrictOrdering());
        siteCounts.Should().BeEquivalentTo(aCounts.Zip(bCounts, (x, y) => x + y).ToArray(), o => o.WithStrictOrdering());

        // Compared positionally: as multisets A and B look alike (each is two 1s and eight 0s),
        // and it is precisely the per-bucket positions that make them different diagrams.
        aCounts.Should().NotBeEquivalentTo(bCounts, o => o.WithStrictOrdering());
        aCounts.Should().NotBeEquivalentTo(siteCounts, o => o.WithStrictOrdering());
        bCounts.Should().NotBeEquivalentTo(siteCounts, o => o.WithStrictOrdering());

        // And the concrete difference, so a failure says which bucket diverged.
        aCounts[0].Should().Be(1);
        bCounts[0].Should().Be(0);
        aCounts[5].Should().Be(0);
        bCounts[5].Should().Be(1);
        siteCounts[0].Should().Be(1);
        siteCounts[5].Should().Be(1);

        AssertTenAscendingBuckets(report.Providers.Single(p => p.ProviderId == providerA).ReliabilityBuckets);
        AssertTenAscendingBuckets(report.Providers.Single(p => p.ProviderId == providerB).ReliabilityBuckets);
    }

    [Fact]
    public async Task ProviderSampleSizeWarning_AppliesPerProvider_WhileTheReportIsAboveTheMinimum()
    {
        var thin = Guid.NewGuid();
        var fat = Guid.NewGuid();

        var rows = new List<DecisionObservation>();
        for (int i = 0; i < 29; i++)
            rows.Add(Row("Colorist", providerId: thin, model: "thin-model"));
        for (int i = 0; i < 30; i++)
            rows.Add(Row("Colorist", providerId: fat, model: "fat-model"));

        DecisionCalibrationReport report = await SeedAndGetAsync(rows);

        // 59 rows: the report-level warning is silent, so only a PER-PROVIDER threshold can
        // produce the warning below.
        report.Totals.ObservationCount.Should().Be(59);
        report.SampleSizeWarning.Should().BeNull();

        DecisionCalibrationProvider thinEntry = report.Providers.Single(p => p.ProviderId == thin);
        DecisionCalibrationProvider fatEntry = report.Providers.Single(p => p.ProviderId == fat);

        thinEntry.ObservationCount.Should().Be(29);
        thinEntry.SampleSizeWarning.Should().NotBeNullOrWhiteSpace();
        thinEntry.SampleSizeWarning.Should().Contain("29");

        // Names which provider is thin, so a reader does not have to guess.
        thinEntry.SampleSizeWarning.Should().Contain("thin-model");

        fatEntry.ObservationCount.Should().Be(30);
        fatEntry.SampleSizeWarning.Should().BeNull();
    }

    [Fact]
    public async Task ProviderWithOnlyUnbucketableRows_StillGetsTenZeroedBucketsAndNeverThrows()
    {
        var provider = Guid.NewGuid();

        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", providerId: provider, probabilitiesJson: "{}", confidence: null, outcomeJson: null),
            Row("Colorist", providerId: provider, probabilitiesJson: "{not json", confidence: 0.9, outcomeJson: null),
            Row("Colorist", providerId: provider, probabilitiesJson: "[]", confidence: 0.9, outcomeJson: null),
            Row("Colorist", providerId: provider, probabilitiesJson: "", confidence: 0.9, outcomeJson: null),
        });

        DecisionCalibrationProvider entry = report.Providers.Single();

        // The rows are counted even though none of them can be plotted.
        entry.ObservationCount.Should().Be(4);
        AssertTenAscendingBuckets(entry.ReliabilityBuckets);
        entry.ReliabilityBuckets.Sum(b => b.Count).Should().Be(0);

        entry.ReliabilityBuckets.Should().OnlyContain(b => b.Count == 0);
        entry.ReliabilityBuckets.Should().OnlyContain(b => b.PredictedMean == 0);
        entry.ReliabilityBuckets.Should().OnlyContain(b => b.ObservedAgreementRate == 0);

        // An empty bucket's interval is the degenerate [0, 0] — never NaN.
        entry.ReliabilityBuckets.Should().OnlyContain(b => b.ConfidenceIntervalLow == 0);
        entry.ReliabilityBuckets.Should().OnlyContain(b => b.ConfidenceIntervalHigh == 0);
        entry.ReliabilityBuckets
            .Should().OnlyContain(b => double.IsFinite(b.PredictedMean) && double.IsFinite(b.ObservedAgreementRate));
    }

    [Fact]
    public async Task ProviderBuckets_ReuseTheSiteBucketShapeAndTheSameBoundaryConvention()
    {
        var provider = Guid.NewGuid();

        // Exactly 1.0 is the top-bucket edge: the shared convention clamps it into bucket 9
        // rather than letting it fall off the range. The provider diagram must obey the same rule
        // as the site's, being the same implementation.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", providerId: provider, confidence: 1.0, outcomeJson: "{\"agreed\":true}"),
            Row("Colorist", providerId: provider, confidence: 0.3, outcomeJson: "{\"agreed\":false}"),
        });

        ReliabilityBucket top = report.Providers.Single().ReliabilityBuckets[9];
        top.Count.Should().Be(1);
        top.PredictedMean.Should().BeApproximately(1.0, 1e-9);
        top.ObservedAgreementRate.Should().BeApproximately(1.0, 1e-9);

        // 0.3 is stored as 0.2999999999999999889, so this only lands in bucket 3 because the
        // shared epsilon is applied before the floor. One of one disagreement is a single trial,
        // so the Wilson interval on it is very wide and still bounded — that width is the point.
        ReliabilityBucket edge = report.Providers.Single().ReliabilityBuckets[3];
        edge.Count.Should().Be(1);
        edge.PredictedMean.Should().BeApproximately(0.3, 1e-9);
        edge.ObservedAgreementRate.Should().Be(0);
        edge.ConfidenceIntervalLow.Should().BeApproximately(0, 1e-5);
        edge.ConfidenceIntervalHigh.Should().BeApproximately(0.7935, 1e-3);
        edge.ConfidenceIntervalHigh.Should().BeLessThan(1);

        // One provider, so its diagram and the site's cover the same rows and must agree bucket
        // for bucket — the same implementation, not a second one that happens to look similar.
        report.Providers.Single().ReliabilityBuckets.Should()
            .BeEquivalentTo(report.Sites.Single().ReliabilityBuckets, o => o.WithStrictOrdering());
    }

    // ------------------------------------------------------------------ filters

    [Fact]
    public async Task SiteFilter_RestrictsSitesProvidersAndTotals()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist"),
            Row("Colorist"),
            Row("MusicSupervisor"),
        }, site: "Colorist");

        report.Sites.Should().HaveCount(1);
        report.Sites.Single().Site.Should().Be("Colorist");
        report.Totals.ObservationCount.Should().Be(2);
    }

    [Fact]
    public async Task ProviderFilter_RestrictsSitesProvidersAndTotals()
    {
        var wanted = Guid.NewGuid();

        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", providerId: wanted),
            Row("Colorist", providerId: Guid.NewGuid()),
        }, providerId: wanted);

        report.Sites.Should().HaveCount(1);
        report.Sites.Single().ObservationCount.Should().Be(1);
        report.Providers.Should().HaveCount(1);
        report.Providers.Single().ProviderId.Should().Be(wanted);
        report.Totals.ObservationCount.Should().Be(1);
    }

    // ------------------------------------------------------------------ robustness

    [Fact]
    public async Task GetCalibrationAsync_SurvivesEveryMalformedShapeAtOnce()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", probabilitiesJson: "{not json", outcomeJson: null),
            Row("Colorist", kind: DecisionKind.Noul, answer: "0.9", probabilitiesJson: "{}", outcomeJson: null),
            Row("Colorist", probabilitiesJson: "{}", outcomeJson: "{broken"),
            Row("Colorist", probabilitiesJson: "{\"a\":0.9,\"b\":0.1}", outcomeJson: null, agentTokens: null),
            Row("Colorist", probabilitiesJson: "[]", outcomeJson: "{\"agreed\":true}"),
        });

        DecisionCalibrationSite site = report.Sites.Single();
        site.ObservationCount.Should().Be(5);
        site.ReliabilityBuckets.Should().HaveCount(10);
        site.ReliabilityBuckets.Sum(b => b.Count).Should().Be(1);
        site.AgreementRate.Should().BeApproximately(1.0, 1e-9);
        report.Totals.TokensSaved.Should().Be(0);
    }

    [Fact]
    public async Task GetCalibrationAsync_WithAnEmptyProbabilitiesObject_DoesNotThrow()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", kind: DecisionKind.Choice, probabilitiesJson: "{}", confidence: null),
        });

        report.Sites.Single().ObservationCount.Should().Be(1);
        report.Sites.Single().ReliabilityBuckets.Sum(b => b.Count).Should().Be(0);
    }

    // ------------------------------------------------------------------ non-finite stored values

    // A stored double can be non-finite: an overflowing JSON number in ProbabilitiesJson
    // deserializes to ±Infinity, and "Infinity"/"NaN" parse under NumberStyles.Float on the Noul
    // answer path. System.Text.Json refuses to WRITE such a value, and the row is stored, so an
    // unguarded figure would 500 the endpoint permanently. The contract is: counted in
    // ObservationCount, excluded from the figure it cannot inform, never serialized.

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task NonFiniteStoredConfidence_FallsBackToTheTopTwoAndNeverReachesMeanConfidence(
        double nonFinite)
    {
        // A finite top-two is still derivable for this row, so it stays informative — the
        // non-finite stored value is dropped, not the row.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: 0.4, probabilitiesJson: "{\"a\":0.4,\"b\":0.6}"),
            Row("Colorist", confidence: nonFinite, probabilitiesJson: "{\"a\":0.6,\"b\":0.4}"),
        });

        DecisionCalibrationSite site = report.Sites.Single();

        // Counted, not dropped.
        site.ObservationCount.Should().Be(2);

        // The mean is 0.5 — (0.4 + the row's 0.6 top-two) / 2 — never NaN and never infinite.
        site.MeanConfidence.Should().BeApproximately(0.5, 1e-9);
        double.IsFinite(site.MeanConfidence).Should().BeTrue();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task NonFiniteConfidence_OnARowWithNoDistribution_LeavesTheRowCountedAndUnplotted(
        double nonFinite)
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: nonFinite, probabilitiesJson: "{}"),
        });

        DecisionCalibrationSite site = report.Sites.Single();

        site.ObservationCount.Should().Be(1);
        site.MeanConfidence.Should().Be(0);
        site.ReliabilityBuckets.Sum(b => b.Count).Should().Be(0);
        site.ReliabilityBuckets.Should().OnlyContain(b => double.IsFinite(b.PredictedMean));
    }

    [Fact]
    public async Task InfiniteTopTwo_ThatWouldHaveClearedTheThreshold_EarnsNoProjectedTokens()
    {
        // An overflowing JSON number deserializes to +Infinity, and the old comparison read it as
        // a maximally confident decision that clears AcceptAt and MinMargin — so a projection was
        // booked against a row no model ever scored. Unscoreable means no projection.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", probabilitiesJson: "{\"a\":1e400,\"b\":0.1}",
                confidence: 0.95, agentTokens: 900, decisionInputTokens: 100),
        }, acceptAt: 0.85, minMargin: 0.25);

        DecisionCalibrationSite site = report.Sites.Single();

        site.ObservationCount.Should().Be(1);
        site.EscalationCount.Should().Be(0);
        report.Totals.TokensSaved.Should().Be(0);
    }

    [Fact]
    public async Task InfiniteRunnerUp_ThatWouldHaveFailedTheThreshold_IsNotCountedAsEscalated()
    {
        // The mirror accident: -Infinity as the runner-up still leaves the level below AcceptAt,
        // so the row "escalated" — a threshold result decided by a value that is not a number.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", probabilitiesJson: "{\"a\":0.1,\"b\":-1e400}", confidence: 0.1),
        }, acceptAt: 0.85, minMargin: 0.25);

        DecisionCalibrationSite site = report.Sites.Single();

        site.ObservationCount.Should().Be(1);
        site.EscalationCount.Should().Be(0);
        site.EscalationRate.Should().Be(0);
    }

    [Fact]
    public async Task EscalationRate_ExcludesAnUnscoreableRowAndIsComputedFromTheRemainingFiniteRows()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            // Unscoreable: an +Infinity top. Counted, but it can inform neither the numerator nor
            // the denominator.
            Row("Colorist", probabilitiesJson: "{\"a\":1e400,\"b\":0.1}", confidence: 0.95),
            // Scorable and genuinely escalated on the margin (0.90 clears 0.85, margin 0.01).
            Row("Colorist", probabilitiesJson: "{\"a\":0.90,\"b\":0.89}", confidence: 0.90),
        }, acceptAt: 0.85, minMargin: 0.25);

        DecisionCalibrationSite site = report.Sites.Single();

        // Both rows are observations; only the finite one can testify about escalation.
        site.ObservationCount.Should().Be(2);
        site.EscalationCount.Should().Be(1);
        site.EscalationRate.Should().BeApproximately(1.0, 1e-9);
    }

    [Theory]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("NaN")]
    public async Task NoulAnswerThatParsesToANonFiniteValue_IsCountedButCannotInformAnyFigure(string answer)
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", kind: DecisionKind.Noul, answer: answer, probabilitiesJson: "{}", confidence: null),
            Row("Colorist", kind: DecisionKind.Noul, answer: "0.9", probabilitiesJson: "{}", confidence: null),
        });

        DecisionCalibrationSite site = report.Sites.Single();

        // The unscoreable row is still an observation; the figure it cannot inform is computed
        // from the remaining finite row alone (0.9 → top-two 0.9), not from 0.9 and a NaN.
        site.ObservationCount.Should().Be(2);
        site.MeanConfidence.Should().BeApproximately(0.9, 1e-9);
        site.EscalationRate.Should().Be(0);
        site.ReliabilityBuckets.Should().OnlyContain(b => double.IsFinite(b.PredictedMean));
    }

    [Fact]
    public async Task GetCalibrationAsync_WithEveryNonFiniteShapeAtOnce_SerialisesWithoutThrowing()
    {
        // The end-to-end guarantee this whole guard exists for: whatever is stored, the report the
        // API hands to System.Text.Json contains no non-finite double — which is the difference
        // between a 200 and a permanent 500.
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: double.PositiveInfinity, probabilitiesJson: "{\"a\":1e400,\"b\":0.1}"),
            Row("Colorist", confidence: double.NaN, probabilitiesJson: "{\"a\":0.9,\"b\":-1e400}"),
            Row("Colorist", confidence: double.NegativeInfinity, probabilitiesJson: "{}"),
            Row("Colorist", kind: DecisionKind.Noul, answer: "NaN", probabilitiesJson: "{}", confidence: null),
            Row("Colorist", kind: DecisionKind.Score, answer: "Infinity", probabilitiesJson: "{}", confidence: null),
            Row("Colorist", confidence: 0.75, probabilitiesJson: "{\"a\":0.75,\"b\":0.25}"),
        }, acceptAt: 0.85, minMargin: 0.25);

        report.Totals.ObservationCount.Should().Be(6);

        foreach (DecisionCalibrationSite site in report.Sites)
        {
            double.IsFinite(site.AgreementRate).Should().BeTrue();
            double.IsFinite(site.MeanConfidence).Should().BeTrue();
            double.IsFinite(site.EscalationRate).Should().BeTrue();

            site.ReliabilityBuckets.Should().OnlyContain(b =>
                double.IsFinite(b.LowerBound)
                && double.IsFinite(b.PredictedMean)
                && double.IsFinite(b.ObservedAgreementRate)
                && double.IsFinite(b.ConfidenceIntervalLow)
                && double.IsFinite(b.ConfidenceIntervalHigh));
        }

        string json = JsonSerializer.Serialize(report, JsonSerializerOptions.Web);
        json.Should().NotContain("Infinity").And.NotContain("NaN");
    }

    // ------------------------------------------------------------------ wire shape

    [Fact]
    public async Task Report_SerialisesToTheCamelCaseNamesTheWebTypesMirror()
    {
        DecisionCalibrationReport report = await SeedAndGetAsync(new[]
        {
            Row("Colorist", confidence: 0.95, providerId: Guid.NewGuid(), model: "gpt-4o-mini"),
        });

        string json = JsonSerializer.Serialize(report, JsonSerializerOptions.Web);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("sites", "providers", "totals", "sampleSizeWarning");

        JsonElement site = root.GetProperty("sites")[0];
        site.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "site", "question", "observationCount", "agreementCount", "agreementRate",
            "meanConfidence", "reliabilityBuckets", "escalationCount", "escalationRate", "thresholdSweep", "recommendation");

        site.GetProperty("reliabilityBuckets")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "lowerBound", "count", "predictedMean", "observedAgreementRate",
            "confidenceIntervalLow", "confidenceIntervalHigh");

        // The provider entry carries its own reliability diagram, serialized under the same names
        // and the same bucket shape as the site's — one concept, one wire shape.
        JsonElement provider = root.GetProperty("providers")[0];
        provider.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "providerId", "model", "observationCount", "agreementRate", "meanConfidence",
            "reliabilityBuckets", "sampleSizeWarning");

        provider.GetProperty("reliabilityBuckets").GetArrayLength().Should().Be(10);
        provider.GetProperty("reliabilityBuckets")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "lowerBound", "count", "predictedMean", "observedAgreementRate",
            "confidenceIntervalLow", "confidenceIntervalHigh");
        provider.GetProperty("sampleSizeWarning").ValueKind.Should().Be(JsonValueKind.String);

        root.GetProperty("totals").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "observationCount", "agreementRate", "escalationRate", "tokensSaved",
            "tokensSavedIsProjected", "acceptAt", "minMargin");
    }
}
