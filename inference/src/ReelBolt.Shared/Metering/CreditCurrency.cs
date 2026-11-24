namespace ReelBolt.Shared.Metering;

/// <summary>
/// The ONE place a vendor's USD figure becomes platform credits.
/// <para>
/// <see cref="PricingDefaults.CreditUsdValue"/> is what a credit is worth against vendor cost (D9:
/// 1 credit = <c>$0.006</c>), and the whole ledger is integer MILLIcredits keyed off it, so a USD
/// amount is converted here and nowhere else. D2's rule follows from that: the vendor's own quote is
/// never abandoned — it stays an internal input, and what a cap, a comparison or a ledger figure
/// carries afterwards is credits.
/// </para>
/// <para>
/// A vendor's published price figures are therefore still published in USD (the price catalog,
/// <c>VideoGenerationPriceQuote</c>, each provider's <c>Describe</c> table); their consumer converts
/// at that boundary rather than displaying the dollars.
/// </para>
/// </summary>
public static class CreditCurrency
{
    /// <summary>
    /// The credits <paramref name="usd"/> of vendor cost buys at the credit's own price (no markup):
    /// <c>0.006</c> → <c>1</c>, <c>0.96</c> → <c>160</c>. Exact decimal division, so a figure derived
    /// from a whole credit count round-trips through it.
    /// </summary>
    public static decimal CreditsFromUsd(decimal usd) => usd / PricingDefaults.CreditUsdValue;
}
