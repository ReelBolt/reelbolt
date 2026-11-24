using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// P2-17 coverage for <see cref="PinnedDocumentLoader"/>: a row is usable only while its stored
/// digest, its recomputed digest and the 256 KiB canonical cap all still hold. Every failure is a
/// null member and one warning, never a throw.
/// </summary>
public class PinnedDocumentLoaderTests
{
    private static readonly TokenProvenance Prov = new("manual", null, null, null);

    private const string ProductJson =
        """
        {
          "schema": "reelbolt.product-map/1",
          "framework": { "name": "next", "version": "15.1", "language": "typescript", "styling": [], "packageManager": "pnpm", "projectType": "web app" },
          "routes": [{ "path": "/", "file": "app/page.tsx", "kind": "page", "requiresAuth": false }],
          "components": [{ "name": "Button", "path": "components/ui/Button.tsx", "responsibility": "Primary button.", "keyProps": ["variant"], "core": true }],
          "dependencies": { "runtime": [], "dev": [], "ui": [] }
        }
        """;

    [Fact]
    public async Task Loads_both_documents_from_seeded_versions()
    {
        using WorkflowEngineDbContext db = NewDb();
        Mock<ILogger<PinnedDocumentLoader>> logger = Logger();
        Guid designId = Guid.NewGuid();
        Guid productId = Guid.NewGuid();

        JsonObject design = DesignDoc();
        string designJson = CanonicalJson.Serialize(design);
        string designDigest = CanonicalJson.Digest(design);

        string mapJson = CanonicalJson.Serialize(JsonNode.Parse(ProductJson));
        string mapDigest = CanonicalJson.Sha256Hex(mapJson);

        db.DesignSystemVersions.Add(Version(designId, 2, designJson, designDigest));
        db.DesignSystemVersions.Add(Version(productId, 3, mapJson, mapDigest));
        await db.SaveChangesAsync();

        var pin = new DesignSystemPin(
            1,
            new DesignSystemPinMember(designId, 2, designDigest),
            new DesignSystemPinMember(productId, 3, mapDigest));

        LoadedProfile profile = await new PinnedDocumentLoader(db, logger.Object).LoadAsync(pin, CancellationToken.None);

        profile.Design.Should().NotBeNull();
        profile.DesignVersion.Should().Be(2);
        profile.ProductMap.Should().NotBeNull();
        profile.ProductVersion.Should().Be(3);
    }

    [Fact]
    public async Task Digest_mismatch_makes_the_member_unusable()
    {
        using WorkflowEngineDbContext db = NewDb();
        Mock<ILogger<PinnedDocumentLoader>> logger = Logger();
        Guid designId = Guid.NewGuid();
        Guid productId = Guid.NewGuid();

        JsonObject design = DesignDoc();
        string designJson = CanonicalJson.Serialize(design);
        string designDigest = CanonicalJson.Digest(design);

        string mapJson = CanonicalJson.Serialize(JsonNode.Parse(ProductJson));
        string mapDigest = CanonicalJson.Sha256Hex(mapJson);

        db.DesignSystemVersions.Add(Version(designId, 2, designJson, designDigest));
        db.DesignSystemVersions.Add(Version(productId, 3, mapJson, mapDigest));
        await db.SaveChangesAsync();

        var pin = new DesignSystemPin(
            1,
            new DesignSystemPinMember(designId, 2, new string('0', 64)),
            new DesignSystemPinMember(productId, 3, mapDigest));

        LoadedProfile profile = await new PinnedDocumentLoader(db, logger.Object).LoadAsync(pin, CancellationToken.None);

        profile.Design.Should().BeNull();
        profile.ProductMap.Should().NotBeNull("its own digest still matches");
        VerifyUnusable(logger);
    }

    [Fact]
    public async Task Changed_document_json_makes_the_member_unusable()
    {
        using WorkflowEngineDbContext db = NewDb();
        Mock<ILogger<PinnedDocumentLoader>> logger = Logger();
        Guid designId = Guid.NewGuid();

        JsonObject pinned = DesignDoc();
        string pinnedDigest = CanonicalJson.Digest(pinned);

        JsonObject changed = DesignDocument.New("Other");
        DesignDocument.SetToken(changed, "color.brand.primary", "color", new JsonObject { ["hex"] = "#000000" }, Prov, 0.9);
        string changedJson = CanonicalJson.Serialize(changed);

        // The stored digest still equals the pin, but the document no longer recomputes to it.
        db.DesignSystemVersions.Add(Version(designId, 2, changedJson, pinnedDigest));
        await db.SaveChangesAsync();

        var pin = new DesignSystemPin(1, new DesignSystemPinMember(designId, 2, pinnedDigest), null);

        LoadedProfile profile = await new PinnedDocumentLoader(db, logger.Object).LoadAsync(pin, CancellationToken.None);

        profile.Design.Should().BeNull();
        VerifyUnusable(logger);
    }

