using Microsoft.Extensions.Hosting;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>
/// Hands the live avatar switches to the pure app-step derivation at startup, so no app view gains an Avatar input while
/// avatars are off. "On" is EITHER switch (<c>Avatars:Enabled</c> for people, <c>Avatars:Objects:Enabled</c> for objects,
/// docs/design/avatar-objects.md): the run brief's avatar may name an object. Every reader asks the switch at call time
/// and nothing built from it is cached, so it holds whenever the installer runs.
/// </summary>
public sealed class AvatarSwitchInstaller(IConfiguration configuration, ILogger<AvatarSwitchInstaller>? logger = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A switch that cannot be read is off; say so once, here, at Error, naming the key and what it accepts.
        if (logger is not null) ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.LogProblems(configuration, logger);
        ReelBolt.Shared.Workflows.Apps.AppStepFacts.UseBriefAvatarSwitch(() => AvatarOptions.AnyEnabledIn(configuration));
        // Per kind: a recipe written for a person is listed only while people are on, and a step or a run brief that
        // names an avatar of a kind that is off sees no such avatar.
        ReelBolt.Shared.Workflows.Apps.AppStepFacts.UsePersonAvatarSwitch(() => ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(configuration, AvatarOptions.EnabledKey));
        ReelBolt.Shared.Workflows.Apps.AppStepFacts.UseObjectAvatarSwitch(() => ReelBolt.Shared.Avatars.AvatarSwitchConfiguration.ReadBool(configuration, AvatarOptions.ObjectsEnabledKey));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
