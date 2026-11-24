using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

internal sealed record EvalRate(string Name, int Passed, int Total, double Target, double Low, double High)
{
    public double Value => Total == 0 ? 0 : (double)Passed / Total;
    public bool MeetsTarget => Total > 0 && Value >= Target;
}

internal sealed record EvalGoalResult(
    EvalGoal Goal, int Samples, string MajorityRoute, string? MajorityTemplate, bool RouteOk, bool? TemplateOk,
    IReadOnlyDictionary<string, bool> BriefFields, int BriefExtras, bool BriefExact, IReadOnlyDictionary<string, bool> Lint,
    bool QuestionPolicyOk, bool UnneededQuestion, bool? PaidDisclosed,
    int HallucinatedSamples, int UnrequestedPaidSamples, int IdShownSamples,
    long InputTokens, long OutputTokens, int ToolCalls, int Errors, IReadOnlyList<string> Questions);

/// <summary>Majority-of-samples aggregation (eval-goals.md "Scoring") plus the JSON and markdown renderings.</summary>
internal static class EvalReport
{
    public static IReadOnlyList<EvalGoalResult> PerGoal(EvalRunResult run)
    {
        var list = new List<EvalGoalResult>();
        foreach (EvalGoal goal in run.Goals)
        {
            List<EvalSampleResult> s = run.Samples.Where(x => x.GoalId == goal.Id).ToList();
            if (s.Count == 0) continue;
            int n = s.Count;
            bool Maj(Func<EvalSampleScore, bool> f) => EvalStats.Majority(s.Count(x => f(x.Score)), n);

            var brief = new Dictionary<string, bool>();
            foreach (string f in goal.Expected.Brief.Keys.Where(k => EvalScoring.BriefFieldNames.Contains(k)))
                brief[f] = EvalStats.Majority(s.Count(x => x.Score.BriefFields.TryGetValue(f, out bool ok) && ok), n);
            var lint = goal.Expected.Lint.ToDictionary(r => r,
                r => EvalStats.Majority(s.Count(x => x.Score.Lint.TryGetValue(r, out bool ok) && ok), n));

            bool? template = goal.IsPlanned || goal.Expected.Route is not ("template" or "template_with_patch")
                ? null : Maj(x => x.TemplateOk == true);
            bool? paid = goal.Expected.Paid ? Maj(x => x.PaidDisclosed == true) : null;
            string route = s.GroupBy(x => x.Observation.Route).OrderByDescending(g => g.Count()).First().Key;
            string? tpl = s.Select(x => x.Observation.TemplateKey).Where(k => k is not null)
                .GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

            list.Add(new EvalGoalResult(goal, n, route, tpl, Maj(x => x.RouteOk), template, brief,
                Majority(s.Count(x => x.Score.BriefExtras > 0), n) ? 1 : 0,
                Maj(x => !x.Failed && x.BriefFields.Values.All(v => v) && x.BriefExtras == 0), lint,
                Maj(x => x.QuestionPolicyOk), Maj(x => x.UnneededQuestion), paid,
                s.Count(x => x.Score.Hallucinated), s.Count(x => x.Score.UnrequestedPaid), s.Count(x => x.Score.IdShown),
                s.Sum(x => x.InputTokens), s.Sum(x => x.OutputTokens), s.Sum(x => x.Calls.Count), s.Count(x => x.Error is not null),
                goal.EffectiveRoute == "clarify_first" ? s.Take(1).SelectMany(x => Sentences(x.Reply)).ToList() : []));
        }
        return list;
    }

    private static bool Majority(int passes, int n) => EvalStats.Majority(passes, n);

