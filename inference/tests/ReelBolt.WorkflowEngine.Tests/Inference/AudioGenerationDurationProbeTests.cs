#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.Inference;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// No audio endpoint returns a duration, so the engine probes the downloaded file before a take is metered.
/// </summary>
public class AudioGenerationDurationProbeTests
{
    private sealed class FakeProbe(double duration) : IMediaProbe
    {
        public List<(string Path, byte[] Bytes)> Seen { get; } = [];

        public Task<MediaProbeResult> ProbeAsync(IMediaWorkspace workspace, string virtualPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MediaProbeResult> ProbeAsync(string localFilePath, CancellationToken ct)
        {
            Seen.Add((localFilePath, File.ReadAllBytes(localFilePath)));
            return Task.FromResult(new MediaProbeResult(duration, 0, 1, 0, 0, null, "mp3", 44100));
        }
    }

    private static AudioGenerationResult Result(double? duration = null) =>
        new(AudioFixtures.Silence, "audio/mpeg", ".mp3", duration, "req-1");

    [Fact]
    public async Task A_result_without_a_duration_is_probed_from_a_temporary_file_that_is_then_removed()
    {
        FakeProbe probe = new(2.0);

        AudioGenerationResult probed = await new AudioGenerationDurationProbe(probe).EnsureDurationAsync(Result(), CancellationToken.None);

        probed.DurationSec.Should().Be(2.0);
        probed.Bytes.Should().Equal(AudioFixtures.Silence);
        (string path, byte[] bytes) = probe.Seen.Should().ContainSingle().Which;
        Path.GetExtension(path).Should().Be(".mp3");
        bytes.Should().Equal(AudioFixtures.Silence);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task A_vendor_stated_duration_is_kept_and_nothing_is_probed()
    {
        FakeProbe probe = new(99);

        AudioGenerationResult kept = await new AudioGenerationDurationProbe(probe).EnsureDurationAsync(Result(12.5), CancellationToken.None);

        kept.DurationSec.Should().Be(12.5);
        probe.Seen.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public async Task An_unreadable_duration_fails_rather_than_metering_zero(double reading)
    {
        Func<Task> act = () => new AudioGenerationDurationProbe(new FakeProbe(reading)).EnsureDurationAsync(Result(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioGenerationException>()).Which.Kind.Should().Be(AudioGenerationErrorKind.VendorFailed);
    }
}
