using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// WP D12: runner output is verified against the PLATFORM-SIDE job spec before it is promoted, and a
/// failure deletes the staged object instead of delivering it. Everything here runs against a fake
/// object store and a fake tool runner, so no object store, no ffmpeg and no network is involved —
/// the argv each check builds is asserted directly, which is the part that must not change.
/// </summary>
public class RunnerOutputVerificationTests
{
    private const string ProbeJson =
        "{\"streams\":[{\"codec_type\":\"video\",\"width\":1920,\"height\":1080,\"codec_name\":\"h264\",\"r_frame_rate\":\"30/1\"}]," +
        "\"format\":{\"duration\":\"12.500000\"}}";

    private static readonly Guid JobId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed class FakeStore : IRunnerOutputStore
    {
        public long? Size { get; set; } = 1024;
        public string? Sha256 { get; set; } = "aa";
        public string Url { get; set; } = "https://objects.example/reelbolt/runner-staging/" + JobId.ToString("D") + "/0?X-Amz-Signature=deadbeef";
        public bool PromoteSucceeds { get; set; } = true;
        public int Promotions { get; private set; }
        public int Discards { get; private set; }
        public TimeSpan? LastUrlTtl { get; private set; }
        public long? LastPromoteCap { get; private set; }
        public string? LastPromotedTo { get; private set; }

        public Task<long?> TryGetSizeAsync(string key, CancellationToken ct) => Task.FromResult(Size);

        /// <summary>Bytes the fake "downloads"; null makes the download unavailable.</summary>
        public byte[]? DownloadBytes { get; set; }
        public int Downloads { get; private set; }
        public long? LastDownloadCap { get; private set; }

        public async Task<byte[]?> TryDownloadAsync(string key, string destinationPath, long maxBytes, CancellationToken ct)
        {
            Downloads++;
            LastDownloadCap = maxBytes;
            if (DownloadBytes is null) return null;
            await System.IO.File.WriteAllBytesAsync(destinationPath, DownloadBytes, ct);
            return DownloadBytes[..Math.Min(16, DownloadBytes.Length)];
        }

        public Task<string> CreateStagingGetUrlAsync(string stagingKey, TimeSpan ttl, CancellationToken ct)
        {
            LastUrlTtl = ttl;
            return Task.FromResult(Url);
        }

        public Task<long> PromoteAsync(string stagingKey, string finalKey, long maxBytes, CancellationToken ct)
        {
            Promotions++;
            LastPromoteCap = maxBytes;
            LastPromotedTo = finalKey;
            if (!PromoteSucceeds)
                throw new InvalidOperationException("copy refused");
            return Task.FromResult(Size ?? 0);
        }

        public Task DiscardAsync(string key, CancellationToken ct)
        {
            Discards++;
            return Task.CompletedTask;
        }

        /// <summary>Mirrors the real store's contract: null when the object is over the hashing limit.</summary>
        public Task<string?> TryGetSha256Async(string key, long sizeBytes, long maxBytes, CancellationToken ct)
            => Task.FromResult(maxBytes > 0 && sizeBytes > maxBytes ? null : Sha256);
    }

    private sealed class FakeToolRunner : IVideoToolRunner
    {
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority)
            => RunFfmpegAsync(args, timeout, ct, onStdOutLine);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
            => RunFfprobeAsync(args, timeout, ct);

        public string ProbeStdOut { get; set; } = ProbeJson;
        public bool ProbeSucceeds { get; set; } = true;
        /// <summary>When set, a probe of a local file (no http input) uses this instead of <see cref="ProbeSucceeds"/>.</summary>
        public bool? LocalProbeSucceeds { get; set; }
        public string ProbeStdErr { get; set; } = "Invalid data found";
        public bool FfmpegSucceeds { get; set; } = true;
        public Func<int, string> FfmpegStdErr { get; set; } = _ => string.Empty;

