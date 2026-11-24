#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Execution.Replay;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Inference;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using ReelBolt.WorkflowEngine.Tests.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-108: <see cref="AudioGenerateStepExecutor"/> over T-107's recorded ElevenLabs mocks (real client, stub transport,
/// key <c>test</c>), the real budget gate on an in-memory ledger, and a faked ffprobe. No live call, no real key.
/// </summary>
public sealed class AudioGenerateStepExecutorTests
{
    private sealed class FakeProbe(double seconds) : IMediaProbe
    {
        public int Calls { get; private set; }

        public Task<MediaProbeResult> ProbeAsync(IMediaWorkspace workspace, string virtualPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MediaProbeResult> ProbeAsync(string localFilePath, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new MediaProbeResult(seconds, 0, 1, 0, 0, null, "mp3", 44100));
        }
    }

    /// <summary>QA-P1-17: a client with a submit/poll split — it reports the vendor's request id, then dies before the
    /// result download, exactly the window a crash between submit and download leaves.</summary>
    private sealed class SubmitThenCrashClient : IAudioGenerationClient
    {
        public const string RequestId = "fal-req-9";

        public async Task<AudioGenerationResult> GenerateAsync(AudioGenerationRequest request, CancellationToken ct, Func<string, Task>? onSubmitted = null)
        {
            if (onSubmitted is not null) await onSubmitted(RequestId);
            throw new IOException("the connection dropped between the submit and the result download");
        }

        public Task<AudioGenerationProbeResult> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();

        public AudioGenerationCapabilities Describe(string model) =>
            AudioModelCatalog.Instance.Find(InferenceProviderKind.ElevenLabs, model)!.Capabilities;
    }

    private sealed class FixedContext(StepChangeRequestScope? scope) : IStepChangeRequestContext
    {
        public Task<StepChangeRequestScope?> FindAsync(WorkflowExecution execution, WorkflowStep step, CancellationToken ct) =>
            Task.FromResult(scope);
    }

    /// <summary>One project, one execution, one provider row, and every collaborator the executor touches.</summary>
    private sealed class Rig
    {
        public Guid ProjectId { get; } = Guid.NewGuid();
        public WorkflowExecution Execution { get; set; }
        public SharedInMemoryDbFactory Db { get; } = new();
        public AudioStubHandler Api { get; }
        public ResolvedAudioGenerationProvider? Provider { get; set; }
        public Mock<IProjectFileWorkspace> Workspace { get; } = new();
        public List<(string Key, string Name, string Mime, long Size)> Registered { get; } = [];
        public List<(string FileName, string Category, string Mime)> Uploaded { get; } = [];
        public List<UsageRecordRequest> Usage { get; } = [];
        public VideoGenerationOptions Options { get; } = new();
        public IAudioGenerationClient? ClientOverride { get; set; }
        public FakeProbe Probe { get; set; } = new(30);
        public List<ProjectWorkspaceFile> Files { get; } = [];

        /// <summary>The prompt gate the executor is given (null = none configured).</summary>
        public ReelBolt.Shared.Moderation.IContentModerationGate? Moderation { get; set; }

        /// <summary>The change request the next run carries (null = an ordinary run).</summary>
        public StepChangeRequestScope? Scope { get; set; }

        /// <summary>Text artifacts (the takes manifests) the executor uploaded, by storage key.</summary>
        public Dictionary<string, string> Stored { get; } = [];

        /// <summary>QA-P1-17: when set, provider resolution throws it (an unpriced platform model).</summary>
        public Exception? ResolveFailure { get; set; }

        public Rig(Func<HttpRequestMessage, HttpResponseMessage> answer, Guid? providerOrganizationId = null)
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId, Status = ExecutionStatus.Running };
            Api = new AudioStubHandler(answer);
            Provider = new ResolvedAudioGenerationProvider(
                Guid.NewGuid(), "ElevenLabs", InferenceProviderKind.ElevenLabs, InferenceProviderCapability.MusicGeneration,
                "https://api.elevenlabs.io", "", AudioFixtures.Key, 60, OrganizationId: providerOrganizationId,
                IsPlatformManaged: providerOrganizationId is null, AccountId: Guid.NewGuid());

