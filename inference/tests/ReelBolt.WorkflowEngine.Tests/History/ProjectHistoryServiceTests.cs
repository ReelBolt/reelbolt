using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>
/// Behaviour of <see cref="ProjectHistoryService"/> against the in-memory provider: what a version
/// contains, dedupe, naming, numbering, caps and expiry. The provider has no advisory locks or
/// transactions, which is exactly the path the service takes when it is not Npgsql.
/// </summary>
public class ProjectHistoryServiceTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Capture_includes_only_eligible_items()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);

        ProjectFile cleanUser = NewFile(project.Id, "userFiles");
        ProjectFile cleanAgent = NewFile(project.Id, "agentFiles");
        ProjectFile output = NewFile(project.Id, "outputFiles");
        ProjectFile moderationQuarantined = NewFile(project.Id, "userFiles");
        moderationQuarantined.ModerationStatus = FileModerationStatus.Quarantined;
        ProjectFile csamQuarantined = NewFile(project.Id, "agentFiles");
        csamQuarantined.CsamStatus = CsamScanStatus.Quarantined;
        db.ProjectFiles.AddRange(cleanUser, cleanAgent, output, moderationQuarantined, csamQuarantined);

        WorkflowDefinition standard = NewWorkflow(project.Id, WorkflowDefinitionKind.Standard);
        WorkflowDefinition hidden = NewWorkflow(project.Id, WorkflowDefinitionKind.CompositionRevision);
        db.WorkflowDefinitions.AddRange(standard, hidden);

        EditTimelineEntity live = NewTimeline(project.Id, deleted: false);
        EditTimelineEntity deleted = NewTimeline(project.Id, deleted: true);
        db.EditTimelines.AddRange(live, deleted);

        ProjectVoice voice = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = "A voice",
            RemoteVoiceId = "remote-1"
        };
        db.ProjectVoices.Add(voice);

        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _);
        ProjectVersion? version = await service.CaptureAsync(
            project.Id, ProjectVersionCause.Auto, name: null, userId: null, CancellationToken.None);

        version.Should().NotBeNull();

        List<ProjectVersionItem> items = await db.ProjectVersionItems
            .Where(i => i.VersionId == version!.Id)
            .ToListAsync();

        items.Where(i => i.Kind == ProjectVersionItemKind.File)
            .Select(i => i.ItemKey)
            .Should().BeEquivalentTo(
                [cleanUser.Id.ToString("D"), cleanAgent.Id.ToString("D")],
                "outputs, quarantined files and every other category are never captured");

        items.Should().Contain(i => i.Kind == ProjectVersionItemKind.Workflow
            && i.ItemKey == standard.Id.ToString("D"));
        items.Should().NotContain(i => i.ItemKey == hidden.Id.ToString("D"),
            "only Standard workflows are captured");

        items.Should().Contain(i => i.Kind == ProjectVersionItemKind.Timeline
            && i.ItemKey == live.Id.ToString("D"));
        items.Should().NotContain(i => i.ItemKey == deleted.Id.ToString("D"),
            "the timeline context filter hides a soft-deleted timeline");

        items.Should().Contain(i => i.Kind == ProjectVersionItemKind.Project);
        items.Should().Contain(i => i.Kind == ProjectVersionItemKind.Pronunciation);
        items.Should().NotContain(i => i.ItemKey == voice.Id.ToString("D"),
            "voices are never versioned: consent must never roll back");
    }

    [Fact]
    public async Task Capture_remembers_the_output_stamp_and_platform_origin_without_changing_the_digest()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);

        ProjectFile generated = NewFile(project.Id, "agentFiles");
        generated.StorageKey = $"projects/{project.Id}/agentFiles/generated/clip.mp4";
        generated.OutputProducedAt = FixedNow.UtcDateTime;
        ProjectFile upload = NewFile(project.Id, "userFiles");
        db.ProjectFiles.AddRange(generated, upload);
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _);
        ProjectVersion version = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;

        List<ProjectVersionItem> items = await db.ProjectVersionItems
            .AsNoTracking()
            .Where(i => i.VersionId == version.Id && i.Kind == ProjectVersionItemKind.File)
            .ToListAsync();

        ProjectVersionItem generatedItem = items.Single(i => i.ItemKey == generated.Id.ToString("D"));
        generatedItem.PlatformMade.Should().BeTrue("the live row says the platform produced these bytes");
        generatedItem.OutputProducedAt.Should().Be(FixedNow.UtcDateTime);

        ProjectVersionItem uploadItem = items.Single(i => i.ItemKey == upload.Id.ToString("D"));
        uploadItem.PlatformMade.Should().BeFalse();
        uploadItem.OutputProducedAt.Should().BeNull();

        ProjectHistoryManifest manifest = (await service.BuildLiveManifestAsync(project.Id, CancellationToken.None))!;
        version.ManifestDigest.Should().Be(ProjectHistoryManifestDigest.Compute(manifest.Items),
            "the output-hold provenance is not identity-bearing and must not enter the digest");
    }

    [Fact]
    public async Task Capture_dedupes_an_unchanged_manifest_for_automatic_causes()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _);

        ProjectVersion first = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;
        ProjectVersion second = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;

        second.Id.Should().Be(first.Id, "an unchanged project returns the newest version, not a new row");
        (await db.ProjectVersions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task BeforeRestore_is_never_deduplicated()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _);

        ProjectVersion first = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.BeforeRestore, null, null, CancellationToken.None))!;
        ProjectVersion second = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.BeforeRestore, null, null, CancellationToken.None))!;

        second.Id.Should().NotBe(first.Id);
        (await db.ProjectVersions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Named_versions_are_never_deduplicated()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _);

        ProjectVersion first = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Named, "Draft", null, CancellationToken.None))!;
        ProjectVersion second = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Named, "Draft", null, CancellationToken.None))!;

        second.Id.Should().NotBe(first.Id);
        first.Name.Should().Be("Draft");
        (await db.ProjectVersions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Automatic_versions_expire_and_named_versions_do_not()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _, clock: new FixedTimeProvider(FixedNow));

        ProjectVersion automatic = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;
        ProjectVersion named = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Named, "Keep me", null, CancellationToken.None))!;

        automatic.ExpiresAt.Should().Be(FixedNow.UtcDateTime.AddDays(30));
        named.ExpiresAt.Should().BeNull("a named version is kept until deleted");
    }

    [Fact]
    public async Task Numbering_is_per_project_and_strictly_increasing()
    {
        using InferenceApiDbContext db = NewDb();
        Project first = SeedProject(db, "First");
        Project second = SeedProject(db, "Second");
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _);

        ProjectVersion firstOne = (await service.CaptureAsync(
            first.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;
        ProjectVersion secondOne = (await service.CaptureAsync(
            second.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;

        firstOne.Number.Should().Be(1);
        secondOne.Number.Should().Be(1, "numbers are per project");

        first.Name = "First renamed";
        await db.SaveChangesAsync();

        ProjectVersion firstTwo = (await service.CaptureAsync(
            first.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;

        firstTwo.Number.Should().Be(2);
    }

    [Fact]
    public async Task Oldest_automatic_version_is_pruned_at_the_cap()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _, options: new ProjectHistoryOptions
        {
            MaxNonNamedVersions = 2
        });

        for (int i = 0; i < 3; i++)
        {
            project.Description = $"revision {i}";
            await db.SaveChangesAsync();
            await service.CaptureAsync(
                project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None);
        }

        List<int> numbers = await db.ProjectVersions
            .Where(v => v.ProjectId == project.Id)
            .OrderBy(v => v.Number)
            .Select(v => v.Number)
            .ToListAsync();

        // The oldest automatic version is pruned at capture, so 2 and 3 remain.
        numbers.Should().Equal(2, 3);
    }

    [Fact]
    public async Task Hold_defers_pruning_a_platform_made_version_and_prunes_the_next_oldest_eligible()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        db.ProjectFiles.Add(NewFile(project.Id, "userFiles"));
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _, options: new ProjectHistoryOptions
        {
            MaxNonNamedVersions = 3
        });

        List<int> captured = [];
        for (int i = 0; i < 3; i++)
        {
            project.Description = $"revision {i}";
            await db.SaveChangesAsync();
            captured.Add((await service.CaptureAsync(
                project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!.Number);
        }

        captured.Should().Equal(1, 2, 3);

        // The oldest version becomes evidence under an output hold: it is what the cap must skip.
        ProjectVersion oldest = await db.ProjectVersions.SingleAsync(v => v.Number == 1);
        foreach (ProjectVersionItem item in await db.ProjectVersionItems
                     .Where(i => i.VersionId == oldest.Id).ToListAsync())
        {
            item.PlatformMade = true;
        }

        project.CsamOutputHoldAt = DateTime.UtcNow;
        project.Description = "after the hold";
        await db.SaveChangesAsync();

        ProjectVersion capturedDuringHold = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;

        capturedDuringHold.Number.Should().Be(4);
        List<int> numbers = await db.ProjectVersions
            .Where(v => v.ProjectId == project.Id)
            .OrderBy(v => v.Number)
            .Select(v => v.Number)
            .ToListAsync();

        numbers.Should().Equal(new[] { 1, 3, 4 },
            "the platform-made version survives and the next oldest eligible version is pruned");
    }

    [Fact]
    public async Task Hold_lets_the_cap_be_exceeded_with_a_warning_instead_of_refusing_the_capture()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        db.ProjectFiles.Add(NewFile(project.Id, "userFiles"));
        await db.SaveChangesAsync();

        RecordingLogger<ProjectHistoryService> logger = new();
        ProjectHistoryService service = NewService(db, out _, logger: logger,
            options: new ProjectHistoryOptions { MaxNonNamedVersions = 2 });

        for (int i = 0; i < 2; i++)
        {
            project.Description = $"revision {i}";
            await db.SaveChangesAsync();
            await service.CaptureAsync(
                project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None);
        }

        foreach (ProjectVersionItem item in await db.ProjectVersionItems.ToListAsync())
            item.PlatformMade = true;

        project.CsamOutputHoldAt = DateTime.UtcNow;
        project.Description = "after the hold";
        await db.SaveChangesAsync();

        ProjectVersion captured = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;

        captured.Number.Should().Be(3, "the capture is not refused; the cap is exceeded while the hold lasts");
        (await db.ProjectVersions.CountAsync()).Should().Be(3);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning,
            "the exceeded cap is recorded");
    }

    [Fact]
    public async Task Named_version_cap_refuses_a_new_named_version()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _, options: new ProjectHistoryOptions
        {
            MaxNamedVersions = 1
        });

        await service.CaptureAsync(
            project.Id, ProjectVersionCause.Named, "One", null, CancellationToken.None);

        ProjectHistoryNamedVersionLimitException exception =
            await Assert.ThrowsAsync<ProjectHistoryNamedVersionLimitException>(() =>
                service.CaptureAsync(
                    project.Id, ProjectVersionCause.Named, "Two", null, CancellationToken.None));

        exception.Code.Should().Be(ProjectHistoryErrorCodes.NamedVersionLimit);
    }

    [Fact]
    public async Task Too_many_files_is_refused()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        db.ProjectFiles.AddRange(NewFile(project.Id, "userFiles"), NewFile(project.Id, "agentFiles"));
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _, options: new ProjectHistoryOptions
        {
            MaxFilesPerVersion = 1
        });

        ProjectHistoryTooLargeException exception =
            await Assert.ThrowsAsync<ProjectHistoryTooLargeException>(() =>
                service.CaptureAsync(
                    project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None));

        exception.Code.Should().Be(ProjectHistoryErrorCodes.ProjectTooLargeToVersion);
        (await db.ProjectVersions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_known_hash_needs_no_head_and_an_unhashed_file_gets_an_etag_identity()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);

        string hash = new('a', 64);
        ProjectFile hashed = NewFile(project.Id, "userFiles");
        hashed.ContentSha256 = hash;
        hashed.ContentSha256ETag = "\"known\"";
        hashed.StorageKey = "projects/" + project.Id + "/userFiles/hashed.bin";

        ProjectFile unhashed = NewFile(project.Id, "agentFiles");
        db.ProjectFiles.AddRange(hashed, unhashed);
        await db.SaveChangesAsync();

        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.GetObjectETagAsync(
                project.Id, unhashed.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync("\"from-head\"");
        storage.Setup(s => s.GetObjectETagAsync(
                project.Id, hashed.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync("\"should-not-be-read\"");

        ProjectHistoryService service = NewService(db, out _, storage);
        ProjectVersion version = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;

        List<ProjectVersionItem> files = await db.ProjectVersionItems
            .Where(i => i.VersionId == version.Id && i.Kind == ProjectVersionItemKind.File)
            .ToListAsync();

        ProjectVersionItem hashedItem = files.Single(i => i.ItemKey == hashed.Id.ToString("D"));
        hashedItem.ContentId.Should().Be("sha256:" + hash);
        hashedItem.StorageETag.Should().Be("\"known\"");

        ProjectVersionItem unhashedItem = files.Single(i => i.ItemKey == unhashed.Id.ToString("D"));
        unhashedItem.ContentId.Should().StartWith("etag:");
        unhashedItem.StorageETag.Should().Be("\"from-head\"");

        storage.Verify(s => s.GetObjectETagAsync(
            project.Id, hashed.StorageKey, It.IsAny<CancellationToken>()), Times.Never,
            "a row with a known hash and its ETag costs no HEAD");
    }

    [Fact]
    public async Task Files_past_the_head_cap_are_stored_unverified()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        ProjectFile file = NewFile(project.Id, "userFiles");
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();

        Mock<IFileStorageService> storage = new();
        ProjectHistoryService service = NewService(db, out _, storage, options: new ProjectHistoryOptions
        {
            MaxHeadsPerCapture = 0
        });

        ProjectVersion version = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;

        ProjectVersionItem item = await db.ProjectVersionItems.SingleAsync(i =>
            i.VersionId == version.Id && i.Kind == ProjectVersionItemKind.File);

        item.ContentId.Should().Be("unverified:" + file.Id.ToString("D"));
        item.StorageETag.Should().BeNull();
        storage.Verify(s => s.GetObjectETagAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BuildLiveManifest_writes_nothing()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        db.ProjectFiles.Add(NewFile(project.Id, "userFiles"));
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _);
        ProjectHistoryManifest? manifest = await service.BuildLiveManifestAsync(project.Id, CancellationToken.None);

        manifest.Should().NotBeNull();
        manifest!.ManifestDigest.Should().HaveLength(64);
        manifest.Items.Should().Contain(i => i.Kind == ProjectVersionItemKind.File);

        (await db.ProjectVersions.CountAsync()).Should().Be(0);
        (await db.ProjectHistoryDocuments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TryCaptureAuto_is_best_effort_and_never_throws()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _);

        // An unknown cause would throw from CaptureAsync; the automatic path swallows it.
        Func<Task> act = () => service.TryCaptureAutoAsync(
            project.Id, "not-a-cause", null, CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await db.ProjectVersions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TryCaptureAuto_is_a_noop_when_auto_capture_is_off()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _, options: new ProjectHistoryOptions
        {
            AutoCapture = false
        });

        await service.TryCaptureAutoAsync(
            project.Id, ProjectVersionCause.BeforeRun, null, CancellationToken.None);

        (await db.ProjectVersions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_expired_newest_version_is_not_reused()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryService service = NewService(db, out _, clock: new FixedTimeProvider(FixedNow));
        ProjectHistoryManifest manifest =
            (await service.BuildLiveManifestAsync(project.Id, CancellationToken.None))!;

        // The newest version has the same digest but expired a day ago: it is gone from the read
        // surface, so reusing it would lose the safety point (QA finding 6).
        db.ProjectVersions.Add(new ProjectVersion
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Number = 1,
            Cause = ProjectVersionCause.Auto,
            ExpiresAt = FixedNow.UtcDateTime.AddDays(-1),
            ManifestDigest = manifest.ManifestDigest,
            ItemCount = manifest.Items.Count
        });
        await db.SaveChangesAsync();

        ProjectVersion captured = (await service.CaptureAsync(
            project.Id, ProjectVersionCause.Auto, null, null, CancellationToken.None))!;

        captured.Number.Should().Be(2, "an expired version must not be returned");
        captured.ExpiresAt.Should().Be(FixedNow.UtcDateTime.AddDays(30));
    }

    [Fact]
    public async Task Dedupe_extends_the_retention_of_the_reused_version()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = SeedProject(db);
        await db.SaveChangesAsync();

        ProjectHistoryService firstService = NewService(db, out _, clock: new FixedTimeProvider(FixedNow));
        ProjectVersion first = (await firstService.CaptureAsync(
            project.Id, ProjectVersionCause.BeforeRun, null, null, CancellationToken.None))!;
        first.ExpiresAt.Should().Be(FixedNow.UtcDateTime.AddDays(30));

        DateTimeOffset later = FixedNow.AddDays(29);
        ProjectHistoryService laterService = NewService(db, out _, clock: new FixedTimeProvider(later));
        ProjectVersion reused = (await laterService.CaptureAsync(
            project.Id, ProjectVersionCause.BeforeRun, null, null, CancellationToken.None))!;

        reused.Id.Should().Be(first.Id, "the manifest is unchanged, so the live version is reused");
        reused.ExpiresAt.Should().Be(later.UtcDateTime.AddDays(30),
            "reuse refreshes the retention window instead of letting the safety point expire");
        (await db.ProjectVersions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TryCaptureAuto_detaches_a_failed_capture_so_the_callers_save_writes_nothing()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"project-history-fail-{Guid.NewGuid():N}")
            .Options;

        Guid projectId;
        using (InferenceApiDbContext seed = new(options))
        {
            projectId = SeedProject(seed).Id;
            await seed.SaveChangesAsync();
        }

        using FailingOnceDbContext caller = new(options);
        ProjectHistoryService service = new(
            caller,
            new Mock<IFileStorageService>().Object,
            Options.Create(new ProjectHistoryOptions()),
            NullLogger<ProjectHistoryService>.Instance,
            new FixedTimeProvider(FixedNow));

        // The first SaveChanges (inside capture) throws after the version, items and documents are
        // tracked; the automatic path must swallow it and leave nothing behind (QA finding 5).
        await service.TryCaptureAutoAsync(
            projectId, ProjectVersionCause.BeforeRun, null, CancellationToken.None);

        await caller.SaveChangesAsync(CancellationToken.None);

        using InferenceApiDbContext verify = new(options);
        (await verify.ProjectVersions.CountAsync()).Should().Be(0);
        (await verify.ProjectVersionItems.CountAsync()).Should().Be(0);
        (await verify.ProjectHistoryDocuments.CountAsync()).Should().Be(0);
    }

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"project-history-{Guid.NewGuid():N}")
            .Options);

    private static ProjectHistoryService NewService(
        InferenceApiDbContext db,
        out Mock<IFileStorageService> storage,
        Mock<IFileStorageService>? storageMock = null,
        ProjectHistoryOptions? options = null,
        TimeProvider? clock = null,
        ILogger<ProjectHistoryService>? logger = null)
    {
        storage = storageMock ?? new Mock<IFileStorageService>();
        if (storageMock is null)
        {
            storage.Setup(s => s.GetObjectETagAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);
        }

        return new ProjectHistoryService(
            db,
            storage.Object,
            Options.Create(options ?? new ProjectHistoryOptions()),
            logger ?? NullLogger<ProjectHistoryService>.Instance,
            clock ?? TimeProvider.System);
    }

    private static Project SeedProject(InferenceApiDbContext db, string name = "Project")
    {
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid()
        };
        db.Projects.Add(project);
        return project;
    }

    private static ProjectFile NewFile(Guid projectId, string category) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Category = category,
        OriginalFileName = "file.bin",
        OriginalPath = "folder/file.bin",
        StorageKey = $"projects/{projectId}/{category}/{Guid.NewGuid():N}.bin",
        StorageBucket = "test",
        MimeType = "application/octet-stream",
        SizeBytes = 123
    };

    private static WorkflowDefinition NewWorkflow(Guid projectId, WorkflowDefinitionKind kind)
    {
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = "Workflow",
            Kind = kind
        };

        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Agent
        });

        return workflow;
    }

    private static EditTimelineEntity NewTimeline(Guid projectId, bool deleted) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Name = "Timeline",
        DocumentJson = """{"tracks":[]}""",
        Revision = 1,
        DeletedAt = deleted ? DateTime.UtcNow : null
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Collects log entries so a test can assert a warning was recorded.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Throws on the first SaveChanges only, so a capture fails after it has tracked rows.</summary>
    private sealed class FailingOnceDbContext(DbContextOptions<InferenceApiDbContext> options)
        : InferenceApiDbContext(options)
    {
        private bool _fail = true;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_fail)
            {
                _fail = false;
                throw new InvalidOperationException("database unavailable");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
