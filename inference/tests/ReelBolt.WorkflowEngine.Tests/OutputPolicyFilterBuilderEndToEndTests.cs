using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The B8b acceptance check, run against REAL ffmpeg and REAL ffprobe — no mocks: the filter chain
/// is produced by the production <see cref="OutputPolicyFilterBuilder"/>, handed to the real binary
/// as the -filter_complex argument, and the resulting file is probed.
///
/// <para>
/// The plan's criterion is "an end-to-end check: ffprobe of a trial output shows height &lt;= 720",
/// and it cannot be satisfied by asserting on strings: scale=-2:min(ih,720) is only correct if
/// ffmpeg agrees it is. So everything here runs a real encode over a real synthesized testsrc
/// source.
/// </para>
///
/// <para>
/// <b>Skips cleanly when the binaries are absent</b> (see <see cref="RealFfmpegFactAttribute"/>) so
/// a CI image without ffmpeg stays green — and, unlike a bare early return, reports as SKIPPED
/// rather than PASSED so it can never quietly assert nothing.
/// </para>
/// </summary>
public class OutputPolicyFilterBuilderEndToEndTests
{
    private readonly ITestOutputHelper _output;

    public OutputPolicyFilterBuilderEndToEndTests(ITestOutputHelper output) => _output = output;

    // ── the source pictures ──────────────────────────────────────────────

    private const int SourceWidth = 1920;
    private const int SourceHeight = 1080;
    private const int SmallWidth = 640;
    private const int SmallHeight = 360;

    /// <summary>Every synthesized source is one second at 30 fps: long enough for a real encode, short enough to be instant.</summary>
    private const string TestSourceDuration = "1";
    private const int TestSourceFps = 30;

    [RealFfmpegFact]
    public void A_1080p_source_is_capped_to_the_policy_height_with_its_aspect_ratio_preserved()
    {
        using Scratch scratch = new();

        string source = scratch.Path("src.mp4");
        Ffmpeg.Synthesize(source, SourceWidth, SourceHeight);
        Ffmpeg.ProbeSize(source).Should().Be((SourceWidth, SourceHeight),
            "the fixture is only meaningful if the source really is larger than the cap");

        // The policy as the executor/tool would resolve it for a trial plan: a 720p cap, no mark.
        string chain = OutputPolicyFilterBuilder.BuildFilterChain(
            "[0:v]", SourceWidth, SourceHeight, maxHeight: OutputPolicyFilterBuilder.DefaultMaxHeight);

        string output = scratch.Path("capped.mp4");
        Ffmpeg.EncodePolicyChain(source, output, chain);

        (int width, int height) = Ffmpeg.ProbeSize(output);
        _output.WriteLine($"source       : ffprobe -> {SourceWidth}x{SourceHeight}");
        _output.WriteLine($"filter chain : {chain}");
        _output.WriteLine($"trial output : ffprobe -> {width}x{height}");

        height.Should().BeLessThanOrEqualTo(720, "a trial output must never exceed the policy's 720p cap");
        height.Should().Be(OutputPolicyFilterBuilder.DefaultMaxHeight, "the cap scales DOWN to exactly the cap");

        // No distortion and no letterboxing: scale=-2 recomputes the width, so the frame is still
        // 16:9 — 1920x1080 -> 1280x720.
        width.Should().Be(OutputPolicyFilterBuilder.OutputWidth(
            SourceWidth, SourceHeight, OutputPolicyFilterBuilder.DefaultMaxHeight));
        double sourceAspect = (double)SourceWidth / SourceHeight;
        double outputAspect = (double)width / height;
        outputAspect.Should().BeApproximately(sourceAspect, 0.005,
            "the cap must preserve the source aspect ratio, never squash the picture into the cap");
    }

