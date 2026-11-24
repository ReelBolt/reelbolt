using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Tests.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Avatars;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using ReelBolt.WorkflowEngine.Services.Voiceover;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Avatars phase 3, lane C2: the still-then-animate path in the <c>VideoGenerate</c> executor, the two services it uses
/// (the speech track and the still), the stills' storage layout, and the Voiceover step that speaks in the avatar's
/// linked voice. Fakes only: no vendor, no ffmpeg, no storage.
/// </summary>
public class VideoGenerateStillPathTests
{
    private const string H3LipSync = "minimax/h3-max/lip-sync/image-to-video";
    private const string KlingAvatar = "fal-ai/kling-video/ai-avatar/v2/standard";
    private static readonly byte[] FacePng = Png(1024, 1024);
    private static readonly byte[] SideJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 1, 2, 3];
    private static readonly byte[] SampleWav = [9, 9, 9, 9];
    private static readonly byte[] StillPng = Png(768, 1376);
    private static readonly FalAiVideoGenerationClient RealFal = new(new System.Net.Http.HttpClient(), "key");

    private sealed class FakeAvatars : IAvatarResolver
    {
        public ResolvedAvatar Avatar { get; set; } = null!;

        public Task<ResolvedAvatar> ResolveAsync(Guid projectId, Guid avatarId, CancellationToken ct) =>
            Task.FromResult(avatarId == Avatar.Id ? Avatar : ResolvedAvatar.NotFound(avatarId));

        public async Task<ResolvedAvatar> ResolveRequiredAsync(Guid projectId, Guid avatarId, CancellationToken ct)
        {
            ResolvedAvatar r = await ResolveAsync(projectId, avatarId, ct);
            return r.Usable ? r : throw new AvatarUnusableException(avatarId, r.Reason!);
        }

        public Task<IReadOnlyList<ResolvedAvatar>> ListSelectableAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ResolvedAvatar>>([Avatar]);

        public void Revoke() => Avatar = Avatar with { Usable = false, Reason = ProjectAvatar.ReasonConsentRevoked, References = [] };
    }

    private sealed class FakeSpeech(AvatarSpeechTrackOutcome outcome) : IAvatarSpeechTrackSource
    {
        public int Calls { get; private set; }
        public AvatarSpeechTrackOutcome Outcome { get; set; } = outcome;

        public Task<AvatarSpeechTrackOutcome> ReadAsync(StepExecutionContext context, VideoGenerateStepConfig config, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeStills(Func<AvatarStillRequest, AvatarStillOutcome> make) : IAvatarStillMaker
    {
        public List<AvatarStillRequest> Requests { get; } = [];
        public Action? OnMake { get; set; }

        public Task<AvatarStillOutcome> MakeAsync(AvatarStillRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            OnMake?.Invoke();
            return Task.FromResult(make(request));
        }
    }

    private sealed record Harness(
        VideoGenerateStepExecutor Executor, StepExecutionContext Context, SharedInMemoryDbFactory Factory, FakeAvatars Avatars,
        Mock<IVideoGenerationBudgetGate> Gate, List<VideoGenerationRequest> Sent, FakeSpeech Speech, FakeStills Stills,
        Mock<IProjectFileWorkspace> Workspace, Mock<IInferenceProviderResolver> Resolver);

    private static AvatarSpeechTrackOutcome SpeechOf(int ms) =>
        new(new AvatarSpeechTrack(SampleWav, "audio/wav", ms, "speechsha", 2));

    private static AvatarStillOutcome MadeStill(AvatarStillRequest r) =>
        new(new AvatarStill(StillPng, "image/png", "stillsha", "google/nano-banana-2.1/edit", Reused: false,
            AvatarStillStorage.ObjectPrefix(r.ProjectId, r.AvatarId) + "digest.img"));

    private static Harness Build(
        string model = H3LipSync,
        string resolution = "768P",
        int duration = 15,
        bool withAudio = true,
        AvatarSpeechTrackOutcome? speech = null,
        Func<AvatarStillRequest, AvatarStillOutcome>? still = null,
        string extra = "",
        bool withAvatar = true,
        bool miniMaxVideoDefault = false,
        bool noLipSyncRow = false)
    {
        var factory = new SharedInMemoryDbFactory();
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Status = ExecutionStatus.Running,
            InitiatedByUserId = Guid.NewGuid()
        };
        Guid project = execution.ProjectId;
        var avatars = new FakeAvatars
        {
            Avatar = new ResolvedAvatar(Guid.NewGuid(), "Anna presenter", true, null,
            [
                Ref(project, AvatarReferenceKind.Image, AvatarReferenceRole.Face, 0, FacePng, "anna-face.png", "image/png"),
                Ref(project, AvatarReferenceKind.Audio, AvatarReferenceRole.Speech, 1, SampleWav, "anna.wav", "audio/wav", 4_000),
                Ref(project, AvatarReferenceKind.Image, AvatarReferenceRole.HalfBody, 2, SideJpeg, "anna-half.jpg", "image/jpeg"),
                Ref(project, AvatarReferenceKind.Image, AvatarReferenceRole.FullBody, 3, FacePng, "anna-full.png", "image/png"),
                Ref(project, AvatarReferenceKind.Image, AvatarReferenceRole.Face, 4, FacePng, "anna-face2.png", "image/png"),
            ], Guid.NewGuid(), AvatarDisclosure.Visible, DateTime.UtcNow.AddDays(-1)),
        };

        var client = new Mock<IVideoGenerationClient>();
        client.Setup(c => c.Describe(It.IsAny<string>())).Returns((string m) => RealFal.Describe(m));
        client.As<IVideoReferenceInputSupport>().Setup(c => c.ReferenceInputSupport(It.IsAny<string>()))
            .Returns((string m) => RealFal.ReferenceInputSupport(m));
        client.As<IVideoReferenceInputSupport>()
            .Setup(c => c.InputMaterialCostUsd(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<VideoReferenceMaterial>>()))
            .Returns(0m);
        var sent = new List<VideoGenerationRequest>();
        client.Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Callback((VideoGenerationRequest r, CancellationToken _) => sent.Add(r))
            .ReturnsAsync(new ExternalJobHandle("remote-1"));
        client.Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobStatus(ExternalJobState.Failed, null, "fake vendor"));

        var gate = new Mock<IVideoGenerationBudgetGate>();
        var real = VideoGenerateStepExecutorTests.RealGate(factory);
        gate.Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .Returns((VideoGenerationReservationRequest r, CancellationToken ct) => real.ReserveAsync(r, ct));

        var bytesByKey = avatars.Avatar.References.ToDictionary(r => r.StorageKey, r => r.Kind == AvatarReferenceKind.Audio
            ? SampleWav : r.StorageKey.EndsWith(".jpg", StringComparison.Ordinal) ? SideJpeg : FacePng);
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ListFilesAsync(project, It.IsAny<CancellationToken>()))
            .ReturnsAsync(avatars.Avatar.References.Select(r => new ProjectWorkspaceFile(
                r.ProjectFileId, project, "f", null, "userFiles", r.StorageKey, r.MimeType, bytesByKey[r.StorageKey].Length,
                DateTime.UtcNow, null)).ToList());
        workspace.Setup(w => w.DownloadStorageKeyToFileAsync(project, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string key, string path, long _, CancellationToken _) => File.WriteAllBytesAsync(path, bytesByKey[key]));
        workspace.Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), "agentFiles"))
            .ReturnsAsync("stored/manifest.json");

        var provider = new ResolvedVideoGenerationProvider(
            Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, "https://queue.fal.run", model, "key123", 1800);
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        // A talking head (avatar audio) resolves the LipSync capability, never VideoGeneration.
        resolver.Setup(r => r.ResolveLipSyncAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(noLipSyncRow ? null : provider);
        if (miniMaxVideoDefault)
        {
            // The production shape: the default VideoGeneration row is MiniMax, which would refuse a lip-sync model.
            var miniMax = new ResolvedVideoGenerationProvider(
                Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key", 1800);
            resolver.Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(miniMax);
        }
        var clientFactory = new Mock<IVideoGenerationClientFactory>();
        clientFactory.Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>())).Returns(client.Object);

        var speechSource = new FakeSpeech(speech ?? SpeechOf(8_400));
        var stills = new FakeStills(still ?? MadeStill);

        var executor = new VideoGenerateStepExecutor(
            gate.Object, resolver.Object, clientFactory.Object, workspace.Object, factory,
            new GeneratedClipNormalizer(new Mock<IVideoToolRunner>().Object,
                Options.Create(new VideoEditingOptions()), NullLogger<GeneratedClipNormalizer>.Instance),
            Options.Create(new VideoGenerationOptions()), NullLogger<VideoGenerateStepExecutor>.Instance,
            new Mock<IMediaWorkspaceFactory>(MockBehavior.Strict).Object, new Mock<IKeyframeExtractor>(MockBehavior.Strict).Object,
            avatars: avatars,
            avatarOptions: Options.Create(new AvatarGenerationOptions { Enabled = true, Generation = AvatarGenerationMode.On }),
            avatarSpeech: speechSource,
            avatarStills: stills);

        string audio = withAudio ? ",\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":1}" : string.Empty;
        string config =
            $$"""{"maxSpendCredits":1000.0,"takes":1,"maxClips":1,"inlinePrompt":"A bright cafe counter, morning light","timeoutSeconds":60,"pollIntervalSeconds":1,"model":"{{model}}","resolution":"{{resolution}}","ratio":"9:16","durationSeconds":{{duration}},{{(withAvatar ? $"\"avatarId\":\"{avatars.Avatar.Id}\"," : "")}}"allowSourceMediaEgress":true{{audio}}{{extra}}}""";
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = config
        };
        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "t",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };
        return new Harness(executor, context, factory, avatars, gate, sent, speechSource, stills, workspace, resolver);
    }

    private static ResolvedAvatarReference Ref(
        Guid project, AvatarReferenceKind kind, AvatarReferenceRole role, int ordinal, byte[] bytes, string name, string mime, int? ms = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), kind, role, ordinal, Sha(bytes), $"projects/{project}/userFiles/{name}", mime, ms);

    private static string Failed(StepExecutionResult r) => JsonNode.Parse(r.Output)!["meta"]!["failed"]!.GetValue<string>();

    private static void NothingReserved(Harness h) =>
        h.Gate.Verify(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()), Times.Never);

    // ================================================================ the request

    [Fact]
    public async Task A_talking_head_take_animates_the_still_with_the_speech_and_lasts_as_long_as_the_speech()
    {
        Harness h = Build();
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        VideoGenerationRequest sent = h.Sent.Should().ContainSingle().Subject;
        sent.Images.Should().ContainSingle().Which.Bytes.Should().Equal(StillPng);
        sent.References.Should().ContainSingle().Which.Kind.Should().Be(AvatarReferenceKind.Audio);
        sent.References![0].Bytes.Should().Equal(SampleWav);
        sent.PromptText.Should().BeEmpty("H3 lip sync has no prompt field; the step's words went into the still");
        sent.DurationSeconds.Should().Be(9, "8.4 s of speech, rounded up to whole seconds");
        sent.NoVendorRetention.Should().BeTrue();

        using var db = h.Factory.CreateDbContext();
        ExternalGenerationJob job = db.ExternalGenerationJobs.Single();
        job.AvatarId.Should().Be(h.Avatars.Avatar.Id);
        job.CostEstimate.Should().Be(CreditCurrency.CreditsFromUsd(9 * 0.08m), "reserved at the speech's seconds, not the step's 15");

        JsonNode avatar = JsonNode.Parse(result.Output)!["meta"]!["avatar"]!;
        avatar["path"]!.GetValue<string>().Should().Be("stillThenAnimate");
        avatar["still"]!["made"]!.GetValue<bool>().Should().BeTrue();
        avatar["still"]!["model"]!.GetValue<string>().Should().Be("google/nano-banana-2.1/edit");
        avatar["referencesSent"]!["images"]!.GetValue<int>().Should().Be(3);
        avatar["referencesSent"]!["audio"]!.GetValue<int>().Should().Be(1);
        result.Output.Should().NotContain("stills/", "no storage key of the still reaches the output");
        result.Output.Should().NotContain("anna-face");
    }

    [Fact]
    public async Task A_talking_head_runs_on_the_LipSync_row_even_when_the_default_video_model_is_MiniMax()
    {
        // The production bug: the step carries the fal lip-sync model and no providerId; the VideoGeneration default is MiniMax.
        Harness h = Build(miniMaxVideoDefault: true);
        await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Should().ContainSingle().Which.Model.Should().Be(H3LipSync);
        h.Resolver.Verify(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Resolver.Verify(r => r.ResolveLipSyncAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_talking_head_with_no_provider_of_its_own_runs_the_LipSync_rows_model_and_its_own_resolution()
    {
        // The template case: the step carries the H3 hint and 768P; the default LipSync row is Kling's avatar model, which offers
        // only the native resolution. The step runs on Kling at its own resolution instead of failing after the script is paid.
        Harness h = Build(model: H3LipSync, resolution: "768P");
        h.Resolver.Setup(r => r.ResolveLipSyncAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(
            new ResolvedVideoGenerationProvider(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, "https://queue.fal.run", KlingAvatar, "key", 1800));
        await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Should().ContainSingle().Which.Model.Should().Be(KlingAvatar);
    }

    [Fact]
    public async Task A_providerId_that_is_not_a_LipSync_row_falls_through_to_the_default_and_its_model_never_the_steps()
    {
        // Precedent: every resolver treats an id of the wrong capability as no id and uses the default. A step saved before
        // LipSync existed can carry a VideoGeneration row's id: it runs the default LipSync row's model.
        Guid stale = Guid.NewGuid();
        Harness h = Build(model: H3LipSync, resolution: "768P", extra: $",\"providerId\":\"{stale}\"");
        ResolvedVideoGenerationProvider kling = new(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, "https://queue.fal.run", KlingAvatar, "key", 1800);
        h.Resolver.Setup(r => r.ResolveLipSyncAsync(stale, It.IsAny<CancellationToken>())).ReturnsAsync(kling);
        await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Should().ContainSingle().Which.Model.Should().Be(KlingAvatar);
    }

    [Fact]
    public async Task A_talking_head_with_no_LipSync_row_fails_in_plain_words_and_never_touches_the_VideoGeneration_row()
    {
        Harness h = Build(miniMaxVideoDefault: true, noLipSyncRow: true);
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        result.Status.Should().Be(StepStatus.Failed);
        Failed(result).Should().Be("PROVIDER_NOT_FOUND");
        result.ErrorDetails.Should().Contain("No lip-sync model is set up").And.Contain("workspace admin");
        h.Resolver.Verify(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Sent.Should().BeEmpty();
        NothingReserved(h);
    }

    [Fact]
    public async Task The_still_is_made_from_at_most_three_pictures_in_the_avatars_order_and_never_from_its_audio()
    {
        Harness h = Build();
        await h.Executor.ExecuteAsync(h.Context);

        AvatarStillRequest request = h.Stills.Requests.Should().ContainSingle().Subject;
        request.Pictures.Should().HaveCount(AvatarStillStorage.MaxPictures);
        request.Pictures.Select(p => p.ReferenceId).Should().Equal(
            h.Avatars.Avatar.References.Where(r => r.Kind == AvatarReferenceKind.Image).OrderBy(r => r.Ordinal).Take(3).Select(r => r.Id));
        request.Ratio.Should().Be("9:16");
        request.Prompt.Should().Be("A bright cafe counter, morning light");
        request.Fingerprint.Should().Be(AvatarFingerprint.Compute(h.Avatars.Avatar));
    }

    [Fact]
    public async Task Kling_also_gets_the_steps_prompt_as_its_motion_prompt()
    {
        Harness h = Build(KlingAvatar, resolution: FalVideoCatalog.NativeToken);
        await h.Executor.ExecuteAsync(h.Context);
        h.Sent.Single().PromptText.Should().Be("A bright cafe counter, morning light");
    }

    [Fact]
    public async Task A_new_speech_is_hashed_apart_so_the_ledger_never_reuses_the_old_clip()
    {
        Harness h = Build();
        await h.Executor.ExecuteAsync(h.Context);
        h.Speech.Outcome = new(new AvatarSpeechTrack(SampleWav, "audio/wav", 8_400, "othersha", 2));
        var second = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                ProjectId = h.Context.Execution.ProjectId,
                Status = ExecutionStatus.Running,
                InitiatedByUserId = h.Context.Execution.InitiatedByUserId
            },
            Step = h.Context.Step,
            AllSteps = h.Context.AllSteps,
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "t2",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };
        await h.Executor.ExecuteAsync(second);

        using var db = h.Factory.CreateDbContext();
        db.ExternalGenerationJobs.Select(j => j.RequestHash).Distinct().Count().Should().Be(2, "a different narration is a different clip");
        h.Sent.Should().HaveCount(2);
    }

    // ================================================================ refusals, all before anything is reserved

    [Fact]
    public async Task A_talking_head_step_with_nothing_to_say_is_refused_before_the_speech_or_the_still()
    {
        Harness h = Build(withAudio: false);
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(VideoReferenceInput.NotSupportedCode);
        result.ErrorDetails.Should().Contain("speech track");
        h.Speech.Calls.Should().Be(0);
        h.Stills.Requests.Should().BeEmpty();
        NothingReserved(h);
    }

    [Fact]
    public async Task A_step_with_the_avatars_speech_but_no_avatar_from_the_step_or_the_brief_is_refused()
    {
        Harness h = Build(withAvatar: false);
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be("CONFIG_INVALID");
        result.ErrorDetails.Should().Contain("no avatar was chosen");
        result.Retryable.Should().BeFalse("the same step fails the same way on every attempt");
        result.UserMessage.Should().Be(result.ErrorDetails, "the sentence reaches the run page verbatim");
        h.Speech.Calls.Should().Be(0);
        h.Sent.Should().BeEmpty();
        NothingReserved(h);
    }

    [Fact]
    public void A_step_may_name_its_speech_and_leave_the_avatar_to_the_run_brief_but_only_where_the_brief_applies()
    {
        var inline = new VideoGenerateStepConfig { MaxSpendCredits = 1m, InlinePrompt = "x", AvatarAudio = new ExtractInputRef(ExtractInputSource.Step, 2) };
        inline.Validate().Should().BeEmpty();

        var both = new VideoGenerateStepConfig { MaxSpendCredits = 1m, InlinePrompt = "x", AvatarAudio = new ExtractInputRef(ExtractInputSource.Previous), AvatarAudioProjectFileId = Guid.NewGuid() };
        both.Validate().Should().Contain(e => e.Contains("choose one"));

        var plan = new VideoGenerateStepConfig
        {
            MaxSpendCredits = 1m,
            PlanSource = VideoGeneratePlanSource.StepRef,
            Plan = new ExtractInputRef(ExtractInputSource.Previous),
            AvatarAudio = new ExtractInputRef(ExtractInputSource.Previous)
        };
        plan.Validate().Should().Contain(e => e.Contains("AvatarAudio applies only to an inline step"));
    }

    [Fact]
    public async Task A_missing_narration_is_the_customers_to_fix_and_makes_no_still()
    {
        Harness h = Build(speech: AvatarSpeechTrackOutcome.Refused(AvatarStillCodes.AudioUnavailable, "The avatar has nothing to say: step 1 spoke no line."));
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarStillCodes.AudioUnavailable);
        h.Stills.Requests.Should().BeEmpty();
        NothingReserved(h);
    }

    [Theory]
    [InlineData(4_900, 15, "at least 5 s")]
    [InlineData(15_200, 15, "at most 15 s")]
    [InlineData(9_100, 9, "at most 9 s")]
    public async Task Speech_outside_the_models_bounds_or_the_steps_priced_duration_is_refused(int ms, int duration, string says)
    {
        Harness h = Build(duration: duration, speech: SpeechOf(ms));
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarStillCodes.AudioLength);
        result.ErrorDetails.Should().Contain(says);
        result.Retryable.Should().BeFalse();
        result.UserMessage.Should().Be(result.ErrorDetails);
        h.Stills.Requests.Should().BeEmpty();
        NothingReserved(h);
    }

    /// <summary>Runs the real VideoGenerate executor under the engine's retry loop and counts the attempts.</summary>
    private sealed class CountingExecutor(IStepExecutor inner) : IStepExecutor
    {
        public int Calls { get; private set; }
        public StepType StepType => inner.StepType;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            Calls++;
            return inner.ExecuteAsync(context);
        }
    }

    [Fact]
    public async Task A_deterministic_failure_is_attempted_once_not_three_times()
    {
        Harness h = Build(withAvatar: false);
        var counting = new CountingExecutor(h.Executor);
        var service = new WorkflowExecutorService(
            scopeFactory: null!,
            eventPublisher: null!,
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: new IStepExecutor[] { counting },
            rabbitHelper: new RabbitMqHelper(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions { MaxStepRetries = 3 }),
            cancellationRegistry: new ExecutionCancellationRegistry());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteStepWithRetryAsync(counting, h.Context, h.Context.Step, CancellationToken.None));

        counting.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_still_that_cannot_be_made_fails_with_its_code_and_reserves_nothing()
    {
        Harness h = Build(still: _ => AvatarStillOutcome.Refused(AvatarStillCodes.StillProviderMissing, "No picture-edit model."));
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarStillCodes.StillProviderMissing);
        h.Sent.Should().BeEmpty();
        NothingReserved(h);
    }

    [Fact]
    public async Task A_revoke_while_the_still_is_made_removes_the_new_still_and_fails_with_the_avatar_code()
    {
        Harness h = Build();
        h.Stills.OnMake = () => h.Avatars.Revoke();
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarFailureCodes.ConsentRevoked);
        h.Workspace.Verify(w => w.DeleteArtifactAsync(h.Context.Execution.ProjectId,
            It.Is<string>(k => k.Contains("/agentFiles/avatars/") && k.Contains("/stills/")), It.IsAny<CancellationToken>()), Times.Once);
        h.Sent.Should().BeEmpty();
        NothingReserved(h);
    }

    [Fact]
    public async Task A_reused_still_is_not_deleted_when_the_avatar_changes_during_the_run()
    {
        Harness h = Build(still: r => new(new AvatarStill(StillPng, "image/png", "s", "m", Reused: true, "projects/x/agentFiles/avatars/y/stills/z.img")));
        h.Stills.OnMake = () => h.Avatars.Revoke();
        await h.Executor.ExecuteAsync(h.Context);

        h.Workspace.Verify(w => w.DeleteArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ================================================================ hold

    [Fact]
    public void A_talking_head_steps_hold_also_covers_its_still()
    {
        VideoGenerateCreditPricing.AnimatesStill(RealFal, H3LipSync).Should().BeTrue();
        VideoGenerateCreditPricing.AnimatesStill(RealFal, "fal-ai/vidu/q4/image-to-video").Should().BeFalse();
        VideoGenerateCreditPricing.AnimatesStill(RealFal, "no/such/model").Should().BeFalse();
        VideoGenerateCreditPricing.AvatarStillHoldUsd.Should().Be(0.063m, "the 2K card of the default still model");
        VideoGenerateCreditPricing.MaxInputMaterialUsd(RealFal, H3LipSync, "768P").Should().Be(0m);
    }

    // ================================================================ the speech track

    private static StepExecutionContext ContextWith(Guid project, params StepOutputHistoryEntry[] history)
    {
        var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoGenerate };
        return new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = project },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "t",
            StepOutputHistory = [.. history],
            CancellationToken = CancellationToken.None
        };
    }

    [Fact]
    public void The_speech_is_the_voiceovers_spoken_lines_in_their_order()
    {
        Guid project = Guid.NewGuid();
        string voiceover = $$$"""
            {"view":{"lines":[
              {"id":"vo1","startSec":0,"durationSec":2.5,"status":"ok","storageKey":"projects/{{{project}}}/agentFiles/voiceover/lines/a.wav"},
              {"id":"vo2","startSec":2.5,"durationSec":0,"status":"skipped"},
              {"id":"vo3","startSec":2.5,"durationSec":1.0,"status":"failed","storageKey":"projects/{{{project}}}/agentFiles/voiceover/lines/x.wav"},
              {"id":"vo4","startSec":3.5,"durationSec":3.0,"status":"ok","storageKey":"projects/{{{project}}}/agentFiles/voiceover/lines/b.wav"},
              {"id":"vo5","startSec":6.5,"durationSec":3.0,"status":"ok","storageKey":"projects/{{{Guid.NewGuid()}}}/agentFiles/voiceover/lines/c.wav"}
            ]},"meta":{}}
            """;
        StepExecutionContext context = ContextWith(project,
            new StepOutputHistoryEntry(1, "Script", "{\"scenes\":[]}"),
            new StepOutputHistoryEntry(2, "Voiceover", voiceover));

        (IReadOnlyList<string>? byStep, _) = AvatarSpeechTrackSource.VoiceoverLineKeys(
            context, new ExtractInputRef(ExtractInputSource.Step, 2), project);
        byStep.Should().Equal($"projects/{project}/agentFiles/voiceover/lines/a.wav", $"projects/{project}/agentFiles/voiceover/lines/b.wav");

        (IReadOnlyList<string>? previous, _) = AvatarSpeechTrackSource.VoiceoverLineKeys(
            context, new ExtractInputRef(ExtractInputSource.Previous), project);
        previous.Should().Equal(byStep);

        (IReadOnlyList<string>? notVoiceover, string? why) = AvatarSpeechTrackSource.VoiceoverLineKeys(
            context, new ExtractInputRef(ExtractInputSource.Step, 1), project);
        notVoiceover.Should().BeNull();
        why.Should().Contain("not a voiceover");
    }

    [Fact]
    public void The_join_is_first_party_argv_with_a_pause_between_lines_and_whitelisted_inputs()
    {
        IReadOnlyList<string> args = AvatarSpeechTrackSource.BuildJoinArgs(["/s/in-0.audio", "/s/in-1.audio"], "/s/speech.wav");

        args.Count(a => a == "-i").Should().Be(2);
        args.Count(a => a == "-format_whitelist").Should().Be(2);
        args[args.ToList().IndexOf("-format_whitelist") + 1].Should().Be("wav,mp3");
        string graph = args[args.ToList().IndexOf("-filter_complex") + 1];
        graph.Should().Contain("[0:a:0]aresample=44100,aformat=sample_fmts=s16:channel_layouts=mono,apad=pad_dur=0.25[a0]");
        graph.Should().Contain("[1:a:0]aresample=44100,aformat=sample_fmts=s16:channel_layouts=mono[a1]");
        graph.Should().EndWith("[a0][a1]concat=n=2:v=0:a=1[out]");
        args[^1].Should().Be("/s/speech.wav");
    }

    // ================================================================ the still maker

    private static (AvatarStillMaker Maker, Mock<IImageClient> Client, Mock<IProjectFileWorkspace> Workspace) StillMaker(
        ResolvedImageProvider? provider, bool exists = false)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);
        var client = new Mock<IImageClient>();
        client.Setup(c => c.EditAsync(It.IsAny<ImageEditRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedImageSet([new GeneratedImage(StillPng, "image/png")]) { ResolvedModel = "google/nano-banana-2.1/edit" });
        var images = new Mock<IImageClientFactory>();
        images.Setup(f => f.Get(It.IsAny<ResolvedImageProvider>())).Returns(client.Object);
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(exists);
        workspace.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string _, string path, long _, CancellationToken _) => File.WriteAllBytesAsync(path, StillPng));
        workspace.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), "agentFiles"))
            .ReturnsAsync("k");
        // ffmpeg is not run here: a failed downscale sends a small picture as it is.
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(1, "", "no ffmpeg in tests", false));
        return (new AvatarStillMaker(resolver.Object, images.Object, workspace.Object, runner.Object, NullLogger<AvatarStillMaker>.Instance),
            client, workspace);
    }

    private static ResolvedImageProvider ImageProvider(InferenceProviderKind kind = InferenceProviderKind.FalAi, string model = "") =>
        new(Guid.NewGuid(), "fal images", kind, InferenceProviderCapability.ImageEdit, "https://queue.fal.run", model, "k", 60);

    private static AvatarStillRequest StillRequest(string fingerprint = "fp") => new(
        Guid.NewGuid(), Guid.NewGuid(), fingerprint,
        [new AvatarStillPicture(Guid.NewGuid(), "image/png", FacePng), new AvatarStillPicture(Guid.NewGuid(), "image/jpeg", SideJpeg)],
        "A bright cafe counter", "9:16");

    [Fact]
    public async Task The_still_is_one_fal_edit_of_the_pictures_kept_under_the_avatars_stills_folder_and_not_kept_by_the_vendor()
    {
        (AvatarStillMaker maker, Mock<IImageClient> client, Mock<IProjectFileWorkspace> workspace) = StillMaker(ImageProvider());
        AvatarStillRequest request = StillRequest();

        AvatarStillOutcome outcome = await maker.MakeAsync(request, CancellationToken.None);

        outcome.Code.Should().BeNull(outcome.Message);
        outcome.Still!.Reused.Should().BeFalse();
        outcome.Still.Bytes.Should().Equal(StillPng);
        outcome.Still.StorageKey.Should().StartWith(AvatarStillStorage.ObjectPrefix(request.ProjectId, request.AvatarId));
        client.Verify(c => c.EditAsync(It.Is<ImageEditRequest>(e =>
            e.Model == "google/nano-banana-2.1/edit"
            && e.NoVendorRetention
            && e.AspectRatio == "9:16"
            && e.SourceImageUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
            && e.ReferenceImageUrls!.Count == 1
            && e.Prompt.Contains("Setting and look: A bright cafe counter", StringComparison.Ordinal)), It.IsAny<CancellationToken>()), Times.Once);
        workspace.Verify(w => w.UploadArtifactAsync(request.ProjectId, It.IsAny<string>(),
            It.Is<string>(p => p.StartsWith($"avatars/{request.AvatarId:D}/stills/", StringComparison.Ordinal)), "image/png",
            It.IsAny<CancellationToken>(), "agentFiles"), Times.Once);
    }

    [Fact]
    public async Task A_chosen_picture_model_makes_the_still_and_a_foreign_or_unusable_one_is_refused()
    {
        // App inputs F1 (docs/design/app-inputs.md 27): the step's stillProviderId is resolved explicit first, like every
        // capability; the default picture-edit model is not asked.
        Guid chosen = Guid.NewGuid();
        (AvatarStillMaker maker, Mock<IImageClient> client, _) = StillMaker(null);
        var resolver = new Mock<IInferenceProviderResolver>(MockBehavior.Strict);
        resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, chosen, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageProvider(model: "google/nano-banana-2.1/edit"));
        var images = new Mock<IImageClientFactory>();
        images.Setup(f => f.Get(It.IsAny<ResolvedImageProvider>())).Returns(client.Object);
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        workspace.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), "agentFiles"))
            .ReturnsAsync("k");
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(1, "", "no ffmpeg in tests", false));
        maker = new AvatarStillMaker(resolver.Object, images.Object, workspace.Object, runner.Object, NullLogger<AvatarStillMaker>.Instance);

        AvatarStillOutcome outcome = await maker.MakeAsync(StillRequest() with { StillProviderId = chosen }, CancellationToken.None);

        outcome.Code.Should().BeNull(outcome.Message);
        resolver.Verify(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, chosen, It.IsAny<CancellationToken>()), Times.Once);

        resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, chosen, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedImageProvider?)null);
        AvatarStillOutcome refused = await maker.MakeAsync(StillRequest() with { StillProviderId = chosen }, CancellationToken.None);
        refused.Code.Should().Be(AvatarStillCodes.StillProviderMissing);
        refused.Message.Should().Contain("picture model chosen");
    }

    [Fact]
    public async Task The_hold_prices_the_chosen_picture_model_at_its_own_billing_and_never_below_the_fixed_still()
    {
        Guid video = Guid.NewGuid();
        Guid still = Guid.NewGuid();
        string Config(Guid? stillId)
        {
            string extra = stillId is { } s ? ",\"stillProviderId\":\"" + s.ToString("D") + "\"" : string.Empty;
            return "{\"planSource\":\"Inline\",\"inlinePrompt\":\"a studio\",\"model\":\"" + H3LipSync + "\",\"resolution\":\"768P\",\"durationSeconds\":15,\"ratio\":\"9:16\",\"takes\":1,\"maxClips\":1,\"maxSpendCredits\":250,\"avatarId\":\"" + Guid.NewGuid().ToString("D") + "\",\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":1}" + extra + "}";
        }

        async Task<long?> Hold(Guid? stillId, Guid? stillOrg, long stillPrice)
        {
            var resolver = new Mock<IInferenceProviderResolver>();
            resolver.Setup(r => r.ResolveLipSyncAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedVideoGenerationProvider(video, "fal", InferenceProviderKind.FalAi, "https://queue.fal.run", H3LipSync, "key", 60, null));
            resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, still, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ImageProvider(model: "google/nano-banana-2.1/edit") with { ProviderId = still, OrganizationId = stillOrg });
            var clients = new Mock<IVideoGenerationClientFactory>();
            clients.Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>())).Returns(RealFal);
            return await VideoGenerateCreditPricing.TryHoldMillisAsync(Config(stillId), resolver.Object, clients.Object, new StillPrices(stillPrice));
        }

        long? plain = await Hold(null, null, 0);
        plain.Should().Be(StillPrices.VideoMillis + StillPrices.FixedStillMillis, "no choice keeps today's figure");
        (await Hold(still, null, 200)).Should().Be(StillPrices.VideoMillis + 200, "a dearer chosen model holds its own price");
        (await Hold(still, null, 10)).Should().Be(plain, "a hold never shrinks below the fixed still");
        (await Hold(still, Guid.NewGuid(), 200)).Should().Be(StillPrices.VideoMillis, "a bring-your-own picture model holds nothing");
    }

    private sealed class StillPrices(long stillMillis) : IModelPriceService
    {
        public const long VideoMillis = 5000;
        public const long FixedStillMillis = 63;

        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
        {
            bool platform = request.BillingMode == UsageBillingMode.Platform;
            long millis = request.Unit == UsageKind.ImageEditImages ? stillMillis : VideoMillis;
            return ValueTask.FromResult(new PriceQuote(platform ? millis : 0, 0m, Guid.NewGuid()));
        }

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new PriceQuote(billingMode == UsageBillingMode.Platform ? FixedStillMillis : 0, 0m, Guid.NewGuid()));

        public void Invalidate()
        {
        }
    }

    [Fact]
    public async Task A_stored_still_of_the_same_avatar_and_recipe_is_reused_and_buys_nothing()
    {
        (AvatarStillMaker maker, Mock<IImageClient> client, _) = StillMaker(ImageProvider(), exists: true);

        AvatarStillOutcome outcome = await maker.MakeAsync(StillRequest(), CancellationToken.None);

        outcome.Still!.Reused.Should().BeTrue();
        client.Verify(c => c.EditAsync(It.IsAny<ImageEditRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task No_fal_edit_model_or_a_single_image_route_is_a_setup_to_fix()
    {
        (await StillMaker(null).Maker.MakeAsync(StillRequest(), default)).Code.Should().Be(AvatarStillCodes.StillProviderMissing);
        (await StillMaker(ImageProvider(InferenceProviderKind.MiniMax)).Maker.MakeAsync(StillRequest(), default)).Code
            .Should().Be(AvatarStillCodes.StillProviderMissing, "the data URIs and the retention header are fal's");
        (await StillMaker(ImageProvider(model: "bria/fibo-edit/edit")).Maker.MakeAsync(StillRequest(), default)).Code
            .Should().Be(AvatarStillCodes.StillModelUnsupported);
        (await StillMaker(ImageProvider(model: "ideogram/v4.5")).Maker.MakeAsync(StillRequest(), default)).Code
            .Should().Be(AvatarStillCodes.StillModelUnsupported);
    }

    [Fact]
    public void A_re_attested_avatar_gets_a_new_still()
    {
        AvatarStillRequest r = StillRequest("before");
        string instruction = AvatarStillMaker.Instruction(r.Prompt);
        AvatarStillMaker.Digest(r, "m", instruction).Should().NotBe(AvatarStillMaker.Digest(r with { Fingerprint = "after" }, "m", instruction));
        AvatarStillMaker.Digest(r, "m", instruction).Should().NotBe(AvatarStillMaker.Digest(r with { Ratio = "1:1" }, "m", instruction));
        AvatarStillMaker.Digest(r, "m", instruction).Should().Be(AvatarStillMaker.Digest(r, "m", instruction));
    }

    // ================================================================ storage layout

    [Fact]
    public void Only_a_still_key_of_the_project_names_an_avatar()
    {
        Guid project = Guid.NewGuid(), avatar = Guid.NewGuid();
        string key = AvatarStillStorage.ObjectPrefix(project, avatar) + "abc.img";

        AvatarStillStorage.AvatarOf(project, key).Should().Be(avatar);
        AvatarStillStorage.AvatarOf(Guid.NewGuid(), key).Should().BeNull("another project's key");
        AvatarStillStorage.AvatarOf(project, $"projects/{project}/agentFiles/avatars/{avatar}/other/abc.img").Should().BeNull();
        AvatarStillStorage.AvatarOf(project, $"projects/{project}/agentFiles/avatars/{avatar}/stills/a/b.img").Should().BeNull();
        AvatarStillStorage.AvatarOf(project, $"projects/{project}/agentFiles/avatars/not-a-guid/stills/abc.img").Should().BeNull();
        AvatarStillStorage.AvatarOf(project, $"projects/{project}/agentFiles/avatars/{avatar}/stills/").Should().BeNull();
    }

    [Fact]
    public void The_engine_may_delete_a_still_of_its_own_project_and_nothing_beside_it()
    {
        Guid project = Guid.NewGuid(), avatar = Guid.NewGuid();
        FluentActions.Invoking(() => ProjectFileWorkspace.ValidateDeleteScope(project, AvatarStillStorage.ObjectPrefix(project, avatar) + "abc.img"))
            .Should().NotThrow();
        FluentActions.Invoking(() => ProjectFileWorkspace.ValidateDeleteScope(project, $"projects/{project}/agentFiles/avatars/{avatar}/notes.txt"))
            .Should().Throw<UnauthorizedAccessException>();
        FluentActions.Invoking(() => ProjectFileWorkspace.ValidateDeleteScope(project, $"projects/{project}/userFiles/anna.png"))
            .Should().Throw<UnauthorizedAccessException>();
    }

    // ================================================================ the voice of the avatar

    private static async Task<StepExecutionResult> RunVoiceover(FakeAvatars avatars, RunBriefState? brief)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "fish", InferenceProviderKind.FishAudio, "https://api.fish.audio", "s1", "k", 60));
        var executor = new VoiceoverStepExecutor(
            new Mock<ISpeechSynthesisClientFactory>().Object, resolver.Object, new Mock<IMediaProbe>().Object,
            new Mock<IProjectFileWorkspace>().Object, new Mock<IVoiceoverAlignmentService>().Object,
            NullLogger<VoiceoverStepExecutor>.Instance, new NoRowsVoiceResolver(), avatars: avatars);
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = """{"source":{"kind":"Inline","lines":[{"id":"vo1","text":"Hello there, welcome in.","startSec":0}]},"voiceFromAvatar":true}"""
        };
        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "t",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };
        using (RunBriefAmbient.Enter(brief))
        {
            return await executor.ExecuteAsync(context);
        }
    }

    private static FakeAvatars VoiceAvatar(Guid? voice) => new()
    {
        Avatar = new ResolvedAvatar(Guid.NewGuid(), "Anna presenter", true, null, [], voice, AvatarDisclosure.Visible, DateTime.UtcNow)
    };

    [Fact]
    public async Task A_voiceover_that_speaks_in_the_avatars_voice_uses_the_linked_voice_through_the_consent_guard()
    {
        Guid voice = Guid.NewGuid();
        FakeAvatars avatars = VoiceAvatar(voice);
        StepExecutionResult result = await RunVoiceover(avatars, new RunBriefState(new StoredRunBrief(Avatar: new RunBriefAvatar(avatars.Avatar.Id))));

        result.Status.Should().Be(StepStatus.Failed, "the fake voice resolver knows no voice, so the guard refuses the linked one");
        result.ErrorDetails.Should().StartWith("VOICE_").And.NotContain("AVATAR_VOICE_MISSING");
    }

    [Fact]
    public async Task No_avatar_in_the_run_or_no_linked_voice_fails_and_never_falls_back()
    {
        (await RunVoiceover(VoiceAvatar(Guid.NewGuid()), null)).ErrorDetails.Should().StartWith("AVATAR_VOICE_MISSING");

        FakeAvatars silent = VoiceAvatar(null);
        (await RunVoiceover(silent, new RunBriefState(new StoredRunBrief(Avatar: new RunBriefAvatar(silent.Avatar.Id)))))
            .ErrorDetails.Should().StartWith("AVATAR_VOICE_MISSING").And.Contain("Anna presenter");

        FakeAvatars revoked = VoiceAvatar(Guid.NewGuid());
        revoked.Revoke();
        (await RunVoiceover(revoked, new RunBriefState(new StoredRunBrief(Avatar: new RunBriefAvatar(revoked.Avatar.Id)))))
            .ErrorDetails.Should().StartWith(AvatarFailureCodes.ConsentRevoked);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] Png(int width, int height)
    {
        byte[] b = new byte[64];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        sig.CopyTo(b, 0);
        b[11] = 13;
        "IHDR"u8.ToArray().CopyTo(b, 12);
        b[16] = (byte)(width >> 24); b[17] = (byte)(width >> 16); b[18] = (byte)(width >> 8); b[19] = (byte)width;
        b[20] = (byte)(height >> 24); b[21] = (byte)(height >> 16); b[22] = (byte)(height >> 8); b[23] = (byte)height;
        return b;
    }
}
