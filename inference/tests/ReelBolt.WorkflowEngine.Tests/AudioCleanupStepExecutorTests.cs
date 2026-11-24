using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Cleanup;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>StepType.AudioCleanup</c> end to end over a faked media session, a fake local cleaner and a fake isolation client:
/// no live vendor call, no real ffmpeg. Covers the refusal order, that a refusal uploads nothing, the settle/refund classes,
/// the 50 ms length check and the shape of the output.
/// </summary>
public sealed class AudioCleanupStepExecutorTests
{
    private static readonly Guid ProjectId = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid OrgId = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly string OutputPrefix = $"projects/{ProjectId}/outputFiles/";
    private const string Model = "fal-ai/elevenlabs/audio-isolation";
    private const string SourceKey = "projects/22222222-0000-0000-0000-000000000001/userFiles/take.wav";
    private const string VideoKey = "projects/22222222-0000-0000-0000-000000000001/userFiles/take.mp4";

    private static string Hosted(string target = "IsolateVoice", bool consent = true, string extra = "") =>
        $$"""{"target":"{{target}}","source":{"kind":"StepOutput","stepOrder":1},"allowSourceMediaEgress":{{(consent ? "true" : "false")}}{{extra}}}""";

    // ---------------- local targets ----------------

    [Fact]
    public async Task A_local_target_runs_the_local_cleaner_with_no_network_and_publishes_a_wav()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"target":"Wind","source":{"kind":"StepOutput","stepOrder":1}}""");

        result.Status.Should().Be(StepStatus.Completed);
        result.OutputStorageKey.Should().StartWith(OutputPrefix).And.EndWith("/step-2-clean.wav");
        f.Cleaner.Calls.Should().ContainSingle().Which.Should().Be(LocalCleanupTarget.Wind);
        f.Resolver.Verify(r => r.ResolveAudioIsolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        f.Files.Verify(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
        f.Presigner.Verify(p => p.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        f.Usage.Verify(u => u.RecordAsync(It.IsAny<UsageRecordRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
        output["target"]!.GetValue<string>().Should().Be("Wind");
        output["method"]!.GetValue<string>().Should().Be("local:wind");
        output["applied"]!.GetValue<bool>().Should().BeTrue();
        output["durationSec"]!.GetValue<double>().Should().Be(12.5);
        output["billedSeconds"].Should().BeNull();
        output["outputFileId"].Should().NotBeNull();
        output["warnings"]!.AsArray().Should().BeEmpty();
        result.Output.Should().NotContain("projects/").And.NotContain("storageKey");
    }

    [Fact]
    public async Task The_result_is_registered_under_a_readable_name_unless_registration_is_off()
    {
        Fixture f = new() { SourceKey = VideoKey };
        f.Probe.Video = true;
        StepExecutionResult result = await f.RunAsync("""{"target":"Hiss","source":{"kind":"StepOutput","stepOrder":1}}""");

        result.OutputStorageKey.Should().EndWith("/step-2-clean.mp4");
        f.Files.Verify(w => w.RegisterStoredObjectAsync(ProjectId, result.OutputStorageKey!, "take (hiss reduced).mp4", "video/mp4",
            It.IsAny<long>(), It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), "outputFiles"), Times.Once);

        Fixture off = new();
        (await off.RunAsync("""{"target":"Hiss","source":{"kind":"StepOutput","stepOrder":1},"registerProjectFile":false}""")).Status.Should().Be(StepStatus.Completed);
        off.Files.Verify(w => w.RegisterStoredObjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Hum_with_no_mains_frequency_is_a_successful_pass_through_that_is_not_registered_or_charged()
    {
        Fixture f = new();
        f.Cleaner.Applied = false;
        f.Cleaner.Reason = CleanupFilterBuilder.HumNotDetected;

        StepExecutionResult result = await f.RunAsync("""{"target":"Hum","source":{"kind":"StepOutput","stepOrder":1}}""");

        result.Status.Should().Be(StepStatus.Completed);
        result.OutputStorageKey.Should().Be(SourceKey, "the untouched source is carried on");
        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
        output["applied"]!.GetValue<bool>().Should().BeFalse();
        output["meta"]!["reason"]!.GetValue<string>().Should().Be("hum_not_detected");
        output["outputFileId"].Should().BeNull();
        f.Session.Published.Should().BeEmpty();
        f.Files.Verify(w => w.RegisterStoredObjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_cleanup_exception_is_a_coded_failure_with_the_class_of_its_code()
    {
        Fixture f = new();
        f.Cleaner.Throw = new CleanupException(CleanupErrorCodes.DurationMismatch, "drifted");

        StepExecutionResult result = await f.RunAsync("""{"target":"Echo","source":{"kind":"StepOutput","stepOrder":1}}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("AUDIO_DURATION_MISMATCH");
        result.FailureClass.Should().Be(FailureClass.PlatformProviderError);
    }

    [Fact]
    public async Task A_video_source_gets_its_picture_copied_with_the_cleaned_audio_and_an_audio_source_a_wav()
    {
        Fixture video = new() { SourceKey = VideoKey };
        video.Probe.Video = true;
        StepExecutionResult mp4 = await video.RunAsync("""{"target":"Wind","source":{"kind":"StepOutput","stepOrder":1}}""");
        mp4.OutputStorageKey.Should().EndWith("/step-2-clean.mp4");
        string[] remux = video.Session.FfmpegCalls.Should().ContainSingle().Which;
        remux.Should().Contain("-c:v").And.Contain("copy").And.Contain("aac").And.NotContain("-map_metadata");

        Fixture audio = new();
        (await audio.RunAsync("""{"target":"Wind","source":{"kind":"StepOutput","stepOrder":1}}""")).OutputStorageKey.Should().EndWith("/step-2-clean.wav");
        audio.Session.FfmpegCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Asking_for_video_out_of_an_audio_only_source_is_refused()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"target":"Wind","source":{"kind":"StepOutput","stepOrder":1},"outputKind":"VideoWithCleanAudio"}""");

        result.FailureCode.Should().Be("AUDIO_OUTPUT_KIND_UNSUPPORTED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
    }

    [Fact]
    public async Task A_source_with_no_audio_stream_is_a_user_input_failure()
    {
        Fixture f = new();
        f.Probe.AudioCodec = null;
        StepExecutionResult result = await f.RunAsync("""{"target":"Wind","source":{"kind":"StepOutput","stepOrder":1}}""");

        result.FailureCode.Should().Be("AUDIO_SOURCE_NO_AUDIO");
        result.FailureClass.Should().Be(FailureClass.UserInput);
    }

    [Fact]
    public async Task An_invalid_config_is_refused_at_run_time_naming_the_field()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"target":"Wind","source":{"kind":"GeneratedClips"}}""");

        result.FailureCode.Should().Be("CONFIG_INVALID");
        result.Output.Should().Contain("GeneratedClips");
        f.Cleaner.Calls.Should().BeEmpty();
    }

    // ---------------- hosted refusals, in order, uploading nothing ----------------

    [Fact]
    public async Task Without_consent_a_hosted_target_is_refused_before_a_provider_is_resolved_and_uploads_nothing()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync(Hosted(consent: false));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("EGRESS_REFUSED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.Output.Should().Contain("Nothing was uploaded");
        f.NothingLeftTheHost();
        f.Resolver.Verify(r => r.ResolveAudioIsolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_plan_feature_is_checked_before_the_consent()
    {
        Fixture f = new() { AudioCleanupAllowed = false };
        StepExecutionResult result = await f.RunAsync(Hosted(consent: false));

        result.FailureCode.Should().Be("PLAN_NOT_ENTITLED");
        result.FailureClass.Should().Be(FailureClass.EntitlementDenied);
        f.NothingLeftTheHost();
    }

    [Fact]
    public async Task A_local_target_needs_no_plan_feature()
    {
        Fixture f = new() { AudioCleanupAllowed = false };
        (await f.RunAsync("""{"target":"Hum","source":{"kind":"StepOutput","stepOrder":1}}""")).Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task No_provider_is_a_user_input_refusal_that_uploads_nothing()
    {
        Fixture f = new() { Row = null };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("NO_AUDIO_ISOLATION_PROVIDER");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        f.NothingLeftTheHost();
    }

    [Fact]
    public async Task A_source_over_the_cap_is_refused_and_the_step_can_only_lower_the_cap()
    {
        Fixture f = new();
        f.Probe.DurationSec = 31 * 60;
        StepExecutionResult over = await f.RunAsync(Hosted());
        over.FailureCode.Should().Be("AUDIO_SOURCE_TOO_LONG");
        over.FailureClass.Should().Be(FailureClass.UserInput);
        f.NothingLeftTheHost();

        Fixture lowered = new();
        lowered.Probe.DurationSec = 6 * 60;
        (await lowered.RunAsync(Hosted(extra: ",\"maxSourceMinutes\":5"))).FailureCode.Should().Be("AUDIO_SOURCE_TOO_LONG");

        Fixture raised = new();
        raised.Probe.DurationSec = 31 * 60;
        (await raised.RunAsync(Hosted(extra: ",\"maxSourceMinutes\":120"))).FailureCode.Should().Be("AUDIO_SOURCE_TOO_LONG", "a step can never raise the deployment cap");
    }

    [Fact]
    public async Task An_unpriced_model_fails_closed_with_config_invalid_and_uploads_nothing()
    {
        Fixture f = new() { Priced = false };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("CONFIG_INVALID");
        f.NothingLeftTheHost();
    }

    [Fact]
    public async Task A_deployment_without_a_presigner_cannot_serve_a_hosted_target()
    {
        Fixture f = new() { HasPresigner = false };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("AUDIO_EGRESS_UNAVAILABLE");
        f.Files.Verify(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
    }

    // ---------------- hosted success and failure ----------------

    [Fact]
    public async Task A_hosted_run_uploads_under_the_per_run_folder_presigns_ten_minutes_meters_and_deletes_the_upload()
    {
        Fixture f = new();
        f.Probe.DurationSec = 95;
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        string sourceKey = $"projects/{ProjectId}/agentFiles/audio-cleanup/{f.ExecutionId:D}/source-2.wav";
        // The extract lives in the media session (remote here: MaterializeAsync answers a relative name), so it is
        // uploaded through the session, never handed to the platform's local-file upload.
        f.Session.Published.Should().Contain(sourceKey);
        f.Files.Verify(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.Is<string>(p => !Path.IsPathRooted(p)), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
        f.Presigned.Should().ContainSingle().Which.Ttl.Should().Be(TimeSpan.FromMinutes(10));
        f.Deleted.Should().Equal(sourceKey);
        f.IsolationRequests.Should().ContainSingle().Which.SourceDurationSec.Should().Be(95);
        f.IsolationRequests[0].SourceUrl.Should().Be("https://signed.example/get");

        UsageRecordRequest usage = f.Recorded.Should().ContainSingle().Which;
        usage.Kind.Should().Be(UsageKind.AudioCleanupMinutes);
        usage.Quantity.Should().Be(2m, "95 s is two started minutes");
        usage.Capability.Should().Be(InferenceProviderCapability.AudioIsolation);
        usage.ModelName.Should().Be(Model);
        usage.IdempotencyKey.Should().Be($"audiocleanup:{f.ExecutionId:N}:2");

        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
        output["billedSeconds"]!.GetValue<long>().Should().Be(95);
        output["method"]!.GetValue<string>().Should().Be($"fal:{Model}:voice-stem");
        result.Output.Should().NotContain("signed.example").And.NotContain("projects/");
        result.OutputStorageKey.Should().EndWith("/step-2-clean.wav");
    }

    [Fact]
    public async Task Remove_music_says_in_the_method_that_it_uses_the_voice_stem()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync(Hosted("RemoveMusic"));

        result.Status.Should().Be(StepStatus.Completed);
        JsonNode.Parse(result.Output)!["method"]!.GetValue<string>().Should().Contain("voice-stem").And.Contain("music");
    }

    [Theory]
    [InlineData(AudioIsolationErrorKind.VendorFailed, "AUDIO_VENDOR_FAILED", FailureClass.PlatformProviderError)]
    [InlineData(AudioIsolationErrorKind.Retryable, "AUDIO_VENDOR_RETRYABLE", FailureClass.PlatformProviderError)]
    [InlineData(AudioIsolationErrorKind.Configuration, "AUDIO_PROVIDER_CONFIG", FailureClass.PlatformInternal)]
    [InlineData(AudioIsolationErrorKind.RequestInvalid, "AUDIO_REQUEST_INVALID", FailureClass.UserInput)]
    public async Task A_vendor_failure_is_classified_records_no_usage_and_still_deletes_the_upload(
        AudioIsolationErrorKind kind, string code, FailureClass expected)
    {
        Fixture f = new() { Isolate = _ => throw new AudioIsolationException(kind, "vendor said no", 500) };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be(code);
        result.FailureClass.Should().Be(expected);
        f.Recorded.Should().BeEmpty();
        f.Deleted.Should().Equal(f.Uploaded).And.HaveCount(1);
    }

    [Fact]
    public async Task An_unwrapped_vendor_transport_error_never_shows_its_text_and_still_deletes_the_upload()
    {
        const string Secret = "https://fal.example/queue?X-Amz-Signature=deadbeef";
        Fixture f = new() { Isolate = _ => throw new System.Net.Http.HttpRequestException("echoed " + Secret) };
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("AUDIO_VENDOR_FAILED");
        result.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        (result.ErrorDetails ?? string.Empty).Should().NotContain("X-Amz").And.NotContain("fal.example");
        result.Output.Should().NotContain("X-Amz").And.NotContain("fal.example");
        f.Recorded.Should().BeEmpty();
        f.Deleted.Should().Equal(f.Uploaded).And.HaveCount(1);
    }

    [Fact]
    public async Task A_vendor_failure_on_the_organizations_own_row_is_a_byo_provider_error()
    {
        Fixture f = new() { OrgOwned = true, Isolate = _ => throw new AudioIsolationException(AudioIsolationErrorKind.VendorFailed, "no") };
        (await f.RunAsync(Hosted())).FailureClass.Should().Be(FailureClass.ByoProviderError);
    }

    [Fact]
    public async Task A_stem_more_than_50_ms_off_the_source_is_refused_refunded_and_not_metered()
    {
        Fixture f = new();
        f.Probe.DurationSec = 60;
        f.Probe.StemDurationSec = 60.2;
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("AUDIO_DURATION_MISMATCH");
        result.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        f.Recorded.Should().BeEmpty();
        f.Deleted.Should().Equal(f.Uploaded).And.HaveCount(1);
    }

    [Fact]
    public async Task A_stem_within_50_ms_is_conformed_to_the_sources_exact_length()
    {
        Fixture f = new();
        f.Probe.DurationSec = 60;
        f.Probe.StemDurationSec = 60.03;
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.Status.Should().Be(StepStatus.Completed);
        string[] conform = f.Session.FfmpegCalls.Should().Contain(c => c.Any(a => a.StartsWith("apad=whole_dur=60.000000", StringComparison.Ordinal))).Which;
        conform.Should().Contain("-ar").And.Contain("pcm_s16le");
    }

    [Fact]
    public async Task A_remux_failure_is_a_platform_failure_and_the_upload_is_still_deleted()
    {
        Fixture f = new() { SourceKey = VideoKey };
        f.Probe.Video = true;
        f.Session.FailArgvContaining = "copy";
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.FailureCode.Should().Be("AUDIO_CLEANUP_FAILED");
        f.Deleted.Should().Equal(f.Uploaded);
        f.Recorded.Should().BeEmpty("a result that was not delivered is not charged");
    }

    [Fact]
    public async Task A_vendor_reporting_a_huge_billed_figure_does_not_move_the_charge()
    {
        Fixture f = new() { Isolate = req => new AudioIsolationResult([1, 2, 3], "audio/mpeg", ".mp3", null, 999_999, Model, null, "r") };
        f.Probe.DurationSec = 95;
        StepExecutionResult result = await f.RunAsync(Hosted());

        result.Status.Should().Be(StepStatus.Completed);
        f.Recorded.Should().ContainSingle().Which.Quantity.Should().Be(2m);
        JsonNode.Parse(result.Output)!["billedSeconds"]!.GetValue<long>().Should().Be(95);
        JsonNode.Parse(result.Output)!["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().Contain("billed_seconds_differ_from_source");
    }

    [Fact]
    public async Task Every_probe_and_ffmpeg_input_carries_a_demuxer_whitelist_and_the_stem_gets_the_audio_only_one()
    {
        Fixture f = new() { SourceKey = VideoKey };
        f.Probe.Video = true;
        (await f.RunAsync(Hosted())).Status.Should().Be(StepStatus.Completed);

        f.Session.ProbeCalls.Should().HaveCount(2).And.OnlyContain(c => c.Contains("-format_whitelist"));
        f.Session.ProbeCalls[0].Should().Contain(MediaDerivativeInputFormats.Whitelist);
        f.Session.ProbeCalls[1].Should().Contain(AudioCleanupArgv.StemFormatWhitelist);
        AudioCleanupArgv.StemFormatWhitelist.Split(',').Should().NotContain(["mov", "matroska"]);
        foreach (string[] call in f.Session.FfmpegCalls)
            call.Count(a => a == "-i").Should().Be(call.Count(a => a == "-format_whitelist"), "each -i has its own whitelist");
    }

    [Fact]
    public async Task The_hold_is_the_effective_cap_so_a_cap_sized_source_runs_and_a_step_cap_below_the_source_refuses()
    {
        Fixture f = new();
        f.Probe.DurationSec = 30 * 60;
        (await f.RunAsync(Hosted())).Status.Should().Be(StepStatus.Completed);

        Fixture low = new();
        low.Probe.DurationSec = 4 * 60;
        (await low.RunAsync(Hosted(extra: ",\"maxSourceMinutes\":3"))).FailureCode.Should().Be("AUDIO_SOURCE_TOO_LONG");
        low.NothingLeftTheHost();
    }

    // ---------------- local source cap ----------------

    [Fact]
    public async Task A_local_source_over_the_sixty_minute_cap_is_refused_before_the_cleaner_runs()
    {
        Fixture f = new();
        f.Probe.DurationSec = 61 * 60;

        StepExecutionResult result = await f.RunAsync("""{"target":"Wind","source":{"kind":"StepOutput","stepOrder":1}}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("AUDIO_SOURCE_TOO_LONG");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        f.Cleaner.Calls.Should().BeEmpty("the cap is checked before any cleanup work");
        f.Session.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task The_deployment_can_only_lower_the_local_cap_and_maxSourceMinutes_can_only_lower_it_further()
    {
        Fixture lowered = new() { Configuration = Config((AudioCleanupCreditPricing.LocalMaxSourceMinutesKey, "10")) };
        lowered.Probe.DurationSec = 11 * 60;
        (await lowered.RunAsync("""{"target":"Echo","source":{"kind":"StepOutput","stepOrder":1}}"""))
            .FailureCode.Should().Be("AUDIO_SOURCE_TOO_LONG");
        lowered.Cleaner.Calls.Should().BeEmpty();

        // A deployment value above the 60-minute default is ignored: the constant is a hard ceiling.
        Fixture raised = new() { Configuration = Config((AudioCleanupCreditPricing.LocalMaxSourceMinutesKey, "120")) };
        raised.Probe.DurationSec = 61 * 60;
        (await raised.RunAsync("""{"target":"Echo","source":{"kind":"StepOutput","stepOrder":1}}"""))
            .FailureCode.Should().Be("AUDIO_SOURCE_TOO_LONG");

        // A step's own maxSourceMinutes lowers the local cap too.
        Fixture stepCap = new();
        stepCap.Probe.DurationSec = 6 * 60;
        (await stepCap.RunAsync("""{"target":"Echo","source":{"kind":"StepOutput","stepOrder":1},"maxSourceMinutes":5}"""))
            .FailureCode.Should().Be("AUDIO_SOURCE_TOO_LONG");
    }

    // ---------------- local scratch disposal ----------------

    [Fact]
    public async Task The_local_scratch_directory_is_deleted_after_a_successful_run()
    {
        using ScratchFixture s = new();
        StepExecutionResult result = await s.Fixture.RunAsync("""{"target":"Wind","source":{"kind":"StepOutput","stepOrder":1}}""");

        result.Status.Should().Be(StepStatus.Completed);
        s.AssertRemoved();
    }

    [Fact]
    public async Task The_local_scratch_directory_is_deleted_after_a_failed_run()
    {
        using ScratchFixture s = new();
        s.Fixture.Cleaner.Throw = new CleanupException(CleanupErrorCodes.FfmpegFailed, "boom");

        StepExecutionResult result = await s.Fixture.RunAsync("""{"target":"Wind","source":{"kind":"StepOutput","stepOrder":1}}""");

        result.Status.Should().Be(StepStatus.Failed);
        s.AssertRemoved();
    }

    [Fact]
    public async Task The_local_scratch_directory_is_deleted_when_the_run_is_cancelled()
    {
        using ScratchFixture s = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();
        s.Fixture.Cleaner.Throw = new OperationCanceledException();

        Func<Task> act = () => s.Fixture.RunAsync("""{"target":"Wind","source":{"kind":"StepOutput","stepOrder":1}}""", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        s.AssertRemoved();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    // ---------------- fixture ----------------

    private sealed class Fixture
    {
        public Guid ExecutionId { get; } = Guid.NewGuid();
        public SessionWorkspace Session { get; } = new();
        public FakeProbe Probe { get; }
        public FakeCleaner Cleaner { get; } = new();
        public Mock<IProjectFileWorkspace> Files { get; } = new();
        public Mock<IInferenceProviderResolver> Resolver { get; } = new();
        public Mock<IPresignedUrlService> Presigner { get; } = new();
        public Mock<IUsageRecorder> Usage { get; } = new();
        public List<string> Uploaded { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<(string Key, TimeSpan Ttl)> Presigned { get; } = [];
        public List<AudioIsolationRequest> IsolationRequests { get; } = [];
        public List<UsageRecordRequest> Recorded { get; } = [];
        public string SourceKey { get; set; } = AudioCleanupStepExecutorTests.SourceKey;
        public bool AudioCleanupAllowed { get; set; } = true;
        public ResolvedAudioGenerationProvider? Row { get; set; }
        public bool OrgOwned { get; set; }
        public bool Priced { get; set; } = true;
        public bool HasPresigner { get; set; } = true;
        public Func<AudioIsolationRequest, AudioIsolationResult>? Isolate { get; set; }
        public IConfiguration? Configuration { get; set; }
        public IMediaWorkspaceFactory? Factory { get; set; }

        public Fixture()
        {
            Probe = new FakeProbe();
            Session.Probe = Probe;
            Row = new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation,
                "", Model, "test", 60, AccountId: Guid.NewGuid());
        }

        public void NothingLeftTheHost()
        {
            Uploaded.Should().BeEmpty();
            Presigned.Should().BeEmpty();
            IsolationRequests.Should().BeEmpty();
            Recorded.Should().BeEmpty();
        }

        public async Task<StepExecutionResult> RunAsync(string configJson, CancellationToken ct = default)
        {
            Session.OnUpload = Uploaded.Add;
            Files.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Files.Setup(w => w.RegisterStoredObjectAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                    It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string key, string name, string type, long size, SummaryStatus s, FileIndexingStatus i, CancellationToken c, string cat) =>
                    new ProjectWorkspaceFile(Guid.NewGuid(), p, name, null, cat, key, type, size, DateTime.UtcNow, null));
            Files.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string local, string name, string type, CancellationToken c, string cat) =>
                {
                    string key = $"projects/{p}/{cat}/{name}";
                    Uploaded.Add(key);
                    return key;
                });
            Files.Setup(w => w.DeleteArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, string, CancellationToken>((_, key, _) => Deleted.Add(key))
                .Returns(Task.CompletedTask);

            Resolver.Setup(r => r.ResolveAudioIsolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
                    .ThrowsAsync(new UnpricedUsageException(UsageKind.AudioCleanupMinutes, InferenceProviderKind.FalAi, Model));
            }

            Presigner.Setup(p => p.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .Callback<Guid, string, TimeSpan>((_, key, ttl) => Presigned.Add((key, ttl)))
                .ReturnsAsync("https://signed.example/get");
            Usage.Setup(u => u.RecordAsync(It.IsAny<UsageRecordRequest>(), It.IsAny<CancellationToken>()))
                .Callback<UsageRecordRequest, CancellationToken>((r, _) => Recorded.Add(r))
                .Returns(ValueTask.FromResult(0L));

            Mock<IAudioIsolationClient> client = new();
            client.Setup(c => c.IsolateAsync(It.IsAny<AudioIsolationRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()))
                .ReturnsAsync((AudioIsolationRequest r, CancellationToken _, Func<string, Task>? _) =>
                {
                    IsolationRequests.Add(r);
                    return (Isolate ?? (req => new AudioIsolationResult([1, 2, 3], "audio/mpeg", ".mp3", null, req.SourceDurationSec, Model, null, "req-1")))(r);
                });
            Mock<IAudioIsolationClientFactory> clients = new();
            clients.Setup(c => c.Get(It.IsAny<ResolvedAudioGenerationProvider>())).Returns(client.Object);

            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(new OrganizationEntitlementSet { Tier = "test", AudioCleanupAllowed = AudioCleanupAllowed }));
            ServiceProvider provider = new ServiceCollection().AddSingleton(entitlements.Object).BuildServiceProvider();

            var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.AudioCleanup, AudioCleanupConfigJson = configJson };
            var executor = new AudioCleanupStepExecutor(
                new Mock<IVideoToolRunner>(MockBehavior.Strict).Object,
                Files.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new VideoEditingOptions()),
                Cleaner,
                Resolver.Object,
                clients.Object,
                NullLogger<AudioCleanupStepExecutor>.Instance,
                prices.Object,
                Usage.Object,
                HasPresigner ? Presigner.Object : null,
                configuration: Configuration,
                mediaWorkspaces: Factory ?? new StubFactory(Session));
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
                CancellationToken = ct
            });
        }
    }

    private sealed class FakeCleaner : ILocalAudioCleaner
    {
        public List<LocalCleanupTarget> Calls { get; } = [];
        public bool Applied { get; set; } = true;
        public string? Reason { get; set; }
        public Exception? Throw { get; set; }

        public Task<LocalCleanupResult> CleanAsync(string sourcePath, string outputPath, LocalCleanupTarget target, CancellationToken ct)
        {
            Calls.Add(target);
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(new LocalCleanupResult(Applied, Reason, 12.5, 48_000, 2, CleanupMeasurements.None));
        }
    }

    private sealed class FakeProbe
    {
        public double DurationSec { get; set; } = 12.5;
        public double? StemDurationSec { get; set; }
        public bool Video { get; set; }
        public string? AudioCodec { get; set; } = "aac";
    }

    private sealed class SessionWorkspace : IMediaWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Published { get; } = [];
        public List<string[]> FfmpegCalls { get; } = [];
        public string? FailArgvContaining { get; set; }
        public FakeProbe? Probe { get; set; }

        public string RootPath => "session:/";

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct) => Task.CompletedTask;

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            RunFfmpegAsync(argv, timeout, ct, null);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        {
            FfmpegCalls.Add(argv.ToArray());
            if (FailArgvContaining is not null && argv.Contains(FailArgvContaining))
                return Task.FromResult(new VideoToolResult(1, string.Empty, "boom", false));
            Files[argv[^1]] = new byte[256];
            return Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));
        }

        public List<string[]> ProbeCalls { get; } = [];

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
        {
            ProbeCalls.Add(argv.ToArray());
            bool stem = argv[^1].StartsWith("audio-cleanup-stem", StringComparison.Ordinal);
            double dur = stem ? Probe!.StemDurationSec ?? Probe.DurationSec : Probe!.DurationSec;
            bool video = Probe.Video && !stem;
            string streams = (video ? "{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1920,\"height\":1080,\"r_frame_rate\":\"30/1\"}," : "")
                + (Probe.AudioCodec is null ? "" : $"{{\"codec_type\":\"audio\",\"codec_name\":\"{Probe.AudioCodec}\",\"sample_rate\":\"48000\",\"channels\":2}}");
            streams = streams.TrimEnd(',');
            string json = $"{{\"streams\":[{streams}],\"format\":{{\"duration\":\"{dur.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"}}}}";
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

        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct)
        {
            // The fake local cleaner writes nothing itself: the clean file exists once a cleaner (or an ffmpeg call) ran.
            if (virtualPath == "audio-cleanup-clean.wav" && !Files.ContainsKey(virtualPath))
                Files[virtualPath] = new byte[512];
            return Task.FromResult(Files.TryGetValue(virtualPath, out byte[]? bytes) ? new MediaWorkspaceStat(true, bytes.Length) : new MediaWorkspaceStat(false, 0));
        }

        public Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            Published.Add(finalStorageKey);
            return Task.FromResult(finalStorageKey);
        }

        /// <summary>Called with the key of every session upload (the vendor's source audio goes this way).</summary>
        public Action<string>? OnUpload { get; set; }

        public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            Published.Add(finalStorageKey);
            OnUpload?.Invoke(finalStorageKey);
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

    /// <summary>A recording local workspace factory: a REAL scratch directory with a sentinel media file, so the test
    /// asserts the directory (and everything in it) is gone after the executor's finally.</summary>
    private sealed class ScratchFactory : IMediaWorkspaceFactory
    {
        private readonly SessionWorkspace _session;
        private readonly string _root;

        public ScratchFactory(SessionWorkspace session, string root)
        {
            _session = session;
            _root = root;
        }

        public VideoScratchSpace? Scratch { get; private set; }

        public string? ScratchPath => Scratch?.DirectoryPath;

        public Task<MediaWorkspaceOpenResult> OpenAsync(MediaWorkspaceRequest request, CancellationToken cancellationToken = default)
        {
            Scratch = VideoScratchSpace.Create(
                new VideoEditingOptions { ScratchPath = _root }, request.ExecutionId, request.StepId, NullLogger.Instance);
            File.WriteAllText(Scratch.GetPath("audio-cleanup-source"), "customer media");
            return Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                _session, Scratch, ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null, request.ExecutionId, request.StepOrder, NullLogger.Instance)));
        }
    }

    private sealed class ScratchFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "reelbolt-scratch-" + Guid.NewGuid().ToString("N"));

        public ScratchFixture()
        {
            Directory.CreateDirectory(_root);
            Fixture = new Fixture();
            Factory = new ScratchFactory(Fixture.Session, _root);
            Fixture.Factory = Factory;
        }

        public Fixture Fixture { get; }

        public ScratchFactory Factory { get; }

        public void AssertRemoved()
        {
            Factory.ScratchPath.Should().NotBeNull("the factory opened a real local scratch directory");
            Directory.Exists(Factory.ScratchPath!).Should().BeFalse("the executor's finally deletes the whole scratch directory");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
