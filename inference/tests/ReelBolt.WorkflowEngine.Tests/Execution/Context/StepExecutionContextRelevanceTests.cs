using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Context;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Context;

/// <summary>
/// Covers <see cref="StepExecutionContext.BuildAgentInputAsync"/> — the ASYNC twin of
/// <see cref="StepExecutionContext.BuildAgentInput"/> that the <c>Agent</c> step executor uses and
/// the only path allowed to consult the optional context-relevance selector.
///
/// <para>
/// The flat fixture below is chosen so that EXACTLY ONE drop happens: three equal-sized droppable
/// parts plus one protected part, with the cap set 100 characters under the legacy concatenation
/// while a single drop frees roughly 188. Digesting is a no-op at those sizes, so the location of
/// the "omitted by context budget" marker names the drop order unambiguously.
/// </para>
/// </summary>
public class StepExecutionContextRelevanceTests
{
    private static readonly Guid ProjectId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static List<StepOutputHistoryEntry> History() =>
    [
        new StepOutputHistoryEntry(1, "Oldest", new string('a', 300)),
        new StepOutputHistoryEntry(2, "Middle", new string('b', 300)),
        new StepOutputHistoryEntry(3, "Newest Droppable", new string('c', 300)),
        new StepOutputHistoryEntry(4, "Protected", new string('d', 100))
    ];

    /// <summary>The pre-budget concatenation, built by hand — the regression guard for byte identity.</summary>
    private static string LegacyFormat(IReadOnlyList<StepOutputHistoryEntry> history) =>
        string.Join("\n\n---\n\n", history.Select(h => $"## Step {h.StepOrder}: {h.StepLabel}\n{h.Output}"));

    private static int LegacyLength() => LegacyFormat(History()).Length;

    private static string DropMarker(int stepOrder, int outputChars) =>
        $"_[Step {stepOrder} output omitted by context budget ({outputChars} chars) " +
        "— re-read it with the project/file tools if you need it.]_";

    private static AgentInputBudgetOptions CreateBudgetOptions(IContextRelevanceSelector? selector, int? maxInputChars = null) =>
        new()
        {
            Enabled = true,
            MaxInputChars = maxInputChars ?? LegacyLength() - 100,
            MaxDigestedStepChars = 300,
            RecentStepsVerbatim = 1,
            DigestMaxStringChars = 50,
            DigestMaxArrayItems = 5,
            DigestMaxDepth = 6,
            RelevanceSelector = selector
        };

