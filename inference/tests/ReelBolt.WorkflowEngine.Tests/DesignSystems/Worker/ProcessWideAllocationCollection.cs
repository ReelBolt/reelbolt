using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

/// <summary>
/// WorkerHostTests measures process-wide allocation (GC.GetTotalAllocatedBytes) around three parallel zip posts,
/// so any test running concurrently in this assembly would be counted against its bound. A collection that disables
/// parallelisation makes xunit run its tests alone, which removes that source of flakes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWideAllocationCollection
{
    public const string Name = "ProcessWideAllocation";
}
