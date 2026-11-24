#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The storage quota across replicas on a REAL PostgreSQL (T-114, plan Task 3 steps 1-2). Two replicas are
/// two <see cref="OrganizationStorageLock"/> instances over one migrated database, each driving its own
/// <see cref="ProjectFilesController"/>; the only thing that can serialise the two deletes is the
/// <c>pg_advisory_xact_lock</c> the guard takes. The plan gate reads the real usage through
/// <see cref="StorageUsageService"/> and holds the first check until the second arrives, so without the
/// advisory lock both replicas see the same pre-delete usage and both admit a quota that only one fits.
/// </summary>
/// <remarks>
/// Skipped (returns immediately) unless <c>REELBOLT_TEST_PG</c> is set; each test creates and drops its own
/// throwaway database, as <c>EditImagePictureSourceRealPostgresTests</c> does. Run through
/// <c>inference/tests/run-postgres-tests.sh</c> with <c>REELBOLT_PGTEST_FILTER</c>.
/// </remarks>
[Collection("Postgres")]
public class StorageQuotaCrossReplicaPostgresTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Two_replicas_cannot_both_pass_a_quota_that_only_one_delete_fits()
    {
        await WithDatabaseAsync(async (options, connectionString) =>
        {
            RecordingStorage storage = new();
            Seed seed = await SeedAsync(options, connectionString, storage);
            long size = seed.PhotoLength;
            long usedAtStart;
            await using (InferenceApiDbContext read = new(options))
            {
                usedAtStart = await new StorageUsageService(read, Mock.Of<IOrganizationEntitlements>())
                    .GetUsedBytesAsync(seed.OrganizationId, CancellationToken.None);
            }

            usedAtStart.Should().Be(2 * size, "two photos, no copies yet");
            // exactly one delete fits: three pictures' copies minus the deleted photo's own bytes is two sizes
            UsageGate gate = new(() => new InferenceApiDbContext(options), usedAtStart + 2 * size, TimeSpan.FromSeconds(2));
            OrganizationStorageLock lockA = new();
            OrganizationStorageLock lockB = new();
            await using InferenceApiDbContext dbA = new(options);
            await using InferenceApiDbContext dbB = new(options);
            ProjectFilesController filesA = Controller(dbA, seed, storage, gate, lockA);
            ProjectFilesController filesB = Controller(dbB, seed, storage, gate, lockB);

            Task<(int Status, JsonElement Body)> a = Delete(filesA, seed.ProjectId, seed.PhotoAId);
            Task<(int Status, JsonElement Body)> b = Delete(filesB, seed.ProjectId, seed.PhotoBId);
            (int statusA, JsonElement bodyA) = await a;
            (int statusB, JsonElement bodyB) = await b;

            new[] { statusA, statusB }.Order().Should().Equal(204, 409);
            JsonElement refused = statusA == 409 ? bodyA : bodyB;
            refused.GetProperty("code").GetString().Should().Be(PictureSourceCopies.OverQuotaCode);

            // the loser copied nothing, so exactly one photo's three pictures each got their own copy
            storage.Copies.Should().HaveCount(3).And.OnlyContain(c => c.From == seed.PhotoAKey || c.From == seed.PhotoBKey);
            await using (InferenceApiDbContext read = new(options))
            {
                long used = await new StorageUsageService(read, Mock.Of<IOrganizationEntitlements>())
                    .GetUsedBytesAsync(seed.OrganizationId, CancellationToken.None);
                used.Should().BeLessThanOrEqualTo(usedAtStart + 2 * size, "the organization's recorded usage is still within the limit");
                (await read.ProjectFiles.CountAsync(f => f.ProjectId == seed.ProjectId)).Should().Be(1, "exactly one photo was deleted");
            }
        });
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task The_advisory_lock_is_held_while_a_delete_records()
    {
        await WithDatabaseAsync(async (options, connectionString) =>
        {
            RecordingStorage storage = new();
            Seed seed = await SeedAsync(options, connectionString, storage);
            HoldingGate gate = new();
            OrganizationStorageLock lockA = new();
            await using InferenceApiDbContext dbA = new(options);
            ProjectFilesController filesA = Controller(dbA, seed, storage, gate, lockA);

            Task<(int Status, JsonElement Body)> delete = Delete(filesA, seed.ProjectId, seed.PhotoAId);

            // Replica A is parked at its quota check with the guard's transaction open: the advisory lock is held.
            await gate.Arrived;

            await using NpgsqlConnection probe = new(seed.ConnectionString);
            await probe.OpenAsync();
            await using NpgsqlCommand command = new("SELECT pg_try_advisory_xact_lock(hashtext($1))", probe);
            command.Parameters.AddWithValue(seed.OrganizationId.ToString());
            object? locked = await command.ExecuteScalarAsync();
            locked.Should().Be(false, "a second connection cannot take the organization's advisory lock while replica A records");

            gate.Release();
            (await delete).Status.Should().Be(204);
        });
    }

    /// <summary>
    /// T-106 finding 2: a duplicate apply — the row another replica committed between this apply's "already
    /// saved" check and its insert — answers that row instead of failing. The collision is real (the id is the
    /// deterministic SaveFileId for the canonical token, and the winner's size is not the copy's size, so a
    /// passing assertion cannot come from an insert that quietly succeeded).
    /// <para>
    /// This test pins behaviour, not a fix: it passes with and without the guard disposal that was proposed
    /// for it, because EF's BatchExecutor rolls a failed <c>SaveChangesAsync</c> back to its own savepoint, so
    /// the recovery query runs on a usable transaction either way. The disposal was removed again - the
    /// security review (SQCR-S-02) found that ending the guard there released the organization's semaphore
    /// before this recovery judges and removes the copy it made, which lets a concurrent same-token apply have
    /// its object deleted underneath it.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task A_duplicate_apply_answers_the_row_another_replica_saved()
    {
        await WithDatabaseAsync(async (options, connectionString) =>
        {
            RecordingStorage storage = new();
            Seed seed = await SeedAsync(options, connectionString, storage);
            await MigrateEngineAsync(connectionString); // a save reads the render's stamps from the engine's tables
            string renderKey = $"projects/{seed.ProjectId}/outputFiles/render/final-cut.mp4";
            storage.Objects[renderKey] = new byte[1000];
            Guid stepResultId = Guid.NewGuid();
            ConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
            CollidingGate gate = new(() => new InferenceApiDbContext(options));
            AssistantOutputTools tools = OutputTools(seed, storage, renderKey, stepResultId, tokens, gate);

            string token = (await Invoke(tools, seed, "ProposeSaveOutputAsFile",
                ("outputId", stepResultId), ("fileName", "teaser.mp4"))).GetProperty("confirmationToken").GetString()!;

            // The row the "other replica" committed between this apply's "already saved" check and its insert.
            // Its id is the deterministic SaveFileId the apply itself will use - the confirmation token IS the
            // canonical token - so the apply's insert really collides on the primary key. The size is
            // deliberately not the copy's size, so a passing assertion cannot come from an insert that quietly
            // succeeded instead of from the recovery.
            string payload = AssistantOutputTools.BuildSaveOutputPayload(seed.ProjectId, stepResultId, "teaser.mp4", 1000);
            string canonical = tokens.Compute(AssistantOutputTools.OpSaveOutputAsFile, payload, seed.OwnerId, seed.OrganizationId);
            canonical.Should().Be(token, "the proposed token is the canonical one, which is what makes the collision deterministic");
            Guid fileId = new(SHA256.HashData(Encoding.UTF8.GetBytes("SaveOutputAsFile.file|" + canonical)).AsSpan(0, 16));
            string key = ProjectFilePath.BuildStorageKey(
                seed.ProjectId, "userFiles", null, ProjectFilePath.BuildStorageFileName(fileId, "teaser.mp4"));
            gate.Winner = new ProjectFile
            {
                Id = fileId,
                ProjectId = seed.ProjectId,
                OriginalFileName = "teaser.mp4",
                OriginalPath = "teaser.mp4",
                Category = "userFiles",
                StorageKey = key,
                StorageBucket = "test-bucket",
                MimeType = "video/mp4",
                SizeBytes = 999,
                UploadedAt = DateTime.UtcNow
            };

            JsonElement applied = await Invoke(tools, seed, "ApplySaveOutputAsFile",
                ("outputId", stepResultId), ("fileName", "teaser.mp4"), ("confirmationToken", token));

            applied.TryGetProperty("error", out _).Should().BeFalse(applied.GetRawText());
            applied.GetProperty("fileId").GetGuid().Should().Be(fileId, "the duplicate apply answers the saved row, not a failure");
            applied.GetProperty("fileName").GetString().Should().Be("teaser.mp4");
            applied.GetProperty("sizeBytes").GetInt64().Should().Be(999,
                "the answer is the winner's row (999), not this apply's own copy (1000) - so the recovery really ran");

            await using InferenceApiDbContext read = new(options);
            (await read.ProjectFiles.CountAsync(f => f.Id == fileId)).Should().Be(1, "exactly the winner's row exists");
        });
    }

    /// <summary>
    /// QA-SQC-6 / T-114 follow-up: the two-tier purge predicate must translate on Npgsql and must purge a
    /// delete-written mark after the short delete grace while leaving a retention mark inside its full
    /// grace alone. Runs on the real provider because the predicate mixes a parameter-array negation.
    /// </summary>
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task The_two_tier_purge_honours_the_delete_grace_on_postgres()
    {
        await WithDatabaseAsync(async (options, connectionString) =>
        {
            RecordingStorage storage = new();
            Seed seed = await SeedAsync(options, connectionString, storage);
            await MigrateEngineAsync(connectionString);
            string deleted = $"projects/{seed.ProjectId}/userFiles/deleted.png";
            string retained = $"projects/{seed.ProjectId}/agentFiles/step-1-analysis.json";
            DateTime now = DateTime.UtcNow;
            storage.Objects[deleted] = [1];
            storage.Objects[retained] = [1];

            await using (InferenceApiDbContext setup = new(options))
            {
                setup.StorageRetentionMarks.AddRange(
                    new StorageRetentionMark
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = seed.OrganizationId,
                        ProjectId = seed.ProjectId,
                        StorageKey = deleted,
                        Category = "userFiles",
                        MarkedAt = now.AddHours(-2)
                    },
                    new StorageRetentionMark
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = seed.OrganizationId,
                        ProjectId = seed.ProjectId,
                        StorageKey = retained,
                        Category = "agentFiles",
                        MarkedAt = now.AddHours(-2)
                    });
                await setup.SaveChangesAsync();
            }

            await using (InferenceApiDbContext check = new(options))
            {
                DateTime purgeBefore = now.AddDays(-7);
                DateTime deletePurgeBefore = now.AddMinutes(-60);
                string[] generated = ["outputFiles", "agentFiles"];
                int due = await check.StorageRetentionMarks.IgnoreQueryFilters()
                    .CountAsync(m => m.PurgedAt == null
                        && (m.MarkedAt <= purgeBefore
                            || (m.MarkedAt <= deletePurgeBefore && !generated.Contains(m.Category))));
                due.Should().Be(1, "only the delete-written mark is due");
            }

            ListLogger logger = new();
            StorageRetentionService service = RetentionService(connectionString, storage, logger);
            StorageRetentionRunResult run = await service.RunOnceAsync(now, CancellationToken.None);

            run.PurgedObjects.Should().Be(1, "log: {0}", string.Join(" ;; ", logger.Messages));
            storage.Objects.Should().NotContainKey(deleted, "a delete mark is due after DeleteGraceMinutes");
            storage.Objects.Should().ContainKey(retained, "a retention mark keeps the full grace");
            await using (InferenceApiDbContext after = new(options))
            {
                List<StorageRetentionMark> marks = await after.StorageRetentionMarks.IgnoreQueryFilters().ToListAsync();
                marks.Should().NotContain(m => m.StorageKey == deleted, "a purged mark nothing references is garbage-collected");
                marks.Single(m => m.StorageKey == retained).PurgedAt.Should().BeNull();
            }
            storage.Objects.Should().NotContainKey(deleted, "a delete mark is due after DeleteGraceMinutes");
            storage.Objects.Should().ContainKey(retained, "a retention mark keeps the full grace");
        });
    }

    /// <summary>
    /// Creates the engine-owned tables the purge's reference-clearing reads. They are
    /// <c>ExcludeFromMigrations</c> on the API context (the engine owns their schema), so a freshly migrated
    /// API database does not have them; migrate the engine context as production does (API first, engine
    /// second) so the sweep can clear the references it must.
    /// </summary>
    private static async Task MigrateEngineAsync(string connectionString)
    {
        DbContextOptions<WorkflowEngineDbContext> options =
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connectionString).Options;
        await using WorkflowEngineDbContext engine = new(options);
        await engine.GetService<IMigrator>().MigrateAsync();
    }

    private sealed class ListLogger : ILogger<StorageRetentionService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " :: " + exception));
    }

    private static StorageRetentionService RetentionService(string connectionString, IFileStorageService storage, ILogger<StorageRetentionService>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton(storage);
        services.AddSingleton<ITenantContext>(new TenantContext(new HttpContextAccessor()));
        services.AddSingleton<IOrganizationEntitlements>(new RetentionPlanStub());
        IServiceProvider sp = services.BuildServiceProvider();
        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:StorageRetention:Enabled"] = "true"
        }).Build();
        return new StorageRetentionService(
            sp.GetRequiredService<IServiceScopeFactory>(), cfg, logger ?? NullLogger<StorageRetentionService>.Instance);
    }

    private sealed class RetentionPlanStub : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            new(new OrganizationEntitlementSet { RetentionDays = 30 });
    }

    // ---- harness -------------------------------------------------------------------------------

    private static async Task WithDatabaseAsync(Func<DbContextOptions<InferenceApiDbContext>, string, Task> body)
    {
        string? baseConnection = Environment.GetEnvironmentVariable("REELBOLT_TEST_PG");
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_quota_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(baseConnection) { Database = dbName };
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql(csb.ConnectionString).Options;
        try
        {
            await using (InferenceApiDbContext setup = new(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync();
            }

            await body(options, csb.ConnectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed record Seed(
        Guid OwnerId, Guid OrganizationId, Guid ProjectId,
        Guid PhotoAId, string PhotoAKey, Guid PhotoBId, string PhotoBKey, long PhotoLength, string ConnectionString);

    private static async Task<Seed> SeedAsync(DbContextOptions<InferenceApiDbContext> options, string connectionString, RecordingStorage storage)
    {
        Guid ownerId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(ownerId);
        Guid projectId = Guid.NewGuid();
        byte[] photo = EditImageDocumentControllersTests.PngOfSize(400, 300);

        ProjectFile photoA = NewPhoto(projectId, "a.png", photo.Length);
        ProjectFile photoB = NewPhoto(projectId, "b.png", photo.Length);
        storage.Objects[photoA.StorageKey] = photo;
        storage.Objects[photoB.StorageKey] = photo;

        await using InferenceApiDbContext setup = new(options);
        DateTime now = DateTime.UtcNow;
        setup.ApplicationUsers.Add(new ApplicationUser
        {
            Id = ownerId,
            Email = $"{ownerId:N}@example.com",
            DisplayName = "Ada",
            PasswordHash = "hash",
            CreatedAt = now,
            EmailVerifiedAt = now
        });
        setup.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "o",
            Slug = "o-" + organizationId.ToString("N"),
            Kind = OrganizationKind.Personal,
            CreatedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now
        });
        setup.OrganizationMemberships.Add(new OrganizationMembership
        {
            OrganizationId = organizationId,
            UserId = ownerId,
            Role = OrganizationRole.Owner,
            CreatedAt = now
        });
        setup.Projects.Add(new Project { Id = projectId, Name = "P", OwnerId = ownerId, OrganizationId = organizationId, CreatedAt = now, UpdatedAt = now });
        setup.ProjectFiles.AddRange(photoA, photoB);
        // three pictures use each photo: deleting one photo has a net growth of two photo sizes
        for (int i = 0; i < 3; i++)
        {
            setup.EditImageDocuments.Add(NewPicture(projectId, photoA));
            setup.EditImageDocuments.Add(NewPicture(projectId, photoB));
        }

        await setup.SaveChangesAsync();
        return new Seed(ownerId, organizationId, projectId, photoA.Id, photoA.StorageKey, photoB.Id, photoB.StorageKey, photo.Length, connectionString);
    }

    private static ProjectFile NewPhoto(Guid projectId, string name, long size) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        OriginalFileName = name,
        OriginalPath = name,
        Category = "userFiles",
        StorageKey = $"projects/{projectId}/userFiles/{name}",
        StorageBucket = "test-bucket",
        MimeType = "image/png",
        SizeBytes = size,
        UploadedAt = DateTime.UtcNow
    };

    private static EditImageDocument NewPicture(Guid projectId, ProjectFile file)
    {
        Guid id = Guid.NewGuid();
        EditImageDocument document = new()
        {
            Id = id,
            ProjectId = projectId,
            SourceProjectFileId = file.Id,
            CanvasWidth = 400,
            CanvasHeight = 300,
            Revision = 1
        };
        document.Layers.Add(new EditImageLayer
        {
            Id = Guid.NewGuid(),
            DocumentId = id,
            ZOrder = 1,
            Role = EditImageLayerRoles.Background,
            Kind = EditImageLayerKind.Source,
            Label = file.OriginalFileName,
            SourceProjectFileId = file.Id,
            StorageKey = file.StorageKey,
            StorageBucket = "test-bucket",
            MimeType = file.MimeType,
            TransformJson = "{}"
        });
        return document;
    }

    private static ProjectFilesController Controller(
        InferenceApiDbContext db, Seed seed, RecordingStorage storage, IPlanGateService? gate, OrganizationStorageLock storageLock)
    {
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(seed.OwnerId);
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.OrganizationId).Returns(seed.OrganizationId);
        user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Owner);
        return new ProjectFilesController(
            db, user.Object, TestTenancy.Access(db, user.Object), storage,
            new ChannelBackgroundTaskQueue<FileSummarizationTask>(4), Mock.Of<IPublishEndpoint>(),
            new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()),
            gate, storageLock: storageLock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static async Task<(int Status, JsonElement Body)> Delete(ProjectFilesController files, Guid projectId, Guid fileId) =>
        Read(await files.Delete(projectId, fileId, CancellationToken.None));

    private static (int Status, JsonElement Body) Read(IActionResult result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200, JsonSerializer.SerializeToElement(o.Value, Web)),
        NoContentResult => (204, default),
        StatusCodeResult s => (s.StatusCode, default),
        _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}")
    };

    /// <summary>
    /// The plan gate of the plan's Task 3: usage-aware (it reads the real usage through
    /// <see cref="StorageUsageService"/> from a fresh context) and it holds the first check until the second
    /// arrives — bounded by <c>overlap</c>, never an unbounded wait. Without the advisory lock both replicas
    /// reach it and both see the same pre-delete usage; with it, the second replica's check runs after the
    /// first replica committed.
    /// </summary>
    private sealed class UsageGate(Func<InferenceApiDbContext> newDb, long limitBytes, TimeSpan overlap) : IPlanGateService
    {
        private int _arrivals;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrivals) >= 2)
            {
                _both.TrySetResult();
            }

            await Task.WhenAny(_both.Task, Task.Delay(overlap));

            await using InferenceApiDbContext db = newDb();
            long used = await new StorageUsageService(db, Mock.Of<IOrganizationEntitlements>()).GetUsedBytesAsync(organizationId, ct);
            return used + incomingBytes > limitBytes
                ? new PlanGateViolation("STORAGE_QUOTA_EXCEEDED", "storage", "full", 413)
                : null;
        }

        public Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);
    }

    /// <summary>A gate that parks a delete at its quota check until the test releases it (bounded at 2 s).</summary>
    private sealed class HoldingGate : IPlanGateService
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Arrived => _arrived.Task;

        public void Release() => _release.TrySetResult();

        public async Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
        {
            _arrived.TrySetResult();
            await Task.WhenAny(_release.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            return null;
        }

        public Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);
    }

    /// <summary>A minimal <see cref="IFileStorageService"/> that records copies and objects, like the in-memory harness's fake.</summary>
    private sealed class RecordingStorage : IFileStorageService
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public List<(string From, string To)> Copies { get; } = [];

        public string BucketName => "test-bucket";

        public Task<StoredFileObject> UploadAsync(
            Guid projectId, Guid fileId, Stream content, string fileName, string contentType,
            IReadOnlyDictionary<string, string>? metadata, CancellationToken ct, string category = "userFiles",
            string? originalPath = null)
        {
            using MemoryStream copy = new();
            content.CopyTo(copy);
            string key = $"projects/{projectId}/{category}/{fileId}{Path.GetExtension(fileName)}";
            Objects[key] = copy.ToArray();
            return Task.FromResult(new StoredFileObject(
                key, BucketName, $"projects/{projectId}/{category}/", null, category, fileName, fileName, null, fileName));
        }

        public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Objects.TryGetValue(storageKey, out byte[]? bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes))
                : throw new AmazonS3Exception("The specified key does not exist.")
                {
                    StatusCode = System.Net.HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey"
                };

        public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
        {
            if (!Objects.TryGetValue(sourceStorageKey, out byte[]? bytes))
            {
                throw new AmazonS3Exception("The specified key does not exist.")
                {
                    StatusCode = System.Net.HttpStatusCode.NotFound,
                    ErrorCode = "NoSuchKey"
                };
            }

            Copies.Add((sourceStorageKey, destinationStorageKey));
            Objects[destinationStorageKey] = bytes;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            Objects.Remove(storageKey);
            return Task.CompletedTask;
        }

        public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult(Objects.TryGetValue(storageKey, out byte[]? bytes)
                ? new StoredObjectMetadata(bytes.Length, "image/png")
                : null);

        public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct) =>
            Task.FromResult(storageKeys.Count(k => Objects.Remove(k)));

        public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
        {
            Objects[destinationStorageKey] = Objects[sourceStorageKey];
            Objects.Remove(sourceStorageKey);
            return Task.CompletedTask;
        }

        public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult<string?>(Objects.ContainsKey(storageKey) ? $"\"{storageKey.GetHashCode():x8}\"" : null);
    }

    private static AssistantOutputTools OutputTools(
        Seed seed, RecordingStorage storage, string renderKey, Guid stepResultId,
        ConfirmationTokenService tokens, CollidingGate gate)
    {
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(seed.OwnerId);
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.OrganizationId).Returns(seed.OrganizationId);
        user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Owner);

        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql(seed.ConnectionString).Options);
        Mock<IOutputDescriber> describer = new();
        describer.Setup(d => d.FindRenderAsync(seed.ProjectId, stepResultId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutputRender(stepResultId, renderKey, "final-cut.mp4", "video/mp4", 1000));
        Mock<IStorageObjectCopier> copier = new();
        copier.Setup(c => c.CopyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid projectId, string source, string destination, CancellationToken ct) => storage.CopyAsync(projectId, source, destination, ct));
        return new AssistantOutputTools(
            db, tokens, TestTenancy.Access(db, user.Object), describer.Object, storage, copier.Object,
            Mock.Of<IPublishEndpoint>(), new ChannelBackgroundTaskQueue<FileSummarizationTask>(4), gate);
    }

    private static async Task<JsonElement> Invoke(AssistantOutputTools tools, Seed seed, string name, params (string Key, object? Value)[] args)
    {
        AIFunction tool = tools.GetTools(new AssistantToolContext(seed.OwnerId, seed.ProjectId)).Single(t => t.Name == name);
        AIFunctionArguments arguments = new();
        foreach ((string key, object? value) in args) arguments[key] = value;
        object? raw = await tool.InvokeAsync(arguments);
        string text = raw switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw!.ToString()!
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>
    /// The plan gate that plays the other replica: on its first check — which the apply makes between its
    /// "already saved" check and its insert — it commits the winning row from a separate connection and then
    /// admits this apply, so this apply's own insert collides on the primary key.
    /// </summary>
    private sealed class CollidingGate(Func<InferenceApiDbContext> newDb) : IPlanGateService
    {
        private int _calls;

        public ProjectFile? Winner { get; set; }

        public async Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1 && Winner is not null)
            {
                await using InferenceApiDbContext rival = newDb();
                rival.ProjectFiles.Add(Winner);
                await rival.SaveChangesAsync(ct);
            }

            return null;
        }

        public Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);
    }
}
