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
/// P2-10 behaviour coverage for <see cref="DesignSystemsController"/> and
/// <see cref="DesignSystemService"/>: the manual Draft path (validation + server-side digest),
/// the approve/reject/restore state machine, and the tenancy answers (foreign project 404,
/// foreign version 404, Billing 403). Direct controller construction on an InMemory context,
/// the same harness the cross-tenant matrix uses.
/// </summary>
public sealed class DesignSystemsControllerTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000011");
    private static readonly Guid ForeignOrg = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000012");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _billingId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();

    public DesignSystemsControllerTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-systems-controller-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------------------------------------
    // 1. Manual create
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_draft_manual_valid_returns_201()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystemsController controller = Controller(Owner());

        ActionResult<DesignVersionResponse> response = await controller.CreateVersion(
            project.Id, new CreateDesignVersionRequest("design", ValidDocument()), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);

        DesignVersionResponse version = created.Value.Should().BeOfType<DesignVersionResponse>().Subject;
        version.State.Should().Be("Draft");
        version.Origin.Should().Be("Manual");
        version.VersionNumber.Should().Be(1);

        _db.DesignSystems.Count(s => s.ProjectId == project.Id).Should().Be(1);
    }

    [Fact]
    public async Task Create_draft_invalid_document_returns_400_with_errors()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystemsController controller = Controller(Owner());

        var invalid = new JsonObject
        {
            ["schema"] = DesignDocument.Schema,
            ["not-a-known-group"] = true,
        };

        ActionResult<DesignVersionResponse> response = await controller.CreateVersion(
            project.Id, new CreateDesignVersionRequest("design", invalid), CancellationToken.None);

        BadRequestObjectResult badRequest = response.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        JsonObject body = Body(badRequest.Value);
        body["errors"].Should().NotBeNull();
        JsonArray errors = body["errors"]!.AsArray();
        errors.Count.Should().BeGreaterThan(0);

        JsonObject first = errors[0]!.AsObject();
        first["code"].Should().NotBeNull();
        first["path"].Should().NotBeNull();
        first["message"].Should().NotBeNull();
    }

    // ---------------------------------------------------------------------------------------------------
    // 2. Approve / reject / restore
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approve_flow_supersedes_previous_approved()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystemsController controller = Controller(Owner());

        DesignVersionResponse v1 = await CreateDraftAsync(controller, project.Id);
        (await controller.Approve(project.Id, v1.Id, CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>();

        DesignVersionResponse v2 = await CreateDraftAsync(controller, project.Id);
        (await controller.Approve(project.Id, v2.Id, CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>();

        _db.ChangeTracker.Clear();
        _db.DesignSystemVersions.AsNoTracking().Single(v => v.Id == v1.Id).State
            .Should().Be(DesignVersionState.Superseded);
        _db.DesignSystemVersions.AsNoTracking().Single(v => v.Id == v2.Id).State
            .Should().Be(DesignVersionState.Approved);
        _db.DesignSystems.AsNoTracking().Single(s => s.ProjectId == project.Id).CurrentVersionId
            .Should().Be(v2.Id);
    }

    [Fact]
    public async Task Approve_non_draft_returns_409_NOT_DRAFT()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystemsController controller = Controller(Owner());
        DesignVersionResponse v1 = await CreateDraftAsync(controller, project.Id);
        (await controller.Approve(project.Id, v1.Id, CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>();

        ActionResult<DesignVersionResponse> response = await controller.Approve(project.Id, v1.Id, CancellationToken.None);

        ConflictObjectResult conflict = response.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        Body(conflict.Value)["code"]!.GetValue<string>().Should().Be("NOT_DRAFT");
    }

    [Fact]
    public async Task Approve_stale_base_returns_409_STALE_BASE()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id, _ownerId);
        DesignSystemVersion v1 = SeedVersion(system.Id, 1, DesignVersionState.Approved, _ownerId);
        DesignSystemVersion v2 = SeedVersion(system.Id, 2, DesignVersionState.Approved, _ownerId);
        DesignSystemVersion v3 = SeedVersion(system.Id, 3, DesignVersionState.Draft, _ownerId, baseVersionId: v1.Id);
        system.CurrentVersionId = v2.Id;
        _db.SaveChanges();

        ActionResult<DesignVersionResponse> response = await Controller(Owner())
            .Approve(project.Id, v3.Id, CancellationToken.None);

        ConflictObjectResult conflict = response.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        Body(conflict.Value)["code"]!.GetValue<string>().Should().Be("STALE_BASE");
    }

    [Fact]
    public async Task Restore_of_a_non_current_version_can_be_approved_and_becomes_current()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystemsController controller = Controller(Owner());
        DesignVersionResponse v1 = await CreateDraftAsync(controller, project.Id);
        (await controller.Approve(project.Id, v1.Id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        DesignVersionResponse v2 = await CreateDraftAsync(controller, project.Id);
        (await controller.Approve(project.Id, v2.Id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();

        ActionResult<DesignVersionResponse> restore = await controller.Restore(project.Id, v1.Id, CancellationToken.None);
        DesignVersionResponse restored = restore.Result.Should().BeOfType<ObjectResult>().Subject
            .Value.Should().BeOfType<DesignVersionResponse>().Subject;
        restored.State.Should().Be("Draft");

        ActionResult<DesignVersionResponse> approve = await controller.Approve(project.Id, restored.Id, CancellationToken.None);

        approve.Result.Should().BeOfType<OkObjectResult>();
        _db.ChangeTracker.Clear();
        _db.DesignSystems.AsNoTracking().Single(s => s.ProjectId == project.Id).CurrentVersionId
            .Should().Be(restored.Id);
        _db.DesignSystemVersions.AsNoTracking().Single(v => v.Id == v2.Id).State
            .Should().Be(DesignVersionState.Superseded);
        _db.DesignSystemVersions.AsNoTracking().Single(v => v.Id == v1.Id).State
            .Should().Be(DesignVersionState.Superseded);
    }

    [Fact]
    public async Task Restore_of_a_draft_returns_409_NOT_DRAFT()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystemsController controller = Controller(Owner());
        DesignVersionResponse draft = await CreateDraftAsync(controller, project.Id);

        ActionResult<DesignVersionResponse> response = await controller.Restore(project.Id, draft.Id, CancellationToken.None);

        Body(response.Result.Should().BeOfType<ConflictObjectResult>().Subject.Value)["code"]!
            .GetValue<string>().Should().Be("NOT_DRAFT");
    }

    [Fact]
    public async Task Summary_reports_the_linked_parent_library()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id, _ownerId);
        DesignSystemLibrary library = SeedLibrary(HomeOrg, "Acme brand");
        system.ParentLibraryId = library.Id;
        _db.SaveChanges();

        ActionResult<DesignSystemsSummaryResponse> response = await Controller(Owner())
            .GetSummary(project.Id, CancellationToken.None);

        DesignSystemsSummaryResponse summary = response.Value
            ?? response.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DesignSystemsSummaryResponse>().Subject;
        summary.Design!.ParentLibrary.Should().NotBeNull();
        summary.Design.ParentLibrary!.Id.Should().Be(library.Id);
        summary.Design.ParentLibrary.Name.Should().Be("Acme brand");
    }

    [Fact]
    public async Task Summary_never_reports_a_parent_library_of_another_organization()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id, _ownerId);
        DesignSystemLibrary foreign = SeedLibrary(Guid.NewGuid(), "Not yours");
        system.ParentLibraryId = foreign.Id;
        _db.SaveChanges();

        ActionResult<DesignSystemsSummaryResponse> response = await Controller(Owner())
            .GetSummary(project.Id, CancellationToken.None);

        DesignSystemsSummaryResponse summary = response.Value
            ?? response.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DesignSystemsSummaryResponse>().Subject;
        summary.Design!.ParentLibrary.Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------------------
    // 3. Tenancy and digest
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_version_from_another_project_returns_404()
    {
        Project mine = SeedProject(HomeOrg, _ownerId, "Mine");
        Project other = SeedProject(HomeOrg, _ownerId, "Other");
        DesignSystem system = SeedSystem(other.Id, _ownerId);
        DesignSystemVersion version = SeedVersion(system.Id, 1, DesignVersionState.Draft, _ownerId);
        _db.SaveChanges();

        ActionResult<DesignVersionResponse> response = await Controller(Owner())
            .GetVersion(mine.Id, version.Id, CancellationToken.None);

        response.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Project_in_another_org_returns_404()
    {
        Project mine = SeedProject(HomeOrg, _ownerId);

        ActionResult<IReadOnlyList<DesignVersionSummaryResponse>> response = await Controller(
                new FakeUser(_strangerId, ForeignOrg, OrganizationRole.Member))
            .ListVersions(mine.Id, "design", CancellationToken.None);

        response.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Billing_role_cannot_approve_returns_403()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        DesignSystem system = SeedSystem(project.Id, _ownerId);
        DesignSystemVersion version = SeedVersion(system.Id, 1, DesignVersionState.Draft, _ownerId);
        _db.SaveChanges();

        ActionResult<DesignVersionResponse> response = await Controller(
                new FakeUser(_billingId, HomeOrg, OrganizationRole.Billing))
            .Approve(project.Id, version.Id, CancellationToken.None);

        response.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Digest_is_recomputed_server_side()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        JsonObject document = ValidDocument();

        DesignVersionResponse version = await CreateDraftAsync(Controller(Owner()), project.Id, document);

        version.DocumentDigest.Should().Be(CanonicalJson.Digest(document));
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

    private static async Task<DesignVersionResponse> CreateDraftAsync(
        DesignSystemsController controller, Guid projectId, JsonObject? document = null)
    {
        ActionResult<DesignVersionResponse> response = await controller.CreateVersion(
            projectId,
            new CreateDesignVersionRequest("design", document ?? ValidDocument()),
            CancellationToken.None);

        return ((ObjectResult)response.Result!).Value.Should().BeOfType<DesignVersionResponse>().Subject;
    }

    private static JsonObject Body(object? value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

    private DesignSystemLibrary SeedLibrary(Guid organizationId, string name)
    {
        var container = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = null,
            Kind = DesignSystemKinds.Design,
            Name = name,
            CreatedByUserId = _ownerId,
        };
        _db.DesignSystems.Add(container);
        var library = new DesignSystemLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name,
            DesignSystemId = container.Id,
            CreatedByUserId = _ownerId,
        };
        _db.DesignSystemLibraries.Add(library);
        _db.SaveChanges();
        return library;
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

    private DesignSystemVersion SeedVersion(
        Guid systemId, int versionNumber, DesignVersionState state, Guid userId, Guid? baseVersionId = null)
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
            BaseVersionId = baseVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
            ApprovedByUserId = state == DesignVersionState.Approved ? userId : null,
            ApprovedAt = state == DesignVersionState.Approved ? DateTime.UtcNow : null,
        };
        _db.DesignSystemVersions.Add(version);
        return version;
    }
}
