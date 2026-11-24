using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>Copy-on-change, erase, quarantine, GC, hash sweeper and quota (contract sections 2, 3, 4, 6).</summary>
public class ProjectHistoryProtectionTests
{
    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Unchanged_file_costs_nothing_when_no_version_names_the_key()
    {
        using HistoryHarness h = new();
        Guid projectId = Guid.NewGuid();
        string key = $"projects/{projectId}/userFiles/x.txt";
        h.Store.Put(key, "old");

        await h.Decorator.DeleteAsync(projectId, key, CancellationToken.None);

        h.Store.Copies.Should().Be(0);
        h.Store.Objects.Should().NotContainKey(key);
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectHistoryBlobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Overwrite_of_a_versioned_key_copies_once_and_dedupes_by_content_id()
    {
        using HistoryHarness h = new();
        (Guid projectId, Guid fileId, string key) = await h.SeedVersionedFileAsync("v1", versions: 3);
        S3FileStorageService.UploadTarget target =
            S3FileStorageService.BuildUploadTarget(projectId, fileId, "a.txt", "userFiles", null);

        // Make the upload land on the captured key: the seed key is fileId-based, so seed the object there.
        h.Store.Objects[target.StorageKey] = h.Store.Objects[key];
        using (InferenceApiDbContext db = h.NewDb())
        {
            foreach (ProjectVersionItem item in db.ProjectVersionItems)
                item.StorageKey = target.StorageKey;
            await db.SaveChangesAsync();
        }

        await h.Decorator.UploadAsync(projectId, fileId, Bytes("v2"), "a.txt", "text/plain", null, CancellationToken.None);
        await h.Decorator.UploadAsync(projectId, fileId, Bytes("v3"), "a.txt", "text/plain", null, CancellationToken.None);

        h.Store.Copies.Should().Be(1, "three versions share one ContentId and the second overwrite has no captured bytes left");
        using InferenceApiDbContext verify = h.NewDb();
        ProjectHistoryBlob blob = await verify.ProjectHistoryBlobs.SingleAsync();
        blob.StorageKey.Should().StartWith($"projects/{projectId}/history/blobs/");
        Encoding.UTF8.GetString(h.Store.Objects[blob.StorageKey]).Should().Be("v1");
    }

    [Fact]
    public async Task Delete_and_move_are_protected()
    {
        using HistoryHarness h = new();
        (Guid projectId, _, string key) = await h.SeedVersionedFileAsync("v1");

        string moved = $"projects/{projectId}/userFiles/renamed.txt";
        await h.Decorator.MoveAsync(projectId, key, moved, CancellationToken.None);
        h.Store.Copies.Should().Be(1);
        h.Store.Objects.Should().ContainKey(moved);

        (Guid project2, _, string key2) = await h.SeedVersionedFileAsync("zz");
        await h.Decorator.DeleteManyAsync(project2, [key2], CancellationToken.None);
        h.Store.Copies.Should().Be(2);
        h.Store.Objects.Should().NotContainKey(key2);
    }

    [Fact]
    public async Task Etag_mismatch_writes_nothing_and_the_operation_still_runs()
    {
        using HistoryHarness h = new();
        (Guid projectId, _, string key) = await h.SeedVersionedFileAsync("v1");
        h.Store.Put(key, "changed behind the version's back");

        await h.Decorator.DeleteAsync(projectId, key, CancellationToken.None);

        h.Store.Copies.Should().Be(0);
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectHistoryBlobs.CountAsync()).Should().Be(0);
        h.Store.Objects.Should().NotContainKey(key);
    }

