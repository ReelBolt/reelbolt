using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>Threshold sweep and advisory recommendation on <see cref="DecisionCalibrationService"/>.</summary>
public sealed class DecisionCalibrationSweepTests
{
    private static DecisionObservation Row(string probs, bool agreed) => new()
    {
        Id = Guid.NewGuid(),
        ExecutionId = Guid.NewGuid(),
        StepOrder = 1,
        AgentType = AgentType.Colorist,
        Site = "Colorist",
        Question = "Look",
        OptionsHash = "h",
        Kind = DecisionKind.Choice,
        Answer = "a",
        ProbabilitiesJson = probs,
        Mode = "Shadow",
        DownstreamOutcomeJson = agreed ? "{\"agreed\":true}" : "{\"agreed\":false}",
        CreatedAt = DateTime.UtcNow,
    };

    private static async Task<DecisionCalibrationSite> SiteAsync(IEnumerable<DecisionObservation> rows)
    {
        var db = new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.DecisionObservations.AddRange(rows);
        await db.SaveChangesAsync();
        DecisionCalibrationReport r = await new DecisionCalibrationService(db)
            .GetCalibrationAsync(0.85, 0.25, null, null, CancellationToken.None);
        return r.Sites.Single();
    }

    [Fact]
    public async Task Sweep_HasElevenPoints_AndAcceptanceFallsAsThresholdRises()
    {
        var rows = Enumerable.Range(0, 40).Select(i =>
            Row(i % 2 == 0 ? "{\"a\":0.95,\"b\":0.05}" : "{\"a\":0.6,\"b\":0.4}", true));
        DecisionCalibrationSite site = await SiteAsync(rows);

        site.ThresholdSweep.Should().HaveCount(11);
        site.ThresholdSweep[0].AcceptAt.Should().Be(0.5);
        site.ThresholdSweep[^1].AcceptAt.Should().Be(1.0);
        site.ThresholdSweep.Select(p => p.AcceptedCount).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Recommendation_FewRows_IsNotEnoughData()
    {
        DecisionCalibrationSite site = await SiteAsync(new[] { Row("{\"a\":0.95,\"b\":0.05}", true) });
        site.Recommendation.Verdict.Should().Be("NotEnoughData");
        site.Recommendation.SuggestedAcceptAt.Should().BeNull();
    }

    [Fact]
    public async Task Recommendation_ConfidentAndAgreeing_IsTryGate()
    {
        DecisionCalibrationSite site = await SiteAsync(
            Enumerable.Range(0, 40).Select(_ => Row("{\"a\":0.97,\"b\":0.03}", true)));
        site.Recommendation.Verdict.Should().Be("TryGate");
        site.Recommendation.SuggestedAcceptAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Recommendation_ConfidentButDisagreeing_IsKeepShadow()
    {
        DecisionCalibrationSite site = await SiteAsync(
            Enumerable.Range(0, 40).Select(i => Row("{\"a\":0.97,\"b\":0.03}", i % 2 == 0)));
        site.Recommendation.Verdict.Should().Be("KeepShadow");
    }
}
