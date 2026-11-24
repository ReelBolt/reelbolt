using Microsoft.AspNetCore.Mvc;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>The HTTP answers and plain-words texts of a refused non-workflow charge.</summary>
public static class NonWorkflowChargeResults
{
    /// <summary>
    /// What the assistant (and an MCP tool call) says when the month's included assistant credits are used up and
    /// the workspace has nothing left. Plain words, no ids, no jargon.
    /// </summary>
    public const string AssistantOutOfCreditsText =
        "You've used this month's included assistant credits and your workspace has no credits left. "
        + "Add credits on the Plan & billing page to keep going.";

    /// <summary>What the assistant (and an MCP tool call) says when credits could not be checked at all.</summary>
    public const string AssistantGateUnavailableText =
        "I couldn't check your workspace's credits just now, so I didn't answer. Please try again in a moment.";

    /// <summary>What the assistant (and an MCP tool call) says when billing is on and no workspace is selected.</summary>
    public const string AssistantNoWorkspaceText =
        "Credits are counted per workspace, and no workspace is selected, so I didn't run this. Choose a workspace and try again.";

    /// <summary>What the assistant (and an MCP tool call) says when the workspace already has too many requests running.</summary>
    public const string AssistantTurnConcurrencyText =
        "Several assistant requests are already running in this workspace. Wait for one of them to finish, then try again.";

    /// <summary>What the assistant (and an MCP tool call) says when a turn reached the most one turn may use.</summary>
    public const string AssistantTurnLimitText =
        "I stopped here because this request reached the most one assistant request may use. "
        + "Nothing more was run. Ask again in smaller steps to keep going.";

    /// <summary>What the assistant (and an MCP tool call) says when the workspace ran out of credits mid-turn.</summary>
    public const string AssistantTurnOutOfCreditsText =
        "I stopped here because your workspace ran out of assistant credits. Nothing more was run. "
        + "Add credits on the Plan & billing page to keep going.";

    /// <summary>What the assistant (and an MCP tool call) says when a turn ran past its wall-clock ceiling.</summary>
    public const string AssistantTurnTooLongText =
        "This took too long, so I stopped it. Please try again, perhaps with a smaller request.";

    /// <summary>The plain words for a refused <see cref="TurnHold"/>.</summary>
    public static string AssistantRefusalText(TurnHold turn) => turn.Code switch
    {
        NonWorkflowChargeCodes.InsufficientCredits => AssistantOutOfCreditsText,
        NonWorkflowChargeCodes.NoOrganization => AssistantNoWorkspaceText,
        NonWorkflowChargeCodes.TurnConcurrencyLimit => AssistantTurnConcurrencyText,
        _ => AssistantGateUnavailableText
    };

    /// <summary>The plain words for a turn the in-turn cut-off stopped (<see cref="TurnCheck.Code"/>).</summary>
    public static string AssistantStopText(string? code) => code switch
    {
        NonWorkflowChargeCodes.TurnLimit => AssistantTurnLimitText,
        NonWorkflowChargeCodes.InsufficientCredits => AssistantTurnOutOfCreditsText,
        NonWorkflowChargeCodes.TurnConcurrencyLimit => AssistantTurnConcurrencyText,
        _ => AssistantGateUnavailableText
    };

