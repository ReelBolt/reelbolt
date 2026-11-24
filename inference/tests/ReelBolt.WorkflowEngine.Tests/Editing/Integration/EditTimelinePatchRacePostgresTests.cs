using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Editing.Integration;

/// <summary>
/// T0.2 (PATCH half): the editor's save path against a REAL PostgreSQL, through the real
/// <see cref="EditTimelinesController"/>, the real <see cref="ProjectAccessService"/> and a real
/// <see cref="InferenceApiDbContext"/> per request — nothing on the write path is mocked.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory provider cannot express the defect this pins (audit d1 N2): it has no transactions,
/// no row locks and no unique index enforcement across contexts, so two writers that both pass the
/// stale-revision check never collide there. On PostgreSQL the losing writer's
/// <c>edit_timeline_ops</c> insert blocks on the <c>(timeline_id, revision)</c> unique index until
/// the winner commits and then fails with SQLSTATE 23505, which escaped as an unhandled
/// <c>DbUpdateException</c> (HTTP 500 with an empty body) instead of the documented 409.
/// </para>
/// <para>
/// <b>How the race is made deterministic.</b> A <see cref="Rendezvous"/> interceptor holds each
/// request at the start of its <c>SaveChanges</c> until both requests of the pair have arrived
/// there, so both have already passed the stale-revision check and both write — the exact window
/// the browser hit with a debounce flush racing a <c>visibilitychange</c> flush.
/// </para>
/// <para>
/// Skipped (returns immediately; xUnit v2 has no dynamic skip) when <c>REELBOLT_TEST_PG</c> is
/// unset, like every other real-Postgres test in this project. Each test creates and drops its own
/// throwaway database.
/// </para>
/// </remarks>
public class EditTimelinePatchRacePostgresTests
{
    private const int Pairs = 20;

    private static readonly JsonSerializerOptions DocumentJsonOptions = EditTimelineJson.Options;

    [Fact]
    public async Task TwentyParallelPatchPairsOnTheSameBase_AnswerExactlyOne200AndOne409Each_AndNever500()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            (Guid ownerId, Guid projectId, Guid timelineId) = await SeedTimelineAsync(connectionString);

            int successes = 0, conflicts = 0, failures = 0;
            List<string> failureDetails = [];

            for (int pair = 0; pair < Pairs; pair++)
            {
                int baseRevision = pair + 1;
                Rendezvous gate = new(participants: 2);

                Task<object> first = PatchAsync(connectionString, gate, ownerId, projectId, timelineId, baseRevision, $"writer A, pair {pair}");
                Task<object> second = PatchAsync(connectionString, gate, ownerId, projectId, timelineId, baseRevision, $"writer B, pair {pair}");
                object[] outcomes = await Task.WhenAll(first, second);

                int pairOk = 0, pairConflict = 0;
                foreach (object outcome in outcomes)
                {
                    switch (outcome)
                    {
                        case OkObjectResult ok when ok.Value is EditTimelineResponse body:
                            pairOk++;
                            body.Revision.Should().Be(baseRevision + 1);
                            break;
                        case ConflictObjectResult conflict when conflict.Value is EditTimelineConflictResponse c:
                            pairConflict++;
                            c.CurrentRevision.Should().Be(baseRevision + 1,
                                "the loser must be told the revision the winner produced, so it can rebase");
                            break;
                        default:
                            failures++;
                            failureDetails.Add($"pair {pair}: {Describe(outcome)}");
                            break;
                    }
                }

                successes += pairOk;
                conflicts += pairConflict;
                if (pairOk != 1 || pairConflict != 1)
                {
                    failureDetails.Add($"pair {pair}: {pairOk}×200, {pairConflict}×409");
                }
            }

            failureDetails.Should().BeEmpty("every pair must be exactly one 200 and one 409, never a 500");
            failures.Should().Be(0);
            successes.Should().Be(Pairs);
            conflicts.Should().Be(Pairs);

