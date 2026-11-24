using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows.ChangeRequests;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>The exact price of just the redone clips, takes or lines, from a fake price table (no vendor call).</summary>
public sealed class StepChangeExactPriceTests
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

    private static ChangeRequestPayload Named(params string[] ids) => new(null, ids, null);

    private static (Mock<IInferenceProviderResolver> Resolver, Mock<IModelPriceService> Prices, List<PriceRequest> Asked) Wire(bool priced = true)
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedVideoGenerationProvider(Guid.NewGuid(), "v", InferenceProviderKind.FalAi, "", "m1", "k", 60, AccountId: Guid.NewGuid()));
        resolver.Setup(r => r.ResolveMusicGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.MusicGeneration, "", "fal-ai/elevenlabs/music", "test", 60, AccountId: Guid.NewGuid()));
        List<PriceRequest> asked = [];
        Mock<IModelPriceService> prices = new();
        if (priced)
        {
            prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PriceRequest, CancellationToken>((r, _) => asked.Add(r))
                .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
        }
        else
        {
            prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new UnpricedUsageException(UsageKind.VideoGenSeconds, InferenceProviderKind.FalAi, "m1"));
        }

        return (resolver, prices, asked);
    }

    [Fact]
    public async Task Two_named_clips_of_four_are_priced_by_their_own_seconds()
    {
        (var resolver, var prices, var asked) = Wire();
        WorkflowStep step = new() { StepType = StepType.VideoGenerate, VideoGenerateConfigJson = """{"planSource":"Inline","takes":4,"durationSeconds":5,"prompt":"x"}""" };
        string output = """{"clips":[{"clipId":"v1","status":"succeeded","durationSeconds":4},{"clipId":"v2","status":"succeeded","durationSeconds":6},{"clipId":"v3","status":"succeeded","durationSeconds":5},{"clipId":"v4","status":"succeeded","durationSeconds":5}]}""";

        StepChangeExactPrice? exact = await StepChangeExactPriceCalculator.TryComputeAsync(step, Named("v1", "v2"), output, resolver.Object, prices.Object, CancellationToken.None);

        exact!.Millis.Should().Be(10_000, "4 s + 6 s at 1 credit a second");
        asked.Should().ContainSingle().Which.Unit.Should().Be(UsageKind.VideoGenSeconds);
    }

    [Fact]
    public async Task A_take_is_priced_by_its_started_minutes()
    {
        (var resolver, var prices, var asked) = Wire();
        WorkflowStep step = new() { StepType = StepType.AudioGenerate, AudioGenerateConfigJson = """{"kind":"Music","prompt":"x","durationSec":90,"takes":2}""" };
        string output = """{"takes":[{"take":1,"status":"succeeded","durationSec":90},{"take":2,"status":"succeeded","durationSec":30}]}""";

        StepChangeExactPrice? exact = await StepChangeExactPriceCalculator.TryComputeAsync(step, Named("a1"), output, resolver.Object, prices.Object, CancellationToken.None);

        exact!.Millis.Should().Be(2_000, "a 90 s take is two started minutes");
        asked.Should().ContainSingle().Which.Unit.Should().Be(UsageKind.MusicGenMinutes);
    }

    [Fact]
    public async Task A_line_is_priced_by_its_utf8_bytes()
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "tts", InferenceProviderKind.FishAudio, "", "s1", "k", 60, AccountId: Guid.NewGuid()));
        List<PriceRequest> asked = [];
        Mock<IModelPriceService> prices = new();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PriceRequest, CancellationToken>((r, _) => asked.Add(r))
            .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)r.Quantity * 10, 0m, null));
        WorkflowStep step = new() { StepType = StepType.Voiceover, VoiceoverConfigJson = """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":2}}""" };
        string output = """{"view":{"lines":[{"id":"vo0","status":"ok","durationSec":2,"text":"Héllo"},{"id":"vo1","status":"ok","durationSec":3,"text":"World"}]}}""";

        StepChangeExactPrice? exact = await StepChangeExactPriceCalculator.TryComputeAsync(step, Named("vo0"), output, resolver.Object, prices.Object, CancellationToken.None);

        exact!.Millis.Should().Be(60, "Héllo is 6 UTF-8 bytes");
        asked.Should().ContainSingle().Which.Unit.Should().Be(UsageKind.TtsInputBytes);
    }

    [Fact]
    public async Task A_whole_step_redo_names_no_item_and_is_null()
    {
        (var resolver, var prices, _) = Wire();
        WorkflowStep step = new() { StepType = StepType.VideoGenerate, VideoGenerateConfigJson = """{"planSource":"Inline","takes":1,"durationSeconds":5,"prompt":"x"}""" };

        (await StepChangeExactPriceCalculator.TryComputeAsync(step, new ChangeRequestPayload("again", null, null), """{"clips":[{"clipId":"v1","durationSeconds":5}]}""", resolver.Object, prices.Object, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_price_is_null()
    {
        (var resolver, var prices, _) = Wire(priced: false);
        WorkflowStep step = new() { StepType = StepType.VideoGenerate, VideoGenerateConfigJson = """{"planSource":"Inline","takes":2,"durationSeconds":5,"prompt":"x"}""" };
        string output = """{"clips":[{"clipId":"v1","status":"succeeded","durationSeconds":5},{"clipId":"v2","status":"succeeded","durationSeconds":5}]}""";

        (await StepChangeExactPriceCalculator.TryComputeAsync(step, Named("v1"), output, resolver.Object, prices.Object, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_higgsfield_clip_and_unreadable_output_are_null()
    {
        (var resolver, var prices, _) = Wire();
        resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedVideoGenerationProvider(Guid.NewGuid(), "h", InferenceProviderKind.Higgsfield, "", "m1", "k", 60));
        WorkflowStep step = new() { StepType = StepType.VideoGenerate, VideoGenerateConfigJson = """{"planSource":"Inline","takes":2,"durationSeconds":5,"prompt":"x"}""" };
        string output = """{"clips":[{"clipId":"v1","status":"succeeded","durationSeconds":5},{"clipId":"v2","status":"succeeded","durationSeconds":5}]}""";

        (await StepChangeExactPriceCalculator.TryComputeAsync(step, Named("v1"), output, resolver.Object, prices.Object, CancellationToken.None)).Should().BeNull();
        (await StepChangeExactPriceCalculator.TryComputeAsync(step, Named("v1"), "not json", resolver.Object, prices.Object, CancellationToken.None)).Should().BeNull();
    }
}
