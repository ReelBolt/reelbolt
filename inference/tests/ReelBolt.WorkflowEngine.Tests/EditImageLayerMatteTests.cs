#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using SkiaSharp;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Layer masks (Wave 4): <c>PUT/DELETE .../layers/{id}/matte</c> for every bitmap kind.</summary>
public partial class EditImageDocumentControllersTests
{
    private static byte[] RgbaPng(int w, int h, SKColor color) => RgbaPng(w, h, (_, _) => color);

    private static byte[] RgbaPng(int w, int h, Func<int, int, SKColor> pixel)
    {
        using SKBitmap bmp = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) bmp.SetPixel(x, y, pixel(x, y));
        using SKImage img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static EditImageDocumentsController WithBody(EditImageDocumentsController c, byte[] body)
    {
        c.ControllerContext.HttpContext.Request.Body = new MemoryStream(body);
        return c;
    }

    private async Task<(Harness H, Guid ProjectId, EditImageDocumentDto Doc, Guid LayerId)> MatteSetupAsync(
        EditImageLayerKind kind = EditImageLayerKind.Source, byte[]? matte = null, Harness? harness = null)
    {
        Harness h = harness ?? new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layerId = await h.SeedLayerAsync(projectId, doc.Id, kind, RgbaPng(4, 3, SKColors.Red), "image/png", 4, 3, matte: matte);
        return (h, projectId, doc, layerId);
    }

    private static async Task<(int Status, JsonElement Body)> PutAsync(
        EditImageDocumentsController c, Guid p, Guid d, Guid l, byte[] body, int? baseRevision = null) =>
        Harness.Read(await WithBody(c, body).PutLayerMatte(p, d, l, baseRevision, CancellationToken.None));

    [Theory]
    [InlineData(EditImageLayerKind.Source)]
    [InlineData(EditImageLayerKind.Cutout)]
    [InlineData(EditImageLayerKind.Generated)]
    [InlineData(EditImageLayerKind.Image)]
    public async Task Matte_put_stores_a_new_grayscale_object_for_every_bitmap_kind_and_exposes_hasMatte(EditImageLayerKind kind)
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync(kind);
        // RGBA, fully opaque: luminance is the visible amount (white = visible, black = hidden).
        byte[] png = RgbaPng(4, 3, (x, _) => x < 2 ? SKColors.Black : SKColors.White);

        int before;
        using (InferenceApiDbContext pre = h.NewDb()) before = (await pre.EditImageDocuments.SingleAsync(d => d.Id == doc.Id)).Revision;

        (int status, JsonElement body) = await PutAsync(h.Documents(), p, doc.Id, layer, png);

