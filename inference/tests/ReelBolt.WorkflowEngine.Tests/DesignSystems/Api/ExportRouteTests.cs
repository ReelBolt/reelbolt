using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// P4-08 coverage for <c>GET /design-system/export?kind=&amp;format=</c> (SPEC 16.2): every design
/// format is an attachment with <c>nosniff</c> and the right content type, the product map exports as
/// json only, an unknown format is a 400 before any read, a missing approved document is a 404 and a
/// foreign project is a 404. The last test pins the effective document for a project linked to an
/// organization library, the same inheritance the pin freezes. The harness mirrors
/// <see cref="DesignSystemPreviewTests"/>.
/// </summary>
public sealed class ExportRouteTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000031");
    private static readonly Guid ForeignOrg = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000032");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();

    public ExportRouteTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-system-export-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("dtcg", "application/json", "design-system.dtcg.json", "\"com.reelbolt\"")]
    [InlineData("css", "text/css", "design-system.css", "--color-primary: #6d28d9;")]
    [InlineData("css-tailwind", "text/css", "design-system.tailwind.css", "@theme {")]
    [InlineData("designmd", "text/markdown", "DESIGN.md", "## Colors")]
    [InlineData("json", "application/json", "design-system.json", "\"schema\"")]
    public async Task Export_serves_each_design_format_as_an_attachment(
        string format, string contentType, string fileName, string fragment)
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedApproved(project.Id, _ownerId, DesignSystemKinds.Design, ValidDocument("Brand"));

        DesignSystemsController controller = Controller(Owner());
        IActionResult result = await controller.Export(project.Id, null, format, CancellationToken.None);

        FileContentResult file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be(contentType);
        file.FileDownloadName.Should().Be(fileName);
        Encoding.UTF8.GetString(file.FileContents).Should().Contain(fragment);
        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        controller.Response.Headers["Content-Security-Policy"].ToString().Should().Contain("sandbox");
        controller.Response.Headers["Cross-Origin-Resource-Policy"].ToString().Should().Be("same-origin");
        controller.Response.Headers["Content-Disposition"].ToString().Should().StartWith("attachment");
    }

    [Fact]
    public async Task Export_defaults_an_empty_format_to_json()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedApproved(project.Id, _ownerId, DesignSystemKinds.Design, ValidDocument("Brand"));

        FileContentResult file = (await Controller(Owner()).Export(project.Id, null, null, CancellationToken.None))
            .Should().BeOfType<FileContentResult>().Subject;

        file.ContentType.Should().Be("application/json");
        file.FileDownloadName.Should().Be("design-system.json");
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("scss")]
    [InlineData("pdf")]
    public async Task Export_unknown_format_is_400(string format)
    {
        // No approved document: the format is validated first, so the answer is the 400, not the 404.
        Project project = SeedProject(HomeOrg, _ownerId);

        BadRequestObjectResult bad = (await Controller(Owner())
                .Export(project.Id, null, format, CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>().Subject;

        Body(bad.Value)["code"]!.GetValue<string>().Should().Be("UNSUPPORTED_FORMAT");
    }

    [Fact]
    public async Task Export_product_map_is_json_only()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedApproved(project.Id, _ownerId, DesignSystemKinds.ProductMap, ProductMapDocument());

        DesignSystemsController controller = Controller(Owner());

        FileContentResult json = (await controller.Export(project.Id, "product_map", "json", CancellationToken.None))
            .Should().BeOfType<FileContentResult>().Subject;
        json.ContentType.Should().Be("application/json");
        json.FileDownloadName.Should().Be("product-map.json");

        BadRequestObjectResult refused = (await controller.Export(project.Id, "product_map", "css", CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>().Subject;
        Body(refused.Value)["code"]!.GetValue<string>().Should().Be("UNSUPPORTED_FORMAT");
    }

    [Fact]
    public async Task Export_without_an_approved_document_is_404()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedSystem(project.Id, _ownerId, DesignSystemKinds.Design);

        IActionResult result = await Controller(Owner()).Export(project.Id, null, "json", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Export_of_a_foreign_project_is_404()
    {
        Project mine = SeedProject(HomeOrg, _ownerId);
        SeedApproved(mine.Id, _ownerId, DesignSystemKinds.Design, ValidDocument("Brand"));

        IActionResult result = await Controller(new FakeUser(_strangerId, ForeignOrg, OrganizationRole.Member))
            .Export(mine.Id, null, "json", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Export_merges_the_linked_library_into_the_effective_document()
    {
        Project project = SeedProject(HomeOrg, _ownerId);

        DesignSystem libraryContainer = SeedSystem(Guid.NewGuid(), _ownerId, DesignSystemKinds.Design);
        DesignSystemVersion libraryVersion = SeedVersion(
            libraryContainer.Id, 1, DesignVersionState.Approved, _ownerId, ValidDocument("Library"));
        libraryContainer.CurrentVersionId = libraryVersion.Id;

        var library = new DesignSystemLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = HomeOrg,
            Name = "Acme brand",
            DesignSystemId = libraryContainer.Id,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.DesignSystemLibraries.Add(library);

        DesignSystem own = SeedSystem(project.Id, _ownerId, DesignSystemKinds.Design);
        var ownDocument = (JsonObject)ValidDocument("Own")!;
        ownDocument["voice"] = new JsonObject { ["summary"] = "Calm and clear." };
        DesignSystemVersion ownVersion = SeedVersion(own.Id, 1, DesignVersionState.Approved, _ownerId, ownDocument);
        own.CurrentVersionId = ownVersion.Id;
        own.ParentLibraryId = library.Id;
        _db.SaveChanges();

        FileContentResult file = (await Controller(Owner()).Export(project.Id, null, "json", CancellationToken.None))
            .Should().BeOfType<FileContentResult>().Subject;

        JsonObject exported = JsonNode.Parse(Encoding.UTF8.GetString(file.FileContents))!.AsObject();
        exported["color"]!["primary"]!["$value"]!["hex"]!.GetValue<string>().Should().Be("#6d28d9");
        exported["voice"]!["summary"]!.GetValue<string>().Should().Be("Calm and clear.");
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private ICurrentUser Owner() => new FakeUser(_ownerId, HomeOrg, OrganizationRole.Owner);

    private DesignSystemsController Controller(ICurrentUser user)
    {
        var controller = new DesignSystemsController(
            _db,
            user,
            new ProjectAccessService(_db, user),
            new DesignSystemService(
                _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
                NullLogger<DesignSystemService>.Instance),
            Mock.Of<IDesignSystemIngestService>(),
            NullLogger<DesignSystemsController>.Instance);

        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static JsonObject ValidDocument(string name) => new()
    {
        ["schema"] = DesignDocument.Schema,
        ["name"] = name,
        ["color"] = new JsonObject
        {
            ["primary"] = new JsonObject
            {
                ["$type"] = "color",
                ["$value"] = new JsonObject { ["hex"] = "#6d28d9", ["colorSpace"] = "srgb" },
            },
        },
        ["spacing"] = new JsonObject
        {
            ["sm"] = new JsonObject
            {
                ["$type"] = "dimension",
                ["$value"] = new JsonObject { ["value"] = 4, ["unit"] = "px" },
            },
        },
        ["voice"] = new JsonObject { ["summary"] = "Calm and clear." },
    };

    private static JsonObject ProductMapDocument() => new()
    {
        ["schema"] = "reelbolt.product-map.v1",
        ["framework"] = new JsonObject { ["name"] = "Next.js" },
    };

    private static JsonObject Body(object? value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

    private void SeedApproved(Guid projectId, Guid userId, string kind, JsonObject document)
    {
        DesignSystem system = SeedSystem(projectId, userId, kind);
        DesignSystemVersion version = SeedVersion(system.Id, 1, DesignVersionState.Approved, userId, document);
        system.CurrentVersionId = version.Id;
        _db.SaveChanges();
    }

    private Project SeedProject(Guid organizationId, Guid ownerId, string name = "Project")
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = ownerId,
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private DesignSystem SeedSystem(Guid projectId, Guid userId, string kind)
    {
        var system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = kind,
            Name = "Design system",
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.DesignSystems.Add(system);
        return system;
    }

    private DesignSystemVersion SeedVersion(
        Guid systemId, int versionNumber, DesignVersionState state, Guid userId, JsonObject document)
    {
        var version = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = systemId,
            VersionNumber = versionNumber,
            State = state,
            DocumentJson = CanonicalJson.Serialize(document),
            DocumentDigest = CanonicalJson.Digest(document),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
            ApprovedByUserId = state == DesignVersionState.Approved ? userId : null,
            ApprovedAt = state == DesignVersionState.Approved ? DateTime.UtcNow : null,
        };
        _db.DesignSystemVersions.Add(version);
        return version;
    }
}
