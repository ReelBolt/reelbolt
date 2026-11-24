using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>The eval run's spend, path and error-text guards (security review SEC-WAB-AEVAL-1 to 4).</summary>
public class EvalGuardTests
{
    private static EvalModelSpec Unpriced() => new()
    {
        Kind = InferenceProviderKind.OpenAICompatible,
        Model = "no-such-priced-model",
        ApiKeyEnvVar = "X_KEY"
    };

    private static Func<string, string?> Env(params (string K, string V)[] pairs) =>
        k => pairs.Where(p => p.K == k).Select(p => p.V).FirstOrDefault();

    [Fact]
    public void An_unpriced_model_refuses_to_start_unless_a_token_cap_or_a_price_is_given()
    {
        Action bare = () => EvalSettings.From(Env(), Unpriced());
        bare.Should().Throw<InvalidOperationException>().WithMessage("*Refusing to start*");

        EvalSettings.From(Env(("REELBOLT_EVAL_STOP_TOKENS", "1000000")), Unpriced()).Budget.Should().NotBeNull();

        EvalModelSpec priced = new()
        {
            Kind = InferenceProviderKind.OpenAICompatible,
            Model = "m",
            ApiKeyEnvVar = "X_KEY",
            InputUsdPerToken = 0.000001m,
            OutputUsdPerToken = 0.000002m
        };
        EvalSettings.From(Env(), priced).Budget.Should().NotBeNull();
    }

    [Fact]
    public void Parallelism_label_and_output_directory_are_bounded()
    {
        var tokens = ("REELBOLT_EVAL_STOP_TOKENS", "1000");
        EvalSettings.From(Env(tokens, ("REELBOLT_EVAL_PARALLEL", "64")), Unpriced()).Parallelism.Should().Be(4);
        EvalSettings.From(Env(tokens, ("REELBOLT_EVAL_PARALLEL", "0")), Unpriced()).Parallelism.Should().Be(1);

        foreach (string bad in new[] { "../x", "Upper", "a b", "", "x/y" }.Where(b => b.Length > 0))
        {
            Action a = () => EvalSettings.From(Env(tokens, ("REELBOLT_EVAL_LABEL", bad)), Unpriced());
            a.Should().Throw<InvalidOperationException>().WithMessage("*REELBOLT_EVAL_LABEL*");
        }

        Action outside = () => EvalSettings.From(Env(tokens, ("REELBOLT_EVAL_OUT", "/etc/eval-out")), Unpriced());
        outside.Should().Throw<InvalidOperationException>().WithMessage("*REELBOLT_EVAL_OUT*");
        string ok = Path.Combine(Path.GetTempPath(), "eval-out-test");
        EvalSettings.From(Env(tokens, ("REELBOLT_EVAL_OUT", ok), ("REELBOLT_EVAL_LABEL", "after")), Unpriced())
            .OutDir.Should().Be(Path.Combine(ok, "after"));
    }

    [Fact]
    public void The_budget_reserves_the_worst_case_before_a_request_and_keeps_it_when_usage_is_missing()
    {
        var budget = new EvalBudget(stopUsd: null, stopTokens: 10_000, null, null);
        EvalBudget.Reservation? first = budget.TryReserve(3_000, 4_000);
        first.Should().NotBeNull();
        budget.Settle(first!, null, null); // failed request: estimate stays
        budget.Tokens.Should().Be(7_000);

        budget.TryReserve(1_000, 4_000).Should().BeNull("7,000 + 5,000 would cross the cap");
        budget.Exhausted.Should().BeTrue();

        var usd = new EvalBudget(stopUsd: 1m, stopTokens: long.MaxValue, 0.0001m, 0.0001m);
        usd.TryReserve(4_000, 4_000).Should().NotBeNull();      // 0.80
        usd.TryReserve(2_000, 1_000).Should().BeNull();        // 0.80 + 0.30 > 1
    }

    [Fact]
    public async Task A_failed_request_still_counts_and_the_next_request_is_refused_at_the_cap()
    {
        var budget = new EvalBudget(null, 30_000, null, null);
        var transcript = new EvalTranscript();
        var throwing = new ThrowingClient();
        var client = new RecordingChatClient(throwing, transcript, budget);
        var msg = new[] { new ChatMessage(ChatRole.User, "hi") };

        Func<Task> first = () => client.GetResponseAsync(msg);
        await first.Should().ThrowAsync<HttpRequestException>();
        budget.Tokens.Should().BeGreaterThan(EvalBudget.MaxOutputTokens, "the worst case was reserved");
        throwing.LastMaxOutputTokens.Should().Be(EvalBudget.MaxOutputTokens, "the output cap is sent to the provider");

        Func<Task> again = async () => { for (int i = 0; i < 5; i++) await SwallowAsync(client, msg); };
        await again.Should().ThrowAsync<EvalBudgetExhaustedException>();
    }

    [Fact]
    public async Task Provider_error_text_is_reduced_to_a_type_and_a_status_code_in_the_sample()
    {
        EvalGoal goal = EvalFixture.Load().Single(g => g.Id == "G13");
        var model = new ThrowingClient("secret-prompt-echo sk-should-never-appear");
        var docs = new Bm25DocsSearch(Bm25DocsSearch.LocateDocsRoot());
        EvalSampleResult r = await EvalRunner.RunSampleAsync(goal, 1, model, docs, TimeSpan.FromMinutes(1), CancellationToken.None);
        r.Error.Should().NotBeNull();
        r.Error.Should().NotContain("secret").And.NotContain("sk-");
        r.Error.Should().Contain("HttpRequestException").And.Contain("429");
        EvalErrors.Scrub(new InvalidOperationException("leaky text")).Should().Be("InvalidOperationException");
    }

    private static async Task SwallowAsync(IChatClient c, ChatMessage[] m)
    {
        try { await c.GetResponseAsync(m); }
        catch (HttpRequestException) { }
    }

    private sealed class ThrowingClient(string message = "boom") : IChatClient
    {
        public int? LastMaxOutputTokens { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMaxOutputTokens = options?.MaxOutputTokens;
            throw new HttpRequestException(message, null, HttpStatusCode.TooManyRequests);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
