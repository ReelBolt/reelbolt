using System;
using System.Globalization;

namespace ReelBolt.WorkflowEngine.Services.VideoGeneration;

/// <summary>
/// Turns a <see cref="BudgetRefusal"/> into the one plain sentence a person reads when a video
/// step fails <c>BUDGET_EXCEEDED</c>: what the request needs, which cap refused (in words), its
/// value, what is already used today, what is left and when the day resets. Never empty.
/// </summary>
public static class VideoBudgetMessages
{
    public const string Code = "BUDGET_EXCEEDED";

    /// <summary>Where a person changes or removes their own daily video limit (the web app's Usage settings page).</summary>
    public const string UserLimitSettingsPath = "Settings > Usage > Daily video limit (/settings/usage)";

    /// <summary>Used when a denial arrives with the code but no text, so the failure is never blank.</summary>
    public const string GenericSentence =
        "The video generation budget for this step or day is used up. Generate fewer clips or wait for the daily reset at 00:00 UTC.";

    public static string Describe(BudgetRefusal refusal, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        bool gpu = refusal.Unit == VideoGenerationCostUnit.GpuSeconds;
        string Amount(decimal v)
        {
            // GPU figures are stored in seconds; people think in minutes.
            decimal shown = gpu ? v / 60m : v;
            return shown.ToString("0.##", CultureInfo.InvariantCulture);
        }

        string Money(decimal v)
        {
            string number = Amount(v);
            return gpu
                ? $"{number} GPU-{(number == "1" ? "minute" : "minutes")}"
                : $"{number} {(number == "1" ? "credit" : "credits")}";
        }

        // The platform-wide cap and its usage are other tenants' business: say the limit was reached
        // and when it resets, never a global figure.
        if (refusal.Cap == BudgetCapKind.GlobalDaily && refusal.ResetsAtUtc is DateTime globalResets)
        {
            return $"Video generation has reached the platform's daily limit; it resets at 00:00 UTC (in {Until(globalResets, nowUtc)}).";
        }

        // A personal limit and the owner's cross-project use are private to the person who set it: this sentence
        // is stored as the step error and shown to every project member, so it names no figure at all (no limit,
        // no used amount, no room left). The initiator sees their own figures on the Usage settings card.
        if (refusal.Cap == BudgetCapKind.UserDaily)
        {
            string tail = refusal.ResetsAtUtc is DateTime userResets
                ? $" It resets at 00:00 UTC (in {Until(userResets, nowUtc)})."
                : string.Empty;
            return $"A personal daily video limit was reached for this run.{tail} Generate fewer clips, wait, or the person who started the run can change or remove their limit in {UserLimitSettingsPath}.";
        }

        string capName = refusal.Cap switch
        {
            BudgetCapKind.StepLimit => gpu ? "this step's GPU-minutes limit" : "this step's spending limit",
            BudgetCapKind.ProjectDaily => gpu ? "the daily GPU-minutes budget" : "the project's daily video budget",
            _ => gpu ? "the platform's daily GPU-minutes budget" : "the platform's daily video budget",
        };

        var sentence = $"This needs about {Money(refusal.Estimated)} of video generation but {capName} is {Money(refusal.CapValue)}";
        if (refusal.Cap != BudgetCapKind.StepLimit && refusal.SpentToday > 0m)
        {
            sentence += $" and {Amount(refusal.SpentToday)} {(Amount(refusal.SpentToday) == "1" ? "is" : "are")} already used today";
        }

        // QA-P0-17: the room left under the cap that refused (a step limit has no "used": all of it is left).
        decimal spent = refusal.Cap == BudgetCapKind.StepLimit ? 0m : refusal.SpentToday;
        sentence += $" ({Money(Math.Max(0m, refusal.CapValue - spent))} left)";

        if (refusal.ResetsAtUtc is DateTime resets)
        {
            sentence += $"; it resets at 00:00 UTC (in {Until(resets, nowUtc)}). Generate fewer clips or wait.";
        }
        else
        {
            sentence += ". Generate fewer clips or raise the step's limit.";
        }

        return sentence;
    }

    /// <summary>Whole minutes, rounded up, at least one: "5 h 12 min", "12 min", "1 min".</summary>
    private static string Until(DateTime resetsUtc, DateTime nowUtc)
    {
        int totalMinutes = Math.Max(1, (int)Math.Ceiling((resetsUtc - nowUtc).TotalMinutes));
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return hours == 0 ? $"{minutes} min" : minutes == 0 ? $"{hours} h" : $"{hours} h {minutes} min";
    }

    /// <summary>The reset instant of a daily cap: the next 00:00 UTC after <paramref name="nowUtc"/>.</summary>
    public static DateTime NextResetUtc(DateTime nowUtc) => nowUtc.Date.AddDays(1);
}
