using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;

/// <summary>
/// The repeat-run defect this file exists for (security verdict condition 2), simulated the way it
/// actually happens in production.
///
/// <para>
/// <b>The bug.</b> <c>VoiceoverStepExecutor</c> is registered <c>AddSingleton</c> and held a
/// process-wide, never-evicted <c>ConcurrentDictionary</c> keyed on
/// <c>SHA-256(text|voiceId|model)</c>. A hit on that dictionary skipped BOTH the Fish Audio call
/// AND the artifact upload, so a second execution that ran the same lines produced output JSON
/// naming WAV objects belonging to the FIRST execution's <c>voiceover/{executionId}/</c> prefix —
/// or, for a line the second execution's own upstream script had not produced before, no WAV at
/// all. Compounding it, the artifact key itself was built from
/// <c>Execution.Id</c>, a value deliberately absent from the step-cache key, so a step-cache hit
/// was structurally guaranteed to name objects nothing had uploaded.
/// </para>
///
/// <para>
/// <b>The fix under test.</b> The artifact key is content-addressed
/// (<c>voiceover/lines/{sha256(text|voiceId|model)}.wav</c>) and therefore carries no execution
/// id, and the process-wide dictionary is gone — the object store itself is the cache, consulted
/// with a HEAD per line. The assertions below are exactly the ones the verdict's remedy has to
/// satisfy: a fresh executor instance (as a fresh process would be) must reuse the first
/// execution's object byte-for-byte without paying Fish Audio again.
/// </para>
/// </summary>
public class VoiceoverCrossExecutionArtifactTests
{
    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// A minimal, real, in-memory object store behind <see cref="IProjectFileWorkspace"/> — not a
    /// mock with a stubbed return value. It is what makes this test able to prove the property the
    /// ticket actually cares about: the SECOND execution must be able to see and download what the
    /// FIRST execution uploaded, which a mocked <c>ArtifactExistsAsync</c> returning a constant
    /// could never demonstrate.
    /// </summary>
    internal sealed class InMemoryObjectStoreWorkspace : IProjectFileWorkspace
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Keys => _objects.Keys;

        /// <summary>
        /// Just the per-line WAV artifacts — the step ALSO uploads its own
        /// <c>voiceover/{executionId}/artifact.json</c> summary, which is intentionally still
        /// execution-scoped, so an assertion about "how many objects did the runs create" has to
        /// filter to the content-addressed lines to say anything meaningful.
        /// </summary>
        public IReadOnlyList<string> LineArtifactKeys =>
            _objects.Keys.Where(k => k.Contains("/voiceover/lines/", StringComparison.Ordinal)).ToList();

        public IReadOnlyList<string> UploadedKeys { get; } = new List<string>();

        public IReadOnlyList<string> UploadedLineKeys =>
            UploadedKeys.Where(k => k.Contains("/voiceover/lines/", StringComparison.Ordinal)).ToList();

        public bool Contains(string storageKey) => _objects.ContainsKey(storageKey);

