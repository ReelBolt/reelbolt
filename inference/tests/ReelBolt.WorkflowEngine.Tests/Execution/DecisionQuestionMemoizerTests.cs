using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

/// <summary>
/// Coverage for <see cref="DecisionQuestionMemoizer"/>: the D4 acceptance — a repeated, unchanged
/// question inside one execution issues no second outbound call, while anything that makes the
/// question a different question (execution, site, offered options, state content) re-asks, and the
/// retained set is bounded both by the cap and by eviction on completion.
/// </summary>
public sealed class DecisionQuestionMemoizerTests
{
    private const string Site = "derush-prefilter";

    private static readonly Guid ExecutionId = Guid.Parse("1f0d6c2a-4b7e-4a1c-9d3f-8e5b7c0a2d41");

    private static readonly DecisionState State = new("shot s1: 3.2s, gentle motion");

    private static readonly DecisionQuestion KeepQuestion = new(
        "keep s1",
        DecisionKind.Noul,
        "Should the editor keep this item?",
        []);

    [Fact]
    public async Task DecideAsync_SecondIdenticalQuestionInSameExecution_DoesNotCallClientAgain()
    {
        var sut = new DecisionQuestionMemoizer();
        var client = new CountingDecisionClient(questions => AnswerAll(questions));

        DecisionResult first = await sut.DecideAsync(
            ExecutionId, Site, State, [KeepQuestion], client, CancellationToken.None);
        DecisionResult second = await sut.DecideAsync(
            ExecutionId, Site, State, [KeepQuestion], client, CancellationToken.None);

        client.CallCount.Should().Be(1);
        second.Answers.Should().ContainKey("keep s1");
        second.Answers["keep s1"].Should().Be(first.Answers["keep s1"]);
        second.Model.Should().Be("memo");
        second.InputTokens.Should().Be(0);
        second.OutputTokens.Should().Be(0);
    }

