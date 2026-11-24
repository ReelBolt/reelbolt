using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.DesignSystems.ProductMap;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// P2-11 coverage for <c>POST /import</c> and <see cref="DesignSystemService.ImportAsync"/>: the
/// size gate BEFORE download, the bounded read with a server-side digest, the two kinds, the plan
/// gate and the tenancy answers.
/// </summary>
public sealed class DesignSystemImportTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000021");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();

    public DesignSystemImportTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-system-import-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------------------------------------
    // 1. Design import
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_css_json_md_files_creates_design_draft_with_3_sources()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        byte[] css = Encoding.UTF8.GetBytes(":root { --color-brand-primary: #6d28d9; --radius: 0.625rem; }");
        byte[] json = Encoding.UTF8.GetBytes(
            "{\"color\":{\"brand\":{\"$type\":\"color\",\"$value\":{\"hex\":\"#6d28d9\",\"colorSpace\":\"srgb\"}}}}");
        byte[] md = Encoding.UTF8.GetBytes("---\nname: Test\n---\n\n## Overview\n\nA test design system.\n");

        ProjectFile cssFile = SeedFile(project.Id, "app/globals.css", css);
        ProjectFile jsonFile = SeedFile(project.Id, "tokens.json", json);
        ProjectFile mdFile = SeedFile(project.Id, "DESIGN.md", md);

        (Mock<IFileStorageService> storage, _) = StorageFor(
            (cssFile.StorageKey, css), (jsonFile.StorageKey, json), (mdFile.StorageKey, md));

        DesignSystemImportResult result = await Service(storage.Object).ImportAsync(
            project.Id, _ownerId, ["design"], [cssFile.Id, jsonFile.Id, mdFile.Id], CancellationToken.None);

        result.Status.Should().Be(DesignSystemImportStatus.Ok);
        result.Versions.Should().NotBeNull();
        result.Versions!.Should().HaveCount(1);
        result.Versions[0].Origin.Should().Be("Import");
        result.Versions[0].VersionNumber.Should().Be(1);

        List<DesignSystemSource> rows = _db.DesignSystemSources.ToList();
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.SourceKind == DesignSourceKind.ProjectFile);
        rows.Select(r => r.Importer).Should().BeEquivalentTo(["css-theme", "dtcg", "design-md"]);

        foreach (DesignSystemSource row in rows)
        {
            byte[] expected = row.Path switch
            {
                "app/globals.css" => css,
                "tokens.json" => json,
                _ => md,
            };
            row.ContentSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(expected)));
        }
    }

    [Fact]
    public async Task Import_oversize_file_is_skipped_with_FILE_TOO_LARGE()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        byte[] css = Encoding.UTF8.GetBytes(":root { --color-brand-primary: #6d28d9; }");

        ProjectFile big = SeedFile(project.Id, "huge.css", new byte[16], sizeOverride: 3 * 1024 * 1024);
        ProjectFile valid = SeedFile(project.Id, "app/globals.css", css);

        (Mock<IFileStorageService> storage, _) = StorageFor((valid.StorageKey, css));

        DesignSystemImportResult result = await Service(storage.Object).ImportAsync(
            project.Id, _ownerId, ["design"], [big.Id, valid.Id], CancellationToken.None);

        result.Status.Should().Be(DesignSystemImportStatus.Ok);
        result.Diagnostics.Should().NotBeNull();
        result.Diagnostics!.Should().Contain(d => d.Code == "FILE_TOO_LARGE");
        result.Versions.Should().NotBeNull();
        result.Versions!.Should().HaveCount(1);

        storage.Verify(
            s => s.DownloadAsync(project.Id, big.StorageKey, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Import_route_omits_the_report_because_the_service_returns_no_design_import_result()
    {
        // The project-file import runs the registered readers inside DesignSystemService, which returns
        // only the created versions: it hands back no DesignImportResult to count. A report built from a
        // null design would claim every axis imported 0 with support None, so the route emits none.
        Project project = SeedProject(HomeOrg, _ownerId);
        byte[] css = Encoding.UTF8.GetBytes(":root { --color-brand-primary: #6d28d9; }");
        ProjectFile file = SeedFile(project.Id, "app/globals.css", css);
        (Mock<IFileStorageService> storage, _) = StorageFor((file.StorageKey, css));

        DesignSystemsController controller = Controller(Owner(), storage.Object, Gate());
        ActionResult<ImportResponse> response = await controller.Import(
            project.Id, new ImportRequest([file.Id], ["design"]), CancellationToken.None);

        ImportResponse payload = response.Result.Should().BeAssignableTo<OkObjectResult>().Subject
            .Value.Should().BeOfType<ImportResponse>().Subject;
        payload.Versions.Should().HaveCount(1);
        payload.Report.Should().BeNull();
    }

    [Fact]
    public async Task Import_that_finds_no_tokens_warns_and_still_creates_the_draft_for_review()
    {
        // The audit case: a DESIGN.md with prose only reads as an empty design. It must say so, never
        // report a quiet success.
        Project project = SeedProject(HomeOrg, _ownerId);
        byte[] md = Encoding.UTF8.GetBytes("# Brand\n\nOur colours are #0b0f14 and #ff5d3a, set in Inter.\n");
        ProjectFile mdFile = SeedFile(project.Id, "DESIGN.md", md);
        (Mock<IFileStorageService> storage, _) = StorageFor((mdFile.StorageKey, md));

        DesignSystemImportResult result = await Service(storage.Object).ImportAsync(
            project.Id, _ownerId, ["design"], [mdFile.Id], CancellationToken.None);

        result.Status.Should().Be(DesignSystemImportStatus.Ok);
        result.Versions.Should().HaveCount(1);
        ImportDiagnostic warning = result.Diagnostics!.Should()
            .ContainSingle(d => d.Code == DesignImportGuard.ZeroTokensCode).Subject;
        warning.Severity.Should().Be(ImportDiagnosticSeverity.Warn);
        warning.Message.Should().Contain("no colours");
    }

    [Fact]
    public async Task Import_that_loses_tokens_against_the_approved_design_warns_with_counts()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedApprovedDesign(project.Id, ("color.brand", "#6d28d9"), ("color.bg", "#0b0f14"), ("color.fg", "#ffffff"));

        byte[] css = Encoding.UTF8.GetBytes(":root { --color-brand: #6d28d9; }");
        ProjectFile cssFile = SeedFile(project.Id, "app/globals.css", css);
        (Mock<IFileStorageService> storage, _) = StorageFor((cssFile.StorageKey, css));

        DesignSystemImportResult result = await Service(storage.Object).ImportAsync(
            project.Id, _ownerId, ["design"], [cssFile.Id], CancellationToken.None);

        result.Status.Should().Be(DesignSystemImportStatus.Ok);
        result.Diagnostics!.Should().NotContain(d => d.Code == DesignImportGuard.ZeroTokensCode);
        ImportDiagnostic warning = result.Diagnostics!.Should()
            .ContainSingle(d => d.Code == DesignImportGuard.LosesTokensCode).Subject;
        warning.Message.Should().Contain("2 of the 3").And.Contain("version 1");
        // The approved design is untouched: the import is a Draft awaiting review.
        _db.DesignSystemVersions.Count(v => v.State == DesignVersionState.Approved).Should().Be(1);
    }

    [Fact]
    public async Task Import_that_keeps_every_token_adds_no_warning()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedApprovedDesign(project.Id, ("color.brand", "#6d28d9"));

        byte[] css = Encoding.UTF8.GetBytes(":root { --color-brand: #6d28d9; --color-accent: #ff5d3a; }");
        ProjectFile cssFile = SeedFile(project.Id, "app/globals.css", css);
        (Mock<IFileStorageService> storage, _) = StorageFor((cssFile.StorageKey, css));

        DesignSystemImportResult result = await Service(storage.Object).ImportAsync(
            project.Id, _ownerId, ["design"], [cssFile.Id], CancellationToken.None);

        result.Diagnostics!.Should().NotContain(d =>
            d.Code == DesignImportGuard.ZeroTokensCode || d.Code == DesignImportGuard.LosesTokensCode);
    }

    [Fact]
    public async Task Import_file_from_another_project_returns_404()
    {
        Project mine = SeedProject(HomeOrg, _ownerId, "Mine");
        Project other = SeedProject(HomeOrg, _ownerId, "Other");
        byte[] css = Encoding.UTF8.GetBytes(":root { --color-brand-primary: #6d28d9; }");
        ProjectFile foreign = SeedFile(other.Id, "app/globals.css", css);

        (Mock<IFileStorageService> storage, _) = StorageFor((foreign.StorageKey, css));

        DesignSystemImportResult result = await Service(storage.Object).ImportAsync(
            mine.Id, _ownerId, ["design"], [foreign.Id], CancellationToken.None);

        result.Status.Should().Be(DesignSystemImportStatus.NotFound);
        storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Import_more_than_50_files_returns_400()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        List<Guid> ids = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToList();

        DesignSystemImportResult result = await Service(Mock.Of<IFileStorageService>()).ImportAsync(
            project.Id, _ownerId, ["design"], ids, CancellationToken.None);

        result.Status.Should().Be(DesignSystemImportStatus.TooManyFiles);
    }

    [Fact]
    public async Task Import_quota_violation_returns_413()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        byte[] css = Encoding.UTF8.GetBytes(":root { --color-brand-primary: #6d28d9; }");
        ProjectFile file = SeedFile(project.Id, "app/globals.css", css);
        (Mock<IFileStorageService> storage, _) = StorageFor((file.StorageKey, css));

        var gate = new Mock<IPlanGateService>();
        gate.Setup(g => g.CheckUploadAsync(project.OrganizationId, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation(PlanGate.StorageCode, "storage", "over quota", 413, 1, 2));

        DesignSystemsController controller = Controller(Owner(), storage.Object, gate);
        ActionResult<ImportResponse> response = await controller.Import(
            project.Id, new ImportRequest([file.Id], ["design"]), CancellationToken.None);

        ObjectResult result = response.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(413);
        Body(result.Value)["code"]!.GetValue<string>().Should().Be("STORAGE_QUOTA_EXCEEDED");
    }

    [Fact]
    public async Task Import_unknown_kind_returns_400()
    {
        Project project = SeedProject(HomeOrg, _ownerId);

        DesignSystemsController controller = Controller(Owner(), Mock.Of<IFileStorageService>(), Gate());
        ActionResult<ImportResponse> response = await controller.Import(
            project.Id, new ImportRequest([Guid.NewGuid()], ["bogus"]), CancellationToken.None);

        BadRequestObjectResult result = response.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        result.StatusCode.Should().Be(400);
        Body(result.Value)["message"]!.GetValue<string>().Should().Be("invalid kind");
    }

    [Fact]
    public async Task Import_product_map_kind_creates_product_map_draft()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        byte[] packageJson = Encoding.UTF8.GetBytes(
            "{\"name\":\"demo\",\"dependencies\":{\"next\":\"15.0.0\",\"react\":\"19.0.0\"}}");
        byte[] page = Encoding.UTF8.GetBytes("export default function Page() { return <div>Hi</div>; }");

        ProjectFile packageFile = SeedFile(project.Id, "package.json", packageJson);
        ProjectFile pageFile = SeedFile(project.Id, "app/page.tsx", page);

        (Mock<IFileStorageService> storage, _) = StorageFor(
            (packageFile.StorageKey, packageJson), (pageFile.StorageKey, page));

        DesignSystemImportResult result = await Service(storage.Object).ImportAsync(
            project.Id, _ownerId, ["product_map"], [packageFile.Id, pageFile.Id], CancellationToken.None);

        result.Status.Should().Be(DesignSystemImportStatus.Ok);
        result.Versions.Should().NotBeNull();
        result.Versions!.Should().HaveCount(1);
        result.Versions[0].Origin.Should().Be("Import");

        DesignSystemVersion version = _db.DesignSystemVersions.Single();
        DesignSystem system = _db.DesignSystems.Single();
        system.Kind.Should().Be(DesignSystemKinds.ProductMap);
        version.Origin.Should().Be(DesignVersionOrigin.Import);

        var expectedFiles = new List<ImportFile>
        {
            new("package.json", packageJson, Convert.ToHexStringLower(SHA256.HashData(packageJson)), packageFile.Id),
            new("app/page.tsx", page, Convert.ToHexStringLower(SHA256.HashData(page)), pageFile.Id),
        };
        ProductMapExtraction expected = ProductMapExtractor.Extract(expectedFiles);
        version.DocumentDigest.Should().Be(CanonicalJson.Sha256Hex(ProductMapJson.Serialize(expected.Map)));
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

    private static Mock<IPlanGateService> Gate()
    {
        var gate = new Mock<IPlanGateService>();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
        return gate;
    }

    private void SeedApprovedDesign(Guid projectId, params (string Path, string Hex)[] colors)
    {
        JsonObject document = DesignDocument.New("Seeded");
        foreach ((string path, string hex) in colors)
        {
            DesignDocument.SetToken(
                document, path, "color",
                new JsonObject { ["colorSpace"] = "srgb", ["hex"] = hex },
                new TokenProvenance("manual", null, null, null), 1.0);
        }

        var versionId = Guid.NewGuid();
        var system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = DesignSystemKinds.Design,
            Name = "design",
            CurrentVersionId = versionId,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.DesignSystems.Add(system);
        _db.DesignSystemVersions.Add(new DesignSystemVersion
        {
            Id = versionId,
            DesignSystemId = system.Id,
            VersionNumber = 1,
            State = DesignVersionState.Approved,
            DocumentJson = document.ToJsonString(),
            DocumentDigest = new string('a', 64),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private DesignSystemService Service(IFileStorageService storage, Mock<IPlanGateService>? gate = null) =>
        new(_db, storage, (gate ?? Gate()).Object, NullLogger<DesignSystemService>.Instance);

    private DesignSystemsController Controller(
        ICurrentUser user, IFileStorageService storage, Mock<IPlanGateService> gate) => new(
        _db,
        user,
        new ProjectAccessService(_db, user),
        Service(storage, gate),
        Mock.Of<IDesignSystemIngestService>(),
        NullLogger<DesignSystemsController>.Instance);

    private static (Mock<IFileStorageService> Storage, Dictionary<string, byte[]> Bytes) StorageFor(
        params (string Key, byte[] Bytes)[] files)
    {
        Dictionary<string, byte[]> bytes = files.ToDictionary(f => f.Key, f => f.Bytes);
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string key, CancellationToken _) => new MemoryStream(bytes[key]));
        return (storage, bytes);
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

    private ProjectFile SeedFile(
        Guid projectId, string path, byte[] bytes, string? sourceRevision = null, long? sizeOverride = null)
    {
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalPath = path,
            OriginalFileName = path,
            StorageKey = $"projects/{projectId}/{path}",
            SizeBytes = sizeOverride ?? bytes.Length,
            SourceRevision = sourceRevision,
            MimeType = "text/plain",
            UploadedAt = DateTime.UtcNow,
        };
        _db.ProjectFiles.Add(file);
        _db.SaveChanges();
        return file;
    }
}