        public int ProbeCalls { get; private set; }
        public int FfmpegCalls { get; private set; }
        public IReadOnlyList<string> ProbeArgv { get; private set; } = Array.Empty<string>();
        public List<IReadOnlyList<string>> FfmpegArgv { get; } = new();

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            ProbeCalls++;
            ProbeArgv = args;
            bool local = !args[^1].StartsWith("http", StringComparison.OrdinalIgnoreCase);
            bool ok = local && LocalProbeSucceeds.HasValue ? LocalProbeSucceeds.Value : ProbeSucceeds;
            return Task.FromResult(ok
                ? new VideoToolResult(0, ProbeStdOut, string.Empty, false)
                : new VideoToolResult(1, string.Empty, ProbeStdErr, false));
        }

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            workspace.RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
            Action<string> onStdOutLine) =>
            workspace.RunFfmpegAsync(args, timeout, ct, onStdOutLine);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            FfmpegCalls++;
            FfmpegArgv.Add(args);
            return Task.FromResult(FfmpegSucceeds
                ? new VideoToolResult(0, string.Empty, FfmpegStdErr(FfmpegCalls), false)
                : new VideoToolResult(1, string.Empty, "decode error", false));
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
            => RunFfmpegAsync(args, timeout, ct);
    }

    private static OutputVerificationOptions VerificationOptions(
        int spotCheckPercent = 0,
        long maxHashBytes = 512L * 1024 * 1024,
        int spotCheckWindows = 3) => new()
        {
            SpotCheckPercent = spotCheckPercent,
            MaxHashBytes = maxHashBytes,
            SpotCheckWindows = spotCheckWindows
        };

    private static RunnerOutputSpec Spec(long maxBytes = 10_000, string? sha256 = null) => new(
        MaxBytes: maxBytes,
        DurationSeconds: 12.5,
        Width: 1920,
        Height: 1080,
        VideoCodec: "h264",
        FpsNum: 30,
        FpsDen: 1,
        Sha256: sha256);

    private static RunnerOutputRequest Request(
        RunnerOutputSpec? spec = null,
        RunnerJobTarget target = RunnerJobTarget.OrgRunner,
        int stagingIndex = 0) => new(
            ProjectId: Guid.NewGuid(),
            JobId: JobId,
            StagingIndex: stagingIndex,
            FinalKey: "projects/p/outputFiles/e/render.mp4",
            Target: target,
            Spec: spec ?? Spec());

    private static OutputVerifier Verifier(
        FakeStore store, FakeToolRunner runner, OutputVerificationOptions? options = null) =>
        new(store, runner, Options.Create(options ?? VerificationOptions()), NullLogger<OutputVerifier>.Instance);


    [Fact]
    public async Task A_matching_output_is_promoted()
    {
        var store = new FakeStore { Sha256 = "aa" };
        var runner = new FakeToolRunner();

        OutputVerificationResult result = await Verifier(store, runner)
            .VerifyAndPromoteAsync(Request(Spec(sha256: "AA")));

        result.Verified.Should().BeTrue();
        result.FailureCode.Should().BeNull();
        result.FailureClass.Should().Be(FailureClass.None);
        result.FinalKey.Should().Be("projects/p/outputFiles/e/render.mp4");
        store.Promotions.Should().Be(1);
        store.Discards.Should().Be(0);
        store.LastPromotedTo.Should().Be("projects/p/outputFiles/e/render.mp4");
        store.LastPromoteCap.Should().Be(10_000);
    }

    [Fact]
    public async Task A_duration_mismatch_fails_the_output_and_deletes_the_staged_object()
    {
        // The acceptance criterion: a runner that returns a video of the wrong length fails the step
        // rather than delivering it.
        var store = new FakeStore();
        var runner = new FakeToolRunner
        {
            ProbeStdOut = ProbeJson.Replace("12.500000", "47.000000", StringComparison.Ordinal)
        };

        OutputVerificationResult result = await Verifier(store, runner)
            .VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.DurationMismatchCode);
        result.FailureClass.Should().Be(FailureClass.Runner, "a runner that produced the wrong thing is not refunded");
        result.Message.Should().Contain("47").And.Contain("12.5");
        store.Promotions.Should().Be(0, "nothing may reach the final key without passing verification");
        store.Discards.Should().Be(1, "the staged object is deleted, not left behind for the lifecycle rule");
    }

    [Theory]
    [InlineData(1920, 1080, "h264", 30, 1, true)]
    [InlineData(1280, 1080, "h264", 30, 1, false)]
    [InlineData(1920, 720, "h264", 30, 1, false)]
    [InlineData(1920, 1080, "hevc", 30, 1, false)]
    [InlineData(1920, 1080, "h264", 25, 1, false)]
    public async Task Every_shape_check_is_enforced(int width, int height, string codec, int fpsNum, int fpsDen, bool expected)
    {
        var store = new FakeStore();
        var runner = new FakeToolRunner
        {
            ProbeStdOut = "{\"streams\":[{\"codec_type\":\"video\",\"width\":" + width + ",\"height\":" + height +
                ",\"codec_name\":\"" + codec + "\",\"r_frame_rate\":\"" + fpsNum + "/" + fpsDen + "\"}]," +
                "\"format\":{\"duration\":\"12.500000\"}}"
        };

        OutputVerificationResult result = await Verifier(store, runner).VerifyAndPromoteAsync(Request());

        result.Verified.Should().Be(expected);
        if (!expected)
            store.Promotions.Should().Be(0);
    }

    [Fact]
    public async Task Duration_tolerance_is_one_frame_when_the_spec_names_a_frame_rate()
    {
        // 0.5 s off is inside one frame at 1 fps and far outside it at 30 fps — the tolerance is not
        // a constant, it follows the rate the platform asked for.
        // Each case's probe output matches its own spec's frame rate, so the duration check is the
        // only one in play.
        string probeAtOneFps = ProbeJson
            .Replace("12.500000", "13.000000", StringComparison.Ordinal)
            .Replace("\"30/1\"", "\"1/1\"", StringComparison.Ordinal);

        var atOneFps = new FakeStore();
        var slowRunner = new FakeToolRunner { ProbeStdOut = probeAtOneFps };
        RunnerOutputSpec slowSpec = Spec() with { FpsNum = 1, FpsDen = 1 };
        (await Verifier(atOneFps, slowRunner).VerifyAndPromoteAsync(Request(slowSpec))).Verified.Should().BeTrue();

        var atThirtyFps = new FakeStore();
        var fastRunner = new FakeToolRunner { ProbeStdOut = probeAtOneFps.Replace("\"1/1\"", "\"30/1\"", StringComparison.Ordinal) };
        OutputVerificationResult result = await Verifier(atThirtyFps, fastRunner).VerifyAndPromoteAsync(Request(Spec()));
        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.DurationMismatchCode);
    }

    [Fact]
    public async Task An_oversized_output_is_rejected_before_it_is_ever_probed()
    {
        var store = new FakeStore { Size = 5_000_000 };
        var runner = new FakeToolRunner();

        OutputVerificationResult result = await Verifier(store, runner)
            .VerifyAndPromoteAsync(Request(Spec(maxBytes: 1_000_000)));

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.TooLargeCode);
        result.SizeBytes.Should().Be(5_000_000);
        runner.ProbeCalls.Should().Be(0, "there is nothing to learn from probing an object we already rejected");
        store.Discards.Should().Be(1);
    }

    [Fact]
    public async Task A_missing_object_is_a_runner_failure_not_a_platform_one()
    {
        var store = new FakeStore { Size = null };

        OutputVerificationResult result = await Verifier(store, new FakeToolRunner()).VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.MissingCode);
        result.FailureClass.Should().Be(FailureClass.Runner);
        store.Discards.Should().Be(0, "there is no object to delete");
    }

    [Fact]
    public async Task An_unreadable_object_fails_and_is_deleted()
    {
        var store = new FakeStore();
        var runner = new FakeToolRunner { ProbeSucceeds = false };

        OutputVerificationResult result = await Verifier(store, runner).VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.UnreadableCode);
        store.Discards.Should().Be(1);
    }

    [Fact]
    public async Task The_engine_hashes_the_object_rather_than_trusting_the_digest_the_runner_reported()
    {
        var store = new FakeStore { Sha256 = "1111111111111111111111111111111111111111111111111111111111111111" };

        OutputVerificationResult result = await Verifier(store, new FakeToolRunner())
            .VerifyAndPromoteAsync(Request(Spec(sha256: "2222222222222222222222222222222222222222222222222222222222222222")));

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.ChecksumMismatchCode);
        result.FailureClass.Should().Be(FailureClass.Runner);
        result.Sha256.Should().Be(store.Sha256);
        store.Discards.Should().Be(1);
    }

    [Fact]
    public async Task An_object_over_the_hashing_limit_is_still_verified_by_shape_and_size()
    {
        // The digest check is integrity against a runner CLAIM; when the object is too large to hash
        // economically the claim goes unchecked and is logged, but the shape checks still apply.
        var store = new FakeStore { Size = 1000, Sha256 = "aa" };

        OutputVerificationResult result = await Verifier(store, new FakeToolRunner(), VerificationOptions(maxHashBytes: 100))
            .VerifyAndPromoteAsync(Request(Spec(maxBytes: 10_000, sha256: "ffff")));

        result.Verified.Should().BeTrue("the digest the runner claimed was never checked, so it cannot have failed");
        result.Sha256.Should().BeNull("the object was not downloaded, so the engine computed no digest of its own");
    }

    [Fact]
    public async Task The_probe_reads_the_staging_object_over_http()
    {
        // The engine probes the STAGING object, never the promoted one, and its argv has to admit the
        // http/https protocols that the rest of this feature deliberately pins to file — the input is
        // a presigned URL the platform minted, not anything a runner supplied.
        var store = new FakeStore();
        var runner = new FakeToolRunner();

        await Verifier(store, runner).VerifyAndPromoteAsync(Request());

        runner.ProbeArgv.Should().Contain(store.Url);
        runner.ProbeArgv.Should().ContainInOrder("-protocol_whitelist", OutputVerifier.ProbeProtocolWhitelist);
        runner.ProbeArgv.Should().NotContain("-i", "ffprobe takes the input positionally, unlike ffmpeg");
        store.LastUrlTtl.Should().NotBeNull();
    }

    [Fact]
    public void The_probe_whitelist_admits_every_layer_an_https_fetch_opens()
    {
        // The regression this pins: "file,http,https" looked right and rejected every real presigned
        // URL, because ffmpeg opens https as https -> tls -> tcp and checks each layer against the
        // whitelist ("Protocol 'tls' not on whitelist"). The earlier test only compared the argv to a
        // string, so it could never see that. Every runner-built render was refused until this.
        string[] whitelisted = OutputVerifier.ProbeProtocolWhitelist.Split(',');

        whitelisted.Should().Contain(["http", "https", "tcp", "tls"]);
        whitelisted.Should().NotContain(["concat", "subfile", "data", "ftp", "crypto", "pipe", "udp"],
            "only the http(s) transport stack is admitted, nothing a hostile file could use to reach elsewhere");
    }

    [Fact]
    public async Task A_promotion_failure_is_the_platforms_fault_not_the_runners()
    {
        var store = new FakeStore { PromoteSucceeds = false };

        OutputVerificationResult result = await Verifier(store, new FakeToolRunner()).VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.PromoteFailedCode);
        result.FailureClass.Should().Be(FailureClass.PlatformInternal, "the output passed verification; our copy failed");
    }


    // -----------------------------------------------------------------
    // The sampled spot check (D12 step 3): OrgRunner only, and never the reason an otherwise verified
    // output is rejected unless EVERY window is blank.
    // -----------------------------------------------------------------

    [Fact]
    public async Task The_spot_check_is_skipped_when_its_share_is_zero()
    {
        var runner = new FakeToolRunner();
        await Verifier(new FakeStore(), runner, VerificationOptions(spotCheckPercent: 0)).VerifyAndPromoteAsync(Request());

        runner.FfmpegCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_spot_check_runs_on_every_org_runner_output_when_its_share_is_a_hundred()
    {
        var runner = new FakeToolRunner();
        await Verifier(new FakeStore(), runner, VerificationOptions(spotCheckPercent: 100)).VerifyAndPromoteAsync(Request());

        runner.FfmpegCalls.Should().Be(3);
        runner.FfmpegArgv[0].Should().ContainInOrder("-ss");
        runner.FfmpegArgv[0].Should().Contain("blackdetect=d=0.5:pix_th=0.10,freezedetect=n=-60dB:d=0.5");
        runner.FfmpegArgv[0].Should().Contain("null");
    }

    [Fact]
    public async Task Both_tools_that_read_the_staged_object_carry_the_same_containment()
    {
        // The staged object is untrusted (a runner built it). With http(s) admitted, a "render" that
        // is really an HLS/DASH/concat playlist would make the engine's own ffprobe/ffmpeg fetch
        // whatever URL the file names. Measured with a real ffprobe: one hostile playlist, one request
        // to a listener on the probing host. The format list is what stops it, on BOTH calls — the
        // spot check used to carry no whitelist at all.
        var runner = new FakeToolRunner();
        await Verifier(new FakeStore(), runner, VerificationOptions(spotCheckPercent: 100)).VerifyAndPromoteAsync(Request());

        runner.ProbeArgv.Should().ContainInOrder("-protocol_whitelist", OutputVerifier.ProbeProtocolWhitelist, "-format_whitelist", OutputVerifier.ProbeFormatWhitelist);
        runner.FfmpegArgv.Should().NotBeEmpty();
        foreach (IReadOnlyList<string> argv in runner.FfmpegArgv)
        {
            argv.Should().ContainInOrder("-protocol_whitelist", OutputVerifier.ProbeProtocolWhitelist, "-format_whitelist", OutputVerifier.ProbeFormatWhitelist, "-i");
        }
    }

    [Fact]
    public void The_format_whitelist_names_no_demuxer_that_can_follow_a_url()
    {
        string[] formats = OutputVerifier.ProbeFormatWhitelist.Split(',');

        formats.Should().Contain(["mp4", "wav", "mp3", "matroska", "jpeg_pipe", "png_pipe"], "everything a render or a published media file legitimately is");
        formats.Should().NotContain(["hls", "applehttp", "dash", "concat", "ffconcat", "sdp", "rtsp", "rtp", "image2", "lavfi", "tee"],
            "a demuxer that reads URLs out of the file turns the verifier into a request forwarder");
    }

    [Fact]
    public async Task A_cloud_pool_output_is_never_spot_checked()
    {
        // It exists to catch a customer's own machine; paying a real decode for our own worker would
        // be waste, and CloudPool is trusted anyway.
        var runner = new FakeToolRunner();
        await Verifier(new FakeStore(), runner, VerificationOptions(spotCheckPercent: 100))
            .VerifyAndPromoteAsync(Request(target: RunnerJobTarget.CloudPool));

        runner.FfmpegCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_output_whose_every_sampled_window_is_blank_fails()
    {
        var store = new FakeStore();
        var runner = new FakeToolRunner
        {
            FfmpegStdErr = _ => "[blackdetect @ 0x1] black_start:0 black_end:1 black_duration:1"
        };

        OutputVerificationResult result = await Verifier(store, runner, VerificationOptions(spotCheckPercent: 100))
            .VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.BlankCode);
        result.FailureClass.Should().Be(FailureClass.Runner);
        store.Discards.Should().Be(1);
        store.Promotions.Should().Be(0);
    }

    [Fact]
    public async Task One_blank_window_is_content_and_does_not_fail_an_otherwise_good_output()
    {
        // A fade to black or a still title card is ordinary video. The rule is "every sampled window",
        // not "any".
        var store = new FakeStore();
        var runner = new FakeToolRunner
        {
            FfmpegStdErr = call => call == 1
                ? "[freezedetect @ 0x1] freeze_start: 0 freeze_duration: 1"
                : string.Empty
        };

        OutputVerificationResult result = await Verifier(store, runner, VerificationOptions(spotCheckPercent: 100))
            .VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeTrue();
        store.Discards.Should().Be(0);
    }

    [Fact]
    public async Task A_spot_check_that_could_not_run_is_not_a_verification_failure()
    {
        // It is the weakest of the four checks and the three structural ones already passed, so a
        // decode error, a timeout or unparseable output degrades to "no signal".
        var store = new FakeStore();
        var runner = new FakeToolRunner { FfmpegSucceeds = false };

        OutputVerificationResult result = await Verifier(store, runner, VerificationOptions(spotCheckPercent: 100))
            .VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeTrue();
        store.Promotions.Should().Be(1);
    }

    [Fact]
    public async Task A_short_blank_segment_inside_a_window_is_not_a_blank_window()
    {
        // The detector is asked for segments of at least half a second; a 0.2 s dip is a cut, not a
        // black frame.
        var store = new FakeStore();
        var runner = new FakeToolRunner
        {
            FfmpegStdErr = _ => "[blackdetect @ 0x1] black_start:0.1 black_end:0.3 black_duration:0.2"
        };

        OutputVerificationResult result = await Verifier(store, runner, VerificationOptions(spotCheckPercent: 100))
            .VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeTrue();
    }

    [Fact]
    public void The_staging_key_is_the_one_a_presigned_put_targets()
    {
        S3RunnerOutputStore.StagingKeyFor(JobId, 0)
            .Should().Be("runner-staging/" + JobId.ToString("D") + "/0");
        S3RunnerOutputStore.StagingKeyFor(JobId, 7)
            .Should().Be("runner-staging/" + JobId.ToString("D") + "/7");
    }

    [Fact]
    public async Task A_failed_probe_over_the_presigned_url_is_not_final_when_the_downloaded_object_is_media()
    {
        // The pool run: the staged object existed but ffprobe over https exited 1. The engine now takes a
        // second look at the object itself before it blames the runner.
        var store = new FakeStore { Size = 2048, DownloadBytes = new byte[2048] };
        var runner = new FakeToolRunner { ProbeSucceeds = false, LocalProbeSucceeds = true };

        OutputVerificationResult result = await Verifier(store, runner).VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeTrue();
        store.Downloads.Should().Be(1);
        runner.ProbeArgv[^1].Should().NotStartWith("http", "the second probe reads a local file");
        string argv = string.Join(' ', runner.ProbeArgv);
        argv.Should().Contain("-protocol_whitelist file ").And.NotContain("https");
    }

    [Fact]
    public async Task When_the_url_is_known_broken_the_spot_check_does_not_read_it()
    {
        var store = new FakeStore { Size = 2048, DownloadBytes = new byte[2048] };
        var runner = new FakeToolRunner { ProbeSucceeds = false, LocalProbeSucceeds = true };

        OutputVerificationResult result = await Verifier(store, runner, VerificationOptions(spotCheckPercent: 100))
            .VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeTrue();
        runner.FfmpegCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_second_look_download_is_capped_by_the_hashing_limit_and_refused_above_it()
    {
        var store = new FakeStore { Size = 4096, DownloadBytes = new byte[4096] };
        var runner = new FakeToolRunner { ProbeSucceeds = false, LocalProbeSucceeds = true };

        OutputVerificationResult result = await Verifier(store, runner, VerificationOptions(maxHashBytes: 1024))
            .VerifyAndPromoteAsync(Request());

        result.FailureCode.Should().Be(OutputVerifier.UnreadableCode);
        result.Message.Should().Contain("too large for a second look");
        store.Downloads.Should().Be(0, "an over-limit object is never downloaded");
    }

    [Fact]
    public async Task An_unreadable_object_reports_its_size_and_leading_bytes_without_any_url()
    {
        var store = new FakeStore { Size = 5, DownloadBytes = new byte[] { 0x3c, 0x68, 0x74, 0x6d, 0x6c } };
        var runner = new FakeToolRunner
        {
            ProbeSucceeds = false,
            ProbeStdErr = "https://acct.r2.cloudflarestorage.com/b/runner-staging/x/1?X-Amz-Signature=deadbeef: Invalid data found"
        };

        OutputVerificationResult result = await Verifier(store, runner).VerifyAndPromoteAsync(Request());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.UnreadableCode);
        result.Message.Should().Contain("5 bytes").And.Contain("3c68746d6c");
        result.Message.Should().NotContain("deadbeef").And.NotContain("r2.cloudflarestorage");
        store.Discards.Should().Be(1);
    }

    [Fact]
    public async Task An_empty_staged_object_is_named_as_empty()
    {
        var store = new FakeStore { Size = 0, DownloadBytes = Array.Empty<byte>() };
        var runner = new FakeToolRunner { ProbeSucceeds = false };

        OutputVerificationResult result = await Verifier(store, runner).VerifyAndPromoteAsync(Request());

        result.FailureCode.Should().Be(OutputVerifier.UnreadableCode);
        result.Message.Should().Contain("empty");
    }

    [Fact]
    public void Presigned_urls_are_redacted_from_tool_output()
    {
        string text = "https://a.r2.cloudflarestorage.com/b/runner-staging/j/1?X-Amz-Signature=abc&X-Amz-Credential=k: Server returned 403";
        OutputVerifier.RedactUrls(text).Should().Be("[staging url]: Server returned 403");
    }
}

/// <summary>
/// The verifier's registrations, exercised through the REAL composition root (the same
/// <c>AddWorkflowEngineServices</c> Program.cs calls), so the keyed "presign" S3 client its store
/// depends on is proven to resolve here rather than at the first runner render in production.
/// </summary>
public class OutputVerificationCompositionTests
{
    [Fact]
    public void The_verifier_and_its_store_resolve_from_the_real_registrations()
    {
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "workflow-engine",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));
        using IServiceScope scope = root.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IOutputVerifier>().Should().BeOfType<OutputVerifier>();
        scope.ServiceProvider.GetRequiredService<IRunnerOutputStore>().Should().BeOfType<S3RunnerOutputStore>();
        scope.ServiceProvider.GetRequiredService<IOptions<OutputVerificationOptions>>().Value
            .SpotCheckPercent.Should().BeInRange(0, 100);
    }
}
