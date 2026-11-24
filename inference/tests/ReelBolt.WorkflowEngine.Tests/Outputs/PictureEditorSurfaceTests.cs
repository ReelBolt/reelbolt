#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// The picture editor: opening, copying, adding to, serving and cutting from a picture whose pixels come from an
/// output the project keeps back is refused, and the same calls on a later output or an upload are not.
/// </summary>
public sealed class PictureEditorSurfaceTests
{
    public enum Origin { EarlierOutput, LaterOutput, Upload }

    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public FakeFileStorage Storage { get; } = new();
        public Dictionary<Origin, EditImageDocument> Documents { get; } = [];
        public Dictionary<Origin, EditImageLayer> Layers { get; } = [];
        public EditImageDocument CopiedOnly { get; }
        public EditImageLayer CopiedLayer { get; }

        public Rig()
        {
            Add(Origin.EarlierOutput, W.EarlierFile, SurfaceWorld.Marker.AddHours(-2));
            Add(Origin.LaterOutput, W.LaterFile, SurfaceWorld.Marker.AddDays(2));
            Add(Origin.Upload, W.Upload, SurfaceWorld.Marker.AddHours(-5));
            // A picture whose source file is gone and which kept its own copy: the copy's age decides.
            (CopiedOnly, CopiedLayer) = Build(null, null, SurfaceWorld.Marker.AddHours(-2));
            W.Db.EditImageDocuments.Add(CopiedOnly);
            W.Db.EditImageLayers.Add(CopiedLayer);
            W.Db.SaveChanges();
        }

