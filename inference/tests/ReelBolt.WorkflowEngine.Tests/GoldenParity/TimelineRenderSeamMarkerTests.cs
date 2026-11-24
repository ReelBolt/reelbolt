#nullable enable
using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The marker that keeps the golden-parity harness's one real gap visible on <b>every</b> test run.
/// </summary>
/// <remarks>
/// <para>
/// The harness is built and proven, but it renders through <see cref="SpikeTimelineRenderSeam"/> —
/// throwaway spike scaffolding — because the production renderer it is supposed to check,
/// <c>StepType.TimelineRender</c> / <c>TimelineRenderStepExecutor</c>, does not exist until plan task
/// P4-M1. A green harness therefore says <i>the tooling works and the tolerances are calibrated</i>;
/// it does not say the editor can render a timeline.
/// </para>
/// <para>
/// That distinction is recorded in prose in several places, and prose is exactly what gets lost. The
/// skipped test below is the machine-checkable version of it: it shows up as a skip in every
/// <c>dotnet test</c> run, and its body is written so that removing the <c>Skip</c> without wiring
/// the executor fails rather than silently passing.
/// </para>
/// </remarks>
[Collection(GoldenParityCollection.Name)]
public class TimelineRenderSeamMarkerTests
{
    private readonly GoldenParityTestEnvironment _environment;

    public TimelineRenderSeamMarkerTests(GoldenParityTestEnvironment environment) => _environment = environment;

    /// <summary>
    /// The real renderer leg, wired in P4-Q1: the harness's <c>TimelineRenderPath</c> is the
    /// production <c>TimelineRenderStepExecutor</c>'s renderer (<c>TimelineRenderer</c>) behind the
    /// test-only <see cref="TimelineRenderSeamAdapter"/>, and the artifact says so.
    /// </summary>
    [FfmpegToolsFact]
    public void Golden_parity_renders_through_the_real_TimelineRender_step_executor()
    {
        Type? executor = typeof(FfmpegVideoToolRunner).Assembly.GetType(
            "ReelBolt.WorkflowEngine.Execution.StepExecutors.TimelineRenderStepExecutor");
        executor.Should().NotBeNull("P4-M1 landed the production executor");

        // The reference leg's invocations carry the renderer's own mezzanine fingerprint (an
        // all-intra x264 region encode followed by a concat-demuxer join), which the spike adapter
        // never emitted — so this is provably not the spike rendering under a new name.
        GoldenParityPairResult p1 = _environment.Verdict.Pair("P1");
        p1.ReferenceInvocation.Should().Contain(args => args.Contains("-keyint_min") && args.Contains("1"),
            "every region renders to an all-intra mezzanine");
        p1.ReferenceInvocation.Should().Contain(args => args.Contains("concat") && args.Contains("-safe"),
            "regions are joined with the concat demuxer");
        p1.ReferenceInvocation.Should().Contain(args => args.Any(a => a.StartsWith("loudnorm=", StringComparison.Ordinal)),
            "program loudness is measured on every render");
    }

    [FfmpegToolsFact]
    public void The_seam_and_its_spike_adapter_are_test_only_types()
    {
        // QA may not author production types and engineering must not depend on test types, so the
        // seam has to live here — asserted rather than assumed.
        typeof(ITimelineRenderSeam).Assembly.Should().BeSameAs(typeof(TimelineRenderSeamMarkerTests).Assembly);
        typeof(SpikeTimelineRenderSeam).Assembly.Should().BeSameAs(typeof(TimelineRenderSeamMarkerTests).Assembly);

        typeof(ITimelineRenderSeam).Namespace.Should().Be("ReelBolt.WorkflowEngine.Tests");
        typeof(SpikeTimelineRenderSeam).Namespace.Should().Be("ReelBolt.WorkflowEngine.Tests");

        // And nothing in the production assemblies implements it.
        typeof(FfmpegVideoToolRunner).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITimelineRenderSeam).IsAssignableFrom(t))
            .Should().BeEmpty("the seam is test-only by design; P4-Q1 adds the adapter here, not in production");
    }

    [FfmpegToolsFact]
    public void The_harness_records_which_seam_it_actually_rendered_through()
    {
        // The artifact must not let a reader mistake a spike render for a product render.
        _environment.Verdict.Pairs.Should().HaveCount(3);
        _environment.Verdict.Pairs.SelectMany(p => p.ReferenceInvocation).Should().NotBeEmpty();

        _environment.Verdict.Pairs
            .SelectMany(p => p.ReferenceInvocation.Concat(p.CandidateInvocation))
            .SelectMany(args => args)
            .Should().Contain("libx264", "the spike adapter's own encoder, not a renderer's preset builder");
    }
}
