using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public partial class EditImageDocumentControllersTests
{
    [Fact]
    public async Task Crop_round_trips_and_a_default_layer_reports_no_crop()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layer = doc.Layers[0].Id;
        doc.Layers[0].Crop.Should().BeNull("a layer that was never cropped has no crop field value");

        (int status, JsonElement body) = Harness.Read(await h.Documents().UpdateLayer(projectId, doc.Id, layer,
            new UpdateEditImageLayerRequest(Crop: new EditImageLayerCropDto(0.25, 0.1, 0.5, 0.6)), CancellationToken.None));

        status.Should().Be(200);
        JsonElement crop = body.GetProperty("crop");
        crop.GetProperty("x").GetDouble().Should().Be(0.25);
        crop.GetProperty("h").GetDouble().Should().Be(0.6);
        (int get, JsonElement after) = Harness.Read(await h.Documents().Get(projectId, doc.Id, CancellationToken.None));
        get.Should().Be(200);
        after.GetProperty("layers")[0].GetProperty("crop").GetProperty("w").GetDouble().Should().Be(0.5);
    }

    [Fact]
    public async Task Sending_the_full_window_removes_the_crop()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layer = doc.Layers[0].Id;
        await h.Documents().UpdateLayer(projectId, doc.Id, layer,
            new UpdateEditImageLayerRequest(Crop: new EditImageLayerCropDto(0.1, 0.1, 0.5, 0.5)), CancellationToken.None);

        (int status, JsonElement body) = Harness.Read(await h.Documents().UpdateLayer(projectId, doc.Id, layer,
            new UpdateEditImageLayerRequest(Crop: new EditImageLayerCropDto(0, 0, 1, 1)), CancellationToken.None));

        status.Should().Be(200);
        body.TryGetProperty("crop", out JsonElement crop).Should().BeTrue();
        crop.ValueKind.Should().Be(JsonValueKind.Null);
        using var db = h.NewDb();
        (await db.EditImageLayers.SingleAsync(l => l.Id == layer)).CropJson.Should().BeNull();
    }

    [Theory]
    [InlineData(-0.1, 0, 0.5, 0.5)]
    [InlineData(0.6, 0, 0.5, 0.5)]
    [InlineData(0, 0.7, 0.5, 0.5)]
    [InlineData(0, 0, 0, 0.5)]
    [InlineData(0, 0, 0.5, 0.005)]
    public async Task A_crop_outside_the_picture_or_too_small_is_400_and_changes_nothing(double x, double y, double w, double hgt)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layer = doc.Layers[0].Id;

        Harness.Read(await h.Documents().UpdateLayer(projectId, doc.Id, layer,
            new UpdateEditImageLayerRequest(Opacity: 0.5, Crop: new EditImageLayerCropDto(x, y, w, hgt)), CancellationToken.None))
            .Status.Should().Be(400);

        using var db = h.NewDb();
        EditImageLayer row = await db.EditImageLayers.SingleAsync(l => l.Id == layer);
        row.CropJson.Should().BeNull();
        row.Opacity.Should().Be(1, "a bad crop rejects the whole request");
    }

    [Fact]
    public async Task A_text_layer_cannot_be_cropped()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        EditImageLayerDto text = await h.AddTextLayerAsync(projectId, doc.Id, "title");

        Harness.Read(await h.Documents().UpdateLayer(projectId, doc.Id, text.Id,
            new UpdateEditImageLayerRequest(Crop: new EditImageLayerCropDto(0, 0, 0.5, 0.5)), CancellationToken.None))
            .Status.Should().Be(400);
    }

    [Fact]
    public async Task Crop_on_a_foreign_project_is_404()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        Harness.Read(await h.Documents(Guid.NewGuid()).UpdateLayer(projectId, doc.Id, doc.Layers[0].Id,
            new UpdateEditImageLayerRequest(Crop: new EditImageLayerCropDto(0, 0, 0.5, 0.5)), CancellationToken.None))
            .Status.Should().Be(404);
    }

    [Fact]
    public async Task Duplicate_copies_the_crop_with_the_layer()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        await h.Documents().UpdateLayer(projectId, doc.Id, doc.Layers[0].Id,
            new UpdateEditImageLayerRequest(Crop: new EditImageLayerCropDto(0.2, 0.3, 0.4, 0.5)), CancellationToken.None);

        (int status, JsonElement body) = Harness.Read(await h.Documents().Duplicate(projectId, doc.Id, CancellationToken.None));

        status.Should().Be(201);
        JsonElement crop = body.GetProperty("layers")[0].GetProperty("crop");
        crop.GetProperty("x").GetDouble().Should().Be(0.2);
        crop.GetProperty("h").GetDouble().Should().Be(0.5);
        Guid newId = body.GetProperty("id").GetGuid();
        using var db = h.NewDb();
        EditImageLayer copy = (await db.EditImageDocuments.Include(d => d.Layers).SingleAsync(d => d.Id == newId)).Layers.Single();
        copy.CopySizeBytes.Should().BeGreaterThan(0, "the copy still owns its own bytes, so quota accounting is unchanged by the crop");
    }
}
