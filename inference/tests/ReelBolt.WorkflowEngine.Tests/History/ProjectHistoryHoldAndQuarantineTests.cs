using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>
/// T-021: the output hold protects platform-made history items from deletion and retention GC,
/// quarantined files are erased from history retryably, quarantine records the move before erasing,
/// and a discarded Block still erases the blocked bytes from history.
/// </summary>
public class ProjectHistoryHoldAndQuarantineTests
{
    // ── retention GC ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gc_defers_expired_versions_and_blobs_of_a_held_project_but_frees_the_rest()
    {
        using GcHarness h = new(withEraser: false);
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Held",
                OwnerId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid(),
                CsamOutputHoldAt = now.AddMinutes(-5)
            });

            ProjectVersion held = Expired(db, projectId, 1, now);
            ProjectVersion heldErased = Expired(db, projectId, 2, now);
            ProjectVersion free = Expired(db, projectId, 3, now);

            string liveKey = $"projects/{projectId}/agentFiles/generated/clip.mp4";
            db.ProjectVersionItems.AddRange(
                FileItem(held, projectId, fileId, "etag:held", platformMade: true, liveKey),
                FileItem(heldErased, projectId, fileId, "etag:held-erased", platformMade: true, liveKey,
                    erasedAt: now),
                FileItem(free, projectId, fileId, "etag:free", platformMade: false, liveKey));

            foreach (string contentId in new[] { "etag:held", "etag:held-erased", "etag:free" })
            {
                string blobKey = $"projects/{projectId}/history/blobs/{contentId.Replace(':', '-')}";
                h.Store.Put(blobKey, "bytes");
                db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
                {
                    ProjectId = projectId,
                    ContentId = contentId,
                    StorageKey = blobKey,
                    SizeBytes = 5
                });
            }

            await db.SaveChangesAsync();
        }

        ProjectHistoryGcResult result = await h.Gc().SweepOnceAsync(CancellationToken.None);

        result.Versions.Should().Be(1, "only the version without a platform-made item expires");
        result.Blobs.Should().Be(1, "only the unheld orphan blob is freed");

        using InferenceApiDbContext verify = h.NewDb();
        (await verify.ProjectVersions.Select(v => v.Number).OrderBy(n => n).ToListAsync())
            .Should().Equal(1, 2);
        (await verify.ProjectHistoryBlobs.Select(b => b.ContentId).OrderBy(c => c).ToListAsync())
            .Should().BeEquivalentTo(["etag:held", "etag:held-erased"]);

        h.Store.Objects.Keys.Should().Contain(k => k.Contains("etag-held", StringComparison.Ordinal));
        h.Store.Objects.Keys.Should().Contain(k => k.Contains("etag-held-erased", StringComparison.Ordinal));
        h.Store.Objects.Keys.Should().NotContain(k => k.Contains("etag-free", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gc_never_deletes_a_blob_referenced_by_a_platform_made_item_under_hold()
    {
        using GcHarness h = new(withEraser: false);
        Guid projectId = Guid.NewGuid();
        Guid heldFileId = Guid.NewGuid();
        Guid freeFileId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Held",
                OwnerId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid(),
                CsamOutputHoldAt = now.AddMinutes(-5)
            });

            // Both items are already erased, so their blobs look orphaned to the GC. The held one is
            // still named by a platform-made item; the free one is not.
            ProjectVersion heldVersion = Expired(db, projectId, 1, now);
            ProjectVersion freeVersion = Expired(db, projectId, 2, now);
            string liveKey = $"projects/{projectId}/agentFiles/generated/clip.mp4";
            db.ProjectVersionItems.AddRange(
                FileItem(heldVersion, projectId, heldFileId, "etag:held", platformMade: true, liveKey,
                    erasedAt: now),
                FileItem(freeVersion, projectId, freeFileId, "etag:free", platformMade: false, liveKey,
                    erasedAt: now));

            foreach (string contentId in new[] { "etag:held", "etag:free" })
            {
                string blobKey = $"projects/{projectId}/history/blobs/{contentId.Replace(':', '-')}";
                h.Store.Put(blobKey, "bytes");
                db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
                {
                    ProjectId = projectId,
                    ContentId = contentId,
                    StorageKey = blobKey,
                    SizeBytes = 5
                });
            }

            await db.SaveChangesAsync();
        }

        ProjectHistoryGcResult result = await h.Gc().SweepOnceAsync(CancellationToken.None);

        result.Blobs.Should().Be(1, "only the blob no platform-made item names is freed");
        using InferenceApiDbContext verify = h.NewDb();
        (await verify.ProjectHistoryBlobs.Select(b => b.ContentId).ToListAsync())
            .Should().BeEquivalentTo(["etag:held"]);
        h.Store.Objects.Keys.Should().Contain(k => k.Contains("etag-held", StringComparison.Ordinal));
        h.Store.Objects.Keys.Should().NotContain(k => k.Contains("etag-free", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gc_erases_the_history_of_a_quarantined_file_even_when_a_previous_erase_raced()
    {
        using GcHarness h = new(withEraser: true);
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Quarantined",
                OwnerId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid()
            });
            ProjectFile file = new()
            {
                Id = fileId,
                ProjectId = projectId,
                Category = "userFiles",
                OriginalFileName = "bad.jpg",
                StorageKey = $"projects/{projectId}/quarantine/{fileId}/bad.jpg",
                StorageBucket = "t",
                MimeType = "image/jpeg",
                SizeBytes = 5,
                ModerationStatus = FileModerationStatus.Quarantined
            };
            db.ProjectFiles.Add(file);

            ProjectVersion version = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Number = 1,
                Cause = ProjectVersionCause.Auto,
                CreatedAt = now.AddHours(-1),
                ExpiresAt = now.AddDays(10),
                ManifestDigest = new string('a', 64)
            };
            db.ProjectVersions.Add(version);

            string blobKey = $"projects/{projectId}/history/blobs/race";
            h.Store.Put(blobKey, "blocked");
            db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
            {
                ProjectId = projectId,
                ContentId = "etag:blocked",
                StorageKey = blobKey,
                SizeBytes = 7
            });
            db.ProjectVersionItems.Add(FileItem(
                version, projectId, fileId, "etag:blocked", platformMade: false,
                $"projects/{projectId}/userFiles/{fileId:N}.jpg"));
            await db.SaveChangesAsync();
        }

        await h.Gc().SweepOnceAsync(CancellationToken.None);

        using InferenceApiDbContext verify = h.NewDb();
        (await verify.ProjectVersionItems.AsNoTracking().SingleAsync()).ErasedAt.Should().NotBeNull(
            "the queued quarantine erase is retried by the GC");
        (await verify.ProjectHistoryBlobs.CountAsync()).Should().Be(0);
        h.Store.Objects.Should().NotContainKey($"projects/{projectId}/history/blobs/race");
    }

    // ── CSAM quarantine ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Csam_quarantine_records_the_move_before_erasing_and_logs_an_eraser_failure()
    {
        using HistoryHarness h = new();
        (Guid projectId, Guid fileId, string key) = await h.SeedVersionedFileAsync("bad");

        using InferenceApiDbContext db = h.NewDb();
        ProjectFile file = await db.ProjectFiles.SingleAsync(f => f.Id == fileId);
        RecordingLogger<CsamQuarantineService> logger = new();
        CsamQuarantineService quarantine = new(
            h.Decorator, historyEraser: new ThrowingEraser(), logger: logger);

        string destination = await quarantine.MoveAsync(file, CancellationToken.None);

        destination.Should().Be($"projects/{projectId}/quarantine/{fileId}/a.txt");
        file.StorageKey.Should().Be(destination, "the row names the evidence before the eraser can fail");
        file.CsamQuarantineKey.Should().Be(destination);
        h.Store.Objects.Should().ContainKey(destination).And.NotContainKey(key);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error,
            "the eraser failure is logged, but never hides or undoes the successful move");
    }

    // ── moderation ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_block_discarded_because_the_bytes_changed_mid_scan_erases_the_file_history()
    {
        string databaseName = $"history-stale-block-{Guid.NewGuid():N}";
        using InferenceApiDbContext db = new(DbOptions(databaseName));
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        string key = $"projects/{projectId}/userFiles/photo.png";
        Project project = new()
        {
            Id = projectId,
            Name = "P",
            OwnerId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid()
        };
        ProjectFile file = new()
        {
            Id = fileId,
            ProjectId = projectId,
            Category = "userFiles",
            OriginalFileName = "photo.png",
            OriginalPath = "photo.png",
            StorageKey = key,
            StorageFileName = "photo.png",
            StoragePrefix = $"projects/{projectId}/userFiles/",
            StorageBucket = "t",
            MimeType = "image/png",
            SizeBytes = 2,
            CsamStatus = CsamScanStatus.NotScanned,
            ModerationStatus = FileModerationStatus.Unmoderated
        };
        db.Projects.Add(project);
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();

        HistoryTestStore store = new();
        store.Put(key, "v1");
        RecordingEraser eraser = new();
        FakeEnqueuer enqueuer = new();
        ContentModerationProcessor processor = new(
            db,
            store,
            new StaticSampler(),
            new BlockGate(store, key),
            Mock.Of<IInferenceProviderResolver>(),
            Mock.Of<ITranscriptionClientFactory>(),
            Mock.Of<IPublishEndpoint>(),
            Options.Create(new ContentModerationOptions { Enabled = true }),
            NullLogger<ContentModerationProcessor>.Instance,
            tenant: null,
            enqueuer: enqueuer,
            csamOptions: null,
            historyEraser: eraser);

        await processor.ProcessAsync(fileId, CancellationToken.None);

        eraser.Erased.Should().ContainSingle().Which.Should().Be((projectId, fileId),
            "the blocked bytes were already copied into history by the overwrite and must be erased there");
        using InferenceApiDbContext verify = new(DbOptions(databaseName));
        (await verify.ProjectFiles.SingleAsync(f => f.Id == fileId)).ModerationStatus
            .Should().Be(FileModerationStatus.Unmoderated, "the stale verdict is not applied");
        enqueuer.Calls.Should().Contain(fileId);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static DbContextOptions<InferenceApiDbContext> DbOptions(string databaseName) =>
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

    private static ProjectVersion Expired(
        InferenceApiDbContext db, Guid projectId, int number, DateTime now)
    {
        ProjectVersion version = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Number = number,
            Cause = ProjectVersionCause.Auto,
            CreatedAt = now.AddDays(-2),
            ExpiresAt = now.AddDays(-1),
            ManifestDigest = new string('a', 64)
        };
        db.ProjectVersions.Add(version);
        return version;
    }

    private static ProjectVersionItem FileItem(
        ProjectVersion version,
        Guid projectId,
        Guid fileId,
        string contentId,
        bool platformMade,
        string storageKey,
        DateTime? erasedAt = null) => new()
        {
            Id = Guid.NewGuid(),
            VersionId = version.Id,
            ProjectId = projectId,
            Kind = ProjectVersionItemKind.File,
            ItemKey = fileId.ToString("D"),
            Name = "a.mp4",
            Category = "userFiles",
            SizeBytes = 5,
            ContentId = contentId,
            StorageKey = storageKey,
            PlatformMade = platformMade,
            OutputProducedAt = platformMade ? DateTime.UtcNow.AddDays(-3) : null,
            ErasedAt = erasedAt
        };

    private static IOptionsMonitor<ProjectHistoryOptions> OptionsOf(ProjectHistoryOptions value) =>
        Mock.Of<IOptionsMonitor<ProjectHistoryOptions>>(m => m.CurrentValue == value);

    private sealed class GcHarness : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _dbName;

        public GcHarness(bool withEraser)
        {
            _dbName = $"history-gc-{Guid.NewGuid():N}";
            ServiceCollection services = new();
            services.AddScoped(_ => new InferenceApiDbContext(DbOptions(_dbName)));
            services.AddSingleton<IFileStorageService>(Store);
            if (withEraser)
            {
                services.AddScoped<IProjectHistoryEraser>(sp => new ProjectHistoryEraser(
                    sp.GetRequiredService<InferenceApiDbContext>(),
                    Store,
                    NullLogger<ProjectHistoryEraser>.Instance));
            }

            _provider = services.BuildServiceProvider();
        }

        public HistoryTestStore Store { get; } = new();

        public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

        public InferenceApiDbContext NewDb() => new(DbOptions(_dbName));

        public ProjectHistoryRetentionService Gc() => new(
            ScopeFactory, OptionsOf(new ProjectHistoryOptions()),
            NullLogger<ProjectHistoryRetentionService>.Instance);

        public void Dispose() => _provider.Dispose();
    }

    private sealed class ThrowingEraser : IProjectHistoryEraser
    {
        public Task EraseFileAsync(Guid projectId, Guid fileId, CancellationToken ct) =>
            throw new IOException("eraser unavailable");

        public Task DeleteProjectObjectsAsync(Guid projectId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RecordingEraser : IProjectHistoryEraser
    {
        public List<(Guid ProjectId, Guid FileId)> Erased { get; } = [];

        public Task EraseFileAsync(Guid projectId, Guid fileId, CancellationToken ct)
        {
            Erased.Add((projectId, fileId));
            return Task.CompletedTask;
        }

        public Task DeleteProjectObjectsAsync(Guid projectId, CancellationToken ct) =>
            Task.CompletedTask;
    }

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

    private sealed class StaticSampler : IModerationMediaSampler
    {
        public Task<ModerationMediaSample> SampleAsync(Stream media, string mimeType, CancellationToken ct) =>
            Task.FromResult(new ModerationMediaSample(
                [new byte[] { 1, 2, 3 }], null, ModerationContentKind.Image, null));
    }

    /// <summary>The bytes are replaced after the last pre-call check and before the apply.</summary>
    private sealed class BlockGate : IContentModerationGate
    {
        private readonly HistoryTestStore _store;
        private readonly string _key;

        public BlockGate(HistoryTestStore store, string key)
        {
            _store = store;
            _key = key;
        }

        public Task<ModerationDecision> ModerateAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            _store.Put(_key, "v2");
            return Task.FromResult(new ModerationDecision(
                ModerationAction.Block, Available: true, ReviewQueued: false,
                "violence", "blocked", "hash"));
        }

        public Task<bool> ScreenGenerationPromptAsync(
            string text, IReadOnlyList<ModerationImageInput> images,
            ModerationAttribution attribution, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ScreenGeneratedTextAsync(
            string text, string site, ModerationAttribution attribution, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ScreenPublishAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeEnqueuer : IContentModerationEnqueuer
    {
        public List<Guid> Calls { get; } = [];

        public bool TryQueue(Guid fileId)
        {
            Calls.Add(fileId);
            return true;
        }
    }
}
