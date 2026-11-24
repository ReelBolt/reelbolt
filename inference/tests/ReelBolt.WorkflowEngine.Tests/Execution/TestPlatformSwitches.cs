using System.Threading;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Services;

namespace ReelBolt.WorkflowEngine.Tests.Execution;

/// <summary>
/// A readable settings store with nothing paused, for the harnesses that are not about switches (the
/// dispatch/liveness plumbing and the decision memo). Production always resolves the real
/// <see cref="PlatformSwitches"/>; the kill-switch behaviour itself is pinned by
/// <c>PlatformSwitchesTests</c> and <c>PlatformKillSwitchAdmissionTests</c>.
/// </summary>
internal sealed class TestPlatformSwitches : IPlatformSwitches
{
    public ValueTask<PlatformSwitchState> GetAsync(CancellationToken cancellationToken = default)
        => new(PlatformSwitchState.Running);
}
