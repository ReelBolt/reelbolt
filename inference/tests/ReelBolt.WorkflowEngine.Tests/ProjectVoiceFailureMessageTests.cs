#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// P2-N2 (S-53): the voice clone and reference-transcription 502s must not answer a PLATFORM row's
/// vendor error text or transport detail (which names the provider host:port) to the tenant; an
/// organization's own row keeps the vendor detail because the account is theirs.
/// </summary>
public class ProjectVoiceFailureMessageTests
{
    /// <summary>Vendor text that also carries the transport detail (host:port) the leak is about.</summary>
    private const string VendorText = "Connection refused (fish-internal:8080) vendor said: quota for acct-42";

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "owner@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    /// <summary>A Fish-shaped cloning client that fails on demand with the vendor's own text.</summary>
    private sealed class FailingCloner : ISpeechSynthesisClient, IVoiceCloningClient
    {
        public bool FailRequirements { get; set; }
        public bool FailCreate { get; set; }
        public VoiceCloningRequirements Requirements { get; set; } = VoiceCloningRequirements.Hosted;

        public Task<VoiceCloningRequirements> GetRequirementsAsync(CancellationToken ct) =>
            FailRequirements ? throw new HttpRequestException(VendorText) : Task.FromResult(Requirements);

        public Task<SynthesizedSpeech> SynthesizeAsync(SpeechRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CreatedVoice> CreateVoiceAsync(VoiceCloneRequest request, CancellationToken ct) =>
            FailCreate ? throw new HttpRequestException(VendorText) : Task.FromResult(new CreatedVoice("fish-model-123", "trained"));

        public Task<bool> DeleteVoiceAsync(string remoteVoiceId, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class Harness
    {
        public InferenceApiDbContext Db { get; }
        public Guid OwnerId { get; } = Guid.NewGuid();
        public Project Project { get; }
        public Guid ProviderId { get; } = Guid.NewGuid();
        public Mock<IFileStorageService> Storage { get; } = new();
        public Mock<IInferenceProviderResolver> Resolver { get; } = new();
        public Mock<ISpeechSynthesisClientFactory> Factory { get; } = new();
        public Mock<ITranscriptionClientFactory> Transcription { get; } = new();
        public Mock<ITranscriptionClient> Asr { get; } = new();
        public FailingCloner Cloner { get; } = new();

        /// <summary>The owning organization of the resolved SPEECH row: null = a platform row.</summary>
        public Guid? SpeechRowOrganizationId { get; set; }

        /// <summary>The owning organization of the resolved TRANSCRIPTION row: null = a platform row.</summary>
        public Guid? TranscriptionRowOrganizationId { get; set; }

        public Harness()
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = OwnerId, OrganizationId = TestTenancy.OrgOf(OwnerId) };
            Db.Projects.Add(Project);
            Db.SaveChanges();

            Resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ResolvedSpeechSynthesisProvider(
                    ProviderId, "fish", InferenceProviderKind.FishAudio, "http://fish-internal:8080", "s2.1-pro", "k", 30,
                    OrganizationId: SpeechRowOrganizationId));
            Resolver.Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ResolvedTranscriptionProvider(
                    Guid.NewGuid(), "whisper", InferenceProviderKind.OpenAICompatible, "http://whisper:8000/v1", "whisper-1", "", 30,
                    OrganizationId: TranscriptionRowOrganizationId));
            Factory.Setup(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>())).Returns(Cloner);
            Transcription.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(Asr.Object);
            Storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.ASCII.GetBytes("RIFF....WAVE")));
        }

        public ProjectVoicesController Controller() =>
            new(Db, new FakeCurrentUser(OwnerId), TestTenancy.Access(Db, new FakeCurrentUser(OwnerId)), Storage.Object,
                Resolver.Object, Factory.Object, Transcription.Object, NullLogger<ProjectVoicesController>.Instance);

        public ProjectFile AddAudioFile()
        {
            var file = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = "take.wav",
                MimeType = "audio/wav",
                StorageKey = $"projects/{Project.Id}/userFiles/{Guid.NewGuid():N}.wav",
                SizeBytes = 1200
            };
            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        public CloneVoiceRequest Clone(params Guid[] fileIds) => new(
            Name: "Presenter",
            ReferenceProjectFileIds: fileIds,
            ConsentStatement: ProjectVoicesController.RequiredConsentStatement,
            ConsentSubjectName: "Dana Presenter",
            InferenceProviderId: ProviderId,
            RetentionDays: 90);
    }

    private static (int Status, string Body) Body(ActionResult<ProjectVoiceResponse> result)
    {
        ObjectResult objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        return (objectResult.StatusCode ?? 0, JsonSerializer.Serialize(objectResult.Value));
    }

    [Fact]
    public async Task A_platform_rows_clone_failure_answers_a_fixed_sentence_not_the_vendor_or_transport_text()
    {
        var h = new Harness { SpeechRowOrganizationId = null };
        h.Cloner.FailRequirements = true;
        ProjectFile file = h.AddAudioFile();

        (int status, string body) = Body(await h.Controller().Clone(h.Project.Id, h.Clone(file.Id), CancellationToken.None));

        status.Should().Be(502);
        body.Should().Contain("clone_failed").And.Contain("The voice could not be cloned. Try again later.");
        body.Should().NotContain("fish-internal").And.NotContain("8080").And.NotContain("acct-42").And.NotContain("quota");
    }

    [Fact]
    public async Task An_organizations_own_row_keeps_the_vendor_detail_on_a_clone_failure()
    {
        var h = new Harness { SpeechRowOrganizationId = Guid.NewGuid() };
        h.Cloner.FailRequirements = true;
        ProjectFile file = h.AddAudioFile();

        (int status, string body) = Body(await h.Controller().Clone(h.Project.Id, h.Clone(file.Id), CancellationToken.None));

        status.Should().Be(502);
        body.Should().Contain("acct-42", "it is the organization's own vendor account");
    }

    [Fact]
    public async Task A_platform_rows_vendor_refusal_at_create_answers_a_fixed_sentence()
    {
        var h = new Harness { SpeechRowOrganizationId = null };
        h.Cloner.FailCreate = true;
        ProjectFile file = h.AddAudioFile();

        (int status, string body) = Body(await h.Controller().Clone(h.Project.Id, h.Clone(file.Id), CancellationToken.None));

        status.Should().Be(502);
        body.Should().Contain("clone_failed").And.Contain("The voice could not be cloned. Try again later.");
        body.Should().NotContain("fish-internal").And.NotContain("8080").And.NotContain("acct-42").And.NotContain("quota");
    }

    [Fact]
    public async Task A_platform_transcription_rows_failure_answers_a_fixed_sentence()
    {
        var h = new Harness { SpeechRowOrganizationId = Guid.NewGuid(), TranscriptionRowOrganizationId = null };
        h.Cloner.Requirements = VoiceCloningRequirements.SelfHostedFishSpeech;
        h.Asr.Setup(a => a.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(VendorText));
        ProjectFile file = h.AddAudioFile();

        (int status, string body) = Body(await h.Controller().Clone(h.Project.Id, h.Clone(file.Id), CancellationToken.None));

        status.Should().Be(502);
        body.Should().Contain("transcription_failed").And.Contain("The reference recording could not be transcribed. Try again later.");
        body.Should().NotContain("fish-internal").And.NotContain("8080").And.NotContain("acct-42").And.NotContain("quota");
    }

    [Fact]
    public async Task An_organizations_own_transcription_row_keeps_the_vendor_detail()
    {
        var h = new Harness { SpeechRowOrganizationId = Guid.NewGuid(), TranscriptionRowOrganizationId = Guid.NewGuid() };
        h.Cloner.Requirements = VoiceCloningRequirements.SelfHostedFishSpeech;
        h.Asr.Setup(a => a.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(VendorText));
        ProjectFile file = h.AddAudioFile();

        (int status, string body) = Body(await h.Controller().Clone(h.Project.Id, h.Clone(file.Id), CancellationToken.None));

        status.Should().Be(502);
        body.Should().Contain("acct-42", "it is the organization's own vendor account");
    }
}
