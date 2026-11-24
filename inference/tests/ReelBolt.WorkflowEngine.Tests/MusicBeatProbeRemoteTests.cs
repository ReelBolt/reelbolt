using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Tests.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The beat grid on a REMOTE media session (compile on the compute pool). Every test drives the real
/// <see cref="RemoteMediaWorkspace"/> over a scripted gateway, so the read cap, the stage cap and the
/// op refusals a pool runner answers with are the real ones; only the runner itself is scripted.
/// </summary>
public class MusicBeatProbeRemoteTests
{
    private static readonly Guid Project = MediaSessionTestData.Project;
    private static readonly Guid Job = MediaSessionTestData.Job;
    private static readonly Guid Session = MediaSessionTestData.Session;

    private static ProjectWorkspaceFile Music() => new(
        Guid.Parse("a0000000-0000-4000-8000-000000000001"), Project, "track.mp3", null, "userFiles",
        "projects/p/userFiles/track.mp3", "audio/mpeg", 4096, DateTime.UnixEpoch, null);

    /// <summary>16 kHz mono s16 WAV of a 128 BPM click track: 32,000 bytes per second.</summary>
    private static byte[] Wav(double seconds) =>
        BeatSyncTests.Wav(MusicBeatAnalyzerTests.ClickTrack(128, 0.1, seconds));

