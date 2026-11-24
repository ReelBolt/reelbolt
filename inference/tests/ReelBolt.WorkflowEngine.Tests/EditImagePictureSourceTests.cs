#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Editor remediation W0.8 (top-10 #10, server half): a new picture opens at its photo's own size, a deleted
/// source photo leaves every picture that used it with its own copy (decision D10), a layer whose object is
/// gone is a 404 rather than a 500, and an empty upload is a 400 rather than a 413.
/// </summary>
public partial class EditImageDocumentControllersTests
{
    // =============================================================================================
    // T1: the canvas is the photo's own size
    // =============================================================================================

    [Fact]
    public async Task Create_from_a_portrait_phone_photo_opens_at_the_photos_own_size()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "portrait.png", "image/png", PngOfSize(1080, 1920));

        EditImageDocumentDto doc = await h.CreateDocumentAsync(projectId, fileId);

        doc.CanvasWidth.Should().Be(1080);
        doc.CanvasHeight.Should().Be(1920);
        EditImageLayerDto source = doc.Layers.Should().ContainSingle().Subject;
        source.Width.Should().Be(1080);
        source.Height.Should().Be(1920);
        source.Transform.Scale.Should().Be(1);
        source.Transform.X.Should().Be(0);
        source.Transform.Y.Should().Be(0);
    }

    [Fact]
    public async Task Create_from_a_very_large_photo_caps_the_long_side_at_4096_and_scales_the_source_to_fit()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "big.png", "image/png", PngOfSize(8000, 6000));

        EditImageDocumentDto doc = await h.CreateDocumentAsync(projectId, fileId);

        doc.CanvasWidth.Should().Be(4096);
        doc.CanvasHeight.Should().Be(3072);
        EditImageLayerDto source = doc.Layers.Should().ContainSingle().Subject;
        source.Width.Should().Be(8000, "the layer records the photo's own pixels");
        source.Height.Should().Be(6000);
        source.Transform.Scale.Should().BeApproximately(4096.0 / 8000, 1e-9);
        (source.Width!.Value * source.Transform.Scale).Should().BeApproximately(4096, 1e-6);
        (source.Height!.Value * source.Transform.Scale).Should().BeApproximately(3072, 1e-6);
    }

    [Theory]
    [InlineData(10, 10, 16, 16)]
    [InlineData(10000, 30, 4096, 16)]
    [InlineData(30, 10000, 16, 4096)]
    public async Task Create_from_a_tiny_or_extreme_aspect_photo_opens_at_a_canvas_a_later_patch_accepts(
        int photoWidth, int photoHeight, int canvasWidth, int canvasHeight)
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "odd.png", "image/png", PngOfSize(photoWidth, photoHeight));

        EditImageDocumentDto doc = await h.CreateDocumentAsync(projectId, fileId);

        doc.CanvasWidth.Should().Be(canvasWidth);
        doc.CanvasHeight.Should().Be(canvasHeight);
        doc.CanvasWidth!.Value.Should().BeGreaterThanOrEqualTo(EditImageDocumentSupport.MinCanvasSide);
        doc.CanvasHeight!.Value.Should().BeGreaterThanOrEqualTo(EditImageDocumentSupport.MinCanvasSide);
        EditImageLayerDto source = doc.Layers.Should().ContainSingle().Subject;
        source.Width.Should().Be(photoWidth, "the layer records the photo's own pixels");
        source.Height.Should().Be(photoHeight);
        (photoWidth * source.Transform.Scale).Should().BeGreaterThanOrEqualTo(canvasWidth - 1e-6, "the photo covers the canvas");
        (photoHeight * source.Transform.Scale).Should().BeGreaterThanOrEqualTo(canvasHeight - 1e-6);
    }

    [Fact]
    public async Task Create_reads_a_gif_header_too()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "anim.gif", "image/gif", GifOfSize(640, 480));

        EditImageDocumentDto doc = await h.CreateDocumentAsync(projectId, fileId);

        doc.CanvasWidth.Should().Be(640);
        doc.CanvasHeight.Should().Be(480);
    }

    [Fact]
    public async Task Create_falls_back_to_the_clients_measured_size_when_the_header_is_unreadable()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        // A PNG signature and no IHDR: nothing for the server to measure.
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "photo.png", "image/png", EditImageDocumentControllersTests.UnreadablePng);

        (int status, JsonElement body) = Harness.Read(await h.Documents().Create(
            projectId, new CreateEditImageDocumentRequest(fileId, Width: 1200, Height: 800), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("canvasWidth").GetInt32().Should().Be(1200);
        body.GetProperty("canvasHeight").GetInt32().Should().Be(800);
    }

    [Fact]
    public async Task Create_prefers_the_measured_header_over_the_clients_size()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "portrait.png", "image/png", PngOfSize(1080, 1920));

        (int status, JsonElement body) = Harness.Read(await h.Documents().Create(
            projectId, new CreateEditImageDocumentRequest(fileId, Width: 1280, Height: 720), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("canvasWidth").GetInt32().Should().Be(1080);
        body.GetProperty("canvasHeight").GetInt32().Should().Be(1920);
    }

    [Theory]
    [InlineData(1200, null)]
    [InlineData(null, 800)]
    [InlineData(0, 800)]
    [InlineData(1200, -1)]
    public async Task Create_refuses_a_client_size_that_is_not_a_pair_of_positive_numbers(int? width, int? height)
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedImageFileAsync(projectId);

        (int status, JsonElement body) = Harness.Read(await h.Documents().Create(
            projectId, new CreateEditImageDocumentRequest(fileId, Width: width, Height: height), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("message").GetString().Should().Contain("width and height");
    }

    [Fact]
    public void ImageHeader_reads_a_gif_logical_screen_size()
    {
        ImageHeader.TryReadDisplaySize(GifOfSize(321, 123), out int w, out int hgt).Should().BeTrue();
        w.Should().Be(321);
        hgt.Should().Be(123);
    }

    // =============================================================================================
    // T4 / D10: copy-on-delete
    // =============================================================================================

    [Fact]
    public async Task PictureUsage_counts_the_pictures_that_use_the_photo()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid used = await h.SeedImageFileAsync(projectId, "used.png");
        Guid unused = await h.SeedImageFileAsync(projectId, "unused.png");
        await h.CreateDocumentAsync(projectId, used);
        EditImageDocumentDto second = await h.CreateDocumentAsync(projectId, used);
        // a second layer of the same photo in one picture is still one picture
        await h.Documents().CreateImageLayerFromFile(projectId, second.Id, new CreateImageLayerFromFileRequest(used), CancellationToken.None);

        (int status, JsonElement body) = Harness.Read(await h.Files().GetPictureUsage(projectId, used, CancellationToken.None));
        status.Should().Be(200);
        body.GetProperty("pictures").GetInt32().Should().Be(2);

        (int unusedStatus, JsonElement unusedBody) = Harness.Read(await h.Files().GetPictureUsage(projectId, unused, CancellationToken.None));
        unusedStatus.Should().Be(200);
        unusedBody.GetProperty("pictures").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task PictureUsage_is_404_for_a_file_of_another_project_and_for_a_stranger()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid otherProject = await h.SeedProjectAsync();
        Guid foreignFile = await h.SeedImageFileAsync(otherProject, "theirs.png");
        Guid file = await h.SeedImageFileAsync(projectId, "mine.png");

        Harness.Read(await h.Files().GetPictureUsage(projectId, foreignFile, CancellationToken.None)).Status.Should().Be(404);
        Harness.Read(await h.Files().GetPictureUsage(projectId, Guid.NewGuid(), CancellationToken.None)).Status.Should().Be(404);
        Harness.Read(await h.Files(Guid.NewGuid()).GetPictureUsage(projectId, file, CancellationToken.None)).Status.Should().Be(404);
    }

    [Fact]
    public async Task Deleting_a_source_photo_gives_every_picture_its_own_copy_and_repoints_every_layer()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        byte[] photo = PngOfSize(1080, 1920);
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "portrait.png", "image/png", photo);
        string originalKey = await h.StorageKeyOfAsync(fileId);
        EditImageDocumentDto first = await h.CreateDocumentAsync(projectId, fileId);
        EditImageDocumentDto second = await h.CreateDocumentAsync(projectId, fileId);
        (int placedStatus, JsonElement placed) = Harness.Read(await h.Documents().CreateImageLayerFromFile(
            projectId, second.Id, new CreateImageLayerFromFileRequest(fileId), CancellationToken.None));
        placedStatus.Should().Be(200);

        (int status, _) = Harness.Read(await h.Files().Delete(projectId, fileId, CancellationToken.None));

        status.Should().Be(204);
        h.Storage.Objects.Should().NotContainKey(originalKey, "the original is still deleted");
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectFiles.AnyAsync(f => f.Id == fileId)).Should().BeFalse();

        List<EditImageDocument> docs = await db.EditImageDocuments.Include(d => d.Layers)
            .Where(d => d.ProjectId == projectId).ToListAsync();
        docs.Should().HaveCount(2, "a picture is no longer deleted with its source photo");
        docs.Should().OnlyContain(d => d.SourceProjectFileId == null);

        foreach (EditImageDocument doc in docs)
        {
            string prefix = $"projects/{projectId}/agentFiles/image-editor/{doc.Id}/";
            List<EditImageLayer> bitmapLayers = doc.Layers.Where(l => l.Kind != EditImageLayerKind.Text).ToList();
            bitmapLayers.Should().OnlyContain(l => l.StorageKey.StartsWith(prefix), "every layer that used the photo points at this picture's copy");
            bitmapLayers.Should().OnlyContain(l => l.SourceProjectFileId == null);
            bitmapLayers.Select(l => l.StorageKey).Distinct().Should().ContainSingle("one copy per picture, shared by its layers");
            bitmapLayers.Should().OnlyContain(l => l.CopySizeBytes == photo.Length);
            h.Storage.Objects[bitmapLayers[0].StorageKey].Should().Equal(photo);
        }

        h.Storage.Copies.Should().HaveCount(2).And.OnlyContain(c => c.From == originalKey);

        // the picture still opens and its pixels are still served
        EditImageDocumentDto reopened = await h.CurrentDocumentAsync(projectId, second.Id);
        reopened.Layers.Should().HaveCount(2);
        Guid placedId = placed.GetProperty("id").GetGuid();
        Harness.Read(await h.Documents().GetLayerImage(projectId, second.Id, placedId, CancellationToken.None)).Status.Should().Be(200);
        Harness.Read(await h.Documents().GetLayerImage(projectId, first.Id, first.Layers[0].Id, CancellationToken.None)).Status.Should().Be(200);
    }

    [Fact]
    public async Task Deleting_a_folder_copies_the_photos_its_pictures_use_before_the_objects_go()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "shoot/a.png", "image/png", PngOfSize(400, 300), directory: "shoot");
        string originalKey = await h.StorageKeyOfAsync(fileId);
        EditImageDocumentDto doc = await h.CreateDocumentAsync(projectId, fileId);

        (int status, _) = Harness.Read(await h.Files().DeleteFolder(projectId, new FolderDeleteRequest("shoot", Recursive: true), CancellationToken.None));

        status.Should().Be(200);
        h.Storage.Objects.Should().NotContainKey(originalKey);
        using InferenceApiDbContext db = h.NewDb();
        EditImageLayer layer = await db.EditImageLayers.SingleAsync(l => l.DocumentId == doc.Id);
        layer.StorageKey.Should().StartWith($"projects/{projectId}/agentFiles/image-editor/{doc.Id}/");
        h.Storage.Objects.Should().ContainKey(layer.StorageKey);
    }

    [Fact]
    public async Task Deleting_a_photo_no_picture_uses_copies_nothing()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedImageFileAsync(projectId, "lonely.png");

        Harness.Read(await h.Files().Delete(projectId, fileId, CancellationToken.None)).Status.Should().Be(204);

        h.Storage.Copies.Should().BeEmpty();
    }

    // ---- the copies of a USER-initiated delete must fit the plan's storage (security review, T-050) ----

    [Fact]
    public async Task Deleting_a_photo_whose_copies_do_not_fit_the_quota_is_refused_before_anything_is_copied_or_deleted()
    {
        byte[] photo = PngOfSize(400, 300);
        // three pictures need three copies; deleting the original frees one, so the NET growth is two photos
        QuotaLeftGate gate = new(bytesLeft: (2 * photo.Length) - 1);
        Harness h = new() { PlanGate = gate };
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "shared.png", "image/png", photo);
        string originalKey = await h.StorageKeyOfAsync(fileId);
        for (int i = 0; i < 3; i++) await h.CreateDocumentAsync(projectId, fileId);

        (int status, JsonElement body) = Harness.Read(await h.Files().Delete(projectId, fileId, CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("PICTURE_COPIES_OVER_QUOTA");
        body.GetProperty("pictures").GetInt32().Should().Be(3);
        body.GetProperty("message").GetString().Should().Be(
            "This photo is used by 3 pictures. Keeping their copies needs more storage than your plan has left. "
            + "Delete some pictures or free up space, then try again.");
        string raw = body.GetRawText();
        raw.Should().NotContain(fileId.ToString()).And.NotContain("projects/").And.NotContain(projectId.ToString());
        gate.UploadChecks.Should().Equal(2L * photo.Length);

        h.Storage.Copies.Should().BeEmpty("nothing is copied before the refusal");
        h.Storage.Objects.Should().ContainKey(originalKey, "and nothing is deleted");
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectFiles.AnyAsync(f => f.Id == fileId)).Should().BeTrue();
        (await db.EditImageLayers.Where(l => db.EditImageDocuments.Any(d => d.Id == l.DocumentId && d.ProjectId == projectId))
            .AllAsync(l => l.StorageKey == originalKey && l.SourceProjectFileId == fileId && l.CopySizeBytes == null)).Should().BeTrue();
        (await db.EditImageDocuments.Where(d => d.ProjectId == projectId).AllAsync(d => d.SourceProjectFileId == fileId)).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_photo_whose_copies_fit_the_quota_copies_then_deletes()
    {
        byte[] photo = PngOfSize(400, 300);
        QuotaLeftGate gate = new(bytesLeft: 2 * photo.Length);
        Harness h = new() { PlanGate = gate };
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "shared.png", "image/png", photo);
        string originalKey = await h.StorageKeyOfAsync(fileId);
        for (int i = 0; i < 3; i++) await h.CreateDocumentAsync(projectId, fileId);

        Harness.Read(await h.Files().Delete(projectId, fileId, CancellationToken.None)).Status.Should().Be(204);

        gate.UploadChecks.Should().Equal(2L * photo.Length);
        h.Storage.Copies.Should().HaveCount(3);
        h.Storage.Objects.Should().NotContainKey(originalKey);
    }

    [Fact]
    public async Task Deleting_a_photo_used_by_one_picture_never_asks_the_quota_because_it_frees_as_much_as_it_copies()
    {
        // a full workspace must still be able to delete: one copy replaces the original byte for byte
        QuotaLeftGate gate = new(bytesLeft: 0);
        Harness h = new() { PlanGate = gate };
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "solo.png", "image/png", PngOfSize(400, 300));
        await h.CreateDocumentAsync(projectId, fileId);

        Harness.Read(await h.Files().Delete(projectId, fileId, CancellationToken.None)).Status.Should().Be(204);

        gate.UploadChecks.Should().BeEmpty();
        h.Storage.Copies.Should().ContainSingle();
    }

    [Fact]
    public async Task Deleting_a_folder_whose_copies_do_not_fit_the_quota_is_refused_before_anything_is_copied_or_deleted()
    {
        byte[] photo = PngOfSize(400, 300);
        QuotaLeftGate gate = new(bytesLeft: 0);
        Harness h = new() { PlanGate = gate };
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "shoot/a.png", "image/png", photo, directory: "shoot");
        string originalKey = await h.StorageKeyOfAsync(fileId);
        await h.CreateDocumentAsync(projectId, fileId);
        await h.CreateDocumentAsync(projectId, fileId);

        (int status, JsonElement body) = Harness.Read(await h.Files().DeleteFolder(
            projectId, new FolderDeleteRequest("shoot", Recursive: true), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("PICTURE_COPIES_OVER_QUOTA");
        body.GetProperty("message").GetString().Should().StartWith("Photos in this folder are used by 2 pictures.");
        h.Storage.Copies.Should().BeEmpty();
        h.Storage.Objects.Should().ContainKey(originalKey);
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectFiles.AnyAsync(f => f.Id == fileId)).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_photo_with_billing_off_or_no_gate_is_never_refused()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "shared.png", "image/png", PngOfSize(400, 300));
        for (int i = 0; i < 3; i++) await h.CreateDocumentAsync(projectId, fileId);

        Harness.Read(await h.Files().Delete(projectId, fileId, CancellationToken.None)).Status.Should().Be(204);

        h.Storage.Copies.Should().HaveCount(3);
    }

    [Fact]
    public async Task Moving_a_photo_repoints_the_layers_that_use_it()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "a.png", "image/png", PngOfSize(400, 300));
        EditImageDocumentDto doc = await h.CreateDocumentAsync(projectId, fileId);

        (int status, _) = Harness.Read(await h.Files().Move(projectId, new MoveProjectFilesRequest([fileId], "moved"), CancellationToken.None));

        status.Should().Be(200);
        using InferenceApiDbContext db = h.NewDb();
        string newKey = (await db.ProjectFiles.SingleAsync(f => f.Id == fileId)).StorageKey;
        EditImageLayer layer = await db.EditImageLayers.SingleAsync(l => l.DocumentId == doc.Id);
        layer.StorageKey.Should().Be(newKey);
        Harness.Read(await h.Documents().GetLayerImage(projectId, doc.Id, layer.Id, CancellationToken.None)).Status.Should().Be(200);
    }

    [Fact]
    public async Task A_cut_out_with_no_layer_uses_the_pictures_copy_once_the_source_photo_is_deleted()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "p.png", "image/png", PngOfSize(400, 300));
        EditImageDocumentDto doc = await h.CreateDocumentAsync(projectId, fileId);
        Harness.Read(await h.Files().Delete(projectId, fileId, CancellationToken.None)).Status.Should().Be(204);

        (int status, _) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the person"), CancellationToken.None));

        status.Should().Be(200);
        h.Analysis.Requests.Should().ContainSingle()
            .Which.SourceImageUrl.Should().Contain($"agentFiles/image-editor/{doc.Id}/");
    }

    [Fact]
    public async Task Deleting_the_last_layer_on_a_private_copy_marks_it_for_retention_but_a_shared_copy_survives()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "p.png", "image/png", PngOfSize(400, 300));
        EditImageDocumentDto doc = await h.CreateDocumentAsync(projectId, fileId);
        (_, JsonElement placed) = Harness.Read(await h.Documents().CreateImageLayerFromFile(
            projectId, doc.Id, new CreateImageLayerFromFileRequest(fileId), CancellationToken.None));
        Harness.Read(await h.Files().Delete(projectId, fileId, CancellationToken.None)).Status.Should().Be(204);
        string copyKey;
        using (InferenceApiDbContext db = h.NewDb())
            copyKey = (await db.EditImageLayers.FirstAsync(l => l.DocumentId == doc.Id)).StorageKey;

        Harness.Read(await h.Documents().DeleteLayer(projectId, doc.Id, placed.GetProperty("id").GetGuid(), CancellationToken.None))
            .Status.Should().Be(204);
        using (InferenceApiDbContext db = h.NewDb())
            (await db.StorageRetentionMarks.AnyAsync(m => m.StorageKey == copyKey)).Should().BeFalse("the backdrop still uses the copy");

        Harness.Read(await h.Documents().DeleteLayer(projectId, doc.Id, doc.Layers[0].Id, CancellationToken.None))
            .Status.Should().Be(204);
        using (InferenceApiDbContext db = h.NewDb())
            (await db.StorageRetentionMarks.CountAsync(m => m.StorageKey == copyKey)).Should().Be(1);
    }

    [Fact]
    public async Task A_pictures_private_copy_counts_toward_storage_once_per_picture()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        byte[] photo = PngOfSize(400, 300);
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "p.png", "image/png", photo);
        Guid keep = await h.SeedImageFileAsync(projectId, "keep.png"); // 8 bytes
        EditImageDocumentDto doc = await h.CreateDocumentAsync(projectId, fileId);
        await h.Documents().CreateImageLayerFromFile(projectId, doc.Id, new CreateImageLayerFromFileRequest(fileId), CancellationToken.None);
        await h.CreateDocumentAsync(projectId, fileId);
        Harness.Read(await h.Files().Delete(projectId, fileId, CancellationToken.None)).Status.Should().Be(204);

        using InferenceApiDbContext db = h.NewDb();
        Mock<IOrganizationEntitlements> entitlements = new();
        StorageUsageService usage = new(db, entitlements.Object);
        long used = await usage.GetUsedBytesAsync(TestTenancy.OrgOf(h.OwnerId), CancellationToken.None);

        used.Should().Be(8 + 2L * photo.Length, "the remaining file plus one copy per picture");
        _ = keep;
    }

    // =============================================================================================
    // A missing object is a 404, and an empty upload is a 400
    // =============================================================================================

    [Fact]
    public async Task A_layer_whose_object_is_gone_answers_404_not_500()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layerId = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Cutout, Png, "image/png", 10, 10, matte: Png);
        h.Storage.Objects.Clear();

        Harness.Read(await h.Documents().GetLayerImage(projectId, doc.Id, doc.Layers[0].Id, CancellationToken.None)).Status.Should().Be(404);
        Harness.Read(await h.Documents().GetLayerImage(projectId, doc.Id, layerId, CancellationToken.None)).Status.Should().Be(404);
        Harness.Read(await h.Documents().GetLayerMatte(projectId, doc.Id, layerId, CancellationToken.None)).Status.Should().Be(404);
    }

    [Fact]
    public async Task An_empty_mask_upload_is_a_400_with_a_plain_message()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Assets().CreateUploadUrl(
            projectId, doc.Id, new EditImageUploadUrlRequest("mask", "image/png", 0), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("message").GetString().Should().Be("The mask is empty. Paint the area to change, then try again.");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>The first 33 bytes of a PNG of the given size: signature and IHDR (all the header parser reads).</summary>
    internal static byte[] PngOfSize(int width, int height)
    {
        byte[] b = new byte[33];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        sig.CopyTo(b, 0);
        b[16] = (byte)(width >> 24); b[17] = (byte)(width >> 16); b[18] = (byte)(width >> 8); b[19] = (byte)width;
        b[20] = (byte)(height >> 24); b[21] = (byte)(height >> 16); b[22] = (byte)(height >> 8); b[23] = (byte)height;
        b[24] = 8; b[25] = 6;
        return b;
    }

    /// <summary>A GIF89a header with the given logical screen size.</summary>
    internal static byte[] GifOfSize(int width, int height) =>
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
         (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0, 0];

    private sealed partial class Harness
    {
        public ProjectFilesController Files(Guid? userId = null)
        {
            InferenceApiDbContext db = NewDb();
            return new ProjectFilesController(
                db, User(userId), TestTenancy.Access(db, User(userId)), Storage,
                new ChannelBackgroundTaskQueue<FileSummarizationTask>(16), Mock.Of<IPublishEndpoint>(),
                new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()),
                PlanGate)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public async Task<Guid> SeedFileWithBytesAsync(Guid projectId, string path, string mime, byte[] bytes, string? directory = null)
        {
            using InferenceApiDbContext db = NewDb();
            string name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OriginalFileName = name,
                OriginalPath = path,
                DirectoryPath = directory,
                StorageKey = $"projects/{projectId}/userFiles/{path}",
                StorageBucket = "test-bucket",
                MimeType = mime,
                SizeBytes = bytes.Length
            };
            db.ProjectFiles.Add(file);
            await db.SaveChangesAsync();
            Storage.Objects[file.StorageKey] = bytes;
            return file.Id;
        }

        public async Task<string> StorageKeyOfAsync(Guid fileId)
        {
            using InferenceApiDbContext db = NewDb();
            return (await db.ProjectFiles.SingleAsync(f => f.Id == fileId)).StorageKey;
        }
    }

    /// <summary>
    /// A plan gate with <paramref name="bytesLeft"/> of storage left, recording every growth it is asked about
    /// (the quota arithmetic itself is the real <c>PlanGate.CheckStorage</c>, pinned in PlanEntitlementGateTests).
    /// </summary>
    private sealed class QuotaLeftGate(long bytesLeft) : IPlanGateService
    {
        public List<long> UploadChecks { get; } = [];

        public Task<ReelBolt.Shared.Billing.PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
        {
            UploadChecks.Add(incomingBytes);
            return Task.FromResult(incomingBytes > bytesLeft
                ? new ReelBolt.Shared.Billing.PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413)
                : null);
        }

        public Task<ReelBolt.Shared.Billing.PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct) =>
            Task.FromResult<ReelBolt.Shared.Billing.PlanGateViolation?>(null);

        public Task<ReelBolt.Shared.Billing.PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) =>
            Task.FromResult<ReelBolt.Shared.Billing.PlanGateViolation?>(null);

        public Task<ReelBolt.Shared.Billing.PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
            Task.FromResult<ReelBolt.Shared.Billing.PlanGateViolation?>(null);
    }
}
