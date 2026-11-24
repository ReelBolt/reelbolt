using System.Globalization;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// The words a paid approval surface (the assistant's permission card) uses for credits, worded like the
/// run modal: the usual range, what the workspace needs available to start, and the generated-video
/// ceiling. One place, so the card can never show a "needs" figure below what the preflight requires.
/// </summary>
/// <remarks>
/// A range (what a run usually costs) is rounded to the nearest shown figure (<see cref="Near"/>); anything
/// the user must HAVE — the amount the run needs available, a ceiling — is rounded UP (<see cref="Up"/>),
/// so 12.4 reads "13", never "12". The shown precision is one decimal below 10 credits, whole credits from 10.
/// </remarks>
public static class CreditCostWording
{
    /// <summary>
    /// A paid run whose cost could not be priced (billing is on, but the estimate or the balance read
    /// failed): the run still charges, so the card stays paid and says this instead of a figure.
    /// </summary>
    public const string RunCostUnknown = "the credits this run uses could not be worked out in advance";

    /// <summary>
    /// <see cref="RunCostUnknown"/> for a paid picture action (an AI picture tool, a designed graphic) whose
    /// price is unknown with billing on. Only an explicit "not charged" makes such a card not paid.
    /// </summary>
    public const string CostUnknown = "the credits this uses could not be worked out in advance";

    /// <summary>
    /// <paramref name="credits"/> rounded UP at the shown precision (one decimal below 10, whole credits from
    /// 10), invariant culture. For figures the user must have available: never shows less than the amount.
    /// </summary>
    public static string Up(double credits) => UpValue(credits).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary><see cref="Up"/> as a number, so a figure derived from it (credits missing) adds up with what is shown.</summary>
    public static double UpValue(double credits)
    {
        double scale = credits < 10 ? 10 : 1;
        // The product is rounded to a few decimals first so binary noise (0.3 × 10 = 3.0000000000000004)
        // does not push an exact figure up a step.
        return Math.Ceiling(Math.Round(credits * scale, 6)) / scale;
    }

    /// <summary><paramref name="credits"/> rounded to the nearest shown figure. For the ends of a range only.</summary>
    public static string Near(double credits) =>
        Math.Round(credits, credits < 10 ? 1 : 0).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// The cost line of a paid action, from its estimate in credits: the usual range (<paramref name="low"/>–<paramref name="high"/>),
    /// what it needs available to start (<paramref name="required"/>, the preflight's figure) and the fixed-price
    /// generated-video ceiling (<paramref name="fixedMax"/>). Null when every figure is zero (nothing to say).
    /// </summary>
    public static string? RunCost(double low, double high, double required, double fixedMax)
    {
        if (high > 0)
        {
            string text = low <= 0 || Near(low) == Near(high)
                ? $"usually {Near(high)} {Noun(Near(high))}"
                : $"usually {Near(low)}–{Near(high)} credits";
            if (required > 0)
                text += $" (needs {Up(required)} available to start; can cost more)";
            if (fixedMax > 0)
                text += $", plus up to {Up(fixedMax)} for generated video";
            return text;
        }

        if (fixedMax > 0)
        {
            return $"up to {Up(fixedMax)} {Noun(Up(fixedMax))} for generated video"
                + (required > 0 ? $" (needs {Up(required)} available to start)" : string.Empty);
        }

        return required > 0 ? $"needs {Up(required)} {Noun(Up(required))} available to start; can cost more" : null;
    }

    /// <summary>
    /// The cost line of a run's approval card: the hold the preflight requires, said as
    /// "needs up to X credits", then what the run usually costs — "(usually about Y)" from the typical cost, else
    /// the usual range — and, when the workspace cannot cover it (<paramref name="balance"/> below the hold),
    /// how many credits are missing. Falls back to <see cref="RunCost"/> when nothing is required.
    /// </summary>
    public static string? RunNeeds(double low, double high, double required, double fixedMax, double typical, double? balance = null)
    {
        if (required <= 0)
            return RunCost(low, high, required, fixedMax);

        string text = $"needs up to {Up(required)} {Noun(Up(required))}";
        if (typical > 0 && typical < required)
            text += $" (usually about {Near(typical)})";
        else if (high > 0)
            text += low <= 0 || Near(low) == Near(high) ? $" (usually {Near(high)} {Noun(Near(high))})" : $" (usually {Near(low)}–{Near(high)} credits)";

        if (fixedMax > 0)
            text += high > 0 || typical > 0 ? $", plus up to {Up(fixedMax)} for generated video" : $", including up to {Up(fixedMax)} for generated video";

        if (balance is { } available && CreditShortfallMessage.MissingCreditsFromCredits(required, available) is > 0 and var missing)
            text += $"; the workspace has {CreditShortfallMessage.Down(available)}, so {Near(missing)} more are needed";
        return text;
    }

    /// <summary>"credit" for a figure shown as exactly 1, else "credits".</summary>
    private static string Noun(string shown) => shown == "1" ? "credit" : "credits";
}
