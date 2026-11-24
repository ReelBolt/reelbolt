using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Observability;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// Thrown before a paid step of an assistant turn the in-turn cut-off stopped (<see cref="Code"/> is
/// <see cref="NonWorkflowChargeCodes.TurnLimit"/>, <see cref="NonWorkflowChargeCodes.InsufficientCredits"/>,
/// <see cref="NonWorkflowChargeCodes.TurnConcurrencyLimit"/> or <see cref="NonWorkflowChargeCodes.GateUnavailable"/>).
/// It is an <see cref="OperationCanceledException"/> so the tool-progress wrapper, which swallows every other failure of
/// its callback, lets it through and the tool never runs.
/// </summary>
public sealed class AssistantTurnStoppedException(string code)
    : OperationCanceledException($"The assistant turn was stopped ({code}).")
{
    public string Code { get; } = code;
}

/// <summary>
/// What bounds one turn's steps: the chat model a round-trip is priced on, the output tokens a
/// round-trip is claimed at (its output ceiling), the most input tokens one round-trip may send
/// (<c>Assistant:MaxRoundTripInputTokens</c>) and the most tool calls one model step may make
/// (<c>Assistant:MaxToolCallsPerStep</c>), and whether the turn's chat model is the platform's or the workspace's own
/// (a BYO turn's round-trips cost nothing; its first Platform-billed tool is admitted under the in-flight cap).
/// </summary>
public sealed record AssistantTurnBudget(
    ResolvedInferenceProvider? ChatProvider,
    int ClaimedOutputTokens,
    int MaxRoundTripInputTokens,
    int MaxToolCallsPerStep,
    UsageBillingMode TurnMode = UsageBillingMode.Platform)
{
    /// <summary>The budget of a chat turn on <paramref name="provider"/> under this deployment's configuration.</summary>
    public static AssistantTurnBudget For(IConfiguration? configuration, ResolvedInferenceProvider? provider) => new(
        provider,
        AssistantChatLimits.ClaimedOutputTokens(configuration, provider?.Kind),
        AssistantChatLimits.MaxRoundTripInputTokens(configuration),
        AssistantChatLimits.MaxToolCallsPerStep(configuration),
        provider is null ? UsageBillingMode.Platform : MeteringChatClient.BillingModeOf(provider.OrganizationId));
}

/// <summary>
/// The in-turn cut-off of one assistant chat turn or MCP tool call. Every paid
/// step is pre-claimed through <see cref="INonWorkflowChargeService.ClaimStepAsync"/> before it runs, at its worst case:
/// a model round-trip (<see cref="Wrap"/>) at the estimated input tokens of the exact request about to be sent (with a
/// safety factor) plus its output ceiling, priced on the chat model (the unpriced default when that model could not be
/// resolved, T-026); a tool call (<see cref="Gate"/>, <see cref="ClaimToolAsync"/>) by its entry in the one cost table
/// (<see cref="AssistantPlatformTools"/>): 0 when it is free; when it is Platform-billed, or (fail closed)
/// not classified at all, at its worst case for the actual input (<c>LookAtPicture</c> on its vision model with the
/// picture's image tokens, <c>LookAtOutput</c> on its vision model with the images it will send,
/// <c>ApplyRunPictureTool</c> at the larger of its top-tier quote and the unpriced default, any
/// other at the unpriced default). A
/// step that metered Platform usage the platform could not price counts at its claim from then on. A round-trip past <see cref="AssistantTurnBudget.MaxRoundTripInputTokens"/> is refused, and a model step's
/// tool calls past <see cref="AssistantTurnBudget.MaxToolCallsPerStep"/> are answered with a plain refusal. When a step's
/// usage lands its claim is replaced by the metered actual. Once stopped the turn stays stopped, without asking again.
/// </summary>
public sealed class AssistantTurnGuard
{
    /// <summary>Characters per token the input estimate assumes (English prose; code and JSON run denser).</summary>
    public const int CharsPerToken = 4;

