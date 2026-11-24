using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The shortener's chat call is metered inside the Voiceover step's own scope (source Workflow with the step's
/// result id), and a final NARRATION_DOES_NOT_FIT on platform-written narration refunds the whole step, the
/// shortener's charge included. No paid calls: the chat client is a fake that reports fixed usage.
/// </summary>
public sealed class VoiceoverShortenerBillingTests : IDisposable
{
    private const long Grant = 100_000;
    private const long TtsCharge = 3_000;

    private readonly UnsettledHoldTestKit _kit = new();
    public void Dispose() => _kit.Dispose();

    private static readonly NarrationLineOverflow[] Lines = [new(0, "a very long line indeed with many many words", 9, 3.5, 6)];

    private static async Task<(CapturingSink Sink, long ShortenerMillis)> RunShortenerAsync(Guid org, Guid execution, Guid stepResult)
    {
        var sink = new CapturingSink();
        var recorder = new UsageRecorder(new StaticPriceService(), sink, NullLogger<UsageRecorder>.Instance);
        IChatClient metered = new MeteringChatClient(
            new UsageReportingChatClient(inputTokens: 100, outputTokens: 20),
            new ResolvedInferenceProvider(Guid.NewGuid(), "chat", InferenceProviderKind.OpenAICompatible, "http://c", "chat-1", "", 30),
            recorder);
        var provider = new Mock<IAgentChatClientProvider>();
        provider.Setup(p => p.GetAsync(AgentType.NarrationWriter, null, It.IsAny<System.Threading.CancellationToken>()))
            .Returns(new ValueTask<IChatClient>(metered));
        var shortener = new NarrationLineShortener(provider.Object, NullLogger<NarrationLineShortener>.Instance);

        await recorder.StartAsync(default);
        using (UsageScope.Begin(org, null, execution, stepResult, null, UsageSource.Workflow))
        {
            // The answer is not valid JSON: the call is still made, and still billed.
            await shortener.ShortenAsync(Lines, "en", default);
        }

        await recorder.StopAsync(default);
        return (sink, sink.Events.Sum(e => e.CreditsMillis));
    }

    [Fact]
    public async Task The_shortener_chat_call_is_recorded_as_a_workflow_step_usage_event()
    {
        Guid org = Guid.NewGuid(), exec = Guid.NewGuid(), step = Guid.NewGuid();
        (CapturingSink sink, long millis) = await RunShortenerAsync(org, exec, step);

        sink.Events.Should().NotBeEmpty();
        sink.Events.Should().OnlyContain(e => e.Source == UsageSource.Workflow && e.StepResultId == step && e.ExecutionId == exec && e.OrganizationId == org);
        sink.Events.Select(e => e.Kind).Should().Contain(UsageKind.LlmInputTokens).And.Contain(UsageKind.LlmOutputTokens);
        millis.Should().Be(100 * 2 + 20 * 8);
    }

    [Fact]
    public async Task A_final_fit_failure_on_platform_narration_refunds_the_whole_step_including_the_shortener_call()
    {
        FailureClassifier.ForCode("NARRATION_DOES_NOT_FIT").Should().Be(FailureClass.PlatformInternal);

        await _kit.GrantAsync(Grant);
        (Guid exec, Guid step, Guid reserve) = await _kit.HoldAsync(StepStatus.Running);
        (_, long shortenerMillis) = await RunShortenerAsync(_kit.Org, exec, step);
        shortenerMillis.Should().BeGreaterThan(0);

        CreditGate gate = _kit.Gate();
        long metered = TtsCharge + shortenerMillis;
        (await gate.SettleAsync(reserve, metered)).Ok.Should().BeTrue();
        (await _kit.BalanceAsync()).BalanceMillis.Should().Be(Grant - metered);

        CreditSettlement released = await gate.ReleaseAsync(reserve, FailureClassifier.ForCode("NARRATION_DOES_NOT_FIT"));

        released.Ok.Should().BeTrue();
        released.RefundedMillis.Should().Be(metered, "the TTS and the shortener call are both refunded");
        CreditLedgerResult balance = await _kit.BalanceAsync();
        balance.BalanceMillis.Should().Be(Grant, "nothing is kept");
        balance.ReservedMillis.Should().Be(0);
    }
}