        private (EditImageDocument, EditImageLayer) Build(Guid? sourceFileId, Guid? layerFileId, DateTime createdAt)
        {
            EditImageDocument doc = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = W.Project.Id,
                SourceProjectFileId = sourceFileId,
                Name = "Picture",
                CanvasWidth = 4,
                CanvasHeight = 4,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            };
            string key = $"projects/{W.Project.Id}/agentFiles/image-editor/{doc.Id}/{Guid.NewGuid():N}.png";
            EditImageLayer layer = new()
            {
                Id = Guid.NewGuid(),
                DocumentId = doc.Id,
                Kind = EditImageLayerKind.Source,
                SourceProjectFileId = layerFileId,
                StorageKey = key,
                MatteStorageKey = key + ".matte.png",
                CopySizeBytes = 8,
                CreatedAt = createdAt,
                Label = "Photo",
            };
            Storage.Objects[key] = [1, 2, 3, 4];
            Storage.Objects[layer.MatteStorageKey] = [1, 2, 3, 4];
            return (doc, layer);
        }

        private void Add(Origin origin, ProjectFile file, DateTime createdAt)
        {
            (EditImageDocument doc, EditImageLayer layer) = Build(file.Id, file.Id, createdAt);
            Documents[origin] = doc;
            Layers[origin] = layer;
            W.Db.EditImageDocuments.Add(doc);
            W.Db.EditImageLayers.Add(layer);
        }

        public ProjectFile FileOf(Origin o) => o switch { Origin.EarlierOutput => W.EarlierFile, Origin.LaterOutput => W.LaterFile, _ => W.Upload };

        public EditImageDocumentsController Docs(InferenceApiDbContext? db = null)
        {
            InferenceApiDbContext d = db ?? W.Db;
            return new EditImageDocumentsController(
                d, TestTenancy.Access(d, W.User), Storage, Mock.Of<IInferenceProviderResolver>(),
                Mock.Of<IImageAnalysisClientFactory>(), Mock.Of<IPresignedUrlService>())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
        }
    }

    private static bool Refused(IActionResult? r) =>
        r is ObjectResult { StatusCode: 404, Value: { } v } && v.ToString()!.Contains("OUTPUT_UNAVAILABLE");

    private static bool Refused<T>(ActionResult<T> r) => Refused(r.Result);

    public static IEnumerable<object[]> Served()
    {
        foreach (Origin o in Enum.GetValues<Origin>())
        {
            yield return [o, SurfaceState.Clear];
            yield return [o, SurfaceState.ModerationOnly];
        }
    }

    public static IEnumerable<object[]> KeptOrigins() { yield return [Origin.LaterOutput]; yield return [Origin.Upload]; }

    // ---- open a picture from a file (203) ------------------------------------------------------

    [Fact]
    public async Task Opening_a_picture_from_an_earlier_output_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Docs().Create(r.W.Project.Id, new CreateEditImageDocumentRequest(SourceProjectFileId: r.W.EarlierFile.Id), CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task Opening_a_picture_is_not_refused_when_the_project_keeps_nothing(Origin origin, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Refused(await r.Docs().Create(r.W.Project.Id, new CreateEditImageDocumentRequest(SourceProjectFileId: r.FileOf(origin).Id), CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(KeptOrigins))]
    public async Task Opening_a_picture_from_a_later_output_or_an_upload_is_not_refused(Origin origin)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Docs().Create(r.W.Project.Id, new CreateEditImageDocumentRequest(SourceProjectFileId: r.FileOf(origin).Id), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Opening_a_picture_on_a_failed_read_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        Refused(await r.Docs(failing).Create(r.W.Project.Id, new CreateEditImageDocumentRequest(SourceProjectFileId: r.W.LaterFile.Id), CancellationToken.None)).Should().BeTrue();
    }

    // ---- copy a picture (386) ------------------------------------------------------------------

    [Fact]
    public async Task Copying_a_picture_with_a_layer_from_an_earlier_output_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Docs().Duplicate(r.W.Project.Id, r.Documents[Origin.EarlierOutput].Id, CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(KeptOrigins))]
    public async Task Copying_a_picture_from_a_later_output_or_an_upload_is_not_refused(Origin origin)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Docs().Duplicate(r.W.Project.Id, r.Documents[origin].Id, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task Copying_a_picture_is_not_refused_when_the_project_keeps_nothing(Origin origin, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Refused(await r.Docs().Duplicate(r.W.Project.Id, r.Documents[origin].Id, CancellationToken.None)).Should().BeFalse();
    }

    // ---- add a file as a layer (1007) ----------------------------------------------------------

    [Fact]
    public async Task Adding_an_earlier_output_as_a_layer_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Docs().CreateImageLayerFromFile(r.W.Project.Id, r.Documents[Origin.Upload].Id,
            new CreateImageLayerFromFileRequest(r.W.EarlierFile.Id), CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(KeptOrigins))]
    public async Task Adding_a_later_output_or_an_upload_as_a_layer_is_not_refused(Origin origin)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Docs().CreateImageLayerFromFile(r.W.Project.Id, r.Documents[Origin.Upload].Id,
            new CreateImageLayerFromFileRequest(r.FileOf(origin).Id), CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task Adding_a_layer_is_not_refused_when_the_project_keeps_nothing(Origin origin, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Refused(await r.Docs().CreateImageLayerFromFile(r.W.Project.Id, r.Documents[Origin.Upload].Id,
            new CreateImageLayerFromFileRequest(r.FileOf(origin).Id), CancellationToken.None)).Should().BeFalse();
    }

    // ---- serve a layer's pixels and matte (1506), replace a matte (1252) -----------------------

    [Fact]
    public async Task The_pixels_and_matte_of_a_layer_from_an_earlier_output_are_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Guid doc = r.Documents[Origin.EarlierOutput].Id, layer = r.Layers[Origin.EarlierOutput].Id;
        Refused(await r.Docs().GetLayerImage(r.W.Project.Id, doc, layer, CancellationToken.None)).Should().BeTrue();
        Refused(await r.Docs().GetLayerMatte(r.W.Project.Id, doc, layer, CancellationToken.None)).Should().BeTrue();
        Refused(await r.Docs().PutLayerMatte(r.W.Project.Id, doc, layer, null, CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(KeptOrigins))]
    public async Task The_pixels_of_a_layer_from_a_later_output_or_an_upload_are_served(Origin origin)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Guid doc = r.Documents[origin].Id, layer = r.Layers[origin].Id;
        Refused(await r.Docs().GetLayerImage(r.W.Project.Id, doc, layer, CancellationToken.None)).Should().BeFalse();
        Refused(await r.Docs().GetLayerMatte(r.W.Project.Id, doc, layer, CancellationToken.None)).Should().BeFalse();
        Refused(await r.Docs().PutLayerMatte(r.W.Project.Id, doc, layer, null, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task The_pixels_of_a_layer_are_served_when_the_project_keeps_nothing(Origin origin, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Guid doc = r.Documents[origin].Id, layer = r.Layers[origin].Id;
        (await r.Docs().GetLayerImage(r.W.Project.Id, doc, layer, CancellationToken.None)).Should().NotBeOfType<NotFoundResult>();
        Refused(await r.Docs().GetLayerImage(r.W.Project.Id, doc, layer, CancellationToken.None)).Should().BeFalse();
        Refused(await r.Docs().GetLayerMatte(r.W.Project.Id, doc, layer, CancellationToken.None)).Should().BeFalse();
        Refused(await r.Docs().PutLayerMatte(r.W.Project.Id, doc, layer, null, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task The_pixels_of_a_layer_on_a_failed_read_are_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        Guid doc = r.Documents[Origin.LaterOutput].Id, layer = r.Layers[Origin.LaterOutput].Id;
        Refused(await r.Docs(failing).GetLayerImage(r.W.Project.Id, doc, layer, CancellationToken.None)).Should().BeTrue();
        Refused(await r.Docs(failing).PutLayerMatte(r.W.Project.Id, doc, layer, null, CancellationToken.None)).Should().BeTrue();
    }

    // ---- cut a subject out (615, 640, 672) -----------------------------------------------------

    [Fact]
    public async Task Cutting_a_subject_from_a_layer_made_from_an_earlier_output_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Docs().SegmentSubject(r.W.Project.Id, r.Documents[Origin.EarlierOutput].Id,
            new SegmentSubjectRequest(Prompt: "person", LayerId: r.Layers[Origin.EarlierOutput].Id), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Cutting_a_subject_from_a_picture_opened_from_an_earlier_output_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Docs().SegmentSubject(r.W.Project.Id, r.Documents[Origin.EarlierOutput].Id,
            new SegmentSubjectRequest(Prompt: "person"), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Cutting_a_subject_from_a_picture_that_kept_only_an_old_copy_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Docs().SegmentSubject(r.W.Project.Id, r.CopiedOnly.Id,
            new SegmentSubjectRequest(Prompt: "person"), CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(KeptOrigins))]
    public async Task Cutting_a_subject_from_a_later_output_or_an_upload_is_not_refused(Origin origin)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Docs().SegmentSubject(r.W.Project.Id, r.Documents[origin].Id, new SegmentSubjectRequest(Prompt: "person"), CancellationToken.None)).Should().BeFalse();
        Refused(await r.Docs().SegmentSubject(r.W.Project.Id, r.Documents[origin].Id,
            new SegmentSubjectRequest(Prompt: "person", LayerId: r.Layers[origin].Id), CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task Cutting_a_subject_is_not_refused_when_the_project_keeps_nothing(Origin origin, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Refused(await r.Docs().SegmentSubject(r.W.Project.Id, r.Documents[origin].Id, new SegmentSubjectRequest(Prompt: "person"), CancellationToken.None)).Should().BeFalse();
        Refused(await r.Docs().SegmentSubject(r.W.Project.Id, r.CopiedOnly.Id, new SegmentSubjectRequest(Prompt: "person"), CancellationToken.None)).Should().BeFalse();
    }
    // ---- edit operations (250: the layer being edited; 470: a reference picture) ---------------

    private static EditImageOperationsController Operations(Rig r, InferenceApiDbContext? db = null)
    {
        InferenceApiDbContext d = db ?? r.W.Db;
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(x => x.ResolveImageJobAsync(It.IsAny<InferenceProviderCapability>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedImageProvider(
                Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit, "https://queue.fal.run", "model", "key", 60));
        Mock<IPresignedUrlService> presigner = new();
        presigner.Setup(x => x.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>())).ReturnsAsync("https://get.test/object");
        return new EditImageOperationsController(
            d, TestTenancy.Access(d, r.W.User), r.Storage, resolver.Object, Mock.Of<IImageClientFactory>(),
            Mock.Of<IImageAnalysisClientFactory>(), presigner.Object, currentUser: r.W.User)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task An_edit_of_a_layer_made_from_an_earlier_output_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await Operations(r).RunOperation(r.W.Project.Id, r.Documents[Origin.EarlierOutput].Id,
            new EditImageOperationRequest(Operation: "removeBackground", LayerId: r.Layers[Origin.EarlierOutput].Id), CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(KeptOrigins))]
    public async Task An_edit_of_a_layer_from_a_later_output_or_an_upload_is_not_refused(Origin origin)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await Operations(r).RunOperation(r.W.Project.Id, r.Documents[origin].Id,
            new EditImageOperationRequest(Operation: "removeBackground", LayerId: r.Layers[origin].Id), CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task An_edit_is_not_refused_when_the_project_keeps_nothing(Origin origin, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Refused(await Operations(r).RunOperation(r.W.Project.Id, r.Documents[origin].Id,
            new EditImageOperationRequest(Operation: "removeBackground", LayerId: r.Layers[origin].Id), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task An_edit_against_an_earlier_output_as_a_reference_picture_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        EditImageOperationRequest request = new(
            Operation: "instruct", LayerId: r.Layers[Origin.Upload].Id, Prompt: "swap the face",
            ReferenceProjectFileIds: [r.W.EarlierFile.Id]);
        Refused(await Operations(r).RunOperation(r.W.Project.Id, r.Documents[Origin.Upload].Id, request, CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(KeptOrigins))]
    public async Task A_later_output_or_an_upload_as_a_reference_picture_is_not_refused(Origin origin)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        EditImageOperationRequest request = new(
            Operation: "instruct", LayerId: r.Layers[Origin.Upload].Id, Prompt: "swap the face",
            ReferenceProjectFileIds: [r.FileOf(origin).Id]);
        Refused(await Operations(r).RunOperation(r.W.Project.Id, r.Documents[Origin.Upload].Id, request, CancellationToken.None)).Should().BeFalse();
    }
}

