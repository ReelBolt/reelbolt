using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// An output's bytes leave the publish gate when they are moved or copied to another key. These tests
/// drive the file controller itself: only an output with a current Allow or Review verdict may go.
/// </summary>
public sealed class OutputModerationCopyGateTests
{
    private const string Version = "e1:1000";

    private sealed class Fixture
    {
        public Guid OrgId { get; } = Guid.NewGuid();
        public Project Project { get; }
        public InferenceApiDbContext Db { get; }
        public ProjectFile Output { get; }
        public Mock<IFileStorageService> Storage { get; } = new();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public ProjectFilesController Controller { get; }
        public string Key { get; }

        public Fixture(string? verdictState, string version = Version, string? reasonCode = null)
        {
            Project = new Project { Id = Guid.NewGuid(), Name = "p", OwnerId = Guid.NewGuid(), OrganizationId = OrgId };
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Db.Projects.Add(Project);
            Output = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = "render.mp4",
                OriginalPath = "render.mp4",
                Category = "outputFiles",
                StorageFileName = "render.mp4",
                StorageKey = ReelBolt.Shared.ProjectFilePath.BuildStorageKey(Project.Id, "outputFiles", null, "render.mp4"),
                MimeType = "video/mp4",
                SizeBytes = 1000,
                CsamStatus = CsamScanStatus.Clean
            };
            Db.ProjectFiles.Add(Output);
            Key = Output.StorageKey;
            if (verdictState is not null)
            {
                Db.OutputModerationVerdicts.Add(new OutputModerationVerdict
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = OrgId,
                    ProjectId = Project.Id,
                    StorageKey = Output.StorageKey,
                    SourceVersion = version,
                    State = verdictState,
                    ReasonCode = reasonCode,
                    ReasonMessage = verdictState == "Block" ? "Blocked." : null,
                    ModelSnapshot = "m"
                });
            }

            Db.SaveChanges();

            Storage.Setup(s => s.MoveIfMatchAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            Storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StoredObjectMetadata(1000, "video/mp4", "\"e1\""));
            _gate = new Mock<IContentModerationGate>().Object;
            PublishModeration moderation = new(
                _gate, Storage.Object, new Mock<IModerationMediaSampler>().Object,
                new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object, Db,
                Options.Create(new ContentModerationOptions { Enabled = true }), NullLogger<PublishModeration>.Instance);

            Mock<IProjectAccessService> access = new();
            access.Setup(a => a.GetAsync(Project.Id, It.IsAny<ProjectPermission>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProjectAccessResult(ProjectAccessOutcome.Found, Project));
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(Project.OwnerId);
            Controller = new ProjectFilesController(
                Db, user.Object, access.Object, Storage.Object,
                Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(), Publish.Object,
                new VectorSearchQueryService(
                    Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()),
                publishModeration: moderation)
            {
                ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
            };
            Moderation = moderation;
        }

        public PublishModeration Moderation { get; }

        public int GateCalls => Mock.Get(_gate).Invocations.Count;

        private IContentModerationGate _gate = null!;

        public Task<ActionResult<List<ProjectFileResponse>>> MoveToUserFilesAsync() =>
            Controller.Move(Project.Id, new MoveProjectFilesRequest([Output.Id], "saved", "userFiles"), CancellationToken.None);

