using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>Which model drives the assistant. The key is read from the environment and never leaves this process.</summary>
internal sealed class EvalModelSpec
{
    public required InferenceProviderKind Kind { get; init; }
    public required string Model { get; init; }
    public string? Endpoint { get; init; }
    public required string ApiKeyEnvVar { get; init; }
    public decimal? InputUsdPerToken { get; init; }
    public decimal? OutputUsdPerToken { get; init; }

    /// <summary>Reads REELBOLT_EVAL_PROVIDER / _MODEL / _ENDPOINT / _API_KEY_ENV / _PRICE_IN_PER_MTOK / _PRICE_OUT_PER_MTOK.</summary>
    public static EvalModelSpec FromEnvironment()
    {
        string providerName = Env("REELBOLT_EVAL_PROVIDER") ?? "DeepSeek";
        if (!Enum.TryParse(providerName, ignoreCase: true, out InferenceProviderKind kind))
            throw new InvalidOperationException($"REELBOLT_EVAL_PROVIDER '{providerName}' is not an InferenceProviderKind.");
        string model = Env("REELBOLT_EVAL_MODEL") ?? throw new InvalidOperationException("REELBOLT_EVAL_MODEL is required.");
        string keyVar = Env("REELBOLT_EVAL_API_KEY_ENV") ?? kind switch
        {
            InferenceProviderKind.DeepSeek => "DEEPSEEK_API_KEY",
            InferenceProviderKind.Anthropic => "ANTHROPIC_API_KEY",
            InferenceProviderKind.Gemini => "GEMINI_API_KEY",
            _ => "OPENAI_API_KEY",
        };
        return new EvalModelSpec
        {
            Kind = kind,
            Model = model,
            Endpoint = Env("REELBOLT_EVAL_ENDPOINT"),
            ApiKeyEnvVar = keyVar,
            InputUsdPerToken = Price("REELBOLT_EVAL_PRICE_IN_PER_MTOK"),
            OutputUsdPerToken = Price("REELBOLT_EVAL_PRICE_OUT_PER_MTOK")
        };
    }

    public IChatClient CreateClient()
    {
        string key = Env(ApiKeyEnvVar) ?? "";
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Environment variable {ApiKeyEnvVar} is empty or unset; the eval cannot call the model.");
        var resolved = new ResolvedInferenceProvider(null, "assistant-eval", Kind, Endpoint ?? "", Model, key, TimeoutSeconds: 180);
        return new ChatClientFactory().Get(resolved);
    }

    /// <summary>USD per token (input, output): explicit override, else the repository price catalog, else null.</summary>
    public (decimal In, decimal Out, string Basis)? Prices()
    {
        if (InputUsdPerToken is { } i && OutputUsdPerToken is { } o) return (i, o, "environment override");
        ModelPrice? row = ModelPriceCatalog.All.FirstOrDefault(m =>
            m.ProviderKind == Kind && m.Capability == InferenceProviderCapability.Chat
            && string.Equals(m.Model, Model, StringComparison.OrdinalIgnoreCase) && m.IsPriced);
        return row is null ? null
            : (row.UsdPerUnit[UsageKind.LlmInputTokens], row.UsdPerUnit[UsageKind.LlmOutputTokens], row.Basis);
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static decimal? Price(string name) =>
        Env(name) is { } v && decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal perMillion)
            ? perMillion / 1_000_000m : null;
}

internal sealed record EvalSampleResult(
    string GoalId, int Sample, EvalObservation Observation, EvalSampleScore Score,
    string Reply, IReadOnlyList<EvalToolCall> Calls, long InputTokens, long OutputTokens, int ModelRequests,
    double Seconds, string? Error);

internal sealed record EvalRunResult(
    IReadOnlyList<EvalGoal> Goals, IReadOnlyList<EvalSampleResult> Samples, int SamplesPerGoal,
    string Provider, string Model, string? PriceBasis, decimal? InUsdPerToken, decimal? OutUsdPerToken,
    bool StoppedEarly, string? StopReason, DateTime StartedUtc, double WallSeconds, string DocsRetrieval)
{
    public long InputTokens => Samples.Sum(s => s.InputTokens);
    public long OutputTokens => Samples.Sum(s => s.OutputTokens);
    public decimal? EstimatedUsd =>
        InUsdPerToken is { } i && OutUsdPerToken is { } o ? InputTokens * i + OutputTokens * o : null;
}

