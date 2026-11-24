using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Wave-A acceptance tests for the PLAN-SOURCED <see cref="VideoGenerateStepExecutor"/> path
/// (<c>PlanSource = StepRef</c>, T-301). The inline path is covered by
/// <see cref="VideoGenerateStepExecutorTests"/>; this file owns the behaviour that only exists on
/// the plan path.
/// </summary>
/// <remarks>
/// <para>
/// Everything is mocked at the <see cref="IVideoGenerationClient"/> /
/// <see cref="IProjectFileWorkspace"/> / <see cref="IVideoToolRunner"/> boundaries exactly as
/// <see cref="VideoGenerateStepExecutorTests"/> does — no HTTP call, no network, and no paid
/// provider request is ever made. <see cref="PricingOnlyMiniMax"/> is a real
/// <see cref="MiniMaxVideoGenerationClient"/> whose <c>Describe</c> (the only member these tests
/// reach — a pure table lookup) supplies the real MiniMax-H3 price/duration vocabulary, while its
/// submit/poll/download members are never invoked because they are reached through an
/// <see cref="HttpClient"/> stub that would have to be real to do anything.
/// </para>
/// <para>
/// The budget test deliberately wires the REAL <see cref="VideoGenerationBudgetGate"/> over an
/// InMemory <see cref="WorkflowEngineDbContext"/> rather than a hand-rolled
/// <see cref="IVideoGenerationBudgetGate"/> mock: the acceptance criterion is "zero submits AND
/// zero <c>external_generation_jobs</c> rows", and only the real gate can write (or fail to write)
/// those rows.
/// </para>
/// </remarks>
public class VideoGeneratePlanSourcedExecutorTests
{
    private const string Model = "MiniMax-H3";
    private const string Resolution = "768P";
    private const string Ratio = "16:9";

    /// <summary>MiniMax-H3 at 768P: $0.08/s — Short=4s → $0.32, Medium=9s → $0.72, Long=15s → $1.20.</summary>
    private const decimal PerSecond768P = 0.08m;

    private static readonly Guid ProviderId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ExecutionId = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly Guid StepResultId = Guid.Parse("12345678-1234-1234-1234-123456789012");

    private const int PlanStepOrder = 2;
    private const int AnalysisStepOrder = 1;
    private const string AnalysisStorageKey =
        "projects/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/agentFiles/video-analysis/99999999-8888-7777-6666-555555555555/step-1-analysis.json";

    private static readonly System.Text.Json.JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly System.Text.Json.JsonSerializerOptions ArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ==========================================================================================
    // Plan-sourced budget: the wave's named acceptance criterion
    // ==========================================================================================

    [Fact]
    public async Task Plan_over_the_step_budget_fails_BUDGET_EXCEEDED_with_zero_submits_and_zero_ledger_rows()
    {
        // Three Medium shots (9s each at $0.08/s) price the whole plan at 3 * $0.72 = $2.16, well
        // over the $1.00 step cap. Nothing about this plan is over MaxClips, so the WHOLE-PLAN
        // pre-check is the only thing standing between this step and three paid submits.
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1", "r2", "r3");
        var workspace = UploadingWorkspace(new UploadLog());

        var config = PlanConfig(stepCap: 1.00m, maxClips: 4);
        var plan = PlanJson(
            Shot("A drone shot over a bay", "Medium"),
            Shot("A close-up of a keyboard", "Medium"),
            Shot("A wide shot of a city at dusk", "Medium"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("BUDGET_EXCEEDED");
        Meta(result)["error"]!.GetValue<string>().Should().Contain("360 credits").And.Contain("this step's spending limit is 1 credit");

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        using var db = factory.CreateDbContext();
        db.Set<ExternalGenerationJob>().Count().Should().Be(0,
            "an over-plan estimate is refused BEFORE the atomic reservation, so no ledger row (and no money) can exist");
    }

    [Fact]
    public async Task A_plan_larger_than_MaxClips_is_trimmed_and_only_the_trimmed_clips_are_checked_against_the_step_cap()
    {
        // The person decides how many clips to buy (MaxClips) and the most credits to spend: a clip past MaxClips is
        // skipped, never submitted or reserved, so the step cap is checked against the trimmed plan. The effective plan
        // here is one clip (120 credits, under the 150-credit cap) while the plan written is three; the run gate prices the same
        // MaxClips clips, so gate and executor agree and nothing fails after the paid analysis.
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());

        var config = PlanConfig(stepCap: 150m, maxClips: 1); // credits: one clip is 120, the whole plan of three 360
        var plan = PlanJson(
            Shot("Shot one", "Medium"),
            Shot("Shot two", "Medium"),
            Shot("Shot three", "Medium"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "the trimmed plan (one clip) fits the cap");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        using var db = factory.CreateDbContext();
        db.Set<ExternalGenerationJob>().Count().Should().Be(1, "only MaxClips clips are reserved; the rest of the plan is skipped");
    }

    // ==========================================================================================
    // Egress: both branches
    // ==========================================================================================

    [Fact]
    public async Task Plan_naming_a_keyframe_without_egress_consent_fails_EGRESS_REFUSED_with_zero_submits_and_zero_rows()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());

        var frames = new FrameFixture();

        var config = PlanConfig(allowSourceMediaEgress: false);
        var plan = PlanJson(Shot("A shot continuing from the previous frame", "Short", firstFrame: "s1"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan, frames: frames);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("EGRESS_REFUSED");
        Meta(result)["error"]!.GetValue<string>().Should().Contain("s1").And.Contain("AllowSourceMediaEgress");

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.Describe(It.IsAny<string>()), Times.AtLeastOnce, "the provider must still be resolved before the egress pre-scan can run");

        // The gate is not merely unpassed — it is unreachable. No session was opened, no object was
        // staged and no frame was decoded, so the user's footage never even reached ffmpeg.
        frames.Factory.Opened.Should().Be(0, "no consent means no media session is opened");
        frames.Session.Staged.Should().BeEmpty("no consent means no source object is staged");
        frames.Session.FfmpegCalls.Should().BeEmpty("no consent means ffmpeg is never asked for a frame");

        using var db = factory.CreateDbContext();
        db.Set<ExternalGenerationJob>().Count().Should().Be(0,
            "the egress pre-scan runs before the single atomic reservation, so a refused plan cannot have cost anything");
    }

