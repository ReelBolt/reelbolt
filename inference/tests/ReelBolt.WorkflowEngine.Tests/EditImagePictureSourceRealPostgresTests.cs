#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.AI;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public partial class EditImageDocumentControllersTests
{
    /// <summary>
    /// D10 on a real Postgres with every API migration applied (skipped unless <c>REELBOLT_TEST_PG</c> is set):
    /// the <c>edit_image_documents.source_project_file_id</c> FK is SET NULL, so a file row deleted by any path
    /// leaves the picture in place; and the Files delete handler gives every picture its own copy, repoints its
    /// layers and deletes the original.
    /// </summary>
    [Fact]
    public async Task Copy_on_delete_and_the_set_null_foreign_key_hold_on_a_real_postgres()
    {
        string? baseConnection = Environment.GetEnvironmentVariable("REELBOLT_TEST_PG");
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_d10_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(baseConnection) { Database = dbName };
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql(csb.ConnectionString).Options;
        try
        {
            Guid ownerId = Guid.NewGuid();
            Guid orgId = TestTenancy.OrgOf(ownerId);
            Guid projectId = Guid.NewGuid();
            byte[] photo = PngOfSize(1080, 1920);
            ProjectFile kept = NewPhoto(projectId, "kept.png", photo.Length);
            ProjectFile dropped = NewPhoto(projectId, "dropped.png", photo.Length);
            Guid keptPicture = Guid.NewGuid();
            Guid droppedPicture = Guid.NewGuid();
            FakeStorage storage = new();
            storage.Objects[kept.StorageKey] = photo;
            storage.Objects[dropped.StorageKey] = photo;

            await using (InferenceApiDbContext setup = new(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync();
                DateTime now = DateTime.UtcNow;
                setup.ApplicationUsers.Add(new ApplicationUser
                {
                    Id = ownerId,
                    Email = $"{ownerId:N}@example.com",
                    DisplayName = "Ada",
                    PasswordHash = "hash",
                    CreatedAt = now,
                    EmailVerifiedAt = now
                });
                setup.Organizations.Add(new Organization
                {
                    Id = orgId,
                    Name = "o",
                    Slug = "o-" + orgId.ToString("N"),
                    Kind = OrganizationKind.Personal,
                    CreatedByUserId = ownerId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                setup.OrganizationMemberships.Add(new OrganizationMembership
                {
                    OrganizationId = orgId,
                    UserId = ownerId,
                    Role = OrganizationRole.Owner,
                    CreatedAt = now
                });
                setup.Projects.Add(new Project { Id = projectId, Name = "P", OwnerId = ownerId, OrganizationId = orgId, CreatedAt = now, UpdatedAt = now });
                setup.ProjectFiles.AddRange(kept, dropped);
                setup.EditImageDocuments.Add(NewPicture(keptPicture, kept, withLayer: true));
                // a legacy picture whose backdrop no longer shows the file: only the document row links to it
                setup.EditImageDocuments.Add(NewPicture(droppedPicture, dropped, withLayer: false));
                await setup.SaveChangesAsync();
            }

            // 1. The FK action alone: a file row removed by plain SQL (any path, no copy) leaves its picture.
            await using (InferenceApiDbContext raw = new(options))
            {
                int removed = await raw.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM project_files WHERE id = {dropped.Id}");
                removed.Should().Be(1);
            }

            await using (InferenceApiDbContext read = new(options))
            {
                EditImageDocument survivor = await read.EditImageDocuments.AsNoTracking().SingleAsync(d => d.Id == droppedPicture);
                survivor.SourceProjectFileId.Should().BeNull("the FK is SET NULL, no longer a cascade");
            }

            // 2. The Files delete handler: copy, repoint, then delete the original.
            await using (InferenceApiDbContext db = new(options))
            {
                Mock<ICurrentUser> user = new();
                user.SetupGet(u => u.UserId).Returns(ownerId);
                user.SetupGet(u => u.IsAuthenticated).Returns(true);
                user.SetupGet(u => u.OrganizationId).Returns(orgId);
                user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Owner);
                ProjectFilesController files = new(
                    db, user.Object, TestTenancy.Access(db, user.Object), storage,
                    new ChannelBackgroundTaskQueue<FileSummarizationTask>(4), Mock.Of<IPublishEndpoint>(),
                    new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()))
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };

                (int usageStatus, System.Text.Json.JsonElement usage) = Harness.Read(await files.GetPictureUsage(projectId, kept.Id, CancellationToken.None));
                usageStatus.Should().Be(200);
                usage.GetProperty("pictures").GetInt32().Should().Be(1);

                Harness.Read(await files.Delete(projectId, kept.Id, CancellationToken.None)).Status.Should().Be(204);
            }

            await using (InferenceApiDbContext read = new(options))
            {
                (await read.ProjectFiles.AnyAsync(f => f.ProjectId == projectId)).Should().BeFalse();
                EditImageDocument picture = await read.EditImageDocuments.AsNoTracking().Include(d => d.Layers)
                    .SingleAsync(d => d.Id == keptPicture);
                picture.SourceProjectFileId.Should().BeNull();
                EditImageLayer layer = picture.Layers.Should().ContainSingle().Subject;
                layer.StorageKey.Should().StartWith($"projects/{projectId}/agentFiles/image-editor/{keptPicture}/source-");
                layer.CopySizeBytes.Should().Be(photo.Length);
                layer.SourceProjectFileId.Should().BeNull();
                storage.Objects.Should().NotContainKey(kept.StorageKey);
                storage.Objects[layer.StorageKey].Should().Equal(photo);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static ProjectFile NewPhoto(Guid projectId, string name, long size) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        OriginalFileName = name,
        OriginalPath = name,
        Category = "userFiles",
        StorageKey = $"projects/{projectId}/userFiles/{name}",
        StorageBucket = "test-bucket",
        MimeType = "image/png",
        SizeBytes = size,
        UploadedAt = DateTime.UtcNow
    };

    private static EditImageDocument NewPicture(Guid id, ProjectFile file, bool withLayer)
    {
        EditImageDocument document = new()
        {
            Id = id,
            ProjectId = file.ProjectId,
            SourceProjectFileId = file.Id,
            CanvasWidth = 1080,
            CanvasHeight = 1920,
            Revision = 1
        };
        if (withLayer)
        {
            document.Layers.Add(new EditImageLayer
            {
                Id = Guid.NewGuid(),
                DocumentId = id,
                ZOrder = 1,
                Role = EditImageLayerRoles.Background,
                Kind = EditImageLayerKind.Source,
                Label = file.OriginalFileName,
                SourceProjectFileId = file.Id,
                StorageKey = file.StorageKey,
                StorageBucket = "test-bucket",
                MimeType = file.MimeType,
                TransformJson = "{}"
            });
        }

        return document;
    }
}