    [Fact]
    public async Task DecideAsync_DifferentExecution_ReAsks()
    {
        var sut = new DecisionQuestionMemoizer();
        var client = new CountingDecisionClient(questions => AnswerAll(questions));

        await sut.DecideAsync(ExecutionId, Site, State, [KeepQuestion], client, CancellationToken.None);
        await sut.DecideAsync(Guid.NewGuid(), Site, State, [KeepQuestion], client, CancellationToken.None);

        client.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task DecideAsync_ChangedOptionSet_ReAsks()
    {
        var sut = new DecisionQuestionMemoizer();
        var client = new CountingDecisionClient(questions => AnswerAll(questions));

        DecisionQuestion twoOptions = new("keep s1", DecisionKind.Choice, "Keep it?", ["yes", "no"]);
        DecisionQuestion threeOptions = twoOptions with { Options = ["yes", "maybe", "no"] };

        await sut.DecideAsync(ExecutionId, Site, State, [twoOptions], client, CancellationToken.None);
        await sut.DecideAsync(ExecutionId, Site, State, [threeOptions], client, CancellationToken.None);

        client.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task DecideAsync_ChangedStateContent_ReAsks()
    {
        var sut = new DecisionQuestionMemoizer();
        var client = new CountingDecisionClient(questions => AnswerAll(questions));

        var changedState = new DecisionState("shot s1: 3.2s, strong motion");

        await sut.DecideAsync(ExecutionId, Site, State, [KeepQuestion], client, CancellationToken.None);
        await sut.DecideAsync(ExecutionId, Site, changedState, [KeepQuestion], client, CancellationToken.None);

        client.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task DecideAsync_DifferentSite_DoesNotCollide()
    {
        var sut = new DecisionQuestionMemoizer();
        var client = new CountingDecisionClient(questions => AnswerAll(questions));

        await sut.DecideAsync(ExecutionId, "derush-prefilter", State, [KeepQuestion], client, CancellationToken.None);
        await sut.DecideAsync(ExecutionId, "room-questions", State, [KeepQuestion], client, CancellationToken.None);

        client.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Evict_DropsTheEntrySoTheNextIdenticalQuestionReAsks()
    {
        var sut = new DecisionQuestionMemoizer();
        var client = new CountingDecisionClient(questions => AnswerAll(questions));

        await sut.DecideAsync(ExecutionId, Site, State, [KeepQuestion], client, CancellationToken.None);
        sut.RetainedExecutionCount.Should().Be(1);

        sut.Evict(ExecutionId);

        sut.RetainedExecutionCount.Should().Be(0);
        sut.TryGet(ExecutionId, Site, KeepQuestion, State, out _).Should().BeFalse();

        await sut.DecideAsync(ExecutionId, Site, State, [KeepQuestion], client, CancellationToken.None);

        client.CallCount.Should().Be(2);
    }

    [Fact]
    public void Store_BeyondTheExecutionCap_DropsTheOldestAndNeverExceedsTheCap()
    {
        var sut = new DecisionQuestionMemoizer();
        int admitted = DecisionQuestionMemoizer.MaxExecutions + 5;
        Guid[] executionIds = [.. Enumerable.Range(0, admitted).Select(_ => Guid.NewGuid())];

        foreach (Guid executionId in executionIds)
        {
            sut.Store(executionId, Site, KeepQuestion, State, AnswerFor(KeepQuestion));
            sut.RetainedExecutionCount.Should().BeLessThanOrEqualTo(DecisionQuestionMemoizer.MaxExecutions);
        }

        sut.RetainedExecutionCount.Should().Be(DecisionQuestionMemoizer.MaxExecutions);

        // Oldest-first: the five oldest admissions are gone, the newest survives.
        sut.TryGet(executionIds[0], Site, KeepQuestion, State, out _).Should().BeFalse();
        sut.TryGet(executionIds[4], Site, KeepQuestion, State, out _).Should().BeFalse();
        sut.TryGet(executionIds[5], Site, KeepQuestion, State, out DecisionAnswer oldestRetained).Should().BeTrue();
        oldestRetained.Name.Should().Be("keep s1");
        sut.TryGet(executionIds[^1], Site, KeepQuestion, State, out _).Should().BeTrue();
    }

    [Fact]
    public void ComputeKey_IsStableForEqualInputsAndDiffersWhenAnyComponentDiffers()
    {
        DecisionQuestion baseline = new("keep s1", DecisionKind.Choice, "Keep it?", ["yes", "no"]);

        string key = DecisionQuestionMemoizer.ComputeKey(Site, baseline, State);

        key.Should().MatchRegex("^[0-9A-F]{64}$");
        DecisionQuestionMemoizer.ComputeKey(Site, baseline, State).Should().Be(key);

        // Equal-by-value inputs, including a freshly built state and option list.
        DecisionQuestion duplicate = new("keep s1", DecisionKind.Choice, "Keep it?", ["yes", "no"]);
        DecisionQuestionMemoizer.ComputeKey(Site, duplicate, new DecisionState("shot s1: 3.2s, gentle motion"))
            .Should().Be(key);

        DecisionQuestionMemoizer.ComputeKey("other-site", baseline, State).Should().NotBe(key);
        DecisionQuestionMemoizer.ComputeKey(Site, baseline with { Name = "keep s2" }, State).Should().NotBe(key);
        DecisionQuestionMemoizer.ComputeKey(Site, baseline with { Kind = DecisionKind.Score }, State).Should().NotBe(key);
        DecisionQuestionMemoizer.ComputeKey(Site, baseline with { Options = ["yes", "no", "maybe"] }, State).Should().NotBe(key);
        DecisionQuestionMemoizer.ComputeKey(Site, baseline with { Options = ["no", "yes"] }, State).Should().NotBe(key);
        DecisionQuestionMemoizer.ComputeKey(Site, baseline, new DecisionState("shot s1: 3.2s, gentle motions"))
            .Should().NotBe(key);

        // Instructions do not reach the key — only the identity of the question does.
        DecisionQuestionMemoizer.ComputeKey(Site, baseline with { Instructions = "reworded" }, State).Should().Be(key);
    }

    // --- The key is unambiguous: a component containing the separator cannot shift a boundary -----

    [Fact]
    public void ComputeKey_does_not_conflate_component_sets_a_bare_pipe_join_would_collide()
    {
        // Under the old string.Join('|', site, name, ...) both of these produced the SAME material
        // "a|b|c|Noul|...": the '|' inside a value was indistinguishable from the separator between
        // values, so two genuinely different questions shared one memoized answer. Length-prefixing
        // every component is what makes the join injective.
        DecisionQuestion nameWithPipe = new("b|c", DecisionKind.Noul, "Keep it?", []);
        DecisionQuestion siteWithPipe = new("c", DecisionKind.Noul, "Keep it?", []);

        DecisionQuestionMemoizer.ComputeKey("a", nameWithPipe, State)
            .Should().NotBe(
                DecisionQuestionMemoizer.ComputeKey("a|b", siteWithPipe, State),
                "site='a', name='b|c' and site='a|b', name='c' are different questions");
    }

    [Fact]
    public void ComputeKey_does_not_conflate_option_lists_a_bare_pipe_join_would_collide()
    {
        // ["x|y", "z"] and ["x", "y|z"] both flatten to "x|y|z". The option count is prefixed and so
        // is every option, so the flattening can no longer be ambiguous.
        DecisionQuestion mergedFirst = new("keep s1", DecisionKind.Choice, "Keep it?", ["x|y", "z"]);
        DecisionQuestion mergedSecond = new("keep s1", DecisionKind.Choice, "Keep it?", ["x", "y|z"]);

        DecisionQuestionMemoizer.ComputeKey(Site, mergedFirst, State)
            .Should().NotBe(DecisionQuestionMemoizer.ComputeKey(Site, mergedSecond, State));
    }

    [Fact]
    public void A_separator_bearing_store_cannot_be_read_back_as_a_different_question()
    {
        var sut = new DecisionQuestionMemoizer();
        var stored = new DecisionQuestion("b|c", DecisionKind.Noul, "Keep it?", []);
        var mistaken = new DecisionQuestion("c", DecisionKind.Noul, "Keep it?", []);

        sut.Store(ExecutionId, "a", stored, State, AnswerFor(stored));

        sut.TryGet(ExecutionId, "a", stored, State, out _).Should().BeTrue();
        sut.TryGet(ExecutionId, "a|b", mistaken, State, out _).Should().BeFalse();
    }

    [Fact]
    public async Task DecideAsync_MixedHitAndMiss_IssuesExactlyOneCallCarryingOnlyTheMiss()
    {
        var sut = new DecisionQuestionMemoizer();
        var client = new CountingDecisionClient(questions => AnswerAll(questions));

        DecisionQuestion cached = new("keep s1", DecisionKind.Noul, "Keep s1?", []);
        DecisionQuestion miss = new("keep s2", DecisionKind.Noul, "Keep s2?", []);

        await sut.DecideAsync(ExecutionId, Site, State, [cached], client, CancellationToken.None);

        DecisionResult batched = await sut.DecideAsync(
            ExecutionId, Site, State, [cached, miss], client, CancellationToken.None);

        client.CallCount.Should().Be(2);
        client.ReceivedQuestionBatches.Should().HaveCount(2);
        client.ReceivedQuestionBatches[^1].Should().HaveCount(1);
        client.ReceivedQuestionBatches[^1][0].Name.Should().Be("keep s2");

        // The union is returned, and the batch's model/token counts come from the fresh call.
        batched.Answers.Keys.Should().BeEquivalentTo("keep s1", "keep s2");
        batched.Model.Should().Be("stub-model");
        batched.InputTokens.Should().Be(11);
        batched.OutputTokens.Should().Be(4);
    }

    private static DecisionResult AnswerAll(IReadOnlyList<DecisionQuestion> questions, string model = "stub-model")
    {
        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);

        foreach (DecisionQuestion question in questions)
            answers[question.Name] = AnswerFor(question);

        return new DecisionResult(model, answers, 11, 4);
    }

    private static DecisionAnswer AnswerFor(DecisionQuestion question) => question.Kind switch
    {
        DecisionKind.Choice => new DecisionAnswer(
            question.Name,
            question.Kind,
            question.Options[0],
            null,
            null,
            1.0,
            new Dictionary<string, double>(StringComparer.Ordinal) { [question.Options[0]] = 1.0 }),
        DecisionKind.Score => new DecisionAnswer(
            question.Name,
            question.Kind,
            null,
            1.0,
            null,
            1.0,
            new Dictionary<string, double>(StringComparer.Ordinal)),
        DecisionKind.Noul => new DecisionAnswer(
            question.Name,
            question.Kind,
            null,
            null,
            0.93,
            null,
            new Dictionary<string, double>(StringComparer.Ordinal)),
        _ => throw new ArgumentOutOfRangeException(nameof(question), question.Kind, "Unhandled decision kind"),
    };

    /// <summary>
    /// A hand-written call-counting <see cref="IDecisionClient"/>: counting calls is the whole point
    /// of several assertions here, and it is clearer read off a real field than off a mock verifier.
    /// </summary>
    private sealed class CountingDecisionClient : IDecisionClient
    {
        private readonly Func<IReadOnlyList<DecisionQuestion>, DecisionResult> _responder;

        public CountingDecisionClient(Func<IReadOnlyList<DecisionQuestion>, DecisionResult> responder)
        {
            _responder = responder;
        }

        public int CallCount { get; private set; }

        public List<IReadOnlyList<DecisionQuestion>> ReceivedQuestionBatches { get; } = [];

        public Task<DecisionResult> DecideAsync(
            DecisionState state,
            IReadOnlyList<DecisionQuestion> questions,
            CancellationToken ct)
        {
            CallCount++;
            ReceivedQuestionBatches.Add(questions);
            return Task.FromResult(_responder(questions));
        }
    }
}