    [Fact]
    public async Task Protect_failure_blocks_the_operation()
    {
        using HistoryHarness h = new();
        (Guid projectId, _, string key) = await h.SeedVersionedFileAsync("v1");
        h.Store.CopyFailure = new InvalidOperationException("store down");

        Func<Task> act = () => h.Decorator.DeleteAsync(projectId, key, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        h.Store.Objects.Should().ContainKey(key, "the delete must not run when the old bytes could not be kept");
    }

    [Fact]
    public async Task Keys_outside_user_and_agent_files_pass_through()
    {
        using HistoryHarness h = new();
        (Guid projectId, _, string key) = await h.SeedVersionedFileAsync("v1", category: "outputFiles");

        await h.Decorator.DeleteAsync(projectId, key, CancellationToken.None);

        h.Store.Copies.Should().Be(0);
        h.Store.Objects.Should().NotContainKey(key);
    }

    [Fact]
    public async Task Suppress_scope_disables_protection()
    {
        using HistoryHarness h = new();
        (Guid projectId, _, string key) = await h.SeedVersionedFileAsync("v1");

        using (ProjectHistoryProtection.Suppress())
            await h.Decorator.DeleteAsync(projectId, key, CancellationToken.None);

        h.Store.Copies.Should().Be(0);
        ProjectHistoryProtection.IsSuppressed.Should().BeFalse();
    }

    [Fact]
    public async Task Overwrite_clears_the_hash_of_the_row_at_that_key()
    {
        using HistoryHarness h = new();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        S3FileStorageService.UploadTarget target =
            S3FileStorageService.BuildUploadTarget(projectId, fileId, "a.txt", "userFiles", null);
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() });
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = fileId,
                ProjectId = projectId,
                Category = "userFiles",
                OriginalFileName = "a.txt",
                StorageKey = target.StorageKey,
                StorageBucket = "t",
                MimeType = "text/plain",
                ContentSha256 = new string('b', 64),
                ContentSha256ETag = "e"
            });
            await db.SaveChangesAsync();
        }

        await h.Decorator.UploadAsync(projectId, fileId, Bytes("n"), "a.txt", "text/plain", null, CancellationToken.None);

        using InferenceApiDbContext verify = h.NewDb();
        ProjectFile row = await verify.ProjectFiles.SingleAsync();
        row.ContentSha256.Should().BeNull();
        row.ContentSha256ETag.Should().BeNull();
    }

    [Fact]
    public async Task Eraser_marks_items_erased_and_deletes_the_blob_for_shared_contents()
    {
        using HistoryHarness h = new();
        (Guid projectId, Guid fileId, string key) = await h.SeedVersionedFileAsync("v1");
        string blobKey = ProjectHistoryStorageKeys.BlobKey(projectId, ContentId.FromEtag(h.Store.ETagAt(key), 2));
        Guid twinId = Guid.NewGuid();
        using (InferenceApiDbContext db = h.NewDb())
        {
            string contentId = ContentId.FromEtag(h.Store.ETagAt(key), 2);
            h.Store.Put(blobKey, "v1");
            db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
            {
                ProjectId = projectId,
                ContentId = contentId,
                StorageKey = blobKey,
                SizeBytes = 2
            });
            // A different file with the same bytes shares the ContentId and must be erased too.
            HistoryHarness.AddVersion(db, projectId, 9, twinId, $"projects/{projectId}/userFiles/twin.txt", "e", contentId, 2);
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = h.ScopeFactory.CreateScope();
        InferenceApiDbContext scoped = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        ProjectHistoryEraser eraser = new(scoped, h.Store, NullLogger<ProjectHistoryEraser>.Instance);
        await eraser.EraseFileAsync(projectId, fileId, CancellationToken.None);

        using InferenceApiDbContext verify = h.NewDb();
        (await verify.ProjectVersionItems.AllAsync(i => i.ErasedAt != null)).Should().BeTrue();
        (await verify.ProjectHistoryBlobs.CountAsync()).Should().Be(0);
        h.Store.Objects.Should().NotContainKey(blobKey);
    }

    [Fact]
    public async Task Quarantine_moves_bytes_without_retaining_them_and_erases_history()
    {
        using HistoryHarness h = new();
        (Guid projectId, Guid fileId, string key) = await h.SeedVersionedFileAsync("bad");
        using IServiceScope scope = h.ScopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        ProjectFile file = await db.ProjectFiles.SingleAsync(f => f.Id == fileId);
        ProjectHistoryEraser eraser = new(db, h.Decorator, NullLogger<ProjectHistoryEraser>.Instance);
        CsamQuarantineService quarantine = new(h.Decorator, historyEraser: eraser);

        string destination = await quarantine.MoveAsync(file, CancellationToken.None);

        h.Store.Copies.Should().Be(0, "a quarantined object is never copied into history");
        h.Store.Objects.Should().ContainKey(destination).And.NotContainKey(key);
        using InferenceApiDbContext verify = h.NewDb();
        (await verify.ProjectVersionItems.AllAsync(i => i.ErasedAt != null)).Should().BeTrue();
        (await verify.ProjectHistoryBlobs.CountAsync()).Should().Be(0);
        h.Store.Objects.Keys.Should().NotContain(k => k.Contains("/history/"));
    }

    [Fact]
    public async Task Gc_frees_unreferenced_blobs_keeps_referenced_ones_and_drops_expired_versions()
    {
        using HistoryHarness h = new();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        string keepKey = ProjectHistoryStorageKeys.BlobKey(projectId, "etag:keep");
        string freeKey = ProjectHistoryStorageKeys.BlobKey(projectId, "etag:free");
        h.Store.Put(keepKey, "k");
        h.Store.Put(freeKey, "f");
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() });
            db.ProjectHistoryBlobs.AddRange(
                new ProjectHistoryBlob { ProjectId = projectId, ContentId = "etag:keep", StorageKey = keepKey, SizeBytes = 1 },
                new ProjectHistoryBlob { ProjectId = projectId, ContentId = "etag:free", StorageKey = freeKey, SizeBytes = 1 });
            HistoryHarness.AddVersion(db, projectId, 1, fileId, "k", "e", "etag:keep", 1);
            HistoryHarness.AddVersion(db, projectId, 2, fileId, "k", "e", "etag:free", 1, expiresAt: DateTime.UtcNow.AddDays(-1));
            db.ProjectHistoryDocuments.AddRange(
                new ProjectHistoryDocument { ProjectId = projectId, Sha256 = new string('1', 64), Json = "{}", SizeBytes = 2 },
                new ProjectHistoryDocument { ProjectId = projectId, Sha256 = new string('2', 64), Json = "{}", SizeBytes = 2 });
            db.ProjectVersionItems.Add(new ProjectVersionItem
            {
                Id = Guid.NewGuid(),
                VersionId = db.ProjectVersions.Local.First(v => v.Number == 1).Id,
                ProjectId = projectId,
                Kind = ProjectVersionItemKind.Workflow,
                ItemKey = "w",
                DocumentSha256 = new string('1', 64)
            });
            await db.SaveChangesAsync();
        }

        ProjectHistoryRetentionService gc = new(
            h.ScopeFactory, OptionsOf(new ProjectHistoryOptions()), NullLogger<ProjectHistoryRetentionService>.Instance);
        ProjectHistoryGcResult result = await gc.SweepOnceAsync(CancellationToken.None);

        result.Versions.Should().Be(1);
        result.Blobs.Should().Be(1);
        result.Documents.Should().Be(1);
        h.Store.Objects.Should().ContainKey(keepKey).And.NotContainKey(freeKey);
        using InferenceApiDbContext verify = h.NewDb();
        (await verify.ProjectHistoryBlobs.Select(b => b.ContentId).ToListAsync()).Should().Equal("etag:keep");
        (await verify.ProjectHistoryDocuments.Select(d => d.Sha256).ToListAsync()).Should().Equal(new string('1', 64));
    }

    [Fact]
    public async Task Hash_sweeper_saves_when_etags_agree_and_skips_when_the_file_changes_mid_read()
    {
        using HistoryHarness h = new();
        (Guid projectId, Guid fileId, string key) = await h.SeedVersionedFileAsync("hello");
        ProjectFileHashSweeper sweeper = new(
            h.ScopeFactory, OptionsOf(new ProjectHistoryOptions()), NullLogger<ProjectFileHashSweeper>.Instance);

        // Race: the second HEAD sees different bytes.
        h.Store.OnETagCall = n =>
        {
            if (n == 2)
                h.Store.Put(key, "overwritten while hashing");
        };
        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        using (InferenceApiDbContext db = h.NewDb())
            (await db.ProjectFiles.SingleAsync(f => f.Id == fileId)).ContentSha256.Should().BeNull();

        // Stable: the next sweep (calls 3 and 4) saves the hash of the bytes that are there now.
        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        using InferenceApiDbContext verify = h.NewDb();
        ProjectFile row = await verify.ProjectFiles.SingleAsync(f => f.Id == fileId);
        row.ContentSha256.Should().Be(Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("overwritten while hashing"))));
        row.ContentSha256ETag.Should().Be(h.Store.ETagAt(key));
        _ = projectId;
    }

    [Fact]
    public async Task Storage_usage_counts_history_blob_and_document_bytes()
    {
        using HistoryHarness h = new();
        Guid orgId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        using InferenceApiDbContext db = h.NewDb();
        db.Projects.Add(new Project { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = orgId });
        db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob { ProjectId = projectId, ContentId = "etag:a", StorageKey = "k", SizeBytes = 1000 });
        db.ProjectHistoryDocuments.Add(new ProjectHistoryDocument { ProjectId = projectId, Sha256 = new string('3', 64), Json = "{}", SizeBytes = 24 });
        Guid otherProject = Guid.NewGuid();
        db.Projects.Add(new Project { Id = otherProject, Name = "O", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() });
        db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob { ProjectId = otherProject, ContentId = "etag:b", StorageKey = "k2", SizeBytes = 7777 });
        await db.SaveChangesAsync();

        long used = await new StorageUsageService(db, Mock.Of<IOrganizationEntitlements>())
            .GetUsedBytesAsync(orgId, CancellationToken.None);

        used.Should().Be(1024);
    }

    [Fact]
    public async Task MoveIfMatch_protects_source_and_destination_and_clears_hashes_before_the_move()
    {
        using HistoryHarness h = new();
        (Guid projectId, Guid fileId, string sourceKey) = await h.SeedVersionedFileAsync("source-bytes");
        string destKey = $"projects/{projectId}/userFiles/dest.txt";
        h.Store.Put(destKey, "dest-bytes");
        string destEtag = h.Store.ETagAt(destKey);
        Guid destFileId = Guid.NewGuid();
        using (InferenceApiDbContext db = h.NewDb())
        {
            HistoryHarness.AddVersion(db, projectId, 2, destFileId, destKey, destEtag,
                ContentId.FromEtag(destEtag, "dest-bytes".Length), "dest-bytes".Length);
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = destFileId,
                ProjectId = projectId,
                Category = "userFiles",
                OriginalFileName = "dest.txt",
                StorageKey = destKey,
                StorageBucket = "t",
                MimeType = "text/plain",
                SizeBytes = "dest-bytes".Length,
                ContentSha256 = new string('d', 64),
                ContentSha256ETag = "old-dest"
            });
            ProjectFile source = await db.ProjectFiles.SingleAsync(f => f.Id == fileId);
            source.ContentSha256 = new string('s', 64);
            source.ContentSha256ETag = "old-source";
            await db.SaveChangesAsync();
        }

        string expected = h.Store.ETagAt(sourceKey);
        h.Store.BeforeMove = async () =>
        {
            using InferenceApiDbContext during = h.NewDb();
            (await during.ProjectFiles.SingleAsync(f => f.Id == fileId)).ContentSha256.Should().BeNull(
                "the source hash is cleared before the move");
            (await during.ProjectFiles.SingleAsync(f => f.Id == destFileId)).ContentSha256.Should().BeNull(
                "the destination hash is cleared before the move");
        };

        bool moved = await h.Decorator.MoveIfMatchAsync(
            projectId, sourceKey, destKey, expected, CancellationToken.None);

        moved.Should().BeTrue();
        h.Store.Copies.Should().Be(2, "both the source and the destination held captured bytes");
        Encoding.UTF8.GetString(h.Store.Objects[destKey]).Should().Be("source-bytes");
        using InferenceApiDbContext verify = h.NewDb();
        (await verify.ProjectFiles.SingleAsync(f => f.Id == fileId)).ContentSha256.Should().BeNull();
        (await verify.ProjectFiles.SingleAsync(f => f.Id == destFileId)).ContentSha256.Should().BeNull();
    }

    [Fact]
    public async Task Move_protects_the_destination_and_clears_both_hashes_before_the_move()
    {
        using HistoryHarness h = new();
        (Guid projectId, Guid fileId, string sourceKey) = await h.SeedVersionedFileAsync("source-bytes");
        string destKey = $"projects/{projectId}/userFiles/dest.txt";
        h.Store.Put(destKey, "dest-bytes");
        string destEtag = h.Store.ETagAt(destKey);
        Guid destFileId = Guid.NewGuid();
        using (InferenceApiDbContext db = h.NewDb())
        {
            HistoryHarness.AddVersion(db, projectId, 2, destFileId, destKey, destEtag,
                ContentId.FromEtag(destEtag, "dest-bytes".Length), "dest-bytes".Length);
            ProjectFile source = await db.ProjectFiles.SingleAsync(f => f.Id == fileId);
            source.ContentSha256 = new string('s', 64);
            source.ContentSha256ETag = "old-source";
            await db.SaveChangesAsync();
        }

        h.Store.BeforeMove = async () =>
        {
            using InferenceApiDbContext during = h.NewDb();
            (await during.ProjectFiles.SingleAsync(f => f.Id == fileId)).ContentSha256.Should().BeNull();
        };

        await h.Decorator.MoveAsync(projectId, sourceKey, destKey, CancellationToken.None);

        h.Store.Copies.Should().Be(2, "the destination's old bytes are protected before the move");
        Encoding.UTF8.GetString(h.Store.Objects[destKey]).Should().Be("source-bytes");
        using InferenceApiDbContext verify = h.NewDb();
        (await verify.ProjectFiles.SingleAsync(f => f.Id == fileId)).ContentSha256.Should().BeNull();
    }

    [Fact]
    public async Task Copy_protects_the_destination_and_clears_its_hash()
    {
        using HistoryHarness h = new();
        (Guid projectId, Guid fileId, string destKey) = await h.SeedVersionedFileAsync("old-dest");
        string sourceKey = $"projects/{projectId}/userFiles/source.txt";
        h.Store.Put(sourceKey, "new-bytes");
        using (InferenceApiDbContext db = h.NewDb())
        {
            ProjectFile row = await db.ProjectFiles.SingleAsync(f => f.Id == fileId);
            row.ContentSha256 = new string('d', 64);
            row.ContentSha256ETag = "old";
            await db.SaveChangesAsync();
        }

        await h.Decorator.CopyAsync(projectId, sourceKey, destKey, CancellationToken.None);

        h.Store.Copies.Should().Be(2, "one copy protects the destination, then the copy itself runs");
        Encoding.UTF8.GetString(h.Store.Objects[destKey]).Should().Be("new-bytes");
        using InferenceApiDbContext verify = h.NewDb();
        ProjectHistoryBlob blob = await verify.ProjectHistoryBlobs.SingleAsync();
        Encoding.UTF8.GetString(h.Store.Objects[blob.StorageKey]).Should().Be("old-dest");
        (await verify.ProjectFiles.SingleAsync(f => f.Id == fileId)).ContentSha256.Should().BeNull();
    }

    [Fact]
    public async Task Gc_keeps_a_blob_shared_by_an_expired_and_a_live_version()
    {
        using HistoryHarness h = new();
        Guid projectId = Guid.NewGuid();
        string contentId = "etag:shared";
        string blobKey = ProjectHistoryStorageKeys.BlobKey(projectId, contentId);
        h.Store.Put(blobKey, "shared");
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() });
            db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
            {
                ProjectId = projectId,
                ContentId = contentId,
                StorageKey = blobKey,
                SizeBytes = 6
            });
            HistoryHarness.AddVersion(db, projectId, 1, Guid.NewGuid(), "k-expired", "e", contentId, 6,
                expiresAt: DateTime.UtcNow.AddDays(-1));
            HistoryHarness.AddVersion(db, projectId, 2, Guid.NewGuid(), "k-live", "e", contentId, 6);
            await db.SaveChangesAsync();
        }

        ProjectHistoryRetentionService gc = new(
            h.ScopeFactory, OptionsOf(new ProjectHistoryOptions()), NullLogger<ProjectHistoryRetentionService>.Instance);
        ProjectHistoryGcResult result = await gc.SweepOnceAsync(CancellationToken.None);

        result.Versions.Should().Be(1, "the expired version goes");
        result.Blobs.Should().Be(0, "the live version still names the blob's bytes");
        h.Store.Objects.Should().ContainKey(blobKey);
        using InferenceApiDbContext verify = h.NewDb();
        (await verify.ProjectHistoryBlobs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Project_delete_removes_every_object_under_the_history_prefix()
    {
        using HistoryHarness h = new();
        Guid projectId = Guid.NewGuid();
        string rowKey = ProjectHistoryStorageKeys.BlobKey(projectId, "etag:row");
        string orphanKey = $"projects/{projectId}/history/blobs/orphan";
        h.Store.Put(rowKey, "row");
        h.Store.Put(orphanKey, "orphan");
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() });
            db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
            {
                ProjectId = projectId,
                ContentId = "etag:row",
                StorageKey = rowKey,
                SizeBytes = 3
            });
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = h.ScopeFactory.CreateScope();
        InferenceApiDbContext scoped = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        ProjectHistoryEraser eraser = new(
            scoped, h.Store, NullLogger<ProjectHistoryEraser>.Instance, h.Store);

        await eraser.DeleteProjectObjectsAsync(projectId, CancellationToken.None);

        h.Store.Objects.Keys.Should().NotContain(k => k.StartsWith($"projects/{projectId}/history/"),
            "the prefix listing finds objects no blob row names");
    }

    [Fact]
    public async Task Project_delete_fails_when_a_history_object_survives()
    {
        using HistoryHarness h = new();
        Guid projectId = Guid.NewGuid();
        string rowKey = ProjectHistoryStorageKeys.BlobKey(projectId, "etag:row");
        h.Store.Put(rowKey, "row");
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project { Id = projectId, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() });
            db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
            {
                ProjectId = projectId,
                ContentId = "etag:row",
                StorageKey = rowKey,
                SizeBytes = 3
            });
            await db.SaveChangesAsync();
        }

        h.Store.DeleteManyShortfall = 1;
        using IServiceScope scope = h.ScopeFactory.CreateScope();
        InferenceApiDbContext scoped = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        ProjectHistoryEraser eraser = new(
            scoped, h.Store, NullLogger<ProjectHistoryEraser>.Instance, h.Store);

        Func<Task> act = () => eraser.DeleteProjectObjectsAsync(projectId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a surviving object must keep the project so it can be retried");
    }

    [Fact]
    public async Task Hash_sweeper_does_not_overwrite_a_hash_written_meanwhile()
    {
        using HistoryHarness h = new();
        (Guid projectId, Guid fileId, string key) = await h.SeedVersionedFileAsync("hello");
        ProjectFileHashSweeper sweeper = new(
            h.ScopeFactory, OptionsOf(new ProjectHistoryOptions()), NullLogger<ProjectFileHashSweeper>.Instance);
        string concurrentHash = new('f', 64);

        h.Store.OnETagCall = n =>
        {
            if (n != 2)
                return;

            using InferenceApiDbContext db = h.NewDb();
            ProjectFile row = db.ProjectFiles.Single(f => f.Id == fileId);
            row.ContentSha256 = concurrentHash;
            row.ContentSha256ETag = "other-writer";
            db.SaveChanges();
        };

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(0,
            "the conditional update sees the hash is no longer null");

        using InferenceApiDbContext verify = h.NewDb();
        ProjectFile stored = await verify.ProjectFiles.SingleAsync(f => f.Id == fileId);
        stored.ContentSha256.Should().Be(concurrentHash);
        stored.ContentSha256ETag.Should().Be("other-writer");
        _ = projectId;
        _ = key;
    }

    private static Microsoft.Extensions.Options.IOptionsMonitor<ProjectHistoryOptions> OptionsOf(ProjectHistoryOptions value) =>
        Mock.Of<Microsoft.Extensions.Options.IOptionsMonitor<ProjectHistoryOptions>>(m => m.CurrentValue == value);
}
