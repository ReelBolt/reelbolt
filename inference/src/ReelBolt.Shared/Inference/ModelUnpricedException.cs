using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Thrown by <see cref="InferenceProviderResolver"/> when billing is on and the only model a call could use is a
/// Platform-billed capability with no effective price (docs/metering-and-billing.md, "Unpriced models fail closed"):
/// an unpriced model is never offered and never run, because its usage would otherwise be metered at zero credits.
/// The message is written for the person who started the step or request and names no internal id. The engine's
/// failure classifier maps <see cref="FailureCode"/> to <c>PlatformInternal</c>: the step persists the code and refunds its hold.
/// </summary>
public sealed class ModelUnpricedException : InvalidOperationException
{
    /// <summary>The stable failure code.</summary>
    public const string FailureCode = "MODEL_UNPRICED";

    public ModelUnpricedException(InferenceProviderCapability capability, string? modelName)
        : base(BuildMessage(capability, modelName))
    {
        Capability = capability;
        ModelName = modelName;
    }

    /// <summary>What the call needed.</summary>
    public InferenceProviderCapability Capability { get; }

    /// <summary>The display name of the unpriced model that was skipped, when there was one.</summary>
    public string? ModelName { get; }

    /// <summary>Always <see cref="FailureCode"/>.</summary>
    public string Code => FailureCode;

    private static string BuildMessage(InferenceProviderCapability capability, string? modelName)
    {
        string which = string.IsNullOrWhiteSpace(modelName) ? $"The {capability} model" : $"The {capability} model \"{modelName}\"";
        return $"{which} has no price set up yet, so it cannot be used right now. " +
               $"Choose another model, or ask an administrator to add a price for it. ({FailureCode})";
    }
}
