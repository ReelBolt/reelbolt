#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
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
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Builds;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Tests.Billing;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using Rig = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarBuildRig;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Lane L12 of docs/design/avatar-objects.md, part 2: the hidden <see cref="WorkflowDefinitionKind.AvatarBuild"/> kind,
/// a variant's videos (grab the first frame, change it like a picture, make the video again from it) and pulling an
/// object out of a video (stills, the segmentation model's box, the frames cut at full size). The engine is a test
/// double: a queued run is a Queued execution row a test ends with <see cref="Rig.FinishRun"/>.
/// </summary>
public class AvatarBuildPipelinesTests
{
    // ------------------------------------------------------------------ the hidden kind

    [Fact]
    public void The_kind_is_ten()
    {
        ((int)WorkflowDefinitionKind.AvatarBuild).Should().Be(10);
    }

    [Fact]
    public async Task The_hidden_kind_is_not_listed_deleted_retried_or_resumed_over_REST()
    {
        Rig rig = new();
        Guid workflowId = Guid.NewGuid(), executionId = Guid.NewGuid();
        using (InferenceApiDbContext db = rig.H.NewDb())
        {
            db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = workflowId,
                ProjectId = rig.H.Project.Id,
                Name = "Object frames: Kettle",
                Kind = WorkflowDefinitionKind.AvatarBuild,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = workflowId,
                ProjectId = rig.H.Project.Id,
                OrganizationId = rig.H.OrganizationId,
                Status = ExecutionStatus.Failed,
                CorrelationId = "c"
            });
            await db.SaveChangesAsync();
        }

        await using InferenceApiDbContext context = rig.H.NewDb();
        ICurrentUser user = rig.H.Owner();
        var workflows = new WorkflowsController(
            context, user, TestTenancy.Access(context, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(context, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            Mock.Of<IWorkflowExecutionService>(), NullLogger<WorkflowsController>.Instance);

        ActionResult<List<WorkflowDefinitionResponse>> list = await workflows.List(rig.H.Project.Id, default);
        list.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<WorkflowDefinitionResponse>>().Which.Should().BeEmpty();
        (await workflows.Delete(rig.H.Project.Id, workflowId, default)).Should().BeOfType<NotFoundResult>();

        ConflictObjectResult resume = (await workflows.ResumeExecution(rig.H.Project.Id, workflowId, executionId, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(resume.Value).Should().Contain("AVATAR_BUILD_RESUME_NOT_SUPPORTED");
        ConflictObjectResult retry = (await workflows.RetryExecution(rig.H.Project.Id, workflowId, executionId, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(retry.Value).Should().Contain("AVATAR_BUILD_RETRY_NOT_SUPPORTED");

        using InferenceApiDbContext check = rig.H.NewDb();
        (await check.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Failed, "a refusal touches nothing");
        (await check.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.Standard)).Should().Be(0,
            "every assistant, MCP, dashboard and apps path reads Kind == Standard");
    }

    [Fact]
    public async Task The_orphan_reaper_takes_old_avatar_build_workflows_that_never_ran()
    {
        DateTime now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid project = Guid.NewGuid();
        WorkflowDefinition W(DateTime at) => new() { Id = Guid.NewGuid(), ProjectId = project, Name = "w", Kind = WorkflowDefinitionKind.AvatarBuild, CreatedAt = at, UpdatedAt = at };
        WorkflowDefinition orphan = W(now.AddHours(-2)), young = W(now.AddMinutes(-30)), ran = W(now.AddDays(-2));
        db.WorkflowDefinitions.AddRange(orphan, young, ran);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = ran.Id, ProjectId = project, Status = ExecutionStatus.Passed, CorrelationId = "c" });
        await db.SaveChangesAsync();

        List<Guid> selected = await TimelineRenderOrphanReaper.SelectOrphans(db.WorkflowDefinitions, now - TimeSpan.FromHours(1)).Select(w => w.Id).ToListAsync();

        selected.Should().Equal(orphan.Id);
    }

    // ------------------------------------------------------------------ a variant's videos: the request

    private static ResolvedVideoGenerationProvider MiniMax() =>
        new(Guid.NewGuid(), "minimax", InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", "key", 60);

    /// <summary>A rig with a video model that starts from a picture, and an object "Kettle" with one picture and <paramref name="videos"/> videos.</summary>
    private static async Task<(Rig Rig, Guid Kettle, ProjectFile Front, List<ProjectFile> Clips)> KettleAsync(int videos = 1, Action<Rig>? arrange = null)
    {
        Rig rig = new() { VideoClients = new Rig.FakeVideoClients() };
        rig.VideoProvider = MiniMax();
        arrange?.Invoke(rig);
        rig.GrantMediaConsent();
        ProjectFile front = rig.H.AddImage("kettle.png", salt: 3);
        List<ProjectFile> clips = [];
        for (int i = 0; i < videos; i++)
        {
            ProjectFile clip = rig.H.AddProbedMedia($"kettle-{i}.mp4", "video/mp4", 5, "h264");
            rig.H.AddFilmstrip(clip, salt: (byte)(20 + i));
            clips.Add(clip);
        }

        Guid kettle = await rig.H.CreateObjectAsync("Kettle", [front, .. clips]);
        return (rig, kettle, front, clips);
    }

    private static async Task<(int Status, JsonElement Body)> StartAsync(Rig rig, Guid avatarId, CreateAvatarVariantRequest body) =>
        H.Read(await rig.Controller(path: $"/api/v1/projects/{rig.H.Project.Id}/avatars/{avatarId}/variants")
            .CreateVariant(rig.H.Project.Id, avatarId, body, CancellationToken.None));

    private static async Task<(int Status, JsonElement Body)> EstimateAsync(Rig rig, Guid avatarId, CreateAvatarVariantRequest body) =>
        H.Read(await rig.Controller().EstimateVariant(rig.H.Project.Id, avatarId, body, CancellationToken.None));

    [Fact]
    public async Task The_estimate_adds_the_videos_as_up_to_and_holds_their_frames_with_the_pictures()
    {
        var prices = new Mock<IModelPriceService>();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuote(30_000, 0.3m, null));
        (Rig rig, Guid kettle, _, _) = await KettleAsync(arrange: r => r.Prices = prices.Object);

        (int status, JsonElement estimate) = await EstimateAsync(rig, kettle, new CreateAvatarVariantRequest(IncludeVideos: true));

        status.Should().Be(200, estimate.ToString());
        estimate.GetProperty("pictures").GetInt32().Should().Be(1);
        estimate.GetProperty("videos").GetInt32().Should().Be(1);
        // One picture plus the video's first frame, both changed by the picture model and held; the take is "up to".
        estimate.GetProperty("credits").GetDouble().Should().Be(8);
        estimate.GetProperty("creditsHigh").GetDouble().Should().Be(38);
        estimate.GetProperty("unavailableReason").ValueKind.Should().Be(JsonValueKind.Null);

        (int without, JsonElement plain) = await EstimateAsync(rig, kettle, new CreateAvatarVariantRequest());
        without.Should().Be(200);
        (plain.GetProperty("videos").GetInt32(), plain.GetProperty("creditsHigh").GetDouble()).Should().Be((0, 4));
    }

    [Fact]
    public async Task Videos_need_the_plan_feature_and_a_video_model_that_starts_from_a_picture()
    {
        (Rig rig, Guid kettle, _, _) = await KettleAsync();
        CreateAvatarVariantRequest body = Rig.Body() with { IncludeVideos = true };

        rig.H.Entitlements.Set = new ReelBolt.Shared.Tenancy.OrganizationEntitlementSet { AvatarsAllowed = true, AvatarObjectsAllowed = true, VideoGenAllowed = false };
        (int status, JsonElement refused) = await StartAsync(rig, kettle, body);
        (status, refused.GetProperty("code").GetString(), refused.GetProperty("feature").GetString())
            .Should().Be((402, "PLAN_NOT_ENTITLED", "video_generation"));

        rig.H.Entitlements.Set = new ReelBolt.Shared.Tenancy.OrganizationEntitlementSet { AvatarsAllowed = true, AvatarObjectsAllowed = true };
        rig.VideoClients!.Support = VideoImageInputSupport.None;
        (status, refused) = await StartAsync(rig, kettle, body);
        (status, refused.GetProperty("code").GetString()).Should().Be((409, "VARIANT_VIDEO_MODEL_MISSING"));
        refused.GetProperty("message").GetString().Should().Contain("MiniMax-H3");

        rig.VideoProvider = null;
        (status, refused) = await StartAsync(rig, kettle, body);
        (status, refused.GetProperty("code").GetString()).Should().Be((409, "VARIANT_VIDEO_MODEL_MISSING"));

        rig.Charges!.Holds.Should().BeEmpty("nothing is held before every check passed");
        rig.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Videos_on_a_default_model_budgeted_in_GPU_time_are_refused_up_front_never_left_out_later()
    {
        // A self-hosted ComfyUI image-to-video graph starts from a picture, but its take needs a GPU-minute cap that a
        // variant's take does not carry: before this check the run's step failed its own validation and the video was
        // silently skipped.
        (Rig rig, Guid kettle, _, _) = await KettleAsync(arrange: r =>
            r.VideoProvider = new ResolvedVideoGenerationProvider(Guid.NewGuid(), "comfy", InferenceProviderKind.ComfyUI, "http://comfyui:8188", "wan2.2-ti2v-5b-i2v", "", 60));
        rig.VideoClients!.Capabilities = new VideoGenerationCapabilities(
            new Dictionary<string, decimal> { ["480P"] = 20m, ["720P"] = 45m }, 1, 5, ["16:9", "9:16", "1:1"]);

        (int estimated, JsonElement estimate) = await EstimateAsync(rig, kettle, new CreateAvatarVariantRequest(IncludeVideos: true));
        estimated.Should().Be(200, estimate.ToString());
        estimate.GetProperty("unavailableReason").GetString().Should().Be("VARIANT_VIDEO_MODEL_UNPRICED");

        (int status, JsonElement refused) = await StartAsync(rig, kettle, Rig.Body() with { IncludeVideos = true });
        (status, refused.GetProperty("code").GetString()).Should().Be((409, "VARIANT_VIDEO_MODEL_UNPRICED"));
        refused.GetProperty("message").GetString().Should().Contain("wan2.2-ti2v-5b-i2v").And.Contain("GPU time");
        rig.Charges!.Holds.Should().BeEmpty("nothing is held before every check passed");
        rig.Queued.Should().BeEmpty();

        // Without the videos the variant is made from its pictures alone.
        (await StartAsync(rig, kettle, Rig.Body())).Status.Should().Be(202);
    }

    [Fact]
    public async Task Videos_on_a_default_model_with_no_price_are_refused_with_their_own_code()
    {
        var prices = new Mock<IModelPriceService>();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no price row"));
        (Rig rig, Guid kettle, _, _) = await KettleAsync(arrange: r => r.Prices = prices.Object);

        (int status, JsonElement refused) = await StartAsync(rig, kettle, Rig.Body() with { IncludeVideos = true });

        (status, refused.GetProperty("code").GetString()).Should().Be((409, "VARIANT_VIDEO_MODEL_UNPRICED"));
        refused.GetProperty("message").GetString().Should().Contain("no price set up");
        rig.Charges!.Holds.Should().BeEmpty();
        rig.Queued.Should().BeEmpty();
    }

    [Fact]
    public void A_take_is_the_originals_length_clamped_to_the_model_in_the_nearest_frame_shape()
    {
        var caps = new VideoGenerationCapabilities(new Dictionary<string, decimal> { ["768P"] = 0.05m }, 6, 10, ["16:9", "9:16", "1:1"]);

        AvatarBuildService.TakeSeconds(5_000, caps).Should().Be(6, "shorter than the model makes: its shortest");
        AvatarBuildService.TakeSeconds(8_400, caps).Should().Be(8);
        AvatarBuildService.TakeSeconds(30_000, caps).Should().Be(10, "longer than the model makes: its longest");
        AvatarBuildService.TakeSeconds(null, caps).Should().Be(6, "an unknown length: the step's own default");

        AvatarBuildService.NearestRatio(1280, 720, caps.AllowedRatios).Should().Be("16:9");
        AvatarBuildService.NearestRatio(1080, 1920, caps.AllowedRatios).Should().Be("9:16");
        AvatarBuildService.NearestRatio(1000, 1100, caps.AllowedRatios).Should().Be("1:1");
        AvatarBuildService.NearestRatio(null, null, caps.AllowedRatios).Should().Be("16:9");
    }

    // ------------------------------------------------------------------ a variant's videos: the job

    private static ProjectFile FileOf(Rig rig, Guid fileId)
    {
        using InferenceApiDbContext db = rig.H.NewDb();
        return db.ProjectFiles.Single(f => f.Id == fileId);
    }

    /// <summary>The ingest probe and filmstrip of a new video, the way the engine's derivative run would add them.</summary>
    private static void Ingest(Rig rig, ProjectFile video)
    {
        using (InferenceApiDbContext db = rig.H.NewDb())
        {
            db.MediaDerivatives.Add(new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = rig.H.Project.Id,
                SourceStorageKey = video.StorageKey,
                SourceProbeJson = new MediaSourceProbe(6, 30, 1, 1280, 720, "h264", "aac", "mp4", 2, 48000, 1_000_000, true).ToJson(),
                CreatedAt = DateTime.UtcNow
            });
            db.SaveChanges();
        }

        rig.H.AddFilmstrip(video, salt: 77);
    }

    private static void Tick(Rig rig, int seconds = 31) => rig.Time.Now = rig.Time.Now.AddSeconds(seconds);

    [Fact]
    public async Task A_variants_video_is_grabbed_changed_and_made_again_in_hidden_runs_then_joins_the_variant_as_motion()
    {
        (Rig rig, Guid kettle, _, List<ProjectFile> clips) = await KettleAsync();
        (int started, JsonElement accepted) = await StartAsync(rig, kettle, Rig.Body() with { IncludeVideos = true });
        started.Should().Be(202, accepted.ToString());
        Guid jobId = accepted.GetProperty("jobId").GetGuid();
        rig.Job(jobId).Total.Should().Be(2, "one picture and one video");

        // ---- The picture, then the grab of the video's first frame in a hidden run. ----
        await rig.RunAsync(jobId);
        AvatarBuildJob waiting = rig.Job(jobId);
        (waiting.State, waiting.Done).Should().Be(("videos", 1));
        rig.Queued.Should().ContainSingle().Which.Kind.Should().Be(WorkflowDefinitionKind.AvatarBuild);
        waiting.ExecutionId.Should().Be(rig.Queued[0].ExecutionId);
        WorkflowStep grab = rig.StepsOf(rig.Queued[0].WorkflowId).Should().ContainSingle().Subject;
        grab.StepType.Should().Be(StepType.ExtractFrame);
        ExtractFrameStepConfig grabConfig = JsonSerializer.Deserialize<ExtractFrameStepConfig>(grab.ExtractFrameConfigJson!, ExtractFrameStepConfig.JsonOptions)!;
        (grabConfig.At, grabConfig.Source.Kind, grabConfig.Source.ProjectFileId, grabConfig.RegisterProjectFile)
            .Should().Be((FrameAt.First, VideoSourceKind.ProjectFile, (Guid?)clips[0].Id, false));
        grabConfig.Validate().Should().BeEmpty();

        // Not finished: the job only looks again.
        Tick(rig);
        await rig.RunAsync(jobId);
        rig.Queued.Should().HaveCount(1);

        // ---- The frame is changed like a picture, the first finished picture going along; then the take. ----
        rig.FinishRun(rig.Queued[0].ExecutionId, ExecutionStatus.Passed, null, StepStatus.Completed);
        Tick(rig);
        await rig.RunAsync(jobId);

        rig.Images.Edits.Should().HaveCount(2);
        rig.Images.Edits[1].ReferenceImageUrls.Should().Equal(["data:image/png;base64," + Convert.ToBase64String(H.Png(1024, 1024, 201))],
            "the frame gets the same change as the picture");
        rig.Queued.Should().HaveCount(2);
        WorkflowStep take = rig.StepsOf(rig.Queued[1].WorkflowId).Should().ContainSingle().Subject;
        take.StepType.Should().Be(StepType.VideoGenerate);
        VideoGenerateStepConfig takeConfig = JsonSerializer.Deserialize<VideoGenerateStepConfig>(take.VideoGenerateConfigJson!, VideoGenerateStepConfig.JsonOptions)!;
        takeConfig.Validate().Should().BeEmpty();
        takeConfig.PlanSource.Should().Be(VideoGeneratePlanSource.Inline);
        (takeConfig.Model, takeConfig.Resolution, takeConfig.Ratio, takeConfig.DurationSeconds, takeConfig.Takes, takeConfig.MaxClips)
            .Should().Be(("MiniMax-H3", "768P", "16:9", 6, 1, 1));
        takeConfig.ProviderId.Should().Be(rig.VideoProvider!.ProviderId);
        takeConfig.AllowSourceMediaEgress.Should().BeTrue("set by the server because the media permission was established");
        takeConfig.MaxSpendCredits.Should().Be(50m, "6 s at $0.05 a second is $0.30, 50 credits");
        takeConfig.InlinePrompt.Should().StartWith(AvatarVariantInstruction.VideoLead).And.EndWith("\"paint it matte red\"");
        take.RegisterOutputAsProjectFile.Should().BeFalse("the job keeps the take as the variant's own file");
        ProjectFile frame = FileOf(rig, takeConfig.FirstFrameProjectFileId!.Value);
        (frame.OriginalPath, frame.SourceKind).Should().Be(("avatars/Kettle/Red/Red-video-1-frame.png", "avatar_variant"));

        // ---- The take arrives, becomes the variant's file, and the variant waits for its ingest. ----
        rig.FinishRun(rig.Queued[1].ExecutionId, ExecutionStatus.Passed, _ => [9, 8, 7, 6], StepStatus.Completed);
        Tick(rig);
        await rig.RunAsync(jobId);
        AvatarBuildJob creating = rig.Job(jobId);
        (creating.State, creating.Done).Should().Be(("creating", 2));
        ProjectFile video;
        using (InferenceApiDbContext db = rig.H.NewDb())
            video = db.ProjectFiles.Single(f => f.OriginalPath == "avatars/Kettle/Red/Red-video-1.mp4");
        (video.SourceKind, video.MimeType, video.Category).Should().Be(("avatar_variant", "video/mp4", "userFiles"));
        rig.H.Objects[video.StorageKey].Should().Equal(9, 8, 7, 6);

        Ingest(rig, video);
        Tick(rig, 11);
        await rig.RunAsync(jobId);

        AvatarBuildJob ready = rig.Job(jobId);
        (ready.State, ready.FailureCode).Should().Be(("ready", null));
        (int status, JsonElement variant) = H.Read(await rig.H.Controller().Get(rig.H.Project.Id, ready.AvatarId!.Value, CancellationToken.None));
        status.Should().Be(200);
        variant.GetProperty("variantOfAvatarId").GetGuid().Should().Be(kettle);
        variant.GetProperty("references").EnumerateArray().Select(r => r.GetProperty("role").GetString()).Should().Equal("Front", "Motion");
        variant.GetProperty("selectable").GetBoolean().Should().BeTrue();
        // The frame edit was part of the job's own hold; the take was the engine's.
        rig.Charges!.Holds.Should().ContainSingle().Which.EstimateMillis.Should().Be(8_000);
        rig.Charges.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(8_000);
    }

    [Fact]
    public async Task A_video_that_fails_is_skipped_the_rest_are_run_again_and_the_variant_is_still_made()
    {
        (Rig rig, Guid kettle, _, List<ProjectFile> clips) = await KettleAsync(videos: 2);
        Guid jobId = (await StartAsync(rig, kettle, Rig.Body() with { IncludeVideos = true })).Body.GetProperty("jobId").GetGuid();

        await rig.RunAsync(jobId);
        rig.StepsOf(rig.Queued[0].WorkflowId).Should().HaveCount(2, "one grab per video, in one run");
        rig.FinishRun(rig.Queued[0].ExecutionId, ExecutionStatus.Passed, null, StepStatus.Completed, StepStatus.Completed);
        Tick(rig);
        await rig.RunAsync(jobId);
        rig.StepsOf(rig.Queued[1].WorkflowId).Should().HaveCount(2, "one take per video, in one run");

        // The engine stops a run at its first failed step: the first take failed, the second was never reached.
        rig.FinishRun(rig.Queued[1].ExecutionId, ExecutionStatus.Failed, null, StepStatus.Failed);
        Tick(rig);
        await rig.RunAsync(jobId);

        rig.Queued.Should().HaveCount(3);
        WorkflowStep again = rig.StepsOf(rig.Queued[2].WorkflowId).Should().ContainSingle("only the video the failed run never reached").Subject;
        JsonSerializer.Deserialize<VideoGenerateStepConfig>(again.VideoGenerateConfigJson!, VideoGenerateStepConfig.JsonOptions)!
            .FirstFrameProjectFileId.Should().Be(AvatarBuildParams.FileIdFor(jobId, AvatarBuildItem.FrameKey(ReferenceOf(rig, kettle, clips[1]))));

        rig.FinishRun(rig.Queued[2].ExecutionId, ExecutionStatus.Passed, _ => [5, 5, 5], StepStatus.Completed);
        Tick(rig);
        await rig.RunAsync(jobId);
        ProjectFile made;
        using (InferenceApiDbContext db = rig.H.NewDb())
            made = db.ProjectFiles.Single(f => f.SourceKind == "avatar_variant" && f.MimeType == "video/mp4");
        made.OriginalPath.Should().Be("avatars/Kettle/Red/Red-video-2.mp4");
        Ingest(rig, made);
        Tick(rig, 11);
        await rig.RunAsync(jobId);

        AvatarBuildJob ready = rig.Job(jobId);
        ready.State.Should().Be("ready");
        ready.Message.Should().Be("1 of 2 videos could not be made again; the variant has the others.");
        using InferenceApiDbContext check = rig.H.NewDb();
        check.ProjectAvatarReferences.Where(r => r.AvatarId == ready.AvatarId).Select(r => r.Role).ToList()
            .Should().BeEquivalentTo([AvatarReferenceRole.Front, AvatarReferenceRole.Motion]);
    }

    [Fact]
    public async Task No_video_made_still_makes_the_variant_from_its_pictures()
    {
        (Rig rig, Guid kettle, _, _) = await KettleAsync();
        Guid jobId = (await StartAsync(rig, kettle, Rig.Body() with { IncludeVideos = true })).Body.GetProperty("jobId").GetGuid();
        await rig.RunAsync(jobId);

        // The grab itself fails: there is no frame to change, so no take is queued.
        rig.FinishRun(rig.Queued[0].ExecutionId, ExecutionStatus.Failed, null, StepStatus.Failed);
        Tick(rig);
        await rig.RunAsync(jobId);

        AvatarBuildJob ready = rig.Job(jobId);
        (ready.State, ready.Done).Should().Be(("ready", 1));
        ready.Message.Should().Be("The videos could not be made again, so the variant was made from its pictures.");
        rig.Queued.Should().HaveCount(1);
        rig.Images.Edits.Should().HaveCount(1, "no frame was there to change");
    }

    [Fact]
    public async Task A_run_the_plans_concurrency_cap_refuses_is_asked_for_again_later_and_leaves_no_workflow()
    {
        (Rig rig, Guid kettle, _, _) = await KettleAsync();
        Guid jobId = (await StartAsync(rig, kettle, Rig.Body() with { IncludeVideos = true })).Body.GetProperty("jobId").GetGuid();
        rig.RefuseQueues = 1;

        await rig.RunAsync(jobId);

        rig.Queued.Should().BeEmpty();
        AvatarBuildParams notes = AvatarBuildParams.Read(rig.Job(jobId).ParamsJson);
        notes.RetryAt.Should().Be(rig.Time.Now.AddSeconds(30));
        using (InferenceApiDbContext db = rig.H.NewDb())
            (await db.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.AvatarBuild)).Should().Be(0);

        Tick(rig);
        await rig.RunAsync(jobId);
        rig.Queued.Should().ContainSingle().Which.ExecutionId.Should().Be(notes.Runs.Single().ExecutionId, "the same run, asked for again");
    }

    [Fact]
    public async Task A_cancel_while_the_takes_run_stops_them_and_settles_what_was_made()
    {
        (Rig rig, Guid kettle, _, _) = await KettleAsync();
        Guid jobId = (await StartAsync(rig, kettle, Rig.Body() with { IncludeVideos = true })).Body.GetProperty("jobId").GetGuid();
        await rig.RunAsync(jobId);
        rig.FinishRun(rig.Queued[0].ExecutionId, ExecutionStatus.Passed, null, StepStatus.Completed);
        Tick(rig);
        await rig.RunAsync(jobId);
        Guid takes = rig.Queued[1].ExecutionId;

        H.Read(await rig.Controller().CancelBuild(rig.H.Project.Id, jobId, CancellationToken.None)).Status.Should().Be(200);
        await rig.RunAsync(jobId);

        rig.Job(jobId).State.Should().Be("cancelled");
        rig.Publish.Verify(p => p.Publish(It.Is<WorkflowExecutionStopRequested>(m => m.ExecutionId == takes), It.IsAny<CancellationToken>()), Times.Once);
        rig.Charges!.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(8_000, "the picture and the frame were made");
    }

    private static Guid ReferenceOf(Rig rig, Guid avatarId, ProjectFile file)
    {
        using InferenceApiDbContext db = rig.H.NewDb();
        return db.ProjectAvatarReferences.Single(r => r.AvatarId == avatarId && r.ProjectFileId == file.Id).Id;
    }

    // ------------------------------------------------------------------ the crop

    [Fact]
    public void The_crop_is_the_box_plus_a_twelve_percent_margin_grown_to_256_pixels_and_kept_in_the_frame()
    {
        // A 320 x 180 box in the middle of a 1280 x 720 frame: 12 % on each side, then 256 px tall at least.
        NormalizedCrop middle = ExtractionCrop.Compute(new NormalizedBox(0.375, 0.375, 0.625, 0.625), 1280, 720)!;
        middle.W.Should().BeApproximately(320 * 1.24 / 1280, 1e-5);
        middle.H.Should().BeApproximately(256.0 / 720, 1e-5);
        middle.X.Should().BeApproximately((640 - 320 * 1.24 / 2) / 1280, 1e-5);
        middle.Y.Should().BeApproximately((360 - 128.0) / 720, 1e-5);

        // In a corner: moved inside the frame, never past its edges.
        NormalizedCrop corner = ExtractionCrop.Compute(new NormalizedBox(0.9, 0.9, 1.0, 1.0), 1280, 720)!;
        (corner.X + corner.W).Should().BeLessThanOrEqualTo(1.0);
        (corner.Y + corner.H).Should().BeLessThanOrEqualTo(1.0);
        corner.W.Should().BeApproximately(256.0 / 1280, 1e-5);
        corner.H.Should().BeApproximately(256.0 / 720, 1e-5);
        corner.X.Should().BeApproximately(1 - 256.0 / 1280, 1e-5);

        // A frame smaller than the minimum: the whole side, no more.
        NormalizedCrop small = ExtractionCrop.Compute(new NormalizedBox(0.4, 0.4, 0.6, 0.6), 200, 150)!;
        (small.X, small.Y, small.W, small.H).Should().Be((0, 0, 1, 1));

        // A box bigger than the frame once the margin is added is clamped to the frame.
        NormalizedCrop large = ExtractionCrop.Compute(new NormalizedBox(0.02, 0.0, 0.98, 1.0), 1920, 1080)!;
        (large.X, large.W, large.Y, large.H).Should().Be((0, 1, 0, 1));

        // No area, or no size: no crop (the whole frame is used).
        ExtractionCrop.Compute(new NormalizedBox(0.5, 0.5, 0.5, 0.7), 1280, 720).Should().BeNull();
        ExtractionCrop.Compute(new NormalizedBox(0.1, 0.1, 0.2, 0.2), 0, 0).Should().BeNull();

        // Every crop is one ExtractFrame accepts.
        foreach (NormalizedCrop crop in new[] { middle, corner, small, large })
            new ExtractFrameStepConfig(new VideoSourceRef(VideoSourceKind.ProjectFile, Guid.NewGuid()), FrameAt.Time, 1, Crop: crop).Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_vendor_box_is_read_from_its_centre_or_from_the_mask_it_came_with()
    {
        NormalizedBox fromCentre = ExtractionCrop.FromCentre([0.5, 0.5, 0.2, 0.4])!;
        new[] { fromCentre.XMin, fromCentre.YMin, fromCentre.XMax, fromCentre.YMax }
            .Zip(new[] { 0.4, 0.3, 0.6, 0.7 }).Should().OnlyContain(p => Math.Abs(p.First - p.Second) < 1e-9);
        ExtractionCrop.FromCentre([0.5, 0.5, 0, 0.4]).Should().BeNull();
        ExtractionCrop.FromCentre(null).Should().BeNull();

        using var bitmap = new SkiaSharp.SKBitmap(100, 50);
        bitmap.Erase(SkiaSharp.SKColors.Black);
        for (int y = 10; y < 20; y++)
            for (int x = 30; x < 70; x++)
                bitmap.SetPixel(x, y, SkiaSharp.SKColors.White);
        using SkiaSharp.SKData png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        ExtractionCrop.FromMask(png.ToArray()).Should().Be(new NormalizedBox(0.3, 0.2, 0.7, 0.4));
        ExtractionCrop.FromMask([1, 2, 3]).Should().BeNull();
    }

    [Fact]
    public void Up_to_six_stills_are_taken_evenly_from_the_set()
    {
        List<OutputStillObject> twelve = [.. Enumerable.Range(0, 12).Select(i => new OutputStillObject(i, "k" + i))];
        AvatarBuildProcessor.ChooseStills(twelve, 6).Select(s => s.AtSec).Should().Equal(1, 3, 5, 7, 9, 11);
        AvatarBuildProcessor.ChooseStills(twelve.Take(4).Reverse().ToList(), 6).Select(s => s.AtSec).Should().Equal(0, 1, 2, 3);
    }

    // ------------------------------------------------------------------ pulling an object out of a video: the request

    private static ResolvedImageAnalysisProvider Sam() =>
        new(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation,
            "https://queue.fal.run", "fal-ai/sam-3-1/image", "key", 60);

    /// <summary>A rig with a 12 s 1280 x 720 video the stills path knows; <paramref name="segmentation"/> sets up a segmentation model.</summary>
    private static (Rig Rig, ProjectFile Clip) VideoRig(bool segmentation = true, bool consent = true)
    {
        Rig rig = new()
        {
            Charges = new FakeNonWorkflowCharges
            {
                AvailableMillis = 1_000_000,
                EstimateMillis = 4_000,
                EstimateBy = r => r.Unit == UsageKind.SegmentationImages ? 1_000 : 4_000
            },
            Describer = new Rig.IOutputDescriberDouble()
        };
        if (segmentation)
        {
            rig.SegmentationProvider = Sam();
            rig.SegmenterClients = new Rig.FakeSegmenter();
            rig.Presigned = new Mock<ReelBolt.Shared.Storage.IPresignedUrlService>();
            rig.Presigned.Setup(p => p.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync((Guid _, string key, TimeSpan _) => "https://signed.example/" + key);
        }

        if (consent) rig.GrantMediaConsent();
        ProjectFile clip = rig.H.AddProbedMedia("kettle-shot.mp4", "video/mp4", 12, "h264");
        rig.Describer!.Sources[clip.Id] = new OutputSource(
            clip.Id, "file", clip.OriginalFileName, "video/mp4", clip.SizeBytes, clip.StorageKey,
            new MediaSourceRef(MediaSourceKind.ProjectFile, clip.Id), clip.Id, 12.0);
        rig.Describer.MakeOnRequest = times =>
        [
            .. (times.Count == 0 ? [2000, 6000, 10000] : times).Select((t, i) =>
            {
                string key = $"projects/{rig.H.Project.Id}/derivatives/{clip.Id:N}/still-{i}.jpg";
                rig.H.Objects[key] = H.Png(1280, 720, (byte)(40 + i));
                return new OutputStillObject(t / 1000.0, key);
            })
        ];
        return (rig, clip);
    }

    private static ExtractObjectRequest Extract(ProjectFile clip, IReadOnlyList<double>? at = null, bool? allow = null) => new(
        MediaRef: "file:" + clip.Id,
        ObjectDescription: "the red kettle",
        Name: "Kettle",
        AtSeconds: at ?? [2, 6, 10],
        ConsentStatement: AvatarObjectStatement.English,
        ConsentLocale: "en",
        AllowMediaEgress: allow);

    private static async Task<(int Status, JsonElement Body)> ExtractAsync(Rig rig, ExtractObjectRequest body, ClaimsPrincipal? principal = null) =>
        H.Read(await rig.Controller(principal: principal, path: $"/api/v1/projects/{rig.H.Project.Id}/avatars/extract-object")
            .ExtractObject(rig.H.Project.Id, body, CancellationToken.None));

    [Fact]
    public async Task An_extraction_is_refused_in_order_and_writes_nothing()
    {
        (Rig rig, ProjectFile clip) = VideoRig();

        rig.H.Options.Objects.Enabled = false;
        (await ExtractAsync(rig, Extract(clip))).Status.Should().Be(404);
        rig.H.Options.Objects.Enabled = true;

        (int status, JsonElement body) = await ExtractAsync(rig, Extract(clip) with { ConsentStatement = "I agree" });
        (status, body.GetProperty("code").GetString()).Should().Be((400, "object_statement_required"));
        (status, body) = await ExtractAsync(rig, Extract(clip) with { ObjectDescription = " " });
        (status, body.GetProperty("code").GetString()).Should().Be((400, "object_description_invalid"));
        (status, body) = await ExtractAsync(rig, Extract(clip, at: [1, 2, 3, 4, 5, 6, 7]));
        (status, body.GetProperty("code").GetString()).Should().Be((400, "at_seconds_invalid"));
        (status, body) = await ExtractAsync(rig, Extract(clip) with { MediaRef = "clip:" + clip.Id });
        (status, body.GetProperty("code").GetString()).Should().Be((400, "media_ref_invalid"));
        (status, body) = await ExtractAsync(rig, Extract(clip) with { MediaRef = "file:" + Guid.NewGuid() });
        (status, body.GetProperty("code").GetString()).Should().Be((409, "EXTRACTION_SOURCE_UNAVAILABLE"));
        (status, body) = await ExtractAsync(rig, Extract(clip, at: [2, 13]));
        (status, body.GetProperty("code").GetString()).Should().Be((400, "at_seconds_invalid"), "the video lasts 12 s");

        await rig.H.CreateObjectAsync("Kettle");
        (status, body) = await ExtractAsync(rig, Extract(clip));
        (status, body.GetProperty("code").GetString()).Should().Be((409, "AVATAR_NAME_TAKEN"));

        using InferenceApiDbContext db = rig.H.NewDb();
        (await db.AvatarBuildJobs.CountAsync()).Should().Be(0);
        rig.Charges!.Holds.Should().BeEmpty();
        rig.Rate.Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task With_a_segmentation_model_and_the_projects_consent_the_stills_are_held_and_the_paid_bucket_is_hit()
    {
        (Rig rig, ProjectFile clip) = VideoRig();

        (int status, JsonElement accepted) = await ExtractAsync(rig, Extract(clip));

        status.Should().Be(202, accepted.ToString());
        AvatarBuildJob job = rig.Job(accepted.GetProperty("jobId").GetGuid());
        (job.Kind, job.State, job.Stage, job.Total, job.AllowedBy, job.SourceMediaRef, job.ObjectDescription)
            .Should().Be(("extraction", "queued", "queued", 3, "project_consent", "file:" + clip.Id, "the red kettle"));
        AvatarBuildParams.Read(job.ParamsJson).Segment.Should().BeTrue();
        rig.Charges!.Holds.Should().ContainSingle().Which.EstimateMillis.Should().Be(3_000, "one still per moment");
        rig.Rate.Hits.Should().ContainSingle().Which.Bucket.Should().Be(RunRateBucket.Paid);

        // The build list carries it at once.
        (int listed, JsonElement builds) = H.Read(await rig.Controller().ListBuilds(rig.H.Project.Id, CancellationToken.None));
        listed.Should().Be(200);
        builds.EnumerateArray().Should().ContainSingle().Which.GetProperty("kind").GetString().Should().Be("extraction");
    }

    [Fact]
    public async Task Without_the_permission_or_without_a_model_whole_frames_are_used_and_nothing_is_held()
    {
        // A model, but no recorded consent and no per-call permission.
        (Rig noPermission, ProjectFile clip) = VideoRig(consent: false);
        Guid first = (await ExtractAsync(noPermission, Extract(clip))).Body.GetProperty("jobId").GetGuid();
        AvatarBuildJob a = noPermission.Job(first);
        (a.AllowedBy, AvatarBuildParams.Read(a.ParamsJson).Segment).Should().Be((null, false));
        noPermission.Charges!.Holds.Should().BeEmpty();
        noPermission.Rate.Hits.Should().ContainSingle().Which.Bucket.Should().Be(RunRateBucket.Free);

        // No model at all, with the consent.
        (Rig noModel, ProjectFile clip2) = VideoRig(segmentation: false);
        Guid second = (await ExtractAsync(noModel, Extract(clip2))).Body.GetProperty("jobId").GetGuid();
        AvatarBuildParams.Read(noModel.Job(second).ParamsJson).Segment.Should().BeFalse();
        noModel.Charges!.Holds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_person_at_the_screen_may_allow_sending_the_frames_for_one_call_and_another_caller_may_not()
    {
        (Rig rig, ProjectFile clip) = VideoRig(consent: false);
        Guid web = (await ExtractAsync(rig, Extract(clip, allow: true))).Body.GetProperty("jobId").GetGuid();
        AvatarBuildJob allowed = rig.Job(web);
        (allowed.AllowedBy, AvatarBuildParams.Read(allowed.ParamsJson).Segment).Should().Be(("call", true));
        rig.Project().SubjectMediaConsentAt.Should().BeNull("a per-call permission never records the project's consent");

        (Rig other, ProjectFile clip2) = VideoRig(consent: false);
        var tool = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthClaims.Scope, "mcp")], "Bearer"));
        Guid mcp = (await ExtractAsync(other, Extract(clip2, allow: true), tool)).Body.GetProperty("jobId").GetGuid();
        AvatarBuildJob refused = other.Job(mcp);
        (refused.AllowedBy, AvatarBuildParams.Read(refused.ParamsJson).Segment).Should().Be((null, false));
    }

    // ------------------------------------------------------------------ pulling an object out of a video: the job

    private static List<ProjectFile> ObjectFiles(Rig rig)
    {
        using InferenceApiDbContext db = rig.H.NewDb();
        return [.. db.ProjectFiles.Where(f => f.SourceKind == "avatar_extraction").OrderBy(f => f.OriginalPath)];
    }

    [Fact]
    public async Task Every_still_is_checked_for_a_person_before_any_still_is_sent_to_the_segmentation_model()
    {
        (Rig rig, ProjectFile clip) = VideoRig();
        // The last of the three stills shows a person: the first two are fine and must not have been sent either.
        byte[] person = H.Png(1280, 720, 42);
        rig.H.PersonCheck = new H.FakePersonCheck
        {
            Answer = picture => picture.AsSpan().SequenceEqual(person) ? ObjectPersonCheckVerdict.Person : ObjectPersonCheckVerdict.NoPerson
        };
        Guid jobId = (await ExtractAsync(rig, Extract(clip))).Body.GetProperty("jobId").GetGuid();
        await rig.RunAsync(jobId);
        Tick(rig, 11);
        await rig.RunAsync(jobId);

        AvatarBuildJob job = rig.Job(jobId);
        (job.State, job.FailureCode).Should().Be(("failed", "object_reference_shows_person"));
        job.Message.Should().Contain("frame at 10 s").And.Contain("shows a person");
        rig.SegmenterClients!.Calls.Should().BeEmpty("no still left the host: the check came first");
        rig.H.PersonCheck.Shown.Should().HaveCount(3);
        rig.Charges!.Released.Should().ContainSingle().Which.ReservationId.Should().Be(job.HoldId, "nothing was sent, so nothing is charged");
        rig.Charges.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_still_check_that_cannot_answer_sends_nothing_and_times_out()
    {
        (Rig rig, ProjectFile clip) = VideoRig();
        rig.H.PersonCheck = new H.FakePersonCheck { Answer = _ => ObjectPersonCheckVerdict.Unavailable };
        Guid jobId = (await ExtractAsync(rig, Extract(clip))).Body.GetProperty("jobId").GetGuid();
        await rig.RunAsync(jobId);
        Tick(rig, 11);
        await rig.RunAsync(jobId);
        rig.Job(jobId).State.Should().Be("finding", "the check waits and tries again");
        Tick(rig, 31 * 60);
        await rig.RunAsync(jobId);
        AvatarBuildJob job = rig.Job(jobId);
        (job.State, job.FailureCode).Should().Be(("failed", "AVATAR_CHECKS_TIMED_OUT"));
        rig.SegmenterClients!.Calls.Should().BeEmpty();
        rig.Charges!.Released.Should().ContainSingle();
    }

    [Fact]
    public async Task No_vision_provider_when_the_extraction_runs_fails_it_before_any_frame_is_sent_and_releases_the_hold()
    {
        (Rig rig, ProjectFile clip) = VideoRig();
        Guid jobId = (await ExtractAsync(rig, Extract(clip))).Body.GetProperty("jobId").GetGuid();
        rig.H.PersonCheck!.Active = false;
        await rig.RunAsync(jobId);
        Tick(rig, 11);
        await rig.RunAsync(jobId);
        AvatarBuildJob job = rig.Job(jobId);
        (job.State, job.FailureCode).Should().Be(("failed", "VISION_PROVIDER_REQUIRED"));
        job.Message.Should().Contain("vision model");
        rig.SegmenterClients!.Calls.Should().BeEmpty();
        rig.Charges!.Released.Should().ContainSingle().Which.ReservationId.Should().Be(job.HoldId);
    }

    [Fact]
    public async Task An_extraction_and_its_estimate_without_a_vision_provider_are_refused_before_anything_is_held_or_queued()
    {
        (Rig rig, ProjectFile clip) = VideoRig();
        rig.H.PersonCheck = null;
        (int status, JsonElement body) = await ExtractAsync(rig, Extract(clip));
        (status, body.GetProperty("code").GetString()).Should().Be((409, "VISION_PROVIDER_REQUIRED"));
        body.GetProperty("canConfigure").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        rig.Charges!.Holds.Should().BeEmpty();
        rig.Queued.Should().BeEmpty();
        await using InferenceApiDbContext db = rig.H.NewDb();
        (await db.AvatarBuildJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_object_is_found_in_the_stills_cut_at_full_size_and_made_an_object_avatar()
    {
        (Rig rig, ProjectFile clip) = VideoRig();
        rig.SegmenterClients!.Answer = n => n switch
        {
            1 => Rig.FakeSegmenter.Box(0.5, 0.5, 0.25, 0.25),
            2 => Rig.FakeSegmenter.Nothing(),
            _ => Rig.FakeSegmenter.Box(0.95, 0.95, 0.1, 0.1)
        };
        Guid jobId = (await ExtractAsync(rig, Extract(clip))).Body.GetProperty("jobId").GetGuid();

        // ---- The stills are asked for, then waited for. ----
        await rig.RunAsync(jobId);
        rig.Describer!.Requests.Should().ContainSingle().Which.Should().Equal(2000, 6000, 10000);
        rig.Job(jobId).State.Should().Be("finding");
        rig.SegmenterClients.Calls.Should().BeEmpty();

        // ---- The object is looked for in each still with the member's words; a miss drops that moment. ----
        Tick(rig, 11);
        await rig.RunAsync(jobId);
        rig.SegmenterClients.Calls.Should().HaveCount(3);
        rig.SegmenterClients.Calls.Should().OnlyContain(c => c.Prompt == "the red kettle" && c.SourceImageUrl.StartsWith("https://signed.example/"));
        AvatarBuildJob cutting = rig.Job(jobId);
        cutting.State.Should().Be("cutting");
        List<WorkflowStep> cuts = rig.StepsOf(rig.Queued.Single().WorkflowId);
        cuts.Should().HaveCount(2, "the moment where the object was not found is dropped");
        List<ExtractFrameStepConfig> configs = [.. cuts.Select(s => JsonSerializer.Deserialize<ExtractFrameStepConfig>(s.ExtractFrameConfigJson!, ExtractFrameStepConfig.JsonOptions)!)];
        configs.Select(c => (c.At, c.AtSec, c.Format, c.RegisterProjectFile)).Should().Equal((FrameAt.Time, 2.0, "png", false), (FrameAt.Time, 10.0, "png", false));
        configs.Should().OnlyContain(c => c.Source.Kind == VideoSourceKind.ProjectFile && c.Source.ProjectFileId == clip.Id && c.Validate().Count == 0);
        // The crops are in the video's own 1280 x 720 pixels: the middle box with its margin, the corner one moved inside.
        configs[0].Crop!.W.Should().BeApproximately(320 * 1.24 / 1280, 1e-5);
        configs[0].Crop!.H.Should().BeApproximately(256.0 / 720, 1e-5);
        (configs[1].Crop!.X + configs[1].Crop!.W).Should().BeLessThanOrEqualTo(1.0);

        // ---- The frames become ordinary files under objects/Kettle/ and the object avatar is made from them. ----
        rig.FinishRun(rig.Queued[0].ExecutionId, ExecutionStatus.Passed, n => H.Png(400, 300, (byte)(90 + n)), StepStatus.Completed, StepStatus.Completed);
        Tick(rig, 11);
        await rig.RunAsync(jobId);

        AvatarBuildJob ready = rig.Job(jobId);
        (ready.State, ready.Stage, ready.Done, ready.FailureCode).Should().Be(("ready", "creating", 2, null));
        ObjectFiles(rig).Select(f => f.OriginalPath).Should().Equal("objects/Kettle/Kettle-1.png", "objects/Kettle/Kettle-3.png");
        (int status, JsonElement avatar) = H.Read(await rig.H.Controller().Get(rig.H.Project.Id, ready.AvatarId!.Value, CancellationToken.None));
        status.Should().Be(200);
        (avatar.GetProperty("name").GetString(), avatar.GetProperty("subjectKind").GetString(), avatar.GetProperty("description").GetString())
            .Should().Be(("Kettle", "Object", "the red kettle"));
        avatar.GetProperty("references").EnumerateArray().Select(r => r.GetProperty("role").GetString()).Should().Equal("Front", "Angle");
        avatar.GetProperty("selectable").GetBoolean().Should().BeTrue();
        // What the segmentation model metered is what is charged.
        rig.Charges!.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(3_000);
    }

    [Fact]
    public async Task Without_a_segmentation_model_the_whole_frames_are_cut_and_the_job_says_so()
    {
        (Rig rig, ProjectFile clip) = VideoRig(segmentation: false);
        Guid jobId = (await ExtractAsync(rig, Extract(clip, at: [3]))).Body.GetProperty("jobId").GetGuid();

        await rig.RunAsync(jobId);
        Tick(rig, 11);
        await rig.RunAsync(jobId);

        WorkflowStep cut = rig.StepsOf(rig.Queued.Single().WorkflowId).Should().ContainSingle().Subject;
        JsonSerializer.Deserialize<ExtractFrameStepConfig>(cut.ExtractFrameConfigJson!, ExtractFrameStepConfig.JsonOptions)!.Crop.Should().BeNull();
        rig.FinishRun(rig.Queued[0].ExecutionId, ExecutionStatus.Passed, n => H.Png(1024, 1024, (byte)(70 + n)), StepStatus.Completed);
        Tick(rig, 11);
        await rig.RunAsync(jobId);

        AvatarBuildJob ready = rig.Job(jobId);
        ready.State.Should().Be("ready");
        ready.Message.Should().Contain("whole frames were used");
        rig.Charges!.Holds.Should().BeEmpty();
    }

    [Fact]
    public async Task An_object_found_nowhere_fails_the_job_and_charges_only_what_the_model_metered()
    {
        (Rig rig, ProjectFile clip) = VideoRig();
        rig.SegmenterClients!.Answer = _ => Rig.FakeSegmenter.Nothing();
        Guid jobId = (await ExtractAsync(rig, Extract(clip))).Body.GetProperty("jobId").GetGuid();

        await rig.RunAsync(jobId);
        Tick(rig, 11);
        await rig.RunAsync(jobId);

        AvatarBuildJob failed = rig.Job(jobId);
        (failed.State, failed.FailureCode).Should().Be(("failed", "OBJECT_NOT_FOUND_IN_VIDEO"));
        rig.Queued.Should().BeEmpty("nothing is cut when nothing was found");
        rig.Charges!.Settled.Should().ContainSingle().Which.ActualMillis.Should().Be(3_000);
    }

    /// <summary>The publish gate over stored bytes as a test double: the keys it refuses, and every key it was asked about.</summary>
    private sealed class FakeStoredCopies : ReelBolt.Inference.Api.Services.Moderation.IStoredCopyGate
    {
        public HashSet<string> Refused { get; } = [];

        public List<string> Asked { get; } = [];

        public Task<ReelBolt.Inference.Api.Services.Moderation.PublishModerationOutcome?> GateStoredCopyAsync(
            Guid projectId, string storageKey, CancellationToken ct)
        {
            Asked.Add(storageKey);
            return Task.FromResult(Refused.Contains(storageKey)
                ? ReelBolt.Inference.Api.Services.Moderation.PublishModerationOutcome.Pending()
                : null);
        }
    }

    /// <summary>A render of the project (a step result, no project file) the stills path knows, beside the rig's upload.</summary>
    private static (Guid RenderId, string Key) AddRender(Rig rig)
    {
        Guid renderId = Guid.NewGuid();
        string key = $"projects/{rig.H.Project.Id}/outputFiles/{Guid.NewGuid()}/film.mp4";
        rig.Describer!.Sources[renderId] = new OutputSource(
            renderId, "render", "film.mp4", "video/mp4", 1_000_000, key,
            new MediaSourceRef(MediaSourceKind.StepResult, renderId), null, 12.0);
        return (renderId, key);
    }

    [Fact]
    public async Task A_renders_frames_go_to_the_segmentation_model_only_with_a_current_publish_verdict()
    {
        // The request: a render whose verdict is not there yet is refused, and nothing is written or held.
        (Rig rig, ProjectFile clip) = VideoRig();
        var gate = new FakeStoredCopies();
        rig.StoredCopies = gate;
        (Guid renderId, string renderKey) = AddRender(rig);
        gate.Refused.Add(renderKey);

        (int status, JsonElement body) = await ExtractAsync(rig, Extract(clip) with { MediaRef = "step_result:" + renderId });

        (status, body.GetProperty("code").GetString()).Should().Be((409, "CONTENT_MODERATION_PENDING"));
        using (InferenceApiDbContext db = rig.H.NewDb()) (await db.AvatarBuildJobs.CountAsync()).Should().Be(0);
        rig.Charges!.Holds.Should().BeEmpty();
        rig.Rate.Hits.Should().BeEmpty();

        // An upload is asked about too and passes (the gate judges only outputs' bytes).
        (int upload, JsonElement accepted) = await ExtractAsync(rig, Extract(clip));
        upload.Should().Be(202, accepted.ToString());
        gate.Asked.Should().Contain(clip.StorageKey);
    }

    [Fact]
    public async Task A_verdict_lost_after_the_request_stops_the_job_before_any_frame_is_sent()
    {
        (Rig rig, ProjectFile clip) = VideoRig();
        var gate = new FakeStoredCopies();
        rig.StoredCopies = gate;
        (Guid renderId, string renderKey) = AddRender(rig);
        Guid jobId = (await ExtractAsync(rig, Extract(clip) with { MediaRef = "step_result:" + renderId }))
            .Body.GetProperty("jobId").GetGuid();
        rig.Charges!.Holds.Should().ContainSingle("the stills the segmentation model may be shown are held");

        // The output is judged again (its bytes changed) after the request was accepted.
        gate.Refused.Add(renderKey);
        await rig.RunAsync(jobId);
        Tick(rig, 11);
        await rig.RunAsync(jobId);

        AvatarBuildJob failed = rig.Job(jobId);
        (failed.State, failed.FailureCode).Should().Be(("failed", "CONTENT_MODERATION_PENDING"));
        rig.SegmenterClients!.Calls.Should().BeEmpty("no still of the render left the host");
        rig.Queued.Should().BeEmpty();
        rig.Charges.Settled.Should().BeEmpty("nothing was metered");
        rig.Charges.Released.Should().ContainSingle("the hold is given back");
    }

    [Fact]
    public async Task A_failed_call_keeps_that_frame_whole_and_a_video_gone_meanwhile_releases_the_hold()
    {
        (Rig rig, ProjectFile clip) = VideoRig();
        rig.SegmenterClients!.Answer = n => n == 1 ? throw new InvalidOperationException("vendor down") : Rig.FakeSegmenter.Box(0.5, 0.5, 0.2, 0.2);
        Guid jobId = (await ExtractAsync(rig, Extract(clip, at: [2, 6]))).Body.GetProperty("jobId").GetGuid();
        await rig.RunAsync(jobId);
        Tick(rig, 11);
        await rig.RunAsync(jobId);

        List<ExtractFrameStepConfig> configs = [.. rig.StepsOf(rig.Queued.Single().WorkflowId)
            .Select(s => JsonSerializer.Deserialize<ExtractFrameStepConfig>(s.ExtractFrameConfigJson!, ExtractFrameStepConfig.JsonOptions)!)];
        configs.Select(c => c.Crop is null).Should().Equal(true, false);
        rig.Job(jobId).State.Should().Be("cutting");

        // Another extraction whose video is gone before a worker takes it: refunded, nothing made.
        (Rig other, ProjectFile clip2) = VideoRig();
        Guid gone = (await ExtractAsync(other, Extract(clip2))).Body.GetProperty("jobId").GetGuid();
        other.Describer!.Sources.Clear();
        await other.RunAsync(gone);
        AvatarBuildJob failed = other.Job(gone);
        (failed.State, failed.FailureCode).Should().Be(("failed", "EXTRACTION_SOURCE_UNAVAILABLE"));
        other.Charges!.Released.Should().ContainSingle();
        other.Charges.Settled.Should().BeEmpty();
    }
}
