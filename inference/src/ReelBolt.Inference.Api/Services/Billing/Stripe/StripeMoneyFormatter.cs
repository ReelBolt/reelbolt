using System.Globalization;

namespace ReelBolt.Inference.Api.Services.Billing.Stripe;

/// <summary>
/// Turns Stripe's integer minor units into display text for <see cref="PricePreview"/>. A pure unit shift by the
/// currency's exponent: no price is ever computed. The API runs with invariant globalization, so symbols are table-driven.
/// </summary>
public static class StripeMoneyFormatter
{
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.Ordinal)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "VND", "VUV", "XAF", "XOF", "XPF"
    };

    /// <summary>
    /// Digits after the decimal point in Stripe's integer amounts: 0 for the zero-decimal list; 2 for everything else,
    /// including ISK and UGX (Stripe sends them with two decimals whose fraction is always 00).
    /// </summary>
    public static int Exponent(string currencyCode) => ZeroDecimal.Contains(currencyCode.ToUpperInvariant()) ? 0 : 2;

    public static string Format(long minor, string currencyCode)
    {
        string code = currencyCode.ToUpperInvariant();
        int exponent = Exponent(code);
        bool negative = minor < 0;
        ulong abs = minor == long.MinValue ? (ulong)long.MaxValue + 1 : (ulong)Math.Abs(minor);
        string digits = abs.ToString(CultureInfo.InvariantCulture);
        string number;
        if (exponent == 0)
        {
            number = digits;
        }
        else
        {
            digits = digits.PadLeft(exponent + 1, '0');
            number = digits[..^exponent] + "." + digits[^exponent..];
        }

        string text = code switch
        {
            "USD" => "$" + number,
            "EUR" => "€" + number,
            "GBP" => "£" + number,
            _ => code + " " + number
        };
        return negative ? "-" + text : text;
    }
}