    private static StepExecutionContext CreateContext(
        AgentInputContextMode mode,
        IContextRelevanceSelector? selector,
        int? maxInputChars = null,
        string inputMappingJson = null,
        string accumulatedOutput = "irrelevant",
        List<StepOutputHistoryEntry> history = null)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 5,
            StepType = StepType.Agent,
            AgentInputContextMode = mode,
            InputMappingJson = inputMappingJson,
            AgentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = "DirectorAgent",
                AgentType = AgentType.DirectorAgent,
                Description = string.Empty,
                SystemPrompt = string.Empty,
                IsBuiltIn = true
            }
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = accumulatedOutput,
            StepOutputHistory = history ?? History(),
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None,
            BudgetOptions = CreateBudgetOptions(selector, maxInputChars)
        };
    }

    private sealed class FakeSelector : IContextRelevanceSelector
    {
        private readonly ContextRelevanceOrdering? _result;
        private readonly Exception? _toThrow;

        public FakeSelector(ContextRelevanceOrdering? result = null, Exception? toThrow = null)
        {
            _result = result;
            _toThrow = toThrow;
        }

        public int CallCount { get; private set; }

        public Guid LastProjectId { get; private set; }

        public IReadOnlyList<ContextRelevanceCandidate> LastCandidates { get; private set; } = [];

        public Task<ContextRelevanceOrdering?> SelectDropOrderAsync(
            Guid projectId,
            IReadOnlyList<ContextRelevanceCandidate> stepCandidates,
            CancellationToken ct)
        {
            CallCount++;
            LastProjectId = projectId;
            LastCandidates = stepCandidates;

            if (_toThrow is not null)
                throw _toThrow;

            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task Under_budget_never_consults_the_selector_and_stays_byte_identical()
    {
        var selector = new FakeSelector(toThrow: new InvalidOperationException("must not be called"));
        StepExecutionContext context = CreateContext(
            AgentInputContextMode.FullWorkflow, selector, maxInputChars: 1_000_000);

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        selector.CallCount.Should().Be(0);
        asyncInput.Should().Be(context.BuildAgentInput());
        asyncInput.Should().Be(LegacyFormat(History()));
        context.LastBudgetSummary.Should().BeNull();
    }

    [Fact]
    public async Task A_disabled_budget_never_consults_the_selector()
    {
        var selector = new FakeSelector(toThrow: new InvalidOperationException("must not be called"));
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector);
        context.BudgetOptions!.Enabled = false;

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        selector.CallCount.Should().Be(0);
        asyncInput.Should().Be(LegacyFormat(History()));
        asyncInput.Should().Be(context.BuildAgentInput());
    }

    [Fact]
    public async Task No_selector_wired_leaves_the_oldest_first_drop_order_untouched()
    {
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector: null);

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        asyncInput.Should().Be(context.BuildAgentInput());
        asyncInput.Should().Contain(DropMarker(1, 300));
        asyncInput.Should().NotContain(DropMarker(3, 300));
    }

    [Fact]
    public async Task Over_budget_with_a_selector_drops_the_least_relevant_part_instead_of_the_oldest()
    {
        var selector = new FakeSelector(new ContextRelevanceOrdering([3, 1, 2]));
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector);

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);
        string syncInput = context.BuildAgentInput();

        selector.CallCount.Should().Be(1);

        asyncInput.Should().Contain(DropMarker(3, 300));
        asyncInput.Should().NotContain(DropMarker(1, 300));
        asyncInput.Should().Contain($"## Step 1: Oldest\n{new string('a', 300)}");

        // The synchronous path is untouched: same context, same budget, still oldest-first.
        syncInput.Should().Contain(DropMarker(1, 300));
        syncInput.Should().NotContain(DropMarker(3, 300));
    }

    [Fact]
    public async Task PreviousStepOnly_never_consults_the_selector()
    {
        var selector = new FakeSelector(toThrow: new InvalidOperationException("must not be called"));
        StepExecutionContext context = CreateContext(AgentInputContextMode.PreviousStepOnly, selector);

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        selector.CallCount.Should().Be(0);
        asyncInput.Should().Be(new string('d', 100));
        asyncInput.Should().Be(context.BuildAgentInput());
    }

    [Fact]
    public async Task CustomMappedSubset_never_consults_the_selector()
    {
        var selector = new FakeSelector(toThrow: new InvalidOperationException("must not be called"));
        StepExecutionContext context = CreateContext(
            AgentInputContextMode.CustomMappedSubset,
            selector,
            inputMappingJson: "{\"picked\":\"$.foo\"}",
            accumulatedOutput: "{\"foo\":\"bar\"}");

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        selector.CallCount.Should().Be(0);
        asyncInput.Should().Contain("\"bar\"");
        asyncInput.Should().Be(context.BuildAgentInput());
    }

    [Fact]
    public async Task A_selector_that_throws_degrades_to_the_synchronous_result()
    {
        var selector = new FakeSelector(toThrow: new InvalidOperationException("boom"));
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector);

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        selector.CallCount.Should().Be(1);
        asyncInput.Should().Be(context.BuildAgentInput());
        asyncInput.Should().Contain(DropMarker(1, 300));
    }

    [Fact]
    public async Task A_selector_that_returns_null_degrades_to_the_synchronous_result()
    {
        var selector = new FakeSelector(result: null);
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector);

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        asyncInput.Should().Be(context.BuildAgentInput());
    }

    [Fact]
    public async Task The_selector_receives_only_the_droppable_parts_of_this_step_and_the_project_id()
    {
        var selector = new FakeSelector(new ContextRelevanceOrdering([2, 1]));
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector);

        await context.BuildAgentInputAsync(CancellationToken.None);

        selector.LastProjectId.Should().Be(ProjectId);
        selector.LastCandidates.Select(c => c.StepOrder).Should().Equal(1, 2, 3); // step 4 is protected
        selector.LastCandidates.Should().OnlyContain(c => c.Kind == ContextRelevanceCandidateKind.Step);
        selector.LastCandidates.Select(c => c.Key).Should().Equal("step:1", "step:2", "step:3");
        selector.LastCandidates.Select(c => c.Meta).Should().AllBe("300 chars");
        selector.LastCandidates.Should().OnlyContain(c => c.Descriptor.Length == 300);
    }

    [Fact]
    public async Task The_selector_sees_the_prompt_facing_output_including_a_prompt_override()
    {
        var selector = new FakeSelector(new ContextRelevanceOrdering([2, 1]));
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector);

        // SetPromptOutputOverride is prompt-only by contract; the relevance pass ranks the prompt,
        // so it must see the same enriched rendering the agent will. The override is deliberately
        // as long as the output it replaces, so this context stays over budget either way.
        string enriched = "{\"enriched\":\"" + new string('e', 300) + "\"}";
        context.SetPromptOutputOverride(2, enriched);

        await context.BuildAgentInputAsync(CancellationToken.None);

        selector.LastCandidates.Single(c => c.StepOrder == 2).Descriptor.Should().Be(enriched);
        selector.LastCandidates.Single(c => c.StepOrder == 1).Descriptor.Should().Be(new string('a', 300));
    }

    [Fact]
    public async Task The_relevance_pass_is_prompt_only_and_leaves_the_step_history_and_overrides_alone()
    {
        List<StepOutputHistoryEntry> history = History();
        var selector = new FakeSelector(new ContextRelevanceOrdering([3, 1, 2]));
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector, history: history);

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        // The produced PROMPT lost step 3's body...
        asyncInput.Should().Contain(DropMarker(3, 300));

        // ...while the history every by-StepOrder consumer and the persisted OutputJson are built
        // from still carries the raw, unmodified output.
        context.StepOutputHistory.Select(h => h.StepOrder).Should().Equal(1, 2, 3, 4);
        context.StepOutputHistory.Single(h => h.StepOrder == 3).Output.Should().Be(new string('c', 300));
        context.StepOutputHistory.Should().NotContain(h => h.Output.Contains("omitted by context budget"));

        // The string the executor reports as the step's resolved input is the budgeted prompt.
        context.LastResolvedAgentInput.Should().Be(asyncInput);
    }

    [Fact]
    public async Task The_budget_summary_still_reports_what_actually_happened()
    {
        var selector = new FakeSelector(new ContextRelevanceOrdering([3, 1, 2]));
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector);

        await context.BuildAgentInputAsync(CancellationToken.None);

        context.LastBudgetSummary.Should().NotBeNull();
        context.LastBudgetSummary.Should().Contain("context budget:");
        context.LastBudgetSummary.Should().Contain("1 dropped");
    }

    /// <summary>
    /// The real <see cref="ContextRelevanceSelector"/> with NO Decision provider resolvable — the
    /// "not configured yet" deployment. It must be indistinguishable from the feature not
    /// existing, which is the first-class state the ticket calls out.
    /// </summary>
    [Fact]
    public async Task No_decision_provider_configured_produces_a_prompt_byte_identical_to_master()
    {
        ContextRelevanceSelector selector = CreateRealSelector(resolvedProvider: null, enabled: true, decide: null);
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector);
        StepExecutionContext reference = CreateContext(AgentInputContextMode.FullWorkflow, selector: null);

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        asyncInput.Should().Be(reference.BuildAgentInput());
        asyncInput.Should().Contain(DropMarker(1, 300));
    }

    /// <summary>
    /// The real selector with the feature switched off — the default configuration. Same
    /// requirement: nothing about the prompt may move.
    /// </summary>
    [Fact]
    public async Task The_feature_disabled_produces_a_prompt_byte_identical_to_master()
    {
        ContextRelevanceSelector selector = CreateRealSelector(resolvedProvider: null, enabled: false, decide: null);
        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector);
        StepExecutionContext reference = CreateContext(AgentInputContextMode.FullWorkflow, selector: null);

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        asyncInput.Should().Be(reference.BuildAgentInput());
        asyncInput.Should().Contain(DropMarker(1, 300));
    }

    /// <summary>
    /// The real selector end to end at this level: a configured provider whose client fails, and
    /// the same byte-identical prompt as master.
    /// </summary>
    [Fact]
    public async Task A_transport_error_in_the_real_selector_produces_a_prompt_byte_identical_to_master()
    {
        var client = new Mock<IDecisionClient>();
        client
            .Setup(c => c.DecideAsync(
                It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionQuestion>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("endpoint down"));

        ContextRelevanceSelector selector = CreateRealSelector(
            resolvedProvider: new ResolvedDecisionProvider(
                Guid.NewGuid(),
                "OpenJev local",
                InferenceProviderKind.OpenJev,
                "http://localhost:8099",
                "jev-small",
                string.Empty,
                30),
            enabled: true,
            decide: client);

        StepExecutionContext context = CreateContext(AgentInputContextMode.FullWorkflow, selector);
        StepExecutionContext reference = CreateContext(AgentInputContextMode.FullWorkflow, selector: null);

        string asyncInput = await context.BuildAgentInputAsync(CancellationToken.None);

        asyncInput.Should().Be(reference.BuildAgentInput());
        asyncInput.Should().Contain(DropMarker(1, 300));
    }

    /// <summary>
    /// The real <see cref="ContextRelevanceSelector"/>, wired the way production wires it, used by
    /// the three "byte-identical to master" tests above. Deliberately the real implementation and
    /// not the local fake: the point of those tests is that the assembly of real collaborators —
    /// real options object, real clamping, real degradation — cannot move the prompt either.
    /// </summary>
    private static ContextRelevanceSelector CreateRealSelector(
        ResolvedDecisionProvider? resolvedProvider,
        bool enabled,
        Mock<IDecisionClient>? decide)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedProvider);

        var factory = new Mock<IDecisionClientFactory>();
        factory
            .Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>()))
            .Returns(decide?.Object ?? new Mock<IDecisionClient>(MockBehavior.Strict).Object);

        return new ContextRelevanceSelector(
            resolver.Object,
            factory.Object,
            new Mock<IProjectFileWorkspace>().Object,
            Options.Create(new ContextRelevanceOptions { Enabled = enabled }),
            NullLogger<ContextRelevanceSelector>.Instance);
    }
}
