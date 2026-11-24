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
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Avatars;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Avatars phase 2, lane B4: a <c>VideoGenerate</c> step that shows an avatar. Fakes only (no vendor is reachable):
/// the order of refusals before anything is reserved, the references on the request, the input-material price in
/// the reservation, <c>avatar_id</c> on the ledger, the per-take consent re-check, the reuse guard, the cache-key
/// component, the disclosure marking and the hold.
/// </summary>
public class VideoGenerateAvatarExecutorTests
{
    private static readonly byte[] FacePng = Png(1024, 1024);
    private static readonly byte[] SpeechWav = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly MiniMaxVideoGenerationClient RealMiniMax = new(new System.Net.Http.HttpClient(), "key");

    private sealed class FakeAvatars : IAvatarResolver
    {
        public ResolvedAvatar Avatar { get; set; } = null!;
        public int Calls { get; private set; }

        public Task<ResolvedAvatar> ResolveAsync(Guid projectId, Guid avatarId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(avatarId == Avatar.Id ? Avatar : ResolvedAvatar.NotFound(avatarId));
        }

        public async Task<ResolvedAvatar> ResolveRequiredAsync(Guid projectId, Guid avatarId, CancellationToken ct)
        {
            ResolvedAvatar r = await ResolveAsync(projectId, avatarId, ct);
            return r.Usable ? r : throw new AvatarUnusableException(avatarId, r.Reason!);
        }

        public Task<IReadOnlyList<ResolvedAvatar>> ListSelectableAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ResolvedAvatar>>(Avatar.Usable ? [Avatar] : []);

        /// <summary>The row's kind alone (not counted in <see cref="Calls"/>: no reference and no byte is read for it).</summary>
        public int KindReads { get; private set; }

        public Task<AvatarSubjectKind?> SubjectKindAsync(Guid projectId, Guid avatarId, CancellationToken ct)
        {
            KindReads++;
            return Task.FromResult<AvatarSubjectKind?>(avatarId == Avatar.Id ? Avatar.SubjectKind : null);
        }

        public void Revoke() => Avatar = Avatar with { Usable = false, Reason = ProjectAvatar.ReasonConsentRevoked, References = [] };
    }

    private sealed record Harness(
        VideoGenerateStepExecutor Executor, StepExecutionContext Context, Mock<IVideoGenerationClient> Client,
        SharedInMemoryDbFactory Factory, FakeAvatars Avatars, Mock<IVideoGenerationBudgetGate> Gate, List<VideoGenerationRequest> Sent);

    private static ResolvedAvatar UsableAvatar(Guid projectId, string faceDigest)
    {
        Guid id = Guid.NewGuid();
        return new ResolvedAvatar(id, "Anna presenter", true, null,
        [
            new ResolvedAvatarReference(Guid.NewGuid(), Guid.NewGuid(), AvatarReferenceKind.Image, AvatarReferenceRole.Face, 0,
                faceDigest, $"projects/{projectId}/userFiles/anna.png", "image/png"),
            new ResolvedAvatarReference(Guid.NewGuid(), Guid.NewGuid(), AvatarReferenceKind.Audio, AvatarReferenceRole.Speech, 1,
                Sha(SpeechWav), $"projects/{projectId}/userFiles/anna.wav", "audio/wav", 4_000),
        ], null, AvatarDisclosure.Visible, DateTime.UtcNow.AddDays(-1));
    }

