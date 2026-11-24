using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
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
/// P3-04 coverage for <c>GET /versions/{id}/diff?against={otherId}</c>: the design shape, the
/// product shape, the missing-<c>against</c> 400, the foreign-id 404 and the cross-kind 400. Direct
/// controller construction on an InMemory context, the same harness the other design-system API
/// tests use.
/// </summary>
public sealed class DesignVersionDiffRouteTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000031");
    private static readonly Guid ForeignOrg = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000032");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();

    public DesignVersionDiffRouteTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-version-diff-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Design_diff_changed_color_returns_changed_entry()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id, DesignSystemKinds.Design);
        DesignSystemVersion v1 = SeedVersion(system.Id, 1, DesignJson("#6d28d9"));
        DesignSystemVersion v2 = SeedVersion(system.Id, 2, DesignJson("#7c3aed"));
        _db.SaveChanges();

        IActionResult result = await Controller()
            .GetVersionDiff(project.Id, v2.Id, v1.Id, CancellationToken.None);

        JsonObject body = Body(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        body["counts"]!["changed"]!.GetValue<int>().Should().Be(1);
        JsonNode changed = body["changed"]!.AsArray()[0]!;
        changed["path"]!.GetValue<string>().Should().Be("color.primary");
        changed["before"]!["hex"]!.GetValue<string>().Should().Be("#6d28d9");
        changed["after"]!["hex"]!.GetValue<string>().Should().Be("#7c3aed");
    }

    [Fact]
    public async Task Diff_missing_against_returns_400()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id, DesignSystemKinds.Design);
        DesignSystemVersion v1 = SeedVersion(system.Id, 1, DesignJson("#6d28d9"));
        _db.SaveChanges();

        IActionResult result = await Controller()
            .GetVersionDiff(project.Id, v1.Id, Guid.Empty, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Diff_version_from_another_project_returns_404()
    {
        Project mine = SeedProject(HomeOrg, _ownerId, "Mine");
        Project other = SeedProject(HomeOrg, _ownerId, "Other");
        DesignSystem mineSystem = SeedSystem(mine.Id, DesignSystemKinds.Design);
        DesignSystem otherSystem = SeedSystem(other.Id, DesignSystemKinds.Design);
        DesignSystemVersion mineVersion = SeedVersion(mineSystem.Id, 1, DesignJson("#6d28d9"));
        DesignSystemVersion otherVersion = SeedVersion(otherSystem.Id, 1, DesignJson("#7c3aed"));
        _db.SaveChanges();

        IActionResult result = await Controller()
            .GetVersionDiff(mine.Id, mineVersion.Id, otherVersion.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Diff_cross_kind_returns_400()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem design = SeedSystem(project.Id, DesignSystemKinds.Design);
        DesignSystem product = SeedSystem(project.Id, DesignSystemKinds.ProductMap);
        DesignSystemVersion designVersion = SeedVersion(design.Id, 1, DesignJson("#6d28d9"));
        DesignSystemVersion productVersion = SeedVersion(product.Id, 1, ProductDocument([]));
        _db.SaveChanges();

        IActionResult result = await Controller()
            .GetVersionDiff(project.Id, designVersion.Id, productVersion.Id, CancellationToken.None);

        BadRequestObjectResult badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Body(badRequest.Value)["code"]!.GetValue<string>().Should().Be("DIFF_KIND_MISMATCH");
    }

    [Fact]
    public async Task Product_diff_returns_product_shape()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id, DesignSystemKinds.ProductMap);
        DesignSystemVersion v1 = SeedVersion(system.Id, 1, ProductDocument([]));
        DesignSystemVersion v2 = SeedVersion(system.Id, 2, ProductDocument(["/pricing"]));
        _db.SaveChanges();

        IActionResult result = await Controller()
            .GetVersionDiff(project.Id, v2.Id, v1.Id, CancellationToken.None);

        JsonObject body = Body(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        body["product"]!["routes"]!["added"]!.AsArray()
            .Should().ContainSingle(route => route!.GetValue<string>() == "/pricing");
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

    private DesignSystemsController Controller() => new(
        _db,
        new FakeUser(_ownerId, HomeOrg, OrganizationRole.Owner),
        new ProjectAccessService(_db, new FakeUser(_ownerId, HomeOrg, OrganizationRole.Owner)),
        new DesignSystemService(
            _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
            NullLogger<DesignSystemService>.Instance),
        Mock.Of<IDesignSystemIngestService>(),
        NullLogger<DesignSystemsController>.Instance);

    private static string DesignJson(string hex) => new JsonObject
    {
        ["schema"] = DesignDocument.Schema,
        ["color"] = new JsonObject
        {
            ["primary"] = new JsonObject
            {
                ["$type"] = "color",
                ["$value"] = new JsonObject { ["hex"] = hex },
            },
        },
    }.ToJsonString();

    private static string ProductDocument(IReadOnlyList<string> routes)
    {
        var routeNodes = new JsonArray();
        foreach (string path in routes)
        {
            routeNodes.Add(new JsonObject
            {
                ["path"] = path,
                ["file"] = "app/page.tsx",
                ["kind"] = "page",
                ["requiresAuth"] = false,
            });
        }

        return new JsonObject
        {
            ["schema"] = ProductMapDocument.SchemaId,
            ["framework"] = new JsonObject
            {
                ["name"] = "Next.js",
                ["version"] = "15",
                ["language"] = "TypeScript",
                ["styling"] = new JsonArray(),
                ["packageManager"] = "npm",
                ["projectType"] = "web",
            },
            ["dependencies"] = new JsonObject
            {
                ["runtime"] = new JsonArray(),
                ["dev"] = new JsonArray(),
                ["ui"] = new JsonArray(),
            },
            ["routes"] = routeNodes,
        }.ToJsonString();
    }

    private static JsonObject Body(object? value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

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

    private DesignSystem SeedSystem(Guid projectId, string kind)
    {
        var system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = kind,
            Name = kind,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.DesignSystems.Add(system);
        _db.SaveChanges();
        return system;
    }

    private DesignSystemVersion SeedVersion(Guid systemId, int versionNumber, string documentJson)
    {
        JsonObject document = JsonNode.Parse(documentJson)!.AsObject();
        var version = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = systemId,
            VersionNumber = versionNumber,
            State = DesignVersionState.Approved,
            DocumentJson = documentJson,
            DocumentDigest = CanonicalJson.Digest(document),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
        };
        _db.DesignSystemVersions.Add(version);
        return version;
    }
}
