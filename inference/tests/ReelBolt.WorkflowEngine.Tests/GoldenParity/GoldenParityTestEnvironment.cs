#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Serializes every golden-parity test class into one collection.
/// </summary>
/// <remarks>
/// xUnit runs collections in parallel by default. These tests each spawn real encoder processes, and
/// the whole point of the harness is a controlled measurement — parallel encodes on a shared host
/// would turn encode contention into a variable in the numbers. One collection, one harness run.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class GoldenParityCollection : ICollectionFixture<GoldenParityTestEnvironment>
{
    /// <summary>The collection's name, used by <c>[Collection(...)]</c> on each test class.</summary>
    public const string Name = "GoldenParity";
}

/// <summary>
/// The shared environment for the golden-parity tests: resolves the real binaries, builds the
/// production <see cref="IVideoToolRunner"/>, and runs the harness exactly once for the whole
/// collection.
/// </summary>
/// <remarks>
/// <para>
/// <b>A missing ffmpeg fails the whole collection, loudly.</b> <see cref="InitializeAsync"/> throws
/// <see cref="GoldenParityToolingUnavailableException"/> when the binaries cannot be found or cannot
/// be executed, which xUnit surfaces as a failure on every test in the collection. That is
/// deliberate: the alternative — a skip — would make an unmeasured run indistinguishable from a
/// measured one.
/// </para>
/// <para>
/// The harness runs in the fixture rather than in one test so that the comparison tooling's focused
/// tests can reuse the same already-rendered outputs instead of paying for another full run.
/// </para>
/// </remarks>
public sealed class GoldenParityTestEnvironment : IAsyncLifetime
{
    private readonly string _workingDirectory;

    public GoldenParityTestEnvironment()
    {
        // Every test in this collection is an [FfmpegToolsFact], so on a host with no ffmpeg (the CI
        // runner) they are all SKIPPED — visibly, never green. The fixture must then construct
        // without resolving the binaries, or xUnit would report every skipped test as failed with
        // this constructor's exception. Where the binaries exist, resolution stays loud.
        ToolsAvailable = FfmpegToolsOnPath.MissingBinary() is null;
        FfmpegPath = ToolsAvailable
            ? GoldenParityTooling.ResolveOnPath(GoldenParityTooling.FfmpegExecutable)
            : GoldenParityTooling.FfmpegExecutable;
        FfprobePath = ToolsAvailable
            ? GoldenParityTooling.ResolveOnPath(GoldenParityTooling.FfprobeExecutable)
            : GoldenParityTooling.FfprobeExecutable;

        Runner = GoldenParityTooling.CreateRunner(FfmpegPath, FfprobePath);

        _workingDirectory = Path.Combine(AppContext.BaseDirectory, "golden-parity-work");
        ManifestPath = GoldenParityTooling.ResolveFixtureFile("timeline-manifest.json");
        MeasurementsPath = Path.Combine(AppContext.BaseDirectory, GoldenParityHarness.MeasurementsFileName);

        ToolTimeout = TimeSpan.FromMinutes(2);
    }

    /// <summary>
    /// Whether <c>ffmpeg</c> and <c>ffprobe</c> were found on PATH. False means no harness run
    /// happened and <see cref="Verdict"/> is null; every consumer is skipped in that case.
    /// </summary>
    public bool ToolsAvailable { get; }

    /// <summary>Absolute path of the resolved <c>ffmpeg</c>.</summary>
    public string FfmpegPath { get; }

    /// <summary>Absolute path of the resolved <c>ffprobe</c>.</summary>
    public string FfprobePath { get; }

    /// <summary>The single production ffmpeg seam every invocation in this collection goes through.</summary>
    public IVideoToolRunner Runner { get; }

    /// <summary>The committed v1 compile-result manifest the seeder consumes.</summary>
    public string ManifestPath { get; }

    /// <summary>Where the harness writes <c>golden-parity-measurements.json</c>.</summary>
    public string MeasurementsPath { get; }

    /// <summary>Per-invocation wall-clock ceiling.</summary>
    public TimeSpan ToolTimeout { get; }

    /// <summary>Scratch directory holding fixture media, renders and per-pair extractions.</summary>
    public string WorkingDirectory => _workingDirectory;

    /// <summary>The verdict of the single harness run this collection shares.</summary>
    public GoldenParityVerdict Verdict { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (!ToolsAvailable)
        {
            return; // nothing to render; every test in the collection is skipped
        }

        Verdict = await GoldenParityHarness.RunAsync(BuildRequest(Runner, _workingDirectory, MeasurementsPath));
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Builds a harness request, used both by the fixture and by the missing-ffmpeg test.
    /// </summary>
    /// <param name="runner">The runner every invocation goes through.</param>
    /// <param name="workingDirectory">Scratch directory.</param>
    /// <param name="measurementsPath">Where the measurements artifact goes.</param>
    public GoldenParityRequest BuildRequest(IVideoToolRunner runner, string workingDirectory, string measurementsPath) =>
        new(
            WorkingDirectory: workingDirectory,
            ManifestPath: ManifestPath,
            MeasurementsPath: measurementsPath,
            Runner: runner,
            // P4-Q1: the real renderer leg. TimelineRenderSeamAdapter drives the production
            // TimelineRenderer (Services/Video/TimelineRender) over the harness's own asset resolver
            // with NO region cache, so P1's "byte-identical argv across two runs" stays meaningful.
            TimelineRenderPath: new TimelineRenderSeamAdapter(
                runner,
                AssetResolverFor(workingDirectory),
                Path.Combine(workingDirectory, "timeline-render-scratch"),
                ToolTimeout),
            VideoCompilePath: new SpikeTimelineRenderSeam(
                runner,
                AssetResolverFor(workingDirectory),
                SpikeSeamPath.SegmentedConcat,
                ToolTimeout),
            AudioDivergenceProbe: new SpikeTimelineRenderSeam(
                runner,
                AssetResolverFor(workingDirectory),
                SpikeSeamPath.SingleFilterGraph,
                ToolTimeout,
                audioGainOverrideDb: ProbeBedGainDb),
            ToolTimeout: ToolTimeout);

    /// <summary>
    /// The bed gain the audio-sensitivity probe renders at. The fixture manifest sets <c>-6</c> dB,
    /// so this is a <b>6 dB</b> downward divergence — comfortably inside the range of a real mix
    /// mistake (a mis-set bed level, a missing music track) and far outside the band two correct
    /// renders occupy.
    /// </summary>
    private const double ProbeBedGainDb = -12.0;

    /// <summary>
    /// Maps a document <see cref="AssetRef"/> back to the generated fixture file it stands for —
    /// the harness's own resolver, mirroring what a renderer's storage lookup would do.
    /// </summary>
    private static Func<AssetRef, string> AssetResolverFor(string workingDirectory) =>
        assetRef => GoldenParityAssets.ResolvePath(
            assetRef,
            GoldenParityHarness.FixtureDirectoryFor(workingDirectory));
}
