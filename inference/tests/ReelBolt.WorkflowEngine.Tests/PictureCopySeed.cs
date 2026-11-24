#nullable enable
using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Seeds a picture (image-editor document) whose backdrop shows a project file, and checks afterwards that the
/// picture kept its own copy (decision D10) — shared by the tests of every path that deletes project files.
/// </summary>
internal static class PictureCopySeed
{
    /// <summary>A live picture over <paramref name="file"/> with one Source layer on its storage key.</summary>
    public static async Task<Guid> PictureOverAsync(InferenceApiDbContext db, ProjectFile file)
    {
        Guid documentId = Guid.NewGuid();
        db.EditImageDocuments.Add(new EditImageDocument
        {
            Id = documentId,
            ProjectId = file.ProjectId,
            SourceProjectFileId = file.Id,
            Revision = 1,
            Layers =
            [
                new EditImageLayer
                {
                    Id = Guid.NewGuid(),
                    DocumentId = documentId,
                    ZOrder = 1,
                    Role = EditImageLayerRoles.Background,
                    Kind = EditImageLayerKind.Source,
                    Label = file.OriginalFileName,
                    SourceProjectFileId = file.Id,
                    StorageKey = file.StorageKey,
                    StorageBucket = "test-bucket",
                    MimeType = "image/png",
                    TransformJson = "{}"
                }
            ]
        });
        await db.SaveChangesAsync();
        return documentId;
    }

    /// <summary>The picture survives, unlinked from the file, with its layer on its own copy; returns the copy's key.</summary>
    public static async Task<string> AssertKeptOwnCopyAsync(InferenceApiDbContext db, Guid projectId, Guid documentId, string originalKey)
    {
        EditImageDocument document = await db.EditImageDocuments.AsNoTracking().Include(d => d.Layers)
            .SingleAsync(d => d.Id == documentId);
        document.SourceProjectFileId.Should().BeNull();
        EditImageLayer layer = document.Layers.Should().ContainSingle().Subject;
        layer.StorageKey.Should().NotBe(originalKey);
        layer.StorageKey.Should().StartWith($"projects/{projectId}/agentFiles/image-editor/{documentId}/source-");
        layer.SourceProjectFileId.Should().BeNull();
        layer.CopySizeBytes.Should().NotBeNull();
        return layer.StorageKey;
    }
}
