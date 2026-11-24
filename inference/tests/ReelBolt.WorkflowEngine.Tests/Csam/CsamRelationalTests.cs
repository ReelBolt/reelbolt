using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// The two production-only branches (a relational <c>ExecuteUpdate</c> and the sweeper's atomic
/// claim) are exercised on SQLite, not only on the InMemory fallback the rest of the suite uses.
/// </summary>
public class CsamRelationalTests
{
    private static (SqliteConnection Connection, InferenceApiDbContext Db) NewSqlite()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection)
            .Options);
        db.Database.EnsureCreated();
        return (connection, db);
    }

    [Fact]
    public async Task ConditionalVerdictWrite_OnSqlite_DoesNotDowngradeAQuarantinedRow()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqlite();
        using (connection)
        using (db)
        {
            ProjectFile file = CsamTestSupport.AddFile(db, mimeType: "video/mp4", fileName: "clip.mp4", bytes: [1, 2, 3, 4]);
            await db.SaveChangesAsync();

            // The matcher loaded this row before another scan quarantined it.
            using (InferenceApiDbContext concurrent = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                       .UseSqlite(connection)
                       .Options))
            {
                ProjectFile other = await concurrent.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id);
                other.CsamStatus = CsamScanStatus.Quarantined;
                other.CsamClassification = "csam";
                other.CsamMatchType = "exact";
                await concurrent.SaveChangesAsync();
            }

            CsamOptions options = CsamTestSupport.EnabledOptions();
            CsamTestStorage storage = new();
            storage.Put(file.StorageKey, [1, 2, 3, 4]);
            CsamFakeServer server = new() { MediaClassification = "no-known-match" };
            using var handler = new CsamRecordingHandler(server);
            using var http = CsamTestSupport.NewHttpClient(options, handler);
            CsamHashMatcher matcher = new(
                db, storage, new PdqHasher(), new ArachnidShieldClient(http, Options.Create(options)),
                new CsamQuarantineService(storage), Options.Create(options), new CsamListLogger<CsamHashMatcher>());

            CsamScanOutcome outcome = await matcher.ScanAsync(file, actorUserId: null, CancellationToken.None);

            outcome.Status.Should().Be(CsamScanStatus.Quarantined);
            db.ChangeTracker.Clear();
            ProjectFile stored = await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id);
            stored.CsamStatus.Should().Be(CsamScanStatus.Quarantined, "the conditional UPDATE affected 0 rows");
            stored.CsamClassification.Should().Be("csam");
            stored.CsamMatchType.Should().Be("exact");
        }
    }

    [Fact]
    public async Task AMatch_OnSqlite_StampsTheProjectMarkerWithTheVerdict_AndACleanScanDoesNot()
    {
        foreach (bool match in new[] { true, false })
        {
            (SqliteConnection connection, InferenceApiDbContext db) = NewSqlite();
            using (connection)
            using (db)
            {
                ProjectFile file = CsamTestSupport.AddFile(db, mimeType: "video/mp4", fileName: "clip.mp4", bytes: [1, 2, 3, 4]);
                db.Projects.Add(new Project { Id = file.ProjectId, Name = "p" });
                await db.SaveChangesAsync();

                CsamOptions options = CsamTestSupport.EnabledOptions();
                CsamTestStorage storage = new();
                storage.Put(file.StorageKey, [1, 2, 3, 4]);
                CsamFakeServer server = new() { MediaClassification = match ? "csam" : "no-known-match" };
                using var handler = new CsamRecordingHandler(server);
                using var http = CsamTestSupport.NewHttpClient(options, handler);
                CsamHashMatcher matcher = new(
                    db, storage, new PdqHasher(), new ArachnidShieldClient(http, Options.Create(options)),
                    new CsamQuarantineService(storage), Options.Create(options), new CsamListLogger<CsamHashMatcher>());

                DateTime before = DateTime.UtcNow.AddSeconds(-1);
                await matcher.ScanAsync(file, actorUserId: null, CancellationToken.None);

                db.ChangeTracker.Clear();
                Project project = await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == file.ProjectId);
                if (match)
                {
                    project.ContentQuarantinedAt.Should().NotBeNull().And.BeOnOrAfter(before);
                    project.CsamOutputHoldAt.Should().NotBeNull().And.BeOnOrAfter(before);
                }
                else
                {
                    project.ContentQuarantinedAt.Should().BeNull();
                    project.CsamOutputHoldAt.Should().BeNull();
                }
            }
        }
    }

    [Fact]
    public async Task Normalization_DoesNotOverwriteAConcurrentQuarantine_OnSqlite()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqlite();
        using (connection)
        using (db)
        {
            ProjectFile file = CsamTestSupport.AddFile(db, mimeType: "image/jpeg", fileName: "photo.jpg", bytes: [1, 2, 3, 4]);
            file.CsamStatus = CsamScanStatus.Pending;
            await db.SaveChangesAsync();

            // The inline matcher quarantines the row after normalization selected it.
            using (InferenceApiDbContext concurrent = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                       .UseSqlite(connection)
                       .Options))
            {
                ProjectFile other = await concurrent.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id);
                other.CsamStatus = CsamScanStatus.Quarantined;
                other.CsamClassification = "csam";
                await concurrent.SaveChangesAsync();
            }

            bool settled = await CsamScanSweeper.TryNormalizeAsync(
                db, file.Id,
                pendingBefore: DateTime.UtcNow.AddMinutes(1),
                staleClaimBefore: DateTime.UtcNow.AddMinutes(-15),
                CancellationToken.None);

            settled.Should().BeFalse("the conditional UPDATE must affect 0 rows");
            db.ChangeTracker.Clear();
            ProjectFile stored = await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file.Id);
            stored.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
            stored.CsamClassification.Should().Be("csam");
        }
    }

    [Fact]
    public async Task SweeperClaim_OnSqlite_ScansThePendingRow()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqlite();
        using (connection)
        using (db)
        {
            Guid organization = Guid.NewGuid();
            Project project = new() { Id = Guid.NewGuid(), Name = "P", OrganizationId = organization, OwnerId = Guid.NewGuid() };
            ProjectFile file = CsamTestSupport.AddFile(db, mimeType: "image/jpeg", fileName: "old.jpg", bytes: [1, 2, 3, 4], projectId: project.Id);
            file.CsamStatus = CsamScanStatus.Pending;
            file.UploadedAt = DateTime.UtcNow.AddMinutes(-5);
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            CsamOptions options = CsamTestSupport.EnabledOptions();
            options.Sweeper.IntervalMinutes = 15;
            ServiceCollection services = new();
            services.AddLogging();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseSqlite(connection));
            services.AddSingleton<ITenantContext>(new CsamFixedTenant(organization));
            CsamRecordingMatcher matcher = new();
            services.AddSingleton<ICsmaHashMatcher>(matcher);
            services.AddSingleton(Options.Create(options));
            await using ServiceProvider provider = services.BuildServiceProvider();

            CsamScanSweeper sweeper = new(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(options), NullLogger<CsamScanSweeper>.Instance);

            int scanned = await sweeper.SweepOnceAsync(CancellationToken.None);

            scanned.Should().Be(1);
            matcher.Scanned.Should().ContainSingle().Which.Should().Be(file.Id);
        }
    }

    private sealed class CsamRecordingMatcher : ICsmaHashMatcher
    {
        public List<Guid> Scanned { get; } = [];

        public Task<CsamScanOutcome> ScanAsync(ProjectFile file, Guid? actorUserId, CancellationToken ct)
        {
            Scanned.Add(file.Id);
            return Task.FromResult(new CsamScanOutcome(CsamScanStatus.Clean, "no-known-match", null, false, null));
        }
    }
}
