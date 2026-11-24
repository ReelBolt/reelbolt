using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
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
using ReelBolt.Shared.DesignSystems.Merge;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Tenancy;

/// <summary>
/// P4-05 (SPEC 16.1): the project's library link (<c>PUT …/library-link</c>), the
/// <see cref="DesignDocumentMerger.Inherit"/> precedence, the library-aware pin member
/// (<c>libraryVersionId</c> + <c>source</c> + the effective document's digest) and the
/// <see cref="PinnedDocumentLoader"/> effective-document recompute. A project with no own approved
/// design but a linked library inherits the library; a library that approves a newer version after
/// the pin leaves the frozen pin alone; a changed pinned library document is an unusable member (no
/// design block, one warning), never a throw.
/// </summary>
public sealed class LibraryLinkInheritanceTests
{
    private static readonly Guid OrgA = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a5");
    private static readonly Guid OrgB = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b5");
    private static readonly TokenProvenance Prov = new("manual", null, null, null);

    // ---------------------------------------------------------------------------------------------
    // Inherit precedence (SPEC 16.1): child wins, parent fills, no tombstones.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Inherit_child_override_wins_and_parent_fills_the_rest()
    {
        JsonObject parent = DesignDoc("Library");
        DesignDocument.SetToken(parent, "color.brand.primary", "color", Hex("#111111"), Prov, 0.9);
        DesignDocument.SetToken(parent, "color.brand.secondary", "color", Hex("#222222"), Prov, 0.9);

        JsonObject child = DesignDoc("Project Own");
        DesignDocument.SetToken(child, "color.brand.primary", "color", Hex("#6d28d9"), Prov, 0.9);
        DesignDocument.SetToken(child, "color.brand.accent", "color", Hex("#ff0000"), Prov, 0.9);

        JsonObject effective = DesignDocumentMerger.Inherit(parent, child);

        HexOf(effective, "color.brand.primary").Should().Be("#6d28d9", "the child wins");
        HexOf(effective, "color.brand.secondary").Should().Be("#222222", "the parent fills the rest");
        HexOf(effective, "color.brand.accent").Should().Be("#ff0000");
        effective["name"]!.GetValue<string>().Should().Be("Project Own");
    }

    [Fact]
    public void Inherit_has_no_tombstones()
    {
        JsonObject parent = DesignDoc("Library");
        DesignDocument.SetToken(parent, "color.brand.secondary", "color", Hex("#222222"), Prov, 0.9);

        JsonObject child = DesignDoc("Project Own");
        DesignDocument.SetToken(child, "color.brand.primary", "color", Hex("#6d28d9"), Prov, 0.9);

        JsonObject effective = DesignDocumentMerger.Inherit(parent, child);

        HexOf(effective, "color.brand.primary").Should().Be("#6d28d9");
        HexOf(effective, "color.brand.secondary").Should().Be("#222222");
    }

    // ---------------------------------------------------------------------------------------------
    // The pin member (SPEC 16.1): libraryVersionId, source and the effective digest.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Linked_project_without_own_approved_pins_source_library()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        LibrarySeed library = AddLibrary(db, OrgA, owner, "Brand", LibraryDoc());
        AddProjectDesignSystem(db, project.Id, owner, ownDocument: null, parentLibraryId: library.LibraryId);
        await db.SaveChangesAsync();

        DesignSystemPin? pin = DesignSystemPinJson.TryParse(
            await new DesignSystemPinService(db).BuildPinJsonAsync(project.Id, CancellationToken.None));

