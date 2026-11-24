#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The durable backfill for lost media-derivative requests.
///
/// <para>
/// The behaviour under test is a selection, not a message log: a source is a candidate exactly when
/// it is a video source of this platform and no live <c>Proxy</c> row names it. These tests pin that
/// predicate against every shape the production data actually has — an uploaded video, an
/// unregistered render, a render that was registered as a file, a soft-deleted proxy — because the
/// 107 proxy-less originals this change repairs came from that predicate never being evaluated.
/// </para>
/// </summary>
public class MediaDerivativeBackfillTests
{
    // -------------------------------------------------------------------------------------------
    // Selection
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sweep_requests_a_video_project_file_with_no_proxy()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddVideo("clip.mp4");

        MediaDerivativeSweepResult result = await fx.SweepAsync();

        result.Requested.Should().Be(1);
        result.Failed.Should().Be(0);
        result.Deferred.Should().Be(0);

        MediaDerivativesRequested message = fx.Published.Should().ContainSingle().Subject;
        message.ProjectId.Should().Be(fx.Project.Id);
        message.SourceRef.Should().Be(new MediaSourceRef(MediaSourceKind.ProjectFile, file.Id));
        message.SourceProjectFileId.Should().Be(file.Id);
        message.SourceStorageKey.Should().Be(file.StorageKey);
        message.SourceEtag.Should().Be(Fixture.Etag);
        message.MimeType.Should().Be("video/mp4");
        message.Kinds.Should().BeEquivalentTo(MediaDerivativeRequests.DefaultKinds);
        message.RequestedBy.Should().Be(MediaDerivativeRequests.RequestedBySweep);
    }

    [Fact]
    public async Task Sweep_waits_for_the_csam_verdict_of_a_video_and_requests_it_once_settled()
    {
        using Fixture fx = new();
        ProjectFile pending = fx.AddFile("pending.mp4", "video/mp4", csam: CsamScanStatus.Pending);
        ProjectFile scanning = fx.AddFile("scanning.mp4", "video/mp4", csam: CsamScanStatus.Scanning);
        ProjectFile held = fx.AddFile("held.mp4", "video/mp4", csam: CsamScanStatus.Quarantined);

        (await fx.SweepAsync()).Requested.Should().Be(0);
        fx.Published.Should().BeEmpty();

        pending.CsamStatus = CsamScanStatus.NotScanned;
        scanning.CsamStatus = CsamScanStatus.Clean;
        fx.Db.SaveChanges();

        (await fx.SweepAsync()).Requested.Should().Be(2);
        fx.Published.Select(m => m.SourceRef.SourceId).Should().BeEquivalentTo(new[] { pending.Id, scanning.Id });
    }

    [Fact]
    public async Task Sweep_still_requests_the_plain_proxy_when_only_a_converted_proxy_exists()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddVideo("log.mp4");
        MediaDerivative converted = fx.AddProxy(MediaSourceKind.ProjectFile, file.Id);
        converted.ParamsJson = "{\"cameraProfile\":\"apple-log\",\"recipe\":\"x\"}";
        fx.Db.SaveChanges();

        MediaDerivativeSweepResult result = await fx.SweepAsync();

        result.Requested.Should().Be(1);
    }

    [Fact]
    public async Task Sweep_does_not_request_a_video_that_already_has_a_live_proxy()
    {
        using Fixture fx = new();
        ProjectFile covered = fx.AddVideo("covered.mp4");
        fx.AddProxy(MediaSourceKind.ProjectFile, covered.Id);

        MediaDerivativeSweepResult result = await fx.SweepAsync();

        result.Requested.Should().Be(0);
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_treats_a_soft_deleted_proxy_as_absent()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddVideo("deleted-proxy.mp4");
        fx.AddProxy(MediaSourceKind.ProjectFile, file.Id, deletedAt: DateTime.UtcNow);

        MediaDerivativeSweepResult result = await fx.SweepAsync();

        result.Requested.Should().Be(1);
        fx.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task Sweep_ignores_audio_and_image_project_files()
    {
        using Fixture fx = new();
        fx.AddFile("track.mp3", "audio/mpeg");
        fx.AddFile("still.png", "image/png");

        MediaDerivativeSweepResult result = await fx.SweepAsync();

        result.Requested.Should().Be(0);
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_requests_an_unregistered_video_render_with_no_proxy()
    {
        using Fixture fx = new();
        WorkflowStepResult render = fx.AddRender(
            $"projects/{fx.Project.Id:D}/outputFiles/exec/render.mp4");

        MediaDerivativeSweepResult result = await fx.SweepAsync();

        result.Requested.Should().Be(1);

        MediaDerivativesRequested message = fx.Published.Should().ContainSingle().Subject;
        message.SourceRef.Should().Be(new MediaSourceRef(MediaSourceKind.StepResult, render.Id));
        message.SourceProjectFileId.Should().BeNull();
        message.SourceStorageKey.Should().Be(render.OutputStorageKey);
        message.MimeType.Should().Be("video/mp4");
    }

    [Fact]
    public async Task Sweep_ignores_a_render_registered_as_a_project_file()
    {
        using Fixture fx = new();
        string key = $"projects/{fx.Project.Id:D}/outputFiles/exec/registered.mp4";
        fx.AddRender(key);
        fx.AddFile("registered.mp4", "video/mp4", storageKey: key);

        MediaDerivativeSweepResult result = await fx.SweepAsync();

        // The render is the same bytes as the file: it must not be requested twice.
        result.Requested.Should().Be(1);
        fx.Published.Should().ContainSingle()
            .Which.SourceRef.Kind.Should().Be(MediaSourceKind.ProjectFile);
    }

    [Fact]
    public async Task Sweep_ignores_a_render_whose_key_is_not_under_output_files()
    {
        using Fixture fx = new();
        fx.AddRender($"projects/{fx.Project.Id:D}/agentFiles/workspaces/render.mp4");

        MediaDerivativeSweepResult result = await fx.SweepAsync();

        result.Requested.Should().Be(0);
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_defers_a_source_whose_etag_cannot_be_read()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddVideo("no-etag.mp4");
        fx.Storage
            .Setup(s => s.GetObjectETagAsync(fx.Project.Id, file.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        MediaDerivativeSweepResult result = await fx.SweepAsync();

        result.Requested.Should().Be(0);
        result.Deferred.Should().Be(1);
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_survives_a_publish_failure_and_keeps_the_source_a_candidate()
    {
        using Fixture fx = new();
        ProjectFile first = fx.AddVideo("first.mp4", uploadedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        fx.AddVideo("second.mp4", uploadedAt: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        int attempt = 0;
        fx.Publisher
            .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Returns<MediaDerivativesRequested, CancellationToken>((message, _) =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                    throw new InvalidOperationException("broker blocked");
                fx.Published.Add(message);
                return Task.CompletedTask;
            });

        MediaDerivativeSweepResult result = await fx.SweepAsync();

        result.Requested.Should().Be(1);
        result.Failed.Should().Be(1);
        fx.Published.Should().ContainSingle()
            .Which.SourceRef.SourceId.Should().NotBe(first.Id);

        // The failed source is still a candidate (a publish that never happened is not remembered); the one
        // already requested is left alone for the cooldown instead of being queued again every sweep.
        MediaDerivativeSweepResult second = await fx.SweepAsync();
        second.Requested.Should().Be(1);
        fx.Published.Select(m => m.SourceRef.SourceId).Should().Contain(first.Id);
    }

    [Fact]
    public async Task Sweep_respects_the_batch_size()
    {
        using Fixture fx = new();
        for (int i = 0; i < 3; i++)
            fx.AddVideo($"clip-{i}.mp4", uploadedAt: new DateTime(2026, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc));

        MediaDerivativeSweepResult result = await fx.SweepAsync(batchSize: 2);

        result.Requested.Should().Be(2);
        result.BatchFull.Should().BeTrue();
    }

    [Fact]
    public async Task Second_sweep_requests_nothing_once_proxy_rows_exist()
    {
        using Fixture fx = new();
        ProjectFile file = fx.AddVideo("clip.mp4");
        WorkflowStepResult render = fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/exec/render.mp4");

        (await fx.SweepAsync()).Requested.Should().Be(2);

        // What the engine does after consuming the two requests.
        fx.AddProxy(MediaSourceKind.ProjectFile, file.Id);
        fx.AddProxy(MediaSourceKind.StepResult, render.Id);

        MediaDerivativeSweepResult rerun = await fx.SweepAsync();

        rerun.Requested.Should().Be(0);
        rerun.BatchFull.Should().BeFalse();
        fx.Published.Should().HaveCount(2);
    }

    // -------------------------------------------------------------------------------------------
    // Coverage
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Coverage_reports_the_same_predicate_as_the_sweep()
    {
        using Fixture fx = new();
        ProjectFile covered = fx.AddVideo("covered.mp4");
        fx.AddVideo("missing.mp4");
        fx.AddFile("track.mp3", "audio/mpeg");
        fx.AddProxy(MediaSourceKind.ProjectFile, covered.Id);
        fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/exec/render.mp4");

        MediaDerivativeCoverageReport report = await fx.Service.GetCoverageAsync(CancellationToken.None);

        report.VideoProjectFiles.Total.Should().Be(2);
        report.VideoProjectFiles.WithProxy.Should().Be(1);
        report.VideoProjectFiles.WithoutProxy.Should().Be(1);
        report.StepResultRenders.Total.Should().Be(1);
        report.StepResultRenders.WithProxy.Should().Be(0);
        report.VideoSources.Should().Be(3);
        report.WithProxy.Should().Be(1);
        report.WithoutProxy.Should().Be(2);

        // The observable count is exactly what a sweep would request.
        (await fx.SweepAsync()).Requested.Should().Be(report.WithoutProxy);
    }

    [Fact]
    public async Task Coverage_does_not_count_a_registered_render_twice()
    {
        using Fixture fx = new();
        string key = $"projects/{fx.Project.Id:D}/outputFiles/exec/registered.mp4";
        fx.AddRender(key);
        fx.AddFile("registered.mp4", "video/mp4", storageKey: key);

        MediaDerivativeCoverageReport report = await fx.Service.GetCoverageAsync(CancellationToken.None);

        report.VideoProjectFiles.Total.Should().Be(1);
        report.StepResultRenders.Total.Should().Be(0);
        report.VideoSources.Should().Be(1);
    }

    // -------------------------------------------------------------------------------------------
    // Admin surface
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Coverage_endpoint_forbids_a_non_platform_admin()
    {
        using Fixture fx = new();
        MediaAdminController controller = fx.ControllerAsPlatformAdmin(false);

        ActionResult<MediaDerivativeCoverageResponse> result = await controller.GetCoverage(CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Backfill_endpoint_forbids_a_non_platform_admin()
    {
        using Fixture fx = new();
        MediaAdminController controller = fx.ControllerAsPlatformAdmin(false);

        ActionResult<MediaDerivativeBackfillResponse> result =
            await controller.Backfill(new MediaDerivativeBackfillRequest(), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        fx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Backfill_endpoint_requests_missing_sources_and_reports_counts()
    {
        using Fixture fx = new();
        fx.AddVideo("missing.mp4");

        MediaAdminController controller = fx.ControllerAsPlatformAdmin(true);

        ActionResult<MediaDerivativeBackfillResponse> result =
            await controller.Backfill(new MediaDerivativeBackfillRequest(), CancellationToken.None);

        AcceptedResult accepted = result.Result.Should().BeOfType<AcceptedResult>().Subject;
        MediaDerivativeBackfillResponse body =
            accepted.Value.Should().BeOfType<MediaDerivativeBackfillResponse>().Subject;

        body.Requested.Should().Be(1);
        body.Failed.Should().Be(0);
        body.Coverage.VideoSources.Should().Be(1);
        body.Coverage.WithoutProxy.Should().Be(1);
        body.Coverage.SweepEnabled.Should().BeTrue();
        fx.Published.Should().ContainSingle()
            .Which.RequestedBy.Should().Be(MediaDerivativeRequests.RequestedByBackfill);
    }

    [Fact]
    public async Task Sweep_in_a_project_with_an_output_marker_backfills_an_upload_and_a_later_output_and_skips_earlier_outputs_without_starving_the_batch()
    {
        using Fixture fx = new();
        DateTime now = DateTime.UtcNow;
        fx.Project.CsamOutputHoldAt = now.AddHours(-1);
        fx.Db.SaveChanges();

        // Three earlier outputs, older than everything else: with a batch of three they would fill it.
        for (int i = 0; i < 3; i++)
        {
            ProjectFile earlier = fx.AddFile(
                $"earlier{i}.mp4", "video/mp4",
                storageKey: $"projects/{fx.Project.Id:D}/outputFiles/earlier{i}.mp4", uploadedAt: now.AddDays(-10 + i));
            earlier.OutputProducedAt = now.AddHours(-3);
        }

        WorkflowStepResult earlierRender = fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/render-old.mp4", completedAt: now.AddDays(-9));
        WorkflowExecution earlierRun = fx.Db.WorkflowExecutions.Single(e => e.Id == earlierRender.WorkflowExecutionId);
        earlierRun.FirstStartedAt = now.AddHours(-4);
        earlierRun.StartedAt = now.AddHours(-4);

        ProjectFile upload = fx.AddVideo("mine.mp4", uploadedAt: now.AddHours(-5));
        ProjectFile laterFile = fx.AddFile(
            "later.mp4", "video/mp4", storageKey: $"projects/{fx.Project.Id:D}/outputFiles/later.mp4", uploadedAt: now.AddMinutes(-10));
        laterFile.OutputProducedAt = now.AddMinutes(-10);
        WorkflowStepResult laterRender = fx.AddRender($"projects/{fx.Project.Id:D}/outputFiles/render-new.mp4", completedAt: now.AddMinutes(-5));
        WorkflowExecution laterRun = fx.Db.WorkflowExecutions.Single(e => e.Id == laterRender.WorkflowExecutionId);
        laterRun.FirstStartedAt = now.AddMinutes(-8);
        laterRun.StartedAt = now.AddMinutes(-8);
        fx.Db.SaveChanges();

        MediaDerivativeSweepResult result = await fx.SweepAsync(batchSize: 3);

        result.Requested.Should().Be(3);
        fx.Published.Select(m => m.SourceRef!.SourceId).Should().BeEquivalentTo([upload.Id, laterFile.Id, laterRender.Id]);
    }

    [Fact]
    public async Task Sweep_without_an_output_marker_still_requests_every_source()
    {
        using Fixture fx = new();
        DateTime now = DateTime.UtcNow;
        ProjectFile output = fx.AddFile(
            "o.mp4", "video/mp4", storageKey: $"projects/{fx.Project.Id:D}/outputFiles/o.mp4", uploadedAt: now.AddDays(-2));
        output.OutputProducedAt = now.AddDays(-2);
        fx.Db.SaveChanges();

        (await fx.SweepAsync()).Requested.Should().Be(1);
    }

    // -------------------------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        public const string Etag = "\"etag-0123456789abcdef\"";

        public Fixture()
        {
            Db = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options);

            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Backfill Project",
                OwnerId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.Projects.Add(Project);
            Db.SaveChanges();

            Storage
                .Setup(s => s.GetObjectETagAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Etag);

            Publisher
                .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
                .Returns<MediaDerivativesRequested, CancellationToken>((message, _) =>
                {
                    Published.Add(message);
                    return Task.CompletedTask;
                });

            Service = new MediaDerivativeCoverageService(
                Db,
                Storage.Object,
                Publisher.Object,
                new TenantContext(new HttpContextAccessor()),
                NullLogger<MediaDerivativeCoverageService>.Instance);
        }

        public InferenceApiDbContext Db { get; }

        public Project Project { get; }

        public Mock<IFileStorageService> Storage { get; } = new();

        public Mock<IPublishEndpoint> Publisher { get; } = new();

        public List<MediaDerivativesRequested> Published { get; } = [];

        public MediaDerivativeCoverageService Service { get; }

        public Task<MediaDerivativeSweepResult> SweepAsync(int batchSize = 200) =>
            Service.SweepOnceAsync(MediaDerivativeRequests.RequestedBySweep, batchSize, CancellationToken.None);

        public ProjectFile AddVideo(string fileName, DateTime? uploadedAt = null) =>
            AddFile(fileName, "video/mp4", uploadedAt: uploadedAt);

        public ProjectFile AddFile(
            string fileName,
            string mimeType,
            string? storageKey = null,
            DateTime? uploadedAt = null,
            CsamScanStatus csam = CsamScanStatus.Clean)
        {
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = fileName,
                StorageKey = storageKey ?? $"projects/{Project.Id:D}/userFiles/{Guid.NewGuid():N}/{fileName}",
                StorageBucket = "reelforge",
                Category = "userFiles",
                MimeType = mimeType,
                SizeBytes = 1024,
                UploadedAt = uploadedAt ?? DateTime.UtcNow,
                CsamStatus = csam
            };

            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        public WorkflowStepResult AddRender(string storageKey, DateTime? completedAt = null)
        {
            WorkflowExecution execution = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                ProjectId = Project.Id,
                Status = ExecutionStatus.Passed
            };

            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = execution.WorkflowDefinitionId,
                AgentDefinitionId = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoCompile
            };

            WorkflowStepResult result = new()
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                WorkflowStepId = step.Id,
                ExecutedAt = completedAt ?? DateTime.UtcNow,
                CompletedAt = completedAt,
                OutputStorageKey = storageKey,
                Status = StepStatus.Completed
            };

            Db.WorkflowExecutions.Add(execution);
            Db.WorkflowSteps.Add(step);
            Db.WorkflowStepResults.Add(result);
            Db.SaveChanges();
            return result;
        }

        public MediaDerivative AddProxy(
            MediaSourceKind kind,
            Guid sourceId,
            DateTime? deletedAt = null)
        {
            MediaDerivative row = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                SourceKind = kind,
                SourceProjectFileId = kind == MediaSourceKind.ProjectFile ? sourceId : null,
                SourceStepResultId = kind == MediaSourceKind.StepResult ? sourceId : null,
                Kind = MediaDerivativeKind.Proxy,
                SourceStorageKey = $"projects/{Project.Id:D}/source.mp4",
                SourceETag = Etag,
                CacheKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                StorageKey = $"projects/{Project.Id:D}/agentFiles/media-derivatives/hash/proxy.mp4",
                StorageBucket = "reelforge",
                MimeType = "video/mp4",
                SizeBytes = 4096,
                CreatedAt = DateTime.UtcNow,
                DeletedAt = deletedAt
            };

            Db.MediaDerivatives.Add(row);
            Db.SaveChanges();
            return row;
        }

        public MediaAdminController ControllerAsPlatformAdmin(bool isPlatformAdmin) =>
            new(
                Service,
                new FakeCurrentUser(isPlatformAdmin),
                Options.Create(new MediaDerivativeBackfillOptions()));

        public void Dispose() => Db.Dispose();
    }

    private sealed class FakeCurrentUser(bool isPlatformAdmin) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();

        public string Email => "admin@example.com";

        public bool IsAuthenticated => true;

        public bool IsAdmin => isPlatformAdmin;

        public bool IsPlatformAdmin => isPlatformAdmin;

        public Guid? OrganizationId => null;

        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }
}