    private static Harness Build(
        Func<Guid, string> config,
        AvatarGenerationMode mode = AvatarGenerationMode.On,
        bool enabled = true,
        bool declaresReferences = true,
        string? faceDigestOverride = null,
        Action<FakeAvatars>? afterSubmit = null,
        bool adminInitiator = false,
        IUsageRecorder? usage = null,
        Exception? submitThrows = null,
        bool objectsEnabled = false,
        Func<ResolvedAvatar, ResolvedAvatar>? shapeAvatar = null,
        string? supportThrows = null,
        string? materialThrows = null)
    {
        var factory = new SharedInMemoryDbFactory();
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        if (adminInitiator)
        {
            var admin = new ApplicationUser { Id = Guid.NewGuid(), Email = "admin@example.invalid", IsAdmin = true };
            using var db = factory.CreateDbContext();
            db.ApplicationUsers.Add(admin);
            db.SaveChanges();
            execution.InitiatedByUserId = admin.Id;
        }
        else
        {
            execution.InitiatedByUserId = Guid.NewGuid();
        }

        var avatars = new FakeAvatars { Avatar = UsableAvatar(execution.ProjectId, faceDigestOverride ?? Sha(FacePng)) };
        if (shapeAvatar is not null) avatars.Avatar = shapeAvatar(avatars.Avatar);

        var client = new Mock<IVideoGenerationClient>();
        client.Setup(c => c.Describe(It.IsAny<string>())).Returns((string m) => RealMiniMax.Describe(m));
        if (declaresReferences)
        {
            client.As<IVideoReferenceInputSupport>()
                .Setup(c => c.ReferenceInputSupport(It.IsAny<string>()))
                .Returns((string _) => supportThrows is null ? MiniMaxVideoGenerationClient.ReferenceSupport : throw new NotSupportedException(supportThrows));
            client.As<IVideoReferenceInputSupport>()
                .Setup(c => c.InputMaterialCostUsd(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<VideoReferenceMaterial>>()))
                .Returns((string m, string r, IReadOnlyList<VideoReferenceMaterial> x) =>
                    materialThrows is null ? RealMiniMax.InputMaterialCostUsd(m, r, x) : throw new NotSupportedException(materialThrows));
        }

        var sent = new List<VideoGenerationRequest>();
        client.Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Callback((VideoGenerationRequest r, CancellationToken _) =>
            {
                if (submitThrows is not null) throw submitThrows;
                sent.Add(r);
                afterSubmit?.Invoke(avatars);
            })
            .ReturnsAsync(new ExternalJobHandle("remote-1"));
        // A take that "fails at the vendor" keeps these tests free of download/ffmpeg wiring.
        client.Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobStatus(ExternalJobState.Failed, null, "fake vendor"));