    private static IAudioExtractor Extractor()
    {
        // The real extractor only materializes two names and runs one ffmpeg op; the scripted runner answers it.
        var extractor = new Mock<IAudioExtractor>();
        extractor
            .Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, input, output, ct) =>
            {
                VideoToolResult r = await ws.RunFfmpegAsync(["-i", input, "-vn", output], TimeSpan.FromSeconds(30), ct);
                if (!r.Succeeded)
                    throw new InvalidOperationException("ffmpeg audio extraction failed (exitCode=" + r.ExitCode + ").");
            });
        return extractor.Object;
    }

    private sealed record Rig(RemoteMediaWorkspace Workspace, ScriptedSessionGateway Gateway, FakeRunnerOutputStore Store);

    /// <summary>A runner whose WAV is <paramref name="wav"/>: stage and ffmpeg succeed, an inline read honours the caller's cap exactly as the worker does.</summary>
    private static Rig Remote(byte[] wav, string? fetchUrl = null, Action<ScriptedSessionGateway>? tweak = null)
    {
        ScriptedSessionGateway gateway = new();
        gateway.On(MediaSessionOps.Stage, """{"bytes":4096,"sha256":"ab"}""");
        gateway.On(MediaSessionOps.Ffmpeg, """{"exitCode":0,"stderrTail":"","durationMs":5}""");
        gateway.On(MediaSessionOps.Stat, $$"""{"exists":true,"bytes":{{wav.Length}}}""");
        gateway.On(MediaSessionOps.Read, args => args["maxBytes"]!.GetValue<int>() < wav.Length
            ? ComputeTestFactory.Json(MediaSessionTestData.OpBody(null, ok: false, code: "file_too_large", message: "over the cap"))
            : ComputeTestFactory.Json(MediaSessionTestData.OpBody(
                new JsonObject { ["content"] = Convert.ToBase64String(wav), ["encoding"] = "base64" }.ToJsonString())));
        gateway.On(MediaSessionOps.Publish, $$"""{"bytes":{{wav.Length}},"sha256":"cd"}""");
        tweak?.Invoke(gateway);

        FakeRunnerOutputStore store = new() { GetUrl = fetchUrl };
        store.Sizes[S3RunnerOutputStore.StagingKeyFor(Job, 1)] = wav.Length;
        RemoteMediaWorkspace workspace = new(
            Project, Job, Session, RunnerJobTarget.CloudPool, ComputeTestFactory.Gateway(gateway.Handler),
            new FakeProjectFileWorkspace(), new FakePresignedUrlService(), new FakeOutputVerifier(), store,
            new RemoteMediaOptions { MaxStageBytes = 1L << 30, MaxPublishBytes = 1L << 30 }, NullLogger.Instance);
        return new Rig(workspace, gateway, store);
    }

    [Fact]
    public void A_two_minute_track_fits_the_inline_read_but_a_two_and_a_half_minute_one_does_not()
    {
        // The evidence for the byte arithmetic: 16 kHz * 2 bytes = 32,000 B/s, so run 4f8aff78's
        // 120.7 s track is 3.86 MB (under the 4 MiB cap) and the cap bites from 131 s on.
        Wav(120.7).Length.Should().BeLessThan(MediaSessionOps.MaxInlineReadBytes);
        Wav(135).Length.Should().BeGreaterThan(MediaSessionOps.MaxInlineReadBytes);
    }

    [Fact]
    public async Task A_track_longer_than_the_inline_read_cap_still_measures_on_a_remote_workspace()
    {
        byte[] wav = Wav(135);
        using LoopbackObjectServer server = new(wav);
        Rig rig = Remote(wav, server.Url);

        MusicBeatAnalysis? measured = await MusicBeatProbe.MeasureAsync(
            rig.Workspace, Extractor(), Music(), "beat-sync-music", CancellationToken.None);

        measured.Should().NotBeNull("a long track must not lose its grid to the 4 MiB inline read cap");
        measured!.Bpm.Should().BeApproximately(128, 1.5);
    }

    [Fact]
    public async Task A_refused_stage_is_a_named_failure_that_is_logged_not_a_plain_no_beat_grid()
    {
        Rig rig = Remote(Wav(30), tweak: g => g.OnOpError(MediaSessionOps.Stage, "stage_failed", "download refused", retryable: false));
        CapturingLogger log = new();

        MusicBeatMeasurement m = await MusicBeatProbe.TryMeasureAsync(
            rig.Workspace, Extractor(), Music(), "beat-sync-music", log, CancellationToken.None);

        m.Analysis.Should().BeNull();
        m.Reason.Should().StartWith("beat_grid_failed:").And.NotBe("no_beat_grid");
        log.Entries.Should().ContainSingle(e => e.Contains("RunnerSessionOpException") && e.Contains("download refused"));
    }

    [Fact]
    public async Task A_refused_ffmpeg_argv_is_a_named_failure()
    {
        Rig rig = Remote(Wav(30), tweak: g => g.OnOpError(MediaSessionOps.Ffmpeg, "argv_not_allowed", "flag refused"));

        MusicBeatMeasurement m = await MusicBeatProbe.TryMeasureAsync(
            rig.Workspace, Extractor(), Music(), "beat-sync-music", NullLogger.Instance, CancellationToken.None);

        m.Analysis.Should().BeNull();
        m.FailureCause.Should().NotBeNullOrEmpty();
        m.Reason.Should().StartWith("beat_grid_failed:");
    }

    [Fact]
    public async Task A_track_with_no_beat_stays_the_plain_no_beat_grid()
    {
        byte[] silence = BeatSyncTests.Wav(new float[16000 * 20]);
        Rig rig = Remote(silence);

        MusicBeatMeasurement m = await MusicBeatProbe.TryMeasureAsync(
            rig.Workspace, Extractor(), Music(), "beat-sync-music", NullLogger.Instance, CancellationToken.None);

        m.Analysis.Should().BeNull();
        m.FailureCause.Should().BeNull();
        m.Reason.Should().Be("no_beat_grid");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception) + " | " + exception);
    }

    /// <summary>Serves one body on a loopback port, standing in for the presigned object-storage GET.</summary>
    private sealed class LoopbackObjectServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public LoopbackObjectServer(byte[] body)
        {
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            Url = $"http://127.0.0.1:{port}/staging";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext c = await _listener.GetContextAsync();
                        c.Response.ContentLength64 = body.Length;
                        await c.Response.OutputStream.WriteAsync(body);
                        c.Response.Close();
                    }
                }
                catch (Exception) { /* listener stopped */ }
            });
        }

        public string Url { get; }

        public void Dispose() => _listener.Close();
    }
}
