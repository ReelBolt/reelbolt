using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The one wording every INSUFFICIENT_CREDITS refusal uses (Product decision 5: the required amount stays the
/// p90 hold; the message only explains it).
/// </summary>
public sealed class CreditShortfallMessageTests
{
    [Fact]
    public void Build_explains_the_hold_the_typical_cost_and_what_is_missing()
    {
        string message = CreditShortfallMessage.Build(788_000, 286_000, 500_000);

        message.Should().Be(
            "This run needs up to 788 credits to start (it usually costs about 286); the workspace has 500 available, "
            + "so 288 more are needed. Credits that aren't used are returned when the run finishes.");
    }

    [Fact]
    public void Build_without_history_drops_the_parenthesis()
    {
        CreditShortfallMessage.Build(788_000, null, 500_000).Should().Be(
            "This run needs up to 788 credits to start; the workspace has 500 available, "
            + "so 288 more are needed. Credits that aren't used are returned when the run finishes.");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(788_000L)]
    [InlineData(900_000L)]
    public void Build_drops_a_typical_cost_that_is_not_below_the_hold(long typicalMillis)
    {
        CreditShortfallMessage.Build(788_000, typicalMillis, 500_000).Should().NotContain("usually costs");
    }

    [Fact]
    public void Missing_is_rounded_up_so_the_workspace_is_never_told_to_top_up_too_little()
    {
        CreditShortfallMessage.MissingCredits(788_400, 500_000).Should().Be(289);
        CreditShortfallMessage.MissingCredits(8_050, 5_000).Should().Be(3.1);
        CreditShortfallMessage.MissingCredits(1_000, 5_000).Should().Be(0);
    }

    [Fact]
    public void Balance_is_rounded_down_so_required_minus_balance_equals_the_missing_figure_shown()
    {
        CreditShortfallMessage.Build(788_000, null, 500_900).Should().Contain("has 500 available").And.Contain("288 more");
    }

    [Theory]
    [InlineData(788_200L, 500_900L, "up to 789", "has 500 available", "289 more", 289.0)]
    [InlineData(788_000L, 500_900L, "up to 788", "has 500 available", "288 more", 288.0)]
    [InlineData(8_050L, 5_020L, "up to 8.1", "has 5 available", "3.1 more", 3.1)]
    public void Required_balance_and_missing_come_from_the_same_rounded_values(
        long required, long balance, string upTo, string has, string more, double missing)
    {
        string message = CreditShortfallMessage.Build(required, null, balance);

        message.Should().Contain(upTo).And.Contain(has).And.Contain(more);
        CreditShortfallMessage.MissingCredits(required, balance).Should().Be(missing);
    }

    [Fact]
    public void Missing_is_zero_when_the_balance_covers_the_hold_even_if_rounding_would_show_a_gap()
    {
        CreditShortfallMessage.MissingCredits(100_200, 100_500).Should().Be(0);
    }
}
