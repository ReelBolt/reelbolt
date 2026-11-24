using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Activity;

/// <summary>
/// Whether a failed step's <see cref="FailureClass"/> gives the credits back. The engine owns the rule
/// (<c>CreditGate.IsRefundable</c>, not referenced from the API); this is the same switch, and a test
/// walks every <see cref="FailureClass"/> member against the engine's answer so the two cannot drift.
/// </summary>
public static class ActivityFailureRefund
{
    public static bool IsRefunded(FailureClass c) => c switch
    {
        FailureClass.UserInput or FailureClass.UserCancelled or FailureClass.ByoProviderError
            or FailureClass.InsufficientCredits or FailureClass.EntitlementDenied or FailureClass.Runner => false,
        _ => true
    };
}
