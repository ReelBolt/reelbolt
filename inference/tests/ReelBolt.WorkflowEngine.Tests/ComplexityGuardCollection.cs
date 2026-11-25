using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Timing guards must not run beside other tests. xUnit v2 runs a collection defined with DisableParallelization
/// alone, after the parallel collections have finished, so CPU contention cannot skew the measured exponents.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ComplexityGuardCollection
{
    public const string Name = "ComplexityGuard";
}