            // The durable state agrees: one op row per revision, the snapshot at revision 20, and
            // the row at the last winner's revision.
            await using InferenceApiDbContext db = CreateContext(connectionString, interceptor: null);
            EditTimelineEntity row = await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId);
            row.Revision.Should().Be(Pairs + 1);
            (await db.EditTimelineOps.CountAsync(o => o.TimelineId == timelineId)).Should().Be(Pairs);
            (await db.EditTimelineSnapshots.CountAsync(s => s.TimelineId == timelineId && s.Revision == 20)).Should().Be(1);
        });
    }

    [Fact]
    public async Task ARenameRacingAPatch_BothSucceed_AndTheRenameNeitherBumpsTheRevisionNorFails()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            (Guid ownerId, Guid projectId, Guid timelineId) = await SeedTimelineAsync(connectionString);

            for (int round = 0; round < 5; round++)
            {
                int baseRevision = round + 1;
                string newName = $"Renamed {round}";
                Rendezvous gate = new(participants: 2);

                // Both read the row at the same revision; the PATCH then writes first, so the
                // rename's UPDATE (guarded by the Revision concurrency token) matches no row — the
                // case the rename must absorb rather than fail.
                PatchWritesFirst order = new();
                Task<object> patch = PatchAsync(connectionString, gate, ownerId, projectId, timelineId, baseRevision, $"patch {round}", order.Writer);
                Task<object> rename = RenameAsync(connectionString, gate, ownerId, projectId, timelineId, newName, order.Follower);
                object[] outcomes = await Task.WhenAll(patch, rename);

                outcomes[0].Should().BeOfType<OkObjectResult>("the PATCH; got {0}", Describe(outcomes[0]));
                outcomes[1].Should().BeOfType<OkObjectResult>("the rename; got {0}", Describe(outcomes[1]));

                await using InferenceApiDbContext db = CreateContext(connectionString, interceptor: null);
                EditTimelineEntity row = await db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timelineId);
                row.Revision.Should().Be(baseRevision + 1, "only the PATCH advances the revision");
                row.Name.Should().Be(newName);
            }
        });
    }

    [Fact]
    public async Task ADeleteRacingAPatch_TheDeleteWins204WhicheverWritesFirst_AndNothingIs500()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            for (int round = 0; round < 5; round++)
            {
                // PATCH writes first: the delete's UPDATE (guarded by the Revision concurrency
                // token) matches no row; it must re-read and delete the fresh row.
                (Guid ownerId, Guid projectId, Guid timelineId) = await SeedTimelineAsync(connectionString);
                Rendezvous gate = new(participants: 2);
                PatchWritesFirst order = new();
                object[] outcomes = await Task.WhenAll(
                    PatchAsync(connectionString, gate, ownerId, projectId, timelineId, 1, $"patch {round}", order.Writer),
                    DeleteAsync(connectionString, gate, ownerId, projectId, timelineId, order.Follower));

                outcomes[0].Should().BeOfType<OkObjectResult>("the PATCH that wrote first; got {0}", Describe(outcomes[0]));
                outcomes[1].Should().BeOfType<NoContentResult>("the delete; got {0}", Describe(outcomes[1]));
                await using (InferenceApiDbContext db = CreateContext(connectionString, interceptor: null))
                {
                    EditTimelineEntity row = await db.EditTimelines.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == timelineId);
                    row.DeletedAt.Should().NotBeNull();
                    row.Revision.Should().Be(3, "the PATCH took revision 2 and the delete advanced it past that");
                }

                // Delete writes first: the PATCH's UPDATE matches no row and there is no current
                // revision to rebase onto — 404, not 409 and never 500.
                (ownerId, projectId, timelineId) = await SeedTimelineAsync(connectionString);
                gate = new(participants: 2);
                order = new();
                outcomes = await Task.WhenAll(
                    DeleteAsync(connectionString, gate, ownerId, projectId, timelineId, order.Writer),
                    PatchAsync(connectionString, gate, ownerId, projectId, timelineId, 1, $"late {round}", order.Follower));

                outcomes[0].Should().BeOfType<NoContentResult>("the delete that wrote first; got {0}", Describe(outcomes[0]));
                outcomes[1].Should().BeOfType<NotFoundResult>("the PATCH after the delete; got {0}", Describe(outcomes[1]));
            }
        });
    }

    private static async Task<object> DeleteAsync(
        string connectionString, Rendezvous gate, Guid ownerId, Guid projectId, Guid timelineId, params IInterceptor[] more)
    {
        await using InferenceApiDbContext db = CreateContext(connectionString, gate, more);
        try
        {
            return await CreateController(db, ownerId).DeleteEditTimeline(projectId, timelineId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            gate.Leave();
        }
    }

    private static async Task<object> RenameAsync(
        string connectionString, Rendezvous gate, Guid ownerId, Guid projectId, Guid timelineId, string name, params IInterceptor[] more)
    {
        await using InferenceApiDbContext db = CreateContext(connectionString, gate, more);
        try
        {
            ActionResult<EditTimelineResponse> result = await CreateController(db, ownerId)
                .RenameEditTimeline(projectId, timelineId, new RenameEditTimelineRequest(name), CancellationToken.None);
            return result.Result!;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            gate.Leave();
        }
    }

    [Fact]
    public async Task ATimelineSeededFromAVideoCompileResult_RoundTripsThroughGetAndPatch()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            Guid ownerId = Guid.NewGuid();
            Guid projectId;
            Guid stepResultId;
            string manifestKey;
            string manifestJson;
            Guid videoFileId;
            Guid musicFileId;

            await using (InferenceApiDbContext db = CreateContext(connectionString, interceptor: null))
            {
                Project project = NewProject(ownerId);
                AddOwner(db, ownerId);
                db.Projects.Add(project);
                projectId = project.Id;

                ProjectFile video = NewProjectFile(projectId, "shoot.mp4", "sources/shoot.mp4", "video/mp4");
                ProjectFile music = NewProjectFile(projectId, "bed.mp3", "audio/bed.mp3", "audio/mpeg");
                db.ProjectFiles.AddRange(video, music);
                videoFileId = video.Id;
                musicFileId = music.Id;

                AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "compile", IsBuiltIn = true };
                WorkflowDefinition definition = new()
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    Name = "compile",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                WorkflowStep step = new()
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = definition.Id,
                    AgentDefinitionId = agent.Id,
                    StepOrder = 1,
                    StepType = StepType.VideoCompile
                };
                WorkflowExecution execution = new()
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = definition.Id,
                    ProjectId = projectId
                };
                manifestKey = $"projects/{projectId}/outputFiles/manifest.json";
                WorkflowStepResult result = new()
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = execution.Id,
                    WorkflowStepId = step.Id,
                    TimelineStorageKey = manifestKey,
                    Status = StepStatus.Completed,
                    ExecutedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow
                };
                stepResultId = result.Id;

                db.AgentDefinitions.Add(agent);
                db.WorkflowDefinitions.Add(definition);
                db.WorkflowSteps.Add(step);
                db.WorkflowExecutions.Add(execution);
                db.WorkflowStepResults.Add(result);
                await db.SaveChangesAsync();

                manifestJson = JsonSerializer.Serialize(
                    BuildManifest(projectId, execution.Id, video.StorageKey, music.OriginalFileName),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            Mock<IFileStorageService> storage = new();
            storage.Setup(s => s.DownloadAsync(projectId, manifestKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(manifestJson)));

            Guid timelineId;
            await using (InferenceApiDbContext db = CreateContext(connectionString, interceptor: null))
            {
                ActionResult<EditTimelineResponse> created = await CreateController(db, ownerId, storage.Object)
                    .CreateEditTimeline(projectId, new CreateEditTimelineRequest("Seeded", stepResultId), CancellationToken.None);
                ObjectResult createdResult = created.Result.Should().BeOfType<ObjectResult>().Subject;
                createdResult.StatusCode.Should().Be(201);
                timelineId = createdResult.Value.Should().BeOfType<EditTimelineResponse>().Subject.Id;
            }

            EditTimeline fetched;
            await using (InferenceApiDbContext db = CreateContext(connectionString, interceptor: null))
            {
                ActionResult<EditTimelineResponse> got = await CreateController(db, ownerId)
                    .GetEditTimeline(projectId, timelineId, CancellationToken.None);
                EditTimelineResponse body = got.Result.Should().BeOfType<OkObjectResult>().Subject
                    .Value.Should().BeOfType<EditTimelineResponse>().Subject;
                body.Revision.Should().Be(1);
                fetched = body.Document;
                fetched.SchemaVersion.Should().Be(2);
                MediaClip cut = fetched.Tracks.Single(t => t.Id == EditTimelineSeeder.TrackId("V1"))
                    .Items.OfType<MediaClip>().Single();
                cut.AssetRef.ProjectFileId.Should().Be(videoFileId);
                fetched.Tracks.Single(t => t.Id == EditTimelineSeeder.TrackId("A3"))
                    .Items.OfType<MediaClip>().Single().AssetRef.ProjectFileId.Should().Be(musicFileId);
            }

            // The seeded document, sent back unchanged through the JSON wire format, must be a valid
            // save (the seeder and the validator agree on every rule, including the new ones).
            EditTimeline overWire = JsonSerializer.Deserialize<EditTimeline>(
                JsonSerializer.Serialize(fetched, DocumentJsonOptions), DocumentJsonOptions)!;
            await using (InferenceApiDbContext db = CreateContext(connectionString, interceptor: null))
            {
                ActionResult<EditTimelineResponse> patched = await CreateController(db, ownerId).PatchEditTimeline(
                    projectId, timelineId, new PatchEditTimelineRequest(1, null, overWire), CancellationToken.None);
                EditTimelineResponse body = patched.Result.Should().BeOfType<OkObjectResult>(
                    "a seeded document must save back unchanged; got {0}", Describe(patched.Result!)).Subject
                    .Value.Should().BeOfType<EditTimelineResponse>().Subject;
                body.Revision.Should().Be(2);
            }

            await using (InferenceApiDbContext db = CreateContext(connectionString, interceptor: null))
            {
                ActionResult<EditTimelineResponse> got = await CreateController(db, ownerId)
                    .GetEditTimeline(projectId, timelineId, CancellationToken.None);
                EditTimelineResponse body = got.Result.Should().BeOfType<OkObjectResult>().Subject
                    .Value.Should().BeOfType<EditTimelineResponse>().Subject;
                body.Revision.Should().Be(2);
                JsonSerializer.Serialize(body.Document, DocumentJsonOptions)
                    .Should().Be(JsonSerializer.Serialize(fetched, DocumentJsonOptions));
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<object> PatchAsync(
        string connectionString,
        Rendezvous gate,
        Guid ownerId,
        Guid projectId,
        Guid timelineId,
        int baseRevision,
        string name,
        params IInterceptor[] more)
    {
        // Each request is its own scope: its own DbContext, its own connection and transaction.
        await using InferenceApiDbContext db = CreateContext(connectionString, gate, more);
        EditTimelinesController controller = CreateController(db, ownerId);
        EditTimeline document = BlankDocument(projectId, timelineId, "Race");
        PatchEditTimelineRequest request = new(
            baseRevision,
            JsonDocument.Parse($$"""[{"op":"rename","name":"{{name}}"}]""").RootElement.Clone(),
            document);

        try
        {
            ActionResult<EditTimelineResponse> result = await controller.PatchEditTimeline(projectId, timelineId, request, CancellationToken.None);
            return result.Result!;
        }
        catch (Exception ex)
        {
            // An escaped exception is what ASP.NET Core turns into a 500 with an empty body.
            return ex;
        }
        finally
        {
            gate.Leave();
        }
    }

    private static string Describe(object outcome) => outcome switch
    {
        Exception ex => $"500 (unhandled {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message})",
        ObjectResult o => $"{o.StatusCode} {JsonSerializer.Serialize(o.Value)}",
        StatusCodeResult s => s.StatusCode.ToString(),
        _ => outcome.GetType().Name
    };

    /// <summary>
    /// Holds every participant at the start of <c>SaveChanges</c> until all have arrived (or one
    /// has left without saving — a request that returned before writing must not strand the other).
    /// </summary>
    private sealed class Rendezvous(int participants) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = participants;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Arrive();
            await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }

        public void Leave() => Arrive();

        private void Arrive()
        {
            if (Interlocked.Decrement(ref _remaining) <= 0) _allArrived.TrySetResult();
        }
    }

    /// <summary>
    /// Orders two writers that have both passed the rendezvous: the follower's first
    /// <c>SaveChanges</c> waits until the writer's has finished (its transaction then commits, and
    /// the follower's UPDATE re-evaluates against the committed row).
    /// </summary>
    private sealed class PatchWritesFirst
    {
        private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IInterceptor Writer => new Signal(_written);

        public IInterceptor Follower => new Wait(_written);

        private sealed class Signal(TaskCompletionSource written) : SaveChangesInterceptor
        {
            public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
            {
                written.TrySetResult();
                return ValueTask.FromResult(result);
            }

            public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
            {
                written.TrySetResult();
                return Task.CompletedTask;
            }
        }

        private sealed class Wait(TaskCompletionSource written) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                await written.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return result;
            }
        }
    }

    private static async Task WithDatabaseAsync(Func<string, Task> body)
    {
        string? baseConnection = Environment.GetEnvironmentVariable("REELBOLT_TEST_PG");
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_t02_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            string connectionString = new NpgsqlConnectionStringBuilder(baseConnection) { Database = dbName }.ConnectionString;

            // Production order: the API migrates its tables, then the engine migrates the ones the
            // API maps ExcludeFromMigrations() (workflow definitions, executions, step results).
            await using (InferenceApiDbContext api = CreateContext(connectionString, interceptor: null))
            {
                await api.Database.MigrateAsync();
            }

            await using (WorkflowEngineDbContext engine = new(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                    .UseNpgsql(connectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory_Workflow"))
                    .Options))
            {
                await engine.Database.MigrateAsync();
            }

            await body(connectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static InferenceApiDbContext CreateContext(string connectionString, IInterceptor? interceptor, params IInterceptor[] more)
    {
        DbContextOptionsBuilder<InferenceApiDbContext> builder = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql(connectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory_Api"));
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        if (more.Length > 0) builder.AddInterceptors(more);
        return new InferenceApiDbContext(builder.Options);
    }

    private static async Task<(Guid OwnerId, Guid ProjectId, Guid TimelineId)> SeedTimelineAsync(string connectionString)
    {
        Guid ownerId = Guid.NewGuid();
        await using InferenceApiDbContext db = CreateContext(connectionString, interceptor: null);
        Project project = NewProject(ownerId);
        AddOwner(db, ownerId);
        db.Projects.Add(project);
        Guid timelineId = Guid.NewGuid();
        db.EditTimelines.Add(new EditTimelineEntity
        {
            Id = timelineId,
            ProjectId = project.Id,
            Name = "Race",
            DocumentJson = JsonSerializer.Serialize(BlankDocument(project.Id, timelineId, "Race"), DocumentJsonOptions),
            Revision = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return (ownerId, project.Id, timelineId);
    }

    /// <summary>The project owner and their personal organization: real rows, because Postgres enforces the foreign keys.</summary>
    private static void AddOwner(InferenceApiDbContext db, Guid ownerId)
    {
        DateTime now = DateTime.UtcNow;
        Guid orgId = TestTenancy.OrgOf(ownerId);
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = ownerId,
            Email = $"t02-{ownerId:N}@example.com",
            DisplayName = "T0.2",
            PasswordHash = "hash",
            CreatedAt = now,
            EmailVerifiedAt = now
        });
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "T0.2 workspace",
            Slug = "t02-" + orgId.ToString("N")[..8],
            Kind = OrganizationKind.Personal,
            CreatedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private static Project NewProject(Guid ownerId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "T0.2",
        OwnerId = ownerId,
        OrganizationId = TestTenancy.OrgOf(ownerId),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static ProjectFile NewProjectFile(Guid projectId, string name, string storageKey, string mime) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        OriginalFileName = name,
        StorageKey = storageKey,
        MimeType = mime,
        SizeBytes = 1024,
        UploadedAt = DateTime.UtcNow
    };

    private static ICurrentUser CurrentUser(Guid userId)
    {
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(userId);
        user.SetupGet(u => u.Email).Returns("t02@example.com");
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.IsPlatformAdmin).Returns(false);
        user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(userId));
        user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
        return user.Object;
    }

    private static EditTimelinesController CreateController(InferenceApiDbContext db, Guid userId, IFileStorageService? storage = null) =>
        new(db, CurrentUser(userId), new ProjectAccessService(db, CurrentUser(userId)),
            storage ?? new Mock<IFileStorageService>().Object, new Mock<IWorkflowExecutionService>().Object);

    private static EditTimeline BlankDocument(Guid projectId, Guid timelineId, string name) =>
        new(
            id: timelineId,
            projectId: projectId,
            name: name,
            settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks:
            [
                new Track(EditTimelineSeeder.TrackId("V1"), TrackKind.Video, "Video", null, 100, false, true, false, false, null, Array.Empty<Item>()),
                new Track(EditTimelineSeeder.TrackId("A1"), TrackKind.Audio, "Audio", null, 100, false, true, false, false, null, Array.Empty<Item>())
            ],
            markers: Array.Empty<Marker>());

    private static TimelineManifest BuildManifest(Guid projectId, Guid executionId, string videoStorageKey, string musicFileName) =>
        new(
            Version: 1,
            ProjectId: projectId,
            ExecutionId: executionId,
            OutputVideo: new TimelineOutputVideo(1920, 1080, 30, 8, "renders/out.mp4"),
            Sources: [new TimelineSource(0, videoStorageKey, 10, 1920, 1080, 30)],
            Tracks:
            [
                new TimelineTrack("track-video", "video", "Video", 0,
                [
                    new TimelineItem(Id: "cut-1", TimelineStartSec: 0, TimelineEndSec: 4, SourceIndex: 0, SourceStartSec: 0, SourceEndSec: 4)
                ]),
                new TimelineTrack("track-music", "music", "Music", 1,
                [
                    new TimelineItem(Id: "music-1", TimelineStartSec: 0, TimelineEndSec: 8, TrackName: musicFileName)
                ])
            ]);
}