        public Task<IReadOnlyList<ProjectWorkspaceFile>> ListFilesAsync(Guid projectId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProjectWorkspaceFile>>([]);

        public Task<string> ReadFileAsync(Guid projectId, string fileReference, CancellationToken ct)
            => throw new NotSupportedException("Not used by the voiceover executor.");

        public Task<ProjectWorkspaceFile> RegisterStoredObjectAsync(
            Guid projectId, string storageKey, string fileName, string contentType, long sizeBytes,
            SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct,
            string category = "outputFiles")
            => throw new NotSupportedException("Not used by the voiceover executor.");

        public Task<ProjectWorkspaceFile> WriteTextFileAsync(
            Guid projectId, string fileName, string content, string contentType, CancellationToken ct,
            string category = "agentFiles", string? originalPath = null)
            => throw new NotSupportedException("Not used by the voiceover executor.");

        public async Task DownloadStorageKeyToFileAsync(
            Guid projectId, string storageKey, string destinationPath, CancellationToken ct)
        {
            if (!_objects.TryGetValue(storageKey, out byte[]? bytes))
            {
                throw new Amazon.S3.AmazonS3Exception("Not Found")
                {
                    StatusCode = HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey"
                };
            }

            string? directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllBytesAsync(destinationPath, bytes, ct);
        }

        /// <summary>The bounded twin (S-67): this store holds the bytes, so its own length answers the cap.</summary>
        public Task DownloadStorageKeyToFileAsync(
            Guid projectId, string storageKey, string destinationPath, long maxBytes, CancellationToken ct)
        {
            if (_objects.TryGetValue(storageKey, out byte[]? bytes) && bytes.Length > maxBytes)
                throw new StorageObjectTooLargeException(maxBytes, bytes.Length);
            return DownloadStorageKeyToFileAsync(projectId, storageKey, destinationPath, ct);
        }

        public Task<ProjectWorkspaceFile> UploadBinaryFileAsync(
            Guid projectId, string localFilePath, string fileName, string contentType,
            SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct,
            string category = "outputFiles", string? originalPath = null)
            => throw new NotSupportedException("Not used by the voiceover executor.");

        public async Task<string> UploadArtifactAsync(
            Guid projectId, string localFilePath, string fileName, string contentType, CancellationToken ct,
            string category = "agentFiles")
        {
            // Mirrors ProjectFileWorkspace.UploadArtifactAsync's real key construction, so the key
            // the executor records is the key the store actually holds.
            string storageKey = ProjectFilePath.BuildStorageKey(
                projectId,
                category,
                directoryPath: null,
                storageFileName: ProjectFilePath.NormalizeRelativePath(fileName));

            _objects[storageKey] = await File.ReadAllBytesAsync(localFilePath, ct);
            ((List<string>)UploadedKeys).Add(storageKey);
            return storageKey;
        }

        public Task DeleteArtifactAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            if (!storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Requested file is not in the project's storage scope.");
            _objects.Remove(storageKey);
            return Task.CompletedTask;
        }

        public Task<bool> ArtifactExistsAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            if (!storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Requested file is not in the project's storage scope.");

            return Task.FromResult(_objects.ContainsKey(storageKey));
        }
    }

    /// <summary>
    /// Counts how many times Fish Audio would actually be billed. This is the counter the ticket's
    /// whole finance argument rests on: a repeat run must add ZERO to it.
    /// </summary>
    internal sealed class CountingSpeechSynthesisClient : ISpeechSynthesisClient
    {
        private readonly List<SpeechRequest> _requests = [];

        public int CallCount => _requests.Count;

        public IReadOnlyList<SpeechRequest> Requests => _requests;

        public Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
        {
            _requests.Add(request);
            // A canonical RIFF/WAVE container, not arbitrary bytes: VoiceoverStepExecutor refuses to
            // upload a payload that is not a WAV container (bytes 0..3 "RIFF", bytes 8..11 "WAVE"),
            // because under a content-addressed key a bad object becomes a permanent cache entry
            // every later execution trusts. Arbitrary bytes here would fail the line for the wrong
            // reason and make this test assert a failure path instead of the reuse path.
            return Task.FromResult(new SynthesizedSpeech(
                new MemoryStream([
                    // "RIFF" + chunk size (36 + 4 bytes of data), "WAVE"
                    0x52, 0x49, 0x46, 0x46, 0x28, 0x00, 0x00, 0x00,
                    0x57, 0x41, 0x56, 0x45,
                    // "fmt " + 16-byte PCM format chunk
                    0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00,
                    0x01, 0x00, 0x01, 0x00, 0x40, 0x1F, 0x00, 0x00,
                    0x40, 0x1F, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00,
                    // "data" + 4 bytes of silence
                    0x64, 0x61, 0x74, 0x61, 0x04, 0x00, 0x00, 0x00,
                    0x80, 0x80, 0x80, 0x80
                ]), "audio/wav"));
        }
    }

