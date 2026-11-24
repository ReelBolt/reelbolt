using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.SfxLibrary;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

internal sealed class FakeSfxCopier : ISfxLibraryObjectCopier
{
    public List<(string From, string To)> Copies { get; } = [];
    public Task CopyAsync(string libraryKey, string projectKey, CancellationToken ct) { Copies.Add((libraryKey, projectKey)); return Task.CompletedTask; }
}

/// <summary>The add and starter-set endpoints of the built-in sound library (docs/sfx-library.md).</summary>
public class SfxLibraryProjectAddTests
{
    private sealed class Rig : IDisposable
    {
        public InferenceApiDbContext Db { get; }
        public FakeSfxCopier Copier { get; } = new();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid ProjectId { get; }
        public Guid ForeignProjectId { get; }

        public Rig()
        {
            Db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase($"sfxadd-{Guid.NewGuid()}").Options);
            Guid other = Guid.NewGuid();
            Project mine = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = UserId, OrganizationId = TestTenancy.OrgOf(UserId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Project foreign = new() { Id = Guid.NewGuid(), Name = "Q", OwnerId = other, OrganizationId = TestTenancy.OrgOf(other), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.AddRange(mine, foreign);
            Db.SaveChanges();
            ProjectId = mine.Id;
            ForeignProjectId = foreign.Id;
        }

        public SfxLibraryItem AddItem(string slug, string category = "pop", bool enabled = true, string? key = null, int sort = 0, int durationMs = 300)
        {
            SfxLibraryItem item = new()
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                Title = slug,
                Category = category,
                Tags = [category],
                DurationMs = durationMs,
                SizeBytes = 1234,
                Sha256 = new string('a', 64),
                StorageKey = key ?? SfxLibraryKeys.KeyFor(slug, "ogg"),
                MimeType = "audio/ogg",
                Licence = "CC0 1.0",
                IsEnabled = enabled,
                SortOrder = sort,
                AddedAt = DateTimeOffset.UtcNow
            };
            Db.SfxLibraryItems.Add(item);
            Db.SaveChanges();
            return item;
        }

