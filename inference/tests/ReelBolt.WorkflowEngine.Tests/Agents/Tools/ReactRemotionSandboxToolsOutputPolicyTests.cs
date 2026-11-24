using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
using ReelBolt.WorkflowEngine.Services.Styles;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// B8b slice 3: the trial output policy as it is applied to the REMOTION render path
/// (<see cref="ReactRemotionSandboxTools.RenderVideoAndUploadToStorage"/>). The sandbox has no
/// ffmpeg — deliberately, see docs/video-editing.md — so the policy is a post-pass on the engine
/// host: the render is pulled to local scratch, re-encoded ONCE through
/// <see cref="OutputPolicyFilterBuilder"/>'s chain, and it is the post-processed file that is
/// uploaded under the same key and metadata.
///
/// <para>
/// The two ends are verified separately and deliberately: these tests pin the WIRING (who resolves
/// the policy, whether a second encode is skipped, what is uploaded when it fails, and the exact
/// ffmpeg argv), while <c>OutputPolicyFilterBuilderEndToEndTests</c> runs that same chain through
/// REAL ffmpeg and ffprobe. A mock here can prove the chain was built by the production builder; it
/// cannot prove the chain does anything.
/// </para>
/// </summary>
public class ReactRemotionSandboxToolsOutputPolicyTests
{
    private static readonly byte[] SandboxRenderBytes = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] PostProcessedBytes = [9, 9, 9, 9];

    /// <summary>Serves the sandbox exec (POST) and the rendered file (GET /files/raw), exactly like the real sandbox API.</summary>
    private sealed class SandboxHandler(byte[] rendered) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"output":"rendered"}""") });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(rendered) });
        }
    }

    /// <summary>Everything one render needs, plus the observations the assertions are made on.</summary>
    private sealed class Harness
    {
        public required ReactRemotionSandboxTools Tool { get; init; }
        public required Mock<IVideoToolRunner> Runner { get; init; }
        public required List<string> FfmpegArgs { get; init; }
        public required List<byte[]> UploadedBodies { get; init; }
        public required WorkflowExecutionContext Context { get; init; }
    }

    /// <param name="entitlements">The run's plan; null means the run has no organization at all (the self-host / bare-fixture case).</param>
    /// <param name="probedHeight">What the engine's ffprobe reports for the render it just pulled down.</param>
    /// <param name="watermarkAssetPath">The configured raster; null means this deployment configures none.</param>
    /// <param name="runnerExitCode">0 = a successful post-pass (the mock writes the target file); non-zero = ffmpeg failed.</param>
    private static Harness CreateHarness(
        OrganizationEntitlementSet? entitlements,
        int probedWidth = 1920,
        int probedHeight = 1080,
        string? watermarkAssetPath = null,
        int runnerExitCode = 0,
        Guid? organizationId = null)
    {
        Guid executionId = Guid.NewGuid();
        WorkflowExecutionContext ctx = new()
        {
            ExecutionId = executionId,
            ProjectId = Guid.NewGuid(),
            CorrelationId = "policy-test",
            OrganizationId = entitlements is null ? null : organizationId ?? Guid.NewGuid()
        };

        Mock<IHttpClientFactory> http = new();
        http.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new SandboxHandler(SandboxRenderBytes)));

        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.SetupGet(a => a.Current).Returns(ctx);

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
        s3.Setup(s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(SandboxRenderBytes) });

        Mock<IMediaProbe> probe = new();
        probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(1.0, 30, 1, probedWidth, probedHeight, "h264", null, null));

        // The post-pass's own output: ffmpeg's LAST argument is the target path, so a mock that
        // writes recognizable bytes there is what makes "the post-processed file was uploaded"
        // observable rather than assumed.
        List<string> ffmpegArgs = [];
        Mock<IVideoToolRunner> runner = new();
        runner.Setup(r => r.RunFfmpegAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) =>
            {
                ffmpegArgs.AddRange(args);
                if (runnerExitCode == 0)
                    File.WriteAllBytes(args[^1], PostProcessedBytes);
            })
            .ReturnsAsync(new VideoToolResult(runnerExitCode, string.Empty, runnerExitCode == 0 ? string.Empty : "boom", false));

        ServiceCollection services = new();
        if (entitlements is not null)
        {
            Mock<IOrganizationEntitlements> resolved = new();
            resolved.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(entitlements);
            services.AddSingleton(resolved.Object);
        }

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sandbox:BaseUrl"] = "http://sandbox:8080",
            ["Sandbox:ApiToken"] = "t",
            ["MinIO:BucketName"] = "b"
        }).Build();

        ReactRemotionSandboxTools tool = new(
            http.Object,
            accessor.Object,
            Mock.Of<IProjectFileWorkspace>(),
            s3.Object,
            config,
            NullLogger<ReactRemotionSandboxTools>.Instance,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            probe.Object,
            usageRecorder: null,
            videoToolRunner: runner.Object,
            videoEditingOptions: Options.Create(new VideoEditingOptions { WatermarkAssetPath = watermarkAssetPath ?? string.Empty }));

        return new Harness
        {
            Tool = tool,
            Runner = runner,
            FfmpegArgs = ffmpegArgs,
            UploadedBodies = uploaded,
            Context = ctx
        };
    }

    private static OrganizationEntitlementSet TrialPlan(int maxHeight = OutputPolicyFilterBuilder.DefaultMaxHeight, bool watermark = false) =>
        new() { Tier = "trial", MaxOutputHeight = maxHeight, Watermark = watermark };

    [Fact]
    public async Task A_capped_plan_re_encodes_the_render_once_and_uploads_the_post_processed_file()
    {
        Harness harness = CreateHarness(TrialPlan());

        string result = await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

        // The contract is unchanged: the same result shape, the same storage key.
        JsonElement json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        string expectedKey = $"projects/{harness.Context.ProjectId}/outputFiles/{harness.Context.ExecutionId}/promo.mp4";
        json.GetProperty("storageKey").GetString().Should().Be(expectedKey);
        harness.Context.PendingOutputStorageKey.Should().Be(expectedKey);

        // Exactly ONE post-pass, and it is the production builder's chain for a 1920x1080 source.
        harness.Runner.Verify(
            r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);

        int chainIndex = harness.FfmpegArgs.IndexOf("-filter_complex");
        chainIndex.Should().BeGreaterThan(-1, "the post-pass must carry a filtergraph");
        harness.FfmpegArgs[chainIndex + 1].Should().Be(
            OutputPolicyFilterBuilder.BuildFilterChain("[0:v]", 1920, 1080),
            "the tool must reuse the SAME builder the compile step uses, not a second one");

        // The POST-PROCESSED bytes are what went to the object store — not the sandbox's original.
        harness.UploadedBodies.Should().ContainSingle();
        harness.UploadedBodies[0].Should().Equal(PostProcessedBytes);
    }

    [Fact]
    public async Task No_organization_means_no_policy_at_all_no_probe_no_re_encode_no_second_encode()
    {
        Harness harness = CreateHarness(entitlements: null);

        string result = await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("success").GetBoolean().Should().BeTrue();
        harness.Runner.Verify(
            r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "without a plan there is no policy, and the pre-B8b path must stay exactly as it was");
        harness.FfmpegArgs.Should().BeEmpty();
        harness.UploadedBodies.Should().ContainSingle().Which.Should().Equal(
            SandboxRenderBytes, "the untouched render is streamed through byte for byte");
    }

    [Fact]
    public async Task A_run_whose_plan_has_no_cap_and_no_watermark_is_uploaded_untouched()
    {
        // An entitled org whose plan is the unlimited/self-host shape: there is nothing a policy
        // could do, so it must cost exactly what it cost before B8b — no second encode.
        Harness harness = CreateHarness(new OrganizationEntitlementSet { Tier = "selfhost" });

        await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

        harness.Runner.Verify(
            r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UploadedBodies.Should().ContainSingle().Which.Should().Equal(SandboxRenderBytes);
    }

    [Fact]
    public async Task A_render_already_inside_the_cap_is_not_re_encoded()
    {
        Harness harness = CreateHarness(TrialPlan(), probedWidth: 1280, probedHeight: 720);

        await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

        harness.Runner.Verify(
            r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a frame already at or below the cap with no mark armed must not pay for a second encode");
        harness.UploadedBodies.Should().ContainSingle().Which.Should().Equal(SandboxRenderBytes);
    }

    [Fact]
    public async Task A_portrait_render_whose_short_side_is_at_the_cap_is_not_re_encoded()
    {
        // The cap names the SHORT side: a 720x1280 render is within a 720p cap. Deciding on the
        // height alone ran the post-pass with a bare "[0:v]" graph, which ffmpeg rejects.
        Harness harness = CreateHarness(TrialPlan(), probedWidth: 720, probedHeight: 1280);

        await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

        harness.Runner.Verify(
            r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UploadedBodies.Should().ContainSingle().Which.Should().Equal(SandboxRenderBytes);
    }

    [Fact]
    public async Task An_armed_watermark_adds_the_configured_raster_as_the_second_input()
    {
        string asset = Path.Combine(Path.GetTempPath(), $"reelbolt-wm-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(asset, [0x89, 0x50, 0x4E, 0x47]);
        try
        {
            Harness harness = CreateHarness(TrialPlan(watermark: true), watermarkAssetPath: asset);

            await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

            int inputIndex = harness.FfmpegArgs.IndexOf("-i");
            inputIndex.Should().BeGreaterThan(-1);
            harness.FfmpegArgs.Should().Contain(asset, "the configured mark is the post-pass's second input");

            int chainIndex = harness.FfmpegArgs.IndexOf("-filter_complex");
            harness.FfmpegArgs[chainIndex + 1].Should().Be(
                OutputPolicyFilterBuilder.BuildFilterChain(
                    "[0:v]", 1920, 1080, applyWatermark: true, inputIndexForIndex: _ => 1));
        }
        finally
        {
            File.Delete(asset);
        }
    }

    [Fact]
    public async Task An_entitled_watermark_with_no_asset_on_disk_still_caps_and_uploads_the_render()
    {
        // The shipped self-host shape: the plan entitles a mark, no raster is configured. The cap
        // still applies and the encode still succeeds — the mark is skipped, never faked.
        Harness harness = CreateHarness(TrialPlan(watermark: true), watermarkAssetPath: null);

        string result = await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("success").GetBoolean().Should().BeTrue();
        harness.FfmpegArgs.Should().NotContain(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        harness.UploadedBodies.Should().ContainSingle().Which.Should().Equal(PostProcessedBytes);
    }

    [Fact]
    public async Task A_failed_post_pass_uploads_the_untouched_render_instead_of_losing_it()
    {
        Harness harness = CreateHarness(TrialPlan(), runnerExitCode: 1);

        string result = await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("success").GetBoolean().Should().BeTrue(
            "a failed post-pass must never fail the render the model already paid for");
        harness.UploadedBodies.Should().ContainSingle().Which.Should().Equal(
            SandboxRenderBytes, "ffmpeg failing means the original render is uploaded, not nothing at all");
    }

    [Fact]
    public async Task A_still_render_is_never_post_processed_even_on_a_watermarked_plan()
    {
        Harness harness = CreateHarness(TrialPlan(watermark: true), watermarkAssetPath: "/nonexistent.png");

        await harness.Tool.RenderVideoAndUploadToStorage("Main", "thumb.png");

        harness.Runner.Verify(
            r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a still has no picture pipeline to cap");
    }

    // --- QA-RR-03: the render cap is the plan clamped by the run's resolution choice --------------
    // ResolveOutputPolicyAsync's MaxHeight must be OutputResolution.EffectiveMaxHeight(plan, choice),
    // never the raw plan height: on Pro (2160) a run at Auto stays at today's 1080p.

    private static OrganizationEntitlementSet ProPlan() => new() { Tier = "pro", MaxOutputHeight = 2160 };

    [Fact]
    public async Task Pro_at_Auto_caps_a_4k_render_to_1080p_not_the_plans_2160p()
    {
        Harness harness = CreateHarness(ProPlan(), probedWidth: 3840, probedHeight: 2160);

        await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");

        harness.Runner.Verify(
            r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Auto on Pro renders at 1080p, so a 2160p frame is brought down to it");
        int chainIndex = harness.FfmpegArgs.IndexOf("-filter_complex");
        harness.FfmpegArgs[chainIndex + 1].Should().Be(
            OutputPolicyFilterBuilder.BuildFilterChain("[0:v]", 3840, 2160, maxHeight: 1080));
        harness.UploadedBodies.Should().ContainSingle().Which.Should().Equal(PostProcessedBytes);
    }

    [Fact]
    public async Task Pro_at_2160p_keeps_a_4k_render_untouched()
    {
        Harness harness = CreateHarness(ProPlan(), probedWidth: 3840, probedHeight: 2160);

        using (RunStyleAmbient.Enter(new RunStyleState(null, null, StyleNudgeService.Instance, outputResolution: 2160)))
        {
            await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4");
        }

        harness.Runner.Verify(
            r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a 2160p choice the plan allows is the cap, and the render is already at it");
        harness.UploadedBodies.Should().ContainSingle().Which.Should().Equal(SandboxRenderBytes);
    }

    [Fact]
    public async Task Trial_at_Auto_caps_a_model_scale_4_render_to_720p()
    {
        // The model asked for --scale 4 on a 1920x1080 composition: the sandbox hands back 7680x4320.
        Harness harness = CreateHarness(TrialPlan(maxHeight: 720), probedWidth: 7680, probedHeight: 4320);

        await harness.Tool.RenderVideoAndUploadToStorage("Main", "promo.mp4", remotionArgs: ["--scale", "4"]);

        int chainIndex = harness.FfmpegArgs.IndexOf("-filter_complex");
        chainIndex.Should().BeGreaterThan(-1, "the model's own --scale can never take a render past the plan");
        harness.FfmpegArgs[chainIndex + 1].Should().Be(
            OutputPolicyFilterBuilder.BuildFilterChain("[0:v]", 7680, 4320, maxHeight: 720));
        harness.UploadedBodies.Should().ContainSingle().Which.Should().Equal(PostProcessedBytes);
    }
}