    [RealFfmpegFact]
    public void A_source_at_or_below_the_cap_is_not_upscaled_and_emits_no_filter_stage()
    {
        using Scratch scratch = new();

        string source = scratch.Path("small.mp4");
        Ffmpeg.Synthesize(source, SmallWidth, SmallHeight);

        string chain = OutputPolicyFilterBuilder.BuildFilterChain(
            "[0:v]", SmallWidth, SmallHeight, maxHeight: OutputPolicyFilterBuilder.DefaultMaxHeight);

        // The builder's contract: a frame already under the cap and no mark means NOTHING to emit —
        // it hands the caller's own label straight back. That is what makes the post-pass a no-op
        // rather than a needless second encode (see the Remotion tool's TryApplyOutputPolicyAsync).
        chain.Should().Be("[0:v]", "no cap is needed, so the builder must not emit a scale stage at all");

        // ...and the bare label really is a pass-through when ffmpeg is handed it as a filtergraph:
        // "the builder emitted nothing" is spelled as a null filter here, rather than by inventing a
        // stage the production code would never emit.
        string output = scratch.Path("small-out.mp4");
        Ffmpeg.EncodePolicyChain(source, output, "[0:v]null[vout]");

        (int smallWidth, int smallHeight) = Ffmpeg.ProbeSize(output);
        _output.WriteLine($"source  : ffprobe -> {SmallWidth}x{SmallHeight}");
        _output.WriteLine($"builder : {chain}  (no scale stage emitted at all)");
        _output.WriteLine($"output  : ffprobe -> {smallWidth}x{smallHeight}");

        (smallWidth, smallHeight).Should().Be((SmallWidth, SmallHeight),
            "an output already inside the cap must never be upscaled");
    }

    [RealFfmpegFact]
    public void An_unentitled_policy_emits_no_filter_stage_for_a_1080p_source_either()
    {
        // The other half of the backward-compatibility claim: with no plan, ResolvedOutputPolicy.None
        // carries UnlimitedInt, and the builder's own no-op check must fire — so a self-host run's
        // picture pipeline is unchanged, not merely "visually similar".
        ResolvedOutputPolicy.None.ApplyWatermark.Should().BeFalse();

        string chain = OutputPolicyFilterBuilder.BuildFilterChain(
            "[0:v]", SourceWidth, SourceHeight, maxHeight: ResolvedOutputPolicy.None.MaxHeight);

        chain.Should().Be("[0:v]");
    }

    [RealFfmpegWatermarkFact]
    public void An_armed_watermark_is_really_composited_into_the_documented_corner_region()
    {
        using Scratch scratch = new();

        string watermarkAsset = Ffmpeg.ResolveWatermarkAsset()!;
        File.Exists(watermarkAsset).Should().BeTrue(
            "the B8b mark is vendored at inference/assets/watermark.png");

        string source = scratch.Path("src.mp4");
        Ffmpeg.Synthesize(source, SourceWidth, SourceHeight);

        int maxHeight = OutputPolicyFilterBuilder.DefaultMaxHeight;

        // Unmarked: the cap alone.
        string unmarked = scratch.Path("unmarked.mp4");
        Ffmpeg.EncodePolicyChain(
            source,
            unmarked,
            OutputPolicyFilterBuilder.BuildFilterChain("[0:v]", SourceWidth, SourceHeight, maxHeight: maxHeight));

        // Marked: the SAME cap, plus the mark. The asset is the caller's second -i input, which is
        // exactly how the compile step and the Remotion post-pass append it.
        string marked = scratch.Path("marked.mp4");
        Ffmpeg.EncodePolicyChain(
            source,
            marked,
            OutputPolicyFilterBuilder.BuildFilterChain(
                "[0:v]", SourceWidth, SourceHeight, maxHeight: maxHeight, applyWatermark: true,
                inputIndexForIndex: _ => 1),
            extraInput: watermarkAsset);

        // Both pictures are capped the same way, so the two files are the same size...
        Ffmpeg.ProbeSize(marked).Should().Be(Ffmpeg.ProbeSize(unmarked));

        string markedFrame = scratch.Path("marked.png");
        string unmarkedFrame = scratch.Path("unmarked.png");
        Ffmpeg.ExtractFirstFrame(marked, markedFrame);
        Ffmpeg.ExtractFirstFrame(unmarked, unmarkedFrame);

        int outputWidth = OutputPolicyFilterBuilder.OutputWidth(SourceWidth, SourceHeight, maxHeight);
        int outputHeight = maxHeight;

        // The exact rectangle the mark is pinned to, DERIVED from the production constants rather
        // than guessed: watermarkWidthPct of the output width, its near corner 24px from the
        // frame's bottom-right corner.
        int markSize = OutputPolicyFilterBuilder.WatermarkWidth(
            outputWidth, OutputPolicyFilterBuilder.DefaultWatermarkWidthPct);
        int markX = outputWidth - OutputPolicyFilterBuilder.DefaultWatermarkMargin - markSize;
        int markY = outputHeight - OutputPolicyFilterBuilder.DefaultWatermarkMargin - markSize;

        double wholeFrame = Ffmpeg.PsnrAverage(markedFrame, unmarkedFrame, outputWidth, outputHeight, 0, 0);
        _output.WriteLine($"watermark asset : {watermarkAsset}");
        _output.WriteLine($"output          : ffprobe -> {outputWidth}x{outputHeight} (both files)");
        _output.WriteLine($"mark box        : {markSize}x{markSize} at ({markX},{markY})");
        _output.WriteLine($"psnr whole frame: {wholeFrame} dB");
        wholeFrame.Should().BeLessThan(60,
            "the marked render must differ from the unmarked one — a white mark cannot leave the frame identical");

        double markRegion = Ffmpeg.PsnrAverage(markedFrame, unmarkedFrame, markSize, markSize, markX, markY);
        _output.WriteLine($"psnr mark box   : {markRegion} dB");
        markRegion.Should().BeLessThan(40,
            $"the mark's own corner region ({markSize}x{markSize} at {markX},{markY}, bottom-right) must have changed");

        // ...and the rest of the frame must not have been touched: the mark is composited, not a
        // global grade. Compared against a same-sized control box in the opposite corner.
        double controlRegion = Ffmpeg.PsnrAverage(markedFrame, unmarkedFrame, markSize, markSize, 0, 0);
        _output.WriteLine($"psnr control box: {controlRegion} dB (top-left, {markSize}x{markSize} at 0,0)");
        controlRegion.Should().BeGreaterThan(markRegion,
            "the unchanged top-left corner must be closer to the original than the corner the mark was drawn in");
        controlRegion.Should().BeGreaterThan(40,
            "the mark is painted in one corner, not across the whole frame");
    }