    /// <summary>The safety factor on the character-based estimate (tokenizers vary; JSON and ids tokenize densely).</summary>
    public const decimal InputSafetyFactor = 1.5m;

    /// <summary>Tokens an image part is counted at (the providers downscale; ~1.6k tokens is the largest a vendor bills).</summary>
    public const int TokensPerImage = 1_600;

    /// <summary>Per-message framing overhead in tokens.</summary>
    public const int TokensPerMessage = 4;

    /// <summary>Pixels per image input token a look is claimed at (<see cref="ImageTokensFor"/>).</summary>
    public const int PixelsPerImageToken = 750;

    /// <summary>
    /// The fewest input tokens one picture is claimed at: above the largest per-image charge of the
    /// priced vision vendors (Gemini's 1,120 at high media resolution, OpenAI's patch models with their multipliers ~3,800).
    /// </summary>
    public const int MinTokensPerImage = 4_000;

    private const long MillisPerCredit = 1000;

    private readonly INonWorkflowChargeService? _charges;
    private readonly TurnHold? _turn;
    private readonly UsageScopeState? _scope;
    private readonly AssistantTurnBudget? _budget;
    // T-020: reporting only. The guard observes the tool results it already passes through and hands
    // nothing back, so a tool's own result reaches the model exactly as the tool produced it.
    private readonly IProductHealthSignalDetector _signals;
    private string? _stopped;
    private long _claimed;
    private int _toolCallsThisStep;
    private int _capAdmitted;

    /// <param name="charges">The charge service (null when nothing is charged on this host).</param>
    /// <param name="turn">The turn's hold.</param>
    /// <param name="scope">The turn's usage scope: what it has metered so far.</param>
    /// <param name="budget">What bounds its steps; null = no round-trip pricing and no per-step tool cap (the MCP bridge).</param>
    public AssistantTurnGuard(
        INonWorkflowChargeService? charges,
        TurnHold? turn,
        UsageScopeState? scope,
        AssistantTurnBudget? budget = null,
        IProductHealthSignalDetector? signals = null)
    {
        _charges = charges;
        _turn = turn;
        _scope = scope;
        _budget = budget;
        _signals = signals ?? ProductHealthSignals.Ambient;
    }

    /// <summary>
    /// Reports a tool result the guard already passes through: a <c>{"ok": false}</c> payload and
    /// each workflow step a run report says failed. Deterministic and metadata-only; the result is returned
    /// by the caller unchanged, so this can never alter what the model reads.
    /// </summary>
    private void ObserveToolResult(string toolName, object? result) =>
        _signals.ObserveToolResult(new ToolCallObservation(
            "assistant.tool_result",
            _scope?.OrganizationId ?? Guid.Empty,
            _scope?.UserId ?? Guid.Empty,
            _scope?.ProjectId,
            _scope?.ExecutionId,
            toolName,
            ResultText(result)));

