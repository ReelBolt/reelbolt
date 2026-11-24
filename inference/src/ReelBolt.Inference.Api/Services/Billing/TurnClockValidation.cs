using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Assistant;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// the turn wall clock (<c>Assistant:MaxTurnSeconds</c>) must be at most half of
/// <see cref="NonWorkflowChargeService.StaleTurnHoldAge"/>, or the stale sweep could release a turn that is still
/// running. Registered with <c>ValidateOnStart</c> on <see cref="NonWorkflowChargeOptions"/>, so a bad value fails the
/// boot rather than the first assistant turn (where <see cref="NonWorkflowChargeService"/>'s constructor checks it too).
/// </summary>
internal sealed class TurnClockValidation(IConfiguration configuration) : IValidateOptions<NonWorkflowChargeOptions>
{
    public ValidateOptionsResult Validate(string? name, NonWorkflowChargeOptions options) =>
        NonWorkflowChargeService.TurnClockProblem(TimeSpan.FromSeconds(AssistantChatLimits.MaxTurnSeconds(configuration))) is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
}

/// <summary>
/// with <c>Billing:Enabled=true</c> the non-workflow charge service must be registered. The four
/// controllers that take it optionally (assistant chat, picture operations, picture documents, timelines) would
/// otherwise skip charging silently, so its absence fails the boot (<c>ValidateOnStart</c> on
/// <see cref="NonWorkflowChargeOptions"/>). With billing off every charge path is a no-op and nothing is required.
/// </summary>
internal sealed class ChargeServiceRequiredValidation(IServiceProviderIsService services) : IValidateOptions<NonWorkflowChargeOptions>
{
    public ValidateOptionsResult Validate(string? name, NonWorkflowChargeOptions options) =>
        options.Enabled && !(services.IsService(typeof(INonWorkflowChargeService)) && services.IsService(typeof(IAssistantAllowanceService)))
            ? ValidateOptionsResult.Fail(
                $"Billing:Enabled is true but {nameof(INonWorkflowChargeService)} (or {nameof(IAssistantAllowanceService)}) is not registered: "
                + "assistant, MCP, picture and timeline calls would run uncharged.")
            : ValidateOptionsResult.Success;
}

/// <summary>
/// The per-turn money bounds must nest: the up-front hold (<c>AssistantTurnHoldCredits</c>) is at most the per-turn ceiling
/// (<c>AssistantTurnCeilingCredits</c>), and the least a turn must be able to hold (<c>AssistantMinTurnCredits</c>) is at
/// most the hold. A turn can meter past its hold, up to the ceiling, when the funds exist (<c>ClaimStepAsync</c>); a hold
/// above the ceiling, or a minimum above the hold, would contradict that. Fails the boot.
/// </summary>
internal sealed class TurnBudgetValidation : IValidateOptions<NonWorkflowChargeOptions>
{
    public ValidateOptionsResult Validate(string? name, NonWorkflowChargeOptions options) => Problem(options) is { } problem
        ? ValidateOptionsResult.Fail(problem)
        : ValidateOptionsResult.Success;

    internal static string? Problem(NonWorkflowChargeOptions options)
    {
        NonWorkflowHoldOptions nw = options.NonWorkflow;
        if (nw.AssistantTurnHoldCredits > nw.AssistantTurnCeilingCredits)
            return $"Billing:NonWorkflow:AssistantTurnHoldCredits ({nw.AssistantTurnHoldCredits}) must not exceed AssistantTurnCeilingCredits ({nw.AssistantTurnCeilingCredits}).";
        if (nw.AssistantMinTurnCredits is { } min && min > nw.AssistantTurnHoldCredits)
            return $"Billing:NonWorkflow:AssistantMinTurnCredits ({min}) must not exceed AssistantTurnHoldCredits ({nw.AssistantTurnHoldCredits}).";
        return null;
    }
}