    // ── the fixture plumbing ─────────────────────────────────────────────

    /// <summary>A per-test temp directory, deleted on dispose.</summary>
    internal sealed class Scratch : IDisposable
    {
        private readonly string _directory =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reelbolt-b8b-e2e-" + Guid.NewGuid().ToString("N"));

        public Scratch() => Directory.CreateDirectory(_directory);

        public string Path(string fileName) => System.IO.Path.Combine(_directory, fileName);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    /// <summary>
    /// The real ffmpeg/ffprobe invocations. Deliberately raw <see cref="ProcessStartInfo"/> with
    /// ArgumentList (never a joined command line) — the same rule <see cref="IVideoToolRunner"/>
    /// holds production callers to, and the reason a filter string carrying quotes and commas
    /// survives intact.
    /// </summary>
    internal static class Ffmpeg
    {
        private static readonly string? FfmpegPath = ResolveBinary("ffmpeg");
        private static readonly string? FfprobePath = ResolveBinary("ffprobe");

        /// <summary>The absolute ffmpeg binary, for seeding VideoEditingOptions in the harness below.</summary>
        public static string Executable => FfmpegPath!;

        /// <summary>The absolute ffprobe binary.</summary>
        public static string Prober => FfprobePath!;

        public static string? MissingBinaryMessage =>
            FfmpegPath is null ? "ffmpeg is not on PATH" : FfprobePath is null ? "ffprobe is not on PATH" : null;

        /// <summary>
        /// The vendored mark, located by walking up from the test output directory to the repository
        /// root. The test project does not copy it into bin/ — the ENGINE image does the copying, from
        /// exactly this path (see the WorkflowEngine Dockerfile and
        /// VideoEditingOptions.WatermarkAssetPath).
        /// </summary>
        public static string? ResolveWatermarkAsset()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                string candidate = System.IO.Path.Combine(directory.FullName, "inference", "assets", "watermark.png");
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }

            return null;
        }

        /// <summary>Synthesizes a deterministic testsrc source of the given size.</summary>
        public static void Synthesize(string outputPath, int width, int height)
        {
            ToolResult result = Run(FfmpegPath!, "-v", "error", "-y",
                "-f", "lavfi",
                "-i", $"testsrc=size={width}x{height}:duration={TestSourceDuration}:rate={TestSourceFps}",
                "-pix_fmt", "yuv420p",
                outputPath);
            result.ExitCode.Should().Be(0, $"synthesizing the test source must work. ffmpeg said: {result.StdErr}");
        }

