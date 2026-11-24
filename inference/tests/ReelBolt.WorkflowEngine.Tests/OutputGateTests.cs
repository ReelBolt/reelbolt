using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The one gate that decides whether a step output or a platform-made file is served.</summary>
public sealed class OutputGateTests
{
    private static readonly DateTime Marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (Guid Project, Guid Result) Seed(InferenceApiDbContext db, DateTime? marker, DateTime executedAt, DateTime startedAt)
    {
        Guid projectId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        db.Projects.Add(new Project { Id = projectId, Name = "p", CsamOutputHoldAt = marker });
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = projectId, StartedAt = startedAt });
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = resultId,
            WorkflowExecutionId = executionId,
            ExecutedAt = executedAt,
            OutputStorageKey = $"projects/{projectId}/outputFiles/a.mp4",
        });
        db.SaveChanges();
        return (projectId, resultId);
    }

    [Fact]
    public async Task Output_made_before_the_marker_is_held()
    {
        using InferenceApiDbContext db = NewDb();
        var (_, result) = Seed(db, Marker, Marker.AddHours(-2), Marker.AddHours(-2));
        (await OutputGate.IsResultHeldAsync(db, result, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Output_made_well_after_the_marker_is_served()
    {
        using InferenceApiDbContext db = NewDb();
        var (_, result) = Seed(db, Marker, Marker.AddHours(2), Marker.AddHours(2));
        (await OutputGate.IsResultHeldAsync(db, result, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Project_without_a_marker_serves_everything()
    {
        using InferenceApiDbContext db = NewDb();
        var (_, result) = Seed(db, null, Marker.AddHours(-2), Marker.AddHours(-2));
        (await OutputGate.IsResultHeldAsync(db, result, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_marker_read_holds_the_output()
    {
        InferenceApiDbContext db = NewDb();
        var (_, result) = Seed(db, null, Marker, Marker);
        // Disposed context: every read fails, and the gate must fail closed.
        Guid project = db.Projects.Select(p => p.Id).First();
        db.Dispose();
        ProjectOutputHoldState state = await ProjectOutputHold.ReadAsync(db, project, CancellationToken.None);
        state.Unreadable.Should().BeTrue();
        state.HoldsOutput(DateTime.UtcNow.AddYears(1), DateTime.UtcNow.AddYears(1)).Should().BeTrue();
    }

    [Fact]
    public async Task A_users_own_upload_stays_available_and_a_held_platform_file_does_not()
    {
        using InferenceApiDbContext db = NewDb();
        var (projectId, _) = Seed(db, Marker, Marker.AddHours(-2), Marker.AddHours(-2));
        ProjectFile own = new() { Id = Guid.NewGuid(), ProjectId = projectId, StorageKey = $"projects/{projectId}/userFiles/mine.mp4", UploadedAt = Marker.AddHours(-3) };
        ProjectFile made = new() { Id = Guid.NewGuid(), ProjectId = projectId, StorageKey = $"projects/{projectId}/outputFiles/b.mp4", UploadedAt = Marker.AddHours(-3) };
        (await OutputGate.IsFileHeldAsync(db, own, CancellationToken.None)).Should().BeFalse();
        (await OutputGate.IsFileHeldAsync(db, made, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public void The_signed_in_answer_is_the_neutral_not_found_and_carries_no_key()
    {
        var result = OutputGate.Unavailable();
        result.StatusCode.Should().Be(404);
        System.Text.Json.JsonSerializer.Serialize(result.Value).Should().Contain(OutputHoldResponse.Code).And.NotContain("projects/");
    }

    private static (Guid Project, EditImageDocument Doc) SeedPicture(InferenceApiDbContext db, DateTime marker)
    {
        Guid projectId = Guid.NewGuid();
        db.Projects.Add(new Project { Id = projectId, Name = "p", CsamOutputHoldAt = marker });
        // The picture's source file is the user's own upload, made after the marker: clean.
        ProjectFile clean = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "a.png",
            MimeType = "image/png",
            StorageKey = $"projects/{projectId}/userFiles/a.png",
            Category = "userFiles",
            UploadedAt = marker.AddDays(2),
        };
        EditImageDocument doc = new() { Id = Guid.NewGuid(), ProjectId = projectId, SourceProjectFileId = clean.Id, CanvasWidth = 10, CanvasHeight = 10 };
        db.ProjectFiles.Add(clean);
        db.EditImageDocuments.Add(doc);
        db.SaveChanges();
        return (projectId, doc);
    }

    private static EditImageLayer Layer(EditImageDocument doc, DateTime createdAt, Guid? sourceFileId = null) => new()
    {
        Id = Guid.NewGuid(),
        DocumentId = doc.Id,
        Kind = EditImageLayerKind.Generated,
        Label = "l",
        TransformJson = "{}",
        StorageKey = $"projects/{doc.ProjectId}/thumbnails/{Guid.NewGuid():N}.png",
        CreatedAt = createdAt,
        SourceProjectFileId = sourceFileId,
    };

    [Fact]
    public async Task A_layer_with_no_link_of_its_own_made_before_the_marker_is_held_though_the_pictures_source_is_clean()
    {
        using InferenceApiDbContext db = NewDb();
        var (project, doc) = SeedPicture(db, Marker);
        EditImageLayer old = Layer(doc, Marker.AddHours(-5));
        EditImageLayer later = Layer(doc, Marker.AddDays(1));
        (await OutputGate.IsPictureLayerHeldAsync(db, project, old, CancellationToken.None)).Should().BeTrue();
        (await OutputGate.IsPictureLayerHeldAsync(db, project, later, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_layer_linked_to_a_clean_file_of_its_own_is_judged_by_that_file()
    {
        using InferenceApiDbContext db = NewDb();
        var (project, doc) = SeedPicture(db, Marker);
        EditImageLayer linked = Layer(doc, Marker.AddHours(-5), doc.SourceProjectFileId);
        linked.Kind = EditImageLayerKind.Source;
        (await OutputGate.IsPictureLayerHeldAsync(db, project, linked, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(EditImageLayerKind.Generated)]
    [InlineData(EditImageLayerKind.Cutout)]
    public async Task A_generated_or_cutout_layer_is_judged_by_its_own_stamp_whatever_link_it_inherited(EditImageLayerKind kind)
    {
        using InferenceApiDbContext db = NewDb();
        var (project, doc) = SeedPicture(db, Marker);
        EditImageLayer old = Layer(doc, Marker.AddHours(-5), doc.SourceProjectFileId);
        old.Kind = kind;
        EditImageLayer later = Layer(doc, Marker.AddDays(1), doc.SourceProjectFileId);
        later.Kind = kind;
        (await OutputGate.IsPictureLayerHeldAsync(db, project, old, CancellationToken.None)).Should().BeTrue();
        (await OutputGate.IsPictureLayerHeldAsync(db, project, later, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task An_output_key_with_no_row_and_no_owner_is_held_only_while_the_project_has_a_marker()
    {
        using InferenceApiDbContext db = NewDb();
        var (project, _) = SeedPicture(db, Marker);
        string orphan = $"projects/{project}/outputFiles/{Guid.NewGuid():N}.mp4";
        (await OutputGate.IsKeyHeldAsync(db, project, orphan, CancellationToken.None)).Should().BeTrue();

        (await db.Projects.SingleAsync(p => p.Id == project)).CsamOutputHoldAt = null;
        await db.SaveChangesAsync();
        (await OutputGate.IsKeyHeldAsync(db, project, orphan, CancellationToken.None)).Should().BeFalse();
    }
}
