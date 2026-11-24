using System;
using System.Collections.Generic;
using System.Linq;
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
using ReelBolt.Inference.Api.Controllers.Dto;
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
/// P2-16 coverage for <c>GET /design-system/preview</c>: the current approved version's block, the
/// empty answers (no approved version, a role without a view), the 400s (unknown kind/role) and the
/// tenancy answer (foreign project 404). The harness mirrors
/// <see cref="DesignSystemsControllerTests"/> so both pin the same construction.
/// </summary>
public sealed class DesignSystemPreviewTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000021");
    private static readonly Guid ForeignOrg = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000022");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();

    public DesignSystemPreviewTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-system-preview-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Preview_returns_the_block_of_the_current_approved_version()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedApproved(project.Id, _ownerId);

        ActionResult<PreviewResponse> response = await Controller(Owner())
            .Preview(project.Id, null, "AuthorAgent", CancellationToken.None);

        OkObjectResult ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        PreviewResponse preview = ok.Value.Should().BeOfType<PreviewResponse>().Subject;

        preview.Block.Should().StartWith("[DESIGN SYSTEM v1]");
        preview.Chars.Should().Be(preview.Block.Length);
    }

    [Fact]
    public async Task Preview_without_approved_version_is_empty()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id, _ownerId);
        DesignSystemVersion draft = SeedVersion(system.Id, 1, DesignVersionState.Draft, _ownerId);
        system.CurrentVersionId = draft.Id;
        _db.SaveChanges();

        ActionResult<PreviewResponse> response = await Controller(Owner())
            .Preview(project.Id, null, "AuthorAgent", CancellationToken.None);

        OkObjectResult ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        PreviewResponse preview = ok.Value.Should().BeOfType<PreviewResponse>().Subject;

        preview.Block.Should().BeEmpty();
        preview.Chars.Should().Be(0);
    }

    [Theory]
    [InlineData("Nope")]
    [InlineData("7")]
    public async Task Preview_unknown_role_is_400(string role)
    {
        Project project = SeedProject(HomeOrg, _ownerId);

        ActionResult<PreviewResponse> response = await Controller(Owner())
            .Preview(project.Id, null, role, CancellationToken.None);

        BadRequestObjectResult bad = response.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        Body(bad.Value)["code"]!.GetValue<string>().Should().Be("INVALID_ROLE");
    }

    [Fact]
    public async Task Preview_role_is_case_insensitive()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedApproved(project.Id, _ownerId);

        ActionResult<PreviewResponse> response = await Controller(Owner())
            .Preview(project.Id, null, "authoragent", CancellationToken.None);

        OkObjectResult ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        PreviewResponse preview = ok.Value.Should().BeOfType<PreviewResponse>().Subject;

        preview.Block.Should().StartWith("[DESIGN SYSTEM v1]");
    }

    [Fact]
    public async Task Preview_of_a_foreign_project_is_404()
    {
        Project mine = SeedProject(HomeOrg, _ownerId);

        ActionResult<PreviewResponse> response = await Controller(
                new FakeUser(_strangerId, ForeignOrg, OrganizationRole.Member))
            .Preview(mine.Id, null, "AuthorAgent", CancellationToken.None);

        response.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Preview_for_role_without_view_is_empty()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedApproved(project.Id, _ownerId);

        ActionResult<PreviewResponse> response = await Controller(Owner())
            .Preview(project.Id, null, "CodeStructureAnalyzer", CancellationToken.None);

        OkObjectResult ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        PreviewResponse preview = ok.Value.Should().BeOfType<PreviewResponse>().Subject;

        preview.Block.Should().BeEmpty();
        preview.Chars.Should().Be(0);
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

    private DesignSystemsController Controller(ICurrentUser user) => new(
        _db,
        user,
        new ProjectAccessService(_db, user),
        new DesignSystemService(
            _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
            NullLogger<DesignSystemService>.Instance),
        Mock.Of<IDesignSystemIngestService>(),
        NullLogger<DesignSystemsController>.Instance);

    private static JsonObject ValidDocument() => new()
    {
        ["schema"] = DesignDocument.Schema,
        ["color"] = new JsonObject
        {
            ["primary"] = new JsonObject
            {
                ["$type"] = "color",
                ["$value"] = new JsonObject { ["hex"] = "#6d28d9", ["colorSpace"] = "srgb" },
            },
        },
    };

    private static JsonObject Body(object? value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

    private void SeedApproved(Guid projectId, Guid userId)
    {
        DesignSystem system = SeedSystem(projectId, userId);
        DesignSystemVersion version = SeedVersion(system.Id, 1, DesignVersionState.Approved, userId);
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

    private DesignSystem SeedSystem(Guid projectId, Guid userId)
    {
        var system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = DesignSystemKinds.Design,
            Name = "Design system",
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.DesignSystems.Add(system);
        return system;
    }

    private DesignSystemVersion SeedVersion(Guid systemId, int versionNumber, DesignVersionState state, Guid userId)
    {
        var version = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = systemId,
            VersionNumber = versionNumber,
            State = state,
            DocumentJson = CanonicalJson.Serialize(ValidDocument()),
            DocumentDigest = CanonicalJson.Digest(ValidDocument()),
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