        public ProjectSfxLibraryController Controller(OrganizationRole role = OrganizationRole.Member, int maxPerProject = 200, int perMinute = 60, Mock<IProjectAccessService>? countingAccess = null)
        {
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(UserId);
            user.SetupGet(u => u.Email).Returns("t@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(UserId));
            user.SetupGet(u => u.OrgRole).Returns(role);
            Mock<IFileStorageService> storage = new();
            storage.SetupGet(s => s.BucketName).Returns("bucket");
            IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SfxLibrary:MaxPerProject"] = maxPerProject.ToString(),
                ["SfxLibrary:MaxAddsPerMinute"] = perMinute.ToString()
            }).Build();
            SfxLibraryProjectAdder adder = new(Db, Copier, new SfxLibraryAddRateLimiter(cfg), storage.Object, Publish.Object, cfg, NullLogger<SfxLibraryProjectAdder>.Instance);
            IProjectAccessService access = countingAccess?.Object ?? TestTenancy.Access(Db, user.Object);
            return new ProjectSfxLibraryController(access, user.Object, adder);
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Add_copies_registers_a_pre_cleared_sfx_file_and_asks_for_peaks()
    {
        using Rig r = new();
        SfxLibraryItem item = r.AddItem("pop-soft");

        IActionResult result = await r.Controller().Add(r.ProjectId, item.Id, default);

        SfxLibraryAddResponse body = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<SfxLibraryAddResponse>().Subject;
        ProjectFile file = r.Db.ProjectFiles.Single();
        body.MediaRef.Should().Be($"file:{file.Id:D}");
        body.FileId.Should().Be(file.Id);
        body.Name.Should().Be("pop-soft.ogg");
        file.StorageKey.Should().Be($"projects/{r.ProjectId}/userFiles/sound-effects/pop-soft.ogg");
        file.SourceKind.Should().Be("sfx_library");
        file.ModerationStatus.Should().Be(FileModerationStatus.Clean);
        file.CsamStatus.Should().Be(CsamScanStatus.Clean);
        r.Copier.Copies.Should().ContainSingle().Which.Should().Be((item.StorageKey, file.StorageKey));
        r.Publish.Verify(p => p.Publish(It.IsAny<ReelBolt.Shared.IntegrationEvents.MediaDerivativesRequested>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_never_overwrites_an_untagged_row_already_at_the_destination_key()
    {
        using Rig r = new();
        SfxLibraryItem item = r.AddItem("pop-soft");
        ProjectFile taken = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = r.ProjectId,
            OriginalFileName = "pop-soft.ogg",
            OriginalPath = "sound-effects/pop-soft.ogg",
            StorageKey = $"projects/{r.ProjectId}/userFiles/sound-effects/pop-soft.ogg",
            StorageBucket = "bucket",
            MimeType = "audio/ogg",
            SizeBytes = 9999,
            SourceKind = null,
            UploadedAt = DateTime.UtcNow
        };
        r.Db.ProjectFiles.Add(taken);
        r.Db.SaveChanges();

        IActionResult result = await r.Controller().Add(r.ProjectId, item.Id, default);

        result.Should().BeOfType<OkObjectResult>();
        r.Copier.Copies.Should().BeEmpty("the bytes at a key owned by an ordinary file are never overwritten");
        ProjectFile only = r.Db.ProjectFiles.Single();
        only.Id.Should().Be(taken.Id);
        only.SourceKind.Should().BeNull();
        only.SizeBytes.Should().Be(9999);
    }

    [Fact]
    public async Task Add_is_idempotent_and_returns_the_same_file()
    {
        using Rig r = new();
        SfxLibraryItem item = r.AddItem("pop-soft");
        ProjectSfxLibraryController c = r.Controller();

        SfxLibraryAddResponse a = (SfxLibraryAddResponse)((OkObjectResult)await c.Add(r.ProjectId, item.Id, default)).Value!;
        SfxLibraryAddResponse b = (SfxLibraryAddResponse)((OkObjectResult)await c.Add(r.ProjectId, item.Id, default)).Value!;

        b.FileId.Should().Be(a.FileId);
        r.Db.ProjectFiles.Count().Should().Be(1);
        r.Copier.Copies.Should().HaveCount(1);
    }

    [Fact]
    public async Task Unknown_disabled_and_foreign_project_are_the_same_404()
    {
        using Rig r = new();
        SfxLibraryItem off = r.AddItem("off-one", enabled: false);
        SfxLibraryItem on = r.AddItem("on-one");
        ProjectSfxLibraryController c = r.Controller();

        (await c.Add(r.ProjectId, Guid.NewGuid(), default)).Should().BeOfType<NotFoundResult>();
        (await c.Add(r.ProjectId, off.Id, default)).Should().BeOfType<NotFoundResult>();
        (await c.Add(r.ForeignProjectId, on.Id, default)).Should().BeOfType<NotFoundResult>();
        (await c.StarterSet(r.ForeignProjectId, default)).Should().BeOfType<NotFoundResult>();
        r.Db.ProjectFiles.Should().BeEmpty();
        r.Copier.Copies.Should().BeEmpty();
    }

    [Fact]
    public async Task A_role_without_edit_gets_403_and_nothing_is_copied()
    {
        using Rig r = new();
        SfxLibraryItem item = r.AddItem("pop-soft");

        (await r.Controller(OrganizationRole.Billing).Add(r.ProjectId, item.Id, default)).Should().BeOfType<ForbidResult>();
        r.Copier.Copies.Should().BeEmpty();
    }

    [Fact]
    public async Task The_project_access_service_is_consulted_exactly_once_with_edit()
    {
        using Rig r = new();
        SfxLibraryItem item = r.AddItem("pop-soft");
        Mock<IProjectAccessService> access = new();
        access.Setup(a => a.GetAsync(r.ProjectId, ProjectPermission.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectAccessResult(ProjectAccessOutcome.Found, r.Db.Projects.Single(p => p.Id == r.ProjectId)));

        await r.Controller(countingAccess: access).Add(r.ProjectId, item.Id, default);

        access.Invocations.Should().HaveCount(1);
        access.Verify(a => a.GetAsync(r.ProjectId, ProjectPermission.Edit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_per_project_cap_answers_409_with_its_code()
    {
        using Rig r = new();
        SfxLibraryItem a = r.AddItem("one");
        SfxLibraryItem b = r.AddItem("two");
        ProjectSfxLibraryController c = r.Controller(maxPerProject: 1);

        (await c.Add(r.ProjectId, a.Id, default)).Should().BeOfType<OkObjectResult>();
        ObjectResult refused = (await c.Add(r.ProjectId, b.Id, default)).Should().BeOfType<ConflictObjectResult>().Subject;
        refused.Value!.ToString().Should().Contain("SFX_LIBRARY_PROJECT_CAP");
        (await c.Add(r.ProjectId, a.Id, default)).Should().BeOfType<OkObjectResult>("an item already in the project is not a new one");
        r.Db.ProjectFiles.Count().Should().Be(1);
    }

    [Fact]
    public async Task Adds_are_rate_limited_per_user()
    {
        using Rig r = new();
        SfxLibraryItem a = r.AddItem("one");
        ProjectSfxLibraryController c = r.Controller(perMinute: 2);

        await c.Add(r.ProjectId, a.Id, default);
        await c.Add(r.ProjectId, a.Id, default);
        ObjectResult third = (ObjectResult)await c.Add(r.ProjectId, a.Id, default);

        third.StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task Starter_set_is_rate_limited_by_the_same_gate()
    {
        using Rig r = new();
        SfxLibraryItem a = r.AddItem("one");
        ProjectSfxLibraryController c = r.Controller(perMinute: 1);

        await c.Add(r.ProjectId, a.Id, default);
        ObjectResult second = (ObjectResult)await c.StarterSet(r.ProjectId, default);

        second.StatusCode.Should().Be(429);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("Upper")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task A_poisoned_slug_in_the_table_cannot_escape_the_sound_effects_folder(string slug)
    {
        using Rig r = new();
        SfxLibraryItem item = r.AddItem("placeholder");
        item.Slug = slug;
        r.Db.SaveChanges();

        (await r.Controller().Add(r.ProjectId, item.Id, default)).Should().BeOfType<NotFoundResult>();
        r.Copier.Copies.Should().BeEmpty();
        SfxLibraryProjectAdder.DestinationKey(r.ProjectId, slug, "platform/sfx-library/x.ogg").Should().BeNull();
    }

    [Theory]
    [InlineData("projects/other/userFiles/secret.ogg")]
    [InlineData("platform/sfx-library/../../projects/x/y.ogg")]
    [InlineData("platform/sfx-library/x.o/gg")]
    public async Task A_source_key_outside_the_library_prefix_is_never_copied(string poisonedKey)
    {
        using Rig r = new();
        SfxLibraryItem item = r.AddItem("pop-soft", key: poisonedKey);

        (await r.Controller().Add(r.ProjectId, item.Id, default)).Should().BeOfType<NotFoundResult>();
        r.Copier.Copies.Should().BeEmpty();
    }

    [Fact]
    public void The_destination_key_depends_on_the_slug_and_a_safe_extension_only()
    {
        Guid p = Guid.NewGuid();
        SfxLibraryProjectAdder.DestinationKey(p, "pop-soft", "platform/sfx-library/whatever.OGG")
            .Should().Be($"projects/{p}/userFiles/sound-effects/pop-soft.ogg");
        SfxLibraryProjectAdder.DestinationKey(p, "pop-soft", "platform/sfx-library/x.o%2e").Should().BeNull();
    }

    [Fact]
    public async Task Starter_set_adds_24_spread_over_categories_and_is_idempotent()
    {
        using Rig r = new();
        string[] cats = ["pop", "click", "whoosh", "ding"];
        int n = 0;
        foreach (string cat in cats)
            for (int i = 0; i < 12; i++) r.AddItem($"{cat}-{i}", cat, sort: n++);
        ProjectSfxLibraryController c = r.Controller();

        SfxLibraryStarterSetResponse first = (SfxLibraryStarterSetResponse)((OkObjectResult)await c.StarterSet(r.ProjectId, default)).Value!;
        SfxLibraryStarterSetResponse second = (SfxLibraryStarterSetResponse)((OkObjectResult)await c.StarterSet(r.ProjectId, default)).Value!;

        first.Added.Should().Be(24);
        first.Items.Should().HaveCount(24);
        second.Added.Should().Be(0);
        second.Items.Select(i => i.FileId).Should().BeEquivalentTo(first.Items.Select(i => i.FileId));
        r.Db.ProjectFiles.Select(f => f.StorageKey).AsEnumerable()
            .GroupBy(k => cats.First(cat => k.Contains($"/{cat}-"))).Select(g => g.Count()).Should().AllBeEquivalentTo(6);
    }

    [Fact]
    public async Task Library_files_do_not_count_in_the_storage_quota_but_uploads_do()
    {
        using Rig r = new();
        SfxLibraryItem item = r.AddItem("pop-soft");
        await r.Controller().Add(r.ProjectId, item.Id, default);
        r.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = r.ProjectId,
            OriginalFileName = "u.wav",
            StorageKey = $"projects/{r.ProjectId}/userFiles/u.wav",
            MimeType = "audio/wav",
            SizeBytes = 500,
            SourceKind = null
        });
        await r.Db.SaveChangesAsync();

        long used = await new StorageUsageService(r.Db, Mock.Of<IOrganizationEntitlements>()).GetUsedBytesAsync(TestTenancy.OrgOf(r.UserId), default);

        used.Should().Be(500);
    }

    [Fact]
    public async Task The_list_filters_by_maximum_duration()
    {
        using Rig r = new();
        r.AddItem("short", durationMs: 200);
        r.AddItem("long", durationMs: 2000);
        SfxLibraryController c = new(r.Db, new FakeSfxStore());

        SfxLibraryListResponse res = (await c.List(null, null, null, maxDurationMs: 500)).Value!;

        res.Items.Select(i => i.Slug).Should().Equal("short");
    }

    [Fact]
    public void Only_the_library_adder_pre_clears_a_project_file()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "ReelBolt.Inference.Api"))) dir = Path.GetDirectoryName(dir)!;
        string src = Path.Combine(dir, "src", "ReelBolt.Inference.Api");
        string[] offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("CsamStatus = CsamScanStatus.Clean", StringComparison.Ordinal)
                        && File.ReadAllText(f).Contains("ProjectFile", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray()!;

        offenders.Should().BeEquivalentTo(["SfxLibraryProjectAdder.cs"], "no other path may register a project file as already cleared");
    }
}