        /// <summary>
        /// Encodes <paramref name="inputPath"/> through the PRODUCTION filter chain, with the same
        /// encoder settings the platform's own re-encode paths use (VideoCompileStepConfig's
        /// defaults). <paramref name="extraInput"/> is the watermark raster, added as the second
        /// input — the index the chain's own callback resolved.
        /// </summary>
        public static void EncodePolicyChain(string inputPath, string outputPath, string chain, string? extraInput = null)
        {
            List<string> args = ["-v", "error", "-y", "-i", inputPath];
            if (extraInput is not null)
            {
                args.Add("-i");
                args.Add(extraInput);
            }

            args.AddRange(["-filter_complex", chain, "-map", "[vout]", "-map", "0:a?"]);
            args.AddRange(["-c:v", "libx264", "-crf", "20", "-preset", "veryfast"]);
            args.AddRange(["-pix_fmt", "yuv420p", "-color_range", "tv", "-movflags", "+faststart"]);
            args.Add(outputPath);

            ToolResult result = Run(FfmpegPath!, [.. args]);
            result.ExitCode.Should().Be(0, $"ffmpeg must accept the production filter chain. It said: {result.StdErr}");
        }

        /// <summary>ffprobe's width,height for the first video stream.</summary>
        public static (int Width, int Height) ProbeSize(string inputPath)
        {
            ToolResult result = Run(FfprobePath!, "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=width,height",
                "-of", "csv=p=0",
                inputPath);
            result.ExitCode.Should().Be(0, $"ffprobe must be able to read {inputPath}. It said: {result.StdErr}");

            string[] parts = result.StdOut.Trim().Split(',');
            parts.Should().HaveCount(2, $"ffprobe returned '{result.StdOut}'");
            return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        /// <summary>Writes the file's first frame to a lossless PNG, so a pixel comparison is not fighting an encoder.</summary>
        public static void ExtractFirstFrame(string inputPath, string outputPath)
        {
            ToolResult result = Run(FfmpegPath!, "-v", "error", "-y", "-i", inputPath,
                "-frames:v", "1", "-update", "1", outputPath);
            result.ExitCode.Should().Be(0, $"extracting a frame must succeed. It said: {result.StdErr}");
        }

        /// <summary>
        /// The PSNR average the psnr filter reports for the two images' rectangle at (x,y) sized
        /// width x height — <see cref="double.PositiveInfinity"/> when the rectangles are
        /// pixel-identical, and a finite dB figure (lower = more different) otherwise.
        /// </summary>
        public static double PsnrAverage(string aPath, string bPath, int width, int height, int x, int y)
        {
            string graph =
                $"[0:v]crop={width}:{height}:{x}:{y}[a];[1:v]crop={width}:{height}:{x}:{y}[b];[a][b]psnr";

            ToolResult result = Run(FfmpegPath!, "-hide_banner", "-i", aPath, "-i", bPath,
                "-lavfi", graph, "-f", "null", "-");
            result.ExitCode.Should().Be(0, $"the psnr comparison must run. It said: {result.StdErr}");

            // The summary line ends "... average:26.497726 min:... max:..."; "inf" over identical pixels.
            int index = result.StdErr.LastIndexOf("average:", StringComparison.Ordinal);
            index.Should().BeGreaterThan(-1, $"ffmpeg must report a PSNR average. It said: {result.StdErr}");

            string value = result.StdErr[(index + "average:".Length)..];
            int end = value.IndexOf(' ');
            if (end >= 0)
                value = value[..end];

            return value.StartsWith("inf", StringComparison.OrdinalIgnoreCase)
                ? double.PositiveInfinity
                : double.Parse(value, CultureInfo.InvariantCulture);
        }

        private static ToolResult Run(string executable, params string[] args)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            using Process process = Process.Start(startInfo)!;
            process.StandardInput.Close();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return new ToolResult(process.ExitCode, stdout, stderr);
        }

        /// <summary>
        /// Locates an executable on PATH. Used only to decide whether to SKIP: ffmpeg is deliberately
        /// optional on the machines that build this repository.
        /// </summary>
        private static string? ResolveBinary(string name)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            foreach (string directory in path.Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                string candidate = System.IO.Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private readonly record struct ToolResult(int ExitCode, string StdOut, string StdErr);
    }

    /// <summary>
    /// Runs its test only when a real ffmpeg AND ffprobe are on PATH; otherwise skips it at discovery
    /// time with the reason. The xUnit v2 idiom this repository already uses for its gated
    /// integration tests (see RealInfrastructureFactAttribute) — chosen over an early return, which
    /// would report these as PASSED on a machine with no ffmpeg while asserting nothing at all.
    /// </summary>
    public sealed class RealFfmpegFactAttribute : FactAttribute
    {
        public RealFfmpegFactAttribute()
        {
            if (Ffmpeg.MissingBinaryMessage is { } missing)
            {
                Skip = "This end-to-end check encodes with real binaries: " + missing
                    + ". Install ffmpeg (apt-get install ffmpeg) or set PATH to include it.";
            }
        }
    }

