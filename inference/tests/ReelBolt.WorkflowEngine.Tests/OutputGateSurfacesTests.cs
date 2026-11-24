using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Per-surface checks for outputs that are not served: run and step-result text, picture layers, exports, file lists.</summary>
public sealed class OutputGateSurfacesTests
{
    private static readonly DateTime Marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Guid SeedProject(InferenceApiDbContext db, DateTime? marker, FileModerationStatus? flagged = null)
    {
        Guid id = Guid.NewGuid();
        db.Projects.Add(new Project { Id = id, Name = "p", CsamOutputHoldAt = marker });
        db.SaveChanges();
        _ = flagged;
        return id;
    }

    private static ProjectFile SeedFile(InferenceApiDbContext db, Guid projectId, string folder, DateTime uploaded)
    {
        ProjectFile f = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            StorageKey = $"projects/{projectId}/{folder}/{Guid.NewGuid():N}.png",
            UploadedAt = uploaded,
        };
        db.ProjectFiles.Add(f);
        db.SaveChanges();
        return f;
    }

    private static EditImageLayer SeedLayer(InferenceApiDbContext db, Guid projectId, Guid? sourceFileId, DateTime createdAt, Guid? docSource = null)
    {
        EditImageDocument doc = new() { Id = Guid.NewGuid(), ProjectId = projectId, SourceProjectFileId = docSource };
        EditImageLayer layer = new()
        {
            Id = Guid.NewGuid(),
            DocumentId = doc.Id,
            Kind = EditImageLayerKind.Source,
            SourceProjectFileId = sourceFileId,
            StorageKey = $"projects/{projectId}/agentFiles/image-editor/{doc.Id}/{Guid.NewGuid():N}.png",
            CopySizeBytes = 10,
            CreatedAt = createdAt,
        };
        db.EditImageDocuments.Add(doc);
        db.EditImageLayers.Add(layer);
        db.SaveChanges();
        return layer;
    }

    private static WorkflowStepResult Result() => new()
    {
        Id = Guid.NewGuid(),
        WorkflowStepId = Guid.NewGuid(),
        Output = "text",
        OutputJson = "{\"a\":1}",
        InputJson = "{\"in\":1}",
        ToolCallsJson = "[1]",
        ReasoningJson = "[2]",
        ChatTranscriptJson = "[3]",
        OutputStorageKey = "projects/x/outputFiles/a.mp4",
        ArtifactStorageKey = "projects/x/agentFiles/a.json",
    };

    [Fact]
    public void A_held_step_result_carries_no_text_or_key()
    {
        StepResultResponse held = WorkflowsController.MapStepResult(Result(), unavailable: true);
        held.Output.Should().BeEmpty();
        held.OutputJson.Should().BeNull();
        held.InputJson.Should().BeNull();
        held.ToolCallsJson.Should().BeNull();
        held.ReasoningJson.Should().BeNull();
        held.ChatTranscriptJson.Should().BeNull();
        held.OutputStorageKey.Should().BeNull();
        held.ArtifactStorageKey.Should().BeNull();

        StepResultResponse served = WorkflowsController.MapStepResult(Result());
        served.Output.Should().Be("text");
        served.ToolCallsJson.Should().Be("[1]");
        served.ChatTranscriptJson.Should().Be("[3]");
    }

    [Fact]
    public void A_held_run_carries_no_result_text_and_a_served_one_does()
    {
        WorkflowExecution run = new() { Id = Guid.NewGuid(), ResultJson = "{\"r\":1}", StepResults = new List<WorkflowStepResult> { Result() } };
        WorkflowsController.MapExecutionResponse(run, runUnavailable: true).ResultJson.Should().BeNull();
        WorkflowsController.MapExecutionResponse(run, unavailable: new HashSet<Guid> { run.StepResults.First().Id }).ResultJson.Should().BeNull();
        WorkflowsController.MapExecutionResponse(run).ResultJson.Should().Be("{\"r\":1}");
    }

    [Fact]
    public async Task A_run_started_before_the_marker_is_held_and_a_later_one_is_served()
    {
        using InferenceApiDbContext db = NewDb();
        Guid project = SeedProject(db, Marker);
        (await OutputGate.IsRunHeldAsync(db, project, Marker.AddHours(-1), Ct)).Should().BeTrue();
        (await OutputGate.IsRunHeldAsync(db, project, Marker.AddHours(2), Ct)).Should().BeFalse();
        (await OutputGate.IsRunHeldAsync(db, SeedProject(db, null), Marker.AddHours(-1), Ct)).Should().BeFalse("a project with no marker holds nothing");
    }

    [Fact]
    public async Task A_layer_copied_from_a_held_source_is_held_even_when_the_copy_is_new()
    {
        using InferenceApiDbContext db = NewDb();
        Guid project = SeedProject(db, Marker);
        ProjectFile source = SeedFile(db, project, "outputFiles", Marker.AddHours(-3));
        EditImageLayer copy = SeedLayer(db, project, source.Id, Marker.AddHours(5));
        (await OutputGate.IsPictureLayerHeldAsync(db, project, copy, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_layer_copied_from_a_served_source_is_served()
    {
        using InferenceApiDbContext db = NewDb();
        Guid project = SeedProject(db, Marker);
        ProjectFile own = SeedFile(db, project, "userFiles", Marker.AddHours(-3));
        ProjectFile later = SeedFile(db, project, "outputFiles", Marker.AddHours(5));
        (await OutputGate.IsPictureLayerHeldAsync(db, project, SeedLayer(db, project, own.Id, Marker.AddHours(-2)), Ct)).Should().BeFalse();
        (await OutputGate.IsPictureLayerHeldAsync(db, project, SeedLayer(db, project, later.Id, Marker.AddHours(6)), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_layer_whose_origin_row_is_gone_is_held_when_made_before_the_marker_and_served_after()
    {
        using InferenceApiDbContext db = NewDb();
        Guid project = SeedProject(db, Marker);
        (await OutputGate.IsPictureLayerHeldAsync(db, project, SeedLayer(db, project, Guid.NewGuid(), Marker.AddHours(-2)), Ct)).Should().BeTrue();
        (await OutputGate.IsPictureLayerHeldAsync(db, project, SeedLayer(db, project, null, Marker.AddHours(-2)), Ct)).Should().BeTrue();
        (await OutputGate.IsPictureLayerHeldAsync(db, project, SeedLayer(db, project, null, Marker.AddHours(4)), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_deleted_origin_row_leaves_a_pre_marker_copy_held()
    {
        using InferenceApiDbContext db = NewDb();
        Guid project = SeedProject(db, Marker);
        ProjectFile source = SeedFile(db, project, "outputFiles", Marker.AddHours(-3));
        EditImageLayer copy = SeedLayer(db, project, source.Id, Marker.AddHours(-2));
        db.ProjectFiles.Remove(source);
        db.SaveChanges();
        (await OutputGate.IsPictureLayerHeldAsync(db, project, copy, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task The_stamp_decides_when_the_layer_has_no_link_whatever_the_document_source_is()
    {
        using InferenceApiDbContext db = NewDb();
        Guid project = SeedProject(db, Marker);
        ProjectFile source = SeedFile(db, project, "outputFiles", Marker.AddHours(-3));
        (await OutputGate.IsPictureLayerHeldAsync(db, project, SeedLayer(db, project, null, Marker.AddHours(5), docSource: source.Id), Ct)).Should().BeFalse();
        (await OutputGate.IsPictureLayerHeldAsync(db, project, SeedLayer(db, project, null, Marker.AddHours(-5), docSource: source.Id), Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_project_with_no_marker_and_a_text_layer_hold_nothing()
    {
        using InferenceApiDbContext db = NewDb();
        Guid open = SeedProject(db, null);
        ProjectFile source = SeedFile(db, open, "outputFiles", Marker.AddHours(-3));
        (await OutputGate.IsPictureLayerHeldAsync(db, open, SeedLayer(db, open, source.Id, Marker.AddHours(-2)), Ct)).Should().BeFalse();
        Guid marked = SeedProject(db, Marker);
        EditImageLayer text = SeedLayer(db, marked, null, Marker.AddHours(-2));
        text.Kind = EditImageLayerKind.Text;
        (await OutputGate.IsPictureLayerHeldAsync(db, marked, text, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_read_holds_every_picture_layer()
    {
        InferenceApiDbContext db = NewDb();
        Guid project = SeedProject(db, null);
        EditImageLayer layer = SeedLayer(db, project, null, DateTime.UtcNow);
        db.Dispose();
        (await OutputGate.IsPictureLayerHeldAsync(db, project, layer, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_picture_export_follows_its_pictures_layers()
    {
        using InferenceApiDbContext db = NewDb();
        Guid project = SeedProject(db, Marker);
        ProjectFile held = SeedFile(db, project, "outputFiles", Marker.AddHours(-3));
        EditImageLayer layer = SeedLayer(db, project, held.Id, Marker.AddHours(5));
        ProjectFile exported = SeedFile(db, project, "userFiles", Marker.AddHours(6));
        db.EditImageExports.Add(new EditImageExport { Id = Guid.NewGuid(), DocumentId = layer.DocumentId, ProjectFileId = exported.Id, CreatedAt = Marker.AddHours(6) });
        ProjectFile ownSource = SeedFile(db, project, "userFiles", Marker.AddHours(-3));
        EditImageLayer ownLayer = SeedLayer(db, project, ownSource.Id, Marker.AddHours(-2));
        ProjectFile ownExport = SeedFile(db, project, "userFiles", Marker.AddHours(-1));
        db.EditImageExports.Add(new EditImageExport { Id = Guid.NewGuid(), DocumentId = ownLayer.DocumentId, ProjectFileId = ownExport.Id, CreatedAt = Marker.AddHours(-1) });
        db.SaveChanges();

        (await OutputGate.IsFileHeldAsync(db, exported, Ct)).Should().BeTrue();
        (await OutputGate.IsFileHeldAsync(db, ownExport, Ct)).Should().BeFalse("a user's own upload and its export stay available");
        (await OutputGate.HeldFileIdsAsync(db, project, new[] { exported, ownExport }, Ct)).Should().BeEquivalentTo(new[] { exported.Id });
    }

    [Fact]
    public async Task The_file_list_omits_held_files_and_keeps_later_and_own_ones()
    {
        using InferenceApiDbContext db = NewDb();
        Guid project = SeedProject(db, Marker);
        ProjectFile held = SeedFile(db, project, "outputFiles", Marker.AddHours(-3));
        ProjectFile later = SeedFile(db, project, "outputFiles", Marker.AddHours(5));
        ProjectFile own = SeedFile(db, project, "userFiles", Marker.AddHours(-3));
        HashSet<Guid> unavailable = await OutputGate.HeldFileIdsAsync(
            db, project, new[] { held, later, own }.Select(f => (f.Id, f.StorageKey, f.UploadedAt, f.OutputProducedAt)), Ct);
        unavailable.Should().BeEquivalentTo(new[] { held.Id });
    }

    [Fact]
    public async Task A_failed_read_holds_every_platform_file_in_a_list()
    {
        InferenceApiDbContext db = NewDb();
        Guid project = SeedProject(db, null);
        ProjectFile made = SeedFile(db, project, "outputFiles", DateTime.UtcNow);
        db.Dispose();
        (await OutputGate.HeldFileIdsAsync(db, project, new[] { made }, Ct)).Should().Contain(made.Id);
    }

    [Fact]
    public void The_refusal_for_a_held_layer_carries_no_key_or_url()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(OutputGate.Unavailable().Value);
        json.Should().NotContain("projects/").And.NotContain("http");
        OutputGate.ToolJson().Should().NotContain("projects/").And.NotContain("http");
    }
}
