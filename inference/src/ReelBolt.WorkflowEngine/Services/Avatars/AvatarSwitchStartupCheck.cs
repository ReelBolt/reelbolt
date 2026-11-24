using Microsoft.Extensions.Hosting;
using ReelBolt.Shared.Avatars;

namespace ReelBolt.WorkflowEngine.Services.Avatars;

/// <summary>
/// Says once, at startup, at Error, when an avatar switch (<c>Avatars:Enabled</c>, <c>Avatars:Objects:Enabled</c>,
/// <c>Avatars:Generation</c>) holds a value that cannot be read, naming the key and what it accepts. The switch itself
/// is off then (<see cref="AvatarSwitchConfiguration"/>), the same as in the Inference API; no run throws because of it.
/// </summary>
public sealed class AvatarSwitchStartupCheck(IConfiguration configuration, ILogger<AvatarSwitchStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        AvatarSwitchConfiguration.LogProblems(configuration, logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