            Workspace.Setup(w => w.UploadArtifactAsync(ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string path, string name, string mime, CancellationToken _, string category) =>
                {
                    File.Exists(path).Should().BeTrue();
                    Uploaded.Add((name, category, mime));
                    string stored = $"projects/{p}/{category}/{name}";
                    if (name.EndsWith(".json", StringComparison.Ordinal))
                        Stored[stored] = File.ReadAllText(path);
                    return stored;
                });
            Workspace.Setup(w => w.RegisterStoredObjectAsync(ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<long>(), It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string key, string name, string mime, long size, SummaryStatus _, FileIndexingStatus _, CancellationToken _, string category) =>
                {
                    Registered.Add((key, name, mime, size));
                    var file = new ProjectWorkspaceFile(Guid.NewGuid(), p, name, null, category, key, mime, size, DateTime.UtcNow, null);
                    Files.Add(file);
                    return file;
                });
            Workspace.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Files.ToList());
            Workspace.Setup(w => w.ArtifactExistsAsync(ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            Workspace.Setup(w => w.DownloadStorageKeyToFileAsync(ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, string key, string dest, long _, CancellationToken _) =>
                {
                    File.WriteAllText(dest, Stored[key]);
                    return Task.CompletedTask;
                });
        }

        public AudioGenerateStepExecutor Executor()
        {
            var resolver = new Mock<IInferenceProviderResolver>();
            resolver.Setup(r => r.ResolveMusicGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ResolveFailure is null ? Provider : throw ResolveFailure);
            resolver.Setup(r => r.ResolveSoundEffectGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Provider is null ? null : Provider with { Capability = InferenceProviderCapability.SoundEffectGeneration });

            var factory = new Mock<IAudioGenerationClientFactory>();
            factory.Setup(f => f.Get(It.IsAny<ResolvedAudioGenerationProvider>())).Returns(() => ClientOverride
                ?? new ElevenLabsAudioGenerationClient(new HttpClient(Api) { BaseAddress = new Uri("https://api.elevenlabs.io") }, AudioFixtures.Key));

            var usage = new Mock<IUsageRecorder>();
            usage.Setup(u => u.RecordAsync(It.IsAny<UsageRecordRequest>(), It.IsAny<CancellationToken>()))
                .Callback<UsageRecordRequest, CancellationToken>((r, _) => Usage.Add(r))
                .Returns(new ValueTask<long>(1L));

            return new AudioGenerateStepExecutor(
                new VideoGenerationBudgetGate(Db, Microsoft.Extensions.Options.Options.Create(Options)),
                resolver.Object, factory.Object, new AudioGenerationDurationProbe(Probe), Workspace.Object, Db,
                NullLogger<AudioGenerateStepExecutor>.Instance, usageRecorder: usage.Object, moderation: Moderation,
                changeRequests: Scope is null ? null : new FixedContext(Scope));
        }

        public Task<StepExecutionResult> RunAsync(string configJson, CancellationToken ct = default, int stepOrder = 2)
        {
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepOrder = stepOrder,
                StepType = StepType.AudioGenerate,
                AudioGenerateConfigJson = configJson
            };
            return Executor().ExecuteAsync(new StepExecutionContext
            {
                StepResultId = Guid.NewGuid(),
                Execution = Execution,
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                CurrentStepIndex = 1,
                IterationCount = 0,
                CorrelationId = "t",
                StepOutputHistory = [],
                CancellationToken = ct
            });
        }

        public List<ExternalGenerationJob> Rows()
        {
            using var db = Db.CreateDbContext();
            return [.. db.ExternalGenerationJobs.AsNoTracking().OrderBy(j => j.CreatedAt).ThenBy(j => j.ClipId)];
        }

        public int Posts => Api.Requests.Count(r => r.Method == HttpMethod.Post);
    }

    private const string Music30 = """{"kind":"Music","prompt":"Warm uplifting corporate pop, light percussion, no vocals","durationSec":30,"instrumental":true,"model":"music_v2_5"}""";

    private static HttpResponseMessage Song() =>
        AudioFixtures.Audio(AudioFixtures.Silence, "audio/mpeg", ("song-id", "song-123"));

    private static JsonObject Output(StepExecutionResult result) => JsonNode.Parse(result.Output)!.AsObject();

    // ================================================================ prompt record

    private static ReelBolt.Shared.Moderation.IContentModerationGate Gate(
        ReelBolt.Shared.Moderation.ModerationAction action, bool available)
    {
        var gate = new Mock<ReelBolt.Shared.Moderation.IContentModerationGate>();
        gate.Setup(g => g.ModerateAsync(It.IsAny<ReelBolt.Shared.Moderation.ModerationInput>(),
                It.IsAny<ReelBolt.Shared.Moderation.ModerationAttribution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReelBolt.Shared.Moderation.ModerationDecision(action, available, false, null, string.Empty, "h"));
        return gate.Object;
    }

    [Theory]
    [InlineData(ReelBolt.Shared.Moderation.ModerationAction.Allow, true, "Allow")]
    [InlineData(ReelBolt.Shared.Moderation.ModerationAction.Review, true, "Review")]
    [InlineData(ReelBolt.Shared.Moderation.ModerationAction.Allow, false, null)]
    public async Task The_manifest_keeps_the_verdict_the_prompt_was_given_and_nothing_after_an_outage(
        ReelBolt.Shared.Moderation.ModerationAction action, bool available, string? expected)
    {
        var rig = new Rig(_ => Song()) { Moderation = Gate(action, available) };

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        JsonObject take = JsonNode.Parse(rig.Stored[result.ArtifactStorageKey!])!["takes"]![0]!.AsObject();
        take["promptCheck"]?.GetValue<string>().Should().Be(expected);
        Output(result)["takes"]![0]!.AsObject().ContainsKey("promptCheck").Should().BeFalse("the step output is unchanged");
    }

    [Fact]
    public async Task With_no_gate_configured_the_manifest_records_no_prompt_check()
    {
        var rig = new Rig(_ => Song());

        StepExecutionResult result = await rig.RunAsync(Music30);

        JsonNode.Parse(rig.Stored[result.ArtifactStorageKey!])!["takes"]![0]!.AsObject().ContainsKey("promptCheck").Should().BeFalse();
    }

    // ================================================================ happy paths

    [Fact]
    public async Task A_music_take_is_generated_metered_published_and_registered_as_an_audio_project_file()
    {
        var rig = new Rig(_ => Song());

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(1);
        string exec = rig.Execution.Id.ToString("D");
        result.OutputStorageKey.Should().Be($"projects/{rig.ProjectId}/outputFiles/{exec}/step-2-a1.mp3");
        result.ArtifactStorageKey.Should().NotBeNullOrEmpty();
        rig.Uploaded.Should().Contain(u => u.FileName == $"{exec}/step-2-a1.mp3" && u.Category == "outputFiles" && u.Mime == "audio/mpeg");

        rig.Registered.Should().ContainSingle().Which.Should().Be(
            (result.OutputStorageKey!, $"music-{rig.Execution.Id:N}"[..14] + "-1.mp3", "audio/mpeg", (long)AudioFixtures.Silence.Length));

        ExternalGenerationJob row = rig.Rows().Should().ContainSingle().Which;
        row.Status.Should().Be(ExternalGenerationJobStatus.Succeeded);
        row.RemoteJobId.Should().Be("song-123");
        row.StorageKey.Should().Be(result.OutputStorageKey);
        row.ClipId.Should().Be("music-1");
        row.CostEstimate.Should().Be(CreditCurrency.CreditsFromUsd(0.15m), "30 s of Eleven Music is one started minute at $0.15");
        row.ActualCost.Should().Be(CreditCurrency.CreditsFromUsd(0.15m));

        UsageRecordRequest usage = rig.Usage.Should().ContainSingle().Which;
        usage.Kind.Should().Be(UsageKind.MusicGenMinutes);
        usage.Quantity.Should().Be(1m);
        usage.Capability.Should().Be(InferenceProviderCapability.MusicGeneration);
        usage.ModelName.Should().Be("music_v2_5");
        usage.IdempotencyKey.Should().Be($"audiogen:{row.Id:N}");
        usage.BillingMode.Should().Be(UsageBillingMode.Platform);

        JsonObject take = Output(result)["takes"]!.AsArray().Single()!.AsObject();
        take["status"]!.GetValue<string>().Should().Be("succeeded");
        take["durationSec"]!.GetValue<double>().Should().Be(30);
        take["projectFileId"].Should().NotBeNull();
        result.Output.Should().NotContain("projects/", "the step output never carries a storage key");
    }

    [Fact]
    public async Task Sound_effect_takes_each_get_a_row_a_file_and_a_suffix()
    {
        var rig = new Rig(_ => AudioFixtures.Audio(AudioFixtures.Silence)) { Probe = new FakeProbe(1.5) };

        StepExecutionResult result = await rig.RunAsync(
            """{"kind":"SoundEffect","prompt":"A short whoosh transition","durationSec":1.5,"takes":2,"fileNamePrefix":"whoosh"}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(2);
        rig.Api.Requests.Should().OnlyContain(r => r.RequestUri!.AbsolutePath == "/v1/sound-generation");
        rig.Rows().Select(r => (r.ClipId, r.Status)).Should().BeEquivalentTo(
            [("sfx-1", ExternalGenerationJobStatus.Succeeded), ("sfx-2", ExternalGenerationJobStatus.Succeeded)]);
        rig.Registered.Select(r => r.Name).Should().Equal(
            $"whoosh-{rig.Execution.Id.ToString("N")[..8]}-1.mp3", $"whoosh-{rig.Execution.Id.ToString("N")[..8]}-2.mp3");
        result.OutputStorageKey.Should().EndWith("/step-2-a1.mp3", "the first take is the step's output");
        rig.Usage.Should().HaveCount(2).And.OnlyContain(u => u.Kind == UsageKind.SoundEffectGenSeconds && u.Quantity == 2m);
    }

    // ================================================================ refusals before any money moves

    [Fact]
    public async Task A_budget_refusal_submits_nothing_and_writes_no_row()
    {
        var rig = new Rig(_ => Song());

        StepExecutionResult result = await rig.RunAsync(
            """{"kind":"Music","prompt":"x","durationSec":30,"model":"music_v2_5","takes":4,"maxSpendCredits":30}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("BUDGET_EXCEEDED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.ErrorDetails.Should().Contain("credits");
        rig.Posts.Should().Be(0);
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task The_video_daily_caps_never_limit_audio()
    {
        // Product decision 6: no daily cap on audio; a 1-credit video budget (project and platform) does not refuse it.
        var rig = new Rig(_ => Song());
        rig.Options.DailyBudgetCredits = 1m;
        rig.Options.GlobalDailyBudgetCredits = 1m;

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(1);
        rig.Rows().Should().ContainSingle().Which.ClipId.Should().Be("music-1");
    }

    [Fact]
    public async Task A_model_the_provider_cannot_price_fails_closed_with_no_row()
    {
        var rig = new Rig(_ => Song());

        // music_v2_5 is catalogued, but for ElevenLabs only: on a fal row it cannot be priced.
        rig.Provider = rig.Provider! with { Kind = InferenceProviderKind.FalAi };
        var client = new Mock<IAudioGenerationClient>(MockBehavior.Strict);
        client.Setup(c => c.Describe("music_v2_5")).Throws(new NotSupportedException("'music_v2_5' is not a fal.ai audio endpoint"));
        rig.ClientOverride = client.Object;

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.FailureCode.Should().Be("CONFIG_INVALID");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_non_positive_estimate_fails_closed_with_no_row_and_no_submit()
    {
        var rig = new Rig(_ => Song());
        var client = new Mock<IAudioGenerationClient>(MockBehavior.Strict);
        client.Setup(c => c.Describe("music_v2_5")).Returns(new AudioGenerationCapabilities(
            "music_v2_5", AudioGenerationKind.Music, UsageKind.MusicGenMinutes, 0m, AudioBillingRounding.UpToWholeMinute,
            3, 300, "free?", false));
        rig.ClientOverride = client.Object;

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.FailureCode.Should().Be("CONFIG_INVALID");
        result.ErrorDetails.Should().Contain("non-positive");
        rig.Rows().Should().BeEmpty();
        client.Verify(c => c.GenerateAsync(It.IsAny<AudioGenerationRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>>()), Times.Never);
    }

    [Fact]
    public async Task No_provider_row_is_NO_AUDIO_PROVIDER_and_the_authors_to_fix()
    {
        var rig = new Rig(_ => Song()) { Provider = null };

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.FailureCode.Should().Be("NO_AUDIO_PROVIDER");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_duration_outside_the_chosen_models_range_is_refused_before_the_gate()
    {
        var rig = new Rig(_ => Song());

        // 25 s is a valid sound effect for ElevenLabs direct (<= 30) but not for fal's SFX v2 (<= 22).
        rig.Provider = rig.Provider! with { Kind = InferenceProviderKind.FalAi };
        var client = new Mock<IAudioGenerationClient>(MockBehavior.Strict);
        client.Setup(c => c.Describe("fal-ai/elevenlabs/sound-effects/v2")).Returns(
            AudioModelCatalog.Instance.Find(InferenceProviderKind.FalAi, "fal-ai/elevenlabs/sound-effects/v2")!.Capabilities);
        rig.ClientOverride = client.Object;

        StepExecutionResult result = await rig.RunAsync("""{"kind":"SoundEffect","prompt":"rain","durationSec":25}""");

        result.FailureCode.Should().Be("CONFIG_INVALID");
        result.ErrorDetails.Should().Contain("22");
        rig.Rows().Should().BeEmpty();
    }

    // ================================================================ vendor answers

    [Fact]
    public async Task A_bad_prompt_is_the_authors_with_the_vendors_suggestion_and_releases_the_row()
    {
        var rig = new Rig(_ => AudioFixtures.Json(AudioFixtures.Text("elevenlabs-bad-prompt.json"), HttpStatusCode.UnprocessableEntity));

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("AUDIO_BAD_REQUEST");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.ErrorDetails.Should().Contain("An epic rock ballad with dramatic tempo changes");
        rig.Rows().Should().ContainSingle().Which.Status.Should().Be(ExternalGenerationJobStatus.Failed, "a refused request bought nothing");
        rig.Usage.Should().BeEmpty();
        rig.Registered.Should().BeEmpty();
    }

    [Fact]
    public async Task A_429_releases_the_row_and_the_step_retry_submits_a_fresh_one()
    {
        int calls = 0;
        var rig = new Rig(_ => ++calls == 1
            ? AudioFixtures.Json(AudioFixtures.Text("elevenlabs-concurrent-limit-429.json"), HttpStatusCode.TooManyRequests)
            : Song());

        StepExecutionResult first = await rig.RunAsync(Music30);
        first.FailureCode.Should().Be("AUDIO_VENDOR_RETRYABLE");
        first.FailureClass.Should().Be(FailureClass.PlatformProviderError);

        StepExecutionResult second = await rig.RunAsync(Music30);

        second.Status.Should().Be(StepStatus.Completed, second.ErrorDetails);
        rig.Posts.Should().Be(2);
        rig.Rows().Select(r => r.Status).Should().Equal(ExternalGenerationJobStatus.Failed, ExternalGenerationJobStatus.Succeeded);
    }

    /// <summary>QA-P1-17: a platform audio model with no price is its own failure (code + class), never folded into
    /// AUDIO_GENERATE_FAILED.</summary>
    [Fact]
    public async Task An_unpriced_platform_audio_model_surfaces_MODEL_UNPRICED_not_the_generic_failure()
    {
        var rig = new Rig(_ => Song())
        {
            ResolveFailure = new ModelUnpricedException(InferenceProviderCapability.MusicGeneration, "music_v2_5")
        };

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.FailureCode.Should().Be("MODEL_UNPRICED");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal, "a catalog gap is ours, so the step is refunded");
        rig.Rows().Should().BeEmpty();
        rig.Posts.Should().Be(0);
    }

    /// <summary>QA-P1-17: a submit/poll client reports the vendor's request id before the result download, and the
    /// engine writes it on the ledger row then, so a crash in that window still leaves it recorded.</summary>
    [Fact]
    public async Task The_vendor_request_id_is_recorded_on_submit_so_a_crash_before_the_download_keeps_it()
    {
        var rig = new Rig(_ => Song()) { ClientOverride = new SubmitThenCrashClient() };

        StepExecutionResult result = await rig.RunAsync(Music30);

        Output(result)["takes"]![0]!["failureReason"]!.GetValue<string>().Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        ExternalGenerationJob row = rig.Rows().Should().ContainSingle().Which;
        row.RemoteJobId.Should().Be(SubmitThenCrashClient.RequestId, "the id was persisted the moment the submit answered");
        row.Status.Should().Be(ExternalGenerationJobStatus.Submitted, "the take is charged; its outcome is unknown");
    }

    [Fact]
    public async Task An_unknown_outcome_keeps_the_row_charged_and_the_retry_never_resubmits_it()
    {
        var rig = new Rig(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        StepExecutionResult first = await rig.RunAsync(Music30);
        first.FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        rig.Rows().Should().ContainSingle().Which.Status.Should().Be(ExternalGenerationJobStatus.Reserved);

        StepExecutionResult second = await rig.RunAsync(Music30);

        second.FailureCode.Should().Be("RESERVATION_ORPHANED");
        rig.Posts.Should().Be(1, "a Reserved row without a vendor id is never re-submitted");
        ExternalGenerationJob row = rig.Rows().Should().ContainSingle().Which;
        row.Status.Should().Be(ExternalGenerationJobStatus.Reserved, "it stays charged until the day rolls over");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task S58_a_5xx_or_408_after_send_keeps_the_row_charged_and_the_retry_never_resubmits(HttpStatusCode status)
    {
        var rig = new Rig(_ => AudioFixtures.Json("{\"detail\":\"upstream unavailable\"}", status));

        StepExecutionResult first = await rig.RunAsync(Music30);
        first.FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        rig.Rows().Should().ContainSingle().Which.Status.Should().Be(ExternalGenerationJobStatus.Reserved);

        StepExecutionResult second = await rig.RunAsync(Music30);

        second.FailureCode.Should().Be("RESERVATION_ORPHANED");
        rig.Posts.Should().Be(1, "the vendor may have generated and billed the first submit");
    }

    [Fact]
    public async Task S58_a_connection_dropped_after_the_request_was_sent_is_never_resubmitted()
    {
        var rig = new Rig(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."));

        StepExecutionResult first = await rig.RunAsync(Music30);
        first.FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        rig.Rows().Should().ContainSingle().Which.Status.Should().Be(ExternalGenerationJobStatus.Reserved);

        StepExecutionResult second = await rig.RunAsync(Music30);

        second.FailureCode.Should().Be("RESERVATION_ORPHANED");
        rig.Posts.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    public async Task S58_a_definite_pre_send_failure_releases_the_row_and_the_retry_submits_again(HttpRequestError error)
    {
        int calls = 0;
        var rig = new Rig(_ => ++calls == 1 ? throw new HttpRequestException(error, "could not connect") : Song());

        StepExecutionResult first = await rig.RunAsync(Music30);
        first.FailureCode.Should().Be("AUDIO_VENDOR_RETRYABLE");

        StepExecutionResult second = await rig.RunAsync(Music30);

        second.Status.Should().Be(StepStatus.Completed, second.ErrorDetails);
        rig.Rows().Select(r => r.Status).Should().Equal(ExternalGenerationJobStatus.Failed, ExternalGenerationJobStatus.Succeeded);
    }

    [Fact]
    public async Task A_vendor_failure_on_the_organizations_own_row_is_a_byo_provider_error()
    {
        var rig = new Rig(_ => AudioFixtures.Json("""{"detail":"upstream exploded"}""", HttpStatusCode.BadGateway),
            providerOrganizationId: Guid.NewGuid());

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN", "a 502 after send may have been billed (S-58)");
        result.FailureClass.Should().Be(FailureClass.ByoProviderError);
    }

    [Fact]
    public async Task A_step_stopped_before_the_gate_reserves_nothing()
    {
        var rig = new Rig(_ => Song());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => rig.RunAsync(Music30, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Posts.Should().Be(0);
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_stop_during_a_take_delivers_the_billed_take_and_releases_the_unsubmitted_one()
    {
        var rig = new Rig(_ => Song());
        using var cts = new CancellationTokenSource();
        var client = new Mock<IAudioGenerationClient>(MockBehavior.Strict);
        client.Setup(c => c.Describe("music_v2_5")).Returns(
            AudioModelCatalog.Instance.Find(InferenceProviderKind.ElevenLabs, "music_v2_5")!.Capabilities);
        client.Setup(c => c.GenerateAsync(It.IsAny<AudioGenerationRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(new AudioGenerationResult(AudioFixtures.Silence, "audio/mpeg", ".mp3", null, "song-1"));
        rig.ClientOverride = client.Object;

        StepExecutionResult result = await rig.RunAsync(
            """{"kind":"Music","prompt":"x","durationSec":30,"model":"music_v2_5","takes":2}""", cts.Token);

        result.Status.Should().Be(StepStatus.Completed);
        rig.Rows().Select(r => (r.ClipId, r.Status)).Should().BeEquivalentTo(
            [("music-1", ExternalGenerationJobStatus.Succeeded), ("music-2", ExternalGenerationJobStatus.Cancelled)]);
        Output(result)["takes"]![1]!["failureReason"]!.GetValue<string>().Should().Be("NOT_SUBMITTED");
        client.Verify(c => c.GenerateAsync(It.IsAny<AudioGenerationRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>>()), Times.Once);
    }

    [Fact]
    public async Task A_failed_upload_keeps_the_ledger_row_charged_but_charges_the_tenant_nothing()
    {
        var rig = new Rig(_ => Song());
        rig.Workspace.Setup(w => w.UploadArtifactAsync(rig.ProjectId, It.IsAny<string>(), It.Is<string>(n => n.EndsWith(".mp3")),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ThrowsAsync(new IOException("bucket unavailable"));

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.FailureCode.Should().Be("DELIVERY_FAILED");
        ExternalGenerationJob row = rig.Rows().Should().ContainSingle().Which;
        row.Status.Should().Be(ExternalGenerationJobStatus.Submitted);
        row.ActualCost.Should().BePositive("the vendor billed the platform, so the spend-side ledger row stays charged");
        rig.Usage.Should().BeEmpty("S-60: the tenant is never charged credits for a take it did not receive");
    }

    [Fact]
    public async Task S60_a_lost_take_is_not_charged_when_another_take_is_delivered()
    {
        var rig = new Rig(_ => AudioFixtures.Audio(AudioFixtures.Silence)) { Probe = new FakeProbe(1.5) };
        rig.Workspace.Setup(w => w.UploadArtifactAsync(rig.ProjectId, It.IsAny<string>(), It.Is<string>(n => n.EndsWith("-a1.mp3")),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ThrowsAsync(new IOException("bucket unavailable"));

        StepExecutionResult result = await rig.RunAsync(
            """{"kind":"SoundEffect","prompt":"A short whoosh transition","durationSec":1.5,"takes":2}""");

        result.Status.Should().Be(StepStatus.Completed, "the second take was delivered");
        rig.Usage.Should().ContainSingle("only the delivered take is charged to the tenant")
            .Which.IdempotencyKey.Should().Be($"audiogen:{rig.Rows().Single(r => r.ClipId == "sfx-2").Id:N}");
        rig.Rows().Single(r => r.ClipId == "sfx-1").Status.Should().Be(ExternalGenerationJobStatus.Submitted);
    }

    [Fact]
    public async Task S59_a_storage_failure_writes_a_fixed_sentence_not_the_exception_text()
    {
        var rig = new Rig(_ => Song());
        rig.Workspace.Setup(w => w.UploadArtifactAsync(rig.ProjectId, It.IsAny<string>(), It.Is<string>(n => n.EndsWith(".mp3")),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ThrowsAsync(new IOException("PUT https://internal-bucket.s3.local/projects/secret-key failed: AccessDenied"));

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.FailureCode.Should().Be("DELIVERY_FAILED");
        (result.ErrorDetails ?? string.Empty).Should().NotContain("internal-bucket").And.NotContain("AccessDenied");
        (result.Output ?? string.Empty).Should().NotContain("internal-bucket").And.NotContain("AccessDenied");
    }

    [Fact]
    public async Task S59_an_unknown_transport_outcome_writes_a_fixed_sentence_not_the_exception_text()
    {
        var rig = new Rig(_ => throw new TaskCanceledException("socket 10.9.8.7:443 reset by peer"));

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        (result.ErrorDetails ?? string.Empty).Should().NotContain("10.9.8.7");
        (result.Output ?? string.Empty).Should().NotContain("10.9.8.7");
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, """{"detail":{"status":"quota_exceeded","message":"This request exceeds your quota of 500000. You have 1234 credits remaining."}}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"detail":{"status":"invalid_api_key","message":"Invalid API key for workspace platform-prod-7"}}""")]
    public async Task S53_a_platform_rows_account_refusal_reaches_the_tenant_as_a_fixed_sentence(HttpStatusCode status, string body)
    {
        var rig = new Rig(_ => AudioFixtures.Json(body, status));

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.Status.Should().Be(StepStatus.Failed);
        foreach (string leak in new[] { "1234 credits", "500000", "platform-prod-7", "quota_exceeded", "invalid_api_key" })
        {
            (result.ErrorDetails ?? string.Empty).Should().NotContain(leak);
            (result.Output ?? string.Empty).Should().NotContain(leak);
        }

        result.ErrorDetails.Should().Contain(AudioGenerateStepExecutor.PlatformAccountSentence);
        result.FailureClass.Should().Be(FailureClass.PlatformInternal, "the platform's account problem is refunded, never the tenant's");
        rig.Rows().Should().ContainSingle().Which.Status.Should().Be(ExternalGenerationJobStatus.Failed);
    }

    [Fact]
    public async Task S53_an_organizations_own_row_keeps_its_vendor_detail()
    {
        var rig = new Rig(_ => AudioFixtures.Json(
            """{"detail":{"status":"quota_exceeded","message":"You have 1234 credits remaining."}}""", HttpStatusCode.PaymentRequired),
            providerOrganizationId: Guid.NewGuid());

        StepExecutionResult result = await rig.RunAsync(Music30);

        result.ErrorDetails.Should().Contain("1234 credits", "it is the organization's own vendor account");
        result.FailureClass.Should().Be(FailureClass.ByoProviderError);
    }

    // ================================================================ reuse

    [Fact]
    public async Task An_identical_request_in_a_later_run_is_reused_from_the_ledger_without_paying_or_registering_twice()
    {
        var rig = new Rig(_ => Song());
        StepExecutionResult first = await rig.RunAsync(Music30);
        first.Status.Should().Be(StepStatus.Completed);

        rig.Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = rig.ProjectId, Status = ExecutionStatus.Running };

        StepExecutionResult second = await rig.RunAsync(Music30);

        second.Status.Should().Be(StepStatus.Completed, second.ErrorDetails);
        rig.Posts.Should().Be(1);
        rig.Rows().Should().ContainSingle();
        rig.Registered.Should().ContainSingle("the reused take is already a project file");
        second.OutputStorageKey.Should().Be(first.OutputStorageKey);
        Output(second)["takes"]![0]!["reused"]!.GetValue<bool>().Should().BeTrue();
        Output(second)["takes"]![0]!["durationSec"]!.GetValue<double>().Should().Be(30d, "a reused take still reports its length, so a later change request can price exactly");
    }

    // ------------------------------------------------------------------ change requests (docs/design/change-requests.md)

    private const string Music3 = """{"kind":"Music","prompt":"Warm uplifting corporate pop, light percussion, no vocals","durationSec":30,"instrumental":true,"model":"music_v2_5","takes":3}""";

    private static async Task<(Rig Rig, StepExecutionResult First)> ThreeTakesAsync()
    {
        var rig = new Rig(_ => Song());
        StepExecutionResult first = await rig.RunAsync(Music3);
        first.Status.Should().Be(StepStatus.Completed, first.ErrorDetails);
        rig.Posts.Should().Be(3);
        return (rig, first);
    }

    /// <summary>The next run of the same project: a new execution carrying <paramref name="payloadJson"/> against <paramref name="first"/>.</summary>
    private static void NextRun(Rig rig, StepExecutionResult first, string payloadJson, string? artifactKey = null)
    {
        rig.Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = rig.ProjectId, Status = ExecutionStatus.Running };
        rig.Scope = new StepChangeRequestScope(Guid.NewGuid(), ChangeRequestPayload.TryParse(payloadJson)!, first.Output, artifactKey ?? first.ArtifactStorageKey);
        rig.Api.Requests.Clear();
    }

    [Fact]
    public async Task Changing_one_take_makes_exactly_one_new_take_and_carries_the_others_verbatim()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        List<ExternalGenerationJob> before = rig.Rows();
        JsonObject firstManifest = JsonNode.Parse(rig.Stored[first.ArtifactStorageKey!])!.AsObject();
        NextRun(rig, first, """{"itemIds":["a2"],"overrides":{"a2":{"prompt":"Slow ambient piano, no vocals"}}}""");

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(1, "only the named take is submitted");
        List<ExternalGenerationJob> after = rig.Rows();
        after.Should().HaveCount(before.Count + 1, "one new ledger row, none for the carried takes");
        ExternalGenerationJob added = after.Single(r => before.All(b => b.Id != r.Id));
        added.ClipId.Should().Be("music-2");
        added.ExecutionId.Should().Be(rig.Execution.Id);

        JsonArray takes = JsonNode.Parse(result.Output!)!["takes"]!.AsArray();
        takes.Select(t => t!["take"]!.GetValue<int>()).Should().Equal(1, 2, 3);
        takes[0]!["carried"]!.GetValue<bool>().Should().BeTrue();
        takes[2]!["carried"]!.GetValue<bool>().Should().BeTrue();
        takes[1]!["prompt"]!.GetValue<string>().Should().Be("Slow ambient piano, no vocals");
        takes[1]!["carried"].Should().BeNull();
        JsonNode.Parse(result.Output!)!["meta"]!["changeRequest"]!["redoneTakeIds"]!.AsArray()
            .Select(n => n!.GetValue<string>()).Should().Equal("a2");

        JsonObject manifest = JsonNode.Parse(rig.Stored[result.ArtifactStorageKey!])!.AsObject();
        JsonArray mt = manifest["takes"]!.AsArray();
        mt[0]!["storageKey"]!.GetValue<string>().Should().Be(firstManifest["takes"]![0]!["storageKey"]!.GetValue<string>());
        mt[0]!["ledgerRowId"]!.GetValue<string>().Should().Be(firstManifest["takes"]![0]!["ledgerRowId"]!.GetValue<string>());
        mt[2]!["storageKey"]!.GetValue<string>().Should().Be(firstManifest["takes"]![2]!["storageKey"]!.GetValue<string>());
        mt[1]!["storageKey"]!.GetValue<string>().Should().Be($"projects/{rig.ProjectId}/outputFiles/{rig.Execution.Id:D}/step-2-a2.mp3");
        mt[1]!["ledgerRowId"]!.GetValue<string>().Should().Be(added.Id.ToString());
        mt[1]!["storageKey"]!.GetValue<string>().Should().NotBe(firstManifest["takes"]![1]!["storageKey"]!.GetValue<string>(), "the old object is untouched");
    }

    [Fact]
    public async Task A_request_with_no_item_ids_remakes_the_whole_step()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        int before = rig.Rows().Count;
        NextRun(rig, first, """{"overrides":{"*":{"durationSec":20}}}""");

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(3);
        rig.Rows().Should().HaveCount(before + 3);
        JsonNode.Parse(result.Output!)!["takes"]!.AsArray().Should().OnlyContain(t => t!["carried"] == null);
    }

    [Fact]
    public async Task An_unchanged_prompt_and_duration_on_a_named_take_is_the_content_addressed_reuse_and_free()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        int before = rig.Rows().Count;
        NextRun(rig, first, """{"itemIds":["a2"],"overrides":{"a2":{"prompt":"Warm uplifting corporate pop, light percussion, no vocals","durationSec":30}}}""");

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(0);
        rig.Rows().Should().HaveCount(before, "no new ledger row");
        JsonNode.Parse(result.Output!)!["takes"]![1]!["reused"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Retake_on_a_named_take_with_nothing_else_changed_buys_one_fresh_take()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        List<ExternalGenerationJob> before = rig.Rows();
        NextRun(rig, first, """{"itemIds":["a2"],"overrides":{"a2":{"retake":true}}}""");

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(1);
        List<ExternalGenerationJob> after = rig.Rows();
        after.Should().HaveCount(before.Count + 1);
        after.Single(r => before.All(b => b.Id != r.Id)).ClipId.Should().Be("music-2");
        JsonArray takes = JsonNode.Parse(result.Output!)!["takes"]!.AsArray();
        takes[0]!["carried"]!.GetValue<bool>().Should().BeTrue();
        takes[2]!["carried"]!.GetValue<bool>().Should().BeTrue();
        takes[1]!["reused"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Retake_false_keeps_the_free_reuse()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        int before = rig.Rows().Count;
        NextRun(rig, first, """{"itemIds":["a2"],"overrides":{"a2":{"retake":false}}}""");

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(0);
        rig.Rows().Should().HaveCount(before);
    }

    [Fact]
    public async Task Star_with_retake_remakes_every_take()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        int before = rig.Rows().Count;
        NextRun(rig, first, """{"overrides":{"*":{"retake":true}}}""");

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(3);
        rig.Rows().Should().HaveCount(before + 3);
    }

    [Fact]
    public async Task The_cap_counts_only_the_retaken_take()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        double oneTake = (double)CreditCurrency.CreditsFromUsd(0.15m);
        NextRun(rig, first, """{"itemIds":["a1"],"overrides":{"a1":{"retake":true}}}""");

        StepExecutionResult result = await rig.RunAsync(Music3.Replace("\"takes\":3", $"\"takes\":3,\"maxSpendCredits\":{oneTake.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(1);
    }

    [Fact]
    public async Task A_retake_only_request_passes_the_validator()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        ChangeRequestValidator.Validate(StepType.AudioGenerate, ChangeRequestPayload.TryParse("""{"itemIds":["a1"],"overrides":{"a1":{"retake":true}}}"""), first.Output)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task The_budget_cap_counts_only_the_takes_that_are_actually_bought()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        double oneTake = (double)CreditCurrency.CreditsFromUsd(0.15m);
        NextRun(rig, first, """{"itemIds":["a3"],"overrides":{"a3":{"prompt":"Tense strings, no vocals"}}}""");

        StepExecutionResult result = await rig.RunAsync(Music3.Replace("\"takes\":3", $"\"takes\":3,\"maxSpendCredits\":{oneTake.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Posts.Should().Be(1);
    }

    [Fact]
    public async Task A_malformed_stored_request_is_CHANGE_NOT_APPLIED_and_buys_nothing()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        int before = rig.Rows().Count;
        NextRun(rig, first, """{"itemIds":["a3"]}""");
        rig.Scope = rig.Scope! with { Malformed = true };

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Posts.Should().Be(0);
        rig.Rows().Should().HaveCount(before);
    }

    [Fact]
    public async Task A_request_naming_no_take_of_the_step_is_CHANGE_NOT_APPLIED_and_buys_nothing()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        int before = rig.Rows().Count;
        NextRun(rig, first, """{"itemIds":["a9"]}""");

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Posts.Should().Be(0);
        rig.Rows().Should().HaveCount(before);
    }

    [Fact]
    public async Task A_prompt_override_with_control_characters_is_refused_before_anything_is_sent()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        int before = rig.Rows().Count;
        string json = """{"itemIds":["a1"],"overrides":{"a1":{"prompt":"bad\u0007prompt"}}}""";
        ChangeRequestValidator.Validate(StepType.AudioGenerate, ChangeRequestPayload.TryParse(json), first.Output)
            .Should().Contain(v => v.Path == "overrides.a1.prompt");
        NextRun(rig, first, json);

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.FailureCode.Should().Be("CONFIG_INVALID");
        rig.Posts.Should().Be(0);
        rig.Rows().Should().HaveCount(before);
    }

    [Fact]
    public async Task A_manifest_key_outside_the_project_is_refused_and_nothing_is_sent()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        NextRun(rig, first, """{"itemIds":["a2"],"overrides":{"a2":{"prompt":"Other"}}}""",
            artifactKey: $"projects/{Guid.NewGuid()}/agentFiles/audio-generate/x/step-2-takes.json");

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
        rig.Posts.Should().Be(0);
    }

    [Fact]
    public async Task A_carried_take_whose_manifest_key_points_outside_the_project_outputs_is_refused()
    {
        (Rig rig, StepExecutionResult first) = await ThreeTakesAsync();
        JsonObject manifest = JsonNode.Parse(rig.Stored[first.ArtifactStorageKey!])!.AsObject();
        manifest["takes"]![0]!["storageKey"] = $"projects/{Guid.NewGuid()}/outputFiles/x/step-2-a1.mp3";
        rig.Stored[first.ArtifactStorageKey!] = manifest.ToJsonString();
        NextRun(rig, first, """{"itemIds":["a2"],"overrides":{"a2":{"prompt":"Other"}}}""");

        StepExecutionResult result = await rig.RunAsync(Music3);

        result.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
        rig.Posts.Should().Be(0);
    }

    [Fact]
    public void Audio_items_are_the_takes_a_k_and_the_registry_offers_prompt_duration_and_retake()
    {
        const string output = """{"takes":[{"take":1,"status":"succeeded","durationSec":30,"prompt":"Warm pop"},{"take":2,"status":"failed"},{"nope":1}]}""";
        StepChangeCapabilities.ListItems(StepType.AudioGenerate, output).Should().Equal(
            new StepItem("a1", "succeeded", 30, "Warm pop"), new StepItem("a2", "failed", null, null));
        foreach (string? garbage in new string?[] { null, "", "nope", "[]", """{"takes":3}""" })
            StepChangeCapabilities.ListItems(StepType.AudioGenerate, garbage).Should().BeEmpty();
        StepChangeCapabilities.For(StepType.AudioGenerate).Overrides.Select(o => o.Key).Should().Equal("prompt", "durationSec", "retake");
        ChangeRequestValidator.Validate(StepType.AudioGenerate, ChangeRequestPayload.TryParse("""{"overrides":{"a1":{"takes":2}}}"""), output)
            .Should().Contain(v => v.Path == "overrides.a1.takes");
        ChangeRequestValidator.Validate(StepType.AudioGenerate, ChangeRequestPayload.TryParse("""{"overrides":{"a1":{"durationSec":99999}}}"""), output)
            .Should().Contain(v => v.Path == "overrides.a1.durationSec");
        ChangeRequestValidator.Validate(StepType.AudioGenerate, ChangeRequestPayload.TryParse("""{"overrides":{"a1":{"prompt":"ok","durationSec":20}}}"""), output)
            .Should().BeEmpty();
        PlanGate.FeatureFor(StepType.AudioGenerate).Should().Be("audio_generation", "a change run is the same step type, so the engine's plan gate still applies");
    }
}
