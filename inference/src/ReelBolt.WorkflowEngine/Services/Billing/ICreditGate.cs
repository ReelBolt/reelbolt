using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Billing;

/// <summary>What a step needs the org's plan to allow, checked before any credit is held.</summary>
public enum CreditEntitlement
{
    None,
    Rooms,
    VideoGeneration,

    /// <summary>The plan's <c>audioGenAllowed</c> only: no daily budget is required.</summary>
    AudioGeneration,

    /// <summary>The plan's <c>videoEnhanceAllowed</c> only (a HOSTED VideoEnhance step; local operations ask for nothing).</summary>
    VideoEnhance
}

/// <summary>Result class of <see cref="ICreditGate.ReserveAsync"/>.</summary>
public enum CreditReserveOutcome
{
    Allowed,
    InsufficientCredits,
    EntitlementDenied
}

/// <summary>
/// Outcome of a reservation. <see cref="ReservationId"/> is null when nothing was held (billing off,
/// BYO-only or zero-estimate step); settle/release of a null id is a no-op.
/// </summary>
public sealed record CreditReservation(
    CreditReserveOutcome Outcome,
    Guid? ReservationId,
    string? Reason = null,
    long ReservedMillis = 0,
    long AvailableMillis = 0,
    ReelBolt.Shared.Billing.ApiKeyBudgetRefusal? ApiKeyBudget = null)
{
    public bool Allowed => Outcome == CreditReserveOutcome.Allowed;
}

/// <summary>Outcome of a settle or release.</summary>
/// <param name="Ok">False only when the ledger refused or failed (see <paramref name="Error"/>).</param>
/// <param name="Error">Stable ledger error code when refused.</param>
/// <param name="ChargedMillis">Credits charged by this call.</param>
/// <param name="RefundedMillis">Credits returned by this call (a refund of an already settled step).</param>
public sealed record CreditSettlement(bool Ok, string? Error = null, long ChargedMillis = 0, long RefundedMillis = 0);

/// <summary>
/// Per-step credit gate (decision D8): reserve before a step, settle after, refund platform-caused
/// failures. Generalises <c>IVideoGenerationBudgetGate</c> over the B5 credit ledger.
/// </summary>
public interface ICreditGate
{
    /// <summary>
    /// Holds <paramref name="estimateMillis"/> for one step. Idempotent per
    /// (<paramref name="stepResultId"/>, <paramref name="attempt"/>): a crash-resumed step gets its
    /// existing reservation back. Billing off, or a zero estimate (BYO / runner), short-circuits to
    /// Allowed with no reservation and no writes. <paramref name="overdraftBasisMillis"/> (default: the estimate) is
    /// what the overdraft headroom is derived from, so a scaled-down hold keeps the full step's tolerance.
    /// <paramref name="apiKeyId"/> (the key that started the run, if any) subjects the hold to the key's caps inside the ledger's lock.
    /// </summary>
    Task<CreditReservation> ReserveAsync(
        Guid organizationId,
        Guid executionId,
        Guid stepResultId,
        long estimateMillis,
        Guid? projectId = null,
        CreditEntitlement requires = CreditEntitlement.None,
        int attempt = 0,
        CancellationToken ct = default,
        long? overdraftBasisMillis = null,
        Guid? apiKeyId = null);

    /// <summary>Charges <paramref name="actualMillis"/> (may exceed the hold) and releases the remainder.</summary>
    Task<CreditSettlement> SettleAsync(Guid? reservationId, long actualMillis, CancellationToken ct = default);

    /// <summary>
    /// Closes the hold of a FAILED step. PlatformProviderError, PlatformInternal and any unclassified
    /// value (None on a failure, or an undefined member: D9) charge nothing, and refund when the step
    /// was already settled. Every other class charges <paramref name="actualMillis"/> consumed so far.
    /// </summary>
    Task<CreditSettlement> ReleaseAsync(Guid? reservationId, FailureClass failureClass, long actualMillis = 0, CancellationToken ct = default);

    /// <summary>
    /// S-3: closes every still-open hold of an execution; returns how many. The hold of a COMPLETED
    /// step result is settled (<see cref="SettleOpenAsync"/>), never refunded; the hold of a FAILED
    /// step result with a recorded class is closed by that class (<see cref="ReleaseOpenAsync"/>); any
    /// other hold is released as a platform failure. A hold found closed by the time it is closed is
    /// left as it is and never refunded.
    /// </summary>
    Task<int> ReleaseExecutionAsync(Guid executionId, CancellationToken ct = default);

    /// <summary>
    /// S-3: settles an open hold whose step completed, at <paramref name="recordedMillis"/> when the
    /// step recorded what it metered, else at max(the step's metered usage events, the hold) — the
    /// hold is the floor when the actual is unknown. Same <c>gate-settle:</c> key as
    /// <see cref="SettleAsync"/>, so a retry, a sweep and a second replica all replay one entry. A
    /// no-op success when the hold is already closed.
    /// </summary>
    Task<CreditSettlement> SettleOpenAsync(Guid reserveId, long? recordedMillis, CancellationToken ct = default);

    /// <summary>
    /// Closes a hold that is still OPEN and never refunds one that is not: a refundable class (a
    /// platform cause, or one nobody can attribute) releases it; any other class settles it at
    /// <paramref name="recordedMillis"/> when the step recorded what it metered, else at its metered
    /// usage, never less than the hold when that usage was unpriced. A hold that already has a Settle
    /// or Release child is left as it is and reported as a no-op success, so a sweep that raced a
    /// settle can never give that charge back. Same <c>gate-settle:</c>/<c>gate-release:</c> keys as
    /// the other closers.
    /// </summary>
    Task<CreditSettlement> ReleaseOpenAsync(Guid reserveId, FailureClass failureClass, long? recordedMillis, CancellationToken ct = default);
}
