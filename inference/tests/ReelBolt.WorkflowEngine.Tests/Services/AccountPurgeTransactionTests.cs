using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// C11 F-2: "the purge can report success with the content still there".
///
/// <para>
/// The finding: <c>DeleteRowsAsync</c> used to commit twice — the memberships first, then the
/// workspace update and the user row. The memberships are also the <b>only</b> handle the sweep has
/// on the workspace (they are what resolves <c>purgeOrgs</c> → projects → object prefixes), so a
/// failure at or after the second commit left a retry that resolved no workspace, no project and no
/// object, deleted the user row anyway and returned <c>Purged = true</c> over a project row, a file
/// row and its blobs. The reviewer demonstrated exactly that by probe. This is the characterisation
/// test that was missing, and it drives the real <see cref="AccountPurgeService"/> against a provider
/// that <b>honours transactions</b> — Microsoft.Data.Sqlite, and a real PostgreSQL when
/// <c>REELBOLT_TEST_PG</c> is set — rather than the in-memory provider, which ignores transactions
/// (<c>InMemoryEventId.TransactionIgnoredWarning</c>) and is why the existing resumability test
/// passed over the bug.
/// </para>
///
/// <para>
/// <b>The failure-injection mechanism, explicitly: a real foreign-key violation on the account row
/// itself.</b> <see cref="ArmTheFailureAsync"/> creates a one-row probe table whose <c>user_id</c>
/// column references <c>application_users (id) ON DELETE RESTRICT</c>, so the database refuses the
/// statement that deletes the account — the statement of the second commit, and the last thing the
/// purge does. This is not a mocked or fail-before-save hook: the failure happens <i>inside the
/// database, in the middle of the transaction</i>, after EF has already sent the earlier commands of
/// that SaveChanges, so only a genuine rollback can undo them. <see cref="DisarmTheFailureAsync"/>
/// drops the probe, which is what lets the next sweep succeed.
/// </para>
///
/// <para>
/// <b>What this test still cannot prove.</b> (1) Nothing here says anything about concurrency: two
/// replicas purging one account at the same instant is out of scope, as it is for the sweep itself.
/// (2) SQLite is not PostgreSQL — its constraint and locking behaviour differs at the edges, so the
/// <c>REELBOLT_TEST_PG</c> run is the load-bearing evidence for the production engine; CI without
/// that variable only has the SQLite half. (3) The atomicity itself is EF Core's
/// (<c>SaveChangesAsync</c> runs its commands in one transaction) and the database's, not this
/// service's: what the service is pinned to here is that it relies on <i>exactly one</i>
/// <c>SaveChangesAsync</c> for the row deletion and on the memberships still being there when it
/// fails — splitting the commit, or deleting the memberships ahead of it, fails the test. (4) The
/// in-memory provider proves none of it: with no transactions a rollback is not a rollback.
/// </para>
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class AccountPurgeTransactionTests
{
    private sealed class Storage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = [];

        /// <summary>Make the store answer every multi-object delete with a per-key error, as S3 does.</summary>
        public bool RefuseDeletes { get; set; }
    }

    /// <summary>Captures the sweep's own log lines, so the test can say WHY the sweep deferred.</summary>
    private sealed class RecordingLogger : ILogger<AccountPurgeService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : " :: " + exception.GetType().Name + ": " + exception.Message));
    }

    private static IAmazonS3 BuildS3(Storage storage)
    {
        var mock = new Mock<IAmazonS3>();
        mock.Setup(s => s.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ListObjectsV2Request request, CancellationToken _) => new ListObjectsV2Response
            {
                S3Objects = storage.Objects.Keys
                    .Where(k => k.StartsWith(request.Prefix, StringComparison.Ordinal))
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .Select(k => new S3Object { Key = k, LastModified = DateTime.UtcNow })
                    .ToList(),
                IsTruncated = false
            });

        mock.Setup(s => s.DeleteObjectsAsync(It.IsAny<DeleteObjectsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeleteObjectsRequest, CancellationToken>((request, _) =>
            {
                if (storage.RefuseDeletes)
                    return;

                foreach (KeyVersion key in request.Objects)
                {
                    storage.Deleted.Add(key.Key);
                    storage.Objects.Remove(key.Key);
                }
            })
            .ReturnsAsync((DeleteObjectsRequest request, CancellationToken _) => storage.RefuseDeletes
                ? new DeleteObjectsResponse
                {
                    DeleteErrors = request.Objects
                        .Select(k => new DeleteError { Key = k.Key, Code = "InternalError", Message = "the store refused" })
                        .ToList()
                }
                : new DeleteObjectsResponse());
        return mock.Object;
    }

    private sealed record Harness(
        AccountPurgeService Service,
        IServiceProvider Provider,
        Storage Storage,
        RecordingLogger Log,
        bool UuidIds);

    private static async Task<Harness> BuildAsync(
        Action<DbContextOptionsBuilder> configure,
        Func<IServiceProvider, Task> createSchema,
        Storage storage,
        bool uuidIds)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(configure);
        services.AddSingleton(BuildS3(storage));
        services.AddSingleton<ITenantContext>(new TenantContext(new HttpContextAccessor()));
        services.AddScoped<IAccountBillingCanceller, AccountBillingCanceller>();
        var vectors = new Mock<IVectorIndexService>();
        vectors.Setup(v => v.DeleteProjectCollectionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        services.AddSingleton(vectors.Object);
        services.AddSingleton(Mock.Of<IInferenceProviderResolver>());
        services.AddSingleton(Mock.Of<ISpeechSynthesisClientFactory>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        IServiceProvider provider = services.BuildServiceProvider();
        await createSchema(provider);

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MinIO:BucketName"] = "test-bucket",
            ["Tenancy:Mode"] = "Cloud"
        }).Build();
        var options = new AccountOptions
        {
            Purge = new AccountPurgeOptions { Enabled = true, GraceDays = 30, PurgeUnverified = true }
        };
        var log = new RecordingLogger();
        var service = new AccountPurgeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            configuration,
            log);
        return new Harness(service, provider, storage, log, uuidIds);
    }

    private static async Task<Harness> BuildSqliteAsync(Storage storage)
    {
        // Foreign Keys=True is what makes the injected violation a real one: Microsoft.Data.Sqlite
        // leaves enforcement to EF unless the connection string asks for it.
        SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();

        // The connection is deliberately never disposed: Sqlite's ":memory:" database lives exactly
        // as long as its connection, and every scope the service creates must see the same one. It is
        // rooted by the DbContextOptions closure the service provider holds.
        return await BuildAsync(
            o => o.UseSqlite(connection),
            async provider =>
            {
                using IServiceScope scope = provider.CreateScope();
                InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                await db.Database.EnsureCreatedAsync();
                await CreateEngineOwnedTablesAsync(db);
            },
            storage,
            uuidIds: false);
    }

    /// <summary>
    /// The eight tables this context maps <c>ExcludeFromMigrations()</c>. <c>EnsureCreated</c> honours
    /// that annotation — exactly as the real migrations do — so Sqlite has no table for them and
    /// <c>DeleteRowsAsync</c> fails on its first query. The WorkflowEngine's own context owns them, so
    /// only those eight statements are taken from its create script; the rest of that model would
    /// collide with what <c>EnsureCreated</c> just built.
    /// </summary>
    private static readonly string[] EngineOwnedTables =
    [
        "workflow_definitions",
        "workflow_steps",
        "workflow_executions",
        "workflow_step_results",
        "review_scores",
        "workflow_step_cache_entries",
        "decision_observations",
        "external_generation_jobs"
    ];

    private static async Task CreateEngineOwnedTablesAsync(InferenceApiDbContext db)
    {
        await using var engine = new WorkflowEngineDbContext(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseSqlite(db.Database.GetDbConnection())
                .Options);

        foreach (string statement in engine.Database.GenerateCreateScript()
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (EngineOwnedTables.Any(t => statement.StartsWith("CREATE TABLE " + (char)34 + t + (char)34, StringComparison.OrdinalIgnoreCase)))
                await db.Database.ExecuteSqlRawAsync(statement);
        }
    }

    private sealed record Seeded(Guid UserId, Guid OrgId, Guid ProjectId);

    /// <summary>
    /// One account whose deletion is due: a Personal workspace, membership of it, one project with a
    /// file row and two objects under the project's prefix. Deliberately minimal — everything the
    /// finding needs and nothing else, so a failure here says "the purge stranded content" and not
    /// "the purge hit an unrelated table".
    /// </summary>
    private static async Task<Seeded> SeedAsync(IServiceProvider provider, Storage storage, DateTime requestedAt)
    {
        Guid userId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();

        storage.Objects["projects/" + projectId + "/userFiles/small.txt"] = "hello"u8.ToArray();
        storage.Objects["projects/" + projectId + "/outputFiles/render.mp4"] = new byte[32];

        using IServiceScope scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        DateTime now = DateTime.UtcNow;

        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = userId,
            Email = "ada@example.com",
            DisplayName = "Ada",
            PasswordHash = "hash",
            CreatedAt = now.AddDays(-100),
            EmailVerifiedAt = now.AddDays(-100),
            DeletionRequestedAt = requestedAt
        });
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = otherUserId,
            Email = "bob@example.com",
            DisplayName = "Bob",
            PasswordHash = "hash",
            CreatedAt = now.AddDays(-100),
            EmailVerifiedAt = now.AddDays(-100)
        });
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "Ada workspace",
            Slug = "ada-" + orgId.ToString("N")[..8],
            Kind = OrganizationKind.Personal,
            CreatedByUserId = userId,
            CreatedAt = now.AddDays(-100),
            UpdatedAt = now.AddDays(-100)
        });
        db.OrganizationMemberships.Add(new OrganizationMembership
        {
            OrganizationId = orgId,
            UserId = userId,
            Role = OrganizationRole.Owner,
            CreatedAt = now.AddDays(-100)
        });
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Launch video",
            OwnerId = userId,
            OrganizationId = orgId,
            CreatedAt = now.AddDays(-90),
            UpdatedAt = now.AddDays(-90)
        });
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            StorageKey = "projects/" + projectId + "/userFiles/small.txt",
            Category = "userFiles",
            OriginalFileName = "small.txt",
            MimeType = "text/plain",
            SizeBytes = 5,
            StorageBucket = "test-bucket",
            UploadedAt = now.AddDays(-90)
        });
        db.StepChangeRequests.Add(new StepChangeRequest
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = orgId,
            SourceExecutionId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepType = "Voiceover",
            RequestedByUserId = userId
        });
        await db.SaveChangesAsync();
        return new Seeded(userId, orgId, projectId);
    }

    private static async Task<T> WithDbAsync<T>(IServiceProvider provider, Func<InferenceApiDbContext, Task<T>> read)
    {
        using IServiceScope scope = provider.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>());
    }

    // The probe table's user_id has to be the same type as the id it references — Sqlite stores a
    // Guid as TEXT, PostgreSQL as uuid — and each has to be a literal rather than a concatenation,
    // because EF's analyzer (rightly) refuses concatenated SQL.
    private const string SqliteProbeTableSql =
        "CREATE TABLE purge_failure_probe (id text PRIMARY KEY, user_id text NOT NULL REFERENCES application_users (id) ON DELETE RESTRICT)";

    private const string PostgresProbeTableSql =
        "CREATE TABLE purge_failure_probe (id text PRIMARY KEY, user_id uuid NOT NULL REFERENCES application_users (id) ON DELETE RESTRICT)";

    /// <summary>
    /// Injects the failure: the database now refuses to delete this account's row, in the middle of
    /// whatever transaction tries — the exact statement the second commit used to carry.
    /// </summary>
    private static async Task ArmTheFailureAsync(Harness harness, Guid userId)
    {
        using IServiceScope scope = harness.Provider.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        await db.Database.ExecuteSqlRawAsync(harness.UuidIds ? PostgresProbeTableSql : SqliteProbeTableSql);
        // Parameterised, not interpolated: the id must be bound exactly the way EF binds a Guid on
        // this provider, or the foreign key would not match the parent row it is meant to block.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO purge_failure_probe (id, user_id) VALUES ('probe', {0})", userId);
    }

    private static async Task DisarmTheFailureAsync(Harness harness)
    {
        using IServiceScope scope = harness.Provider.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE purge_failure_probe");
    }

    /// <summary>
    /// The scenario, provider-agnostic: the commit that deletes the account row fails inside the
    /// database, and the sweep after it must still be able to finish the job.
    /// </summary>
    private static async Task AssertAFailedCommitCannotStrandTheContentAsync(Harness harness)
    {
        Seeded seeded = await SeedAsync(harness.Provider, harness.Storage, DateTime.UtcNow.AddDays(-31));
        await ArmTheFailureAsync(harness, seeded.UserId);

        AccountPurgeRunResult first = await harness.Service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        // The state the failed sweep left behind, read before the retry disturbs it. The objects may
        // already be gone — the object store is not part of the database transaction and the purge
        // deliberately sweeps it first — and that is safe only *because* the rows survive the
        // rollback: they are the map back to the prefix, and re-listing an already-empty prefix is a
        // no-op on the retry.
        int membershipsAfterFailure = await WithDbAsync(harness.Provider, db => db.OrganizationMemberships.CountAsync(m => m.UserId == seeded.UserId));
        int usersAfterFailure = await WithDbAsync(harness.Provider, db => db.ApplicationUsers.CountAsync());
        int projectsAfterFailure = await WithDbAsync(harness.Provider, db => db.Projects.IgnoreQueryFilters().CountAsync());
        int filesAfterFailure = await WithDbAsync(harness.Provider, db => db.ProjectFiles.IgnoreQueryFilters().CountAsync());

        // The retry, with the injected failure removed: the next sweep, on the state the failure left.
        await DisarmTheFailureAsync(harness);
        AccountPurgeRunResult second = await harness.Service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);
        int projectsAfterRetry = await WithDbAsync(harness.Provider, db => db.Projects.IgnoreQueryFilters().CountAsync());
        int filesAfterRetry = await WithDbAsync(harness.Provider, db => db.ProjectFiles.IgnoreQueryFilters().CountAsync());
        int changeRequestsAfterRetry = await WithDbAsync(harness.Provider, db => db.StepChangeRequests.CountAsync());

        // F-2's symptom, first: the retry must finish the purge, not report success over what is left.
        // On the two-commit version this is where the test fails — the retry resolves no organization,
        // no project and no object, deletes the user row and returns Purged = true over the project,
        // the file and both blobs (the reviewer's probe).
        second.UsersPurged.Should().Be(1);
        second.UsersDeferred.Should().Be(0);
        second.ProjectsPurged.Should().Be(1, "the retry must still resolve the workspace and its project");
        second.CollectionsDeleted.Should().Be(2, "and sweep the project's vector collections with it");
        changeRequestsAfterRetry.Should().Be(0, "a step change request goes with its project");
        projectsAfterRetry.Should().Be(0, "reported purged means the content is gone, not that the rows went first");
        filesAfterRetry.Should().Be(0);
        (await WithDbAsync(harness.Provider, db => db.ApplicationUsers.CountAsync())).Should().Be(1, "only Bob is left");
        (await WithDbAsync(harness.Provider, db => db.OrganizationMemberships.CountAsync())).Should().Be(0);
        harness.Storage.Deleted.Distinct().Should().HaveCount(2, "both objects go, across the two sweeps");
        harness.Storage.Objects.Should().BeEmpty("nothing is left in the store");

        // Why it can: the failed sweep deferred rather than half-deleted, and the worklist the retry
        // needs — the memberships, which are the only handle on the workspace — survived the rollback.
        first.UsersPurged.Should().Be(0);
        first.UsersDeferred.Should().Be(1, "a failed purge defers rather than half-deletes");
        harness.Log.Messages.Should().Contain(m => m.Contains("rows not deleted"),
            "the injected failure must have hit the row deletion, not an earlier phase");
        membershipsAfterFailure.Should().Be(1, "the memberships are the only handle on the workspace; a rollback must keep them");
        usersAfterFailure.Should().Be(2, "nothing in the database is touched while the purge cannot finish");
        projectsAfterFailure.Should().Be(1);
        filesAfterFailure.Should().Be(1);
    }

    [Fact]
    public async Task AFailedCommitCannotStrandTheContent_OnATransactionalProvider()
    {
        var harness = await BuildSqliteAsync(new Storage());
        await AssertAFailedCommitCannotStrandTheContentAsync(harness);
    }

    /// <summary>
    /// F-2 has a second door, of the same shape: the store can answer a multi-object delete with
    /// per-key errors instead of throwing, and counting that as success deleted the rows that are the
    /// only map back to the surviving objects. The account must defer instead — while the rows are
    /// there the next sweep lists the prefix again and retries the keys the store refused.
    /// </summary>
    [Fact]
    public async Task AnObjectTheStoreRefusesToDelete_DefersTheAccountInsteadOfReportingItPurged()
    {
        var storage = new Storage { RefuseDeletes = true };
        Harness harness = await BuildSqliteAsync(storage);
        await SeedAsync(harness.Provider, storage, DateTime.UtcNow.AddDays(-31));

        AccountPurgeRunResult run = await harness.Service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        run.UsersPurged.Should().Be(0, "the objects are still there, so nothing may be reported purged");
        run.UsersDeferred.Should().Be(1);
        storage.Objects.Should().HaveCount(2, "the store refused, so both objects are still there");
        (await WithDbAsync(harness.Provider, db => db.ApplicationUsers.CountAsync())).Should().Be(2,
            "the rows are the only map back to the objects the store kept");
        (await WithDbAsync(harness.Provider, db => db.Projects.IgnoreQueryFilters().CountAsync())).Should().Be(1);
    }

    /// <summary>
    /// The same scenario on the production engine, on a throwaway database this test creates and
    /// drops (<c>REELBOLT_TEST_PG</c>, as in <c>OrganizationBackfillTests</c>). Without the variable
    /// the test returns immediately — xUnit v2 has no dynamic skip — so CI without a database stays
    /// green, and a local run is the record that PostgreSQL agrees with SQLite.
    /// </summary>
    [Fact]
    public async Task AFailedCommitCannotStrandTheContent_OnARealPostgres()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_f2_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
            var harness = await BuildAsync(
                o => o.UseNpgsql(csb.ConnectionString),
                async provider =>
                {
                    using IServiceScope scope = provider.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>().Database.MigrateAsync();

                    // The eight tables this context maps ExcludeFromMigrations() belong to the
                    // WorkflowEngine, which migrates them in production (it starts after the API and
                    // migrates against the same database). Without them DeleteRowsAsync has nothing to
                    // delete from here.
                    await using var engine = new WorkflowEngineDbContext(
                        new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                            .UseNpgsql(csb.ConnectionString)
                            .Options);
                    await engine.Database.MigrateAsync();
                },
                new Storage(),
                uuidIds: true);

            await AssertAFailedCommitCannotStrandTheContentAsync(harness);
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// A purged project with a run executed from its own ExecutionSnapshot: the database deletes the
    /// snapshot WITH the run (snapshot_of_execution_id ON DELETE CASCADE), so the purge must not also
    /// delete it through EF, or that DELETE affects 0 rows and the whole purge rolls back.
    /// </summary>
    [Fact]
    public async Task A_project_whose_run_has_a_snapshot_is_purged_on_a_real_postgres()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_snap_purge_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
            DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseNpgsql(csb.ConnectionString).Options;
            Harness harness = await BuildAsync(
                o => o.UseNpgsql(csb.ConnectionString),
                async provider =>
                {
                    using IServiceScope scope = provider.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>().Database.MigrateAsync();
                    await using var engine = new WorkflowEngineDbContext(engineOptions);
                    await engine.Database.MigrateAsync();
                },
                new Storage(),
                uuidIds: true);
            Seeded seeded = await SeedAsync(harness.Provider, harness.Storage, DateTime.UtcNow.AddDays(-31));

            Guid agentId = Guid.NewGuid();
            using (IServiceScope scope = harness.Provider.CreateScope())
            {
                InferenceApiDbContext api = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                api.AgentDefinitions.Add(new AgentDefinition { Id = agentId, Name = "Writer", AgentType = AgentType.Custom, SystemPrompt = "", OrganizationId = seeded.OrgId });
                await api.SaveChangesAsync();
            }

            await using (var engine = new WorkflowEngineDbContext(engineOptions))
            {
                WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Launch reel", ProjectId = seeded.ProjectId };
                workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agentId, StepOrder = 1, StepType = StepType.Agent });
                WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = seeded.ProjectId, OrganizationId = seeded.OrgId, Status = ExecutionStatus.Passed };
                engine.WorkflowDefinitions.Add(workflow);
                engine.WorkflowExecutions.Add(run);
                await engine.SaveChangesAsync();

                // The shape ExecutionSnapshotter leaves: a kind-5 clone owned by the run, its result on the clone.
                WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), Name = "Launch reel (run snapshot)", ProjectId = seeded.ProjectId, Kind = WorkflowDefinitionKind.ExecutionSnapshot };
                WorkflowStep clone = ReelBolt.Shared.Workflows.Replay.WorkflowStepCloner.Clone(workflow.Steps.Single(), snapshot.Id);
                engine.WorkflowDefinitions.Add(snapshot);
                engine.Entry(snapshot).Property(WorkflowEngineDbContext.SnapshotOfExecutionIdColumn).CurrentValue = run.Id;
                engine.WorkflowSteps.Add(clone);
                run.SnapshotDefinitionId = snapshot.Id;
                engine.WorkflowStepResults.Add(new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = run.Id, WorkflowStepId = clone.Id, Status = StepStatus.Completed, Output = "{}", ExecutedAt = DateTime.UtcNow });
                await engine.SaveChangesAsync();
            }

            AccountPurgeRunResult result = await harness.Service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

            result.UsersPurged.Should().Be(1, string.Join(" | ", harness.Log.Messages));
            result.UsersDeferred.Should().Be(0);
            await using (var check = new WorkflowEngineDbContext(engineOptions))
            {
                (await check.WorkflowDefinitions.CountAsync()).Should().Be(0);
                (await check.WorkflowSteps.CountAsync()).Should().Be(0);
                (await check.WorkflowExecutions.CountAsync()).Should().Be(0);
                (await check.WorkflowStepResults.CountAsync()).Should().Be(0);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Simulates the engine writing an external-generation ledger row while the purge is between its
    /// reads and its commit (SQFU LOW 4): the row's <c>Status</c> is a concurrency token, so an EF delete
    /// of the copy the purge read would affect 0 rows and roll the whole purge back.
    /// </summary>
    private sealed class EngineWritesJobMidPurge(string connectionString, Guid jobId) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        /// <summary>Set once seeding is done, so only the purge's own save is intercepted.</summary>
        public bool Armed { get; set; }

        /// <summary>What the simulated engine write did: rows it updated, or "lock-timeout".</summary>
        public string Outcome { get; private set; } = "not run";

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && !Fired && eventData.Context is InferenceApiDbContext)
            {
                Fired = true;
                await using NpgsqlConnection engine = new(connectionString);
                await engine.OpenAsync(cancellationToken);
                // A purge that already deleted the row in its own transaction holds its lock; the
                // engine's write then waits, and the short timeout stands for it losing the race.
                await using (NpgsqlCommand timeout = new("SET lock_timeout = '2s'", engine))
                    await timeout.ExecuteNonQueryAsync(cancellationToken);
                try
                {
                    await using NpgsqlCommand update = new("UPDATE external_generation_jobs SET status = 'Submitted' WHERE id = @id", engine);
                    update.Parameters.AddWithValue("id", jobId);
                    Outcome = "updated " + await update.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
                {
                    Outcome = "lock-timeout";
                }
            }

            return result;
        }
    }

    /// <summary>
    /// SQFU-S2 and LOW 4 on the production engine. The account owns a custom agent that its workflow
    /// AND that workflow's run snapshot use (workflow_steps → agent_definitions is ON DELETE RESTRICT),
    /// and the engine writes the run's external-generation ledger row while the purge is in flight. The
    /// purge must still delete everything, the agent included, in one go.
    /// </summary>
    [Fact]
    public async Task A_purge_deletes_a_custom_agent_its_snapshotted_run_uses_and_survives_a_ledger_write_mid_purge()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_snap_agent_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
            DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseNpgsql(csb.ConnectionString).Options;
            Guid jobId = Guid.NewGuid();
            EngineWritesJobMidPurge engineWrite = new(csb.ConnectionString, jobId);
            Harness harness = await BuildAsync(
                o => o.UseNpgsql(csb.ConnectionString).AddInterceptors(engineWrite),
                async provider =>
                {
                    using IServiceScope scope = provider.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>().Database.MigrateAsync();
                    await using var engine = new WorkflowEngineDbContext(engineOptions);
                    await engine.Database.MigrateAsync();
                },
                new Storage(),
                uuidIds: true);
            Seeded seeded = await SeedAsync(harness.Provider, harness.Storage, DateTime.UtcNow.AddDays(-31));

            // The user's OWN custom agent (OwnerId): the purge deletes it.
            Guid agentId = Guid.NewGuid();
            using (IServiceScope scope = harness.Provider.CreateScope())
            {
                InferenceApiDbContext api = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                api.AgentDefinitions.Add(new AgentDefinition { Id = agentId, Name = "Writer", AgentType = AgentType.Custom, SystemPrompt = "", OwnerId = seeded.UserId, OrganizationId = seeded.OrgId });
                await api.SaveChangesAsync();
            }

            await using (var engine = new WorkflowEngineDbContext(engineOptions))
            {
                WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), Name = "Launch reel", ProjectId = seeded.ProjectId };
                workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agentId, StepOrder = 1, StepType = StepType.Agent });
                WorkflowExecution run = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, ProjectId = seeded.ProjectId, OrganizationId = seeded.OrgId, Status = ExecutionStatus.Passed };
                engine.WorkflowDefinitions.Add(workflow);
                engine.WorkflowExecutions.Add(run);
                await engine.SaveChangesAsync();

                WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), Name = "Launch reel (run snapshot)", ProjectId = seeded.ProjectId, Kind = WorkflowDefinitionKind.ExecutionSnapshot };
                WorkflowStep clone = ReelBolt.Shared.Workflows.Replay.WorkflowStepCloner.Clone(workflow.Steps.Single(), snapshot.Id);
                engine.WorkflowDefinitions.Add(snapshot);
                engine.Entry(snapshot).Property(WorkflowEngineDbContext.SnapshotOfExecutionIdColumn).CurrentValue = run.Id;
                engine.WorkflowSteps.Add(clone);
                run.SnapshotDefinitionId = snapshot.Id;
                Guid resultId = Guid.NewGuid();
                engine.WorkflowStepResults.Add(new WorkflowStepResult { Id = resultId, WorkflowExecutionId = run.Id, WorkflowStepId = clone.Id, Status = StepStatus.Completed, Output = "{}", ExecutedAt = DateTime.UtcNow });
                // A legacy archive (T-002): a kind-5 definition NOT owned by one run, also on the agent.
                WorkflowDefinition archive = new() { Id = Guid.NewGuid(), Name = "Launch reel (archived steps)", ProjectId = seeded.ProjectId, Kind = WorkflowDefinitionKind.ExecutionSnapshot };
                archive.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = archive.Id, AgentDefinitionId = agentId, StepOrder = 1, StepType = StepType.Agent });
                engine.WorkflowDefinitions.Add(archive);
                engine.ExternalGenerationJobs.Add(new ExternalGenerationJob
                {
                    Id = jobId,
                    ExecutionId = run.Id,
                    StepResultId = resultId,
                    ProjectId = seeded.ProjectId,
                    OrganizationId = seeded.OrgId,
                    ClipId = "c1",
                    RequestHash = "h",
                    ProviderId = Guid.NewGuid(),
                    Status = ExternalGenerationJobStatus.Reserved
                });
                await engine.SaveChangesAsync();
            }

            engineWrite.Armed = true;
            AccountPurgeRunResult result = await harness.Service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

            engineWrite.Fired.Should().BeTrue("the simulated engine write ran inside the purge");
            result.UsersPurged.Should().Be(1, string.Join(" | ", harness.Log.Messages));
            result.UsersDeferred.Should().Be(0);
            engineWrite.Outcome.Should().Be("lock-timeout", "the purge already holds the row it deleted, so the engine's write waits instead of slipping in");
            await using (var check = new WorkflowEngineDbContext(engineOptions))
            {
                (await check.AgentDefinitions.AnyAsync(a => a.Id == agentId)).Should().BeFalse("the user's custom agent is purged");
                (await check.WorkflowDefinitions.CountAsync()).Should().Be(0);
                (await check.WorkflowSteps.CountAsync()).Should().Be(0);
                (await check.WorkflowExecutions.CountAsync()).Should().Be(0);
                (await check.WorkflowStepResults.CountAsync()).Should().Be(0);
                (await check.ExternalGenerationJobs.CountAsync()).Should().Be(0);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