    private static VoiceoverStepConfig InlineConfig(params string[] texts)
    {
        var lines = new List<VoiceoverLine>();
        for (int i = 0; i < texts.Length; i++)
            lines.Add(new VoiceoverLine(texts[i], i * 2.0, null));

        return new VoiceoverStepConfig(
            Source: new VoiceoverSource(VoiceoverSourceKind.Inline, lines),
            ProviderId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Model: "s2.1-pro-free",
            DefaultVoiceId: "default-voice",
            MaxBytes: 2_000_000);
    }

    private static StepExecutionContext ContextFor(
        string configJson, Guid executionId, Guid projectId)
    {
        var step = new WorkflowStep
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            WorkflowDefinitionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            AgentDefinitionId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            StepOrder = 1,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = configJson
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = executionId, ProjectId = projectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "cross-execution-test",
            CancellationToken = CancellationToken.None
        };
    }

    /// <summary>
    /// Builds a FRESH executor over the shared store — one per execution, exactly as a fresh
    /// process would. Reusing one instance would let a leftover in-process cache mask the defect
    /// this test exists to catch, which is precisely how the deleted <c>_lineCache</c> hid it.
    /// </summary>
    private static VoiceoverStepExecutor FreshExecutor(
        CountingSpeechSynthesisClient client,
        IProjectFileWorkspace workspace)
    {
        var provider = new ResolvedSpeechSynthesisProvider(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "fish",
            InferenceProviderKind.FishAudio,
            "https://api.fish.audio",
            "s2.1-pro-free",
            "test-key",
            30);

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var factory = new Mock<ISpeechSynthesisClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>())).Returns(client);

        var probe = new Mock<IMediaProbe>();
        probe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(
                DurationSec: 1.75,
                FpsNum: 30,
                FpsDen: 1,
                Width: 0,
                Height: 0,
                VideoCodec: null,
                AudioCodec: "pcm_s16le",
                AudioSampleRate: 44100));

        return new VoiceoverStepExecutor(
            factory.Object,
            resolver.Object,
            probe.Object,
            workspace,
            // This file is about the artifact key a repeat run emits, not about word timings, so
            // alignment is stubbed to its documented "no transcription provider" degradation. The
            // real service's own behaviour is covered by VoiceoverAlignmentServiceTests.
            new NoAlignmentService(),
            NullLogger<VoiceoverStepExecutor>.Instance,
            new NoRowsVoiceResolver());
    }

    /// <summary>
    /// The identity-shaped stub for <see cref="IVoiceoverAlignmentService"/>: word timings are
    /// irrelevant to every assertion in this file, and a loose Moq mock would answer with a null
    /// <see cref="VoiceoverAlignmentResult"/> (Moq's default for <c>Task&lt;T&gt;</c>), turning a
    /// deliberate non-assertion into an accidental failed line.
    /// </summary>
    private sealed class NoAlignmentService : IVoiceoverAlignmentService
    {
        public Task<VoiceoverAlignmentResult> AlignAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult(VoiceoverAlignmentResult.NotApplied(VoiceoverAlignmentReasons.NoTranscriptionProvider));
    }

    private static JsonElement ParseOutput(StepExecutionResult result) =>
        JsonDocument.Parse(result.Output).RootElement.Clone();

    [Fact]
    public async Task Second_execution_of_the_same_lines_reuses_the_first_executions_wav_without_re_synthesizing()
    {
        // Arrange
        string configJson = JsonSerializer.Serialize(InlineConfig("Buy ReelBolt now"), ConfigJsonOptions);

        var store = new InMemoryObjectStoreWorkspace();
        var client = new CountingSpeechSynthesisClient();

        var executionA = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var executionB = Guid.Parse("99999999-8888-7777-6666-555555555555");

        // Act — execution A (nothing in the object store yet).
        StepExecutionResult resultA = await FreshExecutor(client, store)
            .ExecuteAsync(ContextFor(configJson, executionA, ProjectId));

        int callsAfterA = client.CallCount;

        // Act — execution B: a FRESH executor instance, same config and text, different execution id.
        StepExecutionResult resultB = await FreshExecutor(client, store)
            .ExecuteAsync(ContextFor(configJson, executionB, ProjectId));

        // Assert
        resultA.Status.Should().Be(StepStatus.Completed);
        resultB.Status.Should().Be(StepStatus.Completed);

        // (a) Fish Audio was billed exactly ONCE across both executions.
        callsAfterA.Should().Be(1, "execution A must synthesize its one line once");
        client.CallCount.Should().Be(1,
            "execution B must reuse the stored WAV, not pay Fish Audio a second time");

        JsonElement lineA = ParseOutput(resultA).GetProperty("view").GetProperty("lines")[0];
        JsonElement lineB = ParseOutput(resultB).GetProperty("view").GetProperty("lines")[0];

        string keyA = lineA.GetProperty("storageKey").GetString()!;
        string keyB = lineB.GetProperty("storageKey").GetString()!;

        // (b) Both executions name the byte-identical object.
        keyB.Should().Be(keyA, "the artifact is content-addressed, so both executions emit the same key");

        // (c) That key carries no execution id — it is reproducible from (text, voiceId, model).
        keyA.Should().NotContain(executionA.ToString());
        keyA.Should().NotContain(executionB.ToString());
        keyA.Should().NotContain("voiceover/" + executionA);
        keyA.Should().StartWith($"projects/{ProjectId}/agentFiles/voiceover/lines/");
        keyA.Should().EndWith(".wav");

        // (d) The object genuinely exists in the store — the key is not a promise, it is a fact
        // verified by HEAD within the emitting execution.
        store.Contains(keyA).Should().BeTrue();
        store.UploadedLineKeys.Should().ContainSingle("only execution A uploaded a line artifact");

        // The cached path still reports a real, probed duration for THIS execution — the consumer
        // needs durationSec, and re-reading it from the object is what makes the reuse safe.
        lineB.GetProperty("status").GetString().Should().Be("ok");
        lineB.GetProperty("durationSec").GetDouble().Should().Be(1.75);
    }

    [Fact]
    public async Task Second_execution_with_different_text_synthesizes_a_second_distinct_object()
    {
        // The mirror image: content-addressing must key on the CONTENT. Different narration is
        // different work and must not be served from the first execution's object.
        var store = new InMemoryObjectStoreWorkspace();
        var client = new CountingSpeechSynthesisClient();

        string configA = JsonSerializer.Serialize(InlineConfig("Buy ReelBolt now"), ConfigJsonOptions);
        string configB = JsonSerializer.Serialize(InlineConfig("Learn more today"), ConfigJsonOptions);

        StepExecutionResult resultA = await FreshExecutor(client, store)
            .ExecuteAsync(ContextFor(configA, Guid.NewGuid(), ProjectId));
        StepExecutionResult resultB = await FreshExecutor(client, store)
            .ExecuteAsync(ContextFor(configB, Guid.NewGuid(), ProjectId));

        resultA.Status.Should().Be(StepStatus.Completed);
        resultB.Status.Should().Be(StepStatus.Completed);

        client.CallCount.Should().Be(2, "different narration text is genuinely different synthesis work");

        string keyA = ParseOutput(resultA).GetProperty("view").GetProperty("lines")[0]
            .GetProperty("storageKey").GetString()!;
        string keyB = ParseOutput(resultB).GetProperty("view").GetProperty("lines")[0]
            .GetProperty("storageKey").GetString()!;

        keyB.Should().NotBe(keyA);
        store.LineArtifactKeys.Should().HaveCount(2);
    }
}