    private static IEnumerable<string> Sentences(string reply) =>
        reply.Split(['\n', '.', '!'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Contains('?'));

    public static IReadOnlyList<EvalRate> Rates(IReadOnlyList<EvalGoalResult> goals)
    {
        EvalRate Rate(string name, int k, int n, double target)
        {
            (double lo, double hi) = EvalStats.Wilson(k, n);
            return new EvalRate(name, k, n, target, lo, hi);
        }

        var rates = new List<EvalRate>
        {
            Rate("Route accuracy", goals.Count(g => g.RouteOk), goals.Count, 0.85),
            Rate("Template accuracy (template and template_with_patch goals)",
                goals.Count(g => g.TemplateOk == true), goals.Count(g => g.TemplateOk is not null), 0.80),
        };
        foreach (string f in EvalScoring.BriefFieldNames)
        {
            var with = goals.Where(g => g.BriefFields.ContainsKey(f)).ToList();
            if (with.Count > 0)
                rates.Add(Rate($"Brief field '{f}'", with.Count(g => g.BriefFields[f]), with.Count, f is "language" or "format" ? 0.95 : 0));
        }
        int fieldN = goals.Sum(g => g.BriefFields.Count);
        rates.Add(Rate("Brief fields, all (extras not counted)", goals.Sum(g => g.BriefFields.Count(x => x.Value)), fieldN, 0));
        rates.Add(Rate("Brief, goal level (every expected field right and no field the fixture omits; an invented field is one miss)",
            goals.Count(g => g.BriefExact), goals.Count, 0));
        rates.Add(Rate("Goals with no brief field the fixture omits (no hallucinated field)",
            goals.Count(g => g.BriefExtras == 0), goals.Count, 0));
        rates.Add(Rate("Lint recall (expected rule ids seen)", goals.Sum(g => g.Lint.Count(x => x.Value)), goals.Sum(g => g.Lint.Count), 0.90));
        rates.Add(Rate("Question policy (asked iff clarify_first, at most two)", goals.Count(g => g.QuestionPolicyOk), goals.Count, 0));
        rates.Add(Rate("Paid disclosure (heuristic wording check)", goals.Count(g => g.PaidDisclosed == true), goals.Count(g => g.PaidDisclosed is not null), 1.0));
        return rates;
    }

    public static string ToJson(EvalRunResult run)
    {
        IReadOnlyList<EvalGoalResult> goals = PerGoal(run);
        var o = new
        {
            meta = Meta(run),
            rates = Rates(goals).Select(r => new { r.Name, r.Passed, r.Total, value = Math.Round(r.Value, 4), r.Target, wilsonLow = Math.Round(r.Low, 4), wilsonHigh = Math.Round(r.High, 4), meetsTarget = r.Target > 0 ? r.MeetsTarget : (bool?)null }),
            zeroTolerance = new
            {
                hallucinatedTemplateKey = new { goals = goals.Count(g => g.HallucinatedSamples > 0), samples = goals.Sum(g => g.HallucinatedSamples) },
                unrequestedPaidStep = new { goals = goals.Count(g => g.UnrequestedPaidSamples > 0), samples = goals.Sum(g => g.UnrequestedPaidSamples) },
                idShown = new { goals = goals.Count(g => g.IdShownSamples > 0), samples = goals.Sum(g => g.IdShownSamples) },
            },
            unneededQuestionGoals = goals.Count(g => g.UnneededQuestion),
            goals = goals.Select(g => new
            {
                id = g.Goal.Id,
                expectedRoute = g.Goal.EffectiveRoute,
                observedRoute = g.MajorityRoute,
                expectedTemplate = g.Goal.Expected.TemplateKey,
                observedTemplate = g.MajorityTemplate,
                g.RouteOk,
                g.TemplateOk,
                brief = g.BriefFields,
                g.BriefExtras,
                lint = g.Lint,
                g.QuestionPolicyOk,
                g.PaidDisclosed,
                g.HallucinatedSamples,
                g.UnrequestedPaidSamples,
                g.IdShownSamples,
                g.InputTokens,
                g.OutputTokens,
                g.ToolCalls,
                g.Errors,
                g.Questions
            })
        };
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object Meta(EvalRunResult run) => new
    {
        provider = run.Provider,
        model = run.Model,
        samplesPerGoal = run.SamplesPerGoal,
        goals = run.Goals.Count,
        samplesRun = run.Samples.Count,
        startedUtc = run.StartedUtc,
        wallSeconds = Math.Round(run.WallSeconds, 1),
        inputTokens = run.InputTokens,
        outputTokens = run.OutputTokens,
        estimatedUsd = run.EstimatedUsd is { } u ? Math.Round(u, 4) : (decimal?)null,
        priceBasis = run.PriceBasis,
        stoppedEarly = run.StoppedEarly,
        stopReason = run.StopReason,
        docsRetrieval = run.DocsRetrieval,
        modelRequests = run.Samples.Sum(s => s.ModelRequests),
        toolCalls = run.Samples.Sum(s => s.Calls.Count),
        sampleErrors = run.Samples.Count(s => s.Error is not null)
    };

    public static string ToMarkdown(EvalRunResult run)
    {
        IReadOnlyList<EvalGoalResult> goals = PerGoal(run);
        var sb = new StringBuilder();
        string P(double v) => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        sb.AppendLine($"Run: provider {run.Provider}, model {run.Model}, {run.Samples.Count} samples over {run.Goals.Count} goals ({run.SamplesPerGoal} per goal), started {run.StartedUtc:yyyy-MM-dd HH:mm} UTC.");
        sb.AppendLine($"Tokens: {run.InputTokens:N0} in, {run.OutputTokens:N0} out; model requests {run.Samples.Sum(s => s.ModelRequests):N0}; tool calls {run.Samples.Sum(s => s.Calls.Count):N0}; sample errors {run.Samples.Count(s => s.Error is not null)}.");
        sb.AppendLine(run.EstimatedUsd is { } usd
            ? $"Estimated cost: USD {usd.ToString("0.00", CultureInfo.InvariantCulture)} ({run.PriceBasis})."
            : "Estimated cost: not computed (no price for this model in the catalog and no override).");
        if (run.StoppedEarly) sb.AppendLine($"STOPPED EARLY: {run.StopReason}. Metrics cover the samples that ran.");
        sb.AppendLine($"Documentation retrieval: {run.DocsRetrieval}.");
        sb.AppendLine();
        sb.AppendLine("| Metric | Passed | Rate | 95 % Wilson | Target | Met |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (EvalRate r in Rates(goals))
            sb.AppendLine($"| {r.Name} | {r.Passed}/{r.Total} | {P(r.Value)} | {P(r.Low)} to {P(r.High)} | {(r.Target > 0 ? ">= " + P(r.Target) : "-")} | {(r.Target > 0 ? (r.MeetsTarget ? "yes" : "no") : "-")} |");
        sb.AppendLine();
        sb.AppendLine("Zero tolerance (any sample): "
            + $"hallucinated template key {goals.Sum(g => g.HallucinatedSamples)} samples in {goals.Count(g => g.HallucinatedSamples > 0)} goals; "
            + $"unrequested paid step {goals.Sum(g => g.UnrequestedPaidSamples)} samples in {goals.Count(g => g.UnrequestedPaidSamples > 0)} goals; "
            + $"id shown {goals.Sum(g => g.IdShownSamples)} samples in {goals.Count(g => g.IdShownSamples > 0)} goals. "
            + $"Unneeded questions on {goals.Count(g => g.UnneededQuestion)} goals (target <= 2).");
        sb.AppendLine();
        sb.AppendLine("| Goal | Expected route | Observed (majority) | Template expected / observed | Tokens in/out | Tool calls |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (EvalGoalResult g in goals)
            sb.AppendLine($"| {g.Goal.Id} | {g.Goal.EffectiveRoute} | {g.MajorityRoute}{(g.RouteOk ? "" : " (miss)")} | {g.Goal.Expected.TemplateKey ?? "-"} / {g.MajorityTemplate ?? "-"}{(g.TemplateOk == false ? " (miss)" : "")} | {g.InputTokens:N0} / {g.OutputTokens:N0} | {g.ToolCalls} |");
        return sb.ToString();
    }
}
