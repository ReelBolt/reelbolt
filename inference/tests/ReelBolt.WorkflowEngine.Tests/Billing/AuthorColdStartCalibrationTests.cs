using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// Cost-honesty T-001. The cold-start profile of an Opus-priced AuthorAgent quoted ~325 credits a pass while
/// production passes cost 44-70 (runs 197e9ca3 = 73, 30b7458e = 71 and 7c670c3f = 166 credits, the last a
/// 2-pass review loop). Rates below are the production per-unit rates (millicredits) read off usage_events.
/// </summary>
public class AuthorColdStartCalibrationTests
{
    private const decimal OpusInputMillisPerToken = 0.8m;      // 30,401 / 38,001
    private const decimal OpusCachedMillisPerToken = 0.04m;    // 5,524 / 138,115
    private const decimal OpusOutputMillisPerToken = 4.0m;     // 7,960 / 1,990
    private const decimal ComputeMillisPerSecond = 8.2m;       // 122 / 15

    // Real Author step charges per pass, millicredits (tokens + render): 197e9ca3, 30b7458e, 7c670c3f (/2 passes).
    private const long RealPassLowMillis = 44_007;
    private const long RealPassHighMillis = 69_718;
    // Author-only charge of run 7c670c3f (2 passes): 29,423+61,721+47,600+390 (opus) + 76+227 (flash).
    private const long RealAuthorTwoPassMillis = 139_437;

    private static decimal PassTypicalMillis()
    {
        StepUsageProfile p = StepUsageProfiles.ForAgent(AgentType.AuthorAgent);
        return p.Quantities.Sum(q => q.Quantity * q.Kind switch
        {
            UsageKind.LlmInputTokens => OpusInputMillisPerToken,
            UsageKind.LlmCachedInputTokens => OpusCachedMillisPerToken,
            UsageKind.LlmOutputTokens => OpusOutputMillisPerToken,
            _ => ComputeMillisPerSecond,
        });
    }

    [Fact]
    public void OpusAuthorPass_ColdStartTypical_IsNotBelowARealPassNorAboveTwiceTheRealUpper()
    {
        decimal typical = PassTypicalMillis();
        typical.Should().BeGreaterThanOrEqualTo(RealPassLowMillis);
        typical.Should().BeLessThanOrEqualTo(2 * RealPassHighMillis);
    }

    [Fact]
    public void OpusAuthorTwoPassReviewLoop_ColdStartHigh_CoversTheRealRunButNotMoreThanTwiceIt()
    {
        StepUsageProfile p = StepUsageProfiles.ForAgent(AgentType.AuthorAgent);
        decimal high = 2 * PassTypicalMillis() * (decimal)p.HighFactor;
        high.Should().BeGreaterThanOrEqualTo(RealAuthorTwoPassMillis * 0.9m);
        high.Should().BeLessThanOrEqualTo(RealAuthorTwoPassMillis * 1.2m);
    }

    [Fact]
    public void OpusAuthorColdPass_LowEdge_WhichDrivesTheHold_CoversTheLowestRealPass()
    {
        StepUsageProfile p = StepUsageProfiles.ForAgent(AgentType.AuthorAgent);
        (PassTypicalMillis() * (decimal)p.LowFactor).Should().BeGreaterThanOrEqualTo(RealPassLowMillis);
    }
}
