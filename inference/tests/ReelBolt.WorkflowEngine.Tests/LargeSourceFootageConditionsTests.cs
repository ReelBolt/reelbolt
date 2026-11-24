using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Initiative large-source-footage, security and QA conditions: timeout ceiling, HDR capability cache, derivative order.</summary>
public sealed class LargeSourceFootageConditionsTests
{
    [Theory]
    [InlineData(21600, 2160)]
    [InlineData(21600, 1080)]
    [InlineData(1800, 2160)]
    public void The_compile_timeout_never_exceeds_the_six_hour_ceiling_after_the_pixel_factor(int configured, int resolution)
    {
        VideoCompileStepExecutor.CompileTimeout(configured, resolution).TotalSeconds
            .Should().BeLessThanOrEqualTo(SourceTimeoutPolicy.MaxSeconds);
        VideoCompileStepExecutor.CompileTimeout(1800, 2160).TotalSeconds.Should().BeGreaterThan(1800, "the factor still applies below the ceiling");
    }

    [Fact]
    public async Task A_cancelled_first_hdr_probe_is_not_remembered()
    {
        var capability = new FfmpegHdrToneMapCapability();
        using var cts = new CancellationTokenSource();
        var runner = new Mock<IVideoToolRunner>();
        runner.SetupSequence(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, cts.Token);
                return null!;
            })
            .ReturnsAsync(new VideoToolResult(0, " ... zscale V->V\n ... tonemap V->V\n", "", false));

        Func<Task> first = () => capability.IsAvailableAsync(runner.Object, cts.Token);
        await first.Should().ThrowAsync<OperationCanceledException>();

        (await capability.IsAvailableAsync(runner.Object, CancellationToken.None)).Should().BeTrue("the next call probes again instead of rethrowing");
        (await capability.IsAvailableAsync(runner.Object, CancellationToken.None)).Should().BeTrue();
        runner.Verify(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void The_seconds_long_derivatives_run_before_the_proxy_so_the_probe_is_stored_early()
    {
        var plan = MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", kinds: null);
        int proxy = plan.ToList().FindIndex(p => p.Slug == MediaDerivativeKindSlug.Proxy);
        plan.Take(proxy).Select(p => p.Slug).Should().Contain(MediaDerivativeKindSlug.WaveformPeaks).And.Contain(MediaDerivativeKindSlug.KeyframeIndex);
        plan.Skip(proxy + 1).Select(p => p.Slug).Should().OnlyContain(s => s == MediaDerivativeKindSlug.Filmstrip);
    }
}
