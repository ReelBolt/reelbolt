using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// T-010: an organization brand library owns inert text/vector assets — a sanitized SVG or one of the
/// four font containers. The route lives under the org library, every lookup is scoped
/// <c>id + organization</c> (a foreign/missing library or asset is 404, never 403), only the
/// allowlisted types are accepted and an SVG is rebuilt by the sanitizer before it is stored and again
/// before it is served.
/// </summary>
public sealed class LibraryAssetRouteTests : IDisposable
{
    private static readonly Guid OrgA = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid OrgB = Guid.Parse("eeeeeeee-0000-0000-0000-000000000002");

    private static readonly byte[] TinyPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    ];

    private static readonly byte[] TinyWoff2 = [0x77, 0x4F, 0x46, 0x32, 0x00, 0x01, 0x00, 0x00];

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly InMemoryLibraryAssetStorage _storage = new();

    public LibraryAssetRouteTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"library-assets-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_library_can_own_a_sanitized_svg_and_reference_it()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");

        LibraryAssetUploadResponse uploaded = await UploadSvg(library.Id, "<svg><path d='M0 0'/></svg>");

        uploaded.Status.Should().Be(StatusCodes.Status201Created);
        uploaded.Asset.Should().NotBeNull();
        uploaded.Asset!.MimeType.Should().Be("image/svg+xml");
        uploaded.Asset.Name.Should().Be("acme-logo.svg");
        uploaded.Asset.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");

        JsonObject doc = DocumentWithAsset(uploaded.Asset.Id, "acme-logo.svg");

        DesignValidationResult result = DesignDocumentValidator.Validate(doc, LibraryContext(library.Id));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_foreign_library_asset_is_404_on_read_and_invalid_in_a_document()
    {
        DesignSystemLibrary libraryA = SeedLibrary(OrgA, "Acme");
        LibraryAssetUploadResponse uploaded = await UploadSvg(libraryA.Id, "<svg><path d='M0 0'/></svg>");
        DesignSystemLibrary libraryB = SeedLibrary(OrgB, "Other");

        IActionResult foreignRead = await Controller(User(OrgB))
            .GetAssetContent(libraryA.Id, uploaded.Asset!.Id, CancellationToken.None);
        (foreignRead is NotFoundResult || foreignRead is NotFoundObjectResult).Should().BeTrue(
            "a foreign library is 404 with the controller's LIBRARY_NOT_FOUND body, never 403");

        IActionResult foreignList = (await Controller(User(OrgB))
            .ListAssets(libraryA.Id, CancellationToken.None)).Result!;
        foreignList.Should().BeOfType<NotFoundObjectResult>();

        DesignValidationResult validation = DesignDocumentValidator.Validate(
            DocumentWithAsset(uploaded.Asset.Id, "acme-logo.svg"), LibraryContext(libraryB.Id));
        validation.IsValid.Should().BeFalse();
        validation.Violations.Should().Contain(v => v.Code == "DS_V15");
    }

    [Fact]
    public async Task A_hostile_svg_is_stored_only_after_the_script_is_dropped()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");

        LibraryAssetUploadResponse uploaded =
            await UploadSvg(library.Id, "<svg><script>alert(1)</script><path d='M0 0'/></svg>");

        uploaded.Status.Should().Be(StatusCodes.Status201Created);
        uploaded.Asset.Should().NotBeNull();
        byte[] stored = _storage.Objects.Should().ContainSingle().Subject.Value;
        Encoding.UTF8.GetString(stored).Should().Contain("<path").And.NotContain("script");

        // The content route re-sanitizes and serves the same inert document.
        IActionResult result = await Controller(User(OrgA))
            .GetAssetContent(library.Id, uploaded.Asset!.Id, CancellationToken.None);
        FileContentResult content = result.Should().BeOfType<FileContentResult>().Subject;
        Encoding.UTF8.GetString(content.FileContents).Should().Contain("<path").And.NotContain("script");
    }

    [Fact]
    public async Task A_c2pa_metadata_svg_is_stored_without_metadata()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");
        const string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:c2pa=\"http://c2pa.org/manifest\">" +
            "<metadata><c2pa:manifest>{\"claim_generator\":\"acme\"}</c2pa:manifest></metadata>" +
            "<path d=\"M0 0\"/></svg>";

        LibraryAssetUploadResponse uploaded = await UploadSvg(library.Id, svg);

        uploaded.Status.Should().Be(StatusCodes.Status201Created);
        uploaded.Asset!.MimeType.Should().Be("image/svg+xml");
        string stored = Encoding.UTF8.GetString(_storage.Objects.Should().ContainSingle().Subject.Value);
        stored.Should().Contain("<path").And.NotContain("c2pa").And.NotContain("metadata");
    }

    [Fact]
    public async Task A_malformed_svg_is_refused_at_upload()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");

        LibraryAssetUploadResponse uploaded = await UploadSvg(library.Id, "<svg><path d='M0 0'");

        uploaded.Status.Should().Be(StatusCodes.Status422UnprocessableEntity);
        uploaded.Code.Should().Be("LIBRARY_ASSET_UNSAFE");
        uploaded.Asset.Should().BeNull();
        _storage.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task A_raster_upload_is_refused_by_the_library_route()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");

        LibraryAssetUploadResponse uploaded = await Upload(library.Id, TinyPng, "logo.png", "image/png");

        uploaded.Status.Should().Be(StatusCodes.Status415UnsupportedMediaType);
        uploaded.Code.Should().Be("LIBRARY_ASSET_TYPE");
        _storage.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task A_font_upload_is_accepted_and_served_as_an_attachment()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");

        LibraryAssetUploadResponse uploaded =
            await Upload(library.Id, TinyWoff2, "brand.woff2", "font/woff2");

        uploaded.Status.Should().Be(StatusCodes.Status201Created);
        uploaded.Asset!.MimeType.Should().Be("font/woff2");

        OrgDesignLibrariesController controller = Controller(User(OrgA));
        IActionResult result = await controller.GetAssetContent(library.Id, uploaded.Asset.Id, CancellationToken.None);

        FileContentResult content = result.Should().BeOfType<FileContentResult>().Subject;
        content.ContentType.Should().Be("application/octet-stream");
        content.FileContents.Should().Equal(TinyWoff2);
        controller.Response.Headers["Content-Disposition"].ToString().Should().StartWith("attachment");
    }

    [Fact]
    public async Task An_uploaded_svg_is_served_rebuilt_under_a_sandbox_csp()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");
        LibraryAssetUploadResponse uploaded = await UploadSvg(
            library.Id, "<svg xmlns='http://www.w3.org/2000/svg'><path d='M0 0'/></svg>");

        OrgDesignLibrariesController controller = Controller(User(OrgA));
        IActionResult result = await controller.GetAssetContent(library.Id, uploaded.Asset!.Id, CancellationToken.None);

        FileContentResult content = result.Should().BeOfType<FileContentResult>().Subject;
        content.ContentType.Should().Be("image/svg+xml; charset=utf-8");
        Encoding.UTF8.GetString(content.FileContents).Should().Contain("<svg");
        controller.Response.Headers["Content-Security-Policy"].ToString()
            .Should().Contain("default-src 'none'").And.Contain("sandbox");
        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
    }

    [Fact]
    public async Task The_library_asset_cap_is_24()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");
        for (int i = 0; i < LibraryAssetLimits.MaxAssets; i++)
            _db.DesignSystemLibraryAssets.Add(SeedAssetRow(library, $"font-{i}.woff2"));
        await _db.SaveChangesAsync();

        LibraryAssetUploadResponse uploaded = await UploadSvg(library.Id, "<svg><path d='M0 0'/></svg>");

        uploaded.Status.Should().Be(StatusCodes.Status409Conflict);
        uploaded.Code.Should().Be("LIBRARY_ASSET_LIMIT");
    }

    [Fact]
    public async Task Deleting_an_asset_removes_its_row_and_stored_object()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");
        LibraryAssetUploadResponse uploaded = await UploadSvg(library.Id, "<svg><path d='M0 0'/></svg>");
        _storage.Objects.Should().ContainSingle();

        IActionResult result = await Controller(User(OrgA))
            .DeleteAsset(library.Id, uploaded.Asset!.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _storage.Objects.Should().BeEmpty();
        (await _db.DesignSystemLibraryAssets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_upload_over_the_plan_storage_limit_is_refused_with_the_gate_result()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");
        var gate = new Mock<IPlanGateService>();
        gate.Setup(g => g.CheckUploadAsync(OrgA, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation(
                PlanGate.StorageCode, "storage", "This upload would exceed the plan.", StatusCodes.Status413PayloadTooLarge));

        ActionResult<LibraryAssetUploadResponse> result =
            await UploadResult(library.Id, TinyWoff2, "brand.woff2", "font/woff2", gate.Object);

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        Body(refused.Value)["code"]!.GetValue<string>().Should().Be(PlanGate.StorageCode);
        _storage.Objects.Should().BeEmpty("the object is never written when the quota refuses it");
        (await _db.DesignSystemLibraryAssets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_normal_upload_runs_the_gate_with_the_stored_bytes()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");
        var gate = new Mock<IPlanGateService>();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);

        LibraryAssetUploadResponse uploaded =
            await Upload(library.Id, TinyWoff2, "brand.woff2", "font/woff2", gate: gate.Object);

        uploaded.Status.Should().Be(StatusCodes.Status201Created);
        long stored = _storage.Objects.Should().ContainSingle().Subject.Value.LongLength;
        gate.Verify(
            g => g.CheckUploadAsync(OrgA, stored, It.IsAny<CancellationToken>()),
            Times.Once,
            "the quota is asked about the bytes that will actually be stored");
    }

    [Fact]
    public async Task A_name_with_a_path_segment_is_stored_under_the_basename_as_one_segment()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");

        LibraryAssetUploadResponse uploaded =
            await Upload(library.Id, TinyWoff2, "nested/brand.woff2", "font/woff2");

        uploaded.Status.Should().Be(StatusCodes.Status201Created);
        uploaded.Asset!.Name.Should().Be("brand.woff2");
        string key = _storage.Objects.Should().ContainSingle().Subject.Key;
        key.Should().EndWith("/brand.woff2");
        key.Split('/').Should().HaveCount(6, "the stored name is one key segment");
    }

    [Fact]
    public async Task A_name_that_reduces_to_nothing_is_400()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");

        LibraryAssetUploadResponse uploaded =
            await Upload(library.Id, TinyWoff2, "///", "font/woff2");

        uploaded.Status.Should().Be(StatusCodes.Status400BadRequest);
        uploaded.Code.Should().Be("LIBRARY_ASSET_NAME");
        _storage.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stored_key_outside_the_library_prefix_is_404()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");
        Guid assetId = Guid.NewGuid();
        string key = $"design-libraries/{OrgB}/{library.Id}/assets/{assetId}/logo.svg";
        _storage.Objects[key] = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><path d='M0 0'/></svg>");
        _db.DesignSystemLibraryAssets.Add(new DesignSystemLibraryAsset
        {
            Id = assetId,
            LibraryId = library.Id,
            Name = "logo.svg",
            MimeType = "image/svg+xml",
            SizeBytes = _storage.Objects[key].LongLength,
            Sha256 = new string('a', 64),
            StorageKey = key,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        IActionResult result = await Controller(User(OrgA))
            .GetAssetContent(library.Id, assetId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>(
            "a stored key that is not under this library's prefix is never opened");
    }

    [Fact]
    public async Task Poisoned_stored_svg_bytes_are_re_sanitised_on_read()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");
        Guid assetId = Guid.NewGuid();
        // Written straight into the object store, as a corrupted write would: the upload path is bypassed.
        string key = $"design-libraries/{OrgA}/{library.Id}/assets/{assetId}/logo.svg";
        _storage.Objects[key] = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script><path d='M0 0'/></svg>");
        _db.DesignSystemLibraryAssets.Add(new DesignSystemLibraryAsset
        {
            Id = assetId,
            LibraryId = library.Id,
            Name = "logo.svg",
            MimeType = "image/svg+xml",
            SizeBytes = _storage.Objects[key].LongLength,
            Sha256 = new string('a', 64),
            StorageKey = key,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        IActionResult result = await Controller(User(OrgA))
            .GetAssetContent(library.Id, assetId, CancellationToken.None);

        FileContentResult content = result.Should().BeOfType<FileContentResult>().Subject;
        string served = Encoding.UTF8.GetString(content.FileContents);
        served.Should().Contain("<path").And.NotContain("script",
            "a stored-bytes compromise cannot make the route serve script");
    }

    [Fact]
    public async Task A_library_version_accepts_a_reference_to_its_own_asset_through_the_service()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");
        LibraryAssetUploadResponse uploaded = await UploadSvg(library.Id, "<svg><path d='M0 0'/></svg>");

        DesignSystemWriteResult result = await Service().CreateDraftAsync(
            DesignSystemScope.Library(library.DesignSystemId, OrgA),
            _ownerId,
            DocumentWithAsset(uploaded.Asset!.Id, "acme-logo.svg"),
            DesignVersionOrigin.Manual,
            workflowExecutionId: null,
            coverageJson: null,
            baseVersionId: null,
            CancellationToken.None);

        result.Status.Should().Be(DesignSystemWriteStatus.Ok);
    }

    [Fact]
    public async Task A_project_linked_to_a_library_validates_a_ref_to_that_librarys_asset()
    {
        DesignSystemLibrary library = SeedLibrary(OrgA, "Acme");
        LibraryAssetUploadResponse own = await UploadSvg(library.Id, "<svg><path d='M0 0'/></svg>");
        DesignSystemLibrary otherLibrary = SeedLibrary(OrgA, "Elsewhere");
        LibraryAssetUploadResponse foreign = await UploadSvg(otherLibrary.Id, "<svg><path d='M0 0'/></svg>");

        Project project = SeedProject(OrgA);
        _db.DesignSystems.Add(new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = DesignSystemKinds.Design,
            Name = project.Name,
            ParentLibraryId = library.Id,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        DesignSystemWriteResult ownResult = await Service().CreateDraftAsync(
            project.Id, _ownerId, DesignSystemKinds.Design,
            DocumentWithAsset(own.Asset!.Id, "acme-logo.svg"),
            DesignVersionOrigin.Manual, workflowExecutionId: null, coverageJson: null,
            sources: [], baseVersionId: null, CancellationToken.None);

        DesignSystemWriteResult foreignResult = await Service().CreateDraftAsync(
            project.Id, _ownerId, DesignSystemKinds.Design,
            DocumentWithAsset(foreign.Asset!.Id, "other-logo.svg"),
            DesignVersionOrigin.Manual, workflowExecutionId: null, coverageJson: null,
            sources: [], baseVersionId: null, CancellationToken.None);

        ownResult.Status.Should().Be(DesignSystemWriteStatus.Ok);
        foreignResult.Status.Should().Be(DesignSystemWriteStatus.ValidationFailed);
        foreignResult.Violations.Should().NotBeNull();
        foreignResult.Violations!.Should().Contain(v => v.Code == "DS_V15");
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private OrgDesignLibrariesController Controller(ICurrentUser user, IPlanGateService? planGate = null)
    {
        var controller = new OrgDesignLibrariesController(
            _db,
            user,
            Service(),
            NullLogger<OrgDesignLibrariesController>.Instance,
            configuration: null,
            libraryAssets: _storage,
            planGate: planGate)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    private DesignSystemService Service() =>
        new(_db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(), NullLogger<DesignSystemService>.Instance);

    private DesignValidationContext LibraryContext(Guid libraryId)
    {
        HashSet<Guid> ids = _db.DesignSystemLibraryAssets
            .Where(a => a.LibraryId == libraryId)
            .Select(a => a.Id)
            .ToHashSet();
        return new DesignValidationContext(_ => false, DesignSystemFonts.BundledNames, ids.Contains);
    }

    private static JsonObject DocumentWithAsset(Guid assetId, string name)
    {
        JsonObject doc = DesignDocument.New("Acme");
        doc["assets"] = new JsonObject
        {
            ["logo"] = new JsonObject
            {
                ["name"] = name,
                ["fileRef"] = $"library-asset:{assetId:D}",
            },
        };
        return doc;
    }

    private async Task<LibraryAssetUploadResponse> UploadSvg(
        Guid libraryId, string svg, IPlanGateService? gate = null) =>
        await Upload(libraryId, Encoding.UTF8.GetBytes(svg), "acme-logo.svg", "image/svg+xml", gate: gate);

    private async Task<LibraryAssetUploadResponse> Upload(
        Guid libraryId, byte[] bytes, string fileName, string contentType,
        ICurrentUser? user = null, IPlanGateService? gate = null)
    {
        ActionResult<LibraryAssetUploadResponse> result =
            await UploadResult(libraryId, bytes, fileName, contentType, gate, user);
        return result.Value
            ?? (result.Result as ObjectResult)?.Value as LibraryAssetUploadResponse
            ?? throw new InvalidOperationException($"upload answered {result.Result?.GetType().Name ?? "null"} without a body");
    }

    private async Task<ActionResult<LibraryAssetUploadResponse>> UploadResult(
        Guid libraryId, byte[] bytes, string fileName, string contentType,
        IPlanGateService? gate = null, ICurrentUser? user = null)
    {
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

        return await Controller(user ?? User(OrgA), gate)
            .Upload(libraryId, file, CancellationToken.None);
    }

    private static JsonObject Body(object? value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

    private DesignSystemLibrary SeedLibrary(Guid organizationId, string name)
    {
        DateTime now = DateTime.UtcNow;
        var container = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = null,
            Kind = DesignSystemKinds.Design,
            Name = name,
            CreatedByUserId = _ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var library = new DesignSystemLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name,
            DesignSystemId = container.Id,
            CreatedByUserId = _ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.DesignSystems.Add(container);
        _db.DesignSystemLibraries.Add(library);
        _db.SaveChanges();
        return library;
    }

    private DesignSystemLibraryAsset SeedAssetRow(DesignSystemLibrary library, string name)
    {
        string key = $"design-libraries/{library.OrganizationId}/{library.Id}/assets/{Guid.NewGuid():N}/{name}";
        var asset = new DesignSystemLibraryAsset
        {
            Id = Guid.NewGuid(),
            LibraryId = library.Id,
            Name = name,
            MimeType = "font/woff2",
            SizeBytes = TinyWoff2.Length,
            Sha256 = new string('a', 64),
            StorageKey = key,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
        };
        _storage.Objects[key] = TinyWoff2;
        return asset;
    }

    private Project SeedProject(Guid organizationId)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Project",
            OwnerId = _ownerId,
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private ICurrentUser User(Guid organizationId) =>
        new FakeUser(_ownerId, organizationId, OrganizationRole.Owner);

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private sealed class InMemoryLibraryAssetStorage : ILibraryAssetStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public Task<string> UploadAsync(
            Guid organizationId, Guid libraryId, Guid assetId, Stream content,
            string fileName, string contentType, CancellationToken ct)
        {
            string key = $"design-libraries/{organizationId}/{libraryId}/assets/{assetId}/{fileName}";
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            Objects[key] = buffer.ToArray();
            return Task.FromResult(key);
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct) =>
            Task.FromResult<Stream?>(
                Objects.TryGetValue(storageKey, out byte[]? bytes) ? new MemoryStream(bytes) : null);

        public Task DeleteAsync(string storageKey, CancellationToken ct)
        {
            Objects.Remove(storageKey);
            return Task.CompletedTask;
        }
    }
}