internal static class EvalRunner
{
    /// <summary>Runs ONE sample: a fresh stub project, the real assistant agent, the goal as the only user message.</summary>
    public static async Task<EvalSampleResult> RunSampleAsync(
        EvalGoal goal, int sample, IChatClient model, IPlatformDocsSearch docs, TimeSpan timeout, CancellationToken ct,
        EvalBudget? budget = null)
    {
        using EvalStubWorld world = EvalStubWorld.Build(goal, docs);
        var transcript = new EvalTranscript();
        var recorder = new RecordingChatClient(model, transcript, budget);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            string? docsBlock = await world.DocsContext.BuildAsync(goal.Goal, cts.Token);
            string suffix = docsBlock is null ? world.SessionBlock : world.SessionBlock + "\n\n" + docsBlock;
            var agent = new AssistantAgent(
                new FixedChatClientProvider(recorder), new ConfigurationBuilder().Build(), world.Provider, world.Context, suffix);
            AgentRunResult result = await agent.RunAsync([new ChatMessage(ChatRole.User, goal.Goal)], ct: cts.Token);
            transcript.Reply = result.Output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            transcript.Error = ex is EvalBudgetExhaustedException ? "budget exhausted"
                : ex is OperationCanceledException ? "sample timed out" : EvalErrors.Scrub(ex);
        }

        EvalObservation obs = EvalScoring.Observe(transcript, world.AgentTypeOf);
        if (obs.FirstProposalStepsJson is { } stepsJson)
        {
            // Lint recall: the real linter over the FIRST accepted proposal, with the stub project's facts.
            IReadOnlySet<string> ids = await EvalLinting.LintAsync(
                world.Project, world.VoiceId, stepsJson,
                world.Db.AgentDefinitions.AsNoTracking().ToDictionary(a => a.Id), obs.Brief, CancellationToken.None);
            obs = obs with { LintIds = obs.LintIds.Union(ids).ToHashSet(StringComparer.Ordinal) };
        }
        EvalSampleScore score = EvalScoring.Score(goal, obs, transcript.Reply);
        return new EvalSampleResult(goal.Id, sample, obs, score, transcript.Reply, transcript.Calls,
            transcript.InputTokens, transcript.OutputTokens, transcript.ModelRequests, sw.Elapsed.TotalSeconds, transcript.Error);
    }

    /// <summary>
    /// Runs every goal <paramref name="samples"/> times, sample-major (all goals once, then all goals again), so a run
    /// stopped by the budget still covers every goal. Stops starting new samples once the estimated spend reaches
    /// the budget and says so in the result.
    /// </summary>
    public static async Task<EvalRunResult> RunAsync(
        IReadOnlyList<EvalGoal> goals, int samples, EvalModelSpec spec, IChatClient model, IPlatformDocsSearch docs,
        EvalBudget budget, int parallelism, TimeSpan sampleTimeout, string? transcriptDir, CancellationToken ct,
        string docsRetrieval = "BM25 lexical over docs/ (production: dense+BM25+rerank)")
    {
        var prices = spec.Prices();
        var results = new List<EvalSampleResult>();
        string? stop = null;
        var gate = new object();
        var started = DateTime.UtcNow;
        var wall = Stopwatch.StartNew();
        using var sem = new SemaphoreSlim(Math.Clamp(parallelism, 1, EvalSettings.MaxParallelism));
        if (transcriptDir is not null) Directory.CreateDirectory(transcriptDir);

        for (int s = 1; s <= samples && stop is null; s++)
        {
            var tasks = goals.Select(async goal =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    lock (gate)
                    {
                        if (stop is not null) return;
                        if (budget.Exhausted)
                        {
                            stop = budget.Reason;
                            return;
                        }
                    }
                    EvalSampleResult r = await RunSampleAsync(goal, s, model, docs, sampleTimeout, ct, budget);
                    lock (gate)
                    {
                        results.Add(r);
                        if (budget.Exhausted) stop ??= budget.Reason;
                    }
                    if (transcriptDir is not null)
                        await File.AppendAllTextAsync(Path.Combine(transcriptDir, "samples.jsonl"),
                            JsonSerializer.Serialize(new { r.GoalId, r.Sample, r.Reply, r.Calls, r.InputTokens, r.OutputTokens, r.Error }) + "\n", ct);
                }
                finally { sem.Release(); }
            }).ToList();
            await Task.WhenAll(tasks);
        }

        return new EvalRunResult(goals, results.OrderBy(r => r.GoalId, StringComparer.Ordinal).ThenBy(r => r.Sample).ToList(), samples,
            spec.Kind.ToString(), spec.Model, prices?.Basis, prices?.In, prices?.Out, stop is not null, stop, started,
            wall.Elapsed.TotalSeconds, docsRetrieval);
    }
}
