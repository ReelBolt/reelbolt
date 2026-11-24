using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;

/// <summary>
/// Tests for <see cref="VoiceoverStepExecutor"/> covering the TDD acceptance criteria:
/// 1. 2-line Inline success with matching durationSec
/// 2. Byte-budget guardrail with client never invoked
/// 3. Per-line instance cache (2nd ExecuteAsync doesn't re-invoke)
/// 4. Never-throws discipline (1 of 3 fails, overall Completed)
/// 5. Invalid config -> Failed
/// 6a. ScriptScenes with non-empty + empty scene -> exactly 1 VoiceoverLine
/// 6b. ScriptScenes with missing step order -> Failed
/// 7. WorkflowStep with VideoTransform AgentType runs with no chat-client error
/// </summary>
public partial class VoiceoverStepExecutorTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static VoiceoverStepExecutor CreateExecutor(
        Mock<ISpeechSynthesisClientFactory> speechFactory,
        Mock<IInferenceProviderResolver> providerResolver,
        Mock<IMediaProbe> mediaProbe,
        Mock<IProjectFileWorkspace> workspace,
        ILogger<VoiceoverStepExecutor> logger = null,
        IVoiceoverAlignmentService alignmentService = null,
        IContentModerationGate moderation = null)
    {
        return new VoiceoverStepExecutor(
            speechFactory.Object,
            providerResolver.Object,
            mediaProbe.Object,
            workspace.Object,
            alignmentService ?? new StubAlignmentService(),
            logger ?? NullLogger<VoiceoverStepExecutor>.Instance,
            voiceResolver: new NoRowsVoiceResolver(),
            moderation: moderation);
    }

    /// <summary>
    /// The alignment double every case in this file runs against unless it opts out.
    ///
    /// <para>
    /// Its default answer is <see cref="VoiceoverAlignmentReasons.NoTranscriptionProvider"/> —
    /// the documented degradation for a deployment with no ASR configured. That default is
    /// deliberate: it means each pre-existing case observes exactly the words-less <c>ok</c> line it
    /// observed before alignment existed, so those assertions still fail only for the reason they
    /// were written to catch. Cases about alignment itself pass an explicit behaviour.
    /// </para>
    /// </summary>
    private sealed class StubAlignmentService : IVoiceoverAlignmentService
    {
        private readonly Func<string, VoiceoverAlignmentResult> _result;

        public StubAlignmentService(Func<string, VoiceoverAlignmentResult> result = null)
        {
            _result = result ?? (_ => VoiceoverAlignmentResult.NotApplied(
                VoiceoverAlignmentReasons.NoTranscriptionProvider));
        }

        /// <summary>Every storage key this double was asked to align, in call order.</summary>
        public List<string> Calls { get; } = [];

        public Task<VoiceoverAlignmentResult> AlignAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            Calls.Add(storageKey);
            return Task.FromResult(_result(storageKey));
        }
    }

    private static VoiceoverAlignmentResult AlignedWords(params (string Text, double StartSec, double EndSec)[] words) =>
        VoiceoverAlignmentResult.Aligned(
            words.Select(w => new VoiceoverAlignedWord(w.Text, w.StartSec, w.EndSec)).ToList());

    private static VoiceoverAlignmentResult Unaligned(string reason) =>
        VoiceoverAlignmentResult.NotApplied(reason);

    private static StepExecutionContext CreateContext(
        string voiceoverConfigJson,
        Guid? executionId = null,
        Guid? stepId = null,
        CancellationToken cancellationToken = default)
    {
        var step = new WorkflowStep
        {
            Id = stepId ?? Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = voiceoverConfigJson
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution
            {
                Id = executionId ?? Guid.NewGuid(),
                ProjectId = ProjectId
            },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = cancellationToken
        };
    }

    private static JsonElement ParseOutput(StepExecutionResult result) =>
        JsonDocument.Parse(result.Output).RootElement.Clone();

    /// <summary>
    /// A minimal but CANONICAL RIFF/WAVE container. <see cref="VoiceoverStepExecutor"/> refuses to
    /// upload a synthesized payload whose first four bytes are not ASCII "RIFF" or whose bytes 8..11
    /// are not "WAVE" — under a content-addressed key a bad object is a permanent, trusted cache
    /// entry — so every fixture that must produce an <c>ok</c> line has to hand it a real container.
    /// Whether the stream inside is decodable is a separate gate, checked through the mocked
    /// <see cref="IMediaProbe"/>; the payload carries four bytes of silence so it is not degenerate.
    /// </summary>
    private static byte[] ValidWavBytes() =>
    [
        // "RIFF" + chunk size (36 + 4 bytes of data)
        0x52, 0x49, 0x46, 0x46, 0x28, 0x00, 0x00, 0x00,
        // "WAVE"
        0x57, 0x41, 0x56, 0x45,
        // "fmt " + 16-byte PCM format chunk
        0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00,
        0x01, 0x00,             // PCM
        0x01, 0x00,             // mono
        0x40, 0x1F, 0x00, 0x00, // 8000 Hz
        0x40, 0x1F, 0x00, 0x00, // byte rate
        0x01, 0x00,             // block align
        0x08, 0x00,             // 8 bits per sample
        // "data" + 4 bytes of silence
        0x64, 0x61, 0x74, 0x61, 0x04, 0x00, 0x00, 0x00,
        0x80, 0x80, 0x80, 0x80
    ];

    // =====================================================================
    // Test 1: 2-line Inline success with matching durationSec
    // =====================================================================

    [Fact]
    public async Task TwoLineInlineSuccess_BothLinesOk_WithMatchingDurationSec()
    {
        // Arrange
        var lines = new[]
        {
            new VoiceoverLine("Hello world", 0.0, null),
            new VoiceoverLine("This is a test", 2.5, null)
        };

        var config = new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, lines),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000
        );

        string configJson = SerializeConfig(config);
        StepExecutionContext context = CreateContext(configJson);

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var mediaProbe = new Mock<IMediaProbe>();
        var workspace = new Mock<IProjectFileWorkspace>();

        // Setup provider resolution
        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.NewGuid(), "test-provider", InferenceProviderKind.FishAudio, "http://localhost:9000", "test-model", "test-key", 30);
        providerResolver
            .Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        // Setup speech synthesis client
        var mockClient = new Mock<ISpeechSynthesisClient>();
        speechFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        // Setup synthesis to return valid audio
        mockClient
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest req, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(
                    new MemoryStream(ValidWavBytes()), "audio/wav"))
            );

        // Setup media probe to return specific durations based on call count
        var probeCallCount = 0;
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) =>
            {
                // Return 1.5 sec for first line, 2.0 sec for second line
                double duration = (probeCallCount++ == 0) ? 1.5 : 2.0;
                return Task.FromResult(new MediaProbeResult(
                    DurationSec: duration,
                    FpsNum: 30,
                    FpsDen: 1,
                    Width: 0,
                    Height: 0,
                    VideoCodec: null,
                    AudioCodec: "aac",
                    AudioSampleRate: 48000));
            });

        // Setup workspace upload
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("voiceover/artifact.json");

        var executor = CreateExecutor(speechFactory, providerResolver, mediaProbe, workspace);

        // Act
        StepExecutionResult result = await executor.ExecuteAsync(context);

        // Assert
        result.Status.Should().Be(StepStatus.Completed);

        JsonElement root = ParseOutput(result);
        JsonElement lines_array = root.GetProperty("view").GetProperty("lines");

        lines_array.GetArrayLength().Should().Be(2);

        JsonElement line0 = lines_array[0];
        line0.GetProperty("id").GetString().Should().Be("vo0");
        line0.GetProperty("status").GetString().Should().Be("ok");
        line0.GetProperty("durationSec").GetDouble().Should().Be(1.5);

        JsonElement line1 = lines_array[1];
        line1.GetProperty("id").GetString().Should().Be("vo1");
        line1.GetProperty("status").GetString().Should().Be("ok");
        line1.GetProperty("durationSec").GetDouble().Should().Be(2.0);

        JsonElement meta = root.GetProperty("meta");
        meta.GetProperty("totalLines").GetInt32().Should().Be(2);
        meta.GetProperty("failedLines").GetInt32().Should().Be(0);
    }

    // =====================================================================
    // Test 2: Byte-budget guardrail (client never invoked)
    // =====================================================================

    [Fact]
    public async Task ByteBudgetGuardrail_ExceedsMaxBytes_ClientNeverInvoked_FailureNamesByteCount()
    {
        // Arrange - create a config with small MaxBytes to trigger the guardrail
        var lines = new[]
        {
            new VoiceoverLine(new string('a', 500), 0.0, null),
            new VoiceoverLine(new string('b', 500), 2.5, null),
            new VoiceoverLine(new string('c', 500), 5.0, null)
        };

        var config = new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, lines),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 100 // Very small budget to trigger the guardrail
        );

        string configJson = SerializeConfig(config);
        StepExecutionContext context = CreateContext(configJson);

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var mediaProbe = new Mock<IMediaProbe>();
        var workspace = new Mock<IProjectFileWorkspace>();

        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.NewGuid(), "test-provider", InferenceProviderKind.FishAudio, "http://localhost:9000", "test-model", "test-key", 30);
        providerResolver
            .Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClient = new Mock<ISpeechSynthesisClient>();
        speechFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("voiceover/artifact.json");

        var executor = CreateExecutor(speechFactory, providerResolver, mediaProbe, workspace);

        // Act
        StepExecutionResult result = await executor.ExecuteAsync(context);

        // Assert
        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("BYTE_BUDGET_EXCEEDED");
        result.ErrorDetails.Should().Contain("bytes");
        result.ErrorDetails.Should().Contain("Client never invoked");

        // Verify synthesis client was never called
        mockClient.Verify(
            c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Speech synthesis client should never be invoked when byte budget is exceeded");
    }

    // =====================================================================
    // Test 3: Content-addressed per-line artifact (the object store IS the cache)
    // =====================================================================

    /// <summary>
    /// Builds a fresh executor over a shared <see cref="IProjectFileWorkspace"/>, mirroring how
    /// production wires this step: one long-lived executor, but — the point of the ticket — no
    /// per-instance cache that a repeat run could hit. The store is the only memory between runs.
    /// </summary>
    private static VoiceoverStepExecutor CreateExecutorWithStore(
        ISpeechSynthesisClient client,
        IProjectFileWorkspace workspace,
        IMediaProbe mediaProbe)
        => CreateExecutorWithStore(client, workspace, mediaProbe, DefaultStoreProvider());

    /// <summary>
    /// The provider the three-argument overload resolves to. Extracted so the provider-accepting
    /// overload below can be the single implementation, and so the identity of the default is
    /// visible in one place.
    /// </summary>
    private static ResolvedSpeechSynthesisProvider DefaultStoreProvider() =>
        new(Guid.NewGuid(), "test-provider", InferenceProviderKind.FishAudio,
            "http://localhost:9000", "test-model", "test-key", 30);

    /// <summary>
    /// The same thing, but with the resolved provider supplied by the caller. The content address
    /// covers the provider identity and the EFFECTIVE model — which is the provider row's
    /// <c>ModelName</c> whenever the step config leaves <c>Model</c> at its null default — so a test
    /// that varies either of those has to be able to say which provider the step resolves.
    /// </summary>
    private static VoiceoverStepExecutor CreateExecutorWithStore(
        ISpeechSynthesisClient client,
        IProjectFileWorkspace workspace,
        IMediaProbe mediaProbe,
        ResolvedSpeechSynthesisProvider provider)
    {
        var providerResolver = new Mock<IInferenceProviderResolver>();
        providerResolver
            .Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        speechFactory.Setup(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>())).Returns(client);

        return new VoiceoverStepExecutor(
            speechFactory.Object,
            providerResolver.Object,
            mediaProbe,
            workspace,
            new StubAlignmentService(),
            NullLogger<VoiceoverStepExecutor>.Instance,
            new NoRowsVoiceResolver());
    }

    private static void SetupProbe(Mock<IMediaProbe> mediaProbe, double durationSec = 1.5)
    {
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(
                DurationSec: durationSec, FpsNum: 30, FpsDen: 1, Width: 0, Height: 0,
                VideoCodec: null, AudioCodec: "aac", AudioSampleRate: 48000));
    }

    [Fact]
    public async Task Two_consecutive_executions_of_the_same_line_emit_the_same_storageKey_and_synthesize_once()
    {
        // Arrange — a real in-memory object store, so "the second run skipped synthesis" is proven
        // by the artifact genuinely being there to find, not by a stubbed boolean.
        var client = new VoiceoverCrossExecutionArtifactTests.CountingSpeechSynthesisClient();
        var store = new VoiceoverCrossExecutionArtifactTests.InMemoryObjectStoreWorkspace();
        var mediaProbe = new Mock<IMediaProbe>();
        SetupProbe(mediaProbe);

        var lines = new[] { new VoiceoverLine("Same text for caching", 0.0, "voice-1") };
        var config = new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, lines),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000);

        string configJson = SerializeConfig(config);
        var stepId = Guid.NewGuid();

        var executor = CreateExecutorWithStore(client, store, mediaProbe.Object);

        // Act — two executions of the SAME line, different execution ids.
        StepExecutionResult result1 = await executor.ExecuteAsync(
            CreateContext(configJson, Guid.NewGuid(), stepId));
        StepExecutionResult result2 = await executor.ExecuteAsync(
            CreateContext(configJson, Guid.NewGuid(), stepId));

        // Assert
        result1.Status.Should().Be(StepStatus.Completed);
        result2.Status.Should().Be(StepStatus.Completed);

        client.CallCount.Should().Be(1,
            "the second execution must reuse the stored WAV rather than re-bill Fish Audio");

        JsonElement line1 = ParseOutput(result1).GetProperty("view").GetProperty("lines")[0];
        JsonElement line2 = ParseOutput(result2).GetProperty("view").GetProperty("lines")[0];

        // The emitted JSON property name is `storageKey` (JsonSerializerDefaults.Web camelCases the
        // record's StorageKey) — the contract T-066/T-067 consume.
        line1.TryGetProperty("storageKey", out JsonElement key1).Should().BeTrue(
            "each output line must carry its full storage key as `storageKey`");
        string key1Value = key1.GetString()!;
        line2.GetProperty("storageKey").GetString().Should().Be(key1Value);

        key1Value.Should().StartWith($"projects/{ProjectId}/agentFiles/voiceover/lines/");
        key1Value.Should().EndWith(".wav");
    }

    [Fact]
    public async Task A_different_sanitized_text_emits_a_different_storageKey_and_synthesizes_again()
    {
        // The other half of content-addressing: the key must be sensitive to the content.
        var client = new VoiceoverCrossExecutionArtifactTests.CountingSpeechSynthesisClient();
        var store = new VoiceoverCrossExecutionArtifactTests.InMemoryObjectStoreWorkspace();
        var mediaProbe = new Mock<IMediaProbe>();
        SetupProbe(mediaProbe);

        var executor = CreateExecutorWithStore(client, store, mediaProbe.Object);
        var stepId = Guid.NewGuid();

        string firstConfigJson = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, [new VoiceoverLine("Same text for caching", 0.0, "voice-1")]),
            ProviderId: Guid.NewGuid(), Model: "test-model", DefaultVoiceId: "default-voice", MaxBytes: 2_000_000));

        string secondConfigJson = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, [new VoiceoverLine("Entirely different narration", 0.0, "voice-1")]),
            ProviderId: Guid.NewGuid(), Model: "test-model", DefaultVoiceId: "default-voice", MaxBytes: 2_000_000));

        StepExecutionResult result1 = await executor.ExecuteAsync(
            CreateContext(firstConfigJson, Guid.NewGuid(), stepId));
        StepExecutionResult result2 = await executor.ExecuteAsync(
            CreateContext(secondConfigJson, Guid.NewGuid(), stepId));

        result1.Status.Should().Be(StepStatus.Completed);
        result2.Status.Should().Be(StepStatus.Completed);

        client.CallCount.Should().Be(2, "different text is different synthesis work");

        string key1 = ParseOutput(result1).GetProperty("view").GetProperty("lines")[0]
            .GetProperty("storageKey").GetString()!;
        string key2 = ParseOutput(result2).GetProperty("view").GetProperty("lines")[0]
            .GetProperty("storageKey").GetString()!;

        key2.Should().NotBe(key1);
    }

    // =====================================================================
    // Test 3c: CONDITION B — the content key must be INJECTIVE over (text, voiceId)
    // =====================================================================

    /// <summary>
    /// CONDITION B, first defect (security verdict <c>voiceover-104</c>, condition 2: "the content
    /// key is not injective").
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reviewer's colliding pair, driven END TO END through the executor over a real in-memory
    /// store. The old material joined its fields with a bare <c>'|'</c>:
    /// </para>
    /// <code>
    /// text = "Hello|world", voiceId = "abc"        ->  "Hello|world|abc|&lt;model&gt;|"
    /// text = "Hello",       voiceId = "world|abc"  ->  "Hello|world|abc|&lt;model&gt;|"
    /// </code>
    /// <para>
    /// <c>'|'</c> is a legal character in both fields and both are unvalidated workflow-config
    /// values, so the two hashed identically: the second line was never synthesized at all and
    /// narrated the first line's words. Nothing else in this file varies the VOICE ID — the
    /// existing content-addressing cases only ever vary the TEXT, which the old formula already
    /// covered — so the defect was invisible to the suite.
    /// </para>
    /// <para>
    /// Public surface only: two <c>ExecuteAsync</c> runs and the emitted JSON. No test calls the key
    /// helper, so the assertions describe the CONTRACT ("two different (text, voiceId) pairs are two
    /// different pieces of audio") rather than the implementation that happens to satisfy it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Text_and_voiceId_pairs_that_collide_under_a_delimiter_join_get_distinct_artifacts()
    {
        // Arrange — a real store plus a request-counting client, so "the second line was never
        // synthesized" is proven by a billed call that did or did not happen, not by a stub.
        var client = new VoiceoverCrossExecutionArtifactTests.CountingSpeechSynthesisClient();
        var store = new VoiceoverCrossExecutionArtifactTests.InMemoryObjectStoreWorkspace();
        var mediaProbe = new Mock<IMediaProbe>();
        SetupProbe(mediaProbe);

        // SAME provider, SAME model for both runs: the ONLY difference between the two configs is
        // where the '|' sits, which is exactly the difference the old key could not see.
        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "fish", InferenceProviderKind.FishAudio,
            "https://api.fish.audio", "s2.1-pro-free", "test-key", 30);

        string configOne = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline,
                [new VoiceoverLine("Hello|world", 0.0, "abc")]),
            ProviderId: provider.ProviderId,
            Model: "shared-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000));

        string configTwo = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline,
                [new VoiceoverLine("Hello", 0.0, "world|abc")]),
            ProviderId: provider.ProviderId,
            Model: "shared-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000));

        var stepId = Guid.NewGuid();

        // Act — two runs, one per line, against ONE shared store.
        StepExecutionResult resultOne = await CreateExecutorWithStore(client, store, mediaProbe.Object, provider)
            .ExecuteAsync(CreateContext(configOne, Guid.NewGuid(), stepId));
        StepExecutionResult resultTwo = await CreateExecutorWithStore(client, store, mediaProbe.Object, provider)
            .ExecuteAsync(CreateContext(configTwo, Guid.NewGuid(), stepId));

        // Assert
        resultOne.Status.Should().Be(StepStatus.Completed);
        resultTwo.Status.Should().Be(StepStatus.Completed);

        // The second line is different narration and therefore genuinely different work. Asserted
        // FIRST, so a collision fails here with a readable count rather than as a downstream index
        // error on the per-request checks below.
        client.CallCount.Should().Be(2,
            "the second (text, voiceId) pair is different audio and must really be synthesized, "
            + "not served the first line's bytes");

        // What the fixture actually fed the client — without this, a sanitizer rewrite could make
        // the two lines differ for a reason that has nothing to do with the delimiter collision.
        client.Requests[0].Text.Should().Be("Hello|world");
        client.Requests[0].VoiceId.Should().Be("abc");
        client.Requests[1].Text.Should().Be("Hello");
        client.Requests[1].VoiceId.Should().Be("world|abc");

        string keyOne = ParseOutput(resultOne).GetProperty("view").GetProperty("lines")[0]
            .GetProperty("storageKey").GetString()!;
        string keyTwo = ParseOutput(resultTwo).GetProperty("view").GetProperty("lines")[0]
            .GetProperty("storageKey").GetString()!;

        keyTwo.Should().NotBe(keyOne,
            "two distinct (text, voiceId) triples must not share a content address: an unescaped "
            + "delimiter join makes 'Hello|world'+'abc' and 'Hello'+'world|abc' hash alike, so the "
            + "second line narrates the first line's words");

        // Both objects really exist and are distinct entries in the store, not two names for one.
        store.LineArtifactKeys.Should().HaveCount(2);
    }

    // =====================================================================
    // Test 3d: CONDITION B — the key must cover the EFFECTIVE model and the provider identity
    // =====================================================================

    /// <summary>
    /// CONDITION B, second defect (same condition: "the new per-line content key omits provider
    /// identity and the EFFECTIVE model").
    /// </summary>
    /// <remarks>
    /// <para>
    /// The step config below leaves <c>Model</c> at its <see langword="null"/> default — which is
    /// the documented default and the shape that made this a defect. With <c>Model</c> null the
    /// model that actually synthesizes comes from the resolved provider row's <c>ModelName</c>
    /// (<c>SpeechSynthesisClientFactory</c> → <c>FishAudioSpeechSynthesisClient.ResolveEffectiveModel</c>),
    /// and the old hash read only <c>config.Model</c>. So editing that row's model, or re-pointing
    /// the step at a different provider row altogether, reused the previous backend's audio for
    /// byte-identical text and voice — silently, because the step still reports <c>ok</c>.
    /// </para>
    /// <para>
    /// The three providers differ in exactly one respect each, so a key that covered only one of the
    /// two would still fail here: B differs from A only in <c>ModelName</c>, C differs from A only
    /// in <c>ProviderId</c>/<c>Endpoint</c>. All three runs share one config and one store.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_effective_model_and_the_provider_identity_are_part_of_the_content_address()
    {
        var client = new VoiceoverCrossExecutionArtifactTests.CountingSpeechSynthesisClient();
        var store = new VoiceoverCrossExecutionArtifactTests.InMemoryObjectStoreWorkspace();
        var mediaProbe = new Mock<IMediaProbe>();
        SetupProbe(mediaProbe);

        var providerA = new ResolvedSpeechSynthesisProvider(
            Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa"), "fish-a", InferenceProviderKind.FishAudio,
            "https://api.fish.audio", "s2.1-pro-free", "test-key", 30);

        // Differs from A ONLY in the model the request will actually carry.
        var providerB = new ResolvedSpeechSynthesisProvider(
            providerA.ProviderId, "fish-a", InferenceProviderKind.FishAudio,
            providerA.Endpoint, "s2.1-pro", "test-key", 30);

        // Differs from A ONLY in provider identity and endpoint.
        var providerC = new ResolvedSpeechSynthesisProvider(
            Guid.Parse("cccccccc-2222-2222-2222-cccccccccccc"), "fish-c", InferenceProviderKind.FishAudio,
            "https://fish.audio.proxy.internal", providerA.ModelName, "test-key", 30);

        // Model: null is THE point of this case, not an oversight.
        string configJson = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline,
                [new VoiceoverLine("Same words, same voice", 0.0, "voice-1")]),
            ProviderId: null,
            Model: null,
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000));

        var stepId = Guid.NewGuid();

        // Act — three runs of the SAME text and voice, one per provider.
        StepExecutionResult resultA = await CreateExecutorWithStore(client, store, mediaProbe.Object, providerA)
            .ExecuteAsync(CreateContext(configJson, Guid.NewGuid(), stepId));
        StepExecutionResult resultB = await CreateExecutorWithStore(client, store, mediaProbe.Object, providerB)
            .ExecuteAsync(CreateContext(configJson, Guid.NewGuid(), stepId));
        StepExecutionResult resultC = await CreateExecutorWithStore(client, store, mediaProbe.Object, providerC)
            .ExecuteAsync(CreateContext(configJson, Guid.NewGuid(), stepId));

        // Assert
        resultA.Status.Should().Be(StepStatus.Completed);
        resultB.Status.Should().Be(StepStatus.Completed);
        resultC.Status.Should().Be(StepStatus.Completed);

        client.CallCount.Should().Be(3,
            "a different effective model or a different provider is a different backend, so each run "
            + "must synthesize rather than be served the previous backend's bytes");

        // The config itself never named a model or a provider — every difference the keys must see
        // comes from the resolution, which is precisely what the old formula could not read.
        client.Requests.Should().HaveCount(3);
        client.Requests.Should().OnlyContain(r => r.ModelOverride == null);
        client.Requests.Should().OnlyContain(r => r.Text == "Same words, same voice");
        client.Requests.Should().OnlyContain(r => r.VoiceId == "voice-1");

        string keyA = ParseOutput(resultA).GetProperty("view").GetProperty("lines")[0]
            .GetProperty("storageKey").GetString()!;
        string keyB = ParseOutput(resultB).GetProperty("view").GetProperty("lines")[0]
            .GetProperty("storageKey").GetString()!;
        string keyC = ParseOutput(resultC).GetProperty("view").GetProperty("lines")[0]
            .GetProperty("storageKey").GetString()!;

        keyB.Should().NotBe(keyA,
            "the EFFECTIVE model (the provider row's ModelName when config.Model is null) changes "
            + "the audio, so editing that row must not reuse the previous model's audio");
        keyC.Should().NotBe(keyA,
            "re-pointing the step at another provider row must miss the cache rather than serve the "
            + "old backend's audio for the same text and voice");

        store.LineArtifactKeys.Should().HaveCount(3);
    }

    // =====================================================================
    // Test 3b: Empty line is skipped, never synthesized, never billed
    // =====================================================================

    [Fact]
    public async Task EmptyLine_IsSkippedNotSynthesized_AndKeepsTheOutputListPositional()
    {
        // The security verdict's VoiceoverStepExecutor.cs:162 finding: a line whose SANITIZED text
        // is empty (whitespace-only, or reduced to nothing by the sanitizer) was still sent to Fish
        // Audio — a billed request for nothing.
        //
        // THIS IS LOAD-BEARING, which is why the assertion is about the SHAPE and not just the
        // count: ReactRemotionSandboxTools.StageVoiceoverAudio pairs non-empty Scriptwriter scenes
        // with voiceover lines POSITIONALLY. Dropping the entry would shift that mapping and stage
        // the wrong WAV for a later scene, so the skipped line must still occupy its slot.
        var lines = new[]
        {
            new VoiceoverLine("", 0.0, null),        // empty -> skipped
            new VoiceoverLine("   \t  ", 2.0, null), // whitespace-only -> skipped
            new VoiceoverLine("Real narration here", 4.0, null)
        };

        var config = new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, lines),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000);

        string configJson = SerializeConfig(config);

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var mediaProbe = new Mock<IMediaProbe>();
        var workspace = new Mock<IProjectFileWorkspace>();

        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.NewGuid(), "test-provider", InferenceProviderKind.FishAudio, "http://localhost:9000", "test-model", "test-key", 30);
        providerResolver
            .Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClient = new Mock<ISpeechSynthesisClient>();
        speechFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        // A non-empty payload with a real WAV-ish header: the executor now REFUSES to store an empty
        // body (a content-addressed key that holds garbage would be trusted by every later run), so
        // a mock returning an empty stream would fail this line for the wrong reason.
        mockClient
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SynthesizedSpeech(
                new MemoryStream(ValidWavBytes()), "audio/wav"));

        SetupProbe(mediaProbe);

        workspace
            .Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("projects/00000000-0000-0000-0000-000000000000/agentFiles/voiceover/artifact.json");

        var executor = CreateExecutor(speechFactory, providerResolver, mediaProbe, workspace);

        // Act
        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(configJson));

        // Assert
        result.Status.Should().Be(StepStatus.Completed,
            "a skipped line is not a failure");

        // The empty lines are never billed.
        mockClient.Verify(
            c => c.SynthesizeAsync(
                It.Is<SpeechRequest>(r => string.IsNullOrWhiteSpace(r.Text)),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "an empty sanitized line must never reach the synthesis client");
        mockClient.Verify(
            c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);

        JsonElement linesArray = ParseOutput(result).GetProperty("view").GetProperty("lines");

        // 1:1 with the input line list — the positional mapping depends on this.
        linesArray.GetArrayLength().Should().Be(3);

        linesArray[0].GetProperty("id").GetString().Should().Be("vo0");
        linesArray[0].GetProperty("status").GetString().Should().Be("skipped");
        linesArray[0].GetProperty("durationSec").GetDouble().Should().Be(0);
        linesArray[0].TryGetProperty("storageKey", out _).Should().BeFalse(
            "a skipped line has no artifact to name");

        linesArray[1].GetProperty("id").GetString().Should().Be("vo1");
        linesArray[1].GetProperty("status").GetString().Should().Be("skipped");

        linesArray[2].GetProperty("id").GetString().Should().Be("vo2");
        linesArray[2].GetProperty("status").GetString().Should().Be("ok");

        JsonElement meta = ParseOutput(result).GetProperty("meta");
        meta.GetProperty("totalLines").GetInt32().Should().Be(3);
        meta.GetProperty("failedLines").GetInt32().Should().Be(0,
            "skipped lines are not failures");
        meta.GetProperty("skippedLines").GetInt32().Should().Be(2);
    }

    // =====================================================================
    // Test 4: Never-throws (1 of 3 fails, overall Completed)
    // =====================================================================

    [Fact]
    public async Task NeverThrows_OneOf3LinesFails_OverallCompleted_FailedLineMarked()
    {
        // Arrange
        var lines = new[]
        {
            new VoiceoverLine("Success line 1", 0.0, null),
            new VoiceoverLine("Failure line", 2.0, null),
            new VoiceoverLine("Success line 2", 4.0, null)
        };

        var config = new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, lines),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000
        );

        string configJson = SerializeConfig(config);
        StepExecutionContext context = CreateContext(configJson);

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var mediaProbe = new Mock<IMediaProbe>();
        var workspace = new Mock<IProjectFileWorkspace>();

        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.NewGuid(), "test-provider", InferenceProviderKind.FishAudio, "http://localhost:9000", "test-model", "test-key", 30);
        providerResolver
            .Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClient = new Mock<ISpeechSynthesisClient>();
        speechFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        // Make synthesis throw for the middle line
        mockClient
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest req, CancellationToken _) =>
            {
                if (req.Text.Contains("Failure"))
                    throw new InvalidOperationException("Synthesis failed for this line");
                return Task.FromResult(new SynthesizedSpeech(
                    new MemoryStream(ValidWavBytes()), "audio/wav"));
            });

        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(DurationSec: 1.5, FpsNum: 30, FpsDen: 1, Width: 0, Height: 0, VideoCodec: null, AudioCodec: "aac", AudioSampleRate: 48000));

        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("voiceover/artifact.json");

        var executor = CreateExecutor(speechFactory, providerResolver, mediaProbe, workspace);

        // Act
        StepExecutionResult result = await executor.ExecuteAsync(context);

        // Assert
        result.Status.Should().Be(StepStatus.Completed,
            "Overall status should be Completed even though 1 of 3 lines failed");

        JsonElement root = ParseOutput(result);
        JsonElement lines_array = root.GetProperty("view").GetProperty("lines");

        lines_array.GetArrayLength().Should().Be(3);

        lines_array[0].GetProperty("status").GetString().Should().Be("ok");
        lines_array[1].GetProperty("status").GetString().Should().Be("failed", "The failure line should be marked as failed");
        lines_array[2].GetProperty("status").GetString().Should().Be("ok");

        JsonElement meta = root.GetProperty("meta");
        meta.GetProperty("totalLines").GetInt32().Should().Be(3);
        meta.GetProperty("failedLines").GetInt32().Should().Be(1);
    }

    // =====================================================================
    // Test 5: Invalid config -> Failed
    // =====================================================================

    [Fact]
    public async Task BadPronunciationOverride_FailsConfigInvalid_BeforeAnySpeechClientIsUsed()
    {
        StepExecutionContext context = CreateContext(
            "{\"source\":{\"kind\":\"Inline\",\"lines\":[{\"text\":\"Ciao\"}]},\"pronunciationOverrides\":[{\"term\":\"perché\",\"ipa\":\"a<|b\"}]}");

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var executor = CreateExecutor(speechFactory, providerResolver, new Mock<IMediaProbe>(), new Mock<IProjectFileWorkspace>());

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("CONFIG_INVALID").And.Contain("pronunciationOverrides[0].ipa");
        speechFactory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvalidConfig_MissingOrInvalidJson_ReturnsFailedWithConfigError()
    {
        // Arrange
        StepExecutionContext context = CreateContext("invalid json {]");

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var mediaProbe = new Mock<IMediaProbe>();
        var workspace = new Mock<IProjectFileWorkspace>();

        var executor = CreateExecutor(speechFactory, providerResolver, mediaProbe, workspace);

        // Act
        StepExecutionResult result = await executor.ExecuteAsync(context);

        // Assert
        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("CONFIG_INVALID");
    }

    [Fact]
    public async Task InvalidConfig_NullConfigJson_ReturnsFailedWithError()
    {
        // Arrange
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = null // Missing config
        };

        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var mediaProbe = new Mock<IMediaProbe>();
        var workspace = new Mock<IProjectFileWorkspace>();

        var executor = CreateExecutor(speechFactory, providerResolver, mediaProbe, workspace);

        // Act
        StepExecutionResult result = await executor.ExecuteAsync(context);

        // Assert
        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("CONFIG_INVALID");
    }

    // =====================================================================
    // Test 6a: ScriptScenes with non-empty + empty scene
    // =====================================================================

    [Fact]
    public async Task ScriptScenesSource_WithNonEmptyAndEmptyScene_ProducesOneVoiceoverLine()
    {
        // Arrange - Create a fake Scriptwriter output with 2 scenes
        var scriptwriterOutput = new ScriptwriterOutput
        {
            Title = "Test Script",
            DurationInSeconds = 15,
            Scenes = new List<ScriptScene>
            {
                new()
                {
                    SceneId = "scene-1",
                    StartTime = 5, // int, in seconds
                    Duration = 5,
                    Voiceover = "Hello from scriptwriter", // non-empty
                    OnScreenText = [],
                    VisualDescription = "Scene 1"
                },
                new()
                {
                    SceneId = "scene-2",
                    StartTime = 10, // int, in seconds
                    Duration = 5,
                    Voiceover = "", // empty - should produce no line
                    OnScreenText = [],
                    VisualDescription = "Scene 2"
                }
            },
            Narrative = "Test narrative"
        };

        // Serialize the scriptwriter output
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string scriptwriterOutputJson = JsonSerializer.Serialize(scriptwriterOutput, options);

        // Create a config pointing to scriptwriter step order 1
        var config = new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.ScriptScenes, ScriptwriterStepOrder: 1),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000
        );

        string configJson = SerializeConfig(config);

        // Create context with the scriptwriter output in history
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2, // This is the voiceover step
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = configJson
        };

        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = new[]
            {
                new StepOutputHistoryEntry(
                    StepOrder: 1,
                    StepLabel: "Scriptwriter",
                    Output: scriptwriterOutputJson)
            },
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var mediaProbe = new Mock<IMediaProbe>();
        var workspace = new Mock<IProjectFileWorkspace>();

        // Setup provider resolution
        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.NewGuid(), "test-provider", InferenceProviderKind.FishAudio, "http://localhost:9000", "test-model", "test-key", 30);
        providerResolver
            .Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClient = new Mock<ISpeechSynthesisClient>();
        speechFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        mockClient
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav"));

        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(DurationSec: 1.5, FpsNum: 30, FpsDen: 1, Width: 0, Height: 0, VideoCodec: null, AudioCodec: "aac", AudioSampleRate: 48000));

        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("voiceover/artifact.json");

        var executor = CreateExecutor(speechFactory, providerResolver, mediaProbe, workspace);

        // Act
        StepExecutionResult result = await executor.ExecuteAsync(context);

        // Assert
        result.Status.Should().Be(StepStatus.Completed);

        JsonElement root = ParseOutput(result);
        JsonElement lines_array = root.GetProperty("view").GetProperty("lines");

        // Only the non-empty voiceover should produce a line
        lines_array.GetArrayLength().Should().Be(1);

        JsonElement line0 = lines_array[0];
        line0.GetProperty("id").GetString().Should().Be("vo0");
        line0.GetProperty("status").GetString().Should().Be("ok");
        line0.GetProperty("startSec").GetDouble().Should().Be(5.0); // int StartTime 5 converted to double

        JsonElement meta = root.GetProperty("meta");
        meta.GetProperty("totalLines").GetInt32().Should().Be(1);
        meta.GetProperty("failedLines").GetInt32().Should().Be(0);
    }

    // =====================================================================
    // Test 6a-ii: ScriptScenes sanitize the scene's Voiceover text
    // =====================================================================

    /// <summary>
    /// A scene's narration in the shape the sanitizer exists for: an emotion tag in each syntax, a
    /// Cf (format) character on each side of a word, and a URL. The ScriptScenes branch hands this
    /// straight from a model-authored scriptwriter output to the TTS backend, so every one of
    /// these would otherwise be spoken aloud or choke the request.
    /// </summary>
    private const string SanitizerShapedSceneVoiceover =
        "[whispering] Hello (excited) \u200Bworld\u202E — see https://example.com/promo  now";

    /// <summary>
    /// What <see cref="VoiceoverTextSanitizer"/> must reduce the above to. Pinned as a literal
    /// rather than recomputed through the sanitizer itself, so a silent change to the sanitizer's
    /// behaviour is caught here instead of being mirrored into the expectation.
    /// </summary>
    private const string SanitizerShapedSceneVoiceoverSanitized = "Hello world — see now";

    /// <summary>
    /// CONDITION 4b. <c>ScriptScenesSource_WithNonEmptyAndEmptyScene_ProducesOneVoiceoverLine</c>
    /// feeds tag-free text the sanitizer maps to itself, so it cannot observe whether the sanitize
    /// call on the ScriptScenes path ran at all. This case feeds text the sanitizer must REWRITE
    /// and asserts on the payload the synthesis client was actually handed.
    /// </summary>
    [Fact]
    public async Task ScriptScenesSource_WithSanitizerShapedVoiceoverText_ReachesSynthesisSanitized()
    {
        // Arrange - a scriptwriter output whose only scene carries sanitizer-shaped narration.
        var scriptwriterOutput = new ScriptwriterOutput
        {
            Title = "Test Script",
            DurationInSeconds = 15,
            Scenes =
            [
                new ScriptScene
                {
                    SceneId = "scene-1",
                    StartTime = 3, // int, in seconds
                    Duration = 5,
                    Voiceover = SanitizerShapedSceneVoiceover,
                    OnScreenText = [],
                    VisualDescription = "Scene 1"
                }
            ],
            Narrative = "Test narrative"
        };

        string scriptwriterOutputJson = JsonSerializer.Serialize(
            scriptwriterOutput, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        VoiceoverStepConfig config = new(
            Source: new VoiceoverSource(VoiceoverSourceKind.ScriptScenes, ScriptwriterStepOrder: 1),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2, // mocks the voiceover step downstream of the scriptwriter
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = SerializeConfig(config)
        };

        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory =
            [
                new StepOutputHistoryEntry(
                    StepOrder: 1,
                    StepLabel: "Scriptwriter",
                    Output: scriptwriterOutputJson)
            ],
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        VoiceoverFakes fakes = CreateFakes();

        // Capture the request as it is MADE — the whole point of this case is what the client
        // receives, which no assertion on the step's own output can see.
        List<string> synthesizedTexts = [];
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest request, CancellationToken _) =>
            {
                synthesizedTexts.Add(request.Text);
                return Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav"));
            });

        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecodableProbe());

        var executor = CreateExecutor(
            fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace);

        // Act
        StepExecutionResult result = await executor.ExecuteAsync(context);

        // Assert
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        // The sanitized line is non-empty, so it is synthesized rather than skipped — otherwise the
        // assertions below would be reading an empty list and passing for the wrong reason.
        synthesizedTexts.Should().HaveCount(1, "the single non-empty scene produces exactly one synthesis call");
        string synthesizedText = synthesizedTexts[0];

        // The text that reached the client, asserted as a VALUE. Skipping the sanitize call on the
        // ScriptScenes path leaves the raw scene text here and fails both assertions.
        synthesizedText.Should().NotBe(
            SanitizerShapedSceneVoiceover,
            "model-authored scene text must never reach the TTS backend verbatim");
        synthesizedText.Should().Be(
            SanitizerShapedSceneVoiceoverSanitized,
            "the sanitizer's documented rewrite of tagged, Cf-carrying, URL-carrying narration");

        // The properties the sanitizer exists to guarantee, asserted independently of the exact
        // expected string, and each checked on the text that actually went out.
        foreach (string forbidden in new[] { "[", "]", "(", ")", "http", "\u200B", "\u202E" })
            synthesizedText.Should().NotContain(forbidden);

        // The step's own output still reports the line as a normal, successful one.
        JsonElement line0 = ParseOutput(result).GetProperty("view").GetProperty("lines")[0];
        line0.GetProperty("id").GetString().Should().Be("vo0");
        line0.GetProperty("status").GetString().Should().Be("ok");
        line0.GetProperty("startSec").GetDouble().Should().Be(3.0);
    }

    // =====================================================================
    // Test 6b: ScriptScenes with missing step order
    // =====================================================================

    [Fact]
    public async Task ScriptScenesSource_MissingStepOrder_ReturnsFailed()
    {
        // Arrange
        var config = new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.ScriptScenes, ScriptwriterStepOrder: 999), // Non-existent step
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000
        );

        string configJson = SerializeConfig(config);
        var context = CreateContext(configJson);

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var mediaProbe = new Mock<IMediaProbe>();
        var workspace = new Mock<IProjectFileWorkspace>();

        var executor = CreateExecutor(speechFactory, providerResolver, mediaProbe, workspace);

        // Act
        StepExecutionResult result = await executor.ExecuteAsync(context);

        // Assert
        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().NotBeNull();
    }

    // =====================================================================
    // Test 7: WorkflowStep with VideoTransform AgentType runs without chat-client error
    // =====================================================================

    [Fact]
    public async Task VideoTransformAgentStep_NoLlmDependency_CompletesWithoutChatClientError()
    {
        // Arrange
        // Create a step with AgentDefinitionId pointing to the seeded VideoTransform agent type
        var videoTransformAgentId = new Guid("00000000-0000-0000-0000-000000000001"); // Seeded VideoTransform ID (conventional)

        var lines = new[]
        {
            new VoiceoverLine("Test voiceover", 0.0, null)
        };

        var config = new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, lines),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000
        );

        string configJson = SerializeConfig(config);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = videoTransformAgentId,
            StepOrder = 1,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = configJson
        };

        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var mediaProbe = new Mock<IMediaProbe>();
        var workspace = new Mock<IProjectFileWorkspace>();

        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.NewGuid(), "test-provider", InferenceProviderKind.FishAudio, "http://localhost:9000", "test-model", "test-key", 30);
        providerResolver
            .Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var mockClient = new Mock<ISpeechSynthesisClient>();
        speechFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        mockClient
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav"));

        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(DurationSec: 1.5, FpsNum: 30, FpsDen: 1, Width: 0, Height: 0, VideoCodec: null, AudioCodec: "aac", AudioSampleRate: 48000));

        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("voiceover/artifact.json");

        var executor = CreateExecutor(speechFactory, providerResolver, mediaProbe, workspace);

        // Act & Assert - Should complete successfully with no chat-client dependency
        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed,
            "The step should complete successfully using the VideoTransform agent ID without requiring any chat-client");
    }

    // =====================================================================
    // Helper methods
    // =====================================================================

    private static string SerializeConfig(VoiceoverStepConfig config)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        return JsonSerializer.Serialize(config, options);
    }

    // =====================================================================
    // T-066 — condition 5: validate the synthesized payload before it is
    // uploaded under a content-addressed key, and say so when every line failed
    // =====================================================================

    /// <summary>
    /// The mocks every case below needs, wired to one resolved Fish Audio provider. Only the
    /// synthesis bytes and the probe behaviour differ per test, so those stay in the test.
    /// <see cref="Mock{IProjectFileWorkspace}.ArtifactExistsAsync"/> is deliberately left unstubbed:
    /// the loose mock answers <c>false</c>, which is the "nothing uploaded yet" state the synthesis
    /// path needs.
    /// </summary>
    private sealed record VoiceoverFakes(
        Mock<ISpeechSynthesisClientFactory> SpeechFactory,
        Mock<IInferenceProviderResolver> ProviderResolver,
        Mock<IMediaProbe> MediaProbe,
        Mock<IProjectFileWorkspace> Workspace,
        Mock<ISpeechSynthesisClient> Client);

    private static VoiceoverFakes CreateFakes()
    {
        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.NewGuid(), "test-provider", InferenceProviderKind.FishAudio,
            "http://localhost:9000", "test-model", "test-key", 30);

        var speechFactory = new Mock<ISpeechSynthesisClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var mediaProbe = new Mock<IMediaProbe>();
        var workspace = new Mock<IProjectFileWorkspace>();
        var client = new Mock<ISpeechSynthesisClient>();

        providerResolver
            .Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);
        speechFactory.Setup(f => f.Get(provider)).Returns(client.Object);

        // The step ALSO uploads its artifact.json through this member, so every "-never-uploaded"
        // assertion below is scoped to the .wav path rather than to the call as a whole.
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("projects/p/agentFiles/voiceover/artifact.json");

        return new VoiceoverFakes(speechFactory, providerResolver, mediaProbe, workspace, client);
    }

    private static string TwoLineConfigJson() => SerializeConfig(new VoiceoverStepConfig(
        Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[]
        {
            new VoiceoverLine("Hello world", 0.0, null),
            new VoiceoverLine("Second line", 2.5, null)
        }),
        ProviderId: Guid.NewGuid(),
        Model: "test-model",
        DefaultVoiceId: "default-voice",
        MaxBytes: 2_000_000));

    private static MediaProbeResult DecodableProbe() =>
        new(DurationSec: 1.5, FpsNum: 30, FpsDen: 1, Width: 0, Height: 0,
            VideoCodec: null, AudioCodec: "aac", AudioSampleRate: 48000);

    /// <summary>
    /// Two lines that never produce an <c>ok</c> one: the first is whitespace and is skipped, the
    /// second is non-empty and is driven into the per-line catch by a throwing synthesis client.
    /// </summary>
    private static string SkippedThenFailingConfigJson() => SerializeConfig(new VoiceoverStepConfig(
        Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[]
        {
            new VoiceoverLine("   ", 0.0, null),
            new VoiceoverLine("Second line", 2.5, null)
        }),
        ProviderId: Guid.NewGuid(),
        Model: "test-model",
        DefaultVoiceId: "default-voice",
        MaxBytes: 2_000_000));

    /// <summary>
    /// Three declared lines whose declared starts — 0.0, 2.0 and 6.0 — are what the fit solver reads
    /// as the DECLARED narration window: 2.0 s for the first line, 4.0 s for the second, and NONE for
    /// the third, which is the last declared line and therefore has no successor to end at. The word
    /// counts are 10, 3 and 2.
    /// </summary>
    private static string FitConfigJson() => SerializeConfig(new VoiceoverStepConfig(
        Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[]
        {
            new VoiceoverLine("one two three four five six seven eight nine ten", 0.0, null),
            new VoiceoverLine("second line here", 2.0, null),
            new VoiceoverLine("third line", 6.0, null)
        }),
        ProviderId: Guid.NewGuid(),
        Model: "test-model",
        DefaultVoiceId: "default-voice",
        MaxBytes: 2_000_000,
        NarrationFitPolicy: NarrationFitPolicy.Report));

    [Fact]
    public async Task NonWavPayload_IsNeverUploaded_AndDegradesEachLineToFailed()
    {
        // CONDITION 5a. Text is not audio. Under a content-addressed key an uploaded garbage object
        // is a permanently poisoned cache entry — every LATER execution finds it by HEAD on that
        // content key and trusts it as the real narration — so it must never reach the store.
        VoiceoverFakes fakes = CreateFakes();

        // A FRESH stream per call: ReturnsAsync(value) would hand the same MemoryStream to both
        // lines, and the second line would copy from EOF and fail for the wrong reason.
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest _, CancellationToken _) => Task.FromResult(new SynthesizedSpeech(
                new MemoryStream(Encoding.UTF8.GetBytes("not a wav at all")), "audio/wav")));

        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecodableProbe());

        var logger = new Mock<ILogger<VoiceoverStepExecutor>>();

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace, logger.Object)
            .ExecuteAsync(CreateContext(TwoLineConfigJson()));

        // Degrade the LINE, never the step.
        result.Status.Should().Be(StepStatus.Completed,
            "one bad payload degrades a line; the documented contract never fails the whole step");

        // The refusal comes first: it IS the requirement, the failed status is only its consequence.
        fakes.Workspace.Verify(
            w => w.UploadArtifactAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.Is<string>(p => p.EndsWith(".wav", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "a payload that is not a WAV must never be uploaded under its content-addressed key");

        JsonElement meta = ParseOutput(result).GetProperty("meta");
        meta.GetProperty("failedLines").GetInt32().Should().Be(2);
        ParseOutput(result).GetProperty("view").GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("status").GetString())
            .Should().AllBe("failed");

        // The container check is the FIRST gate, so ffprobe is never even consulted.
        fakes.MediaProbe.Verify(
            p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the RIFF/WAVE check short-circuits before the probe");

        // The rejection carries its own name, not a generic synthesis failure.
        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(e => e != null && e.Message.Contains("VOICEOVER_PAYLOAD_NOT_WAV", StringComparison.Ordinal)),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "the refusal must name its reason distinctly from a provider/network failure");
    }

    [Fact]
    public async Task RiffWavePayloadThatFfprobeCannotDecode_IsNeverUploaded_AndDegradesEachLineToFailed()
    {
        // CONDITION 5b. A well-formed container is not proof of usable audio. Both ways ffprobe can
        // reject it — throwing, or answering with no positive duration / no audio codec — must
        // refuse the upload, because the compile's own gate is exactly this pair.
        VoiceoverFakes fakes = CreateFakes();

        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest _, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

        int probeCall = 0;
        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
                probeCall++ == 0
                    ? Task.FromException<MediaProbeResult>(
                        new InvalidOperationException("ffprobe: invalid data found when processing input"))
                    : Task.FromResult(new MediaProbeResult(
                        DurationSec: 0, FpsNum: 30, FpsDen: 1, Width: 0, Height: 0,
                        VideoCodec: null, AudioCodec: null, AudioSampleRate: null)));

        var logger = new Mock<ILogger<VoiceoverStepExecutor>>();

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace, logger.Object)
            .ExecuteAsync(CreateContext(TwoLineConfigJson()));

        result.Status.Should().Be(StepStatus.Completed,
            "a line whose audio cannot be decoded degrades that line, it never fails the step");

        // The refusal comes first: it IS the requirement, the failed status is only its consequence.
        fakes.Workspace.Verify(
            w => w.UploadArtifactAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.Is<string>(p => p.EndsWith(".wav", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "an undecodable payload must never be uploaded under its content-addressed key");

        ParseOutput(result).GetProperty("meta").GetProperty("failedLines").GetInt32().Should().Be(2);
        ParseOutput(result).GetProperty("view").GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("status").GetString())
            .Should().AllBe("failed");

        // Both lines PASSED the container check and were refused by the probe gate — which is what
        // makes this test about (5b) rather than a repeat of (5a).
        probeCall.Should().Be(2, "the RIFF/WAVE check let both payloads through to the probe");

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(e => e != null && e.Message.Contains("VOICEOVER_PAYLOAD_NOT_DECODABLE", StringComparison.Ordinal)),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "the undecodable-container refusal must name its reason distinctly");
    }

    [Fact]
    public async Task OneFailingLineOutOfTwo_LeavesAllLinesFailedFalse_AndStillUploadsTheHealthyLine()
    {
        VoiceoverFakes fakes = CreateFakes();

        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest req, CancellationToken _) =>
                req.Text.Contains("Hello", StringComparison.Ordinal)
                    ? Task.FromException<SynthesizedSpeech>(new HttpRequestException("provider unreachable"))
                    : Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecodableProbe());

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContext(TwoLineConfigJson()));

        result.Status.Should().Be(StepStatus.Completed);

        JsonElement meta = ParseOutput(result).GetProperty("meta");
        meta.GetProperty("failedLines").GetInt32().Should().Be(1);
        meta.GetProperty("allLinesFailed").GetBoolean().Should().BeFalse(
            "one line failed and one succeeded, so the render still has narration");

        ParseOutput(result).GetProperty("view").GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("status").GetString())
            .Should().Equal("failed", "ok");

        fakes.Workspace.Verify(
            w => w.UploadArtifactAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.Is<string>(p => p.EndsWith(".wav", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the healthy line is still validated and uploaded");
    }

    [Fact]
    public async Task EveryLineFailing_LeavesTheStepCompleted_ButFlagsMetaAllLinesFailedAndLogsAtError()
    {
        // CONDITION 5, producer layer. The contract keeps the step Completed — deliberately — but a
        // step that narrated nothing must not be the last word an operator ever sees.
        VoiceoverFakes fakes = CreateFakes();

        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Fish Audio is unavailable"));

        Guid executionId = Guid.Parse("d0d0d0d0-1111-2222-3333-444444444444");
        var logger = new Mock<ILogger<VoiceoverStepExecutor>>();

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace, logger.Object)
            .ExecuteAsync(CreateContext(TwoLineConfigJson(), executionId: executionId));

        result.Status.Should().Be(StepStatus.Completed,
            "the documented contract is never throw, degrade one line, never fail the whole step");

        JsonElement meta = ParseOutput(result).GetProperty("meta");
        meta.GetProperty("failedLines").GetInt32().Should().Be(2);
        meta.GetProperty("allLinesFailed").GetBoolean().Should().BeTrue(
            "at least one line was declared and EVERY emitted line failed");

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains(executionId.ToString(), StringComparison.Ordinal) &&
                    state.ToString()!.Contains("2", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "an all-failed voiceover step must log at Error, naming the count and the execution");
    }

    // =====================================================================
    // T-202 — word alignment on the voiceover line
    // =====================================================================

    [Fact]
    public async Task Alignment_words_are_emitted_on_the_fresh_synthesis_path_and_summarised_in_meta()
    {
        VoiceoverFakes fakes = CreateFakes();

        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest _, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecodableProbe());

        var alignment = new StubAlignmentService(_ => AlignedWords(
            ("Hello", 0.00, 0.45),
            ("world", 0.45, 1.00)));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace,
                alignmentService: alignment)
            .ExecuteAsync(CreateContext(TwoLineConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        // One alignment call per `ok` line — including the line whose WAV this execution uploaded
        // moments earlier, which is the whole point: alignment runs on the synthesis path too, not
        // only on a cache hit.
        alignment.Calls.Should().HaveCount(2);

        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");
        lines.GetArrayLength().Should().Be(2);

        foreach (JsonElement line in lines.EnumerateArray())
        {
            line.GetProperty("status").GetString().Should().Be("ok");

            JsonElement words = line.GetProperty("words");
            words.GetArrayLength().Should().Be(2);
            words[0].GetProperty("text").GetString().Should().Be("Hello");
            words[0].GetProperty("startSec").GetDouble().Should().Be(0.00);
            words[0].GetProperty("endSec").GetDouble().Should().Be(0.45);
            words[1].GetProperty("text").GetString().Should().Be("world");
            words[1].GetProperty("startSec").GetDouble().Should().Be(0.45);
            words[1].GetProperty("endSec").GetDouble().Should().Be(1.00);

            line.TryGetProperty("alignmentReason", out _).Should().BeFalse(
                "an aligned line carries words and NO reason");
        }

        JsonElement alignmentMeta = ParseOutput(result).GetProperty("meta").GetProperty("alignment");
        alignmentMeta.GetProperty("applied").GetBoolean().Should().BeTrue();
        alignmentMeta.GetProperty("alignedLines").GetInt32().Should().Be(2);
        alignmentMeta.GetProperty("unalignedLines").GetInt32().Should().Be(0);
        alignmentMeta.GetProperty("reasons").EnumerateObject().Should().BeEmpty();

        // The alignment call receives the line's OWN storage key — the exact one the line reports —
        // not the execution id and not a reconstructed path: the service's whole contract is
        // (projectId, storageKey).
        foreach (JsonElement line in lines.EnumerateArray())
            alignment.Calls.Should().Contain(line.GetProperty("storageKey").GetString());
    }

    [Fact]
    public async Task Alignment_words_are_emitted_on_the_object_store_cache_hit_path_too()
    {
        // The phase-1 object store IS the per-line cache, so a repeat run takes a completely
        // different branch through this executor. Words must survive it: a second execution that
        // silently dropped them would leave captions and the fit solver with nothing to work from
        // on exactly the runs that are supposed to be the cheap ones.
        VoiceoverFakes fakes = CreateFakes();

        fakes.Workspace
            .Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        fakes.Workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecodableProbe());

        var alignment = new StubAlignmentService(_ => AlignedWords(("cached", 0.10, 0.80)));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace,
                alignmentService: alignment)
            .ExecuteAsync(CreateContext(TwoLineConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        // Proof this really took the cache-hit branch rather than the synthesis one.
        fakes.Client.Verify(
            c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "both lines were already in the object store");

        alignment.Calls.Should().HaveCount(2, "a cache hit must not lose the line's words");

        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");
        foreach (JsonElement line in lines.EnumerateArray())
        {
            line.GetProperty("status").GetString().Should().Be("ok");
            line.GetProperty("words").GetArrayLength().Should().Be(1);
            line.GetProperty("words")[0].GetProperty("text").GetString().Should().Be("cached");
        }

        ParseOutput(result).GetProperty("meta").GetProperty("alignment")
            .GetProperty("alignedLines").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task An_unaligned_ok_line_omits_words_carries_alignmentReason_and_is_counted_in_meta_reasons()
    {
        VoiceoverFakes fakes = CreateFakes();

        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest _, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecodableProbe());

        // Both lines are `ok` and keep their audio; only the word list is missing. A missing word
        // list must never cost a line its narration.
        var alignment = new StubAlignmentService(_ => Unaligned(VoiceoverAlignmentReasons.NoWords));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace,
                alignmentService: alignment)
            .ExecuteAsync(CreateContext(TwoLineConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");
        foreach (JsonElement line in lines.EnumerateArray())
        {
            line.GetProperty("status").GetString().Should().Be("ok");
            line.GetProperty("durationSec").GetDouble().Should().Be(1.5,
                "alignment is additive: it never changes what the line already reported");
            line.GetProperty("storageKey").GetString().Should().NotBeNullOrEmpty();

            line.TryGetProperty("words", out _).Should().BeFalse(
                "the words field is OMITTED, not null, when alignment did not apply");
            line.GetProperty("alignmentReason").GetString().Should().Be("NO_WORDS");
        }

        JsonElement alignmentMeta = ParseOutput(result).GetProperty("meta").GetProperty("alignment");
        alignmentMeta.GetProperty("applied").GetBoolean().Should().BeFalse();
        alignmentMeta.GetProperty("alignedLines").GetInt32().Should().Be(0);
        alignmentMeta.GetProperty("unalignedLines").GetInt32().Should().Be(2);
        alignmentMeta.GetProperty("reasons").GetProperty("NO_WORDS").GetInt32().Should().Be(2,
            "sum(reasons) == unalignedLines is the documented invariant");
    }

    [Fact]
    public async Task Skipped_and_failed_lines_are_never_aligned_and_carry_neither_field()
    {
        VoiceoverFakes fakes = CreateFakes();

        // Line 0 is empty after sanitization (skipped); line 1's synthesis throws (failed). Neither
        // has audio, so neither has an alignment outcome — and reporting one would be a fiction.
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Fish Audio is unavailable"));

        var alignment = new StubAlignmentService(_ => AlignedWords(("never", 0.0, 0.1)));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace,
                alignmentService: alignment)
            .ExecuteAsync(CreateContext(SkippedThenFailingConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        alignment.Calls.Should().BeEmpty("a skipped or failed line is never aligned");

        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");
        lines[0].GetProperty("status").GetString().Should().Be("skipped");
        lines[1].GetProperty("status").GetString().Should().Be("failed");

        foreach (JsonElement line in lines.EnumerateArray())
        {
            line.TryGetProperty("words", out _).Should().BeFalse();
            line.TryGetProperty("alignmentReason", out _).Should().BeFalse(
                "a line with no audio has no alignment outcome to record");
        }

        // meta.alignment is still PRESENT — a consumer never needs a null check — and reports the
        // absence honestly rather than inventing a reason.
        JsonElement alignmentMeta = ParseOutput(result).GetProperty("meta").GetProperty("alignment");
        alignmentMeta.GetProperty("applied").GetBoolean().Should().BeFalse();
        alignmentMeta.GetProperty("alignedLines").GetInt32().Should().Be(0);
        alignmentMeta.GetProperty("unalignedLines").GetInt32().Should().Be(0,
            "unalignedLines counts OK lines only: skipped and failed lines were never aligned");
        alignmentMeta.GetProperty("reasons").EnumerateObject().Should().BeEmpty();

        // The pre-existing counters are untouched by any of this.
        JsonElement meta = ParseOutput(result).GetProperty("meta");
        meta.GetProperty("totalLines").GetInt32().Should().Be(2);
        meta.GetProperty("skippedLines").GetInt32().Should().Be(1);
        meta.GetProperty("failedLines").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_cancelled_alignment_call_is_rethrown_and_the_remaining_lines_are_not_attempted()
    {
        // The service re-throws a caller cancellation instead of returning a reason, and the
        // executor must propagate it rather than converting it into a failed line: a run an operator
        // stopped is not a run that degraded.
        VoiceoverFakes fakes = CreateFakes();

        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest _, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecodableProbe());

        // The source is NOT pre-cancelled: the run is cancelled WHILE alignment is in flight, which
        // is the situation this test is about. (A pre-cancelled token would stop the step earlier,
        // at Stream.CopyToAsync, and never reach the alignment call at all — proving nothing about
        // this seam.)
        using var cts = new CancellationTokenSource();

        var alignment = new StubAlignmentService(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException();
        });

        Func<Task> act = () => CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace,
                alignmentService: alignment)
            .ExecuteAsync(CreateContext(TwoLineConfigJson(), cancellationToken: cts.Token));

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled run is reported as a cancellation, never as a degraded line or an executor defect");

        alignment.Calls.Should().HaveCount(1, "the loop stops at the cancellation instead of degrading every remaining line");

        fakes.Workspace.Verify(
            w => w.UploadArtifactAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.Is<string>(p => p.EndsWith("artifact.json", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "a cancelled run writes no artifact document describing words it never computed");
    }

    // =====================================================================
    // T-203 — the deterministic narration-fit facts
    // =====================================================================

    /// <summary>
    /// Wires the real <see cref="StepResultCache"/> over an in-memory
    /// <see cref="WorkflowEngineDbContext"/>, the same harness <see cref="StepResultCacheTests"/>
    /// uses. No S3 client is passed, so <see cref="StepCacheOptions.ValidateStorageArtifacts"/>
    /// degrades to "trust the row" exactly as it does in that file.
    /// </summary>
    private static StepResultCache NewStepResultCache(WorkflowEngineDbContext db) =>
        new(
            db,
            new StepCacheKeyBuilder(),
            new StepCachePolicy(),
            Options.Create(new StepCacheOptions()),
            NullLogger<StepResultCache>.Instance);

    /// <summary>
    /// The three Ok lines every fit case runs against: 3.5 s of narration for a line declared only
    /// 2.0 s before the next one starts, then two lines with room to spare. The durations are handed
    /// out ONE PER LINE, in emission order, because the fit arithmetic is about the relationship
    /// between a line's own audio and its own declared window — a single shared duration would make
    /// every line fit and prove nothing.
    /// </summary>
    private static void SetupPerLineDurations(Mock<IMediaProbe> mediaProbe, params double[] durationsSec)
    {
        var remaining = new Queue<double>(durationsSec);

        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => DecodableProbe() with { DurationSec = remaining.Dequeue() });
    }

    [Fact]
    public async Task Fit_is_attached_to_every_ok_line_and_summarised_in_meta()
    {
        VoiceoverFakes fakes = CreateFakes();

        // A FRESH stream per call, never one shared SynthesizedSpeech instance: the executor copies
        // the stream to a scratch file, so a single reused MemoryStream is already at its end for the
        // second line and every line after the first uploads zero bytes.
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest _, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

        // 3.5 s of audio for the line declared 2.0 s before the next one starts — an overrun — then
        // two lines with room to spare.
        SetupPerLineDurations(fakes.MediaProbe, 3.5, 1.0, 1.0);

        // Alignment applies to every ok line and returns TWO words for each, deliberately far fewer
        // than the ten words the first line actually speaks. alignedWordCount is evidence the report
        // carries; the assertions below pin that it moved none of the arithmetic.
        var alignment = new StubAlignmentService(_ => AlignedWords(("first", 0.0, 0.5), ("second", 0.5, 1.0)));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace,
                alignmentService: alignment)
            .ExecuteAsync(CreateContext(FitConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");
        lines.GetArrayLength().Should().Be(3);

        // The pre-existing per-line keys keep their exact names AND their exact order — `fit` is
        // APPENDED, never woven in, which is what keeps the seam lossless for every existing consumer.
        lines[0].EnumerateObject().Select(p => p.Name).Take(5)
            .Should().ContainInOrder("id", "startSec", "status", "durationSec", "storageKey");

        // Line 0: 10 words in 3.5 s = 2.857… words/s, inside a 2.0 s declared window, so it overruns
        // by 1.5 s — which at that rate is 4.285… words, ceiled to 5.
        lines[0].GetProperty("id").GetString().Should().Be("vo0");
        lines[0].GetProperty("status").GetString().Should().Be("ok");

        JsonElement fit0 = lines[0].GetProperty("fit");
        fit0.GetProperty("id").GetString().Should().Be("vo0");
        fit0.GetProperty("windowSec").GetDouble().Should().Be(2.0);
        fit0.GetProperty("windowSource").GetString().Should().Be("declared_starts");
        fit0.GetProperty("playDurationSec").GetDouble().Should().Be(3.5);
        fit0.GetProperty("wordCount").GetInt32().Should().Be(10,
            "the count is of the SANITIZED text that was actually spoken");
        fit0.GetProperty("alignedWordCount").GetInt32().Should().Be(2);
        fit0.GetProperty("wordsPerSecond").GetDouble().Should().Be(10.0 / 3.5);
        fit0.GetProperty("overflowSec").GetDouble().Should().Be(1.5);
        fit0.GetProperty("overflowWords").GetInt32().Should().Be(5);
        fit0.GetProperty("fits").GetBoolean().Should().BeFalse();

        // Line 1: it fits, and its window is the DECLARED one — from its own start (2.0) to the next
        // declared start (6.0), NOT its own 1.0 s of audio and not a picture window measured by the
        // compile step. The two measurements answer different questions and must not be conflated.
        JsonElement fit1 = lines[1].GetProperty("fit");
        fit1.GetProperty("windowSec").GetDouble().Should().Be(4.0);
        fit1.GetProperty("windowSource").GetString().Should().Be("declared_starts");
        fit1.GetProperty("overflowSec").GetDouble().Should().Be(0.0);
        fit1.GetProperty("overflowWords").GetInt32().Should().Be(0);
        fit1.GetProperty("fits").GetBoolean().Should().BeTrue();

        // Line 2: the last declared line has no successor, so it has NO window. It is reported as
        // unmeasured rather than given an invented one, and it counts as fitting — never as an error.
        JsonElement fit2 = lines[2].GetProperty("fit");
        fit2.GetProperty("windowSec").ValueKind.Should().Be(JsonValueKind.Null);
        fit2.GetProperty("windowSource").GetString().Should().Be("none");
        fit2.GetProperty("fits").GetBoolean().Should().BeTrue();
        fit2.GetProperty("wordsPerSecond").GetDouble().Should().Be(2.0,
            "the rate is measured from the audio and needs no window");

        // meta.fit is ALWAYS present once lines are produced, and its counters partition the lines:
        // two measured (one of them overflowing) plus one unmeasured is all three.
        JsonElement meta = ParseOutput(result).GetProperty("meta");
        JsonElement metaFit = meta.GetProperty("fit");
        metaFit.GetProperty("measuredLineCount").GetInt32().Should().Be(2);
        metaFit.GetProperty("unknownWindowLineCount").GetInt32().Should().Be(1);
        metaFit.GetProperty("overflowLineCount").GetInt32().Should().Be(1);
        metaFit.GetProperty("overflowWords").GetInt32().Should().Be(5);
        metaFit.GetProperty("overflowTotalSec").GetDouble().Should().Be(1.5);
        metaFit.GetProperty("maxOverflowSec").GetDouble().Should().Be(1.5);

        // …and it is ALSO promoted to meta itself, as bare integers, so a Conditional step can gate
        // without walking view.lines[].
        meta.GetProperty("overflowWords").GetInt32().Should().Be(5);
        meta.GetProperty("overflowLineCount").GetInt32().Should().Be(1);

        // Every pre-existing counter and key is untouched by any of this.
        meta.GetProperty("totalLines").GetInt32().Should().Be(3);
        meta.GetProperty("failedLines").GetInt32().Should().Be(0);
        meta.GetProperty("skippedLines").GetInt32().Should().Be(0);
        meta.GetProperty("allLinesFailed").GetBoolean().Should().BeFalse();
        meta.GetProperty("alignment").GetProperty("alignedLines").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task A_skipped_line_carries_fit_and_a_failed_line_carries_none()
    {
        // The two attachment paths in one run, because what matters is the distinction between them:
        // a skipped line has a DECLARED window and a known (zero) duration, so it has a real fit to
        // report; a failed line synthesized nothing, so there is no measurement to publish and the
        // key is OMITTED rather than written as null.
        VoiceoverFakes fakes = CreateFakes();

        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Fish Audio is unavailable"));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContext(SkippedThenFailingConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");
        lines[0].GetProperty("status").GetString().Should().Be("skipped");
        lines[1].GetProperty("status").GetString().Should().Be("failed");

        JsonElement skippedFit = lines[0].GetProperty("fit");
        skippedFit.GetProperty("id").GetString().Should().Be("vo0");
        skippedFit.GetProperty("windowSec").GetDouble().Should().Be(2.5,
            "the declared window is a fact of the script, whether or not the line was synthesized");
        skippedFit.GetProperty("playDurationSec").GetDouble().Should().Be(0.0);
        skippedFit.GetProperty("wordCount").GetInt32().Should().Be(0);
        skippedFit.GetProperty("alignedWordCount").ValueKind.Should().Be(JsonValueKind.Null,
            "a skipped line is never aligned, so there is no aligned count to carry");
        skippedFit.GetProperty("overflowSec").GetDouble().Should().Be(0.0);
        skippedFit.GetProperty("fits").GetBoolean().Should().BeTrue();

        lines[1].TryGetProperty("fit", out _).Should().BeFalse(
            "a failed line produced no audio, so nothing about it was measured");

        // The failed line contributes NOTHING to the totals: it is neither a measured line nor an
        // unmeasured one, and counting it either way would publish a number that describes nothing.
        JsonElement metaFit = ParseOutput(result).GetProperty("meta").GetProperty("fit");
        metaFit.GetProperty("measuredLineCount").GetInt32().Should().Be(1);
        metaFit.GetProperty("unknownWindowLineCount").GetInt32().Should().Be(0);
        metaFit.GetProperty("overflowLineCount").GetInt32().Should().Be(0);
        metaFit.GetProperty("overflowWords").GetInt32().Should().Be(0);
        metaFit.GetProperty("overflowTotalSec").GetDouble().Should().Be(0.0);
        metaFit.GetProperty("maxOverflowSec").GetDouble().Should().Be(0.0);
    }

    [Fact]
    public async Task Meta_fit_survives_a_step_cache_hit()
    {
        // The step cache persists the executor's output string verbatim and re-serves it UNCHANGED on
        // a hit (WorkflowExecutorService copies cacheEntry.Output straight into the result), so "fit
        // survives a hit" is a property of the string this executor hands back. Asserted against the
        // REAL cache over an in-memory DbContext rather than by re-parsing the string by hand, so the
        // test covers the round trip a hit actually performs — and would catch a fit that lived
        // anywhere other than the serialized document.
        VoiceoverFakes fakes = CreateFakes();

        // A fresh stream per call — see the note in the test above.
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest _, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

        SetupPerLineDurations(fakes.MediaProbe, 3.5, 1.0, 1.0);

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContext(FitConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        using WorkflowEngineDbContext db = new(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        StepResultCache cache = NewStepResultCache(db);

        Guid projectId = Guid.NewGuid();
        var inputs = new StepCacheKeyInputs
        {
            ProjectId = projectId,
            StepType = StepType.Voiceover,
            AgentType = AgentType.VideoTransform,
            AgentDefinitionId = Guid.NewGuid(),
            AgentInputContextMode = AgentInputContextMode.FullWorkflow,
            MaxIterations = 1,
            ResolvedInput = "resolved-input",
            ProjectFileFingerprint = "fingerprint-1",
            WorkflowDefinitionId = Guid.NewGuid(),
            VoiceoverConfigJson = "config-json"
        };

        await cache.StoreAsync(inputs, result, CancellationToken.None);
        StepCacheLookupResult lookup = await cache.TryGetAsync(inputs, CancellationToken.None);

        lookup.Hit.Should().BeTrue("this exact input was stored a moment ago");
        lookup.Entry!.Output.Should().Be(result.Output, "a hit re-serves the stored string verbatim");

        JsonElement cached = JsonDocument.Parse(lookup.Entry.Output).RootElement;
        cached.GetProperty("meta").GetProperty("fit").GetProperty("overflowWords").GetInt32().Should().Be(5);
        cached.GetProperty("meta").GetProperty("overflowWords").GetInt32().Should().Be(5);
        cached.GetProperty("meta").GetProperty("overflowLineCount").GetInt32().Should().Be(1);
        cached.GetProperty("view").GetProperty("lines")[0]
            .GetProperty("fit").GetProperty("overflowWords").GetInt32().Should().Be(5);
        cached.GetProperty("view").GetProperty("lines")[2]
            .GetProperty("fit").GetProperty("windowSource").GetString().Should().Be("none");
    }

    // =====================================================================
    // T-204 — the third source kind: VoiceoverSourceKind.NarrationPlan
    // =====================================================================

    [Fact]
    public void NarrationPlan_is_appended_last_so_every_stored_config_keeps_its_meaning()
    {
        // Asserted HERE, in the reader's own test file, because the executor is what deserializes
        // <c>voiceover_config_json</c>: a member inserted BEFORE ScriptScenes would not fail to
        // compile or to deserialize — it would silently reinterpret every stored Inline and
        // ScriptScenes config as the kind that took its ordinal, and the step would narrate the
        // wrong thing from a config nobody edited.
        ((int)VoiceoverSourceKind.Inline).Should().Be(0);
        ((int)VoiceoverSourceKind.ScriptScenes).Should().Be(1);
        ((int)VoiceoverSourceKind.NarrationPlan).Should().Be(2);
        // Phase 4 appended PickupPlan after it — still a position pin: NarrationPlan keeps 2 and the
        // newest member is the last one.
        ((int)VoiceoverSourceKind.PickupPlan).Should().Be(3);
        VoiceoverSourceKind.PickupPlan.Should().Be(Enum.GetValues<VoiceoverSourceKind>().Last());
    }

    /// <summary>
    /// A context whose <see cref="StepExecutionContext.StepOutputHistory"/> carries the named steps.
    /// That history is the ONLY place a NarrationPlan-sourced step can resolve anything: the plan
    /// lives in the NarrationWriter step's OUTPUT, not in this step's own config, so a case with no
    /// history is exactly the mis-wired workflow the NOT_FOUND code exists to name.
    /// </summary>
    private static StepExecutionContext CreateContextWithHistory(
        string voiceoverConfigJson,
        params StepOutputHistoryEntry[] history)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            // The template puts the Voiceover step at order 4 and the NarrationWriter at 3; the
            // executor never reads its OWN order, but keeping the real one keeps the fixture honest.
            StepOrder = 4,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = voiceoverConfigJson
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = history,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = default
        };
    }

    /// <summary>
    /// The RAW config literal — not a <see cref="SerializeConfig"/> round trip — because this is the
    /// exact JSON the <c>video-derush-edit-voiceover</c> template ships, and a hand-authored literal
    /// is the shape that silently stops working when a property is renamed. A null order omits the
    /// member entirely, which is what a config saved before that member existed looks like.
    /// </summary>
    private static string NarrationPlanConfigJson(int? narrationWriterStepOrder) =>
        narrationWriterStepOrder is int order
            // Concatenated rather than an interpolated raw literal: the JSON closes with two braces,
            // which cannot be written unambiguously inside a $$"""…""" literal.
            ? """{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":""" + order + "}}"
            : """{"source":{"kind":"NarrationPlan"}}""";

    /// <summary>
    /// A NarrationWriter step's real output, serialized from <see cref="NarrationPlanOutput"/> itself
    /// rather than hand-written, so a property rename cannot leave this fixture passing against a
    /// shape the executor is unable to read.
    /// </summary>
    private static string NarrationPlanJson(params (string AnchorId, string Text)[] lines) =>
        JsonSerializer.Serialize(
            new NarrationPlanOutput
            {
                Lines = lines
                    .Select(l => new NarrationLine
                    {
                        AnchorId = l.AnchorId,
                        Text = l.Text,
                        Delivery = "warm and unhurried, landing the product name last",
                        Reason = "the picture already shows it"
                    })
                    .ToList(),
                PlanRationale = "sparse narration over the kept moments"
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static StepOutputHistoryEntry NarrationWriterEntry(int stepOrder, string output) =>
        new(stepOrder, "Write narration lines", output);

    /// <summary>
    /// A Scriptwriter step's real output, serialized from <see cref="ScriptwriterOutput"/> itself so
    /// a property rename cannot leave the fixture passing against a shape the executor cannot read.
    /// Each entry is one scene's <c>Voiceover</c> text.
    /// </summary>
    private static string ScriptwriterJson(params string[] sceneVoiceovers) =>
        JsonSerializer.Serialize(
            new ScriptwriterOutput
            {
                Title = "Test",
                DurationInSeconds = sceneVoiceovers.Length * 5,
                Narrative = "test",
                Scenes = sceneVoiceovers
                    .Select((voiceover, i) => new ScriptScene
                    {
                        SceneId = $"scene-{i}",
                        StartTime = i * 5,
                        Duration = 5,
                        Voiceover = voiceover,
                        VisualDescription = "test"
                    })
                    .ToList()
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static StepOutputHistoryEntry ScriptwriterEntry(int stepOrder, string output) =>
        new(stepOrder, "Write script", output);

    /// <summary>Every synthesis call returns a fresh, valid WAV stream.</summary>
    private static void SetupFreshValidWavPerCall(VoiceoverFakes fakes) =>
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest _, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

    /// <summary>A project-voice resolver double: answers from a fixed map, records what it was asked.</summary>
    private sealed class StubVoiceResolver : IProjectVoiceResolver
    {
        public Dictionary<Guid, ResolvedProjectVoice> Voices { get; } = new();
        public List<(Guid ProjectId, Guid VoiceId)> Asked { get; } = [];

        /// <summary>Vendor ids the rule refuses in this project (an unusable own row, or another project's clone).</summary>
        public HashSet<string> Refused { get; } = new(StringComparer.Ordinal);
        public List<string> AskedRemote { get; } = [];

        public Task<IReadOnlyList<string>> FindRefusedVendorVoiceIdsAsync(Guid projectId, IReadOnlyCollection<string> remoteVoiceIds, CancellationToken ct)
        {
            AskedRemote.AddRange(remoteVoiceIds);
            return Task.FromResult<IReadOnlyList<string>>(remoteVoiceIds.Where(Refused.Contains).ToList());
        }

        public Task<ResolvedProjectVoice> ResolveAsync(Guid projectId, Guid projectVoiceId, CancellationToken ct)
        {
            Asked.Add((projectId, projectVoiceId));
            return Task.FromResult(Voices.TryGetValue(projectVoiceId, out ResolvedProjectVoice? v) ? v : ResolvedProjectVoice.NotFound(projectVoiceId));
        }

        public Task<IReadOnlyList<ResolvedProjectVoice>> ListSelectableAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ResolvedProjectVoice>>(Voices.Values.Where(v => v.Usable).ToList());
    }

    private static VoiceoverStepExecutor CreateExecutorWithVoices(
        VoiceoverFakes fakes, IProjectVoiceResolver? resolver, IDecisionGate? gate = null) =>
        new(fakes.SpeechFactory.Object, fakes.ProviderResolver.Object, fakes.MediaProbe.Object, fakes.Workspace.Object,
            new StubAlignmentService(), NullLogger<VoiceoverStepExecutor>.Instance, resolver, gate);

    private static void SetupSuccessfulSynthesis(VoiceoverFakes fakes, List<SpeechRequest> requests)
    {
        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(1.0, 0, 0, 0, 0, null, "pcm_s16le", 44100));
        fakes.Workspace
            .Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        fakes.Workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.Is<string>(k => k.EndsWith(".wav")), "audio/wav", It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((Guid _, string _, string relativePath, string _, CancellationToken _, string _) =>
                $"projects/{ProjectId}/agentFiles/{relativePath}");
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SpeechRequest, CancellationToken>((r, _) => requests.Add(r))
            .Returns((SpeechRequest _, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));
    }

    private static string OneLineConfigJson(Guid? projectVoiceId = null, VoiceCastingMode casting = VoiceCastingMode.Off) =>
        SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[] { new VoiceoverLine("Hello there.", 0.0) }),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000,
            ProjectVoiceId: projectVoiceId,
            VoiceCasting: casting));

    [Fact]
    public async Task A_project_voice_without_live_consent_fails_the_step_before_any_synthesis_and_never_falls_back()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid revoked = Guid.NewGuid();
        Guid missing = Guid.NewGuid();
        resolver.Voices[revoked] = new ResolvedProjectVoice(revoked, "Dana", null, ProjectVoiceSource.Cloned, Guid.NewGuid(), null, false, "consent_revoked");

        StepExecutionResult revokedResult = await CreateExecutorWithVoices(fakes, resolver)
            .ExecuteAsync(CreateContext(OneLineConfigJson(revoked)));
        StepExecutionResult missingResult = await CreateExecutorWithVoices(fakes, resolver)
            .ExecuteAsync(CreateContext(OneLineConfigJson(missing)));

        revokedResult.Status.Should().Be(StepStatus.Failed);
        revokedResult.ErrorDetails.Should().Contain("consent_revoked");
        revokedResult.ErrorDetails.Should().StartWith("VOICE_CONSENT_MISSING");
        missingResult.Status.Should().Be(StepStatus.Failed);
        missingResult.ErrorDetails.Should().StartWith("VOICE_NOT_FOUND");
        requests.Should().BeEmpty("an unconsented voice must never be spoken, and the default voice must not be substituted");
        resolver.Asked.Should().OnlyContain(a => a.ProjectId == ProjectId, "a voice is resolved within the step's own project only");
    }

    [Fact]
    public async Task A_consented_project_voice_is_the_voice_every_line_speaks_in_and_is_recorded_on_meta()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid voiceId = Guid.NewGuid();
        resolver.Voices[voiceId] = new ResolvedProjectVoice(voiceId, "Dana", "warm", ProjectVoiceSource.Cloned, Guid.NewGuid(), "fish-model-123", true, null);

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver)
            .ExecuteAsync(CreateContext(OneLineConfigJson(voiceId)));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().ContainSingle().Which.VoiceId.Should().Be("fish-model-123", "the project voice takes precedence over DefaultVoiceId");
        JsonElement voice = ParseOutput(result).GetProperty("meta").GetProperty("voice");
        voice.GetProperty("projectVoiceId").GetGuid().Should().Be(voiceId);
        voice.GetProperty("source").GetString().Should().Be("project_voice");
    }

    [Fact]
    public async Task Naming_a_project_voice_on_an_engine_without_a_resolver_fails_rather_than_speaking_unguarded()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver: null)
            .ExecuteAsync(CreateContext(OneLineConfigJson(Guid.NewGuid())));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("VOICE_RESOLVER_UNAVAILABLE");
        requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Voice_casting_uses_the_gate_accepted_choice_only_when_it_names_an_offered_voice()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var resolver = new StubVoiceResolver();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        resolver.Voices[a] = new ResolvedProjectVoice(a, "Warm", "warm and low", ProjectVoiceSource.Library, Guid.NewGuid(), "voice-a", true, null);
        resolver.Voices[b] = new ResolvedProjectVoice(b, "Crisp", "bright and quick", ProjectVoiceSource.Library, Guid.NewGuid(), "voice-b", true, null);

        var gate = new Mock<IDecisionGate>();
        DecisionGateRequest? captured = null;
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionGateRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DecisionGateOutcome(true, true,
                new DecisionResult("jev", new Dictionary<string, DecisionAnswer>
                {
                    ["voice"] = new("voice", DecisionKind.Choice, b.ToString("D"), null, null, 0.93, new Dictionary<string, double>())
                }, 10, 2),
                new Dictionary<string, (double Top, double Second)> { ["voice"] = (0.93, 0.07) }));

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, resolver, gate.Object)
            .ExecuteAsync(CreateContext(OneLineConfigJson(casting: VoiceCastingMode.Decision)));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().ContainSingle().Which.VoiceId.Should().Be("voice-b");
        captured.Should().NotBeNull();
        captured!.Site.Should().Be(VoiceoverStepExecutor.VoiceCastingSite);
        captured.Questions.Should().ContainSingle().Which.Options.Should().BeEquivalentTo(new[] { a.ToString("D"), b.ToString("D") });
        captured.State.Content.Should().NotContain("voice-a").And.NotContain("voice-b", "the model is offered ids, never vendor voice ids");
        JsonElement casting = ParseOutput(result).GetProperty("meta").GetProperty("voice").GetProperty("casting");
        casting.GetProperty("applied").GetBoolean().Should().BeTrue();
        casting.GetProperty("chosenVoiceId").GetGuid().Should().Be(b);

        // A declined gate keeps the configured voice and says so.
        gate.Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecisionGateOutcome.NoDecision);
        requests.Clear();
        StepExecutionResult declined = await CreateExecutorWithVoices(fakes, resolver, gate.Object)
            .ExecuteAsync(CreateContext(OneLineConfigJson(casting: VoiceCastingMode.Decision)));
        requests.Should().ContainSingle().Which.VoiceId.Should().Be("default-voice");
        ParseOutput(declined).GetProperty("meta").GetProperty("voice").GetProperty("casting").GetProperty("reason").GetString().Should().Be("no_decision");
    }

    [Fact]
    public async Task Voice_casting_Off_asks_the_gate_nothing()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);
        var gate = new Mock<IDecisionGate>(MockBehavior.Strict);

        StepExecutionResult result = await CreateExecutorWithVoices(fakes, new StubVoiceResolver(), gate.Object)
            .ExecuteAsync(CreateContext(OneLineConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().ContainSingle().Which.VoiceId.Should().Be("default-voice");
        gate.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PickupPlan_lines_carry_their_segment_id_as_the_anchor_and_an_empty_plan_completes()
    {
        VoiceoverFakes fakes = CreateFakes();
        List<SpeechRequest> requests = [];
        SetupSuccessfulSynthesis(fakes, requests);

        string config = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.PickupPlan, PickupPlannerStepOrder: 3),
            ProviderId: Guid.NewGuid(), Model: "test-model", DefaultVoiceId: "default-voice", MaxBytes: 2_000_000));
        string plan = JsonSerializer.Serialize(new PickupPlanOutput
        {
            Pickups =
            [
                new PickupLine { SegmentId = "t3", Text = "Deploys go from forty minutes to four.", Reason = "said fourteen" },
                new PickupLine { SegmentId = "t7", Text = "   ", Reason = "blank, dropped" }
            ],
            PlanRationale = "one flub"
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        StepExecutionResult result = await CreateExecutor(fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(config, new StepOutputHistoryEntry(3, "Plan pickups", plan)));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().ContainSingle().Which.Text.Should().Be("Deploys go from forty minutes to four.");
        JsonElement line = ParseOutput(result).GetProperty("view").GetProperty("lines")[0];
        line.GetProperty("anchorId").GetString().Should().Be("t3", "the compile step places the pickup over this segment's window");
        line.GetProperty("text").GetString().Should().Be("Deploys go from forty minutes to four.");

        // A clean take: zero pickups is a successful, empty step with the same provenance marker.
        string emptyPlan = JsonSerializer.Serialize(new PickupPlanOutput { Pickups = [], PlanRationale = "clean" }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        StepExecutionResult empty = await CreateExecutor(fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(config, new StepOutputHistoryEntry(3, "Plan pickups", emptyPlan)));
        empty.Status.Should().Be(StepStatus.Completed);
        ParseOutput(empty).GetProperty("meta").GetProperty("emptyReason").GetString().Should().Be("narration_plan_empty");

        // And a config without the step order is the source-invalid failure, before any history read.
        string noOrder = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.PickupPlan), ProviderId: Guid.NewGuid(), Model: "m", DefaultVoiceId: "v", MaxBytes: 2_000_000));
        StepExecutionResult invalid = await CreateExecutor(fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContext(noOrder));
        invalid.Status.Should().Be(StepStatus.Failed);
        invalid.ErrorDetails.Should().StartWith("PICKUP_SOURCE_INVALID");
    }

    [Fact]
    public async Task Auto_pronunciation_sends_analysis_terms_the_line_contains_and_changes_only_that_line_key()
    {
        VoiceoverFakes fakes = CreateFakes();
        SetupFreshValidWavPerCall(fakes);
        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(1.0, 0, 0, 0, 0, null, "pcm_s16le", 44100));
        fakes.Workspace
            .Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        fakes.Workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.Is<string>(k => k.EndsWith(".wav")), "audio/wav", It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((Guid _, string _, string relativePath, string _, CancellationToken _, string _) =>
                $"projects/{ProjectId}/agentFiles/{relativePath}");

        List<SpeechRequest> requests = [];
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SpeechRequest, CancellationToken>((r, _) => requests.Add(r))
            .Returns((SpeechRequest _, CancellationToken _) =>
                Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

        // Two Inline lines: one names a term the dependency analysis found, one does not.
        string config = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[]
            {
                new VoiceoverLine("Built on Remotion.", 0.0),
                new VoiceoverLine("Four minutes to deploy.", 5.0)
            }),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000,
            PronunciationDictionary: PronunciationDictionaryMode.Auto));

        string dependencyAnalysis = JsonSerializer.Serialize(new
        {
            dependencies = new object[] { new { name = "remotion", version = "4", purpose = "video", isCore = true } },
            devDependencies = new string[0],
            packageManager = "npm",
            recommendations = new object[0]
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(config, new StepOutputHistoryEntry(1, "Analyze dependencies", dependencyAnalysis)));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requests.Should().HaveCount(2);
        requests[0].Pronunciation.Should().ContainSingle().Which.Key.Should().Be("Remotion");
        requests[1].Pronunciation.Should().BeNull("a line with no analysis term sends the byte-identical phase-1 request");

        // Provenance on meta: which keys went with which line.
        JsonElement pronunciation = ParseOutput(result).GetProperty("meta").GetProperty("pronunciation");
        pronunciation.GetProperty("mode").GetString().Should().Be("Auto");
        pronunciation.GetProperty("entryCount").GetInt32().Should().Be(1);
        pronunciation.GetProperty("lines")[0].GetProperty("lineId").GetString().Should().Be("vo0");

        // The dictionary is part of the content key for the line that carries one, and ONLY that line.
        string keyWithout = VoiceoverStepExecutor.ComputeLineContentKey("Built on Remotion.", "default-voice", "test-model", null, "e");
        string keyWith = VoiceoverStepExecutor.ComputeLineContentKey("Built on Remotion.", "default-voice", "test-model", null, "e",
            [new PronunciationEntry("Remotion", "x")]);
        keyWith.Should().NotBe(keyWithout);
        VoiceoverStepExecutor.ComputeLineContentKey("a", "v", "m", null, "e", []).Should().Be(
            VoiceoverStepExecutor.ComputeLineContentKey("a", "v", "m", null, "e"),
            "an empty dictionary leaves the v2 key — and the object a phase-1 run uploaded — untouched");
    }

    [Fact]
    public async Task NarrationPlan_wrapped_in_a_markdown_fence_is_still_read()
    {
        // Observed live on a styled run: the narration writer answered with its plan inside a
        // ```json fence. AgentStepExecutor accepts that (it validates with the same extractor
        // every consumer uses), so this step must read it the same way rather than failing the run
        // with NARRATION_OUTPUT_INVALID: '`' is an invalid start of a value.
        VoiceoverFakes fakes = CreateFakes();
        SetupFreshValidWavPerCall(fakes);
        SetupPerLineDurations(fakes.MediaProbe, 2.0, 1.5);

        string fenced = "```json\n" + NarrationPlanJson(("s0", "First spoken line"), ("s1", "Second spoken line")) + "\n```";
        StepExecutionContext context = CreateContextWithHistory(
            NarrationPlanConfigJson(3),
            NarrationWriterEntry(3, fenced));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        ParseOutput(result).GetProperty("view").GetProperty("lines").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task NarrationPlan_lines_are_synthesized_in_plan_order_with_back_to_back_declared_starts()
    {
        VoiceoverFakes fakes = CreateFakes();
        SetupFreshValidWavPerCall(fakes);
        SetupPerLineDurations(fakes.MediaProbe, 2.0, 1.5, 0.5);

        StepExecutionContext context = CreateContextWithHistory(
            NarrationPlanConfigJson(3),
            new StepOutputHistoryEntry(1, "Analyze source video", "{}"),
            NarrationWriterEntry(3, NarrationPlanJson(
                ("s2", "First spoken line"),
                ("g1", "Second spoken line"),
                ("t4", "Third spoken line"))));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");
        lines.GetArrayLength().Should().Be(3);

        // The plan carries no start at all, and this step has no picture timeline, so these are
        // DECLARED: line 0 at 0.0 and each next line at the previous line's declared start plus its
        // MEASURED play duration. Back-to-back, deterministic, never overlapping.
        lines[0].GetProperty("startSec").GetDouble().Should().Be(0.0);
        lines[1].GetProperty("startSec").GetDouble().Should().Be(2.0);
        lines[2].GetProperty("startSec").GetDouble().Should().Be(3.5);

        // Plan order is preserved 1:1 — the anchor-to-slot mapping is positional downstream, so a
        // reordered emitted list would silently re-anchor every line.
        lines[0].GetProperty("id").GetString().Should().Be("vo0");
        lines[1].GetProperty("id").GetString().Should().Be("vo1");
        lines[2].GetProperty("id").GetString().Should().Be("vo2");
        lines.EnumerateArray().Select(l => l.GetProperty("status").GetString()).Should().AllBe("ok");

        // The declared window a NarrationPlan line is measured against is [start_i, start_{i+1}),
        // which by construction IS its own duration — a declaration cannot overrun itself, so the
        // declared-window overflow is structurally zero. Whether the narration overruns the SHOT it
        // lands in is the compile step's measurement (media's narrationFitFacts), not this one's.
        lines[0].GetProperty("fit").GetProperty("windowSec").GetDouble().Should().Be(2.0);
        lines[1].GetProperty("fit").GetProperty("windowSec").GetDouble().Should().Be(1.5);
        lines[2].GetProperty("fit").GetProperty("windowSource").GetString().Should().Be("none");

        JsonElement fit = ParseOutput(result).GetProperty("meta").GetProperty("fit");
        fit.GetProperty("overflowLineCount").GetInt32().Should().Be(0);
        fit.GetProperty("overflowWords").GetInt32().Should().Be(0);
        fit.GetProperty("measuredLineCount").GetInt32().Should().Be(2);
        fit.GetProperty("unknownWindowLineCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_sequential_Inline_block_is_spoken_back_to_back_from_zero_like_a_plan()
    {
        // App inputs F1 (docs/design/app-inputs.md 27): an app's "speak exactly this" field writes every paragraph as an
        // Inline line at 0 s with source.sequential = true; the step lays them out one after the other.
        VoiceoverFakes fakes = CreateFakes();
        SetupFreshValidWavPerCall(fakes);
        SetupPerLineDurations(fakes.MediaProbe, 2.0, 1.5, 0.5);

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(
                """{"source":{"kind":"Inline","sequential":true,"lines":[{"text":"First line.","startSec":0},{"text":"Second line.","startSec":0},{"text":"Third line.","startSec":0}]}}"""));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");
        lines.EnumerateArray().Select(l => l.GetProperty("startSec").GetDouble()).Should().Equal(0.0, 2.0, 3.5);
        lines.EnumerateArray().Select(l => l.GetProperty("status").GetString()).Should().AllBe("ok");
    }

    [Fact]
    public async Task NarrationPlan_anchor_ids_are_threaded_onto_the_fit_facts()
    {
        VoiceoverFakes fakes = CreateFakes();
        SetupFreshValidWavPerCall(fakes);
        SetupPerLineDurations(fakes.MediaProbe, 1.0, 1.0, 1.0);

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(
                NarrationPlanConfigJson(3),
                NarrationWriterEntry(3, NarrationPlanJson(
                    ("s2", "First spoken line"),
                    ("g1", "Second spoken line"),
                    ("t4", "Third spoken line")))));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");

        // The anchor is the ONLY thing the plan contributes besides text, so losing it on the way to
        // the fit facts would make every overflow unattributable — you could no longer say WHICH
        // moment the writer wrote the line for.
        lines[0].GetProperty("fit").GetProperty("anchorId").GetString().Should().Be("s2");
        lines[1].GetProperty("fit").GetProperty("anchorId").GetString().Should().Be("g1");
        lines[2].GetProperty("fit").GetProperty("anchorId").GetString().Should().Be("t4");
    }

    [Fact]
    public async Task An_Inline_line_carries_no_anchor_on_its_fit_facts()
    {
        // Control for the case above: only a NarrationPlan can declare an anchor, so an Inline line
        // must serialize `anchorId: null` rather than a fabricated id.
        VoiceoverFakes fakes = CreateFakes();
        SetupFreshValidWavPerCall(fakes);
        SetupPerLineDurations(fakes.MediaProbe, 1.0, 1.0);

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContext(TwoLineConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");
        lines[0].GetProperty("fit").GetProperty("anchorId").ValueKind.Should().Be(JsonValueKind.Null);
        // The Inline arm is untouched: its declared window is still what the config declared, NOT a
        // back-to-back placement — 2.5 s here, from starts 0.0 and 2.5.
        lines[0].GetProperty("startSec").GetDouble().Should().Be(0.0);
        lines[1].GetProperty("startSec").GetDouble().Should().Be(2.5);
    }

    [Fact]
    public async Task NarrationPlan_with_a_failed_line_gives_that_line_a_zero_width_slot()
    {
        VoiceoverFakes fakes = CreateFakes();

        var call = 0;
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SpeechRequest _, CancellationToken _) =>
                ++call == 2
                    ? throw new InvalidOperationException("vendor said no")
                    : Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));

        // Only the two lines that actually reach the probe consume a duration.
        SetupPerLineDurations(fakes.MediaProbe, 2.0, 1.0);

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(
                NarrationPlanConfigJson(3),
                NarrationWriterEntry(3, NarrationPlanJson(
                    ("s2", "First spoken line"),
                    ("g1", "Second spoken line"),
                    ("t4", "Third spoken line")))));

        // Degrade the line, never the step — the same contract the Inline arm already has.
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement lines = ParseOutput(result).GetProperty("view").GetProperty("lines");
        lines[0].GetProperty("status").GetString().Should().Be("ok");
        lines[1].GetProperty("status").GetString().Should().Be("failed");
        lines[2].GetProperty("status").GetString().Should().Be("ok");

        // A failed line measured nothing, so it advances the declaration by ZERO: the third line
        // starts exactly where the second did rather than leaving a hole nobody measured.
        lines[0].GetProperty("startSec").GetDouble().Should().Be(0.0);
        lines[1].GetProperty("startSec").GetDouble().Should().Be(2.0);
        lines[2].GetProperty("startSec").GetDouble().Should().Be(2.0);

        // A failed line has no fit to report at all — the key is ABSENT, not null.
        lines[1].TryGetProperty("fit", out _).Should().BeFalse();

        // Line 0's window still ends where line 1 was declared to begin, so the failure does not
        // silently swallow the surviving line's measurement.
        lines[0].GetProperty("fit").GetProperty("windowSec").GetDouble().Should().Be(2.0);

        JsonElement fit = ParseOutput(result).GetProperty("meta").GetProperty("fit");
        fit.GetProperty("measuredLineCount").GetInt32().Should().Be(1);
        fit.GetProperty("unknownWindowLineCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task NarrationPlan_with_an_empty_lines_list_completes_with_an_empty_envelope()
    {
        VoiceoverFakes fakes = CreateFakes();
        SetupFreshValidWavPerCall(fakes);

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(
                NarrationPlanConfigJson(3),
                NarrationWriterEntry(3, NarrationPlanJson())));

        // A well-formed but EMPTY plan is a SUCCESSFUL, empty Voiceover step.
        // `NarrationWriterAgent.DefaultPrompt` tells the model to output exactly this when the edit
        // needs no narration and `NarrationPlanOutput`'s own remark says the same, so failing the
        // step made a model that obeyed its prompt redden a correct run.
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        result.ErrorDetails.Should().BeNull();

        JsonElement root = ParseOutput(result);

        // A valid `{view, meta}` envelope, with `view.lines` empty.
        root.TryGetProperty("view", out JsonElement view).Should().BeTrue();
        view.GetProperty("lines").GetArrayLength().Should().Be(0);

        JsonElement meta = root.GetProperty("meta");

        // Counters consistent with "nothing was asked for".
        meta.GetProperty("totalLines").GetInt32().Should().Be(0);
        meta.GetProperty("failedLines").GetInt32().Should().Be(0);
        meta.GetProperty("skippedLines").GetInt32().Should().Be(0);
        meta.GetProperty("allLinesFailed").GetBoolean().Should().BeFalse();

        // ... and the marker that says WHY it is empty, so a consumer tells "nobody asked for
        // narration" from "narration was asked for and every line was lost" by a key check rather
        // than by inferring one from an error string.
        meta.GetProperty("emptyReason").GetString().Should().Be("narration_plan_empty");
        // The never-declared half stays pinned: zero declared entries is exactly what this value
        // means, and the count is carried beside it so a consumer never infers it.
        meta.GetProperty("declaredLines").GetInt32().Should().Be(0);

        // The zero-value nodes the non-empty path always emits are present here too, so a consumer
        // reading meta.fit.overflowLineCount or meta.overflowLineCount needs no empty-plan special
        // case: "nothing overflowed" is readable as a zero, not as an absent key.
        meta.GetProperty("overflowLineCount").GetInt32().Should().Be(0);
        meta.GetProperty("overflowWords").GetInt32().Should().Be(0);
        meta.GetProperty("alignment").GetProperty("applied").GetBoolean().Should().BeFalse();
        meta.GetProperty("alignment").GetProperty("alignedLines").GetInt32().Should().Be(0);
        meta.GetProperty("fit").GetProperty("measuredLineCount").GetInt32().Should().Be(0);
        meta.GetProperty("fit").GetProperty("overflowLineCount").GetInt32().Should().Be(0);

        // Nothing was synthesized and nothing was billed.
        fakes.Client.Verify(
            c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Inline_with_no_lines_still_fails_with_NO_LINES()
    {
        VoiceoverFakes fakes = CreateFakes();

        // The DECISIVE control for the ruling above: an empty resolved line list is a DECISION for a
        // NarrationPlan and an author misconfiguration for Inline, and phase 1's semantics for this
        // kind are deliberately not weakened. An empty-plan fix implemented by loosening NO_LINES
        // globally would be caught right here.
        string inlineWithNoLines = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, Array.Empty<VoiceoverLine>()),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContext(inlineWithNoLines));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("NO_LINES");

        // And never the marker: NO_LINES is a failure, the empty plan is not.
        ParseOutput(result).GetProperty("meta").TryGetProperty("emptyReason", out _).Should().BeFalse();

        fakes.Client.Verify(
            c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ScriptScenes_with_no_resolvable_lines_still_fails_with_NO_LINES()
    {
        VoiceoverFakes fakes = CreateFakes();

        // The ScriptScenes half of the same ruling: a referenced ScriptwriterOutput whose scenes
        // carry no voiceover text is an author misconfiguration, not a decision, so the arm stays a
        // failure. The scenes are "" and "   " so the executor's own IsNullOrWhiteSpace filter is
        // what produces the empty set — the fixture never hands it an empty list directly.
        string scriptScenesConfig = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.ScriptScenes, ScriptwriterStepOrder: 1),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(
                scriptScenesConfig,
                ScriptwriterEntry(1, ScriptwriterJson("", "   "))));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("NO_LINES");

        // NO_LINES is a failure, so the success-only marker must be absent — the same assertion the
        // Inline twin makes.
        ParseOutput(result).GetProperty("meta").TryGetProperty("emptyReason", out _).Should().BeFalse();

        fakes.Client.Verify(
            c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NarrationPlan_declaring_only_blank_lines_completes_with_the_lines_discarded_reason()
    {
        VoiceoverFakes fakes = CreateFakes();
        SetupFreshValidWavPerCall(fakes);

        // Three DECLARED entries, every one blank: still nothing narratable, so the step completes,
        // but the provenance is a producer problem and the marker must say so rather than claiming
        // nobody asked for narration.
        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(
                NarrationPlanConfigJson(3),
                NarrationWriterEntry(3, NarrationPlanJson(("s0", ""), ("s1", "   "), ("s2", "\t")))));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        JsonElement meta = ParseOutput(result).GetProperty("meta");
        meta.GetProperty("emptyReason").GetString().Should().Be("narration_plan_lines_discarded");
        meta.GetProperty("declaredLines").GetInt32().Should().Be(3);
        meta.GetProperty("totalLines").GetInt32().Should().Be(0);
        meta.GetProperty("allLinesFailed").GetBoolean().Should().BeFalse();

        fakes.Client.Verify(
            c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NarrationPlan_without_a_step_order_fails_with_NARRATION_SOURCE_INVALID()
    {
        VoiceoverFakes fakes = CreateFakes();

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(NarrationPlanConfigJson(null)));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("NARRATION_SOURCE_INVALID");
        result.ErrorDetails.Should().Contain("NarrationWriterStepOrder");
    }

    [Fact]
    public async Task NarrationPlan_naming_a_step_absent_from_history_fails_with_NARRATION_STEP_NOT_FOUND()
    {
        VoiceoverFakes fakes = CreateFakes();

        // History carries step 1 only: the named step 3 never ran, or this step was wired to an
        // order that does not exist in the workflow.
        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(
                NarrationPlanConfigJson(3),
                new StepOutputHistoryEntry(1, "Analyze source video", "{}")));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("NARRATION_STEP_NOT_FOUND");
        // Never the generic code: "the step you named is not in this run" is a wiring mistake, and
        // reporting it as "unknown source kind" would send triage looking at the enum.
        result.ErrorDetails.Should().NotContain("SOURCE_INVALID");
    }

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("null")]
    // The two shapes whose JSON parses perfectly and still is not a plan: a property initializer
    // only runs when the member is ABSENT, so an explicit null for `lines` — or for one of its
    // entries — leaves the list or the element null. Both used to NullReference at the plan walk and
    // escape as UNEXPECTED_ERROR, a code that blames this executor for a shape the MODEL authored.
    [InlineData("{\"lines\":null}")]
    [InlineData("{\"lines\":[null]}")]
    [InlineData("{\"lines\":[{\"anchorId\":\"a1\",\"text\":\"ok\"},null]}")]
    public async Task NarrationPlan_output_that_is_not_a_valid_plan_fails_with_NARRATION_OUTPUT_INVALID(string output)
    {
        VoiceoverFakes fakes = CreateFakes();

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
            .ExecuteAsync(CreateContextWithHistory(
                NarrationPlanConfigJson(3),
                NarrationWriterEntry(3, output)));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("NARRATION_OUTPUT_INVALID");
        result.ErrorDetails.Should().NotContain("SOURCE_INVALID");

        // Never the generic code: this arm gives every malformed shape its own, and a
        // NullReferenceException escaping to the outer catch would report a model-authored payload as
        // a defect in this executor.
        result.ErrorDetails.Should().NotContain("UNEXPECTED_ERROR");

        fakes.Client.Verify(
            c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // =====================================================================
    // T-006 — generated-text moderation before the speech client is invoked
    // =====================================================================

    private sealed class FakeGate : IContentModerationGate
    {
        public FakeGate(ModerationAction action, bool available = true)
        {
            Decision = new ModerationDecision(
                action, available, action == ModerationAction.Review, null, Message, "hash");
        }

        public const string Message = "This narration cannot be synthesized.";
        public ModerationDecision Decision { get; }
        public List<ModerationInput> Inputs { get; } = [];
        public List<ModerationAttribution> Attributions { get; } = [];

        public Task<ModerationDecision> ModerateAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            Inputs.Add(input);
            Attributions.Add(attribution);
            return Task.FromResult(Decision);
        }

        public Task<bool> ScreenGenerationPromptAsync(
            string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Action != ModerationAction.Block);

        public Task<bool> ScreenGeneratedTextAsync(
            string text, string site, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Action != ModerationAction.Block);

        public Task<bool> ScreenPublishAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Available && Decision.Action != ModerationAction.Block);
    }

    [Fact]
    public async Task A_blocked_narration_fails_before_the_speech_client_is_invoked()
    {
        VoiceoverFakes fakes = CreateFakes();
        var gate = new FakeGate(ModerationAction.Block);

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace, moderation: gate)
            .ExecuteAsync(CreateContext(TwoLineConfigJson()));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain("CONTENT_BLOCKED");
        result.ErrorDetails.Should().Contain(FakeGate.Message);
        result.FailureClass.Should().Be(FailureClass.UserInput);

        fakes.Client.Verify(
            c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a blocked narration must never reach the speech client");

        gate.Inputs.Should().ContainSingle();
        gate.Inputs[0].Text.Should().Contain("Hello world").And.Contain("Second line");
        gate.Attributions[0].Surface.Should().Be(ModerationSurface.GeneratedText);
        gate.Attributions[0].Kind.Should().Be(ModerationContentKind.Text);
    }

    [Theory]
    [InlineData(ModerationAction.Allow, true)]
    [InlineData(ModerationAction.Review, true)]
    [InlineData(ModerationAction.Unavailable, false)]
    public async Task Allow_review_and_unavailable_narration_still_synthesize(ModerationAction action, bool available)
    {
        VoiceoverFakes fakes = CreateFakes();
        var gate = new FakeGate(action, available);
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav"));
        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecodableProbe());

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace, moderation: gate)
            .ExecuteAsync(CreateContext(TwoLineConfigJson()));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fakes.Client.Verify(
            c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        gate.Inputs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_long_narration_is_moderated_in_every_chunk_before_synthesis()
    {
        VoiceoverFakes fakes = CreateFakes();
        var gate = new FakeGate(ModerationAction.Allow);
        fakes.Client
            .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav"));
        fakes.MediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecodableProbe());

        string line = new('a', 4000);
        string config = SerializeConfig(new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, Enumerable.Range(0, 4)
                .Select(i => new VoiceoverLine(line, i * 2.0, null)).ToArray()),
            ProviderId: Guid.NewGuid(),
            Model: "test-model",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000));

        StepExecutionResult result = await CreateExecutor(
                fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace, moderation: gate)
            .ExecuteAsync(CreateContext(config));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        gate.Inputs.Should().HaveCountGreaterThan(1, "the joined narration exceeds the 8000-char call cap");
        gate.Inputs.Should().OnlyContain(i => i.Text!.Length <= 8000);
        string.Concat(gate.Inputs.Select(i => i.Text))
            .Should().Be(string.Join("\n", Enumerable.Repeat(line, 4)),
                "every character synthesized was classified, in order, with nothing dropped");
    }
    // QA (step-change-requests, PR #555): overrides reach the request with the dictionary Off, and a
    // config with no overrides (null or empty) sends the byte-identical request and content key.
    [Fact]
    public async Task Pronunciation_overrides_apply_with_Off_and_no_override_keeps_request_and_key_identical()
    {
        // ONE fakes instance for all three runs: CreateFakes mints a fresh provider id, which is part of the key.
        VoiceoverFakes fakes = CreateFakes();
        List<string> keys = [];
        List<SpeechRequest> requests = [];
        {
            SetupFreshValidWavPerCall(fakes);
            fakes.MediaProbe
                .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(1.0, 0, 0, 0, 0, null, "pcm_s16le", 44100));
            fakes.Workspace
                .Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            fakes.Workspace
                .Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.Is<string>(k => k.EndsWith(".wav")), "audio/wav", It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .Callback<Guid, string, string, string, CancellationToken, string>((_, _, rel, _, _, _) => keys.Add(rel))
                .ReturnsAsync((Guid _, string _, string relativePath, string _, CancellationToken _, string _) =>
                    $"projects/{ProjectId}/agentFiles/{relativePath}");
            fakes.Client
                .Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
                .Callback<SpeechRequest, CancellationToken>((r, _) => requests.Add(r))
                .Returns((SpeechRequest _, CancellationToken _) =>
                    Task.FromResult(new SynthesizedSpeech(new MemoryStream(ValidWavBytes()), "audio/wav")));
        }

        async Task<(List<SpeechRequest> Requests, List<string> Keys, StepExecutionResult Result)> Run(
            IReadOnlyList<PronunciationOverride>? overrides)
        {
            requests.Clear();
            keys.Clear();
            string config = SerializeConfig(new VoiceoverStepConfig(
                Source: new VoiceoverSource(VoiceoverSourceKind.Inline, new[]
                {
                    new VoiceoverLine("Perché la città è bella.", 0.0),
                    new VoiceoverLine("Quattro minuti.", 5.0)
                }),
                ProviderId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Model: "test-model",
                DefaultVoiceId: "default-voice",
                MaxBytes: 2_000_000,
                PronunciationOverrides: overrides));
            StepExecutionResult result = await CreateExecutor(
                    fakes.SpeechFactory, fakes.ProviderResolver, fakes.MediaProbe, fakes.Workspace)
                .ExecuteAsync(CreateContext(config));
            return ([.. requests], [.. keys], result);
        }

        var baseline = await Run(null);
        var empty = await Run([]);
        var withOverride = await Run([new PronunciationOverride("perché", "perˈke")]);

        baseline.Result.Status.Should().Be(StepStatus.Completed, because: baseline.Result.ErrorDetails ?? baseline.Result.Output);
        withOverride.Result.Status.Should().Be(StepStatus.Completed, because: withOverride.Result.ErrorDetails ?? withOverride.Result.Output);

        baseline.Requests.Should().HaveCount(2).And.OnlyContain(r => r.Pronunciation == null);
        empty.Requests.Should().BeEquivalentTo(baseline.Requests, "an empty override list is the same as none");
        empty.Keys.Should().Equal(baseline.Keys);

        // Dictionary mode is Off (default): the explicit override still goes out, on the line that has the term only.
        withOverride.Requests[0].Pronunciation.Should().ContainSingle()
            .Which.Should().Be(new PronunciationEntry("Perché", "perˈke", false));
        withOverride.Requests[1].Pronunciation.Should().BeNull();
        withOverride.Keys[0].Should().NotBe(baseline.Keys[0], "the override changes the audio of that line");
        withOverride.Keys[1].Should().Be(baseline.Keys[1], "a line without the term keeps its key and cached object");

        JsonElement meta = ParseOutput(withOverride.Result).GetProperty("meta").GetProperty("pronunciation");
        meta.GetProperty("mode").GetString().Should().Be("Off");
        meta.GetProperty("overrideCount").GetInt32().Should().Be(1);
        meta.GetProperty("entryCount").GetInt32().Should().Be(1);
    }
}
