#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Shared.Data.Models;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// A hold on a library copy survives the removal of the file or reference rows that carried the verdict (the hold is
/// read by content digest, so the removal must leave a durable marker). Every hard delete of a <c>ProjectFile</c>, a
/// <c>ProjectAvatarReference</c> or a <c>Project</c> goes through <see cref="InferenceApiDbContext"/>'s save.
/// </summary>
public class AvatarLibraryHoldSurvivesDeletionTests
{
    public enum Verdict { Csam, Moderation, ContentMarker }

    public enum Removal { File, ReferenceRow, Project }

    public static TheoryData<Verdict, Removal> Cases()
    {
        var data = new TheoryData<Verdict, Removal>();
        foreach (Verdict v in Enum.GetValues<Verdict>())
            foreach (Removal r in Enum.GetValues<Removal>())
                data.Add(v, r);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_copy_stays_held_after_the_source_is_removed(Verdict verdict, Removal removal)
    {
        var l = new L();
        // A platform-made output (content marker case) is made at or before the project's marker.
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1,
            tweak: f => { if (verdict == Verdict.ContentMarker) f.OutputProducedAt = DateTime.UtcNow.AddMinutes(-30); });
        (Guid itemId, Guid avatarId) = await l.PublishPersonAsync("Anna", face);
        using (InferenceApiDbContext db = l.NewDb())
        {
            ProjectFile file = await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == face.Id);
            if (verdict == Verdict.Csam) file.CsamStatus = CsamScanStatus.Quarantined;
            if (verdict == Verdict.Moderation) file.ModerationStatus = FileModerationStatus.Quarantined;
            if (verdict == Verdict.ContentMarker)
                (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == l.Project.Id)).ContentQuarantinedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = l.NewDb())
        {
            switch (removal)
            {
                case Removal.File:
                    db.ProjectAvatarReferences.RemoveRange(await db.ProjectAvatarReferences.Where(r => r.ProjectFileId == face.Id).ToListAsync());
                    db.ProjectFiles.Remove(await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == face.Id));
                    break;
                case Removal.ReferenceRow:
                    db.ProjectAvatarReferences.RemoveRange(await db.ProjectAvatarReferences.Where(r => r.AvatarId == avatarId).ToListAsync());
                    break;
                case Removal.Project:
                    db.ProjectAvatarReferences.RemoveRange(await db.ProjectAvatarReferences.Where(r => r.AvatarId == avatarId).ToListAsync());
                    db.ProjectFiles.RemoveRange(await db.ProjectFiles.IgnoreQueryFilters().Where(f => f.ProjectId == l.Project.Id).ToListAsync());
                    db.Projects.Remove(await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == l.Project.Id));
                    break;
            }

            await db.SaveChangesAsync();
        }

        Guid picture;
        using (InferenceApiDbContext db = l.NewDb())
            picture = (await db.AvatarLibraryReferences.SingleAsync(r => r.LibraryItemId == itemId)).Id;
        H.Read(await l.Controller().Picture(itemId, picture, CancellationToken.None)).Status.Should().Be(404);
        (await l.Library().ListAsync(l.OrganizationId, null, CancellationToken.None)).Single().UnusableReason.Should().Be("reference_held");
        Project other = l.AddProject("Other");
        (await l.ImportResultAsync(itemId, other)).Status.Should().Be(409);
    }

    [Fact]
    public async Task A_clean_source_removed_leaves_the_copy_usable()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        (Guid itemId, _) = await l.PublishPersonAsync("Anna", face);
        using (InferenceApiDbContext db = l.NewDb())
        {
            db.ProjectAvatarReferences.RemoveRange(await db.ProjectAvatarReferences.Where(r => r.ProjectFileId == face.Id).ToListAsync());
            db.ProjectFiles.Remove(await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == face.Id));
            await db.SaveChangesAsync();
        }

        Guid picture;
        using (InferenceApiDbContext db = l.NewDb())
            picture = (await db.AvatarLibraryReferences.SingleAsync(r => r.LibraryItemId == itemId)).Id;
        (await l.Controller().Picture(itemId, picture, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
        (await l.Library().ListAsync(l.OrganizationId, null, CancellationToken.None)).Single().UnusableReason.Should().BeNull();
    }

    [Fact]
    public void Every_save_of_the_api_context_stamps_the_marker_and_nothing_removes_rows_around_it()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "inference", "src", "ReelBolt.Inference.Api")))
            dir = System.IO.Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        string api = System.IO.Path.Combine(dir!, "inference", "src", "ReelBolt.Inference.Api");

        string context = System.IO.File.ReadAllText(System.IO.Path.Combine(api, "Data", "InferenceApiDbContext.cs"));
        context.Should().Contain("AvatarLibraryHoldMarker.StampBeforeRemovalAsync(this, cancellationToken)");
        context.Should().Contain("AvatarLibraryHoldMarker.StampBeforeRemoval(this)");

        // A set-based delete skips the context's save and so skips the marker: none may touch these tables.
        var offenders = System.IO.Directory.EnumerateFiles(api, "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(f => !f.Contains(System.IO.Path.DirectorySeparatorChar + "Migrations" + System.IO.Path.DirectorySeparatorChar)
                        && !f.Contains(System.IO.Path.DirectorySeparatorChar + "obj" + System.IO.Path.DirectorySeparatorChar))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(System.IO.File.ReadAllText(f),
                @"(ProjectFiles|ProjectAvatarReferences|Projects|Set<ProjectFile>\(\)|Set<ProjectAvatarReference>\(\)|Set<Project>\(\))[^;]*ExecuteDelete"))
            .Select(System.IO.Path.GetFileName)
            .ToList();
        offenders.Should().BeEmpty("a set-based delete of files, avatar references or projects would bypass the hold marker");
    }
}
