using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.DesignSystems.ProductMap;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// P2-11 coverage for <c>POST /ingest</c> and <see cref="DesignSystemIngestService"/>: the
/// execution-belongs-to-project check, the key prefix, the size cap BEFORE the read, both
/// validators before any write, the origin scan, sources and idempotency.
/// </summary>
public sealed class DesignSystemIngestTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000022");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();

    public DesignSystemIngestTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-system-ingest-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------------------------------------
    // 8. The happy path
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ingest_happy_path_creates_static_extract_drafts()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId);

        string key = KeyFor(project.Id, executionId, 1);
        string artifact = Artifact(
            ValidDesign(),
            JsonNode.Parse(ValidProductMapJson()),
            new JsonArray
            {
                new JsonObject
                {
                    ["path"] = "app/globals.css",
                    ["importer"] = "css-theme",
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("css"))),
                    ["sizeBytes"] = 1234,
                    ["projectFileId"] = null,
                    ["sourceRevision"] = "rev-1",
                },
            },
            new JsonObject { ["tokens"] = 3 });

        Mock<IFileStorageService> storage = StorageFor(project.Id, key, artifact);

        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.Ok);
        result.VersionIds.Should().NotBeNull();
        result.VersionIds!.Should().HaveCount(2);

        DesignSystem designSystem = _db.DesignSystems.Single(s => s.Kind == DesignSystemKinds.Design);
        DesignSystemVersion designVersion = _db.DesignSystemVersions.Single(v => v.DesignSystemId == designSystem.Id);
        designVersion.Origin.Should().Be(DesignVersionOrigin.StaticExtract);
        designVersion.WorkflowExecutionId.Should().Be(executionId);

        DesignSystem productSystem = _db.DesignSystems.Single(s => s.Kind == DesignSystemKinds.ProductMap);
        DesignSystemVersion productVersion = _db.DesignSystemVersions.Single(v => v.DesignSystemId == productSystem.Id);
        productVersion.Origin.Should().Be(DesignVersionOrigin.StaticExtract);
        productVersion.WorkflowExecutionId.Should().Be(executionId);

        _db.DesignSystemSources.Should().ContainSingle();
        DesignSystemSource source = _db.DesignSystemSources.Single();
        source.Path.Should().Be("app/globals.css");
        source.Importer.Should().Be("css-theme");
        source.SourceKind.Should().Be(DesignSourceKind.ProjectFile);
        source.SourceRevision.Should().Be("rev-1");
    }

    [Fact]
    public async Task Ingest_keeps_locked_tokens_of_the_current_version_and_the_draft_is_approvable()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId);

        var system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = DesignSystemKinds.Design,
            Name = "Design system",
            CreatedByUserId = _ownerId,
        };
        _db.DesignSystems.Add(system);
        _db.SaveChanges();

        JsonObject lockedDoc = ValidDesign();
        lockedDoc["color"]!["primary"]!["$value"]!["hex"] = "#111111";
        lockedDoc["color"]!["primary"]!["$extensions"] = new JsonObject
        {
            ["com.reelbolt"] = new JsonObject
            {
                ["prov"] = new JsonObject { ["method"] = "manual" },
                ["conf"] = 1.0,
                ["locked"] = true,
            },
        };
        var current = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            VersionNumber = 1,
            State = DesignVersionState.Approved,
            DocumentJson = CanonicalJson.Serialize(lockedDoc),
            DocumentDigest = CanonicalJson.Digest(lockedDoc),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = _ownerId,
            ApprovedByUserId = _ownerId,
            ApprovedAt = DateTime.UtcNow,
        };
        _db.DesignSystemVersions.Add(current);
        _db.SaveChanges();
        system.CurrentVersionId = current.Id;
        _db.SaveChanges();

        JsonObject rebuilt = ValidDesign();
        rebuilt["color"]!["primary"]!["$value"]!["hex"] = "#ff0000";
        rebuilt["color"]!["secondary"] = new JsonObject
        {
            ["$type"] = "color",
            ["$value"] = new JsonObject { ["hex"] = "#00ff00", ["colorSpace"] = "srgb" },
        };
        string key = KeyFor(project.Id, executionId, 1);
        Mock<IFileStorageService> storage = StorageFor(
            project.Id, key, Artifact(rebuilt, null, new JsonArray(), null));

        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.Ok);
        DesignSystemVersion draft = _db.DesignSystemVersions.Single(v => v.WorkflowExecutionId == executionId);
        draft.BaseVersionId.Should().Be(current.Id);
        JsonObject draftDoc = JsonNode.Parse(draft.DocumentJson)!.AsObject();
        DesignDocument.FindToken(draftDoc, "color.primary")!["$value"]!["hex"]!.GetValue<string>()
            .Should().Be("#111111", "a locked token survives a paid rebuild");
        DesignDocument.FindToken(draftDoc, "color.secondary")!["$value"]!["hex"]!.GetValue<string>()
            .Should().Be("#00ff00");

        DesignSystemWriteResult approved = await Service(storage)
            .ApproveAsync(project.Id, draft.Id, _ownerId, CancellationToken.None);
        approved.Status.Should().Be(DesignSystemWriteStatus.Ok);
    }

    [Fact]
    public async Task Ingest_accepts_and_approves_a_draft_built_from_a_staging_shaped_style_with_dropped_tokens()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId);

        var diagnostics = new System.Collections.Generic.List<ImportDiagnostic>();
        JsonObject design = LegacyAnalyzerMapper.DesignFrom(
            ReelBolt.WorkflowEngine.Tests.DesignSystems.Compose.StagingLikeStyleFixture.Build(), diagnostics)!;
        DesignTokenSanitizer.Sanitize(
            design, new DesignValidationContext(_ => true, new System.Collections.Generic.HashSet<string>()), diagnostics);
        diagnostics.Should().NotBeEmpty("the unparseable colours were reported, not silently lost");

        string key = KeyFor(project.Id, executionId, 1);
        JsonObject root = JsonNode.Parse(Artifact(design, null, new JsonArray(), null))!.AsObject();
        root["diagnostics"] = new JsonArray(diagnostics
            .Select(d => (JsonNode)new JsonObject
            {
                ["severity"] = d.Severity.ToString(),
                ["code"] = d.Code,
                ["path"] = d.Path,
                ["message"] = d.Message,
            })
            .ToArray());
        Mock<IFileStorageService> storage = StorageFor(project.Id, key, root.ToJsonString());

        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.Ok);
        DesignSystemVersion draft = _db.DesignSystemVersions.Single(v => v.WorkflowExecutionId == executionId);
        JsonObject draftDoc = JsonNode.Parse(draft.DocumentJson)!.AsObject();
        DesignDocument.FindToken(draftDoc, "color.extra.violet")!["$value"]!["hex"]!.GetValue<string>().Should().Be("#7c3aed");
        DesignDocument.FindToken(draftDoc, "color.extra.gradient-hero").Should().BeNull();

        DesignSystemWriteResult approved = await Service(storage)
            .ApproveAsync(project.Id, draft.Id, _ownerId, CancellationToken.None);
        approved.Status.Should().Be(DesignSystemWriteStatus.Ok);
    }

    // ---------------------------------------------------------------------------------------------------
    // 9. The key prefix
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_artifact_key_is_built_by_the_server_under_the_execution_prefix()
    {
        Guid projectId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        string key = DesignSystemIngestService.BuildArtifactKey(projectId, executionId, 7);

        key.Should().Be($"projects/{projectId}/agentFiles/design-system/{executionId}/step-7-profile.json");
        DesignSystemIngestService.HasValidPrefix(key, projectId, executionId).Should().BeTrue();
        DesignSystemIngestService.HasValidPrefix(key, Guid.NewGuid(), executionId).Should().BeFalse();
        DesignSystemIngestService.HasValidPrefix(key, projectId, Guid.NewGuid()).Should().BeFalse();
    }

    [Theory]
    [InlineData("../other/step-1-profile.json")]
    [InlineData("a/../../../x.json")]
    [InlineData("..\\x.json")]
    public void A_key_that_climbs_out_of_the_execution_prefix_is_not_valid(string tail)
    {
        Guid projectId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        DesignSystemIngestService.HasValidPrefix(
                $"projects/{projectId}/agentFiles/design-system/{executionId}/{tail}", projectId, executionId)
            .Should().BeFalse();
    }

    [Fact]
    public async Task Ingest_for_a_foreign_execution_returns_not_found_and_never_reads_storage()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        var storage = new Mock<IFileStorageService>();

        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.NotFound);
        storage.Verify(
            s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Ingest_of_a_step_that_is_not_the_compose_step_is_refused_without_reading_storage()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId, composeStepOrder: 3);
        var storage = new Mock<IFileStorageService>();

        foreach (int wrong in new[] { 1, 4, 99 })
        {
            DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
                project.Id, _ownerId, executionId, wrong, CancellationToken.None);
            result.Status.Should().Be(DesignSystemIngestStatus.InvalidArtifactKey);
        }

        storage.Verify(
            s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Ingest_with_the_step_order_omitted_resolves_the_compose_step()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId, composeStepOrder: 9);
        Mock<IFileStorageService> storage = StorageFor(
            project.Id, KeyFor(project.Id, executionId, 9),
            Artifact(ValidDesign(), null, new JsonArray(), null));

        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            project.Id, _ownerId, executionId, 0, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.Ok);
        _db.DesignSystemVersions.Single().WorkflowExecutionId.Should().Be(executionId);
    }

    [Fact]
    public async Task Ingest_with_a_source_naming_another_projects_file_is_refused_and_stores_nothing()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId);
        Mock<IFileStorageService> storage = StorageFor(
            project.Id, KeyFor(project.Id, executionId, 1),
            Artifact(ValidDesign(), null, new JsonArray { SourceNode(projectFileId: Guid.NewGuid().ToString(), sizeBytes: 10) }, null));

        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.InvalidDocument);
        _db.DesignSystemVersions.Should().BeEmpty();
        _db.DesignSystemSources.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"big\"")]
    [InlineData("1.5e999")]
    [InlineData("-5")]
    public async Task Ingest_with_a_malformed_size_is_a_400_not_an_exception(string sizeLiteral)
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId);
        JsonObject source = SourceNode(projectFileId: null, sizeBytes: 0);
        source["sizeBytes"] = JsonNode.Parse(sizeLiteral);
        Mock<IFileStorageService> storage = StorageFor(
            project.Id, KeyFor(project.Id, executionId, 1),
            Artifact(ValidDesign(), null, new JsonArray { source }, null));

        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.InvalidDocument);
        _db.DesignSystemVersions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // 10. Tenancy
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ingest_execution_from_another_project_returns_404()
    {
        Project mine = SeedProject(HomeOrg, _ownerId, "Mine");
        Project other = SeedProject(HomeOrg, _ownerId, "Other");
        Guid executionId = Guid.NewGuid();
        SeedExecution(executionId, other.Id, HomeOrg, _ownerId);

        var storage = new Mock<IFileStorageService>();
        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            mine.Id, _ownerId, executionId, 1, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.NotFound);
        storage.Verify(
            s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---------------------------------------------------------------------------------------------------
    // 11. The size cap
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ingest_artifact_over_2MiB_returns_413_without_reading()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId);

        string key = KeyFor(project.Id, executionId, 1);
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.GetMetadataAsync(project.Id, key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(2 * 1024 * 1024 + 1, null));

        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.ArtifactTooLarge);
        storage.Verify(
            s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---------------------------------------------------------------------------------------------------
    // 12. Idempotency
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ingest_idempotent_second_call_returns_same_ids()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId);

        string key = KeyFor(project.Id, executionId, 1);
        string artifact = Artifact(
            ValidDesign(),
            JsonNode.Parse(ValidProductMapJson()),
            new JsonArray(),
            new JsonObject { ["tokens"] = 3 });

        Mock<IFileStorageService> storage = StorageFor(project.Id, key, artifact);
        DesignSystemIngestService service = Ingest(storage);

        DesignSystemIngestResult first = await service.IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);
        int rowsAfterFirst = _db.DesignSystemVersions.Count();

        DesignSystemIngestResult second = await service.IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);

        first.Status.Should().Be(DesignSystemIngestStatus.Ok);
        second.Status.Should().Be(DesignSystemIngestStatus.Ok);
        second.VersionIds.Should().BeEquivalentTo(first.VersionIds);
        _db.DesignSystemVersions.Count().Should().Be(rowsAfterFirst);
    }

    // ---------------------------------------------------------------------------------------------------
    // 13. Pre-validation before any write
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ingest_invalid_document_returns_400_and_stores_nothing()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId);

        string key = KeyFor(project.Id, executionId, 1);
        var invalidDesign = new JsonObject
        {
            ["schema"] = DesignDocument.Schema,
            ["not-a-known-group"] = true,
        };
        string artifact = Artifact(invalidDesign, null, new JsonArray(), null);

        Mock<IFileStorageService> storage = StorageFor(project.Id, key, artifact);
        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.InvalidDocument);
        _db.DesignSystemVersions.Should().BeEmpty();
        _db.DesignSystemSources.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // 14. Missing artifact
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ingest_artifact_not_found_returns_404()
    {
        Guid executionId = Guid.NewGuid();
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedExecution(executionId, project.Id, HomeOrg, _ownerId);

        string key = KeyFor(project.Id, executionId, 1);
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.GetMetadataAsync(project.Id, key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((StoredObjectMetadata?)null);

        DesignSystemIngestResult result = await Ingest(storage).IngestAsync(
            project.Id, _ownerId, executionId, 1, CancellationToken.None);

        result.Status.Should().Be(DesignSystemIngestStatus.ArtifactNotFound);
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private static Mock<IPlanGateService> Gate()
    {
        var gate = new Mock<IPlanGateService>();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
        return gate;
    }

    private DesignSystemService Service(Mock<IFileStorageService> storage) =>
        new(_db, storage.Object, Gate().Object, NullLogger<DesignSystemService>.Instance);

    private DesignSystemIngestService Ingest(Mock<IFileStorageService> storage) =>
        new(_db, Service(storage), storage.Object, NullLogger<DesignSystemIngestService>.Instance);

    private static string KeyFor(Guid projectId, Guid executionId, int stepOrder) =>
        $"projects/{projectId}/agentFiles/design-system/{executionId}/step-{stepOrder}-profile.json";

    private static Mock<IFileStorageService> StorageFor(Guid projectId, string key, string artifact)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(artifact);
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.GetMetadataAsync(projectId, key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(bytes.Length, "application/json"));
        storage.Setup(s => s.DownloadAsync(projectId, key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(bytes));
        return storage;
    }

    private static JsonObject SourceNode(string? projectFileId, long sizeBytes) => new()
    {
        ["path"] = "app/globals.css",
        ["importer"] = "css-theme",
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("css"))),
        ["sizeBytes"] = sizeBytes,
        ["projectFileId"] = projectFileId,
    };

    private static string Artifact(
        JsonNode? design, JsonNode? productMap, JsonArray sources, JsonObject? coverage)
    {
        var root = new JsonObject
        {
            ["schema"] = "reelbolt.design-system-build/1",
            ["design"] = design,
            ["productMap"] = productMap,
            ["sources"] = sources,
            ["coverage"] = coverage,
            ["diagnostics"] = new JsonArray(),
        };

        return root.ToJsonString();
    }

    private static JsonObject ValidDesign() => new()
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

    private static string ValidProductMapJson()
    {
        byte[] packageJson = Encoding.UTF8.GetBytes(
            "{\"name\":\"demo\",\"dependencies\":{\"next\":\"15.0.0\",\"react\":\"19.0.0\"}}");
        byte[] page = Encoding.UTF8.GetBytes("export default function Page() { return <div>Hi</div>; }");

        var files = new List<ImportFile>
        {
            new("package.json", packageJson, Convert.ToHexStringLower(SHA256.HashData(packageJson)), null),
            new("app/page.tsx", page, Convert.ToHexStringLower(SHA256.HashData(page)), null),
        };

        return ProductMapJson.Serialize(ProductMapExtractor.Extract(files).Map);
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

    private void SeedExecution(
        Guid executionId, Guid projectId, Guid organizationId, Guid userId, int composeStepOrder = 1)
    {
        Guid definitionId = Guid.NewGuid();
        _db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definitionId,
            StepOrder = composeStepOrder,
            StepType = ReelBolt.Shared.Data.Models.StepType.DesignSystem,
            DesignSystemConfigJson = "{\"op\":\"compose\",\"version\":1,\"compose\":{\"parseStepOrder\":" + (composeStepOrder - 1) + "}}",
        });
        _db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definitionId,
            StepOrder = composeStepOrder + 1,
            StepType = ReelBolt.Shared.Data.Models.StepType.DesignSystem,
            DesignSystemConfigJson = "{\"op\":\"parse\",\"version\":1}",
        });
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = definitionId,
            ProjectId = projectId,
            OrganizationId = organizationId,
            InitiatedByUserId = userId,
        });
        _db.SaveChanges();
    }
}
