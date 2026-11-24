using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing.Color;

/// <summary>
/// The owner's prod bug after #998: a 1D .cube uploaded as application/octet-stream was refused by
/// <c>GET …/files/{id}/content</c> with 409 ("not a text-editable asset"), so the viewing LUT said
/// "could not open". A .cube is readable there whatever its MIME, never writable, and never text to
/// the rest of <see cref="ProjectFileTextPolicy"/>.
/// </summary>
public class CubeLutContentReadTests : IDisposable
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly InferenceApiDbContext _db;
    private readonly Project _project;

    public CubeLutContentReadTests()
    {
        _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase($"cube-read-{Guid.NewGuid():N}").Options);
        _project = new Project { Id = Guid.NewGuid(), Name = "LUTs", OwnerId = _userId, OrganizationId = TestTenancy.OrgOf(_userId) };
        _db.Projects.Add(_project);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    /// <summary>The shape of the owner's Samsung Log-to-Linear file: 1D, 4096 rows, CRLF, values up to ~10.</summary>
    internal static string SamsungShapedCube()
    {
        StringBuilder sb = new();
        sb.Append("# Samsung Log to Linear\r\nTITLE \"Samsung Log to Linear\"\r\nLUT_1D_SIZE 4096\r\n");
        for (int i = 0; i < 4096; i++)
        {
            double x = i / 4095.0;
            double v = Math.Pow(10, x * 4 - 3) - 0.00102; // -0.00002 .. ~9.9990
            string s = v.ToString("0.0000000", CultureInfo.InvariantCulture);
            sb.Append(s).Append(' ').Append(s).Append(' ').Append(s).Append("\r\n");
        }

        return sb.ToString();
    }

    private sealed class User(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsAdmin { get; } = false;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
    }

    private ProjectFile AddFile(string name, string mime, long size)
    {
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = _project.Id,
            OriginalFileName = name,
            OriginalPath = name,
            StorageKey = $"projects/{_project.Id}/userFiles/{name}",
            StorageFileName = name,
            StorageBucket = "bucket",
            MimeType = mime,
            SizeBytes = size,
        };
        _db.ProjectFiles.Add(file);
        _db.SaveChanges();
        return file;
    }

    private ProjectFilesController Controller(Mock<IFileStorageService> storage)
    {
        User user = new(_userId, _project.OrganizationId);
        return new ProjectFilesController(
            _db, user, TestTenancy.Access(_db, user), storage.Object,
            Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(), Mock.Of<IPublishEndpoint>(), null!,
            logger: NullLogger<ProjectFilesController>.Instance);
    }

    [Fact]
    public async Task An_octet_stream_cube_is_served_by_GetContent_and_parses_as_1D()
    {
        string text = SamsungShapedCube();
        ProjectFile file = AddFile("Samsung+Log+to+Linear+1DLUT_v1.0.cube", "application/octet-stream", Encoding.UTF8.GetByteCount(text));
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(_project.Id, file.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(text)));

        ActionResult<ProjectFileContentResponse> result = await Controller(storage).GetContent(_project.Id, file.Id, CancellationToken.None);

        ProjectFileContentResponse body = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ProjectFileContentResponse>().Which;
        body.Content.Should().Be(text);
        CubeLut.Parse(body.Content).Kind.Should().Be(CubeLutKind.OneD);
    }

    [Fact]
    public async Task An_oversized_cube_is_refused_413_before_any_download()
    {
        ProjectFile file = AddFile("huge.cube", "application/octet-stream", CubeLut.MaxBytes + 1);
        Mock<IFileStorageService> storage = new();

        ActionResult<ProjectFileContentResponse> result = await Controller(storage).GetContent(_project.Id, file.Id, CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        storage.Verify(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Other_octet_stream_files_are_still_refused_409()
    {
        ProjectFile file = AddFile("blob.bin", "application/octet-stream", 10);

        ActionResult<ProjectFileContentResponse> result = await Controller(new Mock<IFileStorageService>()).GetContent(_project.Id, file.Id, CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task A_cube_stays_not_writable_through_PUT_content()
    {
        ProjectFile file = AddFile("look.cube", "application/octet-stream", 10);

        ActionResult<ProjectFileResponse> result = await Controller(new Mock<IFileStorageService>())
            .UpdateContent(_project.Id, file.Id, new UpdateProjectFileContentRequest("LUT_1D_SIZE 2\n0 0 0\n1 1 1\n"), CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public void The_text_policy_is_not_widened_to_cube()
    {
        // The assistant, the brief resolver, the engine workspace and the writers all ask the policy; it must stay closed.
        ProjectFileTextPolicy.TextExtensions.Should().NotContain(".cube");
        ProjectFileTextPolicy.IsLikelyText("application/octet-stream", "look.cube").Should().BeFalse();
        ProjectFileTextPolicy.IsWritableText("look.cube").Should().BeFalse();
    }
}
