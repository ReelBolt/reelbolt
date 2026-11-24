using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Serializes the test classes that drive <see cref="ReelBolt.WorkflowEngine.Execution.StepExecutors.VideoCompileStepExecutor"/>'s
/// ffmpeg filter-availability probes.
///
/// <para>
/// Those probes (drawtext, amix-normalize, perspective/alphamerge, colour-grade filters) cache
/// their answer in PROCESS-WIDE statics for the lifetime of the run — deliberately, since in
/// production they shell out to <c>ffmpeg -filters</c> and the answer cannot change. Tests
/// therefore have to reset them, and xUnit runs distinct test classes in parallel by default, so
/// two classes resetting and reading the same static race each other: one class's
/// "filters unavailable" case can land between another's reset and read, turning a real assertion
/// into a coin flip. Observed as a genuinely intermittent failure (1 in ~3 full runs) on the
/// multi-source screen-insert tests.
/// </para>
///
/// <para>
/// Putting every class that touches those statics in one collection makes them run sequentially
/// with respect to each other while still running in parallel with the rest of the suite.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class VideoFilterProbeCacheCollection
{
    public const string Name = "VideoFilterProbeCache";
}
