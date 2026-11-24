#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Shared.Editing;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The deterministic render-invocation seam the golden-parity harness renders through.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in test code.</b> The production renderer for the editor is
/// <c>StepType.TimelineRender</c> / <c>TimelineRenderStepExecutor</c>, which does not exist until
/// plan task P4-M1. QA may not author production types, and engineering must not depend on test
/// types, so the seam is a test-only interface: it is exactly the shape P4's executor must be
/// adaptable to, and nothing production imports it.
/// </para>
/// <para>
/// <b>The P4 wiring.</b> In P4-Q1 an adapter over the real <c>TimelineRenderStepExecutor</c>
/// implements this interface and replaces the throwaway reference adapter
/// (<see cref="SpikeTimelineRenderSeam"/>) in the harness. Until then the harness renders through a
/// spike adapter that encodes the document's cut list directly, and
/// <c>TimelineRenderSeamMarkerTests</c> keeps that gap visible on every test run.
/// </para>
/// <para>
/// <b>One method, by design.</b> A render takes a timeline document and an output path and produces
/// a file; anything else (presets, revisions, storage keys, progress) belongs to the caller. The
/// seam returns every argv it used, so a caller can assert that two renders were the same render
/// rather than merely concluding it from two similar-looking files.
/// </para>
/// </remarks>
public interface ITimelineRenderSeam
{
    /// <summary>
    /// Renders <paramref name="document"/> to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="document">The seeded v2 edit-timeline document to render unmodified.</param>
    /// <param name="outputPath">Absolute path the render must be written to.</param>
    /// <param name="cancellationToken">Cancellation for the whole render.</param>
    /// <returns>The produced path plus every ffmpeg invocation the seam made, in order.</returns>
    Task<TimelineRenderProduct> RenderAsync(
        EditTimeline document,
        string outputPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a seam render produced: the output path, and every ffmpeg argv used to produce it.
/// </summary>
/// <param name="OutputPath">Absolute path of the produced file.</param>
/// <param name="Invocations">
/// Every ffmpeg invocation the seam made, in order. A single-pass renderer returns exactly one; a
/// segment-then-concat renderer returns several. Each is the argv as passed to
/// <c>IVideoToolRunner</c> — a list, never a joined command line.
/// </param>
public sealed record TimelineRenderProduct(
    string OutputPath,
    IReadOnlyList<IReadOnlyList<string>> Invocations);
