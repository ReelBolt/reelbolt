#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Environment-gated integration tests for the voiceover pipeline. They exercise the REAL local
/// fish-speech TTS server, the REAL ffprobe binary and a REAL S3-compatible bucket — no mocks.
///
/// <para>
/// <b>Why this file exists.</b> Two defects were fixed late on this branch —
/// <c>VideoCompileStepExecutor.ResolveVoiceoverAsync</c> (consumer) and the sandbox
/// <c>StageVoiceoverAudio</c> tool (producer) — and both had requested the WRONG S3 object key, so
/// every per-line voiceover WAV went missing and the feature silently did nothing end to end. Both
/// fixes are covered ONLY by mocked-<c>IAmazonS3</c> unit tests that match <c>GetObjectAsync</c> on
/// a synthetic key string, which is exactly the kind of test that passes while the real system is
/// broken. These tests close that gap the only way it can be closed: by putting a real WAV in a
/// real bucket under the key the producer wrote and pulling it back out with the key the consumer
/// asks for.
/// </para>
///
/// <para>
/// <b>Gating.</b> Every test here is marked <see cref="RealInfrastructureFactAttribute"/>, which
/// sets xUnit's <c>Skip</c> at discovery time whenever the required environment variables are
/// absent. So the default <c>dotnet test inference/ReelBolt.sln</c> run reports them as skipped
/// and can never fail because a developer machine has no TTS server or bucket.
/// </para>
///
/// <para>
/// <b>Running the gated path.</b> Export <c>REELBOLT_IT_TTS_ENDPOINT</c>,
/// <c>REELBOLT_IT_S3_ENDPOINT</c>, <c>REELBOLT_IT_S3_ACCESS_KEY</c>,
/// <c>REELBOLT_IT_S3_SECRET_KEY</c> and <c>REELBOLT_IT_S3_BUCKET</c> (optionally
/// <c>REELBOLT_IT_S3_REGION</c>, default <c>garage</c>), then run
/// <c>dotnet test inference/ReelBolt.sln --filter FullyQualifiedName~VoiceoverRealInfrastructureTests</c>.
/// No credential is ever read from, or written to, the repository.
/// </para>
///
/// <para>
/// <b>Isolation.</b> Each run mints its own <c>projectId</c>/<c>executionId</c>, so it can never
/// read or overwrite a pre-existing object, and deletes exactly the one key it uploaded in a
/// <c>finally</c> block.
/// </para>
/// </summary>
public class VoiceoverRealInfrastructureTests
{
    [RealInfrastructureFact]
    public async Task FishAudio_client_returns_a_real_RIFF_WAVE_stream_from_the_local_server()
    {
        (byte[] wav, string contentType) = await SynthesizeRealVoiceoverWavAsync(CancellationToken.None);

        // A RIFF/WAVE header alone is 44 bytes; anything at or below that means the server
        // answered with an envelope rather than audio.
        wav.Length.Should().BeGreaterThan(44);
        Encoding.ASCII.GetString(wav, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(wav, 8, 4).Should().Be("WAVE");
        contentType.Should().Be("audio/wav");
    }

    [RealInfrastructureFact]
    public async Task Real_ffprobe_reports_a_positive_duration_and_an_audio_codec_for_synthesized_speech()
    {
        string workDirectory = CreateWorkDirectory();
        try
        {
            (byte[] wav, _) = await SynthesizeRealVoiceoverWavAsync(CancellationToken.None);
            string wavPath = Path.Combine(workDirectory, "vo0.wav");
            await File.WriteAllBytesAsync(wavPath, wav);

            IMediaProbe probe = CreateMediaProbe();
            MediaProbeResult result = await probe.ProbeAsync(wavPath, CancellationToken.None);

            result.DurationSec.Should().BeGreaterThan(0);
            result.AudioCodec.Should().NotBeNullOrWhiteSpace();

            // This is the *exact* predicate VoiceoverStepExecutor applies after probing a freshly
            // synthesized line before it uploads it, and the exact one
            // VideoCompileStepExecutor.ResolveVoiceoverAsync applies after downloading it back.
            // Proving it against REAL synthesized audio is the point of this test: a probe that
            // reports zero duration or a null codec for real speech is what makes a line silently
            // "dropped" end to end.
            (result.DurationSec <= 0 || result.AudioCodec is null).Should().BeFalse();
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    [RealInfrastructureFact]
    public async Task Voiceover_wav_round_trips_through_the_real_bucket_at_the_key_the_compile_step_downloads()
    {
        string bucket = VoiceoverIntegrationEnvironment.Require(VoiceoverIntegrationEnvironment.S3Bucket);
        string workDirectory = CreateWorkDirectory();

        Guid projectId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        // Exactly what VoiceoverStepExecutor passes to UploadArtifactAsync. Its own storage key is
        // not this string, though — UploadArtifactAsync prefixes it with
        // projects/{projectId}/{category}/, which is precisely the mismatch that broke the feature.
        string artifactName = $"voiceover/{executionId}/vo0.wav";

        // The same expression, with the same arguments, that
        // VideoCompileStepExecutor.ResolveVoiceoverAsync uses to address the line it must download.
        // If the two sides ever drift apart again, the round trip below fails here.
        string consumerKey = ProjectFilePath.BuildStorageKey(
            projectId, "agentFiles", directoryPath: null, storageFileName: artifactName);

        IAmazonS3 s3 = CreateS3Client();
        string uploadedKey = string.Empty;
        try
        {
            (byte[] wav, _) = await SynthesizeRealVoiceoverWavAsync(CancellationToken.None);
            string sourcePath = Path.Combine(workDirectory, "vo0.wav");
            await File.WriteAllBytesAsync(sourcePath, wav);

            ProjectFileWorkspace workspace = CreateWorkspace(s3, bucket);

            uploadedKey = await workspace.UploadArtifactAsync(
                projectId, sourcePath, artifactName, "audio/wav", CancellationToken.None);

            uploadedKey.Should().Be(
                consumerKey, "the producer must write the very key the compile step later downloads");

            string downloadedPath = Path.Combine(workDirectory, "vo0-round-tripped.wav");
            await workspace.DownloadStorageKeyToFileAsync(
                projectId, consumerKey, downloadedPath, CancellationToken.None);

            (await File.ReadAllBytesAsync(downloadedPath)).Should().Equal(
                wav, "the object fetched by the consumer key must be byte-identical to what was synthesized");

            // ---- Second assertion: the historical defect cannot silently return ----
            // Before the fix, both sides addressed the line at this prefix-less key. Assert that it
            // resolves to nothing — first through the workspace API (which refuses a key outside
            // projects/{projectId}/ at all), and then against the REAL bucket, so the claim is about
            // the stored object and not merely about a local guard.
            string prefixLessKey = artifactName;
            prefixLessKey.Should().NotBe(consumerKey);
            prefixLessKey.Should().NotStartWith($"projects/{projectId}/");

            Func<Task> downloadAtPrefixLessKey = () => workspace.DownloadStorageKeyToFileAsync(
                projectId,
                prefixLessKey,
                Path.Combine(workDirectory, "never-written.wav"),
                CancellationToken.None);
            await downloadAtPrefixLessKey.Should().ThrowAsync<Exception>();

            Func<Task> rawGetAtPrefixLessKey = () => s3.GetObjectAsync(
                new GetObjectRequest { BucketName = bucket, Key = prefixLessKey }, CancellationToken.None);
            await rawGetAtPrefixLessKey.Should()
                .ThrowAsync<AmazonS3Exception>()
                .Where(ex => ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey");
        }
        finally
        {
            // Delete exactly the one object this run created. Everything is namespaced by a fresh
            // Guid, so nothing pre-existing in the bucket can be touched.
            if (uploadedKey.Length > 0)
            {
                try
                {
                    await s3.DeleteObjectAsync(
                        new DeleteObjectRequest { BucketName = bucket, Key = uploadedKey },
                        CancellationToken.None);
                }
                catch
                {
                    // Best-effort cleanup only — a failed delete must not mask the test's result.
                }
            }

            s3.Dispose();
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    /// Synthesizes one short line through the REAL, production-constructed speech synthesis stack —
    /// <see cref="SpeechSynthesisClientFactory"/> resolving a Fish Audio
    /// <see cref="ResolvedSpeechSynthesisProvider"/> into a live
    /// <see cref="FishAudioSpeechSynthesisClient"/> pointed at
    /// <c>REELBOLT_IT_TTS_ENDPOINT</c> — using the same request shape the voiceover step builds.
    /// The local server accepting the client's own POST /v1/tts body (format=wav, sample_rate,
    /// latency, prosody) is itself the proof that the wire format matches.
    /// </summary>
    private static async Task<(byte[] Bytes, string ContentType)> SynthesizeRealVoiceoverWavAsync(
        CancellationToken ct)
    {
        ResolvedSpeechSynthesisProvider provider = new(
            ProviderId: null,
            Name: "local-fish-speech-integration",
            Kind: InferenceProviderKind.FishAudio,
            Endpoint: VoiceoverIntegrationEnvironment.Require(VoiceoverIntegrationEnvironment.TtsEndpoint),
            ModelName: "s2.1-pro",
            // A local server needs no credential. An empty key makes the factory omit the
            // Authorization header entirely, so no token is ever sent or needed here.
            ApiKey: string.Empty,
            TimeoutSeconds: 120);

        ISpeechSynthesisClient client = new SpeechSynthesisClientFactory().Get(provider);

        SpeechRequest request = new(
            Text: "ReelBolt voiceover integration check.",
            VoiceId: null,
            ModelOverride: "s2.1-pro");

        SynthesizedSpeech speech = await client.SynthesizeAsync(request, ct);

        using MemoryStream buffer = new();
        await speech.Wav.CopyToAsync(buffer, ct);
        return (buffer.ToArray(), speech.ContentType);
    }

    /// <summary>
    /// Builds the real <see cref="FfprobeMediaProbe"/> over the real <see cref="FfmpegVideoToolRunner"/>,
    /// which shells out to the <c>ffprobe</c> resolved from PATH.
    /// </summary>
    private static IMediaProbe CreateMediaProbe()
    {
        IOptions<VideoEditingOptions> options = Options.Create(new VideoEditingOptions());
        FfmpegVideoToolRunner runner = new(options, NullLogger<FfmpegVideoToolRunner>.Instance);
        return new FfprobeMediaProbe(runner, options, NullLogger<FfprobeMediaProbe>.Instance);
    }

    /// <summary>
    /// Builds the real <see cref="ProjectFileWorkspace"/> — the exact class the workflow engine
    /// resolves — against the configured bucket. Its S3 configuration mirrors
    /// <c>WorkflowEngineServiceCollectionExtensions</c>: path-style addressing and an explicit
    /// authentication region, both of which a Garage-compatible endpoint requires.
    /// </summary>
    private static ProjectFileWorkspace CreateWorkspace(IAmazonS3 s3, string bucket)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MinIO:BucketName"] = bucket
            })
            .Build();

        // UploadArtifactAsync/DownloadStorageKeyToFileAsync never resolve a DbContext, so an empty
        // service provider is sufficient here — and it keeps this test from needing a database.
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
            // AWS SDK v4 defaults to aws-chunked/trailer-signed uploads with a flexible checksum
            // Garage does not implement (every PutObject 403s). Both settings mirror production.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });

    private static string CreateWorkDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"reelbolt-voiceover-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips its test at discovery time unless every environment
/// variable named by <see cref="VoiceoverIntegrationEnvironment.RequiredVariables"/> is set.
///
/// <para>
/// This is the standard xUnit v2 idiom, and it is deliberately used instead of the
/// <c>Xunit.SkippableFact</c> package: this test project does not reference that package, and
/// adding a NuGet dependency is out of scope for a ticket that may add exactly one test file. A
/// plain early <c>return</c> was rejected because it would report these as PASSED on every ordinary
/// run, quietly asserting nothing — the failure mode this whole ticket exists to remove.
/// </para>
/// </summary>
public sealed class RealInfrastructureFactAttribute : FactAttribute
{
    public RealInfrastructureFactAttribute()
    {
        string[] missing = VoiceoverIntegrationEnvironment.MissingVariables();
        if (missing.Length > 0)
        {
            Skip = "Requires a real local fish-speech TTS server, a real ffprobe binary and a real "
                + "S3-compatible bucket. Missing environment variable(s): " + string.Join(", ", missing)
                + ". See VoiceoverRealInfrastructureTests for how to run this gated integration test.";
        }
    }
}

/// <summary>
/// The environment contract of <see cref="VoiceoverRealInfrastructureTests"/>. Centralised so the
/// skip message and the assertions below can never disagree about which variables are required.
/// </summary>
public static class VoiceoverIntegrationEnvironment
{
    public const string TtsEndpoint = "REELBOLT_IT_TTS_ENDPOINT";
    public const string S3Endpoint = "REELBOLT_IT_S3_ENDPOINT";
    public const string S3AccessKey = "REELBOLT_IT_S3_ACCESS_KEY";
    public const string S3SecretKey = "REELBOLT_IT_S3_SECRET_KEY";
    public const string S3Bucket = "REELBOLT_IT_S3_BUCKET";

    /// <summary>Optional. Garage's own configured region is the local-development default.</summary>
    public const string S3Region = "REELBOLT_IT_S3_REGION";
    public const string DefaultS3Region = "garage";

    /// <summary>
    /// Every variable that must be present for the gated path to run. <c>REELBOLT_IT_S3_REGION</c>
    /// is deliberately absent from this list — it has a working default.
    /// </summary>
    public static readonly string[] RequiredVariables =
    [
        TtsEndpoint,
        S3Endpoint,
        S3AccessKey,
        S3SecretKey,
        S3Bucket
    ];

    public static string[] MissingVariables() =>
        RequiredVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();

    public static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Environment variable {name} is not set. This test should have been skipped.");

    public static string Region =>
        Environment.GetEnvironmentVariable(S3Region) is { Length: > 0 } region ? region : DefaultS3Region;
}
