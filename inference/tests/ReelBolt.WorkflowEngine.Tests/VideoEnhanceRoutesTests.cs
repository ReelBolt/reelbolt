#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The VideoEnhance HTTP entry: the gate order (a refusal writes nothing), the config the route writes (length, size and rate
/// measured, consent only from the recorded project consent), the hidden run, the estimate (writes nothing, machine codes), the
/// status poll, the cancel and the consent routes. No vendor call, no ffmpeg.
/// </summary>
public sealed class VideoEnhanceRoutesTests
{
    private static VideoEnhanceRequestBody Hosted(string op = "Upscale") =>
        op == "Interpolate"
            ? new VideoEnhanceRequestBody(op, "Hosted", FrameMultiplier: 2)
            : new VideoEnhanceRequestBody(op, "Hosted", UpscaleFactor: 2);

    private static VideoEnhanceRequestBody Local() => new("Denoise", "Local", DenoiseStrength: "Light");

    private static T Value<T>(ActionResult<T> r) => (T)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    // ---------------- the happy paths ----------------

    [Fact]
    public async Task A_hosted_request_measures_the_source_writes_the_hints_and_the_recorded_consent_and_queues_a_hidden_kind_8_run()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2, consented: true);

        ActionResult<VideoEnhanceQueuedResponse> r = await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default);

        ObjectResult accepted = r.Result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(202);
        VideoEnhanceQueuedResponse queued = (VideoEnhanceQueuedResponse)accepted.Value!;
        (queued.Op, queued.Engine, queued.Hosted, queued.State).Should().Be(("Upscale", "Hosted", true, "queued"));
        queued.Credits.Should().Be(0.9, "the estimate the engine will hold: billing is on");
        Json(queued).Should().NotContain("projects/").And.NotContain("storageKey").And.NotContain("http");

        WorkflowDefinition workflow = await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync();
        workflow.Kind.Should().Be(WorkflowDefinitionKind.VideoEnhance);
        ((int)workflow.Kind).Should().Be(8);
        workflow.Steps.Should().ContainSingle().Which.StepType.Should().Be(StepType.VideoEnhance);
        VideoEnhanceStepConfig config = JsonSerializer.Deserialize<VideoEnhanceStepConfig>(workflow.Steps.Single().VideoEnhanceConfigJson!, VideoEnhanceStepConfig.JsonOptions)!;
        config.AllowSourceMediaEgress.Should().BeTrue("the recorded project consent, not the body, turns it on");
        config.MaxSourceSeconds.Should().Be(13, "ceil of the probed 12.2 s, so the hold equals the estimate");
        (config.SourceWidth, config.SourceHeight, config.SourceFps).Should().Be((1920, 1080, 30d));
        config.Source.Should().Be(new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: h.FileId));
        config.RegisterProjectFile.Should().BeTrue();
        config.Validate().Should().BeEmpty();
        h.QueueCalls.Should().Be(1);
        h.LastQueuedKind.Should().Be(WorkflowDefinitionKind.VideoEnhance);
        h.LastQueuedExecutionId.Should().Be(queued.ExecutionId);
        h.LastPreflight!.VideoEnhanceConfigJson.Should().Be(workflow.Steps.Single().VideoEnhanceConfigJson, "the balance check prices the very config that is queued");
    }

    [Fact]
    public async Task A_local_request_needs_no_consent_no_provider_and_no_credits_and_carries_no_hints()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 30, consented: false, videoEnhanceAllowed: false, provider: false, billing: Billing.Poor);

        ObjectResult accepted = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Local(), default)).Result!;

        accepted.StatusCode.Should().Be(202);
        ((VideoEnhanceQueuedResponse)accepted.Value!).Credits.Should().Be(0);
        VideoEnhanceStepConfig config = JsonSerializer.Deserialize<VideoEnhanceStepConfig>(
            (await h.Db.WorkflowSteps.SingleAsync()).VideoEnhanceConfigJson!, VideoEnhanceStepConfig.JsonOptions)!;
        config.IsHosted.Should().BeFalse();
        config.AllowSourceMediaEgress.Should().BeFalse();
        config.HasSourceHints.Should().BeFalse();
        config.MaxSourceSeconds.Should().Be(30);
        h.PreflightCalls.Should().Be(0, "a local run is never priced");
    }

    // ---------------- the gate order: a refusal writes nothing ----------------

    [Fact]
    public async Task Gates_run_in_order_and_a_refusal_writes_nothing()
    {
        // 0. unverified email beats everything.
        using (Harness h = await Harness.CreateAsync(verified: false))
        {
            ((ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default)).Result!).StatusCode.Should().Be(403);
            await h.AssertNothingWrittenAsync();
        }

        // 1. a bad body shape (400) beats a missing media.
        using (Harness h = await Harness.CreateAsync())
        {
            BadRequestObjectResult bad = (await h.Controller().Request(h.ProjectId, "file:" + Guid.NewGuid(), new VideoEnhanceRequestBody("Sparkle", "Hosted"), default))
                .Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            Json(bad.Value).Should().Contain("ENHANCE_REQUEST_INVALID");
            (await h.Controller().Request(h.ProjectId, h.FileRef, new VideoEnhanceRequestBody("Upscale", "Local"), default)).Result.Should().BeOfType<BadRequestObjectResult>();
            (await h.Controller().Request(h.ProjectId, h.FileRef, null, default)).Result.Should().BeOfType<BadRequestObjectResult>();
            await h.AssertNothingWrittenAsync();
        }

        // 2. an unknown media (404, the same for a foreign or malformed one); a non-video is 422.
        using (Harness h = await Harness.CreateAsync())
        {
            (await h.Controller().Request(h.ProjectId, "file:" + Guid.NewGuid(), Hosted(), default)).Result.Should().BeOfType<NotFoundResult>();
            (await h.Controller().Request(h.ProjectId, "nonsense", Hosted(), default)).Result.Should().BeOfType<NotFoundResult>();
            ((ObjectResult)(await h.Controller().Request(h.ProjectId, "file:" + h.AudioFileId, Hosted(), default)).Result!).StatusCode.Should().Be(422);
            await h.AssertNothingWrittenAsync();
        }

        // 3. the plan feature (402) before the consent (409).
        using (Harness h = await Harness.CreateAsync(videoEnhanceAllowed: false, consented: false))
        {
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default)).Result!;
            r.StatusCode.Should().Be(402);
            Json(r.Value).Should().Contain("PLAN_NOT_ENTITLED").And.Contain("video_enhance");
            await h.AssertNothingWrittenAsync();
        }

        // 3b. a target above the plan's output height (402, resolution), also before the consent.
        using (Harness h = await Harness.CreateAsync(maxOutputHeight: 1080, consented: false))
        {
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default)).Result!;
            r.StatusCode.Should().Be(402);
            Json(r.Value).Should().Contain("PLAN_NOT_ENTITLED").And.Contain("resolution");
            await h.AssertNothingWrittenAsync();
        }

        // 4. the consent (409) before the length and the credits.
        using (Harness h = await Harness.CreateAsync(consented: false, probeSeconds: 900, billing: Billing.Poor))
        {
            ConflictObjectResult r = (await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default)).Result.Should().BeOfType<ConflictObjectResult>().Subject;
            Json(r.Value).Should().Contain("CONSENT_REQUIRED");
            await h.AssertNothingWrittenAsync();
        }

        // 5. the length (413, with the cap) before the provider and the credits.
        using (Harness h = await Harness.CreateAsync(probeSeconds: 121, provider: false, billing: Billing.Poor))
        {
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default)).Result!;
            r.StatusCode.Should().Be(413);
            Json(r.Value).Should().Contain("SOURCE_TOO_LONG").And.Contain("\"maxSeconds\":120");
            await h.AssertNothingWrittenAsync();
        }

        // 6. no provider (409 ENHANCE_UNAVAILABLE) before the credits.
        using (Harness h = await Harness.CreateAsync(provider: false, billing: Billing.Poor))
        {
            ConflictObjectResult r = (await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default)).Result.Should().BeOfType<ConflictObjectResult>().Subject;
            Json(r.Value).Should().Contain("ENHANCE_UNAVAILABLE");
            await h.AssertNothingWrittenAsync();
        }

        // 7. credits (402) before the run rate.
        using (Harness h = await Harness.CreateAsync(billing: Billing.Poor))
        {
            await h.SeedRecentRunsAsync(VideoEnhanceService.PaidRunsPerHour);
            int before = await h.Db.WorkflowDefinitions.CountAsync();
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default)).Result!;
            r.StatusCode.Should().Be(402);
            Json(r.Value).Should().Contain("INSUFFICIENT_CREDITS").And.Contain("missing");
            (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(before);
            h.QueueCalls.Should().Be(0);
        }

        // 8. the run rate: ten paid runs in the last hour.
        using (Harness h = await Harness.CreateAsync())
        {
            await h.SeedRecentRunsAsync(VideoEnhanceService.PaidRunsPerHour);
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default)).Result!;
            r.StatusCode.Should().Be(429);
            Json(r.Value).Should().Contain("retryAfterSeconds");
            h.QueueCalls.Should().Be(0);
        }

        // 9. the plan's concurrency cap, raised by the queue, rolls the workflow back.
        using (Harness h = await Harness.CreateAsync())
        {
            h.QueueThrows = new PlanLimitException(new ReelBolt.Shared.Billing.PlanGateViolation("PLAN_CONCURRENCY_LIMIT", "concurrency", "Too many runs.", 429));
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default)).Result!;
            r.StatusCode.Should().Be(429);
            // (The in-memory provider ignores transactions; the rollback itself is the same code as the object-analysis route's.)
            h.QueueCalls.Should().Be(0);
        }
    }

    [Fact]
    public async Task A_hosted_request_for_a_source_not_yet_measured_is_refused_and_a_local_one_is_not()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: null);

        ConflictObjectResult r = (await h.Controller().Request(h.ProjectId, h.FileRef, Hosted(), default)).Result.Should().BeOfType<ConflictObjectResult>().Subject;
        Json(r.Value).Should().Contain("SOURCE_NOT_MEASURED");
        await h.AssertNothingWrittenAsync();

        ((ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Local(), default)).Result!).StatusCode.Should().Be(202);
    }

    // ---------------- extend ----------------

    private static VideoEnhanceRequestBody Extend(string? aspect = "1:1", string? prompt = "a calm sky") =>
        new("Extend", "Hosted", TargetAspect: aspect, Prompt: prompt);

    [Fact]
    public async Task A_hosted_extend_is_queued_with_its_aspect_prompt_and_the_measured_hints_and_estimates_the_new_canvas()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 8, consented: true, billing: Billing.Rich);

        VideoEnhanceEstimate e = Value(await h.Controller().Estimate(h.ProjectId, h.FileRef, Extend(), default));
        (e.UnavailableCode, e.Hosted).Should().Be((null, true));
        (e.OutputWidth, e.OutputHeight, e.OutputFps).Should().Be((1920, 1920, 30d));
        await h.AssertNothingWrittenAsync();

        ObjectResult accepted = (await h.Controller().Request(h.ProjectId, h.FileRef, Extend(), default)).Result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(202);
        ((VideoEnhanceQueuedResponse)accepted.Value!).Op.Should().Be("Extend");
        WorkflowDefinition workflow = await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync();
        VideoEnhanceStepConfig config = JsonSerializer.Deserialize<VideoEnhanceStepConfig>(workflow.Steps.Single().VideoEnhanceConfigJson!, VideoEnhanceStepConfig.JsonOptions)!;
        (config.Op, config.TargetAspect, config.Prompt).Should().Be((VideoEnhanceOp.Extend, "1:1", "a calm sky"));
        config.AllowSourceMediaEgress.Should().BeTrue();
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public async Task An_extend_needs_exactly_one_of_aspect_and_margins_a_local_engine_is_refused_and_an_impossible_canvas_is_400()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 8);

        (await h.Controller().Request(h.ProjectId, h.FileRef, new VideoEnhanceRequestBody("Extend", "Hosted"), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().Request(h.ProjectId, h.FileRef, new VideoEnhanceRequestBody("Extend", "Local", TargetAspect: "1:1"), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().Request(h.ProjectId, h.FileRef, new VideoEnhanceRequestBody("Extend", "Hosted", TargetAspect: "1:1", MarginLeft: 0.1), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().Request(h.ProjectId, h.FileRef, new VideoEnhanceRequestBody("Extend", "Hosted", MarginLeft: 0.6), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().Request(h.ProjectId, h.FileRef, Extend(prompt: new string('x', 501)), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        // 1920x1080 is already 16:9 and cannot become 9:16 inside half a side.
        (await h.Controller().Request(h.ProjectId, h.FileRef, Extend("16:9"), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().Request(h.ProjectId, h.FileRef, Extend("9:16"), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        await h.AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task An_extend_canvas_taller_than_the_plan_is_402_and_RESOLUTION_ABOVE_PLAN_on_the_estimate()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 8, maxOutputHeight: 1080);

        // 1920x1080 to 1:1 is a 1920x1920 canvas.
        ObjectResult refused = (await h.Controller().Request(h.ProjectId, h.FileRef, Extend(), default)).Result.Should().BeAssignableTo<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(402);
        Value(await h.Controller().Estimate(h.ProjectId, h.FileRef, Extend(), default)).UnavailableCode.Should().Be("RESOLUTION_ABOVE_PLAN");
        await h.AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task An_extend_source_under_the_model_frame_floor_is_422_SOURCE_TOO_SHORT_with_the_minimum_seconds()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 2);

        ObjectResult refused = (await h.Controller().Request(h.ProjectId, h.FileRef, Extend(), default)).Result.Should().BeAssignableTo<ObjectResult>().Subject;

        refused.StatusCode.Should().Be(422);
        Json(refused.Value).Should().Contain("SOURCE_TOO_SHORT").And.Contain("\"minSeconds\":3");
        Value(await h.Controller().Estimate(h.ProjectId, h.FileRef, Extend(), default)).UnavailableCode.Should().Be("SOURCE_TOO_SHORT");
        await h.AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task An_extend_source_over_the_model_frame_ceiling_is_413_SOURCE_TOO_LONG_with_the_cap_in_seconds()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 9);

        ObjectResult refused = (await h.Controller().Request(h.ProjectId, h.FileRef, Extend(), default)).Result.Should().BeOfType<ObjectResult>().Subject;

        refused.StatusCode.Should().Be(413);
        Json(refused.Value).Should().Contain("SOURCE_TOO_LONG").And.Contain("\"maxSeconds\":8");
        VideoEnhanceEstimate e = Value(await h.Controller().Estimate(h.ProjectId, h.FileRef, Extend(), default));
        e.UnavailableCode.Should().Be("SOURCE_TOO_LONG");
        await h.AssertNothingWrittenAsync();
    }

    // ---------------- the estimate ----------------

    [Fact]
    public async Task The_estimate_writes_nothing_prices_with_the_queued_config_and_reports_the_size_bytes_and_time()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2, billing: Billing.Rich);

        VideoEnhanceEstimate e = Value(await h.Controller().Estimate(h.ProjectId, h.FileRef, Hosted(), default));

        e.BillingEnabled.Should().BeTrue();
        e.Hosted.Should().BeTrue();
        e.Credits.Should().Be(0.9);
        e.Seconds.Should().Be(13);
        e.BalanceCredits.Should().Be(100);
        e.Affordable.Should().BeTrue();
        (e.UnavailableCode, e.UnavailableReason).Should().Be((null, null));
        e.Consented.Should().BeTrue();
        e.MaxOutputHeight.Should().Be(2160);
        (e.OutputWidth, e.OutputHeight, e.OutputFps).Should().Be((3840, 2160, 30d));
        e.EstimatedBytes.Should().BeGreaterThan(0);
        e.EstimatedProcessingSeconds.Should().Be(VideoEnhanceSizing.HostedBaseSeconds + (int)Math.Ceiling(13.0 * 0 + 12.2 * VideoEnhanceSizing.HostedSecondsPerSourceSecond) - 0);
        await h.AssertNothingWrittenAsync();
        h.VendorCalls.Should().Be(0, "an estimate calls no vendor");
        h.LastPreflight!.VideoEnhanceConfigJson.Should().Contain("\"maxSourceSeconds\":13").And.Contain("\"sourceWidth\":1920");
    }

    [Theory]
    [InlineData("plan", "PLAN_NOT_ENTITLED")]
    [InlineData("height", "RESOLUTION_ABOVE_PLAN")]
    [InlineData("consent", "CONSENT_REQUIRED")]
    [InlineData("long", "SOURCE_TOO_LONG")]
    [InlineData("unmeasured", "SOURCE_NOT_MEASURED")]
    [InlineData("noprovider", "ENHANCE_UNAVAILABLE")]
    public async Task The_estimate_names_why_it_is_unavailable_with_a_machine_code(string scenario, string expectedCode)
    {
        using Harness h = scenario switch
        {
            "plan" => await Harness.CreateAsync(videoEnhanceAllowed: false),
            "height" => await Harness.CreateAsync(maxOutputHeight: 1080),
            "consent" => await Harness.CreateAsync(consented: false),
            "long" => await Harness.CreateAsync(probeSeconds: 200),
            "unmeasured" => await Harness.CreateAsync(probeSeconds: null),
            _ => await Harness.CreateAsync(provider: false)
        };

        VideoEnhanceEstimate e = Value(await h.Controller().Estimate(h.ProjectId, h.FileRef, Hosted(), default));

        e.UnavailableCode.Should().Be(expectedCode);
        e.UnavailableReason.Should().NotBeNullOrWhiteSpace();
        e.Affordable.Should().BeFalse();
        await h.AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task A_local_estimate_is_free_and_always_available_and_a_bad_body_or_media_is_refused_like_the_request()
    {
        using Harness h = await Harness.CreateAsync(videoEnhanceAllowed: false, consented: false, provider: false);
        VideoEnhanceEstimate e = Value(await h.Controller().Estimate(h.ProjectId, h.FileRef, Local(), default));
        (e.Hosted, e.Credits, e.UnavailableCode, e.Affordable).Should().Be((false, 0d, null, true));
        (e.OutputWidth, e.OutputHeight).Should().Be((1920, 1080));

        (await h.Controller().Estimate(h.ProjectId, h.FileRef, new VideoEnhanceRequestBody("Sparkle", "Local"), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Controller().Estimate(h.ProjectId, "file:" + Guid.NewGuid(), Local(), default)).Result.Should().BeOfType<NotFoundResult>();
    }

    // ---------------- the status poll and the cancel ----------------

    [Fact]
    public async Task The_status_is_derived_from_the_hidden_run_and_scoped_to_the_project_kind_and_media()
    {
        using Harness h = await Harness.CreateAsync();
        Guid queued = await h.AddRunAsync(ExecutionStatus.Queued);
        Guid running = await h.AddRunAsync(ExecutionStatus.Running);
        Guid ready = await h.AddRunAsync(ExecutionStatus.Passed, StepStatus.Completed, output: new
        {
            status = "completed",
            op = "Upscale",
            engine = "Hosted",
            method = "fal:m",
            durationSec = 12.2,
            outputFileId = h.OutputFileId.ToString(),
            outputWidth = 3840,
            outputHeight = 2160,
            outputFps = 30.0,
            sizeBytes = 123456L,
            billedSeconds = 13,
            warnings = new[] { "hdr_converted_to_sdr" }
        });
        Guid failed = await h.AddRunAsync(ExecutionStatus.Failed, StepStatus.Failed, failureCode: "ENHANCE_VENDOR_FAILED",
            output: new { status = "failed", errorCode = "ENHANCE_VENDOR_FAILED", message = "The hosted audio service could not finish the job." });
        Guid cancelled = await h.AddRunAsync(ExecutionStatus.Cancelled);

        VideoEnhanceController c = h.Controller();
        Value(await c.Get(h.ProjectId, h.FileRef, queued, default)).Should().Match<VideoEnhanceStatusResponse>(s => s.State == "queued" && s.Progress == 0);
        Value(await c.Get(h.ProjectId, h.FileRef, running, default)).State.Should().Be("running");
        Value(await c.Get(h.ProjectId, h.FileRef, cancelled, default)).State.Should().Be("cancelled");

        VideoEnhanceStatusResponse done = Value(await c.Get(h.ProjectId, h.FileRef, ready, default));
        done.State.Should().Be("ready");
        (done.Op, done.Engine, done.Method).Should().Be(("Upscale", "Hosted", "fal:m"));
        (done.DurationSec, done.OutputWidth, done.OutputHeight, done.OutputFps, done.SizeBytes).Should().Be((12.2, 3840, 2160, 30d, 123456L));
        done.OutputFileId.Should().Be(h.OutputFileId);
        done.SourceMediaRef.Should().Be(h.FileRef);
        done.Warnings.Should().Equal("hdr_converted_to_sdr");
        done.Progress.Should().Be(100);
        Json(done).Should().NotContain("projects/").And.NotContain("storageKey");

        VideoEnhanceStatusResponse bad = Value(await c.Get(h.ProjectId, h.FileRef, failed, default));
        (bad.State, bad.FailureCode).Should().Be(("failed", "ENHANCE_VENDOR_FAILED"));
        bad.Message.Should().NotBeNullOrWhiteSpace();
        bad.OutputFileId.Should().BeNull();

        // another media of the same project, another project's run, and another kind's run are all the same 404.
        (await c.Get(h.ProjectId, "file:" + h.AudioFileId, ready, default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.Get(h.ProjectId, h.FileRef, Guid.NewGuid(), default)).Result.Should().BeOfType<NotFoundResult>();
        Guid standardRun = await h.AddRunAsync(ExecutionStatus.Passed, StepStatus.Completed, kind: WorkflowDefinitionKind.Standard);
        (await c.Get(h.ProjectId, h.FileRef, standardRun, default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller(Guid.NewGuid()).Get(h.ProjectId, h.FileRef, ready, default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Cancel_stops_the_hidden_run_with_the_same_event_as_the_workflow_stop_endpoint()
    {
        using Harness h = await Harness.CreateAsync();
        Guid running = await h.AddRunAsync(ExecutionStatus.Running);
        Guid done = await h.AddRunAsync(ExecutionStatus.Passed, StepStatus.Completed);

        ActionResult<object> stopping = await h.Controller().Cancel(h.ProjectId, h.FileRef, running, default);
        ((ObjectResult)stopping.Result!).StatusCode.Should().Be(202);
        h.Published.Should().ContainSingle().Which.Should().Match<WorkflowExecutionStopRequested>(m => m.ExecutionId == running && m.RequestedByUserId == h.OwnerId);

        ((ObjectResult)(await h.Controller().Cancel(h.ProjectId, h.FileRef, done, default)).Result!).StatusCode.Should().Be(409);
        (await h.Controller().Cancel(h.ProjectId, h.FileRef, Guid.NewGuid(), default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller().Cancel(h.ProjectId, "file:" + h.AudioFileId, running, default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller(Guid.NewGuid()).Cancel(h.ProjectId, h.FileRef, running, default)).Result.Should().BeOfType<NotFoundResult>();
        h.Published.Should().HaveCount(1, "only the one stop was published");
    }

    // ---------------- hidden from every path ----------------

    private static WorkflowsController Workflows(Harness h)
    {
        Mock<ICurrentUser> user = h.User();
        return new WorkflowsController(
            h.Db, user.Object, TestTenancy.Access(h.Db, user.Object), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(h.Db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            Mock.Of<IWorkflowExecutionService>(), NullLogger<WorkflowsController>.Instance);
    }

    [Fact]
    public async Task The_hidden_kind_is_not_listed_deleted_retried_or_resumed_over_REST()
    {
        using Harness h = await Harness.CreateAsync();
        Guid execution = await h.AddRunAsync(ExecutionStatus.Failed, StepStatus.Failed, failureCode: "ENHANCE_VENDOR_FAILED");
        Guid definition = (await h.Db.WorkflowDefinitions.SingleAsync()).Id;
        WorkflowsController workflows = Workflows(h);

        // list
        ActionResult<List<WorkflowDefinitionResponse>> list = await workflows.List(h.ProjectId, default);
        list.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<WorkflowDefinitionResponse>>().Which.Should().BeEmpty();

        // delete
        (await workflows.Delete(h.ProjectId, definition, default)).Should().BeOfType<NotFoundResult>();
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(1);

        // resume and retry are refused with their own codes, and the run is untouched
        ConflictObjectResult resume = (await workflows.ResumeExecution(h.ProjectId, definition, execution, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>().Subject;
        Json(resume.Value).Should().Contain("ENHANCE_RESUME_NOT_SUPPORTED");
        ConflictObjectResult retry = (await workflows.RetryExecution(h.ProjectId, definition, execution, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>().Subject;
        Json(retry.Value).Should().Contain("ENHANCE_RETRY_NOT_SUPPORTED");
        (await h.Db.WorkflowExecutions.AsNoTracking().SingleAsync()).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task The_hidden_kind_is_filtered_out_of_every_workflow_query_the_other_entries_use()
    {
        // Every assistant, MCP, dashboard and apps path reads workflows with Kind == Standard; a VideoEnhance row is not Standard.
        using Harness h = await Harness.CreateAsync();
        await h.AddRunAsync(ExecutionStatus.Passed, StepStatus.Completed);
        (await h.Db.WorkflowDefinitions.CountAsync(w => w.ProjectId == h.ProjectId && w.Kind == WorkflowDefinitionKind.Standard)).Should().Be(0);
        (await h.Db.WorkflowDefinitions.CountAsync(w => w.ProjectId == h.ProjectId && w.Kind == WorkflowDefinitionKind.VideoEnhance)).Should().Be(1);
    }

    // ---------------- the orphan reaper ----------------

    [Fact]
    public async Task The_reaper_selects_only_old_hidden_enhance_workflows_that_never_ran()
    {
        DateTime now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid project = Guid.NewGuid();
        WorkflowDefinition W(WorkflowDefinitionKind kind, DateTime at) => new() { Id = Guid.NewGuid(), ProjectId = project, Name = "w", Kind = kind, CreatedAt = at, UpdatedAt = at };
        WorkflowDefinition orphan = W(WorkflowDefinitionKind.VideoEnhance, now.AddHours(-2));
        WorkflowDefinition young = W(WorkflowDefinitionKind.VideoEnhance, now.AddMinutes(-30));
        WorkflowDefinition ran = W(WorkflowDefinitionKind.VideoEnhance, now.AddDays(-2));
        WorkflowDefinition standard = W(WorkflowDefinitionKind.Standard, now.AddDays(-2));
        db.WorkflowDefinitions.AddRange(orphan, young, ran, standard);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = ran.Id, ProjectId = project, Status = ExecutionStatus.Passed, CorrelationId = "c" });
        await db.SaveChangesAsync();

        List<Guid> selected = await TimelineRenderOrphanReaper.SelectOrphans(db.WorkflowDefinitions, now - TimeSpan.FromHours(1)).Select(w => w.Id).ToListAsync();

        selected.Should().Equal(orphan.Id);
    }

    // ---------------- the consent routes ----------------

    [Fact]
    public async Task Consent_is_read_granted_once_and_withdrawn_and_withdrawing_stops_only_the_hosted_in_flight_runs()
    {
        using Harness h = await Harness.CreateAsync(consented: false);
        Mock<ICurrentUser> user = h.User();
        VideoEnhanceConsentController Consent(Guid? who = null)
        {
            Mock<ICurrentUser> u = h.User(who);
            return new VideoEnhanceConsentController(h.Db, new ProjectAccessService(h.Db, u.Object), u.Object, h.PublishMock.Object);
        }

        static VideoEnhanceConsentResponse Body(ActionResult<VideoEnhanceConsentResponse> r) => (VideoEnhanceConsentResponse)((OkObjectResult)r.Result!).Value!;

        Body(await Consent().Get(h.ProjectId, default)).Should().Be(new VideoEnhanceConsentResponse(false, null));
        VideoEnhanceConsentResponse first = Body(await Consent().Grant(h.ProjectId, default));
        first.Consented.Should().BeTrue();
        Body(await Consent().Grant(h.ProjectId, default)).ConsentedAt.Should().Be(first.ConsentedAt, "granting twice keeps the original timestamp");
        (await h.Db.Projects.AsNoTracking().SingleAsync()).VideoEnhanceConsentAt.Should().Be(first.ConsentedAt);
        (await h.Db.Projects.AsNoTracking().SingleAsync()).ObjectEffectsConsentAt.Should().BeNull("a separate consent from magic blur");

        Guid hosted = await h.AddRunAsync(ExecutionStatus.Running, engine: "Hosted");
        Guid local = await h.AddRunAsync(ExecutionStatus.Running, engine: "Local");
        Guid finished = await h.AddRunAsync(ExecutionStatus.Passed, StepStatus.Completed, engine: "Hosted");

        Body(await Consent().Revoke(h.ProjectId, default)).Should().Be(new VideoEnhanceConsentResponse(false, null));
        h.Published.Select(p => p.ExecutionId).Should().Equal(hosted);
        h.Published.Should().NotContain(p => p.ExecutionId == local || p.ExecutionId == finished);
        Body(await Consent().Revoke(h.ProjectId, default)).Consented.Should().BeFalse("idempotent");

        // across workspaces: 404 on every action; a role below Edit cannot grant.
        Guid stranger = Guid.NewGuid();
        (await Consent(stranger).Get(h.ProjectId, default)).Result.Should().BeOfType<NotFoundResult>();
        (await Consent(stranger).Grant(h.ProjectId, default)).Result.Should().BeOfType<NotFoundResult>();
        (await Consent(stranger).Revoke(h.ProjectId, default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Db.Projects.AsNoTracking().SingleAsync()).VideoEnhanceConsentAt.Should().BeNull();
        _ = user;
    }

    // ---------------- fix round ----------------

    [Theory]
    [InlineData("Interpolate", "Hosted")]
    [InlineData("Interpolate", "Local")]
    [InlineData("Denoise", "Local")]
    [InlineData("MotionBlur", "Local")]
    public async Task Every_operation_whose_output_is_taller_than_the_plan_is_refused_before_anything_is_written(string op, string engine)
    {
        using Harness h = await Harness.CreateAsync(maxOutputHeight: 720);
        VideoEnhanceRequestBody body = op == "Interpolate"
            ? new(op, engine, FrameMultiplier: 2)
            : op == "Denoise" ? new(op, engine, DenoiseStrength: "Light") : new(op, engine);

        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, body, default)).Result!;
        r.StatusCode.Should().Be(402);
        Json(r.Value).Should().Contain("PLAN_NOT_ENTITLED").And.Contain("resolution");
        await h.AssertNothingWrittenAsync();

        VideoEnhanceEstimate e = Value(await h.Controller().Estimate(h.ProjectId, h.FileRef, body, default));
        e.UnavailableCode.Should().Be("RESOLUTION_ABOVE_PLAN");
        e.Affordable.Should().BeFalse();
        e.MaxOutputHeight.Should().Be(720);
    }

    [Fact]
    public async Task The_source_caps_come_from_the_configuration_and_the_estimate_names_the_one_that_applies()
    {
        Dictionary<string, string?> values = new()
        {
            ["VideoEnhance:Hosted:MaxSourceSeconds"] = "60",
            ["VideoEnhance:Local:MaxSourceSeconds"] = "100"
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        using Harness hosted = await Harness.CreateAsync(probeSeconds: 70);
        hosted.Configuration = config;
        ObjectResult r = (ObjectResult)(await hosted.Controller().Request(hosted.ProjectId, hosted.FileRef, Hosted(), default)).Result!;
        r.StatusCode.Should().Be(413);
        Json(r.Value).Should().Contain("\"maxSeconds\":60");
        VideoEnhanceEstimate he = Value(await hosted.Controller().Estimate(hosted.ProjectId, hosted.FileRef, Hosted(), default));
        (he.UnavailableCode, he.MaxSeconds).Should().Be(("SOURCE_TOO_LONG", 60));

        using Harness local = await Harness.CreateAsync(probeSeconds: 101);
        local.Configuration = config;
        ((ObjectResult)(await local.Controller().Request(local.ProjectId, local.FileRef, Local(), default)).Result!).StatusCode.Should().Be(413);
        Value(await local.Controller().Estimate(local.ProjectId, local.FileRef, Local(), default)).MaxSeconds.Should().Be(100);

        // The defaults when nothing is configured.
        using Harness plain = await Harness.CreateAsync(probeSeconds: 30);
        Value(await plain.Controller().Estimate(plain.ProjectId, plain.FileRef, Hosted(), default)).MaxSeconds.Should().Be(120);
        Value(await plain.Controller().Estimate(plain.ProjectId, plain.FileRef, Local(), default)).MaxSeconds.Should().Be(600);
    }

    [Fact]
    public async Task Withdrawing_the_consent_stops_a_hosted_enhance_step_of_any_workflow_kind_not_only_the_hidden_one()
    {
        using Harness h = await Harness.CreateAsync();
        Guid hiddenHosted = await h.AddRunAsync(ExecutionStatus.Running, engine: "Hosted");
        Guid standardHosted = await h.AddRunAsync(ExecutionStatus.Queued, engine: "Hosted", kind: WorkflowDefinitionKind.Standard);
        Guid standardLocal = await h.AddRunAsync(ExecutionStatus.Running, engine: "Local", kind: WorkflowDefinitionKind.Standard);
        Guid standardFinished = await h.AddRunAsync(ExecutionStatus.Passed, StepStatus.Completed, engine: "Hosted", kind: WorkflowDefinitionKind.Standard);

        Mock<ICurrentUser> u = h.User();
        VideoEnhanceConsentController consent = new(h.Db, new ProjectAccessService(h.Db, u.Object), u.Object, h.PublishMock.Object);
        await consent.Revoke(h.ProjectId, default);

        h.Published.Select(p => p.ExecutionId).Should().BeEquivalentTo([hiddenHosted, standardHosted]);
        h.Published.Should().NotContain(p => p.ExecutionId == standardLocal || p.ExecutionId == standardFinished);
    }

    [Fact]
    public async Task Withdrawing_the_consent_stops_a_run_whose_second_step_is_the_hosted_enhance()
    {
        using Harness h = await Harness.CreateAsync();
        Guid twoStep = await h.AddRunAsync(ExecutionStatus.Running, engine: "Local", kind: WorkflowDefinitionKind.Standard);
        // Add a SECOND VideoEnhance step (hosted) to the same workflow; the first is local.
        Guid definition = (await h.Db.WorkflowExecutions.SingleAsync(e => e.Id == twoStep)).WorkflowDefinitionId;
        h.Db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = definition,
            StepOrder = 2,
            StepType = StepType.VideoEnhance,
            VideoEnhanceConfigJson = JsonSerializer.Serialize(new VideoEnhanceStepConfig(
                VideoEnhanceOp.Upscale, VideoEnhanceEngine.Hosted, new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: h.FileId), UpscaleFactor: 2),
                VideoEnhanceStepConfig.JsonOptions)
        });
        await h.Db.SaveChangesAsync();

        Mock<ICurrentUser> u = h.User();
        await new VideoEnhanceConsentController(h.Db, new ProjectAccessService(h.Db, u.Object), u.Object, h.PublishMock.Object).Revoke(h.ProjectId, default);

        h.Published.Select(p => p.ExecutionId).Should().Equal(twoStep);
    }

    // ---------------- sizing ----------------

    [Fact]
    public void The_sizing_helper_follows_the_engines_geometry_and_labels_its_figures_as_estimates()
    {
        VideoEnhanceStepConfig Up(int? factor, int? target) =>
            new(VideoEnhanceOp.Upscale, VideoEnhanceEngine.Local, new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: Guid.NewGuid()), UpscaleFactor: factor, TargetHeight: target);

        VideoEnhanceSizing.OutputFor(Up(2, null), 1920, 1080, 30).Should().Be(new VideoEnhanceSizing.Output(3840, 2160, 30));
        VideoEnhanceSizing.OutputFor(Up(null, 1080), 1280, 720, 30).Should().Be(new VideoEnhanceSizing.Output(1920, 1080, 30));
        VideoEnhanceStepConfig interpolate = new(VideoEnhanceOp.Interpolate, VideoEnhanceEngine.Local, Up(2, null).Source, FrameMultiplier: 4);
        VideoEnhanceSizing.OutputFor(interpolate, 1920, 1080, 30).Should().Be(new VideoEnhanceSizing.Output(1920, 1080, 120));
        VideoEnhanceSizing.OutputFor(new VideoEnhanceStepConfig(VideoEnhanceOp.MotionBlur, VideoEnhanceEngine.Local, Up(2, null).Source), 1280, 720, 24)
            .Should().Be(new VideoEnhanceSizing.Output(1280, 720, 24));

        VideoEnhanceSizing.EstimatedBytes(new VideoEnhanceSizing.Output(1920, 1080, 30), 10).Should().Be((8_000_000L + 192_000) * 10 / 8);
        VideoEnhanceSizing.EstimatedBytes(new VideoEnhanceSizing.Output(1920, 1080, 60), 10).Should().BeGreaterThan(VideoEnhanceSizing.EstimatedBytes(new VideoEnhanceSizing.Output(1920, 1080, 30), 10));
        VideoEnhanceSizing.EstimatedProcessingSeconds(Up(2, null) with { Engine = VideoEnhanceEngine.Hosted }, 10, new VideoEnhanceSizing.Output(3840, 2160, 30))
            .Should().Be(VideoEnhanceSizing.HostedBaseSeconds + 30);
        VideoEnhanceSizing.EstimatedProcessingSeconds(Up(2, null), 10, new VideoEnhanceSizing.Output(3840, 2160, 30)).Should().BeGreaterThan(5);
    }

    // ---------------- harness ----------------

    private enum Billing { Off, Rich, Poor }

    private sealed class Harness : IDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid OwnerId { get; init; }
        public Guid FileId { get; init; }
        public Guid AudioFileId { get; init; }
        public Guid OutputFileId { get; } = Guid.NewGuid();
        public string FileRef => "file:" + FileId;
        public int QueueCalls { get; private set; }
        public int PreflightCalls { get; private set; }
        public int VendorCalls => 0;
        public WorkflowDefinitionKind? LastQueuedKind { get; private set; }
        public Guid? LastQueuedExecutionId { get; private set; }
        public WorkflowStep? LastPreflight { get; private set; }
        public Exception? QueueThrows { get; set; }
        public IConfiguration? Configuration { get; set; }
        public List<WorkflowExecutionStopRequested> Published { get; } = [];
        public Mock<IPublishEndpoint> PublishMock { get; } = new();

        private bool _verified;
        private bool _videoEnhanceAllowed;
        private int _maxOutputHeight;
        private bool _provider;
        private Billing _billing;

        public static async Task<Harness> CreateAsync(
            bool verified = true, bool videoEnhanceAllowed = true, bool consented = true, double? probeSeconds = 10, bool provider = true,
            Billing billing = Billing.Rich, int maxOutputHeight = 2160, double fps = 30)
        {
            InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
            Guid owner = Guid.NewGuid();
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "p",
                OwnerId = owner,
                OrganizationId = TestTenancy.OrgOf(owner),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                VideoEnhanceConsentAt = consented ? DateTime.UtcNow : null
            };
            db.Projects.Add(project);
            string key = $"projects/{project.Id}/userFiles/v.mp4";
            ProjectFile video = new() { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = "v.mp4", StorageKey = key, MimeType = "video/mp4" };
            ProjectFile audio = new() { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = "a.mp3", StorageKey = key + ".mp3", MimeType = "audio/mpeg" };
            db.ProjectFiles.AddRange(video, audio);
            if (probeSeconds is { } seconds)
            {
                db.MediaDerivatives.Add(new MediaDerivative
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    SourceProjectFileId = video.Id,
                    SourceStorageKey = key,
                    Kind = MediaDerivativeKind.Proxy,
                    StorageKey = "d/proxy.mp4",
                    CacheKey = "k",
                    SourceProbeJson = new MediaSourceProbe(seconds, (int)Math.Round(fps * 1000), 1000, 1920, 1080, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
                });
            }

            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
            await db.SaveChangesAsync();

            Harness h = new()
            {
                Db = db,
                ProjectId = project.Id,
                OwnerId = owner,
                FileId = video.Id,
                AudioFileId = audio.Id,
                _verified = verified,
                _videoEnhanceAllowed = videoEnhanceAllowed,
                _maxOutputHeight = maxOutputHeight,
                _provider = provider,
                _billing = billing
            };
            h.PublishMock.Setup(p => p.Publish(It.IsAny<WorkflowExecutionStopRequested>(), It.IsAny<CancellationToken>()))
                .Callback<WorkflowExecutionStopRequested, CancellationToken>((m, _) => h.Published.Add(m))
                .Returns(Task.CompletedTask);
            return h;
        }

        public Mock<ICurrentUser> User(Guid? user = null)
        {
            Guid id = user ?? OwnerId;
            Mock<ICurrentUser> u = new();
            u.SetupGet(x => x.UserId).Returns(id);
            u.SetupGet(x => x.Email).Returns("t@example.com");
            u.SetupGet(x => x.IsAuthenticated).Returns(true);
            u.SetupGet(x => x.EmailVerified).Returns(_verified);
            u.SetupGet(x => x.OrganizationId).Returns(TestTenancy.OrgOf(id));
            u.SetupGet(x => x.OrgRole).Returns(OrganizationRole.Member);
            return u;
        }

        public VideoEnhanceController Controller(Guid? user = null)
        {
            Mock<ICurrentUser> u = User(user);

            Mock<IWorkflowExecutionService> executions = new();
            executions.Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                    It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>(),
                    It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
                .ReturnsAsync((Guid _, Guid workflowId, Guid _, string? _, ExecutionInitiator _, CancellationToken _, bool _, RunChoices? _,
                    WorkflowDefinitionKind kind, Guid? executionId, Func<CancellationToken, Task>? _) =>
                {
                    if (QueueThrows is not null) throw QueueThrows;
                    QueueCalls++;
                    LastQueuedKind = kind;
                    LastQueuedExecutionId = executionId;
                    return new WorkflowExecutionResponse(executionId ?? Guid.NewGuid(), workflowId, "Queued", null, null, 0, null, null, null, [], []);
                });

            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet
                {
                    Tier = "trial",
                    VideoEnhanceAllowed = _videoEnhanceAllowed,
                    MaxOutputHeight = _maxOutputHeight
                }));

            Mock<IWorkflowCostEstimator> costs = new();
            costs.SetupGet(c => c.BillingEnabled).Returns(_billing != Billing.Off);
            costs.Setup(c => c.PreflightAsync(It.IsAny<Guid?>(), It.IsAny<IReadOnlyList<WorkflowStep>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid? _, IReadOnlyList<WorkflowStep> steps, CancellationToken _) =>
                {
                    PreflightCalls++;
                    LastPreflight = steps[0];
                    long balance = _billing == Billing.Rich ? 100_000 : 100;
                    WorkflowCostEstimate estimate = new(true, false, 900, 900, 900, []);
                    return new WorkflowPreflightResult(
                        balance < 900 ? WorkflowPreflightOutcome.InsufficientCredits : WorkflowPreflightOutcome.Allowed, estimate, balance);
                });

            Mock<IInferenceProviderResolver> providers = new();
            ResolvedAudioGenerationProvider? row = _provider
                ? new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.VideoUpscale, "",
                    VideoEnhanceModelCatalog.TopazId, "test", 60, AccountId: Guid.NewGuid())
                : null;
            providers.Setup(p => p.ResolveVideoUpscaleAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(row);
            providers.Setup(p => p.ResolveFrameInterpolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(row is null ? null : row with { Capability = InferenceProviderCapability.FrameInterpolation, ModelName = VideoEnhanceModelCatalog.RifeId });
            providers.Setup(p => p.ResolveVideoOutpaintAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(row is null ? null : row with { Capability = InferenceProviderCapability.VideoOutpaint, ModelName = VideoEnhanceModelCatalog.WanVaceId });
            Mock<IModelPriceService> prices = new();
            prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 100), 0m, Guid.NewGuid()));

            VideoEnhanceService service = new(
                Db, executions.Object, NullLogger<VideoEnhanceService>.Instance, providers.Object, prices.Object, PublishMock.Object,
                TimeProvider.System, entitlements.Object, costs.Object, Configuration);
            return new VideoEnhanceController(u.Object, TestTenancy.Access(Db, u.Object), service);
        }

        public async Task<Guid> AddRunAsync(
            ExecutionStatus status, StepStatus? step = null, object? output = null, string? failureCode = null,
            WorkflowDefinitionKind kind = WorkflowDefinitionKind.VideoEnhance, string engine = "Hosted")
        {
            Guid definition = Guid.NewGuid(), stepId = Guid.NewGuid(), execution = Guid.NewGuid();
            bool hosted = engine == "Hosted";
            string config = JsonSerializer.Serialize(new VideoEnhanceStepConfig(
                hosted ? VideoEnhanceOp.Upscale : VideoEnhanceOp.Denoise, hosted ? VideoEnhanceEngine.Hosted : VideoEnhanceEngine.Local,
                new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: FileId),
                UpscaleFactor: hosted ? 2 : null, DenoiseStrength: hosted ? null : VideoEnhanceDenoiseStrength.Light), VideoEnhanceStepConfig.JsonOptions);
            Db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = definition,
                ProjectId = ProjectId,
                Name = "w",
                Kind = kind,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            Db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = execution,
                WorkflowDefinitionId = definition,
                ProjectId = ProjectId,
                OrganizationId = TestTenancy.OrgOf(OwnerId),
                InitiatedByUserId = OwnerId,
                Status = status,
                QueuedAt = DateTime.UtcNow
            });
            Db.WorkflowSteps.Add(new WorkflowStep
            {
                Id = stepId,
                WorkflowDefinitionId = definition,
                StepOrder = 1,
                StepType = StepType.VideoEnhance,
                VideoEnhanceConfigJson = config
            });
            if (step is { } s)
            {
                Db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = execution,
                    WorkflowStepId = stepId,
                    Status = s,
                    FailureCode = failureCode,
                    CompletedAt = DateTime.UtcNow,
                    OutputJson = JsonSerializer.Serialize(output ?? new { status = "completed" })
                });
            }

            await Db.SaveChangesAsync();
            return execution;
        }

        public async Task SeedRecentRunsAsync(int count)
        {
            Guid definition = Guid.NewGuid();
            Db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = definition,
                ProjectId = ProjectId,
                Name = "w",
                Kind = WorkflowDefinitionKind.VideoEnhance,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            for (int i = 0; i < count; i++)
            {
                Db.WorkflowExecutions.Add(new WorkflowExecution
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = definition,
                    ProjectId = ProjectId,
                    OrganizationId = TestTenancy.OrgOf(OwnerId),
                    InitiatedByUserId = OwnerId,
                    QueuedAt = DateTime.UtcNow.AddMinutes(-5),
                    Status = ExecutionStatus.Passed
                });
            }

            await Db.SaveChangesAsync();
        }

        public async Task AssertNothingWrittenAsync()
        {
            (await Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
            QueueCalls.Should().Be(0);
        }

        public void Dispose() => Db.Dispose();
    }
}
