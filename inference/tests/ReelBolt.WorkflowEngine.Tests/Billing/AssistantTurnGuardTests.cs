#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// the in-turn guard pre-claims every model round-trip (its estimated input plus its output
/// ceiling, priced on the chat model) and every tool call (its quote; the unpriced default for a Platform-billed tool)
/// before it runs, caps a round-trip's input and a model step's tool calls, and admits a BYO-chat turn's first
/// Platform-billed tool under the in-flight cap.
/// </summary>
public sealed class AssistantTurnGuardTests : IAsyncLifetime
{
    private static readonly ResolvedInferenceProvider PlatformChat =
        new(Guid.NewGuid(), "platform", InferenceProviderKind.Anthropic, "http://x", "claude-test", "", 30);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private NonWorkflowChargingHost _host = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        // One millicredit per token, input or output: the claim is the tokens it prices.
        _host = new NonWorkflowChargingHost(o => o.UseSqlite(_connection), prices: new NonWorkflowChargingHost.FixedPrices(1));
        await using (InferenceApiDbContext db = _host.NewDb())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await _host.SeedPlansAsync();
    }

    public Task DisposeAsync()
    {
        _host.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedClients(IChatClient client) : IAgentChatClientProvider
    {
        public ValueTask<IChatClient> GetAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) => ValueTask.FromResult(client);
    }

    private static AssistantTurnBudget Budget(
        UsageBillingMode mode = UsageBillingMode.Platform, int outputTokens = 1_000, int maxInputTokens = 1_000_000, int maxToolCalls = 8)
        => new(mode == UsageBillingMode.Platform ? PlatformChat : PlatformChat with { OrganizationId = Guid.NewGuid(), IsPlatformManaged = false },
            outputTokens, maxInputTokens, maxToolCalls, mode);

    private static List<ChatMessage> Context(int chars) => [new ChatMessage(ChatRole.User, new string('x', chars))];

    private static async Task<IChatClient> GuardedAsync(AssistantTurnGuard guard, IChatClient inner)
        => await guard.Wrap(new FixedClients(inner)).GetAsync(AgentType.Custom, null, CancellationToken.None);

    /// <summary>
    /// F-5.2: a turn admitted on its 5-credit hold with a zero balance left cannot start a round-trip whose context is
    /// too large to pay for: it is refused BEFORE the model is called (the old check let any first round-trip through).
    /// </summary>
    [Fact]
    public async Task A_huge_context_round_trip_at_zero_balance_is_refused_before_it_runs()
    {
        Guid org = await _host.AddOrgAsync();
        await _host.GrantAsync(org, 5_000);
        await UseAllowanceAsync(org);
        TurnHold turn = await _host.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        turn.Allowed.Should().BeTrue();
        (await _host.BalanceAsync(org)).AvailableMillis.Should().Be(0, "the hold took the whole balance");

        RecordingChatClient model = new();
        using var scope = UsageScope.Begin(org, null, null, null, Guid.NewGuid(), UsageSource.Assistant);
        AssistantTurnGuard guard = new(_host.Charges, turn, UsageScope.Current, Budget());
        IChatClient client = await GuardedAsync(guard, model);

        // ~20 000 characters is ~7 500 input tokens with the safety factor, plus 1 000 of output: past 5 held + 1 of headroom.
        Func<Task> huge = () => client.GetResponseAsync(Context(20_000));
        (await huge.Should().ThrowAsync<AssistantTurnStoppedException>()).Which.Code.Should().Be(NonWorkflowChargeCodes.InsufficientCredits);
        model.Calls.Should().Be(0, "the round-trip never ran");
    }

    [Fact]
    public async Task A_small_round_trip_within_the_hold_runs()
    {
        Guid org = await _host.AddOrgAsync();
        await _host.GrantAsync(org, 5_000);
        await UseAllowanceAsync(org);
        TurnHold turn = await _host.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        RecordingChatClient model = new();
        using var scope = UsageScope.Begin(org, null, null, null, Guid.NewGuid(), UsageSource.Assistant);
        AssistantTurnGuard guard = new(_host.Charges, turn, UsageScope.Current, Budget());
        IChatClient client = await GuardedAsync(guard, model);

        await client.GetResponseAsync(Context(400));
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(Context(400)))
        {
        }

        model.Calls.Should().Be(2);
    }

    /// <summary>F-5.2: a round-trip whose input is past Assistant:MaxRoundTripInputTokens is refused however much is available.</summary>
    [Fact]
    public async Task A_round_trip_past_the_input_token_cap_is_refused_with_the_turn_limit()
    {
        FakeNonWorkflowCharges charges = new();
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        RecordingChatClient model = new();
        AssistantTurnGuard guard = new(charges, turn, null, Budget(maxInputTokens: 1_000));
        IChatClient client = await GuardedAsync(guard, model);

        Func<Task> over = () => client.GetResponseAsync(Context(8_000));

        (await over.Should().ThrowAsync<AssistantTurnStoppedException>()).Which.Code.Should().Be(NonWorkflowChargeCodes.TurnLimit);
        model.Calls.Should().Be(0);
        charges.Claims.Should().BeEmpty("the cap refuses before anything is claimed");
    }

    /// <summary>F-5.2: the round-trip's claim is its estimated input plus its output ceiling, priced on the chat model.</summary>
    [Fact]
    public async Task A_round_trip_claims_its_estimated_input_plus_its_output_ceiling()
    {
        FakeNonWorkflowCharges charges = new() { EstimateMillis = 3 };
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        AssistantTurnGuard guard = new(charges, turn, null, Budget(outputTokens: 2_000));
        IChatClient client = await GuardedAsync(guard, new RecordingChatClient());

        await client.GetResponseAsync(Context(4_000));

        charges.Estimates.Select(e => e.Unit).Should().Equal(UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens);
        charges.Estimates[0].Quantity.Should().BeGreaterThanOrEqualTo(1_000, "4 000 characters is at least 1 000 tokens");
        charges.Estimates[0].Quantity.Should().Be(AssistantTurnGuard.EstimateInputTokens(Context(4_000), null));
        charges.Estimates[1].Quantity.Should().Be(2_000);
        charges.Estimates.Should().OnlyContain(e => e.PromptTokens == (long)charges.Estimates[0].Quantity,
            "the known prompt length selects a gt{N}k tier for both units");
        charges.Claims.Should().ContainSingle().Which.StepMillis.Should().Be(6, "both estimates");
    }

    /// <summary>F-5.2: at most Assistant:MaxToolCallsPerStep tool calls run per model step; the rest get a plain refusal.</summary>
    [Fact]
    public async Task Tool_calls_past_the_per_step_maximum_are_answered_with_a_plain_refusal()
    {
        FakeNonWorkflowCharges charges = new();
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        AssistantTurnGuard guard = new(charges, turn, null, Budget(maxToolCalls: 2));
        int ran = 0;
        AIFunction tool = guard.Gate(AIFunctionFactory.Create(() => { ran++; return "done"; }, name: "ListWorkflows"));
        IChatClient client = await GuardedAsync(guard, new RecordingChatClient());

        await client.GetResponseAsync(Context(10));
        object?[] results = [await tool.InvokeAsync(), await tool.InvokeAsync(), await tool.InvokeAsync()];

        ran.Should().Be(2);
        AssistantToolRefusal.IsRefusal(results[2]?.ToString()).Should().BeTrue("the third call in one step is refused in plain words");

        await client.GetResponseAsync(Context(10));
        await tool.InvokeAsync();
        ran.Should().Be(3, "the next model step starts a new count");
    }

    /// <summary>F-5.2: a Platform-billed tool claims its quote (the unpriced default) before it runs; a free tool claims 0.</summary>
    [Fact]
    public async Task A_platform_billed_tool_claims_the_unpriced_default_and_a_free_tool_claims_nothing()
    {
        FakeNonWorkflowCharges charges = new();
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        AssistantTurnGuard guard = new(charges, turn, null, Budget());

        await guard.Gate(AIFunctionFactory.Create(() => "x", name: "ListWorkflows")).InvokeAsync();
        await guard.Gate(AIFunctionFactory.Create(() => "x", name: "LookAtPicture")).InvokeAsync();

        charges.Claims.Select(c => (c.StepMillis, c.AdmitUnderConcurrencyCap)).Should().Equal((0L, false), (10_000L, false));
    }

    /// <summary>
    /// F-7: a BYO-chat turn asks to be counted against the in-flight cap at its first Platform-billed tool (a picture
    /// tool, vision), and only then; refused there, the tool never runs and the turn stops.
    /// </summary>
    [Fact]
    public async Task A_byo_turn_is_admitted_under_the_cap_at_its_first_platform_billed_tool_only()
    {
        FakeNonWorkflowCharges charges = new();
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid(), UsageBillingMode.Byo);
        AssistantTurnGuard guard = new(charges, turn, null, Budget(UsageBillingMode.Byo));
        int ran = 0;

        await guard.Gate(AIFunctionFactory.Create(() => { ran++; return "x"; }, name: "ListWorkflows")).InvokeAsync();
        await guard.Gate(AIFunctionFactory.Create(() => { ran++; return "x"; }, name: "ApplyRunPictureTool")).InvokeAsync();
        await guard.Gate(AIFunctionFactory.Create(() => { ran++; return "x"; }, name: "LookAtPicture")).InvokeAsync();

        ran.Should().Be(3);
        charges.Claims.Select(c => c.AdmitUnderConcurrencyCap).Should().Equal(false, true, false);
    }

    [Fact]
    public async Task A_byo_turn_refused_by_the_cap_at_a_platform_billed_tool_never_runs_it()
    {
        FakeNonWorkflowCharges charges = new() { RefuseCapAdmission = true };
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid(), UsageBillingMode.Byo);
        AssistantTurnGuard guard = new(charges, turn, null, Budget(UsageBillingMode.Byo));
        int ran = 0;

        Func<Task> look = async () => await guard.Gate(AIFunctionFactory.Create(() => { ran++; return "x"; }, name: "LookAtPicture")).InvokeAsync();

        (await look.Should().ThrowAsync<AssistantTurnStoppedException>()).Which.Code.Should().Be(NonWorkflowChargeCodes.TurnConcurrencyLimit);
        ran.Should().Be(0);
        guard.StoppedCode.Should().Be(NonWorkflowChargeCodes.TurnConcurrencyLimit);
    }

    /// <summary>F-7: a pure BYO turn (no Platform-billed tool) never asks for the cap and its round-trips claim nothing.</summary>
    [Fact]
    public async Task A_pure_byo_turn_claims_nothing_and_is_never_capped()
    {
        FakeNonWorkflowCharges charges = new() { RefuseCapAdmission = true };
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid(), UsageBillingMode.Byo);
        AssistantTurnGuard guard = new(charges, turn, null, Budget(UsageBillingMode.Byo, maxInputTokens: 10));
        IChatClient client = await GuardedAsync(guard, new RecordingChatClient());

        await client.GetResponseAsync(Context(8_000));
        await guard.Gate(AIFunctionFactory.Create(() => "x", name: "ListWorkflows")).InvokeAsync();

        charges.Claims.Should().OnlyContain(c => c.StepMillis == 0 && !c.AdmitUnderConcurrencyCap);
        guard.StoppedCode.Should().BeNull();
    }

    /// <summary>
    /// T-026 (finding 2): a Platform turn whose chat provider could not be resolved (ChatProvider null) is not free: each
    /// round-trip is claimed at the unpriced default the estimate uses, and the input cap still applies.
    /// </summary>
    [Fact]
    public async Task A_platform_turn_with_an_unresolved_chat_provider_claims_the_unpriced_default_and_keeps_the_input_cap()
    {
        FakeNonWorkflowCharges charges = new();
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        AssistantTurnGuard guard = new(charges, turn, null, AssistantTurnBudget.For(null, null));
        IChatClient client = await GuardedAsync(guard, new RecordingChatClient());

        await client.GetResponseAsync(Context(400));

        charges.Claims.Should().ContainSingle().Which.StepMillis.Should().Be(10_000, "the unpriced default, never 0");

        RecordingChatClient model = new();
        AssistantTurnGuard capped = new(charges, turn, null, new AssistantTurnBudget(null, 1_000, 1_000, 8));
        Func<Task> over = async () => await (await GuardedAsync(capped, model)).GetResponseAsync(Context(8_000));
        (await over.Should().ThrowAsync<AssistantTurnStoppedException>()).Which.Code.Should().Be(NonWorkflowChargeCodes.TurnLimit);
        model.Calls.Should().Be(0);
    }

    /// <summary>
    /// T-026 (finding 3): a step that metered Platform usage the platform could not price counts at its claim, not 0:
    /// towards the per-turn ceiling (three 10-credit unpriced steps under a 35-credit ceiling: the fourth is refused)
    /// and towards the settlement (charged at least the three claims, not just the 5-credit hold).
    /// </summary>
    [Fact]
    public async Task Unpriced_steps_count_at_their_claims_towards_the_ceiling_and_the_settlement()
    {
        Guid org = await _host.AddOrgAsync();
        await _host.GrantAsync(org, 100_000);
        await UseAllowanceAsync(org);
        _host.Options.NonWorkflow.AssistantTurnCeilingCredits = 35;
        TurnHold turn = await _host.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        UsageRecorder unpricedMeter = new(new NonWorkflowChargingHost.FixedPrices(null), new NullSink(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);

        using var scope = UsageScope.Begin(org, null, null, null, Guid.NewGuid(), UsageSource.Assistant);
        AssistantTurnGuard guard = new(_host.Charges, turn, UsageScope.Current, Budget());
        int ran = 0;
        AIFunction look = guard.Gate(AIFunctionFactory.Create(async () =>
        {
            ran++;
            await unpricedMeter.RecordAsync(new UsageRecordRequest(
                UsageKind.LlmOutputTokens, 100, Guid.NewGuid(), InferenceProviderKind.Anthropic, "vision-x", UsageBillingMode.Platform));
            return "seen";
        }, name: "LookAtPicture"));

        for (int i = 0; i < 3; i++) await look.InvokeAsync();
        Func<Task> fourth = async () => await look.InvokeAsync();

        (await fourth.Should().ThrowAsync<AssistantTurnStoppedException>()).Which.Code.Should().Be(NonWorkflowChargeCodes.TurnLimit);
        ran.Should().Be(3);
        UsageScope.Current!.RunningCreditsMillis.Should().Be(0, "every look metered unpriced usage");

        long before = (await _host.BalanceAsync(org)).BalanceMillis;
        await using (TurnCharge charge = new(_host.Charges, turn, null)) await charge.SettleAsync(UsageScope.Current);
        (before - (await _host.BalanceAsync(org)).BalanceMillis).Should().BeGreaterThanOrEqualTo(30_000, "at least the three claims");
    }

    /// <summary>
    /// T-026 (finding 8): LookAtPicture claims at its resolved vision model's input plus its output ceiling; the
    /// 10-credit default only when that model cannot be resolved.
    /// </summary>
    [Fact]
    public async Task LookAtPicture_claims_its_vision_models_quote_and_the_default_only_when_it_cannot_be_quoted()
    {
        ResolvedInferenceProvider vision = PlatformChat with { Capability = InferenceProviderCapability.Vision, ModelName = "vision-model" };
        FakeNonWorkflowCharges charges = new() { EstimateMillis = 3 };
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        AssistantTurnGuard guard = new(charges, turn, null, Budget()) { ResolveVision = _ => ValueTask.FromResult<ResolvedInferenceProvider?>(vision) };

        await guard.Gate(AIFunctionFactory.Create((string? question) => "x", name: "LookAtPicture")).InvokeAsync(new AIFunctionArguments { ["question"] = "what is this?" });

        charges.Estimates.Select(e => (e.Unit, e.ModelCode)).Should().Equal(
            (UsageKind.LlmInputTokens, "vision-model"), (UsageKind.LlmOutputTokens, "vision-model"));
        charges.Estimates[0].Quantity.Should().BeGreaterThanOrEqualTo(AssistantTurnGuard.TokensPerImage, "the picture is part of the input");
        charges.Estimates.Should().OnlyContain(e => e.PromptTokens == (long)charges.Estimates[0].Quantity);
        charges.Claims.Should().ContainSingle().Which.StepMillis.Should().Be(6, "input plus output, priced on the vision model");

        FakeNonWorkflowCharges unresolved = new() { EstimateMillis = 3 };
        AssistantTurnGuard none = new(unresolved, turn, null, Budget()) { ResolveVision = _ => ValueTask.FromResult<ResolvedInferenceProvider?>(null) };
        await none.Gate(AIFunctionFactory.Create(() => "x", name: "LookAtPicture")).InvokeAsync();
        unresolved.Claims.Should().ContainSingle().Which.StepMillis.Should().Be(10_000);
    }

    private static readonly ResolvedInferenceProvider VisionModel =
        PlatformChat with { Capability = InferenceProviderCapability.Vision, ModelName = "vision-model" };

    private const string Question = "what is this?";

    /// <summary>The tokens of the look's question alone (the picture is counted by <see cref="AssistantTurnGuard.ImageTokensFor"/>).</summary>
    private static long QuestionTokens => AssistantTurnGuard.EstimateInputTokens([new ChatMessage(ChatRole.User, Question)], null);

    private static async Task<long> LookClaimAsync(Func<IDictionary<string, object?>?, CancellationToken, ValueTask<LookSizing?>>? pixels)
    {
        FakeNonWorkflowCharges charges = new() { EstimateBy = r => (long)r.Quantity };
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        AssistantTurnGuard guard = new(charges, turn, null, Budget())
        {
            ResolveVision = _ => ValueTask.FromResult<ResolvedInferenceProvider?>(VisionModel),
            ResolveLookPixels = pixels
        };
        await guard.Gate(AIFunctionFactory.Create((string? question) => "x", name: "LookAtPicture"))
            .InvokeAsync(new AIFunctionArguments { ["question"] = Question });
        return charges.Claims.Single().StepMillis;
    }

    private static long PromptTokens(int chars) =>
        AssistantTurnGuard.EstimateInputTokens([new ChatMessage(ChatRole.User, new string('x', chars))], null);

    private static async Task<long> LookOutputClaimAsync(Func<IDictionary<string, object?>?, CancellationToken, ValueTask<LookOutputSizing?>>? sizer)
    {
        FakeNonWorkflowCharges charges = new() { EstimateBy = r => (long)r.Quantity };
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        AssistantTurnGuard guard = new(charges, turn, null, Budget())
        {
            ResolveVision = _ => ValueTask.FromResult<ResolvedInferenceProvider?>(VisionModel),
            ResolveLookOutputSizing = sizer
        };
        await guard.Gate(AIFunctionFactory.Create((string? question) => "x", name: "LookAtOutput"))
            .InvokeAsync(new AIFunctionArguments { ["question"] = Question });
        charges.Estimates.Should().OnlyContain(e => e.ModelCode == "vision-model");
        return charges.Claims.Single().StepMillis;
    }

    /// <summary>C1 / S-76: LookAtOutput is claimed at its vision model's quote for the images it will send, not the flat default.</summary>
    [Fact]
    public async Task LookAtOutput_claims_its_vision_quote_for_the_stills_it_will_send()
    {
        long two = await LookOutputClaimAsync((_, _) => ValueTask.FromResult<LookOutputSizing?>(
            new LookOutputSizing(2, AssistantOutputTools.MaxStillPixels, PromptChars: 120)));
        long six = await LookOutputClaimAsync((_, _) => ValueTask.FromResult<LookOutputSizing?>(
            new LookOutputSizing(6, AssistantOutputTools.MaxStillPixels, PromptChars: 120)));

        two.Should().Be(PromptTokens(120) + 2 * AssistantTurnGuard.ImageTokensFor(AssistantOutputTools.MaxStillPixels) + AssistantPictureTools.LookOutputTokens);
        six.Should().BeGreaterThan(two);
    }

    /// <summary>C1 / S-76: unsized (no sizer, a failing one, or nothing found) the look is claimed at its worst case.</summary>
    [Fact]
    public async Task LookAtOutput_that_cannot_be_sized_claims_its_worst_case()
    {
        long worstImages = Math.Max(
            AssistantTurnGuard.ImageTokensFor(AssistantPictureTools.MaxLookPixels),
            AssistantOutputTools.MaxLookImages * AssistantTurnGuard.ImageTokensFor(AssistantOutputTools.MaxStillPixels));
        long worst = PromptTokens(AssistantOutputTools.WorstLookPromptChars) + worstImages + AssistantPictureTools.LookOutputTokens;

        (await LookOutputClaimAsync(null)).Should().Be(worst);
        (await LookOutputClaimAsync((_, _) => ValueTask.FromResult<LookOutputSizing?>(null))).Should().Be(worst);
        (await LookOutputClaimAsync((_, _) => throw new InvalidOperationException("db down"))).Should().Be(worst);
        worst.Should().BeGreaterThan(10_000, "more than the flat unpriced default it used to claim");
    }

    /// <summary>
    /// a tool name the cost table does not know is claimed at the unpriced default, exactly like a
    /// Platform-billed tool that cannot be quoted; a tool the table lists as free still claims nothing.
    /// </summary>
    [Fact]
    public async Task An_unclassified_tool_is_claimed_at_the_unpriced_default()
    {
        FakeNonWorkflowCharges charges = new();
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        AssistantTurnGuard guard = new(charges, turn, null, Budget());

        await guard.Gate(AIFunctionFactory.Create(() => "x", name: "ListWorkflows")).InvokeAsync();
        await guard.Gate(AIFunctionFactory.Create(() => "x", name: "SomeNewModelTool")).InvokeAsync();
        await guard.Gate(AIFunctionFactory.Create(() => "x", name: "LookAtPicture")).InvokeAsync();

        charges.Claims.Select(c => c.StepMillis).Should().Equal(0L, 10_000L, 10_000L);
    }

    /// <summary>a BYO-chat turn's first unclassified tool is admitted under the in-flight cap.</summary>
    [Fact]
    public async Task A_byo_turn_admits_an_unclassified_tool_under_the_cap()
    {
        FakeNonWorkflowCharges charges = new();
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid(), UsageBillingMode.Byo);
        AssistantTurnGuard guard = new(charges, turn, null, Budget(UsageBillingMode.Byo));

        await guard.Gate(AIFunctionFactory.Create(() => "x", name: "ListWorkflows")).InvokeAsync();
        await guard.Gate(AIFunctionFactory.Create(() => "x", name: "SomeNewModelTool")).InvokeAsync();

        charges.Claims.Select(c => (c.StepMillis, c.AdmitUnderConcurrencyCap)).Should().Equal((0L, false), (10_000L, true));
    }

    /// <summary>
    /// a look is claimed at the picture it will actually send: the question, the picture's image
    /// tokens (one per 750 pixels) and the look's output ceiling, so a 24 MP photograph claims more than a 512 x 512 one.
    /// </summary>
    [Fact]
    public async Task A_large_picture_look_claims_more_than_a_small_one()
    {
        long small = await LookClaimAsync((_, _) => ValueTask.FromResult<LookSizing?>(new LookSizing(Guid.NewGuid(), 512L * 512)));
        long large = await LookClaimAsync((_, _) => ValueTask.FromResult<LookSizing?>(new LookSizing(Guid.NewGuid(), 6000L * 4000)));

        large.Should().BeGreaterThan(small);
        AssistantTurnGuard.ImageTokensFor(24_000_000).Should().Be(32_000);
        large.Should().Be(QuestionTokens + 32_000 + AssistantPictureTools.LookOutputTokens);
        small.Should().Be(QuestionTokens + AssistantTurnGuard.MinTokensPerImage + AssistantPictureTools.LookOutputTokens);
    }

    /// <summary>a picture whose size cannot be read (or a sizer that fails) is claimed at the 40 MP cap.</summary>
    [Fact]
    public async Task A_look_whose_size_is_unknown_claims_the_pixel_cap()
    {
        long cap = QuestionTokens + 53_334 + AssistantPictureTools.LookOutputTokens;

        (await LookClaimAsync((_, _) => ValueTask.FromResult<LookSizing?>(null))).Should().Be(cap);
        (await LookClaimAsync((_, _) => ValueTask.FromResult<LookSizing?>(new LookSizing(Guid.NewGuid(), null)))).Should().Be(cap);
        (await LookClaimAsync((_, _) => throw new InvalidOperationException("storage down"))).Should().Be(cap);
        (await LookClaimAsync(null)).Should().Be(cap, "no sizer is the same as an unknown size");
    }

    /// <summary>
    /// T-093, T-095: a picture tool is claimed at the larger of the unpriced default and its
    /// dearest worst-case quote; no requests, or a hook that fails, claims the default.
    /// </summary>
    [Fact]
    public async Task ApplyRunPictureTool_claims_the_larger_of_its_top_tier_quote_and_the_default()
    {
        PriceRequest top = new(Guid.NewGuid(), null, InferenceProviderKind.FalAi, InferenceProviderCapability.ImageErase,
            "fal-ai/object-removal/mask", UsageKind.ImageEditImages, "max", UsageBillingMode.Platform, 1);
        PriceRequest cheap = top with { Variant = "low" };

        (long above, FakeNonWorkflowCharges priced) = await PictureToolClaimAsync(r => r.Variant == "max" ? 26_400 : 3_000, (_, _) => Requests(top));
        above.Should().Be(26_400, "a top tier dearer than the default claims its quote");
        priced.Estimates.Should().ContainSingle().Which.Should().Be(top);

        (await PictureToolClaimAsync(r => r.Variant == "max" ? 26_400 : 3_000, (_, _) => Requests(cheap))).Claim.Should().Be(10_000, "never below the default");
        (await PictureToolClaimAsync(_ => 3_000, (_, _) => Requests())).Claim.Should().Be(10_000, "no request claims the default");
        (await PictureToolClaimAsync(_ => 3_000, (_, _) => ValueTask.FromResult<IReadOnlyList<PriceRequest>?>(null))).Claim.Should().Be(10_000);
        (await PictureToolClaimAsync(_ => 3_000, (_, _) => throw new InvalidOperationException("resolver down"))).Claim.Should().Be(10_000);
    }

    /// <summary>
    /// T-095: the claim is the dearest of the estimates of every in-force tier and the variant-less request,
    /// however the price service resolves each, so a tier dearer than the variant-less row is never under-claimed.
    /// </summary>
    [Fact]
    public async Task ApplyRunPictureTool_claims_the_dearest_estimate_over_every_variant()
    {
        PriceRequest any = new(Guid.NewGuid(), null, InferenceProviderKind.FalAi, InferenceProviderCapability.ImageErase,
            "fal-ai/object-removal/mask", UsageKind.ImageEditImages, null, UsageBillingMode.Platform, 1);
        Dictionary<string, long> millis = new() { ["low"] = 12_000, ["mid"] = 41_000, ["max"] = 27_000 };

        (long claim, FakeNonWorkflowCharges charges) = await PictureToolClaimAsync(
            r => r.Variant is { } v ? millis[v] : 15_000,
            (_, _) => Requests(any, any with { Variant = "low" }, any with { Variant = "mid" }, any with { Variant = "max" }));

        claim.Should().Be(41_000, "the dearest estimate, even though the variant-less row prices at 15,000");
        charges.Estimates.Select(e => e.Variant).Should().BeEquivalentTo(new string?[] { null, "low", "mid", "max" });
    }

    private static ValueTask<IReadOnlyList<PriceRequest>?> Requests(params PriceRequest[] requests)
        => ValueTask.FromResult<IReadOnlyList<PriceRequest>?>(requests);

    private static async Task<(long Claim, FakeNonWorkflowCharges Charges)> PictureToolClaimAsync(
        Func<PriceRequest, long> estimate, Func<IDictionary<string, object?>?, CancellationToken, ValueTask<IReadOnlyList<PriceRequest>?>> worstCase)
    {
        FakeNonWorkflowCharges charges = new() { EstimateBy = estimate };
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        AssistantTurnGuard guard = new(charges, turn, null, Budget()) { ResolvePictureToolWorstCase = worstCase };
        await guard.Gate(AIFunctionFactory.Create(() => "x", name: "ApplyRunPictureTool")).InvokeAsync();
        return (charges.Claims.Single().StepMillis, charges);
    }

    /// <summary>
    /// T-095: the layer the look was sized at is written into the very arguments the tool is invoked with,
    /// so a concurrent change to the picture cannot make the look send another layer than the one claimed. Nothing is
    /// pinned (or sized) when the turn is not charged.
    /// </summary>
    [Fact]
    public async Task Look_pins_the_sized_layer_into_the_call_arguments()
    {
        Guid sized = Guid.NewGuid();
        AIFunction look = AIFunctionFactory.Create((Guid? layerId = null) => layerId?.ToString() ?? "none", name: "LookAtPicture");
        int sizings = 0;
        ValueTask<LookSizing?> Sizer(IDictionary<string, object?>? _, CancellationToken __)
        {
            sizings++;
            return ValueTask.FromResult<LookSizing?>(new LookSizing(sized, 512L * 512));
        }

        FakeNonWorkflowCharges charges = new() { EstimateBy = r => (long)r.Quantity };
        TurnHold turn = await charges.BeginTurnAsync(Guid.NewGuid(), UsageSource.Assistant, Guid.NewGuid());
        AssistantTurnGuard guard = new(charges, turn, null, Budget())
        {
            ResolveVision = _ => ValueTask.FromResult<ResolvedInferenceProvider?>(VisionModel),
            ResolveLookPixels = Sizer
        };

        AIFunctionArguments arguments = new() { ["question"] = Question };
        object? sent = await guard.Gate(look).InvokeAsync(arguments);

        arguments["layerId"].Should().Be(sized.ToString());
        Text(sent).Should().Be(sized.ToString(), "the tool binds the pinned layer");

        AssistantTurnGuard uncharged = new(null, null, null, Budget())
        {
            ResolveVision = _ => ValueTask.FromResult<ResolvedInferenceProvider?>(VisionModel),
            ResolveLookPixels = Sizer
        };
        AIFunctionArguments untouched = new() { ["question"] = Question };
        Text(await uncharged.Gate(look).InvokeAsync(untouched)).Should().Be("none");
        untouched.ContainsKey("layerId").Should().BeFalse("nothing is charged, so nothing is sized or pinned");
        sizings.Should().Be(1);
    }

    private static string? Text(object? raw) => raw switch
    {
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e => e.GetString(),
        _ => raw?.ToString()
    };

    /// <summary>
    /// T-093, T-095: one image token per 750 pixels, at least 4,000 (the largest per-image charge
    /// of the priced vision vendors), at most the 40 MP look cap; unknown = the cap.
    /// </summary>
    [Theory]
    [InlineData(null, 53_334L)]
    [InlineData(0L, 4_000L)]
    [InlineData(512L * 512, 4_000L)]
    [InlineData(3_000_000L, 4_000L)]
    [InlineData(24_000_000L, 32_000L)]
    [InlineData(40_000_000L, 53_334L)]
    [InlineData(100_000_000L, 53_334L)]
    public void ImageTokensFor_counts_750_pixels_a_token_between_the_floor_and_the_cap(long? pixels, long tokens)
    {
        AssistantTurnGuard.ImageTokensFor(pixels).Should().Be(tokens);
    }

    private sealed class NullSink : IUsageEventSink
    {
        public Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>F-5.2: the caps' defaults: the round-trip input cap is the replay window at its largest; eight tool calls a step.</summary>
    [Fact]
    public void The_caps_default_to_the_replay_window_and_eight_tool_calls_and_are_configurable()
    {
        AssistantChatLimits.MaxRoundTripInputTokens(null).Should().Be(
            AssistantChatLimits.DefaultMaxReplayMessages * AssistantChatLimits.DefaultMaxMessageChars / 4);
        AssistantChatLimits.MaxToolCallsPerStep(null).Should().Be(8);

        IConfiguration configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assistant:MaxRoundTripInputTokens"] = "50000",
                ["Assistant:MaxToolCallsPerStep"] = "3"
            })
            .Build();
        AssistantChatLimits.MaxRoundTripInputTokens(configured).Should().Be(50_000);
        AssistantChatLimits.MaxToolCallsPerStep(configured).Should().Be(3);
        AssistantTurnBudget.For(configured, PlatformChat).Should().Be(
            new AssistantTurnBudget(PlatformChat, AssistantChatLimits.ClaimedOutputTokens(configured, InferenceProviderKind.Anthropic), 50_000, 3));
    }

    private async Task UseAllowanceAsync(Guid org)
    {
        for (int i = 0; i < 4; i++)
        {
            TurnHold t = await _host.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
            (await _host.Charges.SettleTurnAsync(t, 5_000)).Ok.Should().BeTrue();
        }
    }
}
