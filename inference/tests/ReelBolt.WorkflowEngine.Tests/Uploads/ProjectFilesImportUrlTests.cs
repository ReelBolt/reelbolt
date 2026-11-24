#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The REST twin of the assistant's URL import (<c>POST files/import-url</c>), and the one shared
/// post-add code path (<see cref="ProjectFileAddedEffects"/>) every way of adding a file now runs.
/// </summary>
public class ProjectFilesImportUrlTests
{
    private static readonly byte[] Clip = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();

    private sealed class User(Guid id, Guid? organizationId, OrganizationRole? role, bool verified = true) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
        public bool EmailVerified { get; } = verified;
    }

    private sealed class Rig
    {
        public UrlImportHarness Import { get; } = new();

        public Mock<IPublishEndpoint> Publish { get; } = new();

        public ChannelBackgroundTaskQueue<FileSummarizationTask> Queue { get; } = new(100);

        public ProjectFilesController Controller(ICurrentUser user, IFileStorageService? storage = null, bool withPresigning = false)
        {
            ProjectFileAddedEffects effects = new(Publish.Object, Queue, storage ?? Import.Storage);
            ProjectFilesController controller = new(
                Import.Db, user, TestTenancy.Access(Import.Db, user), storage ?? Import.Storage, Queue, Publish.Object,
                new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()),
                Import.PlanGate,
                withPresigning ? new FakePresignedUrlService() : null,
                withPresigning ? ConfirmationTokenService.CreateEphemeral() : null,
                urlImport: Import.Service,
                fileAddedEffects: effects);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        public User Member(bool verified = true) => new(Guid.NewGuid(), Import.Project.OrganizationId, OrganizationRole.Member, verified);
    }

    private static int StatusOf(IActionResult? result) => result switch
    {
        NotFoundResult => 404,
        ForbidResult => 403,
        ObjectResult o => o.StatusCode ?? 200,
        _ => 0
    };

    private static object? Field(IActionResult? result, string name)
    {
        object body = ((ObjectResult)result!).Value!;
        return body.GetType().GetProperty(name)?.GetValue(body);
    }

    [Fact]
    public async Task A_public_url_answers_201_with_the_new_file()
    {
        Rig rig = new();
        rig.Import.Upstream.Serve("https://cdn.example.com/plates/room.mp4", Clip);

        ActionResult<ProjectFileResponse> result = await rig.Controller(rig.Member()).ImportFromUrl(
            rig.Import.Project.Id, new ImportProjectFileFromUrlRequest("https://cdn.example.com/plates/room.mp4", null, "plates"), CancellationToken.None);

        StatusOf(result.Result).Should().Be(201);
        ProjectFileResponse body = ((ObjectResult)result.Result!).Value.Should().BeOfType<ProjectFileResponse>().Subject;
        body.OriginalFileName.Should().Be("room.mp4");
        body.DirectoryPath.Should().Be("plates");
        body.MimeType.Should().Be("video/mp4");
        body.SizeBytes.Should().Be(Clip.LongLength);
        rig.Import.Db.ProjectFiles.Should().ContainSingle(f => f.Id == body.Id && f.Category == "userFiles");
    }

    [Theory]
    [InlineData("http://cdn.example.com/a.mp4", 400, "IMPORT_URL_REFUSED")]
    [InlineData("https://169.254.169.254/latest/meta-data/", 400, "IMPORT_URL_REFUSED")]
    [InlineData("", 400, "IMPORT_URL_REFUSED")]
    public async Task A_refused_url_answers_400_with_message_and_code(string url, int status, string code)
    {
        Rig rig = new();

        ActionResult<ProjectFileResponse> result = await rig.Controller(rig.Member()).ImportFromUrl(
            rig.Import.Project.Id, new ImportProjectFileFromUrlRequest(url, null, null), CancellationToken.None);

        StatusOf(result.Result).Should().Be(status);
        Field(result.Result, "code").Should().Be(code);
        Field(result.Result, "message").Should().BeOfType<string>().Which.Should().NotBeNullOrWhiteSpace();
        rig.Import.NothingStored.Should().BeTrue();
    }

    [Fact]
    public async Task An_upstream_502_answers_422_never_a_gateway_status()
    {
        Rig rig = new();
        rig.Import.Upstream.Status("https://cdn.example.com/a.mp4", HttpStatusCode.BadGateway);

        ActionResult<ProjectFileResponse> result = await rig.Controller(rig.Member()).ImportFromUrl(
            rig.Import.Project.Id, new ImportProjectFileFromUrlRequest("https://cdn.example.com/a.mp4", null, null), CancellationToken.None);

        StatusOf(result.Result).Should().Be(422);
        Field(result.Result, "code").Should().Be("IMPORT_FAILED");
        Field(result.Result, "upstreamStatus").Should().Be(502);
    }

    [Fact]
    public async Task Too_large_answers_413()
    {
        Rig rig = new();
        rig.Import.Options.MaxBytes = 100;
        rig.Import.Upstream.Serve("https://cdn.example.com/a.mp4", Clip);

        ActionResult<ProjectFileResponse> result = await rig.Controller(rig.Member()).ImportFromUrl(
            rig.Import.Project.Id, new ImportProjectFileFromUrlRequest("https://cdn.example.com/a.mp4", null, null), CancellationToken.None);

        StatusOf(result.Result).Should().Be(413);
        Field(result.Result, "code").Should().Be("IMPORT_TOO_LARGE");
    }

    [Fact]
    public async Task A_project_of_another_organization_is_404_and_nothing_is_fetched()
    {
        Rig rig = new();
        User stranger = new(Guid.NewGuid(), Guid.NewGuid(), OrganizationRole.Owner);

        ActionResult<ProjectFileResponse> result = await rig.Controller(stranger).ImportFromUrl(
            rig.Import.Project.Id, new ImportProjectFileFromUrlRequest("https://cdn.example.com/a.mp4", null, null), CancellationToken.None);

        StatusOf(result.Result).Should().Be(404);
        rig.Import.Upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_billing_member_without_edit_is_refused_and_nothing_is_fetched()
    {
        Rig rig = new();
        User billing = new(Guid.NewGuid(), rig.Import.Project.OrganizationId, OrganizationRole.Billing);

        ActionResult<ProjectFileResponse> result = await rig.Controller(billing).ImportFromUrl(
            rig.Import.Project.Id, new ImportProjectFileFromUrlRequest("https://cdn.example.com/a.mp4", null, null), CancellationToken.None);

        StatusOf(result.Result).Should().BeOneOf(403, 404);
        rig.Import.Upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unverified_email_is_403_and_nothing_is_fetched()
    {
        Rig rig = new();

        ActionResult<ProjectFileResponse> result = await rig.Controller(rig.Member(verified: false)).ImportFromUrl(
            rig.Import.Project.Id, new ImportProjectFileFromUrlRequest("https://cdn.example.com/a.mp4", null, null), CancellationToken.None);

        StatusOf(result.Result).Should().Be(403);
        Field(result.Result, "code").Should().Be("EMAIL_NOT_VERIFIED");
        rig.Import.Upstream.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the shared post-add path

    [Fact]
    public async Task A_text_file_is_indexed_and_queued_for_summary_and_requests_no_derivatives()
    {
        Mock<IPublishEndpoint> publish = new();
        ChannelBackgroundTaskQueue<FileSummarizationTask> queue = new(10);
        ProjectFileAddedEffects effects = new(publish.Object, queue, new ImportObjectStore());
        ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), OriginalFileName = "brief.md", MimeType = "text/markdown", StorageKey = "k", SummaryStatus = SummaryStatus.Pending };

        await effects.RunAsync(file, Guid.NewGuid(), CancellationToken.None);

        publish.Verify(p => p.Publish(It.Is<ProjectFileIndexingRequested>(m => m.FileId == file.Id && m.Operation == "Upsert"), It.IsAny<CancellationToken>()), Times.Once);
        publish.Verify(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()), Times.Never);
        queue.IsTracked(new FileSummarizationTask(file.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_media_file_requests_derivatives_and_a_broker_outage_does_not_throw()
    {
        Mock<IPublishEndpoint> publish = new();
        publish.Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("broker down"));
        ChannelBackgroundTaskQueue<FileSummarizationTask> queue = new(10);
        ProjectFileAddedEffects effects = new(publish.Object, queue, new ImportObjectStore());
        ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), OriginalFileName = "a.mp3", MimeType = "audio/mpeg", StorageKey = "k", SummaryStatus = SummaryStatus.Done };

        Func<Task> act = () => effects.RunAsync(file, Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        publish.Verify(p => p.Publish(It.Is<MediaDerivativesRequested>(m => m.SourceProjectFileId == file.Id), It.IsAny<CancellationToken>()), Times.Once);
        publish.Verify(p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()), Times.Never);
        queue.IsTracked(new FileSummarizationTask(file.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_completed_direct_upload_of_media_now_requests_its_derivatives_like_the_form_upload()
    {
        Rig rig = new();
        UploadsObjectStore storage = new();
        User member = rig.Member();
        ProjectFilesController controller = rig.Controller(member, storage, withPresigning: true);

        ActionResult<ReelBolt.Inference.Api.Controllers.Dto.DirectUploadTicket> created = await controller.CreateUploadUrl(
            rig.Import.Project.Id, new CreateDirectUploadRequest("clip.mp4", 9, "video/mp4", null), CancellationToken.None);
        var ticket = (ReelBolt.Inference.Api.Controllers.Dto.DirectUploadTicket)((OkObjectResult)created.Result!).Value!;
        storage.Put(ticket.Key, 9);

        ActionResult<ProjectFileResponse> completed = await controller.CompleteUpload(
            rig.Import.Project.Id,
            new CompleteDirectUploadRequest(ticket.UploadToken, ticket.Key, ticket.FileId, "clip.mp4", 9, ticket.ExpiresAtUnix, ticket.ContentType, null),
            CancellationToken.None);

        StatusOf(completed.Result).Should().Be(201);
        rig.Publish.Verify(p => p.Publish(It.Is<MediaDerivativesRequested>(m => m.SourceProjectFileId == ticket.FileId), It.IsAny<CancellationToken>()), Times.Once);
    }
}
