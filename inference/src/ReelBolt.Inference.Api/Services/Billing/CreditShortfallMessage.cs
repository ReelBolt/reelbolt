using System.Globalization;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// The one explanation every INSUFFICIENT_CREDITS refusal (REST, the assistant, the MCP) carries.
/// </summary>
/// <remarks>
/// The required amount is the run's hold (the p90 of what it usually costs) and is NOT changed here; this
/// only says what it is, what the run usually costs, and how many credits are missing. Amounts the user must
/// have are rounded UP, the balance DOWN, so "needs up to R, has B, so M more" always adds up the safe way.
/// Never called with billing off (nothing is held then).
/// </remarks>
public static class CreditShortfallMessage
{
    /// <summary>
    /// "This run needs up to 788 credits to start (it usually costs about 286); the workspace has 500 available,
    /// so 288 more are needed. Credits that aren't used are returned when the run finishes."
    /// The parenthesis is dropped when there is no typical cost (no history) or it is not below the hold.
    /// </summary>
    public static string Build(long requiredMillis, long? typicalMillis, long balanceMillis)
    {
        string typical = typicalMillis is { } t && t > 0 && t < requiredMillis
            ? $" (it usually costs about {CreditCostWording.Near(t / 1000.0)})"
            : string.Empty;
        return $"This run needs up to {CreditCostWording.Up(requiredMillis / 1000.0)} credits to start{typical}; "
            + $"the workspace has {Down(balanceMillis / 1000.0)} available, "
            + $"so {Format(MissingCredits(requiredMillis, balanceMillis))} more are needed. "
            + "Credits that aren't used are returned when the run finishes.";
    }

    /// <summary>The message for a refused preflight verdict (typical only when the estimate has history behind it).</summary>
    public static string Build(WorkflowExecutionPreflightResult refused) =>
        BuildFromCredits(refused.Required, refused.HasHistory ? refused.Typical : null, refused.Balance);

    /// <summary>The <c>missing</c> field of a refused preflight verdict, in credits.</summary>
    public static double MissingCredits(WorkflowExecutionPreflightResult refused) =>
        MissingCreditsFromCredits(refused.Required, refused.Balance);

    /// <summary><see cref="Build(long, long?, long)"/> for figures already in credits (the preflight verdict's unit).</summary>
    public static string BuildFromCredits(double required, double? typical, double balance) =>
        Build(ToMillis(required), typical is { } t ? ToMillis(t) : null, ToMillis(balance));

    /// <summary>
    /// The credits still missing: the SHOWN required figure (rounded up) minus the SHOWN balance (rounded down),
    /// exactly, so the three numbers in the message always add up; 0 when the balance really covers the hold.
    /// </summary>
    public static double MissingCredits(long requiredMillis, long balanceMillis)
    {
        double balance = Math.Max(0, balanceMillis) / 1000.0;
        double required = requiredMillis / 1000.0;
        if (required - balance <= 0)
            return 0;
        return Math.Round(CreditCostWording.UpValue(required) - DownValue(balance), 1);
    }

    /// <summary><see cref="MissingCredits(long, long)"/> for figures already in credits.</summary>
    public static double MissingCreditsFromCredits(double required, double balance) =>
        MissingCredits(ToMillis(required), ToMillis(balance));

    private static long ToMillis(double credits) => (long)Math.Round(credits * 1000.0);

    private static string Format(double credits) => credits.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary><paramref name="credits"/> rounded DOWN at the shown precision (one decimal below 10, whole from 10).</summary>
    public static string Down(double credits) => DownValue(credits).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary><see cref="Down"/> as a number.</summary>
    public static double DownValue(double credits)
    {
        double scale = credits < 10 ? 10 : 1;
        return Math.Floor(Math.Round(Math.Max(0, credits) * scale, 6)) / scale;
    }
}