    /// <summary>
    /// The headline of this work: an offered id the plan named as a frame becomes REAL BYTES on the
    /// request the provider receives. Every assertion below is about the request the fake client was
    /// handed, not about the manifest's self-report.
    /// </summary>
    [Fact]
    public async Task Plan_naming_keyframes_with_egress_consent_cuts_them_and_puts_them_on_the_request()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = SubmittingClient(submitted, "r1", "r2");
        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);
        var frames = new FrameFixture();
        DownloadArtifact(workspace, Artifact(
            SourceStorageKey,
            [new VideoAnalysisShot("s1", 0, 5), new VideoAnalysisShot("s2", 5, 10)],
            ["s1", "s2"]));

        var config = PlanConfig(
            allowSourceMediaEgress: true,
            analysis: new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder));
        var plan = PlanJson(
            Shot("Shot keyed off the previous frame", "Short", firstFrame: "s1"),
            Shot("Shot with no keyframe", "Short"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan, frames: frames);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        submitted.Should().HaveCount(2, "consent means the whole plan proceeds, keyframes and all");

        // THE claim: the frame's own bytes ride the request, with the media type ffmpeg encoded.
        submitted[0].Images.Should().NotBeNull().And.ContainSingle();
        submitted[0].Images![0].ContentType.Should().Be("image/jpeg");
        submitted[0].Images![0].Bytes.Should().Equal(FrameBytes,
            "the bytes must be the ones the media session produced, not a placeholder or an empty array");
        submitted[1].Images.Should().BeNull("a shot that names no frame sends none");

        // ...and it was cut from the right place: the shot's OWN start, out of the source the artifact
        // recorded for it, through one staging.
        frames.Session.Staged.Should().ContainSingle("one shot, one source, one staging");
        string[] argv = frames.Session.FfmpegCalls.Should().ContainSingle().Subject;
        argv.Should().Contain("-frames:v");
        argv[Array.IndexOf(argv, "-ss") + 1].Should().Be("0", "a first frame is the shot's first moment");

        // The manifest now says APPLIED. This is the honest opposite of the constant this test used to
        // pin, and the assertion that stops a future regression from silently un-flipping it.
        JsonObject manifestClips = manifestRoot(log)!["clips"]!.AsArray()[0]!.AsObject();
        manifestClips["keyframeRequested"]!.GetValue<bool>().Should().BeTrue();
        manifestClips["keyframeApplied"]!.GetValue<bool>().Should().BeTrue();
        manifestClips["keyframeReason"].Should().BeNull();

        manifestRoot(log)!["clips"]!.AsArray()[1]!["keyframeRequested"]!.GetValue<bool>().Should().BeFalse();

        Meta(result)["keyframesRequested"]!.GetValue<int>().Should().Be(1);
        Meta(result)["keyframesNotApplied"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Plan_naming_a_keyframe_absent_from_the_analysis_artifact_records_keyframe_unresolved_without_failing_the_clip()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = SubmittingClient(submitted, "r1");
        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);
        DownloadAnalysis(workspace, new[] { "s1" });

        var config = PlanConfig(
            allowSourceMediaEgress: true,
            analysis: new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder));
        var plan = PlanJson(Shot("A shot keyed off an id that was never offered", "Short", firstFrame: "s9"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "an unoffered keyframe id degrades its own clip, never the step");
        submitted.Should().HaveCount(1);

        var clip = (JsonObject)manifestRoot(log)!["clips"]![0]!;
        clip["status"]!.GetValue<string>().Should().Be("succeeded");
        clip["keyframeRequested"]!.GetValue<bool>().Should().BeTrue();
        clip["keyframeApplied"]!.GetValue<bool>().Should().BeFalse();
        clip["keyframeReason"]!.GetValue<string>().Should().Be("keyframe_unresolved");
    }

    /// <summary>
    /// A still photo the project holds is a frame like any other: it is a <c>VideoAnalyze</c> SOURCE
    /// (the analyzer renders it into a short clip with a gentle move and one scene per beat), so its
    /// beats are ordinary offered <c>s{n}</c> ids and resolve through exactly this path. That is why
    /// no second id namespace exists for stills.
    /// </summary>
    [Fact]
    public async Task A_still_photo_the_project_holds_reaches_the_provider_as_a_frame()
    {
        const string StillStorageKey =
            "projects/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/agentFiles/video-analysis/99999999-8888-7777-6666-555555555555/step-1-src0-still.mp4";

        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = SubmittingClient(submitted, "r1");
        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);
        var frames = new FrameFixture();

        // 30 fps, so the shot's exclusive end at 3.0s has its last frame at 3.0 - 1/30.
        DownloadArtifact(workspace, Artifact(StillStorageKey, [new VideoAnalysisShot("s1", 0, 3)], ["s1"]));

        var config = PlanConfig(
            allowSourceMediaEgress: true,
            analysis: new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder));
        var plan = PlanJson(Shot("A shot settling onto the still photo", "Short", lastFrame: "s1"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan, frames: frames);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        submitted.Should().ContainSingle();
        submitted[0].Images.Should().NotBeNull().And.ContainSingle();
        submitted[0].Images![0].Bytes.Should().Equal(FrameBytes);

        // The still's own rendered clip was staged, and the frame came from one frame-length BEFORE the
        // shot's exclusive end — asking at the end itself would hand the provider the first frame of
        // whatever follows.
        frames.Session.Staged.Should().ContainSingle().Which.Should().StartWith("kf-source-");
        string[] argv = frames.Session.FfmpegCalls.Should().ContainSingle().Subject;
        double atSec = double.Parse(argv[Array.IndexOf(argv, "-ss") + 1], System.Globalization.CultureInfo.InvariantCulture);
        atSec.Should().BeApproximately(3.0 - (1.0 / 30.0), 0.002,
            "a last frame is the shot's last contained frame, not its exclusive end");

        var clip = (JsonObject)manifestRoot(log)!["clips"]![0]!;
        clip["keyframeRequested"]!.GetValue<bool>().Should().BeTrue();
        clip["keyframeApplied"]!.GetValue<bool>().Should().BeTrue();
    }

    /// <summary>
    /// The genuine refusal this change leaves behind: a model whose own client declaration says it
    /// cannot take an image (fal's text-to-video endpoint). It is refused BEFORE any frame is cut and
    /// before any ledger row can exist, so an operator is never billed for a clip that would silently
    /// drop the frame they consented to send.
    /// </summary>
    [Fact]
    public async Task A_provider_that_cannot_take_an_image_refuses_the_plan_before_any_spend()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = ClientDeclaring(VideoImageInputSupport.None);
        var frames = new FrameFixture();
        var workspace = UploadingWorkspace(new UploadLog());
        DownloadArtifact(workspace, Artifact(SourceStorageKey, [new VideoAnalysisShot("s1", 0, 5)], ["s1"]));

        var config = PlanConfig(
            allowSourceMediaEgress: true,
            analysis: new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder),
            model: "fal-ai/kling-video/v3/turbo/pro/text-to-video");
        var plan = PlanJson(Shot("A shot keyed off the previous frame", "Short", firstFrame: "s1"));

        var (executor, context) = BuildPlanExecutor(
            factory, client, workspace, config, plan,
            kind: InferenceProviderKind.FalAi,
            modelName: "fal-ai/kling-video/v3/turbo/pro/text-to-video", frames: frames);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("KEYFRAME_NOT_SUPPORTED");
        Meta(result)["error"]!.GetValue<string>().Should().Contain("fal-ai/kling-video/v3/turbo/pro/text-to-video").And.Contain("accepts no input image");

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        frames.Factory.Opened.Should().Be(0, "the capability is knowable before any frame is cut");
        frames.Session.FfmpegCalls.Should().BeEmpty();

        using var db = factory.CreateDbContext();
        db.Set<ExternalGenerationJob>().Count().Should().Be(0);
    }

    /// <summary>
    /// fal.ai is one of the video vendors that CAN take an image, so a fal image-to-video plan must
    /// pass the pre-scan and reach the submit with its frames on the request - no
    /// KEYFRAME_NOT_SUPPORTED and no premature refusal.
    /// </summary>
    [Fact]
    public async Task A_FalAi_image_to_video_plan_carries_its_frames_through_the_pre_scan()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = SubmittingClient(submitted, "r1");
        var frames = new FrameFixture();
        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);
        DownloadArtifact(workspace, Artifact(SourceStorageKey, [new VideoAnalysisShot("s1", 0, 5)], ["s1"]));

        var config = PlanConfig(
            allowSourceMediaEgress: true,
            analysis: new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder),
            model: "fal-ai/vidu/q4/image-to-video");
        var plan = PlanJson(Shot("A shot keyed off the previous frame", "Short", firstFrame: "s1", lastFrame: "s1"));

        var (executor, context) = BuildPlanExecutor(
            factory, client, workspace, config, plan,
            kind: InferenceProviderKind.FalAi,
            modelName: "fal-ai/vidu/q4/image-to-video", frames: frames);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        submitted.Should().ContainSingle();
        submitted[0].Images.Should().NotBeNull().And.HaveCount(2);
        submitted[0].Images![0].Bytes.Should().Equal(FrameBytes);
        submitted[0].Images![1].Bytes.Should().Equal(FrameBytes);
        Meta(result)["clipsGenerated"]!.GetValue<int>().Should().Be(1);

        var clip = (JsonObject)manifestRoot(log)!["clips"]![0]!;
        clip["keyframeRequested"]!.GetValue<bool>().Should().BeTrue();
        clip["keyframeApplied"]!.GetValue<bool>().Should().BeTrue();
    }

    /// <summary>
    /// The client-level half of the pre-scan: a resolved provider kind CAN take an image, but the
    /// resolved model's own client declaration says this model cannot. It is refused with the same
    /// stable code before a frame is cut and before any ledger row can exist.
    /// </summary>
    [Fact]
    public async Task A_model_that_declares_no_image_input_refuses_the_plan_before_any_spend()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = ClientDeclaring(VideoImageInputSupport.None);
        var frames = new FrameFixture();
        var workspace = UploadingWorkspace(new UploadLog());
        DownloadArtifact(workspace, Artifact(SourceStorageKey, [new VideoAnalysisShot("s1", 0, 5)], ["s1"]));

        var config = PlanConfig(
            allowSourceMediaEgress: true,
            analysis: new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder));
        var plan = PlanJson(Shot("A shot keyed off the previous frame", "Short", firstFrame: "s1"));

        var (executor, context) = BuildPlanExecutor(
            factory, client, workspace, config, plan,
            kind: InferenceProviderKind.MiniMax, frames: frames);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("KEYFRAME_NOT_SUPPORTED");
        Meta(result)["error"]!.GetValue<string>().Should().Contain("MiniMax-H3").And.Contain("accepts no input image");

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        frames.Factory.Opened.Should().Be(0, "the capability is knowable before any frame is cut");
        frames.Session.FfmpegCalls.Should().BeEmpty();

        using var db = factory.CreateDbContext();
        db.Set<ExternalGenerationJob>().Count().Should().Be(0);
    }

    /// <summary>
    /// A frame that cannot be cut degrades ITS OWN clip with a reason and the clip is still generated
    /// from its prompt — the same shape an unresolvable id has always had, and never a silent drop.
    /// </summary>
    [Fact]
    public async Task A_frame_that_cannot_be_cut_degrades_its_clip_and_still_submits_it()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = SubmittingClient(submitted, "r1");
        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);
        var frames = new FrameFixture { };
        frames.Session.FfmpegExitCode = 1;
        DownloadArtifact(workspace, Artifact(SourceStorageKey, [new VideoAnalysisShot("s1", 0, 5)], ["s1"]));

        var config = PlanConfig(
            allowSourceMediaEgress: true,
            analysis: new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder));
        var plan = PlanJson(Shot("A shot keyed off the previous frame", "Short", firstFrame: "s1"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan, frames: frames);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a frame the environment could not produce is not a plan defect");
        submitted.Should().ContainSingle();
        submitted[0].Images.Should().BeNull();

        var clip = (JsonObject)manifestRoot(log)!["clips"]![0]!;
        clip["status"]!.GetValue<string>().Should().Be("succeeded");
        clip["keyframeRequested"]!.GetValue<bool>().Should().BeTrue();
        clip["keyframeApplied"]!.GetValue<bool>().Should().BeFalse();
        clip["keyframeReason"]!.GetValue<string>().Should().Be("keyframe_extraction_failed");
        Meta(result)["keyframesNotApplied"]!.GetValue<int>().Should().Be(1);
    }

    /// <summary>
    /// A clip the <c>MaxClips</c> trim dropped never reaches a ledger row, so no frame is cut for it and
    /// the manifest says exactly that — a third outcome, distinct from "the id did not resolve" and from
    /// "the frame could not be cut".
    /// </summary>
    [Fact]
    public async Task A_clip_trimmed_past_MaxClips_records_keyframe_not_dispatched_and_cuts_no_frame()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = SubmittingClient(submitted, "r1");
        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);
        var frames = new FrameFixture();
        DownloadArtifact(workspace, Artifact(
            SourceStorageKey,
            [new VideoAnalysisShot("s1", 0, 5), new VideoAnalysisShot("s2", 5, 10)],
            ["s1", "s2"]));

        var config = PlanConfig(
            allowSourceMediaEgress: true,
            maxClips: 1,
            analysis: new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder));
        var plan = PlanJson(
            Shot("First planned shot", "Short", firstFrame: "s1"),
            Shot("Second planned shot", "Short", firstFrame: "s2"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan, frames: frames);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        submitted.Should().ContainSingle();
        submitted[0].Images.Should().NotBeNull().And.ContainSingle();

        // ONE frame cut for TWO keyframed shots: the trimmed clip never cost a decode.
        frames.Session.FfmpegCalls.Should().ContainSingle();

        JsonArray manifestClips = manifestRoot(log)!["clips"]!.AsArray();
        manifestClips[1]!["status"]!.GetValue<string>().Should().Be("skipped");
        manifestClips[1]!["skipReason"]!.GetValue<string>().Should().Be("MAX_CLIPS_EXCEEDED");
        manifestClips[1]!["keyframeRequested"]!.GetValue<bool>().Should().BeTrue();
        manifestClips[1]!["keyframeApplied"]!.GetValue<bool>().Should().BeFalse();
        manifestClips[1]!["keyframeReason"]!.GetValue<string>().Should().Be("keyframe_not_dispatched");
    }

    // ==========================================================================================
    // The Output/manifest split: no storage path is model-visible
    // ==========================================================================================

    [Fact]
    public async Task Plan_sourced_output_carries_no_storage_path_while_the_manifest_carries_the_real_keys()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = SubmittingClient(submitted, "r1", "r2");
        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);

        var config = PlanConfig();
        var plan = PlanJson(Shot("First shot", "Short"), Shot("Second shot", "Short"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);

        // ---- Half one: the step's Output is prompt-visible, so it carries no path at all. ----
        result.Output.Should().NotContain("storageKey");
        result.Output.Should().NotContain("projects/");
        result.Output.Should().NotContain("agentFiles/");
        result.Output.Should().NotContain("outputFiles/");

        JsonArray outputClips = JsonNode.Parse(result.Output)!["clips"]!.AsArray();
        outputClips.Should().HaveCount(2);
        outputClips.All(c => c!["stored"]!.GetValue<bool>()).Should().BeTrue();

        // ---- Half two: the manifest holds the real keys, and ArtifactStorageKey points at it. ----
        result.ArtifactStorageKey.Should().NotBeNull();
        result.ArtifactStorageKey!.Should().StartWith($"projects/{ProjectId:D}/agentFiles/video-analysis/");
        result.ArtifactStorageKey!.Should().EndWith($"step-{context.Step.StepOrder}-generated-clips.json");

        JsonObject manifest = manifestRoot(log)!;
        manifest["version"]!.GetValue<int>().Should().Be(1);
        manifest["planStepOrder"]!.GetValue<int>().Should().Be(PlanStepOrder);

        JsonArray manifestClips = manifest["clips"]!.AsArray();
        manifestClips.Should().HaveCount(2);
        foreach (JsonNode? clip in manifestClips)
        {
            clip!["storageKey"]!.GetValue<string>().Should()
                .StartWith($"projects/{ProjectId:D}/agentFiles/")
                .And.EndWith(".mp4");
        }

        manifestClips[0]!["storageKey"]!.GetValue<string>().Should().NotBe(manifestClips[1]!["storageKey"]!.GetValue<string>());

        // The manifest is uploaded as a bare artifact under the video-analysis prefix — the same
        // prefix StepResultArtifactsController validates against, and never outputFiles.
        log.Calls.Should().ContainSingle(c => c.ContentType == "application/json");
        log.Calls.Single(c => c.ContentType == "application/json").Category.Should().Be("agentFiles");
        log.Calls.Single(c => c.ContentType == "application/json").Name
            .Should().Be($"video-analysis/{ExecutionId:D}/step-{context.Step.StepOrder}-generated-clips.json");
    }

    // ==========================================================================================
    // Dispatch: clip ids, prompt hygiene, and the plan's own words in the manifest
    // ==========================================================================================

    [Fact]
    public async Task Plan_sourced_clips_are_v_numbered_and_their_prompts_go_through_the_shared_sanitizer()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = SubmittingClient(submitted, "r1", "r2", "r3", "r4");
        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);

        // Takes: 2
        var config = PlanConfig(takes: 2, maxClips: 4);
        // The control character is the injection-shaped noise the shared sanitizer exists to strip
        // before the prompt reaches a provider.
        var plan = PlanJson(
            Shot("A cafe at dawn\u0001 with steam rising", "Short", purpose: "Cutaway", anchor: "s1"),
            Shot("A tram crossing a bridge", "Short", purpose: "Cutaway", anchor: "s2"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        submitted.Should().HaveCount(4);

        submitted.Select(r => r.PromptText).Should().AllSatisfy(p => p.Should().NotContain("\u0001"));
        submitted[0].PromptText.Should().Be("A cafe at dawn with steam rising");
        submitted[2].PromptText.Should().Be("A tram crossing a bridge");

        // Takes > 1 ⇒ v{n}t{k}; Takes = 1 ⇒ v{n}.
        JsonArray clips = JsonNode.Parse(result.Output)!["clips"]!.AsArray();
        clips.Select(c => c!["clipId"]!.GetValue<string>()).Should().Equal("v1t1", "v1t2", "v2t1", "v2t2");

        var manifestClips = manifestRoot(log)!["clips"]!.AsArray();
        manifestClips.Select(c => c!["clipId"]!.GetValue<string>()).Should().Equal("v1t1", "v1t2", "v2t1", "v2t2");
        manifestClips.Select(c => c!["shotIndex"]!.GetValue<int>()).Should().Equal(1, 1, 2, 2);
        manifestClips.Select(c => c!["take"]!.GetValue<int>()).Should().Equal(1, 2, 1, 2);
        manifestClips.Select(c => c!["purpose"]!.GetValue<string>()).Should().Equal("Cutaway", "Cutaway", "Cutaway", "Cutaway");
        manifestClips.Select(c => c!["anchor"]!.GetValue<string>()).Should().Equal("s1", "s1", "s2", "s2");
        manifestClips.Select(c => c!["durationSeconds"]!.GetValue<int>()).Should().OnlyContain(d => d == 4);

        Meta(result)["shotsPlanned"]!.GetValue<int>().Should().Be(2);
        Meta(result)["clipsGenerated"]!.GetValue<int>().Should().Be(4);
    }

    [Fact]
    public async Task Plan_sourced_clips_are_v_numbered_when_Takes_is_one()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1", "r2", "r3");
        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);

        var config = PlanConfig(maxClips: 3);
        var plan = PlanJson(Shot("One", "Short"), Shot("Two", "Short"), Shot("Three", "Short"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);
        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        JsonNode.Parse(result.Output)!["clips"]!.AsArray()
            .Select(c => c!["clipId"]!.GetValue<string>())
            .Should().Equal("v1", "v2", "v3");
    }

    // ==========================================================================================
    // MaxClips is a cap ACROSS the plan
    // ==========================================================================================

    [Fact]
    public async Task MaxClips_is_enforced_across_the_whole_plan_with_the_excess_skipped_in_the_manifest()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = SubmittingClient(submitted, "r1", "r2", "r3", "r4", "r5");
        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);

        var config = PlanConfig(maxClips: 3);
        var plan = PlanJson(
            Shot("Shot one", "Short"),
            Shot("Shot two", "Short"),
            Shot("Shot three", "Short"),
            Shot("Shot four", "Short"),
            Shot("Shot five", "Short"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, "a capped plan still produces the clips it can afford");
        submitted.Should().HaveCount(3, "MaxClips caps the whole step, not each shot");

        JsonArray clips = JsonNode.Parse(result.Output)!["clips"]!.AsArray();
        clips.Should().HaveCount(5, "the two skipped shots are still reported, never silently dropped");
        clips.Select(c => c!["clipId"]!.GetValue<string>()).Should().Equal("v1", "v2", "v3", "v4", "v5");
        clips.Select(c => c!["status"]!.GetValue<string>()).Should().Equal("succeeded", "succeeded", "succeeded", "skipped", "skipped");
        clips[3]!["skipReason"]!.GetValue<string>().Should().Be("MAX_CLIPS_EXCEEDED");
        clips[4]!["skipReason"]!.GetValue<string>().Should().Be("MAX_CLIPS_EXCEEDED");
        clips[3]!["stored"]!.GetValue<bool>().Should().BeFalse();

        var manifestClips = manifestRoot(log)!["clips"]!.AsArray();
        manifestClips[0]!["skipReason"].Should().BeNull();
        manifestClips[1]!["skipReason"].Should().BeNull();
        manifestClips[2]!["skipReason"].Should().BeNull();
        manifestClips[3]!["skipReason"]!.GetValue<string>().Should().Be("MAX_CLIPS_EXCEEDED");
        manifestClips[4]!["skipReason"]!.GetValue<string>().Should().Be("MAX_CLIPS_EXCEEDED");
        manifestClips[3]!["storageKey"].Should().BeNull();
        manifestClips[4]!["storageKey"].Should().BeNull();
        manifestClips[3]!["status"]!.GetValue<string>().Should().Be("skipped");

        Meta(result)["shotsPlanned"]!.GetValue<int>().Should().Be(5);
        Meta(result)["clipsSkipped"]!.GetValue<int>().Should().Be(2);

        // The cap is a LEDGER cap too: only the three dispatched clips ever held a reservation.
        using var db = factory.CreateDbContext();
        db.Set<ExternalGenerationJob>().Select(j => j.ClipId).OrderBy(id => id).Should().Equal("v1", "v2", "v3");
    }

    // ==========================================================================================
    // A failed clip does not fail the step
    // ==========================================================================================

    [Fact]
    public async Task One_rejected_clip_does_not_fail_the_step_and_the_other_still_delivers()
    {
        // A 400 is a DEFINITE rejection: the vendor refused the task and nothing was billed, so
        // that one clip degrades to PROVIDER_REJECTED while the plan's other shot still lands.
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        int submits = 0;

        var client = MiniMaxDescribingClient();
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<VideoGenerationRequest, CancellationToken>((r, _) => submitted.Add(r))
            .Returns(() =>
            {
                submits++;
                return submits == 1
                    ? Task.FromException<ExternalJobHandle>(
                        new HttpRequestException("the provider rejected the submission", null, HttpStatusCode.BadRequest))
                    : Task.FromResult(new ExternalJobHandle("r2"));
            });
        client
            .Setup(c => c.PollAsync(It.Is<ExternalJobHandle>(h => h.RemoteJobId == "r2"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.invalid/clip.mp4", null));
        client
            .Setup(c => c.DownloadAsync("https://example.invalid/clip.mp4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4]));

        var log = new UploadLog();
        var workspace = UploadingWorkspace(log);

        var config = PlanConfig(maxClips: 2);
        var plan = PlanJson(Shot("First shot", "Short"), Shot("Second shot", "Short"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed,
            "only an ALL-clips-failed plan fails the step; one degraded clip is a per-clip degradation");
        submitted.Should().HaveCount(2);

        JsonArray clips = JsonNode.Parse(result.Output)!["clips"]!.AsArray();
        clips[0]!["status"]!.GetValue<string>().Should().Be("failed");
        clips[0]!["failureReason"]!.GetValue<string>().Should().Be("PROVIDER_REJECTED");
        clips[0]!["stored"]!.GetValue<bool>().Should().BeFalse();

        clips[1]!["status"]!.GetValue<string>().Should().Be("succeeded");
        clips[1]!["stored"]!.GetValue<bool>().Should().BeTrue();

        result.OutputStorageKey.Should().NotBeNull("OutputStorageKey stays the first SUCCEEDED clip");

        var manifestClips = manifestRoot(log)!["clips"]!.AsArray();
        manifestClips[1]!["storageKey"]!.GetValue<string>().Should().Contain("agentFiles/");
        manifestClips[0]!["storageKey"].Should().BeNull();
        manifestClips[0]!["failureReason"]!.GetValue<string>().Should().Be("PROVIDER_REJECTED");
    }

    [Fact]
    public async Task Every_clip_failing_still_fails_the_step_and_only_then()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = MiniMaxDescribingClient();
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromException<ExternalJobHandle>(
                new HttpRequestException("nope", null, HttpStatusCode.Forbidden)));

        var workspace = UploadingWorkspace(new UploadLog());
        var config = PlanConfig(maxClips: 2);
        var plan = PlanJson(Shot("First shot", "Short"), Shot("Second shot", "Short"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("ALL_TAKES_FAILED");
        JsonNode.Parse(result.Output)!["clips"]!.AsArray()
            .All(c => c!["failureReason"]!.GetValue<string>() == "PROVIDER_AUTH_FAILED")
            .Should().BeTrue();
    }

    // ==========================================================================================
    // Degenerate plans stay valid JSON and cost nothing
    // ==========================================================================================

    [Fact]
    public async Task An_empty_plan_completes_with_EMPTY_PLAN_and_touches_no_slot_of_the_gate()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient();
        var workspace = UploadingWorkspace(new UploadLog());

        var config = PlanConfig();
        var plan = PlanJson();

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        Meta(result)["note"]!.GetValue<string>().Should().Be("EMPTY_PLAN");
        Meta(result)["clipsGenerated"]!.GetValue<int>().Should().Be(0);
        JsonNode.Parse(result.Output)!["clips"]!.AsArray().Should().BeEmpty();

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        using var db = factory.CreateDbContext();
        db.Set<ExternalGenerationJob>().Count().Should().Be(0, "an empty plan buys nothing");
    }

    [Fact]
    public async Task An_unresolvable_plan_reference_fails_CONFIG_INVALID_with_zero_submits_and_valid_json()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());

        var config = PlanConfig();
        // No history entry for StepOrder 7 exists.
        var (executor, context) = BuildPlanExecutor(
            factory, client, workspace, config, planJson: null, planStepOrder: 7);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("CONFIG_INVALID");
        ((Func<JsonNode?>)(() => JsonNode.Parse(result.Output))).Should().NotThrow("output_json is jsonb — the executor always emits valid JSON");

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==========================================================================================
    // T-105: Inline first/last frame images (an ExtractFrame step's output) and the ConformHeight default
    // ==========================================================================================

    private static readonly string FrameKey = $"projects/{ProjectId}/outputFiles/{ExecutionId}/step-2-frame.png";

    /// <summary>A well-formed 8x8 PNG header: what the step's image sniffing accepts as a PNG.</summary>
    private static readonly byte[] PngFrameBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0, 0, 0, 8, 0, 0, 0, 8, 8, 6, 0, 0, 0
    ];

    private static string InlineFrameConfig(bool allowEgress, bool withLast = false)
    {
        VideoGenerateStepConfig cfg = Defaults(allowSourceMediaEgress: allowEgress);
        cfg.PlanSource = VideoGeneratePlanSource.Inline;
        cfg.Plan = null;
        cfg.InlinePrompt = "Continue from this frame";
        cfg.FirstFrame = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2);
        if (withLast)
        {
            cfg.LastFrame = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2);
        }

        return JsonSerializer.Serialize(cfg, ConfigJsonOptions);
    }

    /// <summary>
    /// Serves the frame through the BOUNDED download (S-67): the step states its cap, and refuses an object whose own
    /// length is already over it before a byte is read.
    /// </summary>
    private static void ServeFrame(Mock<IProjectFileWorkspace> workspace, byte[] bytes) =>
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), FrameKey, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, long, CancellationToken>((_, _, dest, maxBytes, _) =>
            {
                if (bytes.Length > maxBytes) throw new StorageObjectTooLargeException(maxBytes, bytes.Length);
                File.WriteAllBytes(dest, bytes);
                return Task.CompletedTask;
            });

    private static void AddFrameStep(StepExecutionContext context) =>
        ((List<StepOutputHistoryEntry>)context.StepOutputHistory)
            .Add(new StepOutputHistoryEntry(2, "Frame", "{}", OutputStorageKey: FrameKey));

    [Fact]
    public async Task Inline_first_frame_without_egress_consent_fails_EGRESS_REFUSED_before_any_read_or_submit()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());
        ServeFrame(workspace, PngFrameBytes);

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, InlineFrameConfig(allowEgress: false), planJson: null);
        AddFrameStep(context);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("EGRESS_REFUSED");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "no byte is read before consent");
        workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never, "no byte is read before consent");
        using var db = factory.CreateDbContext();
        db.Set<ExternalGenerationJob>().Count().Should().Be(0);
    }

    [Fact]
    public async Task Inline_first_frame_with_consent_rides_the_request_as_Images_0()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = SubmittingClient(submitted, "r1");
        var workspace = UploadingWorkspace(new UploadLog());
        ServeFrame(workspace, PngFrameBytes);

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, InlineFrameConfig(allowEgress: true), planJson: null);
        AddFrameStep(context);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        submitted.Should().ContainSingle();
        submitted[0].Images.Should().NotBeNull().And.ContainSingle();
        submitted[0].Images![0].ContentType.Should().Be("image/png");
        submitted[0].Images![0].Bytes.Should().Equal(PngFrameBytes);
        Meta(result)["frameInputs"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task Inline_frames_to_a_model_without_image_input_fail_KEYFRAME_NOT_SUPPORTED_with_zero_submits()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = ClientDeclaring(VideoImageInputSupport.None);
        var workspace = UploadingWorkspace(new UploadLog());
        ServeFrame(workspace, PngFrameBytes);

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, InlineFrameConfig(allowEgress: true), planJson: null);
        AddFrameStep(context);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        Meta(result)["failed"]!.GetValue<string>().Should().Be("KEYFRAME_NOT_SUPPORTED");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Inline_first_and_last_frame_to_a_first_frame_only_model_fail_KEYFRAME_NOT_SUPPORTED()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = ClientDeclaring(VideoImageInputSupport.FirstFrame);
        var workspace = UploadingWorkspace(new UploadLog());
        ServeFrame(workspace, PngFrameBytes);

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, InlineFrameConfig(allowEgress: true, withLast: true), planJson: null);
        AddFrameStep(context);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        Meta(result)["failed"]!.GetValue<string>().Should().Be("KEYFRAME_NOT_SUPPORTED");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Inline_frame_naming_a_step_with_no_image_output_fails_FRAME_INPUT_NOT_FOUND()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, InlineFrameConfig(allowEgress: true), planJson: null);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        Meta(result)["failed"]!.GetValue<string>().Should().Be("FRAME_INPUT_NOT_FOUND");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Inline_frame_that_is_not_an_image_fails_FRAME_INPUT_INVALID()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());
        ServeFrame(workspace, "not an image"u8.ToArray());

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, InlineFrameConfig(allowEgress: true), planJson: null);
        AddFrameStep(context);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        Meta(result)["failed"]!.GetValue<string>().Should().Be("FRAME_INPUT_INVALID");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// QA-P1-1 (with S-67): the step output is tenant-visible, so meta.frameInputs records what was SENT — the role,
    /// the type the bytes were sniffed as and their size — and never the storage key or any part of it.
    /// </summary>
    [Fact]
    public async Task Inline_frame_meta_records_the_role_and_type_and_never_the_storage_key()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());
        ServeFrame(workspace, PngFrameBytes);

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, InlineFrameConfig(allowEgress: true), planJson: null);
        AddFrameStep(context);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        JsonArray frames = Meta(result)["frameInputs"]!.AsArray();
        JsonObject entry = frames.Should().ContainSingle().Subject!.AsObject();
        entry["role"]!.GetValue<string>().Should().Be("firstFrame");
        entry["contentType"]!.GetValue<string>().Should().Be("image/png");
        entry["bytes"]!.GetValue<int>().Should().Be(PngFrameBytes.Length);
        entry.Should().NotContainKey("storageKey");
        result.Output.Should().NotContain(FrameKey).And.NotContain("storageKey").And.NotContain("outputFiles");
        result.ErrorDetails.Should().BeNull();
    }

    /// <summary>
    /// S-67: the cap is enforced WHILE the object is read, not after it is on disk. The step asks the workspace for a
    /// bounded download (its 20 MB cap), so an object whose own length is already over it is refused with nothing
    /// read — and the refusal is the author's to fix (FRAME_INPUT_INVALID), with no key in it.
    /// </summary>
    [Fact]
    public async Task An_inline_frame_over_the_cap_is_refused_by_the_bounded_download_before_any_body_read()
    {
        const long cap = 20L * 1024 * 1024;
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());
        bool served = false;
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), FrameKey, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, long, CancellationToken>((_, _, _, maxBytes, _) =>
            {
                served = true;
                maxBytes.Should().Be(cap, "the step states its frame cap to the download, so the bytes are never written");
                throw new StorageObjectTooLargeException(maxBytes, 40L * 1024 * 1024);
            });

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, InlineFrameConfig(allowEgress: true), planJson: null);
        AddFrameStep(context);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        served.Should().BeTrue();
        Meta(result)["failed"]!.GetValue<string>().Should().Be("FRAME_INPUT_INVALID");
        Meta(result)["error"]!.GetValue<string>().Should().Contain("firstFrame").And.Contain("larger than");
        result.Output.Should().NotContain(FrameKey).And.NotContain("storageKey");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// S-67: any other download failure — an auth error, a missing object, a transport failure — answers ONE fixed
    /// sentence with its own code, and the exception's own text (an internal host, a bucket, a key) stays in the
    /// server log. The failure is ours, so the step is refunded (FRAME_INPUT_UNAVAILABLE, PlatformInternal).
    /// </summary>
    [Fact]
    public async Task An_inline_frame_whose_download_fails_answers_a_fixed_sentence_and_keeps_the_detail_server_side()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), FrameKey, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException($"GET {FrameKey} from minio.internal:9000 failed in bucket reelbolt-prod-media"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, InlineFrameConfig(allowEgress: true), planJson: null);
        AddFrameStep(context);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        Meta(result)["failed"]!.GetValue<string>().Should().Be("FRAME_INPUT_UNAVAILABLE");
        string error = Meta(result)["error"]!.GetValue<string>();
        error.Should().StartWith("firstFrame: the image could not be read from storage");
        error.Should().NotContain("minio.internal").And.NotContain("reelbolt-prod-media").And.NotContain("IOException")
            .And.NotContain("projects/");
        result.Output.Should().NotContain("minio.internal").And.NotContain("reelbolt-prod-media");
        (result.ErrorDetails ?? string.Empty).Should().NotContain("minio.internal");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_unset_ConformHeight_defaults_to_the_runs_explicit_resolution_and_reports_it()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());
        string config = JsonSerializer.Serialize(
            new VideoGenerateStepConfig
            {
                InlinePrompt = "A shot",
                MaxSpendCredits = 100000m,
                Resolution = Resolution,
                Ratio = Ratio,
                DurationSeconds = 6,
                TimeoutSeconds = 60,
                PollIntervalSeconds = 1
            }, ConfigJsonOptions);

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, planJson: null);
        context.Execution.OutputResolution = 1080;

        StepExecutionResult result = await executor.ExecuteAsync(context);

        JsonObject conform = Meta(result)["conformHeight"]!.AsObject();
        conform["height"]!.GetValue<int>().Should().Be(1080);
        conform["source"]!.GetValue<string>().Should().Be("run");
    }

    [Fact]
    public async Task A_run_on_Auto_adds_no_conformHeight_to_the_output()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());
        string config = JsonSerializer.Serialize(
            new VideoGenerateStepConfig
            {
                InlinePrompt = "A shot",
                MaxSpendCredits = 100000m,
                Resolution = Resolution,
                Ratio = Ratio,
                DurationSeconds = 6,
                TimeoutSeconds = 60,
                PollIntervalSeconds = 1
            }, ConfigJsonOptions);

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, planJson: null);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        Meta(result).AsObject().ContainsKey("conformHeight").Should().BeFalse();
    }

    /// <summary>S-39: a plan project file that cannot be read fails with the code and a fixed sentence; the storage
    /// exception's text (an internal host:port here) stays in the server log, never in meta.error/ErrorDetails.</summary>
    [Fact]
    public async Task An_unreadable_plan_project_file_fails_with_a_fixed_sentence_not_the_exception_text()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());
        workspace
            .Setup(w => w.ReadFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused (minio.internal:9000)"));

        var cfg = Defaults();
        cfg.PlanSource = VideoGeneratePlanSource.ProjectFile;
        cfg.Plan = null;
        cfg.PlanProjectFileId = Guid.NewGuid();
        string config = JsonSerializer.Serialize(cfg, ConfigJsonOptions);

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, planJson: null);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("CONFIG_INVALID");
        string error = Meta(result)["error"]!.GetValue<string>();
        error.Should().StartWith("The plan project file could not be read");
        error.Should().NotContain("minio.internal").And.NotContain("9000").And.NotContain("Connection refused");
        result.Output.Should().NotContain("minio.internal");
        (result.ErrorDetails ?? string.Empty).Should().NotContain("minio.internal");
    }

    /// <summary>S-39: an analysis artifact whose download fails is refused with a fixed sentence; neither the exception
    /// type nor its text (an internal address) reaches meta.error/ErrorDetails.</summary>
    [Fact]
    public async Task An_unreadable_analysis_artifact_fails_with_a_fixed_sentence_not_the_exception_text()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SubmittingClient("r1");
        var workspace = UploadingWorkspace(new UploadLog());
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisStorageKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Read from 10.0.4.7:9000 failed: bucket reelbolt-prod-media"));

        var config = PlanConfig(
            allowSourceMediaEgress: true,
            analysis: new ExtractInputRef(ExtractInputSource.Step, StepOrder: AnalysisStepOrder));
        var plan = PlanJson(Shot("Shot keyed off the previous frame", "Short", firstFrame: "s1"));

        var (executor, context) = BuildPlanExecutor(factory, client, workspace, config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        Meta(result)["failed"]!.GetValue<string>().Should().Be("CONFIG_INVALID");
        string error = Meta(result)["error"]!.GetValue<string>();
        error.Should().StartWith("The analysis artifact could not be read");
        error.Should().NotContain("10.0.4.7").And.NotContain("reelbolt-prod-media").And.NotContain("IOException");
        result.Output.Should().NotContain("10.0.4.7");
        (result.ErrorDetails ?? string.Empty).Should().NotContain("10.0.4.7");
    }

    // ==========================================================================================
    // Plan-shape helpers
    // ==========================================================================================

    private static VideoGenerateStepConfig Defaults(
        int takes = 1,
        int maxClips = 4,
        decimal stepCap = 100000m,
        bool allowSourceMediaEgress = false,
        ExtractInputRef? analysis = null,
        string model = Model) => new()
        {
            PlanSource = VideoGeneratePlanSource.StepRef,
            Plan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: PlanStepOrder),
            Analysis = analysis,
            Model = model,
            Resolution = Resolution,
            Ratio = Ratio,
            DurationSeconds = 6,
            Takes = takes,
            MaxClips = maxClips,
            MaxSpendCredits = stepCap,
            AllowSourceMediaEgress = allowSourceMediaEgress,
            TimeoutSeconds = 60,
            PollIntervalSeconds = 1
        };

    private static string PlanConfig(
        int takes = 1,
        int maxClips = 4,
        decimal stepCap = 100000m,
        bool allowSourceMediaEgress = false,
        ExtractInputRef? analysis = null,
        string model = Model) =>
        JsonSerializer.Serialize(
            Defaults(takes, maxClips, stepCap, allowSourceMediaEgress, analysis, model), ConfigJsonOptions);

    private static ShotPlan Shot(
        string prompt,
        string duration,
        string purpose = "Cutaway",
        string anchor = "s1",
        string camera = "",
        string? firstFrame = null,
        string? lastFrame = null) => new()
        {
            Purpose = purpose,
            Anchor = anchor,
            FirstFrame = firstFrame,
            LastFrame = lastFrame,
            Camera = camera,
            Duration = duration,
            Prompt = prompt,
            Reason = "test"
        };

    private static string PlanJson(params ShotPlan[] shots) =>
        JsonSerializer.Serialize(new GeneratedShotPlanOutput { Shots = shots.ToList(), PlanRationale = "test" }, ConfigJsonOptions);

    // ==========================================================================================
    // Executor / context harness (mirrors VideoGenerateStepExecutorTests' own, self-contained)
    // ==========================================================================================

    private static readonly VideoEditingOptions VideoOptions =
        new() { AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 };

    private static readonly MiniMaxVideoGenerationClient PricingOnlyMiniMax =
        new(new Mock<HttpClient>().Object, "key123");

    /// <summary>
    /// A client whose <c>Describe</c> is the real, pure MiniMax table and whose submit/poll/download
    /// are mocks — no paid API is ever reachable from these tests.
    /// </summary>
    private static Mock<IVideoGenerationClient> MiniMaxDescribingClient()
    {
        var client = new Mock<IVideoGenerationClient>();
        client
            .Setup(c => c.Describe(It.IsAny<string>()))
            .Returns((string _) => PricingOnlyMiniMax.Describe("MiniMax-H3"));
        client
            .As<IVideoImageInputSupport>()
            .Setup(s => s.ImageInputSupport(It.IsAny<string>()))
            .Returns(VideoImageInputSupport.FirstAndLastFrame);
        return client;
    }

    private static Mock<IVideoGenerationClient> SubmittingClient(params string[] remoteIds) =>
        SubmittingClient(new List<VideoGenerationRequest>(), remoteIds);

    private static Mock<IVideoGenerationClient> ClientDeclaring(VideoImageInputSupport support)
    {
        var client = MiniMaxDescribingClient();
        client
            .As<IVideoImageInputSupport>()
            .Setup(s => s.ImageInputSupport(It.IsAny<string>()))
            .Returns(support);
        return client;
    }

    private static Mock<IVideoGenerationClient> SubmittingClient(List<VideoGenerationRequest> submitted, params string[] remoteIds)
    {
        var client = MiniMaxDescribingClient();
        int i = 0;
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<VideoGenerationRequest, CancellationToken>((r, _) => submitted.Add(r))
            .ReturnsAsync(() => new ExternalJobHandle(remoteIds[i++]));
        client
            .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalJobHandle h, CancellationToken _) =>
                new ExternalJobStatus(ExternalJobState.Succeeded, $"https://example.invalid/{h.RemoteJobId}.mp4", null));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4]));
        return client;
    }

    /// <summary>
    /// A working ffprobe+ffmpeg runner so <see cref="GeneratedClipNormalizer"/> really delivers a
    /// clip instead of tripping over an unconfigured mock member.
    /// </summary>
    private static Mock<IVideoToolRunner> WorkingRunner()
    {
        const string probeJson = """
            { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480,
              "r_frame_rate": "30/1", "duration": "6" } ], "format": { "duration": "6" } }
            """;
        var runner = new Mock<IVideoToolRunner>();
        runner
            .Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: probeJson, StdErr: "", TimedOut: false));
        runner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));
        runner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));
        return runner;
    }

    /// <summary>Everything the tests want to know about what the step uploaded.</summary>
    private sealed class UploadLog
    {
        public List<(string Name, string Category, string ContentType)> Calls { get; } = new();
        public string? ManifestJson { get; set; }
    }

    /// <summary>
    /// A workspace mock that answers every artifact upload with a realistic
    /// <c>projects/{projectId}/agentFiles/{name}</c> key, and records the manifest's exact bytes
    /// while the temp file still exists (the executor deletes it in a <c>finally</c>).
    /// </summary>
    private static Mock<IProjectFileWorkspace> UploadingWorkspace(UploadLog log)
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>(
                (_, localPath, name, contentType, _, category) =>
                {
                    log.Calls.Add((name, category, contentType));
                    if (contentType == "application/json")
                    {
                        log.ManifestJson = File.ReadAllText(localPath);
                    }
                })
            .ReturnsAsync((Guid projectId, string _, string name, string _, CancellationToken _, string _) =>
                $"projects/{projectId:D}/agentFiles/{name}");
        return workspace;
    }

    /// <summary>The storage key a one-shot artifact's source 0 really is.</summary>
    private const string SourceStorageKey =
        "projects/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/userFiles/99999999-8888-7777-6666-555555555555/source.mp4";

    /// <summary>
    /// Serves an analysis artifact that OFFERS the given ids and places none of them — the shape every
    /// test that only cares about the offer needs.
    /// </summary>
    private static void DownloadAnalysis(Mock<IProjectFileWorkspace> workspace, string[] offeredIds) =>
        DownloadArtifact(workspace, Artifact(SourceStorageKey, [], offeredIds));

    /// <summary>
    /// Serves an explicit artifact for the key the history entry names. The frame tests need real
    /// shots and sources, because resolving an opaque id to a moment is exactly what the artifact's
    /// own <c>Shots</c>/<c>Sources</c> are for.
    /// </summary>
    private static void DownloadArtifact(Mock<IProjectFileWorkspace> workspace, VideoAnalysisArtifact artifact)
    {
        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactJsonOptions);

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisStorageKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });
    }

    private static VideoAnalysisArtifact Artifact(
        string sourceStorageKey, IReadOnlyList<VideoAnalysisShot> shots, IReadOnlyList<string> offeredIds) =>
        new(
            Version: 2,
            Media: new VideoAnalysisMedia(30, 30, 1, 1920, 1080),
            Shots: shots,
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: offeredIds,
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Sources: [new VideoAnalysisSourceInfo(0, sourceStorageKey, new VideoAnalysisMedia(30, 30, 1, 1920, 1080))]);

    private static (VideoGenerateStepExecutor Executor, StepExecutionContext Context) BuildPlanExecutor(
        SharedInMemoryDbFactory factory,
        Mock<IVideoGenerationClient> client,
        Mock<IProjectFileWorkspace> workspace,
        string configJson,
        string? planJson,
        int planStepOrder = PlanStepOrder,
        InferenceProviderKind kind = InferenceProviderKind.MiniMax,
        string modelName = Model,
        IVideoGenerationBudgetGate? gate = null,
        FrameFixture? frames = null)
    {
        frames ??= new FrameFixture();

        var provider = new ResolvedVideoGenerationProvider(
            ProviderId, "MiniMax", kind, "https://api.minimax.io", modelName, "key123", 1800);

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var clientFactory = new Mock<IVideoGenerationClientFactory>();
        clientFactory.Setup(f => f.Get(provider)).Returns(client.Object);

        var normalizer = new GeneratedClipNormalizer(
            WorkingRunner().Object, Options.Create(VideoOptions), NullLogger<GeneratedClipNormalizer>.Instance);

        var executor = new VideoGenerateStepExecutor(
            gate ?? new VideoGenerationBudgetGate(
                factory,
                Options.Create(new VideoGenerationOptions { DailyBudgetCredits = 1_000_000m, GlobalDailyBudgetCredits = 1_000_000m })),
            resolver.Object,
            clientFactory.Object,
            workspace.Object,
            factory,
            normalizer,
            Options.Create(new VideoGenerationOptions()),
            NullLogger<VideoGenerateStepExecutor>.Instance,
            frames.Factory,
            frames.Extractor);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = configJson
        };

        var history = new List<StepOutputHistoryEntry>
        {
            new(AnalysisStepOrder, "Analyze", """{"view":{},"meta":{}}""", OutputStorageKey: null, ArtifactStorageKey: AnalysisStorageKey),
        };
        if (planJson is not null)
        {
            history.Add(new StepOutputHistoryEntry(planStepOrder, "ShotDirector", planJson, OutputStorageKey: null, ArtifactStorageKey: null));
        }

        var context = new StepExecutionContext
        {
            StepResultId = StepResultId,
            Execution = new WorkflowExecution { Id = ExecutionId, ProjectId = ProjectId, Status = ExecutionStatus.Running },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = planJson ?? string.Empty,
            CurrentStepIndex = history.Count,
            IterationCount = 0,
            CorrelationId = "test-correlation",
            StepOutputHistory = history,
            CancellationToken = CancellationToken.None
        };

        return (executor, context);
    }

    private static JsonNode Meta(StepExecutionResult result) => JsonNode.Parse(result.Output)!["meta"]!;

    private static JsonObject? manifestRoot(UploadLog log) =>
        log.ManifestJson is null ? null : (JsonObject)JsonNode.Parse(log.ManifestJson)!;

    // ==========================================================================================
    // Plan path under the async price oracle (the async price oracle, T-902)
    // ==========================================================================================
    //
    // Two properties the plan path has to keep, and they pull in opposite directions:
    //   * a provider that PUBLISHES a table keeps its whole published capability set, so Short and
    //     Long still mean the model's own minimum and maximum — the static arm must NOT go through
    //     the oracle extension's static branch, which would collapse that range to a POINT;
    //   * an oracle-priced model is priced at the ONE configuration the vendor answered for, and
    //     every take is charged, and recorded at, the vendor's OWN figure.
    // Every oracle below is an in-memory Moq proxy: no network call, no vendor API key, and none is
    // read from the environment.

    private const string HiggsfieldPlanResolution = "1080P";

    /// <summary>
    /// The vendor's per-workflow endpoint id. It IS the "model" on this provider — which is what
    /// <c>POST /estimate/{endpoint_id}</c> is addressed by.
    /// </summary>
    private const string HiggsfieldEndpointId = "hf-ep-9f1c2b";

    /// <summary>The duration the vendor's quote answers for — the point the plan snaps every word to.</summary>
    private const int QuotedPlanDurationSeconds = 5;

    private const decimal QuotedPlanTotalUsd = 5.00m;

    /// <summary>
    /// MIMAX UNCHANGED — the regression guard for the design decision the basis deliberately made.
    /// <para>
    /// A client that is NOT an oracle (MiniMax) keeps the FULL published capability set: with a
    /// published 5..10s range, a plan whose shots say <c>Short</c> and <c>Long</c> dispatches at 5s
    /// and 10s. Had <c>ResolveAsync</c> taken the oracle extension's static branch instead, the
    /// capabilities would be a point at the config's inline <c>DurationSeconds</c> (6) and BOTH
    /// takes would have gone out at 6s — losing the plan's Short/Long vocabulary for every
    /// table-priced provider, which is a regression this test exists to catch.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_non_oracle_client_keeps_the_full_published_capability_set()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var client = StaticRangeClient(PublishedRange(), submitted, "r1", "r2");

        // DurationSeconds 6 is the INLINE duration and sits inside the published range; it is
        // deliberately neither the minimum nor the maximum.
        var config = RangePlanConfig(durationSeconds: 6);
        var plan = PlanJson(Shot("A short shot", "Short"), Shot("A long shot", "Long"));

        var (executor, context) = BuildPlanExecutor(
            factory, client, UploadingWorkspace(new UploadLog()), config, plan);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        submitted.Should().HaveCount(2);
        submitted.Select(r => r.DurationSeconds).Should().Equal(
            new[] { 5, 10 },
            "Short snaps to the published minimum and Long to the published maximum");
        submitted.Should().OnlyContain(
            r => r.DurationSeconds != 6,
            "the static arm must not collapse the published range to the point ToCapabilities() yields");

        using var db = factory.CreateDbContext();
        var rows = db.ExternalGenerationJobs.OrderBy(r => r.CreatedAt).ToList();
        rows.Should().HaveCount(2);
        rows.Select(r => r.CostEstimate).Should().Equal(
            new[] { CreditCurrency.CreditsFromUsd(PerSecond768P * 5), CreditCurrency.CreditsFromUsd(PerSecond768P * 10) },
            "the per-clip figure follows the snapped duration, not the inline one");
    }

    /// <summary>
    /// The arithmetic twin of the test above: with <c>Quote == null</c> the whole-plan estimate is
    /// <see cref="ShotPlanCostEstimator.EstimateWholePlanUsd"/>'s own number, verbatim — not a
    /// re-implementation that could drift from it.
    /// </summary>
    [Fact]
    public async Task The_whole_plan_estimate_with_no_quote_is_the_published_estimators_own_number()
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var capabilities = PublishedRange();
        var client = StaticRangeClient(capabilities, submitted, "r1", "r2", "r3");

        var shots = new[]
        {
            Shot("A short shot", "Short"),
            Shot("A medium shot", "Medium"),
            Shot("A long shot", "Long")
        };

        var (executor, context) = BuildPlanExecutor(
            factory, client, UploadingWorkspace(new UploadLog()), RangePlanConfig(), PlanJson(shots));

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);

        decimal fromEstimator = ShotPlanCostEstimator.EstimateWholePlanUsd(
            capabilities, Resolution, shots, takes: 1);

        Meta(result)["planEstimated"]!.GetValue<decimal>().Should().Be(
            CreditCurrency.CreditsFromUsd(fromEstimator), "the non-oracle arm delegates to the shared estimator and converts its USD figure to credits");
        fromEstimator.Should().Be(PerSecond768P * (5 + 7 + 10), "Short=5s, Medium=7s, Long=10s at $0.08/s");
    }

    /// <summary>
    /// PLAN PATH UNDER AN ORACLE — every planned shot dispatches at the POINT the quote priced
    /// (the only duration the vendor answered for), every take's reservation carries the vendor's
    /// own figure together with the quote, and <c>meta.planEstimated</c> stays the exact sum of
    /// the takes handed to the gate. Both the single-take and the multi-take case are pinned: an
    /// off-by-takes error would show up only when a plan has more than one take per shot.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Every_planned_shot_under_an_oracle_quote_dispatches_at_the_point_the_quote_priced(int takes)
    {
        var factory = new SharedInMemoryDbFactory();
        var submitted = new List<VideoGenerationRequest>();
        var quote = PlanVendorQuote();
        var client = HiggsfieldPlanClient(quote, submitted, "hf-1", "hf-2", "hf-3", "hf-4");

        var gate = new RecordingBudgetGate(new VideoGenerationBudgetGate(
            factory,
            Options.Create(new VideoGenerationOptions { DailyBudgetCredits = 1_000_000m, GlobalDailyBudgetCredits = 1_000_000m })));

        // Two shots (Short and Long) x `takes` takes each: the quote priced ONE duration, so both
        // words must land on it however many takes the step orders.
        var config = HiggsfieldPlanConfig(takes: takes, maxClips: 4);
        var plan = PlanJson(Shot("First planned shot", "Short"), Shot("Second planned shot", "Long"));

        var (executor, context) = BuildPlanExecutor(
            factory, client, UploadingWorkspace(new UploadLog()), config, plan,
            kind: InferenceProviderKind.Higgsfield,
            modelName: HiggsfieldEndpointId,
            gate: gate);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        int expectedClips = 2 * takes;

        result.Status.Should().Be(StepStatus.Completed);
        client.Verify(
            c => c.Describe(It.IsAny<string>()),
            Times.Never,
            "an oracle-priced model is priced by the vendor's own quote, never by the fail-closed table");

        submitted.Should().HaveCount(expectedClips);
        submitted.Should().OnlyContain(
            r => r.DurationSeconds == QuotedPlanDurationSeconds,
            "the quote priced a POINT — 5s is the only duration the vendor answered for, so every shot snaps to it");

        gate.Requests.Should().ContainSingle("one atomic reservation for the whole step");
        var reservedTakes = gate.Requests[0].Takes;
        reservedTakes.Should().HaveCount(expectedClips);
        reservedTakes.Should().OnlyContain(t => t.CostEstimate == CreditCurrency.CreditsFromUsd(QuotedPlanTotalUsd));
        reservedTakes.Should().OnlyContain(t => t.Quote != null, "an oracle-priced take always carries the quote it was charged from");

        decimal planEstimate = Meta(result)["planEstimated"]!.GetValue<decimal>();
        planEstimate.Should().Be(
            CreditCurrency.CreditsFromUsd(QuotedPlanTotalUsd * expectedClips),
            "meta.planEstimated is the whole-plan USD sum converted once to credits");

        using var db = factory.CreateDbContext();
        var rows = db.ExternalGenerationJobs.ToList();
        rows.Should().HaveCount(expectedClips);
        rows.Should().OnlyContain(r => r.CostEstimate == CreditCurrency.CreditsFromUsd(QuotedPlanTotalUsd));
        rows.Sum(r => r.CostEstimate).Should().Be(reservedTakes.Sum(t => t.CostEstimate));
        rows.Should().OnlyContain(
            r => r.CostEstimate > 0m,
            "the step cannot record a non-positive estimate on this path either: the quote type cannot represent one");
    }

    // ---- oracle plan-path fixtures ----

    /// <summary>
    /// A plan-sourced config for the Higgsfield-shaped provider: the endpoint id IS the model, and
    /// the inline duration is the one the vendor's quote answers for.
    /// </summary>
    private static string HiggsfieldPlanConfig(
        int takes = 1, int maxClips = 4, decimal stepCap = 100000m,
        int durationSeconds = QuotedPlanDurationSeconds) =>
        JsonSerializer.Serialize(new VideoGenerateStepConfig
        {
            PlanSource = VideoGeneratePlanSource.StepRef,
            Plan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: PlanStepOrder),
            Model = HiggsfieldEndpointId,
            Resolution = HiggsfieldPlanResolution,
            Ratio = Ratio,
            DurationSeconds = durationSeconds,
            Takes = takes,
            MaxClips = maxClips,
            MaxSpendCredits = stepCap,
            TimeoutSeconds = 60,
            PollIntervalSeconds = 1
        }, ConfigJsonOptions);

    /// <summary>A plan-sourced config over the published 5..10s table MiniMax answers with.</summary>
    private static string RangePlanConfig(
        int takes = 1, int maxClips = 4, decimal stepCap = 100000m, int durationSeconds = 6) =>
        JsonSerializer.Serialize(new VideoGenerateStepConfig
        {
            PlanSource = VideoGeneratePlanSource.StepRef,
            Plan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: PlanStepOrder),
            Model = Model,
            Resolution = Resolution,
            Ratio = Ratio,
            DurationSeconds = durationSeconds,
            Takes = takes,
            MaxClips = maxClips,
            MaxSpendCredits = stepCap,
            TimeoutSeconds = 60,
            PollIntervalSeconds = 1
        }, ConfigJsonOptions);

    /// <summary>
    /// The full published capability set a table-priced provider offers: a whole 5..10s duration
    /// RANGE, not a point — which is exactly what the plan path needs to keep Short and Long.
    /// </summary>
    private static VideoGenerationCapabilities PublishedRange() =>
        new(
            new Dictionary<string, decimal>(StringComparer.Ordinal) { [Resolution] = PerSecond768P },
            MinDurationSeconds: 5,
            MaxDurationSeconds: 10,
            AllowedRatios: new[] { Ratio });

    /// <summary>The vendor's own quote for one clip at the point the plan will snap every shot to.</summary>
    private static VideoGenerationPriceQuote PlanVendorQuote() =>
        new(
            HiggsfieldEndpointId,
            HiggsfieldPlanResolution,
            Ratio,
            QuotedPlanDurationSeconds,
            pricePerSecondUsd: QuotedPlanTotalUsd / QuotedPlanDurationSeconds,
            totalUsd: QuotedPlanTotalUsd,
            credits: 12,
            source: VideoGenerationPriceSource.VendorEstimate);

    /// <summary>A table-priced client with mock deliveries — no HTTP handler exists behind it.</summary>
    private static Mock<IVideoGenerationClient> StaticRangeClient(
        VideoGenerationCapabilities capabilities, List<VideoGenerationRequest> submitted, params string[] remoteIds)
    {
        var client = new Mock<IVideoGenerationClient>();
        client.Setup(c => c.Describe(It.IsAny<string>())).Returns(capabilities);
        PlanDeliveries(client, submitted, remoteIds);
        return client;
    }

    /// <summary>
    /// A Higgsfield-shaped client: <c>Describe</c> throws (no price table exists, which is why the
    /// model offered zero models before option B) and the async oracle answers with the quote.
    /// </summary>
    private static Mock<IVideoGenerationClient> HiggsfieldPlanClient(
        VideoGenerationPriceQuote quote, List<VideoGenerationRequest> submitted, params string[] remoteIds)
    {
        var client = new Mock<IVideoGenerationClient>();
        client
            .Setup(c => c.Describe(It.IsAny<string>()))
            .Throws(new NotSupportedException("Higgsfield publishes no per-second price table."));
        client.As<IVideoGenerationPriceOracle>()
            .Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(quote);
        PlanDeliveries(client, submitted, remoteIds);
        return client;
    }

    private static void PlanDeliveries(
        Mock<IVideoGenerationClient> client, List<VideoGenerationRequest> submitted, params string[] remoteIds)
    {
        var i = 0;
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<VideoGenerationRequest, CancellationToken>((r, _) => submitted.Add(r))
            .ReturnsAsync(() => new ExternalJobHandle(remoteIds[i++]));
        client
            .Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalJobHandle h, CancellationToken _) =>
                new ExternalJobStatus(ExternalJobState.Succeeded, $"https://example.invalid/{h.RemoteJobId}.mp4", null));
        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4]));
    }

    // ==========================================================================================
    // Frame transport fixtures (the plan path's image half)
    // ==========================================================================================

    /// <summary>
    /// The media session the plan path cuts its frames in, plus the extractor that drives it. One
    /// instance per test, so the test can assert on exactly what was staged, what ffmpeg was asked
    /// for, and — in the refusal tests — that NOTHING was.
    /// </summary>
    private sealed class FrameFixture
    {
        public FrameFixture()
        {
            Factory = new StubWorkspaceFactory(Session);
            Extractor = new FfmpegKeyframeExtractor(
                new Mock<IVideoToolRunner>(MockBehavior.Strict).Object,
                Options.Create(VideoOptions),
                NullLogger<FfmpegKeyframeExtractor>.Instance);
        }

        public SessionWorkspace Session { get; } = new();

        /// <summary>The concrete stub, not the interface: a test asserts whether a session was asked for at all.</summary>
        public StubWorkspaceFactory Factory { get; }

        public IKeyframeExtractor Extractor { get; }
    }

    /// <summary>
    /// The frame bytes every stub "ffmpeg" run writes: a recognizable JPEG-looking payload, so a test
    /// can assert that the bytes on the provider request are the bytes the media session produced and
    /// not, say, an empty array.
    /// </summary>
    private static readonly byte[] FrameBytes =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00];

    /// <summary>
    /// An in-memory media session shaped like a REMOTE one where it matters: <c>MaterializeAsync</c>
    /// answers the virtual path UNCHANGED, so a green test is evidence that every stage, decode and
    /// read went through the session rather than through a path on this machine.
    /// </summary>
    private sealed class SessionWorkspace : IMediaWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public List<string> Staged { get; } = [];

        /// <summary>Every ffmpeg argv the extractor asked this session to run, in order.</summary>
        public List<string[]> FfmpegCalls { get; } = [];

        /// <summary>Non-zero makes every ffmpeg run fail, which is how "the frame could not be cut" is exercised.</summary>
        public int FfmpegExitCode { get; set; }

        public string RootPath => "session:/";

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct)
        {
            Staged.Add(virtualPath);
            Files.TryAdd(virtualPath, [0x00, 0x01, 0x02]);
            return Task.CompletedTask;
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
            => RunFfmpegAsync(argv, timeout, ct, _ => { });

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        {
            FfmpegCalls.Add(argv.ToArray());
            if (FfmpegExitCode == 0)
            {
                // A real encoder writes the output file the caller stats and reads back.
                Files[argv[^1]] = FrameBytes;
            }

            return Task.FromResult(new VideoToolResult(FfmpegExitCode, string.Empty, "stderr", false));
        }

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct) => Task.FromResult(virtualPath);

        public Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct)
        {
            Files[virtualPath] = content;
            return Task.CompletedTask;
        }

        public Task WriteTextAsync(string virtualPath, string content, System.Text.Encoding encoding, CancellationToken ct)
            => WriteBytesAsync(virtualPath, encoding.GetBytes(content), ct);

        public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) => ReadAsync(virtualPath, int.MaxValue, ct);

        public Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct) =>
            Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? Task.FromResult(bytes)
                : throw new FileNotFoundException($"Media workspace file '{virtualPath}' does not exist.", virtualPath);

        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? new MediaWorkspaceStat(true, bytes.Length)
                : new MediaWorkspaceStat(false, 0));

        public Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
            => Task.FromResult(finalStorageKey);

        public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
            => Task.FromResult(finalStorageKey);
    }

    /// <summary>
    /// Hands the executor the one media session above. Records whether a session was asked for at all,
    /// which is what makes "no consent ⇒ nothing is extracted" a claim about the session rather than
    /// about the manifest.
    /// </summary>
    private sealed class StubWorkspaceFactory(SessionWorkspace session) : IMediaWorkspaceFactory
    {
        public MediaWorkspaceRequest? Request { get; private set; }

        public int Opened { get; private set; }

        public Task<MediaWorkspaceOpenResult> OpenAsync(MediaWorkspaceRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            Opened++;
            return Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                session,
                scratch: null,
                ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null,
                request.ExecutionId,
                request.StepOrder,
                NullLogger.Instance)));
        }
    }
}
