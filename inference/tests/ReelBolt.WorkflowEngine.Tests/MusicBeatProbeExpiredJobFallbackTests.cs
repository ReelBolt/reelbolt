using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Run 4f8aff78: the compile's CloudPool job went <c>Expired</c> (never claimed), the factory fell back to a
/// Local workspace, and the beat probe still ended as no_beat_grid. This drives the real factory over a scripted
/// gateway whose job is Expired, then measures the music on the workspace the compile would be handed.
/// </summary>
public class MusicBeatProbeExpiredJobFallbackTests
{
    private static readonly Guid Execution = Guid.Parse("e0000000-0000-4000-8000-000000000001");
    private static readonly Guid Org = Guid.Parse("0a000000-0000-4000-8000-000000000002");
    private static readonly Guid Step = Guid.Parse("5e000000-0000-4000-8000-000000000003");
    private static readonly Guid Project = Guid.Parse("90000000-0000-4000-8000-000000000004");
    private static readonly Guid Job = Guid.Parse("1b000000-0000-4000-8000-000000000005");

    private sealed class Fixed(ComputeDecision decision) : IComputeTarget
    {
        public ValueTask<ComputeDecision> ResolveAsync(ComputeTargetRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(decision);
    }

    [Theory]
    [InlineData("Expired", true)]
    [InlineData("Expired", false)]
    public async Task After_an_expired_job_falls_back_to_local_the_beat_grid_is_still_measured(string status, bool deleteAlsoExpired)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "mbp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            TestTimeProvider time = new(DateTimeOffset.UnixEpoch);
            StubHttpMessageHandler handler = new(request =>
            {
                if (request.Method == HttpMethod.Get)
                    time.Advance(TimeSpan.FromSeconds(30));
                string st = request.Method == HttpMethod.Delete && !deleteAlsoExpired ? "Cancelled" : status;
                return Task.FromResult(ComputeTestFactory.Json(ComputeTestFactory.JobBody(Job, status: st, sessionLost: st == "Expired")));
            });
            ComputeOptions compute = ComputeTestFactory.Options(jobWaitSeconds: 0);
            compute.PoolWaker.Enabled = true;
            FakeProjectFileWorkspace files = new();
            files.Objects["projects/p/userFiles/track.mp3"] = [1, 2, 3];
            MediaWorkspaceFactory factory = new(
                new Fixed(ComputeDecision.Runner(RunnerJobTarget.CloudPool, ComputeDecisionReasons.PoolWakeAndWait)),
                ComputeTestFactory.Gateway(handler, compute, new ExecutionSessionGate(Options.Create(compute))),
                new MediaWorkspaceFactoryTests.NoopVideoToolRunner(), files, new FakePresignedUrlService(),
                new FakeOutputVerifier(), new FakeRunnerOutputStore(),
                Options.Create(new VideoEditingOptions { ScratchPath = scratch, AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 }),
                Options.Create(new RemoteMediaOptions { AssignWaitSeconds = 300 }),
                NullLogger<MediaWorkspaceFactory>.Instance, sandboxSessions: null,
                computeOptions: Options.Create(compute), time: time);

            MediaWorkspaceOpenResult opened = await factory.OpenAsync(new MediaWorkspaceRequest(
                Execution, Org, Project, Step, 3, RunnerJobType.VideoCompile, "video.compile:3"));
            opened.Refused.Should().BeFalse();
            await using MediaWorkspaceLease lease = opened.Lease!;
            lease.Workspace.Should().BeOfType<LocalMediaWorkspace>("the expired job falls back to a Local workspace, not a dead remote one");

            var extractor = new Mock<IAudioExtractor>();
            extractor
                .Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<IMediaWorkspace, string, string, CancellationToken>((ws, _, output, ct) =>
                    ws.WriteBytesAsync(output, BeatSyncTests.Wav(MusicBeatAnalyzerTests.ClickTrack(140, 0.1, 40)), ct));
            ProjectWorkspaceFile file = new(
                Guid.NewGuid(), Project, "track.mp3", null, "userFiles", "projects/p/userFiles/track.mp3", "audio/mpeg", 3, DateTime.UnixEpoch, null);

            MusicBeatMeasurement m = await MusicBeatProbe.TryMeasureAsync(
                lease.Workspace, extractor.Object, file, "beat-sync-music", NullLogger.Instance, CancellationToken.None);

            m.Reason.Should().BeNull("the fallback workspace is an ordinary local one; got " + m.Reason);
            m.Analysis!.Bpm.Should().BeApproximately(140, 1.5);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
        }
    }
}
