#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Editor v2 Wave 4 (R10.4): the "Preview 3 s" endpoints, the Preview preset sizes and the sweeper.</summary>
public class EditTimelinePreviewTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static void QueueThrows(Harness h) =>
        h.Executions.Setup(e => e.QueueAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<RunChoices?>(), It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
            .ThrowsAsync(new InvalidOperationException("reached the queue"));

    private static TimelineRenderStepConfig StoredConfig(Harness h) =>
        JsonSerializer.Deserialize<TimelineRenderStepConfig>(h.Db.WorkflowSteps.IgnoreQueryFilters().Single().TimelineRenderConfigJson!, Web)!;

    private static async Task<TimelineRenderStepConfig> QueuedConfig(Harness h, PreviewRenderRequest request)
    {
        QueueThrows(h);
        Func<Task> run = () => h.Controller().RenderPreview(h.ProjectId, h.TimelineId, request, default);
        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("reached the queue");
        return StoredConfig(h);
    }

    [Fact]
    public async Task The_kind_is_server_set_and_a_client_kind_field_is_ignored()
    {
        using Harness h = await Harness.CreateAsync();
        PreviewRenderRequest body = JsonSerializer.Deserialize<PreviewRenderRequest>(
            """{"atFrame":30,"previewJobKind":"loudness","audioJobKind":"audition","preset":"Hd1080p16x9"}""", Web)!;

        TimelineRenderStepConfig config = await QueuedConfig(h, body);

        config.AudioJobKind.Should().Be("preview");
        config.Preset.Should().Be(TimelineRenderPreset.Preview);
        config.RegisterProjectFile.Should().BeFalse();
        config.Loudness.Should().BeNull("loudness is off for a preview");
        config.Captions.Should().Be(TimelineRenderCaptions.BurnIn, "the preview is truthful about enabled captions");
        config.Revision.Should().Be(1, "the current revision is pinned");
        config.OutputFileName.Should().StartWith("preview-").And.EndWith(".mp4");
        h.Db.WorkflowDefinitions.IgnoreQueryFilters().Single().Name.Should().StartWith(TimelinePreviewSweeper.WorkflowNamePrefix);
    }

    [Fact]
    public async Task The_public_render_and_estimate_refuse_the_preview_preset_and_the_reserved_file_prefix()
    {
        using Harness h = await Harness.CreateAsync();

        (await h.Controller().RenderEditTimeline(h.ProjectId, h.TimelineId, new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Preview), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().RenderEditTimeline(h.ProjectId, h.TimelineId, new RenderEditTimelineRequest(OutputFileName: "preview-x.mp4"), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().RenderEditTimeline(h.ProjectId, h.TimelineId, new RenderEditTimelineRequest(Preview: new PreviewRenderRequest(0)), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().EstimateRender(h.ProjectId, h.TimelineId, new RenderEditTimelineRequest(Preset: TimelineRenderPreset.Preview), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Db.WorkflowDefinitions.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(0, null, 0, 90)]
    [InlineData(100, 2.0, 100, 160)]
    [InlineData(100, 1.0, 100, 130)]
    [InlineData(290, null, 210, 300)]
    [InlineData(100000, null, 210, 300)]
    public async Task The_range_is_the_window_clamped_to_the_timeline(long atFrame, double? seconds, long expectIn, long expectOut)
    {
        using Harness h = await Harness.CreateAsync();

        TimelineRenderStepConfig config = await QueuedConfig(h, new PreviewRenderRequest(atFrame, seconds));

        config.Range.Should().Be(new TimelineRenderRange(expectIn, expectOut));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(3.5)]
    [InlineData(-1.0)]
    public async Task Seconds_outside_one_to_three_is_a_400(double seconds)
    {
        using Harness h = await Harness.CreateAsync();

        (await h.Controller().RenderPreview(h.ProjectId, h.TimelineId, new PreviewRenderRequest(0, seconds), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().RenderPreview(h.ProjectId, h.TimelineId, new PreviewRenderRequest(-5), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().RenderPreview(h.ProjectId, h.TimelineId, null, default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task A_viewer_is_refused_and_a_foreign_timeline_is_404()
    {
        using Harness h = await Harness.CreateAsync();

        (await h.Controller(viewOnly: true).RenderPreview(h.ProjectId, h.TimelineId, new PreviewRenderRequest(0), default))
            .Result.Should().BeOfType<ForbidResult>();
        (await h.Controller().RenderPreview(Guid.NewGuid(), h.TimelineId, new PreviewRenderRequest(0), default))
            .Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller().RenderPreview(h.ProjectId, Guid.NewGuid(), new PreviewRenderRequest(0), default))
            .Result.Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(1080, 1920, 540, 960)]
    [InlineData(1080, 1080, 540, 540)]
    [InlineData(1080, 1350, 540, 676)]
    [InlineData(640, 360, 640, 360)]
    public void The_preview_preset_is_540_on_the_short_side_in_the_timelines_aspect_and_never_upscales(int w, int h, int expectW, int expectH)
    {
        TimelineRenderStepConfig config = new(Guid.NewGuid(), Preset: TimelineRenderPreset.Preview);

        TimelineRenderSizing.Dimensions(config, w, h).Should().Be((expectW, expectH));
        TimelineRenderSizing.ExplicitShortSide(config).Should().BeNull("the preview size is never an explicit plan request");
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public async Task Too_few_credits_is_a_402_that_persists_nothing_and_the_estimate_prices_the_720_tier_for_the_window()
    {
        using Harness h = await Harness.CreateAsync(balanceMillis: 1_000);
        h.SetPrice(5_000);

        ObjectResult refused = (await h.Controller(billing: true).RenderPreview(h.ProjectId, h.TimelineId, new PreviewRenderRequest(0), default))
            .Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        JsonSerializer.Serialize(refused.Value, Web).Should().Contain("INSUFFICIENT_CREDITS");
        (await h.Db.WorkflowDefinitions.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        EditTimelineRenderEstimateResponse estimate = (await h.Controller(billing: true).EstimateRender(
                h.ProjectId, h.TimelineId, new RenderEditTimelineRequest(Preview: new PreviewRenderRequest(60, 2)), default))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<EditTimelineRenderEstimateResponse>().Subject;
        estimate.BillingEnabled.Should().BeTrue();
        estimate.Credits.Should().Be(5.0);
        estimate.Affordable.Should().BeFalse();
        h.LastPrice!.Variant.Should().Be(ComputeMetering.Tier720);
        h.LastPrice.Quantity.Should().Be(2m, "two seconds were asked for");
        (await h.Db.WorkflowDefinitions.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_hourly_cap_is_a_429_with_its_own_code_and_old_previews_do_not_count()
    {
        using Harness h = await Harness.CreateAsync(maxPerHour: 2);
        await h.AddPreviewAsync(queuedAt: DateTime.UtcNow.AddMinutes(-10));
        await h.AddPreviewAsync(queuedAt: DateTime.UtcNow.AddMinutes(-120));

        QueueThrows(h);
        Func<Task> underCap = () => h.Controller().RenderPreview(h.ProjectId, h.TimelineId, new PreviewRenderRequest(0), default);
        await underCap.Should().ThrowAsync<InvalidOperationException>("one recent preview of two allowed");
        h.Db.ChangeTracker.Clear();

        await h.AddPreviewAsync(queuedAt: DateTime.UtcNow.AddMinutes(-5));
        ObjectResult limited = (await h.Controller().RenderPreview(h.ProjectId, h.TimelineId, new PreviewRenderRequest(0), default))
            .Result.Should().BeOfType<ObjectResult>().Subject;

        limited.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(limited.Value, Web));
        body.RootElement.GetProperty("code").GetString().Should().Be("PREVIEW_RATE_LIMITED");
        body.RootElement.GetProperty("message").GetString().Should().NotContain("—");
    }

    [Fact]
    public async Task Previews_are_hidden_from_the_renders_list_and_the_audio_jobs()
    {
        using Harness h = await Harness.CreateAsync();
        Guid job = await h.AddPreviewAsync();

        List<EditTimelineRenderResponse> listed = (await h.Controller().ListRenders(h.ProjectId, h.TimelineId, default))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<EditTimelineRenderResponse>>().Subject;

        listed.Should().BeEmpty();
        (await h.Controller().GetAudioJob(h.ProjectId, h.TimelineId, job, default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Status_and_content_are_404_across_tenants_timelines_and_job_kinds()
    {
        using Harness h = await Harness.CreateAsync();
        Guid job = await h.AddPreviewAsync(output: "k/preview.mp4");
        Guid plain = await h.AddPreviewAsync(kind: null);
        h.StoreFile("k/preview.mp4", [1, 2, 3]);

        (await h.Controller().GetPreviewJob(Guid.NewGuid(), h.TimelineId, job, default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller().GetPreviewJob(h.ProjectId, Guid.NewGuid(), job, default)).Result.Should().BeOfType<NotFoundResult>("the job must belong to the timeline in the route");
        (await h.Controller().GetPreviewJob(h.ProjectId, h.TimelineId, plain, default)).Result.Should().BeOfType<NotFoundResult>("an ordinary render is not a preview");
        (await h.Controller().GetPreviewContent(Guid.NewGuid(), h.TimelineId, job, default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().GetPreviewContent(h.ProjectId, h.TimelineId, plain, default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().GetPreviewContent(h.ProjectId, h.TimelineId, Guid.NewGuid(), default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Status_reports_running_ready_and_expired_without_any_storage_key()
    {
        using Harness h = await Harness.CreateAsync();
        Guid running = await h.AddPreviewAsync(status: ExecutionStatus.Running);
        Guid passed = await h.AddPreviewAsync(output: "k/ok.mp4", durationSec: 3);
        Guid gone = await h.AddPreviewAsync(output: "k/gone.mp4");
        Guid failed = await h.AddPreviewAsync(status: ExecutionStatus.Failed, error: "No clip covers that part.");
        h.StoreFile("k/ok.mp4", [1, 2, 3]);

        PreviewJobResponse Get(Guid id) => h.Controller().GetPreviewJob(h.ProjectId, h.TimelineId, id, default).GetAwaiter().GetResult()
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<PreviewJobResponse>().Subject;

        Get(running).Should().Match<PreviewJobResponse>(r => r.Status == "Running" && !r.Ready);
        Get(passed).Should().Match<PreviewJobResponse>(r => r.Status == "Passed" && r.Ready && r.DurationSec == 3);
        PreviewJobResponse expired = Get(gone);
        expired.Ready.Should().BeFalse();
        expired.ErrorMessage.Should().Contain("expired");
        Get(failed).Should().Match<PreviewJobResponse>(r => r.Status == "Failed" && !r.Ready && r.ErrorMessage == "No clip covers that part.");
        JsonSerializer.Serialize(Get(passed), Web).Should().NotContain("k/ok.mp4");
    }

    [Fact]
    public async Task Content_streams_the_mp4_with_range_and_no_store()
    {
        using Harness h = await Harness.CreateAsync();
        byte[] bytes = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        Guid job = await h.AddPreviewAsync(output: "k/ok.mp4");
        h.StoreFile("k/ok.mp4", bytes);
        EditTimelinesController controller = h.Controller();
        DefaultHttpContext http = new() { RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider() };
        http.Request.Method = "GET";
        http.Request.Headers.Range = "bytes=10-19";
        http.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        IActionResult result = await controller.GetPreviewContent(h.ProjectId, h.TimelineId, job, default);

        FileStreamResult file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be("video/mp4");
        file.EnableRangeProcessing.Should().BeTrue();
        http.Response.Headers.CacheControl.ToString().Should().Be("private, no-store");
        await file.ExecuteResultAsync(controller.ControllerContext);
        http.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        ((MemoryStream)http.Response.Body).ToArray().Should().Equal(bytes[10..20]);
    }

    /// <summary>
    /// A preview is judged by the clips it was RENDERED from (its pinned revision), not by what the timeline holds now:
    /// the live timeline (revision 2) has had the output clip taken out, the preview (revision 1) was rendered with it.
    /// </summary>
    private static async Task<(Harness H, Guid Job, ReelBolt.Inference.Api.Services.Moderation.PublishModeration Moderation)> PinnedAsync(
        string? verdict, bool snapshotExists)
    {
        Harness h = await Harness.CreateAsync();
        Guid orgId = TestTenancy.OrgOf(h.OwnerId);
        string outputKey = ReelBolt.Shared.ProjectFilePath.BuildStorageKey(h.ProjectId, "outputFiles", null, "out.mp4");
        Guid outputFileId = Guid.NewGuid();
        h.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = outputFileId,
            ProjectId = h.ProjectId,
            OriginalFileName = "out.mp4",
            OriginalPath = "out.mp4",
            Category = "outputFiles",
            StorageFileName = "out.mp4",
            StorageKey = outputKey,
            MimeType = "video/mp4",
            SizeBytes = 1000,
            CsamStatus = CsamScanStatus.Clean
        });
        if (verdict is not null)
        {
            h.Db.OutputModerationVerdicts.Add(new ReelBolt.Inference.Api.Services.Moderation.OutputModerationVerdict
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                ProjectId = h.ProjectId,
                StorageKey = outputKey,
                SourceVersion = "e1:1000",
                State = verdict,
                ModelSnapshot = "m",
                ReasonMessage = "no"
            });
        }

        EditTimeline Doc(Guid file) => new(
            id: h.TimelineId, projectId: h.ProjectId, name: "Cut",
            settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks: [new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null,
                [new MediaClip(Guid.NewGuid(), "Clip", 0, 300, new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: file))])],
            markers: []);
        // Live: revision 2 with a clip that is not an output. Pinned: revision 1, with the output clip.
        EditTimelineEntity live = await h.Db.EditTimelines.SingleAsync();
        live.Revision = 2;
        live.DocumentJson = JsonSerializer.Serialize(Doc(Guid.NewGuid()), EditTimelineJson.Options);
        if (snapshotExists)
        {
            h.Db.EditTimelineSnapshots.Add(new EditTimelineSnapshot
            {
                Id = Guid.NewGuid(),
                TimelineId = h.TimelineId,
                Revision = 1,
                Cause = EditTimelineSnapshotCause.Milestone,
                DocumentJson = JsonSerializer.Serialize(Doc(outputFileId), EditTimelineJson.Options),
                CreatedAt = DateTime.UtcNow
            });
        }

        await h.Db.SaveChangesAsync();
        Guid job = await h.AddPreviewAsync(output: "k/ok.mp4", revision: 1);
        h.StoreFile("k/ok.mp4", [1, 2, 3]);
        h.Storage.Setup(s => s.GetMetadataAsync(h.ProjectId, outputKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(1000, "video/mp4", "\"e1\""));
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration moderation = new(
            new Mock<ReelBolt.Shared.Moderation.IContentModerationGate>().Object, h.Storage.Object,
            new Mock<ReelBolt.Shared.Moderation.IModerationMediaSampler>().Object,
            new Mock<ReelBolt.Shared.Inference.IInferenceProviderResolver>().Object,
            new Mock<ReelBolt.Shared.Inference.ITranscriptionClientFactory>().Object,
            h.Db, Options.Create(new ReelBolt.Shared.Moderation.ContentModerationOptions { Enabled = true }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReelBolt.Inference.Api.Services.Moderation.PublishModeration>.Instance);
        return (h, job, moderation);
    }

    [Fact]
    public async Task Preview_content_is_gated_by_the_pinned_revision_not_the_current_one()
    {
        (Harness h, Guid job, var moderation) = await PinnedAsync(verdict: null, snapshotExists: true);
        using (h)
        {
            IActionResult result = await h.Controller(moderation: moderation).GetPreviewContent(h.ProjectId, h.TimelineId, job, default);

            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409, "the clip the preview was rendered from has no verdict yet");
        }
    }

    [Fact]
    public async Task Preview_content_is_served_when_the_pinned_revisions_clip_is_allowed()
    {
        (Harness h, Guid job, var moderation) = await PinnedAsync(verdict: "Allow", snapshotExists: true);
        using (h)
        {
            EditTimelinesController controller = h.Controller(moderation: moderation);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider(),
                    Response = { Body = new MemoryStream() }
                }
            };

            (await controller.GetPreviewContent(h.ProjectId, h.TimelineId, job, default)).Should().BeOfType<FileStreamResult>();
        }
    }

    [Fact]
    public async Task Preview_content_refuses_when_its_pinned_revision_can_no_longer_be_read()
    {
        (Harness h, Guid job, var moderation) = await PinnedAsync(verdict: "Allow", snapshotExists: false);
        using (h)
        {
            IActionResult result = await h.Controller(moderation: moderation).GetPreviewContent(h.ProjectId, h.TimelineId, job, default);

            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409, "what it was rendered from cannot be checked, so it is not served");
        }
    }

    [Fact]
    public async Task The_sweeper_deletes_expired_files_then_the_job_once_the_rate_window_has_passed()
    {
        using Harness h = await Harness.CreateAsync();
        DateTime now = DateTime.UtcNow;
        Guid fresh = await h.AddPreviewAsync(output: "k/fresh.mp4", createdAt: now.AddMinutes(-10));
        Guid stale = await h.AddPreviewAsync(output: "k/stale.mp4", createdAt: now.AddMinutes(-90));
        Guid mid = await h.AddPreviewAsync(output: "k/mid.mp4", createdAt: now.AddMinutes(-30));
        Guid delivery = await h.AddPreviewAsync(output: "k/final.mp4", createdAt: now.AddDays(-5), kind: null, name: "Render: Cut");
        List<string> deleted = [];
        h.Storage.Setup(s => s.DeleteManyAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyCollection<string>, CancellationToken>((_, keys, _) => deleted.AddRange(keys))
            .ReturnsAsync((Guid _, IReadOnlyCollection<string> keys, CancellationToken _) => keys.Count);

        (int files, int workflows) = await TimelinePreviewSweeper.SweepOnceAsync(h.Db, h.Storage.Object, now, TimeSpan.FromMinutes(20), default);

        deleted.Should().BeEquivalentTo("k/stale.mp4", "k/mid.mp4");
        files.Should().Be(2);
        workflows.Should().Be(1, "only the 90 minute old job is also past the one-hour rate window");
        h.Db.WorkflowExecutions.IgnoreQueryFilters().Select(e => e.Id).ToList().Should().Contain([fresh, mid, delivery]).And.NotContain(stale);
        (await TimelinePreviewSweeper.SweepOnceAsync(h.Db, h.Storage.Object, now, TimeSpan.FromMinutes(20), default)).Workflows.Should().Be(0);
    }

    private sealed class Harness : IDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid TimelineId { get; init; }
        public Guid OwnerId { get; init; }
        public int MaxPerHour { get; init; } = 30;
        public Mock<IWorkflowExecutionService> Executions { get; } = new();
        public Mock<IFileStorageService> Storage { get; } = new();
        public PriceRequest? LastPrice { get; private set; }
        private readonly Mock<INonWorkflowChargeService> _charges = new();

        public static async Task<Harness> CreateAsync(long balanceMillis = 0, int maxPerHour = 30)
        {
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"preview-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            InferenceApiDbContext db = new(options);
            Guid ownerId = Guid.NewGuid();
            Guid orgId = TestTenancy.OrgOf(ownerId);
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "P",
                OwnerId = ownerId,
                OrganizationId = orgId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Projects.Add(project);
            Guid timelineId = Guid.NewGuid();
            EditTimeline document = new(
                id: timelineId,
                projectId: project.Id,
                name: "Cut",
                settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
                tracks:
                [
                    new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null,
                        [new MediaClip(Guid.NewGuid(), "Clip", 0, 300, new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: Guid.NewGuid()))])
                ],
                markers: []);
            db.EditTimelines.Add(new EditTimelineEntity
            {
                Id = timelineId,
                ProjectId = project.Id,
                Name = "Cut",
                Revision = 1,
                DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true, SystemPrompt = string.Empty });
            db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = orgId, BalanceMillis = balanceMillis });
            await db.SaveChangesAsync();
            return new Harness { Db = db, ProjectId = project.Id, TimelineId = timelineId, OwnerId = ownerId, MaxPerHour = maxPerHour };
        }

        public void SetPrice(long millis) =>
            _charges.Setup(c => c.EstimateAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PriceRequest, CancellationToken>((r, _) => LastPrice = r)
                .ReturnsAsync(millis);

        public void StoreFile(string key, byte[] bytes)
        {
            Storage.Setup(s => s.GetMetadataAsync(ProjectId, key, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StoredObjectMetadata(bytes.Length, "video/mp4"));
            Storage.Setup(s => s.DownloadAsync(ProjectId, key, It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult<Stream>(new MemoryStream(bytes)));
        }

        public EditTimelinesController Controller(bool billing = false, bool viewOnly = false, ReelBolt.Inference.Api.Services.Moderation.PublishModeration? moderation = null)
        {
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(OwnerId);
            user.SetupGet(u => u.Email).Returns("t@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(OwnerId));
            user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
            user.SetupGet(u => u.EmailVerified).Returns(true);

            IProjectAccessService real = TestTenancy.Access(Db, user.Object);
            IProjectAccessService access = real;
            if (viewOnly)
            {
                Mock<IProjectAccessService> view = new();
                view.Setup(a => a.GetAsync(It.IsAny<Guid>(), It.IsAny<ProjectPermission>(), It.IsAny<CancellationToken>()))
                    .Returns<Guid, ProjectPermission, CancellationToken>((id, permission, ct) => permission == ProjectPermission.View
                        ? real.GetAsync(id, permission, ct)
                        : Task.FromResult(new ProjectAccessResult(ProjectAccessOutcome.Forbidden, null)));
                access = view.Object;
            }

            Mock<IWorkflowCostEstimator> estimator = new();
            estimator.SetupGet(e => e.BillingEnabled).Returns(billing);
            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.FromResult(new OrganizationEntitlementSet { MaxOutputHeight = int.MaxValue }));

            return new EditTimelinesController(
                Db, user.Object, access, Storage.Object, Executions.Object,
                _charges.Object, null, entitlements.Object, null, moderation, costEstimator: estimator.Object,
                previewOptions: Options.Create(new TimelinePreviewOptions { MaxPerHourPerUserProject = MaxPerHour }));
        }

        /// <summary>Seeds a hidden render job (a preview unless <paramref name="kind"/> is null) and returns its execution id.</summary>
        public async Task<Guid> AddPreviewAsync(
            ExecutionStatus status = ExecutionStatus.Passed, string? output = null, DateTime? queuedAt = null, DateTime? createdAt = null,
            string? kind = "preview", string? error = null, double? durationSec = null, string? name = null, int? revision = null)
        {
            Guid wf = Guid.NewGuid();
            Guid step = Guid.NewGuid();
            Guid execution = Guid.NewGuid();
            DateTime created = createdAt ?? queuedAt ?? DateTime.UtcNow;
            TimelineRenderStepConfig config = new(TimelineId, Preset: kind is null ? TimelineRenderPreset.Hd1080p16x9 : TimelineRenderPreset.Preview,
                OutputFileName: kind is null ? "final.mp4" : "preview-abc.mp4", RegisterProjectFile: kind is null, AudioJobKind: kind, Revision: revision);
            WorkflowDefinition w = new()
            {
                Id = wf,
                ProjectId = ProjectId,
                Name = name ?? (kind is null ? "Render: Cut" : "Preview: Cut"),
                Kind = WorkflowDefinitionKind.TimelineRender,
                CreatedAt = created,
                UpdatedAt = created
            };
            w.Steps.Add(new WorkflowStep
            {
                Id = step,
                WorkflowDefinitionId = wf,
                AgentDefinitionId = Db.AgentDefinitions.First().Id,
                StepOrder = 1,
                Label = "r",
                StepType = StepType.TimelineRender,
                TimelineRenderConfigJson = JsonSerializer.Serialize(config, Web)
            });
            Db.WorkflowDefinitions.Add(w);
            WorkflowExecution e = new()
            {
                Id = execution,
                ProjectId = ProjectId,
                WorkflowDefinitionId = wf,
                Status = status,
                InitiatedByUserId = OwnerId,
                QueuedAt = queuedAt ?? created,
                StartedAt = queuedAt ?? created,
                ErrorMessage = error
            };
            if (output is not null || durationSec is not null)
            {
                e.StepResults.Add(new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = execution,
                    WorkflowStepId = step,
                    Status = StepStatus.Completed,
                    OutputStorageKey = output,
                    OutputJson = durationSec is null ? null : $$"""{"durationSec":{{durationSec}}}"""
                });
            }

            Db.WorkflowExecutions.Add(e);
            await Db.SaveChangesAsync();
            return execution;
        }

        public void Dispose() => Db.Dispose();
    }
}
