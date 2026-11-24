using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Enhance;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>StepType.VideoEnhance</c> end to end over a faked media session, a fake local enhancer, a fake remux and fake fal
/// clients, with the REAL budget gate over an in-memory ledger: no live vendor call, no real ffmpeg. Covers the refusal order
/// (and that a refusal uploads nothing), the local free path, the hosted path with its ledger row, resume without
/// re-submitting, the settle/refund classes, the length check and the shape of the output.
/// </summary>
public sealed class VideoEnhanceStepExecutorTests
{
    private static readonly Guid ProjectId = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly Guid OrgId = Guid.Parse("33333333-0000-0000-0000-000000000002");
    private static readonly string OutputPrefix = $"projects/{ProjectId}/outputFiles/";
    private const string SourceKey = "projects/22222222-0000-0000-0000-000000000002/userFiles/take.mp4";

    private static string Source => """{"kind":"StepOutput","stepOrder":1}""";

    private static string Local(string op = "Denoise", string extra = ",\"denoiseStrength\":\"Light\"") =>
        $$"""{"op":"{{op}}","engine":"Local","source":{{Source}}{{extra}}}""";

    private static string Hosted(string op = "Upscale", bool consent = true, string extra = ",\"upscaleFactor\":2") =>
        $$"""{"op":"{{op}}","engine":"Hosted","source":{{Source}},"allowSourceMediaEgress":{{(consent ? "true" : "false")}}{{extra}}}""";

    // ---------------- local operations ----------------

