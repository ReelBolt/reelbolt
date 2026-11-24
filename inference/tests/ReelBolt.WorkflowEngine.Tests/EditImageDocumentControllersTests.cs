#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The thumbnail editor's three document controllers (docs/design/thumbnail-editor.md): documents,
/// layers, subjects, text, uploads, exports and the AI operations.
///
/// <para>
/// Every vendor, the object store and the presigner are fakes, so these tests pin what OUR code does —
/// validation, dispatch, the layer arithmetic, the error vocabulary, the retention marks and the
/// tenancy gate — and make no claim about what a real vendor returns. Failure cases re-read the
/// database through a FRESH context, so "nothing was written" is a statement about the database and
/// not about the identity map of the context that served the request.
/// </para>
/// </summary>
public partial class EditImageDocumentControllersTests
{
    /// <summary>A real 1 x 1 PNG (valid IHDR, IDAT and IEND): the default source's header declares a known size.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    /// <summary>A PNG signature and no IHDR: a header the size parser cannot read.</summary>
    internal static readonly byte[] UnreadablePng = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // =============================================================================================
    // Documents
    // =============================================================================================

    [Fact]
    public async Task Create_returns_a_document_with_the_editor_defaults()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();
        Guid fileId = await h.SeedFileWithBytesAsync(projectId, "photo.png", "image/png", PngOfSize(1200, 800));

        EditImageDocumentDto doc = await h.CreateDocumentAsync(projectId, fileId);

        // W0.8 T1: a new picture opens at its photo's own size, measured from the stored header, so the
        // canvas and the photo agree from the first frame. (Before that it opened at NULL, "the source
        // image's size", and the browser was the only thing that knew it.)
        doc.CanvasWidth.Should().Be(1200);
        doc.CanvasHeight.Should().Be(800);
        doc.BackgroundColor.Should().BeNull();
        EditImageLayerDto backdrop = doc.Layers.Should().ContainSingle().Subject;
        backdrop.Kind.Should().Be("Source");
        backdrop.Visible.Should().BeTrue();
        backdrop.Locked.Should().BeFalse();
        backdrop.BlendMode.Should().Be("normal");
        backdrop.Content.Should().BeNull();
        backdrop.Provenance.Should().BeNull();

