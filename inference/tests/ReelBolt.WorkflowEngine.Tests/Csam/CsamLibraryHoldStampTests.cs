#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Avatars;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// The two SET-BASED verdict writers (the matcher's relational <c>ExecuteUpdate</c> and the project marker's) are what
/// production runs: each stamps the workspace avatar library's hold marker in the verdict's own transaction.
/// </summary>
public class CsamLibraryHoldStampTests
{
    private const string Digest = "cc11bb22cc33dd44ee55ff6600778899aa11bb22cc33dd44ee55ff6600778899";

    private static (SqliteConnection Connection, InferenceApiDbContext Db) NewSqlite()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        InferenceApiDbContext db = new SqliteLibraryDb(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        return (connection, db);
    }

    private static Guid SeedCopy(InferenceApiDbContext db, Guid org)
    {
        AvatarLibraryItem item = AvatarLibraryPublishTests.SeedItem(org, AvatarSubjectKind.Person, "Anna");
        item.RowVersion = 1;
        var reference = new AvatarLibraryReference
        {
            Id = Guid.NewGuid(),
            LibraryItemId = item.Id,
            Kind = AvatarReferenceKind.Image,
            Role = AvatarReferenceRole.Face,
            ContentSha256 = Digest,
            StorageKey = $"organizations/{org}/avatar-library/{item.Id}/x.png",
            MimeType = "image/png",
            FileName = "a.png",
            SizeBytes = 1
        };
        db.AvatarLibraryItems.Add(item);
        db.AvatarLibraryReferences.Add(reference);
        db.SaveChanges();
        return reference.Id;
    }

    private static AvatarLibraryReference Row(InferenceApiDbContext db, Guid id)
    {
        db.ChangeTracker.Clear();
        return db.AvatarLibraryReferences.IgnoreQueryFilters().AsNoTracking().Single(r => r.Id == id);
    }

    [Fact]
    public async Task A_csam_match_through_the_relational_path_stamps_this_organizations_copy_only()
    {
        foreach (bool match in new[] { true, false })
        {
            (SqliteConnection connection, InferenceApiDbContext db) = NewSqlite();
            using (connection)
            using (db)
            {
                Guid org = Guid.NewGuid();
                ProjectFile file = CsamTestSupport.AddFile(db, mimeType: "video/mp4", fileName: "clip.mp4", bytes: [1, 2, 3, 4]);
                db.Projects.Add(new Project { Id = file.ProjectId, Name = "p", OrganizationId = org });
                (await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id)).ContentSha256 = Digest;
                await db.SaveChangesAsync();
                Guid mine = SeedCopy(db, org);
                Guid foreign = SeedCopy(db, Guid.NewGuid());

                CsamOptions options = CsamTestSupport.EnabledOptions();
                CsamTestStorage storage = new();
                storage.Put(file.StorageKey, [1, 2, 3, 4]);
                CsamFakeServer server = new() { MediaClassification = match ? "csam" : "no-known-match" };
                using var handler = new CsamRecordingHandler(server);
                using var http = CsamTestSupport.NewHttpClient(options, handler);
                var matcher = new CsamHashMatcher(
                    db, storage, new PdqHasher(), new ArachnidShieldClient(http, Options.Create(options)),
                    new CsamQuarantineService(storage), Options.Create(options), new CsamListLogger<CsamHashMatcher>());

                await matcher.ScanAsync(file, actorUserId: null, CancellationToken.None);

                if (match)
                {
                    Row(db, mine).HeldReason.Should().Be("csam");
                    Row(db, mine).HeldAt.Should().NotBeNull();
                }
                else
                {
                    Row(db, mine).HeldAt.Should().BeNull("a clean scan holds nothing");
                }

                Row(db, foreign).HeldAt.Should().BeNull("another organization's copy of the same bytes is never held by this file");
            }
        }
    }

    [Fact]
    public async Task The_set_based_project_marker_stamps_copies_of_the_projects_platform_outputs()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqlite();
        using (connection)
        using (db)
        {
            Guid org = Guid.NewGuid();
            ProjectFile file = CsamTestSupport.AddFile(db, fileName: "render.mp4", mimeType: "video/mp4", bytes: [1, 2, 3, 4]);
            db.Projects.Add(new Project { Id = file.ProjectId, Name = "p", OrganizationId = org });
            ProjectFile row = await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id);
            row.ContentSha256 = Digest;
            row.OutputProducedAt = DateTime.UtcNow.AddHours(-1);
            row.CsamStatus = CsamScanStatus.Clean;
            await db.SaveChangesAsync();
            Guid mine = SeedCopy(db, org);
            Guid foreign = SeedCopy(db, Guid.NewGuid());

            await ProjectContentQuarantineMarker.StampAsync(db, file.ProjectId, DateTime.UtcNow, CancellationToken.None);

            Row(db, mine).HeldReason.Should().Be("content_marker");
            Row(db, foreign).HeldAt.Should().BeNull();
        }
    }
}