        status.Should().Be(200);
        body.GetProperty("hasMatte").GetBoolean().Should().BeTrue();
        body.GetProperty("matteUrl").GetString().Should().EndWith("/matte");
        using InferenceApiDbContext db = h.NewDb();
        EditImageLayer row = await db.EditImageLayers.SingleAsync(l => l.Id == layer);
        row.MatteStorageKey.Should().NotBeNullOrEmpty();
        using SKBitmap stored = SKBitmap.Decode(h.Storage.Objects[row.MatteStorageKey!]);
        (stored.Width, stored.Height, stored.ColorType).Should().Be((4, 3, SKColorType.Gray8));
        stored.GetPixel(0, 0).Red.Should().Be(0);
        stored.GetPixel(3, 0).Red.Should().Be(255);
        (await db.EditImageDocuments.SingleAsync(d => d.Id == doc.Id)).Revision.Should().Be(before + 1);
        (await db.StorageRetentionMarks.CountAsync()).Should().Be(0, "no previous matte, nothing to mark");
    }

    [Fact]
    public async Task Matte_put_uses_alpha_when_the_png_has_transparency()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync();
        byte[] png = RgbaPng(4, 3, (x, _) => new SKColor(255, 255, 255, x == 0 ? (byte)0 : (byte)255));
        (await PutAsync(h.Documents(), p, doc.Id, layer, png)).Status.Should().Be(200);
        using InferenceApiDbContext db = h.NewDb();
        string key = (await db.EditImageLayers.SingleAsync(l => l.Id == layer)).MatteStorageKey!;
        using SKBitmap stored = SKBitmap.Decode(h.Storage.Objects[key]);
        stored.GetPixel(0, 1).Red.Should().Be(0);
        stored.GetPixel(1, 1).Red.Should().Be(255);
    }

    [Fact]
    public async Task Matte_put_replaces_a_previous_mask_with_a_new_object_and_marks_the_old_one()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync();
        byte[] png = RgbaPng(4, 3, SKColors.White);
        await PutAsync(h.Documents(), p, doc.Id, layer, png);
        string first;
        using (InferenceApiDbContext db = h.NewDb()) first = (await db.EditImageLayers.SingleAsync(l => l.Id == layer)).MatteStorageKey!;

        (await PutAsync(h.Documents(), p, doc.Id, layer, png)).Status.Should().Be(200);

        using InferenceApiDbContext db2 = h.NewDb();
        string second = (await db2.EditImageLayers.SingleAsync(l => l.Id == layer)).MatteStorageKey!;
        second.Should().NotBe(first);
        h.Storage.Objects.Should().ContainKey(first, "the old object stays until the retention sweep");
        (await db2.StorageRetentionMarks.SingleAsync()).StorageKey.Should().Be(first);
    }

    [Fact]
    public async Task Matte_put_size_mismatch_is_400_with_a_code()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync();
        (int status, JsonElement body) = await PutAsync(h.Documents(), p, doc.Id, layer, RgbaPng(5, 3, SKColors.White));
        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("MATTE_SIZE_MISMATCH");
    }

    [Fact]
    public async Task Matte_put_rejects_a_non_png_and_a_corrupt_png()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync();
        (await PutAsync(h.Documents(), p, doc.Id, layer, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0 })).Body
            .GetProperty("code").GetString().Should().Be("MATTE_NOT_PNG");
        byte[] truncated = PngHeaderOf(4, 3);
        (await PutAsync(h.Documents(), p, doc.Id, layer, truncated)).Body
            .GetProperty("code").GetString().Should().Be("MATTE_NOT_PNG");
    }

    [Fact]
    public async Task Matte_put_refuses_a_decompression_bomb_header_before_decoding()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync();
        (int status, JsonElement body) = await PutAsync(h.Documents(), p, doc.Id, layer, PngHeaderOf(60000, 60000));
        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("MATTE_TOO_LARGE_PIXELS");
    }

    [Fact]
    public async Task Matte_put_over_the_byte_cap_is_413()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync();
        EditImageDocumentsController c = h.Documents(configuration: new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> { ["PictureEditor:MaxMatteBytes"] = "64" }).Build());
        (int status, JsonElement body) = await PutAsync(c, p, doc.Id, layer, RgbaPng(4, 3, (x, y) => new SKColor((byte)(x * 60), (byte)(y * 80), 7, 255)));
        status.Should().Be(413);
        body.GetProperty("code").GetString().Should().Be("MATTE_TOO_LARGE");
    }

    [Fact]
    public async Task Matte_put_over_the_storage_quota_is_refused_and_writes_nothing()
    {
        Mock<IPlanGateService> gate = new();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413));
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync(harness: new Harness { PlanGate = gate.Object });
        int objects = h.Storage.Objects.Count;
        (int status, JsonElement body) = await PutAsync(h.Documents(), p, doc.Id, layer, RgbaPng(4, 3, SKColors.White));
        status.Should().Be(413);
        body.GetProperty("code").GetString().Should().Be("STORAGE_QUOTA_EXCEEDED");
        h.Storage.Objects.Count.Should().Be(objects);
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageLayers.SingleAsync(l => l.Id == layer)).MatteStorageKey.Should().BeNull();
    }

    [Fact]
    public async Task Matte_put_with_a_stale_baseRevision_is_409_and_changes_nothing()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync();
        (int status, _) = await PutAsync(h.Documents(), p, doc.Id, layer, RgbaPng(4, 3, SKColors.White), baseRevision: doc.Revision + 5);
        status.Should().Be(409);
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageLayers.SingleAsync(l => l.Id == layer)).MatteStorageKey.Should().BeNull();
    }

    [Fact]
    public async Task Matte_put_and_delete_are_404_for_a_text_layer_a_foreign_tenant_and_another_project()
    {
        Harness h = new();
        (Guid p, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid text = Harness.Read(await h.Documents().CreateTextLayer(p, doc.Id, new CreateTextLayerRequest(new TextContentDto("hi")), CancellationToken.None))
            .Body.GetProperty("id").GetGuid();
        Guid layer = await h.SeedLayerAsync(p, doc.Id, EditImageLayerKind.Source, RgbaPng(4, 3, SKColors.Red), "image/png", 4, 3);
        byte[] png = RgbaPng(4, 3, SKColors.White);

        (await PutAsync(h.Documents(), p, doc.Id, text, png)).Status.Should().Be(404);
        Harness.Read(await h.Documents().DeleteLayerMatte(p, doc.Id, text, null, CancellationToken.None)).Status.Should().Be(404);
        (await PutAsync(h.Documents(Guid.NewGuid()), p, doc.Id, layer, png)).Status.Should().Be(404);
        Harness.Read(await h.Documents(Guid.NewGuid()).DeleteLayerMatte(p, doc.Id, layer, null, CancellationToken.None)).Status.Should().Be(404);
        Guid other = await h.SeedProjectAsync(h.OwnerId);
        (await PutAsync(h.Documents(), other, doc.Id, layer, png)).Status.Should().Be(404);
    }

    [Fact]
    public async Task Matte_delete_clears_the_key_marks_the_object_and_is_204_when_there_was_none()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync();
        Harness.Read(await h.Documents().DeleteLayerMatte(p, doc.Id, layer, null, CancellationToken.None)).Status.Should().Be(204);
        await PutAsync(h.Documents(), p, doc.Id, layer, RgbaPng(4, 3, SKColors.White));
        string key;
        using (InferenceApiDbContext db = h.NewDb()) key = (await db.EditImageLayers.SingleAsync(l => l.Id == layer)).MatteStorageKey!;

        Harness.Read(await h.Documents().DeleteLayerMatte(p, doc.Id, layer, null, CancellationToken.None)).Status.Should().Be(204);

        using InferenceApiDbContext db2 = h.NewDb();
        (await db2.EditImageLayers.SingleAsync(l => l.Id == layer)).MatteStorageKey.Should().BeNull();
        (await db2.StorageRetentionMarks.SingleAsync()).StorageKey.Should().Be(key);
    }

    private async Task<(Harness H, Guid P, EditImageDocumentDto Doc, Guid A, Guid B, string Key)> SharedMatteAsync()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid a) = await MatteSetupAsync(matte: RgbaPng(4, 3, SKColors.White));
        Guid b = await h.SeedLayerAsync(p, doc.Id, EditImageLayerKind.Source, RgbaPng(4, 3, SKColors.Red), "image/png", 4, 3);
        string key;
        using (InferenceApiDbContext db = h.NewDb())
        {
            key = (await db.EditImageLayers.SingleAsync(l => l.Id == a)).MatteStorageKey!;
            (await db.EditImageLayers.SingleAsync(l => l.Id == b)).MatteStorageKey = key;
            await db.SaveChangesAsync();
        }

        return (h, p, doc, a, b, key);
    }

    [Fact]
    public async Task Matte_put_does_not_mark_a_key_another_live_layer_still_uses()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid a, Guid b, string key) = await SharedMatteAsync();

        (await PutAsync(h.Documents(), p, doc.Id, a, RgbaPng(4, 3, SKColors.White))).Status.Should().Be(200);
        using (InferenceApiDbContext db = h.NewDb()) (await db.StorageRetentionMarks.CountAsync()).Should().Be(0, "layer B still shows the old mask");

        Harness.Read(await h.Documents().DeleteLayerMatte(p, doc.Id, b, null, CancellationToken.None)).Status.Should().Be(204);
        using InferenceApiDbContext db2 = h.NewDb();
        (await db2.StorageRetentionMarks.SingleAsync()).StorageKey.Should().Be(key, "the last holder releases it once");
    }

    [Fact]
    public async Task Matte_delete_with_a_shared_or_already_marked_key_never_marks_twice()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid a, Guid b, string key) = await SharedMatteAsync();

        Harness.Read(await h.Documents().DeleteLayerMatte(p, doc.Id, a, null, CancellationToken.None)).Status.Should().Be(204);
        using (InferenceApiDbContext db = h.NewDb()) (await db.StorageRetentionMarks.CountAsync()).Should().Be(0);

        Harness.Read(await h.Documents().DeleteLayerMatte(p, doc.Id, b, null, CancellationToken.None)).Status.Should().Be(204);
        using (InferenceApiDbContext db = h.NewDb()) (await db.StorageRetentionMarks.CountAsync(m => m.StorageKey == key)).Should().Be(1);

        // Same key re-attached and released again while a mark exists: still exactly one mark, no failure.
        using (InferenceApiDbContext db = h.NewDb())
        {
            (await db.EditImageLayers.SingleAsync(l => l.Id == a)).MatteStorageKey = key;
            await db.SaveChangesAsync();
        }

        Harness.Read(await h.Documents().DeleteLayerMatte(p, doc.Id, a, null, CancellationToken.None)).Status.Should().Be(204);
        using InferenceApiDbContext db3 = h.NewDb();
        (await db3.StorageRetentionMarks.CountAsync(m => m.StorageKey == key)).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_a_source_layer_marks_its_user_mask_but_never_the_customers_file()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync(matte: RgbaPng(4, 3, SKColors.White));
        string matteKey, imageKey;
        using (InferenceApiDbContext db = h.NewDb())
        {
            EditImageLayer row = await db.EditImageLayers.SingleAsync(l => l.Id == layer);
            (matteKey, imageKey) = (row.MatteStorageKey!, row.StorageKey);
        }

        Harness.Read(await h.Documents().DeleteLayer(p, doc.Id, layer, CancellationToken.None)).Status.Should().Be(204);

        using InferenceApiDbContext db2 = h.NewDb();
        string[] marked = [.. (await db2.StorageRetentionMarks.ToListAsync()).Select(m => m.StorageKey)];
        marked.Should().Contain(matteKey);
        marked.Should().NotContain(imageKey);
    }

    [Fact]
    public async Task Matte_get_serves_a_source_layers_mask_and_dto_reports_hasMatte()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync(matte: RgbaPng(4, 3, SKColors.White));
        (await h.Documents().GetLayerMatte(p, doc.Id, layer, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
        (int status, JsonElement body) = Harness.Read(await h.Documents().Get(p, doc.Id, CancellationToken.None));
        status.Should().Be(200);
        body.GetProperty("layers").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == layer)
            .GetProperty("hasMatte").GetBoolean().Should().BeTrue();
        body.GetProperty("layers").EnumerateArray().Where(l => l.GetProperty("id").GetGuid() != layer)
            .Should().OnlyContain(l => !l.GetProperty("hasMatte").GetBoolean());
    }

    [Fact]
    public async Task Duplicate_copies_a_user_mask_to_a_new_object()
    {
        (Harness h, Guid p, EditImageDocumentDto doc, Guid layer) = await MatteSetupAsync();
        await PutAsync(h.Documents(), p, doc.Id, layer, RgbaPng(4, 3, SKColors.White));
        string original;
        using (InferenceApiDbContext db = h.NewDb()) original = (await db.EditImageLayers.SingleAsync(l => l.Id == layer)).MatteStorageKey!;

        (int status, JsonElement body) = Harness.Read(await h.Documents().Duplicate(p, doc.Id, CancellationToken.None));

        status.Should().Be(201);
        Guid copyId = body.GetProperty("id").GetGuid();
        using InferenceApiDbContext db2 = h.NewDb();
        EditImageLayer copied = (await db2.EditImageLayers.Where(l => l.DocumentId == copyId && l.MatteStorageKey != null).ToListAsync()).Single();
        copied.MatteStorageKey.Should().NotBe(original);
        h.Storage.Objects.Should().ContainKey(copied.MatteStorageKey!);
    }
}
