#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The backfill's selection on the real schema (skipped unless <c>REELBOLT_TEST_PG</c> is set).
///
/// <para>
/// The in-memory suite pins the predicate. This one proves the three places only a real database can
/// disagree with it: the tenant query filter opened by the sweep's system scope (a context built with
/// a tenant sees nothing without it), the <c>deleted_at IS NULL</c> filter on <c>media_derivatives</c>,
/// and the join that keeps a render registered as a project file from being requested twice. It seeds
/// the shapes production has — video files with and without a proxy, a soft-deleted proxy, an
/// unregistered render, a covered render, a registered render — runs the backfill twice, and asserts
/// that the second run is a no-op.
/// </para>
/// </summary>
[Collection("Postgres")]
public class MediaDerivativeBackfillPostgresTests
{
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Backfill_requests_every_proxy_less_video_source_once_and_a_rerun_is_a_no_op()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_backfill_test_" + Guid.NewGuid().ToString("N")[..12];

        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        string connection = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);

        DbContextOptions<InferenceApiDbContext> apiOptions =
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options;
        DbContextOptions<WorkflowEngineDbContext> engineOptions =
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;

        try
        {
            await using (InferenceApiDbContext api = new(apiOptions))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            await using (WorkflowEngineDbContext engine = new(engineOptions))
            {
                await engine.GetService<IMigrator>().MigrateAsync();
            }

            Guid ownerId = Guid.NewGuid();
            Guid orgId = Guid.NewGuid();
            Guid projectId = Guid.NewGuid();
            Guid agentId = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;

            ProjectFile covered = VideoFile(projectId, "covered.mp4", now);
            ProjectFile softDeleted = VideoFile(projectId, "soft-deleted-proxy.mp4", now);
            ProjectFile registered = VideoFile(projectId, "registered.mp4", now);
            ProjectFile audio = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OriginalFileName = "track.mp3",
                StorageKey = $"projects/{projectId:D}/userFiles/audio/track.mp3",
                StorageBucket = "reelforge",
                Category = "userFiles",
                MimeType = "audio/mpeg",
                SizeBytes = 1024,
                UploadedAt = now
            };

            string registeredRenderKey = registered.StorageKey;
            // One step, because workflow_step_results.workflow_step_id is a real FK on the engine schema.
            Guid stepId = Guid.NewGuid();
            WorkflowStepResult missingRender = Render(projectId, $"projects/{projectId:D}/outputFiles/exec/missing.mp4", now, stepId);
            WorkflowStepResult coveredRender = Render(projectId, $"projects/{projectId:D}/outputFiles/exec/covered.mp4", now, stepId);
            WorkflowStepResult registeredRender = Render(projectId, registeredRenderKey, now, stepId);

            await using (InferenceApiDbContext seed = new(apiOptions))
            {
                seed.ApplicationUsers.Add(new ApplicationUser
                {
                    Id = ownerId,
                    Email = $"{ownerId:N}@example.com",
                    DisplayName = "Ada",
                    PasswordHash = "hash",
                    CreatedAt = now,
                    EmailVerifiedAt = now
                });
                seed.Organizations.Add(new Organization
                {
                    Id = orgId,
                    Name = "Backfill",
                    Slug = "backfill-" + orgId.ToString("N"),
                    Kind = OrganizationKind.Personal,
                    CreatedByUserId = ownerId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                seed.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Backfill Project",
                    OwnerId = ownerId,
                    OrganizationId = orgId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                seed.AgentDefinitions.Add(new AgentDefinition
                {
                    Id = agentId,
                    Name = "VideoTransform",
                    Description = string.Empty,
                    SystemPrompt = string.Empty,
                    AgentType = AgentType.VideoTransform,
                    IsBuiltIn = true,
                    CreatedAt = now
                });
                seed.ProjectFiles.AddRange(covered, softDeleted, registered, audio);
                seed.MediaDerivatives.AddRange(
                    Proxy(projectId, MediaSourceKind.ProjectFile, covered.Id, deletedAt: null),
                    Proxy(projectId, MediaSourceKind.ProjectFile, softDeleted.Id, deletedAt: now),
                    Proxy(projectId, MediaSourceKind.StepResult, coveredRender.Id, deletedAt: null));
                await seed.SaveChangesAsync();
            }

            await using (WorkflowEngineDbContext seed = new(engineOptions))
            {
                Guid definitionId = Guid.NewGuid();

                seed.WorkflowDefinitions.Add(new WorkflowDefinition
                {
                    Id = definitionId,
                    Name = "w",
                    ProjectId = projectId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                seed.WorkflowSteps.Add(new WorkflowStep
                {
                    Id = stepId,
                    WorkflowDefinitionId = definitionId,
                    AgentDefinitionId = agentId,
                    StepOrder = 1,
                    StepType = StepType.VideoCompile
                });

                foreach (WorkflowStepResult render in new[] { missingRender, coveredRender, registeredRender })
                {
                    seed.WorkflowExecutions.Add(new WorkflowExecution
                    {
                        Id = render.WorkflowExecutionId,
                        WorkflowDefinitionId = definitionId,
                        ProjectId = projectId,
                        OrganizationId = orgId,
                        Status = ExecutionStatus.Passed,
                        CorrelationId = render.WorkflowExecutionId.ToString("N"),
                        CompletedAt = now
                    });
                    seed.WorkflowStepResults.Add(render);
                }

                await seed.SaveChangesAsync();
            }

            Mock<IPublishEndpoint> publisher = new();
            List<MediaDerivativesRequested> published = [];
            publisher
                .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
                .Returns<MediaDerivativesRequested, CancellationToken>((message, _) =>
                {
                    published.Add(message);
                    return Task.CompletedTask;
                });

            Mock<IFileStorageService> storage = new();
            storage
                .Setup(s => s.GetObjectETagAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("\"etag-backfill\"");

            // The tenant-filtered context the service actually gets from DI: outside a system scope its
            // project_files query matches nothing, so a green test also proves the sweep opens one.
            TenantContext tenant = new(new HttpContextAccessor());
            MediaDerivativeCoverageService service = new(
                new InferenceApiDbContext(apiOptions, tenant),
                storage.Object,
                publisher.Object,
                tenant,
                NullLogger<MediaDerivativeCoverageService>.Instance);

            MediaDerivativeCoverageReport before = await service.GetCoverageAsync(CancellationToken.None);

            before.VideoProjectFiles.Should().Be(new MediaDerivativeSourceCoverage(3, 1));
            before.StepResultRenders.Should().Be(new MediaDerivativeSourceCoverage(2, 1));
            before.VideoSources.Should().Be(5);
            before.WithProxy.Should().Be(2);
            before.WithoutProxy.Should().Be(3);

            MediaDerivativeSweepResult first =
                await service.SweepOnceAsync(MediaDerivativeRequests.RequestedByBackfill, 100, CancellationToken.None);

            first.Requested.Should().Be(3);
            first.Deferred.Should().Be(0);
            first.Failed.Should().Be(0);

            published.Select(m => m.SourceRef).Should().BeEquivalentTo(new[]
            {
                new MediaSourceRef(MediaSourceKind.ProjectFile, softDeleted.Id),
                new MediaSourceRef(MediaSourceKind.ProjectFile, registered.Id),
                new MediaSourceRef(MediaSourceKind.StepResult, missingRender.Id)
            });

            // The engine commits the three proxy rows.
            await using (InferenceApiDbContext seed = new(apiOptions))
            {
                seed.MediaDerivatives.AddRange(
                    Proxy(projectId, MediaSourceKind.ProjectFile, softDeleted.Id, deletedAt: null),
                    Proxy(projectId, MediaSourceKind.ProjectFile, registered.Id, deletedAt: null),
                    Proxy(projectId, MediaSourceKind.StepResult, missingRender.Id, deletedAt: null));
                await seed.SaveChangesAsync();
            }

            MediaDerivativeSweepResult second =
                await service.SweepOnceAsync(MediaDerivativeRequests.RequestedByBackfill, 100, CancellationToken.None);

            second.Requested.Should().Be(0, "a re-run must be a no-op once every source has a live proxy");
            published.Should().HaveCount(3, "the second run published nothing");

            MediaDerivativeCoverageReport after = await service.GetCoverageAsync(CancellationToken.None);
            after.WithoutProxy.Should().Be(0);
            after.WithProxy.Should().Be(after.VideoSources);
        }
        finally
        {
            await using NpgsqlDataSource cleanup = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = cleanup.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------------------------------

    private static ProjectFile VideoFile(Guid projectId, string fileName, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        OriginalFileName = fileName,
        StorageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}/{fileName}",
        StorageBucket = "reelforge",
        Category = "userFiles",
        MimeType = "video/mp4",
        SizeBytes = 1024,
        UploadedAt = now
    };

    private static WorkflowStepResult Render(Guid projectId, string storageKey, DateTime now, Guid stepId) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowExecutionId = Guid.NewGuid(),
        WorkflowStepId = stepId,
        Output = string.Empty,
        ExecutedAt = now,
        CompletedAt = now,
        Status = StepStatus.Completed,
        OutputStorageKey = storageKey
    };

    private static MediaDerivative Proxy(
        Guid projectId,
        MediaSourceKind kind,
        Guid sourceId,
        DateTime? deletedAt) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceKind = kind,
            SourceProjectFileId = kind == MediaSourceKind.ProjectFile ? sourceId : null,
            SourceStepResultId = kind == MediaSourceKind.StepResult ? sourceId : null,
            Kind = MediaDerivativeKind.Proxy,
            SourceStorageKey = $"projects/{projectId:D}/userFiles/source.mp4",
            SourceETag = "\"etag-backfill\"",
            ParamsHash = new string('a', 64),
            ParamsJson = "{}",
            BuilderVersion = 1,
            CacheKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            StorageKey = $"projects/{projectId:D}/agentFiles/media-derivatives/hash/proxy.mp4",
            StorageBucket = "reelforge",
            MimeType = "video/mp4",
            SizeBytes = 4096,
            CreatedAt = DateTime.UtcNow,
            DeletedAt = deletedAt
        };
}
