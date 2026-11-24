namespace ReelBolt.Shared.Metering;

/// <summary>
/// The Chat models an app's <c>model</c> inputs chose for its agents in one run (docs/design/app-inputs.md 8.4, 8.5):
/// agent definition id to the chosen Chat capability id. Carried by an async-local scope so the shared cold-start pricing
/// (<see cref="ProfilePriceTable"/>) prices those agents' chat quantities at the chosen model, on the Inference API's
/// estimate and run gate and on the engine's holds alike, without a new pricing path. Null (the default) everywhere a run
/// has no such choice, so every other price is unchanged.
/// </summary>
public static class AppAgentModelAmbient
{
    private static readonly AsyncLocal<IReadOnlyDictionary<Guid, Guid>?> CurrentChoices = new();

    private static readonly AsyncLocal<IReadOnlySet<Guid>?> CurrentStepModels = new();

    /// <summary>The choices of the run being priced, or null.</summary>
    public static IReadOnlyDictionary<Guid, Guid>? Current => CurrentChoices.Value;

    /// <summary>The chosen Chat capability of <paramref name="agentDefinitionId"/> in the current scope, or null.</summary>
    public static Guid? For(Guid? agentDefinitionId) =>
        agentDefinitionId is { } id && CurrentChoices.Value is { } choices && choices.TryGetValue(id, out Guid chosen) ? chosen : null;

    /// <summary>
    /// The model capabilities the run's STEP <c>model</c> inputs chose (docs/design/app-inputs.md 28): a Voiceover step whose bound
    /// <c>providerId</c> is one of them prices its speech at that model (<see cref="StepUsageProfiles.For(Data.Models.WorkflowStep, Data.Models.AgentType?)"/>),
    /// the model it will run on. Null (the default) everywhere a run chose no step model, so every other hold is unchanged.
    /// </summary>
    public static IReadOnlySet<Guid>? StepModels => CurrentStepModels.Value;

    /// <summary>True when <paramref name="capabilityId"/> was chosen by one of the run's step <c>model</c> inputs.</summary>
    public static bool IsChosenStepModel(Guid? capabilityId) =>
        capabilityId is { } id && CurrentStepModels.Value is { } chosen && chosen.Contains(id);

    /// <summary>Enters a scope with <paramref name="choices"/> (null or empty leaves nothing chosen); dispose to restore.</summary>
    public static IDisposable Enter(IReadOnlyDictionary<Guid, Guid>? choices) => Enter(choices, null);

    /// <summary>
    /// Enters a scope with the agents' <paramref name="choices"/> and the steps' chosen <paramref name="stepModels"/> (null or empty
    /// leaves nothing chosen); dispose to restore both.
    /// </summary>
    public static IDisposable Enter(IReadOnlyDictionary<Guid, Guid>? choices, IReadOnlySet<Guid>? stepModels)
    {
        IReadOnlyDictionary<Guid, Guid>? previous = CurrentChoices.Value;
        IReadOnlySet<Guid>? previousSteps = CurrentStepModels.Value;
        CurrentChoices.Value = choices is { Count: > 0 } ? choices : null;
        CurrentStepModels.Value = stepModels is { Count: > 0 } ? stepModels : null;
        return new Restore(previous, previousSteps);
    }

    private sealed class Restore(IReadOnlyDictionary<Guid, Guid>? previous, IReadOnlySet<Guid>? previousSteps) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CurrentChoices.Value = previous;
            CurrentStepModels.Value = previousSteps;
        }
    }
}
