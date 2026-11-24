using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// T-003: <c>GET /api/v1/projects/{p}/design-system/assets/{fileId}</c>. The route only ever serves a
/// non-quarantined file of the same project under <c>design-system/assets/</c>; an SVG is rebuilt by
/// the sanitizer again on read and leaves under a sandbox CSP, a raster inline, a font as an
/// attachment and anything else as 404.
/// </summary>
public sealed class DesignSystemAssetRouteTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("dddddddd-0000-0000-0000-000000000003");

    private static readonly byte[] TinyPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    ];

    private static readonly byte[] TinyWoff = [0x77, 0x4F, 0x46, 0x46, 0x00, 0x01, 0x00, 0x00];

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Mock<IFileStorageService> _storage = new();

    public DesignSystemAssetRouteTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-asset-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task An_svg_is_re_sanitized_and_served_inert_under_a_sandbox_csp()
    {
        (Project project, ProjectFile file) = Seed(
            "design-system/assets/logo.svg", "image/svg+xml", "logo.svg",
            Encoding.UTF8.GetBytes(
                "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script><path d='M0 0'/></svg>"));
        DesignSystemAssetController controller = Controller();

        IActionResult result = await controller.Get(project.Id, file.Id, CancellationToken.None);

        FileContentResult content = result.Should().BeOfType<FileContentResult>().Subject;
        content.ContentType.Should().Be("image/svg+xml; charset=utf-8");
        Encoding.UTF8.GetString(content.FileContents).Should().NotContain("script");
        controller.Response.Headers["Content-Security-Policy"].ToString()
            .Should().Contain("default-src 'none'").And.Contain("sandbox");
        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        controller.Response.Headers["Content-Disposition"].ToString()
            .Should().StartWith("inline").And.Contain("filename=\"logo.svg\"");
    }

    [Fact]
    public async Task An_svg_that_no_longer_sanitizes_is_404()
    {
        (Project project, ProjectFile file) = Seed(
            "design-system/assets/broken.svg", "image/svg+xml", "broken.svg",
            Encoding.UTF8.GetBytes("<svg><path d='M0 0'"));

        IActionResult result = await Controller().Get(project.Id, file.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_raster_is_served_inline_with_its_stored_mime()
    {
        (Project project, ProjectFile file) = Seed(
            "design-system/assets/logo.png", "image/png", "logo.png", TinyPng);

        IActionResult result = await Controller().Get(project.Id, file.Id, CancellationToken.None);

        FileContentResult content = result.Should().BeOfType<FileContentResult>().Subject;
        content.ContentType.Should().Be("image/png");
        content.FileContents.Should().Equal(TinyPng);
    }

    [Fact]
    public async Task A_font_is_served_as_an_attachment()
    {
        (Project project, ProjectFile file) = Seed(
            "design-system/assets/brand.woff", "font/woff", "brand.woff", TinyWoff);
        DesignSystemAssetController controller = Controller();

        IActionResult result = await controller.Get(project.Id, file.Id, CancellationToken.None);

        FileContentResult content = result.Should().BeOfType<FileContentResult>().Subject;
        content.ContentType.Should().Be("application/octet-stream");
        controller.Response.Headers["Content-Disposition"].ToString().Should().StartWith("attachment");
    }

    [Fact]
    public async Task The_route_404s_for_a_foreign_projects_file()
    {
        (Project owner, ProjectFile file) = Seed(
            "design-system/assets/logo.png", "image/png", "logo.png", TinyPng);
        Project other = SeedProject();

        IActionResult result = await Controller().Get(other.Id, file.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        owner.Id.Should().NotBe(other.Id);
    }

    [Fact]
    public async Task The_route_404s_for_a_user_from_a_foreign_organization()
    {
        (Project project, ProjectFile file) = Seed(
            "design-system/assets/logo.png", "image/png", "logo.png", TinyPng);

        IActionResult result = await Controller(organizationId: Guid.NewGuid())
            .Get(project.Id, file.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>(
            "a foreign tenant's project is a 404, never a 403 that confirms it exists");
    }

    [Fact]
    public async Task The_route_forbids_a_billing_user_without_project_access()
    {
        (Project project, ProjectFile file) = Seed(
            "design-system/assets/logo.png", "image/png", "logo.png", TinyPng);
        var user = new FakeUser(_ownerId, HomeOrg, OrganizationRole.Billing);
        var controller = new DesignSystemAssetController(
            _db,
            new ProjectAccessService(_db, user),
            _storage.Object,
            NullLogger<DesignSystemAssetController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        IActionResult result = await controller.Get(project.Id, file.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task The_route_404s_for_a_quarantined_file()
    {
        (Project project, ProjectFile file) = Seed(
            "design-system/assets/logo.png", "image/png", "logo.png", TinyPng);
        file.CsamStatus = CsamScanStatus.Quarantined;
        _db.SaveChanges();

        IActionResult result = await Controller().Get(project.Id, file.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task The_route_only_serves_design_system_assets()
    {
        (Project project, ProjectFile file) = Seed(
            "uploads/logo.png", "image/png", "logo.png", TinyPng);

        IActionResult result = await Controller().Get(project.Id, file.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task An_unservable_type_is_404()
    {
        (Project project, ProjectFile file) = Seed(
            "design-system/assets/spec.pdf", "application/pdf", "spec.pdf",
            Encoding.UTF8.GetBytes("%PDF-1.7"));

        IActionResult result = await Controller().Get(project.Id, file.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private DesignSystemAssetController Controller(Guid? userId = null, Guid? organizationId = null)
    {
        var user = new FakeUser(userId ?? _ownerId, organizationId ?? HomeOrg, OrganizationRole.Owner);
        var controller = new DesignSystemAssetController(
            _db,
            new ProjectAccessService(_db, user),
            _storage.Object,
            NullLogger<DesignSystemAssetController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private Project SeedProject()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Project",
            OwnerId = _ownerId,
            OrganizationId = HomeOrg,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private (Project Project, ProjectFile File) Seed(
        string originalPath, string mimeType, string fileName, byte[] content)
    {
        Project project = SeedProject();
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalPath = originalPath,
            OriginalFileName = fileName,
            Category = "userFiles",
            StorageKey = $"projects/{project.Id}/userFiles/{Guid.NewGuid()}",
            StorageBucket = "test-bucket",
            MimeType = mimeType,
            SizeBytes = content.Length,
        };
        _db.ProjectFiles.Add(file);
        _db.SaveChanges();

        _storage
            .Setup(s => s.DownloadAsync(project.Id, file.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(content));

        return (project, file);
    }

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }
}