    /// <summary>As <see cref="RealFfmpegFactAttribute"/>, and additionally requires the vendored watermark raster.</summary>
    public sealed class RealFfmpegWatermarkFactAttribute : FactAttribute
    {
        public RealFfmpegWatermarkFactAttribute()
        {
            if (Ffmpeg.MissingBinaryMessage is { } missing)
            {
                Skip = "This end-to-end check encodes with real binaries: " + missing + ".";
            }
            else if (Ffmpeg.ResolveWatermarkAsset() is null)
            {
                Skip = "The vendored watermark raster inference/assets/watermark.png was not found above this test's output directory.";
            }
        }
    }
}

/// <summary>
/// The SAME acceptance criterion, driven through the real production code path rather than the chain
/// in isolation: <see cref="ReactRemotionSandboxTools.RenderVideoAndUploadToStorage"/> runs with the
/// engine's REAL <see cref="FfmpegVideoToolRunner"/> and <see cref="FfprobeMediaProbe"/>, a fake
/// sandbox serving a real 1920x1080 render, and a fake object store that captures the bytes actually
/// uploaded — which are then written to disk and ffprobed.
///
/// <para>
/// This is the check the plan asks for ("ffprobe of a trial output shows height &lt;= 720") applied
/// to the shipped wiring: policy resolved from the run's organization through a DI scope, the
/// production builder, the production ffmpeg argv, the production upload path. The chain-level tests
/// above pin the filter string; this one pins that the string is what actually runs.
/// </para>
/// </summary>
public class RemotionRenderOutputPolicyEndToEndTests
{
    private readonly ITestOutputHelper _output;

    public RemotionRenderOutputPolicyEndToEndTests(ITestOutputHelper output) => _output = output;

    private const string OutputFileName = "promo.mp4";

    [OutputPolicyFilterBuilderEndToEndTests.RealFfmpegWatermarkFact]
    public async Task A_trial_render_is_uploaded_capped_to_720p_and_really_carries_the_mark()
    {
        using OutputPolicyFilterBuilderEndToEndTests.Scratch scratch = new();

        // A REAL 1080p render, exactly what the sandbox would hand back.
        string sourcePath = scratch.Path("render-1080p.mp4");
        OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.Synthesize(sourcePath, 1920, 1080);
        byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);

        string watermarkAsset = OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.ResolveWatermarkAsset()!;

        OrganizationEntitlementSet trial = new()
        {
            Tier = "trial",
            MaxOutputHeight = OutputPolicyFilterBuilder.DefaultMaxHeight,
            Watermark = true
        };

        byte[] marked = await RenderAsync(sourceBytes, trial, watermarkAsset);
        byte[] cappedOnly = await RenderAsync(sourceBytes, trial, watermarkAssetPath: string.Empty);
        byte[] unentitled = await RenderAsync(sourceBytes, plan: null, watermarkAssetPath: string.Empty);

        string markedPath = scratch.Path("uploaded-marked.mp4");
        string cappedPath = scratch.Path("uploaded-capped.mp4");
        string unentitledPath = scratch.Path("uploaded-unentitled.mp4");
        await File.WriteAllBytesAsync(markedPath, marked);
        await File.WriteAllBytesAsync(cappedPath, cappedOnly);
        await File.WriteAllBytesAsync(unentitledPath, unentitled);

        (int markedWidth, int markedHeight) = OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.ProbeSize(markedPath);
        (int cappedWidth, int cappedHeight) = OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.ProbeSize(cappedPath);
        (int plainWidth, int plainHeight) = OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.ProbeSize(unentitledPath);

        _output.WriteLine($"sandbox render   : ffprobe -> {plainWidth}x{plainHeight} when no plan is resolved");
        _output.WriteLine($"trial plan only  : ffprobe -> {cappedWidth}x{cappedHeight}");
        _output.WriteLine($"trial + mark     : ffprobe -> {markedWidth}x{markedHeight}");

        // The acceptance criterion, on the bytes the object store actually received.
        markedHeight.Should().BeLessThanOrEqualTo(720, "ffprobe of a trial output must show height <= 720");
        markedHeight.Should().Be(OutputPolicyFilterBuilder.DefaultMaxHeight);
        markedWidth.Should().Be(1280, "aspect preserved");
        cappedHeight.Should().Be(OutputPolicyFilterBuilder.DefaultMaxHeight);