        // the wire names the web agent will read
        JsonElement json = JsonSerializer.SerializeToElement(backdrop, WebJson);
        json.GetProperty("transform").GetProperty("flipX").GetBoolean().Should().BeFalse();
        json.GetProperty("transform").GetProperty("flipY").GetBoolean().Should().BeFalse();
        json.GetProperty("blendMode").GetString().Should().Be("normal");
        json.TryGetProperty("visible", out _).Should().BeTrue();
        json.TryGetProperty("locked", out _).Should().BeTrue();
    }

    [Fact]
    public async Task PatchDocument_sets_name_canvas_and_background_and_bumps_the_revision()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Documents().UpdateDocument(
            projectId, doc.Id,
            new UpdateEditImageDocumentRequest(" Cover ", 1280, 720, "#112233", doc.Revision), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("name").GetString().Should().Be("Cover");
        body.GetProperty("canvasWidth").GetInt32().Should().Be(1280);
        body.GetProperty("canvasHeight").GetInt32().Should().Be(720);
        body.GetProperty("backgroundColor").GetString().Should().Be("#112233");
        body.GetProperty("revision").GetInt32().Should().Be(doc.Revision + 1);

        // 0/0 and "" are the documented spelling of "clear"
        (status, body) = Harness.Read(await h.Documents().UpdateDocument(
            projectId, doc.Id, new UpdateEditImageDocumentRequest(CanvasWidth: 0, CanvasHeight: 0, BackgroundColor: ""),
            CancellationToken.None));
        status.Should().Be(200);
        body.GetProperty("canvasWidth").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("backgroundColor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PatchDocument_with_a_stale_base_revision_is_409_and_writes_nothing()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Documents().UpdateDocument(
            projectId, doc.Id, new UpdateEditImageDocumentRequest("Nope", 1280, 720, BaseRevision: doc.Revision + 5),
            CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("REVISION_CONFLICT");
        body.GetProperty("revision").GetInt32().Should().Be(doc.Revision);

        using InferenceApiDbContext db = h.NewDb();
        EditImageDocument stored = await db.EditImageDocuments.SingleAsync(d => d.Id == doc.Id);
        stored.Name.Should().BeNull();
        // The picture opened at its photo's measured canvas; the refused PATCH left that size alone.
        stored.CanvasWidth.Should().Be(doc.CanvasWidth);
        stored.Revision.Should().Be(doc.Revision);
    }

    [Theory]
    [InlineData(1280, null, null)]
    [InlineData(null, 720, null)]
    [InlineData(15, 720, null)]
    [InlineData(1280, 9000, null)]
    [InlineData(null, null, "red; background: url(x)")]
    public async Task PatchDocument_rejects_a_half_a_canvas_out_of_range_sizes_and_a_hostile_colour(
        int? width, int? height, string? colour)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Documents().UpdateDocument(
            projectId, doc.Id, new UpdateEditImageDocumentRequest(CanvasWidth: width, CanvasHeight: height, BackgroundColor: colour),
            CancellationToken.None));

        status.Should().Be(400);
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageDocuments.SingleAsync(d => d.Id == doc.Id)).Revision.Should().Be(doc.Revision);
    }

    // =============================================================================================
    // Text layers
    // =============================================================================================

    [Fact]
    public async Task CreateTextLayer_stores_the_content_on_top_with_no_bitmap_and_the_image_route_is_404()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        TextContentDto content = new(
            "COME PERMETTERSI\nUNA M2 A 23 ANNI", "Chakra Petch", 700, 96, 1.05, 0, "left", "#ffffff",
            new TextStrokeDto("#000000", 6), new TextShadowDto("rgba(0,0,0,0.6)", 18, 0, 8), 900, false);

        (int status, JsonElement body) = Harness.Read(await h.Documents().CreateTextLayer(
            projectId, doc.Id, new CreateTextLayerRequest(content, new EditImageLayerTransformDto(40, 60, 1, 0)),
            CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("kind").GetString().Should().Be("Text");
        body.GetProperty("role").GetString().Should().Be("overlay");
        body.GetProperty("zOrder").GetInt32().Should().Be(2);
        body.GetProperty("label").GetString().Should().Be("COME PERMETTERSI");
        body.GetProperty("content").GetProperty("fontSize").GetDouble().Should().Be(96);
        body.GetProperty("content").GetProperty("stroke").GetProperty("width").GetDouble().Should().Be(6);
        body.GetProperty("content").GetProperty("shadow").GetProperty("blur").GetDouble().Should().Be(18);
        body.GetProperty("content").GetProperty("text").GetString().Should().Contain("\n");
        body.GetProperty("transform").GetProperty("x").GetDouble().Should().Be(40);

        Guid layerId = body.GetProperty("id").GetGuid();
        using (InferenceApiDbContext db = h.NewDb())
        {
            EditImageLayer stored = await db.EditImageLayers.SingleAsync(l => l.Id == layerId);
            stored.StorageKey.Should().BeEmpty("a text layer has no bitmap");
            stored.ContentJson.Should().NotBeNull();
            (await db.EditImageDocuments.SingleAsync(d => d.Id == doc.Id)).Revision.Should().Be(doc.Revision + 1);
        }

        h.Documents().Should().NotBeNull();
        (int imageStatus, _) = Harness.Read(await h.Documents().GetLayerImage(projectId, doc.Id, layerId, CancellationToken.None));
        (int matteStatus, _) = Harness.Read(await h.Documents().GetLayerMatte(projectId, doc.Id, layerId, CancellationToken.None));
        imageStatus.Should().Be(404);
        matteStatus.Should().Be(404);
    }

    [Theory]
    [InlineData("fontSize", 7)]
    [InlineData("fontSize", 601)]
    [InlineData("strokeWidth", 65)]
    [InlineData("strokeWidth", -1)]
    [InlineData("textLength", 2001)]
    [InlineData("align", 0)]
    [InlineData("fontFamily", 0)]
    [InlineData("color", 0)]
    [InlineData("missingText", 0)]
    public async Task CreateTextLayer_enforces_the_section_4_bounds(string field, double value)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        TextContentDto content = new("hello", "Chakra Petch", 700, 96, 1.05, 0, "left", "#ffffff");
        content = field switch
        {
            "fontSize" => content with { FontSize = value },
            "strokeWidth" => content with { Stroke = new TextStrokeDto("#000000", value) },
            "textLength" => content with { Text = new string('a', (int)value) },
            "align" => content with { Align = "justify" },
            "fontFamily" => content with { FontFamily = "Evil'; drop" },
            "color" => content with { Color = "url(javascript:1)" },
            "missingText" => content with { Text = null },
            _ => content
        };

        (int status, _) = Harness.Read(await h.Documents().CreateTextLayer(
            projectId, doc.Id, new CreateTextLayerRequest(content), CancellationToken.None));

        status.Should().Be(400);
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageLayers.CountAsync(l => l.DocumentId == doc.Id)).Should().Be(1, "only the backdrop");
    }

    [Fact]
    public async Task CreateTextLayer_accepts_the_inclusive_bounds_and_requires_content()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        foreach (TextContentDto ok in new[]
                 {
                     new TextContentDto(new string('x', 2000), FontSize: 8, Stroke: new TextStrokeDto("#000", 0)),
                     new TextContentDto("x", FontSize: 600, Stroke: new TextStrokeDto("#000", 64))
                 })
        {
            (int status, _) = Harness.Read(await h.Documents().CreateTextLayer(
                projectId, doc.Id, new CreateTextLayerRequest(ok), CancellationToken.None));
            status.Should().Be(200);
        }

        (int missing, _) = Harness.Read(await h.Documents().CreateTextLayer(
            projectId, doc.Id, new CreateTextLayerRequest(), CancellationToken.None));
        missing.Should().Be(400);
    }

    // =============================================================================================
    // from-file
    // =============================================================================================

    [Fact]
    public async Task CreateImageLayerFromFile_places_the_project_image_as_an_Image_layer()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid logoId = await h.SeedImageFileAsync(projectId, "logo.png");

        (int status, JsonElement body) = Harness.Read(await h.Documents().CreateImageLayerFromFile(
            projectId, doc.Id, new CreateImageLayerFromFileRequest(logoId), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("kind").GetString().Should().Be("Image");
        body.GetProperty("role").GetString().Should().Be("overlay");
        body.GetProperty("label").GetString().Should().Be("logo.png");
        body.GetProperty("sourceProjectFileId").GetGuid().Should().Be(logoId);
        body.GetProperty("zOrder").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task CreateImageLayerFromFile_refuses_a_non_image_and_another_projects_file()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid notAnImage = await h.SeedFileAsync(projectId, "notes.txt", "text/plain");
        Guid otherProject = await h.SeedProjectAsync();
        Guid foreign = await h.SeedImageFileAsync(otherProject);

        (int status, JsonElement body) = Harness.Read(await h.Documents().CreateImageLayerFromFile(
            projectId, doc.Id, new CreateImageLayerFromFileRequest(notAnImage), CancellationToken.None));
        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("IMAGE_SOURCE_UNSUPPORTED");

        (int foreignStatus, _) = Harness.Read(await h.Documents().CreateImageLayerFromFile(
            projectId, doc.Id, new CreateImageLayerFromFileRequest(foreign), CancellationToken.None));
        foreignStatus.Should().Be(404);
    }

    [Theory]
    [InlineData("layers.psd", "image/vnd.adobe.photoshop")]
    [InlineData("layers.ora", "image/openraster")]
    public async Task A_layered_export_is_neither_a_document_source_nor_a_placed_layer(string name, string mime)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layered = await h.SeedFileAsync(projectId, name, mime);

        (int layerStatus, JsonElement layerBody) = Harness.Read(await h.Documents().CreateImageLayerFromFile(
            projectId, doc.Id, new CreateImageLayerFromFileRequest(layered), CancellationToken.None));
        layerStatus.Should().Be(409, "no browser can draw a layered file, so it would be a layer that never loads");
        layerBody.GetProperty("code").GetString().Should().Be("IMAGE_SOURCE_UNSUPPORTED");

        (int docStatus, JsonElement docBody) = Harness.Read(await h.Documents().Create(
            projectId, new CreateEditImageDocumentRequest(layered), CancellationToken.None));
        docStatus.Should().Be(409);
        docBody.GetProperty("code").GetString().Should().Be("IMAGE_SOURCE_UNSUPPORTED");
    }

    // =============================================================================================
    // PATCH layer
    // =============================================================================================

    [Fact]
    public async Task PatchLayer_updates_visibility_lock_blend_label_and_flips()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layerId = doc.Layers[0].Id;

        (int status, JsonElement body) = Harness.Read(await h.Documents().UpdateLayer(
            projectId, doc.Id, layerId,
            new UpdateEditImageLayerRequest(
                Transform: new EditImageLayerTransformDto(10, 20, 1.5, 15, FlipX: true, FlipY: true),
                Visible: false, Locked: true, BlendMode: "Soft-Light", Label: "  Backdrop  "),
            CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("visible").GetBoolean().Should().BeFalse();
        body.GetProperty("locked").GetBoolean().Should().BeTrue();
        body.GetProperty("blendMode").GetString().Should().Be("soft-light", "stored in the canonical spelling");
        body.GetProperty("label").GetString().Should().Be("Backdrop");
        body.GetProperty("transform").GetProperty("flipX").GetBoolean().Should().BeTrue();
        body.GetProperty("transform").GetProperty("flipY").GetBoolean().Should().BeTrue();
        body.GetProperty("transform").GetProperty("rotation").GetDouble().Should().Be(15);

        using InferenceApiDbContext db = h.NewDb();
        EditImageLayer stored = await db.EditImageLayers.SingleAsync(l => l.Id == layerId);
        stored.Visible.Should().BeFalse("a bool column must not fall back to a default of true");
        stored.Locked.Should().BeTrue();
    }

    [Theory]
    [InlineData("blend")]
    [InlineData("label")]
    [InlineData("scale")]
    [InlineData("contentOnBitmap")]
    public async Task PatchLayer_rejects_bad_input_and_applies_none_of_the_request(string kind)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layerId = doc.Layers[0].Id;

        UpdateEditImageLayerRequest request = kind switch
        {
            "blend" => new UpdateEditImageLayerRequest(Visible: false, BlendMode: "colour-burn"),
            "label" => new UpdateEditImageLayerRequest(Visible: false, Label: "   "),
            "scale" => new UpdateEditImageLayerRequest(Visible: false, Transform: new EditImageLayerTransformDto(Scale: -1)),
            _ => new UpdateEditImageLayerRequest(Visible: false, Content: new TextContentDto("x"))
        };

        (int status, _) = Harness.Read(await h.Documents().UpdateLayer(projectId, doc.Id, layerId, request, CancellationToken.None));

        status.Should().Be(400);
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageLayers.SingleAsync(l => l.Id == layerId)).Visible.Should().BeTrue("the valid Visible field must not be applied alone");
    }

    [Fact]
    public async Task PatchLayer_replaces_the_content_of_a_text_layer_only()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        EditImageLayerDto text = await h.AddTextLayerAsync(projectId, doc.Id, "first");

        (int status, JsonElement body) = Harness.Read(await h.Documents().UpdateLayer(
            projectId, doc.Id, text.Id,
            new UpdateEditImageLayerRequest(Content: new TextContentDto("second", FontSize: 120)), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("content").GetProperty("text").GetString().Should().Be("second");
        body.GetProperty("content").GetProperty("fontSize").GetDouble().Should().Be(120);
    }

    [Fact]
    public async Task PatchLayer_with_a_stale_base_revision_is_409_and_writes_nothing()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Documents().UpdateLayer(
            projectId, doc.Id, doc.Layers[0].Id,
            new UpdateEditImageLayerRequest(Visible: false, BaseRevision: doc.Revision + 3), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("REVISION_CONFLICT");
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageLayers.SingleAsync(l => l.Id == doc.Layers[0].Id)).Visible.Should().BeTrue();
    }

    // =============================================================================================
    // Deleting layers: retention
    // =============================================================================================

    [Fact]
    public async Task Deleting_a_generated_layer_marks_its_object_but_deleting_the_source_marks_nothing()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid sourceLayer = doc.Layers[0].Id;
        Guid generated = await h.RunOperationAsync(projectId, doc.Id, new EditImageOperationRequest(
            "instruct", sourceLayer, Prompt: "make it blue"));

        await h.Documents().DeleteLayer(projectId, doc.Id, generated, CancellationToken.None);
        await h.Documents().DeleteLayer(projectId, doc.Id, sourceLayer, CancellationToken.None);

        using InferenceApiDbContext db = h.NewDb();
        EditImageLayer stored = await db.EditImageLayers.IgnoreQueryFilters().SingleAsync(l => l.Id == generated);
        List<StorageRetentionMark> marks = await db.StorageRetentionMarks.ToListAsync();
        marks.Should().ContainSingle("only the editor's own object is marked - the Source layer IS the customer's file")
            .Which.StorageKey.Should().Be(stored.StorageKey);
    }

    // =============================================================================================
    // Subjects
    // =============================================================================================

    [Fact]
    public async Task SegmentSubject_refuses_a_source_that_is_an_output_without_a_verdict_before_presigning_it()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        using (InferenceApiDbContext db = h.NewDb())
        {
            EditImageDocument stored = await db.EditImageDocuments.SingleAsync(d => d.Id == doc.Id);
            ProjectFile source = await db.ProjectFiles.SingleAsync(f => f.Id == stored.SourceProjectFileId);
            source.StorageKey = $"projects/{projectId}/outputFiles/render.png";
            await db.SaveChangesAsync();
        }

        Mock<IFileStorageService> moderated = new();
        moderated.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(8, "image/png", "\"e1\""));
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration moderation = new(
            new Mock<ReelBolt.Shared.Moderation.IContentModerationGate>().Object, moderated.Object,
            new Mock<ReelBolt.Shared.Moderation.IModerationMediaSampler>().Object,
            new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object, h.NewDb(),
            Microsoft.Extensions.Options.Options.Create(new ReelBolt.Shared.Moderation.ContentModerationOptions { Enabled = true }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Moderation.PublishModeration>.Instance);

        (int status, JsonElement body) = Harness.Read(await h.Documents(moderation: moderation).SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the person"), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("CONTENT_MODERATION_PENDING");
        h.Analysis.Requests.Should().BeEmpty("nothing reached the segmentation vendor");
    }

    [Fact]
    public async Task SegmentSubject_refuses_a_picture_whose_source_file_is_quarantined()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        using (InferenceApiDbContext db = h.NewDb())
        {
            EditImageDocument stored = await db.EditImageDocuments.SingleAsync(d => d.Id == doc.Id);
            ProjectFile source = await db.ProjectFiles.SingleAsync(f => f.Id == stored.SourceProjectFileId);
            source.CsamStatus = CsamScanStatus.Quarantined;
            await db.SaveChangesAsync();
        }

        (int status, _) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the person"), CancellationToken.None));

        status.Should().Be(404, "a quarantined source is never presigned to the segmentation vendor");
        h.Analysis.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Subjects_without_the_new_fields_behave_as_before_and_answer_an_array_of_one()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the person", Variant: "Matting"), CancellationToken.None));

        status.Should().Be(200);
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(1);
        JsonElement layer = body[0];
        layer.GetProperty("kind").GetString().Should().Be("Cutout");
        layer.GetProperty("role").GetString().Should().Be("subject");
        layer.GetProperty("label").GetString().Should().Be("the person");
        layer.GetProperty("segmentationPrompt").GetString().Should().Be("the person");
        layer.GetProperty("matteUrl").GetString().Should().EndWith("/matte");

        SegmentationRequest sent = h.Analysis.Requests.Should().ContainSingle().Subject;
        sent.PointPrompts.Should().BeNull("the new fields are absent unless the caller sent them");
        sent.BoxPrompts.Should().BeNull();
        sent.ReturnMultipleMasks.Should().BeNull();
        sent.MaxMasks.Should().BeNull();
        sent.Prompt.Should().Be("the person");
        sent.Variant.Should().Be("Matting");
    }

    [Fact]
    public async Task Subjects_with_point_and_box_prompts_pass_them_through_verbatim()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id,
            new SegmentSubjectRequest(
                PointPrompts: [new PointPromptDto(120, 340, 1), new PointPromptDto(5, 6, 0)],
                BoxPrompts: [new BoxPromptDto(10, 20, 300, 400)]),
            CancellationToken.None));

        status.Should().Be(200);
        SegmentationRequest sent = h.Analysis.Requests.Should().ContainSingle().Subject;
        sent.PointPrompts.Should().Equal(new PointPrompt(120, 340, 1), new PointPrompt(5, 6, 0));
        sent.BoxPrompts.Should().Equal(new BoxPrompt(10, 20, 300, 400));
        sent.Prompt.Should().BeNull();
    }

    [Fact]
    public async Task Subjects_with_several_masks_create_one_Cutout_layer_per_mask_in_order()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Analysis.OnSegment = (_, _) => Task.FromResult(
            new SegmentationResult(Img(10, 10), Img(10, 10), "seg-1")
            {
                Masks = [new SegmentedMask(Img(10, 10), Img(10, 10)), new SegmentedMask(Img(20, 20), null), new SegmentedMask(Img(30, 30), null)]
            });

        (int status, JsonElement body) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id,
            new SegmentSubjectRequest(Prompt: "people", ReturnMultipleMasks: true, MaxMasks: 2), CancellationToken.None));

        status.Should().Be(200);
        body.GetArrayLength().Should().Be(2, "MaxMasks caps the layers created");
        body[0].GetProperty("zOrder").GetInt32().Should().Be(2);
        body[1].GetProperty("zOrder").GetInt32().Should().Be(3);
        body[0].GetProperty("label").GetString().Should().Be("people 1");
        body[1].GetProperty("label").GetString().Should().Be("people 2");
        body[0].GetProperty("matteUrl").GetString().Should().NotBeNull();
        body[1].GetProperty("matteUrl").ValueKind.Should().Be(JsonValueKind.Null);

        h.Analysis.Requests.Single().ReturnMultipleMasks.Should().BeTrue();
        h.Analysis.Requests.Single().MaxMasks.Should().Be(2);

        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageLayers.CountAsync(l => l.DocumentId == doc.Id && l.Kind == EditImageLayerKind.Cutout)).Should().Be(2);
        (await db.EditImageDocuments.SingleAsync(d => d.Id == doc.Id)).Revision.Should().Be(doc.Revision + 1, "one revision for the whole batch");
    }

    [Fact]
    public async Task Subjects_without_ReturnMultipleMasks_keep_only_the_first_mask()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Analysis.OnSegment = (_, _) => Task.FromResult(
            new SegmentationResult(Img(10, 10), null)
            {
                Masks = [new SegmentedMask(Img(10, 10), null), new SegmentedMask(Img(20, 20), null)]
            });

        (_, JsonElement body) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "x"), CancellationToken.None));

        body.GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData("negativePoint")]
    [InlineData("badLabel")]
    [InlineData("emptyBox")]
    [InlineData("tooManyPoints")]
    [InlineData("maxMasks")]
    [InlineData("role")]
    public async Task Subjects_reject_malformed_prompts_before_any_vendor_call(string kind)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        SegmentSubjectRequest request = kind switch
        {
            "negativePoint" => new SegmentSubjectRequest(PointPrompts: [new PointPromptDto(-1, 5, 1)]),
            "badLabel" => new SegmentSubjectRequest(PointPrompts: [new PointPromptDto(1, 5, 2)]),
            "emptyBox" => new SegmentSubjectRequest(BoxPrompts: [new BoxPromptDto(10, 10, 10, 50)]),
            "tooManyPoints" => new SegmentSubjectRequest(PointPrompts: [.. Enumerable.Range(0, 65).Select(i => new PointPromptDto(i, i, 1))]),
            "maxMasks" => new SegmentSubjectRequest(ReturnMultipleMasks: true, MaxMasks: 17),
            _ => new SegmentSubjectRequest(Role: "sticker")
        };

        (int status, _) = Harness.Read(await h.Documents().SegmentSubject(projectId, doc.Id, request, CancellationToken.None));

        status.Should().Be(400);
        h.Analysis.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Subjects_with_no_provider_are_409_and_a_vendor_failure_is_424_and_writes_nothing()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        h.Segmentation = null;
        (int noProvider, JsonElement noBody) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "x"), CancellationToken.None));
        noProvider.Should().Be(409);
        noBody.GetProperty("code").GetString().Should().Be("SEGMENTATION_PROVIDER_UNAVAILABLE");

        h.Segmentation = Harness.FalSegmentation();
        h.Analysis.OnSegment = (_, _) => throw new InvalidOperationException("fal said no");
        (int failed, JsonElement failBody) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "x"), CancellationToken.None));
        failed.Should().Be(424, "never 502: Cloudflare replaces a 502's body, and the reason would never reach the user");
        failBody.GetProperty("code").GetString().Should().Be("SEGMENTATION_FAILED");
        failBody.GetProperty("message").GetString().Should().Be("The AI service could not cut out the subject. Nothing was added to your picture. It said: fal said no");

        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageLayers.CountAsync(l => l.DocumentId == doc.Id)).Should().Be(1);
        h.Storage.Objects.Keys.Should().NotContain(k => k.Contains("/agentFiles/"));
    }

    // =============================================================================================
    // assets/upload-url
    // =============================================================================================

    [Theory]
    [InlineData("mask")]
    [InlineData("export")]
    [InlineData("source")]
    public async Task UploadUrl_issues_a_server_chosen_key_a_presigned_put_and_a_24_hour_retention_mark(string purpose)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Assets().CreateUploadUrl(
            projectId, doc.Id, new EditImageUploadUrlRequest(purpose, "image/png", 1234), CancellationToken.None));

        status.Should().Be(200);
        string key = body.GetProperty("key").GetString()!;
        key.Should().MatchRegex(
            "^projects/" + projectId + "/agentFiles/image-editor/" + doc.Id + "/" + purpose
            + "-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.png$");
        body.GetProperty("url").GetString().Should().Be("https://put.test/" + key);
        body.GetProperty("headers").GetProperty("Content-Type").GetString().Should().Be("image/png");
        body.GetProperty("expiresAt").GetDateTime().Should().BeCloseTo(Harness.Now.AddMinutes(30), TimeSpan.FromSeconds(2));

        using InferenceApiDbContext db = h.NewDb();
        StorageRetentionMark mark = (await db.StorageRetentionMarks.ToListAsync()).Should().ContainSingle().Subject;
        mark.StorageKey.Should().Be(key);
        mark.Category.Should().Be("agentFiles");
        mark.ProjectId.Should().Be(projectId);
        // ripe exactly one day after issue under the default 7-day grace period
        (mark.MarkedAt + TimeSpan.FromDays(7)).Should().BeCloseTo(Harness.Now.AddHours(24), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task UploadUrl_honours_the_configured_grace_period()
    {
        Harness h = new() { GraceDays = 3 };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        await h.Assets().CreateUploadUrl(
            projectId, doc.Id, new EditImageUploadUrlRequest("mask", "image/png", 10), CancellationToken.None);

        using InferenceApiDbContext db = h.NewDb();
        StorageRetentionMark mark = await db.StorageRetentionMarks.SingleAsync();
        (mark.MarkedAt + TimeSpan.FromDays(3)).Should().BeCloseTo(Harness.Now.AddHours(24), TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("thumbnail", "image/png", 10L, 400)]
    [InlineData(null, "image/png", 10L, 400)]
    [InlineData("mask", "image/jpeg", 10L, 400)]
    [InlineData("mask", "image/vnd.adobe.photoshop", 10L, 400)]
    [InlineData("export", "image/gif", 10L, 400)]
    [InlineData("export", "application/zip", 10L, 400)]
    [InlineData("export", null, 10L, 400)]
    [InlineData("mask", "image/png", 0L, 400)]
    [InlineData("mask", "image/png", 12_582_913L, 413)]
    [InlineData("export", "image/vnd.adobe.photoshop", 0L, 400)]
    [InlineData("export", "image/png", 209_715_201L, 413)]
    [InlineData("source", "image/webp", 10L, 400)]
    [InlineData("source", "image/jpeg", 50_331_649L, 413)]
    public async Task UploadUrl_validates_purpose_type_and_size_and_marks_nothing(
        string? purpose, string? contentType, long length, int expected)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Assets().CreateUploadUrl(
            projectId, doc.Id, new EditImageUploadUrlRequest(purpose, contentType, length), CancellationToken.None));

        status.Should().Be(expected);
        using InferenceApiDbContext db = h.NewDb();
        (await db.StorageRetentionMarks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UploadUrl_accepts_exactly_12_MiB()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Assets().CreateUploadUrl(
            projectId, doc.Id, new EditImageUploadUrlRequest("export", "image/png", 12_582_912L), CancellationToken.None));

        status.Should().Be(200);
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("IMAGE/JPEG", ".jpg")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/avif", ".avif")]
    [InlineData("image/vnd.adobe.photoshop", ".psd")]
    [InlineData("image/openraster", ".ora")]
    public async Task UploadUrl_issues_an_export_key_with_the_extension_and_signed_type_of_its_format(
        string contentType, string extension)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Assets().CreateUploadUrl(
            projectId, doc.Id, new EditImageUploadUrlRequest("export", contentType, 1234), CancellationToken.None));

        status.Should().Be(200);
        string key = body.GetProperty("key").GetString()!;
        key.Should().MatchRegex(
            "^projects/" + projectId + "/agentFiles/image-editor/" + doc.Id
            + "/export-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}" + Regex.Escape(extension) + "$");
        // the browser must PUT with exactly the type that was signed: the canonical lower-case spelling
        body.GetProperty("headers").GetProperty("Content-Type").GetString().Should().Be(contentType.ToLowerInvariant());

        // and the key the route issued is one the export route accepts
        h.Storage.Objects[key] = Png;
        (int exportStatus, _) = Harness.Read(await h.Assets().CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(key, 10, 10, "x"), CancellationToken.None));
        exportStatus.Should().Be(201);
    }

    [Theory]
    [InlineData(12_582_913L)]
    [InlineData(209_715_200L)]
    public async Task UploadUrl_lets_an_export_be_larger_than_a_mask_up_to_200_MiB(long length)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Assets().CreateUploadUrl(
            projectId, doc.Id, new EditImageUploadUrlRequest("export", "image/vnd.adobe.photoshop", length), CancellationToken.None));

        status.Should().Be(200, "a layered or 4K export routinely passes the 12 MiB mask limit");
    }

    [Fact]
    public async Task UploadUrl_is_501_without_a_presign_service_and_404_for_an_unknown_document()
    {
        Harness h = new() { Presign = false };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Assets().CreateUploadUrl(
            projectId, doc.Id, new EditImageUploadUrlRequest("mask", "image/png", 10), CancellationToken.None));
        status.Should().Be(501);
        body.GetProperty("code").GetString().Should().Be("DIRECT_UPLOAD_DISABLED");

        (int missing, _) = Harness.Read(await h.Assets().CreateUploadUrl(
            projectId, Guid.NewGuid(), new EditImageUploadUrlRequest("mask", "image/png", 10), CancellationToken.None));
        missing.Should().Be(404);
    }

    // =============================================================================================
    // exports
    // =============================================================================================

    [Fact]
    public async Task Export_moves_the_upload_into_thumbnails_registers_the_file_and_lists_it()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);

        (int status, JsonElement body) = Harness.Read(await h.Assets().CreateExport(
            projectId, doc.Id,
            new CreateEditImageExportRequest(key, 1280, 720, "cover", SetAsProjectThumbnail: true), CancellationToken.None));

        status.Should().Be(201);
        body.GetProperty("projectThumbnailApplied").GetBoolean().Should().BeFalse("the project model has no thumbnail field");
        JsonElement file = body.GetProperty("projectFile");
        file.GetProperty("category").GetString().Should().Be("userFiles");
        file.GetProperty("directoryPath").GetString().Should().Be("thumbnails");
        file.GetProperty("originalFileName").GetString().Should().Be("cover.png");
        file.GetProperty("mimeType").GetString().Should().Be("image/png");
        file.GetProperty("sizeBytes").GetInt64().Should().Be(Png.Length);
        string finalKey = file.GetProperty("storageKey").GetString()!;
        finalKey.Should().StartWith($"projects/{projectId}/userFiles/thumbnails/").And.EndWith(".png");
        h.Storage.Objects.Should().ContainKey(finalKey);
        h.Storage.Objects.Should().NotContainKey(key, "the upload key was promoted, not copied");

        JsonElement export = body.GetProperty("export");
        export.GetProperty("width").GetInt32().Should().Be(1280);
        export.GetProperty("height").GetInt32().Should().Be(720);
        export.GetProperty("revision").GetInt32().Should().Be(doc.Revision);
        export.GetProperty("fileUrl").GetString().Should().Be($"/api/v1/projects/{projectId}/files/{file.GetProperty("id").GetGuid()}/download");

        (int listStatus, JsonElement list) = Harness.Read(await h.Assets().ListExports(projectId, doc.Id, CancellationToken.None));
        listStatus.Should().Be(200);
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("projectFileId").GetGuid().Should().Be(file.GetProperty("id").GetGuid());

        using InferenceApiDbContext db = h.NewDb();
        ProjectFile row = await db.ProjectFiles.SingleAsync(f => f.StorageKey == finalKey);
        row.SummaryStatus.Should().Be(SummaryStatus.Done, "a rendered PNG is not queued for the text summarizer");
        row.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
    }

    [Fact]
    public async Task A_key_issued_by_upload_url_is_accepted_by_export_end_to_end()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (_, JsonElement ticket) = Harness.Read(await h.Assets().CreateUploadUrl(
            projectId, doc.Id, new EditImageUploadUrlRequest("export", "image/png", Png.Length), CancellationToken.None));
        string key = ticket.GetProperty("key").GetString()!;
        h.Storage.Objects[key] = Png; // the browser's PUT

        (int status, _) = Harness.Read(await h.Assets().CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(key, 1280, 720, "thumb"), CancellationToken.None));

        status.Should().Be(201);
    }

    [Fact]
    public async Task Export_keeps_a_png_extension_the_caller_already_gave()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);

        (_, JsonElement body) = Harness.Read(await h.Assets().CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(key, 10, 10, "Final.PNG"), CancellationToken.None));

        body.GetProperty("projectFile").GetProperty("originalFileName").GetString().Should().Be("Final.PNG");
    }

    [Theory]
    [InlineData(".psd", "image/vnd.adobe.photoshop", "cover", "cover.psd")]
    [InlineData(".ora", "image/openraster", "cover.ora", "cover.ora")]
    [InlineData(".jpg", "image/jpeg", "cover.jpeg", "cover.jpeg")]
    [InlineData(".jpg", "image/jpeg", "cover.png", "cover.png.jpg")]
    [InlineData(".webp", "image/webp", "cover", "cover.webp")]
    [InlineData(".avif", "image/avif", "Cover.AVIF", "Cover.AVIF")]
    public async Task Export_records_the_type_and_extension_the_key_was_issued_for(
        string extension, string mime, string requestedName, string expectedName)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png, extension);

        (int status, JsonElement body) = Harness.Read(await h.Assets().CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(key, 1920, 1080, requestedName), CancellationToken.None));

        status.Should().Be(201);
        JsonElement file = body.GetProperty("projectFile");
        file.GetProperty("mimeType").GetString().Should().Be(mime, "the key's format decides the type, not the file name");
        file.GetProperty("originalFileName").GetString().Should().Be(expectedName);
        file.GetProperty("directoryPath").GetString().Should().Be("thumbnails");
        h.Storage.Objects.Should().ContainKey(file.GetProperty("storageKey").GetString()!);
    }

    [Fact]
    public async Task Export_accepts_a_layered_file_above_the_old_12_MiB_limit()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png, ".psd");
        h.Storage.SizeOverrides[key] = 64L * 1024 * 1024;

        (int status, JsonElement body) = Harness.Read(await h.Assets().CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(key, 3840, 2160, "big"), CancellationToken.None));

        status.Should().Be(201);
        body.GetProperty("projectFile").GetProperty("sizeBytes").GetInt64().Should().Be(64L * 1024 * 1024);
    }

    [Fact]
    public async Task Export_refuses_a_key_with_an_extension_no_export_format_has()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        foreach (string extension in new[] { ".gif", ".exe", ".PSD", ".jpeg", "" })
        {
            string key = await h.UploadAsync(projectId, doc.Id, "export", Png, extension);
            (int status, JsonElement body) = Harness.Read(await h.Assets().CreateExport(
                projectId, doc.Id, new CreateEditImageExportRequest(key, 10, 10, "x"), CancellationToken.None));

            status.Should().Be(400, $"extension '{extension}'");
            body.GetProperty("code").GetString().Should().Be("EXPORT_KEY_INVALID");
            h.Storage.Objects.Should().ContainKey(key, "a refused key is not touched");
        }
    }

    [Fact]
    public async Task A_mask_key_must_be_a_png()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid sourceId = doc.Layers[0].Id;
        string maskKey = await h.UploadAsync(projectId, doc.Id, "mask", Png, ".psd");

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("erase", sourceId, maskKey), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("MASK_REQUIRED");
    }

    [Fact]
    public async Task Export_refuses_a_key_that_was_not_issued_for_this_document_and_touches_no_storage()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        (Guid otherProject, EditImageDocumentDto otherDoc) = await h.SeedDocumentAsync();
        string foreignKey = await h.UploadAsync(otherProject, otherDoc.Id, "export", Png);
        string maskKey = await h.UploadAsync(projectId, doc.Id, "mask", Png);

        foreach (string? bad in new[]
                 {
                     foreignKey, maskKey, null, "", $"projects/{projectId}/userFiles/photo.png",
                     $"projects/{projectId}/agentFiles/image-editor/{doc.Id}/export-../../userFiles/x.png",
                     $"projects/{projectId}/agentFiles/image-editor/{doc.Id}/export-notaguid.png"
                 })
        {
            (int status, JsonElement body) = Harness.Read(await h.Assets().CreateExport(
                projectId, doc.Id, new CreateEditImageExportRequest(bad, 10, 10, "x"), CancellationToken.None));

            status.Should().Be(400, $"key '{bad}'");
            body.GetProperty("code").GetString().Should().Be("EXPORT_KEY_INVALID");
        }

        h.Storage.Objects.Should().ContainKey(foreignKey, "a foreign key must not be moved or deleted");
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageExports.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Export_is_409_when_nothing_was_uploaded_and_413_and_deletes_when_the_object_is_too_big()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        string missing = $"projects/{projectId}/agentFiles/image-editor/{doc.Id}/export-{Guid.NewGuid()}.png";
        (int status, JsonElement body) = Harness.Read(await h.Assets().CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(missing, 10, 10, "x"), CancellationToken.None));
        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("UPLOAD_OBJECT_MISSING");

        string big = await h.UploadAsync(projectId, doc.Id, "export", Png);
        h.Storage.SizeOverrides[big] = 200L * 1024 * 1024 + 1; // what the HEAD reports, without allocating it
        (int bigStatus, _) = Harness.Read(await h.Assets().CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(big, 10, 10, "x"), CancellationToken.None));
        bigStatus.Should().Be(413);
        h.Storage.Objects.Should().NotContainKey(big, "an oversized upload is deleted, never recorded");

        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageExports.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Export_takes_the_organization_storage_lock_once_before_the_quota_check_and_commits_it()
    {
        List<string> order = [];
        Mock<IPlanGateService> gate = new();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("quota"))
            .ReturnsAsync((PlanGateViolation?)null);
        Mock<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> tx = new();
        tx.Setup(t => t.DisposeAsync()).Returns(ValueTask.CompletedTask);
        tx.Setup(t => t.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask).Callback(() => order.Add("commit"));
        Mock<ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock> storageLock = new();
        storageLock.Setup(g => g.AcquireAsync(It.IsAny<InferenceApiDbContext>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("lock"))
            .ReturnsAsync(() => new ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageGuard(new SemaphoreSlim(0, 1), tx.Object));
        Harness h = new() { PlanGate = gate.Object };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);

        (int status, _) = Harness.Read(await h.Assets(storageLock: storageLock.Object).CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(key, 10, 10, "x"), CancellationToken.None));

        status.Should().Be(201);
        storageLock.Verify(g => g.AcquireAsync(It.IsAny<InferenceApiDbContext>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        tx.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        order.Should().Equal("lock", "quota", "commit");
    }

    [Fact]
    public async Task Export_over_the_storage_quota_is_refused_and_the_upload_deleted()
    {
        Mock<IPlanGateService> gate = new();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413));
        Harness h = new() { PlanGate = gate.Object };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);

        (int status, JsonElement body) = Harness.Read(await h.Assets().CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(key, 10, 10, "x"), CancellationToken.None));

        status.Should().Be(413);
        body.GetProperty("code").GetString().Should().Be("STORAGE_QUOTA_EXCEEDED");
        h.Storage.Objects.Should().NotContainKey(key);
    }

    // ---- upload-replay-fix: the export is moved off the presigned key, then judged ------------------

    /// <summary>Objects left under the keys an export touches (the upload and the moved copy), not the seeded source photo.</summary>
    private static IEnumerable<string> ExportObjects(Harness h) =>
        h.Storage.Objects.Keys.Where(k => !k.EndsWith("/photo.png", StringComparison.Ordinal));

    private static async Task<(int Status, JsonElement Body)> ExportAsync(Harness h, Guid projectId, Guid docId, string key,
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration? moderation = null) =>
        Harness.Read(await h.Assets(publishModeration: moderation).CreateExport(
            projectId, docId, new CreateEditImageExportRequest(key, 10, 10, "x"), CancellationToken.None));

    [Fact]
    public async Task Export_records_the_row_at_the_moved_final_key_and_the_upload_key_is_gone()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);

        (int status, JsonElement body) = await ExportAsync(h, projectId, doc.Id, key);

        status.Should().Be(201);
        (string from, string finalKey) = h.Storage.ConditionalMoves.Should().ContainSingle().Which;
        from.Should().Be(key);
        finalKey.Should().NotBe(key).And.StartWith($"projects/{projectId}/userFiles/thumbnails/");
        body.GetProperty("projectFile").GetProperty("storageKey").GetString().Should().Be(finalKey);
        h.Storage.Objects.Should().ContainKey(finalKey).And.NotContainKey(key);
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectFiles.SingleAsync(f => f.StorageKey == finalKey)).SizeBytes.Should().Be(Png.Length);
    }

    [Fact]
    public async Task Export_whose_upload_was_replaced_between_the_HEAD_and_the_move_is_refused_and_both_keys_are_gone()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);
        bool swapped = false;
        h.Storage.AfterHead = k =>
        {
            if (swapped || k != key) return;
            swapped = true;
            h.Storage.Objects[key] = [.. Png]; // a re-PUT through the live URL: same bytes length, new ETag
        };

        (int status, JsonElement body) = await ExportAsync(h, projectId, doc.Id, key);

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("UPLOAD_CHANGED");
        h.Storage.ConditionalMoves.Should().BeEmpty();
        ExportObjects(h).Should().BeEmpty("neither the upload key nor a copy may remain");
        h.Storage.DeletedKeys.Should().Contain(key);
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageExports.CountAsync()).Should().Be(0);
        (await db.ProjectFiles.CountAsync(f => f.StorageKey.Contains("/thumbnails/"))).Should().Be(0);
    }

    [Theory]
    [InlineData("no-etag")]
    [InlineData("no-conditional-copy")]
    public async Task Export_on_a_store_that_cannot_pin_or_condition_the_copy_is_refused(string mode)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);
        if (mode == "no-etag") h.Storage.OmitETag = true; else h.Storage.CanConditionCopy = false;

        (int status, JsonElement body) = await ExportAsync(h, projectId, doc.Id, key);

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("UPLOAD_CHANGED");
        ExportObjects(h).Should().BeEmpty();
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageExports.CountAsync()).Should().Be(0);
        (await db.ProjectFiles.CountAsync(f => f.StorageKey.Contains("/thumbnails/"))).Should().Be(0);
    }

    [Fact]
    public async Task Export_size_is_judged_on_the_moved_copy_and_an_oversized_copy_is_deleted()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);
        h.Storage.AfterHead = k =>
        {
            // The staged HEAD was fine; the bytes the copy holds are over the ceiling.
            if (k == key && !h.Storage.SizeOverrides.ContainsKey(key)) h.Storage.SizeOverrides[key] = 200L * 1024 * 1024 + 1;
        };

        (int status, JsonElement body) = await ExportAsync(h, projectId, doc.Id, key);

        status.Should().Be(413);
        body.GetProperty("code").GetString().Should().Be("FILE_TOO_LARGE");
        h.Storage.ConditionalMoves.Should().ContainSingle("the copy is what was measured");
        ExportObjects(h).Should().BeEmpty("the moved copy is deleted with the refusal");
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectFiles.CountAsync(f => f.StorageKey.Contains("/thumbnails/"))).Should().Be(0);
    }

    [Fact]
    public async Task Export_whose_move_copied_and_then_threw_leaves_no_copy_and_no_row()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);
        InvalidOperationException failure = new("the source delete failed after the copy landed");
        h.Storage.MoveCopiesThenThrows = failure;

        Func<Task> act = () => h.Assets().CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(key, 10, 10, "x"), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        h.Storage.Objects.Keys.Should().NotContain(k => k.Contains("/thumbnails/"), "the copy that landed is deleted");
        h.Storage.DeletedKeys.Should().Contain(k => k.Contains("/thumbnails/"));
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageExports.CountAsync()).Should().Be(0);
        (await db.ProjectFiles.CountAsync(f => f.StorageKey.Contains("/thumbnails/"))).Should().Be(0);
    }

    [Fact]
    public async Task Export_quota_refusal_deletes_the_moved_copy_and_records_nothing()
    {
        Mock<IPlanGateService> gate = new();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413));
        Harness h = new() { PlanGate = gate.Object };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);

        (int status, _) = await ExportAsync(h, projectId, doc.Id, key);

        status.Should().Be(413);
        string moved = h.Storage.ConditionalMoves.Single().To;
        h.Storage.DeletedKeys.Should().Contain(moved);
        ExportObjects(h).Should().BeEmpty();
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageExports.CountAsync()).Should().Be(0);
        (await db.ProjectFiles.CountAsync(f => f.StorageKey == moved)).Should().Be(0);
    }

    [Fact]
    public async Task Export_moderation_judges_the_moved_copy_never_the_upload_key_and_a_block_deletes_the_copy()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);
        Mock<ReelBolt.Shared.Moderation.IContentModerationGate> gate = new();
        gate.Setup(g => g.ModerateAsync(It.IsAny<ReelBolt.Shared.Moderation.ModerationInput>(), It.IsAny<ReelBolt.Shared.Moderation.ModerationAttribution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReelBolt.Shared.Moderation.ModerationDecision(
                ReelBolt.Shared.Moderation.ModerationAction.Block, Available: true, ReviewQueued: false, "sexual", "blocked", "hash"));
        Mock<ReelBolt.Shared.Moderation.IModerationMediaSampler> sampler = new();
        sampler.Setup(x => x.SampleAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReelBolt.Shared.Moderation.ModerationMediaSample(
                [new byte[] { 9, 9 }], null, ReelBolt.Shared.Moderation.ModerationContentKind.Image, null));
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration moderation = new(
            gate.Object, h.Storage, sampler.Object,
            new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object, h.NewDb(),
            Microsoft.Extensions.Options.Options.Create(new ReelBolt.Shared.Moderation.ContentModerationOptions { Enabled = true }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Moderation.PublishModeration>.Instance,
            csamOptions: Microsoft.Extensions.Options.Options.Create(new ReelBolt.Inference.Api.Services.Csam.CsamOptions
            {
                Arachnid = new ReelBolt.Inference.Api.Services.Csam.ArachnidOptions { Enabled = false } // the seeded photo is still Pending
            }));

        h.Storage.Downloads.Clear(); // seeding the document read its source photo
        (int status, JsonElement body) = await ExportAsync(h, projectId, doc.Id, key, moderation);

        status.Should().Be(422, body.ToString());
        body.GetProperty("code").GetString().Should().Be("CONTENT_BLOCKED");
        string moved = h.Storage.ConditionalMoves.Single().To;
        h.Storage.Downloads.Should().NotBeEmpty().And.OnlyContain(k => k == moved, "moderation reads the moved copy, never the upload key");
        ExportObjects(h).Should().BeEmpty("a blocked export's copy is deleted");
        using InferenceApiDbContext db = h.NewDb();
        (await db.EditImageExports.CountAsync()).Should().Be(0);
        (await db.ProjectFiles.CountAsync(f => f.StorageKey == moved)).Should().Be(0);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(16385, 10)]
    public async Task Export_validates_the_dimensions(int width, int height)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string key = await h.UploadAsync(projectId, doc.Id, "export", Png);

        (int status, _) = Harness.Read(await h.Assets().CreateExport(
            projectId, doc.Id, new CreateEditImageExportRequest(key, width, height, "x"), CancellationToken.None));

        status.Should().Be(400);
        h.Storage.Objects.Should().ContainKey(key);
    }

    // =============================================================================================
    // operations
    // =============================================================================================

    [Fact]
    public async Task Erase_inpaints_with_the_uploaded_mask_and_inserts_a_Generated_layer_above_the_source()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentWithUnknownPhotoAsync();
        EditImageLayerDto top = await h.AddTextLayerAsync(projectId, doc.Id, "title");
        Guid source = doc.Layers[0].Id;
        await h.Documents().UpdateLayer(projectId, doc.Id, source,
            new UpdateEditImageLayerRequest(Transform: new EditImageLayerTransformDto(30, 40, 2, 10, true, false), Opacity: 0.8, BlendMode: "multiply"),
            CancellationToken.None);
        byte[] maskBytes = [9, 9, 9, 9, 9];
        string maskKey = await h.UploadAsync(projectId, doc.Id, "mask", maskBytes);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("erase", source, maskKey), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("operation").GetString().Should().Be("erase");
        body.GetProperty("provider").GetProperty("kind").GetString().Should().Be("BlackForestLabs");
        body.GetProperty("provider").GetProperty("model").GetString().Should().Be("flux-pro-1.0-fill");
        body.GetProperty("usage").GetProperty("unit").GetString().Should().Be("images");
        body.GetProperty("usage").GetProperty("quantity").GetInt32().Should().Be(1);

        ImageInpaintRequest sent = h.Image.Calls.Should().ContainSingle().Subject.Request.Should().BeOfType<ImageInpaintRequest>().Subject;
        sent.MaskBytes.Should().Equal(maskBytes);
        sent.MaskContentType.Should().Be("image/png");
        sent.Prompt.Should().Be("background", "erase carries no instruction; vendors that need a prompt get the neutral one");
        sent.Model.Should().Be("flux-pro-1.0-fill");
        sent.SourceImageUrl.Should().StartWith("https://get.test/projects/").And.Contain("/userFiles/");

        JsonElement layer = body.GetProperty("layers")[0];
        layer.GetProperty("kind").GetString().Should().Be("Generated");
        layer.GetProperty("zOrder").GetInt32().Should().Be(2, "directly above the layer it was made from");
        layer.GetProperty("role").GetString().Should().Be("background", "inherits the source layer's role");
        layer.GetProperty("opacity").GetDouble().Should().Be(0.8);
        layer.GetProperty("blendMode").GetString().Should().Be("multiply");
        layer.GetProperty("transform").GetProperty("x").GetDouble().Should().Be(30);
        layer.GetProperty("transform").GetProperty("scale").GetDouble().Should().Be(2);
        layer.GetProperty("transform").GetProperty("flipX").GetBoolean().Should().BeTrue();
        JsonElement provenance = layer.GetProperty("provenance");
        provenance.GetProperty("operation").GetString().Should().Be("erase");
        provenance.GetProperty("providerKind").GetString().Should().Be("BlackForestLabs");
        provenance.GetProperty("model").GetString().Should().Be("flux-pro-1.0-fill");
        provenance.GetProperty("vendorRequestId").GetString().Should().Be("vendor-req-1");
        provenance.GetProperty("parentLayerId").GetGuid().Should().Be(source);
        body.GetProperty("revision").GetInt32().Should().Be(doc.Revision + 3, "text layer + layer patch + operation");

        using InferenceApiDbContext db = h.NewDb();
        List<EditImageLayer> stack = await db.EditImageLayers.Where(l => l.DocumentId == doc.Id).OrderBy(l => l.ZOrder).ToListAsync();
        stack.Select(l => l.ZOrder).Should().Equal(1, 2, 3);
        stack.Select(l => l.Kind).Should().Equal(EditImageLayerKind.Source, EditImageLayerKind.Generated, EditImageLayerKind.Text);
        stack[2].Id.Should().Be(top.Id);
        h.Storage.Objects.Should().ContainKey(stack[1].StorageKey);
    }

    [Fact]
    public async Task Fill_needs_a_prompt_and_forwards_it_with_the_mask()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string maskKey = await h.UploadAsync(projectId, doc.Id, "mask", [1, 2, 3]);

        (int noPrompt, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("fill", doc.Layers[0].Id, maskKey), CancellationToken.None));
        noPrompt.Should().Be(400);
        h.Image.Calls.Should().BeEmpty();

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("Fill", doc.Layers[0].Id, maskKey, Prompt: "a red door"),
            CancellationToken.None));
        status.Should().Be(200);
        h.Image.Calls.Single().Request.Should().BeOfType<ImageInpaintRequest>().Which.Prompt.Should().Be("a red door");
    }

    [Fact]
    public async Task Erase_and_fill_without_a_usable_mask_are_MASK_REQUIRED()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        (Guid otherProject, EditImageDocumentDto otherDoc) = await h.SeedDocumentAsync();
        string foreign = await h.UploadAsync(otherProject, otherDoc.Id, "mask", [1]);
        string exportKey = await h.UploadAsync(projectId, doc.Id, "export", [1]);
        string neverUploaded = $"projects/{projectId}/agentFiles/image-editor/{doc.Id}/mask-{Guid.NewGuid()}.png";
        string empty = await h.UploadAsync(projectId, doc.Id, "mask", []);

        foreach (string? mask in new[] { null, "", foreign, exportKey, neverUploaded, empty })
        {
            (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
                projectId, doc.Id, new EditImageOperationRequest("erase", doc.Layers[0].Id, mask), CancellationToken.None));

            status.Should().Be(400, $"mask '{mask}'");
            body.GetProperty("code").GetString().Should().Be("MASK_REQUIRED");
        }

        h.Image.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_output_follows_a_layer_the_person_moved_while_the_vendor_worked()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid source = doc.Layers[0].Id;
        h.Image.Handler = async (_, _) =>
        {
            await h.Documents().UpdateLayer(projectId, doc.Id, source,
                new UpdateEditImageLayerRequest(Transform: new EditImageLayerTransformDto(300, 200, 3)), CancellationToken.None);
            return new GeneratedImageSet([new GeneratedImage(Png, "image/png") { Width = 100, Height = 50 }], "vendor-req-1");
        };

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", source, Prompt: "tint"), CancellationToken.None));

        status.Should().Be(200);
        JsonElement layer = body.GetProperty("layers")[0];
        layer.GetProperty("transform").GetProperty("x").GetDouble().Should().Be(300, "the output follows the moved layer");
        layer.GetProperty("visible").GetBoolean().Should().BeTrue();
        body.TryGetProperty("needsReview", out JsonElement review).Should().BeTrue();
        review.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_output_for_a_layer_deleted_meanwhile_is_kept_hidden_flagged_for_review_and_charged_once()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        EditImageLayerDto top = await h.AddTextLayerAsync(projectId, doc.Id, "title");
        Guid source = doc.Layers[0].Id;
        h.Image.Handler = async (_, _) =>
        {
            await h.Documents().DeleteLayer(projectId, doc.Id, source, CancellationToken.None);
            return new GeneratedImageSet([new GeneratedImage(Png, "image/png") { Width = 100, Height = 50 }], "vendor-req-1");
        };

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", source, Prompt: "tint"), CancellationToken.None));

        status.Should().Be(200);
        JsonElement layer = body.GetProperty("layers")[0];
        layer.GetProperty("visible").GetBoolean().Should().BeFalse();
        JsonElement review = body.GetProperty("needsReview");
        review.GetProperty("reason").GetString().Should().Be("layerGone");
        review.GetProperty("layerId").GetGuid().Should().Be(layer.GetProperty("id").GetGuid());

        using InferenceApiDbContext db = h.NewDb();
        List<EditImageLayer> stack = await db.EditImageLayers.Where(l => l.DocumentId == doc.Id).OrderBy(l => l.ZOrder).ToListAsync();
        stack.Should().HaveCount(2);
        stack.Single(l => l.Kind == EditImageLayerKind.Generated).Visible.Should().BeFalse();
        stack.Select(l => l.ZOrder).Should().Equal(1, 2);
        charges.Holds.Should().ContainSingle();
        charges.Settled.Should().ContainSingle();
        charges.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task Instruct_uses_EditAsync()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id,
            new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "make the sky pink", Label: "Pink sky",
                Params: Params(("seed", 7), ("aspect", "16:9"))),
            CancellationToken.None));

        status.Should().Be(200);
        ImageEditRequest sent = h.Image.Calls.Single().Request.Should().BeOfType<ImageEditRequest>().Subject;
        sent.Prompt.Should().Be("make the sky pink");
        sent.Seed.Should().Be(7);
        sent.AspectRatio.Should().Be("16:9");
        body.GetProperty("layers")[0].GetProperty("label").GetString().Should().Be("Pink sky");
    }

    [Fact]
    public async Task Generate_places_a_backdrop_at_the_very_back_and_shifts_the_rest_up()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        await h.Documents().UpdateDocument(projectId, doc.Id, new UpdateEditImageDocumentRequest(CanvasWidth: 1280, CanvasHeight: 720), CancellationToken.None);
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([Img(640, 640)], "gen-1"));

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id,
            new EditImageOperationRequest("generate", Prompt: "a neon city", Params: Params(("width", 1280), ("height", 720))),
            CancellationToken.None));

        status.Should().Be(200);
        ReelBolt.Shared.Inference.ImageGenerationRequest sent =
            h.Image.Calls.Single().Request.Should().BeOfType<ReelBolt.Shared.Inference.ImageGenerationRequest>().Subject;
        sent.Prompt.Should().Be("a neon city");
        sent.Width.Should().Be(1280);
        sent.Height.Should().Be(720);
        sent.Count.Should().Be(1);

        JsonElement layer = body.GetProperty("layers")[0];
        layer.GetProperty("zOrder").GetInt32().Should().Be(1);
        layer.GetProperty("role").GetString().Should().Be("background");
        layer.GetProperty("kind").GetString().Should().Be("Generated");
        // a 640x640 output covering a 1280x720 canvas: scale 2, centred vertically
        layer.GetProperty("transform").GetProperty("scale").GetDouble().Should().Be(2);
        layer.GetProperty("transform").GetProperty("y").GetDouble().Should().Be((720 - 1280) / 2.0);
        layer.GetProperty("provenance").GetProperty("parentLayerId").ValueKind.Should().Be(JsonValueKind.Null);

        using InferenceApiDbContext db = h.NewDb();
        EditImageLayer old = await db.EditImageLayers.SingleAsync(l => l.Id == doc.Layers[0].Id);
        old.ZOrder.Should().Be(2);
    }

    [Fact]
    public async Task Generate_resolves_the_ImageGeneration_capability_and_needs_a_prompt()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int noPrompt, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate"), CancellationToken.None));
        noPrompt.Should().Be(400);

        h.GenerationProvider = null;
        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "x"), CancellationToken.None));
        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("IMAGE_GENERATION_PROVIDER_UNAVAILABLE");
    }

    [Fact]
    public async Task Expand_plans_the_geometry_and_moves_the_layer_so_the_pixels_stay_put()
    {
        Harness h = new() { EditProvider = Harness.FalEdit() };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, OpaquePng(200, 100), "image/png",
            200, 100, transformJson: "{\"x\":100,\"y\":200,\"scale\":2}");
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([Img(250, 140)], "ex-1"));

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id,
            new EditImageOperationRequest("expand", layer, Prompt: "more sky",
                Params: Params(("left", 50), ("top", 30), ("right", 0), ("bottom", 10), ("seed", 3), ("mode", "fast"))),
            CancellationToken.None));

        status.Should().Be(200);
        ImageTransformRequest sent = h.Image.Calls.Single().Request.Should().BeOfType<ImageTransformRequest>().Subject;
        sent.Operation.Should().Be(ImageOperation.Expand);
        sent.Prompt.Should().Be("more sky");
        sent.Seed.Should().Be(3);
        sent.Model.Should().BeEmpty("a FalAi row sends an empty model so the client applies its per-operation default");
        sent.Expand.Should().Be(new ImageExpandGeometry(250, 140, 50, 30, 200, 100));
        sent.Params!.Keys.Should().BeEquivalentTo(["mode"], "the editor's own margin keys became the geometry; the rest is forwarded");
        sent.ImageUrl.Should().EndWith(".png", "the original fits, so the original is sent");

        JsonElement transform = body.GetProperty("layers")[0].GetProperty("transform");
        transform.GetProperty("x").GetDouble().Should().Be(100 - (50 * 2));
        transform.GetProperty("y").GetDouble().Should().Be(200 - (30 * 2));
        transform.GetProperty("scale").GetDouble().Should().Be(2);
        body.GetProperty("provider").GetProperty("kind").GetString().Should().Be("FalAi");
        body.GetProperty("notice").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_expand_the_vendor_delivered_smaller_still_lands_on_the_original_pixels()
    {
        Harness h = new() { EditProvider = Harness.FalEdit() };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, OpaquePng(200, 100), "image/png",
            200, 100, transformJson: "{\"x\":100,\"y\":200,\"scale\":2}");
        // planned 250x140, delivered at half size
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([Img(125, 70)], "ex-2"));

        (_, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("expand", layer, Params: Params(("left", 50), ("top", 30), ("bottom", 10))),
            CancellationToken.None));

        JsonElement transform = body.GetProperty("layers")[0].GetProperty("transform");
        transform.GetProperty("scale").GetDouble().Should().Be(4, "each delivered pixel covers two of the original's");
        transform.GetProperty("x").GetDouble().Should().Be(0);
        transform.GetProperty("y").GetDouble().Should().Be(140);
    }

    [Fact]
    public async Task Expand_to_a_shape_reads_the_size_of_a_photo_whose_row_has_none()
    {
        Harness h = new() { EditProvider = Harness.FalEdit() };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentWithUnknownPhotoAsync();
        // the document's original: a Source layer with no stored size, whose bytes are a 5931x3954 JPEG header
        h.Storage.Objects[(await h.NewDb().EditImageLayers.SingleAsync(l => l.Id == doc.Layers[0].Id)).StorageKey] = JpegHeader(5931, 3954);
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([Img(2812, 4999)], "ex-3"));

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("expand", doc.Layers[0].Id, Prompt: "a sunset sky", Params: Params(("aspect", "9:16"))),
            CancellationToken.None));

        status.Should().Be(200);
        ImageExpandGeometry g = h.Image.Calls.Single().Request.Should().BeOfType<ImageTransformRequest>().Subject.Expand!;
        ((long)g.CanvasWidth * g.CanvasHeight).Should().BeLessThan(25_000_000, "Bria's canvas area must be under 5000x5000");
        ((double)g.CanvasWidth / g.CanvasHeight).Should().BeApproximately(9.0 / 16, 0.001);
        g.ImageX.Should().Be(0);
        g.Top.Should().BeCloseTo(g.Bottom, 1);
        body.GetProperty("notice").ValueKind.Should().Be(JsonValueKind.Null, "Bria takes the original: it is told the size to draw it");
    }

    [Fact]
    public async Task Expand_on_a_margins_endpoint_asks_for_the_displayed_size_copy_then_sends_it_with_a_notice()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.JobProviders[InferenceProviderCapability.ImageExpand] = Harness.FalEdit() with
        {
            Capability = InferenceProviderCapability.ImageExpand,
            ModelName = "fal-ai/flux-2-pro/outpaint"
        };
        // a 5931x3954 photo shown at a fifth of its size: 1186x791 on the canvas
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Image, JpegHeader(5931, 3954), "image/jpeg",
            null, null, transformJson: "{\"x\":0,\"y\":300,\"scale\":0.2}");
        EditImageOperationRequest request = new("expand", layer, Params: Params(("aspect", "9:16")));

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, request, CancellationToken.None));

        status.Should().Be(422, "the 23 MP original is over FLUX.2's 4 MP input");
        body.GetProperty("code").GetString().Should().Be("SOURCE_TOO_LARGE");
        body.GetProperty("width").GetInt32().Should().Be(1186);
        body.GetProperty("height").GetInt32().Should().Be(791);
        body.GetProperty("source").GetString().Should().Be("displayed");
        h.Image.Calls.Should().BeEmpty("nothing is sent before the copy exists");
        await h.AssertNoNewLayerAsync(await h.CurrentDocumentAsync(projectId, doc.Id));

        string copyKey = await h.UploadAsync(projectId, doc.Id, "source", OpaquePng(1186, 791));
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([Img(1186, 2108)], "ex-4"));
        (int ran, JsonElement result) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, request with { SourceKey = copyKey }, CancellationToken.None));

        ran.Should().Be(200);
        ImageTransformRequest sent = h.Image.Calls.Single().Request.Should().BeOfType<ImageTransformRequest>().Subject;
        sent.Model.Should().Be("fal-ai/flux-2-pro/outpaint");
        sent.ImageUrl.Should().EndWith(copyKey, "the copy goes to the vendor, not the original");
        sent.Expand.Should().Be(new ImageExpandGeometry(1186, 2108, 0, 658, 1186, 791));
        result.GetProperty("notice").GetString().Should().Be(
            "This picture was larger than the AI service accepts, so it used the picture at the size it appears in your design (1186\u00d7791).");

        // the result covers the original exactly where it was: 5931 px at scale 0.2 = 1186.2 canvas px wide
        JsonElement transform = result.GetProperty("layers")[0].GetProperty("transform");
        double scale = transform.GetProperty("scale").GetDouble();
        (1186 * scale).Should().BeApproximately(5931 * 0.2, 1e-6);
        transform.GetProperty("x").GetDouble().Should().BeApproximately(0, 1e-9);
        transform.GetProperty("y").GetDouble().Should().BeApproximately(300 - (658 * scale), 1e-9);
    }

    [Fact]
    public async Task Expand_refuses_a_sourceKey_that_is_not_an_issued_source_copy_of_this_document()
    {
        Harness h = new() { EditProvider = Harness.FalEdit() };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, OpaquePng(200, 100), "image/png", 200, 100);
        string mask = await h.UploadAsync(projectId, doc.Id, "mask", OpaquePng(20, 10));

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("expand", layer, Params: Params(("aspect", "1:1")), SourceKey: mask),
            CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("OPERATION_INVALID");
        h.Image.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"aspect\":\"9:16\",\"top\":10}", "not both")]
    [InlineData("{\"aspect\":\"wide\"}", "params.aspect")]
    [InlineData("{}", "Choose a shape")]
    [InlineData("{\"top\":-4}", "params.top")]
    public async Task Expand_needs_one_clear_target(string paramsJson, string expected)
    {
        Harness h = new() { EditProvider = Harness.FalEdit() };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, OpaquePng(200, 100), "image/png", 200, 100);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id,
            new EditImageOperationRequest("expand", layer, Params: JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(paramsJson)),
            CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("message").GetString().Should().Contain(expected);
        h.Image.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_capabilities_route_tells_the_editor_what_the_expand_service_takes()
    {
        Harness h = new() { EditProvider = Harness.FalEdit() };
        Guid projectId = await h.SeedProjectAsync();
        h.RealDescribe = true;

        (int status, JsonElement body) = Harness.Read(await h.Operations().GetOperationCapabilities(
            projectId, "expand", null, null, null, null, CancellationToken.None));
        status.Should().Be(200);
        body.GetProperty("provider").GetProperty("model").GetString().Should().Be("fal-ai/bria/expand");
        body.GetProperty("takesPrompt").GetBoolean().Should().BeTrue();
        body.GetProperty("expand").GetProperty("geometry").GetString().Should().Be("canvas");
        body.GetProperty("expand").GetProperty("maxOutputPixels").GetInt64().Should().Be(25_000_000);

        h.JobProviders[InferenceProviderCapability.ImageExpand] = Harness.FalEdit() with
        {
            Capability = InferenceProviderCapability.ImageExpand,
            ModelName = "fal-ai/flux-2-pro/outpaint"
        };
        (_, JsonElement flux) = Harness.Read(await h.Operations().GetOperationCapabilities(
            projectId, "expand", null, null, null, null, CancellationToken.None));
        flux.GetProperty("provider").GetProperty("model").GetString().Should().Be("fal-ai/flux-2-pro/outpaint");
        flux.GetProperty("takesPrompt").GetBoolean().Should().BeFalse("the UI must not offer a prompt the endpoint refuses");
        flux.GetProperty("expand").GetProperty("geometry").GetString().Should().Be("margins");
        flux.GetProperty("expand").GetProperty("maxMarginPx").GetInt32().Should().Be(2048);
        flux.GetProperty("expand").GetProperty("maxInputPixels").GetInt64().Should().Be(4_000_000);

        h.Segmentation = null;
        (int none, JsonElement noneBody) = Harness.Read(await h.Operations().GetOperationCapabilities(
            projectId, "removeBackground", null, null, null, null, CancellationToken.None));
        none.Should().Be(409);
        noneBody.GetProperty("code").GetString().Should().Be("SEGMENTATION_PROVIDER_UNAVAILABLE");
    }

    [Theory]
    [InlineData("replaceBackground", ImageOperation.ReplaceBackground)]
    [InlineData("relight", ImageOperation.Relight)]
    [InlineData("upscale", ImageOperation.Upscale)]
    public async Task The_transform_operations_dispatch_to_TransformAsync(string name, ImageOperation expected)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id,
            new EditImageOperationRequest(name, doc.Layers[0].Id, Prompt: "golden hour", Params: Params(("factor", 2))),
            CancellationToken.None));

        status.Should().Be(200);
        h.Image.Calls.Single().Request.Should().BeOfType<ImageTransformRequest>().Which.Operation.Should().Be(expected);
    }

    [Fact]
    public async Task Upscale_refuses_a_factor_of_3_with_a_plain_400_and_never_calls_the_vendor()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();
        // the real fal client does the refusal (before any send); the handler fails the test if a byte goes out
        h.Image.Handler = (request, ct) => new FalAiImageClient(new System.Net.Http.HttpClient(new NoSendHandler()), "k")
            .TransformAsync((ImageTransformRequest)request, ct);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", doc.Layers[0].Id, Params: Params(("factor", 3))),
            CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("OPERATION_INVALID");
        body.GetProperty("message").GetString().Should().Be("The upscale factor can be at most 2.");
    }

    [Fact]
    public async Task Upscale_keeps_the_layers_displayed_size_by_dividing_the_scale()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([Img(2000, 1000)], "up-1"));

        // A layer whose row records no size. Its pixels are measured from its own header, so the upscale is
        // allowed (an unmeasurable source is refused before anything is sent), but the width the editor
        // shows it at is unknown, so the documented factor is the fallback...
        Guid source = await h.SeedLayerAsync(
            projectId, doc.Id, EditImageLayerKind.Image, PngOfSize(1000, 500), "image/png", width: null, height: null);
        (_, JsonElement viaFactor) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", source, Params: Params(("factor", 2))),
            CancellationToken.None));
        viaFactor.GetProperty("layers")[0].GetProperty("transform").GetProperty("scale").GetDouble().Should().Be(0.5);
        viaFactor.GetProperty("layers")[0].GetProperty("width").GetInt32().Should().Be(2000);

        // ...and once the parent's width is known (a generated 1000px layer) the real ratio wins
        Guid generated = viaFactor.GetProperty("layers")[0].GetProperty("id").GetGuid();
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([Img(4000, 2000)], "up-2"));
        (_, JsonElement viaWidths) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", generated, Params: Params(("factor", 2))),
            CancellationToken.None));
        viaWidths.GetProperty("layers")[0].GetProperty("transform").GetProperty("scale").GetDouble().Should().Be(0.25);
    }

    [Theory]
    [InlineData(5041, 2160)]
    [InlineData(6000, 4000)]
    public async Task Upscale_refuses_a_picture_larger_than_the_priced_preset_before_anything_is_sent(int width, int height)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();
        h.JobProviders[InferenceProviderCapability.ImageUpscale] = Harness.FalEdit() with
        {
            Capability = InferenceProviderCapability.ImageUpscale,
            ModelName = "topaz/upscale/image/precision"
        };
        h.Storage.Objects[$"projects/{projectId}/userFiles/photo.png"] = PngHeaderOf(width, height);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", doc.Layers[0].Id, Params: Params(("factor", 2))),
            CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("OPERATION_INVALID");
        body.GetProperty("message").GetString()
            .Should().Be("This picture is too large to upscale. The largest picture that can be upscaled is 5040 × 2160.");
        h.Image.Calls.Should().BeEmpty("the refusal comes before any vendor call");
    }

    [Fact]
    public async Task Upscale_accepts_a_picture_at_the_priced_preset_of_5040_by_2160()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([Img(10080, 4320)], "up-max"));
        h.Storage.Objects[$"projects/{projectId}/userFiles/photo.png"] = PngHeaderOf(5040, 2160);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", doc.Layers[0].Id, Params: Params(("factor", 2))),
            CancellationToken.None));

        status.Should().Be(200);
        h.Image.Calls.Should().ContainSingle("a picture at the priced preset is sent exactly once");
    }

    private const string UnreadableUpscaleMessage =
        "This picture's size could not be read, so it cannot be upscaled.";

    private const string TooLargeUpscaleMessage =
        "This picture is too large to upscale. The largest picture that can be upscaled is 5040 × 2160.";

    [Fact]
    public async Task Upscale_refuses_a_picture_whose_size_cannot_be_established_before_anything_is_sent()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Image, [1, 2, 3], "image/png", null, null);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", layer, Params: Params(("factor", 2))),
            CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("OPERATION_INVALID");
        body.GetProperty("message").GetString().Should().Be(UnreadableUpscaleMessage);
        h.Image.Calls.Should().BeEmpty("an unknown size is refused before any vendor call");
    }

    [Theory]
    [InlineData(5041, 2160)]
    [InlineData(6000, 4000)]
    public async Task Upscale_refuses_an_unrecorded_picture_whose_header_is_larger_than_the_bound(int width, int height)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Image, PngHeaderOf(width, height), "image/png", null, null);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", layer, Params: Params(("factor", 2))),
            CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("message").GetString().Should().Be(TooLargeUpscaleMessage);
        h.Image.Calls.Should().BeEmpty("the refusal comes before any vendor call");
    }

    [Fact]
    public async Task Upscale_refuses_a_lying_recorded_size_when_the_header_declares_more()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Image, PngHeaderOf(6000, 4000), "image/png", 40, 20);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", layer, Params: Params(("factor", 2))),
            CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("message").GetString().Should().Be(TooLargeUpscaleMessage);
        h.Image.Calls.Should().BeEmpty("the larger of the recorded and header sizes wins");
    }

    [Fact]
    public async Task Upscale_refuses_a_large_recorded_size_even_when_the_header_is_small()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Image, PngHeaderOf(40, 20), "image/png", 6000, 4000);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", layer, Params: Params(("factor", 2))),
            CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("message").GetString().Should().Be(TooLargeUpscaleMessage);
        h.Image.Calls.Should().BeEmpty("the larger of the recorded and header sizes wins");
    }

    [Fact]
    public async Task Upscale_accepts_a_picture_whose_recorded_and_header_sizes_are_both_within_the_bound()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([Img(80, 40)], "up-small"));
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Image, PngHeaderOf(40, 20), "image/png", 40, 20);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", layer, Params: Params(("factor", 2))),
            CancellationToken.None));

        status.Should().Be(200);
        h.Image.Calls.Should().ContainSingle("a known size within the bound is sent exactly once");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 20)]
    [InlineData(40, 0)]
    public async Task Upscale_refuses_a_recorded_size_with_zero_or_negative_dimensions(int width, int height)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.EditProvider = Harness.FalEdit();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Image, PngHeaderOf(40, 20), "image/png", width, height);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", layer, Params: Params(("factor", 2))),
            CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("message").GetString().Should().Be(UnreadableUpscaleMessage);
        h.Image.Calls.Should().BeEmpty("a size that is not positive is not a size");
    }

    private static byte[] PngHeaderOf(int width, int height)
    {
        byte[] png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        png[24] = 8;
        png[25] = 6;
        return png;
    }

    [Fact]
    public async Task RemoveBackground_segments_with_the_matting_variant_and_produces_a_Cutout_with_a_matte()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("removeBackground", doc.Layers[0].Id), CancellationToken.None));

        status.Should().Be(200);
        SegmentationRequest sent = h.Analysis.Requests.Single();
        sent.Model.Should().Be("fal-ai/birefnet/v2");
        sent.Variant.Should().Be("Matting");
        sent.MatteToo.Should().BeTrue();
        sent.Prompt.Should().BeNull();

        JsonElement layer = body.GetProperty("layers")[0];
        layer.GetProperty("kind").GetString().Should().Be("Cutout");
        layer.GetProperty("role").GetString().Should().Be("subject");
        layer.GetProperty("matteUrl").GetString().Should().EndWith("/matte");
        layer.GetProperty("segmentationModel").GetString().Should().Be("fal-ai/birefnet/v2");
        layer.GetProperty("zOrder").GetInt32().Should().Be(2);
        body.GetProperty("provider").GetProperty("model").GetString().Should().Be("fal-ai/birefnet/v2");
    }

    [Fact]
    public async Task An_explicit_model_wins_and_a_non_fal_row_sends_its_own_model()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        await h.Operations().RunOperation(projectId, doc.Id,
            new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x", Model: "flux-kontext-pro"), CancellationToken.None);
        h.Image.Calls.Single().Request.Should().BeOfType<ImageEditRequest>().Which.Model.Should().Be("flux-kontext-pro");

        h.Image.Calls.Clear();
        await h.Operations().RunOperation(projectId, doc.Id,
            new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"), CancellationToken.None);
        h.Image.Calls.Single().Request.Should().BeOfType<ImageEditRequest>().Which.Model.Should().Be("flux-pro-1.0-fill",
            "a BlackForestLabs row sends the row's own model");
    }

    // ---- error vocabulary ----

    [Fact]
    public async Task A_client_that_throws_NotSupported_is_409_OPERATION_UNSUPPORTED_and_nothing_is_stored()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.JobProviders[InferenceProviderCapability.ImageExpand] =
            Harness.FalEdit() with { Capability = InferenceProviderCapability.ImageExpand };
        h.Image.Handler = (_, _) => throw new NotSupportedException("MiniMax has no outpainting endpoint");
        Guid sized = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, OpaquePng(40, 20), "image/png", 40, 20);
        doc = await h.CurrentDocumentAsync(projectId, doc.Id);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("expand", sized, Params: Params(("left", 10))),
            CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("OPERATION_UNSUPPORTED");
        body.GetProperty("message").GetString().Should().Contain("outpainting");
        await h.AssertNoNewLayerAsync(doc);
    }

    [Fact]
    public async Task A_vendor_that_outlasts_the_bound_is_424_OPERATION_TIMEOUT_and_nothing_is_stored()
    {
        Harness h = new() { OperationTimeout = TimeSpan.FromMilliseconds(60) };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Image.Handler = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new GeneratedImageSet([], null);
        };

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "slow"), CancellationToken.None));

        status.Should().Be(424, "never 504: Cloudflare replaces a 504's body with its own page");
        body.GetProperty("code").GetString().Should().Be("OPERATION_TIMEOUT");
        await h.AssertNothingStoredAsync(doc);
    }

    [Fact]
    public async Task Subjects_vendor_that_outlasts_the_bound_is_424_OPERATION_TIMEOUT_and_nothing_is_charged()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges, SegmentationTimeout = TimeSpan.FromMilliseconds(60) };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Analysis.OnSegment = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        };

        (int status, JsonElement body) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "slow"), CancellationToken.None));

        status.Should().Be(424);
        body.GetProperty("code").GetString().Should().Be("OPERATION_TIMEOUT");
        charges.Settled.Should().BeEmpty("a timed-out call the user never received is not charged");
        await h.AssertNothingStoredAsync(doc);
    }

    [Fact]
    public void The_default_bound_is_90_seconds()
    {
        EditImageOperationsController.DefaultOperationTimeout.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task A_caller_cancellation_is_not_reported_as_a_timeout()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        using CancellationTokenSource cts = new();
        h.Image.Handler = async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new GeneratedImageSet([], null);
        };

        Func<Task> run = async () => await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"), cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_vendor_failure_is_424_OPERATION_FAILED_and_an_argument_refusal_is_400()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        h.Image.Handler = (_, _) => throw new InvalidOperationException("vendor exploded");
        (int failed, JsonElement failBody) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"), CancellationToken.None));
        failed.Should().Be(424, "never 502: Cloudflare replaces a 502's body, and the reason would never reach the user");
        failBody.GetProperty("code").GetString().Should().Be("OPERATION_FAILED");
        failBody.GetProperty("message").GetString().Should().StartWith("The AI service could not").And.EndWith("It said: vendor exploded");

        h.Image.Handler = (_, _) => throw new ArgumentException("'fal-ai/x' documents no 'strength' field");
        (int refused, JsonElement refusedBody) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"), CancellationToken.None));
        refused.Should().Be(400);
        refusedBody.GetProperty("code").GetString().Should().Be("OPERATION_INVALID");

        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([], "empty"));
        (int empty, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"), CancellationToken.None));
        empty.Should().Be(424, "a 200 carrying no image is not a success");

        // a vendor echo of the presigned source URL loses its query, which is the credential
        h.Image.Handler = (_, _) => throw new InvalidOperationException(
            "fal.ai returned HTTP 422: {\"loc\":[\"body\",\"image_url\"],\"input\":\"https://bucket.test/a.jpg?X-Amz-Signature=secret\"}");
        (_, JsonElement echoed) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"), CancellationToken.None));
        echoed.GetProperty("message").GetString().Should().Contain("https://bucket.test/a.jpg?").And.NotContain("secret");

        await h.AssertNothingStoredAsync(doc);
    }

    [Fact]
    public async Task A_text_layer_has_no_bitmap_for_any_pixel_operation()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        EditImageLayerDto text = await h.AddTextLayerAsync(projectId, doc.Id, "title");
        string maskKey = await h.UploadAsync(projectId, doc.Id, "mask", [1]);

        foreach (string op in new[] { "erase", "fill", "expand", "replaceBackground", "relight", "upscale", "instruct", "removeBackground" })
        {
            (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
                projectId, doc.Id,
                new EditImageOperationRequest(op, text.Id, maskKey, Prompt: "x"),
                CancellationToken.None));

            status.Should().Be(409, op);
            body.GetProperty("code").GetString().Should().Be("LAYER_HAS_NO_BITMAP", op);
        }

        h.Image.Calls.Should().BeEmpty();
        h.Analysis.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_unavailable_is_a_409_with_the_capability_named_in_the_code()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        h.EditProvider = null;
        (int edit, JsonElement editBody) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"), CancellationToken.None));
        edit.Should().Be(409);
        editBody.GetProperty("code").GetString().Should().Be("IMAGE_EDIT_PROVIDER_UNAVAILABLE");

        h.Segmentation = null;
        (int seg, JsonElement segBody) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("removeBackground", doc.Layers[0].Id), CancellationToken.None));
        seg.Should().Be(409);
        segBody.GetProperty("code").GetString().Should().Be("SEGMENTATION_PROVIDER_UNAVAILABLE");
    }

    [Fact]
    public async Task No_presign_service_is_501_for_an_operation_that_needs_to_show_the_vendor_an_image()
    {
        Harness h = new() { Presign = false };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"), CancellationToken.None));

        status.Should().Be(501);
    }

    [Theory]
    [InlineData("sharpen", false)]
    [InlineData("", false)]
    [InlineData("instruct", true)]
    public async Task Request_validation_comes_before_any_provider_call(string operation, bool missingLayer)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id,
            new EditImageOperationRequest(operation, missingLayer ? null : doc.Layers[0].Id, Prompt: "x"),
            CancellationToken.None));

        status.Should().Be(400);
        h.Image.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_layer_or_document_is_404()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int layer, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", Guid.NewGuid(), Prompt: "x"), CancellationToken.None));
        (int document, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, Guid.NewGuid(), new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"), CancellationToken.None));

        layer.Should().Be(404);
        document.Should().Be(404);
    }

    [Fact]
    public async Task A_concurrent_edit_during_the_vendor_wait_does_not_lose_the_paid_result()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Image.Handler = async (_, _) =>
        {
            // another tab renames the document while the vendor is working
            using InferenceApiDbContext other = h.NewDb();
            EditImageDocument row = await other.EditImageDocuments.SingleAsync(d => d.Id == doc.Id);
            row.Name = "renamed";
            row.Revision += 1;
            await other.SaveChangesAsync();
            return new GeneratedImageSet([Img(10, 10)], "late");
        };

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("revision").GetInt32().Should().Be(doc.Revision + 2);
        using InferenceApiDbContext db = h.NewDb();
        EditImageDocument stored = await db.EditImageDocuments.SingleAsync(d => d.Id == doc.Id);
        stored.Name.Should().Be("renamed");
        (await db.EditImageLayers.CountAsync(l => l.DocumentId == doc.Id)).Should().Be(2);
    }

    [Fact]
    public async Task An_unrecognised_param_is_refused_by_the_operations_with_typed_fields_only()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id,
            new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x", Params: Params(("strength", 0.5))),
            CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("OPERATION_INVALID");
        body.GetProperty("message").GetString().Should().Contain("strength");
        h.Image.Calls.Should().BeEmpty();
    }

    // =============================================================================================
    // tenancy
    // =============================================================================================

    [Fact]
    public async Task Another_organizations_user_gets_404_from_every_new_route()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid stranger = Guid.NewGuid();
        EditImageDocumentsController documents = h.Documents(stranger);
        EditImageAssetsController assets = h.Assets(stranger);
        EditImageOperationsController operations = h.Operations(stranger);
        Guid layer = doc.Layers[0].Id;

        Harness.Read(await documents.UpdateDocument(projectId, doc.Id, new UpdateEditImageDocumentRequest("x"), CancellationToken.None)).Item1.Should().Be(404);
        Harness.Read(await documents.CreateTextLayer(projectId, doc.Id, new CreateTextLayerRequest(new TextContentDto("x")), CancellationToken.None)).Item1.Should().Be(404);
        Harness.Read(await documents.CreateImageLayerFromFile(projectId, doc.Id, new CreateImageLayerFromFileRequest(Guid.NewGuid()), CancellationToken.None)).Item1.Should().Be(404);
        Harness.Read(await documents.SegmentSubject(projectId, doc.Id, new SegmentSubjectRequest(Prompt: "x"), CancellationToken.None)).Item1.Should().Be(404);
        Harness.Read(await documents.UpdateLayer(projectId, doc.Id, layer, new UpdateEditImageLayerRequest(Visible: false), CancellationToken.None)).Item1.Should().Be(404);
        Harness.Read(await assets.CreateUploadUrl(projectId, doc.Id, new EditImageUploadUrlRequest("mask", "image/png", 10), CancellationToken.None)).Item1.Should().Be(404);
        Harness.Read(await assets.CreateExport(projectId, doc.Id, new CreateEditImageExportRequest("k", 1, 1, "x"), CancellationToken.None)).Item1.Should().Be(404);
        Harness.Read(await assets.ListExports(projectId, doc.Id, CancellationToken.None)).Item1.Should().Be(404);
        Harness.Read(await operations.RunOperation(projectId, doc.Id, new EditImageOperationRequest("instruct", layer, Prompt: "x"), CancellationToken.None)).Item1.Should().Be(404);

        h.Image.Calls.Should().BeEmpty();
        h.Analysis.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_document_in_another_project_is_404_even_for_the_projects_own_member()
    {
        Harness h = new();
        (Guid projectA, EditImageDocumentDto docA) = await h.SeedDocumentAsync();
        Guid projectB = await h.SeedProjectAsync(h.OwnerId);

        Harness.Read(await h.Operations().RunOperation(projectB, docA.Id,
            new EditImageOperationRequest("instruct", docA.Layers[0].Id, Prompt: "x"), CancellationToken.None)).Item1.Should().Be(404);
        Harness.Read(await h.Assets().ListExports(projectB, docA.Id, CancellationToken.None)).Item1.Should().Be(404);
        Harness.Read(await h.Documents().UpdateDocument(projectB, docA.Id, new UpdateEditImageDocumentRequest("x"), CancellationToken.None)).Item1.Should().Be(404);
        projectA.Should().NotBe(projectB);
    }

    // =============================================================================================
    // Per-job capabilities (each tool resolves its own provider)
    // =============================================================================================

    [Theory]
    [InlineData("erase", InferenceProviderCapability.ImageErase)]
    [InlineData("fill", InferenceProviderCapability.ImageFill)]
    [InlineData("expand", InferenceProviderCapability.ImageExpand)]
    [InlineData("replaceBackground", InferenceProviderCapability.ImageBackgroundReplace)]
    [InlineData("relight", InferenceProviderCapability.ImageRelight)]
    [InlineData("upscale", InferenceProviderCapability.ImageUpscale)]
    [InlineData("instruct", InferenceProviderCapability.ImageEdit)]
    [InlineData("generate", InferenceProviderCapability.ImageGeneration)]
    public async Task Each_tool_resolves_its_own_capability_first(string operation, InferenceProviderCapability expected)
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.JobProviders[expected] = Harness.FalEdit() with { Capability = expected, Name = "own-row" };
        string maskKey = await h.UploadAsync(projectId, doc.Id, "mask", [1, 2, 3]);
        Guid sized = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, OpaquePng(40, 20), "image/png", 40, 20);

        EditImageOperationRequest request = operation switch
        {
            "erase" => new(operation, doc.Layers[0].Id, maskKey),
            "fill" => new(operation, doc.Layers[0].Id, maskKey, Prompt: "a hat"),
            "expand" => new(operation, sized, Params: Params(("left", 10))),
            "replaceBackground" or "instruct" => new(operation, doc.Layers[0].Id, Prompt: "a beach"),
            "generate" => new(operation, Prompt: "a beach"),
            _ => new(operation, doc.Layers[0].Id, Params: Params(("seed", 1)))
        };
        if (operation == "relight") request = request with { Prompt = "warm" };
        if (operation == "generate") h.GenerationProvider = h.JobProviders[expected];

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, request, CancellationToken.None));

        status.Should().Be(200);
        h.ResolvedCapabilities.Should().Equal([expected], "the job had a row of its own, so nothing else was asked for");
        body.GetProperty("provider").GetProperty("capability").GetString().Should().Be(expected.ToString());
    }

    [Fact]
    public async Task A_job_row_sends_its_own_model_when_it_is_a_catalogued_endpoint_for_that_job()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.JobProviders[InferenceProviderCapability.ImageUpscale] = Harness.FalEdit() with
        {
            Capability = InferenceProviderCapability.ImageUpscale,
            ModelName = "topaz/upscale/image/precision"
        };

        await h.RunOperationAsync(projectId, doc.Id, new EditImageOperationRequest("upscale", doc.Layers[0].Id));

        h.Image.Calls.Single().Request.Should().BeOfType<ImageTransformRequest>()
            .Which.Model.Should().Be("topaz/upscale/image/precision");
    }

    [Fact]
    public async Task A_legacy_fal_ImageEdit_row_still_serves_every_job_with_the_catalogue_default_for_that_job()
    {
        Harness h = new() { EditProvider = Harness.FalEdit() with { ModelName = "google/nano-banana-2.1/edit" } };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        string maskKey = await h.UploadAsync(projectId, doc.Id, "mask", [1, 2, 3]);

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("erase", doc.Layers[0].Id, maskKey), CancellationToken.None));

        status.Should().Be(200);
        h.ResolvedCapabilities.Should().Equal(InferenceProviderCapability.ImageErase, InferenceProviderCapability.ImageEdit);
        h.Image.Calls.Single().Request.Should().BeOfType<ImageInpaintRequest>()
            .Which.Model.Should().BeEmpty("the row's model is an INSTRUCTION-edit endpoint; the erase job gets the catalogue's erase default");
        body.GetProperty("provider").GetProperty("capability").GetString().Should().Be("ImageEdit");

        h.Image.Calls.Clear();
        await h.RunOperationAsync(projectId, doc.Id, new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "x"));
        h.Image.Calls.Single().Request.Should().BeOfType<ImageEditRequest>()
            .Which.Model.Should().Be("google/nano-banana-2.1/edit", "for its own job the row's model is used");
    }

    [Fact]
    public async Task A_job_is_never_sent_by_fallback_to_a_kind_that_cannot_do_it()
    {
        // The harness's ImageEdit row is Black Forest Labs, which documents a mask but no upscaler.
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", doc.Layers[0].Id), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("IMAGE_EDIT_PROVIDER_UNAVAILABLE");
        body.GetProperty("capability").GetString().Should().Be("ImageUpscale");
        body.GetProperty("message").GetString().Should().Contain("upscaling pictures").And.Contain("ImageUpscale");
        h.Image.Calls.Should().BeEmpty();
        await h.AssertNothingStoredAsync(doc);
    }

    [Fact]
    public async Task The_layer_records_the_model_that_actually_ran_not_the_rows_model()
    {
        Harness h = new() { GenerationProvider = Harness.FalEdit() with { Capability = InferenceProviderCapability.ImageGeneration, ModelName = "not-in-the-catalogue" } };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([Img(64, 36)], "r") { ResolvedModel = "ideogram/v4.5" });

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a beach"), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("provider").GetProperty("model").GetString().Should().Be("ideogram/v4.5");
        body.GetProperty("layers")[0].GetProperty("provenance").GetProperty("model").GetString().Should().Be("ideogram/v4.5");
    }

    // =============================================================================================
    // Transparency survives an AI edit of a cut-out
    // =============================================================================================

    /// <summary>A w x h RGBA picture whose left half is fully transparent and right half opaque red.</summary>
    private static byte[] HalfTransparentPng(int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                rgba[i] = 200;
                rgba[i + 3] = x < width / 2 ? (byte)0 : (byte)255;
            }
        }

        return PngAlpha.EncodeRgba(width, height, rgba);
    }

    /// <summary>An opaque picture, as most models answer: every pixel black except the right half grey.</summary>
    private static byte[] OpaquePng(int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 3] = 255;
        }

        return PngAlpha.EncodeRgba(width, height, rgba);
    }

    private static byte AlphaAt((int Width, int Height, byte[] Rgba) image, int x, int y) =>
        image.Rgba[(((y * image.Width) + x) * 4) + 3];

    [Fact]
    public async Task An_instruction_edit_of_a_cutout_keeps_its_transparent_background()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid cutout = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Cutout, HalfTransparentPng(40, 20), "image/png", 40, 20);
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet(
            [new GeneratedImage(OpaquePng(80, 40), "image/png") { Width = 80, Height = 40 }], "r"));

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", cutout, Prompt: "add a wing"), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("notice").ValueKind.Should().Be(JsonValueKind.Null);
        JsonElement layer = body.GetProperty("layers")[0];
        layer.GetProperty("kind").GetString().Should().Be("Generated");

        using InferenceApiDbContext db = h.NewDb();
        EditImageLayer stored = await db.EditImageLayers.SingleAsync(l => l.Id == layer.GetProperty("id").GetGuid());
        var result = PngAlpha.DecodeRgba(h.Storage.Objects[stored.StorageKey])!.Value;
        result.Width.Should().Be(80);
        AlphaAt(result, 5, 20).Should().Be(0, "the cut-out was transparent there, and the vendor's black must not show");
        AlphaAt(result, 30, 20).Should().Be(0);
        AlphaAt(result, 50, 20).Should().Be(255, "the subject stays opaque");
        AlphaAt(result, 75, 5).Should().Be(255);
        stored.MimeType.Should().Be("image/png");
    }

    [Fact]
    public async Task A_cutout_seen_through_its_matte_keeps_the_mattes_transparency()
    {
        // An opaque bitmap whose transparency comes from its matte (how the browser draws a cut-out).
        Harness h = new() { EditProvider = Harness.FalEdit() };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid cutout = await h.SeedLayerAsync(
            projectId, doc.Id, EditImageLayerKind.Cutout, OpaquePng(40, 20), "image/png", 40, 20, matte: HalfTransparentPng(40, 20));
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([new GeneratedImage(OpaquePng(40, 20), "image/png")], "r"));

        Guid made = await h.RunOperationAsync(projectId, doc.Id, new EditImageOperationRequest("relight", cutout, Prompt: "warm"));

        using InferenceApiDbContext db = h.NewDb();
        var result = PngAlpha.DecodeRgba(h.Storage.Objects[(await db.EditImageLayers.SingleAsync(l => l.Id == made)).StorageKey])!.Value;
        AlphaAt(result, 2, 10).Should().Be(0);
        AlphaAt(result, 38, 10).Should().Be(255);
    }

    [Fact]
    public async Task A_result_that_is_not_a_png_keeps_the_transparency_as_a_matte_and_becomes_a_cutout()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid cutout = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Cutout, HalfTransparentPng(40, 20), "image/png", 40, 20);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet(
            [new GeneratedImage(jpeg, "image/jpeg") { Width = 80, Height = 40 }], "r"));

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", cutout, Prompt: "x"), CancellationToken.None));

        status.Should().Be(200);
        JsonElement layer = body.GetProperty("layers")[0];
        layer.GetProperty("kind").GetString().Should().Be("Cutout", "the browser only draws a layer through its matte when it is a cut-out");
        layer.GetProperty("matteUrl").GetString().Should().EndWith("/matte");

        using InferenceApiDbContext db = h.NewDb();
        EditImageLayer stored = await db.EditImageLayers.SingleAsync(l => l.Id == layer.GetProperty("id").GetGuid());
        h.Storage.Objects[stored.StorageKey].Should().Equal(jpeg, "the vendor's picture is stored as it came");
        var matte = PngAlpha.DecodeRgba(h.Storage.Objects[stored.MatteStorageKey!])!.Value;
        matte.Width.Should().Be(80, "the matte is made at the result's size, which the browser stretches it over");
        AlphaAt(matte, 2, 20).Should().Be(0);
        AlphaAt(matte, 70, 20).Should().Be(255);
    }

    [Fact]
    public async Task A_fill_keeps_what_was_painted_into_the_transparent_area()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid cutout = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Cutout, HalfTransparentPng(40, 20), "image/png", 40, 20);

        // White (paint here) over the top-left quarter, which is transparent on the cut-out.
        byte[] maskRgba = new byte[40 * 20 * 4];
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                int i = ((y * 40) + x) * 4;
                byte v = x < 20 && y < 10 ? (byte)255 : (byte)0;
                maskRgba[i] = maskRgba[i + 1] = maskRgba[i + 2] = v;
                maskRgba[i + 3] = 255;
            }
        }

        string maskKey = await h.UploadAsync(projectId, doc.Id, "mask", PngAlpha.EncodeRgba(40, 20, maskRgba));
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([new GeneratedImage(OpaquePng(40, 20), "image/png")], "r"));

        Guid made = await h.RunOperationAsync(projectId, doc.Id, new EditImageOperationRequest("fill", cutout, maskKey, Prompt: "a hat"));

        using InferenceApiDbContext db = h.NewDb();
        var result = PngAlpha.DecodeRgba(h.Storage.Objects[(await db.EditImageLayers.SingleAsync(l => l.Id == made)).StorageKey])!.Value;
        AlphaAt(result, 5, 3).Should().Be(255, "the hat was painted there");
        AlphaAt(result, 5, 17).Should().Be(0, "still transparent outside the painted area");
        AlphaAt(result, 35, 17).Should().Be(255);
    }

    [Fact]
    public async Task A_result_of_another_shape_is_kept_as_it_came_with_a_plain_notice()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid cutout = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Cutout, HalfTransparentPng(40, 20), "image/png", 40, 20);
        byte[] square = OpaquePng(30, 30);
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([new GeneratedImage(square, "image/png")], "r"));

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("instruct", cutout, Prompt: "x"), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("notice").GetString().Should().Contain("different shape").And.Contain("transparent");
        using InferenceApiDbContext db = h.NewDb();
        EditImageLayer stored = await db.EditImageLayers.SingleAsync(l => l.Id == body.GetProperty("layers")[0].GetProperty("id").GetGuid());
        h.Storage.Objects[stored.StorageKey].Should().Equal(square);
    }

    [Theory]
    [InlineData("replaceBackground")]
    [InlineData("expand")]
    public async Task The_tools_that_fill_the_background_are_not_given_the_cutouts_alpha(string operation)
    {
        Harness h = new() { EditProvider = Harness.FalEdit() };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid cutout = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Cutout, HalfTransparentPng(40, 20), "image/png", 40, 20);
        byte[] answer = OpaquePng(40, 20);
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([new GeneratedImage(answer, "image/png")], "r"));

        EditImageOperationRequest request = operation == "expand"
            ? new(operation, cutout, Params: Params(("left", 4)))
            : new(operation, cutout, Prompt: "a beach");
        Guid made = await h.RunOperationAsync(projectId, doc.Id, request);

        using InferenceApiDbContext db = h.NewDb();
        h.Storage.Objects[(await db.EditImageLayers.SingleAsync(l => l.Id == made)).StorageKey].Should().Equal(answer);
    }

    [Fact]
    public async Task An_opaque_source_leaves_the_vendors_picture_untouched()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, OpaquePng(40, 20), "image/png", 40, 20);
        byte[] answer = OpaquePng(40, 20);
        h.Image.Handler = (_, _) => Task.FromResult(new GeneratedImageSet([new GeneratedImage(answer, "image/png")], "r"));

        Guid made = await h.RunOperationAsync(projectId, doc.Id, new EditImageOperationRequest("instruct", layer, Prompt: "x"));

        using InferenceApiDbContext db = h.NewDb();
        h.Storage.Objects[(await db.EditImageLayers.SingleAsync(l => l.Id == made)).StorageKey].Should().Equal(answer);
    }

    // =============================================================================================
    // Cutting a subject out of any picture layer
    // =============================================================================================

    [Fact]
    public async Task Subjects_cut_from_a_generated_layer_send_that_layers_picture_and_land_above_it()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid generated = await h.SeedLayerAsync(
            projectId, doc.Id, EditImageLayerKind.Generated, OpaquePng(200, 100), "image/png", 200, 100,
            transformJson: """{"x":10,"y":20,"scale":0.5,"rotation":0,"flipX":false,"flipY":false}""");
        await h.AddTextLayerAsync(projectId, doc.Id, "title");

        (int status, JsonElement body) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id,
            new SegmentSubjectRequest(PointPrompts: [new PointPromptDto(150, 50, 1)], LayerId: generated),
            CancellationToken.None));

        status.Should().Be(200);
        SegmentationRequest sent = h.Analysis.Requests.Single();
        using InferenceApiDbContext db = h.NewDb();
        EditImageLayer parent = await db.EditImageLayers.SingleAsync(l => l.Id == generated);
        sent.SourceImageUrl.Should().Be("https://get.test/" + parent.StorageKey, "the vendor is shown the layer that was picked, not the original");
        sent.PointPrompts.Should().ContainSingle().Which.Should().Be(new PointPrompt(150, 50, 1));

        JsonElement cut = body[0];
        cut.GetProperty("kind").GetString().Should().Be("Cutout");
        cut.GetProperty("zOrder").GetInt32().Should().Be(parent.ZOrder + 1, "directly above the layer it was cut from");
        JsonElement transform = cut.GetProperty("transform");
        transform.GetProperty("x").GetDouble().Should().Be(10);
        transform.GetProperty("y").GetDouble().Should().Be(20);
        transform.GetProperty("scale").GetDouble().Should().Be(1.0, "the vendor answered at 100 px wide for a 200 px layer drawn at 0.5");

        List<EditImageLayer> stack = await db.EditImageLayers.Where(l => l.DocumentId == doc.Id).OrderBy(l => l.ZOrder).ToListAsync();
        stack.Select(l => l.ZOrder).Should().Equal(Enumerable.Range(1, stack.Count), "the stack is renumbered without gaps");
        stack.Last().Kind.Should().Be(EditImageLayerKind.Text, "the text layer stays on top");
    }

    [Fact]
    public async Task Subjects_still_work_after_the_original_layer_was_deleted_when_a_layer_is_named()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid generated = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Generated, OpaquePng(100, 50), "image/png", 100, 50);
        await h.Documents().DeleteLayer(projectId, doc.Id, doc.Layers[0].Id, CancellationToken.None);

        (int status, _) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the car", LayerId: generated), CancellationToken.None));

        status.Should().Be(200);
    }

    [Fact]
    public async Task Subjects_from_a_text_layer_or_a_document_with_no_original_are_409_in_plain_words()
    {
        Harness h = new();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        EditImageLayerDto text = await h.AddTextLayerAsync(projectId, doc.Id, "title");

        (int textStatus, JsonElement textBody) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "x", LayerId: text.Id), CancellationToken.None));
        textStatus.Should().Be(409);
        textBody.GetProperty("code").GetString().Should().Be("LAYER_HAS_NO_BITMAP");
        textBody.GetProperty("message").GetString().Should().Contain("Select a picture layer");

        using (InferenceApiDbContext db = h.NewDb())
        {
            db.ProjectFiles.RemoveRange(db.ProjectFiles.Where(f => f.ProjectId == projectId));
            await db.SaveChangesAsync();
        }

        (int noSource, JsonElement noSourceBody) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "x"), CancellationToken.None));
        noSource.Should().Be(409);
        noSourceBody.GetProperty("code").GetString().Should().Be("NO_SOURCE_IMAGE");
        h.Analysis.Requests.Should().BeEmpty();
    }

    // =============================================================================================
    // Harness
    // =============================================================================================

    private static GeneratedImage Img(int width, int height) => new(Png, "image/png") { Width = width, Height = height };

    /// <summary>The first bytes of a baseline JPEG of the given size: SOI, a JFIF APP0, then SOF0. Enough for a header read.</summary>
    private static byte[] JpegHeader(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
        1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        0xFF, 0xD9
    ];

    private static Dictionary<string, JsonElement> Params(params (string Key, object Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    // =============================================================================================
    // Credits (decision 9): picture AI ops and subject cut-outs are charged per call
    // =============================================================================================

    [Fact]
    public async Task An_operation_the_workspace_cannot_afford_answers_402_and_never_reaches_the_vendor()
    {
        Harness h = new() { Charges = new Billing.FakeNonWorkflowCharges { RefuseHolds = true, EstimateMillis = 4_000, AvailableMillis = 1_500 } };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a neon city"), CancellationToken.None));

        status.Should().Be(402);
        body.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        body.GetProperty("required").GetDouble().Should().Be(4);
        body.GetProperty("balance").GetDouble().Should().Be(1.5);
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        h.Image.Calls.Should().BeEmpty("a refused operation is never sent");
        await h.AssertNoNewLayerAsync(await h.CurrentDocumentAsync(projectId, doc.Id));
    }

    [Fact]
    public async Task An_operation_holds_one_priced_unit_of_its_capability_and_settles_what_was_metered()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a neon city"), CancellationToken.None));

        status.Should().Be(200);
        PriceRequest priced = charges.Estimates.Should().ContainSingle().Subject;
        (priced.ModelCapabilityId, priced.Kind, priced.Capability, priced.Unit, priced.BillingMode, priced.Quantity)
            .Should().Be((h.GenerationProvider!.ProviderId, InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ImageGeneration,
                UsageKind.ImageGenImages, UsageBillingMode.Platform, 1m));
        charges.Holds.Should().ContainSingle().Which.Should().Be((TestTenancy.OrgOf(h.OwnerId), UsageSource.Editor, 4_000L));
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(4_000,
            "the vendor answered but metered nothing, so the hold is charged, never a free release (T-013 rework item 4)");
        charges.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task An_operation_whose_platform_usage_could_not_be_priced_is_charged_its_hold_never_zero()
    {
        // the vendor call metered a platform unit the price table does not price (recorded at 0 credits,
        // flagged unpriced), so the settle charges the hold instead of the understated metered sum.
        var charges = new Billing.FakeNonWorkflowCharges { EstimateMillis = 4_000 };
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        UsageRecorder recorder = new(new EmptyPriceService(), new DiscardSink(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);
        h.Image.Handler = async (_, _) =>
        {
            await recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.ImageGenImages, 1, h.GenerationProvider!.ProviderId, InferenceProviderKind.OpenAICompatible, "img",
                UsageBillingMode.Platform));
            return new GeneratedImageSet([Img(640, 640)], "unpriced-1");
        };

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a neon city"), CancellationToken.None));

        status.Should().Be(200);
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(4_000);
    }

    // =============================================================================================
    // T-013 (saas-quality-followups): a per-megapixel picture op holds at its EXPECTED output size
    // =============================================================================================

    private const string FalFill = "fal-ai/flux-pro/v1/fill";
    private const string FalUpscale = "fal-ai/seedvr/upscale/image";

    /// <summary>A fal job row for <paramref name="capability"/> plus charges that price holds on the committed seed.</summary>
    private static (Harness H, Billing.FakeNonWorkflowCharges Charges, ModelPriceSnapshot Prices) SizedHarness(
        InferenceProviderCapability capability, string model, bool refuseHolds = false)
    {
        ResolvedImageProvider row = Harness.FalEdit() with { Capability = capability, ModelName = model };
        ModelPriceSnapshot prices = Metering.SeedPrices.Snapshot(row.ProviderId!.Value, InferenceProviderKind.FalAi, model,
            capability, UsageKind.ImageEditImages);
        Billing.FakeNonWorkflowCharges charges = new()
        {
            RefuseHolds = refuseHolds,
            AvailableMillis = 1_000_000,
            EstimateBy = r => Metering.SeedPrices.Price(prices, r).CreditsMillis
        };
        Harness h = new() { Charges = charges, EditProvider = Harness.FalEdit() };
        h.JobProviders[capability] = row;
        return (h, charges, prices);
    }

    /// <summary>What <paramref name="variant"/> of one unit costs on <paramref name="prices"/> (USD and credits).</summary>
    private static PriceQuote Quote(ModelPriceSnapshot prices, ResolvedImageProvider row, string model, string? variant) =>
        Metering.SeedPrices.Price(prices, new PriceRequest(row.ProviderId, row.ProviderId, InferenceProviderKind.FalAi,
            row.Capability, model, UsageKind.ImageEditImages, variant, UsageBillingMode.Platform, 1m));

    /// <summary>A vendor that delivers a picture of the given size, metered by the REAL image meter over the seed prices.</summary>
    private static void DeliverMetered(Harness h, ResolvedImageProvider row, ModelPriceSnapshot prices, int width, int height, string resolved) =>
        DeliverMetered(h, row, prices, PngHeaderOf(width, height), resolved);

    private static void DeliverMetered(Harness h, ResolvedImageProvider row, ModelPriceSnapshot prices, byte[] delivered, string resolved)
    {
        UsageRecorder recorder = new(new SnapshotPrices(prices), new DiscardSink(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);
        h.Image.Handler = (request, ct) =>
        {
            MeteringImageClient metered = new(new SizedFal(delivered, resolved), row, recorder);
            return request switch
            {
                ImageInpaintRequest inpaint => metered.InpaintAsync(inpaint, ct),
                ImageTransformRequest transform => metered.TransformAsync(transform, ct),
                ImageEditRequest edit => metered.EditAsync(edit, ct),
                ReelBolt.Shared.Inference.ImageGenerationRequest generate => metered.GenerateAsync(generate, ct),
                _ => throw new NotSupportedException()
            };
        };
    }

    private async Task<(Guid ProjectId, EditImageDocumentDto Doc, string MaskKey)> FillDocumentAsync(Harness h, byte[] photo)
    {
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Storage.Objects[$"projects/{projectId}/userFiles/photo.png"] = photo;
        return (projectId, doc, await h.UploadAsync(projectId, doc.Id, "mask", [1, 2, 3]));
    }

    private static EditImageOperationRequest Fill(EditImageDocumentDto doc, string maskKey) =>
        new("fill", doc.Layers[0].Id, maskKey, Prompt: "a hat");

    [Fact]
    public async Task A_10MP_fill_holds_and_settles_at_the_10MP_charge_not_capped()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(4000, 2496));
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, 4000, 2496, FalFill);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        PriceQuote tenMp = Quote(prices, row, FalFill, "10MP");
        tenMp.CostUsd.Should().Be(0.50m, "10 MP x $0.05 per megapixel");
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().Be("10MP", "the source is 4000x2496 = 9.98 MP");
        charges.Holds.Should().ContainSingle().Which.EstimateMillis.Should().Be(tenMp.CreditsMillis);
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(tenMp.CreditsMillis);
        tenMp.CreditsMillis.Should().BeGreaterThan(Quote(prices, row, FalFill, null).CreditsMillis, "more than today's 4 MP hold");
    }

    [Fact]
    public async Task A_2x_upscale_of_a_2MP_picture_holds_at_the_8MP_charge()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageUpscale, FalUpscale);
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Storage.Objects[$"projects/{projectId}/userFiles/photo.png"] = PngHeaderOf(1984, 992);
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageUpscale];
        DeliverMetered(h, row, prices, 3968, 1984, FalUpscale);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("upscale", doc.Layers[0].Id, Params: Params(("factor", 2))),
            CancellationToken.None));

        status.Should().Be(200);
        PriceQuote eightMp = Quote(prices, row, FalUpscale, "8MP");
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().Be("8MP", "1984x992 = 1.97 MP, x2 x2 = 7.87 MP of output");
        charges.Holds.Should().ContainSingle().Which.EstimateMillis.Should().Be(eightMp.CreditsMillis);
        eightMp.CostUsd.Should().Be(Metering.SeedPrices.Cost(InferenceProviderKind.FalAi, FalUpscale,
            InferenceProviderCapability.ImageUpscale, UsageKind.ImageEditImages, "4MP") * 2);
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(eightMp.CreditsMillis);
    }

    [Fact]
    public async Task A_larger_hold_the_workspace_cannot_afford_is_402_before_anything_is_sent()
    {
        (Harness h, _, _) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill, refuseHolds: true);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(4000, 2496));

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(402);
        body.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        h.Image.Calls.Should().BeEmpty();
    }

    // ---- F1: once the vendor answered, a size-priced job settles at what it metered, uncapped, and keeps its layer

    [Fact]
    public async Task An_output_larger_than_its_hold_settles_uncapped_at_the_metered_amount_and_the_layer_is_added()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(1280, 704));
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, 2560, 1344, FalFill);
        EditImageDocumentDto before = await h.CurrentDocumentAsync(projectId, doc.Id);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200, "the vendor answered and the user paid, so the picture is theirs");
        long held = charges.Holds.Should().ContainSingle().Subject.EstimateMillis;
        held.Should().Be(Quote(prices, row, FalFill, "1MP").CreditsMillis);
        long fourMp = Quote(prices, row, FalFill, "4MP").CreditsMillis;
        fourMp.Should().BeGreaterThan(EditImageOperationsController.SettleCeilingMillis(held, charges.Options), "past hold + headroom");
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(fourMp, "2560x1344 = 3.4 MP, within 4x the expected size");
        charges.UncappedSettles.Should().ContainSingle("the size-priced job settles uncapped");
        charges.Released.Should().BeEmpty();
        (await h.CurrentDocumentAsync(projectId, doc.Id)).Layers.Should().HaveCount(before.Layers.Count + 1);
    }

    // ---- F2 (rework): an unknown expected size with no documented maximum holds the variant-less (largest) row

    [Fact]
    public async Task A_fill_of_unknown_expected_size_holds_the_largest_row_and_settles_at_what_it_metered()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        Guid layer = await h.SeedLayerAsync(projectId, doc.Id, EditImageLayerKind.Image, [1, 2, 3], "image/png", null, null);
        string maskKey = await h.UploadAsync(projectId, doc.Id, "mask", [1, 2, 3]);
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, 1280, 704, FalFill);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("fill", layer, maskKey, Prompt: "a hat"), CancellationToken.None));

        status.Should().Be(200, "an unreadable source is not a reason to block the user");
        long largest = Quote(prices, row, FalFill, null).CreditsMillis;
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().BeNull();
        charges.Holds.Should().ContainSingle().Which.EstimateMillis.Should().Be(largest);
        charges.Settled.Should().ContainSingle().Which.ActualMillis
            .Should().Be(Quote(prices, row, FalFill, "1MP").CreditsMillis, "metering succeeded, so the metered 1 MP is the charge");
    }

    // ---- QA HIGH: a conservative hold never becomes the charge when metering succeeded

    [Fact]
    public async Task A_1000x1000_fill_holds_conservatively_and_settles_at_the_metered_1MP_row()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(1000, 1000));
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, 1000, 1000, FalFill);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        PriceQuote oneMp = Quote(prices, row, FalFill, "1MP");
        charges.Holds.Should().ContainSingle().Which.EstimateMillis.Should().Be(oneMp.CreditsMillis,
            "the hold rounds 1000 up to the 1024 grid: 1024 x 1024 = exactly 1 binary MP");
        oneMp.CostUsd.Should().Be(0.05m);
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(oneMp.CreditsMillis, "the picture measured 0.95 binary MP");
    }

    [Fact]
    public async Task A_1024x1024_fill_is_exactly_one_binary_megapixel_five_cents()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(1024, 1024));
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, 1024, 1024, FalFill);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        PriceQuote oneMp = Quote(prices, row, FalFill, "1MP");
        oneMp.CostUsd.Should().Be(0.05m, "1024 x 1024 = 1,048,576 px = 1 MP");
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().Be("1MP");
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(oneMp.CreditsMillis);
    }

    [Fact]
    public async Task A_1025x1024_fill_rounds_up_to_two_binary_megapixels_ten_cents()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(1025, 1024));
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, 1025, 1024, FalFill);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        PriceQuote twoMp = Quote(prices, row, FalFill, "2MP");
        twoMp.CostUsd.Should().Be(0.10m, "1,049,600 px = 1.001 binary MP, rounded up; 2 x $0.05 interpolated");
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(twoMp.CreditsMillis);
    }

    [Fact]
    public async Task A_2048x2048_fill_is_exactly_four_binary_megapixels_twenty_cents()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(2048, 2048));
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, 2048, 2048, FalFill);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        PriceQuote fourMp = Quote(prices, row, FalFill, "4MP");
        fourMp.CostUsd.Should().Be(0.20m);
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().Be("4MP");
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(fourMp.CreditsMillis);
    }

    [Fact]
    public async Task A_1920x1080_fill_settles_at_two_binary_megapixels_ten_cents_not_the_4MP_row()
    {
        // QA-PR1-1: 1920x1080 = 2,073,600 px = 1.98 binary MP, rounded up to 2 MP = $0.10.
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(1920, 1080));
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, 1920, 1080, FalFill);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        PriceQuote twoMp = Quote(prices, row, FalFill, "2MP");
        twoMp.CostUsd.Should().Be(0.10m);
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(twoMp.CreditsMillis);
        twoMp.CreditsMillis.Should().BeLessThan(Quote(prices, row, FalFill, "4MP").CreditsMillis);
    }

    // ---- 3 (rework): an implausible vendor size is clamped, not billed

    [Fact]
    public async Task An_implausible_vendor_size_settles_at_four_times_the_expected_size()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(1280, 704));
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, 20000, 20000, FalFill);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        charges.Settled.Should().ContainSingle().Which.ActualMillis
            .Should().Be(Quote(prices, row, FalFill, "4MP").CreditsMillis, "4 x 0.9 MP expected = 3.6 MP, not the 400 MP the vendor stated");
    }

    // ---- 4 (rework): a billed call the vendor answered is never released for free

    [Fact]
    public async Task A_size_priced_call_whose_meter_recorded_nothing_settles_at_its_hold()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, _) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(4000, 2496));

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        long held = charges.Holds.Should().ContainSingle().Subject.EstimateMillis;
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(held, "the fake vendor meters nothing; the hold is the charge");
        charges.Released.Should().BeEmpty();
    }

    // ---- 1 (rework): an outpaint holds its output AND its input megapixels

    [Fact]
    public async Task An_outpaint_holds_the_planned_canvas_plus_its_input()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, _) = SizedHarness(InferenceProviderCapability.ImageExpand, "fal-ai/flux-2-pro/outpaint");
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Storage.Objects[$"projects/{projectId}/userFiles/photo.png"] = PngHeaderOf(1000, 744);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id,
            new EditImageOperationRequest("expand", doc.Layers[0].Id, Params: Params(("right", 256))), CancellationToken.None));

        status.Should().Be(200);
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().Be("2MP", "1256x744 canvas = 1 MP of output + 0.74 MP of input = 1 MP");
    }

    // ---- 6 (rework): a target-mode upscale never holds below its source

    [Fact]
    public async Task A_target_upscale_smaller_than_its_source_holds_at_least_the_source_size()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, _) = SizedHarness(InferenceProviderCapability.ImageUpscale, FalUpscale);
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Storage.Objects[$"projects/{projectId}/userFiles/photo.png"] = PngHeaderOf(3840, 2144);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id,
            new EditImageOperationRequest("upscale", doc.Layers[0].Id,
                Params: Params(("upscale_mode", "target"), ("target_resolution", "1080p"))),
            CancellationToken.None));

        status.Should().Be(200);
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().Be("8MP", "the scale is at least 1: 3840x2144 = 7.85 binary MP, not 2 MP");
    }

    [Fact]
    public async Task A_jpeg_whose_frame_header_sits_past_2MiB_is_still_sized()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, _) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, JpegWithLateFrame(4000, 2496, 3 * 1024 * 1024));

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().Be("10MP");
    }

    [Fact]
    public async Task A_flux2_generation_with_no_requested_size_holds_at_its_documented_ceiling()
    {
        ResolvedImageProvider row = new(Guid.NewGuid(), "bfl", InferenceProviderKind.BlackForestLabs,
            InferenceProviderCapability.ImageGeneration, "https://api.bfl.ai", "flux-2-pro", "key", 60);
        ModelPriceSnapshot prices = Metering.SeedPrices.Snapshot(row.ProviderId!.Value, InferenceProviderKind.BlackForestLabs,
            "flux-2-pro", InferenceProviderCapability.ImageGeneration, UsageKind.ImageGenImages);
        Billing.FakeNonWorkflowCharges charges = new()
        {
            AvailableMillis = 1_000_000,
            EstimateBy = r => Metering.SeedPrices.Price(prices, r).CreditsMillis
        };
        Harness h = new() { Charges = charges, GenerationProvider = row };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a neon city"), CancellationToken.None));

        status.Should().Be(200);
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().Be("4MP", "\"up to 4MP (e.g., 2048x2048)\" = exactly 4 binary MP");
    }

    // ---- F3: an unmeasurable delivered picture is billed at no less than its hold

    [Fact]
    public async Task An_unmeasurable_delivered_picture_settles_at_no_less_than_its_hold()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(4000, 2496));
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, [1, 2, 3], FalFill);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        long tenMp = Quote(prices, row, FalFill, "10MP").CreditsMillis;
        tenMp.Should().BeGreaterThan(Quote(prices, row, FalFill, null).CreditsMillis, "the meter alone bills the variant-less row");
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(tenMp, "max(metered, the 10 MP hold)");
    }

    // ---- F4: expected sizes round up to the vendors' 32-pixel grid

    [Fact]
    public async Task A_1025x1000_fill_holds_at_the_1056x1024_size_the_vendor_may_deliver()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, _) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(1025, 1000));

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().Be("2MP",
            "1025 x 1000 is 0.98 binary MP, but its 32 px grid size 1056 x 1024 is 1.03, rounded up");
    }

    // ---- the reported overcharge: a 640x352 fill is a 1 MP job, never a 4 MP one

    [Fact]
    public async Task A_640x352_fill_is_billed_at_the_1MP_row()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices) = SizedHarness(InferenceProviderCapability.ImageFill, FalFill);
        (Guid projectId, EditImageDocumentDto doc, string maskKey) = await FillDocumentAsync(h, PngHeaderOf(640, 352));
        ResolvedImageProvider row = h.JobProviders[InferenceProviderCapability.ImageFill];
        DeliverMetered(h, row, prices, 640, 352, FalFill);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id, Fill(doc, maskKey), CancellationToken.None));

        status.Should().Be(200);
        PriceQuote oneMp = Quote(prices, row, FalFill, "1MP");
        oneMp.CostUsd.Should().Be(0.05m, "the 1 MP row is $0.05");
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(oneMp.CreditsMillis,
            "a 640x352 picture is billed at the 1 MP row, a quarter of the 4 MP row");
        oneMp.CreditsMillis.Should().BeLessThan(Quote(prices, row, FalFill, "4MP").CreditsMillis);
        charges.Released.Should().BeEmpty();
    }

    // ---- F5: qwen edit with reference pictures holds and bills output plus every reference per megapixel

    private const string Qwen = "fal-ai/qwen-image-edit-2511";

    private static (Harness H, Billing.FakeNonWorkflowCharges Charges, ModelPriceSnapshot Prices, ResolvedImageProvider Row) QwenHarness()
    {
        ResolvedImageProvider row = Harness.FalEdit() with { ModelName = Qwen };
        ModelPriceSnapshot prices = Metering.SeedPrices.Snapshot(row.ProviderId!.Value, InferenceProviderKind.FalAi, Qwen,
            InferenceProviderCapability.ImageEdit, UsageKind.ImageEditImages);
        Billing.FakeNonWorkflowCharges charges = new()
        {
            AvailableMillis = 1_000_000,
            EstimateBy = r => Metering.SeedPrices.Price(prices, r).CreditsMillis
        };
        return (new Harness { Charges = charges, EditProvider = row }, charges, prices, row);
    }

    [Fact]
    public async Task A_qwen_edit_with_two_3MP_references_holds_and_settles_at_7MP()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, ModelPriceSnapshot prices, ResolvedImageProvider row) = QwenHarness();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Storage.Objects[$"projects/{projectId}/userFiles/photo.png"] = PngHeaderOf(800, 576);
        Guid first = await h.SeedImageFileAsync(projectId, "ref-1.png");
        Guid second = await h.SeedImageFileAsync(projectId, "ref-2.png");
        h.Storage.Objects[$"projects/{projectId}/userFiles/ref-1.png"] = PngHeaderOf(2000, 1500);
        h.Storage.Objects[$"projects/{projectId}/userFiles/ref-2.png"] = PngHeaderOf(2000, 1500);
        DeliverMetered(h, row, prices, 800, 576, Qwen);

        (int status, _) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id,
            new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "swap the face", ReferenceProjectFileIds: [first, second]),
            CancellationToken.None));

        status.Should().Be(200);
        PriceQuote sevenMp = Quote(prices, row, Qwen, "7MP");
        sevenMp.CostUsd.Should().BeGreaterThan(Quote(prices, row, Qwen, null).CostUsd, "never the flat $0.12");
        charges.Estimates.Should().ContainSingle().Which.Variant.Should().Be("7MP", "1 MP output + 3 MP + 3 MP of references");
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(sevenMp.CreditsMillis);
    }

    [Fact]
    public async Task A_reference_picture_that_is_an_output_without_a_verdict_is_refused_before_it_is_presigned()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, _, _) = QwenHarness();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Storage.Objects[$"projects/{projectId}/userFiles/photo.png"] = PngHeaderOf(800, 576);
        string outputKey = $"projects/{projectId}/outputFiles/render.png";
        Guid reference = Guid.NewGuid();
        using (InferenceApiDbContext seed = h.NewDb())
        {
            seed.ProjectFiles.Add(new ProjectFile
            {
                Id = reference,
                ProjectId = projectId,
                OriginalFileName = "render.png",
                OriginalPath = "render.png",
                StorageKey = outputKey,
                StorageBucket = "test-bucket",
                MimeType = "image/png",
                SizeBytes = 8,
                Category = "outputFiles"
            });
            await seed.SaveChangesAsync();
        }

        h.Storage.Objects[outputKey] = PngHeaderOf(800, 576);
        Mock<IFileStorageService> moderated = new();
        moderated.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(8, "image/png", "\"e1\""));
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration moderation = new(
            new Mock<ReelBolt.Shared.Moderation.IContentModerationGate>().Object, moderated.Object,
            new Mock<ReelBolt.Shared.Moderation.IModerationMediaSampler>().Object,
            new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object, h.NewDb(),
            Microsoft.Extensions.Options.Options.Create(new ReelBolt.Shared.Moderation.ContentModerationOptions { Enabled = true }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Moderation.PublishModeration>.Instance);

        (int status, JsonElement body) = Harness.Read(await h.Operations(moderation: moderation).RunOperation(projectId, doc.Id,
            new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "swap the face", ReferenceProjectFileIds: [reference]),
            CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("CONTENT_MODERATION_PENDING");
        charges.Holds.Should().BeEmpty();
        h.Image.Calls.Should().BeEmpty("nothing reached the vendor");
    }

    [Fact]
    public async Task A_qwen_edit_with_a_reference_of_unknown_size_is_422_before_any_hold_or_send()
    {
        (Harness h, Billing.FakeNonWorkflowCharges charges, _, _) = QwenHarness();
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Storage.Objects[$"projects/{projectId}/userFiles/photo.png"] = PngHeaderOf(800, 576);
        Guid reference = await h.SeedImageFileAsync(projectId, "ref-1.png");
        h.Storage.Objects[$"projects/{projectId}/userFiles/ref-1.png"] = [1, 2, 3];

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(projectId, doc.Id,
            new EditImageOperationRequest("instruct", doc.Layers[0].Id, Prompt: "swap the face", ReferenceProjectFileIds: [reference]),
            CancellationToken.None));

        status.Should().Be(422);
        body.GetProperty("code").GetString().Should().Be("OPERATION_INVALID");
        charges.Holds.Should().BeEmpty();
        h.Image.Calls.Should().BeEmpty();
    }

    /// <summary>A baseline JPEG whose SOF0 frame header comes after <paramref name="padding"/> bytes of APP15 segments.</summary>
    private static byte[] JpegWithLateFrame(int width, int height, int padding)
    {
        List<byte> bytes = [0xFF, 0xD8];
        while (bytes.Count < padding)
        {
            bytes.AddRange([0xFF, 0xEF, 0xFF, 0xFF]);
            bytes.AddRange(new byte[0xFFFF - 2]);
        }

        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
            1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9]);
        return [.. bytes];
    }

    /// <summary>The fal client's billing key and size rule, delivering fixed bytes (no transport).</summary>
    private sealed class SizedFal(byte[] delivered, string resolved) : IImageClient, IBillingModelKeyed
    {
        private static readonly FalAiImageClient Real = new(new System.Net.Http.HttpClient(), "k");

        private Task<GeneratedImageSet> Deliver() =>
            Task.FromResult(new GeneratedImageSet([new GeneratedImage(delivered, "image/png")], "sized-1") { ResolvedModel = resolved });

        public Task<GeneratedImageSet> GenerateAsync(ReelBolt.Shared.Inference.ImageGenerationRequest request, CancellationToken ct) => Deliver();
        public Task<GeneratedImageSet> EditAsync(ImageEditRequest request, CancellationToken ct) => Deliver();
        public Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest request, CancellationToken ct) => Deliver();
        public Task<GeneratedImageSet> TransformAsync(ImageTransformRequest request, CancellationToken ct) => Deliver();
        public Task<ImageProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new ImageProbeResult(true, null));
        public ImageModelCapabilities Describe(string model) => Real.Describe(model);
        public string BillingModelKey(string model, IReadOnlyDictionary<string, JsonElement>? parameters) => Real.BillingModelKey(model, parameters);
        public bool PricesByOutputMegapixels(string model, bool editsASource, int referenceImages) =>
            Real.PricesByOutputMegapixels(model, editsASource, referenceImages);
        public long? MaxOutputPixels(string model) => Real.MaxOutputPixels(model);
    }

    /// <summary>An <see cref="IModelPriceService"/> over one fixed snapshot.</summary>
    private sealed class SnapshotPrices(ModelPriceSnapshot snapshot) : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(Metering.SeedPrices.Price(snapshot, request));

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
            ValueTask.FromResult(ModelPriceService.PriceQuotedUsd(snapshot, costUsd, unit, billingMode, Metering.SeedPrices.At));

        public void Invalidate()
        {
        }
    }

    // =============================================================================================
    // T-030: an unpriced Platform model is a readable 409 MODEL_UNPRICED, never a 5xx
    // =============================================================================================

    [Fact]
    public async Task An_operation_whose_model_is_unpriced_is_409_MODEL_UNPRICED_and_never_reaches_the_vendor()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        h.Unpriced.Add(InferenceProviderCapability.ImageGeneration);
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a neon city"), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("MODEL_UNPRICED");
        body.GetProperty("message").GetString().Should().Contain("has no price set up yet");
        h.Image.Calls.Should().BeEmpty();
        charges.Holds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_remove_background_whose_segmenter_is_unpriced_is_409_MODEL_UNPRICED()
    {
        Harness h = new();
        h.Unpriced.Add(InferenceProviderCapability.Segmentation);
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("removeBackground", doc.Layers[0].Id), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("MODEL_UNPRICED");
        h.Analysis.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unpriced_job_model_falls_back_to_the_ImageEdit_row_when_that_row_can_do_the_job()
    {
        Harness h = new() { EditProvider = Harness.FalEdit() };
        h.Unpriced.Add(InferenceProviderCapability.ImageExpand);
        h.RealDescribe = true;
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().GetOperationCapabilities(
            projectId, "expand", null, null, null, null, CancellationToken.None));

        status.Should().Be(200, "an unpriced model is treated as absent, so the next row that serves the job is used");
        body.GetProperty("provider").GetProperty("model").GetString().Should().Be("fal-ai/bria/expand");
    }

    [Fact]
    public async Task Operation_capabilities_of_an_unpriced_model_are_409_MODEL_UNPRICED()
    {
        Harness h = new();
        h.Unpriced.Add(InferenceProviderCapability.ImageGeneration);
        (Guid projectId, _) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().GetOperationCapabilities(
            projectId, "generate", null, null, null, null, CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("MODEL_UNPRICED");
    }

    [Fact]
    public async Task A_subject_cut_out_whose_segmenter_is_unpriced_is_409_MODEL_UNPRICED_and_holds_nothing()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        h.Unpriced.Add(InferenceProviderCapability.Segmentation);
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the dog"), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("MODEL_UNPRICED");
        body.GetProperty("message").GetString().Should().Contain("has no price set up yet");
        h.Analysis.Requests.Should().BeEmpty();
        charges.Holds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_subject_cut_out_whose_platform_usage_could_not_be_priced_is_charged_its_hold_never_zero()
    {
        var charges = new Billing.FakeNonWorkflowCharges { EstimateMillis = 4_000 };
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        UsageRecorder recorder = new(new EmptyPriceService(), new DiscardSink(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);
        h.Analysis.OnSegment = async (_, _) =>
        {
            await recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.SegmentationImages, 1, h.Segmentation!.ProviderId, InferenceProviderKind.FalAi, "fal-ai/evf-sam",
                UsageBillingMode.Platform));
            return new SegmentationResult(
                new GeneratedImage(Png, "image/png") { Width = 100, Height = 50 },
                new GeneratedImage(Png, "image/png") { Width = 100, Height = 50 },
                "seg-unpriced");
        };

        (int status, _) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the dog"), CancellationToken.None));

        status.Should().Be(200);
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(4_000, "max(metered 0, hold 4 credits)");
    }

    /// <summary>
    /// T-026 (finding 6): on a Platform row a caller-chosen picture model must be one of the configured, priced
    /// capabilities. Any other model would be priced at the account's dearest row, which can be below its vendor cost,
    /// so it is refused with a plain 400 before any hold or vendor call. A configured, priced model is accepted.
    /// </summary>
    [Fact]
    public async Task On_a_platform_row_a_requested_picture_model_outside_the_priced_catalog_is_400_before_any_hold()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a neon city", Model: "vendor/pricey-model"), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("message").GetString().Should().Contain("vendor/pricey-model");
        charges.Estimates.Should().BeEmpty();
        charges.Holds.Should().BeEmpty();
        h.Image.Calls.Should().BeEmpty("a refused model is never sent");

        await h.SeedPricedCapabilityAsync(InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ImageGeneration, "vendor/pricey-model");
        (int priced, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a neon city", Model: "vendor/pricey-model"), CancellationToken.None));
        priced.Should().Be(200, "a configured, priced model may be chosen");
        charges.Holds.Should().ContainSingle();
    }

    /// <summary>
    /// T-026 (info 9): ApplyRunPictureTool run from an assistant chat turn goes through the editor's own charge path: it
    /// takes exactly one editor hold and settles it at what the vendor metered, and the turn is not charged for the same
    /// usage again (the editor op meters into its own scope, so the turn settles at what the turn itself metered).
    /// </summary>
    [Fact]
    public async Task ApplyRunPictureTool_from_a_chat_turn_takes_one_editor_hold_and_the_turn_is_not_charged_twice()
    {
        var charges = new Billing.FakeNonWorkflowCharges { EstimateMillis = 4_000 };
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        UsageRecorder recorder = new(new Billing.NonWorkflowChargingHost.FixedPrices(3_000), new DiscardSink(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageRecorder>.Instance);
        long editorMetered = 0;
        h.Image.Handler = async (_, _) =>
        {
            editorMetered = await recorder.RecordAsync(new UsageRecordRequest(
                UsageKind.ImageGenImages, 1, h.GenerationProvider!.ProviderId, InferenceProviderKind.OpenAICompatible, "gpt-image",
                UsageBillingMode.Platform));
            return new GeneratedImageSet([Img(640, 640)], "turn-1");
        };
        Guid org = TestTenancy.OrgOf(h.OwnerId);
        TurnHold turn = await charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        UsageScopeState turnUsage;
        using (UsageScope.Begin(org, projectId, null, null, h.OwnerId, UsageSource.Assistant))
        {
            turnUsage = UsageScope.Current!;
            ReelBolt.Inference.Api.Services.Assistant.AssistantTurnGuard guard = new(charges, turn, turnUsage);
            Microsoft.Extensions.AI.AIFunction apply = guard.Gate(Microsoft.Extensions.AI.AIFunctionFactory.Create(async () =>
            {
                (int status, _) = Harness.Read(await h.Operations().RunOperation(
                    projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a neon city"), CancellationToken.None));
                return status;
            }, name: "ApplyRunPictureTool"));

            (await apply.InvokeAsync())!.ToString().Should().Be("200");
            await using TurnCharge charge = new(charges, turn, null);
            await charge.SettleAsync(turnUsage);
        }

        editorMetered.Should().BeGreaterThan(0);
        charges.Holds.Should().ContainSingle().Which.Source.Should().Be(UsageSource.Editor, "exactly one editor hold");
        charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(editorMetered, "settled at what the vendor metered");
        charges.Released.Should().BeEmpty();
        turnUsage.RunningCreditsMillis.Should().Be(0, "the picture op's usage lands in the editor's scope, not the turn's");
        charges.TurnSettlements.Should().ContainSingle().Which.MeteredMillis.Should().Be(0, "the turn is not charged for it again");
    }

    /// <summary>T-026 (finding 6): a BYO row keeps today's behaviour: the workspace pays its vendor, so any model may be asked for.</summary>
    [Fact]
    public async Task On_a_byo_row_a_requested_picture_model_is_still_passed_through()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        h.GenerationProvider = h.GenerationProvider! with { OrganizationId = Guid.NewGuid(), IsPlatformManaged = false };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a neon city", Model: "vendor/any-model"), CancellationToken.None));

        status.Should().Be(200);
        h.Image.Calls.Should().ContainSingle();
    }

    /// <summary>
    /// T-110 (L2): a subject cut-out applies RunOperation's priced-model rule. On a Platform segmenter a caller-chosen
    /// model outside the priced catalog (unpriced sam2 here) is refused with the same 400 before any estimate, hold or
    /// vendor call; once it is a configured, priced capability it runs.
    /// </summary>
    [Fact]
    public async Task On_a_platform_segmenter_a_requested_subject_model_outside_the_priced_catalog_is_400_before_any_hold()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the dog", Model: "fal-ai/sam2/image"), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("OPERATION_INVALID");
        body.GetProperty("message").GetString().Should().Contain("fal-ai/sam2/image").And.Contain("priced");
        charges.Estimates.Should().BeEmpty();
        charges.Holds.Should().BeEmpty();
        h.Analysis.Requests.Should().BeEmpty("a refused model is never sent");

        await h.SeedPricedCapabilityAsync(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/sam2/image");
        (int priced, _) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the dog", Model: "fal-ai/sam2/image"), CancellationToken.None));
        priced.Should().Be(200, "a configured, priced model may be chosen");
        charges.Holds.Should().ContainSingle();
        h.Analysis.Requests.Single().Model.Should().Be("fal-ai/sam2/image");
    }

    /// <summary>T-110 (L2): a BYO segmenter is the workspace's own vendor bill: any requested model still runs.</summary>
    [Fact]
    public async Task On_a_byo_segmenter_a_requested_subject_model_is_still_passed_through()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        h.Segmentation = h.Segmentation! with { OrganizationId = Guid.NewGuid(), IsPlatformManaged = false };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the dog", Model: "fal-ai/sam2/image"), CancellationToken.None));

        status.Should().Be(200);
        h.Analysis.Requests.Single().Model.Should().Be("fal-ai/sam2/image");
    }

    /// <summary>
    /// T-110 (L2): removeBackground on a fal.ai row defaults to fal-ai/birefnet/v2, which the catalog leaves unpriced
    /// (must-measure #5). On a Platform row with billing on it is refused in plain words (409 MODEL_UNPRICED, the
    /// branch's rule for an unpriced platform model) before any hold or vendor call, never billed at the dearest row.
    /// </summary>
    [Fact]
    public async Task On_a_platform_row_with_billing_on_the_unpriced_birefnet_default_is_409_MODEL_UNPRICED_before_any_hold()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("removeBackground", doc.Layers[0].Id), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("MODEL_UNPRICED");
        body.GetProperty("message").GetString().Should().Contain("fal-ai/birefnet/v2").And.Contain("has no price set up yet");
        charges.Estimates.Should().BeEmpty();
        charges.Holds.Should().BeEmpty();
        h.Analysis.Requests.Should().BeEmpty();

        await h.SeedPricedCapabilityAsync(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/birefnet/v2");
        (int priced, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("removeBackground", doc.Layers[0].Id), CancellationToken.None));
        priced.Should().Be(200, "once it is priced it runs");
    }

    /// <summary>T-110 (L2): the birefnet default is unchanged on a BYO row with billing on (the workspace pays its vendor).</summary>
    [Fact]
    public async Task On_a_byo_row_the_birefnet_default_still_runs()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        h.Segmentation = h.Segmentation! with { OrganizationId = Guid.NewGuid(), IsPlatformManaged = false };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("removeBackground", doc.Layers[0].Id), CancellationToken.None));

        status.Should().Be(200);
        h.Analysis.Requests.Single().Model.Should().Be("fal-ai/birefnet/v2");
    }

    [Fact]
    public async Task A_vendor_failure_releases_the_hold()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        h.Image.Handler = (_, _) => throw new InvalidOperationException("vendor said no");

        (int status, _) = Harness.Read(await h.Operations().RunOperation(
            projectId, doc.Id, new EditImageOperationRequest("generate", Prompt: "a neon city"), CancellationToken.None));

        status.Should().Be(424);
        charges.Released.Should().ContainSingle();
        charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_subject_cut_out_the_workspace_cannot_afford_answers_402_and_never_reaches_the_segmenter()
    {
        var charges = new Billing.FakeNonWorkflowCharges { RefuseHolds = true };
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the person"), CancellationToken.None));

        status.Should().Be(402);
        body.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        h.Analysis.Requests.Should().BeEmpty();
        charges.Estimates.Should().ContainSingle().Which.Unit.Should().Be(UsageKind.SegmentationImages);
        charges.Holds.Should().ContainSingle().Which.Source.Should().Be(UsageSource.Editor);
    }

    [Fact]
    public async Task A_subject_cut_out_settles_after_the_segmenter_answers()
    {
        var charges = new Billing.FakeNonWorkflowCharges();
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, _) = Harness.Read(await h.Documents().SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the person"), CancellationToken.None));

        status.Should().Be(200);
        h.Analysis.Requests.Should().ContainSingle();
        charges.Settled.Should().ContainSingle();
        charges.Released.Should().BeEmpty();
    }

    private sealed partial class Harness
    {
        /// <summary>The instant every controller under test believes it is.</summary>
        public static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        private readonly string _database = Guid.NewGuid().ToString();

        public Guid OwnerId { get; } = Guid.NewGuid();

        public FakeStorage Storage { get; } = new();

        public FakeImageClient Image { get; } = new();

        public FakeAnalysisClient Analysis { get; } = new();

        public bool Presign { get; init; } = true;

        public int GraceDays { get; init; } = 7;

        public IPlanGateService? PlanGate { get; init; }

        /// <summary>The non-workflow charge path; null = not wired (the host registers none).</summary>
        public INonWorkflowChargeService? Charges { get; init; }

        public TimeSpan? OperationTimeout { get; init; }
        public TimeSpan? SegmentationTimeout { get; init; }

        /// <summary>Lets the fake image client describe models from the real fal catalogue.</summary>
        public bool RealDescribe { get => Image.RealDescribe; set => Image.RealDescribe = value; }

        public ResolvedImageProvider? EditProvider { get; set; } = new(
            Guid.NewGuid(), "bfl", InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageEdit,
            "https://api.bfl.ai", "flux-pro-1.0-fill", "key", 60);

        public ResolvedImageProvider? GenerationProvider { get; set; } = new(
            Guid.NewGuid(), "gen", InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.ImageGeneration,
            "https://api.example", "gpt-image", "key", 60);

        public ResolvedImageAnalysisProvider? Segmentation { get; set; } = FalSegmentation();

        /// <summary>
        /// The default row of each per-job capability (erase, fill, expand, ...). Empty by default, so every
        /// job falls back to <see cref="EditProvider"/> exactly as all of them resolved ImageEdit before the
        /// split - when that row's kind serves the job.
        /// </summary>
        public Dictionary<InferenceProviderCapability, ResolvedImageProvider> JobProviders { get; } = [];

        /// <summary>
        /// Capabilities whose default is an unpriced Platform model: the resolver refuses them with
        /// <see cref="ModelUnpricedException"/>, as the real one does with billing on.
        /// </summary>
        public HashSet<InferenceProviderCapability> Unpriced { get; } = [];

        private T Priced<T>(InferenceProviderCapability capability, T value) =>
            Unpriced.Contains(capability) ? throw new ModelUnpricedException(capability, $"Unpriced {capability}") : value;

        /// <summary>Every capability the controller asked <c>ResolveImageJobAsync</c> for, in order.</summary>
        public List<InferenceProviderCapability> ResolvedCapabilities { get; } = [];

        /// <summary>
        /// Writes a bitmap layer straight into the document (the way an earlier operation would have) with
        /// real bytes in storage, and returns its id.
        /// </summary>
        public async Task<Guid> SeedLayerAsync(
            Guid projectId, Guid documentId, EditImageLayerKind kind, byte[] bytes, string mime, int? width, int? height,
            string transformJson = "{}", byte[]? matte = null)
        {
            using InferenceApiDbContext db = NewDb();
            EditImageDocument document = await db.EditImageDocuments.Include(d => d.Layers).SingleAsync(d => d.Id == documentId);
            Guid id = Guid.NewGuid();
            string key = $"projects/{projectId}/agentFiles/{id}.png";
            Storage.Objects[key] = bytes;
            string? matteKey = null;
            if (matte is not null)
            {
                matteKey = $"projects/{projectId}/agentFiles/{id}-matte.png";
                Storage.Objects[matteKey] = matte;
            }

            db.EditImageLayers.Add(new EditImageLayer
            {
                Id = id,
                DocumentId = documentId,
                ZOrder = document.Layers.Count + 1,
                Role = EditImageLayerRoles.Subject,
                Kind = kind,
                Label = kind.ToString(),
                StorageKey = key,
                StorageBucket = "test-bucket",
                MatteStorageKey = matteKey,
                MimeType = mime,
                Width = width,
                Height = height,
                Opacity = 1,
                TransformJson = transformJson,
                CreatedAt = DateTime.UtcNow
            });
            document.Revision += 1;
            await db.SaveChangesAsync();
            return id;
        }

        public static ResolvedImageProvider FalEdit() => new(
            Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit,
            "https://queue.fal.run", "ignored-row-model", "key", 60);

        public static ResolvedImageAnalysisProvider FalSegmentation() => new(
            Guid.NewGuid(), "fal-seg", InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation,
            "https://queue.fal.run", "fal-ai/evf-sam", "key", 60);

        public InferenceApiDbContext NewDb() => new(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(_database).Options);

        private ICurrentUser User(Guid? userId = null)
        {
            Guid id = userId ?? OwnerId;
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(id);
            user.SetupGet(u => u.Email).Returns("test@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.IsPlatformAdmin).Returns(false);
            user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(id));
            user.SetupGet(u => u.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);
            return user.Object;
        }

        private IInferenceProviderResolver Resolver()
        {
            Mock<IInferenceProviderResolver> resolver = new();
            resolver.Setup(r => r.ResolveImageEditAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => EditProvider);
            resolver.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => GenerationProvider);
            resolver.Setup(r => r.ResolveSegmentationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Priced(InferenceProviderCapability.Segmentation, Segmentation));
            resolver.Setup(r => r.ResolveImageJobAsync(
                    It.IsAny<InferenceProviderCapability>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .Callback((InferenceProviderCapability capability, Guid? _, CancellationToken _) => ResolvedCapabilities.Add(capability))
                .ReturnsAsync((InferenceProviderCapability capability, Guid? _, CancellationToken _) => Priced(capability, capability switch
                {
                    InferenceProviderCapability.ImageEdit => EditProvider,
                    InferenceProviderCapability.ImageGeneration => GenerationProvider,
                    _ => JobProviders.GetValueOrDefault(capability)
                }));
            return resolver.Object;
        }

        private IPresignedUrlService? Presigner()
        {
            if (!Presign) return null;

            Mock<IPresignedUrlService> presigner = new();
            presigner.Setup(p => p.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync((Guid _, string key, TimeSpan _) => "https://get.test/" + key);
            presigner.Setup(p => p.CreateBrowserUploadPutAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync((Guid _, string key, string _, TimeSpan _) => new PresignedPut("https://put.test/" + key, key));
            return presigner.Object;
        }

        public EditImageDocumentsController Documents(Guid? userId = null, ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock? storageLock = null, Microsoft.Extensions.Configuration.IConfiguration? configuration = null, ReelBolt.Inference.Api.Services.Moderation.PublishModeration? moderation = null)
        {
            InferenceApiDbContext db = NewDb();
            return new EditImageDocumentsController(
                db, TestTenancy.Access(db, User(userId)), Storage, Resolver(), new FakeAnalysisFactory(Analysis), Presigner(), charges: Charges, planGate: PlanGate, storageLock: storageLock, configuration: configuration, publishModeration: moderation, segmentationTimeout: SegmentationTimeout)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public EditImageOperationsController Operations(Guid? userId = null, ReelBolt.Inference.Api.Services.Moderation.PublishModeration? moderation = null)
        {
            InferenceApiDbContext db = NewDb();
            return new EditImageOperationsController(
                db, TestTenancy.Access(db, User(userId)), Storage, Resolver(), new FakeImageFactory(Image),
                new FakeAnalysisFactory(Analysis), Presigner(), OperationTimeout, charges: Charges, currentUser: User(userId),
                publishModeration: moderation);
        }

        public EditImageAssetsController Assets(Guid? userId = null, ReelBolt.Inference.Api.Services.Uploads.OrganizationStorageLock? storageLock = null, ReelBolt.Inference.Api.Services.Moderation.PublishModeration? publishModeration = null)
        {
            InferenceApiDbContext db = NewDb();
            ICurrentUser user = User(userId);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:StorageRetention:GraceDays"] = GraceDays.ToString() })
                .Build();
            return new EditImageAssetsController(
                db, user, TestTenancy.Access(db, user), Storage, Presigner(), PlanGate, configuration, new FixedTime(Now), publishModeration: publishModeration, storageLock: storageLock);
        }

        public async Task<Guid> SeedProjectAsync(Guid? ownerId = null)
        {
            using InferenceApiDbContext db = NewDb();
            Guid owner = ownerId ?? OwnerId;
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "P",
                OwnerId = owner,
                OrganizationId = TestTenancy.OrgOf(owner),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            return project.Id;
        }

        public Task<Guid> SeedImageFileAsync(Guid projectId, string name = "photo.png") =>
            SeedFileAsync(projectId, name, "image/png");

        public async Task<Guid> SeedFileAsync(Guid projectId, string name, string mime)
        {
            using InferenceApiDbContext db = NewDb();
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OriginalFileName = name,
                OriginalPath = name,
                StorageKey = $"projects/{projectId}/userFiles/{name}",
                StorageBucket = "test-bucket",
                MimeType = mime,
                SizeBytes = 8
            };
            db.ProjectFiles.Add(file);
            await db.SaveChangesAsync();
            Storage.Objects[file.StorageKey] = Png;
            return file.Id;
        }

        public async Task<EditImageDocumentDto> CreateDocumentAsync(Guid projectId, Guid fileId)
        {
            (int status, JsonElement body) = Read(await Documents().Create(
                projectId, new CreateEditImageDocumentRequest(fileId), CancellationToken.None));
            status.Should().Be(200);
            return body.Deserialize<EditImageDocumentDto>(Web)!;
        }

        /// <summary>An enabled platform capability serving <paramref name="modelCode"/>, with a price for every unit it meters.</summary>
        public async Task SeedPricedCapabilityAsync(InferenceProviderKind kind, InferenceProviderCapability capability, string modelCode)
        {
            using InferenceApiDbContext db = NewDb();
            ModelCapabilityView row = new()
            {
                Id = Guid.NewGuid(),
                ProviderId = Guid.NewGuid(),
                ModelId = Guid.NewGuid(),
                Name = modelCode,
                Kind = kind,
                Capability = capability,
                Endpoint = "https://api.example",
                ModelName = modelCode,
                IsEnabled = true
            };
            db.AddFlat(row);
            foreach (UsageKind unit in ProviderCapabilityUsage.ByCapability[capability])
            {
                db.ModelCapabilityPrices.Add(new ModelCapabilityPrice
                {
                    Id = Guid.NewGuid(),
                    ModelCapabilityId = row.Id,
                    Unit = unit,
                    CostUsdPerUnit = 0.05m,
                    EffectiveFrom = DateTime.UtcNow.AddDays(-1)
                });
            }

            await db.SaveChangesAsync();
        }

        /// <summary>A fresh project, an image file and a document over it.</summary>
        public async Task<(Guid ProjectId, EditImageDocumentDto Document)> SeedDocumentAsync()
        {
            Guid projectId = await SeedProjectAsync();
            Guid fileId = await SeedImageFileAsync(projectId);
            return (projectId, await CreateDocumentAsync(projectId, fileId));
        }

        /// <summary>
        /// A fresh project and a document over a photo whose header declares no size (a bare PNG signature
        /// and no IHDR). The Source layer then has neither a recorded size nor a readable header — the state
        /// the size fallbacks are about — and the picture opens with no canvas of its own, so a test that
        /// cares about a size the SERVER cannot measure is not quietly given one measured from a 1x1 photo.
        /// </summary>
        public async Task<(Guid ProjectId, EditImageDocumentDto Document)> SeedDocumentWithUnknownPhotoAsync()
        {
            Guid projectId = await SeedProjectAsync();
            Guid fileId = await SeedFileWithBytesAsync(projectId, "photo.png", "image/png", UnreadablePng);
            return (projectId, await CreateDocumentAsync(projectId, fileId));
        }

        public async Task<EditImageLayerDto> AddTextLayerAsync(Guid projectId, Guid documentId, string text)
        {
            (int status, JsonElement body) = Read(await Documents().CreateTextLayer(
                projectId, documentId, new CreateTextLayerRequest(new TextContentDto(text)), CancellationToken.None));
            status.Should().Be(200);
            return body.Deserialize<EditImageLayerDto>(Web)!;
        }

        /// <summary>Runs an operation that must succeed and returns the new layer's id.</summary>
        public async Task<Guid> RunOperationAsync(Guid projectId, Guid documentId, EditImageOperationRequest request)
        {
            (int status, JsonElement body) = Read(await Operations().RunOperation(projectId, documentId, request, CancellationToken.None));
            status.Should().Be(200);
            return body.GetProperty("layers")[0].GetProperty("id").GetGuid();
        }

        /// <summary>
        /// Plays the browser's PUT: stores the bytes under a key of exactly the shape upload-url issues.
        /// Built directly rather than through the route so a test can upload bytes the route would have
        /// refused to issue a URL for (an empty or oversized object).
        /// </summary>
        public Task<string> UploadAsync(Guid projectId, Guid documentId, string purpose, byte[] bytes, string extension = ".png")
        {
            string key = $"projects/{projectId}/agentFiles/image-editor/{documentId}/{purpose}-{Guid.NewGuid()}{extension}";
            Storage.Objects[key] = bytes;
            return Task.FromResult(key);
        }

        /// <summary>The document as it is stored now (layers and revision).</summary>
        public async Task<EditImageDocumentDto> CurrentDocumentAsync(Guid projectId, Guid documentId)
        {
            (int status, JsonElement body) = Read(await Documents().Get(projectId, documentId, CancellationToken.None));
            status.Should().Be(200);
            return body.Deserialize<EditImageDocumentDto>(Web)!;
        }

        /// <summary>A refused operation adds no layer and no revision (for a document seeded with extra layers).</summary>
        public async Task AssertNoNewLayerAsync(EditImageDocumentDto document)
        {
            using InferenceApiDbContext db = NewDb();
            (await db.EditImageLayers.CountAsync(l => l.DocumentId == document.Id)).Should().Be(document.Layers.Count);
            (await db.EditImageDocuments.SingleAsync(d => d.Id == document.Id)).Revision.Should().Be(document.Revision);
        }

        /// <summary>A failed operation adds no layer, no object and no revision.</summary>
        public async Task AssertNothingStoredAsync(EditImageDocumentDto document)
        {
            using InferenceApiDbContext db = NewDb();
            (await db.EditImageLayers.CountAsync(l => l.DocumentId == document.Id)).Should().Be(document.Layers.Count);
            (await db.EditImageDocuments.SingleAsync(d => d.Id == document.Id)).Revision.Should().Be(document.Revision);
            Storage.Objects.Keys.Should().NotContain(k => k.Contains("/agentFiles/") && !k.Contains("image-editor"));
        }

        // ---- result unwrapping -------------------------------------------------------------------

        public static (int Status, JsonElement Body) Read<T>(ActionResult<T> result) =>
            result.Result is null ? (200, JsonSerializer.SerializeToElement(result.Value, Web)) : Read(result.Result);

        public static (int Status, JsonElement Body) Read(IActionResult result) => result switch
        {
            ObjectResult o => (o.StatusCode ?? 200, JsonSerializer.SerializeToElement(o.Value, Web)),
            FileStreamResult => (200, default),
            NoContentResult => (204, default),
            StatusCodeResult s => (s.StatusCode, default),
            ForbidResult => (403, default),
            _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}")
        };
    }

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private sealed class FakeStorage : IFileStorageService
    {
        public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult<string?>(Objects.ContainsKey(storageKey) ? $"\"{storageKey.GetHashCode():x8}\"" : null);

        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        /// <summary>The size a HEAD reports for a key, when a test needs a size it should not allocate.</summary>
        public Dictionary<string, long> SizeOverrides { get; } = new(StringComparer.Ordinal);

        public string BucketName => "test-bucket";

        public Task<StoredFileObject> UploadAsync(
            Guid projectId, Guid fileId, Stream content, string fileName, string contentType,
            IReadOnlyDictionary<string, string>? metadata, CancellationToken ct, string category = "userFiles",
            string? originalPath = null)
        {
            using MemoryStream copy = new();
            content.CopyTo(copy);
            string storageFileName = $"{fileId}{Path.GetExtension(fileName)}";
            string key = $"projects/{projectId}/{category}/{storageFileName}";
            Objects[key] = copy.ToArray();
            return Task.FromResult(new StoredFileObject(
                key, BucketName, $"projects/{projectId}/{category}/", null, category, fileName, fileName, null, storageFileName));
        }

        /// <summary>What S3 answers for a key with no object: a 404 <c>NoSuchKey</c>, not a dictionary miss.</summary>
        /// <summary>Every key a download was asked for (moderation reads the bytes it judges).</summary>
        public List<string> Downloads { get; } = [];

        public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            Downloads.Add(storageKey);
            return DownloadCoreAsync(storageKey);
        }

        private Task<Stream> DownloadCoreAsync(string storageKey) =>
            Objects.TryGetValue(storageKey, out byte[]? bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new Amazon.S3.AmazonS3Exception("The specified key does not exist.")
                {
                    StatusCode = System.Net.HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey"
                };

        /// <summary>Every server-side copy, in order, as (source, destination).</summary>
        public List<(string From, string To)> Copies { get; } = [];

        public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
        {
            if (!Objects.TryGetValue(sourceStorageKey, out byte[]? bytes))
            {
                throw new Amazon.S3.AmazonS3Exception("The specified key does not exist.")
                {
                    StatusCode = System.Net.HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey"
                };
            }

            Copies.Add((sourceStorageKey, destinationStorageKey));
            Objects[destinationStorageKey] = bytes;
            return Task.CompletedTask;
        }

        /// <summary>Every key a delete was asked for, in order.</summary>
        public List<string> DeletedKeys { get; } = [];

        public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            DeletedKeys.Add(storageKey);
            Objects.Remove(storageKey);
            return Task.CompletedTask;
        }

        /// <summary>When true, HEADs report no ETag (a store that cannot pin the object).</summary>
        public bool OmitETag { get; set; }

        /// <summary>When false, <see cref="MoveIfMatchAsync"/> answers false without moving.</summary>
        public bool CanConditionCopy { get; set; } = true;

        /// <summary>Runs after each HEAD answered, with the key: a test re-PUTs the object here (a swap between HEAD and copy).</summary>
        public Action<string>? AfterHead { get; set; }

        // An object's ETag changes whenever a new byte array is stored at its key (a re-PUT).
        private static string ETagOf(byte[] bytes) => $"\"etag-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bytes):x8}\"";

        public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            StoredObjectMetadata? answer = Objects.TryGetValue(storageKey, out byte[]? bytes)
                ? new StoredObjectMetadata(
                    SizeOverrides.TryGetValue(storageKey, out long size) ? size : bytes.Length, "image/png", OmitETag ? null : ETagOf(bytes))
                : null;
            AfterHead?.Invoke(storageKey);
            return Task.FromResult(answer);
        }

        /// <summary>Every conditional move that actually moved, as (source, destination).</summary>
        public List<(string From, string To)> ConditionalMoves { get; } = [];

        /// <summary>When set, <see cref="MoveIfMatchAsync"/> copies to the destination (source left) and then throws it.</summary>
        public Exception? MoveCopiesThenThrows { get; set; }

        public Task<bool> MoveIfMatchAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, string expectedETag, CancellationToken ct)
        {
            if (MoveCopiesThenThrows is not null && Objects.TryGetValue(sourceStorageKey, out byte[]? landed))
            {
                Objects[destinationStorageKey] = landed;
                throw MoveCopiesThenThrows;
            }

            if (!CanConditionCopy
                || !Objects.TryGetValue(sourceStorageKey, out byte[]? bytes)
                || !string.Equals(ETagOf(bytes), expectedETag, StringComparison.Ordinal))
                return Task.FromResult(false);
            Objects[destinationStorageKey] = bytes;
            Objects.Remove(sourceStorageKey);
            if (SizeOverrides.Remove(sourceStorageKey, out long size)) SizeOverrides[destinationStorageKey] = size;
            ConditionalMoves.Add((sourceStorageKey, destinationStorageKey));
            return Task.FromResult(true);
        }

        public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct) =>
            Task.FromResult(storageKeys.Count(k => Objects.Remove(k)));

        public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
        {
            Objects[destinationStorageKey] = Objects[sourceStorageKey];
            Objects.Remove(sourceStorageKey);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeImageClient : IImageClient
    {
        public List<(string Method, object Request)> Calls { get; } = [];

        public Func<object, CancellationToken, Task<GeneratedImageSet>>? Handler { get; set; }

        private Task<GeneratedImageSet> Handle(string method, object request, CancellationToken ct)
        {
            Calls.Add((method, request));
            return Handler is null
                ? Task.FromResult(new GeneratedImageSet([new GeneratedImage(Png, "image/png") { Width = 100, Height = 50 }], "vendor-req-1"))
                : Handler(request, ct);
        }

        public Task<GeneratedImageSet> GenerateAsync(ReelBolt.Shared.Inference.ImageGenerationRequest request, CancellationToken ct) =>
            Handle("generate", request, ct);

        public Task<GeneratedImageSet> EditAsync(ImageEditRequest request, CancellationToken ct) => Handle("edit", request, ct);

        public Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest request, CancellationToken ct) => Handle("inpaint", request, ct);

        public Task<GeneratedImageSet> TransformAsync(ImageTransformRequest request, CancellationToken ct) => Handle("transform", request, ct);

        public Task<ImageProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new ImageProbeResult(true, null));

        /// <summary>Describe through the real fal catalogue (the capabilities route), instead of refusing.</summary>
        public bool RealDescribe { get; set; }

        public ImageModelCapabilities Describe(string model) =>
            RealDescribe ? new FalAiImageClient(new System.Net.Http.HttpClient(), "k").Describe(model) : throw new NotSupportedException();
    }

    /// <summary>Fails the test if a request is ever sent: a factor the schema refuses must stop before the vendor.</summary>
    private sealed class NoSendHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("A refused factor reached the network.");
    }

    private sealed class FakeImageFactory(FakeImageClient client) : IImageClientFactory
    {
        public IImageClient Get(ResolvedImageProvider provider) => client;
    }

    private sealed class FakeAnalysisClient : IImageAnalysisClient
    {
        public List<SegmentationRequest> Requests { get; } = [];

        public Func<SegmentationRequest, CancellationToken, Task<SegmentationResult>>? OnSegment { get; set; }

        public Task<SegmentationResult> SegmentAsync(SegmentationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return OnSegment is null
                ? Task.FromResult(new SegmentationResult(
                    new GeneratedImage(Png, "image/png") { Width = 100, Height = 50 },
                    new GeneratedImage(Png, "image/png") { Width = 100, Height = 50 },
                    "seg-req-1"))
                : OnSegment(request, ct);
        }

        public Task<DepthResult> EstimateDepthAsync(DepthRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<ImageAnalysisProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new ImageAnalysisProbeResult(true, null));

        public ImageAnalysisModelCapabilities Describe(string model) => throw new NotSupportedException();
    }

    private sealed class FakeAnalysisFactory(FakeAnalysisClient client) : IImageAnalysisClientFactory
    {
        public IImageAnalysisClient Get(ResolvedImageAnalysisProvider provider) => client;
    }
}
