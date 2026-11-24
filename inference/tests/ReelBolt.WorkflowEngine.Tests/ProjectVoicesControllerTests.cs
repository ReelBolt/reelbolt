#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The project voice library (docs/voiceover.md "Voices and consent"): consent is captured verbatim
/// or nothing is created; a voice is project-scoped; revocation makes it unselectable; deletion
/// removes the vendor model and the reference recordings and reports what it removed; retention is
/// enforced by the sweep, not just documented.
/// </summary>
// Clone reads the process-wide REELBOLT_VENDOR_DELETES_DISABLED flag, and one test below sets it: share
// VendorDeleteGuardTests' non-parallel collection so no other test observes the flag mid-change.
[Collection("VendorDeleteEnv")]
public class ProjectVoicesControllerTests
{
    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "owner@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public System.Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    /// <summary>A Fish-shaped client: synthesizes nothing, records clone/delete calls.</summary>
    private sealed class FakeCloningClient : ISpeechSynthesisClient, IVoiceCloningClient
    {
        public List<VoiceCloneRequest> Clones { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool DeleteSucceeds { get; set; } = true;
        public VoiceCloningRequirements Requirements { get; set; } = VoiceCloningRequirements.Hosted;

        public Task<VoiceCloningRequirements> GetRequirementsAsync(CancellationToken ct) => Task.FromResult(Requirements);

        public Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CreatedVoice> CreateVoiceAsync(VoiceCloneRequest request, CancellationToken ct)
        {
            Clones.Add(request);
            return Task.FromResult(new CreatedVoice("fish-model-123", "trained"));
        }

        public Task<bool> DeleteVoiceAsync(string remoteVoiceId, CancellationToken ct)
        {
            Deleted.Add(remoteVoiceId);
            return Task.FromResult(DeleteSucceeds);
        }
    }

    /// <summary>A synthesis-only client (the OpenAI-compatible shape): no cloning API at all.</summary>
    private sealed class SynthesisOnlyClient : ISpeechSynthesisClient
    {
        public Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class Harness
    {
        public InferenceApiDbContext Db { get; }
        public Guid OwnerId { get; } = Guid.NewGuid();
        public Project Project { get; }
        public ModelCapabilityView Provider { get; }
        public Mock<IFileStorageService> Storage { get; } = new();
        public Mock<IInferenceProviderResolver> Resolver { get; } = new();
        public Mock<ISpeechSynthesisClientFactory> Factory { get; } = new();
        public Mock<ITranscriptionClientFactory> Transcription { get; } = new();
        public Mock<ITranscriptionClient> Asr { get; } = new();
        public List<string> TranscribedFiles { get; } = [];
        public string TranscriptText { get; set; } = "Hello, this is a reference recording.";
        public FakeCloningClient Cloner { get; } = new();
        public List<string> DeletedStorageKeys { get; } = [];

        public Harness(bool clonerSupportsCloning = true, ReelBolt.Shared.Metering.IUsageRecorder? recorder = null)
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = OwnerId, OrganizationId = TestTenancy.OrgOf(OwnerId) };
            Provider = new ModelCapabilityView
            {
                Id = Guid.NewGuid(),
                Name = "fish",
                Kind = InferenceProviderKind.FishAudio,
                Capability = InferenceProviderCapability.SpeechSynthesis,
                Endpoint = "https://api.fish.audio",
                ModelName = "s2.1-pro"
            };
            Db.Projects.Add(Project);
            Db.AddFlat(Provider);
            Db.SaveChanges();