        pin.Should().NotBeNull();
        pin!.Design.Should().NotBeNull();
        pin.Design!.VersionId.Should().BeNull();
        pin.Design.LibraryVersionId.Should().Be(library.ApprovedVersionId);
        pin.Design.VersionNumber.Should().Be(1);
        pin.Design.Source.Should().Be("library");
        pin.Design.Digest.Should().Be(CanonicalJson.Digest(library.Document));
        pin.ProductMap.Should().BeNull();
    }

    [Fact]
    public async Task Linked_project_with_both_pins_effective_digest_and_project_plus_library()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        JsonObject libraryDoc = LibraryDoc();
        JsonObject ownDoc = OwnDoc();
        LibrarySeed library = AddLibrary(db, OrgA, owner, "Brand", libraryDoc);
        (DesignSystem _, DesignSystemVersion? own) =
            AddProjectDesignSystem(db, project.Id, owner, ownDoc, library.LibraryId);
        await db.SaveChangesAsync();

        DesignSystemPin? pin = DesignSystemPinJson.TryParse(
            await new DesignSystemPinService(db).BuildPinJsonAsync(project.Id, CancellationToken.None));

        JsonObject effective = DesignDocumentMerger.Inherit(libraryDoc, ownDoc);
        pin.Should().NotBeNull();
        pin!.Design!.VersionId.Should().Be(own!.Id);
        pin.Design.LibraryVersionId.Should().Be(library.ApprovedVersionId);
        pin.Design.VersionNumber.Should().Be(own.VersionNumber);
        pin.Design.Source.Should().Be("project+library");
        pin.Design.Digest.Should().Be(CanonicalJson.Digest(effective));
    }

    [Fact]
    public async Task Project_without_any_design_pins_nothing()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        await db.SaveChangesAsync();

        (await new DesignSystemPinService(db).BuildPinJsonAsync(project.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_later_library_approval_does_not_change_the_frozen_pin()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        LibrarySeed library = AddLibrary(db, OrgA, owner, "Brand", LibraryDoc());
        AddProjectDesignSystem(db, project.Id, owner, ownDocument: null, parentLibraryId: library.LibraryId);
        await db.SaveChangesAsync();

        DesignSystemPin? frozen = DesignSystemPinJson.TryParse(
            await new DesignSystemPinService(db).BuildPinJsonAsync(project.Id, CancellationToken.None));

        // The library approves a newer version after the pin was written.
        DesignSystemVersion v2 = AddApprovedVersion(db, library.ContainerId, 2, LibraryDoc2(), owner);
        await db.SaveChangesAsync();

        // The frozen pin still names v1; a fresh pin names v2.
        frozen!.Design!.LibraryVersionId.Should().Be(library.ApprovedVersionId);
        frozen.Design.Digest.Should().Be(CanonicalJson.Digest(library.Document));

        DesignSystemPin? fresh = DesignSystemPinJson.TryParse(
            await new DesignSystemPinService(db).BuildPinJsonAsync(project.Id, CancellationToken.None));
        fresh!.Design!.LibraryVersionId.Should().Be(v2.Id);
        fresh.Design.Digest.Should().NotBe(frozen.Design.Digest);
    }

    // ---------------------------------------------------------------------------------------------
    // The PinnedDocumentLoader recomputes the effective document and verifies its digest.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Loader_shows_the_effective_tokens_of_a_linked_project()
    {
        using InferenceApiDbContext api = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(api, OrgA, owner);
        JsonObject libraryDoc = LibraryDoc();
        JsonObject ownDoc = OwnDoc();
        LibrarySeed library = AddLibrary(api, OrgA, owner, "Brand", libraryDoc);
        (DesignSystem _, DesignSystemVersion? own) =
            AddProjectDesignSystem(api, project.Id, owner, ownDoc, library.LibraryId);
        await api.SaveChangesAsync();

        DesignSystemPin pin = DesignSystemPinJson.TryParse(
            await new DesignSystemPinService(api).BuildPinJsonAsync(project.Id, CancellationToken.None))!;

        using WorkflowEngineDbContext engine = NewEngineDb();
        engine.DesignSystemVersions.Add(LoaderVersion(own!.Id, own.VersionNumber, ownDoc));
        engine.DesignSystemVersions.Add(LoaderVersion(library.ApprovedVersionId, 1, libraryDoc));
        await engine.SaveChangesAsync();

        LoadedProfile profile = await new PinnedDocumentLoader(engine, Mock.Of<ILogger<PinnedDocumentLoader>>())
            .LoadAsync(pin, CancellationToken.None);

        profile.Design.Should().NotBeNull();
        HexOf(profile.Design!, "color.brand.primary").Should().Be("#6d28d9", "the project's own token wins");
        HexOf(profile.Design!, "color.brand.secondary").Should().Be("#222222", "the library fills the rest");
        profile.DesignVersion.Should().Be(own.VersionNumber);
    }

    [Fact]
    public async Task Loader_falls_back_to_the_pinned_library_version_after_a_new_approval()
    {
        using InferenceApiDbContext api = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(api, OrgA, owner);
        JsonObject libraryDoc = LibraryDoc();
        LibrarySeed library = AddLibrary(api, OrgA, owner, "Brand", libraryDoc);
        AddProjectDesignSystem(api, project.Id, owner, ownDocument: null, parentLibraryId: library.LibraryId);
        await api.SaveChangesAsync();

        DesignSystemPin frozen = DesignSystemPinJson.TryParse(
            await new DesignSystemPinService(api).BuildPinJsonAsync(project.Id, CancellationToken.None))!;

        DesignSystemVersion v2 = AddApprovedVersion(api, library.ContainerId, 2, LibraryDoc2(), owner);
        await api.SaveChangesAsync();

        using WorkflowEngineDbContext engine = NewEngineDb();
        engine.DesignSystemVersions.Add(LoaderVersion(library.ApprovedVersionId, 1, libraryDoc));
        engine.DesignSystemVersions.Add(LoaderVersion(v2.Id, 2, LibraryDoc2()));
        await engine.SaveChangesAsync();

        LoadedProfile profile = await new PinnedDocumentLoader(engine, Mock.Of<ILogger<PinnedDocumentLoader>>())
            .LoadAsync(frozen, CancellationToken.None);

        profile.Design.Should().NotBeNull();
        HexOf(profile.Design!, "color.brand.primary").Should().Be("#111111", "the pin still resolves v1");
    }

    [Fact]
    public async Task Loader_is_unusable_when_the_pinned_library_document_changed()
    {
        using InferenceApiDbContext api = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(api, OrgA, owner);
        LibrarySeed library = AddLibrary(api, OrgA, owner, "Brand", LibraryDoc());
        AddProjectDesignSystem(api, project.Id, owner, ownDocument: null, parentLibraryId: library.LibraryId);
        await api.SaveChangesAsync();

        DesignSystemPin pin = DesignSystemPinJson.TryParse(
            await new DesignSystemPinService(api).BuildPinJsonAsync(project.Id, CancellationToken.None))!;

        Mock<ILogger<PinnedDocumentLoader>> logger = new();
        using WorkflowEngineDbContext engine = NewEngineDb();
        // Same id, different document: the recomputed effective digest no longer matches the pin.
        engine.DesignSystemVersions.Add(LoaderVersion(library.ApprovedVersionId, 1, LibraryDoc2()));
        await engine.SaveChangesAsync();

        LoadedProfile profile = await new PinnedDocumentLoader(engine, logger.Object)
            .LoadAsync(pin, CancellationToken.None);

        profile.Design.Should().BeNull("a changed library document makes the member unusable");
        VerifyUnusable(logger);
    }

    // ---------------------------------------------------------------------------------------------
    // PUT /library-link (SPEC 16.1): Edit on the project + the ability to read libraries.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Link_sets_parent_library_id_on_the_project_design_system()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        LibrarySeed library = AddLibrary(db, OrgA, owner, "Brand");
        AddProjectDesignSystem(db, project.Id, owner, ownDocument: null, parentLibraryId: null);
        await db.SaveChangesAsync();

        IActionResult result = await Controller(db, Owner(owner))
            .SetLibraryLink(project.Id, new LibraryLinkRequest(library.LibraryId), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        db.DesignSystems.Single(s => s.ProjectId == project.Id && s.Kind == DesignSystemKinds.Design)
            .ParentLibraryId.Should().Be(library.LibraryId);
    }

    [Fact]
    public async Task Link_creates_the_project_container_when_none_exists()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        LibrarySeed library = AddLibrary(db, OrgA, owner, "Brand");
        await db.SaveChangesAsync();

        IActionResult result = await Controller(db, Owner(owner))
            .SetLibraryLink(project.Id, new LibraryLinkRequest(library.LibraryId), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.DesignSystems.Single(s => s.ProjectId == project.Id && s.Kind == DesignSystemKinds.Design)
            .ParentLibraryId.Should().Be(library.LibraryId);
    }

    [Fact]
    public async Task Unlink_clears_parent_library_id()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        LibrarySeed library = AddLibrary(db, OrgA, owner, "Brand");
        AddProjectDesignSystem(db, project.Id, owner, ownDocument: null, parentLibraryId: library.LibraryId);
        await db.SaveChangesAsync();

        IActionResult result = await Controller(db, Owner(owner))
            .SetLibraryLink(project.Id, new LibraryLinkRequest(null), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        db.DesignSystems.Single(s => s.ProjectId == project.Id && s.Kind == DesignSystemKinds.Design)
            .ParentLibraryId.Should().BeNull();
    }

    [Fact]
    public async Task Link_to_another_organizations_library_is_404_and_changes_nothing()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        LibrarySeed foreign = AddLibrary(db, OrgB, owner, "Foreign");
        AddProjectDesignSystem(db, project.Id, owner, ownDocument: null, parentLibraryId: null);
        await db.SaveChangesAsync();

        IActionResult result = await Controller(db, Owner(owner))
            .SetLibraryLink(project.Id, new LibraryLinkRequest(foreign.LibraryId), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        db.ChangeTracker.Clear();
        db.DesignSystems.Single(s => s.ProjectId == project.Id && s.Kind == DesignSystemKinds.Design)
            .ParentLibraryId.Should().BeNull();
    }

    [Fact]
    public async Task Link_to_a_missing_library_is_404()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        AddProjectDesignSystem(db, project.Id, owner, ownDocument: null, parentLibraryId: null);
        await db.SaveChangesAsync();

        IActionResult result = await Controller(db, Owner(owner))
            .SetLibraryLink(project.Id, new LibraryLinkRequest(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Foreign_project_is_404()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        AddLibrary(db, OrgA, owner, "Brand");
        await db.SaveChangesAsync();

        IActionResult result = await Controller(db, Owner(owner))
            .SetLibraryLink(Guid.NewGuid(), new LibraryLinkRequest(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Billing_is_forbidden()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        LibrarySeed library = AddLibrary(db, OrgA, owner, "Brand");
        AddProjectDesignSystem(db, project.Id, owner, ownDocument: null, parentLibraryId: null);
        await db.SaveChangesAsync();

        ICurrentUser billing = new FakeUser(owner, OrgA, OrganizationRole.Billing);
        IActionResult result = await Controller(db, billing)
            .SetLibraryLink(project.Id, new LibraryLinkRequest(library.LibraryId), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        db.ChangeTracker.Clear();
        db.DesignSystems.Single(s => s.ProjectId == project.Id && s.Kind == DesignSystemKinds.Design)
            .ParentLibraryId.Should().BeNull();
    }

    [Fact]
    public async Task Member_may_link()
    {
        using InferenceApiDbContext db = NewApiDb();
        Guid owner = Guid.NewGuid();
        Project project = AddProject(db, OrgA, owner);
        LibrarySeed library = AddLibrary(db, OrgA, owner, "Brand");
        AddProjectDesignSystem(db, project.Id, owner, ownDocument: null, parentLibraryId: null);
        await db.SaveChangesAsync();

        ICurrentUser member = new FakeUser(Guid.NewGuid(), OrgA, OrganizationRole.Member);
        IActionResult result = await Controller(db, member)
            .SetLibraryLink(project.Id, new LibraryLinkRequest(library.LibraryId), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private sealed record LibrarySeed(
        Guid LibraryId, Guid ContainerId, Guid ApprovedVersionId, JsonObject Document);

    private sealed class FakeUser(
        Guid userId, Guid? organizationId, OrganizationRole? role, bool isPlatformAdmin = false) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = isPlatformAdmin;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private static ICurrentUser Owner(Guid userId) => new FakeUser(userId, OrgA, OrganizationRole.Owner);

    private static InferenceApiDbContext NewApiDb() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"library-link-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static WorkflowEngineDbContext NewEngineDb() => new(
        new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"library-link-engine-{Guid.NewGuid():N}")
            .Options);

    private static DesignSystemsController Controller(InferenceApiDbContext db, ICurrentUser user) => new(
        db,
        user,
        new ProjectAccessService(db, user),
        new DesignSystemService(
            db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
            NullLogger<DesignSystemService>.Instance),
        Mock.Of<IDesignSystemIngestService>(),
        NullLogger<DesignSystemsController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
    };

    private static Project AddProject(InferenceApiDbContext db, Guid organizationId, Guid ownerId, string name = "Project")
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
        db.Projects.Add(project);
        return project;
    }

    private static LibrarySeed AddLibrary(
        InferenceApiDbContext db, Guid organizationId, Guid ownerId, string name, JsonObject? document = null)
    {
        DateTime now = DateTime.UtcNow;
        var container = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = null,
            Kind = DesignSystemKinds.Design,
            Name = name,
            CreatedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var library = new DesignSystemLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name,
            DesignSystemId = container.Id,
            CreatedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.DesignSystems.Add(container);
        db.DesignSystemLibraries.Add(library);

        JsonObject doc = document ?? LibraryDoc();
        DesignSystemVersion approved = AddApprovedVersion(db, container.Id, 1, doc, ownerId, container);

        return new LibrarySeed(library.Id, container.Id, approved.Id, doc);
    }

    private static DesignSystemVersion AddApprovedVersion(
        InferenceApiDbContext db, Guid containerId, int number, JsonObject document, Guid ownerId,
        DesignSystem? container = null)
    {
        var version = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = containerId,
            VersionNumber = number,
            State = DesignVersionState.Approved,
            DocumentJson = CanonicalJson.Serialize(document),
            DocumentDigest = CanonicalJson.Digest(document),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = ownerId,
            CreatedAt = DateTime.UtcNow,
            ApprovedByUserId = ownerId,
            ApprovedAt = DateTime.UtcNow,
        };
        db.DesignSystemVersions.Add(version);
        (container ?? db.DesignSystems.Local.Single(s => s.Id == containerId)).CurrentVersionId = version.Id;
        return version;
    }

    private static (DesignSystem System, DesignSystemVersion? Version) AddProjectDesignSystem(
        InferenceApiDbContext db, Guid projectId, Guid ownerId, JsonObject? ownDocument, Guid? parentLibraryId)
    {
        DateTime now = DateTime.UtcNow;
        var system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = DesignSystemKinds.Design,
            Name = "Project design",
            ParentLibraryId = parentLibraryId,
            CreatedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.DesignSystems.Add(system);

        DesignSystemVersion? version = null;
        if (ownDocument is not null)
        {
            version = new DesignSystemVersion
            {
                Id = Guid.NewGuid(),
                DesignSystemId = system.Id,
                VersionNumber = 1,
                State = DesignVersionState.Approved,
                DocumentJson = CanonicalJson.Serialize(ownDocument),
                DocumentDigest = CanonicalJson.Digest(ownDocument),
                Origin = DesignVersionOrigin.Manual,
                CreatedByUserId = ownerId,
                CreatedAt = now,
                ApprovedByUserId = ownerId,
                ApprovedAt = now,
            };
            db.DesignSystemVersions.Add(version);
            system.CurrentVersionId = version.Id;
        }

        return (system, version);
    }

    private static DesignSystemVersion LoaderVersion(Guid id, int number, JsonObject document) => new()
    {
        Id = id,
        DesignSystemId = Guid.NewGuid(),
        VersionNumber = number,
        State = DesignVersionState.Approved,
        DocumentJson = CanonicalJson.Serialize(document),
        DocumentDigest = CanonicalJson.Digest(document),
        Origin = DesignVersionOrigin.Manual,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static void VerifyUnusable(Mock<ILogger<PinnedDocumentLoader>> logger) =>
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("design_system.pin_unusable", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);

    private static JsonObject DesignDoc(string name) => DesignDocument.New(name);

    private static JsonObject LibraryDoc()
    {
        JsonObject doc = DesignDoc("Library");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", Hex("#111111"), Prov, 0.9);
        DesignDocument.SetToken(doc, "color.brand.secondary", "color", Hex("#222222"), Prov, 0.9);
        return doc;
    }

    private static JsonObject LibraryDoc2()
    {
        JsonObject doc = DesignDoc("Library v2");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", Hex("#999999"), Prov, 0.9);
        DesignDocument.SetToken(doc, "color.brand.secondary", "color", Hex("#222222"), Prov, 0.9);
        return doc;
    }

    private static JsonObject OwnDoc()
    {
        JsonObject doc = DesignDoc("Project Own");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", Hex("#6d28d9"), Prov, 0.9);
        return doc;
    }

    private static JsonObject Hex(string hex) => new() { ["hex"] = hex, ["colorSpace"] = "srgb" };

    private static string HexOf(JsonObject doc, string path) =>
        DesignDocument.FindToken(doc, path)!["$value"]!.AsObject()["hex"]!.GetValue<string>();
}
