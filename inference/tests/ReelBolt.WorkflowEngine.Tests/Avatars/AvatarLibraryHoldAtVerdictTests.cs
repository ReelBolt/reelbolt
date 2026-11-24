#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// The library hold marker is stamped AT VERDICT TIME, in the same save as the verdict (docs/csam.md: every durable marker),
/// so a later delete, a database cascade with no tracked rows or a transfer loses nothing. Relational (SQLite) so a raw
/// <c>DELETE</c> stands in for the Go user-delete cascade.
/// </summary>
/// <summary>The API context for SQLite: the PostgreSQL <c>xmin</c> row version of a library item is an ordinary column here.</summary>
internal sealed class SqliteLibraryDb(DbContextOptions<InferenceApiDbContext> options) : InferenceApiDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<AvatarLibraryItem>().Property(i => i.RowVersion).HasColumnType("INTEGER").ValueGeneratedNever().IsConcurrencyToken(false);
    }
}

public sealed class AvatarLibraryHoldAtVerdictTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid OtherOrg = Guid.NewGuid();
    private const string Digest = "aa11bb22cc33dd44ee55ff6600778899aa11bb22cc33dd44ee55ff6600778899";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<InferenceApiDbContext> _options;

    public AvatarLibraryHoldAtVerdictTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection)
            .EnableServiceProviderCaching(false)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)).Options;
        using InferenceApiDbContext db = New();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext New() => new SqliteLibraryDb(_options);

    private (Guid Project, Guid File, Guid Reference) Seed(Guid org, bool platformOutput = false)
    {
        using InferenceApiDbContext db = New();
        var project = new Project { Id = Guid.NewGuid(), Name = "p", OwnerId = Guid.NewGuid(), OrganizationId = org };
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "a.png",
            OriginalPath = "a.png",
            MimeType = "image/png",
            StorageKey = $"projects/{project.Id}/userFiles/a.png",
            StorageBucket = "b",
            Category = "userFiles",
            ContentSha256 = Digest,
            CsamStatus = CsamScanStatus.Clean,
            ModerationStatus = FileModerationStatus.Clean,
            OutputProducedAt = platformOutput ? DateTime.UtcNow.AddHours(-1) : null
        };
        var item = AvatarLibraryPublishTests.SeedItem(org, AvatarSubjectKind.Person, "Anna");
        item.RowVersion = 1; // SQLite has no xmin
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
        db.Projects.Add(project);
        db.ProjectFiles.Add(file);
        db.AvatarLibraryItems.Add(item);
        db.AvatarLibraryReferences.Add(reference);
        db.SaveChanges();
        return (project.Id, file.Id, reference.Id);
    }

    private AvatarLibraryReference Row(Guid id)
    {
        using InferenceApiDbContext db = New();
        return db.AvatarLibraryReferences.IgnoreQueryFilters().AsNoTracking().Single(r => r.Id == id);
    }

    [Fact]
    public void A_csam_verdict_stamps_the_copy_in_the_same_save()
    {
        (_, Guid file, Guid reference) = Seed(Org);
        using (InferenceApiDbContext db = New())
        {
            db.ProjectFiles.IgnoreQueryFilters().Single(f => f.Id == file).CsamStatus = CsamScanStatus.Quarantined;
            db.SaveChanges();
        }

        Row(reference).HeldAt.Should().NotBeNull();
        Row(reference).HeldReason.Should().Be("csam");
    }

    [Fact]
    public async Task A_moderation_verdict_stamps_the_copy_and_never_a_clean_one()
    {
        (_, Guid file, Guid reference) = Seed(Org);
        (_, _, Guid foreign) = Seed(OtherOrg);
        using (InferenceApiDbContext db = New())
        {
            (await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file)).ModerationStatus = FileModerationStatus.Quarantined;
            await db.SaveChangesAsync();
        }

        Row(reference).HeldReason.Should().Be("moderation");
        Row(foreign).HeldAt.Should().BeNull("another organization's copy of the same bytes is not held by this file");
    }

    [Fact]
    public async Task Setting_the_project_content_marker_stamps_the_copies_of_its_platform_outputs()
    {
        (Guid project, _, Guid reference) = Seed(Org, platformOutput: true);
        using (InferenceApiDbContext db = New())
        {
            (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == project)).ContentQuarantinedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        Row(reference).HeldReason.Should().Be("content_marker");
    }

    [Fact]
    public async Task A_database_level_cascade_after_a_verdict_loses_nothing()
    {
        (Guid project, Guid file, Guid reference) = Seed(Org);
        using (InferenceApiDbContext db = New())
        {
            (await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == file)).ModerationStatus = FileModerationStatus.Quarantined;
            await db.SaveChangesAsync();
        }

        // The Go user delete: rows disappear by cascade with nothing tracked by this context.
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM project_files WHERE project_id = $p";
            command.Parameters.AddWithValue("$p", project.ToString().ToUpperInvariant());
            command.ExecuteNonQuery();
        }

        using (InferenceApiDbContext db = New()) (await db.ProjectFiles.IgnoreQueryFilters().CountAsync(f => f.ProjectId == project)).Should().Be(0);
        Row(reference).HeldAt.Should().NotBeNull("the marker was written when the verdict landed");
    }

    [Fact]
    public async Task A_clean_file_and_unrelated_saves_leave_the_copy_unheld()
    {
        (Guid project, _, Guid reference) = Seed(Org);
        using (InferenceApiDbContext db = New())
        {
            (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == project)).Name = "renamed";
            await db.SaveChangesAsync();
        }

        Row(reference).HeldAt.Should().BeNull();
    }

    // ------------------------------------------------------------------ the writers of a verdict are reviewed

    // Tracked writers are stamped by the context's save hook. The two SET-BASED writers stamp explicitly, in the verdict's
    // own transaction (CsamHashMatcher's relational path, ProjectContentQuarantineMarker.StampAsync/StampCsamAsync); see
    // The_set_based_writers_stamp_explicitly and docs/csam.md. FileRestoreKindHandler only
    // CLEARS a quarantine key on a restored file (a release): the library marker is never cleared.
    private static readonly string[] ReviewedWriters =
    [
        "ArchiveUnpackProcessor.cs", "ContentModerationProcessor.cs", "CsamHashMatcher.cs", "CsamQuarantineService.cs", "FileRestoreKindHandler.cs", "ProjectContentQuarantineMarker.cs"
    ];

    private static readonly Regex Writer = new(
        @"\.CsamStatus = CsamScanStatus\.Quarantined|CsamStatus, CsamScanStatus\.Quarantined|\.CsamQuarantineKey = |ModerationStatus = FileModerationStatus\.Quarantined|ModerationStatus, FileModerationStatus\.Quarantined|\.ContentQuarantinedAt = |\.CsamOutputHoldAt = |SetProperty\(p => p\.ContentQuarantinedAt, |SetProperty\(p => p\.CsamOutputHoldAt, ",
        RegexOptions.Compiled);

    [Fact]
    public void The_writers_of_a_verdict_are_exactly_the_reviewed_ones_in_the_api_and_the_engine()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "inference", "src", "ReelBolt.Inference.Api")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        List<string> found = [.. new[] { "ReelBolt.Inference.Api", "ReelBolt.WorkflowEngine" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(dir!, "inference", "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Writer.IsMatch(File.ReadAllText(f))).Select(f => Path.GetFileName(f)!).Distinct().Order()];
        found.Should().Equal([.. ReviewedWriters.Order()], "a new writer of a verdict must stamp the library hold marker (or be tracked) and be reviewed here");
    }

    [Fact]
    public void The_set_based_writers_stamp_explicitly()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "inference", "src", "ReelBolt.Inference.Api")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        string services = Path.Combine(dir!, "inference", "src", "ReelBolt.Inference.Api", "Services");
        string matcher = File.ReadAllText(Path.Combine(services, "Csam", "CsamHashMatcher.cs"));
        string marker = File.ReadAllText(Path.Combine(services, "ProjectContentQuarantineMarker.cs"));
        // Any file that writes a verdict with ExecuteUpdate must call the set-based stamp, after the write.
        foreach ((string name, string text) in new[] { ("CsamHashMatcher.cs", matcher), ("ProjectContentQuarantineMarker.cs", marker) })
        {
            int update = text.IndexOf("ExecuteUpdateAsync", StringComparison.Ordinal);
            update.Should().BeGreaterThan(0);
            text.IndexOf("AvatarLibraryHoldMarker.StampProjectSetBasedAsync", StringComparison.Ordinal).Should().BeGreaterThan(update, $"{name} must stamp after its verdict write");
        }

        var setBased = Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"ExecuteUpdate(Async)?\([^;]*?(CsamStatus|ModerationStatus|CsamQuarantineKey|ContentQuarantinedAt|CsamOutputHoldAt)", RegexOptions.Singleline))
            .Select(f => Path.GetFileName(f)!).Distinct().Order().ToList();
        setBased.Should().Equal("CsamHashMatcher.cs", "CsamScanSweeper.cs", "ProjectContentQuarantineMarker.cs");
    }
}