        public void VerifyNotMoved() =>
            Storage.Verify(s => s.MoveAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static int StatusOf(ActionResult result) => ((ObjectResult)result).StatusCode!.Value;

    [Theory]
    [InlineData(null, 409, "CONTENT_MODERATION_PENDING")]
    [InlineData("Block", 422, "CONTENT_BLOCKED")]
    public async Task A_pending_or_blocked_render_cannot_be_moved_out(string? state, int status, string code)
    {
        Fixture f = new(state);

        ActionResult<List<ProjectFileResponse>> result = await f.MoveToUserFilesAsync();

        StatusOf(result.Result!).Should().Be(status);
        ((ObjectResult)result.Result!).Value!.ToString().Should().Contain(code);
        f.VerifyNotMoved();
        (await f.Db.ProjectFiles.SingleAsync()).StorageKey.Should().Be(f.Key);
    }

    [Fact]
    public async Task A_render_whose_bytes_changed_cannot_be_moved_out()
    {
        Fixture f = new("Allow", version: "old:1");

        StatusOf((await f.MoveToUserFilesAsync()).Result!).Should().Be(409);
        f.VerifyNotMoved();
    }

    [Fact]
    public async Task A_recorded_outage_cannot_be_moved_out()
    {
        Fixture f = new("Unavailable");

        StatusOf((await f.MoveToUserFilesAsync()).Result!).Should().Be(409);
        f.VerifyNotMoved();
    }

    [Theory]
    [InlineData("Allow", null)]
    [InlineData("Review", null)]
    [InlineData("Unavailable", OutputModerationReasonCodes.NothingToClassify)]
    public async Task An_allowed_or_wordless_render_can_be_moved(string state, string? reasonCode)
    {
        Fixture f = new(state, reasonCode: reasonCode);

        ActionResult<List<ProjectFileResponse>> result = await f.MoveToUserFilesAsync();

        result.Result.Should().BeOfType<OkObjectResult>();
        f.Storage.Verify(s => s.MoveIfMatchAsync(It.IsAny<Guid>(), f.Key, It.IsAny<string>(), "e1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_move_that_keeps_the_key_changes_nothing_and_the_output_stays_gated()
    {
        Fixture f = new(null);

        ActionResult<List<ProjectFileResponse>> result = await f.Controller.Move(
            f.Project.Id, new MoveProjectFilesRequest([f.Output.Id], null, "outputFiles"), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>("the key is unchanged, so there is nothing to refuse");
        (await f.Db.ProjectFiles.SingleAsync()).StorageKey.Should().Be(f.Key);
        (await f.Moderation.GateStoredFileAsync(f.Project.Id, f.Key, CancellationToken.None))!
            .Code.Should().Be("CONTENT_MODERATION_PENDING");
    }

    [Fact]
    public async Task A_folder_rename_cannot_carry_a_pending_render_to_another_key()
    {
        Fixture f = new(null);
        f.Output.DirectoryPath = "reels";
        await f.Db.SaveChangesAsync();

        IActionResult result = await f.Controller.RenameFolder(
            f.Project.Id, new RenameFolderRequest("reels", "archive"), CancellationToken.None);

        StatusOf((ObjectResult)result).Should().Be(409);
        f.VerifyNotMoved();
    }

    [Fact]
    public async Task Changing_the_content_type_of_an_output_is_refused()
    {
        Fixture f = new("Allow");
        f.Output.MimeType = "text/plain";
        f.Output.OriginalFileName = "notes.txt";
        await f.Db.SaveChangesAsync();

        ActionResult<ProjectFileResponse> result = await f.Controller.UpdateContent(
            f.Project.Id, f.Output.Id, new UpdateProjectFileContentRequest("hello", "video/mp4"), CancellationToken.None);

        StatusOf(result.Result!).Should().Be(409);
        ((ObjectResult)result.Result!).Value!.ToString().Should().Contain("OUTPUT_CONTENT_TYPE_LOCKED");
        f.Storage.Verify(s => s.UploadAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<System.IO.Stream>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task A_moved_allowed_render_keeps_its_verdict_and_is_served_with_no_new_judging_until_its_bytes_change()
    {
        Fixture f = new("Allow");
        string oldKey = f.Key;

        ActionResult<List<ProjectFileResponse>> moved = await f.Controller.Move(
            f.Project.Id, new MoveProjectFilesRequest([f.Output.Id], "archive", "outputFiles"), CancellationToken.None);

        moved.Result.Should().BeOfType<OkObjectResult>();
        string newKey = (await f.Db.ProjectFiles.SingleAsync()).StorageKey;
        newKey.Should().NotBe(oldKey);
        OutputModerationVerdict row = await f.Db.OutputModerationVerdicts.SingleAsync();
        row.StorageKey.Should().Be(newKey, "the verdict followed the object");
        row.SourceVersion.Should().Be(Version, "the version was read after the server's own move");
        (await f.Moderation.GateStoredFileAsync(f.Project.Id, newKey, CancellationToken.None)).Should().BeNull("served, nothing judged again");
        f.GateCalls.Should().Be(0);

        // Only changed bytes make it pending again.
        f.Storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(1000, "video/mp4", "\"changed\""));
        (await f.Moderation.GateStoredFileAsync(f.Project.Id, newKey, CancellationToken.None))!.Code.Should().Be("CONTENT_MODERATION_PENDING");
    }

    [Fact]
    public async Task A_verdict_for_other_bytes_is_not_carried_to_the_new_key()
    {
        Fixture f = new("Allow", version: "old:1");

        // Not an output-key gate case (key outside prefixes would skip it): move the file to a user folder directly.
        f.Output.StorageKey = ReelBolt.Shared.ProjectFilePath.BuildStorageKey(f.Project.Id, "userFiles", null, "render.mp4");
        f.Db.OutputModerationVerdicts.Single().StorageKey = f.Output.StorageKey;
        f.Output.Category = "userFiles";
        await f.Db.SaveChangesAsync();

        await f.Controller.Move(f.Project.Id, new MoveProjectFilesRequest([f.Output.Id], "saved", "userFiles"), CancellationToken.None);

        (await f.Db.OutputModerationVerdicts.CountAsync()).Should().Be(0, "the stored verdict was for other bytes");
    }

    [Fact]
    public async Task A_storage_failure_on_the_second_file_leaves_the_first_row_on_its_new_key()
    {
        Fixture f = new("Allow");
        ProjectFile second = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OriginalFileName = "b.txt",
            OriginalPath = "b.txt",
            Category = "userFiles",
            StorageFileName = "b.txt",
            StorageKey = ReelBolt.Shared.ProjectFilePath.BuildStorageKey(f.Project.Id, "userFiles", null, "b.txt"),
            MimeType = "text/plain",
            SizeBytes = 1,
            CsamStatus = CsamScanStatus.Clean
        };
        f.Db.ProjectFiles.Add(second);
        await f.Db.SaveChangesAsync();
        f.Storage.Setup(s => s.MoveIfMatchAsync(It.IsAny<Guid>(), second.StorageKey, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));

        Func<Task> move = () => f.Controller.Move(
            f.Project.Id, new MoveProjectFilesRequest([f.Output.Id, second.Id], "saved", "userFiles"), CancellationToken.None);

        await move.Should().ThrowAsync<InvalidOperationException>();
        (await f.Db.ProjectFiles.SingleAsync(x => x.Id == f.Output.Id)).StorageKey.Should().Contain("/userFiles/", "its object moved and its row says so");
        (await f.Db.ProjectFiles.SingleAsync(x => x.Id == second.Id)).StorageKey.Should().Contain("/userFiles/b.txt");

        // The file that did move is asked to be indexed under its new key; the one that did not is not.
        f.Publish.Verify(p => p.Publish(
            It.Is<ReelBolt.Shared.IntegrationEvents.ProjectFileIndexingRequested>(m => m.FileId == f.Output.Id), It.IsAny<CancellationToken>()), Times.Once);
        f.Publish.Verify(p => p.Publish(
            It.Is<ReelBolt.Shared.IntegrationEvents.ProjectFileIndexingRequested>(m => m.FileId == second.Id), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_copy_whose_etag_differs_from_the_judged_one_carries_no_verdict()
    {
        Fixture f = new("Allow");
        // The conditional copy succeeded, but the object now at the new key reports another ETag than the judged one.
        f.Storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.Is<string>(k => k.Contains("/archive/")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(1000, "video/mp4", "\"e2\""));

        await f.Controller.Move(f.Project.Id, new MoveProjectFilesRequest([f.Output.Id], "archive", "outputFiles"), CancellationToken.None);

        (await f.Db.OutputModerationVerdicts.CountAsync()).Should().Be(0, "the ETag differs, so nothing vouches for the copy");
    }

    [Fact]
    public async Task A_refused_selection_names_the_file_and_makes_no_promise_about_seconds()
    {
        Fixture f = new(null);

        ObjectResult result = (ObjectResult)(await f.MoveToUserFilesAsync()).Result!;

        string text = result.Value!.ToString()!;
        text.Should().Contain("render.mp4");
        text.Should().NotContain("seconds");
        text.Should().Contain("once it has been checked");
    }

    [Fact]
    public async Task Bytes_swapped_between_judging_and_the_move_carry_no_verdict_and_read_pending()
    {
        Fixture f = new("Allow");
        // The conditional copy on the judged ETag is refused: the object changed after it was judged.
        f.Storage.Setup(s => s.MoveIfMatchAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), "e1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        ActionResult<List<ProjectFileResponse>> moved = await f.Controller.Move(
            f.Project.Id, new MoveProjectFilesRequest([f.Output.Id], "archive", "outputFiles"), CancellationToken.None);

        moved.Result.Should().BeOfType<OkObjectResult>();
        string newKey = (await f.Db.ProjectFiles.SingleAsync()).StorageKey;
        (await f.Db.OutputModerationVerdicts.CountAsync()).Should().Be(0, "nothing vouched for the bytes now at the new key");
        (await f.Moderation.GateStoredFileAsync(f.Project.Id, newKey, CancellationToken.None))!.Code.Should().Be("CONTENT_MODERATION_PENDING");
    }

    [Fact]
    public async Task A_store_that_cannot_condition_the_copy_carries_no_verdict()
    {
        Fixture f = new("Allow");
        f.Storage.Setup(s => s.MoveIfMatchAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await f.Controller.Move(f.Project.Id, new MoveProjectFilesRequest([f.Output.Id], "archive", "outputFiles"), CancellationToken.None);

        (await f.Db.OutputModerationVerdicts.CountAsync()).Should().Be(0);
    }
}