            var resolved = new ResolvedSpeechSynthesisProvider(Provider.Id, "fish", InferenceProviderKind.FishAudio, "https://api.fish.audio", "s2.1-pro", "k", 30);
            Resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(resolved);
            Factory.Setup(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>()))
                .Returns(() =>
                {
                    ISpeechSynthesisClient inner = clonerSupportsCloning ? Cloner : new SynthesisOnlyClient();
                    // Same wrapping the real SpeechSynthesisClientFactory.Get applies when a recorder is registered.
                    return recorder is null ? inner : new ReelBolt.Shared.Metering.MeteringSpeechSynthesisClient(inner, resolved, recorder);
                });
            Storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.ASCII.GetBytes("RIFF....WAVE")));
            Storage.Setup(s => s.DeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, string, CancellationToken>((_, key, _) => DeletedStorageKeys.Add(key))
                .Returns(Task.CompletedTask);
            Transcription.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(Asr.Object);
            Asr.Setup(a => a.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Stream _, string name, string? _, bool _, CancellationToken _) =>
                {
                    TranscribedFiles.Add(name);
                    return new TranscriptResult(TranscriptText, [], [], "en");
                });
        }

        public void UseDefaultTranscriptionProvider() =>
            Resolver.Setup(r => r.ResolveTranscriptionAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedTranscriptionProvider(Guid.NewGuid(), "whisper", InferenceProviderKind.OpenAICompatible, "http://whisper:8000/v1", "whisper-1", "", 30));

        public ProjectVoicesController Controller(Guid? asUser = null, ReelBolt.Inference.Api.Services.Moderation.PublishModeration? moderation = null) =>
            new(Db, new FakeCurrentUser(asUser ?? OwnerId), TestTenancy.Access(Db, new FakeCurrentUser(asUser ?? OwnerId)), Storage.Object, Resolver.Object, Factory.Object,
                Transcription.Object, NullLogger<ProjectVoicesController>.Instance, publishModeration: moderation);

        public ProjectFile AddAudioFile(string name = "take.wav")
        {
            var file = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = name,
                MimeType = "audio/wav",
                StorageKey = $"projects/{Project.Id}/userFiles/{Guid.NewGuid():N}.wav",
                SizeBytes = 1200,
                Category = "userFiles"
            };
            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        public CloneVoiceRequest ValidClone(params Guid[] fileIds) => new(
            Name: "Presenter",
            ReferenceProjectFileIds: fileIds,
            ConsentStatement: ProjectVoicesController.RequiredConsentStatement,
            ConsentSubjectName: "Dana Presenter",
            InferenceProviderId: Provider.Id,
            RetentionDays: 90);
    }

    [Fact]
    public async Task Clone_without_the_verbatim_consent_statement_creates_nothing_anywhere()
    {
        var h = new Harness();
        ProjectFile file = h.AddAudioFile();

        ActionResult<ProjectVoiceResponse> weaker = await h.Controller().Clone(
            h.Project.Id, h.ValidClone(file.Id) with { ConsentStatement = "they said it was fine" }, CancellationToken.None);
        ActionResult<ProjectVoiceResponse> noSubject = await h.Controller().Clone(
            h.Project.Id, h.ValidClone(file.Id) with { ConsentSubjectName = " " }, CancellationToken.None);

        weaker.Result.Should().BeOfType<BadRequestObjectResult>();
        noSubject.Result.Should().BeOfType<BadRequestObjectResult>();
        h.Cloner.Clones.Should().BeEmpty("no byte leaves the host without consent");
        h.Storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await h.Db.ProjectVoices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Clone_refuses_a_recording_that_is_an_output_without_a_verdict_and_sends_nothing()
    {
        var h = new Harness();
        ProjectFile file = h.AddAudioFile();
        file.StorageKey = $"projects/{h.Project.Id}/outputFiles/voice.wav";
        file.Category = "outputFiles";
        h.Db.SaveChanges();
        Mock<IFileStorageService> moderated = new();
        moderated.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(1200, "audio/wav", "\"e1\""));
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration moderation = new(
            new Mock<ReelBolt.Shared.Moderation.IContentModerationGate>().Object, moderated.Object,
            new Mock<ReelBolt.Shared.Moderation.IModerationMediaSampler>().Object,
            new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object, h.Db,
            Microsoft.Extensions.Options.Options.Create(new ReelBolt.Shared.Moderation.ContentModerationOptions { Enabled = true }),
            NullLogger<ReelBolt.Inference.Api.Services.Moderation.PublishModeration>.Instance);

        ActionResult<ProjectVoiceResponse> result = await h.Controller(moderation: moderation).Clone(
            h.Project.Id, h.ValidClone(file.Id), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
        h.Cloner.Clones.Should().BeEmpty("the recording never reached the voice vendor");
        h.Storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Clone_records_the_attestation_verbatim_sends_the_project_recordings_and_is_selectable()
    {
        var h = new Harness();
        ProjectFile file = h.AddAudioFile();

        ActionResult<ProjectVoiceResponse> result = await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var response = created.Value.Should().BeOfType<ProjectVoiceResponse>().Subject;
        response.Source.Should().Be("Cloned");
        response.RemoteVoiceId.Should().Be("fish-model-123");
        response.Selectable.Should().BeTrue();
        response.ConsentAttestedByUserId.Should().Be(h.OwnerId);
        response.ConsentAttestedAt.Should().NotBeNull();
        response.ConsentStatement.Should().Be(ProjectVoicesController.RequiredConsentStatement);
        response.ConsentSubjectName.Should().Be("Dana Presenter");
        response.RetentionUntil.Should().BeCloseTo(DateTime.UtcNow.AddDays(90), TimeSpan.FromMinutes(1));
        response.ReferenceProjectFileIds.Should().Equal(file.Id);

        h.Cloner.Clones.Should().ContainSingle().Which.References.Should().ContainSingle()
            .Which.FileName.Should().Be("take.wav");
    }

    private sealed class RecordingUsage : ReelBolt.Shared.Metering.IUsageRecorder
    {
        public List<ReelBolt.Shared.Metering.UsageRecordRequest> Requests { get; } = [];

        public ValueTask<long> RecordAsync(ReelBolt.Shared.Metering.UsageRecordRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(0L);
        }
    }

    [Fact]
    public async Task Clone_through_the_metering_wrapper_reaches_the_vendor_cloner_and_records_no_usage()
    {
        RecordingUsage usage = new();
        var h = new Harness(recorder: usage);
        ProjectFile file = h.AddAudioFile();

        ActionResult<ProjectVoiceResponse> result = await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        h.Cloner.Clones.Should().ContainSingle();
        usage.Requests.Should().BeEmpty("cloning is an unmetered project-screen action");
    }

    [Fact]
    public async Task Delete_through_the_metering_wrapper_reaches_the_vendor_cloner_and_records_no_usage()
    {
        RecordingUsage usage = new();
        var h = new Harness(recorder: usage);
        ProjectFile file = h.AddAudioFile();
        await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None);
        Guid voiceId = (await h.Db.ProjectVoices.SingleAsync()).Id;

        var deleted = await h.Controller().Delete(h.Project.Id, voiceId, CancellationToken.None);

        deleted.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DeleteVoiceResponse>()
            .Subject.RemoteModelDeleted.Should().BeTrue();
        h.Cloner.Deleted.Should().Equal("fish-model-123");
        usage.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Clone_through_the_metering_wrapper_of_a_synthesis_only_client_is_still_cloning_not_supported()
    {
        var h = new Harness(clonerSupportsCloning: false, recorder: new RecordingUsage());
        ProjectFile file = h.AddAudioFile();

        var bad = (await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>().Subject;

        bad.Value!.ToString().Should().Contain("cloning_not_supported");
    }

    [Fact]
    public void The_real_factory_wraps_a_Fish_client_and_Unwrap_still_finds_the_cloner_and_library()
    {
        RecordingUsage usage = new();
        var factory = new SpeechSynthesisClientFactory(usage);
        var provider = new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "fish", InferenceProviderKind.FishAudio, "https://api.fish.audio", "s2.1-pro", "k", 30);
        var openAi = new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "oai", InferenceProviderKind.OpenAICompatible, "http://tts:8000/v1", "m", "", 30);

        ISpeechSynthesisClient fish = factory.Get(provider);
        ISpeechSynthesisClient oai = factory.Get(openAi);

        (fish as IVoiceCloningClient).Should().BeNull("the wrapper must not claim the vendor interfaces");
        fish.Unwrap<IVoiceCloningClient>().Should().NotBeNull();
        fish.Unwrap<IVoiceLibraryClient>().Should().NotBeNull();
        oai.Unwrap<IVoiceCloningClient>().Should().BeNull();
        oai.Unwrap<IVoiceLibraryClient>().Should().BeNull();
    }

    [Fact]
    public async Task Synthesis_through_the_wrapper_still_records_tts_input_bytes()
    {
        RecordingUsage usage = new();
        var inner = new Mock<ISpeechSynthesisClient>();
        inner.Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SynthesizedSpeech(new MemoryStream(), "audio/mpeg"));
        var provider = new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "fish", InferenceProviderKind.FishAudio, "https://api.fish.audio", "s2.1-pro", "k", 30);
        ISpeechSynthesisClient wrapped = new ReelBolt.Shared.Metering.MeteringSpeechSynthesisClient(inner.Object, provider, usage);

        await wrapped.SynthesizeAsync(new SpeechRequest("héllo", null, null), CancellationToken.None);

        usage.Requests.Should().ContainSingle().Which.Quantity.Should().Be(6);
        usage.Requests[0].Kind.Should().Be(UsageKind.TtsInputBytes);
    }

    private sealed class Wrapper(ISpeechSynthesisClient inner) : ISpeechSynthesisClient, IWrappedSpeechSynthesisClient
    {
        public ISpeechSynthesisClient Inner { get; } = inner;
        public Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct) => Inner.SynthesizeAsync(request, ct);
    }

    private sealed class SelfWrapper : ISpeechSynthesisClient, IWrappedSpeechSynthesisClient
    {
        public ISpeechSynthesisClient Inner => this;
        public Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void Unwrap_finds_direct_wrapped_and_nested_clients_and_returns_null_otherwise()
    {
        var cloner = new FakeCloningClient();
        cloner.Unwrap<IVoiceCloningClient>().Should().BeSameAs(cloner);
        new Wrapper(cloner).Unwrap<IVoiceCloningClient>().Should().BeSameAs(cloner);
        new Wrapper(new Wrapper(new Wrapper(cloner))).Unwrap<IVoiceCloningClient>().Should().BeSameAs(cloner);
        new Wrapper(new SynthesisOnlyClient()).Unwrap<IVoiceCloningClient>().Should().BeNull();
        new SynthesisOnlyClient().Unwrap<IVoiceCloningClient>().Should().BeNull();
        ((ISpeechSynthesisClient?)null).Unwrap<IVoiceCloningClient>().Should().BeNull();
        new SelfWrapper().Unwrap<IVoiceCloningClient>().Should().BeNull("a cyclic chain is bounded");
    }

    [Fact]
    public async Task Clone_refuses_another_projects_recording_and_a_provider_without_a_cloning_api()
    {
        var h = new Harness();
        var otherProject = new Project { Id = Guid.NewGuid(), Name = "Other", OwnerId = h.OwnerId, OrganizationId = TestTenancy.OrgOf(h.OwnerId) };
        h.Db.Projects.Add(otherProject);
        var foreignFile = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = otherProject.Id,
            OriginalFileName = "x.wav",
            MimeType = "audio/wav",
            StorageKey = "projects/other/x.wav",
            SizeBytes = 10
        };
        h.Db.ProjectFiles.Add(foreignFile);
        await h.Db.SaveChangesAsync();

        ActionResult<ProjectVoiceResponse> foreign = await h.Controller().Clone(h.Project.Id, h.ValidClone(foreignFile.Id), CancellationToken.None);
        foreign.Result.Should().BeOfType<BadRequestObjectResult>();
        h.Cloner.Clones.Should().BeEmpty();

        var noCloning = new Harness(clonerSupportsCloning: false);
        ProjectFile file = noCloning.AddAudioFile();
        ActionResult<ProjectVoiceResponse> unsupported = await noCloning.Controller().Clone(noCloning.Project.Id, noCloning.ValidClone(file.Id), CancellationToken.None);
        var bad = unsupported.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("cloning_not_supported");
    }

    [Fact]
    public async Task A_self_hosted_server_gets_one_wav_and_the_default_transcription_providers_transcript()
    {
        var h = new Harness();
        h.Cloner.Requirements = VoiceCloningRequirements.SelfHostedFishSpeech;
        h.UseDefaultTranscriptionProvider();
        ProjectFile file = h.AddAudioFile();

        ActionResult<ProjectVoiceResponse> result = await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None);

        var response = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject.Value.Should().BeOfType<ProjectVoiceResponse>().Subject;
        response.Selectable.Should().BeTrue();
        response.ConsentStatement.Should().Be(ProjectVoicesController.RequiredConsentStatement);
        h.TranscribedFiles.Should().Equal("take.wav");
        h.Cloner.Clones.Should().ContainSingle().Which.References.Should().ContainSingle()
            .Which.Transcript.Should().Be("Hello, this is a reference recording.");
    }

    [Fact]
    public async Task Clone_is_409_vendor_writes_disabled_and_sends_nothing_when_vendor_deletes_are_disabled()
    {
        const string flag = "REELBOLT_VENDOR_DELETES_DISABLED";
        string? old = Environment.GetEnvironmentVariable(flag);
        try
        {
            Environment.SetEnvironmentVariable(flag, "true");
            var h = new Harness();
            ProjectFile file = h.AddAudioFile();

            var result = (await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None)).Result;

            ObjectResult refused = result.Should().BeOfType<ObjectResult>().Subject;
            refused.StatusCode.Should().Be(409);
            JsonSerializer.SerializeToElement(refused.Value).GetProperty("error").GetString().Should().Be("vendor_writes_disabled");
            h.Cloner.Clones.Should().BeEmpty("a voice cloned on a prod copy could never be deleted at the vendor");
            (await h.Db.ProjectVoices.CountAsync()).Should().Be(0);
        }
        finally { Environment.SetEnvironmentVariable(flag, old); }
    }

    [Fact]
    public async Task A_clone_on_an_unpriced_speech_model_is_409_MODEL_UNPRICED_and_sends_nothing()
    {
        var h = new Harness();
        var refusal = new ModelUnpricedException(InferenceProviderCapability.SpeechSynthesis, "fish");
        h.Resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
        ProjectFile file = h.AddAudioFile();

        var result = (await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None)).Result;

        ConflictObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonElement body = JsonSerializer.SerializeToElement(conflict.Value);
        body.GetProperty("code").GetString().Should().Be("MODEL_UNPRICED");
        body.GetProperty("message").GetString().Should().Be(refusal.Message);
        h.Cloner.Clones.Should().BeEmpty();
    }

    [Fact]
    public async Task A_self_hosted_clone_whose_transcription_model_is_unpriced_is_409_MODEL_UNPRICED()
    {
        var h = new Harness();
        h.Cloner.Requirements = VoiceCloningRequirements.SelfHostedFishSpeech;
        h.Resolver.Setup(r => r.ResolveTranscriptionAsync(null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ModelUnpricedException(InferenceProviderCapability.Transcription, "whisper"));
        ProjectFile file = h.AddAudioFile();

        var result = (await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None)).Result;

        JsonSerializer.SerializeToElement(result.Should().BeOfType<ConflictObjectResult>().Subject.Value)
            .GetProperty("code").GetString().Should().Be("MODEL_UNPRICED");
        h.Cloner.Clones.Should().BeEmpty();
    }

    [Fact]
    public async Task The_hosted_api_is_never_sent_to_a_transcription_provider()
    {
        var h = new Harness();
        h.UseDefaultTranscriptionProvider();
        ProjectFile file = h.AddAudioFile();

        await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None);

        h.TranscribedFiles.Should().BeEmpty("the hosted API transcribes its own recordings");
        h.Cloner.Clones.Should().ContainSingle().Which.References.Single().Transcript.Should().BeNull();
    }

    [Fact]
    public async Task A_self_hosted_clone_is_refused_before_anything_is_sent_when_its_requirements_cannot_be_met()
    {
        // Two recordings: the server keeps one per voice.
        var two = new Harness();
        two.Cloner.Requirements = VoiceCloningRequirements.SelfHostedFishSpeech;
        two.UseDefaultTranscriptionProvider();
        ProjectFile a = two.AddAudioFile("a.wav");
        ProjectFile b = two.AddAudioFile("b.wav");
        var tooMany = (await two.Controller().Clone(two.Project.Id, two.ValidClone(a.Id, b.Id), CancellationToken.None)).Result;
        tooMany.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("too_many_reference_files");
        two.Storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Not a WAV.
        var mp3 = new Harness();
        mp3.Cloner.Requirements = VoiceCloningRequirements.SelfHostedFishSpeech;
        mp3.UseDefaultTranscriptionProvider();
        mp3.Storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.ASCII.GetBytes("ID3\u0004not a wav at all")));
        ProjectFile song = mp3.AddAudioFile("take.mp3");
        var notWav = (await mp3.Controller().Clone(mp3.Project.Id, mp3.ValidClone(song.Id), CancellationToken.None)).Result;
        notWav.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("reference_file_not_wav");
        mp3.TranscribedFiles.Should().BeEmpty();

        // No default Transcription provider.
        var noAsr = new Harness();
        noAsr.Cloner.Requirements = VoiceCloningRequirements.SelfHostedFishSpeech;
        ProjectFile take = noAsr.AddAudioFile();
        var missing = (await noAsr.Controller().Clone(noAsr.Project.Id, noAsr.ValidClone(take.Id), CancellationToken.None)).Result;
        missing.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("transcription_provider_required");

        // Silence: nothing recognized.
        var silent = new Harness { TranscriptText = "   " };
        silent.Cloner.Requirements = VoiceCloningRequirements.SelfHostedFishSpeech;
        silent.UseDefaultTranscriptionProvider();
        ProjectFile quiet = silent.AddAudioFile();
        var noSpeech = (await silent.Controller().Clone(silent.Project.Id, silent.ValidClone(quiet.Id), CancellationToken.None)).Result;
        noSpeech.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("reference_has_no_speech");

        foreach (Harness h in new[] { two, mp3, noAsr, silent })
        {
            h.Cloner.Clones.Should().BeEmpty();
            (await h.Db.ProjectVoices.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task A_self_hosted_clone_without_consent_never_reaches_the_transcription_provider()
    {
        var h = new Harness();
        h.Cloner.Requirements = VoiceCloningRequirements.SelfHostedFishSpeech;
        h.UseDefaultTranscriptionProvider();
        ProjectFile file = h.AddAudioFile();

        var result = (await h.Controller().Clone(
            h.Project.Id, h.ValidClone(file.Id) with { ConsentStatement = "ok" }, CancellationToken.None)).Result;

        result.Should().BeOfType<BadRequestObjectResult>();
        h.TranscribedFiles.Should().BeEmpty();
        h.Cloner.Clones.Should().BeEmpty();
    }

    [Fact]
    public async Task Voices_are_owner_scoped_and_project_scoped()
    {
        var h = new Harness();
        ProjectFile file = h.AddAudioFile();
        await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None);
        Guid voiceId = (await h.Db.ProjectVoices.SingleAsync()).Id;

        (await h.Controller(asUser: Guid.NewGuid()).List(h.Project.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller().List(Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();

        var otherProject = new Project { Id = Guid.NewGuid(), Name = "Other", OwnerId = h.OwnerId, OrganizationId = TestTenancy.OrgOf(h.OwnerId) };
        h.Db.Projects.Add(otherProject);
        await h.Db.SaveChangesAsync();
        (await h.Controller().Get(otherProject.Id, voiceId, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>(
            "a voice is never resolvable from another project, even the same owner's");
    }

    [Fact]
    public async Task Revoking_consent_makes_the_voice_unselectable_with_the_reason_and_keeps_the_record()
    {
        var h = new Harness();
        ProjectFile file = h.AddAudioFile();
        await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None);
        Guid voiceId = (await h.Db.ProjectVoices.SingleAsync()).Id;

        ActionResult<ProjectVoiceResponse> revoked = await h.Controller().RevokeConsent(h.Project.Id, voiceId, CancellationToken.None);

        var response = revoked.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<ProjectVoiceResponse>().Subject;
        response.Selectable.Should().BeFalse();
        response.UnselectableReason.Should().Be("consent_revoked");
        response.ConsentRevokedAt.Should().NotBeNull();
        response.ConsentAttestedAt.Should().NotBeNull("the attestation stays on record");

        var listed = (await h.Controller().List(h.Project.Id, CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<List<ProjectVoiceResponse>>().Subject;
        listed.Should().ContainSingle().Which.Selectable.Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_cloned_voice_removes_the_vendor_model_the_reference_recordings_and_the_row_and_reports_each()
    {
        var h = new Harness();
        ProjectFile file = h.AddAudioFile();
        await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None);
        Guid voiceId = (await h.Db.ProjectVoices.SingleAsync()).Id;

        ActionResult<DeleteVoiceResponse> deleted = await h.Controller().Delete(h.Project.Id, voiceId, CancellationToken.None);

        var response = deleted.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DeleteVoiceResponse>().Subject;
        response.RowDeleted.Should().BeTrue();
        response.RemoteModelDeleted.Should().BeTrue();
        response.ReferenceFilesDeleted.Should().Be(1);
        h.Cloner.Deleted.Should().Equal("fish-model-123");
        h.DeletedStorageKeys.Should().Equal(file.StorageKey);
        (await h.Db.ProjectVoices.CountAsync()).Should().Be(0);
        (await h.Db.ProjectFiles.CountAsync(f => f.Id == file.Id)).Should().Be(0, "the biometric material is gone, not hidden");
    }

    [Fact]
    public async Task Deleting_when_the_vendor_refuses_still_removes_the_row_and_says_the_vendor_refused()
    {
        var h = new Harness();
        h.Cloner.DeleteSucceeds = false;
        ProjectFile file = h.AddAudioFile();
        await h.Controller().Clone(h.Project.Id, h.ValidClone(file.Id), CancellationToken.None);
        Guid voiceId = (await h.Db.ProjectVoices.SingleAsync()).Id;

        var response = (await h.Controller().Delete(h.Project.Id, voiceId, CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DeleteVoiceResponse>().Subject;

        response.RowDeleted.Should().BeTrue();
        response.RemoteModelDeleted.Should().BeFalse();
        response.RemoteDeletionNote.Should().Be("vendor_refused");
    }

    [Fact]
    public async Task Library_voices_need_no_consent_and_are_never_deleted_at_the_vendor()
    {
        var h = new Harness();

        var created = (await h.Controller().CreateLibraryVoice(h.Project.Id,
                new CreateLibraryVoiceRequest("Narrator", "lib-voice-7", h.Provider.Id), CancellationToken.None)).Result
            .Should().BeOfType<CreatedAtActionResult>().Subject.Value.Should().BeOfType<ProjectVoiceResponse>().Subject;
        created.Source.Should().Be("Library");
        created.Selectable.Should().BeTrue();
        created.ConsentAttestedAt.Should().BeNull();
        created.RetentionUntil.Should().BeNull();

        var deleted = (await h.Controller().Delete(h.Project.Id, created.Id, CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DeleteVoiceResponse>().Subject;
        deleted.RemoteModelDeleted.Should().BeFalse();
        deleted.RemoteDeletionNote.Should().Be("library_voice_not_owned");
        h.Cloner.Deleted.Should().BeEmpty("a library voice is the vendor's, not ours to delete");
    }

    /// <summary>AUTHZ-IA-1: registering another project's clone id as a Library voice would skip that clone's consent.</summary>
    [Fact]
    public async Task A_library_voice_cannot_carry_the_vendor_id_of_another_projects_clone()
    {
        var h = new Harness();
        var sibling = new Project { Id = Guid.NewGuid(), Name = "Sibling", OwnerId = h.OwnerId, OrganizationId = h.Project.OrganizationId };
        h.Db.Projects.Add(sibling);
        h.Db.ProjectVoices.Add(new ProjectVoice
        {
            Id = Guid.NewGuid(),
            ProjectId = sibling.Id,
            InferenceProviderId = h.Provider.Id,
            Name = "Dana",
            Source = ProjectVoiceSource.Cloned,
            RemoteVoiceId = "clone-of-dana",
            ConsentAttestedAt = DateTime.UtcNow,
            ConsentAttestedByUserId = h.OwnerId,
            ConsentStatement = ProjectVoicesController.RequiredConsentStatement,
            ConsentSubjectName = "Dana Presenter",
            CreatedAt = DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();

        (await h.Controller().CreateLibraryVoice(h.Project.Id,
                new CreateLibraryVoiceRequest("Narrator", " clone-of-dana ", h.Provider.Id), CancellationToken.None)).Result
            .Should().BeOfType<BadRequestObjectResult>();
        (await h.Db.ProjectVoices.CountAsync(v => v.ProjectId == h.Project.Id)).Should().Be(0);

        (await h.Controller().CreateLibraryVoice(h.Project.Id,
                new CreateLibraryVoiceRequest("Narrator", "lib-voice-7", h.Provider.Id), CancellationToken.None)).Result
            .Should().BeOfType<CreatedAtActionResult>("a genuine library id is still accepted");
    }

    [Fact]
    public async Task The_retention_sweep_deletes_expired_and_long_revoked_clones_and_leaves_live_ones()
    {
        var h = new Harness();
        ProjectFile expiredFile = h.AddAudioFile("expired.wav");
        ProjectFile revokedFile = h.AddAudioFile("revoked.wav");
        ProjectFile liveFile = h.AddAudioFile("live.wav");
        DateTime now = DateTime.UtcNow;
        h.Db.ProjectVoices.AddRange(
            new ProjectVoice
            {
                Id = Guid.NewGuid(),
                ProjectId = h.Project.Id,
                InferenceProviderId = h.Provider.Id,
                Name = "expired",
                Source = ProjectVoiceSource.Cloned,
                RemoteVoiceId = "m-expired",
                ReferenceProjectFileIdsJson = $"[\"{expiredFile.Id}\"]",
                ConsentAttestedAt = now.AddDays(-400),
                RetentionUntil = now.AddDays(-1),
                CreatedByUserId = h.OwnerId
            },
            new ProjectVoice
            {
                Id = Guid.NewGuid(),
                ProjectId = h.Project.Id,
                InferenceProviderId = h.Provider.Id,
                Name = "revoked",
                Source = ProjectVoiceSource.Cloned,
                RemoteVoiceId = "m-revoked",
                ReferenceProjectFileIdsJson = $"[\"{revokedFile.Id}\"]",
                ConsentAttestedAt = now.AddDays(-100),
                ConsentRevokedAt = now.AddDays(-ProjectVoiceRetentionService.RevokedGraceDays - 1),
                RetentionUntil = now.AddDays(200),
                CreatedByUserId = h.OwnerId
            },
            new ProjectVoice
            {
                Id = Guid.NewGuid(),
                ProjectId = h.Project.Id,
                InferenceProviderId = h.Provider.Id,
                Name = "live",
                Source = ProjectVoiceSource.Cloned,
                RemoteVoiceId = "m-live",
                ReferenceProjectFileIdsJson = $"[\"{liveFile.Id}\"]",
                ConsentAttestedAt = now.AddDays(-10),
                RetentionUntil = now.AddDays(300),
                CreatedByUserId = h.OwnerId
            });
        await h.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(h.Db);
        services.AddSingleton(h.Storage.Object);
        services.AddSingleton(h.Resolver.Object);
        services.AddSingleton(h.Factory.Object);
        using ServiceProvider provider = services.BuildServiceProvider();
        var sweep = new ProjectVoiceRetentionService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ProjectVoiceRetentionService>.Instance);

        int deleted = await sweep.SweepOnceAsync(CancellationToken.None);

        deleted.Should().Be(2);
        h.Cloner.Deleted.Should().BeEquivalentTo(["m-expired", "m-revoked"]);
        h.DeletedStorageKeys.Should().BeEquivalentTo([expiredFile.StorageKey, revokedFile.StorageKey]);
        (await h.Db.ProjectVoices.Select(v => v.Name).ToListAsync()).Should().Equal("live");
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == liveFile.Id)).Should().BeTrue();
    }

    /// <summary>A platform-made audio file made before the project's output marker: its bytes are not served.</summary>
    private static ProjectFile AddNotServedOutput(Harness h)
    {
        h.Project.CsamOutputHoldAt = DateTime.UtcNow.AddHours(-1);
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = h.Project.Id,
            OriginalFileName = "made.wav",
            MimeType = "audio/wav",
            StorageKey = $"projects/{h.Project.Id}/outputFiles/{Guid.NewGuid():N}.wav",
            SizeBytes = 1200,
            Category = "outputFiles",
            OutputProducedAt = DateTime.UtcNow.AddHours(-3),
            UploadedAt = DateTime.UtcNow.AddHours(-3)
        };
        h.Db.ProjectFiles.Add(file);
        h.Db.SaveChanges();
        return file;
    }

    [Fact]
    public async Task Clone_refuses_a_reference_recording_that_is_not_served_before_any_read_or_vendor_call()
    {
        var h = new Harness();
        ProjectFile held = AddNotServedOutput(h);

        ActionResult<ProjectVoiceResponse> result = await h.Controller().Clone(h.Project.Id, h.ValidClone(held.Id), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString().Should().Contain("reference_file_not_found");
        h.Cloner.Clones.Should().BeEmpty();
        h.Storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await h.Db.ProjectVoices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Clone_still_accepts_an_upload_in_a_project_whose_earlier_outputs_are_not_served()
    {
        var h = new Harness();
        AddNotServedOutput(h);
        ProjectFile upload = h.AddAudioFile();

        ActionResult<ProjectVoiceResponse> result = await h.Controller().Clone(h.Project.Id, h.ValidClone(upload.Id), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        h.Cloner.Clones.Should().ContainSingle();
    }

    [Fact]
    public async Task Deleting_a_voice_keeps_a_reference_recording_that_is_not_served_and_removes_an_upload()
    {
        var h = new Harness();
        ProjectFile held = AddNotServedOutput(h);
        ProjectFile upload = h.AddAudioFile();
        Guid voiceId = Guid.NewGuid();
        h.Db.ProjectVoices.Add(new ProjectVoice
        {
            Id = voiceId,
            ProjectId = h.Project.Id,
            InferenceProviderId = h.Provider.Id,
            Name = "v",
            Source = ProjectVoiceSource.Cloned,
            RemoteVoiceId = "m-1",
            ReferenceProjectFileIdsJson = $"[\"{held.Id}\",\"{upload.Id}\"]",
            ConsentAttestedAt = DateTime.UtcNow.AddDays(-5),
            CreatedByUserId = h.OwnerId
        });
        await h.Db.SaveChangesAsync();

        await h.Controller().Delete(h.Project.Id, voiceId, CancellationToken.None);

        (await h.Db.ProjectVoices.AnyAsync(v => v.Id == voiceId)).Should().BeFalse("the voice itself still goes");
        h.DeletedStorageKeys.Should().Equal(upload.StorageKey);
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == held.Id)).Should().BeTrue();
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == upload.Id)).Should().BeFalse();
    }

    private sealed class FailProjectReads : Microsoft.EntityFrameworkCore.Diagnostics.IQueryExpressionInterceptor
    {
        public bool Armed { get; set; }

        public System.Linq.Expressions.Expression QueryCompilationStarting(
            System.Linq.Expressions.Expression queryExpression, Microsoft.EntityFrameworkCore.Diagnostics.QueryExpressionEventData eventData)
        {
            if (Armed)
            {
                var finder = new RootFinder();
                finder.Visit(queryExpression);
                if (finder.Found)
                    throw new InvalidOperationException("db down");
            }
            return queryExpression;
        }

        private sealed class RootFinder : System.Linq.Expressions.ExpressionVisitor
        {
            public bool Found { get; private set; }

            protected override System.Linq.Expressions.Expression VisitExtension(System.Linq.Expressions.Expression node)
            {
                if (node is Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression root && root.EntityType.ClrType == typeof(Project))
                    Found = true;
                return base.VisitExtension(node);
            }
        }
    }

    [Fact]
    public async Task Deleting_reference_files_erases_the_history_copies_of_each_recording_it_removes()
    {
        var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid projectId = Guid.NewGuid();
        db.Projects.Add(new Project { Id = projectId, Name = "P" });
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "r.wav",
            MimeType = "audio/wav",
            StorageKey = $"projects/{projectId}/userFiles/r.wav",
            Category = "userFiles",
            SizeBytes = 10,
            UploadedAt = DateTime.UtcNow.AddHours(-3)
        };
        db.ProjectFiles.Add(file);
        var voice = new ProjectVoice { Id = Guid.NewGuid(), ProjectId = projectId, Name = "v", ReferenceProjectFileIdsJson = $"[\"{file.Id}\"]" };
        await db.SaveChangesAsync();
        var storage = new Mock<IFileStorageService>();
        var eraser = new Mock<ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser>();

        int deleted = await ProjectVoicesController.DeleteReferenceFilesAsync(voice, db, storage.Object, NullLogger.Instance, CancellationToken.None, eraser.Object);

        deleted.Should().Be(1);
        eraser.Verify(e => e.EraseFileAsync(projectId, file.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Deleting_reference_files_keeps_every_recording_when_the_marker_cannot_be_read()
    {
        var failure = new FailProjectReads();
        var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(failure).Options);
        Guid projectId = Guid.NewGuid();
        db.Projects.Add(new Project { Id = projectId, Name = "P", CsamOutputHoldAt = DateTime.UtcNow.AddHours(-1) });
        // A legacy row moved out of the output prefix: the list check alone would not hold it.
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "r.wav",
            MimeType = "audio/wav",
            StorageKey = $"projects/{projectId}/userFiles/r.wav",
            Category = "userFiles",
            SizeBytes = 10,
            UploadedAt = DateTime.UtcNow.AddHours(-3)
        };
        db.ProjectFiles.Add(file);
        var voice = new ProjectVoice { Id = Guid.NewGuid(), ProjectId = projectId, Name = "v", ReferenceProjectFileIdsJson = $"[\"{file.Id}\"]" };
        await db.SaveChangesAsync();
        var storage = new Mock<IFileStorageService>();

        failure.Armed = true;
        int deleted = await ProjectVoicesController.DeleteReferenceFilesAsync(voice, db, storage.Object, NullLogger.Instance, CancellationToken.None);

        deleted.Should().Be(0);
        storage.Verify(s => s.DeleteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        failure.Armed = false;
        (await db.ProjectFiles.AnyAsync(f => f.Id == file.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task The_retention_sweep_keeps_a_reference_recording_that_is_not_served()
    {
        var h = new Harness();
        ProjectFile held = AddNotServedOutput(h);
        h.Db.ProjectVoices.Add(new ProjectVoice
        {
            Id = Guid.NewGuid(),
            ProjectId = h.Project.Id,
            InferenceProviderId = h.Provider.Id,
            Name = "expired",
            Source = ProjectVoiceSource.Cloned,
            RemoteVoiceId = "m-expired",
            ReferenceProjectFileIdsJson = $"[\"{held.Id}\"]",
            ConsentAttestedAt = DateTime.UtcNow.AddDays(-400),
            RetentionUntil = DateTime.UtcNow.AddDays(-1),
            CreatedByUserId = h.OwnerId
        });
        await h.Db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddSingleton(h.Db);
        services.AddSingleton(h.Storage.Object);
        services.AddSingleton(h.Resolver.Object);
        services.AddSingleton(h.Factory.Object);
        using ServiceProvider provider = services.BuildServiceProvider();
        var sweep = new ProjectVoiceRetentionService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ProjectVoiceRetentionService>.Instance);

        int deleted = await sweep.SweepOnceAsync(CancellationToken.None);

        deleted.Should().Be(1);
        h.DeletedStorageKeys.Should().BeEmpty();
        (await h.Db.ProjectFiles.AnyAsync(f => f.Id == held.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_voices_recordings_keeps_one_an_avatar_also_lists()
    {
        var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid projectId = Guid.NewGuid();
        db.Projects.Add(new Project { Id = projectId, Name = "P" });
        ProjectFile shared = new() { Id = Guid.NewGuid(), ProjectId = projectId, OriginalFileName = "s.wav", MimeType = "audio/wav", StorageKey = $"projects/{projectId}/userFiles/s.wav", Category = "userFiles" };
        ProjectFile own = new() { Id = Guid.NewGuid(), ProjectId = projectId, OriginalFileName = "o.wav", MimeType = "audio/wav", StorageKey = $"projects/{projectId}/userFiles/o.wav", Category = "userFiles" };
        db.ProjectFiles.AddRange(shared, own);
        var voice = new ProjectVoice { Id = Guid.NewGuid(), ProjectId = projectId, Name = "v", ReferenceProjectFileIdsJson = $"[\"{shared.Id}\",\"{own.Id}\"]" };
        await db.SaveChangesAsync();
        Avatars.AvatarSeed.Attach(db, projectId, "Anna", shared);
        var storage = new Mock<IFileStorageService>();

        int deleted = await ProjectVoicesController.DeleteReferenceFilesAsync(voice, db, storage.Object, NullLogger.Instance, CancellationToken.None);
        await db.SaveChangesAsync();

        deleted.Should().Be(1);
        (await db.ProjectFiles.AnyAsync(f => f.Id == shared.Id)).Should().BeTrue("the avatar still lists it");
        storage.Verify(s => s.DeleteAsync(projectId, shared.StorageKey, It.IsAny<CancellationToken>()), Times.Never);
    }
}
