using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Video;
using Moq;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class LargeSourceFootageEngineTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static VideoSourceLimits Limits(string? bytes = null, string? seconds = null)
    {
        var data = new System.Collections.Generic.Dictionary<string, string?>();
        if (bytes is not null) data["VideoSources:MaxBytes"] = bytes;
        if (seconds is not null) data["VideoSources:MaxSeconds"] = seconds;
        return VideoSourceLimits.From(new ConfigurationBuilder().AddInMemoryCollection(data).Build());
    }

    [Fact]
    public void The_legacy_step_value_reads_as_unset()
    {
        VideoSourceLimits limits = Limits();
        limits.EffectiveBytes(VideoSourceLimits.LegacyDefaultMaxInputBytes).Should().Be(VideoSourceLimits.DefaultMaxBytes);
        limits.EffectiveSeconds(VideoSourceLimits.LegacyDefaultMaxDurationSeconds).Should().Be(VideoSourceLimits.DefaultMaxSeconds);
    }

    [Fact]
    public void An_explicit_lower_step_value_is_honoured_and_a_higher_one_is_capped()
    {
        VideoSourceLimits limits = Limits();
        limits.EffectiveBytes(500_000_000).Should().Be(500_000_000);
        limits.EffectiveBytes(100L * Gib).Should().Be(VideoSourceLimits.DefaultMaxBytes);
        limits.EffectiveSeconds(600).Should().Be(600);
        limits.EffectiveSeconds(99999).Should().Be(VideoSourceLimits.DefaultMaxSeconds);
    }

    [Fact]
    public void The_new_codes_are_user_input_and_the_old_ones_stay_mapped()
    {
        foreach (string code in new[] { "SOURCE_TOO_LARGE", "SOURCE_TOO_LONG", "INPUT_TOO_LARGE", "DURATION_EXCEEDED" })
            FailureClassifier.ForCode(code).Should().Be(FailureClass.UserInput);
        FailureClassifier.ForCode("SCRATCH_BUSY").Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public void The_plain_messages_carry_no_byte_counts_and_say_what_to_do()
    {
        string large = FailureMessages.UserMessage("SOURCE_TOO_LARGE");
        string busy = FailureMessages.UserMessage("SCRATCH_BUSY");
        large.Should().Contain("Compress").And.NotContainAny("bytes", "MaxInputBytes", "MaxBytes");
        busy.Should().Contain("nothing was charged").And.Contain("few minutes");
    }

    [Fact]
    public void A_full_disk_error_maps_to_scratch_busy()
    {
        FailureClassifier.CodeFromMessage("ffmpeg: No space left on device").Should().Be("SCRATCH_BUSY");
        FailureClassifier.CodeFromException(new System.IO.IOException("write failed: No space left on device")).Should().Be("SCRATCH_BUSY");
        FailureClassifier.CodeFromException(new MediaStageLimitExceededException("source", 5)).Should().Be("SOURCE_TOO_LARGE");
    }

    private const long RealBytes3MbPerSec = 3_000_000;

    [Fact]
    public void A_short_source_keeps_the_configured_timeout_and_a_long_one_is_scaled()
    {
        SourceTimeoutPolicy.Seconds(900).Should().Be(900, "no scope: argv and behaviour unchanged");
        using (SourceTimeoutPolicy.Begin(60, 1920, 1080, 60 * RealBytes3MbPerSec))
            SourceTimeoutPolicy.Seconds(900).Should().Be(900);
        using (SourceTimeoutPolicy.Begin(3600, 1920, 1080, 3600 * RealBytes3MbPerSec))
            SourceTimeoutPolicy.Seconds(900).Should().Be(3600);
        using (SourceTimeoutPolicy.Begin(3600, 3840, 2160, 3600 * RealBytes3MbPerSec))
            SourceTimeoutPolicy.Seconds(900).Should().Be(14400, "4K is a ratio of 4");
        SourceTimeoutPolicy.Seconds(900).Should().Be(900, "the scope ends with its using");
    }

    [Fact]
    public void A_crafted_header_cannot_buy_time()
    {
        // A tiny file claiming a long 8K video: ratio clamps to 4, duration clamps to the limit, bytes cap the rest.
        using (SourceTimeoutPolicy.Begin(100_000, 15360, 8640, 3_000_000))
            SourceTimeoutPolicy.Seconds(900).Should().Be(910, "900 + 3 MB / 300 KB/s");
        using (SourceTimeoutPolicy.Begin(100_000, 15360, 8640, 100L * 1024 * 1024 * 1024))
            SourceTimeoutPolicy.Seconds(900).Should().Be(SourceTimeoutPolicy.MaxSeconds, "duration clamped to the limit, then the 6 h ceiling");
        using (SourceTimeoutPolicy.Begin(3600, 3840, 2160, 0))
            SourceTimeoutPolicy.Seconds(900).Should().Be(900, "unknown size: no scaling");
    }

    [Fact]
    public void A_configured_timeout_above_the_cap_is_never_shortened()
    {
        using (SourceTimeoutPolicy.Begin(3600, 3840, 2160, 100L * 1024 * 1024 * 1024))
            SourceTimeoutPolicy.Seconds(8 * 3600).Should().Be(8 * 3600);
    }

    [Fact]
    public async Task An_oversize_stored_source_is_refused_before_staging()
    {
        var guard = new ScratchDiskGuard(() => 100L * Gib, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5), limits: new VideoSourceLimits(10 * Gib, 3600));
        var files = new Moq.Mock<ReelBolt.WorkflowEngine.Services.Storage.IProjectFileWorkspace>();
        files.Setup(f => f.ListFilesAsync(Moq.It.IsAny<Guid>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync((System.Collections.Generic.IReadOnlyList<ReelBolt.WorkflowEngine.Services.Storage.ProjectWorkspaceFile>)new System.Collections.Generic.List<ReelBolt.WorkflowEngine.Services.Storage.ProjectWorkspaceFile>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), "a.mp4", null, "uploads", "k", "video/mp4", 11 * Gib, DateTime.UtcNow, null)
            });
        using var leases = new ScratchLeases();
        SourceTooLargeException ex = await Assert.ThrowsAsync<SourceTooLargeException>(
            () => leases.ReserveAsync(guard, files.Object, Guid.NewGuid(), "k", CancellationToken.None));
        FailureClassifier.CodeFromException(ex).Should().Be("SOURCE_TOO_LARGE");
    }

    [Fact]
    public async Task An_unreadable_volume_fails_closed_with_scratch_busy()
    {
        var guard = new ScratchDiskGuard(() => 0, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(5));
        await Assert.ThrowsAsync<ScratchBusyException>(() => guard.ReserveAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task The_guard_reserves_space_and_releases_it_on_dispose()
    {
        long free = 10 * Gib;
        var guard = new ScratchDiskGuard(() => free, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5));
        IDisposable first = await guard.ReserveAsync(4 * Gib, CancellationToken.None);
        guard.TryReserve(4 * Gib, out _).Should().BeFalse("the first lease still holds its space");
        first.Dispose();
        guard.TryReserve(4 * Gib, out IDisposable second).Should().BeTrue();
        second.Dispose();
    }

    [Fact]
    public async Task The_guard_waits_for_space_then_proceeds()
    {
        long free = 1 * Gib;
        var guard = new ScratchDiskGuard(() => Interlocked.Read(ref free), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10));
        Task<IDisposable> waiting = guard.ReserveAsync(Gib, CancellationToken.None);
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse();
        Interlocked.Exchange(ref free, 20 * Gib);
        (await waiting).Dispose();
    }

    [Fact]
    public async Task The_guard_fails_with_scratch_busy_when_space_never_comes()
    {
        var guard = new ScratchDiskGuard(() => 1 * Gib, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(10));
        ScratchBusyException ex = (await Assert.ThrowsAsync<ScratchBusyException>(() => guard.ReserveAsync(Gib, CancellationToken.None)));
        FailureClassifier.CodeFromException(ex).Should().Be("SCRATCH_BUSY");
        FailureClassifier.ForCode("SCRATCH_BUSY").Should().Be(FailureClass.PlatformInternal, "platform class means refunded");
    }

    [Fact]
    public async Task The_guard_wait_is_cancellable()
    {
        var guard = new ScratchDiskGuard(() => 0, TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource(40);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.ReserveAsync(Gib, cts.Token));
    }

    [Fact]
    public async Task A_process_killed_at_its_deadline_reports_timed_out_and_is_gone()
    {
        VideoEditingOptions options = new() { FfmpegPath = "sleep", FfprobePath = "sleep" };
        FfmpegVideoToolRunner runner = new(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegVideoToolRunner>.Instance);

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        VideoToolResult result = await runner.RunFfmpegAsync(["30"], TimeSpan.FromMilliseconds(300), CancellationToken.None);

        result.TimedOut.Should().BeTrue("the deadline killed it; this must never read as a plain exit -1");
        result.Succeeded.Should().BeFalse();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_timeout_that_is_not_ours_stays_a_cancellation()
    {
        VideoEditingOptions options = new() { FfmpegPath = "sleep" };
        FfmpegVideoToolRunner runner = new(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegVideoToolRunner>.Instance);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));

        Func<Task> run = () => runner.RunFfmpegAsync(["30"], TimeSpan.FromMinutes(5), cts.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void The_deadline_follows_frame_rate_and_bitrate_inside_every_bound()
    {
        const long Mbps = 1_000_000 / 8;
        // 249 s, 4K, 60 fps, 120 Mbps (the clip that failed): 249 x 4 x 2 x 3.64 x Factor.
        long bytes = 249 * 120 * Mbps;
        using (SourceTimeoutPolicy.Begin(249, 3840, 2160, bytes, fps: 60))
            SourceTimeoutPolicy.Seconds(900).Should().BeInRange(7000, 7400);

        // Same clip at 30 fps and an ordinary bitrate keeps the old arithmetic.
        using (SourceTimeoutPolicy.Begin(249, 3840, 2160, 249 * 24 * Mbps, fps: 30))
            SourceTimeoutPolicy.Seconds(900).Should().Be(996);

        // Headers cannot buy time: fps clamps at 4x, a tiny file claiming a long 4K 240 fps clip gets almost nothing.
        using (SourceTimeoutPolicy.Begin(3600, 3840, 2160, 3_000_000, fps: 100_000))
            SourceTimeoutPolicy.Seconds(900).Should().Be(910);
        using (SourceTimeoutPolicy.Begin(3600, 3840, 2160, 100L * 1024 * 1024 * 1024, fps: 240))
            SourceTimeoutPolicy.Seconds(900).Should().Be(SourceTimeoutPolicy.MaxSeconds);
        using (SourceTimeoutPolicy.Begin(3600, 3840, 2160, 3600 * 3_000_000L, fps: double.NaN))
            SourceTimeoutPolicy.Seconds(900).Should().Be(14400);
    }

    [Fact]
    public void A_too_demanding_source_is_refunded_with_a_plain_message()
    {
        FailureClassifier.ForCode(VideoSourceLimits.TooDemandingCode).Should().Be(FailureClass.PlatformInternal);
        CreditGate.IsRefundable(FailureClassifier.ForCode("SOURCE_TOO_DEMANDING")).Should().BeTrue("the hold is released and nothing is charged");
        FailureMessages.UserMessage("SOURCE_TOO_DEMANDING")
            .Should().Contain("not charged").And.NotContainAny("bytes", "Factor", "seconds", "Timeout");
    }

    [Fact]
    public void The_analysis_copy_uses_every_core_for_decode_and_encode()
    {
        string[] args = AnalysisProxy.Build("in.mp4", "out.mkv", 16, null);
        args.Select((a, i) => (a, i)).Where(t => t.a == "-threads").Select(t => args[t.i + 1]).Should().Equal("16", "16");
    }
}