        // No plan, no policy: the uploaded object is the render byte for byte, as before B8b.
        unentitled.Should().Equal(sourceBytes);
        plainHeight.Should().Be(1080);

        // ...and the mark is REALLY on the trial output: comparing it against the same plan's
        // cap-only output isolates the watermark from the scale.
        string markedFrame = scratch.Path("marked.png");
        string cappedFrame = scratch.Path("capped.png");
        OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.ExtractFirstFrame(markedPath, markedFrame);
        OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.ExtractFirstFrame(cappedPath, cappedFrame);

        int markSize = OutputPolicyFilterBuilder.WatermarkWidth(
            markedWidth, OutputPolicyFilterBuilder.DefaultWatermarkWidthPct);
        int markX = markedWidth - OutputPolicyFilterBuilder.DefaultWatermarkMargin - markSize;
        int markY = markedHeight - OutputPolicyFilterBuilder.DefaultWatermarkMargin - markSize;

        double markRegion = OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.PsnrAverage(
            markedFrame, cappedFrame, markSize, markSize, markX, markY);
        double controlRegion = OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.PsnrAverage(
            markedFrame, cappedFrame, markSize, markSize, 0, 0);

        _output.WriteLine($"mark box         : {markSize}x{markSize} at ({markX},{markY}) -> psnr {markRegion} dB");
        _output.WriteLine($"control box      : {markSize}x{markSize} at (0,0) -> psnr {controlRegion} dB");

        markRegion.Should().BeLessThan(40, "the trial output's bottom-right corner must carry the mark");
        controlRegion.Should().BeGreaterThan(40, "and nothing else in the frame may have been touched");
    }

    /// <summary>
    /// Runs one render through the real tool. The sandbox (HTTP) and the object store (S3) are the
    /// only fakes; ffmpeg, ffprobe, the policy resolution and the upload path are production code.
    /// </summary>
    private static async Task<byte[]> RenderAsync(byte[] renderBytes, OrganizationEntitlementSet? plan, string watermarkAssetPath)
    {
        List<byte[]> uploaded = [];

        Mock<IAmazonS3> s3 = new();
        s3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) =>
            {
                using MemoryStream copy = new();
                request.InputStream.CopyTo(copy);
                uploaded.Add(copy.ToArray());
            })
            .ReturnsAsync(new PutObjectResponse());

        Mock<IHttpClientFactory> http = new();
        http.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new SandboxHandler(renderBytes)));

        WorkflowExecutionContext context = new()
        {
            ExecutionId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            CorrelationId = "policy-e2e",
            OrganizationId = plan is null ? null : Guid.NewGuid()
        };
        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.SetupGet(a => a.Current).Returns(context);

        ServiceCollection services = new();
        if (plan is not null)
        {
            Mock<IOrganizationEntitlements> entitlements = new();
            entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(plan);
            services.AddSingleton(entitlements.Object);
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sandbox:BaseUrl"] = "http://sandbox:8080",
            ["Sandbox:ApiToken"] = "token",
            ["MinIO:BucketName"] = "bucket"
        }).Build();

        VideoEditingOptions options = new()
        {
            FfmpegPath = OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.Executable,
            FfprobePath = OutputPolicyFilterBuilderEndToEndTests.Ffmpeg.Prober,
            WatermarkAssetPath = watermarkAssetPath
        };
        FfmpegVideoToolRunner runner = new(Options.Create(options), NullLogger<FfmpegVideoToolRunner>.Instance);
        FfprobeMediaProbe probe = new(runner, Options.Create(options), NullLogger<FfprobeMediaProbe>.Instance);

        ReactRemotionSandboxTools tool = new(
            http.Object,
            accessor.Object,
            Mock.Of<IProjectFileWorkspace>(),
            s3.Object,
            configuration,
            NullLogger<ReactRemotionSandboxTools>.Instance,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            probe,
            usageRecorder: null,
            videoToolRunner: runner,
            videoEditingOptions: Options.Create(options));

        await tool.RenderVideoAndUploadToStorage("Main", OutputFileName);

        return uploaded.Should().ContainSingle().Subject;
    }

    /// <summary>Serves the sandbox exec (POST) and the rendered file (GET /files/raw), like the real sandbox API.</summary>
    private sealed class SandboxHandler(byte[] rendered) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"output\":\"rendered\"}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(rendered)
            });
        }
    }
}
