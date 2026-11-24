#nullable enable
using AwesomeAssertions;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Metering;

/// <summary>
/// The boundary conversion itself: a vendor's USD figure becomes platform credits here and nowhere
/// else, at the rate card's own price (1 credit = <c>$0.006</c> of COGS).
/// </summary>
public class CreditCurrencyTests
{
    [Fact]
    public void CreditsFromUsd_converts_at_the_rate_cards_credit_price()
    {
        CreditCurrency.CreditsFromUsd(0.006m).Should().Be(1m);
        CreditCurrency.CreditsFromUsd(0.96m).Should().Be(160m);
        CreditCurrency.CreditsFromUsd(0m).Should().Be(0m);
    }

    [Fact]
    public void CreditsFromUsd_is_the_same_constant_the_ledger_prices_everything_from()
    {
        PricingDefaults.CreditUsdValue.Should().Be(0.006m);
        CreditCurrency.CreditsFromUsd(PricingDefaults.CreditUsdValue).Should().Be(1m);

        // Whole-credit round trip: what the loop-back "saved" figure and the ledger row mean by a
        // credit count is recoverable, which is the property a display or an export depends on.
        decimal credits = CreditCurrency.CreditsFromUsd(0.96m);
        (credits * PricingDefaults.CreditUsdValue).Should().Be(0.96m);
    }
}
