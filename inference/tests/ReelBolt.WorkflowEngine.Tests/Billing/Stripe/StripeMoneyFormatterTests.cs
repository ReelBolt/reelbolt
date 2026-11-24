using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

public sealed class StripeMoneyFormatterTests
{
    [Theory]
    [InlineData(2900, "usd", "$29.00")]
    [InlineData(2900, "EUR", "€29.00")]
    [InlineData(5, "GBP", "£0.05")]
    [InlineData(0, "usd", "$0.00")]
    [InlineData(100050, "USD", "$1000.50")]
    [InlineData(2900, "CHF", "CHF 29.00")]
    [InlineData(2900, "sek", "SEK 29.00")]
    [InlineData(500, "JPY", "JPY 500")]
    [InlineData(1200, "krw", "KRW 1200")]
    [InlineData(500, "ISK", "ISK 5.00")]
    [InlineData(500, "UGX", "UGX 5.00")]
    [InlineData(1234, "BHD", "BHD 12.34")]
    [InlineData(-2900, "usd", "-$29.00")]
    [InlineData(-5, "JPY", "-JPY 5")]
    public void Format_ShiftsTheUnit_ByTheCurrencyExponent(long minor, string currency, string expected)
        => StripeMoneyFormatter.Format(minor, currency).Should().Be(expected);

    [Theory]
    [InlineData("BIF")]
    [InlineData("CLP")]
    [InlineData("DJF")]
    [InlineData("GNF")]
    [InlineData("JPY")]
    [InlineData("KMF")]
    [InlineData("KRW")]
    [InlineData("MGA")]
    [InlineData("PYG")]
    [InlineData("RWF")]
    [InlineData("VND")]
    [InlineData("VUV")]
    [InlineData("XAF")]
    [InlineData("XOF")]
    [InlineData("XPF")]
    public void ZeroDecimalCurrencies_HaveExponentZero(string currency)
        => StripeMoneyFormatter.Exponent(currency).Should().Be(0);

    [Theory]
    [InlineData("USD")]
    [InlineData("ISK")]
    [InlineData("UGX")]
    [InlineData("HUF")]
    [InlineData("TWD")]
    public void EverythingElse_HasExponentTwo(string currency)
        => StripeMoneyFormatter.Exponent(currency).Should().Be(2);

    [Fact]
    public void LongMinValue_DoesNotOverflow()
        => StripeMoneyFormatter.Format(long.MinValue, "USD").Should().StartWith("-$");
}
