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
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ObjectAnalysis;

/// <summary>Object analysis routes: gate order (nothing written on a refusal), the hidden run, reuse, estimate and lazily derived status.</summary>
public class ObjectAnalysisControllerTests
{
    private static readonly ObjectAnalysisRequestBody Body =
        new("the red car", null, "Blur", 0.8, false, 4);

    private static string Track(string id, int frames = 60, int empty = 0) =>
        "{\"id\":\"" + id + "\",\"prompt\":\"the red car\",\"sourceIndex\":0,\"startSec\":0,\"endSec\":2,\"fps\":30,\"frameCount\":" + frames
        + ",\"maskStorageKey\":\"projects/x/agentFiles/segmentation/m.mp4\",\"maskContentHash\":\"abc\",\"facts\":{\"emptyFrames\":" + empty
        + ",\"firstNonEmptyFrame\":0,\"lastNonEmptyFrame\":59}}";

    private static string Artifact(params string[] tracks) => "{\"segmentationTracks\":[" + string.Join(",", tracks) + "]}";

    // ---- gates --------------------------------------------------------------------------------------

    [Fact]
    public async Task Happy_path_queues_a_hidden_ObjectAnalysis_run_with_one_segmentation_target()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2);

        ActionResult<ObjectAnalysisQueuedResponse> r = await h.Controller().Request(h.ProjectId, h.FileRef, Body, default);

        ObjectResult accepted = r.Result.Should().BeOfType<ObjectResult>().Subject;
        accepted.StatusCode.Should().Be(202);
        ObjectAnalysisQueuedResponse queued = (ObjectAnalysisQueuedResponse)accepted.Value!;
        queued.Status.Should().Be("queued");

        WorkflowDefinition workflow = await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync();
        workflow.Kind.Should().Be(WorkflowDefinitionKind.ObjectAnalysis);
        workflow.Steps.Should().ContainSingle().Which.StepType.Should().Be(StepType.VideoAnalyze);
        string json = workflow.Steps.Single().VideoAnalyzeConfigJson!;
        VideoAnalyzeStepConfig config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(json, Harness.ConfigJson)!;
        config.AllowSourceMediaEgress.Should().BeTrue();
        config.MaxSegmentationSeconds.Should().Be(13);
        config.Transcription.Should().Be(VideoTranscriptionMode.Off);
        config.Vision.Should().Be(VideoVisionMode.Off);
        config.TrackObjects.Should().ContainSingle().Which.Should().Match<ObjectTrackTarget>(
            t => t.Label == "the red car" && t.Method == ObjectTrackMethod.Segmentation);
        config.Source.InSec.Should().BeNull();
        config.Source.ProjectFileId.Should().Be(h.FileId);

        ObjectEffectRequest row = await h.Db.ObjectEffectRequests.SingleAsync();
        row.ExecutionId.Should().Be(queued.ExecutionId);
        row.Effect.Should().Be("Blur");
        row.Strength.Should().Be(0.8);
        row.FeatherPx.Should().Be(4);
        row.Status.Should().Be("queued");
        h.QueueCalls.Should().Be(1);
        h.LastQueuedKind.Should().Be(WorkflowDefinitionKind.ObjectAnalysis);
        h.LastQueuedExecutionId.Should().Be(queued.ExecutionId);
    }

    [Fact]
    public async Task Hidden_kind_is_never_listed_in_the_workflows_REST_list()
    {
        using Harness h = await Harness.CreateAsync();
        await h.Controller().Request(h.ProjectId, h.FileRef, Body, default);

        Mock<ICurrentUser> user = h.User();
        WorkflowsController workflows = new(
            h.Db, user.Object, TestTenancy.Access(h.Db, user.Object), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(h.Db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            Mock.Of<IWorkflowExecutionService>(), NullLogger<WorkflowsController>.Instance);

        ActionResult<List<WorkflowDefinitionResponse>> list = await workflows.List(h.ProjectId, default);
        list.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<WorkflowDefinitionResponse>>()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public async Task Resuming_or_retrying_a_hidden_object_analysis_run_is_refused_with_its_own_code()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "x");
        await h.AddRunAsync(row, ExecutionStatus.Failed, StepStatus.Failed, failureCode: "SEGMENTATION_PROVIDER_ERROR");
        Guid definition = (await h.Db.WorkflowDefinitions.SingleAsync()).Id;

        Mock<ICurrentUser> user = h.User();
        WorkflowsController workflows = new(
            h.Db, user.Object, TestTenancy.Access(h.Db, user.Object), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(h.Db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            Mock.Of<IWorkflowExecutionService>(), NullLogger<WorkflowsController>.Instance);

        ConflictObjectResult resume = (await workflows.ResumeExecution(h.ProjectId, definition, row.ExecutionId, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(resume.Value).Should().Contain("OBJECT_RESUME_NOT_SUPPORTED");

        ConflictObjectResult retry = (await workflows.RetryExecution(h.ProjectId, definition, row.ExecutionId, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer.Serialize(retry.Value).Should().Contain("REVISION_RETRY_NOT_SUPPORTED");

        (await h.Db.WorkflowExecutions.AsNoTracking().SingleAsync()).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Gates_run_in_order_and_a_refusal_writes_nothing()
    {
        // 1. unverified email beats everything (even a foreign project).
        using (Harness h = await Harness.CreateAsync(verified: false))
        {
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result!;
            r.StatusCode.Should().Be(403);
            await h.AssertNothingWrittenAsync();
        }

        // 2. a role below Edit: 403, before the source is looked at.
        using (Harness h = await Harness.CreateAsync(role: OrganizationRole.Billing))
        {
            (await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result.Should().BeOfType<ForbidResult>();
            await h.AssertNothingWrittenAsync();
        }

        // 3. an unknown source: 404; an audio file: 422.
        using (Harness h = await Harness.CreateAsync())
        {
            (await h.Controller().Request(h.ProjectId, "file:" + Guid.NewGuid(), Body, default)).Result.Should().BeOfType<NotFoundResult>();
            (await h.Controller().Request(h.ProjectId, "nonsense", Body, default)).Result.Should().BeOfType<NotFoundResult>();
            ((ObjectResult)(await h.Controller().Request(h.ProjectId, "file:" + h.AudioFileId, Body, default)).Result!).StatusCode.Should().Be(422);
            await h.AssertNothingWrittenAsync();
        }

        // 4. plan feature before consent (the project has not consented either).
        using (Harness h = await Harness.CreateAsync(segmentationAllowed: false, consented: false))
        {
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result!;
            r.StatusCode.Should().Be(402);
            JsonSerializer.Serialize(r.Value).Should().Contain("PLAN_NOT_ENTITLED").And.Contain("segmentation");
            await h.AssertNothingWrittenAsync();
        }

        // 5. consent before length and credits.
        using (Harness h = await Harness.CreateAsync(consented: false, probeSeconds: 900, billing: Billing.Poor))
        {
            ConflictObjectResult r = (await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result.Should().BeOfType<ConflictObjectResult>().Subject;
            string json = JsonSerializer.Serialize(r.Value);
            json.Should().Contain("OBJECT_CONSENT_REQUIRED").And.Contain("needsBeforeRunning");
            await h.AssertNothingWrittenAsync();
        }

        // 6. too long before credits.
        using (Harness h = await Harness.CreateAsync(probeSeconds: 601, billing: Billing.Poor))
        {
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result!;
            r.StatusCode.Should().Be(422);
            JsonSerializer.Serialize(r.Value).Should().Contain("OBJECT_SOURCE_TOO_LONG").And.Contain("10 minutes");
            await h.AssertNothingWrittenAsync();
        }

        // 7. credits.
        using (Harness h = await Harness.CreateAsync(probeSeconds: 30, billing: Billing.Poor))
        {
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result!;
            r.StatusCode.Should().Be(402);
            string json = JsonSerializer.Serialize(r.Value);
            json.Should().Contain("INSUFFICIENT_CREDITS").And.Contain("missing").And.Contain("message");
            await h.AssertNothingWrittenAsync();
        }

        // 8. run rate: ten paid runs in the last hour.
        using (Harness h = await Harness.CreateAsync())
        {
            await h.SeedRecentRunsAsync(ObjectAnalysisController.PaidRunsPerHour);
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result!;
            r.StatusCode.Should().Be(429);
            JsonSerializer.Serialize(r.Value).Should().Contain("RUN_RATE_LIMITED");
            h.QueueCalls.Should().Be(0);
            (await h.Db.ObjectEffectRequests.CountAsync()).Should().Be(0);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("a\u0001b")]
    [InlineData("a\nb")]
    public async Task A_bad_prompt_is_a_400_and_writes_nothing(string? prompt)
    {
        using Harness h = await Harness.CreateAsync();
        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body with { Prompt = prompt }, default)).Result!;
        r.StatusCode.Should().Be(400);
        await h.AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task A_prompt_over_80_characters_is_a_400()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body with { Prompt = new string('x', 81) }, default)).Result!;
        r.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task A_bad_effect_or_strength_is_a_400()
    {
        using Harness h = await Harness.CreateAsync();
        foreach (ObjectAnalysisRequestBody bad in new[] { Body with { Effect = "Sparkle" }, Body with { Strength = 1.5 }, Body with { FeatherPx = 500 }, Body with { MatchScope = "Everything" } })
        {
            ((ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, bad, default)).Result!).StatusCode.Should().Be(400);
        }

        await h.AssertNothingWrittenAsync();
    }

    // ---- reuse --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_succeeded_request_for_the_same_source_prompt_and_scope_is_reused_without_a_second_run()
    {
        using Harness h = await Harness.CreateAsync(billing: Billing.Rich);
        ObjectEffectRequest done = h.AddRequest(ObjectEffectRequest.StatusSucceeded, "the red car");
        await h.Db.SaveChangesAsync();

        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result!;

        r.StatusCode.Should().Be(200);
        ((ObjectAnalysisQueuedResponse)r.Value!).RequestId.Should().Be(done.Id);
        h.QueueCalls.Should().Be(0);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_identical_request_still_in_flight_is_returned_not_queued_twice()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest inflight = h.AddRequest(ObjectEffectRequest.StatusQueued, "the red car");
        await h.Db.SaveChangesAsync();

        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result!;

        r.StatusCode.Should().Be(202);
        ((ObjectAnalysisQueuedResponse)r.Value!).RequestId.Should().Be(inflight.Id);
        h.QueueCalls.Should().Be(0);
    }

    // ---- estimate -----------------------------------------------------------------------------------

    [Fact]
    public async Task Estimate_with_billing_off_writes_nothing_and_is_affordable()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 20, consented: false);

        ObjectAnalysisEstimate e = Ok(await h.Controller().Estimate(h.ProjectId, h.FileRef, new("the red car"), default));

        e.BillingEnabled.Should().BeFalse();
        e.Affordable.Should().BeTrue();
        e.Consented.Should().BeFalse();
        e.Seconds.Should().Be(20);
        await h.AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Estimate_prices_the_analysis_with_the_same_inputs_the_hold_uses()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 20.4, billing: Billing.Rich);

        ObjectAnalysisEstimate e = Ok(await h.Controller().Estimate(h.ProjectId, h.FileRef, new("the red car"), default));

        e.BillingEnabled.Should().BeTrue();
        e.Seconds.Should().Be(21);
        e.Frames.Should().Be(SegmentationCreditPricing.PricedFramesFor(21));
        e.Affordable.Should().BeTrue();
        e.Cached.Should().BeFalse();
        e.Consented.Should().BeTrue();
        h.LastPreflightStepJson.Should().NotBeNull();
        SegmentationCreditPricing.Shape(h.LastPreflightStepJson).Should().Be((1, 21), "the hold prices (targets, seconds) read out of the very config the run saves");
        SegmentationCreditPricing.AddOnMillis(h.LastPreflightStepJson)
            .Should().Be(SegmentationCreditPricing.FrameUnitsFor(21) * SegmentationCreditPricing.FrameUnitMillis);
        await h.AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Estimate_is_cached_and_free_when_a_succeeded_request_exists_and_unaffordable_when_the_balance_is_short()
    {
        using (Harness h = await Harness.CreateAsync(billing: Billing.Poor))
        {
            Ok(await h.Controller().Estimate(h.ProjectId, h.FileRef, new("the red car"), default)).Affordable.Should().BeFalse();
            h.AddRequest(ObjectEffectRequest.StatusSucceeded, "the red car");
            await h.Db.SaveChangesAsync();
            ObjectAnalysisEstimate e = Ok(await h.Controller().Estimate(h.ProjectId, h.FileRef, new("the red car"), default));
            e.Cached.Should().BeTrue();
            e.Credits.Should().Be(0);
            e.Affordable.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Estimate_reports_why_it_is_unavailable()
    {
        using Harness h = await Harness.CreateAsync(segmentationAllowed: false);
        ObjectAnalysisEstimate e = Ok(await h.Controller().Estimate(h.ProjectId, h.FileRef, new("the red car"), default));
        e.Affordable.Should().BeFalse();
        e.UnavailableReason.Should().Contain("plan");
    }

    // ---- status -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_row_with_no_execution_row_yet_is_queued()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "x");
        await h.Db.SaveChangesAsync();

        ObjectAnalysisResponse r = Get(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default));
        r.Status.Should().Be("queued");
    }

    [Fact]
    public async Task A_running_run_is_running()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "x");
        await h.AddRunAsync(row, ExecutionStatus.Running);

        Get(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default)).Status.Should().Be("running");
    }

    [Fact]
    public async Task A_passed_run_with_a_usable_track_is_succeeded_persisted_and_leaks_no_key_or_hash()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "x");
        Guid stepResult = await h.AddRunAsync(row, ExecutionStatus.Passed, StepStatus.Completed, artifact: Artifact(Track("g1"), Track("g2", empty: 60), Track("g3")));

        ObjectResult ok = h.Ok(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default));
        ObjectAnalysisResponse r = (ObjectAnalysisResponse)ok.Value!;

        r.Status.Should().Be("succeeded");
        r.AnalysisStepResultId.Should().Be(stepResult);
        r.TrackIds.Should().Equal("g1", "g3");
        r.CoverageEndSec.Should().Be(2);
        string json = JsonSerializer.Serialize(r).ToLowerInvariant();
        json.Should().NotContain("storage").And.NotContain("hash").And.NotContain("projects/").And.NotContain("maskstoragekey");
        (await h.Db.ObjectEffectRequests.AsNoTracking().SingleAsync()).Status.Should().Be("succeeded");
    }

    [Fact]
    public async Task At_most_eight_tracks_are_returned()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "x");
        await h.AddRunAsync(row, ExecutionStatus.Passed, StepStatus.Completed, artifact: Artifact(Enumerable.Range(1, 12).Select(i => Track($"g{i}")).ToArray()));

        Get(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default)).TrackIds.Should().HaveCount(ObjectAnalysisStatus.MaxTrackIds);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("allEmpty")]
    public async Task A_passed_run_with_no_usable_track_is_failed_OBJECT_NOT_FOUND(string variant)
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "x");
        string artifact = variant == "none" ? Artifact() : Artifact(Track("g1", frames: 60, empty: 60));
        await h.AddRunAsync(row, ExecutionStatus.Passed, StepStatus.Completed, artifact: artifact);

        ObjectAnalysisResponse r = Get(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default));

        r.Status.Should().Be("failed");
        r.FailureCode.Should().Be("OBJECT_NOT_FOUND");
        r.Message.Should().NotBeNullOrWhiteSpace();
        (await h.Db.ObjectEffectRequests.AsNoTracking().SingleAsync()).Status.Should().Be("failed");
    }

    [Fact]
    public async Task A_transient_artifact_read_error_stays_running_and_writes_no_terminal_state()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "x");
        await h.AddRunAsync(row, ExecutionStatus.Passed, StepStatus.Completed, artifact: Artifact(Track("g1")), unreadable: true);

        ObjectAnalysisResponse r = Get(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default));
        r.Status.Should().Be("running");
        (await h.Db.ObjectEffectRequests.AsNoTracking().SingleAsync()).Status.Should().Be("queued");

        // The next read retries and settles.
        h.Unreadable(false);
        Get(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default)).Status.Should().Be("succeeded");
        (await h.Db.ObjectEffectRequests.AsNoTracking().SingleAsync()).Status.Should().Be("succeeded");
    }

    [Fact]
    public async Task A_moderation_held_result_is_failed_with_its_own_code_not_object_not_found_and_is_not_reused()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "the red car");
        Guid result = await h.AddRunAsync(row, ExecutionStatus.Passed, StepStatus.Completed, artifact: Artifact(Track("g1")));
        await h.HoldResultAsync(result);

        ObjectAnalysisResponse r = Get(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default));
        r.Status.Should().Be("failed");
        r.FailureCode.Should().Be("RESULT_UNAVAILABLE");
        r.Message.Should().Be(ObjectAnalysisMessages.For("RESULT_UNAVAILABLE"));

        // A succeeded row whose result is held later is not reused either.
        using Harness h2 = await Harness.CreateAsync();
        ObjectEffectRequest done = h2.AddRequest(ObjectEffectRequest.StatusSucceeded, "the red car");
        Guid result2 = await h2.AddRunAsync(done, ExecutionStatus.Passed, StepStatus.Completed);
        await h2.HoldResultAsync(result2);
        ObjectResult queuedAgain = (ObjectResult)(await h2.Controller().Request(h2.ProjectId, h2.FileRef, Body, default)).Result!;
        queuedAgain.StatusCode.Should().Be(202);
        h2.QueueCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_run_maps_its_code_to_a_plain_sentence_and_the_row_keeps_it()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "x");
        await h.AddRunAsync(row, ExecutionStatus.Failed, StepStatus.Failed, failureCode: "SEGMENTATION_SOURCE_TOO_LONG");

        ObjectAnalysisResponse r = Get(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default));
        r.Status.Should().Be("failed");
        r.FailureCode.Should().Be("SEGMENTATION_SOURCE_TOO_LONG");
        r.Message.Should().Be(ObjectAnalysisMessages.For("SEGMENTATION_SOURCE_TOO_LONG"));

        // A second read comes from the stored row.
        Get(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default)).FailureCode.Should().Be("SEGMENTATION_SOURCE_TOO_LONG");
    }

    // ---- click-to-select: a seed instead of words ----------------------------------------------------

    private static ObjectSeed Tap(double atSec = 2, double x = 0.42, double y = 0.61, ObjectSeedBox? box = null) =>
        new(atSec, new ObjectSeedPoint(x, y), box);

    private static ObjectAnalysisRequestBody SeededBody(ObjectSeed? seed, string? prompt = null) =>
        new(prompt, null, "Blur", 0.8, false, 4, seed);

    [Fact]
    public async Task A_seed_without_words_queues_a_tap_only_single_track_target()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2);

        ObjectResult accepted = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, SeededBody(Tap()), default)).Result!;

        accepted.StatusCode.Should().Be(202);
        WorkflowDefinition workflow = await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync();
        VideoAnalyzeStepConfig config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(workflow.Steps.Single().VideoAnalyzeConfigJson!, Harness.ConfigJson)!;
        ObjectTrackTarget target = config.TrackObjects.Should().ContainSingle().Subject;
        target.Label.Should().Be("Selected object");
        target.NoWords.Should().BeTrue();
        target.SeedPoint.Should().Be(new ObjectSeedPoint(0.42, 0.61));
        target.AtSec.Should().Be(2);
        target.Region.Should().BeNull("no box was drawn");
        target.MatchScope.Should().Be(ObjectMatchScope.SingleTrack);
        (await h.Db.ObjectEffectRequests.SingleAsync()).MatchScope.Should().Be("SingleTrack");
        (await h.Db.ObjectEffectRequests.SingleAsync()).Prompt.Should().StartWith("Selected object #");
    }

    [Fact]
    public async Task A_drawn_box_travels_as_the_targets_region()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2);

        await h.Controller().Request(h.ProjectId, h.FileRef, SeededBody(Tap(box: new ObjectSeedBox(0.3, 0.4, 0.2, 0.3))), default);

        WorkflowDefinition workflow = await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync();
        ObjectTrackTarget target = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(workflow.Steps.Single().VideoAnalyzeConfigJson!, Harness.ConfigJson)!.TrackObjects!.Single();
        target.Region.Should().Be(new VideoInsertSurfaceQuad(0.3, 0.4, 0.5, 0.4, 0.3, 0.7, 0.5, 0.7));
    }

    [Fact]
    public async Task Words_with_a_seed_are_still_sent_and_words_without_one_are_still_required()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2);
        await h.Controller().Request(h.ProjectId, h.FileRef, SeededBody(Tap(), "the red car"), default);
        WorkflowDefinition workflow = await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync();
        ObjectTrackTarget target = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(workflow.Steps.Single().VideoAnalyzeConfigJson!, Harness.ConfigJson)!.TrackObjects!.Single();
        target.Label.Should().Be("the red car");
        target.NoWords.Should().BeFalse();

        using Harness none = await Harness.CreateAsync();
        ((ObjectResult)(await none.Controller().Request(none.ProjectId, none.FileRef, SeededBody(null), default)).Result!).StatusCode.Should().Be(400);
        await none.AssertNothingWrittenAsync();
    }

    public static IEnumerable<object[]> BadSeeds()
    {
        yield return [new ObjectSeed(2, null)];
        yield return [Tap(x: double.NaN)];
        yield return [Tap(x: 1.5)];
        yield return [Tap(y: -0.1)];
        yield return [Tap(atSec: -1)];
        yield return [Tap(atSec: double.PositiveInfinity)];
        yield return [Tap(box: new ObjectSeedBox(0.1, 0.1, 0.005, 0.3))];
        yield return [Tap(box: new ObjectSeedBox(0.9, 0.1, 0.3, 0.3))];
        yield return [Tap(box: new ObjectSeedBox(0.1, 0.1, double.NaN, 0.3))];
        yield return [Tap(atSec: 500)];
    }

    [Theory]
    [MemberData(nameof(BadSeeds))]
    public async Task A_bad_seed_is_a_400_with_a_code_and_writes_nothing(ObjectSeed seed)
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2);

        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, SeededBody(seed), default)).Result!;

        r.StatusCode.Should().Be(400);
        string json = JsonSerializer.Serialize(r.Value);
        json.Should().Contain("OBJECT_SEED_INVALID").And.NotContain("projects/");
        await h.AssertNothingWrittenAsync();

        ObjectResult estimate = (ObjectResult)(await h.Controller().Estimate(h.ProjectId, h.FileRef, new ObjectAnalysisEstimateRequest(null, null, seed), default)).Result!;
        estimate.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task A_seeded_request_keeps_the_ownership_role_consent_plan_and_rate_gates()
    {
        ObjectAnalysisRequestBody body = SeededBody(Tap());

        using (Harness h = await Harness.CreateAsync())
        {
            (await h.Controller().Request(h.ProjectId, "file:" + Guid.NewGuid(), body, default)).Result.Should().BeOfType<NotFoundResult>();
            await h.AssertNothingWrittenAsync();
        }

        using (Harness h = await Harness.CreateAsync(role: OrganizationRole.Billing))
        {
            (await h.Controller().Request(h.ProjectId, h.FileRef, body, default)).Result.Should().BeOfType<ForbidResult>();
            await h.AssertNothingWrittenAsync();
        }

        using (Harness h = await Harness.CreateAsync(segmentationAllowed: false, consented: false))
        {
            ((ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, body, default)).Result!).StatusCode.Should().Be(402);
            await h.AssertNothingWrittenAsync();
        }

        using (Harness h = await Harness.CreateAsync(consented: false))
        {
            (await h.Controller().Request(h.ProjectId, h.FileRef, body, default)).Result.Should().BeOfType<ConflictObjectResult>();
            await h.AssertNothingWrittenAsync();
        }

        using (Harness h = await Harness.CreateAsync(probeSeconds: 30, billing: Billing.Poor))
        {
            ((ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, body, default)).Result!).StatusCode.Should().Be(402);
            await h.AssertNothingWrittenAsync();
        }

        using (Harness h = await Harness.CreateAsync())
        {
            await h.SeedRecentRunsAsync(ObjectAnalysisController.PaidRunsPerHour);
            ((ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, body, default)).Result!).StatusCode.Should().Be(429);
        }
    }

    private static void AddMisses(Harness h, int count, string code = "OBJECT_NOT_FOUND_AT_POINT")
    {
        for (int i = 0; i < count; i++)
        {
            ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusFailed, $"Selected object #{i:x8}");
            row.FailureCode = code;
        }
    }

    [Fact]
    public async Task Taps_that_found_nothing_are_capped_per_hour_with_a_429_before_any_run()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2);
        AddMisses(h, ObjectAnalysisService.DefaultMaxNotFoundTapsPerHour);
        await h.Db.SaveChangesAsync();
        int rows = await h.Db.ObjectEffectRequests.CountAsync();

        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, SeededBody(Tap()), default)).Result!;

        r.StatusCode.Should().Be(429);
        JsonSerializer.Serialize(r.Value).Should().Contain("RUN_RATE_LIMITED").And.Contain("retryAfterSeconds");
        h.QueueCalls.Should().Be(0, "no run, so no vendor call");
        (await h.Db.ObjectEffectRequests.CountAsync()).Should().Be(rows);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Taps_still_in_flight_count_toward_the_cap()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2);
        for (int i = 0; i < ObjectAnalysisService.DefaultMaxNotFoundTapsPerHour; i++) h.AddRequest(ObjectEffectRequest.StatusQueued, $"Selected object #{i + 100:x8}");
        await h.Db.SaveChangesAsync();

        ((ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, SeededBody(Tap()), default)).Result!).StatusCode.Should().Be(429);
        h.QueueCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_typed_prompt_of_the_seeded_shape_is_refused_and_never_reuses_a_seeded_row()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2);
        ObjectEffectRequest seeded = h.AddRequest(ObjectEffectRequest.StatusSucceeded, "Selected object #abcd1234");
        seeded.MatchScope = "SingleTrack";
        await h.Db.SaveChangesAsync();

        foreach (string typed in new[] { "Selected object #abcd1234", "dog #abcd1234" })
        {
            ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body with { Prompt = typed, MatchScope = "SingleTrack" }, default)).Result!;
            r.StatusCode.Should().Be(400);
            JsonSerializer.Serialize(r.Value).Should().Contain("OBJECT_PROMPT_INVALID");
        }

        h.QueueCalls.Should().Be(0);
    }

    [Fact]
    public async Task Fewer_misses_than_the_cap_and_billed_finds_are_unaffected()
    {
        using Harness under = await Harness.CreateAsync(probeSeconds: 12.2);
        AddMisses(under, ObjectAnalysisService.DefaultMaxNotFoundTapsPerHour - 1);
        await under.Db.SaveChangesAsync();
        ((ObjectResult)(await under.Controller().Request(under.ProjectId, under.FileRef, SeededBody(Tap()), default)).Result!).StatusCode.Should().Be(202);

        using Harness finds = await Harness.CreateAsync(probeSeconds: 12.2);
        AddMisses(finds, 8, code: "SEGMENTATION_PROVIDER_ERROR");
        await finds.Db.SaveChangesAsync();
        ((ObjectResult)(await finds.Controller().Request(finds.ProjectId, finds.FileRef, SeededBody(Tap()), default)).Result!).StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task Not_found_runs_still_count_toward_the_paid_runs_gate()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2);
        await h.SeedRecentRunsAsync(ObjectAnalysisController.PaidRunsPerHour);
        AddMisses(h, 1);
        await h.Db.SaveChangesAsync();

        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, SeededBody(Tap()), default)).Result!;

        r.StatusCode.Should().Be(429);
        JsonSerializer.Serialize(r.Value).Should().Contain("object searches", "the 10-per-hour paid gate answered, not the tap cap");
        h.QueueCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_same_tap_is_reused_and_a_different_tap_is_a_new_search()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 12.2);

        ObjectAnalysisQueuedResponse first = (ObjectAnalysisQueuedResponse)((ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, SeededBody(Tap()), default)).Result!).Value!;
        ObjectAnalysisQueuedResponse again = (ObjectAnalysisQueuedResponse)((ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, SeededBody(Tap()), default)).Result!).Value!;
        ObjectAnalysisQueuedResponse other = (ObjectAnalysisQueuedResponse)((ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, SeededBody(Tap(x: 0.1)), default)).Result!).Value!;

        again.RequestId.Should().Be(first.RequestId);
        other.RequestId.Should().NotBe(first.RequestId);
        h.QueueCalls.Should().Be(2);
    }

    [Fact]
    public async Task The_estimate_takes_a_seed_without_words_and_prices_one_target()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 20.4, billing: Billing.Rich);

        ObjectAnalysisEstimate e = Ok(await h.Controller().Estimate(h.ProjectId, h.FileRef, new ObjectAnalysisEstimateRequest(null, null, Tap()), default));

        e.BillingEnabled.Should().BeTrue();
        e.Credits.Should().BeGreaterThan(0);
        await h.AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task A_tap_that_finds_nothing_is_failed_with_the_plain_sentence_and_its_stable_code()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "Selected object #1");
        await h.AddRunAsync(row, ExecutionStatus.Failed, StepStatus.Failed, failureCode: "OBJECT_NOT_FOUND_AT_POINT");

        ObjectAnalysisResponse r = Get(await h.Controller().Get(h.ProjectId, h.FileRef, row.Id, default));

        r.Status.Should().Be("failed");
        r.FailureCode.Should().Be("OBJECT_NOT_FOUND_AT_POINT");
        r.Message.Should().Be("We could not find an object where you tapped. Tap its centre, or draw a box.");
        ObjectAnalysisMessages.KnownCodes.Should().Contain(ObjectAnalysisMessages.ObjectNotFoundAtPoint);
    }

    [Fact]
    public async Task List_is_newest_first_for_this_source_only_and_capped()
    {
        using Harness h = await Harness.CreateAsync();
        for (int i = 0; i < 55; i++)
        {
            ObjectEffectRequest r = h.AddRequest(ObjectEffectRequest.StatusFailed, $"p{i}");
            r.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i);
        }

        h.AddRequest(ObjectEffectRequest.StatusFailed, "other source").SourceId = Guid.NewGuid().ToString("D");
        await h.Db.SaveChangesAsync();

        IReadOnlyList<ObjectAnalysisResponse> list = (IReadOnlyList<ObjectAnalysisResponse>)h.Ok(await h.Controller().List(h.ProjectId, h.FileRef, default)).Value!;
        list.Should().HaveCount(ObjectAnalysisController.MaxListed);
        list[0].Prompt.Should().Be("p54");
        list.Should().NotContain(r => r.Prompt == "other source");
    }

    // ---- tenancy ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stranger_gets_404_on_every_route()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusFailed, "x");
        await h.Db.SaveChangesAsync();
        Guid stranger = Guid.NewGuid();

        (await h.Controller(stranger).Estimate(h.ProjectId, h.FileRef, new("x"), default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller(stranger).Request(h.ProjectId, h.FileRef, Body, default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller(stranger).Get(h.ProjectId, h.FileRef, row.Id, default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller(stranger).List(h.ProjectId, h.FileRef, default)).Result.Should().BeOfType<NotFoundResult>();
        h.QueueCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_request_of_another_source_is_404()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusFailed, "x");
        await h.Db.SaveChangesAsync();

        (await h.Controller().Get(h.ProjectId, "file:" + Guid.NewGuid(), row.Id, default)).Result.Should().BeOfType<NotFoundResult>();
    }

    // ---- needs_review list and resolve ---------------------------------------------------------------

    private static async Task<Guid> AddTimelineAsync(Harness h)
    {
        EditTimelineEntity t = new() { Id = Guid.NewGuid(), ProjectId = h.ProjectId, Name = "Cut" };
        h.Db.EditTimelines.Add(t);
        await h.Db.SaveChangesAsync();
        return t.Id;
    }

    private static ObjectRequestsResponse Items(ActionResult<ObjectRequestsResponse> r) =>
        r.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<ObjectRequestsResponse>().Subject;

    [Fact]
    public async Task The_review_list_offers_only_needs_review_rows_of_that_timeline_without_keys_or_urls()
    {
        using Harness h = await Harness.CreateAsync();
        Guid timeline = await AddTimelineAsync(h);
        ObjectEffectRequest review = h.AddRequest(ObjectEffectRequest.StatusNeedsReview, "the \"plate\"");
        review.TimelineId = timeline;
        Guid clip = Guid.NewGuid();
        review.TimelineItemIdsJson = JsonSerializer.Serialize(new[] { clip.ToString("D") });
        h.AddRequest(ObjectEffectRequest.StatusApplied, "done").TimelineId = timeline;
        h.AddRequest(ObjectEffectRequest.StatusNeedsReview, "other timeline").TimelineId = Guid.NewGuid();
        await h.Db.SaveChangesAsync();
        Guid step = await h.AddRunAsync(review, ExecutionStatus.Passed, StepStatus.Completed, artifact: Artifact(Track("g1")));

        ObjectRequestsResponse body = Items(await h.Controller().ListForTimeline(h.ProjectId, timeline, "needs_review", default));

        ObjectRequestItem item = body.Items.Should().ContainSingle().Subject;
        (item.RequestId, item.MediaRef, item.AnalysisStepResultId).Should().Be((review.Id, h.FileRef, step));
        item.ClipIds.Should().Equal(clip.ToString("D"));
        item.WorkflowId.Should().Be((await h.Db.WorkflowExecutions.SingleAsync(e => e.Id == review.ExecutionId)).WorkflowDefinitionId);
        item.Prompt.Should().NotContain("\"");
        JsonSerializer.Serialize(body).ToLowerInvariant().Should().NotContain("storage").And.NotContain("http");

        (await h.Controller().ListForTimeline(h.ProjectId, timeline, "applied", default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task The_review_list_is_404_for_a_stranger_another_project_and_a_role_without_access()
    {
        using Harness h = await Harness.CreateAsync();
        Guid timeline = await AddTimelineAsync(h);
        h.AddRequest(ObjectEffectRequest.StatusNeedsReview, "x").TimelineId = timeline;
        await h.Db.SaveChangesAsync();

        (await h.Controller(Guid.NewGuid()).ListForTimeline(h.ProjectId, timeline, "needs_review", default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller().ListForTimeline(Guid.NewGuid(), timeline, "needs_review", default)).Result.Should().BeOfType<NotFoundResult>();
        (await h.Controller().ListForTimeline(h.ProjectId, Guid.NewGuid(), "needs_review", default)).Result.Should().BeOfType<NotFoundResult>();

        using Harness billing = await Harness.CreateAsync(role: OrganizationRole.Billing);
        Guid t2 = await AddTimelineAsync(billing);
        (await billing.Controller().ListForTimeline(billing.ProjectId, t2, "needs_review", default)).Result
            .Should().NotBeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(ObjectEffectRequest.StatusNeedsReview, "applied", ObjectEffectRequest.StatusApplied)]
    [InlineData(ObjectEffectRequest.StatusSucceeded, "applied", ObjectEffectRequest.StatusApplied)]
    [InlineData(ObjectEffectRequest.StatusNeedsReview, "discarded", ObjectEffectRequest.StatusDiscarded)]
    [InlineData(ObjectEffectRequest.StatusSucceeded, "discarded", ObjectEffectRequest.StatusDiscarded)]
    public async Task Resolve_moves_a_finished_row_to_applied_or_discarded_and_a_second_call_changes_nothing(string from, string action, string expected)
    {
        using Harness h = await Harness.CreateAsync();
        Guid timeline = await AddTimelineAsync(h);
        ObjectEffectRequest row = h.AddRequest(from, "x");
        row.TimelineId = timeline;
        h.Db.EditTimelineOps.Add(new EditTimelineOp { Id = Guid.NewGuid(), TimelineId = timeline, Revision = 7, OpsJson = "[]", CreatedAt = row.CreatedAt.AddSeconds(5) });
        await h.Db.SaveChangesAsync();

        for (int i = 0; i < 2; i++)
        {
            (await h.Controller().Resolve(h.ProjectId, h.FileRef, row.Id, new ObjectRequestResolveBody(action, 7), default)).Should().BeOfType<NoContentResult>();
        }

        ObjectEffectRequest after = await h.Db.ObjectEffectRequests.AsNoTracking().SingleAsync(r => r.Id == row.Id);
        after.Status.Should().Be(expected);
        after.AppliedRevision.Should().Be(action == "applied" ? 7L : null);
    }

    [Fact]
    public async Task Resolve_ignores_a_revision_that_is_not_a_real_recent_one_of_that_timeline()
    {
        using Harness h = await Harness.CreateAsync();
        Guid timeline = await AddTimelineAsync(h);
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusNeedsReview, "x");
        row.TimelineId = timeline;
        // Revision 3 exists but is older than the request; 99 does not exist.
        h.Db.EditTimelineOps.Add(new EditTimelineOp { Id = Guid.NewGuid(), TimelineId = timeline, Revision = 3, OpsJson = "[]", CreatedAt = row.CreatedAt.AddMinutes(-5) });
        await h.Db.SaveChangesAsync();

        (await h.Controller().Resolve(h.ProjectId, h.FileRef, row.Id, new ObjectRequestResolveBody("applied", 99), default)).Should().BeOfType<NoContentResult>();
        (await h.Db.ObjectEffectRequests.AsNoTracking().SingleAsync(r => r.Id == row.Id)).AppliedRevision.Should().Be(0);

        ObjectEffectRequest older = h.AddRequest(ObjectEffectRequest.StatusNeedsReview, "y");
        older.TimelineId = timeline;
        await h.Db.SaveChangesAsync();
        (await h.Controller().Resolve(h.ProjectId, h.FileRef, older.Id, new ObjectRequestResolveBody("applied", 3), default)).Should().BeOfType<NoContentResult>();
        (await h.Db.ObjectEffectRequests.AsNoTracking().SingleAsync(r => r.Id == older.Id)).AppliedRevision.Should().Be(0);
    }

    [Theory]
    [InlineData("applied")]
    [InlineData("discarded")]
    public async Task Resolve_is_404_for_a_row_the_sweeper_is_still_applying(string action)
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusApplied, "x");
        row.TimelineId = Guid.NewGuid();
        row.AppliedRevision = null;
        row.FinalizedAt = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();

        (await h.Controller().Resolve(h.ProjectId, h.FileRef, row.Id, new ObjectRequestResolveBody(action, null), default)).Should().BeOfType<NotFoundResult>();
        (await h.Db.ObjectEffectRequests.AsNoTracking().SingleAsync(r => r.Id == row.Id)).Status.Should().Be(ObjectEffectRequest.StatusApplied);
    }

    [Fact]
    public async Task Resolve_is_404_for_a_stranger_another_source_a_queued_row_and_400_for_a_bad_action()
    {
        using Harness h = await Harness.CreateAsync();
        ObjectEffectRequest ready = h.AddRequest(ObjectEffectRequest.StatusNeedsReview, "x");
        ObjectEffectRequest queued = h.AddRequest(ObjectEffectRequest.StatusQueued, "y");
        await h.Db.SaveChangesAsync();
        ObjectRequestResolveBody apply = new("applied", null);

        (await h.Controller(Guid.NewGuid()).Resolve(h.ProjectId, h.FileRef, ready.Id, apply, default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().Resolve(h.ProjectId, "file:" + Guid.NewGuid(), ready.Id, apply, default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().Resolve(h.ProjectId, h.FileRef, Guid.NewGuid(), apply, default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().Resolve(h.ProjectId, h.FileRef, queued.Id, apply, default)).Should().BeOfType<NotFoundResult>();
        (await h.Controller().Resolve(h.ProjectId, h.FileRef, ready.Id, new ObjectRequestResolveBody("nope", null), default)).Should().BeOfType<BadRequestResult>();
        (await h.Db.ObjectEffectRequests.AsNoTracking().SingleAsync(r => r.Id == ready.Id)).Status.Should().Be(ObjectEffectRequest.StatusNeedsReview);
    }

    // ---- fps cap, unknown length ---------------------------------------------------------------------

    [Theory]
    [InlineData(10.0, 30.0, 10)]
    [InlineData(10.0, 29.97, 10)]
    [InlineData(10.0, 60.0, 20)]
    [InlineData(10.2, 60.0, 21)]
    [InlineData(300.0, 60.0, 600)]
    [InlineData(300.0, 120.0, null)]
    [InlineData(601.0, 30.0, null)]
    public void The_cap_is_ceil_of_duration_times_max_fps_and_30_over_30(double duration, double fps, int? expected) =>
        ObjectAnalysisConfig.MaxSecondsFor(duration, fps).Should().Be(expected);

    [Fact]
    public void An_unknown_length_prices_and_caps_at_the_maximum() =>
        ObjectAnalysisConfig.MaxSecondsFor(null, 30).Should().Be(600);

    [Fact]
    public async Task A_60_fps_video_is_capped_and_priced_for_its_frames_and_the_estimate_matches_the_saved_cap()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 100, fps: 60, billing: Billing.Rich);

        ObjectAnalysisEstimate e = Ok(await h.Controller().Estimate(h.ProjectId, h.FileRef, new("the red car"), default));
        e.Seconds.Should().Be(200);
        e.Frames.Should().Be(SegmentationCreditPricing.PricedFramesFor(200));
        e.UpperBound.Should().BeFalse();

        await h.Controller().Request(h.ProjectId, h.FileRef, Body, default);
        VideoAnalyzeStepConfig config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(
            (await h.Db.WorkflowSteps.SingleAsync()).VideoAnalyzeConfigJson!, Harness.ConfigJson)!;
        config.MaxSegmentationSeconds.Should().Be(200);
        SegmentationCreditPricing.PricedFramesFor(config.MaxSegmentationSeconds).Should().BeGreaterThanOrEqualTo((int)Math.Ceiling(100 * 60.0));
    }

    [Fact]
    public async Task A_video_needing_more_than_600_equivalent_seconds_is_refused_up_front_naming_length_and_frame_rate()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: 400, fps: 60);

        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result!;
        r.StatusCode.Should().Be(422);
        string json = JsonSerializer.Serialize(r.Value);
        json.Should().Contain("OBJECT_SOURCE_TOO_LONG").And.Contain("6 min 40 s").And.Contain("60 frames per second");
        await h.AssertNothingWrittenAsync();

        ObjectAnalysisEstimate e = Ok(await h.Controller().Estimate(h.ProjectId, h.FileRef, new("x"), default));
        e.Affordable.Should().BeFalse();
        e.UnavailableReason.Should().Contain("60 frames per second");
    }

    [Fact]
    public async Task An_unknown_length_holds_the_maximum_and_the_estimate_says_it_is_an_upper_bound()
    {
        using Harness h = await Harness.CreateAsync(probeSeconds: null, billing: Billing.Rich);

        ObjectAnalysisEstimate e = Ok(await h.Controller().Estimate(h.ProjectId, h.FileRef, new("x"), default));
        e.UpperBound.Should().BeTrue();
        e.Seconds.Should().Be(600);

        await h.Controller().Request(h.ProjectId, h.FileRef, Body, default);
        JsonSerializer.Deserialize<VideoAnalyzeStepConfig>((await h.Db.WorkflowSteps.SingleAsync()).VideoAnalyzeConfigJson!, Harness.ConfigJson)!
            .MaxSegmentationSeconds.Should().Be(600);
    }

    // ---- estimate details ----------------------------------------------------------------------------

    [Fact]
    public async Task A_cached_estimate_with_billing_on_reports_the_real_balance()
    {
        using Harness h = await Harness.CreateAsync(billing: Billing.Rich);
        h.AddRequest(ObjectEffectRequest.StatusSucceeded, "the red car");
        await h.Db.SaveChangesAsync();

        ObjectAnalysisEstimate e = Ok(await h.Controller().Estimate(h.ProjectId, h.FileRef, new("the red car"), default));
        e.Cached.Should().BeTrue();
        e.Credits.Should().Be(0);
        e.BalanceCredits.Should().Be(100.0);
    }

    [Fact]
    public async Task The_estimate_probe_uses_the_placeholder_agent_and_a_definition_that_resolves_to_the_project()
    {
        using Harness h = await Harness.CreateAsync(billing: Billing.Rich);
        await h.Controller().Estimate(h.ProjectId, h.FileRef, new("x"), default);

        Guid placeholder = (await h.Db.AgentDefinitions.SingleAsync()).Id;
        h.LastProbe!.AgentDefinitionId.Should().Be(placeholder);
        h.LastProbe.WorkflowDefinitionId.Should().NotBe(Guid.Empty);
        h.ProbeDefinitionProject.Should().Be(h.ProjectId, "the source-footage add-on resolves the probe to its project");
    }

    // ---- concurrency ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_plans_concurrency_cap_is_a_429_and_rolls_the_workflow_and_request_back()
    {
        using Harness h = await Harness.CreateAsync(sqlite: true);
        h.QueueThrows = new PlanLimitException(new ReelBolt.Shared.Billing.PlanGateViolation("PLAN_CONCURRENCY", "concurrent_executions", "Too many at once.", 429));

        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result!;

        r.StatusCode.Should().Be(429);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await h.Db.ObjectEffectRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_in_flight_unique_index_refuses_a_second_identical_queued_row_but_not_a_finished_one()
    {
        using Harness h = await Harness.CreateAsync(sqlite: true);
        h.AddRequest(ObjectEffectRequest.StatusQueued, "the red car");
        await h.Db.SaveChangesAsync();

        h.AddRequest(ObjectEffectRequest.StatusQueued, "the red car");
        DbUpdateException ex = await Assert.ThrowsAsync<DbUpdateException>(() => h.Db.SaveChangesAsync());
        ObjectAnalysisRowWriter.IsUniqueViolation(ex).Should().BeTrue();
        h.Db.ChangeTracker.Clear();

        // finished rows and other prompts never conflict
        h.AddRequest(ObjectEffectRequest.StatusSucceeded, "the red car");
        h.AddRequest(ObjectEffectRequest.StatusQueued, "a different thing");
        await h.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_in_flight_identity_includes_the_timeline_intent()
    {
        using Harness h = await Harness.CreateAsync(sqlite: true);
        Guid timeline = Guid.NewGuid();
        h.AddRequest(ObjectEffectRequest.StatusQueued, "the red car"); // editor path: no timeline
        await h.Db.SaveChangesAsync();

        // An assistant intent on a timeline may be attached to the same in-flight analysis as a second queued row.
        h.AddRequest(ObjectEffectRequest.StatusQueued, "the red car").TimelineId = timeline;
        await h.Db.SaveChangesAsync();

        // The same timeline twice, and a second timeline-less row, are still refused.
        h.AddRequest(ObjectEffectRequest.StatusQueued, "the red car").TimelineId = timeline;
        await Assert.ThrowsAsync<DbUpdateException>(() => h.Db.SaveChangesAsync());
        h.Db.ChangeTracker.Clear();
        h.AddRequest(ObjectEffectRequest.StatusQueued, "the red car");
        await Assert.ThrowsAsync<DbUpdateException>(() => h.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task When_the_insert_loses_the_race_the_winner_is_returned_as_202()
    {
        using Harness h = await Harness.CreateAsync(sqlite: true, billing: Billing.Rich);
        // The identical request lands after the controller's reuse check and before its own insert (the price check is between them).
        ObjectEffectRequest? racing = null;
        h.OnPreflight = () =>
        {
            racing = h.AddRequest(ObjectEffectRequest.StatusQueued, "the red car");
            h.Db.SaveChanges();
        };

        ObjectResult r = (ObjectResult)(await h.Controller().Request(h.ProjectId, h.FileRef, Body, default)).Result!;

        r.StatusCode.Should().Be(202);
        ((ObjectAnalysisQueuedResponse)r.Value!).RequestId.Should().Be(racing!.Id);
        (await h.Db.ObjectEffectRequests.CountAsync()).Should().Be(1);
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        h.QueueCalls.Should().Be(0);
    }

    [Fact]
    public void IsUniqueViolation_recognises_postgres_23505_and_nothing_else()
    {
        DbUpdateException unique = new("x", new Npgsql.PostgresException("dup", "ERROR", "ERROR", "23505"));
        DbUpdateException other = new("x", new Npgsql.PostgresException("fk", "ERROR", "ERROR", "23503"));
        ObjectAnalysisRowWriter.IsUniqueViolation(unique).Should().BeTrue();
        ObjectAnalysisRowWriter.IsUniqueViolation(other).Should().BeFalse();
        ObjectAnalysisRowWriter.IsUniqueViolation(new DbUpdateException("plain")).Should().BeFalse();
    }

    [Fact]
    public async Task A_lazy_status_write_cannot_move_an_applied_row_back_to_succeeded()
    {
        using Harness h = await Harness.CreateAsync(sqlite: true);
        ObjectEffectRequest row = h.AddRequest(ObjectEffectRequest.StatusQueued, "x");
        await h.Db.SaveChangesAsync();

        // The reader holds the row as queued; a finalizer then applies it in the database.
        await h.Db.Database.ExecuteSqlRawAsync("UPDATE object_effect_requests SET status = 'applied'");

        bool moved = await ObjectAnalysisRowWriter.TryFinalizeAsync(h.Db, row, ObjectEffectRequest.StatusSucceeded, null, null, DateTime.UtcNow, default);

        moved.Should().BeFalse();
        row.Status.Should().Be("applied", "the stale row is reloaded, never overwritten");
        (await h.Db.ObjectEffectRequests.AsNoTracking().SingleAsync()).Status.Should().Be("applied");

        // And from a genuinely queued row it does move.
        ObjectEffectRequest queued = h.AddRequest(ObjectEffectRequest.StatusQueued, "y");
        await h.Db.SaveChangesAsync();
        (await ObjectAnalysisRowWriter.TryFinalizeAsync(h.Db, queued, ObjectEffectRequest.StatusFailed, "X", "m", DateTime.UtcNow, default)).Should().BeTrue();
        queued.Status.Should().Be("failed");
        queued.FailureCode.Should().Be("X");
    }

    // ---- messages and parity ------------------------------------------------------------------------

    [Fact]
    public void Every_mapped_code_has_customer_text_without_vendor_or_model_names()
    {
        ObjectAnalysisMessages.KnownCodes.Should().NotBeEmpty();
        foreach (string code in ObjectAnalysisMessages.KnownCodes)
        {
            string text = ObjectAnalysisMessages.For(code);
            text.Should().NotBeNullOrWhiteSpace(code);
            text.ToLowerInvariant().Should().NotContainAny("fal.ai", "sam 3", "sam-3", "model", "provider", "segmentation");
        }

        ObjectAnalysisMessages.For("SOMETHING_NEW").Should().Contain("SOMETHING_NEW");
        ObjectAnalysisMessages.For(null).Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("  the   red\tcar ")]
    [InlineData("a\u0000b\nc")]
    [InlineData("")]
    [InlineData(null)]
    public void Shared_prompt_sanitizer_matches_the_engines(string? raw) =>
        SegmentationPrompt.Sanitize(raw).Should().Be(SegmentationTrackingService.SanitizePrompt(raw));

    [Fact]
    public void Shared_prompt_sanitizer_matches_the_engines_cap() =>
        SegmentationPrompt.Sanitize(new string('x', 400)).Should().Be(SegmentationTrackingService.SanitizePrompt(new string('x', 400)))
            .And.HaveLength(SegmentationTrackingService.MaxPromptChars);

    [Fact]
    public void An_object_analysis_step_is_a_paid_run()
    {
        VideoAnalyzeStepConfig config = ObjectAnalysisConfig.Build(new VideoSourceRef(VideoSourceKind.ProjectFile, Guid.NewGuid()), "car", ObjectMatchScope.AllInstances, 10);
        config.CallsAnyProvider.Should().BeTrue();
        RunRateClassifier.Classify([new WorkflowStep { StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = ObjectAnalysisConfig.Serialize(config) }])
            .Should().Be(RunRateBucket.Paid);
    }

    private static T Ok<T>(ActionResult<T> r) => (T)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    private static ObjectAnalysisResponse Get(ActionResult<ObjectAnalysisResponse> r) => Ok(r);

    // ---- harness ------------------------------------------------------------------------------------

    private enum Billing { Off, Rich, Poor }

    private sealed class Harness : IDisposable
    {
        public static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid OwnerId { get; init; }
        public Guid FileId { get; init; }
        public Guid AudioFileId { get; init; }
        public string FileRef => "file:" + FileId;
        public int QueueCalls { get; private set; }
        public WorkflowDefinitionKind? LastQueuedKind { get; private set; }
        public Guid? LastQueuedExecutionId { get; private set; }
        public string? LastPreflightStepJson { get; private set; }
        public WorkflowStep? LastProbe { get; private set; }
        public Guid? ProbeDefinitionProject { get; private set; }
        public Exception? QueueThrows { get; set; }
        public Action? OnPreflight { get; set; }
        public void Unreadable(bool value) => _unreadable = value;
        private System.Data.Common.DbConnection? _connection;

        public async Task HoldResultAsync(Guid resultId)
        {
            Project p = await Db.Projects.IgnoreQueryFilters().SingleAsync(x => x.Id == ProjectId);
            p.CsamOutputHoldAt = DateTime.UtcNow.AddDays(-1);
            WorkflowStepResult r = await Db.WorkflowStepResults.SingleAsync(x => x.Id == resultId);
            r.ExecutedAt = DateTime.UtcNow.AddDays(-2);
            await Db.SaveChangesAsync();
        }

        private bool _verified;
        private OrganizationRole _role;
        private bool _segmentationAllowed;
        private Billing _billing;
        private string _artifact = Artifact();
        private bool _unreadable;
        private readonly Mock<IFileStorageService> _storage = new();

        public static async Task<Harness> CreateAsync(
            bool verified = true, OrganizationRole role = OrganizationRole.Member, bool segmentationAllowed = true,
            bool consented = true, double? probeSeconds = 10, Billing billing = Billing.Off, double fps = 30, bool sqlite = false)
        {
            System.Data.Common.DbConnection? connection = null;
            InferenceApiDbContext db;
            if (sqlite)
            {
                (Microsoft.Data.Sqlite.SqliteConnection c, InferenceApiDbContext sqliteDb) = ReelBolt.WorkflowEngine.Tests.Controllers.WorkflowAssistantControllerTests.CreateSqliteContext();
                connection = c;
                db = sqliteDb;
                // The in-flight identity is an expression index created by the migration's raw SQL (the model cannot declare it), so the
                // fixture, which builds from the model, creates the same index (SQLite spelling of the same expression).
                db.Database.ExecuteSqlRaw(
                    "CREATE UNIQUE INDEX ux_object_effect_requests_inflight ON object_effect_requests " +
                    "(project_id, source_kind, source_id, prompt, match_scope, coalesce(timeline_id, '00000000-0000-0000-0000-000000000000')) WHERE status = 'queued'");
            }
            else
            {
                db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                    .Options);
            }

            Guid owner = Guid.NewGuid();
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "p",
                OwnerId = owner,
                OrganizationId = TestTenancy.OrgOf(owner),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ObjectEffectsConsentAt = consented ? DateTime.UtcNow : null
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

            return new Harness
            {
                Db = db,
                ProjectId = project.Id,
                OwnerId = owner,
                FileId = video.Id,
                AudioFileId = audio.Id,
                _verified = verified,
                _role = role,
                _segmentationAllowed = segmentationAllowed,
                _billing = billing
            };
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
            u.SetupGet(x => x.OrgRole).Returns(_role);
            return u;
        }

        public ObjectAnalysisController Controller(Guid? user = null)
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

            _storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(() => _unreadable
                    ? throw new IOException("gone")
                    : Task.FromResult((Stream)new MemoryStream(Encoding.UTF8.GetBytes(_artifact))));

            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { Tier = "trial", SegmentationAllowed = _segmentationAllowed }));

            Mock<IWorkflowCostEstimator> costs = new();
            costs.SetupGet(c => c.BillingEnabled).Returns(_billing != Billing.Off);
            costs.Setup(c => c.PreflightAsync(It.IsAny<Guid?>(), It.IsAny<IReadOnlyList<WorkflowStep>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid? _, IReadOnlyList<WorkflowStep> steps, CancellationToken _) =>
                {
                    LastPreflightStepJson = steps[0].VideoAnalyzeConfigJson;
                    LastProbe = steps[0];
                    ProbeDefinitionProject = SourceFootageDefinitions.Current is { } hints && hints.TryGetValue(steps[0].WorkflowDefinitionId, out Guid hinted) ? hinted : null;
                    OnPreflight?.Invoke();
                    long balance = _billing == Billing.Rich ? 100_000 : 100;
                    WorkflowCostEstimate estimate = new(true, false, 500, 900, 900, []);
                    return new WorkflowPreflightResult(
                        balance < 900 ? WorkflowPreflightOutcome.InsufficientCredits : WorkflowPreflightOutcome.Allowed, estimate, balance);
                });

            return new ObjectAnalysisController(
                Db, u.Object, TestTenancy.Access(Db, u.Object), executions.Object, _storage.Object,
                NullLogger<ObjectAnalysisController>.Instance, TimeProvider.System, entitlements.Object, costs.Object);
        }

        public ObjectResult Ok<T>(ActionResult<T> r) => new(r.Result.Should().BeOfType<OkObjectResult>().Subject.Value);

        public ObjectEffectRequest AddRequest(string status, string prompt)
        {
            ObjectEffectRequest row = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = TestTenancy.OrgOf(OwnerId),
                ProjectId = ProjectId,
                ExecutionId = Guid.NewGuid(),
                SourceKind = ObjectEffectRequest.SourceFile,
                SourceId = FileId.ToString("D"),
                Prompt = prompt,
                MatchScope = ObjectMatchScope.AllInstances.ToString(),
                Effect = "Blur",
                Strength = 0.5,
                Status = status,
                CreatedByUserId = OwnerId,
                CreatedAt = DateTime.UtcNow
            };
            Db.ObjectEffectRequests.Add(row);
            return row;
        }

        public async Task<Guid> AddRunAsync(
            ObjectEffectRequest row, ExecutionStatus status, StepStatus? step = null, string? artifact = null,
            string? failureCode = null, bool unreadable = false)
        {
            _artifact = artifact ?? Artifact();
            _unreadable = unreadable;
            Guid definition = Guid.NewGuid(), stepId = Guid.NewGuid(), result = Guid.NewGuid();
            Db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = definition,
                ProjectId = ProjectId,
                Name = "w",
                Kind = WorkflowDefinitionKind.ObjectAnalysis,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            Db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = row.ExecutionId,
                WorkflowDefinitionId = definition,
                ProjectId = ProjectId,
                OrganizationId = TestTenancy.OrgOf(OwnerId),
                Status = status
            });
            Db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = definition, StepOrder = 1, StepType = StepType.VideoAnalyze });
            if (step is { } s)
            {
                Db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = result,
                    WorkflowExecutionId = row.ExecutionId,
                    WorkflowStepId = stepId,
                    Status = s,
                    FailureCode = failureCode,
                    CompletedAt = DateTime.UtcNow,
                    ArtifactStorageKey = $"projects/{ProjectId}/agentFiles/video-analysis/r/a.json"
                });
            }

            await Db.SaveChangesAsync();
            return result;
        }

        public async Task SeedRecentRunsAsync(int count)
        {
            Guid definition = Guid.NewGuid();
            Db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = definition,
                ProjectId = ProjectId,
                Name = "w",
                Kind = WorkflowDefinitionKind.ObjectAnalysis,
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
            (await Db.ObjectEffectRequests.CountAsync()).Should().Be(0);
            QueueCalls.Should().Be(0);
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection?.Dispose();
        }
    }
}