    [Fact]
    public async Task A_local_operation_runs_the_local_enhancer_with_no_network_no_plan_check_and_no_usage()
    {
        Fixture f = new() { VideoEnhanceAllowed = false };
        StepExecutionResult result = await f.RunAsync(Local());

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        result.OutputStorageKey.Should().StartWith(OutputPrefix).And.EndWith("/step-2-enhanced.mp4");
        f.Enhancer.Requests.Should().ContainSingle().Which.Op.Should().Be(VideoEnhanceOp.Denoise);
        f.NothingLeftTheHost();
        f.Resolver.Verify(r => r.ResolveVideoUpscaleAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        f.Files.Verify(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
        f.LedgerRows().Should().BeEmpty("a local operation never touches the generation ledger");

        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
        output["op"]!.GetValue<string>().Should().Be("Denoise");
        output["engine"]!.GetValue<string>().Should().Be("Local");
        output["method"]!.GetValue<string>().Should().Be("local:denoise-light");
        output["durationSec"]!.GetValue<double>().Should().Be(12.5);
        output["outputWidth"]!.GetValue<int>().Should().Be(1920);
        output["outputHeight"]!.GetValue<int>().Should().Be(1080);
        output["outputFps"]!.GetValue<double>().Should().Be(30);
        output["sizeBytes"]!.GetValue<long>().Should().BeGreaterThan(0);
        output["billedSeconds"].Should().BeNull();
        output["outputFileId"].Should().NotBeNull();
        output["warnings"]!.AsArray().Should().BeEmpty();
        result.Output.Should().NotContain("projects/").And.NotContain("storageKey").And.NotContain("http");
        f.Session.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task The_result_is_registered_under_the_source_name_and_a_plain_label()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync(Local("Upscale", ",\"upscaleFactor\":2"));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        f.Registered.Should().ContainSingle().Which.Name.Should().Be("take (sharper 2x).mp4");

        Fixture off = new();
        (await off.RunAsync(Local("MotionBlur", ",\"registerProjectFile\":false"))).Status.Should().Be(StepStatus.Completed);
        off.Registered.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Upscale", ",\"upscaleFactor\":2", "sharper 2x")]
    [InlineData("Upscale", ",\"upscaleFactor\":4", "sharper 4x")]
    [InlineData("Upscale", ",\"targetHeight\":2160", "4K")]
    [InlineData("Upscale", ",\"targetHeight\":1440", "1440p")]
    [InlineData("Upscale", ",\"targetHeight\":1080", "1080p")]
    [InlineData("Upscale", ",\"targetHeight\":720", "720p")]
    [InlineData("Interpolate", ",\"frameMultiplier\":4", "smooth 4x")]
    [InlineData("Denoise", ",\"denoiseStrength\":\"Strong\"", "clean")]
    [InlineData("MotionBlur", "", "motion blur")]
    public void Every_operation_has_one_plain_english_label(string op, string extra, string label)
    {
        VideoEnhanceStepConfig config = System.Text.Json.JsonSerializer.Deserialize<VideoEnhanceStepConfig>(Local(op, extra), VideoEnhanceStepConfig.JsonOptions)!;
        VideoEnhanceStepExecutor.LabelFor(config).Should().Be(label);
        VideoEnhanceStepExecutor.RegisteredName("My: clip/1", config).Should().Be($"My clip1 ({label}).mp4");
    }

    [Fact]
    public async Task A_local_source_over_the_cap_is_refused_before_any_enhancement()
    {
        Fixture f = new() { SourceSeconds = 700 };
        StepExecutionResult result = await f.RunAsync(Local());

        result.FailureCode.Should().Be("ENHANCE_SOURCE_TOO_LONG");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        f.Enhancer.Requests.Should().BeEmpty();

        Fixture lowered = new() { SourceSeconds = 50 };
        (await lowered.RunAsync(Local(extra: ",\"denoiseStrength\":\"Light\",\"maxSourceSeconds\":30"))).FailureCode.Should().Be("ENHANCE_SOURCE_TOO_LONG");
    }

    [Fact]
    public async Task A_local_enhancer_exception_is_a_coded_failure_with_the_class_of_its_code()
    {
        Fixture f = new();
        f.Enhancer.Throw = new EnhanceException(EnhanceErrorCodes.DurationMismatch, "output", "drifted");
        StepExecutionResult result = await f.RunAsync(Local());

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("ENHANCE_DURATION_MISMATCH");
        result.FailureClass.Should().Be(FailureClass.PlatformProviderError);

        Fixture bad = new();
        bad.Enhancer.Throw = new EnhanceException("ENHANCE_NOT_LARGER", "targetHeight", "not larger");
        StepExecutionResult refused = await bad.RunAsync(Local("Upscale", ",\"targetHeight\":720"));
        refused.FailureCode.Should().Be("ENHANCE_NOT_LARGER");
        refused.FailureClass.Should().Be(FailureClass.UserInput);
    }

    [Fact]
    public async Task A_source_with_no_picture_is_a_user_input_failure()
    {
        Fixture f = new();
        f.Probe.Video = false;
        StepExecutionResult result = await f.RunAsync(Local());

        result.FailureCode.Should().Be("ENHANCE_SOURCE_NO_VIDEO");
        result.FailureClass.Should().Be(FailureClass.UserInput);
    }

    [Fact]
    public async Task An_invalid_config_is_refused_at_run_time_naming_the_field()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"op":"Upscale","engine":"Local","source":{"kind":"GeneratedClips"}}""");

        result.FailureCode.Should().Be("CONFIG_INVALID");
        result.Output.Should().Contain("GeneratedClips").And.Contain("upscaleFactor");
        f.Enhancer.Requests.Should().BeEmpty();

        (await new Fixture().RunAsync(Hosted("Denoise", extra: ",\"denoiseStrength\":\"Light\""))).Output.Should().Contain("local operation");
    }

    // ---------------- hosted refusals, in order, uploading nothing ----------------

    [Fact]
    public async Task The_plan_feature_is_checked_before_the_consent()
    {
        Fixture f = new() { VideoEnhanceAllowed = false };
        StepExecutionResult result = await f.RunAsync(Hosted(consent: false));

        result.FailureCode.Should().Be("PLAN_NOT_ENTITLED");
        result.FailureClass.Should().Be(FailureClass.EntitlementDenied);
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty();
    }

    [Fact]
    public async Task Without_consent_a_hosted_operation_is_refused_before_a_provider_is_resolved_and_uploads_nothing()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync(Hosted(consent: false));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("EGRESS_REFUSED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.Output.Should().Contain("Nothing was uploaded");
        f.NothingLeftTheHost();
        f.Resolver.Verify(r => r.ResolveVideoUpscaleAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        f.LedgerRows().Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_provider_a_hosted_operation_is_refused_before_the_source_is_read()
    {
        Fixture f = new() { Row = null };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("NO_VIDEO_ENHANCE_PROVIDER");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        f.NothingLeftTheHost();
        f.Session.ProbeCalls.Should().BeEmpty("the provider is checked before the source is probed");
    }

    [Fact]
    public async Task A_hosted_source_over_the_120_second_cap_is_refused_from_the_probe_before_any_upload()
    {
        Fixture f = new() { SourceSeconds = 121 };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("ENHANCE_SOURCE_TOO_LONG");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty("no ledger row is reserved for a refused source");

        Fixture exact = new() { SourceSeconds = 120 };
        (await exact.RunAsync(Hosted())).Status.Should().Be(StepStatus.Completed);

        Fixture configured = new() { SourceSeconds = 50, Configuration = Config(("VideoEnhance:Hosted:MaxSourceSeconds", "40")) };
        (await configured.RunAsync(Hosted())).FailureCode.Should().Be("ENHANCE_SOURCE_TOO_LONG");
    }

    [Theory]
    [InlineData(",\"sourceWidth\":1280,\"sourceHeight\":720,\"sourceFps\":30,\"maxSourceSeconds\":13", 12.5)]
    [InlineData(",\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":24,\"maxSourceSeconds\":13", 12.5)]
    [InlineData(",\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":30,\"maxSourceSeconds\":10", 12.5)]
    public async Task A_source_that_is_now_bigger_faster_or_longer_than_the_one_priced_is_ENHANCE_SOURCE_CHANGED_and_refunded(string hints, double seconds)
    {
        Fixture f = new() { SourceSeconds = seconds };
        StepExecutionResult result = await f.RunAsync(Hosted(extra: ",\"upscaleFactor\":2" + hints));

        result.FailureCode.Should().Be("ENHANCE_SOURCE_CHANGED");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal, "the hold is returned: nothing the user typed is wrong");
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_that_matches_its_hints_is_enhanced_and_a_smaller_one_is_fine()
    {
        Fixture same = new() { SourceSeconds = 12.2 };
        (await same.RunAsync(Hosted(extra: ",\"upscaleFactor\":2,\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":30,\"maxSourceSeconds\":13")))
            .Status.Should().Be(StepStatus.Completed);

        Fixture smaller = new() { SourceSeconds = 8 };
        (await smaller.RunAsync(Hosted(extra: ",\"upscaleFactor\":2,\"sourceWidth\":3840,\"sourceHeight\":2160,\"sourceFps\":60,\"maxSourceSeconds\":13")))
            .Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task An_unpriced_model_is_refused_before_the_upload()
    {
        Fixture f = new() { Priced = false };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("CONFIG_INVALID");
        result.Output.Should().Contain("Nothing was uploaded");
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_deployment_without_a_presigner_refuses_a_hosted_operation_and_a_local_one_does_not_care()
    {
        Fixture f = new() { HasPresigner = false };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("ENHANCE_EGRESS_UNAVAILABLE");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        f.NothingLeftTheHost();

        Fixture local = new() { HasPresigner = false };
        (await local.RunAsync(Local())).Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task The_plans_output_height_is_checked_before_any_work_and_after_the_probe_for_a_factor()
    {
        Fixture target = new() { MaxOutputHeight = 1080 };
        StepExecutionResult byTarget = await target.RunAsync(Local("Upscale", ",\"targetHeight\":2160"));
        byTarget.FailureCode.Should().Be("PLAN_NOT_ENTITLED");
        byTarget.FailureClass.Should().Be(FailureClass.EntitlementDenied);
        target.Session.ProbeCalls.Should().BeEmpty("a target above the plan is refused before the source is even read");
        target.Enhancer.Requests.Should().BeEmpty();

        // A factor: 1080p x 2 = 2160 short side, above a 1080 plan; known only after the probe, still before any enhancement.
        Fixture factor = new() { MaxOutputHeight = 1080 };
        StepExecutionResult byFactor = await factor.RunAsync(Hosted());
        byFactor.FailureCode.Should().Be("PLAN_NOT_ENTITLED");
        factor.NothingLeftTheHost();

        Fixture fits = new() { MaxOutputHeight = 2160 };
        (await fits.RunAsync(Local("Upscale", ",\"targetHeight\":2160"))).Status.Should().Be(StepStatus.Completed);
    }

    // ---------------- the hosted path ----------------

    [Fact]
    public async Task A_hosted_upscale_uploads_presigns_submits_once_conforms_the_original_audio_and_meters_the_billed_seconds()
    {
        Fixture f = new() { SourceSeconds = 12.2 };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        f.Uploaded.Should().ContainSingle().Which.Should().Be($"projects/{ProjectId}/agentFiles/video-enhance/{f.ExecutionId:D}/source-2.mp4");
        f.Presigned.Should().ContainSingle().Which.Ttl.Should().Be(TimeSpan.FromMinutes(10));
        f.Deleted.Should().Equal(f.Uploaded, "the temporary upload is deleted in the finally");

        VideoUpscaleRequest request = f.UpscaleRequests.Should().ContainSingle().Which;
        request.SourceUrl.Should().Be("https://signed.example/get");
        request.Factor.Should().Be(2);
        request.SourceWidth.Should().Be(1920);
        request.SourceHeight.Should().Be(1080);
        request.SourceDurationSec.Should().Be(12.2);

        // The picture is conformed to the REQUEST's geometry (never the vendor's) with the source's own audio.
        RemuxTarget target = f.Remux.Targets.Should().ContainSingle().Which!;
        target.Should().Be(new RemuxTarget(3840, 2160, 30, 1));
        f.Remux.SourcePaths.Should().ContainSingle().Which.Should().Be("video-enhance-source");

        // The ledger row holds the vendor handle and ends Succeeded; usage is the billed output seconds at the output tier.
        ExternalGenerationJob row = f.LedgerRows().Should().ContainSingle().Which;
        row.ClipId.Should().Be("enhance-upscale");
        row.Status.Should().Be(ExternalGenerationJobStatus.Succeeded);
        row.RemoteJobId.Should().Be(VideoEnhanceHandleCodec.Encode(f.Handle));
        row.CostUnit.Should().Be(ExternalGenerationJob.CreditsUnit);
        row.ActualCost.Should().BeGreaterThan(0m);

        UsageRecordRequest usage = f.Recorded.Should().ContainSingle().Which;
        usage.Kind.Should().Be(UsageKind.VideoEnhanceSeconds);
        usage.Quantity.Should().Be(13m, "12.2 s is billed as 13 whole seconds");
        usage.Variant.Should().Be("uhd");
        usage.Capability.Should().Be(InferenceProviderCapability.VideoUpscale);
        usage.ModelName.Should().Be(VideoEnhanceModelCatalog.TopazId);
        usage.IdempotencyKey.Should().Be($"videoenhance:{row.Id:N}");

        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
        output["method"]!.GetValue<string>().Should().Be($"fal:{VideoEnhanceModelCatalog.TopazId}");
        output["billedSeconds"]!.GetValue<long>().Should().Be(13);
        output["outputWidth"]!.GetValue<int>().Should().Be(3840);
        result.Output.Should().NotContain("signed.example").And.NotContain("projects/").And.NotContain("storageKey");
        f.Registered.Should().ContainSingle().Which.Name.Should().Be("take (sharper 2x).mp4");
    }

    [Fact]
    public async Task A_hosted_interpolation_meters_the_compute_second_hold_not_a_vendor_figure()
    {
        Fixture f = new() { SourceSeconds = 10, FramesPerSecondNumerator = 30 };
        f.UseInterpolationRow();
        StepExecutionResult result = await f.RunAsync(Hosted("Interpolate", extra: ",\"frameMultiplier\":2"));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        f.InterpolationRequests.Should().ContainSingle().Which.Multiplier.Should().Be(2);
        f.Remux.Targets.Should().ContainSingle().Which.Should().Be(new RemuxTarget(1920, 1080, 60, 1));

        UsageRecordRequest usage = f.Recorded.Should().ContainSingle().Which;
        usage.Kind.Should().Be(UsageKind.VideoEnhanceComputeSeconds);
        // 300 source frames x 2 = 600 output frames x 0.05 s x (1920x1080 / 1080p) = 30 compute seconds.
        usage.Quantity.Should().Be(30m);
        usage.Variant.Should().BeNull();
        usage.Capability.Should().Be(InferenceProviderCapability.FrameInterpolation);
        f.LedgerRows().Single().ClipId.Should().Be("enhance-interpolate");
    }

    private static string Extend(string extra = ",\"targetAspect\":\"1:1\"") => Hosted("Extend", extra: extra);

    [Fact]
    public async Task A_hosted_extend_screens_the_prompt_uploads_submits_once_conforms_to_the_new_canvas_and_meters_wan_video_seconds()
    {
        Fixture f = new() { SourceSeconds = 8, FramesPerSecondNumerator = 30 };
        f.UseOutpaintRow();
        StepExecutionResult result = await f.RunAsync(Extend(",\"targetAspect\":\"1:1\",\"prompt\":\"  a calm sky \""));

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        f.ModeratedTexts.Should().ContainSingle().Which.Should().Be("a calm sky");

        VideoOutpaintRequest request = f.OutpaintRequests.Should().ContainSingle().Which;
        request.TargetAspect.Should().Be("1:1");
        request.Prompt.Should().Be("a calm sky");
        request.SourceWidth.Should().Be(1920);
        request.SourceFrames.Should().Be(240);
        f.Deleted.Should().Equal(f.Uploaded, "the temporary upload is deleted in the finally");

        // The picture is conformed to the CANVAS the request computed, at the source's own rate, with the source's audio.
        f.Remux.Targets.Should().ContainSingle().Which.Should().Be(new RemuxTarget(1920, 1920, 30, 1));

        f.LedgerRows().Should().ContainSingle().Which.ClipId.Should().Be("enhance-extend");
        UsageRecordRequest usage = f.Recorded.Should().ContainSingle().Which;
        usage.Kind.Should().Be(UsageKind.VideoEnhanceSeconds);
        usage.Quantity.Should().Be(15m, "240 frames at 16 fps is 15 video seconds");
        usage.Variant.Should().Be("720p");
        usage.Capability.Should().Be(InferenceProviderCapability.VideoOutpaint);
        usage.ModelName.Should().Be(VideoEnhanceModelCatalog.WanVaceId);
    }

    [Fact]
    public async Task A_blank_extend_prompt_sends_the_neutral_default_and_is_not_moderated_as_customer_text()
    {
        Fixture f = new() { SourceSeconds = 8 };
        f.UseOutpaintRow();

        (await f.RunAsync(Extend())).Status.Should().Be(StepStatus.Completed);

        f.OutpaintRequests.Should().ContainSingle().Which.Prompt.Should().Be(VideoEnhanceStepConfig.DefaultExtendPrompt);
        f.ModeratedTexts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blocked_extend_prompt_is_CONTENT_BLOCKED_before_anything_is_reserved_or_uploaded()
    {
        Fixture f = new() { SourceSeconds = 8, ModerationVerdict = ModerationAction.Block };
        f.UseOutpaintRow();

        StepExecutionResult result = await f.RunAsync(Extend(",\"targetAspect\":\"1:1\",\"prompt\":\"anything\""));

        result.FailureCode.Should().Be("CONTENT_BLOCKED");
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty();
    }

    [Fact]
    public async Task An_extend_source_over_the_241_frame_ceiling_is_SOURCE_TOO_LONG_with_the_cap_in_seconds_and_uploads_nothing()
    {
        Fixture f = new() { SourceSeconds = 9, FramesPerSecondNumerator = 30 };
        f.UseOutpaintRow();

        StepExecutionResult result = await f.RunAsync(Extend());

        result.FailureCode.Should().Be("ENHANCE_SOURCE_TOO_LONG");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.ErrorDetails.Should().Contain("8-second limit");
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty();
    }

    [Fact]
    public async Task An_extend_source_under_the_81_frame_floor_is_SOURCE_TOO_SHORT_refunded_and_uploads_nothing()
    {
        Fixture f = new() { SourceSeconds = 2, FramesPerSecondNumerator = 30 };
        f.UseOutpaintRow();

        StepExecutionResult result = await f.RunAsync(Extend());

        result.FailureCode.Should().Be("ENHANCE_SOURCE_TOO_SHORT");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.ErrorDetails.Should().Contain("3-second minimum");
        FailureClassifier.ForCode("ENHANCE_SOURCE_TOO_SHORT").Should().Be(FailureClass.UserInput);
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty();

        Fixture exact = new() { SourceSeconds = 2.7, FramesPerSecondNumerator = 30 };
        exact.UseOutpaintRow();
        (await exact.RunAsync(Extend())).Status.Should().Be(StepStatus.Completed);
    }

    [Theory]
    [InlineData(",\"targetAspect\":\"16:9\"")]
    [InlineData(",\"targetAspect\":\"9:16\"")]
    public async Task An_extend_the_picture_cannot_offer_is_refused_before_any_upload(string extra)
    {
        // 1920x1080 is already 16:9 (nothing to extend) and cannot reach 9:16 inside half a side.
        Fixture f = new() { SourceSeconds = 8 };
        f.UseOutpaintRow();

        StepExecutionResult result = await f.RunAsync(Extend(extra));

        result.FailureCode.Should().Be("CONFIG_INVALID");
        f.NothingLeftTheHost();
    }

    [Fact]
    public async Task Extend_without_consent_is_refused_and_a_local_extend_is_a_config_error()
    {
        Fixture f = new() { SourceSeconds = 8 };
        f.UseOutpaintRow();

        (await f.RunAsync(Hosted("Extend", consent: false, extra: ",\"targetAspect\":\"1:1\"")))
            .FailureCode.Should().Be(GeneratedShotEgressPolicy.EgressRefusedCode);
        f.NothingLeftTheHost();

        StepExecutionResult local = await f.RunAsync(Local("Extend", ",\"targetAspect\":\"1:1\""));
        local.FailureCode.Should().Be("CONFIG_INVALID");
        local.ErrorDetails.Should().Contain("Extend is a hosted operation");
    }

    [Fact]
    public async Task A_resumed_attempt_polls_the_stored_request_and_never_submits_or_uploads_again()
    {
        Fixture f = new();
        f.ResumeFailsOnce = true;
        StepExecutionResult first = await f.RunAsync(Hosted());

        first.Status.Should().Be(StepStatus.Failed);
        first.FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        f.UpscaleRequests.Should().ContainSingle();
        ExternalGenerationJob row = f.LedgerRows().Should().ContainSingle().Which;
        row.Status.Should().Be(ExternalGenerationJobStatus.Submitted, "the row keeps the handle and stays charged");
        row.RemoteJobId.Should().NotBeNullOrEmpty();
        f.Deleted.Should().Equal(f.Uploaded);

        // The restarted attempt of the same step result finds the row, polls it, and does not touch the vendor's submit again.
        StepExecutionResult second = await f.RunAsync(Hosted(), sameStepResult: true);

        second.Status.Should().Be(StepStatus.Completed, second.Output);
        f.UpscaleRequests.Should().ContainSingle("no second submit");
        f.Uploaded.Should().ContainSingle("no second upload");
        f.ResumedHandles.Should().HaveCount(2);
        f.ResumedHandles[1].Should().Be(f.ResumedHandles[0]);
        f.LedgerRows().Should().ContainSingle().Which.Status.Should().Be(ExternalGenerationJobStatus.Succeeded);
        f.Recorded.Should().ContainSingle("the delivered result is metered once");
    }

    [Fact]
    public async Task A_reserved_row_with_no_handle_is_RESERVATION_ORPHANED_and_is_not_submitted_again()
    {
        Fixture f = new();
        f.SubmitThrows = new TimeoutException("the connection dropped");
        StepExecutionResult first = await f.RunAsync(Hosted());
        first.FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        first.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        f.LedgerRows().Single().Status.Should().Be(ExternalGenerationJobStatus.Reserved);

        f.SubmitThrows = null;
        StepExecutionResult second = await f.RunAsync(Hosted(), sameStepResult: true);

        second.FailureCode.Should().Be("RESERVATION_ORPHANED");
        f.UpscaleRequests.Should().HaveCount(1, "the orphan is never re-submitted");
    }

    [Fact]
    public async Task A_definite_vendor_rejection_releases_the_row_and_blames_the_request_or_the_vendor_by_its_code()
    {
        Fixture f = new();
        f.SubmitThrows = new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid, "bad factor", 422);
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("ENHANCE_REQUEST_INVALID");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        f.LedgerRows().Single().Status.Should().Be(ExternalGenerationJobStatus.Failed);
        f.Recorded.Should().BeEmpty();
        f.Deleted.Should().Equal(f.Uploaded);

        Fixture quota = new();
        quota.SubmitThrows = new VideoEnhanceException(VideoEnhanceErrorKind.VendorQuota, "account out of credit https://secret.example/x", 402);
        StepExecutionResult q = await quota.RunAsync(Hosted());
        q.FailureCode.Should().Be("ENHANCE_VENDOR_QUOTA");
        q.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        q.Output.Should().NotContain("secret.example", "a platform account refusal is one fixed sentence");

        Fixture byo = new() { OrgOwned = true };
        byo.SubmitThrows = new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed, "model crashed", 500);
        StepExecutionResult b = await byo.RunAsync(Hosted());
        b.FailureCode.Should().Be("ENHANCE_VENDOR_FAILED");
        b.FailureClass.Should().Be(FailureClass.ByoProviderError);
        byo.LedgerRows().Single().Status.Should().Be(ExternalGenerationJobStatus.Reserved, "the vendor may have run it, so it stays charged");
    }

    [Fact]
    public async Task A_transport_failure_is_one_fixed_sentence_and_logs_the_exception_type_only()
    {
        Fixture f = new();
        f.ResumeThrows = new TaskCanceledException("http timeout against https://queue.fal.run/secret-path");
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        result.Output.Should().NotContain("queue.fal.run").And.NotContain("secret-path");
        f.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task A_result_that_misses_the_length_check_is_not_metered_and_the_upload_is_still_deleted()
    {
        Fixture f = new();
        f.Remux.Throw = new EnhanceException(EnhanceErrorCodes.DurationMismatch, "output", "12.9s versus 12.5s");
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("ENHANCE_DURATION_MISMATCH");
        result.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        f.Recorded.Should().BeEmpty("a result that was not delivered is never metered");
        f.Deleted.Should().Equal(f.Uploaded);
        f.Session.Published.Should().BeEmpty();
        f.Registered.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stop_during_the_poll_propagates_leaves_the_row_charged_for_the_resume_and_deletes_the_upload()
    {
        using CancellationTokenSource cts = new();
        Fixture f = new() { CancelOnResume = cts };
        Func<Task> run = async () => await f.RunAsync(Hosted(), ct: cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        f.Deleted.Should().Equal(f.Uploaded);
        f.LedgerRows().Single().Status.Should().Be(ExternalGenerationJobStatus.Submitted);
        f.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task The_hosted_ledger_row_never_counts_toward_the_video_daily_caps()
    {
        Fixture f = new();
        (await f.RunAsync(Hosted())).Status.Should().Be(StepStatus.Completed);

        ExternalGenerationJob row = f.LedgerRows().Single();
        VideoBudgetAccounting.IsEnhanceClipId(row.ClipId).Should().BeTrue();
        VideoBudgetAccounting.TodayJobs(f.Ledger.CreateDbContext().ExternalGenerationJobs, ExternalGenerationJob.CreditsUnit, DateTime.UtcNow.Date)
            .ToList().Should().BeEmpty();
    }

    // ---------------- scratch ----------------

    [Fact]
    public async Task The_scratch_directory_is_gone_on_success_and_on_failure()
    {
        using ScratchFixture ok = new();
        (await ok.Fixture.RunAsync(Local())).Status.Should().Be(StepStatus.Completed);
        ok.AssertRemoved();

        using ScratchFixture failed = new();
        failed.Fixture.Enhancer.Throw = new EnhanceException(EnhanceErrorCodes.FfmpegFailed, "ffmpeg", "boom");
        (await failed.Fixture.RunAsync(Local())).Status.Should().Be(StepStatus.Failed);
        failed.AssertRemoved();
    }

    // ---------------- fix round: resolution on every op, stream duration, engine-side consent ----------------

    [Theory]
    [InlineData("Denoise", ",\"denoiseStrength\":\"Light\"", "Local")]
    [InlineData("MotionBlur", "", "Local")]
    [InlineData("Interpolate", ",\"frameMultiplier\":2", "Local")]
    [InlineData("Interpolate", ",\"frameMultiplier\":2", "Hosted")]
    public async Task Every_operation_is_refused_when_its_output_is_taller_than_the_plan_allows(string op, string extra, string engine)
    {
        Fixture f = new() { MaxOutputHeight = 720 };
        if (op == "Interpolate" && engine == "Hosted")
            f.UseInterpolationRow();

        string config = engine == "Hosted" ? Hosted(op, extra: extra) : Local(op, extra);
        StepExecutionResult result = await f.RunAsync(config);

        result.FailureCode.Should().Be("PLAN_NOT_ENTITLED");
        result.FailureClass.Should().Be(FailureClass.EntitlementDenied);
        result.Output.Should().Contain("720");
        f.NothingLeftTheHost();
        f.Enhancer.Requests.Should().BeEmpty();
        f.LedgerRows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_container_that_under_reports_its_length_is_held_to_the_video_streams_length()
    {
        // The container says 10 s, the stream says 130 s: the 120 s hosted cap applies to the stream.
        Fixture hosted = new() { SourceSeconds = 10, StreamDurationSec = 130 };
        StepExecutionResult result = await hosted.RunAsync(Hosted());
        result.FailureCode.Should().Be("ENHANCE_SOURCE_TOO_LONG");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        hosted.NothingLeftTheHost();
        hosted.LedgerRows().Should().BeEmpty();

        // A step's own LOWER cap cannot be bypassed that way either (local, 30 s step cap, 50 s stream).
        Fixture local = new() { SourceSeconds = 10, StreamDurationSec = 50 };
        (await local.RunAsync(Local(extra: ",\"denoiseStrength\":\"Light\",\"maxSourceSeconds\":30"))).FailureCode.Should().Be("ENHANCE_SOURCE_TOO_LONG");
        local.Enhancer.Requests.Should().BeEmpty();

        // With the route's hints (13 s priced), a stream longer than that is a changed source, refunded, nothing uploaded.
        Fixture hinted = new() { SourceSeconds = 10, StreamDurationSec = 60 };
        StepExecutionResult changed = await hinted.RunAsync(Hosted(extra: ",\"upscaleFactor\":2,\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":30,\"maxSourceSeconds\":13"));
        changed.FailureCode.Should().Be("ENHANCE_SOURCE_CHANGED");
        hinted.NothingLeftTheHost();
    }

    [Fact]
    public async Task A_source_whose_stream_figures_cannot_be_read_is_refused_not_trusted()
    {
        Fixture f = new() { UnreadableProbe = true };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("ENHANCE_PROBE_FAILED");
        f.NothingLeftTheHost();
    }

    [Fact]
    public async Task The_upscale_settle_never_exceeds_the_remuxed_output_or_the_length_the_hold_was_priced_for()
    {
        Fixture f = new() { SourceSeconds = 12.2, VendorReportedSeconds = 9999 };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        f.Recorded.Should().ContainSingle().Which.Quantity.Should().Be(13m, "the vendor's inflated figure is capped at the delivered 12.2 s");
        JsonNode.Parse(result.Output)!["billedSeconds"]!.GetValue<long>().Should().Be(13);
    }

    [Fact]
    public async Task A_hosted_run_needs_the_project_consent_as_well_as_the_step_flag_and_reads_it_fresh()
    {
        // Step flag true, project consent absent (withdrawn after the run was queued, or never given for a Standard workflow).
        Fixture noProject = new() { ProjectConsented = false };
        StepExecutionResult refused = await noProject.RunAsync(Hosted());
        refused.FailureCode.Should().Be("EGRESS_REFUSED");
        refused.FailureClass.Should().Be(FailureClass.UserInput);
        refused.Output.Should().Contain("Nothing was uploaded").And.Contain("project");
        noProject.NothingLeftTheHost();
        noProject.Resolver.Verify(r => r.ResolveVideoUpscaleAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Project consent present, step flag false: still refused.
        Fixture noFlag = new();
        (await noFlag.RunAsync(Hosted(consent: false))).FailureCode.Should().Be("EGRESS_REFUSED");
        noFlag.NothingLeftTheHost();

        // Both: runs.
        (await new Fixture().RunAsync(Hosted())).Status.Should().Be(StepStatus.Completed);

        // A lookup that cannot be made refuses (fail closed), and a local run never asks.
        Fixture blind = new() { NoProjectDb = true };
        (await blind.RunAsync(Hosted())).FailureCode.Should().Be("EGRESS_REFUSED");
        blind.NothingLeftTheHost();
        (await new Fixture { ProjectConsented = false }.RunAsync(Local())).Status.Should().Be(StepStatus.Completed);
    }

    // ---------------- T-028: the upload is a bounded stream copy, never the original ----------------

    [Fact]
    public async Task What_is_uploaded_is_the_bounded_stream_copy_cut_at_the_checked_length_never_the_original()
    {
        Fixture f = new() { SourceSeconds = 12.2 };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        f.UploadedLocalPaths.Should().ContainSingle().Which.Should().Be("video-enhance-upload.mp4").And.NotBe("video-enhance-source");

        string[] argv = f.Session.FfmpegCalls.Should().Contain(a => a.Contains("-c") && a.Contains("copy")).Which;
        string[] beforeInput = argv[..Array.IndexOf(argv, "-i")];
        beforeInput.Should().Contain("-protocol_whitelist").And.Contain("-format_whitelist");
        argv[Array.IndexOf(argv, "-t") + 1].Should().Be("12.200000");
        argv[Array.IndexOf(argv, "-frames:v") + 1].Should().Be("367", "12.2 s at 30 fps is 366 frames, plus one");
        argv[^1].Should().Be("video-enhance-upload.mp4");
        argv.Should().Contain("0:v:0").And.Contain("0:a?");
    }

    [Fact]
    public async Task The_copy_is_refused_before_upload_when_it_is_longer_than_the_checked_length()
    {
        Fixture f = new() { SourceSeconds = 10, CopyDurationSec = 14 };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("ENHANCE_SOURCE_CHANGED");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty();

        // One frame over is tolerated; more is not.
        Fixture tolerated = new() { SourceSeconds = 10, CopyDurationSec = 10.03 };
        (await tolerated.RunAsync(Hosted())).Status.Should().Be(StepStatus.Completed);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public async Task A_copy_that_measures_as_zero_negative_or_not_a_number_is_refused_before_upload(double measured)
    {
        Fixture f = new() { SourceSeconds = 10, CopyDurationSec = measured };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("ENHANCE_PROBE_FAILED");
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty();
    }

    [Fact]
    public async Task Codecs_that_cannot_go_in_mp4_keep_matroska_for_the_copy()
    {
        Fixture f = new() { VideoCodec = "vp8" };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        f.UploadedLocalPaths.Should().ContainSingle().Which.Should().Be("video-enhance-upload.mkv");
        f.Uploaded.Single().Should().EndWith("source-2.mkv");
        string[] argv = f.Session.FfmpegCalls.First(a => a.Contains("copy"));
        argv[^1].Should().Be("video-enhance-upload.mkv");
        argv.Should().Contain("matroska");
    }

    // ---------------- fix round for Extend: resolution, stream-bound cap and floor, settle, project consent ----------------

    [Fact]
    public async Task An_extend_whose_canvas_is_taller_than_the_plan_allows_is_refused_before_anything_is_uploaded()
    {
        // 1920x1080 to 1:1 is a 1920x1920 canvas: its short side is above a 1080 plan.
        Fixture f = new() { SourceSeconds = 8, MaxOutputHeight = 1080 };
        f.UseOutpaintRow();

        StepExecutionResult result = await f.RunAsync(Extend());

        result.FailureCode.Should().Be("PLAN_NOT_ENTITLED");
        result.FailureClass.Should().Be(FailureClass.EntitlementDenied);
        result.Output.Should().Contain("1080");
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty();

        // The same canvas on a plan that allows it runs.
        Fixture ok = new() { SourceSeconds = 8, MaxOutputHeight = 2160 };
        ok.UseOutpaintRow();
        (await ok.RunAsync(Extend())).Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task An_extend_container_that_under_reports_its_length_is_held_to_the_streams_frames_for_the_cap_and_the_hints()
    {
        // Container 5 s (150 frames), stream 9 s (270 frames): over the 241-frame ceiling, so SOURCE_TOO_LONG with the cap, nothing uploaded.
        Fixture f = new() { SourceSeconds = 5, StreamDurationSec = 9, FramesPerSecondNumerator = 30 };
        f.UseOutpaintRow();
        StepExecutionResult result = await f.RunAsync(Extend());
        result.FailureCode.Should().Be("ENHANCE_SOURCE_TOO_LONG");
        result.ErrorDetails.Should().Contain("8-second limit");
        f.NothingLeftTheHost();
        f.LedgerRows().Should().BeEmpty();

        // With the route's hints (8 s priced), a longer stream is a changed source, refunded, nothing uploaded.
        Fixture hinted = new() { SourceSeconds = 5, StreamDurationSec = 20 };
        hinted.UseOutpaintRow();
        StepExecutionResult changed = await hinted.RunAsync(Extend(
            ",\"targetAspect\":\"1:1\",\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":30,\"maxSourceSeconds\":8"));
        changed.FailureCode.Should().Be("ENHANCE_SOURCE_CHANGED");
        hinted.NothingLeftTheHost();
    }

    [Fact]
    public async Task The_extend_settle_never_exceeds_the_hold_and_ignores_an_inflated_vendor_figure()
    {
        Fixture f = new() { SourceSeconds = 8, FramesPerSecondNumerator = 30, VendorReportedSeconds = 9999 };
        f.UseOutpaintRow();

        StepExecutionResult result = await f.RunAsync(Extend());

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        f.Recorded.Should().ContainSingle().Which.Quantity.Should().Be(15m, "240 frames at 16 fps, whatever the vendor reports");
        long held = (long)(await VideoEnhanceStepPricing.TryStepHoldMillisAsync(
            Extend(), f.Resolver.Object, f.PriceBook(), CancellationToken.None))!.Value;
        (f.Recorded[0].Quantity * 1000).Should().BeLessThanOrEqualTo(held, "settle is never above the hold");
    }

    [Fact]
    public async Task A_hosted_extend_needs_the_project_consent_as_well_as_the_step_flag()
    {
        Fixture noProject = new() { SourceSeconds = 8, ProjectConsented = false };
        noProject.UseOutpaintRow();
        StepExecutionResult refused = await noProject.RunAsync(Extend());
        refused.FailureCode.Should().Be("EGRESS_REFUSED");
        refused.Output.Should().Contain("Nothing was uploaded");
        noProject.NothingLeftTheHost();
        noProject.Resolver.Verify(r => r.ResolveVideoOutpaintAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        noProject.ModeratedTexts.Should().BeEmpty();
    }

    // ---------------- helpers ----------------

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private sealed class Fixture
    {
        public Guid ExecutionId { get; } = Guid.NewGuid();
        public Guid StepResultId { get; } = Guid.NewGuid();
        public SessionWorkspace Session { get; }
        public FakeProbe Probe { get; } = new();
        public FakeEnhancer Enhancer { get; }
        public FakeRemux Remux { get; }
        public SharedInMemoryDbFactory Ledger { get; } = new();
        public Mock<IProjectFileWorkspace> Files { get; } = new();
        public Mock<IInferenceProviderResolver> Resolver { get; } = new();
        public Mock<IPresignedUrlService> Presigner { get; } = new();
        public List<string> Uploaded { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<(string Key, TimeSpan Ttl)> Presigned { get; } = [];
        public List<UsageRecordRequest> Recorded { get; } = [];
        public List<(string Key, string Name)> Registered { get; } = [];
        public List<VideoUpscaleRequest> UpscaleRequests { get; } = [];
        public List<FrameInterpolationRequest> InterpolationRequests { get; } = [];
        public List<VideoOutpaintRequest> OutpaintRequests { get; } = [];
        public ModerationAction? ModerationVerdict { get; set; }
        public List<string> ModeratedTexts { get; } = [];
        public List<VideoEnhanceJobHandle> ResumedHandles { get; } = [];
        public VideoEnhanceJobHandle Handle { get; } = new(
            "req-0001", "https://queue.fal.run/fal-ai/topaz/upscale/video/requests/req-0001/status",
            "https://queue.fal.run/fal-ai/topaz/upscale/video/requests/req-0001");
        public bool VideoEnhanceAllowed { get; set; } = true;
        public int MaxOutputHeight { get; set; } = 2160;
        public ResolvedAudioGenerationProvider? Row { get; set; }
        public bool OrgOwned { get; set; }
        public bool Priced { get; set; } = true;
        public bool HasPresigner { get; set; } = true;
        public bool ProjectConsented { get; set; } = true;
        public bool NoProjectDb { get; set; }
        public double? StreamDurationSec { get => Probe.StreamDurationSec; set => Probe.StreamDurationSec = value; }
        public bool UnreadableProbe { get => Probe.UnreadableProbe; set => Probe.UnreadableProbe = value; }
        public double? VendorReportedSeconds { get; set; }
        public double? CopyDurationSec { get => Probe.CopyDurationSec; set => Probe.CopyDurationSec = value; }
        public string VideoCodec { get => Probe.VideoCodec; set => Probe.VideoCodec = value; }
        public List<string> UploadedLocalPaths { get; } = [];
        public double SourceSeconds { get => Probe.DurationSec; set => Probe.DurationSec = value; }
        public int FramesPerSecondNumerator { get => Probe.FpsNum; set => Probe.FpsNum = value; }
        public bool ResumeFailsOnce { get; set; }
        public Exception? SubmitThrows { get; set; }
        public Exception? ResumeThrows { get; set; }
        public CancellationTokenSource? CancelOnResume { get; set; }
        public IConfiguration? Configuration { get; set; }
        public IMediaWorkspaceFactory? Factory { get; set; }
        private int _resumeCalls;

        public Fixture()
        {
            Session = new SessionWorkspace(Probe);
            Enhancer = new FakeEnhancer(Session, Probe);
            Remux = new FakeRemux(Session, Probe);
            Row = new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.VideoUpscale,
                "", VideoEnhanceModelCatalog.TopazId, "test", 60, AccountId: Guid.NewGuid());
        }

        public void UseInterpolationRow() =>
            Row = new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi,
                InferenceProviderCapability.FrameInterpolation, "", VideoEnhanceModelCatalog.RifeId, "test", 60, AccountId: Guid.NewGuid());

        public IModelPriceService PriceBook()
        {
            Mock<IModelPriceService> prices = new();
            prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
            return prices.Object;
        }

        public void UseOutpaintRow(string model = VideoEnhanceModelCatalog.WanVaceId) =>
            Row = new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi,
                InferenceProviderCapability.VideoOutpaint, "", model, "test", 60, AccountId: Guid.NewGuid());

        public List<ExternalGenerationJob> LedgerRows()
        {
            using WorkflowEngineDbContext db = Ledger.CreateDbContext();
            return db.ExternalGenerationJobs.AsNoTracking().OrderBy(j => j.CreatedAt).ToList();
        }

        public void NothingLeftTheHost()
        {
            Uploaded.Should().BeEmpty();
            Presigned.Should().BeEmpty();
            UpscaleRequests.Should().BeEmpty();
            InterpolationRequests.Should().BeEmpty();
            OutpaintRequests.Should().BeEmpty();
            Recorded.Should().BeEmpty();
        }

        public async Task<StepExecutionResult> RunAsync(string configJson, CancellationToken ct = default, bool sameStepResult = false)
        {
            Files.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Files.Setup(w => w.RegisterStoredObjectAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                    It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string key, string name, string type, long size, SummaryStatus s, FileIndexingStatus i, CancellationToken c, string cat) =>
                {
                    Registered.Add((key, name));
                    return new ProjectWorkspaceFile(Guid.NewGuid(), p, name, null, cat, key, type, size, DateTime.UtcNow, null);
                });
            Files.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string local, string name, string type, CancellationToken c, string cat) =>
                {
                    string key = $"projects/{p}/{cat}/{name}";
                    Uploaded.Add(key);
                    UploadedLocalPaths.Add(local);
                    return key;
                });
            Files.Setup(w => w.DeleteArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, string, CancellationToken>((_, key, _) => Deleted.Add(key))
                .Returns(Task.CompletedTask);

            Resolver.Setup(r => r.ResolveVideoUpscaleAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Row is null ? null : OrgOwned ? Row with { OrganizationId = OrgId } : Row);
            Resolver.Setup(r => r.ResolveFrameInterpolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Row is null ? null : OrgOwned ? Row with { OrganizationId = OrgId } : Row);
            Resolver.Setup(r => r.ResolveVideoOutpaintAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Row is null ? null : OrgOwned ? Row with { OrganizationId = OrgId } : Row);
            Mock<IModelPriceService> prices = new();
            if (Priced)
            {
                prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
            }
            else
            {
                prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new UnpricedUsageException(UsageKind.VideoEnhanceSeconds, InferenceProviderKind.FalAi, VideoEnhanceModelCatalog.TopazId));
            }

            Presigner.Setup(p => p.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .Callback<Guid, string, TimeSpan>((_, key, ttl) => Presigned.Add((key, ttl)))
                .ReturnsAsync("https://signed.example/get");
            Mock<IUsageRecorder> usage = new();
            usage.Setup(u => u.RecordAsync(It.IsAny<UsageRecordRequest>(), It.IsAny<CancellationToken>()))
                .Callback<UsageRecordRequest, CancellationToken>((r, _) => Recorded.Add(r))
                .Returns(ValueTask.FromResult(0L));

            VideoEnhanceResult Result(double? vendorSeconds) => new([1, 2, 3, 4], "video/mp4", ".mp4", vendorSeconds, "m", null, "req-0001");
            Task<VideoEnhanceResult> Resume(VideoEnhanceJobHandle h, CancellationToken token)
            {
                ResumedHandles.Add(h);
                _resumeCalls++;
                CancelOnResume?.Cancel();
                token.ThrowIfCancellationRequested();
                if (ResumeThrows is not null)
                    throw ResumeThrows;
                if (ResumeFailsOnce && _resumeCalls == 1)
                    throw new IOException("the connection dropped between the submit and the result download");
                return Task.FromResult(Result(VendorReportedSeconds));
            }

            Mock<IVideoUpscaleClient> upscale = new();
            upscale.Setup(c => c.SubmitAsync(It.IsAny<VideoUpscaleRequest>(), It.IsAny<CancellationToken>()))
                .Returns((VideoUpscaleRequest r, CancellationToken _) =>
                {
                    UpscaleRequests.Add(r);
                    return SubmitThrows is null ? Task.FromResult(Handle) : Task.FromException<VideoEnhanceJobHandle>(SubmitThrows);
                });
            upscale.Setup(c => c.ResumeAsync(It.IsAny<VideoEnhanceJobHandle>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((VideoEnhanceJobHandle h, string _, CancellationToken token) => Resume(h, token));
            Mock<IVideoUpscaleClientFactory> upscaleFactory = new();
            upscaleFactory.Setup(c => c.Get(It.IsAny<ResolvedAudioGenerationProvider>())).Returns(upscale.Object);

            Mock<IFrameInterpolationClient> interpolation = new();
            interpolation.Setup(c => c.SubmitAsync(It.IsAny<FrameInterpolationRequest>(), It.IsAny<CancellationToken>()))
                .Returns((FrameInterpolationRequest r, CancellationToken _) =>
                {
                    InterpolationRequests.Add(r);
                    return SubmitThrows is null ? Task.FromResult(Handle) : Task.FromException<VideoEnhanceJobHandle>(SubmitThrows);
                });
            interpolation.Setup(c => c.ResumeAsync(It.IsAny<VideoEnhanceJobHandle>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((VideoEnhanceJobHandle h, string _, CancellationToken token) => Resume(h, token));
            Mock<IFrameInterpolationClientFactory> interpolationFactory = new();
            interpolationFactory.Setup(c => c.Get(It.IsAny<ResolvedAudioGenerationProvider>())).Returns(interpolation.Object);

            Mock<IVideoOutpaintClient> outpaint = new();
            outpaint.Setup(c => c.SubmitAsync(It.IsAny<VideoOutpaintRequest>(), It.IsAny<CancellationToken>()))
                .Returns((VideoOutpaintRequest r, CancellationToken _) =>
                {
                    OutpaintRequests.Add(r);
                    return SubmitThrows is null ? Task.FromResult(Handle) : Task.FromException<VideoEnhanceJobHandle>(SubmitThrows);
                });
            outpaint.Setup(c => c.ResumeAsync(It.IsAny<VideoEnhanceJobHandle>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((VideoEnhanceJobHandle h, string _, CancellationToken token) => Resume(h, token));
            Mock<IVideoOutpaintClientFactory> outpaintFactory = new();
            outpaintFactory.Setup(c => c.Get(It.IsAny<ResolvedAudioGenerationProvider>())).Returns(outpaint.Object);
            Mock<IContentModerationGate> gate = new();
            gate.Setup(g => g.ModerateAsync(It.IsAny<ModerationInput>(), It.IsAny<ModerationAttribution>(), It.IsAny<CancellationToken>()))
                .Returns((ModerationInput input, ModerationAttribution _, CancellationToken _) =>
                {
                    ModeratedTexts.Add(input.Text ?? string.Empty);
                    return Task.FromResult(new ModerationDecision(
                        ModerationVerdict ?? ModerationAction.Allow, true, false, null, "That prompt cannot be used.", "h"));
                });

            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(new OrganizationEntitlementSet
                {
                    Tier = "test",
                    VideoEnhanceAllowed = VideoEnhanceAllowed,
                    MaxOutputHeight = MaxOutputHeight,
                    StorageGb = OrganizationEntitlementSet.UnlimitedInt
                }));
            DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            using (WorkflowEngineDbContext seed = new(engineOptions))
            {
                seed.Projects.Add(new Project
                {
                    Id = ProjectId,
                    Name = "p",
                    OwnerId = Guid.NewGuid(),
                    OrganizationId = OrgId,
                    VideoEnhanceConsentAt = ProjectConsented ? DateTime.UtcNow : null
                });
                seed.SaveChanges();
            }

            ServiceCollection services = new();
            services.AddSingleton(entitlements.Object).AddSingleton(Files.Object);
            if (!NoProjectDb)
                services.AddScoped(_ => new WorkflowEngineDbContext(engineOptions));
            ServiceProvider provider = services.BuildServiceProvider();

            var executor = new VideoEnhanceStepExecutor(
                new Mock<IVideoToolRunner>(MockBehavior.Strict).Object,
                Files.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new VideoEditingOptions()),
                Options.Create(new LocalEnhanceOptions()),
                Enhancer,
                Remux,
                Resolver.Object,
                upscaleFactory.Object,
                interpolationFactory.Object,
                new VideoGenerationBudgetGate(Ledger, Options.Create(new VideoGenerationOptions())),
                Ledger,
                NullLogger<VideoEnhanceStepExecutor>.Instance,
                prices.Object,
                usage.Object,
                HasPresigner ? Presigner.Object : null,
                configuration: Configuration,
                mediaWorkspaces: Factory ?? new StubFactory(Session),
                outpaintClients: outpaintFactory.Object,
                moderation: gate.Object);

            var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoEnhance, VideoEnhanceConfigJson = configJson };
            var execution = new WorkflowExecution { Id = ExecutionId, ProjectId = ProjectId, OrganizationId = OrgId };
            List<StepOutputHistoryEntry> history = [new(1, "Source", "{}", OutputStorageKey: SourceKey)];
            return await executor.ExecuteAsync(new StepExecutionContext
            {
                Execution = execution,
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                StepOutputHistory = history,
                CurrentStepIndex = 0,
                IterationCount = 0,
                CorrelationId = "test",
                StepResultId = sameStepResult ? StepResultId : Guid.NewGuid(),
                CancellationToken = ct
            });
        }
    }

    private sealed class FakeProbe
    {
        public double DurationSec { get; set; } = 12.5;
        public int FpsNum { get; set; } = 30;
        public bool Video { get; set; } = true;

        /// <summary>The video stream's own length when it differs from the container's (a container that under-reports).</summary>
        public double? StreamDurationSec { get; set; }

        public bool UnreadableProbe { get; set; }

        /// <summary>What the whitelisted probe measures on the bounded COPY (null: the checked length, as a faithful copy would be).</summary>
        public double? CopyDurationSec { get; set; }

        public string VideoCodec { get; set; } = "h264";
    }

    private sealed class FakeEnhancer(SessionWorkspace session, FakeProbe probe) : ILocalVideoEnhancer
    {
        public List<LocalEnhanceRequest> Requests { get; } = [];
        public Exception? Throw { get; set; }

        public Task<LocalEnhanceResult> EnhanceAsync(
            string sourcePath, string outputPath, LocalEnhanceRequest request, IProgress<double>? progress, CancellationToken ct)
        {
            Requests.Add(request);
            if (Throw is not null)
                throw Throw;
            session.Files[outputPath] = new byte[2048];
            string method = request.Op == VideoEnhanceOp.Denoise ? "denoise-light" : request.Op.ToString().ToLowerInvariant();
            return Task.FromResult(new LocalEnhanceResult(true, method, 1920, 1080, probe.FpsNum, probe.DurationSec, 2048, []));
        }
    }

    private sealed class FakeRemux(SessionWorkspace session, FakeProbe probe) : IEnhancedAudioRemux
    {
        public List<RemuxTarget?> Targets { get; } = [];
        public List<string> SourcePaths { get; } = [];
        public Exception? Throw { get; set; }

        public Task<LocalEnhanceResult> RemuxAsync(string sourcePath, string picturePath, string outputPath, RemuxTarget? target, CancellationToken ct)
        {
            Targets.Add(target);
            SourcePaths.Add(sourcePath);
            if (Throw is not null)
                throw Throw;
            session.Files[outputPath] = new byte[4096];
            return Task.FromResult(new LocalEnhanceResult(true, "remux+copy", target!.Width, target.Height, target.Fps, probe.DurationSec, 4096, []));
        }
    }

    private sealed class SessionWorkspace(FakeProbe probe) : IMediaWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Published { get; } = [];
        public List<string[]> ProbeCalls { get; } = [];
        public List<string[]> FfmpegCalls { get; } = [];

        public string RootPath => "session:/";

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct) => Task.CompletedTask;

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            RunFfmpegAsync(argv, timeout, ct, null!);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        {
            FfmpegCalls.Add([.. argv]);
            return Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));
        }

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
        {
            ProbeCalls.Add(argv.ToArray());
            double duration = argv[^1].Contains("video-enhance-upload", StringComparison.Ordinal)
                ? probe.CopyDurationSec ?? Math.Max(probe.DurationSec, probe.StreamDurationSec ?? 0)
                : probe.DurationSec;
            if (probe.UnreadableProbe)
                return Task.FromResult(new VideoToolResult(0, "not json", string.Empty, false));

            string streamLength = probe.StreamDurationSec is { } sd
                ? $",\"duration\":\"{sd.ToString(System.Globalization.CultureInfo.InvariantCulture)}\""
                : string.Empty;
            string video = probe.Video
                ? $"{{\"codec_type\":\"video\",\"codec_name\":\"{probe.VideoCodec}\",\"width\":1920,\"height\":1080,\"r_frame_rate\":\"{probe.FpsNum}/1\"{streamLength}}},"
                : string.Empty;
            string json = $"{{\"streams\":[{video}{{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"sample_rate\":\"48000\",\"channels\":2}}],"
                + $"\"format\":{{\"duration\":\"{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"}}}}";
            return Task.FromResult(new VideoToolResult(0, json, string.Empty, false));
        }

        public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct) => Task.FromResult(virtualPath);

        public Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct)
        {
            Files[virtualPath] = content;
            return Task.CompletedTask;
        }

        public Task WriteTextAsync(string virtualPath, string content, System.Text.Encoding encoding, CancellationToken ct) =>
            WriteBytesAsync(virtualPath, encoding.GetBytes(content), ct);

        public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) => ReadAsync(virtualPath, int.MaxValue, ct);

        public Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct) =>
            Files.TryGetValue(virtualPath, out byte[]? bytes) ? Task.FromResult(bytes) : throw new FileNotFoundException(virtualPath);

        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(virtualPath, out byte[]? bytes) ? new MediaWorkspaceStat(true, bytes.Length) : new MediaWorkspaceStat(false, 0));

        public Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            Published.Add(finalStorageKey);
            return Task.FromResult(finalStorageKey);
        }

        public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            Published.Add(finalStorageKey);
            return Task.FromResult(finalStorageKey);
        }
    }

    private sealed class StubFactory(SessionWorkspace session) : IMediaWorkspaceFactory
    {
        public Task<MediaWorkspaceOpenResult> OpenAsync(MediaWorkspaceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                session, scratch: null, ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null, request.ExecutionId, request.StepOrder, NullLogger.Instance)));
    }

    /// <summary>A recording factory with a REAL scratch directory holding a sentinel media file, so a test asserts the directory is gone after the executor's finally.</summary>
    private sealed class ScratchFactory(SessionWorkspace session, string root) : IMediaWorkspaceFactory
    {
        public VideoScratchSpace? Scratch { get; private set; }

        public Task<MediaWorkspaceOpenResult> OpenAsync(MediaWorkspaceRequest request, CancellationToken cancellationToken = default)
        {
            Scratch = VideoScratchSpace.Create(
                new VideoEditingOptions { ScratchPath = root }, request.ExecutionId, request.StepId, NullLogger.Instance);
            File.WriteAllText(Scratch.GetPath("video-enhance-source"), "customer media");
            return Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                session, Scratch, ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null, request.ExecutionId, request.StepOrder, NullLogger.Instance)));
        }
    }

    private sealed class ScratchFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "reelbolt-enhance-scratch-" + Guid.NewGuid().ToString("N"));

        public ScratchFixture()
        {
            Directory.CreateDirectory(_root);
            Fixture = new Fixture();
            Factory = new ScratchFactory(Fixture.Session, _root);
            Fixture.Factory = Factory;
        }

        public Fixture Fixture { get; }

        private ScratchFactory Factory { get; }

        public void AssertRemoved()
        {
            Factory.Scratch.Should().NotBeNull("the factory opened a real local scratch directory");
            Directory.Exists(Factory.Scratch!.DirectoryPath).Should().BeFalse("the executor's finally deletes the whole scratch directory");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
