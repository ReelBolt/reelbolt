#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>POST files/sample: the bundled, generated first-party clip copied into a project.</summary>
public class ProjectFilesSampleClipTests
{
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

        public ProjectFilesController Controller(ICurrentUser user)
        {
            ProjectFileAddedEffects effects = new(Publish.Object, Queue, Import.Storage);
            ProjectFilesController controller = new(
                Import.Db, user, TestTenancy.Access(Import.Db, user), Import.Storage, Queue, Publish.Object,
                new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()),
                Import.PlanGate,
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

    [Fact]
    public async Task First_call_stores_the_clip_and_answers_201()
    {
        Rig rig = new();

        ActionResult<ProjectFileResponse> result = await rig.Controller(rig.Member())
            .AddSample(rig.Import.Project.Id, new AddSampleFileRequest("first-film"), CancellationToken.None);

        StatusOf(result.Result).Should().Be(201);
        ProjectFileResponse body = ((ObjectResult)result.Result!).Value.Should().BeOfType<ProjectFileResponse>().Subject;
        body.OriginalFileName.Should().Be("Sample clip.mp4");
        body.DirectoryPath.Should().BeNullOrEmpty();
        body.MimeType.Should().Be("video/mp4");
        body.SizeBytes.Should().BeInRange(1, 3 * 1024 * 1024);
        rig.Import.Storage.Objects.Should().ContainSingle();
        rig.Import.Storage.Objects.Values.Single().Length.Should().Be((int)body.SizeBytes);
        rig.Import.Db.ProjectFiles.Should().ContainSingle(f => f.Id == body.Id && f.Category == "userFiles");
        (await rig.Import.Db.ProjectFiles.SingleAsync()).SizeBytes.Should().Be(rig.Import.Storage.Objects.Values.Single().Length);
    }

    [Fact]
    public async Task Second_call_returns_the_existing_file_with_200_and_no_duplicate()
    {
        Rig rig = new();
        ProjectFilesController controller = rig.Controller(rig.Member());

        ActionResult<ProjectFileResponse> first = await controller.AddSample(rig.Import.Project.Id, new AddSampleFileRequest("first-film"), CancellationToken.None);
        ActionResult<ProjectFileResponse> second = await controller.AddSample(rig.Import.Project.Id, new AddSampleFileRequest("first-film"), CancellationToken.None);

        StatusOf(second.Result).Should().Be(200);
        ((ProjectFileResponse)((ObjectResult)second.Result!).Value!).Id
            .Should().Be(((ProjectFileResponse)((ObjectResult)first.Result!).Value!).Id);
        (await rig.Import.Db.ProjectFiles.CountAsync()).Should().Be(1);
        rig.Import.Storage.Objects.Should().ContainSingle();
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Unknown_key_is_400_and_stores_nothing(string? key)
    {
        Rig rig = new();

        ActionResult<ProjectFileResponse> result = await rig.Controller(rig.Member())
            .AddSample(rig.Import.Project.Id, new AddSampleFileRequest(key), CancellationToken.None);

        StatusOf(result.Result).Should().Be(400);
        rig.Import.Storage.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task Foreign_project_is_404()
    {
        Rig rig = new();
        User stranger = new(Guid.NewGuid(), Guid.NewGuid(), OrganizationRole.Owner);

        ActionResult<ProjectFileResponse> result = await rig.Controller(stranger)
            .AddSample(rig.Import.Project.Id, new AddSampleFileRequest("first-film"), CancellationToken.None);

        StatusOf(result.Result).Should().Be(404);
        rig.Import.Storage.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task Unverified_email_is_403()
    {
        Rig rig = new();

        ActionResult<ProjectFileResponse> result = await rig.Controller(rig.Member(verified: false))
            .AddSample(rig.Import.Project.Id, new AddSampleFileRequest("first-film"), CancellationToken.None);

        StatusOf(result.Result).Should().Be(403);
        rig.Import.Storage.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task Over_quota_is_refused_and_stores_nothing()
    {
        Rig rig = new();
        rig.Import.PlanGate.Violations.Enqueue(new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413));

        ActionResult<ProjectFileResponse> result = await rig.Controller(rig.Member())
            .AddSample(rig.Import.Project.Id, new AddSampleFileRequest("first-film"), CancellationToken.None);

        StatusOf(result.Result).Should().Be(413);
        rig.Import.Storage.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_file_runs_the_shared_post_add_path()
    {
        Rig rig = new();

        await rig.Controller(rig.Member()).AddSample(rig.Import.Project.Id, new AddSampleFileRequest("first-film"), CancellationToken.None);

        rig.Publish.Invocations.Should().NotBeEmpty("a media row requests its derivative set like any upload");
    }
}
