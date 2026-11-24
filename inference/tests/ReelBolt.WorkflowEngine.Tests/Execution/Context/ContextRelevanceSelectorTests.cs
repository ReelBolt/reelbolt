using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution.Context;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Context;

/// <summary>
/// Covers <see cref="ContextRelevanceSelector"/> — the optional Decision-model pass that ranks the
/// droppable prior-step parts of an over-budget agent prompt. No test here touches a network: the
/// resolver, the decision client and the project-file workspace are all mocks.
///
/// <para>
/// The load-bearing assertions are the degradation ones. "Disabled", "no Decision provider
/// configured", "the client threw", "the answer was unusable" and "the model was not confident"
/// must all be indistinguishable from each other and from the feature never having existed: a null
/// ordering, no exception, and nothing else touched.
/// </para>
/// </summary>
public class ContextRelevanceSelectorTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ContextRelevanceOptions EnabledOptions() => new()
    {
        Enabled = true,
        AcceptAt = 0.6,
        MaxCandidates = 12,
        MaxStateChars = 4_000
    };

    private static List<ContextRelevanceCandidate> StepCandidates(params int[] stepOrders) =>
        stepOrders
            .Select(order => new ContextRelevanceCandidate(
                Key: $"step:{order}",
                Kind: ContextRelevanceCandidateKind.Step,
                StepOrder: order,
                Label: $"Step {order}",
                Meta: "500 chars",
                Descriptor: $"{{\"step\":{order},\"summary\":\"some prior step output\"}}"))
            .ToList();

    private static DecisionAnswer ChoiceAnswer(string name, string choice, double keepProbability, double confidence) =>
        new(
            name,
            DecisionKind.Choice,
            choice,
            Score: null,
            Noul: null,
            Confidence: confidence,
            Probabilities: new Dictionary<string, double>
            {
                ["Keep"] = keepProbability,
                ["Drop"] = 1.0 - keepProbability
            });

    private static DecisionResult Result(params DecisionAnswer[] answers) =>
        new("test-model", answers.ToDictionary(a => a.Name, a => a), 10, 5);

    private static ResolvedDecisionProvider Provider() =>
        new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "OpenJev local",
            InferenceProviderKind.OpenJev,
            "http://localhost:8099",
            "jev-small",
            string.Empty,
            30);

    private static ContextRelevanceSelector CreateSelector(
        ContextRelevanceOptions? options = null,
        ResolvedDecisionProvider? provider = null,
        Func<DecisionState, IReadOnlyList<DecisionQuestion>, CancellationToken, Task<DecisionResult>>? decide = null,
        Func<Guid, CancellationToken, Task<IReadOnlyList<ProjectWorkspaceFile>>>? listFiles = null,
        Mock<IInferenceProviderResolver>? resolverMock = null,
        Mock<IDecisionClient>? clientMock = null)
    {
        Mock<IInferenceProviderResolver> resolver = resolverMock ?? new Mock<IInferenceProviderResolver>();
        if (resolverMock is null)
        {
            resolver
                .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(provider);
        }

        Mock<IDecisionClient> client = clientMock ?? new Mock<IDecisionClient>();
        if (decide is not null)
        {
            client
                .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
                .Returns((DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct) =>
                    decide(state, questions, ct));
        }

        var factory = new Mock<IDecisionClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>())).Returns(client.Object);

        var files = new Mock<IProjectFileWorkspace>();
        files
            .Setup(f => f.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid projectId, CancellationToken ct) => listFiles is null
                ? Task.FromResult<IReadOnlyList<ProjectWorkspaceFile>>([])
                : listFiles(projectId, ct));

        return new ContextRelevanceSelector(
            resolver.Object,
            factory.Object,
            files.Object,
            Options.Create(options ?? EnabledOptions()),
            NullLogger<ContextRelevanceSelector>.Instance);
    }

    [Fact]
    public async Task Disabled_makes_no_provider_lookup_and_returns_null()
    {
        ContextRelevanceOptions options = EnabledOptions();
        options.Enabled = false;

        var resolver = new Mock<IInferenceProviderResolver>(MockBehavior.Strict);
        ContextRelevanceSelector selector = CreateSelector(options: options, resolverMock: resolver);

        ContextRelevanceOrdering? ordering = await selector.SelectDropOrderAsync(
            ProjectId, StepCandidates(1, 2, 3), CancellationToken.None);

        ordering.Should().BeNull();
        resolver.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Fewer_than_two_droppable_candidates_makes_no_provider_lookup()
    {
        var resolver = new Mock<IInferenceProviderResolver>(MockBehavior.Strict);
        ContextRelevanceSelector selector = CreateSelector(resolverMock: resolver);

        (await selector.SelectDropOrderAsync(ProjectId, StepCandidates(1), CancellationToken.None)).Should().BeNull();
        (await selector.SelectDropOrderAsync(ProjectId, [], CancellationToken.None)).Should().BeNull();

        resolver.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task No_decision_provider_configured_is_a_first_class_null_result()
    {
        var factory = new Mock<IDecisionClientFactory>(MockBehavior.Strict);
        var files = new Mock<IProjectFileWorkspace>(MockBehavior.Strict);

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDecisionProvider?)null);

        var selector = new ContextRelevanceSelector(
            resolver.Object,
            factory.Object,
            files.Object,
            Options.Create(EnabledOptions()),
            NullLogger<ContextRelevanceSelector>.Instance);

        ContextRelevanceOrdering? ordering = await selector.SelectDropOrderAsync(
            ProjectId, StepCandidates(1, 2, 3), CancellationToken.None);

        ordering.Should().BeNull();
        factory.Invocations.Should().BeEmpty();
        files.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Sends_exactly_one_call_with_one_two_option_choice_question_per_candidate()
    {
        DecisionState? capturedState = null;
        IReadOnlyList<DecisionQuestion>? capturedQuestions = null;
        int calls = 0;

        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .Callback((DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken _) =>
            {
                calls++;
                capturedState = state;
                capturedQuestions = questions;
            })
            .ReturnsAsync(Result(
                ChoiceAnswer("step:1", "Keep", 0.9, 0.9),
                ChoiceAnswer("step:2", "Drop", 0.1, 0.9),
                ChoiceAnswer("step:3", "Keep", 0.55, 0.7)));

        ContextRelevanceSelector selector = CreateSelector(provider: Provider(), clientMock: client);

        ContextRelevanceOrdering? ordering = await selector.SelectDropOrderAsync(
            ProjectId, StepCandidates(1, 2, 3), CancellationToken.None);

        calls.Should().Be(1);
        capturedQuestions.Should().NotBeNull();
        capturedQuestions!.Should().HaveCount(3);
        capturedQuestions.Should().OnlyContain(q => q.Kind == DecisionKind.Choice);
        capturedQuestions.Select(q => q.Options).Should().AllSatisfy(o => o.Should().Equal("Keep", "Drop"));
        capturedQuestions.Select(q => q.Name).Should().Equal("step:1", "step:2", "step:3");

        capturedState.Should().NotBeNull();
        capturedState!.Content.Should().Contain("[step:1]").And.Contain("Step 3");

        // 2 (least relevant) first, then 3, then 1.
        ordering.Should().NotBeNull();
        ordering!.StepOrdersLeastRelevantFirst.Should().Equal(2, 3, 1);
    }

    [Fact]
    public async Task Tentative_answers_below_AcceptAt_are_not_trusted_and_never_reorder_anything()
    {
        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(
                ChoiceAnswer("step:1", "Drop", 0.45, 0.55), // below AcceptAt 0.6 — a coin flip
                ChoiceAnswer("step:2", "Drop", 0.2, 0.8)));

        ContextRelevanceSelector selector = CreateSelector(provider: Provider(), clientMock: client);

        ContextRelevanceOrdering? ordering = await selector.SelectDropOrderAsync(
            ProjectId, StepCandidates(1, 2), CancellationToken.None);

        ordering.Should().NotBeNull();
        ordering!.StepOrdersLeastRelevantFirst.Should().Equal(2);
    }

    [Fact]
    public async Task No_trusted_answer_at_all_returns_null()
    {
        DecisionAnswer unreadable = new(
            "step:2", DecisionKind.Choice, "Drake", null, null, 0.5, new Dictionary<string, double>());

        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(ChoiceAnswer("step:1", "Drop", 0.45, 0.5), unreadable));

        ContextRelevanceSelector selector = CreateSelector(provider: Provider(), clientMock: client);

        (await selector.SelectDropOrderAsync(ProjectId, StepCandidates(1, 2), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task An_answer_with_no_probabilities_falls_back_to_the_discrete_choice()
    {
        DecisionAnswer discreteKeep = new(
            "step:1", DecisionKind.Choice, "Keep", null, null, 0.95, new Dictionary<string, double>());
        DecisionAnswer discreteDrop = new(
            "step:2", DecisionKind.Choice, "Drop", null, null, 0.95, new Dictionary<string, double>());

        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(discreteKeep, discreteDrop));

        ContextRelevanceSelector selector = CreateSelector(provider: Provider(), clientMock: client);

        ContextRelevanceOrdering? ordering = await selector.SelectDropOrderAsync(
            ProjectId, StepCandidates(1, 2), CancellationToken.None);

        ordering.Should().NotBeNull();
        ordering!.StepOrdersLeastRelevantFirst.Should().Equal(2, 1);
    }

    [Fact]
    public async Task Missing_answers_degrade_to_null_rather_than_guessing()
    {
        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result());

        ContextRelevanceSelector selector = CreateSelector(provider: Provider(), clientMock: client);

        (await selector.SelectDropOrderAsync(ProjectId, StepCandidates(1, 2), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_transport_error_returns_null_and_never_throws()
    {
        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("endpoint down"));

        ContextRelevanceSelector selector = CreateSelector(provider: Provider(), clientMock: client);

        (await selector.SelectDropOrderAsync(ProjectId, StepCandidates(1, 2), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_resolver_error_returns_null_and_never_throws()
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no database"));

        ContextRelevanceSelector selector = CreateSelector(resolverMock: resolver);

        (await selector.SelectDropOrderAsync(ProjectId, StepCandidates(1, 2), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_returns_null_instead_of_propagating()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        ContextRelevanceSelector selector = CreateSelector(provider: Provider(), clientMock: client);

        (await selector.SelectDropOrderAsync(ProjectId, StepCandidates(1, 2), cts.Token))
            .Should().BeNull();
    }

    [Fact]
    public async Task Project_files_enrich_the_state_but_never_appear_in_the_step_ordering()
    {
        ProjectWorkspaceFile file = new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ProjectId,
            "brand-guide.pdf",
            null,
            "inputs",
            "projects/x/inputs/brand-guide.pdf",
            "application/pdf",
            2048,
            DateTime.UtcNow,
            "The company brand guide.");

        DecisionState? capturedState = null;
        IReadOnlyList<DecisionQuestion>? capturedQuestions = null;

        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .Callback((DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken _) =>
            {
                capturedState = state;
                capturedQuestions = questions;
            })
            .ReturnsAsync(Result(
                ChoiceAnswer("step:1", "Keep", 0.8, 0.9),
                ChoiceAnswer("step:2", "Drop", 0.2, 0.9)));

        ContextRelevanceSelector selector = CreateSelector(
            provider: Provider(),
            clientMock: client,
            listFiles: (_, _) => Task.FromResult<IReadOnlyList<ProjectWorkspaceFile>>([file]));

        ContextRelevanceOrdering? ordering = await selector.SelectDropOrderAsync(
            ProjectId, StepCandidates(1, 2), CancellationToken.None);

        capturedState!.Content.Should().Contain("brand-guide.pdf");
        capturedState.Content.Should().Contain("The company brand guide.");
        capturedQuestions!.Should().HaveCount(3);
        capturedQuestions.Select(q => q.Name).Should().Contain($"file:{file.Id}");

        // The file is never ranked — only step orders can be dropped.
        ordering!.StepOrdersLeastRelevantFirst.Should().Equal(2, 1);
    }

    [Fact]
    public async Task A_project_file_listing_failure_still_produces_an_ordering_from_the_step_descriptors()
    {
        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(
                ChoiceAnswer("step:1", "Drop", 0.1, 0.9),
                ChoiceAnswer("step:2", "Keep", 0.9, 0.9)));

        ContextRelevanceSelector selector = CreateSelector(
            provider: Provider(),
            clientMock: client,
            listFiles: (_, _) => throw new InvalidOperationException("storage down"));

        ContextRelevanceOrdering? ordering = await selector.SelectDropOrderAsync(
            ProjectId, StepCandidates(1, 2), CancellationToken.None);

        ordering.Should().NotBeNull();
        ordering!.StepOrdersLeastRelevantFirst.Should().Equal(1, 2);
    }

    [Fact]
    public async Task The_composed_state_never_exceeds_MaxStateChars_and_no_question_names_an_item_it_omits()
    {
        ContextRelevanceOptions options = EnabledOptions();
        options.MaxStateChars = 1_024;

        DecisionState? capturedState = null;
        IReadOnlyList<DecisionQuestion>? capturedQuestions = null;

        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .Callback((DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken _) =>
            {
                capturedState = state;
                capturedQuestions = questions;
            })
            .ReturnsAsync(Result());

        List<ContextRelevanceCandidate> candidates = Enumerable.Range(1, 50)
            .Select(order => new ContextRelevanceCandidate(
                Key: $"step:{order}",
                Kind: ContextRelevanceCandidateKind.Step,
                StepOrder: order,
                Label: $"Step {order}",
                Meta: "12000 chars",
                Descriptor: new string('z', 4_000)))
            .ToList();

        ContextRelevanceSelector selector = CreateSelector(options: options, provider: Provider(), clientMock: client);

        await selector.SelectDropOrderAsync(ProjectId, candidates, CancellationToken.None);

        capturedState.Should().NotBeNull();
        capturedState!.Content.Length.Should().BeLessThanOrEqualTo(1_024);
        capturedQuestions.Should().NotBeNull();
        capturedQuestions!.Count.Should().BeGreaterThanOrEqualTo(2).And.BeLessThan(candidates.Count);

        foreach (DecisionQuestion question in capturedQuestions)
            capturedState.Content.Should().Contain($"[{question.Name}]");
    }

    [Fact]
    public async Task The_question_count_is_capped_in_code_even_when_the_configuration_is_absurd()
    {
        ContextRelevanceOptions options = EnabledOptions();
        options.MaxCandidates = 10_000;
        options.MaxStateChars = 32_000;

        IReadOnlyList<DecisionQuestion>? capturedQuestions = null;

        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .Callback((DecisionState _, IReadOnlyList<DecisionQuestion> questions, CancellationToken _) => capturedQuestions = questions)
            .ReturnsAsync(Result());

        List<ContextRelevanceCandidate> candidates = Enumerable.Range(1, 50)
            .Select(order => new ContextRelevanceCandidate(
                Key: $"step:{order}",
                Kind: ContextRelevanceCandidateKind.Step,
                StepOrder: order,
                Label: $"Step {order}",
                Meta: "10 chars",
                Descriptor: "tiny"))
            .ToList();

        ContextRelevanceSelector selector = CreateSelector(options: options, provider: Provider(), clientMock: client);

        await selector.SelectDropOrderAsync(ProjectId, candidates, CancellationToken.None);

        capturedQuestions.Should().NotBeNull();
        capturedQuestions!.Should().HaveCount(32);
    }

    [Fact]
    public async Task MaxCandidates_caps_the_question_count_and_step_candidates_always_win_over_files()
    {
        ContextRelevanceOptions options = EnabledOptions();
        options.MaxCandidates = 3;

        IReadOnlyList<DecisionQuestion>? capturedQuestions = null;

        Mock<IDecisionClient> client = new();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .Callback((DecisionState _, IReadOnlyList<DecisionQuestion> questions, CancellationToken _) => capturedQuestions = questions)
            .ReturnsAsync(Result());

        List<ProjectWorkspaceFile> files = Enumerable.Range(0, 5)
            .Select(index => new ProjectWorkspaceFile(
                Guid.NewGuid(),
                ProjectId,
                $"file-{index}.txt",
                null,
                "inputs",
                $"projects/x/inputs/file-{index}.txt",
                "text/plain",
                10,
                DateTime.UtcNow,
                null))
            .ToList();

        ContextRelevanceSelector selector = CreateSelector(
            options: options,
            provider: Provider(),
            clientMock: client,
            listFiles: (_, _) => Task.FromResult<IReadOnlyList<ProjectWorkspaceFile>>(files));

        await selector.SelectDropOrderAsync(ProjectId, StepCandidates(1, 2, 3), CancellationToken.None);

        // Every droppable step got a question; the file candidates were crowded out entirely.
        capturedQuestions!.Should().HaveCount(3);
        capturedQuestions.Select(q => q.Name).Should().Equal("step:1", "step:2", "step:3");
    }

    [Fact]
    public async Task The_provider_id_from_configuration_is_the_one_resolved()
    {
        Guid configuredProviderId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        ContextRelevanceOptions options = EnabledOptions();
        options.ProviderId = configuredProviderId;

        Guid? requestedProviderId = null;

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid? id, CancellationToken _) => requestedProviderId = id)
            .ReturnsAsync((ResolvedDecisionProvider?)null);

        ContextRelevanceSelector selector = CreateSelector(
            options: options,
            resolverMock: resolver,
            provider: Provider());

        await selector.SelectDropOrderAsync(ProjectId, StepCandidates(1, 2), CancellationToken.None);

        requestedProviderId.Should().Be(configuredProviderId);
    }
}
