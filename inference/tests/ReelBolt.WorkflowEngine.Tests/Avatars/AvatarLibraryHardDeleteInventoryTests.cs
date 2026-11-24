#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using Xunit;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Every place that hard-deletes a project file, an avatar reference, an avatar or a project is reviewed: the API's
/// go through <c>InferenceApiDbContext</c>'s save (which stamps the library hold marker first); the engine's only
/// removal skips held rows by the shared rule; no raw SQL, <c>ExecuteDelete</c> or Go statement touches those tables.
/// A new one fails this test until it is reviewed and added here.
/// </summary>
public class AvatarLibraryHardDeleteInventoryTests
{
    private static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "inference", "src", "ReelBolt.Inference.Api")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static IEnumerable<string> Sources(string root, params string[] projects) =>
        projects.SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, "inference", "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static readonly Regex Remove = new(@"\b(ProjectFiles|ProjectAvatarReferences|ProjectAvatars|Projects)\.(Remove|RemoveRange)\(", RegexOptions.Compiled);

    private static readonly Regex SetBased = new(
        @"(ProjectFiles|ProjectAvatarReferences|ProjectAvatars|Projects|Set<ProjectFile>\(\)|Set<ProjectAvatarReference>\(\)|Set<ProjectAvatar>\(\)|Set<Project>\(\))[^;]*ExecuteDelete|DELETE\s+FROM\s+""?(project_files|project_avatar_references|project_avatars|projects)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The reviewed API sites (each saves through InferenceApiDbContext, so the hook stamps first).
    private static readonly string[] ReviewedApi =
    [
        "AssistantFileTools.cs", "AssistantProjectTools.cs", "GitHubRepositoryPullService.cs", "ProjectAvatarsController.cs",
        "ProjectVoicesController.cs", "FileRestoreKindHandler.cs", "ProjectFilesController.cs", "CloudSyncService.cs",
        "ProjectAvatarDeletion.cs", "ArchiveUnpackProcessor.cs", "OutputsController.cs", "AccountPurgeService.cs",
        "ProjectFileDeletionService.cs", "StorageRetentionService.cs", "LocalFolderSyncService.cs", "ProjectsController.cs"
    ];

    [Fact]
    public void The_api_hard_delete_sites_are_exactly_the_reviewed_ones()
    {
        string root = Root();
        List<string> found = [.. Sources(root, "ReelBolt.Inference.Api").Where(f => Remove.IsMatch(File.ReadAllText(f))).Select(f => Path.GetFileName(f)!).Distinct().Order()];
        found.Should().Equal([.. ReviewedApi.Order()], "a new hard delete of files, references, avatars or projects must be reviewed against the library hold marker");
    }

    [Fact]
    public void The_engine_has_one_removal_and_it_skips_held_rows_by_the_shared_rule()
    {
        string root = Root();
        var sites = Sources(root, "ReelBolt.WorkflowEngine", "ReelBolt.Shared").Where(f => Remove.IsMatch(File.ReadAllText(f))).ToList();
        sites.Select(Path.GetFileName).Should().Equal("VideoCompileStepExecutor.cs");
        string text = File.ReadAllText(sites[0]);
        int remove = Remove.Match(text).Index;
        text[..remove].Should().Contain("HeldFileRule.Reason(f, contentMarker) is null", "held rows are filtered out before the removal");
    }

    [Fact]
    public void No_set_based_or_raw_delete_touches_those_tables_in_any_assembly_or_the_go_api()
    {
        string root = Root();
        Sources(root, "ReelBolt.Inference.Api", "ReelBolt.WorkflowEngine", "ReelBolt.Shared")
            .Where(f => SetBased.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).Should().BeEmpty();
        string go = Path.Combine(root, "api");
        if (Directory.Exists(go))
        {
            Directory.EnumerateFiles(go, "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".go") || f.EndsWith(".sql")) && !f.EndsWith("_test.go"))
                .Where(f => SetBased.IsMatch(File.ReadAllText(f))).Should().BeEmpty("the Go API must not delete these rows");
        }
    }

    [Fact]
    public void The_shared_rule_names_every_kind_of_hold()
    {
        DateTime marker = DateTime.UtcNow;
        HeldFileRule.Reason(CsamScanStatus.Quarantined, null, FileModerationStatus.Clean, null, null).Should().Be("csam");
        HeldFileRule.Reason(CsamScanStatus.Clean, "projects/p/quarantine/x", FileModerationStatus.Clean, null, null).Should().Be("csam");
        HeldFileRule.Reason(CsamScanStatus.Clean, null, FileModerationStatus.Quarantined, null, null).Should().Be("moderation");
        HeldFileRule.Reason(CsamScanStatus.Clean, null, FileModerationStatus.Clean, marker.AddMinutes(-1), marker).Should().Be("content_marker");
        HeldFileRule.Reason(CsamScanStatus.Clean, null, FileModerationStatus.Clean, marker.AddHours(1), marker).Should().BeNull();
        HeldFileRule.Reason(CsamScanStatus.Clean, null, FileModerationStatus.Clean, null, marker).Should().BeNull("a user's own upload is not covered by the marker");
    }

    [Fact]
    public async Task Removing_only_an_avatar_still_stamps_the_marker_of_its_held_reference_files()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        (Guid itemId, Guid avatarId) = await l.PublishPersonAsync("Anna", face);
        using (InferenceApiDbContext db = l.NewDb())
        {
            (await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == face.Id)).CsamStatus = CsamScanStatus.Quarantined;
            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = l.NewDb())
        {
            // The references are not loaded: their removal is a database cascade the context cannot see.
            db.ProjectAvatars.Remove(await db.ProjectAvatars.SingleAsync(a => a.Id == avatarId));
            await db.SaveChangesAsync();
        }

        using InferenceApiDbContext check = l.NewDb();
        AvatarLibraryReference row = await check.AvatarLibraryReferences.SingleAsync(r => r.LibraryItemId == itemId);
        row.HeldAt.Should().NotBeNull();
        row.HeldReason.Should().Be("csam");
    }
}
