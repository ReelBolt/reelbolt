using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;

/// <summary>
/// Tests for <see cref="VoiceoverAlignmentService"/> — the seam that turns an already-emitted
/// voiceover line WAV into per-word timings by re-transcribing it through
/// <see cref="ITranscriptionClient"/>.
///
/// <para>
/// The four named reasons are the contract, not decoration: they are the keys of the step output's
/// <c>meta.alignment.reasons</c> histogram, so an operator counts them and a future captions/fit
/// consumer branches on them. Every one of them is asserted here by VALUE.
/// </para>
/// </summary>
public class VoiceoverAlignmentServiceTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private const string StorageKey =
        "projects/11111111-1111-1111-1111-111111111111/agentFiles/voiceover/lines/deadbeef.wav";

    /// <summary>
    /// A canonical RIFF/WAVE header. The service deletes the scratch file it downloaded in a
    /// <c>finally</c> and never inspects the bytes itself, so this only has to be a file that
    /// exists and is openable.
    /// </summary>
    private static byte[] FakeLineWav() =>
    [
        0x52, 0x49, 0x46, 0x46, 0x28, 0x00, 0x00, 0x00,
        0x57, 0x41, 0x56, 0x45,
        0x66, 0x6D, 0x74, 0x20, 0x10, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x01, 0x00, 0x40, 0x1F, 0x00, 0x00,
        0x40, 0x1F, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00,
        0x64, 0x61, 0x74, 0x61, 0x04, 0x00, 0x00, 0x00,
        0x80, 0x80, 0x80, 0x80
    ];

    private static ResolvedTranscriptionProvider Provider() => new(
        ProviderId: Guid.NewGuid(),
        Name: "Local Whisper",
        Kind: InferenceProviderKind.OpenAICompatible,
        Endpoint: "http://localhost:9002/v1",
        ModelName: "Systran/faster-whisper-small",
        ApiKey: string.Empty,
        TimeoutSeconds: 30);

    /// <summary>
    /// A workspace whose download actually WRITES the destination file, because the service opens
    /// that path with a real <see cref="FileStream"/> immediately afterwards. A bare Moq
    /// <c>Returns(Task.CompletedTask)</c> would leave the file missing and every "applied" case
    /// would fail as <c>TRANSCRIPTION_FAILED</c> for a reason that has nothing to do with what it
    /// is testing.
    /// </summary>
    private static Mock<IProjectFileWorkspace> DownloadingWorkspace()
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, CancellationToken>((_, _, path, _) =>
                File.WriteAllBytes(path, FakeLineWav()))
            .Returns(Task.CompletedTask);
        return workspace;
    }

    private static VoiceoverAlignmentService CreateService(
        Mock<IInferenceProviderResolver> resolver,
        Mock<ITranscriptionClientFactory> factory,
        Mock<IProjectFileWorkspace> workspace) =>
        new(resolver.Object, factory.Object, workspace.Object,
            NullLogger<VoiceoverAlignmentService>.Instance);

    private static Mock<IInferenceProviderResolver> ResolverReturning(ResolvedTranscriptionProvider provider)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);
        return resolver;
    }

    private static Mock<ITranscriptionClientFactory> FactoryReturning(TranscriptResult result) =>
        FactoryReturningWithClient(result).Factory;

    /// <summary>
    /// The factory AND the client behind it, for the cases that assert on what the client was
    /// asked to do — the <c>fileName</c>, the null language and the <c>wordTimestamps: true</c>
    /// flag are the wire shape, and no assertion on the returned words can see them.
    /// </summary>
    private static (Mock<ITranscriptionClientFactory> Factory, Mock<ITranscriptionClient> Client)
        FactoryReturningWithClient(TranscriptResult result)
    {
        var client = new Mock<ITranscriptionClient>();
        client
            .Setup(c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var factory = new Mock<ITranscriptionClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(client.Object);
        return (factory, client);
    }

    private static TranscriptResult Transcript(params TranscriptWord[] words) =>
        new(string.Join(' ', words.Select(w => w.Text)), [], words, "en");

    // =====================================================================
    // 1. The mapping itself
    // =====================================================================

    [Fact]
    public async Task Applied_maps_each_recognized_word_and_preserves_its_offsets_verbatim()
    {
        // The second and third words start well after zero ON PURPOSE: these timings are relative
        // to the start of THIS line's own audio, and re-basing them to zero (or to any other
        // origin) would silently break every downstream caption and fit computation while still
        // looking like a plausible word list.
        TranscriptResult transcript = Transcript(
            new TranscriptWord("ReelBolt", 0.24, 0.86),
            new TranscriptWord("voiceover", 4.10, 4.72),
            new TranscriptWord("check.", 7.001, 7.55));

        (Mock<ITranscriptionClientFactory> factory, Mock<ITranscriptionClient> client) =
            FactoryReturningWithClient(transcript);
        VoiceoverAlignmentService service = CreateService(
            ResolverReturning(Provider()), factory, DownloadingWorkspace());

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, StorageKey, CancellationToken.None);

        result.Applied.Should().BeTrue();
        result.Reason.Should().BeNull("an applied result has no reason to record");

        result.Words.Should().HaveCount(3);
        result.Words.Select(w => w.Text).Should().Equal("ReelBolt", "voiceover", "check.");
        result.Words.Select(w => w.StartSec).Should().Equal(0.24, 4.10, 7.001);
        result.Words.Select(w => w.EndSec).Should().Equal(0.86, 4.72, 7.55);

        // The SDK callers are handed the container's real name, no language hint, and the
        // timestamp flag — none of which any assertion on the returned words can see.
        client.Verify(
            c => c.TranscribeAsync(
                It.IsAny<Stream>(),
                "deadbeef.wav",
                null,
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Applied_leaves_an_empty_word_text_alone_rather_than_filtering_it()
    {
        // The word list is reported exactly as the recognizer produced it. Dropping or merging
        // tokens here would make the step's output disagree with the ASR evidence a reviewer reads
        // in the analysis artifact, and "the recognizer emitted a blank token" is a fact a caption
        // consumer may legitimately need to see.
        TranscriptResult transcript = Transcript(
            new TranscriptWord("Hello", 0.0, 0.4),
            new TranscriptWord(string.Empty, 0.4, 0.5),
            new TranscriptWord("world", 0.5, 0.9));

        VoiceoverAlignmentService service = CreateService(
            ResolverReturning(Provider()), FactoryReturning(transcript), DownloadingWorkspace());

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, StorageKey, CancellationToken.None);

        result.Applied.Should().BeTrue();
        result.Words.Should().HaveCount(3);
        result.Words[1].Text.Should().BeEmpty();
        result.Words[1].StartSec.Should().Be(0.4);
    }

    // =====================================================================
    // 2-4. The three named failure reasons, plus NO_WORDS
    // =====================================================================

    [Fact]
    public async Task A_null_resolution_degrades_with_NO_TRANSCRIPTION_PROVIDER_and_never_downloads()
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedTranscriptionProvider)null);

        Mock<IProjectFileWorkspace> workspace = DownloadingWorkspace();
        VoiceoverAlignmentService service = CreateService(
            resolver, FactoryReturning(Transcript(new TranscriptWord("x", 0, 1))), workspace);

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, StorageKey, CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Reason.Should().Be(VoiceoverAlignmentReasons.NoTranscriptionProvider);
        result.Words.Should().BeEmpty();

        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a deployment with no ASR configured must not pay a download to discover that");

        // The documented precedence: the default Transcription row, never an explicit id and never
        // a legacy AzureOpenAI:* chat fallback.
        resolver.Verify(
            r => r.ResolveTranscriptionAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failed_download_degrades_with_DOWNLOAD_FAILED()
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("S3 said no"));

        (Mock<ITranscriptionClientFactory> factory, Mock<ITranscriptionClient> client) =
            FactoryReturningWithClient(Transcript(new TranscriptWord("x", 0, 1)));
        VoiceoverAlignmentService service = CreateService(ResolverReturning(Provider()), factory, workspace);

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, StorageKey, CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Reason.Should().Be(VoiceoverAlignmentReasons.DownloadFailed);

        // Distinct from TRANSCRIPTION_FAILED for a reason: the ASR backend was never reached, so
        // the operator's next step is to look at the object store, not at Whisper.
        client.Verify(
            c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_client_that_throws_degrades_with_TRANSCRIPTION_FAILED()
    {
        var client = new Mock<ITranscriptionClient>();
        client
            .Setup(c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("502 Bad Gateway"));

        var factory = new Mock<ITranscriptionClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(client.Object);

        VoiceoverAlignmentService service = CreateService(ResolverReturning(Provider()), factory, DownloadingWorkspace());

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, StorageKey, CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Reason.Should().Be(VoiceoverAlignmentReasons.TranscriptionFailed);
    }

    [Fact]
    public async Task A_factory_that_refuses_to_build_a_client_degrades_with_TRANSCRIPTION_FAILED()
    {
        // The controller already rejects an unsupported kind/capability pairing, but a row written
        // straight to the database can still reach TranscriptionClientFactory's NotSupportedException.
        // A line must not fail for it.
        var factory = new Mock<ITranscriptionClientFactory>();
        factory
            .Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>()))
            .Throws(new NotSupportedException("Anthropic exposes no speech-to-text API."));

        VoiceoverAlignmentService service = CreateService(ResolverReturning(Provider()), factory, DownloadingWorkspace());

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, StorageKey, CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Reason.Should().Be(VoiceoverAlignmentReasons.TranscriptionFailed);
    }

    [Fact]
    public async Task An_empty_word_list_degrades_with_NO_WORDS()
    {
        // Silence, music or a backend configured without word timestamps. The call SUCCEEDED — this
        // must not be reported as a transcription failure, or every silent line would look like an
        // outage.
        VoiceoverAlignmentService service = CreateService(
            ResolverReturning(Provider()),
            FactoryReturning(new TranscriptResult("…", [], [], "en")),
            DownloadingWorkspace());

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, StorageKey, CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Reason.Should().Be(VoiceoverAlignmentReasons.NoWords);
        result.Words.Should().BeEmpty();
    }

    [Fact]
    public async Task A_transcript_carrying_a_null_word_list_degrades_with_NO_WORDS_rather_than_throwing()
    {
        VoiceoverAlignmentService service = CreateService(
            ResolverReturning(Provider()),
            FactoryReturning(new TranscriptResult("…", [], null!, "en")),
            DownloadingWorkspace());

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, StorageKey, CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Reason.Should().Be(VoiceoverAlignmentReasons.NoWords);
    }

    [Fact]
    public async Task A_blank_storage_key_degrades_with_DOWNLOAD_FAILED()
    {
        Mock<IProjectFileWorkspace> workspace = DownloadingWorkspace();
        VoiceoverAlignmentService service = CreateService(
            ResolverReturning(Provider()), FactoryReturning(Transcript(new TranscriptWord("x", 0, 1))), workspace);

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, "   ", CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Reason.Should().Be(VoiceoverAlignmentReasons.DownloadFailed);
        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_resolver_that_throws_degrades_with_NO_TRANSCRIPTION_PROVIDER_rather_than_escaping()
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider store unavailable"));

        VoiceoverAlignmentService service = CreateService(
            resolver, FactoryReturning(Transcript(new TranscriptWord("x", 0, 1))), DownloadingWorkspace());

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, StorageKey, CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Reason.Should().Be(VoiceoverAlignmentReasons.NoTranscriptionProvider);
    }

    // =====================================================================
    // 5. Cancellation is re-thrown, never recorded as a degraded line
    // =====================================================================

    [Fact]
    public async Task A_cancelled_token_is_rethrown_when_the_download_is_cancelled()
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var ct = new CancellationToken(canceled: true);
        VoiceoverAlignmentService service = CreateService(
            ResolverReturning(Provider()), FactoryReturning(Transcript(new TranscriptWord("x", 0, 1))), workspace);

        Func<Task> act = () => service.AlignAsync(ProjectId, StorageKey, ct);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "an operator stopping a run must not be reported as an alignment failure");
    }

    [Fact]
    public async Task A_cancelled_token_is_rethrown_when_the_transcription_is_cancelled()
    {
        var client = new Mock<ITranscriptionClient>();
        client
            .Setup(c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var factory = new Mock<ITranscriptionClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(client.Object);

        var ct = new CancellationToken(canceled: true);
        VoiceoverAlignmentService service = CreateService(ResolverReturning(Provider()), factory, DownloadingWorkspace());

        Func<Task> act = () => service.AlignAsync(ProjectId, StorageKey, ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_provider_TIMEOUT_is_still_recorded_as_a_degraded_line_and_not_rethrown()
    {
        // The deliberate inverse of the two cases above, and the reason the `when
        // (ct.IsCancellationRequested)` guard exists at all: a TaskCanceledException arriving with
        // OUR token UNCANCELLED is the provider's own timeout, which must degrade the line exactly
        // like any other ASR failure.
        var client = new Mock<ITranscriptionClient>();
        client
            .Setup(c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("the HTTP client timed out"));

        var factory = new Mock<ITranscriptionClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(client.Object);

        VoiceoverAlignmentService service = CreateService(ResolverReturning(Provider()), factory, DownloadingWorkspace());

        VoiceoverAlignmentResult result = await service.AlignAsync(ProjectId, StorageKey, CancellationToken.None);

        result.Applied.Should().BeFalse();
        result.Reason.Should().Be(VoiceoverAlignmentReasons.TranscriptionFailed);
    }

    // =====================================================================
    // 6. The reason set is closed
    // =====================================================================

    [Fact]
    public void The_reason_set_is_exactly_the_four_documented_members()
    {
        // These strings are the keys of meta.alignment.reasons, so a rename is a breaking change to
        // the step output contract, not a refactor. Pinned as literals rather than compared against
        // the constants themselves, so a silent reword is caught here.
        VoiceoverAlignmentReasons.All.Should().Equal(
            "NO_TRANSCRIPTION_PROVIDER",
            "DOWNLOAD_FAILED",
            "TRANSCRIPTION_FAILED",
            "NO_WORDS");

        VoiceoverAlignmentReasons.All.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task The_scratch_file_is_removed_whether_alignment_applied_or_failed()
    {
        // The service writes into Path.GetTempPath(); a leak there is invisible until the disk
        // fills. Both exits are checked, because the applied path and the failing path take
        // different routes to the finally.
        List<string> downloaded = [];
        Mock<IProjectFileWorkspace> workspace = DownloadingWorkspace();
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, CancellationToken>((_, _, path, _) =>
            {
                File.WriteAllBytes(path, FakeLineWav());
                downloaded.Add(path);
            })
            .Returns(Task.CompletedTask);

        VoiceoverAlignmentService applied = CreateService(
            ResolverReturning(Provider()),
            FactoryReturning(Transcript(new TranscriptWord("ReelBolt", 0, 1))),
            workspace);
        (await applied.AlignAsync(ProjectId, StorageKey, CancellationToken.None)).Applied.Should().BeTrue();

        VoiceoverAlignmentService noWords = CreateService(
            ResolverReturning(Provider()),
            FactoryReturning(new TranscriptResult("…", [], [], "en")),
            workspace);
        (await noWords.AlignAsync(ProjectId, StorageKey, CancellationToken.None)).Reason
            .Should().Be(VoiceoverAlignmentReasons.NoWords);

        downloaded.Should().HaveCount(2);
        downloaded.Should().OnlyContain(path => !File.Exists(path));
    }

    // =====================================================================
    // 7. Env-gated: the REAL ASR backend
    // =====================================================================

    /// <summary>
    /// The end-to-end proof, in the phase-1 <c>VoiceoverRealInfrastructureTests</c> pattern: real
    /// speech is synthesized by the real TTS server, uploaded to the real bucket under the key the
    /// voiceover step emits, and aligned by the REAL service against the REAL ASR server.
    ///
    /// <para>
    /// It is <see cref="VoiceoverAlignmentFactAttribute"/>-gated, so an ordinary
    /// <c>dotnet test</c> run reports it as SKIPPED — never passed — on a machine with no local
    /// stack. Only the provider ROW is stubbed: <see cref="InferenceProviderResolver"/> needs a
    /// database and a provider store, and this test is about the alignment path, not about how a
    /// row is selected. The transcription factory, the S3 workspace and the ASR call are all real.
    /// </para>
    /// </summary>
    [VoiceoverAlignmentFact]
    public async Task Real_speech_synthesized_uploaded_and_aligned_through_the_real_stack_yields_words()
    {
        string bucket = VoiceoverIntegrationEnvironment.Require(VoiceoverIntegrationEnvironment.S3Bucket);
        Guid projectId = Guid.NewGuid();

        string storageKey = null;
        IAmazonS3 s3 = CreateS3Client();
        try
        {
            // 1. Real speech, through the production speech-synthesis stack.
            byte[] wav = await SynthesizeRealWavAsync(CancellationToken.None);

            // 2. Upload it under exactly the key shape the voiceover step writes.
            string workDirectory = Path.Combine(Path.GetTempPath(), $"reelforge-align-it-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDirectory);
            try
            {
                string localPath = Path.Combine(workDirectory, "line.wav");
                await File.WriteAllBytesAsync(localPath, wav);

                ProjectFileWorkspace workspace = CreateWorkspace(s3, bucket);
                storageKey = await workspace.UploadArtifactAsync(
                    projectId, localPath, "voiceover/lines/alignment-it.wav", "audio/wav", CancellationToken.None);

                // 3. The REAL service: real provider config, real transcription factory, real S3.
                var resolver = new Mock<IInferenceProviderResolver>();
                resolver
                    .Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ResolvedTranscriptionProvider(
                        ProviderId: null,
                        Name: "local-whisper-integration",
                        Kind: InferenceProviderKind.OpenAICompatible,
                        Endpoint: RequireAsr(VoiceoverAlignmentEnvironment.AsrEndpoint),
                        ModelName: RequireAsr(VoiceoverAlignmentEnvironment.AsrModel),
                        // speaches runs with no auth; an empty key makes the factory omit the header.
                        ApiKey: string.Empty,
                        TimeoutSeconds: 300));

                VoiceoverAlignmentService service = new(
                    resolver.Object,
                    new TranscriptionClientFactory(),
                    workspace,
                    NullLogger<VoiceoverAlignmentService>.Instance);

                // Act
                VoiceoverAlignmentResult result =
                    await service.AlignAsync(projectId, storageKey, CancellationToken.None);

                // Assert
                result.Applied.Should().BeTrue(
                    $"real synthesized speech through the real ASR server must produce words; got {result.Reason}");
                result.Words.Should().NotBeEmpty();

                // Timings are real and bounded by the audio, not zeros or NaNs.
                foreach (VoiceoverAlignedWord word in result.Words)
                {
                    word.StartSec.Should().BeGreaterThanOrEqualTo(0);
                    word.EndSec.Should().BeGreaterThanOrEqualTo(word.StartSec);
                    word.EndSec.Should().BeLessThan(60);
                }

                // The words are the words that were SPOKEN, not a hallucinated or empty transcript.
                // Normalised because ASR casing and punctuation are the recognizer's business.
                string recognized = Normalize(string.Concat(result.Words.Select(w => w.Text)));
                recognized.Should().Contain("forge");
            }
            finally
            {
                try { Directory.Delete(workDirectory, recursive: true); } catch { }
            }
        }
        finally
        {
            // Delete exactly the one object this run created.
            if (storageKey is not null)
            {
                try
                {
                    await s3.DeleteObjectAsync(new Amazon.S3.Model.DeleteObjectRequest
                    {
                        BucketName = bucket,
                        Key = storageKey
                    });
                }
                catch
                {
                    // Best-effort cleanup only: each run mints its own projectId, so a leftover
                    // object can never be read by a later run.
                }
            }

            s3.Dispose();
        }
    }

    private static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string RequireAsr(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Environment variable {name} is not set. This test should have been skipped.");

    /// <summary>
    /// Synthesizes one short line through the REAL production speech stack (the same shape as
    /// <c>VoiceoverRealInfrastructureTests.SynthesizeRealVoiceoverWavAsync</c>).
    /// </summary>
    private static async Task<byte[]> SynthesizeRealWavAsync(CancellationToken ct)
    {
        ResolvedSpeechSynthesisProvider provider = new(
            ProviderId: null,
            Name: "local-fish-speech-integration",
            Kind: InferenceProviderKind.FishAudio,
            Endpoint: VoiceoverIntegrationEnvironment.Require(VoiceoverIntegrationEnvironment.TtsEndpoint),
            ModelName: "s2.1-pro",
            ApiKey: string.Empty,
            TimeoutSeconds: 120);

        ISpeechSynthesisClient client = new SpeechSynthesisClientFactory().Get(provider);

        SynthesizedSpeech speech = await client.SynthesizeAsync(
            new SpeechRequest(
                Text: "ReelBolt voiceover alignment integration check.",
                VoiceId: null,
                ModelOverride: "s2.1-pro"),
            ct);

        using MemoryStream buffer = new();
        await speech.Wav.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>
    /// The real <see cref="ProjectFileWorkspace"/> against the configured bucket, configured
    /// exactly as <c>WorkflowEngineServiceCollectionExtensions</c> does.
    /// </summary>
    private static ProjectFileWorkspace CreateWorkspace(IAmazonS3 s3, string bucket)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = bucket })
            .Build();

        IServiceScopeFactory scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        return new ProjectFileWorkspace(s3, scopeFactory, configuration, NullLogger<ProjectFileWorkspace>.Instance);
    }

    private static IAmazonS3 CreateS3Client() => new AmazonS3Client(
        VoiceoverIntegrationEnvironment.Require(VoiceoverIntegrationEnvironment.S3AccessKey),
        VoiceoverIntegrationEnvironment.Require(VoiceoverIntegrationEnvironment.S3SecretKey),
        new AmazonS3Config
        {
            ServiceURL = VoiceoverIntegrationEnvironment.Require(VoiceoverIntegrationEnvironment.S3Endpoint),
            ForcePathStyle = true,
            AuthenticationRegion = VoiceoverIntegrationEnvironment.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
}

/// <summary>
/// The environment contract of the gated alignment test. It EXTENDS the phase-1 contract rather
/// than restating it: <see cref="VoiceoverIntegrationEnvironment.MissingVariables"/> already names
/// the TTS and S3 variables this test needs, so a change there cannot leave this gate disagreeing
/// with the one next door.
/// </summary>
public static class VoiceoverAlignmentEnvironment
{
    /// <summary>An OpenAI-compatible ASR server — the bundled <c>whisper</c> (speaches) service.</summary>
    public const string AsrEndpoint = "REELFORGE_IT_ASR_ENDPOINT";

    /// <summary>
    /// The ASR model name the provider row would carry. Required rather than defaulted: guessing a
    /// model name would turn "the server has a different model" into a confusing 404 instead of a
    /// clear skip.
    /// </summary>
    public const string AsrModel = "REELFORGE_IT_ASR_MODEL";

    /// <summary>Every variable required by the alignment gate, phase-1's included.</summary>
    public static string[] MissingVariables() =>
        VoiceoverIntegrationEnvironment.MissingVariables()
            .Concat(new[] { AsrEndpoint, AsrModel }
                .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            .ToArray();
}

/// <summary>
/// Skips at discovery time unless the whole real stack is configured. Same xUnit v2 idiom as
/// <see cref="RealInfrastructureFactAttribute"/>, extended with the two ASR variables.
/// </summary>
public sealed class VoiceoverAlignmentFactAttribute : FactAttribute
{
    public VoiceoverAlignmentFactAttribute()
    {
        string[] missing = VoiceoverAlignmentEnvironment.MissingVariables();
        if (missing.Length > 0)
        {
            Skip = "Requires a real local fish-speech TTS server, a real OpenAI-compatible ASR server, "
                + "a real ffprobe binary and a real S3-compatible bucket. Missing environment variable(s): "
                + string.Join(", ", missing)
                + ". See VoiceoverRealInfrastructureTests for how to run the gated integration tests.";
        }
    }
}
