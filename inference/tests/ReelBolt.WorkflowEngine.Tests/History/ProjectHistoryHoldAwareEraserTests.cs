using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>
/// T-027: "Delete forever" is hold-aware. While a project's output hold is active (or unreadable)
/// it never marks erased nor deletes a content that a platform-made item of the project names,
/// whatever key captured it; the other contents the erase touches still go. A quarantine erase is
/// the platform's own removal and still erases, even when it conflicts with the hold rule.
/// </summary>
public class ProjectHistoryHoldAwareEraserTests
{
    [Fact]
    public async Task Delete_forever_keeps_a_shared_held_content_but_erases_the_rest()
    {
        string dbName = $"history-hold-eraser-{Guid.NewGuid():N}";
        HistoryTestStore store = new();
        Guid projectId = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();
        Guid otherId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        string targetKey = targetId.ToString("D");

        using (InferenceApiDbContext db = NewDb(dbName))
        {
            db.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Held",
                OwnerId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid(),
                CsamOutputHoldAt = now.AddMinutes(-5)
            });
            db.ProjectFiles.Add(File(projectId, targetId, "userFiles", CsamScanStatus.NotScanned));

            ProjectVersion first = Version(db, projectId, 1);
            ProjectVersion second = Version(db, projectId, 2);
            db.ProjectVersionItems.AddRange(
                Item(first, projectId, targetId, "etag:shared", platformMade: false),
                Item(second, projectId, targetId, "etag:free", platformMade: false),
                Item(second, projectId, otherId, "etag:shared", platformMade: true));

            AddBlob(db, store, projectId, "etag:shared");
            AddBlob(db, store, projectId, "etag:free");
            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = NewDb(dbName))
        {
            await Eraser(db, store).EraseFileAsync(projectId, targetId, CancellationToken.None);
        }

        using (InferenceApiDbContext verify = NewDb(dbName))
        {
            List<ProjectVersionItem> items = await verify.ProjectVersionItems.AsNoTracking().ToListAsync();
            items.Single(i => i.ItemKey == targetKey && i.ContentId == "etag:shared")
                .ErasedAt.Should().BeNull("the content is named by a platform-made item of another key");
            items.Single(i => i.ItemKey == targetKey && i.ContentId == "etag:free")
                .ErasedAt.Should().NotBeNull("a content no platform-made item names is still erased");
            items.Single(i => i.ItemKey == otherId.ToString("D"))
                .ErasedAt.Should().BeNull("the platform-made item itself is never touched");

            (await verify.ProjectHistoryBlobs.Select(b => b.ContentId).ToListAsync())
                .Should().BeEquivalentTo(["etag:shared"], "only the unheld blob row is freed");
        }

        store.Objects.Keys.Should().Contain(k => k.Contains("etag-shared", StringComparison.Ordinal));
        store.Objects.Keys.Should().NotContain(k => k.Contains("etag-free", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_quarantine_erase_still_erases_the_content_even_when_another_key_is_platform_made()
    {
        string dbName = $"history-hold-quarantine-{Guid.NewGuid():N}";
        HistoryTestStore store = new();
        Guid projectId = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();
        Guid otherId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        using (InferenceApiDbContext db = NewDb(dbName))
        {
            db.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Held",
                OwnerId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid(),
                CsamOutputHoldAt = now.AddMinutes(-5)
            });
            db.ProjectFiles.Add(File(projectId, targetId, "userFiles", CsamScanStatus.Quarantined));

            ProjectVersion first = Version(db, projectId, 1);
            db.ProjectVersionItems.Add(Item(first, projectId, targetId, "etag:shared", platformMade: false));

            // Another key captured the same content as a platform-made item, which the hold would
            // otherwise protect; quarantine must win over that conflict.
            ProjectVersion second = Version(db, projectId, 2);
            db.ProjectVersionItems.Add(Item(second, projectId, otherId, "etag:shared", platformMade: true));

            AddBlob(db, store, projectId, "etag:shared");
            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = NewDb(dbName))
        {
            await Eraser(db, store).EraseFileAsync(projectId, targetId, CancellationToken.None);
        }

        using (InferenceApiDbContext verify = NewDb(dbName))
        {
            List<ProjectVersionItem> items = await verify.ProjectVersionItems.AsNoTracking().ToListAsync();
            items.Should().OnlyContain(i => i.ErasedAt != null,
                "quarantine wins over the hold rule: the platform's own removal still erases");
            (await verify.ProjectHistoryBlobs.CountAsync()).Should().Be(0);
        }

        store.Objects.Should().NotContainKey($"projects/{projectId}/history/blobs/etag-shared");
    }

    private static InferenceApiDbContext NewDb(string databaseName) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options);

    private static ProjectHistoryEraser Eraser(InferenceApiDbContext db, HistoryTestStore store) =>
        new(db, store, NullLogger<ProjectHistoryEraser>.Instance);

    private static ProjectFile File(Guid projectId, Guid fileId, string category, CsamScanStatus status) => new()
    {
        Id = fileId,
        ProjectId = projectId,
        Category = category,
        OriginalFileName = "a.txt",
        OriginalPath = "a.txt",
        StorageKey = $"projects/{projectId}/{category}/{fileId:N}.txt",
        StorageBucket = "t",
        MimeType = "text/plain",
        SizeBytes = 5,
        CsamStatus = status
    };

    private static ProjectVersion Version(InferenceApiDbContext db, Guid projectId, int number)
    {
        ProjectVersion version = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Number = number,
            Cause = ProjectVersionCause.Auto,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTime.UtcNow.AddDays(10),
            ManifestDigest = new string('a', 64)
        };
        db.ProjectVersions.Add(version);
        return version;
    }

    private static ProjectVersionItem Item(
        ProjectVersion version, Guid projectId, Guid fileId, string contentId, bool platformMade) => new()
        {
            Id = Guid.NewGuid(),
            VersionId = version.Id,
            ProjectId = projectId,
            Kind = ProjectVersionItemKind.File,
            ItemKey = fileId.ToString("D"),
            Name = "a.txt",
            Category = "userFiles",
            SizeBytes = 5,
            ContentId = contentId,
            StorageKey = $"projects/{projectId}/userFiles/{fileId:N}.txt",
            PlatformMade = platformMade,
            OutputProducedAt = platformMade ? DateTime.UtcNow.AddDays(-3) : null
        };

    private static void AddBlob(
        InferenceApiDbContext db, HistoryTestStore store, Guid projectId, string contentId)
    {
        string key = $"projects/{projectId}/history/blobs/{contentId.Replace(':', '-')}";
        store.Put(key, "bytes");
        db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
        {
            ProjectId = projectId,
            ContentId = contentId,
            StorageKey = key,
            SizeBytes = 5
        });
    }
}