    /// <summary>
    /// Closes an assistant/MCP turn at <paramref name="meteredMillis"/>. A settlement that fails is retried once
    /// (both attempts are idempotent and all-or-nothing), then the metered amount is recorded on the open hold for the
    /// stale sweep to charge, and logged at error with the organization and request id. Never throws and never uses the
    /// caller's token.
    /// </summary>
    public static async Task CloseTurnAsync(INonWorkflowChargeService? charges, TurnHold? turn, long meteredMillis, ILogger? logger)
    {
        if (charges is null || turn is not { Allowed: true }) return;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            TurnSettlement settled;
            try
            {
                settled = await charges.SettleTurnAsync(turn, meteredMillis, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Settling an assistant turn threw (attempt {Attempt})", attempt);
                settled = new TurnSettlement(false, 0, 0, 0, NonWorkflowChargeCodes.GateUnavailable);
            }

            if (settled.Ok) return;
        }

        // T-026 (finding 1): the usage is recorded on the still-open hold, so the stale sweep charges it (allowance first,
        // then credits) instead of releasing it; a later settle is charged only what the sweep did not.
        try
        {
            await charges.RecordUnsettledTurnAsync(turn, meteredMillis, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Recording an unsettled assistant turn threw");
        }

        logger?.LogError(
            "Charging the {Source} call of organization {OrganizationId} (request {RequestId}) for {MeteredMillis} millicredits failed twice; the stale sweep charges it from its hold",
            turn.Source, turn.OrganizationId, turn.RequestId, meteredMillis);
    }

    /// <summary>
    /// Closes a hold after the charged call: settled at <paramref name="meteredMillis"/> when the call completed
    /// (null = it failed), else released. Never uses the caller's token, so a cancelled request still frees its hold.
    /// T-110 (L1): a close that fails (false, or a non-cancellation exception) is retried once (the ledger is
    /// idempotent by key, so a retry never charges twice); a second failure records the metered usage on the still-open
    /// hold for the stale sweep to charge (a failed release records nothing, so the sweep releases it), and is logged
    /// at error and counted (<see cref="NonWorkflowChargeDiagnostics.HoldCloseFailed"/>).
    /// </summary>
    /// <param name="uncapped">
    /// T-013 (F1): settle with no ceiling (<see cref="INonWorkflowChargeService.SettleUncappedAsync"/>): only a
    /// size-priced picture job the vendor already answered.
    /// </param>
    public static async Task CloseHoldAsync(
        INonWorkflowChargeService? charges, ChargeHold? hold, long? meteredMillis, ILogger? logger = null, bool uncapped = false)
    {
        if (charges is null || hold is null) return;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            bool closed;
            try
            {
                closed = meteredMillis is { } millis
                    ? uncapped
                        ? await charges.SettleUncappedAsync(hold, millis, CancellationToken.None)
                        : await charges.SettleAsync(hold, millis, CancellationToken.None)
                    : await charges.ReleaseAsync(hold, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Closing the {Source} hold {ReservationId} threw (attempt {Attempt})", hold.Source, hold.ReservationId, attempt);
                closed = false;
            }

            if (closed) return;
        }

        if (meteredMillis is { } metered)
        {
            try
            {
                await charges.RecordUnsettledHoldAsync(hold, metered, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Recording an unsettled {Source} hold threw", hold.Source);
            }
        }

        logger?.LogError(
            "Closing the {Source} hold {ReservationId} of organization {OrganizationId} (request {RequestId}) at {MeteredMillis} millicredits failed twice; the stale sweep closes it",
            hold.Source, hold.ReservationId, hold.OrganizationId, hold.RequestId, meteredMillis);
        NonWorkflowChargeDiagnostics.HoldCloseFailed.Add(1, new KeyValuePair<string, object?>("source", hold.Source.ToString()));
    }

    /// <summary>
    /// What a turn settles at: what it metered, or, when its scope saw a Platform usage that could not be priced, at
    /// least <c>max(metered + the claims of the steps that metered unpriced usage, its hold)</c> (T-026, finding 3: an
    /// unpriced step counts at its claim, as it did towards the ceiling, never at the 0 it landed at).
    /// </summary>
    public static long TurnMeteredMillis(TurnHold? turn, long meteredMillis, bool sawUnpricedUsage, long unpricedStepClaimMillis = 0)
    {
        if (!sawUnpricedUsage) return meteredMillis;
        long withClaims = meteredMillis + Math.Max(0, unpricedStepClaimMillis);
        return turn is { HoldId: not null } ? Math.Max(withClaims, turn.RequiredMillis) : withClaims;
    }

    /// <summary>Millicredits to the whole-credit unit the billing endpoints report (like <c>WorkflowsController.ToCredits</c>).</summary>
    public static double ToCredits(long millis) => millis / 1000.0;

    /// <summary>
    /// The refusal of a hold: <c>402 {code:"INSUFFICIENT_CREDITS", message, required, balance}</c> (credits), or
    /// <c>503 {code:"GATE_UNAVAILABLE", message}</c> for every other refusal (fail closed).
    /// <paramref name="action"/> completes "This … needs about N credits", e.g. "picture edit".
    /// </summary>
    public static ObjectResult Refusal(ChargeHold hold, string action)
    {
        // Only a short balance is the caller's to fix; any other refusal (an unreadable ledger, a ledger error code)
        // is ours, and answered as unavailable rather than as a misleading "add credits".
        if (hold.Code != NonWorkflowChargeCodes.InsufficientCredits)
        {
            return new ObjectResult(new
            {
                code = NonWorkflowChargeCodes.GateUnavailable,
                message = $"Your workspace's credits could not be checked just now, so the {action} did not run. Please try again in a moment."
            })
            { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }

        double required = ToCredits(hold.RequiredMillis);
        double balance = ToCredits(hold.BalanceMillis);
        return new ObjectResult(new
        {
            code = NonWorkflowChargeCodes.InsufficientCredits,
            message = $"This {action} needs about {required:0.##} credits and the workspace has {Math.Max(0, balance):0.##} available. "
                      + "Add credits on the Plan & billing page to keep going.",
            required,
            balance
        })
        { StatusCode = StatusCodes.Status402PaymentRequired };
    }
}

/// <summary>
/// An assistant/MCP turn's hold for the length of the call: <see cref="SettleAsync"/> closes it at what the call
/// metered; disposing it before that (an exception, a refusal, an early return) releases it, so a hold can never be
/// left open by a call that did not get as far as settling.
/// </summary>
public sealed class TurnCharge(INonWorkflowChargeService? charges, TurnHold? turn, ILogger? logger) : IAsyncDisposable
{
    private bool _closed;

    /// <summary>The hold (null when nothing is charged on this host).</summary>
    public TurnHold? Turn => turn;

    /// <summary>Settles at <paramref name="meteredMillis"/> (once; later calls do nothing).</summary>
    public async Task SettleAsync(long meteredMillis)
    {
        if (_closed) return;
        _closed = true;
        await NonWorkflowChargeResults.CloseTurnAsync(charges, turn, meteredMillis, logger);
    }

    /// <summary>
    /// Settles at what <paramref name="scope"/> metered, or, when it saw a Platform usage that could not be priced, at
    /// least its unpriced steps' claims and the hold (F-3.2: unpriced is never free; T-026 finding 3).
    /// </summary>
    public Task SettleAsync(UsageScopeState? scope)
        => SettleAsync(NonWorkflowChargeResults.TurnMeteredMillis(
            turn, scope?.RunningCreditsMillis ?? 0, scope?.SawUnpricedPlatformUsage ?? false, scope?.UnpricedStepClaimMillis ?? 0));

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        if (charges is null || turn is not { Allowed: true, HoldId: not null }) return;
        try
        {
            if (!await charges.ReleaseTurnAsync(turn, CancellationToken.None))
            {
                logger?.LogError("Releasing the {Source} hold of organization {OrganizationId} (request {RequestId}) failed",
                    turn.Source, turn.OrganizationId, turn.RequestId);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Releasing the {Source} hold of organization {OrganizationId} (request {RequestId}) failed",
                turn.Source, turn.OrganizationId, turn.RequestId);
        }
    }
}