    /// <summary>A tool result as the JSON text the detector reads, however the invoker handed it back.</summary>
    private static string? ResultText(object? result) => result switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(result)
    };

    /// <summary>
    /// Resolves the vision model <c>LookAtPicture</c> will call (null = none set up), so a look is claimed at that model's
    /// quote; unset (or failing) means the look is claimed at the unpriced default.
    /// </summary>
    public Func<CancellationToken, ValueTask<ResolvedInferenceProvider?>>? ResolveVision { get; init; }

    /// <summary>The <see cref="ResolveVision"/> of the vision model <c>LookAtPicture</c> resolves (its default row); null without a resolver.</summary>
    public static Func<CancellationToken, ValueTask<ResolvedInferenceProvider?>>? VisionResolverFrom(IInferenceProviderResolver? resolver)
        => resolver is null ? null : async ct => await resolver.ResolveVisionAsync(null, ct);

    /// <summary>
    /// Sizes the picture a <c>LookAtPicture</c> call (its arguments) would send: the layer it resolves
    /// and its pixels. Null when it cannot be resolved (unset, unauthorized, or the sizer failed), or null pixels when it
    /// cannot be read, means the look is claimed at the pixel cap. A resolved layer is pinned into the call's arguments.
    /// </summary>
    public Func<IDictionary<string, object?>?, CancellationToken, ValueTask<LookSizing?>>? ResolveLookPixels { get; init; }

    /// <summary>
    /// Sizes what a <c>LookAtOutput</c> call (its arguments) would send: its image count, pixels per image and
    /// text bound. Null (unset, unresolvable, or failing) means the look is claimed at its worst case.
    /// </summary>
    public Func<IDictionary<string, object?>?, CancellationToken, ValueTask<LookOutputSizing?>>? ResolveLookOutputSizing { get; init; }

    /// <summary>
    /// The worst-case price requests of an <c>ApplyRunPictureTool</c> call (its arguments): one per tier in force of the
    /// model it resolves plus the variant-less one, for the most images the operation returns; the guard claims the dearest estimate. Null, empty (or failing) means the default.
    /// </summary>
    public Func<IDictionary<string, object?>?, CancellationToken, ValueTask<IReadOnlyList<PriceRequest>?>>? ResolvePictureToolWorstCase { get; init; }

    /// <summary>
    /// The guard of one chat turn or MCP call with every hook bound to this request's services and the per-call tool
    /// context the tools run with (the vision resolver, and the picture tools' look sizer and picture-tool worst case),
    /// so the chat turn and the MCP bridge size a call the same way.
    /// </summary>
    public static AssistantTurnGuard ForCall(
        INonWorkflowChargeService? charges, TurnHold? turn, UsageScopeState? scope, AssistantTurnBudget? budget,
        IServiceProvider services, AssistantToolContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        AssistantPictureTools? pictures = services.GetService<AssistantPictureTools>();
        AssistantOutputTools? outputs = services.GetService<AssistantOutputTools>();
        return new AssistantTurnGuard(charges, turn, scope, budget)
        {
            ResolveVision = VisionResolverFrom(services.GetService<IInferenceProviderResolver>()),
            ResolveLookPixels = pictures is null
                ? null
                : (arguments, ct) => new ValueTask<LookSizing?>(pictures.SizeLookAsync(arguments, context, ct)),
            ResolvePictureToolWorstCase = pictures is null
                ? null
                : (arguments, ct) => new ValueTask<IReadOnlyList<PriceRequest>?>(pictures.RunPictureToolWorstCaseAsync(arguments, context, ct)),
            ResolveLookOutputSizing = outputs is null
                ? null
                : (arguments, ct) => new ValueTask<LookOutputSizing?>(outputs.SizeLookOutputAsync(arguments, context, ct))
        };
    }

    /// <summary>
    /// The input tokens one picture of <paramref name="pixels"/> pixels is claimed at: one per
    /// <see cref="PixelsPerImageToken"/> pixels (Anthropic's rule, the densest of the vendors priced), at least
    /// <see cref="MinTokensPerImage"/>, at most what the look's pixel cap allows; an unknown size claims the cap.
    /// </summary>
    public static long ImageTokensFor(long? pixels)
    {
        long clamped = Math.Clamp(pixels ?? AssistantPictureTools.MaxLookPixels, 1, AssistantPictureTools.MaxLookPixels);
        return Math.Max(MinTokensPerImage, (clamped + PixelsPerImageToken - 1) / PixelsPerImageToken);
    }

    /// <summary>Why the turn was stopped, or null while it may go on.</summary>
    public string? StoppedCode => Volatile.Read(ref _stopped);

    private bool Charged => _charges is not null && _turn is { Allowed: true };

    /// <summary>
    /// What the turn has metered so far: its priced usage plus, at their claims, the steps that metered Platform usage
    /// the platform could not price (T-026, finding 3: such a step is never 0 towards the ceiling or the funds check).
    /// </summary>
    private long Metered => (_scope?.RunningCreditsMillis ?? 0) + (_scope?.UnpricedStepClaimMillis ?? 0);

    /// <summary>The unpriced default (<c>Billing:NonWorkflow:UnpricedEstimateCredits</c>) in millicredits, never below one credit.</summary>
    private long UnpricedDefaultMillis => Math.Max(1, _charges?.Options.NonWorkflow.UnpricedEstimateCredits ?? 1) * MillisPerCredit;

    /// <summary>Throws <see cref="AssistantTurnStoppedException"/> when the turn may not take its next paid step.</summary>
    public async Task ThrowIfStoppedAsync(CancellationToken ct)
    {
        await using StepClaim _ = await ClaimAsync(0, admitUnderCap: false, ct);
    }

    /// <summary>
    /// Pre-claims the tool call <paramref name="toolName"/> (the MCP bridge's one call, or one call of a chat turn's tool
    /// loop), classified by <see cref="AssistantPlatformTools"/>: a free tool claims nothing; a
    /// Platform-billed tool, and (fail closed) any tool the table does not classify, claims its worst case:
    /// <c>LookAtPicture</c> its vision quote for the actual picture (<see cref="QuoteLookAsync"/>),
    /// <c>ApplyRunPictureTool</c> the larger of the unpriced default and its top-tier quote, any other the unpriced
    /// default. A BYO-chat turn's first such tool is admitted under the in-flight cap. Nothing is quoted (no hook runs)
    /// when nothing is charged or billing is off. Throws <see cref="AssistantTurnStoppedException"/> when refused.
    /// Dispose the claim once the tool returned.
    /// </summary>
    public async Task<StepClaim> ClaimToolAsync(string toolName, CancellationToken ct, IDictionary<string, object?>? arguments = null)
    {
        bool platform = AssistantPlatformTools.IsPlatformBilled(toolName);
        long step = 0;
        if (platform && Charged && _charges!.Options.Enabled)
        {
            step = toolName switch
            {
                // T-026 (finding 8), T-093: a look is claimed at its vision model's quote for the picture it will send;
                // the default only when it cannot be quoted.
                "LookAtPicture" => await QuoteLookAsync(arguments, ct) ?? UnpricedDefaultMillis,
                // C1 / S-76: a look at an output is claimed at its vision model's quote for the images it will send.
                "LookAtOutput" => await QuoteLookOutputAsync(arguments, ct) ?? UnpricedDefaultMillis,
                "ApplyRunPictureTool" => await QuotePictureToolAsync(arguments, ct),
                _ => UnpricedDefaultMillis
            };
        }

        bool admit = platform && _budget is { TurnMode: not UsageBillingMode.Platform }
                     && Interlocked.CompareExchange(ref _capAdmitted, 1, 0) == 0;
        return await ClaimAsync(step, admit, ct);
    }

    /// <summary>
    /// The worst case of one <c>ApplyRunPictureTool</c> call: the larger of the unpriced default and the dearest quote of
    /// <see cref="ResolvePictureToolWorstCase"/>'s requests (one per tier in force, plus the variant-less one; a top fal
    /// tier can cost more than the default); the default when there is no request or building or pricing one failed.
    /// </summary>
    private async Task<long> QuotePictureToolAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        if (ResolvePictureToolWorstCase is null) return UnpricedDefaultMillis;
        try
        {
            if (await ResolvePictureToolWorstCase(arguments, ct) is not { Count: > 0 } requests) return UnpricedDefaultMillis;

            // T-095: the price service picks the row of each request itself, so the dearest estimate over
            // every tier in force (and the variant-less request) is the worst case whichever row the call bills.
            long dearest = UnpricedDefaultMillis;
            foreach (PriceRequest request in requests)
            {
                dearest = Math.Max(dearest, await _charges!.EstimateAsync(request, ct));
            }

            return dearest;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return UnpricedDefaultMillis;
        }
    }

    /// <summary>
    /// The worst case of one <c>LookAtPicture</c> call on the vision model <see cref="ResolveVision"/> resolves: the
    /// question plus the picture's <see cref="ImageTokensFor"/> tokens (its size from <see cref="ResolveLookPixels"/>;
    /// the pixel cap when it cannot be sized) as input, plus the look's output ceiling, priced on that model (0 when the
    /// workspace pays for it itself). Null when it cannot be quoted (no resolver, no vision model, or the resolution failed).
    /// </summary>
    private async Task<long?> QuoteLookAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        if (ResolveVision is null || _charges is null) return null;

        ResolvedInferenceProvider? vision;
        try
        {
            vision = await ResolveVision(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        if (vision is null) return null;

        UsageBillingMode mode = MeteringChatClient.BillingModeOf(vision.OrganizationId);
        string question = arguments is not null && arguments.TryGetValue("question", out object? asked) && asked?.ToString() is { Length: > 0 } text
            ? text
            : AssistantPictureTools.DefaultLookQuestion;
        LookSizing? sizing;
        try
        {
            sizing = ResolveLookPixels is null ? null : await ResolveLookPixels(arguments, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            sizing = null;
        }

        // T-095: the look is pinned to the layer it was sized at, in the very arguments the tool is invoked
        // with, so a layer added meanwhile (an omitted layerId resolves the backmost) is not sent instead.
        if (sizing is not null && arguments is { IsReadOnly: false })
        {
            arguments["layerId"] = sizing.LayerId.ToString();
        }

        long? pixels = sizing?.Pixels;

        long inputTokens = EstimateInputTokens([new ChatMessage(ChatRole.User, [new TextContent(question)])], null)
                           + ImageTokensFor(pixels);

        PriceRequest Request(UsageKind unit, decimal quantity) => new(
            vision.ProviderId, vision.AccountId, vision.Kind, vision.Capability, vision.ModelName, unit, null, mode, quantity,
            PromptTokens: inputTokens);

        long input = await _charges.EstimateAsync(Request(UsageKind.LlmInputTokens, inputTokens), ct);
        long output = await _charges.EstimateAsync(Request(UsageKind.LlmOutputTokens, AssistantPictureTools.LookOutputTokens), ct);
        return input + output;
    }

    /// <summary>
    /// The worst case of one <c>LookAtOutput</c> call on the vision model <see cref="ResolveVision"/> resolves:
    /// its text part plus <see cref="ImageTokensFor"/> of each image it will send (<see cref="ResolveLookOutputSizing"/>),
    /// plus the look's output ceiling. Unsized, the worst of the two shapes a look can take: one picture at the look's
    /// pixel cap, or <see cref="AssistantOutputTools.MaxLookImages"/> stills at their largest, with the longest text.
    /// Null when it cannot be quoted (no resolver, no vision model, or the resolution failed).
    /// </summary>
    private async Task<long?> QuoteLookOutputAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        if (ResolveVision is null || _charges is null) return null;

        ResolvedInferenceProvider? vision;
        try
        {
            vision = await ResolveVision(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        if (vision is null) return null;

        LookOutputSizing? sizing;
        try
        {
            sizing = ResolveLookOutputSizing is null ? null : await ResolveLookOutputSizing(arguments, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            sizing = null;
        }

        long imageTokens = sizing is { Images: > 0 } sized
            ? sized.Images * ImageTokensFor(sized.PixelsPerImage)
            : Math.Max(
                ImageTokensFor(AssistantPictureTools.MaxLookPixels),
                AssistantOutputTools.MaxLookImages * ImageTokensFor(AssistantOutputTools.MaxStillPixels));
        int promptChars = sizing is { Images: > 0 } ? sizing.PromptChars : AssistantOutputTools.WorstLookPromptChars;
        long inputTokens = EstimateInputTokens([new ChatMessage(ChatRole.User, [new TextContent(new string('x', promptChars))])], null)
                           + imageTokens;

        UsageBillingMode mode = MeteringChatClient.BillingModeOf(vision.OrganizationId);
        PriceRequest Request(UsageKind unit, decimal quantity) => new(
            vision.ProviderId, vision.AccountId, vision.Kind, vision.Capability, vision.ModelName, unit, null, mode, quantity,
            PromptTokens: inputTokens);

        long input = await _charges.EstimateAsync(Request(UsageKind.LlmInputTokens, inputTokens), ct);
        long output = await _charges.EstimateAsync(Request(UsageKind.LlmOutputTokens, AssistantPictureTools.LookOutputTokens), ct);
        return input + output;
    }

    /// <summary>
    /// A tool whose call is pre-claimed (<see cref="ClaimToolAsync"/>) before it runs, and refused in plain words past
    /// the step's <see cref="AssistantTurnBudget.MaxToolCallsPerStep"/>.
    /// </summary>
    public AIFunction Gate(AIFunction tool) => new GatedFunction(tool, this);

    /// <summary>The refusal a tool call past the per-step maximum gets (the refusal envelope the model reads).</summary>
    public static string TooManyToolCallsRefusal(int max) => JsonSerializer.Serialize(new
    {
        error = $"This step asked for more than {max} tool calls at once, so this one was not run. "
                + $"Ask for at most {max} at a time, then continue."
    });

    /// <summary>A provider whose chat clients pre-claim every round-trip.</summary>
    public IAgentChatClientProvider Wrap(IAgentChatClientProvider inner, int? maxToolIterations = null) =>
        new GuardedProvider(inner, this, maxToolIterations);

    /// <summary>
    /// The input tokens of a request about to be sent, estimated from its characters (<see cref="CharsPerToken"/>, times
    /// <see cref="InputSafetyFactor"/>): its instructions, every message part (text, reasoning, tool calls and results)
    /// and every tool declaration, plus <see cref="TokensPerImage"/> per image and <see cref="TokensPerMessage"/> per
    /// message.
    /// </summary>
    public static long EstimateInputTokens(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        long chars = options?.Instructions?.Length ?? 0;
        long fixedTokens = 0;
        foreach (ChatMessage message in messages)
        {
            fixedTokens += TokensPerMessage;
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        chars += text.Text?.Length ?? 0;
                        break;
                    case TextReasoningContent reasoning:
                        chars += reasoning.Text?.Length ?? 0;
                        break;
                    case FunctionCallContent call:
                        chars += call.Name.Length + Serialized(call.Arguments);
                        break;
                    case FunctionResultContent result:
                        chars += Serialized(result.Result);
                        break;
                    case DataContent data when data.HasTopLevelMediaType("image"):
                    case UriContent:
                        fixedTokens += TokensPerImage;
                        break;
                    case DataContent data:
                        chars += data.Data.Length;
                        break;
                }
            }
        }

        foreach (AITool tool in options?.Tools ?? [])
        {
            chars += tool.Name.Length + (tool.Description?.Length ?? 0);
            if (tool is AIFunction function) chars += function.JsonSchema.GetRawText().Length;
        }

        decimal estimated = Math.Ceiling(chars * InputSafetyFactor / CharsPerToken);
        return (long)Math.Min(estimated, long.MaxValue / 2) + fixedTokens;
    }

    private static long Serialized(object? value)
    {
        if (value is null) return 0;
        if (value is string s) return s.Length;
        try
        {
            return JsonSerializer.Serialize(value).Length;
        }
        catch (Exception)
        {
            return value.ToString()?.Length ?? 0;
        }
    }

    /// <summary>Pre-claims one model round-trip about to send <paramref name="messages"/>.</summary>
    private async Task<StepClaim> ClaimRoundTripAsync(IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        // A new model step: the tool calls that follow it are counted afresh.
        Interlocked.Exchange(ref _toolCallsThisStep, 0);
        if (StoppedCode is { } stopped) throw new AssistantTurnStoppedException(stopped);

        // A BYO (or Runner) chat model costs no credits: nothing to claim and no input cap; the turn is still checked.
        if (!Charged || !_charges!.Options.Enabled || _budget is not { TurnMode: UsageBillingMode.Platform } b)
        {
            return await ClaimAsync(0, admitUnderCap: false, ct);
        }

        long inputTokens = EstimateInputTokens(messages, options);
        if (inputTokens > b.MaxRoundTripInputTokens)
        {
            Stop(NonWorkflowChargeCodes.TurnLimit);
        }

        // T-026 (finding 2): a Platform turn whose chat provider could not be resolved cannot be priced, but it is not
        // free either: each round-trip is claimed at the unpriced default (the one the estimate holds), capped as above.
        if (b.ChatProvider is not { } provider)
        {
            return await ClaimAsync(UnpricedDefaultMillis, admitUnderCap: false, ct);
        }

        // The prompt length is known here, so a long round-trip is claimed at its gt{N}k tier like it will be billed.
        PriceRequest Request(UsageKind unit, decimal quantity) => new(
            provider.ProviderId, provider.AccountId, provider.Kind, provider.Capability, provider.ModelName, unit, null,
            UsageBillingMode.Platform, quantity, PromptTokens: inputTokens);

        long input = await _charges.EstimateAsync(Request(UsageKind.LlmInputTokens, inputTokens), ct);
        long output = await _charges.EstimateAsync(Request(UsageKind.LlmOutputTokens, Math.Max(1, b.ClaimedOutputTokens)), ct);
        return await ClaimAsync(input + output, admitUnderCap: false, ct);
    }

    private async Task<StepClaim> ClaimAsync(long stepMillis, bool admitUnderCap, CancellationToken ct)
    {
        if (StoppedCode is { } stopped) throw new AssistantTurnStoppedException(stopped);
        if (!Charged) return StepClaim.None;

        int unpricedBefore = _scope?.UnpricedPlatformUsageCount ?? 0;
        long step = Math.Max(0, stepMillis);
        long others = Interlocked.Add(ref _claimed, step) - step;
        long metered = Metered;
        TurnCheck check;
        try
        {
            check = await _charges!.ClaimStepAsync(_turn!, new TurnStepClaim(metered, others, step, admitUnderCap), ct);
        }
        catch
        {
            Interlocked.Add(ref _claimed, -step);
            throw;
        }

        if (!check.Continue)
        {
            Interlocked.Add(ref _claimed, -step);
            Stop(check.Code ?? NonWorkflowChargeCodes.TurnLimit);
        }

        // The service records the exposure on the turn's hold once it is beyond the hold (or the step asked for the cap):
        // only then does the landed actual need writing back.
        bool recorded = admitUnderCap || metered + others + step >= _turn!.RequiredMillis;
        return new StepClaim(this, step, step > 0 && recorded, unpricedBefore);
    }

    private void Stop(string code)
    {
        Interlocked.CompareExchange(ref _stopped, code, null);
        throw new AssistantTurnStoppedException(StoppedCode!);
    }

    /// <summary>One pre-claimed step; disposing it once the step's usage landed replaces the claim by the actual.</summary>
    public sealed class StepClaim : IAsyncDisposable
    {
        internal static readonly StepClaim None = new(null, 0, false, 0);

        private readonly AssistantTurnGuard? _guard;
        private readonly long _millis;
        private readonly bool _land;
        private readonly int _unpricedBefore;
        private int _disposed;

        internal StepClaim(AssistantTurnGuard? guard, long millis, bool land, int unpricedBefore)
        {
            _guard = guard;
            _millis = millis;
            _land = land;
            _unpricedBefore = unpricedBefore;
        }

        /// <summary>What this step claimed, in millicredits.</summary>
        public long Millis => _millis;

        public async ValueTask DisposeAsync()
        {
            if (_guard is null || Interlocked.Exchange(ref _disposed, 1) != 0) return;
            long stillClaimed = Interlocked.Add(ref _guard._claimed, -_millis);

            // T-026 (finding 3): the step metered Platform usage that could not be priced (it landed at 0): it counts at
            // its claim (the unpriced default when it claimed nothing) from now on, and in the settlement.
            if (_guard._scope is { } scope && scope.UnpricedPlatformUsageCount > _unpricedBefore)
            {
                scope.AddUnpricedStepClaim(_millis > 0 ? _millis : _guard.UnpricedDefaultMillis);
            }

            if (!_land) return;
            try
            {
                await _guard._charges!.LandStepAsync(_guard._turn!, _guard.Metered, Math.Max(0, stillClaimed), CancellationToken.None);
            }
            catch (Exception)
            {
                // Best effort: the next claim or the settlement records the turn's real total.
            }
        }
    }

    private sealed class GatedFunction(AIFunction inner, AssistantTurnGuard guard) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            int max = guard._budget?.MaxToolCallsPerStep ?? int.MaxValue;
            if (Interlocked.Increment(ref guard._toolCallsThisStep) > max)
            {
                return TooManyToolCallsRefusal(max);
            }

            await using StepClaim claim = await guard.ClaimToolAsync(Name, cancellationToken, arguments);
            object? result = await base.InvokeCoreAsync(arguments, cancellationToken);
            // T-020: reporting only — the tool's own result is observed and returned unchanged.
            guard.ObserveToolResult(Name, result);
            return result;
        }
    }

    private sealed class GuardedProvider(IAgentChatClientProvider inner, AssistantTurnGuard guard, int? maxToolIterations) : IAgentChatClientProvider
    {
        public async ValueTask<IChatClient> GetAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
        {
            IChatClient provider = await inner.GetAsync(agentType, agentDefinitionId, ct);
            IChatClient guarded = new GuardedChatClient(provider, guard);

            // The turn's tool loop, with its iteration cap set here: ChatClientAgent adds its own FunctionInvokingChatClient
            // (library default 40) only when none is found in the chain, so providing one is how the cap reaches the loop.
            // A chain that already carries a loop is someone else's and is left alone (no second loop stacked on it).
            if (maxToolIterations is { } max && provider.GetService<FunctionInvokingChatClient>() is null)
                return new FunctionInvokingChatClient(guarded) { MaximumIterationsPerRequest = Math.Max(1, max) };

            return guarded;
        }

        public ValueTask<ResolvedInferenceProvider?> ResolveProviderAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
            => inner.ResolveProviderAsync(agentType, agentDefinitionId, ct);

        public ValueTask<string?> ResolveReasoningEffortAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
            => inner.ResolveReasoningEffortAsync(agentType, agentDefinitionId, ct);
    }

    /// <summary>
    /// Sits under the agent framework's function-invoking client (the provider's clients carry none of their own), so
    /// every round-trip of the tool loop passes through it; the metering client sits under it, so a round-trip's usage
    /// has landed in the turn's scope by the time its claim is disposed.
    /// </summary>
    private sealed class GuardedChatClient(IChatClient inner, AssistantTurnGuard guard) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ChatMessage> request = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
            await using StepClaim claim = await guard.ClaimRoundTripAsync(request, options, cancellationToken);
            return await base.GetResponseAsync(request, options, cancellationToken);
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ChatMessage> request = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
            await using StepClaim claim = await guard.ClaimRoundTripAsync(request, options, cancellationToken);
            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(request, options, cancellationToken))
                yield return update;
        }
    }
}