    [Fact]
    public async Task Missing_version_is_null_not_throw()
    {
        using WorkflowEngineDbContext db = NewDb();
        Mock<ILogger<PinnedDocumentLoader>> logger = Logger();

        var pin = new DesignSystemPin(
            1,
            new DesignSystemPinMember(Guid.NewGuid(), 1, CanonicalJson.Sha256Hex("design")),
            new DesignSystemPinMember(Guid.NewGuid(), 1, CanonicalJson.Sha256Hex("product")));

        LoadedProfile profile = await new PinnedDocumentLoader(db, logger.Object).LoadAsync(pin, CancellationToken.None);

        profile.Design.Should().BeNull();
        profile.ProductMap.Should().BeNull();
        VerifyUnusable(logger);
    }

    [Fact]
    public async Task State_is_not_consulted_digest_is_the_gate()
    {
        using WorkflowEngineDbContext db = NewDb();
        Mock<ILogger<PinnedDocumentLoader>> logger = Logger();
        Guid designId = Guid.NewGuid();

        JsonObject design = DesignDoc();
        string designJson = CanonicalJson.Serialize(design);
        string designDigest = CanonicalJson.Digest(design);

        db.DesignSystemVersions.Add(Version(designId, 4, designJson, designDigest, DesignVersionState.Draft));
        await db.SaveChangesAsync();

        var pin = new DesignSystemPin(1, new DesignSystemPinMember(designId, 4, designDigest), null);

        LoadedProfile profile = await new PinnedDocumentLoader(db, logger.Object).LoadAsync(pin, CancellationToken.None);

        profile.Design.Should().NotBeNull();
        profile.DesignVersion.Should().Be(4);
    }

    [Fact]
    public async Task Over_cap_document_is_unusable()
    {
        using WorkflowEngineDbContext db = NewDb();
        Mock<ILogger<PinnedDocumentLoader>> logger = Logger();
        Guid designId = Guid.NewGuid();

        var oversized = new JsonObject { ["x"] = new string('a', 262145) };
        string json = CanonicalJson.Serialize(oversized);
        string digest = CanonicalJson.Digest(oversized);

        db.DesignSystemVersions.Add(Version(designId, 1, json, digest));
        await db.SaveChangesAsync();

        var pin = new DesignSystemPin(1, new DesignSystemPinMember(designId, 1, digest), null);

        LoadedProfile profile = await new PinnedDocumentLoader(db, logger.Object).LoadAsync(pin, CancellationToken.None);

        profile.Design.Should().BeNull();
        VerifyUnusable(logger);
    }

    [Fact]
    public async Task Garbage_json_is_null_not_throw()
    {
        using WorkflowEngineDbContext db = NewDb();
        Mock<ILogger<PinnedDocumentLoader>> logger = Logger();
        Guid designId = Guid.NewGuid();
        string digest = CanonicalJson.Sha256Hex("not json");

        db.DesignSystemVersions.Add(Version(designId, 1, "not json", digest));
        await db.SaveChangesAsync();

        var pin = new DesignSystemPin(1, new DesignSystemPinMember(designId, 1, digest), null);

        LoadedProfile profile = await new PinnedDocumentLoader(db, logger.Object).LoadAsync(pin, CancellationToken.None);

        profile.Design.Should().BeNull();
        VerifyUnusable(logger);
    }

    private static WorkflowEngineDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Mock<ILogger<PinnedDocumentLoader>> Logger() => new();

    private static void VerifyUnusable(Mock<ILogger<PinnedDocumentLoader>> logger) =>
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("design_system.pin_unusable", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);

    private static DesignSystemVersion Version(
        Guid id,
        int number,
        string json,
        string digest,
        DesignVersionState state = DesignVersionState.Approved) => new()
        {
            Id = id,
            DesignSystemId = Guid.NewGuid(),
            VersionNumber = number,
            State = state,
            DocumentJson = json,
            DocumentDigest = digest,
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
        };

    private static JsonObject DesignDoc()
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#6d28d9" }, Prov, 0.95);
        DesignDocument.SetToken(doc, "typography.family.heading", "fontFamily", new JsonArray("Inter"), Prov, 1.0);
        return doc;
    }
}
