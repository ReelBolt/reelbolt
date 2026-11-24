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
/// Regression for Npgsql 42883 <c>operator does not exist: jsonb !~~ unknown</c>: the sweep and the coverage report
/// excluded converted proxies with a LIKE on the jsonb <c>params_json</c> column. Runs on a real schema (skipped unless
/// <c>REELBOLT_TEST_PG</c> is set) and checks that a converted proxy (params carry <c>cameraProfile</c>) does not count
/// as the plain proxy while a plain proxy does.
/// </summary>
[Collection("Postgres")]
public class MediaDerivativeConvertedProxyPostgresTests
{
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Coverage_and_sweep_run_on_jsonb_and_classify_converted_and_plain_proxies()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_convproxy_test_" + Guid.NewGuid().ToString("N")[..12];

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
            DateTime now = DateTime.UtcNow;

            ProjectFile plain = VideoFile(projectId, "plain.mp4", now);
            ProjectFile convertedOnly = VideoFile(projectId, "converted-only.mp4", now);

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
                    Name = "Converted",
                    Slug = "converted-" + orgId.ToString("N"),
                    Kind = OrganizationKind.Personal,
                    CreatedByUserId = ownerId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                seed.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Converted Project",
                    OwnerId = ownerId,
                    OrganizationId = orgId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                seed.ProjectFiles.AddRange(plain, convertedOnly);
                seed.MediaDerivatives.AddRange(
                    Proxy(projectId, plain.Id, "{}"),
                    Proxy(projectId, convertedOnly.Id, "{\"cameraProfile\":\"slog3\"}"));
                await seed.SaveChangesAsync();
            }

            List<MediaDerivativesRequested> published = new();
            Mock<IPublishEndpoint> publisher = new();
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

            TenantContext tenant = new(new HttpContextAccessor());
            MediaDerivativeCoverageService service = new(
                new InferenceApiDbContext(apiOptions, tenant),
                storage.Object,
                publisher.Object,
                tenant,
                NullLogger<MediaDerivativeCoverageService>.Instance);

            MediaDerivativeCoverageReport report = await service.GetCoverageAsync(CancellationToken.None);
            report.VideoProjectFiles.Should().Be(new MediaDerivativeSourceCoverage(2, 1));

            MediaDerivativeSweepResult sweep =
                await service.SweepOnceAsync(MediaDerivativeRequests.RequestedByBackfill, 100, CancellationToken.None);

            sweep.Failed.Should().Be(0);
            sweep.Requested.Should().Be(1);
            published.Select(m => m.SourceRef).Should().BeEquivalentTo(new[]
            {
                new MediaSourceRef(MediaSourceKind.ProjectFile, convertedOnly.Id)
            });
        }
        finally
        {
            await using NpgsqlDataSource cleanup = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = cleanup.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

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

    private static MediaDerivative Proxy(Guid projectId, Guid fileId, string paramsJson) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        SourceKind = MediaSourceKind.ProjectFile,
        SourceProjectFileId = fileId,
        Kind = MediaDerivativeKind.Proxy,
        SourceStorageKey = $"projects/{projectId:D}/userFiles/source.mp4",
        SourceETag = "\"etag-backfill\"",
        ParamsHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        ParamsJson = paramsJson,
        BuilderVersion = 1,
        CacheKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        StorageKey = $"projects/{projectId:D}/agentFiles/media-derivatives/hash/proxy.mp4",
        StorageBucket = "reelforge",
        MimeType = "video/mp4",
        SizeBytes = 4096,
        CreatedAt = DateTime.UtcNow
    };
}
