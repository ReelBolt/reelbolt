using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public partial class EditImageDocumentControllersTests
{
    [Fact]
    public async Task Duplicate_copies_canvas_layers_and_images_to_new_keys_and_survives_the_original_delete()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, [1, 2, 3], "image/png", 4, 4, matte: [9, 9]);
        await h.Documents().UpdateDocument(projectId, doc.Id,
            new UpdateEditImageDocumentRequest(" Cover ", 1280, 720, "#112233"), CancellationToken.None);

        (int status, JsonElement body) = Harness.Read(await h.Documents().Duplicate(projectId, doc.Id, CancellationToken.None));

        status.Should().Be(201);
        body.GetProperty("name").GetString().Should().Be("Copy of Cover");
        body.GetProperty("canvasWidth").GetInt32().Should().Be(1280);
        body.GetProperty("backgroundColor").GetString().Should().Be("#112233");
        body.GetProperty("revision").GetInt32().Should().Be(1);
        body.GetRawText().Should().NotContain("storageKey").And.NotContain("agentFiles");
        Guid newId = body.GetProperty("id").GetGuid();
        newId.Should().NotBe(doc.Id);

        using (var db = h.NewDb())
        {
            var original = await db.EditImageDocuments.Include(d => d.Layers).SingleAsync(d => d.Id == doc.Id);
            var copy = await db.EditImageDocuments.Include(d => d.Layers).SingleAsync(d => d.Id == newId);
            copy.Layers.Select(l => l.Kind).Should().Equal(original.Layers.OrderBy(l => l.ZOrder).Select(l => l.Kind));
            copy.Layers.Select(l => l.Id).Should().NotIntersectWith(original.Layers.Select(l => l.Id));
            foreach (EditImageLayer l in copy.Layers)
            {
                l.StorageKey.Should().Contain($"/agentFiles/image-editor/{newId}/");
                original.Layers.Select(o => o.StorageKey).Should().NotContain(l.StorageKey);
                h.Storage.Objects.Should().ContainKey(l.StorageKey);
                l.CopySizeBytes.Should().BeGreaterThan(0);
            }

            copy.Layers.Single(l => l.Kind == EditImageLayerKind.Generated).MatteStorageKey.Should().Contain($"/{newId}/");
        }

        Harness.Read(await h.Documents().Delete(projectId, doc.Id, CancellationToken.None)).Status.Should().Be(204);
        (int get, JsonElement after) = Harness.Read(await h.Documents().Get(projectId, newId, CancellationToken.None));
        get.Should().Be(200);
        using var db2 = h.NewDb();
        foreach (EditImageLayer l in (await db2.EditImageDocuments.Include(d => d.Layers).SingleAsync(d => d.Id == newId)).Layers)
            h.Storage.Objects.Should().ContainKey(l.StorageKey);
        after.GetProperty("layers").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Duplicate_commits_the_organization_storage_guard_transaction()
    {
        // Regression: the guard owns the transaction the copy rows are written in; without CommitAsync
        // the dispose rolls them back and the 201 describes a picture that was never stored.
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, [1, 2, 3], "image/png", 4, 4);
        Mock<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> tx = new();
        tx.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);
        tx.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Mock<ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock> gate = new();
        gate.Setup(g => g.AcquireAsync(It.IsAny<ReelBolt.Inference.Api.Data.InferenceApiDbContext>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageGuard(new SemaphoreSlim(0, 1), tx.Object));

        (int status, _) = Harness.Read(await h.Documents(storageLock: gate.Object).Duplicate(projectId, doc.Id, CancellationToken.None));

        status.Should().Be(201);
        gate.Verify(g => g.AcquireAsync(It.IsAny<ReelBolt.Inference.Api.Data.InferenceApiDbContext>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        tx.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Duplicate_of_a_foreign_or_missing_picture_is_404()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        Harness.Read(await h.Documents(Guid.NewGuid()).Duplicate(projectId, doc.Id, CancellationToken.None)).Status.Should().Be(404);
        Harness.Read(await h.Documents().Duplicate(projectId, Guid.NewGuid(), CancellationToken.None)).Status.Should().Be(404);
    }

    [Fact]
    public async Task Duplicate_over_the_storage_quota_is_409_and_copies_nothing()
    {
        Mock<IPlanGateService> gate = new();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413));
        Harness h = new() { PlanGate = gate.Object };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, [1, 2, 3], "image/png", 4, 4);
        int objects = h.Storage.Objects.Count;

        (int status, JsonElement body) = Harness.Read(await h.Documents().Duplicate(projectId, doc.Id, CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("PICTURE_COPIES_OVER_QUOTA");
        h.Storage.Objects.Count.Should().Be(objects);
        using var db = h.NewDb();
        (await db.EditImageDocuments.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Duplicate_of_a_picture_with_a_quarantined_source_is_404_and_copies_nothing(bool csam)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        using (var db = h.NewDb())
        {
            ProjectFile file = await db.ProjectFiles.SingleAsync();
            if (csam) file.CsamStatus = CsamScanStatus.Quarantined; else file.ModerationStatus = FileModerationStatus.Quarantined;
            await db.SaveChangesAsync();
        }

        int objects = h.Storage.Objects.Count;
        Harness.Read(await h.Documents().Duplicate(projectId, doc.Id, CancellationToken.None)).Status.Should().Be(404);
        h.Storage.Objects.Count.Should().Be(objects);
        using var db2 = h.NewDb();
        (await db2.EditImageDocuments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Duplicate_keeps_the_origin_link_so_a_later_quarantine_still_reaches_the_copy()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        (_, JsonElement body) = Harness.Read(await h.Documents().Duplicate(projectId, doc.Id, CancellationToken.None));
        Guid newId = body.GetProperty("id").GetGuid();
        using var db = h.NewDb();
        Guid fileId = (await db.ProjectFiles.SingleAsync()).Id;
        var copy = await db.EditImageDocuments.Include(d => d.Layers).SingleAsync(d => d.Id == newId);
        copy.SourceProjectFileId.Should().Be(fileId);
        copy.Layers.Should().Contain(l => l.SourceProjectFileId == fileId);
    }

    [Fact]
    public async Task Duplicate_counts_the_matte_in_the_copy_size_and_never_copies_a_matte_alone()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layerId = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, [1, 2, 3], "image/png", 4, 4, matte: [9, 9]);
        string imageKey;
        using (var db = h.NewDb())
        {
            imageKey = (await db.EditImageLayers.SingleAsync(l => l.Id == layerId)).StorageKey;
        }

        h.Storage.Objects.Remove(imageKey); // the image is gone, the matte is not
        (int status, JsonElement body) = Harness.Read(await h.Documents().Duplicate(projectId, doc.Id, CancellationToken.None));
        status.Should().Be(201);
        Guid newId = body.GetProperty("id").GetGuid();
        h.Storage.Objects.Keys.Count(k => k.Contains($"/{newId}/")).Should().Be(1, "only the source photo's copy exists; the orphan matte is not copied");

        h.Storage.Objects[imageKey] = [1, 2, 3];
        (_, body) = Harness.Read(await h.Documents().Duplicate(projectId, doc.Id, CancellationToken.None));
        newId = body.GetProperty("id").GetGuid();
        using var db2 = h.NewDb();
        var copy = (await db2.EditImageDocuments.Include(d => d.Layers).SingleAsync(d => d.Id == newId)).Layers.Single(l => l.Kind == EditImageLayerKind.Generated);
        copy.CopySizeBytes.Should().Be(5);
    }
}
