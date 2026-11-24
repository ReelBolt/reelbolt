using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The Voiceover hold under the default Enforce policy carries one line-shortening chat call and +30% TTS;
/// a Report step holds exactly what it always did. The shortener's own chat call is metered by the
/// metering chat client against the ambient workflow-step scope (not re-implemented here).
/// </summary>
public class VoiceoverEnforceHoldTests
{
    private static WorkflowStep Step(string? config) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = Guid.NewGuid(),
        AgentDefinitionId = Guid.NewGuid(),
        StepType = StepType.Voiceover,
        VoiceoverConfigJson = config
    };

    private static string Config(NarrationFitPolicy policy) => JsonSerializer.Serialize(
        new VoiceoverStepConfig(new VoiceoverSource(VoiceoverSourceKind.Inline, [new VoiceoverLine("hi", 0, null)]), NarrationFitPolicy: policy),
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });

    private static decimal Tts(StepUsageProfile p) => p.Quantities.Single(q => q.Kind == UsageKind.TtsInputBytes).Quantity;

    [Fact]
    public void Report_holds_exactly_the_old_profile()
    {
        StepUsageProfile profile = StepUsageProfiles.For(Step(Config(NarrationFitPolicy.Report)), null);
        profile.Should().BeEquivalentTo(StepUsageProfiles.For(StepType.Voiceover, null));
        profile.HasChatPart.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"narrationFitPolicy\":\"Enforce\"}")]
    public void Enforce_and_absent_policy_hold_plus_30_percent_tts_and_one_chat_call(string? config)
    {
        StepUsageProfile basis = StepUsageProfiles.For(StepType.Voiceover, null);
        StepUsageProfile profile = StepUsageProfiles.For(Step(config ?? Config(NarrationFitPolicy.Enforce)), null);
        Tts(profile).Should().Be(Tts(basis) * 1.3m);
        profile.HasChatPart.Should().BeTrue();
        profile.PricedAs.Should().Be(AgentType.NarrationWriter);
        profile.Quantities.Count.Should().BeGreaterThan(basis.Quantities.Count);
    }

    [Fact]
    public void A_config_with_no_narrationFitPolicy_key_resolves_to_Enforce()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Config(NarrationFitPolicy.Report))!.AsObject();
        node.Remove("narrationFitPolicy").Should().BeTrue("the fixture must really drop the key");
        string json = node.ToJsonString();
        json.Should().NotContainEquivalentOf("narrationFitPolicy");

        StepUsageProfile basis = StepUsageProfiles.For(StepType.Voiceover, null);
        StepUsageProfile profile = StepUsageProfiles.For(Step(json), null);
        profile.HasChatPart.Should().BeTrue("an absent policy is Enforce");
        Tts(profile).Should().Be(Tts(basis) * 1.3m);
        JsonSerializer.Deserialize<VoiceoverStepConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })!
            .NarrationFitPolicy.Should().Be(NarrationFitPolicy.Enforce);
    }

    private sealed class FakeChat(string answer, bool throws = false) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throws ? throw new InvalidOperationException("down") : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static NarrationLineShortener Shortener(IChatClient chat)
    {
        var provider = new Mock<IAgentChatClientProvider>();
        provider.Setup(p => p.GetAsync(AgentType.NarrationWriter, null, It.IsAny<CancellationToken>())).Returns(new ValueTask<IChatClient>(chat));
        return new NarrationLineShortener(provider.Object, NullLogger<NarrationLineShortener>.Instance);
    }

    private static readonly NarrationLineOverflow[] Lines = [new(0, "a very long line indeed with many many words", 9, 3.5, 6)];

    [Fact]
    public async Task A_valid_answer_is_accepted()
    {
        var map = await Shortener(new FakeChat("```json\n{\"lines\":[{\"index\":0,\"text\":\"A shorter line.\"}]}\n```")).ShortenAsync(Lines, "en", default);
        map.Should().NotBeNull();
        map![0].Should().Be("A shorter line.");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"lines\":[{\"index\":0,\"text\":\"one two three four five six seven\"}]}")]
    [InlineData("{\"lines\":[{\"index\":0,\"text\":\"\"}]}")]
    public async Task An_invalid_answer_is_rejected(string answer)
    {
        (await Shortener(new FakeChat(answer)).ShortenAsync(Lines, "en", default)).Should().BeNull();
    }

    [Fact]
    public async Task A_failing_chat_client_degrades_to_no_help()
    {
        (await Shortener(new FakeChat("", throws: true)).ShortenAsync(Lines, "en", default)).Should().BeNull();
    }
}
