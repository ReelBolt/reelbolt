using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Billing;

/// <summary>
/// Thrown when the credit gate refuses a step: the organization's balance cannot cover the step's
/// reservation (B6a), or its plan does not include the step type. Nothing ran, so nothing is
/// charged, and the step fails with the code in its message
/// (<c>INSUFFICIENT_CREDITS</c> / <c>PLAN_NOT_ENTITLED</c>) rather than silently continuing.
///
/// <para>
/// Carries its own <see cref="Data.Models.FailureClass"/> and code rather than leaving the executor
/// to re-derive them from the message text: docs/design/saas-metering.md requires the class to be
/// assigned where the failure is produced, and message-substring classification to be a fallback
/// only.
/// </para>
/// </summary>
public sealed class CreditRefusalException : Exception
{
    public CreditRefusalException(FailureClass failureClass, string code, string message)
        : base(message)
    {
        FailureClass = failureClass;
        Code = code;
    }

    /// <summary>The class the refusal charges under: nothing ran, so this is never a platform refund class.</summary>
    public FailureClass FailureClass { get; }

    /// <summary>Stable code, also the first token of <see cref="Exception.Message"/>.</summary>
    public string Code { get; }

    /// <summary>
    /// The refusal for an <see cref="ICreditGate.ReserveAsync"/> outcome that is not Allowed.
    /// <see cref="CreditReserveOutcome.EntitlementDenied"/> is the plan's doing
    /// (<see cref="Data.Models.FailureClass.EntitlementDenied"/>); both credit outcomes — a balance
    /// too low, and a gate we could not evaluate at all — are
    /// <see cref="Data.Models.FailureClass.InsufficientCredits"/>, which the ledger charges nothing
    /// for either way.
    /// </summary>
    public static CreditRefusalException For(CreditReservation reservation, WorkflowStep step)
    {
        // An API key's own cap or status refusal keeps the InsufficientCredits class (consumption is kept) but names its own code.
        bool keyRefusal = reservation.Outcome == CreditReserveOutcome.InsufficientCredits
            && reservation.Reason is CreditLedgerErrors.ApiKeyBudgetExceeded or CreditLedgerErrors.ApiKeyInactive;
        if (keyRefusal)
        {
            string keyCode = reservation.Reason!;
            string keyDetail = keyCode == CreditLedgerErrors.ApiKeyInactive || reservation.ApiKeyBudget is null
                ? "the API key that started this run was revoked or has expired"
                : $"the API key's {ApiKeyBudgetWindows.Name(reservation.ApiKeyBudget.Window)} limit of {reservation.ApiKeyBudget.CapMillis / 1000m:0.##} credits is reached " +
                  $"({reservation.ApiKeyBudget.UsedMillis / 1000m:0.##} used); it resets at {reservation.ApiKeyBudget.ResetsAt:yyyy-MM-dd HH:mm} UTC";
            return new CreditRefusalException(
                FailureClass.InsufficientCredits,
                keyCode,
                $"Step {step.StepOrder} ({step.StepType}) failed: {keyCode} — {keyDetail}.");
        }

        string code = reservation.Outcome == CreditReserveOutcome.EntitlementDenied
            ? "PLAN_NOT_ENTITLED"
            : "INSUFFICIENT_CREDITS";
        FailureClass failureClass = reservation.Outcome == CreditReserveOutcome.EntitlementDenied
            ? FailureClass.EntitlementDenied
            : FailureClass.InsufficientCredits;

        string detail = reservation.Reason switch
        {
            FailureClassifier.GateUnavailableCode =>
                "the credit gate could not be evaluated, so the step was refused rather than run uncharged",
            null => "the organization does not have enough credits for this step",
            _ => reservation.Reason
        };

        return new CreditRefusalException(
            failureClass,
            code,
            $"Step {step.StepOrder} ({step.StepType}) failed: {code} — {detail}. " +
            $"Available: {reservation.AvailableMillis} millicredits; this step would hold {reservation.ReservedMillis}.");
    }
}