        var gate = new Mock<IVideoGenerationBudgetGate>();
        var real = VideoGenerateStepExecutorTests.RealGate(factory);
        gate.Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .Returns((VideoGenerationReservationRequest r, CancellationToken ct) => real.ReserveAsync(r, ct));

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ListFilesAsync(execution.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(avatars.Avatar.References.Select(r => new ProjectWorkspaceFile(
                r.ProjectFileId, execution.ProjectId, "f", null, "userFiles", r.StorageKey, r.MimeType,
                r.Kind == AvatarReferenceKind.Image ? FacePng.Length : SpeechWav.Length, DateTime.UtcNow, null)).ToList());
        workspace.Setup(w => w.DownloadStorageKeyToFileAsync(
                execution.ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string key, string path, long _, CancellationToken _) =>
                File.WriteAllBytesAsync(path, key.EndsWith(".png", StringComparison.Ordinal) ? FacePng : SpeechWav));
        workspace.Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), "agentFiles"))
            .ReturnsAsync("stored/manifest.json");

        var provider = new ResolvedVideoGenerationProvider(
            Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key123", 1800);
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        // An avatar-audio step resolves LipSync; this row is MiniMax on purpose, to see the model-level refusal.
        resolver.Setup(r => r.ResolveLipSyncAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        var clientFactory = new Mock<IVideoGenerationClientFactory>();
        clientFactory.Setup(f => f.Get(provider)).Returns(client.Object);

        var executor = new VideoGenerateStepExecutor(
            gate.Object, resolver.Object, clientFactory.Object, workspace.Object, factory,
            new GeneratedClipNormalizer(new Mock<IVideoToolRunner>().Object,
                Options.Create(new VideoEditingOptions()), NullLogger<GeneratedClipNormalizer>.Instance),
            Options.Create(new VideoGenerationOptions()), NullLogger<VideoGenerateStepExecutor>.Instance,
            new Mock<IMediaWorkspaceFactory>(MockBehavior.Strict).Object, new Mock<IKeyframeExtractor>(MockBehavior.Strict).Object,
            usageRecorder: usage,
            avatars: avatars,
            avatarOptions: Options.Create(new AvatarGenerationOptions { Enabled = enabled, Generation = mode, ObjectsEnabled = objectsEnabled }));

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = config(avatars.Avatar.Id)
        };
        var context = new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "t",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };
        return new Harness(executor, context, client, factory, avatars, gate, sent);
    }

    private static string Cfg(Guid avatarId, bool egress = true, int takes = 1, string extra = "") =>
        $$"""{"maxSpendCredits":1000.0,"takes":{{takes}},"maxClips":{{takes}},"inlinePrompt":"Anna waves at the camera","timeoutSeconds":60,"pollIntervalSeconds":1,"avatarId":"{{avatarId}}","allowSourceMediaEgress":{{(egress ? "true" : "false")}}{{extra}}}""";

    private static string Failed(StepExecutionResult r) => JsonNode.Parse(r.Output)!["meta"]!["failed"]!.GetValue<string>();

    private static void NothingReserved(Harness h) =>
        h.Gate.Verify(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()), Times.Never);

    [Theory]
    [InlineData(false, AvatarGenerationMode.On)]
    [InlineData(true, AvatarGenerationMode.Off)]
    [InlineData(true, AvatarGenerationMode.PlatformAdminOnly)]
    public async Task The_switches_keep_avatar_generation_inert(bool enabled, AvatarGenerationMode mode)
    {
        Harness h = Build(id => Cfg(id), mode, enabled);
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        result.Status.Should().Be(StepStatus.Failed);
        Failed(result).Should().Be(AvatarFailureCodes.GenerationDisabled);
        h.Avatars.Calls.Should().Be(0, "nothing about the avatar is read while the feature is off");
        NothingReserved(h);
    }

    // ---------------------------------------------------------------- objects (docs/design/avatar-objects.md)

    /// <summary>The harness avatar as an object: one Front picture, no speech, metadata-only marking.</summary>
    private static ResolvedAvatar AsObject(ResolvedAvatar person) => person with
    {
        Name = "Espresso machine",
        SubjectKind = AvatarSubjectKind.Object,
        Disclosure = AvatarDisclosure.MetadataOnly,
        CoversPictures = true,
        References = [.. person.References.Where(r => r.Kind == AvatarReferenceKind.Image).Select(r => r with { Role = AvatarReferenceRole.Front })],
    };

    [Fact]
    public async Task An_object_avatar_needs_only_the_object_switch()
    {
        // People are switched off entirely (Avatars:Enabled false, Generation Off); the object switch alone is enough.
        Harness h = Build(id => Cfg(id), AvatarGenerationMode.Off, enabled: false, objectsEnabled: true, shapeAvatar: AsObject);
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Should().ContainSingle();
        h.Sent[0].References.Should().ContainSingle(r => r.Kind == AvatarReferenceKind.Image && r.Role == AvatarReferenceRole.Front);
        JsonNode avatar = JsonNode.Parse(result.Output)!["meta"]![AvatarOutputFacts.MetaKey]!;
        avatar["kind"]!.GetValue<string>().Should().Be("object");
        avatar["marking"]!.GetValue<string>().Should().Be("metadata");
        avatar["fingerprint"]!.GetValue<string>().Should().Be(AvatarFingerprint.Compute(h.Avatars.Avatar));
    }

    [Fact]
    public async Task An_object_avatar_is_not_held_to_the_platform_admin_smoke_mode_of_people()
    {
        Harness h = Build(id => Cfg(id), AvatarGenerationMode.PlatformAdminOnly, objectsEnabled: true, shapeAvatar: AsObject);
        await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Should().ContainSingle("the smoke mode gates avatars of people; the run was not started by an admin");
    }

    [Fact]
    public async Task An_object_avatar_is_refused_while_the_object_switch_is_off_even_with_people_on()
    {
        Harness h = Build(id => Cfg(id), AvatarGenerationMode.On, enabled: true, objectsEnabled: false, shapeAvatar: AsObject);
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarFailureCodes.GenerationDisabled);
        h.Avatars.Calls.Should().Be(0, "only the row's kind was read");
        NothingReserved(h);
    }

    [Theory]
    [InlineData(false, AvatarGenerationMode.On)]
    [InlineData(true, AvatarGenerationMode.Off)]
    public async Task The_object_switch_does_not_open_avatars_of_people(bool enabled, AvatarGenerationMode mode)
    {
        Harness h = Build(id => Cfg(id), mode, enabled, objectsEnabled: true);
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarFailureCodes.GenerationDisabled);
        h.Avatars.Calls.Should().Be(0);
        NothingReserved(h);
    }

    [Fact]
    public async Task With_every_switch_off_not_even_the_avatars_kind_is_read()
    {
        Harness h = Build(id => Cfg(id), AvatarGenerationMode.Off, enabled: false, objectsEnabled: false);
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarFailureCodes.GenerationDisabled);
        (h.Avatars.Calls, h.Avatars.KindReads).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_person_avatars_recorded_facts_carry_no_kind_so_they_stay_what_they_were()
    {
        Harness h = Build(id => Cfg(id));
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        JsonNode.Parse(result.Output)!["meta"]![AvatarOutputFacts.MetaKey]!.AsObject().ContainsKey("kind").Should().BeFalse();
    }

    [Theory]
    [InlineData(false, AvatarGenerationMode.Off, false, false, false)]
    [InlineData(true, AvatarGenerationMode.Off, false, false, false)]
    [InlineData(false, AvatarGenerationMode.On, false, false, false)]
    [InlineData(true, AvatarGenerationMode.On, false, true, false)]
    [InlineData(true, AvatarGenerationMode.PlatformAdminOnly, false, true, false)]
    [InlineData(false, AvatarGenerationMode.Off, true, false, true)]
    [InlineData(true, AvatarGenerationMode.On, true, true, true)]
    public void The_generation_switches_are_judged_per_kind(bool enabled, AvatarGenerationMode mode, bool objects, bool person, bool obj)
    {
        var options = new AvatarGenerationOptions { Enabled = enabled, Generation = mode, ObjectsEnabled = objects };

        options.AllowsGeneration(AvatarSubjectKind.Person).Should().Be(person);
        options.AllowsGeneration(AvatarSubjectKind.Object).Should().Be(obj);
        new AvatarGenerationOptions().ObjectsEnabled.Should().BeFalse("objects are off by default");
        AvatarGenerationOptions.ObjectsEnabledKey.Should().Be("Avatars:Objects:Enabled");
    }

    [Fact]
    public async Task A_clip_that_shows_only_an_object_gets_the_neutral_marking_and_no_label()
    {
        List<IReadOnlyList<string>> calls = [];
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, """{"streams":[{"codec_type":"video","codec_name":"h264","width":1280,"height":720,"r_frame_rate":"24/1","pix_fmt":"yuv420p"}],"format":{"duration":"6.0"}}""", "", false));
        runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<string> args, TimeSpan _, CancellationToken _) => calls.Add(args))
            .ReturnsAsync(new VideoToolResult(0, "", "", false));
        var normalizer = new GeneratedClipNormalizer(runner.Object, Options.Create(new VideoEditingOptions()), NullLogger<GeneratedClipNormalizer>.Instance);

        await normalizer.NormalizeAsync("/tmp/in.mp4", "/tmp/object.mp4", CancellationToken.None, null, GeneratedClipDisclosure.Neutral);
        await normalizer.NormalizeAsync("/tmp/in.mp4", "/tmp/person.mp4", CancellationToken.None, null, new GeneratedClipDisclosure(false));

        (GeneratedClipDisclosure.Neutral.VisibleLabel, GeneratedClipDisclosure.Neutral.ShowsPerson).Should().Be((false, false));
        calls[0].Should().Contain("comment=AI-generated video (ReelBolt).").And.Contain("ai_generated=true").And.Contain("ai_generator=ReelBolt");
        string neutral = string.Join(' ', calls[0]);
        neutral.Should().Contain("+faststart+use_metadata_tags").And.NotContain("drawtext").And.NotContain("likeness").And.NotContain("consent");
        // A person's clip keeps the comment it always had.
        calls[1].Should().Contain("comment=AI-generated video (ReelBolt). It shows a person's likeness used with their recorded consent.");
        new GeneratedClipDisclosure(true).CommentText.Should().Be(GeneratedClipDisclosure.Comment);
    }

    [Fact]
    public async Task The_smoke_run_mode_lets_a_platform_admin_through()
    {
        Harness h = Build(id => Cfg(id), AvatarGenerationMode.PlatformAdminOnly, adminInitiator: true);
        await h.Executor.ExecuteAsync(h.Context);
        h.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Without_the_steps_egress_permission_nothing_is_read_or_reserved()
    {
        Harness h = Build(id => Cfg(id, egress: false));
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be("EGRESS_REFUSED");
        h.Avatars.Calls.Should().Be(0);
        NothingReserved(h);
    }

    [Fact]
    public async Task A_model_without_reference_mode_or_a_driving_audio_track_on_MiniMax_is_refused()
    {
        Harness none = Build(id => Cfg(id), declaresReferences: false);
        Failed(await none.Executor.ExecuteAsync(none.Context)).Should().Be(VideoReferenceInput.NotSupportedCode);
        NothingReserved(none);

        Harness audio = Build(id => Cfg(id, extra: ",\"avatarAudio\":{\"from\":\"Previous\"}"));
        StepExecutionResult result = await audio.Executor.ExecuteAsync(audio.Context);
        Failed(result).Should().Be(VideoReferenceInput.NotSupportedCode);
        result.ErrorDetails.Should().Contain("driving audio");
        NothingReserved(audio);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_provider_refusal_reaches_the_run_page_as_a_fixed_sentence_never_the_raw_exception_text(bool atSupport)
    {
        const string raw = "fal-ai/secret-vendor-endpoint-1, fal-ai/secret-vendor-endpoint-2 are the only endpoints";
        Harness h = atSupport
            ? Build(id => Cfg(id), supportThrows: raw)
            : Build(id => Cfg(id), materialThrows: raw);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        result.Status.Should().Be(StepStatus.Failed);
        result.UserMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("secret-vendor");
        result.ErrorDetails.Should().NotContain("secret-vendor");
        result.Output.Should().NotContain("secret-vendor");
        if (atSupport)
            NothingReserved(h);
    }

    [Fact]
    public async Task A_revoked_avatar_fails_closed_with_its_code_and_never_becomes_no_avatar()
    {
        Harness h = Build(id => Cfg(id));
        h.Avatars.Revoke();
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        result.Status.Should().Be(StepStatus.Failed);
        Failed(result).Should().Be(AvatarFailureCodes.ConsentRevoked);
        h.Sent.Should().BeEmpty();
        NothingReserved(h);
    }

    [Fact]
    public async Task A_reference_recording_the_client_refuses_before_any_request_releases_the_take_and_asks_for_a_reupload()
    {
        Harness h = Build(id => Cfg(id),
            submitThrows: new ReferenceAudioFormatException(ReferenceAudioFormat.ReuploadMessage, "reference"));
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        result.Status.Should().Be(StepStatus.Failed);
        Failed(result).Should().Be("AVATAR_REFERENCE_AUDIO_FORMAT");
        ReelBolt.WorkflowEngine.Services.Billing.FailureClassifier.ForCode("AVATAR_REFERENCE_AUDIO_FORMAT").Should().Be(FailureClass.UserInput);
        result.ErrorDetails.Should().Contain("Re-upload").And.Contain("WAV or MP3").And.NotContain("outcome unknown");
        using var db = h.Factory.CreateDbContext();
        db.ExternalGenerationJobs.Single().Status.Should().Be(ExternalGenerationJobStatus.Cancelled,
            "nothing reached the vendor, so the reservation is released, not charged");
    }

    [Fact]
    public async Task A_reference_whose_bytes_changed_since_the_consent_is_refused()
    {
        Harness h = Build(id => Cfg(id), faceDigestOverride: new string('0', 64));
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarFailureCodes.ConsentMissing);
        result.ErrorDetails.Should().Contain("changed after the consent");
        NothingReserved(h);
    }

    [Fact]
    public async Task The_request_carries_the_references_the_ledger_the_avatar_and_the_price_the_input_material()
    {
        Harness h = Build(id => Cfg(id, extra: ",\"model\":\"MiniMax-H3\""));
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        VideoGenerationRequest sent = h.Sent.Should().ContainSingle().Subject;
        sent.Images.Should().BeNull();
        sent.References.Should().HaveCount(2);
        sent.References![0].Kind.Should().Be(AvatarReferenceKind.Image);
        sent.References[0].Bytes.Should().Equal(FacePng);
        sent.References[1].Kind.Should().Be(AvatarReferenceKind.Audio);

        using var db = h.Factory.CreateDbContext();
        ExternalGenerationJob job = db.ExternalGenerationJobs.Single();
        job.AvatarId.Should().Be(h.Avatars.Avatar.Id);
        // 6 s x $0.08 = $0.48 output; one picture and 4 s of audio add nothing (first 5 free, audio free).
        job.CostEstimate.Should().Be(CreditCurrency.CreditsFromUsd(0.48m));

        JsonNode avatar = JsonNode.Parse(result.Output)!["meta"]!["avatar"]!;
        avatar["avatarId"]!.GetValue<string>().Should().Be(h.Avatars.Avatar.Id.ToString("D"));
        avatar["aiGenerated"]!.GetValue<bool>().Should().BeTrue();
        avatar["marking"]!.GetValue<string>().Should().Be("metadata+visibleLabel");
        avatar["referencesSent"]!["images"]!.GetValue<int>().Should().Be(1);
        avatar["fingerprint"]!.GetValue<string>().Should().Be(AvatarFingerprint.Compute(h.Avatars.Avatar));
        result.Output.Should().NotContain("anna.png", "no storage key reaches the output");
        result.Output.Should().NotContain("Anna Example");
    }

    [Fact]
    public async Task A_role_subset_sends_only_those_roles_and_reports_the_rest()
    {
        Harness h = Build(id => Cfg(id, extra: ",\"avatarReferenceRoles\":[\"Face\"]"));
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Single().References.Should().ContainSingle().Which.Kind.Should().Be(AvatarReferenceKind.Image);
        JsonNode skipped = JsonNode.Parse(result.Output)!["meta"]!["avatar"]!["referencesSkipped"]![0]!;
        skipped["reason"]!.GetValue<string>().Should().Be(VideoReferenceInput.SkipRoleNotRequested);
    }

    [Fact]
    public async Task A_revoke_during_the_run_stops_the_next_take_and_fails_the_step_with_the_avatar_code()
    {
        Harness h = Build(id => Cfg(id, takes: 2), afterSubmit: a => a.Revoke());
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Should().ContainSingle("the second take re-resolves the avatar before its submit and is refused");
        result.Status.Should().Be(StepStatus.Failed);
        Failed(result).Should().Be(AvatarFailureCodes.ConsentRevoked);
        using var db = h.Factory.CreateDbContext();
        db.ExternalGenerationJobs.Single(j => j.ClipId == "clip-2").Status.Should().Be(ExternalGenerationJobStatus.Cancelled,
            "a take that was never submitted is released, not charged");
    }

    [Fact]
    public async Task An_avatar_changed_during_the_run_stops_the_next_take()
    {
        Harness h = Build(id => Cfg(id, takes: 2),
            afterSubmit: a => a.Avatar = a.Avatar with { ConsentAttestedAt = DateTime.UtcNow });
        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Should().ContainSingle();
        Failed(result).Should().Be(AvatarFailureCodes.ConsentMissing);
    }

    [Fact]
    public async Task A_clip_of_the_avatar_is_hashed_apart_from_the_same_prompt_without_it()
    {
        Harness with = Build(id => Cfg(id));
        await with.Executor.ExecuteAsync(with.Context);
        using var db = with.Factory.CreateDbContext();
        db.ExternalGenerationJobs.Single().RequestHash.Should().NotBe(VideoGenerateStepExecutorTests.DefaultHash(1));
    }

    // ---------------------------------------------------------------- reuse guard

    [Fact]
    public async Task The_reuse_guard_serves_a_clip_only_while_the_avatar_is_usable_and_unchanged()
    {
        Guid project = Guid.NewGuid();
        var avatars = new FakeAvatars { Avatar = UsableAvatar(project, Sha(FacePng)) };
        var guard = new AvatarReuseGuard(avatars);
        string config = Cfg(avatars.Avatar.Id);
        string Output(string fp) => "{\"clips\":[],\"meta\":{\"avatar\":{\"avatarId\":\"" + avatars.Avatar.Id + "\",\"fingerprint\":\"" + fp + "\"}}}";
        string fingerprint = AvatarFingerprint.Compute(avatars.Avatar);

        (await guard.IsReuseAllowedAsync(project, config, Output(fingerprint), default)).Should().BeTrue();
        (await guard.IsReuseAllowedAsync(project, config, Output("other"), default)).Should().BeFalse("re-attested references");
        (await guard.IsReuseAllowedAsync(project, config, """{"clips":[],"meta":{}}""", default)).Should().BeFalse("made without the avatar");
        (await guard.IsReuseAllowedAsync(project, """{"maxSpendCredits":1}""", """{"clips":[],"meta":{}}""", default)).Should().BeTrue();
        (await guard.IsReuseAllowedAsync(project, """{"maxSpendCredits":1}""", Output(fingerprint), default)).Should().BeFalse();
        avatars.Revoke();
        (await guard.IsReuseAllowedAsync(project, config, Output(fingerprint), default)).Should().BeFalse("consent withdrawn");
    }

    [Fact]
    public void The_cache_key_gains_the_avatar_only_when_set()
    {
        var builder = new StepCacheKeyBuilder();
        var plain = new StepCacheKeyInputs
        {
            ProjectId = Guid.NewGuid(),
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = "{}",
            AgentDefinitionId = Guid.Empty,
            AgentInputContextMode = default,
            AgentType = AgentType.VideoTransform,
            MaxIterations = 0,
            ProjectFileFingerprint = "fp"
        };
        string before = builder.Build(plain);
        builder.Build(plain with { AvatarConsent = null }).Should().Be(before);
        string a = builder.Build(plain with { AvatarConsent = "fp-a" });
        a.Should().NotBe(before);
        builder.Build(plain with { AvatarConsent = "unusable:consent_revoked" }).Should().NotBe(a);
    }

    // ---------------------------------------------------------------- marking

    [Fact]
    public async Task An_avatar_clip_is_marked_in_metadata_and_labelled_when_the_avatar_says_visible()
    {
        List<IReadOnlyList<string>> calls = [];
        var runner = new Mock<IVideoToolRunner>();
        runner.Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, """{"streams":[{"codec_type":"video","codec_name":"h264","width":1280,"height":720,"r_frame_rate":"24/1","pix_fmt":"yuv420p"}],"format":{"duration":"6.0"}}""", "", false));
        runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<string> args, TimeSpan _, CancellationToken _) => calls.Add(args))
            .ReturnsAsync(new VideoToolResult(0, "", "", false));
        var normalizer = new GeneratedClipNormalizer(runner.Object, Options.Create(new VideoEditingOptions()), NullLogger<GeneratedClipNormalizer>.Instance);

        await normalizer.NormalizeAsync("/tmp/in.mp4", "/tmp/plain.mp4", CancellationToken.None, null, disclosure: null);
        await normalizer.NormalizeAsync("/tmp/in.mp4", "/tmp/meta.mp4", CancellationToken.None, null, new GeneratedClipDisclosure(false));
        await normalizer.NormalizeAsync("/tmp/in.mp4", "/tmp/label.mp4", CancellationToken.None, null, new GeneratedClipDisclosure(true));

        string plain = string.Join(' ', calls[0]);
        plain.Should().NotContain("-metadata").And.NotContain("use_metadata_tags").And.NotContain("drawtext");
        string meta = string.Join(' ', calls[1]);
        meta.Should().Contain("ai_generated=true").And.Contain("+faststart+use_metadata_tags").And.NotContain("drawtext");
        string label = string.Join(' ', calls[2]);
        label.Should().Contain("ai_generated=true").And.Contain("drawtext=").And.Contain("text='AI-generated'");
    }

    [Fact]
    public async Task The_only_provenance_signer_today_signs_nothing_and_says_so()
    {
        (await new NoProvenanceSigner().SignAsync("/tmp/x.mp4", new GeneratedClipProvenanceClaim("ReelBolt", "MiniMax-H3", true), default))
            .Should().Be(ProvenanceOutcome.NotAvailable);
    }

    // ---------------------------------------------------------------- hold

    [Fact]
    public void The_hold_prices_the_most_input_material_the_avatar_limits_allow()
    {
        // Eight pictures (3 beyond the free 5 at $0.04) and 15 s of input video at $0.08.
        VideoGenerateCreditPricing.MaxInputMaterialUsd(RealMiniMax, "MiniMax-H3", "768P").Should().Be(3 * 0.04m + 15 * 0.08m);
        VideoGenerateCreditPricing.MaxInputMaterialUsd(new Mock<IVideoGenerationClient>().Object, "x", "768P").Should().Be(0m);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] Png(int width, int height)
    {
        byte[] b = new byte[64];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        sig.CopyTo(b, 0);
        b[16] = (byte)(width >> 24); b[17] = (byte)(width >> 16); b[18] = (byte)(width >> 8); b[19] = (byte)width;
        b[20] = (byte)(height >> 24); b[21] = (byte)(height >> 16); b[22] = (byte)(height >> 8); b[23] = (byte)height;
        return b;
    }
}
